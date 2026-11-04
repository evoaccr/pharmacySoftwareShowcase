using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.StockMovements;
using pharmacyApp;

namespace pharmacyApp.Modules.Sales;

public partial class RetailSaleView : UserControl, IModuleTabRequestSource
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly RetailSaleRepository repository = new();
    private readonly ConsignmentSaleRepository consignmentRepository = new();
    private readonly RetailItsOrchestrationService itsOrchestrator = new(
        new ItsApiClient(),
        new RetailItsOperationRepository(),
        ReadItsConfiguration);
    private readonly RetailSaleCart cart = new();
    private readonly RetailSaleLaunchOptions launchOptions;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? patientSearchCancellation;
    private PatientSuggestion? selectedPatient;
    private RetailSaleItem? selectedSummaryItem;
    private bool suppressPatientSearch;
    private bool navigatingPatientSuggestions;
    private bool operationInProgress;
    private bool suppressPaymentChanges;
    private bool paymentControlsReady;
    private IReadOnlyDictionary<RetailPaymentKind, decimal> consignmentOriginalPayments =
        new Dictionary<RetailPaymentKind, decimal>();
    private decimal consignmentCollectionTarget;
    private bool manualDiscounts;
    private bool suppressDiscountPercent;
    private bool itsCompletionBlocked;
    private bool launchInitialized;

    public event Action<ModuleTabRequest>? TabRequested;
    public event Action<int>? SaleCompleted;

    public RetailSaleView() : this(new RetailSaleLaunchOptions())
    {
    }

    public RetailSaleView(RetailSaleLaunchOptions launchOptions)
    {
        this.launchOptions = launchOptions;
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        SaleGrid.ItemsSource = cart.Groups;
        cart.Groups.CollectionChanged += OnCartGroupsChanged;
        paymentControlsReady = true;
        ConfigureLaunchMode();
        UpdatePaymentState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (launchOptions.Mode == RetailSaleMode.ConsignmentSettlement && !launchInitialized)
        {
            launchInitialized = true;
            await LoadConsignmentAsync();
            return;
        }
        QrCodeInput.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancellationTokenSourceGuard.CancelAndDispose(ref operationCancellation);
        CancellationTokenSourceGuard.CancelAndDispose(ref patientSearchCancellation);
    }

    private async void OnAddItemClicked(object sender, RoutedEventArgs e) => await AddEnteredItemAsync();

    private async void OnQrCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddEnteredItemAsync();
    }

    private async Task AddEnteredItemAsync()
    {
        var code = QrCodeInput.Text.Trim();
        if (code.Length == 0 || operationInProgress) return;

        try
        {
            BeginOperation();
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = string.Empty;
            var item = await repository.ResolveItemAsync(code, operationCancellation!.Token);
            var tableCheck = RetailSaleTableRules.CheckCanAdd(cart, item, launchOptions.Mode);
            if (!tableCheck.CanAdd)
            {
                if (tableCheck.IssueCode == RetailSaleTableIssueCodes.NotInStock)
                    ShowTemporaryActionDialog(
                        tableCheck.Message!,
                        "İlacı Üzerime Al",
                        "İlaç kabul işlemi henüz uygulanmadı.");
                else if (tableCheck.IssueCode == RetailSaleTableIssueCodes.Consigned)
                    ShowTemporaryActionDialog(
                        tableCheck.Message!,
                        "Emanete git",
                        "Emanet ekranı bağlantısı henüz uygulanmadı.");
                else
                    StatusText.Text = tableCheck.Message;
                return;
            }
            if (item is null) return;

            cart.Add(item);
            var group = cart.Groups.First(candidate => candidate.Barcode == item.Barcode);
            SaleGrid.SelectedItem = group;
            selectedSummaryItem = item;
            UpdateSummary(item);
            RefreshCartUi();
            QrCodeInput.Clear();
            QrCodeInput.Focus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Ürün eklenemedi: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnCartGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshCartUi();

    private void RefreshCartUi()
    {
        if (DiscountPaymentCheckBox.IsChecked == true && !manualDiscounts)
            cart.AllocateDiscount(DiscountPaymentBox.Amount);
        else if (DiscountPaymentCheckBox.IsChecked != true)
            cart.AllocateDiscount(0m);
        cart.SetManualDiscountEditing(manualDiscounts);
        SaleGrid.Items.Refresh();
        TotalText.Text = $"Toplam: {cart.TotalAmount.ToString("N2", TurkishCulture)} ₺";
        RebalancePayments();
        if (cart.ItemCount == 0)
        {
            selectedSummaryItem = null;
            ClearSummary();
        }
    }

    private void OnSaleGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SaleGrid.SelectedItem is not RetailSaleGroup group || group.Entries.Count == 0) return;
        selectedSummaryItem = group.Entries[0].Item;
        UpdateSummary(selectedSummaryItem);
    }

    private void OnSaleGroupExpandClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: RetailSaleGroup group } toggle) return;
        group.IsExpanded = toggle.IsChecked == true;
        if (SaleGrid.ItemContainerGenerator.ContainerFromItem(group) is DataGridRow row)
            row.DetailsVisibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnSaleEntrySelected(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailSaleEntry entry) return;
        selectedSummaryItem = entry.Item;
        SaleGrid.SelectedItem = cart.Groups.FirstOrDefault(group => group.Entries.Contains(entry));
        UpdateSummary(entry.Item);
    }

    private void OnSaleGridRightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null and not DataGridRow)
            current = VisualTreeHelper.GetParent(current);
        if (current is DataGridRow row) row.IsSelected = true;
    }

    private void OnSaleGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DependencyObject? current = SaleGrid.InputHitTest(Mouse.GetPosition(SaleGrid)) as DependencyObject;
        while (current is not null and not DataGridRow)
            current = VisualTreeHelper.GetParent(current);
        if (current is not DataGridRow row)
        {
            e.Handled = true;
            return;
        }

        row.IsSelected = true;
    }

    private void OnRemoveGroupClicked(object sender, RoutedEventArgs e)
    {
        if (launchOptions.Mode == RetailSaleMode.ConsignmentSettlement) return;
        var group = (sender as FrameworkElement)?.DataContext as RetailSaleGroup
            ?? SaleGrid.SelectedItem as RetailSaleGroup;
        if (group is null) return;
        cart.RemoveGroup(group);
        SelectFirstRemainingItem();
    }

    private void OnRemoveEntryClicked(object sender, RoutedEventArgs e)
    {
        if (launchOptions.Mode == RetailSaleMode.ConsignmentSettlement) return;
        if ((sender as FrameworkElement)?.DataContext is not RetailSaleEntry entry) return;
        cart.RemoveEntry(entry);
        SelectFirstRemainingItem();
    }

    private void SelectFirstRemainingItem()
    {
        RefreshCartUi();
        var firstGroup = cart.Groups.FirstOrDefault();
        SaleGrid.SelectedItem = firstGroup;
        if (firstGroup?.Entries.FirstOrDefault() is { } firstEntry)
        {
            selectedSummaryItem = firstEntry.Item;
            UpdateSummary(firstEntry.Item);
        }
    }

    private async void OnOpenGroupProductClicked(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as RetailSaleGroup
            ?? SaleGrid.SelectedItem as RetailSaleGroup;
        if (group is not null)
            await OpenProductPopupAsync(group.ProductRecordId, ProductCatalogInitialTab.General);
    }

    private async void OnOpenEntryProductClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RetailSaleEntry entry)
            await OpenProductPopupAsync(entry.Item.ProductRecordId, ProductCatalogInitialTab.General);
    }

    private async void OnOpenEquivalentsClicked(object sender, RoutedEventArgs e)
    {
        if (selectedSummaryItem is not null)
            await OpenProductPopupAsync(selectedSummaryItem.ProductRecordId, ProductCatalogInitialTab.Equivalent);
    }

    private async Task OpenProductPopupAsync(int productRecordId, ProductCatalogInitialTab initialTab)
    {
        try
        {
            BeginOperation();
            StatusText.Text = string.Empty;
            var product = await new ProductCatalogRepository().LoadByRecordIdAsync(
                productRecordId,
                operationCancellation!.Token);
            if (product is null)
            {
                StatusText.Text = "Ürün bilgisi bulunamadı.";
                return;
            }

            var detailView = new ProductCatalogView(new ProductCatalogLaunchOptions(
                InitialProduct: product,
                InitialTab: initialTab));
            var window = new Window
            {
                Title = product.DisplayName,
                Content = detailView,
                Width = 1120,
                Height = 780,
                MinWidth = 760,
                MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ShowInTaskbar = false
            };
            window.Show();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Ürün bilgisi açılamadı: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnPatientSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        PatientPlaceholder.Visibility = string.IsNullOrEmpty(PatientSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (suppressPatientSearch) return;

        selectedPatient = null;
        EditPatientButton.IsEnabled = false;
        UpdateDebtAvailability();
        PatientSuggestionPopup.IsOpen = false;
        PatientSuggestionList.ItemsSource = null;
        patientSearchCancellation?.Cancel();
        patientSearchCancellation?.Dispose();
        patientSearchCancellation = new CancellationTokenSource();
        var term = PatientSearchBox.Text.Trim();
        if (term.Length < 3)
        {
            PatientSuggestionPopup.IsOpen = false;
            PatientSuggestionList.ItemsSource = null;
            return;
        }

        try
        {
            var token = patientSearchCancellation.Token;
            await Task.Delay(220, token);
            var suggestions = await repository.SearchPatientsAsync(term, token);
            PatientSuggestionList.ItemsSource = suggestions;
            PatientSuggestionPopup.IsOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Hasta araması yapılamadı: {exception.Message}";
        }
    }

    private void OnPatientSearchFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (PatientSuggestionList.Items.Count > 0 && selectedPatient is null)
            PatientSuggestionPopup.IsOpen = true;
    }

    private async void OnPatientSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var term = PatientSearchBox.Text.Trim();
            if (RetailPatientSearchRules.IsCompleteTcIdentityNumber(term))
            {
                e.Handled = true;
                await SelectOrCreatePatientByTcAsync(term);
                return;
            }
            if (PatientSuggestionList.Items.Count == 1
                && PatientSuggestionList.Items[0] is PatientSuggestion onlySuggestion)
            {
                CommitPatientSuggestion(onlySuggestion);
                e.Handled = true;
            }
            return;
        }
        if (e.Key is not (Key.Down or Key.Up) || PatientSuggestionList.Items.Count == 0) return;
        PatientSuggestionPopup.IsOpen = true;
        MovePatientSuggestion(e.Key == Key.Down ? 0 : PatientSuggestionList.Items.Count - 1);
        PatientSuggestionList.Focus();
        e.Handled = true;
    }

    private async Task SelectOrCreatePatientByTcAsync(string tcIdentityNumber)
    {
        CancellationTokenSourceGuard.CancelAndDispose(ref patientSearchCancellation);
        patientSearchCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = string.Empty;
            var patient = await repository.LoadPatientByTcAsync(
                tcIdentityNumber,
                patientSearchCancellation.Token);
            if (patient is not null)
                CommitPatientSuggestion(patient);
            else
                OpenNewPatient(tcIdentityNumber);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Hasta seçilemedi: {exception.Message}";
        }
    }

    private void OnPatientSuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            var current = Math.Max(0, PatientSuggestionList.SelectedIndex);
            MovePatientSuggestion(Math.Clamp(current + (e.Key == Key.Down ? 1 : -1), 0, PatientSuggestionList.Items.Count - 1));
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && PatientSuggestionList.SelectedItem is PatientSuggestion patient)
        {
            CommitPatientSuggestion(patient);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            PatientSuggestionPopup.IsOpen = false;
            PatientSearchBox.Focus();
            e.Handled = true;
        }
    }

    private void MovePatientSuggestion(int index)
    {
        navigatingPatientSuggestions = true;
        PatientSuggestionList.SelectedIndex = index;
        PatientSuggestionList.ScrollIntoView(PatientSuggestionList.SelectedItem);
        navigatingPatientSuggestions = false;
    }

    private void OnPatientSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (navigatingPatientSuggestions) return;
        if (PatientSuggestionList.SelectedItem is not PatientSuggestion patient) return;
        CommitPatientSuggestion(patient);
    }

    private void CommitPatientSuggestion(PatientSuggestion patient)
    {
        SetSelectedPatient(patient);
        PatientSuggestionPopup.IsOpen = false;
        PatientSuggestionList.SelectedItem = null;
        PatientSearchBox.Focus();
    }

    private void SetSelectedPatient(PatientSuggestion patient)
    {
        selectedPatient = patient;
        suppressPatientSearch = true;
        PatientSearchBox.Text = patient.DisplayText;
        PatientSearchBox.CaretIndex = PatientSearchBox.Text.Length;
        suppressPatientSearch = false;
        PatientPlaceholder.Visibility = Visibility.Collapsed;
        EditPatientButton.IsEnabled = launchOptions.Mode != RetailSaleMode.ConsignmentSettlement;
        UpdateCustomerSummary(patient);
        UpdateDebtAvailability();
        if (launchOptions.Mode == RetailSaleMode.ConsignmentCreate && GetSelectedPaymentKinds().Count == 0)
            DebtPaymentCheckBox.IsChecked = true;
    }

    private void OnAddPatientClicked(object sender, RoutedEventArgs e)
        => OpenNewPatient();

    private void OpenNewPatient(string? initialTcIdentityNumber = null)
    {
        PatientSuggestion? savedPatient = null;
        IReadOnlyDictionary<string, object?>? initialValues = initialTcIdentityNumber is null
            ? null
            : new Dictionary<string, object?> { ["tc_identity_no"] = initialTcIdentityNumber };
        var editor = new CardEditorView(
            CardDefinitions.Patients,
            null,
            null,
            () => { },
            initialAddValues: initialValues);
        var window = CreatePatientWindow(editor, "Hasta Ekle");
        editor.RecordSaved += (recordId, displayName) =>
        {
            if (int.TryParse(recordId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var patientId))
                savedPatient = new PatientSuggestion(patientId, string.Empty, displayName);
        };
        editor.CloseRequested += () => window.Close();
        window.ShowDialog();
        if (savedPatient is not null) _ = RefreshSelectedPatientAsync(savedPatient.RecordId);
    }

    private void OnEditPatientClicked(object sender, RoutedEventArgs e)
    {
        if (selectedPatient is null) return;
        var patient = selectedPatient;
        var editor = new CardEditorView(
            CardDefinitions.Patients,
            patient.RecordId.ToString(CultureInfo.InvariantCulture),
            patient.DisplayName,
            () => _ = RefreshSelectedPatientAsync(patient.RecordId),
            startEditing: false);
        var window = CreatePatientWindow(editor, patient.DisplayName);
        editor.ModuleTitleChanged += title => window.Title = title;
        editor.RecordDeleted += deletedKey =>
        {
            if (deletedKey == patient.RecordId.ToString(CultureInfo.InvariantCulture)) ClearSelectedPatient();
        };
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

    private async Task RefreshSelectedPatientAsync(int recordId)
    {
        try
        {
            var patient = await repository.LoadPatientAsync(recordId, CancellationToken.None);
            if (patient is not null) SetSelectedPatient(patient);
            else if (selectedPatient?.RecordId == recordId) ClearSelectedPatient();
        }
        catch
        {
            // The editor already reports save errors; keep the last selected patient if refresh fails.
        }
    }

    private async void OnCompleteSaleClicked(object sender, RoutedEventArgs e)
    {
        if (cart.ItemCount == 0 || operationInProgress) return;
        try
        {
            if (launchOptions.Mode != RetailSaleMode.Retail && selectedPatient is null)
                throw new InvalidOperationException("Emanet satış için hasta seçilmelidir.");
            BeginOperation();
            var lines = cart.Groups.SelectMany(group => group.Entries)
                .Select(entry => new RetailSaleLineInput(entry.Item, entry.DiscountAmount))
                .ToArray();
            var patientId = selectedPatient?.RecordId;
            var payments = BuildPaymentInputs();
            var withDiscount = DiscountPaymentCheckBox.IsChecked == true;
            var notes = SaleNotesBox.Text;
            var userId = (Application.Current as App)?.CurrentUser?.UserId ?? "admin";
            if (launchOptions.Mode == RetailSaleMode.ConsignmentCreate)
            {
                StatusText.Text = "Emanet satış kaydediliyor...";
                var consignmentId = await consignmentRepository.CreateAsync(
                    lines, patientId!.Value, payments, withDiscount, notes, userId, operationCancellation!.Token);
                CompleteSuccessfulOperation(consignmentId, "Emanet satış başarıyla oluşturuldu.", "Emanet Satış");
                return;
            }

            StatusText.Text = "İTS bildirimi gönderiliyor ve satış kaydediliyor...";
            if (launchOptions.Mode == RetailSaleMode.ConsignmentSettlement)
            {
                var consignmentId = launchOptions.ConsignmentSaleId
                    ?? throw new InvalidOperationException("Emanet satış numarası bulunamadı.");
                var settlement = await itsOrchestrator.ExecuteAsync(
                    RetailItsOperationKind.Sale,
                    lines.Select(line => line.Item.EnteredCode).ToArray(),
                    userId,
                    token => consignmentRepository.CompleteRetailSaleAsync(
                        consignmentId, lines, payments, notes, userId, token),
                    operationCancellation!.Token);
                CompleteSuccessfulOperation(
                    settlement.LocalRecordId,
                    "Emanet perakende satışa dönüştürüldü.",
                    "Emanet Satış",
                    settlement.ItsNotificationSent ? settlement.NotificationId : null);
                return;
            }

            var sendItsNotification = SendItsNotificationCheckBox.IsChecked == true;
            RetailItsCompletionResult? completion = null;
            int saleId;
            if (sendItsNotification)
            {
                var qrs = lines.Where(line => !line.Item.IsBarcodeOnly).Select(line => line.Item.EnteredCode).ToArray();
                await new UnnotifiedItsMovementRepository().ValidateBeforeActionAsync(
                    qrs, StockMovementDirection.Out, true, operationCancellation!.Token);
                completion = await itsOrchestrator.ExecuteAsync(
                    RetailItsOperationKind.Sale, qrs, userId,
                    token => repository.CompleteSaleAsync(lines, patientId, payments, withDiscount, notes, userId, true, token),
                    operationCancellation.Token);
                saleId = completion.LocalRecordId;
            }
            else
            {
                StatusText.Text = "Satış yerel stoka kaydediliyor; İTS bildirimi bekleyen harekete alınacak...";
                saleId = await repository.CompleteSaleAsync(
                    lines, patientId, payments, withDiscount, notes, userId, false, operationCancellation!.Token);
            }
            cart.Clear();
            ClearSelectedPatient();
            ResetPayments();
            SaleNotesBox.Clear();
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 125, 78));
            var itsText = completion?.ItsNotificationSent == true
                ? $" İTS bildirim no: {completion.NotificationId ?? "—"}."
                : " İTS bildirimi yapılmadı; Debug ekranından düzeltilebilir.";
            StatusText.Text = $"Satış başarıyla tamamlandı. Satış no: {saleId}.{itsText}";
            pharmacyApp.Localization.EnglishMessageBox.Show(
                $"Satış başarıyla tamamlandı.\nSatış no: {saleId}" +
                (completion?.ItsNotificationSent == true
                    ? $"\nİTS bildirim no: {completion.NotificationId ?? "—"}"
                    : "\nİTS bildirimi yapılmadı."),
                "Perakende Satış",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SaleCompleted?.Invoke(saleId);
            QrCodeInput.Focus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (RetailItsDefinitelyNotSentException exception)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = $"{exception.Message} İşlem: {exception.OperationId}";
        }
        catch (RetailItsRejectedException exception)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = $"{exception.Message} İşlem: {exception.OperationId}";
        }
        catch (RetailItsReconciliationRequiredException exception)
        {
            BlockItsCompletion(exception.Message, exception.OperationId);
        }
        catch (RetailItsLocalCompletionRequiredException exception)
        {
            BlockItsCompletion(exception.Message, exception.OperationId);
        }
        catch (Exception exception)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = $"Satış tamamlanamadı: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void UpdateSummary(RetailSaleItem item)
    {
        SummaryProductName.Text = item.ProductName;
        SummaryWarehousePrice.Text = item.WarehousePriceDisplay;
        SummaryPublicPrice.Text = item.PublicPriceDisplay;
        SummaryPrescriptionColor.Text = item.PrescriptionColorDisplay;
        SummaryIndication.Text = item.IndicationDisplay;
        SummaryStock.Text = item.StockQuantity.ToString("N0", TurkishCulture);
        SummaryPhoto.Source = CreateImage(item.FirstPhoto);
        NoPhotoText.Visibility = SummaryPhoto.Source is null ? Visibility.Visible : Visibility.Collapsed;
        NoPhotoText.Text = "Fotoğraf yok";
        EquivalentsButton.IsEnabled = true;
    }

    private void ClearSummary()
    {
        SummaryProductName.Text = "Bir ürün seçin";
        SummaryWarehousePrice.Text = "—";
        SummaryPublicPrice.Text = "—";
        SummaryPrescriptionColor.Text = "—";
        SummaryIndication.Text = "—";
        SummaryStock.Text = "—";
        SummaryPhoto.Source = null;
        NoPhotoText.Text = "İlaç Seçilmedi";
        NoPhotoText.Visibility = Visibility.Visible;
        EquivalentsButton.IsEnabled = false;
    }

    private static ImageSource? CreateImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void BeginOperation()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        operationInProgress = true;
        CompleteSaleButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        operationInProgress = false;
        RefreshCartUi();
    }

    private void OnPaymentSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!paymentControlsReady || suppressPaymentChanges) return;
        if (sender == DebtPaymentCheckBox && DebtPaymentCheckBox.IsChecked == true && selectedPatient is null)
        {
            suppressPaymentChanges = true;
            DebtPaymentCheckBox.IsChecked = false;
            suppressPaymentChanges = false;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = "Veresiye ödeme türü için hasta seçilmelidir.";
        }
        if (sender == DiscountPaymentCheckBox)
        {
            DiscountDistributionPanel.Visibility = DiscountPaymentCheckBox.IsChecked == true
                ? Visibility.Visible
                : Visibility.Hidden;
            if (DiscountPaymentCheckBox.IsChecked != true)
            {
                manualDiscounts = false;
                DistributeDiscountCheckBox.IsChecked = true;
                cart.AllocateDiscount(0m);
            }
        }
        RebalancePayments();
    }

    private void OnDiscountDistributionChanged(object sender, RoutedEventArgs e)
    {
        if (!paymentControlsReady || suppressPaymentChanges || DiscountPaymentCheckBox.IsChecked != true) return;
        if (DistributeDiscountCheckBox.IsChecked == false)
            cart.AllocateDiscount(DiscountPaymentBox.Amount);
        manualDiscounts = DistributeDiscountCheckBox.IsChecked == false;
        DiscountColumn.Visibility = Visibility.Visible;
        cart.SetManualDiscountEditing(manualDiscounts);
        SaleGrid.Items.Refresh();
        RebalancePayments();
    }

    private void OnDiscountCellLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!manualDiscounts) return;
        suppressPaymentChanges = true;
        DiscountPaymentBox.Amount = cart.TotalDiscount;
        suppressPaymentChanges = false;
        RebalancePayments(RetailPaymentKind.Discount);
        SaleGrid.Items.Refresh();
    }

    private void OnPaymentAmountChanged(object sender, EventArgs e)
    {
        if (!paymentControlsReady || suppressPaymentChanges || sender is not MoneyEntryControl { Tag: string tag }) return;
        if (!Enum.TryParse<RetailPaymentKind>(tag, out var editedKind)) return;
        if (editedKind == RetailPaymentKind.Discount && manualDiscounts)
        {
            suppressPaymentChanges = true;
            DiscountPaymentBox.Amount = cart.TotalDiscount;
            suppressPaymentChanges = false;
        }
        RebalancePayments(editedKind);
    }

    private void RebalancePayments(RetailPaymentKind? editedKind = null)
    {
        if (!paymentControlsReady) return;
        var selected = GetSelectedPaymentKinds();
        var amounts = GetPaymentAmounts();
        if (selected.Contains(RetailPaymentKind.Discount))
        {
            if (manualDiscounts) amounts[RetailPaymentKind.Discount] = cart.TotalDiscount;
            else cart.AllocateDiscount(amounts[RetailPaymentKind.Discount]);
        }
        RetailPaymentBalancer.Rebalance(GetPaymentBalanceTarget(), selected, amounts, editedKind);
        if (selected.Contains(RetailPaymentKind.Discount) && !manualDiscounts)
            cart.AllocateDiscount(amounts[RetailPaymentKind.Discount]);

        suppressPaymentChanges = true;
        try
        {
            foreach (var kind in Enum.GetValues<RetailPaymentKind>())
            {
                if (kind != editedKind || selected.Count != 2)
                    GetPaymentEditor(kind).Amount = amounts[kind];
            }
        }
        finally
        {
            suppressPaymentChanges = false;
        }
        SyncPaymentEditorEnablement();
        DiscountColumn.Visibility = DiscountPaymentCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        cart.SetManualDiscountEditing(manualDiscounts);
        SaleGrid.Items.Refresh();
        DiscountPercentBox.IsEnabled = DiscountPaymentCheckBox.IsEnabled && !manualDiscounts;
        UpdateDiscountPercentDisplay();
        UpdatePaymentState();
        UpdateTotalsSummary();
    }

    private void UpdateTotalsSummary()
    {
        var amounts = GetPaymentAmounts();
        var debtTotal = amounts[RetailPaymentKind.Debt];
        var discountTotal = amounts[RetailPaymentKind.Discount];
        var paymentTotal = amounts[RetailPaymentKind.Cash] + amounts[RetailPaymentKind.Credit] + amounts[RetailPaymentKind.BankTransfer];
        DebtTotalText.Text = $"Yazılacak Borç: {debtTotal.ToString("N2", TurkishCulture)} ₺";
        DiscountTotalText.Text = $"İskonto: {discountTotal.ToString("N2", TurkishCulture)} ₺";
        PaymentTotalText.Text = $"Ödeme Toplamı: {paymentTotal.ToString("N2", TurkishCulture)} ₺";
    }

    private void UpdatePaymentState()
    {
        if (!paymentControlsReady) return;
        var selected = GetSelectedPaymentKinds();
        var amounts = GetPaymentAmounts();
        var paymentTotal = selected.Sum(kind => amounts[kind]);
        var discountExact = DiscountPaymentCheckBox.IsChecked != true ||
                            decimal.Round(cart.TotalDiscount, 2) == decimal.Round(DiscountPaymentBox.Amount, 2);
        bool exact;
        if (launchOptions.Mode == RetailSaleMode.ConsignmentSettlement)
        {
            var collectionTotal = amounts[RetailPaymentKind.Cash] + amounts[RetailPaymentKind.Credit] +
                                  amounts[RetailPaymentKind.BankTransfer];
            exact = decimal.Round(collectionTotal, 2) == decimal.Round(consignmentCollectionTarget, 2);
            PaymentStatusText.Text = $"Tahsil edilecek: {collectionTotal.ToString("N2", TurkishCulture)} ₺ / {consignmentCollectionTarget.ToString("N2", TurkishCulture)} ₺";
        }
        else
        {
            exact = selected.Count > 0 && RetailPaymentBalancer.IsExact(cart.TotalAmount, selected, amounts);
            PaymentStatusText.Text = $"Ödeme toplamı: {paymentTotal.ToString("N2", TurkishCulture)} ₺ / {cart.TotalAmount.ToString("N2", TurkishCulture)} ₺";
        }
        PaymentStatusText.Foreground = new SolidColorBrush(exact ? Color.FromRgb(22, 125, 78) : Color.FromRgb(179, 58, 50));
        var requiredPatientSelected = launchOptions.Mode == RetailSaleMode.Retail || selectedPatient is not null;
        CompleteSaleButton.IsEnabled = cart.ItemCount > 0 && exact && discountExact && requiredPatientSelected &&
                                       !operationInProgress && !itsCompletionBlocked;
    }

    private IReadOnlyList<RetailSalePaymentInput> BuildPaymentInputs()
    {
        var amounts = GetPaymentAmounts();
        var visiblePayments = GetSelectedPaymentKinds()
            .Where(kind => amounts[kind] > 0)
            .Select(kind => new RetailSalePaymentInput(kind, amounts[kind]))
            .ToArray();
        return launchOptions.Mode == RetailSaleMode.ConsignmentSettlement
            ? ConsignmentSettlementPayments.ComposeFinalPayments(consignmentOriginalPayments, visiblePayments)
            : visiblePayments;
    }

    private decimal GetPaymentBalanceTarget()
    {
        if (launchOptions.Mode != RetailSaleMode.ConsignmentSettlement) return cart.TotalAmount;
        var discount = consignmentOriginalPayments.TryGetValue(RetailPaymentKind.Discount, out var amount)
            ? amount
            : 0m;
        return consignmentCollectionTarget + discount;
    }

    private List<RetailPaymentKind> GetSelectedPaymentKinds() => Enum.GetValues<RetailPaymentKind>()
        .Where(kind => GetPaymentCheckBox(kind).IsChecked == true)
        .ToList();

    private Dictionary<RetailPaymentKind, decimal> GetPaymentAmounts() => Enum.GetValues<RetailPaymentKind>()
        .ToDictionary(kind => kind, kind => GetPaymentEditor(kind).Amount);

    private ToggleButton GetPaymentCheckBox(RetailPaymentKind kind) => kind switch
    {
        RetailPaymentKind.Cash => CashPaymentCheckBox,
        RetailPaymentKind.Credit => CreditPaymentCheckBox,
        RetailPaymentKind.BankTransfer => TransferPaymentCheckBox,
        RetailPaymentKind.Debt => DebtPaymentCheckBox,
        RetailPaymentKind.Discount => DiscountPaymentCheckBox,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private void SyncPaymentEditorEnablement()
    {
        foreach (var kind in Enum.GetValues<RetailPaymentKind>())
        {
            var checkBox = GetPaymentCheckBox(kind);
            GetPaymentEditor(kind).IsEnabled = checkBox.IsEnabled &&
                                                (kind != RetailPaymentKind.Discount || !manualDiscounts);
        }
    }

    private void OnPaymentEntryActivated(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<RetailPaymentKind>(tag, out var kind)) return;
        var checkBox = GetPaymentCheckBox(kind);
        if (checkBox.IsEnabled && checkBox.IsChecked != true) checkBox.IsChecked = true;
    }

    private MoneyEntryControl GetPaymentEditor(RetailPaymentKind kind) => kind switch
    {
        RetailPaymentKind.Cash => CashPaymentBox,
        RetailPaymentKind.Credit => CreditPaymentBox,
        RetailPaymentKind.BankTransfer => TransferPaymentBox,
        RetailPaymentKind.Debt => DebtPaymentBox,
        RetailPaymentKind.Discount => DiscountPaymentBox,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private void ResetPayments()
    {
        suppressPaymentChanges = true;
        try
        {
            foreach (var kind in Enum.GetValues<RetailPaymentKind>())
            {
                GetPaymentCheckBox(kind).IsChecked = false;
                GetPaymentEditor(kind).Amount = 0m;
            }
        }
        finally
        {
            suppressPaymentChanges = false;
        }
        manualDiscounts = false;
        DistributeDiscountCheckBox.IsChecked = true;
        DiscountDistributionPanel.Visibility = Visibility.Hidden;
        DiscountColumn.Visibility = Visibility.Collapsed;
        cart.AllocateDiscount(0m);
        suppressDiscountPercent = true;
        DiscountPercentBox.Text = "0";
        suppressDiscountPercent = false;
        SyncPaymentEditorEnablement();
        DiscountPercentBox.IsEnabled = DiscountPaymentCheckBox.IsEnabled && !manualDiscounts;
        UpdatePaymentState();
        UpdateTotalsSummary();
    }

    private void OnDiscountPercentPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text is "." or ",")
        {
            e.Handled = DiscountPercentBox.Text.Contains('.') || DiscountPercentBox.Text.Contains(',');
            return;
        }
        e.Handled = !MoneyAmountParts.ContainsOnlyDigits(e.Text);
    }

    private void OnDiscountPercentTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressDiscountPercent || !paymentControlsReady || manualDiscounts) return;
        if (DiscountPaymentCheckBox.IsChecked != true || cart.TotalAmount <= 0m) return;
        if (!TryParsePercent(DiscountPercentBox.Text, out var percent)) return;
        percent = Math.Clamp(percent, 0m, 100m);
        var amount = decimal.Round(cart.TotalAmount * percent / 100m, 2, MidpointRounding.AwayFromZero);
        suppressPaymentChanges = true;
        DiscountPaymentBox.Amount = amount;
        suppressPaymentChanges = false;
        RebalancePayments(RetailPaymentKind.Discount);
    }

    private void OnDiscountPercentLostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryParsePercent(DiscountPercentBox.Text, out var percent)) percent = 0m;
        percent = Math.Clamp(percent, 0m, 100m);
        suppressDiscountPercent = true;
        DiscountPercentBox.Text = percent.ToString("0.##", TurkishCulture);
        suppressDiscountPercent = false;
    }

    private void UpdateDiscountPercentDisplay()
    {
        if (DiscountPercentBox.IsKeyboardFocused) return;
        suppressDiscountPercent = true;
        try
        {
            DiscountPercentBox.Text = cart.TotalAmount > 0m
                ? decimal.Round(DiscountPaymentBox.Amount / cart.TotalAmount * 100m, 2, MidpointRounding.AwayFromZero)
                    .ToString("0.##", TurkishCulture)
                : "0";
        }
        finally
        {
            suppressDiscountPercent = false;
        }
    }

    private static bool TryParsePercent(string text, out decimal percent) =>
        decimal.TryParse(text, NumberStyles.Number, TurkishCulture, out percent) ||
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out percent);

    private void ClearSelectedPatient()
    {
        selectedPatient = null;
        EditPatientButton.IsEnabled = false;
        suppressPatientSearch = true;
        PatientSearchBox.Clear();
        suppressPatientSearch = false;
        PatientPlaceholder.Visibility = Visibility.Visible;
        ClearCustomerSummary();
        UpdateDebtAvailability();
        RebalancePayments();
    }

    private void UpdateCustomerSummary(PatientSuggestion patient)
    {
        CustomerName.Text = patient.DisplayName;
        CustomerTc.Text = string.IsNullOrWhiteSpace(patient.TcIdentityNo) ? "—" : patient.TcIdentityNo;
        CustomerPhone.Text = patient.PhoneDisplay;
        CustomerDebt.Text = patient.DebtDisplay;
        CustomerCredit.Text = patient.DebtCreditDisplay;
    }

    private void ClearCustomerSummary()
    {
        CustomerName.Text = "Hasta seçilmedi";
        CustomerTc.Text = CustomerPhone.Text = CustomerDebt.Text = CustomerCredit.Text = "—";
    }

    private void UpdateDebtAvailability()
    {
        if (!paymentControlsReady) return;
        var hasPatient = selectedPatient is not null;
        var debtAllowed = hasPatient && launchOptions.Mode != RetailSaleMode.ConsignmentSettlement;
        DebtPaymentCheckBox.IsEnabled = debtAllowed;
        DebtPaymentBox.IsEnabled = debtAllowed;
        DebtPaymentCheckBox.ToolTip = hasPatient ? "Veresiye ödeme türünü kullan." : "Veresiye için hasta seçilmelidir.";
        if (debtAllowed || DebtPaymentCheckBox.IsChecked != true) return;
        suppressPaymentChanges = true;
        DebtPaymentCheckBox.IsChecked = false;
        DebtPaymentBox.Amount = 0m;
        suppressPaymentChanges = false;
    }

    private void OnOpenEntryMovementsClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailSaleEntry entry) return;
        var qrCode = entry.Code;
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

    private void OnCopyEntryQrClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailSaleEntry entry) return;
        try
        {
            Clipboard.SetText(entry.Code);
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 125, 78));
            StatusText.Text = "Karekod panoya kopyalandı.";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = $"Karekod kopyalanamadı: {exception.Message}";
        }
    }

    private void ShowTemporaryActionDialog(string message, string actionText, string actionToolTip)
    {
        var actionButton = new Button
        {
            Content = actionText,
            Width = 150,
            Height = 36,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
            ToolTip = actionToolTip
        };
        var closeButton = new Button { Content = "Kapat", Width = 90, Height = 36, IsDefault = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { actionButton, closeButton }
        };
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(48, 62, 70)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 22)
        });
        content.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Perakende Satış",
            Content = content,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false
        };
        closeButton.Click += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private void BlockItsCompletion(string message, Guid operationId)
    {
        itsCompletionBlocked = true;
        CompleteSaleButton.ToolTip = "İTS işlemi teknik mutabakat gerektiriyor. Bu karekodları yeniden göndermeyin.";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
        StatusText.Text = $"{message} İşlem: {operationId}";
        pharmacyApp.Localization.EnglishMessageBox.Show(
            Window.GetWindow(this),
            $"{message}\n\nİşlem no: {operationId}\nAynı karekodlarla yeniden bildirim göndermeyin.",
            "İTS Teknik Mutabakat Gerekli",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ConfigureLaunchMode()
    {
        if (launchOptions.Mode == RetailSaleMode.Retail) return;
        PatientPlaceholder.Text = "Hasta seçimi zorunludur";
        if (launchOptions.Mode == RetailSaleMode.ConsignmentCreate)
        {
            SendItsNotificationCheckBox.IsChecked = false;
            SendItsNotificationCheckBox.Visibility = Visibility.Collapsed;
            CompleteSaleButton.Content = "Emanete Ver";
            QrCodeInput.ToolTip = "Emanete verilecek kutunun karekodu";
            return;
        }

        CompleteSaleButton.Content = "Perakende Satışını Gerçekleştir";
        SendItsNotificationCheckBox.IsChecked = true;
        SendItsNotificationCheckBox.IsEnabled = false;
        QrCodeInput.IsEnabled = false;
        AddItemButton.IsEnabled = false;
        PatientSearchBox.IsEnabled = false;
        AddPatientButton.IsEnabled = false;
        EditPatientButton.IsEnabled = false;
        DebtPaymentCheckBox.IsEnabled = false;
        DebtPaymentBox.IsEnabled = false;
        DistributeDiscountCheckBox.IsEnabled = false;
        DiscountPercentBox.IsEnabled = false;
    }

    private async Task LoadConsignmentAsync()
    {
        if (launchOptions.ConsignmentSaleId is not int consignmentId) return;
        try
        {
            BeginOperation();
            StatusText.Text = "Emanet satış yükleniyor...";
            var snapshot = await consignmentRepository.LoadAsync(consignmentId, operationCancellation!.Token);
            if (snapshot.Status != "reserved")
                throw new InvalidOperationException("Bu emanet satış daha önce kapatılmış.");
            consignmentOriginalPayments = snapshot.Payments;
            consignmentCollectionTarget = ConsignmentSettlementPayments.CollectionTarget(snapshot.Payments);

            foreach (var line in snapshot.Lines)
            {
                cart.Add(line.Item);
                var entry = cart.Groups.SelectMany(group => group.Entries)
                    .Last(item => item.Code == line.Item.EnteredCode);
                entry.DiscountAmount = line.DiscountAmount;
            }
            SetSelectedPatient(snapshot.Patient);
            SaleNotesBox.Text = snapshot.Notes ?? string.Empty;
            suppressPaymentChanges = true;
            try
            {
                foreach (var kind in Enum.GetValues<RetailPaymentKind>())
                {
                    GetPaymentCheckBox(kind).IsChecked = false;
                    GetPaymentEditor(kind).Amount = 0m;
                }
                var initial = new Dictionary<RetailPaymentKind, decimal>
                {
                    [RetailPaymentKind.Cash] = consignmentCollectionTarget,
                    [RetailPaymentKind.Discount] = snapshot.Payments[RetailPaymentKind.Discount]
                };
                foreach (var pair in initial.Where(pair => pair.Value > 0))
                {
                    GetPaymentCheckBox(pair.Key).IsChecked = true;
                    GetPaymentEditor(pair.Key).Amount = pair.Value;
                }
            }
            finally
            {
                suppressPaymentChanges = false;
            }
            DiscountPaymentCheckBox.IsEnabled = false;
            DiscountPaymentBox.IsEnabled = false;
            DiscountColumn.Visibility = snapshot.Payments[RetailPaymentKind.Discount] > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            cart.SetManualDiscountEditing(false);
            RefreshCartUi();
            var previouslyPaid = snapshot.Payments[RetailPaymentKind.Cash] + snapshot.Payments[RetailPaymentKind.Credit] +
                                 snapshot.Payments[RetailPaymentKind.BankTransfer];
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 125, 78));
            StatusText.Text = $"Emanet hazır. Önceden alınan {previouslyPaid.ToString("N2", TurkishCulture)} ₺ korunur; " +
                              $"kalan {consignmentCollectionTarget.ToString("N2", TurkishCulture)} ₺ tutarı nakit, kart veya havale ile tahsil edin.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = $"Emanet satış yüklenemedi: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void CompleteSuccessfulOperation(int recordId, string message, string title, string? notificationId = null)
    {
        cart.Clear();
        ClearSelectedPatient();
        ResetPayments();
        SaleNotesBox.Clear();
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 125, 78));
        var itsText = string.IsNullOrWhiteSpace(notificationId) ? string.Empty : $"\nİTS bildirim no: {notificationId}";
        StatusText.Text = $"{message} Kayıt no: {recordId}.";
        pharmacyApp.Localization.EnglishMessageBox.Show($"{message}\nKayıt no: {recordId}{itsText}", title, MessageBoxButton.OK, MessageBoxImage.Information);
        SaleCompleted?.Invoke(recordId);
        QrCodeInput.Focus();
    }

    private static RetailItsConfiguration ReadItsConfiguration()
    {
        var settings = ApplicationSettingsStore.Current;
        return new RetailItsConfiguration(
            ItsEnvironment.Production,
            new ItsCredentials(settings.ItsUsername, settings.ItsPassword),
            settings.ItsPharmacyGln);
    }
}
