using System.Text.Json;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Workflows;

internal static class PreviousResultWorkflowTests
{
    public static async Task RunAsync()
    {
        var source = JsonSerializer.SerializeToElement(new
        {
            rows = new[]
            {
                new { reference_code = "P1", patient_name = "GÖNÜL ÇUBUKLU", patient_record_id = "7", medicine_name = "XANAX", sale_type = "prescription", quantity = "2", discount_amount = "12.25", timestamp = "2026-09-04T10:00:00" },
                new { reference_code = "P2", patient_name = "EVREN ACAR", patient_record_id = "8", medicine_name = "PAROL", sale_type = "retail", quantity = "10", discount_amount = "1.234,50 ₺", timestamp = "2026-09-05T09:00:00" },
                new { reference_code = "P3", patient_name = "GÖNÜL ÇUBUKLU", patient_record_id = "7", medicine_name = "XANAX", sale_type = "prescription", quantity = "1", discount_amount = "3.25", timestamp = "2026-09-04T11:00:00" }
            },
            metadata = new { source_workflow = "satislari_gor", visible_columns = new[] { "patient_name", "quantity", "discount_amount" }, total_count = 4000 }
        });
        var outputs = new Dictionary<string, JsonElement> { [PreviousResultData.ContextKey] = source };
        var sum = await Execute(outputs, new() { ["metrics_json"] = "[{\"field\":\"discount_amount\",\"op\":\"sum\",\"alias\":\"total_discount\",\"label\":\"Toplam İskonto\"}]" });
        Assert(sum.Status == AiWorkflowStepStatus.Completed && Cell(sum, "total_discount") == "1250.00", "Sums exact retained rows using invariant and Turkish currency values.");
        Assert(sum.Output.GetProperty("metadata").GetProperty("source_row_count").GetInt32() == 3 && sum.Message.Contains("3 kayıt"), "Explains displayed scope instead of claiming database-wide total.");
        Assert(sum.Output.GetProperty("metadata").GetProperty("column_labels").GetProperty("total_discount").GetString() == "Toplam İskonto", "Carries requested Turkish metric labels.");
        var grouped = await Execute(outputs, new()
        {
            ["filters_json"] = "[{\"field\":\"sale_type\",\"op\":\"eq\",\"value\":\"prescription\"}]",
            ["group_by"] = "patient_name",
            ["metrics_json"] = "[{\"field\":\"quantity\",\"op\":\"sum\",\"alias\":\"total_quantity\",\"label\":\"Toplam Ürün Adedi\"},{\"op\":\"count\",\"alias\":\"record_count\",\"label\":\"Kayıt Sayısı\"}]"
        });
        Assert(Cell(grouped, "total_quantity") == "3" && Cell(grouped, "record_count") == "2", "Filters before grouping and aggregating.");
        var sorted = await Execute(outputs, new() { ["sort_by"] = "quantity", ["sort_direction"] = "desc", ["limit"] = "1", ["columns"] = "medicine_name,quantity" });
        Assert(Cell(sorted, "quantity") == "10" && Cell(sorted, "reference_code") == "P2", "Sorts numerically and preserves hidden row-action identifiers.");
        Assert(sorted.Output.GetProperty("metadata").GetProperty("total_count").GetInt32() == 3, "Limit does not change matching total.");
        Assert(sorted.Output.GetProperty("metadata").GetProperty("visible_columns").GetArrayLength() == 2, "Projection changes visible fields without dropping identity.");
        var limitedSum = await Execute(outputs, new() { ["limit"] = "1", ["metrics_json"] = "[{\"field\":\"quantity\",\"op\":\"sum\",\"alias\":\"quantity_total\",\"label\":\"Adet Toplamı\"}]" });
        Assert(Cell(limitedSum, "quantity_total") == "13", "Output limit is applied after aggregation.");
        var turkishFilter = await Execute(outputs, new() { ["filters_json"] = "[{\"field\":\"patient_name\",\"op\":\"contains\",\"value\":\"gönül çubuklu\"}]" });
        Assert(RowCount(turkishFilter) == 2, "Turkish names match case-insensitively.");
        var resolvedPatient = await Execute(outputs, new() { ["resolve_entity"] = "patient", ["filters_json"] = "[{\"field\":\"medicine_name\",\"op\":\"contains\",\"value\":\"xanax\"}]" });
        Assert(resolvedPatient.Status == AiWorkflowStepStatus.Completed && resolvedPatient.Output.GetProperty("patient_record_id").GetInt32() == 7 &&
            resolvedPatient.Output.GetProperty("selected_patient").GetProperty("name").GetString() == "GÖNÜL ÇUBUKLU", "Resolves the exact unique patient from related medicine rows without guessing a name or ID.");
        Assert((await Execute(outputs, new() { ["resolve_entity"] = "patient", ["limit"] = "1" })).Status == AiWorkflowStepStatus.Failed,
            "Multiple patients never become an arbitrary first patient through a presentation limit.");
        Assert(!turkishFilter.Output.TryGetProperty("patient_record_id", out _), "Ordinary row filtering never silently sets patient scope.");
        var noId = JsonSerializer.SerializeToElement(new { rows = new[] { new { patient_name = "EVREN ACAR" } } });
        Assert((await Execute(new() { [PreviousResultData.ContextKey] = noId }, new() { ["resolve_entity"] = "patient" })).Status == AiWorkflowStepStatus.Failed,
            "A patient's name is never fabricated into a database identity.");
        var numericFilter = await Execute(outputs, new() { ["filters_json"] = "[{\"field\":\"quantity\",\"op\":\"gte\",\"value\":2}]" });
        Assert(RowCount(numericFilter) == 2, "Validates and applies numeric comparisons.");
        var noMatches = await Execute(outputs, new() { ["filters_json"] = "[{\"field\":\"medicine_name\",\"op\":\"eq\",\"value\":\"MISSING\"}]" });
        Assert(noMatches.Status == AiWorkflowStepStatus.Completed && RowCount(noMatches) == 0 && noMatches.Message.Contains("kayıt bulunamadı"), "Empty filters produce a truthful empty-result explanation.");
        var distinct = await Execute(outputs, new() { ["metrics_json"] = "[{\"field\":\"patient_record_id\",\"op\":\"distinct_count\",\"alias\":\"patient_count\",\"label\":\"Hasta Sayısı\"}]" });
        Assert(Cell(distinct, "patient_count") == "2", "Counts distinct entities without counting duplicate medicine lines.");

        foreach (var invalid in new Dictionary<string, string>[]
        {
            new() { ["filters_json"] = "[{\"field\":\"not_in_result\",\"op\":\"eq\",\"value\":1}]" },
            new() { ["filters_json"] = "[{\"field\":\"quantity\",\"op\":\"execute_sql\",\"value\":1}]" },
            new() { ["filters_json"] = "[{\"field\":\"quantity\",\"op\":\"gt\",\"value\":\"missing 2 rows\"}]" },
            new() { ["filters_json"] = "[{\"field\":\"quantity\",\"op\":\"gt\",\"value\":1,\"or\":true}]" },
            new() { ["filters_json"] = "[{\"field\":\"quantity\",\"op\":\"gt\",\"value\":1,\"value\":2}]" },
            new() { ["metrics_json"] = "[{\"op\":\"count\",\"alias\":\"total\",\"label\":\"Toplam\",\"where\":\"retail\"}]" },
            new() { ["metrics_json"] = "[{\"field\":\"medicine_name\",\"op\":\"sum\",\"alias\":\"total\",\"label\":\"Toplam\"}]" },
            new() { ["metrics_json"] = "[{\"op\":\"count\",\"alias\":\"total\",\"label\":\"Toplam\"},{\"op\":\"count\",\"alias\":\"total\",\"label\":\"Yine\"}]" },
            new() { ["group_by"] = "patient_name" }, new() { ["sort_by"] = "missing" },
            new() { ["limit"] = "-1" }, new() { ["filters_json"] = "not JSON" }, new() { ["source_workflow"] = "unknown" }
        })
            Assert((await Execute(outputs, invalid)).Status == AiWorkflowStepStatus.Failed,
                "Fails closed for invalid descriptors or absent fields; never substitutes another query: " + JsonSerializer.Serialize(invalid));

        var missingNumeric = JsonSerializer.SerializeToElement(new { rows = new[] { new { amount = "1.25" }, new { amount = "Maliyet bilgisi eksik" } } });
        Assert((await Execute(new() { [PreviousResultData.ContextKey] = missingNumeric }, new()
        { ["metrics_json"] = "[{\"field\":\"amount\",\"op\":\"sum\",\"alias\":\"total\",\"label\":\"Toplam\"}]" })).Status == AiWorkflowStepStatus.Failed, "Missing amounts are not silently converted to zero.");
        var latest = JsonSerializer.SerializeToElement(new { rows = new[] { new { medicine_name = "NEW RESULT" } } });
        var multiple = new Dictionary<string, JsonElement> { ["older_sales"] = source, [PreviousResultData.ContextKey] = latest };
        Assert((await Execute(multiple, new() { ["metrics_json"] = "[{\"field\":\"quantity\",\"op\":\"sum\",\"alias\":\"total\",\"label\":\"Toplam\"}]" })).Status == AiWorkflowStepStatus.Failed, "Does not fall back to older datasets when the current dataset lacks a field.");
        Assert(RowCount(await Execute(multiple, new() { ["source_workflow"] = "older_sales" })) == 3, "An explicit dataset source may select a retained older dataset.");
        var empty = JsonSerializer.SerializeToElement(new { rows = Array.Empty<object>(), metadata = new { columns = new[] { "quantity" } } });
        var emptyCount = await Execute(new() { [PreviousResultData.ContextKey] = empty }, new() { ["metrics_json"] = "[{\"op\":\"count\",\"alias\":\"count\",\"label\":\"Kayıt Sayısı\"}]" });
        Assert(Cell(emptyCount, "count") == "0", "Empty result count is zero, not a missing result error.");
        var preview = JsonSerializer.SerializeToElement(PreviousResultData.BuildPreview("sales", source, 1));
        Assert(preview.GetProperty("row_count").GetInt32() == 3 && preview.GetProperty("preview_row_count").GetInt32() == 1 && preview.GetProperty("preview_truncated").GetBoolean(), "Preview declares truncation while full rows remain available locally.");
        Assert(preview.GetProperty("columns").EnumerateArray().Any(column => column.GetProperty("field").GetString() == "discount_amount"), "Preview schema includes available numeric fields even when not in the sample projection.");
        foreach (var invalidNumber in new[] { "missing 2 rows", "2026-09-04", "12,34,56", "1.2.3", "", "NaN" })
            Assert(!PreviousResultData.TryDecimal(invalidNumber, out _), "Strict numeric parsing rejects arbitrary text and malformed values.");
        Assert(PreviousResultData.TryDecimal("-1.234,50 ₺", out var negative) && negative == -1234.50m, "Parses Turkish negative currency without losing the sign.");
        var rawProductRows = JsonSerializer.SerializeToElement(new { rows = new[] { new { barcode = "8690000000001" } } });
        var rawGroupRows = JsonSerializer.SerializeToElement(new { rows = new[] { new { group_key = "retail" } } });
        var typedProductRows = JsonSerializer.SerializeToElement(new { rows = new[] { new { group_key = "8690000000001" } }, metadata = new { group_by = "product" } });
        var moduleParameters = new Dictionary<string, string> { ["module_ids"] = "product_catalog" };
        var previousRowsModule = await new OpenModulesWorkflow().ExecuteAsync(Context(new() { ["older_result"] = rawProductRows }, moduleParameters));
        Assert(previousRowsModule.ModuleActions![0].Parameters.GetValueOrDefault("barcodes") is "" or null, "Unrelated previous result rows cannot silently become medicine filters.");
        var groupModule = await new OpenModulesWorkflow().ExecuteAsync(Context(new() { ["analysis"] = rawGroupRows }, moduleParameters) with
        { Trace = [new AiWorkflowTraceEntry("analysis", "Analysis", AiWorkflowStepStatus.Completed, 0, "", rawGroupRows)] });
        Assert(groupModule.ModuleActions![0].Parameters.GetValueOrDefault("barcodes") is "" or null, "Unrelated grouping keys such as retail cannot become barcodes.");
        var productModule = await new OpenModulesWorkflow().ExecuteAsync(Context(new() { ["analysis"] = typedProductRows }, moduleParameters) with
        { Trace = [new AiWorkflowTraceEntry("analysis", "Analysis", AiWorkflowStepStatus.Completed, 0, "", typedProductRows)] });
        Assert(productModule.ModuleActions![0].Parameters.GetValueOrDefault("barcodes") == "8690000000001", "Same-turn typed product results retain intentional module-filter chaining.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await new PreviousResultWorkflow().ExecuteAsync(Context(outputs, new()), cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS generic-previous-result-workflow");
    }

    private static Task<AiWorkflowStepResult> Execute(Dictionary<string, JsonElement> outputs, Dictionary<string, string> parameters) =>
        new PreviousResultWorkflow().ExecuteAsync(Context(outputs, parameters));
    private static AiWorkflowContext Context(Dictionary<string, JsonElement> outputs, Dictionary<string, string> parameters) =>
        new("Önceki sonucu işle", "Önceki sonucu işle", new AiApplicationContext(), outputs, [], Parameters: parameters);
    private static int RowCount(AiWorkflowStepResult result) => result.Output.GetProperty("rows").GetArrayLength();
    private static string? Cell(AiWorkflowStepResult result, string field) => result.Output.GetProperty("rows")[0].GetProperty(field).GetString();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
