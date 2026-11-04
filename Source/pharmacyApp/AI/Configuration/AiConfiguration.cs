using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pharmacyApp.AI.Configuration;

public sealed record AiConfiguration
{
    public const int CurrentConfigurationVersion = 1;
    public int ConfigurationVersion { get; init; } = CurrentConfigurationVersion;
    public bool Enabled { get; init; } = true;
    public string RouterModel { get; init; } = AiRouterModelCatalog.DefaultModelId;
    public string SimpleModel { get; init; } = "gpt-5.6-luna";
    public string DefaultModel { get; init; } = "gpt-5.6-terra";
    public string ComplexModel { get; init; } = "gpt-5.6-sol";
    public string RouterReasoningEffort { get; init; } = "none";
    public string SimpleReasoningEffort { get; init; } = "low";
    public string DefaultReasoningEffort { get; init; } = "low";
    public string ComplexReasoningEffort { get; init; } = "medium";
    public int MaxToolTurns { get; init; } = 6;
    public int MaxToolsPerRequest { get; init; } = 12;
    public int MaxTotalToolCalls { get; init; } = 20;
    public int MaxRepeatedToolCalls { get; init; } = 2;
    public int MaxToolResultSize { get; init; } = 64_000;
    public int MaxConversationMessages { get; init; } = 12;
    public int RouterMaxOutputTokens { get; init; } = 1_800;
    public int ResponseMaxOutputTokens { get; init; } = 1_800;
    public int RequestTimeoutSeconds { get; init; } = 120;
    public int ToolTimeoutSeconds { get; init; } = 30;
    public double DeterministicRoutingThreshold { get; init; } = 0.55;
    public bool DebugMode { get; init; }
    public bool DebugShowWorkflowFunctions { get; init; }
    public string PromptVersion { get; init; } = "phase1-v1";
    public bool EnablePromptCaching { get; init; } = true;
    public bool EnableToolSearch { get; init; } = true;
    public bool EnableProgrammaticToolCalling { get; init; }
    public bool StoreResponses { get; init; }
    public string? GatewayEndpoint { get; init; }
    public int ConfirmationLifetimeMinutes { get; init; } = 5;

    public AiConfiguration Normalize() => this with
    {
        MaxToolTurns = Math.Clamp(MaxToolTurns, 1, 20),
        MaxToolsPerRequest = Math.Clamp(MaxToolsPerRequest, 1, 50),
        MaxTotalToolCalls = Math.Clamp(MaxTotalToolCalls, 1, 100),
        MaxRepeatedToolCalls = Math.Clamp(MaxRepeatedToolCalls, 1, 5),
        MaxToolResultSize = Math.Clamp(MaxToolResultSize, 1_000, 500_000),
        MaxConversationMessages = Math.Clamp(MaxConversationMessages, 2, 40),
        RouterMaxOutputTokens = Math.Clamp(RouterMaxOutputTokens, 900, 4_000),
        ResponseMaxOutputTokens = Math.Clamp(ResponseMaxOutputTokens, 256, 16_000),
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 10, 600),
        ToolTimeoutSeconds = Math.Clamp(ToolTimeoutSeconds, 1, 300),
        DeterministicRoutingThreshold = Math.Clamp(DeterministicRoutingThreshold, 0.1, 1),
        ConfirmationLifetimeMinutes = Math.Clamp(ConfirmationLifetimeMinutes, 1, 30)
    };
}

public interface IAiConfigurationProvider
{
    AiConfiguration GetCurrent();
}

/// <summary>local defaults use the shared JSONC file; explicit test paths remain isolated.</summary>
public sealed class AiConfigurationProvider : IAiConfigurationProvider
{
    public static string ConfigurationPath => pharmacyApp.Infrastructure.AppConfiguration.FilePath;
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string configurationPath;
    private readonly pharmacyApp.Infrastructure.ConfigurationStore? sharedStore;

    public AiConfigurationProvider(string? configurationPath = null)
    {
        this.configurationPath = configurationPath ?? ConfigurationPath;
        if (configurationPath is null || string.Equals(Path.GetFullPath(configurationPath),
                Path.GetFullPath(ConfigurationPath), StringComparison.OrdinalIgnoreCase))
            sharedStore = new pharmacyApp.Infrastructure.ConfigurationStore(this.configurationPath);
        EnsureConfigurationFile();
    }

    /// <summary>Uses the same shared-section JSONC behavior with an explicitly supplied local store.</summary>
    public AiConfigurationProvider(pharmacyApp.Infrastructure.ConfigurationStore configurationStore)
    {
        sharedStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        configurationPath = sharedStore.FilePath;
        EnsureConfigurationFile();
    }

    public string FilePath => configurationPath;

    public AiConfiguration GetCurrent()
    {
        EnsureConfigurationFile();
        var document = sharedStore is not null
            ? sharedStore.ReadSection<JsonElement>("Ai")
            : JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(configurationPath), FileOptions);
        if (document.ValueKind == JsonValueKind.Undefined) return new AiConfiguration();
        var configuration = document.Deserialize<AiConfiguration>(FileOptions) ?? new AiConfiguration();
        if (!document.TryGetProperty(nameof(AiConfiguration.ConfigurationVersion), out var version)
            || version.ValueKind == JsonValueKind.Null
            || configuration.ConfigurationVersion < AiConfiguration.CurrentConfigurationVersion)
        {
            // Only versionless legacy defaults migrate. A later explicit model selection
            // stays pinned, and shared JSONC comments/unknown settings remain untouched.
            configuration = configuration with
            {
                RouterModel = configuration.RouterModel == "gpt-5.6-luna"
                    ? AiRouterModelCatalog.DefaultModelId : configuration.RouterModel,
                RouterMaxOutputTokens = configuration.RouterMaxOutputTokens == 900
                    ? 1_800 : configuration.RouterMaxOutputTokens,
                ConfigurationVersion = AiConfiguration.CurrentConfigurationVersion
            };
            WriteValues(new
            {
                configuration.RouterModel,
                configuration.RouterMaxOutputTokens,
                configuration.ConfigurationVersion
            });
        }
        return configuration.Normalize();
    }

    public void Save(AiConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        WriteValues(configuration.Normalize() with
        {
            ConfigurationVersion = Math.Max(configuration.ConfigurationVersion, AiConfiguration.CurrentConfigurationVersion)
        });
    }

    private void WriteValues<T>(T values)
    {
        if (sharedStore is not null)
        {
            sharedStore.WriteSection("Ai", values);
            return;
        }
        var directory = Path.GetDirectoryName(configurationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = configurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(configurationPath)) File.Copy(configurationPath, temporaryPath);
            else File.WriteAllText(temporaryPath, "{}");
            var store = new pharmacyApp.Infrastructure.ConfigurationStore(temporaryPath);
            foreach (var property in JsonSerializer.SerializeToElement(values).EnumerateObject())
                store.WriteSection(property.Name, property.Value);
            File.Move(temporaryPath, configurationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(temporaryPath + ".tmp")) File.Delete(temporaryPath + ".tmp");
        }
    }

    private void EnsureConfigurationFile()
    {
        if (sharedStore is not null) sharedStore.EnsureFile();
        else if (!File.Exists(configurationPath)) Save(new AiConfiguration());
    }
}

public sealed class FixedAiConfigurationProvider(AiConfiguration configuration) : IAiConfigurationProvider
{
    public AiConfiguration GetCurrent() => configuration.Normalize();
}
