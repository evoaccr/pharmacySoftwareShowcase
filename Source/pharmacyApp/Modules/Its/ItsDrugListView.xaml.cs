using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules;

namespace pharmacyApp.Modules.Its;

public partial class ItsDrugListView : UserControl, IModuleBackgroundWorkSource
{
    private readonly IItsReferenceDataClient referenceDataClient;
    private IReadOnlyList<ItsDrugListRow> allRows = [];
    private CancellationTokenSource? operationCancellation;

    public bool IsBackgroundWorkRunning => operationCancellation is not null;
    public event Action<bool>? BackgroundWorkStateChanged;

    public ItsDrugListView() : this(new ItsApiClient())
    {
    }

    internal ItsDrugListView(IItsReferenceDataClient referenceDataClient)
    {
        this.referenceDataClient = referenceDataClient;
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        EnvironmentBox.ItemsSource = new[]
        {
            new ItsEnvironmentOption(ItsEnvironment.Production, "İTS Gerçek Sistem"),
            new ItsEnvironmentOption(ItsEnvironment.Test, "İTS Test Sistemi")
        };
        EnvironmentBox.SelectedIndex = 0;
        StatusText.Text = "Listeyi getirmek için İTS ortamını seçin.";
    }

    private ItsEnvironment SelectedEnvironment =>
        (EnvironmentBox.SelectedItem as ItsEnvironmentOption)?.Value ?? ItsEnvironment.Production;

    private async void OnLoadClicked(object sender, RoutedEventArgs e)
    {
        if (operationCancellation is not null) return;
        var settings = ApplicationSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.ItsUsername) || string.IsNullOrWhiteSpace(settings.ItsPassword))
        {
            SetStatus("İTS kullanıcı adı ve parolası Ayarlar > İTS Ayarları bölümünde yapılandırılmalıdır.", false);
            return;
        }

        operationCancellation = new CancellationTokenSource();
        SetRunning(true);
        try
        {
            SetStatus("İTS ilaç listesi getiriliyor…", null);
            var drugs = await referenceDataClient.GetDrugsAsync(
                SelectedEnvironment,
                new ItsCredentials(settings.ItsUsername, settings.ItsPassword),
                IncludeInactiveBox.IsChecked == true,
                operationCancellation.Token);
            allRows = drugs.Select(drug => new ItsDrugListRow(
                drug.Gtin,
                drug.DrugName,
                drug.ManufacturerGln,
                drug.ManufacturerName,
                drug.Active ? "Aktif" : "Pasif",
                drug.Imported ? "İthal" : "Yerli")).ToArray();
            ApplyFilter();
            SetStatus($"İTS ilaç listesi {DateTime.Now:dd.MM.yyyy HH:mm:ss} tarihinde getirildi.", true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("İlaç listesi sorgusu iptal edildi.", false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            SetStatus($"İTS ilaç listesi getirilemedi: {exception.Message}", false);
        }
        finally
        {
            operationCancellation.Dispose();
            operationCancellation = null;
            SetRunning(false);
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Sorgu iptal ediliyor…";
        operationCancellation?.Cancel();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();
        var visible = search.Length == 0
            ? allRows
            : allRows.Where(row =>
                    row.Gtin.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    row.DrugName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    row.ManufacturerGln.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    row.ManufacturerName.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        DrugGrid.ItemsSource = visible;
        CountText.Text = allRows.Count == 0
            ? "0 ilaç"
            : $"{visible.Count:N0} / {allRows.Count:N0} ilaç";
    }

    private void SetRunning(bool running)
    {
        LoadButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = running;
        EnvironmentBox.IsEnabled = !running;
        IncludeInactiveBox.IsEnabled = !running;
        BackgroundWorkStateChanged?.Invoke(running);
    }

    private void SetStatus(string text, bool? success)
    {
        StatusText.Text = text;
        StatusText.Foreground = success switch
        {
            true => new SolidColorBrush(Color.FromRgb(22, 125, 78)),
            false => new SolidColorBrush(Color.FromRgb(179, 58, 50)),
            _ => (Brush)FindResource("MutedInk")
        };
    }

    private sealed record ItsDrugListRow(
        string Gtin,
        string DrugName,
        string ManufacturerGln,
        string ManufacturerName,
        string ActiveDisplay,
        string ImportedDisplay);
}
