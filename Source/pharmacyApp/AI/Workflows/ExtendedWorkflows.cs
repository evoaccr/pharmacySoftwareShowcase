using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pharmacyApp.AI.Workflows;

internal static class ExtendedWorkflowData
{
    public static async Task<AiWorkflowStepResult> AggregateSalesAsync(
        AiWorkflowContext context, string groupBy, string sortBy, string sortDirection,
        int defaultDays, CancellationToken cancellationToken, bool retrieveAll = false,
        IReadOnlyList<string>? visibleColumns = null)
    {
        const string tool = "sale.aggregate_records";
        var (start, end) = WorkflowData.DateRange(context, defaultDays);
        var requestedLimit = ExplicitResultLimit(context.OriginalRequest);
        var hasParameterLimit = context.Parameters?.ContainsKey("limit") == true;
        var maximum = retrieveAll
            ? requestedLimit ?? (hasParameterLimit ? WorkflowData.Integer(context, "limit", 100, 1, 10_000) : 10_000)
            : WorkflowData.Integer(context, "limit", 100, 1, 200);
        var pageSize = Math.Min(200, maximum);
        var groups = new List<JsonElement>();
        var totalCount = 0;
        for (var offset = 0; offset < maximum; offset += pageSize)
        {
            var execution = await WorkflowData.Execute(context, tool, new
            {
                start_date = start, end_date = end,
                sale_types = WorkflowData.SaleTypes(context),
                include_refunds = WorkflowData.NullableBoolean(context, "include_refunds") ?? true,
                barcodes = WorkflowData.Barcodes(context), qr_codes = Array.Empty<string>(),
                payment_types = WorkflowData.List(context, "payment_types"),
                statuses = WorkflowData.List(context, "statuses"),
                patient_record_id = WorkflowData.PatientRecordId(context),
                user_id = WorkflowData.Parameter(context, "user_id"),
                prescription_no = WorkflowData.Parameter(context, "prescription_no"),
                is_debted = WorkflowData.NullableBoolean(context, "is_debted"),
                marked_only = WorkflowData.Boolean(context, "marked_only"),
                minimum_total = WorkflowData.NullableDecimal(context, "minimum_total"),
                maximum_total = WorkflowData.NullableDecimal(context, "maximum_total"),
                group_by = groupBy, sort_by = sortBy, sort_direction = sortDirection,
                offset, limit = Math.Min(pageSize, maximum - offset)
            }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(tool);
            if (!execution.IsSuccess) return WorkflowData.Failed("Satış analizi alınamadı.");
            var page = execution.Output.TryGetProperty("groups", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).Select(value => value.Clone()).ToArray() : [];
            groups.AddRange(page);
            totalCount = execution.Output.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var count)
                ? count : groups.Count;
            var hasMore = execution.Output.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            if (!retrieveAll || !hasMore || page.Length == 0 || groups.Count >= maximum) break;
        }
        var truncated = totalCount > groups.Count;
        var groupLabel = groupBy switch
        {
            "product" => "ürün",
            "user" => "kullanıcı",
            "patient" => "hasta",
            "month" => "ay",
            "day" => "gün",
            "sale_type" => "satış türü",
            "record_type" => "kayıt türü",
            _ => "özet"
        };
        var explanation = $"Her satır bir {groupLabel} için birleştirilmiş satış değerlerini temsil eder.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            truncated
                ? $"{FormatDate(start)}–{FormatDate(end)} dönemi için toplam {totalCount:N0} {groupLabel} özeti bulundu; ilk {groups.Count:N0} özet gösteriliyor. {explanation}"
                : $"{FormatDate(start)}–{FormatDate(end)} dönemi için {groups.Count:N0} {groupLabel} özeti bulundu. {explanation}",
            WorkflowData.RowsOutput(groups.Select(group =>
            {
                var row = new Dictionary<string, string>(WorkflowData.FlatRow(group), StringComparer.Ordinal);
                if (!row.ContainsKey("profit")) row["profit"] = "Maliyet bilgisi eksik";
                return (IReadOnlyDictionary<string, string>)row;
            }), new
            {
                start_date = start, end_date = end, group_by = groupBy,
                total_count = totalCount,
                columns = new[] { "group_key", "group_label", "record_count", "sale_count", "refund_count", "quantity", "gross_revenue", "discount_total", "net_revenue", "institution_revenue", "entry_cost_total", "profit", "missing_cost_quantity", "average_record_revenue", "average_item_revenue" },
                visible_columns = visibleColumns ??
                    ["group_label", "record_count", "sale_count", "refund_count", "quantity", "net_revenue", "profit"]
            }));
    }

    private static int? ExplicitResultLimit(string request)
    {
        var match = System.Text.RegularExpressions.Regex.Match(request,
            @"\b(?:ilk|en\s+çok|en\s+cok|en\s+fazla)\s+(?<count>[0-9]{1,4})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
            ? Math.Clamp(count, 1, 10000)
            : null;
    }

    public static string FormatDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR")) : value;

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Rows(JsonElement source)
    {
        if (!source.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
        return rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object)
            .Select(row => (IReadOnlyDictionary<string, string>)row.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText(),
                StringComparer.Ordinal)).ToArray();
    }

    public static JsonElement? LastRows(AiWorkflowContext context)
    {
        foreach (var value in context.Outputs.Reverse().Select(pair => pair.Value))
            if (value.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array) return value;
        return null;
    }
}

public sealed class ProductEquivalentsWorkflow : IAiWorkflow
{
    private const string EquivalentTool = "product.get_equivalents";
    private const string ProductTool = "product.get";
    public AiWorkflowDefinition Definition { get; } = new(
        "urun_muadillerini_getir", "Ürün muadillerini getir",
        "Gets SGK/general equivalents for exact barcodes and resolves their readable local catalogue details. Use exact barcodes; medicine matching is optional and separate.",
        RequiredTools: [EquivalentTool, ProductTool],
        Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "One or more exact source barcodes.", ["kind"] = "all, sgk, or general.",
            ["limit"] = "Maximum equivalents per set, 1-100.",
            ["result_limit"] = "Maximum displayed rows after filtering and sorting, 1-100.",
            ["only_on_market"] = "true to keep only active/on-market equivalents.",
            ["same_sale_price"] = "true to keep only equivalents whose registered sale price equals the source product.",
            ["sort_by"] = "sale_price, purchase_price, or name.", ["sort_direction"] = "ascending or descending."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Muadil sorgusu için barkod gerekiyor.");
        var requestedKind = WorkflowData.Parameter(context, "kind")?.ToLowerInvariant();
        var kind = requestedKind is "sgk" or "general" ? requestedKind : "all";
        decimal? sourceSalePrice = null;
        if (WorkflowData.Boolean(context, "same_sale_price"))
        {
            var sourceExecution = await WorkflowData.Execute(context, ProductTool, new { barcode = barcodes[0] }, cancellationToken);
            if (sourceExecution is null) return WorkflowData.Missing(ProductTool);
            if (sourceExecution.IsSuccess && sourceExecution.Output.TryGetProperty("product", out var sourceProduct) &&
                sourceProduct.ValueKind == JsonValueKind.Object)
            {
                var sourceSummary = sourceProduct.TryGetProperty("summary", out var foundSummary) ? foundSummary : sourceProduct;
                var sourceText = WorkflowData.Text(sourceSummary, "sale_price");
                if (decimal.TryParse(sourceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                    sourceSalePrice = parsed;
            }
            if (sourceSalePrice is null)
                return WorkflowData.Failed("Kaynak ürünün kayıtlı satış fiyatı bulunamadı.");
        }
        var equivalentBarcodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var barcode in barcodes)
        {
            var execution = await WorkflowData.Execute(context, EquivalentTool, new
            {
                barcode, kind, limit = WorkflowData.Integer(context, "limit", 100, 1, 100)
            }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(EquivalentTool);
            if (!execution.IsSuccess) return WorkflowData.Failed("Muadil ürünler alınamadı.");
            Add("sgk_barcodes"); Add("general_barcodes");
            void Add(string property)
            {
                if (!execution.Output.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return;
                foreach (var value in values.EnumerateArray())
                    if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } candidate) equivalentBarcodes.Add(candidate);
            }
        }
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var barcode in equivalentBarcodes)
        {
            var execution = await WorkflowData.Execute(context, ProductTool, new { barcode }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(ProductTool);
            if (!execution.IsSuccess || !execution.Output.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object) continue;
            var summary = product.TryGetProperty("summary", out var foundSummary) ? foundSummary : product;
            rows.Add(new Dictionary<string, string>
            {
                ["barcode"] = barcode, ["name"] = WorkflowData.Text(summary, "name"),
                ["active_ingredient"] = WorkflowData.Text(summary, "active_ingredient"),
                ["sale_price"] = WorkflowData.Text(summary, "sale_price"),
                ["purchase_price"] = WorkflowData.Text(summary, "purchase_price"),
                ["shelf_stock_quantity"] = WorkflowData.Text(summary, "shelf_stock_quantity"),
                ["is_on_market"] = WorkflowData.Text(summary, "is_on_market")
            });
        }
        IEnumerable<IReadOnlyDictionary<string, string>> selectedRows = rows;
        if (WorkflowData.Boolean(context, "only_on_market"))
            selectedRows = selectedRows.Where(row => bool.TryParse(row.GetValueOrDefault("is_on_market"), out var active) && active);
        if (sourceSalePrice is not null)
            selectedRows = selectedRows.Where(row =>
                decimal.TryParse(row.GetValueOrDefault("sale_price"), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) &&
                Math.Abs(value - sourceSalePrice.Value) < 0.005m);
        var sortBy = WorkflowData.Parameter(context, "sort_by")?.ToLowerInvariant();
        var descending = string.Equals(WorkflowData.Parameter(context, "sort_direction"), "descending", StringComparison.OrdinalIgnoreCase);
        selectedRows = sortBy switch
        {
            "sale_price" or "purchase_price" => descending
                ? selectedRows.OrderByDescending(row => Price(row.GetValueOrDefault(sortBy), true))
                : selectedRows.OrderBy(row => Price(row.GetValueOrDefault(sortBy), false)),
            "name" => descending
                ? selectedRows.OrderByDescending(row => row.GetValueOrDefault("name"), StringComparer.CurrentCultureIgnoreCase)
                : selectedRows.OrderBy(row => row.GetValueOrDefault("name"), StringComparer.CurrentCultureIgnoreCase),
            _ => selectedRows
        };
        var resultRows = selectedRows.Take(WorkflowData.Integer(context, "result_limit", 100, 1, 100)).ToArray();
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            resultRows.Length == 0 ? "Filtrelere uyan kayıtlı muadil bulunamadı." : $"{resultRows.Length} kayıtlı muadil bulundu.",
            WorkflowData.RowsOutput(resultRows, new { total_count = resultRows.Length, visible_columns = new[] { "barcode", "name", "active_ingredient", "sale_price", "purchase_price", "shelf_stock_quantity", "is_on_market" } }));

        static decimal Price(string? value, bool descending) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            ? price : descending ? decimal.MinValue : decimal.MaxValue;
    }
}

public sealed class CompareProductsWorkflow : IAiWorkflow
{
    private const string Tool = "product.compare";
    public AiWorkflowDefinition Definition { get; } = new(
        "urunleri_karsilastir", "Ürünleri karşılaştır",
        "Compares two to ten exact barcodes by local catalogue, price, market and shelf-stock facts.",
        RequiredTools: [Tool], Parameters: new Dictionary<string, string> { ["barcodes"] = "Two to ten exact barcodes." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count is < 2 or > 10) return WorkflowData.Failed("Karşılaştırma için 2–10 barkod gerekiyor.");
        var execution = await WorkflowData.Execute(context, Tool, new { barcodes }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Ürün karşılaştırması alınamadı.");
        var rows = execution.Output.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array
            ? products.EnumerateArray().Select(WorkflowData.FlatRow).ToArray() : [];
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, $"{rows.Length} ürün karşılaştırıldı.",
            WorkflowData.RowsOutput(rows, new { total_count = rows.Length, visible_columns = new[] { "barcode", "name", "active_ingredient", "sale_price", "purchase_price", "shelf_stock_quantity", "is_on_market" } }));
    }
}

public sealed class SaleDetailWorkflow : IAiWorkflow
{
    private const string Tool = "sale.get_record";
    private const string SearchTool = "sale.search_records";
    public AiWorkflowDefinition Definition { get; } = new(
        "satis_detayi_getir", "Satış ayrıntısını getir",
        "Gets one immutable sale/refund record by P/G reference, including product lines, payments, patient and QR details.",
        RequiredTools: [Tool, SearchTool], Parameters: new Dictionary<string, string> { ["reference_code"] = "Optional exact P/G reference or R prescription reference; inherited from a preceding sale result when omitted." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var reference = ResolveReference(context);
        if (reference is null) return WorkflowData.Failed("Satış ayrıntısı için önce bir satış/iade kaydı veya P/G referansı gerekiyor.");
        reference = reference.Trim().ToUpperInvariant();
        if (reference.Length > 1 && reference[0] == 'R' && int.TryParse(reference[1..], out _))
        {
            var searchExecution = await WorkflowData.Execute(context, SearchTool, new
            {
                barcodes = Array.Empty<string>(), qr_codes = Array.Empty<string>(),
                start_date = DateOnly.FromDateTime(DateTime.Today).AddYears(-10).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end_date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                sale_types = Array.Empty<string>(), payment_types = Array.Empty<string>(),
                statuses = new[] { "pending", "medula_reported", "completed", "reconciliation_required", "cancelled" },
                patient_record_id = (int?)null, user_id = (string?)null, prescription_no = reference,
                include_refunds = true, is_debted = (bool?)null, marked_only = false,
                minimum_total = (decimal?)null, maximum_total = (decimal?)null,
                include_lines = false, sort_direction = "newest_first", offset = 0, limit = 1
            }, cancellationToken);
            if (searchExecution is null) return WorkflowData.Missing(SearchTool);
            if (!searchExecution.IsSuccess) return WorkflowData.Failed("Reçete referansına bağlı satış aranamadı.");
            reference = searchExecution.Output.TryGetProperty("sales", out var sales) && sales.ValueKind == JsonValueKind.Array
                ? sales.EnumerateArray().Select(item => WorkflowData.Text(item, "reference_code")).FirstOrDefault(value => value.Length > 0)
                : null;
            if (reference is null)
                return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
                    "Bu reçete referansına bağlı satış veya iade kaydı bulunamadı.", WorkflowData.RowsOutput([]));
        }
        if (reference.Length < 2 || reference[0] is not ('P' or 'G') || !int.TryParse(reference[1..], out var referenceId) || referenceId <= 0)
            return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
                "Geçerli satış/iade referansı P<satış no> veya G<iade no> biçiminde olmalıdır.", WorkflowData.RowsOutput([]));
        var execution = await WorkflowData.Execute(context, Tool, new { reference_code = reference }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Satış ayrıntısı alınamadı.");
        if (!execution.Output.TryGetProperty("found", out var found) || !found.GetBoolean() ||
            !execution.Output.TryGetProperty("sale", out var sale) || sale.ValueKind != JsonValueKind.Object)
            return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, "Satış kaydı bulunamadı.", WorkflowData.RowsOutput([]));
        var payments = sale.TryGetProperty("payments", out var paymentArray) && paymentArray.ValueKind == JsonValueKind.Array
            ? paymentArray.EnumerateArray().Select(payment => $"{WorkflowData.Text(payment, "type")}: {WorkflowData.Text(payment, "amount")}").ToArray() : [];
        var paymentSummary = string.Join(" · ", payments);
        var rows = sale.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array
            ? lineArray.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.Object).Select(line =>
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["reference_code"] = reference.ToUpperInvariant(),
                    ["timestamp"] = WorkflowData.Text(sale, "timestamp"),
                    ["record_type"] = WorkflowData.Text(sale, "record_type"),
                    ["sale_type"] = WorkflowData.Text(sale, "sale_type"),
                    ["patient_name"] = WorkflowData.Text(sale, "patient_name"),
                    ["product_name"] = WorkflowData.Text(line, "product_name"),
                    ["barcode"] = WorkflowData.Text(line, "barcode"),
                    ["qr_code"] = WorkflowData.Text(line, "qr_code"),
                    ["gross_price"] = WorkflowData.Text(line, "gross_price"),
                    ["discount_amount"] = WorkflowData.Text(line, "discount_amount"),
                    ["net_price"] = WorkflowData.Text(line, "net_price"),
                    ["entry_cost"] = WorkflowData.Text(line, "entry_cost"),
                    ["profit"] = WorkflowData.Text(line, "profit"),
                    ["payment_summary"] = paymentSummary
                }).ToArray()
            : [];
        if (rows.Length == 0)
            rows = [new Dictionary<string, string>
            {
                ["reference_code"] = reference.ToUpperInvariant(), ["timestamp"] = WorkflowData.Text(sale, "timestamp"),
                ["record_type"] = WorkflowData.Text(sale, "record_type"),
                    ["sale_type"] = WorkflowData.Text(sale, "sale_type"), ["patient_name"] = WorkflowData.Text(sale, "patient_name"),
                ["payment_summary"] = paymentSummary
            }];
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, $"Satış ayrıntısı {rows.Length} ürün satırıyla hazırlandı.",
            WorkflowData.RowsOutput(rows, new
            {
                total_count = rows.Length,
                visible_columns = new[] { "product_name", "barcode", "qr_code", "gross_price", "discount_amount", "net_price", "payment_summary" }
            }));
    }

    private static string? ResolveReference(AiWorkflowContext context)
    {
        var direct = WorkflowData.Parameter(context, "reference_code")?.Trim();
        if (direct is { Length: > 1 } && Regex.IsMatch(direct, @"^[PGR][0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return direct;
        foreach (var output in context.Outputs.Reverse().Select(pair => pair.Value))
        {
            if (!output.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in rows.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
            {
                var candidate = WorkflowData.Text(row, "reference_code").Trim();
                if (Regex.IsMatch(candidate, @"^[PG][0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return candidate;
            }
        }
        return null;
    }
}

public sealed class PatientSalesContentsWorkflow : IAiWorkflow
{
    private const string Tool = "sale.search_records";
    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_satis_iceriklerini_gor", "Hasta satış içeriklerini gör",
        "Lists the product-line contents of a resolved patient's filtered sales. Queue hasta_eslestir first for a named patient and tabloda_goster for presentation.",
        RequiredTools: [Tool], Parameters: new Dictionary<string, string>
        {
            ["patient_record_id"] = "Optional positive local patient ID already resolved by a matcher or prior result; prefer inherited resolved patient context and never infer an ID from a name.",
            ["start_date"] = "Inclusive YYYY-MM-DD.", ["end_date"] = "Inclusive YYYY-MM-DD.",
            ["period_days"] = "Relative days.", ["period_months"] = "Relative months.",
            ["calendar_period"] = "today, yesterday, this_week, last_week, this_month, last_month, this_year, last_year.",
            ["all_history"] = "true for all available history.", ["sale_types"] = "retail,prescription.",
            ["include_refunds"] = "true or false.", ["refunds_only"] = "true to return only refund records.", ["payment_types"] = "Optional payment types.",
            ["statuses"] = "Optional statuses.", ["user_id"] = "Optional user ID.",
            ["prescription_no"] = "Optional prescription number.", ["is_debted"] = "Optional true/false.",
            ["marked_only"] = "true for marked records.", ["minimum_total"] = "Optional minimum total.",
            ["maximum_total"] = "Optional maximum total.", ["highest_total_only"] = "true to keep only the highest-net sale.",
            ["limit"] = "Maximum sale records to inspect, 1-5000; default 2000."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Parameters?.TryGetValue("patient_record_id", out var requestedPatientId) == true &&
            (!int.TryParse(requestedPatientId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPatientId) || parsedPatientId <= 0))
            return WorkflowData.Failed("The patient identifier is invalid. Please select the patient again.");
        var patientId = WorkflowData.PatientRecordId(context);
        if (patientId is null)
            return WorkflowData.Failed("Satış içerikleri için önce hasta kaydının eşleştirilmesi gerekiyor.");

        var hasPeriod = new[] { "start_date", "end_date", "period_days", "period_months", "calendar_period", "all_history" }
            .Any(name => WorkflowData.Parameter(context, name) is not null);
        var effective = hasPeriod
            ? context
            : context with
            {
                Parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                {
                    ["all_history"] = "true"
                }
            };
        var (start, end) = WorkflowData.DateRange(effective);
        var maximumRecords = WorkflowData.Integer(effective, "limit", 2000, 1, 5000);
        var refundsOnly = WorkflowData.Boolean(effective, "refunds_only");
        var highestTotalOnly = WorkflowData.Boolean(effective, "highest_total_only");
        const int pageSize = 200;
        var sales = new List<JsonElement>();
        var totalRecords = 0;
        var partial = false;
        for (var offset = 0; offset < maximumRecords; offset += pageSize)
        {
            var execution = await WorkflowData.Execute(effective, Tool, new
            {
                start_date = start, end_date = end,
                sale_types = WorkflowData.SaleTypes(effective),
                include_refunds = WorkflowData.Boolean(effective, "include_refunds"),
                barcodes = Array.Empty<string>(), qr_codes = Array.Empty<string>(),
                payment_types = WorkflowData.List(effective, "payment_types"),
                statuses = WorkflowData.List(effective, "statuses"),
                patient_record_id = patientId,
                user_id = WorkflowData.Parameter(effective, "user_id"),
                prescription_no = WorkflowData.Parameter(effective, "prescription_no"),
                is_debted = WorkflowData.NullableBoolean(effective, "is_debted"),
                marked_only = WorkflowData.Boolean(effective, "marked_only"),
                minimum_total = WorkflowData.NullableDecimal(effective, "minimum_total"),
                maximum_total = WorkflowData.NullableDecimal(effective, "maximum_total"),
                include_lines = true, sort_direction = "newest_first",
                offset, limit = Math.Min(pageSize, maximumRecords - offset)
            }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(Tool);
            if (!execution.IsSuccess)
            {
                if (sales.Count == 0) return WorkflowData.Failed("Hastanın satış içerikleri alınamadı.");
                partial = true;
                break;
            }
            if (execution.Output.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var parsed))
                totalRecords = parsed;
            var page = execution.Output.TryGetProperty("sales", out var records) && records.ValueKind == JsonValueKind.Array
                ? records.EnumerateArray().Where(record => record.ValueKind == JsonValueKind.Object).Select(record => record.Clone()).ToArray()
                : [];
            sales.AddRange(refundsOnly
                ? page.Where(record => string.Equals(WorkflowData.Text(record, "record_type"), "refund", StringComparison.OrdinalIgnoreCase))
                : page);
            var hasMore = execution.Output.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            if (page.Length == 0 || !hasMore) break;
            if (offset + pageSize >= maximumRecords) partial = true;
        }

        if (highestTotalOnly && sales.Count > 1)
            sales = sales.OrderByDescending(SaleNetAmount).Take(1).ToList();

        var rows = sales.SelectMany(SaleRows).ToArray();
        var patientName = WorkflowData.PatientName(effective);
        var period = $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)}";
        var message = rows.Length == 0
            ? $"{patientName} adlı hastanın {period} dönemindeki satışlarında ürün içeriği bulunamadı."
            : highestTotalOnly
                ? $"{patientName} adlı hastanın {period} dönemindeki en yüksek net tutarlı alışverişinde {rows.Length:N0} ürün satırı bulundu."
                : partial || totalRecords > sales.Count
                ? $"{patientName} adlı hastanın {period} dönemindeki toplam {totalRecords:N0} satışının ilk {sales.Count:N0} kaydından {rows.Length:N0} ürün satırı gösteriliyor."
                    : $"{patientName} adlı hastanın {period} dönemindeki {sales.Count:N0} satışında {rows.Length:N0} ürün satırı bulundu.";
        var action = new AiWorkflowModuleAction(
            "open_patient_sales_contents", "Satışlar modülünde aç", "sales.retail-list",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["start_date"] = start, ["end_date"] = end,
                ["patient_record_id"] = patientId.Value.ToString(CultureInfo.InvariantCulture),
                ["sale_types"] = string.Join(',', WorkflowData.SaleTypes(effective)),
                ["include_refunds"] = WorkflowData.Boolean(effective, "include_refunds") ? "true" : "false"
            });
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                start_date = start, end_date = end, source_record_count = totalRecords,
                total_count = rows.Length,
                columns = new[] { "timestamp", "reference_code", "record_type", "sale_type", "patient_name", "product_name", "barcode", "product_quantity", "gross_price", "discount_amount", "net_price", "qr_codes" },
                visible_columns = new[] { "timestamp", "reference_code", "record_type", "sale_type", "product_name", "barcode", "product_quantity", "net_price" }
            }), ModuleActions: [action]);
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> SaleRows(JsonElement sale)
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
                ["reference_code"] = WorkflowData.Text(sale, "reference_code"),
                ["record_type"] = WorkflowData.Text(sale, "record_type"),
                    ["sale_type"] = WorkflowData.Text(sale, "sale_type"),
                ["patient_name"] = WorkflowData.Text(sale, "patient_name"),
                ["product_name"] = group.Key.ProductName,
                ["barcode"] = group.Key.Barcode,
                ["product_quantity"] = group.Count().ToString(CultureInfo.InvariantCulture),
                ["gross_price"] = Sum(group, "gross_price").ToString(CultureInfo.InvariantCulture),
                ["discount_amount"] = Sum(group, "discount_amount").ToString(CultureInfo.InvariantCulture),
                ["net_price"] = Sum(group, "net_price").ToString(CultureInfo.InvariantCulture),
                ["qr_codes"] = string.Join(", ", group.Select(line => WorkflowData.Text(line, "qr_code")).Where(value => value.Length > 0))
            });
    }

    private static decimal SaleNetAmount(JsonElement sale) =>
        sale.TryGetProperty("net_amount", out var amount) && amount.TryGetDecimal(out var value) ? value : 0m;

    private static decimal Sum(IEnumerable<JsonElement> values, string property) => values.Sum(value =>
        value.TryGetProperty(property, out var amount) && amount.TryGetDecimal(out var number) ? number : 0m);
}

public sealed class PatientProductSummaryWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_urun_ozeti", "Hasta ürün özeti",
        "Groups a resolved patient's sales and refunds by product to answer which products and how many boxes they received. Queue hasta_eslestir first for a named patient.",
        RequiredTools: ["sale.aggregate_records"], Parameters: SalesParameters("quantity"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (WorkflowData.PatientRecordId(context) is null) return WorkflowData.Failed("Hasta ürün özeti için hasta seçimi gerekiyor.");
        var patient = WorkflowData.PatientName(context);
        var result = await ExtendedWorkflowData.AggregateSalesAsync(context, "product", "quantity",
            WorkflowData.SortDirection(context, "descending"), 365, cancellationToken, retrieveAll: true);
        if (result.Status != AiWorkflowStepStatus.Completed)
            return result with { Message = $"{patient} adlı hastanın geçmiş satışları şu anda görüntülenemedi." };
        var rows = ExtendedWorkflowData.Rows(result.Output);
        var (start, end) = WorkflowData.DateRange(context, 365);
        var message = rows.Count == 0
            ? $"{patient} adlı hastanın {ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} döneminde ilaç satışı bulunamadı."
            : $"{patient} adlı hastanın {ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} döneminde aldığı {rows.Count:N0} farklı ürün bulundu.";
        return result with { Message = message };
    }
    internal static IReadOnlyDictionary<string, string> SalesParameters(string sortBy) => new Dictionary<string, string>
    {
        ["start_date"] = "Inclusive YYYY-MM-DD.", ["end_date"] = "Inclusive YYYY-MM-DD.",
        ["period_days"] = "Relative days.", ["period_months"] = "Relative months.", ["all_history"] = "true for all history.",
        ["calendar_period"] = "today, yesterday, this_week, last_week, this_month, last_month, this_year, last_year; calendar rather than rolling period.",
        ["sale_types"] = "retail,prescription.", ["include_refunds"] = "true or false.",
        ["payment_types"] = "Optional payment types.", ["statuses"] = "Optional statuses.",
        ["user_id"] = "Optional user ID.", ["is_debted"] = "Optional true/false.",
        ["sort_by"] = $"Defaults to {sortBy}.", ["sort_direction"] = "descending or ascending.", ["limit"] = "1-200."
    };
}

public sealed class PatientLatestProductsWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_son_urunleri_getir", "Hastanın son aldığı ürünleri getir",
        "Returns only the product or products in a resolved patient's newest sale and states the complete searched date range.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var patientId = WorkflowData.PatientRecordId(context);
        if (patientId is null) return WorkflowData.Failed("Son alınan ürün için hasta seçimi gerekiyor.");
        var hasPeriod = new[] { "start_date", "end_date", "period_days", "period_months", "all_history" }
            .Any(name => WorkflowData.Parameter(context, name) is not null);
        var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (!hasPeriod) parameters["all_history"] = "true";
        var effective = context with { Parameters = parameters };
        var (start, end) = WorkflowData.DateRange(effective, 365);
        var execution = await WorkflowData.Execute(effective, "sale.search_records", new
        {
            start_date = start, end_date = end, sale_types = WorkflowData.SaleTypes(effective),
            include_refunds = false, patient_record_id = patientId, include_lines = true,
            sort_direction = "newest_first", offset = 0, limit = 1,
            barcodes = Array.Empty<string>(), qr_codes = Array.Empty<string>(), payment_types = Array.Empty<string>(),
            statuses = Array.Empty<string>(), marked_only = false
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing("sale.search_records");
        if (!execution.IsSuccess) return WorkflowData.Failed("Hastanın son satın aldığı ürün alınamadı.");
        var sale = execution.Output.TryGetProperty("sales", out var sales) && sales.ValueKind == JsonValueKind.Array
            ? sales.EnumerateArray().FirstOrDefault(value => value.ValueKind == JsonValueKind.Object) : default;
        var patient = WorkflowData.PatientName(context);
        var period = $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)}";
        if (sale.ValueKind != JsonValueKind.Object)
            return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
                $"{patient} adlı hastanın {period} döneminde ilaç satışı bulunamadı.", WorkflowData.RowsOutput([],
                    new { start_date = start, end_date = end, total_count = 0 }));
        var lines = sale.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array
            ? lineArray.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).ToArray() : [];
        var rows = lines.GroupBy(line => (Barcode: WorkflowData.Text(line, "barcode"), Name: WorkflowData.Text(line, "product_name")))
            .Select(group => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["timestamp"] = WorkflowData.Text(sale, "timestamp"),
                ["reference_code"] = WorkflowData.Text(sale, "reference_code"),
                ["record_type"] = WorkflowData.Text(sale, "record_type"),
                    ["sale_type"] = WorkflowData.Text(sale, "sale_type"),
                ["patient_name"] = patient,
                ["product_name"] = group.Key.Name,
                ["barcode"] = group.Key.Barcode,
                ["product_quantity"] = group.Count().ToString(CultureInfo.InvariantCulture),
                ["net_amount"] = group.Sum(line => line.TryGetProperty("net_price", out var value) && value.TryGetDecimal(out var amount) ? amount : 0m).ToString(CultureInfo.InvariantCulture)
            }).ToArray();
        if (rows.Length == 0)
            rows = [new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["timestamp"] = WorkflowData.Text(sale, "timestamp"), ["reference_code"] = WorkflowData.Text(sale, "reference_code"),
                ["record_type"] = WorkflowData.Text(sale, "record_type"),
                    ["sale_type"] = WorkflowData.Text(sale, "sale_type"), ["patient_name"] = patient
            }];
        var timestamp = WorkflowData.Text(sale, "timestamp");
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            $"{patient} adlı hastanın {period} dönemi tarandı. En son satışı {AiWorkflowValueFormatter.Format("timestamp", timestamp)} tarihinde yapıldı; bu işlemdeki {rows.Length:N0} ürün gösteriliyor.",
            WorkflowData.RowsOutput(rows, new { start_date = start, end_date = end, total_count = rows.Length,
                highlight_field = "product_name",
                visible_columns = new[] { "timestamp", "reference_code", "record_type", "sale_type", "product_name", "barcode", "product_quantity", "net_amount" } }));
    }
}

public sealed class PurchaseQuantityRecommendationWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "alim_miktari_oner", "Alım miktarı öner",
        "Calculates a read-only purchase quantity recommendation for selected barcodes from local sales velocity, shelf stock, consigned stock and configured coverage.",
        RequiredTools: ["product.get_analysis"], Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "Selected exact barcodes.", ["days"] = "Analysis period, 1-365; default 90."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Alım miktarı önerisi için ilaç seçimi gerekiyor.");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var barcode in barcodes)
        {
            var execution = await WorkflowData.Execute(context, "product.get_analysis", new
            {
                barcode, days = WorkflowData.Integer(context, "days", 90, 1, 365),
                outgoing_type_code = (string?)null, incoming_type_code = (string?)null
            }, cancellationToken);
            if (execution is null) return WorkflowData.Missing("product.get_analysis");
            if (!execution.IsSuccess) return WorkflowData.Failed("Alım miktarı analizi alınamadı.");
            if (!execution.Output.TryGetProperty("found", out var found) || !found.GetBoolean()) continue;
            var recommended = WorkflowData.Text(execution.Output, "missing_stock_quantity");
            rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["barcode"] = barcode,
                ["medicine_name"] = ProductName(context, barcode),
                ["average_monthly_sales"] = WorkflowData.Text(execution.Output, "average_monthly_sales"),
                ["wanted_stock_months"] = WorkflowData.Text(execution.Output, "wanted_stock_months"),
                ["target_stock_quantity"] = WorkflowData.Text(execution.Output, "target_stock_quantity"),
                ["shelf_stock_quantity"] = WorkflowData.Text(execution.Output, "shelf_stock_quantity"),
                ["consigned_stock_quantity"] = WorkflowData.Text(execution.Output, "consigned_stock_quantity"),
                ["recommended_purchase_quantity"] = recommended,
                ["stock_comment"] = WorkflowData.Text(execution.Output, "stock_comment")
            });
        }
        if (rows.Count == 0)
            return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, "Seçilen ürün için alım miktarı hesaplanamadı.", WorkflowData.RowsOutput([]));
        var message = rows.Count == 1
            ? $"{rows[0].GetValueOrDefault("medicine_name", "Seçilen ürün")} için mevcut stok ve satış hızına göre önerilen alım adedi: {rows[0].GetValueOrDefault("recommended_purchase_quantity", "0")}."
            : $"{rows.Count:N0} ürün için mevcut stok ve satış hızına dayalı alım miktarı önerisi hazırlandı.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new { total_count = rows.Count,
                visible_columns = new[] { "medicine_name", "barcode", "recommended_purchase_quantity", "shelf_stock_quantity", "consigned_stock_quantity", "average_monthly_sales", "target_stock_quantity", "stock_comment" } }));
    }

    private static string ProductName(AiWorkflowContext context, string barcode)
    {
        foreach (var output in context.Outputs.Values.Reverse())
        {
            if (!output.TryGetProperty("selected_products", out var products) || products.ValueKind != JsonValueKind.Array) continue;
            var product = products.EnumerateArray().FirstOrDefault(item => WorkflowData.Text(item, "barcode") == barcode);
            var name = product.ValueKind == JsonValueKind.Object ? WorkflowData.Text(product, "name") : string.Empty;
            if (name.Length > 0) return name;
        }
        return barcode;
    }
}

public sealed class SalesDistributionWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "satis_dagilimi_analizi", "Satış dağılımını analiz et",
        "Groups filtered sales/refunds by day, month, sale type, record type, product, user or patient and returns counts, quantities, revenue, discount and profit.",
        RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("net_revenue"), StringComparer.Ordinal)
        {
            ["barcodes"] = "Optional exact barcodes.", ["group_by"] = "day, month, sale_type, record_type, product, user, or patient.",
            ["minimum_total"] = "Optional transaction minimum.", ["maximum_total"] = "Optional transaction maximum."
        });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var groupBy = WorkflowData.GroupBy(context, "sale_type");
        if (groupBy == "none") groupBy = "sale_type";
        return ExtendedWorkflowData.AggregateSalesAsync(context, groupBy,
            WorkflowData.SortBy(context, groupBy is "day" or "month" ? "group_key" : "net_revenue"),
            WorkflowData.SortDirection(context, groupBy is "day" or "month" ? "ascending" : "descending"), 90, cancellationToken);
    }
}

public sealed class ProfitabilityAnalysisWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "karlilik_analizi", "Kârlılık analizi",
        "Ranks filtered sales by product or user using immutable recorded costs and explicitly exposes missing-cost quantities.",
        RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("profit"), StringComparer.Ordinal)
        { ["barcodes"] = "Optional exact barcodes.", ["group_by"] = "product or user." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var group = WorkflowData.GroupBy(context, "product");
        if (group is not ("product" or "user")) group = "product";
        return ExtendedWorkflowData.AggregateSalesAsync(context, group, "profit", WorkflowData.SortDirection(context, "descending"), 90, cancellationToken);
    }
}

public sealed class DiscountAnalysisWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "iskonto_analizi", "İskonto analizi", "Groups filtered sale/refund discounts by product, user, patient, month or sale type.",
        RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("discount_total"), StringComparer.Ordinal)
        { ["barcodes"] = "Optional exact barcodes.", ["group_by"] = "product, user, patient, month, or sale_type." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default) =>
        ExtendedWorkflowData.AggregateSalesAsync(context, WorkflowData.GroupBy(context, "product"), "discount_total",
            WorkflowData.SortDirection(context, "descending"), 90, cancellationToken,
            retrieveAll: WorkflowData.PatientRecordId(context) is not null,
            visibleColumns: ["group_label", "record_count", "sale_count", "refund_count", "quantity", "gross_revenue", "discount_total", "net_revenue"]);
}

public sealed class RefundAnalysisWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "iade_analizi", "İade analizi",
        "Analyzes refunds together with sales by product, month, user, patient or sale type; refund counts and signed net effect come from persisted records.",
        RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("net_revenue"), StringComparer.Ordinal)
        { ["barcodes"] = "Optional exact barcodes.", ["group_by"] = "product, month, user, patient, or sale_type." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["include_refunds"] = "true" };
        return ExtendedWorkflowData.AggregateSalesAsync(context with { Parameters = parameters }, WorkflowData.GroupBy(context, "product"),
            "net_revenue", WorkflowData.SortDirection(context, "ascending"), 90, cancellationToken);
    }
}

public sealed class QrHistoryWorkflow : IAiWorkflow
{
    private const string Tool = "qr.get_history";
    public AiWorkflowDefinition Definition { get; } = new(
        "karekod_gecmisini_getir", "Karekod geçmişini getir", "Gets the complete bounded chronological movement history for one exact QR code.",
        RequiredTools: [Tool], Parameters: new Dictionary<string, string> { ["qr_code"] = "Exact QR code.", ["limit"] = "1-200." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var qrCode = WorkflowData.Parameter(context, "qr_code");
        if (qrCode is null) return WorkflowData.Failed("Karekod geçmişi için tam karekod gerekiyor.");
        var execution = await WorkflowData.Execute(context, Tool, new { qr_code = qrCode, limit = WorkflowData.Integer(context, "limit", 200, 1, 200) }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Karekod geçmişi alınamadı.");
        var rows = execution.Output.TryGetProperty("movements", out var movements) && movements.ValueKind == JsonValueKind.Array
            ? movements.EnumerateArray().Select(WorkflowData.FlatRow).Reverse().ToArray() : [];
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            rows.Length == 0 ? "Karekod hareketi bulunamadı." : $"{rows.Length} karekod hareketi kronolojik olarak bulundu.",
            WorkflowData.RowsOutput(rows, new { total_count = execution.Output.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var count) ? count : rows.Length,
                visible_columns = new[] { "movement_date", "movement_type", "direction", "address", "user", "reference_code" } }));
    }
}

public sealed class PeriodComparisonWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "donemleri_karsilastir", "Dönemleri karşılaştır",
        "Compares persisted sales/revenue metrics for a requested period with the immediately preceding equal-length period without another AI call.",
        RequiredTools: ["sale.aggregate_records"], Parameters: new Dictionary<string, string>
        {
            ["period_days"] = "Current relative period in days.", ["period_months"] = "Current relative period in months.",
            ["period_kind"] = "Optional last_full_week or last_full_month for completed calendar periods.",
            ["comparison_kind"] = "Optional rolling_vs_previous_calendar_month.",
            ["start_date"] = "Current inclusive YYYY-MM-DD.", ["end_date"] = "Current inclusive YYYY-MM-DD.",
            ["sale_types"] = "retail,prescription.", ["include_refunds"] = "true or false.",
            ["barcodes"] = "Optional exact barcodes.", ["metric"] = "net_revenue, gross_revenue, institution_revenue, quantity, or record_count."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        DateOnly currentStart;
        DateOnly currentEnd;
        DateOnly previousStart;
        DateOnly previousEnd;
        var currentLabel = "Karşılaştırılan dönem";
        var previousLabel = "Önceki eşit dönem";
        var message = "İki eşit dönem karşılaştırmaya hazırlandı.";
        var comparisonKind = WorkflowData.Parameter(context, "comparison_kind");
        if (string.Equals(WorkflowData.Parameter(context, "period_kind"), "last_full_month", StringComparison.Ordinal))
        {
            var currentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
            currentEnd = currentMonth.AddDays(-1);
            currentStart = new DateOnly(currentEnd.Year, currentEnd.Month, 1);
            previousEnd = currentStart.AddDays(-1);
            previousStart = new DateOnly(previousEnd.Year, previousEnd.Month, 1);
            currentLabel = "Son tamamlanmış ay";
            previousLabel = "Önceki tamamlanmış ay";
            message = "Son iki tamamlanmış takvim ayı karşılaştırmaya hazırlandı.";
        }
        else if (string.Equals(WorkflowData.Parameter(context, "period_kind"), "last_full_week", StringComparison.Ordinal))
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
            currentEnd = today.AddDays(-daysSinceMonday - 1);
            currentStart = currentEnd.AddDays(-6);
            var span = currentEnd.DayNumber - currentStart.DayNumber + 1;
            previousEnd = currentStart.AddDays(-1);
            previousStart = previousEnd.AddDays(-(span - 1));
        }
        else
        {
            var (currentStartText, currentEndText) = WorkflowData.DateRange(context, 30);
            currentStart = DateOnly.ParseExact(currentStartText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            currentEnd = DateOnly.ParseExact(currentEndText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.Equals(comparisonKind, "rolling_vs_previous_calendar_month", StringComparison.Ordinal))
            {
                var currentMonth = new DateOnly(currentEnd.Year, currentEnd.Month, 1);
                previousStart = currentMonth.AddMonths(-1);
                previousEnd = currentMonth.AddDays(-1);
                currentLabel = "Son dönem";
                previousLabel = "Geçen takvim ayı";
                message = "Son dönem ile geçen takvim ayı karşılaştırmaya hazırlandı.";
            }
            else
            {
                var span = currentEnd.DayNumber - currentStart.DayNumber + 1;
                previousEnd = currentStart.AddDays(-1);
                previousStart = previousEnd.AddDays(-(span - 1));
            }
        }
        var current = await Summary(context, currentStart, currentEnd, currentLabel, cancellationToken);
        if (current.Status != AiWorkflowStepStatus.Completed) return current;
        var priorParameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["start_date"] = previousStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end_date"] = previousEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        priorParameters.Remove("period_days"); priorParameters.Remove("period_months");
        var prior = await Summary(context with { Parameters = priorParameters }, previousStart, previousEnd, previousLabel, cancellationToken);
        if (prior.Status != AiWorkflowStepStatus.Completed) return prior;
        var rows = ExtendedWorkflowData.Rows(current.Output).Concat(ExtendedWorkflowData.Rows(prior.Output)).ToArray();
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new { visible_columns = new[] { "period", "start_date", "end_date_inclusive", "sale_count", "refund_count", "product_quantity", "gross_sales", "refund_total", "net_revenue", "institution_revenue" } }));
    }

    private static async Task<AiWorkflowStepResult> Summary(AiWorkflowContext context, DateOnly start, DateOnly end, string label, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["start_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end_date"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        parameters.Remove("period_days"); parameters.Remove("period_months"); parameters.Remove("period_kind"); parameters.Remove("comparison_kind");
        var result = await new SalesRevenueSummaryWorkflow().ExecuteAsync(context with { Parameters = parameters }, cancellationToken);
        if (result.Status != AiWorkflowStepStatus.Completed) return result;
        var rows = ExtendedWorkflowData.Rows(result.Output).Select(row =>
        {
            var copy = new Dictionary<string, string>(row, StringComparer.Ordinal) { ["period"] = label };
            return (IReadOnlyDictionary<string, string>)copy;
        });
        return result with { Output = WorkflowData.RowsOutput(rows) };
    }
}

public sealed class PatientSalesTimelineWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_satis_zaman_cizelgesi", "Hasta satış zaman çizelgesi",
        "Gets a resolved patient's sales and refunds in chronological order for timeline presentation. Queue hasta_eslestir first.",
        RequiredTools: ["sale.search_records"], Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("group_key"), StringComparer.Ordinal)
        { ["barcodes"] = "Optional exact barcodes.", ["limit"] = "1-200 records." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (WorkflowData.PatientRecordId(context) is null) return Task.FromResult(WorkflowData.Failed("Hasta zaman çizelgesi için hasta seçimi gerekiyor."));
        var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        { ["sort_direction"] = "oldest_first" };
        return new SeeSalesWorkflow().ExecuteAsync(context with { Parameters = parameters }, cancellationToken);
    }
}

public sealed class PricePeriodComparisonWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "fiyat_donemlerini_karsilastir", "Fiyat dönemlerini karşılaştır",
        "Compares each exact barcode's latest recorded prices with the values recorded at or before a deterministic earlier date.",
        RequiredTools: ["product.get_price_history"], Parameters: new Dictionary<string, string>
        { ["barcodes"] = "One or more exact barcodes.", ["period_days"] = "Lookback days.", ["period_months"] = "Lookback months.", ["comparison_date"] = "Exact YYYY-MM-DD." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Fiyat karşılaştırması için barkod gerekiyor.");
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly target;
        if (DateOnly.TryParseExact(WorkflowData.Parameter(context, "comparison_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) target = exact;
        else if (WorkflowData.Integer(context, "period_months", 0, 0, 120) is > 0 and var months) target = today.AddMonths(-months);
        else target = today.AddDays(-WorkflowData.Integer(context, "period_days", 30, 1, 3650));
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var barcode in barcodes)
        {
            var execution = await WorkflowData.Execute(context, "product.get_price_history", new { barcode, offset = 0, limit = 500 }, cancellationToken);
            if (execution is null) return WorkflowData.Missing("product.get_price_history");
            if (!execution.IsSuccess) return WorkflowData.Failed("Fiyat geçmişi alınamadı.");
            var points = execution.Output.TryGetProperty("points", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).ToArray() : [];
            if (points.Length == 0) continue;
            var latest = points[^1];
            var previous = points.LastOrDefault(point => DateTime.TryParse(WorkflowData.Text(point, "effective_date"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) && DateOnly.FromDateTime(date) <= target);
            rows.Add(new Dictionary<string, string>
            {
                ["barcode"] = barcode, ["comparison_date"] = target.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["old_sale_price"] = previous.ValueKind == JsonValueKind.Object ? WorkflowData.Text(previous, "sale_price") : "Kayıt yok",
                ["current_sale_price"] = WorkflowData.Text(latest, "sale_price"),
                ["old_warehouse_price"] = previous.ValueKind == JsonValueKind.Object ? WorkflowData.Text(previous, "warehouse_price") : "Kayıt yok",
                ["current_warehouse_price"] = WorkflowData.Text(latest, "warehouse_price")
            });
        }
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, $"{rows.Count} ürün için fiyat dönemleri karşılaştırıldı.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class SectionedSummaryWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "bolumlu_ozet_goster", "Bölümlü özet göster",
        "Renders multiple preceding structured workflow outputs in one searchable table with an explicit section column.",
        ProducesUserResponse: true, Parameters: new Dictionary<string, string> { ["title"] = "Short English overall title." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var rows = context.Outputs.Where(pair => pair.Value.TryGetProperty("rows", out var values) && values.ValueKind == JsonValueKind.Array)
            .SelectMany(pair => ExtendedWorkflowData.Rows(pair.Value).Take(50).Select(row =>
            {
                var combined = new Dictionary<string, string>(row, StringComparer.Ordinal)
                {
                    ["section"] = pair.Key
                };
                return (IReadOnlyDictionary<string, string>)combined;
            }))
            .Take(200)
            .ToArray();
        var output = WorkflowData.RowsOutput(rows, new { total_count = rows.Length, visible_columns = new[] { "section", "group_label", "name", "barcode", "record_count", "quantity", "net_revenue", "sale_price", "shelf_stock_quantity" } });
        var tableContext = context with
        {
            Outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["bolumlu_sonuc"] = output },
            Parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["color_modes"] = "section|Bölüme göre|ocean",
                ["default_color_mode"] = "section"
            }
        };
        var table = await new DisplayTableWorkflow().ExecuteAsync(tableContext, cancellationToken);
        return table with { Message = rows.Length == 0 ? "Gösterilecek sonuç bulunamadı." : $"{rows.Length} sonuç bölümlü tabloda gösteriliyor.", Output = output };
    }
}

public sealed class InformationCardsWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "bilgi_kartlari_goster", "Bilgi kartları göster", "Renders one or a few structured records as readable information cards instead of a table.",
        ProducesUserResponse: true, Parameters: new Dictionary<string, string> { ["title"] = "Short English title." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Presentation(context, AiWorkflowContentKind.InformationCards, "Bilgiler"));
    internal static AiWorkflowStepResult Presentation(AiWorkflowContext context, AiWorkflowContentKind kind, string fallbackTitle)
    {
        var source = ExtendedWorkflowData.LastRows(context);
        if (source is null) return WorkflowData.Failed("Gösterilecek yapılandırılmış veri bulunamadı.");
        var rows = ExtendedWorkflowData.Rows(source.Value);
        var labels = PreviousResultData.ReadLabels(source.Value);
        string Label(string field) => labels.TryGetValue(field, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label.Trim()
            : WorkflowData.Label(field);
        var highlightField = MetadataText(source.Value, "highlight_field");
        var items = rows.Select((row, index) =>
        {
            var titleField = FirstField(row, "product_name", "medicine_name", "name", "group_label", "period", "reference_code", "barcode", "day");
            var title = titleField is null ? $"{index + 1}. kayıt" : row[titleField];
            var valueField = highlightField is { Length: > 0 } && !string.IsNullOrWhiteSpace(row.GetValueOrDefault(highlightField))
                ? highlightField
                : kind == AiWorkflowContentKind.MetricCards
                    ? new[] { "net_revenue", "gross_revenue", "quantity", "record_count", "sale_price" }
                        .FirstOrDefault(key => !string.IsNullOrWhiteSpace(row.GetValueOrDefault(key)))
                    : null;
            var value = valueField is null ? null : $"{Label(valueField)}: {FormatValue(valueField, row[valueField])}";
            var subtitleField = FirstField(row, "barcode", "timestamp", "movement_date");
            var subtitle = subtitleField is null ? null : row[subtitleField];
            var detail = string.Join(Environment.NewLine, row.Where(pair => pair.Value.Length > 0 && pair.Value != title && pair.Value != subtitle && pair.Key != valueField)
                .Select(pair => $"{Label(pair.Key)}: {FormatValue(pair.Key, pair.Value)}"));
            return new AiWorkflowContentItem(titleField is null ? title : FormatValue(titleField, title),
                subtitleField is null ? null : FormatValue(subtitleField, subtitle!), value, detail);
        }).ToArray();
        var presentation = new AiWorkflowContentPresentation(kind, WorkflowData.Parameter(context, "title") ?? fallbackTitle, items);
        var sourceMessage = WorkflowData.PreviousResultMessage(context);
        var message = sourceMessage.Length > 0 ? sourceMessage : items.Length == 0 ? "Filtrelere uyan kayıt bulunamadı." : $"{items.Length} sonuç gösteriliyor.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            source.Value.Clone(), Content: presentation);
    }
    private static string? FirstField(IReadOnlyDictionary<string, string> row, params string[] keys) =>
        keys.FirstOrDefault(key => !string.IsNullOrWhiteSpace(row.GetValueOrDefault(key)));

    private static string? MetadataText(JsonElement source, string name) =>
        source.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatValue(string key, string value) => AiWorkflowValueFormatter.Format(key, value);
}

public sealed class MetricCardsWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("metrik_kartlari_goster", "Metrik kartları göster",
        "Renders compact totals, revenue, counts or comparison results as metric cards.", ProducesUserResponse: true,
        Parameters: new Dictionary<string, string> { ["title"] = "Short English title." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(InformationCardsWorkflow.Presentation(context, AiWorkflowContentKind.MetricCards, "Özet"));
}
public sealed class RankedListWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("sirali_liste_goster", "Sıralı liste göster",
        "Renders a short already-sorted result as a ranked card list.", ProducesUserResponse: true,
        Parameters: new Dictionary<string, string> { ["title"] = "Short English title." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(InformationCardsWorkflow.Presentation(context, AiWorkflowContentKind.RankedList, "Sıralı Sonuçlar"));
}
public sealed class TimelineWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("zaman_cizelgesinde_goster", "Zaman çizelgesinde göster",
        "Renders chronological sale, refund, price or QR movement records as a readable timeline.", ProducesUserResponse: true,
        Parameters: new Dictionary<string, string> { ["title"] = "Short English title." });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(InformationCardsWorkflow.Presentation(context, AiWorkflowContentKind.Timeline, "Zaman Çizelgesi"));
}

public sealed class RecognizeOrderIntentWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("siparis_istegini_tani", "Sipariş isteğini tanı",
        "Recognizes explicit purchase/order commands (English or Turkish), NOT advice such as 'what should I purchase?'. Retains quantity, medicine query or resolved barcode context and explains ordering is unavailable. Performs no draft, login, cart, send or database operation.",
        ProducesUserResponse: true, Parameters: new Dictionary<string, string>
        {
            ["quantity"] = "Requested pack count, when supplied.",
            ["medicine_query"] = "Supplied medicine name. Never invent a barcode."
        });
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var subject = WorkflowData.Parameter(context, "medicine_query") ?? "";
        var barcodes = WorkflowData.Barcodes(context);
        var quantity = WorkflowData.Parameter(context, "quantity") ?? "";
        if (subject.Length == 0 && barcodes.Count > 0) subject = string.Join(", ", barcodes);
        var message = subject.Length == 0
            ? "Sipariş için hangi ürünü istediğinizi belirtin. Bu sürümde satın alma işlemi yapılamıyor."
            : $"Sipariş isteği: {subject} · İstenen kutu: {(quantity.Length > 0 ? quantity : "belirtilmedi")}.";
        message += "\nSipariş oluşturma ve sepete ekleme bağlantısı henüz bulunmuyor; satın alma yapılmadı.";
        return Task.FromResult(new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            JsonSerializer.SerializeToElement(new { recognized = true, executed = false, medicine_query = subject, quantity, barcodes })));
    }
}

public abstract class MissingCapabilityWorkflow(string name, string displayName, string description, params string[] tools) : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(name, displayName, description, RequiredTools: tools);
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default) => Task.FromResult(WorkflowData.Missing(tools));
}
public sealed class ExpiryAnalysisWorkflow() : MissingCapabilityWorkflow("son_kullanma_analizi", "Son kullanma tarihi analizi",
    "Analyzes expiring stock quantities, cost and FEFO order from lot/QR expiry snapshots.", "stock.analyze_expiry");
public sealed class PurchaseAnalysisWorkflow() : MissingCapabilityWorkflow("depo_alis_analizi", "Depo alış analizi",
    "Analyzes persisted depot purchases, quantities and historical acquisition costs by depot and barcode.", "purchase.aggregate_records");
public sealed class ForecastWorkflow() : MissingCapabilityWorkflow("tahmin_analizi", "Tahmin analizi",
    "Produces deterministic documented forecasts, moving averages and anomaly baselines from complete time series.", "analytics.forecast_series");
