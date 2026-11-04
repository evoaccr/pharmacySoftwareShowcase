using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace pharmacyApp.Modules.Invoices;

public partial class OfflineInvoiceListView : UserControl
{
    private const int InvoicePageSize = 300;
    private const double NextPageScrollThreshold = 0.75;
    private readonly OfflineInvoiceRepository repository = new();
    private CancellationTokenSource? listCancellation;
    private CancellationTokenSource? detailCancellation;
    private CancellationTokenSource? stockCancellation;
    private bool initialized;
    private bool nextPageLoading;
    private int nextPageOffset;
    private int totalInvoiceCount;
    private long listGeneration;
    private InvoiceListFilter? activeFilter;

    public ObservableCollection<OfflineInvoiceRow> Invoices { get; } = [];
    public ObservableCollection<OfflineInvoiceLineRow> Lines { get; } = [];

    public OfflineInvoiceListView()
    {
        InitializeComponent();
        RelatedRecordMenus.Attach(InvoiceGrid);
        RelatedRecordMenus.Attach(LineGrid);
        DataContext = this;
        TypeBox.ItemsSource = new[]
        {
            new TypeOption(null, "Tümü"),
            new TypeOption("Gelen Fatura", "Gelen Fatura"),
            new TypeOption("Giden Fatura", "Giden Fatura"),
            new TypeOption("E-Arşiv Fatura", "E-Arşiv Fatura")
        };
        TypeBox.SelectedIndex = 1;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
            return;

        initialized = true;
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref listGeneration);
        listCancellation?.Cancel();
        detailCancellation?.Cancel();
        stockCancellation?.Cancel();
        activeFilter = null;
        nextPageLoading = false;
        initialized = false;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized)
            await ReloadAsync();
    }

    private async void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        if (initialized)
            await ReloadAsync();
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var startDate = StartDatePicker.SelectedDate is { } start
            ? DateOnly.FromDateTime(start)
            : (DateOnly?)null;
        var endDate = EndDatePicker.SelectedDate is { } end
            ? DateOnly.FromDateTime(end)
            : (DateOnly?)null;
        if (startDate > endDate)
        {
            StatusText.Text = "Başlangıç tarihi bitiş tarihinden sonra olamaz.";
            return;
        }

        var filter = new InvoiceListFilter(
            (TypeBox.SelectedItem as TypeOption)?.Key,
            startDate,
            endDate,
            SearchBox.Text);
        var generation = Interlocked.Increment(ref listGeneration);
        nextPageLoading = false;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref listCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        RefreshButton.IsEnabled = false;
        BusyLayer.Visibility = Visibility.Visible;
        StatusText.Text = string.Empty;
        try
        {
            var page = await repository.LoadInvoicesAsync(
                filter.InvoiceType,
                filter.StartDate,
                filter.EndDate,
                filter.Search,
                0,
                InvoicePageSize,
                cancellation.Token);

            if (cancellation.IsCancellationRequested || generation != listGeneration)
                return;

            Invoices.Clear();
            Lines.Clear();
            StockActionButton.Visibility = Visibility.Collapsed;
            ActionHintText.Text = string.Empty;
            LineEmptyText.Text = page.Rows.Count == 0
                ? "Filtrelere uyan fatura bulunamadı."
                : "İçeriğini görmek için bir fatura seçin.";
            LineEmptyText.Visibility = Visibility.Visible;
            foreach (var row in page.Rows)
                Invoices.Add(row);
            activeFilter = filter;
            nextPageOffset = page.Rows.Count;
            totalInvoiceCount = page.TotalCount;
            UpdateInvoiceCounts();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Faturalar yüklenemedi: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(listCancellation, cancellation))
            {
                listCancellation = null;
                RefreshButton.IsEnabled = true;
                BusyLayer.Visibility = Visibility.Collapsed;
                cancellation.Dispose();
            }
        }
    }

    private async void OnInvoiceGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scrollViewer
            || e.VerticalChange == 0
            || scrollViewer.ScrollableHeight <= 0
            || scrollViewer.VerticalOffset / scrollViewer.ScrollableHeight < NextPageScrollThreshold)
            return;

        await LoadNextInvoicePageAsync();
    }

    private async Task LoadNextInvoicePageAsync()
    {
        if (!initialized || nextPageLoading || activeFilter is null || nextPageOffset >= totalInvoiceCount)
            return;

        nextPageLoading = true;
        var generation = listGeneration;
        var filter = activeFilter;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref listCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            var page = await repository.LoadInvoicesAsync(
                filter.InvoiceType,
                filter.StartDate,
                filter.EndDate,
                filter.Search,
                nextPageOffset,
                InvoicePageSize,
                cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != listGeneration)
                return;

            nextPageOffset = page.Rows.Count == 0
                ? page.TotalCount
                : nextPageOffset + page.Rows.Count;
            totalInvoiceCount = page.TotalCount;
            var existingIds = Invoices.Select(row => row.InvoiceUuid).ToHashSet(StringComparer.Ordinal);
            foreach (var row in page.Rows)
            {
                if (existingIds.Add(row.InvoiceUuid))
                    Invoices.Add(row);
            }
            UpdateInvoiceCounts();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Diğer faturalar yüklenemedi: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(listCancellation, cancellation))
            {
                listCancellation = null;
                cancellation.Dispose();
            }
            if (generation == listGeneration)
                nextPageLoading = false;
        }
    }

    private void UpdateInvoiceCounts()
    {
        LoadedCountText.Text = $"Gösterilen: {Invoices.Count:N0}";
        TotalCountText.Text = $"Toplam fatura: {totalInvoiceCount:N0}";
    }

    private async void OnInvoiceSelected(object sender, SelectionChangedEventArgs e)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref detailCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        Lines.Clear();
        StockActionButton.Visibility = Visibility.Collapsed;
        MovementNotePanel.Visibility = Visibility.Collapsed;
        MovementNoteBox.Clear();
        ActionHintText.Text = string.Empty;
        if (InvoiceGrid.SelectedItem is not OfflineInvoiceRow invoice)
        {
            LineEmptyText.Text = "İçeriğini görmek için bir fatura seçin.";
            LineEmptyText.Visibility = Visibility.Visible;
            cancellation.Dispose();
            detailCancellation = null;
            return;
        }

        LineEmptyText.Text = "Fatura içeriği yükleniyor…";
        LineEmptyText.Visibility = Visibility.Visible;
        try
        {
            var rows = await repository.LoadLinesAsync(invoice.InvoiceUuid, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            foreach (var row in rows)
                Lines.Add(row);
            LineEmptyText.Text = rows.Count == 0 ? "Bu faturada içerik satırı bulunmuyor." : string.Empty;
            LineEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateStockActionState(invoice);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LineEmptyText.Text = "Fatura içeriği yüklenemedi.";
            LineEmptyText.Visibility = Visibility.Visible;
            StatusText.Text = $"Fatura içeriği yüklenemedi: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(detailCancellation, cancellation))
            {
                detailCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void OnInvoiceLineExpandClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: OfflineInvoiceLineRow line } toggle) return;
        line.IsExpanded = toggle.IsChecked == true;
        if (LineGrid.ItemContainerGenerator.ContainerFromItem(line) is DataGridRow row)
            row.DetailsVisibility = line.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyInvoiceQrClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InvoiceLineQrRow entry) return;
        try
        {
            Clipboard.SetText(entry.QrCode);
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(23, 97, 61));
            StatusText.Text = "Karekod panoya kopyalandı.";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(141, 46, 46));
            StatusText.Text = $"Karekod kopyalanamadı: {exception.Message}";
        }
    }

    private void UpdateStockActionState(OfflineInvoiceRow invoice)
    {
        if (!invoice.IsIncoming)
        {
            StockActionButton.Visibility = Visibility.Collapsed;
            MovementNotePanel.Visibility = Visibility.Collapsed;
            ActionHintText.Text = "Giden ve e-Arşiv faturaları stok girişine dönüştürülmez.";
            return;
        }

        StockActionButton.Visibility = Visibility.Visible;
        MovementNotePanel.Visibility = Visibility.Visible;
        var stockLines = Lines.Where(line => line.StockTrackingType is "QR" or "BARCODE").ToArray();
        var warning = StockValidationWarning(stockLines);
        if (invoice.CanTakeIntoStock)
        {
            StockActionButton.Content = "Faturayı İçeri Al";
            StockActionButton.Tag = true;
            StockActionButton.IsEnabled = stockLines.Length > 0 && warning is null;
            ActionHintText.Text = warning is not null
                ? $"Stok girişi engellendi: {warning}"
                : stockLines.Length == 0
                    ? "Bu fatura stok ürünü içermiyor; hizmet ve stok dışı satırlar hareket oluşturmaz."
                    : StockSummary(stockLines);
            return;
        }

        StockActionButton.Content = "Faturayı İade Et";
        StockActionButton.Tag = false;
        StockActionButton.IsEnabled = invoice.CanRefund && stockLines.Length > 0 && warning is null;
        ActionHintText.Text = invoice.CanRefund
            ? warning is not null ? $"Mal İade engellendi: {warning}" : StockSummary(stockLines)
            : "Bu fatura bu uygulamaya ait stok giriş kaydı olmadığı için iade edilemez.";
    }

    private async void OnStockActionClicked(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not OfflineInvoiceRow invoice
            || StockActionButton.Tag is not bool save)
            return;

        if (!save)
        {
            var answer = pharmacyApp.Localization.EnglishMessageBox.Show(
                "Faturadaki ürünler DEP_RETURN hareketiyle depoya iade edilecek. Devam etmek istiyor musunuz?",
                "Faturayı İade Et",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var currentUser = ((App)Application.Current).CurrentUser;
        if (currentUser is null)
        {
            StatusText.Text = "Fatura stok işlemi için oturum açmış kullanıcı gereklidir.";
            return;
        }
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref stockCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        StockActionButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(96, 113, 123));
        StatusText.Text = save ? "Mal Alım stok hareketleri oluşturuluyor…" : "Mal İade stok hareketleri oluşturuluyor…";
        try
        {
            var result = await repository.ApplyStockActionAsync(
                invoice.InvoiceUuid, currentUser.UserId, save, MovementNoteBox.Text, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var successMessage = result.IsSaved
                ? $"Fatura stoka alındı: {result.SerializedQuantity:N0} karekod, {result.BarcodeQuantity:N0} barkod hareketi."
                : $"Fatura iade edildi: {result.SerializedQuantity:N0} karekod, {result.BarcodeQuantity:N0} barkod hareketi.";
            await ReloadAndReselectAsync(invoice.InvoiceUuid);
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(23, 97, 61));
            StatusText.Text = successMessage;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(141, 46, 46));
            StatusText.Text = $"Fatura stok işlemi tamamlanamadı: {exception.GetBaseException().Message}";
            UpdateStockActionState(invoice);
        }
        finally
        {
            if (ReferenceEquals(stockCancellation, cancellation))
            {
                stockCancellation = null;
                RefreshButton.IsEnabled = true;
                cancellation.Dispose();
            }
        }
    }

    private async Task ReloadAndReselectAsync(string invoiceUuid)
    {
        await ReloadAsync();
        var refreshed = Invoices.FirstOrDefault(row => row.InvoiceUuid == invoiceUuid);
        if (refreshed is null) return;
        InvoiceGrid.SelectedItem = refreshed;
        InvoiceGrid.ScrollIntoView(refreshed);
    }

    private static string StockSummary(IEnumerable<OfflineInvoiceLineRow> lines)
    {
        var array = lines.ToArray();
        var qrCount = array.Sum(line => line.QrCodes.Count);
        var barcodeCount = array.Where(line => line.StockTrackingType == "BARCODE")
            .Sum(line => line.Quantity is { } quantity && quantity >= 0 && quantity <= int.MaxValue
                                                  && quantity == decimal.Truncate(quantity)
                ? decimal.ToInt32(quantity)
                : 0);
        return $"{qrCount:N0} tekil karekod ve {barcodeCount:N0} barkod adedi işlenecek; stok dışı satırlar atlanacak.";
    }

    private static string? StockValidationWarning(IEnumerable<OfflineInvoiceLineRow> lines)
    {
        foreach (var line in lines)
        {
            if (line.TrackingWarning.Length > 0) return line.TrackingWarning;
            if (line.Quantity is null || line.Quantity <= 0 || line.Quantity > int.MaxValue
                                      || line.Quantity != decimal.Truncate(line.Quantity.Value))
                return $"{line.LineNumber}. satırın stok miktarı pozitif tam sayı değil.";
        }
        return null;
    }

    private sealed record TypeOption(string? Key, string DisplayName);
    private sealed record InvoiceListFilter(
        string? InvoiceType,
        DateOnly? StartDate,
        DateOnly? EndDate,
        string Search);
}
