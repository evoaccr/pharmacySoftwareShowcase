using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.WebDepots.Alliance;

public interface IAllianceWebDepotService
{
    AllianceConnectionStatus Status { get; }
    event EventHandler<AllianceConnectionStatus>? StatusChanged;

    Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(CancellationToken cancellationToken = default);
    Task<AllianceMedicationPriceInfo> GetMedicationPriceInfoAsync(
        string barcode,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AllianceBasketItem>> GetBasketAsync(
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

public enum AllianceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed
}

public sealed record AllianceConnectionStatus(
    AllianceConnectionState State,
    string Message,
    DateTimeOffset Timestamp);

public sealed record AllianceBasketItem(string Barcode, int Quantity);

public sealed record AllianceMedicationPriceInfo(
    string Barcode,
    string ProductName,
    decimal ActiveVatIncludedDepotPrice,
    string Campaign,
    string Discount,
    string Maturity);

public sealed class AllianceWebDepotService : IAllianceWebDepotService
{
    public const string HomeUrl = "https://esiparisv2.alliance-healthcare.com.tr/";
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(35);
    private const string PopupCloserScript = """
        (() => {
            const stateKey = '__pharmacyAlliancePopupCloser';
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
            const confirmText = /^(evet|yes|ok|tamam|onayla|satın al|siparişi onayla)$/i;
            const isVisible = element => {
                if (!(element instanceof HTMLElement) || element.getClientRects().length === 0)
                    return false;
                const style = getComputedStyle(element);
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && Number(style.opacity || '1') > 0.01;
            };
            const dismiss = () => {
                let dismissed = 0;
                for (const popup of document.querySelectorAll(popupSelector)) {
                    if (!isVisible(popup)) continue;
                    const controls = Array.from(popup.querySelectorAll(
                        'button, a, input[type="button"], input[type="submit"]'));
                    const isConfirmation = controls.some(control => confirmText.test(
                        (control.textContent || control.value || '').trim()));
                    let close = Array.from(popup.querySelectorAll(closeSelector)).find(isVisible);
                    if (!close && !isConfirmation)
                        close = controls.find(control => isVisible(control)
                            && closeText.test((control.textContent || control.value || '').trim()));
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

    private const string CommandHelpersScript = """
        const post = (url, data) => new Promise((resolve, reject) => {
            if (!window.jQuery) {
                reject(new Error('Alliance sayfa bileşenleri henüz hazır değil.'));
                return;
            }
            window.jQuery.ajax({
                url,
                type: 'POST',
                data,
                success: resolve,
                error: xhr => reject(new Error(`${url}: ${xhr.status}`))
            });
        });
        const parseHtml = html => new DOMParser().parseFromString(String(html || ''), 'text/html');
        const decode = encoded => {
            try { return JSON.parse(atob(String(encoded || ''))); }
            catch { return null; }
        };
        const compact = value => String(value ?? '').replace(/\s+/g, ' ').trim();
        const parseNumber = value => {
            const normalized = compact(value).replace(/\./g, '').replace(',', '.');
            const parsed = Number(normalized);
            return Number.isFinite(parsed) ? parsed : 0;
        };
        const today = () => new Date().toLocaleDateString('sv-SE');
        const isOfferActive = offer => {
            const current = today();
            const starts = String(offer?.DateStarts || '').slice(0, 10);
            const ends = String(offer?.DateEnds || '').slice(0, 10);
            return (!starts || starts <= current) && (!ends || ends >= current);
        };
        const readBasket = async () => {
            const html = await post('/Home/QuickCart', {});
            const doc = parseHtml(html);
            const items = [];
            for (const checkbox of doc.querySelectorAll(
                'input[data-basketitem][data-itemid]')) {
                const row = checkbox.closest('tr');
                const cells = Array.from(row?.querySelectorAll(':scope > td') || []);
                const itemId = String(checkbox.dataset.itemid || '');
                const detailHtml = await post('/Sales/ItemDetail', { ItemID: itemId });
                const detailDoc = parseHtml(detailHtml);
                const item = decode(detailDoc.querySelector('#accordion')?.getAttribute('data-item'));
                if (!item?.Barcode) continue;
                items.push({
                    barcode: String(item.Barcode),
                    quantity: Math.max(0, Math.trunc(parseNumber(cells[5]?.textContent))),
                    name: compact(cells[1]?.textContent || item.Name),
                    basketItemId: String(checkbox.dataset.basketitem || ''),
                    itemId
                });
            }
            return items;
        };
        const fetchProduct = async barcode => {
            const searchHtml = await post('/Sales/SearchItems', {
                SearchText: barcode,
                OnlyStock: false,
                SearchContains: false,
                SelectedClass: 3,
                Sorter: 0,
                ManufacturerID: 0,
                WithEQ: false,
                RequestedPage: 1,
                BeforeSearchRequest: ''
            });
            const searchDoc = parseHtml(searchHtml);
            const candidates = Array.from(
                searchDoc.querySelectorAll('tr[data-itemstring]'))
                .map(row => decode(row.dataset.itemstring))
                .filter(Boolean);
            const item = candidates.find(candidate => String(candidate.Barcode) === barcode);
            if (!item) throw new Error(`${barcode} barkodlu Alliance ürünü bulunamadı.`);

            const detailHtml = await post('/Sales/ItemDetail', { ItemID: item.ID });
            const detailDoc = parseHtml(detailHtml);
            const accordion = detailDoc.querySelector('#accordion');
            const itemString = accordion?.getAttribute('data-item') || '';
            const detailItem = decode(itemString) || item;
            const offers = Array.from(detailDoc.querySelectorAll(
                '#tblKampanyalar input[name="offer"]'))
                .map(input => {
                    const offer = decode(input.dataset.offer);
                    if (!offer) return null;
                    const cells = Array.from(input.closest('tr')?.querySelectorAll(':scope > td') || []);
                    return {
                        ...offer,
                        encoded: input.dataset.offer,
                        maturityText: compact(cells[1]?.textContent),
                        minimumText: compact(cells[2]?.textContent),
                        freeGoodsText: compact(cells[3]?.textContent),
                        institutionDiscountText: compact(cells[4]?.textContent),
                        commercialDiscountText: compact(cells[5]?.textContent),
                        netPriceText: compact(cells[6]?.textContent)
                    };
                }).filter(Boolean);
            return { item: detailItem, itemString, offers };
        };
        const chooseOffer = (product, quantity) => product.offers
            .filter(isOfferActive)
            .filter(offer => Number(offer.MinQuantity || 0) <= quantity)
            .sort((left, right) =>
                Number(right.EQRate || 0) - Number(left.EQRate || 0)
                || Number(left.NetPrice || Number.MAX_VALUE)
                    - Number(right.NetPrice || Number.MAX_VALUE))[0] || null;
        const addOrUpdate = async (barcode, quantity) => {
            const product = await fetchProduct(barcode);
            const offer = chooseOffer(product, quantity);
            if (!product.itemString || !offer?.encoded)
                throw new Error(`${barcode} barkodu için geçerli satış koşulu bulunamadı.`);
            const response = await post('/Sales/AddItemToBasket', {
                ItemString: product.itemString,
                OfferString: offer.encoded,
                Quantity: quantity,
                SupplierControlParam: false
            });
            if (!response?.Result)
                throw new Error(response?.Message || `${barcode} sepete eklenemedi.`);
            return response;
        };
        """;

    private static readonly string[] ForbiddenNonPurchaseMarkers =
    [
        "/Sales/SubmitBasket",
        "/Sales/BasketToSalesOrder",
        "SumbitBasket",
        "SubmitBasketRequest",
        "BasketToSalesOrder"
    ];

    private static readonly HashSet<string> AllowedNonPurchaseEndpoints =
    [
        "/Home/QuickCart",
        "/Sales/SearchItems",
        "/Sales/ItemDetail",
        "/Sales/AddItemToBasket",
        "/Sales/RemoveBasketItems"
    ];

    private static readonly HashSet<string> AllowedPurchaseEndpoints =
    [
        .. AllowedNonPurchaseEndpoints,
        "/Sales/BasketToSalesOrder"
    ];

    private readonly SemaphoreSlim browserGate = new(1, 1);
    private readonly string expectedHost = new Uri(HomeUrl).Host;
    private Window? browserWindow;
    private WebView2? browser;
    private bool debugBrowserVisible;

    private AllianceWebDepotService()
    {
        Status = new AllianceConnectionStatus(
            AllianceConnectionState.Disconnected,
            "Alliance e-Sipariş bağlantısı başlatılmadı.",
            DateTimeOffset.Now);
    }

    public static AllianceWebDepotService Instance { get; } = new();

    public AllianceConnectionStatus Status { get; private set; }

    public event EventHandler<AllianceConnectionStatus>? StatusChanged;

    public Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => OpenBrowserCoreAsync(cancellationToken));

    public Task<bool> LoginAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => LoginCoreAsync(cancellationToken));

    public Task<AllianceMedicationPriceInfo> GetMedicationPriceInfoAsync(
        string barcode,
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => GetMedicationPriceInfoCoreAsync(barcode, cancellationToken));

    public Task<IReadOnlyList<AllianceBasketItem>> GetBasketAsync(
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
                    AllianceConnectionState.Failed,
                    $"Alliance tarayıcısı açılamadı: {exception.Message}");
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
                Report(AllianceConnectionState.Connecting, "Alliance tarayıcısı yeniden başlatılıyor.");
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
                    authenticated ? AllianceConnectionState.Connected : AllianceConnectionState.Disconnected,
                    authenticated
                        ? "Alliance tarayıcısı yeniden başlatıldı ve oturum açıldı."
                        : "Alliance tarayıcısı yeniden başlatıldı; oturum açılamadı.");
                return authenticated;
            }
            catch (Exception exception)
            {
                Report(
                    AllianceConnectionState.Failed,
                    $"Alliance tarayıcısı yeniden başlatılamadı: {exception.Message}");
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
            return await EnsureAuthenticatedCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Report(AllianceConnectionState.Failed, "Alliance tarayıcı açma işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(AllianceConnectionState.Failed, $"Alliance tarayıcısı açılamadı: {exception.Message}");
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
            Report(AllianceConnectionState.Failed, "Alliance girişi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(AllianceConnectionState.Failed, $"Alliance girişi başarısız: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<AllianceMedicationPriceInfo> GetMedicationPriceInfoCoreAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        var barcodeJson = JsonSerializer.Serialize(barcode);
        var script = """
            (async () => {
            """ + CommandHelpersScript + $$"""
                try {
                    const product = await fetchProduct({{barcodeJson}});
                    const activeOffers = product.offers.filter(isOfferActive);
                    const offer = chooseOffer(product, 1);
                    if (!offer)
                        return JSON.stringify({
                            success: false,
                            error: 'Geçerli Alliance satış koşulu bulunamadı.'
                        });
                    const taxRate = Number(product.item?.PriceTag?.TaxRate || 0);
                    const activePrice = Number(offer.NetPrice || 0) * (1 + taxRate / 100);
                    const campaigns = activeOffers.map(candidate => {
                        const parts = [compact(candidate.Name)];
                        if (candidate.RewardName) parts.push(compact(candidate.RewardName));
                        parts.push(`min. ${Number(candidate.MinQuantity || 0)}`);
                        if (Number(candidate.ExtraQuantityForMinQuantity || 0) > 0)
                            parts.push(`MF ${Number(candidate.ExtraQuantityForMinQuantity)}`);
                        return parts.filter(Boolean).join(' | ');
                    }).filter(Boolean);
                    const discounts = [];
                    if (offer.institutionDiscountText)
                        discounts.push(`Kurum: ${offer.institutionDiscountText}`);
                    if (offer.commercialDiscountText)
                        discounts.push(`Ticari: ${offer.commercialDiscountText}`);
                    if (discounts.length === 0) {
                        const list = Number(offer.Price || 0);
                        const net = Number(offer.NetPrice || 0);
                        const effective = list > 0 ? (1 - net / list) * 100 : 0;
                        if (effective > 0.005)
                            discounts.push(`%${effective.toLocaleString('tr-TR', {
                                minimumFractionDigits: 0,
                                maximumFractionDigits: 2
                            })}`);
                    }
                    const maturities = Array.from(new Set(activeOffers
                        .map(candidate => Number(candidate.DaysDue || 0))
                        .filter(value => value > 0)))
                        .map(value => `${value} gün`);
                    return JSON.stringify({
                        success: true,
                        barcode: String(product.item.Barcode),
                        productName: compact(product.item.Name),
                        activeVatIncludedDepotPrice: Math.round(activePrice * 10000) / 10000,
                        campaign: campaigns.length ? campaigns.join('; ') : 'Yok',
                        discount: discounts.length ? discounts.join('; ') : 'Yok',
                        maturity: maturities.length ? maturities.join(', ') : 'Yok'
                    });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        error: error?.message || 'Fiyat bilgisi okunamadı.'
                    });
                }
            })()
            """;

        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken))
                throw new InvalidOperationException("Alliance oturumu açılamadı.");
            await EnsureQuickOrderPageAsync(cancellationToken);
            Report(AllianceConnectionState.Connecting, $"Alliance {barcode} fiyatı okunuyor.");
            var response = await ExecuteCommandAsync<PriceResponse>(script, cancellationToken);
            EnsureCommandSucceeded(response);
            Report(AllianceConnectionState.Connected, $"Alliance {barcode} fiyatı okundu.");
            return new AllianceMedicationPriceInfo(
                response.Barcode,
                response.ProductName,
                response.ActiveVatIncludedDepotPrice,
                response.Campaign,
                response.Discount,
                response.Maturity);
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<IReadOnlyList<AllianceBasketItem>> GetBasketCoreAsync(
        CancellationToken cancellationToken)
    {
        const string script = """
            (async () => {
            """ + CommandHelpersScript + """
                try {
                    const items = await readBasket();
                    return JSON.stringify({ success: true, items });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        error: error?.message || 'Alliance sepeti okunamadı.'
                    });
                }
            })()
            """;

        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken))
                throw new InvalidOperationException("Alliance oturumu açılamadı.");
            await EnsureQuickOrderPageAsync(cancellationToken);
            Report(AllianceConnectionState.Connecting, "Alliance sepeti okunuyor.");
            var response = await ExecuteCommandAsync<BasketResponse>(script, cancellationToken);
            EnsureCommandSucceeded(response);
            var items = response.Items
                .GroupBy(item => item.Barcode, StringComparer.Ordinal)
                .Select(group => new AllianceBasketItem(
                    group.Key,
                    group.Sum(item => item.Quantity)))
                .OrderBy(item => item.Barcode, StringComparer.Ordinal)
                .ToArray();
            Report(
                AllianceConnectionState.Connected,
                items.Length == 0
                    ? "Alliance sepeti boş."
                    : $"Alliance sepetinde {items.Length} farklı barkod bulundu.");
            return items;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<bool> AddBasketItemCoreAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken)
    {
        ValidateBarcodeAndAmount(barcode, amount);
        var barcodeJson = JsonSerializer.Serialize(barcode);
        var script = """
            (async () => {
            """ + CommandHelpersScript + $$"""
                try {
                    const before = await readBasket();
                    const current = before
                        .filter(item => item.barcode === {{barcodeJson}})
                        .reduce((sum, item) => sum + item.quantity, 0);
                    const target = current + {{amount.ToString(CultureInfo.InvariantCulture)}};
                    await addOrUpdate({{barcodeJson}}, target);
                    const after = await readBasket();
                    const actual = after
                        .filter(item => item.barcode === {{barcodeJson}})
                        .reduce((sum, item) => sum + item.quantity, 0);
                    return JSON.stringify({ success: true, completed: actual === target });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        completed: false,
                        error: error?.message || 'Ürün sepete eklenemedi.'
                    });
                }
            })()
            """;
        return await RunMutationAsync(
            $"Alliance {barcode} sepete ekleniyor.",
            $"Alliance {barcode} barkodundan {amount} adet sepete eklendi.",
            script,
            cancellationToken);
    }

    private async Task<bool> DecreaseBasketItemCoreAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken)
    {
        ValidateBarcodeAndAmount(barcode, amount);
        var barcodeJson = JsonSerializer.Serialize(barcode);
        var script = """
            (async () => {
            """ + CommandHelpersScript + $$"""
                try {
                    const before = await readBasket();
                    const lines = before.filter(item => item.barcode === {{barcodeJson}});
                    const current = lines.reduce((sum, item) => sum + item.quantity, 0);
                    if (current <= 0)
                        return JSON.stringify({ success: true, completed: false });
                    const target = Math.max(0,
                        current - {{amount.ToString(CultureInfo.InvariantCulture)}});
                    if (target === 0) {
                        await post('/Sales/RemoveBasketItems', {
                            Clear: false,
                            BasketItemStrings: lines.map(item => item.basketItemId)
                        });
                    } else {
                        if (lines.length > 1) {
                            await post('/Sales/RemoveBasketItems', {
                                Clear: false,
                                BasketItemStrings: lines.map(item => item.basketItemId)
                            });
                        }
                        await addOrUpdate({{barcodeJson}}, target);
                    }
                    const after = await readBasket();
                    const actual = after
                        .filter(item => item.barcode === {{barcodeJson}})
                        .reduce((sum, item) => sum + item.quantity, 0);
                    return JSON.stringify({ success: true, completed: actual === target });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        completed: false,
                        error: error?.message || 'Sepet miktarı azaltılamadı.'
                    });
                }
            })()
            """;
        return await RunMutationAsync(
            $"Alliance {barcode} miktarı azaltılıyor.",
            $"Alliance {barcode} miktarı {amount} azaltıldı.",
            script,
            cancellationToken);
    }

    private async Task<bool> RemoveBasketItemCoreAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        var barcodeJson = JsonSerializer.Serialize(barcode);
        var script = """
            (async () => {
            """ + CommandHelpersScript + $$"""
                try {
                    const before = await readBasket();
                    const lines = before.filter(item => item.barcode === {{barcodeJson}});
                    if (lines.length === 0)
                        return JSON.stringify({ success: true, completed: false });
                    const response = await post('/Sales/RemoveBasketItems', {
                        Clear: false,
                        BasketItemStrings: lines.map(item => item.basketItemId)
                    });
                    if (!response?.Result)
                        throw new Error(response?.Message || 'Ürün sepetten kaldırılamadı.');
                    const after = await readBasket();
                    return JSON.stringify({
                        success: true,
                        completed: !after.some(item => item.barcode === {{barcodeJson}})
                    });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        completed: false,
                        error: error?.message || 'Ürün sepetten kaldırılamadı.'
                    });
                }
            })()
            """;
        return await RunMutationAsync(
            $"Alliance {barcode} sepetten kaldırılıyor.",
            $"Alliance {barcode} sepetten kaldırıldı.",
            script,
            cancellationToken);
    }

    private async Task<bool> ClearBasketCoreAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (async () => {
            """ + CommandHelpersScript + """
                try {
                    const response = await post('/Sales/RemoveBasketItems', {
                        Clear: true,
                        BasketItemStrings: []
                    });
                    if (!response?.Result)
                        throw new Error(response?.Message || 'Alliance sepeti temizlenemedi.');
                    const after = await readBasket();
                    return JSON.stringify({ success: true, completed: after.length === 0 });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        completed: false,
                        error: error?.message || 'Alliance sepeti temizlenemedi.'
                    });
                }
            })()
            """;
        return await RunMutationAsync(
            "Alliance sepeti temizleniyor.",
            "Alliance sepeti temizlendi.",
            script,
            cancellationToken);
    }

    private async Task<bool> RunMutationAsync(
        string progressMessage,
        string successMessage,
        string script,
        CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken)) return false;
            await EnsureQuickOrderPageAsync(cancellationToken);
            await DismissBlockingPopupsAsync();
            Report(AllianceConnectionState.Connecting, progressMessage);
            var response = await ExecuteCommandAsync<MutationResponse>(script, cancellationToken);
            EnsureCommandSucceeded(response);
            Report(
                response.Completed ? AllianceConnectionState.Connected : AllianceConnectionState.Failed,
                response.Completed ? successMessage : "Alliance sepet işlemi doğrulanamadı.");
            return response.Completed;
        }
        catch (OperationCanceledException)
        {
            Report(AllianceConnectionState.Failed, "Alliance sepet işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(
                AllianceConnectionState.Failed,
                $"Alliance sepet işlemi başarısız: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<bool> PurchaseCoreAsync(
        IReadOnlyDictionary<string, int>? expectedBasket,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AllianceBasketItem> snapshot;
        try
        {
            snapshot = await GetBasketCoreAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Report(
                AllianceConnectionState.Failed,
                $"Alliance satın alma öncesi sepeti okunamadı: {exception.Message}");
            return false;
        }

        var actualBasket = snapshot.ToDictionary(
            item => item.Barcode,
            item => item.Quantity,
            StringComparer.Ordinal);
        if (actualBasket.Count == 0)
        {
            Report(AllianceConnectionState.Failed, "Alliance sepeti boş; sipariş verilmedi.");
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
                AllianceConnectionState.Failed,
                "Alliance sepeti beklenen barkod ve miktarlarla eşleşmedi; sipariş verilmedi.");
            return false;
        }

        var expectedJson = JsonSerializer.Serialize(requiredBasket);
        var script = """
            (async () => {
            """ + CommandHelpersScript + $$"""
                try {
                    const expected = {{expectedJson}};
                    const before = await readBasket();
                    const actual = {};
                    for (const item of before)
                        actual[item.barcode] = (actual[item.barcode] || 0) + item.quantity;
                    const expectedKeys = Object.keys(expected).sort();
                    const actualKeys = Object.keys(actual).sort();
                    const matches = expectedKeys.length === actualKeys.length
                        && expectedKeys.every((barcode, index) =>
                            barcode === actualKeys[index]
                            && Number(expected[barcode]) === Number(actual[barcode]));
                    if (!matches)
                        return JSON.stringify({ success: true, completed: false });

                    const quickCart = parseHtml(await post('/Home/QuickCart', {}));
                    const shippingMethods = Array.from(
                        quickCart.querySelectorAll('select[name="OrderPriority"]'))
                        .map(select => select.value)
                        .join(',');
                    const response = await post('/Sales/BasketToSalesOrder', {
                        Note: '',
                        IsDuty: false,
                        CloneAfterAfterProcessing: false,
                        ShippingMethods: shippingMethods
                    });
                    if (response?.Result === true)
                        throw new Error(response.Message || 'Alliance siparişi oluşturulamadı.');

                    const deadline = Date.now() + 20000;
                    do {
                        const after = await readBasket();
                        if (after.length === 0)
                            return JSON.stringify({ success: true, completed: true });
                        await new Promise(resolve => setTimeout(resolve, 250));
                    } while (Date.now() < deadline);
                    return JSON.stringify({ success: true, completed: false });
                } catch (error) {
                    return JSON.stringify({
                        success: false,
                        completed: false,
                        error: error?.message || 'Alliance siparişi oluşturulamadı.'
                    });
                }
            })()
            """;

        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken)) return false;
            await EnsureQuickOrderPageAsync(cancellationToken);
            Report(AllianceConnectionState.Connecting, "Alliance siparişi yeniden doğrulanıyor.");
            var response = await ExecutePurchaseCommandAsync<MutationResponse>(
                script,
                cancellationToken);
            EnsureCommandSucceeded(response);
            Report(
                response.Completed ? AllianceConnectionState.Connected : AllianceConnectionState.Failed,
                response.Completed
                    ? "Alliance siparişi başarıyla oluşturuldu."
                    : "Alliance sipariş sonucu doğrulanamadı; webdepo sipariş kutusunu kontrol edin.");
            return response.Completed;
        }
        catch (Exception exception)
        {
            Report(
                AllianceConnectionState.Failed,
                $"Alliance satın alma işlemi başarısız: {exception.Message}");
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

    private async Task EnsureQuickOrderPageAsync(CancellationToken cancellationToken)
    {
        var current = RequireBrowser().Source;
        if (!current.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
            || !current.AbsolutePath.Equals("/Sales/QuickOrder", StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(
                $"https://{expectedHost}/Sales/QuickOrder",
                cancellationToken);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DismissBlockingPopupsAsync();
            if (await ExecuteBooleanAsync(
                    "typeof window.jQuery === 'function' && !!document.querySelector('#searchText')"))
                return;
            await Task.Delay(120, cancellationToken);
        }
        throw new TimeoutException("Alliance hızlı sipariş sayfası zamanında hazırlanamadı.");
    }

    private static void ValidateBarcode(string barcode)
    {
        if (barcode.Length != 13 || barcode.Any(character => !char.IsAsciiDigit(character)))
            throw new ArgumentException("Barkod 13 haneli olmalıdır.", nameof(barcode));
    }

    private static void ValidateBarcodeAndAmount(string barcode, int amount)
    {
        ValidateBarcode(barcode);
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Miktar sıfırdan büyük olmalıdır.");
    }

    private async Task<bool> EnsureAuthenticatedCoreAsync(CancellationToken cancellationToken)
    {
        Report(AllianceConnectionState.Connecting, "Alliance e-Sipariş oturumu açılıyor.");
        await EnsureBrowserAsync(cancellationToken);
        if (!IsCurrentAlliancePage()) await NavigateAsync(HomeUrl, cancellationToken);

        var pageState = await WaitForPageStateAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (pageState == AuthenticationPageState.Authenticated)
        {
            await DismissBlockingPopupsAsync();
            Report(AllianceConnectionState.Connected, "Mevcut Alliance e-Sipariş oturumu kullanılıyor.");
            return true;
        }
        if (pageState != AuthenticationPageState.LoginForm)
        {
            await NavigateAsync(HomeUrl, cancellationToken);
            pageState = await WaitForPageStateAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        if (pageState == AuthenticationPageState.Authenticated)
        {
            await DismissBlockingPopupsAsync();
            Report(AllianceConnectionState.Connected, "Mevcut Alliance e-Sipariş oturumu tanındı.");
            return true;
        }
        if (pageState != AuthenticationPageState.LoginForm)
        {
            Report(AllianceConnectionState.Failed, "Alliance giriş sayfası tanınamadı.");
            return false;
        }

        var settings = ApplicationSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.AlliancePharmacyCode)
            || string.IsNullOrWhiteSpace(settings.AllianceUsername)
            || string.IsNullOrEmpty(settings.AlliancePassword))
        {
            Report(
                AllianceConnectionState.Disconnected,
                "Alliance eczane kodu, kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");
            return false;
        }

        await FillLoginFieldAsync("#pharmacyCode", settings.AlliancePharmacyCode, "eczane kodu");
        await FillLoginFieldAsync("#Customer_username", settings.AllianceUsername, "kullanıcı adı");
        await FillLoginFieldAsync("#Customer_password", settings.AlliancePassword, "parola");
        if (!await SubmitLoginAndWaitAsync(cancellationToken))
        {
            Report(
                AllianceConnectionState.Failed,
                "Alliance girişi doğrulanamadı. Üç giriş bilgisini kontrol edin.");
            return false;
        }

        await DismissBlockingPopupsAsync();
        Report(AllianceConnectionState.Connected, "Alliance e-Sipariş girişi başarılı.");
        return true;
    }

    private async Task<bool> SubmitLoginAndWaitAsync(CancellationToken cancellationToken)
    {
        var clicked = await ExecuteBooleanAsync(
            """
            (() => {
                const button = document.querySelector('button.Customer_login__button');
                if (!button || button.disabled || button.getClientRects().length === 0) return false;
                button.click();
                return true;
            })()
            """);
        if (!clicked) return false;

        var deadline = DateTime.UtcNow + BrowserTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAuthenticatedAsync()) return true;
            if (await HasVisibleLoginErrorAsync()) return false;
            await Task.Delay(150, cancellationToken);
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
        var profileFolderName = executableName.Equals("pharmacyApp", StringComparison.OrdinalIgnoreCase)
            ? "AllianceWebView2"
            : $"AllianceWebView2-{executableName}";
        var userDataFolder = Path.Combine(
            pharmacyApp.Infrastructure.AppPaths.DataDirectory,
            profileFolderName);
        Directory.CreateDirectory(userDataFolder);

        browser = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder }
        };
        browserWindow = new Window
        {
            Title = "Alliance/Cencora/Hedef Arka Plan Tarayıcısı",
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
            Report(AllianceConnectionState.Disconnected, "Alliance tarayıcısı kapatıldı.");
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
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var requestedUri)
                    && requestedUri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
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
            if (args.Uri.Equals(url, StringComparison.OrdinalIgnoreCase))
                navigationId = args.NavigationId;
        };
        completed = (_, args) =>
        {
            if (navigationId is null || args.NavigationId != navigationId.Value) return;
            if (args.IsSuccess || args.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown)
                completion.TrySetResult();
            else
                completion.TrySetException(new InvalidOperationException(
                    $"Alliance sayfası açılamadı: {args.WebErrorStatus}."));
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
            throw new InvalidOperationException($"Alliance {fieldName} alanı doldurulamadı.");
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
        """
        (() => {
            const fields = [
                document.querySelector('#pharmacyCode'),
                document.querySelector('#Customer_username'),
                document.querySelector('#Customer_password')
            ];
            return fields.every(field => field && field.getClientRects().length > 0)
                && !!document.querySelector('button.Customer_login__button');
        })()
        """);

    private Task<bool> HasVisibleLoginErrorAsync() => ExecuteBooleanAsync(
        """
        (() => {
            const selectors = [
                '.validation-summary-errors',
                '.field-validation-error',
                '.alert-danger',
                '.toast-error',
                '.swal2-error'
            ];
            return selectors.some(selector => Array.from(document.querySelectorAll(selector))
                .some(element => element.getClientRects().length > 0
                    && (element.textContent || '').trim().length > 0));
        })()
        """);

    private Task<bool> IsAuthenticatedAsync()
    {
        var expectedHostJson = JsonSerializer.Serialize(expectedHost);
        return ExecuteBooleanAsync($$"""
            (() => {
                if (location.hostname.toLocaleLowerCase('en-US') !== {{expectedHostJson}})
                    return false;
                const loginFields = [
                    document.querySelector('#pharmacyCode'),
                    document.querySelector('#Customer_username'),
                    document.querySelector('#Customer_password')
                ];
                if (loginFields.some(field => field && field.getClientRects().length > 0))
                    return false;
                const bodyText = document.body?.innerText?.toLocaleLowerCase('tr-TR') ?? '';
                const controls = Array.from(document.querySelectorAll('a, button'));
                const hasLogout = controls.some(control =>
                    /^(çıkış|cikis|oturumu kapat|güvenli çıkış)$/i.test(
                        (control.textContent || '').trim()));
                const hasOrderingUi = bodyText.includes('sipariş')
                    && controls.length >= 3;
                return document.readyState === 'complete' && (hasLogout || hasOrderingUi);
            })()
            """);
    }

    private bool IsCurrentAlliancePage() =>
        browser?.Source is { } source
        && source.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase);

    private async Task<T> ExecuteCommandAsync<T>(
        string script,
        CancellationToken cancellationToken)
    {
        EnsureNonPurchaseScriptCannotPurchase(script);
        var json = await ExecutePromiseStringAsync(script, cancellationToken);
        return JsonSerializer.Deserialize<T>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Alliance komut yanıtı çözümlenemedi.");
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
               ?? throw new InvalidOperationException("Alliance satın alma yanıtı çözümlenemedi.");
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
                var message = JsonSerializer.Deserialize<BrowserCommandMessage>(
                    args.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (message?.Channel != "pharmacy-alliance"
                    || message.CommandId != commandId)
                    return;
                if (message.Succeeded)
                    completion.TrySetResult(message.Value ?? string.Empty);
                else
                    completion.TrySetException(new InvalidOperationException(
                        string.IsNullOrWhiteSpace(message.Error)
                            ? "Alliance web komutu tamamlanamadı."
                            : message.Error));
            }
            catch (InvalidOperationException)
            {
                // Ignore unrelated non-string messages posted by the depot page.
            }
            catch (JsonException)
            {
                // Ignore unrelated string messages posted by the depot page.
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
                "channel: 'pharmacy-alliance', commandId, succeeded: true, ",
                "value: typeof value === 'string' ? value : JSON.stringify(value) })))",
                ".catch(error => window.chrome.webview.postMessage(JSON.stringify({",
                "channel: 'pharmacy-alliance', commandId, succeeded: false, ",
                "error: error?.message || String(error) }))); return true; })()");
            if (!await ExecuteBooleanAsync(wrapper))
                throw new InvalidOperationException("Alliance web komutu başlatılamadı.");
            return await completion.Task.WaitAsync(BrowserTimeout, cancellationToken);
        }
        finally
        {
            currentBrowser.CoreWebView2.WebMessageReceived -= received;
        }
    }

    private static void EnsureNonPurchaseScriptCannotPurchase(string script)
    {
        if (ForbiddenNonPurchaseMarkers.Any(marker =>
                script.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Alliance sepet komutu satın alma işlemi içeremez.");
        EnsureOnlyAllowedEndpoints(script, AllowedNonPurchaseEndpoints, "sepet");
    }

    private static void EnsurePurchaseScriptIsRestricted(string script)
    {
        const string purchaseEndpoint = "/Sales/BasketToSalesOrder";
        if (!script.Contains(purchaseEndpoint, StringComparison.Ordinal))
            throw new InvalidOperationException("Alliance satın alma komutu bulunamadı.");
        if (script.Contains("/Sales/SubmitBasket", StringComparison.OrdinalIgnoreCase)
            || script.Contains("SumbitBasket", StringComparison.OrdinalIgnoreCase)
            || script.Contains("SubmitBasketRequest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Alliance satın alma komutu izin verilmeyen ek onay işlemleri içeriyor.");
        EnsureOnlyAllowedEndpoints(script, AllowedPurchaseEndpoints, "satın alma");
    }

    private static void EnsureOnlyAllowedEndpoints(
        string script,
        IReadOnlySet<string> allowedEndpoints,
        string operationName)
    {
        foreach (Match match in Regex.Matches(
                     script,
                     "post\\(\\s*['\"](?<endpoint>[^'\"]+)"))
        {
            var endpoint = match.Groups["endpoint"].Value;
            if (!allowedEndpoints.Contains(endpoint))
                throw new InvalidOperationException(
                    $"Alliance {operationName} komutunda izin verilmeyen uç nokta kullanıldı: {endpoint}.");
        }
    }

    private static void EnsureCommandSucceeded(CommandResponse response)
    {
        if (response.Success) return;
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(response.Error)
                ? "Alliance web komutu başarısız oldu."
                : response.Error);
    }

    private async Task<bool> ExecuteBooleanAsync(string script)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DismissBlockingPopupsAsync()
    {
        if (browser?.CoreWebView2 is null) return;
        await browser.CoreWebView2.ExecuteScriptAsync(PopupCloserScript);
    }

    private WebView2 RequireBrowser() => browser?.CoreWebView2 is not null
        ? browser
        : throw new InvalidOperationException("Alliance arka plan tarayıcısı başlatılmadı.");

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

    private void Report(AllianceConnectionState state, string message)
    {
        Status = new AllianceConnectionStatus(state, message, DateTimeOffset.Now);
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

    private class CommandResponse
    {
        public bool Success { get; init; }
        public string Error { get; init; } = string.Empty;
    }

    private sealed class MutationResponse : CommandResponse
    {
        public bool Completed { get; init; }
    }

    private sealed class BasketResponse : CommandResponse
    {
        public List<BasketLineResponse> Items { get; init; } = [];
    }

    private sealed class BasketLineResponse
    {
        public string Barcode { get; init; } = string.Empty;
        public int Quantity { get; init; }
    }

    private sealed class PriceResponse : CommandResponse
    {
        public string Barcode { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public decimal ActiveVatIncludedDepotPrice { get; init; }
        public string Campaign { get; init; } = string.Empty;
        public string Discount { get; init; } = string.Empty;
        public string Maturity { get; init; } = string.Empty;
    }

    private sealed class BrowserCommandMessage
    {
        public string? Channel { get; init; }
        public string? CommandId { get; init; }
        public bool Succeeded { get; init; }
        public string? Value { get; init; }
        public string? Error { get; init; }
    }

    private enum AuthenticationPageState
    {
        Unknown,
        Authenticated,
        LoginForm
    }
}
