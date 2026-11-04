using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.StockMovements;

public partial class ManualStockAdjustmentView : UserControl
{
    private static readonly Brush ErrorBrush = BrushFrom(179, 58, 50);
    private static readonly Brush ErrorBackground = BrushFrom(251, 232, 230);
    private static readonly Brush WarningBrush = BrushFrom(164, 109, 16);
    private static readonly Brush WarningBackground = BrushFrom(252, 244, 219);
    private static readonly Brush SuccessBrush = BrushFrom(22, 125, 78);
    private static readonly Brush SuccessBackground = BrushFrom(229, 244, 236);

    private readonly ObservableCollection<ManualStockGroup> rows = [];
    private readonly IProductStatusQueryService queryService = new ItsProductStatusQueryService(
        new ItsProductStatusQueryRepository(), new ItsApiClient());
    private readonly ManualStockRepository repository = new();
    private CancellationTokenSource? cancellation;
    private StockMovementDirection direction = StockMovementDirection.Out;
    private bool operationInProgress;

    public ManualStockAdjustmentView()
    {
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        RowsGrid.ItemsSource = rows;
        UpdateUiState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QrCodeBox.Focus();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    private void OnQrCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        _ = AddAsync();
    }

    private async void OnAddClicked(object sender, RoutedEventArgs e) => await AddAsync();

    private async Task AddAsync()
    {
        var code = QrCodeBox.Text.Trim();
        if (code.Length == 0 || operationInProgress) return;
        if (AllQrRows().Any(row => row.QrCode == code))
        {
            ShowStatus("Bu karekod zaten listede.", false);
            QrCodeBox.SelectAll();
            return;
        }

        try
        {
            BeginOperation("Yerel stok ve İTS durumu sorgulanıyor...");
            cancellation = new CancellationTokenSource();
            var settings = ApplicationSettingsStore.Current;
            var report = await queryService.QueryAsync(
                code,
                ItsEnvironment.Production,
                new ItsCredentials(settings.ItsUsername, settings.ItsPassword),
                settings.ItsPharmacyGln,
                cancellation.Token);
            var row = ManualStockRow.From(report);
            var group = rows.FirstOrDefault(candidate => candidate.Barcode == row.Barcode);
            if (group is null)
            {
                group = new ManualStockGroup(row.Barcode, row.ProductName);
                rows.Add(group);
            }
            group.Items.Add(row);
            group.IsExpanded = true;
            QrCodeBox.Clear();
            ShowStatus("Karekod listeye eklendi ve durumları doğrulandı.", true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Karekod eklenemedi: {exception.Message}", false);
        }
        finally
        {
            EndOperation();
            QrCodeBox.Focus();
        }
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManualStockRow row) return;
        var group = rows.FirstOrDefault(candidate => candidate.Items.Contains(row));
        if (group is null) return;
        group.Items.Remove(row);
        if (group.Items.Count == 0) rows.Remove(group);
        ShowStatus("Karekod listeden kaldırıldı.", true);
        UpdateUiState();
    }

    private void OnRemoveGroupClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManualStockGroup group) return;
        rows.Remove(group);
        ShowStatus("İlaç ve altındaki karekodlar listeden kaldırıldı.", true);
        UpdateUiState();
    }

    private void OnDirectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        direction = DirectionToggle.IsChecked == true ? StockMovementDirection.In : StockMovementDirection.Out;
        UpdateUiState();
    }

    private void OnItsOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateUiState();
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (operationInProgress) return;
        var allRows = AllQrRows().ToArray();
        if (allRows.Length == 0) return;

        var candidates = GetEligibleRows(allRows).ToArray();
        if (candidates.Length == 0)
        {
            ShowStatus(
                direction == StockMovementDirection.In
                    ? "Giriş yapılabilecek karekod bulunamadı. Karekod yerel stokta olmamalı ve İTS bildirimi açıksa Mal Alım yapılabilir durumda olmalıdır."
                    : "Çıkış yapılabilecek karekod bulunamadı. Karekod yerel stokta ve İTS bildirimi açıksa eczane üzerinde kayıtlı olmalıdır.",
                false);
            return;
        }

        try
        {
            BeginOperation(direction == StockMovementDirection.In
                ? "Uygun karekodlar için stok girişi hazırlanıyor..."
                : "Uygun karekodlar için stok çıkışı hazırlanıyor...");
            cancellation = new CancellationTokenSource();
            var user = (Application.Current as App)?.CurrentUser?.UserId ?? "admin";
            StockItsSendResult? itsResult = null;
            if (SendItsCheckBox.IsChecked == true)
            {
                BusyText.Text = direction == StockMovementDirection.In
                    ? "İTS Mal Alım bildirimi gönderiliyor..."
                    : "İTS satış bildirimi gönderiliyor...";
                await new UnnotifiedItsMovementRepository().ValidateBeforeActionAsync(
                    candidates.Select(row => row.QrCode).ToArray(), direction, true, cancellation.Token);
                itsResult = await new StockItsNotificationSender().SendAsync(
                    direction == StockMovementDirection.In
                        ? StockItsNotificationKind.Acceptance
                        : StockItsNotificationKind.Sale,
                    candidates.Select(row => row.QrCode).ToArray(),
                    user,
                    cancellation.Token);
            }

            BusyText.Text = "Yerel stok hareketleri kaydediliyor...";
            var id = await repository.CompleteAsync(
                direction,
                candidates.Select(row => row.QrCode).ToArray(),
                NoteBox.Text,
                user,
                SendItsCheckBox.IsChecked == true,
                itsResult?.NotificationId,
                cancellation.Token);
            if (itsResult is not null)
                await new StockItsNotificationSender().MarkLocalCompletedAsync(
                    itsResult.OperationId, id, CancellationToken.None);

            RemoveRows(candidates);
            NoteBox.Clear();
            var remaining = AllQrRows().Count();
            ShowStatus(
                $"Manuel {(direction == StockMovementDirection.In ? "giriş" : "çıkış")} kaydedildi: M{id}." +
                (remaining > 0 ? $" İşleme uygun olmayan {remaining} karekod listede bırakıldı." : string.Empty),
                true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Manuel stok hareketi tamamlanamadı: {exception.Message}", false);
        }
        finally
        {
            EndOperation();
        }
    }

    private IEnumerable<ManualStockRow> GetEligibleRows(IEnumerable<ManualStockRow> source)
    {
        var sendIts = SendItsCheckBox.IsChecked == true;
        return source.Where(row => direction == StockMovementDirection.Out
            ? row.InLocalStock && (!sendIts || row.ItsOwned)
            : !row.InLocalStock && (!sendIts || row.CanAccept));
    }

    private IEnumerable<ManualStockRow> AllQrRows() => rows.SelectMany(group => group.Items);

    private void RemoveRows(IEnumerable<ManualStockRow> removedRows)
    {
        foreach (var row in removedRows.ToArray())
        {
            var group = rows.First(candidate => candidate.Items.Contains(row));
            group.Items.Remove(row);
            if (group.Items.Count == 0) rows.Remove(group);
        }
    }

    private void BeginOperation(string text)
    {
        operationInProgress = true;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        BusyText.Text = text;
        BusyOverlay.Visibility = Visibility.Visible;
        QrCodeBox.IsEnabled = false;
        AddQrButton.IsEnabled = false;
        DirectionToggle.IsEnabled = false;
        SendItsCheckBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        operationInProgress = false;
        BusyOverlay.Visibility = Visibility.Collapsed;
        QrCodeBox.IsEnabled = true;
        AddQrButton.IsEnabled = true;
        DirectionToggle.IsEnabled = true;
        SendItsCheckBox.IsEnabled = true;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        if (RowsGrid is null) return;
        var qrRows = AllQrRows().ToArray();
        var eligibleCount = GetEligibleRows(qrRows).Count();
        ProductCountText.Text = $"{rows.Count} ilaç";
        QrCountText.Text = $"{qrRows.Length} karekod";
        EmptyStatePanel.Visibility = qrRows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var isEntry = direction == StockMovementDirection.In;
        DirectionBadgeText.Text = isEntry ? "Giriş" : "Çıkış";
        DirectionBadge.Background = isEntry ? SuccessBackground : ErrorBackground;
        DirectionBadgeText.Foreground = isEntry ? SuccessBrush : ErrorBrush;
        SaveButton.Content = isEntry ? "Girişi Kaydet" : "Çıkışı Kaydet";
        SaveButton.Background = isEntry ? BrushFrom(22, 139, 82) : BrushFrom(179, 58, 50);
        SaveButton.IsEnabled = !operationInProgress && eligibleCount > 0;
        SaveSummaryText.Text = qrRows.Length == 0
            ? "Kaydedilecek karekod yok"
            : eligibleCount == qrRows.Length
                ? $"{eligibleCount} karekod kaydedilecek"
                : $"{eligibleCount} / {qrRows.Length} karekod uygun";
    }

    private void ShowStatus(string message, bool success)
    {
        StatusText.Text = message;
        StatusText.Foreground = success ? SuccessBrush : ErrorBrush;
    }

    private sealed record ManualStockRow(
        string QrCode,
        string Barcode,
        string ProductName,
        bool InLocalStock,
        bool ItsOwned,
        bool CanAccept,
        string ItsDetail)
    {
        public string ItsIcon => ItsOwned ? "✓" : CanAccept ? "→" : "×";
        public string ItsStatusText => ItsOwned
            ? "Üzerinize kayıtlı"
            : CanAccept ? "Mal Alım yapılabilir" : "Üzerinize kayıtlı değil";
        public Brush ItsBrush => ItsOwned ? SuccessBrush : CanAccept ? WarningBrush : ErrorBrush;
        public Brush ItsBackground => ItsOwned ? SuccessBackground : CanAccept ? WarningBackground : ErrorBackground;
        public string StockIcon => InLocalStock ? "✓" : "×";
        public string StockStatusText => InLocalStock ? "Stokta" : "Stokta değil";
        public Brush StockBrush => InLocalStock ? SuccessBrush : ErrorBrush;
        public Brush StockBackground => InLocalStock ? SuccessBackground : ErrorBackground;

        public static ManualStockRow From(ProductStatusQueryReport report)
        {
            var owned = report.ItsAssessment?.IsRegisteredToThisPharmacy == true;
            var canAccept = report.ItsAssessment?.State == ItsOwnershipState.IncomingTransfer;
            var barcode = !string.IsNullOrWhiteSpace(report.LocalStatus?.Barcode)
                ? report.LocalStatus.Barcode
                : report.Product.Gtin;
            var productName = !string.IsNullOrWhiteSpace(report.LocalStatus?.ProductName)
                ? report.LocalStatus.ProductName
                : $"Barkod {barcode}";
            return new ManualStockRow(
                report.EnteredQrCode,
                barcode,
                productName,
                report.LocalStatus?.Location == LocalQrLocation.Active,
                owned,
                canAccept,
                report.ItsAssessment?.Summary ?? report.ItsError ?? "İTS durumu sorgulanamadı.");
        }
    }

    private sealed class ManualStockGroup : INotifyPropertyChanged
    {
        private bool isExpanded = true;

        public ManualStockGroup(string barcode, string productName)
        {
            Barcode = barcode;
            ProductName = productName;
            Items.CollectionChanged += (_, _) =>
            {
                Raise(nameof(Amount));
                Raise(nameof(ItsSummary));
                Raise(nameof(StockSummary));
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Barcode { get; }
        public string BarcodeDisplay => $"Barkod: {Barcode}";
        public string ProductName { get; }
        public ObservableCollection<ManualStockRow> Items { get; } = [];
        public int Amount => Items.Count;
        public string ItsSummary => string.Empty;
        public string StockSummary => string.Empty;

        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                Raise(nameof(IsExpanded));
            }
        }

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static SolidColorBrush BrushFrom(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));
}
