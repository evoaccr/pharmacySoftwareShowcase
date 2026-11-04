using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.StockMovements;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Sales;

public partial class RetailSalesListView : UserControl, IModuleTabRequestSource
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly RetailSalesListLaunchOptions launchOptions;
    private readonly RetailSalesListRepository repository = new();
    private readonly RetailSaleRepository patientRepository = new();
    private readonly DispatcherTimer filterTimer;
    private CancellationTokenSource? loadCancellation;
    private CancellationTokenSource? productSearchCancellation;
    private CancellationTokenSource? patientSearchCancellation;
    private CancellationTokenSource? detailCancellation;
    private string selectedBarcode = string.Empty;
    private IReadOnlyList<string> launchBarcodes = [];
    private string selectedBrand = string.Empty;
    private int? selectedPatientId;
    private bool suppressFilterChanges;
    private bool suppressGridSelection;
    private bool initialized;
    private bool initialFocusPending;
    private bool launchFiltersActive = true;
    private IReadOnlyList<RetailSaleListItem> currentSales = [];
    private DataGrid? activeSalesGrid;

    public event Action<ModuleTabRequest>? TabRequested;

    public RetailSalesListView() : this(new RetailSalesListLaunchOptions())
    {
    }

    public RetailSalesListView(RetailSalesListLaunchOptions launchOptions)
    {
        this.launchOptions = launchOptions;
        initialFocusPending = launchOptions.InitialSaleId.HasValue;
        InitializeComponent();
        foreach (var grid in new[] { SalesGrid, EntrySalesGrid, ExitSalesGrid, SaleDetailGrid }) RelatedRecordMenus.Attach(grid);
        activeSalesGrid = SalesGrid;
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
        var initialDate = (launchOptions.InitialDate ?? DateTime.Today).Date;
        StartDatePicker.SelectedDate = (launchOptions.StartDate ?? initialDate).Date;
        EndDatePicker.SelectedDate = (launchOptions.EndDate ?? initialDate).Date;
        launchBarcodes = (launchOptions.Barcodes is { Count: > 0 }
                ? launchOptions.Barcodes
                : [launchOptions.Barcode])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        selectedBarcode = launchBarcodes.Count == 1 ? launchBarcodes[0] : string.Empty;
        ProductFilterBox.Text = launchBarcodes.Count switch
        {
            0 => string.Empty,
            1 => launchBarcodes[0],
            _ => $"{launchBarcodes.Count} barkod seçili"
        };
        ProductFilterBox.ToolTip = launchBarcodes.Count > 1 ? string.Join(Environment.NewLine, launchBarcodes) : null;
        selectedPatientId = launchOptions.PatientId;
        UpdateLaunchFilterDescription();
        MinimumTotalBox.Text = launchOptions.MinimumTotal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MaximumTotalBox.Text = launchOptions.MaximumTotal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        DebtFilter.SelectedIndex = launchOptions.IsDebted switch { true => 1, false => 2, _ => 0 };
        MarkedOnlyCheckBox.IsChecked = launchOptions.MarkedOnly;
        try
        {
            var users = await repository.LoadUsersAsync(CancellationToken.None);
            UserFilter.ItemsSource = new[] { new SaleUserFilterOption(string.Empty, "Hepsi") }.Concat(users);
            UserFilter.SelectedItem = UserFilter.Items.Cast<SaleUserFilterOption>()
                .FirstOrDefault(option => string.Equals(option.UserId, launchOptions.UserId, StringComparison.Ordinal))
                ?? UserFilter.Items.Cast<SaleUserFilterOption>().First();
            SetTransactionTypes(launchOptions.TransactionTypes ?? RetailTransactionTypes.All);
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
        CancellationTokenSourceGuard.CancelAndDispose(ref patientSearchCancellation);
        CancellationTokenSourceGuard.CancelAndDispose(ref detailCancellation);
    }

    private void QueueReload()
    {
        if (suppressFilterChanges || !initialized) return;
        filterTimer.Stop();
        filterTimer.Start();
    }

    private void OnSimpleFilterChanged(object sender, TextChangedEventArgs e) => QueueReload();

    private void UpdateLaunchFilterDescription()
    {
        var filters = new List<string>();
        if (launchFiltersActive)
        {
            if (launchOptions.StartTime is not null || launchOptions.EndTime is not null)
                filters.Add($"Time: {launchOptions.StartTime?.ToString("HH:mm") ?? "00:00"}–{launchOptions.EndTime?.ToString("HH:mm") ?? "23:59"}");
            if (launchOptions.PrescriptionNo.Length > 0) filters.Add($"Prescription: {launchOptions.PrescriptionNo}");
            if (launchOptions.QrCodes is { Count: > 0 }) filters.Add($"{launchOptions.QrCodes.Count} QR codes");
            if (launchOptions.Statuses is { Count: > 0 }) filters.Add("Status: " + string.Join(", ", launchOptions.Statuses.Select(value => value switch
            {
                "pending" => "Pending", "completed" => "Completed", "medula_reported" => "Reported to Medula",
                "reconciliation_required" => "Review required", "cancelled" => "Cancelled", _ => value
            })));
            if (launchOptions.PaymentTypes is { Count: > 0 }) filters.Add("Payment: " + string.Join(", ", launchOptions.PaymentTypes.Select(value => value switch
            {
                "cash" => "Cash", "card" => "Card", "bank_transfer" => "Bank transfer", "debt" => "On account",
                "discount" => "Discounted", "institution" => "Institution", _ => value
            })));
            else if (launchOptions.DiscountedOnly) filters.Add("Discounted sales");
        }
        LaunchFilterText.Text = filters.Count > 0
            ? "Additional filters from chat: " + string.Join(" · ", filters) + ". Use Clear Filters to remove them."
            : string.Empty;
        LaunchFilterText.Visibility = filters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnDateChanged(object sender, SelectionChangedEventArgs e) => QueueReload();
    private void OnComboFilterChanged(object sender, SelectionChangedEventArgs e) => QueueReload();
    private void OnMarkedFilterChanged(object sender, RoutedEventArgs e) => QueueReload();
    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        filterTimer.Stop();
        CancellationTokenSourceGuard.CancelAndDispose(ref productSearchCancellation);
        CancellationTokenSourceGuard.CancelAndDispose(ref patientSearchCancellation);
        suppressFilterChanges = true;
        try
        {
            launchFiltersActive = false;
            UpdateLaunchFilterDescription();
            selectedBarcode = string.Empty;
            launchBarcodes = [];
            selectedBrand = string.Empty;
            selectedPatientId = null;
            QrFilterBox.Clear();
            ProductFilterBox.Clear();
            PatientFilterBox.Clear();
            MinimumTotalBox.Clear();
            MaximumTotalBox.Clear();
            StartDatePicker.SelectedDate = DateTime.Today;
            EndDatePicker.SelectedDate = DateTime.Today;
            UserFilter.SelectedIndex = UserFilter.Items.Count > 0 ? 0 : -1;
            SetAllTransactionTypes(true);
            DebtFilter.SelectedIndex = 0;
            MarkedOnlyCheckBox.IsChecked = false;
            ProductSuggestionPopup.IsOpen = false;
            PatientSuggestionPopup.IsOpen = false;
            SaleTypeFilterPopup.IsOpen = false;
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
        LoadingText.Text = selectedBarcode.Length > 0 || launchBarcodes.Count > 0
            ? "Barkodlar çözümleniyor ve satışlar aranıyor..."
            : "Satışlar yükleniyor...";
        LoadingOverlay.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        DetailPanel.Visibility = Visibility.Collapsed;
        SaleDetailGrid.ItemsSource = null;
        try
        {
            var query = new RetailSalesListQuery(
                StartDatePicker.SelectedDate.Value.Date,
                EndDatePicker.SelectedDate.Value.Date.AddDays(1),
                QrFilterBox.Text.Trim(),
                selectedBarcode,
                selectedBrand,
                (UserFilter.SelectedItem as SaleUserFilterOption)?.UserId ?? string.Empty,
                GetTransactionTypes(),
                GetDebtFilter(),
                selectedPatientId,
                ParseMoney(MinimumTotalBox.Text),
                ParseMoney(MaximumTotalBox.Text),
                MarkedOnlyCheckBox.IsChecked == true,
                launchBarcodes,
                launchFiltersActive && launchOptions.DiscountedOnly,
                launchFiltersActive ? launchOptions.StartTime : null,
                launchFiltersActive ? launchOptions.EndTime : null,
                launchFiltersActive ? launchOptions.Statuses : null,
                launchFiltersActive ? launchOptions.PrescriptionNo : string.Empty,
                launchFiltersActive ? launchOptions.QrCodes : null,
                launchFiltersActive ? launchOptions.PaymentTypes : null);
            var sales = await repository.LoadSalesAsync(query, token);
            currentSales = sales;
            ApplySalesItems();
            if (initialFocusPending && launchOptions.InitialSaleId is int initialSaleId)
            {
                initialFocusPending = false;
                var focusedSale = sales.FirstOrDefault(sale =>
                    sale.ReferenceCode == $"P{initialSaleId}");
                if (focusedSale is not null)
                {
                    SalesGrid.SelectedItem = focusedSale;
                    SalesGrid.ScrollIntoView(focusedSale);
                    SalesGrid.Focus();
                }
                else
                {
                    CountText.Text = $"Satış No {initialSaleId} bulunamadı.";
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            currentSales = [];
            ApplySalesItems();
            CountText.Text = $"Satışlar yüklenemedi: {exception.Message}";
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

    private IEnumerable<DataGrid> SalesGrids
    {
        get
        {
            yield return SalesGrid;
            yield return EntrySalesGrid;
            yield return ExitSalesGrid;
        }
    }

    private RetailSaleListItem? GetSelectedSale() =>
        activeSalesGrid?.SelectedItem as RetailSaleListItem
        ?? SalesGrids.Select(grid => grid.SelectedItem).OfType<RetailSaleListItem>().FirstOrDefault();

    private void ApplySalesItems()
    {
        var entries = currentSales.Where(sale => sale.IsEntryDirection).ToArray();
        var exits = currentSales.Where(sale => !sale.IsEntryDirection).ToArray();
        SalesGrid.ItemsSource = currentSales;
        EntrySalesGrid.ItemsSource = entries;
        ExitSalesGrid.ItemsSource = exits;
        CountText.Text = $"Toplam kayıt: {currentSales.Count:N0}";
        EntryCountText.Text = $"Toplam kayıt: {entries.Length:N0}";
        ExitCountText.Text = $"Toplam kayıt: {exits.Length:N0}";
    }

    private void OnSplitDirectionChanged(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedSale();
        var split = SplitDirectionToggle.IsChecked == true;
        SalesGrid.Visibility = split ? Visibility.Collapsed : Visibility.Visible;
        SplitGridHost.Visibility = split ? Visibility.Visible : Visibility.Collapsed;

        if (selected is null)
        {
            activeSalesGrid = split ? ExitSalesGrid : SalesGrid;
            return;
        }

        var target = split
            ? selected.IsEntryDirection ? EntrySalesGrid : ExitSalesGrid
            : SalesGrid;
        suppressGridSelection = true;
        foreach (var grid in SalesGrids) grid.SelectedItem = null;
        target.SelectedItem = selected;
        suppressGridSelection = false;
        activeSalesGrid = target;
        target.ScrollIntoView(selected);
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
        launchBarcodes = [];
        ProductFilterBox.ToolTip = null;
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

    private async void OnPatientFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressFilterChanges) return;
        selectedPatientId = null;
        CancellationTokenSourceGuard.CancelAndDispose(ref patientSearchCancellation);
        PatientSuggestionPopup.IsOpen = false;
        var term = PatientFilterBox.Text.Trim();
        if (term.Length < 3)
        {
            QueueReload();
            return;
        }
        patientSearchCancellation = new CancellationTokenSource();
        try
        {
            var suggestions = await patientRepository.SearchPatientsAsync(term, patientSearchCancellation.Token);
            PatientSuggestionList.ItemsSource = suggestions;
            PatientSuggestionPopup.IsOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CountText.Text = $"Hasta önerileri yüklenemedi: {exception.Message}";
        }
    }

    private void OnPatientSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PatientSuggestionList.SelectedItem is not PatientSuggestion patient) return;
        suppressFilterChanges = true;
        selectedPatientId = patient.RecordId;
        PatientFilterBox.Text = patient.DisplayText;
        PatientFilterBox.CaretIndex = PatientFilterBox.Text.Length;
        PatientSuggestionPopup.IsOpen = false;
        PatientSuggestionList.SelectedItem = null;
        suppressFilterChanges = false;
        QueueReload();
    }

    private async void OnSaleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressGridSelection || sender is not DataGrid sourceGrid) return;
        if (sourceGrid.SelectedItem is RetailSaleListItem)
        {
            activeSalesGrid = sourceGrid;
            suppressGridSelection = true;
            foreach (var grid in SalesGrids.Where(grid => !ReferenceEquals(grid, sourceGrid)))
                grid.SelectedItem = null;
            suppressGridSelection = false;
        }
        CancellationTokenSourceGuard.CancelAndDispose(ref detailCancellation);
        if (GetSelectedSale() is not RetailSaleListItem sale)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            SaleDetailGrid.ItemsSource = null;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        DetailTitle.Text = $"{sale.TransactionTypeDisplay} İçeriği · Kayıt No: {sale.SaleId}";
        DetailPanel.Visibility = Visibility.Visible;
        if (sale.DetailsLoaded)
        {
            SaleDetailGrid.ItemsSource = sale.Products;
            return;
        }
        detailCancellation = new CancellationTokenSource();
        var token = detailCancellation.Token;
        LoadingText.Text = "Satışın karekodları yükleniyor...";
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var products = await repository.LoadSaleDetailsAsync(sale, token);
            if (!ReferenceEquals(GetSelectedSale(), sale)) return;
            foreach (var product in products) sale.Products.Add(product);
            sale.DetailsLoaded = true;
            SaleDetailGrid.ItemsSource = sale.Products;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CountText.Text = $"Satış ayrıntıları yüklenemedi: {exception.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested) LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProductExpandClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: RetailSaleProductGroup product } toggle) return;
        product.IsExpanded = toggle.IsChecked == true;
        if (SaleDetailGrid.ItemContainerGenerator.ContainerFromItem(product) is DataGridRow row)
            row.DetailsVisibility = product.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private RetailTransactionTypes GetTransactionTypes()
    {
        var result = RetailTransactionTypes.None;
        if (RetailSaleTypeCheckBox.IsChecked == true) result |= RetailTransactionTypes.RetailSale;
        if (PrescriptionSaleTypeCheckBox.IsChecked == true) result |= RetailTransactionTypes.PrescriptionSale;
        if (RetailRefundTypeCheckBox.IsChecked == true) result |= RetailTransactionTypes.RetailRefund;
        if (PrescriptionRefundTypeCheckBox.IsChecked == true) result |= RetailTransactionTypes.PrescriptionRefund;
        if (ConsignmentSaleTypeCheckBox.IsChecked == true) result |= RetailTransactionTypes.ConsignmentSale;
        if (ConsignmentCancellationTypeCheckBox.IsChecked == true) result |= RetailTransactionTypes.ConsignmentCancellation;
        return result;
    }

    private void OnSaleTypeFilterClicked(object sender, RoutedEventArgs e) =>
        SaleTypeFilterPopup.IsOpen = !SaleTypeFilterPopup.IsOpen;

    private void OnSaleTypeChanged(object sender, RoutedEventArgs e)
    {
        var selected = GetTransactionTypes();
        SaleTypeFilterButton.Content = selected switch
        {
            RetailTransactionTypes.All => "Tümü",
            RetailTransactionTypes.None => "Seçim yok",
            _ => $"{CountSelectedTypes(selected)} tür seçili"
        };
        QueueReload();
    }

    private void OnSelectAllSaleTypesClicked(object sender, RoutedEventArgs e) => SetAllTransactionTypes(true);
    private void OnDeselectAllSaleTypesClicked(object sender, RoutedEventArgs e) => SetAllTransactionTypes(false);

    private void SetAllTransactionTypes(bool selected)
        => SetTransactionTypes(selected ? RetailTransactionTypes.All : RetailTransactionTypes.None);

    private void SetTransactionTypes(RetailTransactionTypes selected)
    {
        var wasSuppressed = suppressFilterChanges;
        suppressFilterChanges = true;
        RetailSaleTypeCheckBox.IsChecked = selected.HasFlag(RetailTransactionTypes.RetailSale);
        PrescriptionSaleTypeCheckBox.IsChecked = selected.HasFlag(RetailTransactionTypes.PrescriptionSale);
        RetailRefundTypeCheckBox.IsChecked = selected.HasFlag(RetailTransactionTypes.RetailRefund);
        PrescriptionRefundTypeCheckBox.IsChecked = selected.HasFlag(RetailTransactionTypes.PrescriptionRefund);
        ConsignmentSaleTypeCheckBox.IsChecked = selected.HasFlag(RetailTransactionTypes.ConsignmentSale);
        ConsignmentCancellationTypeCheckBox.IsChecked = selected.HasFlag(RetailTransactionTypes.ConsignmentCancellation);
        suppressFilterChanges = wasSuppressed;
        OnSaleTypeChanged(this, new RoutedEventArgs());
    }

    private static int CountSelectedTypes(RetailTransactionTypes selected) =>
        new[]
        {
            RetailTransactionTypes.RetailSale,
            RetailTransactionTypes.PrescriptionSale,
            RetailTransactionTypes.RetailRefund,
            RetailTransactionTypes.PrescriptionRefund,
            RetailTransactionTypes.ConsignmentSale,
            RetailTransactionTypes.ConsignmentCancellation
        }.Count(type => selected.HasFlag(type));

    private bool? GetDebtFilter()
    {
        if (DebtFilter.SelectedItem is not ComboBoxItem { Tag: string value }) return null;
        return value switch { "Debted" => true, "NotDebted" => false, _ => null };
    }

    private void OnOpenQrMovementsClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailSaleQrEntry entry) return;
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
        if ((sender as FrameworkElement)?.DataContext is not RetailSaleQrEntry entry) return;
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

    private void OnPatientNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not RetailSaleListItem sale) return;
        OpenPatientPopup(sale);
    }

    private void OnSalesGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid)) return;
        activeSalesGrid = grid;
        row.IsSelected = true;
    }

    private void OnSalesGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is DataGrid owner && RelatedRecordMenus.HasNestedMenu(owner, e.OriginalSource as DependencyObject)) return;
        if (sender is not DataGrid grid || grid.ContextMenu is not ContextMenu menu)
        {
            e.Handled = true;
            return;
        }
        var hit = grid.InputHitTest(Mouse.GetPosition(grid)) as DependencyObject;
        var row = FindAncestor<DataGridRow>(hit);
        var selected = e.CursorLeft < 0 && e.CursorTop < 0 ? grid.SelectedItem : row?.Item;
        if (selected is not RetailSaleListItem sale || !grid.Items.Contains(sale))
        {
            e.Handled = true;
            return;
        }

        activeSalesGrid = grid;
        grid.SelectedItem = sale;
        FindContextItem<MenuItem>(menu, "Patient").IsEnabled = sale.PatientId.HasValue;
        FindContextItem<MenuItem>(menu, "Mark").Header = sale.IsMarked
            ? "İşareti Kaldır"
            : sale.IsEntryDirection ? "Giriş İşlemini İşaretle" : "Satışı İşaretle";
        FindContextItem<MenuItem>(menu, "Refund").Visibility = sale.TransactionType == RetailTransactionTypes.RetailSale
            ? Visibility.Visible
            : Visibility.Collapsed;
        var showConsignmentActions = sale.CanResolveConsignment ? Visibility.Visible : Visibility.Collapsed;
        FindContextItem<Separator>(menu, "ConsignmentSeparator").Visibility = showConsignmentActions;
        FindContextItem<MenuItem>(menu, "ReturnConsignment").Visibility = showConsignmentActions;
        FindContextItem<MenuItem>(menu, "SettleConsignment").Visibility = showConsignmentActions;
    }

    private void OnGoToPatientClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSale() is RetailSaleListItem sale) OpenPatientPopup(sale);
    }

    private async void OnToggleMarkClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSale() is not RetailSaleListItem sale) return;
        var newValue = !sale.IsMarked;
        try
        {
            await repository.SetMarkedAsync(sale.SaleId, sale.TransactionType, newValue, CancellationToken.None);
            sale.IsMarked = newValue;
        }
        catch (Exception exception)
        {
            CountText.Text = $"İşaretleme kaydedilemedi: {exception.Message}";
        }
    }

    private void OnRefundSaleClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSale() is not RetailSaleListItem { TransactionType: RetailTransactionTypes.RetailSale } sale) return;
        TabRequested?.Invoke(new ModuleTabRequest(
            $"sales.retail-refund.sale.{sale.SaleId}",
            $"Perakende Satış İade · P{sale.SaleId}",
            () =>
            {
                var view = new RetailRefundView(new RetailRefundLaunchOptions(sale.SaleId));
                view.RefundCompleted += completedId =>
                {
                    _ = completedId;
                    _ = ReloadAsync();
                };
                return view;
            },
            900,
            560));
    }

    private async void OnReturnConsignmentClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSale() is not RetailSaleListItem { CanResolveConsignment: true } sale) return;
        var answer = await ModernDialog.ConfirmAsync(
            this,
            "Emaneti Geri Al",
            $"E{sale.SaleId} numaralı emanet geri alınacak.\n" +
            $"{sale.DebtDisplay} borç silinecek ve {sale.CashAmount + sale.CreditAmount + sale.TransferAmount:N2} ₺ iade kaydı oluşturulacak.\n\nDevam edilsin mi?");
        if (!answer) return;
        try
        {
            var userId = (Application.Current as App)?.CurrentUser?.UserId ?? "admin";
            var result = await new ConsignmentSaleRepository().ReturnAsync(sale.SaleId, userId, CancellationToken.None);
            await ModernDialog.ShowInfoAsync(
                this,
                "Emanet Satış",
                $"Emanet geri alındı.\nSilinen borç: {result.ReversedDebtAmount:N2} ₺\nİade edilecek tutar: {result.RefundedAmount:N2} ₺");
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            CountText.Text = $"Emanet geri alınamadı: {exception.Message}";
        }
    }

    private void OnSettleConsignmentClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSale() is not RetailSaleListItem { CanResolveConsignment: true } sale) return;
        TabRequested?.Invoke(new ModuleTabRequest(
            $"sales.retail.consignment-settlement.{sale.SaleId}",
            $"Emanet Perakende Satış · E{sale.SaleId}",
            () =>
            {
                var view = new RetailSaleView(RetailSaleLaunchOptions.SettleConsignment(sale.SaleId));
                view.SaleCompleted += completedId =>
                {
                    _ = completedId;
                    _ = ReloadAsync();
                };
                return view;
            },
            900,
            560));
    }

    private void OpenPatientPopup(RetailSaleListItem sale)
    {
        if (sale.PatientId is not int patientId) return;
        var editor = new CardEditorView(
            CardDefinitions.Patients,
            patientId.ToString(CultureInfo.InvariantCulture),
            sale.PatientName,
            () => { },
            startEditing: false);
        var window = CreatePatientWindow(editor, sale.PatientDisplay);
        editor.ModuleTitleChanged += title => window.Title = title;
        editor.CloseRequested += window.Close;
        window.Show();
    }

    private Window CreatePatientWindow(CardEditorView editor, string title) => new()
    {
        Title = title,
        Content = editor,
        Width = 1280,
        Height = 880,
        MinWidth = 840,
        MinHeight = 640,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Owner = Window.GetWindow(this),
        ShowInTaskbar = false,
        Background = new SolidColorBrush(Color.FromRgb(243, 245, 247))
    };

    private Window CreatePopupWindow(
        string title,
        FrameworkElement content,
        double width,
        double height,
        double minWidth,
        double minHeight) => new()
    {
        Title = title,
        Content = content,
        Owner = Window.GetWindow(this),
        Width = width,
        Height = height,
        MinWidth = minWidth,
        MinHeight = minHeight,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = new SolidColorBrush(Color.FromRgb(243, 245, 247))
    };

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

    private static T FindContextItem<T>(ContextMenu menu, string tag) where T : FrameworkElement =>
        menu.Items.OfType<T>().First(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
}
