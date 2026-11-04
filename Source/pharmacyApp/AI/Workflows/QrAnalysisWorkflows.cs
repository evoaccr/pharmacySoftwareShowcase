using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pharmacyApp.AI.Workflows;

internal static class QrAnalysisWorkflowData
{
    public static AiWorkflowContext WithInferredRange(AiWorkflowContext context)
    {
        var parameters = new Dictionary<string, string>(
            context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (parameters.Keys.Any(key => key is "start_date" or "end_date" or "period_days" or "period_months"))
            return context with { Parameters = parameters };
        var text = Normalize(context.EffectiveRequest);
        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (text.Contains("bugun", StringComparison.Ordinal))
        {
            parameters["start_date"] = today;
            parameters["end_date"] = today;
        }
        else if (TryPeriod(text, out var key, out var value))
        {
            parameters[key] = value;
        }
        return context with { Parameters = parameters };
    }

    public static IReadOnlyList<string> Barcodes(AiWorkflowContext context)
    {
        var configured = WorkflowData.Barcodes(context);
        if (configured.Count > 0) return configured;
        return Regex.Matches(context.EffectiveRequest, @"(?<![0-9])[0-9]{8,14}(?![0-9])", RegexOptions.CultureInvariant)
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(20).ToArray();
    }

    public static string Normalize(string value) => value.ToLower(CultureInfo.GetCultureInfo("tr-TR"))
        .Replace('ü', 'u').Replace('ı', 'i').Replace('ş', 's').Replace('ç', 'c').Replace('ö', 'o').Replace('ğ', 'g');

    private static bool TryPeriod(string text, out string key, out string value)
    {
        var match = Regex.Match(text, @"\bson\s+(?<count>[0-9]{1,3})\s*(?<unit>gun|gunluk|ay|aylik)\b",
            RegexOptions.CultureInvariant);
        if (match.Success)
        {
            key = match.Groups["unit"].Value.StartsWith("ay", StringComparison.Ordinal) ? "period_months" : "period_days";
            value = match.Groups["count"].Value;
            return true;
        }
        var words = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["bir"] = 1, ["iki"] = 2, ["uc"] = 3, ["dort"] = 4, ["bes"] = 5, ["alti"] = 6,
            ["yedi"] = 7, ["sekiz"] = 8, ["dokuz"] = 9, ["on"] = 10
        };
        match = Regex.Match(text, @"\bson\s+(?<count>bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on)\s+(?<unit>gun|ay)\b",
            RegexOptions.CultureInvariant);
        if (match.Success)
        {
            key = match.Groups["unit"].Value == "ay" ? "period_months" : "period_days";
            value = words[match.Groups["count"].Value].ToString(CultureInfo.InvariantCulture);
            return true;
        }
        key = string.Empty;
        value = string.Empty;
        return false;
    }
}

public sealed class MovementAggregateWorkflow : IAiWorkflow
{
    private const string Tool = "qr.aggregate_movements";

    public AiWorkflowDefinition Definition { get; } = new(
        "hareket_dagilimi_analizi",
        "Karekod hareket dağılımı",
        "Aggregates authoritative QR movements by movement type, direction, barcode, medicine, address, user, day, week, hour, weekday or day-hour without exposing arbitrary rows.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "Optional exact barcode list.",
            ["start_date"] = "Inclusive YYYY-MM-DD.",
            ["end_date"] = "Inclusive YYYY-MM-DD.",
            ["period_days"] = "Relative days.",
            ["period_months"] = "Relative months.",
            ["movement_type_codes"] = "Optional configured movement type codes.",
            ["direction"] = "all, entry, or exit.",
            ["address_type"] = "all, patient, pharmacy, or depot.",
            ["address_code"] = "Optional exact prefixed address.",
            ["user_id"] = "Optional exact user.",
            ["group_by"] = "movement_type, direction, barcode, medicine, address, user, day, week, hour, weekday, or day_hour.",
            ["missing_user_only"] = "true to aggregate only movements without a user.",
            ["limit"] = "Maximum groups, 1-500."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var ranged = QrAnalysisWorkflowData.WithInferredRange(context);
        var (start, end) = WorkflowData.DateRange(ranged, 30);
        var text = QrAnalysisWorkflowData.Normalize(context.EffectiveRequest);
        var requestedGroup = WorkflowData.Parameter(context, "group_by");
        var groupBy = requestedGroup is "movement_type" or "direction" or "barcode" or "medicine" or "address" or "user" or "day" or "week" or "hour" or "weekday" or "day_hour"
            ? requestedGroup :
            text.Contains("gun ve saat", StringComparison.Ordinal) ? "day_hour" :
            text.Contains("saat", StringComparison.Ordinal) && text.Contains("yogun", StringComparison.Ordinal) ? "hour" :
            text.Contains("hareket adres", StringComparison.Ordinal) || text.Contains("depoya gore", StringComparison.Ordinal) ? "address" :
            text.Contains("kullanici", StringComparison.Ordinal) ? "user" :
            text.Contains("haftanin gun", StringComparison.Ordinal) ? "weekday" :
            (text.Contains("barkodlar", StringComparison.Ordinal) || QrAnalysisWorkflowData.Barcodes(context).Count > 1) ? "barcode" :
            text.Contains("giris ve cikis", StringComparison.Ordinal) && !text.Contains("hareket tur", StringComparison.Ordinal) ? "direction" :
            "movement_type";
        var directionParameter = WorkflowData.Parameter(context, "direction");
        var direction = directionParameter is "entry" or "exit" ? directionParameter :
            text.Contains("depo giris", StringComparison.Ordinal) ? "entry" :
            text.Contains("satis cikis", StringComparison.Ordinal) ? "exit" : "all";
        var addressParameter = WorkflowData.Parameter(context, "address_type");
        var addressType = addressParameter is "patient" or "pharmacy" or "depot" ? addressParameter :
            text.Contains("depo", StringComparison.Ordinal) && text.Contains("adres", StringComparison.Ordinal) ? "depot" : "all";
        var toolParameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["start_date"] = start,
            ["end_date"] = end,
            ["direction"] = direction,
            ["address_type"] = addressType,
            ["group_by"] = groupBy,
            ["missing_user_only"] = WorkflowData.Boolean(context, "missing_user_only"),
            ["limit"] = WorkflowData.Integer(context, "limit", 100, 1, 500),
            ["scan_limit"] = 250
        };
        AddOptionalList(toolParameters, "movement_type_codes", WorkflowData.List(context, "movement_type_codes"));
        AddOptionalList(toolParameters, "barcodes", QrAnalysisWorkflowData.Barcodes(context));
        AddOptionalText(toolParameters, "user_id", WorkflowData.Parameter(context, "user_id"));
        AddOptionalText(toolParameters, "address_code", WorkflowData.Parameter(context, "address_code"));
        var execution = await WorkflowData.Execute(context, Tool, toolParameters, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Karekod hareket dağılımı alınamadı.");
        var groups = execution.Output.TryGetProperty("groups", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => (IReadOnlyDictionary<string, string>)WorkflowData.FlatRow(value)).ToArray()
            : [];
        var total = execution.Output.TryGetProperty("total_group_count", out var totalElement) && totalElement.TryGetInt32(out var parsedTotal)
            ? parsedTotal : groups.Length;
        var scanned = execution.Output.TryGetProperty("scanned_movement_count", out var scannedElement) && scannedElement.TryGetInt32(out var parsedScanned)
            ? parsedScanned : 0;
        var truncated = execution.Output.TryGetProperty("truncated", out var truncatedElement) && truncatedElement.ValueKind == JsonValueKind.True;
        var message = groups.Length == 0
            ? $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} döneminde filtrelere uyan karekod hareketi bulunamadı."
            : $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} döneminde {scanned:N0} hareket {total:N0} grupta özetlendi" +
              (truncated ? $"; performans sınırı nedeniyle en yeni {scanned:N0} hareket tarandı ve sonuç kısmi." : ".");
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(groups, new
            {
                group_by = groupBy,
                total_count = total,
                scanned_movement_count = scanned,
                truncated,
                visible_columns = new[] { "group_label", "movement_count", "entry_count", "exit_count", "distinct_qr_count" }
            }));
    }

    private static void AddOptionalList(Dictionary<string, object> values, string key, IReadOnlyList<string> items)
    {
        if (items.Count > 0) values[key] = items;
    }

    private static void AddOptionalText(Dictionary<string, object> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
    }
}

public sealed class MovementAuditWorkflow : IAiWorkflow
{
    private const string Tool = "qr.audit_movement_chains";

    public AiWorkflowDefinition Definition { get; } = new(
        "karekod_tutarlilik_kontrolu",
        "Karekod tutarlılık kontrolü",
        "Runs a read-only audit for repeated QR movements or exits without a preceding entry in the selected period. It never changes stock or movement records.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "Optional exact barcode list.",
            ["start_date"] = "Inclusive YYYY-MM-DD.",
            ["end_date"] = "Inclusive YYYY-MM-DD.",
            ["period_days"] = "Relative days.",
            ["period_months"] = "Relative months.",
            ["audit_kind"] = "all, repeated, exit_without_entry, entry_without_exit, or missing_address.",
            ["repeat_window_minutes"] = "Repeated-movement window in minutes.",
            ["limit"] = "Maximum issues, 1-500."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var ranged = QrAnalysisWorkflowData.WithInferredRange(context);
        var (start, end) = WorkflowData.DateRange(ranged, 30);
        var text = QrAnalysisWorkflowData.Normalize(context.EffectiveRequest);
        var requestedAudit = WorkflowData.Parameter(context, "audit_kind");
        var auditKind = text.Contains("tekrar", StringComparison.Ordinal) || text.Contains("yinelen", StringComparison.Ordinal)
            ? "repeated"
            : text.Contains("giris", StringComparison.Ordinal) && text.Contains("olmadan", StringComparison.Ordinal)
                ? "exit_without_entry"
                : requestedAudit is "repeated" or "repeated_qr_movements" ? "repeated"
                : requestedAudit is "exit_without_entry" ? "exit_without_entry"
                : requestedAudit is "entry_without_exit" ? "entry_without_exit"
                : requestedAudit is "missing_address" ? "missing_address"
                : "all";
        var toolParameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["start_date"] = start,
            ["end_date"] = end,
            ["direction"] = "all",
            ["address_type"] = WorkflowData.Parameter(context, "address_type") ?? "all",
            ["audit_kind"] = auditKind,
            ["repeat_window_minutes"] = WorkflowData.Integer(context, "repeat_window_minutes", 5, 0, 1440),
            ["limit"] = WorkflowData.Integer(context, "limit", 200, 1, 500),
            ["scan_limit"] = 250
        };
        var movementTypes = WorkflowData.List(context, "movement_type_codes");
        if (movementTypes.Count > 0) toolParameters["movement_type_codes"] = movementTypes;
        var barcodes = QrAnalysisWorkflowData.Barcodes(context);
        if (barcodes.Count > 0) toolParameters["barcodes"] = barcodes;
        var userId = WorkflowData.Parameter(context, "user_id");
        if (!string.IsNullOrWhiteSpace(userId)) toolParameters["user_id"] = userId;
        var addressCode = WorkflowData.Parameter(context, "address_code");
        if (!string.IsNullOrWhiteSpace(addressCode)) toolParameters["address_code"] = addressCode;
        var execution = await WorkflowData.Execute(context, Tool, toolParameters, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Karekod hareket tutarlılığı denetlenemedi.");
        var rows = execution.Output.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array
            ? issues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => (IReadOnlyDictionary<string, string>)WorkflowData.FlatRow(value)).ToArray()
            : [];
        var scanned = execution.Output.TryGetProperty("scanned_movement_count", out var scannedElement) && scannedElement.TryGetInt32(out var parsedScanned)
            ? parsedScanned : 0;
        var truncated = execution.Output.TryGetProperty("truncated", out var truncatedElement) && truncatedElement.ValueKind == JsonValueKind.True;
        var message = rows.Length == 0
            ? $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} döneminde {auditKind} türünde şüpheli hareket bulunamadı."
            : $"{ExtendedWorkflowData.FormatDate(start)}–{ExtendedWorkflowData.FormatDate(end)} dönemindeki {scanned:N0} hareket içinde {rows.Length:N0} şüpheli kayıt bulundu" +
              (truncated ? "; tarama sınırı nedeniyle sonuç kısmi." : ".");
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                total_count = rows.Length,
                scanned_movement_count = scanned,
                truncated,
                visible_columns = new[] { "movement_date", "qr_code", "medicine_name", "issue_type", "movement_type", "direction", "reference_code", "detail" }
            }));
    }
}
