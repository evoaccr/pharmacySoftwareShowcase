using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.WebDepots.Selcuk;

public interface ISelcukWebDepotService
{
    Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(CancellationToken cancellationToken = default);
    Task<SelcukMedicationPriceInfo> GetMedicationPriceInfoAsync(
        string barcode,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SelcukBasketItem>> GetBasketAsync(CancellationToken cancellationToken = default);
    Task<bool> AddBasketItemAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken = default);
    Task<bool> DecreaseBasketItemAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken = default);
    Task<bool> RemoveBasketItemAsync(string barcode, CancellationToken cancellationToken = default);
    Task<bool> ClearBasketAsync(CancellationToken cancellationToken = default);
    Task<bool> PurchaseAsync(CancellationToken cancellationToken = default);
    Task<bool> PurchaseAsync(
        IReadOnlyDictionary<string, int> expectedBasket,
        CancellationToken cancellationToken = default);
}

public sealed record SelcukBasketItem(string Barcode, int Quantity);

public sealed record SelcukMedicationPriceInfo(
    string Barcode,
    string ProductName,
    decimal ActiveVatIncludedDepotPrice,
    string Campaign,
    string Discount,
    string Maturity);

public enum SelcukConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed
}

public sealed record SelcukConnectionStatus(
    SelcukConnectionState State,
    string Message,
    DateTimeOffset Timestamp);

public sealed class SelcukWebDepotService : ISelcukWebDepotService
{
    public const string HomeUrl = "https://webdepo.selcukecza.com.tr/";
    public const string LoginUrl = "https://webdepo.selcukecza.com.tr/Login.aspx";
    public const string BasketUrl = "https://webdepo.selcukecza.com.tr/Siparis/sepet.aspx";
    public const string QuickOrderUrl = "https://webdepo.selcukecza.com.tr/Siparis/hizlisiparis.aspx";
    public const string OrderReviewUrl = "https://webdepo.selcukecza.com.tr/Siparis/SiparisOnay.aspx";
    private const string PopupCloserScript = """
        (() => {
            const stateKey = '__pharmacySelcukPopupCloser';
            const existing = window[stateKey];
            if (existing?.dismiss) {
                existing.dismiss();
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
                '.popup.open',
                '[class*="popup"][class*="show"]',
                '[class*="popup"][class*="open"]'
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
                '.dw__close',
                'button.close',
                '[class*="__close"]',
                '[class*="-close"]'
            ].join(',');
            const closeText = /^(×|x|close|kapat|iptal|vazgeç|hayır|no)$/i;
            const confirmText = /^(evet|yes|ok|tamam|onayla|sil|siparişi onayla)$/i;

            const isVisible = element => {
                if (!(element instanceof HTMLElement) || element.getClientRects().length === 0) return false;
                const style = getComputedStyle(element);
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && Number(style.opacity || '1') > 0.01
                    && style.pointerEvents !== 'none';
            };
            const isConfirmation = popup => Array.from(
                popup.querySelectorAll(
                    '[data-bb-handler="confirm"], .bootbox-accept, button, input[type="button"], input[type="submit"]'))
                .some(control => confirmText.test((control.textContent || control.value || '').trim()));
            const dismiss = () => {
                let dismissed = 0;
                for (const popup of document.querySelectorAll(popupSelector)) {
                    if (!isVisible(popup)) continue;
                    const controls = Array.from(popup.querySelectorAll(closeSelector));
                    let close = controls.find(isVisible);
                    if (!close && !isConfirmation(popup)) {
                        close = Array.from(popup.querySelectorAll('button, a, input[type="button"]'))
                            .find(control => isVisible(control)
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
            const start = () => {
                dismiss();
                const observer = new MutationObserver(scheduleDismiss);
                observer.observe(document.documentElement, {
                    attributes: true,
                    childList: true,
                    subtree: true,
                    attributeFilter: ['class', 'style', 'open', 'aria-hidden']
                });
                window[stateKey].observer = observer;
            };

            window[stateKey] = { dismiss, observer: null };
            if (document.documentElement) start();
            else document.addEventListener('DOMContentLoaded', start, { once: true });
            return true;
        })()
        """;
    private const string PopupDismissScript = """
        (() => {
            document.querySelector('#dwClose')?.click();
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
                '.dw__close',
                'button.close',
                '[class*="__close"]',
                '[class*="-close"]'
            ].join(',');
            const closeText = /^(×|x|close|kapat|iptal|vazgeç|hayır|no)$/i;
            const confirmText = /^(evet|yes|ok|tamam|onayla|sil|siparişi onayla)$/i;
            const isDisplayed = element => {
                if (!element?.isConnected || element.getClientRects().length === 0) return false;
                const style = getComputedStyle(element);
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && Number(style.opacity || '1') > 0.01;
            };

            let dismissed = 0;
            for (const popup of document.querySelectorAll(popupSelector)) {
                if (!isDisplayed(popup)) continue;
                let close = Array.from(popup.querySelectorAll(closeSelector)).find(isDisplayed);
                if (!close) {
                    const controls = Array.from(
                        popup.querySelectorAll('button, a, input[type="button"], input[type="submit"]'));
                    const isConfirmation = controls.some(control =>
                        confirmText.test((control.textContent || control.value || '').trim()));
                    if (!isConfirmation) {
                        close = controls.find(control => isDisplayed(control)
                            && closeText.test((control.textContent || control.value || '').trim()));
                    }
                }
                if (!close) continue;
                close.click();
                dismissed++;
            }
            return dismissed;
        })()
        """;
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(35);
    private readonly SemaphoreSlim browserGate = new(1, 1);
    private Window? browserWindow;
    private WebView2? browser;
    private bool debugBrowserVisible;

    private SelcukWebDepotService()
    {
    }

    public static SelcukWebDepotService Instance { get; } = new();

    public SelcukConnectionStatus Status { get; private set; } = new(
        SelcukConnectionState.Disconnected,
        "Selçuk Webdepo bağlantısı başlatılmadı.",
        DateTimeOffset.Now);

    public event EventHandler<SelcukConnectionStatus>? StatusChanged;

    public Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => OpenBrowserCoreAsync(cancellationToken));

    public Task<bool> LoginAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => LoginCoreAsync(cancellationToken));

    public Task<SelcukMedicationPriceInfo> GetMedicationPriceInfoAsync(
        string barcode,
        CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(() => GetMedicationPriceInfoCoreAsync(barcode, cancellationToken));

    public Task<IReadOnlyList<SelcukBasketItem>> GetBasketAsync(
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedBasket);
        var expectedSnapshot = ValidateAndCopyExpectedBasket(expectedBasket);
        return RunOnUiThreadAsync(() => PurchaseCoreAsync(expectedSnapshot, cancellationToken));
    }

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
                Report(SelcukConnectionState.Failed,
                    $"Selçuk Webdepo tarayıcısı açılamadı: {exception.Message}");
                return false;
            }
            finally
            {
                browserGate.Release();
            }
        });

    public Task RestartBrowserForDebugAsync(CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(async () =>
        {
            debugBrowserVisible = true;
            await browserGate.WaitAsync(cancellationToken);
            try
            {
                Report(SelcukConnectionState.Connecting, "Selçuk Webdepo tarayıcısı yeniden başlatılıyor.");
                if (browserWindow is not null)
                {
                    browserWindow.Close();
                }
                else
                {
                    browser?.Dispose();
                    browser = null;
                }

                await EnsureBrowserAsync(cancellationToken);
                ShowBrowserWindow(activate: true);
                await NavigateAsync(HomeUrl, cancellationToken);
                Report(
                    await IsAuthenticatedAsync()
                        ? SelcukConnectionState.Connected
                        : SelcukConnectionState.Disconnected,
                    "Selçuk Webdepo tarayıcısı yeniden başlatıldı.");
            }
            catch (Exception exception)
            {
                Report(SelcukConnectionState.Failed,
                    $"Selçuk Webdepo tarayıcısı yeniden başlatılamadı: {exception.Message}");
                throw;
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

    private async Task<bool> LoginCoreAsync(CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureAuthenticatedCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Report(SelcukConnectionState.Failed, "Selçuk Webdepo girişi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(SelcukConnectionState.Failed, $"Selçuk Webdepo girişi başarısız: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
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
            Report(SelcukConnectionState.Failed, "Selçuk Webdepo tarayıcısı açma işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(SelcukConnectionState.Failed, $"Selçuk Webdepo tarayıcısı açılamadı: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<IReadOnlyList<SelcukBasketItem>> GetBasketCoreAsync(
        CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken))
                throw new InvalidOperationException("Selçuk Webdepo oturumu açılamadı.");

            Report(SelcukConnectionState.Connecting, "Selçuk Webdepo sepeti okunuyor.");
            var rows = await ReadResolvedBasketRowsCoreAsync(cancellationToken);
            var result = rows
                .GroupBy(item => item.Barcode, StringComparer.Ordinal)
                .Select(group => new SelcukBasketItem(group.Key, group.Sum(row => row.Quantity)))
                .OrderBy(item => item.Barcode, StringComparer.Ordinal)
                .ToArray();
            Report(SelcukConnectionState.Connected,
                result.Length == 0
                    ? "Selçuk Webdepo sepeti okundu: sepet boş."
                    : $"Selçuk Webdepo sepeti okundu: {result.Length} farklı barkod bulundu.");
            return result;
        }
        catch (OperationCanceledException)
        {
            Report(SelcukConnectionState.Failed, "Selçuk Webdepo sepet okuma işlemi iptal edildi.");
            throw;
        }
        catch (Exception exception)
        {
            Report(SelcukConnectionState.Failed, $"Selçuk Webdepo sepeti okunamadı: {exception.Message}");
            throw;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<SelcukMedicationPriceInfo> GetMedicationPriceInfoCoreAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken))
                throw new InvalidOperationException("Selçuk Webdepo oturumu açılamadı.");

            Report(SelcukConnectionState.Connecting, "Selçuk Webdepo fiyat ve kampanya bilgisi okunuyor.");
            var productName = await SearchQuickOrderProductCoreAsync(barcode, cancellationToken);
            var descriptorJson = await ExecuteStringAsync(
                """
                (() => {
                    const priceLabel = Array.from(document.querySelectorAll('label'))
                        .find(label => label.textContent?.trim().toLocaleLowerCase('tr-TR')
                            === 'kdv dahil depocu fiyatı');
                    const priceContainer = priceLabel?.parentElement;
                    const priceText = priceContainer
                        ? (priceContainer.textContent ?? '')
                            .replace(priceLabel?.textContent ?? '', '')
                            .trim()
                        : '';
                    const selectedCondition = document.querySelector('input[name="rbKampanya"]:checked');
                    const cells = Array.from(selectedCondition?.closest('tr')?.cells ?? [])
                        .map(cell => cell.textContent?.trim() ?? '');
                    return JSON.stringify({
                        VatIncludedDepotPrice: priceText,
                        Campaign: cells[1] ?? '',
                        Discount: cells[3] ?? '',
                        Maturity: cells[5] ?? ''
                    });
                })()
                """);
            var descriptor = JsonSerializer.Deserialize<MedicationPriceDescriptor>(descriptorJson)
                ?? throw new InvalidOperationException("Selçuk fiyat ve kampanya bilgisi okunamadı.");
            if (string.IsNullOrWhiteSpace(descriptor.VatIncludedDepotPrice)
                || string.IsNullOrWhiteSpace(descriptor.Campaign)
                || string.IsNullOrWhiteSpace(descriptor.Discount)
                || string.IsNullOrWhiteSpace(descriptor.Maturity))
                throw new InvalidOperationException("Selçuk aktif satış koşulu çözümlenemedi.");

            var normalizedPrice = descriptor.VatIncludedDepotPrice
                .Replace("₺", string.Empty, StringComparison.Ordinal)
                .Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (!decimal.TryParse(
                    normalizedPrice,
                    NumberStyles.Number,
                    CultureInfo.GetCultureInfo("tr-TR"),
                    out var vatIncludedDepotPrice))
                throw new InvalidOperationException("Selçuk KDV dahil depocu fiyatı çözümlenemedi.");

            var result = new SelcukMedicationPriceInfo(
                barcode,
                productName,
                vatIncludedDepotPrice,
                descriptor.Campaign,
                descriptor.Discount,
                descriptor.Maturity);
            Report(SelcukConnectionState.Connected, "Selçuk Webdepo fiyat ve kampanya bilgisi okundu.");
            return result;
        }
        catch (OperationCanceledException)
        {
            Report(SelcukConnectionState.Failed, "Selçuk fiyat ve kampanya sorgusu iptal edildi.");
            throw;
        }
        catch (Exception exception)
        {
            Report(
                SelcukConnectionState.Failed,
                $"Selçuk fiyat ve kampanya bilgisi okunamadı: {exception.Message}");
            throw;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private Task<bool> AddBasketItemCoreAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Eklenecek miktar sıfırdan büyük olmalıdır.");
        return RunBasketMutationCoreAsync(
            "Ürün Selçuk Webdepo sepetine ekleniyor.",
            "Ürün Selçuk Webdepo sepetine eklendi.",
            async rows =>
            {
                var currentQuantity = rows.Where(row => row.Barcode == barcode).Sum(row => row.Quantity);
                await AddBasketQuantityCoreAsync(barcode, amount, currentQuantity, cancellationToken);
                var updatedRows = await ReadResolvedBasketRowsCoreAsync(cancellationToken);
                return updatedRows.Where(row => row.Barcode == barcode).Sum(row => row.Quantity)
                       == currentQuantity + amount;
            },
            cancellationToken);
    }

    private Task<bool> DecreaseBasketItemCoreAsync(
        string barcode,
        int amount,
        CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Azaltılacak miktar sıfırdan büyük olmalıdır.");
        return RunBasketMutationCoreAsync(
            "Selçuk Webdepo sepet miktarı azaltılıyor.",
            "Selçuk Webdepo sepet miktarı azaltıldı.",
            async rows =>
            {
                var matches = rows.Where(row => row.Barcode == barcode).ToArray();
                var currentQuantity = matches.Sum(row => row.Quantity);
                if (currentQuantity < amount) return false;

                var remaining = amount;
                foreach (var row in matches)
                {
                    if (remaining == 0) break;
                    if (remaining >= row.Quantity)
                    {
                        await RemoveBasketRowCoreAsync(row.RowId, cancellationToken);
                        remaining -= row.Quantity;
                    }
                    else
                    {
                        await UpdateBasketRowQuantityCoreAsync(
                            row,
                            row.Quantity - remaining,
                            cancellationToken);
                        remaining = 0;
                    }
                }

                var updatedRows = await ReadResolvedBasketRowsCoreAsync(cancellationToken);
                return updatedRows.Where(row => row.Barcode == barcode).Sum(row => row.Quantity)
                       == currentQuantity - amount;
            },
            cancellationToken);
    }

    private Task<bool> RemoveBasketItemCoreAsync(string barcode, CancellationToken cancellationToken)
    {
        ValidateBarcode(barcode);
        return RunBasketMutationCoreAsync(
            "Selçuk Webdepo sepet ürünü kaldırılıyor.",
            "Selçuk Webdepo sepet ürünü kaldırıldı.",
            async rows =>
            {
                var matches = rows.Where(row => row.Barcode == barcode).ToArray();
                if (matches.Length == 0) return false;
                foreach (var row in matches)
                    await RemoveBasketRowCoreAsync(row.RowId, cancellationToken);

                var updatedRows = await ReadResolvedBasketRowsCoreAsync(cancellationToken);
                return updatedRows.All(row => row.Barcode != barcode);
            },
            cancellationToken);
    }

    private Task<bool> ClearBasketCoreAsync(CancellationToken cancellationToken) =>
        RunBasketMutationCoreAsync(
            "Selçuk Webdepo sepeti temizleniyor.",
            "Selçuk Webdepo sepeti temizlendi.",
            async rows =>
            {
                if (rows.Length == 0) return true;
                await NavigateAsync(BasketUrl, cancellationToken);
                await WaitUntilAsync(IsBasketPageReadyAsync, cancellationToken);
                var clicked = await ExecuteBooleanAsync(
                    """
                    (() => {
                        const clear = document.querySelector('a[href*="SepetiTemizle"]');
                        if (!clear) return false;
                        clear.click();
                        return true;
                    })()
                    """);
                if (!clicked) return false;
                await ConfirmBasketDialogCoreAsync(cancellationToken);
                await WaitUntilAsync(IsBasketEmptyAsync, cancellationToken);
                return true;
            },
            cancellationToken);

    private async Task<bool> PurchaseCoreAsync(
        IReadOnlyDictionary<string, int>? expectedBasket,
        CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken)) return false;

            Report(SelcukConnectionState.Connecting, "Selçuk Webdepo siparişi doğrulanıyor.");
            var basketRows = await ReadResolvedBasketRowsCoreAsync(cancellationToken);
            var actualBasket = CreateBasketManifest(basketRows);
            if (actualBasket.Count == 0)
            {
                Report(SelcukConnectionState.Failed, "Selçuk Webdepo sepeti boş; sipariş verilmedi.");
                return false;
            }

            var requiredBasket = expectedBasket ?? actualBasket;
            if (!BasketManifestsMatch(requiredBasket, actualBasket))
            {
                Report(
                    SelcukConnectionState.Failed,
                    "Selçuk Webdepo sepeti beklenen barkod ve miktarlarla eşleşmedi; sipariş verilmedi.");
                return false;
            }

            var expectedReview = basketRows
                .GroupBy(row => row.ProductName, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(row => row.Quantity),
                    StringComparer.Ordinal);

            await NavigateAsync(OrderReviewUrl, cancellationToken);
            await WaitUntilAsync(
                IsOrderReviewPageReadyAsync,
                cancellationToken,
                "Selçuk sipariş onay sayfası zamanında hazırlanmadı.");

            var reviewRows = await ReadOrderReviewRowsAsync();
            var actualReview = reviewRows.ToDictionary(
                row => row.ProductName,
                row => row.Quantity,
                StringComparer.Ordinal);
            if (!BasketManifestsMatch(expectedReview, actualReview))
            {
                Report(
                    SelcukConnectionState.Failed,
                    "Selçuk sipariş özeti beklenen ürün ve miktarlarla eşleşmedi; sipariş verilmedi.");
                return false;
            }

            Report(SelcukConnectionState.Connecting, "Selçuk Webdepo siparişi onaylanıyor.");
            if (!await SubmitOrderReviewCoreAsync(cancellationToken))
            {
                Report(
                    SelcukConnectionState.Failed,
                    "Selçuk Webdepo sipariş sonucunu doğrulayamadı.");
                return false;
            }

            Report(SelcukConnectionState.Connected, "Selçuk Webdepo siparişi başarıyla oluşturuldu.");
            return true;
        }
        catch (OperationCanceledException)
        {
            Report(SelcukConnectionState.Failed, "Selçuk Webdepo satın alma işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(
                SelcukConnectionState.Failed,
                $"Selçuk Webdepo satın alma işlemi başarısız: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<OrderReviewRow[]> ReadOrderReviewRowsAsync()
    {
        var rowsJson = await ExecuteStringAsync(
            """
            (() => {
                const table = Array.from(document.querySelectorAll('table')).find(candidate => {
                    const headers = Array.from(candidate.querySelectorAll('thead th, thead td'))
                        .map(cell => cell.textContent?.trim().toLocaleLowerCase('tr-TR') ?? '');
                    return headers.includes('ürün adı') && headers.includes('miktarı');
                });
                if (!table) return '[]';
                const headers = Array.from(table.querySelectorAll('thead th, thead td'))
                    .map(cell => cell.textContent?.trim().toLocaleLowerCase('tr-TR') ?? '');
                const nameIndex = headers.indexOf('ürün adı');
                const quantityIndex = headers.indexOf('miktarı');
                const rows = Array.from(table.querySelectorAll('tbody tr'))
                    .map(row => {
                        const cells = Array.from(row.cells);
                        return {
                            ProductName: cells[nameIndex]?.textContent?.trim() ?? '',
                            Quantity: Number(cells[quantityIndex]?.textContent?.trim() ?? '')
                        };
                    })
                    .filter(row => row.ProductName && Number.isInteger(row.Quantity) && row.Quantity > 0);
                return JSON.stringify(rows);
            })()
            """);
        var rows = JsonSerializer.Deserialize<OrderReviewRow[]>(rowsJson)
            ?? throw new InvalidOperationException("Selçuk sipariş özeti okunamadı.");
        if (rows.Length == 0 || rows.GroupBy(row => row.ProductName, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Selçuk sipariş özetindeki ürünler çözümlenemedi.");
        return rows;
    }

    private async Task<bool> SubmitOrderReviewCoreAsync(CancellationToken cancellationToken)
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
                    $"Selçuk sipariş sonucu açılamadı: {args.WebErrorStatus}."));
        };
        currentBrowser.NavigationCompleted += completed;
        try
        {
            var clicked = await ExecuteBooleanAsync(
                """
                (() => {
                    const confirm = document.querySelector(
                        'a[href*="ctl00$ContentPlaceHolder1$btnSiparisOnay"]');
                    if (!confirm) return false;
                    confirm.click();
                    return true;
                })()
                """);
            if (!clicked) return false;
            await navigationCompleted.Task.WaitAsync(BrowserTimeout, cancellationToken);
        }
        finally
        {
            currentBrowser.NavigationCompleted -= completed;
        }

        var responseAccepted = await ExecuteBooleanAsync(
            """
            (() => {
                const path = location.pathname.toLocaleLowerCase('en-US');
                const text = document.body?.innerText?.toLocaleLowerCase('tr-TR') ?? '';
                const stillOnReview = path.endsWith('/siparis/siparisonay.aspx')
                    && !!document.querySelector(
                        'a[href*="ctl00$ContentPlaceHolder1$btnSiparisOnay"]');
                const hasSuccessText = text.includes('siparişiniz başarıyla')
                    || text.includes('siparişiniz alınmıştır')
                    || text.includes('sipariş başarıyla')
                    || text.includes('sipariş oluşturuldu')
                    || text.includes('sipariş numarası');
                return !path.endsWith('/login.aspx') && !stillOnReview
                    && (hasSuccessText || !path.endsWith('/siparis/siparisonay.aspx'));
            })()
            """);
        if (!responseAccepted) return false;

        await NavigateAsync(BasketUrl, cancellationToken);
        await WaitUntilAsync(IsBasketPageReadyAsync, cancellationToken);
        return await IsBasketEmptyAsync();
    }

    private static Dictionary<string, int> CreateBasketManifest(IEnumerable<ResolvedBasketRow> rows) =>
        rows.GroupBy(row => row.Barcode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.Quantity),
                StringComparer.Ordinal);

    private static Dictionary<string, int> ValidateAndCopyExpectedBasket(
        IReadOnlyDictionary<string, int> expectedBasket)
    {
        if (expectedBasket.Count == 0)
            throw new ArgumentException("Beklenen sepet boş olamaz.", nameof(expectedBasket));
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (barcode, quantity) in expectedBasket)
        {
            ValidateBarcode(barcode);
            if (quantity <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(expectedBasket),
                    "Beklenen sepet miktarları sıfırdan büyük olmalıdır.");
            result.Add(barcode, quantity);
        }
        return result;
    }

    private static bool BasketManifestsMatch(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual) =>
        expected.Count == actual.Count
        && expected.All(item => actual.TryGetValue(item.Key, out var quantity) && quantity == item.Value);

    private async Task<bool> RunBasketMutationCoreAsync(
        string progressMessage,
        string successMessage,
        Func<ResolvedBasketRow[], Task<bool>> mutation,
        CancellationToken cancellationToken)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(cancellationToken)) return false;
            Report(SelcukConnectionState.Connecting, progressMessage);
            var rows = await ReadResolvedBasketRowsCoreAsync(cancellationToken);
            var succeeded = await mutation(rows);
            Report(
                succeeded ? SelcukConnectionState.Connected : SelcukConnectionState.Failed,
                succeeded ? successMessage : "Selçuk Webdepo sepet işlemi tamamlanamadı.");
            return succeeded;
        }
        catch (OperationCanceledException)
        {
            Report(SelcukConnectionState.Failed, "Selçuk Webdepo sepet işlemi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(SelcukConnectionState.Failed, $"Selçuk Webdepo sepet işlemi başarısız: {exception.Message}");
            return false;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<ResolvedBasketRow[]> ReadResolvedBasketRowsCoreAsync(
        CancellationToken cancellationToken)
    {
        await NavigateAsync(BasketUrl, cancellationToken);
        await WaitUntilAsync(IsBasketPageReadyAsync, cancellationToken);
        var rowsJson = await ExecuteStringAsync(
            """
            (() => {
                const table = document.querySelector('input[name="chkSec"]')?.closest('table');
                if (!table) return '[]';
                const headers = Array.from(table.querySelectorAll('thead th, thead td'))
                    .map(cell => cell.textContent?.trim().toLocaleLowerCase('tr-TR') ?? '');
                const quantityIndex = headers.findIndex(value => value === 'adt' || value === 'adet');
                const rows = Array.from(table.querySelectorAll('tbody tr'))
                    .filter(row => row.querySelector('input[name="chkSec"]'))
                    .map(row => {
                        const cells = Array.from(row.cells);
                        const checkbox = row.querySelector('input[name="chkSec"]');
                        const image = row.querySelector('img[onclick*="hizlisiparis.aspx"]');
                        const onclick = image?.getAttribute('onclick') ?? '';
                        const urlMatch = onclick.match(/['"]([^'"]*\/Siparis\/hizlisiparis\.aspx\?[^'"]+)['"]/i);
                        return {
                            RowId: checkbox?.value ?? '',
                            ProductName: cells[1]?.textContent?.trim() ?? '',
                            ProductUrl: urlMatch?.[1] ?? '',
                            Quantity: Number(cells[quantityIndex]?.textContent?.trim() ?? '')
                        };
                    });
                return JSON.stringify(rows);
            })()
            """);
        var rows = JsonSerializer.Deserialize<BasketRowDescriptor[]>(rowsJson)
            ?? throw new InvalidOperationException("Selçuk sepet satırları okunamadı.");
        if (rows.Any(row => string.IsNullOrWhiteSpace(row.RowId)
                            || !row.RowId.All(char.IsDigit)
                            || string.IsNullOrWhiteSpace(row.ProductName)
                            || string.IsNullOrWhiteSpace(row.ProductUrl)
                            || row.Quantity <= 0))
            throw new InvalidOperationException("Selçuk sepetindeki ürün bağlantısı veya miktarı okunamadı.");

        var resolved = new List<ResolvedBasketRow>(rows.Length);
        var barcodeByProductUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var productUrl = new Uri(new Uri(HomeUrl), row.ProductUrl).AbsoluteUri;
            if (!barcodeByProductUrl.TryGetValue(productUrl, out var barcode))
            {
                await NavigateAsync(productUrl, cancellationToken);
                await WaitUntilAsync(() => IsProductBarcodeReadyAsync(row.ProductName), cancellationToken);
                barcode = await ExecuteStringAsync(
                    """
                    (() => {
                        const heading = document.querySelector('h2.urunkartiisim');
                        return heading?.parentElement?.querySelector('.barkod-box #ilacBarkod')
                            ?.textContent?.trim() ?? '';
                    })()
                    """);
                ValidateBarcode(barcode);
                barcodeByProductUrl[productUrl] = barcode;
            }

            resolved.Add(new ResolvedBasketRow(
                row.RowId,
                row.ProductName,
                productUrl,
                barcode,
                row.Quantity));
        }

        if (rows.Length > 0)
        {
            await NavigateAsync(BasketUrl, cancellationToken);
            await WaitUntilAsync(IsBasketPageReadyAsync, cancellationToken);
        }
        return resolved.ToArray();
    }

    private async Task AddBasketQuantityCoreAsync(
        string barcode,
        int amount,
        int currentQuantity,
        CancellationToken cancellationToken)
    {
        var productName = await SearchQuickOrderProductCoreAsync(barcode, cancellationToken);

        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        var addClicked = await ExecuteBooleanAsync($$"""
            (() => {
                const amountField = document.querySelector('#txtMiktar');
                const add = document.querySelector('#aSiparisEkle');
                const selectedCondition = document.querySelector('input[name="rbKampanya"]:checked');
                if (!amountField || !add || !selectedCondition) return false;
                const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                if (!setter) return false;
                setter.call(amountField, '{{amountText}}');
                amountField.dispatchEvent(new Event('input', { bubbles: true }));
                amountField.dispatchEvent(new Event('change', { bubbles: true }));
                add.click();
                return true;
            })()
            """);
        if (!addClicked) throw new InvalidOperationException("Selçuk sepete ekleme düğmesi kullanılamadı.");

        var expectedQuantity = currentQuantity + amount;
        await WaitUntilAsync(
            () => IsQuickOrderBasketQuantityAsync(productName, expectedQuantity),
            cancellationToken,
            "Selçuk sepetine eklenen miktar zamanında doğrulanamadı.");
    }

    private async Task<string> SearchQuickOrderProductCoreAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        await NavigateAsync(QuickOrderUrl, cancellationToken);
        await WaitUntilAsync(IsQuickOrderSearchReadyAsync, cancellationToken);
        await DismissBlockingPopupsAsync();
        var barcodeJson = JsonSerializer.Serialize(barcode);
        var searchStarted = await ExecuteBooleanAsync($$"""
            (() => {
                const search = document.querySelector('#txtIlcArama, input[placeholder="Hızlı Arama"]');
                const button = Array.from(document.querySelectorAll('button'))
                    .find(candidate => candidate.getAttribute('onclick')?.includes('GetUrunler'));
                if (!search || !button) return false;
                const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                if (!setter) return false;
                setter.call(search, {{barcodeJson}});
                search.dispatchEvent(new Event('input', { bubbles: true }));
                search.dispatchEvent(new Event('change', { bubbles: true }));
                search.focus();
                if (search.value !== {{barcodeJson}}) return false;
                if (typeof window.GetUrunler === 'function') window.GetUrunler(0, 0, {{barcodeJson}});
                else button.click();
                return true;
            })()
            """);
        if (!searchStarted) throw new InvalidOperationException("Selçuk barkod araması başlatılamadı.");
        await WaitUntilAsync(
            () => IsQuickOrderProductReadyAsync(barcode),
            cancellationToken,
            "Selçuk barkod araması zamanında sonuçlanmadı.");
        await DismissBlockingPopupsAsync();
        var productName = await ExecuteStringAsync(
            "document.querySelector('h2.urunkartiisim')?.textContent?.trim() ?? ''");
        if (string.IsNullOrWhiteSpace(productName))
            throw new InvalidOperationException("Selçuk ürün bilgisi okunamadı.");
        return productName;
    }

    private async Task RemoveBasketRowCoreAsync(string rowId, CancellationToken cancellationToken)
    {
        var rowIdJson = JsonSerializer.Serialize(rowId);
        await NavigateAsync(BasketUrl, cancellationToken);
        await WaitUntilAsync(IsBasketPageReadyAsync, cancellationToken);
        var clicked = await ExecuteBooleanAsync($$"""
            (() => {
                const checkbox = document.querySelector('input[name="chkSec"][value={{rowIdJson}}]');
                const remove = checkbox?.closest('tr')?.querySelector('a[href*="SilSepet"]');
                if (!remove) return false;
                remove.click();
                return true;
            })()
            """);
        if (!clicked) throw new InvalidOperationException("Selçuk sepet silme düğmesi bulunamadı.");
        await ConfirmBasketDialogCoreAsync(cancellationToken);
        await WaitUntilAsync(
            () => ExecuteBooleanAsync(
                $"!document.querySelector('input[name=\"chkSec\"][value={rowIdJson}]')"),
            cancellationToken);
    }

    private async Task UpdateBasketRowQuantityCoreAsync(
        ResolvedBasketRow row,
        int quantity,
        CancellationToken cancellationToken)
    {
        var rowIdJson = JsonSerializer.Serialize(row.RowId);
        await NavigateAsync(row.ProductUrl, cancellationToken);
        await WaitUntilAsync(() => IsProductBarcodeReadyAsync(row.ProductName), cancellationToken);
        var editorOpened = await ExecuteBooleanAsync($$"""
            (() => {
                const checkbox = document.querySelector('input[name="rbSepet"][value={{rowIdJson}}]');
                const editCell = checkbox?.closest('tr')?.cells?.[1];
                if (!editCell) return false;
                editCell.click();
                return true;
            })()
            """);
        if (!editorOpened) throw new InvalidOperationException("Selçuk sepet miktar düzenleyicisi açılamadı.");
        await WaitUntilAsync(
            () => ExecuteBooleanAsync(
                "document.querySelector('#aSiparisEkle')?.textContent?.trim() === 'Güncelle'"
                + " && !!document.querySelector('#txtMiktar')"),
            cancellationToken);
        var quantityText = quantity.ToString(CultureInfo.InvariantCulture);
        var updated = await ExecuteBooleanAsync($$"""
            (() => {
                const field = document.querySelector('#txtMiktar');
                const update = document.querySelector('#aSiparisEkle');
                if (!field || !update) return false;
                const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                if (!setter) return false;
                setter.call(field, '{{quantityText}}');
                field.dispatchEvent(new Event('input', { bubbles: true }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
                update.click();
                return true;
            })()
            """);
        if (!updated) throw new InvalidOperationException("Selçuk sepet miktarı güncellenemedi.");
        await WaitUntilAsync(
            () => ExecuteBooleanAsync($$"""
                (() => {
                    const checkbox = document.querySelector('input[name="rbSepet"][value={{rowIdJson}}]');
                    return checkbox?.closest('tr')?.cells?.[3]?.textContent?.trim() === '{{quantityText}}';
                })()
                """),
            cancellationToken);
    }

    private async Task ConfirmBasketDialogCoreAsync(CancellationToken cancellationToken)
    {
        const string findConfirmation =
            "(() => {"
            + "const modal = document.querySelector('.bootbox.modal.show, .bootbox.show');"
            + "if (!modal) return null;"
            + "return modal.querySelector('button[data-bb-handler=\"confirm\"], .bootbox-accept')"
            + " ?? Array.from(modal.querySelectorAll('button')).find(button => "
            + "/^(evet|tamam|sil)$/i.test(button.textContent?.trim() ?? ''));"
            + "})()";
        await WaitUntilAsync(
            () => ExecuteBooleanAsync($"!!{findConfirmation}"),
            cancellationToken);
        if (!await ExecuteBooleanAsync($"(() => {{ const button = {findConfirmation}; button?.click(); return !!button; }})()"))
            throw new InvalidOperationException("Selçuk sepet onay penceresi kullanılamadı.");
    }

    private static void ValidateBarcode(string barcode)
    {
        if (barcode.Length is < 8 or > 14 || !barcode.All(char.IsDigit))
            throw new ArgumentException("Barkod 8-14 rakamdan oluşmalıdır.", nameof(barcode));
    }

    private async Task<bool> EnsureAuthenticatedCoreAsync(CancellationToken cancellationToken)
    {
        var settings = ApplicationSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.SelcukUsername)
            || string.IsNullOrEmpty(settings.SelcukPassword))
        {
            Report(SelcukConnectionState.Failed,
                "Selçuk kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");
            return false;
        }

        Report(SelcukConnectionState.Connecting, "Selçuk Webdepo oturumu açılıyor.");
        await EnsureBrowserAsync(cancellationToken);
        var pageState = await GetAuthenticationPageStateAsync();
        if (pageState == AuthenticationPageState.Unknown && IsCurrentSelcukPage())
        {
            pageState = await WaitForAuthenticationPageStateAsync(
                TimeSpan.FromSeconds(4),
                cancellationToken);
        }

        if (pageState == AuthenticationPageState.Authenticated)
        {
            await DismissBlockingPopupsAsync();
            Report(SelcukConnectionState.Connected, "Mevcut Selçuk Webdepo oturumu kullanılıyor.");
            return true;
        }

        if (pageState != AuthenticationPageState.LoginForm)
        {
            await NavigateAsync(LoginUrl, cancellationToken);
            pageState = await WaitForAuthenticationPageStateAsync(BrowserTimeout, cancellationToken);
        }

        if (pageState == AuthenticationPageState.Authenticated)
        {
            await DismissBlockingPopupsAsync();
            Report(SelcukConnectionState.Connected, "Mevcut Selçuk Webdepo oturumu tanındı.");
            return true;
        }

        if (pageState != AuthenticationPageState.LoginForm)
        {
            Report(SelcukConnectionState.Failed,
                "Selçuk Webdepo giriş sayfası veya açık oturum tanınamadı.");
            return false;
        }

        if (await IsAuthenticatedAsync())
        {
            await DismissBlockingPopupsAsync();
            Report(SelcukConnectionState.Connected, "Mevcut Selçuk Webdepo oturumu tanındı.");
            return true;
        }

        await FillLoginFieldAsync("#txtKullaniciAdi", settings.SelcukUsername, "kullanıcı adı");
        await FillLoginFieldAsync("#txtSifre", settings.SelcukPassword, "parola");

        var submitted = await ExecuteBooleanAsync(
            """
            (() => {
                const submit = document.querySelector('#btnGiris');
                if (!submit || submit.disabled) return false;
                submit.click();
                return true;
            })()
            """);
        if (!submitted)
        {
            Report(SelcukConnectionState.Failed, "Selçuk GİRİŞ YAP düğmesi kullanılamadı.");
            return false;
        }

        await WaitUntilAsync(IsAuthenticatedAsync, cancellationToken);
        await DismissBlockingPopupsAsync();
        Report(SelcukConnectionState.Connected, "Selçuk Webdepo oturumu açıldı.");
        return true;
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
            ? "SelcukWebView2"
            : $"SelcukWebView2-{executableName}";
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
            Title = "Selçuk Webdepo Arka Plan Tarayıcısı",
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
            Report(SelcukConnectionState.Disconnected, "Selçuk Webdepo tarayıcısı kapatıldı.");
        };

        try
        {
            browserWindow.Show();
            await browser.EnsureCoreWebView2Async().WaitAsync(BrowserTimeout, cancellationToken);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            browser.CoreWebView2.ScriptDialogOpening += (_, args) =>
            {
                if (args.Kind is CoreWebView2ScriptDialogKind.Alert
                    or CoreWebView2ScriptDialogKind.Beforeunload)
                    args.Accept();
            };
            await browser.CoreWebView2
                .AddScriptToExecuteOnDocumentCreatedAsync(PopupCloserScript)
                .WaitAsync(BrowserTimeout, cancellationToken);
            await DismissBlockingPopupsAsync();
            ApplyBrowserWindowMode(debugBrowserVisible, activate: debugBrowserVisible);
        }
        catch
        {
            browserWindow.Close();
            throw;
        }
    }

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
                    $"Selçuk Webdepo sayfası açılamadı: {args.WebErrorStatus}."));
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
            throw new InvalidOperationException($"Selçuk {fieldName} alanı doldurulamadı.");
    }

    private async Task<bool> IsAuthenticatedAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        return await ExecuteBooleanAsync(
            """
            (() => {
                const host = location.hostname.toLocaleLowerCase('en-US');
                const path = location.pathname.toLocaleLowerCase('en-US');
                const hasLoginForm = !!document.querySelector('#txtKullaniciAdi')
                    && !!document.querySelector('#txtSifre')
                    && !!document.querySelector('#btnGiris');
                const hrefs = Array.from(document.querySelectorAll('a[href]'))
                    .map(link => link.getAttribute('href') ?? '');
                const hasSignOut = hrefs.some(href => /(^|\/)signout\.aspx(?:$|[?#])/i.test(href));
                const hasQuickOrder = hrefs.some(href => /(^|\/)siparis\/hizlisiparis\.aspx(?:$|[?#])/i.test(href));
                const hasAuthenticatedNavigation = !!document.querySelector('nav, [role="navigation"]');
                return host === 'webdepo.selcukecza.com.tr'
                    && !path.endsWith('/login.aspx')
                    && !hasLoginForm
                    && (hasSignOut || (hasQuickOrder && hasAuthenticatedNavigation));
            })()
            """);
    }

    private async Task<AuthenticationPageState> GetAuthenticationPageStateAsync()
    {
        if (await IsAuthenticatedAsync()) return AuthenticationPageState.Authenticated;
        if (await HasLoginFormAsync()) return AuthenticationPageState.LoginForm;
        return AuthenticationPageState.Unknown;
    }

    private async Task<AuthenticationPageState> WaitForAuthenticationPageStateAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var state = await GetAuthenticationPageStateAsync();
            if (state != AuthenticationPageState.Unknown) return state;
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline) return AuthenticationPageState.Unknown;
            await Task.Delay(120, cancellationToken);
        }
    }

    private bool IsCurrentSelcukPage() =>
        browser?.Source is { } source
        && source.Host.Equals("webdepo.selcukecza.com.tr", StringComparison.OrdinalIgnoreCase);

    private Task<bool> HasLoginFormAsync() => ExecuteBooleanAsync(
        "!!document.querySelector('#txtKullaniciAdi')"
        + " && !!document.querySelector('#txtSifre')"
        + " && !!document.querySelector('#btnGiris')");

    private Task<bool> IsBasketPageReadyAsync() => ExecuteBooleanAsync(
        "location.pathname.toLocaleLowerCase('en-US').endsWith('/siparis/sepet.aspx')"
        + " && (!!document.querySelector('input[name=\"chkSec\"]')"
        + " || document.querySelector('#ContentPlaceHolder1_divGrid')"
        + "?.textContent?.includes('Kayıt Bulunamadı'))");

    private Task<bool> IsQuickOrderSearchReadyAsync() => ExecuteBooleanAsync(
        "location.pathname.toLocaleLowerCase('en-US').endsWith('/siparis/hizlisiparis.aspx')"
        + " && !!document.querySelector('#txtIlcArama, input[placeholder=\"Hızlı Arama\"]')"
        + " && Array.from(document.querySelectorAll('button'))"
        + ".some(button => button.getAttribute('onclick')?.includes('GetUrunler'))");

    private Task<bool> IsQuickOrderProductReadyAsync(string barcode)
    {
        var barcodeJson = JsonSerializer.Serialize(barcode);
        return ExecuteBooleanAsync($$"""
            (() => {
                const heading = document.querySelector('h2.urunkartiisim');
                const shownBarcode = heading?.parentElement?.querySelector('.barkod-box #ilacBarkod')
                    ?.textContent?.trim() ?? '';
                return shownBarcode === {{barcodeJson}}
                    && !!document.querySelector('input[name="rbKampanya"]:checked')
                    && !!document.querySelector('#txtMiktar')
                    && !!document.querySelector('#aSiparisEkle');
            })()
            """);
    }

    private Task<bool> IsQuickOrderBasketQuantityAsync(string productName, int expectedQuantity)
    {
        var productNameJson = JsonSerializer.Serialize(productName);
        var expectedText = expectedQuantity.ToString(CultureInfo.InvariantCulture);
        return ExecuteBooleanAsync($$"""
            (() => {
                const total = Array.from(document.querySelectorAll('input[name="rbSepet"]'))
                    .map(checkbox => checkbox.closest('tr'))
                    .filter(row => row?.cells?.[2]?.textContent?.trim() === {{productNameJson}})
                    .reduce((sum, row) => sum + Number(row.cells[3]?.textContent?.trim() ?? ''), 0);
                return total === {{expectedText}};
            })()
            """);
    }

    private Task<bool> IsProductBarcodeReadyAsync(string productName)
    {
        var productNameJson = JsonSerializer.Serialize(productName);
        return ExecuteBooleanAsync($$"""
            (() => {
                const heading = document.querySelector('h2.urunkartiisim');
                const barcode = heading?.parentElement?.querySelector('.barkod-box #ilacBarkod')
                    ?.textContent?.trim() ?? '';
                return heading?.textContent?.trim() === {{productNameJson}}
                    && /^\d{8,14}$/.test(barcode);
            })()
            """);
    }

    private Task<bool> IsBasketEmptyAsync() => ExecuteBooleanAsync(
        "location.pathname.toLocaleLowerCase('en-US').endsWith('/siparis/sepet.aspx')"
        + " && !document.querySelector('input[name=\"chkSec\"]')"
        + " && document.querySelector('#ContentPlaceHolder1_divGrid')"
        + "?.textContent?.includes('Kayıt Bulunamadı')");

    private Task<bool> IsOrderReviewPageReadyAsync() => ExecuteBooleanAsync(
        "location.pathname.toLocaleLowerCase('en-US').endsWith('/siparis/siparisonay.aspx')"
        + " && !!document.querySelector('a[href*=\"ctl00$ContentPlaceHolder1$btnSiparisOnay\"]')"
        + " && Array.from(document.querySelectorAll('table'))"
        + ".some(table => table.textContent?.includes('Ürün Adı')"
        + " && table.textContent?.includes('Miktarı'))");

    private async Task<bool> ExecuteBooleanAsync(string script)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DismissBlockingPopupsAsync()
    {
        if (browser?.CoreWebView2 is null) return;
        await browser.CoreWebView2.ExecuteScriptAsync(PopupCloserScript);
        await browser.CoreWebView2.ExecuteScriptAsync(PopupDismissScript);
    }

    private async Task<string> ExecuteStringAsync(string script)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(raw)
            ?? throw new InvalidOperationException("Selçuk Webdepo geçersiz bir yanıt döndürdü.");
    }

    private async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken,
        string timeoutMessage = "Selçuk Webdepo beklenen sayfayı zamanında açmadı.")
    {
        var deadline = DateTime.UtcNow + BrowserTimeout;
        while (!await condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(timeoutMessage);
            await Task.Delay(120, cancellationToken);
        }
    }

    private WebView2 RequireBrowser() => browser?.CoreWebView2 is not null
        ? browser
        : throw new InvalidOperationException("Selçuk Webdepo arka plan tarayıcısı başlatılmadı.");

    private void Report(SelcukConnectionState state, string message)
    {
        Status = new SelcukConnectionStatus(state, message, DateTimeOffset.Now);
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

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Uygulama Dispatcher'ı kullanılamıyor.");
        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private sealed record BasketRowDescriptor(
        string RowId,
        string ProductName,
        string ProductUrl,
        int Quantity);

    private sealed record ResolvedBasketRow(
        string RowId,
        string ProductName,
        string ProductUrl,
        string Barcode,
        int Quantity);

    private sealed record OrderReviewRow(string ProductName, int Quantity);

    private sealed record MedicationPriceDescriptor(
        string VatIncludedDepotPrice,
        string Campaign,
        string Discount,
        string Maturity);

    private enum AuthenticationPageState
    {
        Unknown,
        Authenticated,
        LoginForm
    }
}
