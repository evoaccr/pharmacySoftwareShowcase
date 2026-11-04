using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace pharmacyApp.AI.Workflows;

/// <summary>
/// A stable, compact contract, derived from the registry rather than a second capability list.
/// Repeated parameter help is shared, but operation enums and workflow-specific limits survive.
/// </summary>
public static class WorkflowRouterCatalog
{
    // This is a model-text payload, never HTML. Literal Turkish characters avoid
    // paying tokens for repeated six-character Unicode escape sequences.
    public static JsonSerializerOptions ModelJson { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Describe(IReadOnlyList<AiWorkflowDefinition> definitions)
    {
        var shared = definitions.SelectMany(definition => definition.Parameters ?? new Dictionary<string, string>())
            .GroupBy(parameter => parameter.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                Name = group.Key,
                MostCommon = group.GroupBy(parameter => parameter.Value, StringComparer.Ordinal)
                    .OrderByDescending(values => values.Count()).ThenBy(values => values.Key, StringComparer.Ordinal).First()
            })
            .Where(value => value.MostCommon.Count() >= 2)
            .ToDictionary(value => value.Name, value => value.MostCommon.Key, StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            common_parameter_help = shared,
            workflows = definitions.Select(definition => new
            {
                name = definition.Name,
                purpose = definition.Description,
                inputs = definition.Parameters?.ToDictionary(parameter => parameter.Key,
                    parameter => shared.TryGetValue(parameter.Key, out var help) && help == parameter.Value
                        ? "" : parameter.Value, StringComparer.Ordinal),
                terminal = definition.ProducesUserResponse ? (bool?)true : null,
                resolves_entity = definition.ProvidesResolvedContext ? (bool?)true : null
            })
        }, ModelJson);
    }
}
