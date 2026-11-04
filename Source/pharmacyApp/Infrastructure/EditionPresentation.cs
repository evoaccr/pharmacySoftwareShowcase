using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace pharmacyApp.Infrastructure;

public static class EditionPresentation
{
    public const string ItsNotice = "ITS Disabled due to MIT version";

    public static void Initialize() => EventManager.RegisterClassHandler(typeof(Window),
        FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var window = (Window)sender;
            // WPF finishes its own caption styles after class Loaded handlers.
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => RemoveWindowIcon(window)));
        }));

    public static void AddItsNotice(UserControl view)
    {
        if (view.Content is not UIElement content || content is DockPanel { Tag: "ItsDisabledLayout" }) return;
        view.Content = null;
        var layout = new DockPanel { Tag = "ItsDisabledLayout", LastChildFill = true };
        var notice = new TextBlock
        {
            Name = "ItsDisabledNotice", Text = ItsNotice, Foreground = Brushes.Red,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 8, 16, 0)
        };
        DockPanel.SetDock(notice, Dock.Top);
        layout.Children.Add(notice);
        layout.Children.Add(content);
        view.Content = layout;
    }

    public static void RemoveWindowIcon(Window window)
    {
        if (window.WindowStyle == WindowStyle.None) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        const int extendedStyle = -20;
        const long dialogFrame = 1;
        var style = GetWindowLongPtr(handle, extendedStyle).ToInt64();
        if ((style & dialogFrame) != 0) return;
        SetWindowLongPtr(handle, extendedStyle, new IntPtr(style | dialogFrame));
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
        SendMessage(handle, 0x0080, IntPtr.Zero, IntPtr.Zero);
        SendMessage(handle, 0x0080, new IntPtr(1), IntPtr.Zero);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
