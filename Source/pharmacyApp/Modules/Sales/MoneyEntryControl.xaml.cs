using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace pharmacyApp.Modules.Sales;

public partial class MoneyEntryControl : UserControl
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private bool synchronizing;

    public MoneyEntryControl()
    {
        InitializeComponent();
    }

    public event EventHandler? AmountChanged;

    public decimal Amount
    {
        get => MoneyAmountParts.Compose(WholePartBox.Text, DecimalPartBox.Text);
        set
        {
            var parts = MoneyAmountParts.Split(value);
            synchronizing = true;
            try
            {
                WholePartBox.Text = parts.Whole;
                DecimalPartBox.Text = parts.Decimal;
            }
            finally
            {
                synchronizing = false;
            }
        }
    }

    private void OnWholePartPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var movesToDecimal = e.Key is Key.Decimal or Key.OemComma or Key.OemPeriod ||
                             e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        if (!movesToDecimal) return;
        FocusDecimalPart();
        e.Handled = true;
    }

    private void OnWholePartPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text is "." or ",")
        {
            FocusDecimalPart();
            e.Handled = true;
            return;
        }

        e.Handled = !MoneyAmountParts.ContainsOnlyDigits(e.Text);
    }

    private void OnDecimalPartPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !MoneyAmountParts.ContainsOnlyDigits(e.Text);

    private void OnPartTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!synchronizing) AmountChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPartGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox) textBox.SelectAll();
    }

    private void OnDecimalPartLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var normalized = DecimalPartBox.Text.Length switch
        {
            0 => "00",
            1 => DecimalPartBox.Text + "0",
            _ => DecimalPartBox.Text
        };
        if (normalized == DecimalPartBox.Text) return;
        synchronizing = true;
        DecimalPartBox.Text = normalized;
        synchronizing = false;
    }

    private void OnWholePartPasting(object sender, DataObjectPastingEventArgs e)
    {
        var pasted = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        if (MoneyAmountParts.ContainsOnlyDigits(pasted)) return;
        if (!decimal.TryParse(pasted, NumberStyles.Number, TurkishCulture, out var amount) &&
            !decimal.TryParse(pasted, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            e.CancelCommand();
            return;
        }

        Amount = amount;
        AmountChanged?.Invoke(this, EventArgs.Empty);
        e.CancelCommand();
    }

    private void OnDecimalPartPasting(object sender, DataObjectPastingEventArgs e)
    {
        var pasted = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        if (pasted.Length is < 1 or > 2 || !MoneyAmountParts.ContainsOnlyDigits(pasted)) e.CancelCommand();
    }

    private void FocusDecimalPart()
    {
        DecimalPartBox.Focus();
        DecimalPartBox.SelectAll();
    }
}
