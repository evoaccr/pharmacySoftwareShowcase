using System.IO;

namespace pharmacyApp.Infrastructure;

/// <summary>All application-owned local files stay beside the local configuration.</summary>
public static class AppPaths
{
    public const string ConfigurationFileName = "config.json";
    public static string RootDirectory { get; } = FindRoot();
    public static string ConfigurationFilePath => Path.Combine(RootDirectory, ConfigurationFileName);
    public static string DataDirectory => Path.Combine(RootDirectory, "Data");
    public static string DownloadsDirectory => Path.Combine(DataDirectory, "Downloads");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ConfigurationFileName)) ||
                File.Exists(Path.Combine(directory.FullName, "config.jsonc"))) return directory.FullName;
            if (File.Exists(Path.Combine(directory.FullName, "Source", "pharmacyApp", "pharmacyApp.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
