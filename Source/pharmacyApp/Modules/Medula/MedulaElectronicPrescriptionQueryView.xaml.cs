using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Medula;

public partial class MedulaElectronicPrescriptionQueryView : UserControl, IModuleTabRequestSource,
    IModuleTitleSource, IModuleIdentitySource
{
    private readonly MedulaAutomation service = MedulaAutomation.Instance;
    private readonly MedulaDirectoryRepository directoryRepository = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? patientSuggestionCancellation;
    private MedulaElectronicPrescriptionDetail? activeDetail;
    private MedulaElectronicPrescriptionListItem? activePrescription;
    private readonly MedulaElectronicPrescriptionDetail? initialDetail;
    private string pendingPatientName = string.Empty;
    private bool loadedOnce;
    private bool busy;
    private bool applyingPatientSuggestion;
    private bool navigatingPatientSuggestions;
    private bool detailOpenedFromPrescription;
    private readonly string flowIdentity = Guid.NewGuid().ToString("N");

    public MedulaElectronicPrescriptionQueryView()
    {
        Prescriptions = [];
        PreviousPrescriptions = [];
        InitializeComponent();
        DataContext = this;
        PrescriptionDatePicker.SelectedDate = DateTime.Today;
    }

    public MedulaElectronicPrescriptionQueryView(MedulaElectronicPrescriptionDetail detail)
        : this()
    {
        initialDetail = detail;
    }

    public ObservableCollection<MedulaElectronicPrescriptionListItem> Prescriptions { get; }
    public ObservableCollection<MedulaElectronicPrescriptionListItem> PreviousPrescriptions { get; }
    public event Action<ModuleTabRequest>? TabRequested;
    public string CurrentModuleTitle { get; private set; } = "E-Reçete Sorgu";
    public event Action<string>? ModuleTitleChanged;
    public string CurrentModuleId { get; private set; } = "medula.e-prescription-query";
    public event Action<string>? ModuleIdentityChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.StatusChanged += OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnWarningChanged;
        service.ScopedWarningChanged += OnWarningChanged;
        service.ScopedConfirmationChanged -= OnConfirmationChanged;
        service.ScopedConfirmationChanged += OnConfirmationChanged;
        service.ScopedNoticeChanged -= OnNoticeChanged;
        service.ScopedNoticeChanged += OnNoticeChanged;
        if (loadedOnce) return;
        loadedOnce = true;
        if (initialDetail is not null)
        {
            activeDetail = initialDetail;
            detailOpenedFromPrescription = true;
            pendingPatientName = initialDetail.PatientName;
            ShowDetail(initialDetail);
            return;
        }
        await OpenQueryAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnWarningChanged;
        service.ScopedConfirmationChanged -= OnConfirmationChanged;
        service.ScopedNoticeChanged -= OnNoticeChanged;
        patientSuggestionCancellation?.Cancel();
        patientSuggestionCancellation?.Dispose();
        patientSuggestionCancellation = null;
        PatientSuggestionPopup.IsOpen = false;
        pendingPatientName = string.Empty;
        ClearDetailData();
    }

    private void OnServiceStatusChanged(object? sender, MedulaConnectionStatus status) =>
        Dispatcher.InvokeAsync(() =>
        {
            ConnectionText.Text = status.Message;
            StatusDot.Fill = new SolidColorBrush(status.State switch
            {
                MedulaConnectionState.Connected => Color.FromRgb(13, 148, 136),
                MedulaConnectionState.Failed or MedulaConnectionState.TemporarilyOffline => Color.FromRgb(198, 67, 67),
                _ => Color.FromRgb(210, 139, 44)
            });
        });

    private void OnWarningChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.ElectronicPrescription) return;
        Dispatcher.InvokeAsync(() => SetMessage(message.Text, MedulaMessageSeverity.Error));
    }

    private void OnConfirmationChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.ElectronicPrescription) return;
        Dispatcher.InvokeAsync(() => SetMessage(message.Text, MedulaMessageSeverity.Success));
    }

    private void OnNoticeChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.ElectronicPrescription) return;
        Dispatcher.InvokeAsync(() => SetMessage(message.Text, MedulaMessageSeverity.Notice));
    }

    private async Task OpenQueryAsync()
    {
        if (busy) return;
        SetBusy(true, "E-Reçete Sorgu ekranı açılıyor…");
        try
        {
            var result = await service.OpenElectronicPrescriptionQueryAsync(lifetime.Token);
            ApplyQueryResult(result, true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPrimarySearchClick(object sender, RoutedEventArgs e) => await SearchAsync(false);
    private async void OnTrackingSearchClick(object sender, RoutedEventArgs e) => await SearchAsync(true);

    private async void OnPatientSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (applyingPatientSuggestion) return;
        patientSuggestionCancellation?.Cancel();
        patientSuggestionCancellation?.Dispose();
        patientSuggestionCancellation = new CancellationTokenSource();
        var term = PatientIdentityBox.Text.Trim();
        if (term.Length < 3)
        {
            PatientSuggestionPopup.IsOpen = false;
            PatientSuggestionList.ItemsSource = null;
            return;
        }

        try
        {
            var token = patientSuggestionCancellation.Token;
            await Task.Delay(180, token);
            var suggestions = await directoryRepository.SearchPatientsAsync(term, token);
            if (term.Length == 11 && term.All(char.IsDigit))
            {
                var exact = suggestions.FirstOrDefault(patient =>
                    patient.TcIdentityNo.Equals(term, StringComparison.Ordinal));
                if (exact is not null)
                {
                    ApplyPatientSuggestion(exact);
                    return;
                }
            }
            PatientSuggestionList.ItemsSource = suggestions;
            PatientSuggestionPopup.IsOpen = suggestions.Count > 0
                && PatientIdentityBox.IsKeyboardFocusWithin;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetWarning($"Hasta önerileri yüklenemedi: {exception.Message}");
        }
    }

    private void OnPatientSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (navigatingPatientSuggestions
            || PatientSuggestionList.SelectedItem is not PatientSuggestion patient) return;
        ApplyPatientSuggestion(patient);
    }

    private void OnPatientSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && PatientSuggestionList.Items.Count > 0)
        {
            PatientSuggestionPopup.IsOpen = true;
            navigatingPatientSuggestions = true;
            PatientSuggestionList.SelectedIndex = e.Key == Key.Down
                ? 0
                : PatientSuggestionList.Items.Count - 1;
            navigatingPatientSuggestions = false;
            PatientSuggestionList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
            PatientSuggestionPopup.IsOpen = false;
    }

    private void OnPatientSuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && PatientSuggestionList.Items.Count > 0)
        {
            navigatingPatientSuggestions = true;
            PatientSuggestionList.SelectedIndex = Math.Clamp(
                PatientSuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1),
                0,
                PatientSuggestionList.Items.Count - 1);
            PatientSuggestionList.ScrollIntoView(PatientSuggestionList.SelectedItem);
            navigatingPatientSuggestions = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && PatientSuggestionList.SelectedItem is PatientSuggestion patient)
        {
            ApplyPatientSuggestion(patient);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PatientSuggestionPopup.IsOpen = false;
            PatientIdentityBox.Focus();
            e.Handled = true;
        }
    }

    private void ApplyPatientSuggestion(PatientSuggestion patient)
    {
        applyingPatientSuggestion = true;
        try
        {
            PatientIdentityBox.Text = patient.TcIdentityNo;
            PatientIdentityBox.CaretIndex = PatientIdentityBox.Text.Length;
            pendingPatientName = patient.DisplayName;
            PatientSuggestionPopup.IsOpen = false;
            PatientSuggestionList.SelectedItem = null;
            PatientSuggestionList.ItemsSource = null;
            PatientIdentityBox.Focus();
        }
        finally
        {
            applyingPatientSuggestion = false;
        }
    }

    private async Task SearchAsync(bool byTrackingNumber)
    {
        if (busy) return;
        SetBusy(true, "E-Reçete sorgulanıyor…");
        SetWarning(null);
        try
        {
            var previousRows = Prescriptions.ToArray();
            var result = await service.SearchElectronicPrescriptionsAsync(
                PatientIdentityBox.Text,
                ElectronicPrescriptionNumberBox.Text,
                TrackingNumberBox.Text,
                PrescriptionDatePicker.SelectedDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                byTrackingNumber,
                lifetime.Token);
            PreviousPrescriptions.Clear();
            foreach (var item in previousRows) PreviousPrescriptions.Add(item);
            PreviousResultSection.Visibility = PreviousPrescriptions.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyQueryResult(result, true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnResultGridMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (busy || FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row
            || row.Item is not MedulaElectronicPrescriptionListItem item)
            return;
        await OpenDetailAsync(item);
    }

    private async Task OpenDetailAsync(MedulaElectronicPrescriptionListItem item)
    {
        SetBusy(true, "E-Reçete ayrıntıları açılıyor…");
        SetWarning(null);
        try
        {
            var detail = await service.OpenElectronicPrescriptionDetailAsync(item, PatientIdentityBox.Text, lifetime.Token);
            if (!detail.IsOpen)
            {
                SetWarning(detail.WarningText ?? detail.Message);
                return;
            }
            activePrescription = item;
            activeDetail = detail;
            detailOpenedFromPrescription = false;
            pendingPatientName = detail.PatientName;
            if (ApplicationSettingsStore.Current.SkipElectronicPrescriptionView
                && detail.CanContinue
                && await ContinueToPrescriptionAsync(manageBusy: false))
                return;
            ShowDetail(detail);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true, detailOpenedFromPrescription
            ? "Reçete ekranına dönülüyor…"
            : "E-Reçete sorgu sonuçlarına dönülüyor…");
        try
        {
            if (detailOpenedFromPrescription)
            {
                await ReturnToPrescriptionViewAsync();
                return;
            }
            var result = await service.ReturnFromElectronicPrescriptionDetailAsync(lifetime.Token);
            if (!result.IsOpen)
            {
                SetWarning(result.WarningText ?? result.Message);
                return;
            }
            activeDetail = null;
            activePrescription = null;
            pendingPatientName = string.Empty;
            ClearDetailData();
            ApplyQueryResult(result, true);
            ShowQuery();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReturnToPrescriptionViewAsync()
    {
        var detail = activeDetail;
        if (detail is null) return;
        if (!await service.ReturnFromElectronicPrescriptionDetailToPrescriptionAsync(lifetime.Token))
        {
            SetWarning("E-Reçete ekranından reçeteye dönülemedi.");
            return;
        }

        var snapshot = await service.CaptureCurrentPrescriptionAsync(
            detail.PatientName, null, null, 0, lifetime.Token);
        if (snapshot is null)
        {
            SetWarning("Medula reçete ekranına döndü ancak reçete bilgileri okunamadı.");
            return;
        }
        var fields = snapshot.Page.FieldValues is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(snapshot.Page.FieldValues, StringComparer.OrdinalIgnoreCase);
        fields["medula:e-prescription-no"] = detail.ElectronicPrescriptionNumber;
        for (var index = 0; index < snapshot.Drugs.Count; index++)
        {
            var prescriptionDrug = snapshot.Drugs[index];
            var detailDrug = detail.Drugs.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(prescriptionDrug.Barcode)
                    && item.Barcode.Equals(prescriptionDrug.Barcode, StringComparison.Ordinal))
                ?? (index < detail.Drugs.Count ? detail.Drugs[index] : null);
            prescriptionDrug.DiagnosisNote = MedulaElectronicPrescriptionDrugNotes.ImportantDiagnosis(
                detailDrug?.Description);
        }
        snapshot = snapshot with
        {
            PatientDisplayName = FirstNonBlank(detail.PatientName, snapshot.PatientDisplayName),
            Page = snapshot.Page with { FieldValues = fields }
        };
        var prescriptionView = new MedulaPrescriptionEntryView(snapshot);
        prescriptionView.TabRequested += request => TabRequested?.Invoke(request);
        prescriptionView.ElectronicPrescriptionViewRequested += OnElectronicPrescriptionViewRequested;
        activeDetail = null;
        detailOpenedFromPrescription = false;
        ClearDetailData();
        DetailView.Visibility = Visibility.Collapsed;
        QueryView.Visibility = Visibility.Collapsed;
        TransformedContentHost.Content = prescriptionView;
        TransformedContentHost.Visibility = Visibility.Visible;
        SetModuleTitle(detail.ElectronicPrescriptionNumber);
        SetModuleIdentity($"medula.prescription-view.e.{detail.ElectronicPrescriptionNumber}.{flowIdentity}");
    }

    private async void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await ContinueToPrescriptionAsync(manageBusy: true);
    }

    private async Task<bool> ContinueToPrescriptionAsync(bool manageBusy)
    {
        if (activeDetail is null) return false;
        if (manageBusy) SetBusy(true, "Reçete ekranına devam ediliyor…");
        SetWarning(null);
        try
        {
            var result = await service.ContinueElectronicPrescriptionAsync(
                activeDetail.PatientIdentityNumber,
                activeDetail.ElectronicPrescriptionNumber,
                lifetime.Token);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                SetWarning(result.WarningText ?? result.Message);
                return false;
            }
            var electronicPrescriptionNumber = activeDetail.ElectronicPrescriptionNumber;
            var cachedPatientName = pendingPatientName;
            pendingPatientName = string.Empty;
            var prescriptionFields = result.Snapshot.Page.FieldValues is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(result.Snapshot.Page.FieldValues, StringComparer.OrdinalIgnoreCase);
            prescriptionFields["medula:e-prescription-no"] = electronicPrescriptionNumber;
            var detailDrugs = activeDetail.Drugs;
            for (var index = 0; index < result.Snapshot.Drugs.Count; index++)
            {
                var prescriptionDrug = result.Snapshot.Drugs[index];
                var detailDrug = detailDrugs.FirstOrDefault(item =>
                        !string.IsNullOrWhiteSpace(prescriptionDrug.Barcode)
                        && item.Barcode.Equals(prescriptionDrug.Barcode, StringComparison.Ordinal))
                    ?? (index < detailDrugs.Count ? detailDrugs[index] : null);
                prescriptionDrug.DiagnosisNote = MedulaElectronicPrescriptionDrugNotes.ImportantDiagnosis(
                    detailDrug?.Description);
            }
            var snapshot = result.Snapshot with
            {
                PatientDisplayName = FirstNonBlank(cachedPatientName, result.Snapshot.PatientDisplayName),
                Page = result.Snapshot.Page with { FieldValues = prescriptionFields }
            };
            var prescriptionView = new MedulaPrescriptionEntryView(snapshot);
            prescriptionView.TabRequested += request => TabRequested?.Invoke(request);
            prescriptionView.ElectronicPrescriptionViewRequested += OnElectronicPrescriptionViewRequested;
            activeDetail = null;
            ClearDetailData();
            TransformedContentHost.Content = prescriptionView;
            TransformedContentHost.Visibility = Visibility.Visible;
            SetModuleTitle(electronicPrescriptionNumber);
            SetModuleIdentity($"medula.prescription-view.e.{electronicPrescriptionNumber}.{flowIdentity}");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (manageBusy) SetBusy(false);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var reopenDetail = activeDetail is not null && activePrescription is not null;
        var selected = activePrescription;
        var hadSearch = Prescriptions.Count > 0
            || PatientIdentityBox.Text.Trim().Length > 0
            || ElectronicPrescriptionNumberBox.Text.Trim().Length > 0
            || TrackingNumberBox.Text.Trim().Length > 0;
        await OpenQueryAsync();
        if (!hadSearch) return;
        await SearchAsync(TrackingNumberBox.Text.Trim().Length > 0
            && ElectronicPrescriptionNumberBox.Text.Trim().Length == 0);
        if (reopenDetail && selected is not null)
        {
            var match = Prescriptions.FirstOrDefault(item => item.ElectronicPrescriptionNumber.Equals(
                selected.ElectronicPrescriptionNumber, StringComparison.OrdinalIgnoreCase));
            if (match is not null) await OpenDetailAsync(match);
        }
    }

    private void ApplyQueryResult(MedulaElectronicPrescriptionQueryResult result, bool replaceRows)
    {
        if (!string.IsNullOrWhiteSpace(result.PatientIdentityNumber))
        {
            applyingPatientSuggestion = true;
            try { PatientIdentityBox.Text = result.PatientIdentityNumber; }
            finally { applyingPatientSuggestion = false; }
        }
        if (!string.IsNullOrWhiteSpace(result.ElectronicPrescriptionNumber)) ElectronicPrescriptionNumberBox.Text = result.ElectronicPrescriptionNumber;
        if (!string.IsNullOrWhiteSpace(result.TrackingNumber)) TrackingNumberBox.Text = result.TrackingNumber;
        if (DateTime.TryParseExact(result.PrescriptionDate, "dd/MM/yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            PrescriptionDatePicker.SelectedDate = date;
        if (replaceRows)
        {
            Prescriptions.Clear();
            foreach (var item in result.Prescriptions) Prescriptions.Add(item);
        }
        EmptyState.Visibility = Prescriptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionText.Text = result.IsOpen ? result.Message : "E-Reçete Sorgu açılamadı";
        if (!string.IsNullOrWhiteSpace(result.WarningText) || !result.IsOpen)
            SetWarning(result.WarningText ?? result.Message);
    }

    private void ShowDetail(MedulaElectronicPrescriptionDetail detail)
    {
        HeaderTitle.Text = "E-Reçete";
        DetailTitle.Text = detail.Title;
        DetailIdentityText.Text = string.Join("  •  ", new[]
        {
            $"E-Reçete No: {detail.ElectronicPrescriptionNumber}",
            string.IsNullOrWhiteSpace(detail.PatientIdentityNumber) ? string.Empty : $"T.C.: {detail.PatientIdentityNumber}",
            detail.PatientName
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        BackButton.IsEnabled = detail.CanReturn;
        ContinueButton.IsEnabled = detail.CanContinue;
        PopulateFields(PatientFieldsPanel, detail.PatientFields, 245);
        PopulateFields(DoctorFieldsPanel, detail.DoctorFields, 245);
        PopulateFields(PrescriptionFieldsPanel, detail.PrescriptionFields, 250);
        DrugGrid.ItemsSource = detail.Drugs;
        _ = PopulateShelfQuantitiesAsync(detail.Drugs);
        DiagnosisGrid.ItemsSource = detail.Diagnoses;
        SetRemoteTable(ApprovalGrid, ApprovalSection, detail.ApprovalTable,
            row => new ApprovalRow(Cell(row, 0), Cell(row, 1)));
        SignatureText.Text = detail.SignatureText;
        SignaturePanel.Visibility = string.IsNullOrWhiteSpace(detail.SignatureText)
            ? Visibility.Collapsed : Visibility.Visible;
        DetailScrollViewer.ScrollToTop();
        QueryView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        SetModuleTitle(detail.ElectronicPrescriptionNumber);
        SetModuleIdentity($"medula.e-prescription-view.{detail.ElectronicPrescriptionNumber}.{flowIdentity}");
    }

    private async Task PopulateShelfQuantitiesAsync(
        IReadOnlyList<MedulaElectronicPrescriptionDrug> drugs)
    {
        try
        {
            var stocks = await directoryRepository.ReadDrugStockQuantitiesAsync(
                drugs.Select(drug => drug.Barcode), lifetime.Token);
            foreach (var drug in drugs)
                drug.ShelfQuantity = stocks.TryGetValue(drug.Barcode.Trim(), out var quantity)
                    ? quantity.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"))
                    : "0";
            DrugGrid.Items.Refresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // E-prescription detail remains usable if the local stock database is unavailable.
        }
    }

    private void ShowQuery()
    {
        HeaderTitle.Text = "E-Reçete Sorgu";
        DetailView.Visibility = Visibility.Collapsed;
        QueryView.Visibility = Visibility.Visible;
        SetModuleTitle("E-Reçete Sorgu");
        SetModuleIdentity("medula.e-prescription-query");
    }

    private void OnElectronicPrescriptionViewRequested(MedulaElectronicPrescriptionDetail detail)
    {
        if (!detail.IsOpen)
        {
            SetWarning(detail.WarningText ?? detail.Message);
            return;
        }
        activeDetail = detail;
        detailOpenedFromPrescription = true;
        pendingPatientName = detail.PatientName;
        TransformedContentHost.Content = null;
        TransformedContentHost.Visibility = Visibility.Collapsed;
        ShowDetail(detail);
    }

    private void ClearDetailData()
    {
        DetailIdentityText.Text = string.Empty;
        PatientFieldsPanel.Children.Clear();
        DoctorFieldsPanel.Children.Clear();
        PrescriptionFieldsPanel.Children.Clear();
        ApprovalGrid.ItemsSource = null;
        DrugGrid.ItemsSource = null;
        DiagnosisGrid.ItemsSource = null;
        SignatureText.Text = string.Empty;
        SignaturePanel.Visibility = Visibility.Collapsed;
    }

    private void SetModuleTitle(string title)
    {
        title = title.Trim();
        if (title.Length == 0 || string.Equals(CurrentModuleTitle, title, StringComparison.Ordinal)) return;
        CurrentModuleTitle = title;
        ModuleTitleChanged?.Invoke(title);
    }

    private void SetModuleIdentity(string moduleId)
    {
        moduleId = moduleId.Trim();
        if (moduleId.Length == 0 || CurrentModuleId.Equals(moduleId, StringComparison.Ordinal)) return;
        CurrentModuleId = moduleId;
        ModuleIdentityChanged?.Invoke(moduleId);
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static void PopulateFields(
        Panel panel,
        IReadOnlyList<MedulaElectronicPrescriptionDetailField> fields,
        double width)
    {
        panel.Children.Clear();
        foreach (var field in fields)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = field.Label, Foreground = new SolidColorBrush(Color.FromRgb(99, 117, 126)),
                FontSize = 11, FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(field.Value) ? "—" : field.Value,
                Foreground = new SolidColorBrush(Color.FromRgb(39, 57, 68)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });
            panel.Children.Add(new Border
            {
                Width = width, MinHeight = 58, Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(10, 8, 10, 8),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 251)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(224, 231, 234)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Child = content
            });
        }
    }

    private static void SetRemoteTable<TRow>(
        DataGrid grid,
        FrameworkElement section,
        MedulaRemoteTable? table,
        Func<IReadOnlyList<string>, TRow> map)
    {
        var rows = table?.Rows.Select(row => map(row)).ToArray() ?? [];
        grid.ItemsSource = rows;
        section.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : string.Empty;

    private void OnDetailPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void SetWarning(string? warning) => SetMessage(warning, MedulaMessageSeverity.Error);

    private void SetMessage(string? message, MedulaMessageSeverity severity)
    {
        message = message?.Trim();
        WarningText.Text = message ?? string.Empty;
        DetailWarningText.Text = message ?? string.Empty;
        var (background, border, foreground) = severity switch
        {
            MedulaMessageSeverity.Success => (Color.FromRgb(235, 248, 239), Color.FromRgb(144, 203, 160), Color.FromRgb(31, 112, 61)),
            MedulaMessageSeverity.Notice => (Color.FromRgb(255, 248, 224), Color.FromRgb(230, 197, 102), Color.FromRgb(126, 91, 16)),
            _ => (Color.FromRgb(255, 242, 241), Color.FromRgb(232, 184, 180), Color.FromRgb(154, 62, 55))
        };
        foreach (var panel in new[] { WarningPanel, DetailWarningPanel })
        {
            panel.Background = new SolidColorBrush(background);
            panel.BorderBrush = new SolidColorBrush(border);
        }
        WarningText.Foreground = DetailWarningText.Foreground = new SolidColorBrush(foreground);
        var visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        WarningPanel.Visibility = visibility;
        DetailWarningPanel.Visibility = visibility;
    }

    private void SetBusy(bool value, string? text = null)
    {
        busy = value;
        BusyOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(text)) BusyText.Text = text;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private sealed record ApprovalRow(string ApprovalType, string DoctorInformation);
}
