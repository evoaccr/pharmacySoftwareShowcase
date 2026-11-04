using System.Globalization;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoicePtsEnrichmentResult(
    EczaciKartInvoiceDocument Document,
    bool Attempted,
    int MatchedPackages,
    int AddedQrCodes,
    string Warning);

public sealed class InvoicePtsEnrichmentService(IItsPtsClient? ptsClient = null)
{
    private readonly IItsPtsClient client = ptsClient ?? new ItsApiClient();
    private readonly Dictionary<int, IReadOnlyList<ItsStakeholder>> stakeholderCache = [];

    public async Task<InvoicePtsEnrichmentResult> EnrichAsync(
        EczaciKartInvoiceDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = ApplicationSettingsStore.Current;
        if (document.ListItem.TypeKey != "incoming-invoice" || document.IssueDate is null)
            return new InvoicePtsEnrichmentResult(document, false, 0, 0, string.Empty);
        if (string.IsNullOrWhiteSpace(settings.ItsUsername)
            || string.IsNullOrWhiteSpace(settings.ItsPassword)
            || !IsGln(settings.ItsPharmacyGln))
            return new InvoicePtsEnrichmentResult(
                document, false, 0, 0,
                "İTS kimlik bilgileri veya eczane GLN'i eksik olduğu için PTS tamamlama atlandı.");

        try
        {
            var credentials = new ItsCredentials(settings.ItsUsername, settings.ItsPassword);
            var sourceGlns = await ResolveSourceGlnsAsync(document.InvoiceFrom, credentials, cancellationToken);
            if (sourceGlns.Count == 0)
                return new InvoicePtsEnrichmentResult(
                    document, true, 0, 0,
                    "Gönderici bir İTS depo GLN'i ile eşleştirilemedi; fatura eksik işaretli bırakıldı.");

            var startDate = document.IssueDate.Value.AddDays(-3);
            var endDate = document.IssueDate.Value.AddDays(7);
            var matchedPackages = 0;
            var packageCodes = new Dictionary<string, InvoiceTraceabilityCode>(StringComparer.Ordinal);
            foreach (var sourceGln in sourceGlns)
            {
                var transfers = await client.SearchPtsTransfersAsync(
                    ItsEnvironment.Production,
                    credentials,
                    new ItsPtsSearchRequest(
                        sourceGln,
                        settings.ItsPharmacyGln.Trim(),
                        0,
                        startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    cancellationToken);
                foreach (var transfer in transfers)
                {
                    var package = await client.GetPtsPackageAsync(
                        ItsEnvironment.Production, credentials, transfer.TransferId, cancellationToken);
                    var parsed = ItsPtsPackageParser.Parse(package.PackageBytes);
                    if (!ItsPtsPackageParser.MatchesInvoice(parsed, document.InvoiceNumber, document.Ettn))
                        continue;
                    matchedPackages++;
                    foreach (var code in parsed.Codes) packageCodes.TryAdd(code.QrCode, code);
                }
            }

            var enriched = AddCodes(document, packageCodes.Values, out var added);
            var warning = matchedPackages == 0
                ? "Bu faturayı açıkça referanslayan PTS paketi bulunamadı; mevcut XML verisi korundu."
                : added == 0
                    ? "Eşleşen PTS paketi bulundu ancak fatura GTIN'leriyle eşleşen ek seri içermiyordu."
                    : string.Empty;
            return new InvoicePtsEnrichmentResult(enriched, true, matchedPackages, added, warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new InvoicePtsEnrichmentResult(
                document, true, 0, 0,
                $"PTS tamamlama fatura aktarımını durdurmadan başarısız oldu: {exception.Message}");
        }
    }

    public static EczaciKartInvoiceDocument AddCodes(
        EczaciKartInvoiceDocument document,
        IEnumerable<InvoiceTraceabilityCode> candidates,
        out int addedCount)
    {
        var remaining = candidates
            .GroupBy(code => code.Gtin, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Queue<InvoiceTraceabilityCode>(group.DistinctBy(code => code.QrCode)),
                StringComparer.Ordinal);
        var lines = new List<InvoiceDocumentLine>(document.Lines.Count);
        addedCount = 0;
        foreach (var line in document.Lines)
        {
            var existing = InvoiceTraceabilityParser.Parse(line.Values).Codes
                .Select(code => code.QrCode)
                .ToHashSet(StringComparer.Ordinal);
            var gtins = ReadLineGtins(line).ToArray();
            var capacity = line.Quantity is > 0 && decimal.Truncate(line.Quantity.Value) == line.Quantity.Value
                ? Math.Max(0, decimal.ToInt32(line.Quantity.Value) - existing.Count)
                : 0;
            if (capacity == 0 || gtins.Length == 0)
            {
                lines.Add(line);
                continue;
            }

            var values = new Dictionary<string, string>(line.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var gtin in gtins)
            {
                if (!remaining.TryGetValue(gtin, out var queue)) continue;
                while (capacity > 0 && queue.Count > 0)
                {
                    var code = queue.Dequeue();
                    if (!existing.Add(code.QrCode)) continue;
                    var key = $"PTS Karekod {existing.Count:N0}";
                    while (values.ContainsKey(key)) key += "_";
                    values[key] = code.RawCode;
                    capacity--;
                    addedCount++;
                }
            }
            lines.Add(line with { Values = values });
        }
        return document with { Lines = lines };
    }

    private async Task<IReadOnlyList<string>> ResolveSourceGlnsAsync(
        InvoiceParty supplier,
        ItsCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (IsGln(supplier.Gln)) return [supplier.Gln.Trim()];
        var cityPlate = ReadCityPlate(supplier.Address);
        if (cityPlate == 0) return [];
        if (!stakeholderCache.TryGetValue(cityPlate, out var stakeholders))
        {
            stakeholders = await client.GetStakeholdersAsync(
                ItsEnvironment.Production, credentials, cityPlate, cancellationToken);
            stakeholderCache[cityPlate] = stakeholders;
        }
        var supplierTokens = SignificantTokens(supplier.Name);
        return stakeholders
            .Where(stakeholder => IsSupplierNameMatch(supplier.Name, supplierTokens, stakeholder.CompanyName))
            .Select(stakeholder => stakeholder.Gln)
            .Where(IsGln)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsSupplierNameMatch(
        string supplierName,
        IReadOnlySet<string> supplierTokens,
        string stakeholderName)
    {
        var left = Normalize(supplierName);
        var right = Normalize(stakeholderName);
        if (left.Length >= 8 && (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)))
            return true;
        var rightTokens = SignificantTokens(stakeholderName);
        return supplierTokens.Intersect(rightTokens, StringComparer.Ordinal).Count() >= 2;
    }

    private static IEnumerable<string> ReadLineGtins(InvoiceDocumentLine line)
    {
        foreach (var value in new[] { line.Barcode, line.ItemCode }.Concat(line.Values.Values))
        {
            var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            if (digits.Length is >= 8 and <= 14) yield return digits.PadLeft(14, '0');
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(value ?? string.Empty, "(?<!\\d)\\d{8,14}(?!\\d)"))
                yield return match.Value.PadLeft(14, '0');
        }
    }

    private static IReadOnlySet<string> SignificantTokens(string value) => Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length >= 3 && token is not "SANAYI" and not "TICARET" and not "LIMITED"
            and not "ANONIM" and not "SIRKETI" and not "ECZA" and not "DEPOSU")
        .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value)
    {
        var upper = value.ToUpperInvariant()
            .Replace('İ', 'I').Replace('Ş', 'S').Replace('Ğ', 'G')
            .Replace('Ü', 'U').Replace('Ö', 'O').Replace('Ç', 'C');
        return string.Join(' ', upper.Split().Select(token =>
            new string(token.Where(char.IsLetterOrDigit).ToArray())).Where(token => token.Length > 0));
    }

    private static int ReadCityPlate(string address)
    {
        var normalized = Normalize(address);
        foreach (var pair in CityPlates)
            if (normalized.Split(' ').Contains(pair.Key, StringComparer.Ordinal)) return pair.Value;
        return 0;
    }

    private static bool IsGln(string value) => value.Trim().Length == 13 && value.Trim().All(char.IsAsciiDigit);

    private static readonly IReadOnlyDictionary<string, int> CityPlates = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ADANA"]=1,["ADIYAMAN"]=2,["AFYONKARAHISAR"]=3,["AGRI"]=4,["AMASYA"]=5,["ANKARA"]=6,
        ["ANTALYA"]=7,["ARTVIN"]=8,["AYDIN"]=9,["BALIKESIR"]=10,["BILECIK"]=11,["BINGOL"]=12,
        ["BITLIS"]=13,["BOLU"]=14,["BURDUR"]=15,["BURSA"]=16,["CANAKKALE"]=17,["CANKIRI"]=18,
        ["CORUM"]=19,["DENIZLI"]=20,["DIYARBAKIR"]=21,["EDIRNE"]=22,["ELAZIG"]=23,["ERZINCAN"]=24,
        ["ERZURUM"]=25,["ESKISEHIR"]=26,["GAZIANTEP"]=27,["GIRESUN"]=28,["GUMUSHANE"]=29,
        ["HAKKARI"]=30,["HATAY"]=31,["ISPARTA"]=32,["MERSIN"]=33,["ISTANBUL"]=34,["IZMIR"]=35,
        ["KARS"]=36,["KASTAMONU"]=37,["KAYSERI"]=38,["KIRKLARELI"]=39,["KIRSEHIR"]=40,
        ["KOCAELI"]=41,["KONYA"]=42,["KUTAHYA"]=43,["MALATYA"]=44,["MANISA"]=45,
        ["KAHRAMANMARAS"]=46,["MARDIN"]=47,["MUGLA"]=48,["MUS"]=49,["NEVSEHIR"]=50,["NIGDE"]=51,
        ["ORDU"]=52,["RIZE"]=53,["SAKARYA"]=54,["SAMSUN"]=55,["SIIRT"]=56,["SINOP"]=57,
        ["SIVAS"]=58,["TEKIRDAG"]=59,["TOKAT"]=60,["TRABZON"]=61,["TUNCELI"]=62,["SANLIURFA"]=63,
        ["USAK"]=64,["VAN"]=65,["YOZGAT"]=66,["ZONGULDAK"]=67,["AKSARAY"]=68,["BAYBURT"]=69,
        ["KARAMAN"]=70,["KIRIKKALE"]=71,["BATMAN"]=72,["SIRNAK"]=73,["BARTIN"]=74,["ARDAHAN"]=75,
        ["IGDIR"]=76,["YALOVA"]=77,["KARABUK"]=78,["KILIS"]=79,["OSMANIYE"]=80,["DUZCE"]=81
    };
}
