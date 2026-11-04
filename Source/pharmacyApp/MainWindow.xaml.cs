using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules;
using pharmacyApp.Modules.Settings;
using pharmacyApp.Modules.Home;
using pharmacyApp.AI.Models;

namespace pharmacyApp;

public partial class MainWindow : Window
{
    private const string TabDragFormat = "pharmacyApp.WorkspaceTab";
    private const string PanelDragFormat = "pharmacyApp.Panel";
    private readonly ModuleRegistry moduleRegistry = new();
    private readonly List<WorkspaceTab> tabs = [];
    private readonly Stack<WorkspaceTab> closedTabs = [];
    private readonly LinkedList<WorkspaceTab> activationHistory = [];
    private readonly Dictionary<PanelItem, PanelPopoutState> poppedPanels = [];
    private readonly DispatcherTimer tabHoverTimer;
    private WorkspaceTab? selected;
    private WorkspaceTab? hoverTarget;
    private Point dragStart;
    private PanelDropPlacement pendingPlacement = PanelDropPlacement.Right;

    public MainWindow()
    {
        InitializeComponent();
        DisplayMetrics.KeepMaximizedWindowInsideWorkArea(this);
        tabHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        tabHoverTimer.Tick += (_, _) => OpenHoveredTab();
        WorkspaceDropHost.DragOver += WorkspaceDragOver;
        WorkspaceDropHost.DragLeave += (_, _) => HideDropIndicator();
        WorkspaceDropHost.Drop += WorkspaceDrop;
        TabBarHost.DragOver += TabStripDragOver;
        TabBarHost.DragLeave += (_, _) => TabBarHost.Background = new SolidColorBrush(Color.FromRgb(228, 231, 234));
        TabBarHost.Drop += TabStripDrop;
        TopMenuHost.MouseLeftButtonDown += TopMenuHostMouseLeftButtonDown;
        MinimizeButton.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        MaximizeButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => SystemCommands.CloseWindow(this);
        StateChanged += (_, _) => UpdateCaptionGlyphs();
        PreviewKeyDown += MainWindowPreviewKeyDown;
        ApplicationSettingsStore.SettingsChanged += ApplyDisplaySettings;
        Closed += (_, _) => ApplicationSettingsStore.SettingsChanged -= ApplyDisplaySettings;
        ModuleCatalog.RegisterAll(moduleRegistry);
        BuildMenus();
        AddHomeTab();
        AddSettingsTab();
        ApplyDisplaySettings(ApplicationSettingsStore.Current);
    }

    private void BuildMenus()
    {
        TopMenuBar.Items.Clear();
        var roots = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);
        var submenus = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in moduleRegistry.Modules)
        {
            var rootName = module.MenuPath[0];
            if (!roots.TryGetValue(rootName, out var root))
            {
                root = new MenuItem { Header = rootName, Style = (Style)FindResource("TopMenu") };
                var dropdown = new ContextMenu { Placement = PlacementMode.Bottom };
                root.ContextMenu = dropdown;
                root.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    OpenDropdown(root, dropdown);
                    e.Handled = true;
                };
                roots.Add(rootName, root);
                TopMenuBar.Items.Add(root);
            }

            ItemsControl parent = root.ContextMenu!;
            var pathKey = rootName;
            for (var index = 1; index < module.MenuPath.Count; index++)
            {
                var segment = module.MenuPath[index];
                pathKey += "\u001F" + segment;
                if (!submenus.TryGetValue(pathKey, out var submenu))
                {
                    submenu = new MenuItem { Header = segment, MinWidth = 190 };
                    parent.Items.Add(submenu);
                    submenus.Add(pathKey, submenu);
                }
                parent = submenu;
            }

            var allowed = OnlineAccessPolicy.IsModuleEnabled(module.Id);
            object header = module.ButtonText;
            if (module.Id == "medula.prescription-list")
            {
                var label = new TextBlock();
                label.Inlines.Add(new System.Windows.Documents.Run("MIT") { Foreground = Brushes.Red, FontWeight = FontWeights.Bold });
                label.Inlines.Add(new System.Windows.Documents.Run(" local prescriptions"));
                header = label;
            }
            var button = new MenuItem { Header = header, MinWidth = 190, Tag = module.Id,
                Foreground = allowed ? Brushes.Black : Brushes.Gray, Opacity = allowed ? 1 : 0.65 };
            button.Click += (_, _) => OpenModule(module);
            parent.Items.Add(button);
        }
        foreach (var root in roots.Values)
        {
            var rootName = root.Header.ToString();
            root.IsEnabled = true;
        }
    }

    private static void OpenDropdown(MenuItem root, ContextMenu dropdown)
    {
        dropdown.PlacementTarget = root;
        dropdown.IsOpen = true;
    }

    private void AddHomeTab()
    {
        var content = new HomeScreenView(CreateAiApplicationContext);
        content.TabRequested += OpenRequestedTab;
        content.WorkspaceRequested += OpenRequestedWorkspace;
        var homePanel = new PanelItem("Ana Sayfa", content, 0, 0);
        var home = new WorkspaceTab("Ana Sayfa", true, new PanelLeaf(homePanel));
        tabs.Add(home);
        Select(home);
        RenderTabs();
    }

    private AiApplicationContext CreateAiApplicationContext()
    {
        var activeModuleTab = activationHistory.FirstOrDefault(tab => !tab.IsSystem && tabs.Contains(tab));
        var activePanel = activeModuleTab?.Root.Panels.FirstOrDefault();
        var moduleContext = activePanel is null
            ? null
            : FindModuleCapability<IAiApplicationContextSource>(activePanel.Content)?.GetAiApplicationContext();
        var currentUser = (Application.Current as App)?.CurrentUser;
        var settings = ApplicationSettingsStore.Current;
        var userDisplayName = string.IsNullOrWhiteSpace(settings.AiUserDisplayName)
            ? currentUser?.DisplayName
            : settings.AiUserDisplayName;
        return moduleContext is null
            ? new AiApplicationContext(
                CurrentModule: activePanel?.ModuleId ?? "home",
                CurrentScreen: activePanel?.Name ?? "Ana Sayfa",
                CurrentUserId: currentUser?.UserId,
                PharmacyName: settings.PharmacyName,
                PharmacistName: settings.PharmacistName,
                UserDisplayName: userDisplayName)
            : moduleContext with
            {
                CurrentUserId = currentUser?.UserId,
                PharmacyName = settings.PharmacyName,
                PharmacistName = settings.PharmacistName,
                UserDisplayName = userDisplayName
            };
    }

    private void AddSettingsTab()
    {
        var settingsPanel = new PanelItem(
            "Ayarlar",
            new DatabaseSettingsView(),
            0,
            0,
            "system.settings");
        tabs.Add(new WorkspaceTab("Ayarlar", false, new PanelLeaf(settingsPanel), true));
        RenderTabs();
    }

    private void OpenModule(ModuleDefinition module)
    {
        if (!OnlineAccessPolicy.IsModuleEnabled(module.Id)) { OnlineAccessPolicy.ShowRestricted(); return; }
        var existing = tabs.FirstOrDefault(tab => tab.Root.Panels.Any(panel => panel.ModuleId == module.Id));
        if (existing is not null)
        {
            Select(existing);
            RenderTabs();
            return;
        }

        var popped = poppedPanels.FirstOrDefault(pair => pair.Key.ModuleId == module.Id);
        if (popped.Key is not null)
        {
            popped.Value.Window.Activate();
            return;
        }

        var launch = module.Launch();
        if (launch.Panels.Count == 0) throw new InvalidOperationException($"Module '{module.Id}' did not define any panels.");
        PanelLayoutNode? root = null;
        foreach (var panelDefinition in launch.Panels)
        {
            if (panelDefinition.MinimumWidth <= 0 || panelDefinition.MinimumHeight <= 0)
                throw new InvalidOperationException($"Module '{module.Id}' panel '{panelDefinition.Title}' must define positive minimum dimensions.");
            var panel = CreatePanel(
                module.Id,
                panelDefinition.Title,
                panelDefinition.CreateContent(),
                panelDefinition.MinimumWidth,
                panelDefinition.MinimumHeight,
                module.CreateTabIcon);
            root = root is null
                ? new PanelLeaf(panel)
                : InsertNode(root, new PanelLeaf(panel), ToPanelDropPlacement(panelDefinition.Placement));
        }

        var tab = new WorkspaceTab(launch.TabTitle, false, root!, createIcon: module.CreateTabIcon);
        tabs.Add(tab);
        Select(tab);
        RenderTabs();
    }

    private static PanelDropPlacement ToPanelDropPlacement(ModulePanelPlacement placement) => placement switch
    {
        ModulePanelPlacement.Left => PanelDropPlacement.Left,
        ModulePanelPlacement.Top => PanelDropPlacement.Top,
        ModulePanelPlacement.Bottom => PanelDropPlacement.Bottom,
        _ => PanelDropPlacement.Right
    };

    private PanelItem CreatePanel(
        string moduleId,
        string name,
        FrameworkElement moduleContent,
        double minimumWidth,
        double minimumHeight,
        Func<FrameworkElement>? createTabIcon = null)
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(247, 250, 251)),
            Padding = new Thickness(10, 4, 12, 4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(221, 228, 232)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.SizeAll
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerTitle = new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.Normal,
            Foreground = (Brush)FindResource("Ink"),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerGrid.Children.Add(headerTitle);

        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 2, 0) };
        var sendBackButton = CreateHeaderButton("-", "Send panel behind");
        var popOutButton = CreateHeaderButton(CreateWindowSquareGlyph(), "Pop panel out");
        var closeButton = CreateHeaderButton("x", "Close panel");
        headerButtons.Children.Add(sendBackButton);
        headerButtons.Children.Add(popOutButton);
        headerButtons.Children.Add(closeButton);
        Grid.SetColumn(headerButtons, 1);
        headerGrid.Children.Add(headerButtons);
        header.Child = headerGrid;

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new ContentControl
        {
            Content = moduleContent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        });

        var content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(221, 228, 232)),
            BorderThickness = new Thickness(0),
            Background = Brushes.White,
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(8),
            Child = dock
        };
        PanelItem? panel = null;
        panel = new PanelItem(name, content, minimumWidth, minimumHeight, moduleId, createTabIcon);
        panel.AttachHeaderTitle(headerTitle);
        if (moduleContent is IModuleTitleSource titleSource)
        {
            panel.Rename(titleSource.CurrentModuleTitle);
            titleSource.ModuleTitleChanged += title => UpdateModuleTitle(panel!, title);
        }
        if (moduleContent is IModuleIdentitySource identitySource)
        {
            panel.ChangeModuleId(identitySource.CurrentModuleId);
            identitySource.ModuleIdentityChanged += moduleId => UpdateModuleIdentity(panel!, moduleId);
        }
        if (moduleContent is IModulePanelRequestSource panelRequestSource)
            panelRequestSource.PanelRequested += request => OpenRequestedPanel(panel!, request);
        if (moduleContent is IModuleTabRequestSource tabRequestSource)
            tabRequestSource.TabRequested += OpenRequestedTab;
        if (moduleContent is IModuleWorkspaceRequestSource workspaceRequestSource)
            workspaceRequestSource.WorkspaceRequested += OpenRequestedWorkspace;
        if (moduleContent is IModuleCloseRequestSource closeRequestSource)
            closeRequestSource.CloseRequested += () => ClosePanel(panel!);
        if (FindModuleCapability<IModuleBackgroundWorkSource>(moduleContent) is { } backgroundWorkSource)
        {
            backgroundWorkSource.BackgroundWorkStateChanged += _ =>
            {
                if (Dispatcher.CheckAccess()) RenderTabs();
                else Dispatcher.BeginInvoke(new Action(RenderTabs), DispatcherPriority.Background);
            };
        }
        header.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
                dragStart = e.GetPosition(this);
        };
        header.PreviewMouseMove += (_, e) =>
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
                BeginPanelDrag(header, panel!, e);
        };
        sendBackButton.Click += (_, e) => { panel!.SendToBack(); e.Handled = true; };
        popOutButton.Click += (_, e) => { PopOutPanel(panel!); e.Handled = true; };
        closeButton.Click += (_, e) => { ClosePanel(panel!); e.Handled = true; };
        return panel!;
    }

    private void UpdateModuleTitle(PanelItem panel, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        panel.Rename(title);
        if (poppedPanels.TryGetValue(panel, out var popout))
            popout.Window.Title = title;

        var owner = tabs.FirstOrDefault(tab => tab.Root.Panels.Contains(panel));
        if (owner is null || owner.Root.Panels.Count() != 1) return;
        owner.Rename(title);
        RenderTabs();
    }

    private void UpdateModuleIdentity(PanelItem panel, string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || string.Equals(panel.ModuleId, moduleId, StringComparison.Ordinal)) return;
        var isDetachedDetailOpeningItsOwnList = panel.ModuleId?.StartsWith("products.detail.", StringComparison.Ordinal) == true &&
            IsMedicationListModuleId(moduleId);
        if (isDetachedDetailOpeningItsOwnList)
        {
            panel.ChangeModuleId(moduleId);
            RenderTabs();
            return;
        }
        var duplicateTab = tabs.FirstOrDefault(tab => tab.Root.Panels.Any(candidate => !ReferenceEquals(candidate, panel) && candidate.ModuleId == moduleId));
        var duplicatePopout = poppedPanels.FirstOrDefault(pair => !ReferenceEquals(pair.Key, panel) && pair.Key.ModuleId == moduleId);
        if (duplicateTab is not null || duplicatePopout.Key is not null)
        {
            ClosePanel(panel);
            if (duplicateTab is not null) { Select(duplicateTab); RenderTabs(); }
            else duplicatePopout.Value.Window.Activate();
            return;
        }
        panel.ChangeModuleId(moduleId);
    }

    private void OpenRequestedPanel(PanelItem sourcePanel, ModulePanelRequest request)
    {
        var existingTab = tabs.FirstOrDefault(tab => tab.Root.Panels.Any(panel => panel.ModuleId == request.ModuleId));
        if (existingTab is not null)
        {
            Select(existingTab);
            existingTab.Root.Panels.First(panel => panel.ModuleId == request.ModuleId).BringToFront();
            RenderTabs();
            return;
        }
        var existingPopout = poppedPanels.FirstOrDefault(pair => pair.Key.ModuleId == request.ModuleId);
        if (existingPopout.Key is not null)
        {
            existingPopout.Value.Window.Activate();
            return;
        }
        var owner = tabs.FirstOrDefault(tab => tab.Root.Panels.Contains(sourcePanel));
        if (owner is null) return;
        var requested = CreatePanel(
            request.ModuleId,
            request.Title,
            request.CreateContent(),
            request.MinimumWidth,
            request.MinimumHeight,
            request.CreateTabIcon ?? ResolveTabIconFactory(request.ModuleId));
        var anchor = string.IsNullOrWhiteSpace(request.AnchorModuleId) ? null : owner.Root.Panels.FirstOrDefault(panel => panel.ModuleId == request.AnchorModuleId);
        owner.Root = anchor is null
            ? InsertPanel(owner.Root, requested, ToPanelDropPlacement(request.Placement))
            : InsertPanelBeside(owner.Root, anchor, requested, ToPanelDropPlacement(request.Placement));
        Select(owner);
        RenderTabs();
    }

    private void OpenRequestedTab(ModuleTabRequest request)
    {
        var existingTab = tabs.FirstOrDefault(tab => tab.Root.Panels.Any(panel => panel.ModuleId == request.ModuleId));
        if (existingTab is not null)
        {
            Select(existingTab);
            existingTab.Root.Panels.First(panel => panel.ModuleId == request.ModuleId).BringToFront();
            RenderTabs();
            return;
        }

        var existingPopout = poppedPanels.FirstOrDefault(pair => pair.Key.ModuleId == request.ModuleId);
        if (existingPopout.Key is not null)
        {
            existingPopout.Value.Window.Activate();
            return;
        }

        if (request.MinimumWidth <= 0 || request.MinimumHeight <= 0)
            throw new InvalidOperationException($"Requested tab '{request.ModuleId}' must define positive minimum dimensions.");
        var panel = CreatePanel(
            request.ModuleId,
            request.Title,
            request.CreateContent(),
            request.MinimumWidth,
            request.MinimumHeight,
            request.CreateTabIcon ?? ResolveTabIconFactory(request.ModuleId));
        var tab = new WorkspaceTab(
            request.Title,
            false,
            new PanelLeaf(panel),
            createIcon: panel.CreateTabIcon ?? ResolveTabIconFactory(request.ModuleId));
        tabs.Add(tab);
        Select(tab);
        RenderTabs();
    }

    private void OpenRequestedWorkspace(ModuleWorkspaceRequest request)
    {
        var requestedModules = request.ModuleIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(moduleId => moduleRegistry.Modules.FirstOrDefault(module =>
                string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase)))
            .Where(module => module is not null)
            .Select(module => module!)
            .ToArray();
        if (requestedModules.Length == 0) return;

        var requestedLaunches = requestedModules
            .Select(module => (Module: module, Launch: module.Launch()))
            .ToArray();
        var workspaceKey = Guid.NewGuid().ToString("N");
        PanelLayoutNode? root = null;
        for (var moduleIndex = 0; moduleIndex < requestedLaunches.Length; moduleIndex++)
        {
            var (module, launch) = requestedLaunches[moduleIndex];
            for (var panelIndex = 0; panelIndex < launch.Panels.Count; panelIndex++)
            {
                var definition = launch.Panels[panelIndex];
                if (definition.MinimumWidth <= 0 || definition.MinimumHeight <= 0)
                    throw new InvalidOperationException($"Module '{module.Id}' panel '{definition.Title}' must define positive minimum dimensions.");
                var panel = CreatePanel(
                    $"{module.Id}.ai.{workspaceKey}.{moduleIndex}.{panelIndex}",
                    definition.Title,
                    definition.CreateContent(),
                    definition.MinimumWidth,
                    definition.MinimumHeight,
                    module.CreateTabIcon);
                root = root is null
                    ? new PanelLeaf(panel)
                    : InsertNode(root, new PanelLeaf(panel), request.Layout == ModuleWorkspaceLayout.Stacked
                        ? PanelDropPlacement.Bottom
                        : PanelDropPlacement.Right);
            }
        }
        if (root is null) return;
        var title = string.IsNullOrWhiteSpace(request.TabTitle)
            ? string.Join(" + ", requestedLaunches.Select(item => item.Launch.TabTitle))
            : request.TabTitle.Trim();
        var tab = new WorkspaceTab(title, false, root, createIcon: requestedModules[0].CreateTabIcon);
        tabs.Add(tab);
        Select(tab);
        RenderTabs();
    }
    private static PanelLayoutNode InsertPanelBeside(PanelLayoutNode node, PanelItem anchor, PanelItem inserted, PanelDropPlacement placement)
    {
        if (node is PanelLeaf leaf)
            return ReferenceEquals(leaf.Panel, anchor) ? InsertPanel(node, inserted, placement) : node;
        var split = (PanelSplit)node;
        return CopySplit(split,
            InsertPanelBeside(split.First, anchor, inserted, placement),
            InsertPanelBeside(split.Second, anchor, inserted, placement));
    }
    private static Button CreateHeaderButton(object content, string toolTip) => new()
    {
        Content = content,
        ToolTip = toolTip,
        Width = 22,
        Height = 20,
        Padding = new Thickness(0),
        Margin = new Thickness(2, 0, 0, 0),
        Background = Brushes.Transparent,
        BorderBrush = new SolidColorBrush(Color.FromRgb(205, 215, 221)),
        BorderThickness = new Thickness(1),
        Foreground = new SolidColorBrush(Color.FromRgb(73, 85, 101)),
        FontSize = 11
    };

    private static Path CreateWindowSquareGlyph() => new()
    {
        Data = Geometry.Parse("M 2.5,2.5 L 11.5,2.5 L 11.5,11.5 L 2.5,11.5 Z"),
        Stroke = new SolidColorBrush(Color.FromRgb(73, 85, 101)),
        StrokeThickness = 1,
        Width = 14,
        Height = 14,
        Stretch = Stretch.None,
        SnapsToDevicePixels = true
    };

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return null;
    }

    private void RenderTabs()
    {
        RefreshModuleTabContexts();
        HomeTabStrip.Children.Clear();
        TabStrip.Children.Clear();
        SettingsTabStrip.Children.Clear();
        foreach (var tab in tabs)
        {
            var backgroundWorkRunning = IsBackgroundWorkRunning(tab);
            var button = new Button
            {
                Tag = tab,
                Content = CreateTabContent(tab),
                ToolTip = backgroundWorkRunning
                    ? "Arka planda çalışıyor"
                    : tab.IsSettings ? "Ayarlar" : tab.IsHome ? "Ana Sayfa" : "Ana çalışma alanına sürükleyin",
                MinWidth = tab.IsSystem ? 42 : 126,
                Height = 40,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 5, 0),
                BorderThickness = new Thickness(1),
                Style = (Style)FindResource("TabButtonStyle"),
                AllowDrop = true
            };
            ApplyTabStyle(button, tab);
            button.Click += (_, _) =>
            {
                Select(tab);
                UpdateTabStyles();
            };
            button.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
            button.PreviewMouseMove += (_, e) => BeginTabDrag(button, tab, e);
            button.DragOver += (_, e) => HoverTabDuringDrag(tab, e);
            button.DragLeave += (_, _) => CancelTabHover(tab);
            button.Drop += (_, e) =>
            {
                CancelTabHover();
                if (e.Data.GetData(PanelDragFormat) is PanelItem panel)
                {
                    ConvertPanelToTab(panel);
                    e.Effects = DragDropEffects.Move;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
                e.Handled = true;
            };
            button.ContextMenu = BuildContextMenu(tab);
            (tab.IsSettings ? SettingsTabStrip : tab.IsHome ? HomeTabStrip : TabStrip).Children.Add(button);
            if (selected == tab && !tab.IsSystem)
                Dispatcher.BeginInvoke(new Action(button.BringIntoView), DispatcherPriority.Loaded);
        }
    }

    private object CreateTabContent(WorkspaceTab tab)
    {
        if (tab.IsSettings) return "⚙";
        if (tab.IsHome) return "⌂";

        var container = new Grid { VerticalAlignment = VerticalAlignment.Center };
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center });
        container.Children.Add(row);
        if (IsBackgroundWorkRunning(tab))
        {
            var track = new Canvas
            {
                Width = 82,
                Height = 2,
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(66, 91, 111))
            };
            var pulse = new Rectangle
            {
                Width = 30,
                Height = 2,
                RadiusX = 0,
                RadiusY = 0,
                Fill = new SolidColorBrush(Color.FromRgb(45, 212, 191))
            };
            track.Children.Add(pulse);
            pulse.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
            {
                From = -30,
                To = 82,
                Duration = TimeSpan.FromMilliseconds(900),
                RepeatBehavior = RepeatBehavior.Forever
            });
            Grid.SetRow(track, 1);
            container.Children.Add(track);
        }
        return container;
    }

    private static bool IsBackgroundWorkRunning(WorkspaceTab tab) => tab.Root.Panels.Any(panel =>
        FindModuleCapability<IModuleBackgroundWorkSource>(panel.Content)?.IsBackgroundWorkRunning == true);

    private Func<FrameworkElement>? ResolveTabIconFactory(string? moduleId) => moduleRegistry.Modules
        .FirstOrDefault(module => string.Equals(module.Id, moduleId, StringComparison.Ordinal))
        ?.CreateTabIcon;

    private void RefreshModuleTabContexts()
    {
        foreach (var tab in tabs)
        {
            var panels = tab.Root.Panels.ToArray();
            foreach (var panel in panels)
            {
                var hasOtherMedicationList = panels.Any(candidate =>
                    !ReferenceEquals(candidate, panel) && IsMedicationListModuleId(candidate.ModuleId));
                FindModuleCapability<IModuleTabContextAware>(panel.Content)
                    ?.SetMedicationListAvailableInCurrentTab(hasOtherMedicationList);
            }
        }

        foreach (var panel in poppedPanels.Keys)
            FindModuleCapability<IModuleTabContextAware>(panel.Content)
                ?.SetMedicationListAvailableInCurrentTab(false);
    }

    private static bool IsMedicationListModuleId(string? moduleId) =>
        string.Equals(moduleId, "products.catalog", StringComparison.Ordinal) ||
        string.Equals(moduleId, "products.stock", StringComparison.Ordinal);

    private void BeginTabDrag(Button button, WorkspaceTab tab, MouseEventArgs e)
    {
        if (tab.IsSystem || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - dragStart).Length <= 8) return;
        var data = new DataObject(TabDragFormat, tab);
        DragDrop.DoDragDrop(button, data, DragDropEffects.Move);
        CancelTabHover();
        HideDropIndicator();
        RenderTabs();
    }

    private void BeginPanelDrag(Border header, PanelItem panel, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - dragStart).Length <= 8) return;
        var data = new DataObject(PanelDragFormat, panel);
        DragDrop.DoDragDrop(header, data, DragDropEffects.Move);
        CancelTabHover();
        HideDropIndicator();
        RenderTabs();
    }

    private void HoverTabDuringDrag(WorkspaceTab target, DragEventArgs e)
    {
        var sourceTab = GetDraggedSourceTab(e.Data);
        if (sourceTab is null || sourceTab == target || target.IsSystem)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        if (hoverTarget == target) return;
        hoverTarget = target;
        tabHoverTimer.Stop();
        tabHoverTimer.Start();
    }

    private WorkspaceTab? GetDraggedSourceTab(IDataObject data)
    {
        if (data.GetData(TabDragFormat) is WorkspaceTab tab) return tab;
        if (data.GetData(PanelDragFormat) is PanelItem panel)
            return tabs.FirstOrDefault(candidate => candidate.Root.Panels.Contains(panel));
        return null;
    }

    private void OpenHoveredTab()
    {
        tabHoverTimer.Stop();
        if (hoverTarget is null || selected == hoverTarget) return;
        Select(hoverTarget);
        UpdateTabStyles();
    }

    private void CancelTabHover(WorkspaceTab? target = null)
    {
        if (target is not null && hoverTarget != target) return;
        tabHoverTimer.Stop();
        hoverTarget = null;
    }

    private void TabStripDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(PanelDragFormat) is PanelItem)
        {
            TabBarHost.Background = new SolidColorBrush(Color.FromRgb(211, 223, 232));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void TabStripDrop(object sender, DragEventArgs e)
    {
        TabBarHost.Background = new SolidColorBrush(Color.FromRgb(228, 231, 234));
        if (e.Data.GetData(PanelDragFormat) is PanelItem panel)
        {
            ConvertPanelToTab(panel);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void ConvertPanelToTab(PanelItem panel)
    {
        var source = tabs.FirstOrDefault(tab => tab.Root.Panels.Contains(panel));
        if (source is null || !TryRemovePanel(source.Root, panel, out var remaining)) return;
        if (remaining is null) tabs.Remove(source);
        else source.Root = remaining;

        var newTab = new WorkspaceTab(
            panel.Name,
            false,
            new PanelLeaf(panel),
            createIcon: panel.CreateTabIcon ?? ResolveTabIconFactory(panel.ModuleId));
        tabs.Add(newTab);
        Select(newTab);
        RenderTabs();
    }

    private void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ApplicationSettingsStore.Current.ShortcutsEnabled) return;
        var modifiers = Keyboard.Modifiers;
        if (!modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt)) return;
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var handled = false;

        if (e.Key == Key.Tab)
        {
            SelectAdjacentTab(shift ? -1 : 1);
            handled = true;
        }
        else if (shift && e.Key == Key.T)
        {
            ReopenLastClosedTab();
            handled = true;
        }
        else if (!shift && e.Key == Key.W)
        {
            if (selected is not null) RemoveTab(selected);
            handled = true;
        }
        else if (!shift && e.Key == Key.N)
        {
            if (selected is not null) PopOut(selected);
            handled = true;
        }
        else if (!shift && e.Key == Key.Q)
        {
            SelectLastActiveTab();
            handled = true;
        }
        else if (e.Key == Key.OemQuotes || e.Key == Key.Oem3 || (shift && e.Key == Key.D2))
        {
            Select(tabs.FirstOrDefault(tab => tab.IsHome));
            UpdateTabStyles();
            handled = true;
        }
        else if (!shift && e.Key is Key.D0 or Key.NumPad0)
        {
            Select(tabs.FirstOrDefault(tab => tab.IsSettings));
            UpdateTabStyles();
            handled = true;
        }
        else if (!shift && TryGetTabNumber(e.Key, out var tabNumber))
        {
            var regularTab = tabs.Where(tab => !tab.IsSystem).ElementAtOrDefault(tabNumber - 1);
            if (regularTab is not null)
            {
                Select(regularTab);
                UpdateTabStyles();
            }
            handled = true;
        }

        e.Handled = handled;
    }

    private static bool TryGetTabNumber(Key key, out int number)
    {
        if (key is >= Key.D1 and <= Key.D9)
        {
            number = (int)key - (int)Key.D1 + 1;
            return true;
        }
        if (key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            number = (int)key - (int)Key.NumPad1 + 1;
            return true;
        }
        number = 0;
        return false;
    }

    private void SelectAdjacentTab(int direction)
    {
        var ordered = GetVisualTabOrder().ToArray();
        if (ordered.Length == 0) return;
        var current = Array.IndexOf(ordered, selected);
        var next = current < 0 ? 0 : (current + direction + ordered.Length) % ordered.Length;
        Select(ordered[next]);
        UpdateTabStyles();
    }

    private IEnumerable<WorkspaceTab> GetVisualTabOrder() =>
        tabs.Where(tab => tab.IsHome)
            .Concat(tabs.Where(tab => !tab.IsSystem))
            .Concat(tabs.Where(tab => tab.IsSettings));

    private void SelectLastActiveTab()
    {
        var previous = activationHistory.FirstOrDefault(tab => tab != selected && tabs.Contains(tab));
        if (previous is null) return;
        Select(previous);
        UpdateTabStyles();
    }

    private void ReopenLastClosedTab()
    {
        while (closedTabs.TryPop(out var tab))
        {
            var duplicate = tabs.FirstOrDefault(candidate => candidate.Root.Panels.Any(openPanel =>
                tab.Root.Panels.Any(closedPanel => closedPanel.ModuleId is not null && closedPanel.ModuleId == openPanel.ModuleId)));
            if (duplicate is not null)
            {
                Select(duplicate);
                RenderTabs();
                return;
            }

            tabs.Add(tab);
            Select(tab);
            RenderTabs();
            return;
        }
    }

    private void TabScrollViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (NormalTabsScrollViewer.ScrollableWidth <= 0) return;
        NormalTabsScrollViewer.ScrollToHorizontalOffset(NormalTabsScrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void ApplyDisplaySettings(ApplicationSettings settings)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplyDisplaySettings(settings));
            return;
        }

        var requested = DisplayMetrics.ToDeviceIndependentSize(new DisplayResolution(settings.ScreenWidth, settings.ScreenHeight));
        var workArea = SystemParameters.WorkArea;
        WindowState = WindowState.Normal;
        Width = Math.Max(MinWidth, Math.Min(requested.Width, workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(requested.Height, workArea.Height));
        if (settings.StartFullscreen) WindowState = WindowState.Maximized;
        UpdateCaptionGlyphs();
    }

    private void TopMenuHostMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<MenuItem>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void UpdateCaptionGlyphs()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabStyles()
    {
        foreach (var strip in new[] { HomeTabStrip, TabStrip, SettingsTabStrip })
            foreach (Button button in strip.Children)
                if (button.Tag is WorkspaceTab tab) ApplyTabStyle(button, tab);
    }

    private void ApplyTabStyle(Button button, WorkspaceTab tab)
    {
        button.BorderBrush = selected == tab ? new SolidColorBrush(Color.FromRgb(49, 90, 115)) : new SolidColorBrush(Color.FromRgb(200, 205, 210));
        button.BorderThickness = selected == tab ? new Thickness(1, 1, 1, 0) : new Thickness(1);
        button.Background = selected == tab ? Brushes.White : new SolidColorBrush(Color.FromRgb(235, 237, 239));
        button.Foreground = selected == tab ? new SolidColorBrush(Color.FromRgb(49, 90, 115)) : new SolidColorBrush(Color.FromRgb(54, 64, 73));
        button.FontWeight = selected == tab ? FontWeights.SemiBold : FontWeights.Normal;
        button.FontSize = tab.IsSystem ? 20 : 13;
    }

    private void WorkspaceDragOver(object sender, DragEventArgs e)
    {
        var draggedTab = e.Data.GetData(TabDragFormat) as WorkspaceTab;
        var draggedPanel = e.Data.GetData(PanelDragFormat) as PanelItem;
        if (selected is null || selected.IsSystem || (draggedTab is null && draggedPanel is null) || draggedTab == selected ||
            (draggedPanel is not null && selected.Root.Panels.Count() == 1 && selected.Root.Panels.Contains(draggedPanel)))
        {
            e.Effects = DragDropEffects.None;
            HideDropIndicator();
            return;
        }

        pendingPlacement = DeterminePlacement(e.GetPosition(WorkspaceDropHost));
        ShowDropIndicator(pendingPlacement);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void WorkspaceDrop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        CancelTabHover();
        if (selected is WorkspaceTab target && !target.IsSystem && e.Data.GetData(TabDragFormat) is WorkspaceTab source && !source.IsSystem && source != target)
        {
            if (!ConfirmMultiPanelMerge(source, target))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            CombineTabs(source, target, pendingPlacement);
            e.Effects = DragDropEffects.Move;
        }
        else if (selected is WorkspaceTab panelTarget && !panelTarget.IsSystem && e.Data.GetData(PanelDragFormat) is PanelItem panel)
        {
            MovePanel(panel, panelTarget, pendingPlacement);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private bool ConfirmMultiPanelMerge(WorkspaceTab source, WorkspaceTab target)
    {
        var panelCount = source.Root.Panels.Count();
        if (panelCount <= 2) return true;
        var answer = pharmacyApp.Localization.EnglishMessageBox.Show(
            this,
            $"'{source.Title}' contains {panelCount} panels. Are you sure you want to combine it with '{target.Title}'?",
            "Combine Tabs",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }

    private PanelDropPlacement DeterminePlacement(Point point)
    {
        var rowBand = WorkspaceDropHost.ActualHeight * 0.18;
        if (point.Y <= rowBand) return PanelDropPlacement.Top;
        if (point.Y >= WorkspaceDropHost.ActualHeight - rowBand) return PanelDropPlacement.Bottom;
        return point.X < WorkspaceDropHost.ActualWidth / 2 ? PanelDropPlacement.Left : PanelDropPlacement.Right;
    }

    private void ShowDropIndicator(PanelDropPlacement placement)
    {
        DropIndicator.Visibility = Visibility.Visible;
        DropIndicator.Margin = new Thickness(8);
        DropIndicator.Width = double.NaN;
        DropIndicator.Height = double.NaN;
        DropIndicator.HorizontalAlignment = HorizontalAlignment.Stretch;
        DropIndicator.VerticalAlignment = VerticalAlignment.Stretch;

        switch (placement)
        {
            case PanelDropPlacement.Left:
                DropIndicator.Width = WorkspaceDropHost.ActualWidth * 0.46;
                DropIndicator.HorizontalAlignment = HorizontalAlignment.Left;
                DropIndicatorText.Text = "Column · Left";
                break;
            case PanelDropPlacement.Right:
                DropIndicator.Width = WorkspaceDropHost.ActualWidth * 0.46;
                DropIndicator.HorizontalAlignment = HorizontalAlignment.Right;
                DropIndicatorText.Text = "Column · Right";
                break;
            case PanelDropPlacement.Top:
                DropIndicator.Height = WorkspaceDropHost.ActualHeight * 0.34;
                DropIndicator.VerticalAlignment = VerticalAlignment.Top;
                DropIndicatorText.Text = "Row · Top";
                break;
            case PanelDropPlacement.Bottom:
                DropIndicator.Height = WorkspaceDropHost.ActualHeight * 0.34;
                DropIndicator.VerticalAlignment = VerticalAlignment.Bottom;
                DropIndicatorText.Text = "Row · Bottom";
                break;
        }
    }

    private void HideDropIndicator() => DropIndicator.Visibility = Visibility.Collapsed;

    private void CombineTabs(WorkspaceTab source, WorkspaceTab target, PanelDropPlacement placement)
    {
        target.Root = InsertNode(target.Root, source.Root, placement);
        tabs.Remove(source);
        Select(target);
        RenderTabs();
    }

    private void MovePanel(PanelItem panel, WorkspaceTab target, PanelDropPlacement placement)
    {
        var source = tabs.FirstOrDefault(tab => tab.Root.Panels.Contains(panel));
        if (source is null) return;
        if (!TryRemovePanel(source.Root, panel, out var remaining)) return;

        if (source == target)
        {
            if (remaining is null) return;
            target.Root = InsertPanel(remaining, panel, placement);
        }
        else
        {
            if (remaining is null) tabs.Remove(source);
            else source.Root = remaining;
            target.Root = InsertPanel(target.Root, panel, placement);
        }

        Select(target);
        RenderTabs();
    }

    private static PanelLayoutNode InsertPanel(PanelLayoutNode targetRoot, PanelItem panel, PanelDropPlacement placement)
        => InsertNode(targetRoot, new PanelLeaf(panel), placement);

    private static PanelLayoutNode InsertNode(PanelLayoutNode targetRoot, PanelLayoutNode insertedNode, PanelDropPlacement placement)
    {
        var horizontal = placement is PanelDropPlacement.Left or PanelDropPlacement.Right;
        var insertedFirst = placement is PanelDropPlacement.Left or PanelDropPlacement.Top;
        return new PanelSplit(
            horizontal ? Orientation.Horizontal : Orientation.Vertical,
            insertedFirst ? insertedNode : targetRoot,
            insertedFirst ? targetRoot : insertedNode);
    }

    private static bool TryRemovePanel(PanelLayoutNode node, PanelItem panel, out PanelLayoutNode? remaining)
    {
        if (node is PanelLeaf leaf)
        {
            remaining = ReferenceEquals(leaf.Panel, panel) ? null : node;
            return ReferenceEquals(leaf.Panel, panel);
        }

        var split = (PanelSplit)node;
        if (TryRemovePanel(split.First, panel, out var firstRemaining))
        {
            remaining = firstRemaining is null
                ? split.Second
                : CopySplit(split, firstRemaining, split.Second);
            return true;
        }
        if (TryRemovePanel(split.Second, panel, out var secondRemaining))
        {
            remaining = secondRemaining is null
                ? split.First
                : CopySplit(split, split.First, secondRemaining);
            return true;
        }
        remaining = node;
        return false;
    }

    private static PanelSplit CopySplit(PanelSplit original, PanelLayoutNode first, PanelLayoutNode second) => new(original.Orientation, first, second)
    {
        BoundaryRatio = original.BoundaryRatio,
        OverlapSide = original.OverlapSide
    };

    private void ClosePanel(PanelItem panel)
    {
        if (poppedPanels.TryGetValue(panel, out var popout))
        {
            popout.ReturnOnClose = false;
            popout.Window.Close();
            return;
        }

        var owner = tabs.FirstOrDefault(tab => tab.Root.Panels.Contains(panel));
        if (owner is null || !TryRemovePanel(owner.Root, panel, out var remaining)) return;
        if (remaining is null)
        {
            tabs.Remove(owner);
            Select(tabs.LastOrDefault());
        }
        else
        {
            owner.Root = remaining;
            Select(owner);
        }
        RenderTabs();
    }

    private void PopOutPanel(PanelItem panel)
    {
        if (poppedPanels.TryGetValue(panel, out var existing))
        {
            existing.Window.Activate();
            return;
        }

        var owner = tabs.FirstOrDefault(tab => tab.Root.Panels.Contains(panel));
        if (owner is null || !TryRemovePanel(owner.Root, panel, out var remaining)) return;
        if (remaining is null) tabs.Remove(owner);
        else owner.Root = remaining;

        FindPanelHostAware(panel.Content)?.SetHostedInPopout(true);
        FindModuleCapability<IModuleTabContextAware>(panel.Content)?.SetMedicationListAvailableInCurrentTab(false);
        var window = new Window
        {
            Title = panel.Name,
            Width = Math.Max(540, panel.MinimumWidth + 40),
            Height = Math.Max(380, panel.MinimumHeight + 40),
            MinWidth = panel.MinimumWidth,
            MinHeight = panel.MinimumHeight,
            Background = (Brush)FindResource("Canvas"),
            Content = new DockSurface(new PanelLeaf(panel)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (IsLoaded && PresentationSource.FromVisual(this) is not null) window.Owner = this;
        var state = new PanelPopoutState(window, owner);
        poppedPanels.Add(panel, state);
        window.Closed += (_, _) => RestorePoppedPanel(panel, state);
        Select(remaining is null ? tabs.LastOrDefault() : owner);
        RenderTabs();
        window.Show();
    }

    private void RestorePoppedPanel(PanelItem panel, PanelPopoutState state)
    {
        poppedPanels.Remove(panel);
        FindPanelHostAware(panel.Content)?.SetHostedInPopout(false);
        if (!state.ReturnOnClose) return;

        if (tabs.Contains(state.OriginalTab))
        {
            state.OriginalTab.Root = InsertPanel(state.OriginalTab.Root, panel, PanelDropPlacement.Right);
            Select(state.OriginalTab);
        }
        else
        {
            var restored = new WorkspaceTab(
                panel.Name,
                false,
                new PanelLeaf(panel),
                createIcon: panel.CreateTabIcon ?? ResolveTabIconFactory(panel.ModuleId));
            tabs.Add(restored);
            Select(restored);
        }
        RenderTabs();
    }

    private static IPanelHostModeAware? FindPanelHostAware(FrameworkElement element)
    {
        if (element is IPanelHostModeAware aware) return aware;
        if (element is Border { Child: FrameworkElement child }) return FindPanelHostAware(child);
        if (element is DockPanel dock)
            return dock.Children.OfType<FrameworkElement>().Select(FindPanelHostAware).FirstOrDefault(value => value is not null);
        if (element is ContentControl { Content: FrameworkElement content }) return FindPanelHostAware(content);
        return null;
    }

    private static T? FindModuleCapability<T>(FrameworkElement element) where T : class
    {
        if (element is T capability) return capability;
        if (element is Border { Child: FrameworkElement child }) return FindModuleCapability<T>(child);
        if (element is DockPanel dock)
            return dock.Children.OfType<FrameworkElement>().Select(FindModuleCapability<T>).FirstOrDefault(value => value is not null);
        if (element is ContentControl { Content: FrameworkElement content }) return FindModuleCapability<T>(content);
        return null;
    }
    private ContextMenu BuildContextMenu(WorkspaceTab tab)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Kaldır" };
        remove.Click += (_, _) => RemoveTab(tab);
        var popup = new MenuItem { Header = "Ayrı Pencerede Aç" };
        popup.Click += (_, _) => PopOut(tab);
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        menu.Items.Add(popup);
        return menu;
    }

    private void RemoveTab(WorkspaceTab tab)
    {
        if (tab.IsSystem)
        {
            pharmacyApp.Localization.EnglishMessageBox.Show(
                tab.IsSettings ? "Ayarlar sekmesi kaldırılamaz." : "Ana Sayfa sekmesi kaldırılamaz.",
                "Bilgi",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        tabs.Remove(tab);
        activationHistory.Remove(tab);
        closedTabs.Push(tab);
        Select(tabs.LastOrDefault());
        RenderTabs();
    }

    private void PopOut(WorkspaceTab tab)
    {
        if (tab.IsSystem) return;
        foreach (var panel in tab.Root.Panels) FindPanelHostAware(panel.Content)?.SetHostedInPopout(true);
        var window = new Window
        {
            Title = tab.Title,
            Width = 820,
            Height = 560,
            MinWidth = 540,
            MinHeight = 380,
            Background = (Brush)FindResource("Canvas"),
            Content = new DockSurface(tab.Root),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (IsLoaded && PresentationSource.FromVisual(this) is not null) window.Owner = this;
        tabs.Remove(tab);
        Select(tabs.LastOrDefault());
        RenderTabs();
        window.Closed += (_, _) =>
        {
            foreach (var panel in tab.Root.Panels) FindPanelHostAware(panel.Content)?.SetHostedInPopout(false);
            if (!tabs.Contains(tab))
            {
                tabs.Add(tab);
                Select(tab);
                RenderTabs();
            }
        };
        window.Show();
    }

    private void Select(WorkspaceTab? tab)
    {
        selected = tab;
        if (tab is not null)
        {
            activationHistory.Remove(tab);
            activationHistory.AddFirst(tab);
        }
        EmptyState.Visibility = tab is null ? Visibility.Visible : Visibility.Collapsed;
        Workspace.Content = tab is null ? null : new DockSurface(tab.Root);
    }

    private enum PanelDropPlacement { Left, Right, Top, Bottom }

    private sealed class WorkspaceTab(
        string title,
        bool isHome,
        PanelLayoutNode root,
        bool isSettings = false,
        Func<FrameworkElement>? createIcon = null)
    {
        public string Title { get; private set; } = title;
        public bool IsHome { get; } = isHome;
        public bool IsSettings { get; } = isSettings;
        public bool IsSystem => IsHome || IsSettings;
        public Func<FrameworkElement>? CreateIcon { get; } = createIcon;
        public PanelLayoutNode Root { get; set; } = root;
        public void Rename(string title) => Title = title;
    }

    private sealed class PanelPopoutState(Window window, WorkspaceTab originalTab)
    {
        public Window Window { get; } = window;
        public WorkspaceTab OriginalTab { get; } = originalTab;
        public bool ReturnOnClose { get; set; } = true;
    }
}
