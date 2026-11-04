using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Routing;
using pharmacyApp.AI.Search;
using pharmacyApp.AI.Tools;

namespace pharmacyApp.AI.Runtime;

internal sealed record AiContinuationState(
    AiRequestContext Context,
    AiRouteDecision Route,
    AiModelSelection Selection,
    List<JsonElement> Input,
    List<AiToolDefinition> Tools,
    AiDebugTrace Trace,
    Dictionary<string, int> RepeatedCalls,
    int ToolTurns,
    int TotalToolCalls,
    AiUsage Usage,
    bool BroadFallbackUsed);

public sealed class AiOrchestrator(
    IAiRouter router,
    IAiToolSearchService search,
    IAiToolRegistry registry,
    IAiModelSelector modelSelector,
    IAiModelTransport transport,
    AiToolExecutionGateway gateway,
    IAiConversationManager conversations,
    IAiConfigurationProvider configurationProvider,
    IAiPromptProvider prompts,
    AiRouteApprovalService routeApprovals,
    MissingToolStore missingTools,
    AiUsageTelemetry telemetry,
    AiOperationalLog operationalLog)
{
    public async Task<AiAssistantResult> RunAsync(AiRequestContext context, CancellationToken cancellationToken = default)
    {
        var configuration = configurationProvider.GetCurrent();
        if (!configuration.Enabled) return AiAssistantResult.Error("Yapay zekâ asistanı şu anda devre dışı.");
        if (!transport.IsConfigured) return AiAssistantResult.Error(
            "AI bağlantısı yapılandırılmamış. Üretimde uygulama AI ağ geçidini, geliştirmede OPENAI_API_KEY ortam değişkenini yapılandırın.");

        var trace = AiDebugTrace.Start();
        Report(context, "İstek analiz ediliyor…", $"Request {trace.RequestId} started.");
        AiRouteDecision route;
        try
        {
            route = await router.RouteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AiAssistantResult(AiAssistantStatus.Cancelled, "İstek iptal edildi.");
        }
        catch (AiTransportException exception)
        {
            trace.Add($"Router transport error: {FormatTransportError(exception)}");
            Report(context, "AI bağlantısı kontrol ediliyor…", $"Router transport error: {FormatTransportError(exception)}");
            operationalLog.WriteRequest(trace.RequestId, configuration.RouterModel, exception.Code, 0);
            return AiAssistantResult.Error(ToUserMessage(exception), configuration.DebugMode ? trace.RequestId : null) with
            {
                DebugSummary = configuration.DebugMode ? trace.ToSummary() : null,
                ModelUsed = configuration.RouterModel
            };
        }
        catch (Exception)
        {
            trace.Add("Router error: UNEXPECTED_ROUTER_FAILURE");
            operationalLog.WriteRequest(trace.RequestId, configuration.RouterModel, "UNEXPECTED_ROUTER_FAILURE", 0);
            return AiAssistantResult.Error(
                "AI isteği yönlendirilirken beklenmeyen bir hata oluştu. Uygulama çalışmaya devam edebilir.",
                configuration.DebugMode ? trace.RequestId : null);
        }
        var routingDetail = $"Routing: router={(route.UsedModelRouter ? configuration.RouterModel : "local")}, domains=[{string.Join(", ", route.Domains)}], preferred_tools=[{string.Join(", ", route.PreferredToolNames ?? [])}], confidence={route.Confidence:0.00}, model_tier={route.ModelTier}, complexity={route.Complexity}, clarification={route.NeedsClarification}, expensive={route.IsExpensive}, explosive={route.IsExplosive}";
        trace.Add(routingDetail);
        Report(context, route.NeedsTools ? "Uygun araçlar aranıyor…" : "Yanıt hazırlanıyor…", routingDetail,
            route.UsedModelRouter ? configuration.RouterModel : null);
        if (route.NeedsClarification &&
            !string.IsNullOrWhiteSpace(route.ClarificationQuestion) &&
            route.ClarificationOptions is { Count: >= 2 })
        {
            var question = route.ClarificationQuestion.Trim();
            var actions = route.ClarificationOptions
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select((option, index) => new AiSuggestedAction($"route_choice_{index + 1}", option.Trim()))
                .ToArray();
            if (actions.Length >= 2)
            {
                conversations.Add(context.SessionId, "user", context.UserMessage);
                conversations.Add(context.SessionId, "assistant", question);
                trace.Add($"Router clarification requested with {actions.Length} text option(s); main model skipped.");
                Report(context, "Kapsam seçimi bekleniyor…",
                    $"Router clarification returned {actions.Length} option(s) without invoking the main model.",
                    route.UsedModelRouter ? configuration.RouterModel : null);
                return new AiAssistantResult(
                    AiAssistantStatus.ClarificationRequired,
                    question,
                    SuggestedActions: actions,
                    DebugTraceId: configuration.DebugMode ? trace.RequestId : null,
                    DebugSummary: configuration.DebugMode ? trace.ToSummary() : null,
                    ModelUsed: route.UsedModelRouter ? configuration.RouterModel : "Yerel yönlendirici",
                    UsageByModel: UsageBreakdown(route, configuration.RouterModel));
            }
        }
        if (route.RequiresApproval)
        {
            trace.Add($"Router preflight paused: expensive={route.IsExpensive}, explosive={route.IsExplosive}, expense_reason={route.ExpenseReason ?? "none"}, explosion_reason={route.ExplosionReason ?? "none"}");
            var approval = routeApprovals.Create(
                context, route, trace, configuration.ConfirmationLifetimeMinutes);
            Report(context, "Kullanıcı onayı bekleniyor…", "Router preflight requires explicit yes/no approval.", configuration.RouterModel);
            return new AiAssistantResult(
                AiAssistantStatus.ConfirmationRequired,
                approval.Summary,
                DebugTraceId: configuration.DebugMode ? trace.RequestId : null,
                DebugSummary: configuration.DebugMode ? trace.ToSummary() : null,
                PendingRouteApproval: approval,
                ModelUsed: configuration.RouterModel,
                UsageByModel: UsageBreakdown(route, configuration.RouterModel));
        }
        return await StartRoutedAsync(context, route, trace, cancellationToken);
    }

    internal Task<AiAssistantResult> ResumeApprovedRouteAsync(
        AiRequestContext context,
        AiRouteDecision route,
        AiDebugTrace trace,
        CancellationToken cancellationToken)
    {
        trace.Add("Router preflight approved by the user.");
        return StartRoutedAsync(context, route, trace, cancellationToken);
    }

    private async Task<AiAssistantResult> StartRoutedAsync(
        AiRequestContext context,
        AiRouteDecision route,
        AiDebugTrace trace,
        CancellationToken cancellationToken)
    {
        var configuration = configurationProvider.GetCurrent();
        var hits = route.NeedsTools && configuration.EnableToolSearch
            ? search.Search(context.UserMessage, route.Domains, configuration.MaxToolsPerRequest)
            : [];
        IReadOnlyList<string> preferredToolNames = route.NeedsTools ? route.PreferredToolNames ?? [] : [];
        var tools = preferredToolNames
            .Select(name => registry.GetTool(name)?.Definition)
            .Where(tool => tool is not null)
            .Cast<AiToolDefinition>()
            .Concat(hits.Select(hit => hit.Tool))
            .DistinctBy(tool => tool.Name, StringComparer.Ordinal)
            .Take(configuration.MaxToolsPerRequest)
            .ToList();
        trace.CandidateTools.AddRange(tools.Select(tool => tool.Name));
        var shortlistDetail = $"Tool shortlist: {tools.Count}/{configuration.MaxToolsPerRequest} ({string.Join(", ", tools.Select(tool => tool.Name))})";
        trace.Add(shortlistDetail);
        Report(context, tools.Count > 0 ? "Uygulama araçları hazırlanıyor…" : "Yanıt hazırlanıyor…", shortlistDetail);
        if (configuration.DebugMode) tools.Add(DebugMissingToolDefinition());
        var selection = modelSelector.Select(route, tools.Where(tool => tool.Name != "debug.report_missing_tool").ToArray());
        var modelDetail = $"Model: {selection.Model}, reasoning={selection.ReasoningEffort}";
        trace.Add(modelDetail);
        Report(context, "Eczanenizin Yapay Zeka Asistanı yanıt hazırlıyor…", modelDetail, selection.Model);

        var input = conversations.GetRecent(context.SessionId)
            .Select(message => AiRouter.Message(message.Role, message.Content)).ToList();
        var contextJson = JsonSerializer.Serialize(context.ApplicationContext.RelevantValues());
        input.Add(AiRouter.Message("user", $"Application context (trusted metadata): {contextJson}\n\nUser request: {context.UserMessage}"));
        var state = new AiContinuationState(context, route, selection, input, tools, trace,
            new Dictionary<string, int>(StringComparer.Ordinal), 0, 0, new AiUsage(), false);
        return await ContinueAsync(state, cancellationToken);
    }

    internal async Task<AiAssistantResult> ResumeConfirmedAsync(
        AiContinuationState state,
        AiToolCall call,
        AiToolExecutionResult execution,
        CancellationToken cancellationToken)
    {
        state.Trace.ExecutedTools.Add(call.Name);
        state.Input.Add(FunctionOutput(call.CallId, execution));
        return await ContinueAsync(state with { TotalToolCalls = state.TotalToolCalls + 1 }, cancellationToken);
    }

    private async Task<AiAssistantResult> ContinueAsync(AiContinuationState initialState, CancellationToken cancellationToken)
    {
        var state = initialState;
        var configuration = configurationProvider.GetCurrent();
        var stopwatch = Stopwatch.StartNew();
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
        try
        {
            while (state.ToolTurns <= configuration.MaxToolTurns)
            {
                Report(state.Context, "Eczanenizin Yapay Zeka Asistanı yanıt hazırlıyor…",
                    $"Model request: turn={state.ToolTurns + 1}, model={state.Selection.Model}, tools={state.Tools.Count}, input_items={state.Input.Count}.",
                    state.Selection.Model);
                var response = await transport.SendAsync(new AiModelRequest(
                    state.Selection.Model,
                    state.Selection.ReasoningEffort,
                    BuildInstructions(configuration),
                    state.Input,
                    state.Tools,
                    configuration.StoreResponses,
                    true,
                    PromptCacheKey: configuration.EnablePromptCaching
                        ? BuildPromptCacheKey(configuration.PromptVersion, state.Tools)
                        : null,
                    ResponseJsonSchema: AssistantResultSchema(),
                    MaxOutputTokens: configuration.ResponseMaxOutputTokens), requestTimeout.Token);
                state = state with { Usage = Add(state.Usage, response.Usage) };
                if (response.ToolCalls.Count == 0)
                {
                    var final = ParseAssistantResponse(response.Text);
                    var message = final.Message;
                    conversations.Add(state.Context.SessionId, "user", state.Context.UserMessage);
                    conversations.Add(state.Context.SessionId, "assistant", message);
                    telemetry.Record(state.Selection.Model, state.Usage, state.TotalToolCalls);
                    operationalLog.WriteRequest(state.Trace.RequestId, state.Selection.Model, "success", stopwatch.ElapsedMilliseconds);
                    state.Trace.Add($"Completed: {stopwatch.ElapsedMilliseconds} ms, tools={state.TotalToolCalls}, input_tokens={state.Usage.InputTokens}, output_tokens={state.Usage.OutputTokens}");
                    Report(state.Context, "Yanıt tamamlandı.",
                        $"Completed: {stopwatch.ElapsedMilliseconds} ms; input={state.Usage.InputTokens}; cached={state.Usage.CachedInputTokens}; cache_write={state.Usage.CacheWriteTokens}; output={state.Usage.OutputTokens}; reasoning={state.Usage.ReasoningTokens}.",
                        state.Selection.Model);
                    return final with
                    {
                        DebugTraceId = configuration.DebugMode ? state.Trace.RequestId : null,
                        DebugSummary = configuration.DebugMode ? state.Trace.ToSummary() : null,
                        ModelUsed = state.Selection.Model,
                        UsageByModel = UsageBreakdown(state.Route, configuration.RouterModel, state.Selection.Model, state.Usage)
                    };
                }

                if (state.ToolTurns >= configuration.MaxToolTurns || state.TotalToolCalls + response.ToolCalls.Count > configuration.MaxTotalToolCalls)
                    return Fail(state, "AI araç çağrısı güvenlik sınırına ulaştı. İsteği daha küçük adımlara bölerek yeniden deneyin.");

                foreach (var item in response.OutputItems) state.Input.Add(item);
                var calls = response.ToolCalls;
                Report(state.Context, "Uygulama verileri kontrol ediliyor…",
                    $"Model requested {calls.Count} tool call(s): {string.Join(", ", calls.Select(call => call.Name))}.",
                    state.Selection.Model);
                var debugCalls = calls.Where(call => call.Name == "debug.report_missing_tool").ToArray();
                if (debugCalls.Length > 0)
                {
                    AiAssistantResult? missingFinal = null;
                    foreach (var debugCall in debugCalls)
                    {
                        var missingResult = HandleMissingTool(state, debugCall);
                        if (missingResult.State is not null) state = missingResult.State;
                        if (missingResult.Result is not null) missingFinal = missingResult.Result;
                    }
                    if (missingFinal is not null) return missingFinal with
                    {
                        DebugSummary = configuration.DebugMode ? state.Trace.ToSummary() : null,
                        ModelUsed = state.Selection.Model,
                        UsageByModel = UsageBreakdown(state.Route, configuration.RouterModel, state.Selection.Model, state.Usage)
                    };
                    continue;
                }

                foreach (var call in calls)
                {
                    var repeatedKey = call.Name + "\n" + NormalizeJson(call.ArgumentsJson);
                    var repeated = state.RepeatedCalls.GetValueOrDefault(repeatedKey) + 1;
                    state.RepeatedCalls[repeatedKey] = repeated;
                    if (repeated > configuration.MaxRepeatedToolCalls)
                        return Fail(state, "AI aynı araç çağrısını tekrarladığı için işlem güvenli biçimde durduruldu.");
                }

                var canRunParallel = calls.All(call =>
                    registryTool(call.Name)?.Risk == AiToolRisks.ReadOnly);
                if (canRunParallel)
                {
                    var continuation = state with { ToolTurns = state.ToolTurns + 1 };
                    var executions = await Task.WhenAll(calls.Select(call =>
                        ExecuteTimedAsync(state.Context, call, continuation, requestTimeout.Token)));
                    for (var index = 0; index < calls.Count; index++)
                    {
                        var execution = executions[index];
                        state.Trace.ExecutedTools.Add($"{calls[index].Name}:{execution.Result.Status}:{execution.DurationMilliseconds}ms");
                        state.Trace.Add(ToolCompletionDetail(calls[index].Name, execution.Result, execution.DurationMilliseconds));
                        state.Input.Add(FunctionOutput(calls[index].CallId, execution.Result));
                    }
                }
                else
                {
                    var completed = 0;
                    foreach (var call in calls)
                    {
                        var continuation = state with
                        {
                            ToolTurns = state.ToolTurns + 1,
                            TotalToolCalls = state.TotalToolCalls + completed
                        };
                        var timedExecution = await ExecuteTimedAsync(
                            state.Context, call, continuation, requestTimeout.Token);
                        var execution = timedExecution.Result;
                        state.Trace.ExecutedTools.Add($"{call.Name}:{execution.Status}:{timedExecution.DurationMilliseconds}ms");
                        state.Trace.Add(ToolCompletionDetail(call.Name, execution, timedExecution.DurationMilliseconds));
                        if (execution.Status == AiToolExecutionStatus.ConfirmationRequired)
                        {
                            state.Trace.Add($"Confirmation required: {call.Name}");
                            return new AiAssistantResult(
                                AiAssistantStatus.ConfirmationRequired,
                                "Bu işlem uygulama onayı gerektiriyor. Ayrıntıları kontrol edip onaylayın veya reddedin.",
                                PendingConfirmation: execution.PendingAction,
                                DebugTraceId: configuration.DebugMode ? state.Trace.RequestId : null,
                                DebugSummary: configuration.DebugMode ? state.Trace.ToSummary() : null,
                                ModelUsed: state.Selection.Model,
                                UsageByModel: UsageBreakdown(state.Route, configuration.RouterModel, state.Selection.Model, state.Usage));
                        }
                        state.Input.Add(FunctionOutput(call.CallId, execution));
                        completed++;
                    }
                }
                state = state with
                {
                    ToolTurns = state.ToolTurns + 1,
                    TotalToolCalls = state.TotalToolCalls + calls.Count
                };
            }
            return Fail(state, "AI araç döngüsü güvenlik sınırına ulaştı.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report(state.Context, "İstek iptal edildi.", "Request cancelled by the user.", state.Selection.Model);
            return new AiAssistantResult(AiAssistantStatus.Cancelled, "İstek iptal edildi.", ModelUsed: state.Selection.Model,
                UsageByModel: UsageBreakdown(state.Route, configuration.RouterModel, state.Selection.Model, state.Usage));
        }
        catch (OperationCanceledException)
        {
            Report(state.Context, "AI isteği zaman aşımına uğradı.", "Request timeout reached.", state.Selection.Model);
            return AiAssistantResult.Error("AI isteği zaman aşımına uğradı.", configuration.DebugMode ? state.Trace.RequestId : null) with
            {
                ModelUsed = state.Selection.Model,
                UsageByModel = UsageBreakdown(state.Route, configuration.RouterModel, state.Selection.Model, state.Usage)
            };
        }
        catch (AiTransportException exception)
        {
            state.Trace.Add($"Transport error: {FormatTransportError(exception)}");
            Report(state.Context, "AI isteği tamamlanamadı.", $"Transport error: {FormatTransportError(exception)}", state.Selection.Model);
            operationalLog.WriteRequest(state.Trace.RequestId, state.Selection.Model, exception.Code, stopwatch.ElapsedMilliseconds);
            return AiAssistantResult.Error(ToUserMessage(exception), configuration.DebugMode ? state.Trace.RequestId : null) with
            {
                DebugSummary = configuration.DebugMode ? state.Trace.ToSummary() : null,
                ModelUsed = state.Selection.Model,
                UsageByModel = UsageBreakdown(state.Route, configuration.RouterModel, state.Selection.Model, state.Usage)
            };
        }

        AiToolDefinition? registryTool(string name) => state.Tools.FirstOrDefault(tool => tool.Name == name);
    }

    private async Task<(AiToolExecutionResult Result, long DurationMilliseconds)> ExecuteTimedAsync(
        AiRequestContext context,
        AiToolCall call,
        AiContinuationState continuation,
        CancellationToken cancellationToken)
    {
        Report(context, "Uygulama aracı çalıştırılıyor…", $"Tool started: {call.Name}.", continuation.Selection.Model);
        var stopwatch = Stopwatch.StartNew();
        var result = await gateway.ExecuteAsync(context, call, continuation, cancellationToken);
        stopwatch.Stop();
        Report(context, "Araç sonucu değerlendiriliyor…",
            $"Tool finished: {call.Name}; status={result.Status}; duration={stopwatch.ElapsedMilliseconds} ms.",
            continuation.Selection.Model);
        return (result, stopwatch.ElapsedMilliseconds);
    }

    private (AiContinuationState? State, AiAssistantResult? Result) HandleMissingTool(
        AiContinuationState state,
        AiToolCall call)
    {
        var configuration = configurationProvider.GetCurrent();
        if (!configuration.DebugMode) return (null, new AiAssistantResult(AiAssistantStatus.Unsupported, "Bu işlem şu anda desteklenmiyor."));
        MissingToolSuggestion suggestion;
        try
        {
            suggestion = ParseSuggestion(call.ArgumentsJson, state.Context.UserMessage);
        }
        catch (JsonException)
        {
            return (null, Fail(state, "Eksik yetenek bildirimi doğrulanamadı."));
        }
        var broad = search.SearchAll(suggestion.MissingCapability + " " + suggestion.OriginalRequest, 8);
        var suggestedNamespace = suggestion.SuggestedNamespace.Trim();
        var existing = broad.FirstOrDefault(hit =>
            hit.Score >= 6 &&
            state.Tools.All(tool => tool.Name != hit.Tool.Name) &&
            (suggestedNamespace.Length == 0 || string.Equals(
                AiToolRegistry.GetNamespace(hit.Tool), suggestedNamespace, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null && !state.BroadFallbackUsed)
        {
            state.Tools.Add(existing.Tool);
            state.Input.Add(FunctionOutput(call.CallId, new AiToolExecutionResult(
                AiToolExecutionStatus.Success, call.Name,
                JsonSerializer.Serialize(new { existing_tool_found = existing.Tool.Name, retry_with_tool = true }))));
            state.Trace.Add($"Missing-tool fallback found existing tool: {existing.Tool.Name}");
            return (state with { BroadFallbackUsed = true }, null);
        }
        suggestion = suggestion with { ClosestExistingTools = broad.Select(hit => hit.Tool.Name).Take(5).ToArray() };
        var record = missingTools.Record(suggestion);
        state.Trace.Add($"Missing capability recorded: {record.SuggestedToolName}, occurrences={record.Occurrences}");
        return (null, new AiAssistantResult(
            AiAssistantStatus.Unsupported,
            "Bu işlem şu anda desteklenmiyor.",
            DebugTraceId: state.Trace.RequestId,
            DebugSummary: state.Trace.ToSummary(),
            ModelUsed: state.Selection.Model));
    }

    private string BuildInstructions(AiConfiguration configuration) =>
        prompts.AssistantCore + $"\n\nPrompt version: {configuration.PromptVersion}." +
        (configuration.DebugMode ? "\n\n" + prompts.DebugToolGap : string.Empty);

    private AiAssistantResult Fail(AiContinuationState state, string message) =>
        new(AiAssistantStatus.Error, message, DebugTraceId: state.Trace.RequestId,
            DebugSummary: state.Trace.ToSummary(), ModelUsed: state.Selection.Model,
            UsageByModel: UsageBreakdown(state.Route, configurationProvider.GetCurrent().RouterModel, state.Selection.Model, state.Usage));

    private static string ToolCompletionDetail(string name, AiToolExecutionResult execution, long durationMilliseconds)
    {
        var detail = execution.IsSuccess || string.IsNullOrWhiteSpace(execution.SafeMessage)
            ? string.Empty
            : $", detail={execution.SafeMessage}";
        return $"Tool completed: {name}, status={execution.Status}, duration={durationMilliseconds} ms{detail}";
    }

    private static IReadOnlyList<AiModelUsage> UsageBreakdown(
        AiRouteDecision route,
        string routerModel,
        string? responseModel = null,
        AiUsage? responseUsage = null)
    {
        var values = new List<AiModelUsage>(2);
        if (route.UsedModelRouter && route.RouterUsage is not null)
            values.Add(new AiModelUsage("Yönlendirici", routerModel, route.RouterUsage));
        if (!string.IsNullOrWhiteSpace(responseModel) && responseUsage is not null)
            values.Add(new AiModelUsage("Yanıt", responseModel, responseUsage));
        return values;
    }

    private static JsonElement FunctionOutput(string callId, AiToolExecutionResult execution)
    {
        JsonElement? result = null;
        if (execution.IsSuccess)
        {
            using var document = JsonDocument.Parse(execution.OutputJson);
            result = document.RootElement.Clone();
        }
        return JsonSerializer.SerializeToElement(new
        {
            type = "function_call_output",
            call_id = callId,
            output = JsonSerializer.Serialize(new
            {
                status = execution.Status.ToString().ToUpperInvariant(),
                result,
                error = execution.IsSuccess ? null : execution.SafeMessage
            })
        });
    }

    private static AiUsage Add(AiUsage left, AiUsage right) => new(
        left.InputTokens + right.InputTokens,
        left.CachedInputTokens + right.CachedInputTokens,
        left.CacheWriteTokens + right.CacheWriteTokens,
        left.OutputTokens + right.OutputTokens,
        left.ReasoningTokens + right.ReasoningTokens);

    private static string NormalizeJson(string value)
    {
        try { using var document = JsonDocument.Parse(value); return document.RootElement.GetRawText(); }
        catch (JsonException) { return value.Trim(); }
    }

    private static string BuildPromptCacheKey(string promptVersion, IReadOnlyCollection<AiToolDefinition> tools)
    {
        var catalogSlice = string.Join('|', tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
        var catalogHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalogSlice)))[..16];
        return $"pharmacyapp-ai-{promptVersion}-{catalogHash}";
    }

    private static string ToUserMessage(AiTransportException exception) => exception.Code switch
    {
        "INSUFFICIENT_QUOTA" => "OpenAI API hesabında kullanılabilir kredi veya harcama kotası bulunmuyor. Faturalandırma ayarlarını kontrol edin.",
        "RATE_LIMIT" => "AI hizmeti şu anda yoğun. Kısa bir süre sonra yeniden deneyin.",
        "TIMEOUT" => "AI hizmeti zaman aşımına uğradı.",
        "AUTHENTICATION_FAILED" => "AI bağlantı kimlik doğrulaması başarısız.",
        "NOT_CONFIGURED" => "AI bağlantısı yapılandırılmamış.",
        "INVALID_REQUEST" => "AI isteği sağlayıcı tarafından geçersiz bulundu. Debug modu açıksa teknik ayrıntıları mesajın altında görebilirsiniz.",
        "INVALID_RESPONSE" => "AI hizmetinden beklenen biçimde yanıt alınamadı.",
        "NETWORK_FAILURE" => "AI hizmetine ağ bağlantısı kurulamadı.",
        "SERVICE_UNAVAILABLE" => "AI hizmeti geçici olarak kullanılamıyor.",
        _ => "AI hizmetine şu anda ulaşılamıyor."
    };

    private static string FormatTransportError(AiTransportException exception)
    {
        var status = exception.HttpStatus is null ? string.Empty : $", http={exception.HttpStatus}";
        var requestId = string.IsNullOrWhiteSpace(exception.RequestId) ? string.Empty : $", request_id={exception.RequestId}";
        return $"{exception.Code}{status}{requestId}: {exception.Message}";
    }

    private static void Report(
        AiRequestContext context,
        string message,
        string? debugDetail = null,
        string? model = null) =>
        context.Progress?.Report(new AiActivityUpdate(message, debugDetail, model));

    private static AiAssistantResult ParseAssistantResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return AiAssistantResult.Error("AI yanıt üretemedi. Lütfen isteğinizi daha açık yazarak yeniden deneyin.");
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString() switch
            {
                "clarification_required" => AiAssistantStatus.ClarificationRequired,
                "unsupported" => AiAssistantStatus.Unsupported,
                "error" => AiAssistantStatus.Error,
                _ => AiAssistantStatus.Success
            };
            var blocks = root.GetProperty("blocks").EnumerateArray().Select(block => new AiAssistantBlock(
                block.GetProperty("type").GetString() ?? "text",
                block.GetProperty("title").ValueKind == JsonValueKind.Null ? null : block.GetProperty("title").GetString(),
                block.GetProperty("content").GetString())).ToArray();
            var actions = root.GetProperty("suggested_actions").EnumerateArray().Select(action => new AiSuggestedAction(
                action.GetProperty("id").GetString() ?? string.Empty,
                action.GetProperty("label").GetString() ?? string.Empty)).ToArray();
            return new AiAssistantResult(status, root.GetProperty("message").GetString() ?? string.Empty, blocks, actions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new AiAssistantResult(AiAssistantStatus.Success, text.Trim());
        }
    }

    private static JsonElement AssistantResultSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            status = new { type = "string", @enum = new[] { "success", "clarification_required", "unsupported", "error" } },
            message = new { type = "string" },
            blocks = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        type = new { type = "string" },
                        title = new { type = new[] { "string", "null" } },
                        content = new { type = "string" }
                    },
                    required = new[] { "type", "title", "content" },
                    additionalProperties = false
                }
            },
            suggested_actions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { id = new { type = "string" }, label = new { type = "string" } },
                    required = new[] { "id", "label" },
                    additionalProperties = false
                }
            }
        },
        required = new[] { "status", "message", "blocks", "suggested_actions" },
        additionalProperties = false
    });

    private static MissingToolSuggestion ParseSuggestion(string json, string originalRequest)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Text(string name, string fallback = "") => root.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
        string[] Array(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
            : [];
        return new MissingToolSuggestion(
            Text("original_request", originalRequest), Text("missing_capability"), Text("suggested_namespace"),
            Text("suggested_tool_name"), Text("description"), Array("suggested_parameters"),
            Text("suggested_result"), Text("reason"), Array("closest_existing_tools"), Text("estimated_risk", "read_only"));
    }

    private static AiToolDefinition DebugMissingToolDefinition() => new(
        "debug.report_missing_tool",
        "Reports a genuinely missing business capability after supplied tools are insufficient; the runtime verifies the complete catalog before recording it.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["original_request"] = new("string", "The user's original request.", true),
            ["missing_capability"] = new("string", "The unavailable business capability.", true),
            ["suggested_namespace"] = new("string", "Suggested existing or new business namespace.", true),
            ["suggested_tool_name"] = new("string", "Suggested namespace.action tool name.", true),
            ["description"] = new("string", "Concise tool purpose.", true),
            ["suggested_parameters"] = new("array", "Suggested business parameters.", true, ItemsType: "string"),
            ["suggested_result"] = new("string", "Expected structured result.", true),
            ["reason"] = new("string", "Why supplied tools cannot perform the operation.", true),
            ["closest_existing_tools"] = new("array", "Closest supplied tools.", true, ItemsType: "string"),
            ["estimated_risk"] = new("string", "Estimated risk.", true,
                AllowedValues: [AiToolRisks.ReadOnly, AiToolRisks.LowRiskWrite, AiToolRisks.ConfirmationRequired, AiToolRisks.HighRisk])
        },
        new Dictionary<string, string> { ["recorded"] = "boolean", ["existing_tool_found"] = "string|null" },
        ["debug", "missing", "capability"]);
}
