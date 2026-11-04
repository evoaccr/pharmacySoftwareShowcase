using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Its;

public partial class ItsStatusQueryView : UserControl
{
    private readonly IProductStatusQueryService queryService = new ItsProductStatusQueryService(
        new ItsProductStatusQueryRepository(),
        new ItsApiClient());
    private CancellationTokenSource? operationCancellation;
    private bool operationInProgress;

    public ItsStatusQueryView()
    {
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QrCodeBox.Focus();

    private void OnQrCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        _ = QueryAsync();
    }

    private async void OnQueryClicked(object sender, RoutedEventArgs e) => await QueryAsync();

    private async Task QueryAsync()
    {
        if (operationInProgress) return;
        try
        {
            BeginOperation();
            var settings = ApplicationSettingsStore.Current;
            var report = await queryService.QueryAsync(
                QrCodeBox.Text,
                ItsEnvironment.Production,
                new ItsCredentials(settings.ItsUsername, settings.ItsPassword),
                settings.ItsPharmacyGln,
                operationCancellation!.Token);
            Render(report);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Sorgu iptal edildi.", false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ResultsPanel.Visibility = Visibility.Collapsed;
            ComparisonPanel.Visibility = Visibility.Collapsed;
            SetStatus($"Sorgu yapılamadı: {exception.Message}", false);
        }
        finally
        {
            EndOperation();
        }
    }

    private void Render(ProductStatusQueryReport report)
    {
        ResultsPanel.Visibility = Visibility.Visible;
        ComparisonPanel.Visibility = Visibility.Visible;
        StatusText.Text = string.Empty;
        RenderProductInformation(report);
        LatestMovementText.Text = FormatLatestMovement(report.LocalStatus, report.LocalError);

        if (report.LocalStatus is { } local)
        {
            LocalSummaryText.Text = local.Summary;
            LocalSummaryText.Foreground = StatusBrush(local.Location == LocalQrLocation.Active);
            LocalDetailsText.Text = FormatLocalDetails(local);
        }
        else
        {
            LocalSummaryText.Text = "Yerel veritabanı sorgulanamadı.";
            LocalSummaryText.Foreground = StatusBrush(false);
            LocalDetailsText.Text = report.LocalError ?? "Bilinmeyen veritabanı hatası.";
        }

        if (report.ItsAssessment is { } assessment && report.ItsResult is { } itsResult)
        {
            ItsSummaryText.Text = assessment.Summary;
            ItsSummaryText.Foreground = StatusBrush(assessment.IsRegisteredToThisPharmacy);
            ItsDetailsText.Text = FormatItsDetails(report, assessment, itsResult);
        }
        else
        {
            var itsError = report.ItsError ?? "İTS kullanılabilir bir durum bilgisi döndürmedi.";
            ItsSummaryText.Text = FormatItsErrorSummary(itsError);
            ItsSummaryText.Foreground = StatusBrush(false);
            ItsDetailsText.Text = $"{itsError}{Environment.NewLine}{Environment.NewLine}" +
                                  "Yerel veritabanı sonucu etkilenmedi. Bağlantı düzeldikten sonra yeniden sorgulayabilirsiniz.";
        }

        ComparisonText.Text = report.ComparisonSummary;
        ComparisonText.Foreground = report.ComparisonSummary.Contains("uyumludur", StringComparison.CurrentCultureIgnoreCase)
            ? StatusBrush(true)
            : new SolidColorBrush(Color.FromRgb(143, 91, 18));
        QueryMetaText.Text = $"Sorgu zamanı: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
    }

    private void RenderProductInformation(ProductStatusQueryReport report)
    {
        var local = report.LocalStatus;
        var productName = local is { ExistsInDatabase: true } && !string.IsNullOrWhiteSpace(local.ProductName)
            ? local.ProductName
            : "Ürün adı yerel veritabanında bulunamadı";
        var barcode = local is { ExistsInDatabase: true } && !string.IsNullOrWhiteSpace(local.Barcode)
            ? local.Barcode
            : DeriveBarcode(report.Product.Gtin);

        ProductNameText.Text = productName;
        ProductIdentifiersText.Text =
            $"Barkod: {Display(barcode)}  ·  GTIN: {report.Product.Gtin}  ·  " +
            $"Sıra no: {report.Product.SerialNumber}  ·  Parti: {report.Product.BatchNumber}  ·  " +
            $"SKT: {report.Product.ExpiryDate:dd.MM.yyyy}";
        ProductQrText.Text = $"Karekod: {report.EnteredQrCode}";
    }

    private static string FormatLocalDetails(LocalQrStatus local)
    {
        var details = new List<string>();
        if (local.ExistsInDatabase) details.Add($"Yerel konum: {FormatLocalLocation(local.Location)}");
        if (!string.IsNullOrWhiteSpace(local.TransferDirection))
            details.Add($"Takas yönü: {FormatTransferDirection(local.TransferDirection)}");
        if (!string.IsNullOrWhiteSpace(local.TransferStatus))
            details.Add($"Takas durumu: {local.TransferStatus}");
        if (!string.IsNullOrWhiteSpace(local.TransferDestinationGln))
            details.Add(
                $"Takas hedefi (veritabanı): {Display(local.TransferDestinationName)} · {local.TransferDestinationGln}");
        if (local.LastMovementCode is "PERAK_OUT" or "PRESC_OUT")
        {
            var patient = !string.IsNullOrWhiteSpace(local.PatientName)
                ? local.PatientName
                : local.PatientId.HasValue ? $"Hasta #{local.PatientId}" : "Hasta belirtilmemiş";
            details.Add($"Satılan kişi (veritabanı): {patient}");
            if (local.SaleId.HasValue) details.Add($"Satış no: {local.SaleId}");
        }
        return details.Count == 0 ? "Yerel ayrıntı bulunamadı." : string.Join(Environment.NewLine, details);
    }

    private static string FormatLatestMovement(LocalQrStatus? local, string? localError)
    {
        if (!string.IsNullOrWhiteSpace(localError)) return $"Yerel hareket sorgulanamadı: {localError}";
        if (local is null || local.LastMovementCode.Length == 0) return "Yerel hareket kaydı bulunamadı.";

        var parts = new List<string>
        {
            local.LastMovementName.Length == 0
                ? local.LastMovementCode
                : $"{local.LastMovementName} ({local.LastMovementCode})"
        };
        if (local.LastMovementDate.HasValue) parts.Add(local.LastMovementDate.Value.ToString("dd.MM.yyyy HH:mm"));
        if (local.LastMovementAddress.Length > 0) parts.Add($"Adres: {local.LastMovementAddress}");
        if (local.ReferenceCode.Length > 0) parts.Add($"Referans: {local.ReferenceCode}");
        return string.Join("  ·  ", parts);
    }

    private static string FormatItsDetails(
        ProductStatusQueryReport report,
        ItsOwnershipAssessment assessment,
        ItsStatusQueryResult result)
    {
        var product = result.Products.FirstOrDefault(candidate =>
                          candidate.Gtin == report.Product.Gtin &&
                          candidate.SerialNumber == report.Product.SerialNumber)
                      ?? result.Products.First();
        var details = new List<string>
        {
            $"GLN1 (son sahip / alıcı): {Display(product.Gln1)}",
            $"GLN2 (arada ise gönderen): {Display(product.Gln2)}",
            $"İTS sonucu: {product.ResultDisplay}"
        };
        if (assessment.State is ItsOwnershipState.IncomingTransfer or
                ItsOwnershipState.OutgoingTransfer or
                ItsOwnershipState.TransferBetweenOtherStakeholders &&
            !string.IsNullOrWhiteSpace(assessment.DestinationGln))
        {
            var destinationName = report.LocalStatus?.TransferDestinationGln == assessment.DestinationGln
                ? report.LocalStatus.TransferDestinationName
                : null;
            details.Add($"Hedef (İTS): {FormatParty(destinationName, assessment.DestinationGln)}");
        }
        if (assessment.State == ItsOwnershipState.SoldToEndConsumer)
            details.Add("İTS durum sorgusu, ilacın satıldığı kişinin kimliğini paylaşmaz.");
        if (!string.IsNullOrWhiteSpace(result.NotificationId))
            details.Add($"Sorgu bildirim no: {result.NotificationId}");
        return string.Join(Environment.NewLine, details);
    }

    private void BeginOperation()
    {
        operationInProgress = true;
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        QueryButton.IsEnabled = false;
        QrCodeBox.IsEnabled = false;
        SetStatus("Yerel veritabanı ve İTS birlikte sorgulanıyor...", null);
    }

    private void EndOperation()
    {
        operationInProgress = false;
        operationCancellation?.Dispose();
        operationCancellation = null;
        QueryButton.IsEnabled = true;
        QrCodeBox.IsEnabled = true;
        QrCodeBox.Focus();
        QrCodeBox.SelectAll();
    }

    private void SetStatus(string text, bool? success)
    {
        StatusText.Text = text;
        StatusText.Foreground = success switch
        {
            true => StatusBrush(true),
            false => StatusBrush(false),
            _ => (Brush)FindResource("MutedInk")
        };
    }

    private static Brush StatusBrush(bool success) => new SolidColorBrush(
        success ? Color.FromRgb(22, 125, 78) : Color.FromRgb(179, 58, 50));

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string DeriveBarcode(string gtin) =>
        gtin.Length == 14 && gtin[0] == '0' ? gtin[1..] : gtin;

    private static string FormatLocalLocation(LocalQrLocation location) => location switch
    {
        LocalQrLocation.Active => "Aktif stok",
        LocalQrLocation.Hanged => "Askıda stok",
        LocalQrLocation.Inactive => "Pasif stok",
        LocalQrLocation.HistoryOnly => "Yalnızca hareket geçmişi",
        _ => "Bulunamadı"
    };

    private static string FormatTransferDirection(string direction) => direction switch
    {
        "outgoing" => "Giden",
        "incoming" => "Gelen",
        _ => direction
    };

    private static string FormatItsErrorSummary(string error)
    {
        if (error.Contains("zaman aş", StringComparison.CurrentCultureIgnoreCase))
            return "İTS sorgusu zaman aşımına uğradı.";
        if (error.Contains("host", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("bağlant", StringComparison.CurrentCultureIgnoreCase) ||
            error.Contains("connection", StringComparison.OrdinalIgnoreCase))
            return "İTS sunucusuna bağlanılamadı.";
        if (error.Contains("kullanıcı", StringComparison.CurrentCultureIgnoreCase) ||
            error.Contains("parola", StringComparison.CurrentCultureIgnoreCase) ||
            error.Contains("şifre", StringComparison.CurrentCultureIgnoreCase) ||
            error.Contains("token", StringComparison.OrdinalIgnoreCase))
            return "İTS giriş bilgileri doğrulanamadı.";
        return "İTS durumu sorgulanamadı.";
    }

    private static string FormatParty(string? name, string gln) =>
        string.IsNullOrWhiteSpace(name) ? gln : $"{name} · {gln}";
}
