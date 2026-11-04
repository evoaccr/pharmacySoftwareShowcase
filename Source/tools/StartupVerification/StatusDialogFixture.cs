using System;
using System.Collections.Generic;
using pharmacyApp.Startup;

// Isolated native renderer verification; never starts the app or changes settings.
internal static class StatusDialogFixture
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool reverse = args.Length > 0;
        StartupFailureDialog.Show(StartupFailureKind.Database, new List<StartupCheck> {
            new StartupCheck("Check internet", !reverse),
            new StartupCheck("Check connection settings at config.json", reverse)
        }, null);
    }
}
