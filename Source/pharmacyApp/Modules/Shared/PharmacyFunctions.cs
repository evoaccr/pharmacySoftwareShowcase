using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.Modules.Shared;

/// <summary>
/// Discoverable, UI-independent business functions intended for calls between modules.
/// Business writes stay in their owning orchestration services so callers cannot
/// bypass stock, debt, payment, traceability, or ITS transaction rules.
/// </summary>
public static class PharmacyFunctions
{
    public static PatientFunctions Patients { get; } = new();
    public static ProductFunctions Products { get; } = new();
    public static QrFunctions Qr { get; } = new();
    public static SalesQueryFunctions Sales { get; } = new();
}

/// <summary>
/// UI-independent, read-only QR movement functions for calls between modules.
/// Movement creation remains in the owning sale, invoice, return, and manual-stock
/// workflows so callers cannot bypass their stock and traceability transactions.
/// </summary>
public sealed class QrFunctions
{
    private readonly QrMovementRepository repository;

    public QrFunctions(QrMovementRepository? repository = null) =>
        this.repository = repository ?? new QrMovementRepository();

    public Task<QrMovementPage> SearchMovementsAsync(
        QrMovementQuery query,
        DateTime? beforeMovementDate,
        int? beforeRecordId,
        int pageSize,
        bool includeSummary,
        CancellationToken cancellationToken = default) =>
        repository.LoadPageAsync(
            query,
            beforeMovementDate,
            beforeRecordId,
            pageSize,
            includeSummary,
            cancellationToken);

    public async Task<QrMovementPage> GetHistoryAsync(
        string qrCode,
        DateTime? beforeMovementDate,
        int? beforeRecordId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(qrCode))
            throw new ArgumentException("QR code cannot be blank.", nameof(qrCode));

        var movementTypes = await repository.LoadMovementTypesAsync(cancellationToken);
        var today = DateTime.Today;
        var query = new QrMovementQuery(
            today,
            today.AddDays(1),
            movementTypes.Select(type => type.Id).ToArray(),
            qrCode.Trim(),
            QrMovementSearchMode.QrCode,
            IgnoreDateFilter: true);
        return await repository.LoadPageAsync(
            query,
            beforeMovementDate,
            beforeRecordId,
            pageSize,
            includeSummary: true,
            cancellationToken);
    }

    public Task<IReadOnlyList<QrMovementTypeOption>> GetMovementTypesAsync(
        CancellationToken cancellationToken = default) =>
        repository.LoadMovementTypesAsync(cancellationToken);
}

/// <summary>
/// UI-independent, read-only product catalog functions for calls between modules.
/// Search functions use the same query, filtering, Turkish text matching, stock,
/// market-state, header-filter, sorting, and pagination rules as Ürün Katalogu.
/// </summary>
public sealed class ProductFunctions
{
    private static readonly HashSet<string> ProductSearchNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bana", "bul", "diğer", "diger", "fiyat", "fiyatı", "fiyati", "form", "formları", "formlari",
        "hakkında", "hakkinda", "için", "icin", "ilaç", "ilac", "işlem", "islem", "kaç", "kac",
        "nedir", "söyle", "soyle", "yap", "getir", "what", "find", "price", "other", "forms"
    };
    private readonly ProductCatalogRepository catalogRepository;
    private readonly ProductAnalysisRepository analysisRepository;

    public ProductFunctions(
        ProductCatalogRepository? catalogRepository = null,
        ProductAnalysisRepository? analysisRepository = null)
    {
        this.catalogRepository = catalogRepository ?? new ProductCatalogRepository();
        this.analysisRepository = analysisRepository ?? new ProductAnalysisRepository();
    }

    public Task<ProductBarcodePage> SearchBarcodesAsync(
        ProductCatalogQuery query,
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadBarcodePageAsync(query, cancellationToken);

    public async Task<IReadOnlyList<string>> SearchAllBarcodesAsync(
        ProductCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var barcodes = new List<string>();
        var offset = 0;
        do
        {
            var page = await catalogRepository.LoadBarcodePageAsync(
                query with { Offset = offset, Limit = 1000 }, cancellationToken);
            barcodes.AddRange(page.Barcodes);
            if (!page.HasMore || page.Barcodes.Count == 0) break;
            offset += page.Barcodes.Count;
        }
        while (true);

        return barcodes;
    }

    public Task<ProductCatalogPage> SearchProductsAsync(
        ProductCatalogQuery query,
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadPageAsync(query, cancellationToken);

    /// <summary>
    /// Resolves a product name from natural-language text using the same repository as Ürün Katalogu.
    /// The complete phrase is attempted first; if it contains surrounding words, the narrowest
    /// matching catalog token is returned. Passive/off-market products remain visible.
    /// </summary>
    public async Task<ProductNameSearchResult> SearchProductsByNameAsync(
        string text,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Product search text cannot be empty.", nameof(text));

        limit = Math.Clamp(limit, 1, 50);
        var original = text.Trim();
        var candidates = new[] { original }
            .Concat(TokenizeProductSearch(original))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(7)
            .ToArray();
        ProductCatalogPage? best = null;
        var matchedText = original;
        foreach (var candidate in candidates)
        {
            var page = await catalogRepository.LoadPageAsync(new ProductCatalogQuery(
                Search: candidate,
                MarketStatus: null,
                RequireBarcode: true,
                SortField: "Name",
                Offset: 0,
                Limit: limit), cancellationToken);
            if (page.TotalCount == 0) continue;
            if (best is null || page.TotalCount < best.TotalCount)
            {
                best = page;
                matchedText = candidate;
            }
            if (candidate.Equals(original, StringComparison.OrdinalIgnoreCase)) break;
        }

        return new ProductNameSearchResult(
            original,
            best is null ? null : matchedText,
            best?.Items ?? [],
            best?.TotalCount ?? 0,
            best?.HasMore ?? false);
    }

    private static IEnumerable<string> TokenizeProductSearch(string text) => text
        .Split(text.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Length >= 3 && !ProductSearchNoiseWords.Contains(token))
        .OrderByDescending(token => token.Length);

    public Task<ProductCatalogItem?> GetByRecordIdAsync(
        int recordId,
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadByRecordIdAsync(recordId, cancellationToken);

    public Task<ProductCatalogItem?> GetByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadByBarcodeAsync(barcode, cancellationToken);

    public Task<ProductCatalogFilterOptions> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadFilterOptionsAsync(cancellationToken);

    public Task<IReadOnlyList<PriceHistoryPoint>> GetPriceHistoryAsync(
        int recordId,
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadPriceHistoryAsync(recordId, cancellationToken);

    public Task<IReadOnlyList<ProductPriceChangeFrequency>> GetPriceChangeFrequencyAsync(
        DateOnly start, DateOnly end, int limit, CancellationToken cancellationToken = default) =>
        catalogRepository.LoadPriceChangeFrequencyAsync(start, end, limit, cancellationToken);

    public Task<ProductDetailPayload> GetDetailsAsync(
        int recordId,
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadProductDetailsAsync(recordId, cancellationToken);

    public async Task<IReadOnlyList<string>> GetSgkEquivalentBarcodesAsync(
        string? equivalentGroupCode,
        int? excludedRecordId = null,
        CancellationToken cancellationToken = default) =>
        ToBarcodes(await catalogRepository.LoadSgkEquivalentsAsync(
            equivalentGroupCode, excludedRecordId ?? 0, cancellationToken));

    public async Task<IReadOnlyList<string>> GetGeneralEquivalentBarcodesAsync(
        IReadOnlyList<string> barcodes,
        int? excludedRecordId = null,
        CancellationToken cancellationToken = default) =>
        ToBarcodes(await catalogRepository.LoadGeneralEquivalentsAsync(
            barcodes, excludedRecordId ?? 0, cancellationToken));

    public Task<IReadOnlyList<QrCodeDisplayItem>> GetQrCodesAsync(
        string barcode,
        bool includeInactive,
        DateTime inactiveSince,
        CancellationToken cancellationToken = default) =>
        catalogRepository.LoadQrCodesAsync(barcode, includeInactive, inactiveSince, cancellationToken);

    public async Task<ProductAnalysisData?> GetAnalysisByRecordIdAsync(
        int recordId,
        CancellationToken cancellationToken = default)
    {
        var product = await catalogRepository.LoadByRecordIdAsync(recordId, cancellationToken);
        return product is null
            ? null
            : await analysisRepository.LoadReadOnlyAsync(product.RecordId, product.Barcode, cancellationToken);
    }

    public async Task<ProductAnalysisData?> GetAnalysisByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        var product = await catalogRepository.LoadByBarcodeAsync(barcode, cancellationToken);
        return product is null
            ? null
            : await analysisRepository.LoadReadOnlyAsync(product.RecordId, product.Barcode, cancellationToken);
    }

    private static IReadOnlyList<string> ToBarcodes(IEnumerable<ProductCatalogItem> products) =>
        products
            .Select(product => product.Barcode?.Trim())
            .Where(barcode => !string.IsNullOrWhiteSpace(barcode))
            .Select(barcode => barcode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record ProductNameSearchResult(
    string RequestedText,
    string? MatchedText,
    IReadOnlyList<ProductCatalogItem> Items,
    int TotalCount,
    bool HasMore);

public sealed class PatientFunctions
{
    private readonly RetailSaleRepository repository;

    public PatientFunctions(RetailSaleRepository? repository = null) =>
        this.repository = repository ?? new RetailSaleRepository();

    public Task<IReadOnlyList<PatientSuggestion>> SearchAsync(
        string search,
        CancellationToken cancellationToken = default,
        int limit = 20) =>
        repository.SearchPatientsAsync(search, cancellationToken, limit);

    public Task<PatientSuggestion?> GetByRecordIdAsync(
        int recordId,
        CancellationToken cancellationToken = default) =>
        repository.LoadPatientAsync(recordId, cancellationToken);

    public Task<PatientSuggestion?> GetByTcIdentityNumberAsync(
        string tcIdentityNumber,
        CancellationToken cancellationToken = default) =>
        repository.LoadPatientByTcAsync(tcIdentityNumber, cancellationToken);
}

public sealed class SalesQueryFunctions
{
    private readonly RetailSalesListRepository listRepository;
    private readonly RetailSaleRepository saleRepository;
    private readonly RetailRefundRepository refundRepository;

    public SalesQueryFunctions(
        RetailSalesListRepository? listRepository = null,
        RetailSaleRepository? saleRepository = null,
        RetailRefundRepository? refundRepository = null)
    {
        this.listRepository = listRepository ?? new RetailSalesListRepository();
        this.saleRepository = saleRepository ?? new RetailSaleRepository();
        this.refundRepository = refundRepository ?? new RetailRefundRepository();
    }

    public Task<IReadOnlyList<RetailSaleListItem>> SearchTransactionsAsync(
        RetailSalesListQuery query,
        CancellationToken cancellationToken = default) =>
        listRepository.LoadSalesAsync(query, cancellationToken);

    public Task<IReadOnlyList<RetailSaleProductGroup>> GetTransactionDetailsAsync(
        RetailSaleListItem transaction,
        CancellationToken cancellationToken = default) =>
        listRepository.LoadSaleDetailsAsync(transaction, cancellationToken);

    public Task<IReadOnlyList<SaleProductSuggestion>> SearchProductSuggestionsAsync(
        string search,
        CancellationToken cancellationToken = default) =>
        listRepository.SearchProductsAsync(search, cancellationToken);

    public Task<IReadOnlyList<SaleUserFilterOption>> GetUserFiltersAsync(
        CancellationToken cancellationToken = default) =>
        listRepository.LoadUsersAsync(cancellationToken);

    public Task<RetailSaleItem?> ResolveSaleItemAsync(
        string enteredCode,
        CancellationToken cancellationToken = default) =>
        saleRepository.ResolveItemAsync(enteredCode, cancellationToken);

    public Task<RetailRefundItem?> ResolveRefundItemAsync(
        string enteredCode,
        CancellationToken cancellationToken = default) =>
        refundRepository.ResolveItemAsync(enteredCode, cancellationToken);

    public Task<IReadOnlyList<string>> GetSaleQrCodesAsync(
        int saleId,
        CancellationToken cancellationToken = default) =>
        refundRepository.GetSaleQrCodesAsync(saleId, cancellationToken);
}
