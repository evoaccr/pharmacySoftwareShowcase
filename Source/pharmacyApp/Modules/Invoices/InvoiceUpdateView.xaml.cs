using System.Windows;
using System.Windows.Controls;
using pharmacyApp.Modules;

namespace pharmacyApp.Modules.Invoices;

public partial class InvoiceUpdateView : UserControl, IModuleBackgroundWorkSource
{
    private readonly InvoiceIncrementalSyncService service = new();
    private CancellationTokenSource? cancellation;
    private bool loaded;

    public bool IsBackgroundWorkRunning => cancellation is not null;
    public event Action<bool>? BackgroundWorkStateChanged;

    public InvoiceUpdateView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        try { UpdateState(await service.LoadStateAsync()); }
        catch (Exception exception) { ShowError($"Takip durumu yüklenemedi: {exception.Message}"); }
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null) return;
        cancellation = new CancellationTokenSource();
        SetRunning(true);
        ErrorPanel.Visibility = Visibility.Collapsed;
        try
        {
            var progress = new Progress<InvoiceIncrementalSyncProgress>(UpdateProgress);
            await service.RunAsync(progress, cancellation.Token);
            UpdateState(await service.LoadStateAsync());
        }
        catch (Exception exception) { ShowError($"Fatura güncelleme tamamlanamadı: {exception.GetBaseException().Message}"); }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            SetRunning(false);
        }
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        MessageText.Text = "Güvenli kontrol noktasında duraklatılıyor…";
        cancellation?.Cancel();
    }

    private void UpdateProgress(InvoiceIncrementalSyncProgress progress)
    {
        UpdateState(progress.State);
        MessageText.Text = progress.Message;
        CurrentTypeText.Text = Empty(progress.CurrentType);
        CurrentInvoiceText.Text = Empty(progress.CurrentInvoice);
    }

    private void UpdateState(InvoiceIncrementalSyncState state)
    {
        LastSuccessText.Text = state.LastSuccessfulCutoff?.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss") ?? "İlk çalışma";
        PhaseText.Text = TurkishPhase(state.Phase);
        ProgressBar.Maximum = Math.Max(1, state.Total);
        ProgressBar.Value = Math.Min(state.Completed, ProgressBar.Maximum);
        ProgressCountText.Text = $"{state.Completed:N0} / {state.Total:N0}";
        ImportedText.Text = $"Aktarılan: {state.Imported:N0}";
        FailedText.Text = $"Hata: {state.Failed:N0}";
        UpdatedText.Text = $"Güncelleme: {state.UpdatedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}";
        if (!string.IsNullOrWhiteSpace(state.CurrentInvoice)) CurrentInvoiceText.Text = state.CurrentInvoice;
        if (!string.IsNullOrWhiteSpace(state.LastError)) ShowError(state.LastError);
        else ErrorPanel.Visibility = Visibility.Collapsed;
        MessageText.Text = state.Phase switch
        {
            "Completed" => "Son çalışma başarıyla tamamlandı.",
            "Paused" => "Kontrol noktası kaydedildi; devam edebilirsiniz.",
            "Faulted" => "Başarısız faturalar takipte; devam ederek yeniden deneyebilirsiniz.",
            "Running" => "Kaydedilmiş aktarım çalışıyor.",
            "Discovering" => "Yeni faturalar aranıyor…",
            _ => "Yeni faturaları aramak için Başlat / Devam Et düğmesine basın."
        };
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        BackgroundWorkStateChanged?.Invoke(running);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string TurkishPhase(string phase) => phase switch
    {
        "Idle" => "Hazır", "Discovering" => "Aranıyor", "Running" => "Çalışıyor",
        "Paused" => "Duraklatıldı", "Faulted" => "Hata", "Completed" => "Tamamlandı", _ => phase
    };
}
