using System.Text.Json;
using pharmacyApp.Modules.Medula;

namespace pharmacyApp.AI.Tools.Medula;

internal static partial class MedulaClinicalAiTools
{
    private static IAiTool ScreenTool(
        string name,
        string description,
        IReadOnlyDictionary<string, AiToolParameterDefinition> parameters,
        IReadOnlyList<string> tags,
        Func<AiToolParameterReader, CancellationToken, Task<MedulaAiRemoteScreenResult>> execute) =>
        new DelegatingAiTool(
            Definition(name, description, parameters, RemoteReturns(), tags),
            async (json, token) => (object)await execute(Reader(json), token));

    private static AiToolDefinition Definition(
        string name,
        string description,
        IReadOnlyDictionary<string, AiToolParameterDefinition> parameters,
        IReadOnlyDictionary<string, string> returns,
        IReadOnlyList<string> tags) =>
        new(name, description, AiToolRisks.ConfirmationRequired, parameters, returns, tags);

    private static AiToolParameterDefinition Parameter(
        string type,
        string description,
        bool required = false,
        IReadOnlyList<string>? allowedValues = null,
        object? defaultValue = null) =>
        new(type, description, required, allowedValues, defaultValue);

    private static AiToolParameterDefinition PatientTcParameter() =>
        Parameter("string", "Patient T.C. identity number (exactly 11 digits).", true);

    private static AiToolParameterDefinition OffsetParameter() =>
        Parameter("integer", "Zero-based row offset applied to each returned Medula table.", defaultValue: 0);

    private static AiToolParameterDefinition LimitParameter() =>
        Parameter("integer", "Maximum rows returned per Medula table (1-100).", defaultValue: 50);

    private static IReadOnlyDictionary<string, AiToolParameterDefinition> PatientScreenParameters() =>
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["patient_tc"] = PatientTcParameter(),
            ["offset"] = OffsetParameter(),
            ["limit"] = LimitParameter()
        };

    private static IReadOnlyDictionary<string, string> RemoteReturns() =>
        new Dictionary<string, string>
        {
            ["success"] = "boolean",
            ["title"] = "string",
            ["message"] = "string",
            ["warning"] = "string|null",
            ["fields"] = "array<{label:string,value:string,kind:string}>",
            ["tables"] = "array<{title:string,headers:array<string>,rows:array<array<string>>,offset:integer,limit:integer,total_count:integer,next_offset:integer|null}>",
            ["editable_rows"] = "array<{medication_name:string,values:array<{label:string,value:string}>}>"
        };

    private static MedulaAiRemoteScreenResult Remote(
        MedulaRemoteScreen screen,
        int offset,
        int limit)
    {
        var fields = screen.Fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Label))
            .Select(field => new MedulaAiRemoteField(
                field.Label.Trim(), field.Value.Trim(), field.Kind))
            .ToArray();
        var tables = screen.Tables.Select(table =>
        {
            var rows = table.Rows.Skip(offset).Take(limit).ToArray();
            return new MedulaAiRemoteTable(
                table.Title?.Trim() ?? string.Empty,
                table.Headers,
                rows,
                offset,
                limit,
                table.Rows.Count,
                NextOffset(offset, rows.Length, table.Rows.Count));
        }).ToArray();
        var editableRows = (screen.TreatmentRows ?? [])
            .Skip(offset)
            .Take(limit)
            .Select(row => new MedulaAiEditableRow(
                row.MedicationName,
                row.Cells.Select(cell => new MedulaAiEditableValue(
                    cell.Label.Trim().TrimEnd(':'), cell.Value.Trim())).ToArray()))
            .ToArray();
        return new MedulaAiRemoteScreenResult(
            screen.IsOpen,
            screen.Title,
            screen.Message,
            screen.IsOpen ? null : screen.Message,
            fields,
            tables,
            editableRows);
    }

    private static int Offset(AiToolParameterReader reader) =>
        reader.OptionalInteger("offset", 0, 0, 100_000);

    private static int Limit(AiToolParameterReader reader) =>
        reader.OptionalInteger("limit", 50, 1, 100);

    private static int? NextOffset(int offset, int count, int total) =>
        offset + count < total ? offset + count : null;

    private static AiToolParameterReader Reader(JsonElement parameters) => new(parameters);

    private static string Tc(string value) => Digits(value, "patient_tc", 11, 11);

    private static string Digits(
        string value,
        string parameterName,
        int minimumLength,
        int maximumLength)
    {
        var normalized = value.Trim();
        if (normalized.Length < minimumLength || normalized.Length > maximumLength
            || normalized.Any(character => !char.IsDigit(character)))
            throw new AiToolValidationException(
                $"Parameter '{parameterName}' must contain {minimumLength}" +
                (minimumLength == maximumLength ? string.Empty : $"-{maximumLength}") +
                " digits.");
        return normalized;
    }

    private static string Text(
        string value,
        string parameterName,
        int maximumLength,
        int minimumLength = 1)
    {
        var normalized = value.Trim();
        if (normalized.Length < minimumLength || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
            throw new AiToolValidationException(
                $"Parameter '{parameterName}' must contain {minimumLength}-{maximumLength} printable characters.");
        return normalized;
    }

    private static void AddField(
        IDictionary<string, string> destination,
        string name,
        IReadOnlyDictionary<string, string>? source,
        string sourceName)
    {
        if (source is not null
            && source.TryGetValue(sourceName, out var value)
            && !string.IsNullOrWhiteSpace(value))
            destination[name] = value.Trim();
    }

    private static IReadOnlyList<string> DistinctMessages(params string?[] values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
