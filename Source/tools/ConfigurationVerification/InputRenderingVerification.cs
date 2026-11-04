using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using pharmacyApp.Localization;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Sales;

internal static class InputRenderingVerification
{
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        EnglishUi.Initialize();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/pharmacyApp;component/Themes/Workbench.xaml", UriKind.Relative) });
        app.Resources["SubtleDataGridRowStyle"] = new Style(typeof(DataGridRow));
        app.Resources["SortableDataGridColumnHeaderStyle"] = new Style(typeof(DataGridColumnHeader));
        var panel = new StackPanel { Margin = new Thickness(20), Width = 520 };
        var retail = new RetailSaleView();
        var stock = new ProductCatalogView(new ProductCatalogLaunchOptions(OnlyInStockOnLaunch: true));
        var qr = Detach((TextBox)retail.FindName("QrCodeInput"));
        var search = Detach((TextBox)stock.FindName("SearchBox"));
        var regular = new TextBox { Height = 30, Padding = new Thickness(8, 5, 8, 5), FontSize = 13 };
        var multiline = new TextBox { Height = 90, Padding = new Thickness(8), FontSize = 14, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var readOnly = new TextBox { Height = 34, Padding = new Thickness(9, 6, 9, 6), IsReadOnly = true, Text = "Readable stored value 123" };
        var password = new PasswordBox { Height = 36, Padding = new Thickness(9, 6, 9, 6), FontSize = 13 };
        var combo = new ComboBox { Height = 34, Padding = new Thickness(9, 6, 9, 6), IsEditable = true, IsTextSearchEnabled = false, ItemsSource = new[] { "Alpha", "Beta" } };
        Add("Retail: actual QR entry", qr);
        Add("Stock: actual search field", search);
        Add("Compact shared text field", regular);
        Add("Multiline notes", multiline);
        Add("Read-only shared text field", readOnly);
        Add("Shared password field (test value)", password);
        Add("Editable combo box", combo);
        var window = new Window { Title = "Input rendering verification", Content = panel, Width = 580, Height = 750, ShowInTaskbar = false };
        window.Show(); Pump();
        var errors = new List<string>();
        var metrics = new List<object>();
        var comboEditor = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
        foreach (var (name, box, text) in new[]
        {
            ("retail_qr", qr, "(01)08691234567890(21)Abc_123"),
            ("stock_search", search, "Abc_\u00c7\u0131\u011f \u00d6\u015f\u00fc 123"),
            ("compact", regular, "ABC_abc 0123456789"),
            ("notes", multiline, "First line with visible letters"),
            ("combo", comboEditor, "Mixed_CASE 123")
        })
        {
            box.Focus(); Keyboard.Focus(box);
            foreach (var character in text)
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, character.ToString()));
            Pump(); EnglishUi.Apply(box); window.UpdateLayout();
            if (box.Text != text) errors.Add(name + ": input was changed or rejected: " + box.Text);
            Inspect(name, box, box.FontSize, box.FontFamily);
        }
        multiline.AppendText(Environment.NewLine + "Second line is visible too");
        window.UpdateLayout();
        var last = multiline.GetRectFromCharacterIndex(multiline.Text.Length - 1);
        if (last.IsEmpty || last.Bottom > multiline.ActualHeight) errors.Add("notes: final line clipped");
        Inspect("read_only", readOnly, readOnly.FontSize, readOnly.FontFamily);
        password.Focus(); Keyboard.Focus(password);
        const string testPassword = "test-password";
        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, password, testPassword));
        Pump();
        if (password.Password != testPassword) errors.Add("password: input was changed or rejected");
        Inspect("password", password, password.FontSize, password.FontFamily);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(outputDirectory, "input-fields.png"))) png.Save(stream);
        File.WriteAllText(Path.Combine(outputDirectory, "metrics.json"), JsonSerializer.Serialize(new { metrics, errors }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close(); app.Shutdown();
        foreach (var error in errors) Console.Error.WriteLine("FAIL " + error);
        if (errors.Count == 0) Console.WriteLine("PASS Actual QR/stock controls and shared text/password/combo fields accept and fully display input");
        return errors.Count == 0 ? 0 : 1;

        void Add(string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
            control.Width = 500; panel.Children.Add(control);
        }

        void Inspect(string name, Control control, double fontSize, FontFamily font)
        {
            var host = (ScrollViewer)control.Template.FindName("PART_ContentHost", control);
            var lineHeight = font.LineSpacing * fontSize;
            metrics.Add(new { name, height = control.ActualHeight, padding = control.Padding.ToString(), viewport = host.ViewportHeight, lineHeight });
            Console.WriteLine($"{name}: field={control.ActualHeight:0.##}, viewport={host.ViewportHeight:0.##}, line={lineHeight:0.##}");
            if (host.ViewportHeight + 1 < lineHeight) errors.Add(name + ": text viewport is shorter than one full line");
        }
    }

    private static TextBox Detach(TextBox box)
    {
        ((Panel)box.Parent).Children.Remove(box);
        return box;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
