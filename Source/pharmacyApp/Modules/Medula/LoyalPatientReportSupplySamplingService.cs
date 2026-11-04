using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Medula;

public sealed record LoyalPatientReportSupplyProgress(
    int ProcessedPatients,
    int TotalPatients,
    string PatientName,
    int LoyalPatients,
    int FailedPatients);

public sealed class LoyalPatientReportSupplySamplingService
{
    private readonly LoyalPatientReportSupplyRepository repository;
    private readonly MedulaAutomation automation;
    private readonly MedulaRemoteScreenPager pager;

    public LoyalPatientReportSupplySamplingService(
        LoyalPatientReportSupplyRepository? repository = null,
        MedulaAutomation? automation = null)
    {
        this.repository = repository ?? new LoyalPatientReportSupplyRepository();
        this.automation = automation ?? MedulaAutomation.Instance;
        pager = new MedulaRemoteScreenPager(this.automation);
    }

    public async Task<LoyalPatientReportSupplyRunResult> RunAsync(
        LoyalPatientReportSupplyOptions options,
        IProgress<LoyalPatientReportSupplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var population = await repository.LoadBenchmarkSampleAsync(options.SampleSize, cancellationToken);
        var runId = await repository.BeginRunAsync(options, population, cancellationToken);
        var evaluated = 0;
        var failed = 0;
        var withReports = 0;
        var loyal = 0;
        var mechanism1Loyal = 0;
        var mechanism2Loyal = 0;
        var successful = new List<(
            LoyalPatientIdentity Patient,
            LoyalPatientReportSupplyEvaluation Report,
            LoyalPatientEvaluation Frequency)>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var frequencyOptions = new LoyalPatientClassificationOptions(
            ApplicationSettingsStore.Current.LoyalPatientMinimumMonthlyFrequency,
            ApplicationSettingsStore.Current.LoyalPatientMinimumDistinctVisitDays,
            ApplicationSettingsStore.Current.LoyalPatientMinimumObservationIntervals,
            ApplicationSettingsStore.Current.LoyalPatientFrequencyTolerancePercent / 100m,
            ApplicationSettingsStore.Current.LoyalPatientRecentVisitGraceIntervals);
        progress?.Report(new LoyalPatientReportSupplyProgress(0, population.Patients.Count, string.Empty, 0, 0));
        try
        {
            for (var index = 0; index < population.Patients.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index > 0)
                    await Task.Delay(LoyalPatientReportSupplyEvaluator.CalculateDelay(
                        options.QueryDelaySeconds, options.QueryJitterSeconds, Random.Shared.NextDouble()), cancellationToken);
                var patient = population.Patients[index];
                try
                {
                    var known = await repository.LoadKnownPrescriptionNumbersAsync(patient.TcIdentityNo, cancellationToken);
                    var medicationScreen = await automation
                        .OpenPatientMedicationListFromDailyPrescriptionAsync(patient.TcIdentityNo, cancellationToken);
                    if (!medicationScreen.IsOpen) throw new InvalidOperationException(medicationScreen.Message);
                    medicationScreen = await pager.LoadAllPagesAsync(medicationScreen, cancellationToken);
                    var evaluation = LoyalPatientReportSupplyEvaluator.Evaluate(
                        medicationScreen, known, options.LoyaltyThresholdRatio,
                        options.MinimumReportEvidence);
                    var context = await repository.LoadHybridContextAsync(patient.RecordId, cancellationToken);
                    var frequency = LoyalPatientClassifier.Evaluate(
                        context.PrescriptionDates, frequencyOptions, today);
                    var hybrid = LoyalPatientReportSupplyEvaluator.EvaluateHybrid(
                        frequency, evaluation, context, options, today);
                    await repository.RecordEvaluationAsync(
                        runId, patient, evaluation, hybrid, context, options, null, cancellationToken);
                    successful.Add((patient, evaluation, frequency));
                    evaluated++;
                    if (evaluation.TotalReports > 0) withReports++;
                    if (hybrid.Mechanism1IsLoyal) mechanism1Loyal++;
                    if (hybrid.Mechanism2IsLoyal) mechanism2Loyal++;
                    if (hybrid.IsLoyal) loyal++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    failed++;
                    await repository.RecordEvaluationAsync(
                        runId, patient, null, null, null, options,
                        exception.GetBaseException().Message, cancellationToken);
                }
                progress?.Report(new LoyalPatientReportSupplyProgress(
                    index + 1, population.Patients.Count, patient.DisplayName, loyal, failed));
            }

            // Re-evaluate locally after all sampled decisions are persisted. This makes family
            // inheritance independent of random sample order without issuing a second Medula query.
            loyal = 0;
            mechanism1Loyal = 0;
            mechanism2Loyal = 0;
            foreach (var item in successful)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = await repository.LoadHybridContextAsync(
                    item.Patient.RecordId, cancellationToken);
                var hybrid = LoyalPatientReportSupplyEvaluator.EvaluateHybrid(
                    item.Frequency, item.Report, context, options, today);
                await repository.RecordEvaluationAsync(
                    runId, item.Patient, item.Report, hybrid, context, options, null, cancellationToken);
                if (hybrid.Mechanism1IsLoyal) mechanism1Loyal++;
                if (hybrid.Mechanism2IsLoyal) mechanism2Loyal++;
                if (hybrid.IsLoyal) loyal++;
            }
            var result = BuildResult(
                runId, population, evaluated, failed, withReports,
                mechanism1Loyal, mechanism2Loyal, loyal);
            await repository.CompleteRunAsync(result, "COMPLETED", null, cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = BuildResult(
                runId, population, evaluated, failed, withReports,
                mechanism1Loyal, mechanism2Loyal, loyal);
            await repository.CompleteRunAsync(result, "CANCELLED", "User cancelled the sample.", CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var result = BuildResult(
                runId, population, evaluated, failed, withReports,
                mechanism1Loyal, mechanism2Loyal, loyal);
            await repository.CompleteRunAsync(result, "FAILED", exception.GetBaseException().Message, CancellationToken.None);
            throw;
        }
    }

    private static LoyalPatientReportSupplyRunResult BuildResult(
        Guid runId, LoyalPatientSamplePopulation population, int evaluated,
        int failed, int withReports, int mechanism1Loyal,
        int mechanism2Loyal, int mechanism3Loyal)
    {
        static decimal Percentage(int count, int evaluatedCount) =>
            evaluatedCount == 0 ? 0 : decimal.Round((decimal)count / evaluatedCount * 100m, 2);
        var mechanism1Percentage = Percentage(mechanism1Loyal, evaluated);
        var mechanism2Percentage = Percentage(mechanism2Loyal, evaluated);
        var mechanism3Percentage = Percentage(mechanism3Loyal, evaluated);
        return new LoyalPatientReportSupplyRunResult(
            runId, population.TotalPatientCount, population.Patients.Count, evaluated, failed,
            withReports, mechanism3Loyal, mechanism3Percentage,
            LoyalPatientReportSupplyEvaluator.EstimatePopulationCount(
                mechanism3Loyal, evaluated, population.TotalPatientCount),
            mechanism1Loyal, mechanism1Percentage,
            LoyalPatientReportSupplyEvaluator.EstimatePopulationCount(
                mechanism1Loyal, evaluated, population.TotalPatientCount),
            mechanism2Loyal, mechanism2Percentage,
            LoyalPatientReportSupplyEvaluator.EstimatePopulationCount(
                mechanism2Loyal, evaluated, population.TotalPatientCount));
    }

    private static void Validate(LoyalPatientReportSupplyOptions options)
    {
        if (options.SampleSize is < 1 or > 5000
            || options.LoyaltyThresholdRatio is < 0 or > 1
            || options.QueryDelaySeconds is < 0 or > 60
            || options.QueryJitterSeconds is < 0 or > 30
            || options.MinimumReportEvidence is < 1 or > 100
            || options.HybridScoreThreshold is < 0 or > 100
            || options.FamilyLoyalBonus is < 0 or > 100
            || options.FamilyAdditionalLoyalBonus is < 0 or > 100
            || options.FamilyUnloyalPenalty is < 0 or > 100
            || options.WearOffDays is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(options), "Report-supply sample settings are invalid.");
    }
}
