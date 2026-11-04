using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pharmacyApp.Infrastructure;

public static class ModernDialog
{
    public static Task<bool> ConfirmAsync(FrameworkElement source, string title, string message) =>
        ShowAsync(source, title, message, true);

    public static Task ShowInfoAsync(FrameworkElement source, string title, string message) =>
        ShowAsync(source, title, message, false);

    private static Task<bool> ShowAsync(FrameworkElement source, string title, string message, bool confirmation)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            MinHeight = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.White,
            Owner = Window.GetWindow(source)
        };

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(32, 50, 60))
        });
        var body = new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 14, 0, 22),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(70, 87, 96))
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirmation)
        {
            var cancel = CreateButton("Vazgeç", false);
            cancel.Margin = new Thickness(0, 0, 10, 0);
            cancel.Click += (_, _) => { completion.TrySetResult(false); window.Close(); };
            buttons.Children.Add(cancel);
        }
        var accept = CreateButton(confirmation ? "Devam Et" : "Tamam", true);
        accept.Click += (_, _) => { completion.TrySetResult(true); window.Close(); };
        buttons.Children.Add(accept);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        window.Content = root;
        window.Closed += (_, _) => completion.TrySetResult(false);
        window.Show();
        return completion.Task;
    }

    private static Button CreateButton(string text, bool primary) => new()
    {
        Content = text,
        MinWidth = 105,
        Height = 38,
        Padding = new Thickness(16, 0, 16, 0),
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(primary ? Color.FromRgb(22, 139, 82) : Color.FromRgb(231, 237, 239)),
        Foreground = new SolidColorBrush(primary ? Colors.White : Color.FromRgb(51, 70, 79)),
        FontWeight = FontWeights.SemiBold
    };
}
