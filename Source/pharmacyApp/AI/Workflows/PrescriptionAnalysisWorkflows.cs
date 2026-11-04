using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public sealed class PrescriptionReportStatusSummaryWorkflow : IAiWorkflow
{
    private const string Tool = "medula.aggregate_prescription_reports";

    public AiWorkflowDefinition Definition { get; } = new(
        "recete_rapor_durumu_ozeti",
        "Reçete rapor durumu özeti",
        "Counts locally stored prescriptions with or without a medical report in an exact or relative period. Use this for raporlu/raporsuz reçete counts; it is not a medicine, stock, sale-status, or Medula-submission query.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["start_date"] = "Exact inclusive YYYY-MM-DD.",
            ["end_date"] = "Exact inclusive YYYY-MM-DD.",
            ["period_days"] = "Relative day count.",
            ["period_months"] = "Relative month count.",
            ["report_status"] = "reported, unreported, or both."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = WorkflowData.DateRange(context, 30);
        var requested = NormalizeStatus(WorkflowData.Parameter(context, "report_status"));
        var execution = await WorkflowData.Execute(context, Tool, new
        {
            start_date = start,
            end_date = end
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Reçete rapor durumu özeti alınamadı.");
        static int Count(JsonElement output, string key) =>
            output.TryGetProperty(key, out var value) && value.TryGetInt32(out var count) ? count : 0;
        var reported = Count(execution.Output, "reported_count");
        var unreported = Count(execution.Output, "unreported_count");
        var total = Count(execution.Output, "total_count");
        var selectedCount = requested switch
        {
            "reported" => reported,
            "unreported" => unreported,
            _ => total
        };
        var period = $"{FormatDate(start)}–{FormatDate(end)}";
        var statusLabel = requested switch
        {
            "reported" => "raporlu",
            "unreported" => "raporsuz",
            _ => "toplam"
        };
        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["start_date"] = start,
            ["end_date_inclusive"] = end,
            ["requested_report_status"] = statusLabel,
            ["prescription_count"] = selectedCount.ToString(CultureInfo.InvariantCulture),
            ["reported_prescription_count"] = reported.ToString(CultureInfo.InvariantCulture),
            ["unreported_prescription_count"] = unreported.ToString(CultureInfo.InvariantCulture),
            ["total_prescription_count"] = total.ToString(CultureInfo.InvariantCulture)
        };
        var message = requested switch
        {
            "reported" => $"{period} döneminde {reported:N0} raporlu reçete bulundu. Toplam {total:N0} reçetenin {unreported:N0} tanesi raporsuz.",
            "unreported" => $"{period} döneminde {unreported:N0} raporsuz reçete bulundu. Toplam {total:N0} reçetenin {reported:N0} tanesi raporlu.",
            _ => $"{period} döneminde {total:N0} reçete bulundu: {reported:N0} raporlu, {unreported:N0} raporsuz."
        };
        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            message,
            WorkflowData.RowsOutput([row], new
            {
                start_date = start,
                end_date = end,
                report_status = requested,
                total_count = 1,
                highlight_field = "prescription_count",
                visible_columns = new[]
                {
                    "start_date", "end_date_inclusive", "prescription_count",
                    "reported_prescription_count", "unreported_prescription_count"
                }
            }));
    }

    private static string NormalizeStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "reported" or "raporlu" => "reported",
        "unreported" or "raporsuz" => "unreported",
        _ => "both"
    };

    private static string FormatDate(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
public sealed class SalesLifecycleStatusSummaryWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "satis_durum_ozeti",
        "Satış durumlarını karşılaştır",
        "Compares sale transaction counts and net revenue for explicit lifecycle statuses such as pending, Medula-reported, completed, reconciliation-required, or cancelled.",
        RequiredTools: ["sale.aggregate_records"],
        Parameters: new Dictionary<string, string>
        {
            ["start_date"] = "Exact inclusive YYYY-MM-DD.",
            ["end_date"] = "Exact inclusive YYYY-MM-DD.",
            ["period_days"] = "Relative day count.",
            ["period_months"] = "Relative month count.",
            ["sale_types"] = "retail, prescription, or both.",
            ["statuses"] = "Comma-separated pending, medula_reported, completed, reconciliation_required, or cancelled."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var statuses = WorkflowData.List(context, "statuses")
            .Select(NormalizeStatus)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (statuses.Length == 0) statuses = ["pending", "completed"];
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var status in statuses)
        {
            var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["statuses"] = status,
                ["metric"] = "record_count",
                ["group_by"] = "record_type",
                ["include_refunds"] = "false"
            };
            var result = await new SalesRevenueSummaryWorkflow().ExecuteAsync(context with { Parameters = parameters }, cancellationToken);
            if (result.Status != AiWorkflowStepStatus.Completed) return result;
            var summary = ExtendedWorkflowData.Rows(result.Output).FirstOrDefault() ?? new Dictionary<string, string>();
            rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = status,
                ["sale_count"] = summary.GetValueOrDefault("sale_count", "0"),
                ["product_quantity"] = summary.GetValueOrDefault("product_quantity", "0"),
                ["net_revenue"] = summary.GetValueOrDefault("net_revenue", "0")
            });
        }
        var period = WorkflowData.DateRange(context, 30);
        var message = $"{ExtendedWorkflowData.FormatDate(period.Start)}–{ExtendedWorkflowData.FormatDate(period.End)} dönemi için " +
                      string.Join(" · ", rows.Select(row => $"{StatusLabel(row["status"])}: {row["sale_count"]} satış"));
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                total_count = rows.Count,
                visible_columns = new[] { "status", "sale_count", "product_quantity", "net_revenue" }
            }));
    }

    private static string? NormalizeStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "pending" or "bekleyen" => "pending",
        "medula_reported" or "medula" => "medula_reported",
        "completed" or "tamamlanan" or "tamamlandi" => "completed",
        "reconciliation_required" or "mutabakat" => "reconciliation_required",
        "cancelled" or "iptal" => "cancelled",
        _ => null
    };

    private static string StatusLabel(string value) => value switch
    {
        "pending" => "Bekleyen",
        "medula_reported" => "Medula'ya bildirilen",
        "completed" => "Tamamlanan",
        "reconciliation_required" => "Mutabakat gereken",
        "cancelled" => "İptal edilen",
        _ => value
    };
}