using System.Text.Json;
using System.Text.Json.Serialization;

namespace pharmacyApp.AI.Tools;

public sealed class AiToolCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IReadOnlyDictionary<string, IAiTool> tools;

    public AiToolCatalog(IEnumerable<IAiTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var registered = tools.ToArray();
        AiToolDefinitionValidator.Validate(registered);
        var indexed = new Dictionary<string, IAiTool>(StringComparer.Ordinal);
        foreach (var tool in registered)
        {
            indexed.Add(tool.Definition.Name, tool);
        }
        this.tools = indexed;
        Definitions = indexed.Values.Select(tool => tool.Definition)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<AiToolDefinition> Definitions { get; }

    public bool TryGet(string name, out IAiTool? tool) => tools.TryGetValue(name, out tool);

    public IAiTool GetRequired(string name) => tools.TryGetValue(name, out var tool)
        ? tool
        : throw new KeyNotFoundException($"AI tool '{name}' is not registered.");

    public Task<object> ExecuteAsync(
        string name,
        JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        GetRequired(name).ExecuteAsync(parameters, cancellationToken);

    public string GetDefinitionsJson(bool indented = false) => JsonSerializer.Serialize(
        Definitions,
        new JsonSerializerOptions(SerializerOptions) { WriteIndented = indented });

    public async Task<string> ExecuteJsonAsync(
        string name,
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(parametersJson);
        var result = await ExecuteAsync(name, document.RootElement, cancellationToken);
        return JsonSerializer.Serialize(result, SerializerOptions);
    }
}

internal sealed class DelegatingAiTool : IAiTool
{
    private readonly Func<System.Text.Json.JsonElement, CancellationToken, Task<object>> execute;

    public DelegatingAiTool(
        AiToolDefinition definition,
        Func<System.Text.Json.JsonElement, CancellationToken, Task<object>> execute)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public AiToolDefinition Definition { get; }

    public Task<object> ExecuteAsync(
        System.Text.Json.JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        execute(parameters, cancellationToken);
}
