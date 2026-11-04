using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace pharmacyApp.Modules.Invoices;

public partial class InvoiceAddressListView : UserControl
{
    private readonly InvoiceAddressRepository repository = new();
    private CancellationTokenSource? cancellation;
    private bool initialized;
    public ObservableCollection<InvoiceAddressRow> Addresses { get; } = [];

    public InvoiceAddressListView()
    {
        InitializeComponent();
        RelatedRecordMenus.Attach(AddressGrid);
        DataContext = this;
        IdentityFilterBox.ItemsSource = new[]
        {
            new FilterOption("all", "Tümü"), new FilterOption("identity", "VKN/TCKN bulunan"),
            new FilterOption("gln", "GLN bulunan"), new FilterOption("both", "Her ikisi bulunan"),
            new FilterOption("missing", "Kimliği eksik")
        };
        IdentityFilterBox.SelectedIndex = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        initialized = false;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized) await ReloadAsync();
    }
    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref cancellation, next);
        previous?.Cancel();
        previous?.Dispose();
        RefreshButton.IsEnabled = false;
        BusyLayer.Visibility = Visibility.Visible;
        StatusText.Text = string.Empty;
        try
        {
            var filter = (IdentityFilterBox.SelectedItem as FilterOption)?.Key ?? "all";
            var rows = await repository.LoadAsync(SearchBox.Text, filter, next.Token);
            if (next.IsCancellationRequested) return;
            Addresses.Clear();
            foreach (var row in rows) Addresses.Add(row);
            CountText.Text = $"{rows.Count:N0} adres";
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested) { }
        catch (Exception exception) { StatusText.Text = $"Adresler yüklenemedi: {exception.Message}"; }
        finally
        {
            if (ReferenceEquals(cancellation, next))
            {
                cancellation = null;
                RefreshButton.IsEnabled = true;
                BusyLayer.Visibility = Visibility.Collapsed;
                next.Dispose();
            }
        }
    }

    private sealed record FilterOption(string Key, string DisplayName);
}
