using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace pharmacyApp.AI.Tools;

public static class AiToolRisks
{
    public const string ReadOnly = "read_only";
    public const string LowRiskWrite = "low_risk_write";
    public const string ConfirmationRequired = "confirmation_required";
    public const string HighRisk = "high_risk";
}

public sealed record AiToolParameterDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("enum"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AllowedValues = null,
    [property: JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? DefaultValue = null,
    [property: JsonPropertyName("items_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ItemsType = null);

public sealed record AiToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("risk")] string Risk,
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, AiToolParameterDefinition> Parameters,
    [property: JsonPropertyName("returns")] IReadOnlyDictionary<string, string> Returns,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);

public interface IAiTool
{
    AiToolDefinition Definition { get; }

    Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default);
}

/// <summary>
/// Domain-level registration boundary. Runtime discovery instantiates providers rather than
/// maintaining a central list of every tool.
/// </summary>
public interface IAiToolProvider
{
    IReadOnlyCollection<IAiTool> Tools { get; }
}

public static partial class AiToolDefinitionValidator
{
    private static readonly HashSet<string> AllowedRisks =
    [
        AiToolRisks.ReadOnly,
        AiToolRisks.LowRiskWrite,
        AiToolRisks.ConfirmationRequired,
        AiToolRisks.HighRisk
    ];

    public static void Validate(IEnumerable<IAiTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var definition = tool.Definition;
            if (!ToolNamePattern().IsMatch(definition.Name))
                throw new InvalidOperationException(
                    $"AI tool name '{definition.Name}' must use lowercase namespace.action naming.");
            if (!names.Add(definition.Name))
                throw new InvalidOperationException($"Duplicate AI tool name '{definition.Name}'.");
            if (string.IsNullOrWhiteSpace(definition.Description))
                throw new InvalidOperationException($"AI tool '{definition.Name}' requires a description.");
            if (!AllowedRisks.Contains(definition.Risk))
                throw new InvalidOperationException($"AI tool '{definition.Name}' has invalid risk '{definition.Risk}'.");
            if (definition.Returns.Count == 0)
                throw new InvalidOperationException($"AI tool '{definition.Name}' requires a return definition.");
            if (definition.Tags.Count == 0 || definition.Tags.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException($"AI tool '{definition.Name}' requires non-blank tags.");
            foreach (var (parameterName, parameter) in definition.Parameters)
            {
                if (!ParameterNamePattern().IsMatch(parameterName))
                    throw new InvalidOperationException(
                        $"AI tool '{definition.Name}' parameter '{parameterName}' must use snake_case.");
                if (string.IsNullOrWhiteSpace(parameter.Type) || string.IsNullOrWhiteSpace(parameter.Description))
                    throw new InvalidOperationException(
                        $"AI tool '{definition.Name}' parameter '{parameterName}' requires type and description.");
                if (string.Equals(parameter.Type, "array", StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(parameter.ItemsType))
                    throw new InvalidOperationException(
                        $"AI tool '{definition.Name}' array parameter '{parameterName}' requires items_type metadata.");
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$")]
    private static partial Regex ToolNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ParameterNamePattern();
}

public sealed class AiToolValidationException : ArgumentException
{
    public AiToolValidationException(string message) : base(message)
    {
    }
}

internal sealed class AiToolParameterReader
{
    private readonly JsonElement parameters;

    public AiToolParameterReader(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new AiToolValidationException("Tool parameters must be a JSON object.");
        this.parameters = parameters;
    }

    public void RejectUnknown(params string[] acceptedNames)
    {
        var accepted = acceptedNames.ToHashSet(StringComparer.Ordinal);
        var unknown = parameters.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !accepted.Contains(name))
            .ToArray();
        if (unknown.Length > 0)
            throw new AiToolValidationException($"Unknown parameter(s): {string.Join(", ", unknown)}.");
    }

    public string RequireString(string name)
    {
        var value = OptionalString(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new AiToolValidationException($"Parameter '{name}' is required and cannot be blank.");
    }

    public string? OptionalString(string name)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new AiToolValidationException($"Parameter '{name}' must be a string.");
        var result = value.GetString()?.Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    public bool OptionalBoolean(string name, bool defaultValue)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new AiToolValidationException($"Parameter '{name}' must be a boolean.");
        return value.GetBoolean();
    }

    public bool? OptionalNullableBoolean(string name)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new AiToolValidationException($"Parameter '{name}' must be a boolean.");
        return value.GetBoolean();
    }

    public int OptionalInteger(string name, int defaultValue, int minimum, int maximum)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new AiToolValidationException($"Parameter '{name}' must be an integer.");
        if (result < minimum || result > maximum)
            throw new AiToolValidationException($"Parameter '{name}' must be between {minimum} and {maximum}.");
        return result;
    }

    public int? OptionalNullableInteger(string name, int minimum, int maximum)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new AiToolValidationException($"Parameter '{name}' must be an integer.");
        if (result < minimum || result > maximum)
            throw new AiToolValidationException($"Parameter '{name}' must be between {minimum} and {maximum}.");
        return result;
    }

    public decimal OptionalDecimal(string name, decimal defaultValue, decimal minimum, decimal maximum)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result))
            throw new AiToolValidationException($"Parameter '{name}' must be a number.");
        if (result < minimum || result > maximum)
            throw new AiToolValidationException($"Parameter '{name}' must be between {minimum} and {maximum}.");
        return result;
    }

    public decimal? OptionalNullableDecimal(string name, decimal minimum, decimal maximum)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result))
            throw new AiToolValidationException($"Parameter '{name}' must be a number.");
        if (result < minimum || result > maximum)
            throw new AiToolValidationException($"Parameter '{name}' must be between {minimum} and {maximum}.");
        return result;
    }

    public IReadOnlyList<string> OptionalStringArray(string name, int maximumCount)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw new AiToolValidationException($"Parameter '{name}' must be an array of strings.");

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new AiToolValidationException($"Parameter '{name}' can contain only non-blank strings.");
            items.Add(item.GetString()!.Trim());
        }

        var distinct = items.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length > maximumCount)
            throw new AiToolValidationException($"Parameter '{name}' can contain at most {maximumCount} distinct values.");
        return distinct;
    }

    public IReadOnlyList<decimal>? OptionalDecimalArray(
        string name,
        int requiredCount,
        decimal minimum,
        decimal maximum)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array)
            throw new AiToolValidationException($"Parameter '{name}' must be an array of numbers.");

        var items = new List<decimal>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDecimal(out var result))
                throw new AiToolValidationException($"Parameter '{name}' can contain only numbers.");
            if (result < minimum || result > maximum)
                throw new AiToolValidationException(
                    $"Every value in '{name}' must be between {minimum} and {maximum}.");
            items.Add(result);
        }
        if (items.Count != requiredCount)
            throw new AiToolValidationException(
                $"Parameter '{name}' must contain exactly {requiredCount} value(s), matching 'entered_codes'.");
        return items;
    }

    public string EnumValue(string name, string defaultValue, params string[] allowedValues)
    {
        var value = OptionalString(name) ?? defaultValue;
        var match = allowedValues.FirstOrDefault(allowed =>
            string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new AiToolValidationException(
            $"Parameter '{name}' must be one of: {string.Join(", ", allowedValues)}.");
    }

    public IReadOnlyList<string> RequireStringArray(string name, int minimumCount, int maximumCount)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new AiToolValidationException($"Parameter '{name}' must be an array of strings.");

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new AiToolValidationException($"Parameter '{name}' can contain only non-blank strings.");
            items.Add(item.GetString()!.Trim());
        }

        var distinct = items.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length < minimumCount || distinct.Length > maximumCount)
            throw new AiToolValidationException(
                $"Parameter '{name}' must contain between {minimumCount} and {maximumCount} distinct values.");
        return distinct;
    }

    public IReadOnlyList<string> RequireStringArrayPreservingOrder(
        string name,
        int minimumCount,
        int maximumCount)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new AiToolValidationException($"Parameter '{name}' must be an array of strings.");

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new AiToolValidationException($"Parameter '{name}' can contain only non-blank strings.");
            items.Add(item.GetString()!.Trim());
        }
        if (items.Count < minimumCount || items.Count > maximumCount)
            throw new AiToolValidationException(
                $"Parameter '{name}' must contain between {minimumCount} and {maximumCount} values.");
        return items;
    }
}
