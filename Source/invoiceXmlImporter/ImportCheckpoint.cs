using System.IO;
using System.Text.Json;

namespace invoiceXmlImporter;

public sealed class ImportCheckpoint
{
    public string RunId { get; set; } = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string Phase { get; set; } = "Downloading";
    public string ResumePhase { get; set; } = "Downloading";
    public double AccumulatedSeconds { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTimeOffset RunCutoff { get; set; } = DateTimeOffset.Now;
    public bool DebugMode { get; set; }
    public string SelectedTypeKey { get; set; } = string.Empty;
    public int? MaximumInvoices { get; set; }
    public string DownloadOrder { get; set; } = InvoiceDownloadOrder.NewestFirst;
    public bool WithoutFarmassistRecords { get; set; }
    public Dictionary<string, int> ExpectedTotals { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TargetTotals { get; set; } = new(StringComparer.Ordinal);
    public List<string> CompletedDownloadTypeKeys { get; set; } = [];
    public List<string> CompletedPackageTypeKeys { get; set; } = [];
    public List<PageCheckpoint> Pages { get; set; } = [];
    public List<PackageCheckpoint> Packages { get; set; } = [];
}

public static class InvoiceDownloadOrder
{
    public const string NewestFirst = "NewestFirst";
    public const string OldestFirst = "OldestFirst";
    public const string Random = "Random";

    public static bool IsValid(string value) =>
        value is NewestFirst or OldestFirst or Random;
}

public sealed class PackageCheckpoint
{
    public string TypeKey { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public int PackageIndex { get; set; }
    public int TotalPackages { get; set; }
    public string ZipPath { get; set; } = string.Empty;
    public int XmlCount { get; set; }
    public int ImportedEntries { get; set; }
    public bool ImportComplete { get; set; }
    public int ImportedQrCodes { get; set; }
    public int IncompleteQrLines { get; set; }
    public int IndividualXmlQrRetries { get; set; }
}

public sealed class PageCheckpoint
{
    public string TypeKey { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public DateOnly Month { get; set; }
    public int PageIndex { get; set; }
    public int TotalDocuments { get; set; }
    public int TotalPages { get; set; }
    public string RecordsHash { get; set; } = string.Empty;
    public List<string> RecordIds { get; set; } = [];
    public List<string> ChunkFiles { get; set; } = [];
    public string ZipPath { get; set; } = string.Empty;
    public bool DownloadComplete { get; set; }
    public int ImportedEntries { get; set; }
    public int XmlCount { get; set; }
    public bool ImportComplete { get; set; }
}

public sealed class CheckpointStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public CheckpointStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pharmacyApp", "InvoiceXmlImporter");
    }

    public string RootDirectory { get; }
    public string CheckpointPath => Path.Combine(RootDirectory, "checkpoint.json");

    public ImportCheckpoint? Load()
    {
        try
        {
            return File.Exists(CheckpointPath)
                ? JsonSerializer.Deserialize<ImportCheckpoint>(File.ReadAllText(CheckpointPath), Options)
                : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void Save(ImportCheckpoint checkpoint)
    {
        Directory.CreateDirectory(RootDirectory);
        checkpoint.UpdatedAt = DateTimeOffset.Now;
        var temporary = CheckpointPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoint, Options));
        File.Move(temporary, CheckpointPath, true);
    }

    public string RunDirectory(ImportCheckpoint checkpoint)
    {
        var path = Path.Combine(RootDirectory, "downloads", checkpoint.RunId);
        Directory.CreateDirectory(path);
        return path;
    }
}
