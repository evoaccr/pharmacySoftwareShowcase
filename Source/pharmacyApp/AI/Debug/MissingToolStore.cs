using System.IO;
using System.Text.Json;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Debug;

public sealed record MissingToolRecord(
    string Key,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string OriginalRequest,
    string MissingCapability,
    string SuggestedNamespace,
    string SuggestedToolName,
    IReadOnlyList<string> SuggestedParameters,
    string SuggestedResult,
    string Reason,
    string EstimatedRisk,
    IReadOnlyList<string> ClosestExistingTools,
    int Occurrences,
    string Status);

public sealed class MissingToolStore
{
    private static readonly object Sync = new();
    public static string DefaultFilePath { get; } = Path.Combine(
        pharmacyApp.Infrastructure.AppPaths.DataDirectory, "ai", "missing-tools.json");
    private readonly string filePath;

    public MissingToolStore(string? filePath = null) => this.filePath = filePath ?? DefaultFilePath;

    public MissingToolRecord Record(MissingToolSuggestion suggestion)
    {
        lock (Sync)
        {
            var records = LoadInternal().ToList();
            var key = Normalize(suggestion.SuggestedNamespace + ":" + suggestion.MissingCapability);
            var index = records.FindIndex(record => record.Key == key);
            var now = DateTimeOffset.UtcNow;
            MissingToolRecord record;
            if (index >= 0)
            {
                record = records[index] with { LastSeen = now, Occurrences = records[index].Occurrences + 1 };
                records[index] = record;
            }
            else
            {
                record = new MissingToolRecord(
                    key, now, now, suggestion.OriginalRequest, suggestion.MissingCapability,
                    suggestion.SuggestedNamespace, suggestion.SuggestedToolName,
                    suggestion.SuggestedParameters, suggestion.SuggestedResult, suggestion.Reason,
                    suggestion.EstimatedRisk, suggestion.ClosestExistingTools, 1, "open");
                records.Add(record);
            }
            Save(records);
            return record;
        }
    }

    public IReadOnlyList<MissingToolRecord> Load()
    {
        lock (Sync) return LoadInternal();
    }

    public string CreateCodexPrompt(MissingToolRecord record) =>
        $"Implement the missing pharmacyApp AI tool '{record.SuggestedToolName}' in namespace '{record.SuggestedNamespace}' according to obsidian/ai/aiToolCreationStandard.md. Capability: {record.MissingCapability}. Parameters: {string.Join(", ", record.SuggestedParameters)}. Expected result: {record.SuggestedResult}. Risk: {record.EstimatedRisk}. Reuse existing application services and do not duplicate business logic.";

    private IReadOnlyList<MissingToolRecord> LoadInternal()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<List<MissingToolRecord>>(File.ReadAllText(filePath)) ?? []
                : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (JsonException) { return []; }
    }

    private void Save(IReadOnlyList<MissingToolRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, filePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Normalize(string value) => string.Join(' ', value.Trim().ToLowerInvariant().Split(
        [' ', '\t', '\r', '\n', '.', ',', ';', ':'], StringSplitOptions.RemoveEmptyEntries));
}
