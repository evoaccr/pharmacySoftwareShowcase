#pragma warning disable
// Shared by the Windows Framework launcher and the .NET desktop app; keep C# 5 compatibility.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace pharmacyApp.Startup
{
    public enum StartupFailureKind { Database, Project, Dependencies }

    public sealed class StartupCheck
    {
        public readonly string Name;
        public bool Passed;
        public StartupCheck(string name, bool passed) { Name = name; Passed = passed; }
    }

    public static class StartupDiagnostics
    {
        public const string ConfigurationFileName = "config.json";
        public const string DatabaseMessage = "Could not connect to database:";
        public const string ProjectMessage = "Project folder may be incomplete, re-pull the repository";
        public const string DependenciesMessage = "Missing Dependincies";
        public const string DesktopRuntime = ".NET 10 Desktop Runtime (included)";
        public const string BrowserRuntime = "Microsoft Edge WebView2 Runtime";
        public const string BrowserLibraries = "WebView2 libraries (included)";
        public const string DatabaseDriver = "Npgsql (included)";
        public const string MarkdownLibrary = "Markdig (included)";

        public static string Heading(StartupFailureKind kind)
        {
            return kind == StartupFailureKind.Database ? DatabaseMessage :
                kind == StartupFailureKind.Dependencies ? DependenciesMessage : ProjectMessage;
        }

        public static void MigrateConfiguration(string root)
        {
            string current = Path.Combine(root, ConfigurationFileName);
            string legacy = Path.Combine(root, "config.jsonc");
            if (!File.Exists(current) && File.Exists(legacy)) File.Move(legacy, current);
        }

        public static bool ProjectComplete(string root)
        {
            foreach (string name in new[] { "pharmacyApp.Core.exe", "pharmacyApp.dll", "pharmacyApp.deps.json", "pharmacyApp.runtimeconfig.json", ConfigurationFileName })
                if (!File.Exists(Path.Combine(root, name))) return false;
            return IsExecutable(Path.Combine(root, "pharmacyApp.Core.exe")) && IsExecutable(Path.Combine(root, "pharmacyApp.dll"));
        }

        public static List<StartupCheck> Dependencies(string root, string failedDependency)
        {
            var rows = new List<StartupCheck>();
            rows.Add(new StartupCheck(".NET Framework 4.x (Windows component)", IsExecutable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET/Framework64/v4.0.30319/clr.dll"))));
            rows.Add(new StartupCheck(DesktopRuntime, FilesOkay(root, new[] {
                "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll", "clrgc.dll", "vcruntime140_cor3.dll",
                "System.Private.CoreLib.dll", "WindowsBase.dll", "PresentationCore.dll", "PresentationFramework.dll", "System.Xaml.dll", "System.IO.Packaging.dll", "wpfgfx_cor3.dll"
            })));
            rows.Add(new StartupCheck(BrowserRuntime, BrowserInstalled()));
            rows.Add(new StartupCheck(BrowserLibraries, FilesOkay(root, new[] { "WebView2Loader.dll", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll" })));
            rows.Add(new StartupCheck(DatabaseDriver, FilesOkay(root, new[] { "Npgsql.dll" })));
            rows.Add(new StartupCheck(MarkdownLibrary, FilesOkay(root, new[] { "Markdig.dll" })));
            foreach (var row in rows) if (row.Name == failedDependency) row.Passed = false;
            return rows;
        }

        public static List<StartupCheck> DatabaseChecks(bool settingsOkay)
        {
            return new List<StartupCheck> {
                new StartupCheck("Check internet", InternetConnected()),
                new StartupCheck("Check connection settings at config.json", settingsOkay)
            };
        }

        public static bool InternetConnected()
        {
            object manager = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                manager = Activator.CreateInstance(type);
                return (bool)type.InvokeMember("IsConnectedToInternet", BindingFlags.GetProperty, null, manager, null);
            }
            catch { return false; }
            finally { if (manager != null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager); }
        }

        private static bool FilesOkay(string root, string[] names)
        {
            foreach (string name in names) if (!IsExecutable(Path.Combine(root, name))) return false;
            return true;
        }

        private static bool IsExecutable(string path)
        {
            try
            {
                using (var file = File.OpenRead(path))
                using (var reader = new BinaryReader(file))
                {
                    if (file.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
                    file.Position = 60;
                    int offset = reader.ReadInt32();
                    if (offset < 0 || offset > file.Length - 4) return false;
                    file.Position = offset;
                    return reader.ReadUInt32() == 0x00004550;
                }
            }
            catch { return false; }
        }

        private static bool BrowserInstalled()
        {
            IntPtr version = IntPtr.Zero;
            try
            {
                string folder = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
                return GetAvailableCoreWebView2BrowserVersionString(folder, out version) >= 0 && version != IntPtr.Zero &&
                    !string.IsNullOrWhiteSpace(Marshal.PtrToStringUni(version));
            }
            catch { return false; }
            finally { if (version != IntPtr.Zero) Marshal.FreeCoTaskMem(version); }
        }
        [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
        private static extern int GetAvailableCoreWebView2BrowserVersionString(string folder, out IntPtr version);
    }

    /// <summary>Native coloured status rows; no WPF, WebView2 or bundled runtime UI dependency.</summary>
    public static class StartupFailureDialog
    {
        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr first, IntPtr second);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size, Style;
            public IntPtr Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string Menu, Name;
            public IntPtr SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr Window;
            public uint Id;
            public IntPtr First, Second;
            public uint Time;
            public int X, Y;
            public uint Private;
        }

        public static void Show(StartupFailureKind kind, List<StartupCheck> rows, Action visible)
        {
            const string title = "Pharmacy - Startup error";
            string className = "PharmacyStartup_" + Guid.NewGuid().ToString("N");
            IntPtr instance = GetModuleHandle(null), window = IntPtr.Zero;
            IntPtr font = IntPtr.Zero, bold = IntPtr.Zero;
            bool closed = false;
            var colours = new Dictionary<IntPtr, bool>();
            WindowProcedure procedure = delegate(IntPtr handle, uint message, IntPtr first, IntPtr second)
            {
                if (message == 0x0138) // WM_CTLCOLORSTATIC
                {
                    bool okay;
                    SetTextColor(first, colours.TryGetValue(second, out okay) ? (okay ? 0x00008000u : 0x000000FFu) : 0x00222222u);
                    SetBkMode(first, 1);
                    return GetStockObject(0);
                }
                if (message == 0x0010 || (message == 0x0111 && (first.ToInt64() & 0xFFFF) == 1))
                { DestroyWindow(handle); return IntPtr.Zero; }
                if (message == 0x0002) { closed = true; return IntPtr.Zero; }
                return DefWindowProc(handle, message, first, second);
            };
            try
            {
                var descriptor = new WindowClass {
                    Size = (uint)Marshal.SizeOf(typeof(WindowClass)), Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                    Instance = instance, Name = className, Background = GetStockObject(0), Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512))
                };
                if (RegisterClassEx(ref descriptor) == 0) throw new InvalidOperationException();
                int width = Math.Min(620, GetSystemMetrics(0) - 40), height = 165 + rows.Count * 30;
                window = CreateWindowEx(0x00040009, className, title, 0x00C80000,
                    Math.Max(0, (GetSystemMetrics(0) - width) / 2), Math.Max(0, (GetSystemMetrics(1) - height) / 2),
                    width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (window == IntPtr.Zero) throw new InvalidOperationException();
                font = CreateFont(-18, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                bold = CreateFont(-18, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                var heading = CreateWindowEx(0, "STATIC", StartupDiagnostics.Heading(kind), 0x50000080,
                    20, 18, width - 60, 52, window, new IntPtr(100), instance, IntPtr.Zero);
                SendMessage(heading, 0x0030, bold, new IntPtr(1));
                for (int i = 0; i < rows.Count; i++)
                {
                    var marker = CreateWindowEx(0, "STATIC", rows[i].Passed ? "\u2713" : "\u2717", 0x50000080,
                        22, 72 + i * 30, 25, 26, window, new IntPtr(200 + i), instance, IntPtr.Zero);
                    colours[marker] = rows[i].Passed;
                    SendMessage(marker, 0x0030, bold, new IntPtr(1));
                    var label = CreateWindowEx(0, "STATIC", rows[i].Name, 0x50000080,
                        52, 72 + i * 30, width - 90, 26, window, new IntPtr(300 + i), instance, IntPtr.Zero);
                    SendMessage(label, 0x0030, font, new IntPtr(1));
                }
                var okayButton = CreateWindowEx(0, "BUTTON", "OK", 0x50010001,
                    width - 120, height - 78, 85, 30, window, new IntPtr(1), instance, IntPtr.Zero);
                SendMessage(okayButton, 0x0030, font, new IntPtr(1));
                ShowWindow(window, 1); UpdateWindow(window); SetForegroundWindow(window);
                if (visible != null) visible();
                Message next;
                while (!closed && GetMessage(out next, IntPtr.Zero, 0, 0) > 0)
                {
                    if (!IsDialogMessage(window, ref next)) { TranslateMessage(ref next); DispatchMessage(ref next); }
                }
            }
            catch
            {
                if (window != IntPtr.Zero && !closed) DestroyWindow(window);
                if (visible != null) visible();
                MessageBox(IntPtr.Zero, StartupDiagnostics.Heading(kind), title, 0x00050010);
            }
            finally
            {
                if (font != IntPtr.Zero) DeleteObject(font);
                if (bold != IntPtr.Zero) DeleteObject(bold);
                UnregisterClass(className, instance);
                GC.KeepAlive(procedure);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass descriptor);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extended, string type, string text, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr first, IntPtr second);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, IntPtr window, uint first, uint last);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool IsDialogMessage(IntPtr window, ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string title, uint flags);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr context, uint colour);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr context, int mode);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    }
}
