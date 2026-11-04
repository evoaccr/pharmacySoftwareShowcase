using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace pharmacyApp.Modules.Cards;

public sealed record CardLookupOption(string Key, string Name);

internal class PlaceholderTextEditor : Grid
{
    private readonly TextBlock placeholderText;

    public PlaceholderTextEditor(string placeholder, double height = 34, bool multiline = false)
    {
        Height = height;
        Input = new TextBox
        {
            Height = height,
            Padding = new Thickness(9, 5, 9, 5),
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        placeholderText = new TextBlock
        {
            Text = placeholder,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 154, 162)),
            Margin = new Thickness(10, multiline ? 7 : 0, 8, 0),
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Children.Add(Input);
        Children.Add(placeholderText);
        Input.TextChanged += (_, _) => placeholderText.Visibility =
            string.IsNullOrEmpty(Input.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    public TextBox Input { get; }
    public virtual string Text
    {
        get => Input.Text;
        set => Input.Text = value;
    }

    public event TextChangedEventHandler TextChanged
    {
        add => Input.TextChanged += value;
        remove => Input.TextChanged -= value;
    }

    public void FocusInput() => Input.Focus();
    public virtual void SetEditing(bool editing) => Input.IsReadOnly = !editing;
}

internal sealed class WebsiteTextEditor : PlaceholderTextEditor
{
    private bool changing;

    public WebsiteTextEditor() : base("www.")
    {
        Input.GotKeyboardFocus += (_, _) => EnsurePrefix();
        Input.TextChanged += (_, _) =>
        {
            if (changing || string.IsNullOrEmpty(Input.Text)) return;
            EnsurePrefix();
        };
    }

    public override string Text
    {
        get => Input.Text == "www." ? "" : Input.Text;
        set => Input.Text = string.IsNullOrWhiteSpace(value) ? "" : value;
    }

    private void EnsurePrefix()
    {
        if (Input.Text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return;
        changing = true;
        Input.Text = "www." + Input.Text.TrimStart();
        Input.CaretIndex = Input.Text.Length;
        changing = false;
    }
}

internal sealed class PhoneTextEditor : PlaceholderTextEditor
{
    private bool formatting;

    public PhoneTextEditor() : base("0(___) ___ __ __") => Input.TextChanged += OnPhoneTextChanged;

    public string? GetValidatedValue(string label)
    {
        var digits = Digits(Input.Text);
        if (digits.Length == 0) return null;
        if (digits.Length == 10 && digits[0] != '0') digits = "0" + digits;
        if (digits.Length != 11 || digits[0] != '0')
            throw new InvalidOperationException($"{label} 0(___) ___ __ __ biçiminde 11 rakam olmalıdır.");
        return FormatDigits(digits);
    }

    public static string FormatStoredValue(string value)
    {
        return TryNormalizeStoredValue(value) ?? value.Trim();
    }

    public static string? TryNormalizeStoredValue(string value)
    {
        var digits = Digits(value);
        if (digits.Length == 12 && digits.StartsWith("90", StringComparison.Ordinal)) digits = "0" + digits[2..];
        if (digits.Length == 10 && digits[0] != '0') digits = "0" + digits;
        return digits.Length == 11 && digits[0] == '0' ? FormatDigits(digits) : null;
    }

    private void OnPhoneTextChanged(object sender, TextChangedEventArgs e)
    {
        if (formatting) return;
        var digits = Digits(Input.Text);
        if (digits.Length > 0 && digits[0] != '0') digits = "0" + digits;
        if (digits.Length > 11) digits = digits[..11];
        var formatted = FormatDigits(digits);
        if (formatted == Input.Text) return;
        formatting = true;
        Input.Text = formatted;
        Input.CaretIndex = formatted.Length;
        formatting = false;
    }

    private static string Digits(string value) => Regex.Replace(value ?? "", @"\D", "");

    private static string FormatDigits(string digits)
    {
        if (digits.Length == 0) return "";
        var result = new StringBuilder();
        result.Append(digits[0]);
        if (digits.Length > 1)
        {
            result.Append('(');
            result.Append(digits.AsSpan(1, Math.Min(3, digits.Length - 1)));
        }
        if (digits.Length > 4)
        {
            result.Append(") ");
            result.Append(digits.AsSpan(4, Math.Min(3, digits.Length - 4)));
        }
        if (digits.Length > 7)
        {
            result.Append(' ');
            result.Append(digits.AsSpan(7, Math.Min(2, digits.Length - 7)));
        }
        if (digits.Length > 9)
        {
            result.Append(' ');
            result.Append(digits.AsSpan(9, Math.Min(2, digits.Length - 9)));
        }
        return result.ToString();
    }
}

internal sealed class LookupEditorControl : ComboBox
{
    private bool suppressEvents;
    private int searchVersion;
    private string? selectedKey;

    public LookupEditorControl()
    {
        Height = 34;
        IsEditable = true;
        IsTextSearchEnabled = false;
        StaysOpenOnEdit = true;
        DisplayMemberPath = nameof(CardLookupOption.Name);
        MaxDropDownHeight = 34;
        SelectionChanged += OnSelectionChanged;
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
    }

    public event EventHandler? ValueChanged;
    public Func<string, Task<IReadOnlyList<CardLookupOption>>>? SearchRequested { get; set; }
    public string? SelectedKey => selectedKey;
    public string StateValue => selectedKey is null ? $"text:{Text}" : $"key:{selectedKey}";

    public void SetOptions(IEnumerable<CardLookupOption> options)
    {
        var items = options.ToList();
        ItemsSource = items;
        MaxDropDownHeight = items.Count == 0 ? 34 : 220;
    }

    public void SetSelectedOption(CardLookupOption? option, string? unresolvedKey = null)
    {
        suppressEvents = true;
        selectedKey = option?.Key ?? (string.IsNullOrWhiteSpace(unresolvedKey) ? null : unresolvedKey);
        SetOptions(option is null ? [] : [option]);
        SelectedItem = option;
        Text = option?.Name ?? (selectedKey is null ? "" : "Eşleşen kayıt bulunamadı");
        suppressEvents = false;
    }

    public string? GetSelectedKeyOrThrow(string label)
    {
        if (string.IsNullOrWhiteSpace(Text)) return null;
        if (selectedKey is not null) return selectedKey;
        if (Items.Cast<CardLookupOption>().FirstOrDefault(option =>
                string.Equals(option.Name, Text.Trim(), StringComparison.CurrentCultureIgnoreCase)) is { } exact)
            return exact.Key;
        throw new InvalidOperationException($"{label} en az üç harf yazılarak listeden seçilmelidir.");
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressEvents) return;
        if (SelectedItem is CardLookupOption option)
        {
            selectedKey = option.Key;
            suppressEvents = true;
            Text = option.Name;
            suppressEvents = false;
        }
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressEvents || !IsKeyboardFocusWithin) return;
        var typed = Text;
        if (SelectedItem is not CardLookupOption selected ||
            !string.Equals(selected.Name, typed, StringComparison.CurrentCultureIgnoreCase))
            selectedKey = null;

        var version = ++searchVersion;
        if (typed.Trim().Length < 3 || SearchRequested is null)
        {
            suppressEvents = true;
            SetOptions([]);
            Text = typed;
            IsDropDownOpen = false;
            suppressEvents = false;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var results = await SearchRequested(typed.Trim());
        if (version != searchVersion) return;
        suppressEvents = true;
        SetOptions(results);
        Text = typed;
        IsDropDownOpen = true;
        suppressEvents = false;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (version != searchVersion || GetEditableTextBox() is not { } textBox) return;
            textBox.Select(textBox.Text.Length, 0);
        });
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private TextBox? GetEditableTextBox() =>
        Template.FindName("PART_EditableTextBox", this) as TextBox;
}

internal sealed class FamilyGroupEditorControl : Grid
{
    public FamilyGroupEditorControl()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Lookup = new LookupEditorControl();
        AddButton = new Button
        {
            Content = "+", Width = 34, Height = 34, Margin = new Thickness(6, 0, 0, 0),
            FontSize = 18, FontWeight = FontWeights.SemiBold, ToolTip = "Yeni aile grubu ekle"
        };
        Children.Add(Lookup);
        Grid.SetColumn(AddButton, 1);
        Children.Add(AddButton);
        Lookup.ValueChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
        AddButton.Click += (_, _) => NewGroupRequested?.Invoke(this, EventArgs.Empty);
    }

    public LookupEditorControl Lookup { get; }
    public Button AddButton { get; }
    public event EventHandler? ValueChanged;
    public event EventHandler? NewGroupRequested;
    public string StateValue => Lookup.StateValue;
    public string? GetSelectedKeyOrThrow(string label) => Lookup.GetSelectedKeyOrThrow(label);
    public void SetSelectedOption(CardLookupOption? option, string? unresolvedKey = null) => Lookup.SetSelectedOption(option, unresolvedKey);
    public void SetEditing(bool editing) { Lookup.IsEnabled = editing; AddButton.IsEnabled = editing; }
}

internal sealed class DebtAmountEditorControl : Grid
{
    public DebtAmountEditorControl()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AmountEditor = new PlaceholderTextEditor("");
        AmountEditor.Input.IsReadOnly = true;
        AdjustButton = new Button
        {
            Width = 34,
            Height = 34,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Borç miktarını değiştir",
            Content = new Path
            {
                Data = Geometry.Parse("M3,14.25 V17 H5.75 L13.81,8.94 L11.06,6.19 Z M14.71,8.04 L11.96,5.29 L13.13,4.12 C13.52,3.73 14.15,3.73 14.54,4.12 L15.88,5.46 C16.27,5.85 16.27,6.48 15.88,6.87 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(46, 77, 91)),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform
            }
        };
        Children.Add(AmountEditor);
        Grid.SetColumn(AdjustButton, 1);
        Children.Add(AdjustButton);
        AdjustButton.Click += (_, _) => AdjustmentRequested?.Invoke(this, EventArgs.Empty);
    }

    public PlaceholderTextEditor AmountEditor { get; }
    public Button AdjustButton { get; }
    public string Text { get => AmountEditor.Text; set => AmountEditor.Text = value; }
    public string StateValue => Text;
    public event EventHandler? AdjustmentRequested;

    public void SetEditing(bool editing)
    {
        AmountEditor.Input.IsReadOnly = true;
        AdjustButton.IsEnabled = editing;
    }
}

internal sealed class MultiValueEditorControl : Grid
{
    private readonly ObservableCollection<string> values = [];
    private readonly CardFieldKind kind;
    private readonly PhonePurposeProfile purposeProfile;
    private readonly string? defaultPurpose;
    private readonly ComboBox primaryBox;
    private readonly Button plusButton;
    private readonly Grid editPanel;
    private readonly StackPanel viewPanel;
    private readonly Grid addPanel;
    private readonly PlaceholderTextEditor valueEditor;
    private readonly ComboBox? purposeBox;
    private bool suppressEvents;

    public MultiValueEditorControl(CardFieldKind kind, PhonePurposeProfile purposeProfile, string? defaultPurpose)
    {
        this.kind = kind;
        this.purposeProfile = purposeProfile;
        this.defaultPurpose = defaultPurpose;
        var purposeOptions = PurposeOptions(purposeProfile);

        editPanel = new Grid();
        editPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var primaryRow = new Grid();
        primaryRow.ColumnDefinitions.Add(new ColumnDefinition());
        primaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        primaryBox = new ComboBox
        {
            Height = 34,
            ItemsSource = values,
            MaxDropDownHeight = 34,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        primaryBox.SelectionChanged += OnPrimarySelectionChanged;
        plusButton = new Button
        {
            Content = "+", Width = 34, Height = 34, Margin = new Thickness(6, 0, 0, 0),
            FontSize = 18, FontWeight = FontWeights.SemiBold, ToolTip = "Yeni kayıt ekle"
        };
        plusButton.Click += OnPlusClicked;
        primaryRow.Children.Add(primaryBox);
        Grid.SetColumn(plusButton, 1);
        primaryRow.Children.Add(plusButton);
        editPanel.Children.Add(primaryRow);

        addPanel = new Grid { Margin = new Thickness(0, 7, 0, 0), Visibility = Visibility.Collapsed };
        addPanel.ColumnDefinitions.Add(new ColumnDefinition());
        if (purposeOptions.Count > 0) addPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        addPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        valueEditor = kind is CardFieldKind.PhoneList ? new PhoneTextEditor() :
            new PlaceholderTextEditor(kind == CardFieldKind.EmailList ? "E-posta" : "Değer");
        addPanel.Children.Add(valueEditor);
        if (purposeOptions.Count > 0)
        {
            purposeBox = new ComboBox
            {
                Height = 34, Margin = new Thickness(6, 0, 0, 0), IsEditable = true,
                ItemsSource = purposeOptions, MaxDropDownHeight = 160,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(purposeBox, 1);
            addPanel.Children.Add(purposeBox);
        }
        var confirmButton = new Button
        {
            Content = "Ekle", Height = 34, MinWidth = 54, Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 3, 8, 3)
        };
        confirmButton.Click += OnConfirmClicked;
        Grid.SetColumn(confirmButton, purposeOptions.Count > 0 ? 2 : 1);
        addPanel.Children.Add(confirmButton);
        Grid.SetRow(addPanel, 1);
        editPanel.Children.Add(addPanel);

        viewPanel = new StackPanel { Visibility = Visibility.Collapsed };
        Children.Add(editPanel);
        Children.Add(viewPanel);
    }

    public event EventHandler? ValueChanged;
    public event Action<string>? ValidationFailed;
    public string[]? Values
    {
        get => values.Count == 0 ? null : values.ToArray();
        set
        {
            suppressEvents = true;
            values.Clear();
            foreach (var item in value ?? [])
            {
                var normalized = kind == CardFieldKind.PhoneList ? NormalizeStoredPhoneItem(item) : item.Trim();
                if (!string.IsNullOrWhiteSpace(normalized)) values.Add(normalized);
            }
            primaryBox.MaxDropDownHeight = values.Count == 0 ? 34 : 220;
            primaryBox.SelectedIndex = values.Count == 0 ? -1 : 0;
            suppressEvents = false;
            RebuildViewList();
        }
    }

    public void SetEditing(bool editing)
    {
        editPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        viewPanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        if (!editing) RebuildViewList();
    }

    private void OnPlusClicked(object sender, RoutedEventArgs e)
    {
        valueEditor.Text = "";
        if (purposeBox is not null)
        {
            purposeBox.SelectedIndex = -1;
            purposeBox.Text = defaultPurpose ?? "";
        }
        addPanel.Visibility = Visibility.Visible;
        valueEditor.FocusInput();
    }

    private void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string? value = kind == CardFieldKind.PhoneList
                ? ((PhoneTextEditor)valueEditor).GetValidatedValue("Telefon numarası")
                : NullIfWhiteSpace(valueEditor.Text);
            if (value is null) throw new InvalidOperationException("Eklenecek değer boş bırakılamaz.");
            if (kind == CardFieldKind.EmailList && !Regex.IsMatch(value, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
                throw new InvalidOperationException("Geçerli bir e-posta adresi girilmelidir.");
            var purpose = purposeBox is null ? null : NullIfWhiteSpace(purposeBox.Text);
            if (purposeBox is not null && purpose is null) throw new InvalidOperationException("Tür seçilmeli veya yazılmalıdır.");
            values.Add(purpose is null ? value : $"{value} {purpose}");
            primaryBox.MaxDropDownHeight = 220;
            suppressEvents = true;
            primaryBox.SelectedIndex = values.Count == 1 ? 0 : primaryBox.SelectedIndex;
            suppressEvents = false;
            addPanel.Visibility = Visibility.Collapsed;
            RebuildViewList();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ValidationFailed?.Invoke(ex.Message); }
    }

    private void OnPrimarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressEvents || primaryBox.SelectedItem is not string selected) return;
        var index = values.IndexOf(selected);
        if (index <= 0) return;
        suppressEvents = true;
        values.Move(index, 0);
        primaryBox.SelectedIndex = 0;
        suppressEvents = false;
        RebuildViewList();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildViewList()
    {
        viewPanel.Children.Clear();
        if (values.Count == 0)
        {
            viewPanel.Children.Add(new TextBlock { Text = "—", Foreground = Brushes.Gray });
            return;
        }
        for (var index = 0; index < values.Count; index++)
        {
            var (value, purpose) = SplitStoredItem(values[index]);
            var text = new TextBlock { Margin = new Thickness(2, 2, 2, 3), FontSize = 13.5 };
            var shown = purpose is null ? value : $"{purpose}: {value}";
            text.Inlines.Add(new Run(shown) { FontWeight = index == 0 ? FontWeights.Bold : FontWeights.Normal });
            viewPanel.Children.Add(text);
        }
    }

    private static IReadOnlyList<string> PurposeOptions(PhonePurposeProfile profile) => profile switch
    {
        PhonePurposeProfile.Personal => ["iş", "cep", "ev"],
        PhonePurposeProfile.Business => ["işletme", "irtibat"],
        PhonePurposeProfile.Pharmacy => ["eczane", "eleman", "eczacı"],
        _ => []
    };

    private static string? NormalizeStoredPhoneItem(string item)
    {
        var match = Regex.Match(item.Trim(), @"^(.*?\d)(?:\s+([^\d].*))?$");
        var rawValue = match.Success ? match.Groups[1].Value : item;
        var purpose = match.Success && match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
        var formatted = PhoneTextEditor.TryNormalizeStoredValue(rawValue);
        if (formatted is null) return null;
        return purpose is null ? formatted : $"{formatted} {purpose}";
    }

    private (string Value, string? Purpose) SplitStoredItem(string item)
    {
        if (kind != CardFieldKind.PhoneList) return (item, null);
        var match = Regex.Match(item, @"^(0\(\d{3}\) \d{3} \d{2} \d{2})(?: (.+))?$");
        return match.Success
            ? (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null)
            : (item, null);
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
