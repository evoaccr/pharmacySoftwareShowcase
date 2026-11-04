using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using pharmacyApp.Infrastructure;

namespace pharmacyApp;

public sealed class OpeningWindow : Window
{
    private const string EntryPageTitle = "Enter the app";
    private readonly WebView2 browser = new();
    private readonly TabControl tabs = new();
    private readonly Button previousButton = new() { Content = "Previous", MinWidth = 110, Padding = new Thickness(16, 8, 16, 8) };
    private readonly Button forwardButton = new() { Content = "Forward", MinWidth = 110, Padding = new Thickness(16, 8, 16, 8) };
    private readonly Button skipButton = new() { Content = "SKIP", MinWidth = 110, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button continueButton = new() { Content = "Continue", MinWidth = 110, Padding = new Thickness(16, 8, 16, 8) };
    private readonly CheckBox acknowledgementCheckBox = new()
    {
        Content = "I red and understand", IsChecked = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 16, 0)
    };
    private readonly IReadOnlyList<OpeningPage> pages;
    private bool ready;

    public OpeningWindow() : this(OpeningDocument.Load()) { }

    public OpeningWindow(IReadOnlyList<OpeningPage> pages)
    {
        var entry = pages.LastOrDefault(page => string.Equals(page.Title, EntryPageTitle, StringComparison.OrdinalIgnoreCase))
            ?? OpeningDocument.Parse(string.Empty)[0] with { Title = EntryPageTitle };
        this.pages = pages.Where(page => !string.Equals(page.Title, EntryPageTitle, StringComparison.OrdinalIgnoreCase)).Append(entry).ToArray();
        Title = "Pharmacy";
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1000, workArea.Width * .9); Height = Math.Min(730, workArea.Height * .9);
        MinWidth = Math.Min(540, Width); MinHeight = Math.Min(360, Height);
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var page in this.pages)
            tabs.Items.Add(new TabItem { Header = page.Title, Padding = new Thickness(16, 10, 16, 10) });
        tabs.SelectedIndex = 0;
        tabs.SelectionChanged += (_, _) => ShowPage();
        grid.Children.Add(tabs);
        Grid.SetRow(browser, 1); grid.Children.Add(browser);
        var footer = new DockPanel { Margin = new Thickness(16, 10, 16, 10), LastChildFill = true };
        previousButton.Click += (_, _) => tabs.SelectedIndex = Math.Max(0, tabs.SelectedIndex - 1);
        forwardButton.Click += (_, _) => tabs.SelectedIndex = Math.Min(this.pages.Count - 1, tabs.SelectedIndex + 1);
        skipButton.Click += (_, _) => tabs.SelectedIndex = this.pages.Count - 1;
        continueButton.Click += (_, _) =>
        {
            if (tabs.SelectedIndex == this.pages.Count - 1 && acknowledgementCheckBox.IsChecked == true) DialogResult = true;
        };
        acknowledgementCheckBox.Checked += (_, _) => UpdateNavigation();
        acknowledgementCheckBox.Unchecked += (_, _) => UpdateNavigation();
        DockPanel.SetDock(previousButton, Dock.Left); footer.Children.Add(previousButton);
        var nextButtons = new StackPanel { Orientation = Orientation.Horizontal };
        nextButtons.Children.Add(skipButton); nextButtons.Children.Add(forwardButton); nextButtons.Children.Add(continueButton);
        DockPanel.SetDock(nextButtons, Dock.Right); footer.Children.Add(nextButtons);
        footer.Children.Add(acknowledgementCheckBox);
        Grid.SetRow(footer, 2); grid.Children.Add(footer);
        Content = grid;
        ShowPage();
        ContentRendered += (_, _) => StartupErrors.NotifyVisible();
        Loaded += InitializeBrowser;
        Closed += (_, _) => browser.Dispose();
    }

    private async void InitializeBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(AppPaths.DataDirectory, "OpeningBrowser")).WaitAsync(TimeSpan.FromSeconds(20));
            if (!IsVisible) return;
            await browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));
            if (!IsVisible) return;
            browser.CoreWebView2.Settings.IsScriptEnabled = false;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.NavigationStarting += (_, args) =>
                args.Cancel = !args.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase) &&
                    !(!args.IsUserInitiated && args.Uri.StartsWith("data:text/html;", StringComparison.OrdinalIgnoreCase));
            browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            ready = true;
            ShowPage();
        }
        catch (Exception exception)
        {
            if (IsVisible)
            {
                StartupErrors.Kind = pharmacyApp.Startup.StartupFailureKind.Dependencies;
                StartupErrors.Dependency = pharmacyApp.Startup.StartupDiagnostics.BrowserRuntime;
                StartupErrors.Report(exception);
                DialogResult = false;
            }
        }
    }

    private void ShowPage()
    {
        UpdateNavigation();
        if (ready && tabs.SelectedIndex >= 0 && tabs.SelectedIndex < pages.Count)
            browser.CoreWebView2.NavigateToString(pages[tabs.SelectedIndex].Html);
    }

    private void UpdateNavigation()
    {
        var isLastPage = tabs.SelectedIndex == pages.Count - 1;
        previousButton.IsEnabled = tabs.SelectedIndex > 0;
        skipButton.Visibility = isLastPage ? Visibility.Collapsed : Visibility.Visible;
        forwardButton.Visibility = isLastPage ? Visibility.Collapsed : Visibility.Visible;
        forwardButton.IsDefault = !isLastPage;
        continueButton.Visibility = isLastPage ? Visibility.Visible : Visibility.Collapsed;
        continueButton.IsDefault = isLastPage;
        acknowledgementCheckBox.Visibility = isLastPage ? Visibility.Visible : Visibility.Collapsed;
        continueButton.IsEnabled = isLastPage && acknowledgementCheckBox.IsChecked == true;
        continueButton.Foreground = continueButton.IsEnabled ? SystemColors.ControlTextBrush : Brushes.Gray;
    }
}
