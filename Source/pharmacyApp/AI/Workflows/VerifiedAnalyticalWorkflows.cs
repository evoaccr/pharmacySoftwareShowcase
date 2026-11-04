using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

internal sealed record WorkflowFetchResult(IReadOnlyList<JsonElement> Items, AiWorkflowStepResult? Failure = null);

internal static class VerifiedAnalyticsData
{
    private const int AggregatePageSize = 200;
    private const int RecordPageSize = 40;
    private const int MaximumRows = 10_000;

    public static async Task<WorkflowFetchResult> AggregateAsync(
        AiWorkflowContext context, string groupBy, string sortBy, string sortDirection, int defaultDays,
        CancellationToken cancellationToken, IReadOnlyList<string>? saleTypes = null, bool? includeRefunds = null,
        string? startOverride = null, string? endOverride = null)
    {
        var (calculatedStart, calculatedEnd) = WorkflowData.DateRange(context, defaultDays);
        var start = startOverride ?? calculatedStart;
        var end = endOverride ?? calculatedEnd;
        var items = new List<JsonElement>();
        for (var offset = 0; offset < MaximumRows; offset += AggregatePageSize)
        {
            var execution = await WorkflowData.Execute(context, "sale.aggregate_records", new
            {
                start_date = start, end_date = end, sale_types = saleTypes ?? WorkflowData.SaleTypes(context),
                include_refunds = includeRefunds ?? WorkflowData.NullableBoolean(context, "include_refunds") ?? false,
                barcodes = WorkflowData.Barcodes(context), qr_codes = Array.Empty<string>(),
                payment_types = WorkflowData.List(context, "payment_types"), statuses = WorkflowData.List(context, "statuses"),
                patient_record_id = WorkflowData.PatientRecordId(context), user_id = WorkflowData.Parameter(context, "user_id"),
                prescription_no = WorkflowData.Parameter(context, "prescription_no"),
                is_debted = WorkflowData.NullableBoolean(context, "is_debted"), marked_only = WorkflowData.Boolean(context, "marked_only"),
                minimum_total = WorkflowData.NullableDecimal(context, "minimum_total"), maximum_total = WorkflowData.NullableDecimal(context, "maximum_total"),
                group_by = groupBy, sort_by = sortBy, sort_direction = sortDirection, offset, limit = AggregatePageSize
            }, cancellationToken);
            if (execution is null) return new([], WorkflowData.Missing("sale.aggregate_records"));
            if (!execution.IsSuccess) return new([], WorkflowData.Failed("Satış analizi alınamadı."));
            if (execution.Output.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                items.AddRange(groups.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone()));
            if (!True(execution.Output, "has_more")) return new(items);
        }
        return new([], WorkflowData.Failed("Analiz 10.000 gruptan fazla sonuç üretti. Daha dar bir tarih veya filtre belirtin."));
    }

    public static async Task<WorkflowFetchResult> RecordsAsync(
        AiWorkflowContext context, int defaultDays, bool includeLines, CancellationToken cancellationToken,
        bool includeRefunds = false, string sortDirection = "newest_first")
    {
        var (start, end) = WorkflowData.DateRange(context, defaultDays);
        var items = new List<JsonElement>();
        for (var offset = 0; offset < MaximumRows; offset += RecordPageSize)
        {
            var execution = await WorkflowData.Execute(context, "sale.search_records", new
            {
                start_date = start, end_date = end, sale_types = WorkflowData.SaleTypes(context), include_refunds = includeRefunds,
                barcodes = WorkflowData.Barcodes(context), qr_codes = Array.Empty<string>(),
                payment_types = WorkflowData.List(context, "payment_types"), statuses = WorkflowData.List(context, "statuses"),
                patient_record_id = WorkflowData.PatientRecordId(context), user_id = WorkflowData.Parameter(context, "user_id"),
                prescription_no = WorkflowData.Parameter(context, "prescription_no"),
                is_debted = WorkflowData.NullableBoolean(context, "is_debted"), marked_only = WorkflowData.Boolean(context, "marked_only"),
                minimum_total = WorkflowData.NullableDecimal(context, "minimum_total"), maximum_total = WorkflowData.NullableDecimal(context, "maximum_total"),
                offset, limit = RecordPageSize, include_lines = includeLines, sort_direction = sortDirection
            }, cancellationToken);
            if (execution is null) return new([], WorkflowData.Missing("sale.search_records"));
            if (!execution.IsSuccess) return new([], WorkflowData.Failed("Satış kayıtları alınamadı."));
            if (execution.Output.TryGetProperty("sales", out var sales) && sales.ValueKind == JsonValueKind.Array)
                items.AddRange(sales.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone()));
            if (!True(execution.Output, "has_more")) return new(items);
        }
        return new([], WorkflowData.Failed("Analiz 10.000 satıştan fazla sonuç üretti. Daha dar bir tarih veya filtre belirtin."));
    }

    public static decimal Number(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : 0m;
    public static int Integer(JsonElement item, string name) => (int)Number(item, name);
    public static string Text(JsonElement item, string name) => WorkflowData.Text(item, name);
    public static bool True(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    public static DateTime? Timestamp(JsonElement item)
    {
        if (item.TryGetProperty("timestamp", out var raw) && raw.ValueKind == JsonValueKind.String && raw.TryGetDateTime(out var parsed))
            return parsed;
        var text = Text(item, "timestamp");
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value)) return value;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var offset)
            ? offset.LocalDateTime : null;
    }
    public static string DecimalText(decimal value) => decimal.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
    public static string Percent(decimal value) => decimal.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed class PatientPurchaseIntervalWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("hasta_alisveris_araligi", "Hasta alışveriş aralığı",
        "Calculates a resolved patient's purchase count, first/last purchase and average days between distinct shopping dates.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (WorkflowData.PatientRecordId(context) is null) return WorkflowData.Failed("Alışveriş aralığı için hasta seçimi gerekiyor.");
        var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (!parameters.Keys.Any(key => key is "period_days" or "period_months" or "start_date" or "all_history")) parameters["all_history"] = "true";
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context with { Parameters = parameters }, 3650, false, cancellationToken);
        if (fetch.Failure is not null) return fetch.Failure;
        var purchases = fetch.Items.Select(VerifiedAnalyticsData.Timestamp).Where(value => value.HasValue)
            .Select(value => value!.Value).Order().ToArray();
        var days = purchases.Select(value => value.Date).Distinct().Order().ToArray();
        var average = days.Length < 2 ? 0m : (decimal)days.Zip(days.Skip(1), (left, right) => (right - left).TotalDays).Average();
        var row = new Dictionary<string, string>
        {
            ["patient_name"] = fetch.Items.Select(item => VerifiedAnalyticsData.Text(item, "patient_name")).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
            ["purchase_count"] = purchases.Length.ToString(CultureInfo.InvariantCulture),
            ["first_purchase"] = days.Length == 0 ? string.Empty : days[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["last_purchase"] = days.Length == 0 ? string.Empty : days[^1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["average_days"] = VerifiedAnalyticsData.DecimalText(average)
        };
        return new(AiWorkflowStepStatus.Completed, $"Alışverişler arasındaki ortalama süre {row["average_days"]} gün.", WorkflowData.RowsOutput([row]));
    }
}

public sealed class LapsedPatientWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("geri_kazanilacak_hastalar", "Uzun süredir gelmeyen hastalar",
        "Finds patients with at least a configurable prior purchase count who made no purchase during the recent inactive period.",
        RequiredTools: ["sale.aggregate_records"], Parameters: new Dictionary<string, string>
        { ["inactive_months"] = "Recent months with no purchase, default 6.", ["history_months"] = "Prior analysis window, default 24.", ["minimum_purchases"] = "Prior purchases required, default 3." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var inactiveMonths = WorkflowData.Integer(context, "inactive_months", 6, 1, 60);
        var historyMonths = WorkflowData.Integer(context, "history_months", 24, inactiveMonths + 1, 120);
        var minimum = WorkflowData.Integer(context, "minimum_purchases", 3, 2, 100);
        var cutoff = today.AddMonths(-inactiveMonths);
        var history = await VerifiedAnalyticsData.AggregateAsync(context, "patient", "record_count", "descending", 365,
            cancellationToken, includeRefunds: false, startOverride: today.AddMonths(-historyMonths).ToString("yyyy-MM-dd"), endOverride: cutoff.AddDays(-1).ToString("yyyy-MM-dd"));
        if (history.Failure is not null) return history.Failure;
        var recent = await VerifiedAnalyticsData.AggregateAsync(context, "patient", "record_count", "descending", 365,
            cancellationToken, includeRefunds: false, startOverride: cutoff.ToString("yyyy-MM-dd"), endOverride: today.ToString("yyyy-MM-dd"));
        if (recent.Failure is not null) return recent.Failure;
        var active = recent.Items.Select(item => VerifiedAnalyticsData.Text(item, "group_key")).ToHashSet(StringComparer.Ordinal);
        var rows = history.Items.Where(item => VerifiedAnalyticsData.Integer(item, "sale_count") >= minimum && !active.Contains(VerifiedAnalyticsData.Text(item, "group_key")))
            .Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["patient_record_id"] = VerifiedAnalyticsData.Text(item, "group_key"), ["patient_name"] = VerifiedAnalyticsData.Text(item, "group_label"),
                ["purchase_count"] = VerifiedAnalyticsData.Text(item, "sale_count"), ["inactive_since"] = cutoff.ToString("yyyy-MM-dd")
            }).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"Son {inactiveMonths} aydır gelmeyen {rows.Length} düzenli hasta bulundu.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class SameDayRepeatSalesWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("ayni_gun_tekrar_satislari", "Aynı gün tekrarlanan satışlar",
        "Finds patients with multiple separate sales on the same calendar day and lists references and totals.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 30, false, cancellationToken);
        if (fetch.Failure is not null) return fetch.Failure;
        var rows = fetch.Items.Where(item => VerifiedAnalyticsData.Text(item, "patient_record_id").Length > 0 && VerifiedAnalyticsData.Timestamp(item).HasValue)
            .GroupBy(item => (Patient: VerifiedAnalyticsData.Text(item, "patient_record_id"), Day: VerifiedAnalyticsData.Timestamp(item)!.Value.Date))
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["day"] = group.Key.Day.ToString("yyyy-MM-dd"), ["patient_record_id"] = group.Key.Patient,
                ["patient_name"] = group.Select(item => VerifiedAnalyticsData.Text(item, "patient_name")).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                ["sale_count"] = group.Count().ToString(CultureInfo.InvariantCulture),
                ["reference_code"] = string.Join(", ", group.Select(item => VerifiedAnalyticsData.Text(item, "reference_code"))),
                ["net_revenue"] = VerifiedAnalyticsData.DecimalText(group.Sum(item => VerifiedAnalyticsData.Number(item, "net_amount")))
            }).OrderByDescending(row => row["day"]).ToArray();
        var (start, end) = WorkflowData.DateRange(context, 30);
        return new(AiWorkflowStepStatus.Completed, $"{start}–{end} döneminde aynı gün birden fazla satış yapılan {rows.Length} hasta-gün bulundu.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class PrescriptionShareWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("receteli_satis_payi_analizi", "Ürün reçeteli satış payı",
        "Calculates each product's prescription quantity share from all persisted sales and filters by a minimum percentage.",
        RequiredTools: ["sale.aggregate_records", "product.get"], Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("quantity"), StringComparer.Ordinal)
        {
            ["minimum_share"] = "Optional minimum prescription quantity percentage.",
            ["sort_direction"] = "ascending for the lowest shares or descending for the highest.",
            ["only_on_market"] = "true to keep active/on-market products only.",
            ["limit"] = "Maximum rows from 1 to 200."
        });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var all = await VerifiedAnalyticsData.AggregateAsync(context, "product", "quantity", "descending", 365, cancellationToken, includeRefunds: false);
        if (all.Failure is not null) return all.Failure;
        var prescription = await VerifiedAnalyticsData.AggregateAsync(context, "product", "quantity", "descending", 365, cancellationToken, saleTypes: ["prescription"], includeRefunds: false);
        if (prescription.Failure is not null) return prescription.Failure;
        var prescribed = prescription.Items.ToDictionary(item => VerifiedAnalyticsData.Text(item, "group_key"), item => VerifiedAnalyticsData.Number(item, "quantity"), StringComparer.Ordinal);
        var minimum = WorkflowData.NullableDecimal(context, "minimum_share");
        var ascending = string.Equals(WorkflowData.Parameter(context, "sort_direction"), "ascending", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(WorkflowData.Parameter(context, "sort_direction"), "asc", StringComparison.OrdinalIgnoreCase);
        var candidates = all.Items.Select(item =>
            {
                var total = VerifiedAnalyticsData.Number(item, "quantity"); var count = prescribed.GetValueOrDefault(VerifiedAnalyticsData.Text(item, "group_key"));
                return (Item: item, Total: total, Prescription: count, Share: total == 0 ? 0 : count / total * 100m);
            })
            .Where(value => minimum is null || value.Share >= minimum.Value);
        candidates = ascending
            ? candidates.OrderBy(value => value.Share).ThenByDescending(value => value.Total)
            : candidates.OrderByDescending(value => value.Share).ThenByDescending(value => value.Total);
        var requested = WorkflowData.Integer(context, "limit", 100, 1, 200);
        var onlyActive = WorkflowData.Boolean(context, "only_on_market");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var value in candidates)
        {
            if (onlyActive)
            {
                var barcode = VerifiedAnalyticsData.Text(value.Item, "group_key");
                var product = await WorkflowData.Execute(context, "product.get", new { barcode }, cancellationToken);
                if (product is null) return WorkflowData.Missing("product.get");
                if (!product.IsSuccess || !product.Output.TryGetProperty("product", out var productData) ||
                    productData.ValueKind != JsonValueKind.Object) continue;
                var summary = productData.TryGetProperty("summary", out var foundSummary) ? foundSummary : productData;
                if (!bool.TryParse(WorkflowData.Text(summary, "is_on_market"), out var onMarket) || !onMarket) continue;
            }
            rows.Add(new Dictionary<string, string>
            {
                ["barcode"] = VerifiedAnalyticsData.Text(value.Item, "group_key"), ["name"] = VerifiedAnalyticsData.Text(value.Item, "group_label"),
                ["total_quantity"] = VerifiedAnalyticsData.DecimalText(value.Total), ["prescription_quantity"] = VerifiedAnalyticsData.DecimalText(value.Prescription),
                ["prescription_share"] = VerifiedAnalyticsData.Percent(value.Share)
            });
            if (rows.Count >= requested) break;
        }
        var message = ascending
            ? $"Reçeteli satış payı en düşük {rows.Count} ürün sıralandı."
            : minimum is not null
                ? $"Reçeteli satış payı en az %{minimum:0.##} olan {rows.Count} ürün bulundu."
                : $"Reçeteli satış payı en yüksek {rows.Count} ürün sıralandı.";
        return new(AiWorkflowStepStatus.Completed, message, WorkflowData.RowsOutput(rows));
    }
}

public sealed class RevenueConcentrationWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("hasilat_yogunlugu_analizi", "Hasılat yoğunluğu analizi",
        "Ranks products by net revenue and calculates each product's and the requested leading set's share of total revenue.",
        RequiredTools: ["sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("net_revenue"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "product", "net_revenue", "descending", 30, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var total = fetch.Items.Sum(item => VerifiedAnalyticsData.Number(item, "net_revenue"));
        var requested = WorkflowData.Integer(context, "limit", 20, 1, 200);
        decimal cumulative = 0;
        var rows = fetch.Items.Take(requested).Select(item =>
        {
            var revenue = VerifiedAnalyticsData.Number(item, "net_revenue");
            var share = total == 0 ? 0 : revenue / total * 100m;
            cumulative += share;
            return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["barcode"] = VerifiedAnalyticsData.Text(item, "group_key"), ["name"] = VerifiedAnalyticsData.Text(item, "group_label"),
                ["net_revenue"] = VerifiedAnalyticsData.DecimalText(revenue), ["revenue_share"] = VerifiedAnalyticsData.Percent(share),
                ["cumulative_share"] = VerifiedAnalyticsData.Percent(cumulative)
            };
        }).ToArray();
        return new(AiWorkflowStepStatus.Completed,
            $"İlk {rows.Length} ürün toplam net hasılatın %{VerifiedAnalyticsData.Percent(cumulative)} bölümünü oluşturuyor.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class ProductSalesRankingsComparisonWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("urun_satis_siralamalarini_karsilastir", "Ürün satış sıralamalarını karşılaştır",
        "ONLY for explicitly comparing TWO different rankings: quantity rank versus net-revenue rank. Produces two lists, not one top-products list. A single top-N quantity/revenue ranking uses satis_hasilati_ozeti with group_by=product, metric and sort_by set to the requested measure, and limit=N.",
        RequiredTools: ["sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("quantity"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "product", "quantity", "descending", 30,
            cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;

        var limit = WorkflowData.Integer(context, "limit", 15, 1, 100);
        var rows = new List<IReadOnlyDictionary<string, string>>(limit * 2);
        AddRanking("Satış Adedi", fetch.Items.OrderByDescending(item => VerifiedAnalyticsData.Number(item, "quantity")));
        AddRanking("Net Hasılat", fetch.Items.OrderByDescending(item => VerifiedAnalyticsData.Number(item, "net_revenue")));

        return new(AiWorkflowStepStatus.Completed,
            $"Aynı dönemde satış adedi ve net hasılata göre ilk {Math.Min(limit, fetch.Items.Count)} ürün ayrı ayrı sıralandı.",
            WorkflowData.RowsOutput(rows, new
            {
                total_count = rows.Count,
                visible_columns = new[] { "ranking_type", "rank", "barcode", "name", "quantity", "net_revenue" }
            }));

        void AddRanking(string rankingType, IEnumerable<JsonElement> ranked)
        {
            var rank = 0;
            foreach (var item in ranked.Take(limit))
            {
                rank++;
                rows.Add(new Dictionary<string, string>
                {
                    ["ranking_type"] = rankingType,
                    ["rank"] = rank.ToString(CultureInfo.InvariantCulture),
                    ["barcode"] = VerifiedAnalyticsData.Text(item, "group_key"),
                    ["name"] = VerifiedAnalyticsData.Text(item, "group_label"),
                    ["quantity"] = VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(item, "quantity")),
                    ["net_revenue"] = VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(item, "net_revenue"))
                });
            }
        }
    }
}

public sealed class WeekdaySalesComparisonWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("hafta_gunlerini_karsilastir", "Hafta günlerini karşılaştır",
        "Compares sales count, quantity and net revenue for two weekdays in the current week.",
        RequiredTools: ["sale.aggregate_records"], Parameters: new Dictionary<string, string>
        {
            ["first_day"] = "monday through sunday.", ["second_day"] = "monday through sunday.",
            ["sale_types"] = "Optional retail and/or prescription.", ["include_refunds"] = "true or false."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var (parameter, fallback, label) in new[]
                 {
                     ("first_day", "monday", "Pazartesi"), ("second_day", "tuesday", "Salı")
                 })
        {
            var day = DayIndex(WorkflowData.Parameter(context, parameter) ?? fallback);
            var date = monday.AddDays(day);
            var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "none", "record_count", "descending", 1,
                cancellationToken, includeRefunds: false, startOverride: iso, endOverride: iso);
            if (fetch.Failure is not null) return fetch.Failure;
            var item = fetch.Items.FirstOrDefault();
            rows.Add(new Dictionary<string, string>
            {
                ["weekday"] = label,
                ["date"] = iso,
                ["sale_count"] = item.ValueKind == JsonValueKind.Object ? VerifiedAnalyticsData.Text(item, "sale_count") : "0",
                ["quantity"] = item.ValueKind == JsonValueKind.Object ? VerifiedAnalyticsData.Text(item, "quantity") : "0",
                ["net_revenue"] = item.ValueKind == JsonValueKind.Object ? VerifiedAnalyticsData.Text(item, "net_revenue") : "0"
            });
        }
        return new(AiWorkflowStepStatus.Completed, "Bu haftanın iki günü aynı satış ölçütleriyle karşılaştırıldı.",
            WorkflowData.RowsOutput(rows, new { visible_columns = new[] { "weekday", "date", "sale_count", "quantity", "net_revenue" } }));
    }

    private static int DayIndex(string value) => value.Trim().ToLowerInvariant() switch
    {
        "monday" or "pazartesi" => 0, "tuesday" or "sali" or "salı" => 1,
        "wednesday" or "carsamba" or "çarşamba" => 2, "thursday" or "persembe" or "perşembe" => 3,
        "friday" or "cuma" => 4, "saturday" or "cumartesi" => 5, "sunday" or "pazar" => 6,
        _ => 0
    };
}

public sealed class InstitutionFeeSalesRankingWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("kamu_ucretli_satislari_sirala", "Kamu ücretli satışları sırala",
        "Ranks individual sales with a positive institution/public fee by that fee.", RequiredTools: ["sale.search_records"],
        Parameters: PatientProductSummaryWorkflow.SalesParameters("institution_amount"));

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 30, false, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var limit = WorkflowData.Integer(context, "limit", 15, 1, 200);
        var rows = fetch.Items.Where(item => VerifiedAnalyticsData.Number(item, "institution_amount") > 0)
            .OrderByDescending(item => VerifiedAnalyticsData.Number(item, "institution_amount")).Take(limit)
            .Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["timestamp"] = VerifiedAnalyticsData.Text(item, "timestamp"),
                ["reference_code"] = VerifiedAnalyticsData.Text(item, "reference_code"),
                ["patient_name"] = VerifiedAnalyticsData.Text(item, "patient_name"),
                ["institution_amount"] = VerifiedAnalyticsData.Text(item, "institution_amount"),
                ["net_amount"] = VerifiedAnalyticsData.Text(item, "net_amount")
            }).ToArray();
        return new(AiWorkflowStepStatus.Completed,
            rows.Length == 0 ? "Kamu ücreti bulunan satış kaydı bulunamadı." : $"Kamu ücreti en yüksek {rows.Length} satış sıralandı.",
            WorkflowData.RowsOutput(rows, new { visible_columns = new[] { "timestamp", "reference_code", "patient_name", "institution_amount", "net_amount" } }));
    }
}

public sealed class DailyAverageRevenueWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("gunluk_ortalama_hasilat", "Günlük ortalama hasılat",
        "Calculates average daily net revenue over the complete requested calendar range, including zero-sale days.",
        RequiredTools: ["sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var (startText, endText) = WorkflowData.DateRange(context, 30);
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "day", "group_key", "ascending", 30, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var start = DateOnly.ParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(endText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var calendarDays = end.DayNumber - start.DayNumber + 1;
        var revenue = fetch.Items.Sum(item => VerifiedAnalyticsData.Number(item, "net_revenue"));
        var activeDays = fetch.Items.Count(item => VerifiedAnalyticsData.Number(item, "sale_count") > 0);
        var average = calendarDays == 0 ? 0 : revenue / calendarDays;
        var row = new Dictionary<string, string>
        {
            ["start_date"] = startText, ["end_date"] = endText, ["net_revenue"] = VerifiedAnalyticsData.DecimalText(revenue),
            ["calendar_days"] = calendarDays.ToString(CultureInfo.InvariantCulture), ["active_sale_days"] = activeDays.ToString(CultureInfo.InvariantCulture),
            ["zero_sale_days"] = (calendarDays - activeDays).ToString(CultureInfo.InvariantCulture),
            ["average_daily_revenue"] = VerifiedAnalyticsData.DecimalText(average)
        };
        return new(AiWorkflowStepStatus.Completed, $"Takvim günü başına ortalama net hasılat {row["average_daily_revenue"]}.", WorkflowData.RowsOutput([row]));
    }
}

public sealed class WeekdaySalesAnalysisWorkflow : IAiWorkflow
{
    private static readonly string[] TurkishDays = ["Pazar", "Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi"];
    public AiWorkflowDefinition Definition { get; } = new("hafta_gunu_satis_analizi", "Haftanın günlerine göre satış",
        "Groups daily sales into weekdays and returns sale count, quantity, net revenue and average basket.",
        RequiredTools: ["sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "day", "group_key", "ascending", 90, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var weekendOnly = WorkflowData.Boolean(context, "weekend_only");
        var rows = fetch.Items.Where(item => DateOnly.TryParseExact(VerifiedAnalyticsData.Text(item, "group_key"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .GroupBy(item => DateOnly.ParseExact(VerifiedAnalyticsData.Text(item, "group_key"), "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek)
            .Where(group => !weekendOnly || group.Key is DayOfWeek.Saturday or DayOfWeek.Sunday)
            .OrderBy(group => group.Key == DayOfWeek.Sunday ? 7 : (int)group.Key)
            .Select(group =>
            {
                var sales = group.Sum(item => VerifiedAnalyticsData.Number(item, "sale_count"));
                var revenue = group.Sum(item => VerifiedAnalyticsData.Number(item, "net_revenue"));
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["weekday"] = TurkishDays[(int)group.Key], ["sale_count"] = VerifiedAnalyticsData.DecimalText(sales),
                    ["quantity"] = VerifiedAnalyticsData.DecimalText(group.Sum(item => VerifiedAnalyticsData.Number(item, "quantity"))),
                    ["net_revenue"] = VerifiedAnalyticsData.DecimalText(revenue), ["average_basket"] = VerifiedAnalyticsData.DecimalText(sales == 0 ? 0 : revenue / sales)
                };
            }).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"Satışlar haftanın {rows.Length} gününde özetlendi.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class HourlySalesAnalysisWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("saatlik_satis_analizi", "Saat aralıklarına göre satış",
        "Groups individual sales into hourly intervals and returns sale count, quantity, revenue and average basket.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 30, false, cancellationToken);
        if (fetch.Failure is not null) return fetch.Failure;
        var groups = fetch.Items.Where(item => VerifiedAnalyticsData.Timestamp(item).HasValue)
            .GroupBy(item => VerifiedAnalyticsData.Timestamp(item)!.Value.Hour)
            .Select(group =>
            {
                var revenue = group.Sum(item => VerifiedAnalyticsData.Number(item, "net_amount"));
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["hour_range"] = $"{group.Key:00}:00–{(group.Key + 1) % 24:00}:00", ["sale_count"] = group.Count().ToString(CultureInfo.InvariantCulture),
                    ["quantity"] = group.Sum(item => VerifiedAnalyticsData.Integer(item, "quantity")).ToString(CultureInfo.InvariantCulture),
                    ["net_revenue"] = VerifiedAnalyticsData.DecimalText(revenue), ["average_basket"] = VerifiedAnalyticsData.DecimalText(revenue / group.Count())
                };
            });
        var sort = WorkflowData.SortBy(context, "group_key");
        var sortByRevenue = sort == "net_revenue";
        var sortByCount = sort == "record_count";
        var descending = WorkflowData.SortDirection(context, "descending") == "descending";
        groups = sortByRevenue
            ? descending
                ? groups.OrderByDescending(row => decimal.Parse(row["net_revenue"], CultureInfo.InvariantCulture))
                : groups.OrderBy(row => decimal.Parse(row["net_revenue"], CultureInfo.InvariantCulture))
            : sortByCount
                ? descending
                    ? groups.OrderByDescending(row => int.Parse(row["sale_count"], CultureInfo.InvariantCulture))
                    : groups.OrderBy(row => int.Parse(row["sale_count"], CultureInfo.InvariantCulture))
            : groups.OrderBy(row => row["hour_range"], StringComparer.Ordinal);
        var rows = groups.Take(WorkflowData.Integer(context, "limit", 24, 1, 24)).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"{fetch.Items.Count} satış {rows.Length} saat aralığında özetlendi.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class InstitutionPatientPaymentWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("kamu_hasta_odeme_analizi", "Kamu ve hasta ödeme analizi",
        "Groups prescription revenue by month and separates institution contribution from the patient's net contribution.",
        RequiredTools: ["sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "month", "group_key", "ascending", 365, cancellationToken,
            saleTypes: ["prescription"], includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var rows = fetch.Items.Select(item =>
        {
            var net = VerifiedAnalyticsData.Number(item, "net_revenue");
            var institution = VerifiedAnalyticsData.Number(item, "institution_revenue");
            var patient = Math.Max(0, net - institution);
            return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["period"] = VerifiedAnalyticsData.Text(item, "group_label"), ["net_revenue"] = VerifiedAnalyticsData.DecimalText(net),
                ["institution_revenue"] = VerifiedAnalyticsData.DecimalText(institution), ["patient_payment"] = VerifiedAnalyticsData.DecimalText(patient),
                ["institution_payment_share"] = VerifiedAnalyticsData.Percent(net == 0 ? 0 : institution / net * 100m),
                ["patient_payment_share"] = VerifiedAnalyticsData.Percent(net == 0 ? 0 : patient / net * 100m)
            };
        }).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"Kamu ve hasta katkısı {rows.Length} ay için ayrıştırıldı.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class MovementPeriodComparisonWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("hareket_donemlerini_karsilastir", "Hareket dönemlerini karşılaştır",
        "Compares the total QR movement count of this week with the preceding complete week.", RequiredTools: ["qr.search_movements"]);
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var mondayOffset = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-mondayOffset);
        var periods = new[]
        {
            (Label: "Bu hafta", Start: thisMonday, End: today),
            (Label: "Geçen hafta", Start: thisMonday.AddDays(-7), End: thisMonday.AddDays(-1))
        };
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var period in periods)
        {
            var barcodes = WorkflowData.Barcodes(context);
            var execution = await WorkflowData.Execute(context, "qr.search_movements", new Dictionary<string, object?>
            {
                ["start_date"] = period.Start.ToString("yyyy-MM-dd"), ["end_date"] = period.End.ToString("yyyy-MM-dd"),
                ["movement_type_codes"] = null, ["direction"] = "all", ["barcodes"] = barcodes.Count == 0 ? null : barcodes,
                ["user_id"] = null, ["address_type"] = "all", ["address_code"] = null, ["include_summary"] = true, ["limit"] = 1
            }, cancellationToken);
            if (execution is null) return WorkflowData.Missing("qr.search_movements");
            if (!execution.IsSuccess) return WorkflowData.Failed("Hareket dönemleri karşılaştırılamadı.");
            var count = execution.Output.TryGetProperty("summary", out var summary) && summary.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var parsed) ? parsed : 0;
            rows.Add(new Dictionary<string, string>
            {
                ["period"] = period.Label, ["start_date"] = period.Start.ToString("yyyy-MM-dd"), ["end_date"] = period.End.ToString("yyyy-MM-dd"),
                ["movement_count"] = count.ToString(CultureInfo.InvariantCulture)
            });
        }
        return new(AiWorkflowStepStatus.Completed, "Bu hafta ile geçen haftanın karekod hareket sayıları karşılaştırıldı.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class HighDiscountSaleWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("yuksek_iskontolu_satislar", "Yüksek iskontolu satışlar",
        "Inspects individual sale transactions above an explicit discount-percentage threshold. For ordinary discounted or latest discounted sales, use satislari_gor with payment_types=discount instead. Applies threshold, then ordering, then the requested output limit; does not group products.", RequiredTools: ["sale.search_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("group_key"), StringComparer.Ordinal)
        {
            ["minimum_discount_percent"] = "Exclusive transaction discount-percentage threshold, 0-100; default 20.",
            ["include_refunds"] = "false only; this inspection compares sale-transaction discounts, not refunds.",
            ["sort_by"] = "discount_percent (default), discount_total, or date.",
            ["sort_direction"] = "descending (default) or ascending; date descending means newest first.",
            ["limit"] = "Maximum matching sale transactions shown after filtering and sorting, 1-200; default 100."
        });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (WorkflowData.Parameter(context, "include_refunds") is { } refunds &&
            (!bool.TryParse(refunds, out var includeRefunds) || includeRefunds))
            return WorkflowData.Failed("This discount inspection covers sales only. Request refunds separately.");
        var minimum = WorkflowData.NullableDecimal(context, "minimum_discount_percent") ?? 20m;
        if (minimum is < 0 or > 100 || WorkflowData.Parameter(context, "minimum_discount_percent") is not null &&
            WorkflowData.NullableDecimal(context, "minimum_discount_percent") is null)
            return WorkflowData.Failed("Enter a valid discount-percentage threshold between 0 and 100.");
        var requestedLimit = WorkflowData.Parameter(context, "limit");
        var limit = 100;
        if (requestedLimit is not null && (!int.TryParse(requestedLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200))
            return WorkflowData.Failed("Choose a sale count between 1 and 200.");
        var sortBy = (WorkflowData.Parameter(context, "sort_by") ?? "discount_percent").ToLowerInvariant();
        sortBy = sortBy switch { "timestamp" => "date", "discount_amount" => "discount_total", _ => sortBy };
        if (sortBy is not ("date" or "discount_percent" or "discount_total"))
            return WorkflowData.Failed("Discounted sales can be sorted by date, discount amount, or discount percentage.");
        var direction = (WorkflowData.Parameter(context, "sort_direction") ?? "descending").ToLowerInvariant();
        direction = direction switch { "newest_first" => "descending", "oldest_first" => "ascending", _ => direction };
        if (direction is not ("ascending" or "descending"))
            return WorkflowData.Failed("Choose ascending or descending order for the discounted sales.");

        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 30, false, cancellationToken);
        if (fetch.Failure is not null) return fetch.Failure;
        var matches = fetch.Items.Select(item =>
        {
            var gross = VerifiedAnalyticsData.Number(item, "gross_amount");
            var discount = VerifiedAnalyticsData.Number(item, "discount_amount");
            var percent = gross > 0 && discount > 0 ? discount / gross * 100m : 0m;
            return (Item: item, Percent: percent, Discount: discount);
        }).Where(value => value.Percent > minimum && VerifiedAnalyticsData.Text(value.Item, "record_type") != "refund").ToArray();
        if (sortBy == "date" && matches.Any(value => VerifiedAnalyticsData.Timestamp(value.Item) is null))
            return WorkflowData.Failed("The sales could not be ordered reliably because a transaction date is missing or invalid.");
        var ordered = sortBy == "date"
            ? direction == "ascending" ? matches.OrderBy(value => VerifiedAnalyticsData.Timestamp(value.Item)) : matches.OrderByDescending(value => VerifiedAnalyticsData.Timestamp(value.Item))
            : sortBy == "discount_total"
                ? direction == "ascending" ? matches.OrderBy(value => value.Discount) : matches.OrderByDescending(value => value.Discount)
                : direction == "ascending" ? matches.OrderBy(value => value.Percent) : matches.OrderByDescending(value => value.Percent);
        var rows = ordered.ThenBy(value => VerifiedAnalyticsData.Text(value.Item, "reference_code"), StringComparer.Ordinal).Take(limit)
          .Select(value => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
          {
              ["record_id"] = VerifiedAnalyticsData.Text(value.Item, "record_id"),
              ["record_type"] = VerifiedAnalyticsData.Text(value.Item, "record_type"),
              ["reference_code"] = VerifiedAnalyticsData.Text(value.Item, "reference_code"), ["timestamp"] = VerifiedAnalyticsData.Text(value.Item, "timestamp"),
              ["sale_type"] = VerifiedAnalyticsData.Text(value.Item, "sale_type"), ["patient_name"] = VerifiedAnalyticsData.Text(value.Item, "patient_name"),
              ["patient_record_id"] = VerifiedAnalyticsData.Text(value.Item, "patient_record_id"),
              ["patient_tc"] = VerifiedAnalyticsData.Text(value.Item, "patient_tc"),
              ["prescription_no"] = VerifiedAnalyticsData.Text(value.Item, "prescription_no"),
              ["gross_revenue"] = VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(value.Item, "gross_amount")),
              ["discount_total"] = VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(value.Item, "discount_amount")),
              ["discount_percent"] = VerifiedAnalyticsData.Percent(value.Percent), ["net_revenue"] = VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(value.Item, "net_amount"))
          }).ToArray();
        var (start, end) = WorkflowData.DateRange(context, 30);
        var threshold = minimum.ToString("0.##", CultureInfo.InvariantCulture);
        var message = $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)}: {matches.Length} sale transactions have a discount above {threshold}%." +
                      (rows.Length < matches.Length ? $" Showing the first {rows.Length} matches." : string.Empty) +
                      " Each row represents one sale transaction.";
        return new(AiWorkflowStepStatus.Completed, message, WorkflowData.RowsOutput(rows, new
        {
            start_date = start, end_date = end, minimum_discount_percent = minimum,
            total_count = matches.Length, shown_count = rows.Length, has_more = rows.Length < matches.Length,
            sort_by = sortBy, sort_direction = direction,
            visible_columns = new[] { "timestamp", "sale_type", "patient_name", "gross_revenue", "discount_total", "discount_percent", "net_revenue" }
        }));
    }
}

public sealed class LossMakingSaleLineWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("zararina_satis_satirlari", "Zararına satış satırları",
        "Lists immutable sale lines whose recorded profit is negative; lines without a recorded entry cost are excluded and identified separately.",
        RequiredTools: ["sale.search_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("group_key"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.RecordsAsync(context, 30, true, cancellationToken);
        if (fetch.Failure is not null) return fetch.Failure;
        var missingCost = 0;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var sale in fetch.Items)
        {
            if (!sale.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) continue;
            foreach (var line in lines.EnumerateArray())
            {
                if (!line.TryGetProperty("entry_cost", out var cost) || cost.ValueKind == JsonValueKind.Null) { missingCost++; continue; }
                var profit = VerifiedAnalyticsData.Number(line, "profit");
                if (profit >= 0) continue;
                rows.Add(new Dictionary<string, string>
                {
                    ["reference_code"] = VerifiedAnalyticsData.Text(sale, "reference_code"), ["timestamp"] = VerifiedAnalyticsData.Text(sale, "timestamp"),
                    ["product_name"] = VerifiedAnalyticsData.Text(line, "product_name"), ["barcode"] = VerifiedAnalyticsData.Text(line, "barcode"),
                    ["net_price"] = VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(line, "net_price")),
                    ["entry_cost"] = VerifiedAnalyticsData.DecimalText(VerifiedAnalyticsData.Number(line, "entry_cost")), ["profit"] = VerifiedAnalyticsData.DecimalText(profit)
                });
            }
        }
        return new(AiWorkflowStepStatus.Completed, $"{rows.Count} zararına satış satırı bulundu; maliyeti kayıtlı olmayan {missingCost} satır hesaba katılmadı.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class ProductMarginWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("urun_kar_marji_analizi", "Ürün kâr marjı analizi",
        "Ranks sufficiently sold products by recorded profit margin and exposes missing-cost quantities.", RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>(PatientProductSummaryWorkflow.SalesParameters("profit"), StringComparer.Ordinal)
        { ["minimum_quantity"] = "Minimum absolute sold quantity.", ["limit"] = "Maximum products." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "product", "profit", "descending", 30, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var minimum = WorkflowData.Integer(context, "minimum_quantity", 10, 1, 1_000_000);
        var limit = WorkflowData.Integer(context, "limit", 20, 1, 200);
        var rows = fetch.Items.Where(item => Math.Abs(VerifiedAnalyticsData.Number(item, "quantity")) >= minimum)
            .Select(item =>
            {
                var revenue = VerifiedAnalyticsData.Number(item, "net_revenue");
                var profit = VerifiedAnalyticsData.Number(item, "profit");
                return (Item: item, Margin: revenue == 0 ? 0 : profit / revenue * 100m);
            }).OrderByDescending(value => value.Margin).Take(limit)
            .Select(value => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["barcode"] = VerifiedAnalyticsData.Text(value.Item, "group_key"), ["name"] = VerifiedAnalyticsData.Text(value.Item, "group_label"),
                ["quantity"] = VerifiedAnalyticsData.Text(value.Item, "quantity"), ["net_revenue"] = VerifiedAnalyticsData.Text(value.Item, "net_revenue"),
                ["profit"] = VerifiedAnalyticsData.Text(value.Item, "profit"), ["profit_margin"] = VerifiedAnalyticsData.Percent(value.Margin),
                ["missing_cost_quantity"] = VerifiedAnalyticsData.Text(value.Item, "missing_cost_quantity")
            }).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"En az {minimum} adet satılan {rows.Length} ürün kâr marjına göre sıralandı.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class AbcRevenueClassificationWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("abc_hasilat_siniflandirmasi", "ABC hasılat sınıflandırması",
        "Classifies products by cumulative net-revenue contribution: A through 80%, B through 95%, and C for the remainder.",
        RequiredTools: ["sale.aggregate_records"], Parameters: PatientProductSummaryWorkflow.SalesParameters("net_revenue"));
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var fetch = await VerifiedAnalyticsData.AggregateAsync(context, "product", "net_revenue", "descending", 365, cancellationToken, includeRefunds: false);
        if (fetch.Failure is not null) return fetch.Failure;
        var total = fetch.Items.Sum(item => VerifiedAnalyticsData.Number(item, "net_revenue"));
        decimal cumulative = 0;
        var rows = fetch.Items.Select(item =>
        {
            var revenue = VerifiedAnalyticsData.Number(item, "net_revenue");
            var share = total == 0 ? 0 : revenue / total * 100m;
            cumulative += share;
            var classification = cumulative <= 80m ? "A" : cumulative <= 95m ? "B" : "C";
            return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["barcode"] = VerifiedAnalyticsData.Text(item, "group_key"), ["name"] = VerifiedAnalyticsData.Text(item, "group_label"),
                ["net_revenue"] = VerifiedAnalyticsData.DecimalText(revenue), ["revenue_share"] = VerifiedAnalyticsData.Percent(share),
                ["cumulative_share"] = VerifiedAnalyticsData.Percent(cumulative), ["abc_class"] = classification
            };
        }).ToArray();
        return new(AiWorkflowStepStatus.Completed, $"{rows.Length} ürün net hasılat katkısına göre A/B/C olarak sınıflandırıldı.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class FilterInactiveProductsWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("pasif_urunleri_filtrele", "Pasif ürünleri filtrele",
        "Filters preceding product rows to products whose local catalogue on-market value is false.");
    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var source = context.Outputs.Reverse().Select(pair => pair.Value)
            .FirstOrDefault(value => value.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array);
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("rows", out var values))
            return Task.FromResult(WorkflowData.Failed("Filtrelenecek ürün sonucu bulunamadı."));
        var rows = values.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("is_on_market", out var active) &&
                (active.ValueKind == JsonValueKind.False || active.ValueKind == JsonValueKind.String &&
                    bool.TryParse(active.GetString(), out var parsed) && !parsed))
            .Select(WorkflowData.FlatRow).ToArray();
        return Task.FromResult(new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            rows.Length == 0 ? "Seçili sonuçlarda pasif ürün bulunmadı." : $"{rows.Length} pasif ürün bulundu.", WorkflowData.RowsOutput(rows)));
    }
}
