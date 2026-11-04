using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Its;

public partial class ItsSaleTestView : UserControl
{
    private static readonly JsonSerializerOptions PreviewJsonOptions = new() { WriteIndented = true };
    private readonly ItsApiClient apiClient = new();
    private CancellationTokenSource? operationCancellation;
    private ItsQrProduct? parsedProduct;
    private ItsPharmacyNotificationRequest? preparedRequest;
    private bool operationInProgress;

    public ItsSaleTestView()
    {
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        EnvironmentBox.ItemsSource = new[]
        {
            new ItsEnvironmentOption(ItsEnvironment.Test, "İTS Test Sistemi"),
            new ItsEnvironmentOption(ItsEnvironment.Production, "İTS Gerçek Sistem")
        };
        OperationBox.ItemsSource = new[]
        {
            new ItsOperationOption(ItsPharmacyOperation.RetailSale, "Elden Perakende Satış"),
            new ItsOperationOption(ItsPharmacyOperation.RetailSaleCancellation, "Elden Perakende Satış İptali")
        };
        EnvironmentBox.SelectedIndex = 0;
        OperationBox.SelectedIndex = 0;
        RefreshPreparedRequest();
    }

    private ItsEnvironment SelectedEnvironment =>
        (EnvironmentBox.SelectedItem as ItsEnvironmentOption)?.Value ?? ItsEnvironment.Test;

    private ItsPharmacyOperation SelectedOperation =>
        (OperationBox.SelectedItem as ItsOperationOption)?.Value ?? ItsPharmacyOperation.RetailSale;

    private void OnLoaded(object sender, RoutedEventArgs e) => PharmacyGlnBox.Focus();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var cancellation = Interlocked.Exchange(ref operationCancellation, null);
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        cancellation.Dispose();
    }

    private void OnConfigurationChanged(object sender, SelectionChangedEventArgs e) => RefreshPreparedRequest();

    private void OnQrOrGlnChanged(object sender, TextChangedEventArgs e) => RefreshPreparedRequest();

    private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (operationInProgress) return;
        try
        {
            BeginOperation("Access token isteniyor...");
            await apiClient.TestConnectionAsync(
                SelectedEnvironment,
                ReadCredentials(),
                operationCancellation!.Token);
            SetStatus("Bağlantı başarılı. Access token alındı ve bellekte tutulmadan silindi.", success: true);
            ResponseBox.Text = "Access token isteği başarılı. Bildirim gönderilmedi.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            SetStatus($"Bağlantı başarısız: {exception.Message}", success: false);
            ResponseBox.Text = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (operationInProgress || preparedRequest is null || parsedProduct is null) return;
        ItsCredentials credentials;
        try
        {
            credentials = ReadCredentials();
        }
        catch (InvalidOperationException exception)
        {
            SetStatus(exception.Message, success: false);
            return;
        }

        var environmentName = SelectedEnvironment == ItsEnvironment.Test ? "İTS TEST SİSTEMİ" : "İTS GERÇEK SİSTEMİ";
        var operationName = SelectedOperation == ItsPharmacyOperation.RetailSale
            ? "Elden Perakende Satış (S)"
            : "Elden Perakende Satış İptali (C)";
        var endpoint = ItsServiceEndpoints.PharmacyNotification(SelectedEnvironment, SelectedOperation);
        var confirmation = pharmacyApp.Localization.EnglishMessageBox.Show(
            Window.GetWindow(this),
            $"Bu işlem İTS üzerinde stok hareketi oluşturacaktır.\n\n" +
            $"Ortam: {environmentName}\n" +
            $"Hareket: {operationName}\n" +
            $"Adres: {endpoint}\n" +
            $"GTIN: {parsedProduct.Gtin}\n" +
            $"Sıra No: {parsedProduct.SerialNumber}\n" +
            $"Parti No: {parsedProduct.BatchNumber}\n" +
            $"SKT: {parsedProduct.ExpiryDateText}\n\n" +
            "Göndermek istediğinize emin misiniz?",
            SelectedEnvironment == ItsEnvironment.Production ? "DİKKAT: Gerçek İTS Bildirimi" : "İTS Test Bildirimi",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            BeginOperation("Access token alınıyor ve bildirim gönderiliyor...");
            var result = await apiClient.SendNotificationAsync(
                SelectedEnvironment,
                SelectedOperation,
                credentials,
                preparedRequest,
                operationCancellation!.Token);
            ResponseBox.Text = FormatResult(result);
            if (result.IsSuccessful)
            {
                var notificationText = string.IsNullOrWhiteSpace(result.NotificationId)
                    ? string.Empty
                    : $" Bildirim no: {result.NotificationId}";
                SetStatus($"İTS bildirimi başarılı.{notificationText}", success: true);
            }
            else
            {
                var failure = string.IsNullOrWhiteSpace(result.FaultMessage)
                    ? string.Join(", ", result.Products.Select(product => $"{product.SerialNumber}: {product.ResultDisplay}"))
                    : $"{result.FaultCode} {result.FaultMessage}".Trim();
                SetStatus($"İTS bildirimi başarıyla tamamlanmadı. HTTP {result.HttpStatusCode}. {failure}".Trim(), success: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            SetStatus($"İTS isteği gönderilemedi: {exception.Message}", success: false);
            ResponseBox.Text = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private ItsCredentials ReadCredentials()
    {
        var settings = ApplicationSettingsStore.Current;
        var username = settings.ItsUsername;
        var password = settings.ItsPassword;
        if (username.Length == 0 || password.Length == 0)
            throw new InvalidOperationException("İTS kullanıcı adı ve parolası Ayarlar > İTS Ayarları bölümünde yapılandırılmalıdır.");
        return new ItsCredentials(username, password);
    }

    private void RefreshPreparedRequest()
    {
        if (TokenEndpointBox is null || NotificationEndpointBox is null) return;
        SendButton.Content = SelectedOperation == ItsPharmacyOperation.RetailSale
            ? "İTS Satış Bildirimini Gönder"
            : "İTS Satış İptal Bildirimini Gönder";
        TokenEndpointBox.Text = ItsServiceEndpoints.Token(SelectedEnvironment).AbsoluteUri;
        NotificationEndpointBox.Text = ItsServiceEndpoints
            .PharmacyNotification(SelectedEnvironment, SelectedOperation)
            .AbsoluteUri;
        preparedRequest = null;
        parsedProduct = null;
        ClearParsedFields();

        if (string.IsNullOrWhiteSpace(QrCodeBox?.Text))
        {
            ValidationText.Text = "Karekod bekleniyor.";
            RequestPreviewBox.Text = string.Empty;
            UpdateButtons();
            return;
        }
        if (!ItsQrParser.TryParse(QrCodeBox.Text, out var product, out var parseError) || product is null)
        {
            ValidationText.Text = parseError;
            RequestPreviewBox.Text = string.Empty;
            UpdateButtons();
            return;
        }

        parsedProduct = product;
        GtinBox.Text = product.Gtin;
        SerialNumberBox.Text = product.SerialNumber;
        BatchNumberBox.Text = product.BatchNumber;
        ExpiryDateBox.Text = product.ExpiryDate.ToString("dd.MM.yyyy");
        try
        {
            preparedRequest = ItsPharmacyRequestFactory.Create(SelectedOperation, PharmacyGlnBox.Text, product);
            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(22, 125, 78));
            ValidationText.Text = "Karekod ve bildirim alanları geçerli.";
            RequestPreviewBox.Text = JsonSerializer.Serialize(preparedRequest, PreviewJsonOptions);
        }
        catch (InvalidOperationException exception)
        {
            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            ValidationText.Text = exception.Message;
            RequestPreviewBox.Text = string.Empty;
        }
        UpdateButtons();
    }

    private void ClearParsedFields()
    {
        GtinBox.Text = string.Empty;
        SerialNumberBox.Text = string.Empty;
        BatchNumberBox.Text = string.Empty;
        ExpiryDateBox.Text = string.Empty;
        ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
    }

    private void BeginOperation(string status)
    {
        operationInProgress = true;
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        SetStatus(status, success: null);
        UpdateButtons();
    }

    private void EndOperation()
    {
        operationInProgress = false;
        operationCancellation?.Dispose();
        operationCancellation = null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (SendButton is null) return;
        SendButton.IsEnabled = preparedRequest is not null && !operationInProgress;
        TestConnectionButton.IsEnabled = !operationInProgress;
        EnvironmentBox.IsEnabled = !operationInProgress;
        OperationBox.IsEnabled = !operationInProgress;
    }

    private void SetStatus(string text, bool? success)
    {
        StatusText.Text = text;
        StatusText.Foreground = success switch
        {
            true => new SolidColorBrush(Color.FromRgb(22, 125, 78)),
            false => new SolidColorBrush(Color.FromRgb(179, 58, 50)),
            _ => (Brush)FindResource("MutedInk")
        };
    }

    private static string FormatResult(ItsNotificationResult result)
    {
        var summary = $"HTTP {result.HttpStatusCode}";
        if (!string.IsNullOrWhiteSpace(result.NotificationId)) summary += $"\nBildirim no: {result.NotificationId}";
        if (!string.IsNullOrWhiteSpace(result.FaultCode)) summary += $"\nHata kodu: {result.FaultCode}";
        if (!string.IsNullOrWhiteSpace(result.FaultMessage)) summary += $"\nHata: {result.FaultMessage}";
        if (result.Products.Count > 0)
            summary += "\nÜrün sonuçları:\n" + string.Join(
                Environment.NewLine,
                result.Products.Select(product => $"- {product.Gtin} / {product.SerialNumber}: {product.ResultDisplay}"));
        return summary + "\n\nHam cevap:\n" + result.ResponseBody;
    }
}
