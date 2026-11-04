using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules;

namespace pharmacyApp.Modules.Medula;

public partial class LoyalPatientUpdateView : UserControl, IModuleBackgroundWorkSource
{
    private readonly LoyalPatientRepository repository = new();
    private readonly LoyalPatientHistorySyncService historySyncService = new();
    private readonly LoyalPatientReportSupplySamplingService reportSamplingService = new();
    private CancellationTokenSource? operationCancellation;

    public LoyalPatientUpdateView()
    {
        InitializeComponent();
        LoadSettings();
    }
    public bool IsBackgroundWorkRunning => operationCancellation is not null;
    public event Action<bool>? BackgroundWorkStateChanged;

    private async void OnUpdatePatientsClicked(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            ShowStatus("Reçeteler değerlendiriliyor…", false);
            var progress = new Progress<LoyalPatientClassificationProgress>(item =>
            {
                ClassificationProgressBar.Maximum = Math.Max(1, item.TotalPrescriptions);
                ClassificationProgressBar.Value = Math.Min(item.ProcessedPrescriptions, ClassificationProgressBar.Maximum);
                ClassificationCountText.Text = $"{item.ProcessedPrescriptions:N0} / {item.TotalPrescriptions:N0}";
            });
            if (!TrySaveSettings(out var options, out _)) return;
            var result = await repository.UpdateAllStatusesAsync(options, progress, operationCancellation!.Token);
            ClassificationMessageText.Text = $"{result.PatientCount:N0} hasta içinden {result.LoyalPatientCount:N0} sadık hasta bulundu. {result.LocalPrescriptionCount:N0} yerel ve {result.MedulaImportedPrescriptionCount:N0} Medula reçetesi işlendi.";
            ShowStatus("Sadık hasta durumları güncellendi; yeni sadık hastaların Medula geçmişi eşitleniyor…", false);
            var history = await SynchronizeAllHistoriesAsync(operationCancellation.Token);
            ShowStatus(
                history.FailedPatients == 0
                    ? "Sadık hasta durumları ve kayıt geçmişleri güncellendi."
                    : $"Sadakat güncellendi; {history.FailedPatients:N0} hastanın Medula geçmişi tamamlanamadı.",
                history.FailedPatients > 0);
        }
        catch (OperationCanceledException) { ShowStatus("İşlem durduruldu.", false); }
        catch (Exception exception) { ShowStatus($"Sadık hastalar güncellenemedi: {exception.GetBaseException().Message}", true); }
        finally { EndOperation(); }
    }

    private void OnSaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (TrySaveSettings(out _, out _))
            ShowStatus("Mekanizma 1, 2 ve 3 ayarları kaydedildi.", false);
    }

    private void LoadSettings()
    {
        var settings = ApplicationSettingsStore.Current;
        MonthlyFrequencyBox.Text = Format(settings.LoyalPatientMinimumMonthlyFrequency);
        MinimumVisitDaysBox.Text = settings.LoyalPatientMinimumDistinctVisitDays.ToString();
        MinimumObservationIntervalsBox.Text = Format(settings.LoyalPatientMinimumObservationIntervals);
        FrequencyTolerancePercentBox.Text = settings.LoyalPatientFrequencyTolerancePercent.ToString();
        RecentGraceIntervalsBox.Text = Format(settings.LoyalPatientRecentVisitGraceIntervals);
        ReportSampleSizeBox.Text = settings.LoyalPatientReportSampleSize.ToString();
        ReportThresholdPercentBox.Text = settings.LoyalPatientReportSupplyThresholdPercent.ToString();
        ReportDelaySecondsBox.Text = Format(settings.LoyalPatientReportQueryDelaySeconds);
        ReportJitterSecondsBox.Text = Format(settings.LoyalPatientReportQueryJitterSeconds);
        MinimumReportEvidenceBox.Text = settings.LoyalPatientMinimumReportEvidence.ToString();
        HybridScoreThresholdBox.Text = Format(settings.LoyalPatientHybridScoreThreshold);
        FamilyLoyalBonusBox.Text = Format(settings.LoyalPatientFamilyLoyalBonus);
        FamilyAdditionalLoyalBonusBox.Text = Format(settings.LoyalPatientFamilyAdditionalLoyalBonus);
        FamilyUnloyalPenaltyBox.Text = Format(settings.LoyalPatientFamilyUnloyalPenalty);
        WearOffEnabledBox.IsChecked = settings.LoyalPatientWearOffEnabled;
        WearOffDaysBox.Text = settings.LoyalPatientWearOffDays.ToString();
    }

    private bool TrySaveSettings(
        out LoyalPatientClassificationOptions options,
        out LoyalPatientReportSupplyOptions reportOptions)
    {
        options = new LoyalPatientClassificationOptions();
        reportOptions = new LoyalPatientReportSupplyOptions();
        if (!TryDecimal(MonthlyFrequencyBox.Text, 0.1m, 12m, out var frequency)
            || !int.TryParse(MinimumVisitDaysBox.Text.Trim(), out var visitDays) || visitDays is < 2 or > 24
            || !TryDecimal(MinimumObservationIntervalsBox.Text, 0.5m, 24m, out var observationIntervals)
            || !int.TryParse(FrequencyTolerancePercentBox.Text.Trim(), out var tolerancePercent) || tolerancePercent is < 10 or > 100
            || !TryDecimal(RecentGraceIntervalsBox.Text, 1m, 24m, out var graceIntervals))
        {
            ShowStatus("Ayarlar geçersiz. Sıklık 0,1–12; ziyaret 2–24; gözlem 0,5–24; tolerans %10–100; güncellik 1–24 olmalıdır.", true);
            return false;
        }
        if (!int.TryParse(ReportSampleSizeBox.Text.Trim(), out var sampleSize) || sampleSize is < 1 or > 5000
            || !int.TryParse(ReportThresholdPercentBox.Text.Trim(), out var reportThreshold) || reportThreshold is < 0 or > 100
            || !TryDecimal(ReportDelaySecondsBox.Text, 0m, 60m, out var reportDelay)
            || !TryDecimal(ReportJitterSecondsBox.Text, 0m, 30m, out var reportJitter)
            || !int.TryParse(MinimumReportEvidenceBox.Text.Trim(), out var minimumReportEvidence)
            || minimumReportEvidence is < 1 or > 100
            || !TryDecimal(HybridScoreThresholdBox.Text, 0m, 100m, out var hybridThreshold)
            || !TryDecimal(FamilyLoyalBonusBox.Text, 0m, 100m, out var familyLoyalBonus)
            || !TryDecimal(FamilyAdditionalLoyalBonusBox.Text, 0m, 100m, out var familyAdditionalBonus)
            || !TryDecimal(FamilyUnloyalPenaltyBox.Text, 0m, 100m, out var familyUnloyalPenalty)
            || !int.TryParse(WearOffDaysBox.Text.Trim(), out var wearOffDays)
            || wearOffDays is < 1 or > 3650)
        {
            ShowStatus("Hibrit örneklem ayarları geçersiz. Örnek 1–5000, rapor kanıtı 1–100, puanlar 0–100 ve aşınma 1–3650 gün olmalıdır.", true);
            return false;
        }

        var current = ApplicationSettingsStore.Current;
        ApplicationSettingsStore.Save(current with
        {
            LoyalPatientMinimumMonthlyFrequency = frequency,
            LoyalPatientMinimumDistinctVisitDays = visitDays,
            LoyalPatientMinimumObservationIntervals = observationIntervals,
            LoyalPatientFrequencyTolerancePercent = tolerancePercent,
            LoyalPatientRecentVisitGraceIntervals = graceIntervals,
            LoyalPatientReportSampleSize = sampleSize,
            LoyalPatientReportSupplyThresholdPercent = reportThreshold,
            LoyalPatientReportQueryDelaySeconds = reportDelay,
            LoyalPatientReportQueryJitterSeconds = reportJitter,
            LoyalPatientMinimumReportEvidence = minimumReportEvidence,
            LoyalPatientHybridScoreThreshold = hybridThreshold,
            LoyalPatientFamilyLoyalBonus = familyLoyalBonus,
            LoyalPatientFamilyAdditionalLoyalBonus = familyAdditionalBonus,
            LoyalPatientFamilyUnloyalPenalty = familyUnloyalPenalty,
            LoyalPatientWearOffEnabled = WearOffEnabledBox.IsChecked == true,
            LoyalPatientWearOffDays = wearOffDays
        });
        options = new LoyalPatientClassificationOptions(
            frequency, visitDays, observationIntervals, tolerancePercent / 100m, graceIntervals);
        reportOptions = new LoyalPatientReportSupplyOptions(
            sampleSize, reportThreshold / 100m, reportDelay, reportJitter,
            minimumReportEvidence, hybridThreshold, familyLoyalBonus,
            familyAdditionalBonus, familyUnloyalPenalty,
            WearOffEnabledBox.IsChecked == true, wearOffDays);
        return true;
    }

    private static bool TryDecimal(string text, decimal minimum, decimal maximum, out decimal value) =>
        decimal.TryParse(
            text.Trim().Replace(',', '.'),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out value)
        && value >= minimum && value <= maximum;

    private static string Format(decimal value) => value.ToString(
        "0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

    private async void OnUpdateHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            ShowStatus("Sadık hastaların Medula kayıtları eşitleniyor…", false);
            var result = await SynchronizeAllHistoriesAsync(operationCancellation!.Token);
            HistoryMessageText.Text = $"{result.SuccessfulPatients:N0} hasta eşitlendi; {result.FailedPatients:N0} hasta tamamlanamadı.";
            ShowStatus(result.FailedPatients == 0 ? "Sadık hasta kayıt verileri güncellendi." : "Eşitleme tamamlandı; hatalı hastalar profil kaydında işaretlendi.", result.FailedPatients > 0);
        }
        catch (OperationCanceledException) { ShowStatus("İşlem durduruldu.", false); }
        catch (Exception exception) { ShowStatus($"Kayıt verileri güncellenemedi: {exception.GetBaseException().Message}", true); }
        finally { EndOperation(); }
    }

    private async void OnSingleHistoryClicked(object sender, RoutedEventArgs e)
    {
        var tc = SinglePatientTcBox.Text.Trim();
        if (tc.Length != 11 || tc.Any(character => !char.IsDigit(character)))
        {
            ShowStatus("Geçerli 11 haneli bir T.C. Kimlik No giriniz.", true);
            return;
        }
        if (!BeginOperation()) return;
        try
        {
            SingleHistoryMessageText.Text = $"{tc} için Medula geçmişi eşitleniyor…";
            ShowStatus("Tek hastanın ilaç ve rapor geçmişi içe aktarılıyor…", false);
            var result = await historySyncService.SynchronizePatientAsync(
                tc, operationCancellation!.Token);
            SingleHistoryMessageText.Text =
                $"{result.Patient.DisplayName}: {result.MedicationCount:N0} ilaç ve {result.ReportCount:N0} aktif rapor içe aktarıldı."
                + (result.UnresolvedMedicationNames.Count == 0
                    ? string.Empty
                    : $" {result.UnresolvedMedicationNames.Count:N0} ilaç barkodu güvenli biçimde çözülemedi.");
            ShowStatus(
                result.UnresolvedMedicationNames.Count == 0
                    ? "Hasta geçmişi başarıyla güncellendi."
                    : "Hasta geçmişi kısmen güncellendi; çözülemeyen ilaçlar profil hatasında listelendi.",
                result.UnresolvedMedicationNames.Count > 0);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("İşlem durduruldu.", false);
        }
        catch (Exception exception)
        {
            ShowStatus($"Hasta geçmişi içe aktarılamadı: {exception.GetBaseException().Message}", true);
        }
        finally
        {
            EndOperation();
        }
    }

    private Task<LoyalPatientHistorySyncResult> SynchronizeAllHistoriesAsync(
        CancellationToken cancellationToken)
    {
        var progress = new Progress<LoyalPatientHistorySyncProgress>(item =>
        {
            HistoryProgressBar.Maximum = Math.Max(1, item.TotalPatients);
            HistoryProgressBar.Value = Math.Min(item.ProcessedPatients, HistoryProgressBar.Maximum);
            HistoryCountText.Text = $"{item.ProcessedPatients:N0} / {item.TotalPatients:N0}";
            HistoryMessageText.Text = string.IsNullOrWhiteSpace(item.PatientName)
                ? "Sadık hasta listesi hazırlanıyor…"
                : $"{item.PatientName} işlendi · Hata: {item.FailureCount:N0}";
        });
        return historySyncService.SynchronizeAllAsync(progress, cancellationToken);
    }

    private async void OnRunReportSampleClicked(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            if (!TrySaveSettings(out _, out var options)) return;
            ShowStatus("Sabit karşılaştırma hasta grubu hazırlanıyor…", false);
            var progress = new Progress<LoyalPatientReportSupplyProgress>(item =>
            {
                ReportSampleProgressBar.Maximum = Math.Max(1, item.TotalPatients);
                ReportSampleProgressBar.Value = Math.Min(item.ProcessedPatients, ReportSampleProgressBar.Maximum);
                ReportSampleCountText.Text = $"{item.ProcessedPatients:N0} / {item.TotalPatients:N0}";
                if (!string.IsNullOrWhiteSpace(item.PatientName))
                    ReportSampleMessageText.Text = $"{item.PatientName} işlendi · Sadık: {item.LoyalPatients:N0} · Hata: {item.FailedPatients:N0}";
            });
            var result = await reportSamplingService.RunAsync(
                options, progress, operationCancellation!.Token);
            ReportSampleMessageText.Text =
                $"Aynı {result.EvaluatedPatientCount:N0} hastada: " +
                $"M1 {result.Mechanism1LoyalPatientCount:N0} (%{result.Mechanism1LoyalPercentage:0.##}, tahmin {result.Mechanism1EstimatedLoyalPatientCount:N0}); " +
                $"M2 {result.Mechanism2LoyalPatientCount:N0} (%{result.Mechanism2LoyalPercentage:0.##}, tahmin {result.Mechanism2EstimatedLoyalPatientCount:N0}); " +
                $"M3 {result.LoyalPatientCount:N0} (%{result.LoyalPercentage:0.##}, tahmin {result.EstimatedLoyalPatientCount:N0}). " +
                $"İlaç geçmişinde rapor kanıtı olan: {result.PatientsWithReports:N0}; hata: {result.FailedPatientCount:N0}.";
            ShowStatus("Sadakat karşılaştırması tamamlandı; sadık hasta geçmişleri eşitleniyor…", false);
            var history = await SynchronizeAllHistoriesAsync(operationCancellation.Token);
            ShowStatus(
                $"Mekanizma 1, 2 ve 3 karşılaştırıldı; {history.SuccessfulPatients:N0} sadık hasta geçmişi eşitlendi"
                + (history.FailedPatients > 0
                    ? $", {history.FailedPatients:N0} hasta tamamlanamadı."
                    : "."),
                result.FailedPatientCount > 0 || history.FailedPatients > 0);
        }
        catch (OperationCanceledException) { ShowStatus("Hibrit örneklem durduruldu.", false); }
        catch (Exception exception) { ShowStatus($"Hibrit örneklem çalıştırılamadı: {exception.GetBaseException().Message}", true); }
        finally { EndOperation(); }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; StatusText.Text = "Güvenli adımda durduruluyor…"; operationCancellation?.Cancel(); }
    private bool BeginOperation()
    {
        if (operationCancellation is not null) return false;
        operationCancellation = new CancellationTokenSource();
        UpdatePatientsButton.IsEnabled = false; UpdateHistoryButton.IsEnabled = false; RunReportSampleButton.IsEnabled = false; SingleHistoryButton.IsEnabled = false; CancelButton.IsEnabled = true;
        BackgroundWorkStateChanged?.Invoke(true); return true;
    }
    private void EndOperation()
    {
        operationCancellation?.Dispose(); operationCancellation = null;
        UpdatePatientsButton.IsEnabled = true; UpdateHistoryButton.IsEnabled = true; RunReportSampleButton.IsEnabled = true; SingleHistoryButton.IsEnabled = true; CancelButton.IsEnabled = false;
        BackgroundWorkStateChanged?.Invoke(false);
    }
    private void ShowStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(153, 45, 45) : Color.FromRgb(35, 103, 82));
        StatusPanel.Visibility = Visibility.Visible;
    }
}
