using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public sealed class SalesCohortAnalysisWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "satis_kohort_analizi", "Satış gruplarını karşılaştır",
        "Compares sale/refund cohorts by sale type, record type, user, patient, day, or month with counts, quantity, discounts, public contribution, revenue, and averages.",
        RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("net_revenue"), StringComparer.Ordinal)
        {
            ["group_by"] = "sale_type, record_type, user, patient, day, or month.",
            ["minimum_sale_count"] = "Optional minimum sale count.", ["limit"] = "Maximum rows, 1-500."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var groupBy = WorkflowData.GroupBy(context, "sale_type");
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, groupBy, "net_revenue", "descending", 30,
            cancellationToken, includeRefunds: WorkflowData.NullableBoolean(context, "include_refunds") ?? true);
        if (fetch.Failure is not null) return fetch.Failure;
        var minimum = WorkflowData.Integer(context, "minimum_sale_count", 0, 0, 1_000_000);
        var rows = fetch.Items.Where(item => VerifiedAnalyticsData.Integer(item, "sale_count") >= minimum)
            .Take(WorkflowData.Integer(context, "limit", 100, 1, 500)).Select(item =>
            {
                var sales = VerifiedAnalyticsData.Number(item, "sale_count");
                var discount = VerifiedAnalyticsData.Number(item, "discount_total");
                var institution = VerifiedAnalyticsData.Number(item, "institution_revenue");
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["group_label"] = VerifiedAnalyticsData.Text(item, "group_label"),
                    ["record_count"] = VerifiedAnalyticsData.Text(item, "record_count"),
                    ["sale_count"] = VerifiedAnalyticsData.Text(item, "sale_count"),
                    ["refund_count"] = VerifiedAnalyticsData.Text(item, "refund_count"),
                    ["quantity"] = VerifiedAnalyticsData.Text(item, "quantity"),
                    ["gross_revenue"] = VerifiedAnalyticsData.Text(item, "gross_revenue"),
                    ["discount_total"] = VerifiedAnalyticsData.DecimalText(discount),
                    ["net_revenue"] = VerifiedAnalyticsData.Text(item, "net_revenue"),
                    ["institution_revenue"] = VerifiedAnalyticsData.DecimalText(institution),
                    ["average_basket"] = VerifiedAnalyticsData.DecimalText(sales == 0 ? 0 : VerifiedAnalyticsData.Number(item, "net_revenue") / sales),
                    ["average_discount"] = VerifiedAnalyticsData.DecimalText(sales == 0 ? 0 : discount / sales),
                    ["average_institution_amount"] = VerifiedAnalyticsData.DecimalText(sales == 0 ? 0 : institution / sales)
                };
            }).ToArray();
        var (start, end) = WorkflowData.DateRange(context);
        return new(AiWorkflowStepStatus.Completed, $"{Format(start)}–{Format(end)} döneminde {rows.Length} satış grubu karşılaştırıldı.",
            WorkflowData.RowsOutput(rows));
    }
    private static string Format(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy");
}

public sealed class WeeklyRefundRateWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "haftalik_iade_orani", "Haftalık iade oranı",
        "Calculates weekly sale/refund amounts and the refund-to-gross percentage for the requested sale type and period.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 90, false, cancellationToken, includeRefunds: true);
        if (fetch.Failure is not null) return fetch.Failure;
        var rows = fetch.Items.Where(item => VerifiedAnalyticsData.Timestamp(item).HasValue)
            .GroupBy(item => StartOfWeek(VerifiedAnalyticsData.Timestamp(item)!.Value))
            .OrderBy(group => group.Key).Select(group =>
            {
                var sales = group.Where(item => !IsRefund(item)).ToArray();
                var refunds = group.Where(IsRefund).ToArray();
                var gross = sales.Sum(item => VerifiedAnalyticsData.Number(item, "gross_amount"));
                var refund = refunds.Sum(item => Math.Abs(VerifiedAnalyticsData.Number(item, "gross_amount")));
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["week_start"] = group.Key.ToString("yyyy-MM-dd"), ["week_end"] = group.Key.AddDays(6).ToString("yyyy-MM-dd"),
                    ["sale_count"] = sales.Length.ToString(CultureInfo.InvariantCulture), ["refund_count"] = refunds.Length.ToString(CultureInfo.InvariantCulture),
                    ["gross_revenue"] = VerifiedAnalyticsData.DecimalText(gross), ["refund_total"] = VerifiedAnalyticsData.DecimalText(refund),
                    ["refund_rate"] = VerifiedAnalyticsData.Percent(gross == 0 ? 0 : refund / gross * 100m)
                };
            }).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"{rows.Length} hafta için iade oranı hesaplandı.", WorkflowData.RowsOutput(rows));
    }
    private static bool IsRefund(JsonElement item) =>
        string.Equals(VerifiedAnalyticsData.Text(item, "record_type"), "refund", StringComparison.OrdinalIgnoreCase);
    private static DateOnly StartOfWeek(DateTime value)
    {
        var day = DateOnly.FromDateTime(value);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }
}

public sealed class PeakRevenueDayWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "en_yuksek_hasilatli_gun", "En yüksek hasılatlı gün ve satışları",
        "Finds the highest-revenue day and lists that day's sale records without another model call.",
        RequiredTools: ["sale.aggregate_records", "sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("net_revenue"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var aggregate = await VerifiedAnalyticsData.AggregateAsync(context, "day", "net_revenue", "descending", 30, cancellationToken, includeRefunds: false);
        if (aggregate.Failure is not null) return aggregate.Failure;
        var winner = aggregate.Items.FirstOrDefault();
        var day = winner.ValueKind == JsonValueKind.Object ? VerifiedAnalyticsData.Text(winner, "group_key") : string.Empty;
        if (day.Length == 0) return new(AiWorkflowStepStatus.Completed, "Seçilen dönemde satış bulunamadı.", WorkflowData.RowsOutput([]));
        var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            { ["start_date"] = day, ["end_date"] = day };
        parameters.Remove("period_days"); parameters.Remove("period_months"); parameters.Remove("all_history");
        var records = await VerifiedAnalyticsData.RecordsAsync(context with { Parameters = parameters }, 1, false, cancellationToken);
        if (records.Failure is not null) return records.Failure;
        var rows = records.Items.Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["peak_day"] = day, ["timestamp"] = VerifiedAnalyticsData.Text(item, "timestamp"),
            ["reference_code"] = VerifiedAnalyticsData.Text(item, "reference_code"), ["sale_type"] = VerifiedAnalyticsData.Text(item, "sale_type"),
            ["patient_name"] = VerifiedAnalyticsData.Text(item, "patient_name"), ["quantity"] = VerifiedAnalyticsData.Text(item, "quantity"),
            ["net_revenue"] = VerifiedAnalyticsData.Text(item, "net_amount")
        }).ToArray();
        return new(AiWorkflowStepStatus.Completed,
            $"En yüksek hasılatlı gün {Format(day)}: {VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(winner, "net_revenue"))} ₺; {rows.Length} satış gösteriliyor.",
            WorkflowData.RowsOutput(rows));
    }
    private static string Format(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy");
}

public sealed class PatientRevenueConcentrationWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_hasilat_yogunlugu", "Hasta hasılat yoğunluğu",
        "Finds the smallest leading patient set that reaches a requested cumulative share of net revenue.",
        RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("net_revenue"), StringComparer.Ordinal)
            { ["target_percent"] = "Cumulative revenue target percentage, default 50." });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "patient", "net_revenue", "descending", 365, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var total = fetch.Items.Sum(item => VerifiedAnalyticsData.Number(item, "net_revenue"));
        var target = Math.Clamp(WorkflowData.NullableDecimal(context, "target_percent") ?? 50m, 1m, 100m);
        decimal cumulative = 0;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var item in fetch.Items)
        {
            if (total <= 0 || cumulative / total * 100m >= target) break;
            var revenue = VerifiedAnalyticsData.Number(item, "net_revenue"); cumulative += revenue;
            rows.Add(new Dictionary<string, string>
            {
                ["patient_name"] = VerifiedAnalyticsData.Text(item, "group_label"), ["sale_count"] = VerifiedAnalyticsData.Text(item, "sale_count"),
                ["net_revenue"] = VerifiedAnalyticsData.DecimalText(revenue),
                ["cumulative_share"] = VerifiedAnalyticsData.Percent(total == 0 ? 0 : cumulative / total * 100m)
            });
        }
        return new(AiWorkflowStepStatus.Completed, $"Net hasılatın %{target:0.##} bölümüne ulaşmak için {rows.Count} hasta gerekiyor.",
            WorkflowData.RowsOutput(rows));
    }
}

public sealed class ProductNoSaleMonthsWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "urun_satis_olmayan_aylar", "Ürünün satış olmayan ayları",
        "Lists calendar months with no persisted sale for the selected product in the requested period.",
        RequiredTools: ["sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (WorkflowData.Barcodes(context).Count == 0) return WorkflowData.Failed("Satış olmayan ayları bulmak için ilaç seçimi gerekiyor.");
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "month", "group_key", "ascending", 365, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var sold = fetch.Items.Select(item => VerifiedAnalyticsData.Text(item, "group_key")).ToHashSet(StringComparer.Ordinal);
        var (startText, endText) = WorkflowData.DateRange(context, 365);
        var start = DateOnly.ParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(endText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (var month = new DateOnly(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM");
            if (!sold.Contains(key)) rows.Add(new Dictionary<string, string> { ["period"] = key, ["sale_count"] = "0" });
        }
        return new(AiWorkflowStepStatus.Completed, $"Seçilen dönemde satış olmayan {rows.Count} ay bulundu.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class ProductOperationalComparisonWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "urun_operasyonel_karsilastirma", "Ürün fiyat, stok ve satış karşılaştırması",
        "Combines catalog prices and shelf stock with period sales for two to ten resolved barcodes.",
        RequiredTools: ["product.compare", "sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("net_revenue"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count < 2) return WorkflowData.Failed("Karşılaştırma için en az iki ilaç seçimi gerekiyor.");
        var products = await WorkflowData.Execute(context, "product.compare", new { barcodes }, cancellationToken);
        if (products is null) return WorkflowData.Missing("product.compare");
        if (!products.IsSuccess) return WorkflowData.Failed("Ürün fiyat ve stok bilgileri alınamadı.");
        var sales = await VerifiedAnalyticsData.AggregateAsync(context, "product", "net_revenue", "descending", 30, cancellationToken, includeRefunds: false);
        if (sales.Failure is not null) return sales.Failure;
        var byBarcode = sales.Items.ToDictionary(item => VerifiedAnalyticsData.Text(item, "group_key"), item => item, StringComparer.Ordinal);
        var source = products.Output.TryGetProperty("products", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray() : [];
        var rows = source.Select(product =>
        {
            var barcode = VerifiedAnalyticsData.Text(product, "barcode"); byBarcode.TryGetValue(barcode, out var sale);
            return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["barcode"] = barcode, ["medicine_name"] = VerifiedAnalyticsData.Text(product, "name"),
                ["sale_price"] = VerifiedAnalyticsData.Text(product, "sale_price"), ["purchase_price"] = VerifiedAnalyticsData.Text(product, "warehouse_price"),
                ["shelf_stock_quantity"] = VerifiedAnalyticsData.Text(product, "shelf_stock_quantity"),
                ["quantity"] = sale.ValueKind == JsonValueKind.Object ? VerifiedAnalyticsData.Text(sale, "quantity") : "0",
                ["net_revenue"] = sale.ValueKind == JsonValueKind.Object ? VerifiedAnalyticsData.Text(sale, "net_revenue") : "0",
                ["profit"] = sale.ValueKind == JsonValueKind.Object ? VerifiedAnalyticsData.Text(sale, "profit") : string.Empty
            };
        }).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"{rows.Length} ürün fiyat, stok ve dönem satışlarıyla karşılaştırıldı.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class ZeroInstitutionSaleWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "sifir_kamu_ucretli_satislar", "Kamu ücreti sıfır satışlar",
        "Lists persisted prescription sales whose institution/public contribution is exactly zero.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 30, false, cancellationToken);
        if (fetch.Failure is not null) return fetch.Failure;
        var rows = fetch.Items.Where(item => VerifiedAnalyticsData.Number(item, "institution_amount") == 0m)
            .Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["timestamp"] = VerifiedAnalyticsData.Text(item, "timestamp"), ["reference_code"] = VerifiedAnalyticsData.Text(item, "reference_code"),
                ["patient_name"] = VerifiedAnalyticsData.Text(item, "patient_name"), ["quantity"] = VerifiedAnalyticsData.Text(item, "quantity"),
                ["institution_amount"] = "0", ["net_revenue"] = VerifiedAnalyticsData.Text(item, "net_amount")
            }).ToArray();
        return new(AiWorkflowStepStatus.Completed,
            rows.Length == 0 ? "Seçilen dönemde kamu ücreti sıfır reçeteli satış bulunamadı." : $"Kamu ücreti sıfır {rows.Length} reçeteli satış bulundu.",
            WorkflowData.RowsOutput(rows));
    }
}

public sealed class PatientRefundHistoryWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_iadelerini_gor", "Hasta iade geçmişi",
        "Lists only refund records for a resolved patient, including date, reference, refunded products, and amount.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (WorkflowData.PatientRecordId(context) is null) return WorkflowData.Failed("İade geçmişi için hasta seçimi gerekiyor.");
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 3650, true, cancellationToken, includeRefunds: true);
        if (fetch.Failure is not null) return fetch.Failure;
        var rows = fetch.Items.Where(item => string.Equals(VerifiedAnalyticsData.Text(item, "record_type"), "refund", StringComparison.OrdinalIgnoreCase))
            .Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["timestamp"] = VerifiedAnalyticsData.Text(item, "timestamp"), ["reference_code"] = VerifiedAnalyticsData.Text(item, "reference_code"),
                ["patient_name"] = VerifiedAnalyticsData.Text(item, "patient_name"), ["requested_products"] = ProductNames(item),
                ["quantity"] = VerifiedAnalyticsData.Text(item, "quantity"), ["net_revenue"] = VerifiedAnalyticsData.Text(item, "net_amount")
            }).ToArray();
        var patient = WorkflowData.PatientName(context);
        return new(AiWorkflowStepStatus.Completed,
            rows.Length == 0 ? $"{patient} adlı hastanın seçilen dönemde iadesi bulunamadı." : $"{patient} adlı hastanın {rows.Length} iadesi bulundu.",
            WorkflowData.RowsOutput(rows));
    }

    private static string ProductNames(JsonElement item) =>
        item.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array
            ? string.Join(", ", lines.EnumerateArray().Select(line => VerifiedAnalyticsData.Text(line, "product_name"))
                .Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase))
            : string.Empty;
}
