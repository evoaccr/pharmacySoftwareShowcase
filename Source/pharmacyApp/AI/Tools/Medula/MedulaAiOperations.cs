using pharmacyApp.Modules.Medula;

namespace pharmacyApp.AI.Tools.Medula;

public interface IMedulaAiOperations
{
    Task<MedulaBrowserOperationResult> GetSessionStateAsync(CancellationToken cancellationToken);
    Task<MedulaBrowserOperationResult> LoginAsync(bool forceNewSession, CancellationToken cancellationToken);
    Task<MedulaBrowserOperationResult> RestartSessionAsync(bool restorePreviousState, CancellationToken cancellationToken);
    Task<MedulaPrescriptionSnapshot?> OpenPrescriptionAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken);
    Task<MedulaPrescriptionListResult> SearchPatientPrescriptionsAsync(
        string patientTcIdentityNo,
        string patientDisplayName,
        CancellationToken cancellationToken);
    Task<MedulaPrescriptionListResult> SearchLivePrescriptionsAsync(
        DateOnly startDate,
        DateOnly endDate,
        string invoiceTypeCode,
        CancellationToken cancellationToken);
    Task<MedulaPrescriptionListResult> SearchMedulaFilteredPrescriptionsAsync(
        bool daily,
        int day,
        string period,
        string invoiceTypeCode,
        CancellationToken cancellationToken);
    Task<MedulaPrescriptionListResult> SearchReachablePrescriptionsAsync(
        string invoiceTypeCode,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MedulaPrescriptionListItem>> SearchLocalPrescriptionsAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? invoiceTypeCode,
        bool includePermanentlyDeleted,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MedulaPrescriptionPeriodOption>> LoadPrescriptionPeriodsAsync(
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MedulaPrescriptionHistoryEntry>> LoadPrescriptionHistoryAsync(
        Guid prescriptionUid,
        CancellationToken cancellationToken);
    Task<MedulaElectronicPrescriptionQueryResult> SearchElectronicPrescriptionsAsync(
        string patientTcIdentityNo,
        string electronicPrescriptionNumber,
        string trackingNumber,
        string prescriptionDate,
        bool searchByTrackingNumber,
        CancellationToken cancellationToken);
    Task<MedulaElectronicPrescriptionDetail> OpenElectronicPrescriptionDetailAsync(
        MedulaElectronicPrescriptionListItem item,
        string patientTcIdentityNo,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> SearchPatientMedicationHistoryAsync(
        string patientTcIdentityNo,
        bool eyeMedications,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> GetPatientMedicationListAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> GetPatientEligibilityAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> SearchPatientReportsAsync(
        string patientTcIdentityNo,
        bool includeExpired,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> GetPatientReportAsync(
        string patientTcIdentityNo,
        string reportNumber,
        CancellationToken cancellationToken);
    Task<MedulaDrugSearchResult> SearchDrugsAsync(
        string query,
        CancellationToken cancellationToken);
    Task<MedulaDrugActionResult> GetDrugAsync(
        string barcode,
        CancellationToken cancellationToken);
    Task<MedulaDrugActionResult> SearchDrugEquivalentsAsync(
        string barcode,
        CancellationToken cancellationToken);
    Task<MedulaPrescriptionPageResult> SearchDoctorAsync(
        string diplomaRegistrationNumber,
        CancellationToken cancellationToken);
    Task<MedulaDiagnosisSearchResult> SearchDiagnosesAsync(
        string query,
        CancellationToken cancellationToken);
    Task<MedulaPrescriptionSnapshot?> GetPrescriptionWarningsAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> SearchPatientExaminationsAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> SearchPatientOffLabelApprovalsAsync(
        string patientTcIdentityNo,
        bool personBased,
        CancellationToken cancellationToken);
    Task<MedulaRemoteScreen> GetPatientTreatmentSchemeAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken);
}

public sealed class MedulaAiOperations : IMedulaAiOperations
{
    private readonly MedulaAutomation automation;
    private readonly MedulaPrescriptionRepository prescriptionRepository;

    public MedulaAiOperations(
        MedulaAutomation? automation = null,
        MedulaPrescriptionRepository? prescriptionRepository = null)
    {
        this.automation = automation ?? MedulaAutomation.Instance;
        this.prescriptionRepository = prescriptionRepository ?? new MedulaPrescriptionRepository();
    }

    public Task<MedulaBrowserOperationResult> GetSessionStateAsync(CancellationToken cancellationToken) =>
        automation.GetBrowserStateAsync().WaitAsync(cancellationToken);

    public Task<MedulaBrowserOperationResult> LoginAsync(
        bool forceNewSession,
        CancellationToken cancellationToken) =>
        automation.LoginAsync(forceNewSession, cancellationToken);

    public Task<MedulaBrowserOperationResult> RestartSessionAsync(
        bool restorePreviousState,
        CancellationToken cancellationToken) =>
        automation.RestartWebBrowserAsync(restorePreviousState, cancellationToken);

    public async Task<MedulaPrescriptionSnapshot?> OpenPrescriptionAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken)
    {
        if (!pharmacyApp.Infrastructure.OnlineAccessPolicy.MedulaOnlineEnabled)
            return await OpenLocalPrescriptionAsync(locator, cancellationToken);
        if (locator.Source.Contains("local_first", StringComparison.OrdinalIgnoreCase) &&
            await OpenLocalPrescriptionAsync(locator, cancellationToken) is { } preferredLocal)
            return preferredLocal;
        var live = await automation.OpenPrescriptionAsync(locator, cancellationToken);
        if (live is not null) return live;
        return await OpenLocalPrescriptionAsync(locator, cancellationToken);
    }

    private async Task<MedulaPrescriptionSnapshot?> OpenLocalPrescriptionAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken)
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        var local = await SearchLocalPrescriptionsAsync(
            end.AddYears(-20), end,
            string.IsNullOrWhiteSpace(locator.TypeValue) ? null : locator.TypeValue,
            includePermanentlyDeleted: false,
            cancellationToken);
        var archived = local.FirstOrDefault(item =>
            item.PrescriptionNumber.Equals(locator.PrescriptionNumber, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(locator.PatientTcIdentityNo) ||
             item.PatientTcIdentityNo.Equals(locator.PatientTcIdentityNo, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(locator.ElectronicPrescriptionNumber) ||
             (item.LocalSnapshot?.Page.FieldValues?
                 .GetValueOrDefault("medula:e-prescription-no") ?? string.Empty)
             .Equals(locator.ElectronicPrescriptionNumber, StringComparison.OrdinalIgnoreCase)));
        return archived?.LocalSnapshot is { } snapshot
            ? snapshot with
            {
                SourceListItem = archived with { IsLiveOnMedula = false },
                IsLocalArchive = true
            }
            : null;
    }

    public async Task<MedulaPrescriptionListResult> SearchPatientPrescriptionsAsync(
        string patientTcIdentityNo,
        string patientDisplayName,
        CancellationToken cancellationToken)
    {
        var live = await RunPrescriptionQueryWithRecoveryAsync(
            token => automation.OpenPatientPrescriptionsAsync(
                patientTcIdentityNo, patientDisplayName, token),
            cancellationToken);
        var end = DateOnly.FromDateTime(DateTime.Today);
        var local = (await SearchLocalPrescriptionsAsync(
                end.AddYears(-20), end, invoiceTypeCode: null,
                includePermanentlyDeleted: false, cancellationToken))
            .Where(item => item.PatientTcIdentityNo.Equals(
                patientTcIdentityNo, StringComparison.Ordinal))
            .ToArray();
        var merged = MergePatientPrescriptions(local, live.Prescriptions);
        var patient = string.IsNullOrWhiteSpace(patientDisplayName)
            ? "Hasta"
            : patientDisplayName.Trim();
        if (live.IsSuccess)
            return new MedulaPrescriptionListResult(
                true,
                merged.Count == 0
                    ? $"{patient} adlı hastanın reçetesi bulunamadı."
                    : $"{patient} adlı hastanın {merged.Count} reçetesi bulundu; " +
                      $"{merged.Count(item => item.IsLiveOnMedula)} tanesi Medula'da aktif.",
                merged,
                live.WarningText);
        if (merged.Count > 0)
            return new MedulaPrescriptionListResult(
                true,
                $"{patient} adlı hastanın yerel arşivindeki {merged.Count} reçete bulundu; " +
                "Medula'daki aktiflik durumu doğrulanamadı.",
                merged,
                live.WarningText ?? live.Message);
        if (IsNoPrescriptionRecord(live))
            return new MedulaPrescriptionListResult(
                true,
                $"{patient} adlı hastanın yerel arşivinde veya Medula'da reçetesi bulunamadı.",
                [],
                live.WarningText);
        return live;
    }

    public Task<MedulaPrescriptionListResult> SearchLivePrescriptionsAsync(
        DateOnly startDate,
        DateOnly endDate,
        string invoiceTypeCode,
        CancellationToken cancellationToken) =>
        RunPrescriptionQueryWithRecoveryAsync(
            token => automation.OpenPrescriptionsByDateRangeAsync(
                startDate, endDate, invoiceTypeCode, token), cancellationToken);

    public Task<MedulaPrescriptionListResult> SearchMedulaFilteredPrescriptionsAsync(
        bool daily,
        int day,
        string period,
        string invoiceTypeCode,
        CancellationToken cancellationToken) =>
        RunPrescriptionQueryWithRecoveryAsync(
            token => automation.SearchPrescriptionListByMedulaFilterAsync(
                daily, day, period, invoiceTypeCode, token), cancellationToken);

    public Task<MedulaPrescriptionListResult> SearchReachablePrescriptionsAsync(
        string invoiceTypeCode,
        CancellationToken cancellationToken) =>
        RunPrescriptionQueryWithRecoveryAsync(
            token => automation.SearchReachablePrescriptionsAsync(invoiceTypeCode, token),
            cancellationToken);

    public async Task<IReadOnlyList<MedulaPrescriptionListItem>> SearchLocalPrescriptionsAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? invoiceTypeCode,
        bool includePermanentlyDeleted,
        CancellationToken cancellationToken)
    {
        var active = await prescriptionRepository.LoadLatestAsync(
            startDate,
            endDate,
            invoiceTypeCode,
            cancellationToken);
        if (!includePermanentlyDeleted) return active;
        var deleted = await prescriptionRepository.LoadPermanentlyDeletedAsync(
            startDate,
            endDate,
            invoiceTypeCode,
            cancellationToken);
        return active.Concat(deleted)
            .OrderByDescending(item => ParseListDate(item.MedulaFilterDate)
                ?? ParseListDate(item.PrescriptionDate)
                ?? DateOnly.MinValue)
            .ThenByDescending(item => double.IsNaN(item.MedulaOrderKey)
                ? double.MinValue
                : item.MedulaOrderKey)
            .ThenByDescending(item => item.VersionNumber)
            .ToArray();
    }

    public Task<IReadOnlyList<MedulaPrescriptionPeriodOption>> LoadPrescriptionPeriodsAsync(
        CancellationToken cancellationToken) =>
        automation.LoadPrescriptionListPeriodsAsync(cancellationToken);

    public Task<IReadOnlyList<MedulaPrescriptionHistoryEntry>> LoadPrescriptionHistoryAsync(
        Guid prescriptionUid,
        CancellationToken cancellationToken) =>
        prescriptionRepository.LoadHistoryAsync(prescriptionUid, cancellationToken);

    public Task<MedulaElectronicPrescriptionQueryResult> SearchElectronicPrescriptionsAsync(
        string patientTcIdentityNo,
        string electronicPrescriptionNumber,
        string trackingNumber,
        string prescriptionDate,
        bool searchByTrackingNumber,
        CancellationToken cancellationToken) =>
        automation.SearchElectronicPrescriptionsAsync(
            patientTcIdentityNo,
            electronicPrescriptionNumber,
            trackingNumber,
            prescriptionDate,
            searchByTrackingNumber,
            cancellationToken);

    public Task<MedulaElectronicPrescriptionDetail> OpenElectronicPrescriptionDetailAsync(
        MedulaElectronicPrescriptionListItem item,
        string patientTcIdentityNo,
        CancellationToken cancellationToken) =>
        automation.OpenElectronicPrescriptionDetailAsync(item, patientTcIdentityNo, cancellationToken);

    public async Task<MedulaRemoteScreen> SearchPatientMedicationHistoryAsync(
        string patientTcIdentityNo,
        bool eyeMedications,
        CancellationToken cancellationToken)
    {
        var verification = await GetPatientEligibilityAsync(
            patientTcIdentityNo, cancellationToken);
        if (!verification.IsOpen) return verification with
        {
            Key = "medication-history",
            Title = "İlaç Geçmişi"
        };

        // Medula's prescription-entry "İlaç" action is not a standalone patient
        // history search: SGK requires a facility, doctor and an entered drug.
        // Never fabricate those values or mutate a draft prescription.  Once the
        // patient is verified live, return their medication lines from the
        // application's synchronized Medula prescription archive instead.
        var end = DateOnly.FromDateTime(DateTime.Today);
        var prescriptions = await SearchLocalPrescriptionsAsync(
            end.AddYears(-10), end, invoiceTypeCode: null,
            includePermanentlyDeleted: false, cancellationToken);
        var patientPrescriptions = prescriptions
            .Where(item => item.PatientTcIdentityNo.Equals(
                patientTcIdentityNo, StringComparison.Ordinal))
            .OrderByDescending(item => ParseListDate(item.LastTransactionDate)
                ?? ParseListDate(item.PrescriptionDate)
                ?? DateOnly.MinValue)
            .ToArray();
        var rows = patientPrescriptions
            .SelectMany(item => (item.LocalSnapshot?.Drugs ?? [])
                .Select(drug => (IReadOnlyList<string>)new[]
                {
                    item.PrescriptionDate,
                    item.LastTransactionDate,
                    item.PrescriptionNumber,
                    item.LocalSnapshot?.Page.FieldValues is { } fields &&
                    fields.TryGetValue("medula:e-prescription-no", out var ePrescription)
                        ? ePrescription
                        : string.Empty,
                    drug.Barcode,
                    drug.Name,
                    drug.Quantity,
                    drug.Report
                }))
            .ToArray();
        var sourceNote = eyeMedications
            ? " Göz ilacı ayrımı arşiv kayıtlarında bulunmadığı için tüm ilaçlar gösteriliyor."
            : string.Empty;
        var message = rows.Length == 0
            ? $"{patientTcIdentityNo} T.C. kimlik numaralı hasta Medula'da doğrulandı; eşitlenmiş Medula reçete arşivinde ilaç kaydı bulunamadı.{sourceNote}"
            : $"Hasta Medula'da doğrulandı; eşitlenmiş Medula reçete arşivindeki {patientPrescriptions.Length} reçeteden {rows.Length} ilaç satırı getirildi.{sourceNote}";
        return new MedulaRemoteScreen(
            true,
            "medication-history",
            "İlaç Geçmişi",
            verification.Url,
            message,
            verification.Fields,
            [],
            [
                new MedulaRemoteTable(
                    "medula:patient-medication-history",
                    ["Reçete Tarihi", "Son İşlem", "Reçete No", "E-Reçete No",
                        "Barkod", "İlaç", "Kutu", "Rapor"],
                    rows,
                    Title: "Medula İlaç Geçmişi")
            ]);
    }

    public async Task<MedulaRemoteScreen> GetPatientMedicationListAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken)
    {
        var screen = await automation.OpenPatientMedicationListFromDailyPrescriptionAsync(
            patientTcIdentityNo, cancellationToken);
        return screen with
        {
            Key = "patient-medication-list",
            Title = "Hasta İlaç Listesi"
        };
    }

    public Task<MedulaRemoteScreen> GetPatientEligibilityAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken)
    {
        var input = PatientInput(patientTcIdentityNo);
        return automation.LookupPatientFromFreshPrescriptionEntryAsync(
            patientTcIdentityNo, input, cancellationToken);
    }

    public async Task<MedulaRemoteScreen> SearchPatientReportsAsync(
        string patientTcIdentityNo,
        bool includeExpired,
        CancellationToken cancellationToken)
    {
        var screen = await automation.OpenPatientReportsFromDailyPrescriptionAsync(
            patientTcIdentityNo, cancellationToken);
        if (!screen.IsOpen || !includeExpired) return screen;
        var action = screen.Actions.FirstOrDefault(item =>
            item.Caption.Contains("Bitmiş raporları", StringComparison.CurrentCultureIgnoreCase));
        return action is null
            ? screen
            : await automation.ExecuteSubScreenActionAsync(
                screen.Key, screen.Title, action.Id, EmptyPayload(), cancellationToken);
    }

    public async Task<MedulaRemoteScreen> GetPatientReportAsync(
        string patientTcIdentityNo,
        string reportNumber,
        CancellationToken cancellationToken)
    {
        var screen = await SearchPatientReportsAsync(
            patientTcIdentityNo, includeExpired: true, cancellationToken: cancellationToken);
        if (!screen.IsOpen) return screen;
        foreach (var table in screen.Tables)
        {
            var rowIndex = table.Rows
                .Select((row, index) => new { row, index })
                .FirstOrDefault(item => item.row.Any(cell =>
                    cell.Contains(reportNumber, StringComparison.CurrentCultureIgnoreCase)))?.index;
            if (rowIndex is null
                || table.RowActionIds is null
                || rowIndex.Value >= table.RowActionIds.Count)
                continue;
            return await automation.ExecuteSubScreenActionAsync(
                screen.Key, "Rapor Detayı", table.RowActionIds[rowIndex.Value],
                EmptyPayload(), cancellationToken);
        }
        return screen with
        {
            IsOpen = false,
            Message = $"{reportNumber} numaralı rapor Medula sonuçlarında bulunamadı."
        };
    }

    public Task<MedulaDrugSearchResult> SearchDrugsAsync(
        string query,
        CancellationToken cancellationToken) =>
        automation.SearchPrescriptionDrugsAsync(
            PatientInput(string.Empty), query, cancellationToken);

    public async Task<MedulaDrugActionResult> GetDrugAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        var input = PatientInput(string.Empty);
        await automation.OpenPrescriptionEntryAsync(cancellationToken);
        var added = await automation.AddPrescriptionDrugBarcodeAsync(input, barcode, cancellationToken);
        if (!added.IsSuccess) return added;
        return await automation.OpenDrugInformationAsync(
            input with { Drugs = added.Drugs }, 0, cancellationToken);
    }

    public async Task<MedulaDrugActionResult> SearchDrugEquivalentsAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        var input = PatientInput(string.Empty);
        await automation.OpenPrescriptionEntryAsync(cancellationToken);
        var added = await automation.AddPrescriptionDrugBarcodeAsync(input, barcode, cancellationToken);
        if (!added.IsSuccess) return added;
        return await automation.OpenDrugEquivalentsAsync(
            input with { Drugs = added.Drugs }, 0, cancellationToken);
    }

    public Task<MedulaPrescriptionPageResult> SearchDoctorAsync(
        string diplomaRegistrationNumber,
        CancellationToken cancellationToken) =>
        automation.SearchDoctorAsync(
            PatientInput(string.Empty) with
            {
                RegistrationNumber = diplomaRegistrationNumber,
                PhysicianType = "1"
            },
            cancellationToken);

    public Task<MedulaDiagnosisSearchResult> SearchDiagnosesAsync(
        string query,
        CancellationToken cancellationToken) =>
        automation.SearchPrescriptionDiagnosesAsync(
            PatientInput(string.Empty), query, cancellationToken);

    public Task<MedulaPrescriptionSnapshot?> GetPrescriptionWarningsAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken) =>
        automation.OpenPrescriptionAsync(locator, cancellationToken);

    public Task<MedulaRemoteScreen> SearchPatientExaminationsAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken) =>
        automation.OpenExaminationsAsync(PatientInput(patientTcIdentityNo), cancellationToken);

    public Task<MedulaRemoteScreen> SearchPatientOffLabelApprovalsAsync(
        string patientTcIdentityNo,
        bool personBased,
        CancellationToken cancellationToken) =>
        automation.OpenOffLabelUseAsync(
            PatientInput(patientTcIdentityNo), personBased, cancellationToken);

    public async Task<MedulaRemoteScreen> GetPatientTreatmentSchemeAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken)
    {
        var snapshot = await automation.OpenPrescriptionAsync(locator, cancellationToken);
        return snapshot is null
            ? new MedulaRemoteScreen(
                false, "treatment-scheme", "Tedavi Şeması", string.Empty,
                "Reçete Medula'da açılamadı.", [], [], [])
            : await automation.OpenTreatmentSchemeAsync(
                InputFromSnapshot(snapshot), cancellationToken);
    }

    private static DateOnly? ParseListDate(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        foreach (var format in new[] { "dd/MM/yyyy", "yyyy-MM-dd", "yyyyMMdd" })
        {
            if (DateOnly.TryParseExact(
                    normalized,
                    format,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
                return parsed;
        }
        return null;
    }

    internal static IReadOnlyList<MedulaPrescriptionListItem> MergePatientPrescriptions(
        IReadOnlyList<MedulaPrescriptionListItem> local,
        IReadOnlyList<MedulaPrescriptionListItem> live)
    {
        var liveByKey = live
            .GroupBy(PrescriptionIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var merged = local.Select(item =>
        {
            var key = PrescriptionIdentity(item);
            if (!liveByKey.Remove(key, out var active))
                return item with { IsLiveOnMedula = false };
            return item with
            {
                IsLiveOnMedula = !item.IsDeleted && !item.IsPermanentlyDeleted,
                IsStoredLocally = true,
                PageNumber = active.PageNumber,
                RowIndex = active.RowIndex,
                MedulaOrderKey = active.MedulaOrderKey
            };
        }).ToList();
        merged.AddRange(liveByKey.Values.Select(item => item with { IsLiveOnMedula = true }));
        return merged
            .GroupBy(PrescriptionIdentity, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.IsLiveOnMedula)
                .ThenByDescending(item => item.IsStoredLocally)
                .ThenByDescending(item => item.VersionNumber)
                .First())
            .OrderByDescending(item => ParseListDate(item.MedulaFilterDate)
                ?? ParseListDate(item.LastTransactionDate)
                ?? ParseListDate(item.PrescriptionDate)
                ?? DateOnly.MinValue)
            .ThenByDescending(item => item.VersionNumber)
            .ToArray();
    }

    private async Task<MedulaPrescriptionListResult> RunPrescriptionQueryWithRecoveryAsync(
        Func<CancellationToken, Task<MedulaPrescriptionListResult>> query,
        CancellationToken cancellationToken)
    {
        var result = await query(cancellationToken);
        if (!NeedsPrescriptionBrowserRecovery(result)) return result;
        var restart = await automation.RestartWebBrowserAsync(
            restorePreviousState: false, cancellationToken);
        return restart.IsSuccess ? await query(cancellationToken) : result;
    }

    private static bool NeedsPrescriptionBrowserRecovery(MedulaPrescriptionListResult result)
    {
        if (result.IsSuccess) return false;
        var diagnostic = string.Join(" | ", result.Message, result.WarningText ?? string.Empty);
        return MedulaSystemErrorDetector.IsSystemError(
                   null, null, diagnostic, hasKnownMedulaControls: false)
               || diagnostic.Contains("denetimleri hazırlanmadı", StringComparison.CurrentCultureIgnoreCase)
               || diagnostic.Contains("geçerli adres:", StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsNoPrescriptionRecord(MedulaPrescriptionListResult result)
    {
        var message = string.Join(" | ", result.Message, result.WarningText ?? string.Empty)
            .ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        return message.Contains("kayıt bulunamad", StringComparison.Ordinal) ||
               message.Contains("kayit bulunamad", StringComparison.Ordinal) ||
               message.Contains("reçete bulunamad", StringComparison.Ordinal) ||
               message.Contains("recete bulunamad", StringComparison.Ordinal);
    }

    private static string PrescriptionIdentity(MedulaPrescriptionListItem item) => string.Join('|',
        item.PrescriptionNumber.Trim().ToUpperInvariant(),
        ParseListDate(item.PrescriptionDate)?.ToString("yyyyMMdd") ?? string.Empty,
        item.PatientTcIdentityNo.Trim());

    private static MedulaRemoteFieldPayload EmptyPayload() =>
        new(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal));

    private static MedulaPrescriptionInput PatientInput(string patientTcIdentityNo)
    {
        var today = DateTime.Today.ToString(
            "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return new MedulaPrescriptionInput(
            patientTcIdentityNo, string.Empty, "0", "1",
            string.Empty, string.Empty, today, today, "1", "1",
            string.Empty, "1", string.Empty, string.Empty, string.Empty, "0",
            false, false, string.Empty, false, [], []);
    }

    private static MedulaPrescriptionInput InputFromSnapshot(MedulaPrescriptionSnapshot snapshot)
    {
        var fields = snapshot.Page.FieldValues
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Field(string key) =>
            fields.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
        return new MedulaPrescriptionInput(
            Field("f:t18"), Field("f:text37"), Field("f:menu4"), Field("f:menu2"),
            Field("f:t24"), Field("f:t33"), Field("f:t29"), Field("f:t31"),
            Field("f:m4"), Field("f:m8"), Field("f:t43"), Field("f:m6"),
            Field("f:t39"), Field("f:t41"), Field("f:t42"), Field("f:m7"),
            bool.TryParse(Field("f:checkbox1"), out var hasQr) && hasQr,
            bool.TryParse(Field("f:checkbox2"), out var sequential) && sequential,
            string.Empty,
            bool.TryParse(Field("f:checkbox4"), out var repeat) && repeat,
            snapshot.Drugs,
            snapshot.Diagnoses,
            snapshot.SourceListItem,
            Field("medula:e-prescription-no"),
            snapshot.IsSavedOnMedula);
    }
}
