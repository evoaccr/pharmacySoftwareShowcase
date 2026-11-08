using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using pharmacyApp.Infrastructure;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Providers;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        VerifyOpeningDocument();
        VerifyOnlineRestrictions();
        if (args is ["--verify-ui-cleanup"]) return UiCleanupVerification.Run();
        if (args is ["--verify-ui-cleanup", var cleanupImages]) return UiCleanupVerification.Run(cleanupImages);
        if (args is ["--verify-ai-presentation", var workflowLabels]) return VerifyAiPresentation(workflowLabels);
        if (args is ["--verify-input-rendering", var inputOutput]) return InputRenderingVerification.Run(inputOutput);
        if (args is ["--verify-window-close", var closeMode]) return VerifyWindowClose(closeMode);
        if (args is ["--verify-restricted-popups"]) return RestrictedPopupVerification.Run();
        if (args is ["--verify-edition"]) return EditionVerification.Run();
        if (args is ["--verify-list-navigation"]) return RelatedNavigationVerification.Run();
        if (args is ["--verify-context-navigation"]) return VerifyContextNavigation();
        if (args is ["--verify-startup"])
        {
            var app = new pharmacyApp.App();
            app.InitializeComponent();
            var deadline = DateTime.UtcNow.AddSeconds(25);
            var seenStartupWindows = new HashSet<string>();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                foreach (System.Windows.Window window in app.Windows)
                    if (seenStartupWindows.Add(window.GetType().FullName + ":" + window.Title)) Console.WriteLine("Startup window: " + window.GetType().Name + " / " + window.Title);
                var errorWindow = app.Windows.OfType<System.Windows.Window>().FirstOrDefault(window => window.GetType() == typeof(System.Windows.Window) && window.Title is "Startup" or "local Configuration" or "Opening");
                if (errorWindow?.Content is System.Windows.Controls.Grid errorContent)
                {
                    Console.Error.WriteLine(string.Join("\n", errorContent.Children.OfType<System.Windows.Controls.TextBox>().Select(box => box.Text)));
                    timer.Stop(); errorWindow.Close(); app.Shutdown(1); return;
                }
                if (app.Windows.OfType<pharmacyApp.Modules.Login.LoginWindow>().Any())
                {
                    timer.Stop();Console.Error.WriteLine("Unexpected login screen");app.Shutdown(1);return;
                }
                var opening = app.Windows.OfType<pharmacyApp.OpeningWindow>().FirstOrDefault();
                if (opening is not null) EnterOpeningForVerification(opening);
                if (app.MainWindow is pharmacyApp.MainWindow && app.CurrentUser?.UserId == "admin")
                {
                    timer.Stop();Console.WriteLine("PASS Production startup opens admin directly without a login window");app.Shutdown(0);
                }
                else if (DateTime.UtcNow > deadline)
                {
                    timer.Stop();Console.Error.WriteLine("Startup timed out");app.Shutdown(1);
                }
            };
            timer.Start();
            return app.Run();
        }
        if (args is ["--normalize-settings"])
        {
            ApplicationSettingsStore.Save(ApplicationSettingsStore.Current);
            Console.WriteLine("Saved automatic admin, offline prescriptions and empty depot credentials.");
            return 0;
        }
        if (args is ["--verify-settings", var output])
        {
            var app = new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/pharmacyApp;component/Themes/Workbench.xaml", UriKind.Relative) });
            var view = new pharmacyApp.Modules.Settings.DatabaseSettingsView { Width = 1200, Height = 700 };
            view.Measure(new System.Windows.Size(1200, 700));
            view.Arrange(new System.Windows.Rect(0, 0, 1200, 700));
            view.UpdateLayout();
            var tabs = (System.Windows.Controls.TabControl)view.FindName("SettingsTabs");
            Assert(tabs.Items.Count == 5, "Settings categories replace the long single page");
            foreach (var name in new[] { "MedulaSettingsTitle", "ItsSettingsTitle", "EczaciKartSettingsTitle", "SelcukSettingsTitle", "BekSettingsTitle", "IskoopSettingsTitle", "AllianceSettingsTitle", "DatabaseSettingsTitle" })
            {
                var title = (System.Windows.Controls.TextBlock)view.FindName(name);
                Assert(title.TextWrapping == System.Windows.TextWrapping.Wrap && title.Inlines.OfType<System.Windows.Documents.Run>().Last().Foreground is System.Windows.Media.SolidColorBrush { Color: var color } && color == System.Windows.Media.Colors.Red,
                    "Settings title has a wrapping red disclaimer: " + name);
            }
            foreach (var name in new[] { "MedulaUsernameBox", "MedulaPasswordEntry", "ItsUsernameBox", "ItsPasswordEntry", "EczaciKartUsernameBox", "EczaciKartPasswordEntry", "SelcukUsernameBox", "SelcukPasswordEntry", "BekUsernameBox", "BekPasswordEntry", "IskoopUsernameBox", "IskoopPasswordEntry", "AllianceUsernameBox", "AlliancePasswordEntry" })
                Assert(view.FindName(name) is null, "Integration credential control removed: " + name);
            foreach (var name in new[] { "HostnameBox", "PortBox", "DatabaseNameBox", "UsernameBox" })
                Assert(view.FindName(name) is System.Windows.Controls.TextBox { IsEnabled: true, IsReadOnly: false }, "Database setting stays editable: " + name);
            Assert(view.FindName("PasswordEntry") is System.Windows.Controls.PasswordBox { IsEnabled: true }, "Database password stays editable");
            var panel = (pharmacyApp.Modules.Settings.SettingsPanel)((System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)tabs.Items[0]).Content).Content;
            Assert(panel.Children.Count == 4 && System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((System.Windows.FrameworkElement)panel.Children[1]).X > 0,
                "Actual settings cards use multiple columns on a desktop viewport");
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(1200,700,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(view);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (var stream = File.Create(output)) encoder.Save(stream);
            foreach (var index in new[] { 3, 4 })
            {
                tabs.SelectedIndex = index;
                view.UpdateLayout();
                var preview = new System.Windows.Media.Imaging.RenderTargetBitmap(1200,700,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                preview.Render(view);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
                using var stream = File.Create(Path.Combine(Path.GetDirectoryName(output)!, index == 3 ? "integrations.png" : "database.png")); png.Save(stream);
            }
            tabs.SelectedIndex = 0;
            view.Width = 720; view.Height = 600;
            view.Measure(new System.Windows.Size(720,600));view.Arrange(new System.Windows.Rect(0,0,720,600));view.UpdateLayout();
            Assert(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((System.Windows.FrameworkElement)panel.Children[1]).X == 0,
                "Settings cards stack within narrower viewports");
            return 0;
        }
        if (args is ["--set-database"])
        {
            var database = JsonSerializer.Deserialize<DatabaseConnectionSettings>(Console.In.ReadToEnd())
                ?? throw new InvalidOperationException("Database settings are required.");
            AppConfiguration.WriteSection("Database", database);
            AppConfiguration.WriteSection("Application", ApplicationSettingsStore.Current with { IgnoredUserId = null });
            Console.WriteLine("Updated local database settings; unrelated settings and comments preserved.");
            return 0;
        }
        if (args is ["--verify-opening-browser"])
            return VerifyOpeningBrowser();
        if (args is ["--verify-synthetic-data"])
        {
            var cards = new pharmacyApp.Modules.Cards.CardRepository();
            var patients = cards.LoadListPageAsync(pharmacyApp.Modules.Cards.CardDefinitions.Patients,
                new pharmacyApp.Modules.Cards.CardListQuery(), 0, 50, CancellationToken.None).GetAwaiter().GetResult();
            Assert(patients.TotalCount == 1000 && patients.Rows.Count == 50, "Actual patient list reads the synthetic patients and joined card data");
            var end = DateOnly.FromDateTime(DateTime.Today);
            var prescriptions = new pharmacyApp.Modules.Medula.MedulaPrescriptionRepository().LoadLatestAsync(
                end.AddDays(-31), end, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(prescriptions.Count >= 1000 && prescriptions.All(p => p.LocalSnapshot is { IsLocalArchive: true, Drugs.Count: > 0 } && p.IsQrFinalized),
                "Actual local prescription repository reconstructs archived medication snapshots");
            var sales = new pharmacyApp.Modules.Sales.RetailSalesListRepository().LoadSalesAsync(
                new pharmacyApp.Modules.Sales.RetailSalesListQuery(DateTime.Today.AddDays(-31), DateTime.Today.AddDays(1)), CancellationToken.None).GetAwaiter().GetResult();
            Assert(sales.Count >= 1000, "Actual transaction list reads linked sales and payment data");
            return 0;
        }
        if (args is ["--verify-catalog"])
        {
            var repository = new pharmacyApp.Modules.Products.ProductCatalogRepository();
            var page = repository.LoadPageAsync(new pharmacyApp.Modules.Products.ProductCatalogQuery(Limit: 5), CancellationToken.None).GetAwaiter().GetResult();
            Assert(page.TotalCount == 1533 && page.Items.Count == 5, "Actual app repository reads the original 1533-medication catalog");
            using var connection = DatabaseConnectionSettingsStore.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT b.record_id FROM barcode b JOIN documents d ON d.barcode=b.barcode WHERE cardinality(b.photo)>0 AND (d.kub IS NOT NULL OR d.kit IS NOT NULL) AND EXISTS (SELECT 1 FROM \"barcodePriceHistory\" h WHERE h.record_reference_no=b.record_id) ORDER BY b.record_id LIMIT 1";
            var recordId = Convert.ToInt32(command.ExecuteScalar());
            var detail = repository.LoadProductDetailsAsync(recordId, CancellationToken.None).GetAwaiter().GetResult();
            Assert(detail.Photos.Count > 0 && (detail.KubDocument?.Length > 0 || detail.KitDocument?.Length > 0), "Actual app repository loads medication photos and PDF documents");
            Assert(repository.LoadPriceHistoryAsync(recordId, CancellationToken.None).GetAwaiter().GetResult().Count > 0, "Actual app repository loads original price history");
            command.CommandText = "SELECT count(*) FROM patient";
            var populated = Convert.ToInt32(command.ExecuteScalar()) > 0;
            var inStock = repository.LoadPageAsync(new pharmacyApp.Modules.Products.ProductCatalogQuery(OnlyInStock: true, Limit: 5), CancellationToken.None).GetAwaiter().GetResult().TotalCount;
            Assert(populated ? inStock > 0 : inStock == 0, "Actual stock view reflects whether synthetic activity has been generated");
            return 0;
        }
        if (args is ["--verify-database-ui"])
        {
            var settings = DatabaseConnectionSettingsStore.Current;
            Assert(settings.DatabaseName == "Portfolio" && settings.Username == "portfolio", "Application uses the dedicated database login");
            var users = new UserSessionRepository();
            Assert(users.LoadActiveUsersAsync().GetAwaiter().GetResult().Count == 1 &&
                users.AuthenticateAsync("admin", "portfoli123").GetAwaiter().GetResult()?.UserId == "admin",
                "The seeded administrator can authenticate through the actual app repository");
            var app = new pharmacyApp.App();
            app.InitializeComponent();
            var shell = new pharmacyApp.MainWindow();
            var menu = (System.Windows.Controls.Menu)shell.FindName("TopMenuBar");
            var disabled = menu.Items.Cast<System.Windows.Controls.MenuItem>().Where(item => !item.IsEnabled).ToArray();
            Assert(!disabled.Any(item => item.Header.ToString() is "Medula" or "Debug"), "Real shell exposes local Medula and the restricted Debug dropdown");
            var items = new pharmacyApp.Modules.Medula.MedulaPrescriptionRepository().LoadLatestAsync(
                DateOnly.FromDateTime(DateTime.Today.AddDays(-31)), DateOnly.FromDateTime(DateTime.Today), null, CancellationToken.None).GetAwaiter().GetResult();
            var snapshot = items.First(item => item.LocalSnapshot?.Drugs.Count > 0).LocalSnapshot!;
            var entry = new pharmacyApp.Modules.Medula.MedulaPrescriptionEntryView(snapshot);
            entry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent));
            Assert(entry.Drugs.Count == snapshot.Drugs.Count, "Offline prescription screen displays stored medication rows without an online session");
            entry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.UnloadedEvent));
            shell.Close();
            return 0;
        }
        if (args is ["--preview-opening"])
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            new pharmacyApp.OpeningWindow(OpeningDocument.Parse("""
                # Overview
                Some **bold** and *italic* text.

                | Item | Status |
                | --- | --- |
                | Markdown | Ready |

                - First item
                - Second item

                ---

                ## Details
                Separate page with `code`.
                """)).ShowDialog();
            return 0;
        }
        if (args is ["--write-example", var example])
        {
            new ConfigurationStore(example).EnsureFile();
            Console.WriteLine("Generated a complete example config with empty credentials.");
            return 0;
        }
        var fixtures = Path.Combine(AppContext.BaseDirectory, "verification-fixtures");
        Directory.CreateDirectory(fixtures);
        var path = Path.Combine(fixtures, "config.json");
        var original = """
            // Profile notes: İstanbul — do not remove.
            {
              "Application": {
                "PharmacyName": "First", // Inline note stays here.
                // "PharmacistName": "DISABLED VALUE",
                "AiUserDisplayName": "local"
              },
              "Database": {
                "Hostname": "localhost",
                // "Port": 1111,
                "DatabaseName": "UnitTest",
                "Username": "unit",
                "Password": "literal // password",
              },
              "Ai": {
                /* Keep this block comment. */
                "GatewayEndpoint": "https://example.test/path//literal" // Tail note.
              },
              "Secrets": {},
              "Custom": { "Keep": "untouched" }
            }
            """;
        File.WriteAllText(path, original);
        var store = new ConfigurationStore(path);
        Assert(store.ReadSection<ApplicationSettings>("Application").PharmacistName == "", "Commented settings are ignored and use defaults");
        Assert(store.ReadDatabase().Port == new DatabaseConnectionSettings().Port, "Commented database settings use constructor defaults");
        Assert(store.ReadDatabase().Password == "literal // password", "Comment prefixes inside strings are literal data");
        store.WriteSection("Application", new { PharmacyName = "Updated İstanbul" });
        store.WriteSection("Ai", new { Enabled = false, MaxToolTurns = 8 });
        store.WriteSection("Secrets", new AiSecrets { OpenAiApiKey = "unit-key" });
        store.WriteSection("NewSection", new { Value = "added" });
        var updated = File.ReadAllText(path);
        Assert(updated.Contains("// Profile notes: İstanbul") && updated.Contains("// Inline note stays here.") && updated.Contains("// Tail note.") && updated.Contains("/* Keep this block comment. */"), "Saving settings preserves line, inline and block comments");
        Assert(updated.Contains("// \"PharmacistName\": \"DISABLED VALUE\""), "Saving settings retains disabled lines");
        Assert(store.ReadSection<ApplicationSettings>("Application").PharmacyName == "Updated İstanbul", "Unicode profile values round-trip");
        Assert(store.ReadSection<AiConfiguration>("Ai").GatewayEndpoint == "https://example.test/path//literal", "Partial updates retain other settings and URL slashes");
        Assert(!store.ReadSection<AiConfiguration>("Ai").Enabled && store.ReadSection<AiConfiguration>("Ai").MaxToolTurns == 8, "Adding properties after a tail comment produces valid JSONC");
        using (var document = JsonDocument.Parse(updated, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            Assert(document.RootElement.GetProperty("Custom").GetProperty("Keep").GetString() == "untouched", "Unknown sections remain intact");
        store.WriteSection("Database", store.ReadDatabase() with { Port = 5433 });
        Assert(store.ReadDatabase().Port == 5433 && store.ReadSection<AiSecrets>("Secrets").OpenAiApiKey == "unit-key", "Database saves preserve other sections");
        store.WriteSection("Application", store.ReadSection<ApplicationSettings>("Application") with { AiUserDisplayName = "Full settings save" });
        Assert(store.ReadSection<ApplicationSettings>("Application").AiUserDisplayName == "Full settings save" && File.ReadAllText(path).Contains("// Inline note stays here."), "Full application settings saves preserve comments and nested defaults");
        Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => store.WriteSection("Section" + i, new { Value = i })))).GetAwaiter().GetResult();
        using (var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            Assert(Enumerable.Range(0, 12).All(i => document.RootElement.GetProperty("Section" + i).GetProperty("Value").GetInt32() == i), "Concurrent section writes do not lose unrelated updates");
        foreach (var invalid in new[] { "{ broken", "[]", "{\"Ai\":{},\"Ai\":{}}" })
        {
            File.WriteAllText(path, invalid);
            AssertThrows(() => store.ReadSection<AiConfiguration>("Ai"), "Invalid or duplicate configuration is rejected");
            AssertThrows(() => store.WriteSection("Ai", new AiConfiguration()), "Invalid configuration is never silently overwritten");
            Assert(File.ReadAllText(path) == invalid, "Rejected edits leave the original file intact");
        }
        var handler = new CaptureHandler();
        var transport = new OpenAiResponsesTransport(new FixedAiConfigurationProvider(new AiConfiguration()), new HttpClient(handler),
            () => new AiSecrets { OpenAiApiKey = "unit-config-key" });
        transport.SendAsync(new AiModelRequest("test", "", "test", [], [], false, false)).GetAwaiter().GetResult();
        Assert(handler.Authorization == "Bearer unit-config-key", "AI authentication uses configuration secrets");
        var gateway = new OpenAiResponsesTransport(new FixedAiConfigurationProvider(new AiConfiguration { GatewayEndpoint = "https://gateway.test" }), new HttpClient(handler),
            () => new AiSecrets { GatewayToken = "unit-gateway-key" });
        gateway.SendAsync(new AiModelRequest("test", "", "test", [], [], false, false)).GetAwaiter().GetResult();
        Assert(handler.Authorization == "Bearer unit-gateway-key", "Gateway authentication uses configuration secrets");
        Assert(ApplicationSettingsStore.SettingsFilePath == DatabaseConnectionSettingsStore.SettingsFilePath &&
               ApplicationSettingsStore.SettingsFilePath == AiConfigurationProvider.ConfigurationPath, "Application, database and AI share exactly one settings file");
        var verifiedSettings = new DatabaseConnectionSettings("portfolio-db.acarise.net", 1234, "Portfolio", "portfolio", "test-only");
        var build = typeof(DatabaseConnectionSettingsStore).GetMethod("BuildConnectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var connectionOptions = new Npgsql.NpgsqlConnectionStringBuilder((string)build.Invoke(null, [verifiedSettings])!);
        Assert(connectionOptions.SslMode == Npgsql.SslMode.VerifyFull, "Public database connections require certificate and hostname verification");
        Assert(!verifiedSettings.AllowSchemaChanges, "Reviewer clients do not run database migrations");
        Assert(!StartupErrors.RuntimeErrorMessage(new InvalidOperationException()).Contains("re-pull"), "Runtime errors do not report an incomplete installation");
        Assert(StartupErrors.RuntimeErrorMessage(new Exception("outer", new Npgsql.PostgresException("test", "ERROR", "ERROR", "XX001"))).Contains("damaged data"),
            "Nested PostgreSQL corruption errors identify damaged server data");
        Console.WriteLine("PASS portable configuration, comments, isolation and AI authentication");
        return 0;
    }

    private static void VerifyOpeningDocument()
    {
        var pages = OpeningDocument.Parse("""
            # **Welcome**
            Some **bold** and *italic* text.

            | Name | Value |
            | --- | --- |
            | A | B |

            - First
            * Second

            ---

            ## Subheading
            ### Smaller heading
            Subsection content.

            # Details
            ```md
            # This is code, not a tab
            ```
            <script>alert('test')</script>
            """);
        Assert(pages.Count == 2 && pages[0].Title == "Welcome" && pages[1].Title == "Details", "Only level-one headings create tabs, excluding code fences");
        Assert(pages[0].Html.Contains("<h2>Subheading</h2>") && pages[0].Html.Contains("<h3>Smaller heading</h3>") && pages[0].Html.Contains("Subsection content."), "Subheadings and their content remain inside the parent tab");
        Assert(pages[0].Html.Contains("<table>") && pages[0].Html.Contains("<strong>bold</strong>") &&
               pages[0].Html.Contains("<em>italic</em>") && pages[0].Html.Contains("<li>First</li>") &&
               pages[0].Html.Contains("<hr"), "Tables, emphasis, lists and separators render as Markdown");
        Assert(!pages[0].Html.Contains("Details") && !pages[1].Html.Contains("<table>"), "Tab contents are separate pages");
        Assert(!pages[1].Html.Contains("<script>"), "Raw HTML cannot execute");
        Assert(OpeningDocument.Parse("").Count == 1 && OpeningDocument.Parse("")[0].Title == "Opening", "Blank files still provide a startup screen");
        var preamble = OpeningDocument.Parse("Introduction text.\n\nTitle\n=====\nBody");
        Assert(preamble.Count == 2 && preamble[1].Title == "Title", "Preamble and setext headings are supported");
    }

    private static int VerifyContextNavigation()
    {
        var app = new pharmacyApp.App();
        app.InitializeComponent();
        var end = DateOnly.FromDateTime(DateTime.Today);
        var repository = new pharmacyApp.Modules.Medula.MedulaPrescriptionRepository();
        var rx = repository.LoadLatestAsync(end.AddDays(-31), end, null, CancellationToken.None).GetAwaiter().GetResult()
            .First(item => item.LocalSnapshot?.Drugs.Count > 0);
        var view = new pharmacyApp.Modules.Medula.MedulaPrescriptionListView();
        view.Prescriptions.Add(rx);
        var grid = (System.Windows.Controls.DataGrid)view.FindName("PrescriptionGrid");
        grid.ItemsSource = view.PrescriptionView;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var row = new System.Windows.Controls.DataGridRow { Item = rx };
        var factory = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock), "CellText");
        row.Template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.DataGridRow)) { VisualTree = factory };
        row.ApplyTemplate();
        var text = (System.Windows.Controls.TextBlock)row.Template.FindName("CellText", row);
        var run = new System.Windows.Documents.Run("Prescription text");
        text.Inlines.Add(new System.Windows.Documents.Span(run));
        var find = typeof(pharmacyApp.Modules.Medula.MedulaPrescriptionListView).GetMethod("FindAncestor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(typeof(System.Windows.Controls.DataGridRow));
        Assert(ReferenceEquals(find.Invoke(null, [run]), row), "Prescription hit testing crosses nested inline text to the row without a visual-tree exception");
        Assert(find.Invoke(null, [new System.Windows.Documents.Run("Detached")]) is null, "Detached prescription text is harmless");
        var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right)
            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent, Source = run };
        run.RaiseEvent(click);
        typeof(pharmacyApp.Modules.Medula.MedulaPrescriptionListView).GetMethod("OnPrescriptionGridRightClick", flags)!.Invoke(view, [grid, click]);
        Assert(ReferenceEquals(grid.SelectedItem, rx), "Actual prescription right-click handler selects the clicked inline-text row");
        typeof(pharmacyApp.Modules.Medula.MedulaPrescriptionListView).GetMethod("ConfigureRelatedCardMenu", flags)!.Invoke(view, [rx]);
        foreach (var name in new[] { "GoToPatientMenuItem", "GoToDoctorMenuItem", "GoToFacilityMenuItem" })
        {
            var menu = (System.Windows.Controls.MenuItem)view.FindName(name);
            Assert(menu.IsEnabled, "Linked prescription action is enabled: " + name);
            var (definition, identity) = (menu.Tag as string) switch
            {
                "doctor" => (pharmacyApp.Modules.Cards.CardDefinitions.Doctors, rx.LocalSnapshot!.Page.FieldValues!["f:t43"]),
                "facility" => (pharmacyApp.Modules.Cards.CardDefinitions.Faculties, rx.LocalSnapshot!.Page.FieldValues!["f:t33"]),
                _ => (pharmacyApp.Modules.Cards.CardDefinitions.Patients, rx.PatientTcIdentityNo)
            };
            var cards = new pharmacyApp.Modules.Cards.CardRepository();
            var key = cards.ResolveRelatedCardKeyAsync(definition.Kind, identity, CancellationToken.None).GetAwaiter().GetResult();
            Assert(key is not null && cards.LoadValuesAsync(definition, key, CancellationToken.None).GetAwaiter().GetResult().Count > 0,
                "Prescription identity resolves to an existing local card: " + name);
        }
        Assert(new pharmacyApp.Modules.Cards.CardRepository().ResolveRelatedCardKeyAsync(
            pharmacyApp.Modules.Cards.CardEntityKind.Patients, "missing-patient", CancellationToken.None).GetAwaiter().GetResult() is null,
            "Missing related identities do not open an unrelated card");
        var movements = new pharmacyApp.Modules.StockMovements.QrMovementsView();
        foreach (var name in new[] { "MovementGrid", "EntryGrid", "ExitGrid" })
            Assert(((System.Windows.Controls.DataGrid)movements.FindName(name)).ContextMenu is not null, "QR context menu is installed: " + name);
        var movementRepository = new pharmacyApp.Modules.StockMovements.QrMovementRepository();
        var types = movementRepository.LoadMovementTypesAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var code in new[] { "PRESC_OUT", "DEP_IN", "PERAK_OUT" })
        {
            var type = types.Single(t => t.Code == code);
            var page = movementRepository.LoadPageAsync(new pharmacyApp.Modules.StockMovements.QrMovementQuery(
                DateTime.Today.AddDays(-31), DateTime.Today.AddDays(1), [type.Id]), null, null, 5, false, CancellationToken.None).GetAwaiter().GetResult();
            var movement = page.Items.First();
            var menu = new System.Windows.Controls.ContextMenu();
            typeof(pharmacyApp.Modules.StockMovements.QrMovementsView).GetMethod("PopulateMovementMenu", flags)!.Invoke(movements, [menu, movement]);
            var labels = menu.Items.Cast<System.Windows.Controls.MenuItem>().Select(m => m.Header.ToString()).ToArray();
            Assert(labels.Contains("Go to Product") && labels.Contains("Go to User"), "QR row links product and user: " + code);
            Assert(labels.Contains(code == "DEP_IN" ? "Go to Warehouse (Depot)" : "Go to Patient"), "QR row links the correct address record: " + code);
            if (code == "PRESC_OUT")
                Assert(labels.Contains("Go to Prescription") && repository.LoadLocalSnapshotForQrAsync(movement.QrCode).GetAwaiter().GetResult() is { IsLocalArchive: true, Drugs.Count: > 0 },
                    "Prescription QR movement resolves an archived prescription with medication rows");
            if (code == "PERAK_OUT") Assert(labels.Contains("Go to Sale"), "Retail QR movement links its source sale");
        }
        var qrFind = typeof(pharmacyApp.Modules.StockMovements.QrMovementsView).GetMethod("FindVisualParent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(typeof(System.Windows.Controls.DataGridRow));
        Assert(ReferenceEquals(qrFind.Invoke(null, [run]), row), "QR hit testing also accepts inline text");
        return 0;
    }

    private static void VerifyOnlineRestrictions()
    {
        var settings = ApplicationSettingsStore.Current;
        Assert(settings.IgnoredUserId == "admin" && settings.OfflinePrescriptionList,
            "Local settings always select automatic admin and offline prescriptions");
        Assert(new[] { settings.SelcukUsername, settings.SelcukPassword, settings.BekUsername, settings.BekPassword,
            settings.IskoopUsername, settings.IskoopPassword, settings.AlliancePharmacyCode, settings.AllianceUsername,
            settings.AlliancePassword }.All(string.IsNullOrEmpty), "All web depot credential fields remain empty");
        Assert(!OnlineAccessPolicy.IsModuleEnabled("medula.prescription-entry") &&
               !OnlineAccessPolicy.IsModuleEnabled("debug.selcuk-test") &&
               !OnlineAccessPolicy.IsModuleEnabled("debug.bek-test") &&
               OnlineAccessPolicy.IsModuleEnabled("products.catalog") && OnlineAccessPolicy.IsModuleEnabled("medula.prescription-list"), "Online menus are disabled while local prescriptions and modules remain available");
        var registry = pharmacyApp.AI.Runtime.AiToolRegistry.Discover();
        Assert(registry.GetTool("medula.session_login") is null && registry.GetTool("medula.e_prescriptions_search") is null &&
               registry.GetTool("medula.prescriptions_search") is not null && registry.GetTool("medula.prescription_get") is not null,
               "AI discovery excludes online-only tools and retains local archive tools");
        var gateway = new pharmacyApp.AI.Execution.AiToolExecutionGateway(registry,
            new pharmacyApp.AI.Execution.AppAiPermissionService(), new pharmacyApp.AI.Execution.AiConfirmationService(),
            new FixedAiConfigurationProvider(new AiConfiguration()),
            new pharmacyApp.AI.Execution.AiOperationalLog(Path.Combine(AppContext.BaseDirectory, "verification-fixtures", "logs")));
        var context = new pharmacyApp.AI.Models.AiRequestContext("offline-test", "test",
            new pharmacyApp.AI.Models.AiApplicationContext(CurrentUserId: "admin"), new HashSet<string>());
        var deniedSearch = gateway.ExecuteAsync(context, new pharmacyApp.AI.Models.AiToolCall(
            "offline", "medula.prescriptions_search", "{\"mode\":\"live_date_range\"}")).GetAwaiter().GetResult();
        Assert(deniedSearch.Status == pharmacyApp.AI.Execution.AiToolExecutionStatus.Disabled,
            "AI execution rejects live Medula mode before calling its handler");
        Assert(!pharmacyApp.Modules.Medula.MedulaConnectionService.Instance.ConnectAsync().GetAwaiter().GetResult(),
            "Medula connection is denied before credentials, browser or network access");
        Action[] attempts = [
            () => new pharmacyApp.Modules.Its.ItsApiClient().TestConnectionAsync(pharmacyApp.Modules.Its.ItsEnvironment.Production,
                new pharmacyApp.Modules.Its.ItsCredentials("verification", "unused"), CancellationToken.None).GetAwaiter().GetResult(),
            () => ((Task)typeof(pharmacyApp.Modules.Invoices.EczaciKartConnectionService).GetMethod("EnsureBrowserAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(pharmacyApp.Modules.Invoices.EczaciKartConnectionService.Instance, [false, CancellationToken.None])!).GetAwaiter().GetResult(),
            () => pharmacyApp.Modules.WebDepots.Selcuk.SelcukWebDepotService.Instance.OpenBrowserAsync().GetAwaiter().GetResult(),
            () => pharmacyApp.Modules.WebDepots.Alliance.AllianceWebDepotService.Instance.OpenBrowserAsync().GetAwaiter().GetResult(),
            () => pharmacyApp.Modules.WebDepots.Bek.BekWebDepotService.Instance.OpenBrowserAsync().GetAwaiter().GetResult(),
            () => pharmacyApp.Modules.WebDepots.Iskoop.IskoopWebDepotService.Instance.OpenBrowserAsync().GetAwaiter().GetResult()
        ];
        foreach (var attempt in attempts)
        {
            var denied = false;
            try { attempt(); } catch (NotSupportedException) { denied = true; }
            Assert(denied, "Restricted integration is denied before browser or network access");
        }
    }

    private static int VerifyOpeningBrowser()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/pharmacyApp;component/Themes/Workbench.xaml", UriKind.Relative) });
        var window = new pharmacyApp.OpeningWindow(OpeningDocument.Parse("# First\nVisible paragraph.\n\n## Subheading\nChild content.\n\n# Second\nOther page.\n\n# Enter the app\n"));
        var grid = (System.Windows.Controls.Grid)window.Content;
        var tabs = (System.Windows.Controls.TabControl)grid.Children[0];
        var browser = (Microsoft.Web.WebView2.Wpf.WebView2)grid.Children[1];
        var previous = OpeningButton(window, "previousButton");
        var forward = OpeningButton(window, "forwardButton");
        var enter = OpeningButton(window, "continueButton");
        var skip = OpeningButton(window, "skipButton");
        var acknowledgement = OpeningAcknowledgement(window);
        var completed = new TaskCompletionSource();
        browser.NavigationStarting += (_, e) => Console.WriteLine("Navigation starting scheme: " + new Uri(e.Uri).Scheme);
        browser.NavigationCompleted += (_, e) =>
        {
            Console.WriteLine("Navigation completed: " + e.IsSuccess + " " + e.WebErrorStatus);
            completed.TrySetResult();
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert(window.WindowState == System.Windows.WindowState.Normal && window.Width <= 1000 && window.Height <= 730 &&
                    window.Width < System.Windows.SystemParameters.WorkArea.Width && window.Height < System.Windows.SystemParameters.WorkArea.Height,
                    "Opening screen starts smaller than the PC work area");
                Assert(tabs.Items.Count == 3 && ((System.Windows.Controls.TabItem)tabs.Items[2]).Header.ToString() == "Enter the app",
                    "A final Enter the app tab follows the information pages");
                Assert(!previous.IsEnabled && forward.Visibility == System.Windows.Visibility.Visible && forward.IsDefault &&
                    enter.Visibility == System.Windows.Visibility.Collapsed && !enter.IsDefault &&
                    acknowledgement.Visibility == System.Windows.Visibility.Collapsed && acknowledgement.IsChecked == false,
                    "First page has Forward, disabled Previous and no Continue");
                enter.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert(window.IsVisible && tabs.SelectedIndex == 0, "Continue cannot enter the app from an information page");
                var text = await browser.ExecuteScriptAsync("document.body.innerText");
                Console.WriteLine("First page browser text: " + text);
                Assert(text.Contains("Visible paragraph.") && text.Contains("Subheading") && text.Contains("Child content.") && !text.Contains("Other page."), "Actual browser displays the first tab content and subheading");
                Assert(browser.ActualHeight > 100 && browser.ActualWidth > 100, "Browser has usable visible layout bounds");
                Assert(skip.Content?.ToString() == "SKIP" && skip.Visibility == System.Windows.Visibility.Visible,
                    "First information page shows the SKIP action");
                completed = new TaskCompletionSource();
                skip.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert(tabs.SelectedIndex == 2 && skip.Visibility == System.Windows.Visibility.Collapsed &&
                    acknowledgement.IsChecked == false && !enter.IsEnabled && window.IsVisible,
                    "SKIP jumps directly from the first page to Enter the app without bypassing acknowledgement");
                completed = new TaskCompletionSource();
                tabs.SelectedIndex = 0;
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                completed = new TaskCompletionSource();
                forward.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                text = await browser.ExecuteScriptAsync("document.body.innerText");
                Assert(text.Contains("Other page.") && !text.Contains("Visible paragraph."), "Switching tabs displays the selected page in the actual browser");
                Assert(previous.IsEnabled && forward.Visibility == System.Windows.Visibility.Visible && enter.Visibility == System.Windows.Visibility.Collapsed,
                    "Middle information page has Previous and Forward without Continue");
                Assert(skip.Visibility == System.Windows.Visibility.Visible, "Middle information page also shows SKIP");
                completed = new TaskCompletionSource();
                skip.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                text = await browser.ExecuteScriptAsync("document.body.innerText");
                Assert(JsonSerializer.Deserialize<string>(text)?.Trim() == "Enter the app", "Real Markdown entry heading renders with no added body text");
                Assert(previous.IsEnabled && forward.Visibility == System.Windows.Visibility.Collapsed && skip.Visibility == System.Windows.Visibility.Collapsed &&
                    enter.Visibility == System.Windows.Visibility.Visible && enter.IsDefault,
                    "Only the final page has Continue, with Previous available to return");
                Assert(acknowledgement.Visibility == System.Windows.Visibility.Visible && acknowledgement.Content?.ToString() == "I red and understand" &&
                    acknowledgement.IsChecked == false && !enter.IsEnabled &&
                    enter.Foreground is System.Windows.Media.SolidColorBrush { Color: var disabledColor } && disabledColor == System.Windows.Media.Colors.Gray,
                    "Final page has the requested unchecked acknowledgement and a disabled grey Continue button");
                enter.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert(window.IsVisible && tabs.SelectedIndex == 2, "Unchecked acknowledgement cannot be bypassed by invoking Continue");
                acknowledgement.IsChecked = true;
                Assert(enter.IsEnabled, "Checking acknowledgement enables Continue");
                acknowledgement.IsChecked = false;
                Assert(!enter.IsEnabled, "Unchecking acknowledgement disables Continue again");
                completed = new TaskCompletionSource();
                previous.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                text = await browser.ExecuteScriptAsync("document.body.innerText");
                Assert(tabs.SelectedIndex == 1 && text.Contains("Other page.") && enter.Visibility == System.Windows.Visibility.Collapsed && skip.Visibility == System.Windows.Visibility.Visible,
                    "Previous returns to information content and hides Continue again");
                var reserved = new pharmacyApp.OpeningWindow(OpeningDocument.Parse("# Information\nContent\n# Enter the app\n"));
                var reservedTabs = (System.Windows.Controls.TabControl)((System.Windows.Controls.Grid)reserved.Content).Children[0];
                Assert(reservedTabs.Items.Count == 2, "An existing Enter the app Markdown page is reused without duplication");
                Assert(OpeningAcknowledgement(reserved).IsChecked == false,
                    "A new opening window always starts without saved acknowledgement");
                reserved.Close();
                var entryOnly = new pharmacyApp.OpeningWindow(OpeningDocument.Parse("# Enter the app\n"));
                Assert(OpeningButton(entryOnly, "skipButton").Visibility == System.Windows.Visibility.Collapsed,
                    "An intro containing only Enter the app has no SKIP button");
                entryOnly.Close();
                app.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                app.Shutdown(1);
            }
        };
        return app.Run(window);
    }

    private static int VerifyWindowClose(string mode)
    {
        if (mode is not ("opening" or "caption" or "system" or "programmatic")) throw new ArgumentException("Unknown close mode.");
        Assert(typeof(pharmacyApp.App).Assembly.EntryPoint?.DeclaringType == typeof(pharmacyApp.Program),
            "Production executable selects the explicit process-lifetime entry point");
        // Simulate a browser/SDK foreground thread that outlives the WPF dispatcher.
        new Thread(() => Thread.Sleep(Timeout.Infinite)) { IsBackground = false }.Start();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var observedExit = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var app = (pharmacyApp.App)System.Windows.Application.Current;
            if (!observedExit)
            {
                observedExit = true;
                app.Exit += (_, _) =>
                {
                    Assert(app.CurrentUser is null, "Normal WPF exit finishes administrator-session cleanup before process exit");
                    Console.WriteLine("PASS WPF exit completed for " + mode);
                };
            }
            if (DateTime.UtcNow > deadline) { Console.Error.WriteLine("Window-close verification timed out."); Environment.Exit(1); }
            var opening = app.Windows.OfType<pharmacyApp.OpeningWindow>().FirstOrDefault();
            if (opening is not null)
            {
                Assert(opening.WindowState == System.Windows.WindowState.Normal && opening.Width <= 1000 && opening.Height <= 730,
                    "Opening screen starts at its smaller window size");
                if (mode == "opening")
                {
                    timer.Stop(); opening.Close(); return;
                }
                EnterOpeningForVerification(opening);
            }
            if (app.MainWindow is not pharmacyApp.MainWindow main || app.CurrentUser?.UserId != "admin" || !main.IsVisible) return;
            var primary = DisplayMetrics.PrimaryResolution;
            var settings = ApplicationSettingsStore.Current;
            Assert(settings.ScreenWidth == primary.Width && settings.ScreenHeight == primary.Height,
                "Startup resolves the current PC resolution instead of the copied configuration size");
            var requested = DisplayMetrics.ToDeviceIndependentSize(primary);
            var work = System.Windows.SystemParameters.WorkArea;
            Assert(Math.Abs(main.Width - Math.Max(main.MinWidth, Math.Min(requested.Width, work.Width))) < 1 &&
                Math.Abs(main.Height - Math.Max(main.MinHeight, Math.Min(requested.Height, work.Height))) < 1,
                "Main window uses DPI-aware PC dimensions within the work area");
            timer.Stop();
            if (mode == "caption")
                ((System.Windows.Controls.Button)main.FindName("CloseButton")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            else if (mode == "system")
            {
                var auxiliary = new System.Windows.Window { Owner = main, Title = "Close verification", Width = 200, Height = 150 };
                auxiliary.Show(); auxiliary.Hide();
                System.Windows.SystemCommands.CloseWindow(main);
            }
            else main.Close();
        };
        timer.Start();
        pharmacyApp.Program.Main([]);
        return 1;
    }

    private static System.Windows.Controls.Button OpeningButton(pharmacyApp.OpeningWindow window, string field) =>
        (System.Windows.Controls.Button)typeof(pharmacyApp.OpeningWindow).GetField(field,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;

    private static void EnterOpeningForVerification(pharmacyApp.OpeningWindow window)
    {
        var tabs = (System.Windows.Controls.TabControl)((System.Windows.Controls.Grid)window.Content).Children[0];
        tabs.SelectedIndex = tabs.Items.Count - 1;
        Assert(!OpeningButton(window, "continueButton").IsEnabled, "Startup cannot enter before acknowledgement");
        OpeningAcknowledgement(window).IsChecked = true;
        OpeningButton(window, "continueButton").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }

    private static System.Windows.Controls.CheckBox OpeningAcknowledgement(pharmacyApp.OpeningWindow window) =>
        (System.Windows.Controls.CheckBox)typeof(pharmacyApp.OpeningWindow).GetField("acknowledgementCheckBox",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;

    private static int VerifyAiPresentation(string workflowLabels)
    {
        const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var app = new pharmacyApp.App(); app.InitializeComponent();
        var registry = pharmacyApp.AI.Workflows.AiWorkflowRegistry.Discover();
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(workflowLabels))!;
        Assert(registry.Definitions.Count == expected.Count, "Every discovered workflow has an expected English display label");
        foreach (var definition in registry.Definitions)
        {
            Assert(expected.TryGetValue(definition.Name, out var label) && definition.DisplayName == label,
                "English workflow label with stable execution ID: " + definition.Name);
            Assert(System.Text.RegularExpressions.Regex.IsMatch(definition.DisplayFunctionName, "^[a-z0-9]+(?:_[a-z0-9]+)*$"),
                "Workflow function uses lowercase snake_case: " + definition.DisplayFunctionName);
        }
        Assert(registry.Definitions.Select(definition => definition.DisplayFunctionName).Distinct().Count() == expected.Count,
            "English function names are unique across all workflows");
        var home = new pharmacyApp.Modules.Home.HomeScreenView(() => new pharmacyApp.AI.Models.AiApplicationContext());
        var runtimeField = home.GetType().GetField("runtime", instance)!;
        var runtime = (pharmacyApp.AI.Runtime.AiRuntime)runtimeField.GetValue(home)!;
        runtimeField.SetValue(home, runtime with { Configuration = new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true, DebugShowWorkflowFunctions = true }) });
        home.GetType().GetMethod("RefreshDebugControls", instance)!.Invoke(home, null);
        Assert(home.FindName("ModelDocsButton") is null, "The model website arrow is absent even in Debug mode");
        var usage = new[]
        {
            new pharmacyApp.AI.Models.AiModelUsage("Yönlendirici", "gpt-test", new(123, 20, 5, 45, 7)),
            new pharmacyApp.AI.Models.AiModelUsage("Gelişmiş Planlayıcı", "gpt-test", new(10, OutputTokens: 2)),
            new pharmacyApp.AI.Models.AiModelUsage("Yanıt", "gpt-test", new(20, OutputTokens: 4))
        };
        var footer = (string)home.GetType().GetMethod("FormatWorkflowFooter", instance)!.Invoke(home,
            new object[] { usage, new[] { "hasta_eslestir", "urun_stok_ozeti", "hareket_coklu_dagilim_analizi" } })!;
        Assert(footer.Contains("Router · gpt-test") && footer.Contains("Advanced Planner") && footer.Contains("Response") &&
            footer.Contains("Input: 123") && footer.Contains("cached: 20") && footer.Contains("cache writes: 5") &&
            footer.Contains("Output: 45") && footer.Contains("reasoning: 7") && footer.Contains("Total: 168"),
            "Answer footer stages and all token labels are English with unchanged counts");
        Assert(footer.Contains("Workflow: [match_patients → product_and_stock_summary → analyze_movements_across_multiple_dimensions]") &&
            !footer.Contains("hasta_eslestir"), "Workflow footer shows English snake_case function names rather than execution IDs");
        var list = (System.Windows.Controls.ListBox)home.FindName("MessageList");
        var template = (System.Windows.FrameworkElement)list.ItemTemplate.LoadContent();
        var message = new pharmacyApp.Modules.Home.ChatMessageViewModel(false, "Result") { FooterText = footer };
        message.AddRequestCapture(new pharmacyApp.AI.Workflows.AiRequestCaptureFile("Router request", "local-request.json", true));
        template.DataContext = message;
        template.Measure(new System.Windows.Size(1000, 1000));
        template.Arrange(new System.Windows.Rect(0, 0, 1000, 1000)); template.UpdateLayout();
        Assert(Texts(template).Contains("Router request") && !Texts(template).Any(text => string.Equals(text, "JSON", StringComparison.OrdinalIgnoreCase)),
            "Chat response template has no JSON badges");
        var answerFooter = (System.Windows.Controls.TextBlock)template.FindName("AnswerFooter");
        pharmacyApp.Localization.EnglishUi.Apply(answerFooter);
        var runs = answerFooter.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
        Assert(runs.Length == 2 && runs[1].Text == message.WorkflowFooterText && runs[1].Text.Contains("match_patients → product_and_stock_summary") &&
            runs[1].Foreground is System.Windows.Media.SolidColorBrush { Color: var red } && red == System.Windows.Media.Colors.Red &&
            runs[0].Foreground is System.Windows.Media.SolidColorBrush { Color: var muted } && muted != System.Windows.Media.Colors.Red,
            "Only the workflow arrow sequence is red after live English localization");
        Assert(runs[0].Text + runs[1].Text == footer, "Colored footer retains the complete text used by Copy Chat");
        message.FooterText = "Router · gpt-test — Input: 1";
        Assert(runs[1].Text.Length == 0 && runs[0].Text == message.FooterText,
            "Footer bindings update without leaving a stale workflow sequence");
        home.GetType().GetMethod("OnDiagnosticsClicked", instance)!.Invoke(home, new object[] { home, new System.Windows.RoutedEventArgs() });
        var diagnostics = app.Windows.OfType<System.Windows.Window>().Single(window => window.Title == "AI Diagnostics");
        var diagnosticsText = ((System.Windows.Controls.TextBox)diagnostics.Content).Text;
        Assert(registry.Definitions.All(definition => diagnosticsText.Contains("- " + definition.DisplayFunctionName)),
            "Actual diagnostics popup includes every English snake_case function name");
        diagnostics.Close();
        Console.WriteLine(footer);
        return 0;

        static IEnumerable<string> Texts(System.Windows.DependencyObject root)
        {
            if (root is System.Windows.Controls.TextBlock block) yield return block.Text;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var text in Texts(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return text;
        }
    }

    private static void Assert(bool result, string name)
    {
        if (!result) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
    private static void AssertThrows(Action action, string name)
    {
        try { action(); } catch (JsonException) { Console.WriteLine("PASS " + name); return; }
        throw new InvalidOperationException(name);
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"unit\",\"output\":[]}")
            });
        }
    }
}
