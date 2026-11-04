using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Npgsql;

namespace pharmacyApp.Modules.Cards;

public partial class CardEditorView : UserControl, IModuleTitleSource, IModuleIdentitySource, IModuleCloseRequestSource
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly SolidColorBrush ActiveSaveBrush = new(Color.FromRgb(15, 118, 110));
    private static readonly SolidColorBrush InactiveSaveBrush = new(Color.FromRgb(83, 100, 111));
    private readonly CardEntityDefinition definition;
    private readonly CardRepository repository = new();
    private readonly Action changed;
    private readonly bool startEditing;
    private readonly IReadOnlyDictionary<string, object?> initialAddValues;
    private readonly Dictionary<string, FrameworkElement> editors = new(StringComparer.Ordinal);
    private IReadOnlyList<CardFieldDefinition> visibleFields = [];
    private Dictionary<string, object?> cleanEditorState = new(StringComparer.Ordinal);
    private CancellationTokenSource? operationCancellation;
    private string? originalKey;
    private string displayName;
    private string currentModuleTitle;
    private byte[]? photoBytes;
    private Image? photoPreview;
    private Button? photoChooseButton;
    private bool initialized;
    private bool suppressChangeTracking = true;
    private bool isDirty;
    private bool isEditing;
    private bool operationInProgress;

    public CardEditorView(
        CardEntityDefinition definition,
        string? key,
        string? initialDisplayName,
        Action changed,
        bool startEditing = true,
        IReadOnlyDictionary<string, object?>? initialAddValues = null)
    {
        this.definition = definition;
        originalKey = key;
        displayName = string.IsNullOrWhiteSpace(initialDisplayName) ? definition.SingularName : initialDisplayName;
        this.changed = changed;
        this.startEditing = startEditing || key is null;
        this.initialAddValues = initialAddValues is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(initialAddValues, StringComparer.Ordinal);
        isEditing = this.startEditing;
        currentModuleTitle = key is null ? definition.AddTitle : isEditing ? BuildEditTitle(displayName) : BuildViewTitle(displayName);
        InitializeComponent();
        PreviewTextInput += OnViewModeTextInput;
        RebuildForm();
        UpdateModeVisuals();
        UpdateSaveVisuals();
    }

    public string CurrentModuleTitle => currentModuleTitle;
    public event Action<string>? ModuleTitleChanged;
    public string CurrentModuleId => originalKey is null ? $"{definition.ModuleId}.add" : $"{definition.ModuleId}.edit.{originalKey}";
    public event Action<string>? ModuleIdentityChanged;
    public event Action? CloseRequested;
    public event Action<string, string>? RecordSaved;
    public event Action<string>? RecordDeleted;

    private void RebuildForm()
    {
        suppressChangeTracking = true;
        editors.Clear();
        photoBytes = null;
        photoPreview = null;
        photoChooseButton = null;
        visibleFields = definition.FieldsFor(originalKey is null);
        FormContentHost.Content = BuildForm();
        ApplyAddDefaults();
        ApplyEditingState();
    }

    private FrameworkElement BuildForm()
    {
        var photoField = definition.IsPatients ? null : visibleFields.FirstOrDefault(field => field.Kind == CardFieldKind.Photo);
        var sectionNames = visibleFields
            .Where(field => field.Kind != CardFieldKind.Photo && !string.IsNullOrWhiteSpace(field.Section))
            .Select(field => field.Section!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var sectionControls = sectionNames.ToDictionary(
            section => section,
            section => BuildSection(section, visibleFields.Where(field => field.Section == section).ToList()),
            StringComparer.Ordinal);
        var standalone = visibleFields
            .Where(field => field.Kind != CardFieldKind.Photo && string.IsNullOrWhiteSpace(field.Section))
            .ToList();

        if (definition.IsPatients)
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel { Margin = new Thickness(2, 2, 12, 2) };
            if (photoField is not null) left.Children.Add(BuildPhotoEditor(photoField));
            if (sectionControls.Remove("Kimlik", out var identity)) left.Children.Add(identity);
            root.Children.Add(left);
            var right = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var section in sectionNames.Where(sectionControls.ContainsKey)) right.Children.Add(sectionControls[section]);
            foreach (var field in standalone) right.Children.Add(BuildSingleField(field));
            Grid.SetColumn(right, 1);
            root.Children.Add(right);
            return root;
        }

        var standardRoot = new Grid();
        standardRoot.ColumnDefinitions.Add(new ColumnDefinition());
        standardRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sections = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var section in sectionNames) sections.Children.Add(sectionControls[section]);
        foreach (var field in standalone) sections.Children.Add(BuildSingleField(field));
        standardRoot.Children.Add(sections);
        if (photoField is not null)
        {
            var photo = BuildPhotoEditor(photoField);
            photo.Margin = new Thickness(16, 4, 6, 10);
            Grid.SetColumn(photo, 1);
            standardRoot.Children.Add(photo);
        }
        return standardRoot;
    }

    private GroupBox BuildSection(string title, IReadOnlyList<CardFieldDefinition> fields)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var completedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field.GroupName))
            {
                if (!completedGroups.Add(field.GroupName)) continue;
                panel.Children.Add(BuildGroupedField(fields.Where(candidate => candidate.GroupName == field.GroupName).ToList()));
            }
            else
            {
                panel.Children.Add(BuildSingleField(field));
            }
        }
        return new GroupBox
        {
            Header = title,
            Content = panel,
            MinWidth = 286,
            MaxWidth = 864,
            Padding = new Thickness(6),
            Margin = new Thickness(6, 4, 6, 10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 214, 220)),
            Foreground = new SolidColorBrush(Color.FromRgb(54, 69, 80)),
            FontWeight = FontWeights.SemiBold
        };
    }

    private FrameworkElement BuildSingleField(CardFieldDefinition field)
    {
        var editor = CreateEditor(field);
        editors[field.ColumnName] = editor;
        AttachChangeHandler(editor);
        var width = IsMultiValueKind(field.Kind) ? 396 : 250;
        if (definition.IsPatients && field.Kind == CardFieldKind.MultilineText)
            width = field.ColumnName == "note" ? 532 : 396;
        return CreateFieldHost(field.Label, field.Required, editor, width);
    }

    private FrameworkElement BuildGroupedField(IReadOnlyList<CardFieldDefinition> fields) =>
        fields[0].GroupName == "tax" ? BuildTaxGroup(fields) : BuildAddressGroup(fields);

    private FrameworkElement BuildAddressGroup(IReadOnlyList<CardFieldDefinition> fields)
    {
        var address = fields.First(field => field.ColumnName == "address");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var addressEditor = new PlaceholderTextEditor(address.Placeholder ?? "Adres", 64, true);
        editors[address.ColumnName] = addressEditor;
        AttachChangeHandler(addressEditor);
        root.Children.Add(addressEditor);

        var details = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var ordered = fields.Where(field => field != address)
            .OrderBy(field => field.ColumnName switch { "district" => 0, "city" => 1, "postal_code" => 2, "country" => 3, _ => 4 })
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var field = ordered[index];
            var editor = new PlaceholderTextEditor(field.Placeholder ?? field.Label) { Width = index < 2 ? 126 : 118 };
            editors[field.ColumnName] = editor;
            AttachChangeHandler(editor);
            if (index > 0) editor.Margin = new Thickness(6, 0, 0, 0);
            if (index == 1)
                details.Children.Add(new TextBlock { Text = "/", FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            details.Children.Add(editor);
        }
        Grid.SetRow(details, 1);
        root.Children.Add(details);
        return CreateFieldHost("Adres", false, root, ordered.Count > 2 ? 560 : 400);
    }

    private FrameworkElement BuildTaxGroup(IReadOnlyList<CardFieldDefinition> fields)
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields.OrderBy(candidate => candidate.ColumnName == "tax_office" ? 0 : 1).ElementAt(index);
            var editor = new PlaceholderTextEditor(field.Placeholder ?? field.Label);
            if (index > 0) editor.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(editor, index);
            panel.Children.Add(editor);
            editors[field.ColumnName] = editor;
            AttachChangeHandler(editor);
        }
        return CreateFieldHost("Vergi Bilgileri", false, panel, 520);
    }

    private FrameworkElement CreateEditor(CardFieldDefinition field)
    {
        FrameworkElement editor = definition.IsPatients && originalKey is not null && field.ColumnName == "debt_amount"
            ? CreateDebtAmountEditor()
            : field.Kind switch
        {
            CardFieldKind.Date => new DatePicker { Height = 34, DisplayDateEnd = DateTime.Today, SelectedDateFormat = DatePickerFormat.Short },
            CardFieldKind.Permission => CreateStringCombo(["Eleman", "Eczacı", "A"], "Eleman"),
            CardFieldKind.Boolean => CreateBooleanEditor(),
            CardFieldKind.Gender => CreateStringCombo(["Erkek", "Kadın", "Belirtilmemiş"], "Belirtilmemiş"),
            CardFieldKind.Ownership => CreateStringCombo(["Kamu", "Özel"], null),
            CardFieldKind.CareLevel => CreateIntegerCombo([(1, "1. Basamak"), (2, "2. Basamak"), (3, "3. Basamak"), (4, "4. Basamak")]),
            CardFieldKind.PatientType => CreateIntegerCombo([(1, "Çalışan"), (2, "Emekli"), (3, "Diğer")]),
            CardFieldKind.Lookup => CreateLookupEditor(field),
            CardFieldKind.FamilyGroup => CreateFamilyGroupEditor(field),
            CardFieldKind.Phone => new PhoneTextEditor(),
            CardFieldKind.Email => new PlaceholderTextEditor("ornek@eposta.com"),
            CardFieldKind.Website => new WebsiteTextEditor(),
            CardFieldKind.PhoneList or CardFieldKind.EmailList or CardFieldKind.UtsCompanyList =>
                new MultiValueEditorControl(field.Kind, field.PhonePurposeProfile, field.DefaultPhonePurpose),
            CardFieldKind.MultilineText or CardFieldKind.TextArray =>
                new PlaceholderTextEditor(field.Placeholder ?? field.Label,
                    definition.IsPatients && field.ColumnName is "address" or "note" ? 104 : 64, true),
            _ => new PlaceholderTextEditor(field.Placeholder ?? "")
        };
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (definition.IsUsers && field.ColumnName is "user_id" or "permisson_level") editor.IsEnabled = false;
        return editor;
    }

    private LookupEditorControl CreateLookupEditor(CardFieldDefinition field)
    {
        var lookup = new LookupEditorControl();
        lookup.SearchRequested = term => repository.SearchLookupOptionsAsync(field.LookupTarget!.Value, term, CancellationToken.None);
        return lookup;
    }

    private FamilyGroupEditorControl CreateFamilyGroupEditor(CardFieldDefinition field)
    {
        var editor = new FamilyGroupEditorControl();
        editor.Lookup.SearchRequested = term => repository.SearchLookupOptionsAsync(field.LookupTarget!.Value, term, CancellationToken.None);
        editor.NewGroupRequested += OnNewFamilyGroupRequested;
        return editor;
    }

    private DebtAmountEditorControl CreateDebtAmountEditor()
    {
        var editor = new DebtAmountEditorControl();
        editor.AdjustmentRequested += OnDebtAdjustmentRequested;
        return editor;
    }

    private async void OnDebtAdjustmentRequested(object? sender, EventArgs e)
    {
        if (sender is not DebtAmountEditorControl editor
            || !int.TryParse(originalKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var patientId)) return;
        var currentDebt = ParseDecimal(editor.AmountEditor, "Borç Miktarı") ?? 0m;
        var dialog = new PatientDebtAdjustmentDialog(currentDebt) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            editor.AdjustButton.IsEnabled = false;
            BeginOperation("Borç miktarı güncelleniyor...");
            var newDebt = await repository.AdjustPatientDebtAsync(
                patientId, dialog.Amount, dialog.AddDebt, operationCancellation!.Token);
            suppressChangeTracking = true;
            editor.Text = newDebt.ToString("0.00", TurkishCulture);
            cleanEditorState["debt_amount"] = editor.StateValue;
            suppressChangeTracking = false;
            UpdateDirtyState();
            changed();
            ShowSuccess(dialog.AddDebt ? "Borç miktarı artırıldı." : "Borç miktarı azaltıldı.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInlineError(ex.Message); }
        finally
        {
            suppressChangeTracking = false;
            EndOperation();
            ApplyEditingState();
        }
    }

    private void OnNewFamilyGroupRequested(object? sender, EventArgs e)
    {
        var currentPatientId = int.TryParse(originalKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null;
        var dialog = new FamilyGroupDialog(repository, currentPatientId) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.CreatedOption is { } option && sender is FamilyGroupEditorControl editor)
        {
            editor.SetSelectedOption(option);
            UpdateDirtyState();
        }
    }

    private static ComboBox CreateStringCombo(IEnumerable<string> values, string? defaultValue)
    {
        var combo = new ComboBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var value in values) combo.Items.Add(value);
        combo.SelectedItem = defaultValue;
        return combo;
    }

    private static ComboBox CreateBooleanEditor()
    {
        var combo = new ComboBox { Height = 34, DisplayMemberPath = nameof(BooleanChoice.Label), VerticalContentAlignment = VerticalAlignment.Center };
        combo.Items.Add(new BooleanChoice(null, "Belirtilmedi"));
        combo.Items.Add(new BooleanChoice(true, "Evet"));
        combo.Items.Add(new BooleanChoice(false, "Hayır"));
        combo.SelectedIndex = 0;
        return combo;
    }

    private static ComboBox CreateIntegerCombo(IEnumerable<(int Value, string Label)> options)
    {
        var combo = new ComboBox { Height = 34, DisplayMemberPath = nameof(IntegerChoice.Label), VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var option in options) combo.Items.Add(new IntegerChoice(option.Value, option.Label));
        combo.SelectedIndex = -1;
        return combo;
    }

    private static Grid CreateFieldHost(string labelText, bool required, FrameworkElement editor, double width)
    {
        var host = new Grid { Width = width, Margin = new Thickness(6, 3, 6, 8) };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.Children.Add(new TextBlock
        {
            Text = required ? $"{labelText} *" : labelText,
            Foreground = new SolidColorBrush(Color.FromRgb(74, 87, 99)), FontSize = 12,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5)
        });
        Grid.SetRow(editor, 1);
        host.Children.Add(editor);
        return host;
    }

    private FrameworkElement BuildPhotoEditor(CardFieldDefinition field)
    {
        photoPreview = new Image { Width = 180, Height = 180, Stretch = Stretch.Uniform };
        var square = new Grid { Width = 190, Height = 190 };
        square.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(242, 245, 247)), BorderBrush = new SolidColorBrush(Color.FromRgb(194, 206, 213)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Child = photoPreview
        });
        photoChooseButton = new Button
        {
            Content = "Fotoğraf Ekle", MinWidth = 122, Height = 32, Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255))
        };
        photoChooseButton.Click += OnChoosePhotoClicked;
        square.Children.Add(photoChooseButton);
        editors[field.ColumnName] = square;
        var host = new StackPanel { Margin = new Thickness(6, 4, 6, 10) };
        host.Children.Add(new TextBlock { Text = field.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        host.Children.Add(square);
        return host;
    }

    private void AttachChangeHandler(FrameworkElement editor)
    {
        switch (editor)
        {
            case MultiValueEditorControl multi:
                multi.ValueChanged += (_, _) => UpdateDirtyState();
                multi.ValidationFailed += ShowInlineError;
                break;
            case LookupEditorControl lookup: lookup.ValueChanged += (_, _) => UpdateDirtyState(); break;
            case FamilyGroupEditorControl family: family.ValueChanged += (_, _) => UpdateDirtyState(); break;
            case PlaceholderTextEditor text: text.TextChanged += OnEditorValueChanged; break;
            case DatePicker date: date.SelectedDateChanged += OnEditorValueChanged; break;
            case ComboBox combo: combo.SelectionChanged += OnEditorValueChanged; break;
        }
    }

    private void ApplyAddDefaults()
    {
        if (originalKey is not null) return;
        foreach (var field in visibleFields.Where(field => field.DefaultValue is not null)) SetEditorValue(field, field.DefaultValue);
        foreach (var field in visibleFields)
        {
            if (initialAddValues.TryGetValue(field.ColumnName, out var value))
                SetEditorValue(field, value);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        if (originalKey is not null) await LoadAsync();
        else
        {
            suppressChangeTracking = false;
            ApplyEditingState();
            UpdateSaveVisuals();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

    private async Task LoadAsync()
    {
        BeginOperation("Kayıt yükleniyor...");
        suppressChangeTracking = true;
        try
        {
            var values = await repository.LoadValuesAsync(definition, originalKey!, operationCancellation!.Token);
            foreach (var field in visibleFields)
            {
                values.TryGetValue(field.ColumnName, out var value);
                if (field.Kind is CardFieldKind.Lookup or CardFieldKind.FamilyGroup)
                {
                    var key = Convert.ToString(value, CultureInfo.InvariantCulture);
                    var option = string.IsNullOrWhiteSpace(key)
                        ? null
                        : await repository.LoadLookupOptionAsync(field.LookupTarget!.Value, key, operationCancellation.Token);
                    if (field.Kind == CardFieldKind.Lookup)
                        ((LookupEditorControl)editors[field.ColumnName]).SetSelectedOption(option, key);
                    else
                        ((FamilyGroupEditorControl)editors[field.ColumnName]).SetSelectedOption(option, key);
                }
                else SetEditorValue(field, value);
            }
            displayName = TitleCase(definition.GetDisplayName(values, originalKey!));
            cleanEditorState = CaptureEditorState();
            isDirty = false;
            isEditing = startEditing;
            SetCurrentTitle(isEditing ? BuildEditTitle(displayName) : BuildViewTitle(displayName));
            ShowSuccess(isEditing ? "Kayıt düzenlemeye hazır." : "Kayıt görüntüleniyor.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInlineError($"Kayıt yüklenemedi: {ex.Message}"); }
        finally
        {
            suppressChangeTracking = false;
            EndOperation();
            ApplyEditingState();
        }
    }

    private void SetEditorValue(CardFieldDefinition field, object? value)
    {
        if (field.Kind == CardFieldKind.Photo)
        {
            photoBytes = value as byte[];
            SetPhotoPreview(photoBytes);
            return;
        }
        if (editors.TryGetValue(field.ColumnName, out var fieldEditor) && fieldEditor is DebtAmountEditorControl debtEditor)
        {
            debtEditor.Text = value is null ? "0,00" : Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("0.00", TurkishCulture);
            return;
        }
        switch (field.Kind)
        {
            case CardFieldKind.Date:
                ((DatePicker)editors[field.ColumnName]).SelectedDate = value switch { DateTime date => date.Date, DateOnly date => date.ToDateTime(TimeOnly.MinValue), _ => null };
                break;
            case CardFieldKind.Permission:
            case CardFieldKind.Gender:
            case CardFieldKind.Ownership:
                ((ComboBox)editors[field.ColumnName]).SelectedItem = ((ComboBox)editors[field.ColumnName]).Items.Cast<string>()
                    .FirstOrDefault(item => string.Equals(item, value as string, StringComparison.CurrentCultureIgnoreCase));
                break;
            case CardFieldKind.CareLevel:
                var integer = value is null ? (int?)null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
                ((ComboBox)editors[field.ColumnName]).SelectedItem = ((ComboBox)editors[field.ColumnName]).Items.Cast<IntegerChoice>().FirstOrDefault(item => item.Value == integer);
                break;
            case CardFieldKind.PatientType:
                var patientType = ParsePatientType(value);
                ((ComboBox)editors[field.ColumnName]).SelectedItem = ((ComboBox)editors[field.ColumnName]).Items.Cast<IntegerChoice>()
                    .FirstOrDefault(item => item.Value == patientType);
                break;
            case CardFieldKind.Boolean:
                var boolean = value is bool candidate ? candidate : (bool?)null;
                ((ComboBox)editors[field.ColumnName]).SelectedItem = ((ComboBox)editors[field.ColumnName]).Items.Cast<BooleanChoice>().First(item => item.Value == boolean);
                break;
            case CardFieldKind.Lookup:
                ((LookupEditorControl)editors[field.ColumnName]).SetSelectedOption(null);
                break;
            case CardFieldKind.FamilyGroup:
                ((FamilyGroupEditorControl)editors[field.ColumnName]).SetSelectedOption(null);
                break;
            case CardFieldKind.Photo: photoBytes = value as byte[]; SetPhotoPreview(photoBytes); break;
            case CardFieldKind.PhoneList:
            case CardFieldKind.EmailList:
            case CardFieldKind.UtsCompanyList:
                ((MultiValueEditorControl)editors[field.ColumnName]).Values = value as string[]; break;
            case CardFieldKind.TextArray:
                ((PlaceholderTextEditor)editors[field.ColumnName]).Text = value is string[] values ? string.Join(Environment.NewLine, values) : ""; break;
            case CardFieldKind.Phone:
                ((PhoneTextEditor)editors[field.ColumnName]).Text = value is string phone ? PhoneTextEditor.FormatStoredValue(phone) : ""; break;
            default:
                ((PlaceholderTextEditor)editors[field.ColumnName]).Text = field.StoreUppercase
                    ? TitleCase(Convert.ToString(value, TurkishCulture) ?? "")
                    : Convert.ToString(value, TurkishCulture) ?? "";
                break;
        }
    }

    private static int? ParsePatientType(object? value)
    {
        var text = Convert.ToString(value, TurkishCulture)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric is >= 1 and <= 3)
            return numeric;
        return text.ToUpper(TurkishCulture) switch
        {
            "ÇALIŞAN" => 1,
            "EMEKLİ" => 2,
            _ => 3
        };
    }

    private Dictionary<string, object?> ReadAndValidateValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in visibleFields)
        {
            object? value = field.Kind switch
            {
                _ when editors.TryGetValue(field.ColumnName, out var fieldEditor) && fieldEditor is DebtAmountEditorControl debtEditor => ParseDecimal(debtEditor.AmountEditor, field.Label),
                CardFieldKind.Date => ReadBirthDate((DatePicker)editors[field.ColumnName]),
                CardFieldKind.Permission or CardFieldKind.Gender or CardFieldKind.Ownership => ((ComboBox)editors[field.ColumnName]).SelectedItem as string,
                CardFieldKind.CareLevel => (((ComboBox)editors[field.ColumnName]).SelectedItem as IntegerChoice)?.Value,
                CardFieldKind.PatientType => (((ComboBox)editors[field.ColumnName]).SelectedItem as IntegerChoice)?.Value.ToString(CultureInfo.InvariantCulture),
                CardFieldKind.Boolean => (((ComboBox)editors[field.ColumnName]).SelectedItem as BooleanChoice)?.Value,
                CardFieldKind.Lookup => ((LookupEditorControl)editors[field.ColumnName]).GetSelectedKeyOrThrow(field.Label),
                CardFieldKind.FamilyGroup => ((FamilyGroupEditorControl)editors[field.ColumnName]).GetSelectedKeyOrThrow(field.Label),
                CardFieldKind.Photo => photoBytes,
                CardFieldKind.Phone => ((PhoneTextEditor)editors[field.ColumnName]).GetValidatedValue(field.Label),
                CardFieldKind.Email => ParseEmail(((PlaceholderTextEditor)editors[field.ColumnName]).Text, field.Label),
                CardFieldKind.PhoneList or CardFieldKind.EmailList or CardFieldKind.UtsCompanyList => ((MultiValueEditorControl)editors[field.ColumnName]).Values,
                CardFieldKind.Integer => ParseInteger(editors[field.ColumnName], field.Label),
                CardFieldKind.Decimal => ParseDecimal(editors[field.ColumnName], field.Label),
                CardFieldKind.TextArray => ParseTextArray(editors[field.ColumnName]),
                _ => NullIfWhiteSpace(ReadEditorText(editors[field.ColumnName]))
            };
            if (field.StoreUppercase && value is string textValue) value = textValue.ToUpper(TurkishCulture);
            if (field.Required && (value is null || value is string text && string.IsNullOrWhiteSpace(text)))
                throw new InvalidOperationException($"{field.Label} zorunludur.");
            values[field.ColumnName] = value;
        }
        if (definition.IsUsers) ValidateIdentityNumber(values["tc_identity_no"] as string);
        if (definition.IsPatients)
        {
            ValidateIdentityNumber(values["tc_identity_no"] as string);
            if (values["debt_amount"] is null) values["debt_amount"] = 0m;
        }
        return values;
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (originalKey is not null && !isEditing)
        {
            isEditing = true;
            SetCurrentTitle(BuildEditTitle(displayName));
            EditNoticeText.Visibility = Visibility.Collapsed;
            ApplyEditingState();
            ShowSuccess("Alanlar düzenlemeye açıldı.");
            return;
        }
        if (originalKey is not null && !isDirty) return;
        try
        {
            var values = ReadAndValidateValues();
            if (originalKey is not null && HasConfirmationFieldChanged() && pharmacyApp.Localization.EnglishMessageBox.Show(
                    "Benzersiz bir tanımlayıcıyı değiştirdiniz. Bu değişikliği kaydetmek istediğinizden emin misiniz?",
                    "Tanımlayıcı Değişikliği", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                ShowInlineError("Kaydetme işlemi iptal edildi.");
                return;
            }
            BeginOperation("Kayıt kaydediliyor...");
            if (definition.IsUsers && originalKey is null)
            {
                var userId = (string)values["user_id"]!;
                if (await repository.FindUserActiveStatusAsync(userId, operationCancellation!.Token) is false)
                {
                    if (pharmacyApp.Localization.EnglishMessageBox.Show("Bu kullanıcı pasif durumda. Yeniden aktif hale getirmek istediğinizden emin misiniz?", "Kullanıcıyı Yeniden Aktifleştir", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        ShowInlineError("Yeniden aktifleştirme işlemi iptal edildi.");
                        return;
                    }
                    await repository.ReactivateUserAsync(userId, operationCancellation.Token);
                    changed();
                    CloseRequested?.Invoke();
                    return;
                }
            }

            var wasAdd = originalKey is null;
            var savedKey = await repository.SaveAsync(definition, originalKey, values, operationCancellation!.Token);
            changed();
            if (wasAdd)
            {
                originalKey = savedKey;
                displayName = TitleCase(definition.GetDisplayName(values, savedKey));
                RecordSaved?.Invoke(savedKey, displayName);
                ShowSuccess("Kayıt başarıyla eklendi.");
                CloseRequested?.Invoke();
                return;
            }
            if (!string.Equals(originalKey, savedKey, StringComparison.Ordinal))
            {
                originalKey = savedKey;
                ModuleIdentityChanged?.Invoke(CurrentModuleId);
            }
            displayName = TitleCase(definition.GetDisplayName(values, originalKey!));
            cleanEditorState = CaptureEditorState();
            isDirty = false;
            ShowSuccess("Değişiklikler başarıyla kaydedildi.");
            SetCurrentTitle(BuildEditTitle(displayName));
        }
        catch (OperationCanceledException) { }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { ShowInlineError("Bu benzersiz değer başka bir kayıtta kullanılıyor."); }
        catch (Exception ex) { ShowInlineError(ex.Message); }
        finally { EndOperation(); }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (definition.Kind == CardEntityKind.Users) return;
        if (originalKey is null) return;
        var action = definition.IsUsers ? "pasif duruma getirmek" : "silmek";
        if (pharmacyApp.Localization.EnglishMessageBox.Show($"{definition.SingularName} kaydını {action} istediğinizden emin misiniz?", definition.DeleteButtonText, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            BeginOperation(definition.IsUsers ? "Kullanıcı pasif duruma getiriliyor..." : "Kayıt siliniyor...");
            await repository.DeleteAsync(definition, originalKey, operationCancellation!.Token);
            changed();
            RecordDeleted?.Invoke(originalKey);
            CloseRequested?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInlineError($"İşlem tamamlanamadı: {ex.Message}"); }
        finally { EndOperation(); }
    }

    private bool HasConfirmationFieldChanged() => visibleFields.Where(field => field.ConfirmOnChange).Any(field =>
        cleanEditorState.TryGetValue(field.ColumnName, out var oldValue) &&
        !Equals(oldValue, CaptureEditorState()[field.ColumnName]));

    private void ApplyEditingState()
    {
        foreach (var field in visibleFields)
        {
            if (!editors.TryGetValue(field.ColumnName, out var editor)) continue;
            switch (editor)
            {
                case MultiValueEditorControl multi: multi.SetEditing(isEditing); break;
                case PlaceholderTextEditor text: text.SetEditing(isEditing); break;
                case LookupEditorControl lookup: lookup.IsEnabled = isEditing; break;
                case FamilyGroupEditorControl family: family.SetEditing(isEditing); break;
                case DebtAmountEditorControl debt: debt.SetEditing(isEditing && !operationInProgress); break;
                case DatePicker date: date.IsEnabled = isEditing; break;
                case ComboBox combo: combo.IsEnabled = isEditing; break;
            }
            if (definition.IsUsers && field.ColumnName is "user_id" or "permisson_level") editor.IsEnabled = false;
        }
        if (photoChooseButton is not null) photoChooseButton.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        if (isEditing) EditNoticeText.Visibility = Visibility.Collapsed;
        UpdateModeVisuals();
        UpdateSaveVisuals();
    }

    private void UpdateModeVisuals()
    {
        SaveButton.Content = originalKey is not null && !isEditing ? "Düzenle" : "Kaydet";
        DeleteButton.Content = definition.DeleteButtonText;
        DeleteButton.Visibility = !definition.IsUsers && originalKey is not null && isEditing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSaveVisuals()
    {
        var canAct = !operationInProgress && (originalKey is null || !isEditing || isDirty);
        SaveButton.IsEnabled = canAct;
        SaveButton.Background = canAct ? ActiveSaveBrush : InactiveSaveBrush;
        SaveButton.BorderBrush = SaveButton.Background;
        DeleteButton.IsEnabled = !definition.IsUsers && !operationInProgress && originalKey is not null && isEditing;
    }

    private void BeginOperation(string message)
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        operationInProgress = true;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 91, 102));
        StatusText.Text = message;
        UpdateSaveVisuals();
    }

    private void EndOperation() { operationInProgress = false; UpdateSaveVisuals(); }
    private void ShowInlineError(string message) { StatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24)); StatusText.Text = message; }
    private void ShowSuccess(string message) { StatusText.Foreground = new SolidColorBrush(Color.FromRgb(23, 97, 61)); StatusText.Text = message; }

    private void OnViewModeTextInput(object sender, TextCompositionEventArgs e)
    {
        if (originalKey is null || isEditing || string.IsNullOrEmpty(e.Text)) return;
        EditNoticeText.Visibility = Visibility.Visible;
    }

    private void OnChoosePhotoClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Localization.UiText.Translate($"{definition.SingularName} Fotoğrafı Seç"), Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        try { photoBytes = File.ReadAllBytes(dialog.FileName); SetPhotoPreview(photoBytes); UpdateDirtyState(); }
        catch (Exception ex) { ShowInlineError($"Fotoğraf okunamadı: {ex.Message}"); }
    }

    private void SetPhotoPreview(byte[]? bytes)
    {
        if (photoPreview is null) return;
        if (bytes is null || bytes.Length == 0) { photoPreview.Source = null; if (photoChooseButton is not null) photoChooseButton.Content = "Fotoğraf Ekle"; return; }
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            photoPreview.Source = image;
            if (photoChooseButton is not null) photoChooseButton.Content = "Fotoğraf Değiştir";
        }
        catch { photoPreview.Source = null; ShowInlineError("Kayıtlı fotoğraf görüntülenemedi."); }
    }

    private void OnEditorValueChanged(object? sender, RoutedEventArgs e) => UpdateDirtyState();
    private void UpdateDirtyState()
    {
        if (suppressChangeTracking || originalKey is null || !isEditing) return;
        isDirty = !EditorStatesEqual(cleanEditorState, CaptureEditorState());
        UpdateSaveVisuals();
    }

    private Dictionary<string, object?> CaptureEditorState()
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in visibleFields)
        {
            state[field.ColumnName] = field.Kind switch
            {
                _ when editors.TryGetValue(field.ColumnName, out var fieldEditor) && fieldEditor is DebtAmountEditorControl debtEditor => debtEditor.StateValue,
                CardFieldKind.Date => ((DatePicker)editors[field.ColumnName]).SelectedDate?.Date,
                CardFieldKind.Permission or CardFieldKind.Gender or CardFieldKind.Ownership => ((ComboBox)editors[field.ColumnName]).SelectedItem as string,
                CardFieldKind.CareLevel or CardFieldKind.PatientType => (((ComboBox)editors[field.ColumnName]).SelectedItem as IntegerChoice)?.Value,
                CardFieldKind.Boolean => (((ComboBox)editors[field.ColumnName]).SelectedItem as BooleanChoice)?.Value,
                CardFieldKind.Lookup => ((LookupEditorControl)editors[field.ColumnName]).StateValue,
                CardFieldKind.FamilyGroup => ((FamilyGroupEditorControl)editors[field.ColumnName]).StateValue,
                CardFieldKind.Photo => photoBytes?.ToArray(),
                CardFieldKind.PhoneList or CardFieldKind.EmailList or CardFieldKind.UtsCompanyList => ((MultiValueEditorControl)editors[field.ColumnName]).Values?.ToArray(),
                _ => ReadEditorText(editors[field.ColumnName])
            };
        }
        return state;
    }

    private static bool EditorStatesEqual(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var item in left)
        {
            if (!right.TryGetValue(item.Key, out var other)) return false;
            if (item.Value is byte[] bytes && other is byte[] otherBytes) { if (!bytes.SequenceEqual(otherBytes)) return false; }
            else if (item.Value is string[] values && other is string[] otherValues) { if (!values.SequenceEqual(otherValues, StringComparer.Ordinal)) return false; }
            else if (!Equals(item.Value, other)) return false;
        }
        return true;
    }

    private static DateTime? ReadBirthDate(DatePicker editor)
    {
        var date = editor.SelectedDate?.Date;
        if (date > DateTime.Today) throw new InvalidOperationException("Doğum tarihi gelecekte olamaz.");
        return date;
    }
    private static int? ParseInteger(FrameworkElement editor, string label) => string.IsNullOrWhiteSpace(ReadEditorText(editor)) ? null :
        int.TryParse(ReadEditorText(editor).Trim(), NumberStyles.Integer, TurkishCulture, out var value) ? value : throw new InvalidOperationException($"{label} tam sayı olmalıdır.");
    private static decimal? ParseDecimal(FrameworkElement editor, string label)
    {
        var text = ReadEditorText(editor);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, TurkishCulture, out var value) || decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return value;
        throw new InvalidOperationException($"{label} geçerli bir sayı olmalıdır.");
    }
    private static string? ParseEmail(string value, string label)
    {
        var email = NullIfWhiteSpace(value);
        if (email is not null && !Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) throw new InvalidOperationException($"{label} geçerli bir e-posta adresi olmalıdır.");
        return email;
    }
    private static string[]? ParseTextArray(FrameworkElement editor)
    {
        var values = ReadEditorText(editor).Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length == 0 ? null : values;
    }
    private static string ReadEditorText(FrameworkElement editor) => editor switch
    {
        PlaceholderTextEditor placeholder => placeholder.Text,
        DebtAmountEditorControl debt => debt.Text,
        TextBox text => text.Text,
        _ => ""
    };
    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ValidateIdentityNumber(string? identity) { if (!Regex.IsMatch(identity ?? "", @"^\d{11}$")) throw new InvalidOperationException("T.C. kimlik numarası 11 rakamdan oluşmalıdır."); }
    private static string TitleCase(string value) => TurkishCulture.TextInfo.ToTitleCase(value.ToLower(TurkishCulture));
    private void SetCurrentTitle(string title) { if (currentModuleTitle == title) return; currentModuleTitle = title; ModuleTitleChanged?.Invoke(title); }
    private string BuildEditTitle(string name) => $"{name} | {definition.EditTitle}";
    private string BuildViewTitle(string name) => $"{name} | {definition.EditMenuText.Replace("Düzenle", "Görüntüle", StringComparison.Ordinal)}";
    private static bool IsMultiValueKind(CardFieldKind kind) => kind is CardFieldKind.PhoneList or CardFieldKind.EmailList or CardFieldKind.UtsCompanyList;
    private sealed record BooleanChoice(bool? Value, string Label);
    private sealed record IntegerChoice(int Value, string Label);
}
