using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public abstract class PreviousContextWorkflowBase(
    string name,
    string displayName,
    string contextKey,
    string missingMessage) : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        name,
        displayName,
        "Confirms and injects an exact entity resolved by a previous workflow without another model call.",
        RouterVisible: true,
        Parameters: new Dictionary<string, string>(),
        ProvidesResolvedContext: true);

    public Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Outputs.TryGetValue(contextKey, out var value))
            return Task.FromResult(WorkflowData.Failed(missingMessage));
        return Task.FromResult(new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            "Önceki bağlam onaylandı.",
            value.Clone()));
    }
}
public sealed class PreviousPatientContextWorkflow() : PreviousContextWorkflowBase(
    "onceki_hasta_bilgisini_getir", "Önceki hastayı kullan", "remembered_patient_context",
    "Bu konuşmada yeniden kullanılabilecek bir hasta seçimi bulunamadı.");

public sealed class PreviousMedicineContextWorkflow() : PreviousContextWorkflowBase(
    "onceki_ilac_bilgisini_getir", "Önceki ilacı kullan", "remembered_medicine_context",
    "Bu konuşmada yeniden kullanılabilecek bir ilaç seçimi bulunamadı.");

public sealed class PreviousPrescriptionContextWorkflow() : PreviousContextWorkflowBase(
    "onceki_recete_bilgisini_getir", "Önceki reçeteyi kullan", "remembered_prescription_context",
    "Bu konuşmada yeniden kullanılabilecek bir reçete bulunamadı.");

/// <summary>
/// Generic, router-planned branch point. Option data carries a complete deterministic queue,
/// so choosing a branch never invokes the router a second time.
/// </summary>
public sealed class WorkflowBranchSelectionWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "is_akisi_dali_sec",
        "İş akışı dalını seç",
        "Pauses an ambiguous request with AI-labelled choices whose complete workflow queues are planned up front and resumed without a second model call.",
        Parameters: new Dictionary<string, string>
        {
            ["question"] = "Short English clarification question.",
            ["options_json"] = "JSON array of {id,label,queue,inputs} deterministic branches."
        });

    public Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var question = WorkflowData.Parameter(context, "question")?.Trim();
        if (string.IsNullOrWhiteSpace(question)) question = "Hangi işlemle devam edilsin?";
        var optionsJson = WorkflowData.Parameter(context, "options_json");
        if (string.IsNullOrWhiteSpace(optionsJson))
            return Task.FromResult(WorkflowData.Failed("İş akışı seçenekleri hazırlanamadı."));

        JsonElement array;
        try { array = JsonSerializer.Deserialize<JsonElement>(optionsJson); }
        catch (JsonException) { return Task.FromResult(WorkflowData.Failed("İş akışı seçenekleri doğrulanamadı.")); }
        if (array.ValueKind != JsonValueKind.Array)
            return Task.FromResult(WorkflowData.Failed("İş akışı seçenekleri doğrulanamadı."));
        var options = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item =>
            {
                var id = WorkflowData.Text(item, "id").Trim();
                var label = WorkflowData.Text(item, "label").Trim();
                return id.Length == 0 || label.Length == 0
                    ? null
                    : new AiWorkflowSelectionOption(id, label, item.Clone());
            })
            .Where(option => option is not null)
            .Select(option => option!)
            .ToArray();
        if (options.Length < 2)
            return Task.FromResult(WorkflowData.Failed(
                "İşlem seçenekleri hazırlanamadı. Lütfen hasta veya ilacı ve görmek istediğiniz bilgiyi birlikte belirtin; örneğin “Özlem Duman'ın raporlarını göster”."));
        if (options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != options.Length)
            return Task.FromResult(WorkflowData.Failed("İşlem seçenekleri güvenilir biçimde ayrıştırılamadı. Lütfen yapmak istediğiniz işlemi yeniden belirtin."));

        if (context.SelectedOptionIds is { Count: > 0 })
        {
            if (context.SelectedOptionIds.Distinct(StringComparer.Ordinal).Count() != 1)
                return Task.FromResult(WorkflowData.Failed("Devam etmek için yalnızca bir işlem seçin."));
            var selected = options.FirstOrDefault(option => context.SelectedOptionIds.Contains(option.Id, StringComparer.Ordinal));
            return Task.FromResult(selected is null
                ? WorkflowData.Failed("Seçilen iş akışı doğrulanamadı.")
                : new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, "Seçiminizle devam ediliyor.", selected.Data.Clone()));
        }

        return Task.FromResult(new AiWorkflowStepResult(
            AiWorkflowStepStatus.AwaitingSelection,
            question,
            JsonSerializer.SerializeToElement(new { selection_kind = "workflow_branch" }),
            options,
            AllowsMultipleSelection: false));
    }
}
