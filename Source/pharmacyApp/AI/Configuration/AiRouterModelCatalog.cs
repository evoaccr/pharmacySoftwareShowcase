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
/// This edition uses GPT-6 Luna only. The private gateway also enforces the model restriction.
/// </summary>
public static class AiRouterModelCatalog
{
    public const string DefaultModelId = "gpt-6-luna";

    public static IReadOnlyList<AiRouterModelOption> Options { get; } =
    [
        new("gpt-6-luna", "GPT-6 Luna", 0.10m, 0.50m,
            "https://developers.openai.com/api/docs/models/gpt-6-luna", "none")
    ];

    public static AiRouterModelOption Default => Options[0];

    public static IReadOnlyList<AiRouterModelOption> ForConfiguration(AiConfiguration configuration) =>
        Find(configuration.RouterModel) is not null ? Options :
        [.. Options, new(configuration.RouterModel, configuration.RouterModel, null, null,
            "https://developers.openai.com/api/docs/models", configuration.RouterReasoningEffort)];

    public static AiRouterModelOption? Find(string? modelId) =>
        Options.FirstOrDefault(option => string.Equals(option.Id, modelId, StringComparison.Ordinal));
}
