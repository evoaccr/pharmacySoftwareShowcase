using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

/// <summary>
/// Expands the exact sales retained from the preceding result into their product lines.
/// This keeps anaphoric follow-ups bounded to what the user actually saw.
/// </summary>
public sealed class PreviousSalesContentsWorkflow : IAiWorkflow
{
    private const string Tool = "sale.get_record";

    public AiWorkflowDefinition Definition { get; } = new(
        "onceki_satislarin_iceriklerini_gor",
        "Önceki satışların içeriklerini gör",
        "Gets the product-line contents of the exact sales in the preceding displayed result. This is a helper for follow-ups such as 'bunlardaki ilaçları listele'.",
        RequiredTools: [Tool]);

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        // Use one exact dataset, never union an original list with its filtered subset.
        // A metric may have replaced the visible list; follow its bounded input lineage.
        var sources = new List<JsonElement>();
        if (PreviousResultData.TryGetSource(context.Outputs, null, out _, out var latest)) sources.Add(latest);
        foreach (var key in PreviousResultData.AncestorKeys)
            if (context.Outputs.TryGetValue(key, out var ancestor)) sources.Add(ancestor);
        var source = sources.FirstOrDefault(output =>
            PreviousResultData.MetadataStrings(output, "columns").Contains("reference_code", StringComparer.Ordinal) ||
            output.GetProperty("rows").EnumerateArray().Any(row => row.ValueKind == JsonValueKind.Object && row.TryGetProperty("reference_code", out _)));
        var references = (source.ValueKind == JsonValueKind.Object
                ? source.GetProperty("rows").EnumerateArray().ToArray() : Array.Empty<JsonElement>())
            .Where(row => row.ValueKind == JsonValueKind.Object)
            .Select(row => WorkflowData.Text(row, "reference_code").Trim().ToUpperInvariant())
            .Where(reference => reference.Length > 1 && reference[0] is 'P' or 'G' &&
                                int.TryParse(reference[1..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (references.Length == 0)
            return WorkflowData.Failed("Önceki yanıtta içerikleri getirilebilecek satış veya iade kaydı bulunamadı.");
        if (context.ToolExecutor?.HasTool(Tool) != true)
            return WorkflowData.Missing(Tool);

        var rows = new List<IReadOnlyDictionary<string, string>>();
        var loadedRecords = 0;
        foreach (var reference in references)
        {
            var execution = await WorkflowData.Execute(context, Tool, new { reference_code = reference }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(Tool);
            if (!execution.IsSuccess ||
                !execution.Output.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True ||
                !execution.Output.TryGetProperty("sale", out var sale) || sale.ValueKind != JsonValueKind.Object)
                continue;
            loadedRecords++;
            rows.AddRange(ToProductRows(sale, reference));
        }

        var message = rows.Count == 0
            ? $"Önceki yanıtta gösterilen {references.Length:N0} satışın ürün içeriği bulunamadı."
            : loadedRecords == references.Length
                ? $"Önceki yanıtta gösterilen {references.Length:N0} satıştan {rows.Count:N0} ilaç satırı getirildi."
                : $"Önceki yanıtta gösterilen {references.Length:N0} satışın {loadedRecords:N0} tanesinden {rows.Count:N0} ilaç satırı getirildi; {references.Length - loadedRecords:N0} satış artık okunamadı.";
        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            message,
            WorkflowData.RowsOutput(rows, new
            {
                source_record_count = references.Length,
                loaded_record_count = loadedRecords,
                total_count = rows.Count,
                columns = new[]
                {
                    "timestamp", "reference_code", "sale_type", "patient_name", "medicine_name", "barcode",
                    "product_quantity", "gross_price", "discount_amount", "net_price"
                },
                visible_columns = new[]
                {
                    "timestamp", "sale_type", "patient_name", "medicine_name", "barcode", "product_quantity", "net_price"
                }
            }));
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> ToProductRows(JsonElement sale, string reference)
    {
        if (!sale.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return [];
        return lines.EnumerateArray()
            .Where(line => line.ValueKind == JsonValueKind.Object)
            .GroupBy(line => new
            {
                Barcode = WorkflowData.Text(line, "barcode"),
                ProductName = WorkflowData.Text(line, "product_name")
            })
            .Select(group => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["timestamp"] = WorkflowData.Text(sale, "timestamp"),
                ["reference_code"] = reference,
                ["sale_type"] = WorkflowData.Text(sale, "sale_type"),
                ["patient_name"] = WorkflowData.Text(sale, "patient_name"),
                ["patient_record_id"] = WorkflowData.Text(sale, "patient_record_id"),
                ["patient_tc"] = WorkflowData.Text(sale, "patient_tc"),
                ["medicine_name"] = group.Key.ProductName,
                ["barcode"] = group.Key.Barcode,
                ["product_quantity"] = group.Count().ToString(CultureInfo.InvariantCulture),
                ["gross_price"] = Sum(group, "gross_price").ToString(CultureInfo.InvariantCulture),
                ["discount_amount"] = Sum(group, "discount_amount").ToString(CultureInfo.InvariantCulture),
                ["net_price"] = Sum(group, "net_price").ToString(CultureInfo.InvariantCulture)
            });
    }

    private static decimal Sum(IEnumerable<JsonElement> lines, string property) => lines.Sum(line =>
        line.TryGetProperty(property, out var value) && value.TryGetDecimal(out var number) ? number : 0m);
}
