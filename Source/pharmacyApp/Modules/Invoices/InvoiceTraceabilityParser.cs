using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceTraceabilityCode(
    string QrCode,
    string Gtin,
    string Barcode,
    string SerialNumber,
    string BatchNumber,
    DateOnly ExpiryDate,
    string RawCode);

public sealed record InvoiceTraceabilityParseResult(
    IReadOnlyList<InvoiceTraceabilityCode> Codes,
    int CandidateCount,
    IReadOnlyList<string> Errors)
{
    public bool HasTraceabilityMarkers => CandidateCount > 0;
    public bool IsComplete => CandidateCount == Codes.Count && Errors.Count == 0;
}

public static partial class InvoiceTraceabilityParser
{
    [GeneratedRegex(
        @"(?:\(\s*GTIN\s*\)|GTIN\s*[:=])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CandidateStartRegex();

    [GeneratedRegex(
        @"(?:\(\s*(?<key>GTIN|BN|BATCH|LOT|SN|SERIAL|XD|EXP|EXPIRY)\s*\)|(?<key>GTIN|BN|BATCH|LOT|SN|SERIAL|XD|EXP|EXPIRY)\s*[:=])\s*(?<value>.*?)(?=(?:\(\s*(?:GTIN|BN|BATCH|LOT|SN|SERIAL|XD|EXP|EXPIRY)\s*\)|(?:GTIN|BN|BATCH|LOT|SN|SERIAL|XD|EXP|EXPIRY)\s*[:=])|\s*\||$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(
        @"\(\s*01\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AiCandidateStartRegex();

    [GeneratedRegex(
        @"\(\s*(?<key>01|17|10|21)\s*\)\s*(?<value>.*?)(?=\(\s*(?:01|17|10|21)\s*\)|\s*\||$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AiFieldRegex();

    public static InvoiceTraceabilityParseResult Parse(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var source = string.Join(" | ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        var starts = CandidateStartRegex().Matches(source);
        var aiStarts = AiCandidateStartRegex().Matches(source);
        if (starts.Count == 0 && aiStarts.Count == 0)
            return new InvoiceTraceabilityParseResult([], 0, []);

        var codes = new List<InvoiceTraceabilityCode>();
        var errors = new List<string>();
        var candidateCount = 0;
        var seenBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index].Index;
            var end = index + 1 < starts.Count ? starts[index + 1].Index : source.Length;
            var block = source[start..end].Trim().TrimEnd('|').Trim();
            var blockKey = Regex.Replace(block, @"\s+", string.Empty);
            if (!seenBlocks.Add(blockKey)) continue;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match field in FieldRegex().Matches(block))
            {
                var key = CanonicalKey(field.Groups["key"].Value);
                var value = CleanVariableField(field.Groups["value"].Value);
                if (value.Length > 0 && !fields.ContainsKey(key)) fields.Add(key, value);
            }
            AddCandidate(fields, block, seen, codes, errors, ref candidateCount);
        }

        for (var index = 0; index < aiStarts.Count; index++)
        {
            var start = aiStarts[index].Index;
            var end = index + 1 < aiStarts.Count ? aiStarts[index + 1].Index : source.Length;
            var block = source[start..end].Trim().TrimEnd('|').Trim();
            var blockKey = Regex.Replace(block, @"\s+", string.Empty);
            if (!seenBlocks.Add(blockKey)) continue;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match field in AiFieldRegex().Matches(block))
            {
                var key = CanonicalKey(field.Groups["key"].Value);
                var value = CleanVariableField(field.Groups["value"].Value);
                if (value.Length > 0 && !fields.ContainsKey(key)) fields.Add(key, value);
            }
            AddCandidate(fields, block, seen, codes, errors, ref candidateCount);
        }

        if (codes.Count < candidateCount && errors.Count == 0)
            errors.Add($"{candidateCount - codes.Count:N0} karekod ayrıştırılamadı.");
        return new InvoiceTraceabilityParseResult(codes, candidateCount, errors);
    }

    public static InvoiceTraceabilityParseResult Parse(IReadOnlyDictionary<string, string> values) =>
        Parse(values.Values);

    public static string BuildTrackingWarning(
        decimal? quantity,
        InvoiceTraceabilityParseResult parsed,
        int storedQrCount)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentOutOfRangeException.ThrowIfNegative(storedQrCount);
        if (!parsed.HasTraceabilityMarkers && storedQrCount == 0)
            return string.Empty;

        var messages = parsed.Errors.Distinct(StringComparer.Ordinal).ToList();
        if (quantity is null || quantity <= 0 || decimal.Truncate(quantity.Value) != quantity.Value)
            messages.Add("Karekodlu satırın fatura miktarı pozitif tam sayı değil.");
        else if (storedQrCount != decimal.ToInt32(quantity.Value))
            messages.Add($"Fatura miktarı {quantity.Value:N0}, saklanan tekil karekod sayısı {storedQrCount:N0}.");
        return string.Join(" ", messages.Distinct(StringComparer.Ordinal));
    }

    private static bool TryParseExpiryDate(string value, out DateOnly expiryDate)
    {
        expiryDate = default;
        if (value.Length != 6 ||
            !int.TryParse(value.AsSpan(0, 2), out var year) ||
            !int.TryParse(value.AsSpan(2, 2), out var month) ||
            !int.TryParse(value.AsSpan(4, 2), out var day))
            return false;

        try
        {
            expiryDate = day == 0
                ? new DateOnly(2000 + year, month, DateTime.DaysInMonth(2000 + year, month))
                : new DateOnly(2000 + year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static void AddCandidate(
        IReadOnlyDictionary<string, string> fields,
        string block,
        ISet<string> seen,
        ICollection<InvoiceTraceabilityCode> codes,
        ICollection<string> errors,
        ref int candidateCount)
    {
        var gtin = fields.GetValueOrDefault("GTIN", string.Empty);
        var batch = fields.GetValueOrDefault("BN", string.Empty);
        var serial = fields.GetValueOrDefault("SN", string.Empty);
        var expiryText = fields.GetValueOrDefault("XD", string.Empty);
        if (!Regex.IsMatch(gtin, "^[0-9]{8,14}$", RegexOptions.CultureInvariant)
            || batch.Length == 0 || serial.Length == 0 || expiryText.Length != 6)
        {
            candidateCount++;
            errors.Add("GTIN, parti, seri veya son kullanma tarihi eksik bir karekod bulundu.");
            return;
        }
        if (!TryParseExpiryDate(expiryText, out var expiryDate))
        {
            candidateCount++;
            errors.Add($"{serial} seri numaralı karekodun son kullanma tarihi geçersiz.");
            return;
        }

        var gtin14 = gtin.PadLeft(14, '0');
        var qrCode = $"(01){gtin14}(17){expiryText}(10){batch}(21){serial}";
        if (!seen.Add(qrCode)) return;

        candidateCount++;
        var barcode = gtin14[0] == '0' ? gtin14[1..] : gtin14;
        codes.Add(new InvoiceTraceabilityCode(
            qrCode, gtin14, barcode, serial, batch, expiryDate, block));
    }

    private static string CanonicalKey(string value) => value.ToUpperInvariant() switch
    {
        "01" => "GTIN",
        "10" or "BATCH" or "LOT" => "BN",
        "21" or "SERIAL" => "SN",
        "17" or "EXP" or "EXPIRY" => "XD",
        var key => key
    };

    private static string CleanVariableField(string value) => value.Trim().Trim('|').Trim();
}
