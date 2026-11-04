using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Npgsql;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.WebDepots.Cooperative;

public interface ICooperativeWebDepotService
{
    string DepotName { get; }
    CooperativeConnectionStatus Status { get; }
    event EventHandler<CooperativeConnectionStatus>? StatusChanged;

    Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(CancellationToken cancellationToken = default);
    Task<CooperativeMedicationPriceInfo> GetMedicationPriceInfoAsync(
        string barcode,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CooperativeBasketItem>> GetBasketAsync(
        CancellationToken cancellationToken = default);
    Task<bool> AddBasketItemAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken = default);
    Task<bool> DecreaseBasketItemAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken = default);
    Task<bool> RemoveBasketItemAsync(
        string barcode,
        CancellationToken cancellationToken = default);
    Task<bool> ClearBasketAsync(CancellationToken cancellationToken = default);
    Task<bool> PurchaseAsync(CancellationToken cancellationToken = default);
    Task<bool> PurchaseAsync(
        IReadOnlyDictionary<string, int> expectedBasket,
        CancellationToken cancellationToken = default);
    Task<bool> ShowBrowserForDebugAsync(CancellationToken cancellationToken = default);
    Task<bool> RestartBrowserForDebugAsync(CancellationToken cancellationToken = default);
    void ShowBrowser();
}

public enum CooperativeConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed
}

public sealed record CooperativeConnectionStatus(
    CooperativeConnectionState State,
    string Message,
    DateTimeOffset Timestamp);

public sealed record CooperativeBasketItem(string Barcode, int Quantity);

public sealed record CooperativeMedicationPriceInfo(
    string Barcode,
    string ProductName,
    decimal ActiveVatIncludedDepotPrice,
    string Campaign,
    string Discount,
    string Maturity);

public abstract class CooperativeWebDepotService : ICooperativeWebDepotService
{
    private static readonly HashSet<string> AllowedBasketDispatchActions =
    [
        "basket/fetchBasketsAndProducts",
        "header/searchBar/fetchSearchBarResults",
        "products/fetchProduct",
        "basket/removeFromBasket",
        "products/goToProductDetail",
        "basket/prepareAddingProductInBasketToInsert",
        "basket/prepareAddingProductInBasketToUpdate",
        "basket/changeBaremByQuantity",
        "basket/addToBasket",
        "basket/deleteOrder"
    ];

    private static readonly HashSet<string> AllowedPurchaseDispatchActions =
    [
        "basket/fetchBasketsAndProducts",
        "products/fetchProduct",
        "basket/completeOrderOrCheckOnlinePayment"
    ];

    private const string VueStoreLookupScript = """
        const findStore = () => {
            const documents = [];
            const visit = (doc, depth = 0) => {
                if (!doc || documents.includes(doc) || depth > 5) return;
                documents.push(doc);
                for (const frame of doc.querySelectorAll('iframe, frame')) {
                    try { visit(frame.contentDocument, depth + 1); } catch { }
                }
            };
            visit(document);
            for (const doc of documents) {
                const preferred = [doc.querySelector('#app'), doc.body, doc.documentElement]
                    .filter(Boolean);
                for (const element of preferred) {
                    if (element.__vue__?.$store) return element.__vue__.$store;
                    if (element.__vue__?.$root?.$store) return element.__vue__.$root.$store;
                }
                for (const element of doc.querySelectorAll('*')) {
                    if (element.__vue__?.$store) return element.__vue__.$store;
                    if (element.__vue__?.$root?.$store) return element.__vue__.$root.$store;
                }
            }
            return null;
        };
        """;

    private const string VueProductBarcodeLookupScript = """
        const productBarcodeCache = new Map();
        const normalizeBarcode = value => String(value ?? '').trim();
        const resolveProductBarcode = async (store, product) => {
            const embedded = normalizeBarcode(
                product?.barcode ?? product?.ean11 ?? product?.eAN11);
            if (/^\d{13}$/.test(embedded)) return embedded;

            const productId = String(product?.id ?? '').trim();
            if (!productId) throw new Error('Sepet satırında ürün kodu bulunamadı.');
            if (productBarcodeCache.has(productId))
                return productBarcodeCache.get(productId);

            await store.dispatch('products/fetchProduct', { productId });
            const currentProduct = store.getters['products/getCurrentProduct'];
            const barcode = normalizeBarcode(
                currentProduct?.barcode ?? currentProduct?.ean11 ?? currentProduct?.eAN11);
            if (!/^\d{13}$/.test(barcode))
                throw new Error(`${productId} ürününün barkodu çözümlenemedi.`);
            productBarcodeCache.set(productId, barcode);
            return barcode;
        };
        """;

    private const string OrdersAppReadyScript = """
        (() => {
        """ + VueStoreLookupScript + """
            const store = findStore();
            return !!store
                && typeof store.dispatch === 'function'
                && !!store.getters?.['basket/getBasketsForDrawer'];
        })()
        """;

    private const string OrdersPageStateScript = """
        (() => {
        """ + VueStoreLookupScript + """
            if (findStore()) return 'ready';
            const documents = [];
            const visit = (doc, depth = 0) => {
                if (!doc || documents.includes(doc) || depth > 5) return;
                documents.push(doc);
                for (const frame of doc.querySelectorAll('iframe, frame')) {
                    try { visit(frame.contentDocument, depth + 1); } catch { }
                }
            };
            visit(document);
            if (documents.some(doc => /application server error/i.test(doc.title || '')
                || /503 service not available/i.test(doc.body?.innerText || ''))) return 'unavailable';
            return documents.some(doc => /\/orders\/?(?:[#?].*)?$/i.test(doc.location?.href || ''))
                ? 'loading'
                : 'portal';
        })()
        """;

    private const string PopupCloserScript = """
        (() => {
            const stateKey = '__pharmacyCooperativePopupCloser';
            if (window[stateKey]?.dismiss) {
                window[stateKey].dismiss();
                return true;
            }

            const popupSelector = [
                '[role="dialog"]',
                'dialog[open]',
                '[aria-modal="true"]',
                '.modal.show',
                '.bootbox.show',
                '.swal2-container',
                '.sweet-alert.show',
                '.popup.show',
                '.popup.open'
            ].join(',');
            const closeSelector = [
                '[data-bs-dismiss="modal"]',
                '[data-dismiss="modal"]',
                '[aria-label*="kapat" i]',
                '[aria-label*="close" i]',
                '[title*="kapat" i]',
                '[title*="close" i]',
                '.btn-close',
                '.modal-close',
                '.popup-close',
                'button.close'
            ].join(',');
            const closeText = /^(×|x|close|kapat|iptal|vazgeç|hayır|no)$/i;
            const confirmText = /^(evet|yes|ok|tamam|onayla|sil|siparişi onayla)$/i;
            const isVisible = element => {
                if (!(element instanceof HTMLElement) || element.getClientRects().length === 0) return false;
                const style = getComputedStyle(element);
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && Number(style.opacity || '1') > 0.01;
            };
            const dismiss = () => {
                let dismissed = 0;
                for (const popup of document.querySelectorAll(popupSelector)) {
                    if (!isVisible(popup)) continue;
                    const controls = Array.from(
                        popup.querySelectorAll('button, a, input[type="button"], input[type="submit"]'));
                    const isConfirmation = controls.some(control =>
                        confirmText.test((control.textContent || control.value || '').trim()));
                    let close = Array.from(popup.querySelectorAll(closeSelector)).find(isVisible);
                    if (!close && !isConfirmation) {
                        close = controls.find(control => isVisible(control)
                            && closeText.test((control.textContent || control.value || '').trim()));
                    }
                    if (!close) continue;
                    close.click();
                    dismissed++;
                }
                return dismissed;
            };
            let scheduled = false;
            const scheduleDismiss = () => {
                if (scheduled) return;
                scheduled = true;
                queueMicrotask(() => {
                    scheduled = false;
                    dismiss();
                });
            };
            const observer = new MutationObserver(scheduleDismiss);
            const start = () => {
                dismiss();
                observer.observe(document.documentElement, {
                    attributes: true,
                    childList: true,
                    subtree: true,
                    attributeFilter: ['class', 'style', 'open', 'aria-hidden']
                });
            };
            window[stateKey] = { dismiss, observer };
            if (document.documentElement) start();
            else document.addEventListener('DOMContentLoaded', start, { once: true });
            return true;
        })()
        """;

    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan PortalInitializationDelay = TimeSpan.FromSeconds(12);
    private readonly SemaphoreSlim browserGate = new(1, 1);
    private readonly string homeUrl;
    private readonly string expectedHost;
    private readonly string profileFolderName;
    private readonly Func<ApplicationSettings, string> usernameSelector;
    private readonly Func<ApplicationSettings, string> passwordSelector;
    private Window? browserWindow;
    private WebView2? browser;
    private bool debugBrowserVisible;

    protected CooperativeWebDepotService(
        string depotName,
        string homeUrl,
        string profileFolderName,
        Func<ApplicationSettings, string> usernameSelector,
        Func<ApplicationSettings, string> passwordSelector)
    {
        DepotName = depotName;
        this.homeUrl = homeUrl;
        expectedHost = new Uri(homeUrl).Host;
        this.profileFolderName = profileFolderName;
        this.usernameSelector = usernameSelector;
        this.passwordSelector = passwordSelector;
        Status = new CooperativeConnectionStatus(
            CooperativeConnectionState.Disconnected,
            $"{DepotName} bağlantısı başlatılmadı.",
            DateTimeOffset.Now);
    }

    public string DepotName { get; }

    public CooperativeConnectionStatus Status { get; private set; }

    public event EventHandler<CooperativeConnectionStatus>? StatusChanged;

    public Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => OpenBrowserCoreAsync(cancellationToken));

    public Task<bool> LoginAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => LoginCoreAsync(cancellationToken));

    public Task<CooperativeMedicationPriceInfo> GetMedicationPriceInfoAsync(
        string barcode,
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => GetMedicationPriceInfoCoreAsync(barcode, cancellationToken));

    public Task<IReadOnlyList<CooperativeBasketItem>> GetBasketAsync(
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => GetBasketCoreAsync(cancellationToken));

    public Task<bool> AddBasketItemAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => AddBasketItemCoreAsync(barcode, amount, cancellationToken));

    public Task<bool> DecreaseBasketItemAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => DecreaseBasketItemCoreAsync(barcode, amount, cancellationToken));

    public Task<bool> RemoveBasketItemAsync(
        string barcode,
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => RemoveBasketItemCoreAsync(barcode, cancellationToken));

    public Task<bool> ClearBasketAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => ClearBasketCoreAsync(cancellationToken));

    public Task<bool> PurchaseAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => PurchaseCoreAsync(null, cancellationToken));

    public Task<bool> PurchaseAsync(
        IReadOnlyDictionary<string, int> expectedBasket,
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => PurchaseCoreAsync(expectedBasket, cancellationToken));

    public Task<bool> ShowBrowserForDebugAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(async () =>
        {
            debugBrowserVisible = true;
            await browserGate.WaitAsync(cancellationToken);
            try
            {
                await EnsureBrowserAsync(cancellationToken);
                ShowBrowserWindow(activate: true);
                return await EnsureAuthenticatedCoreAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                Report(
                    CooperativeConnectionState.Failed,
                    $"{DepotName} tarayıcısı açılamadı: {exception.Message}");
                return false;
            }
            finally
            {
                browserGate.Release();
            }
        });

    public Task<bool> RestartBrowserForDebugAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(async () =>
        {
            debugBrowserVisible = true;
            await browserGate.WaitAsync(cancellationToken);
            try
            {
                Report(CooperativeConnectionState.Connecting, $"{DepotName} tarayıcısı yeniden başlatılıyor.");
                if (browserWindow is not null) browserWindow.Close();
                else
                {
                    browser?.Dispose();
                    browser = null;
                }

                await EnsureBrowserAsync(cancellationToken);
                ShowBrowserWindow(activate: true);
                var authenticated = await EnsureAuthenticatedCoreAsync(cancellationToken);
                Report(
                    authenticated ? CooperativeConnectionState.Connected : CooperativeConnectionState.Disconnected,
                    authenticated
                        ? $"{DepotName} tarayıcısı yeniden başlatıldı ve oturum açıldı."
                        : $"{DepotName} tarayıcısı yeniden başlatıldı; oturum açılamadı.");
                return authenticated;
            }
            catch (Exception exception)
            {
                Report(
                    CooperativeConnectionState.Failed,
                    $"{DepotName} tarayıcısı yeniden başlatılamadı: {exception.Message}");
                return false;
            }
            finally
            {
                browserGate.Release();
            }
        });

    public void ShowBrowser()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(ShowBrowser);
            return;
        }

        debugBrowserVisible = true;
        ShowBrowserWindow(activate: true);
    }

    private async Task<bool> OpenBrowserCoreAsync(CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureBrowserAsync(cancellationToken);
            return await EnsureAuthenticatedCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} tarayıcı açma işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} tarayıcısı açılamadı: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<bool> LoginCoreAsync(CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureAuthenticatedCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} girişi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} girişi başarısız: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private Task<IReadOnlyList<CooperativeBasketItem>> GetBasketCoreAsync(
        CancellationToken cancellationToken) =>
        RunBasketOperationCoreAsync(
            "sepeti okunuyor",
            "sepeti okundu",
            async () =>
            {
                var response = await ExecuteBasketCommandAsync<BasketReadResponse>("""
                    (async () => {
                    """ + VueStoreLookupScript + VueProductBarcodeLookupScript + """
                        const store = findStore();
                        if (!store) return JSON.stringify({ success: false, error: 'Sepet uygulaması bulunamadı.' });
                        try {
                            await store.dispatch('basket/fetchBasketsAndProducts', {
                                storeId: store.getters['stores/getSelectedStoreId'],
                                willForce: true
                            });
                            const baskets = store.getters[
                                'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                                || store.getters['basket/getBasketsForDrawer'] || [];
                            const items = [];
                            for (const basket of baskets) {
                                for (const product of basket.products || []) {
                                    items.push({
                                        barcode: await resolveProductBarcode(store, product),
                                        quantity: Number(product.quantity ?? 0),
                                        additionalQuantity: Number(product.additionalQuantity ?? 0)
                                    });
                                }
                            }
                            return JSON.stringify({ success: true, items });
                        } catch (error) {
                            return JSON.stringify({
                                success: false,
                                error: error?.message || 'Sepet bilgisi alınamadı.'
                            });
                        }
                    })()
                    """, cancellationToken);
                EnsureBasketCommandSucceeded(response);
                return (IReadOnlyList<CooperativeBasketItem>)response.Items
                    .Select(item => new CooperativeBasketItem(
                        item.Barcode.Trim(),
                        Math.Max(0, item.Quantity) + Math.Max(0, item.AdditionalQuantity)))
                    .Where(item => item.Barcode.Length > 0 && item.Quantity > 0)
                    .GroupBy(item => item.Barcode, StringComparer.Ordinal)
                    .Select(group => new CooperativeBasketItem(
                        group.Key,
                        group.Sum(item => item.Quantity)))
                    .OrderBy(item => item.Barcode, StringComparer.Ordinal)
                    .ToArray();
            },
            cancellationToken);

    private async Task<CooperativeMedicationPriceInfo> GetMedicationPriceInfoCoreAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        var barcodeJson = JsonSerializer.Serialize(barcode);
        var searchText = await ResolveProductSearchTextAsync(barcode, cancellationToken);
        var searchTextJson = JsonSerializer.Serialize(searchText);
        var script = ("""
            (async () => {
            """ + VueStoreLookupScript + VueProductBarcodeLookupScript + """
                const store = findStore();
                if (!store)
                    return JSON.stringify({ success: false, error: 'Sipariş uygulaması bulunamadı.' });
                try {
                    const barcode = __BARCODE__;
                    const searchText = __SEARCH_TEXT__;
                    const searchTerm = searchText.trim().split(/\s+/)[0] || barcode;
                    const dispatchedSearchResults = await store.dispatch(
                        'header/searchBar/fetchSearchBarResults', searchTerm);
                    const searchResults = [];
                    const collectSearchResults = (value, depth = 0) => {
                        if (!value || depth > 4) return;
                        if (Array.isArray(value)) {
                            for (const entry of value)
                                collectSearchResults(entry, depth + 1);
                            return;
                        }
                        if (typeof value !== 'object') return;
                        const resultId = String(value.id ?? '').trim();
                        if (/^\d+$/.test(resultId) && /[1-9]/.test(resultId))
                            searchResults.push(value);
                        for (const nested of Object.values(value))
                            collectSearchResults(nested, depth + 1);
                    };
                    const searchDeadline = Date.now() + 10000;
                    do {
                        const searchResultsGetter =
                            store.getters['header/searchBar/getSearchBarResults'];
                        const getterSearchResults =
                            typeof searchResultsGetter === 'function'
                                ? searchResultsGetter(searchTerm)
                                : searchResultsGetter;
                        const rawSearchResults = getterSearchResults
                            || store.state?.header?.searchBar?.searchBarResults
                            || store.state?.header?.searchBar?.results
                            || dispatchedSearchResults || [];
                        searchResults.length = 0;
                        collectSearchResults(rawSearchResults);
                        if (searchResults.length > 0) break;
                        await new Promise(resolve => setTimeout(resolve, 100));
                    } while (Date.now() < searchDeadline);

                    let product = null;
                    for (const result of searchResults) {
                        await store.dispatch('products/fetchProduct', {
                            productId: String(result.id)
                        });
                        const productDeadline = Date.now() + 10000;
                        let candidate;
                        do {
                            const currentProductGetter =
                                store.getters['products/getCurrentProduct'];
                            candidate = typeof currentProductGetter === 'function'
                                ? currentProductGetter(String(result.id))
                                : currentProductGetter;
                            if (String(candidate?.id ?? '') === String(result.id)) break;
                            await new Promise(resolve => setTimeout(resolve, 100));
                        } while (Date.now() < productDeadline);
                        if (!candidate?.id) continue;
                        let candidateBarcode;
                        try {
                            candidateBarcode = await resolveProductBarcode(store, candidate);
                        } catch {
                            continue;
                        }
                        if (candidateBarcode === barcode) {
                            product = { ...candidate };
                            break;
                        }
                    }
                    if (!product)
                        return JSON.stringify({ success: false,
                            error: 'Barkodla eşleşen ürün bulunamadı.' });

                    const paymentTypes = Array.isArray(product.paymentTypes)
                        ? product.paymentTypes : [];
                    const paymentType = paymentTypes.find(entry =>
                        Number.isFinite(Number(entry?.netPrice))) || paymentTypes[0] || null;
                    const barems = Array.isArray(product.baremler) ? product.baremler : [];
                    const baseBarem = barems.find(entry =>
                        Number(entry?.mfTaban) === 1
                        && Number(entry?.mfAdet) === 0
                        && (!paymentType
                            || String(entry?.paymentTypeId ?? '')
                                === String(paymentType?.id ?? '')))
                        || barems.find(entry => Number(entry?.mfAdet) === 0)
                        || null;
                    const netPrice = Number(
                        baseBarem?.netPrice ?? paymentType?.netPrice ?? product.dsf);
                    if (!Number.isFinite(netPrice) || netPrice < 0)
                        return JSON.stringify({ success: false,
                            error: 'Aktif depocu satış fiyatı bulunamadı.' });
                    const taxPercentage = Number(product.taxPercentage ?? 0);
                    const vatIncludedPrice = Number((netPrice
                        * (1 + (Number.isFinite(taxPercentage) ? taxPercentage : 0) / 100))
                        .toFixed(2));

                    const campaign = barems
                        .filter(entry => Number(entry?.mfAdet) > 0)
                        .map(entry => String(entry?.text ?? '').trim()
                            || `${entry.mfTaban} + ${entry.mfAdet}`)
                        .filter(Boolean)
                        .filter((value, index, values) => values.indexOf(value) === index)
                        .join('; ') || 'Yok';

                    const baseQuantity = Number(baseBarem?.mfTaban ?? 1);
                    const discount = (Array.isArray(product.discounts)
                        ? product.discounts : [])
                        .filter(entry => Number(entry?.kstbm) === baseQuantity)
                        .filter(entry => !/^(ara toplam|kdv)$/i.test(
                            String(entry?.name ?? '').trim()))
                        .filter(entry => String(entry?.percentage ?? '').trim()
                            || Number(entry?.amount ?? 0) > 0)
                        .map(entry => {
                            const name = String(entry?.name ?? 'İskonto')
                                .replace(/\s*tutarı\s*$/i, '').trim();
                            const value = String(entry?.percentage ?? '').trim()
                                || Number(entry?.amount ?? 0).toFixed(2);
                            return `${name}: ${value}`;
                        })
                        .filter((value, index, values) => values.indexOf(value) === index)
                        .join('; ')
                        || (Number(product.manufacturerDiscount ?? 0) > 0
                            ? `%${Number(product.manufacturerDiscount)}`
                            : 'Yok');
                    const maturity = String(paymentType?.name ?? '').trim()
                        || (paymentType?.paymentDate
                            ? new Date(paymentType.paymentDate).toLocaleDateString('tr-TR')
                            : 'Belirtilmedi');

                    return JSON.stringify({
                        success: true,
                        barcode,
                        productName: String(product.name ?? '').trim(),
                        activeVatIncludedDepotPrice: vatIncludedPrice,
                        campaign,
                        discount,
                        maturity
                    });
                } catch (error) {
                    return JSON.stringify({ success: false,
                        error: error?.message || 'Fiyat ve kampanya bilgisi okunamadı.' });
                }
            })()
            """)
            .Replace("__BARCODE__", barcodeJson, StringComparison.Ordinal)
            .Replace("__SEARCH_TEXT__", searchTextJson, StringComparison.Ordinal);

        var response = await RunBasketOperationCoreAsync(
            "fiyat ve kampanya bilgisi okunuyor",
            "fiyat ve kampanya bilgisi okundu",
            async () =>
            {
                var result = await ExecuteBasketCommandAsync<MedicationPriceResponse>(
                    script,
                    cancellationToken);
                EnsureBasketCommandSucceeded(result);
                return result;
            },
            cancellationToken);
        return new CooperativeMedicationPriceInfo(
            response.Barcode,
            response.ProductName,
            response.ActiveVatIncludedDepotPrice,
            response.Campaign,
            response.Discount,
            response.Maturity);
    }

    private Task<bool> RemoveBasketItemCoreAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        var barcodeJson = JsonSerializer.Serialize(barcode);
        var script = ("""
            (async () => {
            """ + VueStoreLookupScript + VueProductBarcodeLookupScript + """
                const store = findStore();
                if (!store) return JSON.stringify({ success: false, error: 'Sepet uygulaması bulunamadı.' });
                try {
                    await store.dispatch('basket/fetchBasketsAndProducts', {
                        storeId: store.getters['stores/getSelectedStoreId'],
                        willForce: true
                    });
                    const barcode = __BARCODE__;
                    const baskets = store.getters[
                        'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                        || store.getters['basket/getBasketsForDrawer'] || [];
                    const matches = [];
                    for (const basket of baskets) {
                        for (const product of basket.products || []) {
                            if (await resolveProductBarcode(store, product) === barcode)
                                matches.push({ basket, product });
                        }
                    }
                    if (matches.length === 0) return JSON.stringify({ success: true, changed: false });
                    const matchedProductIds = new Set(matches.map(match => String(match.product.id ?? '')));
                    for (const match of matches) {
                        await store.dispatch('basket/removeFromBasket', {
                            basketId: match.basket.id,
                            product: match.product
                        });
                    }
                    await store.dispatch('basket/fetchBasketsAndProducts', {
                        storeId: store.getters['stores/getSelectedStoreId'],
                        willForce: true
                    });
                    const remaining = (store.getters[
                            'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                            || store.getters['basket/getBasketsForDrawer'] || [])
                        .some(basket => (basket.products || [])
                            .some(product => matchedProductIds.has(String(product.id ?? ''))));
                    return JSON.stringify({ success: true, changed: !remaining });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        changed: false,
                        error: error?.message || 'Ürün sepetten kaldırılamadı.'
                    });
                }
            })()
            """).Replace("__BARCODE__", barcodeJson, StringComparison.Ordinal);
        return RunBasketMutationCoreAsync(
            "sepet ürünü kaldırılıyor",
            "sepet ürünü kaldırıldı",
            script,
            cancellationToken);
    }

    private async Task<bool> AddBasketItemCoreAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Eklenecek miktar sıfırdan büyük olmalıdır.");

        var barcodeJson = JsonSerializer.Serialize(barcode);
        var searchText = await ResolveProductSearchTextAsync(barcode, cancellationToken);
        var searchTextJson = JsonSerializer.Serialize(searchText);
        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        var script = ("""
            (async () => {
            """ + VueStoreLookupScript + VueProductBarcodeLookupScript + """
                const store = findStore();
                if (!store) return JSON.stringify({ success: false, error: 'Sepet uygulaması bulunamadı.' });
                const managedBaskets = () => store.getters[
                    'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                    || store.getters['basket/getBasketsForDrawer'] || [];
                const purchasedQuantity = (baskets, productId) => baskets
                    .flatMap(basket => basket.products || [])
                    .filter(product => String(product.id ?? '') === productId)
                    .reduce((sum, product) => sum + Number(product.quantity ?? 0), 0);
                const waitForAddingProduct = async productId => {
                    const deadline = Date.now() + 15000;
                    let addingProduct = store.getters['basket/getAddingProduct'];
                    while (String(addingProduct?.id ?? '') !== productId
                        && Date.now() < deadline) {
                        await new Promise(resolve => setTimeout(resolve, 50));
                        addingProduct = store.getters['basket/getAddingProduct'];
                    }
                    return addingProduct;
                };
                try {
                    const barcode = __BARCODE__;
                    const searchText = __SEARCH_TEXT__;
                    const searchTerm = searchText.trim().split(/\s+/)[0] || barcode;
                    const amount = __AMOUNT__;
                    await store.dispatch('basket/fetchBasketsAndProducts', {
                        storeId: store.getters['stores/getSelectedStoreId'],
                        willForce: true
                    });

                    const dispatchedSearchResults = await store.dispatch(
                        'header/searchBar/fetchSearchBarResults', searchTerm);
                    const searchResults = [];
                    const collectSearchResults = (value, depth = 0) => {
                        if (!value || depth > 4) return;
                        if (Array.isArray(value)) {
                            for (const entry of value)
                                collectSearchResults(entry, depth + 1);
                            return;
                        }
                        if (typeof value !== 'object') return;
                        const resultId = String(value.id ?? '').trim();
                        if (/^\d+$/.test(resultId) && /[1-9]/.test(resultId))
                            searchResults.push(value);
                        for (const nested of Object.values(value))
                            collectSearchResults(nested, depth + 1);
                    };
                    let rawSearchResults;
                    const searchDeadline = Date.now() + 10000;
                    do {
                        const searchResultsGetter =
                            store.getters['header/searchBar/getSearchBarResults'];
                        const getterSearchResults =
                            typeof searchResultsGetter === 'function'
                                ? searchResultsGetter(searchTerm)
                                : searchResultsGetter;
                        rawSearchResults =
                            getterSearchResults
                            || store.state?.header?.searchBar?.searchBarResults
                            || store.state?.header?.searchBar?.results
                            || dispatchedSearchResults || [];
                        searchResults.length = 0;
                        collectSearchResults(rawSearchResults);
                        if (searchResults.length > 0) break;
                        await new Promise(resolve => setTimeout(resolve, 100));
                    } while (Date.now() < searchDeadline);
                    let product = null;
                    for (const result of searchResults) {
                        if (!result?.id) continue;
                        await store.dispatch('products/fetchProduct', {
                            productId: String(result.id)
                        });
                        const candidateDeadline = Date.now() + 10000;
                        let candidate;
                        do {
                            const currentProductGetter =
                                store.getters['products/getCurrentProduct'];
                            candidate = typeof currentProductGetter === 'function'
                                ? currentProductGetter(String(result.id))
                                : currentProductGetter;
                            if (String(candidate?.id ?? '') === String(result.id)) break;
                            await new Promise(resolve => setTimeout(resolve, 100));
                        } while (Date.now() < candidateDeadline);
                        if (!candidate?.id) continue;
                        let candidateBarcode;
                        try {
                            candidateBarcode = await resolveProductBarcode(store, candidate);
                        } catch {
                            continue;
                        }
                        if (candidateBarcode === barcode) {
                            product = { ...candidate };
                            break;
                        }
                    }
                    if (!product)
                        return JSON.stringify({ success: false, changed: false,
                            error: 'Barkodla eşleşen ürün bulunamadı.' });

                    const productId = String(product.id ?? '');
                    const beforeQuantity = purchasedQuantity(managedBaskets(), productId);
                    await store.dispatch('products/goToProductDetail', {
                        product,
                        params: { noClearSearchResults: true }
                    });
                    let addingProduct = await waitForAddingProduct(productId);
                    if (!addingProduct || String(addingProduct.id ?? '') !== productId)
                        throw new Error('Ürün ekleme ekranı hazırlanamadı.');

                    await store.dispatch('basket/prepareAddingProductInBasketToInsert');
                    addingProduct = store.getters['basket/getAddingProduct'];
                    if (!addingProduct || String(addingProduct.id ?? '') !== productId)
                        throw new Error('Ürün ekleme modeli hazırlanamadı.');
                    addingProduct.quantity = amount;
                    await store.dispatch('basket/changeBaremByQuantity', amount);
                    await store.dispatch('basket/addToBasket');

                    const expectedQuantity = beforeQuantity + amount;
                    const basketDeadline = Date.now() + 15000;
                    let afterQuantity;
                    do {
                        await store.dispatch('basket/fetchBasketsAndProducts', {
                            storeId: store.getters['stores/getSelectedStoreId'],
                            willForce: true
                        });
                        afterQuantity = purchasedQuantity(managedBaskets(), productId);
                        if (afterQuantity === expectedQuantity) break;
                        await new Promise(resolve => setTimeout(resolve, 150));
                    } while (Date.now() < basketDeadline);
                    return JSON.stringify({
                        success: true,
                        changed: afterQuantity === expectedQuantity
                    });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        changed: false,
                        error: error?.message || 'Ürün sepete eklenemedi.'
                    });
                }
            })()
            """)
            .Replace("__BARCODE__", barcodeJson, StringComparison.Ordinal)
            .Replace("__SEARCH_TEXT__", searchTextJson, StringComparison.Ordinal)
            .Replace("__AMOUNT__", amountText, StringComparison.Ordinal);
        return await RunBasketMutationCoreAsync(
            "sepete ürün ekleniyor",
            "sepete ürün eklendi",
            script,
            cancellationToken);
    }

    private static async Task<string> ResolveProductSearchTextAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            const string sql = """
                SELECT COALESCE(
                    NULLIF(BTRIM(brand_name), ''),
                    NULLIF(BTRIM(old_name), ''),
                    NULLIF(BTRIM(medicine_name), ''),
                    barcode)
                FROM public.barcode
                WHERE barcode = @barcode
                LIMIT 1
                """;
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
            command.Parameters.AddWithValue("barcode", barcode);
            return (await command.ExecuteScalarAsync(cancellationToken) as string)?.Trim()
                   is { Length: > 0 } name
                ? name
                : barcode;
        }
        catch (NpgsqlException)
        {
            return barcode;
        }
    }

    private Task<bool> DecreaseBasketItemCoreAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Azaltılacak miktar sıfırdan büyük olmalıdır.");

        var barcodeJson = JsonSerializer.Serialize(barcode);
        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        var script = ("""
            (async () => {
            """ + VueStoreLookupScript + VueProductBarcodeLookupScript + """
                const store = findStore();
                if (!store) return JSON.stringify({ success: false, error: 'Sepet uygulaması bulunamadı.' });
                try {
                    const barcode = __BARCODE__;
                    const amount = __AMOUNT__;
                    await store.dispatch('basket/fetchBasketsAndProducts', {
                        storeId: store.getters['stores/getSelectedStoreId'],
                        willForce: true
                    });
                    const baskets = store.getters[
                        'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                        || store.getters['basket/getBasketsForDrawer'] || [];
                    const matches = [];
                    for (const basket of baskets) {
                        for (const product of basket.products || []) {
                            if (await resolveProductBarcode(store, product) === barcode)
                                matches.push({ basket, product });
                        }
                    }
                    const currentQuantity = matches.reduce(
                        (sum, match) => sum + Number(match.product.quantity ?? 0), 0);
                    if (currentQuantity < amount) {
                        return JSON.stringify({ success: true, changed: false });
                    }

                    let remaining = amount;
                    for (const match of matches) {
                        if (remaining <= 0) break;
                        const quantity = Number(match.product.quantity ?? 0);
                        if (remaining >= quantity) {
                            await store.dispatch('basket/removeFromBasket', {
                                basketId: match.basket.id,
                                product: match.product
                            });
                            remaining -= quantity;
                            continue;
                        }

                        const targetQuantity = quantity - remaining;
                        await store.dispatch('products/goToProductDetail', {
                            product: match.product,
                            params: { noClearSearchResults: true }
                        });
                        const expectedProductId = String(match.product.id ?? '');
                        const deadline = Date.now() + 15000;
                        let addingProduct = store.getters['basket/getAddingProduct'];
                        while (String(addingProduct?.id ?? '') !== expectedProductId
                            && Date.now() < deadline) {
                            await new Promise(resolve => setTimeout(resolve, 50));
                            addingProduct = store.getters['basket/getAddingProduct'];
                        }
                        if (!addingProduct || String(addingProduct.id ?? '') !== expectedProductId) {
                            throw new Error('Ürün miktarı güncelleme ekranı hazırlanamadı.');
                        }
                        await store.dispatch('basket/prepareAddingProductInBasketToUpdate', {
                            basketId: match.basket.id,
                            product: match.product
                        });
                        addingProduct = store.getters['basket/getAddingProduct'];
                        if (!addingProduct
                            || String(addingProduct.id ?? '') !== expectedProductId
                            || String(addingProduct.updatingProps?.basketId ?? '')
                                !== String(match.basket.id ?? '')) {
                            throw new Error('Ürün miktarı güncelleme modeli hazırlanamadı.');
                        }
                        addingProduct.quantity = targetQuantity;
                        await store.dispatch('basket/changeBaremByQuantity', targetQuantity);
                        await store.dispatch('basket/addToBasket', { basketId: match.basket.id });
                        remaining = 0;
                    }

                    const matchedProductIds = new Set(matches.map(match => String(match.product.id ?? '')));
                    const expectedQuantity = currentQuantity - amount;
                    const basketDeadline = Date.now() + 15000;
                    let updatedQuantity;
                    do {
                        await store.dispatch('basket/fetchBasketsAndProducts', {
                            storeId: store.getters['stores/getSelectedStoreId'],
                            willForce: true
                        });
                        updatedQuantity = (store.getters[
                                'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                                || store.getters['basket/getBasketsForDrawer'] || [])
                            .flatMap(basket => basket.products || [])
                            .filter(product => matchedProductIds.has(String(product.id ?? '')))
                            .reduce((sum, product) => sum + Number(product.quantity ?? 0), 0);
                        if (updatedQuantity === expectedQuantity) break;
                        await new Promise(resolve => setTimeout(resolve, 150));
                    } while (Date.now() < basketDeadline);
                    return JSON.stringify({
                        success: true,
                        changed: updatedQuantity === expectedQuantity
                    });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        changed: false,
                        error: error?.message || 'Ürün miktarı azaltılamadı.'
                    });
                }
            })()
            """)
            .Replace("__BARCODE__", barcodeJson, StringComparison.Ordinal)
            .Replace("__AMOUNT__", amountText, StringComparison.Ordinal);
        return RunBasketMutationCoreAsync(
            "sepet miktarı azaltılıyor",
            "sepet miktarı azaltıldı",
            script,
            cancellationToken);
    }

    private Task<bool> ClearBasketCoreAsync(CancellationToken cancellationToken)
    {
        var script = """
            (async () => {
            """ + VueStoreLookupScript + """
                const store = findStore();
                if (!store) return JSON.stringify({ success: false, error: 'Sepet uygulaması bulunamadı.' });
                try {
                    await store.dispatch('basket/fetchBasketsAndProducts', {
                        storeId: store.getters['stores/getSelectedStoreId'],
                        willForce: true
                    });
                    const baskets = (store.getters[
                            'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                            || store.getters['basket/getBasketsForDrawer'] || [])
                        .filter(basket => basket.id && (basket.products || []).length > 0);
                    for (const basket of baskets)
                        await store.dispatch('basket/deleteOrder', basket.id);
                    await store.dispatch('basket/fetchBasketsAndProducts', {
                        storeId: store.getters['stores/getSelectedStoreId'],
                        willForce: true
                    });
                    const remaining = (store.getters[
                            'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                            || store.getters['basket/getBasketsForDrawer'] || [])
                        .some(basket => (basket.products || []).length > 0);
                    return JSON.stringify({ success: true, changed: !remaining });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        changed: false,
                        error: error?.message || 'Sepet temizlenemedi.'
                    });
                }
            })()
            """;
        return RunBasketMutationCoreAsync(
            "sepeti temizleniyor",
            "sepeti temizlendi",
            script,
            cancellationToken);
    }

    private async Task<bool> PurchaseCoreAsync(
        IReadOnlyDictionary<string, int>? expectedBasket,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CooperativeBasketItem> snapshot;
        try
        {
            snapshot = await GetBasketCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} satın alma işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(
                CooperativeConnectionState.Failed,
                $"{DepotName} satın alma öncesi sepeti okunamadı: {exception.Message}");
            return false;
        }

        var actualBasket = snapshot
            .GroupBy(item => item.Barcode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.Quantity),
                StringComparer.Ordinal);
        if (actualBasket.Count == 0)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} sepeti boş; sipariş verilmedi.");
            return false;
        }

        var requiredBasket = expectedBasket ?? actualBasket;
        if (requiredBasket.Any(item =>
                item.Key.Length != 13
                || item.Key.Any(character => !char.IsAsciiDigit(character))
                || item.Value <= 0))
            throw new ArgumentException(
                "Beklenen sepet yalnızca 13 haneli barkodlar ve pozitif miktarlar içermelidir.",
                nameof(expectedBasket));
        if (!BasketManifestsMatch(requiredBasket, actualBasket))
        {
            Report(
                CooperativeConnectionState.Failed,
                $"{DepotName} sepeti beklenen barkod ve miktarlarla eşleşmedi; sipariş verilmedi.");
            return false;
        }

        var expectedBasketJson = JsonSerializer.Serialize(requiredBasket);
        var script = ("""
            (async () => {
            """ + VueStoreLookupScript + VueProductBarcodeLookupScript + """
                const store = findStore();
                if (!store)
                    return JSON.stringify({ success: false, error: 'Sepet uygulaması bulunamadı.' });
                try {
                    const expectedBasket = __EXPECTED_BASKET__;
                    const selectedStoreId = store.getters['stores/getSelectedStoreId'];
                    const managedBaskets = () => store.getters[
                        'basket/getMyBasketsNotWaitingOnlinePaymentForSelectedStore']
                        || store.getters['basket/getBasketsForDrawer'] || [];
                    await store.dispatch('basket/fetchBasketsAndProducts', {
                        storeId: selectedStoreId,
                        willForce: true
                    });
                    const baskets = managedBaskets()
                        .filter(basket => basket.id && (basket.products || []).length > 0);
                    if (baskets.length === 0)
                        return JSON.stringify({ success: true, completed: false });

                    const actualBasket = {};
                    for (const basket of baskets) {
                        for (const product of basket.products || []) {
                            const barcode = await resolveProductBarcode(store, product);
                            const quantity = Math.max(0, Number(product.quantity ?? 0))
                                + Math.max(0, Number(product.additionalQuantity ?? 0));
                            actualBasket[barcode] = (actualBasket[barcode] || 0) + quantity;
                        }
                    }
                    const expectedKeys = Object.keys(expectedBasket).sort();
                    const actualKeys = Object.keys(actualBasket).sort();
                    const matches = expectedKeys.length === actualKeys.length
                        && expectedKeys.every((barcode, index) =>
                            barcode === actualKeys[index]
                            && Number(expectedBasket[barcode]) === Number(actualBasket[barcode]));
                    if (!matches)
                        return JSON.stringify({ success: true, completed: false });

                    const basketIds = baskets.map(basket => String(basket.id));
                    for (const basket of baskets)
                        await store.dispatch(
                            'basket/completeOrderOrCheckOnlinePayment', basket);

                    const deadline = Date.now() + 20000;
                    let remaining = true;
                    do {
                        await store.dispatch('basket/fetchBasketsAndProducts', {
                            storeId: selectedStoreId,
                            willForce: true
                        });
                        remaining = managedBaskets().some(basket =>
                            basketIds.includes(String(basket.id))
                            && (basket.products || []).length > 0);
                        if (!remaining) break;
                        await new Promise(resolve => setTimeout(resolve, 200));
                    } while (Date.now() < deadline);
                    return JSON.stringify({ success: true, completed: !remaining });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        completed: false,
                        error: error?.message || 'Sipariş tamamlanamadı.'
                    });
                }
            })()
            """).Replace(
                "__EXPECTED_BASKET__",
                expectedBasketJson,
                StringComparison.Ordinal);

        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken)) return false;
            Report(CooperativeConnectionState.Connecting, $"{DepotName} siparişi doğrulanıyor.");
            await EnsureOrdersAppCoreAsync(cancellationToken);
            var response = await ExecutePurchaseCommandAsync<PurchaseResponse>(
                script,
                cancellationToken);
            EnsureBasketCommandSucceeded(response);
            Report(
                response.Completed
                    ? CooperativeConnectionState.Connected
                    : CooperativeConnectionState.Failed,
                response.Completed
                    ? $"{DepotName} siparişi başarıyla oluşturuldu."
                    : $"{DepotName} sepeti değişti veya sipariş sonucu doğrulanamadı; webdepo sipariş kutusunu kontrol edin.");
            return response.Completed;
        }
        catch (OperationCanceledException)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} satın alma işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(
                CooperativeConnectionState.Failed,
                $"{DepotName} satın alma işlemi başarısız: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private static bool BasketManifestsMatch(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual) =>
        expected.Count == actual.Count
        && expected.All(item =>
            actual.TryGetValue(item.Key, out var quantity) && quantity == item.Value);

    private async Task<bool> RunBasketMutationCoreAsync(
        string progressMessage,
        string successMessage,
        string script,
        CancellationToken cancellationToken)
    {
        var response = await RunBasketOperationCoreAsync(
            progressMessage,
            successMessage,
            async () =>
            {
                var result = await ExecuteBasketCommandAsync<BasketMutationResponse>(
                    script,
                    cancellationToken);
                EnsureBasketCommandSucceeded(result);
                return result;
            },
            cancellationToken);
        return response.Changed;
    }

    private async Task<T> RunBasketOperationCoreAsync<T>(
        string progressMessage,
        string successMessage,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken))
                throw new InvalidOperationException($"{DepotName} oturumu açılamadı.");
            Report(CooperativeConnectionState.Connecting, $"{DepotName} {progressMessage}.");
            await EnsureOrdersAppCoreAsync(cancellationToken);
            var result = await operation();
            Report(CooperativeConnectionState.Connected, $"{DepotName} {successMessage}.");
            return result;
        }
        catch (OperationCanceledException)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} işlemi iptal edildi.");
            throw;
        }
        catch (Exception exception)
        {
            Report(
                CooperativeConnectionState.Failed,
                $"{DepotName} işlemi başarısız: {exception.Message}");
            throw;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<bool> EnsureAuthenticatedCoreAsync(CancellationToken cancellationToken)
    {
        Report(CooperativeConnectionState.Connecting, $"{DepotName} oturumu açılıyor.");
        await EnsureBrowserAsync(cancellationToken);
        if (!IsCurrentDepotPage()) await NavigateAsync(homeUrl, cancellationToken);

        var pageState = await WaitForPageStateAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (pageState == AuthenticationPageState.Authenticated)
        {
            await DismissBlockingPopupsAsync();
            Report(CooperativeConnectionState.Connected, $"Mevcut {DepotName} oturumu kullanılıyor.");
            return true;
        }

        if (pageState != AuthenticationPageState.LoginForm)
        {
            await NavigateAsync(homeUrl, cancellationToken);
            pageState = await WaitForPageStateAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        if (pageState == AuthenticationPageState.Authenticated)
        {
            await DismissBlockingPopupsAsync();
            Report(CooperativeConnectionState.Connected, $"Mevcut {DepotName} oturumu tanındı.");
            return true;
        }

        if (pageState != AuthenticationPageState.LoginForm)
        {
            Report(CooperativeConnectionState.Failed, $"{DepotName} giriş sayfası tanınamadı.");
            return false;
        }

        var settings = ApplicationSettingsStore.Current;
        var username = usernameSelector(settings);
        var password = passwordSelector(settings);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            Report(
                CooperativeConnectionState.Failed,
                $"{DepotName} kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");
            return false;
        }

        await FillLoginFieldAsync("#logonuidfield", username, "kullanıcı adı");
        await FillLoginFieldAsync("#logonpassfield", password, "parola");
        if (!await SubmitLoginAndWaitAsync(cancellationToken))
        {
            Report(
                CooperativeConnectionState.Failed,
                $"{DepotName} girişi doğrulanamadı. Kullanıcı adı ve parolayı kontrol edin.");
            return false;
        }

        await DismissBlockingPopupsAsync();
        Report(CooperativeConnectionState.Connected, $"{DepotName} oturumu açıldı.");
        return true;
    }

    private async Task<bool> SubmitLoginAndWaitAsync(CancellationToken cancellationToken)
    {
        var currentBrowser = RequireBrowser();
        var navigationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? completed = null;
        completed = (_, args) =>
        {
            if (args.IsSuccess || args.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown)
                navigationCompleted.TrySetResult();
            else
                navigationCompleted.TrySetException(new InvalidOperationException(
                    $"{DepotName} giriş yanıtı açılamadı: {args.WebErrorStatus}."));
        };
        currentBrowser.NavigationCompleted += completed;
        try
        {
            var submitted = await ExecuteBooleanAsync(
                """
                (() => {
                    const submit = document.querySelector('input[type="submit"][name="login"]');
                    if (!submit || submit.disabled) return false;
                    submit.click();
                    return true;
                })()
                """);
            if (!submitted) return false;
            await navigationCompleted.Task.WaitAsync(BrowserTimeout, cancellationToken);
        }
        finally
        {
            currentBrowser.NavigationCompleted -= completed;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsAuthenticatedAsync()) return true;
            if (await HasLoginFormAsync()) return false;
            await Task.Delay(120, cancellationToken);
        }
        return false;
    }

    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireWebDepot();
        if (browser?.CoreWebView2 is not null && browserWindow is not null)
        {
            ApplyBrowserWindowMode(debugBrowserVisible, activate: debugBrowserVisible);
            return;
        }

        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "pharmacyApp";
        var resolvedProfileFolderName = executableName.Equals("pharmacyApp", StringComparison.OrdinalIgnoreCase)
            ? profileFolderName
            : $"{profileFolderName}-{executableName}";
        var userDataFolder = Path.Combine(
            pharmacyApp.Infrastructure.AppPaths.DataDirectory,
            resolvedProfileFolderName);
        Directory.CreateDirectory(userDataFolder);

        browser = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder }
        };
        browserWindow = new Window
        {
            Title = $"{DepotName} Arka Plan Tarayıcısı",
            Width = 1180,
            Height = 820,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0.01,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = browser
        };
        if (Application.Current.MainWindow is { IsVisible: true } mainWindow)
            browserWindow.Owner = mainWindow;
        ApplyBrowserWindowMode(debugBrowserVisible, activate: false);
        browserWindow.Closed += (_, _) =>
        {
            browser?.Dispose();
            browser = null;
            browserWindow = null;
            Report(CooperativeConnectionState.Disconnected, $"{DepotName} tarayıcısı kapatıldı.");
        };

        try
        {
            browserWindow.Show();
            await browser.EnsureCoreWebView2Async().WaitAsync(BrowserTimeout, cancellationToken);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var requestedUri)) return;
                var isDepotHost = requestedUri.Host.Equals(
                    expectedHost,
                    StringComparison.OrdinalIgnoreCase);
                var isSharedOrderHost = requestedUri.Host.Equals(
                    "esube.farma-lojistik.com",
                    StringComparison.OrdinalIgnoreCase);
                if (isDepotHost || isSharedOrderHost)
                    browser.CoreWebView2.Navigate(requestedUri.AbsoluteUri);
            };
            browser.CoreWebView2.ScriptDialogOpening += (_, args) =>
            {
                if (args.Kind is CoreWebView2ScriptDialogKind.Alert
                    or CoreWebView2ScriptDialogKind.Beforeunload)
                    args.Accept();
            };
            await browser.CoreWebView2
                .AddScriptToExecuteOnDocumentCreatedAsync(PopupCloserScript)
                .WaitAsync(BrowserTimeout, cancellationToken);
            ApplyBrowserWindowMode(debugBrowserVisible, activate: debugBrowserVisible);
        }
        catch
        {
            browserWindow.Close();
            throw;
        }
    }

    private async Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        var currentBrowser = RequireBrowser();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        EventHandler<CoreWebView2NavigationStartingEventArgs>? starting = null;
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? completed = null;
        starting = (_, args) =>
        {
            if (args.Uri.Equals(url, StringComparison.OrdinalIgnoreCase)) navigationId = args.NavigationId;
        };
        completed = (_, args) =>
        {
            if (navigationId is null || args.NavigationId != navigationId.Value) return;
            if (args.IsSuccess || args.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown)
                completion.TrySetResult();
            else
                completion.TrySetException(new InvalidOperationException(
                    $"{DepotName} sayfası açılamadı: {args.WebErrorStatus}."));
        };
        currentBrowser.NavigationStarting += starting;
        currentBrowser.NavigationCompleted += completed;
        try
        {
            currentBrowser.CoreWebView2.Navigate(url);
            await completion.Task.WaitAsync(BrowserTimeout, cancellationToken);
            await DismissBlockingPopupsAsync();
        }
        finally
        {
            currentBrowser.NavigationStarting -= starting;
            currentBrowser.NavigationCompleted -= completed;
        }
    }

    private async Task FillLoginFieldAsync(string selector, string value, string fieldName)
    {
        var selectorJson = JsonSerializer.Serialize(selector);
        var valueJson = JsonSerializer.Serialize(value);
        var succeeded = await ExecuteBooleanAsync($$"""
            (() => {
                const field = document.querySelector({{selectorJson}});
                if (!field) return false;
                const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                if (!setter) return false;
                setter.call(field, {{valueJson}});
                field.dispatchEvent(new Event('input', { bubbles: true }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
                return field.value.length === {{value.Length.ToString(CultureInfo.InvariantCulture)}};
            })()
            """);
        if (!succeeded)
            throw new InvalidOperationException($"{DepotName} {fieldName} alanı doldurulamadı.");
    }

    private async Task<AuthenticationPageState> WaitForPageStateAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await IsAuthenticatedAsync()) return AuthenticationPageState.Authenticated;
            if (await HasLoginFormAsync()) return AuthenticationPageState.LoginForm;
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline) return AuthenticationPageState.Unknown;
            await Task.Delay(120, cancellationToken);
        }
    }

    private Task<bool> HasLoginFormAsync() => ExecuteBooleanAsync(
        "!!document.querySelector('#logonuidfield')"
        + " && !!document.querySelector('#logonpassfield')"
        + " && !!document.querySelector('input[type=\"submit\"][name=\"login\"]')");

    private Task<bool> IsAuthenticatedAsync()
    {
        var expectedHostJson = JsonSerializer.Serialize(expectedHost);
        return ExecuteBooleanAsync($$"""
            (() => {
                const host = location.hostname.toLocaleLowerCase('en-US');
                const hasLoginForm = !!document.querySelector('#logonuidfield')
                    && !!document.querySelector('#logonpassfield');
                if (host !== {{expectedHostJson}} || hasLoginForm) return false;
                const links = Array.from(document.querySelectorAll('a[href]'));
                const hasLogoutLink = links.some(link =>
                    /(logout|logoff|signout|oturum.*kapat|cikis|çıkış)/i.test(
                        `${link.getAttribute('href') ?? ''} ${link.textContent ?? ''}`));
                const controls = Array.from(document.querySelectorAll('a, button, input[type="button"]'));
                const hasLogoutControl = controls.some(control =>
                    /^(çıkış|cikis|oturumu kapat|güvenli çıkış)$/i.test(
                        (control.textContent || control.value || '').trim()));
                const bodyText = document.body?.innerText?.toLocaleLowerCase('tr-TR') ?? '';
                const hasPortalNavigation = bodyText.includes('sipariş') && links.length >= 4;
                const hasSapPortalShell = location.pathname.toLocaleLowerCase('en-US').startsWith('/irj/portal')
                    && document.body?.classList.contains('prtlBody')
                    && !!document.querySelector('iframe#contentAreaFrame')
                    && !!document.querySelector('form#contentAreaFrame_Form');
                return document.readyState === 'complete'
                    && (hasLogoutLink || hasLogoutControl || hasPortalNavigation || hasSapPortalShell);
            })()
            """);
    }

    private async Task EnsureOrdersAppCoreAsync(CancellationToken cancellationToken)
    {
        if (await ExecuteBooleanAsync(OrdersAppReadyScript)) return;

        await Task.Delay(PortalInitializationDelay, cancellationToken);
        if (await ExecuteBooleanAsync(OrdersAppReadyScript)) return;

        var clicked = await ExecuteBooleanAsync(
            """
            (() => {
                const candidates = Array.from(document.querySelectorAll('a'))
                    .filter(link => (link.textContent || '')
                        .toLocaleLowerCase('tr-TR')
                        .includes('siparişler ve iade'));
                const target = candidates.find(link => link.getClientRects().length > 0)
                    || candidates[0];
                if (!target) return false;
                target.click();
                return true;
            })()
            """);
        if (!clicked)
            throw new InvalidOperationException(
                "Siparişler ve İade sayfası portal menüsünde bulunamadı.");

        var deadline = DateTime.UtcNow + BrowserTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ExecuteStringAsync(OrdersPageStateScript);
            if (state == "ready") return;
            if (state == "unavailable")
                throw new InvalidOperationException(
                    "Siparişler ve İade sunucusu şu anda 503 Service Not Available yanıtı veriyor.");
            await Task.Delay(180, cancellationToken);
        }

        throw new TimeoutException("Siparişler ve İade uygulaması zamanında açılamadı.");
    }

    private async Task<T> ExecuteBasketCommandAsync<T>(
        string script,
        CancellationToken cancellationToken)
    {
        EnsureBasketScriptCannotPurchase(script);
        var json = await ExecutePromiseStringAsync(script, cancellationToken);
        return JsonSerializer.Deserialize<T>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Sepet yanıtı çözümlenemedi.");
    }

    private async Task<T> ExecutePurchaseCommandAsync<T>(
        string script,
        CancellationToken cancellationToken)
    {
        EnsurePurchaseScriptIsRestricted(script);
        var json = await ExecutePromiseStringAsync(script, cancellationToken);
        return JsonSerializer.Deserialize<T>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Satın alma yanıtı çözümlenemedi.");
    }

    private async Task<string> ExecutePromiseStringAsync(
        string promiseScript,
        CancellationToken cancellationToken)
    {
        var currentBrowser = RequireBrowser();
        var commandId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var commandIdJson = JsonSerializer.Serialize(commandId);
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? received = null;
        received = (_, args) =>
        {
            try
            {
                var messageJson = args.TryGetWebMessageAsString();
                var message = JsonSerializer.Deserialize<BrowserCommandMessage>(
                    messageJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (message?.Channel != "pharmacy-cooperative"
                    || message.CommandId != commandId)
                    return;

                if (message.Succeeded)
                    completion.TrySetResult(message.Value ?? string.Empty);
                else
                    completion.TrySetException(new InvalidOperationException(
                        string.IsNullOrWhiteSpace(message.Error)
                            ? "Web deposu komutu tamamlanamadı."
                            : message.Error));
            }
            catch (InvalidOperationException)
            {
                // Ignore messages posted by the depot application in a non-string format.
            }
            catch (JsonException)
            {
                // Ignore unrelated string messages posted by the depot application.
            }
        };

        currentBrowser.CoreWebView2.WebMessageReceived += received;
        try
        {
            var wrapper = string.Concat(
                "(() => { const commandId = ",
                commandIdJson,
                "; Promise.resolve(",
                promiseScript,
                ").then(value => window.chrome.webview.postMessage(JSON.stringify({",
                "channel: 'pharmacy-cooperative', commandId, succeeded: true, ",
                "value: typeof value === 'string' ? value : JSON.stringify(value) })))",
                ".catch(error => window.chrome.webview.postMessage(JSON.stringify({",
                "channel: 'pharmacy-cooperative', commandId, succeeded: false, ",
                "error: error?.message || String(error) }))); return true; })()");
            if (!await ExecuteBooleanAsync(wrapper))
                throw new InvalidOperationException("Web deposu komutu başlatılamadı.");
            return await completion.Task.WaitAsync(BrowserTimeout, cancellationToken);
        }
        finally
        {
            currentBrowser.CoreWebView2.WebMessageReceived -= received;
        }
    }

    private static void EnsureBasketScriptCannotPurchase(string script)
    {
        string[] forbiddenMarkers =
        [
            "completeOrderOrCheckOnlinePayment",
            "goToAndSetSucceedOrderBasket",
            "completingOrder",
            "processPayment",
            "SiparisOnay",
            "btnSiparisOnay"
        ];
        if (forbiddenMarkers.Any(marker =>
                script.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Sepet komutlarında sipariş tamamlama işlemi kullanılamaz.");

        foreach (Match match in Regex.Matches(
                     script,
                     "store\\.dispatch\\(\\s*['\\\"](?<action>[^'\\\"]+)"))
        {
            var action = match.Groups["action"].Value;
            if (!AllowedBasketDispatchActions.Contains(action))
                throw new InvalidOperationException(
                    $"Sepet komutunda izin verilmeyen Vuex işlemi kullanıldı: {action}.");
        }
    }

    private static void EnsurePurchaseScriptIsRestricted(string script)
    {
        const string purchaseAction = "basket/completeOrderOrCheckOnlinePayment";
        if (!script.Contains(purchaseAction, StringComparison.Ordinal))
            throw new InvalidOperationException("Satın alma komutunda sipariş işlemi bulunamadı.");
        if (script.Contains("goToAndSetSucceedOrderBasket", StringComparison.OrdinalIgnoreCase)
            || script.Contains("deleteOrder", StringComparison.OrdinalIgnoreCase)
            || script.Contains("processPayment", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Satın alma komutu izin verilmeyen ek sipariş işlemleri içeriyor.");

        foreach (Match match in Regex.Matches(
                     script,
                     "store\\.dispatch\\(\\s*['\\\"](?<action>[^'\\\"]+)"))
        {
            var action = match.Groups["action"].Value;
            if (!AllowedPurchaseDispatchActions.Contains(action))
                throw new InvalidOperationException(
                    $"Satın alma komutunda izin verilmeyen Vuex işlemi kullanıldı: {action}.");
        }
    }

    private static void EnsureBasketCommandSucceeded(BasketCommandResponse response)
    {
        if (response.Success) return;
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(response.Error)
                ? "Sepet işlemi web deposu tarafından tamamlanamadı."
                : response.Error);
    }

    private static void ValidateBarcode(string barcode)
    {
        if (barcode.Length != 13 || barcode.Any(character => !char.IsAsciiDigit(character)))
            throw new ArgumentException("Barkod 13 rakamdan oluşmalıdır.", nameof(barcode));
    }

    private bool IsCurrentDepotPage() =>
        browser?.Source is { } source
        && source.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ExecuteBooleanAsync(string script)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ExecuteStringAsync(string script)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(raw)
               ?? throw new InvalidOperationException("Web deposu boş bir yanıt döndürdü.");
    }

    private async Task DismissBlockingPopupsAsync()
    {
        if (browser?.CoreWebView2 is null) return;
        await browser.CoreWebView2.ExecuteScriptAsync(PopupCloserScript);
    }

    private WebView2 RequireBrowser() => browser?.CoreWebView2 is not null
        ? browser
        : throw new InvalidOperationException($"{DepotName} arka plan tarayıcısı başlatılmadı.");

    private void ApplyBrowserWindowMode(bool visible, bool activate)
    {
        if (browserWindow is null) return;
        if (visible)
        {
            ShowBrowserWindow(activate);
            return;
        }

        browserWindow.ShowInTaskbar = false;
        browserWindow.Opacity = 0.01;
        browserWindow.WindowStyle = WindowStyle.None;
        browserWindow.ResizeMode = ResizeMode.NoResize;
        browserWindow.Left = -32000;
        browserWindow.Top = -32000;
    }

    private void ShowBrowserWindow(bool activate)
    {
        if (browserWindow is null) return;
        var workArea = SystemParameters.WorkArea;
        browserWindow.ShowInTaskbar = true;
        browserWindow.Opacity = 1;
        browserWindow.WindowStyle = WindowStyle.SingleBorderWindow;
        browserWindow.ResizeMode = ResizeMode.CanResize;
        browserWindow.Left = workArea.Left + Math.Max(0, (workArea.Width - browserWindow.Width) / 2);
        browserWindow.Top = workArea.Top + Math.Max(0, (workArea.Height - browserWindow.Height) / 2);
        if (browserWindow.IsLoaded && !browserWindow.IsVisible) browserWindow.Show();
        if (activate && browserWindow.IsVisible) browserWindow.Activate();
    }

    private void Report(CooperativeConnectionState state, string message)
    {
        Status = new CooperativeConnectionStatus(state, message, DateTimeOffset.Now);
        StatusChanged?.Invoke(this, Status);
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireWebDepot();
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Uygulama Dispatcher'ı kullanılamıyor.");
        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private enum AuthenticationPageState
    {
        Unknown,
        Authenticated,
        LoginForm
    }

    private class BasketCommandResponse
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
    }

    private sealed class BasketReadResponse : BasketCommandResponse
    {
        public BasketReadItem[] Items { get; init; } = [];
    }

    private sealed class BasketReadItem
    {
        public string Barcode { get; init; } = string.Empty;
        public int Quantity { get; init; }
        public int AdditionalQuantity { get; init; }
    }

    private sealed class BasketMutationResponse : BasketCommandResponse
    {
        public bool Changed { get; init; }
    }

    private sealed class MedicationPriceResponse : BasketCommandResponse
    {
        public string Barcode { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public decimal ActiveVatIncludedDepotPrice { get; init; }
        public string Campaign { get; init; } = string.Empty;
        public string Discount { get; init; } = string.Empty;
        public string Maturity { get; init; } = string.Empty;
    }

    private sealed class PurchaseResponse : BasketCommandResponse
    {
        public bool Completed { get; init; }
    }

    private sealed class BrowserCommandMessage
    {
        public string? Channel { get; init; }
        public string? CommandId { get; init; }
        public bool Succeeded { get; init; }
        public string? Value { get; init; }
        public string? Error { get; init; }
    }
}
