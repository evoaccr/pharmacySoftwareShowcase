using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Runtime;

namespace pharmacyApp.AI.Debug;

public sealed record AiDiagnostics(
    bool Enabled,
    bool TransportConfigured,
    string Transport,
    string RouterModel,
    string SimpleModel,
    string DefaultModel,
    string ComplexModel,
    int ToolCount,
    int NamespaceCount,
    string CatalogVersion,
    string PromptVersion,
    bool DebugMode,
    string ConfigurationFile,
    IReadOnlyList<string> InvalidTools);

public sealed class AiDiagnosticsService(
    IAiConfigurationProvider configurationProvider,
    IAiToolRegistry registry,
    IAiModelTransport transport)
{
    public AiDiagnostics Inspect()
    {
        var configuration = configurationProvider.GetCurrent();
        return new AiDiagnostics(
            configuration.Enabled, transport.IsConfigured, transport.ConfigurationDescription,
            configuration.RouterModel, configuration.SimpleModel, configuration.DefaultModel, configuration.ComplexModel,
            registry.ToolCount, registry.Namespaces.Count, registry.CatalogVersion,
            configuration.PromptVersion, configuration.DebugMode,
            configurationProvider is AiConfigurationProvider fileProvider
                ? fileProvider.FilePath
                : "Sabit/test yapılandırması",
            []);
    }

    public string FormatTurkish()
    {
        var value = Inspect();
        return $"AI enabled: {value.Enabled}\nConnection: {value.Transport} ({value.TransportConfigured})\n" +
               $"Router model: {value.RouterModel}\n" +
               $"Response models: {value.SimpleModel} / {value.DefaultModel} / {value.ComplexModel}\n" +
               $"Tool catalog: {value.ToolCount} tools, {value.NamespaceCount} namespaces\n" +
               $"Catalog version: {value.CatalogVersion}\nPrompt version: {value.PromptVersion}\nDebug: {value.DebugMode}\n" +
               $"Configuration: {value.ConfigurationFile}";
    }
}
