using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using IOPath = System.IO.Path;
using Microsoft.Web.WebView2.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using pharmacyApp.Modules;
using pharmacyApp.Modules.StockMovements;
using pharmacyApp.AI.Models;

namespace pharmacyApp.Modules.Products;

public enum ProductCatalogInitialTab
{
    General,
    Equivalent
}

public sealed record ProductCatalogLaunchOptions(
    bool OnlyInStockOnLaunch = false,
    ProductCatalogItem? InitialProduct = null,
    IReadOnlyList<ProductCatalogItem>? NavigationHistory = null,
    int NavigationIndex = -1,
    ProductCatalogInitialTab InitialTab = ProductCatalogInitialTab.General,
    IReadOnlyList<string>? Barcodes = null)
{
    public static ProductCatalogLaunchOptions Default { get; } = new();
}

public partial class ProductCatalogView : UserControl, IModuleTitleSource, IModuleIdentitySource, IModulePanelRequestSource, IModuleTabRequestSource, IPanelHostModeAware, IModuleTabContextAware, IAiApplicationContextSource
{
    private const string AllFilter = "";
    private const double SidebarExpandedWidth = 344;
    private const double AnalysisGraphLeft = 43;
    private const double AnalysisGraphRight = 12;
    private const double AnalysisGraphTop = 12;
    private const double AnalysisGraphBottom = 38;
    internal const int PageSize = 300;
    private readonly ProductCatalogRepository repository = new();
    private readonly ProductAnalysisRepository analysisRepository = new();
    private readonly IProductPriceRefreshConnector priceRefreshConnector;
    private readonly ProductCatalogLaunchOptions launchOptions;
    private readonly List<ProductCatalogItem> navigationHistory = [];
    private readonly ObservableCollection<ProductCatalogItem> visibleProducts = [];
    private readonly DispatcherTimer filterDebounce;
    private readonly HashSet<int> requestedMissingPriceRefreshes = [];
    private ProductCatalogItem? selectedProduct;
    private string? headerFilterField;
    private string? headerFilterValue;
    private CancellationTokenSource? pageCancellation;
    private CancellationTokenSource? selectionCancellation;
    private bool suppressFilterChanges;
    private bool filtersInitialized;
    private bool isLoadingPage;
    private bool hasMore;
    private bool initialViewLoadCompleted;
    private bool isIgnoringFiltersForSearch;
    private bool ignoredFilterSearchDismissed;
    private string sortField = "Name";
    private bool sortDescending;
    private int? rememberedRecordId;
    private int rememberedTabIndex;
    private int totalCount;
    private int navigationIndex = -1;
    private bool navigatingHistory;
    private bool sidebarVisible = true;
    private bool detailOnly;
    private bool medicationListAvailableInCurrentTab;
    private bool preservingDetailDuringReload;
    private ProductCatalogItem? preservedProductDuringReload;
    private bool scrollThumbDragging;
    private double lastScrollOffset;
    private double lastScrollViewport;
    private double lastScrollExtent;
    private int? kubViewerRecordId;
    private int? kitViewerRecordId;
    private ProductCatalogItem? pendingListComparisonBase;
    private readonly List<UIElement> analysisHoverElements = [];
    private bool suppressAnalysisMovementTypeChanges;

    private ProductCatalogItem? DetailIdentityProduct => selectedProduct ?? launchOptions.InitialProduct;
    private bool DefaultOnlyOnMarket => !launchOptions.OnlyInStockOnLaunch;
    public string CurrentModuleTitle => detailOnly && DetailIdentityProduct is not null
        ? DetailIdentityProduct.DisplayName
        : OnlyInStockToggle.IsChecked == true ? "Stok" : "Ürün Katalogu";
    public string CurrentModuleId => detailOnly && DetailIdentityProduct is not null
        ? $"products.detail.{DetailIdentityProduct.RecordId}"
        : OnlyInStockToggle.IsChecked == true ? "products.stock" : "products.catalog";
    public event Action<string>? ModuleTitleChanged;
    public event Action<string>? ModuleIdentityChanged;
    public event Action<ModulePanelRequest>? PanelRequested;
    public event Action<ModuleTabRequest>? TabRequested;

    public AiApplicationContext GetAiApplicationContext() => new(
        CurrentModule: CurrentModuleId,
        CurrentScreen: CurrentModuleTitle,
        SelectedProductId: selectedProduct?.RecordId.ToString(),
        SelectedBarcode: selectedProduct?.Barcode);

    public ProductCatalogView() : this(ProductCatalogLaunchOptions.Default, ProductPriceRefreshConnector.Current)
    {
    }

    public ProductCatalogView(ProductCatalogLaunchOptions launchOptions) : this(launchOptions, ProductPriceRefreshConnector.Current)
    {
    }

    public ProductCatalogView(
        ProductCatalogLaunchOptions launchOptions,
        IProductPriceRefreshConnector priceRefreshConnector)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        this.launchOptions = launchOptions;
        this.priceRefreshConnector = priceRefreshConnector ?? throw new ArgumentNullException(nameof(priceRefreshConnector));
        InitializeComponent();
        AnalysisWantedMonthsBox.ItemsSource = Enumerable.Range(1, 120).Select(value => value / 2m).ToArray();
        AnalysisWantedMonthsBox.Text = ProductAnalysisCalculations.DefaultWantedStockMonths.ToString("0.0");
        detailOnly = launchOptions.InitialProduct is not null;
        if (launchOptions.NavigationHistory is { Count: > 0 } importedHistory)
        {
            navigationHistory.AddRange(importedHistory);
            navigationIndex = Math.Clamp(launchOptions.NavigationIndex, 0, navigationHistory.Count - 1);
        }
        InactiveQrSincePicker.SelectedDate = DateTime.Today.AddMonths(-3);
        OnlyOnMarketToggle.IsChecked = DefaultOnlyOnMarket;
        filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        filterDebounce.Tick += async (_, _) =>
        {
            filterDebounce.Stop();
            await ResetAndLoadAsync();
        };
        suppressFilterChanges = true;
        OnlyInStockToggle.IsChecked = launchOptions.OnlyInStockOnLaunch;
        if (launchOptions.OnlyInStockOnLaunch) { sortField = "Stock"; sortDescending = true; }
        suppressFilterChanges = false;
        IngredientFilter.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnFilterTextChanged));
        ProductList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnProductListScrollChanged));
        ProductList.ItemsSource = visibleProducts;
        if (launchOptions.InitialProduct is not null)
        {
            sidebarVisible = false;
            SidebarHost.Visibility = Visibility.Collapsed;
            SidebarSeparator.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            DetachDetailButton.Visibility = Visibility.Collapsed;
            visibleProducts.Add(launchOptions.InitialProduct);
        }
        UpdateSortHeaders();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ModuleTitleChanged?.Invoke(CurrentModuleTitle);
        ModuleIdentityChanged?.Invoke(CurrentModuleId);
        if (launchOptions.InitialProduct is not null && detailOnly && !filtersInitialized)
        {
            initialViewLoadCompleted = true;
            navigatingHistory = navigationHistory.Count > 0;
            if (ReferenceEquals(ProductList.SelectedItem, launchOptions.InitialProduct)) ProductList.SelectedItem = null;
            ProductList.SelectedItem = launchOptions.InitialProduct;
            if (launchOptions.InitialTab == ProductCatalogInitialTab.Equivalent)
                _ = Dispatcher.BeginInvoke(
                    () => CatalogTabs.SelectedItem = EquivalentTab,
                    DispatcherPriority.Loaded);
            navigatingHistory = false;
            UpdateNavigationButtons();
            return;
        }
        if (filtersInitialized && initialViewLoadCompleted)
        {
            pageCancellation = new CancellationTokenSource();
            RestoreRememberedState();
            return;
        }
        await InitializeAndLoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        rememberedRecordId = selectedProduct?.RecordId ?? rememberedRecordId;
        rememberedTabIndex = CatalogTabs.SelectedIndex;
        filterDebounce.Stop();
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = null;
        selectionCancellation?.Cancel();
        selectionCancellation?.Dispose();
        selectionCancellation = null;
    }

    private void RestoreRememberedState()
    {
        CatalogTabs.SelectedIndex = Math.Clamp(rememberedTabIndex, 0, Math.Max(0, CatalogTabs.Items.Count - 1));
        if (!rememberedRecordId.HasValue) return;

        var product = visibleProducts.FirstOrDefault(candidate => candidate.RecordId == rememberedRecordId.Value);
        if (product is null) return;
        if (ReferenceEquals(ProductList.SelectedItem, product)) ProductList.SelectedItem = null;
        ProductList.SelectedItem = product;
        ProductList.ScrollIntoView(product);
    }

    private async Task InitializeAndLoadAsync()
    {
        if (filtersInitialized)
        {
            await ResetAndLoadAsync();
            return;
        }

        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = new CancellationTokenSource();
        LoadingPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ProductList.IsEnabled = false;
        try
        {
            var options = await repository.LoadFilterOptionsAsync(pageCancellation.Token);
            InitializeFilters(options);
            await ResetAndLoadAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowLoadError(ex);
        }
        finally
        {
            ProductList.IsEnabled = true;
        }
    }

    private async Task ResetAndLoadAsync()
    {
        if (!filtersInitialized) return;

        filterDebounce.Stop();
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = new CancellationTokenSource();
        isLoadingPage = false;
        initialViewLoadCompleted = false;
        isIgnoringFiltersForSearch = false;
        preservedProductDuringReload = selectedProduct;
        preservingDetailDuringReload = preservedProductDuringReload is not null;
        visibleProducts.Clear();
        totalCount = 0;
        hasMore = true;
        UpdateFilterChipPresentation();
        ModuleTitleChanged?.Invoke(CurrentModuleTitle);
        ModuleIdentityChanged?.Invoke(CurrentModuleId);
        await LoadNextPageAsync(pageCancellation.Token, initialLoad: true);
        if (preservedProductDuringReload is not null)
        {
            var replacement = visibleProducts.FirstOrDefault(item => item.RecordId == preservedProductDuringReload.RecordId);
            if (replacement is not null)
            {
                ProductList.SelectedItem = replacement;
                ProductList.ScrollIntoView(replacement);
            }
        }
        preservingDetailDuringReload = false;
        preservedProductDuringReload = null;
    }

    private async Task LoadNextPageAsync(CancellationToken cancellationToken, bool initialLoad = false)
    {
        if (isLoadingPage || !hasMore) return;

        isLoadingPage = true;
        if (initialLoad) LoadingPanel.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        var loadSucceeded = false;
        try
        {
            var page = await repository.LoadPageAsync(BuildQuery(visibleProducts.Count, isIgnoringFiltersForSearch), cancellationToken);
            if (initialLoad && page.TotalCount == 0 && ShouldSearchIgnoringFilters())
            {
                isIgnoringFiltersForSearch = true;
                UpdateFilterChipPresentation();
                page = await repository.LoadPageAsync(BuildQuery(0, ignoreFilters: true), cancellationToken);
            }
            foreach (var product in page.Items) visibleProducts.Add(product);
            totalCount = page.TotalCount;
            hasMore = page.HasMore;
            EmptyMessage.Text = "Arama veya filtreleri değiştirin.";
            EmptyPanel.Visibility = visibleProducts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateResultCount();
            loadSucceeded = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowLoadError(ex);
        }
        finally
        {
            isLoadingPage = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (initialLoad && loadSucceeded) initialViewLoadCompleted = true;
        }
    }

    private void UpdateResultCount()
    {
        var unfilteredStock = OnlyInStockToggle.IsChecked == true &&
                              !isIgnoringFiltersForSearch &&
                              string.IsNullOrWhiteSpace(SearchBox.Text) &&
                              string.IsNullOrWhiteSpace(ProductPrefixFilter.SelectedItem as string) &&
                              string.IsNullOrWhiteSpace(IngredientFilter.Text) &&
                              string.IsNullOrWhiteSpace(PrescriptionFilter.SelectedItem as string) &&
                              OnlyOnMarketToggle.IsChecked != true &&
                              StockRangeToggle.IsChecked != true &&
                              string.IsNullOrWhiteSpace(headerFilterField);
        ResultCount.Text = $"İlaç Sayısı: {totalCount:N0}{(unfilteredStock ? " (Kalem Sayısı)" : string.Empty)}";
    }
    private void ShowLoadError(Exception ex)
    {
        if (visibleProducts.Count == 0)
        {
            EmptyMessage.Text = $"Veritabanına bağlanılamadı: {ex.Message}";
            EmptyPanel.Visibility = Visibility.Visible;
        }
        RetryButton.Visibility = Visibility.Visible;
        ResultCount.Text = visibleProducts.Count == 0 ? string.Empty : $"{visibleProducts.Count:N0} ürün yüklendi";
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnProductListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        lastScrollOffset = e.VerticalOffset;
        lastScrollViewport = e.ViewportHeight;
        lastScrollExtent = e.ExtentHeight;
        if (scrollThumbDragging) return;
        if (!filtersInitialized || isLoadingPage || !hasMore ||
            !ShouldLoadNextPage(e.VerticalOffset, e.ViewportHeight, e.ExtentHeight) ||
            pageCancellation is null)
            return;

        await LoadNextPageAsync(pageCancellation.Token);
    }

    private void InitializeFilters(ProductCatalogFilterOptions options)
    {
        if (filtersInitialized) return;

        suppressFilterChanges = true;
        SetFilterItems(ProductPrefixFilter, options.ProductPrefixes);
        SetFilterItems(IngredientFilter, options.Ingredients);
        SetFilterItems(PrescriptionFilter, options.Prescriptions);
        OnlyOnMarketToggle.IsChecked = DefaultOnlyOnMarket;
        StockRangeToggle.IsEnabled = options.StockDataAvailable;
        StockRangeToggle.ToolTip = options.StockDataAvailable
            ? "Stok aralığı filtresini etkinleştir"
            : "Stok verisi mevcut değil.";
        suppressFilterChanges = false;
        filtersInitialized = true;
    }

    private static void SetFilterItems(ComboBox comboBox, IEnumerable<string?> values)
    {
        var items = new[] { AllFilter }.Concat(values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));

        comboBox.ItemsSource = items;
        comboBox.SelectedIndex = 0;
    }

    private ProductCatalogQuery BuildQuery(int offset, bool ignoreFilters = false)
    {
        var stockMinimum = !ignoreFilters && StockRangeToggle.IsChecked == true
            ? ProductCatalogFormatting.ParseStockBound(StockMinimumFilter.Text)
            : null;
        var stockMaximum = !ignoreFilters && StockRangeToggle.IsChecked == true
            ? ProductCatalogFormatting.ParseStockBound(StockMaximumFilter.Text)
            : null;
        if (stockMinimum.HasValue && stockMaximum.HasValue && stockMinimum > stockMaximum)
            (stockMinimum, stockMaximum) = (stockMaximum, stockMinimum);

        return new ProductCatalogQuery(
            Search: SearchBox.Text.Trim(),
            ProductPrefix: ignoreFilters ? string.Empty : ProductPrefixFilter.SelectedItem as string ?? string.Empty,
            Ingredient: ignoreFilters ? string.Empty : IngredientFilter.Text.Trim(),
            Prescription: ignoreFilters ? string.Empty : PrescriptionFilter.SelectedItem as string ?? string.Empty,
            OnlyInStock: !ignoreFilters && OnlyInStockToggle.IsChecked == true,
            StockMinimum: stockMinimum,
            StockMaximum: stockMaximum,
            MarketStatus: !ignoreFilters && OnlyOnMarketToggle.IsChecked == true ? 1 : null,
            HeaderField: ignoreFilters ? null : headerFilterField,
            HeaderValue: ignoreFilters ? null : headerFilterValue,
            SortField: sortField,
            SortDescending: sortDescending,
            Offset: offset,
            Limit: PageSize,
            Barcodes: launchOptions.Barcodes,
            OnlyWithMovements: !ignoreFilters && OnlyWithMovementsToggle.IsChecked == true);
    }

    private bool ShouldSearchIgnoringFilters() =>
        !ignoredFilterSearchDismissed &&
        !string.IsNullOrWhiteSpace(SearchBox.Text) &&
        (OnlyInStockToggle.IsChecked == true ||
         !string.IsNullOrWhiteSpace(ProductPrefixFilter.SelectedItem as string) ||
         !string.IsNullOrWhiteSpace(IngredientFilter.Text) ||
         !string.IsNullOrWhiteSpace(PrescriptionFilter.SelectedItem as string) ||
         OnlyOnMarketToggle.IsChecked == true ||
         StockRangeToggle.IsChecked == true ||
         !string.IsNullOrWhiteSpace(headerFilterField));

    internal static bool ShouldLoadNextPage(double verticalOffset, double viewportHeight, double extentHeight) =>
        extentHeight > 0 && verticalOffset + viewportHeight >= extentHeight * 0.75;

    private async void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (suppressFilterChanges || !filtersInitialized) return;
        ResetIgnoredFilterFallback();
        ModuleTitleChanged?.Invoke(CurrentModuleTitle);
        ModuleIdentityChanged?.Invoke(CurrentModuleId);
        await ResetAndLoadAsync();
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressFilterChanges || !filtersInitialized) return;
        ResetIgnoredFilterFallback();
        filterDebounce.Stop();
        filterDebounce.Start();
    }

    private void OnStockRangeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (StockRangeToggle.IsChecked != true) return;
        OnFilterTextChanged(sender, e);
    }

    private void OnFilterToggleChanged(object sender, RoutedEventArgs e) =>
        FilterPanel.Visibility = FilterToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void OnListSortClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field } || field is not ("Name" or "Price" or "Stock")) return;

        if (string.Equals(sortField, field, StringComparison.Ordinal))
            sortDescending = !sortDescending;
        else
        {
            sortField = field;
            sortDescending = false;
        }

        ResetIgnoredFilterFallback();
        UpdateSortHeaders();
        await ResetAndLoadAsync();
    }

    private void UpdateSortHeaders()
    {
        NameSortHeader.Text = SortHeaderText("Ad", "Name");
        PriceSortHeader.Text = SortHeaderText("Fiyat", "Price");
        StockSortHeader.Text = SortHeaderText("Stok", "Stock");
    }

    private string SortHeaderText(string label, string field) =>
        string.Equals(sortField, field, StringComparison.Ordinal)
            ? $"{label} {(sortDescending ? "▼" : "▲")}"
            : label;

    private void ResetIgnoredFilterFallback()
    {
        ignoredFilterSearchDismissed = false;
        isIgnoringFiltersForSearch = false;
    }

    private async void OnClearIgnoredFilterSearchClicked(object sender, RoutedEventArgs e)
    {
        ignoredFilterSearchDismissed = true;
        isIgnoringFiltersForSearch = false;
        UpdateFilterChipPresentation();
        await ResetAndLoadAsync();
    }

    private async void OnProductSelected(object sender, SelectionChangedEventArgs e)
    {
        selectionCancellation?.Cancel();
        selectionCancellation?.Dispose();
        selectionCancellation = new CancellationTokenSource();

        if (ProductList.SelectedItem is not ProductCatalogItem product)
        {
            if (preservingDetailDuringReload) return;
            selectedProduct = null;
            ResetPdfViewers();
            DetailHost.Visibility = Visibility.Collapsed;
            NoSelectionPanel.Visibility = Visibility.Visible;
            EquivalentGrid.ItemsSource = null;
            CurrentEquivalentGrid.ItemsSource = null;
            GeneralEquivalentGrid.ItemsSource = null;
            PriceHistoryGrid.ItemsSource = null;
            PriceHistoryCanvas.Children.Clear();
            AnalysisCanvas.Children.Clear();
            AnalysisStatusText.Text = "Analiz verisi yükleniyor…";
            AnalysisStatusText.Visibility = Visibility.Visible;
            AnalysisSaveStatusText.Visibility = Visibility.Collapsed;
            ClearAnalysisMovementSelectors();
            QrCodeGrid.ItemsSource = null;
            return;
        }

        selectedProduct = product;
        ResetPdfViewers();
        if (!navigatingHistory) AddNavigationEntry(product);
        if (detailOnly)
        {
            ModuleTitleChanged?.Invoke(CurrentModuleTitle);
            ModuleIdentityChanged?.Invoke(CurrentModuleId);
        }
        UpdateNavigationButtons();
        DetailHost.DataContext = product;
        EnsureSelectedDetailTabIsAvailable(product);
        DetailHost.Visibility = Visibility.Visible;
        NoSelectionPanel.Visibility = Visibility.Collapsed;
        EquivalentGrid.ItemsSource = null;
        CurrentEquivalentGrid.ItemsSource = new[] { BuildEquivalentRows([], product).Current };
        GeneralEquivalentGrid.ItemsSource = Array.Empty<EquivalentProductRow>();
        PriceHistoryGrid.ItemsSource = null;
        PriceHistoryCanvas.Children.Clear();
        product.Analysis = null;
        AnalysisCanvas.Children.Clear();
        AnalysisStatusText.Text = "Analiz verisi yükleniyor…";
        AnalysisStatusText.Visibility = Visibility.Visible;
        AnalysisSaveStatusText.Visibility = Visibility.Collapsed;
        IngredientExpandToggle.IsChecked = false;
        IndicationExpandToggle.IsChecked = true;
        UpdateExpandableText();

        try
        {
            var token = selectionCancellation.Token;
            var historyTask = repository.LoadPriceHistoryAsync(product.RecordId, token);
            var analysisTask = analysisRepository.LoadAsync(product.RecordId, product.Barcode, token);
            var sgkEquivalentsTask = repository.LoadSgkEquivalentsAsync(product.EquivalentGroupCode, product.RecordId, token);
            var detailsTask = repository.LoadProductDetailsAsync(product.RecordId, token);
            var showInactiveQrCodes = InactiveQrToggle.IsChecked == true;
            var inactiveQrSince = InactiveQrSincePicker.SelectedDate ?? DateTime.Today.AddMonths(-3);
            var qrTask = repository.LoadQrCodesAsync(product.Barcode, showInactiveQrCodes, inactiveQrSince, token);
            var missingPriceRefreshTask = RefreshMissingPricesIfNeededAsync(product, token);

            var details = await AwaitOrDefaultAsync(detailsTask, new ProductDetailPayload([], null, null, [], null, null, null));
            if (!ReferenceEquals(selectedProduct, product)) return;
            product.Photos = details.Photos;
            product.KubDocument = details.KubDocument;
            product.KitDocument = details.KitDocument;
            product.WarningItems = details.Warnings;
            product.SutSummaryJson = details.SutSummaryJson;
            product.MedulaInfoJson = details.MedulaInfoJson;
            product.MedulaDetailsJson = details.MedulaDetailsJson;
            var sutOverview = SutOverviewFormatter.Parse(details.SutSummaryJson, details.MedulaInfoJson, details.MedulaDetailsJson);
            product.SutOverviewDateDisplay = sutOverview.DateDisplay;
            product.SutOverviewSections = sutOverview.Sections;

            var generalEquivalentsTask = repository.LoadGeneralEquivalentsAsync(product.AllEquivalentBarcodes, product.RecordId, token);
            var history = await AwaitOrDefaultAsync(historyTask, Array.Empty<PriceHistoryPoint>());
            ProductAnalysisData? analysis = null;
            try { analysis = await analysisTask; }
            catch (OperationCanceledException) { throw; }
            catch { }
            var sgkEquivalents = await AwaitOrDefaultAsync(sgkEquivalentsTask, Array.Empty<ProductCatalogItem>());
            var generalEquivalents = await AwaitOrDefaultAsync(generalEquivalentsTask, Array.Empty<ProductCatalogItem>());
            var qrCodes = await AwaitOrDefaultAsync(qrTask, Array.Empty<QrCodeDisplayItem>());
            await AwaitOrDefaultAsync(missingPriceRefreshTask);
            if (!ReferenceEquals(selectedProduct, product)) return;

            product.PriceHistory = history;
            product.Analysis = analysis;
            DetailHost.DataContext = null;
            DetailHost.DataContext = product;
            EnsureSelectedDetailTabIsAvailable(product);
            await RefreshSelectedPdfAsync();
            ProductList.Items.Refresh();
            var equivalentRows = BuildEquivalentRows(sgkEquivalents, product);
            CurrentEquivalentGrid.ItemsSource = new[] { equivalentRows.Current };
            EquivalentGrid.ItemsSource = equivalentRows.Others;
            var combinedGeneralEquivalents = sgkEquivalents
                .Concat(generalEquivalents)
                .GroupBy(item => item.RecordId)
                .Select(group => group.First())
                .ToList();
            GeneralEquivalentGrid.ItemsSource = BuildEquivalentRows(combinedGeneralEquivalents, product).Others;
            QrCodeGrid.ItemsSource = qrCodes;
            for (var index = 0; index < EquivalentGrid.Columns.Count; index++)
                EquivalentGrid.Columns[index].SortDirection = index == 1 ? System.ComponentModel.ListSortDirection.Ascending : null;
            for (var index = 0; index < GeneralEquivalentGrid.Columns.Count; index++)
                GeneralEquivalentGrid.Columns[index].SortDirection = index == 1 ? System.ComponentModel.ListSortDirection.Ascending : null;
            UpdateHistoricalPriceDifferenceAvailability(product);
            PriceHistoryGrid.ItemsSource = product.PriceHistory.OrderByDescending(point => point.EffectiveDate);
            _ = Dispatcher.BeginInvoke(RenderPriceHistoryGraph, DispatcherPriority.Loaded);
            AnalysisStatusText.Text = analysis is null ? "Analiz verisi yüklenemedi." : string.Empty;
            AnalysisStatusText.Visibility = analysis is null ? Visibility.Visible : Visibility.Collapsed;
            if (analysis is not null)
            {
                AnalysisWantedMonthsBox.Text = analysis.WantedStockMonths.ToString("0.0");
                ConfigureAnalysisMovementSelectors(analysis);
            }
            else
            {
                ClearAnalysisMovementSelectors();
            }
            _ = Dispatcher.BeginInvoke(RenderAnalysisGraph, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PriceHistoryCanvas.Children.Add(new TextBlock
            {
                Text = $"Detay verileri yüklenemedi: {ex.Message}",
                Foreground = new SolidColorBrush(Color.FromRgb(180, 60, 50))
            });
        }
    }

    private static async Task<T> AwaitOrDefaultAsync<T>(Task<T> task, T fallback)
    {
        try { return await task; }
        catch (OperationCanceledException) { throw; }
        catch { return fallback; }
    }

    private static async Task AwaitOrDefaultAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private async Task RefreshMissingPricesIfNeededAsync(
        ProductCatalogItem product,
        CancellationToken cancellationToken)
    {
        if (product.WarehousePrice is not null ||
            product.ManufacturerPrice is not null ||
            !requestedMissingPriceRefreshes.Add(product.RecordId))
            return;

        var result = await priceRefreshConnector.RefreshMissingPricesAsync(
            new ProductPriceRefreshRequest(
                product.RecordId,
                product.Barcode,
                DepotSaleMissing: true,
                ManufacturerSaleMissing: true),
            cancellationToken);
        if (result is null) return;

        product.WarehousePrice = result.DepotSale;
        product.ManufacturerPrice = result.ManufacturerSale;
        product.VatRate = result.VatRate;
        product.Currency = result.Currency;
    }

    private void OnPreviousPhotoClicked(object sender, RoutedEventArgs e) => selectedProduct?.MovePhoto(-1);

    private void OnNextPhotoClicked(object sender, RoutedEventArgs e) => selectedProduct?.MovePhoto(1);

    private async void OnCatalogTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, CatalogTabs) || selectedProduct is null) return;
        if (ReferenceEquals(CatalogTabs.SelectedItem, KubTab))
            await ShowPdfInAppAsync("KUB", KubPdfViewer, KubPdfStatus, KubPdfStatusText);
        else if (ReferenceEquals(CatalogTabs.SelectedItem, KitTab))
            await ShowPdfInAppAsync("KIT", KitPdfViewer, KitPdfStatus, KitPdfStatusText);
    }

    private async Task RefreshSelectedPdfAsync()
    {
        if (ReferenceEquals(CatalogTabs.SelectedItem, KubTab))
            await ShowPdfInAppAsync("KUB", KubPdfViewer, KubPdfStatus, KubPdfStatusText);
        else if (ReferenceEquals(CatalogTabs.SelectedItem, KitTab))
            await ShowPdfInAppAsync("KIT", KitPdfViewer, KitPdfStatus, KitPdfStatusText);
    }

    private void ResetPdfViewers()
    {
        kubViewerRecordId = null;
        kitViewerRecordId = null;
        KubPdfViewer.Visibility = Visibility.Collapsed;
        KitPdfViewer.Visibility = Visibility.Collapsed;
        KubPdfStatusText.Text = "KÜB belgesi hazırlanıyor…";
        KitPdfStatusText.Text = "KİT belgesi hazırlanıyor…";
        KubPdfStatus.Visibility = Visibility.Visible;
        KitPdfStatus.Visibility = Visibility.Visible;
    }

    private async Task ShowPdfInAppAsync(
        string documentType,
        WebView2 viewer,
        FrameworkElement statusHost,
        TextBlock statusText)
    {
        var product = selectedProduct;
        if (product is null) return;
        var bytes = documentType == "KUB" ? product.KubDocument : product.KitDocument;
        if (bytes is not { Length: > 0 }) return;

        var loadedRecordId = documentType == "KUB" ? kubViewerRecordId : kitViewerRecordId;
        if (loadedRecordId == product.RecordId && viewer.Visibility == Visibility.Visible) return;

        viewer.Visibility = Visibility.Collapsed;
        statusText.Text = $"{(documentType == "KUB" ? "KÜB" : "KİT")} belgesi hazırlanıyor…";
        statusHost.Visibility = Visibility.Visible;

        try
        {
            var folder = IOPath.Combine(pharmacyApp.Infrastructure.AppPaths.DataDirectory, "ProductDocuments");
            Directory.CreateDirectory(folder);
            var safeBarcode = string.Concat((product.Barcode ?? product.RecordId.ToString())
                .Where(character => !IOPath.GetInvalidFileNameChars().Contains(character)));
            var path = IOPath.Combine(folder, $"{safeBarcode}-{documentType.ToLowerInvariant()}.pdf");
            await File.WriteAllBytesAsync(path, bytes);

            await viewer.EnsureCoreWebView2Async();
            viewer.CoreWebView2.Settings.AreDevToolsEnabled = false;
            viewer.CoreWebView2.Settings.IsStatusBarEnabled = false;
            viewer.Source = new Uri(path, UriKind.Absolute);
            if (documentType == "KUB") kubViewerRecordId = product.RecordId;
            else kitViewerRecordId = product.RecordId;
            if (!ReferenceEquals(selectedProduct, product)) return;
            viewer.Visibility = Visibility.Visible;
            statusHost.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            statusText.Text = $"PDF uygulama içinde görüntülenemedi: {exception.Message}";
            statusHost.Visibility = Visibility.Visible;
        }
    }

    private async void OnQrFilterChanged(object sender, RoutedEventArgs e)
    {
        var showInactive = InactiveQrToggle.IsChecked == true;
        InactiveQrDateLabel.Visibility = showInactive ? Visibility.Visible : Visibility.Collapsed;
        InactiveQrQuickDates.Visibility = showInactive ? Visibility.Visible : Visibility.Collapsed;
        InactiveQrSincePicker.Visibility = showInactive ? Visibility.Visible : Visibility.Collapsed;
        QrMovementDateColumn.Header = showInactive ? "Çıkış Tarihi" : "Giriş Tarihi";
        QrExitPriceColumn.Visibility = showInactive ? Visibility.Visible : Visibility.Collapsed;
        QrProfitColumn.Visibility = showInactive ? Visibility.Visible : Visibility.Collapsed;
        if (selectedProduct is null || selectionCancellation is null) return;

        try
        {
            var product = selectedProduct;
            var since = InactiveQrSincePicker.SelectedDate ?? DateTime.Today.AddMonths(-3);
            var rows = await repository.LoadQrCodesAsync(product.Barcode, showInactive, since, selectionCancellation.Token);
            if (ReferenceEquals(selectedProduct, product)) QrCodeGrid.ItemsSource = rows;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            QrCodeGrid.ItemsSource = Array.Empty<QrCodeDisplayItem>();
        }
    }

    private void OnQrPeriodPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string period }) return;
        var today = DateTime.Today;
        InactiveQrSincePicker.SelectedDate = period switch
        {
            "Week" => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            "Month" => new DateTime(today.Year, today.Month, 1),
            "Year" => new DateTime(today.Year, 1, 1),
            _ => today
        };
    }

    private void OnQrGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        if (!QrCodeGrid.SelectedItems.Contains(row.Item))
        {
            QrCodeGrid.SelectedItems.Clear();
            QrCodeGrid.SelectedItem = row.Item;
        }
        var cell = FindVisualAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is not null)
            QrCodeGrid.CurrentCell = new DataGridCellInfo(row.Item, cell.Column);
    }

    private void OnQrGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = QrCodeGrid.InputHitTest(Mouse.GetPosition(QrCodeGrid)) as DependencyObject;
        if (FindVisualAncestor<DataGridRow>(hit) is null)
        {
            e.Handled = true;
            return;
        }

        var multipleRowsSelected = GetSelectedQrRows().Count > 1;
        QrOpenMovementsMenuItem.Visibility = QrCopySingleMenuItem.Visibility = multipleRowsSelected ? Visibility.Collapsed : Visibility.Visible;
        QrCopyMultipleMenuItem.Visibility = multipleRowsSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenQrMovementsClicked(object sender, RoutedEventArgs e)
    {
        if (QrCodeGrid.SelectedItem is not QrCodeDisplayItem row || string.IsNullOrWhiteSpace(row.QrCode)) return;
        var qrCode = row.QrCode.Trim();
        var options = new QrMovementsLaunchOptions(
            DateTime.Today,
            DateTime.Today,
            PeriodTitle: string.Empty,
            Search: qrCode,
            IgnoreDateFilter: true);
        TabRequested?.Invoke(new ModuleTabRequest(
            $"products.qr-movements.qr.{Uri.EscapeDataString(qrCode)}",
            "Karekod Hareketleri (Karekoda Özel)",
            () => new QrMovementsView(options),
            1050,
            560));
    }

    private void OnCopyQrCodeClicked(object sender, RoutedEventArgs e)
    {
        if (QrCodeGrid.SelectedItem is QrCodeDisplayItem row)
            TrySetClipboardText(row.QrCode);
    }

    private void OnCopyQrCodesClicked(object sender, RoutedEventArgs e)
    {
        var qrCodes = GetSelectedQrRows()
            .Select(row => row.QrCode?.Trim())
            .Where(qrCode => !string.IsNullOrWhiteSpace(qrCode))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (qrCodes.Length > 0)
            TrySetClipboardText(string.Join(Environment.NewLine, qrCodes!));
    }

    private IReadOnlyList<QrCodeDisplayItem> GetSelectedQrRows() => QrCodeGrid.Items
        .OfType<QrCodeDisplayItem>()
        .Where(row => QrCodeGrid.SelectedItems.Contains(row))
        .ToArray();

    private static bool TrySetClipboardText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(value.Trim(), true);
                return true;
            }
            catch (COMException)
            {
                if (attempt == 5) return false;
                Thread.Sleep(attempt * 20);
            }
        }
        return false;
    }

    private static (IReadOnlyList<EquivalentProductRow> Others, EquivalentProductRow Current) BuildEquivalentRows(
        IEnumerable<ProductCatalogItem> source,
        ProductCatalogItem selected)
    {
        var products = source
            .OrderBy(product => product.EffectivePriceDifference ?? decimal.MaxValue)
            .ThenBy(product => product.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var allProducts = products.Append(selected).ToList();
        var lowestPriceDifference = allProducts
            .Where(product => product.EffectivePriceDifference.HasValue)
            .Select(product => product.EffectivePriceDifference!.Value)
            .DefaultIfEmpty()
            .Min();
        var hasPriceDifference = allProducts.Any(product => product.EffectivePriceDifference.HasValue);

        var rows = products
            .Select(product => new EquivalentProductRow(
                product,
                hasPriceDifference && product.EffectivePriceDifference == lowestPriceDifference))
            .ToList();
        var current = new EquivalentProductRow(
            selected,
            hasPriceDifference && selected.EffectivePriceDifference == lowestPriceDifference);
        return (rows, current);
    }

    private static void UpdateHistoricalPriceDifferenceAvailability(ProductCatalogItem product)
    {
        foreach (var point in product.PriceHistory)
            point.IsPriceDifferenceUnavailable = false;

        if (!product.IsNotReimbursed || product.PriceHistory.Count == 0) return;

        var unavailableSince = product.ReimbursementUnavailableSince;
        var latestEffectiveDate = product.PriceHistory.Max(point => point.EffectiveDate.Date);
        foreach (var point in product.PriceHistory.Where(point => unavailableSince.HasValue
                     ? point.EffectiveDate.Date >= unavailableSince.Value
                     : point.EffectiveDate.Date == latestEffectiveDate))
            point.IsPriceDifferenceUnavailable = true;
    }

    private void OnIngredientExpandChanged(object sender, RoutedEventArgs e) => UpdateExpandableText();

    private void OnIndicationExpandChanged(object sender, RoutedEventArgs e) => UpdateExpandableText();

    private void UpdateExpandableText()
    {
        if (IngredientText is null || IndicationText is null) return;

        var ingredientExpanded = IngredientExpandToggle.IsChecked == true;
        IngredientText.MaxHeight = ingredientExpanded ? 10000 : 40;
        IngredientExpandToggle.ToolTip = ingredientExpanded ? "Etken maddeyi daralt" : "Etken maddenin tamamını göster";

        var indicationExpanded = IndicationExpandToggle.IsChecked == true;
        IndicationText.MaxHeight = indicationExpanded ? 10000 : 21;
        IndicationExpandToggle.ToolTip = indicationExpanded ? "Endikasyonu tek satıra daralt" : "Endikasyonun tamamını göster";
    }

    private void OnAnalysisCanvasSizeChanged(object sender, SizeChangedEventArgs e) => RenderAnalysisGraph();

    private async void OnSaveCurrentProductAnalysisMonthsClicked(object sender, RoutedEventArgs e)
    {
        if (selectedProduct is not { } product)
        {
            ShowAnalysisSaveStatus("Önce bir ilaç seçin.", isError: true);
            return;
        }

        if (!decimal.TryParse(
                AnalysisWantedMonthsBox.Text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var months) || !ProductAnalysisCalculations.IsValidWantedStockMonths(months))
        {
            ShowAnalysisSaveStatus("0,5 ile 60 arasında, 0,5'in katı bir değer girin.", isError: true);
            return;
        }

        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            await analysisRepository.SaveOverrideAsync(product.RecordId, months);
            var refreshed = await analysisRepository.LoadAsync(product.RecordId, product.Barcode);
            if (!ReferenceEquals(selectedProduct, product)) return;

            product.Analysis = refreshed;
            DetailHost.DataContext = null;
            DetailHost.DataContext = product;
            AnalysisWantedMonthsBox.Text = refreshed.WantedStockMonths.ToString("0.0");
            ConfigureAnalysisMovementSelectors(refreshed);
            ShowAnalysisSaveStatus("İlaca özel hedef kaydedildi.", isError: false);
            RenderAnalysisGraph();
        }
        catch (Exception exception)
        {
            ShowAnalysisSaveStatus($"Kaydedilemedi: {exception.Message}", isError: true);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ShowAnalysisSaveStatus(string message, bool isError)
    {
        AnalysisSaveStatusText.Text = message;
        AnalysisSaveStatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(180, 52, 52)
            : Color.FromRgb(35, 122, 87));
        AnalysisSaveStatusText.Visibility = Visibility.Visible;
    }

    private void ConfigureAnalysisMovementSelectors(ProductAnalysisData analysis)
    {
        var previousExitCode = (AnalysisExitTypeBox.SelectedItem as ProductAnalysisMovementType)?.Code;
        var previousEntryCode = (AnalysisEntryTypeBox.SelectedItem as ProductAnalysisMovementType)?.Code;
        var exitOptions = new[] { new ProductAnalysisMovementType(null, "All Issues", false) }
            .Concat(analysis.MovementTypes.Where(type => !type.IsEntry))
            .ToArray();
        var entryOptions = new[] { new ProductAnalysisMovementType(null, "All Receipts", true) }
            .Concat(analysis.MovementTypes.Where(type => type.IsEntry))
            .ToArray();

        suppressAnalysisMovementTypeChanges = true;
        try
        {
            AnalysisExitTypeBox.ItemsSource = exitOptions;
            AnalysisEntryTypeBox.ItemsSource = entryOptions;
            AnalysisExitTypeBox.SelectedItem = exitOptions.FirstOrDefault(type => type.Code == previousExitCode) ?? exitOptions[0];
            AnalysisEntryTypeBox.SelectedItem = entryOptions.FirstOrDefault(type => type.Code == previousEntryCode) ?? entryOptions[0];
        }
        finally
        {
            suppressAnalysisMovementTypeChanges = false;
        }
    }

    private void ClearAnalysisMovementSelectors()
    {
        suppressAnalysisMovementTypeChanges = true;
        try
        {
            AnalysisExitTypeBox.ItemsSource = null;
            AnalysisEntryTypeBox.ItemsSource = null;
        }
        finally
        {
            suppressAnalysisMovementTypeChanges = false;
        }
    }

    private void OnAnalysisMovementTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressAnalysisMovementTypeChanges) return;
        RenderAnalysisGraph();
    }

    private void RenderAnalysisGraph()
    {
        AnalysisCanvas.Children.Clear();
        analysisHoverElements.Clear();
        if (selectedProduct?.Analysis is not { } analysis || analysis.DailyMovements.Count == 0) return;

        var width = AnalysisCanvas.ActualWidth;
        var height = AnalysisCanvas.ActualHeight;
        if (width < 120 || height < 90) return;

        var exitType = AnalysisExitTypeBox.SelectedItem as ProductAnalysisMovementType;
        var entryType = AnalysisEntryTypeBox.SelectedItem as ProductAnalysisMovementType;
        var graphWidth = Math.Max(1, width - AnalysisGraphLeft - AnalysisGraphRight);
        var graphHeight = Math.Max(1, height - AnalysisGraphTop - AnalysisGraphBottom);
        var maximum = GetAnalysisGraphMaximum(analysis, exitType, entryType);

        for (var index = 0; index <= 4; index++)
        {
            var y = AnalysisGraphTop + graphHeight * index / 4;
            AnalysisCanvas.Children.Add(new Line
            {
                X1 = AnalysisGraphLeft,
                X2 = AnalysisGraphLeft + graphWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(224, 229, 233)),
                StrokeThickness = 1
            });
            var value = maximum * (4 - index) / 4m;
            AddAnalysisLabel(value.ToString("N0"), 3, y - 8);
        }

        AddAnalysisSeries(analysis.DailyMovements, point => analysis.QuantityFor(point, exitType),
            Color.FromRgb(23, 122, 114), graphWidth, graphHeight, maximum);
        AddAnalysisSeries(analysis.DailyMovements, point => analysis.QuantityFor(point, entryType),
            Color.FromRgb(66, 112, 156), graphWidth, graphHeight, maximum);

        var labelStep = width >= 760 ? 30 : 60;
        for (var index = 0; index < analysis.DailyMovements.Count; index += labelStep)
        {
            var x = AnalysisGraphLeft + graphWidth * index / Math.Max(1, analysis.DailyMovements.Count - 1);
            var label = new TextBlock
            {
                Text = analysis.DailyMovements[index].MonthDisplay,
                Foreground = new SolidColorBrush(Color.FromRgb(101, 113, 126)),
                FontSize = 10
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, AnalysisGraphTop + graphHeight + 9);
            AnalysisCanvas.Children.Add(label);
        }
    }

    private void AddAnalysisSeries(
        IReadOnlyList<ProductAnalysisMovementDay> days,
        Func<ProductAnalysisMovementDay, int> valueSelector,
        Color color,
        double graphWidth,
        double graphHeight,
        int maximum)
    {
        var stroke = new SolidColorBrush(color);
        var line = new Polyline { Stroke = stroke, StrokeThickness = 2.2 };
        for (var index = 0; index < days.Count; index++)
        {
            var x = AnalysisGraphLeft + graphWidth * index / Math.Max(1, days.Count - 1);
            var y = AnalysisGraphTop + graphHeight * (1 - (double)valueSelector(days[index]) / maximum);
            line.Points.Add(new Point(x, y));
        }
        AnalysisCanvas.Children.Add(line);
    }

    private static int GetAnalysisGraphMaximum(
        ProductAnalysisData analysis,
        ProductAnalysisMovementType? exitType,
        ProductAnalysisMovementType? entryType) => Math.Max(1, analysis.DailyMovements.Max(day =>
        Math.Max(analysis.QuantityFor(day, exitType), analysis.QuantityFor(day, entryType))));

    private void OnAnalysisCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (selectedProduct?.Analysis is not { } analysis || analysis.DailyMovements.Count == 0) return;
        var width = AnalysisCanvas.ActualWidth;
        var height = AnalysisCanvas.ActualHeight;
        var graphWidth = Math.Max(1, width - AnalysisGraphLeft - AnalysisGraphRight);
        var graphHeight = Math.Max(1, height - AnalysisGraphTop - AnalysisGraphBottom);
        var position = e.GetPosition(AnalysisCanvas);
        if (position.X < AnalysisGraphLeft || position.X > AnalysisGraphLeft + graphWidth ||
            position.Y < AnalysisGraphTop || position.Y > AnalysisGraphTop + graphHeight)
        {
            ClearAnalysisGraphHover();
            return;
        }

        var index = (int)Math.Round(
            (position.X - AnalysisGraphLeft) / graphWidth * (analysis.DailyMovements.Count - 1));
        index = Math.Clamp(index, 0, analysis.DailyMovements.Count - 1);
        var day = analysis.DailyMovements[index];
        var exitType = AnalysisExitTypeBox.SelectedItem as ProductAnalysisMovementType;
        var entryType = AnalysisEntryTypeBox.SelectedItem as ProductAnalysisMovementType;
        var exitQuantity = analysis.QuantityFor(day, exitType);
        var entryQuantity = analysis.QuantityFor(day, entryType);
        var maximum = GetAnalysisGraphMaximum(analysis, exitType, entryType);
        var x = AnalysisGraphLeft + graphWidth * index / Math.Max(1, analysis.DailyMovements.Count - 1);
        var exitY = AnalysisGraphTop + graphHeight * (1 - (double)exitQuantity / maximum);
        var entryY = AnalysisGraphTop + graphHeight * (1 - (double)entryQuantity / maximum);

        ClearAnalysisGraphHover();
        AddAnalysisHoverElement(new Line
        {
            X1 = x, X2 = x, Y1 = AnalysisGraphTop, Y2 = AnalysisGraphTop + graphHeight,
            Stroke = new SolidColorBrush(Color.FromRgb(137, 149, 158)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            IsHitTestVisible = false
        });
        AddAnalysisHoverMarker(x, exitY, Color.FromRgb(23, 122, 114));
        AddAnalysisHoverMarker(x, entryY, Color.FromRgb(66, 112, 156));

        var popup = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 38, 56, 75)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(87, 105, 119)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(9, 6, 9, 6),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = $"{day.Day:dd.MM.yyyy}\nÇıkış: {exitQuantity:N0}\nGiriş: {entryQuantity:N0}",
                Foreground = Brushes.White,
                FontSize = 11,
                LineHeight = 16
            }
        };
        popup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var popupLeft = x + 10;
        if (popupLeft + popup.DesiredSize.Width > width - 2) popupLeft = x - popup.DesiredSize.Width - 10;
        var popupTop = Math.Clamp(position.Y - popup.DesiredSize.Height / 2, 2, Math.Max(2, height - popup.DesiredSize.Height - 2));
        Canvas.SetLeft(popup, popupLeft);
        Canvas.SetTop(popup, popupTop);
        Panel.SetZIndex(popup, 30);
        AddAnalysisHoverElement(popup);
    }

    private void OnAnalysisCanvasMouseLeave(object sender, MouseEventArgs e) => ClearAnalysisGraphHover();

    private void AddAnalysisHoverMarker(double x, double y, Color color)
    {
        var marker = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.5,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(marker, x - 4.5);
        Canvas.SetTop(marker, y - 4.5);
        Panel.SetZIndex(marker, 25);
        AddAnalysisHoverElement(marker);
    }

    private void AddAnalysisHoverElement(UIElement element)
    {
        analysisHoverElements.Add(element);
        AnalysisCanvas.Children.Add(element);
    }

    private void ClearAnalysisGraphHover()
    {
        foreach (var element in analysisHoverElements) AnalysisCanvas.Children.Remove(element);
        analysisHoverElements.Clear();
    }

    private void AddAnalysisLabel(string text, double left, double top)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(101, 113, 126)),
            FontSize = 10
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        AnalysisCanvas.Children.Add(label);
    }

    private void OnPriceHistoryCanvasSizeChanged(object sender, SizeChangedEventArgs e) => RenderPriceHistoryGraph();

    private void OnVatGraphToggleChanged(object sender, RoutedEventArgs e) => RenderPriceHistoryGraph();

    private void RenderPriceHistoryGraph()
    {
        PriceHistoryCanvas.Children.Clear();
        if (selectedProduct is null) return;

        var history = selectedProduct.PriceHistory
            .OrderBy(point => point.EffectiveDate)
            .ToList();
        var values = history
            .SelectMany(point => new[] { point.SalePrice, point.WarehousePrice })
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        var width = PriceHistoryCanvas.ActualWidth;
        var height = PriceHistoryCanvas.ActualHeight;
        if (history.Count == 0 || values.Count == 0 || width < 120 || height < 100)
        {
            PriceHistoryCanvas.Children.Add(new TextBlock
            {
                Text = "Bu ürün için fiyat geçmişi bulunamadı.",
                Foreground = new SolidColorBrush(Color.FromRgb(111, 124, 135))
            });
            return;
        }

        var showVat = VatGraphToggle.IsChecked == true;
        VatLegend.Visibility = showVat ? Visibility.Visible : Visibility.Collapsed;
        const double left = 58;
        var right = showVat ? 54 : 18;
        const double top = 14;
        const double bottom = 32;
        var graphWidth = Math.Max(1, width - left - right);
        var graphHeight = Math.Max(1, height - top - bottom);
        var minimum = values.Min();
        var maximum = values.Max();
        if (maximum == minimum)
        {
            minimum = Math.Max(0, minimum - 1);
            maximum += 1;
        }

        for (var index = 0; index <= 4; index++)
        {
            var y = top + graphHeight * index / 4;
            PriceHistoryCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + graphWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(224, 229, 233)),
                StrokeThickness = 1
            });

            var labelValue = maximum - (maximum - minimum) * index / 4;
            AddAxisLabel(labelValue.ToString("N2"), 2, y - 7);
        }

        AddSeries(
            history,
            point => point.SalePrice,
            "Perakende Satış",
            selectedProduct.CurrencyDisplay,
            Color.FromRgb(23, 122, 114),
            left,
            top,
            graphWidth,
            graphHeight,
            minimum,
            maximum);
        AddSeries(
            history,
            point => point.WarehousePrice,
            "Depocu Satış",
            selectedProduct.CurrencyDisplay,
            Color.FromRgb(66, 112, 156),
            left,
            top,
            graphWidth,
            graphHeight,
            minimum,
            maximum);

        if (showVat)
            AddVatSeries(history, left, top, graphWidth, graphHeight);

        AddDateLabels(history, left, top + graphHeight + 7, graphWidth);
    }

    private void AddVatSeries(
        IReadOnlyList<PriceHistoryPoint> history,
        double left,
        double top,
        double graphWidth,
        double graphHeight)
    {
        var vatValues = history.Where(point => point.VatRate.HasValue).Select(point => point.VatRate!.Value).ToList();
        if (vatValues.Count == 0) return;

        const decimal minimumVat = 0;
        var maximumVat = Math.Max(1, vatValues.Max());
        for (var index = 0; index <= 4; index++)
        {
            var y = top + graphHeight * index / 4;
            var value = maximumVat - (maximumVat - minimumVat) * index / 4;
            AddAxisLabel($"{value:N1}%", left + graphWidth + 7, y - 7);
        }

        AddSeries(
            history,
            point => point.VatRate,
            "KDV",
            "%",
            Color.FromRgb(209, 126, 38),
            left,
            top,
            graphWidth,
            graphHeight,
            minimumVat,
            maximumVat,
            dashed: true);
    }

    private void AddSeries(
        IReadOnlyList<PriceHistoryPoint> history,
        Func<PriceHistoryPoint, decimal?> valueSelector,
        string seriesName,
        string unit,
        Color color,
        double left,
        double top,
        double graphWidth,
        double graphHeight,
        decimal minimum,
        decimal maximum,
        bool dashed = false)
    {
        var points = history
            .Select((point, index) => new { point, index, value = valueSelector(point) })
            .Where(entry => entry.value.HasValue)
            .ToList();
        if (points.Count == 0) return;

        var stroke = new SolidColorBrush(color);
        var polyline = new Polyline { Stroke = stroke, StrokeThickness = 2.2 };
        if (dashed) polyline.StrokeDashArray = new DoubleCollection { 4, 3 };

        foreach (var entry in points)
        {
            var x = left + graphWidth * entry.index / Math.Max(1, history.Count - 1);
            var ratio = (double)((entry.value!.Value - minimum) / (maximum - minimum));
            var y = top + graphHeight * (1 - ratio);
            polyline.Points.Add(new Point(x, y));
        }
        PriceHistoryCanvas.Children.Add(polyline);

        foreach (var entry in points)
        {
            var x = left + graphWidth * entry.index / Math.Max(1, history.Count - 1);
            var ratio = (double)((entry.value!.Value - minimum) / (maximum - minimum));
            var y = top + graphHeight * (1 - ratio);
            var marker = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.White,
                Stroke = stroke,
                StrokeThickness = 2,
                ToolTip = $"{seriesName}\n{entry.point.EffectiveDate:dd.MM.yyyy}\n{entry.value:N2} {unit}"
            };
            Canvas.SetLeft(marker, x - 4);
            Canvas.SetTop(marker, y - 4);
            PriceHistoryCanvas.Children.Add(marker);
        }
    }

    private void AddDateLabels(
        IReadOnlyList<PriceHistoryPoint> history,
        double left,
        double y,
        double graphWidth)
    {
        var labelCount = Math.Clamp((int)(graphWidth / 82) + 1, 1, history.Count);
        var indices = labelCount == 1
            ? new[] { 0 }
            : Enumerable.Range(0, labelCount)
                .Select(index => (int)Math.Round(index * (history.Count - 1d) / (labelCount - 1)))
                .Distinct()
                .ToArray();

        foreach (var historyIndex in indices)
        {
            var x = left + graphWidth * historyIndex / Math.Max(1, history.Count - 1);
            AddDateLabel(history[historyIndex].EffectiveDate, x, y, historyIndex == 0, historyIndex == history.Count - 1);
        }
    }

    private void AddDateLabel(DateTime date, double x, double y, bool alignLeft, bool alignRight)
    {
        var label = new TextBlock
        {
            Text = date.ToString("dd.MM.yy"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(105, 117, 127))
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelX = alignLeft ? x : alignRight ? x - label.DesiredSize.Width : x - label.DesiredSize.Width / 2;
        Canvas.SetLeft(label, labelX);
        Canvas.SetTop(label, y);
        PriceHistoryCanvas.Children.Add(label);
    }

    private void AddAxisLabel(string text, double x, double y)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(105, 117, 127))
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        PriceHistoryCanvas.Children.Add(label);
    }
    private async void OnHeaderFilterClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field } || selectedProduct is null) return;

        var (label, value, openEquivalentTab) = field switch
        {
            "ProductPrefix" => ("Ürün Ön Eki", selectedProduct.ProductPrefix, false),
            "PharmaceuticalForm" => ("Farmasötik Şekil", selectedProduct.PharmaceuticalForm, false),
            "SgkCode" => ("SGK Kodu", selectedProduct.SgkCode, false),
            "AtcCode" => ("ATC Kodu", selectedProduct.AtcCode, false),
            "GenericCode" => ("Jenerik Kod", selectedProduct.GenericCode, false),
            "CompanyName" => ("Firma", selectedProduct.CompanyName, false),
            "BrandName" => ("Marka Adı", selectedProduct.BrandName, false),
            "EquivalentGroupCode" => ("Eşdeğer Kod", selectedProduct.EquivalentGroupCode, true),
            _ => (string.Empty, null, false)
        };

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value)) return;

        ResetStandardFilters();
        ResetIgnoredFilterFallback();
        var normalizedValue = value.Trim();
        headerFilterField = field;
        headerFilterValue = normalizedValue;
        ActiveHeaderFilterText.Text = $"{label}: {normalizedValue}";
        ActiveHeaderFilterChip.Visibility = Visibility.Visible;
        await ResetAndLoadAsync();

        if (openEquivalentTab)
            CatalogTabs.SelectedItem = EquivalentTab;
    }

    private async void OnClearHeaderFilterClicked(object sender, RoutedEventArgs e)
    {
        headerFilterField = null;
        headerFilterValue = null;
        ResetIgnoredFilterFallback();
        await ResetAndLoadAsync();
    }

    private void UpdateFilterChipPresentation()
    {
        var productPrefix = ProductPrefixFilter.SelectedItem as string;
        var ingredient = IngredientFilter.Text.Trim();
        var prescription = PrescriptionFilter.SelectedItem as string;
        SetFilterChip(ProductPrefixFilterChip, ProductPrefixFilterChipText, "Ürün Ön Eki", productPrefix);
        SetFilterChip(IngredientFilterChip, IngredientFilterChipText, "Etken Madde", ingredient);
        SetFilterChip(PrescriptionFilterChip, PrescriptionFilterChipText, "Reçete Türü", prescription);
        ActiveHeaderFilterChip.Visibility =
            !string.IsNullOrWhiteSpace(headerFilterField) && !string.IsNullOrWhiteSpace(headerFilterValue)
                ? Visibility.Visible
                : Visibility.Collapsed;
        IgnoredFiltersSearchChip.Visibility = isIgnoringFiltersForSearch ? Visibility.Visible : Visibility.Collapsed;

        if (!isIgnoringFiltersForSearch) return;
        ActiveHeaderFilterChip.Visibility = Visibility.Collapsed;
        ProductPrefixFilterChip.Visibility = Visibility.Collapsed;
        IngredientFilterChip.Visibility = Visibility.Collapsed;
        PrescriptionFilterChip.Visibility = Visibility.Collapsed;
    }

    private static void SetFilterChip(Border chip, TextBlock text, string label, string? value)
    {
        var hasValue = !string.IsNullOrWhiteSpace(value);
        chip.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        text.Text = hasValue ? $"{label}: {value!.Trim()}" : string.Empty;
    }

    private async void OnClearStandardFilterClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter }) return;

        suppressFilterChanges = true;
        try
        {
            switch (filter)
            {
                case "ProductPrefix":
                    ProductPrefixFilter.SelectedIndex = 0;
                    break;
                case "Ingredient":
                    IngredientFilter.SelectedIndex = 0;
                    IngredientFilter.Text = string.Empty;
                    break;
                case "Prescription":
                    PrescriptionFilter.SelectedIndex = 0;
                    break;
                default:
                    return;
            }
        }
        finally
        {
            suppressFilterChanges = false;
        }

        ResetIgnoredFilterFallback();
        await ResetAndLoadAsync();
    }

    private void ResetStandardFilters()
    {
        suppressFilterChanges = true;
        try
        {
            SearchBox.Clear();
            ProductPrefixFilter.SelectedIndex = 0;
            IngredientFilter.SelectedIndex = 0;
            IngredientFilter.Text = string.Empty;
            PrescriptionFilter.SelectedIndex = 0;
            OnlyOnMarketToggle.IsChecked = DefaultOnlyOnMarket;
            OnlyInStockToggle.IsChecked = false;
            OnlyWithMovementsToggle.IsChecked = true;
            StockRangeToggle.IsChecked = false;
            StockMinimumFilter.Clear();
            StockMaximumFilter.Clear();
        }
        finally
        {
            suppressFilterChanges = false;
        }
    }

    private async void OnEquivalentDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        var clickedRow = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (clickedRow?.Item is not EquivalentProductRow row) return;
        e.Handled = true;

        await NavigateToProductAsync(row.Product);
    }

    private void EnsureSelectedDetailTabIsAvailable(ProductCatalogItem product)
    {
        var selectedTabIsUnavailable =
            (ReferenceEquals(CatalogTabs.SelectedItem, KubTab) && !product.HasKubDocument) ||
            (ReferenceEquals(CatalogTabs.SelectedItem, KitTab) && !product.HasKitDocument) ||
            (ReferenceEquals(CatalogTabs.SelectedItem, SutTab) && !product.HasSutContent);
        if (selectedTabIsUnavailable)
            CatalogTabs.SelectedItem = GeneralTab;
    }

    private async Task NavigateToProductAsync(ProductCatalogItem product)
    {
        AddNavigationEntry(product);
        navigatingHistory = true;
        try
        {
            headerFilterField = null;
            headerFilterValue = null;
            ActiveHeaderFilterChip.Visibility = Visibility.Collapsed;
            ResetIgnoredFilterFallback();
            ResetStandardFilters();
            await ResetAndLoadAsync();
            var target = visibleProducts.FirstOrDefault(candidate => candidate.RecordId == product.RecordId);
            if (target is null)
            {
                visibleProducts.Insert(0, product);
                target = product;
            }
            CatalogTabs.SelectedItem = GeneralTab;
            ProductList.SelectedItem = target;
            ProductList.ScrollIntoView(target);
            CatalogTabs.SelectedItem = GeneralTab;
            GeneralTab.IsSelected = true;
            _ = Dispatcher.BeginInvoke(() =>
            {
                CatalogTabs.SelectedItem = GeneralTab;
                GeneralTab.IsSelected = true;
            }, DispatcherPriority.ContextIdle);
        }
        finally
        {
            navigatingHistory = false;
            UpdateNavigationButtons();
        }
    }

    private async void OnToggleSidebarClicked(object sender, RoutedEventArgs e)
    {
        sidebarVisible = !sidebarVisible;
        detailOnly = !sidebarVisible && selectedProduct is not null;
        SidebarHost.Width = sidebarVisible ? SidebarExpandedWidth : 0;
        SidebarHost.Visibility = sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = sidebarVisible ? GridLength.Auto : new GridLength(0);
        SidebarSeparator.Visibility = sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggleIcon.Data = Geometry.Parse(sidebarVisible
            ? "M2,2 L14,2 L14,14 L2,14 Z M6,2 L6,14"
            : "M2,2 L14,2 L14,14 L2,14 Z M10,2 L10,14");
        DetachDetailButton.Visibility = detailOnly ? Visibility.Collapsed : Visibility.Visible;
        UpdateSidebarToggleVisibility();
        ModuleTitleChanged?.Invoke(CurrentModuleTitle);
        ModuleIdentityChanged?.Invoke(CurrentModuleId);
        if (sidebarVisible && !filtersInitialized)
            await InitializeAndLoadAsync();
    }

    private void AddNavigationEntry(ProductCatalogItem product)
    {
        if (navigationIndex >= 0 && navigationIndex < navigationHistory.Count &&
            navigationHistory[navigationIndex].RecordId == product.RecordId)
            return;
        if (navigationIndex + 1 < navigationHistory.Count)
            navigationHistory.RemoveRange(navigationIndex + 1, navigationHistory.Count - navigationIndex - 1);
        if (navigationHistory.Count == 0 || navigationHistory[^1].RecordId != product.RecordId)
            navigationHistory.Add(product);
        navigationIndex = navigationHistory.Count - 1;
    }
    private void UpdateNavigationButtons()
    {
        var hasSelection = selectedProduct is not null;
        UndoButton.IsEnabled = hasSelection && navigationIndex > 0;
        RedoButton.IsEnabled = hasSelection && navigationIndex >= 0 && navigationIndex < navigationHistory.Count - 1;
    }

    private void OnUndoClicked(object sender, RoutedEventArgs e) => NavigateHistory(-1);
    private void OnRedoClicked(object sender, RoutedEventArgs e) => NavigateHistory(1);

    private void NavigateHistory(int delta)
    {
        var next = navigationIndex + delta;
        if (selectedProduct is null || next < 0 || next >= navigationHistory.Count) return;
        navigationIndex = next;
        var product = navigationHistory[next];
        var target = visibleProducts.FirstOrDefault(item => item.RecordId == product.RecordId);
        if (target is null)
        {
            visibleProducts.Insert(0, product);
            target = product;
        }
        navigatingHistory = true;
        ProductList.SelectedItem = target;
        navigatingHistory = false;
        CatalogTabs.SelectedItem = GeneralTab;
        UpdateNavigationButtons();
    }

    private void OnDetachDetailClicked(object sender, RoutedEventArgs e)
    {
        if (selectedProduct is null || detailOnly) return;
        RequestProductPanel(selectedProduct, ModulePanelPlacement.Right);
        ClearAttachedSelection();
    }

    private void RequestProductPanel(ProductCatalogItem product, ModulePanelPlacement placement, string? anchorModuleId = null)
    {
        var historySnapshot = navigationHistory.ToArray();
        var historyIndexSnapshot = navigationIndex;
        PanelRequested?.Invoke(new ModulePanelRequest(
            $"products.detail.{product.RecordId}",
            product.DisplayName,
            () => new ProductCatalogView(new ProductCatalogLaunchOptions(
                InitialProduct: product,
                NavigationHistory: historySnapshot,
                NavigationIndex: historyIndexSnapshot), priceRefreshConnector),
            610,
            540,
            placement,
            anchorModuleId));
    }

    private async void OnCompareProductMenuItemClicked(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        var product = GetContextProduct(menuItem);
        if (product is null) return;
        var actionMenu = menuItem is null ? null : ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
        var placementTarget = actionMenu?.PlacementTarget as FrameworkElement;
        var openedFromList = FindVisualAncestor<ListBox>(placementTarget) == ProductList;
        if (!openedFromList)
        {
            pendingListComparisonBase = null;
            if (selectedProduct?.RecordId == product.RecordId) return;
            RequestProductPanel(product, ModulePanelPlacement.Bottom, CurrentModuleId);
            return;
        }
        var comparisonBase = openedFromList ? pendingListComparisonBase ?? selectedProduct : selectedProduct;
        pendingListComparisonBase = null;
        if (comparisonBase is null)
        {
            await NavigateToProductAsync(product);
            return;
        }
        if (comparisonBase.RecordId == product.RecordId) return;
        var anchorId = $"products.detail.{comparisonBase.RecordId}";
        RequestProductPanel(comparisonBase, ModulePanelPlacement.Right);
        RequestProductPanel(product, ModulePanelPlacement.Bottom, anchorId);
        ClearAttachedSelection();
    }

    private void OnProductListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var itemContainer = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (itemContainer?.DataContext is not ProductCatalogItem item) return;
        pendingListComparisonBase = selectedProduct?.RecordId != item.RecordId
            ? selectedProduct
            : null;
        if (itemContainer.ContextMenu is { } actionMenu)
        {
            actionMenu.PlacementTarget = itemContainer;
            actionMenu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void OnCopyValueClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value })
            TrySetClipboardText(value);
    }

    private static ProductCatalogItem? GetContextProduct(MenuItem? menuItem)
    {
        var actionMenu = menuItem is null ? null : ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
        return (actionMenu?.PlacementTarget as FrameworkElement)?.DataContext switch
        {
            ProductCatalogItem item => item,
            EquivalentProductRow row => row.Product,
            _ => null
        };
    }

    private void ClearAttachedSelection()
    {
        ProductList.SelectedItem = null;
        selectedProduct = null;
        navigationHistory.Clear();
        navigationIndex = -1;
        UpdateNavigationButtons();
    }

    private void OnProductScrollMouseDown(object sender, MouseButtonEventArgs e)
    {
        scrollThumbDragging = FindVisualAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null;
    }

    private async void OnProductScrollMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!scrollThumbDragging) return;
        scrollThumbDragging = false;
        if (filtersInitialized && !isLoadingPage && hasMore && pageCancellation is not null &&
            ShouldLoadNextPage(lastScrollOffset, lastScrollViewport, lastScrollExtent))
            await LoadNextPageAsync(pageCancellation.Token);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement contentElement => contentElement.Parent ?? ContentOperations.GetParent(contentElement),
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return null;
    }

    public void SetHostedInPopout(bool isPopout)
    {
        UndoButton.Visibility = isPopout ? Visibility.Collapsed : Visibility.Visible;
        RedoButton.Visibility = isPopout ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetMedicationListAvailableInCurrentTab(bool isAvailable)
    {
        medicationListAvailableInCurrentTab = isAvailable;
        UpdateSidebarToggleVisibility();
    }

    private void UpdateSidebarToggleVisibility()
    {
        SidebarToggleButton.Visibility = detailOnly && medicationListAvailableInCurrentTab
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void OnRetryClicked(object sender, RoutedEventArgs e) => await InitializeAndLoadAsync();

    private sealed class EquivalentProductRow
    {
        private readonly ProductCatalogItem product;

        public EquivalentProductRow(ProductCatalogItem product, bool isLowestPrice)
        {
            this.product = product;
            IsLowestPrice = isLowestPrice;
        }

        public ProductCatalogItem Product => product;
        public string DisplayName => product.DisplayName;
        public string PriceDifference => product.PriceDifferenceDisplay;
        public decimal PriceDifferenceValue => product.EffectivePriceDifference ?? decimal.MaxValue;
        public string StockQuantity => product.StockQuantityDisplay;
        public string InstantOrderPrice => product.PurchasePriceOnlyDisplay;
        public string CampaignOrderPrice => product.CampaignPurchasePriceDisplay;
        public Brush CampaignBrush => product.HasActiveCampaign ? Brushes.SeaGreen : new SolidColorBrush(Color.FromRgb(80, 92, 102));
        public bool IsLowestPrice { get; }
    }
}
