namespace pharmacyApp;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Infrastructure.StartupErrors.Initialize(args);
        var exitCode = 1;
        try { exitCode = RunApplication(); }
        catch (Exception exception) { Infrastructure.StartupErrors.Report(exception); }
        Environment.Exit(Infrastructure.StartupErrors.Failed ? 1 : exitCode);
    }

    // Keep WPF assembly/resource loading inside the protected call.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
