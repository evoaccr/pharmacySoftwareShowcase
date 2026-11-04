using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Tools;

namespace pharmacyApp.AI.Execution;

public enum AiToolExecutionStatus
{
    Success, ToolMissing, Disabled, PermissionDenied, ValidationFailed,
    ConfirmationRequired, ExternalServiceUnavailable, ExecutionFailed, Timeout, Cancelled
}

public sealed record AiToolExecutionResult(
    AiToolExecutionStatus Status,
    string ToolName,
    string OutputJson,
    string? SafeMessage = null,
    PendingAiActionView? PendingAction = null,
    long DurationMilliseconds = 0)
{
    public bool IsSuccess => Status == AiToolExecutionStatus.Success;
}

public interface IAiPermissionService
{
    bool CanExecute(AiRequestContext context, AiToolDefinition definition);
}

public sealed class AppAiPermissionService : IAiPermissionService
{
    public bool CanExecute(AiRequestContext context, AiToolDefinition definition) => definition.Risk switch
    {
        AiToolRisks.ReadOnly => true,
        AiToolRisks.HighRisk => context.Permissions.Contains("ai.high_risk"),
        _ => !string.IsNullOrWhiteSpace(context.ApplicationContext.CurrentUserId)
    };
}

public sealed class AiToolExecutionGateway(
    IAiToolRegistry registry,
    IAiPermissionService permissions,
    AiConfirmationService confirmationService,
    IAiConfigurationProvider configurationProvider,
    AiOperationalLog operationalLog)
{
    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<AiToolExecutionResult> ExecuteAsync(
        AiRequestContext context,
        AiToolCall call,
        object? continuationState = null,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(context, call, false, continuationState, cancellationToken);

    public async Task<AiToolExecutionResult> ExecuteConfirmedAsync(
        string pendingActionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var pending = confirmationService.TakeConfirmed(pendingActionId, userId);
        if (pending is null)
            return new AiToolExecutionResult(AiToolExecutionStatus.ValidationFailed, string.Empty, "{}",
                "The pending action is missing, rejected, expired, or belongs to another user.");
        return await ExecuteCoreAsync(pending.Context, pending.Call, true, pending.ContinuationState, cancellationToken);
    }

    public async Task<AiToolExecutionResult> ConfirmAndExecuteAsync(
        string pendingActionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!confirmationService.Confirm(pendingActionId, userId))
            return new AiToolExecutionResult(
                AiToolExecutionStatus.ValidationFailed,
                string.Empty,
                "{}",
                "The pending action is missing, rejected, expired, or belongs to another user.");
        return await ExecuteConfirmedAsync(pendingActionId, userId, cancellationToken);
    }

    public void RejectConfirmation(string pendingActionId, string userId) =>
        confirmationService.Reject(pendingActionId, userId);

    private async Task<AiToolExecutionResult> ExecuteCoreAsync(
        AiRequestContext context,
        AiToolCall call,
        bool confirmed,
        object? continuationState,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!pharmacyApp.Infrastructure.OnlineAccessPolicy.IsAiToolEnabled(call.Name))
            return Result(AiToolExecutionStatus.Disabled, call.Name, "{}", pharmacyApp.Infrastructure.OnlineAccessPolicy.MedulaMessage);
        var tool = registry.GetTool(call.Name);
        if (tool is null) return Result(AiToolExecutionStatus.ToolMissing, call.Name, "{}", "The requested tool is not registered.");
        if (!permissions.CanExecute(context, tool.Definition))
            return Result(AiToolExecutionStatus.PermissionDenied, call.Name, "{}", "You are not authorized to perform this operation.");

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            arguments = document.RootElement.Clone();
            ValidateSchema(tool.Definition, arguments);
            if (call.Name == "medula.prescriptions_search" &&
                arguments.TryGetProperty("mode", out var mode) && mode.GetString() != "local")
                return Result(AiToolExecutionStatus.Disabled, call.Name, "{}", pharmacyApp.Infrastructure.OnlineAccessPolicy.MedulaMessage);
        }
        catch (Exception exception) when (exception is JsonException or AiToolValidationException)
        {
            return Result(AiToolExecutionStatus.ValidationFailed, call.Name, "{}", exception.Message);
        }

        if (!confirmed && tool.Definition.Risk is AiToolRisks.ConfirmationRequired or AiToolRisks.HighRisk)
        {
            var pending = confirmationService.Create(
                context, call with { ArgumentsJson = arguments.GetRawText() }, tool.Definition,
                continuationState, configurationProvider.GetCurrent().ConfirmationLifetimeMinutes);
            return Result(AiToolExecutionStatus.ConfirmationRequired, call.Name, "{}",
                "User confirmation is required.", pending);
        }

        try
        {
            var configuration = configurationProvider.GetCurrent();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(configuration.ToolTimeoutSeconds));
            var result = await tool.ExecuteAsync(arguments, timeout.Token);
            var json = JsonSerializer.Serialize(result, ResultOptions);
            if (json.Length > configuration.MaxToolResultSize)
                json = JsonSerializer.Serialize(new
                {
                    truncated = true,
                    message = "The tool result exceeded the configured model-output limit.",
                    original_characters = json.Length
                }, ResultOptions);
            var success = Result(AiToolExecutionStatus.Success, call.Name, json);
            operationalLog.WriteTool(context.SessionId, call.Name, success.Status.ToString(), stopwatch.ElapsedMilliseconds);
            return success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(AiToolExecutionStatus.Cancelled, call.Name, "{}", "The operation was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return Result(AiToolExecutionStatus.Timeout, call.Name, "{}", "The operation timed out.");
        }
        catch (AiToolValidationException exception)
        {
            return Result(AiToolExecutionStatus.ValidationFailed, call.Name, "{}", exception.Message);
        }
        catch (Exception)
        {
            operationalLog.WriteTool(context.SessionId, call.Name, AiToolExecutionStatus.ExecutionFailed.ToString(), stopwatch.ElapsedMilliseconds);
            return Result(AiToolExecutionStatus.ExecutionFailed, call.Name, "{}",
                "The application operation could not be completed.");
        }

        AiToolExecutionResult Result(
            AiToolExecutionStatus status,
            string name,
            string output,
            string? message = null,
            PendingAiActionView? pending = null) =>
            new(status, name, output, message, pending, stopwatch.ElapsedMilliseconds);
    }

    private static void ValidateSchema(AiToolDefinition definition, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new AiToolValidationException("Tool arguments must be a JSON object.");
        var properties = arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        var unknown = properties.Keys.Where(name => !definition.Parameters.ContainsKey(name)).ToArray();
        if (unknown.Length > 0) throw new AiToolValidationException($"Unknown parameter(s): {string.Join(", ", unknown)}.");
        foreach (var (name, parameter) in definition.Parameters)
        {
            if (!properties.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (parameter.Required) throw new AiToolValidationException($"Parameter '{name}' is required.");
                continue;
            }
            if (!Matches(parameter.Type, value.ValueKind))
                throw new AiToolValidationException($"Parameter '{name}' must be {parameter.Type}.");
            if (parameter.AllowedValues is { Count: > 0 } &&
                (value.ValueKind != JsonValueKind.String || !parameter.AllowedValues.Contains(value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)))
                throw new AiToolValidationException($"Parameter '{name}' contains an unsupported value.");
            if (parameter.Type == "array" && parameter.ItemsType is { } itemType &&
                value.EnumerateArray().Any(item => !Matches(itemType, item.ValueKind)))
                throw new AiToolValidationException($"Parameter '{name}' contains an invalid array item.");
        }
    }

    private static bool Matches(string type, JsonValueKind kind) => type switch
    {
        "string" => kind == JsonValueKind.String,
        "integer" => kind == JsonValueKind.Number,
        "number" => kind == JsonValueKind.Number,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "array" => kind == JsonValueKind.Array,
        "object" => kind == JsonValueKind.Object,
        _ => false
    };
}
