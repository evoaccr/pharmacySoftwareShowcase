using System.Text.Json;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Runtime;

namespace pharmacyApp.AI.Workflows;

public sealed record AiWorkflowToolExecution(
    string ToolName,
    AiToolExecutionStatus Status,
    JsonElement Output,
    string? SafeMessage,
    long DurationMilliseconds,
    PendingAiActionView? PendingAction = null)
{
    public bool IsSuccess => Status == AiToolExecutionStatus.Success;
}

public interface IAiWorkflowToolExecutor
{
    bool HasTool(string toolName);
    Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default);
    Task<AiWorkflowToolExecution> ExecuteConfirmedAsync(
        string pendingActionId,
        string userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiWorkflowToolExecution(
            string.Empty,
            AiToolExecutionStatus.ValidationFailed,
            JsonSerializer.SerializeToElement(new { }),
            "This workflow executor does not support confirmed tool continuations.",
            0));
    void RejectConfirmation(string pendingActionId, string userId) { }
}

/// <summary>
/// Workflow-only access to the existing tool gateway. The router never receives this registry.
/// Workflow implementations own their deterministic tool order, branches, and bounded retries.
/// </summary>
public sealed class AiWorkflowToolExecutor(
    IAiToolRegistry registry,
    AiToolExecutionGateway gateway) : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => registry.GetTool(toolName) is not null;

    public async Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var execution = await gateway.ExecuteAsync(
            request,
            new AiToolCall(Guid.NewGuid().ToString("N"), toolName, arguments.GetRawText()),
            cancellationToken: cancellationToken);
        JsonElement output;
        try
        {
            using var document = JsonDocument.Parse(execution.OutputJson);
            output = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            output = JsonSerializer.SerializeToElement(new { raw = execution.OutputJson });
        }
        return new AiWorkflowToolExecution(
            toolName,
            execution.Status,
            output,
            execution.SafeMessage,
            execution.DurationMilliseconds,
            execution.PendingAction);
    }

    public async Task<AiWorkflowToolExecution> ExecuteConfirmedAsync(
        string pendingActionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var execution = await gateway.ConfirmAndExecuteAsync(pendingActionId, userId, cancellationToken);
        JsonElement output;
        try
        {
            using var document = JsonDocument.Parse(execution.OutputJson);
            output = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            output = JsonSerializer.SerializeToElement(new { raw = execution.OutputJson });
        }
        return new AiWorkflowToolExecution(
            execution.ToolName,
            execution.Status,
            output,
            execution.SafeMessage,
            execution.DurationMilliseconds,
            execution.PendingAction);
    }

    public void RejectConfirmation(string pendingActionId, string userId) =>
        gateway.RejectConfirmation(pendingActionId, userId);
}
