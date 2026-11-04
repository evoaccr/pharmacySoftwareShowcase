using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using pharmacyApp.Infrastructure;
using pharmacyApp.Localization;
using pharmacyApp.Modules.Medula;
using pharmacyApp.Modules.Products;

internal static class EditionVerification
{
    public static int Run()
    {
        var app = new pharmacyApp.App(); app.InitializeComponent();
        var shell = new pharmacyApp.MainWindow();
        var menu = (Menu)shell.FindName("TopMenuBar");
        var debug = menu.Items.Cast<MenuItem>().Single(i => i.Header?.ToString() == "Debug");
        Check(debug.IsEnabled, "Debug dropdown can be opened");
        foreach (var item in Leaves(debug.ContextMenu!.Items).Where(i => i.Tag is string id && !OnlineAccessPolicy.IsModuleEnabled(id)))
            Check(item.Foreground is SolidColorBrush { Color: var color } && color == Colors.Gray && item.Opacity < 1,
                "Restricted menu remains visibly grey: " + item.Tag);
        var medula = menu.Items.Cast<MenuItem>().Single(i => i.Header?.ToString() == "Medula");
        var local = Leaves(medula.ContextMenu!.Items).Single(i => i.Tag as string == "medula.prescription-list");
        var label = (TextBlock)local.Header;
        Check(label.Inlines.FirstInline is Run { Text: "MIT", Foreground: SolidColorBrush { Color: var red } } && red == Colors.Red,
            "Local prescription menu has a red MIT prefix");
        Check(OnlineAccessPolicy.RestrictedMessage == "This feature cannot be opened in the safe portfolio version as it connects to government services or pharmacy depots. These screens' functions can be seen from the attached images.", "Restriction message matches the requested text exactly");
        Check(MedulaStatusText.For(MedulaConnectionState.Disconnected) == "Offline" && MedulaStatusText.For(MedulaConnectionState.TemporarilyOffline) == "Temporary offline", "Connection labels contain status only");
        foreach (var stock in new[] { false, true })
        {
            var view = new ProductCatalogView(new ProductCatalogLaunchOptions(OnlyInStockOnLaunch: stock));
            Check(((CheckBox)view.FindName("OnlyWithMovementsToggle")).IsChecked == true, "Movement filter defaults on: " + (stock ? "stock" : "catalog"));
        }
        using var connection = DatabaseConnectionSettingsStore.CreateConnection(); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select branch_name from branches order by branch_id";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) Check(UiText.TranslateSpecialty(reader.GetString(0)) != reader.GetString(0), "Specialty translates: " + reader.GetString(0));
        foreach (var (table, column) in new[] { ("prescription","saved_at"), ("sales","timestamp"), ("qrMovements","movement_date"), ("invoices","database_entry_at") })
        {
            command.CommandText = $"SELECT count(*) FROM public.\"{table}\" WHERE \"{column}\">CURRENT_TIMESTAMP";
            Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "Database role hides all future " + table);
        }
        var repository = new ProductCatalogRepository();
        var used = repository.LoadPageAsync(new ProductCatalogQuery(OnlyWithMovements: true, Limit: 1), CancellationToken.None).GetAwaiter().GetResult();
        Check(used.TotalCount == 1380, "Movement filter uses about 90 percent of the original catalog");
        var futureStart = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        var prescriptions = new MedulaPrescriptionRepository().LoadLatestAsync(futureStart,futureStart.AddMonths(3),null,CancellationToken.None).GetAwaiter().GetResult();
        Check(prescriptions.Count == 0, "Future date-range prescription searches return no rows");
        var current = new MedulaPrescriptionRepository().LoadLatestAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-31)), DateOnly.FromDateTime(DateTime.Today),null,CancellationToken.None).GetAwaiter().GetResult().First();
        var entry = new MedulaPrescriptionEntryView(current.LocalSnapshot!);
        entry.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var specialty = ((TextBlock)entry.FindName("BranchNameText")).Text;
        Check(!specialty.Any(c => "çğıöşüÇĞİÖŞÜ".Contains(c)), "Prescription view renders the English specialty");
        Check(((TextBlock)entry.FindName("ConnectionText")).Text is "Offline" or "Temporary offline", "Prescription status has no explanatory suffix");
        entry.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var analysis = new ProductAnalysisRepository().LoadReadOnlyAsync(used.Items[0].RecordId,used.Items[0].Barcode).GetAwaiter().GetResult();
        Check(analysis.ShelfStockDisplay.EndsWith(" packs") && analysis.WantedStockMonthsDisplay.EndsWith(" months"), "Analysis units are English");
        Check(analysis.MovementTypes.All(t => !t.DisplayName.Any(c => "çğıöşüÇĞİÖŞÜ".Contains(c))), "All analysis dropdown movement labels are English");
        shell.Close();
        return 0;
    }

    private static IEnumerable<MenuItem> Leaves(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
            if (item.Items.Count == 0) yield return item;
            else foreach (var child in Leaves(item.Items)) yield return child;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
