using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Products;

public static class ProductCatalogFormatting
{
    public static string FormatPurchasePriceForClipboard(decimal? value) => value is null
        ? "—"
        : value.Value.ToString("0.00", CultureInfo.GetCultureInfo("tr-TR"));

    public static int? ParseStockBound(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
}

public sealed record ProductDetailPayload(
    IReadOnlyList<byte[]> Photos,
    byte[]? KubDocument,
    byte[]? KitDocument,
    IReadOnlyList<ProductWarningItem> Warnings,
    string? SutSummaryJson,
    string? MedulaInfoJson,
    string? MedulaDetailsJson);

public sealed record ProductWarningItem(string Header, string Description, byte[]? Icon);

public sealed record SutOverviewDetailItem(string Header, string Content);

public sealed record SutOverviewSection(
    string Header,
    string Status,
    string Content,
    IReadOnlyList<SutOverviewDetailItem>? Details = null)
{
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public IReadOnlyList<SutOverviewDetailItem> DetailItems => Details ?? [];
    public bool HasDetails => DetailItems.Count > 0;
    public bool IsTemporarilyVisible => !Header.StartsWith("MEDULA", StringComparison.OrdinalIgnoreCase);
    public bool IsUsableSutContent => IsTemporarilyVisible &&
        !string.Equals(Header, "SUT Özeti", StringComparison.OrdinalIgnoreCase);
}

public sealed record SutOverviewResult(string DateDisplay, IReadOnlyList<SutOverviewSection> Sections);

public sealed class QrCodeDisplayItem
{
    public string QrCode { get; init; } = string.Empty;
    public DateTime? ExpiryDate { get; init; }
    public decimal? PurchasePrice { get; init; }
    public DateTime? MovementDate { get; init; }
    public decimal? ExitPrice { get; init; }
    public decimal? Profit { get; init; }
    public string ExpiryDateDisplay => ExpiryDate?.ToString("dd.MM.yyyy") ?? "—";
    public string PurchasePriceDisplay => PurchasePrice?.ToString("N2") ?? "—";
    public string MovementDateDisplay => MovementDate?.ToString("dd.MM.yyyy HH:mm") ?? "—";
    public string ExitPriceDisplay => ExitPrice?.ToString("N2") ?? "—";
    public string ProfitDisplay => Profit?.ToString("N2") ?? "—";
}

public static class ProductWarningFormatter
{
    public static IReadOnlyList<ProductWarningItem> Parse(string? rawJson, string? iconCatalogJson = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return [];
        try
        {
            var iconCatalog = ParseIconCatalog(iconCatalogJson);
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("urunUyariIcerik", out var warnings) ||
                warnings.ValueKind != JsonValueKind.Array)
                return [];

            return warnings.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item =>
                {
                    var header = Value(item, "baslik") ?? "Ürün Uyarısı";
                    var sourceUrl = Value(item, "resim_url") ?? Value(item, "resim");
                    iconCatalog.TryGetValue(IconKey(header, sourceUrl), out var icon);
                    return new ProductWarningItem(
                        header,
                        Value(item, "aciklama") ?? "Açıklama bulunamadı.",
                        icon);
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Value(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyDictionary<string, byte[]> ParseIconCatalog(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return new Dictionary<string, byte[]>();
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.ValueKind != JsonValueKind.Array
                ? new Dictionary<string, byte[]>()
                : document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => new
                    {
                        Header = Value(item, "header"),
                        SourceUrl = Value(item, "source_url"),
                        Icon = Value(item, "icon_base64")
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Header) && !string.IsNullOrWhiteSpace(item.Icon))
                    .GroupBy(item => IconKey(item.Header!, item.SourceUrl), StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => Convert.FromBase64String(group.First().Icon!),
                        StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return new Dictionary<string, byte[]>();
        }
    }

    private static string IconKey(string header, string? sourceUrl) => $"{header}\n{sourceUrl}";
}

public static class SutOverviewFormatter
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ayaktan_max_kullanim"] = "Raporsuz Ayaktan Maksimum Kullanım",
        ["raporlu_max_kullanim"] = "Raporlu Ayaktan Maksimum Kullanım",
        ["yatan_max_kullanim"] = "Yatan Maksimum Kullanım",
        ["cinsiyet"] = "Cinsiyet",
        ["yas_araligi"] = "Yaş Aralığı",
        ["tedavi_semasi"] = "Tedavi Şeması",
        ["etkin_madde"] = "Etkin Madde",
        ["etkin_madde_aciklama"] = "Etkin Madde Açıklaması",
        ["tek_doz_miktari"] = "Tek Doz Miktarı",
        ["tek_doz_birimi"] = "Tek Doz Birimi",
        ["ambalaj_miktari"] = "Ambalaj Miktarı",
        ["ambalaj_birimi"] = "Ambalaj Birimi",
        ["takipli_ilac_sayisi"] = "Takipli İlaç Sayısı",
        ["medula_tani_kod"] = "Tanı Kodları",
        ["ayaktanRaporsuz"] = "Ayaktan Raporsuz",
        ["ayaktanRaporlu"] = "Ayaktan Raporlu",
        ["saglikKurulu"] = "Sağlık Kurulu",
        ["uzmanHekim"] = "Uzman Hekim",
        ["raporlu"] = "Raporlu",
        ["raporsuz"] = "Raporsuz",
        ["yatan"] = "Yatan",
        ["receteTeshis"] = "Reçete Teşhis",
        ["receteTani"] = "Reçete Tanı",
        ["raporTaniArr"] = "Rapor Tanıları",
        ["taniKodu"] = "Tanı Kodu",
        ["iliskiliICD10"] = "İlişkili ICD-10",
        ["iliskiliICD10Odenmez"] = "Ödenmeyen İlişkili ICD-10",
        ["sgkem"] = "SGK Ek Madde",
        ["aciklama"] = "Açıklama",
        ["rapor_brans"] = "Rapor Branşı",
        ["rapor_suresi"] = "Rapor Süresi",
        ["rapor_tesis_tur"] = "Rapor Tesis Türü",
        ["rapor_tur"] = "Rapor Türü",
        ["recete_brans"] = "Reçete Branşı",
        ["tedavisinde"] = "Tedavisinde",
        ["odenme_durumu"] = "Ödenme Durumu",
        ["odenmeDurumu"] = "Ödenme Durumu",
        ["odenmeDurumuId"] = "Ödenme Durumu Kimliği",
        ["tedavi_tip"] = "Tedavi Tipi",
        ["tedaviTip"] = "Tedavi Tipi",
        ["tedaviTipId"] = "Tedavi Tipi Kimliği"
    };

    public static SutOverviewResult Parse(string? sutSummaryJson, string? medulaInfoJson, string? medulaDetailsJson)
    {
        var sections = new List<SutOverviewSection>
        {
            ParseMedulaInfo(medulaInfoJson),
            ParseGenericDocument("MEDULA Detay Bilgileri", medulaDetailsJson)
        };
        var dateDisplay = string.Empty;

        if (string.IsNullOrWhiteSpace(sutSummaryJson))
        {
            sections.Add(new SutOverviewSection("SUT Özeti", "Bilgi alınamadı", "Bilgi alınamadı."));
            return new SutOverviewResult(dateDisplay, sections);
        }

        try
        {
            using var document = JsonDocument.Parse(sutSummaryJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("as_of_date", out var date) && IsPresent(date))
                dateDisplay = $"Bilgi tarihi: {FormatDate(date.ToString())}";

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sections", out var summarySections) ||
                summarySections.ValueKind != JsonValueKind.Object ||
                !summarySections.EnumerateObject().Any())
            {
                sections.Add(new SutOverviewSection("SUT Özeti", "Bilgi bulunamadı", "Bilgi bulunamadı."));
                return new SutOverviewResult(dateDisplay, sections);
            }

            foreach (var section in summarySections.EnumerateObject())
            {
                var status = "Bilgi yok";
                if (section.Value.ValueKind == JsonValueKind.Object && section.Value.TryGetProperty("is_reimbursed", out var reimbursed))
                    status = reimbursed.ValueKind == JsonValueKind.True ? "Ödenir" : reimbursed.ValueKind == JsonValueKind.False ? "Ödenmez" : "Bilgi yok";

                var formatted = FormatSummarySection(section.Value);
                sections.Add(new SutOverviewSection(section.Name, status, formatted.Content, formatted.Details));
            }
        }
        catch (JsonException)
        {
            sections.Add(new SutOverviewSection("SUT Özeti", "Okunamadı", "Bilgi okunamadı."));
        }

        return new SutOverviewResult(dateDisplay, sections);
    }

    private static SutOverviewSection ParseMedulaInfo(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return new SutOverviewSection("MEDULA Bilgileri", "Bilgi alınamadı", "Bilgi alınamadı.");

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (IsEmpty(root))
                return new SutOverviewSection("MEDULA Bilgileri", "Bilgi bulunamadı", "MEDULA bilgisi bulunamadı.");

            var objects = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList()
                : root.ValueKind == JsonValueKind.Object ? [root] : [];
            if (objects.Count == 0)
                return new SutOverviewSection("MEDULA Bilgileri", "Bilgi bulunamadı", "MEDULA bilgisi bulunamadı.");

            var builder = new StringBuilder();
            for (var index = 0; index < objects.Count; index++)
            {
                if (index > 0) builder.AppendLine().AppendLine($"Kayıt {index + 1}");
                FormatMedulaInfoObject(objects[index], builder);
            }
            return new SutOverviewSection("MEDULA Bilgileri", "", builder.Length == 0 ? "MEDULA bilgisi bulunamadı." : builder.ToString().TrimEnd());
        }
        catch (JsonException)
        {
            return new SutOverviewSection("MEDULA Bilgileri", "Okunamadı", "Bilgi okunamadı.");
        }
    }

    private static void FormatMedulaInfoObject(JsonElement item, StringBuilder builder)
    {
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sut_kontrol_id" };
        AppendPair(item, "tek_doz_miktari", "tek_doz_birimi", "Tek Doz Miktarı", consumed, builder);
        AppendPair(item, "ambalaj_miktari", "ambalaj_birimi", "Ambalaj Miktarı", consumed, builder);

        foreach (var property in item.EnumerateObject())
        {
            if (consumed.Contains(property.Name) || !IsPresent(property.Value)) continue;
            if (property.Name.Equals("medula_tani_kod", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                var diagnoses = property.Value.GetString()!.Split('$', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (diagnoses.Length > 0) builder.AppendLine($"Tanı Kodları: {string.Join(Environment.NewLine + "  • ", diagnoses.Prepend(string.Empty)).TrimStart()}");
                continue;
            }
            AppendProperty(builder, property, 0);
        }
    }

    private static void AppendPair(JsonElement item, string valueKey, string unitKey, string label, HashSet<string> consumed, StringBuilder builder)
    {
        consumed.Add(valueKey);
        consumed.Add(unitKey);
        item.TryGetProperty(valueKey, out var value);
        item.TryGetProperty(unitKey, out var unit);
        var parts = new[] { IsPresent(value) ? Scalar(value) : null, IsPresent(unit) ? Scalar(unit) : null }.Where(text => !string.IsNullOrWhiteSpace(text));
        var combined = string.Join(" ", parts);
        if (!string.IsNullOrWhiteSpace(combined)) builder.AppendLine($"{label}: {combined}");
    }

    private static SutOverviewSection ParseGenericDocument(string header, string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return new SutOverviewSection(header, "Bilgi alınamadı", "Bilgi alınamadı.");
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (IsEmpty(document.RootElement)) return new SutOverviewSection(header, "Bilgi bulunamadı", "Bilgi bulunamadı.");
            var builder = new StringBuilder();
            AppendElement(builder, document.RootElement, 0);
            return new SutOverviewSection(header, "", builder.ToString().TrimEnd());
        }
        catch (JsonException)
        {
            return new SutOverviewSection(header, "Okunamadı", "Bilgi okunamadı.");
        }
    }

    private static (string Content, IReadOnlyList<SutOverviewDetailItem> Details) FormatSummarySection(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object) return ("Detay bilgisi bulunamadı.", []);
        if (!section.TryGetProperty("details", out var details) || IsEmpty(details)) return ("Detay bilgisi bulunamadı.", []);
        var builder = new StringBuilder();
        AppendElement(builder, details, 0);
        var content = builder.Length == 0 ? "Detay bilgisi bulunamadı." : builder.ToString().TrimEnd();
        return (content, BuildSummaryDetailItems(details));
    }

    private static IReadOnlyList<SutOverviewDetailItem> BuildSummaryDetailItems(JsonElement details)
    {
        var items = new List<SutOverviewDetailItem>();
        if (details.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var detail in details.EnumerateArray())
            {
                if (!IsPresent(detail)) continue;
                index++;
                var builder = new StringBuilder();
                AppendElement(builder, detail, 0);
                if (builder.Length > 0)
                    items.Add(new SutOverviewDetailItem($"Koşul {index}", builder.ToString().TrimEnd()));
            }
        }
        else if (details.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in details.EnumerateObject())
            {
                if (!IsPresent(property.Value)) continue;
                var builder = new StringBuilder();
                AppendElement(builder, property.Value, 0);
                if (builder.Length > 0)
                    items.Add(new SutOverviewDetailItem(Label(property.Name), builder.ToString().TrimEnd()));
            }
        }
        return items;
    }

    private static void AppendElement(StringBuilder builder, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    if (!property.NameEquals("is_reimbursed") && !property.NameEquals("sut_kontrol_id") && IsPresent(property.Value))
                        AppendProperty(builder, property, depth);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (!IsPresent(item)) continue;
                    index++;
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        builder.Append(' ', depth * 2).AppendLine($"Kayıt {index}");
                        AppendElement(builder, item, depth + 1);
                    }
                    else builder.Append(' ', depth * 2).AppendLine($"• {Scalar(item)}");
                }
                break;
            default:
                if (IsPresent(element)) builder.Append(' ', depth * 2).AppendLine(Scalar(element));
                break;
        }
    }

    private static void AppendProperty(StringBuilder builder, JsonProperty property, int depth)
    {
        var isUnpaidIcd = property.Name.Equals("iliskiliICD10Odenmez", StringComparison.OrdinalIgnoreCase);
        var label = (isUnpaidIcd ? "⚠ " : string.Empty) + Label(property.Name);
        if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            builder.Append(' ', depth * 2).AppendLine($"{label}:");
            AppendElement(builder, property.Value, depth + 1);
        }
        else builder.Append(' ', depth * 2).AppendLine($"{label}: {Scalar(property.Value)}");
    }

    private static string Label(string key)
    {
        if (Labels.TryGetValue(key, out var label)) return label;
        var spaced = Regex.Replace(key.Replace('_', ' '), "(?<=[a-zçğıöşü])(?=[A-ZÇĞİÖŞÜ])", " ");
        return CultureInfo.GetCultureInfo("tr-TR").TextInfo.ToTitleCase(spaced.ToLower(CultureInfo.GetCultureInfo("tr-TR")));
    }

    private static string Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => CleanRichText(element.GetString()),
        JsonValueKind.True => "Evet",
        JsonValueKind.False => "Hayır",
        _ => element.ToString()
    };

    internal static string CleanRichText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = WebUtility.HtmlDecode(WebUtility.HtmlDecode(value));
        cleaned = Regex.Replace(cleaned, @"<\s*br\s*/?\s*>", Environment.NewLine, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<\s*li(?:\s+[^>]*)?>", "• ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<\s*/?\s*(p|div|ul|ol|li|tr|h[1-6])(?:\s+[^>]*)?>", Environment.NewLine, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<[^>]+>", string.Empty);
        cleaned = cleaned.Replace('\u00a0', ' ');

        return string.Join(Environment.NewLine, cleaned
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Replace(line, @"[ \t]+", " ")));
    }

    private static bool IsPresent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString() != "-",
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => true
    };

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(),
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false
    };

    private static string FormatDate(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("dd.MM.yyyy")
        : value;
}
