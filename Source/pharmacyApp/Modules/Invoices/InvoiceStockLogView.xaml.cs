using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace pharmacyApp.Modules.Invoices;

public partial class InvoiceStockLogView : UserControl
{
    private readonly InvoiceStockLogRepository repository = new();
    private readonly DispatcherTimer reloadTimer;
    private CancellationTokenSource? loadCancellation;
    private CancellationTokenSource? detailCancellation;
    private bool initialized;
    private bool suppressFilters;

    public InvoiceStockLogView()
    {
        InitializeComponent();
        RelatedRecordMenus.Attach(DetailGrid);
        reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        reloadTimer.Tick += async (_, _) =>
        {
            reloadTimer.Stop();
            await ReloadAsync();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        suppressFilters = true;
        ActionFilter.ItemsSource = new[]
        {
            new InvoiceStockLogActionOption(string.Empty, "Tümü"),
            new InvoiceStockLogActionOption("SAVE", "Stoka Alındı"),
            new InvoiceStockLogActionOption("REFUND", "İade Edildi"),
            new InvoiceStockLogActionOption("PARTIAL_REFUND", "Kısmi İade Edildi")
        };
        ActionFilter.SelectedIndex = 0;
        var today = DateTime.Today;
        StartDatePicker.SelectedDate = new DateTime(today.Year, today.Month, 1);
        EndDatePicker.SelectedDate = today;
        try
        {
            var users = await repository.LoadUsersAsync();
            UserFilter.ItemsSource = new[] { new InvoiceStockLogUserOption(string.Empty, "Tümü") }.Concat(users);
            UserFilter.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Kullanıcılar yüklenemedi: {exception.Message}";
        }
        finally
        {
            suppressFilters = false;
        }
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        reloadTimer.Stop();
        CancelAndDispose(ref loadCancellation);
        CancelAndDispose(ref detailCancellation);
    }

    private void OnFilterChanged(object sender, EventArgs e)
    {
        if (!initialized || suppressFilters) return;
        reloadTimer.Stop();
        reloadTimer.Start();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string period }) return;
        var today = DateTime.Today;
        var start = period switch
        {
            "Week" => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            "Month" => new DateTime(today.Year, today.Month, 1),
            "Year" => new DateTime(today.Year, 1, 1),
            _ => today
        };
        suppressFilters = true;
        StartDatePicker.SelectedDate = start;
        EndDatePicker.SelectedDate = today;
        suppressFilters = false;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (!initialized || StartDatePicker.SelectedDate is null || EndDatePicker.SelectedDate is null) return;
        CancelAndDispose(ref loadCancellation);
        loadCancellation = new CancellationTokenSource();
        var token = loadCancellation.Token;
        LoadingText.Text = "Fatura hareketleri yükleniyor...";
        LoadingOverlay.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        StatusText.Text = string.Empty;
        DetailGrid.ItemsSource = null;
        DetailTitle.Text = "Bir fatura hareketi seçin.";
        NotesBox.Text = string.Empty;
        NotesBox.IsEnabled = false;
        SaveNotesButton.IsEnabled = false;
        try
        {
            var start = new DateTimeOffset(StartDatePicker.SelectedDate.Value.Date, TimeZoneInfo.Local.GetUtcOffset(StartDatePicker.SelectedDate.Value.Date));
            var endLocal = EndDatePicker.SelectedDate.Value.Date.AddDays(1);
            var end = new DateTimeOffset(endLocal, TimeZoneInfo.Local.GetUtcOffset(endLocal));
            var query = new InvoiceStockLogQuery(
                start.ToUniversalTime(), end.ToUniversalTime(),
                (ActionFilter.SelectedItem as InvoiceStockLogActionOption)?.Code ?? string.Empty,
                (UserFilter.SelectedItem as InvoiceStockLogUserOption)?.UserId ?? string.Empty,
                SearchBox.Text.Trim());
            var rows = await repository.LoadAsync(query, token);
            LogGrid.ItemsSource = rows;
            CountText.Text = $"Toplam kayıt: {rows.Count:N0}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LogGrid.ItemsSource = null;
            CountText.Text = "Toplam kayıt: 0";
            StatusText.Text = $"Fatura hareketleri yüklenemedi: {exception.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private async void OnLogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelAndDispose(ref detailCancellation);
        if (LogGrid.SelectedItem is not InvoiceStockLogRow log)
        {
            DetailGrid.ItemsSource = null;
            NotesBox.IsEnabled = false;
            SaveNotesButton.IsEnabled = false;
            return;
        }
        DetailTitle.Text = $"{log.ActionDisplay} · {log.ReferenceCode} · Fatura {log.InvoiceUuid}";
        NotesBox.Text = log.Notes;
        NotesBox.IsEnabled = true;
        SaveNotesButton.IsEnabled = true;
        detailCancellation = new CancellationTokenSource();
        var token = detailCancellation.Token;
        LoadingText.Text = "Hareket ayrıntıları yükleniyor...";
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var details = await repository.LoadDetailsAsync(log, token);
            if (ReferenceEquals(LogGrid.SelectedItem, log)) DetailGrid.ItemsSource = details;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Hareket ayrıntıları yüklenemedi: {exception.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested) LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnSaveNotesClicked(object sender, RoutedEventArgs e)
    {
        if (LogGrid.SelectedItem is not InvoiceStockLogRow log) return;
        SaveNotesButton.IsEnabled = false;
        try
        {
            await repository.SaveNotesAsync(log.RecordId, NotesBox.Text);
            log.Notes = NotesBox.Text.Trim();
            LogGrid.Items.Refresh();
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(23, 97, 61));
            StatusText.Text = "Kullanıcı notu kaydedildi.";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(141, 46, 46));
            StatusText.Text = $"Not kaydedilemedi: {exception.Message}";
        }
        finally
        {
            SaveNotesButton.IsEnabled = LogGrid.SelectedItem is InvoiceStockLogRow;
        }
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }
}
