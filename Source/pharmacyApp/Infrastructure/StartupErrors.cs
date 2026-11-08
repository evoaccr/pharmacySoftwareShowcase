using pharmacyApp.Startup;

namespace pharmacyApp.Infrastructure;

/// <summary>Three startup failure categories, rendered without WPF or browser dependencies.</summary>
public static class StartupErrors
{
    public static StartupFailureKind Kind { get; set; } = StartupFailureKind.Project;
    public static string? Dependency { get; set; }
    public static bool DatabaseVerified { get; set; }
    public static bool Failed { get; private set; }
    public static bool StartupCompleted { get; private set; }
    public static void CompleteStartup() => StartupCompleted = true;
    private static string? eventName;
    private static int reporting;

    public static void Initialize(string[] args)
    {
        if (args is ["--startup-event", var name] && name.StartsWith("Local\\PharmacyStartup_", StringComparison.Ordinal))
            eventName = name;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception ?? new Exception());
    }

    public static void NotifyVisible()
    {
        if (eventName is null) return;
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Report(Exception exception)
    {
        Failed = true;
        if (Interlocked.Exchange(ref reporting, 1) != 0) return;
        try
        {
            if (StartupCompleted)
            {
                System.Windows.MessageBox.Show(RuntimeErrorMessage(exception), "Pharmacy - Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }
            var kind = Kind;
            var dependency = Dependency;
            for (Exception? error = exception; error is not null; error = error.InnerException)
            {
                if (error is System.IO.FileNotFoundException or DllNotFoundException or BadImageFormatException or System.IO.FileLoadException)
                {
                    kind = StartupFailureKind.Dependencies;
                    var file = error is System.IO.FileNotFoundException missing ? missing.FileName : error.Message;
                    dependency = file?.Contains("WebView2", StringComparison.OrdinalIgnoreCase) == true ? StartupDiagnostics.BrowserLibraries :
                        file?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true ? StartupDiagnostics.DatabaseDriver :
                        file?.Contains("Markdig", StringComparison.OrdinalIgnoreCase) == true ? StartupDiagnostics.MarkdownLibrary :
                        StartupDiagnostics.DesktopRuntime;
                }
            }
            var rows = kind == StartupFailureKind.Database ? StartupDiagnostics.DatabaseChecks(DatabaseVerified) :
                kind == StartupFailureKind.Dependencies ? StartupDiagnostics.Dependencies(AppContext.BaseDirectory, dependency!) : [];
            StartupFailureDialog.Show(kind, rows, NotifyVisible);
        }
        finally { Interlocked.Exchange(ref reporting, 0); }
    }

    public static string RuntimeErrorMessage(Exception exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
        {
            if (error is Npgsql.PostgresException { SqlState: "XX001" or "XX002" })
                return "The medication database contains damaged data. Contact the server administrator.";
            if (error is Npgsql.NpgsqlException)
                return "The database operation failed. Check the connection or contact the server administrator.";
        }
        return "Could not complete the operation. Close and reopen the app.";
    }
}
