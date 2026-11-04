using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using pharmacyApp.Modules.Invoices;

namespace pharmacyApp.Modules.Its;

public sealed record ItsPtsTransfer(string SourceGln, string DestinationGln, long TransferId, DateTimeOffset? TransferDate);

public sealed record ItsStakeholder(string Gln, string CompanyName);

public sealed record ItsPtsSearchRequest(
    [property: JsonPropertyName("sourceGln")] string SourceGln,
    [property: JsonPropertyName("destinationGln")] string DestinationGln,
    [property: JsonPropertyName("bringNotReceivedTransferInfo")] int BringNotReceivedTransferInfo,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("endDate")] string EndDate);

public sealed record ItsPtsPackageResult(long TransferId, byte[] PackageBytes);

public sealed record ItsPtsPackageParseResult(
    IReadOnlyList<InvoiceTraceabilityCode> Codes,
    IReadOnlyList<string> TextDocuments);

public static class ItsPtsPackageParser
{
    public static ItsPtsPackageParseResult Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var texts = ReadTexts(bytes);
        var codes = new Dictionary<string, InvoiceTraceabilityCode>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            foreach (var code in InvoiceTraceabilityParser.Parse([text]).Codes)
                codes.TryAdd(code.QrCode, code);
            TryReadStructuredXml(text, codes);
        }
        return new ItsPtsPackageParseResult(codes.Values.ToArray(), texts);
    }

    public static bool MatchesInvoice(ItsPtsPackageParseResult package, string invoiceNumber, string ettn)
    {
        ArgumentNullException.ThrowIfNull(package);
        var keys = new[] { invoiceNumber, ettn }
            .Select(NormalizeDocumentKey)
            .Where(value => value.Length >= 6)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return keys.Length > 0 && package.TextDocuments
            .SelectMany(ReadDocumentReferences)
            .Any(reference => keys.Contains(reference, StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> ReadTexts(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var texts = new List<string>();
            foreach (var entry in archive.Entries.Where(entry => entry.Length > 0))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
                texts.Add(entry.FullName + "\n" + reader.ReadToEnd());
            }
            return texts;
        }
        return [Encoding.UTF8.GetString(bytes)];
    }

    private static void TryReadStructuredXml(
        string text,
        IDictionary<string, InvoiceTraceabilityCode> codes)
    {
        var xmlStart = text.IndexOf('<');
        if (xmlStart < 0) return;
        XDocument document;
        try { document = XDocument.Parse(text[xmlStart..]); }
        catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException) { return; }

        foreach (var product in document.Descendants().Where(element =>
                     element.Elements().Any(child => Is(child, "gtin"))))
        {
            var gtin = Value(product, "gtin");
            var batch = Value(product, "bn", "batch", "batchNumber", "lot");
            var expiry = Value(product, "xd", "expiry", "expiryDate");
            var serials = product.Descendants()
                .Where(element => Is(element, "sn", "serial", "serialNumber"))
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var serial in serials)
                if (TryCreate(gtin, batch, expiry, serial, out var code))
                    codes.TryAdd(code.QrCode, code);
        }
    }

    private static bool TryCreate(
        string gtin,
        string batch,
        string expiry,
        string serial,
        out InvoiceTraceabilityCode code)
    {
        code = null!;
        gtin = Digits(gtin);
        if (gtin.Length is < 8 or > 14 || batch.Length == 0 || serial.Length == 0) return false;
        if (!TryReadExpiry(expiry, out var expiryDate)) return false;
        var gtin14 = gtin.PadLeft(14, '0');
        var expiryText = expiryDate.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var qr = $"(01){gtin14}(17){expiryText}(10){batch.Trim()}(21){serial.Trim()}";
        code = new InvoiceTraceabilityCode(
            qr,
            gtin14,
            gtin14[0] == '0' ? gtin14[1..] : gtin14,
            serial.Trim(),
            batch.Trim(),
            expiryDate,
            $"PTS: GTIN={gtin14} BN={batch.Trim()} XD={expiryText} SN={serial.Trim()}");
        return true;
    }

    private static bool TryReadExpiry(string value, out DateOnly result)
    {
        value = value.Trim();
        var formats = new[] { "yyyy-MM-dd", "yyyyMMdd", "yyMMdd", "dd.MM.yyyy" };
        return DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out result);
    }

    private static string Value(XElement element, params string[] names) =>
        element.Elements().FirstOrDefault(child => Is(child, names))?.Value.Trim()
        ?? element.Descendants().FirstOrDefault(child => Is(child, names))?.Value.Trim()
        ?? string.Empty;

    private static bool Is(XElement element, params string[] names) =>
        names.Any(name => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeDocumentKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static IEnumerable<string> ReadDocumentReferences(string text)
    {
        var firstNewline = text.IndexOf('\n');
        if (firstNewline > 0)
        {
            var fileName = Path.GetFileNameWithoutExtension(text[..firstNewline].Trim());
            var normalizedFileName = NormalizeDocumentKey(fileName);
            if (normalizedFileName.Length >= 6) yield return normalizedFileName;
        }
        var xmlStart = text.IndexOf('<');
        if (xmlStart < 0) yield break;
        XDocument document;
        try { document = XDocument.Parse(text[xmlStart..]); }
        catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException) { yield break; }
        foreach (var element in document.Descendants().Where(element => !element.HasElements))
        {
            var name = element.Name.LocalName;
            if (!name.Contains("invoice", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("document", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("uuid", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("ettn", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("reference", StringComparison.OrdinalIgnoreCase))
                continue;
            var normalized = NormalizeDocumentKey(element.Value);
            if (normalized.Length >= 6) yield return normalized;
        }
    }

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
}
