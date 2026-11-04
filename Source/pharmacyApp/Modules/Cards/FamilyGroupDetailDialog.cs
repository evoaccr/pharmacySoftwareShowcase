using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pharmacyApp.Modules.Cards;

internal sealed class FamilyGroupDetailDialog : Window
{
    public FamilyGroupDetailDialog(FamilyGroupDetail detail)
    {
        Title = $"Aile Grubu | {detail.Name}";
        Width = 520;
        Height = 480;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(243, 245, 247));
        Content = BuildContent(detail);
    }

    private static FrameworkElement BuildContent(FamilyGroupDetail detail)
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = detail.Name,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(39, 52, 62)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        var elder = new TextBlock
        {
            Text = $"Aile Büyüğü: {detail.MainPerson?.Name ?? "Belirtilmemiş"}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(elder, 1);
        root.Children.Add(elder);

        var members = new ListBox { ItemsSource = detail.Members, DisplayMemberPath = nameof(CardLookupOption.Name) };
        Grid.SetRow(members, 2);
        root.Children.Add(members);

        var close = new Button
        {
            Content = "Kapat", Width = 90, Height = 34,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        close.Click += (_, _) => Window.GetWindow(close)?.Close();
        Grid.SetRow(close, 3);
        root.Children.Add(close);
        return root;
    }
}
