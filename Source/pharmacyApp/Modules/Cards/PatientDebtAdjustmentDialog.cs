using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pharmacyApp.Modules.Cards;

internal sealed class PatientDebtAdjustmentDialog : Window
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly decimal currentDebt;
    private readonly TextBox amountBox = new()
    {
        Height = 34,
        Padding = new Thickness(9, 5, 9, 5),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Button directionButton = new() { Width = 42, Height = 34, FontSize = 20, FontWeight = FontWeights.Bold };
    private bool addDebt = true;

    public PatientDebtAdjustmentDialog(decimal currentDebt)
    {
        this.currentDebt = currentDebt;
        Title = "Borç Miktarını Değiştir";
        Width = 430;
        Height = 235;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(243, 245, 247));
        Content = BuildContent();
        UpdateDirectionVisual();
        Loaded += (_, _) => amountBox.Focus();
    }

    public decimal Amount { get; private set; }
    public bool AddDebt => addDebt;

    private FrameworkElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = $"Mevcut borç: {currentDebt:N2} ₺",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(54, 69, 80)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition());
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(amountBox);
        directionButton.Margin = new Thickness(8, 0, 0, 0);
        directionButton.Click += (_, _) => { addDebt = !addDebt; UpdateDirectionVisual(); };
        Grid.SetColumn(directionButton, 1);
        inputRow.Children.Add(directionButton);
        Grid.SetRow(inputRow, 1);
        root.Children.Add(inputRow);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = "Vazgeç", Width = 88, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button
        {
            Content = "Kaydet", Width = 92, Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(15, 118, 110)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 118, 110)), Foreground = Brushes.White
        };
        save.Click += OnSave;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        return root;
    }

    private void UpdateDirectionVisual()
    {
        directionButton.Content = addDebt ? "+" : "−";
        directionButton.ToolTip = addDebt ? "Borca ekle" : "Borçtan düş";
        directionButton.Background = new SolidColorBrush(addDebt ? Color.FromRgb(24, 134, 75) : Color.FromRgb(180, 35, 24));
        directionButton.BorderBrush = directionButton.Background;
        directionButton.Foreground = Brushes.White;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(amountBox.Text.Trim(), NumberStyles.Number, TurkishCulture, out var amount)
            && !decimal.TryParse(amountBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            pharmacyApp.Localization.EnglishMessageBox.Show("Sıfırdan büyük geçerli bir miktar giriniz.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show("Sıfırdan büyük geçerli bir miktar giriniz.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!addDebt && amount > currentDebt)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show("Borçtan düşülecek miktar mevcut borçtan fazla olamaz.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Amount = amount;
        DialogResult = true;
    }
}
