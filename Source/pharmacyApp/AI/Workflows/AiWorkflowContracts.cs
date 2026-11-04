using System.Reflection;
using System.Text.Json;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Workflows;

public sealed record AiWorkflowDefinition(
    string Name,
    string DisplayName,
    string Description,
    bool ProducesUserResponse = false,
    bool RouterVisible = true,
    IReadOnlyList<string>? RequiredTools = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool RequiresRuntimeAi = false,
    bool ProvidesResolvedContext = false)
{
    public string DisplayName { get; init; } = pharmacyApp.Localization.UiText.Translate(DisplayName);
    public string DisplayFunctionName => System.Text.RegularExpressions.Regex.Replace(
        DisplayName.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
}

public sealed record AiWorkflowTableColumn(string Key, string Label, bool IsVisibleByDefault = true);

public sealed record AiWorkflowTableColorMode(
    string Id,
    string Label,
    string Field,
    IReadOnlyList<string> Colors);

public sealed record AiWorkflowTablePresentation(
    string Title,
    IReadOnlyList<AiWorkflowTableColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    IReadOnlyList<AiWorkflowTableColorMode> ColorModes,
    string DefaultColorMode,
    int? TotalRowCount = null,
    string? HighlightField = null,
    string? RowBackgroundField = null);

public enum AiWorkflowContentKind
{
    InformationCards,
    MetricCards,
    RankedList,
    Timeline
}

public sealed record AiWorkflowContentItem(
    string Title,
    string? Subtitle = null,
    string? Value = null,
    string? Detail = null);

public sealed record AiWorkflowContentPresentation(
    AiWorkflowContentKind Kind,
    string Title,
    IReadOnlyList<AiWorkflowContentItem> Items);

public sealed record AiWorkflowPrescriptionDrug(
    string Barcode,
    string Name,
    string Quantity,
    string Usage,
    string Report,
    string AvailableDate,
    string Message);

public sealed record AiWorkflowPrescriptionDiagnosis(string Code, string Description);

public sealed record AiWorkflowPrescriptionField(string Label, string Value);

/// <summary>
/// A prescription is a domain view, not an arbitrary result row. HomeScreen renders this
/// with the same section hierarchy used by the Medula prescription module.
/// </summary>
public sealed record AiWorkflowPrescriptionPresentation(
    string PrescriptionNumber,
    string ElectronicPrescriptionNumber,
    string PatientName,
    string PatientTc,
    IReadOnlyList<AiWorkflowPrescriptionField> PrescriptionFields,
    IReadOnlyList<AiWorkflowPrescriptionField> DoctorFields,
    IReadOnlyList<AiWorkflowPrescriptionDrug> Drugs,
    IReadOnlyList<AiWorkflowPrescriptionDiagnosis> Diagnoses,
    IReadOnlyList<AiWorkflowPrescriptionField> Payments,
    bool IsLiveOnMedula);

public sealed record AiWorkflowSelectionOption(
    string Id,
    string Label,
    JsonElement Data);

public sealed record AiWorkflowModuleAction(
    string Id,
    string Label,
    string ModuleId,
    IReadOnlyDictionary<string, string> Parameters);

public enum AiWorkflowStepStatus
{
    Completed,
    AwaitingSelection,
    AwaitingApproval,
    MissingTool,
    Failed
}

/// <summary>A user-correctable input problem whose message is safe to display in the conversation.</summary>
public sealed class AiWorkflowInputException(string message) : Exception(message);

public sealed record AiWorkflowStepResult(
    AiWorkflowStepStatus Status,
    string Message,
    JsonElement Output,
    IReadOnlyList<AiWorkflowSelectionOption>? SelectionOptions = null,
    bool AllowsMultipleSelection = false,
    IReadOnlyList<string>? MissingTools = null,
    AiWorkflowTablePresentation? Table = null,
    IReadOnlyList<AiWorkflowModuleAction>? ModuleActions = null,
    AiWorkflowContentPresentation? Content = null,
    AiWorkflowPrescriptionPresentation? Prescription = null);

public sealed record AiWorkflowTraceEntry(
    string WorkflowName,
    string DisplayName,
    AiWorkflowStepStatus Status,
    long DurationMilliseconds,
    string Message,
    JsonElement Output);

public sealed record AiWorkflowContext(
    string OriginalRequest,
    string EffectiveRequest,
    AiApplicationContext ApplicationContext,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    IReadOnlyList<AiWorkflowTraceEntry> Trace,
    IReadOnlyList<string>? MedicineNames = null,
    IReadOnlyList<string>? SelectedOptionIds = null,
    IReadOnlyList<AiWorkflowSelectionOption>? OfferedOptions = null,
    AiRequestContext? RequestContext = null,
    IAiWorkflowToolExecutor? ToolExecutor = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    AiWorkflowToolExecution? ConfirmedToolExecution = null);

public interface IAiWorkflow
{
    AiWorkflowDefinition Definition { get; }
    Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default);
}

public interface IAiWorkflowRegistry
{
    IReadOnlyList<AiWorkflowDefinition> RouterDefinitions { get; }
    IReadOnlyList<AiWorkflowDefinition> Definitions { get; }
    IAiWorkflow? Get(string name);
}

public sealed class AiWorkflowRegistry : IAiWorkflowRegistry
{
    private readonly IReadOnlyDictionary<string, IAiWorkflow> workflows;

    public AiWorkflowRegistry(IEnumerable<IAiWorkflow> workflows)
    {
        var values = workflows.ToArray();
        foreach (var workflow in values) Validate(workflow.Definition);
        var duplicate = values.GroupBy(value => value.Definition.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate AI workflow name '{duplicate.Key}'.");
        this.workflows = values.ToDictionary(value => value.Definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<AiWorkflowDefinition> Definitions => workflows.Values
        .Select(value => value.Definition)
        .OrderBy(value => value.Name, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<AiWorkflowDefinition> RouterDefinitions => Definitions
        .Where(value => value.RouterVisible)
        .ToArray();

    public IAiWorkflow? Get(string name) => workflows.GetValueOrDefault(name);

    public static AiWorkflowRegistry Discover(Assembly? assembly = null)
    {
        assembly ??= typeof(AiWorkflowRegistry).Assembly;
        var workflows = assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IAiWorkflow).IsAssignableFrom(type))
            .Select(type => type.GetConstructor(Type.EmptyTypes) is null
                ? throw new InvalidOperationException($"AI workflow '{type.FullName}' needs a public parameterless constructor for discovery.")
                : (IAiWorkflow)Activator.CreateInstance(type)!)
            .ToArray();
        return new AiWorkflowRegistry(workflows);
    }

    private static void Validate(AiWorkflowDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) ||
            definition.Name.Any(character => !(char.IsAsciiLetterLower(character) || char.IsDigit(character) || character == '_')))
            throw new InvalidOperationException($"Invalid AI workflow name '{definition.Name}'. Use lowercase snake_case.");
        if (string.IsNullOrWhiteSpace(definition.DisplayName) || string.IsNullOrWhiteSpace(definition.Description))
            throw new InvalidOperationException($"AI workflow '{definition.Name}' needs a display name and description.");
        if (definition.RequiredTools is { Count: > 0 } &&
            definition.RequiredTools.Any(name => string.IsNullOrWhiteSpace(name) || !name.Contains('.', StringComparison.Ordinal)))
            throw new InvalidOperationException($"AI workflow '{definition.Name}' contains an invalid required tool name.");
        if (definition.Parameters is { Count: > 0 } && definition.Parameters.Any(parameter =>
                string.IsNullOrWhiteSpace(parameter.Key) || string.IsNullOrWhiteSpace(parameter.Value)))
            throw new InvalidOperationException($"AI workflow '{definition.Name}' contains invalid router parameter metadata.");
    }
}

public sealed record AiWorkflowSelectionView(
    string Id,
    string Prompt,
    IReadOnlyList<AiWorkflowSelectionOption> Options,
    bool AllowsMultiple,
    DateTimeOffset ExpiresAt);

public sealed record AiWorkflowApprovalView(
    string Id,
    string Summary,
    DateTimeOffset ExpiresAt);

public sealed record AiRequestCaptureFile(
    string Label,
    string FilePath,
    bool SentToModel);

public sealed record AiWorkflowAssistantResult(
    string Message,
    string? ModelUsed = null,
    IReadOnlyList<AiModelUsage>? UsageByModel = null,
    string? DebugSummary = null,
    AiWorkflowSelectionView? PendingSelection = null,
    AiWorkflowApprovalView? PendingApproval = null,
    AiWorkflowTablePresentation? Table = null,
    IReadOnlyList<AiWorkflowModuleAction>? ModuleActions = null,
    AiWorkflowContentPresentation? Content = null,
    IReadOnlyList<string>? WorkflowQueue = null,
    AiWorkflowPrescriptionPresentation? Prescription = null);
