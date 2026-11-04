using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Medula;

public enum MedulaConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    TemporarilyOffline,
    Failed
}

public sealed record MedulaConnectionStatus(
    MedulaConnectionState State,
    string Message,
    DateTimeOffset Timestamp);

public enum MedulaMessageScope
{
    Prescription,
    PrescriptionList,
    ElectronicPrescription
}

public sealed record MedulaScopedText(string Text, MedulaMessageScope Scope);

public sealed class MedulaConnectionService
{
    public const string LoginUrl = "https://medeczane.sgk.gov.tr/eczane/faces/login.jsp";
    private const string HomeUrl = "https://medeczane.sgk.gov.tr/eczane/faces/index.jsp";
    private const string PrescriptionInfoUrl =
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteBilgi.jsp";
    private const string WarningBridgePrefix = "__PHARMACY_MEDULA_WARNING__";
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImageReaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim prescriptionGate = new(1, 1);
    private readonly SemaphoreSlim systemErrorRecoveryGate = new(1, 1);
    private readonly object warningSync = new();
    private readonly object messageScopeSync = new();
    private Window? browserWindow;
    private WebView2? browser;
    private Window? prescriptionDocumentWindow;
    private WebView2? prescriptionDocumentBrowser;
    private bool prescriptionDocumentUsesPrimaryBrowser;
    private long browserWarningRevision;
    private string latestBrowserWarning = string.Empty;
    private string latestWarningEvent = string.Empty;
    private DateTimeOffset latestWarningEventAt = DateTimeOffset.MinValue;
    private string latestConfirmation = string.Empty;
    private DateTimeOffset latestConfirmationAt = DateTimeOffset.MinValue;
    private readonly Dictionary<int, string> hydratedPrescriptionDrugRows = new();
    private MedulaRestorationState lastRestorationState = MedulaRestorationState.Prescription;
    private MedulaRestorationState? activeOperationRestorationState;
    private bool systemErrorRecoveryInProgress;
    private bool suppressBrowserClosedReport;
    private string? browserUserDataFolderOverride;
    private MedulaMessageScope messageScope = MedulaMessageScope.Prescription;
    private long messageScopeRevision;

    private static readonly HashSet<string> PrescriptionActionIds = new(StringComparer.Ordinal)
    {
        "f:buttonGeriDon", "f:buttonAnaSayfa", "f:buttonIlacListesi", "f:buttonRaporListesi",
        "f:buttonMuayeneListesi", "f:buttonEndikasyonDisiSorgu", "f:buttonKisiEndikasyonDisiSorgu",
        "f:button1", "f:buttonBolumler", "f:buttonDoktorArama", "f:buttonBransSec", "f:buttonUyariKodu",
        "f:buttonOzelDurum", "f:buttonIlacEsdeger", "f:buttonIlacBilgiGorme", "f:buttonIlacSil",
        "f:buttonIlacArama", "f:buttonIlacEkle", "f:buttonRaporGoruntule", "f:buttonIlacTedaviSema",
        "f:buttonReceteTeshis", "f:buttonMajistralIlacEkle", "f:buttonRaporSecim", "f:buttonEkle",
        "f:buttonIcd10Sorgula", "f:buttonReceteKaydet", "f:buttonYeniRecete", "f:buttonEreceteSorgu",
        "f:buttonOncekiRecete", "f:buttonSonraki", "f:buttonReceteSil", "f:buttonReceteBilgi",
        "f:buttonReceteYazdir", "f:buttonDokumanEkle", "medula:buttonKarekodSonlandir",
        "medula:buttonKarekodSonlandirmaIptal", "medula:buttonUtsIslemleri"
    };
    private static readonly IReadOnlyDictionary<string, (string Key, string Title)> SavedScreenActions =
        new Dictionary<string, (string Key, string Title)>(StringComparer.Ordinal)
        {
            ["f:buttonReceteBilgi"] = ("saved-prescription-info", "Reçete Bilgi"),
            ["f:buttonReceteYazdir"] = ("saved-prescription-print", "Reçete Yazdır"),
            ["f:buttonDokumanEkle"] = ("saved-prescription-document", "Reçeteye Doküman Ekle")
        };
    private static readonly IReadOnlyDictionary<string, string> PrescriptionSubScreenActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["f:buttonIlacListesi"] = "drugs",
            ["f:buttonRaporListesi"] = "reports",
            ["f:buttonMuayeneListesi"] = "examinations",
            ["f:buttonEndikasyonDisiSorgu"] = "off-label-old",
            ["f:buttonKisiEndikasyonDisiSorgu"] = "off-label-new",
            ["f:button1"] = "facility-search",
            ["f:buttonBolumler"] = "facility-departments",
            ["f:buttonBransSec"] = "branch-select",
            ["f:buttonUyariKodu"] = "warning-code",
            ["f:buttonIlacTedaviSema"] = "treatment-scheme",
            ["f:buttonReceteTeshis"] = "new-warning-code",
            ["f:buttonRaporSecim"] = "report-selection",
            ["f:buttonKarekodIslemleri"] = "its-qr",
            ["medula:buttonUtsIslemleri"] = "uts-operations"
        };

    private MedulaConnectionService()
    {
        ApplicationSettingsStore.SettingsChanged += OnApplicationSettingsChanged;
    }

    public static MedulaConnectionService Instance { get; } = new();

    public MedulaConnectionState State { get; private set; } = MedulaConnectionState.Disconnected;

    public event EventHandler<MedulaConnectionStatus>? StatusChanged;
    public event Action<string>? WarningChanged;
    public event Action<string>? ConfirmationChanged;
    public event Action<string>? NoticeChanged;
    public event Action<MedulaMessage>? MessageChanged;
    public event Action? ForegroundOperationRequested;
    public event Action<MedulaScopedText>? ScopedWarningChanged;
    public event Action<MedulaScopedText>? ScopedConfirmationChanged;
    public event Action<MedulaScopedText>? ScopedNoticeChanged;

    public void RequestForegroundPriority() => ForegroundOperationRequested?.Invoke();

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!pharmacyApp.Infrastructure.OnlineAccessPolicy.MedulaOnlineEnabled)
        {
            Report(MedulaConnectionState.Failed, pharmacyApp.Infrastructure.OnlineAccessPolicy.MedulaMessage);
            return false;
        }
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            var settings = ApplicationSettingsStore.Current;
            if (string.IsNullOrWhiteSpace(settings.MedulaUsername) || string.IsNullOrEmpty(settings.MedulaPassword))
            {
                Report(MedulaConnectionState.Failed, "Medula kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");
                return false;
            }

            Report(MedulaConnectionState.Connecting, "Arka plan tarayıcısı hazırlanıyor.");
            await EnsureBrowserAsync(cancellationToken);

            if (RequireBrowser().Source is { IsAbsoluteUri: true }
                && !RequireBrowser().Source.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                var currentPage = await ReadPageProbeAsync();
                var currentKind = MedulaPageClassifier.Classify(
                    currentPage.Url,
                    currentPage.HasLoginForm,
                    currentPage.Text,
                    currentPage.Title,
                    currentPage.HasKnownMedulaControls);
                if (currentKind == MedulaPageKind.SystemError)
                {
                    Report(MedulaConnectionState.TemporarilyOffline,
                        "Geçici çevrimdışı · Medula Sistem Hatası algılandı; tarayıcı yenileniyor.");
                    await RecreateBrowserAsync(cancellationToken);
                }
                else if (currentKind == MedulaPageKind.Authenticated)
                {
                    Report(MedulaConnectionState.Connected, "Mevcut Medula oturumu kullanılmaya devam ediyor.");
                    return true;
                }
            }

            Report(MedulaConnectionState.Connecting, "Medula giriş sayfası açılıyor.");
            await NavigateAsync(LoginUrl, cancellationToken);

            var loginPage = await ReadPageProbeAsync();
            var loginPageKind = MedulaPageClassifier.Classify(
                loginPage.Url,
                loginPage.HasLoginForm,
                loginPage.Text,
                loginPage.Title,
                loginPage.HasKnownMedulaControls);
            if (loginPageKind == MedulaPageKind.Authenticated)
            {
                Report(MedulaConnectionState.Connected, "Medula bağlantısı başarıyla kuruldu.");
                return true;
            }
            if (loginPageKind != MedulaPageKind.Login)
            {
                Report(MedulaConnectionState.Failed, DescribeFailure(loginPageKind, loginPage, afterSubmission: false));
                return false;
            }

            const int maximumChallengeAttempts = 3;
            for (var challengeAttempt = 1; challengeAttempt <= maximumChallengeAttempts; challengeAttempt++)
            {
                Report(MedulaConnectionState.Connecting,
                    challengeAttempt == 1
                        ? "Güvenlik anahtarı görüntüsü alınıyor."
                        : $"Güvenlik anahtarı yeniden okunuyor ({challengeAttempt}/{maximumChallengeAttempts}).");
                var imagePath = await CaptureChallengeImageAsync(cancellationToken);
                try
                {
                    Report(MedulaConnectionState.Connecting, "Güvenlik anahtarı yanıtı bekleniyor.");
                    var challengeAnswer = await PythonImageReader.ReadImageAsync(
                        imagePath,
                        ImageReaderTimeout,
                        cancellationToken);

                    Report(MedulaConnectionState.Connecting, "Giriş bilgileri Medula'ya gönderiliyor.");
                    await SubmitLoginAsync(
                        settings.MedulaUsername,
                        settings.MedulaPassword,
                        challengeAnswer,
                        cancellationToken);
                }
                finally
                {
                    DeleteTemporaryImage(imagePath);
                }

                var responsePage = await ReadPageProbeAsync();
                var responseKind = MedulaPageClassifier.Classify(
                    responsePage.Url,
                    responsePage.HasLoginForm,
                    responsePage.Text,
                    responsePage.Title,
                    responsePage.HasKnownMedulaControls);
                if (responseKind == MedulaPageKind.Authenticated)
                {
                    Report(MedulaConnectionState.Connected, "Medula bağlantısı başarıyla kuruldu.");
                    return true;
                }

                // Medula silently replaces the challenge and returns the same login form
                // when OCR reads it incorrectly. Retry only that exact response; genuine
                // Medula errors and unexpected pages remain terminal.
                if (responseKind == MedulaPageKind.Login
                    && challengeAttempt < maximumChallengeAttempts)
                {
                    Report(MedulaConnectionState.Connecting,
                        "Güvenlik anahtarı Medula tarafından kabul edilmedi; yenisi okunuyor.");
                    await Task.Delay(150, cancellationToken);
                    continue;
                }

                Report(MedulaConnectionState.Failed,
                    DescribeFailure(responseKind, responsePage, afterSubmission: true));
                return false;
            }

            Report(MedulaConnectionState.Failed,
                "Medula güvenlik anahtarı üç denemede doğrulanamadı.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report(MedulaConnectionState.Failed, "Medula bağlantı denemesi iptal edildi.");
            return false;
        }
        catch (Exception exception)
        {
            Report(MedulaConnectionState.Failed, $"Medula bağlantısı kurulamadı: {exception.Message}");
            return false;
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public void ShowBrowser()
    {
        if (browserWindow is null)
        {
            Report(State, "Arka plan tarayıcısı henüz başlatılmadı.");
            return;
        }

        ShowBrowserWindow();
    }

    public void HideBrowser()
    {
        if (browserWindow is null) return;
        browserWindow.Hide();
        browserWindow.ShowInTaskbar = false;
    }

    public async Task<MedulaBrowserOperationResult> CloseWebBrowserAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            DisposePrescriptionDocumentBrowser();
            var oldBrowser = browser;
            var oldWindow = browserWindow;
            suppressBrowserClosedReport = true;
            browser = null;
            browserWindow = null;
            try
            {
                if (oldBrowser?.CoreWebView2 is not null)
                    oldBrowser.NavigationCompleted -= OnBrowserNavigationCompleted;
                if (oldWindow is not null)
                {
                    oldWindow.Content = null;
                    oldWindow.Close();
                }
                oldBrowser?.Dispose();
            }
            finally
            {
                suppressBrowserClosedReport = false;
            }
            Report(MedulaConnectionState.Disconnected, "Medula tarayıcısı kapatıldı.");
            return new MedulaBrowserOperationResult(
                true, State, "Medula tarayıcısı kapatıldı.", LoginUrl);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public Task<MedulaBrowserOperationResult> GetBrowserStateAsync()
    {
        var ready = browser?.CoreWebView2 is not null && browserWindow is not null;
        return Task.FromResult(new MedulaBrowserOperationResult(
            ready,
            State,
            ready ? "Medula tarayıcısı hazır." : "Medula tarayıcısı açık değil.",
            browser?.Source?.AbsoluteUri ?? LoginUrl));
    }

    public async Task<MedulaBrowserOperationResult> OpenHomeAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaBrowserOperationResult(false, State, "Medula bağlantısı kurulamadı.", LoginUrl);
            await NavigateAsync(HomeUrl, cancellationToken);
            var ready = await WaitForPageStateAsync(HasPrescriptionEntryMenuAsync, cancellationToken);
            return new MedulaBrowserOperationResult(
                ready,
                State,
                ready ? "Medula ana sayfası açıldı." : "Medula ana sayfası doğrulanamadı.",
                browser?.Source?.AbsoluteUri ?? HomeUrl);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaBrowserOperationResult> RefreshCurrentPageAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaBrowserOperationResult(false, State, "Medula bağlantısı kurulamadı.", LoginUrl);
            var currentUrl = browser?.Source?.AbsoluteUri ?? HomeUrl;
            await NavigateAsync(currentUrl, cancellationToken);
            return new MedulaBrowserOperationResult(
                true, State, "Medula sayfası yenilendi.", browser?.Source?.AbsoluteUri ?? currentUrl);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaBrowserOperationResult> OpenWebBrowserAsync(
        bool visible = false,
        CancellationToken cancellationToken = default)
    {
        var connected = await ConnectAsync(cancellationToken);
        if (connected && visible) ShowBrowser();
        return new MedulaBrowserOperationResult(
            connected,
            State,
            connected ? "Medula tarayıcısı açık ve oturum hazır." : "Medula tarayıcısı açılamadı.",
            browser?.Source?.AbsoluteUri ?? LoginUrl);
    }

    public async Task<MedulaBrowserOperationResult> LoginAsync(
        bool forceNewSession = false,
        CancellationToken cancellationToken = default)
    {
        if (forceNewSession)
            return await RestartWebBrowserAsync(false, cancellationToken);

        var connected = await ConnectAsync(cancellationToken);
        return new MedulaBrowserOperationResult(
            connected,
            State,
            connected ? "Medula oturumu açıldı." : "Medula oturumu açılamadı.",
            browser?.Source?.AbsoluteUri ?? LoginUrl);
    }

    public async Task<MedulaBrowserOperationResult> RestartWebBrowserAsync(
        bool restorePreviousState = true,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (restorePreviousState)
            {
                await RestoreAfterSystemErrorCoreAsync(lastRestorationState, cancellationToken);
                return new MedulaBrowserOperationResult(
                    true,
                    State,
                    "Medula tarayıcısı yeniden başlatıldı ve önceki ekran geri yüklendi.",
                    browser?.Source?.AbsoluteUri ?? LoginUrl,
                    BrowserRecreated: true,
                    StateRestored: true);
            }

            Report(MedulaConnectionState.Connecting, "Medula tarayıcısı yeniden başlatılıyor.");
            await RecreateBrowserAsync(cancellationToken);
            var connected = await ConnectWithRetriesAsync(cancellationToken);
            return new MedulaBrowserOperationResult(
                connected,
                State,
                connected ? "Medula tarayıcısı yeniden başlatıldı." : "Medula tarayıcısı yeniden başlatılamadı.",
                browser?.Source?.AbsoluteUri ?? LoginUrl,
                BrowserRecreated: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Medula tarayıcısı yeniden başlatılamadı: {exception.Message}";
            Report(MedulaConnectionState.Failed, message);
            return new MedulaBrowserOperationResult(
                false, State, message, browser?.Source?.AbsoluteUri ?? LoginUrl,
                BrowserRecreated: true);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private void OnApplicationSettingsChanged(ApplicationSettings settings)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        dispatcher.InvokeAsync(() => ApplyBrowserDebugVisibility(settings.MedulaBrowserDebug));
    }

    private void ApplyBrowserDebugVisibility(bool visible)
    {
        if (browserWindow is null || browser?.CoreWebView2 is null) return;
        if (visible)
        {
            ShowBrowserWindow();
            return;
        }

        browserWindow.Hide();
        browserWindow.ShowInTaskbar = false;
    }

    private void ShowBrowserWindow()
    {
        if (browserWindow is null) return;
        browserWindow.WindowStyle = WindowStyle.SingleBorderWindow;
        browserWindow.ResizeMode = ResizeMode.CanResize;
        browserWindow.Width = 1180;
        browserWindow.Height = 820;
        browserWindow.Opacity = 1;
        browserWindow.ShowInTaskbar = true;
        CenterBrowserWindow(browserWindow);
        browserWindow.Show();
        browserWindow.Activate();
    }

    private static void CenterBrowserWindow(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Left + Math.Max(0, (workArea.Width - window.Width) / 2);
        window.Top = workArea.Top + Math.Max(0, (workArea.Height - window.Height) / 2);
    }

    public async Task<MedulaPrescriptionPageResult> OpenPrescriptionEntryAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            hydratedPrescriptionDrugRows.Clear();
            var result = await OpenPrescriptionEntryCoreAsync(cancellationToken);
            if (result.IsPrescriptionEntryPage && !systemErrorRecoveryInProgress)
                lastRestorationState = MedulaRestorationState.Prescription;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MedulaPrescriptionPageResult(
                false,
                false,
                "Medula",
                browser?.Source?.AbsoluteUri ?? LoginUrl,
                $"Reçete Giriş ekranı açılamadı: {exception.Message}",
                string.Empty);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaElectronicPrescriptionQueryResult> OpenElectronicPrescriptionQueryAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.ElectronicPrescription);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyElectronicPrescriptionQuery("Medula bağlantısı kurulamadı.");
            if (!await BrowserElementExistsAsync("form1:buttonSorgula"))
            {
                await NavigateAsync(HomeUrl, cancellationToken);
                if (!await WaitForPageStateAsync(
                        () => BrowserElementExistsAsync("form1:menuHtmlCommandExButton11"), cancellationToken))
                    return EmptyElectronicPrescriptionQuery("Medula ana menüsü hazır değil.");
                var opened = await ExecuteActionAndWaitAsync("""
                    (() => {
                        const action = document.getElementById('form1:menuHtmlCommandExButton11');
                        if (!action) return { clicked: false, messages: [], confirmations: [] };
                        action.click();
                        return { clicked: true, messages: [], confirmations: [] };
                    })();
                    """, cancellationToken);
                if (!opened.Clicked || !await WaitForPageStateAsync(
                        () => BrowserElementExistsAsync("form1:buttonSorgula"), cancellationToken))
                    return EmptyElectronicPrescriptionQuery("E-Reçete Sorgu ekranı Medula'da açılamadı.");
            }
            var result = await ReadElectronicPrescriptionQueryAsync("E-Reçete Sorgu ekranı hazır.");
            if (result.IsOpen)
                RememberElectronicPrescriptionState(new MedulaElectronicPrescriptionRestoration(
                    result.PatientIdentityNumber, result.ElectronicPrescriptionNumber,
                    result.TrackingNumber, result.PrescriptionDate, false, false));
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyElectronicPrescriptionQuery($"E-Reçete Sorgu ekranı açılamadı: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaElectronicPrescriptionQueryResult> SearchElectronicPrescriptionsAsync(
        string patientIdentityNumber,
        string electronicPrescriptionNumber,
        string trackingNumber,
        string prescriptionDate,
        bool searchByTrackingNumber,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.ElectronicPrescription);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyElectronicPrescriptionQuery("Medula bağlantısı kurulamadı.");
            if (!await BrowserElementExistsAsync("form1:buttonSorgula"))
            {
                await NavigateAsync(HomeUrl, cancellationToken);
                await WaitForPageStateAsync(() => BrowserElementExistsAsync("form1:menuHtmlCommandExButton11"), cancellationToken);
                await ExecuteActionAndWaitAsync("""
                    (() => {
                        const action = document.getElementById('form1:menuHtmlCommandExButton11');
                        if (!action) return { clicked: false, messages: [], confirmations: [] };
                        action.click();
                        return { clicked: true, messages: [], confirmations: [] };
                    })();
                    """, cancellationToken);
                if (!await WaitForPageStateAsync(() => BrowserElementExistsAsync("form1:buttonSorgula"), cancellationToken))
                    return EmptyElectronicPrescriptionQuery("E-Reçete Sorgu ekranı Medula'da açılamadı.");
            }

            var tcJson = JsonSerializer.Serialize(patientIdentityNumber.Trim());
            var ePrescriptionJson = JsonSerializer.Serialize(electronicPrescriptionNumber.Trim().ToUpperInvariant());
            var trackingJson = JsonSerializer.Serialize(trackingNumber.Trim());
            var dateJson = JsonSerializer.Serialize(prescriptionDate.Trim());
            var actionIdJson = JsonSerializer.Serialize(
                searchByTrackingNumber ? "form1:buttonSorgula2" : "form1:buttonSorgula");
            var restoration = new MedulaElectronicPrescriptionRestoration(
                patientIdentityNumber.Trim(), electronicPrescriptionNumber.Trim().ToUpperInvariant(),
                trackingNumber.Trim(), prescriptionDate.Trim(), searchByTrackingNumber, false);
            RememberElectronicPrescriptionState(restoration);
            var operationRestoration = new MedulaRestorationState(
                null, [], ElectronicPrescription: restoration);
            activeOperationRestorationState = operationRestoration;
            var search = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => { confirmations.push(String(message ?? '')); return false; };
                    try {
                        const set = (id, value) => {
                            const field = document.getElementById(id);
                            if (!field) return false;
                            field.value = value;
                            field.dispatchEvent(new Event('input', { bubbles: true }));
                            field.dispatchEvent(new Event('change', { bubbles: true }));
                            return true;
                        };
                        set('form1:textSmartDeger', {{tcJson}});
                        set('form1:text4', {{ePrescriptionJson}});
                        set('form1:text9', {{trackingJson}});
                        set('form1:text7', {{dateJson}});
                        const action = document.getElementById({{actionIdJson}});
                        if (!action) return { clicked: false, messages, confirmations };
                        action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """, cancellationToken);
            if (await RecoverElectronicPrescriptionOperationIfRequiredAsync(
                    operationRestoration, cancellationToken))
            {
                var recovered = await ReadElectronicPrescriptionQueryAsync(
                    "E-Reçete sorgusu Medula yeniden başlatıldıktan sonra geri yüklendi.");
                return recovered with { IsSuccess = recovered.IsOpen };
            }
            var emittedWarning = MedulaBrowserWarning.First(search.Messages, search.Confirmations);
            var visibleWarning = await ReadVisibleMedulaWarningAsync();
            var warning = FirstNonBlank(emittedWarning, visibleWarning);
            if (!string.IsNullOrWhiteSpace(warning)) ReportWarning(warning);
            var result = await ReadElectronicPrescriptionQueryAsync(
                string.IsNullOrWhiteSpace(warning) ? "E-Reçete sorgu sonuçları hazır." : warning);
            if (result.IsOpen) RememberElectronicPrescriptionState(restoration);
            return result with
            {
                IsSuccess = search.Clicked && string.IsNullOrWhiteSpace(warning),
                Prescriptions = string.IsNullOrWhiteSpace(warning) ? result.Prescriptions : [],
                WarningText = string.IsNullOrWhiteSpace(warning) ? null : warning
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyElectronicPrescriptionQuery($"E-Reçete sorgusu tamamlanamadı: {exception.Message}");
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaElectronicPrescriptionDetail> OpenElectronicPrescriptionDetailAsync(
        MedulaElectronicPrescriptionListItem prescription,
        string patientIdentityNumber,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.ElectronicPrescription);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyElectronicPrescriptionDetail("Medula bağlantısı kurulamadı.");
            var previousState = lastRestorationState.ElectronicPrescription;
            var restoration = new MedulaElectronicPrescriptionRestoration(
                patientIdentityNumber.Trim(), prescription.ElectronicPrescriptionNumber.Trim(),
                previousState?.TrackingNumber ?? string.Empty,
                previousState?.PrescriptionDate ?? prescription.PrescriptionDate,
                previousState?.SearchByTrackingNumber ?? false,
                true);
            RememberElectronicPrescriptionState(restoration);
            var operationRestoration = new MedulaRestorationState(
                null, [], ElectronicPrescription: restoration);
            activeOperationRestorationState = operationRestoration;
            var numberJson = JsonSerializer.Serialize(prescription.ElectronicPrescriptionNumber);
            var actionIdJson = JsonSerializer.Serialize(prescription.ActionId);
            var warningRevision = GetWarningRevision();
            var click = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => { confirmations.push(String(message ?? '')); return false; };
                    try {
                        const table = document.getElementById('form1:tableExERecete');
                        const rows = table ? [...table.rows].slice(1) : [];
                        const row = rows.find(candidate => clean(candidate.textContent).includes({{numberJson}}));
                        if (!row) return { clicked: false, messages, confirmations };
                        const requested = document.getElementById({{actionIdJson}});
                        const clickable = requested && requested.type !== 'hidden' ? requested
                            : [row, ...row.querySelectorAll('*')].find(element => typeof element.onclick === 'function')
                            || row.querySelector('a,input[type=submit],input[type=image],button');
                        if (clickable) clickable.click();
                        else row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """, cancellationToken);
            if (await RecoverElectronicPrescriptionOperationIfRequiredAsync(
                    operationRestoration, cancellationToken))
                return await ReadElectronicPrescriptionDetailAsync(
                    patientIdentityNumber, prescription.ElectronicPrescriptionNumber,
                    "E-Reçete ayrıntıları Medula yeniden başlatıldıktan sonra geri yüklendi.");
            var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations)
                ?? GetWarningAfter(warningRevision)
                ?? await ReadVisibleMedulaWarningAsync();
            if (!click.Clicked)
                return EmptyElectronicPrescriptionDetail("Seçilen e-reçete Medula sonuçlarında bulunamadı.");
            if (!string.IsNullOrWhiteSpace(warning))
            {
                ReportWarning(warning);
                return EmptyElectronicPrescriptionDetail(warning) with { WarningText = warning };
            }
            var outcome = await WaitForElectronicPrescriptionDetailOutcomeAsync(
                warningRevision, cancellationToken);
            if (!string.IsNullOrWhiteSpace(outcome.Warning))
            {
                ReportWarning(outcome.Warning);
                return EmptyElectronicPrescriptionDetail(outcome.Warning) with
                {
                    WarningText = outcome.Warning
                };
            }
            if (!outcome.Opened)
                return EmptyElectronicPrescriptionDetail("E-Reçete detay ekranı Medula'da açılamadı.");
            var detail = await ReadElectronicPrescriptionDetailAsync(
                patientIdentityNumber, prescription.ElectronicPrescriptionNumber,
                "E-Reçete bilgileri hazır.");
            if (detail.IsOpen) RememberElectronicPrescriptionState(restoration);
            return detail;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyElectronicPrescriptionDetail($"E-Reçete detayları açılamadı: {exception.Message}");
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaElectronicPrescriptionQueryResult> ReturnFromElectronicPrescriptionDetailAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.ElectronicPrescription);
        try
        {
            var click = await ClickElectronicPrescriptionCaptionAsync("geri dön", cancellationToken);
            if (!click.Clicked)
                return EmptyElectronicPrescriptionQuery("Geri Dön denetimi Medula ekranında bulunamadı.");
            await WaitForPageStateAsync(() => BrowserElementExistsAsync("form1:buttonSorgula"), cancellationToken);
            var result = await ReadElectronicPrescriptionQueryAsync("E-Reçete sorgu sonuçlarına dönüldü.");
            if (lastRestorationState.ElectronicPrescription is { } state)
                RememberElectronicPrescriptionState(state with { DetailOpen = false });
            return result;
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<bool> ReturnFromElectronicPrescriptionDetailToPrescriptionAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.Prescription);
        try
        {
            var click = await ClickElectronicPrescriptionCaptionAsync("geri dön", cancellationToken);
            if (!click.Clicked) return false;
            return await WaitForPageStateAsync(async () =>
                await IsPrescriptionEntryPageAsync() || await IsPrescriptionViewPageAsync(),
                cancellationToken);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaElectronicPrescriptionContinueResult> ContinueElectronicPrescriptionAsync(
        string patientIdentityNumber,
        string electronicPrescriptionNumber,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.ElectronicPrescription);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new(false, "Medula bağlantısı kurulamadı.", null, "Medula bağlantısı kurulamadı.");
            var electronicState = lastRestorationState.ElectronicPrescription
                ?? new MedulaElectronicPrescriptionRestoration(
                    patientIdentityNumber.Trim(), electronicPrescriptionNumber.Trim(),
                    string.Empty, string.Empty, false, true);
            electronicState = electronicState with { DetailOpen = true };
            var operationRestoration = new MedulaRestorationState(
                null, [], ElectronicPrescription: electronicState);
            activeOperationRestorationState = operationRestoration;
            ActionClickResult? click = null;
            string warning = string.Empty;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                click = await ClickElectronicPrescriptionCaptionAsync("reçete kaydına devam", cancellationToken);
                if (await RecoverElectronicPrescriptionOperationIfRequiredAsync(
                        operationRestoration, cancellationToken))
                {
                    if (attempt == 0) continue;
                    const string repeatedFatalError =
                        "Medula E-Reçete ekranı yeniden başlatıldıktan sonra tekrar sistem hatası verdi.";
                    return new(false, repeatedFatalError, null, repeatedFatalError);
                }
                warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations)
                    ?? await ReadVisibleMedulaWarningAsync()
                    ?? string.Empty;
                break;
            }
            if (click is null || !click.Clicked || !string.IsNullOrWhiteSpace(warning))
            {
                warning = FirstNonBlank(warning, "Reçete Kaydına Devam Et denetimi Medula ekranında bulunamadı.");
                ReportWarning(warning);
                return new(false, warning, null, warning);
            }
            if (!await WaitForPageStateAsync(async () =>
                await BrowserElementExistsAsync("f:buttonReceteKaydet")
                || await BrowserElementExistsAsync("f:button2"), cancellationToken))
                return new(false, "E-Reçete reçete ekranı Medula'da açılamadı.", null,
                    "E-Reçete reçete ekranı Medula'da açılamadı.");

            var page = await ReadPrescriptionPageResultAsync("E-Reçete reçete ekranı açıldı.");
            var values = new Dictionary<string, string>(page.FieldValues ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["medula:e-prescription-no"] = electronicPrescriptionNumber.Trim()
            };
            page = page with { FieldValues = values };
            var drugs = await ReadPrescriptionDrugsAsync();
            var diagnoses = await ReadPrescriptionDiagnosesAsync();
            var firstName = values.GetValueOrDefault("medula:patient-first-name", string.Empty);
            var lastName = values.GetValueOrDefault("medula:patient-last-name", string.Empty);
            var source = new MedulaPrescriptionListItem(
                values.GetValueOrDefault("medula:prescription-no", string.Empty), string.Empty,
                values.GetValueOrDefault("f:t29", string.Empty), firstName, lastName,
                values.GetValueOrDefault("medula:coverage", string.Empty),
                values.GetValueOrDefault("medula:qr-status", string.Empty), "I", string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, "e-prescription-query", 1, 0)
            {
                PatientTcIdentityNo = patientIdentityNumber.Trim(),
                IsLiveOnMedula = true
            };
            var snapshot = new MedulaPrescriptionSnapshot(
                page, drugs, diagnoses, $"{firstName} {lastName}".Trim(), source,
                IsSavedOnMedula: false);
            if (!systemErrorRecoveryInProgress)
            {
                var prescriptionState = electronicState with
                {
                    DetailOpen = true,
                    PrescriptionOpen = true
                };
                lastRestorationState = new MedulaRestorationState(
                    BuildElectronicPrescriptionInput(snapshot), [],
                    ElectronicPrescription: prescriptionState);
            }
            return new(page.IsPrescriptionEntryPage, page.Message, snapshot, page.WarningText);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, $"E-Reçete reçete ekranı açılamadı: {exception.Message}", null, exception.Message);
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaElectronicPrescriptionDetail> ReturnToElectronicPrescriptionDetailAsync(
        string patientIdentityNumber,
        string electronicPrescriptionNumber,
        CancellationToken cancellationToken = default)
    {
        var normalizedPatientIdentity = patientIdentityNumber.Trim();
        var normalizedElectronicPrescription = electronicPrescriptionNumber.Trim().ToUpperInvariant();
        if (normalizedElectronicPrescription.Length == 0)
            return EmptyElectronicPrescriptionDetail("E-Reçete numarası bulunamadı.");

        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.ElectronicPrescription);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyElectronicPrescriptionDetail("Medula bağlantısı kurulamadı.");

            var previous = lastRestorationState.ElectronicPrescription;
            var electronicState = new MedulaElectronicPrescriptionRestoration(
                normalizedPatientIdentity,
                normalizedElectronicPrescription,
                previous?.TrackingNumber ?? string.Empty,
                previous?.PrescriptionDate ?? string.Empty,
                previous?.SearchByTrackingNumber ?? false,
                true);
            var restoration = new MedulaRestorationState(
                null, [], ElectronicPrescription: electronicState);
            activeOperationRestorationState = restoration;

            if (await BrowserElementExistsAsync("f:buttonEreceteGoruntule"))
            {
                var warningRevision = GetWarningRevision();
                var click = await ExecuteActionAndWaitAsync("""
                    (() => {
                        const messages = [];
                        const confirmations = [];
                        const originalAlert = window.alert;
                        const originalConfirm = window.confirm;
                        window.alert = message => messages.push(String(message ?? ''));
                        window.confirm = message => { confirmations.push(String(message ?? '')); return false; };
                        try {
                            const action = document.getElementById('f:buttonEreceteGoruntule');
                            if (!action) return { clicked: false, messages, confirmations };
                            action.click();
                            return { clicked: true, messages, confirmations };
                        } finally {
                            window.alert = originalAlert;
                            window.confirm = originalConfirm;
                        }
                    })();
                    """, cancellationToken);
                if (await RecoverElectronicPrescriptionOperationIfRequiredAsync(restoration, cancellationToken))
                    return await ReadElectronicPrescriptionDetailAsync(
                        normalizedPatientIdentity, normalizedElectronicPrescription,
                        "E-Reçete ekranı Medula yeniden başlatıldıktan sonra geri yüklendi.");
                var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
                if (!click.Clicked || !string.IsNullOrWhiteSpace(warning))
                {
                    warning = FirstNonBlank(warning, "E-Reçete Görüntüle denetimi Medula ekranında bulunamadı.");
                    ReportWarning(warning);
                    return EmptyElectronicPrescriptionDetail(warning) with { WarningText = warning };
                }
                var outcome = await WaitForElectronicPrescriptionDetailOutcomeAsync(
                    warningRevision, cancellationToken);
                if (!outcome.Opened)
                {
                    warning = FirstNonBlank(
                        outcome.Warning, "E-Reçete ekranı Medula'da geri açılamadı.");
                    if (!string.IsNullOrWhiteSpace(outcome.Warning)) ReportWarning(outcome.Warning);
                    return EmptyElectronicPrescriptionDetail(warning) with { WarningText = warning };
                }
            }
            else
            {
                await RestoreElectronicPrescriptionStateAsync(electronicState, cancellationToken);
            }

            var detail = await ReadElectronicPrescriptionDetailAsync(
                normalizedPatientIdentity, normalizedElectronicPrescription,
                "E-Reçete ekranına dönüldü.");
            if (detail.IsOpen) RememberElectronicPrescriptionState(electronicState);
            return detail;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyElectronicPrescriptionDetail($"E-Reçete ekranına dönülemedi: {exception.Message}");
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    private async Task<ActionClickResult> ClickElectronicPrescriptionCaptionAsync(
        string caption,
        CancellationToken cancellationToken)
    {
        var captionJson = JsonSerializer.Serialize(caption);
        return await ExecuteActionAndWaitAsync($$"""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleLowerCase('tr-TR');
                const action = [...document.querySelectorAll('input[type=submit],input[type=button],button,a')]
                    .find(candidate => clean(candidate.value || candidate.textContent).includes({{captionJson}}));
                if (!action) return { clicked: false, messages: [], confirmations: [] };
                action.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
    }

    private async Task<MedulaElectronicPrescriptionQueryResult> ReadElectronicPrescriptionQueryAsync(string message)
    {
        const string script = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const value = id => document.getElementById(id)?.value ?? '';
                const table = document.getElementById('form1:tableExERecete');
                const rows = table ? [...table.rows].slice(1) : [];
                return {
                    isOpen: !!document.getElementById('form1:buttonSorgula'),
                    url: location.href,
                    patientIdentityNumber: value('form1:textSmartDeger') === '0' ? '' : value('form1:textSmartDeger'),
                    electronicPrescriptionNumber: value('form1:text4'),
                    trackingNumber: value('form1:text9'),
                    prescriptionDate: value('form1:text7'),
                    prescriptions: rows.map((row, index) => {
                        const cells = [...row.cells].map(cell => clean(cell.textContent));
                        const hidden = row.querySelector('[id*="rowAction"][id$="__hidden_action"]');
                        const direct = row.querySelector('[id*="rowAction"]:not([type=hidden]),a[id],input[type=image][id],button[id]');
                        return {
                            actionId: direct?.id || hidden?.id?.replace(/__hidden_action$/, '') || '',
                            trackingNumber: cells[1] || '', electronicPrescriptionNumber: cells[2] || '',
                            prescriptionDate: cells[3] || '', doctorName: cells[4] || '',
                            prescriptionType: cells[5] || '', facilityName: cells[6] || ''
                        };
                    }).filter(item => item.electronicPrescriptionNumber)
                };
            })();
            """;
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        var probe = JsonSerializer.Deserialize<ElectronicPrescriptionQueryProbe>(raw, JsonOptions)
            ?? new ElectronicPrescriptionQueryProbe(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, []);
        return new MedulaElectronicPrescriptionQueryResult(
            probe.IsOpen, probe.IsOpen, message, probe.Url, probe.PatientIdentityNumber,
            probe.ElectronicPrescriptionNumber, probe.TrackingNumber, probe.PrescriptionDate,
            probe.Prescriptions);
    }

    private async Task<MedulaElectronicPrescriptionDetail> ReadElectronicPrescriptionDetailAsync(
        string patientIdentityNumber,
        string electronicPrescriptionNumber,
        string message)
    {
        const string script = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const visible = element => element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden';
                const actionText = element => clean(element.value || element.innerText || element.textContent);
                const actions = [...document.querySelectorAll('input[type=submit],input[type=button],button,a')]
                    .filter(visible).map(actionText);
                const ownText = cell => {
                    if (!cell) return '';
                    const clone = cell.cloneNode(true);
                    clone.querySelectorAll('input,select,textarea,button,a,img,table').forEach(item => item.remove());
                    return clean(clone.innerText || clone.textContent);
                };
                const directRows = table => [...table?.rows || []].filter(row => row.closest('table') === table);
                const panels = [...document.querySelectorAll('table.borderTable')].filter(visible);
                const main = panels.find(table => {
                    const text = clean(table.innerText || table.textContent);
                    return text.includes('E-Reçete No') && text.includes('Doktor Dip.Tesc.No');
                });
                const fields = [];
                const signatureRows = [];
                directRows(main).forEach(row => {
                    const cells = [...row.children].filter(cell => /^(TD|TH)$/.test(cell.tagName));
                    const rowText = clean(cells.map(cell => ownText(cell)).join(' '));
                    if (/reçete elektronik olarak imzalanmıştır/i.test(rowText)) signatureRows.push(rowText);
                    for (let index = 0; index + 2 < cells.length; index++) {
                        const label = ownText(cells[index]).replace(/[:：]+$/, '').trim();
                        if (!label || label.length > 100 || !/^[:：]$/.test(ownText(cells[index + 1]))) continue;
                        const value = clean(cells[index + 2].innerText || cells[index + 2].textContent);
                        if (value.length <= 1200) fields.push({ label, value });
                        index += 2;
                    }
                });
                const fieldValue = label => fields.find(field => field.label === label)?.value || '';
                const patientBox = document.getElementById('form1:box4');
                const patientName = clean([...(patientBox?.querySelectorAll('td') || [])]
                    .map(cell => ownText(cell)).filter(Boolean).join(' '));
                const patientFields = [
                    { label: 'T.C. Kimlik No', value: fieldValue('T.C.Kimlik No') },
                    { label: 'Adı Soyadı', value: patientName }
                ];
                const doctorLabels = new Set(['Doktor Adı/Soyadı', 'Doktor Dip.Tesc.No', 'Doktor Branş', 'Doktor Sertifika']);
                const ignoredLabels = new Set(['T.C.Kimlik No']);
                const doctorFields = fields.filter(field => doctorLabels.has(field.label));
                const prescriptionFields = fields.filter(field => !doctorLabels.has(field.label) && !ignoredLabels.has(field.label));

                const drugPanel = panels.find(table => {
                    const text = clean(table.innerText || table.textContent);
                    return text.startsWith('İlaç Bilgileri') && text.includes('Kullanım Şekli');
                });
                const drugRows = directRows(drugPanel);
                const drugs = [];
                for (let rowIndex = 0; rowIndex < drugRows.length; rowIndex++) {
                    const row = drugRows[rowIndex];
                    const cells = [...row.children].filter(cell => /^(TD|TH)$/.test(cell.tagName));
                    const tokens = cells.map(cell => ownText(cell));
                    const valueAfter = label => {
                        const index = tokens.findIndex(value => value.replace(/\s+/g, '').toLocaleLowerCase('tr-TR')
                            === label.replace(/\s+/g, '').toLocaleLowerCase('tr-TR'));
                        return index >= 0 ? clean(cells[index + 1]?.innerText || cells[index + 1]?.textContent) : '';
                    };
                    const fullName = valueAfter('Adı:');
                    if (!fullName) continue;
                    const barcodeMatch = fullName.match(/\((\d{8,14})\)\s*$/);
                    const diagnosis = clean(drugRows[rowIndex + 1]?.innerText || drugRows[rowIndex + 1]?.textContent);
                    drugs.push({
                        barcode: barcodeMatch?.[1] || '',
                        name: barcodeMatch ? fullName.slice(0, barcodeMatch.index).trim() : fullName,
                        quantity: valueAfter('Adet:'), usage: valueAfter('Kullanım:'),
                        usageRoute: valueAfter('Kullanım Şekli:'),
                        description: /\badı\s*:/i.test(diagnosis) ? '' : diagnosis
                    });
                }
                const readTable = (id, title) => {
                    const table = document.getElementById(id);
                    if (!table) return null;
                    const rows = [...table.rows].filter(row => row.closest('table') === table);
                    const headers = [...rows[0]?.cells || []].map(cell => clean(cell.innerText || cell.textContent));
                    const dataRows = rows.slice(1).map(row => [...row.cells].map(cell => clean(cell.innerText || cell.textContent)))
                        .filter(row => row.some(Boolean));
                    return { id, headers, rows: dataRows, rowActionIds: [], pageActions: [], title };
                };
                const diagnosisTable = readTable('form1:tableEx3', 'ICD-10 Tanılar');
                const diagnoses = (diagnosisTable?.rows || []).map(row => ({
                    code: row[0] || '', description: row[1] || ''
                })).filter(item => item.code || item.description);
                return {
                    isOpen: !!main,
                    title: 'E-Reçete', url: location.href, patientName,
                    patientFields, prescriptionFields, doctorFields, drugs, diagnoses,
                    approvalTable: readTable('form1:tableEx2', 'Onay Listesi'),
                    signatureText: signatureRows.join(' '),
                    canReturn: actions.some(text => text.toLocaleLowerCase('tr-TR').includes('geri dön')),
                    canContinue: actions.some(text => text.toLocaleLowerCase('tr-TR').includes('reçete kaydına devam'))
                };
            })();
            """;
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        var probe = JsonSerializer.Deserialize<ElectronicPrescriptionDetailProbe>(raw, JsonOptions)
            ?? ElectronicPrescriptionDetailProbe.Empty;
        return new MedulaElectronicPrescriptionDetail(
            probe.IsOpen, probe.Title, probe.Url, message, patientIdentityNumber,
            electronicPrescriptionNumber, probe.PatientName, probe.PatientFields,
            probe.PrescriptionFields, probe.DoctorFields, probe.Drugs, probe.Diagnoses,
            probe.ApprovalTable, probe.SignatureText,
            probe.CanReturn, probe.CanContinue);
    }

    private static MedulaElectronicPrescriptionQueryResult EmptyElectronicPrescriptionQuery(string message) =>
        new(false, false, message, string.Empty, string.Empty, string.Empty, string.Empty,
            DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), [], message);

    private static MedulaElectronicPrescriptionDetail EmptyElectronicPrescriptionDetail(string message) =>
        new(false, "E-Reçete", string.Empty, message, string.Empty, string.Empty, string.Empty,
            [], [], [], [], [], null, string.Empty, false, false, message);

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private void RememberElectronicPrescriptionState(
        MedulaElectronicPrescriptionRestoration state)
    {
        if (systemErrorRecoveryInProgress) return;
        lastRestorationState = new MedulaRestorationState(
            null, [], ElectronicPrescription: state);
    }

    public void RememberSavedPrescriptionContext(MedulaPrescriptionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsSavedOnMedula
            || snapshot.SourceListItem is not { } source
            || string.IsNullOrWhiteSpace(source.PrescriptionNumber)
            || source.PrescriptionNumber.Trim() == "0")
            return;
        lastRestorationState = new MedulaRestorationState(
            BuildElectronicPrescriptionInput(snapshot), []);
    }

    private static MedulaPrescriptionInput BuildElectronicPrescriptionInput(
        MedulaPrescriptionSnapshot snapshot)
    {
        var fields = snapshot.Page.FieldValues ?? new Dictionary<string, string>();
        string Field(string key) => fields.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
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

    public async Task<MedulaPrescriptionPageResult> RefreshPrescriptionEntryAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaPrescriptionPageResult(
                    false, false, "Medula", browser?.Source?.AbsoluteUri ?? LoginUrl,
                    "Medula bağlantısı kurulamadı.", string.Empty);
            hydratedPrescriptionDrugRows.Clear();
            var result = await ReloadPrescriptionEntryThroughHomeAsync(cancellationToken);
            if (result.IsPrescriptionEntryPage && !systemErrorRecoveryInProgress)
                lastRestorationState = MedulaRestorationState.Prescription;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MedulaPrescriptionPageResult(
                false, false, "Medula", browser?.Source?.AbsoluteUri ?? LoginUrl,
                $"Reçete Giriş ekranı yenilenemedi: {exception.Message}", string.Empty);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionListResult> SearchPrescriptionListAsync(
        DateOnly startDate,
        DateOnly endDate,
        string typeValue,
        CancellationToken cancellationToken = default)
    {
        if (endDate < startDate)
            return new MedulaPrescriptionListResult(false, "Bitiş tarihi başlangıç tarihinden önce olamaz.", []);
        if (!MedulaPrescriptionListTypes.All.Any(type => type.Value == typeValue))
            return new MedulaPrescriptionListResult(false, "Geçerli bir reçete türü seçin.", []);

        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.PrescriptionList);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaPrescriptionListResult(false, "Medula bağlantısı kurulamadı.", []);

            Report(MedulaConnectionState.Connecting, "Reçete listesi hazırlanıyor.");
            // The daily Medula filter is the only reliable source of the prescription's
            // first-save/filter date; that date is not included in the result table.
            var rows = new List<MedulaPrescriptionListItem>();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var currentMonth = new DateOnly(today.Year, today.Month, 1);
            var liveStart = startDate < currentMonth ? currentMonth : startDate;
            var liveEnd = endDate > today ? today : endDate;
            for (var date = liveStart; date <= liveEnd; date = date.AddDays(1))
                rows.AddRange(await SearchDailyPrescriptionListCoreAsync(date, typeValue, cancellationToken));
            return CompletePrescriptionListSearch(rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Reçete listesi alınamadı: {exception.Message}";
            Report(MedulaConnectionState.Failed, message);
            return new MedulaPrescriptionListResult(false, message, []);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaRemoteScreen> OpenPatientMedicationListFromDailyPrescriptionAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken = default)
    {
        var patientTc = patientTcIdentityNo.Trim();
        if (patientTc.Length != 11 || patientTc.Any(character => !char.IsDigit(character)))
            return EmptyRemoteScreen(
                "patient-medication-list", "Hasta İlaç Listesi",
                "Geçerli bir T.C. Kimlik No giriniz.");

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyRemoteScreen(
                    "patient-medication-list", "Hasta İlaç Listesi",
                    "Medula bağlantısı kurulamadı.");

            for (var recoveryAttempt = 0; recoveryAttempt < 2; recoveryAttempt++)
            {
                try
                {
                    var result = await OpenPatientMedicationListFromDailyPrescriptionCoreAsync(
                        patientTc, cancellationToken);
                    if (result.IsOpen)
                        Report(MedulaConnectionState.Connected,
                            "Hastanın Medula ilaç listesi günlük reçete üzerinden açıldı.");
                    return result;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (recoveryAttempt == 0)
                    {
                        var recovered = await IsSystemErrorPageAsync()
                            ? await RecoverFromSystemErrorDuringOperationAsync(
                                MedulaRestorationState.Prescription, cancellationToken)
                            : await IsLoginPageAsync()
                                && await RecoverFromExpiredSessionDuringOperationAsync(
                                    MedulaRestorationState.Prescription, cancellationToken);
                        if (recovered) continue;
                    }

                    return EmptyRemoteScreen(
                        "patient-medication-list",
                        "Hasta İlaç Listesi",
                        $"Hasta ilaç listesi açılamadı: {exception.Message}",
                        browser?.Source?.AbsoluteUri ?? string.Empty);
                }
            }

            return EmptyRemoteScreen(
                "patient-medication-list", "Hasta İlaç Listesi",
                "Hasta ilaç listesi Medula kurtarma işleminden sonra açılamadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaRemoteScreen> OpenPatientReportsFromDailyPrescriptionAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken = default)
    {
        var patientTc = patientTcIdentityNo.Trim();
        if (patientTc.Length != 11 || patientTc.Any(character => !char.IsDigit(character)))
            return EmptyRemoteScreen(
                "reports", "Hasta Raporları",
                "Geçerli bir T.C. Kimlik No giriniz.");

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyRemoteScreen(
                    "reports", "Hasta Raporları",
                    "Medula bağlantısı kurulamadı.");

            for (var recoveryAttempt = 0; recoveryAttempt < 2; recoveryAttempt++)
            {
                try
                {
                    var result = await OpenPatientReportsFromDailyPrescriptionCoreAsync(
                        patientTc, cancellationToken);
                    if (result.IsOpen)
                        Report(MedulaConnectionState.Connected,
                            "Hastanın Medula raporları günlük reçete üzerinden açıldı.");
                    return result;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (recoveryAttempt == 0)
                    {
                        var recovered = await IsSystemErrorPageAsync()
                            ? await RecoverFromSystemErrorDuringOperationAsync(
                                MedulaRestorationState.Prescription, cancellationToken)
                            : await IsLoginPageAsync()
                                && await RecoverFromExpiredSessionDuringOperationAsync(
                                    MedulaRestorationState.Prescription, cancellationToken);
                        if (recovered) continue;
                    }

                    return EmptyRemoteScreen(
                        "reports",
                        "Hasta Raporları",
                        $"Hasta raporları açılamadı: {exception.Message}",
                        browser?.Source?.AbsoluteUri ?? string.Empty);
                }
            }

            return EmptyRemoteScreen(
                "reports", "Hasta Raporları",
                "Hasta raporları Medula kurtarma işleminden sonra açılamadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionListResult> SearchPrescriptionListByMedulaFilterAsync(
        bool daily,
        int day,
        string periodValue,
        string typeValue,
        CancellationToken cancellationToken = default)
    {
        if (!MedulaPrescriptionListTypes.All.Any(type => type.Value == typeValue))
            return new MedulaPrescriptionListResult(false, "Geçerli bir reçete türü seçin.", []);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (daily && (day < 1 || day > today.Day))
            return new MedulaPrescriptionListResult(false, "Geçerli bir günlük Medula filtresi seçin.", []);
        if (!daily && !DateOnly.TryParseExact(
                periodValue, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return new MedulaPrescriptionListResult(false, "Geçerli bir Medula dönemi seçin.", []);

        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.PrescriptionList);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaPrescriptionListResult(false, "Medula bağlantısı kurulamadı.", []);
            Report(MedulaConnectionState.Connecting, "Medula reçete filtresi uygulanıyor.");
            var rows = daily
                ? await SearchDailyPrescriptionListCoreAsync(
                    new DateOnly(today.Year, today.Month, day), typeValue, cancellationToken)
                : await SearchMonthlyPrescriptionPeriodCoreAsync(periodValue, typeValue, cancellationToken);
            return CompletePrescriptionListSearch(rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Reçete listesi alınamadı: {exception.Message}";
            Report(MedulaConnectionState.Failed, message);
            return new MedulaPrescriptionListResult(false, message, []);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<IReadOnlyList<MedulaPrescriptionPeriodOption>> LoadPrescriptionListPeriodsAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.PrescriptionList);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken)) return [];
            await OpenPrescriptionListScreenAsync(
                "form1:menuHtmlCommandExButton31", "ReceteListe.jsp", cancellationToken);
            return await ReadPrescriptionListPeriodsAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionListResult> SearchReachablePrescriptionsAsync(
        string typeValue,
        CancellationToken cancellationToken = default)
    {
        if (!MedulaPrescriptionListTypes.All.Any(type => type.Value == typeValue))
            return new MedulaPrescriptionListResult(false, "Geçerli bir reçete türü seçin.", []);
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.PrescriptionList);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaPrescriptionListResult(false, "Medula bağlantısı kurulamadı.", []);
            Report(MedulaConnectionState.Connecting, "Medula'da erişilebilir reçeteler denetleniyor.");
            await OpenPrescriptionListScreenAsync(
                "form1:menuHtmlCommandExButton31", "ReceteListe.jsp", cancellationToken);
            var periods = OrderReachabilityPeriods(await ReadPrescriptionListPeriodsAsync());
            var rows = new List<MedulaPrescriptionListItem>();
            var consecutiveEmptyPeriods = 0;
            foreach (var period in periods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var periodRows = await SearchMonthlyPrescriptionPeriodCoreAsync(
                    period.Value, typeValue, cancellationToken);
                if (periodRows.Count == 0)
                {
                    consecutiveEmptyPeriods++;
                    if (MedulaPrescriptionListRefreshPolicy.ShouldStopAfterEmptyPeriod(
                            consecutiveEmptyPeriods)) break;
                    continue;
                }

                consecutiveEmptyPeriods = 0;
                rows.AddRange(periodRows);
            }
            return CompletePrescriptionListSearch(rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Medula erişilebilirlik denetimi tamamlanamadı: {exception.Message}";
            return new MedulaPrescriptionListResult(false, message, [], message);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private async Task<IReadOnlyList<MedulaPrescriptionPeriodOption>> ReadPrescriptionListPeriodsAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => [...(document.getElementById('form1:menu2')?.options ?? [])]
                .map(option => ({ value: option.value, name: (option.textContent ?? '').replace(/\s+/g, ' ').trim() }))
                .filter(option => option.value && option.name))();
            """);
        return JsonSerializer.Deserialize<MedulaPrescriptionPeriodOption[]>(raw, JsonOptions) ?? [];
    }

    internal static IReadOnlyList<MedulaPrescriptionPeriodOption> OrderReachabilityPeriods(
        IReadOnlyList<MedulaPrescriptionPeriodOption> periods)
    {
        var parsed = periods
            .Select(period => new
            {
                Period = period,
                Date = DateOnly.TryParseExact(
                    period.Value,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date)
                        ? date
                        : (DateOnly?)null
            })
            .Where(item => item.Date is not null)
            .ToArray();
        if (parsed.Length == 0) return [];
        return parsed
            .OrderByDescending(item => item.Date)
            .Select(item => item.Period)
            .ToArray();
    }

    public async Task<MedulaPrescriptionListResult> SearchActivePrescriptionsByPatientAsync(
        string patientTcIdentityNo,
        string patientDisplayName,
        CancellationToken cancellationToken = default)
    {
        patientTcIdentityNo = patientTcIdentityNo.Trim();
        if (patientTcIdentityNo.Length != 11 || !patientTcIdentityNo.All(char.IsDigit))
            return new MedulaPrescriptionListResult(false, "Geçerli bir T.C. Kimlik No girin.", []);

        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.PrescriptionList);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaPrescriptionListResult(false, "Medula bağlantısı kurulamadı.", []);
            Report(MedulaConnectionState.Connecting, "Hastanın aktif reçeteleri sorgulanıyor.");
            await OpenPrescriptionQueryScreenAsync(cancellationToken);
            var tcJson = JsonSerializer.Serialize(patientTcIdentityNo);
            var click = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => { confirmations.push(String(message ?? '')); return false; };
                    try {
                        const field = document.getElementById('form1:text4');
                        const action = document.getElementById('form1:button1');
                        if (!field || !action) return { clicked: false, messages, confirmations };
                        field.value = {{tcJson}};
                        field.dispatchEvent(new Event('input', { bubbles: true }));
                        field.dispatchEvent(new Event('change', { bubbles: true }));
                        action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """, cancellationToken);
            var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (!click.Clicked)
                return new MedulaPrescriptionListResult(false, "Medula T.C. sorgulama denetimleri bulunamadı.", []);
            if (!string.IsNullOrWhiteSpace(warning))
                return new MedulaPrescriptionListResult(false, warning, [], warning);

            await Task.Delay(120, cancellationToken);
            var rows = new List<MedulaPrescriptionListItem>();
            var page = await ReadPatientPrescriptionQueryPageAsync();
            while (true)
            {
                rows.AddRange(MapPatientPrescriptionQueryRows(page, patientTcIdentityNo, patientDisplayName));
                if (!page.HasNextPage
                    || !await MovePatientPrescriptionQueryForwardAsync(page.PageNumber, cancellationToken))
                    break;
                page = await ReadPatientPrescriptionQueryPageAsync();
            }
            var unique = rows.GroupBy(item => string.Join('\0', item.PrescriptionNumber, item.PrescriptionDate),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var message = unique.Length == 0
                ? "Hastanın Medula'da aktif reçetesi bulunamadı."
                : $"Hastanın {unique.Length} aktif reçetesi listelendi.";
            Report(MedulaConnectionState.Connected, message);
            return new MedulaPrescriptionListResult(true, message, unique);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Hastanın aktif reçeteleri alınamadı: {exception.Message}";
            ReportWarning(message);
            return new MedulaPrescriptionListResult(false, message, []);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaBrowserOperationResult> OpenPrescriptionQueryAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        using var messageScopeLease = EnterMessageScope(MedulaMessageScope.PrescriptionList);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaBrowserOperationResult(
                    false, State, "Medula bağlantısı kurulamadı.",
                    browser?.Source?.AbsoluteUri ?? LoginUrl);
            Report(MedulaConnectionState.Connecting, "Reçete Sorgu ekranı açılıyor.");
            await OpenPrescriptionQueryScreenAsync(cancellationToken);
            const string message = "Reçete Sorgu ekranı hazır.";
            Report(MedulaConnectionState.Connected, message);
            return new MedulaBrowserOperationResult(
                true, MedulaConnectionState.Connected, message,
                browser?.Source?.AbsoluteUri ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Reçete Sorgu ekranı açılamadı: {exception.Message}";
            ReportWarning(message);
            return new MedulaBrowserOperationResult(
                false, State, message, browser?.Source?.AbsoluteUri ?? string.Empty);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionSnapshot?> OpenPrescriptionFromListAsync(
        MedulaPrescriptionListItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken)) return null;
            return await OpenPrescriptionFromListCoreAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportWarning(exception.Message);
            Report(MedulaConnectionState.Failed, exception.Message);
            return null;
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public Task<MedulaPrescriptionSnapshot?> OpenPrescriptionAsync(
        MedulaPrescriptionLocator locator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (string.IsNullOrWhiteSpace(locator.PrescriptionNumber))
            throw new ArgumentException("Prescription number is required.", nameof(locator));

        var filterDate = locator.MedulaFilterDate;
        var item = new MedulaPrescriptionListItem(
            locator.PrescriptionNumber.Trim(),
            string.Empty,
            filterDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            locator.PatientDisplayName.Trim(),
            string.Empty,
            string.Empty,
            string.Empty,
            string.IsNullOrWhiteSpace(locator.ElectronicPrescriptionNumber) ? string.Empty : "I",
            string.Empty,
            string.Empty,
            string.Empty,
            locator.TypeValue,
            locator.Month is { } month && locator.Year is { } year
                ? $"{year:D4}{month:D2}01"
                : string.Empty,
            locator.Source,
            0,
            0)
        {
            PatientTcIdentityNo = locator.PatientTcIdentityNo.Trim(),
            MedulaFilterDate = filterDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            IsLiveOnMedula = true
        };
        return OpenPrescriptionFromListAsync(item, cancellationToken);
    }

    public async Task<MedulaPrescriptionSnapshot?> OpenAdjacentPrescriptionAsync(
        MedulaPrescriptionInput input,
        string actionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (actionId is not ("f:buttonOncekiRecete" or "f:buttonSonraki"))
            throw new ArgumentOutOfRangeException(nameof(actionId), actionId, "Unsupported adjacent prescription action.");
        if (input.SourceListItem is null) return null;

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken)
                || !await EnsurePrescriptionContextAsync(input, cancellationToken))
                return null;

            var originalNumber = input.SourceListItem.PrescriptionNumber.Trim();
            var restoration = BuildPrescriptionRestorationState(input, []);
            activeOperationRestorationState = restoration;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var actionIdJson = JsonSerializer.Serialize(actionId);
                var click = await ExecuteActionAndWaitAsync($$"""
                    (() => {
                        const messages = [];
                        const confirmations = [];
                        const originalAlert = window.alert;
                        const originalConfirm = window.confirm;
                        window.alert = message => messages.push(String(message ?? ''));
                        window.confirm = message => { confirmations.push(String(message ?? '')); return false; };
                        try {
                            const action = document.getElementById({{actionIdJson}});
                            if (!action) return { clicked: false, messages, confirmations };
                            action.click();
                            return { clicked: true, messages, confirmations };
                        } finally {
                            window.alert = originalAlert;
                            window.confirm = originalConfirm;
                        }
                    })();
                    """, cancellationToken, waitForNavigation: false);
                var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
                if (warning is not null)
                {
                    ReportWarning(warning);
                    return null;
                }
                if (!click.Clicked) return null;

                MedulaPrescriptionPageResult? page = null;
                for (var poll = 0; poll < MedulaSubScreenSafety.DestinationPollAttempts; poll++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await IsLoginPageAsync())
                    {
                        if (attempt == 0
                            && await RecoverFromExpiredSessionDuringOperationAsync(restoration, cancellationToken))
                            break;
                        return null;
                    }
                    if (await IsSystemErrorPageAsync())
                    {
                        if (attempt == 0
                            && await RecoverFromSystemErrorDuringOperationAsync(restoration, cancellationToken))
                            break;
                        return null;
                    }
                    if (await IsPrescriptionViewPageAsync())
                    {
                        page = await ReadPrescriptionPageResultAsync("Komşu reçete açıldı.");
                        var polledNumber = page.FieldValues?
                            .GetValueOrDefault("medula:prescription-no")?.Trim() ?? string.Empty;
                        if (polledNumber.Length > 0
                            && !polledNumber.Equals(originalNumber, StringComparison.OrdinalIgnoreCase))
                            break;
                        page = null;
                    }
                    await Task.Delay(MedulaSubScreenSafety.DestinationPollIntervalMilliseconds, cancellationToken);
                }
                if (page is null) continue;

                var fields = page.FieldValues ?? new Dictionary<string, string>();
                string Field(string key) => fields.GetValueOrDefault(key)?.Trim() ?? string.Empty;
                var firstName = Field("medula:patient-first-name");
                var lastName = Field("medula:patient-last-name");
                var openedNumber = Field("medula:prescription-no");
                var isElectronic = Field("medula:e-prescription-no").Length > 0;
                var source = input.SourceListItem with
                {
                    PrescriptionNumber = openedNumber,
                    PrescriptionDate = Field("f:t29"),
                    PatientFirstName = firstName,
                    PatientLastName = lastName,
                    Coverage = Field("medula:coverage"),
                    QrCodeStatus = Field("medula:qr-status"),
                    StatusCode = isElectronic ? "I" : "K",
                    IsLiveOnMedula = true,
                    IsStoredLocally = false,
                    PrescriptionUid = null,
                    RecordId = null,
                    VersionNumber = 0,
                    LocalSnapshot = null
                };
                var drugs = await ReadPrescriptionDrugsAsync();
                var diagnoses = await ReadPrescriptionDiagnosesAsync();
                var snapshot = new MedulaPrescriptionSnapshot(
                    page,
                    drugs,
                    diagnoses,
                    $"{firstName} {lastName}".Trim(),
                    source,
                    IsSavedOnMedula: true);
                var restoredInput = input with
                {
                    PatientIdentityNumber = Field("f:t18"),
                    RecipientIdentityNumber = Field("f:text37"),
                    Drugs = drugs,
                    Diagnoses = diagnoses,
                    SourceListItem = source,
                    ElectronicPrescriptionNumber = Field("medula:e-prescription-no"),
                    IsSavedOnMedula = true
                };
                lastRestorationState = BuildPrescriptionRestorationState(restoredInput, []);
                Report(MedulaConnectionState.Connected, $"{openedNumber} numaralı reçete açıldı.");
                return snapshot;
            }

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportWarning($"Komşu reçete açılamadı: {exception.Message}");
            return null;
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionSnapshot?> CaptureCurrentPrescriptionAsync(
        string patientDisplayName,
        MedulaPrescriptionListItem? sourceListItem,
        Guid? prescriptionUid,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            var page = await ReadPrescriptionPageResultAsync("Kayıtlı reçete verileri okundu.");
            for (var attempt = 0; attempt < 12; attempt++)
            {
                page = page with
                {
                    FieldValues = MedulaPrescriptionHeaderParser.Normalize(
                        page.FieldValues, page.PageText, sourceListItem)
                };
                var prescriptionNumber = page.FieldValues?
                    .GetValueOrDefault("medula:prescription-no");
                var savedPrescriptionExpected = sourceListItem is not null
                    || page.SavedSummary?.PaymentFields.Count > 0;
                if (MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(prescriptionNumber)
                    || !savedPrescriptionExpected
                    || attempt == 11)
                    break;
                await Task.Delay(50, cancellationToken);
                page = await ReadPrescriptionPageResultAsync("Kayıtlı reçete numarası okunuyor.");
            }
            if (!page.IsPrescriptionEntryPage && !await IsPrescriptionViewPageAsync()) return null;
            if (!page.IsPrescriptionEntryPage) page = page with { IsPrescriptionEntryPage = true };
            page = page with
            {
                FieldValues = MedulaPrescriptionHeaderParser.Normalize(
                    page.FieldValues, page.PageText, sourceListItem)
            };
            var sourceIsKnownSaved = sourceListItem is not null
                && !sourceListItem.Source.Equals(
                    "e-prescription-query", StringComparison.OrdinalIgnoreCase)
                && MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(
                    sourceListItem.PrescriptionNumber);
            var isSavedOnMedula = MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(
                page.FieldValues?.GetValueOrDefault("medula:prescription-no"))
                && (sourceIsKnownSaved || page.SavedSummary?.PaymentFields.Count > 0);
            return MedulaPrescriptionHeaderParser.Normalize(new MedulaPrescriptionSnapshot(
                page,
                await ReadPrescriptionDrugsAsync(),
                await ReadPrescriptionDiagnosesAsync(),
                patientDisplayName,
                sourceListItem,
                prescriptionUid,
                versionNumber,
                IsSavedOnMedula: isSavedOnMedula));
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionSnapshot?> RefreshPrescriptionContextAsync(
        MedulaPrescriptionInput input,
        string patientDisplayName,
        Guid? prescriptionUid,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken)
                || !await EnsurePrescriptionContextAsync(input, cancellationToken))
                return null;
            var page = await ReadPrescriptionPageResultAsync("Reçete bağlamı yenilendi.");
            page = page with
            {
                FieldValues = MedulaPrescriptionHeaderParser.Normalize(
                    page.FieldValues, page.PageText, input.SourceListItem)
            };
            return MedulaPrescriptionHeaderParser.Normalize(new MedulaPrescriptionSnapshot(
                page,
                await ReadPrescriptionDrugsAsync(),
                await ReadPrescriptionDiagnosesAsync(),
                patientDisplayName,
                input.SourceListItem,
                prescriptionUid,
                versionNumber,
                IsSavedOnMedula: input.IsSavedOnMedula));
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private async Task<MedulaPrescriptionSnapshot> OpenPrescriptionFromListCoreAsync(
        MedulaPrescriptionListItem item,
        CancellationToken cancellationToken)
    {
        Report(MedulaConnectionState.Connecting, $"{item.PrescriptionNumber} numaralı reçete açılıyor.");
        await OpenPrescriptionQueryScreenAsync(cancellationToken);
        await SubmitPrescriptionNumberQueryAsync(item.PrescriptionNumber, cancellationToken);

        var pageResult = await ReadStableOpenedPrescriptionPageAsync(item, cancellationToken);
        if (!pageResult.IsPrescriptionEntryPage && await IsPrescriptionViewPageAsync())
            pageResult = pageResult with { IsPrescriptionEntryPage = true };
        pageResult = pageResult with
        {
            FieldValues = MedulaPrescriptionHeaderParser.Normalize(
                pageResult.FieldValues, pageResult.PageText, item)
        };
        var openedPrescriptionNumber = pageResult.FieldValues?
            .GetValueOrDefault("medula:prescription-no")?.Trim() ?? string.Empty;
        if (openedPrescriptionNumber.Length > 0
            && openedPrescriptionNumber != "0"
            && !openedPrescriptionNumber.Equals(item.PrescriptionNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Medula farklı bir kayıtlı reçete açtı. Hasta gizliliği için işlem durduruldu.");
        var fields = pageResult.FieldValues ?? new Dictionary<string, string>();
        var openedPatientName = $"{fields.GetValueOrDefault("medula:patient-first-name")} "
            + fields.GetValueOrDefault("medula:patient-last-name");
        var openedPatientTc = fields.GetValueOrDefault("f:t18")?.Trim() ?? string.Empty;
        if (item.PatientTcIdentityNo.Length == 11
            && !item.PatientTcIdentityNo.Equals(openedPatientTc, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Medula aynı numarayla farklı bir hastanın reçetesini açtı. Hasta gizliliği için işlem durduruldu.");
        if (!string.IsNullOrWhiteSpace(item.PatientName)
            && !NormalizePatientName(openedPatientName).Equals(
                NormalizePatientName(item.PatientName), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Medula aynı numarayla farklı bir hastanın reçetesini açtı. Hasta gizliliği için işlem durduruldu.");
        var snapshot = MedulaPrescriptionHeaderParser.Normalize(new MedulaPrescriptionSnapshot(
            pageResult,
            await ReadPrescriptionDrugsAsync(),
            await ReadPrescriptionDiagnosesAsync(),
            item.PatientName,
            item,
            IsSavedOnMedula: true));
        lastRestorationState = BuildPrescriptionRestorationState(
            BuildElectronicPrescriptionInput(snapshot), []);
        Report(MedulaConnectionState.Connected, $"{item.PrescriptionNumber} numaralı reçete açıldı.");
        return snapshot;
    }

    private async Task<MedulaPrescriptionPageResult> ReadStableOpenedPrescriptionPageAsync(
        MedulaPrescriptionListItem source,
        CancellationToken cancellationToken)
    {
        MedulaPrescriptionPageResult? latest = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await ReadPrescriptionPageResultAsync("Kayıtlı reçete açıldı.");
            if (MedulaPrescriptionHeaderParser.HasAuthoritativeSummary(
                    latest.FieldValues, source.PrescriptionNumber))
                return latest;
            if (attempt < 7) await Task.Delay(60, cancellationToken);
        }

        latest ??= await ReadPrescriptionPageResultAsync("Kayıtlı reçete açıldı.");
        return latest with
        {
            FieldValues = MedulaPrescriptionHeaderParser.Normalize(
                latest.FieldValues, latest.PageText, source)
        };
    }

    private async Task OpenPrescriptionQueryScreenAsync(CancellationToken cancellationToken)
    {
        if (await BrowserElementExistsAsync("form1:text2")
            && await BrowserElementExistsAsync("form1:buttonReceteNoSorgula"))
            return;

        await NavigateAsync(HomeUrl, cancellationToken);
        if (!await WaitForPageStateAsync(
                () => BrowserElementExistsAsync("form1:menuHtmlCommandExButton51"), cancellationToken))
            throw new InvalidOperationException("Medula Reçete Sorgu menüsü hazırlanmadı.");
        var click = await ExecuteActionAndWaitAsync("""
            (() => {
                const action = document.getElementById('form1:menuHtmlCommandExButton51');
                if (!action) return { clicked: false, messages: [], confirmations: [] };
                action.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!click.Clicked || !await WaitForPageStateAsync(
                () => BrowserElementExistsAsync("form1:buttonReceteNoSorgula"), cancellationToken))
            throw new InvalidOperationException("Medula Reçete Sorgu ekranını açmadı.");
    }

    private async Task SubmitPrescriptionNumberQueryAsync(
        string prescriptionNumber,
        CancellationToken cancellationToken)
    {
        var numberJson = JsonSerializer.Serialize(prescriptionNumber.Trim());
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const messages = [];
                const confirmations = [];
                const originalAlert = window.alert;
                const originalConfirm = window.confirm;
                window.alert = message => messages.push(String(message ?? ''));
                window.confirm = message => { confirmations.push(String(message ?? '')); return false; };
                try {
                    const field = document.getElementById('form1:text2');
                    const action = document.getElementById('form1:buttonReceteNoSorgula');
                    if (!field || !action) return { clicked: false, messages, confirmations };
                    field.value = {{numberJson}};
                    field.dispatchEvent(new Event('input', { bubbles: true }));
                    field.dispatchEvent(new Event('change', { bubbles: true }));
                    action.click();
                    return { clicked: true, messages, confirmations };
                } finally {
                    window.alert = originalAlert;
                    window.confirm = originalConfirm;
                }
            })();
            """, cancellationToken);
        var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
        if (!string.IsNullOrWhiteSpace(warning))
            throw new InvalidOperationException(warning);
        if (!click.Clicked)
            throw new InvalidOperationException("Medula Reçete Sorgu denetimleri bulunamadı.");
        if (!await WaitForPageStateAsync(IsPrescriptionViewPageAsync, cancellationToken))
        {
            var failedPage = await ReadPrescriptionPageResultAsync("Reçete Sorgu sonuç vermedi.");
            throw new InvalidOperationException(
                failedPage.WarningText ?? failedPage.Message ?? "Medula reçeteyi açmadı.");
        }
    }

    private async Task<PatientPrescriptionQueryPageProbe> ReadPatientPrescriptionQueryPageAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const table = document.getElementById('form1:tableEx1');
                const rows = [...(table?.querySelectorAll(':scope > tbody > tr') ?? [])]
                    .map((row, rowIndex) => ({ rowIndex, cells: [...row.cells].map(cell => clean(cell.textContent)) }))
                    .filter(row => row.cells.length >= 6 && row.cells[1] && row.cells[1] !== 'Reçete No');
                const pager = document.getElementById('form1:tableEx1:web1');
                const hidden = document.getElementById('form1:tableEx1:web1__pagerWeb');
                const pageIndex = hidden ? Number(hidden.value || 0) : 0;
                const links = [...(pager?.querySelectorAll('a') ?? [])];
                const targets = links.map(link => {
                    const match = (link.getAttribute('onclick') || '').match(/pagerWeb',\s*(\d+)/);
                    return match ? Number(match[1]) : -1;
                }).filter(target => target >= 0);
                return {
                    pageNumber: pageIndex + 1,
                    hasNextPage: targets.includes(pageIndex + 1)
                        || links.some(link => /^(?:Sonraki|İleri)$/i.test(clean(link.textContent))),
                    rows
                };
            })();
            """);
        return JsonSerializer.Deserialize<PatientPrescriptionQueryPageProbe>(raw, JsonOptions)
            ?? new PatientPrescriptionQueryPageProbe(1, false, []);
    }

    private async Task<bool> MovePatientPrescriptionQueryForwardAsync(
        int currentPage,
        CancellationToken cancellationToken)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const pager = document.getElementById('form1:tableEx1:web1');
                const hidden = document.getElementById('form1:tableEx1:web1__pagerWeb');
                if (!pager || !hidden) return false;
                const currentIndex = Number(hidden.value || 0);
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const links = [...pager.querySelectorAll('a')];
                const target = links.find(link => {
                    const match = (link.getAttribute('onclick') || '').match(/pagerWeb',\s*(\d+)/);
                    return match && Number(match[1]) === currentIndex + 1;
                }) || links.find(link => /^(?:Sonraki|İleri)$/i.test(clean(link.textContent)));
                if (!target) return false;
                target.click();
                return true;
            })();
            """);
        if (!raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
        return await WaitForPageStateAsync(async () =>
            (await ReadPatientPrescriptionQueryPageAsync()).PageNumber > currentPage, cancellationToken);
    }

    private static IReadOnlyList<MedulaPrescriptionListItem> MapPatientPrescriptionQueryRows(
        PatientPrescriptionQueryPageProbe page,
        string patientTcIdentityNo,
        string patientDisplayName)
    {
        var parts = patientDisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var firstName = parts.Length <= 1 ? patientDisplayName.Trim() : string.Join(' ', parts[..^1]);
        var lastName = parts.Length <= 1 ? string.Empty : parts[^1];
        return page.Rows.Select(row => new MedulaPrescriptionListItem(
            row.Cells.ElementAtOrDefault(1) ?? string.Empty,
            row.Cells.ElementAtOrDefault(4) ?? string.Empty,
            row.Cells.ElementAtOrDefault(2) ?? string.Empty,
            firstName,
            lastName,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "patient-query",
            page.PageNumber,
            row.RowIndex)
        {
            MedulaFilterDate = row.Cells.ElementAtOrDefault(4) ?? string.Empty,
            IsLiveOnMedula = true,
            PatientTcIdentityNo = patientTcIdentityNo,
            SectionTitle = "Hastanın Aktif Reçeteleri"
        }).ToArray();
    }

    private async Task<MedulaRemoteScreen> OpenPatientMedicationListFromDailyPrescriptionCoreAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken)
    {
        if (!await OpenAnyRecentDailyPrescriptionHostAsync(cancellationToken))
            return EmptyRemoteScreen(
                "patient-medication-list",
                "Hasta İlaç Listesi",
                "Medula Reçete Listesi Günlük ekranında ilaç listesini açmak için kullanılabilecek bir reçete bulunamadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);

        var patientTcJson = JsonSerializer.Serialize(patientTcIdentityNo);
        var warningBeforeClick = await ReadVisibleMedulaWarningAsync();
        var warningRevision = GetWarningRevision();
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const messages = [];
                const confirmations = [];
                const originalAlert = window.alert;
                const originalConfirm = window.confirm;
                window.alert = message => messages.push(String(message ?? ''));
                window.confirm = message => {
                    confirmations.push(String(message ?? ''));
                    return false;
                };
                try {
                    const patientTc = document.getElementById('f:t18');
                    const drugs = document.getElementById('f:buttonIlacListesi');
                    if (!patientTc || !drugs)
                        return { clicked: false, messages, confirmations };
                    patientTc.value = {{patientTcJson}};
                    patientTc.setAttribute('value', {{patientTcJson}});
                    patientTc.dispatchEvent(new Event('input', { bubbles: true }));
                    patientTc.dispatchEvent(new Event('change', { bubbles: true }));
                    drugs.click();
                    return { clicked: true, messages, confirmations };
                } finally {
                    window.alert = originalAlert;
                    window.confirm = originalConfirm;
                }
            })();
            """, cancellationToken, waitForNavigation: false);
        if (!click.Clicked)
            return EmptyRemoteScreen(
                "patient-medication-list",
                "Hasta İlaç Listesi",
                "Günlük reçete açıldı ancak Reçete Sahibi T.C. veya İlaç denetimi bulunamadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);

        if (MedulaBrowserWarning.First(click.Messages, click.Confirmations) is { } immediateWarning)
            throw new InvalidOperationException(immediateWarning);

        var destination = await WaitForExpectedSubScreenAsync(
            "drugs", warningBeforeClick, warningRevision, cancellationToken);
        if (destination.SessionExpiredDetected)
            throw new InvalidOperationException("Medula oturumu sona erdi.");
        if (destination.SystemErrorDetected)
            throw new InvalidOperationException(
                destination.Warning ?? "Medula Sistem Hatası oluştu.");
        if (!destination.DestinationReached)
            return EmptyRemoteScreen(
                "patient-medication-list",
                "Hasta İlaç Listesi",
                destination.Warning ?? "Medula Hasta İlaç Listesi ekranını açmadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);

        var screen = await ReadRemoteScreenAsync(
            "drugs",
            "Hasta İlaç Listesi",
            "Hastanın Medula ilaç listesi hazır.");
        return screen with
        {
            Key = "patient-medication-list",
            Title = "Hasta İlaç Listesi"
        };
    }

    private async Task<MedulaRemoteScreen> OpenPatientReportsFromDailyPrescriptionCoreAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken)
    {
        if (!await OpenAnyRecentDailyPrescriptionHostAsync(cancellationToken))
            return EmptyRemoteScreen(
                "reports",
                "Hasta Raporları",
                "Medula Reçete Listesi Günlük ekranında raporları açmak için kullanılabilecek bir reçete bulunamadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);

        var patientTcJson = JsonSerializer.Serialize(patientTcIdentityNo);
        var warningBeforeClick = await ReadVisibleMedulaWarningAsync();
        var warningRevision = GetWarningRevision();
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const messages = [];
                const confirmations = [];
                const originalAlert = window.alert;
                const originalConfirm = window.confirm;
                window.alert = message => messages.push(String(message ?? ''));
                window.confirm = message => {
                    confirmations.push(String(message ?? ''));
                    return false;
                };
                try {
                    const patientTc = document.getElementById('f:t18');
                    const reports = document.getElementById('f:buttonRaporListesi');
                    if (!patientTc || !reports)
                        return { clicked: false, messages, confirmations };
                    patientTc.value = {{patientTcJson}};
                    patientTc.setAttribute('value', {{patientTcJson}});
                    patientTc.dispatchEvent(new Event('input', { bubbles: true }));
                    patientTc.dispatchEvent(new Event('change', { bubbles: true }));
                    reports.click();
                    return { clicked: true, messages, confirmations };
                } finally {
                    window.alert = originalAlert;
                    window.confirm = originalConfirm;
                }
            })();
            """, cancellationToken, waitForNavigation: false);
        if (!click.Clicked)
            return EmptyRemoteScreen(
                "reports",
                "Hasta Raporları",
                "Günlük reçete açıldı ancak Reçete Sahibi T.C. veya Rapor denetimi bulunamadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);

        if (MedulaBrowserWarning.First(click.Messages, click.Confirmations) is { } immediateWarning)
            throw new InvalidOperationException(immediateWarning);

        var destination = await WaitForExpectedSubScreenAsync(
            "reports", warningBeforeClick, warningRevision, cancellationToken);
        if (destination.SessionExpiredDetected)
            throw new InvalidOperationException("Medula oturumu sona erdi.");
        if (destination.SystemErrorDetected)
            throw new InvalidOperationException(
                destination.Warning ?? "Medula Sistem Hatası oluştu.");
        if (!destination.DestinationReached)
            return EmptyRemoteScreen(
                "reports",
                "Hasta Raporları",
                destination.Warning ?? "Medula Hasta Raporları ekranını açmadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);

        return await ReadRemoteScreenAsync(
            "reports",
            "Hasta Raporları",
            "Hastanın Medula raporları hazır.");
    }

    private async Task<bool> OpenAnyRecentDailyPrescriptionHostAsync(
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        string[] commonTypeValues = ["4", "1"];
        for (var dayOffset = 0; dayOffset < 14; dayOffset++)
        {
            foreach (var typeValue in commonTypeValues)
                if (await TryOpenFirstDailyPrescriptionAsync(
                        today.AddDays(-dayOffset), typeValue, cancellationToken))
                    return true;
        }

        string[] fallbackTypeValues = ["28", "7", "25", "16", "19", "10", "13", "99", "22", "31", "34", "37", "40", "43"];
        for (var dayOffset = 0; dayOffset < 7; dayOffset++)
        {
            foreach (var typeValue in fallbackTypeValues)
                if (await TryOpenFirstDailyPrescriptionAsync(
                        today.AddDays(-dayOffset), typeValue, cancellationToken))
                    return true;
        }

        return false;
    }

    private async Task<bool> TryOpenFirstDailyPrescriptionAsync(
        DateOnly date,
        string typeValue,
        CancellationToken cancellationToken)
    {
        await PrepareDailyPrescriptionListAsync(date, typeValue, cancellationToken);
        var page = await ReadPrescriptionListPageAsync();
        var row = page.Rows.FirstOrDefault();
        if (row is null) return false;

        var rowIndexJson = JsonSerializer.Serialize(row.RowIndex);
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const table = document.getElementById('form1:tableExReceteList');
                const rows = [...(table?.querySelectorAll(':scope > tbody > tr') ?? [])];
                const row = rows[{{rowIndexJson}}];
                if (!row) return { clicked: false, messages: [], confirmations: [] };
                const clickable = [...row.querySelectorAll(
                        'a,input[type="submit"],input[type="image"],button')]
                    .find(element => !element.disabled)
                    || [row, ...row.querySelectorAll('*')]
                        .find(element => typeof element.onclick === 'function');
                if (clickable) clickable.click();
                else row.dispatchEvent(new MouseEvent(
                    'click', { bubbles: true, cancelable: true, view: window }));
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!click.Clicked) return false;
        if (MedulaBrowserWarning.First(click.Messages, click.Confirmations) is { } warning)
            throw new InvalidOperationException(warning);
        if (!await WaitForPageStateAsync(IsPrescriptionViewPageAsync, cancellationToken))
            throw new InvalidOperationException(
                "Medula Reçete Listesi Günlük kaydını açmadı.");
        return true;
    }

    private async Task<IReadOnlyList<MedulaPrescriptionListItem>> SearchDailyPrescriptionListCoreAsync(
        DateOnly date,
        string typeValue,
        CancellationToken cancellationToken)
    {
        await PrepareDailyPrescriptionListAsync(date, typeValue, cancellationToken);
        var result = new List<MedulaPrescriptionListItem>();
        var page = await ReadPrescriptionListPageAsync();
        var monthValue = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)[..6] + "01";
        while (true)
        {
            result.AddRange(MapPrescriptionListRows(page, typeValue, monthValue, "daily", date));
            if (!page.HasNextPage) break;
            if (!await MovePrescriptionListForwardAsync(page.PageNumber, cancellationToken)) break;
            page = await ReadPrescriptionListPageAsync();
        }
        return result;
    }

    private async Task<IReadOnlyList<MedulaPrescriptionListItem>> SearchMonthlyPrescriptionPeriodCoreAsync(
        string monthValue,
        string typeValue,
        CancellationToken cancellationToken)
    {
        var result = new List<MedulaPrescriptionListItem>();
        await PrepareMonthlyPrescriptionListAsync(monthValue, typeValue, cancellationToken);
        var page = await ReadPrescriptionListPageAsync();
        while (true)
        {
            result.AddRange(MapPrescriptionListRows(page, typeValue, monthValue, "monthly", null));
            if (!page.HasNextPage) break;
            if (!await MovePrescriptionListForwardAsync(page.PageNumber, cancellationToken)) break;
            page = await ReadPrescriptionListPageAsync();
        }
        return result;
    }

    private MedulaPrescriptionListResult CompletePrescriptionListSearch(
        IReadOnlyList<MedulaPrescriptionListItem> rows)
    {
        var uniqueRows = rows
            .GroupBy(row => string.Join('\0',
                row.PrescriptionNumber,
                row.MedulaFilterDate,
                row.PatientFirstName,
                row.PatientLastName,
                row.TypeValue), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var message = uniqueRows.Length == 0
            ? "Seçilen filtreler için kayıt bulunamadı."
            : $"{uniqueRows.Length} reçete listelendi.";
        Report(MedulaConnectionState.Connected, message);
        return new MedulaPrescriptionListResult(true, message, uniqueRows);
    }

    private async Task<IReadOnlyList<MedulaPrescriptionListItem>> SearchMonthlyPrescriptionListCoreAsync(
        DateOnly startDate,
        DateOnly endDate,
        string typeValue,
        CancellationToken cancellationToken)
    {
        var result = new List<MedulaPrescriptionListItem>();
        var month = new DateOnly(startDate.Year, startDate.Month, 1);
        var finalMonth = new DateOnly(endDate.Year, endDate.Month, 1);
        while (month <= finalMonth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var monthValue = month.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            await PrepareMonthlyPrescriptionListAsync(monthValue, typeValue, cancellationToken);
            var page = await ReadPrescriptionListPageAsync();
            while (true)
            {
                result.AddRange(MapPrescriptionListRows(page, typeValue, monthValue, "monthly", null)
                    .Where(item => ParseMedulaListDate(item.PrescriptionDate) is { } date
                        && date >= startDate && date <= endDate));
                if (!page.HasNextPage) break;
                if (!await MovePrescriptionListForwardAsync(page.PageNumber, cancellationToken)) break;
                page = await ReadPrescriptionListPageAsync();
            }
            month = month.AddMonths(1);
        }
        return result;
    }

    private async Task PrepareMonthlyPrescriptionListAsync(
        string monthValue,
        string typeValue,
        CancellationToken cancellationToken)
    {
        await OpenPrescriptionListScreenAsync("form1:menuHtmlCommandExButton31", "ReceteListe.jsp", cancellationToken);
        await SetListFiltersAndClickAsync(typeValue, monthValue, null, "form1:buttonSonlandirilmamisReceteler", cancellationToken);
    }

    private async Task PrepareDailyPrescriptionListAsync(
        DateOnly date,
        string typeValue,
        CancellationToken cancellationToken)
    {
        await OpenPrescriptionListScreenAsync("form1:menuHtmlCommandExButton41", "ReceteListeGunluk.jsp", cancellationToken);
        var monthValue = new DateOnly(date.Year, date.Month, 1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        await SetListFiltersAndClickAsync(typeValue, monthValue, null, "form1:buttonSonlandirilmamisReceteler", cancellationToken);
        if (!await WaitForPageStateAsync(
                () => BrowserElementExistsAsync("form1:menu3"), cancellationToken))
            throw new InvalidOperationException("Medula günlük reçete filtresini açmadı.");
        await SetListFiltersAndClickAsync(typeValue, monthValue, date.Day.ToString(CultureInfo.InvariantCulture),
            "form1:buttonReceteSorgula", cancellationToken);
    }

    private async Task OpenPrescriptionListScreenAsync(
        string menuId,
        string urlSuffix,
        CancellationToken cancellationToken)
    {
        if (!await ConnectWithRetriesAsync(cancellationToken))
            throw new InvalidOperationException("Medula bağlantısı kurulamadı.");
        if (browser?.Source?.AbsoluteUri.Contains(urlSuffix, StringComparison.OrdinalIgnoreCase) == true
            && await BrowserElementExistsAsync("form1:buttonSonlandirilmamisReceteler"))
            return;
        if (!await WaitForPageStateAsync(() => BrowserElementExistsAsync(menuId), cancellationToken))
        {
            if (await BrowserElementExistsAsync("f:buttonAnaSayfa"))
            {
                var homeClick = await ExecuteActionAndWaitAsync("""
                    (() => {
                        const action = document.getElementById('f:buttonAnaSayfa');
                        if (!action) return { clicked: false, messages: [], confirmations: [] };
                        action.click();
                        return { clicked: true, messages: [], confirmations: [] };
                    })();
                    """, cancellationToken);
                if (!homeClick.Clicked) throw new InvalidOperationException("Medula Ana Sayfa düğmesi tıklanamadı.");
            }
            else
                await NavigateAsync(HomeUrl, cancellationToken);
            if (!await WaitForPageStateAsync(() => BrowserElementExistsAsync(menuId), cancellationToken))
                throw new InvalidOperationException("Medula ana menüsü hazırlanmadı.");
        }
        var idJson = JsonSerializer.Serialize(menuId);
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const action = document.getElementById({{idJson}});
                if (!action) return { clicked: false, messages: [], confirmations: [] };
                action.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!click.Clicked) throw new InvalidOperationException("Medula reçete listesi menüsü tıklanamadı.");
        if (!await WaitForPageStateAsync(
                () => BrowserElementExistsAsync("form1:buttonSonlandirilmamisReceteler"), cancellationToken))
            throw new InvalidOperationException(
                $"Medula reçete listesi denetimleri hazırlanmadı. Geçerli adres: {await ReadCurrentUrlAsync()}");
    }

    private async Task SetListFiltersAndClickAsync(
        string typeValue,
        string monthValue,
        string? dayValue,
        string buttonId,
        CancellationToken cancellationToken)
    {
        var typeJson = JsonSerializer.Serialize(typeValue);
        var monthJson = JsonSerializer.Serialize(monthValue);
        var dayJson = JsonSerializer.Serialize(dayValue);
        var buttonJson = JsonSerializer.Serialize(buttonId);
        var raw = await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                const set = (id, value) => {
                    const field = document.getElementById(id);
                    if (!field || value == null || ![...field.options].some(option => option.value === value)) return value == null;
                    field.value = value;
                    field.dispatchEvent(new Event('change', { bubbles: true }));
                    return true;
                };
                if (!set('form1:menu1', {{typeJson}}) || !set('form1:menu2', {{monthJson}}) || !set('form1:menu3', {{dayJson}}))
                    return false;
                const action = document.getElementById({{buttonJson}});
                if (!action) return false;
                action.click();
                return true;
            })();
            """);
        if (!raw.Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Medula reçete listesi filtreleri uygulanamadı.");
        await Task.Delay(120, cancellationToken);
        await WaitForPageStateAsync(
            async () => (await ReadPrescriptionListPageAsync()).IsReady,
            cancellationToken);
    }

    private async Task<bool> MovePrescriptionListForwardAsync(
        int currentPage,
        CancellationToken cancellationToken)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const action = document.getElementById('form1:buttonIleri');
                if (action && !action.disabled) {
                    action.click();
                    return true;
                }
                const pager = document.getElementById('form1:tableExReceteList:web1');
                const hidden = document.getElementById('form1:tableExReceteList:web1__pagerWeb');
                if (!pager || !hidden) return false;
                const currentIndex = Number(hidden.value || 0);
                const links = [...pager.querySelectorAll('a')];
                const targetFor = link => {
                    const match = (link.getAttribute('onclick') || '').match(/pagerWeb',\s*(\d+)/);
                    return match ? Number(match[1]) : -1;
                };
                const target = links.find(link => targetFor(link) === currentIndex + 1)
                    || links.find(link => /^(?:Sonraki|İleri)$/i.test(
                        (link.textContent || '').replace(/\s+/g, ' ').trim()));
                if (!target) return false;
                target.click();
                return true;
            })();
            """);
        if (!raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
        return await WaitForPageStateAsync(async () =>
            (await ReadPrescriptionListPageAsync()).PageNumber > currentPage, cancellationToken);
    }

    private async Task<PrescriptionListPageProbe> ReadPrescriptionListPageAsync()
    {
        const string script = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const table = document.getElementById('form1:tableExReceteList');
                const rows = [...(table?.querySelectorAll(':scope > tbody > tr') ?? [])]
                    .map((row, rowIndex) => ({ rowIndex, cells: [...row.cells].map(cell => clean(cell.textContent)) }))
                    .filter(row => row.cells.length >= 12 && row.cells[1]);
                const pageText = clean(document.getElementById('form1:box1')?.textContent);
                const match = pageText.match(/(\d+)\s*\/\s*(\d+)/);
                const pager = document.getElementById('form1:tableExReceteList:web1');
                const hidden = document.getElementById('form1:tableExReceteList:web1__pagerWeb');
                const pagerIndex = hidden ? Number(hidden.value || 0) : -1;
                const links = [...(pager?.querySelectorAll('a') ?? [])];
                const targets = links.map(link => {
                    const target = (link.getAttribute('onclick') || '').match(/pagerWeb',\s*(\d+)/);
                    return target ? Number(target[1]) : -1;
                }).filter(target => target >= 0);
                const pageNumber = pagerIndex >= 0 ? pagerIndex + 1 : match ? Number(match[1]) : 1;
                const totalPages = pagerIndex >= 0
                    ? Math.max(pageNumber, ...targets.map(target => target + 1))
                    : match ? Number(match[2]) : 1;
                const hasNextPage = pagerIndex >= 0
                    ? targets.includes(pagerIndex + 1) || links.some(link => /^(?:Sonraki|İleri)$/i.test(clean(link.textContent)))
                    : !!match && Number(match[1]) < Number(match[2]);
                return {
                    isReady: !!table || !!document.getElementById('form1:buttonSonlandirilmamisReceteler'),
                    pageNumber,
                    totalPages,
                    hasNextPage,
                    rows
                };
            })();
            """;
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<PrescriptionListPageProbe>(raw, JsonOptions)
            ?? new PrescriptionListPageProbe(false, 1, 1, false, []);
    }

    private static IReadOnlyList<MedulaPrescriptionListItem> MapPrescriptionListRows(
        PrescriptionListPageProbe page,
        string typeValue,
        string monthValue,
        string source,
        DateOnly? filterDate) =>
        page.Rows.Select(row => new MedulaPrescriptionListItem(
            row.Cells.ElementAtOrDefault(1) ?? string.Empty,
            row.Cells.ElementAtOrDefault(2) ?? string.Empty,
            row.Cells.ElementAtOrDefault(3) ?? string.Empty,
            row.Cells.ElementAtOrDefault(4) ?? string.Empty,
            row.Cells.ElementAtOrDefault(5) ?? string.Empty,
            row.Cells.ElementAtOrDefault(6) ?? string.Empty,
            row.Cells.ElementAtOrDefault(7) ?? string.Empty,
            row.Cells.ElementAtOrDefault(8) ?? string.Empty,
            row.Cells.ElementAtOrDefault(9) ?? string.Empty,
            row.Cells.ElementAtOrDefault(10) ?? string.Empty,
            row.Cells.ElementAtOrDefault(11) ?? string.Empty,
            typeValue,
            monthValue,
            source,
            page.PageNumber,
            row.RowIndex)
        {
            MedulaFilterDate = filterDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                ?? (row.Cells.ElementAtOrDefault(2) ?? string.Empty),
            IsLiveOnMedula = true
        }).ToArray();

    private static DateOnly? ParseMedulaListDate(string value) =>
        DateOnly.TryParseExact(value.Trim(), ["dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    private static string NormalizePatientName(string value) =>
        CultureInfo.GetCultureInfo("tr-TR").TextInfo.ToUpper(
            string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    private async Task<bool> BrowserElementExistsAsync(string id)
    {
        var idJson = JsonSerializer.Serialize(id);
        var raw = await RequireBrowser().ExecuteScriptAsync($"!!document.getElementById({idJson})");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ReadCurrentUrlAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("location.href");
        return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
    }

    private async Task<MedulaPrescriptionPageResult> OpenPrescriptionEntryCoreAsync(
        CancellationToken cancellationToken)
    {
        hydratedPrescriptionDrugRows.Clear();
        if (!await ConnectWithRetriesAsync(cancellationToken))
            return new MedulaPrescriptionPageResult(
                false, false, "Medula", browser?.Source?.AbsoluteUri ?? LoginUrl,
                "Medula bağlantısı kurulamadı. Bağlantı durumunu ve Ayarlar ekranını kontrol edin.", string.Empty);

        Report(MedulaConnectionState.Connecting, "Medula Reçete Giriş ekranı açılıyor.");

        const string openScript = """
            (() => {
                const button = document.getElementById('form1:menuHtmlCommandExButton21');
                if (!button) return { clicked: false, messages: [], confirmations: [] };
                button.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await IsPrescriptionEntryPageAsync())
            {
                var ready = await ReadPrescriptionPageResultAsync("Reçete Giriş ekranı hazır.");
                Report(MedulaConnectionState.Connected, "Reçete Giriş ekranı hazır.");
                return ready;
            }

            if (!await HasPrescriptionEntryMenuAsync())
            {
                await NavigateAsync(HomeUrl, cancellationToken);
                if (!await WaitForPageStateAsync(HasPrescriptionEntryMenuAsync, cancellationToken))
                    continue;
            }

            var click = await ExecuteActionAndWaitAsync(openScript, cancellationToken);
            if (click.Clicked
                && await WaitForPageStateAsync(IsPrescriptionEntryPageAsync, cancellationToken))
            {
                var ready = await ReadPrescriptionPageResultAsync("Reçete Giriş ekranı hazır.");
                Report(MedulaConnectionState.Connected, "Reçete Giriş ekranı hazır.");
                return ready;
            }
        }

        var failed = await ReadPrescriptionPageResultAsync("Medula Reçete Giriş ekranını açmadı.");
        Report(MedulaConnectionState.Failed, "Medula Reçete Giriş ekranını açmadı.");
        return failed;
    }

    private async Task<MedulaPrescriptionPageResult> RecoverPrescriptionEntryIfRequiredAsync(
        MedulaPrescriptionPageResult result,
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken)
    {
        if (!MedulaSubScreenSafety.RequiresPrescriptionReload(result.Message)
            && !MedulaSubScreenSafety.RequiresPrescriptionReload(result.PageText))
            return result;

        var reloaded = await ReloadPrescriptionContextAsync(input, cancellationToken);
        return reloaded.IsPrescriptionEntryPage
            ? reloaded with
            {
                IsSuccess = false,
                Message = "Medula Reçete Giriş ekranı otomatik olarak yenilendi. İşlemi tekrar deneyin."
            }
            : result;
    }

    private async Task<MedulaPrescriptionPageResult> ReloadPrescriptionContextAsync(
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken)
    {
        switch (MedulaPrescriptionAccessPolicy.Resolve(input))
        {
            case MedulaPrescriptionAccessRoute.PrescriptionQuery:
                if (input.SourceListItem is not { } savedSource
                    || !MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(
                        savedSource.PrescriptionNumber))
                    return new MedulaPrescriptionPageResult(
                        false, false, "Kayıtlı Reçete", browser?.Source?.AbsoluteUri ?? string.Empty,
                        "Kayıtlı reçetenin Medula reçete numarası bulunamadı.", string.Empty);
                return (await OpenPrescriptionFromListCoreAsync(
                    savedSource, cancellationToken)).Page;
            case MedulaPrescriptionAccessRoute.ElectronicPrescriptionQuery:
                if (await EnsureElectronicPrescriptionDraftContextAsync(input, cancellationToken))
                    return await ReadPrescriptionPageResultAsync(
                        "Kaydedilmemiş e-reçete E-Reçete Sorgu üzerinden geri yüklendi.");
                return new MedulaPrescriptionPageResult(
                    false, false, "E-Reçete", browser?.Source?.AbsoluteUri ?? string.Empty,
                    "Kaydedilmemiş e-reçete E-Reçete Sorgu üzerinden geri yüklenemedi.",
                    string.Empty);
            default:
                return await ReloadPrescriptionEntryThroughHomeAsync(cancellationToken);
        }
    }

    private async Task<bool> EnsurePrescriptionContextAsync(
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken)
    {
        switch (MedulaPrescriptionAccessPolicy.Resolve(input))
        {
            case MedulaPrescriptionAccessRoute.PrescriptionQuery:
                if (input.SourceListItem is not { } savedSource
                    || !MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(
                        savedSource.PrescriptionNumber))
                    return false;
                if (await IsExactSavedPrescriptionOpenAsync(input)) return true;
                var restored = await OpenPrescriptionFromListCoreAsync(
                    savedSource, cancellationToken);
                return restored.Page.IsPrescriptionEntryPage
                    && await IsExactSavedPrescriptionOpenAsync(input);
            case MedulaPrescriptionAccessRoute.ElectronicPrescriptionQuery:
                return await EnsureElectronicPrescriptionDraftContextAsync(input, cancellationToken);
            default:
                if (await IsPrescriptionEntryPageAsync()) return true;
                return (await OpenPrescriptionEntryCoreAsync(cancellationToken)).IsPrescriptionEntryPage;
        }
    }

    private async Task<bool> EnsureElectronicPrescriptionDraftContextAsync(
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken)
    {
        if (await IsExactElectronicPrescriptionDraftOpenAsync(input)) return true;
        var state = BuildElectronicPrescriptionDraftRestoration(input);
        await RestoreElectronicPrescriptionStateAsync(state, cancellationToken);
        if (!await IsExactElectronicPrescriptionDraftOpenAsync(input)) return false;
        await ApplyPrescriptionInputAsync(input, cancellationToken);
        if (!systemErrorRecoveryInProgress)
            lastRestorationState = new MedulaRestorationState(
                input, [], ElectronicPrescription: state);
        return true;
    }

    private async Task<bool> IsExactElectronicPrescriptionDraftOpenAsync(
        MedulaPrescriptionInput input)
    {
        if (!await IsPrescriptionEntryPageAsync()) return false;
        var page = await ReadPrescriptionPageResultAsync("Kaydedilmemiş e-reçete doğrulandı.");
        var fields = page.FieldValues ?? new Dictionary<string, string>();
        var openedElectronicNumber = fields
            .GetValueOrDefault("medula:e-prescription-no")?.Trim() ?? string.Empty;
        var openedPatient = fields.GetValueOrDefault("f:t18")?.Trim() ?? string.Empty;
        return openedElectronicNumber.Equals(
                input.ElectronicPrescriptionNumber.Trim(), StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(input.PatientIdentityNumber)
                || openedPatient.Equals(input.PatientIdentityNumber.Trim(), StringComparison.Ordinal));
    }

    private MedulaElectronicPrescriptionRestoration BuildElectronicPrescriptionDraftRestoration(
        MedulaPrescriptionInput input)
    {
        var previous = lastRestorationState.ElectronicPrescription;
        var sameElectronicPrescription = previous is not null
            && previous.ElectronicPrescriptionNumber.Equals(
                input.ElectronicPrescriptionNumber.Trim(), StringComparison.OrdinalIgnoreCase);
        return new MedulaElectronicPrescriptionRestoration(
            input.PatientIdentityNumber.Trim(),
            input.ElectronicPrescriptionNumber.Trim().ToUpperInvariant(),
            sameElectronicPrescription ? previous!.TrackingNumber : string.Empty,
            sameElectronicPrescription ? previous!.PrescriptionDate : input.PrescriptionDate,
            sameElectronicPrescription && previous!.SearchByTrackingNumber,
            DetailOpen: true,
            PrescriptionOpen: true);
    }

    private MedulaRestorationState BuildPrescriptionRestorationState(
        MedulaPrescriptionInput input,
        IReadOnlyList<MedulaRestorationStep> steps,
        bool applyInput = true)
    {
        var electronicPrescription = MedulaPrescriptionAccessPolicy.Resolve(input)
            == MedulaPrescriptionAccessRoute.ElectronicPrescriptionQuery
                ? BuildElectronicPrescriptionDraftRestoration(input)
                : null;
        return new MedulaRestorationState(
            input, steps, applyInput, electronicPrescription);
    }

    private async Task<bool> IsExactSavedPrescriptionOpenAsync(MedulaPrescriptionInput input)
    {
        if (input.SourceListItem is not { } sourceItem || !await IsPrescriptionEntryPageAsync())
            return false;
        var page = await ReadPrescriptionPageResultAsync("Kayıtlı reçete doğrulandı.");
        var openedNumber = page.FieldValues?.GetValueOrDefault("medula:prescription-no")?.Trim() ?? string.Empty;
        var openedPatient = page.FieldValues?.GetValueOrDefault("f:t18")?.Trim() ?? string.Empty;
        if (!openedNumber.Equals(sourceItem.PrescriptionNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(input.PatientIdentityNumber)
            || openedPatient.Equals(input.PatientIdentityNumber.Trim(), StringComparison.Ordinal);
    }

    private async Task<MedulaPrescriptionPageResult> ReloadPrescriptionEntryThroughHomeAsync(
        CancellationToken cancellationToken)
    {
        const string homeScript = """
            (() => {
                const messages = [];
                const confirmations = [];
                const action = document.getElementById('f:buttonAnaSayfa');
                if (!action) return { clicked: false, messages, confirmations };
                const originalAlert = window.alert;
                const originalConfirm = window.confirm;
                window.alert = message => messages.push(String(message ?? ''));
                window.confirm = message => {
                    confirmations.push(String(message ?? ''));
                    return false;
                };
                try {
                    action.click();
                    return { clicked: true, messages, confirmations };
                } finally {
                    window.alert = originalAlert;
                    window.confirm = originalConfirm;
                }
            })();
            """;

        var homeClick = await ExecuteActionAndWaitAsync(homeScript, cancellationToken);
        if (!homeClick.Clicked)
            await NavigateAsync(HomeUrl, cancellationToken);
        if (!await WaitForPageStateAsync(HasPrescriptionEntryMenuAsync, cancellationToken))
            await NavigateAsync(HomeUrl, cancellationToken);
        return await OpenPrescriptionEntryCoreAsync(cancellationToken);
    }

    public async Task<MedulaPrescriptionPageResult> ExecutePrescriptionActionAsync(
        MedulaPrescriptionInput input,
        string actionId,
        string actionCaption,
        bool confirmationAccepted = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!PrescriptionActionIds.Contains(actionId))
            throw new ArgumentOutOfRangeException(nameof(actionId), actionId, "Unsupported Medula prescription action.");

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaPrescriptionPageResult(
                    false, false, "Medula", browser?.Source?.AbsoluteUri ?? LoginUrl,
                    "Medula bağlantısı kurulamadı. Bağlantı durumunu ve Ayarlar ekranını kontrol edin.", string.Empty);

            var accessRoute = MedulaPrescriptionAccessPolicy.Resolve(input);
            if (accessRoute != MedulaPrescriptionAccessRoute.PrescriptionEntry)
            {
                if (!await EnsurePrescriptionContextAsync(input, cancellationToken))
                    return new MedulaPrescriptionPageResult(
                        false, false, "Reçete", browser?.Source?.AbsoluteUri ?? string.Empty,
                        "Reçete doğru Medula sorgu ekranından güvenli biçimde geri yüklenemedi. İşlem uygulanmadı.",
                        string.Empty);
            }
            else if (!await IsPrescriptionEntryPageAsync())
            {
                var reloaded = await OpenPrescriptionEntryCoreAsync(cancellationToken);
                return reloaded.IsPrescriptionEntryPage
                    ? reloaded with
                    {
                        IsSuccess = false,
                        Message = "Medula Reçete Giriş ekranı otomatik olarak yenilendi. İşlemi tekrar deneyin."
                    }
                    : reloaded;
            }

            // Medula can leave a JSF overlay open after reporting an error. Remove that
            // blocking layer before dispatching any later prescription command.
            await ReturnToPrescriptionCoreAsync(cancellationToken);
            if (!await IsPrescriptionEntryPageAsync())
                return new MedulaPrescriptionPageResult(
                    false, false, "Reçete", browser?.Source?.AbsoluteUri ?? string.Empty,
                    "Medula'daki açık pencere kapatılamadı. İşlem uygulanmadı.", string.Empty);

            var requiresTrustedClick = SavedScreenActions.ContainsKey(actionId);
            var actionRestoration = BuildPrescriptionRestorationState(
                input, [], !requiresTrustedClick);
            activeOperationRestorationState = actionRestoration;
            if (!requiresTrustedClick)
                await ApplyPrescriptionInputAsync(input, cancellationToken);
            var payload = BuildPrescriptionPayload(input);
            var valuesJson = JsonSerializer.Serialize(payload.Values);
            var checksJson = JsonSerializer.Serialize(payload.Checks);
            var actionIdJson = JsonSerializer.Serialize(actionId);
            var confirmationJson = confirmationAccepted ? "true" : "false";
            var clickInDocumentJson = requiresTrustedClick ? "false" : "true";
            var isPrescriptionSaveAction = actionId == "f:buttonReceteKaydet";
            var saveWarningBeforeAction = isPrescriptionSaveAction
                ? await ReadVisibleMedulaWarningAsync()
                : null;
            var saveWarningRevision = GetWarningRevision();
            var script = $$"""
                (() => {
                    const values = {{valuesJson}};
                    const checks = {{checksJson}};
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => {
                        if (!{{confirmationJson}}) confirmations.push(String(message ?? ''));
                        return {{confirmationJson}};
                    };
                    for (const [id, value] of Object.entries(values)) {
                        const element = document.getElementById(id);
                        if (!element) continue;
                        element.value = value ?? '';
                    }
                    for (const [id, checked] of Object.entries(checks)) {
                        const element = document.getElementById(id);
                        if (!element) continue;
                        element.checked = !!checked;
                    }
                    const requestedActionId = {{actionIdJson}};
                    const action = document.getElementById(requestedActionId)
                        ?? (requestedActionId === 'f:buttonReceteKaydet'
                            ? document.getElementById('f:button2')
                            : requestedActionId === 'medula:buttonKarekodSonlandir'
                                ? [...document.querySelectorAll(
                                    'button,input[type=button],input[type=submit],input[type=image]')]
                                    .find(element => String(element.value || element.alt || element.title
                                        || element.innerText || element.textContent || '')
                                        .replace(/\s+/g, ' ').trim().toLocaleLowerCase('tr-TR')
                                        === 'karekod sonlandır')
                            : requestedActionId === 'medula:buttonKarekodSonlandirmaIptal'
                                ? [...document.querySelectorAll(
                                    'button,input[type=button],input[type=submit],input[type=image]')]
                                    .find(element => String(element.value || element.alt || element.title
                                        || element.innerText || element.textContent || '')
                                        .replace(/\s+/g, ' ').trim().toLocaleLowerCase('tr-TR')
                                        === 'karekod sonlandırma iptal')
                                : null);
                    try {
                        if (!action) return { clicked: false, messages, confirmations };
                        if ({{clickInDocumentJson}}) action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """;

            var currentBrowser = RequireBrowser();
            EventHandler<CoreWebView2NewWindowRequestedEventArgs>? newWindowHandler = null;
            EventHandler<CoreWebView2DownloadStartingEventArgs>? downloadHandler = null;
            EventHandler<CoreWebView2DownloadStartingEventArgs>? popupDownloadHandler = null;
            EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs>? responseHandler = null;
            EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs>? popupResponseHandler = null;
            CoreWebView2DevToolsProtocolEventReceiver? networkReceiver = null;
            CoreWebView2DevToolsProtocolEventReceiver? popupNetworkReceiver = null;
            EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs>? networkHandler = null;
            EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs>? popupNetworkHandler = null;
            Action<CoreWebView2, CoreWebView2DevToolsProtocolEventReceivedEventArgs>?
                handleNetworkResponse = null;
            TaskCompletionSource<string>? downloadCompletion = null;
            TaskCompletionSource<WebView2>? popupCompletion = null;
            WebView2? popupBrowser = null;
            Window? popupWindow = null;
            MedulaRemoteScreen? savedActionScreen = null;
            var keepDocumentPopup = false;
            var downloadedFilePath = string.Empty;
            var downloadError = string.Empty;
            if (actionId == "f:buttonReceteYazdir")
            {
                downloadCompletion = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void HandleDownload(CoreWebView2DownloadStartingEventArgs args)
                {
                    var downloadDirectory = pharmacyApp.Infrastructure.AppPaths.DownloadsDirectory;
                    Directory.CreateDirectory(downloadDirectory);
                    var suggestedName = Path.GetFileName(args.ResultFilePath);
                    if (string.IsNullOrWhiteSpace(suggestedName))
                        suggestedName = $"Medula-Recete-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
                    var extension = Path.GetExtension(suggestedName);
                    var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(suggestedName));
                    if (string.IsNullOrWhiteSpace(extension)) extension = ".pdf";
                    var targetPath = UniqueDownloadPath(downloadDirectory, baseName, extension);
                    args.ResultFilePath = targetPath;
                    args.Handled = true;
                    void CompleteDownload()
                    {
                        if (args.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                            downloadCompletion.TrySetResult(targetPath);
                        else if (args.DownloadOperation.State == CoreWebView2DownloadState.Interrupted)
                            downloadCompletion.TrySetException(new InvalidOperationException(
                                $"Medula indirmesi kesildi: {args.DownloadOperation.InterruptReason}"));
                    }
                    args.DownloadOperation.StateChanged += (_, _) => CompleteDownload();
                    CompleteDownload();
                }
                downloadHandler = (_, args) => HandleDownload(args);
                popupDownloadHandler = (_, args) => HandleDownload(args);
                async void HandleResponse(CoreWebView2WebResourceResponseReceivedEventArgs args)
                {
                    try
                    {
                        string ReadOptionalHeader(string name)
                        {
                            try { return args.Response.Headers.GetHeader(name) ?? string.Empty; }
                            catch (ArgumentException) { return string.Empty; }
                            catch (System.Runtime.InteropServices.COMException) { return string.Empty; }
                        }
                        var contentType = ReadOptionalHeader("Content-Type");
                        var disposition = ReadOptionalHeader("Content-Disposition");
                        if (!contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                            && !contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                            && !disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase))
                            return;
                        var downloadDirectory = pharmacyApp.Infrastructure.AppPaths.DownloadsDirectory;
                        Directory.CreateDirectory(downloadDirectory);
                        var targetPath = UniqueDownloadPath(
                            downloadDirectory,
                            $"Medula-Recete-{DateTime.Now:yyyyMMdd-HHmmss}",
                            ".pdf");
                        await using var source = await args.Response.GetContentAsync();
                        if (source is null) return;
                        await using var target = new FileStream(
                            targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        await source.CopyToAsync(target);
                        downloadCompletion.TrySetResult(targetPath);
                    }
                    catch (Exception)
                    {
                        // Download-managed response bodies are not readable through this API.
                        // The DevTools network handler below captures the same payload instead.
                    }
                }
                responseHandler = (_, args) => HandleResponse(args);
                popupResponseHandler = (_, args) => HandleResponse(args);
                handleNetworkResponse = async (core, args) =>
                {
                    try
                    {
                        using var responseDocument = JsonDocument.Parse(args.ParameterObjectAsJson);
                        var root = responseDocument.RootElement;
                        var response = root.GetProperty("response");
                        var mimeType = response.TryGetProperty("mimeType", out var mimeElement)
                            ? mimeElement.GetString() ?? string.Empty
                            : string.Empty;
                        var disposition = string.Empty;
                        if (response.TryGetProperty("headers", out var headers)
                            && headers.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var header in headers.EnumerateObject())
                                if (header.Name.Equals("content-disposition", StringComparison.OrdinalIgnoreCase))
                                    disposition = header.Value.ToString();
                        }
                        if (!mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                            && !mimeType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                            && !disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase))
                            return;
                        var requestId = root.GetProperty("requestId").GetString();
                        if (string.IsNullOrWhiteSpace(requestId)) return;

                        string? rawBody = null;
                        for (var attempt = 0; attempt < 30 && rawBody is null; attempt++)
                        {
                            try
                            {
                                rawBody = await core.CallDevToolsProtocolMethodAsync(
                                    "Network.getResponseBody",
                                    JsonSerializer.Serialize(new { requestId }));
                            }
                            catch (Exception) when (attempt < 29)
                            {
                                await Task.Delay(100);
                            }
                        }
                        if (rawBody is null) return;
                        using var bodyDocument = JsonDocument.Parse(rawBody);
                        var bodyRoot = bodyDocument.RootElement;
                        var body = bodyRoot.GetProperty("body").GetString() ?? string.Empty;
                        var base64 = bodyRoot.TryGetProperty("base64Encoded", out var encoded)
                            && encoded.GetBoolean();
                        var bytes = base64
                            ? Convert.FromBase64String(body)
                            : System.Text.Encoding.UTF8.GetBytes(body);
                        if (bytes.Length == 0) return;
                        var downloadDirectory = pharmacyApp.Infrastructure.AppPaths.DownloadsDirectory;
                        Directory.CreateDirectory(downloadDirectory);
                        var targetPath = UniqueDownloadPath(
                            downloadDirectory,
                            $"Medula-Recete-{DateTime.Now:yyyyMMdd-HHmmss}",
                            ".pdf");
                        await File.WriteAllBytesAsync(targetPath, bytes);
                        downloadCompletion.TrySetResult(targetPath);
                    }
                    catch (Exception)
                    {
                        // The authenticated form-fetch path below remains authoritative.
                    }
                };
                await currentBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync(
                    "Network.enable", "{}");
                networkReceiver = currentBrowser.CoreWebView2.GetDevToolsProtocolEventReceiver(
                    "Network.responseReceived");
                networkHandler = (_, args) => handleNetworkResponse(currentBrowser.CoreWebView2, args);
                networkReceiver.DevToolsProtocolEventReceived += networkHandler;
                currentBrowser.CoreWebView2.DownloadStarting += downloadHandler;
                currentBrowser.CoreWebView2.WebResourceResponseReceived += responseHandler;
            }
            if (SavedScreenActions.ContainsKey(actionId))
            {
                if (actionId == "f:buttonDokumanEkle")
                {
                    DisposePrescriptionDocumentBrowser();
                    prescriptionDocumentUsesPrimaryBrowser = false;
                }
                popupCompletion = new TaskCompletionSource<WebView2>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                newWindowHandler = async (_, args) =>
                {
                    var deferral = args.GetDeferral();
                    try
                    {
                        popupBrowser = new WebView2();
                        popupWindow = new Window
                        {
                            Width = 1280,
                            Height = 900,
                            Left = -32000,
                            Top = -32000,
                            ShowActivated = false,
                            ShowInTaskbar = false,
                            Opacity = 0,
                            WindowStyle = WindowStyle.None,
                            ResizeMode = ResizeMode.NoResize,
                            Content = popupBrowser
                        };
                        popupWindow.Show();
                        await popupBrowser.EnsureCoreWebView2Async(
                            currentBrowser.CoreWebView2.Environment);
                        popupBrowser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled =
                            !MedulaWarningBridge.SuppressNativeScriptDialogs;
                        popupBrowser.CoreWebView2.WebMessageReceived += OnBrowserWebMessageReceived;
                        await popupBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                            MedulaWarningBridge.Script);
                        popupBrowser.CoreWebView2.ScriptDialogOpening += (_, dialogArgs) =>
                        {
                            if (!string.IsNullOrWhiteSpace(dialogArgs.Message))
                                ReportBrowserMessage(dialogArgs.Message);
                            if (dialogArgs.Kind == CoreWebView2ScriptDialogKind.Alert) dialogArgs.Accept();
                        };
                        if (popupDownloadHandler is not null)
                            popupBrowser.CoreWebView2.DownloadStarting += popupDownloadHandler;
                        if (popupResponseHandler is not null)
                            popupBrowser.CoreWebView2.WebResourceResponseReceived += popupResponseHandler;
                        if (actionId == "f:buttonReceteYazdir")
                        {
                            await popupBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync(
                                "Network.enable", "{}");
                            popupNetworkReceiver = popupBrowser.CoreWebView2
                                .GetDevToolsProtocolEventReceiver("Network.responseReceived");
                            popupNetworkHandler = (_, eventArgs) =>
                                handleNetworkResponse?.Invoke(popupBrowser.CoreWebView2, eventArgs);
                            popupNetworkReceiver.DevToolsProtocolEventReceived += popupNetworkHandler;
                        }
                        args.NewWindow = popupBrowser.CoreWebView2;
                        popupCompletion.TrySetResult(popupBrowser);
                    }
                    catch (Exception exception)
                    {
                        args.Handled = true;
                        popupCompletion.TrySetException(exception);
                        downloadCompletion?.TrySetException(exception);
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                };
                currentBrowser.CoreWebView2.NewWindowRequested += newWindowHandler;
            }

            ActionClickResult click;
            try
            {
                var sourceBeforeAction = currentBrowser.Source?.AbsoluteUri ?? string.Empty;
                click = await ExecuteActionAndWaitAsync(
                    script,
                    cancellationToken,
                    waitForNavigation: !requiresTrustedClick);
                if (requiresTrustedClick && click.Clicked)
                {
                    await DispatchTrustedElementClickAsync(actionId, cancellationToken);
                }
                if (SavedScreenActions.ContainsKey(actionId)
                    && actionId != "f:buttonReceteYazdir"
                    && click.Clicked)
                {
                    WebView2? destinationBrowser = null;
                    var destinationReached = false;
                    for (var attempt = 0; attempt < 1800; attempt++)
                    {
                        if (popupCompletion!.Task.IsCompleted)
                            destinationBrowser = await popupCompletion.Task;
                        else if (!string.IsNullOrWhiteSpace(currentBrowser.Source?.AbsoluteUri)
                            && !string.Equals(currentBrowser.Source.AbsoluteUri, sourceBeforeAction,
                                StringComparison.OrdinalIgnoreCase))
                            destinationBrowser = currentBrowser;

                        if (destinationBrowser is null
                            && actionId == "f:buttonDokumanEkle")
                        {
                            try
                            {
                                var documentScreenProbe = await currentBrowser.ExecuteScriptAsync(
                                    "!!document.querySelector('input[type=file]') && /d[oö]k[uü]man/i.test(document.body?.innerText??'')");
                                if (string.Equals(documentScreenProbe, "true", StringComparison.OrdinalIgnoreCase))
                                    destinationBrowser = currentBrowser;
                            }
                            catch (InvalidOperationException)
                            {
                                // A JSF postback can replace the DOM while this same-URL probe runs.
                            }
                        }

                        if (destinationBrowser is not null)
                        {
                            try
                            {
                                var rawProbe = await destinationBrowser.ExecuteScriptAsync(
                                    "({url:location.href,ready:document.readyState,text:document.body?.innerText??''})");
                                var probe = JsonSerializer.Deserialize<PopupPageProbe>(rawProbe, JsonOptions);
                                if (probe is not null
                                    && !probe.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                                    && probe.Ready.Equals("complete", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(probe.Text))
                                {
                                    destinationReached = true;
                                    break;
                                }
                            }
                            catch (InvalidOperationException)
                            {
                                // The document can be replaced between the URL and DOM probes.
                            }
                        }
                        if (attempt == 20
                            && actionId == "f:buttonReceteBilgi"
                            && destinationBrowser is null)
                        {
                            await NavigateAsync(PrescriptionInfoUrl, cancellationToken);
                            destinationBrowser = currentBrowser;
                        }
                        await Task.Delay(50, cancellationToken);
                    }
                    if (!destinationReached || destinationBrowser is null)
                        throw new TimeoutException($"{actionCaption} ekranı zamanında açılmadı.");
                    var popupDestination = SavedScreenActions[actionId];
                    savedActionScreen = await ReadRemoteScreenAsync(
                        popupDestination.Key,
                        popupDestination.Title,
                        $"{popupDestination.Title} ekranı hazır.",
                        destinationBrowser);
                    if (actionId == "f:buttonDokumanEkle" && savedActionScreen.IsOpen)
                    {
                        prescriptionDocumentUsesPrimaryBrowser = ReferenceEquals(destinationBrowser, currentBrowser);
                        keepDocumentPopup = !prescriptionDocumentUsesPrimaryBrowser
                            && ReferenceEquals(destinationBrowser, popupBrowser);
                    }
                }
                if (downloadCompletion is not null
                    && click.Clicked
                    && string.IsNullOrWhiteSpace(downloadedFilePath))
                {
                    try
                    {
                        downloadedFilePath = await downloadCompletion.Task.WaitAsync(
                            TimeSpan.FromSeconds(90), cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        downloadError = "Medula yazdırma dosyasını 90 saniye içinde indirmedi.";
                    }
                    catch (InvalidOperationException exception)
                    {
                        downloadError = exception.Message;
                    }
                }
            }
            finally
            {
                if (newWindowHandler is not null)
                    currentBrowser.CoreWebView2.NewWindowRequested -= newWindowHandler;
                if (downloadHandler is not null)
                    currentBrowser.CoreWebView2.DownloadStarting -= downloadHandler;
                if (popupDownloadHandler is not null && popupBrowser?.CoreWebView2 is not null)
                    popupBrowser.CoreWebView2.DownloadStarting -= popupDownloadHandler;
                if (responseHandler is not null)
                    currentBrowser.CoreWebView2.WebResourceResponseReceived -= responseHandler;
                if (popupResponseHandler is not null && popupBrowser?.CoreWebView2 is not null)
                    popupBrowser.CoreWebView2.WebResourceResponseReceived -= popupResponseHandler;
                if (networkReceiver is not null && networkHandler is not null)
                    networkReceiver.DevToolsProtocolEventReceived -= networkHandler;
                if (popupNetworkReceiver is not null && popupNetworkHandler is not null)
                    popupNetworkReceiver.DevToolsProtocolEventReceived -= popupNetworkHandler;
                if (keepDocumentPopup && popupWindow is not null && popupBrowser is not null)
                {
                    prescriptionDocumentWindow = popupWindow;
                    prescriptionDocumentBrowser = popupBrowser;
                    popupWindow = null;
                    popupBrowser = null;
                }
                if (popupWindow is not null)
                {
                    popupWindow.Content = null;
                    popupWindow.Close();
                }
                popupBrowser?.Dispose();
            }
            if (await IsLoginPageAsync())
            {
                var restored = await RecoverFromExpiredSessionDuringOperationAsync(
                    actionRestoration,
                    cancellationToken);
                var recoveredPage = await ReadPrescriptionPageResultAsync(
                    restored
                        ? "Medula oturumu yenilendi ve reçete geri yüklendi."
                        : "Medula oturumu yeniden açılamadı.");
                return recoveredPage with
                {
                    IsSuccess = false,
                    Message = restored
                        ? "Medula oturumu otomatik olarak yenilendi. İşlemi tekrar deneyin."
                        : "Medula oturumu yeniden açılamadı."
                };
            }
            if (!click.Clicked)
                return await ReadPrescriptionPageResultAsync($"{actionCaption} denetimi Medula sayfasında bulunamadı.");

            var confirmation = click.Confirmations.FirstOrDefault();
            if (!confirmationAccepted && !string.IsNullOrWhiteSpace(confirmation))
            {
                var pending = await ReadPrescriptionPageResultAsync("Medula işlemi onay bekliyor.");
                return pending with { ConfirmationMessage = confirmation };
            }

            var browserMessage = click.Messages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            var pageResult = await ReadPrescriptionPageResultAsync(
                browserMessage ?? $"{actionCaption} işlemi Medula'ya gönderildi.");
            if (isPrescriptionSaveAction)
            {
                var directSaveWarning = !string.IsNullOrWhiteSpace(browserMessage)
                    && !IsSuccessMessage(browserMessage)
                    && !IsNoticeMessage(browserMessage)
                        ? browserMessage
                        : null;
                if (!string.IsNullOrWhiteSpace(directSaveWarning))
                {
                    ReportWarning(directSaveWarning);
                    pageResult = pageResult with
                    {
                        IsSuccess = false,
                        Message = directSaveWarning,
                        WarningText = directSaveWarning,
                        SuccessText = null
                    };
                }
                else
                {
                    // A save can complete through a quiet JSF partial update. Conversely, a
                    // validation failure can leave the same draft/saved form visible and render
                    // its red message shortly after the click. Observe both outcomes together;
                    // never use a generally successful page read as proof of persistence.
                    var existingSavedPrescription = input.IsSavedOnMedula;
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        var warning = pageResult.WarningText;
                        if (string.IsNullOrWhiteSpace(warning))
                            warning = GetWarningAfter(saveWarningRevision);
                        if (string.IsNullOrWhiteSpace(warning))
                        {
                            var visibleWarning = await ReadVisibleMedulaWarningAsync();
                            if (!string.IsNullOrWhiteSpace(visibleWarning)
                                && (!string.Equals(
                                        visibleWarning, saveWarningBeforeAction, StringComparison.Ordinal)
                                    || GetWarningRevision() > saveWarningRevision))
                                warning = visibleWarning;
                        }
                        if (!string.IsNullOrWhiteSpace(warning))
                        {
                            ReportWarning(warning);
                            pageResult = pageResult with
                            {
                                IsSuccess = false,
                                Message = warning,
                                WarningText = warning,
                                SuccessText = null
                            };
                            break;
                        }

                        var directSaveSuccess = !string.IsNullOrWhiteSpace(browserMessage)
                            && IsSuccessMessage(browserMessage);
                        if (MedulaPrescriptionSaveGuard.IsConfirmed(pageResult)
                            && (!existingSavedPrescription || directSaveSuccess || attempt >= 20))
                            break;
                        if (attempt + 1 >= 40) break;
                        await Task.Delay(50, cancellationToken);
                        pageResult = await ReadPrescriptionPageResultAsync(
                            $"{actionCaption} işlemi Medula'ya gönderildi.");
                    }

                    if (!MedulaPrescriptionSaveGuard.IsConfirmed(pageResult))
                    {
                        var unconfirmedMessage = !string.IsNullOrWhiteSpace(pageResult.WarningText)
                            ? pageResult.WarningText
                            : "Medula reçete kaydını doğrulamadı. Yerel reçete kaydı oluşturulmadı.";
                        if (string.IsNullOrWhiteSpace(pageResult.WarningText))
                            ReportWarning(unconfirmedMessage);
                        pageResult = pageResult with
                        {
                            IsSuccess = false,
                            Message = unconfirmedMessage,
                            WarningText = unconfirmedMessage,
                            SuccessText = null
                        };
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(downloadError))
                return pageResult with
                {
                    IsSuccess = false,
                    Message = downloadError,
                    WarningText = downloadError
                };
            if (!string.IsNullOrWhiteSpace(downloadedFilePath))
            {
                const string downloadMessage = "Medula reçete çıktısı indirildi.";
                ReportConfirmation(downloadMessage);
                pageResult = pageResult with
                {
                    IsSuccess = true,
                    Message = downloadMessage,
                    SuccessText = downloadMessage,
                    DownloadedFilePath = downloadedFilePath
                };
            }
            if (!string.IsNullOrWhiteSpace(browserMessage))
            {
                if (IsSuccessMessage(browserMessage))
                {
                    ReportConfirmation(browserMessage);
                    pageResult = pageResult with { SuccessText = browserMessage, WarningText = null };
                }
                else if (IsNoticeMessage(browserMessage))
                {
                    ReportNotice(browserMessage);
                    pageResult = pageResult with { NoticeText = browserMessage, WarningText = null };
                }
                else
                {
                    ReportWarning(browserMessage);
                    pageResult = pageResult with { WarningText = browserMessage };
                }
            }
            if (SavedScreenActions.TryGetValue(actionId, out var destination)
                && (actionId == "f:buttonDokumanEkle" && savedActionScreen is not null
                    || pageResult.IsSuccess && string.IsNullOrWhiteSpace(pageResult.WarningText)))
            {
                if (savedActionScreen is not null)
                    pageResult = pageResult with
                    {
                        OpenedScreen = savedActionScreen,
                        IsSuccess = savedActionScreen.IsOpen,
                        Message = savedActionScreen.Message,
                        WarningText = null
                    };
                else if (actionId != "f:buttonReceteYazdir"
                    && !pageResult.IsPrescriptionEntryPage)
                {
                    var screen = await ReadRemoteScreenAsync(
                        destination.Key,
                        destination.Title,
                        $"{destination.Title} ekranı hazır.");
                    pageResult = pageResult with { OpenedScreen = screen, IsSuccess = screen.IsOpen };
                }
            }
            var recoveredResult = await RecoverPrescriptionEntryIfRequiredAsync(
                pageResult, input, cancellationToken);
            if (recoveredResult.IsPrescriptionEntryPage)
            {
                var refreshedDrugs = await ReadPrescriptionDrugsAsync();
                MedulaDrugQuantityAdjustments.AnnotateReductions(input.Drugs, refreshedDrugs);
                recoveredResult = recoveredResult with { RefreshedDrugs = refreshedDrugs };
            }
            return recoveredResult;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MedulaPrescriptionPageResult(
                false,
                false,
                "Medula",
                browser?.Source?.AbsoluteUri ?? LoginUrl,
                $"{actionCaption} işlemi tamamlanamadı: {exception.Message}",
                string.Empty);
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<(bool IsSuccess, string Message)> SelectPrescriptionDocumentFileAsync(
        MedulaRemoteScreen screen,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (!File.Exists(filePath)) return (false, "Seçilen dosya bulunamadı.");

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            using var messageScope = EnterMessageScope(MedulaMessageScope.Prescription);
            var target = PrescriptionDocumentBrowser();
            if (target?.CoreWebView2 is null)
                return (false, "Medula doküman ekranı artık açık değil.");
            var file = screen.Fields.FirstOrDefault(field => field.Kind == "file");
            if (file is null) return (false, "Medula dosya seçme alanı okunamadı.");
            var selected = await SetPrescriptionDocumentFileAsync(
                target, file.Id, filePath, cancellationToken);
            return selected
                ? (true, "Dosya Medula doküman alanında seçildi.")
                : (false, "Dosya Medula doküman alanında seçilemedi.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportWarning(exception.Message);
            return (false, exception.Message);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaRemoteScreen> UploadPrescriptionDocumentAsync(
        MedulaRemoteScreen screen,
        string documentTypeValue,
        string filePath,
        bool confirmationAccepted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (!File.Exists(filePath))
            return screen with { Message = "Seçilen dosya bulunamadı." };

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            using var messageScope = EnterMessageScope(MedulaMessageScope.Prescription);
            var target = PrescriptionDocumentBrowser();
            if (target?.CoreWebView2 is null)
                return screen with { IsOpen = false, Message = "Medula doküman ekranı artık açık değil." };

            var select = screen.Fields.FirstOrDefault(field => field.Kind == "select");
            var file = screen.Fields.FirstOrDefault(field => field.Kind == "file");
            var action = screen.Actions.FirstOrDefault(candidate =>
                candidate.Id.Equals("form1:buttonUpload", StringComparison.Ordinal)
                || candidate.Caption.Contains("küman Ekle", StringComparison.CurrentCultureIgnoreCase)
                || candidate.Caption.Contains("kümanı Yükle", StringComparison.CurrentCultureIgnoreCase));
            if (file is null)
                return screen with { Message = "Medula doküman ekleme denetimleri okunamadı." };

            if (select is not null)
            {
                var selectIdJson = JsonSerializer.Serialize(select.Id);
                var selectValueJson = JsonSerializer.Serialize(documentTypeValue);
                await target.ExecuteScriptAsync($$"""
                    (() => {
                        const field = document.getElementById({{selectIdJson}});
                        if (!field) return false;
                        field.value = {{selectValueJson}};
                        field.dispatchEvent(new Event('change', { bubbles: true }));
                        return true;
                    })();
                    """);
            }

            if (!await SetPrescriptionDocumentFileAsync(target, file.Id, filePath, cancellationToken))
                return screen with { Message = "Dosya Medula doküman alanında seçilemedi." };

            var warningRevision = GetWarningRevision();
            var actionIdJson = JsonSerializer.Serialize(action?.Id ?? string.Empty);
            var confirmationAcceptedJson = confirmationAccepted ? "true" : "false";
            var fileIdJson = JsonSerializer.Serialize(file.Id);
            var raw = await target.ExecuteScriptAsync($$"""
                (() => {
                    const clean = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleLowerCase('tr-TR');
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => {
                        confirmations.push(String(message ?? ''));
                        return {{confirmationAcceptedJson}};
                    };
                    try {
                        const file = document.getElementById({{fileIdJson}});
                        const scope = file?.closest('form') ?? document;
                        const configured = document.getElementById({{actionIdJson}});
                        const candidates = [...scope.querySelectorAll(
                            'input[type=submit],input[type=button],input[type=image],button,a')];
                        const button = configured ?? candidates.find(candidate => {
                            const caption = clean(candidate.value || candidate.alt || candidate.title
                                || candidate.innerText || candidate.textContent);
                            return /d[oö]k[uü]man/.test(caption) && /(ekle|y[uü]kle)/.test(caption);
                        }) ?? candidates.find(candidate => clean(
                            candidate.value || candidate.alt || candidate.title
                                || candidate.innerText || candidate.textContent) === 'ekle');
                        if (!button) return { clicked: false, messages, confirmations };
                        button.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """);
            var click = JsonSerializer.Deserialize<ActionClickResult>(raw, JsonOptions)
                ?? new ActionClickResult(false, [], []);
            if (!click.Clicked)
                return screen with { Message = "Medula Doküman Ekle denetimi bulunamadı." };
            var confirmation = click.Confirmations.FirstOrDefault(message =>
                !string.IsNullOrWhiteSpace(message));
            if (!confirmationAccepted && !string.IsNullOrWhiteSpace(confirmation))
                return screen with
                {
                    Message = confirmation,
                    ConfirmationMessage = confirmation
                };
            await Task.Delay(350, cancellationToken);
            var refreshed = await ReadRemoteScreenAsync(
                screen.Key,
                screen.Title,
                "Doküman ekranı güncellendi.",
                target);
            var warning = click.Messages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message))
                ?? GetWarningAfter(warningRevision)
                ?? await ReadVisibleMedulaWarningAsync(target);
            if (!string.IsNullOrWhiteSpace(warning))
            {
                ReportWarning(warning);
                return refreshed with { Message = warning };
            }
            return refreshed with { ConfirmationMessage = null };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportWarning(exception.Message);
            return screen with { Message = exception.Message };
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private WebView2? PrescriptionDocumentBrowser() =>
        prescriptionDocumentUsesPrimaryBrowser ? RequireBrowser() : prescriptionDocumentBrowser;

    private static async Task<bool> SetPrescriptionDocumentFileAsync(
        WebView2 target,
        string fieldId,
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fieldIdJson = JsonSerializer.Serialize(fieldId);
        var evaluated = await target.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Runtime.evaluate",
            JsonSerializer.Serialize(new
            {
                expression = $"document.getElementById({fieldIdJson})",
                returnByValue = false
            }));
        using var evaluatedDocument = JsonDocument.Parse(evaluated);
        if (!evaluatedDocument.RootElement.TryGetProperty("result", out var runtimeResult))
            return false;
        // CoreWebView2 returns the CDP RemoteObject directly under `result`.
        // Some WebView2 versions wrap it once more, so accept both shapes.
        var evaluatedResult = runtimeResult.TryGetProperty("result", out var nestedResult)
            ? nestedResult
            : runtimeResult;
        if (!evaluatedResult.TryGetProperty("objectId", out var objectIdElement)) return false;
        var objectId = objectIdElement.GetString();
        if (string.IsNullOrWhiteSpace(objectId)) return false;
        await target.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "DOM.setFileInputFiles",
            JsonSerializer.Serialize(new { files = new[] { Path.GetFullPath(filePath) }, objectId }));
        var expectedNameJson = JsonSerializer.Serialize(Path.GetFileName(filePath));
        var verified = await target.ExecuteScriptAsync($$"""
            (() => {
                const field = document.getElementById({{fieldIdJson}});
                return !!field?.files?.length && field.files[0].name === {{expectedNameJson}};
            })();
            """);
        return verified.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MedulaPrescriptionPageResult> ClosePrescriptionDocumentAsync(
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            DisposePrescriptionDocumentBrowser();
            if (prescriptionDocumentUsesPrimaryBrowser)
            {
                const string returnScript = """
                    (() => {
                        const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                        const candidates = [...document.querySelectorAll(
                            'input[type=submit],input[type=button],button,a')];
                        const action = candidates.find(element =>
                            /^(?:Reçeteye\s+Geri\s+Dön|Geri\s+Dön|Kapat)$/i.test(
                                clean(element.value || element.innerText || element.textContent)));
                        if (!action) return { clicked: false, messages: [], confirmations: [] };
                        window.alert = () => {};
                        window.confirm = () => false;
                        action.click();
                        return { clicked: true, messages: [], confirmations: [] };
                    })();
                    """;
                var returned = await ExecuteActionAndWaitAsync(returnScript, cancellationToken);
                if (!returned.Clicked && RequireBrowser().CoreWebView2.CanGoBack)
                {
                    RequireBrowser().CoreWebView2.GoBack();
                    await Task.Delay(300, cancellationToken);
                }
                if (MedulaPrescriptionAccessPolicy.Resolve(input)
                        != MedulaPrescriptionAccessRoute.PrescriptionEntry
                    && !await EnsurePrescriptionContextAsync(input, cancellationToken))
                    return await ReadPrescriptionPageResultAsync("Reçete ekranına dönülemedi.");
            }
            prescriptionDocumentUsesPrimaryBrowser = false;
            return await ReadPrescriptionPageResultAsync("Kayıtlı reçete ekranına dönüldü.");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDrugSearchResult> SearchPrescriptionDrugsAsync(
        MedulaPrescriptionInput input,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        query = query.Trim();
        if (query.Length < 3)
            return new MedulaDrugSearchResult(false, "İlaç aramak için en az 3 karakter girin.", []);

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyAsync(input, cancellationToken))
                return new MedulaDrugSearchResult(false, "Medula Reçete Giriş ekranı açılamadı.", []);

            await ApplyPrescriptionInputAsync(input, cancellationToken);
            var opened = await ClickDrugControlAsync("f:buttonIlacArama", false, cancellationToken);
            var warning = MedulaBrowserWarning.First(opened.Messages, opened.Confirmations);
            if (warning is not null) return DrugSearchWarning(warning);

            var queryJson = JsonSerializer.Serialize(query);
            var searched = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => { confirmations.push(String(message ?? '')); return false; };
                    try {
                        const field = document.getElementById('f:text56');
                        const action = document.getElementById('f:buttonIlacAra');
                        if (!field || !action) return { clicked: false, messages, confirmations };
                        field.value = {{queryJson}};
                        field.dispatchEvent(new Event('input', { bubbles: true }));
                        field.dispatchEvent(new Event('change', { bubbles: true }));
                        action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """, cancellationToken);
            warning = MedulaBrowserWarning.First(searched.Messages, searched.Confirmations);
            if (warning is not null) return DrugSearchWarning(warning);
            if (!searched.Clicked)
                return new MedulaDrugSearchResult(false, "Medula ilaç arama alanını açmadı.", []);

            IReadOnlyList<MedulaDrugSearchOption> options = [];
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var page = await ReadDrugSearchPageAsync();
                if (page.PageIndex != 0)
                {
                    await NavigateDrugSearchPageAsync(0, cancellationToken);
                    page = await ReadDrugSearchPageAsync();
                }
                options = page.Options;
                if (options.Count > 0) break;
                var visibleWarning = await ReadVisibleMedulaWarningAsync();
                if (!string.IsNullOrWhiteSpace(visibleWarning)) return DrugSearchWarning(visibleWarning);
                if (attempt < 19) await Task.Delay(50, cancellationToken);
            }
            if (options.Count > 0)
                options = await ReadAllDrugSearchOptionsAsync(cancellationToken);

            return new MedulaDrugSearchResult(
                true,
                options.Count > 0 ? $"{options.Count} ilaç bulundu." : "Aramayla eşleşen ilaç bulunamadı.",
                options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MedulaDrugSearchResult(false, $"İlaç araması tamamlanamadı: {exception.Message}", []);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDrugActionResult> SelectPrescriptionDrugAsync(
        MedulaPrescriptionInput input,
        MedulaDrugSearchOption option,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(option);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyOrSearchOpenAsync(cancellationToken))
                return EmptyDrugAction("Medula ilaç arama ekranı artık açık değil.");
            if (!await NavigateDrugSearchPageAsync(option.PageIndex, cancellationToken))
                return EmptyDrugAction("Seçilen ilacın Medula sonuç sayfası açılamadı.");
            var click = await ClickDrugControlAsync(option.ActionId, false, cancellationToken);
            var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (warning is not null) return DrugActionWarning(warning);
            if (!click.Clicked) return EmptyDrugAction("Seçilen ilaç Medula sonuç listesinde bulunamadı.");
            await WaitForDrugSearchToCloseAsync(cancellationToken);
            return new MedulaDrugActionResult(true, $"{option.Name} reçeteye eklendi.",
                await ReadPrescriptionDrugsAsync());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyDrugAction($"İlaç eklenemedi: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDrugActionResult> AddPrescriptionDrugBarcodeAsync(
        MedulaPrescriptionInput input,
        string barcode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        barcode = barcode.Trim();
        if (barcode.Length == 0) return EmptyDrugAction("Barkod girin.");
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyAsync(input, cancellationToken))
                return EmptyDrugAction("Medula Reçete Giriş ekranı açılamadı.");
            await ApplyPrescriptionInputAsync(input, cancellationToken);

            var rowIndex = await FindEmptyDrugRowAsync();
            if (rowIndex < 0)
            {
                var added = await ClickDrugControlAsync("f:buttonIlacEkle", false, cancellationToken);
                var addWarning = MedulaBrowserWarning.First(added.Messages, added.Confirmations);
                if (addWarning is not null) return DrugActionWarning(addWarning);
                if (!added.Clicked) return EmptyDrugAction("Medula yeni ilaç satırı ekleyemedi.");
                for (var attempt = 0; attempt < 20 && rowIndex < 0; attempt++)
                {
                    rowIndex = await FindEmptyDrugRowAsync();
                    if (rowIndex < 0) await Task.Delay(50, cancellationToken);
                }
                if (rowIndex < 0) return EmptyDrugAction("Medula yeni ilaç satırını oluşturmadı.");
            }

            await SetBarcodeAndSelectedDrugRowAsync(rowIndex, barcode);
            return await ResolvePrescriptionDrugBarcodeAsync(
                rowIndex, "Barkod Medula'ya eklendi.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyDrugAction($"Barkod eklenemedi: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDiagnosisSearchResult> SearchPrescriptionDiagnosesAsync(
        MedulaPrescriptionInput input,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        query = query.Trim();
        if (query.Length < 3)
            return new MedulaDiagnosisSearchResult(false, "ICD-10 aramak için en az 3 karakter girin.", []);

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyAsync(input, cancellationToken))
                return new MedulaDiagnosisSearchResult(false, "Medula Reçete Giriş ekranı açılamadı.", []);

            var searchInput = input with { DiagnosisSearch = query };
            await ApplyPrescriptionInputAsync(searchInput, cancellationToken);
            var searched = await ClickDrugControlAsync("f:buttonIcd10Sorgula", false, cancellationToken);
            var warning = MedulaBrowserWarning.First(searched.Messages, searched.Confirmations);
            if (warning is not null) return DiagnosisSearchWarning(warning);
            if (!searched.Clicked)
                return new MedulaDiagnosisSearchResult(false, "Medula ICD-10 sorgulama denetimini bulamadı.", []);

            DiagnosisSearchPageProbe page = EmptyDiagnosisSearchPage();
            for (var attempt = 0; attempt < 24; attempt++)
            {
                page = await ReadDiagnosisSearchPageAsync();
                if (page.Options.Count > 0) break;
                var visibleWarning = await ReadVisibleMedulaWarningAsync();
                if (!string.IsNullOrWhiteSpace(visibleWarning)) return DiagnosisSearchWarning(visibleWarning);
                if (attempt < 23) await Task.Delay(50, cancellationToken);
            }
            if (page.PageIndex != 0 && await NavigateDiagnosisSearchPageAsync(0, cancellationToken))
                page = await ReadDiagnosisSearchPageAsync();

            var options = page.Options.Count > 0
                ? await ReadAllDiagnosisSearchOptionsAsync(cancellationToken)
                : [];
            return new MedulaDiagnosisSearchResult(
                true,
                options.Count > 0 ? $"{options.Count} ICD-10 tanısı bulundu." : "Aramayla eşleşen ICD-10 tanısı bulunamadı.",
                options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MedulaDiagnosisSearchResult(false, $"ICD-10 araması tamamlanamadı: {exception.Message}", []);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDiagnosisActionResult> SelectPrescriptionDiagnosisAsync(
        MedulaDiagnosisSearchOption option,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyOrSearchOpenAsync(cancellationToken))
                return EmptyDiagnosisAction("Medula ICD-10 arama sonuçları artık açık değil.");
            if (!await NavigateDiagnosisSearchPageAsync(option.PageIndex, cancellationToken))
                return EmptyDiagnosisAction("Seçilen ICD-10 sonucunun Medula sayfası açılamadı.");

            var before = await ReadPrescriptionDiagnosesAsync();
            var click = await ClickDrugControlAsync(option.ActionId, false, cancellationToken);
            var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (warning is not null) return DiagnosisActionWarning(warning, before);
            if (!click.Clicked) return EmptyDiagnosisAction("Seçilen ICD-10 tanısı Medula sonuç listesinde bulunamadı.");

            IReadOnlyList<MedulaDiagnosisEntry> diagnoses = before;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                diagnoses = await ReadPrescriptionDiagnosesAsync();
                if (diagnoses.Count > before.Count
                    || diagnoses.Any(item => item.Code.Equals(option.Code, StringComparison.OrdinalIgnoreCase)))
                    break;
                if (attempt < 23) await Task.Delay(50, cancellationToken);
            }
            return new MedulaDiagnosisActionResult(
                true,
                $"{option.Code} — {option.Description} reçeteye eklendi.",
                diagnoses);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyDiagnosisAction($"ICD-10 tanısı eklenemedi: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDiagnosisActionResult> SetPrescriptionDiagnosisEntryAsync(
        MedulaPrescriptionInput input,
        MedulaDiagnosisEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyAsync(input, cancellationToken))
                return EmptyDiagnosisAction("Medula Reçete Giriş ekranı açılamadı.");
            var codeJson = JsonSerializer.Serialize(entry.Code.Trim());
            var descriptionJson = JsonSerializer.Serialize(entry.Description.Trim());
            var raw = await RequireBrowser().ExecuteScriptAsync($$"""
                (() => {
                    const codeInputs = [...document.querySelectorAll(
                        'input[id^="f:tableEx1:"][id$=":text7"]')];
                    const codeInput = codeInputs.find(input => !(input.value ?? '').trim());
                    if (!codeInput) return false;
                    const index = codeInput.id.match(/^f:tableEx1:(\d+):text7$/)?.[1];
                    const descriptionInput = document.getElementById(`f:tableEx1:${index}:text3`);
                    codeInput.value = {{codeJson}};
                    if (descriptionInput) descriptionInput.value = {{descriptionJson}};
                    for (const element of [codeInput, descriptionInput].filter(Boolean)) {
                        element.dispatchEvent(new Event('input', { bubbles: true }));
                        element.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    return true;
                })();
                """);
            if (!string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
                return EmptyDiagnosisAction("Medula'da boş ICD-10 satırı bulunamadı.");
            var diagnoses = await ReadPrescriptionDiagnosesAsync();
            return new MedulaDiagnosisActionResult(
                true, "ICD-10 satırı Medula'ya aktarıldı.", diagnoses);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyDiagnosisAction($"ICD-10 satırı aktarılamadı: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDrugActionResult> UpdatePrescriptionDrugBarcodeAsync(
        MedulaPrescriptionInput input,
        int rowIndex,
        string barcode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        barcode = barcode.Trim();
        if (barcode.Length == 0) return EmptyDrugAction("Barkod girin.");
        if (rowIndex < 0 || rowIndex >= input.Drugs.Count)
            return EmptyDrugAction("Güncellenecek ilaç satırı bulunamadı.");

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyAsync(input, cancellationToken))
                return EmptyDrugAction("Medula Reçete Giriş ekranı açılamadı.");
            await ApplyPrescriptionInputAsync(input, cancellationToken, synchronizeDrugs: false);
            await SetBarcodeAndSelectedDrugRowAsync(rowIndex, barcode);
            var result = await ResolvePrescriptionDrugBarcodeAsync(
                rowIndex, "Barkod Medula'da güncellendi.", cancellationToken);
            if (!result.IsSuccess && rowIndex < result.Drugs.Count)
                result.Drugs[rowIndex].Name = string.Empty;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyDrugAction($"Barkod güncellenemedi: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private async Task<MedulaDrugActionResult> ResolvePrescriptionDrugBarcodeAsync(
        int rowIndex,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var warningBeforeInfo = await ReadVisibleMedulaWarningAsync();
        var infoClick = await ClickDrugControlAsync("f:buttonIlacBilgiGorme", true, cancellationToken);
        var warning = MedulaBrowserWarning.First(infoClick.Messages, infoClick.Confirmations);
        var drugInfoOpened = warning is null && await IsDrugInfoScreenAsync();
        if (warning is null && !drugInfoOpened)
        {
            var visibleWarning = await ReadVisibleMedulaWarningAsync();
            if (!string.IsNullOrWhiteSpace(visibleWarning)
                && !string.Equals(visibleWarning, warningBeforeInfo, StringComparison.Ordinal))
                warning = visibleWarning;
            warning ??= await ReadDrugOperationWarningAsync();
        }
        if (warning is not null) return DrugActionWarning(warning, await ReadPrescriptionDrugsAsync());
        if (!infoClick.Clicked) return EmptyDrugAction("Medula İlaç Bilgi denetimini bulamadı.");
        if (!drugInfoOpened)
            return EmptyDrugAction("Medula İlaç Bilgi ekranını açmadı.");

        var info = await ReadRemoteScreenAsync("drug-info", "İlaç Bilgi", "İlaç bilgileri hazır.");
        var drugName = info.Fields.FirstOrDefault(field =>
            field.Label.Contains("İlaç Adı", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        await ReturnToPrescriptionCoreAsync(cancellationToken);
        var drugs = (await ReadPrescriptionDrugsAsync()).ToList();
        if (rowIndex < drugs.Count && !string.IsNullOrWhiteSpace(drugName)) drugs[rowIndex].Name = drugName;
        return new MedulaDrugActionResult(true, successMessage, drugs);
    }

    public async Task<MedulaDrugActionResult> ExecutePrescriptionDrugRowActionAsync(
        MedulaPrescriptionInput input,
        int rowIndex,
        string actionId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (actionId is not ("f:buttonOzelDurum" or "f:buttonIlacEsdeger" or
            "f:buttonIlacBilgiGorme" or "f:buttonRaporGoruntule" or "f:buttonIlacSil"))
            throw new ArgumentOutOfRangeException(nameof(actionId));

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyAsync(input, cancellationToken))
                return EmptyDrugAction("Medula Reçete Giriş ekranı açılamadı.");
            if (MedulaSubScreenSafety.ShouldSynchronizePrescriptionInput(actionId))
                await ApplyPrescriptionInputAsync(input, cancellationToken);
            if (!await SelectDrugRowAsync(rowIndex))
                return EmptyDrugAction("Seçilen ilaç satırı Medula'da bulunamadı.");

            var isDelete = actionId == "f:buttonIlacSil";
            var opensReport = actionId == "f:buttonRaporGoruntule";
            var warningBeforeClick = await ReadVisibleMedulaWarningAsync();
            var warningRevision = GetWarningRevision();
            var click = await ClickDrugControlAsync(actionId, isDelete, cancellationToken);
            var warning = isDelete
                ? click.Messages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message))
                : MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            var drugInfoOpened = actionId == "f:buttonIlacBilgiGorme"
                && warning is null && await IsDrugInfoScreenAsync();
            var reportOpened = false;
            if (opensReport && warning is null)
            {
                var destination = await WaitForExpectedSubScreenAsync(
                    "drug-report-detail", warningBeforeClick, warningRevision, cancellationToken);
                warning = destination.Warning;
                reportOpened = destination.DestinationReached;
            }
            if (warning is null && !drugInfoOpened && !reportOpened)
            {
                var visibleWarning = await ReadVisibleMedulaWarningAsync();
                if (!string.IsNullOrWhiteSpace(visibleWarning)
                    && !string.Equals(visibleWarning, warningBeforeClick, StringComparison.Ordinal))
                    warning = visibleWarning;
                warning ??= await ReadDrugOperationWarningAsync();
            }
            if (warning is not null) return DrugActionWarning(warning, await ReadPrescriptionDrugsAsync());
            if (!click.Clicked) return EmptyDrugAction($"Medula {title} denetimini bulamadı.");
            if (actionId == "f:buttonIlacBilgiGorme" && !drugInfoOpened)
                return EmptyDrugAction("Medula İlaç Bilgi ekranını açmadı.");
            if (opensReport && !reportOpened)
                return EmptyDrugAction("Medula seçilen ilacın rapor ekranını açmadı.");

            if (isDelete)
            {
                await Task.Delay(100, cancellationToken);
                return new MedulaDrugActionResult(true, "İlaç satırı silindi.", await ReadPrescriptionDrugsAsync());
            }

            if (actionId == "f:buttonIlacBilgiGorme")
            {
                var info = await ReadRemoteScreenAsync("drug-info", title, "İlaç bilgileri hazır.");
                RememberDrugRowScreen(input, rowIndex, actionId, info);
                return new MedulaDrugActionResult(info.IsOpen, info.Message, await ReadPrescriptionDrugsAsync(), info);
            }

            if (opensReport)
            {
                var report = await ReadRemoteScreenAsync(
                    "drug-report-detail", "Rapor Detayı", "Rapor detayı hazır.");
                RememberDrugRowScreen(input, rowIndex, actionId, report);
                return new MedulaDrugActionResult(
                    report.IsOpen, report.Message, input.Drugs, report);
            }

            var key = actionId == "f:buttonOzelDurum" ? "drug-special" : "drug-equivalent";
            if (key == "drug-special")
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var raw = await RequireBrowser().ExecuteScriptAsync(
                        "!!document.getElementById('f:menu8') && document.getElementById('f:menu8').getClientRects().length > 0");
                    if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) break;
                    if (attempt < 19) await Task.Delay(50, cancellationToken);
                }
            }
            else
                await Task.Delay(150, cancellationToken);
            var screen = await ReadDrugOverlayAsync(key, title);
            RememberDrugRowScreen(input, rowIndex, actionId, screen);
            return new MedulaDrugActionResult(screen.IsOpen, screen.Message,
                await ReadPrescriptionDrugsAsync(), screen);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyDrugAction($"{title} işlemi tamamlanamadı: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionPageResult> SynchronizePrescriptionDatesAsync(
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyAsync(input, cancellationToken))
                return new MedulaPrescriptionPageResult(false, false, "Medula",
                    browser?.Source?.AbsoluteUri ?? LoginUrl,
                    MedulaPrescriptionAccessPolicy.Resolve(input)
                        == MedulaPrescriptionAccessRoute.PrescriptionEntry
                        ? "Tarih doğrulaması için Medula Reçete Giriş ekranı açılamadı."
                        : "Reçete doğru Medula sorgu ekranından güvenli biçimde geri yüklenemedi. Tarih uygulanmadı.",
                    string.Empty);

            var dates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["f:t29"] = input.PrescriptionDate,
                ["f:t31"] = input.DispensingDate
            };
            var datesJson = JsonSerializer.Serialize(dates);
            var raw = await RequireBrowser().ExecuteScriptAsync($$"""
                (() => {
                    const dates = {{datesJson}};
                    const messages = [];
                    const originalAlert = window.alert;
                    window.alert = message => messages.push(String(message ?? ''));
                    for (const [id, value] of Object.entries(dates)) {
                        const element = document.getElementById(id);
                        if (!element) continue;
                        element.value = value ?? '';
                        element.dispatchEvent(new Event('input', { bubbles: true }));
                        element.dispatchEvent(new Event('change', { bubbles: true }));
                        element.dispatchEvent(new Event('blur', { bubbles: true }));
                    }
                    document.body?.focus();
                    window.alert = originalAlert;
                    return messages;
                })();
                """);
            await Task.Delay(120, cancellationToken);
            var messages = JsonSerializer.Deserialize<string[]>(raw, JsonOptions) ?? [];
            var browserMessage = messages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            var pageResult = await ReadPrescriptionPageResultAsync(browserMessage ?? "Tarihler Medula tarafından kabul edildi.");
            if (!string.IsNullOrWhiteSpace(browserMessage))
            {
                ReportWarning(browserMessage);
                pageResult = pageResult with { WarningText = browserMessage };
            }
            return pageResult;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MedulaPrescriptionPageResult(false, false, "Medula",
                browser?.Source?.AbsoluteUri ?? LoginUrl,
                $"Tarih doğrulaması tamamlanamadı: {exception.Message}", string.Empty);
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private async Task<bool> EnsurePrescriptionReadyAsync(
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken)
    {
        if (!await ConnectWithRetriesAsync(cancellationToken)) return false;
        if (!await EnsurePrescriptionContextAsync(input, cancellationToken)) return false;
        await ReturnToPrescriptionCoreAsync(cancellationToken);
        return await IsPrescriptionEntryPageAsync();
    }

    private async Task<bool> EnsurePrescriptionReadyOrSearchOpenAsync(CancellationToken cancellationToken)
    {
        if (!await ConnectWithRetriesAsync(cancellationToken)) return false;
        if (await IsPrescriptionEntryPageAsync()) return true;
        return false;
    }

    private async Task ApplyPrescriptionInputAsync(
        MedulaPrescriptionInput input,
        CancellationToken cancellationToken,
        bool synchronizeDrugs = true)
    {
        if (!systemErrorRecoveryInProgress)
            lastRestorationState = lastRestorationState with { Input = input };
        var payload = BuildPrescriptionPayload(input);
        var valuesJson = JsonSerializer.Serialize(payload.Values);
        var checksJson = JsonSerializer.Serialize(payload.Checks);
        await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                const values = {{valuesJson}};
                const checks = {{checksJson}};
                for (const [id, value] of Object.entries(values)) {
                    const element = document.getElementById(id);
                    if (!element || element.disabled || element.readOnly) continue;
                    element.value = value ?? '';
                }
                document.querySelectorAll('input[id^="f:tbl1:"][id$=":checkbox7"]')
                    .forEach(element => element.checked = false);
                for (const [id, checked] of Object.entries(checks)) {
                    const element = document.getElementById(id);
                    if (!element || element.disabled) continue;
                    element.checked = !!checked;
                }
                return true;
            })();
            """);
        cancellationToken.ThrowIfCancellationRequested();
        if (synchronizeDrugs)
            await SynchronizePrescriptionDrugRowsAsync(input.Drugs, cancellationToken);
    }

    private async Task SynchronizePrescriptionDrugRowsAsync(
        IReadOnlyList<MedulaDrugEntry> drugs,
        CancellationToken cancellationToken)
    {
        if (drugs.Count == 0) return;
        var current = await ReadPrescriptionDrugsAsync();
        for (var rowIndex = 0; rowIndex < drugs.Count; rowIndex++)
        {
            var barcode = drugs[rowIndex].Barcode.Trim();
            if (barcode.Length == 0) continue;
            if (hydratedPrescriptionDrugRows.TryGetValue(rowIndex, out var hydrated)
                && hydrated.Equals(barcode, StringComparison.Ordinal)) continue;
            var currentRow = current.ElementAtOrDefault(rowIndex);
            if (currentRow is not null
                && currentRow.Barcode.Equals(barcode, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(currentRow.Name))
            {
                hydratedPrescriptionDrugRows[rowIndex] = barcode;
                continue;
            }

            var rowExists = await RequireBrowser().ExecuteScriptAsync(
                $"!!document.getElementById('f:tbl1:{rowIndex}:t1')");
            while (!string.Equals(rowExists, "true", StringComparison.OrdinalIgnoreCase))
            {
                var added = await ClickDrugControlAsync("f:buttonIlacEkle", false, cancellationToken);
                var warning = MedulaBrowserWarning.First(added.Messages, added.Confirmations);
                if (!added.Clicked || warning is not null)
                {
                    if (warning is not null) ReportWarning(warning);
                    return;
                }
                rowExists = await RequireBrowser().ExecuteScriptAsync(
                    $"!!document.getElementById('f:tbl1:{rowIndex}:t1')");
            }

            await SetBarcodeAndSelectedDrugRowAsync(rowIndex, barcode);
            var resolved = await ResolvePrescriptionDrugBarcodeAsync(
                rowIndex, "Barkod Medula'da tanındı.", cancellationToken);
            if (!resolved.IsSuccess)
            {
                if (!string.IsNullOrWhiteSpace(resolved.WarningText))
                    ReportWarning(resolved.WarningText);
                return;
            }
            hydratedPrescriptionDrugRows[rowIndex] = barcode;
            current = resolved.Drugs;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < drugs.Count; index++)
        {
            var drug = drugs[index];
            values[$"f:tbl1:{index}:t2"] = drug.Quantity;
            values[$"f:tbl1:{index}:t5"] = drug.Period;
            values[$"f:tbl1:{index}:m1"] = drug.PeriodUnit;
            values[$"f:tbl1:{index}:t3"] = drug.DoseCount;
            values[$"f:tbl1:{index}:t4"] = drug.DoseAmount;
        }
        var valuesJson = JsonSerializer.Serialize(values);
        await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                const values = {{valuesJson}};
                document.querySelectorAll('input[id^="f:tbl1:"][id$=":checkbox7"]')
                    .forEach(element => element.checked = false);
                for (const [id, value] of Object.entries(values)) {
                    const element = document.getElementById(id);
                    if (element) element.value = value ?? '';
                }
                return true;
            })();
            """);
    }

    private Task<ActionClickResult> ClickDrugControlAsync(
        string actionId,
        bool acceptConfirmation,
        CancellationToken cancellationToken)
    {
        var actionIdJson = JsonSerializer.Serialize(actionId);
        var confirmationJson = acceptConfirmation ? "true" : "false";
        return ExecuteActionAndWaitAsync($$"""
            (() => {
                const messages = [];
                const confirmations = [];
                const originalAlert = window.alert;
                const originalConfirm = window.confirm;
                window.alert = message => messages.push(String(message ?? ''));
                window.confirm = message => {
                    if (!{{confirmationJson}}) confirmations.push(String(message ?? ''));
                    return {{confirmationJson}};
                };
                try {
                    const action = document.getElementById({{actionIdJson}});
                    if (!action) return { clicked: false, messages, confirmations };
                    action.click();
                    return { clicked: true, messages, confirmations };
                } finally {
                    window.alert = originalAlert;
                    window.confirm = originalConfirm;
                }
            })();
            """, cancellationToken);
    }

    private async Task<bool> IsDrugInfoScreenAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const text = String(document.body?.innerText || document.body?.textContent || '');
                return !!document.getElementById('form1:buttonGeriDon') && /İlaç\s+Bilgileri/i.test(text);
            })();
            """);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ReadDrugOperationWarningAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const pattern = /(?:barkodlu ilacın eşdeğeri bulunamadı|ilaç seçiniz|ilaç bulunamadı|geçersiz barkod|barkod.*(?:hatalı|bulunamadı)|ilaç.*(?:hatalı|uygun değildir))/i;
                const lines = String(document.body?.innerText || '').split(/\r?\n/).map(clean)
                    .filter(line => line && line.length <= 600 && pattern.test(line));
                return lines[0] || '';
            })();
            """);
        var warning = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        return string.IsNullOrWhiteSpace(warning) ? null : warning;
    }

    private async Task<DrugSearchPageProbe> ReadDrugSearchPageAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const table = document.getElementById('f:tableExIlacList');
                if (!table || table.getClientRects().length === 0)
                    return { pageIndex: 0, fingerprint: '', nextActionId: '', options: [] };
                const pager = document.getElementById('f:tableExIlacList:web3__pagerWeb');
                const pageIndex = Number(pager?.value ?? 0);
                const options = [...table.querySelectorAll('[id*="rowActionIlacSec"]')]
                  .filter(action => action.getClientRects().length > 0
                    && /^f:tableExIlacList:\d+:rowActionIlacSec$/.test(action.id))
                  .map(action => {
                    const match = action.id.match(/^f:tableExIlacList:(\d+):rowActionIlacSec$/);
                    const index = match?.[1];
                    return {
                        actionId: action.id,
                        barcode: clean(document.getElementById(`f:tableExIlacList:${index}:text151`)?.textContent),
                        name: clean(document.getElementById(`f:tableExIlacList:${index}:text152`)?.textContent),
                        pageIndex
                    };
                  }).filter(item => item.barcode || item.name);
                const pagerTarget = action => {
                    const match = String(action.getAttribute('onclick') || '')
                      .match(/JSFWebPagerSubmit\([^,]+,\s*(\d+)\s*,/);
                    return match ? Number(match[1]) : -1;
                };
                const next = [...document.querySelectorAll('a[id]')].find(action =>
                    action.getClientRects().length > 0
                    && action.id.startsWith('f:tableExIlacList:web3__pagerWeb')
                    && pagerTarget(action) === pageIndex + 1);
                return {
                    pageIndex,
                    fingerprint: options.map(option => `${option.barcode}\u001f${option.name}`).join('\u001e'),
                    nextActionId: next?.id ?? '',
                    options
                };
            })();
            """);
        return JsonSerializer.Deserialize<DrugSearchPageProbe>(raw, JsonOptions)
            ?? new DrugSearchPageProbe(0, string.Empty, string.Empty, []);
    }

    private async Task<IReadOnlyList<MedulaDrugSearchOption>> ReadAllDrugSearchOptionsAsync(
        CancellationToken cancellationToken)
    {
        var all = new List<MedulaDrugSearchOption>();
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        var seenDrugs = new HashSet<string>(StringComparer.Ordinal);
        for (var pageCount = 0; pageCount < 60; pageCount++)
        {
            var page = await ReadDrugSearchPageAsync();
            var pageKey = $"{page.PageIndex}:{page.Fingerprint}";
            if (!seenPages.Add(pageKey)) break;
            foreach (var option in page.Options)
            {
                if (seenDrugs.Add($"{option.Barcode}\u001f{option.Name}")) all.Add(option);
            }
            if (page.NextActionId.Length == 0) break;

            if (!await NavigateDrugSearchPageAsync(page.PageIndex + 1, cancellationToken)) break;
        }
        return all;
    }

    private async Task<bool> NavigateDrugSearchPageAsync(
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var current = await ReadDrugSearchPageAsync();
        if (current.PageIndex == pageIndex) return true;
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const pagerId = 'f:tableExIlacList:web3__pagerWeb';
                if (!document.getElementById(pagerId)
                    || typeof hX_6 === 'undefined'
                    || typeof hX_6.JSFWebPagerSubmit !== 'function')
                    return { clicked: false, messages: [], confirmations: [] };
                hX_6.JSFWebPagerSubmit(pagerId, {{pageIndex}}, 'f');
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!click.Clicked) return false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var page = await ReadDrugSearchPageAsync();
            if (page.PageIndex == pageIndex && page.Options.Count > 0) return true;
            if (attempt < 29) await Task.Delay(50, cancellationToken);
        }
        return false;
    }

    private async Task WaitForDrugSearchToCloseAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var raw = await RequireBrowser().ExecuteScriptAsync(
                "!!document.getElementById('f:text56') && document.getElementById('f:text56').getClientRects().length > 0");
            if (!string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return;
            if (attempt < 19) await Task.Delay(50, cancellationToken);
        }
    }

    private async Task<DiagnosisSearchPageProbe> ReadDiagnosisSearchPageAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const visible = element => element && element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden';
                const codePattern = /\b[A-Z]\d{2}(?:\.[0-9A-Z]{1,4})?\b/i;
                const actionCaption = element => clean(
                    element?.value || element?.alt || element?.title || element?.innerText || element?.textContent);
                const tables = [...document.querySelectorAll('table[id]')].filter(table =>
                    visible(table)
                    && table.id !== 'f:tableEx1'
                    && table.id !== 'f:tbl1'
                    && !table.closest('#f\\:tableEx1')
                    && !table.closest('#f\\:tbl1'));
                const candidates = tables.map(table => {
                    const options = [...table.querySelectorAll('tr')].map(row => {
                        const cells = [...row.children]
                            .filter(cell => cell.matches('td,th'))
                            .map(cell => clean(cell.innerText || cell.textContent));
                        const joined = cells.join(' ');
                        const code = joined.match(codePattern)?.[0]?.toUpperCase() ?? '';
                        const action = [...row.querySelectorAll(
                            'a[id],input[type="submit"][id],input[type="image"][id],button[id]')].find(element =>
                            visible(element)
                            && !/(?:pager|sorgula|kapat|geri|iptal)/i.test(element.id + ' ' + actionCaption(element)))
                            || (row.id && row.getAttribute('onclick') ? row : null);
                        if (!code || !action) return null;
                        const description = cells
                            .map(value => value.replace(codePattern, '').trim())
                            .filter(value => value && value !== actionCaption(action))
                            .sort((left, right) => right.length - left.length)[0] ?? '';
                        return { actionId: action.id, code, description };
                    }).filter(Boolean);
                    return { table, options };
                }).filter(candidate => candidate.options.length > 0)
                  .sort((left, right) => right.options.length - left.options.length);
                const candidate = candidates[0];
                if (!candidate)
                    return { tableId: '', pagerId: '', pageIndex: 0, fingerprint: '', nextActionId: '', options: [] };

                let scope = candidate.table;
                let pager = null;
                for (let depth = 0; scope && depth < 5 && !pager; depth++, scope = scope.parentElement)
                    pager = scope.querySelector('[id$="__pagerWeb"]');
                const pageIndex = Number(pager?.value ?? 0);
                const pagerTarget = action => {
                    const match = String(action.getAttribute('onclick') || '')
                        .match(/JSFWebPagerSubmit\([^,]+,\s*(\d+)\s*,/);
                    return match ? Number(match[1]) : -1;
                };
                const next = pager ? [...document.querySelectorAll('a[id]')].find(action =>
                    visible(action)
                    && action.id.startsWith(pager.id)
                    && pagerTarget(action) === pageIndex + 1) : null;
                return {
                    tableId: candidate.table.id,
                    pagerId: pager?.id ?? '',
                    pageIndex,
                    fingerprint: candidate.options.map(option =>
                        `${option.code}\u001f${option.description}`).join('\u001e'),
                    nextActionId: next?.id ?? '',
                    options: candidate.options.map(option => ({ ...option, pageIndex }))
                };
            })();
            """);
        return JsonSerializer.Deserialize<DiagnosisSearchPageProbe>(raw, JsonOptions)
            ?? EmptyDiagnosisSearchPage();
    }

    private async Task<IReadOnlyList<MedulaDiagnosisSearchOption>> ReadAllDiagnosisSearchOptionsAsync(
        CancellationToken cancellationToken)
    {
        var all = new List<MedulaDiagnosisSearchOption>();
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        var seenDiagnoses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var pageCount = 0; pageCount < 60; pageCount++)
        {
            var page = await ReadDiagnosisSearchPageAsync();
            if (!seenPages.Add($"{page.PageIndex}:{page.Fingerprint}")) break;
            foreach (var option in page.Options)
            {
                if (seenDiagnoses.Add($"{option.Code}\u001f{option.Description}")) all.Add(option);
            }
            if (page.NextActionId.Length == 0) break;
            if (!await NavigateDiagnosisSearchPageAsync(page.PageIndex + 1, cancellationToken)) break;
        }
        return all;
    }

    private async Task<bool> NavigateDiagnosisSearchPageAsync(
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var current = await ReadDiagnosisSearchPageAsync();
        if (current.PageIndex == pageIndex) return true;
        if (current.PagerId.Length == 0) return pageIndex == 0;
        var pagerIdJson = JsonSerializer.Serialize(current.PagerId);
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const pagerId = {{pagerIdJson}};
                if (!document.getElementById(pagerId)
                    || typeof hX_6 === 'undefined'
                    || typeof hX_6.JSFWebPagerSubmit !== 'function')
                    return { clicked: false, messages: [], confirmations: [] };
                hX_6.JSFWebPagerSubmit(pagerId, {{pageIndex}}, 'f');
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!click.Clicked) return false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var page = await ReadDiagnosisSearchPageAsync();
            if (page.PageIndex == pageIndex && page.Options.Count > 0) return true;
            if (attempt < 29) await Task.Delay(50, cancellationToken);
        }
        return false;
    }

    private async Task<IReadOnlyList<MedulaDiagnosisEntry>> ReadPrescriptionDiagnosesAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                return [...document.querySelectorAll('input[id^="f:tableEx1:"][id$=":text7"]')]
                    .map(codeInput => {
                        const index = codeInput.id.match(/^f:tableEx1:(\d+):text7$/)?.[1];
                        const description = document.getElementById(`f:tableEx1:${index}:text3`);
                        return {
                            code: clean(codeInput.value),
                            description: clean(description?.value || description?.textContent)
                        };
                    }).filter(item => item.code || item.description);
            })();
            """);
        return JsonSerializer.Deserialize<MedulaDiagnosisEntry[]>(raw, JsonOptions) ?? [];
    }

    private static DiagnosisSearchPageProbe EmptyDiagnosisSearchPage() =>
        new(string.Empty, string.Empty, 0, string.Empty, string.Empty, []);

    private async Task<int> FindEmptyDrugRowAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const inputs = [...document.querySelectorAll('input[id^="f:tbl1:"][id$=":t1"]')];
                const empty = inputs.find(input => !(input.value ?? '').trim());
                if (!empty) return -1;
                return Number(empty.id.match(/^f:tbl1:(\d+):t1$/)?.[1] ?? -1);
            })();
            """);
        return int.TryParse(raw, out var index) ? index : -1;
    }

    private async Task SetBarcodeAndSelectedDrugRowAsync(int rowIndex, string barcode)
    {
        var barcodeJson = JsonSerializer.Serialize(barcode);
        await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                document.querySelectorAll('input[id^="f:tbl1:"][id$=":checkbox7"]')
                    .forEach(element => element.checked = false);
                const barcode = document.getElementById('f:tbl1:{{rowIndex}}:t1');
                const selected = document.getElementById('f:tbl1:{{rowIndex}}:checkbox7');
                if (!barcode || !selected) return false;
                barcode.value = {{barcodeJson}};
                barcode.dispatchEvent(new Event('input', { bubbles: true }));
                barcode.dispatchEvent(new Event('change', { bubbles: true }));
                selected.checked = true;
                return true;
            })();
            """);
    }

    private async Task<bool> SelectDrugRowAsync(int rowIndex)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                document.querySelectorAll('input[id^="f:tbl1:"][id$=":checkbox7"]')
                    .forEach(element => element.checked = false);
                const selected = document.getElementById('f:tbl1:{{rowIndex}}:checkbox7');
                if (!selected) return false;
                selected.checked = true;
                return true;
            })();
            """);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<MedulaDrugEntry>> ReadPrescriptionDrugsAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const inputs = [...document.querySelectorAll('input[id^="f:tbl1:"][id$=":t1"]')];
                return inputs.map(barcodeInput => {
                    const index = Number(barcodeInput.id.match(/^f:tbl1:(\d+):t1$/)?.[1] ?? -1);
                    const id = suffix => document.getElementById(`f:tbl1:${index}:${suffix}`);
                    const row = barcodeInput.closest('tr');
                    const cells = row ? [...row.children].filter(cell => cell.matches('td,th')) : [];
                    const cell = position => clean(cells[position]?.innerText || cells[position]?.textContent);
                    const select = id('m1');
                    return {
                        selected: !!id('checkbox7')?.checked,
                        barcode: clean(barcodeInput.value),
                        quantity: clean(id('t2')?.value) || '0',
                        period: clean(id('t5')?.value) || '0',
                        periodUnit: clean(select?.value) || '3',
                        doseCount: clean(id('t3')?.value) || '0',
                        doseAmount: clean(id('t4')?.value) || '0,0',
                        name: cell(3),
                        amount: cell(4),
                        difference: cell(5),
                        shelfQuantity: '',
                        report: cell(6),
                        availableDate: cell(7),
                        dispensingMessage: cell(8)
                    };
                }).filter(item => item.barcode || item.name);
            })();
            """);
        return JsonSerializer.Deserialize<MedulaDrugEntry[]>(raw, JsonOptions) ?? [];
    }

    private async Task<MedulaRemoteScreen> ReadDrugOverlayAsync(string key, string title)
    {
        var source = await ReadRemoteScreenAsync(key, title, $"{title} bilgileri hazır.");
        var excludedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "f:tbl1", "f:tableEx1"
        };
        var prescriptionFieldIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "f:t18", "f:text37", "f:menu4", "f:menu2", "f:t24", "f:t33", "f:t29", "f:t31",
            "f:m4", "f:m8", "f:t43", "f:m6", "f:t39", "f:t41", "f:t42", "f:m7", "f:text15",
            "f:checkbox1", "f:checkbox2", "f:checkbox4"
        };
        var fields = source.Fields.Where(field =>
                !field.Id.StartsWith("f:tbl1:", StringComparison.Ordinal)
                && !field.Id.StartsWith("f:tableEx1:", StringComparison.Ordinal)
                && !field.Id.Contains(":checkbox", StringComparison.OrdinalIgnoreCase)
                && !prescriptionFieldIds.Contains(field.Id))
            .Select(field => key == "drug-special" && field.Id == "f:menu8"
                ? field with { Label = "Özel Durum" }
                : field)
            .ToArray();
        var tables = source.Tables.Where(table =>
                !excludedIds.Contains(table.Id)
                && !table.Id.StartsWith("f:tbl1:", StringComparison.Ordinal)
                && !table.Id.StartsWith("f:tableEx1:", StringComparison.Ordinal))
            .Select(table => table.Id switch
            {
                "f:tableExRecetedeYazilanIlacList" => RefineEquivalentTable(
                    table, "Reçetede Yazılan İlaçlar"),
                "f:tableExHastayaVerilenIlacList" => RefineEquivalentTable(
                    table, "Hastaya Verilecek İlaçlar"),
                _ => table
            })
            .ToArray();
        var message = fields.Length == 0 && tables.Length == 0
            ? $"Medula bu ilaç için {title.ToLowerInvariant()} bilgisi göstermedi."
            : source.Message;
        return source with { IsOpen = true, Message = message, Fields = fields, Actions = [], Tables = tables };
    }

    private static MedulaRemoteTable RefineEquivalentTable(
        MedulaRemoteTable table,
        string title)
    {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        static string Cell(IReadOnlyList<string> row, int index) =>
            index < row.Count ? row[index] : string.Empty;
        string Difference(string discounted, string paid)
        {
            if (!decimal.TryParse(discounted, NumberStyles.Number, culture, out var discountedValue)
                || !decimal.TryParse(paid, NumberStyles.Number, culture, out var paidValue))
                return "—";
            return (discountedValue - paidValue).ToString("N2", culture);
        }

        var rows = table.Rows.Select(row => (IReadOnlyList<string>)new[]
        {
            Cell(row, 1),
            Cell(row, 2),
            Cell(row, 3),
            Cell(row, 4),
            Difference(Cell(row, 3), Cell(row, 4)),
            "—"
        }).ToArray();
        return table with
        {
            Headers = ["Barkod", "İlaç Adı", "İnd. Fiyat", "Ödenen Fiyat", "Fiyat Farkı", "Stok"],
            Rows = rows,
            Title = title
        };
    }

    private MedulaDrugSearchResult DrugSearchWarning(string warning)
    {
        ReportWarning(warning);
        return new MedulaDrugSearchResult(false, warning, [], warning);
    }

    private MedulaDrugActionResult DrugActionWarning(
        string warning,
        IReadOnlyList<MedulaDrugEntry>? drugs = null)
    {
        ReportWarning(warning);
        return new MedulaDrugActionResult(false, warning, drugs ?? [], null, warning);
    }

    private static MedulaDrugActionResult EmptyDrugAction(string message) =>
        new(false, message, []);

    private MedulaDiagnosisSearchResult DiagnosisSearchWarning(string warning)
    {
        ReportWarning(warning);
        return new MedulaDiagnosisSearchResult(false, warning, [], warning);
    }

    private MedulaDiagnosisActionResult DiagnosisActionWarning(
        string warning,
        IReadOnlyList<MedulaDiagnosisEntry>? diagnoses = null)
    {
        ReportWarning(warning);
        return new MedulaDiagnosisActionResult(false, warning, diagnoses ?? [], warning);
    }

    private static MedulaDiagnosisActionResult EmptyDiagnosisAction(string message) =>
        new(false, message, []);

    public async Task ClosePrescriptionDrugOverlayAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (key is not ("drug-special" or "drug-equivalent")) return;
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (browser?.CoreWebView2 is null) return;
            var actionId = key == "drug-special"
                ? "f:buttonOzelDurumKapat"
                : "f:buttonEsdegerSecimTamam";
            var click = await ClickDrugControlAsync(actionId, false, cancellationToken);
            var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (warning is not null) ReportWarning(warning);
            else if (click.Clicked && !systemErrorRecoveryInProgress)
                lastRestorationState = lastRestorationState with { Steps = [] };
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaDrugActionResult> SetPrescriptionDrugSpecialConditionAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsurePrescriptionReadyOrSearchOpenAsync(cancellationToken))
                return EmptyDrugAction("Medula Reçete Giriş ekranı açılamadı.");
            var valueJson = JsonSerializer.Serialize(value);
            var click = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    window.alert = message => messages.push(String(message ?? ''));
                    try {
                        const select = document.getElementById('f:menu8');
                        if (!select) return { clicked: false, messages, confirmations };
                        select.value = {{valueJson}};
                        select.dispatchEvent(new Event('input', { bubbles: true }));
                        select.dispatchEvent(new Event('change', { bubbles: true }));
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                    }
                })();
                """, cancellationToken);
            var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (warning is not null) return DrugActionWarning(warning, await ReadPrescriptionDrugsAsync());
            return click.Clicked
                ? new MedulaDrugActionResult(true, "Özel durum Medula'ya aktarıldı.", await ReadPrescriptionDrugsAsync())
                : EmptyDrugAction("Medula özel durum alanı bulunamadı.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyDrugAction($"Özel durum aktarılamadı: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaRemoteScreen> OpenPrescriptionSubScreenAsync(
        MedulaPrescriptionInput input,
        string actionId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!PrescriptionSubScreenActions.TryGetValue(actionId, out var screenKey))
            throw new ArgumentOutOfRangeException(nameof(actionId), actionId, "Unsupported Medula sub-screen action.");

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyRemoteScreen(screenKey, title, "Medula bağlantısı kurulamadı.");

            var requestedRestoration = BuildPrescriptionRestorationState(
                input,
                [new MedulaRestorationStep(actionId, EmptyRemotePayload(), null)]);
            activeOperationRestorationState = requestedRestoration;
            if (await IsSystemErrorPageAsync())
            {
                var recovered = await RecoverFromSystemErrorDuringOperationAsync(
                    requestedRestoration, cancellationToken);
                if (!recovered)
                    return EmptyRemoteScreen(
                        screenKey, title, "Medula Sistem Hatası sonrasında yeniden açılamadı.",
                        browser?.Source?.AbsoluteUri ?? string.Empty);
                if (await WaitForPageStateAsync(
                        () => IsExpectedSubScreenNowAsync(screenKey), cancellationToken))
                {
                    var recoveredScreen = await ReadRemoteScreenAsync(
                        screenKey, title, $"{title} ekranı Sistem Hatası sonrasında geri yüklendi.");
                    RememberTopLevelScreen(input, actionId, recoveredScreen);
                    return recoveredScreen;
                }
            }

            var accessRoute = MedulaPrescriptionAccessPolicy.Resolve(input);
            if (accessRoute != MedulaPrescriptionAccessRoute.PrescriptionEntry)
            {
                if (!await EnsurePrescriptionContextAsync(input, cancellationToken))
                    return EmptyRemoteScreen(
                        screenKey,
                        title,
                        "Reçete doğru Medula sorgu ekranından güvenli biçimde geri yüklenemedi. İşlem uygulanmadı.",
                        browser?.Source?.AbsoluteUri ?? string.Empty);
            }
            await ReturnToPrescriptionCoreAsync(cancellationToken);
            if (!await IsPrescriptionEntryPageAsync())
            {
                var reloaded = await OpenPrescriptionEntryCoreAsync(cancellationToken);
                if (!reloaded.IsPrescriptionEntryPage)
                    return EmptyRemoteScreen(
                        screenKey,
                        title,
                        "Medula Reçete Giriş ekranı otomatik olarak açılamadı.",
                        reloaded.Url);
            }

            for (var staleRetryAttempt = 0; staleRetryAttempt < 2; staleRetryAttempt++)
            {
                // Report selection belongs to the live, unsaved e-prescription state. Reapplying the
                // frontend model here can erase Medula-only transient recipient/report metadata and
                // makes the native overlay refuse to open.
                var synchronizeInput = MedulaSubScreenSafety.ShouldSynchronizePrescriptionInput(actionId);
                if (synchronizeInput)
                    await ApplyPrescriptionInputAsync(input, cancellationToken);
            activeOperationRestorationState = BuildPrescriptionRestorationState(
                input,
                [new MedulaRestorationStep(actionId, EmptyRemotePayload(), null)]);
            var payload = BuildPrescriptionPayload(input);
            var valuesJson = !synchronizeInput
                ? "{}"
                : JsonSerializer.Serialize(payload.Values);
            var checksJson = !synchronizeInput
                ? "{}"
                : JsonSerializer.Serialize(payload.Checks);
            var actionIdJson = JsonSerializer.Serialize(actionId);
            var warningBeforeClick = await ReadVisibleMedulaWarningAsync();
            var warningRevision = GetWarningRevision();
            var script = $$"""
                (() => {
                    const values = {{valuesJson}};
                    const checks = {{checksJson}};
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => {
                        confirmations.push(String(message ?? ''));
                        return false;
                    };
                    for (const [id, value] of Object.entries(values)) {
                        const element = document.getElementById(id);
                        if (!element) continue;
                        element.value = value ?? '';
                    }
                    for (const [id, checked] of Object.entries(checks)) {
                        const element = document.getElementById(id);
                        if (!element) continue;
                        element.checked = !!checked;
                    }
                    const requestedActionId = {{actionIdJson}};
                    const action = document.getElementById(requestedActionId)
                        ?? (requestedActionId === 'medula:buttonUtsIslemleri'
                            ? [...document.querySelectorAll(
                                'button,input[type=button],input[type=submit],input[type=image],a')]
                                .find(element => String(element.value || element.alt || element.title
                                    || element.innerText || element.textContent || '')
                                    .replace(/\s+/g, ' ').trim().toLocaleLowerCase('tr-TR')
                                    === 'üts işlemleri')
                            : null);
                    try {
                        if (!action) return { clicked: false, messages, confirmations };
                        document.querySelectorAll(
                            'input,select,textarea,button,a,[role="dialog"]')
                            .forEach(element => {
                                if (element.getClientRects().length > 0)
                                    element.setAttribute('data-pharmacy-subscreen-before-click', 'true');
                                else
                                    element.removeAttribute('data-pharmacy-subscreen-before-click');
                            });
                        action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """;
            var click = await ExecuteActionAndWaitAsync(
                script,
                cancellationToken,
                waitForNavigation: false);
            if (!click.Clicked)
                return EmptyRemoteScreen(screenKey, title, $"{title} denetimi Medula sayfasında bulunamadı.");

            var immediateWarning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (immediateWarning is not null)
            {
                if (MedulaSystemErrorDetector.IsSystemError(
                        browser?.Source?.AbsoluteUri, null, immediateWarning,
                        hasKnownMedulaControls: false) &&
                    activeOperationRestorationState is { } restoration &&
                    await RecoverFromSystemErrorDuringOperationAsync(
                        restoration, cancellationToken, systemErrorSignalled: true))
                {
                    if (await WaitForPageStateAsync(
                            () => IsExpectedSubScreenNowAsync(screenKey), cancellationToken))
                    {
                        var recoveredScreen = await ReadRemoteScreenAsync(
                            screenKey,
                            title,
                            $"{title} ekranı Sistem Hatası sonrasında geri yüklendi.");
                        RememberTopLevelScreen(input, actionId, recoveredScreen);
                        return recoveredScreen;
                    }
                    if (await WaitForPageStateAsync(IsPrescriptionEntryPageAsync, cancellationToken))
                        continue;
                }
                if (MedulaSubScreenSafety.ShouldRetryAfterPrescriptionReload(
                        immediateWarning, staleRetryAttempt))
                {
                    var reloaded = await ReloadPrescriptionContextAsync(input, cancellationToken);
                    if (reloaded.IsPrescriptionEntryPage) continue;
                }
                return await RecoverRemoteScreenWarningAsync(
                    screenKey, title, immediateWarning, cancellationToken, input);
            }

            var destination = await WaitForExpectedSubScreenAsync(
                screenKey,
                warningBeforeClick,
                warningRevision,
                cancellationToken);
            if (destination.SessionExpiredDetected)
            {
                const string expiredMessage = "Medula oturumu sona erdi; yeniden giriş yapılıyor.";
                ReportWarning(expiredMessage);
                if (activeOperationRestorationState is { } restoration
                    && await RecoverFromExpiredSessionDuringOperationAsync(restoration, cancellationToken))
                {
                    if (await WaitForPageStateAsync(
                            () => IsExpectedSubScreenNowAsync(screenKey), cancellationToken))
                    {
                        var recoveredScreen = await ReadRemoteScreenAsync(
                            screenKey,
                            title,
                            $"{title} ekranı Medula oturumu yenilendikten sonra geri yüklendi.");
                        RememberTopLevelScreen(input, actionId, recoveredScreen);
                        return recoveredScreen;
                    }
                    if (await WaitForPageStateAsync(IsPrescriptionEntryPageAsync, cancellationToken))
                        continue;
                }
                return EmptyRemoteScreen(
                    screenKey,
                    title,
                    expiredMessage,
                    browser?.Source?.AbsoluteUri ?? string.Empty);
            }
            if (destination.SystemErrorDetected)
            {
                var error = destination.Warning ?? "Yazılımsal veya sistemsel bir hata oluştu.";
                ReportWarning(error);
                if (activeOperationRestorationState is { } restoration
                    && await RecoverFromSystemErrorDuringOperationAsync(restoration, cancellationToken))
                {
                    if (await WaitForPageStateAsync(
                            () => IsExpectedSubScreenNowAsync(screenKey), cancellationToken))
                    {
                        var recoveredScreen = await ReadRemoteScreenAsync(
                            screenKey,
                            title,
                            $"{title} ekranı Sistem Hatası sonrasında geri yüklendi.");
                        RememberTopLevelScreen(input, actionId, recoveredScreen);
                        return recoveredScreen;
                    }
                    if (await WaitForPageStateAsync(IsPrescriptionEntryPageAsync, cancellationToken))
                        continue;
                }
                return EmptyRemoteScreen(screenKey, title, error, browser?.Source?.AbsoluteUri ?? string.Empty);
            }
            if (!destination.DestinationReached)
            {
                if (destination.Warning is not null)
                {
                    if (MedulaSubScreenSafety.ShouldRetryAfterPrescriptionReload(
                            destination.Warning, staleRetryAttempt))
                    {
                        var reloaded = await ReloadPrescriptionContextAsync(input, cancellationToken);
                        if (reloaded.IsPrescriptionEntryPage) continue;
                    }
                    return await RecoverRemoteScreenWarningAsync(
                        screenKey, title, destination.Warning, cancellationToken, input);
                }
                var page = await ReadPrescriptionPageResultAsync(
                    $"Medula {title} ekranını açmadı; Reçete ekranı değiştirilmedi.");
                if (MedulaSubScreenSafety.RequiresPrescriptionReload(page.Message)
                    || MedulaSubScreenSafety.RequiresPrescriptionReload(page.PageText))
                {
                    if (MedulaSubScreenSafety.ShouldRetryAfterPrescriptionReload(
                            page.Message + "\n" + page.PageText, staleRetryAttempt))
                    {
                        var retryReload = await ReloadPrescriptionContextAsync(input, cancellationToken);
                        if (retryReload.IsPrescriptionEntryPage) continue;
                    }
                    var reloaded = await ReloadPrescriptionContextAsync(input, cancellationToken);
                    return EmptyRemoteScreen(
                        screenKey,
                        title,
                        reloaded.IsPrescriptionEntryPage
                            ? "Medula Reçete Giriş ekranı otomatik olarak yenilendi. İşlemi tekrar deneyin."
                            : page.Message,
                        reloaded.Url);
                }
                return EmptyRemoteScreen(screenKey, title, page.Message, page.Url);
            }

            var openedScreen = await ReadRemoteScreenAsync(
                screenKey,
                title,
                $"{title} ekranı hazır.");
            RememberTopLevelScreen(input, actionId, openedScreen);
            return openedScreen;
            }

            return EmptyRemoteScreen(
                screenKey,
                title,
                $"Medula {title} ekranını iki denemede açmadı.",
                browser?.Source?.AbsoluteUri ?? string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyRemoteScreen(screenKey, title, $"{title} ekranı açılamadı: {exception.Message}");
        }
        finally
        {
            activeOperationRestorationState = null;
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaRemoteScreen> LookupPatientFromFreshPrescriptionEntryAsync(
        string tcIdentityNumber,
        MedulaPrescriptionInput restoreInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restoreInput);
        var tc = tcIdentityNumber.Trim();
        if (tc.Length != 11 || tc.Any(character => !char.IsDigit(character)))
            return EmptyRemoteScreen("off-label-old", "Hasta Doğrulama", "Geçerli bir T.C. Kimlik No giriniz.");

        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyRemoteScreen("off-label-old", "Hasta Doğrulama", "Medula bağlantısı kurulamadı.");

            var restoration = BuildPrescriptionRestorationState(restoreInput, []);
            lastRestorationState = restoration;
            var systemRecoveryAttempted = false;
        RetryPatientLookup:
            var freshPage = await ReloadPrescriptionEntryThroughHomeAsync(cancellationToken);
            if (!freshPage.IsPrescriptionEntryPage)
                return EmptyRemoteScreen(
                    "off-label-old",
                    "Hasta Doğrulama",
                    "Hasta doğrulaması için boş Reçete Giriş ekranı açılamadı.",
                    freshPage.Url);

            var today = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var lookupInput = restoreInput with
            {
                PatientIdentityNumber = tc,
                RecipientIdentityNumber = string.Empty,
                RecipientType = "0",
                ProtocolNumber = string.Empty,
                FacilityCode = string.Empty,
                PrescriptionDate = today,
                DispensingDate = today,
                RegistrationNumber = string.Empty,
                PhysicianRegistrationNumber = string.Empty,
                PhysicianFirstName = string.Empty,
                PhysicianLastName = string.Empty,
                Drugs = [],
                Diagnoses = [],
                SourceListItem = null
            };
            await ApplyPrescriptionInputAsync(lookupInput, cancellationToken);

            var warningBeforeClick = await ReadVisibleMedulaWarningAsync();
            var warningRevision = GetWarningRevision();
            var click = await ExecuteActionAndWaitAsync(
                """
                (() => {
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => {
                        confirmations.push(String(message ?? ''));
                        return false;
                    };
                    try {
                        const action = document.getElementById('f:buttonEndikasyonDisiSorgu');
                        if (!action) return { clicked: false, messages, confirmations };
                        action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """,
                cancellationToken,
                waitForNavigation: false);
            if (!click.Clicked)
                return EmptyRemoteScreen(
                    "off-label-old",
                    "Hasta Doğrulama",
                    "End. Dışı denetimi Medula Reçete Giriş ekranında bulunamadı.");

            var immediateWarning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (immediateWarning is not null)
            {
                if (!systemRecoveryAttempted &&
                    MedulaSystemErrorDetector.IsSystemError(
                        browser?.Source?.AbsoluteUri, null, immediateWarning,
                        hasKnownMedulaControls: false) &&
                    await RecoverFromSystemErrorDuringOperationAsync(
                        restoration, cancellationToken, systemErrorSignalled: true))
                {
                    systemRecoveryAttempted = true;
                    goto RetryPatientLookup;
                }
                ReportWarning(immediateWarning);
                return EmptyRemoteScreen(
                    "off-label-old",
                    "Hasta Doğrulama",
                    immediateWarning,
                    browser?.Source?.AbsoluteUri ?? string.Empty);
            }

            var destination = await WaitForExpectedSubScreenAsync(
                "off-label-old",
                warningBeforeClick,
                warningRevision,
                cancellationToken);
            if (!destination.DestinationReached)
            {
                var warning = destination.Warning ?? "Hasta bilgileri Medula'da açılamadı.";
                if (!systemRecoveryAttempted &&
                    MedulaSystemErrorDetector.IsSystemError(
                        browser?.Source?.AbsoluteUri, null, warning,
                        hasKnownMedulaControls: false) &&
                    await RecoverFromSystemErrorDuringOperationAsync(
                        restoration, cancellationToken, systemErrorSignalled: true))
                {
                    systemRecoveryAttempted = true;
                    goto RetryPatientLookup;
                }
                ReportWarning(warning);
                return EmptyRemoteScreen(
                    "off-label-old",
                    "Hasta Doğrulama",
                    warning,
                    browser?.Source?.AbsoluteUri ?? string.Empty);
            }

            return await ReadRemoteScreenAsync(
                "off-label-old",
                "Hasta Doğrulama",
                "Hasta bilgileri Medula'dan okundu.");
        }
        finally
        {
            try
            {
                await ReturnToPrescriptionCoreAsync(cancellationToken);
                if (MedulaPrescriptionAccessPolicy.Resolve(restoreInput)
                    != MedulaPrescriptionAccessRoute.PrescriptionEntry)
                {
                    await EnsurePrescriptionContextAsync(restoreInput, cancellationToken);
                }
                else
                {
                    var restored = await ReloadPrescriptionEntryThroughHomeAsync(cancellationToken);
                    if (restored.IsPrescriptionEntryPage)
                        await ApplyPrescriptionInputAsync(restoreInput, cancellationToken);
                }
                lastRestorationState = BuildPrescriptionRestorationState(restoreInput, []);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportWarning($"Hasta doğrulamasından sonra reçete bağlamı geri yüklenemedi: {exception.Message}");
            }
            finally
            {
                prescriptionGate.Release();
            }
        }
    }

    public async Task<MedulaRemoteScreen> ReadDoctorBranchSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyRemoteScreen(
                    "doctor-branch-select", "Branş Seç", "Medula bağlantısı kurulamadı.");

            return await ReadRemoteScreenAsync(
                "doctor-branch-select",
                "Branş Seç",
                "Doktorun Medula'daki branşlarından birini seçin.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyRemoteScreen(
                "doctor-branch-select", "Branş Seç", $"Branş listesi okunamadı: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private async Task<MedulaRemoteScreen> RecoverRemoteScreenWarningAsync(
        string screenKey,
        string title,
        string warning,
        CancellationToken cancellationToken,
        MedulaPrescriptionInput? input = null)
    {
        // Uyarı Kodu (Yeni) can render its inline panel and emit an alert in the
        // same synchronous click. Preserve the panel as an explicit-close popup;
        // otherwise it remains invisibly open in Medula and blocks the next action.
        if (screenKey == "new-warning-code" && await IsExpectedSubScreenNowAsync(screenKey))
            return await ReadRemoteScreenAsync(screenKey, title, warning);
        if (!MedulaSubScreenSafety.RequiresPrescriptionReload(warning))
            return EmptyRemoteScreen(
                screenKey, title, warning, browser?.Source?.AbsoluteUri ?? string.Empty);

        var reloaded = input is null
            ? await ReloadPrescriptionEntryThroughHomeAsync(cancellationToken)
            : await ReloadPrescriptionContextAsync(input, cancellationToken);
        return EmptyRemoteScreen(
            screenKey,
            title,
            reloaded.IsPrescriptionEntryPage
                ? $"{warning}\nMedula Ana Sayfa üzerinden Reçete Giriş ekranına otomatik olarak dönüldü."
                : warning,
            reloaded.Url);
    }

    public async Task<MedulaRemoteScreen> ExecuteSubScreenActionAsync(
        string screenKey,
        string title,
        string actionId,
        MedulaRemoteFieldPayload payload,
        CancellationToken cancellationToken = default,
        bool confirmationAccepted = false)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyRemoteScreen(screenKey, title, "Medula bağlantısı kurulamadı.");

            var actionCaption = await ReadActionCaptionAsync(actionId);
            var allowed = await IsAllowedSubScreenActionAsync(screenKey, actionId, actionCaption);
            if (!allowed)
                return await ReadRemoteScreenAsync(screenKey, title, "Bu işlem güvenli Reçete alt ekranı kapsamına dahil değil.");
            var opensEyeDrugs = screenKey == "drugs"
                && actionCaption.Equals("Göz İlaçları", StringComparison.OrdinalIgnoreCase);
            var isItsSaleNotification = screenKey == "its-qr"
                && actionCaption.Contains("Satış Bildirimi", StringComparison.CurrentCultureIgnoreCase);
            var isItsSaleCancellation = screenKey == "its-qr"
                && actionCaption.Contains("Satış İptal", StringComparison.CurrentCultureIgnoreCase);
            if (opensEyeDrugs)
                await EnsureRemoteTableFirstPageAsync("form1:tableExKisiIlacList", cancellationToken);

            var warningBeforeClick = await ReadVisibleMedulaWarningAsync();
            var warningRevision = GetWarningRevision();
            var valuesJson = JsonSerializer.Serialize(payload.Values);
            var checksJson = JsonSerializer.Serialize(payload.Checks);
            var actionIdJson = JsonSerializer.Serialize(actionId);
            var script = $$"""
                (() => {
                    const values = {{valuesJson}};
                    const checks = {{checksJson}};
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => {
                        confirmations.push(String(message ?? ''));
                        return {{JsonSerializer.Serialize(confirmationAccepted)}};
                    };
                    for (const [id, value] of Object.entries(values)) {
                        const element = document.getElementById(id);
                        if (!element || element.disabled || element.readOnly) continue;
                        element.value = value ?? '';
                    }
                    for (const [id, checked] of Object.entries(checks)) {
                        const element = document.getElementById(id);
                        if (!element || element.disabled) continue;
                        element.checked = !!checked;
                    }
                    const action = document.getElementById({{actionIdJson}});
                    try {
                        if (!action) return { clicked: false, messages, confirmations };
                        action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """;
            var click = await ExecuteActionAndWaitAsync(script, cancellationToken);
            var confirmation = click.Confirmations.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            if (!confirmationAccepted && !string.IsNullOrWhiteSpace(confirmation))
            {
                var pending = await ReadRemoteScreenAsync(screenKey, title, "Medula işlemi onay bekliyor.");
                return pending with { ConfirmationMessage = confirmation };
            }

            if (isItsSaleNotification || isItsSaleCancellation)
            {
                var outcome = await ObserveItsSaleNotificationOutcomeAsync(
                    click.Messages, warningBeforeClick, warningRevision, cancellationToken,
                    isItsSaleCancellation);
                var outcomeScreen = await ReadRemoteScreenAsync(
                    screenKey,
                    title,
                    outcome.Message ?? outcome.Warning ?? (isItsSaleCancellation
                        ? "Medula ITS satış iptal sonucu doğrulanamadı."
                        : "Medula ITS satış sonucu doğrulanamadı."));
                if (outcome.Success)
                {
                    ReportConfirmation(outcome.Message!);
                    return outcomeScreen with
                    {
                        Message = outcome.Message!,
                        RemoteSuccessConfirmed = true,
                        ConfirmationMessage = null
                    };
                }

                var failure = outcome.Warning ?? (isItsSaleCancellation
                    ? "Medula ITS satış iptalini doğrulamadı; yerel iade oluşturulmadı."
                    : "Medula ITS satış bildirimini doğrulamadı; yerel satış oluşturulmadı.");
                ReportWarning(failure);
                return outcomeScreen with
                {
                    Message = failure,
                    RemoteSuccessConfirmed = false,
                    ConfirmationMessage = null
                };
            }

            if (MedulaSubScreenSafety.ShouldReturnToPrescriptionAfterAccept(
                    screenKey, actionCaption))
            {
                var confirmationText = MedulaBrowserWarning.First(
                    click.Messages,
                    confirmationAccepted ? [] : click.Confirmations);
                if (!string.IsNullOrWhiteSpace(confirmationText)
                    && IsTreatmentSchemeValidationFailure(confirmationText))
                {
                    ReportWarning(confirmationText);
                    return await ReadRemoteScreenAsync(screenKey, title, confirmationText);
                }

                if (!string.IsNullOrWhiteSpace(confirmationText))
                    ReportConfirmation(confirmationText);
                await ReturnToPrescriptionCoreAsync(cancellationToken);
                var page = await ReadPrescriptionPageResultAsync(
                    confirmationText ?? "Tedavi şeması Medula'ya aktarıldı.");
                var returned = page.IsPrescriptionEntryPage;
                return new MedulaRemoteScreen(
                    false,
                    screenKey,
                    title,
                    page.Url,
                    returned
                        ? confirmationText ?? "Tedavi şeması Medula'ya aktarıldı."
                        : page.WarningText ?? confirmationText ?? "Tedavi şeması ekranından Reçete ekranına dönülemedi.",
                    [],
                    [],
                    [],
                    RemoteSuccessConfirmed: returned);
            }

            var rawWarning = MedulaBrowserWarning.First(
                click.Messages,
                confirmationAccepted ? [] : click.Confirmations);
            if (rawWarning is not null)
                return MedulaSubScreenSafety.RequiresPrescriptionReload(rawWarning)
                    ? await RecoverRemoteScreenWarningAsync(screenKey, title, rawWarning, cancellationToken)
                    : await ReadRemoteScreenAsync(screenKey, title, rawWarning);

            var visibleWarning = await ReadVisibleMedulaWarningAsync();
            var expectedDoctorBranchWarning = screenKey == "doctor-branch-select"
                && IsDoctorBranchSelectionWarning(visibleWarning);
            if (!string.IsNullOrWhiteSpace(visibleWarning)
                && !expectedDoctorBranchWarning
                && (!string.Equals(visibleWarning, warningBeforeClick, StringComparison.Ordinal)
                    || GetWarningRevision() > warningRevision))
            {
                ReportWarning(visibleWarning);
                return MedulaSubScreenSafety.RequiresPrescriptionReload(visibleWarning)
                    ? await RecoverRemoteScreenWarningAsync(screenKey, title, visibleWarning, cancellationToken)
                    : await ReadRemoteScreenAsync(screenKey, title, visibleWarning);
            }

            if (screenKey == "report-selection"
                && actionCaption.Equals("Tamam", StringComparison.OrdinalIgnoreCase))
            {
                for (var attempt = 0; attempt < 10 && await IsExpectedSubScreenNowAsync(screenKey); attempt++)
                    await Task.Delay(50, cancellationToken);
                var page = await ReadPrescriptionPageResultAsync("Rapor seçimi Medula'ya aktarıldı.");
                return new MedulaRemoteScreen(
                    false,
                    screenKey,
                    title,
                    page.Url,
                    "Rapor seçimi Medula'ya aktarıldı.",
                    [],
                    [],
                    [],
                    RemoteSuccessConfirmed: page.IsPrescriptionEntryPage);
            }

            if (screenKey == "doctor-branch-select")
            {
                var page = await ReadPrescriptionPageResultAsync("Branş seçimi Medula'ya aktarıldı.");
                return EmptyRemoteScreen(
                    screenKey,
                    title,
                    page.WarningText ?? "Branş seçimi Medula'ya aktarıldı.",
                    page.Url);
            }

            var message = click.Clicked ? "Medula ekranı güncellendi." : "İşlem denetimi bulunamadı.";
            var opensNewReport = screenKey == "reports"
                && (actionCaption.Equals("Yeni Kayıt", StringComparison.OrdinalIgnoreCase)
                    || actionCaption.Equals("Yeni Rapor", StringComparison.OrdinalIgnoreCase));
            if (opensEyeDrugs)
            {
                var destination = await WaitForExpectedSubScreenAsync(
                    "eye-drugs", warningBeforeClick, warningRevision, cancellationToken);
                if (!destination.DestinationReached)
                {
                    var warning = destination.Warning
                        ?? "Medula gerçek Göz İlaçları sayfasını açmadı.";
                    if (!string.IsNullOrWhiteSpace(destination.Warning)) ReportWarning(destination.Warning);
                    return await ReadRemoteScreenAsync("drugs", "İlaçlar", warning);
                }
            }
            if (opensEyeDrugs)
            {
                await EnsureRemoteTableFirstPageAsync("form1:tableExKisiIlacList", cancellationToken);
                await Task.Delay(80, cancellationToken);
                if (!await IsExpectedSubScreenNowAsync("eye-drugs"))
                {
                    var warning = await ReadVisibleMedulaWarningAsync()
                        ?? "Medula gerçek Göz İlaçları sayfasını açmadı.";
                    return await ReadRemoteScreenAsync("drugs", "İlaçlar", warning);
                }
            }
            if (opensNewReport)
                await ExpandNewReportFormIfPresentAsync(cancellationToken);
            var nextKey = opensEyeDrugs
                ? "eye-drugs"
                : opensNewReport
                ? "report-new"
                : screenKey == "reports"
                    && !actionCaption.Equals("Bitmiş raporları da göster", StringComparison.OrdinalIgnoreCase)
                        ? "report-detail"
                        : await InferRemoteScreenKeyAsync(screenKey);
            var nextTitle = nextKey switch
            {
                "eye-drugs" => "Göz İlaçları",
                "report-new" => "Raporlar — Yeni Kayıt",
                "report-detail" => "Rapor Detayı",
                _ => title
            };
            var nextScreen = await ReadRemoteScreenAsync(nextKey, nextTitle, message);
            RememberSubScreenStep(screenKey, actionId, payload, nextScreen);
            return nextScreen;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyRemoteScreen(screenKey, title, $"İşlem tamamlanamadı: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaRemoteScreen> ExecuteTablePageActionAsync(
        string screenKey,
        string title,
        string tableId,
        string actionId,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return EmptyRemoteScreen(screenKey, title, "Medula bağlantısı kurulamadı.");
            var tableIdJson = JsonSerializer.Serialize(tableId);
            var actionIdJson = JsonSerializer.Serialize(actionId);
            var raw = await RequireBrowser().ExecuteScriptAsync($$"""
                (() => {
                    const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                    const table = document.getElementById({{tableIdJson}});
                    const action = document.getElementById({{actionIdJson}});
                    if (!table || !action || action.getClientRects().length === 0)
                        return { caption: '', fingerprint: '' };
                    let scope = action.parentElement;
                    while (scope && !scope.contains(table)) scope = scope.parentElement;
                    if (!scope) return { caption: '', fingerprint: '' };
                    const fingerprint = [...table.querySelectorAll(
                        ':scope > tbody > tr > td, :scope > tr > td')]
                        .map(cell => clean(cell.textContent))
                        .join('\u001f');
                    return {
                        caption: clean(action.value || action.alt || action.title || action.innerText || action.textContent),
                        fingerprint
                    };
                })();
                """);
            var pageProbe = JsonSerializer.Deserialize<TablePageProbe>(raw, JsonOptions)
                ?? new TablePageProbe(string.Empty, string.Empty);
            var caption = pageProbe.Caption;
            if (!MedulaSubScreenSafety.IsSafePageCaption(caption))
                return await ReadRemoteScreenAsync(screenKey, title, "Geçersiz tablo sayfası denetimi yok sayıldı.");

            var click = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const messages = [];
                    const confirmations = [];
                    const originalAlert = window.alert;
                    const originalConfirm = window.confirm;
                    window.alert = message => messages.push(String(message ?? ''));
                    window.confirm = message => {
                        confirmations.push(String(message ?? ''));
                        return false;
                    };
                    const action = document.getElementById({{actionIdJson}});
                    try {
                        if (!action) return { clicked: false, messages, confirmations };
                        action.click();
                        return { clicked: true, messages, confirmations };
                    } finally {
                        window.alert = originalAlert;
                        window.confirm = originalConfirm;
                    }
                })();
                """, cancellationToken);
            var warning = MedulaBrowserWarning.First(click.Messages, click.Confirmations);
            if (warning is not null)
                return await ReadRemoteScreenAsync(screenKey, title, warning);
            if (click.Clicked)
                await WaitForRemoteTableChangeAsync(tableId, pageProbe.Fingerprint, cancellationToken);
            return await ReadRemoteScreenAsync(
                screenKey,
                title,
                click.Clicked ? $"{caption}. sayfa yüklendi." : "Tablo sayfası açılamadı.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyRemoteScreen(screenKey, title, $"Tablo sayfası yüklenemedi: {exception.Message}");
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task<MedulaPrescriptionPageResult> ReturnToPrescriptionAsync(
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken))
                return new MedulaPrescriptionPageResult(false, false, "Medula", LoginUrl,
                    "Medula bağlantısı kurulamadı.", string.Empty);
            await ReturnToPrescriptionCoreAsync(cancellationToken);
            var result = await ReadPrescriptionPageResultAsync(
                await IsPrescriptionEntryPageAsync() ? "Reçete ekranı hazır." : "Reçete ekranına dönülemedi.");
            if (result.IsPrescriptionEntryPage && !systemErrorRecoveryInProgress)
                lastRestorationState = lastRestorationState with { Steps = [] };
            return result;
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    public async Task ClosePrescriptionInlineScreenAsync(
        string screenKey,
        CancellationToken cancellationToken = default)
    {
        await prescriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (!await ConnectWithRetriesAsync(cancellationToken)) return;
            var rootIds = screenKey switch
            {
                "facility-search" => new[] { "f:box11", "f:box14" },
                "branch-select" => new[] { "f:box18" },
                "doctor-branch-select" => new[] { "f:dialog4" },
                "report-selection" => new[] { "__report_selection__" },
                "new-warning-code" => new[] { "__new_warning_code__" },
                "treatment-scheme" => new[] { "__treatment_scheme__" },
                _ => []
            };
            if (rootIds.Length == 0) return;
            var rootIdsJson = JsonSerializer.Serialize(rootIds);
            var knownIdsJson = JsonSerializer.Serialize(new[]
            {
                "f:buttonBransSecKapat", "f:buttonDoktorBransSecKapat",
                "f:buttonReceteTeshisPanelKapat", "f:buttonVazgec",
                "form1:buttonVazgec", "form1:buttonGeriDon", "f:buttonGeriDon"
            });
            var click = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const rootIds = {{rootIdsJson}};
                    const knownIds = {{knownIdsJson}};
                    const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                    const roots = rootIds.includes('__report_selection__')
                        || rootIds.includes('__treatment_scheme__')
                        || rootIds.includes('__new_warning_code__')
                        ? [document]
                        : rootIds.map(id => document.getElementById(id)).filter(Boolean);
                    const candidates = [
                        ...knownIds.map(id => document.getElementById(id)).filter(Boolean),
                        ...roots.flatMap(root => [...root.querySelectorAll(
                            'input[type=submit],input[type=button],button,a')])
                    ].filter(element => element.getClientRects().length > 0);
                    const action = candidates.find(element =>
                        rootIds.includes('__report_selection__')
                            && /^Vazgeç$/i.test(clean(element.value || element.innerText || element.textContent))
                            && element.closest('form')?.querySelector('select'))
                        || candidates.find(element =>
                            rootIds.includes('__treatment_scheme__')
                            && /^Geri D[oö]n$/i.test(clean(element.value || element.innerText || element.textContent)))
                        || candidates.find(element =>
                            rootIds.includes('__new_warning_code__')
                            && /^(?:Kapat|Vazge[cç])$/i.test(clean(
                                element.value || element.innerText || element.textContent))
                            && (() => {
                                let node = element.parentElement;
                                while (node && node !== document.body) {
                                    const content = clean(node.innerText || node.textContent);
                                    const newlyVisible = !element.hasAttribute(
                                        'data-pharmacy-subscreen-before-click');
                                    if (!node.querySelector('#f\\:buttonReceteKaydet')
                                        && (/Uyar[ıi]\s*Kodu/i.test(content)
                                            || newlyVisible && content.length >= 8
                                                && !!node.querySelector('input,select,textarea,button,a')))
                                        return true;
                                    node = node.parentElement;
                                }
                                return false;
                            })())
                        || candidates.find(element =>
                        /^Kapat$/i.test(clean(element.value || element.innerText || element.textContent)))
                        || candidates.find(element =>
                            /^Vazgeç$/i.test(clean(element.value || element.innerText || element.textContent)));
                    if (!action) return { clicked: false, messages: [], confirmations: [] };
                    action.click();
                    return { clicked: true, messages: [], confirmations: [] };
                })();
                """, cancellationToken);
            if (click.Clicked && !systemErrorRecoveryInProgress)
                lastRestorationState = lastRestorationState with { Steps = [] };
        }
        finally
        {
            prescriptionGate.Release();
        }
    }

    private async Task ReturnToPrescriptionCoreAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var onPrescription = await IsPrescriptionEntryPageAsync();
            var ids = onPrescription
                ? new[]
                {
                    "f:buttonReceteTeshisPanelKapat", "f:buttonKonuTamam", "f:buttonVazgec",
                    "form1:buttonVazgec", "f:buttonIptal", "form1:buttonIptal",
                    "f:buttonOzelDurumKapat", "f:buttonIlacEsdegerKapat", "f:buttonEsdegerKapat",
                    "f:buttonIlacAramaKapat", "f:buttonBransSecKapat",
                    "f:buttonDoktorBransSecKapat"
                }
                : new[] { "form1:buttonGeriDon", "f:buttonGeriDon", "form1:buttonVazgec", "f:buttonVazgec" };
            var idsJson = JsonSerializer.Serialize(ids);
            var onPrescriptionJson = onPrescription ? "true" : "false";
            var script = $$"""
                (() => {
                    const ids = {{idsJson}};
                    const visible = element => element && element.getClientRects().length > 0
                        && getComputedStyle(element).visibility !== 'hidden';
                    const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                    const known = ids.map(id => document.getElementById(id)).find(visible);
                    const overlayAction = {{onPrescriptionJson}} ? [...document.querySelectorAll(
                        'input[type=submit],input[type=button],button,a')]
                        .filter(visible)
                        .find(element => {
                            if (!/^(?:Vazge[cç]|Kapat|[İI]ptal)$/i.test(
                                    clean(element.value || element.innerText || element.textContent))) return false;
                            const overlay = element.closest(
                                '[role="dialog"],[aria-modal="true"],[id*="dialog" i],[id*="panel" i],[id*="box" i]');
                            return !!overlay && !overlay.querySelector('#f\\:buttonReceteKaydet');
                        }) : null;
                    const action = known || overlayAction;
                    if (!action) return { clicked: false, messages: [], confirmations: [] };
                    window.alert = () => {};
                    window.confirm = () => false;
                    action.click();
                    return { clicked: true, messages: [], confirmations: [] };
                })();
                """;
            var result = await ExecuteActionAndWaitAsync(script, cancellationToken);
            if (!result.Clicked) return;
        }
    }

    private async Task<bool> IsAllowedSubScreenActionAsync(
        string screenKey,
        string actionId,
        string? knownCaption = null)
    {
        if (MedulaSubScreenSafety.IsAllowedActionId(screenKey, actionId)) return true;
        var caption = knownCaption ?? await ReadActionCaptionAsync(actionId);
        if (MedulaSubScreenSafety.IsAllowed(screenKey, caption)) return true;
        if (screenKey is not ("reports" or "report-detail")) return false;

        var actionIdJson = JsonSerializer.Serialize(actionId);
        var raw = await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const element = document.getElementById({{actionIdJson}});
                const table = element?.closest('table');
                const isReportPage = [...document.querySelectorAll('input,button,a')].some(action => {
                    const caption = clean(action.value || action.alt || action.title || action.innerText || action.textContent);
                    return action.getClientRects().length > 0
                        && /^(?:Yeni Kayıt|Bitmiş raporları da göster)$/i.test(caption);
                });
                return !!element && element.getClientRects().length > 0
                    && !!element.closest('tr')?.querySelector('td')
                    && (isReportPage || /rapor/i.test(table?.id ?? ''));
            })();
            """);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ReadActionCaptionAsync(string actionId)
    {
        var actionIdJson = JsonSerializer.Serialize(actionId);
        var raw = await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const element = document.getElementById({{actionIdJson}});
                if (!element || element.getClientRects().length === 0) return '';
                return clean(element.value || element.alt || element.title || element.innerText || element.textContent);
            })();
            """);
        return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
    }

    private async Task<(bool Success, string? Message, string? Warning)> ObserveItsSaleNotificationOutcomeAsync(
        IReadOnlyList<string> immediateMessages,
        string? warningBeforeClick,
        long warningRevision,
        CancellationToken cancellationToken,
        bool cancellation = false)
    {
        foreach (var message in immediateMessages.Where(message => !string.IsNullOrWhiteSpace(message)))
        {
            if (IsItsSaleSuccessMessage(message, cancellation)) return (true, message.Trim(), null);
            if (IsItsSaleFailureMessage(message)) return (false, null, message.Trim());
        }

        const string outcomeScript = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const lines = (document.body?.innerText ?? '').split(/\r?\n/).map(clean);
                return lines.find(line => line.length <= 700
                    && /(?:[İI]TS\s*)?sat[ıi][sş]\s+(?:bildirim|iptal)/i.test(line)
                    && /(?:ba[sş]ar|tamamlan|yap[ıi]l|bildiril|hata|ba[sş]ar[ıi]s[ıi]z|yap[ıi]lam)/i.test(line)) || '';
            })();
            """;
        for (var attempt = 0; attempt < MedulaSubScreenSafety.DestinationPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsSystemErrorPageAsync())
            {
                var systemError = await ReadSystemErrorMessageAsync();
                return (false, null, systemError);
            }
            if (await IsLoginPageAsync())
                return (false, null, "Medula oturumu ITS satış bildirimi sırasında sona erdi.");

            var rawOutcome = await RequireBrowser().ExecuteScriptAsync(outcomeScript);
            var outcome = JsonSerializer.Deserialize<string>(rawOutcome) ?? string.Empty;
            if (IsItsSaleSuccessMessage(outcome, cancellation)) return (true, outcome.Trim(), null);
            if (IsItsSaleFailureMessage(outcome)) return (false, null, outcome.Trim());

            var warning = GetWarningAfter(warningRevision);
            if (string.IsNullOrWhiteSpace(warning))
                warning = await ReadVisibleMedulaWarningAsync();
            if (!string.IsNullOrWhiteSpace(warning)
                && (!string.Equals(warning, warningBeforeClick, StringComparison.Ordinal)
                    || GetWarningRevision() > warningRevision))
                return (false, null, warning);

            if (attempt + 1 < MedulaSubScreenSafety.DestinationPollAttempts)
                await Task.Delay(MedulaSubScreenSafety.DestinationPollIntervalMilliseconds, cancellationToken);
        }
        if (cancellation && await IsItsSaleCancellationClearedAsync(cancellationToken))
            return (true, "ITS Satış İptali tamamlandı.", null);
        return (false, null, null);
    }

    private async Task<bool> IsItsSaleCancellationClearedAsync(CancellationToken cancellationToken)
    {
        var listResult = await ExecuteActionAndWaitAsync("""
            (() => {
                const action = document.getElementById('form1:buttonListele');
                if (!action) return { clicked: false, messages: [], confirmations: [] };
                action.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!listResult.Clicked) return false;

        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim()
                    .toLocaleLowerCase('tr-TR');
                return [...document.querySelectorAll(
                    'button,input[type=button],input[type=submit],input[type=image],a')]
                    .some(element => element.getClientRects().length > 0
                        && clean(element.value || element.alt || element.title
                            || element.innerText || element.textContent) === 'its satış bildirimi');
            })();
            """);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsItsSaleSuccessMessage(string? message, bool cancellation = false) =>
        !string.IsNullOrWhiteSpace(message)
        && message.Contains("Satış", StringComparison.CurrentCultureIgnoreCase)
        && (cancellation
            ? message.Contains("İptal", StringComparison.CurrentCultureIgnoreCase)
            : message.Contains("Bildirim", StringComparison.CurrentCultureIgnoreCase))
        && (message.Contains("başarı", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("tamamlan", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("yapıl", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("bildiril", StringComparison.CurrentCultureIgnoreCase))
        && !IsItsSaleFailureMessage(message);

    private static bool IsItsSaleFailureMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("başarısız", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("yapılam", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("hata", StringComparison.CurrentCultureIgnoreCase));

    private async Task ExpandNewReportFormIfPresentAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await RequireBrowser().ExecuteScriptAsync("""
                (() => {
                    const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                    const action = [...document.querySelectorAll('input,button,a')].find(element =>
                        element.getClientRects().length > 0
                        && clean(element.value || element.alt || element.title || element.innerText || element.textContent)
                            .toLocaleLowerCase('tr-TR') === 'yeni rapor');
                    if (!action) return false;
                    action.click();
                    return true;
                })();
                """);
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(250, cancellationToken);
                return;
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task<SubScreenWaitResult> WaitForExpectedSubScreenAsync(
        string screenKey,
        string? warningBeforeClick,
        long warningRevision,
        CancellationToken cancellationToken)
    {
        var repeatedWarningPolls = 0;
        for (var attempt = 0; attempt < MedulaSubScreenSafety.DestinationPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await IsSystemErrorPageAsync())
                    return new SubScreenWaitResult(
                        false,
                        await ReadSystemErrorMessageAsync(),
                        true,
                        false);
                if (await IsLoginPageAsync())
                    return new SubScreenWaitResult(
                        false,
                        "Medula oturumu sona erdi.",
                        false,
                        true);

                const string script = """
                    (() => {
                        const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                        const visible = element => element.getClientRects().length > 0
                            && getComputedStyle(element).visibility !== 'hidden';
                        const actionCaption = element => clean(element.value || element.alt
                            || element.title || element.innerText || element.textContent);
                        const visibleActions = [...document.querySelectorAll(
                            'input[type=submit],input[type=button],input[type=image],button,a')]
                            .filter(visible);
                        const newWarningCloses = visibleActions
                            .filter(element => /^(?:Vazge[cç]|Kapat|Tamam)$/i.test(actionCaption(element)));
                        const newWarningDialog = newWarningCloses
                            .sort((left, right) => Number(left.hasAttribute('data-pharmacy-subscreen-before-click'))
                                - Number(right.hasAttribute('data-pharmacy-subscreen-before-click')))
                            .map(element => {
                                let node = element.parentElement;
                                while (node && node !== document.body) {
                                    const content = clean(node.innerText || node.textContent);
                                    const newlyVisible = !element.hasAttribute(
                                        'data-pharmacy-subscreen-before-click');
                                    if (!node.querySelector('#f\\:buttonReceteKaydet')
                                        && (/Uyar[ıi]\s*Kodu/i.test(content)
                                            || newlyVisible && content.length >= 8
                                                && !!node.querySelector('input,select,textarea,button,a')))
                                        return node;
                                    node = node.parentElement;
                                }
                                return null;
                            }).find(Boolean);
                        const warningSelectors = [
                            '[role="alert"]', '.error', '.errors', '.errorMessage', '.message-error',
                            '[class*="warning"]', '[class*="uyari"]', '[class*="hata"]',
                            '[id*="warning"]', '[id*="uyari"]', '[id*="hata"]'
                        ];
                        const warning = warningSelectors
                            .flatMap(selector => [...document.querySelectorAll(selector)])
                            .filter(visible)
                            .filter(element => {
                                const dataTable = element.closest(
                                    'table[id*="tableEx"],table[id*="TableEx"],table[id*="tbl"]');
                                return !dataTable || element.getAttribute('role') === 'alert';
                            })
                            .map(element => String(element.innerText || element.textContent || '').trim())
                            .find(text => text && text.length <= 1000) || '';
                        return {
                            url: location.href,
                            visibleElementIds: [...document.querySelectorAll('[id]')]
                                .filter(visible)
                                .map(element => element.id)
                                .concat(newWarningDialog ? ['__new_warning_code_dialog__'] : []),
                            visibleActionCaptions: visibleActions
                                .map(actionCaption)
                                .filter(Boolean),
                            warning
                        };
                    })();
                    """;
                var raw = await RequireBrowser().ExecuteScriptAsync(script);
                var probe = JsonSerializer.Deserialize<SubScreenDestinationProbe>(raw, JsonOptions)
                    ?? new SubScreenDestinationProbe(string.Empty, [], [], string.Empty);
                if (MedulaSubScreenSafety.IsExpectedDestination(
                        screenKey,
                        probe.Url,
                        probe.VisibleElementIds,
                        probe.VisibleActionCaptions))
                    return new SubScreenWaitResult(true, null, false, false);

                var emittedWarning = GetWarningAfter(warningRevision);
                if (emittedWarning is not null)
                    return new SubScreenWaitResult(false, emittedWarning, false, false);

                if (!string.IsNullOrWhiteSpace(probe.Warning))
                {
                    if (!string.Equals(probe.Warning, warningBeforeClick, StringComparison.Ordinal))
                    {
                        ReportWarning(probe.Warning);
                        return new SubScreenWaitResult(false, probe.Warning, false, false);
                    }

                    repeatedWarningPolls++;
                    if (repeatedWarningPolls >= 3)
                    {
                        ReportWarning(probe.Warning);
                        return new SubScreenWaitResult(false, probe.Warning, false, false);
                    }
                }
                else
                {
                    repeatedWarningPolls = 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The document can be temporarily unavailable while WebView2 swaps
                // the old Medula page for the requested destination. Retry briefly.
            }
            if (attempt + 1 < MedulaSubScreenSafety.DestinationPollAttempts)
                await Task.Delay(MedulaSubScreenSafety.DestinationPollIntervalMilliseconds, cancellationToken);
        }
        return new SubScreenWaitResult(false, GetWarningAfter(warningRevision), false, false);
    }

    private async Task<bool> IsExpectedSubScreenNowAsync(string screenKey)
    {
        const string script = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const visible = element => element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden'
                    && getComputedStyle(element).display !== 'none';
                const actionCaption = element => clean(element.value || element.alt
                    || element.title || element.innerText || element.textContent);
                const visibleActions = [...document.querySelectorAll(
                    'input[type=submit],input[type=button],input[type=image],button,a')]
                    .filter(visible);
                const newWarningCloses = visibleActions
                    .filter(element => /^(?:Vazge[cç]|Kapat|Tamam)$/i.test(actionCaption(element)));
                const newWarningDialog = newWarningCloses
                    .sort((left, right) => Number(left.hasAttribute('data-pharmacy-subscreen-before-click'))
                        - Number(right.hasAttribute('data-pharmacy-subscreen-before-click')))
                    .map(element => {
                        let node = element.parentElement;
                        while (node && node !== document.body) {
                            const content = clean(node.innerText || node.textContent);
                            const newlyVisible = !element.hasAttribute(
                                'data-pharmacy-subscreen-before-click');
                            if (!node.querySelector('#f\\:buttonReceteKaydet')
                                && (/Uyar[ıi]\s*Kodu/i.test(content)
                                    || newlyVisible && content.length >= 8
                                        && !!node.querySelector('input,select,textarea,button,a')))
                                return node;
                            node = node.parentElement;
                        }
                        return null;
                    }).find(Boolean);
                return {
                    url: location.href,
                    visibleElementIds: [...document.querySelectorAll('[id]')]
                        .filter(visible).map(element => element.id)
                        .concat(newWarningDialog ? ['__new_warning_code_dialog__'] : []),
                    visibleActionCaptions: visibleActions
                        .map(actionCaption)
                        .filter(Boolean),
                    warning: ''
                };
            })();
            """;
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        var probe = JsonSerializer.Deserialize<SubScreenDestinationProbe>(raw, JsonOptions)
            ?? new SubScreenDestinationProbe(string.Empty, [], [], string.Empty);
        return MedulaSubScreenSafety.IsExpectedDestination(
            screenKey, probe.Url, probe.VisibleElementIds, probe.VisibleActionCaptions);
    }

    private async Task<string> MarkVisibleMedulaWarningsBeforeActionAsync(WebView2 sourceBrowser)
    {
        var marker = Guid.NewGuid().ToString("N");
        var markerJson = JsonSerializer.Serialize(marker);
        await sourceBrowser.ExecuteScriptAsync($$"""
            (() => {
                const marker = {{markerJson}};
                const visible = element => element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden'
                    && getComputedStyle(element).display !== 'none';
                const selectors = [
                    '[role="alert"]', '.error', '.errors', '.errorMessage', '.message-error',
                    '.message', '.messages', '[class*="message"]', '[id*="message"]',
                    '[class*="warning"]', '[class*="uyari"]', '[class*="hata"]',
                    '[id*="warning"]', '[id*="uyari"]', '[id*="hata"]',
                    'font[color="red"]', '[style*="color: red"]', '[style*="color:red"]'
                ];
                selectors.flatMap(selector => [...document.querySelectorAll(selector)])
                    .filter(visible)
                    .forEach(element => element.setAttribute(
                        'data-pharmacy-warning-before-action', marker));
            })();
            """);
        return marker;
    }

    private async Task<string?> ReadVisibleMedulaWarningAsync(
        WebView2? sourceBrowser = null,
        string? staleActionMarker = null)
    {
        var markerJson = JsonSerializer.Serialize(staleActionMarker);
        var script = $$"""
            (() => {
                const staleActionMarker = {{markerJson}};
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const visible = element => element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden'
                    && getComputedStyle(element).display !== 'none';
                const warningPattern = /(?:(?:^|[^a-zçğıöşü])hata(?:s[ıi])?(?:$|[^a-zçğıöşü])|uyar[ıi]|zorunlu|olmal[ıi]d[ıi]r|girilmelidir|giriniz|se[cç]iniz|bulunamad[ıi]|ge[cç]ersiz|ba[sş]ar[ıi]s[ıi]z|uygun de[gğ]ildir|izin verilenler aras[ıi]nda yer almamaktad[ıi]r|[cç]al[ıi][sş](?:mamaktad[ıi]r|m[ıi]yor|maz)|e[sş]le[sş]m|mevcut de[gğ]ildir|yoktur|daha [oö]nce|kar[sş][ıi]lan(?:m[ıi][sş]|m[ıi][sş]t[ıi]r|mam[ıi][sş])|ekran[ıi]na yeniden giri[sş] yap[ıi]n[ıi]z)/i;
                const informationalPattern = /(?:hatal[ıi]\s+bilgileri\s+d[uü]zelttirmek\s+i[cç]in\s+sgk\s+sosyal\s+g[uü]venlik\s+merkezlerine\s+ba[sş]vurunuz|bilgi\s+g[uü]venli[gğ]i\s+nedeniyle|kay[ıi]t\s+alt[ıi]na\s+al[ıi]nmaktad[ıi]r)/i;
                const selectors = [
                    '[role="alert"]', '.error', '.errors', '.errorMessage', '.message-error',
                    '.message', '.messages', '[class*="message"]', '[id*="message"]',
                    '[class*="warning"]', '[class*="uyari"]', '[class*="hata"]',
                    '[id*="warning"]', '[id*="uyari"]', '[id*="hata"]',
                    'font[color="red"]', '[style*="color: red"]', '[style*="color:red"]'
                ];
                return selectors.flatMap(selector => [...document.querySelectorAll(selector)])
                    .filter(visible)
                    .filter(element => !staleActionMarker
                        || element.getAttribute('data-pharmacy-warning-before-action') !== staleActionMarker)
                    .filter(element => {
                        const dataTable = element.closest(
                            'table[id*="tableEx"],table[id*="TableEx"],table[id*="tbl"]');
                        return !dataTable || element.getAttribute('role') === 'alert';
                    })
                    .map(element => String(element.innerText || element.textContent || '').trim())
                    .filter(text => !/^silinmiş\s+karekod$/i.test(text))
                    .filter(text => !informationalPattern.test(text))
                    .filter(text => text && text.length <= 1000 && warningPattern.test(text))
                    .sort((left, right) => left.length - right.length)[0] || '';
            })();
            """;
        var raw = await (sourceBrowser ?? RequireBrowser()).ExecuteScriptAsync(script);
        var warning = JsonSerializer.Deserialize<string>(raw);
        return MedulaBrowserWarning.IsPrescriptionDataStatus(warning) ? null : warning;
    }

    private async Task<string?> ReadFreshWarningAfterActionAsync(
        long warningRevision,
        string staleActionMarker,
        WebView2 sourceBrowser)
    {
        if (GetWarningAfter(warningRevision) is { } bridgedWarning) return bridgedWarning;
        try
        {
            return await ReadVisibleMedulaWarningAsync(sourceBrowser, staleActionMarker);
        }
        catch
        {
            // A postback can replace the document while it is being inspected.
            return GetWarningAfter(warningRevision);
        }
    }

    private async Task<(bool Opened, string? Warning)> WaitForElectronicPrescriptionDetailOutcomeAsync(
        long warningRevision,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MedulaSubScreenSafety.DestinationPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await IsElectronicPrescriptionDetailOpenAsync()) return (true, null);

                var warning = GetWarningAfter(warningRevision)
                    ?? await ReadVisibleMedulaWarningAsync();
                if (!string.IsNullOrWhiteSpace(warning)) return (false, warning);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Medula can briefly replace the document during the detail postback.
            }
            if (attempt + 1 < MedulaSubScreenSafety.DestinationPollAttempts)
                await Task.Delay(MedulaSubScreenSafety.DestinationPollIntervalMilliseconds, cancellationToken);
        }
        return (false, GetWarningAfter(warningRevision));
    }

    private async Task<bool> IsElectronicPrescriptionDetailOpenAsync()
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("""
            (() => {
                const clean = value => String(value ?? '').replace(/\s+/g, ' ').trim();
                const visible = element => element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden'
                    && getComputedStyle(element).display !== 'none';
                const main = [...document.querySelectorAll('table.borderTable')]
                    .filter(visible)
                    .find(table => {
                        const text = clean(table.innerText || table.textContent);
                        return text.includes('E-Reçete No') && text.includes('Doktor Dip.Tesc.No');
                    });
                if (!main) return false;
                const actionTexts = [...document.querySelectorAll('input,button,a')]
                    .filter(visible)
                    .map(element => clean(element.value || element.textContent).toLocaleLowerCase('tr-TR'));
                return actionTexts.some(text => text.includes('reçete kaydına devam')
                    || text.includes('geri dön'))
                    || /e-reçete\s+no/i.test(clean(main.innerText || main.textContent));
            })();
            """);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WaitForRemoteTableChangeAsync(
        string tableId,
        string previousFingerprint,
        CancellationToken cancellationToken)
    {
        var tableIdJson = JsonSerializer.Serialize(tableId);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var raw = await RequireBrowser().ExecuteScriptAsync($$"""
                    (() => {
                        const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                        const table = document.getElementById({{tableIdJson}});
                        if (!table) return '';
                        return [...table.querySelectorAll(
                            ':scope > tbody > tr > td, :scope > tr > td')]
                            .map(cell => clean(cell.textContent))
                            .join('\u001f');
                    })();
                    """);
                var fingerprint = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
                if (fingerprint.Length > 0
                    && !string.Equals(fingerprint, previousFingerprint, StringComparison.Ordinal))
                    return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Medula can briefly replace the document while changing a table page.
            }
            if (attempt < 29) await Task.Delay(100, cancellationToken);
        }
    }

    private async Task EnsureRemoteTableFirstPageAsync(
        string tableId,
        CancellationToken cancellationToken)
    {
        var tableIdJson = JsonSerializer.Serialize(tableId);
        var raw = await RequireBrowser().ExecuteScriptAsync($$"""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const table = document.getElementById({{tableIdJson}});
                if (!table) return { actionId: '', fingerprint: '' };
                let scope = table.parentElement;
                let action = null;
                while (scope && scope !== document.body && !action) {
                    action = [...scope.querySelectorAll(
                        'a[id],button[id],input[type=submit][id],input[type=button][id]')]
                        .find(element => element.getClientRects().length > 0
                            && clean(element.value || element.alt || element.title
                                || element.innerText || element.textContent) === '1');
                    scope = scope.parentElement;
                }
                const fingerprint = [...table.querySelectorAll(
                    ':scope > tbody > tr > td, :scope > tr > td')]
                    .map(cell => clean(cell.textContent))
                    .join('\u001f');
                return { actionId: action?.id ?? '', fingerprint };
            })();
            """);
        var probe = JsonSerializer.Deserialize<FirstPageProbe>(raw, JsonOptions)
            ?? new FirstPageProbe(string.Empty, string.Empty);
        if (probe.ActionId.Length == 0) return;
        var actionIdJson = JsonSerializer.Serialize(probe.ActionId);
        var click = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const action = document.getElementById({{actionIdJson}});
                if (!action) return { clicked: false, messages: [], confirmations: [] };
                window.alert = () => {};
                window.confirm = () => false;
                action.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (click.Clicked)
            await WaitForRemoteTableChangeAsync(tableId, probe.Fingerprint, cancellationToken);
    }

    private async Task<string> InferRemoteScreenKeyAsync(string fallback)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync("location.pathname + '|' + Boolean(document.getElementById('form1:buttonYeniRapor'))");
        var marker = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        if (marker.Contains("RaporListe.jsp|true", StringComparison.OrdinalIgnoreCase)) return "reports";
        if (fallback is "reports" or "report-new") return "report-new";
        return fallback;
    }

    private async Task<MedulaRemoteScreen> ReadRemoteScreenAsync(
        string screenKey,
        string title,
        string message,
        WebView2? sourceBrowser = null)
    {
        var rootIdJson = JsonSerializer.Serialize(MedulaSubScreenSafety.InlineRootId(screenKey));
        var screenKeyJson = JsonSerializer.Serialize(screenKey);
        var script = $$"""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const visible = element => element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden';
                const rootId = {{rootIdJson}};
                const screenKey = {{screenKeyJson}};
                const rootIds = screenKey === 'facility-search' ? ['f:box11', 'f:box14'] : [rootId];
                const inScope = element => ['facility-search', 'doctor-branch-select'].includes(screenKey)
                    || visible(element);
                const actionCaption = element => clean(
                    element?.value || element?.alt || element?.title || element?.innerText || element?.textContent);
                const reportSelectionRoot = (() => {
                    if (screenKey !== 'report-selection') return null;
                    const actions = [...document.querySelectorAll(
                        'input[type=submit],input[type=button],button,a')].filter(visible);
                    const accept = actions.find(element => /^Tamam$/i.test(actionCaption(element))
                        && element.closest('form')?.querySelector('select'));
                    if (!accept) return null;
                    const cancel = actions.find(element => /^Vazge[cç]$/i.test(actionCaption(element)));
                    let node = accept.parentElement;
                    while (node && node !== document.body) {
                        const text = clean(node.innerText || node.textContent);
                        const ownsPrescriptionSave = !!node.querySelector('#f\\:buttonReceteKaydet');
                        if (/Rapor\s+Se[cç]/i.test(text)
                            && node.querySelector('select')
                            && (!cancel || node.contains(cancel))
                            && !ownsPrescriptionSave)
                            return node;
                        node = node.parentElement;
                    }
                    return accept.closest('table');
                })();
                const newWarningRoot = (() => {
                    if (screenKey !== 'new-warning-code') return null;
                    const actions = [...document.querySelectorAll(
                        'input[type=submit],input[type=button],button,a')].filter(visible);
                    const closes = actions.filter(element =>
                        /^(?:Vazge[cç]|Kapat|Tamam)$/i.test(actionCaption(element)));
                    closes.sort((left, right) =>
                        Number(left.hasAttribute('data-pharmacy-subscreen-before-click'))
                        - Number(right.hasAttribute('data-pharmacy-subscreen-before-click')));
                    for (const close of closes) {
                        let node = close.parentElement;
                        while (node && node !== document.body) {
                            const content = clean(node.innerText || node.textContent);
                            const newlyVisible = !close.hasAttribute(
                                'data-pharmacy-subscreen-before-click');
                            if (!node.querySelector('#f\\:buttonReceteKaydet')
                                && (/Uyar[ıi]\s*Kodu/i.test(content)
                                    || newlyVisible && content.length >= 8
                                        && !!node.querySelector('input,select,textarea,button,a')))
                                return node;
                            node = node.parentElement;
                        }
                    }
                    return closes
                        .map(element => element.closest('table'))
                        .find(table => table && /Uyar[ıi]/i.test(clean(table.innerText || table.textContent)))
                        || null;
                })();
                const roots = screenKey === 'report-selection'
                    ? (reportSelectionRoot ? [reportSelectionRoot] : [])
                    : screenKey === 'new-warning-code'
                    ? (newWarningRoot ? [newWarningRoot] : [])
                    : rootId === null
                    ? [document]
                    : rootIds.map(id => document.getElementById(id)).filter(element => element && inScope(element));
                if (roots.length === 0)
                    return { isOpen: false, url: location.href, fields: [], actions: [], tables: [] };
                const all = selector => [...new Set(roots.flatMap(root => [
                    ...(root.matches?.(selector) ? [root] : []),
                    ...root.querySelectorAll(selector)
                ]))];
                const labelFor = element => {
                    const usableLabel = value => {
                        const text = clean(value);
                        return text && text.length <= 160 && !/^[:：\-–—|/\\.\s]+$/.test(text);
                    };
                    const explicit = element.id
                        ? document.querySelector(`label[for="${CSS.escape(element.id)}"]`)
                        : null;
                    if (explicit && usableLabel(explicit.innerText || explicit.textContent))
                        return clean(explicit.innerText || explicit.textContent).slice(0, 160);
                    let sibling = element.previousElementSibling;
                    while (sibling) {
                        const siblingText = clean(sibling.innerText || sibling.textContent);
                        if (usableLabel(siblingText)) return siblingText.slice(0, 160);
                        sibling = sibling.previousElementSibling;
                    }
                    const cell = element.closest('td');
                    if (cell?.parentElement) {
                        const cells = [...cell.parentElement.children];
                        const index = cells.indexOf(cell);
                        for (let offset = index - 1; offset >= 0; offset--) {
                            const candidate = cells[offset];
                            if (candidate.querySelector('input,select,button')) continue;
                            const text = clean(candidate.innerText || candidate.textContent);
                            if (usableLabel(text)) return text.slice(0, 160);
                        }
                    }
                    if (cell) {
                        const clone = cell.cloneNode(true);
                        clone.querySelectorAll('input,select,textarea,button,a,img').forEach(item => item.remove());
                        const ownText = clean(clone.innerText || clone.textContent).replace(/[:：]+$/, '').trim();
                        if (ownText) return ownText.slice(0, 160);
                    }
                    return element.name || element.id || 'Alan';
                };
                const fields = all('input,select,textarea')
                    .filter(element => element.id && inScope(element)
                        && !['hidden','submit','button','image'].includes((element.type || '').toLowerCase()))
                    .map(element => ({
                        id: element.id,
                        label: screenKey === 'report-selection' && element.tagName === 'SELECT'
                            ? clean(element.closest('tr')?.querySelector('td')?.innerText
                                || element.closest('tr')?.querySelector('td')?.textContent)
                                || labelFor(element)
                            : labelFor(element),
                        value: ['checkbox','radio'].includes((element.type || '').toLowerCase())
                            ? String(!!element.checked)
                            : element.value ?? '',
                        kind: (element.type || '').toLowerCase() === 'file'
                            ? 'file'
                            : element.tagName === 'TEXTAREA'
                            ? 'multiline'
                            : element.tagName === 'SELECT'
                            ? 'select'
                            : ['checkbox','radio'].includes((element.type || '').toLowerCase()) ? 'check' : 'text',
                        isReadOnly: !!(element.readOnly || element.disabled),
                        options: element.tagName === 'SELECT'
                            ? [...element.options].map(option => ({ value: option.value ?? '', text: clean(option.text) }))
                            : []
                    }));
                const treatmentRows = [];
                if (['treatment-scheme', 'new-warning-code'].includes(screenKey)) {
                    const editable = all('input,select,textarea')
                        .filter(element => element.id && visible(element)
                            && !element.disabled && !element.readOnly
                            && !['hidden','submit','button','image'].includes(
                                (element.type || '').toLowerCase()));
                    const sourceRows = [...new Set(editable
                        .map(element => element.closest('tr'))
                        .filter(Boolean))];
                    const ownText = element => {
                        if (!element) return '';
                        const clone = element.cloneNode(true);
                        clone.querySelectorAll('input,select,textarea,button,a,img,script,style')
                            .forEach(item => item.remove());
                        return clean(clone.innerText || clone.textContent);
                    };
                    const structuredText = element => {
                        if (!element) return '';
                        const clone = element.cloneNode(true);
                        clone.querySelectorAll('input,select,textarea,button,a,img,script,style')
                            .forEach(item => item.remove());
                        return clean(clone.innerText || clone.textContent);
                    };
                    const normalizedCaption = value => clean(value)
                        .replace(/[:：]+$/g, '').trim();
                    const isEditorCaption = value => /^(?:Periyot|Doz|x|Adet|Miktar|Kullanım|Süre|Tedavi Şeması|Uyarı Kodu)$/i
                        .test(normalizedCaption(value));
                    const columnLabel = (control, row) => {
                        const cell = control.closest('td,th');
                        const table = row.closest('table');
                        if (cell && table) {
                            const cells = [...row.children]
                                .filter(item => /^(TD|TH)$/.test(item.tagName));
                            const column = cells.indexOf(cell);
                            const tableRows = [...table.querySelectorAll('tr')];
                            const precedingRows = tableRows
                                .slice(0, Math.max(0, tableRows.indexOf(row)))
                                .reverse();
                            for (const headerRow of precedingRows) {
                                const headers = [...headerRow.children]
                                    .filter(item => /^(TD|TH)$/.test(item.tagName));
                                if (column >= 0 && column < headers.length) {
                                    const candidate = ownText(headers[column])
                                        .replace(/[:：]+$/, '').trim();
                                    if (candidate && candidate.length <= 80) return candidate;
                                }
                            }
                        }
                        return labelFor(control).replace(/[:：]+$/, '').trim();
                    };
                    sourceRows.forEach((row, rowIndex) => {
                        const controls = [...row.querySelectorAll('input,select,textarea')]
                            .filter(element => editable.includes(element));
                        if (!controls.length) return;
                        const cells = controls.map(control => ({
                            fieldId: control.id,
                            label: columnLabel(control, row),
                            value: ['checkbox','radio'].includes((control.type || '').toLowerCase())
                                ? String(!!control.checked)
                                : control.value ?? '',
                            kind: (control.type || '').toLowerCase() === 'file'
                                ? 'file'
                                : control.tagName === 'TEXTAREA'
                                ? 'multiline'
                                : control.tagName === 'SELECT'
                                ? 'select'
                                : ['checkbox','radio'].includes((control.type || '').toLowerCase())
                                ? 'check'
                                : 'text',
                            isReadOnly: false,
                            options: control.tagName === 'SELECT'
                                ? [...control.options].map(option => ({
                                    value: option.value ?? '', text: clean(option.text)
                                }))
                                : []
                        }));
                        const labels = new Set(cells.map(cell =>
                            clean(cell.label).toLocaleLowerCase('tr-TR')));
                        let medicationCandidates = [...row.children]
                            .filter(element => /^(TD|TH)$/.test(element.tagName))
                            .filter(element => !element.querySelector('input,select,textarea'))
                            .map(structuredText)
                            .filter(value => value && value.length <= 240)
                            .filter(value => !labels.has(value.toLocaleLowerCase('tr-TR')))
                            .filter(value => !/^(?:\d+(?:[.,]\d+)?|[-/:._\s]+)$/.test(value))
                            .filter(value => !isEditorCaption(value));
                        if (!medicationCandidates.length) {
                            const table = row.closest('table');
                            const tableRows = [...(table?.querySelectorAll('tr') || [])];
                            const rowPosition = tableRows.indexOf(row);
                            medicationCandidates = tableRows
                                .slice(0, Math.max(0, rowPosition))
                                .reverse()
                                .flatMap(candidateRow => [...candidateRow.children]
                                    .filter(element => /^(TD|TH)$/.test(element.tagName))
                                    .filter(element => !element.querySelector('input,select,textarea'))
                                    .map(structuredText))
                                .filter(value => value && value.length <= 240)
                                .filter(value => !isEditorCaption(value))
                                .filter(value => !/^(?:\d+(?:[.,]\d+)?|[-/:._\s]+)$/.test(value));
                        }
                        const medicationName = medicationCandidates
                            .sort((left, right) => right.length - left.length)[0]
                            || `İlaç ${rowIndex + 1}`;
                        treatmentRows.push({ medicationName, cells });
                    });
                }
                const text = id => clean(document.getElementById(id)?.innerText
                    || document.getElementById(id)?.textContent);
                const readOnlyField = (id, labelId, valueIds) => ({
                    id,
                    label: text(labelId),
                    value: valueIds.map(text).filter(Boolean).join(' '),
                    kind: 'text',
                    isReadOnly: true,
                    options: []
                });
                if (screenKey === 'off-label-old') fields.push(
                    readOnlyField('display:identity', 'form1:text9', ['form1:text18']),
                    readOnlyField('display:name', 'form1:text4', ['form1:text7', 'form1:text6']),
                    readOnlyField('display:first-name', 'form1:text4', ['form1:text7']),
                    readOnlyField('display:last-name', 'form1:text4', ['form1:text6']),
                    readOnlyField('display:gender', 'form1:text19', ['form1:text21']),
                    readOnlyField('display:birth-date', 'form1:text22', ['form1:text23'])
                );
                if (screenKey === 'off-label-new') fields.push(
                    readOnlyField('display:identity', 'form1:text1', ['form1:text2']),
                    readOnlyField('display:name', 'form1:text3', ['form1:text4', 'form1:text5']),
                    readOnlyField('display:first-name', 'form1:text3', ['form1:text4']),
                    readOnlyField('display:last-name', 'form1:text3', ['form1:text5']),
                    readOnlyField('display:gender', 'form1:text6', ['form1:text7']),
                    readOnlyField('display:birth-date', 'form1:text8', ['form1:text9'])
                );
                if (['report-detail', 'drug-report-detail'].includes(screenKey)) {
                    const wantedLabels = new Set([
                        'T.C. Kimlik No', 'Adı / Soyadı', 'Cinsiyeti', 'Doğum Tarihi',
                        'Rapor Numarası', 'Rapor Tarihi', 'Protokol No', 'Düzenleme Türü',
                        'Açıklama', 'Kayıt Şekli', 'Tesis Kodu', 'Rapor Takip No',
                        'Tesis Ünvanı', 'Kullanıcı Adı'
                    ]);
                    const normalizedLabel = value => clean(value)
                        .replace(/[(*):：]+/g, ' ')
                        .replace(/\s+/g, ' ')
                        .trim();
                    const seenPairs = new Set();
                    all('td,th').forEach(cell => {
                        const clone = cell.cloneNode(true);
                        clone.querySelectorAll('input,select,textarea,button,a,img,table').forEach(item => item.remove());
                        const label = normalizedLabel(clone.innerText || clone.textContent);
                        if (!wantedLabels.has(label) || seenPairs.has(label)) return;
                        let valueCell = cell.nextElementSibling;
                        while (valueCell && /^[:：\s]*$/.test(clean(valueCell.innerText || valueCell.textContent)))
                            valueCell = valueCell.nextElementSibling;
                        let value = clean(valueCell?.innerText || valueCell?.textContent);
                        if (wantedLabels.has(normalizedLabel(value))) value = '—';
                        if (!value || value.length > 1200) return;
                        seenPairs.add(label);
                        fields.push({
                            id: `display:report:${seenPairs.size}`,
                            label,
                            value,
                            kind: 'text',
                            isReadOnly: true,
                            options: []
                        });
                    });
                }
                if (screenKey === 'saved-prescription-info') {
                    const seenInfo = new Set();
                    const isFooterText = value => /sosyal\s+g[uü]venlik\s+kurumu\s*©?\s*20\d{2}\s*[-–]\s*20\d{2}/i.test(clean(value));
                    const ownText = cell => {
                        if (!cell) return '';
                        const clone = cell.cloneNode(true);
                        clone.querySelectorAll('input,select,textarea,button,a,img,table').forEach(item => item.remove());
                        return clean(clone.innerText || clone.textContent);
                    };
                    all('td,th,dt').forEach(cell => {
                        const rawLabel = ownText(cell);
                        const label = rawLabel.replace(/[:：]+$/, '').trim();
                        if (!label || label.length > 160 || isFooterText(label)) return;
                        const followedByColon = /^[:：]$/.test(ownText(cell.nextElementSibling));
                        if (!/[:：]\s*$/.test(rawLabel) && !followedByColon) return;
                        let valueCell = cell.nextElementSibling;
                        while (valueCell && /^[:：\s]*$/.test(ownText(valueCell)))
                            valueCell = valueCell.nextElementSibling;
                        const control = valueCell?.querySelector('input:not([type=hidden]),select,textarea');
                        const value = control?.tagName === 'SELECT'
                            ? clean(control.selectedOptions?.[0]?.text)
                            : clean(control?.value || valueCell?.innerText || valueCell?.textContent);
                        if (!value || value.length > 1600 || value === label || isFooterText(value)) return;
                        const key = `${label}\u001f${value}`;
                        if (seenInfo.has(key)) return;
                        seenInfo.add(key);
                        fields.push({
                            id: `display:saved-info:${seenInfo.size}`,
                            label,
                            value,
                            kind: 'text',
                            isReadOnly: true,
                            options: []
                        });
                    });
                    all('dl').forEach(list => {
                        const terms = [...list.querySelectorAll(':scope > dt')];
                        terms.forEach(term => {
                            const label = clean(term.innerText || term.textContent).replace(/[:：]+$/, '').trim();
                            const value = clean(term.nextElementSibling?.innerText || term.nextElementSibling?.textContent);
                            const key = `${label}\u001f${value}`;
                            if (!label || !value || isFooterText(label) || isFooterText(value)
                                || seenInfo.has(key)) return;
                            seenInfo.add(key);
                            fields.push({ id: `display:saved-info:${seenInfo.size}`, label, value,
                                kind: 'text', isReadOnly: true, options: [] });
                        });
                    });
                    all('tr').forEach(row => {
                        const cells = [...row.children].filter(cell => /^(TD|TH)$/.test(cell.tagName));
                        for (let index = 0; index + 1 < cells.length; index += 2) {
                            const label = ownText(cells[index]).replace(/[:：]+$/, '').trim();
                            let valueIndex = index + 1;
                            if (/^[:：]$/.test(ownText(cells[valueIndex])) && valueIndex + 1 < cells.length)
                                valueIndex++;
                            const valueCell = cells[valueIndex];
                            const control = valueCell?.querySelector('input:not([type=hidden]),select,textarea');
                            const value = control?.tagName === 'SELECT'
                                ? clean(control.selectedOptions?.[0]?.text)
                                : clean(control?.value || valueCell?.innerText || valueCell?.textContent);
                            const key = `${label}\u001f${value}`;
                            if (!label || !value || label === value || isFooterText(label) || isFooterText(value)
                                || label.length > 160
                                || value.length > 1600 || seenInfo.has(key)) continue;
                            if (cells[index].querySelector('input,select,textarea,button,a,img')) continue;
                            seenInfo.add(key);
                            fields.push({ id: `display:saved-info:${seenInfo.size}`, label, value,
                                kind: 'text', isReadOnly: true, options: [] });
                        }
                    });
                }
                if (screenKey === 'drug-info') {
                    const completeVisibleText = clean(document.body?.innerText || document.body?.textContent);
                    if (completeVisibleText) fields.push({
                        id: 'display:drug:complete-visible-text',
                        label: 'İlaç Bilgisi Tam Görünür Metni',
                        value: completeVisibleText,
                        kind: 'text',
                        isReadOnly: true,
                        options: []
                    });
                    const ruleTable = document.getElementById('form1:tableEx1');
                    const rulePanels = [...(ruleTable?.querySelectorAll('table.borderTable') || [])]
                      .filter(panel => panel.querySelector('.headerRow'));
                    const seenPairs = new Set();
                    const ownText = cell => {
                        if (!cell) return '';
                        const clone = cell.cloneNode(true);
                        clone.querySelectorAll('input,select,textarea,button,a,img,table').forEach(item => item.remove());
                        return clean(clone.innerText || clone.textContent);
                    };
                    all('td,th').forEach(cell => {
                        if (document.getElementById('form1:menu')?.contains(cell)) return;
                        const containingDataTable = cell.closest('table[id]');
                        if (containingDataTable?.id === 'form1:tableExIlacMesajListesi'
                            || containingDataTable?.id === 'form1:tableExIlacEsdeger') return;
                        const rawLabel = ownText(cell);
                        const label = rawLabel.replace(/[:：]+$/, '').trim();
                        if (!label || label.length > 120) return;
                        const followedByColon = /^[:：]$/.test(ownText(cell.nextElementSibling));
                        if (!/[:：]\s*$/.test(rawLabel) && !followedByColon
                            && !/^(?:İlaç Adı|Perakende\/Ödenen Fiyat|Kamu İndirim Oranı|Reçete Türü|Ayaktan Reçetesi|Yatan Reçetesi)$/i.test(label)) return;
                        let valueCell = cell.nextElementSibling;
                        while (valueCell && /^[:：]\s*$/.test(ownText(valueCell)))
                            valueCell = valueCell.nextElementSibling;
                        const ruleIndex = rulePanels.findIndex(panel => panel.contains(cell));
                        const section = ruleIndex >= 0 ? `rule:${ruleIndex}` : 'general';
                        const rawValue = clean(valueCell?.innerText || valueCell?.textContent);
                        const value = rawValue || '—';
                        if (!value || value.length > 700 || value === rawLabel) return;
                        const key = `${section}\u001f${label}\u001f${value}`;
                        if (seenPairs.has(key)) return;
                        seenPairs.add(key);
                        fields.push({
                            id: `display:drug:${section}:${seenPairs.size}`,
                            label,
                            value,
                            kind: 'text',
                            isReadOnly: true,
                            options: []
                        });
                    });
                    rulePanels.forEach((panel, index) => {
                        const header = panel.querySelector('.headerRow');
                        const value = clean(header.innerText || header.textContent);
                        if (!value) return;
                        fields.push({
                            id: `display:drug:rule-group:${index}`,
                            label: 'Ödeme Kuralı',
                            value,
                            kind: 'text',
                            isReadOnly: true,
                            options: []
                        });
                    });
                }
                if (['treatment-scheme', 'new-warning-code'].includes(screenKey)) {
                    const ownText = cell => {
                        if (!cell) return '';
                        const clone = cell.cloneNode(true);
                        clone.querySelectorAll('input,select,textarea,button,a,img,table').forEach(item => item.remove());
                        return clean(clone.innerText || clone.textContent);
                    };
                    const seen = new Set(fields.map(field => `${clean(field.label)}\u001f${clean(field.value)}`));
                    all('tr').forEach(row => {
                        const cells = [...row.children].filter(cell => /^(TD|TH)$/.test(cell.tagName));
                        for (let index = 0; index + 1 < cells.length; index++) {
                            const rawLabel = ownText(cells[index]);
                            const label = rawLabel.replace(/[:：]+$/, '').trim();
                            if (!label || label.length > 140
                                || cells[index].querySelector('input,select,textarea,button,a,img')) continue;
                            let valueIndex = index + 1;
                            if (/^[:：\s]*$/.test(ownText(cells[valueIndex])) && valueIndex + 1 < cells.length)
                                valueIndex++;
                            const valueCell = cells[valueIndex];
                            if (!/[:：]\s*$/.test(rawLabel) && valueIndex === index + 1
                                && cells.length > 2) continue;
                            const control = valueCell?.querySelector('input:not([type=hidden]),select,textarea');
                            const value = control?.tagName === 'SELECT'
                                ? clean(control.selectedOptions?.[0]?.text)
                                : clean(control?.value || valueCell?.innerText || valueCell?.textContent);
                            const key = `${label}\u001f${value}`;
                            if (!value || value === label || value.length > 1200 || seen.has(key)) continue;
                            seen.add(key);
                            fields.push({ id: `display:${screenKey}:${seen.size}`, label, value,
                                kind: 'text', isReadOnly: true, options: [] });
                        }
                    });
                }
                const actions = all('input[type=submit],input[type=button],input[type=image],button,a')
                    .filter(element => element.id && inScope(element))
                    .map(element => ({
                        id: element.id,
                        caption: clean(element.value || element.alt || element.title || element.innerText || element.textContent).slice(0, 120),
                        context: labelFor(element)
                    }))
                    .filter(action => action.caption);
                const directHeader = table => table.querySelector(
                    ':scope > thead > tr > th, :scope > tbody > tr > th, :scope > tr > th');
                const exactDataId = table => /(?:^|:)(?:tableEx|tbl)[^:]*$/i.test(table.id || '')
                    && !/head\d*$/i.test(table.id || '');
                const cellValue = cell => {
                    const values = [];
                    const add = value => {
                        const normalized = clean(value);
                        if (normalized && !values.includes(normalized)) values.push(normalized);
                    };
                    if (['report-detail', 'drug-report-detail'].includes(screenKey) && cell.querySelector('table')) {
                        const clone = cell.cloneNode(true);
                        clone.querySelectorAll('table').forEach(table => {
                            if (!table.querySelector('table')) table.remove();
                        });
                        add(clone.innerText);
                        add(clone.textContent);
                    } else {
                        add(cell.innerText);
                        add(cell.textContent);
                    }
                    const cellControlSelector = ['report-detail', 'drug-report-detail', 'drugs', 'eye-drugs', 'reports'].includes(screenKey)
                        ? 'input,select,textarea,[title],[alt]'
                        : 'input:not([type=hidden]),select,textarea,[title],[alt]';
                    cell.querySelectorAll(cellControlSelector).forEach(element => {
                        if (element.matches('input[type=hidden]')) {
                            const id = String(element.id || '');
                            const value = clean(element.value);
                            if (!value || /(?:rowAction|viewstate|eventvalidation)/i.test(id)
                                || /^(?:true|false)$/i.test(value)) return;
                        }
                        add((element.type || '').toLowerCase() === 'checkbox'
                            ? (element.checked ? 'Evet' : 'Hayır')
                            : element.value);
                        add(element.title);
                        add(element.alt);
                        if (element.tagName === 'SELECT') add(element.selectedOptions?.[0]?.text);
                    });
                    if (screenKey === 'reports') {
                        cell.querySelectorAll('a,button,input').forEach(element => {
                            add(element.getAttribute('href'));
                            add(element.getAttribute('onclick'));
                            add(element.getAttribute('name'));
                        });
                    }
                    return values.join(' · ').slice(0, 500);
                };
                const pageNumber = caption => /^\d+$/.test(caption) ? Number(caption) : 0;
                const pageActionsFor = table => {
                    let scope = table.parentElement;
                    while (scope && scope !== document.body) {
                        const candidates = [...scope.querySelectorAll(
                            'a[id],button[id],input[type=submit][id],input[type=button][id],input[type=image][id]')]
                            .filter(visible)
                            .map(element => ({ element, caption: actionCaption(element) }))
                            .filter(item => item.caption && (/^\d+$/.test(item.caption)
                                || /^(?:İleri|Geri|[<>]{1,2})$/i.test(item.caption)));
                        if (candidates.length > 0)
                            return candidates.map(item => ({
                                id: item.element.id,
                                caption: item.caption,
                                pageNumber: pageNumber(item.caption),
                                isCurrent: item.element.disabled
                                    || /current|selected|active/i.test(item.element.className || '')
                                    || item.element.getAttribute('aria-current') === 'page'
                            }));
                        scope = scope.parentElement;
                    }
                    return [];
                };
                const tables = all('table')
                    .filter(table => inScope(table) && (directHeader(table) || exactDataId(table)))
                    .map(table => {
                        const sourceRows = [...table.querySelectorAll(':scope > tbody > tr, :scope > tr')];
                        const rows = sourceRows.length ? sourceRows : [...table.querySelectorAll('tr')];
                        const headers = [...table.querySelectorAll(':scope > thead > tr > th, :scope > tbody > tr > th, :scope > tr > th')]
                            .map(cell => clean(cell.innerText || cell.textContent)).slice(0, 24);
                        const dataRows = rows.map(row => ({
                            values: [...row.children]
                                .filter(cell => cell.tagName === 'TD' || cell.tagName === 'TH')
                                .map(cellValue),
                            actionId: row.querySelector(
                                '[id*="rowAction"],a[id],input[type=image][id],button[id]')?.id ?? '',
                            background: getComputedStyle(row).backgroundColor || '',
                            selected: !!row.querySelector('input[type=checkbox]:checked')
                        }))
                            .filter(row => row.values.some(Boolean)
                                && !row.values.every((value, index) => value === (headers[index] || '')))
                            .slice(0, 100);
                        const columnCount = Math.max(headers.length, ...dataRows.map(row => row.values.length), 0);
                        const normalizedHeaders = Array.from({ length: columnCount }, (_, index) =>
                            headers[index] || `Alan ${index + 1}`);
                        return {
                            id: table.id || '',
                            headers: normalizedHeaders,
                            rows: dataRows.map(row => row.values),
                            rowActionIds: dataRows.map(row => row.actionId),
                            pageActions: pageActionsFor(table),
                            rowBackgrounds: dataRows.map(row => row.background),
                            rowSelections: dataRows.map(row => row.selected)
                        };
                    })
                    .filter(table => table.headers.some(Boolean) && table.rows.length);
                return { isOpen: true, url: location.href, fields, actions, tables, treatmentRows };
            })();
            """;
        var raw = await (sourceBrowser ?? RequireBrowser()).ExecuteScriptAsync(script);
        var probe = JsonSerializer.Deserialize<RemoteScreenProbe>(raw, JsonOptions)
            ?? new RemoteScreenProbe(false, string.Empty, [], [], [], []);
        var sourceFields = screenKey == "report-selection"
            ? probe.Fields.Where(field => field.Kind == "select").ToArray()
            : probe.Fields;
        var fields = MedulaRemoteScreenRefiner.RefineFields(screenKey, sourceFields);
        var tables = screenKey is "report-selection" or "treatment-scheme"
            || screenKey == "new-warning-code" && probe.TreatmentRows.Count > 0
            ? []
            : MedulaRemoteScreenRefiner.RefineTables(screenKey, probe.Tables);
        if (screenKey == "drug-info")
        {
            tables = tables.Where(table => table.Id != "form1:tableEx1")
                .Select(table => table.Id switch
                {
                    "form1:tableExIlacMesajListesi" => table with
                    {
                        Headers = table.Headers.Count == 2 ? ["Kod", "İlaç Mesajı"] : table.Headers,
                        Title = "İlaç Mesajları"
                    },
                    "form1:tableExIlacEsdeger" => table with { Title = "Eşdeğer ve Uyarı Bilgileri" },
                    _ => table
                }).ToArray();
        }
        var actions = probe.Actions
            .Where(action => MedulaSubScreenSafety.IsAllowed(screenKey, action.Caption))
            .GroupBy(action => action.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return new MedulaRemoteScreen(
            probe.IsOpen, screenKey, title, probe.Url, message, fields, actions, tables,
            TreatmentRows: probe.TreatmentRows);
    }

    private static MedulaRemoteScreen EmptyRemoteScreen(
        string key,
        string title,
        string message,
        string url = "") =>
        new(false, key, title, url, message, [], [], []);

    private async Task<ActionClickResult> ExecuteActionAndWaitAsync(
        string script,
        CancellationToken cancellationToken,
        bool waitForNavigation = true)
    {
        var currentBrowser = RequireBrowser();
        var navigationStarted = false;
        var navigationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Starting(object? _, CoreWebView2NavigationStartingEventArgs __) => navigationStarted = true;
        void Completed(object? _, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) navigationCompleted.TrySetResult();
            else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted)
                navigationCompleted.TrySetResult();
        }

        currentBrowser.CoreWebView2.NavigationStarting += Starting;
        currentBrowser.NavigationCompleted += Completed;
        try
        {
            var staleWarningMarker = await MarkVisibleMedulaWarningsBeforeActionAsync(currentBrowser);
            var warningRevision = GetWarningRevision();
            ReportWarning(string.Empty);
            ReportNotice(string.Empty);
            var rawResult = await currentBrowser.ExecuteScriptAsync(script);
            var click = JsonSerializer.Deserialize<ActionClickResult>(rawResult, JsonOptions)
                ?? new ActionClickResult(false, [], []);

            var emittedMessage = click.Messages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            if (emittedMessage is not null)
            {
                ReportBrowserMessage(emittedMessage);
                return click;
            }
            if (click.Confirmations.Any(message => !string.IsNullOrWhiteSpace(message))) return click;

            if (!click.Clicked) return click;
            await Task.Delay(waitForNavigation ? 100 : 50, cancellationToken);
            if (await ReadFreshWarningAfterActionAsync(
                    warningRevision, staleWarningMarker, currentBrowser) is { } immediateWarning)
                return click with
                {
                    Messages = click.Messages.Concat([immediateWarning]).ToArray()
                };
            if (!waitForNavigation) return click;
            if (navigationStarted)
            {
                var deadline = DateTime.UtcNow + NavigationTimeout;
                while (!navigationCompleted.Task.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await ReadFreshWarningAfterActionAsync(
                            warningRevision, staleWarningMarker, currentBrowser) is { } asynchronousWarning)
                        return click with
                        {
                            Messages = click.Messages.Concat([asynchronousWarning]).ToArray()
                        };
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException("Medula navigation timed out.");
                    await Task.Delay(25, cancellationToken);
                }
                await navigationCompleted.Task;
            }
            else
                await Task.Delay(150, cancellationToken);

            if (await ReadFreshWarningAfterActionAsync(
                    warningRevision, staleWarningMarker, currentBrowser) is { } completedWarning)
                return click with
                {
                    Messages = click.Messages.Concat([completedWarning]).ToArray()
                };

            return click;
        }
        finally
        {
            currentBrowser.CoreWebView2.NavigationStarting -= Starting;
            currentBrowser.NavigationCompleted -= Completed;
        }
    }

    private async Task DispatchTrustedElementClickAsync(
        string elementId,
        CancellationToken cancellationToken)
    {
        var currentBrowser = RequireBrowser();
        var elementIdJson = JsonSerializer.Serialize(elementId);
        var rawExists = await currentBrowser.ExecuteScriptAsync(
            $"!!document.getElementById({elementIdJson})");
        if (!string.Equals(rawExists, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Medula denetimi görünür değil: {elementId}");
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = JsonSerializer.Serialize(new
        {
            expression = $"document.getElementById({elementIdJson}).click()",
            userGesture = true,
            awaitPromise = true,
            returnByValue = true
        });
        await currentBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Runtime.evaluate", parameters);
    }

    private async Task<string> FetchPrescriptionPrintAsync(
        string actionId,
        CancellationToken cancellationToken)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireMedulaOnline();
        var currentBrowser = RequireBrowser();
        var actionIdJson = JsonSerializer.Serialize(actionId);
        var rawProbe = await currentBrowser.ExecuteScriptAsync($$"""
            (() => {
                const action = document.getElementById({{actionIdJson}});
                const form = action?.form;
                if (!action || !form) throw new Error('Medula Yazdır formu bulunamadı.');
                const parameters = new URLSearchParams();
                new FormData(form).forEach((value, key) => {
                    if (typeof value === 'string') parameters.append(key, value);
                });
                if (action.name) parameters.set(action.name, action.value || '');
                return {
                    url: form.action || location.href,
                    method: (form.method || 'GET').toUpperCase(),
                    body: parameters.toString()
                };
            })();
            """);
        cancellationToken.ThrowIfCancellationRequested();
        var probe = JsonSerializer.Deserialize<PrintRequestProbe>(rawProbe, JsonOptions)
            ?? throw new InvalidOperationException("Medula Yazdır isteği okunamadı.");
        var requestUri = new Uri(probe.Url);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer()
        };
        var cookies = await currentBrowser.CoreWebView2.CookieManager.GetCookiesAsync(probe.Url);
        foreach (var sourceCookie in cookies)
        {
            var cookie = new Cookie(
                sourceCookie.Name,
                sourceCookie.Value,
                string.IsNullOrWhiteSpace(sourceCookie.Path) ? "/" : sourceCookie.Path);
            handler.CookieContainer.Add(requestUri, cookie);
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        var method = new HttpMethod(probe.Method);
        var targetUri = requestUri;
        if (method == HttpMethod.Get || method == HttpMethod.Head)
        {
            var separator = requestUri.Query.Length == 0 ? "?" : "&";
            targetUri = new Uri(probe.Url + separator + probe.Body);
        }
        using var request = new HttpRequestMessage(method, targetUri);
        if (method != HttpMethod.Get && method != HttpMethod.Head)
            request.Content = new StringContent(
                probe.Body, Encoding.UTF8, "application/x-www-form-urlencoded");
        if (Uri.TryCreate(currentBrowser.Source?.AbsoluteUri, UriKind.Absolute, out var referrer))
            request.Headers.Referrer = referrer;
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            for (var redirect = 0; redirect < 5
                && (int)response.StatusCode is >= 300 and < 400;
                redirect++)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException(
                        $"Medula Yazdır yönlendirmesi hedef içermiyor ({(int)response.StatusCode}).");
                var nextUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(response.RequestMessage?.RequestUri ?? targetUri, location);
                response.Dispose();
                using var redirectedRequest = new HttpRequestMessage(HttpMethod.Get, nextUri);
                redirectedRequest.Headers.Referrer = targetUri;
                redirectedRequest.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
                response = await client.SendAsync(
                    redirectedRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                targetUri = nextUri;
            }
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            throw new InvalidOperationException($"Medula Yazdır yanıtı boş döndü ({targetUri}).");
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var looksLikePdf = bytes.Length >= 5
            && bytes[0] == (byte)'%'
            && bytes[1] == (byte)'P'
            && bytes[2] == (byte)'D'
            && bytes[3] == (byte)'F';
        if (!looksLikePdf
            && !contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Medula Yazdır PDF yerine {contentType} döndürdü.");

        var downloadDirectory = pharmacyApp.Infrastructure.AppPaths.DownloadsDirectory;
        Directory.CreateDirectory(downloadDirectory);
        var targetPath = UniqueDownloadPath(
            downloadDirectory,
            $"Medula-Recete-{DateTime.Now:yyyyMMdd-HHmmss}",
            ".pdf");
        await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
        return targetPath;
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<bool> ConnectWithRetriesAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            if (await ConnectAsync(cancellationToken)) return true;
            if (attempt < maximumAttempts)
                await Task.Delay(750, cancellationToken);
        }

        return false;
    }

    private static PrescriptionPayload BuildPrescriptionPayload(MedulaPrescriptionInput input)
    {
        var mapped = MedulaPrescriptionFieldMapper.Build(input);
        return new PrescriptionPayload(mapped.Values, mapped.Checks);
    }

    private async Task<bool> IsPrescriptionEntryPageAsync()
    {
        const string script = "!!(document.getElementById('f:buttonReceteKaydet') || document.getElementById('f:button2'))";
        var result = await RequireBrowser().ExecuteScriptAsync(script);
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsPrescriptionViewPageAsync()
    {
        const string script = """
            !!document.getElementById('f:t18')
                && !!document.getElementById('f:tbl1')
                && !document.getElementById('form1:tableExReceteList')
            """;
        var result = await RequireBrowser().ExecuteScriptAsync(script);
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasPrescriptionEntryMenuAsync()
    {
        const string script = "!!document.getElementById('form1:menuHtmlCommandExButton21')";
        var result = await RequireBrowser().ExecuteScriptAsync(script);
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForPageStateAsync(
        Func<Task<bool>> probe,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MedulaSubScreenSafety.DestinationPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await probe()) return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Medula can briefly replace the document during a JSF postback.
            }
            if (attempt + 1 < MedulaSubScreenSafety.DestinationPollAttempts)
                await Task.Delay(MedulaSubScreenSafety.DestinationPollIntervalMilliseconds, cancellationToken);
        }
        return false;
    }

    private async Task<MedulaPrescriptionPageResult> ReadPrescriptionPageResultAsync(string fallbackMessage)
    {
        const string script = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const rawBodyText = document.body?.innerText ?? '';
                const bodyText = clean(rawBodyText);
                const rawHtml = document.documentElement?.innerHTML ?? '';
                const visible = element => element.getClientRects().length > 0
                    && getComputedStyle(element).visibility !== 'hidden';
                const errorSelectors = [
                    '.error', '.errors', '.errorMessage', '.message-error',
                    '[class*="error"]', '[class*="hata"]', '[id*="error"]', '[id*="hata"]',
                    '[style*="color: red"]', '[style*="color:red"]'
                ];
                const validationPattern = /(?:(?:^|[^a-zçğıöşü])hata(?:s[ıi])?(?:$|[^a-zçğıöşü])|olmal[ıi]d[ıi]r|zorunlu|ge[cç]ersiz|girilmelidir|gerekmektedir|bulunamad[ıi]|ba[sş]ar[ıi]s[ıi]z|uygun de[gğ]ildir|[cç]al[ıi][sş](?:mamaktad[ıi]r|m[ıi]yor|maz))/i;
                const doctorBranchPattern = /doktor['’]un birden fazla branşı bulunmaktad[ıi]r\.\s*branş seçimi yapmanız gerekmektedir\.?/i;
                const informationalPattern = /(?:hatal[ıi]\s+bilgileri\s+d[uü]zelttirmek\s+i[cç]in\s+sgk\s+sosyal\s+g[uü]venlik\s+merkezlerine\s+ba[sş]vurunuz|bilgi g[uü]venli[gğ]i nedeniyle|kay[ıi]t alt[ıi]na al[ıi]nmaktad[ıi]r)/i;
                const lineErrors = rawBodyText.split(/\r?\n/).map(clean).slice(0, 12)
                    .filter(text => text && text.length <= 500
                        && (validationPattern.test(text) || doctorBranchPattern.test(text)));
                const embeddedValidation = bodyText.match(
                    /en az\s+\d+\s+tane\s+[^.!?]{1,160}?\s+gerekmektedir\.?/i)?.[0] || '';
                const successPattern = /(?:başarıyla|başarı\s+ile|başarılı\s+olarak|kaydedilmiştir|kaydedildi|silinmiştir|silindi|onaylanmıştır|onaylandı|işlem\s+tamamlanmıştır|işlem\s+tamamlandı|kayıt\s+tamamlandı)/i;
                const successMessagePattern = /(?:[A-Z0-9-]+\s+nolu\s+reçete\s+(?:başarıyla\s+)?(?:kaydedildi|kaydedilmiştir|silindi|silinmiştir)|kayıt\s+tamamlandı\.\s*reçete\s+numarası\s*:\s*[A-Z0-9-]+)\.?/i;
                const embeddedSuccess = bodyText.match(successMessagePattern)?.[0]
                    || clean(rawHtml.match(successMessagePattern)?.[0]);
                const successText = rawBodyText.split(/\r?\n/).map(clean)
                    .find(text => text && text.length <= 600 && successPattern.test(text))
                    || embeddedSuccess;
                const noticePattern = /(?:ilaç\s+adedi\s+d[uü][sş][uü]r[uü]ld[uü]|d[uü][sş][uü]r[uü]ld[uü]|d[uü]zenlendi|maksimum\s+ila[cç]\s+adet|otomatik(?:\s+olarak)?\s+g[uü]ncellendi|g[uü]ncellendi|de[gğ]i[sş]tirildi|bilgilendirme|karekod\s+bilgisi\s+tekrar\s+okutulmu[sş])/i;
                const noticeSelectors = [
                    '[role="status"]', '.warning', '.notice', '.info',
                    '[class*="warning"]', '[class*="notice"]', '[class*="info"]',
                    '[id*="warning"]', '[id*="notice"]', '[id*="info"]',
                    'font[color]', '[style*="color"]', '[style*="background"]'
                ];
                const noticeText = [...new Set(noticeSelectors.flatMap(selector =>
                    [...document.querySelectorAll(selector)]
                        .filter(visible)
                        .filter(element => {
                            const dataTable = element.closest(
                                'table[id*="tableEx"],table[id*="TableEx"],table[id*="tbl"]');
                            return !dataTable || element.getAttribute('role') === 'status';
                        })
                        .map(element => clean(element.innerText || element.textContent)))
                    .concat(rawBodyText.split(/\r?\n/).map(clean)))]
                    .find(text => text && text.length <= 1000 && noticePattern.test(text)) || '';
                const errors = [...new Set(errorSelectors.flatMap(selector =>
                    [...document.querySelectorAll(selector)]
                        .filter(visible)
                        .filter(element => {
                            const dataTable = element.closest(
                                'table[id*="tableEx"],table[id*="TableEx"],table[id*="tbl"]');
                            return !dataTable || element.getAttribute('role') === 'alert';
                        })
                        .map(element => clean(element.innerText || element.textContent)))
                    .concat(lineErrors, embeddedValidation))]
                    .filter(Boolean)
                    .filter(text => !/^silinmiş\s+karekod$/i.test(text))
                    .filter(text => text.length <= 1000 && !informationalPattern.test(text));
                const fieldIds = [
                    'f:t18', 'f:text37', 'f:menu4', 'f:menu2', 'f:t24', 'f:t33',
                    'f:t29', 'f:t31', 'f:m4', 'f:m8', 'f:t43', 'f:m6', 'f:t39',
                    'f:t41', 'f:t42', 'f:m7', 'f:checkbox1', 'f:checkbox2'
                ];
                const fieldValues = Object.fromEntries(fieldIds.map(id => [id, document.getElementById(id)?.value ?? '']));
                ['f:checkbox1', 'f:checkbox2', 'f:checkbox4'].forEach(id => {
                    const field = document.getElementById(id);
                    fieldValues[id] = field?.checked ? 'true' : 'false';
                });
                const valueAfterLabel = pattern => {
                    const labels = [...document.querySelectorAll('label,td,th,span')].filter(visible);
                    const label = labels.find(element => pattern.test(clean(element.textContent).replace(/\s*:\s*$/, '')));
                    if (!label) return '';
                    const cell = label.closest('td,th');
                    const row = cell?.parentElement;
                    const cells = row ? [...row.children] : [];
                    let index = cells.indexOf(cell);
                    while (++index < cells.length) {
                        const candidate = cells[index];
                        const control = candidate.querySelector('input:not([type=hidden]),select,textarea');
                        const value = control?.tagName === 'SELECT'
                            ? clean(control.selectedOptions?.[0]?.text)
                            : clean(control?.value || candidate.textContent);
                        if (value && !/^[:：\-–—]$/.test(value)) return value;
                    }
                    return '';
                };
                const robustValueAfterLabel = pattern => {
                    const direct = valueAfterLabel(pattern);
                    if (direct) return direct;
                    const candidates = [...document.querySelectorAll('label,td,th,span,div')].filter(visible);
                    for (const candidate of candidates) {
                        const fullText = clean(candidate.textContent);
                        const colon = fullText.indexOf(':');
                        if (colon > 0 && pattern.test(fullText.slice(0, colon).trim())) {
                            const inlineValue = clean(fullText.slice(colon + 1));
                            if (inlineValue && !/^[-–—]$/.test(inlineValue)) return inlineValue;
                        }
                        if (!pattern.test(fullText.replace(/\s*:\s*$/, ''))) continue;
                        const parent = candidate.parentElement;
                        const control = parent?.querySelector('select,input:not([type=hidden]),textarea');
                        const controlValue = control?.tagName === 'SELECT'
                            ? clean(control.selectedOptions?.[0]?.text)
                            : clean(control?.value);
                        if (controlValue) return controlValue;
                        let sibling = candidate.nextElementSibling;
                        while (sibling) {
                            const valueControl = sibling.querySelector('select,input:not([type=hidden]),textarea');
                            const value = valueControl?.tagName === 'SELECT'
                                ? clean(valueControl.selectedOptions?.[0]?.text)
                                : clean(valueControl?.value || sibling.textContent);
                            if (value && !/^[:：\-–—]$/.test(value)) return value;
                            sibling = sibling.nextElementSibling;
                        }
                    }
                    const lines = rawBodyText.split(/\r?\n/).map(clean);
                    for (let index = 0; index < lines.length; index++) {
                        const line = lines[index];
                        const colon = line.indexOf(':');
                        if (colon > 0 && pattern.test(line.slice(0, colon).trim())) {
                            const inlineValue = clean(line.slice(colon + 1));
                            if (inlineValue && !/^[-–—]$/.test(inlineValue)) return inlineValue;
                        }
                        if (pattern.test(line.replace(/\s*:\s*$/, ''))) {
                            const next = clean(lines[index + 1]);
                            if (next && !/^[:：\-–—]$/.test(next)) return next;
                        }
                    }
                    return '';
                };
                const summaryRow = [...document.querySelectorAll('tr')]
                    .filter(visible)
                    .find(row => {
                        const text = clean(row.textContent);
                        return /Reçete\s*No/i.test(text)
                            && /Karekod\s+Durumu/i.test(text)
                            && /Kapsam/i.test(text)
                            && /(?:Fat\.?\s*Türü|Fatura\s*(?:Türü|Grubu|Tipi))/i.test(text);
                    });
                const summaryCells = summaryRow
                    ? [...summaryRow.cells].map(cell => clean(cell.textContent))
                    : [];
                const summaryLabelPattern = /^(?:Reçete\s*No(?:su)?|Karekod\s+Durumu|Kapsam|Hasta\s+Türü|Fat\.?\s*Türü|Fatura\s*(?:Türü|Grubu|Tipi))\s*:?$/i;
                const summaryIndex = pattern => summaryCells.findIndex(value =>
                    pattern.test(value.replace(/\s*:\s*$/, '')));
                const summaryValue = pattern => {
                    const index = summaryIndex(pattern);
                    if (index < 0) return '';
                    for (let candidateIndex = index + 1; candidateIndex < summaryCells.length; candidateIndex++) {
                        const value = summaryCells[candidateIndex];
                        if (!value || /^[:：\-–—]$/.test(value)) continue;
                        if (summaryLabelPattern.test(value)) return '';
                        return value;
                    }
                    return '';
                };
                const summaryPrescriptionNumber = summaryValue(/^Reçete\s*No(?:su)?$/i);
                const summaryQrStatus = summaryValue(/^Karekod\s+Durumu$/i);
                const summaryCoverage = summaryValue(/^Kapsam$/i);
                const coverageIndex = summaryIndex(/^Kapsam$/i);
                const invoiceIndex = summaryIndex(/^(?:Fat\.?\s*Türü|Fatura\s*(?:Türü|Grubu|Tipi))$/i);
                const summaryPatientType = coverageIndex >= 0 && invoiceIndex > coverageIndex
                    ? summaryCells.slice(coverageIndex + 1, invoiceIndex)
                        .find(value => /^(?:Çalışan|Emekli)$/i.test(value)) || ''
                    : '';
                const summaryInvoiceType = summaryValue(
                    /^(?:Fat\.?\s*Türü|Fatura\s*(?:Türü|Grubu|Tipi))$/i);
                fieldValues['medula:e-prescription-no'] = valueAfterLabel(/^(?:E-?Reçete|Elektronik Reçete) No(?:su)?$/i);
                fieldValues['medula:prescription-no'] = summaryPrescriptionNumber
                    || valueAfterLabel(/^Reçete No(?:su)?$/i);
                const invoiceControl = [...document.querySelectorAll('select,input,textarea')].find(element =>
                    /fatura.*(?:grup|t[uü]r|tip)|(?:grup|t[uü]r|tip).*fatura/i.test(
                        `${element.id || ''} ${element.name || ''}`));
                const invoiceControlValue = invoiceControl?.tagName === 'SELECT'
                    ? clean(invoiceControl.selectedOptions?.[0]?.text)
                    : clean(invoiceControl?.value);
                const invoiceLabelValue = robustValueAfterLabel(/^Fatura\s*(?:Grubu|Türü)(?:\s*\(\*\))?$/i);
                const invoiceTextMatch = rawBodyText.match(
                    /Fatura\s*(?:Grubu|Türü)(?:\s*\(\*\))?\s*[:：]?\s*([^\r\n]+)/i);
                const invoiceTextValue = clean(invoiceTextMatch?.[1]);
                const usableInvoiceValue = value => value
                    && !/^(?:[-–—]|Seçiniz|Fatura\s*(?:Grubu|Türü|Tipi)?)$/i.test(clean(value));
                fieldValues['medula:invoice-type'] = [summaryInvoiceType, invoiceControlValue, invoiceLabelValue, invoiceTextValue]
                    .find(usableInvoiceValue) || '';
                fieldValues['medula:qr-status'] = summaryQrStatus || valueAfterLabel(/^Karekod Durumu$/i);
                fieldValues['medula:patient-type'] = summaryPatientType || valueAfterLabel(/^Hasta Türü$/i);
                fieldValues['medula:coverage'] = summaryCoverage || valueAfterLabel(/^Kapsam$/i);
                const insuranceLabelValue = robustValueAfterLabel(
                    /^(?:(?:Sigortalı|Sigortalılık|Güvence|Hak Sahibi)\s*(?:Türü|Tipi|Durumu)|Çalışan\s*\/\s*Emekli)$/i);
                const insuranceInvoiceMatch = rawBodyText.match(/\b(Çalışan|Emekli)\s+Fat\.?\s*Türü\b/i);
                fieldValues['medula:insurance-type'] = summaryPatientType || insuranceLabelValue
                    || clean(insuranceInvoiceMatch?.[1]);
                fieldValues['medula:prescription-note'] = valueAfterLabel(/^Reçete (?:Notu|Açıklaması)$/i);
                fieldValues['medula:patient-first-name'] = clean(document.getElementById('f:t15')?.textContent);
                fieldValues['medula:patient-last-name'] = clean(document.getElementById('f:t16')?.textContent);
                const branchLabel = [...document.querySelectorAll('label,span,td,th')].find(element =>
                    visible(element) && /^Branş\s*(?:\(\*\))?\s*:?$/i.test(clean(element.textContent)));
                let branchValue = '';
                if (branchLabel) {
                    const row = branchLabel.closest('tr');
                    const cells = row ? [...row.children] : [];
                    const labelCell = branchLabel.closest('td,th');
                    const labelIndex = cells.indexOf(labelCell);
                    const valueCell = labelIndex >= 0 ? cells.slice(labelIndex + 1).find(cell => clean(cell.textContent)) : null;
                    branchValue = clean(valueCell?.querySelector('select')?.selectedOptions?.[0]?.text
                        || valueCell?.querySelector('input')?.value
                        || valueCell?.textContent);
                }
                fieldValues['medula:branch'] = branchValue;
                const savedRoot = document.getElementById('f:box25');
                let savedSummary = null;
                if (savedRoot && visible(savedRoot)) {
                    const paymentFields = [];
                    const seenPayments = new Set();
                    const addPayment = (label, value) => {
                        label = clean(label).replace(/\s*:\s*$/, '');
                        value = clean(value);
                        const key = `${label}\u0000${value}`;
                        if (!label || !value || seenPayments.has(key)) return;
                        seenPayments.add(key);
                        paymentFields.push({ label, value });
                    };
                    [...savedRoot.querySelectorAll('tr.row1, tr.row2')].forEach(row => {
                        const cells = [...row.children];
                        for (let index = 0; index + 2 < cells.length; index++) {
                            if (clean(cells[index + 1].textContent) !== ':') continue;
                            addPayment(cells[index].textContent, cells[index + 2].textContent);
                        }
                    });
                    const patientTotal = document.getElementById('f:box28');
                    if (patientTotal) {
                        const cells = [...patientTotal.querySelectorAll(':scope > tbody > tr > td')];
                        if (cells.length >= 2) addPayment(cells[0].textContent, cells[1].textContent);
                    }
                    const actionCaptions = ['f:box23', 'f:box24', 'f:box22']
                        .flatMap(id => [...(document.getElementById(id)?.querySelectorAll(
                            'button,input[type=button],input[type=submit]') ?? [])])
                        .filter(visible)
                        .map(action => clean(action.value || action.innerText || action.textContent))
                        .filter(Boolean);
                    const title = clean(savedRoot.querySelector('tr.headerRow')?.textContent)
                        || 'Reçete Tutarları';
                    if (paymentFields.length > 0)
                        savedSummary = { title, paymentFields, actionCaptions };
                }
                return {
                    url: location.href,
                    title: document.title || 'Medula',
                    hasLoginForm: !!document.querySelector("input[id$=':secret1']"),
                    isPrescriptionEntryPage: !!(document.getElementById('f:buttonReceteKaydet')
                        || document.getElementById('f:button2')),
                    bodyText: bodyText.slice(0, 8000),
                    errorText: errors.join('\n'),
                    successText,
                    noticeText,
                    fieldValues,
                    savedSummary
                };
            })();
            """;
        var rawResult = await RequireBrowser().ExecuteScriptAsync(script);
        var page = JsonSerializer.Deserialize<PrescriptionPageProbe>(rawResult, JsonOptions)
            ?? throw new InvalidOperationException("The Medula prescription response could not be read.");
        var normalizedFields = MedulaPrescriptionHeaderParser.Normalize(page.FieldValues, page.BodyText);
        var authenticated = !page.HasLoginForm;
        var pageSuccess = string.IsNullOrWhiteSpace(page.ErrorText)
            ? FirstSuccessMessage(page.SuccessText, page.BodyText)
            : null;
        var pageNotice = NormalizeBrowserMessage(page.NoticeText);
        var message = !string.IsNullOrWhiteSpace(page.ErrorText)
            ? page.ErrorText
            : pageSuccess ?? (pageNotice.Length == 0 ? null : pageNotice) ?? fallbackMessage;
        if (!string.IsNullOrWhiteSpace(page.ErrorText)) ReportWarning(page.ErrorText);
        else if (!string.IsNullOrWhiteSpace(pageSuccess)) ReportConfirmation(pageSuccess);
        if (pageNotice.Length > 0) ReportNotice(pageNotice);
        return new MedulaPrescriptionPageResult(
            authenticated && string.IsNullOrWhiteSpace(page.ErrorText),
            page.IsPrescriptionEntryPage,
            page.Title,
            page.Url,
            message,
            page.BodyText,
            FieldValues: normalizedFields,
            WarningText: string.IsNullOrWhiteSpace(page.ErrorText) ? null : page.ErrorText,
            SavedSummary: page.SavedSummary,
            SuccessText: pageSuccess,
            NoticeText: pageNotice.Length == 0 ? null : pageNotice);
    }

    private static bool IsDoctorBranchSelectionWarning(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains("birden fazla branşı", StringComparison.OrdinalIgnoreCase)
        && text.Contains("branş seçimi yapmanız gerekmektedir", StringComparison.OrdinalIgnoreCase);

    private void RememberTopLevelScreen(
        MedulaPrescriptionInput input,
        string actionId,
        MedulaRemoteScreen screen)
    {
        if (systemErrorRecoveryInProgress || !screen.IsOpen) return;
        lastRestorationState = BuildPrescriptionRestorationState(
            input,
            [new MedulaRestorationStep(actionId, EmptyRemotePayload(), null)]);
    }

    private void RememberSubScreenStep(
        string sourceScreenKey,
        string actionId,
        MedulaRemoteFieldPayload payload,
        MedulaRemoteScreen screen)
    {
        if (systemErrorRecoveryInProgress || !screen.IsOpen
            || sourceScreenKey == "doctor-branch-select") return;
        var steps = lastRestorationState.Steps.ToList();
        steps.Add(new MedulaRestorationStep(
            actionId,
            CopyRemotePayload(payload),
            null));
        lastRestorationState = lastRestorationState with { Steps = steps };
    }

    private void RememberDrugRowScreen(
        MedulaPrescriptionInput input,
        int rowIndex,
        string actionId,
        MedulaRemoteScreen screen)
    {
        if (systemErrorRecoveryInProgress || !screen.IsOpen) return;
        lastRestorationState = BuildPrescriptionRestorationState(
            input,
            [new MedulaRestorationStep(actionId, EmptyRemotePayload(), rowIndex)]);
    }

    private static MedulaRemoteFieldPayload EmptyRemotePayload() =>
        new(new Dictionary<string, string>(), new Dictionary<string, bool>());

    private static MedulaRemoteFieldPayload CopyRemotePayload(MedulaRemoteFieldPayload payload) =>
        new(
            new Dictionary<string, string>(payload.Values, StringComparer.Ordinal),
            new Dictionary<string, bool>(payload.Checks, StringComparer.Ordinal));

    private async void OnBrowserNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess || systemErrorRecoveryInProgress) return;
        try
        {
            await Task.Delay(60);
            if (connectionGate.CurrentCount > 0 && await IsLoginPageAsync())
            {
                if (activeOperationRestorationState is not null) return;
                var restoration = activeOperationRestorationState ?? lastRestorationState;
                await RecoverFromExpiredSessionAsync(restoration);
                return;
            }
            if (activeOperationRestorationState is not null) return;
            if (await IsSystemErrorPageAsync())
                await RecoverFromSystemErrorAsync();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (browser?.CoreWebView2 is null)
        {
        }
        catch
        {
            // A document can be replaced again while the post-navigation probe runs.
        }
    }

    private async Task<bool> IsSystemErrorPageAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        const string script = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const title = clean(document.title);
                const body = clean(document.body?.innerText ?? '');
                const hasKnownMedulaControls = !!(
                    document.getElementById('f:buttonReceteKaydet')
                    || document.getElementById('f:button2')
                    || document.getElementById('form1:menuHtmlCommandExButton21')
                    || document.querySelector("input[id$=':secret1']"));
                const headingText = [...document.querySelectorAll('h1,h2,h3,strong,b,font')]
                    .map(element => clean(element.innerText || element.textContent))
                    .join(' | ');
                const blockingError = /yazılımsal veya sistemsel bir hata oluştu/i;
                const plainSystemError = /^(?:sistem hatası|system error)[.!]?$/i;
                const titleMarker = /(?:sistem hatası|system error)/i.test(title)
                    || blockingError.test(title);
                const headingMarker = /(?:^|\|\s*)(?:sistem hatası|system error)(?:\s*\||$)/i.test(headingText)
                    || blockingError.test(headingText);
                const bodyMarker = /^(?:sistem hatası|system error)\b/i.test(body)
                    || /beklenmeyen bir sistem hatası oluştu/i.test(body)
                    || blockingError.test(body)
                    || (body.length <= 3000 && /sistem hatası/i.test(body));
                const errorPath = /(?:\/error(?:[/?#]|$)|error\.jsp|exception)/i.test(location.href);
                return blockingError.test(title) || blockingError.test(headingText) || blockingError.test(body)
                    || plainSystemError.test(title) || plainSystemError.test(body)
                    || headingText.split('|').some(value => plainSystemError.test(clean(value)))
                    || (!hasKnownMedulaControls && (titleMarker || headingMarker || bodyMarker
                        || (errorPath && /sistem/i.test(body) && /hata/i.test(body))));
            })();
            """;
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ReadSystemErrorMessageAsync()
    {
        if (browser?.CoreWebView2 is null)
            return "Yazılımsal veya sistemsel bir hata oluştu.";
        const string script = """
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const body = document.body?.innerText ?? '';
                const exact = body.split(/\r?\n/).map(clean).find(line =>
                    /yazılımsal veya sistemsel bir hata oluştu/i.test(line));
                if (exact) return exact;
                const heading = [...document.querySelectorAll('h1,h2,h3,strong,b,font')]
                    .map(element => clean(element.innerText || element.textContent))
                    .find(text => /(?:sistem hatası|system error)/i.test(text));
                return heading || clean(document.title) || 'Yazılımsal veya sistemsel bir hata oluştu.';
            })();
            """;
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(raw)?.Trim() is { Length: > 0 } message
            ? message
            : "Yazılımsal veya sistemsel bir hata oluştu.";
    }

    private async Task<bool> IsLoginPageAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        var page = await ReadPageProbeAsync();
        return MedulaPageClassifier.Classify(
            page.Url,
            page.HasLoginForm,
            page.Text,
            page.Title,
            page.HasKnownMedulaControls) == MedulaPageKind.Login;
    }

    private async Task RecoverFromExpiredSessionAsync(MedulaRestorationState restoration)
    {
        await systemErrorRecoveryGate.WaitAsync();
        try
        {
            if (systemErrorRecoveryInProgress || !await IsLoginPageAsync()) return;
            systemErrorRecoveryInProgress = true;
            Report(
                MedulaConnectionState.TemporarilyOffline,
                "Geçici çevrimdışı · Medula oturumu sona erdi; yeniden giriş yapılıyor.");

            await prescriptionGate.WaitAsync();
            try
            {
                await RestoreAfterSessionExpiryCoreAsync(restoration, CancellationToken.None);
            }
            finally
            {
                prescriptionGate.Release();
            }
        }
        catch (Exception exception)
        {
            Report(MedulaConnectionState.Failed,
                $"Medula oturumu yeniden açılamadı: {exception.Message}");
        }
        finally
        {
            systemErrorRecoveryInProgress = false;
            systemErrorRecoveryGate.Release();
        }
    }

    private async Task RecoverFromSystemErrorAsync()
    {
        await systemErrorRecoveryGate.WaitAsync();
        try
        {
            if (systemErrorRecoveryInProgress || !await IsSystemErrorPageAsync()) return;
            systemErrorRecoveryInProgress = true;
            var restoration = lastRestorationState;
            ReportWarning(await ReadSystemErrorMessageAsync());

            await prescriptionGate.WaitAsync();
            try
            {
                await RestoreAfterSystemErrorCoreAsync(restoration, CancellationToken.None);
            }
            finally
            {
                prescriptionGate.Release();
            }
        }
        catch (Exception exception)
        {
            Report(MedulaConnectionState.Failed,
                $"Medula Sistem Hatası sonrasında yeniden açılamadı: {exception.Message}");
        }
        finally
        {
            systemErrorRecoveryInProgress = false;
            systemErrorRecoveryGate.Release();
        }
    }

    private async Task<bool> RecoverFromSystemErrorDuringOperationAsync(
        MedulaRestorationState restoration,
        CancellationToken cancellationToken,
        bool systemErrorSignalled = false)
    {
        await systemErrorRecoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (systemErrorRecoveryInProgress ||
                (!systemErrorSignalled && !await IsSystemErrorPageAsync()))
                return false;
            systemErrorRecoveryInProgress = true;
            ReportWarning(await ReadSystemErrorMessageAsync());
            await RestoreAfterSystemErrorCoreAsync(restoration, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Report(MedulaConnectionState.Failed,
                $"Medula Sistem Hatası sonrasında yeniden açılamadı: {exception.Message}");
            return false;
        }
        finally
        {
            systemErrorRecoveryInProgress = false;
            systemErrorRecoveryGate.Release();
        }
    }

    private async Task<bool> RecoverElectronicPrescriptionOperationIfRequiredAsync(
        MedulaRestorationState restoration,
        CancellationToken cancellationToken)
    {
        if (await IsSystemErrorPageAsync())
            return await RecoverFromSystemErrorDuringOperationAsync(restoration, cancellationToken);
        if (await IsLoginPageAsync())
            return await RecoverFromExpiredSessionDuringOperationAsync(restoration, cancellationToken);
        return false;
    }

    private async Task<bool> RecoverFromExpiredSessionDuringOperationAsync(
        MedulaRestorationState restoration,
        CancellationToken cancellationToken)
    {
        await systemErrorRecoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (systemErrorRecoveryInProgress || !await IsLoginPageAsync()) return false;
            systemErrorRecoveryInProgress = true;
            Report(
                MedulaConnectionState.TemporarilyOffline,
                "Geçici çevrimdışı · Medula oturumu sona erdi; yeniden giriş yapılıyor.");
            await RestoreAfterSessionExpiryCoreAsync(restoration, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Report(MedulaConnectionState.Failed,
                $"Medula oturumu yeniden açılamadı: {exception.Message}");
            return false;
        }
        finally
        {
            systemErrorRecoveryInProgress = false;
            systemErrorRecoveryGate.Release();
        }
    }

    private async Task RestoreAfterSystemErrorCoreAsync(
        MedulaRestorationState restoration,
        CancellationToken cancellationToken)
    {
        Report(MedulaConnectionState.TemporarilyOffline,
            "Geçici çevrimdışı · Medula hatası algılandı; arka plan tarayıcısı yeniden başlatılıyor.");
        await RecreateBrowserAsync(cancellationToken);
        var resumed = await TryResumeAuthenticatedHomeAsync(cancellationToken);
        if (!resumed && !await ConnectWithRetriesAsync(cancellationToken))
            throw new InvalidOperationException("Yeni Medula oturumu açılamadı.");
        if (restoration.ElectronicPrescription is { } electronicPrescription)
        {
            await RestoreElectronicPrescriptionStateAsync(electronicPrescription, cancellationToken);
            if (electronicPrescription.PrescriptionOpen)
                await ReplayRestorationStateAsync(restoration, cancellationToken);
            lastRestorationState = restoration;
            Report(MedulaConnectionState.Connected,
                electronicPrescription.DetailOpen
                    ? "Medula yeniden açıldı ve E-Reçete ekranı geri yüklendi."
                    : "Medula yeniden açıldı ve E-Reçete Sorgu ekranı geri yüklendi.");
            return;
        }
        var prescription = restoration.Input is { } input
            ? await ReloadPrescriptionContextAsync(input, cancellationToken)
            : await OpenPrescriptionEntryCoreAsync(cancellationToken);
        if (!prescription.IsPrescriptionEntryPage)
            throw new InvalidOperationException(
                restoration.Input is null
                    ? "Reçete Giriş ekranı geri yüklenemedi."
                    : "Reçete doğru Medula sorgu ekranından güvenli biçimde geri yüklenemedi.");
        await ReplayRestorationStateAsync(restoration, cancellationToken);
        lastRestorationState = restoration;
        Report(MedulaConnectionState.Connected,
            restoration.Steps.Count == 0
                ? "Medula yeniden açıldı ve Reçete ekranı geri yüklendi."
                : "Medula yeniden açıldı ve son işlem geri yüklendi.");
    }

    private async Task RestoreAfterSessionExpiryCoreAsync(
        MedulaRestorationState restoration,
        CancellationToken cancellationToken)
    {
        if (!await ConnectWithRetriesAsync(cancellationToken))
            throw new InvalidOperationException("Medula oturumu yeniden açılamadı.");
        if (restoration.ElectronicPrescription is { } electronicPrescription)
        {
            await RestoreElectronicPrescriptionStateAsync(electronicPrescription, cancellationToken);
            if (electronicPrescription.PrescriptionOpen)
                await ReplayRestorationStateAsync(restoration, cancellationToken);
            lastRestorationState = restoration;
            Report(MedulaConnectionState.Connected,
                electronicPrescription.DetailOpen
                    ? "Medula oturumu yenilendi ve E-Reçete ekranı geri yüklendi."
                    : "Medula oturumu yenilendi ve E-Reçete Sorgu ekranı geri yüklendi.");
            return;
        }
        var prescription = restoration.Input is { } input
            ? await ReloadPrescriptionContextAsync(input, cancellationToken)
            : await OpenPrescriptionEntryCoreAsync(cancellationToken);
        if (!prescription.IsPrescriptionEntryPage)
            throw new InvalidOperationException(
                restoration.Input is null
                    ? "Reçete Giriş ekranı geri yüklenemedi."
                    : "Reçete doğru Medula sorgu ekranından güvenli biçimde geri yüklenemedi.");
        await ReplayRestorationStateAsync(restoration, cancellationToken);
        lastRestorationState = restoration;
        Report(MedulaConnectionState.Connected,
            restoration.Steps.Count == 0
                ? "Medula oturumu yenilendi ve Reçete ekranı geri yüklendi."
                : "Medula oturumu yenilendi ve son işlem geri yüklendi.");
    }

    private async Task<bool> TryResumeAuthenticatedHomeAsync(CancellationToken cancellationToken)
    {
        try
        {
            Report(MedulaConnectionState.Connecting, "Yeni tarayıcıda mevcut Medula oturumu aranıyor.");
            await NavigateAsync(HomeUrl, cancellationToken);
            if (!await WaitForPageStateAsync(HasPrescriptionEntryMenuAsync, cancellationToken))
                return false;
            Report(MedulaConnectionState.Connected, "Mevcut Medula oturumu yeni tarayıcıda sürdürüldü.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task RestoreElectronicPrescriptionStateAsync(
        MedulaElectronicPrescriptionRestoration state,
        CancellationToken cancellationToken)
    {
        await NavigateAsync(HomeUrl, cancellationToken);
        if (!await WaitForPageStateAsync(
                () => BrowserElementExistsAsync("form1:menuHtmlCommandExButton11"), cancellationToken))
            throw new InvalidOperationException("E-Reçete Sorgu menüsü geri yüklenemedi.");
        var opened = await ExecuteActionAndWaitAsync("""
            (() => {
                const action = document.getElementById('form1:menuHtmlCommandExButton11');
                if (!action) return { clicked: false, messages: [], confirmations: [] };
                action.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!opened.Clicked || !await WaitForPageStateAsync(
                () => BrowserElementExistsAsync("form1:buttonSorgula"), cancellationToken))
            throw new InvalidOperationException("E-Reçete Sorgu ekranı geri yüklenemedi.");

        if (string.IsNullOrWhiteSpace(state.PatientIdentityNumber)
            && string.IsNullOrWhiteSpace(state.ElectronicPrescriptionNumber)
            && string.IsNullOrWhiteSpace(state.TrackingNumber))
            return;

        var tcJson = JsonSerializer.Serialize(state.PatientIdentityNumber);
        var ePrescriptionJson = JsonSerializer.Serialize(state.ElectronicPrescriptionNumber);
        var trackingJson = JsonSerializer.Serialize(state.TrackingNumber);
        var dateJson = JsonSerializer.Serialize(state.PrescriptionDate);
        var actionIdJson = JsonSerializer.Serialize(
            state.SearchByTrackingNumber ? "form1:buttonSorgula2" : "form1:buttonSorgula");
        var queried = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const set = (id, value) => {
                    const field = document.getElementById(id);
                    if (!field) return;
                    field.value = value || '';
                    field.dispatchEvent(new Event('input', { bubbles: true }));
                    field.dispatchEvent(new Event('change', { bubbles: true }));
                };
                set('form1:textSmartDeger', {{tcJson}});
                set('form1:text4', {{ePrescriptionJson}});
                set('form1:text9', {{trackingJson}});
                set('form1:text7', {{dateJson}});
                const action = document.getElementById({{actionIdJson}});
                if (!action) return { clicked: false, messages: [], confirmations: [] };
                action.click();
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!queried.Clicked)
            throw new InvalidOperationException("E-Reçete sorgusu geri yüklenemedi.");
        await Task.Delay(200, cancellationToken);
        if (await IsSystemErrorPageAsync())
            throw new InvalidOperationException("E-Reçete sorgusu geri yüklenirken yeniden Sistem Hatası verdi.");
        if (!state.DetailOpen) return;

        var numberJson = JsonSerializer.Serialize(state.ElectronicPrescriptionNumber);
        var detailClick = await ExecuteActionAndWaitAsync($$"""
            (() => {
                const clean = value => (value ?? '').replace(/\s+/g, ' ').trim();
                const table = document.getElementById('form1:tableExERecete');
                const row = [...(table?.rows || [])].slice(1)
                    .find(candidate => clean(candidate.textContent).includes({{numberJson}}));
                if (!row) return { clicked: false, messages: [], confirmations: [] };
                const clickable = [row, ...row.querySelectorAll('*')]
                    .find(element => typeof element.onclick === 'function')
                    || row.querySelector('a,input[type=submit],input[type=image],button');
                if (clickable) clickable.click();
                else row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                return { clicked: true, messages: [], confirmations: [] };
            })();
            """, cancellationToken);
        if (!detailClick.Clicked || !await WaitForPageStateAsync(
                IsElectronicPrescriptionDetailOpenAsync, cancellationToken))
            throw new InvalidOperationException("E-Reçete detay ekranı geri yüklenemedi.");
        if (!state.PrescriptionOpen) return;

        var continueClick = await ClickElectronicPrescriptionCaptionAsync(
            "reçete kaydına devam", cancellationToken);
        if (!continueClick.Clicked || !await WaitForPageStateAsync(async () =>
            await BrowserElementExistsAsync("f:buttonReceteKaydet")
            || await BrowserElementExistsAsync("f:button2"), cancellationToken))
            throw new InvalidOperationException(
                "Kaydedilmemiş e-reçetenin Reçete ekranı geri yüklenemedi.");
    }

    private async Task ReplayRestorationStateAsync(
        MedulaRestorationState restoration,
        CancellationToken cancellationToken)
    {
        if (restoration.Input is not null && restoration.ApplyInput)
            await ApplyPrescriptionInputAsync(restoration.Input, cancellationToken);

        foreach (var step in restoration.Steps)
        {
            if (step.SelectedDrugRow is int rowIndex && !await SelectDrugRowAsync(rowIndex))
                throw new InvalidOperationException("Son ilaç satırı yeniden seçilemedi.");

            var actionIdJson = JsonSerializer.Serialize(step.ActionId);
            var valuesJson = JsonSerializer.Serialize(step.Payload.Values);
            var checksJson = JsonSerializer.Serialize(step.Payload.Checks);
            var click = await ExecuteActionAndWaitAsync($$"""
                (() => {
                    const values = {{valuesJson}};
                    const checks = {{checksJson}};
                    for (const [id, value] of Object.entries(values)) {
                        const element = document.getElementById(id);
                        if (element && !element.disabled && !element.readOnly) element.value = value ?? '';
                    }
                    for (const [id, checked] of Object.entries(checks)) {
                        const element = document.getElementById(id);
                        if (element && !element.disabled) element.checked = !!checked;
                    }
                    const action = document.getElementById({{actionIdJson}});
                    if (!action) return { clicked: false, messages: [], confirmations: [] };
                    window.alert = () => {};
                    window.confirm = () => false;
                    action.click();
                    return { clicked: true, messages: [], confirmations: [] };
                })();
                """, cancellationToken);
            if (!click.Clicked)
                throw new InvalidOperationException($"Son Medula ekranı denetimi bulunamadı: {step.ActionId}");
            await Task.Delay(250, cancellationToken);
            if (await IsSystemErrorPageAsync())
                throw new InvalidOperationException("Medula son ekranı geri yüklerken yeniden Sistem Hatası verdi.");
        }
    }

    private async Task RecreateBrowserAsync(CancellationToken cancellationToken)
    {
        DisposePrescriptionDocumentBrowser();
        prescriptionDocumentUsesPrimaryBrowser = false;
        var oldBrowser = browser;
        var oldWindow = browserWindow;
        suppressBrowserClosedReport = true;
        browser = null;
        browserWindow = null;
        try
        {
            if (oldBrowser?.CoreWebView2 is not null)
                oldBrowser.NavigationCompleted -= OnBrowserNavigationCompleted;
            if (oldWindow is not null)
            {
                oldWindow.Content = null;
                oldWindow.Close();
            }
            oldBrowser?.Dispose();
        }
        finally
        {
            suppressBrowserClosedReport = false;
        }

        browserUserDataFolderOverride = Path.Combine(
            pharmacyApp.Infrastructure.AppPaths.DataDirectory, "MedulaWebView2Recovery", Guid.NewGuid().ToString("N"));
        await Task.Delay(250, cancellationToken);
        await EnsureBrowserAsync(cancellationToken);
    }

    private void DisposePrescriptionDocumentBrowser()
    {
        if (prescriptionDocumentWindow is not null)
        {
            prescriptionDocumentWindow.Content = null;
            prescriptionDocumentWindow.Close();
        }
        prescriptionDocumentBrowser?.Dispose();
        prescriptionDocumentWindow = null;
        prescriptionDocumentBrowser = null;
    }

    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireMedulaOnline();
        if (browser?.CoreWebView2 is not null && browserWindow is not null) return;

        var userDataFolder = browserUserDataFolderOverride ?? Path.Combine(
            pharmacyApp.Infrastructure.AppPaths.DataDirectory,
            "MedulaWebView2");
        Directory.CreateDirectory(userDataFolder);
        browser = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder
            }
        };
        browserWindow = new Window
        {
            Title = "Medula Arka Plan Tarayıcısı",
            Width = 1280,
            Height = 900,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = browser
        };
        if (Application.Current.MainWindow is { } mainWindow && mainWindow.IsVisible)
            browserWindow.Owner = mainWindow;

        browserWindow.Closed += (_, _) =>
        {
            DisposePrescriptionDocumentBrowser();
            prescriptionDocumentUsesPrimaryBrowser = false;
            if (browser?.CoreWebView2 is not null)
                browser.NavigationCompleted -= OnBrowserNavigationCompleted;
            browser?.Dispose();
            browser = null;
            browserWindow = null;
            if (!suppressBrowserClosedReport
                && State is (MedulaConnectionState.Connected or MedulaConnectionState.Connecting))
                Report(MedulaConnectionState.Disconnected, "Medula arka plan tarayıcısı kapatıldı.");
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(MedulaConnectionState.Connecting, "Medula tarayıcı profili hazırlandı.");
            var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2InitializationCompletedEventArgs>? initializationHandler = null;
            initializationHandler = (_, args) =>
            {
                if (args.IsSuccess) initialization.TrySetResult();
                else initialization.TrySetException(
                    args.InitializationException
                    ?? new InvalidOperationException("The Medula browser engine could not be initialized."));
            };
            browser.CoreWebView2InitializationCompleted += initializationHandler;
            browserWindow.Show();
            Report(MedulaConnectionState.Connecting, "Medula tarayıcı penceresi hazırlandı.");
            try
            {
                browser.Source = new Uri("about:blank");
                await initialization.Task.WaitAsync(NavigationTimeout, cancellationToken);
            }
            finally
            {
                browser.CoreWebView2InitializationCompleted -= initializationHandler;
            }
            Report(MedulaConnectionState.Connecting, "Medula tarayıcı motoru başlatıldı.");
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled =
                !MedulaWarningBridge.SuppressNativeScriptDialogs;
            browser.NavigationCompleted += OnBrowserNavigationCompleted;
            browser.CoreWebView2.WebMessageReceived += OnBrowserWebMessageReceived;
            await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                MedulaWarningBridge.Script);
            browser.CoreWebView2.ScriptDialogOpening += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Message))
                    ReportBrowserMessage(args.Message);
                if (args.Kind == CoreWebView2ScriptDialogKind.Alert) args.Accept();
            };
            browserWindow.MinWidth = 800;
            browserWindow.MinHeight = 600;
            browserWindow.Width = 1180;
            browserWindow.Height = 820;
            browserWindow.WindowStyle = WindowStyle.SingleBorderWindow;
            browserWindow.ResizeMode = ResizeMode.CanResize;
            if (ApplicationSettingsStore.Current.MedulaBrowserDebug)
                ShowBrowserWindow();
            else
            {
                browserWindow.Hide();
                browserWindow.ShowInTaskbar = false;
                browserWindow.Opacity = 1;
            }
        }
        catch
        {
            browserWindow.Close();
            throw;
        }
    }

    private void OnBrowserWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var message = args.TryGetWebMessageAsString();
            if (message.StartsWith(WarningBridgePrefix, StringComparison.Ordinal))
            {
                var payload = message[WarningBridgePrefix.Length..];
                var separator = payload.IndexOf('\u001f');
                var severity = separator > 0 ? payload[..separator] : string.Empty;
                var medulaMessage = separator > 0 ? payload[(separator + 1)..] : payload;
                ReportBrowserMessage(medulaMessage, severity);
            }
        }
        catch (ArgumentException)
        {
            // Ignore non-string web messages; the Medula bridge sends only strings.
        }
    }

    private async Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireMedulaOnline();
        var currentBrowser = RequireBrowser();
        await WaitForNavigationAsync(
            () =>
            {
                currentBrowser.CoreWebView2.Navigate(url);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private async Task SubmitLoginAsync(
        string username,
        string password,
        string challengeAnswer,
        CancellationToken cancellationToken)
    {
        var currentBrowser = RequireBrowser();
        var usernameJson = JsonSerializer.Serialize(username);
        var passwordJson = JsonSerializer.Serialize(password);
        var challengeJson = JsonSerializer.Serialize(challengeAnswer);
        var script = $$"""
            (() => {
                const form = document.querySelector('form');
                const username = document.querySelector("input[id$=':text1']")
                    ?? document.querySelector("input[type='text']");
                const password = document.querySelector("input[id$=':secret1']")
                    ?? document.querySelector("input[type='password']");
                const challenge = document.querySelector("input[id$=':j_id_jsp_2072829783_5']")
                    ?? Array.from(document.querySelectorAll("input[type='text']")).find(input => input !== username);
                const consent = document.querySelector("input[id$=':kvkkTaahhut']")
                    ?? document.querySelector("input[type='checkbox']");
                const submit = document.querySelector("input[id$=':button1']")
                    ?? document.querySelector("input[type='submit']");
                if (!form || !username || !password || !challenge || !consent || !submit)
                    throw new Error('Medula login controls could not be located.');

                username.value = {{usernameJson}};
                password.value = {{passwordJson}};
                challenge.value = {{challengeJson}};
                consent.checked = true;
                for (const element of [username, password, challenge, consent]) {
                    element.dispatchEvent(new Event('input', { bubbles: true }));
                    element.dispatchEvent(new Event('change', { bubbles: true }));
                }
                submit.click();
                return true;
            })();
            """;

        try
        {
            await WaitForNavigationAsync(
                async () =>
                {
                    var result = await currentBrowser.ExecuteScriptAsync(script);
                    if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Medula login form was not submitted.");
                },
                cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "Medula giriş düğmesi çalıştırıldı ancak site 30 saniye içinde bir sayfa yanıtı vermedi.",
                exception);
        }
    }

    private async Task<string> CaptureChallengeImageAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
                const image = Array.from(document.images).find(item => item.src.includes('SayiUretenImageYeniServlet'));
                if (!image || !image.complete || image.naturalWidth === 0)
                    throw new Error('Medula challenge image is not available.');
                const canvas = document.createElement('canvas');
                canvas.width = image.naturalWidth;
                canvas.height = image.naturalHeight;
                canvas.getContext('2d').drawImage(image, 0, 0);
                return canvas.toDataURL('image/png');
            })();
            """;

        var encodedResult = await RequireBrowser().ExecuteScriptAsync(script);
        cancellationToken.ThrowIfCancellationRequested();
        var dataUrl = JsonSerializer.Deserialize<string>(encodedResult);
        const string prefix = "data:image/png;base64,";
        if (dataUrl is null || !dataUrl.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The Medula challenge image could not be captured.");

        var directory = Path.Combine(pharmacyApp.Infrastructure.AppPaths.DataDirectory, "medula");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, $"challenge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(dataUrl[prefix.Length..]), cancellationToken);
        return imagePath;
    }

    private async Task<PageProbe> ReadPageProbeAsync()
    {
        const string script = """
            (() => ({
                url: location.href,
                title: document.title,
                hasLoginForm: !!document.querySelector("input[id$=':secret1']"),
                hasKnownMedulaControls: !!(document.getElementById('f:buttonReceteKaydet')
                    || document.getElementById('f:button2')
                    || document.getElementById('form1:menuHtmlCommandExButton21')
                    || document.querySelector("input[id$=':secret1']")),
                text: document.body?.innerText ?? ''
            }))();
            """;
        var result = await RequireBrowser().ExecuteScriptAsync(script);
        var page = JsonSerializer.Deserialize<PageProbe>(result, JsonOptions)
            ?? throw new InvalidOperationException("The Medula response page could not be read.");
        return page;
    }

    private static string DescribeFailure(MedulaPageKind kind, PageProbe page, bool afterSubmission)
    {
        var path = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : page.Url;
        return kind switch
        {
            MedulaPageKind.LoginRejected =>
                "Medula kullanıcı adı, parola veya güvenlik anahtarını kabul etmedi. Yeniden deneyin.",
            MedulaPageKind.SmsVerification =>
                "Medula ek SMS doğrulaması istiyor. Arka plan tarayıcısını açıp doğrulamayı tamamlayın.",
            MedulaPageKind.SystemError =>
                "Medula Sistem Hatası sayfasını döndürdü; tarayıcı yeniden oluşturulacak.",
            MedulaPageKind.Login when afterSubmission =>
                $"Medula giriş formunu aldı ancak hata göstermeden giriş ekranını yeniden döndürdü ({path}). IP yönlendirmesini ve giriş bilgilerini kontrol edip yeniden deneyin.",
            _ => $"Medula giriş sayfası beklenen durumda değil ({path}). Arka plan tarayıcısını açıp sayfayı kontrol edin."
        };
    }

    private async Task WaitForNavigationAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var currentBrowser = RequireBrowser();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.IsSuccess) completion.TrySetResult();
            else if (args.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted)
            {
                // Medula replaces the initial navigation while redirecting between its
                // login entry and JSF page. Wait for the succeeding navigation event.
            }
            else completion.TrySetException(new InvalidOperationException($"Medula navigation failed: {args.WebErrorStatus}."));
        };

        currentBrowser.NavigationCompleted += handler;
        try
        {
            await action();
            await completion.Task.WaitAsync(NavigationTimeout, cancellationToken);
        }
        finally
        {
            currentBrowser.NavigationCompleted -= handler;
        }
    }

    private WebView2 RequireBrowser() =>
        browser?.CoreWebView2 is not null
            ? browser
            : throw new InvalidOperationException("The Medula background browser is not initialized.");

    private IDisposable EnterMessageScope(MedulaMessageScope scope)
    {
        lock (messageScopeSync)
        {
            var previous = messageScope;
            var revision = ++messageScopeRevision;
            messageScope = scope;
            return new MessageScopeLease(this, revision, previous);
        }
    }

    private void ExitMessageScope(long revision, MedulaMessageScope previous)
    {
        lock (messageScopeSync)
        {
            if (messageScopeRevision != revision) return;
            messageScope = previous;
        }
    }

    private MedulaMessageScope ReadMessageScope()
    {
        lock (messageScopeSync) return messageScope;
    }

    private void Report(MedulaConnectionState state, string message)
    {
        State = state;
        StatusChanged?.Invoke(this, new MedulaConnectionStatus(state, message, DateTimeOffset.Now));
    }

    private long GetWarningRevision()
    {
        lock (warningSync) return browserWarningRevision;
    }

    private string? GetWarningAfter(long revision)
    {
        lock (warningSync)
            return browserWarningRevision > revision
                && latestBrowserWarning.Length > 0
                && !MedulaBrowserWarning.IsPrescriptionDataStatus(latestBrowserWarning)
                ? latestBrowserWarning
                : null;
    }

    private void ReportWarning(string? warning)
    {
        warning = NormalizeBrowserMessage(warning);
        if (MedulaBrowserWarning.IsPrescriptionDataStatus(warning)) return;
        var now = DateTimeOffset.UtcNow;
        if (warning.Length > 0)
        {
            lock (warningSync)
            {
                if (warning.Equals(latestWarningEvent, StringComparison.Ordinal)
                    && now - latestWarningEventAt < TimeSpan.FromMilliseconds(75))
                    return;
                latestBrowserWarning = warning;
                browserWarningRevision++;
                latestWarningEvent = warning;
                latestWarningEventAt = now;
            }
        }
        else
        {
            lock (warningSync)
            {
                latestWarningEvent = string.Empty;
                latestWarningEventAt = DateTimeOffset.MinValue;
            }
        }
        WarningChanged?.Invoke(warning);
        ScopedWarningChanged?.Invoke(new MedulaScopedText(warning, ReadMessageScope()));
        if (warning.Length > 0)
            MessageChanged?.Invoke(new MedulaMessage(
                warning, MedulaMessageSeverity.Error, DateTimeOffset.Now));
    }

    private void ReportConfirmation(string confirmation)
    {
        confirmation = NormalizeBrowserMessage(confirmation);
        if (confirmation.Length == 0) return;
        var now = DateTimeOffset.UtcNow;
        if (confirmation.Equals(latestConfirmation, StringComparison.Ordinal)
            && now - latestConfirmationAt < TimeSpan.FromSeconds(5))
            return;
        latestConfirmation = confirmation;
        latestConfirmationAt = now;
        ConfirmationChanged?.Invoke(confirmation);
        ScopedConfirmationChanged?.Invoke(new MedulaScopedText(confirmation, ReadMessageScope()));
        MessageChanged?.Invoke(new MedulaMessage(
            confirmation, MedulaMessageSeverity.Success, DateTimeOffset.Now));
    }

    private void ReportNotice(string? notice)
    {
        notice = NormalizeBrowserMessage(notice);
        NoticeChanged?.Invoke(notice);
        ScopedNoticeChanged?.Invoke(new MedulaScopedText(notice, ReadMessageScope()));
        if (notice.Length > 0)
            MessageChanged?.Invoke(new MedulaMessage(
                notice, MedulaMessageSeverity.Notice, DateTimeOffset.Now));
    }

    private void ReportBrowserMessage(string message, string? severity = null)
    {
        message = NormalizeBrowserMessage(message);
        if (message.Length == 0 || MedulaBrowserWarning.IsPrescriptionDataStatus(message)) return;
        // Medula sometimes renders notices with the same red text style used by
        // validation errors. Message meaning is authoritative for known success
        // and notice phrases; visual severity remains the fallback.
        if (IsNoticeMessage(message)
            || severity?.Equals("notice", StringComparison.OrdinalIgnoreCase) == true)
            ReportNotice(message);
        else if (IsSuccessMessage(message)
            || severity?.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
            ReportConfirmation(message);
        else
            ReportWarning(message);
    }

    internal static string NormalizeBrowserMessage(string? message)
    {
        var normalized = Regex.Replace(message ?? string.Empty, @"\s+", " ").Trim();
        var documentValidation = Regex.Match(
            normalized,
            @"(Kaydetmeden önce doküman türünü seçiniz\.?|Gönderilen dosyanın uzantısı izin verilenler arasında yer almamaktadır\.\s*\(Eklenebilecek dosya uzantıları:\s*pdf,\s*jpg,\s*jpeg,\s*doc,\s*docx,\s*xls,\s*xlsx\))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (documentValidation.Success) return documentValidation.Groups[1].Value.Trim();
        var repeatedQr = Regex.Match(
            normalized,
            @"(?:^|\s)([0-9A-Z]{20,120}\s+Karekod\s+bilgisi\s+tekrar\s+okutulmuş\.?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return repeatedQr.Success ? repeatedQr.Groups[1].Value.Trim() : normalized;
    }

    private static bool IsNoticeMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("düşürüldü", StringComparison.OrdinalIgnoreCase)
            || message.Contains("düzenlendi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maksimum ilaç adet", StringComparison.OrdinalIgnoreCase)
            || message.Contains("otomatik", StringComparison.OrdinalIgnoreCase)
            || message.Contains("güncellendi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("değiştirildi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bilgilendirme", StringComparison.OrdinalIgnoreCase)
            || message.Contains("karekod bilgisi tekrar okutulmuş", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuccessMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("başarıyla", StringComparison.OrdinalIgnoreCase)
            || message.Contains("başarı ile", StringComparison.OrdinalIgnoreCase)
            || message.Contains("başarılı olarak", StringComparison.OrdinalIgnoreCase)
            || message.Contains("kaydedilmiştir", StringComparison.OrdinalIgnoreCase)
            || message.Contains("kaydedildi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("silinmiştir", StringComparison.OrdinalIgnoreCase)
            || message.Contains("silindi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("onaylanmıştır", StringComparison.OrdinalIgnoreCase)
            || message.Contains("onaylandı", StringComparison.OrdinalIgnoreCase)
            || message.Contains("işlem tamamlanmıştır", StringComparison.OrdinalIgnoreCase)
            || message.Contains("işlem tamamlandı", StringComparison.OrdinalIgnoreCase)
            || message.Contains("kayıt tamamlandı", StringComparison.OrdinalIgnoreCase));

    internal static bool IsTreatmentSchemeValidationFailure(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("bulunamadı", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("hata", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("geçersiz", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("giriniz", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("seçiniz", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("zorunlu", StringComparison.CurrentCultureIgnoreCase));

    private static string? FirstSuccessMessage(params string?[] messages)
    {
        return messages.FirstOrDefault(message =>
            !string.IsNullOrWhiteSpace(message)
            && message.Length <= 600
            && IsSuccessMessage(message))?.Trim();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Medula-Recete" : sanitized;
    }

    private static string UniqueDownloadPath(string directory, string baseName, string extension)
    {
        var path = Path.Combine(directory, baseName + extension);
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"{baseName}-{suffix}{extension}");
        return path;
    }

    private static void DeleteTemporaryImage(string imagePath)
    {
        try
        {
            File.Delete(imagePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PageProbe(
        string Url,
        string Title,
        bool HasLoginForm,
        bool HasKnownMedulaControls,
        string? Text);
    private sealed record ActionClickResult(bool Clicked, IReadOnlyList<string> Messages, IReadOnlyList<string> Confirmations);
    private sealed record PopupPageProbe(string Url, string Ready, string Text);
    private sealed record PrintRequestProbe(string Url, string Method, string Body);
    private sealed record PrescriptionPayload(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyDictionary<string, bool> Checks);
    private sealed record MedulaRestorationState(
        MedulaPrescriptionInput? Input,
        IReadOnlyList<MedulaRestorationStep> Steps,
        bool ApplyInput = true,
        MedulaElectronicPrescriptionRestoration? ElectronicPrescription = null)
    {
        public static MedulaRestorationState Prescription { get; } = new(null, []);
    }
    private sealed record MedulaElectronicPrescriptionRestoration(
        string PatientIdentityNumber,
        string ElectronicPrescriptionNumber,
        string TrackingNumber,
        string PrescriptionDate,
        bool SearchByTrackingNumber,
        bool DetailOpen,
        bool PrescriptionOpen = false);
    private sealed record MedulaRestorationStep(
        string ActionId,
        MedulaRemoteFieldPayload Payload,
        int? SelectedDrugRow);
    private sealed class MessageScopeLease(
        MedulaConnectionService owner,
        long revision,
        MedulaMessageScope previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.ExitMessageScope(revision, previous);
        }
    }

    private sealed record PrescriptionPageProbe(
        string Url,
        string Title,
        bool HasLoginForm,
        bool IsPrescriptionEntryPage,
        string BodyText,
        string? ErrorText,
        string? SuccessText,
        string? NoticeText,
        IReadOnlyDictionary<string, string> FieldValues,
        MedulaSavedPrescriptionSummary? SavedSummary);
    private sealed record ElectronicPrescriptionQueryProbe(
        bool IsOpen,
        string Url,
        string PatientIdentityNumber,
        string ElectronicPrescriptionNumber,
        string TrackingNumber,
        string PrescriptionDate,
        IReadOnlyList<MedulaElectronicPrescriptionListItem> Prescriptions);
    private sealed record ElectronicPrescriptionDetailProbe(
        bool IsOpen,
        string Title,
        string Url,
        string PatientName,
        IReadOnlyList<MedulaElectronicPrescriptionDetailField> PatientFields,
        IReadOnlyList<MedulaElectronicPrescriptionDetailField> PrescriptionFields,
        IReadOnlyList<MedulaElectronicPrescriptionDetailField> DoctorFields,
        IReadOnlyList<MedulaElectronicPrescriptionDrug> Drugs,
        IReadOnlyList<MedulaElectronicPrescriptionDiagnosis> Diagnoses,
        MedulaRemoteTable? ApprovalTable,
        string SignatureText,
        bool CanReturn,
        bool CanContinue)
    {
        public static ElectronicPrescriptionDetailProbe Empty { get; } = new(
            false, "E-Reçete", string.Empty, string.Empty, [], [], [], [], [],
            null, string.Empty, false, false);
    }
    private sealed record RemoteScreenProbe(
        bool IsOpen,
        string Url,
        IReadOnlyList<MedulaRemoteField> Fields,
        IReadOnlyList<MedulaRemoteAction> Actions,
        IReadOnlyList<MedulaRemoteTable> Tables,
        IReadOnlyList<MedulaTreatmentSchemeRow> TreatmentRows);
    private sealed record SubScreenDestinationProbe(
        string Url,
        IReadOnlyList<string> VisibleElementIds,
        IReadOnlyList<string> VisibleActionCaptions,
        string Warning);
    private sealed record SubScreenWaitResult(
        bool DestinationReached,
        string? Warning,
        bool SystemErrorDetected,
        bool SessionExpiredDetected);
    private sealed record TablePageProbe(string Caption, string Fingerprint);
    private sealed record FirstPageProbe(string ActionId, string Fingerprint);
    private sealed record DrugSearchPageProbe(
        int PageIndex,
        string Fingerprint,
        string NextActionId,
        IReadOnlyList<MedulaDrugSearchOption> Options);
    private sealed record DiagnosisSearchPageProbe(
        string TableId,
        string PagerId,
        int PageIndex,
        string Fingerprint,
        string NextActionId,
        IReadOnlyList<MedulaDiagnosisSearchOption> Options);
    private sealed record PrescriptionListPageProbe(
        bool IsReady,
        int PageNumber,
        int TotalPages,
        bool HasNextPage,
        IReadOnlyList<PrescriptionListRowProbe> Rows);
    private sealed record PrescriptionListRowProbe(
        int RowIndex,
        IReadOnlyList<string> Cells);
    private sealed record PatientPrescriptionQueryPageProbe(
        int PageNumber,
        bool HasNextPage,
        IReadOnlyList<PrescriptionListRowProbe> Rows);
}
