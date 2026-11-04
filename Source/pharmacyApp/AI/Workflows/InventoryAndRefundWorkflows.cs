using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public sealed class InventoryAnalysisWorkflow : IAiWorkflow
{
    private const string Tool = "product.search";

    public AiWorkflowDefinition Definition { get; } = new(
        "stok_portfoy_analizi",
        "Stok portföy analizi",
        "Lists active catalogue stock using the Product Catalog's authoritative shelf-stock query. Supports all, zero/negative, negative, and positive-stock views without arbitrary SQL.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["mode"] = "all, zero, shortage/zero_or_negative, negative, in_stock, passive_in_stock, or stock_value.",
            ["only_on_market"] = "true by default; false also includes passive products.",
            ["limit"] = "Maximum displayed rows, 1-500."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var requestText = Normalize(context.OriginalRequest);
        var requestedMode = Normalize(WorkflowData.Parameter(context, "mode") ?? string.Empty);
        var mode = requestText.Contains("stok degeri", StringComparison.Ordinal) &&
                   requestText.Contains("en yuksek", StringComparison.Ordinal) ? "stock_value" :
            requestText.Contains("pasif", StringComparison.Ordinal) &&
            (requestText.Contains("rafta", StringComparison.Ordinal) || requestText.Contains("stok", StringComparison.Ordinal)) ? "passive_in_stock" :
            requestText.Contains("negatif", StringComparison.Ordinal) ? "negative" :
            requestText.Contains("sifir", StringComparison.Ordinal) ? "zero" :
            requestedMode.Length > 0 ? requestedMode :
            requestText.Contains("eksik", StringComparison.Ordinal) ? "shortage" : "all";
        mode = mode switch
        {
            "shortage" or "eksik" or "zero_or_negative" or "missing_stock" => "zero_or_negative",
            "zero" or "sifir" or "zero_stock" or "shelf_stock" => requestText.Contains("sifir", StringComparison.Ordinal) ? "zero" : "all",
            "negative" or "negatif" or "negative_shelf_stock" => "negative",
            "in_stock" or "stokta" or "positive" => "in_stock",
            "passive_in_stock" or "inactive_in_stock" => "passive_in_stock",
            "stock_value" or "value" => "stock_value",
            _ => "all"
        };
        var displayLimit = WorkflowData.Integer(context, "limit", 200, 1, 500);
        var onlyOnMarket = WorkflowData.NullableBoolean(context, "only_on_market") ?? true;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var totalCount = 0;
        const bool requiresFullScan = false;
        var scanMaximum = displayLimit;
        const int pageSize = 100;
        for (var offset = 0; offset < scanMaximum; offset += pageSize)
        {
            var execution = await WorkflowData.Execute(context, Tool, new
            {
                search = string.Empty,
                product_prefix = string.Empty,
                ingredient = string.Empty,
                prescription_type = string.Empty,
                only_in_stock = mode is "in_stock" or "passive_in_stock",
                stock_minimum = mode switch { "zero" => 0, "negative" => -1_000_000, _ => (int?)null },
                stock_maximum = mode switch { "zero" => 0, "negative" => -1, "zero_or_negative" => 0, _ => (int?)null },
                only_on_market = false,
                market_status = mode == "passive_in_stock" ? "passive" : onlyOnMarket ? "active" : "all",
                filter_field = (string?)null,
                filter_value = (string?)null,
                sort_by = mode == "stock_value" ? "stock_value" : "stock",
                sort_direction = mode == "stock_value" ? "desc" : "asc",
                offset,
                limit = Math.Min(pageSize, scanMaximum - offset)
            }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(Tool);
            if (!execution.IsSuccess) return WorkflowData.Failed("Stok portföyü alınamadı.");
            var products = execution.Output.TryGetProperty("products", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).ToArray()
                : [];
            foreach (var product in products)
            {
                var stock = Number(product, "shelf_stock_quantity");
                var price = Decimal(product, "purchase_price");
                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["medicine_name"] = WorkflowData.Text(product, "name"),
                    ["barcode"] = WorkflowData.Text(product, "barcode"),
                    ["shelf_stock_quantity"] = stock.ToString(CultureInfo.InvariantCulture),
                    ["purchase_price"] = price?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    ["stock_value"] = price.HasValue ? (price.Value * stock).ToString(CultureInfo.InvariantCulture) : string.Empty,
                    ["is_on_market"] = WorkflowData.Text(product, "is_on_market")
                });
            }
            totalCount = execution.Output.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var parsed)
                ? parsed : rows.Count;
            var hasMore = execution.Output.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            if (!hasMore || products.Length == 0 || (!requiresFullScan && rows.Count >= displayLimit)) break;
        }
        if (rows.Count > displayLimit)
            rows = rows.Take(displayLimit).ToList();
        var scope = mode == "passive_in_stock" ? "pasif ürün kataloğunda" :
            onlyOnMarket ? "aktif ürün kataloğunda" : "ürün kataloğunda";
        var kind = mode switch
        {
            "zero" => "raf stoku sıfır",
            "zero_or_negative" => "stoku sıfır veya negatif",
            "negative" => "stoku negatif",
            "in_stock" => "stokta bulunan",
            "passive_in_stock" => "rafta bulunan",
            "stock_value" => "stok değerine göre sıralanan",
            _ => "stok"
        };
        var message = totalCount == 0
            ? $"{scope} {kind} ürün bulunamadı."
            : totalCount > rows.Count
                ? $"{scope} {totalCount:N0} {kind} ürün bulundu; ilk {rows.Count:N0} ürün gösteriliyor."
                : $"{scope} {rows.Count:N0} {kind} ürün bulundu.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                total_count = totalCount,
                mode,
                scan_truncated = false,
                visible_columns = new[] { "medicine_name", "barcode", "shelf_stock_quantity", "purchase_price", "stock_value" }
            }));
    }

    private static int Number(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static decimal? Decimal(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : null;

    private static decimal Decimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : decimal.MinValue;

    private static string Normalize(string value) => value.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"))
        .Replace('ç', 'c').Replace('ğ', 'g').Replace('ı', 'i').Replace('ö', 'o').Replace('ş', 's').Replace('ü', 'u');
}

public sealed class LatestRefundsWorkflow : IAiWorkflow
{
    private const string Tool = "sale.search_records";

    public AiWorkflowDefinition Definition { get; } = new(
        "son_iadeleri_gor", "Son iadeleri gör",
        "Returns the newest refund records with their product lines and original-sale provenance, scanning persisted records in descending time order.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["limit"] = "Number of newest refunds, 1-200.",
            ["start_date"] = "Optional inclusive YYYY-MM-DD.",
            ["end_date"] = "Optional inclusive YYYY-MM-DD.",
            ["sale_types"] = "Optional retail and/or prescription."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var limit = WorkflowData.Integer(context, "limit", 10, 1, 200);
        var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (!parameters.ContainsKey("start_date") && !parameters.ContainsKey("end_date")) parameters["all_history"] = "true";
        var bounded = context with { Parameters = parameters };
        var (start, end) = WorkflowData.DateRange(bounded);
        var refunds = new List<JsonElement>();
        // Refund rows include full immutable product-line snapshots. Keep each tool result below the
        // gateway output limit; a 100-record page was replaced by a truncation sentinel before parsing.
        var pageSize = Math.Min(10, limit);
        const int scanMaximum = 10_000;
        var scanned = 0;
        for (var offset = 0; offset < scanMaximum && refunds.Count < limit; offset += pageSize)
        {
            var execution = await WorkflowData.Execute(context, Tool, new
            {
                barcodes = Array.Empty<string>(), qr_codes = Array.Empty<string>(), start_date = start, end_date = end,
                sale_types = WorkflowData.SaleTypes(context), payment_types = Array.Empty<string>(), statuses = Array.Empty<string>(),
                record_types = new[] { "refund" },
                patient_record_id = (int?)null, user_id = (string?)null, prescription_no = (string?)null,
                include_refunds = true, is_debted = (bool?)null, marked_only = false,
                minimum_total = (decimal?)null, maximum_total = (decimal?)null,
                include_lines = true, sort_direction = "newest_first", offset, limit = pageSize
            }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(Tool);
            if (!execution.IsSuccess) return WorkflowData.Failed("İade kayıtları alınamadı.");
            var page = execution.Output.TryGetProperty("sales", out var sales) && sales.ValueKind == JsonValueKind.Array
                ? sales.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).ToArray() : [];
            scanned += page.Length;
            // The database query is constrained to refunds, while this defensive check keeps custom
            // executors and older providers from leaking interleaved sales into the result.
            refunds.AddRange(page
                .Where(value => string.Equals(WorkflowData.Text(value, "record_type"), "refund", StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Clone()));
            var hasMore = execution.Output.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            if (!hasMore || page.Length == 0) break;
        }
        var selected = refunds.Take(limit).ToArray();
        var rows = selected.SelectMany(RefundRows).ToArray();
        var period = $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)}";
        var message = selected.Length == 0
            ? $"{period} dönemi tarandı; iade kaydı bulunamadı."
            : $"{period} dönemi tarandı; en son {selected.Length:N0} iadenin {rows.Length:N0} ürün satırı gösteriliyor.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                total_count = rows.Length, refund_count = selected.Length, scanned_record_count = scanned,
                visible_columns = new[] { "timestamp", "refund_reference", "original_sale_reference", "sale_type", "patient_name", "medicine_name", "barcode", "net_amount", "user_name" }
            }));
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> RefundRows(JsonElement refund)
    {
        if (!refund.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0)
            return [new Dictionary<string, string>(WorkflowData.FlatRow(refund), StringComparer.Ordinal)];
        return lines.EnumerateArray().Select(line => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["timestamp"] = WorkflowData.Text(refund, "timestamp"),
            ["refund_reference"] = WorkflowData.Text(refund, "reference_code"),
            ["record_type"] = "refund",
            ["original_sale_reference"] = int.TryParse(WorkflowData.Text(line, "original_sale_id"), out var id) && id > 0 ? $"P{id}" : string.Empty,
            ["sale_type"] = WorkflowData.Text(refund, "sale_type"),
            ["patient_name"] = WorkflowData.Text(refund, "patient_name"),
            ["medicine_name"] = WorkflowData.Text(line, "product_name"),
            ["barcode"] = WorkflowData.Text(line, "barcode"),
            ["net_amount"] = WorkflowData.Text(line, "net_price"),
            ["user_name"] = WorkflowData.Text(refund, "user_name")
        });
    }
}
