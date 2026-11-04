using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public sealed class DebugWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "debug_workflow",
        "Debug workflow function",
        "Collects a compact summary of preceding workflows for the separate Debug panel.",
        RouterVisible: false);

    public Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var output = JsonSerializer.SerializeToElement(new
        {
            workflow_count = context.Trace.Count,
            workflows = context.Trace.Select(entry => new
            {
                name = entry.WorkflowName,
                status = entry.Status.ToString(),
                duration_ms = entry.DurationMilliseconds,
                row_count = entry.Output.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
                    ? rows.GetArrayLength()
                    : (int?)null
            }).ToArray()
        });
        return Task.FromResult(new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            "Debug iş akışı özeti hazırlandı.",
            output));
    }
}
