using pharmacyApp.Startup;

namespace pharmacyApp.Infrastructure;

/// <summary>Three startup failure categories, rendered without WPF or browser dependencies.</summary>
public static class StartupErrors
{
    public static StartupFailureKind Kind { get; set; } = StartupFailureKind.Project;
    public static string? Dependency { get; set; }
    public static bool DatabaseVerified { get; set; }
    public static bool Failed { get; private set; }
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
}
