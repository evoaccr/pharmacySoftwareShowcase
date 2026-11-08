using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using pharmacyApp.AI.Configuration;

namespace pharmacyApp.Infrastructure;

/// <summary>One frontend action per routed command, never per PostgreSQL command.</summary>
public static class FrontendTelemetry
{
    private static readonly string Session = Guid.NewGuid().ToString();
    public static string SessionId => Session;
    private static readonly Channel<object> Pending = Channel.CreateBounded<object>(new BoundedChannelOptions(512)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private static bool initialized;
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(Click), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler(Click), true);
        _ = Task.Run(Worker);
        Report("app_started", "application");
    }
    private static void Click(object sender, RoutedEventArgs args)
    {
        try
        {
        if (!ReferenceEquals(sender,args.Source) || sender is not FrameworkElement element) return;
        string screen = element.GetType().Name;
        DependencyObject? parent = element;
        while (parent is not null)
        {
            if (parent is UserControl control) { screen = control.GetType().Name; break; }
            parent = VisualTreeHelper.GetParent(parent);
        }
        string action = element.Name.Length > 0 ? element.Name : element.GetType().Name;
        if (element is MenuItem { Tag: string tag } && System.Text.RegularExpressions.Regex.IsMatch(tag,@"^[a-z_]+\.[a-z_.-]+$"))
        { action = "open_" + tag; screen = tag; }
        Report(action,screen);
        }
        catch { /* Observing a control must never interfere with its actual handler. */ }
    }
    private static void Report(string action,string screen)
    {
        try { Pending.Writer.TryWrite(new { eventId = Guid.NewGuid().ToString(), sessionId = Session,
            database = DatabaseConnectionSettingsStore.Current.DatabaseName, application = "Pharmacy",
            action,screen,status = "reported" }); }
        catch { /* Telemetry never changes application behavior. */ }
    }
    private static async Task Worker()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await foreach (var value in Pending.Reader.ReadAllAsync())
        {
            try
            {
                var ai = AppConfiguration.ReadSection<AiConfiguration>("Ai");
                var secrets = AppConfiguration.ReadSection<AiSecrets>("Secrets");
                if (string.IsNullOrWhiteSpace(ai.GatewayEndpoint) || string.IsNullOrWhiteSpace(secrets.GatewayToken)) continue;
                var endpoint = new Uri(new Uri(ai.GatewayEndpoint), "/v1/frontend/actions");
                for (int attempt=0;attempt<3;attempt++)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post,endpoint)
                            { Content = new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json") };
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",secrets.GatewayToken);
                        using var response = await client.SendAsync(request);
                        if (response.IsSuccessStatusCode || (int)response.StatusCode is 400 or 401 or 403) break;
                    }
                    catch { }
                    if (attempt < 2) await Task.Delay(500);
                }
            }
            catch { }
        }
    }
}
