using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using LookupEllipse = System.Windows.Shapes.Ellipse;
using LookupLine = System.Windows.Shapes.Line;
using System.Windows.Threading;
using Microsoft.Win32;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Medula;

public partial class MedulaPrescriptionEntryView : UserControl, IModuleTabRequestSource, IModuleCloseRequestSource
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private const string RemoteActionColumnName = "__MedulaActionId";
    private const string RemotePageColumnName = "__MedulaPageNumber";
    private const string RemoteOrderColumnName = "__MedulaSourceOrder";
    private const string RemoteBranchParentColumnName = "__MedulaBranchParent";
    private const string ReportSgkColorColumnName = "__ReportSgkColor";
    private const string ReportRowKindColumnName = "__ReportRowKind";
    private readonly MedulaAutomation service = MedulaAutomation.Instance;
    private readonly MedulaPrescriptionSnapshot? launchSnapshot;
    private readonly Action? returnToHistoryAction;
    private readonly bool historicalSnapshot;
    private MedulaPrescriptionSnapshot? activeSnapshot;
    private readonly MedulaDirectoryRepository directoryRepository = new();
    private readonly MedulaPrescriptionRepository prescriptionRepository = new();
    private readonly MedulaPrescriptionQrStockRepository prescriptionQrStockRepository = new();
    private readonly MedulaPrescriptionSaleRepository prescriptionSaleRepository = new();
    private readonly LoyalPatientRepository loyalPatientRepository = new();
    private readonly LoyalPatientHistorySyncService loyalPatientHistorySyncService = new();
    private CancellationTokenSource lifetime = new();
    private string browserMessageSurface = string.Empty;
    private bool loadedOnce;
    private bool busy;
    private bool prescriptionScreenReady;
    private readonly Dictionary<string, FrameworkElement> remoteFieldControls = new(StringComparer.Ordinal);
    private string currentRemoteTitle = "Medula";
    private FrameworkElement? remotePopupAnchor;
    private bool inlinePopupBackendOpen;
    private bool suppressInlinePopupCleanup;
    private bool suppressPatientLookup;
    private bool synchronizingIdentity;
    private CancellationTokenSource? ownerSearchCancellation;
    private CancellationTokenSource? recipientSearchCancellation;
    private CancellationTokenSource? ownerVerificationCancellation;
    private CancellationTokenSource? dateValidationCancellation;
    private CancellationTokenSource? drugSearchCancellation;
    private CancellationTokenSource? diagnosisSearchCancellation;
    private CancellationTokenSource? itsQrValidationCancellation;
    private Task barcodeEditTask = Task.CompletedTask;
    private string lastVerifiedOwnerTc = string.Empty;
    private readonly Dictionary<string, Border> activeBrowserMessages = new(StringComparer.Ordinal);
    private bool branchExplicitlySelected;
    private bool suppressDateValidation;
    private IReadOnlyList<string>? medulaDoctorSpecialties;
    private MedulaDiagnosisEntry diagnosisDraftRow = new();
    private PendingPatientReconciliation? pendingPatientReconciliation;
    private PrescriptionAvailabilityMode availabilityMode = PrescriptionAvailabilityMode.Live;
    private bool prescriptionOfflineLookupApproved;
    private MedulaRemoteScreen? activeDocumentScreen;
    private string selectedDocumentFilePath = string.Empty;
    private string latestDocumentBrowserMessage = string.Empty;
    private MedulaMessageSeverity? latestDocumentBrowserMessageSeverity;
    private string[] lastPersistedItsQrCodes = [];
    private readonly HashSet<string> ignoredMissingItsQrCodes = new(StringComparer.Ordinal);
    private bool suppressItsQrTextValidation;
    private bool electronicPrescriptionContext;
    private string prescriptionNumberContext = string.Empty;
    private string electronicPrescriptionNumberContext = string.Empty;
    private readonly HashSet<string> reconciledOpenedPrescriptionKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> loyaltyCheckedPatientTcs = new(StringComparer.Ordinal);
    private static readonly RetailPaymentKind[] ItsPatientPaymentKinds =
    [
        RetailPaymentKind.Cash,
        RetailPaymentKind.Credit,
        RetailPaymentKind.BankTransfer,
        RetailPaymentKind.Debt,
        RetailPaymentKind.Discount
    ];
    private readonly HashSet<RetailPaymentKind> selectedItsPatientPaymentKinds = [];
    private readonly Dictionary<RetailPaymentKind, decimal> itsPatientPaymentAmounts = new()
    {
        [RetailPaymentKind.Cash] = 0m,
        [RetailPaymentKind.Credit] = 0m,
        [RetailPaymentKind.BankTransfer] = 0m,
        [RetailPaymentKind.Debt] = 0m,
        [RetailPaymentKind.Discount] = 0m
    };
    private readonly Dictionary<RetailPaymentKind, ToggleButton> itsPatientPaymentSelectors = [];
    private readonly Dictionary<RetailPaymentKind, MoneyEntryControl> itsPatientPaymentEditors = [];
    private bool suppressItsPatientPaymentChanges;
    private decimal itsPatientPaymentTotal;
    private TextBlock? itsPatientPaymentStatusText;
    private TextBlock? itsDebtDueText;
    private Button? itsSaleNotificationButton;
    private readonly HashSet<string> itsSaleReportedQrCodes = new(StringComparer.Ordinal);
    private bool suppressItsDebtSelectionChanges;
    private MedulaSavedPrescriptionSummary? currentSavedSummary;
    private bool savedPaymentExpanded;
    private bool patientDetailsExpanded;
    private bool doctorDetailsExpanded;
    private bool prescriptionDetailsExpanded;
    private bool diagnosisDetailsExpanded;
    private string loadedPrescriptionPartiesDebtKey = string.Empty;
    private decimal prescriptionPartiesDebtTotal;
    private MedulaPrescriptionPartyDebtSummary prescriptionPartyDebtSummary =
        new(string.Empty, string.Empty, string.Empty, 0m, null, 0m);

    private static readonly HashSet<MedulaPrescriptionCommand> PrescriptionOfflineLookupActions =
    [
        MedulaPrescriptionCommand.PatientDrugs,
        MedulaPrescriptionCommand.PatientReports,
        MedulaPrescriptionCommand.Examinations,
        MedulaPrescriptionCommand.OffLabelCombined,
        MedulaPrescriptionCommand.OffLabelOld,
        MedulaPrescriptionCommand.OffLabelNew
    ];

    private static readonly HashSet<MedulaPrescriptionCommand> PrescriptionOfflineStandaloneActions =
    [
        MedulaPrescriptionCommand.NewPrescription,
        MedulaPrescriptionCommand.ElectronicPrescriptionQuery
    ];

    private enum PrescriptionAvailabilityMode
    {
        Live,
        NetworkOffline,
        PrescriptionOffline
    }

    private sealed record PendingPatientReconciliation(
        string TcIdentityNo,
        string FirstName,
        string LastName,
        string Gender,
        string BirthDate);

    private sealed record BrowserMessageTag(
        MedulaMessageSeverity Severity,
        string Key,
        string Text);

    public sealed record ItsQrCodeDisplay(string Code, bool IsSaleReported)
    {
        public string SaleStatusText => IsSaleReported ? "Evet" : "Hayır";
    }

    public sealed class ItsQrMedicationDisplay(
        string barcode,
        string name,
        string prescribedQuantity,
        string qrQuantity,
        bool isBalanced,
        IEnumerable<string>? qrCodes = null,
        IReadOnlySet<string>? saleReportedQrCodes = null) : INotifyPropertyChanged
    {
        private bool isExpanded;

        public string Barcode { get; } = barcode;
        public string Name { get; } = name;
        public string PrescribedQuantity { get; } = prescribedQuantity;
        public string QrQuantity { get; } = qrQuantity;
        public bool IsBalanced { get; } = isBalanced;
        public ObservableCollection<ItsQrCodeDisplay> QrCodes { get; } =
            new((qrCodes ?? []).Select(code => new ItsQrCodeDisplay(
                code, saleReportedQrCodes?.Contains(RetailQrCodeNormalizer.Normalize(code)) == true)));
        public bool HasQrCodes => QrCodes.Count > 0;
        public bool IsSaleReported => HasQrCodes && QrCodes.All(code => code.IsSaleReported);
        public string SaleStatusText => IsSaleReported ? "Evet" : "Hayır";

        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class RemoteTableRuntime
    {
        public required MedulaRemoteScreen Screen { get; set; }
        public required MedulaRemoteTable Source { get; set; }
        public required DataTable Data { get; init; }
        public required DataGrid Grid { get; init; }
        public required DataView View { get; init; }
        public HashSet<string> RowKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<int> LoadedPageNumbers { get; } = [1];
        public int SequentialLoads { get; set; }
        public int CurrentPage { get; set; } = 1;
        public bool Loading { get; set; }
        public bool Exhausted { get; set; }
        public TextBlock? CountText { get; set; }
        public RemoteTableRuntime? Companion { get; set; }
    }

    public MedulaPrescriptionEntryView()
        : this(null, null)
    {
    }

    public MedulaPrescriptionEntryView(MedulaPrescriptionSnapshot? snapshot)
        : this(snapshot, null)
    {
    }

    public MedulaPrescriptionEntryView(MedulaPrescriptionSnapshot? snapshot, Action? returnToHistory)
    {
        launchSnapshot = snapshot;
        returnToHistoryAction = returnToHistory;
        historicalSnapshot = returnToHistory is not null;
        activeSnapshot = snapshot;
        prescriptionNumberContext = FirstValidPrescriptionNumber(
            snapshot?.Page.FieldValues?.GetValueOrDefault("medula:prescription-no"),
            snapshot?.SourceListItem?.PrescriptionNumber);
        Drugs = new ObservableCollection<MedulaDrugEntry>();
        DrugSuggestions = new ObservableCollection<MedulaDrugSearchOption>();
        Diagnoses = new ObservableCollection<MedulaDiagnosisEntry>();
        DiagnosisSuggestions = new ObservableCollection<MedulaDiagnosisSearchOption>();
        ItsQrMedicationRows = new ObservableCollection<ItsQrMedicationDisplay>();
        Diagnoses.Add(diagnosisDraftRow);
        InitializeComponent();
        InitializeItsDebtSelection();
        OwnerPatientLookupButton.Content = CreatePatientLookupIcon();
        RecipientPatientLookupButton.Content = CreatePatientLookupIcon();
        DataContext = this;
        CollapsePhotoColumn(OwnerPhotoColumn);
        CollapsePhotoColumn(OwnerPhotoSpacerColumn);
        CollapsePhotoColumn(RecipientPhotoColumn);
        CollapsePhotoColumn(RecipientPhotoSpacerColumn);

        PrescriptionDateBox.SelectedDate = DateTime.Today;
        DispensingDateBox.SelectedDate = DateTime.Today;
        FormatDatePickerText(PrescriptionDateBox);
        FormatDatePickerText(DispensingDateBox);
        ApplyMedulaDebugVisibility(ApplicationSettingsStore.Current);
    }

    private static Viewbox CreatePatientLookupIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new LookupEllipse
        {
            Width = 9,
            Height = 9,
            Stroke = new SolidColorBrush(Color.FromRgb(36, 51, 61)),
            StrokeThickness = 2
        });
        Canvas.SetLeft(canvas.Children[^1], 1);
        Canvas.SetTop(canvas.Children[^1], 1);
        canvas.Children.Add(new LookupLine
        {
            X1 = 9,
            Y1 = 9,
            X2 = 15,
            Y2 = 15,
            Stroke = new SolidColorBrush(Color.FromRgb(36, 51, 61)),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        return new Viewbox { Width = 14, Height = 14, Child = canvas };
    }

    private void InitializeItsDebtSelection()
    {
        foreach (var checkBox in new[]
                 {
                     ItsQrTotalDebtCheckBox,
                     ItsQrOwnerDebtCheckBox,
                     ItsQrRecipientDebtCheckBox,
                     ItsQrFamilyDebtCheckBox
                 })
        {
            checkBox.Checked += OnItsDebtSelectionChanged;
            checkBox.Unchecked += OnItsDebtSelectionChanged;
        }
        suppressItsDebtSelectionChanges = true;
        ItsQrTotalDebtCheckBox.IsChecked = true;
        ItsQrOwnerDebtCheckBox.IsChecked = true;
        ItsQrRecipientDebtCheckBox.IsChecked = true;
        ItsQrFamilyDebtCheckBox.IsChecked = true;
        suppressItsDebtSelectionChanges = false;
    }

    public ObservableCollection<MedulaDrugEntry> Drugs { get; }

    public ObservableCollection<MedulaDrugSearchOption> DrugSuggestions { get; }

    public ObservableCollection<MedulaDiagnosisEntry> Diagnoses { get; }

    public ObservableCollection<MedulaDiagnosisSearchOption> DiagnosisSuggestions { get; }

    public ObservableCollection<ItsQrMedicationDisplay> ItsQrMedicationRows { get; }

    public event Action<ModuleTabRequest>? TabRequested;
    public event Action? CloseRequested;
    public event Action<MedulaElectronicPrescriptionDetail>? ElectronicPrescriptionViewRequested;

    private void OnDrugGridSizeChanged(object sender, SizeChangedEventArgs e) =>
        FillDataGridWidth(
            DrugGrid,
            [105, 250, 290, 62, 62, 68, 65, 105, 110],
            [1.1, 3.6, 2.8, 0.75, 0.75, 0.8, 0.8, 1.15, 2.0]);

    private void OnDiagnosisGridSizeChanged(object sender, SizeChangedEventArgs e) =>
        FillDataGridWidth(DiagnosisGrid, [130, 420], [1.2, 6.0]);

    private static void FillDataGridWidth(
        DataGrid grid,
        IReadOnlyList<double> minimums,
        IReadOnlyList<double> weights)
    {
        if (grid.ActualWidth <= 0 || grid.Columns.Count != minimums.Count || weights.Count != minimums.Count)
            return;
        var minimumTotal = minimums.Sum();
        var available = Math.Max(minimumTotal, grid.ActualWidth - 2);
        var extra = Math.Max(0, available - minimumTotal);
        var totalWeight = weights.Sum();
        for (var index = 0; index < grid.Columns.Count; index++)
            grid.Columns[index].Width = new DataGridLength(
                minimums[index] + extra * weights[index] / totalWeight,
                DataGridLengthUnitType.Pixel);
    }

    private static void CollapsePhotoColumn(ColumnDefinition column)
    {
        column.MinWidth = 0;
        column.MaxWidth = 0;
        column.Width = new GridLength(0);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.StatusChanged += OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnBrowserWarningChanged;
        service.ScopedWarningChanged += OnBrowserWarningChanged;
        service.ScopedConfirmationChanged -= OnBrowserConfirmationChanged;
        service.ScopedConfirmationChanged += OnBrowserConfirmationChanged;
        service.ScopedNoticeChanged -= OnBrowserNoticeChanged;
        service.ScopedNoticeChanged += OnBrowserNoticeChanged;
        ApplicationSettingsStore.SettingsChanged -= OnApplicationSettingsChanged;
        ApplicationSettingsStore.SettingsChanged += OnApplicationSettingsChanged;
        ApplyMedulaDebugVisibility(ApplicationSettingsStore.Current);
        if (loadedOnce) return;

        loadedOnce = true;
        prescriptionScreenReady = false;
        if (launchSnapshot is not null)
        {
            ApplyPrescriptionSnapshot(launchSnapshot);
            if (!historicalSnapshot)
                _ = ReconcileOpenedPrescriptionAsync(launchSnapshot);
            return;
        }
        UpdateConnectionDisplay(MedulaConnectionState.Connecting, "Reçete ekranı hazırlanıyor");
        await OpenPrescriptionEntryAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnBrowserWarningChanged;
        service.ScopedConfirmationChanged -= OnBrowserConfirmationChanged;
        service.ScopedNoticeChanged -= OnBrowserNoticeChanged;
        ApplicationSettingsStore.SettingsChanged -= OnApplicationSettingsChanged;
        lifetime.Cancel();
        lifetime.Dispose();
        lifetime = new CancellationTokenSource();
        loadedOnce = false;
        ownerSearchCancellation?.Cancel();
        recipientSearchCancellation?.Cancel();
        ownerVerificationCancellation?.Cancel();
        dateValidationCancellation?.Cancel();
        drugSearchCancellation?.Cancel();
        diagnosisSearchCancellation?.Cancel();
        itsQrValidationCancellation?.Cancel();
        itsQrValidationCancellation?.Dispose();
        itsQrValidationCancellation = null;
    }

    private void OnApplicationSettingsChanged(ApplicationSettings settings) =>
        Dispatcher.InvokeAsync(() =>
        {
            ApplyMedulaDebugVisibility(settings);
            ApplyAutomaticRecipientSelf();
            ApplyPrescriptionDetailVisibility();
            if (currentSavedSummary is not null) ShowSavedPrescriptionSummary(currentSavedSummary);
        });

    private void ApplyMedulaDebugVisibility(ApplicationSettings settings)
    {
        var visible = settings.MedulaDebug;
        MedulaResponsePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DebugResponseSpacer.Width = new GridLength(visible ? 12 : 0);
        DebugResponseColumn.Width = new GridLength(visible ? 310 : 0);
    }

    private void ApplyPrescriptionDetailVisibility()
    {
        var compactEnabled = ApplicationSettingsStore.Current.HideUnimportantPrescriptionDetails;
        var establishedPrescription = electronicPrescriptionContext
            || activeSnapshot?.IsSavedOnMedula == true
            || SavedPrescriptionPanel.Visibility == Visibility.Visible;
        var recipientComplete = HasDisplayValue(RecipientIdentityBox.Text)
            && SelectedValue(RecipientTypeBox, "0") != "0";
        var ownerComplete = HasDisplayValue(PatientIdentityBox.Text)
            && HasDisplayValue(OwnerPatientNameBox.Text)
            && HasDisplayValue(CoverageValueText.Text)
            && HasDisplayValue(PatientTypeValueText.Text);
        var patientComplete = establishedPrescription
            && recipientComplete
            && (electronicPrescriptionContext || ownerComplete);
        var doctorComplete = establishedPrescription
            && HasDisplayValue(RegistrationNumberBox.Text)
            && HasDisplayValue(PhysicianRegistrationBox.Text)
            && HasDisplayValue(PhysicianFirstNameBox.Text)
            && HasDisplayValue(PhysicianLastNameBox.Text)
            && HasDisplayValue(BranchNameText.Text);
        var prescriptionComplete = establishedPrescription
            && HasDisplayValue(ProtocolNumberBox.Text)
            && HasDisplayValue(FacilityCodeBox.Text)
            && PrescriptionDateBox.SelectedDate is not null
            && DispensingDateBox.SelectedDate is not null
            && (!electronicPrescriptionContext || HasDisplayValue(EPrescriptionNumberBox.Text));
        var diagnosisComplete = establishedPrescription;

        PatientCompactSummaryText.Text = string.Join("  •  ", new[]
        {
            OwnerPatientNameBox.Text.Trim(),
            SelectedText(RecipientTypeBox)
        }.Where(HasDisplayValue));
        DoctorCompactSummaryText.Text = string.Join(' ', new[]
        {
            PhysicianFirstNameBox.Text.Trim(),
            PhysicianLastNameBox.Text.Trim()
        }.Where(HasDisplayValue));
        PrescriptionCompactSummaryText.Text = string.Join("  •  ", new[]
        {
            PrescriptionDateBox.SelectedDate?.ToString("dd/MM/yyyy", TurkishCulture) ?? string.Empty,
            electronicPrescriptionContext && HasDisplayValue(EPrescriptionNumberBox.Text)
                ? $"E-Reçete No: {EPrescriptionNumberBox.Text.Trim()}"
                : string.Empty
        }.Where(HasDisplayValue));
        DiagnosisCompactSummaryText.Text = string.Join("  •  ", Diagnoses
            .Where(diagnosis => !ReferenceEquals(diagnosis, diagnosisDraftRow))
            .Select(diagnosis => diagnosis.Code.Trim())
            .Where(HasDisplayValue)
            .Take(6));

        var peopleComplete = patientComplete && doctorComplete;
        var peopleExpanded = patientDetailsExpanded || doctorDetailsExpanded;
        patientDetailsExpanded = peopleExpanded;
        doctorDetailsExpanded = peopleExpanded;
        ConfigureCompactSection(
            PatientInformationContent, PatientInformationToggleButton, PatientCompactSummaryText,
            compactEnabled && peopleComplete, peopleExpanded);
        ConfigureCompactSection(
            DoctorInformationContent, DoctorInformationToggleButton, DoctorCompactSummaryText,
            compactEnabled && peopleComplete, peopleExpanded);
        ConfigureCompactSection(
            PrescriptionInformationContent, PrescriptionInformationToggleButton, PrescriptionCompactSummaryText,
            compactEnabled && prescriptionComplete, prescriptionDetailsExpanded);
        ConfigureCompactSection(
            DiagnosisInformationContent, DiagnosisInformationToggleButton, DiagnosisCompactSummaryText,
            compactEnabled && diagnosisComplete, diagnosisDetailsExpanded);
    }

    private static bool HasDisplayValue(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length > 0 && text is not "?" and not "—" and not "-";
    }

    private static void ConfigureCompactSection(
        UIElement content,
        Button toggle,
        UIElement summary,
        bool eligible,
        bool expanded)
    {
        toggle.Visibility = eligible ? Visibility.Visible : Visibility.Collapsed;
        var collapsed = eligible && !expanded;
        content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        summary.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        toggle.Content = collapsed ? "▶" : "▼";
        toggle.ToolTip = collapsed ? "Bilgileri göster" : "Bilgileri gizle";
    }

    private void OnPatientInformationToggleClick(object sender, RoutedEventArgs e)
    {
        patientDetailsExpanded = doctorDetailsExpanded =
            PatientInformationContent.Visibility != Visibility.Visible;
        ApplyPrescriptionDetailVisibility();
    }

    private void OnDoctorInformationToggleClick(object sender, RoutedEventArgs e)
    {
        patientDetailsExpanded = doctorDetailsExpanded =
            DoctorInformationContent.Visibility != Visibility.Visible;
        ApplyPrescriptionDetailVisibility();
    }

    private void OnPrescriptionInformationToggleClick(object sender, RoutedEventArgs e)
    {
        prescriptionDetailsExpanded = PrescriptionInformationContent.Visibility != Visibility.Visible;
        ApplyPrescriptionDetailVisibility();
    }

    private void OnDiagnosisInformationToggleClick(object sender, RoutedEventArgs e)
    {
        diagnosisDetailsExpanded = DiagnosisInformationContent.Visibility != Visibility.Visible;
        ApplyPrescriptionDetailVisibility();
    }

    private void OnSavedPaymentToggleClick(object sender, RoutedEventArgs e)
    {
        savedPaymentExpanded = !savedPaymentExpanded;
        ShowSavedPrescriptionSummary(currentSavedSummary);
    }

    private void OnServiceStatusChanged(object? sender, MedulaConnectionStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (historicalSnapshot || IsPrescriptionOfflineSnapshot(activeSnapshot))
            {
                availabilityMode = PrescriptionAvailabilityMode.PrescriptionOffline;
                UpdateConnectionDisplay(
                    MedulaConnectionState.Failed,
                    "Reçete çevrimdışı · Medula'da aktif değil");
                ApplyAvailabilityPolicy();
                return;
            }
            if (activeSnapshot is not null
                && status.State is MedulaConnectionState.Disconnected
                    or MedulaConnectionState.Failed
                    or MedulaConnectionState.TemporarilyOffline)
            {
                availabilityMode = PrescriptionAvailabilityMode.NetworkOffline;
                UpdateConnectionDisplay(
                    MedulaConnectionState.TemporarilyOffline,
                    $"Geçici çevrimdışı · {status.Message.Replace("Geçici çevrimdışı · ", string.Empty, StringComparison.OrdinalIgnoreCase)}");
                ApplyAvailabilityPolicy();
                return;
            }
            if (status.State == MedulaConnectionState.Connected)
                availabilityMode = PrescriptionAvailabilityMode.Live;
            var state = status.State == MedulaConnectionState.Connected && !prescriptionScreenReady
                ? MedulaConnectionState.Connecting
                : status.State;
            var message = state == MedulaConnectionState.Connecting && status.State == MedulaConnectionState.Connected
                ? "Reçete ekranı hazırlanıyor"
                : status.Message;
            UpdateConnectionDisplay(state, message);
            ApplyAvailabilityPolicy();
        });
    }

    private void OnBrowserWarningChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.Prescription) return;
        if (Dispatcher.CheckAccess())
        {
            SetScopedPrescriptionMessage(message.Text, MedulaMessageSeverity.Error);
            return;
        }
        Dispatcher.InvokeAsync(
            () => SetScopedPrescriptionMessage(message.Text, MedulaMessageSeverity.Error),
            DispatcherPriority.Send);
    }

    private void SetBrowserWarning(string? warning)
    {
        SetBrowserMessage(warning, MedulaMessageSeverity.Error);
    }

    private void OnBrowserConfirmationChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.Prescription) return;
        if (Dispatcher.CheckAccess())
        {
            SetScopedPrescriptionMessage(message.Text, MedulaMessageSeverity.Success);
            return;
        }
        Dispatcher.InvokeAsync(
            () => SetScopedPrescriptionMessage(message.Text, MedulaMessageSeverity.Success),
            DispatcherPriority.Send);
    }

    private void SetBrowserConfirmation(string? confirmation)
    {
        SetBrowserMessage(confirmation, MedulaMessageSeverity.Success);
    }

    private void OnBrowserNoticeChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.Prescription) return;
        if (Dispatcher.CheckAccess())
        {
            SetScopedPrescriptionMessage(message.Text, MedulaMessageSeverity.Notice);
            return;
        }
        Dispatcher.InvokeAsync(
            () => SetScopedPrescriptionMessage(message.Text, MedulaMessageSeverity.Notice),
            DispatcherPriority.Send);
    }

    private void SetScopedPrescriptionMessage(string? message, MedulaMessageSeverity severity)
    {
        if (DocumentPopupLayer.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(message))
        {
            latestDocumentBrowserMessage = message.Trim();
            latestDocumentBrowserMessageSeverity = severity;
            DocumentMessageText.Text = message;
            DocumentMessageText.Foreground = new SolidColorBrush(severity switch
            {
                MedulaMessageSeverity.Success => Color.FromRgb(23, 107, 77),
                MedulaMessageSeverity.Notice => Color.FromRgb(158, 103, 16),
                _ => Color.FromRgb(180, 52, 52)
            });
            return;
        }

        switch (severity)
        {
            case MedulaMessageSeverity.Success:
                SetBrowserConfirmation(message);
                break;
            case MedulaMessageSeverity.Notice:
                SetBrowserNotice(message);
                break;
            default:
                SetBrowserWarning(message);
                break;
        }
    }

    private void SetBrowserNotice(string? notice)
    {
        SetBrowserMessage(notice, MedulaMessageSeverity.Notice);
    }

    private void SetBrowserMessage(string? message, MedulaMessageSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            foreach (var existingBorder in BrowserMessageStack.Children.OfType<Border>()
                         .Where(item => item.Tag is BrowserMessageTag tag && tag.Severity == severity)
                         .ToArray())
                RemoveBrowserMessage(existingBorder);
            return;
        }

        var text = message.Trim();
        var key = $"{severity}:{text}";
        if (activeBrowserMessages.TryGetValue(key, out var existing))
        {
            existing.BeginAnimation(OpacityProperty, null);
            existing.Opacity = 1;
            return;
        }

        if (severity == MedulaMessageSeverity.Error)
        {
            var duplicate = BrowserMessageStack.Children.OfType<Border>()
                .FirstOrDefault(item => item.Tag is BrowserMessageTag tag
                    && tag.Severity == severity
                    && MedulaPrescriptionMessageRules.AreDuplicateErrors(tag.Text, text));
            if (duplicate?.Tag is BrowserMessageTag duplicateTag)
            {
                var preferred = MedulaPrescriptionMessageRules.PreferDetailedError(
                    duplicateTag.Text, text);
                if (preferred.Equals(duplicateTag.Text, StringComparison.Ordinal))
                {
                    duplicate.BeginAnimation(OpacityProperty, null);
                    duplicate.Opacity = 1;
                    RefreshDrugErrorIndicators();
                    return;
                }
                RemoveBrowserMessage(duplicate);
            }
        }

        var colors = severity switch
        {
            MedulaMessageSeverity.Success => (Background: Color.FromRgb(232, 247, 240), Border: Color.FromRgb(119, 185, 155), Foreground: Color.FromRgb(23, 107, 77)),
            MedulaMessageSeverity.Notice => (Background: Color.FromRgb(255, 247, 214), Border: Color.FromRgb(215, 182, 73), Foreground: Color.FromRgb(118, 91, 8)),
            _ => (Background: Color.FromRgb(253, 236, 236), Border: Color.FromRgb(214, 107, 107), Foreground: Color.FromRgb(161, 40, 40))
        };
        var border = new Border
        {
            MaxWidth = severity == MedulaMessageSeverity.Error ? 640 : 290,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(11, 6, 11, 6),
            CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(colors.Background),
            BorderBrush = new SolidColorBrush(colors.Border),
            BorderThickness = new Thickness(1),
            Tag = new BrowserMessageTag(severity, key, text),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(colors.Foreground),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = severity == MedulaMessageSeverity.Error
                    ? TextWrapping.Wrap
                    : TextWrapping.NoWrap,
                TextTrimming = severity == MedulaMessageSeverity.Error
                    ? TextTrimming.None
                    : TextTrimming.CharacterEllipsis,
                ToolTip = text
            }
        };
        activeBrowserMessages[key] = border;
        BrowserMessageStack.Children.Add(border);
        RefreshDrugErrorIndicators();
        if (severity != MedulaMessageSeverity.Error)
            _ = FadeAndRemoveAsync(border, () => RemoveBrowserMessage(border));
    }

    private void RemoveBrowserMessage(Border border)
    {
        if (border.Tag is BrowserMessageTag tag) activeBrowserMessages.Remove(tag.Key);
        BrowserMessageStack.Children.Remove(border);
        RefreshDrugErrorIndicators();
    }

    private void ChangeBrowserMessageSurface(string surface)
    {
        surface = surface.Trim();
        if (surface.Equals(browserMessageSurface, StringComparison.Ordinal)) return;
        browserMessageSurface = surface;
        activeBrowserMessages.Clear();
        BrowserMessageStack.Children.Clear();
        RefreshDrugErrorIndicators();
    }

    private void RefreshDrugErrorIndicators()
    {
        var errorMessages = BrowserMessageStack.Children.OfType<Border>()
            .Select(item => item.Tag)
            .OfType<BrowserMessageTag>()
            .Where(tag => tag.Severity == MedulaMessageSeverity.Error)
            .Select(tag => tag.Text)
            .ToArray();
        MedulaPrescriptionMessageRules.ApplyBarcodeErrors(Drugs, errorMessages);
        DrugGrid.Items.Refresh();
    }

    private async Task FadeAndRemoveAsync(UIElement element, Action remove)
    {
        try
        {
            var totalSeconds = Math.Clamp(
                ApplicationSettingsStore.Current.NotificationDisplaySeconds, 1, 120);
            var fadeSeconds = Math.Min(1, totalSeconds);
            var holdSeconds = totalSeconds - fadeSeconds;
            if (holdSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(holdSeconds), lifetime.Token);
            if (!element.IsVisible)
            {
                remove();
                return;
            }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(fadeSeconds));
            fade.Completed += (_, _) => remove();
            element.BeginAnimation(OpacityProperty, fade);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateConnectionDisplay(MedulaConnectionState state, string message)
    {
        ConnectionText.Text = MedulaStatusText.For(state);
        StatusDot.Fill = state switch
        {
            MedulaConnectionState.Connected => new SolidColorBrush(Color.FromRgb(13, 148, 136)),
            MedulaConnectionState.TemporarilyOffline => new SolidColorBrush(Color.FromRgb(210, 139, 44)),
            MedulaConnectionState.Failed => new SolidColorBrush(Color.FromRgb(198, 67, 67)),
            _ => new SolidColorBrush(Color.FromRgb(210, 139, 44))
        };
    }

    private void OnRegistrationNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Keyboard.ClearFocus();
        DoctorSearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void OnPrescriptionDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DatePicker picker) FormatDatePickerText(picker);
        if (suppressDateValidation) return;
        ScheduleDateValidation(250);
    }

    private void OnPrescriptionDateLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ScheduleDateValidation(0);

    private void OnDateTextBoxPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not DatePickerTextBox textBox
            || string.IsNullOrEmpty(e.Text)
            || e.Text.Any(character => !char.IsDigit(character) && character is not ('/' or '.')))
            return;

        var current = textBox.Text ?? string.Empty;
        var selectionStart = Math.Clamp(textBox.SelectionStart, 0, current.Length);
        var selectionLength = Math.Clamp(textBox.SelectionLength, 0, current.Length - selectionStart);
        textBox.Text = current.Remove(selectionStart, selectionLength).Insert(selectionStart, e.Text);
        textBox.CaretIndex = selectionStart + e.Text.Length;
        e.Handled = true;
    }

    private void OnDateTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Insert) e.Handled = true;
    }

    private void OnPrescriptionMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || PrescriptionScrollViewer.ScrollableHeight <= 0) return;
        PrescriptionScrollViewer.ScrollToVerticalOffset(
            PrescriptionScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private async void ScheduleDateValidation(int delayMilliseconds)
    {
        if (!loadedOnce || suppressDateValidation) return;
        dateValidationCancellation?.Cancel();
        dateValidationCancellation?.Dispose();
        dateValidationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = dateValidationCancellation.Token;
        try
        {
            if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, token);
            if (NormalizeMedulaDate(PrescriptionDateBox) is null
                || NormalizeMedulaDate(DispensingDateBox) is null)
            {
                DateValidationText.Text = "Tarihleri gün/ay/yıl biçiminde girin.";
                DateValidationText.Visibility = Visibility.Visible;
                return;
            }

            var result = await service.SynchronizePrescriptionDatesAsync(BuildInput(), token);
            ShowResult(result);
            DateValidationText.Text = result.IsSuccess ? string.Empty : result.Message;
            DateValidationText.Visibility = result.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void FormatDatePickerText(DatePicker picker)
    {
        if (picker.SelectedDate is DateTime date)
            picker.Text = date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void OnReturnToEntryClick(object sender, RoutedEventArgs e) =>
        await OpenPrescriptionEntryAsync();

    private void OnShowBrowserClick(object sender, RoutedEventArgs e) => service.ShowBrowser();

    private async void OnMedulaRefreshClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (activeSnapshot is { IsLocalArchive: true, SourceListItem: null })
        {
            SetBrowserWarning("Bu reçete yalnızca yerel geçmişte bulunuyor; Medula'da canlı olarak yenilenemez.");
            return;
        }
        SetBusy(true, "Medula reçete ekranı yenileniyor…");
        SetBrowserWarning(null);
        try
        {
            if (activeSnapshot is { } snapshot)
            {
                var refreshed = await service.RefreshPrescriptionContextAsync(
                    BuildInput(),
                    OwnerPatientNameBox.Text.Trim(),
                    snapshot.PrescriptionUid,
                    snapshot.VersionNumber,
                    lifetime.Token);
                if (refreshed is null)
                {
                    SetBrowserWarning("Kayıtlı reçete Medula'da yeniden açılamadı.");
                    return;
                }
                activeSnapshot = refreshed;
                ApplyPrescriptionSnapshot(refreshed);
                _ = ReconcileOpenedPrescriptionAsync(refreshed);
                return;
            }

            var result = await service.RefreshPrescriptionEntryAsync(lifetime.Token);
            prescriptionScreenReady = result.IsPrescriptionEntryPage;
            ShowResult(result);
            UpdateConnectionDisplay(
                prescriptionScreenReady ? MedulaConnectionState.Connected : MedulaConnectionState.Failed,
                prescriptionScreenReady ? "Reçete Giriş ekranı yenilendi." : result.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button button
            || !TryGetPrescriptionCommand(button.Tag, out var command)) return;

        var caption = button.Content?.ToString() ?? "Medula";
        if (!await CanExecuteForAvailabilityAsync(command)) return;
        await CommitDrugGridEditsAsync();

        if (command is MedulaPrescriptionCommand.NewPrescription
            or MedulaPrescriptionCommand.ElectronicPrescriptionQuery)
        {
            await OpenStandalonePrescriptionTabAsync(command, caption);
            return;
        }

        if (command is MedulaPrescriptionCommand.PreviousPrescription
            or MedulaPrescriptionCommand.NextPrescription)
        {
            await OpenAdjacentPrescriptionTabAsync(command, caption);
            return;
        }

        if (command == MedulaPrescriptionCommand.OffLabelCombined)
        {
            remotePopupAnchor = null;
            OffLabelTabsPanel.Visibility = Visibility.Visible;
            await OpenRemoteScreenAsync(MedulaPrescriptionCommand.OffLabelOld);
            return;
        }

        if (MedulaActionCatalog.Get(command).SubScreenTitle is not null)
        {
            remotePopupAnchor = button;
            OffLabelTabsPanel.Visibility = Visibility.Collapsed;
            await OpenRemoteScreenAsync(command);
            return;
        }

        var autoAcceptSaveConfirmation = command == MedulaPrescriptionCommand.SavePrescription
            && ApplicationSettingsStore.Current.SkipPrescriptionSaveConfirmation;
        await RunActionAsync(command, caption, confirmationAccepted: autoAcceptSaveConfirmation);
    }

    private async void OnViewElectronicPrescriptionClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var electronicPrescriptionNumber = EPrescriptionNumberBox.Text.Trim();
        if (electronicPrescriptionNumber.Length == 0)
        {
            SetBrowserWarning("E-Reçete numarası bulunamadı.");
            return;
        }

        SetBusy(true, "E-Reçete ekranına dönülüyor…");
        SetBrowserWarning(null);
        try
        {
            var detail = await service.ReturnToElectronicPrescriptionDetailAsync(
                PatientIdentityBox.Text,
                electronicPrescriptionNumber,
                lifetime.Token);
            if (!detail.IsOpen)
            {
                SetBrowserWarning(detail.WarningText ?? detail.Message);
                return;
            }
            if (ElectronicPrescriptionViewRequested is not null)
            {
                ElectronicPrescriptionViewRequested.Invoke(detail);
            }
            else
            {
                TabRequested?.Invoke(new ModuleTabRequest(
                    $"medula.e-prescription-view.{electronicPrescriptionNumber}",
                    electronicPrescriptionNumber,
                    () => new MedulaElectronicPrescriptionQueryView(detail),
                    980,
                    600,
                    CreateElectronicPrescriptionIcon));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> CanExecuteForAvailabilityAsync(MedulaPrescriptionCommand command)
    {
        if (availabilityMode == PrescriptionAvailabilityMode.NetworkOffline)
        {
            SetBrowserWarning("Medula geçici olarak çevrimdışı. Oturum ve reçete ekranı geri yüklenene kadar bu işlem kullanılamaz.");
            return false;
        }
        if (availabilityMode != PrescriptionAvailabilityMode.PrescriptionOffline) return true;
        if (PrescriptionOfflineStandaloneActions.Contains(command)) return true;
        if (!PrescriptionOfflineLookupActions.Contains(command))
        {
            SetBrowserWarning("Bu reçete Medula'da aktif olmadığı için reçeteyi değiştiren işlem kullanılamaz.");
            return false;
        }

        var accepted = ShowTurkishConfirmation(
            "Reçete aktif olarak Medulada olmadığı için hastanın ilaçlarına bakmak için Reçete Gir ekranı kullanılacaktır, hastanın reçetesinin olup olmadığına ve tercihine bağlı olarak bu aksiyon KVKK'ya karşı gelebilir, yine de ilerlemek ister misiniz?",
            "Reçete Çevrimdışı");
        if (!accepted) return false;

        var entry = await service.OpenPrescriptionEntryAsync(lifetime.Token);
        if (entry.IsPrescriptionEntryPage)
        {
            prescriptionOfflineLookupApproved = true;
            return true;
        }
        SetBrowserWarning("Medula Reçete Gir ekranı açılamadı; işlem gönderilmedi.");
        return false;
    }

    private async Task OpenStandalonePrescriptionTabAsync(
        MedulaPrescriptionCommand command,
        string caption)
    {
        SetBusy(true, $"{caption} açılıyor…");
        try
        {
            var result = await service.ExecutePrescriptionCommandAsync(
                BuildInput(), command, caption, cancellationToken: lifetime.Token);
            if (!result.IsSuccess)
            {
                SetBrowserWarning(result.WarningText ?? result.Message);
                return;
            }
            if (command == MedulaPrescriptionCommand.NewPrescription)
            {
                TabRequested?.Invoke(new ModuleTabRequest(
                    $"medula.prescription-entry.{Guid.NewGuid():N}",
                    "Medula Reçete Giriş",
                    () => new MedulaPrescriptionEntryView(),
                    1180,
                    680,
                    CreatePrescriptionIcon));
            }
            else
            {
                TabRequested?.Invoke(new ModuleTabRequest(
                    $"medula.e-prescription-query.{Guid.NewGuid():N}",
                    "E-Reçete Sorgu",
                    () => new MedulaElectronicPrescriptionQueryView(),
                    980,
                    600,
                    CreateElectronicPrescriptionIcon));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OpenAdjacentPrescriptionTabAsync(
        MedulaPrescriptionCommand command,
        string caption)
    {
        if (activeSnapshot?.SourceListItem is null)
        {
            SetBrowserWarning("Önceki veya sonraki reçete yalnızca Medula'da açık kayıtlı bir reçeteden seçilebilir.");
            return;
        }
        SetBusy(true, $"{caption} açılıyor…");
        try
        {
            var snapshot = await service.OpenAdjacentPrescriptionAsync(
                BuildInput(),
                command == MedulaPrescriptionCommand.PreviousPrescription
                    ? MedulaAdjacentDirection.Previous
                    : MedulaAdjacentDirection.Next,
                lifetime.Token);
            if (snapshot is null)
            {
                SetBrowserWarning($"{caption} Medula'da açılamadı.");
                return;
            }

            try
            {
                var record = MedulaPrescriptionPersistenceMapper.FromSnapshot(snapshot);
                var inserted = await prescriptionRepository.InsertVersionIfChangedAsync(record, lifetime.Token);
                snapshot = snapshot with
                {
                    PrescriptionUid = inserted.PrescriptionUid,
                    VersionNumber = inserted.VersionNumber,
                    StoredFilterDate = record.MedulaFilterDate
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SetBrowserWarning($"Reçete açıldı ancak yerel geçmişe aktarılamadı: {exception.Message}");
            }

            var number = snapshot.Page.FieldValues?
                .GetValueOrDefault("medula:prescription-no")?.Trim() ?? "0";
            var identity = snapshot.PrescriptionUid?.ToString("N") ?? Guid.NewGuid().ToString("N");
            var openedSnapshot = snapshot;
            PrescriptionScrollViewer.ScrollToTop();
            TabRequested?.Invoke(new ModuleTabRequest(
                $"medula.prescription-view.{identity}",
                $"Reçete {number}",
                () => new MedulaPrescriptionEntryView(openedSnapshot),
                1180,
                680,
                CreatePrescriptionIcon));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static FrameworkElement CreatePrescriptionIcon() => CreateTabIcon("R", Color.FromRgb(35, 94, 115));

    private static FrameworkElement CreateElectronicPrescriptionIcon() => CreateTabIcon("E", Color.FromRgb(12, 94, 136));

    private static FrameworkElement CreateTabIcon(string text, Color color) => new Border
    {
        Width = 16,
        Height = 16,
        Background = new SolidColorBrush(color),
        CornerRadius = new CornerRadius(0),
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private async void OnDrugQuickEntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || availabilityMode != PrescriptionAvailabilityMode.Live || sender is not TextBox box) return;
        DrugQuickEntryPlaceholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        drugSearchCancellation?.Cancel();
        drugSearchCancellation?.Dispose();
        drugSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        DrugSuggestionPopup.IsOpen = false;
        DrugSuggestions.Clear();
        var query = box.Text.Trim();
        if (query.Length < 3 || !query.Any(char.IsLetter)) return;

        try
        {
            var token = drugSearchCancellation.Token;
            await Task.Delay(250, token);
            await CommitDrugGridEditsAsync();
            SetBusy(true, "İlaç aranıyor…");
            var result = await service.SearchPrescriptionDrugsAsync(BuildInput(), query, token);
            if (box.Text.Trim() != query) return;
            foreach (var option in result.Options) DrugSuggestions.Add(option);
            DrugSuggestionPopup.IsOpen = result.Options.Count > 0;
            ShowDrugFeedback(result.IsSuccess, result.Message, result.WarningText);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDrugSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: MedulaDrugSearchOption option } list) return;
        list.SelectedItem = null;
        DrugSuggestionPopup.IsOpen = false;
        drugSearchCancellation?.Cancel();
        SetBusy(true, "İlaç reçeteye ekleniyor…");
        try
        {
            var result = await service.SelectPrescriptionDrugAsync(BuildInput(), option, lifetime.Token);
            ApplyDrugActionResult(result, replaceRows: result.IsSuccess || result.Drugs.Count > 0);
            if (result.IsSuccess)
            {
                DrugQuickEntryBox.Text = string.Empty;
                DrugQuickEntryBox.Focus();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnDrugQuickEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (DrugSuggestionList.SelectedItem is MedulaDrugSearchOption option)
        {
            DrugSuggestionList.SelectedItem = null;
            _ = SelectDrugSuggestionAsync(option);
            return;
        }
        _ = AddDrugBarcodeAsync();
    }

    private async void OnAddDrugBarcodeClick(object sender, RoutedEventArgs e) =>
        await AddDrugBarcodeAsync();

    private async Task SelectDrugSuggestionAsync(MedulaDrugSearchOption option)
    {
        DrugSuggestionPopup.IsOpen = false;
        drugSearchCancellation?.Cancel();
        SetBusy(true, "İlaç reçeteye ekleniyor…");
        try
        {
            var result = await service.SelectPrescriptionDrugAsync(BuildInput(), option, lifetime.Token);
            ApplyDrugActionResult(result, replaceRows: result.IsSuccess || result.Drugs.Count > 0);
            if (result.IsSuccess) DrugQuickEntryBox.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task AddDrugBarcodeAsync()
    {
        if (busy) return;
        var barcode = DrugQuickEntryBox.Text.Trim();
        if (barcode.Length == 0) return;
        DrugSuggestionPopup.IsOpen = false;
        drugSearchCancellation?.Cancel();
        await CommitDrugGridEditsAsync();
        SetBusy(true, "Barkod Medula'ya ekleniyor…");
        try
        {
            var result = await service.AddPrescriptionDrugBarcodeAsync(
                BuildInput(), barcode, lifetime.Token);
            ApplyDrugActionResult(result, replaceRows: result.IsSuccess || result.Drugs.Count > 0);
            if (result.IsSuccess) DrugQuickEntryBox.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDiagnosisSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || availabilityMode != PrescriptionAvailabilityMode.Live || sender is not TextBox box) return;
        DiagnosisSearchPlaceholder.Visibility = box.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        diagnosisSearchCancellation?.Cancel();
        DiagnosisSuggestions.Clear();
        DiagnosisSuggestionPopup.IsOpen = false;
        var query = box.Text.Trim();
        if (query.Length < 3) return;

        diagnosisSearchCancellation?.Dispose();
        diagnosisSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            await Task.Delay(250, diagnosisSearchCancellation.Token);
            await SearchDiagnosesAsync(query, selectExactMatch: false, diagnosisSearchCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnDiagnosisSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchOrSelectDiagnosisAsync();
    }

    private async void OnDiagnosisAddClick(object sender, RoutedEventArgs e) =>
        await SearchOrSelectDiagnosisAsync();

    private async Task SearchOrSelectDiagnosisAsync()
    {
        if (busy) return;
        var query = DiagnosisSearchBox.Text.Trim();
        if (query.Length < 3)
        {
            ShowDrugFeedback(false, "ICD-10 aramak için en az 3 karakter girin.", null);
            return;
        }

        var exact = DiagnosisSuggestions.FirstOrDefault(option =>
            option.Code.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            await SelectDiagnosisAsync(exact);
            return;
        }

        diagnosisSearchCancellation?.Cancel();
        diagnosisSearchCancellation?.Dispose();
        diagnosisSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            await SearchDiagnosesAsync(query, selectExactMatch: true, diagnosisSearchCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SearchDiagnosesAsync(
        string query,
        bool selectExactMatch,
        CancellationToken cancellationToken)
    {
        SetBusy(true, "ICD-10 tanısı aranıyor…");
        try
        {
            var result = await service.SearchPrescriptionDiagnosesAsync(
                BuildInput(), query, cancellationToken);
            if (!DiagnosisSearchBox.Text.Trim().Equals(query, StringComparison.Ordinal)) return;
            DiagnosisSuggestions.Clear();
            foreach (var option in result.Options) DiagnosisSuggestions.Add(option);
            ShowDrugFeedback(result.IsSuccess, result.Message, result.WarningText);

            var exact = result.Options.FirstOrDefault(option =>
                option.Code.Equals(query, StringComparison.OrdinalIgnoreCase));
            if (selectExactMatch && exact is not null)
            {
                await SelectDiagnosisAsync(exact);
                return;
            }
            DiagnosisSuggestionPopup.IsOpen = result.Options.Count > 0;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDiagnosisSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: MedulaDiagnosisSearchOption option } list) return;
        list.SelectedItem = null;
        await SelectDiagnosisAsync(option);
    }

    private async Task SelectDiagnosisAsync(MedulaDiagnosisSearchOption option)
    {
        DiagnosisSuggestionPopup.IsOpen = false;
        diagnosisSearchCancellation?.Cancel();
        SetBusy(true, "ICD-10 tanısı reçeteye ekleniyor…");
        try
        {
            var result = await service.SelectPrescriptionDiagnosisAsync(option, lifetime.Token);
            if (result.IsSuccess || result.Diagnoses.Count > 0)
            {
                ReplaceDiagnoses(result.Diagnoses);
            }
            ShowDrugFeedback(result.IsSuccess, result.Message, result.WarningText);
            if (result.IsSuccess) DiagnosisSearchBox.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnDiagnosisGridBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (!ReferenceEquals(e.Row.Item, diagnosisDraftRow)) e.Cancel = true;
    }

    private async void OnDiagnosisGridRowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || !ReferenceEquals(e.Row.Item, diagnosisDraftRow)) return;
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (string.IsNullOrWhiteSpace(diagnosisDraftRow.Code)
            && string.IsNullOrWhiteSpace(diagnosisDraftRow.Description)) return;
        SetBusy(true, "ICD-10 satırı Medula'ya aktarılıyor…");
        try
        {
            var result = await service.SetPrescriptionDiagnosisEntryAsync(
                BuildInput(), diagnosisDraftRow, lifetime.Token);
            if (result.IsSuccess || result.Diagnoses.Count > 0)
                ReplaceDiagnoses(result.Diagnoses);
            ShowDrugFeedback(result.IsSuccess, result.Message, result.WarningText);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ReplaceDiagnoses(IEnumerable<MedulaDiagnosisEntry> diagnoses)
    {
        Diagnoses.Clear();
        foreach (var diagnosis in diagnoses.Where(item =>
                     !string.IsNullOrWhiteSpace(item.Code) || !string.IsNullOrWhiteSpace(item.Description)))
            Diagnoses.Add(diagnosis);
        diagnosisDraftRow = new MedulaDiagnosisEntry();
        Diagnoses.Add(diagnosisDraftRow);
    }

    private void OnDrugGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(DrugGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row is null) return;
        DrugGrid.SelectedItem = row.Item;
        row.IsSelected = true;
        row.Focus();
    }

    private void OnDrugGridCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || e.Column.SortMemberPath != nameof(MedulaDrugEntry.Barcode)
            || e.Row.Item is not MedulaDrugEntry drug)
            return;

        var editor = e.EditingElement as TextBox
            ?? FindVisualChildren<TextBox>(e.EditingElement).FirstOrDefault();
        if (editor is null) return;

        var previousBarcode = drug.Barcode;
        var previousName = drug.Name;
        var barcode = editor.Text.Trim();
        if (string.Equals(previousBarcode, barcode, StringComparison.Ordinal)) return;
        if (barcode.Length == 0)
        {
            e.Cancel = true;
            editor.Text = previousBarcode;
            ShowDrugFeedback(false, "Barkod girin.", null);
            return;
        }

        var rowIndex = Drugs.IndexOf(drug);
        if (rowIndex < 0) return;
        drug.Barcode = barcode;
        drug.Name = string.Empty;
        barcodeEditTask = UpdateDrugBarcodeCellAsync(
            drug, rowIndex, previousBarcode, previousName, barcode);
    }

    private async Task UpdateDrugBarcodeCellAsync(
        MedulaDrugEntry drug,
        int rowIndex,
        string previousBarcode,
        string previousName,
        string barcode)
    {
        SetBusy(true, "Barkod Medula'da güncelleniyor…");
        try
        {
            var result = await service.UpdatePrescriptionDrugBarcodeAsync(
                BuildInput(), rowIndex, barcode, lifetime.Token);
            if (!result.IsSuccess && result.Drugs.Count == 0)
            {
                drug.Barcode = previousBarcode;
                drug.Name = previousName;
                DrugGrid.Items.Refresh();
            }
            ApplyDrugActionResult(result, replaceRows: result.IsSuccess || result.Drugs.Count > 0);
        }
        catch (OperationCanceledException)
        {
            drug.Barcode = previousBarcode;
            drug.Name = previousName;
            DrugGrid.Items.Refresh();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CommitDrugGridEditsAsync()
    {
        DrugGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DrugGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await barcodeEditTask;
    }

    private async void OnDrugContextActionClick(object sender, RoutedEventArgs e)
    {
        if (busy || availabilityMode != PrescriptionAvailabilityMode.Live
            || sender is not MenuItem { Header: object header } menuItem
            || !TryGetDrugRowCommand(menuItem.Tag, out var command)
            || DrugGrid.SelectedItem is not MedulaDrugEntry selected) return;
        var rowIndex = Drugs.IndexOf(selected);
        if (rowIndex < 0) return;
        var title = header.ToString() ?? "İlaç";
        await CommitDrugGridEditsAsync();
        SetBusy(true, $"{title} açılıyor…");
        try
        {
            var result = await service.ExecuteDrugRowCommandAsync(
                BuildInput(), rowIndex, command, lifetime.Token);
            var replaceRows = command == MedulaDrugRowCommand.Delete
                || (result.Screen is null && result.Drugs.Count > 0);
            ApplyDrugActionResult(result, replaceRows);
            if (result.Screen is not null)
            {
                var screen = result.Screen.Tables.Any(table => table.Id is
                        "f:tableExRecetedeYazilanIlacList" or "f:tableExHastayaVerilenIlacList")
                    ? await AddEquivalentStockAsync(result.Screen)
                    : result.Screen;
                remotePopupAnchor = DrugGrid;
                ShowRemoteScreen(screen);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<MedulaRemoteScreen> AddEquivalentStockAsync(MedulaRemoteScreen screen)
    {
        var barcodes = screen.Tables.SelectMany(table => table.Rows)
            .Select(row => row.Count > 0 ? row[0] : string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();
        var stocks = await directoryRepository.ReadDrugStockQuantitiesAsync(barcodes, lifetime.Token);
        var tables = screen.Tables.Select(table => table with
        {
            Rows = table.Rows.Select(row =>
            {
                var values = row.ToArray();
                if (values.Length >= 6)
                    values[5] = stocks.TryGetValue(values[0], out var quantity)
                        ? quantity.ToString("N0")
                        : "0";
                return (IReadOnlyList<string>)values;
            }).ToArray()
        }).ToArray();
        return screen with { Tables = tables };
    }

    private void ApplyDrugActionResult(MedulaDrugActionResult result, bool replaceRows)
    {
        if (replaceRows)
        {
            Drugs.Clear();
            foreach (var drug in result.Drugs) Drugs.Add(drug);
        }
        _ = PopulateDrugShelfQuantitiesAsync(Drugs.ToArray());
        ShowDrugFeedback(result.IsSuccess, result.Message, result.WarningText);
    }

    private async Task PopulateDrugShelfQuantitiesAsync(IReadOnlyList<MedulaDrugEntry> drugs)
    {
        try
        {
            var stocks = await directoryRepository.ReadDrugStockQuantitiesAsync(
                drugs.Select(drug => drug.Barcode), lifetime.Token);
            foreach (var drug in drugs)
                drug.ShelfQuantity = stocks.TryGetValue(drug.Barcode.Trim(), out var quantity)
                    ? quantity.ToString("N0", TurkishCulture)
                    : "0";
            DrugGrid.Items.Refresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Medula remains usable if the local inventory database is temporarily unavailable.
        }
    }

    private void ShowDrugFeedback(bool success, string message, string? warning)
    {
        if (warning is not null) SetBrowserWarning(warning);
        ResponseMessageText.Text = message;
        ResponseBanner.Background = success
            ? new SolidColorBrush(Color.FromRgb(232, 245, 242))
            : new SolidColorBrush(Color.FromRgb(252, 235, 235));
        ResponseMessageText.Foreground = success
            ? new SolidColorBrush(Color.FromRgb(49, 84, 79))
            : new SolidColorBrush(Color.FromRgb(143, 48, 48));
    }

    private async Task OpenRemoteScreenAsync(MedulaPrescriptionCommand command)
    {
        var descriptor = MedulaActionCatalog.Get(command);
        var title = descriptor.SubScreenTitle
            ?? throw new ArgumentOutOfRangeException(nameof(command), command, "Command does not open a sub-screen.");
        service.RequestForegroundPriority();
        SetBusy(true, $"{title} ekranı açılıyor…");
        try
        {
            if (command == MedulaPrescriptionCommand.ItsQrOperations
                && !await EnsureActivePrescriptionIdentityAsync())
                return;
            currentRemoteTitle = title;
            var screen = await service.OpenPrescriptionSubScreenAsync(
                BuildActionInput(command), command, lifetime.Token);
            if (screen.IsOpen)
            {
                ShowRemoteScreen(screen);
                if (screen.Key is "off-label-old" or "off-label-new")
                    await ReconcileOwnerFromMedulaAsync(screen);
            }
            else
                ShowRemoteScreenFailure(screen);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> EnsureActivePrescriptionIdentityAsync()
    {
        if (activeSnapshot?.PrescriptionUid is not null) return true;

        try
        {
            var input = BuildInput();
            var captured = activeSnapshot ?? await service.CaptureCurrentPrescriptionAsync(
                    OwnerPatientNameBox.Text.Trim(),
                    null,
                    null,
                    0,
                    lifetime.Token);
            if (captured is null)
            {
                SetBrowserWarning("Kayıtlı reçete bilgileri Medula ekranından okunamadı.");
                return false;
            }

            captured = PreservePrescriptionIdentity(captured);
            captured = PromoteToSavedPrescriptionContext(captured, input) with
            {
                StoredFilterDate = activeSnapshot?.StoredFilterDate
            };
            var record = MedulaPrescriptionPersistenceMapper.FromSnapshot(captured, input);
            if (string.IsNullOrWhiteSpace(record.MedulaPrescriptionNo))
            {
                SetBrowserWarning("ITS işlemi için Medula reçete numarası okunamadı.");
                return false;
            }

            var inserted = await prescriptionRepository.InsertVersionIfChangedAsync(
                record, lifetime.Token);
            var source = captured.SourceListItem is null
                ? null
                : captured.SourceListItem with
                {
                    PrescriptionUid = inserted.PrescriptionUid,
                    RecordId = inserted.RecordId,
                    VersionNumber = inserted.VersionNumber,
                    IsStoredLocally = true
                };
            activeSnapshot = captured with
            {
                SourceListItem = source,
                PrescriptionUid = inserted.PrescriptionUid,
                VersionNumber = inserted.VersionNumber,
                IsLocalArchive = false,
                StoredFilterDate = record.MedulaFilterDate
            };
            service.RememberSavedPrescriptionContext(activeSnapshot);
            ResponseUrlText.ToolTip =
                $"Reçete yerel kimliği doğrulandı · Kayıt: {inserted.RecordId} · Sürüm: {inserted.VersionNumber}";
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetBrowserWarning($"Reçetenin yerel kimliği doğrulanamadı: {exception.Message}");
            return false;
        }
    }

    private async void OnOffLabelTabClick(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button button
            || !TryGetPrescriptionCommand(button.Tag, out var command)) return;
        await OpenRemoteScreenAsync(command);
    }

    private async void OnRemoteActionClick(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: MedulaRemoteAction action }) return;
        service.RequestForegroundPriority();

        if (action.Id == "__app:return-drugs")
        {
            SetBusy(true, "İlaçlar ekranına dönülüyor…");
            try
            {
                await service.ReturnToPrescriptionAsync(lifetime.Token);
                var screen = await service.OpenPrescriptionSubScreenAsync(
                    BuildInput(), MedulaPrescriptionCommand.PatientDrugs, lifetime.Token);
                if (screen.IsOpen) ShowRemoteScreen(screen);
                else ShowRemoteActionFailure(screen);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetBusy(false);
            }
            return;
        }

        SetBusy(true, $"{action.Caption} işlemi Medula'ya gönderiliyor…");
        try
        {
            var payload = BuildRemoteFieldPayload();
            var activeScreen = InlineRemotePopup.IsOpen
                ? InlineRemotePopup.Tag as MedulaRemoteScreen
                : RemoteScreenLayer.Tag as MedulaRemoteScreen;
            if (activeScreen?.Key == "its-qr"
                && action.Caption.Contains("Satış Bildirimi", StringComparison.CurrentCultureIgnoreCase))
            {
                await ExecuteItsSaleNotificationAsync(activeScreen, action, payload);
                return;
            }
            if (activeScreen?.Key == "its-qr"
                && action.Caption.Contains("Satış İptal", StringComparison.CurrentCultureIgnoreCase))
            {
                await ExecuteItsSaleCancellationAsync(activeScreen, action, payload);
                return;
            }
            var confirmationAccepted = false;
            if (activeScreen?.Key == "uts-operations"
                && action.Caption.Equals(
                    "Yeni Kayıt Ekle/Seçilen Kaydı Güncelle",
                    StringComparison.CurrentCultureIgnoreCase))
            {
                confirmationAccepted = ShowTurkishConfirmation(
                    "ÜTS kaydı Medula üzerinde eklenecek veya seçili kayıt güncellenecek. Devam etmek istiyor musunuz?",
                    "ÜTS Kaydını Onayla");
                if (!confirmationAccepted) return;
            }
            var screen = await service.ExecuteSubScreenActionAsync(
                activeScreen?.Key ?? string.Empty,
                currentRemoteTitle,
                action.Id,
                payload,
                lifetime.Token,
                confirmationAccepted);
            if (activeScreen?.Key == "report-selection"
                && action.Caption.Equals("Tamam", StringComparison.OrdinalIgnoreCase)
                && screen.RemoteSuccessConfirmed)
            {
                CloseInlinePopupWithoutCleanup();
                ChangeBrowserMessageSurface(CurrentPrescriptionMessageSurface());
                var snapshot = await service.CaptureCurrentPrescriptionAsync(
                    OwnerPatientNameBox.Text.Trim(),
                    activeSnapshot?.SourceListItem,
                    activeSnapshot?.PrescriptionUid,
                    activeSnapshot?.VersionNumber ?? 0,
                    lifetime.Token);
                if (snapshot is not null)
                {
                    activeSnapshot = snapshot;
                    ApplyPrescriptionSnapshot(snapshot);
                }
                ShowDrugFeedback(true, screen.Message, null);
                return;
            }
            if (activeScreen?.Key == "treatment-scheme"
                && action.Caption.Equals("Tamam", StringComparison.OrdinalIgnoreCase)
                && screen.RemoteSuccessConfirmed)
            {
                CloseInlinePopupWithoutCleanup();
                ChangeBrowserMessageSurface(CurrentPrescriptionMessageSurface());
                var snapshot = await service.CaptureCurrentPrescriptionAsync(
                    OwnerPatientNameBox.Text.Trim(),
                    activeSnapshot?.SourceListItem,
                    activeSnapshot?.PrescriptionUid,
                    activeSnapshot?.VersionNumber ?? 0,
                    lifetime.Token);
                if (snapshot is not null)
                {
                    activeSnapshot = snapshot;
                    ApplyPrescriptionSnapshot(snapshot);
                }
                ShowDrugFeedback(true, screen.Message, null);
                return;
            }
            if (activeScreen?.Key == "its-qr"
                && action.Caption.Equals("Listele", StringComparison.CurrentCultureIgnoreCase)
                && IsSuccessfulItsQrValidation(screen))
                await PersistItsQrCodesAsync(ParseItsQrCodes(payload.Values.GetValueOrDefault("form1:textarea1")), lifetime.Token);
            if (screen.IsOpen)
            {
                ShowRemoteScreen(screen);
            }
            else
                ShowRemoteActionFailure(screen);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExecuteItsSaleNotificationAsync(
        MedulaRemoteScreen activeScreen,
        MedulaRemoteAction action,
        MedulaRemoteFieldPayload payload)
    {
        if (activeSnapshot?.PrescriptionUid is not { } prescriptionUid)
        {
            SetBrowserWarning("ITS satış bildirimi için kayıtlı reçete kimliği bulunamadı.");
            return;
        }

        var qrCodes = ParseItsQrCodes(payload.Values.GetValueOrDefault("form1:textarea1"));
        var checkoutPayments = BuildItsPatientPaymentInputs();
        var debtCollection = SelectedItsDebtCollection();
        var checkoutTotal = ItsCheckoutTotal();
        if (checkoutTotal > 0m
            && (checkoutPayments.Count == 0
                || checkoutPayments.Any(payment => payment.Amount <= 0m)
                || decimal.Round(checkoutPayments.Sum(payment => payment.Amount), 2)
                    != decimal.Round(checkoutTotal, 2)))
        {
            SetBrowserWarning("Ödeme türleri toplamı reçete ve seçilen borç tahsilatına eşit olmalıdır.");
            return;
        }
        IReadOnlyList<RetailSalePaymentInput> prescriptionPayments;
        try
        {
            prescriptionPayments = MedulaPrescriptionCheckoutRules.PrescriptionPaymentsOnly(
                itsPatientPaymentTotal, debtCollection, checkoutPayments);
        }
        catch (InvalidOperationException exception)
        {
            SetBrowserWarning(exception.Message);
            return;
        }
        MedulaPrescriptionSalePreparation preparation;
        try
        {
            var pendingRecord = MedulaPrescriptionPersistenceMapper.FromSnapshot(
                activeSnapshot,
                BuildInput()) with
            {
                QrCodes = qrCodes,
                PrescriptionUid = prescriptionUid
            };
            await prescriptionSaleRepository.EnsurePendingSaleAsync(
                pendingRecord, prescriptionUid, lifetime.Token);
            await prescriptionSaleRepository.ConfigureItsPatientPaymentsAsync(
                prescriptionUid, prescriptionPayments, lifetime.Token);
            preparation = await prescriptionSaleRepository.PrepareItsSaleAsync(
                prescriptionUid,
                qrCodes,
                CurrentUserId(),
                lifetime.Token,
                ignoredMissingItsQrCodes);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetBrowserWarning(exception.Message);
            return;
        }

        try
        {
            var screen = await service.ExecuteSubScreenActionAsync(
                activeScreen.Key,
                activeScreen.Title,
                action.Id,
                payload,
                lifetime.Token);
            if (!string.IsNullOrWhiteSpace(screen.ConfirmationMessage))
            {
                if (!ShowTurkishConfirmation(screen.ConfirmationMessage, "ITS Satış Bildirimi"))
                {
                    await prescriptionSaleRepository.MarkCancelledAsync(
                        preparation.OperationId, lifetime.Token);
                    ShowRemoteScreen(screen with { ConfirmationMessage = null });
                    return;
                }

                screen = await service.ExecuteSubScreenActionAsync(
                    activeScreen.Key,
                    activeScreen.Title,
                    action.Id,
                    payload,
                    lifetime.Token,
                    confirmationAccepted: true);
            }

            if (!screen.RemoteSuccessConfirmed)
            {
                await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                    preparation.OperationId,
                    screen.Message,
                    lifetime.Token);
                ShowRemoteActionFailure(screen);
                return;
            }

            await prescriptionSaleRepository.MarkRemoteConfirmedAsync(
                preparation.OperationId, screen.Message, lifetime.Token);
            SetItsQrSaleReported(qrCodes, true);
            try
            {
                await prescriptionSaleRepository.CompleteItsSaleAsync(
                    preparation.OperationId,
                    screen.Message,
                    prescriptionPayments,
                    debtCollection,
                    PatientIdentityBox.Text,
                    RecipientIdentityBox.Text,
                    lifetime.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                    preparation.OperationId, exception.Message, lifetime.Token);
                SetBrowserWarning(
                    $"ITS satışı Medula'da tamamlandı ancak yerel satış mutabakat bekliyor: {exception.Message}");
                return;
            }

            SetBrowserConfirmation("ITS satış bildirimi Medula ve yerel stokta tamamlandı.");
            if (screen.IsOpen) ShowRemoteScreen(screen);
            else
            {
                await service.ReturnToPrescriptionAsync(lifetime.Token);
                var refreshed = await service.RefreshPrescriptionEntryAsync(lifetime.Token);
                ShowResult(refreshed);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                preparation.OperationId, exception.Message, CancellationToken.None);
            SetBrowserWarning(exception.Message);
        }
    }

    private async Task ExecuteItsSaleCancellationAsync(
        MedulaRemoteScreen activeScreen,
        MedulaRemoteAction action,
        MedulaRemoteFieldPayload payload)
    {
        if (activeSnapshot?.PrescriptionUid is not { } prescriptionUid)
        {
            SetBrowserWarning("ITS satış iptali için kayıtlı reçete kimliği bulunamadı.");
            return;
        }

        var listAction = activeScreen.Actions.FirstOrDefault(candidate =>
            candidate.Caption.Equals("Listele", StringComparison.CurrentCultureIgnoreCase));
        if (listAction is not null)
        {
            var listedScreen = await service.ExecuteSubScreenActionAsync(
                activeScreen.Key, activeScreen.Title, listAction.Id, payload, lifetime.Token);
            if (!listedScreen.IsOpen)
            {
                ShowRemoteActionFailure(listedScreen);
                return;
            }
            activeScreen = listedScreen;
            action = activeScreen.Actions.FirstOrDefault(candidate =>
                candidate.Caption.Contains("Satış İptal", StringComparison.CurrentCultureIgnoreCase))
                ?? action;
            payload = new MedulaRemoteFieldPayload(
                activeScreen.Fields
                    .Where(field => !field.IsReadOnly
                        && !field.Kind.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(field => field.Id, field => field.Value, StringComparer.Ordinal),
                activeScreen.Fields
                    .Where(field => !field.IsReadOnly
                        && field.Kind.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(field => field.Id, field =>
                        field.Value.Equals("true", StringComparison.OrdinalIgnoreCase) || field.Value == "1",
                        StringComparer.Ordinal));
        }

        var qrCodes = ParseItsQrCodes(payload.Values.GetValueOrDefault("form1:textarea1"));
        MedulaPrescriptionSalePreparation preparation;
        try
        {
            preparation = await prescriptionSaleRepository.PrepareItsSaleCancellationAsync(
                prescriptionUid, qrCodes, CurrentUserId(), lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetBrowserWarning(exception.Message);
            return;
        }

        try
        {
            var screen = await service.ExecuteSubScreenActionAsync(
                activeScreen.Key, activeScreen.Title, action.Id, payload, lifetime.Token);
            if (!string.IsNullOrWhiteSpace(screen.ConfirmationMessage))
            {
                if (!ShowTurkishConfirmation(screen.ConfirmationMessage, "ITS Satış İptali"))
                {
                    await prescriptionSaleRepository.MarkCancelledAsync(
                        preparation.OperationId, lifetime.Token);
                    ShowRemoteScreen(screen with { ConfirmationMessage = null });
                    return;
                }
                screen = await service.ExecuteSubScreenActionAsync(
                    activeScreen.Key, activeScreen.Title, action.Id, payload,
                    lifetime.Token, confirmationAccepted: true);
            }

            if (!screen.RemoteSuccessConfirmed)
            {
                if (IsDefinitiveItsCancellationFailure(screen.Message))
                {
                    await prescriptionSaleRepository.MarkFailedAsync(
                        preparation.OperationId, screen.Message, lifetime.Token);
                }
                else
                {
                    await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                        preparation.OperationId, screen.Message, lifetime.Token);
                }
                ShowRemoteActionFailure(screen);
                return;
            }

            await prescriptionSaleRepository.MarkRemoteConfirmedAsync(
                preparation.OperationId, screen.Message, lifetime.Token);
            SetItsQrSaleReported(qrCodes, false);
            int refundId;
            try
            {
                refundId = await prescriptionSaleRepository.CompleteItsSaleCancellationAsync(
                    preparation.OperationId, screen.Message, lifetime.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                    preparation.OperationId, exception.Message, lifetime.Token);
                SetBrowserWarning(
                    $"ITS satış iptali Medula'da tamamlandı ancak yerel iade mutabakat bekliyor: {exception.Message}");
                return;
            }

            SetBrowserConfirmation($"ITS satış iptali tamamlandı. Yerel iade no: G{refundId}");
            await service.ReturnToPrescriptionAsync(lifetime.Token);
            var refreshed = await service.RefreshPrescriptionEntryAsync(lifetime.Token);
            ShowResult(refreshed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                preparation.OperationId, exception.Message, CancellationToken.None);
            SetBrowserWarning(exception.Message);
        }
    }

    private static bool IsDefinitiveItsCancellationFailure(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("İTS'DEN GELEN HATA", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("ITS'DEN GELEN HATA", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("başarısız", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("yapılamadı", StringComparison.CurrentCultureIgnoreCase));

    private MedulaRemoteFieldPayload BuildRemoteFieldPayload()
    {
        var values = remoteFieldControls.ToDictionary(
            pair => pair.Key,
            pair => pair.Value switch
            {
                ComboBox comboBox => comboBox.SelectedValue?.ToString() ?? string.Empty,
                TextBox textBox => textBox.Text,
                _ => string.Empty
            },
            StringComparer.Ordinal);
        var checks = remoteFieldControls
            .Where(pair => pair.Value is CheckBox)
            .ToDictionary(
                pair => pair.Key,
                pair => ((CheckBox)pair.Value).IsChecked == true,
                StringComparer.Ordinal);
        return new MedulaRemoteFieldPayload(values, checks);
    }

    private void OnItsQrTextBoxKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox) return;
        ScheduleItsQrValidation(textBox, TimeSpan.Zero);
    }

    private void OnItsQrEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitItsQrEntry();
    }

    private void OnItsQrTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressItsQrTextValidation
            || sender is not TextBox textBox
            || lastPersistedItsQrCodes.Length == 0) return;
        var current = ParseItsQrCodes(textBox.Text);
        if (lastPersistedItsQrCodes.Except(current, StringComparer.Ordinal).Any())
            ScheduleItsQrValidation(textBox, TimeSpan.FromMilliseconds(450));
    }

    private void ScheduleItsQrValidation(TextBox textBox, TimeSpan delay)
    {
        if ((RemoteScreenLayer.Tag as MedulaRemoteScreen)?.Key != "its-qr") return;
        itsQrValidationCancellation?.Cancel();
        itsQrValidationCancellation?.Dispose();
        itsQrValidationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = ValidateItsQrCodesAsync(textBox, delay, itsQrValidationCancellation.Token);
    }

    private async Task ValidateItsQrCodesAsync(
        TextBox sourceTextBox,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            while (busy)
                await Task.Delay(50, cancellationToken);
            if (!IsLoaded || (RemoteScreenLayer.Tag as MedulaRemoteScreen) is not { Key: "its-qr" } screen)
                return;
            if (!await ValidateItsQrStockAsync(sourceTextBox, cancellationToken)) return;
            var listAction = screen.Actions.FirstOrDefault(action =>
                action.Caption.Equals("Listele", StringComparison.CurrentCultureIgnoreCase));
            if (listAction is null) return;

            var restoreFocus = sourceTextBox.IsKeyboardFocusWithin;
            var restoreCustomFocus = ReferenceEquals(sourceTextBox, ItsQrBackendTextBox);
            var caretIndex = sourceTextBox.CaretIndex;
            var selectionStart = sourceTextBox.SelectionStart;
            var selectionLength = sourceTextBox.SelectionLength;
            SetBusy(true, "Karekodlar Medula'da doğrulanıyor…");
            var payload = BuildRemoteFieldPayload();
            service.RequestForegroundPriority();
            var result = await service.ExecuteSubScreenActionAsync(
                screen.Key,
                screen.Title,
                listAction.Id,
                payload,
                cancellationToken);
            if (IsSuccessfulItsQrValidation(result))
                await PersistItsQrCodesAsync(
                    ParseItsQrCodes(payload.Values.GetValueOrDefault("form1:textarea1")),
                    cancellationToken);
            if (result.IsOpen)
            {
                ShowRemoteScreen(result);
                if (restoreCustomFocus)
                {
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        ItsQrEntryTextBox.Clear();
                        ItsQrEntryTextBox.Focus();
                        Keyboard.Focus(ItsQrEntryTextBox);
                    }));
                }
                else if (restoreFocus
                    && remoteFieldControls.TryGetValue("form1:textarea1", out var refreshedEditor)
                    && refreshedEditor is TextBox refreshedTextBox)
                {
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        refreshedTextBox.Focus();
                        Keyboard.Focus(refreshedTextBox);
                        var length = refreshedTextBox.Text.Length;
                        refreshedTextBox.Select(
                            Math.Clamp(selectionStart, 0, length),
                            Math.Clamp(selectionLength, 0, Math.Max(0, length - selectionStart)));
                        refreshedTextBox.CaretIndex = Math.Clamp(caretIndex, 0, length);
                    }));
                }
            }
            else ShowRemoteActionFailure(result);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) SetBusy(false);
        }
    }

    private static bool IsSuccessfulItsQrValidation(MedulaRemoteScreen screen) =>
        screen.IsOpen && (screen.Message.Equals("Medula ekranı güncellendi.", StringComparison.Ordinal)
            || screen.Message.Equals("ITS Karekod İşlemleri ekranı hazır.", StringComparison.Ordinal));

    private async Task PersistItsQrCodesAsync(string[] qrCodes, CancellationToken cancellationToken)
    {
        if (activeSnapshot is null) return;
        var hasChanged = !qrCodes.SequenceEqual(lastPersistedItsQrCodes, StringComparer.Ordinal);
        try
        {
            if (!hasChanged)
            {
                if (IsUnfinalizedSavedPrescription(activeSnapshot)
                    && activeSnapshot.PrescriptionUid is { } existingUid)
                    await prescriptionQrStockRepository.SynchronizeAsync(existingUid, qrCodes, cancellationToken);
                return;
            }
            var fields = new Dictionary<string, string>(
                activeSnapshot.Page.FieldValues ?? new Dictionary<string, string>(),
                StringComparer.Ordinal)
            {
                ["medula:its-qr-codes"] = string.Join('\n', qrCodes)
            };
            var updated = activeSnapshot with
            {
                Page = activeSnapshot.Page with { FieldValues = fields }
            };
            var record = MedulaPrescriptionPersistenceMapper.FromSnapshot(updated, BuildInput()) with
            {
                QrCodes = qrCodes
            };
            var inserted = await prescriptionRepository.InsertVersionAsync(record, cancellationToken);
            activeSnapshot = updated with
            {
                PrescriptionUid = inserted.PrescriptionUid,
                VersionNumber = inserted.VersionNumber,
                IsLocalArchive = false
            };
            if (IsUnfinalizedSavedPrescription(activeSnapshot))
                await prescriptionQrStockRepository.SynchronizeAsync(
                    inserted.PrescriptionUid, qrCodes, cancellationToken);
            lastPersistedItsQrCodes = qrCodes.ToArray();
            ResponseUrlText.ToolTip =
                $"ITS karekod değişikliği veritabanına kaydedildi · Kayıt: {inserted.RecordId} · Sürüm: {inserted.VersionNumber}";
            SetBrowserConfirmation("ITS karekod değişikliği reçete geçmişine kaydedildi.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetBrowserWarning($"ITS karekod değişikliği Medula'da doğrulandı ancak reçete geçmişine kaydedilemedi: {exception.Message}");
        }
    }

    private static string[] ParseItsQrCodes(string? text) =>
        (text ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<bool> ValidateItsQrStockAsync(
        TextBox sourceTextBox,
        CancellationToken cancellationToken)
    {
        if (activeSnapshot is null || !IsUnfinalizedSavedPrescription(activeSnapshot)) return true;
        var entered = ParseItsQrCodes(sourceTextBox.Text);
        var prescriptionUid = activeSnapshot.PrescriptionUid ?? Guid.Empty;
        var validation = await prescriptionQrStockRepository.ValidateAsync(
            entered, prescriptionUid, cancellationToken);
        var missing = validation.MissingCodes
            .Where(code => !ignoredMissingItsQrCodes.Contains(code))
            .ToArray();
        var reserved = validation.ReservedByAnotherPrescription
            .Where(code => !ignoredMissingItsQrCodes.Contains(code))
            .ToArray();
        if (missing.Length == 0 && reserved.Length == 0) return true;

        var details = new List<string>();
        if (missing.Length > 0)
            details.Add("Aktif stokta bulunmayan karekodlar:\n" + string.Join('\n', missing));
        if (reserved.Length > 0)
            details.Add("Başka bir reçetede ayrılmış karekodlar:\n" + string.Join('\n', reserved));
        var rejected = missing.Concat(reserved).ToHashSet(StringComparer.Ordinal);
        var remove = ShowQrStockWarning(string.Join("\n\n", details));
        if (!remove)
        {
            ignoredMissingItsQrCodes.UnionWith(rejected);
            return true;
        }

        var retained = entered.Where(code =>
            !rejected.Contains(RetailQrCodeNormalizer.Normalize(code))).ToArray();
        suppressItsQrTextValidation = true;
        try
        {
            sourceTextBox.Text = string.Join(Environment.NewLine, retained);
            sourceTextBox.CaretIndex = sourceTextBox.Text.Length;
        }
        finally
        {
            suppressItsQrTextValidation = false;
        }
        return retained.Length > 0 || lastPersistedItsQrCodes.Length > 0;
    }

    private static bool IsUnfinalizedSavedPrescription(MedulaPrescriptionSnapshot snapshot)
    {
        var fields = snapshot.Page.FieldValues ?? new Dictionary<string, string>();
        var status = fields.GetValueOrDefault("medula:qr-status")?.Trim() ?? string.Empty;
        return !MedulaPrescriptionListTypes.IsItsFinalized(status)
            && (snapshot.SourceListItem?.IsQrUnfinalized == true
                || status.Contains("Sonlandırılmadı", StringComparison.CurrentCultureIgnoreCase));
    }

    private async void OnCloseRemoteScreenClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true, "Reçete ekranına dönülüyor…");
        try
        {
            var result = await service.ReturnToPrescriptionAsync(lifetime.Token);
            RemoteScreenLayer.Visibility = Visibility.Collapsed;
            RemoteScreenLayer.Tag = null;
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnReturnToRemoteParentClick(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: string target }) return;
        SetBusy(true, target switch
        {
            "drugs" => "İlaçlar ekranına dönülüyor…",
            "prescription" => "Reçete ekranına dönülüyor…",
            _ => "Raporlar ekranına dönülüyor…"
        });
        try
        {
            await service.ReturnToPrescriptionAsync(lifetime.Token);
            if (target == "prescription")
            {
                RemoteScreenLayer.Visibility = Visibility.Collapsed;
                RemoteScreenLayer.Tag = null;
                ChangeBrowserMessageSurface(CurrentPrescriptionMessageSurface());
                var snapshot = await service.CaptureCurrentPrescriptionAsync(
                    OwnerPatientNameBox.Text.Trim(),
                    activeSnapshot?.SourceListItem,
                    activeSnapshot?.PrescriptionUid,
                    activeSnapshot?.VersionNumber ?? 0,
                    lifetime.Token);
                if (snapshot is not null)
                {
                    activeSnapshot = snapshot;
                    ApplyPrescriptionSnapshot(snapshot);
                }
                return;
            }
            var command = target == "drugs"
                ? MedulaPrescriptionCommand.PatientDrugs
                : MedulaPrescriptionCommand.PatientReports;
            var screen = await service.OpenPrescriptionSubScreenAsync(
                BuildInput(), command, lifetime.Token);
            if (screen.IsOpen) ShowRemoteScreen(screen);
            else ShowRemoteActionFailure(screen);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnInlineRemotePopupClosed(object? sender, EventArgs e)
    {
        if (suppressInlinePopupCleanup || !inlinePopupBackendOpen) return;
        inlinePopupBackendOpen = false;
        ChangeBrowserMessageSurface(CurrentPrescriptionMessageSurface());
        try
        {
            if (InlineRemotePopup.Tag is MedulaRemoteScreen { Key: "drug-special" or "drug-equivalent" } drugScreen)
                await service.ClosePrescriptionDrugOverlayAsync(drugScreen.Key, lifetime.Token);
            else if (InlineRemotePopup.Tag is MedulaRemoteScreen { Key: "facility-search" or "branch-select" or "doctor-branch-select" or "report-selection" or "treatment-scheme" or "new-warning-code" } inlineScreen)
                await service.ClosePrescriptionInlineScreenAsync(inlineScreen.Key, lifetime.Token);
            else
                await service.ReturnToPrescriptionAsync(lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnInlineRemoteCloseClick(object sender, RoutedEventArgs e)
    {
        if (InlineRemotePopup.IsOpen) InlineRemotePopup.IsOpen = false;
    }

    private void ShowRemoteScreen(MedulaRemoteScreen screen)
    {
        var previousScreen = RemoteScreenLayer.Tag as MedulaRemoteScreen;
        var enteringItsQrScreen = MedulaSubScreenSafety.IsEnteringItsQrScreen(
            screen.Key,
            previousScreen?.Key,
            RemoteScreenLayer.Visibility == Visibility.Visible);
        ChangeBrowserMessageSurface($"remote:{screen.Key}");
        RemoteScreenLayer.Tag = screen;
        if (screen.Key == "its-qr")
        {
            ShowItsQrRetailScreen(screen, enteringItsQrScreen);
            return;
        }
        ItsQrRetailPanel.Visibility = Visibility.Collapsed;
        RemoteContentScrollViewer.Visibility = Visibility.Visible;
        RemoteActionsPanel.Visibility = Visibility.Visible;
        var inline = IsInlineRemoteScreen(screen.Key);
        var hasEquivalentTables = screen.Tables.Any(table => table.Id is
            "f:tableExRecetedeYazilanIlacList" or "f:tableExHastayaVerilenIlacList");
        WrapPanel actionsPanel;
        WrapPanel fieldsPanel;
        StackPanel tablesPanel;
        if (inline)
        {
            RemoteScreenLayer.Visibility = Visibility.Collapsed;
            InlineRemotePopup.Tag = screen;
            InlineRemoteActionsPanel.Children.Clear();
            InlineRemoteBottomActionsPanel.Children.Clear();
            InlineRemoteCard.Width = screen.Key switch
            {
                "facility-search" => 930,
                "facility-departments" => 720,
                "warning-code" => 720,
                "drug-equivalent" => 1540,
                "drug-special" when hasEquivalentTables => 1540,
                "report-selection" => 1040,
                "treatment-scheme" => 1120,
                "new-warning-code" => 1120,
                "uts-operations" => 1180,
                _ => 860
            };
            InlineRemoteCard.MaxHeight = hasEquivalentTables
                ? 860
                : screen.Key is "treatment-scheme" or "new-warning-code" or "uts-operations" ? 780 : 620;
            InlineRemoteTitleText.Text = screen.Title;
            InlineRemoteMessageText.Text = RemoteMessageForDisplay(screen);
            InlineRemoteMessageText.Visibility = string.IsNullOrWhiteSpace(InlineRemoteMessageText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            actionsPanel = screen.Key == "uts-operations"
                ? InlineRemoteBottomActionsPanel
                : InlineRemoteActionsPanel;
            fieldsPanel = InlineRemoteFieldsPanel;
            tablesPanel = InlineRemoteTablesPanel;
        }
        else
        {
            CloseInlinePopupWithoutCleanup();
            RemoteScreenLayer.Visibility = Visibility.Visible;
            RemoteTitleText.Text = BuildRemoteScreenTitle(screen);
            RemoteMessageText.Text = RemoteMessageForDisplay(screen);
            RemoteMessageText.Visibility = string.IsNullOrWhiteSpace(RemoteMessageText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            actionsPanel = RemoteActionsPanel;
            fieldsPanel = RemoteFieldsPanel;
            tablesPanel = RemoteTablesPanel;
        }
        currentRemoteTitle = screen.Title;
        RemoteParentButton.Visibility = screen.Key is "eye-drugs" or "report-detail" or "drug-report-detail"
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoteParentButton.Content = screen.Key switch
        {
            "eye-drugs" => "İlaçlara Dön",
            _ => "Raporlara Dön"
        };
        RemoteParentButton.Tag = screen.Key switch
        {
            "eye-drugs" => "drugs",
            _ => "reports"
        };
        remoteFieldControls.Clear();
        itsSaleNotificationButton = null;
        actionsPanel.Children.Clear();
        fieldsPanel.Children.Clear();
        tablesPanel.Children.Clear();
        fieldsPanel.Visibility = screen.Fields.Count == 0
                && (screen.TreatmentRows?.Count ?? 0) == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        var facilitySearchAction = screen.Key == "facility-search"
            ? screen.Actions.FirstOrDefault(action => action.Caption.Equals("Ara", StringComparison.OrdinalIgnoreCase))
            : null;
        var displayActions = screen.Actions
            .Where(action => screen.Key != "eye-drugs"
                || !action.Caption.Equals("Göz İlaçları", StringComparison.OrdinalIgnoreCase))
            .Where(action => action != facilitySearchAction)
            .ToArray();
        foreach (var action in displayActions)
        {
            var button = new Button
            {
                Content = action.Caption,
                Tag = action,
                ToolTip = string.IsNullOrWhiteSpace(action.Context) ? null : action.Context,
                Style = (Style)FindResource("ActionButton")
            };
            button.Click += OnRemoteActionClick;
            actionsPanel.Children.Add(button);
            if (screen.Key == "its-qr"
                && action.Caption.Contains("Satış Bildirimi", StringComparison.CurrentCultureIgnoreCase))
                itsSaleNotificationButton = button;
        }
        actionsPanel.Visibility = displayActions.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (screen.Key == "report-new")
        {
            var saveButton = new Button
            {
                Content = "Kaydet",
                IsEnabled = false,
                ToolTip = "Kaydetme bu aşamada bilerek devre dışıdır.",
                Style = (Style)FindResource("PrimaryActionButton")
            };
            actionsPanel.Children.Add(saveButton);
            actionsPanel.Visibility = Visibility.Visible;
        }

        AddRemoteFields(screen, fieldsPanel, facilitySearchAction);
        if (screen.Key == "drug-special"
            && remoteFieldControls.TryGetValue("f:menu8", out var specialEditor)
            && specialEditor is ComboBox specialBox)
        {
            specialBox.Tag = "drug-special";
            specialBox.SelectionChanged += OnDrugSpecialSelectionChanged;
        }

        Grid? equivalentTablesGrid = null;
        if (hasEquivalentTables)
        {
            equivalentTablesGrid = new Grid();
            equivalentTablesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            equivalentTablesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            equivalentTablesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            tablesPanel.Children.Add(equivalentTablesGrid);
        }
        var tableIndex = 0;
        var fillRemoteHeight = screen.Key is "drugs" or "eye-drugs" or "reports"
            && screen.Tables.Count == 1;
        foreach (var table in screen.Tables)
        {
            var card = new Border
            {
                Style = (Style)FindResource("SectionCard"),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var data = new DataTable();
            for (var index = 0; index < table.Headers.Count; index++)
            {
                var name = string.IsNullOrWhiteSpace(table.Headers[index]) ? $"Alan {index + 1}" : table.Headers[index];
                var uniqueName = name;
                for (var suffix = 2; data.Columns.Contains(uniqueName); suffix++) uniqueName = $"{name} ({suffix})";
                data.Columns.Add(uniqueName);
            }
            var isReportIngredientTable = screen.Key is "report-detail" or "drug-report-detail"
                && table.Title == "Rapor Etkin Madde Bilgileri";
            if (isReportIngredientTable)
            {
                data.Columns.Add(ReportSgkColorColumnName);
                data.Columns.Add(ReportRowKindColumnName);
            }
            data.Columns.Add(RemoteActionColumnName);
            data.Columns.Add(RemotePageColumnName, typeof(int));
            data.Columns.Add(RemoteOrderColumnName, typeof(int));
            data.Columns.Add(RemoteBranchParentColumnName);
            var grid = new DataGrid
            {
                Style = (Style)FindResource("MedulaDataGridStyle"),
                ItemsSource = data.DefaultView,
                AutoGenerateColumns = true,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(244, 247, 248)),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(215, 224, 228)),
                BorderThickness = new Thickness(1),
                RowHeight = (screen.Key is "report-detail" or "drug-report-detail") && table.Title == "Açıklamalar"
                    ? double.NaN
                    : 36,
                ColumnHeaderHeight = 38,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                RowHeaderWidth = 0,
                MaxHeight = isReportIngredientTable
                    ? double.PositiveInfinity
                    : fillRemoteHeight ? double.PositiveInfinity : hasEquivalentTables ? 520 : 360,
                Height = fillRemoteHeight
                    ? Math.Max(430, RemoteScreenLayer.ActualHeight - 205)
                    : hasEquivalentTables ? 500 : double.NaN,
                MinHeight = fillRemoteHeight ? 430 : 90
            };
            ScrollViewer.SetVerticalScrollBarVisibility(
                grid, isReportIngredientTable
                    ? ScrollBarVisibility.Disabled
                    : hasEquivalentTables ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            var runtime = new RemoteTableRuntime
            {
                Screen = screen,
                Source = table,
                Data = data,
                Grid = grid,
                View = data.DefaultView
            };
            AppendRemoteRows(runtime, table);
            runtime.View.Sort = $"[{RemoteOrderColumnName}] ASC";
            grid.Tag = runtime;
            if (hasEquivalentTables)
                grid.LoadingRow += (_, args) => ApplyEquivalentRowStyle(args.Row, table);
            if (isReportIngredientTable)
                grid.LoadingRow += (_, args) => ApplyReportSgkRowStyle(args.Row);
            grid.AutoGeneratingColumn += (_, args) =>
            {
                if (args.PropertyName is RemoteActionColumnName or RemotePageColumnName
                    or RemoteOrderColumnName or RemoteBranchParentColumnName
                    or ReportSgkColorColumnName or ReportRowKindColumnName) args.Cancel = true;
                if (!args.Cancel && args.Column is DataGridTextColumn generatedTextColumn)
                    generatedTextColumn.Binding = new Binding($"[{args.PropertyName}]");
                if ((screen.Key is "report-detail" or "drug-report-detail") && table.Title == "Açıklamalar"
                    && args.Column is DataGridTextColumn textColumn)
                {
                    var wrappedText = new Style(typeof(TextBlock));
                    wrappedText.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                    wrappedText.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 5, 6, 5)));
                    wrappedText.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top));
                    wrappedText.Setters.Add(new Setter(TextBlock.FontSizeProperty, 15d));
                    textColumn.ElementStyle = wrappedText;
                    if (args.PropertyName == "Açıklama") textColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                }
                if ((screen.Key is "report-detail" or "drug-report-detail")
                    && table.Title == "Doktor Bilgileri"
                    && args.PropertyName == "Branş"
                    && args.Column is DataGridTextColumn branchColumn)
                {
                    var boldText = new Style(typeof(TextBlock));
                    boldText.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
                    boldText.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
                    branchColumn.ElementStyle = boldText;
                }
                if (isReportIngredientTable
                    && args.PropertyName == "Tedavi Şeması"
                    && args.Column is DataGridTextColumn importantColumn)
                {
                    var boldText = new Style(typeof(TextBlock));
                    boldText.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
                    boldText.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                    boldText.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5, 3, 5, 3)));
                    importantColumn.ElementStyle = boldText;
                }
                if (hasEquivalentTables && args.Column is DataGridTextColumn equivalentColumn)
                {
                    equivalentColumn.Binding = new Binding($"[{args.PropertyName}]");
                    equivalentColumn.Width = args.PropertyName switch
                    {
                        "Barkod" => new DataGridLength(118),
                        "İlaç Adı" => new DataGridLength(1, DataGridLengthUnitType.Star),
                        "Stok" => new DataGridLength(58),
                        _ => new DataGridLength(88)
                    };
                }
                if (screen.Key is "drugs" or "eye-drugs"
                    && args.Column is DataGridTextColumn drugHistoryColumn)
                {
                    drugHistoryColumn.Width = args.PropertyName switch
                    {
                        "İlaç Adı" => new DataGridLength(2.4, DataGridLengthUnitType.Star),
                        "Rap. Teşhisi / Rap. Tak. No" => new DataGridLength(1.45, DataGridLengthUnitType.Star),
                        "İlaç Kullanımı" => new DataGridLength(1.2, DataGridLengthUnitType.Star),
                        _ => new DataGridLength(1, DataGridLengthUnitType.Star)
                    };
                }
                if (screen.Key == "its-qr" && args.Column is DataGridTextColumn itsMedicationColumn)
                {
                    itsMedicationColumn.Width = args.PropertyName switch
                    {
                        "Barkod" => new DataGridLength(1.15, DataGridLengthUnitType.Star),
                        "Adı" => new DataGridLength(2.8, DataGridLengthUnitType.Star),
                        _ => new DataGridLength(1, DataGridLengthUnitType.Star)
                    };
                }
            };
            grid.Sorting += OnRemoteTableSorting;
            grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnRemoteTableScrollChanged));
            if (IsBranchSelectionScreen(screen.Key) || screen.Key == "reports")
                grid.PreviewMouseLeftButtonUp += OnRemoteTableRowSingleClick;
            else
                grid.MouseDoubleClick += OnRemoteTableRowDoubleClick;
            if (screen.Key is "report-detail" or "drug-report-detail")
                grid.PreviewMouseWheel += OnReportDetailTableMouseWheel;

            var content = new StackPanel();
            if (ShouldShowRemoteTableTitle(screen, table))
            {
                var titleText = new TextBlock
                {
                    Text = table.Title,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(38, 58, 71)),
                    Margin = screen.Key is "report-detail" or "drug-report-detail"
                        ? new Thickness(0)
                        : new Thickness(12, 10, 12, 2)
                };
                if (screen.Key is "report-detail" or "drug-report-detail")
                {
                    content.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(231, 242, 246)),
                        Padding = new Thickness(12, 9, 12, 8),
                        Child = titleText
                    });
                }
                else
                {
                    content.Children.Add(titleText);
                }
            }
            if (screen.Key == "branch-select")
            {
                var search = new TextBox
                {
                    Style = (Style)FindResource("EntryBox"),
                    Margin = new Thickness(10, 10, 10, 8),
                    ToolTip = "Ana veya alt branş ara"
                };
                search.TextChanged += (_, _) =>
                {
                    var term = search.Text.Trim().Replace("'", "''", StringComparison.Ordinal);
                    runtime.View.RowFilter = term.Length == 0
                        ? string.Empty
                        : $"CONVERT([Ana Branş], 'System.String') LIKE '%{term}%' OR CONVERT([Alt Branş], 'System.String') LIKE '%{term}%'";
                };
                content.Children.Add(search);
            }
            if (screen.Key is not ("report-detail" or "drug-report-detail" or "its-qr"))
            {
                var toolbar = new DockPanel { Margin = new Thickness(8, 4, 8, 5) };
                var reset = new Button
                {
                    Content = "⏷×",
                    ToolTip = "Medula sırasına dön",
                    Width = 38,
                    Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(190, 48, 48)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Tag = runtime
                };
                reset.Click += OnResetRemoteTableSort;
                DockPanel.SetDock(reset, Dock.Right);
                toolbar.Children.Add(reset);
                var countText = new TextBlock
                {
                    Text = $"{data.Rows.Count} satır",
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 118, 128)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                runtime.CountText = countText;
                toolbar.Children.Add(countText);
                content.Children.Add(toolbar);
            }
            content.Children.Add(grid);
            card.Child = content;
            if (equivalentTablesGrid is not null)
            {
                Grid.SetColumn(card, tableIndex == 0 ? 0 : 2);
                equivalentTablesGrid.Children.Add(card);
            }
            else if (!isReportIngredientTable)
                tablesPanel.Children.Add(card);
            if (isReportIngredientTable)
            {
                var pair = new Grid();
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var medicationCard = CreateReportPrescriptionMedicationCard(runtime);
                var medicationRuntime = medicationCard.Tag as RemoteTableRuntime;
                var reportContent = card.Child;
                var medicationContent = medicationCard.Child;
                card.Child = null;
                medicationCard.Child = null;
                Grid.SetColumn(reportContent, 0);
                pair.Children.Add(reportContent);
                var divider = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(207, 220, 225)),
                    Width = 1,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Grid.SetColumn(divider, 1);
                pair.Children.Add(divider);
                Grid.SetColumn(medicationContent, 2);
                pair.Children.Add(medicationContent);
                var combinedCard = new Border
                {
                    Style = (Style)FindResource("SectionCard"),
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = pair
                };
                tablesPanel.Children.Add(combinedCard);
                _ = PopulateReportIngredientMedicationsAsync(runtime, medicationRuntime);
            }
            tableIndex++;
        }

        if (screen.Fields.Count == 0
            && screen.Tables.Count == 0
            && (screen.TreatmentRows?.Count ?? 0) == 0)
        {
            tablesPanel.Children.Add(new TextBlock
            {
                Text = "Bu kayıt için görüntülenecek veri bulunamadı.",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 118, 128)),
                Margin = new Thickness(4, 18, 4, 4)
            });
        }

        if (inline)
        {
            var requiresExplicitClose = screen.Key is
                "report-selection" or "treatment-scheme" or "new-warning-code" or "uts-operations";
            InlineRemotePopup.StaysOpen = requiresExplicitClose;
            InlineRemoteDismissHint.Visibility = requiresExplicitClose
                ? Visibility.Collapsed
                : Visibility.Visible;
            InlineRemoteCloseButton.Visibility = requiresExplicitClose
                ? Visibility.Visible
                : Visibility.Collapsed;
            var centered = screen.Key is "facility-search" or "report-selection"
                or "treatment-scheme" or "new-warning-code" or "uts-operations" || hasEquivalentTables;
            InlineRemotePopup.Placement = centered ? PlacementMode.Center : PlacementMode.Bottom;
            InlineRemotePopup.PlacementTarget = centered ? this : remotePopupAnchor ?? this;
            InlineRemotePopup.HorizontalOffset = 0;
            InlineRemotePopup.VerticalOffset = 0;
            inlinePopupBackendOpen = true;
            InlineRemotePopup.IsOpen = true;
        }
    }

    private void ShowItsQrRetailScreen(MedulaRemoteScreen screen, bool enteringScreen)
    {
        CloseInlinePopupWithoutCleanup();
        RemoteScreenLayer.Visibility = Visibility.Visible;
        RemoteTitleText.Text = screen.Title;
        RemoteMessageText.Visibility = Visibility.Collapsed;
        RemoteParentButton.Visibility = Visibility.Collapsed;
        OffLabelTabsPanel.Visibility = Visibility.Collapsed;
        RemoteActionsPanel.Visibility = Visibility.Collapsed;
        RemoteContentScrollViewer.Visibility = Visibility.Collapsed;
        ItsQrRetailPanel.Visibility = Visibility.Visible;
        currentRemoteTitle = screen.Title;

        remoteFieldControls.Clear();
        suppressItsQrTextValidation = true;
        try
        {
            var effectiveCodes = MedulaRemoteScreenRefiner.ResolveItsQrCodes(
                screen.Fields.FirstOrDefault(field => field.Id == "form1:textarea1")?.Value,
                activeSnapshot?.Page.FieldValues?.GetValueOrDefault("medula:its-qr-codes"));
            ItsQrBackendTextBox.Text = string.Join(Environment.NewLine, effectiveCodes);
            ItsQrEntryTextBox.Clear();
        }
        finally
        {
            suppressItsQrTextValidation = false;
        }
        remoteFieldControls["form1:textarea1"] = ItsQrBackendTextBox;

        ItsQrActionsPanel.Children.Clear();
        itsSaleNotificationButton = null;
        foreach (var action in screen.Actions)
        {
            var button = new Button
            {
                Content = action.Caption,
                Tag = action,
                ToolTip = string.IsNullOrWhiteSpace(action.Context) ? null : action.Context,
                Style = (Style)FindResource(action.Caption.Contains(
                    "Satış Bildirimi", StringComparison.CurrentCultureIgnoreCase)
                        ? "PrimaryActionButton"
                        : "ActionButton")
            };
            button.Click += OnRemoteActionClick;
            ItsQrActionsPanel.Children.Add(button);
            if (action.Caption.Contains("Satış Bildirimi", StringComparison.CurrentCultureIgnoreCase))
                itsSaleNotificationButton = button;
        }
        ItsQrActionsPanel.Visibility = screen.Actions.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (enteringScreen) ResetItsPatientPayments();
        ItsQrPaymentPanel.Children.Clear();
        AddItsPatientPaymentSelector(ItsQrPaymentPanel);
        PopulateItsQrMedicationRows(screen);
        ItsQrRetailMessageText.Text = RemoteMessageForDisplay(screen);
        ApplyItsPatientAndPrescriptionSummary();

        if (!enteringScreen) return;
        _ = RefreshItsQrSaleStateAsync();
        var stored = ParseItsQrCodes(activeSnapshot?.Page.FieldValues?
            .GetValueOrDefault("medula:its-qr-codes"));
        lastPersistedItsQrCodes = stored.Length > 0
            ? stored
            : ParseItsQrCodes(ItsQrBackendTextBox.Text);
        if (ParseItsQrCodes(ItsQrBackendTextBox.Text).Length > 0)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => ScheduleItsQrValidation(ItsQrBackendTextBox, TimeSpan.Zero)));
        }
    }

    private void PopulateItsQrMedicationRows(MedulaRemoteScreen screen)
    {
        ItsQrMedicationRows.Clear();
        var qrCodesByBarcode = ParseItsQrCodes(ItsQrBackendTextBox.Text)
            .Select(code => new { Code = code, Barcode = BarcodeFromItsQrCode(code) })
            .Where(item => item.Barcode.Length > 0)
            .GroupBy(item => item.Barcode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Code).ToArray(),
                StringComparer.Ordinal);
        var source = screen.Tables.FirstOrDefault();
        if (source is not null)
        {
            foreach (var row in source.Rows)
            {
                var prescribed = row.ElementAtOrDefault(2)?.Trim() ?? string.Empty;
                var scanned = row.ElementAtOrDefault(3)?.Trim() ?? string.Empty;
                ItsQrMedicationRows.Add(new ItsQrMedicationDisplay(
                    row.ElementAtOrDefault(0)?.Trim() ?? string.Empty,
                    row.ElementAtOrDefault(1)?.Trim() ?? string.Empty,
                    prescribed,
                    scanned,
                    MedulaRemoteScreenRefiner.ItsQrQuantitiesMatch(prescribed, scanned),
                    qrCodesByBarcode.GetValueOrDefault(row.ElementAtOrDefault(0)?.Trim() ?? string.Empty),
                    itsSaleReportedQrCodes));
            }
        }
        if (ItsQrMedicationRows.Count == 0)
        {
            foreach (var drug in Drugs.Where(drug => !string.IsNullOrWhiteSpace(drug.Barcode)))
            {
                var drugQrCodes = qrCodesByBarcode.GetValueOrDefault(drug.Barcode.Trim()) ?? [];
                var scanned = drugQrCodes.Length;
                var scannedText = scanned.ToString(TurkishCulture);
                ItsQrMedicationRows.Add(new ItsQrMedicationDisplay(
                    drug.Barcode.Trim(), drug.Name.Trim(), drug.Quantity.Trim(), scannedText,
                    MedulaRemoteScreenRefiner.ItsQrQuantitiesMatch(drug.Quantity, scannedText),
                    drugQrCodes,
                    itsSaleReportedQrCodes));
            }
        }
        var prescribedTotal = ItsQrMedicationRows.Sum(row => ParseItsQrQuantity(row.PrescribedQuantity));
        var scannedTotal = ItsQrMedicationRows.Sum(row => ParseItsQrQuantity(row.QrQuantity));
        var balanced = ItsQrMedicationRows.Count > 0 && ItsQrMedicationRows.All(row => row.IsBalanced);
        ItsQrBalanceText.Text = $"{scannedTotal:0.##} / {prescribedTotal:0.##} karekod";
        ItsQrBalanceText.Foreground = new SolidColorBrush(balanced
            ? Color.FromRgb(22, 125, 78)
            : Color.FromRgb(179, 58, 50));
    }

    private async Task RefreshItsQrSaleStateAsync()
    {
        try
        {
            var reported = activeSnapshot?.PrescriptionUid is { } prescriptionUid
                ? await prescriptionSaleRepository.ReadReportedQrCodesAsync(
                    prescriptionUid, lifetime.Token)
                : new HashSet<string>(StringComparer.Ordinal);
            itsSaleReportedQrCodes.Clear();
            itsSaleReportedQrCodes.UnionWith(
                reported.Select(RetailQrCodeNormalizer.Normalize));
            if (RemoteScreenLayer.Tag is MedulaRemoteScreen { Key: "its-qr" } screen)
                PopulateItsQrMedicationRows(screen);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Medula remains usable when local sale-state readback is temporarily unavailable.
        }
    }

    private void SetItsQrSaleReported(IEnumerable<string> qrCodes, bool reported)
    {
        foreach (var qrCode in qrCodes.Select(RetailQrCodeNormalizer.Normalize))
        {
            if (reported) itsSaleReportedQrCodes.Add(qrCode);
            else itsSaleReportedQrCodes.Remove(qrCode);
        }
        if (RemoteScreenLayer.Tag is MedulaRemoteScreen { Key: "its-qr" } screen)
            PopulateItsQrMedicationRows(screen);
    }

    private void ApplyItsPatientAndPrescriptionSummary()
    {
        ItsQrPatientNameText.Text = FirstNonBlank(
            prescriptionPartyDebtSummary.OwnerDisplayName,
            OwnerPatientNameBox.Text,
            activeSnapshot?.PatientDisplayName,
            "—");
        ItsQrPatientIdentityText.Text = FirstNonBlank(PatientIdentityBox.Text, "—");
        ItsQrPatientPhoneText.Text = FirstNonBlank(prescriptionPartyDebtSummary.OwnerPrimaryPhone, "—");
        ItsQrPatientPhonePanel.Visibility = Visibility.Visible;
        ItsQrPatientFamilyText.Text = prescriptionPartyDebtSummary.FamilyGroupName;
        ItsQrPatientFamilyPanel.Visibility = prescriptionPartyDebtSummary.HasFamilyGroup
            ? Visibility.Visible
            : Visibility.Collapsed;
        ItsQrPatientDebtText.Text =
            $"Toplam borç: {prescriptionPartyDebtSummary.TotalDebt.ToString("N2", TurkishCulture)} ₺";
        ItsQrOwnerDebtText.Text =
            $"Reçete sahibi borcu: {prescriptionPartyDebtSummary.OwnerDebt.ToString("N2", TurkishCulture)} ₺";
        ItsQrRecipientDebtText.Text = prescriptionPartyDebtSummary.RecipientDebt is { } recipientDebt
            ? $"Teslim alan borcu: {recipientDebt.ToString("N2", TurkishCulture)} ₺"
            : "Teslim alan borcu: —";
        ItsQrFamilyDebtText.Text =
            $"Aile grubu borcu: {prescriptionPartyDebtSummary.FamilyGroupDebt.ToString("N2", TurkishCulture)} ₺";
        ItsQrFamilyDebtPanel.Visibility = prescriptionPartyDebtSummary.HasFamilyGroup
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyItsDebtSelectionAvailability();

        var fields = activeSnapshot?.Page.FieldValues ?? new Dictionary<string, string>();
        var payments = activeSnapshot?.Page.SavedSummary?.PaymentFields ?? [];
        static string Money(decimal? amount, CultureInfo culture) =>
            $"{(amount ?? 0m).ToString("N2", culture)} ₺";
        var details = new List<string>
        {
            $"Reçete No: {FirstNonBlank(prescriptionNumberContext, fields.GetValueOrDefault("medula:prescription-no"), activeSnapshot?.SourceListItem?.PrescriptionNumber, "—")}"
        };
        var electronicNumber = FirstNonBlank(
            electronicPrescriptionNumberContext,
            fields.GetValueOrDefault("medula:e-prescription-no"),
            EPrescriptionNumberBox.Text);
        if (electronicNumber.Length > 0)
            details.Add($"E-Reçete No: {electronicNumber}");
        details.Add($"Reçete Tarihi: {FirstNonBlank(fields.GetValueOrDefault("f:t29"), PrescriptionDateBox.Text, "—")}");
        details.Add($"Reçete Rengi: {FirstNonBlank(SelectedText(PrescriptionTypeBox), "—")}");
        details.Add($"Toplam Tutar: {Money(PaymentValue(payments, "Toplam Tutar"), TurkishCulture)}");
        details.Add($"Ödenecek Tutar: {Money(PaymentValue(payments, "Ödenecek Tutar"), TurkishCulture)}");
        details.Add($"Hastanın ödeyeceği tutar: {Money(PaymentValue(payments, "Hastanın eczaneye ödemesi gereken"), TurkishCulture)}");
        ItsQrPrescriptionText.Text = string.Join(Environment.NewLine, details);
    }

    private static decimal ParseItsQrQuantity(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return decimal.TryParse(text, NumberStyles.Number, TurkishCulture, out var result)
            || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out result)
                ? result
                : 0m;
    }

    private static string BarcodeFromItsQrCode(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (!normalized.StartsWith("01", StringComparison.Ordinal) || normalized.Length < 16)
            return string.Empty;
        var gtin = normalized.Substring(2, 14);
        return gtin.StartsWith('0') ? gtin[1..] : gtin;
    }

    private void OnItsQrAddClick(object sender, RoutedEventArgs e)
    {
        CommitItsQrEntry();
    }

    private void CommitItsQrEntry()
    {
        var code = ItsQrEntryTextBox.Text.Trim();
        if (code.Length == 0)
        {
            ItsQrEntryTextBox.Focus();
            return;
        }
        var existing = ParseItsQrCodes(ItsQrBackendTextBox.Text);
        ItsQrBackendTextBox.Text = string.Join(
            Environment.NewLine,
            existing.Append(code));
        ItsQrEntryTextBox.Clear();
        ScheduleItsQrValidation(ItsQrBackendTextBox, TimeSpan.Zero);
        ItsQrEntryTextBox.Focus();
        Keyboard.Focus(ItsQrEntryTextBox);
    }

    private void OnItsQrMedicationExpandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: ItsQrMedicationDisplay row } toggle) return;
        row.IsExpanded = toggle.IsChecked == true;
    }

    private void OnCopyItsQrCodeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ItsQrCodeDisplay qrCode) return;
        try
        {
            Clipboard.SetText(qrCode.Code);
            ItsQrRetailMessageText.Text = "Karekod panoya kopyalandı.";
        }
        catch (Exception exception)
        {
            ItsQrRetailMessageText.Text = $"Karekod kopyalanamadı: {exception.Message}";
        }
    }

    private void OnRemoveItsQrCodeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ItsQrCodeDisplay qrCode) return;
        var remaining = ParseItsQrCodes(ItsQrBackendTextBox.Text)
            .Where(code => !string.Equals(code, qrCode.Code, StringComparison.Ordinal))
            .ToArray();
        ItsQrBackendTextBox.Text = string.Join(Environment.NewLine, remaining);
        ScheduleItsQrValidation(ItsQrBackendTextBox, TimeSpan.Zero);
        ItsQrEntryTextBox.Focus();
        Keyboard.Focus(ItsQrEntryTextBox);
    }

    private void ResetItsPatientPayments()
    {
        selectedItsPatientPaymentKinds.Clear();
        foreach (var kind in ItsPatientPaymentKinds) itsPatientPaymentAmounts[kind] = 0m;
        suppressItsDebtSelectionChanges = true;
        try
        {
            ItsQrOwnerDebtCheckBox.IsChecked = true;
            ItsQrRecipientDebtCheckBox.IsChecked = prescriptionPartyDebtSummary.RecipientDebt is not null;
            ItsQrFamilyDebtCheckBox.IsChecked = prescriptionPartyDebtSummary.HasFamilyGroup;
            ItsQrTotalDebtCheckBox.IsChecked = true;
        }
        finally
        {
            suppressItsDebtSelectionChanges = false;
        }
    }

    private void ApplyItsDebtSelectionAvailability()
    {
        suppressItsDebtSelectionChanges = true;
        try
        {
            ItsQrOwnerDebtCheckBox.IsEnabled = prescriptionPartyDebtSummary.OwnerDebt > 0m;
            ItsQrRecipientDebtCheckBox.IsEnabled =
                prescriptionPartyDebtSummary.RecipientDebt is > 0m;
            ItsQrFamilyDebtCheckBox.IsEnabled =
                prescriptionPartyDebtSummary.HasFamilyGroup
                && prescriptionPartyDebtSummary.FamilyGroupDebt > 0m;
            if (!ItsQrOwnerDebtCheckBox.IsEnabled) ItsQrOwnerDebtCheckBox.IsChecked = false;
            if (!ItsQrRecipientDebtCheckBox.IsEnabled) ItsQrRecipientDebtCheckBox.IsChecked = false;
            if (!ItsQrFamilyDebtCheckBox.IsEnabled) ItsQrFamilyDebtCheckBox.IsChecked = false;
            ItsQrTotalDebtCheckBox.IsChecked =
                ItsQrOwnerDebtCheckBox.IsChecked == true
                || ItsQrRecipientDebtCheckBox.IsChecked == true
                || ItsQrFamilyDebtCheckBox.IsChecked == true;
            ItsQrTotalDebtCheckBox.IsEnabled = prescriptionPartyDebtSummary.TotalDebt > 0m;
        }
        finally
        {
            suppressItsDebtSelectionChanges = false;
        }
        RebalanceItsPatientPayments();
    }

    private void OnItsDebtSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (suppressItsDebtSelectionChanges) return;
        suppressItsDebtSelectionChanges = true;
        try
        {
            if (ReferenceEquals(sender, ItsQrTotalDebtCheckBox))
            {
                var selected = ItsQrTotalDebtCheckBox.IsChecked == true;
                ItsQrOwnerDebtCheckBox.IsChecked = selected && ItsQrOwnerDebtCheckBox.IsEnabled;
                ItsQrRecipientDebtCheckBox.IsChecked = selected && ItsQrRecipientDebtCheckBox.IsEnabled;
                ItsQrFamilyDebtCheckBox.IsChecked = selected && ItsQrFamilyDebtCheckBox.IsEnabled;
            }
            else
            {
                ItsQrTotalDebtCheckBox.IsChecked =
                    ItsQrOwnerDebtCheckBox.IsChecked == true
                    || ItsQrRecipientDebtCheckBox.IsChecked == true
                    || ItsQrFamilyDebtCheckBox.IsChecked == true;
            }
        }
        finally
        {
            suppressItsDebtSelectionChanges = false;
        }
        RebalanceItsPatientPayments();
    }

    private MedulaPrescriptionDebtCollection SelectedItsDebtCollection() => new(
        ItsQrOwnerDebtCheckBox.IsChecked == true ? prescriptionPartyDebtSummary.OwnerDebt : 0m,
        ItsQrRecipientDebtCheckBox.IsChecked == true ? prescriptionPartyDebtSummary.RecipientDebt ?? 0m : 0m,
        ItsQrFamilyDebtCheckBox.IsChecked == true ? prescriptionPartyDebtSummary.FamilyGroupDebt : 0m);

    private decimal ItsCheckoutTotal() =>
        MedulaPrescriptionCheckoutRules.CheckoutTotal(
            itsPatientPaymentTotal, SelectedItsDebtCollection());

    private void AddItsPatientPaymentSelector(WrapPanel fieldsPanel)
    {
        itsPatientPaymentTotal = decimal.Round(PaymentValue(
            activeSnapshot?.Page.SavedSummary?.PaymentFields ?? [],
            "Hastanın eczaneye ödemesi gereken") ?? 0m, 2);
        itsPatientPaymentSelectors.Clear();
        itsPatientPaymentEditors.Clear();
        fieldsPanel.Visibility = Visibility.Visible;

        var content = new StackPanel();
        var selectors = new UniformGrid { Columns = 5 };
        foreach (var kind in ItsPatientPaymentKinds)
        {
            var editor = new MoneyEntryControl
            {
                Tag = kind,
                Amount = itsPatientPaymentAmounts[kind],
                HorizontalAlignment = HorizontalAlignment.Center,
                IsEnabled = selectedItsPatientPaymentKinds.Contains(kind)
            };
            editor.AmountChanged += OnItsPatientPaymentAmountChanged;
            var selectorContent = new StackPanel { Margin = new Thickness(8, 6, 8, 7) };
            selectorContent.Children.Add(new TextBlock
            {
                Text = ItsPatientPaymentCaption(kind),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 108)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5)
            });
            selectorContent.Children.Add(editor);
            var selector = new ToggleButton
            {
                Tag = kind,
                Content = selectorContent,
                IsChecked = selectedItsPatientPaymentKinds.Contains(kind),
                MinWidth = 138,
                MinHeight = 70,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(selectedItsPatientPaymentKinds.Contains(kind)
                    ? Color.FromRgb(238, 244, 244)
                    : Color.FromRgb(249, 251, 251)),
                BorderBrush = new SolidColorBrush(selectedItsPatientPaymentKinds.Contains(kind)
                    ? Color.FromRgb(13, 148, 136)
                    : Color.FromRgb(216, 224, 229)),
                BorderThickness = new Thickness(1)
            };
            selector.Checked += OnItsPatientPaymentSelectionChanged;
            selector.Unchecked += OnItsPatientPaymentSelectionChanged;
            itsPatientPaymentSelectors[kind] = selector;
            itsPatientPaymentEditors[kind] = editor;
            selectors.Children.Add(selector);
        }
        content.Children.Add(selectors);
        var totals = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 9, 0, 0)
        };
        itsDebtDueText = new TextBlock
        {
            Margin = new Thickness(0, 0, 14, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(23, 105, 170)),
            FontWeight = FontWeights.SemiBold
        };
        itsPatientPaymentStatusText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold
        };
        totals.Children.Add(itsDebtDueText);
        totals.Children.Add(itsPatientPaymentStatusText);
        content.Children.Add(totals);
        fieldsPanel.Children.Add(new Border
        {
            Width = 820,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 224, 228)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = content
        });
        UpdateItsPatientPaymentState();
    }

    private void OnItsPatientPaymentSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (suppressItsPatientPaymentChanges || sender is not ToggleButton { Tag: RetailPaymentKind kind } selector)
            return;
        if (selector.IsChecked == true) selectedItsPatientPaymentKinds.Add(kind);
        else selectedItsPatientPaymentKinds.Remove(kind);
        RebalanceItsPatientPayments();
    }

    private void OnItsPatientPaymentAmountChanged(object? sender, EventArgs e)
    {
        if (suppressItsPatientPaymentChanges || sender is not MoneyEntryControl { Tag: RetailPaymentKind kind } editor)
            return;
        itsPatientPaymentAmounts[kind] = editor.Amount;
        RebalanceItsPatientPayments(kind);
    }

    private void RebalanceItsPatientPayments(RetailPaymentKind? editedKind = null)
    {
        var selected = ItsPatientPaymentKinds.Where(selectedItsPatientPaymentKinds.Contains).ToArray();
        var checkoutTotal = ItsCheckoutTotal();
        RetailPaymentBalancer.Rebalance(checkoutTotal, selected, itsPatientPaymentAmounts, editedKind);
        suppressItsPatientPaymentChanges = true;
        try
        {
            foreach (var kind in ItsPatientPaymentKinds)
            {
                if (itsPatientPaymentEditors.TryGetValue(kind, out var editor))
                {
                    editor.IsEnabled = selectedItsPatientPaymentKinds.Contains(kind) && checkoutTotal > 0m;
                    editor.Amount = itsPatientPaymentAmounts[kind];
                }
                if (itsPatientPaymentSelectors.TryGetValue(kind, out var selector))
                {
                    var selectedKind = selectedItsPatientPaymentKinds.Contains(kind);
                    selector.Background = new SolidColorBrush(selectedKind
                        ? Color.FromRgb(238, 244, 244)
                        : Color.FromRgb(249, 251, 251));
                    selector.BorderBrush = new SolidColorBrush(selectedKind
                        ? Color.FromRgb(13, 148, 136)
                        : Color.FromRgb(216, 224, 229));
                }
            }
        }
        finally
        {
            suppressItsPatientPaymentChanges = false;
        }
        UpdateItsPatientPaymentState();
    }

    private void UpdateItsPatientPaymentState()
    {
        var selected = ItsPatientPaymentKinds.Where(selectedItsPatientPaymentKinds.Contains).ToArray();
        var allocated = selected.Sum(kind => itsPatientPaymentAmounts[kind]);
        var debtCollection = SelectedItsDebtCollection();
        var checkoutTotal = ItsCheckoutTotal();
        var debtCovered = selected
            .Where(kind => kind is RetailPaymentKind.Cash
                or RetailPaymentKind.Credit
                or RetailPaymentKind.BankTransfer)
            .Sum(kind => itsPatientPaymentAmounts[kind]) >= debtCollection.TotalAmount;
        var exact = checkoutTotal == 0m
            || selected.Length > 0
            && selected.All(kind => itsPatientPaymentAmounts[kind] > 0m)
            && RetailPaymentBalancer.IsExact(checkoutTotal, selected, itsPatientPaymentAmounts)
            && debtCovered;
        if (itsDebtDueText is not null)
            itsDebtDueText.Text = $"Ödenecek borç: {debtCollection.TotalAmount.ToString("N2", TurkishCulture)} ₺";
        if (itsPatientPaymentStatusText is not null)
        {
            itsPatientPaymentStatusText.Text = checkoutTotal == 0m
                ? "Ödeme bulunmuyor."
                : $"Ödeme toplamı: {allocated.ToString("N2", TurkishCulture)} ₺ / {checkoutTotal.ToString("N2", TurkishCulture)} ₺";
            itsPatientPaymentStatusText.Foreground = new SolidColorBrush(exact
                ? Color.FromRgb(22, 125, 78)
                : Color.FromRgb(179, 58, 50));
        }
        if (itsSaleNotificationButton is not null)
        {
            itsSaleNotificationButton.IsEnabled = exact;
            itsSaleNotificationButton.ToolTip = exact
                ? null
                : !debtCovered
                    ? "Ödenecek borç nakit, kart veya havale tutarlarıyla karşılanmalıdır."
                    : "Ödeme türleri toplamı reçete ve seçilen borç tahsilatına eşit olmalıdır.";
        }
    }

    private IReadOnlyList<RetailSalePaymentInput> BuildItsPatientPaymentInputs() =>
        ItsPatientPaymentKinds
            .Where(kind => selectedItsPatientPaymentKinds.Contains(kind) && itsPatientPaymentAmounts[kind] > 0m)
            .Select(kind => new RetailSalePaymentInput(kind, itsPatientPaymentAmounts[kind]))
            .ToArray();

    private static string ItsPatientPaymentCaption(RetailPaymentKind kind) => kind switch
    {
        RetailPaymentKind.Cash => "Nakit",
        RetailPaymentKind.Credit => "Kart",
        RetailPaymentKind.BankTransfer => "Havale",
        RetailPaymentKind.Debt => "Veresiye",
        RetailPaymentKind.Discount => "İskonto",
        _ => kind.ToString()
    };

    private async void OnDrugSpecialSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (busy || sender is not ComboBox { Tag: "drug-special" } box) return;
        SetBusy(true, "Özel durum Medula'ya aktarılıyor…");
        try
        {
            var result = await service.SetPrescriptionDrugSpecialConditionAsync(
                box.SelectedValue?.ToString() ?? string.Empty, lifetime.Token);
            ApplyDrugActionResult(result, replaceRows: result.Drugs.Count > 0);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void ApplyEquivalentRowStyle(DataGridRow row, MedulaRemoteTable table)
    {
        if (row.Item is not DataRowView view
            || !int.TryParse(view[RemoteOrderColumnName]?.ToString(), out var sourceIndex)) return;
        var background = table.RowBackgrounds is { } backgrounds && sourceIndex < backgrounds.Count
            ? backgrounds[sourceIndex]
            : string.Empty;
        var selected = table.RowSelections is { } selections
            && sourceIndex < selections.Count && selections[sourceIndex];
        var isGreen = background.Contains("149, 255, 149", StringComparison.Ordinal)
            || background.Contains("rowGreen", StringComparison.OrdinalIgnoreCase);
        row.Background = isGreen
            ? new SolidColorBrush(selected
                ? Color.FromRgb(66, 245, 98)
                : Color.FromRgb(149, 255, 149))
            : new SolidColorBrush(selected
                ? Color.FromRgb(255, 222, 35)
                : Color.FromRgb(255, 255, 127));
        row.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void AddRemoteFields(MedulaRemoteScreen screen, WrapPanel fieldsPanel)
        => AddRemoteFields(screen, fieldsPanel, null);

    private void AddRemoteFields(
        MedulaRemoteScreen screen,
        WrapPanel fieldsPanel,
        MedulaRemoteAction? facilitySearchAction)
    {
        var fields = screen.Fields
            .Where(field => field.Id is not ("display:first-name" or "display:last-name"))
            .ToArray();
        if (screen.Key is "treatment-scheme" or "new-warning-code")
        {
            var treatmentRows = screen.TreatmentRows is { Count: > 0 }
                ? screen.TreatmentRows
                : BuildFallbackTreatmentRows(fields);
            if (treatmentRows.Count > 0)
            {
                AddTreatmentSchemeGrid(fieldsPanel, treatmentRows);
                return;
            }
        }
        if (fields.Length == 0) return;

        if (screen.Key == "facility-search")
        {
            var searchField = fields.FirstOrDefault(field => field.Id == "f:text74")
                ?? fields.FirstOrDefault(field => field.Kind == "text");
            if (searchField is not null)
            {
                var row = new Grid { Width = 820, Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var fieldPanel = CreateRemoteFieldPanel(searchField);
                fieldPanel.Width = double.NaN;
                fieldPanel.Margin = new Thickness(0);
                row.Children.Add(fieldPanel);
                if (facilitySearchAction is not null)
                {
                    var button = new Button
                    {
                        Content = facilitySearchAction.Caption,
                        Tag = facilitySearchAction,
                        Style = (Style)FindResource("PrimaryActionButton"),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0)
                    };
                    button.Click += OnRemoteActionClick;
                    Grid.SetColumn(button, 2);
                    row.Children.Add(button);
                }
                fieldsPanel.Children.Add(row);
            }
            foreach (var field in fields.Where(field => field != searchField))
                fieldsPanel.Children.Add(CreateRemoteFieldPanel(field));
            return;
        }

        if (screen.Key == "report-selection")
        {
            var stack = new StackPanel { Width = 970 };
            foreach (var field in fields)
            {
                var row = CreateRemoteFieldPanel(field);
                row.Width = 950;
                row.Margin = new Thickness(0, 0, 0, 12);
                stack.Children.Add(row);
            }
            fieldsPanel.Children.Add(stack);
            return;
        }

        if (screen.Key == "drug-info")
        {
            var availableWidth = fieldsPanel.ActualWidth > 0
                ? fieldsPanel.ActualWidth
                : Math.Max(900, ActualWidth - 48);
            var cardWidth = Math.Max(420, (availableWidth - 24) / 2);
            AddRemoteFieldGroup(fieldsPanel, "Genel İlaç Bilgileri", fields.Where(field =>
                field.Id.StartsWith("display:drug:general:", StringComparison.Ordinal)), cardWidth);
            var ruleHeaders = fields
                .Where(field => field.Id.StartsWith("display:drug:rule-group:", StringComparison.Ordinal))
                .OrderBy(field => ParseTrailingIndex(field.Id))
                .ToArray();
            foreach (var header in ruleHeaders)
            {
                var groupIndex = ParseTrailingIndex(header.Id);
                AddRemoteFieldGroup(
                    fieldsPanel,
                    string.IsNullOrWhiteSpace(header.Value) ? $"İlaç Kuralı {groupIndex + 1}" : header.Value,
                    fields.Where(field => field.Id.StartsWith(
                        $"display:drug:rule:{groupIndex}:", StringComparison.Ordinal)),
                    cardWidth);
            }
            AddRemoteFieldGroup(fieldsPanel, "Diğer İlaç Bilgileri", fields.Where(field =>
                !field.Id.StartsWith("display:drug:general:", StringComparison.Ordinal)
                && !field.Id.StartsWith("display:drug:rule:", StringComparison.Ordinal)
                && !field.Id.StartsWith("display:drug:rule-group:", StringComparison.Ordinal)), cardWidth);
            return;
        }

        if (screen.Key == "report-new")
        {
            AddRemoteFieldGroup(fieldsPanel, "Rapor Bilgileri", fields.Where(field => field.Id is
                "form1:text28" or "form1:text30" or "form1:text24" or "form1:menu2"
                or "form1:text120" or "form1:text60"));
            AddRemoteFieldGroup(fieldsPanel, "Tanı Bilgileri", fields.Where(field =>
                field.Id is "form1:menu3" or "form1:text13"
                || field.Id.StartsWith("form1:tableEx5:", StringComparison.Ordinal)));
            AddRemoteFieldGroup(fieldsPanel, "Doktor Bilgileri", fields.Where(field => field.Id is
                "form1:text46" or "form1:text25" or "form1:menu5" or "form1:menu4"
                or "form1:text50" or "form1:text52" or "form1:checkbox1"));
            var known = new HashSet<string>(remoteFieldControls.Keys, StringComparer.Ordinal);
            AddRemoteFieldGroup(fieldsPanel, "Diğer Bilgiler", fields.Where(field => !known.Contains(field.Id)));
            return;
        }

        if (screen.Key is "report-detail" or "drug-report-detail")
        {
            var beneficiary = fields.Where(IsBeneficiaryField).ToArray();
            var availableWidth = RemoteContentScrollViewer.ActualWidth > 0
                ? RemoteContentScrollViewer.ActualWidth
                : Math.Max(960, ActualWidth - 56);
            var initiallyCollapsed = ApplicationSettingsStore.Current.HideUnimportantReportDetails;
            var row = new Grid
            {
                Width = Math.Max(900, availableWidth - 4),
                Margin = new Thickness(0, 0, 0, 12)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var reportCard = CreateCollapsibleReportFieldGroup(
                "Rapor Bilgileri", fields.Except(beneficiary), initiallyCollapsed);
            var beneficiaryCard = CreateCollapsibleReportFieldGroup(
                "Hak Sahibi Bilgileri", beneficiary, initiallyCollapsed);
            if (reportCard is not null)
            {
                Grid.SetColumn(reportCard, 0);
                row.Children.Add(reportCard);
            }
            if (beneficiaryCard is not null)
            {
                Grid.SetColumn(beneficiaryCard, 2);
                row.Children.Add(beneficiaryCard);
            }
            if (row.Children.Count > 0) fieldsPanel.Children.Add(row);
            return;
        }

        foreach (var field in fields)
            fieldsPanel.Children.Add(CreateRemoteFieldPanel(field));
    }

    private void AddTreatmentSchemeGrid(
        WrapPanel host,
        IReadOnlyList<MedulaTreatmentSchemeRow> rows)
    {
        var columnGroups = MedulaEditableTableLayout.BuildColumnGroups(rows);
        if (columnGroups.Count == 0) return;

        var table = new Grid { Width = 1070 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
        for (var index = 0; index < columnGroups.Count; index++)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        AddTreatmentSchemeCell(table, "İlaç", 0, 0, true);
        for (var index = 0; index < columnGroups.Count; index++)
            AddTreatmentSchemeCell(table, columnGroups[index].Label, index + 1, 0, true);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var source = rows[rowIndex];
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            AddTreatmentSchemeCell(
                table,
                string.IsNullOrWhiteSpace(source.MedicationName)
                    ? $"İlaç {rowIndex + 1}"
                    : source.MedicationName,
                0,
                rowIndex + 1,
                false);
            for (var columnIndex = 0; columnIndex < columnGroups.Count; columnIndex++)
            {
                var cells = columnGroups[columnIndex].CellIndexes
                    .Select(index => source.Cells.ElementAtOrDefault(index))
                    .Where(cell => cell is not null)
                    .Cast<MedulaTreatmentSchemeCell>()
                    .ToArray();
                if (cells.Length == 0) continue;
                var editor = CreateTreatmentSchemeEditorGroup(cells);
                editor.Margin = new Thickness(7, 6, 7, 6);
                Grid.SetColumn(editor, columnIndex + 1);
                Grid.SetRow(editor, rowIndex + 1);
                table.Children.Add(editor);
            }
        }

        host.Children.Add(new Border
        {
            Width = 1070,
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(207, 220, 226)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = table
        });
    }

    private FrameworkElement CreateTreatmentSchemeEditorGroup(
        IReadOnlyList<MedulaTreatmentSchemeCell> cells)
    {
        if (cells.Count == 1) return CreateTreatmentSchemeEditor(cells[0]);

        var group = new Grid();
        for (var index = 0; index < cells.Count; index++)
        {
            group.ColumnDefinitions.Add(new ColumnDefinition());
            if (index + 1 < cells.Count)
                group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var editor = CreateTreatmentSchemeEditor(cells[index]);
            editor.Margin = new Thickness(index == 0 ? 0 : 5, 0, index + 1 == cells.Count ? 0 : 5, 0);
            Grid.SetColumn(editor, index * 2);
            group.Children.Add(editor);
            if (index + 1 >= cells.Count) continue;
            var separator = new TextBlock
            {
                Text = "×",
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(74, 94, 105))
            };
            Grid.SetColumn(separator, index * 2 + 1);
            group.Children.Add(separator);
        }
        return group;
    }

    private static IReadOnlyList<MedulaTreatmentSchemeRow> BuildFallbackTreatmentRows(
        IEnumerable<MedulaRemoteField> fields)
    {
        var editable = fields.Where(field => !field.IsReadOnly
                && !field.Id.StartsWith("display:", StringComparison.Ordinal))
            .ToArray();
        if (editable.Length == 0) return [];

        static string RowKey(MedulaRemoteField field)
        {
            foreach (var segment in field.Id.Split(':'))
            {
                if (int.TryParse(segment, out var index)) return index.ToString(TurkishCulture);
            }
            return "0";
        }

        return editable.GroupBy(RowKey, StringComparer.Ordinal)
            .Select((group, index) => new MedulaTreatmentSchemeRow(
                $"İlaç {index + 1}",
                group.Select(field => new MedulaTreatmentSchemeCell(
                    field.Id,
                    field.Label,
                    field.Value,
                    field.Kind,
                    field.IsReadOnly,
                    field.Options)).ToArray()))
            .ToArray();
    }

    private static void AddTreatmentSchemeCell(
        Grid table,
        string text,
        int column,
        int row,
        bool header)
    {
        var border = new Border
        {
            Background = header
                ? new SolidColorBrush(Color.FromRgb(220, 238, 245))
                : row % 2 == 0
                    ? new SolidColorBrush(Color.FromRgb(247, 250, 251))
                    : Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 228, 232)),
            BorderThickness = new Thickness(column == 0 ? 0 : 1, row == 0 ? 0 : 1, 0, 0),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new TextBlock
            {
                Text = text,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(43, 62, 72)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = text
            }
        };
        Grid.SetColumn(border, column);
        Grid.SetRow(border, row);
        table.Children.Add(border);
    }

    private FrameworkElement CreateTreatmentSchemeEditor(MedulaTreatmentSchemeCell cell)
    {
        FrameworkElement editor;
        if (cell.Kind == "select")
        {
            editor = new ComboBox
            {
                ItemsSource = cell.Options,
                DisplayMemberPath = nameof(MedulaRemoteOption.Text),
                SelectedValuePath = nameof(MedulaRemoteOption.Value),
                SelectedValue = cell.Value,
                IsEnabled = !cell.IsReadOnly,
                Style = (Style)FindResource("EntryCombo")
            };
        }
        else if (cell.Kind == "check")
        {
            editor = new CheckBox
            {
                IsChecked = string.Equals(cell.Value, "true", StringComparison.OrdinalIgnoreCase),
                IsEnabled = !cell.IsReadOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            editor = new TextBox
            {
                Text = cell.Value,
                IsReadOnly = cell.IsReadOnly,
                Style = (Style)FindResource("EntryBox"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }
        remoteFieldControls[cell.FieldId] = editor;
        return editor;
    }

    private static bool IsBeneficiaryField(MedulaRemoteField field)
    {
        var label = field.Label;
        return field.Id is "display:identity" or "display:name" or "display:first-name" or "display:last-name"
            || label.Contains("Hak Sahibi", StringComparison.OrdinalIgnoreCase)
            || label.Contains("T.C.", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Hasta", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Adı", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Soyadı", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Doğum", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Cinsiyet", StringComparison.OrdinalIgnoreCase);
    }

    private void AddRemoteFieldGroup(
        WrapPanel host,
        string title,
        IEnumerable<MedulaRemoteField> source,
        double cardWidth = 940)
    {
        var fields = source.Where(field => !remoteFieldControls.ContainsKey(field.Id)).ToArray();
        if (fields.Length == 0) return;
        var content = new WrapPanel();
        foreach (var field in fields) content.Children.Add(CreateRemoteFieldPanel(field));
        var stack = new StackPanel();
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(220, 238, 245)),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(14, 9, 14, 9),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(38, 58, 71))
            }
        };
        stack.Children.Add(header);
        content.Margin = new Thickness(14, 12, 14, 4);
        stack.Children.Add(content);
        host.Children.Add(new Border
        {
            Width = cardWidth,
            Margin = new Thickness(0, 0, 12, 12),
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 231)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = stack
        });
    }

    private Border? CreateCollapsibleReportFieldGroup(
        string title,
        IEnumerable<MedulaRemoteField> source,
        bool initiallyCollapsed)
    {
        var fields = source.Where(field => !remoteFieldControls.ContainsKey(field.Id)).ToArray();
        if (fields.Length == 0) return null;

        var content = new WrapPanel { Margin = new Thickness(14, 12, 14, 4) };
        foreach (var field in fields) content.Children.Add(CreateRemoteFieldPanel(field));
        content.Visibility = initiallyCollapsed ? Visibility.Collapsed : Visibility.Visible;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 58, 71)),
            VerticalAlignment = VerticalAlignment.Center
        });
        var toggle = new Button
        {
            Content = initiallyCollapsed ? "▶" : "▼",
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(54, 81, 94)),
            ToolTip = initiallyCollapsed ? "Bilgileri göster" : "Bilgileri gizle"
        };
        toggle.Click += (_, _) =>
        {
            var expand = content.Visibility != Visibility.Visible;
            content.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = expand ? "▼" : "▶";
            toggle.ToolTip = expand ? "Bilgileri gizle" : "Bilgileri göster";
        };
        Grid.SetColumn(toggle, 1);
        header.Children.Add(toggle);

        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(220, 238, 245)),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(14, 7, 8, 7),
            Child = header
        });
        stack.Children.Add(content);
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 231)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = stack
        };
    }

    private string BuildRemoteScreenTitle(MedulaRemoteScreen screen)
    {
        if (screen.Key is not ("report-detail" or "drug-report-detail")) return screen.Title;

        var fullName = screen.Fields.FirstOrDefault(field => field.Id == "display:name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(fullName))
        {
            var firstName = screen.Fields.FirstOrDefault(field => field.Id == "display:first-name")?.Value?.Trim();
            var lastName = screen.Fields.FirstOrDefault(field => field.Id == "display:last-name")?.Value?.Trim();
            fullName = string.Join(' ', new[] { firstName, lastName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        if (string.IsNullOrWhiteSpace(fullName))
            fullName = FirstNonBlank(activeSnapshot?.PatientDisplayName, OwnerPatientNameBox.Text);

        var diagnosis = screen.Tables.FirstOrDefault(table => table.Title == "Tanı Bilgileri")?
            .Rows.FirstOrDefault()?.FirstOrDefault()?.Trim() ?? string.Empty;
        return string.Join("  •  ", new[] { screen.Title, fullName, diagnosis }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static int ParseTrailingIndex(string value)
    {
        var separator = value.LastIndexOf(':');
        return separator >= 0 && int.TryParse(value[(separator + 1)..], out var index)
            ? index
            : int.MaxValue;
    }

    private FrameworkElement CreateRemoteFieldPanel(MedulaRemoteField field)
    {
        var multiline = field.Kind == "multiline";
        var wide = multiline || field.Label.Contains("Açıklama", StringComparison.OrdinalIgnoreCase)
            || field.Label.Contains("Tanı (*)", StringComparison.OrdinalIgnoreCase);
        var panel = new StackPanel
        {
            Width = multiline ? 1040 : wide ? 600 : 286,
            Margin = new Thickness(0, 0, 12, 10)
        };
        panel.Children.Add(new TextBlock
        {
            Text = field.Label,
            Style = (Style)FindResource("FieldLabel"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = field.Label
        });
        FrameworkElement editor;
        if (field.Kind == "select")
        {
            editor = new ComboBox
            {
                ItemsSource = field.Options,
                DisplayMemberPath = nameof(MedulaRemoteOption.Text),
                SelectedValuePath = nameof(MedulaRemoteOption.Value),
                SelectedValue = field.Value,
                IsEnabled = !field.IsReadOnly,
                Style = (Style)FindResource("EntryCombo")
            };
        }
        else if (field.Kind == "check")
        {
            editor = new CheckBox
            {
                IsChecked = string.Equals(field.Value, "true", StringComparison.OrdinalIgnoreCase),
                IsEnabled = !field.IsReadOnly,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 6, 0, 0)
            };
        }
        else
        {
            editor = new TextBox
            {
                Text = field.Value,
                IsReadOnly = field.IsReadOnly,
                TextWrapping = multiline ? TextWrapping.NoWrap : wide ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = wide,
                MinHeight = multiline ? 260 : wide ? 64 : 0,
                VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                Style = (Style)FindResource("EntryBox")
            };
        }
        if (field.Id == "form1:textarea1" && editor is TextBox itsQrTextBox)
        {
            itsQrTextBox.KeyUp += OnItsQrTextBoxKeyUp;
            itsQrTextBox.TextChanged += OnItsQrTextChanged;
        }
        remoteFieldControls[field.Id] = editor;
        panel.Children.Add(editor);
        return panel;
    }

    private async void OnRemoteTableRowDoubleClick(object sender, MouseButtonEventArgs e) =>
        await ActivateRemoteTableRowAsync(sender as DataGrid, e.OriginalSource as DependencyObject);

    private async void OnRemoteTableRowSingleClick(object sender, MouseButtonEventArgs e) =>
        await ActivateRemoteTableRowAsync(sender as DataGrid, e.OriginalSource as DependencyObject);

    private void OnReportDetailTableMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        RemoteContentScrollViewer.ScrollToVerticalOffset(
            RemoteContentScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private async Task ActivateRemoteTableRowAsync(DataGrid? grid, DependencyObject? originalSource)
    {
        if (busy || grid?.SelectedItem is not DataRowView row
            || FindVisualParent<DataGridRow>(originalSource) is null) return;
        var actionId = row.Row.Table.Columns.Contains(RemoteActionColumnName)
            ? row[RemoteActionColumnName]?.ToString() ?? string.Empty
            : string.Empty;
        var activeScreen = InlineRemotePopup.IsOpen
            ? InlineRemotePopup.Tag as MedulaRemoteScreen
            : RemoteScreenLayer.Tag as MedulaRemoteScreen;
        if (activeScreen is null || string.IsNullOrWhiteSpace(actionId)) return;

        var runtime = grid?.Tag as RemoteTableRuntime;
        if (runtime is not null
            && row.Row.Table.Columns.Contains(RemotePageColumnName)
            && row[RemotePageColumnName] is int rowPage
            && rowPage != runtime.CurrentPage)
        {
            var pageAction = (runtime.Source.PageActions ?? [])
                .FirstOrDefault(candidate => candidate.PageNumber == rowPage);
            if (pageAction is null) return;
            var pageResult = await service.ExecuteTablePageActionAsync(
                runtime.Screen.Key,
                runtime.Screen.Title,
                runtime.Source.Id,
                pageAction.Id,
                lifetime.Token);
            var currentTable = pageResult.Tables.FirstOrDefault(table => table.Id == runtime.Source.Id);
            if (!pageResult.IsOpen || currentTable is null) return;
            runtime.Screen = pageResult;
            runtime.Source = currentTable;
            runtime.CurrentPage = rowPage;
        }

        var visibleValues = row.Row.ItemArray
            .Take(activeScreen.Tables.FirstOrDefault(table => table.Id == runtime?.Source.Id)?.Headers.Count
                ?? Math.Max(0, row.Row.Table.Columns.Count - 4))
            .Select(value => value?.ToString()?.Trim() ?? string.Empty)
            .ToArray();
        var branchParent = row.Row.Table.Columns.Contains(RemoteBranchParentColumnName)
            ? row[RemoteBranchParentColumnName]?.ToString()?.Trim() ?? string.Empty
            : string.Empty;
        var branchChild = visibleValues.ElementAtOrDefault(1)?.TrimStart('│', '├', '└', '─', '›', ' ') ?? string.Empty;
        var selectedText = activeScreen.Key == "branch-select" && branchChild.Length > 0
            ? $"{branchParent} / {branchChild}"
            : visibleValues.FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        SetBusy(true, "Seçim Medula'ya aktarılıyor…");
        try
        {
            var result = await service.ExecuteSubScreenActionAsync(
                activeScreen.Key,
                activeScreen.Title,
                actionId,
                new MedulaRemoteFieldPayload(
                    new Dictionary<string, string>(),
                    new Dictionary<string, bool>()),
                lifetime.Token);
            if (result.IsOpen)
            {
                ShowRemoteScreen(result);
                return;
            }

            if (IsBranchSelectionScreen(activeScreen.Key))
            {
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    BranchNameText.Text = pharmacyApp.Localization.UiText.TranslateSpecialty(selectedText);
                    branchExplicitlySelected = true;
                }
                CloseInlinePopupWithoutCleanup();
                ShowResult(new MedulaPrescriptionPageResult(
                    true, true, "Medula Reçete", result.Url,
                    "Branş seçimi Medula'ya aktarıldı.", string.Empty));
                await ReconcileDoctorAsync(doctorSearchSucceeded: medulaDoctorSpecialties is not null);
            }
            else if (activeScreen.Key == "facility-search")
            {
                var facilityCode = MedulaRemoteScreenRefiner.FindColumnValue(
                    row.Row.Table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray(),
                    row.Row.ItemArray.Select(value => value?.ToString() ?? string.Empty).ToArray(),
                    "Tesis Kodu");
                if (!string.IsNullOrWhiteSpace(facilityCode)) FacilityCodeBox.Text = facilityCode;
                CloseInlinePopupWithoutCleanup();
                ShowResult(new MedulaPrescriptionPageResult(
                    true, true, "Medula Reçete", result.Url,
                    "Tesis seçimi Medula'ya aktarıldı.", string.Empty));
            }
            else
            {
                ShowRemoteActionFailure(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static void AppendRemoteRows(RemoteTableRuntime runtime, MedulaRemoteTable table)
    {
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var sourceRow = table.Rows[rowIndex];
            var key = string.Join('\u001f', sourceRow);
            if (!runtime.RowKeys.Add(key)) continue;
            var row = runtime.Data.NewRow();
            for (var index = 0; index < Math.Min(sourceRow.Count, table.Headers.Count); index++)
            {
                if (runtime.Data.Columns.Contains(table.Headers[index]))
                    row[table.Headers[index]] = sourceRow[index];
                else
                    row[index] = sourceRow[index];
            }
            row[RemoteActionColumnName] = table.RowActionIds?.ElementAtOrDefault(rowIndex) ?? string.Empty;
            row[RemotePageColumnName] = runtime.CurrentPage;
            row[RemoteOrderColumnName] = runtime.Data.Rows.Count;
            row[RemoteBranchParentColumnName] = table.RowParentValues?.ElementAtOrDefault(rowIndex) ?? string.Empty;
            if (runtime.Data.Columns.Contains(ReportRowKindColumnName))
                row[ReportRowKindColumnName] = "data";
            runtime.Data.Rows.Add(row);
        }
        if (runtime.CountText is not null)
            runtime.CountText.Text = $"{runtime.Data.Rows.Count} satır";
        runtime.Grid.Items.Refresh();
    }

    private async Task PopulateReportIngredientMedicationsAsync(
        RemoteTableRuntime runtime,
        RemoteTableRuntime? medicationRuntime)
    {
        if (!runtime.Data.Columns.Contains("SGK Kodu")
            || !runtime.Data.Columns.Contains(ReportSgkColorColumnName)
            || medicationRuntime is null) return;

        var currentDrugs = Drugs
            .Where(drug => !string.IsNullOrWhiteSpace(drug.Barcode) && drug.Barcode != "?")
            .Select(drug => new { Entry = drug, Barcode = drug.Barcode.Trim(), Name = drug.Name.Trim() })
            .ToArray();
        try
        {
            var sgkCodesByBarcode = await directoryRepository.ReadDrugSgkCodesAsync(
                currentDrugs.Select(drug => drug.Barcode), lifetime.Token);
            var palette = new[]
            {
                "#DCEFFC", "#E4F3DF", "#FBE8C9", "#EEE3FA",
                "#F8DFE7", "#DDF3EF", "#F3E5D8", "#E4E9FA"
            };
            var sourceReportRows = runtime.Data.Rows.Cast<DataRow>()
                .Where(row => !runtime.Data.Columns.Contains(ReportRowKindColumnName)
                    || row[ReportRowKindColumnName]?.ToString() is not ("padding" or "separator"))
                .Select(row => row.ItemArray.ToArray())
                .ToArray();
            var sgkOrdinal = runtime.Data.Columns.IndexOf("SGK Kodu");
            var ingredientNameOrdinal = runtime.Data.Columns.IndexOf("Adı");
            var reportCodes = sourceReportRows
                .Select(values => MedulaReportIngredientMatching.NormalizeSgkCode(
                    values[sgkOrdinal]?.ToString()))
                .ToArray();
            var medications = currentDrugs.Select(drug =>
            {
                var sgkCode = sgkCodesByBarcode.TryGetValue(drug.Barcode, out var code)
                    ? MedulaReportIngredientMatching.NormalizeSgkCode(code)
                    : string.Empty;
                return new { drug.Entry, drug.Barcode, drug.Name, SgkCode = sgkCode };
            }).ToArray();
            var plan = MedulaReportRowAlignment.Build(
                reportCodes,
                medications.Select(item => (string?)item.SgkCode).ToArray());
            var colorBySgk = plan
                .Where(slot => slot.MatchCode.Length > 0)
                .Select(slot => slot.MatchCode)
                .Distinct(StringComparer.Ordinal)
                .Select((code, index) => (code, color: palette[index % palette.Length]))
                .ToDictionary(item => item.code, item => item.color, StringComparer.Ordinal);
            var activeIngredientBySgk = sourceReportRows
                .Select((values, index) => new
                {
                    Code = reportCodes[index],
                    Name = ingredientNameOrdinal >= 0
                        ? values[ingredientNameOrdinal]?.ToString()?.Trim() ?? string.Empty
                        : string.Empty
                })
                .Where(item => item.Code.Length > 0)
                .GroupBy(item => item.Code, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

            runtime.Data.Rows.Clear();
            medicationRuntime.Data.Rows.Clear();
            for (var slotIndex = 0; slotIndex < plan.Count; slotIndex++)
            {
                var slot = plan[slotIndex];
                var color = colorBySgk.GetValueOrDefault(slot.MatchCode, string.Empty);
                var reportRow = runtime.Data.NewRow();
                if (slot.ReportRowIndex is int reportIndex)
                    reportRow.ItemArray = sourceReportRows[reportIndex].ToArray();
                reportRow[ReportSgkColorColumnName] = color;
                reportRow[ReportRowKindColumnName] = slot.IsSeparator
                    ? "separator"
                    : slot.ReportRowIndex.HasValue ? "data" : "padding";
                reportRow[RemoteOrderColumnName] = slotIndex;
                if (!slot.ReportRowIndex.HasValue)
                {
                    reportRow[RemoteActionColumnName] = string.Empty;
                    reportRow[RemotePageColumnName] = 0;
                    reportRow[RemoteBranchParentColumnName] = string.Empty;
                }
                runtime.Data.Rows.Add(reportRow);

                var medicationRow = medicationRuntime.Data.NewRow();
                if (slot.MedicationRowIndex is int medicationIndex)
                {
                    var medication = medications[medicationIndex];
                    medicationRow["İlaç Adı"] = string.IsNullOrWhiteSpace(medication.Name)
                        ? medication.Barcode
                        : medication.Name;
                    medicationRow["Doz"] = $"{medication.Entry.DoseCount} × {medication.Entry.DoseAmount}";
                    medicationRow["Adet"] = medication.Entry.Quantity;
                    medicationRow["Etkin Madde"] = activeIngredientBySgk.GetValueOrDefault(
                        medication.SgkCode, "—");
                }
                medicationRow[ReportSgkColorColumnName] = color;
                medicationRow[ReportRowKindColumnName] = slot.IsSeparator
                    ? "separator"
                    : slot.MedicationRowIndex.HasValue ? "data" : "padding";
                medicationRuntime.Data.Rows.Add(medicationRow);
            }
            runtime.Grid.Items.Refresh();
            medicationRuntime.Grid.Items.Refresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            runtime.Grid.Items.Refresh();
            medicationRuntime.Grid.Items.Refresh();
        }
    }

    private Border CreateReportPrescriptionMedicationCard(RemoteTableRuntime reportRuntime)
    {
        var data = new DataTable();
        data.Columns.Add("İlaç Adı");
        data.Columns.Add("Doz");
        data.Columns.Add("Adet");
        data.Columns.Add("Etkin Madde");
        data.Columns.Add(ReportSgkColorColumnName);
        data.Columns.Add(ReportRowKindColumnName);
        var grid = new DataGrid
        {
            Style = (Style)FindResource("MedulaDataGridStyle"),
            ItemsSource = data.DefaultView,
            AutoGenerateColumns = true,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            RowHeaderWidth = 0,
            MinHeight = 90,
            MaxHeight = double.PositiveInfinity
        };
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        var runtime = new RemoteTableRuntime
        {
            Screen = reportRuntime.Screen,
            Source = new MedulaRemoteTable(
                "app:prescription-medications",
                ["İlaç Adı", "Doz", "Adet", "Etkin Madde"],
                [],
                Title: "Reçetedeki İlaçlar"),
            Data = data,
            Grid = grid,
            View = data.DefaultView,
            Exhausted = true,
            Companion = reportRuntime
        };
        reportRuntime.Companion = runtime;
        grid.Tag = runtime;
        grid.LoadingRow += (_, args) => ApplyReportSgkRowStyle(args.Row);
        grid.AutoGeneratingColumn += (_, args) =>
        {
            if (args.PropertyName is ReportSgkColorColumnName or ReportRowKindColumnName)
            {
                args.Cancel = true;
                return;
            }
            if (args.Column is not DataGridTextColumn column) return;
            column.Binding = new Binding($"[{args.PropertyName}]");
            column.Width = args.PropertyName switch
            {
                "İlaç Adı" => new DataGridLength(2.2, DataGridLengthUnitType.Star),
                "Etkin Madde" => new DataGridLength(1.6, DataGridLengthUnitType.Star),
                _ => new DataGridLength(0.8, DataGridLengthUnitType.Star)
            };
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            textStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3)));
            column.ElementStyle = textStyle;
        };
        var content = new StackPanel();
        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(231, 242, 246)),
            Padding = new Thickness(12, 9, 12, 8),
            Child = new TextBlock
            {
                Text = "Reçetedeki İlaçlar",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(38, 58, 71))
            }
        });
        content.Children.Add(grid);
        return new Border
        {
            Tag = runtime,
            Style = (Style)FindResource("SectionCard"),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
    }

    private static void ApplyReportSgkRowStyle(DataGridRow row)
    {
        if (row.Item is not DataRowView view
            || !view.Row.Table.Columns.Contains(ReportSgkColorColumnName))
            return;
        var kind = view.Row.Table.Columns.Contains(ReportRowKindColumnName)
            ? view[ReportRowKindColumnName]?.ToString()
            : "data";
        row.Height = kind == "separator" ? 14 : 36;
        row.IsHitTestVisible = kind == "data";
        row.BorderThickness = kind == "separator" ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        var value = view[ReportSgkColorColumnName]?.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            row.Background = kind == "separator" ? Brushes.Transparent : Brushes.White;
            return;
        }
        try
        {
            row.Background = (Brush)new BrushConverter().ConvertFromString(value)!;
        }
        catch
        {
            row.ClearValue(BackgroundProperty);
        }
    }

    private async void OnRemoteTableScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || viewer.ScrollableHeight <= 0
            || viewer.VerticalOffset < viewer.ScrollableHeight
                - Math.Max(1, viewer.ViewportHeight * 0.25)
            || sender is not DataGrid { Tag: RemoteTableRuntime runtime }) return;
        await LoadNextRemoteTablePageAsync(runtime);
    }

    private async Task<int> LoadNextRemoteTablePageAsync(RemoteTableRuntime runtime)
    {
        if (runtime.Loading || runtime.Exhausted) return 0;
        var numeric = (runtime.Source.PageActions ?? [])
            .Where(action => action.PageNumber > 0 && !runtime.LoadedPageNumbers.Contains(action.PageNumber))
            .OrderBy(action => action.PageNumber)
            .FirstOrDefault();
        var action = numeric ?? (runtime.Source.PageActions ?? [])
            .FirstOrDefault(candidate => candidate.Caption.Equals("İleri", StringComparison.OrdinalIgnoreCase)
                || candidate.Caption is ">" or ">>");
        if (action is null || runtime.SequentialLoads >= 100)
        {
            runtime.Exhausted = true;
            return 0;
        }

        runtime.Loading = true;
        var before = runtime.Data.Rows.Count;
        try
        {
            var result = await service.ExecuteTablePageActionAsync(
                runtime.Screen.Key,
                runtime.Screen.Title,
                runtime.Source.Id,
                action.Id,
                lifetime.Token);
            var nextTable = result.Tables.FirstOrDefault(table => table.Id == runtime.Source.Id);
            if (!result.IsOpen || nextTable is null)
            {
                runtime.Exhausted = true;
                return 0;
            }
            runtime.SequentialLoads++;
            runtime.CurrentPage = action.PageNumber > 0 ? action.PageNumber : runtime.CurrentPage + 1;
            if (action.PageNumber > 0) runtime.LoadedPageNumbers.Add(action.PageNumber);
            AppendRemoteRows(runtime, nextTable);
            runtime.Screen = result;
            runtime.Source = nextTable;
            if (runtime.Data.Columns.Contains(ReportSgkColorColumnName))
                await PopulateReportIngredientMedicationsAsync(runtime, runtime.Companion);
            var added = runtime.Data.Rows.Count - before;
            if (added == 0) runtime.Exhausted = true;
            return added;
        }
        finally
        {
            runtime.Loading = false;
        }
    }

    private async void OnRemoteTableSorting(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid { Tag: RemoteTableRuntime runtime }) return;
        e.Handled = true;
        SetBusy(true, "Tüm tablo sayfaları sıralama için yükleniyor…");
        try
        {
            while (await LoadNextRemoteTablePageAsync(runtime) > 0)
            {
            }
            var direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            foreach (var column in runtime.Grid.Columns) column.SortDirection = null;
            e.Column.SortDirection = direction;
            var name = e.Column.SortMemberPath;
            if (string.IsNullOrWhiteSpace(name)) name = e.Column.Header?.ToString() ?? string.Empty;
            if (runtime.Data.Columns.Contains(name))
                runtime.View.Sort = $"[{name.Replace("]", "]]", StringComparison.Ordinal)}] "
                    + (direction == ListSortDirection.Ascending ? "ASC" : "DESC");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnResetRemoteTableSort(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RemoteTableRuntime runtime }) return;
        runtime.View.Sort = $"[{RemoteOrderColumnName}] ASC";
        foreach (var column in runtime.Grid.Columns) column.SortDirection = null;
    }

    private void ShowRemoteScreenFailure(MedulaRemoteScreen screen)
    {
        RemoteScreenLayer.Visibility = Visibility.Collapsed;
        RemoteScreenLayer.Tag = null;
        OffLabelTabsPanel.Visibility = Visibility.Collapsed;
        CloseInlinePopupWithoutCleanup();
        SetBrowserWarning(screen.Message);
        ResponseMessageText.Text = screen.Message;
        ResponseUrlText.Text = screen.Url;
        ResponsePageText.Text = string.Empty;
        ResponseBanner.Background = new SolidColorBrush(Color.FromRgb(252, 235, 235));
        ResponseMessageText.Foreground = new SolidColorBrush(Color.FromRgb(143, 48, 48));
    }

    private void ShowRemoteActionFailure(MedulaRemoteScreen screen)
    {
        if (InlineRemotePopup.IsOpen)
            InlineRemoteMessageText.Text = screen.Message;
        else if (RemoteScreenLayer.Visibility == Visibility.Visible)
            RemoteMessageText.Text = screen.Message;
        else
            ShowRemoteScreenFailure(screen);
    }

    private static bool IsInlineRemoteScreen(string key) => key is
        "facility-search" or "facility-departments" or "branch-select" or "doctor-branch-select" or "warning-code"
        or "drug-special" or "drug-equivalent" or "report-selection"
        or "treatment-scheme" or "new-warning-code" or "uts-operations";

    private static bool IsBranchSelectionScreen(string key) =>
        key is "branch-select" or "doctor-branch-select";

    private static string RemoteMessageForDisplay(MedulaRemoteScreen screen)
    {
        if (IsBranchSelectionScreen(screen.Key)) return string.Empty;
        if (screen.Key is "report-detail" or "drug-report-detail"
            && screen.Message.Trim().Equals("Rapor detayı hazır.", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var message = screen.Message.Trim();
        return message.EndsWith("ekranı hazır.", StringComparison.OrdinalIgnoreCase)
            || message.EndsWith("bilgileri hazır.", StringComparison.OrdinalIgnoreCase)
            || message.Equals("Medula ekranı güncellendi.", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : message;
    }

    private static bool ShouldShowRemoteTableTitle(
        MedulaRemoteScreen screen,
        MedulaRemoteTable table)
    {
        if (string.IsNullOrWhiteSpace(table.Title) || IsBranchSelectionScreen(screen.Key)) return false;
        var title = table.Title.Trim();
        var screenTitle = screen.Title.Trim();
        return !title.Equals(screenTitle, StringComparison.OrdinalIgnoreCase)
            && !screenTitle.StartsWith(title + " —", StringComparison.OrdinalIgnoreCase);
    }

    private void CloseInlinePopupWithoutCleanup()
    {
        if (!InlineRemotePopup.IsOpen)
        {
            inlinePopupBackendOpen = false;
            return;
        }
        suppressInlinePopupCleanup = true;
        try
        {
            InlineRemotePopup.IsOpen = false;
            inlinePopupBackendOpen = false;
        }
        finally
        {
            suppressInlinePopupCleanup = false;
        }
    }

    private async Task RunActionAsync(
        MedulaPrescriptionCommand command,
        string caption,
        bool confirmationAccepted,
        Guid? saleOperationId = null)
    {
        MedulaPrescriptionSnapshot? deletedOfflineSnapshot = null;
        SetBusy(true, $"{caption} işlemi Medula'ya gönderiliyor…");
        try
        {
            if (command == MedulaPrescriptionCommand.FinalizeQr && saleOperationId is null)
            {
                if (activeSnapshot?.PrescriptionUid is not { } prescriptionUid)
                {
                    SetBrowserWarning("Karekod sonlandırma için kayıtlı reçete kimliği bulunamadı.");
                    return;
                }
                try
                {
                    saleOperationId = (await prescriptionSaleRepository.PrepareFinalizationAsync(
                        prescriptionUid,
                        CurrentUserId(),
                        lifetime.Token)).OperationId;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    SetBrowserWarning(exception.Message);
                    return;
                }
            }
            if (command == MedulaPrescriptionCommand.CancelQrFinalization && saleOperationId is null)
            {
                if (activeSnapshot?.PrescriptionUid is not { } prescriptionUid)
                {
                    SetBrowserWarning("Karekod sonlandırma iptali için kayıtlı reçete kimliği bulunamadı.");
                    return;
                }
                try
                {
                    saleOperationId = (await prescriptionSaleRepository
                        .PrepareFinalizationCancellationAsync(
                            prescriptionUid, CurrentUserId(), lifetime.Token)).OperationId;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    SetBrowserWarning(exception.Message);
                    return;
                }
            }
            if (command == MedulaPrescriptionCommand.DoctorSearch) medulaDoctorSpecialties = null;
            var input = BuildActionInput(command);
            var result = await service.ExecutePrescriptionCommandAsync(
                input,
                command,
                caption,
                confirmationAccepted,
                lifetime.Token);
            result = PreservePrescriptionIdentity(result);
            if (command == MedulaPrescriptionCommand.SavePrescription
                && string.IsNullOrWhiteSpace(result.ConfirmationMessage)
                && !MedulaPrescriptionSaveGuard.IsConfirmed(result))
            {
                var saveFailure = !string.IsNullOrWhiteSpace(result.WarningText)
                    ? result.WarningText
                    : "Medula reçete kaydını doğrulamadı. Yerel reçete kaydı oluşturulmadı.";
                result = result with
                {
                    IsSuccess = false,
                    Message = saveFailure,
                    WarningText = saveFailure,
                    SuccessText = null
                };
            }
            var requiresBranchSelection = command == MedulaPrescriptionCommand.DoctorSearch
                && RequiresDoctorBranchSelection(result);
            var doctorSearchSucceeded = command == MedulaPrescriptionCommand.DoctorSearch
                && result.IsSuccess
                && string.IsNullOrWhiteSpace(result.WarningText);
            if (command == MedulaPrescriptionCommand.DoctorSearch && (doctorSearchSucceeded || requiresBranchSelection))
            {
                ApplyDoctorValues(result.FieldValues);
                if (!requiresBranchSelection)
                    await ReconcileDoctorAsync(doctorSearchSucceeded: true);
            }
            else if (command == MedulaPrescriptionCommand.SavePrescription
                && MedulaPrescriptionSaveGuard.IsConfirmed(result)
                && string.IsNullOrWhiteSpace(result.ConfirmationMessage))
            {
                await ReconcilePendingPatientAsync();
                await ReconcileDoctorAsync(prescriptionSaved: true);
                try
                {
                    var captured = await service.CaptureCurrentPrescriptionAsync(
                        OwnerPatientNameBox.Text.Trim(),
                        activeSnapshot?.SourceListItem,
                        activeSnapshot?.PrescriptionUid,
                        activeSnapshot?.VersionNumber ?? 0,
                        lifetime.Token)
                        ?? new MedulaPrescriptionSnapshot(
                            result, input.Drugs, input.Diagnoses, OwnerPatientNameBox.Text.Trim(),
                            activeSnapshot?.SourceListItem, activeSnapshot?.PrescriptionUid,
                            activeSnapshot?.VersionNumber ?? 0);
                    MedulaDrugQuantityAdjustments.AnnotateReductions(input.Drugs, captured.Drugs);
                    captured = PreservePrescriptionIdentity(captured);
                    captured = PromoteToSavedPrescriptionContext(captured, input);
                    captured = captured with { StoredFilterDate = activeSnapshot?.StoredFilterDate };
                    activeSnapshot = captured with { IsLocalArchive = false };
                    service.RememberSavedPrescriptionContext(activeSnapshot);
                    var persistenceRecord = MedulaPrescriptionPersistenceMapper.FromSnapshot(captured, input);
                    var inserted = await prescriptionRepository.InsertVersionAsync(
                        persistenceRecord,
                        lifetime.Token);
                    activeSnapshot = captured with
                    {
                        PrescriptionUid = inserted.PrescriptionUid,
                        VersionNumber = inserted.VersionNumber,
                        IsLocalArchive = false,
                        StoredFilterDate = persistenceRecord.MedulaFilterDate
                    };
                    service.RememberSavedPrescriptionContext(activeSnapshot);
                    await RefreshOwnerLoyaltyAsync(input.PatientIdentityNumber, force: true);
                    ResponseUrlText.ToolTip =
                        $"Veritabanı reçete kayıt no: {inserted.RecordId} · Sürüm: {inserted.VersionNumber}";
                    try
                    {
                        var pendingSaleId = await prescriptionSaleRepository.EnsurePendingSaleAsync(
                            persistenceRecord with { PrescriptionUid = inserted.PrescriptionUid },
                            inserted.PrescriptionUid,
                            lifetime.Token);
                        ResponseUrlText.ToolTip += $" · Bekleyen satış: P{pendingSaleId}";
                    }
                    catch (Exception saleException) when (saleException is not OperationCanceledException)
                    {
                        pharmacyApp.Localization.EnglishMessageBox.Show(
                            $"Reçete kaydedildi ancak bekleyen satış ödemeleri oluşturulamadı: {saleException.Message}",
                            "Reçete Satışı",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    pharmacyApp.Localization.EnglishMessageBox.Show(
                        $"Reçete Medula'da kaydedildi ancak veritabanı geçmişine eklenemedi: {exception.Message}",
                        "Reçete Veritabanı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            else if (command == MedulaPrescriptionCommand.DeletePrescription
                && result.IsSuccess
                && string.IsNullOrWhiteSpace(result.ConfirmationMessage))
            {
                try
                {
                    if (activeSnapshot?.PrescriptionUid is not { } prescriptionUid)
                        throw new InvalidOperationException("Silinen reçetenin yerel kimliği bulunamadı.");
                    var deleted = await prescriptionRepository.MarkDeletedAsync(
                        prescriptionUid,
                        (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty,
                        lifetime.Token);
                    if (deleted is null)
                        throw new InvalidOperationException("Silinen reçetenin güncel yerel kaydı bulunamadı.");
                    activeSnapshot = activeSnapshot with
                    {
                        VersionNumber = deleted.VersionNumber,
                        IsLocalArchive = true,
                        SourceListItem = null
                    };
                    deletedOfflineSnapshot = activeSnapshot;
                    ResponseUrlText.ToolTip =
                        $"Silme işlemi veritabanına kaydedildi · Sürüm: {deleted.VersionNumber}";
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    pharmacyApp.Localization.EnglishMessageBox.Show(
                        $"Reçete Medula'da silindi ancak silme günlüğüne eklenemedi: {exception.Message}",
                        "Reçete Veritabanı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            else if (command == MedulaPrescriptionCommand.FinalizeQr
                && saleOperationId is { } finalizationOperationId
                && string.IsNullOrWhiteSpace(result.ConfirmationMessage))
            {
                if (IsQrFinalizationConfirmed(result))
                {
                    await prescriptionSaleRepository.MarkRemoteConfirmedAsync(
                        finalizationOperationId,
                        result.SuccessText ?? result.Message,
                        lifetime.Token);
                    try
                    {
                        await PersistFinalizedPrescriptionAsync(
                            result,
                            input,
                            finalizationOperationId,
                            lifetime.Token);
                        SetBrowserConfirmation("Reçete karekodları Medula ve yerel kayıtlarda sonlandırıldı.");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                            finalizationOperationId,
                            exception.Message,
                            CancellationToken.None);
                        SetBrowserWarning(
                            $"Karekodlar Medula'da sonlandırıldı ancak yerel kayıt mutabakat bekliyor: {exception.Message}");
                    }
                }
                else
                {
                    var error = result.WarningText ?? result.Message;
                    if (!string.IsNullOrWhiteSpace(result.WarningText))
                        await prescriptionSaleRepository.MarkFailedAsync(
                            finalizationOperationId, error, lifetime.Token);
                    else
                        await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                            finalizationOperationId,
                            "Medula karekod sonlandırma sonucu kesin olarak doğrulanamadı. İşlem tekrar gönderilmedi.",
                            lifetime.Token);
                }
            }
            else if (command == MedulaPrescriptionCommand.CancelQrFinalization
                && saleOperationId is { } cancellationOperationId
                && string.IsNullOrWhiteSpace(result.ConfirmationMessage))
            {
                if (MedulaQrFinalizationCancellationResultDetector.IsConfirmed(result))
                {
                    await prescriptionSaleRepository.MarkRemoteConfirmedAsync(
                        cancellationOperationId,
                        result.SuccessText ?? result.Message,
                        lifetime.Token);
                    try
                    {
                        await PersistUnfinalizedPrescriptionAsync(
                            result, input, cancellationOperationId, lifetime.Token);
                        SetBrowserConfirmation(
                            "Reçete karekod sonlandırması Medula ve yerel kayıtlarda iptal edildi.");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                            cancellationOperationId, exception.Message, CancellationToken.None);
                        SetBrowserWarning(
                            $"Karekod sonlandırma Medula'da iptal edildi ancak yerel kayıt mutabakat bekliyor: {exception.Message}");
                    }
                }
                else
                {
                    var error = result.WarningText ?? result.Message;
                    if (!string.IsNullOrWhiteSpace(result.WarningText))
                        await prescriptionSaleRepository.MarkFailedAsync(
                            cancellationOperationId, error, lifetime.Token);
                    else
                        await prescriptionSaleRepository.MarkReconciliationRequiredAsync(
                            cancellationOperationId,
                            "Medula karekod sonlandırma iptal sonucu kesin olarak doğrulanamadı. İşlem tekrar gönderilmedi.",
                            lifetime.Token);
                }
            }
            var openedDocumentScreen = result.OpenedScreen is { IsOpen: true, Key: "saved-prescription-document" };
            // The document form may live in a secondary WebView. Its probe is not
            // the prescription page and must never replace the saved prescription UI.
            if (!openedDocumentScreen) ShowResult(result);
            if (deletedOfflineSnapshot is not null)
                ApplyPrescriptionSnapshot(deletedOfflineSnapshot);
            if (result.OpenedScreen is { IsOpen: true } openedScreen)
            {
                if (openedScreen.Key == "saved-prescription-document")
                    ShowDocumentPopup(openedScreen);
                else
                    ShowRemoteScreen(openedScreen);
            }
            if (!string.IsNullOrWhiteSpace(result.DownloadedFilePath))
                OpenDownloadedPrescription(result.DownloadedFilePath);

            if (requiresBranchSelection)
            {
                currentRemoteTitle = "Branş Seç";
                remotePopupAnchor = DoctorSearchButton;
                OffLabelTabsPanel.Visibility = Visibility.Collapsed;
                var branchScreen = await service.ReadDoctorBranchSelectionAsync(lifetime.Token);
                if (branchScreen.IsOpen)
                {
                    medulaDoctorSpecialties = branchScreen.Tables
                        .SelectMany(table => table.Rows)
                        .SelectMany(row => row)
                        .Select(value => value.Trim())
                        .Where(value => value.Length > 0)
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                    await ReconcileDoctorAsync(doctorSearchSucceeded: true);
                    ShowRemoteScreen(branchScreen);
                }
                else ShowRemoteScreenFailure(branchScreen);
                return;
            }

            if (!confirmationAccepted && !string.IsNullOrWhiteSpace(result.ConfirmationMessage))
            {
                var accepted = ShowTurkishConfirmation(
                    result.ConfirmationMessage,
                    "Medula İşlem Onayı");
                if (accepted)
                    await RunActionAsync(
                        command,
                        caption,
                        confirmationAccepted: true,
                        saleOperationId);
                else if (saleOperationId is { } cancelledOperationId)
                    await prescriptionSaleRepository.MarkCancelledAsync(
                        cancelledOperationId, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool RequiresDoctorBranchSelection(MedulaPrescriptionPageResult result)
    {
        return new[] { result.WarningText, result.Message, result.PageText }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => value!.Contains("birden fazla branşı", StringComparison.OrdinalIgnoreCase)
                && value.Contains("branş seçimi yapmanız gerekmektedir", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsQrFinalizationConfirmed(MedulaPrescriptionPageResult result)
        => MedulaQrFinalizationResultDetector.IsConfirmed(result);

    private async Task PersistFinalizedPrescriptionAsync(
        MedulaPrescriptionPageResult result,
        MedulaPrescriptionInput input,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var captured = await service.CaptureCurrentPrescriptionAsync(
            OwnerPatientNameBox.Text.Trim(),
            activeSnapshot?.SourceListItem,
            activeSnapshot?.PrescriptionUid,
            activeSnapshot?.VersionNumber ?? 0,
            cancellationToken)
            ?? new MedulaPrescriptionSnapshot(
                result,
                input.Drugs,
                input.Diagnoses,
                OwnerPatientNameBox.Text.Trim(),
                activeSnapshot?.SourceListItem,
                activeSnapshot?.PrescriptionUid,
                activeSnapshot?.VersionNumber ?? 0);
        captured = PreservePrescriptionIdentity(captured);
        captured = PromoteToSavedPrescriptionContext(captured, input);
        var fields = new Dictionary<string, string>(
            captured.Page.FieldValues ?? new Dictionary<string, string>(),
            StringComparer.Ordinal)
        {
            ["medula:qr-status"] = "Sonlandırıldı"
        };
        captured = captured with
        {
            Page = captured.Page with { FieldValues = fields },
            StoredFilterDate = activeSnapshot?.StoredFilterDate
        };
        var record = MedulaPrescriptionPersistenceMapper.FromSnapshot(captured, input) with
        {
            ItsFinalized = true,
            MedulaQrStatus = "Sonlandırıldı"
        };
        var inserted = await prescriptionRepository.InsertVersionAsync(record, cancellationToken);
        activeSnapshot = captured with
        {
            PrescriptionUid = inserted.PrescriptionUid,
            VersionNumber = inserted.VersionNumber,
            IsLocalArchive = false,
            StoredFilterDate = record.MedulaFilterDate
        };
        service.RememberSavedPrescriptionContext(activeSnapshot);
        await prescriptionSaleRepository.CompleteFinalizationAsync(
            operationId,
            result.SuccessText ?? result.Message,
            cancellationToken);
        ResponseUrlText.ToolTip =
            $"Karekod sonlandırma veritabanına kaydedildi · Sürüm: {inserted.VersionNumber}";
    }

    private async Task PersistUnfinalizedPrescriptionAsync(
        MedulaPrescriptionPageResult result,
        MedulaPrescriptionInput input,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var captured = await service.CaptureCurrentPrescriptionAsync(
            OwnerPatientNameBox.Text.Trim(),
            activeSnapshot?.SourceListItem,
            activeSnapshot?.PrescriptionUid,
            activeSnapshot?.VersionNumber ?? 0,
            cancellationToken)
            ?? new MedulaPrescriptionSnapshot(
                result,
                input.Drugs,
                input.Diagnoses,
                OwnerPatientNameBox.Text.Trim(),
                activeSnapshot?.SourceListItem,
                activeSnapshot?.PrescriptionUid,
                activeSnapshot?.VersionNumber ?? 0);
        captured = PreservePrescriptionIdentity(captured);
        captured = PromoteToSavedPrescriptionContext(captured, input);
        var fields = new Dictionary<string, string>(
            captured.Page.FieldValues ?? new Dictionary<string, string>(),
            StringComparer.Ordinal)
        {
            ["medula:qr-status"] = "Sonlandırılmadı"
        };
        captured = captured with
        {
            Page = captured.Page with { FieldValues = fields },
            StoredFilterDate = activeSnapshot?.StoredFilterDate
        };
        var record = MedulaPrescriptionPersistenceMapper.FromSnapshot(captured, input) with
        {
            ItsFinalized = false,
            MedulaQrStatus = "Sonlandırılmadı"
        };
        var inserted = await prescriptionRepository.InsertVersionAsync(record, cancellationToken);
        activeSnapshot = captured with
        {
            PrescriptionUid = inserted.PrescriptionUid,
            VersionNumber = inserted.VersionNumber,
            IsLocalArchive = false,
            StoredFilterDate = record.MedulaFilterDate
        };
        service.RememberSavedPrescriptionContext(activeSnapshot);
        await prescriptionSaleRepository.CompleteFinalizationCancellationAsync(
            operationId, result.SuccessText ?? result.Message, cancellationToken);
        ResponseUrlText.ToolTip =
            $"Karekod sonlandırma iptali veritabanına kaydedildi · Sürüm: {inserted.VersionNumber}";
    }

    private static void OpenDownloadedPrescription(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("İndirilen reçete çıktısı bulunamadı.", filePath);
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show(
                $"Reçete çıktısı indirildi ancak açılamadı.\n{filePath}\n\n{exception.Message}",
                "Reçete Yazdır",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ShowDocumentPopup(MedulaRemoteScreen screen)
    {
        var openingPopup = DocumentPopupLayer.Visibility != Visibility.Visible;
        if (openingPopup)
        {
            latestDocumentBrowserMessage = string.Empty;
            latestDocumentBrowserMessageSeverity = null;
        }
        activeDocumentScreen = screen;
        selectedDocumentFilePath = string.Empty;
        DocumentFileNameText.Text = "Dosya seçilmedi";
        var documentMessage = latestDocumentBrowserMessage.Length > 0
            ? latestDocumentBrowserMessage
            : screen.Message.EndsWith("ekranı hazır.", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : screen.Message;
        var messageSeverity = latestDocumentBrowserMessageSeverity
            ?? (LooksLikeMedulaDocumentError(documentMessage) ? MedulaMessageSeverity.Error : null);
        DocumentMessageText.Foreground = new SolidColorBrush(messageSeverity switch
        {
            MedulaMessageSeverity.Error => Color.FromRgb(180, 52, 52),
            MedulaMessageSeverity.Success => Color.FromRgb(23, 107, 77),
            MedulaMessageSeverity.Notice => Color.FromRgb(158, 103, 16),
            _ => Color.FromRgb(82, 97, 108)
        });
        DocumentMessageText.Text = documentMessage;
        var typeField = screen.Fields.FirstOrDefault(field => field.Kind == "select");
        DocumentTypeComboBox.ItemsSource = typeField?.Options ?? [];
        DocumentTypeComboBox.SelectedValue = typeField?.Value;
        if (DocumentTypeComboBox.SelectedIndex < 0 && DocumentTypeComboBox.Items.Count > 0)
            DocumentTypeComboBox.SelectedIndex = 0;

        var table = screen.Tables.FirstOrDefault();
        if (table is null || table.Rows.Count == 0)
        {
            DocumentTable.ItemsSource = null;
            DocumentTable.Visibility = Visibility.Collapsed;
            DocumentEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            var data = new DataTable();
            for (var index = 0; index < table.Headers.Count; index++)
            {
                var name = string.IsNullOrWhiteSpace(table.Headers[index]) ? $"Alan {index + 1}" : table.Headers[index];
                var unique = name;
                for (var suffix = 2; data.Columns.Contains(unique); suffix++) unique = $"{name} ({suffix})";
                data.Columns.Add(unique);
            }
            foreach (var sourceRow in table.Rows)
            {
                var row = data.NewRow();
                for (var index = 0; index < data.Columns.Count; index++)
                    row[index] = sourceRow.ElementAtOrDefault(index) ?? string.Empty;
                data.Rows.Add(row);
            }
            DocumentTable.ItemsSource = data.DefaultView;
            DocumentTable.Visibility = Visibility.Visible;
            DocumentEmptyText.Visibility = Visibility.Collapsed;
        }
        DocumentPopupLayer.Visibility = Visibility.Visible;
        ChangeBrowserMessageSurface("popup:saved-prescription-document");
    }

    private async void OnSelectDocumentFileClick(object sender, RoutedEventArgs e)
    {
        if (busy || activeDocumentScreen is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select a document to attach to the prescription",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        DocumentSelectFileButton.IsEnabled = false;
        DocumentAddButton.IsEnabled = false;
        latestDocumentBrowserMessage = string.Empty;
        latestDocumentBrowserMessageSeverity = null;
        DocumentMessageText.Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 108));
        DocumentMessageText.Text = "Dosya Medula doküman alanında seçiliyor…";
        try
        {
            var selection = await service.SelectPrescriptionDocumentFileAsync(
                activeDocumentScreen, dialog.FileName, lifetime.Token);
            DocumentMessageText.Text = selection.Message;
            DocumentMessageText.Foreground = new SolidColorBrush(selection.IsSuccess
                ? Color.FromRgb(23, 107, 77)
                : Color.FromRgb(180, 52, 52));
            if (!selection.IsSuccess) return;
            selectedDocumentFilePath = dialog.FileName;
            DocumentFileNameText.Text = Path.GetFileName(dialog.FileName);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            DocumentSelectFileButton.IsEnabled = true;
            DocumentAddButton.IsEnabled = true;
        }
    }

    private async void OnAddDocumentClick(object sender, RoutedEventArgs e)
    {
        if (busy || activeDocumentScreen is null) return;
        if (string.IsNullOrWhiteSpace(selectedDocumentFilePath))
        {
            DocumentMessageText.Text = "Önce eklenecek dosyayı seçin.";
            DocumentMessageText.Foreground = new SolidColorBrush(Color.FromRgb(180, 52, 52));
            return;
        }
        DocumentAddButton.IsEnabled = false;
        DocumentSelectFileButton.IsEnabled = false;
        latestDocumentBrowserMessage = string.Empty;
        latestDocumentBrowserMessageSeverity = null;
        DocumentMessageText.Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 108));
        DocumentMessageText.Text = "Doküman Medula'ya ekleniyor…";
        try
        {
            var result = await service.UploadPrescriptionDocumentAsync(
                activeDocumentScreen,
                DocumentTypeComboBox.SelectedValue?.ToString() ?? string.Empty,
                selectedDocumentFilePath,
                confirmationAccepted: false,
                cancellationToken: lifetime.Token);
            if (!string.IsNullOrWhiteSpace(result.ConfirmationMessage))
            {
                var accepted = ShowTurkishConfirmation(
                    result.ConfirmationMessage,
                    "Doküman Ekleme Onayı");
                if (accepted)
                {
                    result = await service.UploadPrescriptionDocumentAsync(
                        activeDocumentScreen,
                        DocumentTypeComboBox.SelectedValue?.ToString() ?? string.Empty,
                        selectedDocumentFilePath,
                        confirmationAccepted: true,
                        cancellationToken: lifetime.Token);
                }
            }
            ShowDocumentPopup(result);
            if (!result.IsOpen || result.Message.Contains("bulunamadı", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("okunamadı", StringComparison.OrdinalIgnoreCase))
                DocumentMessageText.Foreground = new SolidColorBrush(Color.FromRgb(180, 52, 52));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            DocumentAddButton.IsEnabled = true;
            DocumentSelectFileButton.IsEnabled = true;
        }
    }

    private async void OnCloseDocumentPopupClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        DocumentAddButton.IsEnabled = false;
        DocumentSelectFileButton.IsEnabled = false;
        DocumentMessageText.Text = "Reçeteye dönülüyor…";
        try
        {
            var result = await service.ClosePrescriptionDocumentAsync(BuildInput(), lifetime.Token);
            DocumentPopupLayer.Visibility = Visibility.Collapsed;
            activeDocumentScreen = null;
            selectedDocumentFilePath = string.Empty;
            latestDocumentBrowserMessage = string.Empty;
            latestDocumentBrowserMessageSeverity = null;
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            DocumentAddButton.IsEnabled = true;
            DocumentSelectFileButton.IsEnabled = true;
        }
    }

    private static bool LooksLikeMedulaDocumentError(string message) =>
        message.Contains("hata", StringComparison.CurrentCultureIgnoreCase)
        || message.Contains("başarısız", StringComparison.CurrentCultureIgnoreCase)
        || message.Contains("geçersiz", StringComparison.CurrentCultureIgnoreCase)
        || message.Contains("zorunlu", StringComparison.CurrentCultureIgnoreCase)
        || message.Contains("seçiniz", StringComparison.CurrentCultureIgnoreCase)
        || message.Contains("bulunamadı", StringComparison.CurrentCultureIgnoreCase)
        || message.Contains("okunamadı", StringComparison.CurrentCultureIgnoreCase)
        || message.Contains("izin verilenler arasında yer almamaktadır", StringComparison.CurrentCultureIgnoreCase);

    private void ApplyDoctorValues(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null) return;
        if (values.TryGetValue("f:t43", out var diplomaRegistration))
            RegistrationNumberBox.Text = diplomaRegistration;
        if (values.TryGetValue("f:t39", out var diplomaNo))
            PhysicianRegistrationBox.Text = diplomaNo;
        if (values.TryGetValue("f:t41", out var firstName))
            PhysicianFirstNameBox.Text = firstName;
        if (values.TryGetValue("f:t42", out var lastName))
            PhysicianLastNameBox.Text = lastName;
        if (values.TryGetValue("f:m7", out var certificate))
            CertificateTypeBox.SelectedValue = certificate;
        if (values.TryGetValue("medula:branch", out var branch)
            && !string.IsNullOrWhiteSpace(branch))
            BranchNameText.Text = pharmacyApp.Localization.UiText.TranslateSpecialty(branch);
    }

    private async void OnPatientLookupTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressPatientLookup || sender is not TextBox { Tag: string side } box) return;
        UpdatePatientLookupButtonVisibility();
        var isIdentityBox = ReferenceEquals(box, PatientIdentityBox)
            || ReferenceEquals(box, RecipientIdentityBox);
        if (isIdentityBox) ClearPatientPresentation(side);
        if (ReferenceEquals(box, PatientIdentityBox)) ApplyAutomaticRecipientSelf();
        SynchronizeSelfRecipient(side == "owner");
        if (isIdentityBox && currentSavedSummary is not null)
            _ = RefreshPrescriptionPartiesDebtAsync();
        var term = box.Text.Trim();
        if (ReferenceEquals(box, PatientIdentityBox)) ScheduleOwnerVerification(term);
        var isOwner = side == "owner";
        var list = isOwner ? OwnerSuggestionList : RecipientSuggestionList;
        var popup = isOwner ? OwnerSuggestionPopup : RecipientSuggestionPopup;
        ref var cancellation = ref (isOwner ? ref ownerSearchCancellation : ref recipientSearchCancellation);
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        if (!isOwner && term.Length == 0)
        {
            await ShowDefaultRecipientSuggestionsAsync(cancellation.Token);
            return;
        }
        if (term.Length < 3)
        {
            popup.IsOpen = false;
            list.ItemsSource = null;
            return;
        }

        try
        {
            var token = cancellation.Token;
            await Task.Delay(220, token);
            var suggestions = await directoryRepository.SearchPatientsAsync(term, token);
            if (isIdentityBox && term.Length == 11 && term.All(char.IsDigit))
            {
                var exact = suggestions.FirstOrDefault(patient =>
                    string.Equals(patient.TcIdentityNo, term, StringComparison.Ordinal));
                if (exact is not null)
                {
                    ApplyPatientSuggestion(side, exact);
                    popup.IsOpen = false;
                    list.ItemsSource = null;
                    return;
                }
            }
            list.ItemsSource = suggestions;
            popup.IsOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowDirectoryNotification("Hasta Araması", exception.Message, isUpdate: true);
        }
    }

    private async void OnRecipientLookupGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (suppressPatientLookup
            || RecipientIdentityBox.Text.Trim().Length > 0
            || RecipientPatientNameBox.Text.Trim().Length > 0)
            return;

        recipientSearchCancellation?.Cancel();
        recipientSearchCancellation?.Dispose();
        recipientSearchCancellation = new CancellationTokenSource();
        try
        {
            await ShowDefaultRecipientSuggestionsAsync(recipientSearchCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowDirectoryNotification("Hasta Araması", exception.Message, isUpdate: true);
        }
    }

    private async Task ShowDefaultRecipientSuggestionsAsync(CancellationToken cancellationToken)
    {
        var ownerTc = PatientIdentityBox.Text.Trim();
        var suggestions = (await directoryRepository.LoadRecipientSuggestionsAsync(ownerTc, cancellationToken))
            .ToList();
        if (suggestions.Count == 0
            && ownerTc.Length == 11
            && ownerTc.All(char.IsDigit))
        {
            suggestions.Add(new MedulaRecipientSuggestion(
                new PatientSuggestion(0, ownerTc, OwnerPatientNameBox.Text.Trim()),
                "Kendisi"));
        }

        RecipientSuggestionList.ItemsSource = suggestions;
        RecipientSuggestionPopup.IsOpen = suggestions.Count > 0;
    }

    private async void OnPatientSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { Tag: string side } list || list.SelectedItem is null) return;
        var patient = list.SelectedItem switch
        {
            PatientSuggestion suggestion => suggestion,
            MedulaRecipientSuggestion recipient => recipient.Patient,
            _ => null
        };
        if (patient is null) return;
        ApplyPatientSuggestion(side, patient);
        if (side == "recipient" && list.SelectedItem is MedulaRecipientSuggestion recipientSuggestion)
            RecipientTypeBox.SelectedValue = recipientSuggestion.RelationshipNote == "Kendisi" ? "K" : "Y";
        if (side == "owner") OwnerSuggestionPopup.IsOpen = false;
        else RecipientSuggestionPopup.IsOpen = false;
        list.SelectedItem = null;
        if (side == "owner") await VerifySelectedOwnerWithMedulaAsync(force: true);
    }

    private void ApplyPatientSuggestion(string side, PatientSuggestion patient)
    {
        suppressPatientLookup = true;
        try
        {
            if (side == "owner")
            {
                PatientIdentityBox.Text = patient.TcIdentityNo;
                OwnerPatientNameBox.Text = patient.DisplayName;
                SetPatientPhoto(OwnerPatientPhoto, OwnerPhotoContainer, OwnerPhotoColumn, OwnerPhotoSpacerColumn, 48, patient.Photo);
            }
            else
            {
                RecipientIdentityBox.Text = patient.TcIdentityNo;
                RecipientPatientNameBox.Text = patient.DisplayName;
                SetPatientPhoto(RecipientPatientPhoto, RecipientPhotoContainer, RecipientPhotoColumn, RecipientPhotoSpacerColumn, 48, patient.Photo);
            }
        }
        finally
        {
            suppressPatientLookup = false;
        }
        UpdatePatientLookupButtonVisibility();
        SynchronizeSelfRecipient(side == "owner");
    }

    private void UpdatePatientLookupButtonVisibility()
    {
        static bool IsValidTc(string value) =>
            value.Length == 11 && value.All(char.IsDigit);
        OwnerPatientLookupButton.Visibility = IsValidTc(PatientIdentityBox.Text.Trim())
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecipientPatientLookupButton.Visibility = IsValidTc(RecipientIdentityBox.Text.Trim())
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnPatientDirectoryLookupClick(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: string side }) return;
        var isOwner = side == "owner";
        var tc = (isOwner ? PatientIdentityBox.Text : RecipientIdentityBox.Text).Trim();
        if (tc.Length != 11 || tc.Any(character => !char.IsDigit(character))) return;

        ownerVerificationCancellation?.Cancel();
        SetBusy(true, isOwner
            ? "Hasta bilgileri Medula'dan doğrulanıyor…"
            : "Teslim alan bilgileri Medula'dan doğrulanıyor…");
        try
        {
            MedulaRemoteScreen screen;
            if (isOwner)
            {
                screen = await service.OpenPrescriptionSubScreenAsync(
                    BuildInput(),
                    MedulaPrescriptionCommand.OffLabelOld,
                    "Hasta Doğrulama",
                    lifetime.Token);
                if (screen.IsOpen)
                {
                    await ReconcileOwnerFromMedulaAsync(screen);
                    await ReconcilePendingPatientAsync(showVerifiedNotification: true);
                    lastVerifiedOwnerTc = tc;
                }
            }
            else
            {
                screen = await service.LookupPatientFromFreshPrescriptionEntryAsync(
                    tc,
                    BuildInput(),
                    lifetime.Token);
                if (screen.IsOpen)
                    await ReconcileRecipientFromMedulaAsync(screen, tc);
            }

            if (!screen.IsOpen)
                ShowDirectoryNotification("Hasta Doğrulaması", screen.Message, isUpdate: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowDirectoryNotification("Hasta Doğrulaması", exception.Message, isUpdate: true);
        }
        finally
        {
            if (isOwner)
            {
                try
                {
                    await service.ReturnToPrescriptionAsync(lifetime.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ShowDirectoryNotification("Reçete Ekranına Dönüş", exception.Message, isUpdate: true);
                }
            }
            SetBusy(false);
        }
    }

    private void ClearPatientPresentation(string side)
    {
        suppressPatientLookup = true;
        try
        {
            if (side == "owner")
            {
                OwnerPatientNameBox.Text = string.Empty;
                SetPatientPhoto(OwnerPatientPhoto, OwnerPhotoContainer, OwnerPhotoColumn, OwnerPhotoSpacerColumn, 48, null);
            }
            else
            {
                RecipientPatientNameBox.Text = string.Empty;
                SetPatientPhoto(RecipientPatientPhoto, RecipientPhotoContainer, RecipientPhotoColumn, RecipientPhotoSpacerColumn, 48, null);
            }
        }
        finally
        {
            suppressPatientLookup = false;
        }
    }

    private void OnRecipientTypeChanged(object sender, SelectionChangedEventArgs e) =>
        SynchronizeSelfRecipient(ownerChanged: true);

    private void SynchronizeSelfRecipient(bool ownerChanged)
    {
        if (synchronizingIdentity || SelectedValue(RecipientTypeBox, "0") != "K") return;
        synchronizingIdentity = true;
        suppressPatientLookup = true;
        try
        {
            var ownerTc = PatientIdentityBox.Text.Trim();
            var recipientTc = RecipientIdentityBox.Text.Trim();
            if (ownerTc.Length > 0 || (ownerChanged && recipientTc.Length == 0))
            {
                RecipientIdentityBox.Text = ownerTc;
                RecipientPatientNameBox.Text = OwnerPatientNameBox.Text;
                CopyPatientPhoto(OwnerPatientPhoto, RecipientPatientPhoto, RecipientPhotoContainer,
                    RecipientPhotoColumn, RecipientPhotoSpacerColumn, 48);
            }
            else if (recipientTc.Length > 0)
            {
                PatientIdentityBox.Text = recipientTc;
                OwnerPatientNameBox.Text = RecipientPatientNameBox.Text;
                CopyPatientPhoto(RecipientPatientPhoto, OwnerPatientPhoto, OwnerPhotoContainer,
                    OwnerPhotoColumn, OwnerPhotoSpacerColumn, 48);
            }
        }
        finally
        {
            suppressPatientLookup = false;
            synchronizingIdentity = false;
        }
        UpdatePatientLookupButtonVisibility();
    }

    private void ApplyAutomaticRecipientSelf()
    {
        if (!ApplicationSettingsStore.Current.AutomaticPrescriptionRecipientSelf
            || !HasDisplayValue(PatientIdentityBox.Text)
            || SelectedValue(RecipientTypeBox, "0") != "0"
            || HasDisplayValue(RecipientIdentityBox.Text)) return;
        RecipientTypeBox.SelectedValue = "K";
        SynchronizeSelfRecipient(ownerChanged: true);
    }

    private static void SetPatientPhoto(
        Image image,
        FrameworkElement container,
        ColumnDefinition photoColumn,
        ColumnDefinition spacerColumn,
        double width,
        byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            ApplyPatientPhotoSource(image, container, photoColumn, spacerColumn, width, null);
            return;
        }
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            ApplyPatientPhotoSource(image, container, photoColumn, spacerColumn, width, bitmap);
        }
        catch
        {
            ApplyPatientPhotoSource(image, container, photoColumn, spacerColumn, width, null);
        }
    }

    private static void CopyPatientPhoto(
        Image source,
        Image target,
        FrameworkElement targetContainer,
        ColumnDefinition targetColumn,
        ColumnDefinition targetSpacerColumn,
        double width) =>
        ApplyPatientPhotoSource(target, targetContainer, targetColumn, targetSpacerColumn, width, source.Source);

    private static void ApplyPatientPhotoSource(
        Image image,
        FrameworkElement container,
        ColumnDefinition photoColumn,
        ColumnDefinition spacerColumn,
        double width,
        ImageSource? source)
    {
        image.Source = null;
        container.Visibility = Visibility.Collapsed;
        photoColumn.MaxWidth = 0;
        spacerColumn.MaxWidth = 0;
        photoColumn.Width = new GridLength(0);
        spacerColumn.Width = new GridLength(0);
    }

    private static PendingPatientReconciliation? ReadPatientReconciliation(
        MedulaRemoteScreen screen,
        string fallbackTc)
    {
        static string Value(MedulaRemoteScreen source, string id) =>
            source.Fields.FirstOrDefault(field => field.Id == id)?.Value?.Trim() ?? string.Empty;
        var tc = Value(screen, "display:identity");
        var firstName = Value(screen, "display:first-name");
        var lastName = Value(screen, "display:last-name");
        var fullName = Value(screen, "display:name");
        if (firstName.Length == 0 && fullName.Length > 0)
        {
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            firstName = parts.FirstOrDefault() ?? string.Empty;
            lastName = string.Join(' ', parts.Skip(1));
        }
        tc = MedulaRemoteScreenRefiner.ResolveVerifiedIdentity(tc, fallbackTc);
        if (tc.Length != 11 || firstName.Length == 0) return null;

        return new PendingPatientReconciliation(
            tc,
            firstName,
            lastName,
            Value(screen, "display:gender"),
            Value(screen, "display:birth-date"));
    }

    private async Task ReconcileOwnerFromMedulaAsync(MedulaRemoteScreen screen)
    {
        var patient = ReadPatientReconciliation(screen, PatientIdentityBox.Text);
        if (patient is null) return;

        pendingPatientReconciliation = patient;
        suppressPatientLookup = true;
        try
        {
            PatientIdentityBox.Text = patient.TcIdentityNo;
            OwnerPatientNameBox.Text = string.Join(' ', new[] { patient.FirstName, patient.LastName }.Where(value => value.Length > 0));
        }
        finally
        {
            suppressPatientLookup = false;
        }
        UpdatePatientLookupButtonVisibility();
        var stored = (await directoryRepository.SearchPatientsAsync(patient.TcIdentityNo, lifetime.Token))
            .FirstOrDefault(item => item.TcIdentityNo == patient.TcIdentityNo);
        if (stored is not null) SetPatientPhoto(OwnerPatientPhoto, OwnerPhotoContainer, OwnerPhotoColumn, OwnerPhotoSpacerColumn, 48, stored.Photo);
        SynchronizeSelfRecipient(ownerChanged: true);
    }

    private async Task ReconcileRecipientFromMedulaAsync(MedulaRemoteScreen screen, string fallbackTc)
    {
        var patient = ReadPatientReconciliation(screen, fallbackTc);
        if (patient is null)
        {
            ShowDirectoryNotification(
                "Teslim Alan Doğrulaması",
                "Medula hasta ekranındaki kimlik veya ad bilgileri okunamadı.",
                isUpdate: true);
            return;
        }

        var change = await directoryRepository.ReconcilePatientAsync(
            patient.TcIdentityNo,
            patient.FirstName,
            patient.LastName,
            patient.Gender,
            patient.BirthDate,
            lifetime.Token);
        suppressPatientLookup = true;
        try
        {
            RecipientIdentityBox.Text = patient.TcIdentityNo;
            RecipientPatientNameBox.Text = string.Join(' ', new[] { patient.FirstName, patient.LastName }
                .Where(value => value.Length > 0));
        }
        finally
        {
            suppressPatientLookup = false;
        }
        UpdatePatientLookupButtonVisibility();
        var stored = (await directoryRepository.SearchPatientsAsync(patient.TcIdentityNo, lifetime.Token))
            .FirstOrDefault(item => item.TcIdentityNo == patient.TcIdentityNo);
        if (stored is not null)
            SetPatientPhoto(RecipientPatientPhoto, RecipientPhotoContainer, RecipientPhotoColumn,
                RecipientPhotoSpacerColumn, 48, stored.Photo);
        SynchronizeSelfRecipient(ownerChanged: false);
        if (change is not null)
            ShowDirectoryNotification(change);
        else
            ShowDirectoryNotification(new MedulaDirectoryChange(
                false,
                false,
                "Hasta Doğrulandı",
                "Medula ve yerel teslim alan bilgileri zaten aynı.",
                [new MedulaDirectoryField("T.C. Kimlik No", patient.TcIdentityNo)]));
    }

    private async Task ReconcilePendingPatientAsync(bool showVerifiedNotification = false)
    {
        var pending = pendingPatientReconciliation;
        if (pending is null
            || !string.Equals(pending.TcIdentityNo, PatientIdentityBox.Text.Trim(), StringComparison.Ordinal))
            return;

        try
        {
            var change = await directoryRepository.ReconcilePatientAsync(
                pending.TcIdentityNo,
                pending.FirstName,
                pending.LastName,
                pending.Gender,
                pending.BirthDate,
                lifetime.Token);
            pendingPatientReconciliation = null;
            if (change is not null)
                ShowDirectoryNotification(change);
            else if (showVerifiedNotification || ApplicationSettingsStore.Current.MedulaDebug)
                ShowDirectoryNotification(new MedulaDirectoryChange(
                    false,
                    false,
                    "Hasta Doğrulandı",
                    "Medula ve yerel hasta bilgileri zaten aynı.",
                    [
                        new MedulaDirectoryField("T.C. Kimlik No", pending.TcIdentityNo),
                        new MedulaDirectoryField("Hasta Adı Soyadı",
                            string.Join(' ', new[] { pending.FirstName, pending.LastName }
                                .Where(value => value.Length > 0))),
                        new MedulaDirectoryField("Durum", "Yerel kayıt güncel")
                    ]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowDirectoryNotification("Hasta Kaydı", exception.Message, isUpdate: true);
        }
    }

    private async Task ReconcileOpenedPrescriptionAsync(MedulaPrescriptionSnapshot snapshot)
    {
        if (snapshot.IsLocalArchive || snapshot.SourceListItem?.IsLiveOnMedula != true) return;
        var fields = snapshot.Page.FieldValues ?? new Dictionary<string, string>();
        string Field(string key) => fields.TryGetValue(key, out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;
        var reconciliationKey = snapshot.PrescriptionUid?.ToString("N")
            ?? string.Join('|',
                Field("medula:prescription-no"),
                Field("medula:e-prescription-no"),
                Field("f:t18"));
        if (reconciliationKey.Replace("|", string.Empty, StringComparison.Ordinal).Length == 0
            || !reconciledOpenedPrescriptionKeys.Add(reconciliationKey))
            return;
        var completed = false;
        var ownsBusyState = !busy;
        if (ownsBusyState) SetBusy(true, "Hasta ve doktor bilgileri Medula ile doğrulanıyor…");
        try
        {
            var patientTc = FirstNonBlank(Field("f:t18"), snapshot.SourceListItem.PatientTcIdentityNo);
            var patientFirstName = FirstNonBlank(
                Field("medula:patient-first-name"),
                snapshot.SourceListItem.PatientFirstName);
            var patientLastName = FirstNonBlank(
                Field("medula:patient-last-name"),
                snapshot.SourceListItem.PatientLastName);
            if (patientFirstName.Length == 0)
            {
                var splitName = SplitPatientDisplayName(snapshot.PatientDisplayName);
                patientFirstName = splitName.FirstName;
                patientLastName = FirstNonBlank(patientLastName, splitName.LastName);
            }

            var isElectronic = electronicPrescriptionContext
                || snapshot.SourceListItem.IsTypeI
                || Field("medula:e-prescription-no").Length > 0;
            var reconciledFromEndikasyonDisi = false;
            if (isElectronic && patientTc.Length == 11)
            {
                try
                {
                    var screen = await service.OpenPrescriptionSubScreenAsync(
                        BuildActionInput(MedulaPrescriptionCommand.OffLabelOld),
                        MedulaPrescriptionCommand.OffLabelOld,
                        "Hasta Doğrulama",
                        lifetime.Token);
                    if (screen.IsOpen)
                    {
                        await ReconcileOwnerFromMedulaAsync(screen);
                        reconciledFromEndikasyonDisi = pendingPatientReconciliation is not null;
                        await ReconcilePendingPatientAsync();
                        lastVerifiedOwnerTc = patientTc;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ShowDirectoryNotification("Hasta Doğrulaması", exception.Message, isUpdate: true);
                }
                finally
                {
                    try
                    {
                        await service.ReturnToPrescriptionAsync(lifetime.Token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        ShowDirectoryNotification("Reçete Ekranına Dönüş", exception.Message, isUpdate: true);
                    }
                }
            }

            if (!reconciledFromEndikasyonDisi)
            {
                try
                {
                    var patientChange = await directoryRepository.ReconcilePatientAsync(
                        patientTc,
                        patientFirstName,
                        patientLastName,
                        string.Empty,
                        string.Empty,
                        lifetime.Token);
                    if (patientChange is not null) ShowDirectoryNotification(patientChange);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ShowDirectoryNotification("Hasta Kaydı", exception.Message, isUpdate: true);
                }
            }

            await ReconcileDoctorAsync(prescriptionSaved: true);
            await RefreshOwnerLoyaltyAsync(patientTc, force: true);
            completed = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowDirectoryNotification("Hasta Doğrulaması", exception.Message, isUpdate: true);
        }
        finally
        {
            if (!completed) reconciledOpenedPrescriptionKeys.Remove(reconciliationKey);
            if (ownsBusyState) SetBusy(false);
        }
    }

    private async Task RefreshOwnerLoyaltyAsync(string tcIdentityNo, bool force = false)
    {
        tcIdentityNo = tcIdentityNo.Trim();
        if (tcIdentityNo.Length != 11 || tcIdentityNo.Any(character => !char.IsDigit(character))) return;
        if (!force && !loyaltyCheckedPatientTcs.Add(tcIdentityNo)) return;
        try
        {
            var evaluation = await loyalPatientRepository.EvaluatePatientOnPrescriptionOpenAsync(
                tcIdentityNo,
                CurrentLoyalPatientClassificationOptions(),
                lifetime.Token);
            loyaltyCheckedPatientTcs.Add(tcIdentityNo);
            if (evaluation?.IsLoyal != true
                || activeSnapshot is null
                || historicalSnapshot
                || IsPrescriptionOfflineSnapshot(activeSnapshot)
                || activeSnapshot.IsSavedOnMedula != true)
                return;

            var returnSnapshot = activeSnapshot;
            try
            {
                await loyalPatientHistorySyncService.SynchronizePatientAsync(
                    tcIdentityNo, lifetime.Token);
            }
            finally
            {
                await RestorePrescriptionAfterLoyalHistorySyncAsync(
                    returnSnapshot, tcIdentityNo);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!force) loyaltyCheckedPatientTcs.Remove(tcIdentityNo);
        }
    }

    private async Task RestorePrescriptionAfterLoyalHistorySyncAsync(
        MedulaPrescriptionSnapshot returnSnapshot,
        string patientTcIdentityNo)
    {
        var fields = returnSnapshot.Page.FieldValues
            ?? new Dictionary<string, string>();
        var prescriptionNumber = FirstNonBlank(
            prescriptionNumberContext,
            fields.GetValueOrDefault("medula:prescription-no"),
            returnSnapshot.SourceListItem?.PrescriptionNumber);
        if (!MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(prescriptionNumber))
            return;

        service.RequestForegroundPriority();
        var restored = await service.OpenPrescriptionAsync(
            new MedulaPrescriptionLocator(
                prescriptionNumber,
                patientTcIdentityNo,
                returnSnapshot.PatientDisplayName,
                FirstNonBlank(
                    electronicPrescriptionNumberContext,
                    fields.GetValueOrDefault("medula:e-prescription-no")),
                returnSnapshot.StoredFilterDate,
                TypeValue: returnSnapshot.SourceListItem?.TypeValue ?? string.Empty,
                Source: "loyal-history-sync"),
            lifetime.Token);
        if (restored is null) return;

        restored = restored with
        {
            SourceListItem = restored.SourceListItem ?? returnSnapshot.SourceListItem,
            PrescriptionUid = returnSnapshot.PrescriptionUid,
            VersionNumber = returnSnapshot.VersionNumber,
            IsLocalArchive = false,
            StoredFilterDate = returnSnapshot.StoredFilterDate,
            IsSavedOnMedula = true
        };
        service.RememberSavedPrescriptionContext(restored);
        ApplyPrescriptionSnapshot(restored);
    }

    private static LoyalPatientClassificationOptions CurrentLoyalPatientClassificationOptions()
    {
        var settings = ApplicationSettingsStore.Current;
        return new LoyalPatientClassificationOptions(
            settings.LoyalPatientMinimumMonthlyFrequency,
            settings.LoyalPatientMinimumDistinctVisitDays,
            settings.LoyalPatientMinimumObservationIntervals,
            settings.LoyalPatientFrequencyTolerancePercent / 100m,
            settings.LoyalPatientRecentVisitGraceIntervals);
    }

    private static (string FirstName, string LastName) SplitPatientDisplayName(string value)
    {
        var parts = value.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (parts[0], string.Empty),
            _ => (string.Join(' ', parts[..^1]), parts[^1])
        };
    }

    private void ScheduleOwnerVerification(string tc)
    {
        ownerVerificationCancellation?.Cancel();
        ownerVerificationCancellation?.Dispose();
        ownerVerificationCancellation = new CancellationTokenSource();
        if (pendingPatientReconciliation is not null
            && !string.Equals(pendingPatientReconciliation.TcIdentityNo, tc, StringComparison.Ordinal))
            pendingPatientReconciliation = null;
        if (tc.Length != 11 || tc.Any(character => !char.IsDigit(character)))
        {
            if (!string.Equals(lastVerifiedOwnerTc, tc, StringComparison.Ordinal))
                lastVerifiedOwnerTc = string.Empty;
            return;
        }
        _ = VerifyOwnerAfterDelayAsync(tc, ownerVerificationCancellation.Token);
    }

    private async Task VerifyOwnerAfterDelayAsync(string tc, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(650, cancellationToken);
            for (var attempt = 0; busy && attempt < 60; attempt++)
                await Task.Delay(500, cancellationToken);
            if (busy || PatientIdentityBox.Text.Trim() != tc || lastVerifiedOwnerTc == tc) return;
            await VerifySelectedOwnerWithMedulaAsync(expectedTc: tc);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task VerifySelectedOwnerWithMedulaAsync(bool force = false, string? expectedTc = null)
    {
        if (busy || PatientIdentityBox.Text.Trim().Length != 11) return;
        var tc = PatientIdentityBox.Text.Trim();
        if (expectedTc is not null && !string.Equals(expectedTc, tc, StringComparison.Ordinal)) return;
        if (!force && lastVerifiedOwnerTc == tc) return;
        SetBusy(true, "Hasta bilgileri Medula ile doğrulanıyor…");
        try
        {
            var screen = await service.OpenPrescriptionSubScreenAsync(
                BuildInput(),
                MedulaPrescriptionCommand.OffLabelOld,
                "Hasta Doğrulama",
                lifetime.Token);
            if (screen.IsOpen)
            {
                if (!string.Equals(PatientIdentityBox.Text.Trim(), tc, StringComparison.Ordinal)) return;
                await ReconcileOwnerFromMedulaAsync(screen);
                lastVerifiedOwnerTc = tc;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowDirectoryNotification("Hasta Doğrulaması", exception.Message, isUpdate: true);
        }
        finally
        {
            try
            {
                await service.ReturnToPrescriptionAsync(lifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ShowDirectoryNotification("Reçete Ekranına Dönüş", exception.Message, isUpdate: true);
            }
            SetBusy(false);
        }
    }

    private async Task ReconcileDoctorAsync(
        bool prescriptionSaved = false,
        bool doctorSearchSucceeded = false)
    {
        if (!prescriptionSaved && !doctorSearchSucceeded) return;
        try
        {
            var specialty = MedulaDoctorReconciliation.SpecialtyForPersistence(
                BranchNameText.Text,
                branchExplicitlySelected,
                prescriptionSaved);
            IReadOnlyList<string>? specialties = medulaDoctorSpecialties is { Count: > 0 }
                ? medulaDoctorSpecialties
                : specialty is null ? null : [specialty];
            var change = await directoryRepository.ReconcileDoctorAsync(
                RegistrationNumberBox.Text,
                PhysicianRegistrationBox.Text,
                PhysicianFirstNameBox.Text,
                PhysicianLastNameBox.Text,
                specialties,
                lifetime.Token);
            if (change is not null)
                ShowDirectoryNotification(change);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowDirectoryNotification("Hekim Kaydı", exception.Message, isUpdate: true);
        }
    }

    private void ShowDirectoryNotification(MedulaDirectoryChange change)
    {
        var body = change.WasUpdated
            ? "Medula bilgileri yerel kayıttan farklıydı. Güncel değerler uygulandı."
            : change.WasInserted
                ? "Medula bilgileriyle yeni bir yerel kayıt oluşturuldu."
                : change.Summary;
        var card = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(205, 217, 222)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0)
        };
        var content = new StackPanel();
        card.Child = content;

        var header = new Border
        {
            Background = new SolidColorBrush(change.WasUpdated
                ? Color.FromRgb(190, 48, 48)
                : Color.FromRgb(22, 128, 111)),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(14, 10, 10, 10)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titlePanel = new StackPanel();
        titlePanel.Children.Add(new TextBlock
        {
            Text = change.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Yerel kayıt değişikliği",
            Foreground = new SolidColorBrush(Color.FromRgb(233, 255, 250)),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        });
        headerGrid.Children.Add(titlePanel);
        var closeButton = new Button
        {
            Content = "×",
            Width = 28,
            Height = 28,
            Margin = new Thickness(12, -3, -4, -3),
            FontSize = 18,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "Bildirimi kapat"
        };
        closeButton.Click += (_, _) => DirectoryNotificationStack.Children.Remove(card);
        Grid.SetColumn(closeButton, 1);
        headerGrid.Children.Add(closeButton);
        header.Child = headerGrid;
        content.Children.Add(header);

        var detailPanel = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
        detailPanel.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 108)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 12
        });
        foreach (var field in change.Fields ?? [])
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock
            {
                Text = field.Label,
                Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 108)),
                FontWeight = FontWeights.SemiBold
            });
            var value = new TextBlock
            {
                Text = field.Value,
                Foreground = new SolidColorBrush(Color.FromRgb(38, 58, 71)),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            detailPanel.Children.Add(row);
        }
        content.Children.Add(detailPanel);

        var footer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(228, 235, 238)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9, 12, 9)
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var dismissButton = new Button
        {
            Content = "Kapat",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(238, 243, 245)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(214, 224, 228)),
            Foreground = new SolidColorBrush(Color.FromRgb(52, 72, 82))
        };
        dismissButton.Click += (_, _) => DirectoryNotificationStack.Children.Remove(card);
        actions.Children.Add(dismissButton);
        if (change.Undo is not null)
        {
            var undoButton = new Button
            {
                Content = "Geri Al",
                Padding = new Thickness(16, 6, 16, 6),
                Background = new SolidColorBrush(Color.FromRgb(255, 240, 240)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 181, 181)),
                Foreground = new SolidColorBrush(Color.FromRgb(165, 46, 46))
            };
            undoButton.Click += async (_, _) =>
                await UndoDirectoryChangeAsync(change, undoButton, card);
            actions.Children.Add(undoButton);
        }
        footer.Child = actions;
        content.Children.Add(footer);
        DirectoryNotificationStack.Children.Add(card);
        _ = FadeAndRemoveAsync(card, () => DirectoryNotificationStack.Children.Remove(card));
    }

    private void ShowDirectoryNotification(string title, string body, bool isUpdate)
    {
        ShowDirectoryNotification(new MedulaDirectoryChange(
            false,
            isUpdate,
            title,
            body,
            [new MedulaDirectoryField("Detay", body)]));
    }

    private async Task UndoDirectoryChangeAsync(
        MedulaDirectoryChange change,
        Button undoButton,
        Border card)
    {
        var undo = change.Undo;
        if (undo is null) return;
        undoButton.IsEnabled = false;
        try
        {
            await directoryRepository.UndoAsync(undo, lifetime.Token);
            DirectoryNotificationStack.Children.Remove(card);
            ShowDirectoryNotification(new MedulaDirectoryChange(
                false,
                false,
                "Değişiklik Geri Alındı",
                "Yerel veritabanı önceki durumuna döndürüldü.",
                [new MedulaDirectoryField("Durum", "Önceki kayıt geri yüklendi.")]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowDirectoryNotification("Geri Alma Başarısız", exception.Message, isUpdate: true);
        }
    }

    private async Task OpenPrescriptionEntryAsync()
    {
        if (busy) return;

        SetBusy(true, "Medula Reçete Giriş ekranı açılıyor…");
        try
        {
            MedulaPrescriptionPageResult? result = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                result = await service.OpenPrescriptionEntryAsync(lifetime.Token);
                if (result.IsPrescriptionEntryPage) break;
                if (attempt < 2) await Task.Delay(250, lifetime.Token);
            }
            result ??= new MedulaPrescriptionPageResult(
                false, false, "Medula", MedulaConnectionService.LoginUrl,
                "Medula Reçete Giriş ekranını açmadı.", string.Empty);
            prescriptionScreenReady = result.IsPrescriptionEntryPage;
            if (prescriptionScreenReady)
            {
                activeSnapshot = null;
                electronicPrescriptionContext = false;
                prescriptionNumberContext = string.Empty;
                electronicPrescriptionNumberContext = string.Empty;
                EPrescriptionNumberBox.Text = string.Empty;
                EPrescriptionNumberPanel.Visibility = Visibility.Collapsed;
                ViewElectronicPrescriptionButton.Visibility = Visibility.Collapsed;
                PrescriptionSaveButton.Content = "Kaydet";
            }
            ShowResult(result);
            UpdateConnectionDisplay(
                prescriptionScreenReady ? MedulaConnectionState.Connected : MedulaConnectionState.Failed,
                prescriptionScreenReady ? "Reçete Giriş ekranı hazır." : "Medula Reçete Giriş ekranını açmadı.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyPrescriptionSnapshot(MedulaPrescriptionSnapshot snapshot)
    {
        snapshot = MedulaPrescriptionHeaderParser.Normalize(snapshot);
        activeSnapshot = snapshot;
        availabilityMode = historicalSnapshot || IsPrescriptionOfflineSnapshot(snapshot)
            ? PrescriptionAvailabilityMode.PrescriptionOffline
            : service.State is MedulaConnectionState.Disconnected
                or MedulaConnectionState.Failed
                or MedulaConnectionState.TemporarilyOffline
                ? PrescriptionAvailabilityMode.NetworkOffline
                : PrescriptionAvailabilityMode.Live;
        suppressPatientLookup = true;
        suppressDateValidation = true;
        try
        {
            var isPrescriptionOffline = availabilityMode == PrescriptionAvailabilityMode.PrescriptionOffline;
            var unknown = isPrescriptionOffline ? "?" : string.Empty;
            var fields = snapshot.Page.FieldValues ?? new Dictionary<string, string>();
            string Field(string key) => fields.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
            string DisplayField(string key) => FirstNonBlank(Field(key), unknown);
            void Select(ComboBox box, string key)
            {
                var value = Field(key);
                if (value.Length > 0) box.SelectedValue = value;
            }

            PatientIdentityBox.Text = DisplayField("f:t18");
            var recipientIdentity = Field("f:text37");
            RecipientIdentityBox.Text = recipientIdentity == "0"
                ? string.Empty
                : FirstNonBlank(recipientIdentity, unknown);
            OwnerPatientNameBox.Text = FirstNonBlank(snapshot.PatientDisplayName, unknown);
            RecipientPatientNameBox.Text = unknown;
            Select(RecipientTypeBox, "f:menu4");
            Select(ProvisionTypeBox, "f:menu2");
            ProtocolNumberBox.Text = DisplayField("f:t24");
            FacilityCodeBox.Text = DisplayField("f:t33");
            PrescriptionDateBox.SelectedDate = ParseSnapshotDate(Field("f:t29"));
            DispensingDateBox.SelectedDate = ParseSnapshotDate(Field("f:t31"));
            Select(PrescriptionTypeBox, "f:m4");
            Select(PrescriptionSubTypeBox, "f:m8");
            RegistrationNumberBox.Text = DisplayField("f:t43");
            Select(PhysicianTypeBox, "f:m6");
            PhysicianRegistrationBox.Text = DisplayField("f:t39");
            PhysicianFirstNameBox.Text = DisplayField("f:t41");
            PhysicianLastNameBox.Text = DisplayField("f:t42");
            Select(CertificateTypeBox, "f:m7");
            HasQrCodeBox.IsChecked = bool.TryParse(Field("f:checkbox1"), out var hasQr) && hasQr;
            SequentialDistributionBox.IsChecked = bool.TryParse(Field("f:checkbox2"), out var sequential) && sequential;
            RepeatEligibilityBox.IsChecked = bool.TryParse(Field("f:checkbox4"), out var repeat) && repeat;

            CoverageValueText.Text = FirstNonBlank(Field("medula:coverage"), isPrescriptionOffline ? "?" : "—");
            PatientTypeValueText.Text = FirstNonBlank(Field("medula:patient-type"), isPrescriptionOffline ? "?" : "—");
            BranchNameText.Text = pharmacyApp.Localization.UiText.TranslateSpecialty(FirstNonBlank(Field("medula:branch"), isPrescriptionOffline ? "?" : "Pratisyen Hekim"));
            var invoiceGroup = FirstNonBlank(
                MedulaPrescriptionListTypes.DisplayGroupName(Field("medula:invoice-type")),
                MedulaPrescriptionListTypes.DisplayGroupName(snapshot.SourceListItem?.TypeValue),
                isPrescriptionOffline ? "?" : "—");
            var prescriptionNumber = Field("medula:prescription-no");
            if (!MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(prescriptionNumber))
                prescriptionNumber = snapshot.SourceListItem?.PrescriptionNumber?.Trim() ?? string.Empty;
            if (MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(prescriptionNumber))
                prescriptionNumberContext = prescriptionNumber;
            PrescriptionSummaryText.Text =
                $"Reçete No: {FirstNonBlank(prescriptionNumber, isPrescriptionOffline ? "?" : "—")}  •  " +
                $"Karekod Durumu: {FirstNonBlank(Field("medula:qr-status"), isPrescriptionOffline ? "?" : "—")}  •  " +
                $"Fatura Grubu: {invoiceGroup}";
            var electronicPrescriptionNumber = Field("medula:e-prescription-no");
            if (electronicPrescriptionNumber.Length > 0)
                electronicPrescriptionNumberContext = electronicPrescriptionNumber;
            electronicPrescriptionContext = electronicPrescriptionContext
                || electronicPrescriptionNumber.Length > 0
                || snapshot.SourceListItem?.IsTypeI == true;
            electronicPrescriptionNumber = FirstNonBlank(
                electronicPrescriptionNumber,
                electronicPrescriptionNumberContext);
            var isElectronicPrescription = electronicPrescriptionContext;
            EPrescriptionNumberBox.Text = electronicPrescriptionNumber;
            PrescriptionSaveButton.Content = isElectronicPrescription ? "E-Reçete Kaydet" : "Kaydet";
            EPrescriptionNumberPanel.Visibility = isElectronicPrescription
                ? Visibility.Visible
                : Visibility.Collapsed;
            ViewElectronicPrescriptionButton.Visibility = isElectronicPrescription
                && electronicPrescriptionNumber.Length > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            Drugs.Clear();
            foreach (var drug in snapshot.Drugs) Drugs.Add(drug);
            _ = PopulateDrugShelfQuantitiesAsync(Drugs.ToArray());
            if (isPrescriptionOffline && Drugs.Count == 0)
                Drugs.Add(new MedulaDrugEntry { Barcode = "?", Name = "?", Quantity = "?", Period = "?", DoseCount = "?", DoseAmount = "?", Amount = "?", Difference = "?", ShelfQuantity = "?", Report = "?", AvailableDate = "?", DispensingMessage = "?" });
            Diagnoses.Clear();
            foreach (var diagnosis in snapshot.Diagnoses) Diagnoses.Add(diagnosis);
            if (isPrescriptionOffline)
            {
                if (Diagnoses.Count == 0) Diagnoses.Add(new MedulaDiagnosisEntry { Code = "?", Description = "?" });
            }
            else
            {
                diagnosisDraftRow = new MedulaDiagnosisEntry();
                Diagnoses.Add(diagnosisDraftRow);
            }

            prescriptionScreenReady = !isPrescriptionOffline && snapshot.Page.IsPrescriptionEntryPage;
            ShowResult(snapshot.Page);
            if (historicalSnapshot)
                UpdateConnectionDisplay(MedulaConnectionState.Failed, "Reçete geçmişi · Salt okunur sürüm");
            else if (isPrescriptionOffline)
                UpdateConnectionDisplay(MedulaConnectionState.Failed, "Reçete çevrimdışı · Medula'da aktif değil");
            else if (availabilityMode == PrescriptionAvailabilityMode.NetworkOffline)
                UpdateConnectionDisplay(MedulaConnectionState.TemporarilyOffline, "Geçici çevrimdışı · Reçete verileri yerel kayıttan gösteriliyor");
            else
                UpdateConnectionDisplay(
                    prescriptionScreenReady ? MedulaConnectionState.Connected : MedulaConnectionState.Failed,
                    prescriptionScreenReady ? "Kayıtlı reçete açıldı." : "Kayıtlı reçete okunamadı.");
            ApplyAvailabilityPolicy();
            Dispatcher.BeginInvoke(
                () => PrescriptionScrollViewer.ScrollToTop(),
                DispatcherPriority.Loaded);
        }
        finally
        {
            suppressDateValidation = false;
            suppressPatientLookup = false;
            ApplyAutomaticRecipientSelf();
            UpdatePatientLookupButtonVisibility();
            ApplyPrescriptionDetailVisibility();
            _ = RefreshPrescriptionPartiesDebtAsync();
        }
    }

    private async Task RefreshPrescriptionPartiesDebtAsync()
    {
        var ownerTc = PatientIdentityBox.Text.Trim();
        var recipientTc = RecipientIdentityBox.Text.Trim();
        var key = $"{ownerTc}|{recipientTc}";
        if (key == loadedPrescriptionPartiesDebtKey) return;
        loadedPrescriptionPartiesDebtKey = key;
        try
        {
            prescriptionPartyDebtSummary = await directoryRepository.ReadPrescriptionPartyDebtSummaryAsync(
                ownerTc, recipientTc, lifetime.Token);
            prescriptionPartiesDebtTotal = prescriptionPartyDebtSummary.TotalDebt;
            SelectAllAvailableItsDebts();
            ApplyItsPatientAndPrescriptionSummary();
            if (currentSavedSummary is not null) ShowSavedPrescriptionSummary(currentSavedSummary);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Prescription navigation remains usable when the local directory is unavailable.
        }
    }

    private void SelectAllAvailableItsDebts()
    {
        suppressItsDebtSelectionChanges = true;
        try
        {
            ItsQrOwnerDebtCheckBox.IsChecked = prescriptionPartyDebtSummary.OwnerDebt > 0m;
            ItsQrRecipientDebtCheckBox.IsChecked = prescriptionPartyDebtSummary.RecipientDebt is > 0m;
            ItsQrFamilyDebtCheckBox.IsChecked =
                prescriptionPartyDebtSummary.HasFamilyGroup
                && prescriptionPartyDebtSummary.FamilyGroupDebt > 0m;
            ItsQrTotalDebtCheckBox.IsChecked =
                ItsQrOwnerDebtCheckBox.IsChecked == true
                || ItsQrRecipientDebtCheckBox.IsChecked == true
                || ItsQrFamilyDebtCheckBox.IsChecked == true;
        }
        finally
        {
            suppressItsDebtSelectionChanges = false;
        }
    }

    private static DateTime? ParseSnapshotDate(string value)
    {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        return DateTime.TryParseExact(value, ["dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy"],
            culture, DateTimeStyles.None, out var date) ? date : null;
    }

    private MedulaPrescriptionInput BuildInput() => new(
        InputValue(PatientIdentityBox.Text),
        InputValue(RecipientIdentityBox.Text),
        SelectedValue(RecipientTypeBox, "0"),
        SelectedValue(ProvisionTypeBox, "1"),
        InputValue(ProtocolNumberBox.Text),
        InputValue(FacilityCodeBox.Text),
        NormalizeMedulaDate(PrescriptionDateBox) ?? PrescriptionDateBox.Text.Trim(),
        NormalizeMedulaDate(DispensingDateBox) ?? DispensingDateBox.Text.Trim(),
        SelectedValue(PrescriptionTypeBox, "1"),
        SelectedValue(PrescriptionSubTypeBox, "1"),
        InputValue(RegistrationNumberBox.Text),
        SelectedValue(PhysicianTypeBox, "1"),
        InputValue(PhysicianRegistrationBox.Text),
        InputValue(PhysicianFirstNameBox.Text),
        InputValue(PhysicianLastNameBox.Text),
        SelectedValue(CertificateTypeBox, "0"),
        HasQrCodeBox.IsChecked == true,
        SequentialDistributionBox.IsChecked == true,
        DiagnosisSearchBox.Text.Trim(),
        RepeatEligibilityBox.IsChecked == true,
        Drugs.Where(drug => drug.Barcode != "?" && drug.Name != "?").ToArray(),
        Diagnoses.Where(diagnosis =>
            !ReferenceEquals(diagnosis, diagnosisDraftRow)
            && diagnosis.Code != "?" && diagnosis.Description != "?"
            && (!string.IsNullOrWhiteSpace(diagnosis.Code)
                || !string.IsNullOrWhiteSpace(diagnosis.Description))).ToArray(),
        activeSnapshot?.SourceListItem,
        EPrescriptionNumberBox.Text.Trim(),
        activeSnapshot?.IsSavedOnMedula == true);

    private MedulaPrescriptionInput BuildActionInput(MedulaPrescriptionCommand command)
    {
        var input = BuildInput();
        if (availabilityMode == PrescriptionAvailabilityMode.PrescriptionOffline
            && PrescriptionOfflineLookupActions.Contains(command)
            && prescriptionOfflineLookupApproved)
        {
            prescriptionOfflineLookupApproved = false;
            return input with
            {
                SourceListItem = null,
                ElectronicPrescriptionNumber = string.Empty,
                IsSavedOnMedula = false
            };
        }
        return input;
    }

    private static bool TryGetPrescriptionCommand(object? value, out MedulaPrescriptionCommand command)
    {
        if (value is MedulaPrescriptionCommand typed)
        {
            command = typed;
            return true;
        }
        return Enum.TryParse(value?.ToString(), ignoreCase: true, out command);
    }

    private static bool TryGetDrugRowCommand(object? value, out MedulaDrugRowCommand command)
    {
        if (value is MedulaDrugRowCommand typed)
        {
            command = typed;
            return true;
        }
        return Enum.TryParse(value?.ToString(), ignoreCase: true, out command);
    }

    private static string InputValue(string? value) => value?.Trim() == "?" ? string.Empty : value?.Trim() ?? string.Empty;

    private static string CurrentUserId() =>
        (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty;

    private static string SelectedValue(ComboBox box, string fallback) =>
        box.SelectedValue?.ToString() ?? fallback;

    private static string SelectedText(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? string.Empty;

    private static string? NormalizeMedulaDate(DatePicker picker) =>
        MedulaDateNormalizer.Normalize(picker.Text, picker.SelectedDate);

    private MedulaPrescriptionPersistenceRecord BuildPersistenceRecord(
        MedulaPrescriptionInput input,
        MedulaPrescriptionPageResult result)
    {
        var payments = result.SavedSummary?.PaymentFields ?? [];
        var qrStatus = FieldValue(result, "medula:qr-status");
        var coverage = FirstNonBlank(
            FieldValue(result, "medula:coverage"),
            CoverageValueText.Text);
        var patientType = FirstNonBlank(
            FieldValue(result, "medula:patient-type"),
            PatientTypeValueText.Text);
        var registeredContent = DrugMatrix(input.Drugs);
        return new MedulaPrescriptionPersistenceRecord(
            FieldValue(result, "medula:e-prescription-no"),
            FieldValue(result, "medula:prescription-no"),
            qrStatus,
            SelectedText(PrescriptionTypeBox),
            SelectedText(PrescriptionSubTypeBox),
            MedulaPrescriptionListTypes.ResolveInvoiceType(
                FieldValue(result, "medula:invoice-type"),
                activeSnapshot?.SourceListItem?.TypeValue),
            SelectedText(ProvisionTypeBox),
            input.ProtocolNumber,
            input.FacilityCode,
            ParseDate(input.PrescriptionDate),
            ParseDate(input.DispensingDate),
            input.HasQrCode,
            input.IsSequentialDistribution,
            input.PatientIdentityNumber,
            patientType,
            FieldValue(result, "medula:insurance-type"),
            coverage,
            input.RecipientIdentityNumber,
            SelectedText(RecipientTypeBox),
            input.RegistrationNumber,
            input.PhysicianFirstName,
            input.PhysicianLastName,
            BranchNameText.Text.Trim(),
            SelectedText(CertificateTypeBox),
            registeredContent,
            (string[,])registeredContent.Clone(),
            DiagnosisMatrix(input.Diagnoses),
            FieldValue(result, "medula:prescription-note"),
            PaymentValue(payments, "Reç.Kat.Payı", "Elden"),
            PaymentValue(payments, "Reç.Kat.Payı", "Maaştan"),
            PaymentValue(payments, "Mua.Kat.Payı", "Elden"),
            PaymentValue(payments, "Mua.Kat.Payı", "Maaştan"),
            PaymentValue(payments, "İl.Kat.Payı"),
            PaymentValue(payments, "Fiyat Farkı"),
            PaymentValue(payments, "Ödenecek Tutar"),
            PaymentValue(payments, "Hastanın eczaneye ödemesi gereken"),
            PaymentValue(payments, "Toplam Tutar"),
            ParseItsQrCodes(activeSnapshot?.Page.FieldValues?
                .GetValueOrDefault("medula:its-qr-codes")),
            MedulaPrescriptionListTypes.IsItsFinalized(qrStatus),
            (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty,
            MedulaPageNumber: activeSnapshot?.SourceListItem?.PageNumber ?? 0,
            MedulaRowIndex: activeSnapshot?.SourceListItem?.RowIndex ?? 0);
    }

    private static string FieldValue(MedulaPrescriptionPageResult result, string key) =>
        result.FieldValues?.TryGetValue(key, out var value) == true ? value.Trim() : string.Empty;

    private MedulaPrescriptionPageResult PreservePrescriptionIdentity(
        MedulaPrescriptionPageResult result)
    {
        var fields = result.FieldValues is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(result.FieldValues, StringComparer.OrdinalIgnoreCase);
        var resultPrescriptionNumber = FieldValue(result, "medula:prescription-no");
        if (MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(resultPrescriptionNumber))
            prescriptionNumberContext = resultPrescriptionNumber;
        else
        {
            var preservedPrescriptionNumber = FirstValidPrescriptionNumber(
                activeSnapshot?.Page.FieldValues?.GetValueOrDefault("medula:prescription-no"),
                activeSnapshot?.SourceListItem?.PrescriptionNumber,
                prescriptionNumberContext);
            if (MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(preservedPrescriptionNumber))
                fields["medula:prescription-no"] = preservedPrescriptionNumber;
        }

        var resultNumber = FieldValue(result, "medula:e-prescription-no");
        if (resultNumber.Length > 0)
        {
            electronicPrescriptionContext = true;
            electronicPrescriptionNumberContext = resultNumber;
            fields["medula:e-prescription-no"] = resultNumber;
            return result with { FieldValues = fields };
        }

        var electronicPrescriptionNumber = FirstNonBlank(
            EPrescriptionNumberBox.Text,
            electronicPrescriptionNumberContext);
        if (electronicPrescriptionContext && electronicPrescriptionNumber.Length > 0)
            fields["medula:e-prescription-no"] = electronicPrescriptionNumber;
        return result with { FieldValues = fields };
    }

    private MedulaPrescriptionSnapshot PreservePrescriptionIdentity(
        MedulaPrescriptionSnapshot snapshot) =>
        snapshot with { Page = PreservePrescriptionIdentity(snapshot.Page) };

    private static MedulaPrescriptionSnapshot PromoteToSavedPrescriptionContext(
        MedulaPrescriptionSnapshot snapshot,
        MedulaPrescriptionInput input)
    {
        var fields = snapshot.Page.FieldValues ?? new Dictionary<string, string>();
        string Field(string key) => fields.TryGetValue(key, out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;
        var prescriptionNumber = Field("medula:prescription-no");
        if (prescriptionNumber.Length == 0 || prescriptionNumber == "0") return snapshot;

        var existing = snapshot.SourceListItem;
        var electronicPrescription = Field("medula:e-prescription-no");
        var firstName = FirstNonBlank(Field("medula:patient-first-name"), existing?.PatientFirstName);
        var lastName = FirstNonBlank(Field("medula:patient-last-name"), existing?.PatientLastName);
        var prescriptionDate = FirstNonBlank(Field("f:t29"), existing?.PrescriptionDate);
        var source = existing is null
            ? new MedulaPrescriptionListItem(
                prescriptionNumber,
                DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                prescriptionDate,
                firstName,
                lastName,
                Field("medula:coverage"),
                Field("medula:qr-status"),
                electronicPrescription.Length > 0 ? "I" : "K",
                input.HasQrCode ? "Evet" : "Hayır",
                string.Empty,
                string.Empty,
                string.Empty,
                DateTime.Today.ToString("yyyyMM", CultureInfo.InvariantCulture),
                "prescription-query",
                1,
                0)
            : existing with
            {
                PrescriptionNumber = prescriptionNumber,
                PrescriptionDate = prescriptionDate,
                PatientFirstName = firstName,
                PatientLastName = lastName,
                Coverage = FirstNonBlank(Field("medula:coverage"), existing.Coverage),
                QrCodeStatus = FirstNonBlank(Field("medula:qr-status"), existing.QrCodeStatus),
                StatusCode = electronicPrescription.Length > 0 ? "I" : existing.StatusCode
            };
        source = source with
        {
            PatientTcIdentityNo = input.PatientIdentityNumber.Trim(),
            IsLiveOnMedula = true,
            IsDeleted = false
        };
        return snapshot with { SourceListItem = source, IsSavedOnMedula = true };
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FirstValidPrescriptionNumber(params string?[] values) =>
        values.FirstOrDefault(MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber)?.Trim()
        ?? string.Empty;

    private static DateOnly? ParseDate(string value)
    {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        return DateOnly.TryParseExact(
            value.Trim(),
            ["dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yy", "d/M/yy"],
            culture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    private static string[,] DrugMatrix(IReadOnlyList<MedulaDrugEntry> drugs)
    {
        var result = new string[drugs.Count, 15];
        for (var row = 0; row < drugs.Count; row++)
        {
            var drug = drugs[row];
            result[row, 0] = drug.Barcode;
            result[row, 1] = drug.Name;
            result[row, 2] = drug.Quantity;
            result[row, 3] = drug.Period;
            result[row, 4] = drug.PeriodUnit;
            result[row, 5] = drug.DoseCount;
            result[row, 6] = drug.DoseAmount;
            result[row, 7] = drug.Amount;
            result[row, 8] = drug.Difference;
            result[row, 9] = drug.ShelfQuantity;
            result[row, 10] = drug.Report;
            result[row, 11] = drug.AvailableDate;
            result[row, 12] = drug.DispensingMessage;
            result[row, 13] = drug.Selected.ToString(CultureInfo.InvariantCulture);
            result[row, 14] = drug.DiagnosisNote;
        }
        return result;
    }

    private static string[,] DiagnosisMatrix(IReadOnlyList<MedulaDiagnosisEntry> diagnoses)
    {
        var result = new string[diagnoses.Count, 2];
        for (var row = 0; row < diagnoses.Count; row++)
        {
            result[row, 0] = diagnoses[row].Code;
            result[row, 1] = diagnoses[row].Description;
        }
        return result;
    }

    private static decimal? PaymentValue(
        IReadOnlyList<MedulaPrescriptionPaymentField> payments,
        params string[] labelParts)
    {
        var field = payments.FirstOrDefault(item => labelParts.All(part =>
            item.Label.Contains(part, StringComparison.CurrentCultureIgnoreCase)));
        if (field is null) return null;
        var value = field.Value.Replace("₺", string.Empty, StringComparison.Ordinal).Trim();
        return decimal.TryParse(
            value,
            NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
            CultureInfo.GetCultureInfo("tr-TR"),
            out var parsed)
            ? parsed
            : null;
    }

    private void ShowResult(MedulaPrescriptionPageResult result)
    {
        result = ApplyImmediatePrescriptionHeader(result);
        var state = result.SavedSummary is null ? "draft" : "saved";
        ChangeBrowserMessageSurface($"prescription:{state}:{result.Url}");
        if (result.WarningText is not null) SetBrowserWarning(result.WarningText);
        if (result.SuccessText is not null) SetBrowserConfirmation(result.SuccessText);
        if (result.NoticeText is not null) SetBrowserNotice(result.NoticeText);
        if (result.RefreshedDrugs is not null)
        {
            Drugs.Clear();
            foreach (var drug in result.RefreshedDrugs) Drugs.Add(drug);
            RefreshDrugErrorIndicators();
        }
        ShowSavedPrescriptionSummary(result.SavedSummary);
        ApplyPrescriptionDetailVisibility();
        ResponseMessageText.Text = result.Message;
        ResponseUrlText.Text = result.Url;
        ResponsePageText.Text = result.PageText;
        ResponseBanner.Background = result.IsSuccess
            ? new SolidColorBrush(Color.FromRgb(232, 245, 242))
            : new SolidColorBrush(Color.FromRgb(252, 235, 235));
        ResponseMessageText.Foreground = result.IsSuccess
            ? new SolidColorBrush(Color.FromRgb(49, 84, 79))
            : new SolidColorBrush(Color.FromRgb(143, 48, 48));
        ReturnToEntryButton.Content = result.IsPrescriptionEntryPage
            ? "Reçete Ekranını Yenile"
            : "Reçete Ekranına Dön";
    }

    private MedulaPrescriptionPageResult ApplyImmediatePrescriptionHeader(
        MedulaPrescriptionPageResult result)
    {
        if (!result.IsPrescriptionEntryPage) return result;

        var source = activeSnapshot?.SourceListItem;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var storedFields = MedulaPrescriptionHeaderParser.Normalize(
            activeSnapshot?.Page.FieldValues,
            activeSnapshot?.Page.PageText,
            source);
        foreach (var pair in storedFields) merged[pair.Key] = pair.Value;

        var freshFields = MedulaPrescriptionHeaderParser.Normalize(
            result.FieldValues,
            result.PageText,
            source);
        foreach (var pair in freshFields) merged[pair.Key] = pair.Value;

        var normalized = MedulaPrescriptionHeaderParser.Normalize(
            merged,
            $"{activeSnapshot?.Page.PageText}\n{result.PageText}",
            source);
        string Field(string key) => normalized.TryGetValue(key, out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;

        var prescriptionNumber = Field("medula:prescription-no");
        if (MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(prescriptionNumber))
            prescriptionNumberContext = prescriptionNumber;
        else
            prescriptionNumber = prescriptionNumberContext;

        var invoiceGroup = FirstNonBlank(
            MedulaPrescriptionListTypes.ResolveInvoiceType(
                Field("medula:invoice-type"), source?.TypeValue),
            MedulaPrescriptionListTypes.DisplayGroupName(source?.TypeValue),
            "—");
        CoverageValueText.Text = FirstNonBlank(Field("medula:coverage"), "—");
        PatientTypeValueText.Text = FirstNonBlank(Field("medula:patient-type"), "—");
        PrescriptionSummaryText.Text =
            $"Reçete No: {FirstNonBlank(prescriptionNumber, "—")}  •  " +
            $"Karekod Durumu: {FirstNonBlank(Field("medula:qr-status"), "—")}  •  " +
            $"Fatura Grubu: {invoiceGroup}";

        var electronicPrescriptionNumber = Field("medula:e-prescription-no");
        if (electronicPrescriptionNumber.Length > 0)
            electronicPrescriptionNumberContext = electronicPrescriptionNumber;

        var savedSummary = result.SavedSummary ?? activeSnapshot?.Page.SavedSummary;
        result = result with { FieldValues = normalized, SavedSummary = savedSummary };
        if (activeSnapshot is not null)
            activeSnapshot = activeSnapshot with
            {
                Page = activeSnapshot.Page with
                {
                    FieldValues = normalized,
                    SavedSummary = savedSummary,
                    PageText = string.IsNullOrWhiteSpace(result.PageText)
                        ? activeSnapshot.Page.PageText
                        : result.PageText
                }
            };
        return result;
    }

    private string CurrentPrescriptionMessageSurface()
    {
        var saved = SavedPrescriptionPanel.Visibility == Visibility.Visible ? "saved" : "draft";
        return $"prescription:{saved}:{ResponseUrlText.Text}";
    }

    private void ShowSavedPrescriptionSummary(MedulaSavedPrescriptionSummary? summary)
    {
        currentSavedSummary = summary;
        var isSaved = summary is { PaymentFields.Count: > 0 };
        PrescriptionEntryActionsPanel.Visibility = isSaved ? Visibility.Collapsed : Visibility.Visible;
        SavedPrescriptionPanel.Visibility = isSaved ? Visibility.Visible : Visibility.Collapsed;
        SavedPaymentFieldsPanel.Children.Clear();
        SavedPrescriptionPrimaryActions.Children.Clear();
        SavedPrescriptionSecondaryActions.Children.Clear();
        SavedPrescriptionNavigationActions.Children.Clear();
        PrescriptionLockToggleButton.Visibility = Visibility.Collapsed;
        PrescriptionLockToggleButton.Tag = null;
        PrescriptionInformationHeader.Background = new SolidColorBrush(Color.FromRgb(220, 238, 245));
        PrescriptionInformationTitle.Foreground = new SolidColorBrush(Color.FromRgb(38, 58, 71));
        SavedPrescriptionTitleBorder.Background = new SolidColorBrush(Color.FromRgb(220, 238, 245));
        SavedPaymentTitleText.Foreground = new SolidColorBrush(Color.FromRgb(38, 58, 71));
        PrescriptionStatusRow.Background = new SolidColorBrush(Color.FromRgb(234, 243, 246));
        PrescriptionStatusRow.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 218, 223));
        PrescriptionSummaryText.Foreground = new SolidColorBrush(Color.FromRgb(12, 94, 136));
        if (!isSaved || summary is null)
        {
            savedPaymentExpanded = false;
            SavedPaymentToggleButton.Visibility = Visibility.Collapsed;
            return;
        }

        var salaryMedicationCopayment = summary.Title.Contains(
                "İlaç katılım payı maaştan tahsil edilecek",
                StringComparison.CurrentCultureIgnoreCase)
            || activeSnapshot?.Page.PageText.Contains(
                "İlaç katılım payı maaştan tahsil edilecek",
                StringComparison.CurrentCultureIgnoreCase) == true;
        SavedPaymentTitleText.Text = System.Text.RegularExpressions.Regex.Replace(
            summary.Title,
            @"\s*\(\s*İlaç katılım payı maaştan tahsil edilecek\s*\)\s*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (SavedPaymentTitleText.Text.Length == 0)
            SavedPaymentTitleText.Text = "Reçete Tutarları";
        var qrStatus = activeSnapshot?.Page.FieldValues is { } savedFields
            && savedFields.TryGetValue("medula:qr-status", out var savedQrStatus)
                ? savedQrStatus?.Trim() ?? string.Empty
                : activeSnapshot?.SourceListItem?.QrCodeStatus?.Trim() ?? string.Empty;
        var isQrFinalized = qrStatus.Contains("sonlandırıldı", StringComparison.CurrentCultureIgnoreCase)
            || activeSnapshot?.SourceListItem?.IsQrFinalized == true;
        var isQrUnfinalized = qrStatus.Contains("sonlandırılmadı", StringComparison.CurrentCultureIgnoreCase)
            || qrStatus.Equals("Karekodlu ilaç var", StringComparison.CurrentCultureIgnoreCase)
            || activeSnapshot?.SourceListItem?.IsQrUnfinalized == true;
        PrescriptionStatusRow.Background = new SolidColorBrush(isQrFinalized
            ? Color.FromRgb(218, 241, 224)
            : isQrUnfinalized ? Color.FromRgb(248, 220, 220) : Color.FromRgb(220, 238, 245));
        PrescriptionStatusRow.BorderBrush = new SolidColorBrush(isQrFinalized
            ? Color.FromRgb(150, 205, 162)
            : isQrUnfinalized ? Color.FromRgb(221, 157, 157) : Color.FromRgb(190, 211, 219));
        PrescriptionSummaryText.Foreground = new SolidColorBrush(isQrFinalized
            ? Color.FromRgb(27, 112, 59)
            : isQrUnfinalized ? Color.FromRgb(161, 40, 40) : Color.FromRgb(38, 58, 71));
        SavedPaymentFieldsPanel.RowDefinitions.Clear();
        SavedPaymentFieldsPanel.ColumnDefinitions.Clear();
        var paymentDetailsComplete = summary.PaymentFields.Count >= 9
            && summary.PaymentFields.All(field => HasDisplayValue(field.Value));
        var compactPayments = ApplicationSettingsStore.Current.HideUnimportantPrescriptionDetails
            && paymentDetailsComplete
            && !savedPaymentExpanded;
        SavedPaymentToggleButton.Visibility =
            ApplicationSettingsStore.Current.HideUnimportantPrescriptionDetails && paymentDetailsComplete
                ? Visibility.Visible
                : Visibility.Collapsed;
        SavedPaymentToggleButton.Content = compactPayments ? "▶" : "▼";
        SavedPaymentToggleButton.ToolTip = compactPayments
            ? "Tüm tutarları göster"
            : "Önemsiz tutarları gizle";
        for (var column = 0; column < 4; column++)
            SavedPaymentFieldsPanel.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < (compactPayments ? 1 : 4); row++)
            SavedPaymentFieldsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        MedulaPrescriptionPaymentField? Find(params string[] parts) =>
            summary.PaymentFields.FirstOrDefault(field => parts.All(part =>
                field.Label.Contains(part, StringComparison.CurrentCultureIgnoreCase)));

        void AddPayment(int row, int column, MedulaPrescriptionPaymentField? field, int columnSpan = 1)
        {
            if (field is null) return;
            var isPatientTotal = field.Label.Contains(
                "Hastanın eczaneye ödemesi gereken", StringComparison.OrdinalIgnoreCase);
            var value = new TextBlock
            {
                Text = field.Value,
                FontSize = isPatientTotal ? 18 : 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isPatientTotal
                    ? Color.FromRgb(15, 112, 84)
                    : Color.FromRgb(12, 94, 136))
            };
            var content = new StackPanel();
            var label = field.Label;
            if (salaryMedicationCopayment
                && label.Contains("İl.Kat.Payı", StringComparison.CurrentCultureIgnoreCase)
                && !label.Contains("Maaştan", StringComparison.CurrentCultureIgnoreCase))
                label += " (Maaştan)";
            content.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 108)),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            });
            if (isPatientTotal)
            {
                var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
                valueRow.Children.Add(value);
                valueRow.Children.Add(new TextBlock
                {
                    Text = $"Toplam borç: {prescriptionPartyDebtSummary.TotalDebt.ToString("N2", TurkishCulture)} ₺",
                    Margin = new Thickness(12, 3, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(23, 105, 170)),
                    FontWeight = FontWeights.SemiBold
                });
                content.Children.Add(valueRow);
            }
            else
            {
                content.Children.Add(value);
            }
            var card = new Border
            {
                MinHeight = 62,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(column == 0 ? 0 : 3, 0, column + columnSpan == 4 ? 0 : 3, 7),
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(isPatientTotal
                    ? Color.FromRgb(155, 207, 190)
                    : Color.FromRgb(215, 226, 230)),
                Background = new SolidColorBrush(isPatientTotal
                    ? Color.FromRgb(234, 247, 242)
                    : Colors.White),
                Child = content
            };
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            Grid.SetColumnSpan(card, columnSpan);
            SavedPaymentFieldsPanel.Children.Add(card);
        }

        if (compactPayments)
        {
            AddPayment(0, 0, Find("Toplam Tutar"));
            AddPayment(0, 1, Find("Ödenecek Tutar"));
            AddPayment(0, 2, Find("Fiyat Farkı"));
            AddPayment(0, 3, Find("Hastanın eczaneye ödemesi gereken"));
        }
        else
        {
            AddPayment(0, 0, Find("Reç.Kat.Payı", "Elden"));
            AddPayment(1, 0, Find("Reç.Kat.Payı", "Maaştan"));
            AddPayment(1, 1, Find("İl.Kat.Payı"));
            AddPayment(1, 2, Find("Toplam Tutar"));
            AddPayment(1, 3, Find("Ödenecek Tutar"));
            AddPayment(2, 0, Find("Mua.Kat.Payı", "Elden"));
            AddPayment(2, 1, Find("Fiyat Farkı"));
            AddPayment(3, 0, Find("Mua.Kat.Payı", "Maaştan"));
            AddPayment(3, 1, Find("Hastanın eczaneye ödemesi gereken"), 3);
        }

        var availableCaptions = summary.ActionCaptions
            .Select(NormalizeSavedPrescriptionActionCaption)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var shouldShowElectronicView = electronicPrescriptionContext
            && EPrescriptionNumberBox.Text.Trim().Length > 0;
        var prefix = electronicPrescriptionContext ? "E-Reçete " : string.Empty;
        var showQrFinalize = isQrUnfinalized
            || availableCaptions.Contains("Karekod Sonlandır");
        var lockCommand = isQrFinalized
            ? MedulaPrescriptionCommand.CancelQrFinalization
            : isQrUnfinalized ? MedulaPrescriptionCommand.FinalizeQr : (MedulaPrescriptionCommand?)null;
        var lockActionCaption = isQrFinalized
            ? "Karekod Sonlandırma İptal"
            : "Karekod Sonlandır";
        var lockActionAvailable = lockCommand is not null
            && availableCaptions.Contains(lockActionCaption);
        PrescriptionLockToggleButton.Visibility = lockActionAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrescriptionLockToggleButton.Tag = lockActionAvailable ? lockCommand : null;
        PrescriptionLockIcon.Text = isQrFinalized ? "\uE72E" : "\uE785";
        PrescriptionLockToggleButton.ToolTip = isQrFinalized
            ? "Karekod sonlandırmasını iptal et"
            : "Karekodları sonlandır";
        var primaryActions = new (string Caption, MedulaPrescriptionCommand? Command, bool Visible, bool ElectronicView)[]
        {
            ($"{prefix}Kaydet", MedulaPrescriptionCommand.SavePrescription, true, false),
            ("E-Reçete Görüntüle", null, shouldShowElectronicView, true),
            ($"{prefix}Sil", MedulaPrescriptionCommand.DeletePrescription, true, false),
            ($"{prefix}Bilgi", MedulaPrescriptionCommand.PrescriptionInformation, true, false),
            ($"{prefix}Yazdır", MedulaPrescriptionCommand.PrintPrescription, true, false),
            ("Yeni Reçete", MedulaPrescriptionCommand.NewPrescription, true, false),
            ("E-Reçete Sorgu", MedulaPrescriptionCommand.ElectronicPrescriptionQuery, true, false)
        };
        var secondaryActions = new List<(string Caption, MedulaPrescriptionCommand? Command, bool Visible, bool ElectronicView)>
        {
            ("ITS Karekod İşlemleri", MedulaPrescriptionCommand.ItsQrOperations, true, false),
            ("ÜTS İşlemleri", MedulaPrescriptionCommand.UtsOperations,
                availableCaptions.Contains("ÜTS İşlemleri"), false),
        };
        if (showQrFinalize)
            secondaryActions.Add(("Karekod Sonlandır", MedulaPrescriptionCommand.FinalizeQr, true, false));
        secondaryActions.Add((
            "Karekod Sonlandırma İptal",
            MedulaPrescriptionCommand.CancelQrFinalization,
            isQrFinalized,
            false));
        secondaryActions.Add(("Reçeteye Doküman Ekle", MedulaPrescriptionCommand.AddPrescriptionDocument, true, false));
        var navigationActions = new (string Caption, MedulaPrescriptionCommand? Command, bool Visible, bool ElectronicView)[]
        {
            ("Önceki Reçete", MedulaPrescriptionCommand.PreviousPrescription, true, false),
            ("Sonraki Reçete", MedulaPrescriptionCommand.NextPrescription, true, false)
        };

        SavedPrescriptionSecondaryActions.Columns = Math.Max(
            1, secondaryActions.Count(action => action.Visible));
        void AddActions(
            Panel panel,
            IEnumerable<(string Caption, MedulaPrescriptionCommand? Command, bool Visible, bool ElectronicView)> actions)
        {
            foreach (var action in actions)
            {
                var available = action.ElectronicView
                    ? shouldShowElectronicView
                    : action.Command is not null && availableCaptions.Contains(action.Caption);
                var button = new Button
                {
                    Content = action.Caption,
                    IsEnabled = available,
                    Tag = available ? action.Command : null,
                    Visibility = action.Visible ? Visibility.Visible : Visibility.Collapsed,
                    Height = 32,
                    Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = available
                        ? null
                        : action.Command is null
                            ? "Bu işlem reçeteyi değiştirdiği için güvenlik amacıyla devre dışıdır."
                            : "Bu işlem bu reçetede Medula tarafından sunulmuyor.",
                    Style = (Style)FindResource(action.Caption.EndsWith("Kaydet", StringComparison.CurrentCultureIgnoreCase)
                        ? "PrimaryActionButton"
                        : "ActionButton")
                };
                if (available)
                {
                    if (action.ElectronicView)
                        button.Click += OnViewElectronicPrescriptionClick;
                    else
                        button.Click += OnActionClick;
                }
                panel.Children.Add(button);
            }
        }
        AddActions(SavedPrescriptionPrimaryActions, primaryActions);
        AddActions(SavedPrescriptionSecondaryActions, secondaryActions);
        AddActions(SavedPrescriptionNavigationActions, navigationActions);
        if (returnToHistoryAction is not null)
        {
            SavedPrescriptionNavigationActions.Columns = 3;
            var returnButton = new Button
            {
                Content = "Reçete Geçmiş Listesine Geri Dön",
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)FindResource("ActionButton")
            };
            returnButton.Click += OnReturnToPrescriptionHistoryClick;
            SavedPrescriptionNavigationActions.Children.Add(returnButton);
        }
        ApplyAvailabilityPolicy();
    }

    private void OnReturnToPrescriptionHistoryClick(object sender, RoutedEventArgs e)
    {
        if (returnToHistoryAction is null) return;
        Dispatcher.BeginInvoke(returnToHistoryAction, DispatcherPriority.ContextIdle);
        CloseRequested?.Invoke();
    }

    private string NormalizeSavedPrescriptionActionCaption(string caption)
    {
        var normalized = caption.Trim().Replace("Döküman", "Doküman", StringComparison.CurrentCultureIgnoreCase);
        if (!electronicPrescriptionContext) return normalized;
        return normalized switch
        {
            "Kaydet" => "E-Reçete Kaydet",
            "Sil" => "E-Reçete Sil",
            "Bilgi" => "E-Reçete Bilgi",
            "Yazdır" => "E-Reçete Yazdır",
            _ => normalized
        };
    }

    private static bool IsPrescriptionOfflineSnapshot(MedulaPrescriptionSnapshot? snapshot) =>
        snapshot is { IsLocalArchive: true, SourceListItem: null };

    private void ApplyAvailabilityPolicy()
    {
        if (!IsInitialized) return;
        var isLive = availabilityMode == PrescriptionAvailabilityMode.Live && !historicalSnapshot;
        var canUseBackend = availabilityMode != PrescriptionAvailabilityMode.NetworkOffline;

        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (ReferenceEquals(button, MedulaRefreshButton))
            {
                button.IsEnabled = !historicalSnapshot && !busy;
                continue;
            }
            if (!TryGetPrescriptionCommand(button.Tag, out var command)) continue;
            button.IsEnabled = !historicalSnapshot && !busy && (availabilityMode switch
            {
                PrescriptionAvailabilityMode.Live => true,
                PrescriptionAvailabilityMode.NetworkOffline => false,
                PrescriptionAvailabilityMode.PrescriptionOffline =>
                    PrescriptionOfflineLookupActions.Contains(command)
                    || PrescriptionOfflineStandaloneActions.Contains(command),
                _ => false
            });
        }

        Control[] editableControls =
        [
            PatientIdentityBox, RecipientIdentityBox, OwnerPatientNameBox, RecipientPatientNameBox,
            RecipientTypeBox, ProvisionTypeBox, ProtocolNumberBox, FacilityCodeBox,
            PrescriptionDateBox, DispensingDateBox, PrescriptionTypeBox, PrescriptionSubTypeBox,
            RegistrationNumberBox, PhysicianTypeBox, PhysicianRegistrationBox,
            PhysicianFirstNameBox, PhysicianLastNameBox, CertificateTypeBox,
            HasQrCodeBox, SequentialDistributionBox, RepeatEligibilityBox,
            DrugQuickEntryBox, DiagnosisSearchBox
        ];
        foreach (var control in editableControls) control.IsEnabled = !busy && isLive;

        DrugGrid.IsReadOnly = !isLive;
        DiagnosisGrid.IsReadOnly = !isLive;
        if (DrugGrid.ContextMenu is not null) DrugGrid.ContextMenu.IsEnabled = !busy && isLive;
        DrugSuggestionPopup.IsOpen = isLive && DrugSuggestionPopup.IsOpen;
        DiagnosisSuggestionPopup.IsOpen = isLive && DiagnosisSuggestionPopup.IsOpen;
        ReturnToEntryButton.IsEnabled = !busy && isLive;
        MedulaRefreshButton.IsEnabled = !historicalSnapshot && !busy;
        MedulaRefreshButton.ToolTip = canUseBackend
            ? "Medula ekranını yenile"
            : "Medula bağlantısını ve reçete ekranını yeniden kurmayı dene";
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private bool ShowTurkishConfirmation(string message, string title)
    {
        var accepted = false;
        var dialog = new Window
        {
            Owner = Window.GetWindow(this),
            Title = title,
            Width = 510,
            SizeToContent = SizeToContent.Height,
            MinHeight = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.White
        };
        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(52, 72, 82)),
            FontSize = 13,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 20)
        };
        var yes = new Button
        {
            Content = "Evet",
            Width = 96,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(15, 118, 110)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 118, 110)),
            FontWeight = FontWeights.SemiBold,
            IsDefault = true
        };
        var no = new Button
        {
            Content = "Hayır",
            Width = 96,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(238, 243, 245)),
            Foreground = new SolidColorBrush(Color.FromRgb(52, 72, 82)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(205, 217, 222)),
            IsCancel = true
        };
        yes.Click += (_, _) =>
        {
            accepted = true;
            dialog.DialogResult = true;
        };
        no.Click += (_, _) => dialog.DialogResult = false;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(yes);
        actions.Children.Add(no);
        var content = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        content.Children.Add(messageText);
        content.Children.Add(actions);
        dialog.Content = content;
        dialog.ShowDialog();
        return accepted;
    }

    private bool ShowQrStockWarning(string details)
    {
        var removeCodes = false;
        var dialog = new Window
        {
            Owner = Window.GetWindow(this),
            Title = "Karekod Stok Uyarısı",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MinHeight = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.White
        };
        var message = new TextBlock
        {
            Text = details + "\n\nTamam seçilirse karekod girişten kaldırılır. Görmezden Gel seçilirse giriş korunur.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(137, 58, 46)),
            FontSize = 13,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 20)
        };
        var remove = new Button
        {
            Content = "Tamam",
            Width = 110,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(193, 71, 62)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(193, 71, 62)),
            FontWeight = FontWeights.SemiBold,
            IsDefault = true
        };
        var ignore = new Button
        {
            Content = "Görmezden Gel",
            Width = 132,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(238, 243, 245)),
            Foreground = new SolidColorBrush(Color.FromRgb(52, 72, 82)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(205, 217, 222)),
            IsCancel = true
        };
        remove.Click += (_, _) =>
        {
            removeCodes = true;
            dialog.DialogResult = true;
        };
        ignore.Click += (_, _) =>
        {
            removeCodes = false;
            dialog.DialogResult = false;
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(remove);
        actions.Children.Add(ignore);
        var content = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        content.Children.Add(message);
        content.Children.Add(actions);
        dialog.Content = content;
        dialog.ShowDialog();
        return removeCodes;
    }

    private void SetBusy(bool value, string? message = null)
    {
        busy = value;
        ApplyAvailabilityPolicy();
        if (!string.IsNullOrWhiteSpace(message))
        {
            ResponseMessageText.Text = message;
            ResponseBanner.Background = new SolidColorBrush(Color.FromRgb(242, 245, 246));
            ResponseMessageText.Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 108));
        }
    }
}
