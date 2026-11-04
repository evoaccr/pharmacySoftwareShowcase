using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceParty(
    string Name,
    string Address,
    string Phone,
    string Fax,
    string Email,
    string Website,
    string TaxOffice,
    string Vkn,
    string Tckn,
    string MersisNo,
    string TradeRegistryNo,
    string Gln,
    IReadOnlyDictionary<string, string> Identifiers);

public sealed record InvoiceDocumentLine(
    int LineNumber,
    string ItemCode,
    string Barcode,
    string Name,
    string Description,
    decimal? Quantity,
    string Unit,
    decimal? UnitPrice,
    decimal? VatRate,
    decimal? VatAmount,
    decimal? DiscountAmount,
    decimal? LineAmount,
    string Currency,
    IReadOnlyDictionary<string, string> Values);

public sealed record EczaciKartInvoiceDocument(
    EczaciKartInvoiceItem ListItem,
    InvoiceParty InvoiceFrom,
    InvoiceParty InvoiceTo,
    string InvoiceNumber,
    string Ettn,
    DateOnly? IssueDate,
    DateOnly? DueDate,
    string Scenario,
    string InvoiceType,
    string Currency,
    decimal? LineExtensionAmount,
    decimal? TaxExclusiveAmount,
    decimal? TaxAmount,
    decimal? TaxInclusiveAmount,
    decimal? PayableAmount,
    string Notes,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<InvoiceDocumentLine> Lines,
    string SourceHtml);

public static partial class EczaciKartInvoiceDocumentParser
{
    public static EczaciKartInvoiceDocument Parse(
        EczaciKartInvoiceItem item,
        EczaciKartInvoiceDetailTable table,
        string sourceHtml)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(table);
        var metadata = ReadMetadata(sourceHtml);
        var qr = ReadQrData(sourceHtml);
        foreach (var pair in qr) metadata.TryAdd(pair.Key, pair.Value);

        var from = ReadParty(sourceHtml, seller: true, item);
        var to = ReadParty(sourceHtml, seller: false, item);
        var parsedLines = table.Rows
            .Select((row, index) => ReadLine(table.Headers, row.Cells, index + 1))
            .ToArray();
        var lines = parsedLines.Where(IsContentLine).ToArray();
        var issueDate = ParseDate(First(qr, metadata, item.IssueDate, "tarih", "fatura tarihi", "düzenleme tarihi"));
        var dueDate = ParseDate(First(qr, metadata, string.Empty, "son ödeme tarihi", "vade tarihi"))
            ?? ReadDueDate(issueDate, parsedLines);
        var lineSum = Sum(lines.Select(line => line.LineAmount));
        var lineExtensionAmount = NormalizeAggregate(
            ParseDecimal(First(qr, metadata, string.Empty, "malhizmettoplam", "mal hizmet toplam tutarı")),
            lineSum) ?? lineSum;
        var lineTax = Sum(lines.Select(line => line.VatAmount));
        var taxAmount = NormalizeAggregate(
            ParseDecimal(First(qr, metadata, string.Empty, "hesaplanankdv(20.00)", "kdv tutarı", "vergi tutarı")),
            lineTax) ?? lineTax;
        var payableText = First(qr, metadata, string.Empty, "odenecek", "ödenecek tutar", "payableamount");
        var payableAmount = string.IsNullOrWhiteSpace(payableText)
            ? ParsePortalAmount(item.Amount)
            : ParseDecimal(payableText);
        payableAmount = NormalizeAggregate(
            payableAmount,
            lineExtensionAmount is { } extension && taxAmount is { } tax ? extension + tax : null);

        return new EczaciKartInvoiceDocument(
            item,
            from,
            to,
            First(qr, metadata, item.DocumentNumber, "no", "fatura no", "belge no"),
            First(qr, metadata, item.Ettn, "ettn", "uuid"),
            issueDate,
            dueDate,
            First(qr, metadata, item.Scenario, "senaryo", "scenario"),
            First(qr, metadata, string.Empty, "tip", "fatura tipi", "invoice type"),
            First(qr, metadata, item.Currency, "parabirimi", "para birimi", "currency"),
            lineExtensionAmount,
            ParseDecimal(First(qr, metadata, string.Empty, "vergi hariç toplam", "taxexclusiveamount")),
            taxAmount,
            ParseDecimal(First(qr, metadata, string.Empty, "vergidahil", "vergi dahil toplam", "taxinclusiveamount")),
            payableAmount,
            ReadElementText(sourceHtml, "notlar"),
            metadata,
            lines,
            sourceHtml ?? string.Empty);
    }

    private static InvoiceDocumentLine ReadLine(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> cells,
        int fallbackLineNumber)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headers.Count; index++)
            values[headers[index]] = index < cells.Count ? cells[index] : string.Empty;

        var lineNumber = int.TryParse(Find(values, "sıra", "satır no"), out var parsedLine) ? parsedLine : fallbackLineNumber;
        var name = Find(values, "ürün adı", "mal hizmet", "ürün", "hizmet");
        var description = FindExact(values, "açıklama", "description");
        var vatRate = ParseDecimal(FindExact(values, "kdv%", "kdv oranı", "kdv oran", "kdv"));
        var lineAmount = ParseDecimal(FindExact(values, "net tutar", "tutar(tl)", "mal hizmet tutarı", "mal hizmet tutar", "satır tutarı", "satır tutar", "toplam"));
        var vatAmount = ParseDecimal(FindExact(values, "kdv tutarı", "kdv tutar"));
        if (vatAmount is null && lineAmount is { } amount && vatRate is { } rate)
            vatAmount = decimal.Round(amount * rate / 100m, 4, MidpointRounding.AwayFromZero);
        return new InvoiceDocumentLine(
            lineNumber,
            FindExact(values, "mal hizmet kodu", "ürün kodu", "stok kodu", "item code"),
            FindExact(values, "barkod", "gtin"),
            name,
            description,
            ParseQuantity(FindExact(values, "miktar", "adet")),
            FindExact(values, "birim", "ölçü birimi", "unit"),
            ParseDecimal(FindExact(values, "birim satış fiyatı(tl)", "birim satış fiyatı", "isk.sonrası birim fiyatı", "birim fiyat", "price")),
            vatRate,
            vatAmount,
            ParseDecimal(FindExact(values, "iskontolar", "iskonto", "indirim tutarı", "indirim tutar")),
            lineAmount,
            FindExact(values, "para birimi", "döviz", "currency"),
            values);
    }

    private static bool IsContentLine(InvoiceDocumentLine line) =>
        !string.IsNullOrWhiteSpace(line.ItemCode)
        || !string.IsNullOrWhiteSpace(line.Barcode)
        || !string.IsNullOrWhiteSpace(line.Name)
        || !string.IsNullOrWhiteSpace(line.Description)
        || line.Quantity is not null
        || line.UnitPrice is not null
        || line.LineAmount is not null;

    private static InvoiceParty ReadParty(string html, bool seller, EczaciKartInvoiceItem item)
    {
        var pattern = seller
            ? "id=[\\\"']AccountingSupplierParty[\\\"'][\\s\\S]*?(?=<[^>]+class=[\\\"'][^\\\"']*alici)"
            : "class=[\\\"'][^\\\"']*alici[^\\\"']*[\\\"'][\\s\\S]*?(?=<[^>]+id=[\\\"']ETTN[\\\"'])";
        var block = Regex.Match(html, pattern, RegexOptions.IgnoreCase).Value;
        var identifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(block, "class=[\\\"'][^\\\"']*partyID[^\\\"']*[\\\"'][^>]*>([\\s\\S]*?)</", RegexOptions.IgnoreCase))
        {
            var text = Clean(match.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var separator = text.IndexOf(':');
            var key = separator > 0 ? text[..separator].Trim() : $"Kimlik {identifiers.Count + 1}";
            var value = separator > 0 ? text[(separator + 1)..].Trim() : text;
            if (!string.IsNullOrWhiteSpace(value)) identifiers[key] = value;
        }

        string Identifier(params string[] names) => identifiers
            .FirstOrDefault(pair => names.Any(name => Normalize(pair.Key).Contains(Normalize(name), StringComparison.Ordinal))).Value ?? string.Empty;

        var party = new InvoiceParty(
            ReadClassText(block, "partyName"),
            ReadClassText(block, "addres"),
            ReadClassText(block, "tel"),
            ReadClassText(block, "fax"),
            ReadClassText(block, "eMail"),
            ReadClassText(block, "WebsiteURI"),
            ReadClassText(block, "taxOffice"),
            Identifier("VKN", "Vergi Kimlik"),
            Identifier("TCKN", "T.C."),
            Identifier("MERSİS", "MERSIS"),
            Identifier("Ticaret Sicil"),
            Identifier("GLN"),
            identifiers);
        if (!string.IsNullOrWhiteSpace(party.Name)) return party;
        return ReadAlternateParty(html, seller, item);
    }

    private static InvoiceParty ReadAlternateParty(string html, bool seller, EczaciKartInvoiceItem item)
    {
        var candidates = Regex.Matches(html ?? string.Empty, "<table\\b[^>]*>([\\s\\S]*?)</table>", RegexOptions.IgnoreCase)
            .Select(match => Clean(match.Groups[1].Value))
            .Where(text => text.Length is > 15 and < 2500)
            .ToArray();
        var text = seller
            ? candidates.FirstOrDefault(value => Regex.IsMatch(value, "\\bVKN\\s*:", RegexOptions.IgnoreCase)
                && Regex.IsMatch(value, "VERG[Iİ] DA[Iİ]RES[Iİ]", RegexOptions.IgnoreCase))
            : candidates.FirstOrDefault(value => Regex.IsMatch(value, "\\bTCKN\\s*:", RegexOptions.IgnoreCase)
                || value.StartsWith("SAYIN ", StringComparison.OrdinalIgnoreCase));
        text ??= string.Empty;

        var isCounterparty = (item.TypeKey == "incoming-invoice" && seller)
            || (item.TypeKey != "incoming-invoice" && !seller);
        var name = isCounterparty && !string.IsNullOrWhiteSpace(item.CounterpartyName)
            ? item.CounterpartyName
            : ReadLeadingPartyName(text);
        var identifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vkn = ReadLabeledValue(text, "VKN");
        var tckn = ReadLabeledValue(text, "TCKN");
        if (!string.IsNullOrWhiteSpace(vkn)) identifiers["VKN"] = vkn;
        if (!string.IsNullOrWhiteSpace(tckn)) identifiers["TCKN"] = tckn;
        return new InvoiceParty(
            name,
            text,
            ReadLabeledValue(text, "Tel"),
            ReadLabeledValue(text, "Fax"),
            Regex.Match(text, "[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}", RegexOptions.IgnoreCase).Value,
            Regex.Match(text, "(?:https?://|www\\.)\\S+", RegexOptions.IgnoreCase).Value,
            ReadLabeledValue(text, "Vergi Dairesi"),
            vkn,
            tckn,
            ReadLabeledValue(text, "MERSİS"),
            ReadLabeledValue(text, "Sicil No"),
            ReadLabeledValue(text, "GLN"),
            identifiers);
    }

    private static string ReadLeadingPartyName(string text)
    {
        var value = Regex.Replace(text, "^SAYIN\\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        var boundary = Regex.Match(value, "\\s+(?:[A-ZÇĞİÖŞÜ]+\\s+)?(?:MAH(?:ALLES[Iİ])?|CAD(?:DES[Iİ])?|SOK(?:AK)?|BULVAR[Iİ]?)\\b", RegexOptions.IgnoreCase);
        if (boundary.Success) value = value[..boundary.Index];
        return value.Length <= 160 ? value : value[..160].Trim();
    }

    private static string ReadLabeledValue(string text, string label)
    {
        var match = Regex.Match(text, $"{Regex.Escape(label)}\\s*:\\s*(.+?)(?=\\s+(?:Tel|Fax|E-?Posta|Vergi Dairesi|VKN|TCKN|MERS[Iİ]S|Sicil No|GLN)\\s*:|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static Dictionary<string, string> ReadMetadata(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in Regex.Matches(html ?? string.Empty, "<tr\\b[^>]*>([\\s\\S]*?)</tr>", RegexOptions.IgnoreCase))
        {
            var cells = Regex.Matches(row.Groups[1].Value, "<t[hd]\\b[^>]*>([\\s\\S]*?)</t[hd]>", RegexOptions.IgnoreCase)
                .Select(match => Clean(match.Groups[1].Value)).Where(value => value.Length > 0).ToArray();
            if (cells.Length < 2 || cells[0].Length > 100) continue;
            Add(result, cells[0].TrimEnd(' ', ':'), string.Join(" | ", cells.Skip(1)));
        }
        return result;
    }

    private static Dictionary<string, string> ReadQrData(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var decoded = WebUtility.HtmlDecode(html ?? string.Empty).Replace("&quot;", "\"");
        var match = Regex.Match(decoded, "\\{[^{}]*[\\\"']vkntckn[\\\"'][^{}]*\\}", RegexOptions.IgnoreCase);
        if (!match.Success) return result;
        var candidate = match.Value.Replace('\'', '"');
        try
        {
            using var document = JsonDocument.Parse(candidate);
            foreach (var property in document.RootElement.EnumerateObject())
                result[property.Name] = property.Value.ToString().Trim();
        }
        catch (JsonException)
        {
            // Invoice visual templates occasionally contain a non-JSON QR fallback.
        }
        return result;
    }

    private static string ReadClassText(string block, string className)
    {
        var match = Regex.Match(block, $"class=[\\\"'][^\\\"']*{Regex.Escape(className)}[^\\\"']*[\\\"'][^>]*>([\\s\\S]*?)</", RegexOptions.IgnoreCase);
        return match.Success ? Clean(match.Groups[1].Value) : string.Empty;
    }

    private static string ReadElementText(string html, string id)
    {
        var match = Regex.Match(html ?? string.Empty, $"id=[\\\"']{Regex.Escape(id)}[\\\"'][^>]*>([\\s\\S]*?)</(?:div|td|section)>", RegexOptions.IgnoreCase);
        return match.Success ? Clean(match.Groups[1].Value) : string.Empty;
    }

    private static string Find(IReadOnlyDictionary<string, string> values, params string[] needles)
    {
        foreach (var needle in needles)
        {
            var found = values.FirstOrDefault(pair => Normalize(pair.Key).Contains(Normalize(needle), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(found.Value)) return found.Value;
        }
        return string.Empty;
    }

    private static string FindExact(IReadOnlyDictionary<string, string> values, params string[] names)
    {
        foreach (var name in names)
        {
            var normalized = Normalize(name);
            var found = values.FirstOrDefault(pair => Normalize(pair.Key).Equals(normalized, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(found.Value)) return found.Value;
        }
        return string.Empty;
    }

    private static string First(
        IReadOnlyDictionary<string, string> qr,
        IReadOnlyDictionary<string, string> metadata,
        string fallback,
        params string[] keys)
    {
        var found = Find(qr, keys);
        if (!string.IsNullOrWhiteSpace(found)) return found;
        found = Find(metadata, keys);
        return string.IsNullOrWhiteSpace(found) ? fallback : found;
    }

    private static decimal? ParseDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = Regex.Replace(value, "[^0-9,.-]", string.Empty);
        if (decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.GetCultureInfo("tr-TR"), out var number)) return number;
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static decimal? ParseQuantity(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            var quantities = parts.Select(ParseDecimal).ToArray();
            if (quantities.All(quantity => quantity is not null))
                return quantities.Sum(quantity => quantity!.Value);
        }
        return ParseDecimal(value);
    }

    private static decimal? ParsePortalAmount(string value)
    {
        var amount = ParseDecimal(value);
        if (amount is null) return null;
        return value.Contains('.') || value.Contains(',') ? amount : amount / 100m;
    }

    private static decimal? NormalizeAggregate(decimal? candidate, decimal? expected)
    {
        if (candidate is null || expected is null || expected == 0) return candidate;
        var ratio = decimal.Abs(candidate.Value / expected.Value);
        return ratio is >= 99m and <= 101m ? expected : candidate;
    }

    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static DateOnly? ReadDueDate(DateOnly? issueDate, IEnumerable<InvoiceDocumentLine> lines)
    {
        foreach (var line in lines)
        {
            foreach (var value in line.Values.Values)
            {
                var normalized = Normalize(value);
                var match = Regex.Match(normalized,
                    "(?:VADE\\s*TARIHI|DEME\\s*TAR)[^0-9]{0,30}([0-9]{2}[.][0-9]{2}[.][0-9]{4})");
                if (match.Success
                    && ParseDate(match.Groups[1].Value) is { } date
                    && (issueDate is null || date >= issueDate.Value))
                    return date;
            }

            if (issueDate is null) continue;
            var dayText = line.Values.FirstOrDefault(pair =>
                Normalize(pair.Key).Equals("VADE GUN", StringComparison.Ordinal)).Value;
            if (int.TryParse(dayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days >= 0)
                return issueDate.Value.AddDays(days);
        }
        return null;
    }

    private static DateOnly? ParseDate(string value)
    {
        if (DateOnly.TryParse(value, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.AllowWhiteSpaces, out var date)) return date;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? DateOnly.FromDateTime(parsed.DateTime)
            : null;
    }

    private static string Clean(string value) => Regex.Replace(
        WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<[^>]+>", " ")),
        "\\s+", " ").Trim();

    private static string Normalize(string value) => value.Trim().ToUpperInvariant()
        .Replace('İ', 'I').Replace('ı', 'I').Replace('Ş', 'S').Replace('Ğ', 'G').Replace('Ü', 'U').Replace('Ö', 'O').Replace('Ç', 'C');

    private static void Add(IDictionary<string, string> values, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return;
        if (!values.TryGetValue(key, out var existing)) values[key] = value;
        else if (!existing.Contains(value, StringComparison.OrdinalIgnoreCase)) values[key] = $"{existing} | {value}";
    }
}
