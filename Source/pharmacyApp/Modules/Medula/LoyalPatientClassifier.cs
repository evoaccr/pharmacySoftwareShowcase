namespace pharmacyApp.Modules.Medula;

public sealed record LoyalPatientEvaluation(
    bool IsLoyal,
    int UniqueVisitDays,
    DateOnly? FirstVisit,
    DateOnly? LastVisit,
    decimal ObservedMonthlyFrequency,
    decimal ContinuityRatio,
    string Reason);

public sealed record LoyalPatientClassificationOptions(
    decimal MinimumMonthlyFrequency = 1m,
    int MinimumDistinctVisitDays = 3,
    decimal MinimumObservationIntervals = 1.5m,
    decimal FrequencyAcceptanceRatio = 0.55m,
    decimal RecentVisitGraceIntervals = 4m);

public static class LoyalPatientClassifier
{
    private const double DaysPerMonth = 365.2425 / 12d;

    public static LoyalPatientEvaluation Evaluate(
        IEnumerable<DateOnly> prescriptionDates,
        decimal minimumMonthlyFrequency,
        DateOnly today) => Evaluate(
            prescriptionDates,
            new LoyalPatientClassificationOptions(minimumMonthlyFrequency),
            today);

    public static LoyalPatientEvaluation Evaluate(
        IEnumerable<DateOnly> prescriptionDates,
        LoyalPatientClassificationOptions options,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinimumMonthlyFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Monthly frequency must be positive.");
        if (options.MinimumDistinctVisitDays < 2
            || options.MinimumObservationIntervals <= 0
            || options.FrequencyAcceptanceRatio is <= 0 or > 1
            || options.RecentVisitGraceIntervals <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Loyal-patient thresholds are invalid.");

        var visits = prescriptionDates.Distinct().OrderBy(date => date).ToArray();
        if (visits.Length < options.MinimumDistinctVisitDays)
            return Result(false, visits, 0, 0,
                $"En az {options.MinimumDistinctVisitDays} farklı ziyaret günü gerekir.");

        var expectedGapDays = DaysPerMonth / (double)options.MinimumMonthlyFrequency;
        var activeSpanDays = visits[^1].DayNumber - visits[0].DayNumber;
        var minimumEvidenceSpan = expectedGapDays * (double)options.MinimumObservationIntervals;
        if (activeSpanDays < minimumEvidenceSpan)
            return Result(false, visits, 0, 0, "Kısa süreli yoğun reçete hareketi sadakat sayılmaz.");

        // Add one expected interval to the observed span. This prevents three tightly grouped
        // visits from looking artificially frequent while avoiding a whole-history attendance grid.
        var exposureMonths = (activeSpanDays + expectedGapDays) / DaysPerMonth;
        var observedFrequency = visits.Length / Math.Max(1d, exposureMonths);
        var requiredFrequency = (double)(
            options.MinimumMonthlyFrequency * options.FrequencyAcceptanceRatio);
        var frequencyRatio = Math.Min(1d, observedFrequency / requiredFrequency);
        var recentGap = today.DayNumber - visits[^1].DayNumber;
        if (recentGap > expectedGapDays * (double)options.RecentVisitGraceIntervals)
            return Result(false, visits, observedFrequency, frequencyRatio, "Son ziyaret sadakat aralığının dışında.");

        if (observedFrequency + 0.0001d < requiredFrequency)
            return Result(false, visits, observedFrequency, frequencyRatio,
                "Ortalama reçete sıklığı ayarlanan sadakat eşiğinin altında.");

        return Result(true, visits, observedFrequency, frequencyRatio,
            "Ortalama reçete sıklığı ve güncellik sadakat eşiğini karşılıyor.");
    }

    private static LoyalPatientEvaluation Result(
        bool isLoyal,
        IReadOnlyList<DateOnly> visits,
        double observedFrequency,
        double continuity,
        string reason) =>
        new(
            isLoyal,
            visits.Count,
            visits.Count == 0 ? null : visits[0],
            visits.Count == 0 ? null : visits[^1],
            decimal.Round((decimal)observedFrequency, 3),
            decimal.Round((decimal)continuity, 3),
            reason);
}

public sealed record LoyalPatientStrikeDecision(bool IsLoyal, int StrikeCount, bool Removed);

public static class LoyalPatientStrikePolicy
{
    public const int RemovalThreshold = 3;

    public static LoyalPatientStrikeDecision Apply(
        bool wasLoyal,
        int currentStrikeCount,
        bool qualifiesNow)
    {
        if (qualifiesNow)
            return new LoyalPatientStrikeDecision(true, 0, false);
        if (!wasLoyal)
            return new LoyalPatientStrikeDecision(false, 0, false);

        var strikes = Math.Min(RemovalThreshold, Math.Max(0, currentStrikeCount) + 1);
        return new LoyalPatientStrikeDecision(
            strikes < RemovalThreshold,
            strikes,
            strikes >= RemovalThreshold);
    }
}
