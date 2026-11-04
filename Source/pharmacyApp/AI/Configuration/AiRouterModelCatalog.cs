using System.Globalization;

namespace pharmacyApp.AI.Configuration;

public sealed record AiRouterModelOption(
    string Id,
    string DisplayName,
    decimal? InputPriceUsdPerMillion,
    decimal? OutputPriceUsdPerMillion,
    string DocumentationUrl,
    string RouterReasoningEffort)
{
    public string SelectorLabel => InputPriceUsdPerMillion is { } input && OutputPriceUsdPerMillion is { } output
        ? $"{DisplayName}  ·  ${input.ToString("0.##", CultureInfo.InvariantCulture)} / ${output.ToString("0.##", CultureInfo.InvariantCulture)}"
        : $"{DisplayName} · Custom setting";

    public string PriceDescription => InputPriceUsdPerMillion is { } input && OutputPriceUsdPerMillion is { } output
        ? $"Input ${input.ToString("0.##", CultureInfo.InvariantCulture)} · Output ${output.ToString("0.##", CultureInfo.InvariantCulture)} / 1 million tokens"
        : "Custom model from configuration; pricing has not been verified.";
}

/// <summary>
/// Router-compatible OpenAI text models. The lower-cost list uses standard input-token
/// pricing below $2/1M; Terra remains available as the explicit quality comparison.
/// Specialized, deprecated, audio, image, realtime, and Codex-only models are excluded.
/// </summary>
public static class AiRouterModelCatalog
{
    public const string DefaultModelId = "gpt-6-luna";

    public static IReadOnlyList<AiRouterModelOption> Options { get; } =
    [
        new("gpt-6-luna", "GPT-6 Luna", 0.10m, 0.50m,
            "https://developers.openai.com/api/docs/models/gpt-6-luna", "none"),
        new("gpt-5.6-luna", "GPT-5.6 Luna", 0.20m, 1.20m,
            "https://developers.openai.com/api/docs/models/gpt-5.6-luna", "none"),
        new("gpt-5.6-terra", "GPT-5.6 Terra", 2.00m, 12.00m,
            "https://developers.openai.com/api/docs/models/gpt-5.6-terra", "none"),
        new("gpt-5.4-mini", "GPT-5.4 Mini", 0.75m, 4.50m,
            "https://developers.openai.com/api/docs/models/gpt-5.4-mini", "none"),
        new("gpt-5.4-nano", "GPT-5.4 Nano", 0.20m, 1.25m,
            "https://developers.openai.com/api/docs/models/gpt-5.4-nano", "none"),
        new("gpt-5.2", "GPT-5.2", 1.75m, 14.00m,
            "https://developers.openai.com/api/docs/models/gpt-5.2", "none"),
        new("gpt-5.1", "GPT-5.1", 1.25m, 10.00m,
            "https://developers.openai.com/api/docs/models/gpt-5.1", "none"),
        new("gpt-5-mini", "GPT-5 Mini", 0.25m, 2.00m,
            "https://developers.openai.com/api/docs/models/gpt-5-mini", "none"),
        new("gpt-5-nano", "GPT-5 Nano", 0.05m, 0.40m,
            "https://developers.openai.com/api/docs/models/gpt-5-nano", "none"),
        new("o4-mini", "o4-mini", 1.10m, 4.40m,
            "https://developers.openai.com/api/docs/models/o4-mini", "low"),
        new("o3-mini", "o3-mini", 1.10m, 4.40m,
            "https://developers.openai.com/api/docs/models/o3-mini", "low"),
        new("gpt-4.1-mini", "GPT-4.1 Mini", 0.40m, 1.60m,
            "https://developers.openai.com/api/docs/models/gpt-4.1-mini", string.Empty),
        new("gpt-4o-mini", "GPT-4o Mini", 0.15m, 0.60m,
            "https://developers.openai.com/api/docs/models/gpt-4o-mini", string.Empty)
    ];

    public static AiRouterModelOption Default => Options[0];

    public static IReadOnlyList<AiRouterModelOption> ForConfiguration(AiConfiguration configuration) =>
        Find(configuration.RouterModel) is not null ? Options :
        [.. Options, new(configuration.RouterModel, configuration.RouterModel, null, null,
            "https://developers.openai.com/api/docs/models", configuration.RouterReasoningEffort)];

    public static AiRouterModelOption? Find(string? modelId) =>
        Options.FirstOrDefault(option => string.Equals(option.Id, modelId, StringComparison.Ordinal));
}
