namespace pharmacyApp.Modules.Medula;

internal enum MedulaPrescriptionListRefreshCause
{
    InitialOpen,
    ExplicitRefresh,
    FilterChange,
    LocalViewInteraction
}

internal static class MedulaPrescriptionListRefreshPolicy
{
    public const int DefaultQuickRangeDays = 1;

    public static bool RequiresReachabilityRefresh(MedulaPrescriptionListRefreshCause cause) =>
        cause is MedulaPrescriptionListRefreshCause.InitialOpen
            or MedulaPrescriptionListRefreshCause.ExplicitRefresh
            or MedulaPrescriptionListRefreshCause.FilterChange;

    public static bool ShouldStopAfterEmptyPeriod(int consecutiveEmptyPeriods) =>
        consecutiveEmptyPeriods >= 2;
}
