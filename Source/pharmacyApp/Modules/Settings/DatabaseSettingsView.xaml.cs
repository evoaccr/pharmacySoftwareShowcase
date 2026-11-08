using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.AI.Configuration;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Products;

namespace pharmacyApp.Modules.Settings;

public partial class DatabaseSettingsView : UserControl
{
    private readonly UserSessionRepository userRepository = new();
    private readonly ProductAnalysisRepository analysisRepository = new();
    private readonly AiConfigurationProvider aiConfigurationProvider = new();
    private readonly Dictionary<string, TextBox> tableNameBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool initialized;

    public DatabaseSettingsView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;

        var databaseSettings = DatabaseConnectionSettingsStore.Current;
        HostnameBox.Text = databaseSettings.Hostname;
        PortBox.Text = databaseSettings.Port.ToString();
        DatabaseNameBox.Text = databaseSettings.DatabaseName;
        UsernameBox.Text = databaseSettings.Username;
        PasswordEntry.Password = databaseSettings.Password;

        var appSettings = ApplicationSettingsStore.Current;
        var aiConfiguration = aiConfigurationProvider.GetCurrent();
        AiDebugModeToggle.IsChecked = aiConfiguration.DebugMode;
        AiDebugWorkflowQueueToggle.IsChecked = aiConfiguration.DebugShowWorkflowFunctions;
        PharmacyNameBox.Text = appSettings.PharmacyName;
        PharmacistNameBox.Text = appSettings.PharmacistName;
        AiUserDisplayNameBox.Text = appSettings.AiUserDisplayName;
        ShortcutsEnabledCheckBox.IsChecked = appSettings.ShortcutsEnabled;
        StartFullscreenCheckBox.IsChecked = appSettings.StartFullscreen;
        MedulaDebugCheckBox.IsChecked = appSettings.MedulaDebug;
        MedulaBrowserDebugCheckBox.IsChecked = appSettings.MedulaBrowserDebug;
        OfflinePrescriptionListCheckBox.IsChecked = appSettings.OfflinePrescriptionList;
        SkipElectronicPrescriptionViewCheckBox.IsChecked = appSettings.SkipElectronicPrescriptionView;
        SkipPrescriptionSaveConfirmationCheckBox.IsChecked = appSettings.SkipPrescriptionSaveConfirmation;
        HideUnimportantReportDetailsCheckBox.IsChecked = appSettings.HideUnimportantReportDetails;
        HideUnimportantPrescriptionDetailsCheckBox.IsChecked = appSettings.HideUnimportantPrescriptionDetails;
        AutomaticPrescriptionRecipientSelfCheckBox.IsChecked = appSettings.AutomaticPrescriptionRecipientSelf;
        NotificationDisplaySecondsBox.Text = appSettings.NotificationDisplaySeconds.ToString();
        LoyalPatientMonthlyFrequencyBox.Text = appSettings.LoyalPatientMinimumMonthlyFrequency
            .ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        ItsPharmacyGlnBox.Text = appSettings.ItsPharmacyGln;
        EczaciKartBrowserDebugCheckBox.IsChecked = appSettings.EczaciKartBrowserDebug;
        PopulateTableNames(appSettings.DatabaseTableNames);
        PopulateResolutions(appSettings);
        await PopulateIgnoredUsersAsync(appSettings.IgnoredUserId);
        await PopulateAnalysisSettingsAsync();
    }

    private async Task PopulateAnalysisSettingsAsync()
    {
        try
        {
            AnalysisDefaultMonthsBox.Text = (await analysisRepository.LoadDefaultWantedStockMonthsAsync()).ToString("0.0");
            AnalysisProductBox.ItemsSource = await analysisRepository.SearchProductsAsync(string.Empty);
            await RefreshAnalysisOverridesAsync();
        }
        catch (Exception exception)
        {
            ShowStatus($"Ürün analizi ayarları yüklenemedi: {exception.Message}", isError: true);
        }
    }

    private async Task RefreshAnalysisOverridesAsync()
    {
        AnalysisOverridesGrid.ItemsSource = await analysisRepository.LoadOverridesAsync();
    }

    private async void OnAnalysisProductSearchClicked(object sender, RoutedEventArgs e) =>
        await SearchAnalysisProductsAsync();

    private async void OnAnalysisProductSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAnalysisProductsAsync();
    }

    private async Task SearchAnalysisProductsAsync()
    {
        try
        {
            var products = await analysisRepository.SearchProductsAsync(AnalysisProductSearchBox.Text);
            AnalysisProductBox.ItemsSource = products;
            AnalysisProductBox.SelectedIndex = products.Count > 0 ? 0 : -1;
            if (products.Count == 0) ShowStatus("İlaç bulunamadı.", isError: true);
        }
        catch (Exception exception)
        {
            ShowStatus($"İlaçlar aranamadı: {exception.Message}", isError: true);
        }
    }

    private void OnAnalysisProductSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnalysisProductBox.SelectedItem is not ProductAnalysisSettingItem product) return;
        AnalysisProductMonthsBox.Text = (product.WantedStockMonths ??
            ParseWantedStockMonths(AnalysisDefaultMonthsBox.Text) ?? ProductAnalysisCalculations.DefaultWantedStockMonths).ToString("0.0");
    }

    private async void OnApplyAnalysisDefaultToAllClicked(object sender, RoutedEventArgs e)
    {
        var months = ParseWantedStockMonths(AnalysisDefaultMonthsBox.Text);
        if (months is null)
        {
            ShowStatus("Stok süresi 0,5 ile 60 ay arasında ve 0,5'in katı olmalıdır.", isError: true);
            return;
        }
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                "Bu değer tüm ilaçlara uygulanacak ve ilaca özel mevcut ayarlar kaldırılacaktır. Devam edilsin mi?",
                "Ürün Analizi",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            await analysisRepository.SetDefaultForAllAsync(months.Value);
            await RefreshAnalysisOverridesAsync();
            ShowStatus($"Varsayılan stok süresi tüm ilaçlar için {months.Value:0.0} ay olarak uygulandı.", isError: false);
        }
        catch (Exception exception)
        {
            ShowStatus($"Genel analiz ayarı kaydedilemedi: {exception.Message}", isError: true);
        }
    }

    private async void OnSaveAnalysisOverrideClicked(object sender, RoutedEventArgs e)
    {
        if (AnalysisProductBox.SelectedItem is not ProductAnalysisSettingItem product)
        {
            ShowStatus("Önce bir ilaç seçin.", isError: true);
            return;
        }
        var months = ParseWantedStockMonths(AnalysisProductMonthsBox.Text);
        if (months is null)
        {
            ShowStatus("Stok süresi 0,5 ile 60 ay arasında ve 0,5'in katı olmalıdır.", isError: true);
            return;
        }
        try
        {
            await analysisRepository.SaveOverrideAsync(product.RecordId, months.Value);
            await RefreshAnalysisOverridesAsync();
            await SearchAnalysisProductsAsync();
            ShowStatus($"{product.DisplayName} için stok süresi {months.Value:0.0} ay olarak kaydedildi.", isError: false);
        }
        catch (Exception exception)
        {
            ShowStatus($"İlaca özel analiz ayarı kaydedilemedi: {exception.Message}", isError: true);
        }
    }

    private async void OnRemoveAnalysisOverrideClicked(object sender, RoutedEventArgs e)
    {
        if (AnalysisProductBox.SelectedItem is not ProductAnalysisSettingItem product)
        {
            ShowStatus("Önce bir ilaç seçin.", isError: true);
            return;
        }
        try
        {
            await analysisRepository.RemoveOverrideAsync(product.RecordId);
            AnalysisProductMonthsBox.Text = AnalysisDefaultMonthsBox.Text;
            await RefreshAnalysisOverridesAsync();
            await SearchAnalysisProductsAsync();
            ShowStatus($"{product.DisplayName} için özel ayar kaldırıldı; genel ayar kullanılacak.", isError: false);
        }
        catch (Exception exception)
        {
            ShowStatus($"İlaca özel analiz ayarı kaldırılamadı: {exception.Message}", isError: true);
        }
    }

    private void OnAnalysisOverrideGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnalysisOverridesGrid.SelectedItem is not ProductAnalysisSettingItem product) return;
        AnalysisProductBox.ItemsSource = new[] { product };
        AnalysisProductBox.SelectedItem = product;
        AnalysisProductMonthsBox.Text = product.WantedStockMonths?.ToString("0.0") ?? AnalysisDefaultMonthsBox.Text;
    }

    private static decimal? ParseWantedStockMonths(string? text)
    {
        if (!decimal.TryParse(
                text?.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)) return null;
        return ProductAnalysisCalculations.IsValidWantedStockMonths(value) ? value : null;
    }

    private void PopulateResolutions(ApplicationSettings settings)
    {
        var primary = DisplayMetrics.PrimaryResolution;
        var resolutions = new[]
        {
            primary,
            new DisplayResolution(1280, 720),
            new DisplayResolution(1366, 768),
            new DisplayResolution(1600, 900),
            new DisplayResolution(1920, 1080),
            new DisplayResolution(2560, 1440),
            new DisplayResolution(3840, 2160)
        }.Distinct().OrderBy(item => item.Width).ThenBy(item => item.Height).Select(item => item.ToString()).ToArray();
        ResolutionBox.ItemsSource = resolutions;
        ResolutionBox.Text = new DisplayResolution(settings.ScreenWidth, settings.ScreenHeight).ToString();
    }

    private async Task PopulateIgnoredUsersAsync(string? selectedUserId)
    {
        try
        {
            var users = await userRepository.LoadActiveUsersAsync();
            var options = new List<IgnoreUserOption> { new(null, "Hiçbiri") };
            options.AddRange(users.Select(user => new IgnoreUserOption(user.UserId, user.DisplayName)));
            IgnoredUserBox.ItemsSource = options;
            IgnoredUserBox.SelectedItem = options.FirstOrDefault(option => option.UserId == selectedUserId) ?? options[0];
        }
        catch (Exception exception)
        {
            var fallback = new List<IgnoreUserOption> { new(null, "Hiçbiri") };
            if (selectedUserId is not null) fallback.Add(new(selectedUserId, $"{selectedUserId} — mevcut seçim"));
            IgnoredUserBox.ItemsSource = fallback;
            IgnoredUserBox.SelectedItem = fallback.Last();
            ShowStatus($"Kullanıcı listesi yüklenemedi: {exception.Message}", isError: true);
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowStatus("Port 1 ile 65535 arasında bir tam sayı olmalıdır.", isError: true);
            return;
        }
        if (!DisplayResolution.TryParse(ResolutionBox.Text, out var resolution))
        {
            ShowStatus("Çözünürlüğü genişlik x yükseklik biçiminde girin (ör. 1920 x 1080).", isError: true);
            return;
        }
        if (!int.TryParse(NotificationDisplaySecondsBox.Text.Trim(), out var notificationSeconds)
            || notificationSeconds is < 1 or > 120)
        {
            ShowStatus("Bildirim görünme süresi 1 ile 120 saniye arasında olmalıdır.", isError: true);
            return;
        }
        if (!decimal.TryParse(
                LoyalPatientMonthlyFrequencyBox.Text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var loyalPatientFrequency)
            || loyalPatientFrequency is < 0.1m or > 12m)
        {
            ShowStatus("Sadık hasta reçete sıklığı 0,1 ile 12 arasında olmalıdır.", isError: true);
            return;
        }
        var itsPharmacyGln = ItsPharmacyGlnBox.Text.Trim();
        if (itsPharmacyGln.Length > 0 && !Gs1IdentifierValidator.IsValidGln13(itsPharmacyGln))
        {
            ShowStatus("Eczane GLN kodu 13 haneli ve geçerli bir kontrol basamağına sahip olmalıdır.", isError: true);
            return;
        }
        var enteredTableNames = tableNameBoxes.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
        if (!DatabaseTableNameCatalog.TryValidate(enteredTableNames, out var tableNames, out var tableNameError))
        {
            ShowStatus(tableNameError, isError: true);
            return;
        }

        try
        {
            DatabaseConnectionSettingsStore.Save(new DatabaseConnectionSettings(
                HostnameBox.Text.Trim(),
                port,
                DatabaseNameBox.Text.Trim(),
                UsernameBox.Text.Trim(),
                PasswordEntry.Password,
                DatabaseConnectionSettingsStore.Current.SslMode,
                DatabaseConnectionSettingsStore.Current.AllowSchemaChanges));

            var ignoredUserId = (IgnoredUserBox.SelectedItem as IgnoreUserOption)?.UserId;
            ApplicationSettingsStore.Save(new ApplicationSettings
            {
                ShortcutsEnabled = ShortcutsEnabledCheckBox.IsChecked == true,
                StartFullscreen = StartFullscreenCheckBox.IsChecked == true,
                ScreenWidth = resolution.Width,
                ScreenHeight = resolution.Height,
                PharmacyName = PharmacyNameBox.Text.Trim(),
                PharmacistName = PharmacistNameBox.Text.Trim(),
                AiUserDisplayName = AiUserDisplayNameBox.Text.Trim(),
                IgnoredUserId = ignoredUserId,
                MedulaUsername = string.Empty,
                MedulaPassword = string.Empty,
                ItsUsername = string.Empty,
                ItsPassword = string.Empty,
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
                AlliancePassword = string.Empty,
                MedulaDebug = MedulaDebugCheckBox.IsChecked == true,
                MedulaBrowserDebug = MedulaBrowserDebugCheckBox.IsChecked == true,
                OfflinePrescriptionList = OfflinePrescriptionListCheckBox.IsChecked == true,
                SkipElectronicPrescriptionView = SkipElectronicPrescriptionViewCheckBox.IsChecked == true,
                SkipPrescriptionSaveConfirmation = SkipPrescriptionSaveConfirmationCheckBox.IsChecked == true,
                HideUnimportantReportDetails = HideUnimportantReportDetailsCheckBox.IsChecked == true,
                HideUnimportantPrescriptionDetails = HideUnimportantPrescriptionDetailsCheckBox.IsChecked == true,
                AutomaticPrescriptionRecipientSelf = AutomaticPrescriptionRecipientSelfCheckBox.IsChecked == true,
                NotificationDisplaySeconds = notificationSeconds,
                LoyalPatientMinimumMonthlyFrequency = loyalPatientFrequency,
                LoyalPatientMinimumDistinctVisitDays = ApplicationSettingsStore.Current.LoyalPatientMinimumDistinctVisitDays,
                LoyalPatientMinimumObservationIntervals = ApplicationSettingsStore.Current.LoyalPatientMinimumObservationIntervals,
                LoyalPatientFrequencyTolerancePercent = ApplicationSettingsStore.Current.LoyalPatientFrequencyTolerancePercent,
                LoyalPatientRecentVisitGraceIntervals = ApplicationSettingsStore.Current.LoyalPatientRecentVisitGraceIntervals,
                LoyalPatientReportSampleSize = ApplicationSettingsStore.Current.LoyalPatientReportSampleSize,
                LoyalPatientReportSupplyThresholdPercent = ApplicationSettingsStore.Current.LoyalPatientReportSupplyThresholdPercent,
                LoyalPatientReportQueryDelaySeconds = ApplicationSettingsStore.Current.LoyalPatientReportQueryDelaySeconds,
                LoyalPatientReportQueryJitterSeconds = ApplicationSettingsStore.Current.LoyalPatientReportQueryJitterSeconds,
                DatabaseTableNames = tableNames,
                ItsPharmacyGln = itsPharmacyGln,
                EczaciKartBrowserDebug = EczaciKartBrowserDebugCheckBox.IsChecked == true,
            });
            aiConfigurationProvider.Save(aiConfigurationProvider.GetCurrent() with
            {
                DebugMode = AiDebugModeToggle.IsChecked == true,
                DebugShowWorkflowFunctions = AiDebugWorkflowQueueToggle.IsChecked == true
            });
            ShowStatus("Ayarlar kaydedildi.", isError: false);
        }
        catch (ArgumentException exception)
        {
            ShowStatus(exception.Message, isError: true);
        }
        catch (Exception exception)
        {
            ShowStatus($"Ayarlar kaydedilemedi: {exception.Message}", isError: true);
        }
    }

    private void OnToggleTableNamesClicked(object sender, RoutedEventArgs e)
    {
        TableNamesPanel.Visibility = TableNamesPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        ToggleTableNamesButton.Content = TableNamesPanel.Visibility == Visibility.Visible
            ? "Tabloları Gizle"
            : "Tabloları Değiştir";
    }

    private void PopulateTableNames(IReadOnlyDictionary<string, string> configured)
    {
        if (tableNameBoxes.Count > 0) return;
        var normalized = DatabaseTableNameCatalog.Normalize(configured);
        foreach (var defaultName in DatabaseTableNameCatalog.Defaults)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = defaultName,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(64, 81, 92))
            };
            var box = new TextBox { Text = normalized[defaultName], MaxLength = 63 };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(box, 2);
            row.Children.Add(label);
            row.Children.Add(box);
            TableNameRowsPanel.Children.Add(row);
            tableNameBoxes[defaultName] = box;
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(180, 52, 52)
            : Color.FromRgb(35, 122, 87));
        StatusText.Visibility = Visibility.Visible;
    }

    private sealed record IgnoreUserOption(string? UserId, string DisplayName);
}
