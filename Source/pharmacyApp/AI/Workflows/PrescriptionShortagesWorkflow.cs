using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

/// <summary>Compares exact prescription pack quantities with recorded shelf stock; never orders or modifies stock.</summary>
public sealed class PrescriptionShortagesWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "recete_stok_eksiklerini_hesapla", "Reçetenin stok eksiklerini hesapla",
        "Calculates missing medicine packs for the resolved prescription, not sales-based target-stock shortage. Queue hasta_eslestir (first/partial names accepted), then medula_son_receteyi_ac for a patient's latest prescription. Reads exact prescription drug quantities and recorded shelf stock; returns only positive shortages. Terminal: renders its own shortage result; no purchase.",
        ProducesUserResponse: true, RequiredTools: ["product.get"]);

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var source = context.Trace.Where(entry => entry.Status == AiWorkflowStepStatus.Completed)
            .Reverse().Select(entry => entry.Output).Concat(context.Outputs.Values.Reverse())
            .FirstOrDefault(value => value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("prescription", out var prescription) && prescription.ValueKind == JsonValueKind.Object);
        if (source.ValueKind != JsonValueKind.Object)
            return WorkflowData.Failed("Eksik ilaçları hesaplamak için önce bir reçete seçilmesi gerekiyor.");
        var rx = source.GetProperty("metadata").GetProperty("prescription");
        var number = WorkflowData.Text(rx, "prescription_number");
        var patient = WorkflowData.Text(rx, "patient_name");
        if (!rx.TryGetProperty("drugs", out var drugs) || drugs.ValueKind != JsonValueKind.Array)
            return WorkflowData.Failed("Reçetedeki ilaç miktarları okunamadığı için eksikler hesaplanamadı.");
        var required = new Dictionary<string, (string Name, decimal Quantity)>(StringComparer.Ordinal);
        foreach (var drug in drugs.EnumerateArray())
        {
            if (drug.ValueKind != JsonValueKind.Object)
                return WorkflowData.Failed("Reçetedeki ilaç miktarları okunamadığı için eksikler hesaplanamadı.");
            var barcode = WorkflowData.Text(drug, "barcode").Trim();
            if (barcode.Length == 0 || !PreviousResultData.TryDecimal(WorkflowData.Text(drug, "quantity"), out var quantity) ||
                quantity <= 0 || quantity != decimal.Truncate(quantity))
                return WorkflowData.Failed("Reçetedeki ilaç miktarları okunamadığı için eksikler hesaplanamadı.");
            var old = required.GetValueOrDefault(barcode);
            required[barcode] = (WorkflowData.Text(drug, "name"), old.Quantity + quantity);
        }
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var (barcode, item) in required)
        {
            var execution = await WorkflowData.Execute(context, "product.get", new { barcode }, cancellationToken);
            if (execution is not { IsSuccess: true } ||
                !execution.Output.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True ||
                !execution.Output.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object ||
                !product.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object ||
                WorkflowData.Text(summary, "barcode") != barcode ||
                !PreviousResultData.TryDecimal(WorkflowData.Text(summary, "shelf_stock_quantity"), out var stock))
                return WorkflowData.Failed("Raf stokları doğrulanamadığı için reçetenin eksik ilaçları hesaplanamadı.");
            // Negative inventory is a discrepancy, not additional prescription demand.
            var missing = Math.Max(0, item.Quantity - Math.Max(0, stock));
            if (missing == 0) continue;
            rows.Add(new Dictionary<string, string>
            {
                ["barcode"] = barcode, ["medicine_name"] = item.Name,
                ["required_quantity"] = item.Quantity.ToString(CultureInfo.InvariantCulture),
                ["shelf_stock_quantity"] = stock.ToString(CultureInfo.InvariantCulture),
                ["missing_quantity"] = missing.ToString(CultureInfo.InvariantCulture)
            });
        }
        var output = WorkflowData.RowsOutput(rows, new
        {
            prescription_number = number, patient_name = patient, checked_product_count = required.Count,
            stock_basis = "recorded_shelf_stock", suppress_prescription_presentation = true,
            columns = new[] { "medicine_name", "barcode", "required_quantity", "shelf_stock_quantity", "missing_quantity" },
            column_labels = new Dictionary<string, string>
            {
                ["medicine_name"] = "İlaç", ["barcode"] = "Barkod", ["required_quantity"] = "Reçetede İstenen Kutu",
                ["shelf_stock_quantity"] = "Kayıtlı Raf Stoku", ["missing_quantity"] = "Eksik Kutu"
            }
        });
        var message = rows.Count == 0
            ? $"{patient} — {number} reçetesi: kayıtlı raf stoklarına göre eksik ilaç yok."
            : $"{patient} — {number} reçetesi: kayıtlı raf stoklarına göre {rows.Count} ürün eksik.";
        if (rows.Count == 0) return new(AiWorkflowStepStatus.Completed, message, output);
        var display = await new DisplayTableWorkflow().ExecuteAsync(context with
        {
            Outputs = new Dictionary<string, JsonElement> { [Definition.Name] = output },
            Trace = [], Parameters = new Dictionary<string, string> { ["title"] = "Reçetenin Eksik İlaçları", ["default_color_mode"] = "none" }
        }, cancellationToken);
        return new(AiWorkflowStepStatus.Completed, message, output, Table: display.Table);
    }
}

