namespace pharmacyApp.Modules;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.StockMovements;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Medula;
using pharmacyApp.Modules.Invoices;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Takas;
using pharmacyApp.Modules.WebDepots.Alliance;
using pharmacyApp.Modules.WebDepots.Bek;
using pharmacyApp.Modules.WebDepots.Cooperative;
using pharmacyApp.Modules.WebDepots.Iskoop;
using pharmacyApp.Modules.WebDepots.Selcuk;

public static class ModuleCatalog
{
    public static void RegisterAll(ModuleRegistry registry)
    {
        registry.Register(new ModuleDefinition(
            Id: "products.catalog",
            MenuPath: ["Ürünler"],
            ButtonText: "Ürün Katalogu",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Ürün Katalogu",
                createContent: () => new ProductCatalogView(new ProductCatalogLaunchOptions(OnlyInStockOnLaunch: false)),
                minimumWidth: 950,
                minimumHeight: 540)));

        registry.Register(new ModuleDefinition(
            Id: "products.stock",
            MenuPath: ["Ürünler"],
            ButtonText: "Stok",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Stok",
                createContent: () => new ProductCatalogView(new ProductCatalogLaunchOptions(OnlyInStockOnLaunch: true)),
                minimumWidth: 950,
                minimumHeight: 540)));

        registry.Register(new ModuleDefinition(
            Id: "products.qr-movements",
            MenuPath: ["Ürünler"],
            ButtonText: "Karekod Hareketleri",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Karekod Hareketleri (Günlük)",
                createContent: () => new QrMovementsView(QrMovementsLaunchOptions.Daily),
                minimumWidth: 1050,
                minimumHeight: 560)));

        registry.Register(new ModuleDefinition(
            Id: "sales.retail",
            MenuPath: ["Satışlar"],
            ButtonText: "Perakende Satış",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Perakende Satış",
                createContent: () => new RetailSaleView(),
                minimumWidth: 1250,
                minimumHeight: 620)));

        registry.Register(new ModuleDefinition(
            Id: "sales.retail-list",
            MenuPath: ["Satışlar"],
            ButtonText: "Satışlar",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Satışlar",
                createContent: () => new RetailSalesListView(),
                minimumWidth: 1180,
                minimumHeight: 650)));

        registry.Register(new ModuleDefinition(
            Id: "sales.retail-refund",
            MenuPath: ["Satışlar"],
            ButtonText: "Perakende Satış İade",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Perakende Satış İade",
                createContent: () => new RetailRefundView(),
                minimumWidth: 1250,
                minimumHeight: 620)));

        registry.Register(new ModuleDefinition(
            Id: "sales.consignment",
            MenuPath: ["Satışlar"],
            ButtonText: "Emanet Satış",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Emanet Satış",
                createContent: () => new RetailSaleView(RetailSaleLaunchOptions.Consignment),
                minimumWidth: 1250,
                minimumHeight: 620)));

        registry.Register(new ModuleDefinition(
            Id: "takas.incoming-entry",
            MenuPath: ["Takas"],
            ButtonText: "Takas Girişi",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Takas Girişi",
                createContent: () => new TakasEntryView(TakasEntryLaunchOptions.Incoming),
                minimumWidth: 1180,
                minimumHeight: 650),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(13, 148, 136)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "G",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "takas.entry",
            MenuPath: ["Takas"],
            ButtonText: "Takas Çıkışı",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Takas Çıkışı",
                createContent: () => new TakasEntryView(TakasEntryLaunchOptions.Outgoing),
                minimumWidth: 1180,
                minimumHeight: 650),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(109, 40, 217)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "Ç",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "takas.list",
            MenuPath: ["Takas"],
            ButtonText: "Takaslar",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Takaslar",
                createContent: () => new TakasListView(),
                minimumWidth: 1180,
                minimumHeight: 650),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(91, 33, 182)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "L",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "invoices.offline-list",
            MenuPath: ["Faturalar"],
            ButtonText: "Fatura Listesi",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Fatura Listesi",
                createContent: () => new OfflineInvoiceListView(),
                minimumWidth: 1180,
                minimumHeight: 640),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(13, 148, 136)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "F",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "invoices.addresses",
            MenuPath: ["Faturalar"],
            ButtonText: "Adresler",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Adresler",
                createContent: () => new InvoiceAddressListView(),
                minimumWidth: 900,
                minimumHeight: 540),
            CreateTabIcon: () => new Border
            {
                Width = 16, Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(55, 98, 122)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "A", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "invoices.stock-movements",
            MenuPath: ["Faturalar"],
            ButtonText: "Fatura Hareketleri",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Fatura Hareketleri",
                createContent: () => new InvoiceStockLogView(),
                minimumWidth: 1180,
                minimumHeight: 650),
            CreateTabIcon: () => new Border
            {
                Width = 16, Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(23, 97, 61)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "H", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "invoices.partial-refund",
            MenuPath: ["Faturalar"],
            ButtonText: "Kısmi İade",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Kısmi İade",
                createContent: () => new PartialInvoiceRefundView(
                    new PartialInvoiceRefundLaunchOptions(SendItsNotifications: false)),
                minimumWidth: 1080,
                minimumHeight: 620),
            CreateTabIcon: () => new Border
            {
                Width = 16, Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(180, 83, 9)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "K", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.invoice-update",
            MenuPath: ["Debug"],
            ButtonText: "Faturaları Güncelle",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Faturaları Güncelle",
                createContent: () => new InvoiceUpdateView(),
                minimumWidth: 880,
                minimumHeight: 540),
            CreateTabIcon: () => new Border
            {
                Width = 16, Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(13, 148, 136)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "G", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.loyal-patient-update",
            MenuPath: ["Debug"],
            ButtonText: "Sadık Hastaları Güncelle",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Sadık Hastaları Güncelle",
                createContent: () => new LoyalPatientUpdateView(),
                minimumWidth: 900,
                minimumHeight: 560),
            CreateTabIcon: () => new Border
            {
                Width = 16, Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(14, 116, 144)), CornerRadius = new CornerRadius(0),
                Child = new TextBlock { Text = "S", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            }));

        registry.Register(new ModuleDefinition(
            Id: "medula.prescription-entry",
            MenuPath: ["Medula"],
            ButtonText: "Reçete Giriş",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Medula Reçete Giriş",
                createContent: () => new MedulaPrescriptionEntryView(),
                minimumWidth: 1180,
                minimumHeight: 680),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(35, 94, 115)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "R",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "medula.prescription-list",
            MenuPath: ["Medula"],
            ButtonText: "MIT local prescriptions",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "MIT local prescriptions",
                createContent: () => new MedulaPrescriptionListView(),
                minimumWidth: 1080,
                minimumHeight: 620),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(35, 94, 115)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "L",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "medula.e-prescription-query",
            MenuPath: ["Medula"],
            ButtonText: "E-Reçete Sorgu",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "E-Reçete Sorgu",
                createContent: () => new MedulaElectronicPrescriptionQueryView(),
                minimumWidth: 1040,
                minimumHeight: 620),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(12, 94, 136)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "E",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "medula.prescription-query",
            MenuPath: ["Medula"],
            ButtonText: "Reçete Sorgu",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Reçete Sorgu",
                createContent: () => new MedulaPrescriptionQueryView(),
                minimumWidth: 980,
                minimumHeight: 590),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(43, 106, 125)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "S",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "medula.connection-test",
            MenuPath: ["Debug"],
            ButtonText: "Medula Bağlantısını Test et",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Medula Bağlantı Testi",
                createContent: () => new MedulaConnectionTestView(),
                minimumWidth: 720,
                minimumHeight: 430),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(13, 148, 136)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "M",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.its-sale-test",
            MenuPath: ["Debug"],
            ButtonText: "İTS Satış Denemesi",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "İTS Satış Denemesi",
                createContent: () => new ItsSaleTestView(),
                minimumWidth: 1120,
                minimumHeight: 680),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(31, 105, 164)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "İ",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.manual-stock-adjustment",
            MenuPath: ["Debug"],
            ButtonText: "Manuel Stok Düzenleme",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Manuel Stok Düzenleme",
                createContent: () => new ManualStockAdjustmentView(),
                minimumWidth: 980,
                minimumHeight: 620)));

        registry.Register(new ModuleDefinition(
            Id: "debug.unnotified-its-movements",
            MenuPath: ["Debug"],
            ButtonText: "İTS Bildirimsiz Hareketler",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "İTS Bildirimsiz Hareketler",
                createContent: () => new UnnotifiedItsMovementsView(),
                minimumWidth: 980,
                minimumHeight: 580)));

        registry.Register(new ModuleDefinition(
            Id: "debug.its-status-query",
            MenuPath: ["Debug"],
            ButtonText: "İTS Sorgu",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "İTS Sorgu",
                createContent: () => new ItsStatusQueryView(),
                minimumWidth: 900,
                minimumHeight: 560),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(13, 148, 136)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "S",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.its-drug-list",
            MenuPath: ["Debug"],
            ButtonText: "İTS İlaç Listesi",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "İTS İlaç Listesi",
                createContent: () => new ItsDrugListView(),
                minimumWidth: 1000,
                minimumHeight: 600),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(41, 112, 94)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "L",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.selcuk-test",
            MenuPath: ["Debug"],
            ButtonText: "Selçuk Test",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Selçuk Test",
                createContent: () => new SelcukTestView(),
                minimumWidth: 720,
                minimumHeight: 430),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(45, 93, 135)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "S",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.bek-test",
            MenuPath: ["Debug"],
            ButtonText: "BEK Test",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "BEK Test",
                createContent: () => new CooperativeWebDepotTestView(BekWebDepotService.Instance),
                minimumWidth: 720,
                minimumHeight: 430),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(37, 122, 78)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "B",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.iskoop-test",
            MenuPath: ["Debug"],
            ButtonText: "İSKOOP Test",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "İSKOOP Test",
                createContent: () => new CooperativeWebDepotTestView(IskoopWebDepotService.Instance),
                minimumWidth: 720,
                minimumHeight: 430),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(111, 78, 155)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "İ",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        registry.Register(new ModuleDefinition(
            Id: "debug.alliance-test",
            MenuPath: ["Debug"],
            ButtonText: "Alliance/Cencora Test",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Alliance/Cencora Test",
                createContent: () => new AllianceTestView(),
                minimumWidth: 720,
                minimumHeight: 430),
            CreateTabIcon: () => new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Color.FromRgb(20, 103, 165)),
                CornerRadius = new CornerRadius(0),
                Child = new TextBlock
                {
                    Text = "A",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }));

        foreach (var card in CardDefinitions.All)
        {
            registry.Register(new ModuleDefinition(
                Id: card.ModuleId,
                MenuPath: ["Kartlar"],
                ButtonText: card.ButtonText,
                Launch: () => ModuleLaunchDefinition.SinglePanel(
                    tabTitle: card.ListTitle,
                    createContent: () => new CardListView(card),
                    minimumWidth: 820,
                    minimumHeight: 520)));
        }

        registry.Register(new ModuleDefinition(
            Id: "cards.patient-debt-history",
            MenuPath: ["Kartlar"],
            ButtonText: "Hasta Borç Geçmişi",
            Launch: () => ModuleLaunchDefinition.SinglePanel(
                tabTitle: "Hasta Borç Geçmişi",
                createContent: () => new PatientDebtHistoryView(),
                minimumWidth: 1050,
                minimumHeight: 560)));

        // MODULE STARTER — copy this block, remove the comment markers, and replace the values/view.
        // MenuPath creates the hierarchy: ["Patients", "Search"] becomes Patients > Search > ButtonText.
        // A one-segment path such as ["Patients"] places ButtonText directly inside the Patients menu.
        //
        // registry.Register(new ModuleDefinition(
        //     Id: "patients.search",
        //     MenuPath: ["Patients", "Search"],
        //     ButtonText: "Open Patient Search",
        //     Launch: () => ModuleLaunchDefinition.SinglePanel(
        //         tabTitle: "Patient Search",
        //         createContent: () => new PatientSearchView(),
        //         minimumWidth: 640,
        //         minimumHeight: 360),
        //     CreateTabIcon: () => new Image
        //     {
        //         Source = (ImageSource)Application.Current.FindResource("PatientSearchTabIcon"),
        //         Stretch = Stretch.Uniform
        //     }));
        //
        // For a module that opens several panels in one tab, return a ModuleLaunchDefinition directly:
        // Launch: () => new ModuleLaunchDefinition("Module Tab", [
        //     new ModulePanelDefinition("Main", () => new MainModuleView(), 640, 360),
        //     new ModulePanelDefinition("Filters", () => new FilterView(), 280, 360, ModulePanelPlacement.Right)
        // ])
    }
}
