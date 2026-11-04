using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using pharmacyApp.Modules;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.Invoices;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Takas;

internal static class RelatedNavigationVerification
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/pharmacyApp;component/Themes/Workbench.xaml", UriKind.Relative) });
        app.Resources["SubtleDataGridRowStyle"] = new Style(typeof(DataGridRow));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        return Pump(VerifyAsync);
    }

    private static async Task<int> VerifyAsync()
    {
        var token = CancellationToken.None;
        var start = DateTime.Today.AddDays(-31);
        var end = DateTime.Today.AddDays(1);
        var salesView = new RetailSalesListView();
        foreach (var name in new[] { "SalesGrid", "EntrySalesGrid", "ExitSalesGrid", "SaleDetailGrid" })
            Check(((DataGrid)salesView.FindName(name)).ContextMenu is not null, "Sales menu attached: " + name);
        var sales = await new RetailSalesListRepository().LoadSalesAsync(new RetailSalesListQuery(start, end), token);
        foreach (var sale in sales.GroupBy(s => s.TransactionType).Select(g => g.First()))
        {
            var links = RelatedRecordMenus.LinksFor(sale);
            if (sale.PatientId is > 0) Check(await links.Single(l => l.Title == "Go to Patient").CreateView!(token) is CardEditorView, "Transaction links its patient: " + sale.TransactionType);
            var products = await links.Single(l => l.Title == "Go to Product").LoadChildren!(token);
            Check(products.Count > 0 && await products[0].CreateView!(token) is ProductCatalogView, "Transaction product submenu resolves local products: " + sale.TransactionType);
        }
        var invoiceView = new OfflineInvoiceListView();
        Check(((DataGrid)invoiceView.FindName("InvoiceGrid")).ContextMenu is not null && ((DataGrid)invoiceView.FindName("LineGrid")).ContextMenu is not null, "Invoice and invoice-line menus are attached");
        var invoices = await new OfflineInvoiceRepository().LoadInvoicesAsync(null, null, null, null, 0, 300, token);
        var invoice = invoices.Rows.First(i => i.IsIncoming);
        var invoiceLinks = RelatedRecordMenus.LinksFor(invoice);
        var parties = await invoiceLinks.Single(l => l.Title == "Go to Sender").LoadChildren!(token);
        Check(parties.Any(p => p.Title.StartsWith("Go to Warehouse (Depot)")) && await parties[0].CreateView!(token) is CardEditorView, "Invoice sender resolves the matching depot card");
        var outgoing = invoices.Rows.First(i => !i.IsIncoming);
        var receivers = await RelatedRecordMenus.LinksFor(outgoing).Single(l => l.Title == "Go to Receiver").LoadChildren!(token);
        Check(receivers.Any(p => p.Title.StartsWith("Go to Pharmacy")) && await receivers[0].CreateView!(token) is CardEditorView,
            "Outgoing invoice receiver resolves the matching pharmacy card");
        var invoiceProducts = await invoiceLinks.Single(l => l.Title == "Go to Product").LoadChildren!(token);
        Check(invoiceProducts.Count > 0 && await invoiceProducts[0].CreateView!(token) is ProductCatalogView, "Invoice products resolve from that invoice's lines");
        var line = (await new OfflineInvoiceRepository().LoadLinesAsync(invoice.InvoiceUuid, token)).First(l => l.Barcode.Length > 0);
        Check(await RelatedRecordMenus.LinksFor(line).Single().CreateView!(token) is ProductCatalogView, "Invoice-line product opens the catalog detail");
        var tradeView = new TakasListView();
        var trades = await new TakasListRepository().LoadAsync(new TakasListQuery(start, end), token);
        var trade = trades.First();
        var tradeLinks = RelatedRecordMenus.LinksFor(trade);
        Check(await tradeLinks.Single(l => l.Title == "Go to Pharmacy").CreateView!(token) is CardEditorView, "Trade resolves its pharmacy card");
        var tradeProducts = await tradeLinks.Single(l => l.Title == "Go to Product").LoadChildren!(token);
        Check(tradeProducts.Count > 0 && await tradeProducts[0].CreateView!(token) is ProductCatalogView, "Trade product submenu resolves the traded products");
        typeof(TakasListView).GetField("suppressSelectionChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tradeView, true);
        foreach (var name in new[] { "TakasGrid", "IncomingTakasGrid", "OutgoingTakasGrid" })
        {
            var grid = (DataGrid)tradeView.FindName(name);
            grid.ItemsSource = new[] { trade }; grid.SelectedItem = trade;
            var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { grid, true, -1d, -1d }, null)!;
            grid.RaiseEvent(args);
            Check(!args.Handled && grid.ContextMenu.Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == "Go to Pharmacy"), "Completed/incoming trade rows retain navigation: " + name);
        }
        var testGrid = new DataGrid { ContextMenu = new ContextMenu() };
        var retained = new MenuItem { Header = "Existing action" }; testGrid.ContextMenu.Items.Add(retained);
        RelatedRecordMenus.Populate(testGrid, tradeLinks); RelatedRecordMenus.Populate(testGrid, tradeLinks);
        Check(testGrid.ContextMenu.Items.Contains(retained) && testGrid.ContextMenu.Items.OfType<MenuItem>().Count(m => m.Header?.ToString() == "Go to Pharmacy") == 1,
            "Reopening menus preserves existing actions and avoids duplicate links");
        var child = new Border { ContextMenu = new ContextMenu() };
        Check(RelatedRecordMenus.HasNestedMenu(testGrid, child), "Existing QR-code child menus are preserved");
        Check((await RelatedRecordMenus.LoadPartyLinksAsync("", "", token)).Count == 0, "Empty party identities never match unrelated records");
        Check(((DataGrid)new PartialInvoiceRefundView().FindName("RefundGrid")).ContextMenu is not null &&
              ((DataGrid)new RetailRefundView().FindName("RefundGrid")).ContextMenu is not null &&
              ((DataGrid)new InvoiceStockLogView().FindName("DetailGrid")).ContextMenu is not null &&
              ((DataGrid)new InvoiceAddressListView().FindName("AddressGrid")).ContextMenu is not null,
            "Refund, stock-history and invoice-address menus are attached");
        Console.WriteLine("PASS Related-list navigation");
        return 0;
    }

    private static T Pump<T>(Func<Task<T>> action)
    {
        var frame = new DispatcherFrame();
        var task = action();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
