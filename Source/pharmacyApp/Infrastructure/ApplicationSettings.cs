namespace pharmacyApp.Infrastructure;

public sealed record ApplicationSettings
{
    public bool ShortcutsEnabled { get; init; } = true;
    public bool StartFullscreen { get; init; } = true;
    public int ScreenWidth { get; init; } = DisplayMetrics.PrimaryResolution.Width;
    public int ScreenHeight { get; init; } = DisplayMetrics.PrimaryResolution.Height;
    public string PharmacyName { get; init; } = string.Empty;
    public string PharmacistName { get; init; } = string.Empty;
    public string AiUserDisplayName { get; init; } = string.Empty;
    public string? IgnoredUserId { get; init; }
    public string MedulaUsername { get; init; } = string.Empty;
    public string MedulaPassword { get; init; } = string.Empty;
    public bool MedulaDebug { get; init; } = false;
    public bool MedulaBrowserDebug { get; init; } = false;
    public bool OfflinePrescriptionList { get; init; } = true;
    public bool SkipElectronicPrescriptionView { get; init; } = false;
    public bool SkipPrescriptionSaveConfirmation { get; init; } = false;
    public bool HideUnimportantReportDetails { get; init; } = true;
    public bool HideUnimportantPrescriptionDetails { get; init; } = true;
    public bool AutomaticPrescriptionRecipientSelf { get; init; } = true;
    public int NotificationDisplaySeconds { get; init; } = 10;
    public decimal LoyalPatientMinimumMonthlyFrequency { get; init; } = 1m;
    public int LoyalPatientMinimumDistinctVisitDays { get; init; } = 3;
    public decimal LoyalPatientMinimumObservationIntervals { get; init; } = 1.5m;
    public int LoyalPatientFrequencyTolerancePercent { get; init; } = 55;
    public decimal LoyalPatientRecentVisitGraceIntervals { get; init; } = 4m;
    public int LoyalPatientReportSampleSize { get; init; } = 300;
    public int LoyalPatientReportSupplyThresholdPercent { get; init; } = 70;
    public decimal LoyalPatientReportQueryDelaySeconds { get; init; } = 3m;
    public decimal LoyalPatientReportQueryJitterSeconds { get; init; } = 0.2m;
    public int LoyalPatientMinimumReportEvidence { get; init; } = 3;
    public decimal LoyalPatientHybridScoreThreshold { get; init; } = 60m;
    public decimal LoyalPatientFamilyLoyalBonus { get; init; } = 65m;
    public decimal LoyalPatientFamilyAdditionalLoyalBonus { get; init; } = 5m;
    public decimal LoyalPatientFamilyUnloyalPenalty { get; init; } = 15m;
    public bool LoyalPatientWearOffEnabled { get; init; } = true;
    public int LoyalPatientWearOffDays { get; init; } = 30;
    public IReadOnlyDictionary<string, string> DatabaseTableNames { get; init; } =
        DatabaseTableNameCatalog.Normalize(null);
    public string ItsUsername { get; init; } = string.Empty;
    public string ItsPassword { get; init; } = string.Empty;
    public string ItsPharmacyGln { get; init; } = string.Empty;
    public string EczaciKartUsername { get; init; } = string.Empty;
    public string EczaciKartPassword { get; init; } = string.Empty;
    public bool EczaciKartBrowserDebug { get; init; } = false;
    public string SelcukUsername { get; init; } = string.Empty;
    public string SelcukPassword { get; init; } = string.Empty;
    public string BekUsername { get; init; } = string.Empty;
    public string BekPassword { get; init; } = string.Empty;
    public string IskoopUsername { get; init; } = string.Empty;
    public string IskoopPassword { get; init; } = string.Empty;
    public string AlliancePharmacyCode { get; init; } = string.Empty;
    public string AllianceUsername { get; init; } = string.Empty;
    public string AlliancePassword { get; init; } = string.Empty;
}

public static class ApplicationSettingsStore
{
    private static readonly object SyncRoot = new();
    public static string SettingsFilePath => AppConfiguration.FilePath;

    private static ApplicationSettings current = Load();

    public static event Action<ApplicationSettings>? SettingsChanged;

    public static ApplicationSettings Current
    {
        get
        {
            lock (SyncRoot)
            {
                return current with { };
            }
        }
    }

    public static void Save(ApplicationSettings settings)
    {
        var normalized = Normalize(settings);
        lock (SyncRoot)
        {
            AppConfiguration.WriteSection("Application", normalized);
            current = normalized;
        }

        SettingsChanged?.Invoke(normalized with { });
    }

    private static ApplicationSettings Load()
    {
        var primary = DisplayMetrics.PrimaryResolution;
        return Normalize(AppConfiguration.ReadSection<ApplicationSettings>("Application") with
        {
            ScreenWidth = primary.Width,
            ScreenHeight = primary.Height
        });
    }

    private static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        var primary = DisplayMetrics.PrimaryResolution;
        return settings with
        {
            ScreenWidth = settings.ScreenWidth is >= 800 and <= 7680 ? settings.ScreenWidth : primary.Width,
            ScreenHeight = settings.ScreenHeight is >= 600 and <= 4320 ? settings.ScreenHeight : primary.Height,
            PharmacyName = NormalizeDisplayName(settings.PharmacyName, 160),
            PharmacistName = NormalizeDisplayName(settings.PharmacistName, 120),
            AiUserDisplayName = NormalizeDisplayName(settings.AiUserDisplayName, 120),
            IgnoredUserId = "admin",
            OfflinePrescriptionList = true,
            MedulaUsername = string.Empty,
            MedulaPassword = string.Empty,
            NotificationDisplaySeconds = Math.Clamp(settings.NotificationDisplaySeconds, 1, 120),
            LoyalPatientMinimumMonthlyFrequency = settings.LoyalPatientMinimumMonthlyFrequency is >= 0.1m and <= 12m
                ? settings.LoyalPatientMinimumMonthlyFrequency
                : 1m,
            LoyalPatientMinimumDistinctVisitDays = Math.Clamp(settings.LoyalPatientMinimumDistinctVisitDays, 2, 24),
            LoyalPatientMinimumObservationIntervals = settings.LoyalPatientMinimumObservationIntervals is >= 0.5m and <= 24m
                ? settings.LoyalPatientMinimumObservationIntervals
                : 1.5m,
            LoyalPatientFrequencyTolerancePercent = Math.Clamp(settings.LoyalPatientFrequencyTolerancePercent, 10, 100),
            LoyalPatientRecentVisitGraceIntervals = settings.LoyalPatientRecentVisitGraceIntervals is >= 1m and <= 24m
                ? settings.LoyalPatientRecentVisitGraceIntervals
                : 4m,
            LoyalPatientReportSampleSize = Math.Clamp(settings.LoyalPatientReportSampleSize, 1, 5000),
            LoyalPatientReportSupplyThresholdPercent = Math.Clamp(settings.LoyalPatientReportSupplyThresholdPercent, 0, 100),
            LoyalPatientReportQueryDelaySeconds = settings.LoyalPatientReportQueryDelaySeconds is >= 0m and <= 60m
                ? settings.LoyalPatientReportQueryDelaySeconds
                : 3m,
            LoyalPatientReportQueryJitterSeconds = settings.LoyalPatientReportQueryJitterSeconds is >= 0m and <= 30m
                ? settings.LoyalPatientReportQueryJitterSeconds
                : 0.2m,
            LoyalPatientMinimumReportEvidence = Math.Clamp(settings.LoyalPatientMinimumReportEvidence, 1, 100),
            LoyalPatientHybridScoreThreshold = settings.LoyalPatientHybridScoreThreshold is >= 0m and <= 100m
                ? settings.LoyalPatientHybridScoreThreshold
                : 60m,
            LoyalPatientFamilyLoyalBonus = settings.LoyalPatientFamilyLoyalBonus is >= 0m and <= 100m
                ? settings.LoyalPatientFamilyLoyalBonus
                : 65m,
            LoyalPatientFamilyAdditionalLoyalBonus = settings.LoyalPatientFamilyAdditionalLoyalBonus is >= 0m and <= 100m
                ? settings.LoyalPatientFamilyAdditionalLoyalBonus
                : 5m,
            LoyalPatientFamilyUnloyalPenalty = settings.LoyalPatientFamilyUnloyalPenalty is >= 0m and <= 100m
                ? settings.LoyalPatientFamilyUnloyalPenalty
                : 15m,
            LoyalPatientWearOffDays = Math.Clamp(settings.LoyalPatientWearOffDays, 1, 3650),
            DatabaseTableNames = DatabaseTableNameCatalog.Normalize(settings.DatabaseTableNames),
            ItsUsername = string.Empty,
            ItsPassword = string.Empty,
            ItsPharmacyGln = settings.ItsPharmacyGln?.Trim() ?? string.Empty,
            EczaciKartUsername = string.Empty,
            EczaciKartPassword = string.Empty,
            SelcukUsername = string.Empty,
            SelcukPassword = string.Empty,
            BekUsername = string.Empty,
            BekPassword = string.Empty,
            IskoopUsername = string.Empty,
            IskoopPassword = string.Empty,
            AlliancePharmacyCode = string.Empty,
            AllianceUsername = string.Empty,
            AlliancePassword = string.Empty
        };
    }

    private static string NormalizeDisplayName(string? value, int maximumLength)
    {
        var normalized = string.Join(' ', (value ?? string.Empty).Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength].TrimEnd();
    }
}
