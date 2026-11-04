using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pharmacyApp.AI.Tools;

namespace pharmacyApp.AI.Runtime;

public interface IAiToolRegistry
{
    IReadOnlyList<AiToolDefinition> GetAllTools();
    IAiTool? GetTool(string name);
    IReadOnlyList<AiToolDefinition> GetToolsByNamespace(string toolNamespace);
    IReadOnlyList<string> Namespaces { get; }
    string CatalogVersion { get; }
    int ToolCount { get; }
}

public sealed class AiToolRegistry : IAiToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAiTool> tools;

    public AiToolRegistry(IEnumerable<IAiTool> tools)
    {
        var all = tools.ToArray();
        AiToolDefinitionValidator.Validate(all);
        this.tools = all.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
        Definitions = this.tools.Values.Select(tool => tool.Definition)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal).ToArray();
        Namespaces = Definitions.Select(GetNamespace).Distinct(StringComparer.Ordinal).Order().ToArray();
        CatalogVersion = ComputeVersion(Definitions);
    }

    public IReadOnlyList<AiToolDefinition> Definitions { get; }
    public IReadOnlyList<string> Namespaces { get; }
    public string CatalogVersion { get; }
    public int ToolCount => Definitions.Count;
    public IReadOnlyList<AiToolDefinition> GetAllTools() => Definitions;
    public IAiTool? GetTool(string name) => tools.GetValueOrDefault(name);
    public IReadOnlyList<AiToolDefinition> GetToolsByNamespace(string toolNamespace) => Definitions
        .Where(definition => string.Equals(GetNamespace(definition), toolNamespace, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    public static AiToolRegistry Discover(Assembly? assembly = null)
    {
        assembly ??= typeof(AiToolRegistry).Assembly;
        var discovered = new List<IAiTool>();
        var providers = assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IAiToolProvider).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        foreach (var type in providers)
        {
            var constructor = type.GetConstructors()
                .OrderBy(candidate => candidate.GetParameters().Length)
                .FirstOrDefault(candidate => candidate.GetParameters().All(parameter => parameter.HasDefaultValue));
            if (constructor is null)
                throw new InvalidOperationException($"AI tool provider '{type.FullName}' needs a public constructor whose parameters are optional.");
            var arguments = constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
            var provider = (IAiToolProvider)constructor.Invoke(arguments);
            discovered.AddRange(provider.Tools.Where(tool => pharmacyApp.Infrastructure.OnlineAccessPolicy.IsAiToolEnabled(tool.Definition.Name)));
        }
        return new AiToolRegistry(discovered);
    }

    public static string GetNamespace(AiToolDefinition definition) =>
        definition.Name[..definition.Name.IndexOf('.', StringComparison.Ordinal)];

    private static string ComputeVersion(IReadOnlyList<AiToolDefinition> definitions)
    {
        var canonical = JsonSerializer.Serialize(definitions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }
}
