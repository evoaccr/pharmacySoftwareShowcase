using System.IO;
using System.Text.Json;

namespace pharmacyApp.AI.Execution;

public sealed class AiOperationalLog
{
    private static readonly object Sync = new();
    private static readonly string DefaultDirectoryPath = Path.Combine(
        pharmacyApp.Infrastructure.AppPaths.DataDirectory, "ai");
    private readonly string directoryPath;

    public AiOperationalLog(string? directoryPath = null) => this.directoryPath = directoryPath ?? DefaultDirectoryPath;

    public void WriteTool(string sessionId, string toolName, string status, long durationMilliseconds) =>
        Write(new { timestamp = DateTimeOffset.UtcNow, request_id = SafeId(sessionId), tool_name = toolName, status, duration_ms = durationMilliseconds });

    public void WriteRequest(string requestId, string model, string status, long durationMilliseconds) =>
        Write(new { timestamp = DateTimeOffset.UtcNow, request_id = SafeId(requestId), model, status, duration_ms = durationMilliseconds });

    private void Write(object value)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(directoryPath);
                File.AppendAllText(Path.Combine(directoryPath, "operations.jsonl"), JsonSerializer.Serialize(value) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string SafeId(string value) => value.Length <= 64 ? value : value[..64];
}
