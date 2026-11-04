using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Tools;

namespace pharmacyApp.AI.Providers;

/// <summary>
/// Isolated Responses API adapter. An application gateway is preferred in production; direct
/// The local reads credentials exclusively from the local JSONC configuration.
/// </summary>
public sealed class OpenAiResponsesTransport : IAiModelTransport
{
    private static readonly Uri DirectEndpoint = new("https://api.openai.com/v1/responses");
    private readonly HttpClient httpClient;
    private readonly IAiConfigurationProvider configurationProvider;
    private readonly Func<pharmacyApp.Infrastructure.AiSecrets> getSecrets;

    public OpenAiResponsesTransport(IAiConfigurationProvider configurationProvider, HttpClient? httpClient = null, Func<pharmacyApp.Infrastructure.AiSecrets>? getSecrets = null)
    {
        this.configurationProvider = configurationProvider;
        this.getSecrets = getSecrets ?? (() => pharmacyApp.Infrastructure.AppConfiguration.ReadSection<pharmacyApp.Infrastructure.AiSecrets>("Secrets"));
        this.httpClient = httpClient ?? new HttpClient();
    }

    public bool IsConfigured
    {
        get
        {
            var configuration = configurationProvider.GetCurrent();
            return !string.IsNullOrWhiteSpace(configuration.GatewayEndpoint) ||
                   !string.IsNullOrWhiteSpace(getSecrets().OpenAiApiKey);
        }
    }

    public string ConfigurationDescription
    {
        get
        {
            var configuration = configurationProvider.GetCurrent();
            if (!string.IsNullOrWhiteSpace(configuration.GatewayEndpoint)) return "Application AI gateway";
            return IsConfigured ? "Direct OpenAI development mode" : "Not configured";
        }
    }

    public async Task<AiModelResponse> SendAsync(AiModelRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new AiTransportException("NOT_CONFIGURED",
                "AI connection is not configured. Set Ai.GatewayEndpoint or Secrets.OpenAiApiKey in config.json.");

        var configuration = configurationProvider.GetCurrent();
        var endpoint = string.IsNullOrWhiteSpace(configuration.GatewayEndpoint)
            ? DirectEndpoint
            : new Uri(configuration.GatewayEndpoint, UriKind.Absolute);
        var toolNames = AiToolNameCodec.Create(request.Tools);
        var payload = BuildPayload(request, toolNames.CanonicalToProvider);
        var transientDelay = TimeSpan.FromMilliseconds(350);
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            ApplyAuthentication(message, configuration);
            try
            {
                using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return Parse(body, toolNames.ProviderToCanonical);
                var errorCode = MapStatus(response.StatusCode, body);
                if (attempt < 2 && IsTransient(response.StatusCode) && errorCode != "INSUFFICIENT_QUOTA")
                {
                    await Task.Delay(transientDelay, cancellationToken);
                    transientDelay *= 2;
                    continue;
                }
                var requestId = response.Headers.TryGetValues("x-request-id", out var requestIds)
                    ? requestIds.FirstOrDefault()
                    : null;
                throw new AiTransportException(
                    errorCode,
                    SafeApiMessage(body, response.StatusCode),
                    httpStatus: (int)response.StatusCode,
                    requestId: requestId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2)
            {
                await Task.Delay(transientDelay, cancellationToken);
                transientDelay *= 2;
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(transientDelay, cancellationToken);
                transientDelay *= 2;
            }
            catch (HttpRequestException exception)
            {
                throw new AiTransportException(
                    "NETWORK_FAILURE",
                    $"The AI service could not be reached ({exception.GetType().Name}).",
                    exception);
            }
        }
    }

    private static string BuildPayload(
        AiModelRequest request,
        IReadOnlyDictionary<string, string> canonicalToProvider)
    {
        var input = new JsonArray();
        foreach (var item in request.InputItems) input.Add(JsonNode.Parse(item.GetRawText()));
        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = request.Instructions,
            ["input"] = input,
            ["store"] = request.Store,
            ["parallel_tool_calls"] = request.AllowParallelToolCalls,
            ["tools"] = new JsonArray(request.Tools.Select(tool =>
                AiToolSchemaConverter.Convert(tool, canonicalToProvider[tool.Name])).ToArray())
        };
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            root["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort };
        if (!string.IsNullOrWhiteSpace(request.PreviousResponseId)) root["previous_response_id"] = request.PreviousResponseId;
        if (!string.IsNullOrWhiteSpace(request.PromptCacheKey)) root["prompt_cache_key"] = request.PromptCacheKey;
        if (request.MaxOutputTokens is > 0) root["max_output_tokens"] = request.MaxOutputTokens.Value;
        if (request.ResponseJsonSchema is { } schema)
        {
            root["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "structured_response",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(schema.GetRawText())
                }
            };
        }
        return root.ToJsonString();
    }

    private void ApplyAuthentication(HttpRequestMessage message, AiConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.GatewayEndpoint))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", getSecrets().OpenAiApiKey);
            return;
        }
        var token = getSecrets().GatewayToken;
        if (!string.IsNullOrWhiteSpace(token)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static AiModelResponse Parse(
        string json,
        IReadOnlyDictionary<string, string> providerToCanonical)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var responseId = root.GetProperty("id").GetString() ?? string.Empty;
            var outputItems = root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array
                ? output.EnumerateArray().Select(item => item.Clone()).ToArray()
                : [];
            var calls = new List<AiToolCall>();
            var text = new StringBuilder();
            // Commentary is not a final structured plan. Preserve the original output
            // items, including phase, so tool continuations can replay them unchanged.
            var hasFinalAnswer = outputItems.Any(item =>
                item.TryGetProperty("type", out var itemType) && itemType.GetString() == "message" &&
                item.TryGetProperty("phase", out var phase) && phase.GetString() == "final_answer");
            foreach (var item in outputItems)
            {
                var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                if (type == "function_call")
                {
                    var providerName = item.GetProperty("name").GetString() ?? string.Empty;
                    calls.Add(new AiToolCall(
                        item.GetProperty("call_id").GetString() ?? string.Empty,
                        providerToCanonical.GetValueOrDefault(providerName, providerName),
                        item.GetProperty("arguments").GetString() ?? "{}"));
                }
                else if (type == "message" && item.TryGetProperty("content", out var content))
                {
                    var phase = item.TryGetProperty("phase", out var phaseValue) ? phaseValue.GetString() : null;
                    if (phase == "commentary" || hasFinalAnswer && phase != "final_answer") continue;
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var partType) && partType.GetString() == "output_text" &&
                            part.TryGetProperty("text", out var partText)) text.Append(partText.GetString());
                }
            }
            if (text.Length == 0 && outputItems.Length == 0 && root.TryGetProperty("output_text", out var directText))
                text.Append(directText.GetString());
            return new AiModelResponse(responseId, text.ToString(), calls, outputItems, ParseUsage(root));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiTransportException("INVALID_RESPONSE", "The AI service returned an invalid response.", exception);
        }
    }

    private static AiUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return new AiUsage();
        int Value(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
        var cached = usage.TryGetProperty("input_tokens_details", out var inputDetails) ? Value(inputDetails, "cached_tokens") : 0;
        var cacheWrite = usage.TryGetProperty("input_tokens_details", out inputDetails) ? Value(inputDetails, "cache_write_tokens") : 0;
        var reasoning = usage.TryGetProperty("output_tokens_details", out var outputDetails) ? Value(outputDetails, "reasoning_tokens") : 0;
        return new AiUsage(Value(usage, "input_tokens"), cached, cacheWrite, Value(usage, "output_tokens"), reasoning);
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static string MapStatus(HttpStatusCode status, string body)
    {
        var providerCode = ApiErrorValue(body, "code");
        var providerType = ApiErrorValue(body, "type");
        var providerMessage = ApiErrorValue(body, "message");
        if (MatchesQuota(providerCode) || MatchesQuota(providerType) ||
            providerMessage.Contains("no credits", StringComparison.OrdinalIgnoreCase) ||
            providerMessage.Contains("credit balance", StringComparison.OrdinalIgnoreCase) ||
            providerMessage.Contains("billing quota", StringComparison.OrdinalIgnoreCase))
            return "INSUFFICIENT_QUOTA";
        return status switch
        {
            HttpStatusCode.TooManyRequests => "RATE_LIMIT",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "TIMEOUT",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "AUTHENTICATION_FAILED",
            _ when (int)status >= 500 => "SERVICE_UNAVAILABLE",
            _ => "INVALID_REQUEST"
        };
    }

    private static bool MatchesQuota(string value) =>
        value.Equals("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("billing_hard_limit_reached", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("credit_balance_exhausted", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("no_credits", StringComparison.OrdinalIgnoreCase);

    private static string ApiErrorValue(string body, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.TryGetProperty(propertyName, out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static string SafeApiMessage(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                return $"AI service error ({(int)status}): {message.GetString()}";
        }
        catch (JsonException) { }
        return $"AI service error ({(int)status}).";
    }
}

public static class AiToolSchemaConverter
{
    public static JsonObject Convert(AiToolDefinition definition) =>
        Convert(definition, AiToolNameCodec.ToProviderName(definition.Name));

    internal static JsonObject Convert(AiToolDefinition definition, string providerName)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, parameter) in definition.Parameters)
        {
            var schema = new JsonObject { ["description"] = parameter.Description };
            schema["type"] = parameter.Required
                ? JsonValue.Create(parameter.Type)
                : new JsonArray(parameter.Type, "null");
            if (parameter.AllowedValues is { Count: > 0 })
            {
                var allowed = new JsonArray(parameter.AllowedValues
                    .Select(value => JsonValue.Create(value)).ToArray());
                if (!parameter.Required) allowed.Add(null);
                schema["enum"] = allowed;
            }
            if (parameter.Type == "array") schema["items"] = new JsonObject { ["type"] = parameter.ItemsType };
            properties[name] = schema;
            required.Add(name);
        }
        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = providerName,
            ["description"] = definition.Description,
            ["strict"] = true,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            }
        };
    }
}

public static class AiToolNameCodec
{
    public static string ToProviderName(string canonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        var cleaned = new string(canonicalName.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        if (cleaned.Length == 0) cleaned = "tool";
        return cleaned.Length <= 64 ? cleaned : cleaned[..64];
    }

    internal static AiToolNameMap Create(IReadOnlyList<AiToolDefinition> tools)
    {
        var canonicalToProvider = new Dictionary<string, string>(StringComparer.Ordinal);
        var providerToCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var providerName = ToProviderName(tool.Name);
            if (providerToCanonical.TryGetValue(providerName, out var existing) && existing != tool.Name)
            {
                var suffix = "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool.Name)))[..8];
                providerName = providerName[..Math.Min(providerName.Length, 64 - suffix.Length)] + suffix;
            }
            canonicalToProvider[tool.Name] = providerName;
            providerToCanonical[providerName] = tool.Name;
        }
        return new AiToolNameMap(canonicalToProvider, providerToCanonical);
    }
}

internal sealed record AiToolNameMap(
    IReadOnlyDictionary<string, string> CanonicalToProvider,
    IReadOnlyDictionary<string, string> ProviderToCanonical);
