using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace pharmacyApp.Localization;

/// <summary>Localizes rendered text without rewriting repositories, selections or editable record fields.</summary>
public static class EnglishUi
{
    private sealed class State { public bool Updating; public bool Observed; public bool LifecycleObserved; public bool WindowObserved; public bool MenuObserved; }
    private static readonly ConditionalWeakTable<DependencyObject, State> States = new();
    private static DispatcherTimer? windowDiscovery;

    public static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnUnloaded));
        EventManager.RegisterClassHandler(typeof(TextBox), TextBoxBase.TextChangedEvent, new TextChangedEventHandler((sender, _) => TranslateElement((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { ToolTip: string tip } element) element.SetCurrentValue(FrameworkElement.ToolTipProperty, UiText.Translate(tip));
        }));
        // WPF can omit Loaded broadcasts for nodes that only have class handlers.
        // Discover window roots, then observe their real visual children as layout creates them.
        windowDiscovery = new DispatcherTimer(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(200) };
        windowDiscovery.Tick += (_, _) =>
        {
            foreach (Window window in Application.Current.Windows)
            {
                pharmacyApp.Infrastructure.EditionPresentation.RemoveWindowIcon(window);
                var state = States.GetOrCreateValue(window);
                if (state.WindowObserved) continue;
                state.WindowObserved = true;
                window.LayoutUpdated += (_, _) => Apply(window);
                Apply(window);
            }
        };
        Application.Current.Exit += (_, _) => windowDiscovery.Stop();
        windowDiscovery.Start();
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element) return;
        Apply(element);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element || !States.TryGetValue(element, out var state) || !state.Observed) return;
        var property = ObservedProperty(element);
        if (property is not null) DependencyPropertyDescriptor.FromProperty(property, element.GetType()).RemoveValueChanged(element, OnChanged);
        state.Observed = false;
    }

    private static DependencyProperty? ObservedProperty(DependencyObject element) => element switch
    {
        TextBlock => TextBlock.TextProperty,
        AccessText => AccessText.TextProperty,
        Window => Window.TitleProperty,
        ButtonBase => ContentControl.ContentProperty,
        MenuItem => HeaderedItemsControl.HeaderProperty,
        _ => null
    };

    private static void OnChanged(object? sender, EventArgs e) { if (sender is DependencyObject element) TranslateElement(element); }

    private static void TranslateElement(DependencyObject element)
    {
        var state = States.GetOrCreateValue(element);
        if (state.Updating) return;
        state.Updating = true;
        try
        {
            if (element is FrameworkElement { ToolTip: string tip } control)
                control.SetCurrentValue(FrameworkElement.ToolTipProperty, UiText.Translate(tip));
            switch (element)
            {
                case TextBlock block:
                    if (block.Inlines.Count <= 1 && block.Inlines.FirstInline is not Span &&
                        BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Mode != BindingMode.TwoWay)
                        SetText(block, TextBlock.TextProperty, block.Text);
                    foreach (var inline in block.Inlines.ToArray()) TranslateInline(inline);
                    break;
                case AccessText access: SetText(access, AccessText.TextProperty, access.Text); break;
                case TextBox { IsReadOnly: true } box:
                    if (box.DataContext is Modules.Home.ChatMessageViewModel { IsUser: true }) break;
                    var binding = BindingOperations.GetBinding(box, TextBox.TextProperty);
                    if (binding is null || binding.Mode is BindingMode.OneWay or BindingMode.OneTime) SetText(box, TextBox.TextProperty, box.Text);
                    break;
                case Window window: SetText(window, Window.TitleProperty, window.Title); break;
                case ButtonBase { Content: string content } button:
                    button.SetCurrentValue(AutomationProperties.NameProperty, UiText.Translate(content));
                    break;
                case MenuItem { Header: string header } menu:
                    menu.SetCurrentValue(AutomationProperties.NameProperty, UiText.Translate(header));
                    break;
            }
        }
        finally { state.Updating = false; }
    }

    private static void SetText(DependencyObject element, DependencyProperty property, string value)
    {
        var english = UiText.Translate(value);
        if (!string.Equals(value, english, StringComparison.Ordinal)) element.SetCurrentValue(property, english);
    }

    private static void TranslateInline(Inline inline)
    {
        if (inline is Run run && BindingOperations.GetBinding(run, Run.TextProperty)?.Mode != BindingMode.TwoWay)
            SetText(run, Run.TextProperty, run.Text);
        else if (inline is Span span)
            foreach (var child in span.Inlines.ToArray()) TranslateInline(child);
    }

    /// <summary>Also usable by the offline visual verifier, which does not run Loaded workflows.</summary>
    public static void Apply(DependencyObject root)
    {
        var state = States.GetOrCreateValue(root);
        if (!state.LifecycleObserved && root is FrameworkElement element)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            state.LifecycleObserved = true;
        }
        if (!state.Observed)
        {
            var property = ObservedProperty(root);
            if (property is not null) DependencyPropertyDescriptor.FromProperty(property, root.GetType()).AddValueChanged(root, OnChanged);
            state.Observed = true;
            TranslateElement(root);
        }
        if (root is FrameworkElement { ContextMenu: { } menu } && !States.GetOrCreateValue(menu).MenuObserved)
        {
            States.GetOrCreateValue(menu).MenuObserved = true;
            EventHandler update = (_, _) => Apply(menu);
            menu.Opened += (_, _) => { menu.LayoutUpdated += update; Apply(menu); };
            menu.Closed += (_, _) => menu.LayoutUpdated -= update;
        }
        if (root is Popup { IsOpen: true, Child: { } child }) Apply(child);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Apply(VisualTreeHelper.GetChild(root, i));
    }
}
