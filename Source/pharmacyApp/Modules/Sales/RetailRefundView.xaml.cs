using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.Modules.Sales;

public partial class RetailRefundView : UserControl, IModuleTabRequestSource
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly RetailPaymentKind[] RefundPaymentKinds =
        [RetailPaymentKind.Cash, RetailPaymentKind.Credit, RetailPaymentKind.BankTransfer];
    private readonly RetailRefundLaunchOptions launchOptions;
    private readonly RetailRefundRepository repository = new();
    private readonly RetailSaleRepository patientRepository = new();
    private readonly RetailItsOrchestrationService itsOrchestrator = new(
        new ItsApiClient(),
        new RetailItsOperationRepository(),
        ReadItsConfiguration);
    private readonly RetailRefundCart cart = new();
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? patientSearchCancellation;
    private PatientSuggestion? selectedPatient;
    private RetailRefundItem? selectedSummaryItem;
    private bool suppressPatientSearch;
    private bool suppressPaymentChanges;
    private bool paymentControlsReady;
    private bool operationInProgress;
    private bool patientConflictNotified;
    private bool useDebtReduction = true;
    private bool initialSeedPending;
    private bool itsCompletionBlocked;

    public event Action<ModuleTabRequest>? TabRequested;
    public event Action<int>? RefundCompleted;

    public RetailRefundView() : this(new RetailRefundLaunchOptions())
    {
    }

    public RetailRefundView(RetailRefundLaunchOptions launchOptions)
    {
        this.launchOptions = launchOptions;
        initialSeedPending = launchOptions.InitialSaleId.HasValue;
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        RelatedRecordMenus.Attach(RefundGrid);
        RefundGrid.ItemsSource = cart.Groups;
        cart.Groups.CollectionChanged += OnCartGroupsChanged;
        paymentControlsReady = true;
        UpdatePaymentState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        QrCodeInput.Focus();
        if (!initialSeedPending) return;
        initialSeedPending = false;
        await SeedFromSaleAsync(launchOptions.InitialSaleId!.Value);
    }

    private async Task SeedFromSaleAsync(int saleId)
    {
        try
        {
            BeginOperation();
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
            StatusText.Text = string.Empty;
            var qrCodes = await repository.GetSaleQrCodesAsync(saleId, operationCancellation!.Token);
            var skipped = 0;
            foreach (var code in qrCodes)
            {
                if (cart.Contains(code)) continue;
                var item = await repository.ResolveItemAsync(code, operationCancellation!.Token);
                if (item is null) { skipped++; continue; }
                cart.Add(item);
            }
            ReconcilePatient(showConflictNotice: false);
            RefreshCartUi();
            StatusText.Text = qrCodes.Count == 0
                ? "Bu satışa ait iade edilebilir karekod bulunamadı."
                : skipped > 0
                    ? $"{skipped} karekod artık iade edilebilir durumda değil ve eklenmedi."
                    : string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Satış iade için hazırlanamadı: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
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
            if (item is null)
            {
                StatusText.Text = "Bu karekod daha önce perakende satılmamış veya zaten iade edilmiş.";
                return;
            }
            if (cart.Contains(item.QrCode))
            {
                StatusText.Text = "Bu karekod iade listesinde zaten bulunuyor.";
                return;
            }

            cart.Add(item);
            var group = cart.Groups.First(candidate => candidate.Barcode == item.Barcode);
            RefundGrid.SelectedItem = group;
            selectedSummaryItem = item;
            UpdateSummary(item);
            ReconcilePatient(showConflictNotice: true);
            RefreshCartUi();
            QrCodeInput.Clear();
            QrCodeInput.Focus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Karekod iadeye eklenemedi: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void ReconcilePatient(bool showConflictNotice)
    {
        var resolution = RetailRefundRules.ResolvePatient(cart.Items.Select(item => item.OriginalPatient?.RecordId));
        if (resolution.HasConflict)
        {
            ClearSelectedPatient();
            if (showConflictNotice && !patientConflictNotified)
            {
                var owner = Window.GetWindow(this);
                const string message = "Eklenen karekodlar farklı hastalara ait satışlardan geliyor. Hasta otomatik seçilmedi; lütfen hasta bilgisini elle seçin.";
                if (owner is null) pharmacyApp.Localization.EnglishMessageBox.Show(message, "İade Bilgilendirmesi", MessageBoxButton.OK, MessageBoxImage.Information);
                else pharmacyApp.Localization.EnglishMessageBox.Show(owner, message, "İade Bilgilendirmesi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            patientConflictNotified = true;
            return;
        }

        patientConflictNotified = false;
        var patient = cart.Items.Select(item => item.OriginalPatient).FirstOrDefault(item => item?.RecordId == resolution.PatientId);
        if (patient is null) ClearSelectedPatient();
        else
        {
            SetSelectedPatient(patient);
            _ = RefreshSelectedPatientAsync(patient.RecordId);
        }
    }

    private void OnCartGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshCartUi();

    private void RefreshCartUi()
    {
        RefundGrid.Items.Refresh();
        TotalText.Text = $"İade Toplamı: {cart.TotalAmount.ToString("N2", TurkishCulture)} ₺";
        DiscountTotalText.Text = $"İskonto: {cart.TotalDiscount.ToString("N2", TurkishCulture)} ₺";
        UpdateDebtReductionUi();
        RebalancePayments();
        if (cart.ItemCount != 0) return;
        selectedSummaryItem = null;
        ClearSummary();
    }

    private void OnRefundGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RefundGrid.SelectedItem is not RetailRefundGroup { Entries.Count: > 0 } group) return;
        selectedSummaryItem = group.Entries[0].Item;
        UpdateSummary(selectedSummaryItem);
    }

    private void OnRefundGroupExpandClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: RetailRefundGroup group } toggle) return;
        group.IsExpanded = toggle.IsChecked == true;
        if (RefundGrid.ItemContainerGenerator.ContainerFromItem(group) is DataGridRow row)
            row.DetailsVisibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnRefundEntrySelected(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailRefundEntry entry) return;
        selectedSummaryItem = entry.Item;
        RefundGrid.SelectedItem = cart.Groups.FirstOrDefault(group => group.Entries.Contains(entry));
        UpdateSummary(entry.Item);
    }

    private void OnRefundGridRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is not null) row.IsSelected = true;
    }

    private void OnRefundGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = RefundGrid.InputHitTest(Mouse.GetPosition(RefundGrid)) as DependencyObject;
        if (FindAncestor<DataGridRow>(hit) is null) e.Handled = true;
    }

    private void OnRemoveGroupClicked(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as RetailRefundGroup ?? RefundGrid.SelectedItem as RetailRefundGroup;
        if (group is null) return;
        cart.RemoveGroup(group);
        ReconcilePatient(showConflictNotice: false);
        SelectFirstRemainingItem();
    }

    private void OnRemoveEntryClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailRefundEntry entry) return;
        cart.RemoveEntry(entry);
        ReconcilePatient(showConflictNotice: false);
        SelectFirstRemainingItem();
    }

    private void SelectFirstRemainingItem()
    {
        RefreshCartUi();
        var first = cart.Groups.FirstOrDefault();
        RefundGrid.SelectedItem = first;
        if (first?.Entries.FirstOrDefault() is { } entry) UpdateSummary(entry.Item);
    }

    private async void OnOpenGroupProductClicked(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as RetailRefundGroup ?? RefundGrid.SelectedItem as RetailRefundGroup;
        if (group is not null) await OpenProductPopupAsync(group.ProductRecordId, ProductCatalogInitialTab.General);
    }

    private async void OnOpenEntryProductClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RetailRefundEntry entry)
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
            var product = await new ProductCatalogRepository().LoadByRecordIdAsync(productRecordId, operationCancellation!.Token);
            if (product is null) return;
            var view = new ProductCatalogView(new ProductCatalogLaunchOptions(InitialProduct: product, InitialTab: initialTab));
            new Window
            {
                Title = product.DisplayName, Content = view, Width = 1120, Height = 780,
                MinWidth = 760, MinHeight = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this), ShowInTaskbar = false
            }.Show();
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
        PatientPlaceholder.Visibility = string.IsNullOrEmpty(PatientSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (suppressPatientSearch) return;
        selectedPatient = null;
        EditPatientButton.IsEnabled = false;
        CancellationTokenSourceGuard.CancelAndDispose(ref patientSearchCancellation);
        PatientSuggestionPopup.IsOpen = false;
        PatientSuggestionList.ItemsSource = null;
        var term = PatientSearchBox.Text.Trim();
        if (term.Length < 3)
        {
            PatientSuggestionPopup.IsOpen = false;
            return;
        }
        patientSearchCancellation = new CancellationTokenSource();
        try
        {
            var token = patientSearchCancellation.Token;
            await Task.Delay(220, token);
            var suggestions = await patientRepository.SearchPatientsAsync(term, token);
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

    private async void OnPatientSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

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
            SetSelectedPatient(onlySuggestion);
            PatientSuggestionPopup.IsOpen = false;
            PatientSuggestionList.SelectedItem = null;
            e.Handled = true;
        }
    }

    private async Task SelectOrCreatePatientByTcAsync(string tcIdentityNumber)
    {
        CancellationTokenSourceGuard.CancelAndDispose(ref patientSearchCancellation);
        patientSearchCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = string.Empty;
            var patient = await patientRepository.LoadPatientByTcAsync(
                tcIdentityNumber,
                patientSearchCancellation.Token);
            if (patient is not null)
                SetSelectedPatient(patient);
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

    private void OnPatientSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PatientSuggestionList.SelectedItem is not PatientSuggestion patient) return;
        SetSelectedPatient(patient);
        PatientSuggestionPopup.IsOpen = false;
        PatientSuggestionList.SelectedItem = null;
    }

    private void SetSelectedPatient(PatientSuggestion patient)
    {
        selectedPatient = patient;
        useDebtReduction = true;
        UseDebtReductionCheckBox.IsChecked = patient.DebtAmount > 0m;
        suppressPatientSearch = true;
        PatientSearchBox.Text = patient.DisplayText;
        PatientSearchBox.CaretIndex = PatientSearchBox.Text.Length;
        suppressPatientSearch = false;
        PatientPlaceholder.Visibility = Visibility.Collapsed;
        EditPatientButton.IsEnabled = true;
        UpdateCustomerSummary(patient);
        UpdateDebtReductionUi();
        UpdatePaymentState();
    }

    private void ClearSelectedPatient()
    {
        selectedPatient = null;
        EditPatientButton.IsEnabled = false;
        suppressPatientSearch = true;
        PatientSearchBox.Clear();
        suppressPatientSearch = false;
        PatientPlaceholder.Visibility = Visibility.Visible;
        ClearCustomerSummary();
        UpdateDebtReductionUi();
        UpdatePaymentState();
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
        editor.CloseRequested += window.Close;
        window.ShowDialog();
        if (savedPatient is not null) _ = RefreshSelectedPatientAsync(savedPatient.RecordId);
    }

    private void OnEditPatientClicked(object sender, RoutedEventArgs e)
    {
        if (selectedPatient is null) return;
        var patient = selectedPatient;
        var editor = new CardEditorView(CardDefinitions.Patients, patient.RecordId.ToString(CultureInfo.InvariantCulture),
            patient.DisplayName, () => _ = RefreshSelectedPatientAsync(patient.RecordId), startEditing: false);
        var window = CreatePatientWindow(editor, patient.DisplayName);
        editor.RecordDeleted += key => { if (key == patient.RecordId.ToString(CultureInfo.InvariantCulture)) ClearSelectedPatient(); };
        editor.CloseRequested += window.Close;
        window.Show();
    }

    private Window CreatePatientWindow(CardEditorView editor, string title) => new()
    {
        Title = title, Content = editor, Width = 1280, Height = 880, MinWidth = 840, MinHeight = 640,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), ShowInTaskbar = false,
        Background = new SolidColorBrush(Color.FromRgb(243, 245, 247))
    };

    private async Task RefreshSelectedPatientAsync(int recordId)
    {
        try
        {
            var patient = await patientRepository.LoadPatientAsync(recordId, CancellationToken.None);
            if (patient is not null) SetSelectedPatient(patient);
            else ClearSelectedPatient();
        }
        catch
        {
        }
    }

    private void OnPaymentSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!paymentControlsReady || suppressPaymentChanges) return;
        RebalancePayments();
    }

    private void OnPaymentAmountChanged(object sender, EventArgs e)
    {
        if (!paymentControlsReady || suppressPaymentChanges || sender is not MoneyEntryControl { Tag: string tag }) return;
        if (Enum.TryParse<RetailPaymentKind>(tag, out var kind)) RebalancePayments(kind);
    }

    private void RebalancePayments(RetailPaymentKind? editedKind = null)
    {
        if (!paymentControlsReady) return;
        var selected = GetSelectedPaymentKinds();
        var amounts = GetPaymentAmounts();
        RetailPaymentBalancer.Rebalance(PaymentTotal, selected, amounts, editedKind);
        suppressPaymentChanges = true;
        try
        {
            foreach (var kind in RefundPaymentKinds)
            {
                if (kind != editedKind || selected.Count != 2) GetPaymentEditor(kind).Amount = amounts[kind];
            }
        }
        finally
        {
            suppressPaymentChanges = false;
        }
        UpdatePaymentState();
    }

    private void OnPaymentEntryActivated(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<RetailPaymentKind>(tag, out var kind)) return;
        var checkBox = GetPaymentCheckBox(kind);
        if (checkBox.IsEnabled && checkBox.IsChecked != true) checkBox.IsChecked = true;
    }

    private void UpdatePaymentState()
    {
        var selected = GetSelectedPaymentKinds();
        var amounts = GetPaymentAmounts();
        var paymentTotal = selected.Sum(kind => amounts[kind]);
        var exact = (selected.Count > 0 || PaymentTotal == 0m) &&
                    RetailPaymentBalancer.IsExact(PaymentTotal, selected, amounts);
        var hasPatient = selectedPatient is not null;
        PaymentStatusText.Text = $"İade ödeme toplamı: {paymentTotal.ToString("N2", TurkishCulture)} ₺ / {PaymentTotal.ToString("N2", TurkishCulture)} ₺";
        PaymentStatusText.Foreground = new SolidColorBrush(exact ? Color.FromRgb(22, 125, 78) : Color.FromRgb(179, 58, 50));
        CompleteRefundButton.IsEnabled = cart.ItemCount > 0 && exact && hasPatient &&
                                         !operationInProgress && !itsCompletionBlocked;
        CompleteRefundButton.ToolTip = itsCompletionBlocked
            ? "İTS işlemi teknik mutabakat gerektiriyor. Bu karekodları yeniden göndermeyin."
            : hasPatient ? null : "İade için hasta seçilmelidir.";
    }

    private List<RetailPaymentKind> GetSelectedPaymentKinds() => RefundPaymentKinds
        .Where(kind => GetPaymentCheckBox(kind).IsChecked == true).ToList();

    private Dictionary<RetailPaymentKind, decimal> GetPaymentAmounts() => RefundPaymentKinds
        .ToDictionary(kind => kind, kind => GetPaymentEditor(kind).Amount);

    private ToggleButton GetPaymentCheckBox(RetailPaymentKind kind) => kind switch
    {
        RetailPaymentKind.Cash => CashPaymentCheckBox,
        RetailPaymentKind.Credit => CreditPaymentCheckBox,
        RetailPaymentKind.BankTransfer => TransferPaymentCheckBox,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private MoneyEntryControl GetPaymentEditor(RetailPaymentKind kind) => kind switch
    {
        RetailPaymentKind.Cash => CashPaymentBox,
        RetailPaymentKind.Credit => CreditPaymentBox,
        RetailPaymentKind.BankTransfer => TransferPaymentBox,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private IReadOnlyList<RetailSalePaymentInput> BuildPaymentInputs()
    {
        var amounts = GetPaymentAmounts();
        return GetSelectedPaymentKinds().Where(kind => amounts[kind] > 0)
            .Select(kind => new RetailSalePaymentInput(kind, amounts[kind])).ToArray();
    }

    private void ResetPayments()
    {
        suppressPaymentChanges = true;
        foreach (var kind in RefundPaymentKinds)
        {
            GetPaymentCheckBox(kind).IsChecked = false;
            GetPaymentEditor(kind).Amount = 0;
        }
        suppressPaymentChanges = false;
        UpdatePaymentState();
    }

    private async void OnCompleteRefundClicked(object sender, RoutedEventArgs e)
    {
        if (cart.ItemCount == 0 || operationInProgress) return;
        try
        {
            BeginOperation();
            StatusText.Text = "İTS iptal bildirimi gönderiliyor ve iade kaydediliyor...";
            var items = cart.Items.ToArray();
            var patientId = selectedPatient?.RecordId;
            var payments = BuildPaymentInputs();
            var debtReduction = DebtReduction;
            var notes = RefundNotesBox.Text;
            var userId = (Application.Current as App)?.CurrentUser?.UserId ?? "admin";
            var sendItsNotification = SendItsNotificationCheckBox.IsChecked == true;
            RetailItsCompletionResult? completion = null;
            int refundId;
            if (sendItsNotification)
            {
                var qrs = items.Select(item => item.QrCode).ToArray();
                await new UnnotifiedItsMovementRepository().ValidateBeforeActionAsync(
                    qrs, StockMovementDirection.In, true, operationCancellation!.Token);
                completion = await itsOrchestrator.ExecuteAsync(
                    RetailItsOperationKind.SaleCancellation, qrs, userId,
                    token => repository.CompleteRefundAsync(items, patientId, payments, debtReduction, notes, userId, true, token),
                    operationCancellation.Token);
                refundId = completion.LocalRecordId;
            }
            else
            {
                StatusText.Text = "İade yerel stoka kaydediliyor; İTS bildirimi bekleyen harekete alınacak...";
                refundId = await repository.CompleteRefundAsync(
                    items, patientId, payments, debtReduction, notes, userId, false, operationCancellation!.Token);
            }
            cart.Clear();
            ClearSelectedPatient();
            ResetPayments();
            RefundNotesBox.Clear();
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 125, 78));
            StatusText.Text = $"İade başarıyla tamamlandı. İade no: {refundId}. " +
                              (completion?.ItsNotificationSent == true
                                  ? $"İTS bildirim no: {completion.NotificationId ?? "—"}."
                                  : "İTS bildirimi yapılmadı; Debug ekranından düzeltilebilir.");
            pharmacyApp.Localization.EnglishMessageBox.Show(Window.GetWindow(this),
                $"İade başarıyla tamamlandı.\nİade no: {refundId}\n" +
                (completion?.ItsNotificationSent == true ? $"İTS bildirim no: {completion.NotificationId ?? "—"}" : "İTS bildirimi yapılmadı."),
                "Perakende Satış İade", MessageBoxButton.OK, MessageBoxImage.Information);
            RefundCompleted?.Invoke(refundId);
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
            StatusText.Text = $"İade tamamlanamadı: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void UpdateSummary(RetailRefundItem item)
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
        SummaryWarehousePrice.Text = SummaryPublicPrice.Text = SummaryPrescriptionColor.Text = SummaryIndication.Text = SummaryStock.Text = "—";
        SummaryPhoto.Source = null;
        NoPhotoText.Text = "İlaç Seçilmedi";
        NoPhotoText.Visibility = Visibility.Visible;
        EquivalentsButton.IsEnabled = false;
    }

    private decimal DebtReduction => RetailRefundRules.CalculateDebtReduction(
        cart.TotalAmount,
        selectedPatient?.DebtAmount ?? 0m,
        useDebtReduction && UseDebtReductionCheckBox.IsChecked == true);

    private decimal PaymentTotal => decimal.Round(cart.TotalAmount - DebtReduction, 2, MidpointRounding.AwayFromZero);

    private void OnUseDebtReductionChanged(object sender, RoutedEventArgs e)
    {
        if (suppressPaymentChanges) return;
        useDebtReduction = UseDebtReductionCheckBox.IsChecked == true;
        UpdateDebtReductionUi();
        RebalancePayments();
    }

    private void UpdateDebtReductionUi()
    {
        if (!paymentControlsReady) return;
        var hasDebt = selectedPatient is { DebtAmount: > 0m } && cart.ItemCount > 0;
        UseDebtReductionCheckBox.IsEnabled = hasDebt;
        if (!hasDebt)
        {
            suppressPaymentChanges = true;
            UseDebtReductionCheckBox.IsChecked = false;
            suppressPaymentChanges = false;
            useDebtReduction = false;
        }
        else if (UseDebtReductionCheckBox.IsChecked is null or false && useDebtReduction)
        {
            UseDebtReductionCheckBox.IsChecked = true;
        }
        DebtReductionText.Text = $"Silinecek Borç: {DebtReduction.ToString("N2", TurkishCulture)} ₺";
        DebtReductionBorder.Background = new SolidColorBrush(hasDebt && UseDebtReductionCheckBox.IsChecked == true
            ? Color.FromRgb(255, 255, 255)
            : Color.FromRgb(231, 235, 237));
        PaymentTotalText.Text = $"Ödeme Toplamı: {PaymentTotal.ToString("N2", TurkishCulture)} ₺";
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

    private static ImageSource? CreateImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void OnCopyQrClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailRefundEntry entry) return;
        try { Clipboard.SetText(entry.QrCode); }
        catch (Exception exception) { StatusText.Text = $"Karekod kopyalanamadı: {exception.Message}"; }
    }

    private void OnOpenQrMovementsClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RetailRefundEntry entry) return;
        var options = new QrMovementsLaunchOptions(
            DateTime.Today, DateTime.Today, PeriodTitle: string.Empty,
            Search: entry.QrCode, IgnoreDateFilter: true);
        TabRequested?.Invoke(new ModuleTabRequest(
            $"products.qr-movements.qr.{Uri.EscapeDataString(entry.QrCode)}",
            "Karekod Hareketleri (Karekoda Özel)",
            () => new QrMovementsView(options),
            1050,
            560));
    }

    private void BeginOperation()
    {
        CancellationTokenSourceGuard.CancelAndDispose(ref operationCancellation);
        operationCancellation = new CancellationTokenSource();
        operationInProgress = true;
        CompleteRefundButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        operationInProgress = false;
        RefreshCartUi();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void BlockItsCompletion(string message, Guid operationId)
    {
        itsCompletionBlocked = true;
        CompleteRefundButton.ToolTip = "İTS işlemi teknik mutabakat gerektiriyor. Bu karekodları yeniden göndermeyin.";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 58, 50));
        StatusText.Text = $"{message} İşlem: {operationId}";
        pharmacyApp.Localization.EnglishMessageBox.Show(
            Window.GetWindow(this),
            $"{message}\n\nİşlem no: {operationId}\nAynı karekodlarla yeniden bildirim göndermeyin.",
            "İTS Teknik Mutabakat Gerekli",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
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

/// <summary>Maps RetailRefundEntry.SaleGroupIndex to a light background tint so QR rows from different origin sales are visually distinguishable; -1 keeps the neutral default.</summary>
public sealed class SaleGroupIndexToBrushConverter : IValueConverter
{
    private static readonly Brush DefaultBrush = CreateFrozen("#F4F8F9");
    private static readonly Brush[] Palette =
    [
        CreateFrozen("#EAF2FB"),
        CreateFrozen("#FDF3E4"),
        CreateFrozen("#F1E9F9"),
        CreateFrozen("#E9F7EE"),
        CreateFrozen("#FDEAF0")
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index and >= 0 ? Palette[index % Palette.Length] : DefaultBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush CreateFrozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
