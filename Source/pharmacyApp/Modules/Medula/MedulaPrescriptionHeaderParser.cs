using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Medula;

/// <summary>
/// Normalizes the compact saved-prescription summary rendered by Medula.
/// The summary is more reliable than similarly named controls elsewhere on the page.
/// </summary>
public static partial class MedulaPrescriptionHeaderParser
{
    public static IReadOnlyDictionary<string, string> Normalize(
        IReadOnlyDictionary<string, string>? scrapedFields,
        string? pageText,
        MedulaPrescriptionListItem? source = null)
    {
        var fields = scrapedFields is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(scrapedFields, StringComparer.OrdinalIgnoreCase);
        var summary = SavedPrescriptionSummaryRegex().Match(NormalizeWhitespace(pageText));
        if (summary.Success)
        {
            Set(fields, "medula:prescription-no", summary.Groups["number"].Value);
            Set(fields, "medula:qr-status", summary.Groups["qr"].Value);
            Set(fields, "medula:coverage", summary.Groups["coverage"].Value);
            Set(fields, "medula:patient-type", summary.Groups["insurance"].Value);
            Set(fields, "medula:insurance-type", summary.Groups["insurance"].Value);
            Set(fields, "medula:invoice-type", $"{summary.Groups["invoice"].Value.ToUpperInvariant()} Grubu");
        }

        if (!IsUsefulCoverage(Value(fields, "medula:coverage")))
            fields.Remove("medula:coverage");
        var normalizedInvoice = NormalizeInvoiceValue(Value(fields, "medula:invoice-type"));
        if (!IsUsefulInvoice(normalizedInvoice))
            fields.Remove("medula:invoice-type");
        else
            Set(fields, "medula:invoice-type", normalizedInvoice);
        if (LooksLikeHeader(Value(fields, "medula:patient-type")))
            fields.Remove("medula:patient-type");
        if (LooksLikeHeader(Value(fields, "medula:qr-status")))
            fields.Remove("medula:qr-status");

        if (!IsValidPrescriptionNumber(Value(fields, "medula:prescription-no"))
            && IsValidPrescriptionNumber(source?.PrescriptionNumber))
            Set(fields, "medula:prescription-no", source!.PrescriptionNumber);
        if (!IsUseful(Value(fields, "medula:qr-status")) && IsUseful(source?.QrCodeStatus))
            Set(fields, "medula:qr-status", source!.QrCodeStatus);
        if (!IsUsefulCoverage(Value(fields, "medula:coverage")) && IsUsefulCoverage(source?.Coverage))
            Set(fields, "medula:coverage", source!.Coverage);
        if (!IsUsefulInvoice(Value(fields, "medula:invoice-type")) && source is not null)
            Set(fields, "medula:invoice-type",
                MedulaPrescriptionListTypes.ResolveInvoiceType(string.Empty, source.TypeValue));

        return fields;
    }

    public static MedulaPrescriptionSnapshot Normalize(MedulaPrescriptionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var fields = Normalize(snapshot.Page.FieldValues, snapshot.Page.PageText, snapshot.SourceListItem);
        var source = snapshot.SourceListItem;
        if (source is not null)
        {
            var number = Value(fields, "medula:prescription-no");
            source = source with
            {
                PrescriptionNumber = IsValidPrescriptionNumber(number) ? number : source.PrescriptionNumber,
                Coverage = FirstUseful(Value(fields, "medula:coverage"), source.Coverage),
                QrCodeStatus = FirstUseful(Value(fields, "medula:qr-status"), source.QrCodeStatus)
            };
        }

        return snapshot with
        {
            Page = snapshot.Page with { FieldValues = fields },
            SourceListItem = source
        };
    }

    public static bool IsValidPrescriptionNumber(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length >= 4
            && normalized != "0"
            && normalized != "-"
            && normalized != "—"
            && normalized.All(character => char.IsLetterOrDigit(character) || character == '-');
    }

    public static bool HasAuthoritativeSummary(
        IReadOnlyDictionary<string, string>? fields,
        string? expectedPrescriptionNumber = null)
    {
        if (fields is null) return false;
        var number = Value(fields, "medula:prescription-no");
        if (!IsValidPrescriptionNumber(number)
            || IsValidPrescriptionNumber(expectedPrescriptionNumber)
            && !number.Equals(expectedPrescriptionNumber!.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        var patientType = Value(fields, "medula:patient-type");
        return IsUseful(Value(fields, "medula:qr-status"))
            && IsUsefulCoverage(Value(fields, "medula:coverage"))
            && IsUsefulInvoice(Value(fields, "medula:invoice-type"))
            && (patientType.Equals("Çalışan", StringComparison.CurrentCultureIgnoreCase)
                || patientType.Equals("Emekli", StringComparison.CurrentCultureIgnoreCase));
    }

    private static string NormalizeWhitespace(string? value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string Value(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static void Set(IDictionary<string, string> fields, string key, string? value)
    {
        var normalized = NormalizeWhitespace(value);
        if (normalized.Length > 0) fields[key] = normalized;
    }

    private static bool IsUseful(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim() is not "-" and not "—";

    private static bool IsUsefulCoverage(string? value) =>
        IsUseful(value)
        && !value!.Contains("Fatura", StringComparison.CurrentCultureIgnoreCase)
        && !value.Contains("Fat.Türü", StringComparison.CurrentCultureIgnoreCase);

    private static bool IsUsefulInvoice(string? value) =>
        IsUseful(value)
        && !Regex.IsMatch(value!.Trim(), @"^(?:Seçiniz|Fat(?:ura)?\.?\s*(?:Türü|Grubu|Tipi)?)$",
            RegexOptions.IgnoreCase);

    private static string NormalizeInvoiceValue(string? value)
    {
        var normalized = NormalizeWhitespace(value);
        if (normalized.Length == 0) return string.Empty;
        normalized = Regex.Replace(
            normalized,
            @"^Fat(?:ura)?\.?(?:\s*(?:Türü|Grubu|Tipi))?\s*:?\s*",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        var simpleGroup = Regex.Match(
            normalized,
            @"\b(?<group>[A-ZÇĞİÖŞÜ])\s*Grubu\b",
            RegexOptions.IgnoreCase);
        if (simpleGroup.Success)
            return $"{simpleGroup.Groups["group"].Value.ToUpperInvariant()} Grubu";
        return MedulaPrescriptionListTypes.DisplayGroupName(normalized);
    }

    private static bool LooksLikeHeader(string? value) =>
        Regex.IsMatch(value?.Trim() ?? string.Empty,
            @"^(?:Reçete\s*No(?:su)?|Karekod\s+Durumu|Kapsam|Hasta\s+Türü|Fat(?:ura)?\.?\s*(?:Türü|Grubu|Tipi)?)\s*:?$",
            RegexOptions.IgnoreCase);

    private static string FirstUseful(string first, string second) =>
        IsUseful(first) ? first : second;

    [GeneratedRegex(
        @"(?:^|\s)Reçete\s*No(?:su)?\s*:?\s*" +
        @"(?<number>[A-Z0-9-]{4,})\s+" +
        @"Karekod\s+Durumu\s*:?\s*(?<qr>.+?)\s+" +
        @"Kapsam\s*:?\s*(?<coverage>.+?)\s+" +
        @"(?<insurance>Çalışan|Emekli)\s+" +
        @"(?:Fat\.?\s*Türü|Fatura\s*(?:Türü|Grubu|Tipi))\s*:?\s*" +
        @"(?<invoice>[A-ZÇĞİÖŞÜ])\s*Grubu\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SavedPrescriptionSummaryRegex();
}
