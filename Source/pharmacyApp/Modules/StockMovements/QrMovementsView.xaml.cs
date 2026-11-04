using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Medula;

namespace pharmacyApp.Modules.StockMovements;

public partial class QrMovementsView : UserControl, IModuleTitleSource
{
    private const int PageSize = 300;
    private readonly QrMovementsLaunchOptions launchOptions;
    private readonly QrMovementRepository repository = new();
    private readonly ProductCatalogRepository productRepository = new();
    private IReadOnlyList<QrMovementTypeOption> movementTypeOptions = [];
    private readonly DispatcherTimer searchDebounce;
    private readonly DispatcherTimer suggestionDebounce;
    private readonly DispatcherTimer addressDebounce;
    private CancellationTokenSource? loadCancellation;
    private CancellationTokenSource? suggestionCancellation;
    private CancellationTokenSource? addressCancellation;
    private readonly ObservableCollection<QrMovementItem> currentMovements = [];
    private readonly ObservableCollection<QrMovementItem> entryMovements = [];
    private readonly ObservableCollection<QrMovementItem> exitMovements = [];
    private QrMovementSummary currentSummary = QrMovementSummary.Empty;
    private QrMovementQuery? activeQuery;
    private QrSearchSuggestion? selectedSuggestion;
    private QrAddressFilterOption? selectedAddress;
    private string? resolvedBarcode;
    private IReadOnlyList<string> launchBarcodes = [];
    private DateTime? customRangeStart;
    private DateTime? customRangeEndExclusive;
    private string? activePeriodTitle = "Günlük";
    private bool initialized;
    private bool suppressFilterChanges;
    private bool applyingSuggestion;
    private bool navigatingSuggestions;
    private bool pageLoadInProgress;
    private bool hasMoreMovements;
    private int loadVersion;

    public QrMovementsView() : this(QrMovementsLaunchOptions.Daily)
    {
    }

    public QrMovementsView(QrMovementsLaunchOptions launchOptions)
    {
        this.launchOptions = launchOptions;
        InitializeComponent();
        MovementGrid.ItemsSource = currentMovements;
        EntryGrid.ItemsSource = entryMovements;
        ExitGrid.ItemsSource = exitMovements;
        foreach (var grid in new[] { MovementGrid, EntryGrid, ExitGrid })
        {
            grid.ContextMenu = new ContextMenu();
            grid.PreviewMouseRightButtonDown += OnMovementRightClick;
            grid.ContextMenuOpening += OnMovementContextMenuOpening;
        }
        searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        searchDebounce.Tick += OnSearchDebounceTick;
        suggestionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(225) };
        suggestionDebounce.Tick += OnSuggestionDebounceTick;
        addressDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        addressDebounce.Tick += OnAddressDebounceTick;
    }

    public string CurrentModuleTitle => BuildTitle();
    public event Action<string>? ModuleTitleChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        suppressFilterChanges = true;
        var today = DateTime.Today;
        StartDatePicker.SelectedDate = launchOptions.StartDate.Date > today ? today : launchOptions.StartDate.Date;
        EndDatePicker.SelectedDate = launchOptions.EndDate.Date > today ? today : launchOptions.EndDate.Date;
        activePeriodTitle = launchOptions.IgnoreDateFilter ? null : launchOptions.PeriodTitle;
        try
        {
            movementTypeOptions = await repository.LoadMovementTypesAsync(CancellationToken.None);
            var users = await repository.LoadUsersAsync(CancellationToken.None);
            UserFilter.ItemsSource = new[] { new QrUserFilterOption("", "Tüm Kullanıcılar") }
                .Concat(users)
                .ToList();
            UserFilter.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            suppressFilterChanges = false;
            initialized = true;
            SetStatus($"Filtre seçenekleri yüklenemedi: {ex.Message}", true);
            return;
        }
        if (launchOptions.MovementTypes is not null)
        {
            var selectedTypes = launchOptions.MovementTypes.ToHashSet(StringComparer.Ordinal);
            foreach (var option in movementTypeOptions) option.IsSelected = selectedTypes.Contains(option.Code);
        }
        MovementTypeList.ItemsSource = movementTypeOptions;
        if (!string.IsNullOrWhiteSpace(launchOptions.UserId))
        {
            var user = UserFilter.Items.Cast<QrUserFilterOption>()
                .FirstOrDefault(option => string.Equals(option.UserId, launchOptions.UserId, StringComparison.Ordinal));
            if (user is not null) UserFilter.SelectedItem = user;
        }
        if (!string.IsNullOrWhiteSpace(launchOptions.MovementAddress))
        {
            var addressItem = AddressTypeFilter.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Content as string, launchOptions.AddressType, StringComparison.Ordinal));
            if (addressItem?.Tag is string { Length: > 0 })
            {
                AddressTypeFilter.SelectedItem = addressItem;
                selectedAddress = new QrAddressFilterOption(launchOptions.MovementAddress, launchOptions.MovementAddress);
                AddressLookupBox.Text = launchOptions.MovementAddress;
                AddressLookupBox.IsEnabled = true;
                AddressLookupBox.Background = Brushes.White;
                AddressLookupBox.ToolTip = "Seçili hareket adresine göre filtreleniyor.";
            }
        }
        MedicineSuggestionList.ItemsSource = Array.Empty<QrSearchSuggestion>();
        BrandSuggestionList.ItemsSource = Array.Empty<QrSearchSuggestion>();
        launchBarcodes = (launchOptions.Barcodes ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (launchBarcodes.Count > 0)
        {
            SearchBox.Text = string.Join(", ", launchBarcodes);
            SearchBox.ToolTip = launchBarcodes.Count == 1
                ? $"Barkod filtresi: {launchBarcodes[0]}"
                : $"{launchBarcodes.Count} barkod filtresi uygulanıyor.";
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
        else if (!string.IsNullOrWhiteSpace(launchOptions.Search))
        {
            SearchBox.Text = launchOptions.Search;
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
        suppressFilterChanges = false;
        initialized = true;
        UpdateDateConstraints();
        UpdateMovementTypeButton();
        UpdateTitle();
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        searchDebounce.Stop();
        suggestionDebounce.Stop();
        addressDebounce.Stop();
        loadCancellation?.Cancel();
        suggestionCancellation?.Cancel();
        addressCancellation?.Cancel();
    }

    private async Task ReloadAsync()
    {
        if (!initialized) return;
        searchDebounce.Stop();
        var version = ++loadVersion;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;
        activeQuery = BuildQuery();
        pageLoadInProgress = true;
        hasMoreMovements = false;
        ResetMovementItems([]);
        currentSummary = QrMovementSummary.Empty;
        ApplyMovementCounts();

        SetStatus("Hareketler yükleniyor...", false);
        RefreshButton.IsEnabled = false;
        try
        {
            var page = await repository.LoadPageAsync(
                activeQuery, null, null, PageSize, includeSummary: true, cancellationToken);
            if (version != loadVersion) return;
            currentSummary = page.Summary ?? QrMovementSummary.Empty;
            ResetMovementItems(page.Items);
            hasMoreMovements = currentMovements.Count < currentSummary.TotalCount && page.Items.Count > 0;
            ApplyMovementCounts();
            if (currentSummary.TotalCount == 0)
                SetStatus("Seçilen filtrelerle eşleşen karekod hareketi bulunamadı.", false);
            else
                StatusOverlay.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;
            ResetMovementItems([]);
            currentSummary = QrMovementSummary.Empty;
            ApplyMovementCounts();
            SetStatus($"Karekod hareketleri yüklenemedi: {ex.Message}", true);
        }
        finally
        {
            if (version == loadVersion)
            {
                pageLoadInProgress = false;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private async Task LoadMoreAsync()
    {
        if (!initialized || pageLoadInProgress || !hasMoreMovements || activeQuery is null || currentMovements.Count == 0)
            return;

        var version = loadVersion;
        var cancellationToken = loadCancellation?.Token ?? CancellationToken.None;
        var cursor = currentMovements[^1];
        pageLoadInProgress = true;
        try
        {
            var page = await repository.LoadPageAsync(
                activeQuery,
                cursor.MovementDate,
                cursor.RecordId,
                PageSize,
                includeSummary: false,
                cancellationToken);
            if (version != loadVersion) return;
            AppendMovementItems(page.Items);
            hasMoreMovements = page.Items.Count > 0 && currentMovements.Count < currentSummary.TotalCount;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == loadVersion)
                SetStatus($"Ek karekod hareketleri yüklenemedi: {ex.Message}", true);
        }
        finally
        {
            if (version == loadVersion) pageLoadInProgress = false;
        }
    }

    private QrMovementQuery BuildQuery()
    {
        var startDateTime = customRangeStart ?? StartDatePicker.SelectedDate?.Date ?? DateTime.Today;
        var endDateTimeExclusive = customRangeEndExclusive ??
                                   (EndDatePicker.SelectedDate?.Date ?? startDateTime.Date).AddDays(1);
        var (search, searchMode) = launchBarcodes.Count > 0
            ? (string.Empty, QrMovementSearchMode.None)
            : GetEffectiveSearch();
        var userId = (UserFilter.SelectedItem as QrUserFilterOption)?.UserId ?? string.Empty;
        var addressType = ((AddressTypeFilter.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "Patient" => "P",
            "Pharmacy" => "E",
            "Depot" => "D",
            _ => string.Empty
        };
        return new QrMovementQuery(
            startDateTime,
            endDateTimeExclusive,
            movementTypeOptions.Where(option => option.IsSelected).Select(option => option.Id).ToArray(),
            search,
            searchMode,
            userId,
            selectedAddress?.Value ?? string.Empty,
            addressType,
            launchOptions.IgnoreDateFilter,
            launchBarcodes);
    }

    private (string Search, QrMovementSearchMode Mode) GetEffectiveSearch()
    {
        if (selectedSuggestion is not null)
            return (selectedSuggestion.FilterValue, selectedSuggestion.SearchMode);
        if (!string.IsNullOrWhiteSpace(resolvedBarcode))
            return (resolvedBarcode, QrMovementSearchMode.Barcode);
        var search = SearchBox.Text.Trim();
        return (search, DetectSearchMode(search));
    }

    private void ResetMovementItems(IEnumerable<QrMovementItem> items)
    {
        currentMovements.Clear();
        entryMovements.Clear();
        exitMovements.Clear();
        AppendMovementItems(items);
    }

    private void AppendMovementItems(IEnumerable<QrMovementItem> items)
    {
        foreach (var item in items)
        {
            currentMovements.Add(item);
            (item.IsEntryDirection ? entryMovements : exitMovements).Add(item);
        }
    }

    private void ApplyMovementCounts()
    {
        TotalCountText.Text = $"Toplam Hareketler: {currentSummary.TotalCount:N0}";
        DistinctQrCountText.Text = $"Farklı Karekodlar: {currentSummary.DistinctQrCount:N0}";
        MedicineTypeCountText.Text = $"İlaç Tür Sayısı: {currentSummary.DistinctMedicineCount:N0}";
        MedicineTypeCountText.Visibility = ShouldShowIncompleteMedicineCount()
            ? Visibility.Visible
            : Visibility.Collapsed;
        EntryCountText.Text = $"Toplam Hareketler: {currentSummary.EntryCount:N0}";
        ExitCountText.Text = $"Toplam Hareketler: {currentSummary.ExitCount:N0}";
    }

    private bool ShouldShowIncompleteMedicineCount()
    {
        if (selectedSuggestion is not null || !string.IsNullOrWhiteSpace(resolvedBarcode)) return false;
        var search = SearchBox.Text.Trim();
        return !string.IsNullOrWhiteSpace(search) && DetectSearchMode(search) == QrMovementSearchMode.MedicineNameContains;
    }

    private static QrMovementSearchMode DetectSearchMode(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return QrMovementSearchMode.None;
        if (LooksLikeQrCode(search)) return QrMovementSearchMode.QrCode;
        if (search.Length is >= 8 and <= 14 && search.All(char.IsDigit)) return QrMovementSearchMode.Barcode;
        return search.All(char.IsDigit) ? QrMovementSearchMode.Barcode : QrMovementSearchMode.MedicineNameContains;
    }

    private static bool LooksLikeQrCode(string value) =>
        value.StartsWith("01", StringComparison.Ordinal) ||
        value.Contains("(01)", StringComparison.Ordinal) ||
        value.StartsWith("]d2", StringComparison.OrdinalIgnoreCase) ||
        value.Contains((char)29);

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || suppressFilterChanges) return;
        if (launchBarcodes.Count > 0) launchBarcodes = [];
        selectedSuggestion = null;
        resolvedBarcode = null;
        SearchBox.ToolTip = "Karekod ve barkod tam eşleşir; ürün adı ve marka önerilerinden seçim yapılabilir.";
        MedicineSuggestionList.SelectedItem = null;
        BrandSuggestionList.SelectedItem = null;
        SearchSuggestionsPopup.IsOpen = false;
        UpdateTitle();

        var search = SearchBox.Text.Trim();
        var mode = DetectSearchMode(search);
        suggestionDebounce.Stop();
        if (mode == QrMovementSearchMode.MedicineNameContains && search.Length >= 2)
            suggestionDebounce.Start();

        searchDebounce.Stop();
        searchDebounce.Interval = mode == QrMovementSearchMode.Barcode
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromMilliseconds(350);
        searchDebounce.Start();
    }

    private async void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        searchDebounce.Stop();
        if (DetectSearchMode(SearchBox.Text.Trim()) == QrMovementSearchMode.Barcode)
            await ResolveBarcodeAndReloadAsync();
        else
            await ReloadAsync();
    }

    private async void OnSuggestionDebounceTick(object? sender, EventArgs e)
    {
        suggestionDebounce.Stop();
        var search = SearchBox.Text.Trim();
        if (DetectSearchMode(search) != QrMovementSearchMode.MedicineNameContains || search.Length < 2) return;

        suggestionCancellation?.Cancel();
        suggestionCancellation?.Dispose();
        suggestionCancellation = new CancellationTokenSource();
        try
        {
            var suggestions = await repository.LoadSuggestionsAsync(search, 8, suggestionCancellation.Token);
            if (!string.Equals(search, SearchBox.Text.Trim(), StringComparison.Ordinal)) return;
            MedicineSuggestionList.ItemsSource = suggestions.Where(item => !item.IsBrand).ToList();
            BrandSuggestionList.ItemsSource = suggestions.Where(item => item.IsBrand).ToList();
            SearchSuggestionsPopup.IsOpen = suggestions.Count > 0 && SearchBox.IsKeyboardFocusWithin;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SearchSuggestionsPopup.IsOpen = false;
        }
    }

    private async void OnSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (applyingSuggestion || navigatingSuggestions || sender is not ListBox { SelectedItem: QrSearchSuggestion suggestion }) return;
        await ApplySearchSuggestionAsync(suggestion);
    }

    private async Task ApplySearchSuggestionAsync(QrSearchSuggestion suggestion)
    {
        applyingSuggestion = true;
        selectedSuggestion = suggestion;
        resolvedBarcode = null;
        suppressFilterChanges = true;
        SearchBox.Text = suggestion.DisplayText;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        MedicineSuggestionList.SelectedItem = null;
        BrandSuggestionList.SelectedItem = null;
        suppressFilterChanges = false;
        SearchSuggestionsPopup.IsOpen = false;
        applyingSuggestion = false;
        SearchBox.Focus();
        UpdateTitle();
        await ReloadAsync();
    }

    private void MoveSearchSuggestion(int direction)
    {
        var medicineCount = MedicineSuggestionList.Items.Count;
        var brandCount = BrandSuggestionList.Items.Count;
        var total = medicineCount + brandCount;
        if (total == 0) return;

        var current = MedicineSuggestionList.SelectedIndex >= 0
            ? MedicineSuggestionList.SelectedIndex
            : BrandSuggestionList.SelectedIndex >= 0
                ? medicineCount + BrandSuggestionList.SelectedIndex
                : -1;
        var next = current < 0
            ? (direction > 0 ? 0 : total - 1)
            : Math.Clamp(current + direction, 0, total - 1);

        navigatingSuggestions = true;
        MedicineSuggestionList.SelectedItem = null;
        BrandSuggestionList.SelectedItem = null;
        var target = next < medicineCount ? MedicineSuggestionList : BrandSuggestionList;
        target.SelectedIndex = next < medicineCount ? next : next - medicineCount;
        target.ScrollIntoView(target.SelectedItem);
        navigatingSuggestions = false;
        target.Focus();
    }

    private async void OnSearchSuggestionListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            MoveSearchSuggestion(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: QrSearchSuggestion suggestion })
        {
            e.Handled = true;
            await ApplySearchSuggestionAsync(suggestion);
            return;
        }
        if (e.Key == Key.Escape)
        {
            SearchSuggestionsPopup.IsOpen = false;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private async void OnUserFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || suppressFilterChanges) return;
        await ReloadAsync();
    }

    private async void OnAddressTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || suppressFilterChanges) return;
        addressDebounce.Stop();
        addressCancellation?.Cancel();
        selectedAddress = null;
        AddressSuggestionList.ItemsSource = Array.Empty<QrAddressFilterOption>();
        AddressSuggestionsPopup.IsOpen = false;

        var addressType = (AddressTypeFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        suppressFilterChanges = true;
        AddressLookupBox.Text = string.Empty;
        AddressLookupBox.IsEnabled = addressType.Length > 0;
        AddressLookupBox.Background = addressType.Length > 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(226, 230, 233));
        suppressFilterChanges = false;
        if (!initialized) return;
        if (addressType.Length > 0) AddressLookupBox.Focus();
        await ReloadAsync();
    }

    private async void OnAddressSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || suppressFilterChanges || !AddressLookupBox.IsEnabled) return;
        var hadSelection = selectedAddress is not null;
        selectedAddress = null;
        AddressSuggestionsPopup.IsOpen = false;
        addressDebounce.Stop();
        if (AddressLookupBox.Text.Trim().Length >= 2)
            addressDebounce.Start();
        if (hadSelection) await ReloadAsync();
    }

    private async void OnAddressDebounceTick(object? sender, EventArgs e)
    {
        addressDebounce.Stop();
        var addressType = (AddressTypeFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        var search = AddressLookupBox.Text.Trim();
        if (addressType.Length == 0 || search.Length < 2) return;

        addressCancellation?.Cancel();
        addressCancellation?.Dispose();
        addressCancellation = new CancellationTokenSource();
        try
        {
            var suggestions = await repository.LoadAddressSuggestionsAsync(
                addressType, search, 20, addressCancellation.Token);
            if (!string.Equals(search, AddressLookupBox.Text.Trim(), StringComparison.Ordinal)) return;
            AddressSuggestionList.ItemsSource = suggestions;
            AddressSuggestionsPopup.IsOpen = suggestions.Count > 0 && AddressLookupBox.IsKeyboardFocusWithin;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            AddressSuggestionsPopup.IsOpen = false;
        }
    }

    private async void OnAddressSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (navigatingSuggestions) return;
        if (AddressSuggestionList.SelectedItem is not QrAddressFilterOption option) return;
        await ApplyAddressSuggestionAsync(option);
    }

    private async Task ApplyAddressSuggestionAsync(QrAddressFilterOption option)
    {
        selectedAddress = option;
        suppressFilterChanges = true;
        AddressLookupBox.Text = option.DisplayText;
        AddressLookupBox.CaretIndex = AddressLookupBox.Text.Length;
        suppressFilterChanges = false;
        AddressSuggestionsPopup.IsOpen = false;
        AddressSuggestionList.SelectedItem = null;
        AddressLookupBox.Focus();
        await ReloadAsync();
    }

    private void OnAddressSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up) || AddressSuggestionList.Items.Count == 0) return;
        AddressSuggestionsPopup.IsOpen = true;
        MoveAddressSuggestion(e.Key == Key.Down ? 1 : -1);
        e.Handled = true;
    }

    private async void OnAddressSuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            MoveAddressSuggestion(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && AddressSuggestionList.SelectedItem is QrAddressFilterOption option)
        {
            e.Handled = true;
            await ApplyAddressSuggestionAsync(option);
            return;
        }
        if (e.Key == Key.Escape)
        {
            AddressSuggestionsPopup.IsOpen = false;
            AddressLookupBox.Focus();
            e.Handled = true;
        }
    }

    private void MoveAddressSuggestion(int direction)
    {
        if (AddressSuggestionList.Items.Count == 0) return;
        var current = AddressSuggestionList.SelectedIndex;
        var next = current < 0
            ? (direction > 0 ? 0 : AddressSuggestionList.Items.Count - 1)
            : Math.Clamp(current + direction, 0, AddressSuggestionList.Items.Count - 1);
        navigatingSuggestions = true;
        AddressSuggestionList.SelectedIndex = next;
        AddressSuggestionList.ScrollIntoView(AddressSuggestionList.SelectedItem);
        navigatingSuggestions = false;
        AddressSuggestionList.Focus();
    }

    private async void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (selectedSuggestion is null && string.IsNullOrWhiteSpace(resolvedBarcode) &&
            DetectSearchMode(SearchBox.Text.Trim()) == QrMovementSearchMode.Barcode)
        {
            searchDebounce.Stop();
            await ResolveBarcodeAndReloadAsync();
        }
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Down or Key.Up) && MedicineSuggestionList.Items.Count + BrandSuggestionList.Items.Count > 0)
        {
            SearchSuggestionsPopup.IsOpen = true;
            MoveSearchSuggestion(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchSuggestionsPopup.IsOpen)
        {
            SearchSuggestionsPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        searchDebounce.Stop();
        suggestionDebounce.Stop();
        SearchSuggestionsPopup.IsOpen = false;
        if (selectedSuggestion is null && string.IsNullOrWhiteSpace(resolvedBarcode) &&
            DetectSearchMode(SearchBox.Text.Trim()) == QrMovementSearchMode.Barcode)
            await ResolveBarcodeAndReloadAsync();
        else
            await ReloadAsync();
    }

    private async Task ResolveBarcodeAndReloadAsync()
    {
        var barcode = SearchBox.Text.Trim();
        if (DetectSearchMode(barcode) != QrMovementSearchMode.Barcode) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var resolution = await repository.ResolveBarcodeAsync(barcode, timeout.Token);
            if (!string.Equals(barcode, SearchBox.Text.Trim(), StringComparison.Ordinal)) return;
            resolvedBarcode = barcode;
            selectedSuggestion = null;
            if (resolution is not null)
            {
                suppressFilterChanges = true;
                SearchBox.Text = resolution.DisplayName;
                SearchBox.CaretIndex = SearchBox.Text.Length;
                SearchBox.ToolTip = $"Barkod: {resolution.Barcode}";
                suppressFilterChanges = false;
            }
            UpdateTitle();
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || suppressFilterChanges) return;
        if (StartDatePicker.SelectedDate is null || EndDatePicker.SelectedDate is null) return;

        suppressFilterChanges = true;
        var today = DateTime.Today;
        var start = StartDatePicker.SelectedDate.Value.Date;
        var end = EndDatePicker.SelectedDate.Value.Date;
        if (start > today) start = today;
        if (end > today) end = today;
        if (start > end)
        {
            if (ReferenceEquals(sender, StartDatePicker)) end = start;
            else start = end;
        }
        StartDatePicker.SelectedDate = start;
        EndDatePicker.SelectedDate = end;
        customRangeStart = null;
        customRangeEndExclusive = null;
        CustomRangeBox.Text = string.Empty;
        activePeriodTitle = start == end ? "Günlük" : null;
        suppressFilterChanges = false;
        UpdateDateConstraints();
        UpdateTitle();
        await ReloadAsync();
    }

    private async void OnPeriodPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string period }) return;
        var today = DateTime.Today;
        var start = today;
        if (period == "Week") start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        else if (period == "Month") start = new DateTime(today.Year, today.Month, 1);
        else if (period == "Year") start = new DateTime(today.Year, 1, 1);

        suppressFilterChanges = true;
        StartDatePicker.SelectedDate = start;
        EndDatePicker.SelectedDate = today;
        CustomRangeBox.Text = string.Empty;
        customRangeStart = null;
        customRangeEndExclusive = null;
        activePeriodTitle = period switch
        {
            "Day" => "Günlük",
            "Week" => "Haftalık",
            "Month" => "Aylık",
            "Year" => "Yıllık",
            _ => null
        };
        suppressFilterChanges = false;
        UpdateDateConstraints();
        UpdateTitle();
        await ReloadAsync();
    }

    private void UpdateDateConstraints()
    {
        var today = DateTime.Today;
        var start = StartDatePicker.SelectedDate?.Date ?? today;
        var end = EndDatePicker.SelectedDate?.Date ?? today;
        StartDatePicker.DisplayDateEnd = end < today ? end : today;
        EndDatePicker.DisplayDateStart = start;
        EndDatePicker.DisplayDateEnd = today;
    }

    private void OnCustomRangePreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var proposed = CustomRangeBox.Text.Remove(CustomRangeBox.SelectionStart, CustomRangeBox.SelectionLength)
            .Insert(CustomRangeBox.SelectionStart, e.Text);
        e.Handled = !int.TryParse(proposed, out var value) || value <= 0;
    }

    private void OnCustomRangePasting(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        if (!int.TryParse(text, out var value) || value <= 0) e.CancelCommand();
    }

    private async void OnCustomRangeLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        await ApplyCustomRangeAsync();

    private async void OnCustomRangeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyCustomRangeAsync();
    }

    private async void OnCustomRangeUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || suppressFilterChanges) return;
        await ApplyCustomRangeAsync();
    }

    private async Task ApplyCustomRangeAsync()
    {
        if (!initialized || !int.TryParse(CustomRangeBox.Text, out var value) || value <= 0) return;
        if (CustomRangeUnit.SelectedItem is not ComboBoxItem { Tag: string unit }) return;

        var end = DateTime.Now.AddMilliseconds(1);
        DateTime start;
        try
        {
            start = unit switch
            {
                "Hour" => end.AddHours(-value),
                "Day" => end.AddDays(-value),
                "Week" => end.AddDays(-7d * value),
                "Month" => end.AddMonths(-value),
                "Year" => end.AddYears(-value),
                _ => end.AddDays(-value)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            CustomRangeBox.BorderBrush = Brushes.Firebrick;
            return;
        }

        CustomRangeBox.ClearValue(Border.BorderBrushProperty);
        customRangeStart = start;
        customRangeEndExclusive = end;
        activePeriodTitle = null;
        suppressFilterChanges = true;
        StartDatePicker.SelectedDate = start.Date;
        EndDatePicker.SelectedDate = DateTime.Today;
        suppressFilterChanges = false;
        UpdateDateConstraints();
        UpdateTitle();
        await ReloadAsync();
    }

    private async void OnMovementTypeChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || suppressFilterChanges) return;
        UpdateMovementTypeButton();
        await ReloadAsync();
    }

    private async void OnSelectAllMovementTypes(object sender, RoutedEventArgs e)
    {
        foreach (var option in movementTypeOptions) option.IsSelected = true;
        MovementTypeList.Items.Refresh();
        UpdateMovementTypeButton();
        await ReloadAsync();
    }

    private async void OnClearMovementTypes(object sender, RoutedEventArgs e)
    {
        foreach (var option in movementTypeOptions) option.IsSelected = false;
        MovementTypeList.Items.Refresh();
        UpdateMovementTypeButton();
        await ReloadAsync();
    }

    private void UpdateMovementTypeButton()
    {
        var selectedCount = movementTypeOptions.Count(option => option.IsSelected);
        MovementTypeButton.Content = $"Hareket Türü ({selectedCount}/{movementTypeOptions.Count})";
    }

    private void OnSplitDirectionChanged(object sender, RoutedEventArgs e)
    {
        var split = SplitDirectionToggle.IsChecked == true;
        MovementGrid.Visibility = split ? Visibility.Collapsed : Visibility.Visible;
        SplitGridHost.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnMovementGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= e.ViewportHeight) return;
        var scrollableHeight = e.ExtentHeight - e.ViewportHeight;
        if (e.VerticalOffset >= scrollableHeight * 0.75)
            await LoadMoreAsync();
    }

    private void OnMovementRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid)
            grid.SelectedItem = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as QrMovementItem;
    }

    private void OnMovementContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var item = e.CursorLeft < 0 && e.CursorTop < 0
            ? grid.SelectedItem as QrMovementItem
            : FindVisualParent<DataGridRow>(grid.InputHitTest(Mouse.GetPosition(grid)) as DependencyObject)?.Item as QrMovementItem;
        if (item is null) { e.Handled = true; return; }
        grid.SelectedItem = item;
        PopulateMovementMenu(grid.ContextMenu, item);
        if (grid.ContextMenu.Items.Count == 0) e.Handled = true;
    }

    private void PopulateMovementMenu(ContextMenu menu, QrMovementItem movement)
    {
        menu.Items.Clear();
        void Add(string title, Func<Task> action)
        {
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { ShowPopupMessage($"Could not open the linked record: {exception.Message}", "Related Record", MessageBoxImage.Error); }
            };
            menu.Items.Add(item);
        }
        if (movement.ProductRecordId is not null) Add("Go to Product", () => OpenProductPopupAsync(movement));
        var addressLabel = movement.MovementAddressEntityType switch
        {
            "Patient" => "Go to Patient", "Pharmacy" => "Go to Pharmacy", "Depot" => "Go to Warehouse (Depot)", _ => null
        };
        if (addressLabel is not null && !string.IsNullOrWhiteSpace(movement.MovementAddressRecordKey))
            Add(addressLabel, () => { OpenMovementAddressPopup(movement); return Task.CompletedTask; });
        if (!string.IsNullOrWhiteSpace(movement.UserId))
            Add("Go to User", () => { OpenUserPopup(movement); return Task.CompletedTask; });
        if (TryGetRetailSaleId(movement, out var saleId))
            Add("Go to Sale", () => { OpenRetailSalePopup(movement, saleId); return Task.CompletedTask; });
        if (movement.MovementTypeCode == "PRESC_OUT")
            Add("Go to Prescription", () => OpenLinkedPrescriptionAsync(movement));
    }

    private async Task OpenLinkedPrescriptionAsync(QrMovementItem movement)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var snapshot = await new MedulaPrescriptionRepository().LoadLocalSnapshotForQrAsync(movement.QrCode, timeout.Token);
        if (snapshot is null)
        {
            ShowPopupMessage("The linked prescription is not available locally.", "Prescription", MessageBoxImage.Information);
            return;
        }
        var view = new MedulaPrescriptionEntryView(snapshot);
        var number = snapshot.Page.FieldValues?.GetValueOrDefault("medula:prescription-no") ?? "";
        CreatePopupWindow($"Prescription {number}", view, 1280, 880, 980, 650).Show();
    }

    private async void OnMovementDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        var cell = FindVisualParent<DataGridCell>(source);
        if (cell?.DataContext is not QrMovementItem movement || cell.Column.Header is not string header) return;
        grid.SelectedItem = movement;
        e.Handled = true;

        try
        {
            switch (header)
            {
                case "Ürün Adı":
                    await OpenProductPopupAsync(movement);
                    break;
                case "Hareket Adresi":
                    OpenMovementAddressPopup(movement);
                    break;
                case "Kullanıcı":
                    OpenUserPopup(movement);
                    break;
                case "Referans Kodu":
                case "Referans":
                case "Hareket Türü":
                case "Hareket Tarihi":
                    if (TryGetRetailSaleId(movement, out var saleId))
                        OpenRetailSalePopup(movement, saleId);
                    else
                        ShowPopupMessage(
                            "Bu sütuna ait referans ekranı henüz kullanıma hazır değil.",
                            "Referans Ekranı",
                            MessageBoxImage.Warning);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowPopupMessage($"Detay ekranı açılamadı: {ex.Message}", "Detay Hatası", MessageBoxImage.Error);
        }
    }

    private async Task OpenProductPopupAsync(QrMovementItem movement)
    {
        if (movement.ProductRecordId is not int recordId)
        {
            ShowPopupMessage("Bu ürün katalogda bulunamadı.", "Ürün Detayı", MessageBoxImage.Warning);
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var product = await productRepository.LoadByRecordIdAsync(recordId, timeout.Token);
        if (product is null)
        {
            ShowPopupMessage("Bu ürün katalogda bulunamadı.", "Ürün Detayı", MessageBoxImage.Warning);
            return;
        }

        var view = new ProductCatalogView(new ProductCatalogLaunchOptions(InitialProduct: product));
        var window = CreatePopupWindow(view.CurrentModuleTitle, view, 1280, 850, 980, 650);
        view.ModuleTitleChanged += title => window.Title = title;
        window.Show();
    }

    private void OpenMovementAddressPopup(QrMovementItem movement)
    {
        if (string.IsNullOrWhiteSpace(movement.MovementAddressRecordKey))
        {
            ShowPopupMessage("Hareket adresine ait kart bulunamadı.", "Hareket Adresi", MessageBoxImage.Warning);
            return;
        }

        var definition = movement.MovementAddressEntityType switch
        {
            "Patient" => CardDefinitions.Patients,
            "Pharmacy" => CardDefinitions.Pharmacies,
            "Depot" => CardDefinitions.Depots,
            _ => null
        };
        if (definition is null)
        {
            ShowPopupMessage("Hareket adresi türü tanınmıyor.", "Hareket Adresi", MessageBoxImage.Warning);
            return;
        }

        OpenCardPopup(definition, movement.MovementAddressRecordKey, movement.MovementAddressDisplay);
    }

    private void OpenUserPopup(QrMovementItem movement)
    {
        if (string.IsNullOrWhiteSpace(movement.UserId))
        {
            ShowPopupMessage("Bu harekete bağlı kullanıcı bulunmuyor.", "Kullanıcı", MessageBoxImage.Warning);
            return;
        }

        OpenCardPopup(CardDefinitions.Users, movement.UserId, movement.UserDisplay);
    }

    private static bool TryGetRetailSaleId(QrMovementItem movement, out int saleId)
    {
        saleId = 0;
        return string.Equals(movement.MovementTypeCode, "PERAK_OUT", StringComparison.Ordinal)
               && movement.ReferenceCode is { Length: > 1 } referenceCode
               && referenceCode[0] == 'P'
               && int.TryParse(referenceCode.AsSpan(1), out saleId)
               && saleId > 0;
    }

    private void OpenRetailSalePopup(QrMovementItem movement, int saleId)
    {
        var view = new RetailSalesListView(new RetailSalesListLaunchOptions(saleId, movement.MovementDate.Date));
        var window = CreatePopupWindow("Perakende Satış Listesi", view, 1420, 900, 1100, 700);
        window.Show();
    }

    private void OpenCardPopup(CardEntityDefinition definition, string key, string displayName)
    {
        var editor = new CardEditorView(definition, key, displayName, () => { }, false);
        var (width, height) = definition.Kind switch
        {
            CardEntityKind.Patients => (1280d, 880d),
            CardEntityKind.Depots => (1120d, 850d),
            CardEntityKind.Pharmacies => (960d, 660d),
            _ => (1120d, 760d)
        };
        var window = CreatePopupWindow(
            editor.CurrentModuleTitle, editor, width, height,
            Math.Min(820, width), Math.Min(560, height));
        editor.ModuleTitleChanged += title => window.Title = title;
        editor.CloseRequested += window.Close;
        window.Show();
    }

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

    private void ShowPopupMessage(string message, string title, MessageBoxImage image)
    {
        var owner = Window.GetWindow(this);
        if (owner is null) pharmacyApp.Localization.EnglishMessageBox.Show(message, title, MessageBoxButton.OK, image);
        else pharmacyApp.Localization.EnglishMessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return null;
    }

    private string BuildTitle()
    {
        var (_, searchMode) = GetEffectiveSearch();
        if (searchMode == QrMovementSearchMode.QrCode)
            return "Karekod Hareketleri (Karekoda Özel)";
        return string.IsNullOrWhiteSpace(activePeriodTitle)
            ? "Karekod Hareketleri"
            : $"Karekod Hareketleri ({activePeriodTitle})";
    }

    private void UpdateTitle() => ModuleTitleChanged?.Invoke(CurrentModuleTitle);

    private void SetStatus(string message, bool showRetry)
    {
        StatusMessage.Text = message;
        RetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Visible;
    }
}
