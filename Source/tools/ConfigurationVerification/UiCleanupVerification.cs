using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules;
using pharmacyApp.Modules.Cards;
using pharmacyApp.Modules.Invoices;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Medula;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.StockMovements;
using pharmacyApp.Modules.Takas;

internal static class UiCleanupVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(string? imageDirectory = null)
    {
        // Load actual application resources while suppressing production startup/database work.
        var startup = new List<DispatcherOperation>();
        DispatcherHookEventHandler capture = (_, e) => { if (e.Operation.Priority == DispatcherPriority.Send) startup.Add(e.Operation); };
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += capture;
        var app = new pharmacyApp.App();
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= capture;
        foreach (var operation in startup) operation.Abort();
        app.InitializeComponent();

        var sale = new RetailSaleView();
        var refund = new RetailRefundView();
        var views = new UserControl[]
        {
            sale, refund, new TakasEntryView(), new TakasEntryView(TakasEntryLaunchOptions.Incoming),
            new TakasListView(), new ItsSaleTestView(), new ItsDrugListView(), new ItsStatusQueryView(),
            new ManualStockAdjustmentView(), new UnnotifiedItsMovementsView(),
            new PartialInvoiceRefundView(new PartialInvoiceRefundLaunchOptions(SendItsNotifications: true))
        };
        foreach (var view in views)
        {
            Layout(view);
            var notices = Children(view).OfType<TextBlock>().Where(x => x.Text == EditionPresentation.ItsNotice).ToArray();
            Check(notices.Length == 1 && notices[0].IsVisible == false && notices[0].Visibility == Visibility.Visible &&
                notices[0].Foreground is SolidColorBrush { Color: var color } && color == Colors.Red,
                "One red ITS notice in detached layout: " + view.GetType().Name);
            EditionPresentation.AddItsNotice(view);
            Check(Children(view).OfType<TextBlock>().Count(x => x.Text == EditionPresentation.ItsNotice) == 1,
                "Reapplying the ITS notice creates no duplicate: " + view.GetType().Name);
        }

        var prescription = new MedulaPrescriptionEntryView();
        var prescriptionList = new MedulaPrescriptionListView();
        foreach (var view in new UserControl[] { new QrMovementsView(), prescription, prescriptionList,
                     new OfflineInvoiceListView(), new PartialInvoiceRefundView() })
        {
            Layout(view);
            Check(!Children(view).OfType<TextBlock>().Any(x => x.Text == EditionPresentation.ItsNotice),
                "Local screen has no ITS banner: " + view.GetType().Name);
        }
        foreach (var view in new UserControl[] { prescription, prescriptionList })
        {
            var notice = (TextBlock)view.FindName("LocalPrescriptionNotice");
            Check(notice.Text == "Only local(fake,) prescriptions due to MIT version" &&
                notice.Visibility == Visibility.Visible && notice.Foreground is SolidColorBrush { Color: var color } && color == Colors.Red,
                "Local prescription screen has the exact red notice: " + view.GetType().Name);
        }

        foreach (var view in new[] { new ProductCatalogView(), new ProductCatalogView(new ProductCatalogLaunchOptions(OnlyInStockOnLaunch: true)) })
        {
            Layout(view);
            var notice = (TextBlock)view.FindName("MedicineRangeNotice");
            Check(notice.Text == "Only medicine in AA-AV interval are included due to MIT version" &&
                notice.Visibility == Visibility.Visible && notice.Foreground is SolidColorBrush { Color: var color } && color == Colors.Red,
                "Catalogue and Stock show the requested red AA-AV notice: " + view.CurrentModuleId);
            Check(!Children(view).OfType<TextBlock>().Any(x => x.Text == EditionPresentation.ItsNotice),
                "Catalogue and Stock have no ITS notice: " + view.CurrentModuleId);
        }

        foreach (var view in new UserControl[] { sale, refund })
        {
            Check(view.FindName("CustomerPhoto") is null && view.FindName("NoCustomerPhotoText") is null,
                "Patient photo showcase is removed: " + view.GetType().Name);
            Check(view.FindName("SummaryPhoto") is Image, "Medication image remains: " + view.GetType().Name);
            var name = (TextBlock)view.FindName("CustomerName");
            var title = Children(view).OfType<TextBlock>().Single(x => x.Text == "Customer Summary");
            Check(name.TranslatePoint(new Point(), view).Y - title.TranslatePoint(new Point(), view).Y < 60,
                "Patient summary has no reserved photo gap: " + view.GetType().Name);
        }

        var editor = new CardEditorView(CardDefinitions.Patients, null, null, () => { });
        Layout(editor);
        Check(typeof(CardEditorView).GetField("photoPreview", Private)!.GetValue(editor) is null,
            "Patient editor has no photo picker/preview");
        var bytes = new byte[] { 1, 2, 3 };
        var photo = CardDefinitions.Patients.Fields.Single(x => x.Kind == CardFieldKind.Photo);
        typeof(CardEditorView).GetMethod("SetEditorValue", Private)!.Invoke(editor, new object[] { photo, bytes });
        var state = (Dictionary<string, object?>)typeof(CardEditorView).GetMethod("CaptureEditorState", Private)!.Invoke(editor, null)!;
        Check(((byte[])state[photo.ColumnName]!).SequenceEqual(bytes), "Hidden patient image data is preserved without a photo editor");

        var photoMethod = typeof(MedulaPrescriptionEntryView).GetMethod("ApplyPatientPhotoSource", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var prefix in new[] { "Owner", "Recipient" })
        {
            var image = (Image)prescription.FindName(prefix + "PatientPhoto");
            var container = (FrameworkElement)prescription.FindName(prefix + "PhotoContainer");
            var column = (ColumnDefinition)prescription.FindName(prefix + "PhotoColumn");
            var spacer = (ColumnDefinition)prescription.FindName(prefix + "PhotoSpacerColumn");
            photoMethod.Invoke(null, new object[] { image, container, column, spacer, 48d, new DrawingImage() });
            Check(image.Source is null && container.Visibility == Visibility.Collapsed && column.Width.Value == 0 && spacer.Width.Value == 0,
                "Prescription photo remains hidden even when image data is supplied: " + prefix);
        }

        var home = new pharmacyApp.Modules.Home.HomeScreenView(() => new pharmacyApp.AI.Models.AiApplicationContext(CurrentModule: "home"));
        Layout(home);
        Check(!Children((DependencyObject)home.FindName("WelcomePanel")).OfType<TextBlock>().Any(), "Empty chat has no New Chat placeholder");
        Check(Children(home).OfType<Button>().Any(button => button.Content?.ToString() == "New Chat"), "New Chat action remains available");

        var settings = new pharmacyApp.Modules.Settings.DatabaseSettingsView();
        Check(((TextBlock)settings.FindName("ItsSettingsTitle")).Inlines.OfType<Run>().Any(run =>
            run.Text.Contains(EditionPresentation.ItsNotice) && run.Foreground is SolidColorBrush { Color: var color } && color == Colors.Red),
            "ITS settings contain the red MIT notice");
        if (imageDirectory is not null)
        {
            System.IO.Directory.CreateDirectory(imageDirectory);
            Capture(sale, System.IO.Path.Combine(imageDirectory, "retail.png"));
            Capture(refund, System.IO.Path.Combine(imageDirectory, "refund.png"));
            Capture(views[2], System.IO.Path.Combine(imageDirectory, "outgoing-trade.png"));
            Capture(home, System.IO.Path.Combine(imageDirectory, "assistant.png"));
            Capture(editor, System.IO.Path.Combine(imageDirectory, "patient-editor.png"));
        }

        var shell = new pharmacyApp.MainWindow();
        var tabs = (IList)typeof(pharmacyApp.MainWindow).GetField("tabs", Private)!.GetValue(shell)!;
        var before = tabs.Count;
        var request = new ModuleTabRequest("verification.ui", "Verification", () => new UserControl(), 100, 100,
            () => throw new InvalidOperationException("Decorative module icon must never be created."));
        typeof(pharmacyApp.MainWindow).GetMethod("OpenRequestedTab", Private)!.Invoke(shell, new object[] { request });
        Check(tabs.Count == before + 1, "A module tab opens without constructing its decorative icon");
        var unexpectedDialog = false;
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        guard.Tick += (_, _) =>
        {
            foreach (Window window in app.Windows.Cast<Window>().Where(x => x != shell && x.IsVisible).ToArray())
            { unexpectedDialog = true; window.Close(); }
        };
        guard.Start();
        typeof(pharmacyApp.MainWindow).GetMethod("RemoveTab", Private)!.Invoke(shell, new[] { tabs[tabs.Count - 1] });
        guard.Stop();
        Check(!unexpectedDialog && tabs.Count == before, "Closing a module tab does not show a save dialog");
        Check(((StackPanel)shell.FindName("HomeTabStrip")).Children.OfType<Button>().Any(button => button.Content?.ToString() == "\u2302") &&
            ((StackPanel)shell.FindName("SettingsTabStrip")).Children.OfType<Button>().Any(button => button.Content?.ToString() == "\u2699"),
            "Home and Settings tab icons are restored");

        var windowForIcon = new Window { Title = "Caption verification", Width = 250, Height = 120 };
        windowForIcon.Show();
        windowForIcon.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        var handle = new WindowInteropHelper(windowForIcon).Handle;
        var iconDeadline = DateTime.UtcNow.AddSeconds(1);
        while ((GetWindowLongPtr(handle, -20).ToInt64() & 1) == 0 && DateTime.UtcNow < iconDeadline)
        {
            var tick = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => tick.Continue = false));
            Dispatcher.PushFrame(tick);
        }
        Check((GetWindowLongPtr(handle, -20).ToInt64() & 1) != 0 && SendMessage(handle, 0x007f, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero,
            "Loaded windows have no native caption icon");
        windowForIcon.Close();
        shell.Close();
        Console.WriteLine("PASS UI cleanup without production Loaded handlers or business operations");
        return 0;
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1280, 900)); view.Arrange(new Rect(0, 0, 1280, 900)); view.UpdateLayout();
        pharmacyApp.Localization.EnglishUi.Apply(view);
    }
    private static void Capture(FrameworkElement view, string path)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32);
        var canvas = new DrawingVisual();
        using (var drawing = canvas.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1280, 900));
            drawing.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, 1280, 900));
        }
        bitmap.Render(canvas);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
}
