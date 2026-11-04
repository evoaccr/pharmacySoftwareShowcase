using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public abstract class NamedSaleAnalysisWorkflow : IAiWorkflow
{
    private readonly string toolName;
    private readonly string defaultKind;
    protected NamedSaleAnalysisWorkflow(string name, string displayName, string description, string toolName, string defaultKind)
    {
        this.toolName = toolName;
        this.defaultKind = defaultKind;
        Definition = new AiWorkflowDefinition(name, displayName, description, RequiredTools: [toolName],
            Parameters: new Dictionary<string, string>
            {
                ["analysis_kind"] = "Named deterministic analysis described by this workflow.",
                ["start_date"] = "Optional inclusive YYYY-MM-DD.",
                ["end_date"] = "Optional inclusive YYYY-MM-DD.",
                ["period_days"] = "Optional rolling day count.",
                ["all_history"] = "true for complete history.",
                ["sale_types"] = "Optional retail and/or prescription.",
                ["include_refunds"] = "Whether refunds are included.",
                ["patient_record_id"] = "Resolved patient ID; never invent it.",
                ["reference_code"] = "Optional exact P/G transaction reference.",
                ["minimum_quantity"] = "Optional minimum product quantity.",
                ["limit"] = "Maximum displayed rows, 1-200."
            });
    }

    private static decimal ExtractPercent(string request)
    {
        var match = System.Text.RegularExpressions.Regex.Match(request, @"(?:%|yüzde\s*)(?<value>[0-9]+(?:[.,][0-9]+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && decimal.TryParse(match.Groups["value"].Value.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private static string? ExtractReference(string request)
    {
        var match = System.Text.RegularExpressions.Regex.Match(request, @"(?<![A-Za-z0-9])[PG][0-9]+(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string NormalizeSaleKind(string value) => value.ToLowerInvariant() switch
    {
        "duplicate_references" => "duplicate_reference",
        "non_positive_totals" or "nonpositive_totals" => "nonpositive_total",
        "payment_totals" => "payment_summary",
        "details" => "payment_summary",
        "monthly_refund_ratio" => "refund_rate_by_month",
        "missing_product_lines" => "empty_lines",
        _ => value.ToLowerInvariant()
    };

    public AiWorkflowDefinition Definition { get; }

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var (start, end) = WorkflowData.DateRange(context, 30);
        var kind = NormalizeSaleKind(WorkflowData.Parameter(context, "analysis_kind") ?? defaultKind);
        var execution = await WorkflowData.Execute(context, toolName, new
        {
            analysis_kind = kind,
            start_date = start,
            end_date = end,
            sale_types = WorkflowData.SaleTypes(context),
            patient_record_id = WorkflowData.PatientRecordId(context),
            reference_code = ExtractReference(context.OriginalRequest),
            include_refunds = WorkflowData.NullableBoolean(context, "include_refunds") ?? true,
            minimum_quantity = WorkflowData.Integer(context, "minimum_quantity", 1, 1, 1_000_000),
            minimum_percent = ExtractPercent(context.OriginalRequest),
            limit = WorkflowData.Integer(context, "limit", 100, 1, 200),
            scan_limit = 10_000
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(toolName);
        if (!execution.IsSuccess) return WorkflowData.Failed("Analiz alınamadı.");
        var rows = ExtendedWorkflowData.Rows(execution.Output);
        var total = execution.Output.TryGetProperty("total_matching_count", out var count) && count.TryGetInt32(out var parsed)
            ? parsed : rows.Count;
        var scanned = execution.Output.TryGetProperty("source_record_count", out var source) && source.TryGetInt32(out var sourceCount)
            ? sourceCount : 0;
        var truncated = execution.Output.TryGetProperty("source_truncated", out var cut) && cut.ValueKind == JsonValueKind.True;
        var message = total == 0
            ? $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} döneminde bu analize uyan kayıt bulunamadı."
            : $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} döneminde {total:N0} sonuç bulundu.";
        if (truncated) message += $" Kaynak taraması {scanned:N0} kayıtla sınırlandı.";
        var columns = rows.FirstOrDefault()?.Keys.ToArray() ?? [];
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                analysis_kind = kind, total_count = total, source_record_count = scanned,
                source_truncated = truncated, visible_columns = columns
            }));
    }
}

public sealed class PaymentAnalysisWorkflow() : NamedSaleAnalysisWorkflow(
    "odeme_dagilimi_analizi", "Ödeme dağılımı analizi",
    "Calculates payment totals, average basket, or monthly refund ratios from bounded immutable sale records.",
    "sale.aggregate_payments", "payment_summary");

public sealed class BasketAnalysisWorkflow() : NamedSaleAnalysisWorkflow(
    "sepet_analizi", "Sepet analizi",
    "Calculates basket size, weekday/weekend, discount-cohort, patient-average, maximum-sale, and product-consistency analyses.",
    "sale.analyze_baskets", "items_per_sale_by_month");

public sealed class SalesConsistencyWorkflow() : NamedSaleAnalysisWorkflow(
    "satis_tutarlilik_kontrolu", "Satış tutarlılık kontrolü",
    "Audits empty lines, payment mismatches, duplicate references, non-positive totals, and refund provenance without changing data.",
    "sale.audit_record_consistency", "empty_lines");

public sealed class CatalogAuditWorkflow : IAiWorkflow
{
    private const string Tool = "product.aggregate_catalog";
    public AiWorkflowDefinition Definition { get; } = new(
        "urun_katalog_analizi", "Ürün kataloğu analizi",
        "Runs bounded catalogue-wide checks for missing fields, duplicate barcodes, company groups, and shelf-stock ranges.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["analysis_kind"] = "missing_company_with_ingredient, company_counts, ingredient_price_spread, price_change_frequency, duplicate_barcode, stock_range, stock_value_bottom, field_completeness, or positive_stock_missing_sale_price.",
            ["search"] = "Optional catalogue search text.",
            ["only_on_market"] = "true to keep active products only.",
            ["stock_minimum"] = "Optional inclusive stock minimum.",
            ["stock_maximum"] = "Optional inclusive stock maximum.",
            ["limit"] = "Maximum displayed rows, 1-500."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var kind = (WorkflowData.Parameter(context, "analysis_kind") ?? "field_completeness").ToLowerInvariant() switch
        {
            "duplicate_barcodes" => "duplicate_barcode",
            "shelf_stock_range" => "stock_range",
            "missing_fields" or "missing_manufacturer_with_active_ingredient" => "missing_company_with_ingredient",
            _ => (WorkflowData.Parameter(context, "analysis_kind") ?? "field_completeness").ToLowerInvariant()
        };
        var min = ParseNullable(context, "stock_minimum");
        var max = ParseNullable(context, "stock_maximum");
        var execution = await WorkflowData.Execute(context, Tool, new
        {
            analysis_kind = kind,
            search = WorkflowData.Parameter(context, "search") ?? string.Empty,
            only_on_market = WorkflowData.NullableBoolean(context, "only_on_market") ?? false,
            stock_minimum = min,
            stock_maximum = max,
            start_date = WorkflowData.DateRange(context, 365).Start,
            end_date = WorkflowData.DateRange(context, 365).End,
            limit = WorkflowData.Integer(context, "limit", 100, 1, 500),
            scan_limit = 20_000
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Ürün kataloğu analizi alınamadı.");
        var rows = ExtendedWorkflowData.Rows(execution.Output);
        var total = execution.Output.TryGetProperty("total_matching_count", out var count) && count.TryGetInt32(out var parsed)
            ? parsed : rows.Count;
        var truncated = execution.Output.TryGetProperty("source_truncated", out var cut) && cut.ValueKind == JsonValueKind.True;
        var message = total == 0 ? "Bu katalog analizine uyan ürün bulunamadı." : $"{total:N0} katalog sonucu bulundu.";
        if (truncated) message += " Sonuç, belirtilen güvenli tarama sınırı içinden hesaplandı.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                analysis_kind = kind, total_count = total, source_truncated = truncated,
                visible_columns = rows.FirstOrDefault()?.Keys.ToArray() ?? []
            }));
    }

    private static int? ParseNullable(AiWorkflowContext context, string name) =>
        int.TryParse(WorkflowData.Parameter(context, name), out var value) ? value : null;
}

public sealed class InventorySalesIntersectionWorkflow : IAiWorkflow
{
    private const string Tool = "product.analyze_inventory_sales";
    public AiWorkflowDefinition Definition { get; } = new(
        "stok_satis_kesisim_analizi", "Stok ve satış kesişim analizi",
        "Lists which products to purchase across the active catalogue. General purchase/replenishment advice uses analysis_kind=replenishment, period_days for observed sales history and target_days for desired future coverage. Computes positive ceil(sold packs / observed days * target_days - recorded shelf stock). Independent of per-product analysis tools; no medicine selection needed. Also supports dead stock and stock coverage. Never orders; excludes refunds and incoming orders from the estimate.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["analysis_kind"] = "replenishment, dead_stock, sold_over_stock, sold_out_recently, or coverage.",
            ["target_days"] = "Replenishment target coverage, 1-365 days; default 30.",
            ["start_date"] = "Inclusive YYYY-MM-DD.", ["end_date"] = "Inclusive YYYY-MM-DD.",
            ["period_days"] = "Relative day count.", ["period_months"] = "Relative month count.",
            ["minimum_quantity"] = "Minimum sold quantity.", ["maximum_coverage_days"] = "Optional maximum coverage in days.", ["limit"] = "Maximum rows, 1-500."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var (start, end) = WorkflowData.DateRange(context, 30);
        var kind = (WorkflowData.Parameter(context, "analysis_kind") ?? "sold_out_recently").ToLowerInvariant() switch
        {
            "recently_sold_out" => "sold_out_recently",
            "limited_stock_coverage" => "coverage",
            _ => (WorkflowData.Parameter(context, "analysis_kind") ?? "sold_out_recently").ToLowerInvariant()
        };
        var execution = await WorkflowData.Execute(context, Tool, new
        {
            analysis_kind = kind, start_date = start, end_date = end,
            minimum_quantity = WorkflowData.Integer(context, "minimum_quantity", 1, 1, 1_000_000),
            maximum_coverage_days = WorkflowData.NullableDecimal(context, "maximum_coverage_days"),
            target_days = WorkflowData.Integer(context, "target_days", 30, 1, 365),
            limit = WorkflowData.Integer(context, "limit", 100, 1, 500),
            catalog_scan_limit = 50_000
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Satış ve raf stokları şu anda okunamadığı için güvenilir bir alım önerisi hazırlayamadım. Lütfen tekrar deneyin.");
        var rows = ExtendedWorkflowData.Rows(execution.Output);
        var total = execution.Output.TryGetProperty("total_matching_count", out var count) && count.TryGetInt32(out var parsed)
            ? parsed : rows.Count;
        var truncated = execution.Output.TryGetProperty("catalog_truncated", out var cut) && cut.ValueKind == JsonValueKind.True;
        var period = $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)}";
        var message = total == 0 ? $"{period} döneminde ölçüte uyan ürün bulunamadı." : $"{period} döneminde {total:N0} ürün bulundu.";
        if (kind == "replenishment")
        {
            var targetDays = WorkflowData.Integer(context, "target_days", 30, 1, 365);
            message = total == 0
                ? $"{period} satışlarına ve kayıtlı raf stoklarına göre ek alım gerektiren aktif ürün bulunamadı."
                : $"{period} satışlarına göre {targetDays} günlük stok hedefi için {total:N0} üründe alım önerisi var; {rows.Count:N0} ürün gösteriliyor.";
            message += "\nBu tahmin kayıtlı raf stoku ve satılan kutu sayısını kullanır; iadeler ve beklenen siparişler hesaba katılmaz. Satın alma yapılmadı.";
        }
        if (truncated) message += " Katalog güvenli tarama sınırında kesildi.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                analysis_kind = kind, total_count = total, catalog_truncated = truncated,
                start_date = start, end_date = end,
                target_days = WorkflowData.Integer(context, "target_days", 30, 1, 365),
                visible_columns = kind == "replenishment"
                    ? new[] { "medicine_name", "recommended_purchase_quantity", "shelf_stock_quantity", "sold_quantity", "coverage_days" }
                    : new[] { "medicine_name", "barcode", "shelf_stock_quantity", "sold_quantity", "net_revenue", "coverage_days" },
                column_labels = new Dictionary<string, string>
                {
                    ["medicine_name"] = "İlaç", ["recommended_purchase_quantity"] = "Önerilen Alım Adedi",
                    ["shelf_stock_quantity"] = "Raf Stoku", ["sold_quantity"] = "Dönemde Satılan Kutu",
                    ["coverage_days"] = "Tahmini Stok Süresi (Gün)", ["target_stock_quantity"] = "Hedef Stok"
                }
            }));
    }
}
