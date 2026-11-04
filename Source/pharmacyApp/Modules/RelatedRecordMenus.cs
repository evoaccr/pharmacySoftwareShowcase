using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.Invoices;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Takas;

namespace pharmacyApp.Modules;

public sealed record RelatedRecordLink(
    string Title,
    Func<CancellationToken, Task<FrameworkElement?>>? CreateView = null,
    Func<CancellationToken, Task<IReadOnlyList<RelatedRecordLink>>>? LoadChildren = null);

public static class RelatedRecordMenus
{
    private static readonly object GeneratedItem = new();

    public static void Attach(DataGrid grid)
    {
        grid.ContextMenu ??= new ContextMenu();
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (HasNestedMenu(grid, e.OriginalSource as DependencyObject)) return;
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row is null) { grid.SelectedItem = null; return; }
            if (ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid)) grid.SelectedItem = row.Item;
        };
        grid.ContextMenuOpening += (_, e) =>
        {
            if (HasNestedMenu(grid, e.OriginalSource as DependencyObject)) return;
            var row = e.CursorLeft < 0 && e.CursorTop < 0 ? grid.SelectedItem
                : FindAncestor<DataGridRow>(grid.InputHitTest(Mouse.GetPosition(grid)) as DependencyObject)?.Item;
            if (row is null || !grid.Items.Contains(row)) { e.Handled = true; return; }
            grid.SelectedItem = row;
            Populate(grid, LinksFor(row));
            if (grid.ContextMenu.Items.Count == 0) e.Handled = true;
        };
    }

    public static IReadOnlyList<RelatedRecordLink> LinksFor(object row) => row switch
    {
        RetailSaleListItem sale => SaleLinks(sale),
        RetailSaleProductGroup product => ProductLinks(product.Barcode),
        TakasListItem trade => TradeLinks(trade),
        TakasListProductGroup product => ProductLinks(product.Barcode),
        OfflineInvoiceRow invoice => InvoiceLinks(invoice),
        InvoiceAddressRow address when !string.IsNullOrWhiteSpace(address.Gln) || !string.IsNullOrWhiteSpace(address.IdentityNo) =>
            [new("Go to Related Record", LoadChildren: token => LoadPartyLinksAsync(address.Gln, address.IdentityNo, token))],
        OfflineInvoiceLineRow line => ProductLinks(line.Barcode),
        InvoiceStockLogMovementRow movement => ProductLinks(movement.Barcode),
        PartialInvoiceRefundGroup product => ProductLinks(product.Barcode),
        RetailRefundGroup product => ProductLinks(product.Barcode),
        _ => []
    };

    private static IReadOnlyList<RelatedRecordLink> SaleLinks(RetailSaleListItem sale)
    {
        var links = new List<RelatedRecordLink>();
        if (sale.PatientId is > 0) links.Add(Card("Go to Patient", CardDefinitions.Patients, sale.PatientId.Value.ToString(CultureInfo.InvariantCulture)));
        links.Add(new("Go to Product", LoadChildren: token => LoadSaleProductLinksAsync(sale, token)));
        return links;
    }

    private static async Task<IReadOnlyList<RelatedRecordLink>> LoadSaleProductLinksAsync(RetailSaleListItem sale, CancellationToken token)
    {
        var (table, parentColumn) = sale.IsConsignment ? ("consignmentSaleItems", "consignment_sale_id")
            : sale.IsRefund ? ("salesRefundItems", "refund_id") : ("salesQrItems", "sale_id");
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(token);
        // Table/column identifiers come only from the fixed choices above; the record ID is parameterized.
        await using var command = DatabaseCommand.Create($"""
            SELECT DISTINCT item.barcode, COALESCE(NULLIF(product.medicine_name,''), NULLIF(product.brand_name,''), item.barcode)
            FROM public."{table}" item JOIN public.barcode product ON product.barcode=item.barcode
            WHERE item.{parentColumn}=@id ORDER BY 2,1
            """, connection);
        command.Parameters.AddWithValue("id", sale.SaleId);
        var links = new List<RelatedRecordLink>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) links.Add(Product(reader.GetString(0), reader.GetString(1)));
        return links;
    }

    private static IReadOnlyList<RelatedRecordLink> TradeLinks(TakasListItem trade)
    {
        var links = new List<RelatedRecordLink>();
        if (trade.PharmacyId > 0) links.Add(Card("Go to Pharmacy", CardDefinitions.Pharmacies, trade.PharmacyId.ToString(CultureInfo.InvariantCulture)));
        links.Add(new("Go to Product", LoadChildren: async token =>
            (await new TakasListRepository().LoadDetailsAsync(trade.TakasId, token))
                .Where(p => !string.IsNullOrWhiteSpace(p.Barcode)).Select(p => Product(p.Barcode, p.ProductName)).ToArray()));
        return links;
    }

    private static IReadOnlyList<RelatedRecordLink> InvoiceLinks(OfflineInvoiceRow invoice)
    {
        var links = new List<RelatedRecordLink>();
        if (!string.IsNullOrWhiteSpace(invoice.SenderGln) || !string.IsNullOrWhiteSpace(invoice.SenderIdentityNo))
            links.Add(new("Go to Sender", LoadChildren: token => LoadPartyLinksAsync(invoice.SenderGln, invoice.SenderIdentityNo, token)));
        if (!string.IsNullOrWhiteSpace(invoice.ReceiverGln) || !string.IsNullOrWhiteSpace(invoice.ReceiverIdentityNo))
            links.Add(new("Go to Receiver", LoadChildren: token => LoadPartyLinksAsync(invoice.ReceiverGln, invoice.ReceiverIdentityNo, token)));
        links.Add(new("Go to Product", LoadChildren: async token =>
            (await new OfflineInvoiceRepository().LoadLinesAsync(invoice.InvoiceUuid, token))
                .Where(p => !string.IsNullOrWhiteSpace(p.Barcode)).GroupBy(p => p.Barcode)
                .Select(p => Product(p.Key, p.First().ItemName)).ToArray()));
        return links;
    }

    public static async Task<IReadOnlyList<RelatedRecordLink>> LoadPartyLinksAsync(string gln, string identity, CancellationToken token)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(token);
        await using var command = DatabaseCommand.Create("""
            SELECT 'depot', record_id::text, depot_name FROM public.depot
            WHERE (@gln<>'' AND gln_code=@gln) OR (@identity<>'' AND tax_no=@identity)
            UNION ALL
            SELECT 'pharmacy', record_id::text, pharmacy_name FROM public.pharmacy
            WHERE (@gln<>'' AND gln_code=@gln) OR (@identity<>'' AND tax_no=@identity)
            UNION ALL
            SELECT 'patient', p.record_id::text, CONCAT_WS(' ', p.first_name, p.last_name) FROM public.patient p
            WHERE @identity<>'' AND (p.tc_identity_no=@identity OR EXISTS (
                SELECT 1 FROM public.patient_identity i WHERE i.patient_record_id=p.record_id AND i.tc_identity_no=@identity))
            ORDER BY 1, 2
            """, connection);
        command.Parameters.AddWithValue("gln", gln.Trim());
        command.Parameters.AddWithValue("identity", identity.Trim());
        var links = new List<RelatedRecordLink>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var (title, definition) = reader.GetString(0) switch
            {
                "depot" => ("Go to Warehouse (Depot)", CardDefinitions.Depots),
                "pharmacy" => ("Go to Pharmacy", CardDefinitions.Pharmacies),
                _ => ("Go to Patient", CardDefinitions.Patients)
            };
            var label = reader.IsDBNull(2) ? "" : reader.GetString(2);
            links.Add(Card(string.IsNullOrWhiteSpace(label) ? title : $"{title}: {label}", definition, reader.GetString(1)));
        }
        return links;
    }

    private static RelatedRecordLink Card(string title, CardEntityDefinition definition, string key) => new(title,
        async token => (await new CardRepository().LoadValuesAsync(definition, key, token)).Count == 0
            ? null : new CardEditorView(definition, key, null, () => { }, startEditing: false));

    private static IReadOnlyList<RelatedRecordLink> ProductLinks(string? barcode) =>
        string.IsNullOrWhiteSpace(barcode) ? [] : [Product(barcode, "Go to Product", includeBarcode: false)];

    private static RelatedRecordLink Product(string barcode, string name, bool includeBarcode = true) => new(
        includeBarcode ? $"{name} ({barcode})" : name,
        async token => await new ProductCatalogRepository().LoadByBarcodeAsync(barcode, token) is { } product
            ? new ProductCatalogView(new ProductCatalogLaunchOptions(InitialProduct: product)) : null);

    public static void Populate(DataGrid grid, IReadOnlyList<RelatedRecordLink> links)
    {
        var menu = grid.ContextMenu ??= new ContextMenu();
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(item => ReferenceEquals(item.Tag, GeneratedItem)).ToArray()) menu.Items.Remove(old);
        var existing = menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToHashSet();
        var added = links.Where(link => !existing.Contains(link.Title)).ToArray();
        if (added.Length == 0) return;
        if (menu.Items.Count > 0) menu.Items.Add(new Separator { Tag = GeneratedItem });
        foreach (var link in added) menu.Items.Add(CreateMenuItem(grid, link));
    }

    private static MenuItem CreateMenuItem(DataGrid grid, RelatedRecordLink link)
    {
        var item = new MenuItem { Header = link.Title, Tag = GeneratedItem };
        if (link.LoadChildren is not null)
        {
            item.Items.Add(new MenuItem { Header = "Loading…", IsEnabled = false });
            item.SubmenuOpened += async (_, e) =>
            {
                if (!ReferenceEquals(e.Source, item)) return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var children = await link.LoadChildren(timeout.Token);
                    item.Items.Clear();
                    foreach (var child in children) item.Items.Add(CreateMenuItem(grid, child));
                    if (item.Items.Count == 0) item.Items.Add(new MenuItem { Header = "No linked local records", IsEnabled = false });
                }
                catch (Exception) { item.Items.Clear(); item.Items.Add(new MenuItem { Header = "Could not load related records", IsEnabled = false }); }
            };
        }
        else if (link.CreateView is not null)
        {
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var view = await link.CreateView(timeout.Token);
                    if (view is null) { pharmacyApp.Localization.EnglishMessageBox.Show("The linked record is not available locally.", "Related Record"); return; }
                    var window = new Window { Content = view, Title = link.Title, Width = 1280, Height = 880,
                        MinWidth = 840, MinHeight = 640, Owner = Window.GetWindow(grid),
                        WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
                    if (view is IModuleTitleSource titles) { window.Title = titles.CurrentModuleTitle; titles.ModuleTitleChanged += title => window.Title = title; }
                    if (view is IModuleCloseRequestSource close) close.CloseRequested += window.Close;
                    window.Show();
                }
                catch (OperationCanceledException) { }
                catch (Exception exception) { pharmacyApp.Localization.EnglishMessageBox.Show($"Could not open the linked record: {exception.Message}", "Related Record"); }
            };
        }
        return item;
    }

    public static bool HasNestedMenu(DataGrid grid, DependencyObject? source)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, grid); current = Parent(current))
            if (current is FrameworkElement { ContextMenu: { } menu } && !ReferenceEquals(menu, grid.ContextMenu)) return true;
        return false;
    }

    public static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = Parent(current)) if (current is T match) return match;
        return null;
    }

    private static DependencyObject? Parent(DependencyObject source) => source switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source),
        FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
        ContentElement content => ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(source)
    };
}
