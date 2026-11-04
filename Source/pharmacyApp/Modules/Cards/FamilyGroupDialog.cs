using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pharmacyApp.Modules.Cards;

internal sealed class FamilyGroupDialog : Window
{
    private readonly CardRepository repository;
    private readonly int? currentPatientId;
    private readonly TextBox nameBox = new() { Height = 34, Padding = new Thickness(8, 4, 8, 4) };
    private readonly LookupEditorControl mainLookup = new();
    private readonly LookupEditorControl memberLookup = new();
    private readonly ObservableCollection<CardLookupOption> members = [];
    private readonly ListBox memberList = new() { Height = 150 };

    public FamilyGroupDialog(CardRepository repository, int? currentPatientId)
    {
        this.repository = repository;
        this.currentPatientId = currentPatientId;
        Title = "Aile Grubu Ekle";
        Width = 520;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(243, 245, 247));
        Content = BuildContent();
        mainLookup.SearchRequested = term => repository.SearchPatientsAsync(term, CancellationToken.None);
        memberLookup.SearchRequested = term => repository.SearchPatientsAsync(term, CancellationToken.None);
        Loaded += OnLoaded;
    }

    public CardLookupOption? CreatedOption { get; private set; }

    private FrameworkElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(18) };
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        root.Children.Add(Field("Aile Grubu Adı *", nameBox, 0));
        root.Children.Add(Field("Aile Büyüğü", mainLookup, 1));

        var memberRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        memberRow.ColumnDefinitions.Add(new ColumnDefinition());
        memberRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        memberRow.Children.Add(memberLookup);
        var add = new Button { Content = "Üye Ekle", Height = 34, Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        add.Click += OnAddMember;
        Grid.SetColumn(add, 1);
        memberRow.Children.Add(add);
        root.Children.Add(Field("Üye Seçimi", memberRow, 2));

        memberList.ItemsSource = members;
        memberList.DisplayMemberPath = nameof(CardLookupOption.Name);
        Grid.SetRow(memberList, 3);
        root.Children.Add(memberList);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var remove = new Button { Content = "Seçili Üyeyi Çıkar", Height = 34, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        remove.Click += (_, _) => { if (memberList.SelectedItem is CardLookupOption option) members.Remove(option); };
        var cancel = new Button { Content = "Vazgeç", Height = 34, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Kaydet", Height = 34, Padding = new Thickness(14, 3, 14, 3), Background = Brushes.Teal, Foreground = Brushes.White };
        save.Click += OnSave;
        buttons.Children.Add(remove); buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        return root;
    }

    private static FrameworkElement Field(string label, FrameworkElement control, int row)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(control);
        Grid.SetRow(panel, row);
        return panel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (currentPatientId is not int id) return;
        var option = await repository.LoadPatientOptionAsync(id, CancellationToken.None);
        if (option is not null) members.Add(option);
    }

    private async void OnAddMember(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = memberLookup.GetSelectedKeyOrThrow("Üye");
            if (key is null || !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return;
            var option = memberLookup.SelectedItem as CardLookupOption
                         ?? await repository.LoadPatientOptionAsync(id, CancellationToken.None);
            if (option is not null && members.All(existing => existing.Key != option.Key)) members.Add(option);
            memberLookup.SetSelectedOption(null);
        }
        catch (Exception ex) { pharmacyApp.Localization.EnglishMessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var mainKey = mainLookup.GetSelectedKeyOrThrow("Aile büyüğü");
            var mainId = string.IsNullOrWhiteSpace(mainKey) ? (int?)null : int.Parse(mainKey, CultureInfo.InvariantCulture);
            if (mainId is int id && members.All(member => member.Key != mainKey))
            {
                var option = await repository.LoadPatientOptionAsync(id, CancellationToken.None);
                if (option is not null) members.Insert(0, option);
            }
            var groupId = await repository.CreateFamilyGroupAsync(nameBox.Text, mainId,
                members.Select(member => int.Parse(member.Key, CultureInfo.InvariantCulture)).ToArray(), CancellationToken.None);
            CreatedOption = new CardLookupOption(groupId.ToString(CultureInfo.InvariantCulture), nameBox.Text.Trim());
            DialogResult = true;
        }
        catch (Exception ex) { pharmacyApp.Localization.EnglishMessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
