using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using pharmacyApp.Modules.Invoices;

namespace invoiceXmlImporter;

public partial class MainWindow : Window
{
    private readonly CheckpointStore store = new();
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? cancellation;
    private DateTimeOffset runStarted;
    private TimeSpan runBaseElapsed;
    private TimeSpan previousElapsed;
    private bool closeAfterStop;
    private bool applyingSavedOptions;
    private bool databaseResetRunning;

    public MainWindow()
    {
        InitializeComponent();
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateElapsed(), Dispatcher);
        ShowSavedState();
        UpdateDebugOptionState();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || databaseResetRunning) return;
        var checkpoint = store.Load();
        if (checkpoint is null || checkpoint.Phase == "Completed")
        {
            try { checkpoint = CreateCheckpointFromControls(); }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Debug ayarları", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        cancellation = new CancellationTokenSource();
        runStarted = DateTimeOffset.Now;
        runBaseElapsed = TimeSpan.FromSeconds(checkpoint.AccumulatedSeconds);
        previousElapsed = runBaseElapsed;
        SetRunning(true);
        timer.Start();
        ErrorPanel.Visibility = Visibility.Collapsed;
        try
        {
            var coordinator = new InvoiceXmlImportCoordinator(store);
            await coordinator.RunAsync(checkpoint, new Progress<ImportProgress>(UpdateProgress), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ErrorText.Text = exception.GetBaseException().Message;
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            timer.Stop();
            cancellation.Dispose();
            cancellation = null;
            SetRunning(false);
            ShowSavedState();
            if (closeAfterStop) Close();
        }
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        PhaseText.Text = "Duraklatılıyor";
        MessageText.Text = "Geçerli güvenli işlem tamamlanınca kontrol noktası kaydedilecek…";
        cancellation?.Cancel();
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || databaseResetRunning) return;
        ImportCheckpoint checkpoint;
        try { checkpoint = CreateCheckpointFromControls(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Debug ayarları", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        store.Save(checkpoint);
        ShowSavedState();
    }

    private void OnRecheckQr(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || databaseResetRunning) return;
        var checkpoint = store.Load();
        if (checkpoint is null || checkpoint.Packages.Count == 0)
        {
            MessageBox.Show(this, "Yeniden işlenecek indirilmiş XML paketi bulunamadı.",
                "Karekod kontrolü", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var package in checkpoint.Packages)
        {
            package.ImportedEntries = 0;
            package.ImportComplete = false;
            package.ImportedQrCodes = 0;
            package.IncompleteQrLines = 0;
            package.IndividualXmlQrRetries = 0;
        }
        checkpoint.Phase = "Importing";
        checkpoint.ResumePhase = "Importing";
        checkpoint.LastError = string.Empty;
        checkpoint.AccumulatedSeconds = 0;
        store.Save(checkpoint);
        ShowSavedState();
        OnStart(sender, e);
    }

    private async void OnClearInvoiceTables(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || databaseResetRunning) return;
        var answer = MessageBox.Show(this,
            "Bu işlem yapılandırılmış veritabanındaki tüm fatura, fatura satırı, karekod, "
            + "fatura hareketi, Farmassist eşleme, güncelleme takip ve adres kayıtlarını kalıcı olarak siler.\n\n"
            + "İndirilen ZIP/XML dosyaları ve yerel kontrol noktası silinmez. Devam edilsin mi?",
            "Fatura tablolarını temizle",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        databaseResetRunning = true;
        SetRunning(false);
        ErrorPanel.Visibility = Visibility.Collapsed;
        PhaseText.Text = "Fatura tabloları temizleniyor";
        MessageText.Text = "Veritabanı işlemi tek bir güvenli işlem olarak yürütülüyor…";
        try
        {
            var repository = new CompactInvoiceRepository();
            var result = await repository.ClearInvoiceDataAsync();
            PhaseText.Text = "Hazır";
            MessageText.Text = $"{result.TotalRows:N0} fatura ilişkili kayıt temizlendi. Yeni bir aktarım başlatabilirsiniz.";
            MessageBox.Show(this,
                $"Fatura tabloları temizlendi.\n\n"
                + $"Fatura: {result.Invoices:N0}\n"
                + $"Satır: {result.InvoiceLines:N0}\n"
                + $"Karekod: {result.InvoiceLineQrCodes:N0}\n"
                + $"Adres: {result.Addresses:N0}\n"
                + $"Diğer takip/eşleme kayıtları: "
                + $"{result.InvoiceStockLogs + result.FarmassistMappings + result.FarmassistStockMappings + result.IncrementalSyncStates + result.IncrementalSyncItems:N0}",
                "Fatura tablolarını temizle",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            var message = exception.GetBaseException().Message;
            ErrorText.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
            PhaseText.Text = "Hata";
            MessageText.Text = "Fatura tabloları temizlenemedi; veritabanı işlemi geri alındı.";
        }
        finally
        {
            databaseResetRunning = false;
            SetRunning(false);
        }
    }

    private void UpdateProgress(ImportProgress value)
    {
        previousElapsed = value.Elapsed;
        PhaseText.Text = value.Phase;
        MessageText.Text = value.Message;
        CurrentTypeText.Text = Empty(value.CurrentType);
        CurrentMonthText.Text = Empty(value.CurrentMonth);
        CurrentPageText.Text = value.CurrentPage == 0 ? "—" : value.CurrentChunk == 0
            ? $"{value.CurrentPage} / {value.TotalPages}" : $"{value.CurrentPage} / {value.TotalPages}  •  {value.CurrentChunk} / {value.TotalChunks}";
        CurrentInvoiceText.Text = Empty(value.CurrentInvoice);
        CurrentAddressText.Text = Empty(value.CurrentAddress);
        OverallProgress.Maximum = Math.Max(1, value.Total);
        OverallProgress.Value = Math.Min(value.Completed, OverallProgress.Maximum);
        ProgressCountText.Text = $"{value.Completed:N0} / {value.Total:N0}";
        AverageText.Text = $"{value.AverageSeconds:N2} sn";
        QrProgressText.Text = $"Karekod: {value.ImportedQrCodes:N0} • "
            + $"Eksik kaynak satırı: {value.IncompleteQrLines:N0} • "
            + $"Tekil XML kontrolü: {value.IndividualXmlQrRetries:N0}";
        foreach (var type in value.Types) UpdateType(type);
        if (!string.IsNullOrWhiteSpace(value.Error))
        {
            ErrorText.Text = value.Error;
            ErrorPanel.Visibility = Visibility.Visible;
        }
    }

    private void UpdateType(ImportTypeProgress type)
    {
        var text = $"Portal {type.PortalTotal:N0} • Bulunan {type.Found:N0}\n"
            + $"İndirilen {type.Downloaded:N0} • Paketlenen {type.Packaged:N0} • Aktarılan {type.Imported:N0}";
        var (card, counts) = type.Key switch
        {
            "incoming-invoice" => (IncomingCard, IncomingCounts),
            "outgoing-invoice" => (OutgoingCard, OutgoingCounts),
            _ => (ArchiveCard, ArchiveCounts)
        };
        counts.Text = text;
        card.BorderBrush = type.Active ? (Brush)FindResource("Accent") : (Brush)FindResource("Border");
        card.BorderThickness = type.Active ? new Thickness(2) : new Thickness(1);
        card.Background = type.Active ? new SolidColorBrush(Color.FromRgb(237, 250, 248)) : (Brush)FindResource("Surface");
    }

    private void ShowSavedState()
    {
        var checkpoint = store.Load();
        CheckpointText.Text = checkpoint is null
            ? $"Kontrol noktası: {store.CheckpointPath}"
            : $"Kontrol noktası: {checkpoint.UpdatedAt:dd.MM.yyyy HH:mm:ss}  •  {TurkishPhase(checkpoint.Phase)}";
        if (checkpoint is null) return;
        ApplyCheckpointOptions(checkpoint);
        if (checkpoint.Phase is "Paused" or "Faulted")
        {
            PhaseText.Text = TurkishPhase(checkpoint.Phase);
            MessageText.Text = "Başlat / Devam Et düğmesiyle son güvenli noktadan sürdürülebilir.";
        }
        else if (checkpoint.Phase == "Completed")
        {
            PhaseText.Text = "Tamamlandı";
            MessageText.Text = "Yeni bir tam aktarım başlatabilirsiniz.";
        }
        if (!string.IsNullOrWhiteSpace(checkpoint.LastError))
        {
            ErrorText.Text = checkpoint.LastError;
            ErrorPanel.Visibility = Visibility.Visible;
        }
        var typeProgress = new[]
        {
            ("incoming-invoice", "Gelen Fatura"),
            ("outgoing-invoice", "Giden Fatura"),
            ("archive-invoice", "E-Arşiv Faturası")
        }.Select(definition =>
        {
            var pages = checkpoint.Pages.Where(p => p.TypeKey == definition.Item1).ToArray();
            var packages = checkpoint.Packages.Where(p => p.TypeKey == definition.Item1).ToArray();
            return new ImportTypeProgress(definition.Item1, definition.Item2,
                checkpoint.ExpectedTotals.GetValueOrDefault(definition.Item1), pages.Sum(p => p.XmlCount),
                pages.Where(p => p.DownloadComplete).Sum(p => p.XmlCount), packages.Sum(p => p.XmlCount),
                packages.Sum(p => p.ImportedEntries), false);
        });
        foreach (var type in typeProgress) UpdateType(type);
        RecheckQrButton.IsEnabled = cancellation is null
            && checkpoint.Phase == "Completed"
            && checkpoint.Packages.Any(package => package.ImportComplete && File.Exists(package.ZipPath));
    }

    private void SetRunning(bool running)
    {
        var idle = !running && !databaseResetRunning;
        StartButton.IsEnabled = idle;
        NewButton.IsEnabled = idle;
        ClearInvoiceTablesButton.IsEnabled = idle;
        RecheckQrButton.IsEnabled = idle
            && store.Load() is { Phase: "Completed" } checkpoint
            && checkpoint.Packages.Any(package => package.ImportComplete && File.Exists(package.ZipPath));
        PauseButton.IsEnabled = running;
        DebugModeCheckBox.IsEnabled = idle;
        UpdateDebugOptionState(!idle);
    }

    private void UpdateElapsed()
    {
        if (cancellation is not null) previousElapsed = runBaseElapsed + (DateTimeOffset.Now - runStarted);
        ElapsedText.Text = previousElapsed.ToString(@"hh\:mm\:ss");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (databaseResetRunning)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Veritabanı temizleme işlemi tamamlanana kadar pencere kapatılamaz.",
                "Fatura tabloları temizleniyor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (cancellation is null) return;
        e.Cancel = true;
        closeAfterStop = true;
        OnPause(this, new RoutedEventArgs());
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void OnDebugOptionsChanged(object sender, RoutedEventArgs e) => UpdateDebugOptionState();

    private void UpdateDebugOptionState(bool running = false)
    {
        if (DebugOptionsPanel is null || applyingSavedOptions) return;
        DebugOptionsPanel.IsEnabled = !running && DebugModeCheckBox.IsChecked == true;
        if (InvoiceTypeCombo is not null)
            InvoiceTypeCombo.IsEnabled = DebugOptionsPanel.IsEnabled && OnlyTypeCheckBox.IsChecked == true;
    }

    private ImportCheckpoint CreateCheckpointFromControls()
    {
        var debug = DebugModeCheckBox.IsChecked == true;
        int? maximum = null;
        if (debug && !string.IsNullOrWhiteSpace(MaximumInvoicesBox.Text))
        {
            if (!int.TryParse(MaximumInvoicesBox.Text.Trim(), out var parsed) || parsed <= 0)
                throw new InvalidOperationException("Fatura sınırı pozitif bir tam sayı olmalıdır.");
            maximum = parsed;
        }
        return new ImportCheckpoint
        {
            DebugMode = debug,
            SelectedTypeKey = debug && OnlyTypeCheckBox.IsChecked == true
                ? SelectedTag(InvoiceTypeCombo, "incoming-invoice") : string.Empty,
            MaximumInvoices = maximum,
            DownloadOrder = debug ? SelectedTag(DownloadOrderCombo, InvoiceDownloadOrder.NewestFirst)
                : InvoiceDownloadOrder.NewestFirst,
            WithoutFarmassistRecords = debug && WithoutFarmassistCheckBox.IsChecked == true
        };
    }

    private void ApplyCheckpointOptions(ImportCheckpoint checkpoint)
    {
        applyingSavedOptions = true;
        try
        {
            DebugModeCheckBox.IsChecked = checkpoint.DebugMode;
            OnlyTypeCheckBox.IsChecked = checkpoint.DebugMode && !string.IsNullOrWhiteSpace(checkpoint.SelectedTypeKey);
            SelectTag(InvoiceTypeCombo, checkpoint.SelectedTypeKey, "incoming-invoice");
            MaximumInvoicesBox.Text = checkpoint.MaximumInvoices?.ToString() ?? string.Empty;
            SelectTag(DownloadOrderCombo, checkpoint.DownloadOrder, InvoiceDownloadOrder.NewestFirst);
            WithoutFarmassistCheckBox.IsChecked = checkpoint.WithoutFarmassistRecords;
        }
        finally { applyingSavedOptions = false; }
        UpdateDebugOptionState(cancellation is not null);
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectTag(ComboBox combo, string value, string fallback)
    {
        var wanted = string.IsNullOrWhiteSpace(value) ? fallback : value;
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), wanted, StringComparison.Ordinal))
            ?? combo.Items[0];
    }
    private static string TurkishPhase(string phase) => phase switch
    {
        "Downloading" => "İndiriliyor", "Packaging" => "Paketleniyor", "Importing" => "Veritabanına aktarılıyor", "Paused" => "Duraklatıldı",
        "Faulted" => "Hata", "Completed" => "Tamamlandı", _ => phase
    };
}
