using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Home;

internal static class RestrictedPopupVerification
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/pharmacyApp;component/Themes/Workbench.xaml", UriKind.Relative) });
        app.Resources["SubtleDataGridRowStyle"] = new Style(typeof(DataGridRow));
        var shell = new pharmacyApp.MainWindow();
        var menu = (Menu)shell.FindName("TopMenuBar");
        var leaves = menu.Items.Cast<MenuItem>().SelectMany(root => Leaves(root.ContextMenu!.Items)).ToArray();
        foreach (var id in new[] { "medula.prescription-entry", "debug.selcuk-test" })
        {
            var button = leaves.Single(item => item.Tag as string == id);
            ExpectDialog(app, () => button.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), id);
        }
        var home = new HomeScreenView(() => new pharmacyApp.AI.Models.AiApplicationContext());
        var legacy = (Button)home.FindName("LegacyAiButton");
        ExpectDialog(app, () => legacy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "Legacy AI");
        var diagnostics = (Button)home.FindName("DiagnosticsButton");
        diagnostics.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var popup = app.Windows.Cast<Window>().Single(w => w.Title == "AI Diagnostics");
        var content = ((TextBox)popup.Content).Text;
        if (!content.Contains("Status:") || !content.Contains("Workflows:") || content.Contains("Durum:")) throw new InvalidOperationException("Diagnostics are not English");
        popup.Close(); shell.Close();
        Console.WriteLine("PASS Actual AI diagnostics popup uses English labels");
        return 0;
    }

    private static void ExpectDialog(Application app, Action click, string name)
    {
        var seen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "Restricted Feature");
            if (dialog?.Content is not Grid body) return;
            seen = body.Children.OfType<TextBox>().Single().Text == OnlineAccessPolicy.RestrictedMessage;
            timer.Stop(); dialog.Close();
        };
        timer.Start(); click(); timer.Stop();
        if (!seen) throw new InvalidOperationException("Missing or incorrect restriction dialog: " + name);
        Console.WriteLine("PASS Actual restricted click shows the exact requested popup: " + name);
    }

    private static IEnumerable<MenuItem> Leaves(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
            if (item.Items.Count == 0) yield return item;
            else foreach (var child in Leaves(item.Items)) yield return child;
    }
}
