using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Reflection;
using pharmacyApp.Localization;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var culture = CultureInfo.CurrentCulture;
        var app = new Application();
        EnglishUi.Initialize();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/pharmacyApp;component/Themes/Workbench.xaml", UriKind.Relative)
        });
        Assert(CultureInfo.CurrentCulture.Equals(culture), "Data/parsing culture remains unchanged");
        Assert(UiText.Translate("Hasta") == "Patient", "Static labels translate");
        Assert(UiText.Translate("EMEKLİ") == "Retired" && UiText.Translate("Çalışan") == "Employed", "Patient types use consistent sentence case");
        Assert(UiText.Translate("Satışlar") == "Sales", "Sales navigation uses title case");
        Assert(UiText.Translate("Karekod Hareketleri") == "QR Movements", "QR movement navigation uses the requested terminology");
        Assert(UiText.Translate("Karekod") == "QR Code", "Medicine codes use QR terminology");
        Assert(UiText.Translate("Takas Girişi") == "Incoming Trade" && UiText.Translate("Takas Çıkışı") == "Outgoing Trade", "Incoming and outgoing pharmacy trades use Trade");
        Assert(UiText.Translate("Depo") == "Warehouse (Depot)" && UiText.Translate("Warehouse") == "Warehouse (Depot)", "Warehouse captions include Depot");
        Assert(UiText.Translate("Havale") == "Bank Transfer", "Bank Transfer retains its payment meaning");
        var inline = new Run("Assistant");
        var inlineLabel = new TextBlock(inline);
        var inlineButton = new Button { Content = inlineLabel };
        var findButton = typeof(pharmacyApp.MainWindow).GetMethod("FindAncestor", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(Button));
        Assert(ReferenceEquals(findButton.Invoke(null, new object[] { inline }), inlineButton), "Panel hit testing follows inline text to its button ancestor");
        Assert(findButton.Invoke(null, new object[] { new Run("Detached title") }) is null, "Detached inline title hit testing safely returns no ancestor");
        Assert(UiText.Translate("Ad *") == "Name *", "Required field markers are retained");
        Assert(UiText.Translate("T.C. Kimlik No *") == "National ID No. *", "Composed identity labels translate");
        Assert(UiText.Translate("Fotoğraf okunamadı: disk unavailable") == "Could not read photo: disk unavailable", "Interpolated errors retain details");
        Assert(UiText.Translate("Reçete No: 12345") == "Prescription No.: 12345", "Concatenated labels retain identifiers");
        Assert(UiText.Translate("2 ay") == "2 months", "Dynamic stock-duration units translate");
        Assert(UiText.Translate("Hasta Ekle") == "Add Patient", "Composed action captions translate");
        Assert(UiText.Translate("Ödeme toplamı: 25,50 ₺ / 30,00 ₺") == "Payment total: TRY 25,50 / TRY 30,00", "Payment amounts are preserved exactly");
        Assert(UiText.Translate("İstanbul / Çağrı Öztürk / 8690123456789") == "İstanbul / Çağrı Öztürk / 8690123456789", "Names and barcode data remain intact");
        Assert(UiText.Translate("Unknown server response 519") == "Unknown server response 519", "Unknown source data is preserved");
        var formatted = new TextBlock();
        var titleRun = new Run("Ayarlar") { FontWeight = FontWeights.Bold };
        var warningRun = new Run("Hasta") { Foreground = System.Windows.Media.Brushes.Red };
        var nestedRun = new Run("Hazır");
        var nested = new Bold(nestedRun);
        formatted.Inlines.Add(titleRun); formatted.Inlines.Add(warningRun); formatted.Inlines.Add(nested);
        EnglishUi.Apply(formatted);
        Assert(titleRun.Text == "Settings" && warningRun.Text == "Patient" && nestedRun.Text == "Ready",
            "Formatted and nested runs translate without invalidating their live collection enumerators");
        Assert(formatted.Inlines.Count == 3 && ReferenceEquals(formatted.Inlines.FirstInline, titleRun) &&
            ReferenceEquals(warningRun.Foreground, System.Windows.Media.Brushes.Red) && titleRun.FontWeight == FontWeights.Bold,
            "Mixed translated titles preserve their original runs, emphasis and red notices");
        var source = new Source { Value = "Hazır" };
        var block = new TextBlock();
        block.SetBinding(TextBlock.TextProperty, new Binding(nameof(Source.Value)) { Source = source, Mode = BindingMode.OneWay });
        block.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert(block.Text == "Ready" && source.Value == "Hazır", "Bound display is English without changing its source");
        source.Value = "İptal Edildi";
        Assert(block.Text == "Cancelled" && source.Value == "İptal Edildi", "Live bound updates translate without rewriting the model");
        block.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var editor = new TextBox { Text = "Hayır" };
        EnglishUi.Apply(editor);
        Assert(editor.Text == "Hayır", "Editable input remains untouched");
        var choice = new ComboBox { ItemsSource = new[] { "Hayır", "Evet" }, SelectedIndex = 0 };
        choice.Measure(new Size(200,40)); choice.Arrange(new Rect(0,0,200,40)); choice.UpdateLayout();
        EnglishUi.Apply(choice);
        Assert((string)choice.SelectedItem == "Hayır", "Selected integration values remain unchanged");
        var readOnly = new TextBox { IsReadOnly = true };
        readOnly.SetBinding(TextBox.TextProperty, new Binding(nameof(Source.Value)) { Source = source, Mode = BindingMode.OneWay });
        EnglishUi.Apply(readOnly);
        Assert(readOnly.Text == "Cancelled" && source.Value == "İptal Edildi", "Read-only status binding retains original data");
        var liveLabel = new TextBlock { Text = "Asistan" };
        var livePanel = new StackPanel();
        livePanel.Children.Add(liveLabel);
        var menuItem = new MenuItem { Header = "Hasta" };
        var menu = new ContextMenu(); menu.Items.Add(menuItem);
        livePanel.ContextMenu = menu;
        var window = new Window { Title = "English UI verification", Content = livePanel, Width = 320, Height = 180, ShowInTaskbar = false };
        window.Show();
        Pump();
        Assert(liveLabel.Text == "Assistant", "A real window localizes without manual Apply or Loaded calls");
        liveLabel.Text = "Hazır";
        Assert(liveLabel.Text == "Ready", "Real window text updates remain English");
        var later = new TextBlock { Text = "Hasta", ToolTip = "Ayarlar" };
        livePanel.Children.Add(later);
        Pump();
        Assert(later.Text == "Patient" && (string)later.ToolTip == "Settings", "New visual children and tooltips localize after layout");
        menu.PlacementTarget = livePanel; menu.IsOpen = true;
        Pump();
        Assert(AutomationProperties.GetName(menuItem) == "Patient" && (string)menuItem.Header == "Hasta", "Popup menu captions localize without changing command values");
        menu.IsOpen = false;
        var settings = new pharmacyApp.Modules.Settings.DatabaseSettingsView();
        window.Content = settings; window.Width = 1200; window.Height = 800;
        Pump();
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        for (var pass = 0; pass < 2; pass++)
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                ((TabItem)tabs.Items[index]).Focus();
                window.UpdateLayout(); Pump();
            }
        Assert(tabs.Items.Count == 5, "All settings sections can be opened repeatedly with live English localization");
        foreach (var name in new[] { "MedulaSettingsTitle", "ItsSettingsTitle", "EczaciKartSettingsTitle", "SelcukSettingsTitle", "BekSettingsTitle", "IskoopSettingsTitle", "AllianceSettingsTitle", "DatabaseSettingsTitle" })
        {
            var title = (TextBlock)settings.FindName(name);
            Assert(title.Inlines.OfType<Run>().Last().Foreground is System.Windows.Media.SolidColorBrush { Color: var red } && red == System.Windows.Media.Colors.Red,
                "Red title notice survives settings tab switching: " + name);
        }
        window.Close();
        app.Shutdown();
        Console.WriteLine("PASS English UI localization and binding isolation");
        return 0;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Assert(bool result, string name)
    {
        if (!result) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
    private sealed class Source : INotifyPropertyChanged
    {
        private string value = "";
        public string Value { get => value; set { this.value=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
