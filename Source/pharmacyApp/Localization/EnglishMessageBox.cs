using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace pharmacyApp.Localization;

/// <summary>App-owned dialogs use English buttons independently of the Windows display language.</summary>
public static class EnglishMessageBox
{
    public static MessageBoxResult Show(string message, string caption = "Pharmacy", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None) => Show(null, message, caption, buttons, image, defaultResult, options);

    public static MessageBoxResult Show(Window? owner, string message, string caption = "Pharmacy", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
            return Application.Current.Dispatcher.Invoke(() => Show(owner, message, caption, buttons, image, defaultResult, options));
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        var dialog = new Window
        {
            Title = UiText.Translate(caption), Width = 560, MinWidth = 420, MaxHeight = 700,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false, Background = Brushes.White, Language = System.Windows.Markup.XmlLanguage.GetLanguage("en-GB")
        };
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        var content = new Grid { Margin = new Thickness(20) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var kind = image switch { MessageBoxImage.Error => "Error", MessageBoxImage.Warning => "Warning", MessageBoxImage.Question => "Confirmation", _ => "" };
        content.Children.Add(new TextBlock { Text = kind, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,10), Visibility = kind.Length == 0 ? Visibility.Collapsed : Visibility.Visible });
        var body = new TextBox
        {
            Text = UiText.Translate(message), IsReadOnly = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, Padding = new Thickness(0),
            MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(body, 1); content.Children.Add(body);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        Grid.SetRow(actions, 2); content.Children.Add(actions);
        var choices = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            _ => new[] { MessageBoxResult.OK }
        };
        var result = MessageBoxResult.None;
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice.ToString(), MinWidth = 86, Height = 32, Margin = new Thickness(8,0,0,0),
                IsDefault = choice == (defaultResult == MessageBoxResult.None ? choices[0] : defaultResult),
                IsCancel = choice == MessageBoxResult.Cancel || buttons == MessageBoxButton.OK
            };
            button.Click += (_, _) => { result = choice; dialog.Close(); };
            actions.Children.Add(button);
        }
        dialog.Closing += (_, e) =>
        {
            if (result != MessageBoxResult.None) return;
            if (buttons == MessageBoxButton.YesNo) { e.Cancel = true; return; }
            result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        };
        dialog.Content = content;
        dialog.ShowDialog();
        return result;
    }
}
