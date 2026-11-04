using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pharmacyApp.Modules.Products;

public sealed class ProductCatalogItem : INotifyPropertyChanged
{
    private IReadOnlyList<byte[]> photos = [];
    private int currentPhotoIndex;

    public int RecordId { get; init; }
    public string? MedicineName { get; init; }
    public string? BrandName { get; init; }
    public string? OldName { get; init; }
    public string? Barcode { get; init; }
    public string? ProductPrefix { get; init; }
    public string? ProductTypeName { get; init; }
    public string? CompanyName { get; init; }
    public string? Origin { get; init; }
    public string? EquivalentGroupCode { get; init; }
    public string? EquivalentClassification { get; init; }
    public string? ActiveIngredient { get; init; }
    public string? AtcCode { get; init; }
    public string? SgkCode { get; init; }
    public string? IndicationSummary { get; init; }
    public string? DosageInstructions { get; init; }
    public string? PackageDescription { get; init; }
    public string? PharmaceuticalForm { get; init; }
    public string? PrescriptionTypeName { get; init; }
    public string? PregnancyCategory { get; init; }
    public string? ShelfLife { get; init; }
    public string? GenericCode { get; init; }
    public string? ReimbursementStatusDescription { get; init; }
    public string? Documents { get; init; }
    public string? SutRules { get; init; }
    public int? MarketStatus { get; init; }
    public string[] AllEquivalentBarcodes { get; init; } = [];
    public string? SutSummaryJson { get; set; }
    public string? MedulaInfoJson { get; set; }
    public string? MedulaDetailsJson { get; set; }
    public byte[]? KubDocument { get; set; }
    public byte[]? KitDocument { get; set; }
    public IReadOnlyList<ProductWarningItem> WarningItems { get; set; } = [];
    public IReadOnlyList<SutOverviewSection> SutOverviewSections { get; set; } = [];
    public string SutOverviewDateDisplay { get; set; } = string.Empty;
    public decimal? SalePrice { get; set; }
    public decimal? WarehousePrice { get; set; }
    public decimal? ManufacturerPrice { get; set; }
    public decimal? VatRate { get; set; }
    public decimal? PriceDifference { get; init; }
    public string? Currency { get; set; }
    public bool HasActiveCampaign { get; init; }
    public decimal? CampaignPurchasePrice { get; init; }

    // StockQuantity is raf stock: active quantity excluding consigned units.
    public int? StockQuantity { get; init; }
    public int? DesiredStockQuantity { get; init; }
    public ProductAnalysisData? Analysis { get; set; }

    public IReadOnlyList<PriceHistoryPoint> PriceHistory { get; set; } = [];
    public IReadOnlyList<SutRuleDisplayItem> SutRuleItems => SutRuleFormatter.Parse(SutRules);
    public IReadOnlyList<SutOverviewSection> VisibleSutOverviewSections => SutOverviewSections
        .Where(section => section.IsTemporarilyVisible)
        .ToArray();
    public bool HasSutContent => SutRuleItems.Count > 0 ||
        VisibleSutOverviewSections.Any(section => section.IsUsableSutContent);

    public string DisplayName => FirstValue(MedicineName, BrandName, "Adsız ürün");
    public bool IsInactive => MarketStatus == 0;
    public string InactiveIndicator => IsInactive ? "○" : string.Empty;
    public string DisplayBarcode => string.IsNullOrWhiteSpace(Barcode) ? "Barkod yok" : Barcode;
    public string BrandNameDisplay => string.IsNullOrWhiteSpace(BrandName) ? "—" : BrandName;
    public string OldNameDisplay => string.IsNullOrWhiteSpace(OldName) ? "—" : OldName;
    public string ProductPrefixDisplay => string.IsNullOrWhiteSpace(ProductPrefix) ? "—" : ProductPrefix;
    public string ReimbursementDisplay => string.IsNullOrWhiteSpace(ReimbursementStatusDescription) ? "—" : ReimbursementStatusDescription;
    public string DocumentsDisplay => string.IsNullOrWhiteSpace(Documents) ? "Belge bulunamadı." : Documents;
    public string DosageInstructionsDisplay => CleanUiText(DosageInstructions);
    public string SutEmptyMessage => SutRuleItems.Count == 0 ? "SUT bilgisi bulunamadı." : string.Empty;
    public bool HasKubDocument => KubDocument is { Length: > 0 };
    public bool HasKitDocument => KitDocument is { Length: > 0 };
    public IReadOnlyList<byte[]> Photos
    {
        get => photos;
        set
        {
            photos = value ?? [];
            currentPhotoIndex = 0;
            NotifyPhotoChanged();
        }
    }
    public int CurrentPhotoIndex => currentPhotoIndex;
    public bool HasMultiplePhotos => photos.Count > 1;
    public string PhotoPositionDisplay => photos.Count > 1 ? $"{currentPhotoIndex + 1}/{photos.Count}" : string.Empty;
    public string PurchasePriceDisplay => WarehousePrice is null ? "Alış: —" : $"Alış: {WarehousePrice:N2} {CurrencyDisplay}";
    public string PurchasePriceOnlyDisplay => WarehousePrice is null ? "—" : $"{WarehousePrice:N2} {CurrencyDisplay}";
    public string PurchasePriceClipboardDisplay => ProductCatalogFormatting.FormatPurchasePriceForClipboard(WarehousePrice);
    public string PurchasePriceWithVatDisplay => WarehousePrice is null
        ? "—"
        : $"{WarehousePrice:N2} {CurrencyDisplay}{(VatRate.HasValue ? $" (%{VatRate:N0})" : string.Empty)}";
    public string CampaignPurchasePriceDisplay => CampaignPurchasePrice is null ? "—" : $"{CampaignPurchasePrice:N2} {CurrencyDisplay}";
    public bool IsNotReimbursed => IsNonReimbursedStatus(ReimbursementStatusDescription);
    public DateTime? ReimbursementUnavailableSince => GetReimbursementUnavailableSince(ReimbursementStatusDescription);
    public decimal? EffectivePriceDifference => IsNotReimbursed ? null : PriceDifference;
    public string PriceDifferenceDisplay => IsNotReimbursed ? "-" : PriceDifference is null ? "—" : $"{PriceDifference:N2} {CurrencyDisplay}";
    public string StockDisplay => StockQuantity is null ? "Stok: —" : $"Stok: {StockQuantity:N0}";
    public string StockQuantityDisplay => StockQuantity?.ToString("N0") ?? "—";
    public bool HasProductPrefix => HasValue(ProductPrefix);
    public bool HasBrandName => HasValue(BrandName);
    public bool HasBarcode => HasValue(Barcode);
    public bool HasPackageDescription => HasValue(PackageDescription);
    public bool HasPharmaceuticalForm => HasValue(PharmaceuticalForm);
    public bool HasActiveIngredient => HasValue(ActiveIngredient);
    public bool HasPrescriptionType => HasValue(PrescriptionTypeName);
    public bool HasReimbursementStatus => HasValue(ReimbursementStatusDescription);
    public bool HasIndication => HasValue(IndicationSummary);
    public bool HasDosageInstructions => HasValue(DosageInstructions);
    public bool IsProductPrefixMissing => !HasProductPrefix;
    public bool IsBrandNameMissing => !HasBrandName;
    public bool IsBarcodeMissing => !HasBarcode;
    public bool IsPackageDescriptionMissing => !HasPackageDescription;
    public bool IsPharmaceuticalFormMissing => !HasPharmaceuticalForm;
    public bool IsActiveIngredientMissing => !HasActiveIngredient;
    public bool IsPrescriptionTypeMissing => !HasPrescriptionType;
    public bool IsReimbursementStatusMissing => !HasReimbursementStatus;
    public bool IsIndicationMissing => !HasIndication;
    public bool IsDosageInstructionsMissing => !HasDosageInstructions;
    public Brush PurchasePriceBrush => HasActiveCampaign ? Brushes.SeaGreen : new SolidColorBrush(Color.FromRgb(93, 109, 122));
    public Brush StockBrush => StockQuantity is null || DesiredStockQuantity is null
        ? new SolidColorBrush(Color.FromRgb(112, 128, 143))
        : StockQuantity < DesiredStockQuantity ? Brushes.Firebrick : Brushes.SeaGreen;
    public ImageSource? PhotoSource => photos.Count == 0 ? null : CreateImage(photos[currentPhotoIndex]);

    public string CurrencyDisplay => FormatCurrency(Currency);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void MovePhoto(int offset)
    {
        if (photos.Count < 2) return;
        currentPhotoIndex = (currentPhotoIndex + offset + photos.Count) % photos.Count;
        NotifyPhotoChanged();
    }

    private void NotifyPhotoChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhotoSource)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPhotoIndex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMultiplePhotos)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhotoPositionDisplay)));
    }

    private static string FormatCurrency(string? currency)
    {
        var cleaned = RepairUtf8Mojibake(currency?.Trim());
        if (string.IsNullOrWhiteSpace(cleaned) ||
            string.Equals(cleaned, "Türk Lirası", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleaned, "Turk Lirasi", StringComparison.OrdinalIgnoreCase))
            return "₺";

        var normalized = cleaned?.ToUpperInvariant();
        var known = normalized switch
        {
            "TRY" or "TL" or "TRL" or "TÜRK LIRASI" or "TÜRK LİRASI" or "TURKISH LIRA" => "₺",
            "USD" or "$" or "US DOLLAR" or "US DOLLARS" or "ABD DOLARI" => "US Dollars",
            "EUR" or "€" or "EURO" or "EUROS" => "Euros",
            "GBP" or "£" or "BRITISH POUND" or "BRITISH POUNDS" => "British Pounds",
            "CHF" or "SWISS FRANC" or "SWISS FRANCS" => "Swiss Francs",
            "JPY" or "¥" or "JAPANESE YEN" => "Japanese Yen",
            "CAD" or "CANADIAN DOLLAR" or "CANADIAN DOLLARS" => "Canadian Dollars",
            "AUD" or "AUSTRALIAN DOLLAR" or "AUSTRALIAN DOLLARS" => "Australian Dollars",
            _ => null
        };
        if (known is not null) return known;

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (string.Equals(region.ISOCurrencySymbol, normalized, StringComparison.OrdinalIgnoreCase))
                    return region.CurrencyEnglishName;
            }
            catch (ArgumentException)
            {
            }
        }

        return cleaned!;
    }

    private static string? RepairUtf8Mojibake(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || (!value.Contains('Ã') && !value.Contains('Ä'))) return value;

        try
        {
            return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value));
        }
        catch
        {
            return value;
        }
    }

    private static string CleanUiText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";

        var cleaned = Regex.Replace(value, @"<\s*br\s*/?\s*>", Environment.NewLine, RegexOptions.IgnoreCase);
        cleaned = WebUtility.HtmlDecode(cleaned);
        return string.Join(Environment.NewLine, cleaned
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool IsNonReimbursedStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = Regex.Replace(value.Trim().TrimEnd('.', ';'), @"\s+", " ");
        return string.Equals(normalized, "ödenmez", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("bedeli ödenmez", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("ödenme listesinden çıkarılmıştır", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("ödeme listesinden çıkarılmıştır", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? GetReimbursementUnavailableSince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(value.Contains("ödenme listesinden çıkarılmıştır", StringComparison.OrdinalIgnoreCase) ||
              value.Contains("ödeme listesinden çıkarılmıştır", StringComparison.OrdinalIgnoreCase)))
            return null;

        var dateMatch = Regex.Match(value, @"\b\d{2}/\d{2}/(?:\d{2}|\d{4})\b");
        if (!dateMatch.Success) return null;

        return DateTime.TryParseExact(
            dateMatch.Value,
            ["dd/MM/yy", "dd/MM/yyyy"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedDate)
            ? parsedDate.Date
            : null;
    }

    private static string FirstValue(params string?[] values) => values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static ImageSource? CreateImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class PriceHistoryPoint
{
    public DateTime EffectiveDate { get; init; }
    public decimal? SalePrice { get; init; }
    public decimal? WarehousePrice { get; init; }
    public decimal? ManufacturerPrice { get; init; }
    public decimal? PublicPrice { get; init; }
    public decimal? PublicPaidPrice { get; init; }
    public decimal? PriceDifference { get; init; }
    public decimal? DiscountRate { get; init; }
    public decimal? VatRate { get; init; }
    public bool IsPriceDifferenceUnavailable { get; set; }
    public string PriceDifferenceDisplay => IsPriceDifferenceUnavailable ? "-" : PriceDifference?.ToString("N2") ?? "—";
    public decimal PriceDifferenceSortValue => IsPriceDifferenceUnavailable || PriceDifference is null ? decimal.MaxValue : PriceDifference.Value;
}

public sealed class SutRuleDisplayItem
{
    public string Heading { get; init; } = "SUT Kuralı";
    public string Reference { get; init; } = "—";
    public string EffectiveDate { get; init; } = "—";
    public string Text { get; init; } = "Metin bulunamadı.";
    public string Archive { get; init; } = string.Empty;
}

internal static partial class SutRuleFormatter
{
    public static IReadOnlyList<SutRuleDisplayItem> Parse(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return [];

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var source = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];

            return source
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => new SutRuleDisplayItem
                {
                    Heading = Value(element, "sut_madde") ?? "SUT Kuralı",
                    Reference = Value(element, "referans") ?? "—",
                    EffectiveDate = Value(element, "gecerlilik_tarihi") ?? "—",
                    Text = CleanHtml(Value(element, "sut_metin")),
                    Archive = FormatArchive(element)
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [new SutRuleDisplayItem { Heading = "SUT Bilgisi", Text = CleanHtml(rawJson) }];
        }
    }

    private static string? Value(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return RepairMojibake(property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString());
    }

    private static string FormatArchive(JsonElement element)
    {
        if (!element.TryGetProperty("arsiv", out var archive) || archive.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var entries = archive.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item =>
            {
                var gt = Value(item, "gt");
                var id = Value(item, "id");
                return string.Join(" · ", new[]
                {
                    string.IsNullOrWhiteSpace(gt) ? null : $"GT: {gt}",
                    string.IsNullOrWhiteSpace(id) ? null : $"Kimlik: {id}"
                }.Where(value => value is not null));
            })
            .Where(value => !string.IsNullOrWhiteSpace(value));

        return string.Join(Environment.NewLine, entries);
    }

    private static string CleanHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Metin bulunamadı.";

        var normalized = BreakTagRegex().Replace(text, Environment.NewLine);
        normalized = ClosingBlockTagRegex().Replace(normalized, Environment.NewLine);
        normalized = ListItemTagRegex().Replace(normalized, "• ");
        normalized = HtmlTagRegex().Replace(normalized, string.Empty);
        normalized = WebUtility.HtmlDecode(WebUtility.HtmlDecode(normalized));
        normalized = RepairMojibake(normalized) ?? normalized;
        normalized = normalized.Replace('\u00a0', ' ');

        return string.Join(Environment.NewLine, normalized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => WhitespaceRegex().Replace(line, " ")));
    }

    private static string? RepairMojibake(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(value.Contains('Ã') || value.Contains('Ä') || value.Contains('Å') || value.Contains('Â')))
            return value;

        try
        {
            var repaired = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value));
            return repaired.Count(character => character == '�') <= value.Count(character => character == '�')
                ? repaired
                : value;
        }
        catch
        {
            return value;
        }
    }

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTagRegex();

    [GeneratedRegex(@"<\s*/\s*(p|div|li|tr|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingBlockTagRegex();

    [GeneratedRegex(@"<\s*li(?:\s+[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex WhitespaceRegex();
}
