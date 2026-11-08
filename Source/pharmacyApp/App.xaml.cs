using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.Infrastructure;
using pharmacyApp.Startup;
using pharmacyApp.Modules.Login;

namespace pharmacyApp;

public partial class App : Application
{
    private UserSessionManager? sessionManager;
    private System.Windows.Threading.DispatcherTimer? activityTimer;
    private bool activityRefreshRunning;

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            StartupErrors.Report(e.Exception);
            e.Handled = true;
            Shutdown(1);
        };
        EditionPresentation.Initialize();
        Localization.EnglishUi.Initialize();
        EventManager.RegisterClassHandler(
            typeof(DataGrid),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(OnDataGridPreviewKeyDown));
    }

    public LoginUser? CurrentUser => sessionManager?.CurrentUser;

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            base.OnStartup(e);
            StartupDiagnostics.MigrateConfiguration(AppPaths.RootDirectory);
            StartupErrors.Kind = StartupFailureKind.Project;
            if (!File.Exists(AppPaths.ConfigurationFilePath))
                throw new InvalidOperationException("The configuration file is missing.");
            StartupErrors.Kind = StartupFailureKind.Database;
            _ = ApplicationSettingsStore.Current;
            _ = DatabaseConnectionSettingsStore.Current;
            _ = AppConfiguration.ReadSection<pharmacyApp.AI.Configuration.AiConfiguration>("Ai");
            _ = AppConfiguration.ReadSection<AiSecrets>("Secrets");

            StartupErrors.Kind = StartupFailureKind.Dependencies;
            StartupErrors.Dependency = StartupDiagnostics.BrowserRuntime;
            ValidateBrowserRuntime();

            StartupErrors.Kind = StartupFailureKind.Database;
            StartupErrors.Dependency = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
                await connection.OpenAsync(timeout.Token);
                await using var command = new Npgsql.NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(timeout.Token);
            }

            StartupErrors.DatabaseVerified = true;
            StartupErrors.Kind = StartupFailureKind.Project;
            if (new OpeningWindow().ShowDialog() != true)
            {
                Shutdown(StartupErrors.Failed ? 1 : 0);
                return;
            }

            StartupErrors.Kind = StartupFailureKind.Database;
            var repository = new UserSessionRepository();
            sessionManager = new UserSessionManager(repository);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try { await sessionManager.CleanupStaleSessionsAsync(timeout.Token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The actual session/schema operation below reports connection failures.
                }
                await LocalActivityClock.RefreshAsync(timeout.Token);
                var user = await repository.LoadActiveUserAsync("admin", timeout.Token)
                    ?? throw new InvalidOperationException("The administrator account is unavailable.");
                await sessionManager.BeginAsync(user, timeout.Token);
            }

            StartupErrors.Kind = StartupFailureKind.Project;
            var mainWindow = new MainWindow();
            FrontendTelemetry.Initialize();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            StartupErrors.CompleteStartup();
            activityTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            activityTimer.Tick += async (_, _) =>
            {
                if (activityRefreshRunning) return;
                activityRefreshRunning = true;
                try { await LocalActivityClock.RefreshAsync(); }
                catch { /* Retry on the next tick if the local database is temporarily unavailable. */ }
                finally { activityRefreshRunning = false; }
            };
            activityTimer.Start();
        }
        catch (Exception exception)
        {
            StartupErrors.Report(exception);
            Shutdown(1);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ValidateBrowserRuntime() =>
        _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        EndSessionSafely();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        activityTimer?.Stop();
        EndSessionSafely();
        base.OnExit(e);
    }

    private void EndSessionSafely()
    {
        if (sessionManager?.CurrentUser is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task.Run(() => sessionManager.EndAsync(timeout.Token), timeout.Token)
                .WaitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // A marker remains recoverable on the next launch if shutdown interrupts database access.
        }
    }

    private static void OnDataGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid || e.Key is not (Key.Up or Key.Down) || IsEditingControl(e.OriginalSource))
            return;
        if (grid.Items.Count == 0) return;

        var currentIndex = grid.SelectedIndex;
        var nextIndex = currentIndex < 0
            ? (e.Key == Key.Down ? 0 : grid.Items.Count - 1)
            : Math.Clamp(currentIndex + (e.Key == Key.Down ? 1 : -1), 0, grid.Items.Count - 1);
        var item = grid.Items[nextIndex];
        if (item == CollectionView.NewItemPlaceholder) return;

        grid.SelectedItem = item;
        grid.ScrollIntoView(item);
        var column = grid.Columns.Where(candidate => candidate.Visibility == Visibility.Visible)
            .OrderBy(candidate => candidate.DisplayIndex)
            .FirstOrDefault();
        if (column is not null) grid.CurrentCell = new DataGridCellInfo(item, column);
        e.Handled = true;
    }

    private static bool IsEditingControl(object source) => source is TextBoxBase or ComboBox or PasswordBox or CheckBox;

    private void OnDataGridColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridColumnHeader { Column: not null } header)
        {
            return;
        }

        var grid = FindVisualAncestor<DataGrid>(header);
        if (grid is null)
        {
            return;
        }

        foreach (var column in grid.Columns)
        {
            if (!ReferenceEquals(column, header.Column))
            {
                column.SortDirection = null;
            }
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
