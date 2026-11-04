using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace pharmacyApp.Modules.Cards;

internal sealed class PatientDebtCell : Grid
{
    public PatientDebtCell()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var permission = new Border { Padding = new Thickness(5, 0, 5, 0) };
        permission.SetBinding(BackgroundProperty, new Binding("[AllowDebt]") { Converter = AllowDebtBrushConverter.Instance });
        var permissionText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        permissionText.SetBinding(TextBlock.TextProperty, new Binding("[AllowDebt]") { Converter = AllowDebtSymbolConverter.Instance });
        permission.Child = permissionText;
        Children.Add(permission);

        var amount = new Border { Padding = new Thickness(8, 0, 8, 0) };
        amount.SetBinding(BackgroundProperty, new Binding("[DebtAmount]") { Converter = DebtAmountBrushConverter.Instance });
        var amountText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        amountText.SetBinding(TextBlock.TextProperty, new Binding("[DebtAmount]") { StringFormat = "N2" });
        amountText.SetBinding(TextBlock.ForegroundProperty, new Binding("[DebtAmount]") { Converter = DebtAmountForegroundConverter.Instance });
        amount.Child = amountText;
        Grid.SetColumn(amount, 1);
        Children.Add(amount);
    }
}

internal sealed class AllowDebtBrushConverter : IValueConverter
{
    public static AllowDebtBrushConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush(value is true ? Color.FromRgb(213, 241, 224) : Color.FromRgb(250, 218, 218));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed class AllowDebtSymbolConverter : IValueConverter
{
    public static AllowDebtSymbolConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "✓" : "✕";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed class DebtAmountBrushConverter : IValueConverter
{
    public static DebtAmountBrushConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var debt = Math.Max(0m, System.Convert.ToDecimal(value ?? 0m, CultureInfo.InvariantCulture));
        if (debt == 0) return Brushes.White;
        if (debt <= 3000m) return BrushBetween(Color.FromRgb(255, 245, 245), Color.FromRgb(225, 35, 35), (double)(debt / 3000m));
        return BrushBetween(Color.FromRgb(225, 35, 35), Color.FromRgb(10, 10, 10), Math.Min(1d, (double)((debt - 3000m) / 3000m)));
    }

    private static SolidColorBrush BrushBetween(Color from, Color to, double ratio) => new(Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * ratio),
        (byte)Math.Round(from.G + (to.G - from.G) * ratio),
        (byte)Math.Round(from.B + (to.B - from.B) * ratio)));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed class DebtAmountForegroundConverter : IValueConverter
{
    public static DebtAmountForegroundConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Convert.ToDecimal(value ?? 0m, CultureInfo.InvariantCulture) >= 1800m ? Brushes.White : new SolidColorBrush(Color.FromRgb(39, 52, 62));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
