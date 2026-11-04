using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Medula;

public sealed record LoyalPatientReportSupplyOptions(
    int SampleSize = 300,
    decimal LoyaltyThresholdRatio = 0.70m,
    decimal QueryDelaySeconds = 3m,
    decimal QueryJitterSeconds = 0.2m,
    int MinimumReportEvidence = 3,
    decimal HybridScoreThreshold = 60m,
    decimal FamilyLoyalBonus = 65m,
    decimal FamilyAdditionalLoyalBonus = 5m,
    decimal FamilyUnloyalPenalty = 15m,
    bool WearOffEnabled = true,
    int WearOffDays = 30);

public sealed record LoyalPatientReportSupplyEvaluation(
    int TotalReports,
    int SuppliedReports,
    decimal SupplyRatio,
    bool IsLoyal,
    IReadOnlyList<string> LinkedPrescriptionNumbers,
    bool HasReportEvidence = false,
    bool IsDecisive = false);

public sealed record LoyalPatientHybridContext(
    IReadOnlyList<DateOnly> PrescriptionDates,
    int LoyalFamilyMemberCount,
    int ExplicitlyUnloyalFamilyMemberCount,
    bool WasPreviouslyLoyal,
    bool PreviouslyQualifiedByReport,
    DateTimeOffset? UnloyalSince);

public sealed record LoyalPatientHybridEvaluation(
    bool IsLoyal,
    bool IsReportDecision,
    decimal FrequencyScore,
    decimal ReportScore,
    decimal FamilyScore,
    decimal TotalScore,
    string Reason,
    bool Mechanism1IsLoyal,
    bool Mechanism2IsLoyal);

public static partial class LoyalPatientReportSupplyEvaluator
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRegex();
    [GeneratedRegex(@"(?<!\d)\d{6,}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ReportNumberRegex();

    public static LoyalPatientReportSupplyEvaluation Evaluate(
        MedulaRemoteScreen medicationHistoryScreen,
        IEnumerable<string> pharmacyPrescriptionNumbers,
        decimal thresholdRatio,
        int minimumReportEvidence = 3)
    {
        if (thresholdRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(thresholdRatio));
        if (minimumReportEvidence < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumReportEvidence));

        var known = pharmacyPrescriptionNumbers.Select(Normalize)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var table = FindMedicationTable(medicationHistoryScreen);
        if (table is null)
            return Empty();

        var evidence = table.Rows
            .Select(row => new MedicationReportEvidence(
                Normalize(MedulaRemoteScreenRefiner.FindColumnValue(table.Headers, row, "Reçete No")),
                NormalizeReportReference(FirstValue(table, row,
                    "Rap. Teşhisi/Rap. Tak. No", "Rap. Teşhisi", "Rap. Tak. No"))))
            .Where(row => row.ReportReference.Length > 0)
            .GroupBy(row => row.ReportReference, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var linked = evidence
            .SelectMany(group => group.Where(row => known.Contains(row.PrescriptionNo)))
            .Select(row => row.PrescriptionNo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var supplied = evidence.Count(group => group.Any(row => known.Contains(row.PrescriptionNo)));
        var total = evidence.Length;
        var ratio = total == 0 ? 0 : decimal.Round((decimal)supplied / total, 6);
        var decisive = total >= minimumReportEvidence;
        return new LoyalPatientReportSupplyEvaluation(
            total, supplied, ratio, total > 0 && ratio > thresholdRatio, linked,
            total > 0, decisive);
    }

    public static LoyalPatientHybridEvaluation EvaluateHybrid(
        LoyalPatientEvaluation frequency,
        LoyalPatientReportSupplyEvaluation report,
        LoyalPatientHybridContext context,
        LoyalPatientReportSupplyOptions options,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(frequency);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (!report.HasReportEvidence && context.PreviouslyQualifiedByReport)
            return new LoyalPatientHybridEvaluation(
                true, true, 0, 100, 0, 100,
                "Daha önce ilaç geçmişindeki rapor kanıtıyla sadık olarak doğrulandı; kanıt artık görünmese de durum korundu.",
                frequency.IsLoyal, false);

        // Mechanism 3 is deliberately a superset: a patient accepted by Mechanism 2
        // cannot be rejected by the hybrid mechanism. Mechanism 1/family scoring is
        // the fallback only when Mechanism 2 did not qualify the patient.
        if (report.IsLoyal)
            return new LoyalPatientHybridEvaluation(
                true, true, 0, report.SupplyRatio * 100m, 0,
                report.SupplyRatio * 100m,
                "İlaç geçmişindeki rapor karşılama oranı Mekanizma 2 eşiğinin üzerinde.",
                frequency.IsLoyal, true);

        var frequencyScore = CalculateFrequencyScore(frequency, today);
        var reportScore = report.IsDecisive ? 0m : CalculateSparseReportScore(report);
        var familyScore = CalculateFamilyScore(context, options);
        var score = decimal.Round(Math.Clamp(Math.Max(frequencyScore, reportScore) + familyScore, 0m, 100m), 2);
        var loyal = score >= options.HybridScoreThreshold;
        var reason = report.HasReportEvidence
            ? "Rapor kanıtı karar için az olduğundan reçete sıklığı, güven ağırlıklı rapor oranı ve aile kararı birlikte değerlendirildi."
            : "Rapor kanıtı olmadığından reçete sıklığı ve ailede kesinleşmiş sadakat kararları birlikte değerlendirildi.";
        return new LoyalPatientHybridEvaluation(
            loyal, false, frequencyScore, reportScore, familyScore, score, reason,
            frequency.IsLoyal, false);
    }

    public static TimeSpan CalculateDelay(decimal delaySeconds, decimal jitterSeconds, double randomUnitValue)
    {
        var boundedRandom = Math.Clamp(randomUnitValue, 0d, 1d);
        var jitter = Math.Max(0m, jitterSeconds);
        var seconds = Math.Max(0m, delaySeconds) + (((decimal)boundedRandom * 2m) - 1m) * jitter;
        return TimeSpan.FromSeconds((double)Math.Max(0m, seconds));
    }

    public static int EstimatePopulationCount(int loyalSampleCount, int evaluatedPatientCount, int totalPatientCount) =>
        evaluatedPatientCount <= 0 || totalPatientCount <= 0 ? 0 : (int)Math.Round(
            (double)loyalSampleCount / evaluatedPatientCount * totalPatientCount,
            MidpointRounding.AwayFromZero);

    private static decimal CalculateFrequencyScore(LoyalPatientEvaluation frequency, DateOnly today)
    {
        if (frequency.IsLoyal) return 70m;
        if (frequency.UniqueVisitDays == 0) return 0m;

        var repeatConfidence = Math.Clamp((frequency.UniqueVisitDays - 1m) / 4m, 0m, 1m);
        var visitEvidence = Math.Min(15m, frequency.UniqueVisitDays * 3m);
        var frequencyFit = 30m * Math.Clamp(frequency.ContinuityRatio, 0m, 1m) * repeatConfidence;
        var spanMonths = frequency.FirstVisit is null || frequency.LastVisit is null
            ? 0m
            : (decimal)Math.Max(0, frequency.LastVisit.Value.DayNumber - frequency.FirstVisit.Value.DayNumber) / 30.436875m;
        var longevity = Math.Min(15m, spanMonths / 12m * 15m) * repeatConfidence;
        var daysSinceLast = frequency.LastVisit is null
            ? 365
            : Math.Max(0, today.DayNumber - frequency.LastVisit.Value.DayNumber);
        var recency = 10m * Math.Max(0m, 1m - daysSinceLast / 180m) * repeatConfidence;
        return decimal.Round(Math.Min(70m, visitEvidence + frequencyFit + longevity + recency), 2);
    }

    private static decimal CalculateSparseReportScore(LoyalPatientReportSupplyEvaluation report)
    {
        if (!report.HasReportEvidence) return 0m;
        var confidence = report.TotalReports / (report.TotalReports + 3m);
        return decimal.Round(report.SupplyRatio * 100m * confidence, 2);
    }

    private static decimal CalculateFamilyScore(
        LoyalPatientHybridContext context,
        LoyalPatientReportSupplyOptions options)
    {
        var positive = context.LoyalFamilyMemberCount <= 0 ? 0m
            : options.FamilyLoyalBonus
              + Math.Max(0, context.LoyalFamilyMemberCount - 1) * options.FamilyAdditionalLoyalBonus;
        positive = Math.Min(75m, positive);
        var negative = Math.Min(45m,
            context.ExplicitlyUnloyalFamilyMemberCount * options.FamilyUnloyalPenalty);
        var score = positive - negative;
        // A confirmed loyal household member is sufficient for a child/dependent with no
        // personal evidence. Negative family evidence still affects patients who have history,
        // but cannot erase this explicit household-inheritance rule.
        return context.LoyalFamilyMemberCount > 0
            ? Math.Max(options.HybridScoreThreshold, score)
            : score;
    }

    private static MedulaRemoteTable? FindMedicationTable(MedulaRemoteScreen screen) =>
        screen.Tables.FirstOrDefault(candidate =>
            candidate.Title?.Contains("İlaç", StringComparison.CurrentCultureIgnoreCase) == true
            || candidate.Headers.Any(header => header.Contains(
                "Verilebileceği", StringComparison.CurrentCultureIgnoreCase)));

    private static string FirstValue(
        MedulaRemoteTable table,
        IReadOnlyList<string> row,
        params string[] headers)
    {
        foreach (var header in headers)
        {
            var value = MedulaRemoteScreenRefiner.FindColumnValue(table.Headers, row, header);
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static string NormalizeReportReference(string? value)
    {
        var normalized = WhiteSpaceRegex().Replace((value ?? string.Empty).Trim(), " ")
            .Trim('-', '/', ':', ';', ' ')
            .ToUpperInvariant();
        if (normalized.Length == 0
            || normalized is "-" or "YOK"
            || normalized.Contains("RAPORSUZ", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var reportNumbers = ReportNumberRegex().Matches(normalized)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return reportNumbers.Length == 0 ? string.Empty : string.Join('|', reportNumbers);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static LoyalPatientReportSupplyEvaluation Empty() => new(0, 0, 0, false, [], false, false);
    private sealed record MedicationReportEvidence(string PrescriptionNo, string ReportReference);
}
