using pharmacyApp.Modules.Medula;

namespace pharmacyApp.AI.Tools.Medula;

internal static partial class MedulaClinicalAiTools
{
    private static IAiTool DrugSearch(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.drug_search",
            "Searches Medula's prescription drug catalog by a phrase of at least three characters and returns barcode/name matches.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["query"] = Parameter("string", "Drug-name search phrase (3-80 characters).", true),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            new Dictionary<string, string>
            {
                ["success"] = "boolean",
                ["message"] = "string",
                ["warning"] = "string|null",
                ["items"] = "array<{barcode:string,name:string}>",
                ["offset"] = "integer",
                ["limit"] = "integer",
                ["total_count"] = "integer",
                ["next_offset"] = "integer|null"
            },
            ["medula", "drug", "search"]),
        async (parameters, token) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("query", "offset", "limit");
            var query = Text(reader.RequireString("query"), "query", 80, 3);
            var result = await client.SearchDrugsAsync(query, token);
            var items = result.Options
                .GroupBy(option => $"{option.Barcode}|{option.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(option => new MedulaAiDrugSearchItem(option.Barcode, option.Name))
                .ToArray();
            var offset = Offset(reader);
            var limit = Limit(reader);
            var page = items.Skip(offset).Take(limit).ToArray();
            return new MedulaAiDrugSearchResult(
                result.IsSuccess, result.Message, result.WarningText, page, offset, limit,
                items.Length, NextOffset(offset, page.Length, items.Length));
        });

    private static IAiTool DrugGet(IMedulaAiOperations client) =>
        DrugScreenTool(
            "medula.drug_get",
            "Returns Medula's detailed payment, usage, prescription-rule, and product fields for one exact drug barcode.",
            ["medula", "drug", "detail"],
            client.GetDrugAsync);

    private static IAiTool DrugEquivalentsSearch(IMedulaAiOperations client) =>
        DrugScreenTool(
            "medula.drug_equivalents_search",
            "Searches the Medula equivalent-drug tables for one exact source drug barcode.",
            ["medula", "drug", "equivalent", "search"],
            client.SearchDrugEquivalentsAsync);

    private static IAiTool DoctorSearch(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.doctor_search",
            "Searches Medula by doctor diploma registration number and returns normalized doctor fields and branch choices.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["diploma_registration_number"] = Parameter(
                    "string", "Doctor diploma registration number (1-32 printable characters).", true)
            },
            new Dictionary<string, string>
            {
                ["success"] = "boolean",
                ["message"] = "string",
                ["warning"] = "string|null",
                ["fields"] = "object",
                ["branch_selection"] = "remote_screen|null"
            },
            ["medula", "doctor", "search"]),
        async (parameters, token) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("diploma_registration_number");
            var result = await client.SearchDoctorAsync(
                Text(reader.RequireString("diploma_registration_number"),
                    "diploma_registration_number", 32), token);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddField(fields, "diploma_registration_number", result.FieldValues, "f:t43");
            AddField(fields, "diploma_number", result.FieldValues, "f:t39");
            AddField(fields, "first_name", result.FieldValues, "f:t41");
            AddField(fields, "last_name", result.FieldValues, "f:t42");
            AddField(fields, "certificate_code", result.FieldValues, "f:m7");
            AddField(fields, "branch", result.FieldValues, "medula:branch");
            return new MedulaAiDoctorSearchResult(
                result.IsSuccess, result.Message, result.WarningText, fields,
                result.OpenedScreen is null ? null : Remote(result.OpenedScreen, 0, 100));
        });

    private static IAiTool DiagnosisSearch(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.diagnosis_search",
            "Searches Medula's ICD-10 catalog by code or description and returns bounded code/description matches.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["query"] = Parameter("string", "ICD-10 code or diagnosis phrase (2-80 characters).", true),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            new Dictionary<string, string>
            {
                ["success"] = "boolean",
                ["message"] = "string",
                ["warning"] = "string|null",
                ["items"] = "array<{code:string,description:string}>",
                ["offset"] = "integer",
                ["limit"] = "integer",
                ["total_count"] = "integer",
                ["next_offset"] = "integer|null"
            },
            ["medula", "diagnosis", "icd-10", "search"]),
        async (parameters, token) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("query", "offset", "limit");
            var result = await client.SearchDiagnosesAsync(
                Text(reader.RequireString("query"), "query", 80, 2), token);
            var items = result.Options
                .GroupBy(option => $"{option.Code}|{option.Description}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(option => new MedulaAiDiagnosisSearchItem(option.Code, option.Description))
                .ToArray();
            var offset = Offset(reader);
            var limit = Limit(reader);
            var page = items.Skip(offset).Take(limit).ToArray();
            return new MedulaAiDiagnosisSearchResult(
                result.IsSuccess, result.Message, result.WarningText, page, offset, limit,
                items.Length, NextOffset(offset, page.Length, items.Length));
        });

    private static IAiTool DrugScreenTool(
        string name,
        string description,
        IReadOnlyList<string> tags,
        Func<string, CancellationToken, Task<MedulaDrugActionResult>> operation) =>
        ScreenTool(
            name,
            description,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["barcode"] = Parameter("string", "Exact numeric drug barcode (8-14 digits).", true),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            tags,
            async (reader, token) =>
            {
                reader.RejectUnknown("barcode", "offset", "limit");
                var result = await operation(
                    Digits(reader.RequireString("barcode"), "barcode", 8, 14), token);
                if (result.Screen is not null)
                    return Remote(result.Screen, Offset(reader), Limit(reader));
                return new MedulaAiRemoteScreenResult(
                    result.IsSuccess, string.Empty, result.Message, result.WarningText,
                    [], [], []);
            });
}
