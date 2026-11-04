using System.Globalization;
using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Medula;

public sealed record LoyalPatientMedicationHistoryRow(
    string PrescriptionNo,
    string MedicationBarcode,
    DateOnly? MedicationPurchaseDate,
    DateOnly? AvailableDate,
    decimal? Quantity,
    string MedicationUsage,
    string ReportDiagnosisNo,
    string MedicationName = "");

public sealed record LoyalPatientMedicationBarcodeCandidate(
    string PrescriptionNo,
    string Barcode,
    string MedicationName,
    bool IsFinalContent);

public sealed record LoyalPatientReportHistoryRow(
    string ReportTrackingNo,
    string ReportNo,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Diagnosis);

public static partial class MedulaLoyalPatientHistoryParser
{
    [GeneratedRegex(@"(?<!\d)(?<barcode>\d{13,14})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex BarcodeRegex();

    public static IReadOnlyList<LoyalPatientMedicationHistoryRow> ParseMedications(
        MedulaRemoteScreen screen)
    {
        var table = screen.Tables.FirstOrDefault(candidate =>
            candidate.Title?.Contains("İlaç", StringComparison.CurrentCultureIgnoreCase) == true
            || candidate.Headers.Any(header => header.Contains(
                "Verilebileceği", StringComparison.CurrentCultureIgnoreCase)));
        if (table is null) return [];

        var rows = new List<LoyalPatientMedicationHistoryRow>();
        foreach (var row in table.Rows)
        {
            var combined = string.Join(' ', row);
            var medicationName = Value(table, row, "İlaç Adı");
            var barcode = BarcodeRegex().Matches(combined)
                .Select(match => match.Groups["barcode"].Value)
                .FirstOrDefault() ?? string.Empty;
            if (barcode.Length == 0 && medicationName.Length == 0) continue;
            rows.Add(new LoyalPatientMedicationHistoryRow(
                Value(table, row, "Reçete No"),
                barcode,
                ParseDate(Value(table, row, "İlaç Alım Tarihi")),
                ParseDate(Value(table, row, "Verilebileceği Tarih")),
                ParseDecimal(Value(table, row, "Adet")),
                Value(table, row, "İlaç Kullanımı"),
                FirstValue(table, row, "Rap. Teşhisi", "Rap. Tak. No"),
                medicationName));
        }
        return rows.Distinct().ToArray();
    }

    public static IReadOnlyList<LoyalPatientReportHistoryRow> ParseReports(
        MedulaRemoteScreen screen,
        DateOnly? asOf = null)
    {
        var table = screen.Tables.FirstOrDefault(candidate =>
            candidate.Title?.Contains("Rapor", StringComparison.CurrentCultureIgnoreCase) == true
            || candidate.Headers.Any(header => header.Contains(
                "Rapor Takip", StringComparison.CurrentCultureIgnoreCase)));
        if (table is null) return [];
        var activeOn = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        return table.Rows.Select(row => new LoyalPatientReportHistoryRow(
                Value(table, row, "Rapor Takip No"),
                Value(table, row, "Rapor No"),
                ParseDate(Value(table, row, "Başlangıç Tarihi")),
                ParseDate(Value(table, row, "Bitiş Tarihi")),
                Value(table, row, "Tanı")))
            .Where(row => row.EndDate is null || row.EndDate >= activeOn)
            .Distinct()
            .ToArray();
    }

    private static string FirstValue(
        MedulaRemoteTable table,
        IReadOnlyList<string> row,
        params string[] headers)
    {
        foreach (var header in headers)
        {
            var value = Value(table, row, header);
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static string Value(
        MedulaRemoteTable table,
        IReadOnlyList<string> row,
        string header) =>
        MedulaRemoteScreenRefiner.FindColumnValue(table.Headers, row, header);

    private static DateOnly? ParseDate(string value)
    {
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy" };
        return DateOnly.TryParseExact(
            value.Trim(), formats, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None,
            out var date) ? date : null;
    }

    private static decimal? ParseDecimal(string value)
    {
        var match = Regex.Match(value, @"-?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
        return match.Success && decimal.TryParse(
            match.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture,
            out var parsed) ? parsed : null;
    }
}

public static class LoyalPatientMedicationBarcodeResolver
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    public static string Resolve(
        LoyalPatientMedicationHistoryRow row,
        IEnumerable<LoyalPatientMedicationBarcodeCandidate> candidates)
    {
        if (IsBarcode(row.MedicationBarcode)) return row.MedicationBarcode.Trim();
        var name = Normalize(row.MedicationName);
        if (name.Length == 0) return string.Empty;

        var all = candidates
            .Where(candidate => IsBarcode(candidate.Barcode))
            .ToArray();
        var samePrescription = row.PrescriptionNo.Length == 0
            ? []
            : all.Where(candidate => string.Equals(
                candidate.PrescriptionNo.Trim(), row.PrescriptionNo.Trim(),
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var pool = samePrescription.Length > 0
            ? samePrescription
            : all.Where(candidate => candidate.PrescriptionNo.Length == 0).ToArray();
        var distinctBarcodes = pool.Select(candidate => candidate.Barcode.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (samePrescription.Length > 0 && distinctBarcodes.Length == 1)
            return distinctBarcodes[0];

        var ranked = pool
            .GroupBy(candidate => candidate.Barcode.Trim(), StringComparer.Ordinal)
            .Select(group => new
            {
                Barcode = group.Key,
                Score = group.Max(candidate =>
                    Similarity(name, Normalize(candidate.MedicationName))
                    + (candidate.IsFinalContent ? 0.01m : 0m))
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Barcode, StringComparer.Ordinal)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Score < 0.55m) return string.Empty;
        if (ranked.Length > 1 && ranked[0].Score - ranked[1].Score < 0.08m)
            return string.Empty;
        return ranked[0].Barcode;
    }

    public static string Normalize(string? value)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(
                (value ?? string.Empty).ToUpper(TurkishCulture), @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .ToArray();
        return string.Join(' ', tokens);
    }

    private static decimal Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0m;
        if (left == right) return 1m;
        if (left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal))
            return 0.92m * Math.Min(left.Length, right.Length)
                / Math.Max(left.Length, right.Length) + 0.08m;
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0m : (decimal)leftTokens.Intersect(rightTokens).Count() / union;
    }

    private static bool IsBarcode(string? value) =>
        value is { Length: >= 13 and <= 14 } && value.All(char.IsDigit);
}
