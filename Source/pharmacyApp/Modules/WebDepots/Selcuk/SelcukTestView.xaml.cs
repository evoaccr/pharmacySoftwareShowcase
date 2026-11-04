using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pharmacyApp.Modules.WebDepots.Selcuk;

public partial class SelcukTestView : UserControl
{
    private readonly SelcukWebDepotService connection = SelcukWebDepotService.Instance;
    private readonly List<string> logEntries = [];
    private bool subscribed;

    public SelcukTestView()
    {
        InitializeComponent();
        UpdateState(connection.Status.State);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed)
        {
            connection.StatusChanged += ConnectionStatusChanged;
            subscribed = true;
        }

        try
        {
            var autoLoggedIn = await connection.ShowBrowserForDebugAsync();
            AddLog(autoLoggedIn
                ? "Tarayıcı açıldı ve Selçuk Webdepo'ya otomatik giriş yapıldı."
                : "Tarayıcı açıldı ancak Selçuk Webdepo'ya otomatik giriş yapılamadı.");
        }
        catch (Exception exception)
        {
            AddLog($"Tarayıcı açılamadı: {exception.Message}");
            UpdateState(SelcukConnectionState.Failed);
        }
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
            var succeeded = await connection.LoginAsync();
            if (!succeeded && logEntries.Count == 0)
                AddLog("Selçuk Webdepo girişi başarısız oldu.");
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
            await connection.RestartBrowserForDebugAsync();
        }
        catch (Exception exception)
        {
            AddLog($"Tarayıcı yeniden başlatılamadı: {exception.Message}");
        }
        finally
        {
            RestartBrowserButton.IsEnabled = true;
        }
    }

    private async void BasketClick(object sender, RoutedEventArgs e)
    {
        BasketButton.IsEnabled = false;
        try
        {
            var items = await connection.GetBasketAsync();
            if (items.Count == 0)
            {
                AddLog("Sepet boş.");
                return;
            }

            AddLog("Sepetteki ürünler:");
            foreach (var item in items)
                AddLog($"{item.Barcode} × {item.Quantity}");
        }
        catch (OperationCanceledException)
        {
            AddLog("Sepet okuma işlemi iptal edildi.");
        }
        catch (Exception exception)
        {
            AddLog($"Sepet okunamadı: {exception.Message}");
        }
        finally
        {
            BasketButton.IsEnabled = true;
        }
    }

    private async void DecreaseBasketClick(object sender, RoutedEventArgs e)
    {
        var barcode = BasketBarcodeTextBox.Text.Trim();
        if (!TryValidateBarcode(barcode)
            || !int.TryParse(DecreaseAmountTextBox.Text.Trim(), out var amount)
            || amount <= 0)
        {
            AddLog("Geçerli bir barkod ve sıfırdan büyük azaltma miktarı girin.");
            return;
        }

        await RunBasketActionAsync(
            () => connection.DecreaseBasketItemAsync(barcode, amount),
            $"{barcode} barkodunun miktarı {amount} azaltıldı.");
    }

    private async void AddBasketClick(object sender, RoutedEventArgs e)
    {
        var barcode = BasketBarcodeTextBox.Text.Trim();
        if (!TryValidateBarcode(barcode)
            || !int.TryParse(DecreaseAmountTextBox.Text.Trim(), out var amount)
            || amount <= 0)
        {
            AddLog("Geçerli bir barkod ve sıfırdan büyük ekleme miktarı girin.");
            return;
        }

        await RunBasketActionAsync(
            () => connection.AddBasketItemAsync(barcode, amount),
            $"{barcode} barkodundan {amount} adet sepete eklendi.");
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
                CultureInfo.GetCultureInfo("tr-TR"));
            AddLog($"Ürün: {info.ProductName}");
            AddLog($"Barkod: {info.Barcode}");
            AddLog($"KDV dahil depocu satış fiyatı: {price} ₺");
            AddLog($"Kampanya: {info.Campaign}");
            AddLog($"İskonto: {info.Discount}");
            AddLog($"Vade: {info.Maturity}");
        }
        catch (OperationCanceledException)
        {
            AddLog("Fiyat ve kampanya sorgusu iptal edildi.");
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

    private async void RemoveBasketClick(object sender, RoutedEventArgs e)
    {
        var barcode = BasketBarcodeTextBox.Text.Trim();
        if (!TryValidateBarcode(barcode))
        {
            AddLog("Geçerli bir barkod girin.");
            return;
        }
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                $"{barcode} barkodlu ürün Selçuk sepetinden kaldırılacak. Devam edilsin mi?",
                "Sepetten Kaldır",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunBasketActionAsync(
            () => connection.RemoveBasketItemAsync(barcode),
            $"{barcode} barkodlu ürün sepetten kaldırıldı.");
    }

    private async void ClearBasketClick(object sender, RoutedEventArgs e)
    {
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                "Selçuk sepetindeki tüm ürünler kaldırılacak. Devam edilsin mi?",
                "Sepeti Temizle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunBasketActionAsync(
            () => connection.ClearBasketAsync(),
            "Selçuk sepeti temizlendi.");
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
                    $"Aşağıdaki ürünler gerçek Selçuk siparişi olarak satın alınacak:\n\n{basketText}\n\n"
                    + "Bu işlem ücretli bir sipariş oluşturur ve geri alınamayabilir. Devam edilsin mi?",
                    "Gerçek Selçuk Siparişi",
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
            AddLog("Sepet ve sipariş özeti doğrulanıyor; gerçek sipariş onaylanacak.");
            AddLog(await connection.PurchaseAsync(expectedBasket)
                ? "Selçuk siparişi başarıyla oluşturuldu."
                : "Selçuk siparişi oluşturulamadı; ürün veya miktar doğrulaması başarısız olmuş olabilir.");
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
            AddLog(await action() ? successMessage : "Sepet işlemi tamamlanamadı.");
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

    private void SetBasketActionsEnabled(bool enabled)
    {
        AddButton.IsEnabled = enabled;
        PriceInfoButton.IsEnabled = enabled;
        DecreaseButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
        ClearButton.IsEnabled = enabled;
        PurchaseButton.IsEnabled = enabled;
    }

    private static bool TryValidateBarcode(string barcode) =>
        barcode.Length is >= 8 and <= 14 && barcode.All(char.IsDigit);

    private void ConnectionStatusChanged(object? sender, SelcukConnectionStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ConnectionStatusChanged(sender, status));
            return;
        }

        AddLog($"{status.Timestamp:HH:mm:ss}  {status.Message}");
        UpdateState(status.State);
    }

    private void AddLog(string message)
    {
        logEntries.Add(message);
        StatusLogText.Text = string.Join(Environment.NewLine, logEntries);
    }

    private void UpdateState(SelcukConnectionState state)
    {
        (ConnectionStateText.Text, ConnectionStateText.Foreground) = state switch
        {
            SelcukConnectionState.Connecting => ("Bağlanıyor", new SolidColorBrush(Color.FromRgb(173, 105, 16))),
            SelcukConnectionState.Connected => ("Bağlandı", new SolidColorBrush(Color.FromRgb(35, 122, 87))),
            SelcukConnectionState.Failed => ("Bağlantı başarısız", new SolidColorBrush(Color.FromRgb(180, 52, 52))),
            _ => ("Bağlı değil", new SolidColorBrush(Color.FromRgb(105, 115, 134)))
        };
    }
}
