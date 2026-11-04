using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pharmacyApp.Modules.Medula;

public partial class MedulaConnectionTestView : UserControl
{
    private readonly MedulaAutomation connection = MedulaAutomation.Instance;
    private readonly List<string> logEntries = [];
    private bool subscribed;

    public MedulaConnectionTestView()
    {
        InitializeComponent();
        UpdateState(connection.State);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (subscribed) return;
        connection.StatusChanged += ConnectionStatusChanged;
        subscribed = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed) return;
        connection.StatusChanged -= ConnectionStatusChanged;
        subscribed = false;
    }

    private async void StartClick(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void RetryClick(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void RefreshClick(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async Task ConnectAsync()
    {
        SetButtonsEnabled(false);
        try
        {
            await connection.OpenWebBrowserAsync();
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void ShowBrowserClick(object sender, RoutedEventArgs e) => connection.ShowBrowser();

    private void ConnectionStatusChanged(object? sender, MedulaConnectionStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ConnectionStatusChanged(sender, status));
            return;
        }

        logEntries.Add($"{status.Timestamp:HH:mm:ss}  {status.Message}");
        StatusLogText.Text = string.Join(Environment.NewLine, logEntries);
        UpdateState(status.State);
    }

    private void UpdateState(MedulaConnectionState state)
    {
        (ConnectionStateText.Text, ConnectionStateText.Foreground) = state switch
        {
            MedulaConnectionState.Connecting => ("Bağlanıyor", new SolidColorBrush(Color.FromRgb(173, 105, 16))),
            MedulaConnectionState.Connected => ("Bağlandı", new SolidColorBrush(Color.FromRgb(35, 122, 87))),
            MedulaConnectionState.TemporarilyOffline => ("Geçici çevrimdışı", new SolidColorBrush(Color.FromRgb(210, 139, 44))),
            MedulaConnectionState.Failed => ("Bağlantı başarısız", new SolidColorBrush(Color.FromRgb(180, 52, 52))),
            _ => ("Bağlı değil", new SolidColorBrush(Color.FromRgb(105, 115, 134)))
        };
        RetryButton.Visibility = state == MedulaConnectionState.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        RetryButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
    }
}
