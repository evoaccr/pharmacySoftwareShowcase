using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pharmacyApp.Modules.WebDepots.Alliance;

public partial class AllianceTestView : UserControl
{
    private readonly IAllianceWebDepotService connection;
    private readonly List<string> logEntries = [];
    private bool subscribed;

    public AllianceTestView()
        : this(AllianceWebDepotService.Instance)
    {
    }

    internal AllianceTestView(IAllianceWebDepotService connection)
    {
        this.connection = connection;
        InitializeComponent();
        StatusLogText.Text = "Alliance Test açıldığında arka plan tarayıcısı görünür olacaktır.";
        UpdateState(connection.Status.State);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed)
        {
            connection.StatusChanged += ConnectionStatusChanged;
            subscribed = true;
        }

        var autoLoggedIn = await connection.ShowBrowserForDebugAsync();
        AddLog(autoLoggedIn
            ? "Tarayıcı açıldı ve Alliance oturumu otomatik açıldı."
            : "Tarayıcı açıldı ancak Alliance oturumu otomatik açılamadı.");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed) return;
        connection.StatusChanged -= ConnectionStatusChanged;
        subscribed = false;
    }

    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        try
        {
            if (!await connection.LoginAsync()) AddLog("Alliance girişi başarısız oldu.");
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private void ShowBrowserClick(object sender, RoutedEventArgs e) => connection.ShowBrowser();

    private async void RestartBrowserClick(object sender, RoutedEventArgs e)
    {
        RestartBrowserButton.IsEnabled = false;
        try
        {
            AddLog(await connection.RestartBrowserForDebugAsync()
                ? "Alliance tarayıcısı yeniden başlatıldı ve oturum açıldı."
                : "Alliance tarayıcısı yeniden başlatıldı; oturum açılamadı.");
        }
        finally
        {
            RestartBrowserButton.IsEnabled = true;
        }
    }

    private async void BasketClick(object sender, RoutedEventArgs e)
    {
        SetBasketActionsEnabled(false);
        try
        {
            var items = await connection.GetBasketAsync();
            if (items.Count == 0)
            {
                AddLog("Sepet boş.");
                return;
            }

            AddLog("Sepetteki ürünler:");
            foreach (var item in items) AddLog($"{item.Barcode} × {item.Quantity}");
        }
        catch (Exception exception)
        {
            AddLog($"Sepet okunamadı: {exception.Message}");
        }
        finally
        {
            SetBasketActionsEnabled(true);
        }
    }

    private async void PriceInfoClick(object sender, RoutedEventArgs e)
    {
        var barcode = BasketBarcodeTextBox.Text.Trim();
        if (!TryValidateBarcode(barcode))
        {
            AddLog("Fiyat ve kampanya sorgusu için geçerli bir barkod girin.");
            return;
        }

        SetBasketActionsEnabled(false);
        try
        {
            var info = await connection.GetMedicationPriceInfoAsync(barcode);
            var price = info.ActiveVatIncludedDepotPrice.ToString(
                "N2",
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
            AddLog($"Ürün: {info.ProductName} ({info.Barcode})");
            AddLog($"KDV dahil aktif alış fiyatı: {price} ₺");
            AddLog($"Kampanya: {info.Campaign}");
            AddLog($"İskonto: {info.Discount}");
            AddLog($"Vade: {info.Maturity}");
        }
        catch (Exception exception)
        {
            AddLog($"Fiyat ve kampanya bilgisi okunamadı: {exception.Message}");
        }
        finally
        {
            SetBasketActionsEnabled(true);
        }
    }

    private async void AddBasketClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetBarcodeAndAmount(out var barcode, out var amount)) return;
        await RunBasketActionAsync(
            () => connection.AddBasketItemAsync(barcode, amount),
            $"{barcode} barkodundan {amount} adet sepete eklendi.");
    }

    private async void DecreaseBasketClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetBarcodeAndAmount(out var barcode, out var amount)) return;
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                $"{barcode} barkodlu ürünün miktarı {amount} azaltılacak. Devam edilsin mi?",
                "Sepet Miktarını Azalt",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await RunBasketActionAsync(
            () => connection.DecreaseBasketItemAsync(barcode, amount),
            $"{barcode} barkodunun miktarı {amount} azaltıldı.");
    }

    private async void RemoveBasketClick(object sender, RoutedEventArgs e)
    {
        var barcode = BasketBarcodeTextBox.Text.Trim();
        if (!TryValidateBarcode(barcode))
        {
            AddLog("Sepetten kaldırmak için geçerli bir barkod girin.");
            return;
        }
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                $"{barcode} barkodlu ürün sepetten tamamen kaldırılacak. Devam edilsin mi?",
                "Sepetten Kaldır",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await RunBasketActionAsync(
            () => connection.RemoveBasketItemAsync(barcode),
            $"{barcode} barkodlu ürün sepetten kaldırıldı.");
    }

    private async void ClearBasketClick(object sender, RoutedEventArgs e)
    {
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                "Alliance sepetindeki tüm ürünler kaldırılacak. Devam edilsin mi?",
                "Sepeti Temizle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await RunBasketActionAsync(
            () => connection.ClearBasketAsync(),
            "Alliance sepeti temizlendi.");
    }

    private async void PurchaseClick(object sender, RoutedEventArgs e)
    {
        SetBasketActionsEnabled(false);
        try
        {
            var basket = await connection.GetBasketAsync();
            if (basket.Count == 0)
            {
                AddLog("Sepet boş; satın alma işlemi başlatılmadı.");
                return;
            }
            var basketText = string.Join(
                Environment.NewLine,
                basket.Select(item => $"{item.Barcode} × {item.Quantity}"));
            if (pharmacyApp.Localization.EnglishMessageBox.Show(
                    $"Aşağıdaki ürünler gerçek Alliance siparişi olarak satın alınacak:\n\n"
                    + $"{basketText}\n\n"
                    + "Bu işlem ücretli bir sipariş oluşturur ve geri alınamayabilir. Devam edilsin mi?",
                    "Gerçek Alliance Siparişi",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Stop,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                AddLog("Satın alma işlemi kullanıcı tarafından iptal edildi.");
                return;
            }

            var expectedBasket = basket.ToDictionary(
                item => item.Barcode,
                item => item.Quantity,
                StringComparer.Ordinal);
            AddLog("Sepet yeniden doğrulanıyor; gerçek sipariş onaylanacak.");
            AddLog(await connection.PurchaseAsync(expectedBasket)
                ? "Alliance siparişi başarıyla oluşturuldu."
                : "Alliance sipariş sonucu doğrulanamadı; webdepo sipariş kutusunu kontrol edin.");
        }
        catch (Exception exception)
        {
            AddLog($"Satın alma işlemi başarısız: {exception.Message}");
        }
        finally
        {
            SetBasketActionsEnabled(true);
        }
    }

    private async Task RunBasketActionAsync(Func<Task<bool>> action, string successMessage)
    {
        SetBasketActionsEnabled(false);
        try
        {
            AddLog(await action()
                ? successMessage
                : "Sepet işlemi tamamlanamadı; barkodu ve miktarı kontrol edin.");
        }
        catch (Exception exception)
        {
            AddLog($"Sepet işlemi başarısız: {exception.Message}");
        }
        finally
        {
            SetBasketActionsEnabled(true);
        }
    }

    private bool TryGetBarcodeAndAmount(out string barcode, out int amount)
    {
        barcode = BasketBarcodeTextBox.Text.Trim();
        if (TryValidateBarcode(barcode)
            && int.TryParse(AmountTextBox.Text.Trim(), out amount)
            && amount > 0)
            return true;
        amount = 0;
        AddLog("Geçerli bir barkod ve sıfırdan büyük miktar girin.");
        return false;
    }

    private static bool TryValidateBarcode(string barcode) =>
        barcode.Length == 13 && barcode.All(char.IsAsciiDigit);

    private void SetBasketActionsEnabled(bool enabled)
    {
        BasketButton.IsEnabled = enabled;
        PriceInfoButton.IsEnabled = enabled;
        AddBasketButton.IsEnabled = enabled;
        DecreaseBasketButton.IsEnabled = enabled;
        RemoveBasketButton.IsEnabled = enabled;
        ClearBasketButton.IsEnabled = enabled;
        PurchaseButton.IsEnabled = enabled;
    }

    private void ConnectionStatusChanged(object? sender, AllianceConnectionStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ConnectionStatusChanged(sender, status));
            return;
        }
        UpdateState(status.State);
        AddLog(status.Message);
    }

    private void UpdateState(AllianceConnectionState state)
    {
        ConnectionStateText.Text = state switch
        {
            AllianceConnectionState.Connected => "Bağlı",
            AllianceConnectionState.Connecting => "Bağlanıyor",
            AllianceConnectionState.Failed => "Başarısız",
            _ => "Bağlı değil"
        };
        ConnectionStateText.Foreground = state switch
        {
            AllianceConnectionState.Connected => new SolidColorBrush(Color.FromRgb(19, 121, 87)),
            AllianceConnectionState.Connecting => new SolidColorBrush(Color.FromRgb(180, 116, 20)),
            AllianceConnectionState.Failed => new SolidColorBrush(Color.FromRgb(181, 48, 48)),
            _ => new SolidColorBrush(Color.FromRgb(105, 115, 134))
        };
    }

    private void AddLog(string message)
    {
        logEntries.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (logEntries.Count > 100) logEntries.RemoveAt(0);
        StatusLogText.Text = string.Join(Environment.NewLine, logEntries);
    }
}
