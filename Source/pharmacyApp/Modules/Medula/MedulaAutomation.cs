namespace pharmacyApp.Modules.Medula;

public enum MedulaPrescriptionCommand
{
    PatientDrugs,
    PatientReports,
    Examinations,
    OffLabelCombined,
    OffLabelOld,
    OffLabelNew,
    FacilitySearch,
    FacilityDepartments,
    DoctorSearch,
    BranchSelect,
    WarningCode,
    TreatmentScheme,
    NewWarningCode,
    MagistralDrug,
    ReportSelection,
    SavePrescription,
    NewPrescription,
    ElectronicPrescriptionQuery,
    PreviousPrescription,
    NextPrescription,
    DeletePrescription,
    PrescriptionInformation,
    PrintPrescription,
    AddPrescriptionDocument,
    ItsQrOperations,
    UtsOperations,
    FinalizeQr,
    CancelQrFinalization
}

public enum MedulaDrugRowCommand
{
    SpecialCondition,
    Equivalent,
    DrugInformation,
    Report,
    Delete
}

public enum MedulaAdjacentDirection
{
    Previous,
    Next
}

public sealed record MedulaBrowserOperationResult(
    bool IsSuccess,
    MedulaConnectionState State,
    string Message,
    string Url,
    bool BrowserRecreated = false,
    bool StateRestored = false);

public sealed record MedulaPrescriptionLocator(
    string PrescriptionNumber,
    string PatientTcIdentityNo = "",
    string PatientDisplayName = "",
    string ElectronicPrescriptionNumber = "",
    DateOnly? MedulaFilterDate = null,
    int? Year = null,
    int? Month = null,
    string TypeValue = "",
    string Source = "function");

internal sealed record MedulaPrescriptionCommandDescriptor(
    string ActionId,
    string Caption,
    string? SubScreenTitle = null);

internal static class MedulaActionCatalog
{
    private static readonly IReadOnlyDictionary<MedulaPrescriptionCommand, MedulaPrescriptionCommandDescriptor>
        PrescriptionCommands = new Dictionary<MedulaPrescriptionCommand, MedulaPrescriptionCommandDescriptor>
        {
            [MedulaPrescriptionCommand.PatientDrugs] = new("f:buttonIlacListesi", "İlaç", "İlaçlar"),
            [MedulaPrescriptionCommand.PatientReports] = new("f:buttonRaporListesi", "Rapor", "Raporlar"),
            [MedulaPrescriptionCommand.Examinations] = new("f:buttonMuayeneListesi", "Muayene", "Muayene"),
            [MedulaPrescriptionCommand.OffLabelCombined] = new("f:buttonEndikasyonDisiSorgu", "End. Dışı", "Endikasyon Dışı — Eski İzinler"),
            [MedulaPrescriptionCommand.OffLabelOld] = new("f:buttonEndikasyonDisiSorgu", "End. Dışı", "Endikasyon Dışı — Eski İzinler"),
            [MedulaPrescriptionCommand.OffLabelNew] = new("f:buttonKisiEndikasyonDisiSorgu", "End. Dışı (Yeni)", "Endikasyon Dışı — Kişi Bazlı (Yeni)"),
            [MedulaPrescriptionCommand.FacilitySearch] = new("f:button1", "Tesis Ara", "Tesis Ara"),
            [MedulaPrescriptionCommand.FacilityDepartments] = new("f:buttonBolumler", "Bölümler", "Tesis Bölümleri"),
            [MedulaPrescriptionCommand.DoctorSearch] = new("f:buttonDoktorArama", "Ara"),
            [MedulaPrescriptionCommand.BranchSelect] = new("f:buttonBransSec", "Branş Seç", "Branş Seç"),
            [MedulaPrescriptionCommand.WarningCode] = new("f:buttonUyariKodu", "Uyarı Kodu", "Uyarı Kodu"),
            [MedulaPrescriptionCommand.TreatmentScheme] = new(
                "f:buttonIlacTedaviSema", "Ted. Şema", "Tedavi Şeması"),
            [MedulaPrescriptionCommand.NewWarningCode] = new(
                "f:buttonReceteTeshis", "Uyarı Kodu (Yeni)", "Uyarı Kodu (Yeni)"),
            [MedulaPrescriptionCommand.MagistralDrug] = new("f:buttonMajistralIlacEkle", "Maj. İlaç"),
            [MedulaPrescriptionCommand.ReportSelection] = new("f:buttonRaporSecim", "Rapor Seç", "Rapor Seç"),
            [MedulaPrescriptionCommand.SavePrescription] = new("f:buttonReceteKaydet", "Kaydet"),
            [MedulaPrescriptionCommand.NewPrescription] = new("f:buttonYeniRecete", "Yeni Reçete"),
            [MedulaPrescriptionCommand.ElectronicPrescriptionQuery] = new("f:buttonEreceteSorgu", "E-Reçete Sorgu"),
            [MedulaPrescriptionCommand.PreviousPrescription] = new("f:buttonOncekiRecete", "Önceki Reçete"),
            [MedulaPrescriptionCommand.NextPrescription] = new("f:buttonSonraki", "Sonraki Reçete"),
            [MedulaPrescriptionCommand.DeletePrescription] = new("f:buttonReceteSil", "Sil"),
            [MedulaPrescriptionCommand.PrescriptionInformation] = new("f:buttonReceteBilgi", "Bilgi"),
            [MedulaPrescriptionCommand.PrintPrescription] = new("f:buttonReceteYazdir", "Yazdır"),
            [MedulaPrescriptionCommand.AddPrescriptionDocument] = new("f:buttonDokumanEkle", "Reçeteye Doküman Ekle"),
            [MedulaPrescriptionCommand.ItsQrOperations] = new("f:buttonKarekodIslemleri", "ITS Karekod İşlemleri", "ITS Karekod İşlemleri"),
            [MedulaPrescriptionCommand.UtsOperations] = new("medula:buttonUtsIslemleri", "ÜTS İşlemleri", "ÜTS İşlemleri"),
            [MedulaPrescriptionCommand.FinalizeQr] = new("medula:buttonKarekodSonlandir", "Karekod Sonlandır"),
            [MedulaPrescriptionCommand.CancelQrFinalization] = new("medula:buttonKarekodSonlandirmaIptal", "Karekod Sonlandırma İptal")
        };

    private static readonly IReadOnlyDictionary<MedulaDrugRowCommand, MedulaPrescriptionCommandDescriptor>
        DrugCommands = new Dictionary<MedulaDrugRowCommand, MedulaPrescriptionCommandDescriptor>
        {
            [MedulaDrugRowCommand.SpecialCondition] = new("f:buttonOzelDurum", "Özel Durum"),
            [MedulaDrugRowCommand.Equivalent] = new("f:buttonIlacEsdeger", "Eşdeğer"),
            [MedulaDrugRowCommand.DrugInformation] = new("f:buttonIlacBilgiGorme", "İlaç Bilgi"),
            [MedulaDrugRowCommand.Report] = new("f:buttonRaporGoruntule", "Rapor"),
            [MedulaDrugRowCommand.Delete] = new("f:buttonIlacSil", "Sil")
        };

    public static MedulaPrescriptionCommandDescriptor Get(MedulaPrescriptionCommand command) =>
        PrescriptionCommands.TryGetValue(command, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported Medula command.");

    public static MedulaPrescriptionCommandDescriptor Get(MedulaDrugRowCommand command) =>
        DrugCommands.TryGetValue(command, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported Medula drug command.");
}

/// <summary>
/// Public, reusable Medula automation facade. UI modules call this facade; WebView2,
/// JavaScript and Medula DOM identifiers remain inside the connection service and action catalog.
/// </summary>
public sealed class MedulaAutomation
{
    private readonly MedulaConnectionService driver;

    private MedulaAutomation(MedulaConnectionService driver) => this.driver = driver;

    public static MedulaAutomation Instance { get; } = new(MedulaConnectionService.Instance);
    public MedulaConnectionState State => driver.State;

    public event EventHandler<MedulaConnectionStatus>? StatusChanged
    {
        add => driver.StatusChanged += value;
        remove => driver.StatusChanged -= value;
    }
    public event Action<MedulaScopedText>? ScopedWarningChanged
    {
        add => driver.ScopedWarningChanged += value;
        remove => driver.ScopedWarningChanged -= value;
    }
    public event Action<MedulaScopedText>? ScopedConfirmationChanged
    {
        add => driver.ScopedConfirmationChanged += value;
        remove => driver.ScopedConfirmationChanged -= value;
    }
    public event Action<MedulaScopedText>? ScopedNoticeChanged
    {
        add => driver.ScopedNoticeChanged += value;
        remove => driver.ScopedNoticeChanged -= value;
    }
    public event Action? ForegroundOperationRequested
    {
        add => driver.ForegroundOperationRequested += value;
        remove => driver.ForegroundOperationRequested -= value;
    }

    public Task<MedulaBrowserOperationResult> OpenWebBrowserAsync(
        bool visible = false,
        CancellationToken cancellationToken = default) =>
        driver.OpenWebBrowserAsync(visible, cancellationToken);

    public Task<MedulaBrowserOperationResult> RestartWebBrowserAsync(
        bool restorePreviousState = true,
        CancellationToken cancellationToken = default) =>
        driver.RestartWebBrowserAsync(restorePreviousState, cancellationToken);

    public Task<MedulaBrowserOperationResult> LoginAsync(
        bool forceNewSession = false,
        CancellationToken cancellationToken = default) =>
        driver.LoginAsync(forceNewSession, cancellationToken);

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) =>
        driver.ConnectAsync(cancellationToken);
    public void ShowBrowser() => driver.ShowBrowser();
    public void HideBrowser() => driver.HideBrowser();
    public Task<MedulaBrowserOperationResult> CloseWebBrowserAsync(CancellationToken token = default) =>
        driver.CloseWebBrowserAsync(token);
    public Task<MedulaBrowserOperationResult> GetBrowserStateAsync() => driver.GetBrowserStateAsync();
    public Task<MedulaBrowserOperationResult> OpenHomeAsync(CancellationToken token = default) =>
        driver.OpenHomeAsync(token);
    public Task<MedulaBrowserOperationResult> RefreshCurrentPageAsync(CancellationToken token = default) =>
        driver.RefreshCurrentPageAsync(token);
    public Task<MedulaBrowserOperationResult> RestoreLastStateAsync(CancellationToken token = default) =>
        driver.RestartWebBrowserAsync(true, token);
    public void RequestForegroundPriority() => driver.RequestForegroundPriority();

    public Task<MedulaPrescriptionSnapshot?> OpenPrescriptionAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken = default) =>
        driver.OpenPrescriptionAsync(locator, cancellationToken);

    public Task<MedulaPrescriptionListResult> OpenPatientPrescriptionsAsync(
        string patientTcIdentityNo,
        string patientDisplayName = "",
        CancellationToken cancellationToken = default) =>
        driver.SearchActivePrescriptionsByPatientAsync(patientTcIdentityNo, patientDisplayName, cancellationToken);

    public Task<MedulaBrowserOperationResult> OpenPrescriptionQueryAsync(
        CancellationToken cancellationToken = default) =>
        driver.OpenPrescriptionQueryAsync(cancellationToken);

    public Task<MedulaPrescriptionListResult> OpenPrescriptionsByDayAsync(
        DateOnly date,
        string typeValue,
        CancellationToken cancellationToken = default) =>
        driver.SearchPrescriptionListAsync(date, date, typeValue, cancellationToken);

    public Task<MedulaPrescriptionListResult> OpenPrescriptionsByPeriodAsync(
        int year,
        int month,
        string typeValue,
        CancellationToken cancellationToken = default) =>
        driver.SearchPrescriptionListByMedulaFilterAsync(
            false, 0, new DateOnly(year, month, 1).ToString("yyyyMMdd"), typeValue, cancellationToken);

    public Task<MedulaPrescriptionListResult> OpenPrescriptionsByDateRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        string typeValue,
        CancellationToken cancellationToken = default) =>
        driver.SearchPrescriptionListAsync(startDate, endDate, typeValue, cancellationToken);

    public Task<MedulaPrescriptionPageResult> ExecutePrescriptionCommandAsync(
        MedulaPrescriptionInput input,
        MedulaPrescriptionCommand command,
        string? caption = null,
        bool confirmationAccepted = false,
        CancellationToken cancellationToken = default)
    {
        var descriptor = MedulaActionCatalog.Get(command);
        return driver.ExecutePrescriptionActionAsync(
            input, descriptor.ActionId, caption ?? descriptor.Caption, confirmationAccepted, cancellationToken);
    }

    public Task<MedulaRemoteScreen> OpenPrescriptionSubScreenAsync(
        MedulaPrescriptionInput input,
        MedulaPrescriptionCommand command,
        CancellationToken cancellationToken = default)
        => OpenPrescriptionSubScreenAsync(input, command, null, cancellationToken);

    public Task<MedulaRemoteScreen> OpenPrescriptionSubScreenAsync(
        MedulaPrescriptionInput input,
        MedulaPrescriptionCommand command,
        string? title,
        CancellationToken cancellationToken = default)
    {
        var descriptor = MedulaActionCatalog.Get(command);
        if (descriptor.SubScreenTitle is null)
            throw new ArgumentOutOfRangeException(nameof(command), command, "Command does not open a sub-screen.");
        return driver.OpenPrescriptionSubScreenAsync(
            input, descriptor.ActionId, title ?? descriptor.SubScreenTitle, cancellationToken);
    }

    public Task<MedulaPrescriptionSnapshot?> OpenAdjacentPrescriptionAsync(
        MedulaPrescriptionInput input,
        MedulaAdjacentDirection direction,
        CancellationToken cancellationToken = default) =>
        driver.OpenAdjacentPrescriptionAsync(
            input,
            MedulaActionCatalog.Get(direction == MedulaAdjacentDirection.Previous
                ? MedulaPrescriptionCommand.PreviousPrescription
                : MedulaPrescriptionCommand.NextPrescription).ActionId,
            cancellationToken);

    public Task<MedulaDrugActionResult> ExecuteDrugRowCommandAsync(
        MedulaPrescriptionInput input,
        int rowIndex,
        MedulaDrugRowCommand command,
        CancellationToken cancellationToken = default)
    {
        var descriptor = MedulaActionCatalog.Get(command);
        return driver.ExecutePrescriptionDrugRowActionAsync(
            input, rowIndex, descriptor.ActionId, descriptor.Caption, cancellationToken);
    }

    public Task<MedulaRemoteScreen> OpenPatientDrugsAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.PatientDrugs, token);
    public Task<MedulaRemoteScreen> OpenPatientMedicationListFromDailyPrescriptionAsync(
        string patientTcIdentityNo,
        CancellationToken token = default) =>
        driver.OpenPatientMedicationListFromDailyPrescriptionAsync(patientTcIdentityNo, token);
    public Task<MedulaRemoteScreen> OpenPatientReportsAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.PatientReports, token);
    public Task<MedulaRemoteScreen> OpenPatientReportsFromDailyPrescriptionAsync(
        string patientTcIdentityNo,
        CancellationToken token = default) =>
        driver.OpenPatientReportsFromDailyPrescriptionAsync(patientTcIdentityNo, token);
    public Task<MedulaRemoteScreen> OpenExaminationsAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.Examinations, token);
    public Task<MedulaRemoteScreen> OpenOffLabelUseAsync(MedulaPrescriptionInput input, bool personBased, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, personBased ? MedulaPrescriptionCommand.OffLabelNew : MedulaPrescriptionCommand.OffLabelOld, token);
    public Task<MedulaRemoteScreen> OpenFacilitySearchAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.FacilitySearch, token);
    public Task<MedulaRemoteScreen> OpenFacilityDepartmentsAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.FacilityDepartments, token);
    public Task<MedulaRemoteScreen> OpenBranchSelectionAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.BranchSelect, token);
    public Task<MedulaRemoteScreen> OpenWarningCodesAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.WarningCode, token);
    public Task<MedulaRemoteScreen> OpenItsQrOperationsAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.ItsQrOperations, token);
    public Task<MedulaRemoteScreen> OpenUtsOperationsAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.UtsOperations, token);

    public Task<MedulaPrescriptionPageResult> SearchDoctorAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.DoctorSearch, cancellationToken: token);
    public Task<MedulaRemoteScreen> OpenTreatmentSchemeAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.TreatmentScheme, token);
    public Task<MedulaRemoteScreen> OpenNewWarningCodeAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.NewWarningCode, token);
    public Task<MedulaPrescriptionPageResult> OpenMagistralDrugAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.MagistralDrug, cancellationToken: token);
    public Task<MedulaRemoteScreen> OpenReportSelectionAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        OpenPrescriptionSubScreenAsync(input, MedulaPrescriptionCommand.ReportSelection, token);
    public Task<MedulaPrescriptionPageResult> SavePrescriptionAsync(MedulaPrescriptionInput input, bool confirmationAccepted = false, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.SavePrescription, confirmationAccepted: confirmationAccepted, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> DeletePrescriptionAsync(MedulaPrescriptionInput input, bool confirmationAccepted = false, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.DeletePrescription, confirmationAccepted: confirmationAccepted, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> CreateNewPrescriptionAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.NewPrescription, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> OpenElectronicPrescriptionQueryFromPrescriptionAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.ElectronicPrescriptionQuery, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> GetPrescriptionInformationAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.PrescriptionInformation, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> PrintPrescriptionAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.PrintPrescription, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> OpenPrescriptionDocumentManagerAsync(MedulaPrescriptionInput input, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.AddPrescriptionDocument, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> FinalizePrescriptionQrAsync(MedulaPrescriptionInput input, bool confirmationAccepted = false, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.FinalizeQr, confirmationAccepted: confirmationAccepted, cancellationToken: token);
    public Task<MedulaPrescriptionPageResult> CancelPrescriptionQrFinalizationAsync(MedulaPrescriptionInput input, bool confirmationAccepted = false, CancellationToken token = default) =>
        ExecutePrescriptionCommandAsync(input, MedulaPrescriptionCommand.CancelQrFinalization, confirmationAccepted: confirmationAccepted, cancellationToken: token);

    public Task<MedulaDrugActionResult> OpenDrugSpecialConditionAsync(MedulaPrescriptionInput input, int rowIndex, CancellationToken token = default) =>
        ExecuteDrugRowCommandAsync(input, rowIndex, MedulaDrugRowCommand.SpecialCondition, token);
    public Task<MedulaDrugActionResult> OpenDrugEquivalentsAsync(MedulaPrescriptionInput input, int rowIndex, CancellationToken token = default) =>
        ExecuteDrugRowCommandAsync(input, rowIndex, MedulaDrugRowCommand.Equivalent, token);
    public Task<MedulaDrugActionResult> OpenDrugInformationAsync(MedulaPrescriptionInput input, int rowIndex, CancellationToken token = default) =>
        ExecuteDrugRowCommandAsync(input, rowIndex, MedulaDrugRowCommand.DrugInformation, token);
    public Task<MedulaDrugActionResult> OpenDrugReportAsync(MedulaPrescriptionInput input, int rowIndex, CancellationToken token = default) =>
        ExecuteDrugRowCommandAsync(input, rowIndex, MedulaDrugRowCommand.Report, token);
    public Task<MedulaDrugActionResult> DeleteDrugAsync(MedulaPrescriptionInput input, int rowIndex, CancellationToken token = default) =>
        ExecuteDrugRowCommandAsync(input, rowIndex, MedulaDrugRowCommand.Delete, token);

    public Task<MedulaPrescriptionPageResult> OpenPrescriptionEntryAsync(CancellationToken token = default) => driver.OpenPrescriptionEntryAsync(token);
    public Task<MedulaPrescriptionPageResult> RefreshPrescriptionEntryAsync(CancellationToken token = default) => driver.RefreshPrescriptionEntryAsync(token);
    public Task<MedulaElectronicPrescriptionQueryResult> OpenElectronicPrescriptionQueryAsync(CancellationToken token = default) => driver.OpenElectronicPrescriptionQueryAsync(token);
    public Task<MedulaElectronicPrescriptionQueryResult> SearchElectronicPrescriptionsAsync(string tc, string number, string tracking, string date, bool byTracking, CancellationToken token = default) => driver.SearchElectronicPrescriptionsAsync(tc, number, tracking, date, byTracking, token);
    public Task<MedulaElectronicPrescriptionDetail> OpenElectronicPrescriptionDetailAsync(MedulaElectronicPrescriptionListItem item, string tc, CancellationToken token = default) => driver.OpenElectronicPrescriptionDetailAsync(item, tc, token);
    public Task<MedulaElectronicPrescriptionQueryResult> ReturnFromElectronicPrescriptionDetailAsync(CancellationToken token = default) => driver.ReturnFromElectronicPrescriptionDetailAsync(token);
    public Task<bool> ReturnFromElectronicPrescriptionDetailToPrescriptionAsync(CancellationToken token = default) => driver.ReturnFromElectronicPrescriptionDetailToPrescriptionAsync(token);
    public Task<MedulaElectronicPrescriptionContinueResult> ContinueElectronicPrescriptionAsync(string tc, string number, CancellationToken token = default) => driver.ContinueElectronicPrescriptionAsync(tc, number, token);
    public Task<MedulaElectronicPrescriptionDetail> ReturnToElectronicPrescriptionDetailAsync(string tc, string number, CancellationToken token = default) => driver.ReturnToElectronicPrescriptionDetailAsync(tc, number, token);
    public Task<MedulaPrescriptionListResult> SearchPrescriptionListAsync(DateOnly start, DateOnly end, string type, CancellationToken token = default) => driver.SearchPrescriptionListAsync(start, end, type, token);
    public Task<MedulaPrescriptionListResult> SearchPrescriptionListByMedulaFilterAsync(bool daily, int day, string period, string type, CancellationToken token = default) => driver.SearchPrescriptionListByMedulaFilterAsync(daily, day, period, type, token);
    public Task<IReadOnlyList<MedulaPrescriptionPeriodOption>> LoadPrescriptionListPeriodsAsync(CancellationToken token = default) => driver.LoadPrescriptionListPeriodsAsync(token);
    public Task<MedulaPrescriptionListResult> SearchReachablePrescriptionsAsync(string type, CancellationToken token = default) => driver.SearchReachablePrescriptionsAsync(type, token);
    public Task<MedulaPrescriptionListResult> SearchActivePrescriptionsByPatientAsync(string tc, string name, CancellationToken token = default) => driver.SearchActivePrescriptionsByPatientAsync(tc, name, token);
    public Task<MedulaPrescriptionSnapshot?> OpenPrescriptionFromListAsync(MedulaPrescriptionListItem item, CancellationToken token = default) => driver.OpenPrescriptionFromListAsync(item, token);
    public Task<MedulaPrescriptionSnapshot?> CaptureCurrentPrescriptionAsync(string name, MedulaPrescriptionListItem? item, Guid? uid, int version, CancellationToken token = default) => driver.CaptureCurrentPrescriptionAsync(name, item, uid, version, token);
    public Task<MedulaPrescriptionSnapshot?> RefreshPrescriptionContextAsync(MedulaPrescriptionInput input, string name, Guid? uid, int version, CancellationToken token = default) => driver.RefreshPrescriptionContextAsync(input, name, uid, version, token);
    public void RememberSavedPrescriptionContext(MedulaPrescriptionSnapshot snapshot) => driver.RememberSavedPrescriptionContext(snapshot);
    public Task<MedulaDrugSearchResult> SearchPrescriptionDrugsAsync(MedulaPrescriptionInput input, string query, CancellationToken token = default) => driver.SearchPrescriptionDrugsAsync(input, query, token);
    public Task<MedulaDrugActionResult> SelectPrescriptionDrugAsync(MedulaPrescriptionInput input, MedulaDrugSearchOption option, CancellationToken token = default) => driver.SelectPrescriptionDrugAsync(input, option, token);
    public Task<MedulaDrugActionResult> AddPrescriptionDrugBarcodeAsync(MedulaPrescriptionInput input, string barcode, CancellationToken token = default) => driver.AddPrescriptionDrugBarcodeAsync(input, barcode, token);
    public Task<MedulaDiagnosisSearchResult> SearchPrescriptionDiagnosesAsync(MedulaPrescriptionInput input, string query, CancellationToken token = default) => driver.SearchPrescriptionDiagnosesAsync(input, query, token);
    public Task<MedulaDiagnosisActionResult> SelectPrescriptionDiagnosisAsync(MedulaDiagnosisSearchOption option, CancellationToken token = default) => driver.SelectPrescriptionDiagnosisAsync(option, token);
    public Task<MedulaDiagnosisActionResult> SetPrescriptionDiagnosisEntryAsync(MedulaPrescriptionInput input, MedulaDiagnosisEntry entry, CancellationToken token = default) => driver.SetPrescriptionDiagnosisEntryAsync(input, entry, token);
    public Task<MedulaDrugActionResult> UpdatePrescriptionDrugBarcodeAsync(MedulaPrescriptionInput input, int row, string barcode, CancellationToken token = default) => driver.UpdatePrescriptionDrugBarcodeAsync(input, row, barcode, token);
    public Task<MedulaPrescriptionPageResult> SynchronizePrescriptionDatesAsync(MedulaPrescriptionInput input, CancellationToken token = default) => driver.SynchronizePrescriptionDatesAsync(input, token);
    public Task ClosePrescriptionDrugOverlayAsync(string key, CancellationToken token = default) => driver.ClosePrescriptionDrugOverlayAsync(key, token);
    public Task<MedulaDrugActionResult> SetPrescriptionDrugSpecialConditionAsync(string value, CancellationToken token = default) => driver.SetPrescriptionDrugSpecialConditionAsync(value, token);
    public Task<MedulaRemoteScreen> LookupPatientFromFreshPrescriptionEntryAsync(string tc, MedulaPrescriptionInput restore, CancellationToken token = default) => driver.LookupPatientFromFreshPrescriptionEntryAsync(tc, restore, token);
    public Task<MedulaRemoteScreen> ReadDoctorBranchSelectionAsync(CancellationToken token = default) => driver.ReadDoctorBranchSelectionAsync(token);
    public Task<MedulaRemoteScreen> ExecuteSubScreenActionAsync(string key, string title, string actionId, MedulaRemoteFieldPayload payload, CancellationToken token = default, bool confirmationAccepted = false) => driver.ExecuteSubScreenActionAsync(key, title, actionId, payload, token, confirmationAccepted);
    public Task<MedulaRemoteScreen> ExecuteTablePageActionAsync(string key, string title, string tableId, string actionId, CancellationToken token = default) => driver.ExecuteTablePageActionAsync(key, title, tableId, actionId, token);
    public Task<MedulaPrescriptionPageResult> ReturnToPrescriptionAsync(CancellationToken token = default) => driver.ReturnToPrescriptionAsync(token);
    public Task ClosePrescriptionInlineScreenAsync(string key, CancellationToken token = default) => driver.ClosePrescriptionInlineScreenAsync(key, token);
    public Task<(bool IsSuccess, string Message)> SelectPrescriptionDocumentFileAsync(MedulaRemoteScreen screen, string path, CancellationToken token = default) => driver.SelectPrescriptionDocumentFileAsync(screen, path, token);
    public Task<MedulaRemoteScreen> UploadPrescriptionDocumentAsync(MedulaRemoteScreen screen, string type, string path, bool confirmationAccepted, CancellationToken cancellationToken = default) => driver.UploadPrescriptionDocumentAsync(screen, type, path, confirmationAccepted, cancellationToken);
    public Task<MedulaPrescriptionPageResult> ClosePrescriptionDocumentAsync(MedulaPrescriptionInput input, CancellationToken token = default) => driver.ClosePrescriptionDocumentAsync(input, token);
}
