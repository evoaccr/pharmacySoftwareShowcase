namespace pharmacyApp.Modules.Medula;

public sealed record LoyalPatientHistorySyncProgress(
    int ProcessedPatients,
    int TotalPatients,
    string PatientName,
    int FailureCount);

public sealed record LoyalPatientHistorySyncResult(
    int TotalPatients,
    int SuccessfulPatients,
    int FailedPatients);

public sealed record LoyalPatientHistoryPatientSyncResult(
    LoyalPatientIdentity Patient,
    int MedicationCount,
    int ReportCount,
    IReadOnlyList<string> UnresolvedMedicationNames);

public sealed class LoyalPatientHistorySyncService
{
    private readonly LoyalPatientRepository repository;
    private readonly MedulaAutomation automation;
    private readonly MedulaRemoteScreenPager pager;

    public LoyalPatientHistorySyncService(
        LoyalPatientRepository? repository = null,
        MedulaAutomation? automation = null)
    {
        this.repository = repository ?? new LoyalPatientRepository();
        this.automation = automation ?? MedulaAutomation.Instance;
        pager = new MedulaRemoteScreenPager(this.automation);
    }

    public async Task<LoyalPatientHistorySyncResult> SynchronizeAllAsync(
        IProgress<LoyalPatientHistorySyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var patients = await repository.LoadLoyalPatientsAsync(cancellationToken);
        var succeeded = 0;
        var failed = 0;
        progress?.Report(new LoyalPatientHistorySyncProgress(0, patients.Count, string.Empty, 0));
        for (var index = 0; index < patients.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patient = patients[index];
            try
            {
                await SynchronizePatientAsync(patient, cancellationToken);
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                await repository.RecordSyncErrorAsync(
                    patient.RecordId, exception.Message, cancellationToken);
            }
            progress?.Report(new LoyalPatientHistorySyncProgress(
                index + 1, patients.Count, patient.DisplayName, failed));
            await Task.Delay(75, cancellationToken);
        }
        return new LoyalPatientHistorySyncResult(patients.Count, succeeded, failed);
    }

    public async Task<LoyalPatientHistoryPatientSyncResult> SynchronizePatientAsync(
        string tcIdentityNo,
        CancellationToken cancellationToken)
    {
        var patient = await repository.FindLoyalPatientAsync(tcIdentityNo, cancellationToken)
            ?? throw new InvalidOperationException(
                "Hasta bulunamadı veya sadık hasta olarak işaretli değil.");
        try
        {
            return await SynchronizePatientAsync(patient, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await repository.RecordSyncErrorAsync(
                patient.RecordId, exception.Message, cancellationToken);
            throw;
        }
    }

    private async Task<LoyalPatientHistoryPatientSyncResult> SynchronizePatientAsync(
        LoyalPatientIdentity patient,
        CancellationToken cancellationToken)
    {
        automation.RequestForegroundPriority();
        var medicationScreen = await automation
            .OpenPatientMedicationListFromDailyPrescriptionAsync(
                patient.TcIdentityNo, cancellationToken);
        if (!medicationScreen.IsOpen)
            throw new InvalidOperationException(medicationScreen.Message);
        medicationScreen = await pager.LoadAllPagesAsync(medicationScreen, cancellationToken);

        var reportScreen = await automation.OpenPatientReportsFromDailyPrescriptionAsync(
            patient.TcIdentityNo, cancellationToken);
        if (!reportScreen.IsOpen)
            throw new InvalidOperationException(reportScreen.Message);
        reportScreen = await pager.LoadAllPagesAsync(reportScreen, cancellationToken);

        var medications = MedulaLoyalPatientHistoryParser.ParseMedications(medicationScreen);
        var reports = MedulaLoyalPatientHistoryParser.ParseReports(reportScreen);
        var replacement = await repository.ReplaceLiveHistoriesAsync(
            patient.RecordId, medications, reports, cancellationToken);
        return new LoyalPatientHistoryPatientSyncResult(
            patient,
            replacement.MedicationCount,
            replacement.ReportCount,
            replacement.UnresolvedMedicationNames);
    }

}
