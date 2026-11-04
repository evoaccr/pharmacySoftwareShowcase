using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using pharmacyApp.Modules.Medula;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.Modules.Cards;

public partial class CardListView : UserControl, IModuleTabRequestSource
{
    private const int PageSize = 300;
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly CardEntityDefinition definition;
    private readonly CardRepository repository = new();
    private readonly LoyalPatientRepository loyalPatientRepository = new();
    private readonly ObservableCollection<CardRow> rows = [];
    private readonly DispatcherTimer filterReloadTimer;
    private CancellationTokenSource? loadCancellation;
    private TextBox? searchBox;
    private TextBox? secondarySearchBox;
    private TextBox? minimumAgeBox;
    private TextBox? maximumAgeBox;
    private ComboBox? debtFilter;
    private ComboBox? consignedFilter;
    private ComboBox? institutionFilter;
    private ComboBox? familyFilter;
    private ComboBox? specialtyFilter;
    private ComboBox? facilityFilter;
    private CheckBox? pharmacyDebtOnly;
    private bool updatingSuggestions;
    private bool initialized;
    private bool isLoading;
    private int totalCount;
    private int loadVersion;
    private int suggestionVersion;
    private string sortColumn = "";
    private bool sortDescending;
    private CardListQuery activeQuery = new();

    public CardListView(CardEntityDefinition definition)
    {
        this.definition = definition;
        InitializeComponent();
        AddButton.Content = definition.AddButtonText;
        if (definition.Kind == CardEntityKind.Users) AddButton.IsEnabled = false;
        ConfigureColumns();
        ConfigureFilters();
        ConfigureContextMenu();
        CardGrid.ItemsSource = rows;
        filterReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        filterReloadTimer.Tick += async (_, _) =>
        {
            filterReloadTimer.Stop();
            await ReloadAsync();
        };
    }

    public event Action<ModuleTabRequest>? TabRequested;

    private void ConfigureColumns()
    {
        foreach (var column in definition.Columns)
        {
            var binding = new Binding($"[{column.Key}]");
            if (!string.IsNullOrWhiteSpace(column.StringFormat)) binding.StringFormat = column.StringFormat;
            DataGridColumn gridColumn;
            if (definition.IsPatients && column.Key == "DebtDisplay")
            {
                gridColumn = new DataGridTemplateColumn
                {
                    Header = column.Header,
                    CellTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(PatientDebtCell)) },
                    SortMemberPath = column.Key,
                    CanUserResize = true,
                    CanUserSort = true,
                    Width = column.IsStar ? new DataGridLength(column.Width, DataGridLengthUnitType.Star) : column.Width,
                    MinWidth = 90,
                    CellStyle = CreateDebtCellStyle()
                };
            }
            else
            {
                gridColumn = new DataGridTextColumn
                {
                    Header = column.Header,
                    Binding = binding,
                    SortMemberPath = column.Key,
                    CanUserResize = true,
                    CanUserSort = true,
                    Width = column.IsStar ? new DataGridLength(column.Width, DataGridLengthUnitType.Star) : column.Width,
                    MinWidth = 46
                };
            }
            if (definition.IsUsers && column.Key == nameof(CardRow.StatusDisplay)) gridColumn.CellStyle = CreateStatusCellStyle();
            if (definition.IsPatients && CreatePatientCellStyle(column.Key) is { } patientStyle) gridColumn.CellStyle = patientStyle;
            CardGrid.Columns.Add(gridColumn);
        }
    }

    private static Style CreateDebtCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        return style;
    }

    private static Style? CreatePatientCellStyle(string key)
    {
        var sourceKey = key switch
        {
            "LoyalDisplay" => "IsLoyal",
            "FirstName" or "LastName" => "IsEmployee",
            "Age" => "Alive",
            "ConsignedAmount" => "HasConsignment",
            _ => null
        };
        if (sourceKey is null) return null;
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var trueTrigger = new DataTrigger { Binding = new Binding($"[{sourceKey}]"), Value = true };
        trueTrigger.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(key switch
            {
                "ConsignedAmount" => Color.FromRgb(255, 239, 153),
                "FirstName" or "LastName" => Color.FromRgb(214, 232, 252),
                _ => Color.FromRgb(213, 241, 224)
            })));
        style.Triggers.Add(trueTrigger);
        if (key is "LoyalDisplay" or "Age")
        {
            var falseTrigger = new DataTrigger { Binding = new Binding($"[{sourceKey}]"), Value = false };
            falseTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(250, 218, 218))));
            style.Triggers.Add(falseTrigger);
        }
        return style;
    }

    private static Style CreateStatusCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var active = new DataTrigger { Binding = new Binding(nameof(CardRow.Status)), Value = true };
        active.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(220, 245, 230))));
        var inactive = new DataTrigger { Binding = new Binding(nameof(CardRow.Status)), Value = false };
        inactive.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(253, 222, 222))));
        style.Triggers.Add(active);
        style.Triggers.Add(inactive);
        return style;
    }

    private void ConfigureFilters()
    {
        searchBox = AddTextFilter(definition.Kind switch
        {
            CardEntityKind.Patients => "Ad Soyad / T.C.",
            CardEntityKind.Pharmacies => "GLN / Eczane Adı",
            CardEntityKind.Faculties => "Tesis Kodu / Adı",
            CardEntityKind.Depots => "GLN / Depo Adı",
            CardEntityKind.Institutions => "Kurum Kodu / Adı",
            CardEntityKind.Doctors => "Dip. Tescil No / Ad Soyad",
            _ => "Kullanıcı Adı"
        }, 230);

        if (definition.IsPatients)
        {
            minimumAgeBox = AddTextFilter("Yaş aralığı", 58);
            FilterPanel.Children.Add(new TextBlock { Text = "-", Margin = new Thickness(3, 22, 3, 0), FontSize = 16 });
            maximumAgeBox = AddTextFilter(" ", 58);
            debtFilter = AddComboFilter("Borç Durumu", ["Hepsi", "Borçlu", "Borçsuz"]);
            consignedFilter = AddComboFilter("Emanet Durumu", ["Hepsi", "Emaneti Var", "Emaneti Yok"]);
            institutionFilter = AddComboFilter("Kurumu", ["Hepsi"], 180);
            familyFilter = AddEditableComboFilter("Hasta Aile Grubu", 170);
            var upcoming = new CheckBox { Content = "Yalnızca Yaklaşan İlaçlı", IsEnabled = false, Margin = new Thickness(8, 23, 8, 0), ToolTip = "Geçici olarak uygulanmadı." };
            FilterPanel.Children.Add(upcoming);
        }
        else if (definition.IsPharmacies)
        {
            secondarySearchBox = AddTextFilter("Eczacı Adı", 180);
            pharmacyDebtOnly = new CheckBox { Content = "Yalnızca Borçlu Eczaneler", Margin = new Thickness(8, 23, 8, 0) };
            pharmacyDebtOnly.Checked += (_, _) => QueueFilterReload();
            pharmacyDebtOnly.Unchecked += (_, _) => QueueFilterReload();
            FilterPanel.Children.Add(pharmacyDebtOnly);
        }
        else if (definition.IsDoctors)
        {
            secondarySearchBox = AddTextFilter("Diploma No", 145);
            specialtyFilter = AddComboFilter("Branş", ["Hepsi"], 190);
            facilityFilter = AddEditableComboFilter("Tesis", 190, UpdateFacilitySuggestions);
        }

        AddClearFilterButton();
    }

    private TextBox AddTextFilter(string label, double width)
    {
        var box = new TextBox { Width = width, Height = 30, Padding = new Thickness(7, 3, 7, 3) };
        box.TextChanged += (_, _) => QueueFilterReload();
        FilterPanel.Children.Add(WrapFilter(label, box));
        return box;
    }

    private ComboBox AddComboFilter(string label, IEnumerable<string> values, double width = 145)
    {
        var combo = new ComboBox { Width = width, Height = 30, ItemsSource = values, SelectedIndex = 0 };
        combo.SelectionChanged += (_, _) => QueueFilterReload();
        FilterPanel.Children.Add(WrapFilter(label, combo));
        return combo;
    }

    private ComboBox AddEditableComboFilter(string label, double width, Action? changed = null)
    {
        var combo = new ComboBox { Width = width, Height = 30, IsEditable = true, IsTextSearchEnabled = false, MaxDropDownHeight = 34 };
        combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => (changed ?? UpdateFamilySuggestions)()));
        FilterPanel.Children.Add(WrapFilter(label, combo));
        return combo;
    }

    private void AddClearFilterButton()
    {
        var icon = new Path
        {
            Data = Geometry.Parse("M2,3 H18 L12,10 V17 L8,19 V10 Z"),
            Fill = Brushes.White,
            Stretch = Stretch.Uniform,
            Width = 17,
            Height = 17
        };
        var button = new Button
        {
            Content = icon,
            Width = 36,
            Height = 30,
            Margin = new Thickness(0, 20, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(180, 35, 24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24)),
            ToolTip = "Filtreleri Temizle"
        };
        button.Click += (_, _) => ClearFilters();
        FilterPanel.Children.Add(button);
    }

    private static FrameworkElement WrapFilter(string label, Control control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(82, 96, 107)), Margin = new Thickness(1, 0, 0, 3) });
        stack.Children.Add(control);
        return stack;
    }

    private void ConfigureContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(definition.EditMenuText, (_, _) => OpenEditor(true)));
        if (definition.IsUsers)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Hareketlerini Aç", (_, _) => OpenUserMovements("all", null)));
            menu.Items.Add(CreateMenuItem("Satışlarını Aç", (_, _) => OpenUserMovements("sales", ["PERAK_OUT", "PRESC_OUT"])));
            menu.Items.Add(CreateMenuItem("Satın Almalarını Aç", (_, _) => OpenUserMovements("purchases", ["DEP_IN", "FIRM_IN"])));
        }
        else if (definition.IsPharmacies)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Eczaneye Giden Hareketleri Aç", (_, _) => OpenPharmacyMovements()));
        }
        else if (definition.IsPatients)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Borç Geçmişini Aç", (_, _) => OpenSelectedPatientDebtHistory()));
            menu.Items.Add(CreateMenuItem("Aile Grubunu Görüntüle", (_, _) => OpenSelectedFamilyGroup()));
            menu.Items.Add(CreateMenuItem("İlaçları Görüntüle", OnOpenMedicationHistory));
            menu.Items.Add(CreateMenuItem("Raporları Görüntüle", OnOpenReportHistory));
            menu.Items.Add(new MenuItem { Header = "Satışları Aç", IsEnabled = false });
            menu.Items.Add(CreateMenuItem("Hareketleri Aç", (_, _) => OpenPatientMovements()));
        }
        CardGrid.ContextMenu = menu;
    }

    private static MenuItem CreateMenuItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        await ConfigureFilterOptionsAsync();
        initialized = true;
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        filterReloadTimer.Stop();
        loadCancellation?.Cancel();
    }

    private async Task ReloadAsync()
    {
        var version = ++loadVersion;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        isLoading = true;
        SetStatus("Kayıtlar yükleniyor...", false);
        try
        {
            activeQuery = BuildListQuery();
            var page = await repository.LoadListPageAsync(definition, activeQuery, 0, PageSize, loadCancellation.Token);
            if (version != loadVersion) return;
            rows.Clear();
            foreach (var row in page.Rows) rows.Add(row);
            totalCount = page.TotalCount;
            CountText.Text = $"Toplam Kayıt: {totalCount:N0}";
            StatusOverlay.Visibility = totalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (totalCount == 0) SetStatus(HasActiveFilters() ? "Filtreye uyan kayıt bulunamadı." : "Henüz kayıt bulunmuyor.", false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != loadVersion) return;
            rows.Clear();
            totalCount = 0;
            CountText.Text = "Toplam Kayıt: 0";
            SetStatus($"Kayıtlar yüklenemedi: {ex.Message}", true);
        }
        finally
        {
            if (version == loadVersion) isLoading = false;
        }
    }

    private async Task LoadMoreAsync()
    {
        if (isLoading || rows.Count >= totalCount) return;
        var version = loadVersion;
        isLoading = true;
        try
        {
            var page = await repository.LoadListPageAsync(definition, activeQuery, rows.Count, PageSize,
                loadCancellation?.Token ?? CancellationToken.None);
            if (version != loadVersion) return;
            foreach (var row in page.Rows) rows.Add(row);
            if (page.Rows.Count > 0) totalCount = page.TotalCount;
            CountText.Text = $"Toplam Kayıt: {totalCount:N0}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus($"Ek kayıtlar yüklenemedi: {ex.Message}", true); }
        finally { if (version == loadVersion) isLoading = false; }
    }

    private async Task ConfigureFilterOptionsAsync()
    {
        try
        {
            if (institutionFilter is not null)
            {
                var values = await repository.LoadListFilterOptionsAsync(definition.Kind, "institution", CancellationToken.None);
                institutionFilter.ItemsSource = new[] { "Hepsi" }.Concat(values);
                institutionFilter.SelectedIndex = 0;
            }
            if (specialtyFilter is not null)
            {
                var values = await repository.LoadListFilterOptionsAsync(definition.Kind, "specialty", CancellationToken.None);
                specialtyFilter.ItemsSource = new[] { "Hepsi" }.Concat(values);
                specialtyFilter.SelectedIndex = 0;
            }
        }
        catch { /* The record list can still be used when optional filter options cannot be loaded. */ }
    }

    private async void UpdateFamilySuggestions()
    {
        if (familyFilter is null) return;
        await UpdateEditableSuggestionsAsync(familyFilter, "family");
    }

    private async void UpdateFacilitySuggestions()
    {
        if (facilityFilter is not null) await UpdateEditableSuggestionsAsync(facilityFilter, "facility");
    }

    private async Task UpdateEditableSuggestionsAsync(ComboBox combo, string filter)
    {
        if (updatingSuggestions) return;
        var typed = combo.Text;
        var version = ++suggestionVersion;
        IReadOnlyList<string> suggestions = [];
        if (typed.Trim().Length >= 3)
        {
            try { suggestions = await repository.SearchListFilterOptionsAsync(definition.Kind, filter, typed, CancellationToken.None); }
            catch { suggestions = []; }
        }
        if (version != suggestionVersion) return;
        updatingSuggestions = true;
        combo.ItemsSource = suggestions;
        combo.Text = typed;
        combo.MaxDropDownHeight = suggestions.Count == 0 ? 34 : 180;
        combo.IsDropDownOpen = suggestions.Count > 0;
        updatingSuggestions = false;
        _ = combo.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox textBox)
                textBox.Select(textBox.Text.Length, 0);
        });
        QueueFilterReload();
    }

    private void ClearFilters()
    {
        searchBox?.Clear();
        secondarySearchBox?.Clear();
        minimumAgeBox?.Clear();
        maximumAgeBox?.Clear();
        if (debtFilter is not null) debtFilter.SelectedIndex = 0;
        if (consignedFilter is not null) consignedFilter.SelectedIndex = 0;
        if (institutionFilter is not null) institutionFilter.SelectedIndex = 0;
        if (specialtyFilter is not null) specialtyFilter.SelectedIndex = 0;
        if (familyFilter is not null) familyFilter.Text = "";
        if (facilityFilter is not null) facilityFilter.Text = "";
        if (pharmacyDebtOnly is not null) pharmacyDebtOnly.IsChecked = false;
        QueueFilterReload();
    }

    private void QueueFilterReload()
    {
        if (!initialized || updatingSuggestions) return;
        filterReloadTimer.Stop();
        filterReloadTimer.Start();
    }

    private CardListQuery BuildListQuery()
    {
        int? minimumAge = int.TryParse(minimumAgeBox?.Text, out var minimum) ? minimum : null;
        int? maximumAge = int.TryParse(maximumAgeBox?.Text, out var maximum) ? maximum : null;
        var institution = institutionFilter?.SelectedItem as string;
        var specialty = specialtyFilter?.SelectedItem as string;
        return new CardListQuery(
            searchBox?.Text.Trim() ?? "",
            secondarySearchBox?.Text.Trim() ?? "",
            minimumAge,
            maximumAge,
            debtFilter?.SelectedItem as string ?? "Hepsi",
            consignedFilter?.SelectedItem as string ?? "Hepsi",
            institution is null or "Hepsi" ? "" : institution,
            familyFilter?.Text.Trim() ?? "",
            specialty is null or "Hepsi" ? "" : specialty,
            facilityFilter?.Text.Trim() ?? "",
            pharmacyDebtOnly?.IsChecked == true,
            sortColumn,
            sortDescending);
    }

    private bool HasActiveFilters() => BuildListQuery() with { SortColumn = "", SortDescending = false } != new CardListQuery();

    private async void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scrollViewer || scrollViewer.ScrollableHeight <= 0) return;
        if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight * 0.75) await LoadMoreAsync();
    }

    private async void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var descending = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending;
        foreach (var column in CardGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = descending
            ? System.ComponentModel.ListSortDirection.Descending
            : System.ComponentModel.ListSortDirection.Ascending;
        sortColumn = e.Column.SortMemberPath;
        sortDescending = descending;
        await ReloadAsync();
    }

    private async void OnRetryClicked(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void OnAddClicked(object sender, RoutedEventArgs e) => OpenEditorPopup(null, true);
    private void OnGridDoubleClicked(object sender, MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(CardGrid, e.OriginalSource as DependencyObject) is DataGridRow) OpenEditor(false); }
    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(CardGrid, e.OriginalSource as DependencyObject) is DataGridRow row) CardGrid.SelectedItem = row.Item; }
    private void OpenEditor(bool startInEditMode) { if (CardGrid.SelectedItem is CardRow row) OpenEditorPopup(row, startInEditMode); }

    private void OpenEditorPopup(CardRow? row, bool startInEditMode)
    {
        var editor = new CardEditorView(definition, row?.Key, row?.DisplayName, () => _ = ReloadAsync(), startInEditMode);
        var (width, height) = GetPopupSize(row is not null);
        var window = new Window
        {
            Title = editor.CurrentModuleTitle,
            Content = editor,
            Owner = Window.GetWindow(this),
            Width = width,
            Height = height,
            MinWidth = Math.Min(820, width),
            MinHeight = Math.Min(560, height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(243, 245, 247))
        };
        editor.ModuleTitleChanged += title => window.Title = title;
        editor.CloseRequested += window.Close;
        window.Show();
    }

    private (double Width, double Height) GetPopupSize(bool existingRecord) => definition.Kind switch
    {
        CardEntityKind.Patients => (1280, 880),
        CardEntityKind.Depots => (1120, 850),
        CardEntityKind.Doctors when existingRecord => (900, 640),
        CardEntityKind.Pharmacies when existingRecord => (960, 660),
        CardEntityKind.Institutions or CardEntityKind.Faculties when existingRecord => (960, 650),
        _ => (1120, 760)
    };

    private void OpenUserMovements(string scope, IReadOnlyList<string>? movementTypes)
    {
        if (CardGrid.SelectedItem is not CardRow row) return;
        var today = DateTime.Today;
        RequestMovementPanel($"user.{row.Key}.{scope}", new QrMovementsLaunchOptions(new DateTime(today.Year, 1, 1), today, row.Key, movementTypes, "", ""));
    }
    private void OpenPharmacyMovements()
    {
        if (CardGrid.SelectedItem is not CardRow row) return;
        var today = DateTime.Today;
        var rawMovementKey = Convert.ToString(row["MovementKey"]);
        var movementKey = $"E{(string.IsNullOrWhiteSpace(rawMovementKey) ? row.Key : rawMovementKey)}";
        RequestMovementPanel($"pharmacy.{row.Key}", new QrMovementsLaunchOptions(new DateTime(today.Year, 1, 1), today, "", null, movementKey, "Eczane"));
    }
    private void OpenPatientMovements()
    {
        if (CardGrid.SelectedItem is not CardRow row) return;
        var today = DateTime.Today;
        var rawMovementKey = Convert.ToString(row["MovementKey"]);
        var movementKey = $"P{(string.IsNullOrWhiteSpace(rawMovementKey) ? row.Key : rawMovementKey)}";
        RequestMovementPanel($"patient.{row.Key}", new QrMovementsLaunchOptions(new DateTime(today.Year, 1, 1), today, "", null, movementKey, "Hasta"));
    }

    private async void OpenSelectedFamilyGroup()
    {
        if (CardGrid.SelectedItem is not CardRow row) return;
        if (row["FamilyGroupId"] is not int familyGroupId)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show("Bu hasta bir aile grubuna bağlı değil.", "Aile Grubu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var detail = await repository.LoadFamilyGroupDetailAsync(familyGroupId, CancellationToken.None);
            if (detail is null) throw new InvalidOperationException("Aile grubu bulunamadı.");
            new FamilyGroupDetailDialog(detail) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show($"Aile grubu yüklenemedi: {ex.Message}", "Aile Grubu", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void OpenSelectedPatientDebtHistory()
    {
        if (CardGrid.SelectedItem is not CardRow row
            || !int.TryParse(row.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var patientId)) return;
        TabRequested?.Invoke(new ModuleTabRequest(
            $"cards.patient-debt-history.{patientId}",
            $"Borç Geçmişi | {row.DisplayName}",
            () => new PatientDebtHistoryView(patientId, row.DisplayName),
            1050,
            560));
    }

    private async void OnOpenMedicationHistory(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedPatient(out var patientId, out var patientName)) return;
        try
        {
            var historyTask = loyalPatientRepository.LoadMedicationHistoryAsync(
                patientId, CancellationToken.None);
            var statusTask = loyalPatientRepository.LoadHistoryStatusAsync(
                patientId, CancellationToken.None);
            await Task.WhenAll(historyTask, statusTask);
            var history = await historyTask;
            var status = await statusTask;
            ShowHistoryOverlay(
                "İlaç Geçmişi", patientName, MedicationHistoryGrid, history, history.Count,
                history.Count == 0 && status.LastSyncError.Length > 0
                    ? $"İçe aktarım tamamlanamadı: {status.LastSyncError}"
                    : "İçe aktarılmış ilaç kaydı bulunamadı.");
        }
        catch (Exception exception)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show(
                $"İlaç geçmişi yüklenemedi: {exception.GetBaseException().Message}",
                "İlaç Geçmişi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnOpenReportHistory(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedPatient(out var patientId, out var patientName)) return;
        try
        {
            var historyTask = loyalPatientRepository.LoadReportHistoryAsync(
                patientId, CancellationToken.None);
            var statusTask = loyalPatientRepository.LoadHistoryStatusAsync(
                patientId, CancellationToken.None);
            await Task.WhenAll(historyTask, statusTask);
            var history = await historyTask;
            var status = await statusTask;
            ShowHistoryOverlay(
                "Rapor Geçmişi", patientName, ReportHistoryGrid, history, history.Count,
                status.ReportSyncedAt is not null
                    ? "Hastanın aktif raporu bulunamadı."
                    : status.LastSyncError.Length > 0
                        ? $"İçe aktarım tamamlanamadı: {status.LastSyncError}"
                        : "İçe aktarılmış rapor kaydı bulunamadı.");
        }
        catch (Exception exception)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show(
                $"Rapor geçmişi yüklenemedi: {exception.GetBaseException().Message}",
                "Rapor Geçmişi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryGetSelectedPatient(out int patientId, out string patientName)
    {
        patientId = 0;
        patientName = string.Empty;
        if (CardGrid.SelectedItem is not CardRow row
            || !int.TryParse(
                row.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out patientId))
            return false;
        patientName = row.DisplayName;
        return true;
    }

    private void ShowHistoryOverlay(
        string title,
        string patientName,
        DataGrid visibleGrid,
        System.Collections.IEnumerable rowsSource,
        int rowCount,
        string emptyMessage)
    {
        HistoryTitleText.Text = title;
        HistoryPatientText.Text = $"{patientName} · {rowCount:N0} kayıt";
        MedicationHistoryGrid.Visibility =
            ReferenceEquals(visibleGrid, MedicationHistoryGrid)
                ? Visibility.Visible
                : Visibility.Collapsed;
        ReportHistoryGrid.Visibility =
            ReferenceEquals(visibleGrid, ReportHistoryGrid)
                ? Visibility.Visible
                : Visibility.Collapsed;
        MedicationHistoryGrid.ItemsSource =
            ReferenceEquals(visibleGrid, MedicationHistoryGrid) ? rowsSource : null;
        ReportHistoryGrid.ItemsSource =
            ReferenceEquals(visibleGrid, ReportHistoryGrid) ? rowsSource : null;
        HistoryEmptyText.Visibility = rowCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryEmptyText.Text = emptyMessage;
        HistoryOverlay.Visibility = Visibility.Visible;
    }

    private void OnCloseHistoryOverlay(object sender, RoutedEventArgs e)
    {
        HistoryOverlay.Visibility = Visibility.Collapsed;
        MedicationHistoryGrid.ItemsSource = null;
        ReportHistoryGrid.ItemsSource = null;
    }

    private void RequestMovementPanel(string suffix, QrMovementsLaunchOptions options) => TabRequested?.Invoke(new ModuleTabRequest($"products.qr-movements.{suffix}", "Karekod Hareketleri (Yıllık)", () => new QrMovementsView(options), 1050, 560));
    private void SetStatus(string message, bool showRetry) { StatusText.Text = message; RetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed; StatusOverlay.Visibility = Visibility.Visible; }

}
