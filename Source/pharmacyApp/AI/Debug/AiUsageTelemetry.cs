using System.IO;
using System.Text.Json;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Debug;

public sealed record AiUsageAggregate(int Requests, int InputTokens, int CachedInputTokens, int CacheWriteTokens, int OutputTokens, int ReasoningTokens, int ToolCalls);

public sealed class AiUsageTelemetry
{
    private static readonly object Sync = new();
    private static readonly string DefaultFilePath = Path.Combine(
        pharmacyApp.Infrastructure.AppPaths.DataDirectory, "ai", "usage.json");
    private readonly string filePath;

    public AiUsageTelemetry(string? filePath = null) => this.filePath = filePath ?? DefaultFilePath;

    public void Record(string model, AiUsage usage, int toolCalls)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                Dictionary<string, AiUsageAggregate> aggregates = [];
                if (File.Exists(filePath)) aggregates = JsonSerializer.Deserialize<Dictionary<string, AiUsageAggregate>>(File.ReadAllText(filePath)) ?? [];
                var key = $"{DateTimeOffset.Now:yyyy-MM}|{model}";
                var current = aggregates.GetValueOrDefault(key) ?? new AiUsageAggregate(0, 0, 0, 0, 0, 0, 0);
                aggregates[key] = current with
                {
                    Requests = current.Requests + 1,
                    InputTokens = current.InputTokens + usage.InputTokens,
                    CachedInputTokens = current.CachedInputTokens + usage.CachedInputTokens,
                    CacheWriteTokens = current.CacheWriteTokens + usage.CacheWriteTokens,
                    OutputTokens = current.OutputTokens + usage.OutputTokens,
                    ReasoningTokens = current.ReasoningTokens + usage.ReasoningTokens,
                    ToolCalls = current.ToolCalls + toolCalls
                };
                var temporary = filePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(aggregates, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, filePath, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }
}
