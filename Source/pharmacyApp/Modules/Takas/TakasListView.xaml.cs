using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.Modules.Takas;

public partial class TakasListView : UserControl, IModuleTabRequestSource, IModuleBackgroundWorkSource
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly TakasListRepository repository = new();
    private readonly TakasRepository pharmacyRepository = new();
    private readonly ItsApiClient apiClient = new();
    private readonly DispatcherTimer filterTimer;
    private CancellationTokenSource? loadCancellation;
    private CancellationTokenSource? productSearchCancellation;
    private CancellationTokenSource? pharmacySearchCancellation;
    private CancellationTokenSource? detailCancellation;
    private string selectedBarcode = string.Empty;
    private string selectedBrand = string.Empty;
    private int? selectedPharmacyId;
    private bool suppressFilterChanges;
    private bool suppressSelectionChanges;
    private bool initialized;
    private bool cancellationInProgress;
    private TakasListItem? selectedTransfer;

    public event Action<ModuleTabRequest>? TabRequested;
    public event Action<bool>? BackgroundWorkStateChanged;
    public bool IsBackgroundWorkRunning => cancellationInProgress;

    public TakasListView()
    {
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        foreach (var grid in new[] { TakasGrid, IncomingTakasGrid, OutgoingTakasGrid, TakasDetailGrid }) RelatedRecordMenus.Attach(grid);
        filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        filterTimer.Tick += async (_, _) =>
        {
            filterTimer.Stop();
            await ReloadAsync();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        suppressFilterChanges = true;
        StartDatePicker.SelectedDate = DateTime.Today;
        EndDatePicker.SelectedDate = DateTime.Today;
        try
        {
            var users = await repository.LoadUsersAsync(CancellationToken.None);
            UserFilter.ItemsSource = new[] { new TakasListUserFilterOption(string.Empty, "Hepsi") }.Concat(users);
            UserFilter.SelectedIndex = 0;
            SetAllStatuses(true);
        }
        catch (Exception exception)
        {
            CountText.Text = $"Kullanıcılar yüklenemedi: {exception.Message}";
        }
        finally
        {
            suppressFilterChanges = false;
        }
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        filterTimer.Stop();
        CancellationTokenSourceGuard.CancelAndDispose(ref loadCancellation);
        CancellationTokenSourceGuard.CancelAndDispose(ref productSearchCancellation);
        CancellationTokenSourceGuard.CancelAndDispose(ref pharmacySearchCancellation);
        CancellationTokenSourceGuard.CancelAndDispose(ref detailCancellation);
    }

    private void QueueReload()
    {
        if (suppressFilterChanges || !initialized || cancellationInProgress) return;
        filterTimer.Stop();
        filterTimer.Start();
    }

    private void OnSimpleFilterChanged(object sender, TextChangedEventArgs e) => QueueReload();
    private void OnDateChanged(object sender, SelectionChangedEventArgs e) => QueueReload();
    private void OnComboFilterChanged(object sender, SelectionChangedEventArgs e) => QueueReload();
    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        filterTimer.Stop();
        CancellationTokenSourceGuard.CancelAndDispose(ref productSearchCancellation);
        CancellationTokenSourceGuard.CancelAndDispose(ref pharmacySearchCancellation);
        suppressFilterChanges = true;
        try
        {
            selectedBarcode = string.Empty;
            selectedBrand = string.Empty;
            selectedPharmacyId = null;
            QrFilterBox.Clear();
            ProductFilterBox.Clear();
            PharmacyFilterBox.Clear();
            MinimumDebtBox.Clear();
            MaximumDebtBox.Clear();
            StartDatePicker.SelectedDate = DateTime.Today;
            EndDatePicker.SelectedDate = DateTime.Today;
            UserFilter.SelectedIndex = UserFilter.Items.Count > 0 ? 0 : -1;
            DirectionFilter.SelectedIndex = 0;
            SetAllStatuses(true);
            ProductSuggestionPopup.IsOpen = false;
            PharmacySuggestionPopup.IsOpen = false;
            StatusFilterPopup.IsOpen = false;
        }
        finally
        {
            suppressFilterChanges = false;
        }
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (!initialized || StartDatePicker.SelectedDate is null || EndDatePicker.SelectedDate is null) return;
        CancellationTokenSourceGuard.CancelAndDispose(ref loadCancellation);
        loadCancellation = new CancellationTokenSource();
        var token = loadCancellation.Token;
        LoadingText.Text = "Takaslar yükleniyor...";
        LoadingOverlay.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        selectedTransfer = null;
        suppressSelectionChanges = true;
        try
        {
            foreach (var grid in TakasGrids()) grid.SelectedItem = null;
        }
        finally
        {
            suppressSelectionChanges = false;
        }
        DetailPanel.Visibility = Visibility.Collapsed;
        TakasDetailGrid.ItemsSource = null;
        try
        {
            var query = new TakasListQuery(
                StartDatePicker.SelectedDate.Value.Date,
                EndDatePicker.SelectedDate.Value.Date.AddDays(1),
                QrFilterBox.Text.Trim(),
                selectedBarcode,
                selectedBrand,
                selectedPharmacyId,
                (UserFilter.SelectedItem as TakasListUserFilterOption)?.UserId ?? string.Empty,
                (DirectionFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
                GetStatuses(),
                ParseMoney(MinimumDebtBox.Text),
                ParseMoney(MaximumDebtBox.Text));
            var transfers = await repository.LoadAsync(query, token);
            TakasGrid.ItemsSource = transfers;
            var incomingTransfers = transfers
                .Where(transfer => string.Equals(transfer.Direction, "incoming", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var outgoingTransfers = transfers
                .Where(transfer => string.Equals(transfer.Direction, "outgoing", StringComparison.OrdinalIgnoreCase))
                .ToList();
            IncomingTakasGrid.ItemsSource = incomingTransfers;
            OutgoingTakasGrid.ItemsSource = outgoingTransfers;
            IncomingCountText.Text = $"Toplam gelen takas: {incomingTransfers.Count:N0}";
            OutgoingCountText.Text = $"Toplam giden takas: {outgoingTransfers.Count:N0}";
            CountText.Text = $"Toplam kayıt: {transfers.Count:N0}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            TakasGrid.ItemsSource = null;
            IncomingTakasGrid.ItemsSource = null;
            OutgoingTakasGrid.ItemsSource = null;
            IncomingCountText.Text = "Toplam gelen takas: 0";
            OutgoingCountText.Text = "Toplam giden takas: 0";
            CountText.Text = $"Takaslar yüklenemedi: {exception.Message}";
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

    private void OnPeriodPresetClicked(object sender, RoutedEventArgs e)
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
        suppressFilterChanges = true;
        StartDatePicker.SelectedDate = start;
        EndDatePicker.SelectedDate = today;
        suppressFilterChanges = false;
        QueueReload();
    }

    private async void OnProductFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressFilterChanges) return;
        selectedBarcode = string.Empty;
        selectedBrand = string.Empty;
        CancellationTokenSourceGuard.CancelAndDispose(ref productSearchCancellation);
        ProductSuggestionPopup.IsOpen = false;
        var term = ProductFilterBox.Text.Trim();
        if (term.Length is >= 8 and <= 14 && term.All(char.IsDigit))
        {
            selectedBarcode = term;
            QueueReload();
        }
        if (term.Length < 3)
        {
            QueueReload();
            return;
        }

        productSearchCancellation = new CancellationTokenSource();
        try
        {
            var suggestions = await repository.SearchProductsAsync(term, productSearchCancellation.Token);
            MedicineSuggestionList.ItemsSource = suggestions.Where(item => !item.IsBrand).ToList();
            BrandSuggestionList.ItemsSource = suggestions.Where(item => item.IsBrand).ToList();
            ProductSuggestionPopup.IsOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CountText.Text = $"Ürün önerileri yüklenemedi: {exception.Message}";
        }
    }

    private void OnProductSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: SaleProductSuggestion product }) return;
        suppressFilterChanges = true;
        selectedBarcode = product.IsBrand ? string.Empty : product.FilterValue;
        selectedBrand = product.IsBrand ? product.FilterValue : string.Empty;
        ProductFilterBox.Text = product.DisplayName;
        ProductFilterBox.CaretIndex = ProductFilterBox.Text.Length;
        ProductSuggestionPopup.IsOpen = false;
        MedicineSuggestionList.SelectedItem = null;
        BrandSuggestionList.SelectedItem = null;
        suppressFilterChanges = false;
        QueueReload();
    }

    private async void OnPharmacyFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressFilterChanges) return;
        selectedPharmacyId = null;
        CancellationTokenSourceGuard.CancelAndDispose(ref pharmacySearchCancellation);
        PharmacySuggestionPopup.IsOpen = false;
        var term = PharmacyFilterBox.Text.Trim();
        if (term.Length < 2)
        {
            QueueReload();
            return;
        }

        pharmacySearchCancellation = new CancellationTokenSource();
        try
        {
            var suggestions = await pharmacyRepository.SearchPharmaciesAsync(term, pharmacySearchCancellation.Token);
            PharmacySuggestionList.ItemsSource = suggestions;
            PharmacySuggestionPopup.IsOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CountText.Text = $"Eczane önerileri yüklenemedi: {exception.Message}";
        }
    }

    private void OnPharmacySuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PharmacySuggestionList.SelectedItem is not TakasPharmacySuggestion pharmacy) return;
        suppressFilterChanges = true;
        selectedPharmacyId = pharmacy.RecordId;
        PharmacyFilterBox.Text = pharmacy.DisplayText;
        PharmacyFilterBox.CaretIndex = PharmacyFilterBox.Text.Length;
        PharmacySuggestionPopup.IsOpen = false;
        PharmacySuggestionList.SelectedItem = null;
        suppressFilterChanges = false;
        QueueReload();
    }

    private async void OnTakasSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectionChanges || sender is not DataGrid sourceGrid) return;
        CancellationTokenSourceGuard.CancelAndDispose(ref detailCancellation);
        if (sourceGrid.SelectedItem is not TakasListItem transfer)
        {
            if (selectedTransfer is not null) return;
            DetailPanel.Visibility = Visibility.Collapsed;
            TakasDetailGrid.ItemsSource = null;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        selectedTransfer = transfer;
        suppressSelectionChanges = true;
        try
        {
            foreach (var grid in TakasGrids())
                if (!ReferenceEquals(grid, sourceGrid)) grid.SelectedItem = null;
        }
        finally
        {
            suppressSelectionChanges = false;
        }

        DetailTitle.Text = $"Takas İçeriği · Takas No: {transfer.TakasId} · {transfer.PharmacyDisplay}";
        DetailPanel.Visibility = Visibility.Visible;
        if (transfer.DetailsLoaded)
        {
            TakasDetailGrid.ItemsSource = transfer.Products;
            return;
        }

        detailCancellation = new CancellationTokenSource();
        var token = detailCancellation.Token;
        LoadingText.Text = "Takasın karekodları yükleniyor...";
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var products = await repository.LoadDetailsAsync(transfer.TakasId, token);
            if (!ReferenceEquals(selectedTransfer, transfer)) return;
            foreach (var product in products) transfer.Products.Add(product);
            transfer.DetailsLoaded = true;
            TakasDetailGrid.ItemsSource = transfer.Products;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CountText.Text = $"Takas ayrıntıları yüklenemedi: {exception.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested) LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTakasGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid) return;
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is not null) row.IsSelected = true;
    }

    private void OnTakasGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is DataGrid owner && RelatedRecordMenus.HasNestedMenu(owner, e.OriginalSource as DependencyObject)) return;
        if (sender is not DataGrid grid)
        {
            e.Handled = true;
            return;
        }
        var hit = grid.InputHitTest(Mouse.GetPosition(grid)) as DependencyObject;
        var row = FindAncestor<DataGridRow>(hit);
        var selected = e.CursorLeft < 0 && e.CursorTop < 0 ? grid.SelectedItem : row?.Item;
        if (selected is not TakasListItem transfer || !grid.Items.Contains(transfer))
        {
            e.Handled = true;
            return;
        }
        grid.SelectedItem = transfer;
        if (grid.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header?.ToString() == "Cancel") is { } menuItem)
        {
            menuItem.Visibility = transfer is { Status: "in_transit", Direction: "outgoing" } ? Visibility.Visible : Visibility.Collapsed;
            menuItem.IsEnabled = !cancellationInProgress;
        }
    }

    private async void OnCancelTakasClicked(object sender, RoutedEventArgs e)
    {
        if (cancellationInProgress ||
            selectedTransfer is not { Status: "in_transit", Direction: "outgoing" } transfer)
            return;

        var notificationAttempted = false;
        try
        {
            SetCancellationOperationState(true, "Mal Devir İptal bilgileri hazırlanıyor...");
            var settings = ApplicationSettingsStore.Current;
            if (settings.ItsUsername.Length == 0 || settings.ItsPassword.Length == 0)
                throw new InvalidOperationException("İTS kullanıcı adı ve parolası Ayarlar > İTS Ayarları bölümünde yapılandırılmalıdır.");
            var configuredGln = settings.ItsPharmacyGln.Trim();
            if (!Gs1IdentifierValidator.IsValidGln13(configuredGln))
                throw new InvalidOperationException("Eczane GLN kodu Ayarlar > İTS Ayarları bölümünde geçerli olarak yapılandırılmalıdır.");
            var currentUser = (Application.Current as App)?.CurrentUser
                ?? throw new InvalidOperationException("Mal Devir İptal için oturum açmış kullanıcı gereklidir.");

            var preparation = await pharmacyRepository.LoadCancellationPreparationAsync(
                transfer.TakasId,
                CancellationToken.None);
            if (!string.Equals(configuredGln, preparation.SourceGln, StringComparison.Ordinal))
                throw new InvalidOperationException("Ayarlardaki eczane GLN kodu, Takası gönderen GLN koduyla eşleşmiyor.");
            var request = ItsTransferCancellationRequestFactory.Create(preparation.Products);
            var credentials = new ItsCredentials(settings.ItsUsername, settings.ItsPassword);

            LoadingOverlay.Visibility = Visibility.Collapsed;
            var confirmation = pharmacyApp.Localization.EnglishMessageBox.Show(
                Window.GetWindow(this),
                "Bu işlem GERÇEK İTS sistemine Mal Devir İptal bildirimi gönderecektir.\n\n" +
                $"Takas No: {transfer.TakasId}\n" +
                $"Eczane: {transfer.PharmacyDisplay}\n" +
                $"Eczane GLN: {preparation.DestinationGln}\n" +
                $"Karekod adedi: {preparation.Products.Count}\n" +
                $"Geri alınacak borç: {transfer.DebtDisplay}\n" +
                $"Adres: {ItsServiceEndpoints.TransferCancellation(ItsEnvironment.Production)}\n\n" +
                "İTS tarafından kabul edilen karekodlar aktif stoka geri alınacak ve eczane borcu azaltılacaktır. Emin misiniz?",
                "DİKKAT: Gerçek İTS Mal Devir İptal Bildirimi",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;

            SetCancellationOperationState(true, "İTS Mal Devir İptal bildirimi gönderiliyor...");
            notificationAttempted = true;
            var result = await apiClient.SendTransferCancellationAsync(
                ItsEnvironment.Production,
                credentials,
                request,
                CancellationToken.None);
            var finalized = await pharmacyRepository.ApplyCancellationResultAsync(
                transfer.TakasId,
                result,
                currentUser.UserId,
                CancellationToken.None);

            await ReloadAsync();
            var notification = string.IsNullOrWhiteSpace(finalized.NotificationId)
                ? string.Empty
                : $"\nBildirim no: {finalized.NotificationId}";
            if (finalized.FailedItemCount == 0)
            {
                pharmacyApp.Localization.EnglishMessageBox.Show(
                    Window.GetWindow(this),
                    $"Mal Devir İptal tamamlandı.\n\n" +
                    $"Aktif stoka alınan karekod: {finalized.CancelledItemCount}\n" +
                    $"Geri alınan borç: {finalized.ReversedDebtAmount.ToString("N2", TurkishCulture)} ₺" + notification,
                    "Mal Devir İptal Tamamlandı",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                pharmacyApp.Localization.EnglishMessageBox.Show(
                    Window.GetWindow(this),
                    finalized.CancelledItemCount == 0
                        ? $"İTS Mal Devir İptal talebindeki {finalized.FailedItemCount} karekodu reddetti. Stok ve borç değiştirilmedi.{notification}"
                        : $"Mal Devir İptal kısmen tamamlandı. {finalized.CancelledItemCount} karekod iptal edildi, {finalized.FailedItemCount} karekod reddedildi.{notification}",
                    "Mal Devir İptal Sonucu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (ItsNotificationNotSentException exception)
        {
            try
            {
                await pharmacyRepository.MarkCancellationNotSentAsync(
                    transfer.TakasId,
                    exception.Message,
                    CancellationToken.None);
            }
            catch (Exception localException)
            {
                pharmacyApp.Localization.EnglishMessageBox.Show(
                    Window.GetWindow(this),
                    $"{exception.Message}\n\nYerel Takas kaydı güncellenemedi: {localException.Message}",
                    "Mal Devir İptal Gönderilemedi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            await ReloadAsync();
            pharmacyApp.Localization.EnglishMessageBox.Show(
                Window.GetWindow(this),
                $"{exception.Message}\n\nStok ve borç değiştirilmedi. Takas Yolda durumunda kaldı; bağlantı düzeldikten sonra yeniden deneyebilirsiniz.",
                "Mal Devir İptal Gönderilemedi",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            if (notificationAttempted)
            {
                try
                {
                    await pharmacyRepository.MarkCancellationReconciliationRequiredAsync(
                        transfer.TakasId,
                        "İTS Mal Devir İptal çağrısı veya yerel sonuç uygulaması kesinleştirilemedi: " + exception.Message,
                        CancellationToken.None);
                    await ReloadAsync();
                }
                catch
                {
                }
            }
            pharmacyApp.Localization.EnglishMessageBox.Show(
                Window.GetWindow(this),
                notificationAttempted
                    ? $"Mal Devir İptal sonucu kesinleştirilemedi: {exception.Message}\n\nTakas #{transfer.TakasId} teknik kontrol gerektiriyor; aynı karekodlarla tekrar iptal bildirimi göndermeyin."
                    : $"Mal Devir İptal başlatılamadı: {exception.Message}",
                "Mal Devir İptal Hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetCancellationOperationState(false);
        }
    }

    private void SetCancellationOperationState(bool running, string? loadingText = null)
    {
        if (cancellationInProgress != running)
        {
            cancellationInProgress = running;
            BackgroundWorkStateChanged?.Invoke(running);
        }
        if (running)
        {
            filterTimer.Stop();
            LoadingText.Text = loadingText ?? "Mal Devir İptal işlemi sürüyor...";
            LoadingOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        foreach (var grid in TakasGrids()) grid.IsEnabled = !running;
        RefreshButton.IsEnabled = !running;
    }

    private void OnSplitDirectionChanged(object sender, RoutedEventArgs e)
    {
        var split = SplitDirectionToggle.IsChecked == true;
        TakasGrid.Visibility = split ? Visibility.Collapsed : Visibility.Visible;
        SplitTakasHost.Visibility = split ? Visibility.Visible : Visibility.Collapsed;

        suppressSelectionChanges = true;
        try
        {
            foreach (var grid in TakasGrids()) grid.SelectedItem = null;
            if (selectedTransfer is not null)
            {
                var targetGrid = split
                    ? string.Equals(selectedTransfer.Direction, "incoming", StringComparison.OrdinalIgnoreCase)
                        ? IncomingTakasGrid
                        : OutgoingTakasGrid
                    : TakasGrid;
                targetGrid.SelectedItem = selectedTransfer;
                targetGrid.ScrollIntoView(selectedTransfer);
            }
        }
        finally
        {
            suppressSelectionChanges = false;
        }
    }

    private DataGrid[] TakasGrids() => [TakasGrid, IncomingTakasGrid, OutgoingTakasGrid];

    private void OnProductExpandClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: TakasListProductGroup product } toggle) return;
        product.IsExpanded = toggle.IsChecked == true;
        if (TakasDetailGrid.ItemContainerGenerator.ContainerFromItem(product) is DataGridRow row)
            row.DetailsVisibility = product.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private TakasListStatuses GetStatuses()
    {
        var result = TakasListStatuses.None;
        foreach (var checkBox in StatusCheckBoxes())
        {
            if (checkBox.IsChecked == true && Enum.TryParse<TakasListStatuses>(checkBox.Tag as string, out var status))
                result |= status;
        }
        return result;
    }

    private void OnStatusFilterClicked(object sender, RoutedEventArgs e) =>
        StatusFilterPopup.IsOpen = !StatusFilterPopup.IsOpen;

    private void OnStatusChanged(object sender, RoutedEventArgs e)
    {
        var selected = GetStatuses();
        StatusFilterButton.Content = selected switch
        {
            TakasListStatuses.All => "Tümü",
            TakasListStatuses.None => "Seçim yok",
            _ => $"{StatusCheckBoxes().Count(checkBox => checkBox.IsChecked == true)} durum seçili"
        };
        QueueReload();
    }

    private void OnSelectAllStatusesClicked(object sender, RoutedEventArgs e) => SetAllStatuses(true);
    private void OnDeselectAllStatusesClicked(object sender, RoutedEventArgs e) => SetAllStatuses(false);

    private void SetAllStatuses(bool selected)
    {
        var wasSuppressed = suppressFilterChanges;
        suppressFilterChanges = true;
        foreach (var checkBox in StatusCheckBoxes()) checkBox.IsChecked = selected;
        suppressFilterChanges = wasSuppressed;
        OnStatusChanged(this, new RoutedEventArgs());
    }

    private CheckBox[] StatusCheckBoxes() =>
    [
        PendingStatusCheckBox, TransitStatusCheckBox, PartTransferredStatusCheckBox,
        PartAcceptedStatusCheckBox, AcceptedStatusCheckBox, PartCancelledStatusCheckBox,
        CancelledStatusCheckBox, FailedStatusCheckBox, ReconciliationStatusCheckBox
    ];

    private void OnOpenQrMovementsClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TakasListQrEntry entry) return;
        var options = new QrMovementsLaunchOptions(
            DateTime.Today,
            DateTime.Today,
            PeriodTitle: string.Empty,
            Search: entry.QrCode,
            IgnoreDateFilter: true);
        TabRequested?.Invoke(new ModuleTabRequest(
            $"products.qr-movements.qr.{Uri.EscapeDataString(entry.QrCode)}",
            "Karekod Hareketleri (Karekoda Özel)",
            () => new QrMovementsView(options),
            1050,
            560));
    }

    private void OnCopyQrClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TakasListQrEntry entry) return;
        try
        {
            Clipboard.SetText(entry.QrCode);
            CountText.Text = "Karekod panoya kopyalandı.";
        }
        catch (Exception exception)
        {
            CountText.Text = $"Karekod kopyalanamadı: {exception.Message}";
        }
    }

    private static decimal? ParseMoney(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (decimal.TryParse(text, NumberStyles.Number, TurkishCulture, out var value)) return value;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value) ? value : null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
}
