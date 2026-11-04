using System.Text.Json;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Tools;

namespace pharmacyApp.AI.Providers;

public sealed record AiModelRequest(
    string Model,
    string ReasoningEffort,
    string Instructions,
    IReadOnlyList<JsonElement> InputItems,
    IReadOnlyList<AiToolDefinition> Tools,
    bool Store,
    bool AllowParallelToolCalls,
    string? PreviousResponseId = null,
    string? PromptCacheKey = null,
    JsonElement? ResponseJsonSchema = null,
    int? MaxOutputTokens = null);

public sealed record AiModelResponse(
    string ResponseId,
    string Text,
    IReadOnlyList<AiToolCall> ToolCalls,
    IReadOnlyList<JsonElement> OutputItems,
    AiUsage Usage);

public interface IAiModelTransport
{
    bool IsConfigured { get; }
    string ConfigurationDescription { get; }
    Task<AiModelResponse> SendAsync(AiModelRequest request, CancellationToken cancellationToken = default);
}

public sealed class AiTransportException(
    string code,
    string message,
    Exception? inner = null,
    int? httpStatus = null,
    string? requestId = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
    public string? RequestId { get; } = requestId;
}
