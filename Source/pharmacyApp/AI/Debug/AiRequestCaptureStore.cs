using System.IO;
using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Workflows;

namespace pharmacyApp.AI.Debug;

/// <summary>
/// Writes debug-only, user-inspectable copies of the application-level request payload.
/// Provider authentication headers and API keys are owned by the transport and never enter these files.
/// </summary>
public sealed class AiRequestCaptureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object sync = new();
    private readonly string directoryPath;
    private readonly Dictionary<string, List<AiRequestCaptureFile>> latestBySession = new(StringComparer.Ordinal);

    public AiRequestCaptureStore(string? directoryPath = null) =>
        this.directoryPath = directoryPath ?? Path.Combine(
            pharmacyApp.Infrastructure.AppPaths.DataDirectory, "ai", "request-captures");

    public void BeginTurn(AiRequestContext request, AiConfiguration configuration)
    {
        Clear(request.SessionId);
        if (!configuration.DebugMode) return;
        Write(request.SessionId, "İstek kaydı (yerel)", "turn", new
        {
            sent_to_openai = false,
            note = "This is the local turn envelope. If a model is called, its exact application-level request is saved as a separate file. API keys and HTTP authorization headers are never captured.",
            captured_at_utc = DateTimeOffset.UtcNow,
            user_message = request.UserMessage,
            router_model_override = request.RouterModelOverride,
            application_context = request.ApplicationContext.RelevantValues()
        });
    }

    public void CaptureModelRequest(string sessionId, string stage, AiModelRequest request)
    {
        var label = stage == "advanced-planner"
            ? "Gelişmiş planlayıcı API isteği"
            : "Yönlendirici API isteği";
        Write(sessionId, label, stage, new
        {
            sent_to_openai = true,
            note = "Exact application-level model request. API keys and HTTP authorization headers are intentionally excluded because the transport adds them separately.",
            captured_at_utc = DateTimeOffset.UtcNow,
            stage,
            request
        });
    }

    public IReadOnlyList<AiRequestCaptureFile> GetLatest(string sessionId)
    {
        lock (sync)
            return latestBySession.TryGetValue(sessionId, out var files) ? files.ToArray() : [];
    }

    public void Clear(string sessionId)
    {
        lock (sync) latestBySession.Remove(sessionId);
    }

    private void Write(string sessionId, string label, string stage, object payload)
    {
        try
        {
            lock (sync)
            {
                Directory.CreateDirectory(directoryPath);
                var safeSession = SafeFilePart(sessionId);
                var path = Path.Combine(directoryPath,
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeSession}-{stage}-{Guid.NewGuid():N}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
                if (!latestBySession.TryGetValue(sessionId, out var files))
                    latestBySession[sessionId] = files = [];
                files.Add(new AiRequestCaptureFile(label, path, stage != "turn"));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Where(character => !invalid.Contains(character)).Take(24).ToArray());
        return safe.Length == 0 ? "session" : safe;
    }
}
