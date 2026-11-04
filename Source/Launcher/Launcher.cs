using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using pharmacyApp.Startup;

// Uses Windows' built-in .NET Framework, independently of the bundled .NET/WPF runtime.
internal static class Launcher
{
    private static int Failure(StartupFailureKind kind, string root, string failedDependency)
    {
        StartupFailureDialog.Show(kind, kind == StartupFailureKind.Dependencies ?
            StartupDiagnostics.Dependencies(root, failedDependency) : new List<StartupCheck>(), null);
        return 1;
    }

    [STAThread]
    private static int Main()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            StartupDiagnostics.MigrateConfiguration(root);
            if (!StartupDiagnostics.ProjectComplete(root)) return Failure(StartupFailureKind.Project, root, null);
            var dependencies = StartupDiagnostics.Dependencies(root, null);
            if (dependencies.Exists(row => !row.Passed))
            {
                StartupFailureDialog.Show(StartupFailureKind.Dependencies, dependencies, null);
                return 1;
            }
            string data = Path.Combine(root, "Data");
            Directory.CreateDirectory(data);
            using (new FileStream(Path.Combine(data, ".startup-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }

            string eventName = "Local\\PharmacyStartup_" + Guid.NewGuid().ToString("N");
            using (var visible = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
            {
                var info = new ProcessStartInfo(Path.Combine(root, "pharmacyApp.Core.exe"), "--startup-event " + eventName);
                info.WorkingDirectory = root;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.EnvironmentVariables["DOTNET_DISABLE_GUI_ERRORS"] = "1";
                using (var process = Process.Start(info))
                {
                    if (process == null) return Failure(StartupFailureKind.Project, root, null);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                    while (!visible.WaitOne(100))
                    {
                        if (process.HasExited)
                        {
                            if (visible.WaitOne(0)) return 0;
                            // .NET host HRESULT failures identify a runtime that cannot load.
                            uint code = unchecked((uint)process.ExitCode);
                            return Failure((code & 0xFFFF0000u) == 0x80000000u ?
                                StartupFailureKind.Dependencies : StartupFailureKind.Project, root,
                                StartupDiagnostics.DesktopRuntime);
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            try { process.Kill(); process.WaitForExit(3000); }
                            catch (InvalidOperationException) { }
                            return Failure(StartupFailureKind.Project, root, null);
                        }
                    }
                }
            }
            return 0;
        }
        catch { return Failure(StartupFailureKind.Project, root, null); }
    }
}
