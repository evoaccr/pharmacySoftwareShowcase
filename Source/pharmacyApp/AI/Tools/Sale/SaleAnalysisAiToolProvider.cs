using System.Globalization;
using System.Text.Json;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.AI.Tools.Sale;

/// <summary>
/// Read-only business analyses over the same immutable sale/refund projection used by the Sales module.
/// The tools expose named analyses only; they do not accept SQL or mutate pharmacy data.
/// </summary>
public sealed class SaleAnalysisAiToolProvider : IAiToolProvider
{
    private readonly IRetailSaleRecordQueryService queries;
    private readonly IReadOnlyCollection<IAiTool> tools;

    public SaleAnalysisAiToolProvider(IRetailSaleRecordQueryService? queries = null)
    {
        this.queries = queries ?? new RetailSaleRecordQueryService();
        tools =
        [
            Create("sale.audit_record_consistency",
                "Audits bounded sale/refund records for empty product lines, payment/total mismatches, duplicate references, non-positive totals, or refund provenance.",
                ["empty_lines", "payment_mismatch", "duplicate_reference", "nonpositive_total", "refund_provenance", "same_qr_sale_refund_same_day"]),
            Create("sale.aggregate_payments",
                "Aggregates bounded sale/refund payment facts, average basket value, or monthly refund ratio.",
                ["payment_summary", "payment_reconciliation", "average_basket", "refund_rate_by_month"]),
            Create("sale.analyze_baskets",
                "Analyzes bounded sale baskets by month, weekday/weekend, discount cohort, patient average, maximum sale, product consistency, or products without refunds.",
                ["items_per_sale_by_month", "weekday_weekend", "discount_cohorts", "top_patients_average_basket", "patient_max_sale", "consistent_products", "no_refund_products"])
        ];
        AiToolDefinitionValidator.Validate(tools);
    }

    public IReadOnlyCollection<IAiTool> Tools => tools;

    private IAiTool Create(string name, string description, IReadOnlyList<string> kinds) =>
        new AnalysisTool(new AiToolDefinition(
            name, description, AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["analysis_kind"] = new("string", "Named, deterministic analysis to run.", true, kinds),
                ["start_date"] = new("string", "Inclusive start date in YYYY-MM-DD.", true),
                ["end_date"] = new("string", "Inclusive end date in YYYY-MM-DD.", true),
                ["sale_types"] = new("array", "Optional retail and/or prescription filter.", ItemsType: "string"),
                ["patient_record_id"] = new("integer", "Optional exact patient record ID."),
                ["reference_code"] = new("string", "Optional exact P/G transaction reference."),
                ["include_refunds"] = new("boolean", "Whether refunds are loaded.", DefaultValue: true),
                ["minimum_quantity"] = new("integer", "Optional minimum signed product quantity.", DefaultValue: 1),
                ["minimum_percent"] = new("number", "Optional minimum percentage threshold.", DefaultValue: 0m),
                ["limit"] = new("integer", "Maximum result rows, 1-200.", DefaultValue: 100),
                ["scan_limit"] = new("integer", "Maximum source records scanned, 1-10000.", DefaultValue: 10000)
            },
            new Dictionary<string, string>
            {
                ["analysis_kind"] = "string", ["rows"] = "structured analysis rows",
                ["source_record_count"] = "integer", ["total_matching_count"] = "integer",
                ["source_truncated"] = "boolean"
            },
            ["sale", "analysis", "read_only"]),
            (p, ct) => ExecuteAsync(name, p, ct));

    private async Task<object> ExecuteAsync(string toolName, JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("analysis_kind", "start_date", "end_date", "sale_types", "patient_record_id",
            "include_refunds", "minimum_quantity", "minimum_percent", "limit", "scan_limit", "reference_code");
        var kind = reader.RequireString("analysis_kind");
        var referenceCode = reader.OptionalString("reference_code");
        var start = ParseDate(reader.RequireString("start_date"), "start_date");
        var end = ParseDate(reader.RequireString("end_date"), "end_date");
        if (end < start) throw new AiToolValidationException("'end_date' cannot be earlier than 'start_date'.");
        var saleTypes = reader.OptionalStringArray("sale_types", 2)
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "retail" or "prescription")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (saleTypes.Length == 0) saleTypes = ["retail", "prescription"];
        var patientId = reader.OptionalNullableInteger("patient_record_id", 1, int.MaxValue);
        var includeRefunds = reader.OptionalBoolean("include_refunds", true);
        var limit = reader.OptionalInteger("limit", 100, 1, 200);
        var scanLimit = reader.OptionalInteger("scan_limit", 10000, 1, 10000);
        var minimumQuantity = reader.OptionalInteger("minimum_quantity", 1, 1, 1_000_000);
        var minimumPercent = reader.OptionalDecimal("minimum_percent", 0m, 0m, 100m);
        var filter = new RetailSaleRecordFilter(
            start.ToDateTime(TimeOnly.MinValue), end.AddDays(1).ToDateTime(TimeOnly.MinValue),
            saleTypes, includeRefunds, [], [], [], [], patientId, "", "", null, false, null, null);
        var records = new List<RetailSaleRecord>();
        const int pageSize = 200;
        var totalSource = 0;
        if (referenceCode is not null)
        {
            var record = await queries.GetAsync(referenceCode.ToUpperInvariant(), cancellationToken);
            if (record is not null) records.Add(record);
            totalSource = records.Count;
        }
        else
        {
            for (var offset = 0; offset < scanLimit; offset += pageSize)
            {
                var page = await queries.SearchAsync(new RetailSaleRecordSearchQuery(
                    filter, offset, Math.Min(pageSize, scanLimit - offset), true, true), cancellationToken);
                totalSource = page.TotalCount;
                records.AddRange(page.Records);
                if (records.Count >= scanLimit || offset + page.Records.Count >= page.TotalCount || page.Records.Count == 0) break;
            }
        }

        IEnumerable<IReadOnlyDictionary<string, object?>> result = toolName switch
        {
            "sale.audit_record_consistency" => Audit(kind, records),
            "sale.aggregate_payments" => Payments(kind, records, minimumPercent),
            "sale.analyze_baskets" => Baskets(kind, records, minimumQuantity),
            _ => throw new AiToolValidationException("Unknown analysis tool.")
        };
        var all = result.ToArray();
        return new
        {
            analysis_kind = kind,
            rows = all.Take(limit).ToArray(),
            source_record_count = records.Count,
            total_matching_count = all.Length,
            source_truncated = totalSource > records.Count
        };
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Audit(string kind, IReadOnlyList<RetailSaleRecord> records) =>
        kind switch
        {
            "empty_lines" => records.Where(r => r.Lines.Count == 0).Select(RecordRow),
            "payment_mismatch" => records.Where(r => Math.Abs(r.Payments.Sum(p => p.Amount) - r.TotalAmount) > 0.01m)
                .Select(r => Row(("timestamp", r.Timestamp), ("reference_code", r.ReferenceCode),
                    ("stored_total", r.TotalAmount), ("payment_total", r.Payments.Sum(p => p.Amount)),
                    ("difference", r.Payments.Sum(p => p.Amount) - r.TotalAmount))),
            "duplicate_reference" => records.GroupBy(r => r.ReferenceCode, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => Row(("reference_code", g.Key), ("duplicate_count", g.Count()))),
            "nonpositive_total" => records.Where(r => r.TotalAmount <= 0).Select(RecordRow),
            "same_qr_sale_refund_same_day" => records.SelectMany(r => r.Lines.Select(line => new { Record = r, Line = line }))
                .Where(x => !string.IsNullOrWhiteSpace(x.Line.QrCode))
                .GroupBy(x => new { x.Line.QrCode, Day = x.Record.Timestamp.Date })
                .Where(g => g.Any(x => IsSale(x.Record)) && g.Any(x => !IsSale(x.Record)))
                .Select(g => Row(("date", g.Key.Day), ("qr_code", g.Key.QrCode),
                    ("sale_references", string.Join(", ", g.Where(x => IsSale(x.Record)).Select(x => x.Record.ReferenceCode).Distinct())),
                    ("refund_references", string.Join(", ", g.Where(x => !IsSale(x.Record)).Select(x => x.Record.ReferenceCode).Distinct())))),
            "refund_provenance" => records.Where(r => r.RecordType.Equals("refund", StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Lines.DefaultIfEmpty()).Select(line => line is null
                    ? RecordRow(records.First(r => r.RecordType.Equals("refund", StringComparison.OrdinalIgnoreCase)))
                    : Row(("timestamp", records.First(r => r.Lines.Contains(line)).Timestamp),
                        ("refund_reference", records.First(r => r.Lines.Contains(line)).ReferenceCode),
                        ("original_sale_reference", line.OriginalSaleId is > 0 ? $"P{line.OriginalSaleId}" : ""),
                        ("barcode", line.Barcode), ("medicine_name", line.ProductName), ("net_amount", line.NetPrice))),
            _ => throw new AiToolValidationException("Unsupported 'analysis_kind'.")
        };

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Payments(string kind, IReadOnlyList<RetailSaleRecord> records, decimal minimumPercent) =>
        kind switch
        {
            "payment_reconciliation" => records.Select(r => Row(("reference_code", r.ReferenceCode), ("stored_total", r.TotalAmount),
                    ("payment_total", r.Payments.Sum(p => p.Amount)), ("difference", r.Payments.Sum(p => p.Amount) - r.TotalAmount),
                    ("is_reconciled", Math.Abs(r.Payments.Sum(p => p.Amount) - r.TotalAmount) <= 0.01m))),
            "payment_summary" => records.SelectMany(r => r.Payments)
                .GroupBy(p => p.Type, StringComparer.OrdinalIgnoreCase)
                .Select(g => Row(("payment_type", g.Key), ("record_count", g.Count()), ("amount", g.Sum(p => p.Amount)))),
            "average_basket" => [Row(("record_count", records.Count), ("quantity", records.Sum(r => r.Quantity)),
                ("net_revenue", records.Sum(r => r.NetAmount)),
                ("average_basket", records.Count == 0 ? 0 : records.Average(r => r.NetAmount)))],
            "refund_rate_by_month" => records.GroupBy(r => new DateTime(r.Timestamp.Year, r.Timestamp.Month, 1))
                .OrderBy(g => g.Key).Select(g =>
                {
                    var sales = g.Where(IsSale).ToArray();
                    var refunds = g.Where(r => !IsSale(r)).ToArray();
                    var gross = sales.Sum(r => r.GrossAmount);
                    var refundAmount = Math.Abs(refunds.Sum(r => r.GrossAmount));
                    var ratio = gross == 0 ? 0 : refundAmount * 100m / gross;
                    return new { Ratio = ratio, Row = Row(("month", g.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture)),
                        ("sale_count", sales.Length), ("refund_count", refunds.Length), ("gross_sales", gross),
                        ("refund_total", refundAmount), ("refund_amount_rate_percent", ratio)) };
                }).Where(x => x.Ratio >= minimumPercent).Select(x => x.Row),
            _ => throw new AiToolValidationException("Unsupported 'analysis_kind'.")
        };

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Baskets(
        string kind, IReadOnlyList<RetailSaleRecord> records, int minimumQuantity) =>
        kind switch
        {
            "items_per_sale_by_month" => records.Where(IsSale).GroupBy(r => new DateTime(r.Timestamp.Year, r.Timestamp.Month, 1))
                .OrderBy(g => g.Key).Select(g => Row(("month", g.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture)),
                    ("sale_count", g.Count()), ("quantity", g.Sum(r => r.Quantity)),
                    ("average_items_per_sale", g.Average(r => r.Quantity)))),
            "weekday_weekend" => records.Where(IsSale).GroupBy(r => r.Timestamp.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "weekend" : "weekday")
                .Select(g => Row(("period_type", g.Key), ("sale_count", g.Count()), ("quantity", g.Sum(r => r.Quantity)),
                    ("net_revenue", g.Sum(r => r.NetAmount)), ("average_basket", g.Average(r => r.NetAmount)))),
            "discount_cohorts" => records.Where(IsSale).GroupBy(r => r.DiscountAmount > 0 ? "discounted" : "not_discounted")
                .Select(g => Row(("discount_cohort", g.Key), ("sale_count", g.Count()),
                    ("net_revenue", g.Sum(r => r.NetAmount)), ("average_basket", g.Average(r => r.NetAmount)))),
            "top_patients_average_basket" => records.Where(r => IsSale(r) && r.PatientRecordId.HasValue)
                .GroupBy(r => new { r.PatientRecordId, r.PatientName }).Select(g => Row(("patient_record_id", g.Key.PatientRecordId),
                    ("patient_name", g.Key.PatientName), ("sale_count", g.Count()),
                    ("net_revenue", g.Sum(r => r.NetAmount)), ("average_basket", g.Average(r => r.NetAmount))))
                .OrderByDescending(r => Convert.ToDecimal(r["average_basket"], CultureInfo.InvariantCulture)),
            "patient_max_sale" => records.Where(IsSale).OrderByDescending(r => r.NetAmount).Take(1).Select(RecordRow),
            "consistent_products" => ProductGroups(records).Where(g => g.Value.Months.Count > 1 && g.Value.Months.Count == DistinctMonths(records))
                .Select(g => ProductRow(g.Key, g.Value)),
            "no_refund_products" => ProductGroups(records).Where(g => g.Value.Quantity >= minimumQuantity && g.Value.RefundCount == 0)
                .Select(g => ProductRow(g.Key, g.Value)).OrderByDescending(r => Convert.ToInt32(r["quantity"], CultureInfo.InvariantCulture)),
            _ => throw new AiToolValidationException("Unsupported 'analysis_kind'.")
        };

    private static Dictionary<(string Barcode, string Name), ProductBucket> ProductGroups(IEnumerable<RetailSaleRecord> records)
    {
        var result = new Dictionary<(string, string), ProductBucket>();
        foreach (var record in records)
        foreach (var line in record.Lines)
        {
            var key = (line.Barcode, line.ProductName);
            if (!result.TryGetValue(key, out var bucket)) result[key] = bucket = new ProductBucket();
            var refund = record.RecordType.Equals("refund", StringComparison.OrdinalIgnoreCase);
            bucket.Quantity += refund ? -1 : 1;
            bucket.Net += refund ? -line.NetPrice : line.NetPrice;
            if (refund) bucket.RefundCount++;
            else bucket.Months.Add(new DateTime(record.Timestamp.Year, record.Timestamp.Month, 1));
        }
        return result;
    }

    private static int DistinctMonths(IEnumerable<RetailSaleRecord> records) =>
        records.Where(IsSale).Select(r => new { r.Timestamp.Year, r.Timestamp.Month }).Distinct().Count();

    private static IReadOnlyDictionary<string, object?> ProductRow((string Barcode, string Name) key, ProductBucket value) =>
        Row(("barcode", key.Barcode), ("medicine_name", key.Name), ("quantity", value.Quantity),
            ("net_revenue", value.Net), ("refund_count", value.RefundCount), ("active_month_count", value.Months.Count));

    private static IReadOnlyDictionary<string, object?> RecordRow(RetailSaleRecord r) =>
        Row(("timestamp", r.Timestamp), ("reference_code", r.ReferenceCode), ("record_type", r.RecordType),
            ("sale_type", r.SaleType), ("patient_name", r.PatientName), ("quantity", r.Quantity),
            ("net_amount", r.NetAmount), ("total_amount", r.TotalAmount));

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    private static bool IsSale(RetailSaleRecord record) =>
        !record.RecordType.Equals("refund", StringComparison.OrdinalIgnoreCase);

    private static DateOnly ParseDate(string value, string name) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new AiToolValidationException($"'{name}' must use YYYY-MM-DD.");

    private sealed class ProductBucket
    {
        public int Quantity { get; set; }
        public decimal Net { get; set; }
        public int RefundCount { get; set; }
        public HashSet<DateTime> Months { get; } = [];
    }

    private sealed class AnalysisTool(
        AiToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<object>> execute) : IAiTool
    {
        public AiToolDefinition Definition { get; } = definition;
        public Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default) =>
            execute(parameters, cancellationToken);
    }
}
