using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Routing;
using pharmacyApp.AI.Runtime;

namespace pharmacyApp.AI.Workflows;

internal sealed record AiWorkflowPlan(
    string? Answer,
    bool IsExplosive,
    string? ExplosionReason,
    string CorrectedRequest,
    double CorrectionConfidence,
    IReadOnlyList<string> MedicineNames,
    bool UseRememberedMedicine,
    IReadOnlyList<string> Queue,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> WorkflowInputs,
    AiUsage Usage,
    bool NeedsAdvancedPlanning = false,
    string? AdvancedPlanningReason = null);

internal sealed record AiWorkflowRunState(
    AiRequestContext Request,
    AiWorkflowPlan Plan,
    IReadOnlyList<string> Queue,
    int QueueIndex,
    Dictionary<string, JsonElement> Outputs,
    List<AiWorkflowTraceEntry> Trace,
    List<AiWorkflowModuleAction> ModuleActions,
    string Model,
    bool RouterResultPresented,
    AiWorkflowToolExecution? ConfirmedToolExecution = null);

internal sealed record PendingWorkflowSelection(
    string Id,
    AiWorkflowRunState State,
    IReadOnlyList<AiWorkflowSelectionOption> Options,
    DateTimeOffset ExpiresAt,
    bool AllowsMultiple = true);

internal sealed record PendingWorkflowApproval(
    string Id,
    AiWorkflowRunState State,
    string Summary,
    DateTimeOffset ExpiresAt,
    bool IsAdvancedPlanning = false,
    string? ToolActionId = null);

internal sealed record RecentPeriodWorkflow(
    string WorkflowName,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, JsonElement> ResolvedContext);

public sealed partial class WorkflowRouterChatService(
    IAiWorkflowRegistry registry,
    IAiModelTransport transport,
    IAiConfigurationProvider configurationProvider,
    IAiConversationManager conversations,
    AiUsageTelemetry telemetry,
    IAiPromptProvider prompts,
    IAiWorkflowToolExecutor toolExecutor,
    AiRequestCaptureStore? requestCaptureStore = null)
{
    private readonly AiRequestCaptureStore requestCaptures = requestCaptureStore ?? new AiRequestCaptureStore();
    private readonly object sync = new();
    private readonly Dictionary<string, PendingWorkflowSelection> pendingSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingWorkflowApproval> pendingApprovals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> rememberedMedicineSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> rememberedPatientSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> rememberedPrescriptionSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> recentWorkflowFacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecentPeriodWorkflow> recentPeriodWorkflows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, JsonElement>> recentWorkflowOutputs = new(StringComparer.Ordinal);

    public IAiWorkflowRegistry Registry => registry;

    public async Task<AiWorkflowAssistantResult> SendAsync(
        AiRequestContext request,
        CancellationToken cancellationToken = default)
    {
        var configuration = configurationProvider.GetCurrent();
        if (configuration.DebugMode && AiRouterModelCatalog.Find(request.RouterModelOverride) is { } selectedModel)
            configuration = configuration with
            {
                RouterModel = selectedModel.Id,
                RouterReasoningEffort = selectedModel.RouterReasoningEffort
            };
        requestCaptures.BeginTurn(request, configuration);
        if (!configuration.Enabled)
            return new AiWorkflowAssistantResult("Yapay zekâ asistanı şu anda devre dışı.");
        if (TryBuildPurchaseState(request, configuration, out var purchaseState))
        {
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(purchaseState, cancellationToken);
        }
        if (TryBuildPrescriptionShortageState(request, configuration, out var shortageState))
        {
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(shortageState, cancellationToken);
        }
        if (TryAnswerRecentWorkflowScopeQuestion(request, configuration, out var scopeAnswer))
        {
            conversations.Add(request.SessionId, "user", request.UserMessage);
            conversations.Add(request.SessionId, "assistant", scopeAnswer.Message);
            return scopeAnswer;
        }
        if (TryAnswerRecentResultMetricQuestion(request, configuration, out var metricAnswer))
        {
            conversations.Add(request.SessionId, "user", request.UserMessage);
            conversations.Add(request.SessionId, "assistant", metricAnswer.Message);
            return metricAnswer;
        }
        if (TryBuildPreviousSalesContentsState(request, configuration, out var previousSalesContentsState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Gösterilen satışların ilaçları hazırlanıyor…",
                "The exact retained sale references are being expanded through the read-only sale detail tool.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(previousSalesContentsState, cancellationToken);
        }
        if (TryAnswerUnsupportedWorkflowRequest(request, configuration, out var unsupportedAnswer))
        {
            conversations.Add(request.SessionId, "user", request.UserMessage);
            conversations.Add(request.SessionId, "assistant", unsupportedAnswer.Message);
            return unsupportedAnswer;
        }
        if (TryAnswerAmbiguousPrescriptionCollectionRequest(request, configuration, out var prescriptionSelectionAnswer))
        {
            conversations.Add(request.SessionId, "user", request.UserMessage);
            conversations.Add(request.SessionId, "assistant", prescriptionSelectionAnswer.Message);
            return prescriptionSelectionAnswer;
        }
        if (TryAnswerRecentSalesScopeQuestion(request, configuration, out var contextualAnswer))
        {
            conversations.Add(request.SessionId, "user", request.UserMessage);
            conversations.Add(request.SessionId, "assistant", contextualAnswer.Message);
            return contextualAnswer;
        }
        if (TryBuildNamedPatientReportsState(request, configuration, out var patientReportsState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Hasta raporları için güvenli Medula akışı hazırlanıyor…",
                "A clear named-patient report intent was planned deterministically without a router model call.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(patientReportsState, cancellationToken);
        }
        if (TryBuildRememberedPatientReportsState(request, configuration, out var rememberedPatientReportsState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Önceki hasta bağlamı doğrulanarak raporları hazırlanıyor…",
                "The remembered patient is being confirmed before the Medula report workflow continues.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            if (TryPauseForRememberedContext(rememberedPatientReportsState, out var rememberedApproval))
                return rememberedApproval;
            return await ExecuteQueueAsync(rememberedPatientReportsState, cancellationToken);
        }
        if (TryBuildLatestSalesState(request, configuration, out var latestSalesState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Son satışlar hazırlanıyor…",
                "A simple latest-sales request was planned deterministically with the shared filtered sales workflow.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(latestSalesState, cancellationToken);
        }
        if (TryBuildDiscountedSalesState(request, configuration, out var discountedSalesState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "İskontolu satışlar hazırlanıyor…",
                "An individual discounted-sales request was planned with the shared filtered sales workflow.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(discountedSalesState, cancellationToken);
        }
        if (TryBuildRememberedPatientPrescriptionsState(request, configuration, out var rememberedPatientPrescriptionsState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Önceki hasta bağlamı doğrulanarak reçeteleri hazırlanıyor…",
                "The remembered patient is being confirmed before the unified prescription workflow continues.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            if (TryPauseForRememberedContext(rememberedPatientPrescriptionsState, out var rememberedApproval))
                return rememberedApproval;
            return await ExecuteQueueAsync(rememberedPatientPrescriptionsState, cancellationToken);
        }
        if (TryBuildNamedPatientPrescriptionsState(request, configuration, out var patientPrescriptionsState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Hastanın yerel ve aktif reçeteleri hazırlanıyor…",
                "A clear named-patient prescription intent was planned deterministically without a router model call.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(patientPrescriptionsState, cancellationToken);
        }
        if (TryBuildGeneralPrescriptionSearchState(request, configuration, out var generalPrescriptionState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Medula ve yerel reçete arşivi birlikte taranıyor…",
                "A prescription date/status search was planned deterministically across live Medula and the local archive.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(generalPrescriptionState, cancellationToken);
        }
        if (TryBuildPrescriptionSearchContinuationState(request, configuration, out var prescriptionContinuationState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Önceki reçete araması yeni dönem ve fatura gruplarıyla güncelleniyor…",
                "Saved prescription status and filters are being reused without another model call.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(prescriptionContinuationState, cancellationToken);
        }
        if (TryBuildPrescriptionReportContinuationState(request, configuration, out var reportContinuationState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Önceki reçete dönemi yeni rapor durumuyla kullanılıyor…",
                "Saved prescription report-status context is being reused without another model call.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(reportContinuationState, cancellationToken);
        }
        if (TryBuildPeriodContinuationState(request, configuration, out var continuationState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Önceki iş akışı yeni dönemle güncelleniyor…",
                $"Saved workflow period continuation: {continuationState.Queue[0]}.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(continuationState, cancellationToken);
        }
        if (TryBuildPatientSalesContinuationState(request, configuration, out var patientSalesContinuationState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Seçili hastanın satış bağlamı yeniden kullanılıyor…",
                $"Saved patient sales continuation: {patientSalesContinuationState.Queue[0]}.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(patientSalesContinuationState, cancellationToken);
        }
        if (TryBuildSalesDetailContinuationState(request, configuration, out var salesContinuationState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Önceki hasılatın satışları açılıyor…",
                "Saved revenue filters are being reused for a zero-model-call sales detail continuation.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(salesContinuationState, cancellationToken);
        }
        if (TryBuildPatientDiscountContinuationState(request, configuration, out var discountContinuationState))
        {
            request.Progress?.Report(new AiActivityUpdate(
                "Önceki hasta tablosuna iskonto bilgileri ekleniyor…",
                "Saved patient and period context is being reused for a zero-model-call discount continuation.",
                configuration.RouterModel));
            conversations.Add(request.SessionId, "user", request.UserMessage);
            return await ExecuteQueueAsync(discountContinuationState, cancellationToken);
        }
        if (!transport.IsConfigured)
            return new AiWorkflowAssistantResult(
                "AI bağlantısı yapılandırılmamış. OPENAI_API_KEY ortam değişkenini veya uygulama AI ağ geçidini yapılandırın.",
                configuration.RouterModel);

        request.Progress?.Report(new AiActivityUpdate(
            "İstek yönlendiriliyor…",
            $"Workflow router started with {registry.RouterDefinitions.Count} visible workflow(s).",
            configuration.RouterModel));
        AiModelResponse response;
        try
        {
            var rememberedSelection = GetRememberedSelection(request.SessionId);
            var recentFacts = GetRecentWorkflowFacts(request.SessionId);
            var recentContext = GetRecentWorkflowContext(request.SessionId);
            var input = conversations.GetRecent(request.SessionId)
                .Select(message => AiRouter.Message(message.Role, message.Content))
                .ToList();
            input.Add(AiRouter.Message("user", JsonSerializer.Serialize(new
            {
                request = request.UserMessage,
                pharmacy_date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
                application_context = request.ApplicationContext.RelevantValues(),
                remembered_medicine_selection = rememberedSelection,
                remembered_patient_selection = GetRememberedPatientSelection(request.SessionId),
                remembered_prescription = CompactPrescriptionContext(GetRememberedPrescriptionSelection(request.SessionId)),
                recent_workflow_facts = recentFacts,
                recent_workflow_context = recentContext,
                unavailable_workflows = registry.RouterDefinitions.Where(definition =>
                    (definition.RequiredTools ?? []).Any(tool => !toolExecutor.HasTool(tool))).Select(definition => definition.Name)
            }, WorkflowRouterCatalog.ModelJson)));
            var modelRequest = new AiModelRequest(
                configuration.RouterModel,
                configuration.RouterReasoningEffort,
                BuildRouterInstructions(),
                input,
                [],
                false,
                false,
                ResponseJsonSchema: RouterSchema(registry.RouterDefinitions),
                MaxOutputTokens: configuration.RouterMaxOutputTokens);
            requestCaptures.CaptureModelRequest(request.SessionId, "router", modelRequest);
            response = await transport.SendAsync(modelRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiTransportException exception)
        {
            return new AiWorkflowAssistantResult(
                ToUserMessage(exception),
                configuration.RouterModel,
                DebugSummary: configuration.DebugMode
                    ? $"Workflow router transport error: {exception.Code}; HTTP={exception.HttpStatus}; request_id={exception.RequestId ?? "none"}"
                    : null);
        }

        AiWorkflowPlan plan;
        try
        {
            plan = ParsePlan(response, request.UserMessage, registry);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new AiWorkflowAssistantResult(
                "Yönlendirici yanıtı doğrulanamadı. Lütfen isteğinizi yeniden yazın.",
                configuration.RouterModel,
                [new AiModelUsage("Yönlendirici", configuration.RouterModel, response.Usage)],
                configuration.DebugMode ? $"Workflow router response validation failed: {exception.Message}" : null);
        }

        plan = NormalizePharmacySpelling(plan, request.UserMessage);
        var effectiveRequest = SafeCorrectedRequest(plan, request.UserMessage);
        plan = plan with { CorrectedRequest = effectiveRequest };
        if (plan.Queue.Count > 0 && !plan.Queue.Contains(PreviousResultWorkflow.WorkflowName, StringComparer.Ordinal))
        {
        plan = NormalizeMovementPeriod(plan);
        plan = NormalizeSalesIntent(plan);
        plan = NormalizeProductPriceComparisonIntent(plan, effectiveRequest);
        plan = NormalizeMedicineComparisonIntent(plan, effectiveRequest);
        plan = NormalizeStockIntent(plan, effectiveRequest);
        plan = NormalizeProductStockSummaryIntent(plan, effectiveRequest);
        plan = NormalizeExactBarcodeInformationIntent(plan, effectiveRequest);
        plan = NormalizeLatestRefundIntent(plan, effectiveRequest);
        plan = NormalizeDiversityAnalysisIntent(plan, effectiveRequest);
        plan = NormalizePatientProductIntent(plan, effectiveRequest);
        plan = NormalizePurchaseRecommendationIntent(plan, effectiveRequest);
        plan = NormalizeOrderIntent(plan, effectiveRequest);
        var contactRequest = ExtractPatientContactQuery(effectiveRequest) is not null
            ? effectiveRequest
            : request.UserMessage;
        plan = NormalizePatientContactIntent(plan, contactRequest);
        plan = NormalizeLocalProductPriceIntent(plan);
        plan = NormalizeEquivalentIntent(plan);
        plan = NormalizeGlobalCatalogIntent(plan);
        plan = NormalizeVerifiedAnalytics(plan);
        plan = NormalizeDeterministicSalesSummaryIntent(plan, effectiveRequest);
        plan = NormalizePrescriptionReportIntent(plan, effectiveRequest);
        plan = NormalizeAmbiguousPastMedicationIntent(plan, effectiveRequest);
        plan = NormalizeMedulaIntent(plan, effectiveRequest);
        plan = NormalizeMedicineFilteredLatestPrescriptionIntent(plan, effectiveRequest);
        plan = NormalizePreviousContextIntent(plan, effectiveRequest);
        plan = NormalizePatientFromRecentResult(plan, request.SessionId, effectiveRequest);
        plan = NormalizeBareMedicineIntent(plan, effectiveRequest);
        }
        plan = PreserveExplosiveSafety(plan, request.UserMessage);
        plan = NormalizePresentation(plan);
        var rememberedCandidate = plan.UseRememberedMedicine && plan.MedicineNames.Count == 0
            ? GetRememberedSelection(request.SessionId)
            : null;
        var rememberedOutput = rememberedCandidate is { } remembered &&
                               (RememberedSelectionMatchesRequest(plan.CorrectedRequest, remembered) || HasRecentOutputCue(plan.CorrectedRequest))
            ? rememberedCandidate
            : null;
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (HasRecentOutputCue(plan.CorrectedRequest) || plan.Queue.Any(name => name is PreviousResultWorkflow.WorkflowName or "onceki_satislarin_iceriklerini_gor"))
        {
            foreach (var pair in GetRecentWorkflowOutputs(request.SessionId)) outputs[pair.Key] = pair.Value.Clone();
            if (GetRecentBarcodeContext(request.SessionId) is { } barcodeContext)
                outputs["recent_barcode_context"] = barcodeContext;
        }
        if (rememberedOutput is { } selection)
            outputs["ilac_barkod_eslestir"] = selection.Clone();
        var rememberedPatient = GetRememberedPatientSelection(request.SessionId);
        var reuseRememberedPatient = rememberedPatient is { } patientSelection &&
                                     ShouldReuseRememberedPatient(request.SessionId, plan, patientSelection);
        if (reuseRememberedPatient) outputs["hasta_eslestir"] = rememberedPatient!.Value.Clone();
        var queue = plan.Queue
            .Where(name => !(rememberedOutput.HasValue && name == "ilac_barkod_eslestir"))
            .Where(name => !(reuseRememberedPatient && name == "hasta_eslestir"))
            .ToList();
        if (configuration.DebugMode && queue.Count > 0 &&
            registry.Get(queue[^1])?.Definition.ProducesUserResponse != true &&
            registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");

        var state = new AiWorkflowRunState(
            request,
            plan,
            queue,
            0,
            outputs,
            [],
            [],
            configuration.RouterModel,
            false);
        conversations.Add(request.SessionId, "user", request.UserMessage);
        if (plan.IsExplosive)
        {
            var summary = "Güvenlik uyarısı: " +
                          (plan.ExplosionReason ?? "İstek yanlış uygulanırsa eczane işlemlerine zarar verebilir.") +
                          Environment.NewLine + Environment.NewLine + "Emin misiniz?";
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(configuration.ConfirmationLifetimeMinutes);
            var pending = new PendingWorkflowApproval(
                Guid.NewGuid().ToString("N"), state with { RouterResultPresented = true }, summary, expiresAt);
            lock (sync) pendingApprovals[pending.Id] = pending;
            conversations.Add(request.SessionId, "assistant", summary);
            return Result(state, summary,
                debug: DebugSummary(state, "Router paused the explosive request before workflow execution."),
                approval: new AiWorkflowApprovalView(pending.Id, summary, expiresAt));
        }

        if (TryPauseForRememberedContext(state, out var contextApproval))
            return contextApproval;

        if (plan.NeedsAdvancedPlanning)
        {
            var reason = string.IsNullOrWhiteSpace(plan.AdvancedPlanningReason)
                ? "İstek mevcut iş akışlarıyla güvenilir biçimde planlanamadı."
                : plan.AdvancedPlanningReason.Trim();
            var summary = reason + Environment.NewLine + Environment.NewLine +
                          $"Daha gelişmiş {configuration.DefaultModel} modeliyle bir kez yeniden planlansın mı?";
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(configuration.ConfirmationLifetimeMinutes);
            var pending = new PendingWorkflowApproval(Guid.NewGuid().ToString("N"),
                state with { RouterResultPresented = true }, summary, expiresAt, IsAdvancedPlanning: true);
            lock (sync) pendingApprovals[pending.Id] = pending;
            conversations.Add(request.SessionId, "assistant", summary);
            return Result(state, summary,
                DebugSummary(state, "Router requested permission for one controlled Terra replan."),
                approval: new AiWorkflowApprovalView(pending.Id, summary, expiresAt));
        }

        if (queue.Count == 0)
        {
            var answer = string.IsNullOrWhiteSpace(plan.Answer)
                ? "Bu istek için kullanılabilir bir iş akışı bulunmuyor."
                : plan.Answer.Trim();
            conversations.Add(request.SessionId, "assistant", answer);
            telemetry.Record(configuration.RouterModel, response.Usage, 0);
            return Result(state, answer, DebugSummary(state, "Router answered without a workflow."));
        }
        return await ExecuteQueueAsync(state, cancellationToken);
    }

    public async Task<AiWorkflowAssistantResult> ContinueSelectionAsync(
        string pendingId,
        string userId,
        IReadOnlyList<string> selectedOptionIds,
        CancellationToken cancellationToken = default,
        IProgress<AiActivityUpdate>? progress = null)
    {
        PendingWorkflowSelection? pending;
        lock (sync)
        {
            if (!pendingSelections.TryGetValue(pendingId, out pending) ||
                pending.ExpiresAt <= DateTimeOffset.UtcNow ||
                !Owns(pending.State.Request, userId))
                pending = null;
        }
        if (pending is null)
            return new AiWorkflowAssistantResult("Bekleyen seçim bulunamadı veya süresi doldu.");
        var selected = selectedOptionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0 || selected.Any(id => pending.Options.All(option => option.Id != id)) ||
            (!pending.AllowsMultiple && selected.Length != 1))
            return new AiWorkflowAssistantResult(
                pending.AllowsMultiple ? "En az bir geçerli seçenek seçin." : "Lütfen yalnızca bir seçenek seçin.",
                PendingSelection: new AiWorkflowSelectionView(pending.Id,
                    pending.State.Trace.LastOrDefault()?.Message ?? "Seçiminizi yapın.",
                    pending.Options, pending.AllowsMultiple, pending.ExpiresAt));
        lock (sync)
        {
            // Consume only a valid, owned selection. Invalid submissions must not destroy
            // the user's saved queue, and racing clicks must not execute it twice.
            if (!pendingSelections.Remove(pendingId))
                return new AiWorkflowAssistantResult("Bu seçim zaten işlendi.");
        }
        requestCaptures.Clear(pending.State.Request.SessionId);

        var state = pending.State with
        {
            Request = pending.State.Request with { Progress = progress }
        };
        var workflow = registry.Get(state.Queue[state.QueueIndex])!;
        var context = BuildContext(state, selected, pending.Options);
        var execution = await ExecuteStepAsync(workflow, context, cancellationToken);
        state.Trace.Add(execution.Trace);
        AddModuleActions(state, execution.Result.ModuleActions);
        if (execution.Result.Status != AiWorkflowStepStatus.Completed)
            return Finish(state, execution.Result.Message);
        state.Outputs[workflow.Definition.Name] = execution.Result.Output.Clone();
        RememberMedicineSelection(state.Request.SessionId, workflow.Definition.Name, execution.Result.Output);
        RememberPatientSelection(state.Request.SessionId, workflow.Definition.Name, execution.Result.Output);
        RememberPrescriptionSelection(state.Request.SessionId, workflow.Definition.Name, execution.Result.Output);
        if (workflow.Definition.Name == "is_akisi_dali_sec")
        {
            if (!TryApplySelectedBranch(state, execution.Result.Output, out var branched))
                return Finish(state, "Seçilen iş akışı dalı doğrulanamadı.");
            state = branched;
        }
        state = state with { QueueIndex = state.QueueIndex + 1 };
        return await ExecuteQueueAsync(state, cancellationToken);
    }

    private bool TryApplySelectedBranch(
        AiWorkflowRunState state,
        JsonElement selection,
        out AiWorkflowRunState branched)
    {
        branched = state;
        if (!selection.TryGetProperty("queue", out var queueValue) || queueValue.ValueKind != JsonValueKind.Array)
            return false;
        var branchQueue = queueValue.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty)
            .ToArray();
        if (branchQueue.Length == 0 || state.QueueIndex + 1 + branchQueue.Length > 20 ||
            branchQueue.Distinct(StringComparer.Ordinal).Count() != branchQueue.Length ||
            branchQueue.Contains("is_akisi_dali_sec", StringComparer.Ordinal) ||
            branchQueue.Any(name => registry.Get(name)?.Definition.RouterVisible != true)) return false;
        var queue = state.Queue.Take(state.QueueIndex + 1).Concat(branchQueue).ToArray();
        var inputs = state.Plan.WorkflowInputs.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        if (selection.TryGetProperty("inputs", out var inputValue))
        {
            if (inputValue.ValueKind != JsonValueKind.Object ||
                inputValue.EnumerateObject().Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() !=
                inputValue.EnumerateObject().Count()) return false;
            foreach (var workflow in inputValue.EnumerateObject())
            {
                if (workflow.Value.ValueKind != JsonValueKind.Object ||
                    !branchQueue.Contains(workflow.Name, StringComparer.Ordinal)) return false;
                var allowed = registry.Get(workflow.Name)!.Definition.Parameters;
                if (workflow.Value.EnumerateObject().Any(property => allowed?.ContainsKey(property.Name) != true) ||
                    workflow.Value.EnumerateObject().Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() !=
                    workflow.Value.EnumerateObject().Count())
                    return false;
                inputs[workflow.Name] = workflow.Value.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText(),
                    StringComparer.Ordinal);
            }
        }
        branched = state with
        {
            Queue = queue,
            Plan = state.Plan with { Queue = queue, WorkflowInputs = inputs }
        };
        return true;
    }

    public async Task<AiWorkflowAssistantResult> ApproveAsync(
        string pendingId,
        string userId,
        CancellationToken cancellationToken = default,
        IProgress<AiActivityUpdate>? progress = null)
    {
        PendingWorkflowApproval? pending;
        lock (sync)
        {
            if (!pendingApprovals.TryGetValue(pendingId, out pending) ||
                pending.ExpiresAt <= DateTimeOffset.UtcNow ||
                !Owns(pending.State.Request, userId))
                pending = null;
            else pendingApprovals.Remove(pendingId);
        }
        if (pending is null)
            return new AiWorkflowAssistantResult("Onay bekleyen yönlendirme bulunamadı veya süresi doldu.");
        requestCaptures.Clear(pending.State.Request.SessionId);
        var state = pending.State with { Request = pending.State.Request with { Progress = progress } };
        if (!string.IsNullOrWhiteSpace(pending.ToolActionId))
        {
            var confirmed = await toolExecutor.ExecuteConfirmedAsync(
                pending.ToolActionId, userId, cancellationToken);
            if (!confirmed.IsSuccess)
                return Finish(state, "Onaylanan Medula sorgusu tamamlanamadı.");
            return await ExecuteQueueAsync(
                state with { ConfirmedToolExecution = confirmed },
                cancellationToken);
        }
        if (pending.IsAdvancedPlanning)
            return await ReplanWithAdvancedModelAsync(state, cancellationToken);
        if (state.Queue.Count == 0)
            return Finish(state, string.IsNullOrWhiteSpace(state.Plan.Answer)
                ? "Onaylandı; ancak bu istek için çalıştırılabilir bir iş akışı bulunmuyor."
                : state.Plan.Answer!);
        return await ExecuteQueueAsync(state, cancellationToken);
    }

    private async Task<AiWorkflowAssistantResult> ReplanWithAdvancedModelAsync(
        AiWorkflowRunState previousState,
        CancellationToken cancellationToken)
    {
        var configuration = configurationProvider.GetCurrent();
        previousState.Request.Progress?.Report(new AiActivityUpdate(
            "Gelişmiş model iş akışını planlıyor…",
            $"One controlled workflow replan started with {configuration.DefaultModel}.",
            configuration.DefaultModel));
        AiModelResponse response;
        try
        {
            var input = new[]
            {
                AiRouter.Message("user", JsonSerializer.Serialize(new
                {
                    request = previousState.Request.UserMessage,
                    pharmacy_date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
                    application_context = previousState.Request.ApplicationContext.RelevantValues(),
                    remembered_medicine_selection = GetRememberedSelection(previousState.Request.SessionId),
                    remembered_patient_selection = GetRememberedPatientSelection(previousState.Request.SessionId),
                    remembered_prescription = CompactPrescriptionContext(GetRememberedPrescriptionSelection(previousState.Request.SessionId)),
                    recent_workflow_facts = GetRecentWorkflowFacts(previousState.Request.SessionId),
                    recent_workflow_context = GetRecentWorkflowContext(previousState.Request.SessionId),
                    unavailable_workflows = registry.RouterDefinitions.Where(definition =>
                        (definition.RequiredTools ?? []).Any(tool => !toolExecutor.HasTool(tool))).Select(definition => definition.Name)
                }, WorkflowRouterCatalog.ModelJson))
            };
            requestCaptures.Clear(previousState.Request.SessionId);
            var modelRequest = new AiModelRequest(
                configuration.DefaultModel,
                configuration.DefaultReasoningEffort,
                BuildRouterInstructions() + Environment.NewLine + Environment.NewLine +
                "This is the single approved advanced replan. Build the best complete queue from available workflows. " +
                "Never request SQL, code execution, arbitrary methods, or unavailable tools. Set needs_advanced_planning=false.",
                input, [], false, false,
                ResponseJsonSchema: RouterSchema(registry.RouterDefinitions),
                MaxOutputTokens: configuration.RouterMaxOutputTokens);
            requestCaptures.CaptureModelRequest(previousState.Request.SessionId, "advanced-planner", modelRequest);
            response = await transport.SendAsync(modelRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AiTransportException exception)
        {
            return new AiWorkflowAssistantResult(ToUserMessage(exception), configuration.DefaultModel,
                DebugSummary: configuration.DebugMode
                    ? $"Advanced workflow replan failed: {exception.Code}; HTTP={exception.HttpStatus}; request_id={exception.RequestId ?? "none"}"
                    : null);
        }

        AiWorkflowPlan plan;
        try { plan = ParsePlan(response, previousState.Request.UserMessage, registry); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new AiWorkflowAssistantResult("Gelişmiş iş akışı planı doğrulanamadı.", configuration.DefaultModel,
                [new AiModelUsage("Gelişmiş Planlayıcı", configuration.DefaultModel, response.Usage)],
                configuration.DebugMode ? exception.Message : null);
        }
        plan = NormalizePharmacySpelling(plan, previousState.Request.UserMessage);
        var effectiveRequest = SafeCorrectedRequest(plan, previousState.Request.UserMessage);
        plan = plan with { CorrectedRequest = effectiveRequest };
        if (plan.Queue.Count > 0 && !plan.Queue.Contains(PreviousResultWorkflow.WorkflowName, StringComparer.Ordinal))
        {
        plan = NormalizeMovementPeriod(plan);
        plan = NormalizeSalesIntent(plan);
        plan = NormalizeProductPriceComparisonIntent(plan, effectiveRequest);
        plan = NormalizeMedicineComparisonIntent(plan, effectiveRequest);
        plan = NormalizeStockIntent(plan, effectiveRequest);
        plan = NormalizeProductStockSummaryIntent(plan, effectiveRequest);
        plan = NormalizeExactBarcodeInformationIntent(plan, effectiveRequest);
        plan = NormalizeLatestRefundIntent(plan, effectiveRequest);
        plan = NormalizeDiversityAnalysisIntent(plan, effectiveRequest);
        plan = NormalizePatientProductIntent(plan, effectiveRequest);
        plan = NormalizePurchaseRecommendationIntent(plan, effectiveRequest);
        plan = NormalizeOrderIntent(plan, effectiveRequest);
        var contactRequest = ExtractPatientContactQuery(effectiveRequest) is not null
            ? effectiveRequest
            : previousState.Request.UserMessage;
        plan = NormalizePatientContactIntent(plan, contactRequest);
        plan = NormalizeLocalProductPriceIntent(plan);
        plan = NormalizeEquivalentIntent(plan);
        plan = NormalizeGlobalCatalogIntent(plan);
        plan = NormalizeVerifiedAnalytics(plan);
        plan = NormalizeDeterministicSalesSummaryIntent(plan, effectiveRequest);
        plan = NormalizePrescriptionReportIntent(plan, effectiveRequest);
        plan = NormalizeAmbiguousPastMedicationIntent(plan, effectiveRequest);
        plan = NormalizeMedulaIntent(plan, effectiveRequest);
        plan = NormalizeMedicineFilteredLatestPrescriptionIntent(plan, effectiveRequest);
        plan = NormalizePreviousContextIntent(plan, effectiveRequest);
        plan = NormalizePatientFromRecentResult(plan, previousState.Request.SessionId, effectiveRequest);
        plan = NormalizeBareMedicineIntent(plan, effectiveRequest);
        }
        plan = PreserveExplosiveSafety(plan, previousState.Request.UserMessage);
        plan = NormalizePresentation(plan);
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (HasRecentOutputCue(plan.CorrectedRequest) || plan.Queue.Any(name => name is PreviousResultWorkflow.WorkflowName or "onceki_satislarin_iceriklerini_gor"))
            foreach (var pair in GetRecentWorkflowOutputs(previousState.Request.SessionId)) outputs[pair.Key] = pair.Value.Clone();
        var remembered = plan.UseRememberedMedicine && plan.MedicineNames.Count == 0
            ? GetRememberedSelection(previousState.Request.SessionId) : null;
        if (remembered is { } selection && (RememberedSelectionMatchesRequest(plan.CorrectedRequest, selection) || HasRecentOutputCue(plan.CorrectedRequest)))
            outputs["ilac_barkod_eslestir"] = selection.Clone();
        var rememberedPatient = GetRememberedPatientSelection(previousState.Request.SessionId);
        var reuseRememberedPatient = rememberedPatient is { } patientSelection &&
                                     ShouldReuseRememberedPatient(previousState.Request.SessionId, plan, patientSelection);
        if (reuseRememberedPatient) outputs["hasta_eslestir"] = rememberedPatient!.Value.Clone();
        var queue = plan.Queue
            .Where(name => !(outputs.ContainsKey("ilac_barkod_eslestir") && name == "ilac_barkod_eslestir"))
            .Where(name => !(reuseRememberedPatient && name == "hasta_eslestir"))
            .ToArray();
        if (queue.Length == 0)
        {
            var answer = string.IsNullOrWhiteSpace(plan.Answer)
                ? "Gelişmiş model de mevcut güvenli iş akışlarıyla bu isteği planlayamadı."
                : plan.Answer.Trim();
            conversations.Add(previousState.Request.SessionId, "assistant", answer);
            telemetry.Record(configuration.DefaultModel, response.Usage, 0);
            return new AiWorkflowAssistantResult(answer, configuration.DefaultModel,
                [new AiModelUsage("Gelişmiş Planlayıcı", configuration.DefaultModel, response.Usage)],
                configuration.DebugMode ? "Advanced replan completed without an executable workflow." : null);
        }
        var state = new AiWorkflowRunState(previousState.Request, plan with
        {
            Usage = response.Usage,
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        }, queue, 0, outputs, [], [], configuration.DefaultModel, false);
        if (TryPauseForRememberedContext(state, out var contextApproval))
            return contextApproval;
        return await ExecuteQueueAsync(state, cancellationToken);
    }

    public bool RejectApproval(string pendingId, string userId)
    {
        lock (sync)
        {
            if (!pendingApprovals.TryGetValue(pendingId, out var pending) || !Owns(pending.State.Request, userId))
                return false;
            pendingApprovals.Remove(pendingId);
            if (!string.IsNullOrWhiteSpace(pending.ToolActionId))
                toolExecutor.RejectConfirmation(pending.ToolActionId, userId);
            return true;
        }
    }

    public IReadOnlyList<AiRequestCaptureFile> GetLatestRequestCaptureFiles(string sessionId) =>
        requestCaptures.GetLatest(sessionId);

    private bool TryPauseForRememberedContext(
        AiWorkflowRunState state,
        out AiWorkflowAssistantResult result)
    {
        result = null!;
        if (state.QueueIndex >= state.Queue.Count) return false;
        var marker = state.Queue[state.QueueIndex];
        JsonElement? remembered = marker switch
        {
            "onceki_hasta_bilgisini_getir" => GetRememberedPatientSelection(state.Request.SessionId),
            "onceki_ilac_bilgisini_getir" => GetRememberedSelection(state.Request.SessionId),
            "onceki_recete_bilgisini_getir" => GetRememberedPrescriptionSelection(state.Request.SessionId),
            _ => null
        };
        if (remembered is not { } context) return false;

        var outputs = state.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        string summary;
        switch (marker)
        {
            case "onceki_hasta_bilgisini_getir":
                outputs["remembered_patient_context"] = context.Clone();
                outputs["hasta_eslestir"] = context.Clone();
                var patient = context.TryGetProperty("selected_patient", out var selectedPatient)
                    ? WorkflowData.Text(selectedPatient, "name")
                    : "önceki hasta";
                summary = $"Bahsettiğiniz hasta {patient} mı?";
                break;
            case "onceki_ilac_bilgisini_getir":
                outputs["remembered_medicine_context"] = context.Clone();
                outputs["ilac_barkod_eslestir"] = context.Clone();
                var medicineNames = context.TryGetProperty("selected_products", out var products) && products.ValueKind == JsonValueKind.Array
                    ? products.EnumerateArray().Select(item => WorkflowData.Text(item, "name")).Where(value => value.Length > 0).Take(3).ToArray()
                    : [];
                summary = $"Bahsettiğiniz ilaç {string.Join(", ", medicineNames.DefaultIfEmpty("önceki seçili ilaç"))} mı?";
                break;
            default:
                outputs["remembered_prescription_context"] = context.Clone();
                if (TryGetPrescriptionPayload(context, out var prescription))
                    outputs["medula_recete_bilgisi_getir"] = context.Clone();
                var number = TryGetPrescriptionPayload(context, out var payload)
                    ? WorkflowData.Text(payload, "prescription_number")
                    : "önceki";
                summary = $"Bahsettiğiniz reçete {number} numaralı reçete mi?";
                break;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(
            configurationProvider.GetCurrent().ConfirmationLifetimeMinutes);
        var pendingState = state with
        {
            QueueIndex = state.QueueIndex + 1,
            Outputs = outputs,
            RouterResultPresented = true
        };
        var pending = new PendingWorkflowApproval(
            Guid.NewGuid().ToString("N"), pendingState, summary, expiresAt);
        lock (sync) pendingApprovals[pending.Id] = pending;
        conversations.Add(state.Request.SessionId, "assistant", summary);
        result = Result(state, summary,
            DebugSummary(state, $"Previous context confirmation requested: {marker}."),
            approval: new AiWorkflowApprovalView(pending.Id, summary, expiresAt));
        return true;
    }

    public void Clear(string sessionId)
    {
        conversations.Clear(sessionId);
        lock (sync)
        {
            foreach (var id in pendingSelections.Where(pair => pair.Value.State.Request.SessionId == sessionId).Select(pair => pair.Key).ToArray())
                pendingSelections.Remove(id);
            foreach (var id in pendingApprovals.Where(pair => pair.Value.State.Request.SessionId == sessionId).Select(pair => pair.Key).ToArray())
                pendingApprovals.Remove(id);
            rememberedMedicineSelections.Remove(sessionId);
            recentPurchaseSubjects.Remove(sessionId);
            rememberedPatientSelections.Remove(sessionId);
            rememberedPrescriptionSelections.Remove(sessionId);
            recentWorkflowFacts.Remove(sessionId);
            recentPeriodWorkflows.Remove(sessionId);
            recentWorkflowOutputs.Remove(sessionId);
        }
    }

    private async Task<AiWorkflowAssistantResult> ExecuteQueueAsync(
        AiWorkflowRunState initialState,
        CancellationToken cancellationToken)
    {
        var state = initialState;
        while (state.QueueIndex < state.Queue.Count)
        {
            var workflow = registry.Get(state.Queue[state.QueueIndex]);
            if (workflow is null) return Finish(state, "İş akışı sırası doğrulanamadı.");
            var missingTools = (workflow.Definition.RequiredTools ?? [])
                .Where(toolName => !toolExecutor.HasTool(toolName))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (missingTools.Length > 0)
            {
                var missingMessage = $"'{workflow.Definition.DisplayFunctionName}' iş akışı için eksik araçlar: " +
                                     $"{string.Join(", ", missingTools)}. Bu araçları eklemeniz gerekiyor.";
                var output = JsonSerializer.SerializeToElement(new { missing_tools = missingTools });
                state.Trace.Add(new AiWorkflowTraceEntry(
                    workflow.Definition.Name, workflow.Definition.DisplayName,
                    AiWorkflowStepStatus.MissingTool, 0, missingMessage, output));
                return Finish(state, missingMessage);
            }
            state.Request.Progress?.Report(new AiActivityUpdate(
                $"{workflow.Definition.DisplayFunctionName} çalışıyor…",
                $"Workflow started: {workflow.Definition.DisplayFunctionName}; queue_position={state.QueueIndex + 1}/{state.Queue.Count}."));
            var execution = await ExecuteStepAsync(workflow, BuildContext(state), cancellationToken);
            state.Trace.Add(execution.Trace);
            AddModuleActions(state, execution.Result.ModuleActions);
            if (execution.Result.Status == AiWorkflowStepStatus.AwaitingSelection)
            {
                var options = execution.Result.SelectionOptions ?? [];
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(
                    configurationProvider.GetCurrent().ConfirmationLifetimeMinutes);
                var pending = new PendingWorkflowSelection(
                    Guid.NewGuid().ToString("N"), state with { RouterResultPresented = true }, options, expiresAt,
                    execution.Result.AllowsMultipleSelection);
                lock (sync) pendingSelections[pending.Id] = pending;
                conversations.Add(state.Request.SessionId, "assistant", execution.Result.Message);
                return Result(state, execution.Result.Message,
                    DebugSummary(state, $"Workflow awaiting {options.Count} selectable product option(s)."),
                    selection: new AiWorkflowSelectionView(
                        pending.Id, execution.Result.Message, options,
                        execution.Result.AllowsMultipleSelection, expiresAt));
            }
            if (execution.Result.Status == AiWorkflowStepStatus.AwaitingApproval)
            {
                var toolActionId = execution.Result.Output.TryGetProperty("pending_action_id", out var actionId) &&
                                   actionId.ValueKind == JsonValueKind.String
                    ? actionId.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(toolActionId))
                    return Finish(state, "Medula sorgusu için onay kaydı oluşturulamadı.");
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(
                    configurationProvider.GetCurrent().ConfirmationLifetimeMinutes);
                var pending = new PendingWorkflowApproval(
                    Guid.NewGuid().ToString("N"),
                    state with { RouterResultPresented = true },
                    execution.Result.Message,
                    expiresAt,
                    ToolActionId: toolActionId);
                lock (sync) pendingApprovals[pending.Id] = pending;
                conversations.Add(state.Request.SessionId, "assistant", execution.Result.Message);
                return Result(state, execution.Result.Message,
                    DebugSummary(state, $"Workflow awaits confirmation for {workflow.Definition.DisplayFunctionName}."),
                    approval: new AiWorkflowApprovalView(pending.Id, execution.Result.Message, expiresAt));
            }
            if (execution.Result.Status is AiWorkflowStepStatus.Failed or AiWorkflowStepStatus.MissingTool)
                return Finish(state, execution.Result.Message);
            state.Outputs[workflow.Definition.Name] = execution.Result.Output.Clone();
            RememberMedicineSelection(state.Request.SessionId, workflow.Definition.Name, execution.Result.Output);
            RememberPatientSelection(state.Request.SessionId, workflow.Definition.Name, execution.Result.Output);
            RememberPrescriptionSelection(state.Request.SessionId, workflow.Definition.Name, execution.Result.Output);
            state = state with { QueueIndex = state.QueueIndex + 1, ConfirmedToolExecution = null };
            if (workflow.Definition.ProducesUserResponse)
                return Finish(state, execution.Result.Message, execution.Result.Table, execution.Result.Content);
        }
        var prescription = BuildImplicitPrescription(state);
        var content = prescription is null ? BuildImplicitContent(state) : null;
        var table = prescription is null && content is null
            ? await BuildImplicitTableAsync(state, cancellationToken)
            : null;
        return Finish(state, BuildUserMessage(state), table, content, prescription);
    }

    private AiWorkflowAssistantResult Finish(
        AiWorkflowRunState state,
        string message,
        AiWorkflowTablePresentation? table = null,
        AiWorkflowContentPresentation? content = null,
        AiWorkflowPrescriptionPresentation? prescription = null)
    {
        prescription ??= BuildImplicitPrescription(state);
        if (prescription is not null)
        {
            table = null;
            content = null;
        }
        conversations.Add(state.Request.SessionId, "assistant", message);
        RememberRecentWorkflowFacts(state.Request.SessionId, state);
        telemetry.Record(state.Model, state.Plan.Usage, state.Trace.Count);
        return Result(state, message, DebugSummary(state, "Workflow queue completed."),
            table: table, moduleActions: state.ModuleActions, content: content,
            prescription: prescription);
    }

    private AiWorkflowContext BuildContext(
        AiWorkflowRunState state,
        IReadOnlyList<string>? selected = null,
        IReadOnlyList<AiWorkflowSelectionOption>? offered = null) => new(
        state.Request.UserMessage,
        state.Plan.CorrectedRequest,
        state.Request.ApplicationContext,
        state.Outputs,
        state.Trace,
        state.Plan.MedicineNames,
        selected,
        offered,
        state.Request,
        toolExecutor,
        state.Plan.WorkflowInputs.GetValueOrDefault(state.Queue[state.QueueIndex]),
        state.ConfirmedToolExecution);

    private static async Task<(AiWorkflowStepResult Result, AiWorkflowTraceEntry Trace)> ExecuteStepAsync(
        IAiWorkflow workflow,
        AiWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        AiWorkflowStepResult result;
        try
        {
            result = await workflow.ExecuteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (AiWorkflowInputException exception)
        {
            result = new AiWorkflowStepResult(AiWorkflowStepStatus.Failed, exception.Message,
                JsonSerializer.SerializeToElement(new { error = "INVALID_WORKFLOW_INPUT" }));
        }
        catch (Exception)
        {
            result = new AiWorkflowStepResult(
                AiWorkflowStepStatus.Failed,
                "İş akışı çalıştırılırken beklenmeyen bir hata oluştu.",
                JsonSerializer.SerializeToElement(new { error = "WORKFLOW_EXECUTION_FAILED" }));
        }
        stopwatch.Stop();
        return (result, new AiWorkflowTraceEntry(
            workflow.Definition.Name,
            workflow.Definition.DisplayName,
            result.Status,
            stopwatch.ElapsedMilliseconds,
            result.Message,
            result.Output.Clone()));
    }

    private AiWorkflowAssistantResult Result(
        AiWorkflowRunState state,
        string message,
        string? debug,
        AiWorkflowSelectionView? selection = null,
        AiWorkflowApprovalView? approval = null,
        AiWorkflowTablePresentation? table = null,
        IReadOnlyList<AiWorkflowModuleAction>? moduleActions = null,
        AiWorkflowContentPresentation? content = null,
        AiWorkflowPrescriptionPresentation? prescription = null)
    {
        var includeRouterCall = !state.RouterResultPresented;
        return new AiWorkflowAssistantResult(
            message,
            includeRouterCall ? state.Model : null,
            includeRouterCall ? [new AiModelUsage("Yönlendirici", state.Model, state.Plan.Usage)] : [],
            configurationProvider.GetCurrent().DebugMode ? debug : null,
            selection,
            approval,
            table,
            moduleActions,
            content,
            state.Queue.ToArray(),
            prescription);
    }

    private static AiWorkflowPrescriptionPresentation? BuildImplicitPrescription(AiWorkflowRunState state)
    {
        foreach (var output in state.Trace.Where(entry => entry.Status == AiWorkflowStepStatus.Completed)
                     .Reverse().Select(entry => entry.Output))
        {
            if (output.TryGetProperty("metadata", out var displayMetadata) && displayMetadata.ValueKind == JsonValueKind.Object &&
                displayMetadata.TryGetProperty("suppress_prescription_presentation", out var suppress) && suppress.ValueKind == JsonValueKind.True)
                return null;
            if (output.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("prescription", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var presentation = MedulaWorkflowPresentation.BuildPrescription(nested);
                if (presentation is not null) return presentation;
            }
            var direct = MedulaWorkflowPresentation.BuildPrescription(output);
            if (direct is not null) return direct;
        }
        return null;
    }

    private static AiWorkflowContentPresentation? BuildImplicitContent(AiWorkflowRunState state)
    {
        var source = state.Outputs.Reverse().FirstOrDefault(pair =>
            pair.Value.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array);
        if (source.Key != "barkod_bilgisi_getir" ||
            !source.Value.TryGetProperty("rows", out var rowArray)) return null;
        var items = rowArray.EnumerateArray()
            .Where(row => row.ValueKind == JsonValueKind.Object)
            .Select(row =>
            {
                var values = row.EnumerateObject().ToArray();
                var title = values.FirstOrDefault(value => value.Name is "name" or "medicine_name").Value;
                var barcode = values.FirstOrDefault(value => value.Name == "barcode").Value;
                var details = values.Where(value => value.Name is not ("name" or "medicine_name" or "barcode"))
                    .Select(value => $"{WorkflowData.Label(value.Name)}: {(value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.GetRawText())}");
                return new AiWorkflowContentItem(
                    title.ValueKind == JsonValueKind.String ? title.GetString() ?? "Ürün bilgisi" : "Ürün bilgisi",
                    barcode.ValueKind == JsonValueKind.String ? barcode.GetString() : null,
                    Detail: string.Join(Environment.NewLine, details));
            }).ToArray();
        return items.Length == 0 ? null : new AiWorkflowContentPresentation(
            AiWorkflowContentKind.InformationCards, "Ürün Bilgileri", items);
    }

    private async Task<AiWorkflowTablePresentation?> BuildImplicitTableAsync(
        AiWorkflowRunState state,
        CancellationToken cancellationToken)
    {
        var source = state.Outputs.Reverse().FirstOrDefault(pair =>
            pair.Value.TryGetProperty("rows", out var rows) &&
            rows.ValueKind == JsonValueKind.Array);
        if (string.IsNullOrWhiteSpace(source.Key)) return null;
        var workflow = registry.Get("tabloda_goster") ?? new DisplayTableWorkflow();
        var title = source.Key switch
        {
            "hareketleri_gor" => "Karekod Hareketleri",
            "satislari_gor" => "Satış Kayıtları",
            "barkod_bilgisi_getir" => "Ürün Bilgileri",
            "satis_hasilati_ozeti" => "Satış Hasılatı",
            "fiyat_gecmisini_gor" => "Fiyat Geçmişi",
            "stok_analizini_gor" => "Stok Analizi",
            _ => "İş Akışı Sonuçları"
        };
        var context = new AiWorkflowContext(
            state.Request.UserMessage,
            state.Plan.CorrectedRequest,
            state.Request.ApplicationContext,
            state.Outputs,
            state.Trace,
            state.Plan.MedicineNames,
            RequestContext: state.Request,
            ToolExecutor: toolExecutor,
            Parameters: new Dictionary<string, string> { ["title"] = title });
        var execution = await ExecuteStepAsync(workflow, context, cancellationToken);
        state.Trace.Add(execution.Trace);
        return execution.Result.Status == AiWorkflowStepStatus.Completed
            ? execution.Result.Table
            : null;
    }

    private static string BuildUserMessage(AiWorkflowRunState state)
    {
        var messages = state.Trace
            .Where(entry => entry.Status == AiWorkflowStepStatus.Completed &&
                            entry.WorkflowName is not ("debug_workflow" or "tabloda_goster") &&
                            !entry.WorkflowName.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal))
            .Select(entry => entry.Message.Trim())
            .Where(message => message.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (messages.Length > 0) return string.Join(Environment.NewLine, messages);
        return state.Trace.LastOrDefault(entry => entry.WorkflowName != "debug_workflow")?.Message
               ?? "İş akışı tamamlandı.";
    }

    private static void AddModuleActions(
        AiWorkflowRunState state,
        IReadOnlyList<AiWorkflowModuleAction>? actions)
    {
        if (actions is null) return;
        foreach (var action in actions)
            if (state.ModuleActions.All(existing => existing.Id != action.Id))
                state.ModuleActions.Add(action);
    }

    private string DebugSummary(AiWorkflowRunState state, string finalEvent)
    {
        var correction = state.Plan.CorrectionConfidence > 0.90 &&
                         !string.Equals(state.Request.UserMessage, state.Plan.CorrectedRequest, StringComparison.Ordinal)
            ? $"{state.Request.UserMessage} -> {state.Plan.CorrectedRequest} ({state.Plan.CorrectionConfidence:0.00})"
            : "none";
        var lines = new List<string>
        {
            state.RouterResultPresented
                ? "Model calls for this continuation: 0 (saved router plan reused)"
                : $"Model calls for this result: 1 ({state.Model})",
            $"Explosive: {state.Plan.IsExplosive}",
            $"Spelling correction: {correction}",
            $"Medicine names: [{string.Join(", ", state.Plan.MedicineNames)}]",
            $"Queue: [{string.Join(", ", state.Queue.Select(name => registry.Get(name)?.Definition.DisplayFunctionName ?? name))}]"
        };
        lines.AddRange(state.Trace.Select(entry =>
            $"Workflow: {registry.Get(entry.WorkflowName)?.Definition.DisplayFunctionName ?? entry.DisplayName}, status={entry.Status}, duration={entry.DurationMilliseconds} ms" +
            (entry.Status is AiWorkflowStepStatus.Failed or AiWorkflowStepStatus.MissingTool
                ? $", detail={entry.Message}"
                : string.Empty)));
        lines.Add(finalEvent);
        return string.Join(Environment.NewLine, lines);
    }

    private static AiWorkflowPlan ParsePlan(
        AiModelResponse response,
        string originalRequest,
        IAiWorkflowRegistry registry)
    {
        using var document = JsonDocument.Parse(response.Text);
        var root = document.RootElement;
        var answerElement = root.GetProperty("answer");
        var reasonElement = root.GetProperty("explosion_reason");
        var queue = root.GetProperty("workflow_queue").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        if (queue.Length > 20 || queue.Distinct(StringComparer.Ordinal).Count() != queue.Length ||
            queue.Any(name => registry.Get(name)?.Definition.RouterVisible != true))
            throw new InvalidOperationException("The plan contains an unknown, repeated, or excessive workflow step.");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("workflow_inputs").EnumerateArray())
        {
            var workflowName = item.GetProperty("workflow_name").GetString() ?? string.Empty;
            var definition = registry.RouterDefinitions.FirstOrDefault(value => value.Name == workflowName);
            if (definition is null || !queue.Contains(workflowName, StringComparer.Ordinal) || inputs.ContainsKey(workflowName))
                throw new InvalidOperationException("Workflow inputs do not match the planned queue.");
            var allowed = definition.Parameters?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
            var arguments = item.GetProperty("arguments").EnumerateArray()
                .Select(argument => new
                {
                    Name = argument.GetProperty("name").GetString() ?? string.Empty,
                    Value = argument.GetProperty("value").GetString() ?? string.Empty
                })
                .ToArray();
            if (arguments.Any(argument => !allowed.Contains(argument.Name)) ||
                arguments.Select(argument => argument.Name).Distinct(StringComparer.Ordinal).Count() != arguments.Length)
                throw new InvalidOperationException($"Invalid or repeated parameter for workflow '{workflowName}'.");
            inputs[workflowName] = arguments.ToDictionary(argument => argument.Name, argument => argument.Value, StringComparer.Ordinal);
        }
        return new AiWorkflowPlan(
            answerElement.ValueKind == JsonValueKind.Null ? null : answerElement.GetString(),
            root.GetProperty("explosive").GetBoolean(),
            reasonElement.ValueKind == JsonValueKind.Null ? null : reasonElement.GetString(),
            root.GetProperty("corrected_request").GetString() ?? originalRequest,
            root.GetProperty("correction_confidence").GetDouble(),
            root.GetProperty("medicine_names").EnumerateArray()
                .Select(item => item.GetString()?.Trim() ?? string.Empty)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            root.TryGetProperty("use_remembered_medicine", out var remembered) &&
            remembered.ValueKind == JsonValueKind.True,
            queue,
            inputs,
            response.Usage,
            root.TryGetProperty("needs_advanced_planning", out var advanced) && advanced.ValueKind == JsonValueKind.True,
            root.TryGetProperty("advanced_planning_reason", out var advancedReason) && advancedReason.ValueKind == JsonValueKind.String
                ? advancedReason.GetString()
                : null);
    }

    private static JsonElement RouterSchema(IReadOnlyList<AiWorkflowDefinition> definitions) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["answer"] = new { type = new[] { "string", "null" } },
                ["explosive"] = new { type = "boolean" },
                ["explosion_reason"] = new { type = new[] { "string", "null" } },
                ["corrected_request"] = new { type = "string" },
                ["correction_confidence"] = new { type = "number", minimum = 0, maximum = 1 },
                ["medicine_names"] = new
                {
                    type = "array",
                    items = new { type = "string" },
                    maxItems = 20
                },
                ["use_remembered_medicine"] = new { type = "boolean" },
                ["needs_advanced_planning"] = new { type = "boolean" },
                ["advanced_planning_reason"] = new { type = new[] { "string", "null" } },
                ["workflow_queue"] = new
                {
                    type = "array",
                    maxItems = 20,
                    items = new
                    {
                        type = "string",
                        @enum = definitions.Select(definition => definition.Name).ToArray()
                    }
                },
                ["workflow_inputs"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["workflow_name"] = new
                            {
                                type = "string"
                            },
                            ["arguments"] = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["name"] = new { type = "string" },
                                        ["value"] = new { type = "string" }
                                    },
                                    required = new[] { "name", "value" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "workflow_name", "arguments" },
                        additionalProperties = false
                    }
                }
            },
            required = new[]
            {
                "answer", "explosive", "explosion_reason", "corrected_request",
                "correction_confidence", "medicine_names", "use_remembered_medicine",
                "needs_advanced_planning", "advanced_planning_reason",
                "workflow_queue", "workflow_inputs"
            },
            additionalProperties = false
        });

    private static AiWorkflowPlan NormalizeMovementPeriod(AiWorkflowPlan plan)
    {
        if (!plan.WorkflowInputs.TryGetValue("hareketleri_gor", out var original)) return plan;
        var values = new Dictionary<string, string>(original, StringComparer.Ordinal);
        var nearestRequested = string.Equals(
            values.GetValueOrDefault("nearest"), "true", StringComparison.OrdinalIgnoreCase);
        if (nearestRequested && !HasNearestMovementCue(plan.CorrectedRequest)) values.Remove("nearest");
        var hasPeriod = values.Keys.Any(key => key is "start_date" or "end_date" or "period_days" or "period_months") ||
                        string.Equals(values.GetValueOrDefault("nearest"), "true", StringComparison.OrdinalIgnoreCase);
        if (!hasPeriod)
        {
            values["period_days"] = "30";
            values["_period_default"] = "true";
        }
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(plan.WorkflowInputs, StringComparer.Ordinal)
        {
            ["hareketleri_gor"] = values
        };
        return plan with { WorkflowInputs = inputs };
    }

    private AiWorkflowPlan NormalizeSalesIntent(AiWorkflowPlan plan)
    {
        var salesWorkflows = new[] { "satislari_gor", "satis_hasilati_ozeti", "hasta_satis_iceriklerini_gor" };
        if (!plan.Queue.Any(name => salesWorkflows.Contains(name, StringComparer.Ordinal))) return plan;

        var queue = plan.Queue.ToList();
        var inputs = plan.WorkflowInputs.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var allHistoryRequested = HasAllHistorySalesCue(plan.CorrectedRequest);
        var normalizedRequest = NormalizeIntent(plan.CorrectedRequest);
        var patientQuery = plan.WorkflowInputs.GetValueOrDefault("hasta_eslestir")?.GetValueOrDefault("query")
            ?? ExtractPatientQuery(plan.CorrectedRequest);
        var latestRecordRequested = normalizedRequest.Contains("en son", StringComparison.Ordinal) ||
                                    normalizedRequest.Contains("son alisveris", StringComparison.Ordinal);
        var latestRecordLimit = ExtractRequestedLatestCount(normalizedRequest);
        if (patientQuery is not null && HasSalesContentsCue(normalizedRequest) &&
            registry.Get("hasta_satis_iceriklerini_gor") is not null && queue.Contains("satislari_gor", StringComparer.Ordinal))
        {
            var index = queue.IndexOf("satislari_gor");
            queue[index] = "hasta_satis_iceriklerini_gor";
            if (inputs.Remove("satislari_gor", out var salesInput))
                inputs["hasta_satis_iceriklerini_gor"] = salesInput;
        }
        foreach (var workflowName in salesWorkflows.Where(queue.Contains))
        {
            var values = new Dictionary<string, string>(
                inputs.GetValueOrDefault(workflowName) ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            if (normalizedRequest.Contains("perakende", StringComparison.Ordinal) &&
                !normalizedRequest.Contains("receteli", StringComparison.Ordinal) && !values.ContainsKey("sale_types"))
                values["sale_types"] = "retail";
            else if (normalizedRequest.Contains("receteli", StringComparison.Ordinal) &&
                     !normalizedRequest.Contains("perakende", StringComparison.Ordinal) && !values.ContainsKey("sale_types"))
                values["sale_types"] = "prescription";
            if (values.TryGetValue("statuses", out var statusText))
            {
                var allowedStatuses = new HashSet<string>(["pending", "medula_reported", "completed", "reconciliation_required", "cancelled"], StringComparer.Ordinal);
                var statuses = statusText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeIntent).Where(allowedStatuses.Contains).Distinct(StringComparer.Ordinal).ToArray();
                // Never broaden an invalid status filter into an unfiltered query.
                // Leave invalid values for the tool's validation boundary to reject.
                if (statuses.Length == statusText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)
                    values["statuses"] = string.Join(',', statuses);
            }
            if (workflowName == "hasta_satis_iceriklerini_gor" && normalizedRequest.Contains("iade", StringComparison.Ordinal))
            {
                values["include_refunds"] = "true";
                values["refunds_only"] = "true";
            }
            if (workflowName == "satislari_gor" && values.TryGetValue("sort_direction", out var direction))
                values["sort_direction"] = NormalizeIntent(direction) is "asc" or "ascending" or "artan" or "oldest_first"
                    ? "oldest_first"
                    : "newest_first";
            // Clock bounds and row limits are part of the model's explicit filter contract.
            // A bare HH:mm match cannot tell "before" from "after" (and can match a date).
            var hasExplicitPeriod = values.Keys.Any(key => key is "start_date" or "end_date" or "period_days" or "period_months" or "calendar_period");
            if (allHistoryRequested && !hasExplicitPeriod) values["all_history"] = "true";
            if (latestRecordRequested && !hasExplicitPeriod)
            {
                values["all_history"] = "true";
                values["limit"] = latestRecordLimit.ToString(CultureInfo.InvariantCulture);
            }
            ApplyCalendarRange(values, normalizedRequest);
            if (normalizedRequest.Contains("kac tane", StringComparison.Ordinal) ||
                normalizedRequest.Contains("kutu", StringComparison.Ordinal) ||
                normalizedRequest.Contains("urun adedi", StringComparison.Ordinal))
            {
                values["metric"] = "quantity";
                if (!normalizedRequest.Contains("iade", StringComparison.Ordinal))
                    values["include_refunds"] = "false";
            }
            else if (normalizedRequest.Contains("kac satis", StringComparison.Ordinal) ||
                     normalizedRequest.Contains("satis sayisi", StringComparison.Ordinal))
                values["metric"] = "record_count";
            else if (normalizedRequest.Contains("kac tl", StringComparison.Ordinal) ||
                     Regex.IsMatch(normalizedRequest, @"\btoplam\b.*\btl\b", RegexOptions.CultureInvariant))
                values["metric"] = "net_revenue";
            inputs[workflowName] = values;
        }

        if (patientQuery is not null && registry.Get("hasta_eslestir") is not null)
        {
            queue.RemoveAll(name => name == "hasta_eslestir");
            var salesIndex = queue.FindIndex(name => salesWorkflows.Contains(name, StringComparer.Ordinal));
            queue.Insert(Math.Max(0, salesIndex), "hasta_eslestir");
            inputs["hasta_eslestir"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = patientQuery
            };
        }
        return plan with { Queue = queue, WorkflowInputs = inputs };
    }

    private static void ApplyCalendarRange(Dictionary<string, string> values, string normalizedRequest)
    {
        if (values.ContainsKey("calendar_period") || values.ContainsKey("start_date") || values.ContainsKey("end_date")) return;
        DateTime start;
        var end = DateTime.Today;
        if (Regex.IsMatch(normalizedRequest, @"\bbugun(?:ku)?\b", RegexOptions.CultureInvariant))
            start = end;
        else if (Regex.IsMatch(normalizedRequest, @"\bdun(?:ku)?\b", RegexOptions.CultureInvariant))
        {
            start = end.AddDays(-1);
            end = start;
        }
        else if (normalizedRequest.Contains("bu ay", StringComparison.Ordinal))
            start = new DateTime(end.Year, end.Month, 1);
        else if (normalizedRequest.Contains("gecen ay", StringComparison.Ordinal))
        {
            var currentMonth = new DateTime(end.Year, end.Month, 1);
            start = currentMonth.AddMonths(-1);
            end = currentMonth.AddDays(-1);
            if (normalizedRequest.Contains("ilk hafta", StringComparison.Ordinal))
                end = start.AddDays(6);
        }
        else
            return;

        values.Remove("period_days");
        values.Remove("period_months");
        values.Remove("all_history");
        values["start_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        values["end_date"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private AiWorkflowPlan NormalizeDeterministicSalesSummaryIntent(AiWorkflowPlan plan, string originalRequest)
    {
        var text = NormalizeIntent(originalRequest);
        var isSalesLanguage = text.Contains("satis", StringComparison.Ordinal) || text.Contains("iade", StringComparison.Ordinal) ||
                              text.Contains("hasilat", StringComparison.Ordinal);
        if (!isSalesLanguage) return plan;
        if ((text.Contains("nasil", StringComparison.Ordinal) || text.Contains("ne demek", StringComparison.Ordinal)) &&
            (text.Contains("hesap", StringComparison.Ordinal) || text.Contains("ele alin", StringComparison.Ordinal) ||
             text.Contains("nedir", StringComparison.Ordinal)))
            return plan;

        var specializedSalesWorkflows = new HashSet<string>(StringComparer.Ordinal)
        {
            "odeme_dagilimi_analizi", "sepet_analizi", "satis_tutarlilik_kontrolu",
            "iade_analizi", "son_iadeleri_gor", "donemleri_karsilastir",
            "kamu_ucretli_satislari_sirala", "saatlik_satis_analizi", "satis_kohort_analizi",
            "haftalik_iade_orani", "en_yuksek_hasilatli_gun", "hasta_hasilat_yogunlugu",
            "urun_satis_olmayan_aylar", "urun_operasyonel_karsilastirma",
            "sifir_kamu_ucretli_satislar", "hasta_iadelerini_gor",
            "hafta_gunu_satis_analizi", "hafta_gunlerini_karsilastir",
            "urun_satis_siralamalarini_karsilastir", "karlilik_analizi",
            "urun_kar_marji_analizi", "yuksek_iskontolu_satislar",
            "zararina_satis_satirlari", "kamu_hasta_odeme_analizi",
            "abc_hasilat_siniflandirmasi", "hasilat_yogunlugu_analizi"
        };
        if (plan.Queue.Any(specializedSalesWorkflows.Contains))
            return plan;

        var shared = new Dictionary<string, string>(CopySalesPeriodParameters(plan), StringComparer.Ordinal);
        foreach (var arguments in plan.WorkflowInputs.Values)
            foreach (var key in new[] { "group_by", "sort_by", "sort_direction", "metric", "minimum_total", "maximum_total", "user_id", "is_debted" })
                if (arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) shared[key] = value;
        if (shared.TryGetValue("user_id", out var userId))
            shared["user_id"] = userId.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        if (text.Contains("perakende", StringComparison.Ordinal) && !text.Contains("receteli", StringComparison.Ordinal) && !shared.ContainsKey("sale_types")) shared["sale_types"] = "retail";
        else if (text.Contains("receteli", StringComparison.Ordinal) && !text.Contains("perakende", StringComparison.Ordinal) && !shared.ContainsKey("sale_types")) shared["sale_types"] = "prescription";
        if (text.Contains("borca", StringComparison.Ordinal) || text.Contains("veresiye", StringComparison.Ordinal)) shared["is_debted"] = "true";
        var period = Regex.Match(text, @"\bson\s+(?<count>[0-9]{1,4})\s*(?<unit>gun|gunluk|ay|aylik)\b", RegexOptions.CultureInvariant);
        if (period.Success)
            shared[period.Groups["unit"].Value.StartsWith("ay", StringComparison.Ordinal) ? "period_months" : "period_days"] = period.Groups["count"].Value;
        ApplyCalendarRange(shared, text);

        var comparesQuantityAndRevenueRankings = text.Contains("karsilastir", StringComparison.Ordinal) &&
            (text.Contains("en cok kutu", StringComparison.Ordinal) || text.Contains("en fazla kutu", StringComparison.Ordinal) ||
             text.Contains("en cok satilan", StringComparison.Ordinal) || text.Contains("en fazla satilan", StringComparison.Ordinal)) &&
            (text.Contains("en cok hasilat", StringComparison.Ordinal) || text.Contains("en fazla hasilat", StringComparison.Ordinal) ||
             text.Contains("hasilat getiren", StringComparison.Ordinal));
        if (comparesQuantityAndRevenueRankings && registry.Get("urun_satis_siralamalarini_karsilastir") is not null)
        {
            var limits = Regex.Matches(text, @"\b(?<count>[0-9]{1,3})\s+urun\b", RegexOptions.CultureInvariant)
                .Select(match => match.Groups["count"].Value).ToArray();
            shared["limit"] = limits.FirstOrDefault() ?? "15";
            shared.Remove("group_by");
            shared.Remove("sort_by");
            shared.Remove("sort_direction");
            return Rebuild(["urun_satis_siralamalarini_karsilastir", "tabloda_goster"],
                ("urun_satis_siralamalarini_karsilastir", shared),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "Satış Adedi ve Hasılat Sıralamaları",
                    ["color_modes"] = "ranking_type|Sıralama türüne göre|contrast",
                    ["default_color_mode"] = "ranking_type"
                }));
        }

        if (text.Contains("son tamamlanmis ay", StringComparison.Ordinal) &&
            text.Contains("onceki tamamlanmis ay", StringComparison.Ordinal) &&
            registry.Get("donemleri_karsilastir") is not null)
            return Rebuild(["donemleri_karsilastir", "metrik_kartlari_goster"],
                ("donemleri_karsilastir", new Dictionary<string, string>
                {
                    ["period_kind"] = "last_full_month", ["metric"] = "net_revenue", ["include_refunds"] = "true"
                }),
                ("metrik_kartlari_goster", new Dictionary<string, string>
                {
                    ["title"] = "Tamamlanmış Ay Hasılat Karşılaştırması"
                }));

        var exactComparisonBarcodes = ExtractExactBarcodes(originalRequest);
        if (exactComparisonBarcodes.Count >= 2 && text.Contains("karsilastir", StringComparison.Ordinal) &&
            registry.Get("satis_dagilimi_analizi") is not null)
        {
            shared["barcodes"] = string.Join(',', exactComparisonBarcodes);
            shared["group_by"] = "product";
            shared["sort_by"] = text.Contains("kar", StringComparison.Ordinal) ? "profit" : "quantity";
            shared["sort_direction"] = "descending";
            shared["include_refunds"] = "true";
            return Rebuild(["satis_dagilimi_analizi", "tabloda_goster"],
                ("satis_dagilimi_analizi", shared),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "Ürün Satış Karşılaştırması"
                }));
        }

        if (text.Contains("urun", StringComparison.Ordinal) &&
            (text.Contains("en fazla net hasilat", StringComparison.Ordinal) ||
             text.Contains("en cok net hasilat", StringComparison.Ordinal)) &&
            registry.Get("satis_dagilimi_analizi") is not null)
        {
            shared["group_by"] = "product";
            shared["sort_by"] = "net_revenue";
            shared["sort_direction"] = "descending";
            var count = Regex.Match(text, @"\b(?<count>[0-9]{1,3})\s+urun", RegexOptions.CultureInvariant);
            shared["limit"] = count.Success ? count.Groups["count"].Value : "10";
            return Rebuild(["satis_dagilimi_analizi", "tabloda_goster"],
                ("satis_dagilimi_analizi", shared),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "En Yüksek Hasılatlı Ürünler"
                }));
        }

        var comparesRollingAndCalendar = text.Contains("gecen takvim ayi", StringComparison.Ordinal) &&
                                         (text.Contains("fark", StringComparison.Ordinal) || text.Contains("karsilastir", StringComparison.Ordinal));
        if (comparesRollingAndCalendar && registry.Get("donemleri_karsilastir") is not null)
        {
            shared.Remove("start_date");
            shared.Remove("end_date");
            shared["period_days"] = ExtractPeriodNumber(text, "gun")?.ToString(CultureInfo.InvariantCulture) ?? "30";
            shared["comparison_kind"] = "rolling_vs_previous_calendar_month";
            shared["metric"] = "net_revenue";
            shared["include_refunds"] = "true";
            return Rebuild(["donemleri_karsilastir", "metrik_kartlari_goster"],
                ("donemleri_karsilastir", shared),
                ("metrik_kartlari_goster", new Dictionary<string, string> { ["title"] = "Son Dönem ve Geçen Takvim Ayı" }));
        }

        var lifecycle = (text.Contains("bekleyen", StringComparison.Ordinal) || text.Contains("tamamlanan", StringComparison.Ordinal)) &&
                        text.Contains("satis", StringComparison.Ordinal) && text.Contains("sayi", StringComparison.Ordinal);
        if (lifecycle && registry.Get("satis_durum_ozeti") is not null)
        {
            var statuses = new List<string>();
            if (text.Contains("bekleyen", StringComparison.Ordinal)) statuses.Add("pending");
            if (text.Contains("tamamlanan", StringComparison.Ordinal)) statuses.Add("completed");
            shared["statuses"] = string.Join(',', statuses);
            return Rebuild(["satis_durum_ozeti", "tabloda_goster"],
                ("satis_durum_ozeti", shared),
                ("tabloda_goster", new Dictionary<string, string> { ["title"] = "Satış Durumları" }));
        }

        var asksTransactionCount = text.Contains("satis", StringComparison.Ordinal) &&
                                   (text.Contains("satis sayisi", StringComparison.Ordinal) ||
                                    text.Contains("kac", StringComparison.Ordinal) && text.Contains("islem", StringComparison.Ordinal));
        var asksRefundTotal = text.Contains("iade", StringComparison.Ordinal) &&
                              (text.Contains("tutar", StringComparison.Ordinal) || text.Contains("toplam", StringComparison.Ordinal)) &&
                              !text.Contains("oran", StringComparison.Ordinal);
        var asksInstitutionRevenue = text.Contains("kamu", StringComparison.Ordinal) && text.Contains("hasilat", StringComparison.Ordinal);
        var asksRevenue = text.Contains("hasilat", StringComparison.Ordinal) || text.Contains("kac tl", StringComparison.Ordinal) ||
                          text.Contains("brut satis", StringComparison.Ordinal) && text.Contains("net", StringComparison.Ordinal) ||
                          text.Contains("net tutar", StringComparison.Ordinal) && text.Contains("satis", StringComparison.Ordinal);
        if (!asksTransactionCount && !asksRefundTotal && !asksInstitutionRevenue && !asksRevenue ||
            registry.Get("satis_hasilati_ozeti") is null)
            return plan;

        shared["metric"] = asksRefundTotal ? "refund_total"
            : asksInstitutionRevenue ? "institution_revenue"
            : asksTransactionCount ? "record_count"
            : "net_revenue";
        shared["include_refunds"] = asksTransactionCount ? "false" : "true";
        if (asksRefundTotal)
        {
            // A refund-total request is a single summary unless the user explicitly asks
            // for a breakdown. Router-proposed grouping/sorting can otherwise leak an
            // unsupported refund_total sort into the aggregation tool.
            shared.Remove("group_by");
            shared.Remove("sort_by");
            shared.Remove("sort_direction");
        }
        var title = asksRefundTotal ? "İade Toplamı"
            : asksInstitutionRevenue ? "Kamu Ücreti Hasılatı"
            : asksTransactionCount ? "Satış İşlem Sayısı"
            : "Satış Hasılatı Özeti";
        var presentation = shared.TryGetValue("group_by", out var groupBy) &&
                           !string.Equals(groupBy, "none", StringComparison.OrdinalIgnoreCase)
            ? "tabloda_goster"
            : "metrik_kartlari_goster";
        return Rebuild(["satis_hasilati_ozeti", presentation],
            ("satis_hasilati_ozeti", shared),
            (presentation, new Dictionary<string, string> { ["title"] = title }));

        AiWorkflowPlan Rebuild(IReadOnlyList<string> queue, params (string Name, Dictionary<string, string> Values)[] values)
        {
            var resolvers = plan.Queue.Where(name => registry.Get(name)?.Definition.ProvidesResolvedContext == true).ToArray();
            var validQueue = resolvers.Concat(queue).Where(name => registry.Get(name) is not null).Distinct(StringComparer.Ordinal).ToArray();
            var inputs = values.Where(value => registry.Get(value.Name) is not null)
                .ToDictionary(value => value.Name, value => (IReadOnlyDictionary<string, string>)value.Values, StringComparer.Ordinal);
            foreach (var resolver in resolvers)
                inputs[resolver] = plan.WorkflowInputs.GetValueOrDefault(resolver) ?? new Dictionary<string, string>();
            return plan with
            {
                Answer = null,
                Queue = validQueue,
                WorkflowInputs = inputs,
                NeedsAdvancedPlanning = false,
                AdvancedPlanningReason = null
            };
        }
    }
    private AiWorkflowPlan NormalizePrescriptionReportIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("recete_rapor_durumu_ozeti") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        if (!text.Contains("recete", StringComparison.Ordinal) ||
            !text.Contains("raporlu", StringComparison.Ordinal) && !text.Contains("raporsuz", StringComparison.Ordinal))
            return plan;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameters in plan.WorkflowInputs.Values)
            foreach (var key in new[] { "start_date", "end_date", "period_days", "period_months" })
                if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) values[key] = value;
        var period = Regex.Match(text, @"\bson\s+(?<count>[0-9]{1,4})\s*(?<unit>gun|gunluk|ay|aylik)\b", RegexOptions.CultureInvariant);
        if (period.Success)
            values[period.Groups["unit"].Value.StartsWith("ay", StringComparison.Ordinal) ? "period_months" : "period_days"] = period.Groups["count"].Value;
        ApplyCalendarRange(values, text);
        if (!values.Keys.Any(key => key is "start_date" or "end_date" or "period_days" or "period_months"))
            values["period_days"] = "30";
        values["report_status"] = text.Contains("raporlu", StringComparison.Ordinal) && text.Contains("raporsuz", StringComparison.Ordinal)
            ? "both"
            : text.Contains("raporsuz", StringComparison.Ordinal) ? "unreported" : "reported";

        var queue = new List<string> { "recete_rapor_durumu_ozeti" };
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["recete_rapor_durumu_ozeti"] = values
        };
        if (registry.Get("metrik_kartlari_goster") is not null)
        {
            queue.Add("metrik_kartlari_goster");
            inputs["metrik_kartlari_goster"] = new Dictionary<string, string>
            {
                ["title"] = values["report_status"] switch
                {
                    "reported" => "Raporlu Reçete Sayısı",
                    "unreported" => "Raporsuz Reçete Sayısı",
                    _ => "Reçete Rapor Durumu"
                }
            };
        }
        return plan with
        {
            Answer = null,
            MedicineNames = [],
            UseRememberedMedicine = false,
            Queue = queue,
            WorkflowInputs = inputs,
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };
    }
    private AiWorkflowPlan NormalizeProductPriceComparisonIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("urunleri_karsilastir") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        var barcodes = ExtractExactBarcodes(originalRequest);
        if (barcodes.Count is < 2 or > 10 || !text.Contains("fiyat", StringComparison.Ordinal) ||
            !text.Contains("karsilastir", StringComparison.Ordinal))
            return plan;
        return plan with
        {
            Answer = null, MedicineNames = [], UseRememberedMedicine = false,
            Queue = ["urunleri_karsilastir", "tabloda_goster"],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["urunleri_karsilastir"] = new Dictionary<string, string> { ["barcodes"] = string.Join(',', barcodes) },
                ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = "Ürün Fiyat Karşılaştırması" }
            },
            NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizeProductStockSummaryIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("urun_stok_ozeti") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        var barcodes = ExtractExactBarcodes(originalRequest);
        var stockCue = text.Contains("raf stok", StringComparison.Ordinal) ||
                       text.Contains("yoldaki", StringComparison.Ordinal) ||
                       text.Contains("mevcut stok", StringComparison.Ordinal);
        if (barcodes.Count == 0 || !stockCue) return plan;
        var presentation = barcodes.Count == 1 ? "bilgi_kartlari_goster" : "tabloda_goster";
        return plan with
        {
            Answer = null, MedicineNames = [], UseRememberedMedicine = false,
            Queue = ["urun_stok_ozeti", presentation],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["urun_stok_ozeti"] = new Dictionary<string, string>
                {
                    ["barcodes"] = string.Join(',', barcodes),
                    ["days"] = ExtractPeriodNumber(text, "gun")?.ToString(CultureInfo.InvariantCulture) ?? "30"
                },
                [presentation] = new Dictionary<string, string> { ["title"] = "Ürün ve Stok Özeti" }
            },
            NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizeExactBarcodeInformationIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("barkod_bilgisi_getir") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        if (text.Contains("muadil", StringComparison.Ordinal)) return plan;
        if (Regex.IsMatch(text, @"\b(?:tc|t c|kimlik|hasta)\b", RegexOptions.CultureInvariant) &&
            Regex.IsMatch(originalRequest, @"(?<![0-9])[0-9]{11}(?![0-9])", RegexOptions.CultureInvariant))
            return plan;
        var barcodes = ExtractExactBarcodes(originalRequest);
        if (barcodes.Count == 0) return plan;
        var fields = new List<string>();
        Add("kdv", "vat_rate");
        if (text.Contains("firma", StringComparison.Ordinal) || text.Contains("uretici", StringComparison.Ordinal))
            fields.Add("company");
        if (text.Contains("recete tur", StringComparison.Ordinal)) fields.Add("prescription_type");
        if (text.Contains("etkin madde", StringComparison.Ordinal) || text.Contains("etken madde", StringComparison.Ordinal))
            fields.Add("active_ingredient");
        if (text.Contains("farmasotik form", StringComparison.Ordinal) || text.Contains("ilac form", StringComparison.Ordinal))
            fields.Add("pharmaceutical_form");
        if (text.Contains("piyasa", StringComparison.Ordinal) || text.Contains("aktif", StringComparison.Ordinal) ||
            text.Contains("pasif", StringComparison.Ordinal))
            fields.Add("is_on_market");
        if ((text.Contains("bos", StringComparison.Ordinal) || text.Contains("eksik", StringComparison.Ordinal)) &&
            (text.Contains("alan", StringComparison.Ordinal) || text.Contains("bilgi", StringComparison.Ordinal)))
            fields.Add("missing_fields");
        if (fields.Count == 0) return plan;
        fields.Insert(0, "name");
        var presentation = barcodes.Count == 1 ? "bilgi_kartlari_goster" : "tabloda_goster";
        return plan with
        {
            Answer = null,
            MedicineNames = [],
            UseRememberedMedicine = false,
            Queue = ["barkod_bilgisi_getir", presentation],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["barkod_bilgisi_getir"] = new Dictionary<string, string>
                {
                    ["barcodes"] = string.Join(',', barcodes),
                    ["fields"] = string.Join(',', fields.Distinct(StringComparer.Ordinal))
                },
                [presentation] = new Dictionary<string, string> { ["title"] = "Ürün Bilgileri" }
            },
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };

        void Add(string cue, string field)
        {
            if (text.Contains(cue, StringComparison.Ordinal)) fields.Add(field);
        }
    }

    private AiWorkflowPlan NormalizeMedicineComparisonIntent(AiWorkflowPlan plan, string originalRequest)
    {
        var normalizedRequest = NormalizeIntent(originalRequest);
        if (normalizedRequest.Contains("fiyat", StringComparison.Ordinal) ||
            Regex.IsMatch(normalizedRequest, @"\b(?:pazartesi|sali|carsamba|persembe|cuma|cumartesi|pazar)\b", RegexOptions.CultureInvariant))
            return plan;
        if (registry.Get("ilac_barkod_eslestir_1") is null || registry.Get("ilac_barkod_eslestir_2") is null ||
            registry.Get("satis_dagilimi_analizi") is null || ExtractMedicineComparison(originalRequest) is not { } names)
            return plan;
        var parameters = CopySalesPeriodParameters(plan);
        parameters["group_by"] = "product";
        parameters["sort_by"] = "quantity";
        parameters["sort_direction"] = "descending";
        parameters["include_refunds"] = "true";
        return plan with
        {
            Answer = null, IsExplosive = false, ExplosionReason = null,
            MedicineNames = [], UseRememberedMedicine = false,
            Queue = ["ilac_barkod_eslestir_1", "ilac_barkod_eslestir_2", "satis_dagilimi_analizi", "tabloda_goster"],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["ilac_barkod_eslestir_1"] = new Dictionary<string, string> { ["query"] = names.Left },
                ["ilac_barkod_eslestir_2"] = new Dictionary<string, string> { ["query"] = names.Right },
                ["satis_dagilimi_analizi"] = parameters,
                ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = $"{names.Left} ve {names.Right} Satış Karşılaştırması" }
            },
            NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizeStockIntent(AiWorkflowPlan plan, string originalRequest)
    {
        var text = NormalizeIntent(originalRequest);
        if (text.Contains("satis", StringComparison.Ordinal) || text.Contains("hasilat", StringComparison.Ordinal) ||
            text.Contains("karini", StringComparison.Ordinal) || text.Contains("almaliyim", StringComparison.Ordinal))
            return plan;

        var inventoryMode = text.Contains("pasif", StringComparison.Ordinal) &&
                            (text.Contains("stok", StringComparison.Ordinal) || text.Contains("rafta", StringComparison.Ordinal))
            ? "passive_in_stock"
            : text.Contains("stok degeri", StringComparison.Ordinal) && text.Contains("en yuksek", StringComparison.Ordinal)
                ? "stock_value"
                : text.Contains("negatif", StringComparison.Ordinal) && text.Contains("stok", StringComparison.Ordinal)
                    ? "negative"
                    : text.Contains("sifir", StringComparison.Ordinal) && text.Contains("stok", StringComparison.Ordinal)
                        ? "zero"
                        : null;
        if (inventoryMode is not null && registry.Get("stok_portfoy_analizi") is not null)
        {
            var limitMatch = Regex.Match(text, @"\b(?:ilk|en yuksek)\s+(?<count>[0-9]{1,3})\b", RegexOptions.CultureInvariant);
            var limit = limitMatch.Success ? limitMatch.Groups["count"].Value : "100";
            return plan with
            {
                Answer = null, IsExplosive = false, ExplosionReason = null, MedicineNames = [], UseRememberedMedicine = false,
                Queue = ["stok_portfoy_analizi", "tabloda_goster"],
                WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["stok_portfoy_analizi"] = new Dictionary<string, string>
                    {
                        ["mode"] = inventoryMode,
                        ["only_on_market"] = inventoryMode == "passive_in_stock" ? "false" : "true",
                        ["limit"] = limit
                    },
                    ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = "Stok Portföyü" }
                }, NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
            };
        }

        var shortage = text.Contains("eksik listesi", StringComparison.Ordinal) || text.Contains("stogu olmayan", StringComparison.Ordinal);
        if (shortage && registry.Get("stok_portfoy_analizi") is not null)
            return plan with
            {
                Answer = null, IsExplosive = false, ExplosionReason = null, MedicineNames = [], UseRememberedMedicine = false,
                Queue = ["stok_portfoy_analizi", "tabloda_goster"],
                WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["stok_portfoy_analizi"] = new Dictionary<string, string> { ["mode"] = "shortage", ["only_on_market"] = "true" },
                    ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = "Eksik Stok Listesi" }
                }, NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
            };

        var medicineName = ExtractStockMedicineName(originalRequest);
        var shortageQuantityCue = Regex.IsMatch(text,
            @"\bkac\b.*\b(?:eksik|eksigi|eksigim|eksigimiz|eksiginiz)\b",
            RegexOptions.CultureInvariant);
        var stockCue = medicineName is not null ||
                       shortageQuantityCue && plan.MedicineNames.Count > 0 ||
                       Regex.IsMatch(text, @"\bstokta\s+kac(?:\s+tane)?\s+var\b", RegexOptions.CultureInvariant) ||
                       text.Contains("stok miktari", StringComparison.Ordinal) || text.Contains("mevcut stok", StringComparison.Ordinal) ||
                       text.Contains("stok kac gun", StringComparison.Ordinal);
        if (!stockCue || registry.Get("stok_analizini_gor") is null) return plan;

        var medicineNames = medicineName is not null ? new[] { medicineName } : plan.MedicineNames.ToArray();
        var hasNamedMedicine = medicineNames.Length > 0;
        var queue = new List<string>();
        if (hasNamedMedicine && registry.Get("ilac_barkod_eslestir") is not null) queue.Add("ilac_barkod_eslestir");
        queue.Add("stok_analizini_gor");
        queue.Add("bilgi_kartlari_goster");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["stok_analizini_gor"] = new Dictionary<string, string>(),
            ["bilgi_kartlari_goster"] = new Dictionary<string, string> { ["title"] = "Stok Bilgisi" }
        };
        if (hasNamedMedicine) inputs["ilac_barkod_eslestir"] = new Dictionary<string, string>();
        return plan with
        {
            Answer = null, IsExplosive = false, ExplosionReason = null,
            MedicineNames = medicineNames, UseRememberedMedicine = !hasNamedMedicine,
            Queue = queue, WorkflowInputs = inputs, NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizeLatestRefundIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("son_iadeleri_gor") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        if (!Regex.IsMatch(text, @"\ben son(?:\s+yapilan)?\s+(?:[0-9]+\s+)?iade(?:nin)?", RegexOptions.CultureInvariant))
            return plan;
        var match = Regex.Match(text, @"\ben son(?:\s+yapilan)?\s+(?<count>[0-9]{1,3})\s+iade", RegexOptions.CultureInvariant);
        var count = match.Success && int.TryParse(match.Groups["count"].Value, out var parsed)
            ? Math.Clamp(parsed, 1, 200)
            : Regex.IsMatch(text, @"\ben son(?:\s+yapilan)?\s+iade(?:nin)?\b", RegexOptions.CultureInvariant) ? 1 : 10;
        return plan with
        {
            Answer = null, IsExplosive = false, ExplosionReason = null, MedicineNames = [], UseRememberedMedicine = false,
            Queue = ["son_iadeleri_gor", "tabloda_goster"],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["son_iadeleri_gor"] = new Dictionary<string, string> { ["limit"] = count.ToString(CultureInfo.InvariantCulture) },
                ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = $"En Son {count} İade" }
            }, NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizeDiversityAnalysisIntent(AiWorkflowPlan plan, string originalRequest)
    {
        var text = NormalizeIntent(originalRequest);
        string? workflow = null;
        string? kind = null;
        string title = "Analiz Sonuçları";
        var maximumPatientQuery = ExtractPatientQuery(originalRequest);
        var exactSaleReference = Regex.Match(originalRequest,
            @"(?<![A-Za-z0-9])(?<reference>[PGR][0-9]+)(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (exactSaleReference.Success &&
            (exactSaleReference.Groups["reference"].Value.StartsWith("R", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("barkoda gore grupla", StringComparison.Ordinal)) &&
            registry.Get("satis_detayi_getir") is not null)
            return Rebuild(plan, ["satis_detayi_getir", "tabloda_goster"],
                ("satis_detayi_getir", new Dictionary<string, string>
                {
                    ["reference_code"] = exactSaleReference.Groups["reference"].Value.ToUpperInvariant()
                }),
                ("tabloda_goster", new Dictionary<string, string> { ["title"] = "Satış Ürünleri" }));
        if (text.Contains("receteli satis oran", StringComparison.Ordinal) &&
            text.Contains("en dusuk", StringComparison.Ordinal) &&
            registry.Get("receteli_satis_payi_analizi") is not null)
            return Rebuild(plan, ["receteli_satis_payi_analizi", "tabloda_goster"],
                ("receteli_satis_payi_analizi", new Dictionary<string, string>
                {
                    ["all_history"] = "true", ["sort_direction"] = "ascending",
                    ["only_on_market"] = text.Contains("aktif", StringComparison.Ordinal) ? "true" : "false",
                    ["limit"] = FirstNumber(text, 10).ToString(CultureInfo.InvariantCulture)
                }),
                ("tabloda_goster", new Dictionary<string, string> { ["title"] = "Reçeteli Satış Oranı En Düşük Ürünler" }));
        if (text.Contains("bu hafta", StringComparison.Ordinal) &&
            text.Contains("pazartesi", StringComparison.Ordinal) &&
            text.Contains("sali", StringComparison.Ordinal) &&
            text.Contains("karsilastir", StringComparison.Ordinal) &&
            registry.Get("hafta_gunlerini_karsilastir") is not null)
            return Rebuild(plan, ["hafta_gunlerini_karsilastir", "tabloda_goster"],
                ("hafta_gunlerini_karsilastir", new Dictionary<string, string>()),
                ("tabloda_goster", new Dictionary<string, string> { ["title"] = "Pazartesi ve Sali Satis Karsilastirmasi" }));
        if ((text.Contains("kamu ucret", StringComparison.Ordinal) || text.Contains("kurum ucret", StringComparison.Ordinal)) &&
            text.Contains("satis", StringComparison.Ordinal) &&
            (text.Contains("en yuksek", StringComparison.Ordinal) || text.Contains("en fazla", StringComparison.Ordinal)) &&
            registry.Get("kamu_ucretli_satislari_sirala") is not null)
            return Rebuild(plan, ["kamu_ucretli_satislari_sirala", "tabloda_goster"],
                ("kamu_ucretli_satislari_sirala", new Dictionary<string, string>
                {
                    ["limit"] = FirstNumber(text, 15).ToString(CultureInfo.InvariantCulture)
                }),
                ("tabloda_goster", new Dictionary<string, string> { ["title"] = "Kamu Ucreti En Yuksek Satislar" }));
        if (maximumPatientQuery is not null &&
            text.Contains("en yuksek tutarli", StringComparison.Ordinal) &&
            (text.Contains("alisveris", StringComparison.Ordinal) || text.Contains("satis", StringComparison.Ordinal)))
        {
            return plan with
            {
                Answer = null,
                Queue = ["hasta_eslestir", "hasta_satis_iceriklerini_gor", "tabloda_goster"],
                WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["hasta_eslestir"] = new Dictionary<string, string> { ["query"] = maximumPatientQuery },
                    ["hasta_satis_iceriklerini_gor"] = new Dictionary<string, string>
                    {
                        ["all_history"] = "true", ["highest_total_only"] = "true", ["include_refunds"] = "false"
                    },
                    ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = $"{maximumPatientQuery} — En Yüksek Tutarlı Alışveriş" }
                },
                MedicineNames = [], UseRememberedMedicine = false,
                NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
            };
        }

        if ((text.Contains("adresi bos", StringComparison.Ordinal) || text.Contains("adres bilgisi bos", StringComparison.Ordinal) ||
             text.Contains("adresi olmayan", StringComparison.Ordinal) || text.Contains("adres bilgisi olmayan", StringComparison.Ordinal)) &&
            text.Contains("hareket", StringComparison.Ordinal))
            (workflow, kind, title) = ("karekod_tutarlilik_kontrolu", "missing_address", "Hareket Adresi Eksik Karekodlar");
        else if ((text.Contains("yalnizca giris", StringComparison.Ordinal) || text.Contains("sadece giris", StringComparison.Ordinal) ||
                  text.Contains("yalniz giris", StringComparison.Ordinal)) &&
                 (text.Contains("hic cikis", StringComparison.Ordinal) || text.Contains("cikisi olmayan", StringComparison.Ordinal)))
            (workflow, kind, title) = ("karekod_tutarlilik_kontrolu", "entry_without_exit", "Çıkışı Olmayan Karekod Girişleri");
        else if (text.Contains("raf stogu pozitif", StringComparison.Ordinal) &&
                 text.Contains("satis fiyati olmayan", StringComparison.Ordinal))
            (workflow, kind, title) = ("urun_katalog_analizi", "positive_stock_missing_sale_price", "Satış Fiyatı Eksik Stoklu Ürünler");
        else if (text.Contains("stokta", StringComparison.Ordinal) && text.Contains("satilmayan", StringComparison.Ordinal))
            (workflow, kind, title) = ("stok_satis_kesisim_analizi", "dead_stock", "Satılmayan Stoklu Ürünler");
        else if (text.Contains("stoktan yuksek", StringComparison.Ordinal) && (text.Contains("satilan", StringComparison.Ordinal) || text.Contains("satis", StringComparison.Ordinal)))
            (workflow, kind, title) = ("stok_satis_kesisim_analizi", "sold_over_stock", "Satışı Mevcut Stoktan Yüksek Ürünler");
        else if ((text.Contains("stogu olmayan", StringComparison.Ordinal) || text.Contains("raf stogu olmayan", StringComparison.Ordinal) || text.Contains("stokta olmayan", StringComparison.Ordinal)) &&
                 (text.Contains("satilmis", StringComparison.Ordinal) || text.Contains("satilan", StringComparison.Ordinal)))
            (workflow, kind, title) = ("stok_satis_kesisim_analizi", "sold_out_recently", "Yakın Dönemde Satılmış Stoksuz Ürünler");
        else if (text.Contains("urun satiri olmayan", StringComparison.Ordinal) ||
            text.Contains("kalemi olmayan", StringComparison.Ordinal))
            (workflow, kind, title) = ("satis_tutarlilik_kontrolu", "empty_lines", "Ürün Satırı Olmayan Satışlar");
        else if ((text.Contains("odeme", StringComparison.Ordinal) || text.Contains("tahsilat", StringComparison.Ordinal)) &&
                 (text.Contains("uyusm", StringComparison.Ordinal) || text.Contains("eslesm", StringComparison.Ordinal) || text.Contains("farkli", StringComparison.Ordinal)))
            (workflow, kind, title) = ("satis_tutarlilik_kontrolu", "payment_mismatch", "Ödeme ve Toplam Tutarsızlıkları");
        else if (text.Contains("tekrarlanan referans", StringComparison.Ordinal) || text.Contains("mukerrer referans", StringComparison.Ordinal))
            (workflow, kind, title) = ("satis_tutarlilik_kontrolu", "duplicate_reference", "Tekrarlanan Satış Referansları");
        else if ((text.Contains("sifir", StringComparison.Ordinal) || text.Contains("negatif", StringComparison.Ordinal)) &&
                 (text.Contains("net toplam", StringComparison.Ordinal) || text.Contains("net tutar", StringComparison.Ordinal)))
            (workflow, kind, title) = ("satis_tutarlilik_kontrolu", "nonpositive_total", "Sıfır veya Negatif Satış Toplamları");
        else if (text.Contains("iade", StringComparison.Ordinal) &&
                 (text.Contains("orijinal satis", StringComparison.Ordinal) || text.Contains("kaynag", StringComparison.Ordinal) ||
                  text.Contains("urunleriyle", StringComparison.Ordinal)))
            (workflow, kind, title) = ("satis_tutarlilik_kontrolu", "refund_provenance", "İade Kaynakları");
        else if (Regex.IsMatch(originalRequest, @"(?<![A-Za-z0-9])[PG][0-9]+(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                 text.Contains("odeme", StringComparison.Ordinal) &&
                 (text.Contains("toplam", StringComparison.Ordinal) || text.Contains("uyus", StringComparison.Ordinal)))
            (workflow, kind, title) = ("odeme_dagilimi_analizi", "payment_reconciliation", "İşlem Ödeme Mutabakatı");
        else if (text.Contains("iade", StringComparison.Ordinal) && text.Contains("brut satis", StringComparison.Ordinal))
            (workflow, kind, title) = ("odeme_dagilimi_analizi", "refund_rate_by_month", "Aylık İade Tutarı Oranı");
        else if (text.Contains("ayni", StringComparison.Ordinal) && text.Contains("karekod", StringComparison.Ordinal) &&
                 text.Contains("ayni gun", StringComparison.Ordinal) && text.Contains("satis", StringComparison.Ordinal) && text.Contains("iade", StringComparison.Ordinal))
            (workflow, kind, title) = ("satis_tutarlilik_kontrolu", "same_qr_sale_refund_same_day", "Aynı Gün Satış ve İade Gören Karekodlar");
        else if (text.Contains("ortalama sepet", StringComparison.Ordinal) && !text.Contains("hafta", StringComparison.Ordinal) && !text.Contains("hasta", StringComparison.Ordinal))
            (workflow, kind, title) = ("odeme_dagilimi_analizi", "average_basket", "Ortalama Sepet");
        else if (text.Contains("iade oran", StringComparison.Ordinal))
            (workflow, kind, title) = ("odeme_dagilimi_analizi", "refund_rate_by_month", "Aylık İade Oranı");
        else if ((text.Contains("odeme", StringComparison.Ordinal) || text.Contains("tahsilat", StringComparison.Ordinal) ||
                  text.Contains("nakit", StringComparison.Ordinal) && text.Contains("kart", StringComparison.Ordinal)) &&
                 (text.Contains("dagilim", StringComparison.Ordinal) || text.Contains("karsilastir", StringComparison.Ordinal)))
            (workflow, kind, title) = ("odeme_dagilimi_analizi", "payment_summary", "Ödeme Dağılımı");
        else if (text.Contains("satis basina", StringComparison.Ordinal) &&
                 (text.Contains("urun", StringComparison.Ordinal) || text.Contains("kutu", StringComparison.Ordinal)))
            (workflow, kind, title) = ("sepet_analizi", "items_per_sale_by_month", "Satış Başına Ürün");
        else if (text.Contains("hafta ici", StringComparison.Ordinal) && text.Contains("hafta son", StringComparison.Ordinal))
            (workflow, kind, title) = ("sepet_analizi", "weekday_weekend", "Hafta İçi ve Hafta Sonu");
        else if ((text.Contains("indirimli", StringComparison.Ordinal) || text.Contains("iskontolu", StringComparison.Ordinal)) &&
                 (text.Contains("indirimsiz", StringComparison.Ordinal) || text.Contains("iskontosuz", StringComparison.Ordinal)))
            (workflow, kind, title) = ("sepet_analizi", "discount_cohorts", "İndirimli ve İndirimsiz Sepetler");
        else if (text.Contains("her ay", StringComparison.Ordinal) && text.Contains("sat", StringComparison.Ordinal))
            (workflow, kind, title) = ("sepet_analizi", "consistent_products", "Her Ay Satılan Ürünler");
        else if (text.Contains("iade", StringComparison.Ordinal) &&
                 (text.Contains("olmayan", StringComparison.Ordinal) || text.Contains("gormeyen", StringComparison.Ordinal)) &&
                 (text.Contains("urun", StringComparison.Ordinal) || text.Contains("ilac", StringComparison.Ordinal)))
            (workflow, kind, title) = ("sepet_analizi", "no_refund_products", "İadesi Olmayan Ürünler");
        else if (text.Contains("hasta", StringComparison.Ordinal) && text.Contains("ortalama sepet", StringComparison.Ordinal))
            (workflow, kind, title) = ("sepet_analizi", "top_patients_average_basket", "Hasta Ortalama Sepetleri");
        else if (text.Contains("firma", StringComparison.Ordinal) &&
                 (text.Contains("etken madde", StringComparison.Ordinal) || text.Contains("etkin madde", StringComparison.Ordinal)) &&
                 (text.Contains("eksik", StringComparison.Ordinal) || text.Contains("olmayan", StringComparison.Ordinal) || text.Contains("bos", StringComparison.Ordinal)))
            (workflow, kind, title) = ("urun_katalog_analizi", "missing_company_with_ingredient", "Firma Bilgisi Eksik Ürünler");
        else if (text.Contains("mukerrer barkod", StringComparison.Ordinal) || text.Contains("tekrarlanan barkod", StringComparison.Ordinal))
            (workflow, kind, title) = ("urun_katalog_analizi", "duplicate_barcode", "Tekrarlanan Barkodlar");
        else if (text.Contains("firmaya gore", StringComparison.Ordinal) || text.Contains("firma bazinda", StringComparison.Ordinal))
            (workflow, kind, title) = ("urun_katalog_analizi", "company_counts", "Firma Bazında Ürünler");
        else if (text.Contains("stok degeri", StringComparison.Ordinal) && (text.Contains("en dusuk", StringComparison.Ordinal) || text.Contains("dip", StringComparison.Ordinal)))
            (workflow, kind, title) = ("urun_katalog_analizi", "stock_value_bottom", "En Düşük Stok Değerleri");
        else if (Regex.IsMatch(text, @"\bstoku?\s+[0-9]+\s*(?:ile|-)\s*[0-9]+\s+aras", RegexOptions.CultureInvariant))
            (workflow, kind, title) = ("urun_katalog_analizi", "stock_range", "Stok Aralığındaki Ürünler");

        if (workflow is null || kind is null || registry.Get(workflow) is null) return plan;
        var values = new Dictionary<string, string>(CopySalesPeriodParameters(plan), StringComparer.Ordinal);
        values[workflow == "karekod_tutarlilik_kontrolu" ? "audit_kind" : "analysis_kind"] = kind;
        if (workflow == "urun_katalog_analizi" && kind == "missing_company_with_ingredient" &&
            text.Contains("aktif", StringComparison.Ordinal))
            values["only_on_market"] = "true";
        if (workflow == "urun_katalog_analizi" && kind == "company_counts")
        {
            var nameCue = Regex.Match(text, @"\badinda\s+(?<query>.+?)\s+gecen\b", RegexOptions.CultureInvariant);
            if (nameCue.Success) values["search"] = nameCue.Groups["query"].Value.Trim();
            if (text.Contains("aktif", StringComparison.Ordinal)) values["only_on_market"] = "true";
        }
        if (workflow == "sepet_analizi" && kind == "no_refund_products")
        {
            var minimum = Regex.Match(text, @"\ben az\s+(?<count>[0-9]{1,6})\s+(?:kutu|urun)\b", RegexOptions.CultureInvariant);
            if (minimum.Success) values["minimum_quantity"] = minimum.Groups["count"].Value;
        }
        if (workflow == "odeme_dagilimi_analizi" && kind == "payment_summary" &&
            text.Contains("nakit", StringComparison.Ordinal) && text.Contains("kart", StringComparison.Ordinal))
        {
            var paymentTypes = new List<string> { "cash", "card" };
            if (text.Contains("veresiye", StringComparison.Ordinal) || text.Contains("borc", StringComparison.Ordinal))
                paymentTypes.Add("debt");
            values["payment_types"] = string.Join(',', paymentTypes);
            values.Remove("is_debted");
        }
        ApplyCalendarRange(values, text);
        if (!values.ContainsKey("start_date"))
        {
            if (ExtractPeriodNumber(text, "gun") is { } requestedDays)
                values["period_days"] = requestedDays.ToString(CultureInfo.InvariantCulture);
            else if (ExtractPeriodNumber(text, "ay") is { } requestedMonths)
                values["period_months"] = requestedMonths.ToString(CultureInfo.InvariantCulture);
        }
        var range = Regex.Match(text, @"\bstoku?\s+(?<min>[0-9]+)\s*(?:ile|-)\s*(?<max>[0-9]+)\s+aras", RegexOptions.CultureInvariant);
        if (range.Success)
        {
            values["stock_minimum"] = range.Groups["min"].Value;
            values["stock_maximum"] = range.Groups["max"].Value;
        }
        var count = Regex.Match(text, @"\b(?:ilk|en\s+(?:dusuk|yuksek|cok|az))\s+(?<count>[0-9]{1,3})\b", RegexOptions.CultureInvariant);
        if (count.Success) values["limit"] = count.Groups["count"].Value;
        var queue = new List<string>();
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var patientQuery = ExtractPatientQuery(originalRequest);
        var acceptsPatientFilter = workflow is "satis_tutarlilik_kontrolu" or "odeme_dagilimi_analizi" or "sepet_analizi";
        if (acceptsPatientFilter && patientQuery is not null && registry.Get("hasta_eslestir") is not null)
        {
            queue.Add("hasta_eslestir");
            inputs["hasta_eslestir"] = new Dictionary<string, string> { ["query"] = patientQuery };
        }
        queue.Add(workflow);
        queue.Add("tabloda_goster");
        inputs[workflow] = values;
        inputs["tabloda_goster"] = new Dictionary<string, string> { ["title"] = title };
        return plan with
        {
            Answer = null, Queue = queue, WorkflowInputs = inputs,
            MedicineNames = [], UseRememberedMedicine = false,
            NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };

        AiWorkflowPlan Rebuild(AiWorkflowPlan source, IReadOnlyList<string> requestedQueue,
            params (string Name, Dictionary<string, string> Arguments)[] arguments)
        {
            var validQueue = requestedQueue.Where(name => registry.Get(name) is not null).ToArray();
            var rebuiltInputs = arguments
                .Where(item => registry.Get(item.Name) is not null)
                .ToDictionary(item => item.Name,
                    item => (IReadOnlyDictionary<string, string>)item.Arguments, StringComparer.Ordinal);
            return source with
            {
                Answer = null, Queue = validQueue, WorkflowInputs = rebuiltInputs,
                MedicineNames = [], UseRememberedMedicine = false,
                NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
            };
        }

        static int FirstNumber(string value, int fallback)
        {
            var match = Regex.Match(value, @"\b(?<count>[0-9]{1,6})\b", RegexOptions.CultureInvariant);
            return match.Success && int.TryParse(match.Groups["count"].Value, out var number) ? number : fallback;
        }
    }

    private AiWorkflowPlan NormalizeAmbiguousPastMedicationIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("is_akisi_dali_sec") is null ||
            (ExtractPastMedicationPatientQuery(originalRequest) ??
             ExtractGeneralPatientMedicationQuery(originalRequest)) is not { } patient)
            return plan;

        var text = NormalizeIntent(originalRequest);
        var mentionsMedula = text.Contains("medula", StringComparison.Ordinal);
        var hasExplicitMeaning =
            text.Contains("recete", StringComparison.Ordinal) ||
            text.Contains("satis", StringComparison.Ordinal) ||
            text.Contains("ilac listesi", StringComparison.Ordinal) ||
            text.Contains("ilac ekrani", StringComparison.Ordinal) ||
            text.Contains("aktif ilac", StringComparison.Ordinal) ||
            text.Contains("mevcut ilac", StringComparison.Ordinal) ||
            text.Contains("senkronize", StringComparison.Ordinal) ||
            text.Contains("arsiv", StringComparison.Ordinal);
        if (hasExplicitMeaning)
            return plan;

        var requiredWorkflows = new[]
        {
            "hasta_eslestir", "medula_son_receteyi_ac", "medula_receteleri_ara",
            "medula_recete_bilgisi_getir", "medula_hasta_ilac_listesini_getir",
            "medula_hasta_klinik_bilgisi_getir", "hasta_urun_ozeti", "tabloda_goster"
        };
        if (requiredWorkflows.Any(name => registry.Get(name) is null))
            return plan;

        var patientQuery = PatientMatchWorkflow.TurkishUpper(patient);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var startDate = today.AddYears(-20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var allOptions = new object[]
        {
            new
            {
                id = "current_prescription", label = "Aktif Reçetenin İçeriği",
                queue = new[] { "hasta_eslestir", "medula_son_receteyi_ac" },
                inputs = new Dictionary<string, object>
                {
                    ["hasta_eslestir"] = new { query = patientQuery },
                    ["medula_son_receteyi_ac"] = new { }
                }
            },
            new
            {
                id = "previous_prescription", label = "Önceki Reçetenin İçeriği",
                queue = new[] { "hasta_eslestir", "medula_receteleri_ara", "medula_recete_bilgisi_getir" },
                inputs = new Dictionary<string, object>
                {
                    ["hasta_eslestir"] = new { query = patientQuery },
                    ["medula_receteleri_ara"] = new
                    {
                        source = "local", start_date = startDate, end_date = endDate,
                        offset = "1", limit = "1", include_permanently_deleted = "false"
                    },
                    ["medula_recete_bilgisi_getir"] = new { operation = "detail" }
                }
            },
            new
            {
                id = "patient_medication_list", label = "Medula Hasta İlaç Listesi",
                queue = new[] { "hasta_eslestir", "medula_hasta_ilac_listesini_getir", "tabloda_goster" },
                inputs = new Dictionary<string, object>
                {
                    ["hasta_eslestir"] = new { query = patientQuery },
                    ["medula_hasta_ilac_listesini_getir"] = new { },
                    ["tabloda_goster"] = new { title = $"{patient} — Medula Hasta İlaç Listesi" }
                }
            },
            new
            {
                id = "local_sales", label = "Geçmiş Satışlar",
                queue = new[] { "hasta_eslestir", "hasta_urun_ozeti", "tabloda_goster" },
                inputs = new Dictionary<string, object>
                {
                    ["hasta_eslestir"] = new { query = patientQuery },
                    ["hasta_urun_ozeti"] = new { all_history = "true" },
                    ["tabloda_goster"] = new { title = $"{patient} — Geçmiş Satışlar" }
                }
            }
        };
        var medulaOptions = new object[]
        {
            new
            {
                id = "patient_medication_list", label = "Medula Hasta İlaç Listesi (Canlı)",
                queue = new[] { "hasta_eslestir", "medula_hasta_ilac_listesini_getir", "tabloda_goster" },
                inputs = new Dictionary<string, object>
                {
                    ["hasta_eslestir"] = new { query = patientQuery },
                    ["medula_hasta_ilac_listesini_getir"] = new { },
                    ["tabloda_goster"] = new { title = $"{patient} — Medula Hasta İlaç Listesi" }
                }
            },
            new
            {
                id = "synchronized_medication_history", label = "Medula İlaç Geçmişi (Senkronize)",
                queue = new[] { "hasta_eslestir", "medula_hasta_klinik_bilgisi_getir", "tabloda_goster" },
                inputs = new Dictionary<string, object>
                {
                    ["hasta_eslestir"] = new { query = patientQuery },
                    ["medula_hasta_klinik_bilgisi_getir"] = new { operation = "medication_history" },
                    ["tabloda_goster"] = new { title = $"{patient} — Medula İlaç Geçmişi" }
                }
            }
        };
        var options = JsonSerializer.Serialize(mentionsMedula ? medulaOptions : allOptions);
        return plan with
        {
            Answer = null,
            Queue = ["is_akisi_dali_sec"],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["is_akisi_dali_sec"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["question"] = mentionsMedula
                        ? $"{patient} için Medula'dan hangi ilaç bilgisi getirilsin?"
                        : $"{patient} için hangi ilaç bilgisi gösterilsin?",
                    ["options_json"] = options
                }
            },
            MedicineNames = [],
            UseRememberedMedicine = false,
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizePreviousContextIntent(AiWorkflowPlan plan, string originalRequest)
    {
        var text = NormalizeIntent(originalRequest);
        if ((text.Contains("ayni medula ilac sorgu", StringComparison.Ordinal) ||
             (text.Contains("bu hasta", StringComparison.Ordinal) &&
              (text.Contains("ilac gecmis", StringComparison.Ordinal) || text.Contains("gecmis ilac", StringComparison.Ordinal)))) &&
            registry.Get("onceki_hasta_bilgisini_getir") is not null)
        {
            var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["medula_hasta_klinik_bilgisi_getir"] = new Dictionary<string, string> { ["operation"] = "medication_history" },
                ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = "Medula İlaç Geçmişi" }
            };
            return plan with
            {
                Answer = null,
                Queue = ["onceki_hasta_bilgisini_getir", "medula_hasta_klinik_bilgisi_getir", "tabloda_goster"],
                WorkflowInputs = inputs,
                NeedsAdvancedPlanning = false,
                AdvancedPlanningReason = null
            };
        }

        if (text.Contains("bu hasta", StringComparison.Ordinal) &&
            (text.Contains("medula ilac liste", StringComparison.Ordinal) || text.Contains("medula ilac ekran", StringComparison.Ordinal)) &&
            registry.Get("onceki_hasta_bilgisini_getir") is not null &&
            registry.Get("medula_hasta_ilac_listesini_getir") is not null)
        {
            return plan with
            {
                Answer = null,
                Queue = ["onceki_hasta_bilgisini_getir", "medula_hasta_ilac_listesini_getir", "tabloda_goster"],
                WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["medula_hasta_ilac_listesini_getir"] = new Dictionary<string, string>(),
                    ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = "Medula Hasta İlaç Listesi" }
                },
                NeedsAdvancedPlanning = false,
                AdvancedPlanningReason = null
            };
        }

        var currentPrescription = Regex.IsMatch(text,
            @"\b(?:bu|ayni)\s+recete(?:nin|deki|yi|yi)?\b", RegexOptions.CultureInvariant);
        if (currentPrescription && registry.Get("onceki_recete_bilgisini_getir") is not null)
        {
            var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["medula_recete_bilgisi_getir"] = new Dictionary<string, string> { ["operation"] = "detail" }
            };
            return plan with
            {
                Answer = null,
                Queue = ["onceki_recete_bilgisini_getir", "medula_recete_bilgisi_getir"],
                WorkflowInputs = inputs,
                NeedsAdvancedPlanning = false,
                AdvancedPlanningReason = null
            };
        }

        if (Regex.IsMatch(text, @"\b(?:bu|ayni)\s+ilac(?:in|larin)?\b", RegexOptions.CultureInvariant) &&
            registry.Get("onceki_ilac_bilgisini_getir") is not null &&
            plan.Queue.Any(name => name.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal)))
        {
            var queue = plan.Queue.Select(name => name.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal)
                    ? "onceki_ilac_bilgisini_getir"
                    : name)
                .Distinct(StringComparer.Ordinal).ToArray();
            return plan with { Queue = queue };
        }
        return plan;
    }

    private AiWorkflowPlan NormalizeMedicineFilteredLatestPrescriptionIntent(
        AiWorkflowPlan plan,
        string originalRequest)
    {
        if (registry.Get("medula_son_receteyi_ac") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        var isLatestSoldPrescription = text.Contains("recete", StringComparison.Ordinal) &&
                                       text.Contains("en son", StringComparison.Ordinal) &&
                                       Regex.IsMatch(text, @"\bsat\p{L}*\b", RegexOptions.CultureInvariant);
        if (!isLatestSoldPrescription) return plan;
        // A filtered search followed by prescription detail is already a valid
        // composition. Replacing it would discard search-specific constraints.
        if (plan.Queue.Contains("medula_recete_bilgisi_getir", StringComparer.Ordinal) &&
            plan.Queue.Any(name => name is "satislari_gor" or "medula_receteleri_ara" or "medula_hasta_recetelerini_getir"))
            return plan;

        var barcodes = ExtractExactBarcodes(originalRequest);
        var medicineNames = plan.MedicineNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (medicineNames.Length == 0 && barcodes.Count == 0)
        {
            var match = Regex.Match(originalRequest,
                @"^(?<medicine>.+?)\s+en\s+son\s+sat\p{L}*\s+reçete\p{L}*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && !Regex.IsMatch(NormalizeIntent(match.Groups["medicine"].Value),
                    @"\b(?:hasta|hastanin|gecen|bu|son|ay|hafta|gun|yil|tarih|icin)\b|['’](?:a|e|in|nin)\b",
                    RegexOptions.CultureInvariant))
            {
                var medicine = match.Groups["medicine"].Value.Trim();
                medicine = Regex.Replace(medicine,
                    @"\s+ila[cç](?:[ıiuü]n(?:dan|den)?|[ıiuü])?$", string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
                // Only remove an explicit apostrophe suffix. Removing every trailing vowel
                // corrupts actual product names; the catalogue matcher handles unseparated forms.
                medicine = Regex.Replace(medicine,
                    @"['’]\s*y?[ıiuü]$", string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
                if (medicine.Length >= 2) medicineNames = [medicine];
            }
        }
        if (medicineNames.Length == 0 && barcodes.Count == 0) return plan;

        var queue = plan.Queue.Where(name => name == "hasta_eslestir" ||
            name.StartsWith("onceki_", StringComparison.Ordinal) &&
            registry.Get(name)?.Definition.ProvidesResolvedContext == true).ToList();
        var inputs = queue.ToDictionary(name => name,
            name => plan.WorkflowInputs.GetValueOrDefault(name) ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (barcodes.Count == 0)
        {
            if (registry.Get("ilac_barkod_eslestir") is null) return plan;
            queue.Add("ilac_barkod_eslestir");
            inputs["ilac_barkod_eslestir"] = new Dictionary<string, string>();
        }
        queue.Add("medula_son_receteyi_ac");
        var latestInputs = new Dictionary<string, string>(
            plan.WorkflowInputs.GetValueOrDefault("medula_son_receteyi_ac") ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (barcodes.Count > 0) latestInputs["barcodes"] = string.Join(',', barcodes);
        inputs["medula_son_receteyi_ac"] = latestInputs;
        return plan with
        {
            Answer = null,
            IsExplosive = false,
            ExplosionReason = null,
            MedicineNames = medicineNames,
            UseRememberedMedicine = false,
            Queue = queue,
            WorkflowInputs = inputs,
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizeMedulaIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (plan.Queue.Count > 0 && plan.Queue[0] == "is_akisi_dali_sec")
            return plan;
        // An already scoped latest-prescription plan must not be replaced by an unfiltered
        // "latest" shortcut. Its medicine, patient and date constraints are independent.
        if (plan.Queue.Contains("medula_son_receteyi_ac", StringComparer.Ordinal))
            return plan;
        var text = NormalizeIntent(originalRequest);
        var explicitlyMedula = text.Contains("medula", StringComparison.Ordinal) ||
                               text.Contains("e-recete", StringComparison.Ordinal);
        var mentionsPrescriptionRecord = Regex.IsMatch(
            text, @"\brecete(?!li)[a-z]*\b", RegexOptions.CultureInvariant);
        var mentionsMedula = explicitlyMedula ||
                             mentionsPrescriptionRecord &&
                             Regex.IsMatch(text, @"\b(?:ac|goster|getir|detay|ilaclari|uyari)\b", RegexOptions.CultureInvariant);
        var tc = Regex.Match(originalRequest, @"(?<![0-9])(?<tc>[0-9]{11})(?![0-9])",
            RegexOptions.CultureInvariant).Groups["tc"].Value;
        var patientName = ExtractMedulaPatientQuery(originalRequest);
        var protectedLocalWorkflow = plan.Queue.Any(name => name is
            "recete_rapor_durumu_ozeti" or "kamu_hasta_odeme_analizi" or
            "barkod_bilgisi_getir" or "satis_hasilati_ozeti" or "satis_dagilimi_analizi");
        if (!explicitlyMedula && protectedLocalWorkflow)
            return plan;

        AiWorkflowPlan Build(
            IReadOnlyList<string> queue,
            params (string Name, IReadOnlyDictionary<string, string> Values)[] values)
        {
            var valid = queue.Where(name => registry.Get(name) is not null).ToList();
            var inputs = values.Where(value => registry.Get(value.Name) is not null)
                .ToDictionary(value => value.Name, value => value.Values, StringComparer.Ordinal);
            if (patientName is not null && valid.Any(RequiresPatientContext) &&
                registry.Get("hasta_eslestir") is not null)
            {
                valid.RemoveAll(name => name == "hasta_eslestir");
                valid.Insert(0, "hasta_eslestir");
                inputs["hasta_eslestir"] = new Dictionary<string, string>
                {
                    ["query"] = patientName
                };
            }
            return plan with
            {
                Answer = null,
                IsExplosive = false,
                ExplosionReason = null,
                MedicineNames = [],
                UseRememberedMedicine = false,
                Queue = valid,
                WorkflowInputs = inputs,
                NeedsAdvancedPlanning = false,
                AdvancedPlanningReason = null
            };
        }

        if (mentionsMedula && registry.Get("medula_oturumunu_yonet") is not null)
        {
            var operation = text.Contains("yeniden baslat", StringComparison.Ordinal) ||
                            text.Contains("restart", StringComparison.Ordinal) ||
                            text.Contains("oturumu yenile", StringComparison.Ordinal)
                ? "restart"
                : text.Contains("giris", StringComparison.Ordinal) ||
                  text.Contains("oturum ac", StringComparison.Ordinal)
                    ? "login"
                    : text.Contains("oturum", StringComparison.Ordinal) ||
                      text.Contains("baglanti", StringComparison.Ordinal)
                        ? "status"
                        : null;
            if (operation is not null)
                return Build(["medula_oturumunu_yonet", "bilgi_kartlari_goster"],
                    ("medula_oturumunu_yonet", new Dictionary<string, string>
                    {
                        ["operation"] = operation
                    }),
                    ("bilgi_kartlari_goster", new Dictionary<string, string>
                    {
                        ["title"] = "Medula Oturum Bilgisi"
                    }));
        }

        var patientOperation =
            text.Contains("ilac gecmis", StringComparison.Ordinal) ||
            text.Contains("gecmis ilac", StringComparison.Ordinal) ||
            (text.Contains("goz ilac", StringComparison.Ordinal) && text.Contains("gecmis", StringComparison.Ordinal)) ||
            text.Contains("kullandigi ilac", StringComparison.Ordinal) ||
            text.Contains("verilen ilac", StringComparison.Ordinal)
                ? "medication_history"
                : text.Contains("mustehak", StringComparison.Ordinal) ||
                  text.Contains("provizyon uygun", StringComparison.Ordinal)
                    ? "eligibility"
                    : text.Contains("muayene", StringComparison.Ordinal)
                        ? "examinations"
                        : text.Contains("endikasyon disi", StringComparison.Ordinal)
                            ? "off_label"
                            : text.Contains("rapor", StringComparison.Ordinal) &&
                              !text.Contains("detay", StringComparison.Ordinal) &&
                              (mentionsMedula || patientName is not null || tc.Length == 11)
                                ? "reports"
                                : null;

        var patientMedicationList =
            text.Contains("ilac listesi", StringComparison.Ordinal) ||
            text.Contains("ilac listesini", StringComparison.Ordinal) ||
            text.Contains("ilac ekran", StringComparison.Ordinal);
        if (patientMedicationList && (mentionsMedula || patientName is not null || tc.Length == 11) &&
            registry.Get("medula_hasta_ilac_listesini_getir") is not null)
        {
            var parameters = new Dictionary<string, string>();
            if (tc.Length == 11) parameters["patient_tc"] = tc;
            if (patientName is not null && registry.Get("hasta_eslestir") is not null)
                return Build(["hasta_eslestir", "medula_hasta_ilac_listesini_getir", "tabloda_goster"],
                    ("hasta_eslestir", new Dictionary<string, string>
                    {
                        ["query"] = PatientMatchWorkflow.TurkishUpper(patientName)
                    }),
                    ("medula_hasta_ilac_listesini_getir", parameters),
                    ("tabloda_goster", new Dictionary<string, string> { ["title"] = "Medula Hasta İlaç Listesi" }));
            return Build(["medula_hasta_ilac_listesini_getir", "tabloda_goster"],
                ("medula_hasta_ilac_listesini_getir", parameters),
                ("tabloda_goster", new Dictionary<string, string> { ["title"] = "Medula Hasta İlaç Listesi" }));
        }
        if (patientOperation is not null &&
            registry.Get("medula_hasta_klinik_bilgisi_getir") is not null)
        {
            if (patientOperation == "medication_history" && !mentionsMedula)
                return plan;
            var parameters = new Dictionary<string, string>
            {
                ["operation"] = patientOperation
            };
            if (tc.Length == 11) parameters["patient_tc"] = tc;
            if (text.Contains("goz", StringComparison.Ordinal))
                parameters["eye_medications"] = "true";
            if (text.Contains("suresi dolmus", StringComparison.Ordinal) ||
                text.Contains("gecmis rapor", StringComparison.Ordinal))
                parameters["include_expired"] = "true";
            if (patientOperation == "off_label")
                parameters["off_label_mode"] = text.Contains("eski", StringComparison.Ordinal)
                    ? "legacy"
                    : "person_based";
            var presentation = new Dictionary<string, string>
                {
                    ["title"] = patientOperation switch
                    {
                        "medication_history" => "Medula İlaç Geçmişi",
                        "eligibility" => "Medula Müstehaklık Bilgisi",
                        "reports" => "Medula Raporları",
                        "examinations" => "Medula Muayeneleri",
                        _ => "Medula Endikasyon Dışı Onayları"
                    }
                };
            if (patientName is not null && registry.Get("hasta_eslestir") is not null)
                return Build(["hasta_eslestir", "medula_hasta_klinik_bilgisi_getir", "tabloda_goster"],
                    ("hasta_eslestir", new Dictionary<string, string>
                    {
                        ["query"] = PatientMatchWorkflow.TurkishUpper(patientName)
                    }),
                    ("medula_hasta_klinik_bilgisi_getir", parameters),
                    ("tabloda_goster", presentation));
            return Build(["medula_hasta_klinik_bilgisi_getir", "tabloda_goster"],
                ("medula_hasta_klinik_bilgisi_getir", parameters),
                ("tabloda_goster", presentation));
        }

        var electronicPrescriptionNumber = ExtractMedulaIdentifier(
            originalRequest, @"(?:e[-\s]?reçete|e[-\s]?recete)");
        var trackingNumber = ExtractMedulaIdentifier(
            originalRequest, @"(?:takip(?:\s+numarası|\s+numarasi)?)");
        if (trackingNumber is null)
        {
            var trackingMatch = Regex.Match(originalRequest,
                @"takip\s+numaras[ıi]\s*[:#]?\s*(?<value>[A-Za-z0-9./-]{5,40})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (trackingMatch.Success) trackingNumber = trackingMatch.Groups["value"].Value;
        }
        if (mentionsMedula && tc.Length == 11 &&
            (electronicPrescriptionNumber is not null || trackingNumber is not null) &&
            registry.Get("medula_erecete_sorgula") is not null)
        {
            var byTracking = trackingNumber is not null;
            var parameters = new Dictionary<string, string>
            {
                ["operation"] = "search", ["mode"] = byTracking ? "tracking" : "number",
                ["patient_tc"] = tc
            };
            parameters[byTracking ? "tracking_number" : "electronic_prescription_number"] =
                byTracking ? trackingNumber! : electronicPrescriptionNumber!;
            return Build(["medula_erecete_sorgula", "tabloda_goster"],
                ("medula_erecete_sorgula", parameters),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "Medula E-Reçete Sonuçları"
                }));
        }

        var reportNumber = ExtractMedulaIdentifier(originalRequest, @"(?:medula\s+)?rapor");
        if (mentionsMedula && tc.Length == 11 && reportNumber is not null &&
            text.Contains("detay", StringComparison.Ordinal) &&
            registry.Get("medula_hasta_rapor_detayini_getir") is not null)
            return Build(["medula_hasta_rapor_detayini_getir", "tabloda_goster"],
                ("medula_hasta_rapor_detayini_getir", new Dictionary<string, string>
                {
                    ["patient_tc"] = tc, ["report_number"] = reportNumber
                }),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "Medula Rapor Detayı"
                }));

        if (mentionsMedula && text.Contains("recete filtre", StringComparison.Ordinal) &&
            registry.Get("medula_recete_filtrelerini_getir") is not null)
            return Build(["medula_recete_filtrelerini_getir", "tabloda_goster"],
                ("medula_recete_filtrelerini_getir", new Dictionary<string, string>()),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "Medula Reçete Filtreleri"
                }));

        var latestPrescription = mentionsPrescriptionRecord &&
                                 (text.Contains("en son", StringComparison.Ordinal) ||
                                  Regex.IsMatch(text, @"\bson\s+recete", RegexOptions.CultureInvariant));
        if (mentionsMedula && latestPrescription &&
            ExtractMedulaIdentifier(originalRequest, "reçete|recete") is null &&
            registry.Get("medula_son_receteyi_ac") is not null)
        {
            if (plan.Queue.Any(name => name is "satis_hasilati_ozeti" or "satis_dagilimi_analizi" or "kamu_hasta_odeme_analizi"))
            {
                var queue = plan.Queue.Where(name => name != "debug_workflow").ToList();
                if (!queue.Contains("medula_son_receteyi_ac", StringComparer.Ordinal))
                    queue.Add("medula_son_receteyi_ac");
                var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(
                    plan.WorkflowInputs, StringComparer.Ordinal)
                {
                    ["medula_son_receteyi_ac"] = new Dictionary<string, string>()
                };
                return plan with { Queue = queue, WorkflowInputs = inputs };
            }
            return Build(["medula_son_receteyi_ac"],
                ("medula_son_receteyi_ac", new Dictionary<string, string>()));
        }

        var activePrescriptionContents = mentionsPrescriptionRecord &&
                                         text.Contains("aktif", StringComparison.Ordinal) &&
                                         (text.Contains("iceri", StringComparison.Ordinal) || text.Contains("ilac", StringComparison.Ordinal));
        if (mentionsMedula && activePrescriptionContents &&
            (tc.Length == 11 || patientName is not null) &&
            registry.Get("medula_son_receteyi_ac") is not null)
        {
            var values = new Dictionary<string, string>();
            if (tc.Length == 11) values["patient_tc"] = tc;
            return Build(["medula_son_receteyi_ac"], ("medula_son_receteyi_ac", values));
        }

        var previousPrescription = mentionsPrescriptionRecord &&
                                   (text.Contains("bir onceki", StringComparison.Ordinal) ||
                                    text.Contains("onceki recete", StringComparison.Ordinal));
        if (mentionsMedula && previousPrescription && registry.Get("medula_receteleri_ara") is not null &&
            registry.Get("medula_recete_bilgisi_getir") is not null)
            return Build(["medula_receteleri_ara", "medula_recete_bilgisi_getir"],
                ("medula_receteleri_ara", new Dictionary<string, string>
                {
                    ["source"] = "reachable", ["offset"] = "1", ["limit"] = "1"
                }),
                ("medula_recete_bilgisi_getir", new Dictionary<string, string>
                {
                    ["operation"] = "detail"
                }));

        if (mentionsMedula && text.Contains("recete", StringComparison.Ordinal) &&
            patientName is null && tc.Length != 11 &&
            ExtractMedulaIdentifier(originalRequest, "reçete|recete") is null &&
            registry.Get("medula_receteleri_ara") is not null)
        {
            var parameters = new Dictionary<string, string>();
            var periodMatch = Regex.Match(text, @"\b(?<period>[0-9]{4}-[0-9]{2})\b", RegexOptions.CultureInvariant);
            if (text.Contains("yerel", StringComparison.Ordinal) || text.Contains("arsiv", StringComparison.Ordinal))
            {
                parameters["source"] = "local";
                var days = ExtractPeriodNumber(text, "gun") ?? 30;
                parameters["start_date"] = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                parameters["end_date"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else if (periodMatch.Success)
            {
                parameters["source"] = "medula_period";
                parameters["period"] = periodMatch.Groups["period"].Value;
            }
            else
            {
                parameters["source"] = "medula_daily";
                parameters["day"] = DateTime.Today.Day.ToString(CultureInfo.InvariantCulture);
            }
            if (text.Contains("b grubu", StringComparison.Ordinal)) parameters["invoice_type_code"] = "4";
            return Build(["medula_receteleri_ara", "tabloda_goster"],
                ("medula_receteleri_ara", parameters),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "Medula Reçeteleri"
                }));
        }

        if (mentionsMedula && text.Contains("recete", StringComparison.Ordinal) &&
            (patientName is not null || tc.Length == 11) &&
            registry.Get("medula_hasta_recetelerini_getir") is not null)
        {
            var parameters = new Dictionary<string, string>();
            if (tc.Length == 11) parameters["patient_tc"] = tc;
            return Build(["medula_hasta_recetelerini_getir", "tabloda_goster"],
                ("medula_hasta_recetelerini_getir", parameters),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = "Hastanın Medula Reçeteleri"
                }));
        }

        var prescriptionNumber = ExtractMedulaIdentifier(originalRequest, "reçete|recete");
        if (mentionsMedula && prescriptionNumber is not null &&
            registry.Get("medula_recete_bilgisi_getir") is not null)
        {
            var operation = text.Contains("uyari", StringComparison.Ordinal) ||
                            text.Contains("hata", StringComparison.Ordinal)
                ? "warnings"
                : text.Contains("tedavi sema", StringComparison.Ordinal)
                    ? "treatment_scheme"
                    : "detail";
            return operation == "detail"
                ? Build(["medula_recete_bilgisi_getir"],
                    ("medula_recete_bilgisi_getir", new Dictionary<string, string>
                    {
                        ["operation"] = operation,
                        ["prescription_number"] = prescriptionNumber
                    }))
                : Build(["medula_recete_bilgisi_getir", "tabloda_goster"],
                ("medula_recete_bilgisi_getir", new Dictionary<string, string>
                {
                    ["operation"] = operation,
                    ["prescription_number"] = prescriptionNumber
                }),
                ("tabloda_goster", new Dictionary<string, string>
                {
                    ["title"] = operation == "warnings"
                        ? "Medula Reçete Uyarıları"
                        : operation == "treatment_scheme"
                            ? "Medula Tedavi Şeması"
                            : "Medula Reçete Detayı"
                }));
        }

        if (mentionsMedula && Regex.IsMatch(text, @"\btani(?:\b|lar|si|sini|yi)", RegexOptions.CultureInvariant) &&
            registry.Get("medula_referans_bilgisi_ara") is not null)
        {
            var query = ExtractDiagnosisQuery(originalRequest);
            if (query.Length >= 2)
                return Build(["medula_referans_bilgisi_ara", "tabloda_goster"],
                    ("medula_referans_bilgisi_ara", new Dictionary<string, string>
                    {
                        ["operation"] = "diagnosis", ["query"] = query
                    }),
                    ("tabloda_goster", new Dictionary<string, string>
                    {
                        ["title"] = "Medula Tanı Araması"
                    }));
        }

        if (mentionsMedula && text.Contains("diploma", StringComparison.Ordinal) &&
            registry.Get("medula_referans_bilgisi_ara") is not null)
        {
            var diploma = Regex.Match(originalRequest,
                @"(?:diploma(?:\s+(?:tescil|kayıt|kayit))?\s*(?:no|numarası|numarasi)?\s*[:#]?\s*)(?<value>[A-Za-z0-9./-]{1,32})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["value"].Value;
            if (diploma.Length > 0)
                return Build(["medula_referans_bilgisi_ara", "bilgi_kartlari_goster"],
                    ("medula_referans_bilgisi_ara", new Dictionary<string, string>
                    {
                        ["operation"] = "doctor", ["query"] = diploma
                    }),
                    ("bilgi_kartlari_goster", new Dictionary<string, string>
                    {
                        ["title"] = "Medula Doktor Bilgisi"
                    }));
        }

        if (mentionsMedula && registry.Get("medula_ilac_bilgisi_getir") is not null)
        {
            var barcodes = ExtractExactBarcodes(originalRequest);
            var operation = text.Contains("muadil", StringComparison.Ordinal)
                ? "equivalents"
                : text.Contains("ilac bilg", StringComparison.Ordinal) ||
                  text.Contains("odeme kural", StringComparison.Ordinal) ||
                  text.Contains("kullanim kural", StringComparison.Ordinal)
                    ? "detail"
                    : null;
            if (operation is not null && barcodes.Count > 0)
                return Build(["medula_ilac_bilgisi_getir", "tabloda_goster"],
                    ("medula_ilac_bilgisi_getir", new Dictionary<string, string>
                    {
                        ["operation"] = operation, ["barcode"] = barcodes[0]
                    }),
                    ("tabloda_goster", new Dictionary<string, string>
                    {
                        ["title"] = operation == "equivalents"
                            ? "Medula Muadil İlaçları"
                            : "Medula İlaç Bilgisi"
                    }));
        }

        return plan;
    }

    private static AiWorkflowPlan NormalizeBareMedicineIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (plan.Queue.Count == 0 || plan.IsExplosive) return plan;
        var text = NormalizeIntent(originalRequest).Trim();
        if (text.Length == 0 || ExtractExactBarcodes(originalRequest).Count > 0) return plan;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 1 or > 6) return plan;
        var hasIntent = Regex.IsMatch(text,
            @"\b(?:purchase|buy|order|price|stock|sales?|revenue|profit|movements?|information|details?|campaigns?|equivalents?|compare|show|list|find|how|what|which|fiyat\w*|ucret\w*|para|stok\w*|kac|satis\w*|hasilat\w*|kar\w*|hareket\w*|bilgi\w*|kampanya\w*|muadil\w*|al|alim\w*|siparis\w*|karsilastir\w*|goster\w*|getir\w*|bul|liste\w*|nedir|ne|hangi)\b",
            RegexOptions.CultureInvariant);
        if (hasIntent) return plan;
        var medicineLikePlan = plan.MedicineNames.Count > 0 || plan.Queue.Any(name =>
            name.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal) ||
            name is "stok_portfoy_analizi" or "urun_katalog_analizi" or "barkod_bilgisi_getir" or
                "fiyat_donemlerini_karsilastir" or "stok_analizini_gor");
        if (!medicineLikePlan) return plan;
        var subject = originalRequest.Trim();
        return plan with
        {
            Answer = $"{subject} için ne öğrenmek istediğinizi belirtin: fiyat, stok, satış, hareket, muadil veya alım miktarı gibi.",
            MedicineNames = [],
            UseRememberedMedicine = false,
            Queue = [],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };
    }
    private static AiWorkflowPlan PreserveExplosiveSafety(AiWorkflowPlan plan, string originalRequest)
    {
        var text = NormalizeIntent(originalRequest);
        var mutatesStoredData = Regex.IsMatch(text,
            @"\b(?:sifirla|sifira\s+esitle|0\s+yap|degistir|guncelle|kaydet|sil|duzelt)\b",
            RegexOptions.CultureInvariant);
        if (!mutatesStoredData) return plan;
        return plan with
        {
            IsExplosive = true,
            ExplosionReason = string.IsNullOrWhiteSpace(plan.ExplosionReason)
                ? "Bu istek kayıtlı veriyi değiştirebilir; işlem ancak açık onaydan sonra değerlendirilebilir."
                : plan.ExplosionReason,
            Answer = "Bu değişiklik için kayıtlı bir yazma iş akışı yok; hiçbir veri değiştirilmedi.",
            Queue = [],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizePatientContactIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("hasta_eslestir") is null || registry.Get("hasta_iletisim_bilgisi_getir") is null ||
            HasPatientMutationCue(originalRequest) ||
            ExtractPatientContactQuery(originalRequest) is not { } patientQuery)
            return plan;

        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["hasta_eslestir"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = patientQuery
            },
            ["hasta_iletisim_bilgisi_getir"] = new Dictionary<string, string>()
        };
        return plan with
        {
            Answer = null,
            IsExplosive = false,
            ExplosionReason = null,
            Queue = ["hasta_eslestir", "hasta_iletisim_bilgisi_getir"],
            WorkflowInputs = inputs,
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizePatientProductIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("hasta_eslestir") is null || ExtractPatientProductQuery(originalRequest) is not { } patientQuery)
            return plan;
        var text = NormalizeIntent(originalRequest);
        var latest = text.Contains("en son", StringComparison.Ordinal) ||
                     text.Contains("son satin aldigi", StringComparison.Ordinal) ||
                     text.Contains("son aldigi ilac", StringComparison.Ordinal) ||
                     text.Contains("son aldigi urun", StringComparison.Ordinal);
        var workflow = latest ? "hasta_son_urunleri_getir" : "hasta_urun_ozeti";
        if (registry.Get(workflow) is null) return plan;
        var parameters = CopySalesPeriodParameters(plan);
        var hasRequestedPeriod = Regex.IsMatch(text, @"\b(?:son\s+)?[0-9]{1,4}\s*(?:gun|ay|yil)\b", RegexOptions.CultureInvariant) ||
                                 text.Contains("bugun", StringComparison.Ordinal) ||
                                 text.Contains("bu ay", StringComparison.Ordinal) ||
                                 text.Contains("gecen ay", StringComparison.Ordinal) ||
                                 text.Contains("bu yil", StringComparison.Ordinal) ||
                                 text.Contains("gecen yil", StringComparison.Ordinal);
        if (latest && !hasRequestedPeriod)
        {
            foreach (var key in new[] { "start_date", "end_date", "period_days", "period_months" })
                parameters.Remove(key);
            parameters["all_history"] = "true";
        }
        if (!parameters.Keys.Any(key => key is "start_date" or "end_date" or "period_days" or "period_months" or "all_history"))
            parameters["all_history"] = "true";
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["hasta_eslestir"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["query"] = patientQuery },
            [workflow] = parameters,
            ["tabloda_goster"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = latest ? $"{patientQuery} Son Alışverişi" : $"{patientQuery} Aldığı Ürünler"
            }
        };
        return plan with
        {
            Answer = null, IsExplosive = false, ExplosionReason = null,
            Queue = ["hasta_eslestir", workflow, "tabloda_goster"], WorkflowInputs = inputs,
            NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizePurchaseRecommendationIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("alim_miktari_oner") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        var extractedMedicineName = ExtractRecommendationMedicineName(originalRequest);
        var recommendation = extractedMedicineName is not null ||
                             text.Contains("kac tane almaliyim", StringComparison.Ordinal) ||
                             text.Contains("kac tane almam gerek", StringComparison.Ordinal) ||
                             text.Contains("ne kadar almaliyim", StringComparison.Ordinal) ||
                             text.Contains("alim miktari", StringComparison.Ordinal) ||
                             text.Contains("siparis miktari oner", StringComparison.Ordinal);
        var execution = Regex.IsMatch(text, @"\b(?:siparis\s+(?:ver|et|olustur|gonder)|sepete\s+ekle)\b", RegexOptions.CultureInvariant);
        if (!recommendation || execution) return plan;
        if (plan.MedicineNames.Count == 0 && extractedMedicineName is null &&
            text.Contains("urun", StringComparison.Ordinal) &&
            (text.Contains("stogu yetersiz", StringComparison.Ordinal) || text.Contains("stok yetersiz", StringComparison.Ordinal)) &&
            registry.Get("stok_portfoy_analizi") is not null && registry.Get("tabloda_goster") is not null)
            return plan with
            {
                Answer = null, IsExplosive = false, ExplosionReason = null,
                MedicineNames = [], UseRememberedMedicine = false,
                Queue = ["stok_portfoy_analizi", "alim_miktari_oner", "tabloda_goster"],
                WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["stok_portfoy_analizi"] = new Dictionary<string, string>
                    {
                        ["mode"] = "shortage", ["only_on_market"] = "true", ["limit"] = "20"
                    },
                    ["alim_miktari_oner"] = new Dictionary<string, string>
                    {
                        ["days"] = ExtractPeriodNumber(text, "gun")?.ToString(CultureInfo.InvariantCulture) ?? "30"
                    },
                    ["tabloda_goster"] = new Dictionary<string, string>
                    {
                        ["title"] = "Stok Alım Önerileri"
                    }
                },
                NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
            };
        var isTwoMedicineComparison = plan.Queue.Contains("ilac_barkod_eslestir_1", StringComparer.Ordinal) &&
                                      plan.Queue.Contains("ilac_barkod_eslestir_2", StringComparer.Ordinal);
        if (isTwoMedicineComparison)
        {
            var compoundQueue = plan.Queue.Where(name =>
                    name is not ("tabloda_goster" or "bilgi_kartlari_goster" or
                        "metrik_kartlari_goster" or "bolumlu_ozet_goster"))
                .ToList();
            if (!compoundQueue.Contains("alim_miktari_oner", StringComparer.Ordinal))
                compoundQueue.Add("alim_miktari_oner");
            if (registry.Get("bolumlu_ozet_goster") is not null)
                compoundQueue.Add("bolumlu_ozet_goster");
            var compoundInputs = plan.WorkflowInputs.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                    pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
            foreach (var presentation in new[]
                     {
                         "tabloda_goster", "bilgi_kartlari_goster", "metrik_kartlari_goster",
                         "bolumlu_ozet_goster"
                     })
                compoundInputs.Remove(presentation);
            var days = ExtractPeriodNumber(text, "gun") ??
                       (ExtractPeriodNumber(text, "ay") is { } months ? months * 30 : 90);
            compoundInputs["alim_miktari_oner"] = new Dictionary<string, string>
            {
                ["days"] = Math.Clamp(days, 1, 365).ToString(CultureInfo.InvariantCulture)
            };
            if (registry.Get("bolumlu_ozet_goster") is not null)
                compoundInputs["bolumlu_ozet_goster"] = new Dictionary<string, string>
                {
                    ["title"] = "Satış Karşılaştırması ve Alım Önerisi"
                };
            return plan with
            {
                Answer = null,
                IsExplosive = false,
                ExplosionReason = null,
                MedicineNames = [],
                UseRememberedMedicine = false,
                Queue = compoundQueue,
                WorkflowInputs = compoundInputs,
                NeedsAdvancedPlanning = false,
                AdvancedPlanningReason = null
            };
        }
        var medicineNames = plan.MedicineNames.Count > 0
            ? plan.MedicineNames
            : extractedMedicineName is { } medicineName ? [medicineName] : [];
        var queue = new List<string>();
        if (medicineNames.Count > 0 && registry.Get("ilac_barkod_eslestir") is not null)
            queue.Add("ilac_barkod_eslestir");
        queue.Add("alim_miktari_oner");
        queue.Add("bilgi_kartlari_goster");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (queue.Contains("ilac_barkod_eslestir", StringComparer.Ordinal))
            inputs["ilac_barkod_eslestir"] = plan.WorkflowInputs.GetValueOrDefault("ilac_barkod_eslestir") ?? new Dictionary<string, string>();
        inputs["alim_miktari_oner"] = plan.WorkflowInputs.GetValueOrDefault("alim_miktari_oner") ?? new Dictionary<string, string>();
        inputs["bilgi_kartlari_goster"] = new Dictionary<string, string> { ["title"] = "Alım Miktarı Önerisi" };
        return plan with
        {
            Answer = null, IsExplosive = false, ExplosionReason = null, MedicineNames = medicineNames, Queue = queue,
            WorkflowInputs = inputs, NeedsAdvancedPlanning = false, AdvancedPlanningReason = null
        };
    }

    private AiWorkflowPlan NormalizeOrderIntent(AiWorkflowPlan plan, string originalRequest)
    {
        if (registry.Get("siparis_istegini_tani") is null) return plan;
        var text = NormalizeIntent(originalRequest);
        if (!Regex.IsMatch(text,
                @"\b(?:siparis\s+(?:ver|et|olustur|gonder)|sepete\s+ekle)\b",
                RegexOptions.CultureInvariant))
            return plan;
        return plan with
        {
            Answer = null,
            IsExplosive = false,
            ExplosionReason = null,
            Queue = ["siparis_istegini_tani"],
            WorkflowInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["siparis_istegini_tani"] = plan.WorkflowInputs.GetValueOrDefault("siparis_istegini_tani")
                    ?? new Dictionary<string, string> { ["medicine_query"] = string.Join(", ", plan.MedicineNames) }
            },
            NeedsAdvancedPlanning = false,
            AdvancedPlanningReason = null
        };
    }

    private static string? ExtractRecommendationMedicineName(string request)
    {
        string[] patterns =
        [
            @"^\s*(?<name>[\p{L}0-9][\p{L}0-9\s./-]{1,80}?)\s+(?:kaç\s+tane\s+almalıyım|kac\s+tane\s+almaliyim|ne\s+kadar\s+almalıyım|ne\s+kadar\s+almaliyim|alım\s+miktarı|alim\s+miktari|sipariş\s+miktarı|siparis\s+miktari)\b",
            @"^\s*(?:kaç|kac)\s+(?:tane\s+)?(?<name>[\p{L}0-9][\p{L}0-9\s./-]{0,80}?)\s+(?:almalıyım|almaliyim)\b"
        ];
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(request, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 1 and <= 6) return name;
        }
        return null;
    }

    private static Dictionary<string, string> CopySalesPeriodParameters(AiWorkflowPlan plan)
    {
        var allowed = new HashSet<string>(["start_date", "end_date", "period_days", "period_months", "calendar_period", "all_history",
            "barcodes", "sale_types", "include_refunds", "payment_types", "statuses", "user_id", "is_debted"], StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in plan.WorkflowInputs.Values.SelectMany(value => value))
            if (allowed.Contains(pair.Key)) result[pair.Key] = pair.Value;
        return result;
    }

    private AiWorkflowPlan NormalizeLocalProductPriceIntent(AiWorkflowPlan plan)
    {
        if (!HasLocalProductPriceCue(plan.CorrectedRequest) || registry.Get("barkod_bilgisi_getir") is null)
            return plan;
        var queue = plan.Queue.ToList();
        if (!queue.Contains("ilac_barkod_eslestir", StringComparer.Ordinal) &&
            plan.MedicineNames.Count > 0 && registry.Get("ilac_barkod_eslestir") is not null)
            queue.Insert(0, "ilac_barkod_eslestir");
        if (!queue.Contains("ilac_barkod_eslestir", StringComparer.Ordinal) ||
            queue.Any(name => name is "barkod_bilgisi_getir" or "canli_barkod_fiyati_getir" or "fiyat_gecmisini_gor"))
            return plan;
        queue.Add("barkod_bilgisi_getir");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(plan.WorkflowInputs, StringComparer.Ordinal);
        if (!inputs.ContainsKey("ilac_barkod_eslestir"))
            inputs["ilac_barkod_eslestir"] = new Dictionary<string, string>();
        inputs["barkod_bilgisi_getir"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fields"] = "name,sale_price,public_price,public_paid_price,purchase_price,campaign_purchase_price,currency"
        };
        return plan with { Queue = queue, WorkflowInputs = inputs };
    }

    private static AiWorkflowPlan NormalizeEquivalentIntent(AiWorkflowPlan plan)
    {
        if (!plan.Queue.Contains("urun_muadillerini_getir", StringComparer.Ordinal)) return plan;
        var presentations = new HashSet<string>(StringComparer.Ordinal)
        {
            "tabloda_goster", "bilgi_kartlari_goster", "sirali_liste_goster"
        };
        var queue = plan.Queue.Where(name =>
                name.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal) ||
                name == "urun_muadillerini_getir" || presentations.Contains(name))
            .Distinct(StringComparer.Ordinal).ToList();
        if (!queue.Any(presentations.Contains)) queue.Add("tabloda_goster");
        var inputs = plan.WorkflowInputs.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var key in inputs.Keys.Where(key => !queue.Contains(key, StringComparer.Ordinal)).ToArray())
            inputs.Remove(key);
        var values = new Dictionary<string, string>(
            inputs.GetValueOrDefault("urun_muadillerini_getir") ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var normalized = NormalizeIntent(plan.CorrectedRequest);
        if (normalized.Contains("pasif", StringComparison.Ordinal)) values["only_on_market"] = "false";
        else if (normalized.Contains("aktif", StringComparison.Ordinal)) values["only_on_market"] = "true";
        if (normalized.Contains("satis fiyati ayni", StringComparison.Ordinal) ||
            normalized.Contains("ayni satis fiyat", StringComparison.Ordinal))
            values["same_sale_price"] = "true";
        if (normalized.Contains("fiyat", StringComparison.Ordinal) &&
            (normalized.Contains("sira", StringComparison.Ordinal) || normalized.Contains("en dusuk", StringComparison.Ordinal)))
        {
            values["sort_by"] = "sale_price";
            values["sort_direction"] = "ascending";
        }
        if (normalized.Contains("hangisi", StringComparison.Ordinal) && normalized.Contains("en dusuk", StringComparison.Ordinal))
            values["result_limit"] = "1";
        inputs["urun_muadillerini_getir"] = values;
        return plan with { Queue = queue, WorkflowInputs = inputs };
    }

    private static AiWorkflowPlan NormalizeGlobalCatalogIntent(AiWorkflowPlan plan)
    {
        if (!plan.Queue.Contains("fiyat_donemlerini_karsilastir", StringComparer.Ordinal) || plan.MedicineNames.Count > 0)
            return plan;
        var parameters = plan.WorkflowInputs.GetValueOrDefault("fiyat_donemlerini_karsilastir");
        if (parameters?.GetValueOrDefault("barcodes") is { Length: > 0 }) return plan;
        var normalized = NormalizeIntent(plan.CorrectedRequest);
        var isCatalogueWide = normalized.Contains("urunleri", StringComparison.Ordinal) ||
                              normalized.Contains("katalogdaki", StringComparison.Ordinal) ||
                              normalized.Contains("aktif urun", StringComparison.Ordinal) ||
                              normalized.Contains("pasif urun", StringComparison.Ordinal);
        if (!isCatalogueWide) return plan;
        var queue = plan.Queue.Select(name => name == "fiyat_donemlerini_karsilastir" ? "urun_katalog_analizi" : name)
            .Distinct(StringComparer.Ordinal).ToArray();
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(plan.WorkflowInputs, StringComparer.Ordinal);
        inputs.Remove("fiyat_donemlerini_karsilastir");
        inputs["urun_katalog_analizi"] = new Dictionary<string, string>();
        return plan with { Queue = queue, WorkflowInputs = inputs };
    }

    private AiWorkflowPlan NormalizeVerifiedAnalytics(AiWorkflowPlan plan)
    {
        var text = NormalizeIntent(plan.CorrectedRequest);
        var shared = plan.WorkflowInputs.Values.SelectMany(value => value)
            .Where(pair => pair.Key is "start_date" or "end_date" or "period_days" or "period_months" or
                "all_history" or "sale_types" or "include_refunds" or "barcodes" or "statuses" or "limit")
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        ApplyCalendarRange(shared, text);

        if (text.Contains("sinif", StringComparison.Ordinal) && text.Contains("urun", StringComparison.Ordinal) &&
            text.Contains("hasilat", StringComparison.Ordinal))
            return Rebuild(plan, ["abc_hasilat_siniflandirmasi", "tabloda_goster"],
                ("abc_hasilat_siniflandirmasi", shared),
                ("tabloda_goster", new() { ["title"] = "Ürün Hasılatı ABC Sınıflandırması" }));
        if (text.Contains("kullanici", StringComparison.Ordinal) && text.Contains("ortalama sepet", StringComparison.Ordinal))
            return Rebuild(plan, ["satis_kohort_analizi", "tabloda_goster"],
                ("satis_kohort_analizi", new(shared) { ["group_by"] = "user", ["include_refunds"] = "false" }),
                ("tabloda_goster", new() { ["title"] = "Kullanıcı Satış Sayısı ve Ortalama Sepeti" }));
        if (text.Contains("en cok hasilat", StringComparison.Ordinal) && text.Contains("urun", StringComparison.Ordinal) &&
            (text.Contains("yuzde kac", StringComparison.Ordinal) || text.Contains("toplam hasilatin", StringComparison.Ordinal)))
            return Rebuild(plan, ["hasilat_yogunlugu_analizi", "tabloda_goster"],
                ("hasilat_yogunlugu_analizi", new(shared) { ["limit"] = LastNumber(text, 10).ToString(CultureInfo.InvariantCulture) }),
                ("tabloda_goster", new() { ["title"] = "Ürün Hasılat Yoğunluğu" }));
        if (text.Contains("iade", StringComparison.Ordinal) &&
            (text.Contains("hafta hafta", StringComparison.Ordinal) || text.Contains("haftalik", StringComparison.Ordinal) ||
             text.Contains("haftalara gore", StringComparison.Ordinal)))
            return Rebuild(plan, ["haftalik_iade_orani", "tabloda_goster"],
                ("haftalik_iade_orani", shared),
                ("tabloda_goster", new() { ["title"] = "Haftalık İade Özeti" }));
        if (text.Contains("kullanici", StringComparison.Ordinal) && text.Contains("bos", StringComparison.Ordinal) &&
            text.Contains("karekod hareket", StringComparison.Ordinal))
            return Rebuild(plan, ["hareket_dagilimi_analizi", "tabloda_goster"],
                ("hareket_dagilimi_analizi", new(shared) { ["group_by"] = "user", ["missing_user_only"] = "true" }),
                ("tabloda_goster", new() { ["title"] = "Kullanıcısı Eksik Karekod Hareketleri" }));
        if (text.Contains("depo giris", StringComparison.Ordinal) && text.Contains("haftalara gore", StringComparison.Ordinal))
            return Rebuild(plan, ["hareket_dagilimi_analizi", "tabloda_goster"],
                ("hareket_dagilimi_analizi", new(shared) { ["group_by"] = "week", ["direction"] = "entry", ["address_type"] = "depot" }),
                ("tabloda_goster", new() { ["title"] = "Haftalık Depo Girişleri" }));

        if (text.Contains("satis", StringComparison.Ordinal) &&
            (text.Contains("karekod hareket", StringComparison.Ordinal) || text.Contains("stok hareket", StringComparison.Ordinal)) &&
            (text.Contains("yan yana", StringComparison.Ordinal) || text.Contains("birlikte", StringComparison.Ordinal)))
            return Rebuild(plan, ["satislari_gor", "hareketleri_gor", "bolumlu_ozet_goster"],
                ("satislari_gor", new(shared) { ["period_days"] = "0", ["limit"] = "200" }),
                ("hareketleri_gor", new(shared) { ["period_days"] = "0", ["limit"] = "200" }),
                ("bolumlu_ozet_goster", new() { ["title"] = "Satışlar ve Karekod Hareketleri" }));
        if (text.Contains("en yuksek hasilatli gun", StringComparison.Ordinal) ||
            text.Contains("hasilatin en yuksek oldugu gun", StringComparison.Ordinal))
            return Rebuild(plan, ["en_yuksek_hasilatli_gun", "tabloda_goster"],
                ("en_yuksek_hasilatli_gun", shared),
                ("tabloda_goster", new() { ["title"] = "En Yüksek Hasılatlı Günün Satışları" }));
        if (text.Contains("iade oran", StringComparison.Ordinal) &&
            (text.Contains("hafta hafta", StringComparison.Ordinal) || text.Contains("haftalik", StringComparison.Ordinal) ||
             text.Contains("haftalara gore", StringComparison.Ordinal)))
            return Rebuild(plan, ["haftalik_iade_orani", "tabloda_goster"],
                ("haftalik_iade_orani", shared),
                ("tabloda_goster", new() { ["title"] = "Haftalık İade Oranı" }));
        if (text.Contains("satis olmayan ay", StringComparison.Ordinal) ||
            text.Contains("hic satilmadigi ay", StringComparison.Ordinal))
        {
            var barcodes = ExtractExactBarcodes(plan.CorrectedRequest);
            var noSale = new Dictionary<string, string>(shared, StringComparer.Ordinal);
            if (barcodes.Count > 0) noSale["barcodes"] = string.Join(',', barcodes);
            if (barcodes.Count > 0)
                return Rebuild(plan, ["urun_satis_olmayan_aylar", "tabloda_goster"],
                    ("urun_satis_olmayan_aylar", noSale),
                    ("tabloda_goster", new() { ["title"] = "Satış Olmayan Aylar" }));
            return Rebuild(plan, ["ilac_barkod_eslestir", "urun_satis_olmayan_aylar", "tabloda_goster"],
                ("ilac_barkod_eslestir", new()),
                ("urun_satis_olmayan_aylar", noSale),
                ("tabloda_goster", new() { ["title"] = "Satış Olmayan Aylar" }));
        }
        if (text.Contains("hasta", StringComparison.Ordinal) && text.Contains("hasilat", StringComparison.Ordinal) &&
            (text.Contains("yuzde 50", StringComparison.Ordinal) || text.Contains("%50", StringComparison.Ordinal)))
            return Rebuild(plan, ["hasta_hasilat_yogunlugu", "tabloda_goster"],
                ("hasta_hasilat_yogunlugu", new(shared) { ["target_percent"] = "50" }),
                ("tabloda_goster", new() { ["title"] = "Hasta Hasılat Yoğunluğu" }));
        if (text.Contains("ortalama kamu ucret", StringComparison.Ordinal) ||
            text.Contains("ortalama kurum ucret", StringComparison.Ordinal))
            return Rebuild(plan, ["satis_kohort_analizi", "tabloda_goster"],
                ("satis_kohort_analizi", new(shared)
                {
                    ["group_by"] = "sale_type", ["sale_types"] = "prescription", ["include_refunds"] = "false",
                    ["statuses"] = text.Contains("tamamlan", StringComparison.Ordinal) ? "completed" : string.Empty
                }),
                ("tabloda_goster", new() { ["title"] = "Ortalama Kamu Ücreti" }));
        if ((text.Contains("kamu ucret", StringComparison.Ordinal) || text.Contains("kurum ucret", StringComparison.Ordinal)) &&
            text.Contains("sifir", StringComparison.Ordinal) && text.Contains("satis", StringComparison.Ordinal))
            return Rebuild(plan, ["sifir_kamu_ucretli_satislar", "tabloda_goster"],
                ("sifir_kamu_ucretli_satislar", new(shared) { ["sale_types"] = "prescription" }),
                ("tabloda_goster", new() { ["title"] = "Kamu Ücreti Sıfır Reçeteli Satışlar" }));
        var refundPatient = ExtractPatientQuery(plan.CorrectedRequest);
        if (refundPatient is not null && text.Contains("iade", StringComparison.Ordinal))
            return Rebuild(plan, ["hasta_eslestir", "hasta_iadelerini_gor", "tabloda_goster"],
                ("hasta_eslestir", new() { ["query"] = refundPatient }),
                ("hasta_iadelerini_gor", new(shared) { ["all_history"] = "true" }),
                ("tabloda_goster", new() { ["title"] = $"{refundPatient} — İade Geçmişi" }));
        if ((text.Contains("receteli", StringComparison.Ordinal) && text.Contains("perakende", StringComparison.Ordinal) &&
             (text.Contains("ayri", StringComparison.Ordinal) || text.Contains("karsilastir", StringComparison.Ordinal))) ||
            (text.Contains("satis", StringComparison.Ordinal) && text.Contains("iade", StringComparison.Ordinal) &&
             (text.Contains("ayri", StringComparison.Ordinal) || text.Contains("karsilastir", StringComparison.Ordinal))) ||
            (text.Contains("satis tur", StringComparison.Ordinal) && text.Contains("ortalama iskonto", StringComparison.Ordinal)))
        {
            var group = text.Contains("satis", StringComparison.Ordinal) && text.Contains("iade", StringComparison.Ordinal) &&
                        !text.Contains("iade oran", StringComparison.Ordinal) ? "record_type" : "sale_type";
            var cohort = new Dictionary<string, string>(shared, StringComparer.Ordinal)
            {
                ["group_by"] = group, ["include_refunds"] = group == "record_type" ? "true" : "false"
            };
            cohort.Remove("sale_types");
            return Rebuild(plan, ["satis_kohort_analizi", "tabloda_goster"],
                ("satis_kohort_analizi", cohort),
                ("tabloda_goster", new() { ["title"] = group == "record_type" ? "Satış ve İade Karşılaştırması" : "Reçeteli ve Perakende Satış Karşılaştırması" }));
        }
        if ((text.Contains("ucten fazla", StringComparison.Ordinal) || text.Contains("3 ten fazla", StringComparison.Ordinal)) &&
            text.Contains("hasta", StringComparison.Ordinal))
            return Rebuild(plan, ["satis_kohort_analizi", "tabloda_goster"],
                ("satis_kohort_analizi", new(shared) { ["group_by"] = "patient", ["minimum_sale_count"] = "4", ["include_refunds"] = "false" }),
                ("tabloda_goster", new() { ["title"] = "Birden Fazla Alışveriş Yapan Hastalar" }));
        if ((text.Contains("stok kapsama", StringComparison.Ordinal) || text.Contains("stok yeterlilik", StringComparison.Ordinal)) &&
            (text.Contains("gunden az", StringComparison.Ordinal) || text.Contains("gunun altinda", StringComparison.Ordinal)))
            return Rebuild(plan, ["stok_satis_kesisim_analizi", "tabloda_goster"],
                ("stok_satis_kesisim_analizi", new(shared)
                {
                    ["analysis_kind"] = "coverage",
                    ["maximum_coverage_days"] = FirstNumber(text, 15).ToString(CultureInfo.InvariantCulture),
                    ["limit"] = "100"
                }),
                ("tabloda_goster", new() { ["title"] = "Sınırlı Stok Kapsaması" }));
        if ((text.Contains("etken madde", StringComparison.Ordinal) || text.Contains("etkin madde", StringComparison.Ordinal)) &&
            text.Contains("fiyat", StringComparison.Ordinal) &&
            (text.Contains("fark", StringComparison.Ordinal) || text.Contains("aralig", StringComparison.Ordinal)))
            return Rebuild(plan, ["urun_katalog_analizi", "tabloda_goster"],
                ("urun_katalog_analizi", new() { ["analysis_kind"] = "ingredient_price_spread", ["only_on_market"] = "true", ["limit"] = "100" }),
                ("tabloda_goster", new() { ["title"] = "Etken Madde Fiyat Aralıkları" }));
        if (text.Contains("fiyati", StringComparison.Ordinal) && text.Contains("degisen", StringComparison.Ordinal) &&
            (text.Contains("en cok", StringComparison.Ordinal) || text.Contains("kac kez", StringComparison.Ordinal)))
            return Rebuild(plan, ["urun_katalog_analizi", "tabloda_goster"],
                ("urun_katalog_analizi", new(shared) { ["analysis_kind"] = "price_change_frequency", ["only_on_market"] = "true", ["limit"] = "100" }),
                ("tabloda_goster", new() { ["title"] = "Fiyatı En Sık Değişen Aktif Ürünler" }));
        var comparisonBarcodes = ExtractExactBarcodes(plan.CorrectedRequest);
        if (comparisonBarcodes.Count > 1 && text.Contains("fiyat", StringComparison.Ordinal) &&
            text.Contains("stok", StringComparison.Ordinal) && text.Contains("satis", StringComparison.Ordinal))
            return Rebuild(plan, ["urun_operasyonel_karsilastirma", "tabloda_goster"],
                ("urun_operasyonel_karsilastirma", new(shared) { ["barcodes"] = string.Join(',', comparisonBarcodes) }),
                ("tabloda_goster", new() { ["title"] = "Ürün Fiyat, Stok ve Satış Karşılaştırması" }));

        if (text.Contains("bunlardan", StringComparison.Ordinal) && text.Contains("pasif", StringComparison.Ordinal))
            return Rebuild(plan, ["barkod_bilgisi_getir", "pasif_urunleri_filtrele", "tabloda_goster"],
                ("barkod_bilgisi_getir", new() { ["fields"] = "name,is_on_market,shelf_stock_quantity" }),
                ("pasif_urunleri_filtrele", new()));
        if (text.Contains("pasif olan", StringComparison.Ordinal) && text.Contains("hareket", StringComparison.Ordinal))
            return Rebuild(plan, ["hareketleri_gor", "tabloda_goster"],
                ("hareketleri_gor", new() { ["period_days"] = "30" }));
        if (text.Contains("fiyati hic degismeyen", StringComparison.Ordinal) ||
            text.Contains("alis fiyati", StringComparison.Ordinal) && text.Contains("satis fiyatindan", StringComparison.Ordinal) ||
            text.Contains("pasif urun", StringComparison.Ordinal) && text.Contains("stok deger", StringComparison.Ordinal))
            return Rebuild(plan, ["urun_katalog_analizi", "tabloda_goster"]);
        if (text.Contains("depo girisinden satis cikisina", StringComparison.Ordinal) ||
            text.Contains("hasta iadesi", StringComparison.Ordinal) && text.Contains("sonraki hareket", StringComparison.Ordinal))
            return Rebuild(plan, ["karekod_tutarlilik_kontrolu", "tabloda_goster"]);
        if (text.Contains("orijinal satis", StringComparison.Ordinal) && text.Contains("eslestirilemeyen iade", StringComparison.Ordinal))
            return Rebuild(plan, ["satis_tutarlilik_kontrolu", "tabloda_goster"]);
        if (text.Contains("besten fazla", StringComparison.Ordinal) && text.Contains("urun bulunan satis", StringComparison.Ordinal))
            return Rebuild(plan, ["sepet_analizi", "tabloda_goster"]);
        var requestsProductAbcAnalysis = text.Contains("siniflarina ayir", StringComparison.Ordinal) ||
                                         text.Contains("urun hasilat", StringComparison.Ordinal) ||
                                         text.Contains("stok sinif", StringComparison.Ordinal) ||
                                         text.Contains("abc analiz", StringComparison.Ordinal);
        if (requestsProductAbcAnalysis &&
            (text.Contains("a, b ve c", StringComparison.Ordinal) || text.Contains("a b ve c", StringComparison.Ordinal) ||
             text.Contains("a/b/c", StringComparison.Ordinal) || text.Contains("abc", StringComparison.Ordinal)))
            return Rebuild(plan, ["abc_hasilat_siniflandirmasi", "tabloda_goster"],
                ("abc_hasilat_siniflandirmasi", shared));
        if ((text.Contains("stok", StringComparison.Ordinal) || text.Contains("stogu", StringComparison.Ordinal)) && text.Contains("kac gun", StringComparison.Ordinal))
            return Rebuild(plan, ["stok_analizini_gor", "bilgi_kartlari_goster"],
                ("stok_analizini_gor", shared));
        if ((text.Contains("stok", StringComparison.Ordinal) || text.Contains("stogu", StringComparison.Ordinal)) &&
            (text.Contains("bitebilecek", StringComparison.Ordinal) ||
             text.Contains("minimum stok", StringComparison.Ordinal) || text.Contains("negatif stok", StringComparison.Ordinal)))
            return Rebuild(plan, ["stok_portfoy_analizi", "tabloda_goster"]);
        if (text.Contains("alinmis", StringComparison.Ordinal) && text.Contains("satilmamis", StringComparison.Ordinal) ||
            text.Contains("son alisa gore", StringComparison.Ordinal))
            return Rebuild(plan, ["depo_alis_analizi", "tabloda_goster"]);

        if (text.Contains("hasta iadesi", StringComparison.Ordinal) &&
            (text.Contains("karekod", StringComparison.Ordinal) || text.Contains("hareket", StringComparison.Ordinal)) &&
            !text.Contains("sonraki hareket", StringComparison.Ordinal))
            return Rebuild(plan, ["hareketleri_gor", "zaman_cizelgesinde_goster"],
                ("hareketleri_gor", new()
                {
                    ["period_days"] = ExtractPeriodNumber(text, "gun")?.ToString(CultureInfo.InvariantCulture) ?? "30",
                    ["movement_types"] = "Hasta İade", ["direction"] = "entry", ["address_type"] = "patient"
                }),
                ("zaman_cizelgesinde_goster", new() { ["title"] = "Hasta İadesi Karekod Hareketleri" }));

        if (text.Contains("depo giris", StringComparison.Ordinal) &&
            (text.Contains("depoya gore", StringComparison.Ordinal) || text.Contains("depo bazinda", StringComparison.Ordinal)) &&
            text.Contains("kullanici", StringComparison.Ordinal) &&
            text.Contains("gore", StringComparison.Ordinal) && registry.Get("hareket_coklu_dagilim_analizi") is not null)
            return Rebuild(plan, ["hareket_coklu_dagilim_analizi", "tabloda_goster"],
                ("hareket_coklu_dagilim_analizi", new()
                {
                    ["dimensions"] = "address,user",
                    ["period_days"] = ExtractPeriodNumber(text, "gun")?.ToString(CultureInfo.InvariantCulture) ?? "1",
                    ["direction"] = "entry", ["address_type"] = "depot"
                }),
                ("tabloda_goster", new() { ["title"] = "Depo Girişleri: Depo ve Kullanıcı Özeti" }));

        if (text.Contains("satis cikis", StringComparison.Ordinal) &&
            text.Contains("urun adina gore", StringComparison.Ordinal) &&
            registry.Get("hareket_dagilimi_analizi") is not null)
            return Rebuild(plan, ["hareket_dagilimi_analizi", "tabloda_goster"],
                ("hareket_dagilimi_analizi", new(shared)
                {
                    ["group_by"] = "medicine", ["direction"] = "exit",
                    ["movement_types"] = "Reçeteli Satış,Perakende Satış",
                    ["limit"] = LastNumber(text, 20).ToString(CultureInfo.InvariantCulture)
                }),
                ("tabloda_goster", new() { ["title"] = "Satış Çıkışları — Ürün Özeti" }));

        if (text.Contains("karekod", StringComparison.Ordinal) && text.Contains("bu hafta", StringComparison.Ordinal) &&
            text.Contains("gecen hafta", StringComparison.Ordinal))
            return Rebuild(plan, ["hareket_donemlerini_karsilastir", "tabloda_goster"],
                ("hareket_donemlerini_karsilastir", new() { ["period_kind"] = "this_week" }));
        var exactBarcodes = ExtractExactBarcodes(plan.CorrectedRequest);
        if (exactBarcodes.Count > 1 && text.Contains("giris", StringComparison.Ordinal) &&
            text.Contains("cikis", StringComparison.Ordinal) && text.Contains("karsilastir", StringComparison.Ordinal))
            return Rebuild(plan, ["hareket_dagilimi_analizi", "tabloda_goster"],
                ("hareket_dagilimi_analizi", new(shared)
                {
                    ["barcodes"] = string.Join(',', exactBarcodes),
                    ["group_by"] = "barcode",
                    ["direction"] = "all"
                }));

        var patient = ExtractPatientQuery(plan.CorrectedRequest) ??
                      plan.WorkflowInputs.GetValueOrDefault("hasta_eslestir")?.GetValueOrDefault("query");
        if (patient is not null && text.Contains("aldigi urun", StringComparison.Ordinal) && text.Contains("stok", StringComparison.Ordinal))
            return Rebuild(plan, ["hasta_eslestir", "hasta_urun_ozeti", "stok_analizini_gor", "bolumlu_ozet_goster"],
                ("hasta_eslestir", new() { ["query"] = patient }),
                ("hasta_urun_ozeti", new(shared) { ["all_history"] = "true" }),
                ("stok_analizini_gor", new()));
        if (patient is not null && text.Contains("alisveris", StringComparison.Ordinal) &&
            text.Contains("tarih", StringComparison.Ordinal) && text.Contains("sayi", StringComparison.Ordinal))
            return Rebuild(plan, ["hasta_eslestir", "hasta_satis_zaman_cizelgesi", "tabloda_goster"],
                ("hasta_eslestir", new() { ["query"] = patient }),
                ("hasta_satis_zaman_cizelgesi", shared));
        if (patient is not null && text.Contains("ortalama gun", StringComparison.Ordinal) && text.Contains("alisveris", StringComparison.Ordinal))
            return Rebuild(plan, ["hasta_eslestir", "hasta_alisveris_araligi", "metrik_kartlari_goster"],
                ("hasta_eslestir", new() { ["query"] = patient }),
                ("hasta_alisveris_araligi", shared));
        if (text.Contains("duzenli alisveris", StringComparison.Ordinal) && text.Contains("gelmeyen hasta", StringComparison.Ordinal))
            return Rebuild(plan, ["geri_kazanilacak_hastalar", "tabloda_goster"],
                ("geri_kazanilacak_hastalar", shared));
        if (text.Contains("ayni hasta", StringComparison.Ordinal) && text.Contains("ayni gun", StringComparison.Ordinal))
        {
            var repeat = new Dictionary<string, string>(shared, StringComparer.Ordinal);
            if (!Regex.IsMatch(text, @"\bson\s+[0-9]+\s+(?:gun|ay|yil)\b", RegexOptions.CultureInvariant))
            {
                repeat.Remove("all_history");
                repeat["period_days"] = "30";
            }
            return Rebuild(plan, ["ayni_gun_tekrar_satislari", "tabloda_goster"],
                ("ayni_gun_tekrar_satislari", repeat));
        }
        if (text.Contains("yuzde 90", StringComparison.Ordinal) && text.Contains("receteli", StringComparison.Ordinal))
            return Rebuild(plan, ["receteli_satis_payi_analizi", "tabloda_goster"],
                ("receteli_satis_payi_analizi", new(shared) { ["minimum_share"] = "90" }));

        var asksInstitutionPatientSplit =
            (text.Contains("kamu odeme", StringComparison.Ordinal) ||
             text.Contains("kamu pay", StringComparison.Ordinal) ||
             text.Contains("kurum odeme", StringComparison.Ordinal)) &&
            (text.Contains("hasta odeme", StringComparison.Ordinal) ||
             text.Contains("hasta pay", StringComparison.Ordinal));
        if (asksInstitutionPatientSplit)
            return Rebuild(plan, ["kamu_hasta_odeme_analizi", "tabloda_goster"],
                ("kamu_hasta_odeme_analizi", shared));
        if (text.Contains("iskonto orani", StringComparison.Ordinal) && text.Contains("yuzde", StringComparison.Ordinal))
            return Rebuild(plan, ["yuksek_iskontolu_satislar", "tabloda_goster"],
                ("yuksek_iskontolu_satislar", new(shared) { ["minimum_discount_percent"] = FirstPercent(text, 20).ToString(CultureInfo.InvariantCulture) }));
        if (text.Contains("zararina", StringComparison.Ordinal) && text.Contains("urun satir", StringComparison.Ordinal))
            return Rebuild(plan, ["zararina_satis_satirlari", "tabloda_goster"],
                ("zararina_satis_satirlari", shared));
        if (text.Contains("kar marji", StringComparison.Ordinal) && text.Contains("en az", StringComparison.Ordinal))
            return Rebuild(plan, ["urun_kar_marji_analizi", "tabloda_goster"],
                ("urun_kar_marji_analizi", new(shared) { ["minimum_quantity"] = FirstNumber(text, 10).ToString(CultureInfo.InvariantCulture), ["limit"] = "20" }));
        if (text.Contains("kar marji", StringComparison.Ordinal) && text.Contains("aktif urun", StringComparison.Ordinal))
            return Rebuild(plan, ["urun_kar_marji_analizi", "tabloda_goster"],
                ("urun_kar_marji_analizi", new(shared) { ["only_on_market"] = "true", ["limit"] = text.Contains("bes", StringComparison.Ordinal) ? "5" : "20" }),
                ("tabloda_goster", new() { ["title"] = "Aktif Ürün Kâr Marjı Sıralaması" }));
        if (text.Contains("gunluk ortalama net hasilat", StringComparison.Ordinal))
            return Rebuild(plan, ["gunluk_ortalama_hasilat", "metrik_kartlari_goster"],
                ("gunluk_ortalama_hasilat", shared));
        if (text.Contains("haftanin gunlerine gore", StringComparison.Ordinal))
            return Rebuild(plan, ["hafta_gunu_satis_analizi", "tabloda_goster"],
                ("hafta_gunu_satis_analizi", shared));
        if (text.Contains("hafta sonu", StringComparison.Ordinal) && text.Contains("satis", StringComparison.Ordinal))
            return Rebuild(plan, ["hafta_gunu_satis_analizi", "tabloda_goster"],
                ("hafta_gunu_satis_analizi", new(shared) { ["weekend_only"] = "true" }),
                ("tabloda_goster", new() { ["title"] = "Hafta Sonu Satışları" }));
        if (text.Contains("satis say", StringComparison.Ordinal) && text.Contains("en yuksek", StringComparison.Ordinal) &&
            text.Contains("saat", StringComparison.Ordinal))
            return Rebuild(plan, ["saatlik_satis_analizi", "tabloda_goster"],
                ("saatlik_satis_analizi", new(shared) { ["sort_by"] = "record_count", ["sort_direction"] = "descending", ["limit"] = "1" }),
                ("tabloda_goster", new() { ["title"] = "En Yoğun Satış Saati" }));
        if (text.Contains("saat araliklarina gore", StringComparison.Ordinal))
            return Rebuild(plan, ["saatlik_satis_analizi", "tabloda_goster"],
                ("saatlik_satis_analizi", shared));
        if (text.Contains("en cok hasilat", StringComparison.Ordinal) && text.Contains("yuzde kac", StringComparison.Ordinal))
            return Rebuild(plan, ["hasilat_yogunlugu_analizi", "tabloda_goster"],
                ("hasilat_yogunlugu_analizi", new(shared) { ["limit"] = FirstNumber(text, 20).ToString(CultureInfo.InvariantCulture) }));
        if (text.Contains("brut satis", StringComparison.Ordinal) && text.Contains("iskonto", StringComparison.Ordinal) &&
            text.Contains("net hasilat", StringComparison.Ordinal))
            return Rebuild(plan, ["satis_hasilati_ozeti", "metrik_kartlari_goster"],
                ("satis_hasilati_ozeti", new(shared) { ["period_months"] = "1", ["include_refunds"] = "true" }));
        if (text.Contains("en cok satilan", StringComparison.Ordinal) && text.Contains("urun katalog", StringComparison.Ordinal))
            return Rebuild(plan, ["satis_dagilimi_analizi", "modulleri_ac", "tabloda_goster"],
                ("satis_dagilimi_analizi", new(shared) { ["group_by"] = "product", ["sort_by"] = "quantity", ["sort_direction"] = "descending", ["limit"] = LastNumber(text, 10).ToString(CultureInfo.InvariantCulture) }),
                ("modulleri_ac", new() { ["module_ids"] = "products.catalog" }));

        var hasResolvedProduct = plan.MedicineNames.Count > 0 || plan.WorkflowInputs.Values.Any(value => value.ContainsKey("barcodes"));
        if (!hasResolvedProduct && !HasRecentOutputCue(plan.CorrectedRequest) &&
            (plan.Queue.Contains("stok_analizini_gor", StringComparer.Ordinal) ||
             text.Contains("siparis oner", StringComparison.Ordinal) &&
             (text.Contains("mevcut stok", StringComparison.Ordinal) || text.Contains("yoldaki urun", StringComparison.Ordinal) || text.Contains("satis hizi", StringComparison.Ordinal))))
            return Rebuild(plan, ["stok_portfoy_analizi", "tabloda_goster"]);
        return plan;

        static AiWorkflowPlan Rebuild(AiWorkflowPlan source, IReadOnlyList<string> queue,
            params (string Name, Dictionary<string, string> Arguments)[] arguments)
        {
            var inputs = arguments.ToDictionary(item => item.Name,
                item => (IReadOnlyDictionary<string, string>)item.Arguments, StringComparer.Ordinal);
            foreach (var presentation in queue.Where(name => name.EndsWith("_goster", StringComparison.Ordinal)))
                if (!inputs.ContainsKey(presentation)) inputs[presentation] = new Dictionary<string, string>();
            return source with { Answer = null, Queue = queue, WorkflowInputs = inputs, NeedsAdvancedPlanning = false, AdvancedPlanningReason = null };
        }
        static int FirstPercent(string value, int fallback)
        {
            var match = Regex.Match(value, @"\byuzde\s*([0-9]{1,3})\b", RegexOptions.CultureInvariant);
            return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : fallback;
        }
        static int LastNumber(string value, int fallback)
        {
            var matches = Regex.Matches(value, @"\b([0-9]{1,4})\b", RegexOptions.CultureInvariant);
            return matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out var number) ? number : fallback;
        }
        static int FirstNumber(string value, int fallback)
        {
            var match = Regex.Match(value, @"\b([0-9]{1,4})\b", RegexOptions.CultureInvariant);
            return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : fallback;
        }
    }

    private static AiWorkflowPlan NormalizePresentation(AiWorkflowPlan plan)
    {
        string[] presentationNames = ["tabloda_goster", "bilgi_kartlari_goster", "metrik_kartlari_goster",
            "sirali_liste_goster", "zaman_cizelgesinde_goster", "bolumlu_ozet_goster"];
        var selected = plan.Queue.Where(name => presentationNames.Contains(name, StringComparer.Ordinal)).ToArray();
        if (selected.Length == 0) return plan;
        var dataCount = plan.Queue.Count(name => !presentationNames.Contains(name, StringComparer.Ordinal) && name != "debug_workflow" && name != "modulleri_ac");
        var tablePreferred = plan.Queue.Any(name => name is "satislari_gor" or "satis_detayi_getir" or "urun_muadillerini_getir" or "urunleri_karsilastir" or
            "hareket_dagilimi_analizi" or "karekod_tutarlilik_kontrolu" or "stok_portfoy_analizi" or "stok_analizini_gor" or
            "hasta_urun_ozeti" or "satis_dagilimi_analizi" or "karlilik_analizi" or "iskonto_analizi" or "iade_analizi" or
            "geri_kazanilacak_hastalar" or "ayni_gun_tekrar_satislari" or "receteli_satis_payi_analizi" or
            "kamu_hasta_odeme_analizi" or "yuksek_iskontolu_satislar" or "zararina_satis_satirlari" or
            "urun_kar_marji_analizi" or "hafta_gunu_satis_analizi" or "saatlik_satis_analizi" or "hasilat_yogunlugu_analizi" or
            "satis_kohort_analizi" or "haftalik_iade_orani" or "en_yuksek_hasilatli_gun" or
            "hasta_hasilat_yogunlugu" or "urun_satis_olmayan_aylar" or "urun_operasyonel_karsilastirma" or
            "sifir_kamu_ucretli_satislar" or "hasta_iadelerini_gor" or
            "abc_hasilat_siniflandirmasi" or "pasif_urunleri_filtrele");
        var desired = dataCount > 1 && selected.Contains("bolumlu_ozet_goster", StringComparer.Ordinal)
            ? "bolumlu_ozet_goster"
            : tablePreferred ? "tabloda_goster" : selected[^1];
        var queue = plan.Queue.Where(name => !presentationNames.Contains(name, StringComparer.Ordinal)).ToList();
        queue.Add(desired);
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(plan.WorkflowInputs, StringComparer.Ordinal);
        var selectedArguments = selected.Reverse().Select(name => inputs.GetValueOrDefault(name))
            .FirstOrDefault(value => value is not null);
        var title = selectedArguments?.GetValueOrDefault("title") ?? selected.Reverse()
            .Select(name => inputs.GetValueOrDefault(name)?.GetValueOrDefault("title"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        foreach (var name in presentationNames) inputs.Remove(name);
        var desiredArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(title)) desiredArguments["title"] = title;
        if (desired == "tabloda_goster" && selectedArguments is not null)
        {
            foreach (var key in new[] { "color_modes", "default_color_mode" })
                if (selectedArguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    desiredArguments[key] = value;
        }
        inputs[desired] = desiredArguments;
        return plan with { Queue = queue, WorkflowInputs = inputs };
    }

    private static bool AnswerSignalsCapabilityGap(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        var normalized = NormalizeIntent(answer);
        return normalized.Contains("kullanilabilir bir is akisi", StringComparison.Ordinal) ||
               normalized.Contains("bu istek icin is akisi", StringComparison.Ordinal) ||
               normalized.Contains("mevcut is akis", StringComparison.Ordinal) ||
               normalized.Contains("gerceklestiremiyorum", StringComparison.Ordinal) ||
               normalized.Contains("erisimim bulunmuyor", StringComparison.Ordinal) ||
               normalized.Contains("dogrudan yapamiyorum", StringComparison.Ordinal) ||
               normalized.Contains("dogrudan hesaplayamiyorum", StringComparison.Ordinal) ||
               normalized.Contains("dogrudan belirleyemiyorum", StringComparison.Ordinal) ||
               normalized.Contains("uygun bir is akisi", StringComparison.Ordinal) ||
               normalized.Contains("uygun analiz", StringComparison.Ordinal) ||
               normalized.Contains("yetenek bulunmuyor", StringComparison.Ordinal) ||
               normalized.Contains("kapsam disinda", StringComparison.Ordinal);
    }

    private static bool HasAllHistorySalesCue(string request)
    {
        var normalized = NormalizeIntent(request);
        return (normalized.Contains("tum satis", StringComparison.Ordinal) ||
                normalized.Contains("butun satis", StringComparison.Ordinal)) &&
               !Regex.IsMatch(normalized, @"\b(?:son\s+)?[0-9]+\s*(?:gun|gunluk|ay|aylik|yil|yillik)\b", RegexOptions.CultureInvariant);
    }

    private static string? ExtractPatientQuery(string request)
    {
        var normalized = NormalizeIntent(request).Trim();
        var match = Regex.Match(normalized,
            @"^(?<name>[a-z]+(?:\s+[a-z]+){1,3}?)(?:['’](?:in|un))?\s+" +
            @"(?:(?:hastasinin|hastanin|in|un|gecmis(?:teki)?|onceki|eski|tum|butun|son|bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on|[0-9]+|" +
            @"gun(?:de|luk|lukte|lukta|daki)?|ay(?:da|lik|likta|daki)?|yil(?:da|lik|likta|daki)?)\s+)*satis[a-z]*\b",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var words = match.Groups["name"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 2 or > 4) return null;
        var noise = new HashSet<string>(StringComparer.Ordinal)
        {
            "bu", "ayni", "o", "onceki", "secili", "secilen", "hasta", "hastanin", "hastasinin", "bugun", "bugunku", "son", "gecen", "ayki", "aylik", "receteli", "perakende",
            "tum", "butun", "satis", "satislari", "net", "brut", "kamu", "ucreti", "bulunan", "iskontolu", "iskontosuz", "indirimli", "indirimsiz", "ve"
        };
        if (words.Select(NormalizeIntent).Any(noise.Contains)) return null;
        var originalWords = Regex.Matches(request, @"[\p{L}]+", RegexOptions.CultureInvariant)
            .Select(value => value.Value)
            .Take(words.Length)
            .ToArray();
        if (originalWords.Length != words.Length) return null;
        var last = originalWords[^1];
        var lower = last.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        foreach (var suffix in new[] { "nın", "nin", "nun", "nün", "ın", "in", "un", "ün" })
        {
            if (!lower.EndsWith(suffix, StringComparison.Ordinal) || last.Length - suffix.Length < 2) continue;
            originalWords[^1] = last[..^suffix.Length];
            break;
        }
        return string.Join(' ', originalWords).ToLower(CultureInfo.GetCultureInfo("tr-TR"));
    }

    private static string? ExtractPatientContactQuery(string request)
    {
        var match = Regex.Match(request,
            @"^\s*(?<name>[\p{L}]+(?:\s+[\p{L}]+){1,3}?)(?:['’](?:nın|nin|nun|nün|in|ın|un|ün))?\s+(?:(?:hastasının|hastasinin)\s+)?(?:cep\s+(?:telefonu|numarası)|telefon(?:u|\s+numarası))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var words = match.Groups["name"].Value.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length is >= 2 and <= 4 ? string.Join(' ', words) : null;
    }

    private static string? ExtractPatientProductQuery(string request)
    {
        var match = Regex.Match(request,
            @"^\s*(?<name>[\p{L}]+(?:\s+[\p{L}]+){1,3}?)(?:['’]?(?:nın|nin|nun|nün|ın|in|un|ün))?\s+(?:(?:hastasının|hastasinin)\s+)?(?:(?:en\s+)?son\s+(?:satın\s+)?aldığı|aldığı)\s+(?:ilaç(?:lar|ları)?|ürün(?:ler|leri)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var words = match.Groups["name"].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 4) return null;
        var last = words[^1];
        var lower = last.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        foreach (var suffix in new[] { "nın", "nin", "nun", "nün", "ın", "in", "un", "ün" })
        {
            if (!lower.EndsWith(suffix, StringComparison.Ordinal) || last.Length - suffix.Length < 2) continue;
            words[^1] = last[..^suffix.Length].TrimEnd((char)39);
            break;
        }
        return string.Join(' ', words);
    }

    private static string? ExtractMedulaPatientQuery(string request)
    {
        // The dedicated past-medication grammar already understands phrases such as
        // "Asım Uras Yılmaz adlı hastanın geçmiş ilaçları".  Reuse it before the
        // broader anchor parser so Turkish patient wording cannot be mistaken for
        // another Medula reference term.
        if (ExtractPastMedicationPatientQuery(request) is { } medicationPatient)
            return medicationPatient;
        var anchor = Regex.Match(request,
            @"\b(?:ilaç\s+geçmiş|ilac\s+gecmis|geçmiş\s+(?:kullandığı\s+)?ilaç|gecmis\s+(?:kullandigi\s+)?ilac|" +
            @"kullandığı\s+ilaç|kullandigi\s+ilac|(?:medula\s+)?(?:hasta\s+)?(?:ilaç|ilac)\s+(?:listesi|listesini|ekranı|ekrani)|" +
            @"müstehak[\p{L}]*|mustehak[\p{L}]*|rapor[\p{L}]*|muayene[\p{L}]*|" +
            @"endikasyon\s+dışı|endikasyon\s+disi|reçete[\p{L}]*|recete[\p{L}]*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!anchor.Success || anchor.Index == 0) return null;
        var candidate = request[..anchor.Index].Trim(' ', '\t', '\r', '\n', ',', '.', ':', ';', '?', '!');
        for (var pass = 0; pass < 3; pass++)
        {
            var previous = candidate;
            candidate = Regex.Replace(candidate,
                @"\s+(?:(?:adlı\s+)?(?:hastasının|hastasinin|hastanın|hastanin)|" +
                @"bu\s+haftaki|bu\s+ayki|geçen\s+haftaki|gecen\s+haftaki|geçen\s+ayki|gecen\s+ayki|" +
                @"(?:en\s+)?son|tüm|tum|bütün|butun|aktif|geçmiş|gecmis|bitmiş|bitmis|yerel|meduladaki)\s*$",
                string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (candidate == previous) break;
        }
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 2 or > 4) return null;
        candidate = StripTurkishPossessiveFromLastWord(words);
        var normalized = NormalizeIntent(candidate);
        // Common invoice-group prefixes are filters, never patient names. Mixed
        // name/group wording is left to the router rather than partially parsed.
        if (ResolvePrescriptionInvoiceTypeCodes(normalized) is not null) return null;
        var noise = new HashSet<string>(StringComparer.Ordinal)
        {
            "medula", "en", "son", "bugun", "dun", "recete", "ac", "goster", "getir",
            "bu", "ay", "hafta", "kurum", "odemeli", "tum", "butun", "yerel", "veritabani"
        };
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(noise.Contains))
            return null;
        return candidate;
    }

    private static string? ExtractPastMedicationPatientQuery(string request)
    {
        var searchable = Regex.Replace(request,
            @"^\s*medula(?:['’]?(?:dan|den|da|de))?\s*", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = Regex.Match(searchable,
            @"^\s*(?<name>[\p{L}]+(?:\s+[\p{L}]+){1,3}?)(?:['’]?(?:nın|nin|nun|nün|ın|in|un|ün))?\s+" +
            @"(?:(?:adlı\s+)?(?:hastasının|hastanın)\s+)?(?:geçmiş(?:teki)?\s+(?:kullandığı\s+)?ilaç[\p{L}]*|gecmis(?:teki)?\s+(?:kullandigi\s+)?ilac[\p{L}]*|" +
            @"eski\s+(?:ilaç|ilac)[\p{L}]*|önceki\s+ilaç[\p{L}]*|onceki\s+ilac[\p{L}]*|" +
            @"kullandığı\s+ilaç[\p{L}]*|kullandigi\s+ilac[\p{L}]*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var words = match.Groups["name"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 2 or > 4) return null;
        return StripTurkishPossessiveFromLastWord(words);
    }

    private static string? ExtractGeneralPatientMedicationQuery(string request)
    {
        string[] patterns =
        [
            @"^\s*(?<name>[\p{L}]+(?:\s+[\p{L}]+){1,3}?)\s+adlı\s+hastanın\s+(?:ilaçlarını|ilaçları|ilaclarini|ilaclari)(?:\s+(?:getir|göster|goster|listele))?\s*[?.!]*\s*$",
            @"^\s*(?<name>[\p{L}]+(?:\s+[\p{L}]+){1,3}?)(?:['’]?(?:nın|nin|nun|nün|ın|in|un|ün))?\s+(?:(?:hastasının|hastasinin)\s+)?(?:ilaçlarını|ilaçları|ilaclarini|ilaclari)(?:\s+(?:getir|göster|goster|listele))?\s*[?.!]*\s*$"
        ];
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(request, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var words = match.Groups["name"].Value.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length is >= 2 and <= 4)
                return StripTurkishPossessiveFromLastWord(words);
        }
        return null;
    }

    private static string StripTurkishPossessiveFromLastWord(string[] words)
    {
        if (words.Length == 0) return string.Empty;
        var last = words[^1];
        var lower = last.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var hasApostrophe = last.Contains((char)39) || last.Contains((char)8217);
        var suffixes = hasApostrophe
            ? new[] { "nın", "nin", "nun", "nün", "ın", "in", "un", "ün" }
            : new[] { "ın", "in", "un", "ün", "nın", "nin", "nun", "nün" };
        foreach (var suffix in suffixes)
        {
            if (!lower.EndsWith(suffix, StringComparison.Ordinal) || last.Length - suffix.Length < 2) continue;
            words[^1] = last[..^suffix.Length].TrimEnd((char)39, (char)8217);
            break;
        }
        return string.Join(' ', words);
    }

    private static string? ExtractMedulaIdentifier(string request, string labelPattern)
    {
        var token = @"(?<value>(?=[A-Za-z0-9./-]{5,40}\b)(?=[A-Za-z0-9./-]*[0-9])[A-Za-z0-9./-]+)";
        var before = Regex.Match(request,
            token + $@"\s+(?:numaralı\s+|numarali\s+)?(?:Medula(?:daki|da)?\s+)?(?:{labelPattern})(?:sinin|sının|sunun|sünün|nin|nın|nun|nün|yi|yı|yu|yü|si|sı|su|sü)?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (before.Success) return before.Groups["value"].Value;
        var after = Regex.Match(request,
            $@"(?:{labelPattern})(?:sinin|sının|sunun|sünün|nin|nın|nun|nün|yi|yı|yu|yü|si|sı|su|sü)?\s*(?:no|numarası|numarasi)?\s*[:#]?\s*" + token,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return after.Success ? after.Groups["value"].Value : null;
    }

    private static string ExtractDiagnosisQuery(string request)
    {
        var match = Regex.Match(request,
            @"(?:medula(?:['’]?da)?\s+)?(?<query>[\p{L}0-9.-]{2,80})\s+tanı(?:sını|sini|yı|yi|sı|si)?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success) return match.Groups["query"].Value.Trim();
        return Regex.Replace(request,
                @"\b(?:medula(?:['’]?(?:da|dan))?|tanı(?:yı|sı|si|sını|sini)?|tani(?:yi|si|sini)?|kodu|açıklamasıyla|aciklamasiyla|ara|bul|göster|goster)\b",
                " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Replace("'", " ", StringComparison.Ordinal).Trim(' ', '.', ',');
    }

    private static (string Left, string Right)? ExtractMedicineComparison(string request)
    {
        var match = Regex.Match(request,
            @"^\s*(?<left>[\p{L}0-9][\p{L}0-9\s./-]{0,70}?)\s+(?:ile|ve)\s+(?<right>[\p{L}0-9][\p{L}0-9\s./-]{0,70}?)\s+(?:satış[\p{L}]*|satis[\p{L}]*)\b.*\b(?:karşılaştır[\p{L}]*|karsilastir[\p{L}]*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var left = Regex.Replace(match.Groups["left"].Value.Trim(),
            @"\s+(?:satışları|satislari|satışı|satisi|satış|satis)$",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        var right = Regex.Replace(match.Groups["right"].Value.Trim(),
            @"\s+(?:ürünlerinin?|urunlerinin?)?\s*son\s+.*$",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return left.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 1 and <= 6 &&
               right.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 1 and <= 6
            ? (left, right)
            : null;
    }

    private static IReadOnlyList<string> ExtractExactBarcodes(string request) =>
        Regex.Matches(request, @"(?<![0-9])[0-9]{8,14}(?![0-9])", RegexOptions.CultureInvariant)
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(20).ToArray();

    private static int ExtractRequestedLatestCount(string normalizedRequest)
    {
        var match = Regex.Match(normalizedRequest, @"\b(?:en\s+)?son\s+(?<count>[0-9]{1,3}|bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on)\b", RegexOptions.CultureInvariant);
        if (!match.Success) return 1;
        if (int.TryParse(match.Groups["count"].Value, out var number)) return Math.Clamp(number, 1, 200);
        return match.Groups["count"].Value switch { "bir" => 1, "iki" => 2, "uc" => 3, "dort" => 4, "bes" => 5, "alti" => 6, "yedi" => 7, "sekiz" => 8, "dokuz" => 9, "on" => 10, _ => 1 };
    }

    private static int? ExtractPeriodNumber(string normalizedRequest, string unit)
    {
        var match = Regex.Match(normalizedRequest,
            $@"\b(?:son\s+)?(?<count>[0-9]{{1,3}})\s*{Regex.Escape(unit)}(?:ki|de|da|luk|lik|lukte|likta|lukta|likte|daki|deki)?\b",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : null;
    }

    private static string? ExtractStockMedicineName(string request)
    {
        var patterns = new[]
        {
            @"^\s*(?<name>[\p{L}0-9][\p{L}0-9\s./-]{0,70}?)(?:['’]?[dt][ae]n)\s+(?:kaç|kac)\s+(?:eksik|eksiği|eksiğim|eksiğimiz|eksiğiniz)\s+var\b",
            @"^\s*(?:kaç|kac)\s+(?:tane\s+)?(?<name>[\p{L}0-9][\p{L}0-9\s./-]{0,70}?)\s+(?:stokta\s+)?var\b",
            @"^\s*(?<name>[\p{L}0-9][\p{L}0-9\s./-]{0,70}?)\s+stokta\s+(?:kaç|kac)(?:\s+tane)?\s+var\b"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(request, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 1 and <= 6) return name;
        }
        return null;
    }

    private static AiWorkflowPlan NormalizePharmacySpelling(AiWorkflowPlan plan, string originalRequest)
    {
        if (plan.MedicineNames.Count == 0 || !Regex.IsMatch(NormalizeIntent(originalRequest), @"\bkac\b", RegexOptions.CultureInvariant))
            return plan;

        var forms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eksik"] = "eksik",
            ["eksigi"] = "eksiği",
            ["eksigim"] = "eksiğim",
            ["eksigimiz"] = "eksiğimiz",
            ["eksiginiz"] = "eksiğiniz"
        };
        Match? bestMatch = null;
        string? replacement = null;
        var bestDistance = int.MaxValue;
        foreach (Match word in Regex.Matches(originalRequest, @"\p{L}+", RegexOptions.CultureInvariant))
        {
            var normalizedWord = NormalizeIntent(word.Value);
            foreach (var form in forms)
            {
                var distance = DamerauLevenshteinDistance(normalizedWord, form.Key, 2);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestMatch = word;
                replacement = form.Value;
            }
        }
        if (bestMatch is null || replacement is null || bestDistance > 2) return plan;
        var corrected = originalRequest.Remove(bestMatch.Index, bestMatch.Length).Insert(bestMatch.Index, replacement);
        return plan with { CorrectedRequest = corrected, CorrectionConfidence = 1.0 };
    }

    private static int DamerauLevenshteinDistance(string left, string right, int maximum)
    {
        if (Math.Abs(left.Length - right.Length) > maximum) return maximum + 1;
        var distances = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) distances[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) distances[0, j] = j;
        for (var i = 1; i <= left.Length; i++)
        for (var j = 1; j <= right.Length; j++)
        {
            var cost = left[i - 1] == right[j - 1] ? 0 : 1;
            distances[i, j] = Math.Min(
                Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                distances[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                distances[i, j] = Math.Min(distances[i, j], distances[i - 2, j - 2] + cost);
        }
        return distances[left.Length, right.Length];
    }

    private static bool HasPatientMutationCue(string request)
    {
        var normalized = NormalizeIntent(request);
        return Regex.IsMatch(normalized,
            @"\b(?:degistir|guncelle|kaydet|sil|duzelt|ekle)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool HasLocalProductPriceCue(string request)
    {
        var normalized = NormalizeIntent(request);
        if (normalized.Contains("hasilat", StringComparison.Ordinal) || normalized.Contains("kampanya", StringComparison.Ordinal) ||
            normalized.Contains("canli", StringComparison.Ordinal) || normalized.Contains("depolarda", StringComparison.Ordinal) ||
            normalized.Contains("depodan", StringComparison.Ordinal)) return false;
        return normalized.Contains("fiyat", StringComparison.Ordinal) || normalized.Contains("ucret", StringComparison.Ordinal) ||
               normalized.Contains("kac para", StringComparison.Ordinal) || normalized.Contains("ne kadar", StringComparison.Ordinal);
    }

    private static string NormalizeIntent(string value) => value.ToLower(CultureInfo.GetCultureInfo("tr-TR"))
        .Replace('ü', 'u').Replace('ı', 'i').Replace('ş', 's').Replace('ç', 'c').Replace('ö', 'o').Replace('ğ', 'g');

    private static bool HasNearestMovementCue(string request)
    {
        var value = request.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        return value.Contains("en yakın", StringComparison.Ordinal) ||
               value.Contains("en yakin", StringComparison.Ordinal) ||
               value.Contains("en son hareket", StringComparison.Ordinal) ||
               value.Contains("son hareketi", StringComparison.Ordinal) ||
               value.Contains("latest movement", StringComparison.Ordinal) ||
               value.Contains("nearest movement", StringComparison.Ordinal);
    }

    private JsonElement? GetRememberedSelection(string sessionId)
    {
        lock (sync)
            return rememberedMedicineSelections.TryGetValue(sessionId, out var selection)
                ? selection.Clone()
                : null;
    }

    private JsonElement? GetRememberedPatientSelection(string sessionId)
    {
        lock (sync)
            return rememberedPatientSelections.TryGetValue(sessionId, out var selection)
                ? selection.Clone()
                : null;
    }

    private JsonElement? GetRememberedPrescriptionSelection(string sessionId)
    {
        lock (sync)
            return rememberedPrescriptionSelections.TryGetValue(sessionId, out var selection)
                ? selection.Clone()
                : null;
    }

    private static object? CompactPrescriptionContext(JsonElement? value)
    {
        if (value is not { } output || !TryGetPrescriptionPayload(output, out var prescription)) return null;
        return new
        {
            prescription_number = WorkflowData.Text(prescription, "prescription_number"),
            electronic_prescription_number = WorkflowData.Text(prescription, "electronic_prescription_number"),
            patient_tc = WorkflowData.Text(prescription, "patient_tc"),
            patient_name = WorkflowData.Text(prescription, "patient_name")
        };
    }

    private static bool RememberedSelectionMatchesRequest(string request, JsonElement selection)
    {
        var requestedNumbers = Regex.Matches(request, "[0-9]+", RegexOptions.CultureInvariant)
            .Select(match => int.TryParse(match.Value, out var value) ? value : -1)
            .Where(value => value >= 0).ToArray();
        if (requestedNumbers.Length < 2) return true;
        if (!selection.TryGetProperty("selected_products", out var products) || products.ValueKind != JsonValueKind.Array)
            return false;
        var requestedDose = requestedNumbers[0];
        var requestedPackage = requestedNumbers[1];
        var doseCandidates = requestedDose is >= 10 and < 100
            ? new[] { requestedDose * 10, requestedDose }
            : new[] { requestedDose };
        return products.EnumerateArray().Any(product =>
        {
            var name = product.TryGetProperty("name", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            var numbers = Regex.Matches(name, "[0-9]+", RegexOptions.CultureInvariant)
                .Select(match => int.TryParse(match.Value, out var parsed) ? parsed : -1)
                .Where(parsed => parsed >= 0).ToArray();
            return numbers.Length > 0 && doseCandidates.Any(numbers.Contains) && numbers[^1] == requestedPackage;
        });
    }

    private string? GetRecentWorkflowFacts(string sessionId)
    {
        lock (sync) return recentWorkflowFacts.GetValueOrDefault(sessionId);
    }

    private JsonElement? GetRecentBarcodeContext(string sessionId)
    {
        lock (sync)
        {
            if (!recentPeriodWorkflows.TryGetValue(sessionId, out var replay) ||
                !replay.Parameters.TryGetValue("barcodes", out var value)) return null;
            var barcodes = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
            return barcodes.Length == 0 ? null : JsonSerializer.SerializeToElement(new { barcodes });
        }
    }
    private IReadOnlyDictionary<string, JsonElement> GetRecentWorkflowOutputs(string sessionId)
    {
        lock (sync)
            return recentWorkflowOutputs.TryGetValue(sessionId, out var outputs)
                ? outputs.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private bool TryAnswerRecentResultMetricQuestion(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowAssistantResult result)
    {
        result = null!;
        var text = NormalizeIntent(request.UserMessage);
        if (!ReferencesDisplayedResult(text) || !IsSimpleRetainedMetricQuestion(text)) return false;

        var operation = text.Contains("ortalama", StringComparison.Ordinal) ? "average"
            : Regex.IsMatch(text, @"\b(?:en\s+yuksek|en\s+fazla|maksimum)\b", RegexOptions.CultureInvariant) ? "maximum"
            : Regex.IsMatch(text, @"\b(?:en\s+dusuk|en\s+az|minimum)\b", RegexOptions.CultureInvariant) ? "minimum"
            : text.Contains("toplam", StringComparison.Ordinal) || text.Contains("ne kadar", StringComparison.Ordinal) ? "sum"
            : Regex.IsMatch(text, @"\b(?:kac\s+tane|kac\s+adet|sayisi)\b", RegexOptions.CultureInvariant) ? "count"
            : null;
        if (operation is null) return false;

        var outputs = GetRecentWorkflowOutputs(request.SessionId);
        if (outputs.Count == 0) return false;

        if (operation == "count" && Regex.IsMatch(text,
                @"\b(?:satis|kayit|satir|sonuc)(?:lar|lari|i)?\b", RegexOptions.CultureInvariant) &&
            TryGetRecentRows(outputs, [], out var countWorkflow, out var countRows))
        {
            var count = countRows.Count;
            var countValue = count.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
            var countMessage = $"Son gösterilen sonuçta {countValue} kayıt var.";
            result = BuildRecentMetricAnswer(configuration, countMessage, "Kayıt Sayısı", countValue,
                count, countWorkflow);
            return true;
        }

        if (operation == "count") return false;
        var metric = ResolveRecentMetric(text);
        if (metric is null || !TryGetRecentRows(outputs, metric.Value.Keys, out var workflowName, out var rows) ||
            !TryReadDecimalColumn(rows, metric.Value.Keys, out var values))
            return false;

        var calculated = operation switch
        {
            "average" => values.Average(),
            "maximum" => values.Max(),
            "minimum" => values.Min(),
            _ => values.Sum()
        };
        var operationLabel = operation switch
        {
            "average" => "Ortalama",
            "maximum" => "En Yüksek",
            "minimum" => "En Düşük",
            _ => "Toplam"
        };
        var formatted = metric.Value.IsCurrency
            ? $"{calculated.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"))} ₺"
            : calculated.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));
        var message = $"Son gösterilen {rows.Count} kayıttaki {operationLabel.ToLower(CultureInfo.GetCultureInfo("tr-TR"))} " +
                      $"{metric.Value.Label.ToLower(CultureInfo.GetCultureInfo("tr-TR"))} {formatted}.";
        result = BuildRecentMetricAnswer(configuration, message, $"{operationLabel} {metric.Value.Label}", formatted,
            rows.Count, workflowName);
        return true;
    }

    private static AiWorkflowAssistantResult BuildRecentMetricAnswer(
        AiConfiguration configuration,
        string message,
        string title,
        string value,
        int rowCount,
        string workflowName) =>
        new(
            message,
            UsageByModel: [],
            DebugSummary: configuration.DebugMode
                ? $"Model calls for this contextual answer: 0 (calculated locally from {rowCount} saved row(s) produced by {workflowName})."
                : null,
            Content: new AiWorkflowContentPresentation(
                AiWorkflowContentKind.MetricCards,
                "Önceki Sonuç Özeti",
                [new AiWorkflowContentItem(
                    title,
                    $"{rowCount} sonuç satırı",
                    value,
                    "Değer önceki yanıtta gösterilen satırlardan hesaplandı; yeni bir veritabanı sorgusu yapılmadı.")]),
            WorkflowQueue: []);

    private static bool ReferencesDisplayedResult(string text) =>
        text.Contains("bunlar", StringComparison.Ordinal) ||
        text.Contains("bunlarda", StringComparison.Ordinal) ||
        text.Contains("bunlarin", StringComparison.Ordinal) ||
        text.Contains("bu satis", StringComparison.Ordinal) ||
        text.Contains("bu kayit", StringComparison.Ordinal) ||
        text.Contains("bu satir", StringComparison.Ordinal) ||
        text.Contains("bu liste", StringComparison.Ordinal) ||
        text.Contains("bu tablo", StringComparison.Ordinal) ||
        text.Contains("gosterilen", StringComparison.Ordinal) ||
        text.Contains("listelenen", StringComparison.Ordinal) ||
        text.Contains("onceki sonuc", StringComparison.Ordinal);

    private static (string Label, string[] Keys, bool IsCurrency)? ResolveRecentMetric(string text)
    {
        if (text.Contains("iskonto", StringComparison.Ordinal) || text.Contains("indirim", StringComparison.Ordinal))
            return ("İskonto", ["discount_amount", "discount_total"], true);
        if (text.Contains("net hasilat", StringComparison.Ordinal) || text.Contains("net tutar", StringComparison.Ordinal) ||
            text.Contains("net satis", StringComparison.Ordinal))
            return ("Net Tutar", ["transaction_net_amount", "requested_product_net_amount", "net_amount", "net_revenue"], true);
        if (text.Contains("brut", StringComparison.Ordinal))
            return ("Brüt Tutar", ["transaction_gross_amount", "requested_product_gross_amount", "gross_amount", "gross_revenue"], true);
        if (text.Contains("kamu", StringComparison.Ordinal) || text.Contains("kurum", StringComparison.Ordinal))
            return ("Kurum Tutarı", ["institution_amount", "institution_revenue"], true);
        if (Regex.IsMatch(text, @"\bkar(?:i|in|lar)?\b", RegexOptions.CultureInvariant))
            return ("Kâr", ["profit"], true);
        if (text.Contains("urun adedi", StringComparison.Ordinal) || text.Contains("kutu", StringComparison.Ordinal) ||
            Regex.IsMatch(text, @"\badet(?:i|ler)?\b", RegexOptions.CultureInvariant))
            return ("Ürün Adedi", ["requested_product_quantity", "transaction_quantity", "product_quantity", "quantity"], false);
        return null;
    }

    private static bool TryGetRecentRows(
        IReadOnlyDictionary<string, JsonElement> outputs,
        IReadOnlyList<string> candidateKeys,
        out string workflowName,
        out IReadOnlyList<JsonElement> rows)
    {
        rows = [];
        if (!PreviousResultData.TryGetSource(outputs, null, out workflowName, out var source)) return false;
        rows = source.GetProperty("rows").EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object).ToArray();
        var currentRows = rows;
        // Missing values in the latest result never authorize looking at an older dataset.
        return candidateKeys.Count == 0 || rows.Count > 0 &&
            candidateKeys.Any(key => currentRows.All(row => row.TryGetProperty(key, out _)));
    }

    private static bool TryReadDecimalColumn(
        IReadOnlyList<JsonElement> rows,
        IReadOnlyList<string> candidateKeys,
        out IReadOnlyList<decimal> values)
    {
        foreach (var key in candidateKeys)
        {
            var parsed = new List<decimal>(rows.Count);
            foreach (var row in rows)
            {
                if (!row.TryGetProperty(key, out var value) || !TryReadDecimal(value, out var number))
                {
                    parsed.Clear();
                    break;
                }
                parsed.Add(number);
            }
            if (parsed.Count == rows.Count)
            {
                values = parsed;
                return true;
            }
        }
        values = [];
        return false;
    }

    private static bool TryReadDecimal(JsonElement value, out decimal number)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out number);
        if (value.ValueKind != JsonValueKind.String)
        {
            number = 0;
            return false;
        }
        return PreviousResultData.TryDecimal(value.GetString(), out number);
    }

    private object? GetRecentWorkflowContext(string sessionId)
    {
        var outputs = GetRecentWorkflowOutputs(sessionId);
        if (outputs.Count == 0) return null;
        var sources = outputs.Where(pair => pair.Key != PreviousResultData.ContextKey &&
                pair.Value.ValueKind == JsonValueKind.Object &&
                pair.Value.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            .Reverse().Take(3).Reverse().ToArray();
        return new
        {
            latest_source = sources.LastOrDefault().Key,
            datasets = sources.Select(pair => PreviousResultData.BuildPreview(pair.Key, pair.Value)).ToArray()
        };
    }

    private AiWorkflowPlan NormalizePatientFromRecentResult(
        AiWorkflowPlan plan,
        string sessionId,
        string request)
    {
        if (!plan.Queue.Contains("hasta_eslestir", StringComparer.Ordinal) ||
            !TryResolvePatientFromRecentRows(sessionId, request, out var patientName))
            return plan;
        var inputs = plan.WorkflowInputs.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        inputs["hasta_eslestir"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = PatientMatchWorkflow.TurkishUpper(patientName)
        };
        return plan with { WorkflowInputs = inputs };
    }

    private string? ResolvePatientQuery(AiRequestContext request) =>
        TryResolvePatientFromRecentRows(request.SessionId, request.UserMessage, out var patientName)
            ? patientName
            : ExtractMedulaPatientQuery(request.UserMessage);

    private bool TryResolvePatientFromRecentRows(string sessionId, string request, out string patientName)
    {
        patientName = string.Empty;
        if (!HasRecentOutputCue(request)) return false;
        var normalizedRequest = NormalizeIntent(request);
        var requestTokens = SignificantContextTokens(normalizedRequest).ToHashSet(StringComparer.Ordinal);
        // A reference to "this list" means the latest displayed dataset, including
        // an empty one. Missing patient information must not resurrect an older,
        // unrelated patient's record from an earlier step in the same queue.
        if (!PreviousResultData.TryGetSource(GetRecentWorkflowOutputs(sessionId), null, out _, out var output))
            return false;
        var rows = output.GetProperty("rows").EnumerateArray().ToArray();
        var candidates = rows.Where(row => row.ValueKind == JsonValueKind.Object &&
            WorkflowData.Text(row, "patient_name").Trim().Length > 0).ToArray();
        if (candidates.Length == 0) return false;
        var matching = new List<string>();
        if (Regex.IsMatch(normalizedRequest, @"\b(?:ilk|son) satir\b", RegexOptions.CultureInvariant))
        {
            var row = Regex.IsMatch(normalizedRequest, @"\bilk satir\b", RegexOptions.CultureInvariant) ? rows[0] : rows[^1];
            if (row.ValueKind != JsonValueKind.Object) return false;
            matching.Add(WorkflowData.Text(row, "patient_name").Trim());
        }
        else if (Regex.IsMatch(normalizedRequest, @"\bilk hasta\b", RegexOptions.CultureInvariant))
            matching.Add(WorkflowData.Text(candidates[0], "patient_name").Trim());
        else if (Regex.IsMatch(normalizedRequest, @"\bson hasta\b", RegexOptions.CultureInvariant))
            matching.Add(WorkflowData.Text(candidates[^1], "patient_name").Trim());
        else
            matching.AddRange(candidates.Where(row => RowMatchesRequest(row, requestTokens, normalizedRequest))
                .Select(row => WorkflowData.Text(row, "patient_name").Trim()));
        var distinct = matching.Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (distinct.Length != 1) return false;
        patientName = distinct[0];
        return true;
    }

    private static bool RowMatchesRequest(
        JsonElement row,
        IReadOnlySet<string> requestTokens,
        string normalizedRequest)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (property.Name is "patient_name" or "patient_tc" or "patient_record_id" ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
                continue;
            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            var normalizedValue = NormalizeIntent(value).Trim();
            if (normalizedValue.Length >= 4 && normalizedRequest.Contains(normalizedValue, StringComparison.Ordinal))
                return true;
            if (SignificantContextTokens(normalizedValue).Any(requestTokens.Contains))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> SignificantContextTokens(string value)
    {
        var ignored = new HashSet<string>(
        [
            "satis", "recete", "receteli", "perakende", "ilac", "urun", "tablet", "kapsul",
            "hasta", "goster", "getir", "liste", "listedeki", "bunlar", "bunlardaki", "alan"
        ], StringComparer.Ordinal);
        return Regex.Split(value, @"[^a-z0-9]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length >= 4 && !ignored.Contains(token));
    }

    private void RememberMedicineSelection(string sessionId, string workflowName, JsonElement output)
    {
        if (!(workflowName.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal) ||
              workflowName == PreviousResultWorkflow.WorkflowName && WorkflowData.Text(output, "resolved_entity") == "medicine") ||
            !output.TryGetProperty("barcodes", out var barcodes) ||
            barcodes.ValueKind != JsonValueKind.Array || barcodes.GetArrayLength() == 0)
            return;
        lock (sync)
        {
            rememberedMedicineSelections[sessionId] = output.Clone();
            recentPurchaseSubjects.Remove(sessionId);
        }
    }

    private void RememberPatientSelection(string sessionId, string workflowName, JsonElement output)
    {
        if (!(workflowName == "hasta_eslestir" || workflowName == PreviousResultWorkflow.WorkflowName &&
              WorkflowData.Text(output, "resolved_entity") == "patient") ||
            !output.TryGetProperty("patient_record_id", out var patientId) ||
            !int.TryParse(patientId.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return;
        lock (sync) rememberedPatientSelections[sessionId] = output.Clone();
    }

    private void RememberPrescriptionSelection(string sessionId, string workflowName, JsonElement output)
    {
        if (!(workflowName is "medula_son_receteyi_ac" or "medula_recete_bilgisi_getir" ||
              workflowName == PreviousResultWorkflow.WorkflowName && WorkflowData.Text(output, "resolved_entity") == "prescription") ||
            !TryGetPrescriptionPayload(output, out var prescription) ||
            string.IsNullOrWhiteSpace(WorkflowData.Text(prescription, "prescription_number")))
            return;
        lock (sync) rememberedPrescriptionSelections[sessionId] = output.Clone();
    }

    private static bool TryGetPrescriptionPayload(JsonElement output, out JsonElement prescription)
    {
        if (output.ValueKind == JsonValueKind.Object &&
            output.TryGetProperty("prescription_number", out var number) && number.ValueKind == JsonValueKind.String)
        {
            prescription = output.Clone();
            return true;
        }
        if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("prescription", out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            prescription = nested.Clone();
            return true;
        }
        prescription = default;
        return false;
    }

    private static bool RequiresPatientContext(string workflowName) => workflowName is
        "hasta_iletisim_bilgisi_getir" or "hasta_urun_ozeti" or "hasta_son_urunleri_getir" or
        "hasta_satis_zaman_cizelgesi" or "hasta_alisveris_araligi" or "hasta_satis_iceriklerini_gor" or
        "medula_hasta_klinik_bilgisi_getir" or "medula_hasta_recetelerini_getir" or
        "medula_hasta_rapor_detayini_getir" or "medula_erecete_sorgula" or
        "medula_son_receteyi_ac";

    private void RememberRecentWorkflowFacts(string sessionId, AiWorkflowRunState state)
    {
        var facts = state.Trace
            .Where(entry => entry.Status is AiWorkflowStepStatus.Completed or AiWorkflowStepStatus.Failed or AiWorkflowStepStatus.MissingTool &&
                            entry.WorkflowName is not ("debug_workflow" or "tabloda_goster") &&
                            !entry.WorkflowName.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal))
            .Select(entry => $"{entry.WorkflowName} [{entry.Status}]: {entry.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var replayable = state.Trace.LastOrDefault(entry =>
            entry.Status == AiWorkflowStepStatus.Completed &&
            entry.WorkflowName is "hareketleri_gor" or "satislari_gor" or "satis_hasilati_ozeti" or
                "hasta_satis_iceriklerini_gor" or "hasta_urun_ozeti" or "iskonto_analizi" or
                "recete_rapor_durumu_ozeti" or "medula_receteleri_ara");
        var replayName = replayable?.WorkflowName ?? state.Trace.LastOrDefault(entry =>
            entry.Status == AiWorkflowStepStatus.Failed &&
            entry.WorkflowName is "satislari_gor" or "satis_hasilati_ozeti" or "hasta_satis_iceriklerini_gor")?.WorkflowName;
        lock (sync)
        {
            if (facts.Length > 0) recentWorkflowFacts[sessionId] = string.Join(" | ", facts);
            var completedOutputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in state.Trace.Where(entry => entry.Status == AiWorkflowStepStatus.Completed &&
                         entry.WorkflowName is not ("debug_workflow" or "tabloda_goster" or "bilgi_kartlari_goster" or
                             "metrik_kartlari_goster" or "sirali_liste_goster" or "zaman_cizelgesinde_goster" or "bolumlu_ozet_goster") &&
                         entry.Output.ValueKind == JsonValueKind.Object &&
                         entry.Output.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array))
            {
                completedOutputs.Remove(entry.WorkflowName);
                completedOutputs[entry.WorkflowName] = entry.Output.Clone();
            }
            if (completedOutputs.Count > 0)
            {
                var latest = completedOutputs.Last();
                // Derived answers retain their input lineage, so a total does not erase
                // the displayed rows it summarized. Fresh queries replace the lineage.
                // Keep a bounded local chain; the default result remains the latest one.
                var derived = state.Trace.Any(entry => entry.Status == AiWorkflowStepStatus.Completed &&
                    entry.WorkflowName is PreviousResultWorkflow.WorkflowName or "onceki_satislarin_iceriklerini_gor");
                if (derived && PreviousResultData.TryGetSource(state.Outputs,
                        state.Plan.WorkflowInputs.GetValueOrDefault(PreviousResultWorkflow.WorkflowName)?.GetValueOrDefault("source_workflow"),
                        out _, out var parent))
                {
                    var retained = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    for (var index = PreviousResultData.AncestorKeys.Count - 1; index > 0; index--)
                        if (state.Outputs.TryGetValue(PreviousResultData.AncestorKeys[index - 1], out var ancestor))
                            retained[PreviousResultData.AncestorKeys[index]] = ancestor.Clone();
                    retained[PreviousResultData.AncestorKeys[0]] = parent.Clone();
                    foreach (var pair in completedOutputs) retained[pair.Key] = pair.Value;
                    completedOutputs = retained;
                }
                var snapshot = System.Text.Json.Nodes.JsonNode.Parse(latest.Value.GetRawText())!.AsObject();
                var metadata = snapshot["metadata"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                metadata["source_workflow"] = latest.Key;
                if (snapshot["metadata"] is null) snapshot["metadata"] = metadata;
                completedOutputs[PreviousResultData.ContextKey] = JsonSerializer.SerializeToElement(snapshot);
                recentWorkflowOutputs[sessionId] = completedOutputs;
            }
            if (replayName is not null)
            {
                var parameters = new Dictionary<string, string>(
                    state.Plan.WorkflowInputs.GetValueOrDefault(replayName)
                    ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                var patientRecordId = state.Outputs.Values.Reverse()
                    .Select(output => output.TryGetProperty("patient_record_id", out var value) &&
                                      int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                        ? id
                        : 0)
                    .FirstOrDefault(id => id > 0);
                if (patientRecordId > 0)
                    parameters["patient_record_id"] = patientRecordId.ToString(CultureInfo.InvariantCulture);
                var resolvedBarcodes = state.Outputs.Values.Reverse()
                    .Where(output => output.TryGetProperty("barcodes", out var values) && values.ValueKind == JsonValueKind.Array)
                    .SelectMany(output => output.GetProperty("barcodes").EnumerateArray())
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()?.Trim() ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (resolvedBarcodes.Length > 0)
                    parameters["barcodes"] = string.Join(',', resolvedBarcodes);
                var resolvedContext = state.Outputs
                    .Where(pair => registry.Get(pair.Key)?.Definition.ProvidesResolvedContext == true)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
                recentPeriodWorkflows[sessionId] = new RecentPeriodWorkflow(
                    replayName,
                    parameters,
                    resolvedContext);
            }
        }
    }

    private static bool HasRecentOutputCue(string request)
    {
        var text = NormalizeIntent(request);
        return text.Contains("ilk ", StringComparison.Ordinal) || text.Contains("bunlar", StringComparison.Ordinal) ||
               text.Contains("bunlardan", StringComparison.Ordinal) || text.Contains("onceki", StringComparison.Ordinal) ||
               text.Contains("bu satis", StringComparison.Ordinal) || text.Contains("bu kayit", StringComparison.Ordinal) ||
               text.Contains("bu liste", StringComparison.Ordinal) || text.Contains("gosterilen", StringComparison.Ordinal) ||
               text.Contains("listelenen", StringComparison.Ordinal) ||
               text.Contains("aynis", StringComparison.Ordinal) || text.StartsWith("simdi ", StringComparison.Ordinal) ||
               text.Contains("ekle", StringComparison.Ordinal) || text.Contains("dahil et", StringComparison.Ordinal) ||
               text.Contains("renkleri", StringComparison.Ordinal) || text.Contains("pasif olan", StringComparison.Ordinal) ||
               text.Contains("yalniz", StringComparison.Ordinal) || text.Contains("sadece", StringComparison.Ordinal) ||
               text.Contains("birak", StringComparison.Ordinal) || text.Contains("bundan", StringComparison.Ordinal) ||
               text.Contains("birlikte goster", StringComparison.Ordinal) || text.Contains("tabloyu", StringComparison.Ordinal) ||
               text.Contains("tabloya", StringComparison.Ordinal) || text.Contains("bu tablo", StringComparison.Ordinal) ||
               text.Contains("stok kac gun", StringComparison.Ordinal);
    }

    private static bool HasSalesContentsCue(string normalizedRequest) =>
        normalizedRequest.Contains("icerik", StringComparison.Ordinal) ||
        normalizedRequest.Contains("urun satir", StringComparison.Ordinal) ||
        normalizedRequest.Contains("kalemler", StringComparison.Ordinal);

    private bool ShouldReuseRememberedPatient(string sessionId, AiWorkflowPlan plan, JsonElement selection)
    {
        var patientAware = plan.Queue.Any(name => RequiresPatientContext(name) ||
            name is "satislari_gor" or "satis_hasilati_ozeti" or "satis_dagilimi_analizi" or
                "karlilik_analizi" or "iskonto_analizi" or "iade_analizi");
        if (!patientAware) return false;
        // An explicit newly planned matcher wins over remembered context, including
        // elliptical requests such as "aynısı Deniz için". Never skip it just because
        // a prior patient happened to be selected in this conversation.
        var plannedQuery = plan.WorkflowInputs.GetValueOrDefault("hasta_eslestir")?.GetValueOrDefault("query");
        if (plan.Queue.Contains("hasta_eslestir", StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(plannedQuery))
        {
            if (ReferencesRememberedPatient(plannedQuery, selection)) return true;
            var selectedFirstName = selection.TryGetProperty("selected_patient", out var selectedPatient)
                ? NormalizeIntent(WorkflowData.Text(selectedPatient, "name")).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                : null;
            return NormalizeIntent(plannedQuery).Trim() == selectedFirstName &&
                   ReferencesRememberedPatient(plan.CorrectedRequest, selection);
        }
        if (ReferencesRememberedPatient(plan.CorrectedRequest, selection)) return true;
        if (Regex.IsMatch(NormalizeIntent(plan.CorrectedRequest),
                @"\b(?:(?:bu|ayni|o|onceki|secili|secilen)\s+hasta\p{L}*|(?:this|same|that|previous|selected)\s+patient(?:['’]s)?)\b", RegexOptions.CultureInvariant))
            return PatientSelectionId(selection) > 0;
        if (!HasRecentOutputCue(plan.CorrectedRequest)) return false;
        var explicitPatient = ExtractPatientQuery(plan.CorrectedRequest);
        return explicitPatient is null && PatientSelectionId(selection) > 0;
    }

    private static bool ReferencesRememberedPatient(string request, JsonElement selection)
    {
        if (!selection.TryGetProperty("selected_patient", out var patient) || patient.ValueKind != JsonValueKind.Object)
            return false;
        var name = NormalizeIntent(WorkflowData.Text(patient, "name")).Trim();
        if (name.Length == 0) return false;
        var requestText = NormalizeIntent(request).Trim();
        if (Regex.IsMatch(requestText, $@"\b{Regex.Escape(name)}(?:['’]?(?:in|nin|un|nun))?\b", RegexOptions.CultureInvariant))
            return true;
        var firstName = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return Regex.IsMatch(requestText,
            $@"^{Regex.Escape(firstName)}(?:['’]?(?:in|nin|un|nun))?\s+(?:tum|butun|gecmis|onceki|son|satis)",
            RegexOptions.CultureInvariant);
    }

    private static int PatientSelectionId(JsonElement selection) =>
        selection.TryGetProperty("patient_record_id", out var value) &&
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private bool TryAnswerRecentSalesScopeQuestion(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowAssistantResult result)
    {
        result = null!;
        var text = NormalizeIntent(request.UserMessage);
        var referencesPrevious = text.Contains("bunun icerisinde", StringComparison.Ordinal) ||
                                 text.Contains("bunun icinde", StringComparison.Ordinal) ||
                                 text.Contains("bu sonuca", StringComparison.Ordinal) ||
                                 text.Contains("onceki sonuc", StringComparison.Ordinal) ||
                                 text.Contains("bu tutardan", StringComparison.Ordinal);
        var asksInclusion = text.Contains("var mi", StringComparison.Ordinal) ||
                            text.Contains("dahil mi", StringComparison.Ordinal) ||
                            text.Contains("dahil edildi mi", StringComparison.Ordinal) ||
                            text.Contains("dusuldu mu", StringComparison.Ordinal);
        var asksPrescription = text.Contains("receteli", StringComparison.Ordinal);
        var asksRetail = text.Contains("perakende", StringComparison.Ordinal);
        var asksRefund = text.Contains("iade", StringComparison.Ordinal);
        if (!referencesPrevious || !asksInclusion || !asksPrescription && !asksRetail && !asksRefund) return false;

        RecentPeriodWorkflow? replay;
        lock (sync) replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        if (replay?.WorkflowName != "satis_hasilati_ozeti") return false;
        var saleTypes = replay.Parameters.GetValueOrDefault("sale_types")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeIntent).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(["retail", "prescription"], StringComparer.Ordinal);
        if (saleTypes.Count == 0) saleTypes.UnionWith(["retail", "prescription"]);
        var includesRefunds = !replay.Parameters.TryGetValue("include_refunds", out var refundText) ||
                              !string.Equals(refundText, "false", StringComparison.OrdinalIgnoreCase);
        var requestedIncluded = asksPrescription ? saleTypes.Contains("prescription")
            : asksRetail ? saleTypes.Contains("retail")
            : includesRefunds;
        var scope = saleTypes.SetEquals(["retail"]) ? "yalnızca perakende satışları"
            : saleTypes.SetEquals(["prescription"]) ? "yalnızca reçeteli satışları"
            : "hem perakende hem reçeteli satışları";
        var requestedLabel = asksPrescription ? "Reçeteli satışlar" : asksRetail ? "Perakende satışlar" : "İadeler";
        var answer = requestedIncluded ? "Evet. " : "Hayır. ";
        answer += $"Önceki sonuç {scope} kapsıyordu; {requestedLabel.ToLower(CultureInfo.GetCultureInfo("tr-TR"))} " +
                  (requestedIncluded ? "sonuca dahildi." : "sonuca dahil değildi.");
        answer += includesRefunds
            ? " İadeler dahil edildi ve net hasılattan düşüldü."
            : " İadeler bu sonuca dahil edilmedi.";
        result = new AiWorkflowAssistantResult(
            answer,
            UsageByModel: [],
            DebugSummary: configuration.DebugMode
                ? "Model calls for this contextual answer: 0 (saved revenue filters inspected)."
                : null);
        return true;
    }

    private bool TryAnswerRecentWorkflowScopeQuestion(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowAssistantResult result)
    {
        result = null!;
        var text = NormalizeIntent(request.UserMessage);
        var asksPatient = (text.Contains("kimi arat", StringComparison.Ordinal) ||
                           text.Contains("hangi hast", StringComparison.Ordinal)) &&
                          text.Contains("hasta", StringComparison.Ordinal);
        if (asksPatient && GetRememberedPatientSelection(request.SessionId) is { } selection)
        {
            var patient = selection.TryGetProperty("selected_patient", out var selected) &&
                          selected.ValueKind == JsonValueKind.Object
                ? selected
                : selection;
            var name = WorkflowData.Text(patient, "name").Trim();
            var tc = WorkflowData.Text(patient, "patient_tc").Trim();
            if (name.Length > 0)
            {
                var answer = tc.Length == 11
                    ? $"Hasta olarak {name} (T.C. {tc[..3]}********) eşleştirildi."
                    : $"Hasta olarak {name} eşleştirildi.";
                result = new AiWorkflowAssistantResult(
                    answer,
                    UsageByModel: [],
                    DebugSummary: configuration.DebugMode
                        ? "Model calls for this contextual answer: 0 (remembered patient inspected)."
                        : null,
                    WorkflowQueue: []);
                return true;
            }
        }

        var asksScope = text.Contains("hangi tarih", StringComparison.Ordinal) ||
                        text.Contains("tarih araligi", StringComparison.Ordinal) ||
                        text.Contains("hangi donem", StringComparison.Ordinal) ||
                        text.Contains("neyi filtre", StringComparison.Ordinal) ||
                        text.Contains("hangi filtre", StringComparison.Ordinal) ||
                        text.Contains("neleri filtre", StringComparison.Ordinal);
        var facts = asksScope ? GetRecentWorkflowFacts(request.SessionId) : null;
        if (string.IsNullOrWhiteSpace(facts)) return false;
        var friendlyFacts = string.Join(" ", facts.Split(" | ", StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Contains(": ", StringComparison.Ordinal)
                ? value[(value.IndexOf(": ", StringComparison.Ordinal) + 2)..]
                : value)
            .Distinct(StringComparer.Ordinal));
        result = new AiWorkflowAssistantResult(
            friendlyFacts,
            UsageByModel: [],
            DebugSummary: configuration.DebugMode
                ? "Model calls for this contextual answer: 0 (saved workflow scope inspected)."
                : null,
            WorkflowQueue: []);
        return true;
    }

    private static bool TryAnswerUnsupportedWorkflowRequest(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowAssistantResult result)
    {
        result = null!;
        var text = NormalizeIntent(request.UserMessage);
        var pharmacyData = Regex.IsMatch(text,
            @"\b(?:medula|recete|rapor|hasta|satis|ilac|karekod)\w*\b",
            RegexOptions.CultureInvariant);
        var asksTranslation = Regex.IsMatch(text,
            @"\b(?:cevir|tercume|translate)\w*\b",
            RegexOptions.CultureInvariant);
        var asksUnsupportedBulkDetail = text.Contains("recete", StringComparison.Ordinal) &&
            (text.Contains("her bir", StringComparison.Ordinal) ||
             text.Contains("tum recete", StringComparison.Ordinal)) &&
            (text.Contains("teker teker", StringComparison.Ordinal) ||
             text.Contains("tum icer", StringComparison.Ordinal) ||
             text.Contains("her yerini", StringComparison.Ordinal) ||
             text.Contains("tamamini oku", StringComparison.Ordinal));
        if (!pharmacyData || !asksTranslation && !asksUnsupportedBulkDetail) return false;

        const string message =
            "Bu isteğin tüm adımlarını güvenilir biçimde gerçekleştirebilen bir iş akışım yok. " +
            "Reçeteleri filtreleyip listeleyebilir veya tek bir reçetenin ayrıntısını gösterebilirim; " +
            "ancak reçetelerin tamamını tek tek okuyup topluca çeviremiyorum.";
        result = new AiWorkflowAssistantResult(
            message,
            UsageByModel: [],
            DebugSummary: configuration.DebugMode
                ? "Model calls for this answer: 0 (unsupported multi-step data transformation rejected before routing)."
                : null,
            WorkflowQueue: []);
        return true;
    }

    private bool TryAnswerAmbiguousPrescriptionCollectionRequest(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowAssistantResult result)
    {
        result = null!;
        var text = NormalizeIntent(request.UserMessage);
        var asksForUnspecifiedPrescription =
            text.Contains("receteler", StringComparison.Ordinal) &&
            text.Contains("bir", StringComparison.Ordinal) &&
            (text.Contains("icerik", StringComparison.Ordinal) ||
             text.Contains("icer", StringComparison.Ordinal) ||
             text.Contains("ilac", StringComparison.Ordinal)) &&
            ExtractMedulaIdentifier(request.UserMessage, "reçete|recete") is null;
        if (!asksForUnspecifiedPrescription) return false;

        const string message =
            "Hangi reçeteyi kastettiğinizi güvenilir biçimde seçemiyorum. " +
            "Yukarıdaki reçete tablosunda bir satıra tıklayarak içeriğini görebilir veya satıra sağ tıklayıp " +
            "“Reçeteyi Sohbette Aç” seçeneğini kullanabilirsiniz.";
        result = new AiWorkflowAssistantResult(
            message,
            UsageByModel: [],
            DebugSummary: configuration.DebugMode
                ? "Model calls for this answer: 0 (ambiguous prescription collection reference stopped before routing)."
                : null,
            WorkflowQueue: []);
        return true;
    }

    private bool TryBuildNamedPatientReportsState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        var patient = ResolvePatientQuery(request);
        if (!Regex.IsMatch(text, @"\brapor(?:lar|lari|larini)?\b", RegexOptions.CultureInvariant) ||
            text.Contains("raporlu", StringComparison.Ordinal) ||
            text.Contains("raporsuz", StringComparison.Ordinal) ||
            text.Contains("recete", StringComparison.Ordinal) ||
            text.Contains("detay", StringComparison.Ordinal) ||
            ExtractMedulaIdentifier(request.UserMessage, @"(?:medula\s+)?rapor") is not null ||
            patient is null ||
            registry.Get("hasta_eslestir") is null ||
            registry.Get("medula_hasta_klinik_bilgisi_getir") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var clinicalParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operation"] = "reports"
        };
        if (text.Contains("gecmis", StringComparison.Ordinal) ||
            text.Contains("bitmis", StringComparison.Ordinal) ||
            text.Contains("suresi dolmus", StringComparison.Ordinal))
            clinicalParameters["include_expired"] = "true";
        var queue = new List<string>
        {
            "hasta_eslestir", "medula_hasta_klinik_bilgisi_getir", "tabloda_goster"
        };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["hasta_eslestir"] = new Dictionary<string, string>
            {
                ["query"] = PatientMatchWorkflow.TurkishUpper(patient)
            },
            ["medula_hasta_klinik_bilgisi_getir"] = clinicalParameters,
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = $"{patient} — Medula Raporları"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal), [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildNamedPatientPrescriptionsState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        var patient = ResolvePatientQuery(request);
        if (!Regex.IsMatch(text, @"\brecete(?:ler|leri|lerini|si|sini)?\b", RegexOptions.CultureInvariant) ||
            text.Contains("receteli", StringComparison.Ordinal) ||
            text.Contains("rapor", StringComparison.Ordinal) ||
            text.Contains("en son", StringComparison.Ordinal) ||
            ExtractMedulaIdentifier(request.UserMessage, "reçete|recete") is not null ||
            patient is null ||
            registry.Get("hasta_eslestir") is null ||
            registry.Get("medula_hasta_recetelerini_getir") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var queue = new List<string>
        {
            "hasta_eslestir", "medula_hasta_recetelerini_getir", "tabloda_goster"
        };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var prescriptionInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ResolvePrescriptionDateRange(text) is { } range)
        {
            prescriptionInputs["start_date"] = range.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            prescriptionInputs["end_date"] = range.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (PrescriptionQrStatus(text) is { } qrStatus)
            prescriptionInputs["qr_status"] = qrStatus;
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["hasta_eslestir"] = new Dictionary<string, string>
            {
                ["query"] = PatientMatchWorkflow.TurkishUpper(patient)
            },
            ["medula_hasta_recetelerini_getir"] = prescriptionInputs,
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = $"{patient} — Reçeteler"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal), [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildRememberedPatientReportsState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        if (!Regex.IsMatch(text, @"^\s*(?:(?:bu|ayni)\s+hastanin\s+)?rapor(?:lar|lari|larini)?(?:\s+(?:goster|getir|listele))?\s*[?.!]*\s*$",
                RegexOptions.CultureInvariant) ||
            text.Contains("raporlu", StringComparison.Ordinal) ||
            text.Contains("raporsuz", StringComparison.Ordinal) ||
            GetRememberedPatientSelection(request.SessionId) is not { } remembered ||
            registry.Get("onceki_hasta_bilgisini_getir") is null ||
            registry.Get("medula_hasta_klinik_bilgisi_getir") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var selectedPatient = remembered.TryGetProperty("selected_patient", out var selected) &&
                              selected.ValueKind == JsonValueKind.Object
            ? selected
            : remembered;
        var patientName = WorkflowData.Text(selectedPatient, "name").Trim();
        if (patientName.Length == 0) patientName = "Hasta";

        var queue = new List<string>
        {
            "onceki_hasta_bilgisini_getir", "medula_hasta_klinik_bilgisi_getir", "tabloda_goster"
        };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["medula_hasta_klinik_bilgisi_getir"] = new Dictionary<string, string>
            {
                ["operation"] = "reports"
            },
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = $"{patientName} — Medula Raporları"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal), [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildLatestSalesState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        var match = Regex.Match(text,
                @"^\s*(?:en\s+)?son\s+(?:[0-9]{1,3}|bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on)\s+(?:(?<sale_type>receteli|perakende)\s+)?(?:(?<discounted>iskontolu|indirimli)\s+)?satis(?:lar|lari|i)?(?:\s+(?:goster|getir|listele))?\s*[?.!]*\s*$",
                RegexOptions.CultureInvariant);
        if (!match.Success ||
            registry.Get("satislari_gor") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var count = ExtractRequestedLatestCount(text);
        var discountedOnly = match.Groups["discounted"].Success;
        var requestedSaleType = match.Groups["sale_type"].Value switch
        {
            "receteli" => "prescription",
            "perakende" => "retail",
            _ => string.Empty
        };
        var typeLabel = requestedSaleType switch
        {
            "prescription" => "Reçeteli ",
            "retail" => "Perakende ",
            _ => string.Empty
        };
        var queue = new List<string> { "satislari_gor", "tabloda_goster" };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["satislari_gor"] = new Dictionary<string, string>
            {
                ["all_history"] = "true",
                ["include_refunds"] = "false",
                ["sort_direction"] = "newest_first",
                ["latest_only"] = "true",
                ["payment_types"] = discountedOnly ? "discount" : string.Empty,
                ["sale_types"] = requestedSaleType,
                ["limit"] = count.ToString(CultureInfo.InvariantCulture)
            },
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = discountedOnly ? $"Son {count} {typeLabel}İskontolu Satış" : $"Son {count} {typeLabel}Satış",
                ["color_modes"] = "sale_type|Satış türüne göre|amber",
                ["default_color_mode"] = "sale_type"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal), [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildPreviousSalesContentsState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        var asksForContents = Regex.IsMatch(text,
            @"\b(?:(?:ilac|urun|kalem)(?:lar|lari|lerini|leri|i)?|icerik\w*|satilmis|satildi|alinmis)\b",
            RegexOptions.CultureInvariant) &&
            Regex.IsMatch(text, @"\b(?:listele|goster|getir|hangi|neler|ne|var)\b", RegexOptions.CultureInvariant);
        if (!ReferencesDisplayedResult(text) || !asksForContents ||
            registry.Get("onceki_satislarin_iceriklerini_gor") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var previousOutputs = GetRecentWorkflowOutputs(request.SessionId);
        if (!previousOutputs.ContainsKey("satislari_gor")) return false;
        var queue = new List<string> { "onceki_satislarin_iceriklerini_gor", "tabloda_goster" };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = "Gösterilen Satışlardaki İlaçlar",
                ["color_modes"] = "sale_type|Satış türüne göre|amber",
                ["default_color_mode"] = "sale_type"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(
            request,
            plan,
            queue,
            0,
            previousOutputs.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            [],
            [],
            configuration.RouterModel,
            true);
        return true;
    }

    private bool TryBuildDiscountedSalesState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage).Trim();
        var asksForDiscountedSales =
            (text.Contains("indirimli", StringComparison.Ordinal) ||
             text.Contains("iskontolu", StringComparison.Ordinal)) &&
            Regex.IsMatch(text, @"\bsatis(?:lar|lari|i)?\b", RegexOptions.CultureInvariant) &&
            Regex.IsMatch(text, @"\b(?:ac|goster|getir|listele)\b", RegexOptions.CultureInvariant);
        var asksForAggregation = Regex.IsMatch(text,
            @"\b(?:urun|ilac|hasta|kullanici|ay|gun|dagilim|oran|karsilastir|en\s+cok|toplam|hasilat)\b",
            RegexOptions.CultureInvariant);
        // Fast paths are exact optimizations, not keyword routers. Richer requests
        // (dates, patient, sale type, negation, ordering) must retain model-planned filters.
        if (!Regex.IsMatch(text, @"^(?:indirimli|iskontolu)(?:\s+olan)?\s+satis(?:lar|lari|i)?\s+(?:ac|goster|getir|listele)[.!?]*$",
                RegexOptions.CultureInvariant) || !asksForDiscountedSales || asksForAggregation ||
            registry.Get("satislari_gor") is null || registry.Get("tabloda_goster") is null)
            return false;

        RecentPeriodWorkflow? replay;
        lock (sync) replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        var reusesSales = replay?.WorkflowName == "satislari_gor";
        var parameters = reusesSales
            ? new Dictionary<string, string>(replay!.Parameters, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["period_days"] = "30",
                ["include_refunds"] = "false",
                ["sort_direction"] = "newest_first",
                ["limit"] = "100"
            };
        parameters["payment_types"] = "discount";
        parameters["include_refunds"] = "false";
        parameters["sort_direction"] = "newest_first";
        if (!parameters.ContainsKey("limit")) parameters["limit"] = "100";

        var latestOnly = parameters.TryGetValue("latest_only", out var latest) &&
                         string.Equals(latest, "true", StringComparison.OrdinalIgnoreCase);
        var count = int.TryParse(parameters.GetValueOrDefault("limit"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedCount) ? parsedCount : 100;
        var title = latestOnly ? $"Son {count} İskontolu Satış" : "İskontolu Satışlar";
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["satislari_gor"] = parameters,
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = title,
                ["color_modes"] = "sale_type|Satış türüne göre|amber",
                ["default_color_mode"] = "sale_type"
            }
        };
        var queue = new List<string> { "satislari_gor", "tabloda_goster" };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var resolvedContext = reusesSales
            ? replay!.ResolvedContext.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0, resolvedContext, [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildGeneralPrescriptionSearchState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        if (!IsSimplePrescriptionSearchRequest(text, continuation: false) ||
            text.Contains("receteli", StringComparison.Ordinal) ||
            ExtractMedulaIdentifier(request.UserMessage, "reçete|recete") is not null ||
            ExtractMedulaPatientQuery(request.UserMessage) is not null ||
            text.Contains("icerik", StringComparison.Ordinal) ||
            text.Contains("ilaclari", StringComparison.Ordinal) ||
            registry.Get("medula_receteleri_ara") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var range = ResolvePrescriptionDateRange(text);
        var defaulted = range is null;
        var today = DateOnly.FromDateTime(DateTime.Today);
        range ??= (today, today);
        var requestedInvoiceTypeCodes = ResolvePrescriptionInvoiceTypeCodes(text);
        var searchInputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "live_date_range",
            ["start_date"] = range.Value.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end_date"] = range.Value.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["invoice_type_codes"] = requestedInvoiceTypeCodes is null
                ? MedulaPrescriptionSearchWorkflow.DefaultInvoiceTypeCodes
                : string.Join(',', requestedInvoiceTypeCodes),
            ["_period_default"] = defaulted ? "true" : "false"
        };
        var qrStatus = PrescriptionQrStatus(text);
        if (qrStatus is not null)
            searchInputs["qr_status"] = qrStatus;
        var queue = new List<string> { "medula_receteleri_ara", "tabloda_goster" };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["medula_receteleri_ara"] = searchInputs,
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = qrStatus == "unfinalized" ? "Sonlandırılmamış Reçeteler" : "Reçeteler"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal), [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildPrescriptionSearchContinuationState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        RecentPeriodWorkflow? replay;
        lock (sync) replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        if (replay?.WorkflowName != "medula_receteleri_ara" ||
            registry.Get("medula_receteleri_ara") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var text = NormalizeIntent(request.UserMessage);
        if (!IsSimplePrescriptionSearchRequest(text, continuation: true)) return false;
        var range = ResolvePrescriptionDateRange(text);
        var invoiceTypeCodes = ResolvePrescriptionInvoiceTypeCodes(text);
        if (range is null && invoiceTypeCodes is null)
            return false;

        var parameters = new Dictionary<string, string>(replay.Parameters, StringComparer.Ordinal);
        if (range is not null)
        {
            foreach (var key in new[] { "calendar_period", "period_days", "period_months", "all_history", "nearest" })
                parameters.Remove(key);
            parameters["start_date"] = range.Value.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            parameters["end_date"] = range.Value.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            parameters["_period_default"] = "false";
        }
        if (invoiceTypeCodes is not null)
        {
            parameters.Remove("invoice_type_code");
            parameters["invoice_type_codes"] = string.Join(',', invoiceTypeCodes);
        }
        else if (!parameters.ContainsKey("invoice_type_code") && !parameters.ContainsKey("invoice_type_codes"))
        {
            parameters["invoice_type_codes"] = MedulaPrescriptionSearchWorkflow.DefaultInvoiceTypeCodes;
        }

        var queue = new List<string> { "medula_receteleri_ara", "tabloda_goster" };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["medula_receteleri_ara"] = parameters,
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = parameters.GetValueOrDefault("qr_status") == "unfinalized"
                    ? "Sonlandırılmamış Reçeteler"
                    : "Reçeteler"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            replay.ResolvedContext.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            [], [], configuration.RouterModel, true);
        return true;
    }

    private static IReadOnlyList<string>? ResolvePrescriptionInvoiceTypeCodes(string normalizedRequest)
    {
        var mentionsGroup = normalizedRequest.Contains("grub", StringComparison.Ordinal) ||
                            Regex.IsMatch(normalizedRequest,
                                @"\ba\s*,?\s*b\s+(?:ve\s+)?c\b", RegexOptions.CultureInvariant);
        if (!mentionsGroup) return null;
        var codes = new List<string>();
        if (Regex.IsMatch(normalizedRequest, @"\ba(?:\s+grubu)?\b", RegexOptions.CultureInvariant))
            codes.Add("1");
        if (Regex.IsMatch(normalizedRequest, @"\bb(?:\s+grubu)?\b", RegexOptions.CultureInvariant))
            codes.Add("4");
        if (Regex.IsMatch(normalizedRequest, @"\bc(?:\s+grubu)?\b", RegexOptions.CultureInvariant))
        {
            codes.Add("28");
            codes.Add("7");
        }
        return codes.Count == 0 ? null : codes;
    }

    private static (DateOnly Start, DateOnly End)? ResolvePrescriptionDateRange(string normalizedRequest)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (normalizedRequest.Contains("bu hafta", StringComparison.Ordinal))
        {
            var daysAfterMonday = ((int)today.DayOfWeek + 6) % 7;
            return (today.AddDays(-daysAfterMonday), today);
        }
        if (normalizedRequest.Contains("bugun", StringComparison.Ordinal)) return (today, today);
        if (normalizedRequest.Contains("bu ay", StringComparison.Ordinal))
            return (new DateOnly(today.Year, today.Month, 1), today);
        var days = Regex.Match(normalizedRequest,
            @"\bson\s+(?<days>[0-9]{1,4})\s+gun", RegexOptions.CultureInvariant);
        if (days.Success && int.TryParse(days.Groups["days"].Value, out var count) && count is >= 1 and <= 3660)
            return (today.AddDays(-count), today);
        return null;
    }

    private static string? PrescriptionQrStatus(string normalizedRequest)
    {
        var text = normalizedRequest.Replace("sonalndir", "sonlandir", StringComparison.Ordinal);
        if (text.Contains("sonlandirilmamis", StringComparison.Ordinal) ||
            text.Contains("sonlandirilmadi", StringComparison.Ordinal) ||
            text.Contains("bitmemis", StringComparison.Ordinal) ||
            text.Contains("tamamlanmamis", StringComparison.Ordinal))
            return "unfinalized";
        if (text.Contains("sonlandirilmis", StringComparison.Ordinal) ||
            text.Contains("sonlandirildi", StringComparison.Ordinal) ||
            text.Contains("tamamlanmis", StringComparison.Ordinal))
            return "finalized";
        return null;
    }

    private bool TryBuildRememberedPatientPrescriptionsState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        var requestsLocalPrescriptionFollowUp = IsSimpleRememberedLocalPrescriptionRequest(text);
        if (!requestsLocalPrescriptionFollowUp ||
            GetRememberedPatientSelection(request.SessionId) is null ||
            registry.Get("onceki_hasta_bilgisini_getir") is null ||
            registry.Get("medula_hasta_recetelerini_getir") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        var queue = new List<string>
        {
            "onceki_hasta_bilgisini_getir", "medula_hasta_recetelerini_getir", "tabloda_goster"
        };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["medula_hasta_recetelerini_getir"] = new Dictionary<string, string>(),
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = "Hastanın Yerel ve Aktif Reçeteleri"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal), [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildPrescriptionReportContinuationState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage);
        if (!text.Contains("recete", StringComparison.Ordinal) ||
            !text.Contains("raporlu", StringComparison.Ordinal) && !text.Contains("raporsuz", StringComparison.Ordinal))
            return false;
        RecentPeriodWorkflow? replay;
        lock (sync) replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        if (replay?.WorkflowName != "recete_rapor_durumu_ozeti" || registry.Get(replay.WorkflowName) is null) return false;

        var parameters = new Dictionary<string, string>(replay.Parameters, StringComparer.Ordinal)
        {
            ["report_status"] = text.Contains("raporlu", StringComparison.Ordinal) && text.Contains("raporsuz", StringComparison.Ordinal)
                ? "both"
                : text.Contains("raporsuz", StringComparison.Ordinal) ? "unreported" : "reported"
        };
        var queue = new List<string> { replay.WorkflowName };
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [replay.WorkflowName] = parameters
        };
        if (registry.Get("metrik_kartlari_goster") is not null)
        {
            queue.Add("metrik_kartlari_goster");
            inputs["metrik_kartlari_goster"] = new Dictionary<string, string>
            {
                ["title"] = parameters["report_status"] switch
                {
                    "reported" => "Raporlu Reçete Sayısı",
                    "unreported" => "Raporsuz Reçete Sayısı",
                    _ => "Reçete Rapor Durumu"
                }
            };
        }
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false, queue, inputs, new AiUsage());
        var outputs = replay.ResolvedContext.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        state = new AiWorkflowRunState(request, plan, queue, 0, outputs, [], [], configuration.RouterModel, true);
        return true;
    }
    private bool TryBuildPatientSalesContinuationState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage).Trim().TrimEnd('.', '!', '?');
        var retry = Regex.IsMatch(text, @"^(?:tekrar|yeniden|bir daha)\s+(?:dene|deneyin)$", RegexOptions.CultureInvariant);
        var contents = HasSalesContentsCue(text) &&
                       (text.Contains("goster", StringComparison.Ordinal) || text.Contains("getir", StringComparison.Ordinal) ||
                        text.Contains("birlikte", StringComparison.Ordinal) || text.Contains("dahil", StringComparison.Ordinal));
        if (!retry && !contents) return false;

        RecentPeriodWorkflow? replay;
        lock (sync) replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        if (replay is null || replay.WorkflowName is not ("satislari_gor" or "satis_hasilati_ozeti" or "hasta_satis_iceriklerini_gor"))
            return false;
        var parameters = new Dictionary<string, string>(replay.Parameters, StringComparer.Ordinal);
        var resolvedContext = replay.ResolvedContext.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        var remembered = GetRememberedPatientSelection(request.SessionId);
        var patientId = parameters.TryGetValue("patient_record_id", out var idText) &&
                        int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId
            : 0;
        if (!resolvedContext.ContainsKey("hasta_eslestir") && remembered is { } patient &&
            (patientId == 0 || PatientSelectionId(patient) == patientId))
            resolvedContext["hasta_eslestir"] = patient.Clone();
        if (patientId == 0 && resolvedContext.TryGetValue("hasta_eslestir", out var selected))
            patientId = PatientSelectionId(selected);
        if (patientId <= 0) return false;
        parameters["patient_record_id"] = patientId.ToString(CultureInfo.InvariantCulture);

        var workflowName = contents ? "hasta_satis_iceriklerini_gor" : replay.WorkflowName;
        if (registry.Get(workflowName) is null) return false;
        if (contents)
            foreach (var key in new[] { "metric", "group_by", "sort_by", "sort_direction" }) parameters.Remove(key);
        var patientName = resolvedContext.TryGetValue("hasta_eslestir", out var patientOutput) &&
                          patientOutput.TryGetProperty("selected_patient", out var patientData)
            ? WorkflowData.Text(patientData, "name")
            : "Hasta";
        var title = contents ? $"{patientName} Satış İçerikleri" : $"{patientName} Satışları";
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [workflowName] = parameters,
            ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = title }
        };
        var queue = new List<string> { workflowName, "tabloda_goster" };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false, queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0, resolvedContext, [], [], configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildSalesDetailContinuationState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage).Trim();
        var asksForContributingSales = Regex.IsMatch(text,
            @"^(?:bu\s+)?satis(?:lar|lari)?(?:i)?\s+(?:goster|getir|listele|neler|neydi)$|^(?:bu\s+)?satislar\s+ne(?:ler|ydi)$|^(?:bu\s+)?hasilati\s+olusturan\s+satislari\s+(?:goster|getir|listele)$",
            RegexOptions.CultureInvariant);
        if (!asksForContributingSales || registry.Get("satislari_gor") is null) return false;
        RecentPeriodWorkflow? replay;
        lock (sync) replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        if (replay is null || replay.WorkflowName != "satis_hasilati_ozeti") return false;
        var parameters = new Dictionary<string, string>(replay.Parameters, StringComparer.Ordinal);
        foreach (var key in new[] { "metric", "group_by", "sort_by", "sort_direction", "limit" }) parameters.Remove(key);
        if (!parameters.ContainsKey("include_refunds")) parameters["include_refunds"] = "true";
        parameters["include_lines"] = "true";
        parameters["sort_direction"] = "newest_first";
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["satislari_gor"] = parameters,
            ["tabloda_goster"] = new Dictionary<string, string> { ["title"] = "Hasılata Dahil Satışlar" }
        };
        var queue = new List<string> { "satislari_gor", "tabloda_goster" };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null) queue.Add("debug_workflow");
        var resolvedContext = replay.ResolvedContext.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        if (!resolvedContext.ContainsKey("ilac_barkod_eslestir") && GetRememberedSelection(request.SessionId) is { } remembered)
            resolvedContext["ilac_barkod_eslestir"] = remembered.Clone();
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [],
            resolvedContext.ContainsKey("ilac_barkod_eslestir"), queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0,
            resolvedContext,
            [], [], configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildPatientDiscountContinuationState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage).Trim();
        var asksForDiscount = text.Contains("iskonto", StringComparison.Ordinal) ||
                              text.Contains("indirim", StringComparison.Ordinal);
        var referencesPrevious = text.Contains("birlikte", StringComparison.Ordinal) ||
                                 text.Contains("ekle", StringComparison.Ordinal) ||
                                 text.Contains("tabloyu", StringComparison.Ordinal) ||
                                 text.Contains("tabloya", StringComparison.Ordinal) ||
                                 text.Contains("bu tablo", StringComparison.Ordinal);
        if (!asksForDiscount || !referencesPrevious || registry.Get("iskonto_analizi") is null ||
            registry.Get("tabloda_goster") is null)
            return false;

        RecentPeriodWorkflow? replay;
        lock (sync) replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        if (replay?.WorkflowName is not ("hasta_urun_ozeti" or "iskonto_analizi")) return false;

        var parameters = new Dictionary<string, string>(replay.Parameters, StringComparer.Ordinal);
        if (!parameters.TryGetValue("patient_record_id", out var idText) ||
            !int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var patientId) || patientId <= 0)
            return false;
        parameters["group_by"] = "product";
        parameters["sort_by"] = "discount_total";
        parameters["sort_direction"] = "descending";
        parameters.Remove("limit");

        var resolvedContext = replay.ResolvedContext.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        var remembered = GetRememberedPatientSelection(request.SessionId);
        if (!resolvedContext.ContainsKey("hasta_eslestir") && remembered is { } selected &&
            PatientSelectionId(selected) == patientId)
            resolvedContext["hasta_eslestir"] = selected.Clone();
        if (!resolvedContext.TryGetValue("hasta_eslestir", out var patientOutput) ||
            PatientSelectionId(patientOutput) != patientId)
            return false;

        var patientName = patientOutput.TryGetProperty("selected_patient", out var patient)
            ? WorkflowData.Text(patient, "name")
            : "Hasta";
        var queue = new List<string> { "iskonto_analizi", "tabloda_goster" };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["iskonto_analizi"] = parameters,
            ["tabloda_goster"] = new Dictionary<string, string>
            {
                ["title"] = $"{patientName} — İlaçlar ve İskonto Bilgileri"
            }
        };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false,
            queue, inputs, new AiUsage());
        state = new AiWorkflowRunState(request, plan, queue, 0, resolvedContext, [], [],
            configuration.RouterModel, true);
        return true;
    }

    private bool TryBuildPeriodContinuationState(
        AiRequestContext request,
        AiConfiguration configuration,
        out AiWorkflowRunState state)
    {
        state = null!;
        RecentPeriodWorkflow? replay;
        lock (sync)
        {
            replay = recentPeriodWorkflows.GetValueOrDefault(request.SessionId);
        }
        if (replay is null || registry.Get(replay.WorkflowName) is null) return false;
        if (!TryParsePeriodOnly(request.UserMessage, replay.Parameters, out var periodName, out var periodValue))
            return false;
        var parameters = new Dictionary<string, string>(replay.Parameters, StringComparer.Ordinal);
        foreach (var key in new[] { "start_date", "end_date", "period_days", "period_months", "calendar_period", "all_history", "nearest", "_period_default" })
            parameters.Remove(key);
        parameters[periodName] = periodValue;
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [replay.WorkflowName] = parameters
        };
        var plan = new AiWorkflowPlan(
            null, false, null, request.UserMessage, 0, [], replay.ResolvedContext.ContainsKey("ilac_barkod_eslestir"),
            [replay.WorkflowName], inputs, new AiUsage());
        var queue = new List<string> { replay.WorkflowName };
        if (configuration.DebugMode && registry.Get("debug_workflow") is not null)
            queue.Add("debug_workflow");
        var outputs = replay.ResolvedContext.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        state = new AiWorkflowRunState(
            request, plan, queue, 0, outputs, [], [], configuration.RouterModel, true);
        return true;
    }

    private static bool TryParsePeriodOnly(
        string request,
        IReadOnlyDictionary<string, string> previousParameters,
        out string periodName,
        out string periodValue)
    {
        periodName = string.Empty;
        periodValue = string.Empty;
        var match = Regex.Match(request,
            "^\\s*(?:(?:show|get)\\s+)?(?:(?:the\\s+)?(?:last|past)\\s+|son\\s+)?(?<count>[0-9]{1,4})(?:\\s*(?<unit>günlük|gunluk|aylık|aylik|gün|gun|ay|days?|daily|months?|monthly))?(?:\\s+(?:için|icin|instead))?(?:\\s+(?:göster|goster|getir|olsun|yap))?\\s*[.!?]*\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["count"].Value, out var count) || count <= 0)
            return false;
        var unit = match.Groups["unit"].Value;
        var months = unit.Length > 0
            ? unit.StartsWith("ay", StringComparison.OrdinalIgnoreCase) || unit.StartsWith("month", StringComparison.OrdinalIgnoreCase)
            : previousParameters.ContainsKey("period_months");
        if (months ? count > 120 : count > 3650) return false;
        periodName = months ? "period_months" : "period_days";
        periodValue = count.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool Owns(AiRequestContext request, string userId) =>
        string.Equals(request.ApplicationContext.CurrentUserId ?? string.Empty, userId, StringComparison.Ordinal);

    private static string ToUserMessage(AiTransportException exception) => exception.Code switch
    {
        "AUTHENTICATION_FAILED" => "AI kimlik doğrulaması başarısız. API anahtarını kontrol edin.",
        "QUOTA_EXCEEDED" => "AI kullanım kotası veya bakiyesi yetersiz.",
        "RATE_LIMITED" => "AI hizmeti geçici olarak yoğun. Lütfen kısa süre sonra yeniden deneyin.",
        "INVALID_REQUEST" => "AI isteği sağlayıcı tarafından geçersiz bulundu.",
        _ => "AI hizmetlerine şu anda ulaşılamıyor."
    };

}

public sealed record AiWorkflowRuntime(
    WorkflowRouterChatService Chat,
    IAiWorkflowRegistry Registry,
    IAiWorkflowToolExecutor ToolExecutor);

public static class AiWorkflowRuntimeFactory
{
    public static AiWorkflowRuntime Create(
        IAiConfigurationProvider configuration,
        IAiModelTransport transport,
        IAiToolRegistry toolRegistry)
    {
        var registry = AiWorkflowRegistry.Discover();
        var conversations = new AiConversationManager(configuration);
        var gateway = new AiToolExecutionGateway(
            toolRegistry,
            new AppAiPermissionService(),
            new AiConfirmationService(),
            configuration,
            new AiOperationalLog());
        var toolExecutor = new AiWorkflowToolExecutor(toolRegistry, gateway);
        return new AiWorkflowRuntime(
            new WorkflowRouterChatService(
                registry,
                transport,
                configuration,
                conversations,
                new AiUsageTelemetry(),
                new AiPromptProvider(),
                toolExecutor),
            registry,
            toolExecutor);
    }
}
