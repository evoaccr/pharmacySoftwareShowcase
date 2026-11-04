using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace pharmacyApp.Modules.Cards;

public partial class PatientDebtHistoryView : UserControl
{
    private const int PageSize = 300;
    private readonly PatientDebtHistoryRepository repository = new();
    private readonly ObservableCollection<PatientDebtHistoryRow> rows = [];
    private readonly LookupEditorControl patientLookup = new();
    private readonly int? initialPatientId;
    private CancellationTokenSource? loadCancellation;
    private int? selectedPatientId;
    private int totalCount;
    private int loadVersion;
    private bool initialized;
    private bool isLoading;

    public PatientDebtHistoryView(int? patientId = null, string? patientName = null)
    {
        initialPatientId = patientId;
        InitializeComponent();
        patientLookup.Height = 30;
        patientLookup.SearchRequested = term => repository.SearchPatientsAsync(term, CancellationToken.None);
        patientLookup.ValueChanged += OnPatientSelectionChanged;
        PatientLookupHost.Children.Add(patientLookup);
        HistoryGrid.ItemsSource = rows;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        if (initialPatientId is int patientId)
        {
            var option = await repository.LoadPatientAsync(patientId, CancellationToken.None);
            patientLookup.SetSelectedOption(option, patientId.ToString(CultureInfo.InvariantCulture));
            selectedPatientId = option is null ? null : patientId;
        }
        initialized = true;
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => loadCancellation?.Cancel();

    private async void OnPatientSelectionChanged(object? sender, EventArgs e)
    {
        if (!initialized) return;
        if (int.TryParse(patientLookup.SelectedKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var patientId))
        {
            if (selectedPatientId == patientId) return;
            selectedPatientId = patientId;
            await ReloadAsync();
        }
        else if (string.IsNullOrWhiteSpace(patientLookup.Text) && selectedPatientId is not null)
        {
            selectedPatientId = null;
            await ReloadAsync();
        }
    }

    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        patientLookup.SetSelectedOption(null);
        selectedPatientId = null;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var version = ++loadVersion;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        isLoading = true;
        ShowStatus("Borç geçmişi yükleniyor...");
        try
        {
            var page = await repository.LoadPageAsync(selectedPatientId, 0, PageSize, loadCancellation.Token);
            if (version != loadVersion) return;
            rows.Clear();
            foreach (var row in page.Rows) rows.Add(row);
            totalCount = page.TotalCount;
            CountText.Text = $"Toplam Kayıt: {totalCount:N0}";
            StatusOverlay.Visibility = totalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (totalCount == 0) ShowStatus(selectedPatientId is null
                ? "Henüz borç hareketi bulunmuyor."
                : "Seçilen hastaya ait borç hareketi bulunmuyor.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != loadVersion) return;
            rows.Clear();
            totalCount = 0;
            CountText.Text = "Toplam Kayıt: 0";
            ShowStatus($"Borç geçmişi yüklenemedi: {ex.Message}");
        }
        finally
        {
            if (version == loadVersion) isLoading = false;
        }
    }

    private async Task LoadMoreAsync()
    {
        if (isLoading || rows.Count >= totalCount) return;
        var version = loadVersion;
        isLoading = true;
        try
        {
            var page = await repository.LoadPageAsync(selectedPatientId, rows.Count, PageSize,
                loadCancellation?.Token ?? CancellationToken.None);
            if (version != loadVersion) return;
            foreach (var row in page.Rows) rows.Add(row);
            if (page.Rows.Count > 0) totalCount = page.TotalCount;
            CountText.Text = $"Toplam Kayıt: {totalCount:N0}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowStatus($"Ek borç hareketleri yüklenemedi: {ex.Message}"); }
        finally { if (version == loadVersion) isLoading = false; }
    }

    private async void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || viewer.ScrollableHeight <= 0) return;
        if (viewer.VerticalOffset >= viewer.ScrollableHeight * 0.75) await LoadMoreAsync();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusOverlay.Visibility = Visibility.Visible;
    }
}
