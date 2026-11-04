using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using pharmacyApp.Modules.Shared;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.AI.Tools.QR;

/// <summary>
/// Discoverable, executable AI tools owned by the QR Movements module.
/// All tools are read-only wrappers over QrFunctions; no UI or SQL is exposed.
/// </summary>
public sealed class QrAiToolCatalog : IAiToolProvider
{
    private const int MaximumCodeLength = 512;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly QrFunctions qr;
    private readonly IReadOnlyDictionary<string, IAiTool> tools;

    public QrAiToolCatalog(QrFunctions? qr = null)
    {
        this.qr = qr ?? PharmacyFunctions.Qr;
        var registered = new IAiTool[]
        {
            Tool(SearchMovementsDefinition(), SearchMovementsAsync),
            Tool(GetHistoryDefinition(), GetHistoryAsync),
            Tool(ListMovementTypesDefinition(), ListMovementTypesAsync),
            Tool(AggregateMovementsDefinition(), AggregateMovementsAsync),
            Tool(AuditMovementChainsDefinition(), AuditMovementChainsAsync)
        };
        AiToolDefinitionValidator.Validate(registered);
        tools = registered.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<AiToolDefinition> Definitions => tools.Values
        .Select(tool => tool.Definition)
        .OrderBy(definition => definition.Name, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyCollection<IAiTool> Tools => tools.Values.ToArray();

    public string GetDefinitionsJson(bool indented = false) => JsonSerializer.Serialize(
        Definitions,
        new JsonSerializerOptions(SerializerOptions) { WriteIndented = indented });

    public Task<object> ExecuteAsync(
        string toolName,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        if (!tools.TryGetValue(toolName, out var tool))
            throw new KeyNotFoundException($"Unknown QR AI tool '{toolName}'.");
        return tool.ExecuteAsync(parameters, cancellationToken);
    }

    public async Task<string> ExecuteJsonAsync(
        string toolName,
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(parametersJson);
        var result = await ExecuteAsync(toolName, document.RootElement, cancellationToken);
        return JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task<object> SearchMovementsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return await SearchMovementsAsync(parameters, cancellationToken, 200);
    }

    private async Task<object> SearchMovementsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken,
        int maximumLimit)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(
            "start_date", "end_date", "movement_type_codes", "direction",
            "search", "search_kind", "barcodes", "user_id", "address_type", "address_code",
            "before_movement_date", "before_record_id", "include_summary", "limit");

        var startDate = ReadDate(reader.RequireString("start_date"), "start_date");
        var endDate = ReadDate(reader.RequireString("end_date"), "end_date");
        if (startDate > endDate)
            throw new AiToolValidationException("'start_date' cannot be after 'end_date'.");
        if (endDate > DateOnly.FromDateTime(DateTime.Today))
            throw new AiToolValidationException("'end_date' cannot be later than the current pharmacy date.");

        DateTime endExclusive;
        try
        {
            endExclusive = endDate.ToDateTime(TimeOnly.MinValue).AddDays(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new AiToolValidationException("'end_date' is outside the supported date range.");
        }

        var search = reader.OptionalString("search");
        var searchKind = reader.OptionalString("search_kind");
        if ((search is null) != (searchKind is null))
            throw new AiToolValidationException("'search' and 'search_kind' must be supplied together.");
        var barcodes = ReadOptionalStringArray(parameters, "barcodes", 1, 20)?
            .Select(value => ValidateCode(value, "barcodes"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (barcodes is not null && search is not null)
            throw new AiToolValidationException("'barcodes' cannot be combined with 'search'/'search_kind'.");
        var searchMode = searchKind?.ToLowerInvariant() switch
        {
            null => QrMovementSearchMode.None,
            "qr_code" => QrMovementSearchMode.QrCode,
            "barcode" => QrMovementSearchMode.Barcode,
            "medicine_name_contains" => QrMovementSearchMode.MedicineNameContains,
            "medicine_name_exact" => QrMovementSearchMode.MedicineNameExact,
            "brand_name_exact" => QrMovementSearchMode.BrandNameExact,
            _ => throw new AiToolValidationException(
                "'search_kind' must be one of: qr_code, barcode, medicine_name_contains, medicine_name_exact, brand_name_exact.")
        };
        if (searchMode is QrMovementSearchMode.QrCode or QrMovementSearchMode.Barcode)
            search = ValidateCode(search!, "search");
        else if (search is not null)
            search = ValidateText(search, "search", 256);

        var addressType = reader.EnumValue(
            "address_type", "all", "all", "patient", "pharmacy", "depot");
        var addressPrefix = addressType switch
        {
            "patient" => "P",
            "pharmacy" => "E",
            "depot" => "D",
            _ => string.Empty
        };
        var addressCode = reader.OptionalString("address_code");
        if (addressCode is not null && addressPrefix.Length == 0)
            throw new AiToolValidationException(
                "'address_type' must be patient, pharmacy, or depot when 'address_code' is supplied.");
        if (addressCode is not null && !addressCode.StartsWith(addressPrefix, StringComparison.Ordinal))
            throw new AiToolValidationException(
                $"'address_code' must start with '{addressPrefix}' for address_type '{addressType}'.");
        if (addressCode is not null)
            addressCode = ValidateText(addressCode, "address_code", 128);

        var userId = reader.OptionalString("user_id");
        if (userId is not null)
            userId = ValidateText(userId, "user_id", 128);

        var beforeMovementDate = ReadOptionalCursorDate(reader.OptionalString("before_movement_date"));
        var beforeRecordId = reader.OptionalNullableInteger("before_record_id", 1, int.MaxValue);
        if (beforeMovementDate.HasValue != beforeRecordId.HasValue)
            throw new AiToolValidationException(
                "'before_movement_date' and 'before_record_id' must be supplied together.");

        var movementTypes = await qr.GetMovementTypesAsync(cancellationToken);
        var requestedCodes = ReadOptionalStringArray(parameters, "movement_type_codes", 1, 100);
        var selectedTypes = SelectMovementTypes(
            movementTypes,
            requestedCodes,
            reader.EnumValue("direction", "all", "all", "entry", "exit"));
        var query = new QrMovementQuery(
            startDate.ToDateTime(TimeOnly.MinValue),
            endExclusive,
            selectedTypes.Select(type => type.Id).ToArray(),
            search ?? string.Empty,
            searchMode,
            userId ?? string.Empty,
            addressCode ?? string.Empty,
            addressPrefix,
            Barcodes: barcodes);
        var limit = reader.OptionalInteger("limit", 50, 1, maximumLimit);
        var includeSummary = reader.OptionalBoolean("include_summary", true);
        var page = await qr.SearchMovementsAsync(
            query,
            beforeMovementDate,
            beforeRecordId,
            limit + 1,
            includeSummary,
            cancellationToken);
        return ToPageResult(page, limit);
    }

    private async Task<object> GetHistoryAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("qr_code", "before_movement_date", "before_record_id", "limit");
        var qrCode = ValidateCode(reader.RequireString("qr_code"), "qr_code");
        var beforeMovementDate = ReadOptionalCursorDate(reader.OptionalString("before_movement_date"));
        var beforeRecordId = reader.OptionalNullableInteger("before_record_id", 1, int.MaxValue);
        if (beforeMovementDate.HasValue != beforeRecordId.HasValue)
            throw new AiToolValidationException(
                "'before_movement_date' and 'before_record_id' must be supplied together.");

        var limit = reader.OptionalInteger("limit", 100, 1, 200);
        var page = await qr.GetHistoryAsync(
            qrCode,
            beforeMovementDate,
            beforeRecordId,
            limit + 1,
            cancellationToken);
        var result = ToPageResult(page, limit);
        return new QrHistoryToolResult(
            result.Summary is { TotalCount: > 0 },
            qrCode,
            result.Movements,
            result.Summary?.TotalCount ?? 0,
            result.Limit,
            result.HasMore,
            result.NextCursor);
    }

    private async Task<object> ListMovementTypesAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown();
        var types = await qr.GetMovementTypesAsync(cancellationToken);
        return new QrMovementTypesToolResult(types.Select(type => new QrMovementTypeToolResult(
            type.Code,
            type.DisplayName,
            type.IsEntry ? "entry" : "exit")).ToArray());
    }

    private async Task<object> AggregateMovementsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(AnalysisFilterNames.Concat(["group_by", "missing_user_only", "limit", "scan_limit"]).ToArray());
        var groupBy = reader.EnumValue("group_by", "movement_type",
            "movement_type", "direction", "barcode", "medicine", "address", "user", "day", "week", "hour", "weekday", "day_hour");
        var missingUserOnly = reader.OptionalBoolean("missing_user_only", false);
        var limit = reader.OptionalInteger("limit", 100, 1, 500);
        var scanLimit = reader.OptionalInteger("scan_limit", 10_000, 1, 50_000);
        var loaded = await LoadAnalysisMovementsAsync(parameters, scanLimit, cancellationToken);
        var source = missingUserOnly
            ? loaded.Movements.Where(movement => movement.User is null || string.IsNullOrWhiteSpace(movement.User.UserId)).ToArray()
            : loaded.Movements;
        var groups = source
            .GroupBy(movement => AggregateKey(movement, groupBy))
            .Select(group => new QrMovementAggregateToolRow(
                group.Key.Key,
                group.Key.Label,
                group.Count(),
                group.Count(movement => movement.MovementType.Direction == "entry"),
                group.Count(movement => movement.MovementType.Direction == "exit"),
                group.Select(movement => movement.QrCode).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(group => group.MovementCount)
            .ThenBy(group => group.GroupLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new QrMovementAggregateToolResult(
            groups.Take(limit).ToArray(),
            groups.Length,
            loaded.Movements.Count,
            loaded.Truncated,
            limit);
    }

    private async Task<object> AuditMovementChainsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(AnalysisFilterNames.Concat(["audit_kind", "repeat_window_minutes", "limit", "scan_limit"]).ToArray());
        var auditKind = reader.EnumValue("audit_kind", "all", "all", "repeated", "exit_without_entry", "entry_without_exit", "missing_address");
        var repeatWindow = reader.OptionalInteger("repeat_window_minutes", 5, 0, 1440);
        var limit = reader.OptionalInteger("limit", 200, 1, 500);
        var scanLimit = reader.OptionalInteger("scan_limit", 10_000, 1, 50_000);
        var loaded = await LoadAnalysisMovementsAsync(parameters, scanLimit, cancellationToken);
        var issues = new List<QrMovementAuditToolRow>();
        if (auditKind is "all" or "missing_address")
        {
            issues.AddRange(loaded.Movements
                .Where(movement => movement.MovementAddress is null ||
                    (string.IsNullOrWhiteSpace(movement.MovementAddress.Code) &&
                     string.IsNullOrWhiteSpace(movement.MovementAddress.Name)))
                .Select(movement => AuditRow(movement, "missing_address", "Hareket adresi kaydı boş.")));
        }

        foreach (var chain in loaded.Movements.Where(movement => !string.IsNullOrWhiteSpace(movement.QrCode))
                     .GroupBy(movement => movement.QrCode, StringComparer.Ordinal))
        {
            var ordered = chain.OrderBy(movement => ParseMovementDate(movement.MovementDate))
                .ThenBy(movement => movement.RecordId).ToArray();
            var openEntries = new Queue<QrMovementToolResult>();
            QrMovementToolResult? previous = null;
            foreach (var movement in ordered)
            {
                if (auditKind is ("all" or "repeated") && previous is not null &&
                    string.Equals(previous.MovementType.Code, movement.MovementType.Code, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.ReferenceCode, movement.ReferenceCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.MovementAddress?.Code, movement.MovementAddress?.Code, StringComparison.OrdinalIgnoreCase) &&
                    (ParseMovementDate(movement.MovementDate) - ParseMovementDate(previous.MovementDate)).TotalMinutes <= repeatWindow)
                {
                    issues.Add(AuditRow(movement, "repeated",
                        $"Aynı hareket {repeatWindow} dakika içinde yinelenmiş görünüyor."));
                }

                if (movement.MovementType.Direction == "entry")
                {
                    openEntries.Enqueue(movement);
                }
                else if (openEntries.Count == 0)
                {
                    if (auditKind is "all" or "exit_without_entry")
                        issues.Add(AuditRow(movement, "exit_without_entry",
                            "Seçilen dönem içinde bu çıkıştan önce eşleşen bir giriş bulunamadı."));
                }
                else
                {
                    openEntries.Dequeue();
                }
                previous = movement;
            }

            if (auditKind is "all" or "entry_without_exit")
                foreach (var entry in openEntries)
                    issues.Add(AuditRow(entry, "entry_without_exit",
                        "Seçilen dönem içinde bu girişten sonra eşleşen bir çıkış bulunamadı."));
        }
        var orderedIssues = issues.OrderByDescending(issue => ParseMovementDate(issue.MovementDate))
            .ThenByDescending(issue => issue.RecordId).Take(limit).ToArray();
        return new QrMovementAuditToolResult(
            orderedIssues,
            issues.Count,
            loaded.Movements.Count,
            loaded.Truncated,
            limit);
    }

    private async Task<QrAnalysisLoadResult> LoadAnalysisMovementsAsync(
        JsonElement parameters,
        int scanLimit,
        CancellationToken cancellationToken)
    {
        var searchParameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in AnalysisFilterNames)
            if (parameters.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                searchParameters[name] = value.Clone();
        searchParameters["include_summary"] = JsonSerializer.SerializeToElement(false);
        const int batchSize = 5_000;
        searchParameters["limit"] = JsonSerializer.SerializeToElement(batchSize);
        var movements = new List<QrMovementToolResult>();
        QrMovementCursorToolResult? cursor = null;
        var hasMore = true;
        while (hasMore && movements.Count < scanLimit)
        {
            searchParameters.Remove("before_movement_date");
            searchParameters.Remove("before_record_id");
            if (cursor is not null)
            {
                searchParameters["before_movement_date"] = JsonSerializer.SerializeToElement(cursor.BeforeMovementDate);
                searchParameters["before_record_id"] = JsonSerializer.SerializeToElement(cursor.BeforeRecordId);
            }
            var page = (QrMovementPageToolResult)await SearchMovementsAsync(
                JsonSerializer.SerializeToElement(searchParameters), cancellationToken, batchSize);
            movements.AddRange(page.Movements.Take(scanLimit - movements.Count));
            hasMore = page.HasMore;
            cursor = page.NextCursor;
            if (!hasMore || cursor is null || page.Movements.Count == 0) break;
        }
        return new QrAnalysisLoadResult(movements, hasMore);
    }

    private static (string Key, string Label) AggregateKey(QrMovementToolResult movement, string groupBy)
    {
        var date = ParseMovementDate(movement.MovementDate);
        return groupBy switch
        {
            "direction" => (movement.MovementType.Direction,
                movement.MovementType.Direction == "entry" ? "Giriş" : "Çıkış"),
            "barcode" => (movement.Barcode ?? "unknown", movement.MedicineName ?? movement.Barcode ?? "Barkodsuz"),
            "medicine" => (movement.Barcode ?? movement.MedicineName ?? "unknown", movement.MedicineName ?? movement.Barcode ?? "Bilinmeyen ürün"),
            "address" => (movement.MovementAddress?.Code ?? "unknown", movement.MovementAddress?.Name ?? movement.MovementAddress?.Code ?? "Adres yok"),
            "user" => (movement.User?.UserId ?? "unknown", movement.User?.Name ?? movement.User?.UserId ?? "Kullanıcı yok"),
            "day" => (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"))),
            "week" => (date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                $"{date.AddDays(-(((int)date.DayOfWeek + 6) % 7)):dd.MM.yyyy} haftası"),
            "hour" => (date.ToString("HH", CultureInfo.InvariantCulture), date.ToString("HH:00–HH:59", CultureInfo.InvariantCulture)),
            "weekday" => (((int)date.DayOfWeek).ToString(CultureInfo.InvariantCulture), date.ToString("dddd", CultureInfo.GetCultureInfo("tr-TR"))),
            "day_hour" => (date.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture), date.ToString("dd.MM.yyyy HH:00", CultureInfo.GetCultureInfo("tr-TR"))),
            _ => (movement.MovementType.Code, movement.MovementType.DisplayName)
        };
    }

    private static QrMovementAuditToolRow AuditRow(
        QrMovementToolResult movement,
        string issueType,
        string detail) => new(
        movement.RecordId,
        movement.QrCode,
        movement.Barcode,
        movement.MedicineName,
        issueType,
        movement.MovementDate,
        movement.MovementType.DisplayName,
        movement.MovementType.Direction,
        movement.MovementAddress?.Name ?? movement.MovementAddress?.Code,
        movement.ReferenceCode,
        detail);

    private static DateTime ParseMovementDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private static readonly string[] AnalysisFilterNames =
    [
        "start_date", "end_date", "movement_type_codes", "direction", "search", "search_kind",
        "barcodes", "user_id", "address_type", "address_code"
    ];

    private static IReadOnlyList<QrMovementTypeOption> SelectMovementTypes(
        IReadOnlyList<QrMovementTypeOption> available,
        IReadOnlyList<string>? requestedCodes,
        string direction)
    {
        if (requestedCodes is not null)
        {
            var knownCodes = available.Select(type => type.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownCodes = requestedCodes.Where(code => !knownCodes.Contains(code)).ToArray();
            if (unknownCodes.Length > 0)
                throw new AiToolValidationException(
                    $"Unknown movement_type_codes: {string.Join(", ", unknownCodes)}. " +
                    $"Use qr.list_movement_types to discover valid codes.");
        }

        var requestedSet = requestedCodes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = available.Where(type =>
                (requestedSet is null || requestedSet.Contains(type.Code)) &&
                (direction == "all" || type.IsEntry == (direction == "entry")))
            .ToArray();
        if (requestedCodes is not null && selected.Length != requestedCodes.Count)
            throw new AiToolValidationException(
                "One or more 'movement_type_codes' conflict with the requested 'direction'.");
        return selected;
    }

    private static QrMovementPageToolResult ToPageResult(QrMovementPage page, int limit)
    {
        var returnedItems = page.Items.Take(limit).ToArray();
        var movements = returnedItems.Select(ToMovement).ToArray();
        var last = returnedItems.LastOrDefault();
        var hasMore = page.Items.Count > limit;
        return new QrMovementPageToolResult(
            movements,
            page.Summary is null ? null : new QrMovementSummaryToolResult(
                page.Summary.TotalCount,
                page.Summary.DistinctQrCount,
                page.Summary.DistinctMedicineCount,
                page.Summary.EntryCount,
                page.Summary.ExitCount),
            limit,
            hasMore,
            hasMore && last is not null
                ? new QrMovementCursorToolResult(ToCursorDate(last.MovementDate), last.RecordId)
                : null);
    }

    private static QrMovementToolResult ToMovement(QrMovementItem movement) => new(
        movement.RecordId,
        movement.QrCode,
        Clean(movement.Barcode),
        Clean(movement.MedicineName),
        movement.ProductRecordId,
        new QrMovementTypeToolResult(
            movement.MovementTypeCode,
            movement.MovementTypeDisplay,
            movement.IsEntryDirection ? "entry" : "exit"),
        ToCursorDate(movement.MovementDate),
        string.IsNullOrWhiteSpace(movement.MovementAddress) ? null : new QrMovementAddressToolResult(
            movement.MovementAddress,
            Clean(movement.MovementAddressName),
            Clean(movement.MovementAddressEntityType),
            Clean(movement.MovementAddressRecordKey)),
        string.IsNullOrWhiteSpace(movement.UserId) ? null : new QrMovementUserToolResult(
            movement.UserId,
            Clean(movement.UserName)),
        Clean(movement.ReferenceCode),
        Clean(movement.Notes));

    private static DateOnly ReadDate(string value, string parameterName)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            throw new AiToolValidationException($"'{parameterName}' must use YYYY-MM-DD format.");
        return date;
    }

    private static DateTime? ReadOptionalCursorDate(string? value)
    {
        if (value is null) return null;
        if (!DateTime.TryParseExact(
                value,
                ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
            throw new AiToolValidationException(
                "'before_movement_date' must use local pharmacy time as YYYY-MM-DDTHH:mm:ss with optional fractional seconds.");
        return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
    }

    private static IReadOnlyList<string>? ReadOptionalStringArray(
        JsonElement parameters,
        string name,
        int minimumCount,
        int maximumCount)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
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
        if (distinct.Length < minimumCount || distinct.Length > maximumCount)
            throw new AiToolValidationException(
                $"Parameter '{name}' must contain between {minimumCount} and {maximumCount} distinct values.");
        return distinct;
    }

    private static string ValidateCode(string value, string parameterName)
        => ValidateText(value, parameterName, MaximumCodeLength);

    private static string ValidateText(string value, string parameterName, int maximumLength)
    {
        if (value.Length > maximumLength)
            throw new AiToolValidationException(
                $"Parameter '{parameterName}' cannot exceed {maximumLength} characters.");
        if (value.Any(char.IsControl))
            throw new AiToolValidationException($"Parameter '{parameterName}' cannot contain control characters.");
        return value;
    }

    private static string ToCursorDate(DateTime value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "—" ? null : value.Trim();

    private static IAiTool Tool(
        AiToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<object>> execute) =>
        new DelegateAiTool(definition, execute);

    private static AiToolDefinition SearchMovementsDefinition() => new(
        "qr.search_movements",
        "Searches QR/package movements with the same date, movement-type, direction, product, user, and movement-address rules as Karekod Hareketleri. Returns one keyset-paginated page and an optional full-filter summary.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["start_date"] = new("string", "Inclusive local pharmacy date in YYYY-MM-DD format.", Required: true),
            ["end_date"] = new("string", "Inclusive local pharmacy date in YYYY-MM-DD format; cannot be in the future.", Required: true),
            ["movement_type_codes"] = new("array", "Optional distinct qrMovementTypes codes; use qr.list_movement_types to discover them.", ItemsType: "string"),
            ["direction"] = new("string", "Optional entry/exit direction filter.", AllowedValues: ["all", "entry", "exit"], DefaultValue: "all"),
            ["search"] = new("string", "Search value; must be paired with search_kind."),
            ["search_kind"] = new("string", "How search is matched.", AllowedValues: ["qr_code", "barcode", "medicine_name_contains", "medicine_name_exact", "brand_name_exact"]),
            ["barcodes"] = new("array", "Optional 1-20 exact product barcodes; cannot be combined with search/search_kind.", ItemsType: "string"),
            ["user_id"] = new("string", "Exact movement user ID."),
            ["address_type"] = new("string", "Movement address entity type.", AllowedValues: ["all", "patient", "pharmacy", "depot"], DefaultValue: "all"),
            ["address_code"] = new("string", "Exact prefixed address code; requires a matching address_type (P patient, E pharmacy, D depot)."),
            ["before_movement_date"] = new("string", "Cursor date returned by a previous page; requires before_record_id."),
            ["before_record_id"] = new("integer", "Cursor record ID returned by a previous page; requires before_movement_date."),
            ["include_summary"] = new("boolean", "Whether to calculate totals across all matching movements.", DefaultValue: true),
            ["limit"] = new("integer", "Page size from 1 to 200.", DefaultValue: 50)
        },
        new Dictionary<string, string>
        {
            ["movements"] = "array of QR/package movement records",
            ["summary"] = "full-filter counts or null",
            ["limit"] = "integer",
            ["has_more"] = "boolean",
            ["next_cursor"] = "cursor object or null"
        },
        ["qr", "package", "movement", "history", "search", "stock_traceability"]);

    private static AiToolDefinition GetHistoryDefinition() => new(
        "qr.get_history",
        "Returns the all-time movement history for one exact QR/DataMatrix package code, or for a non-serialized cosmetic barcode stored in the QR column.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["qr_code"] = new("string", "Exact QR/DataMatrix code or stored cosmetic barcode.", Required: true),
            ["before_movement_date"] = new("string", "Cursor date returned by a previous page; requires before_record_id."),
            ["before_record_id"] = new("integer", "Cursor record ID returned by a previous page; requires before_movement_date."),
            ["limit"] = new("integer", "Page size from 1 to 200.", DefaultValue: 100)
        },
        new Dictionary<string, string>
        {
            ["found"] = "boolean",
            ["qr_code"] = "string",
            ["movements"] = "array of movement records",
            ["total_count"] = "integer",
            ["limit"] = "integer",
            ["has_more"] = "boolean",
            ["next_cursor"] = "cursor object or null"
        },
        ["qr", "package", "movement", "history", "traceability"]);

    private static AiToolDefinition ListMovementTypesDefinition() => new(
        "qr.list_movement_types",
        "Lists the configured QR movement type codes, Turkish display names, and entry/exit directions used by QR movement filters.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>(),
        new Dictionary<string, string> { ["movement_types"] = "array of movement type records" },
        ["qr", "movement", "type", "filter"]);

    private static AiToolDefinition AggregateMovementsDefinition()
    {
        var parameters = AnalysisFilterDefinitions();
        parameters["group_by"] = new("string", "Deterministic grouping dimension.",
            AllowedValues: ["movement_type", "direction", "barcode", "medicine", "address", "user", "day", "week", "hour", "weekday", "day_hour"],
            DefaultValue: "movement_type");
        parameters["missing_user_only"] = new("boolean", "When true, aggregates only movements without a recorded user.", DefaultValue: false);
        parameters["limit"] = new("integer", "Maximum returned groups from 1 to 500.", DefaultValue: 100);
        parameters["scan_limit"] = new("integer", "Maximum movement rows scanned from 1 to 50000; truncation is reported.", DefaultValue: 10000);
        return new AiToolDefinition(
            "qr.aggregate_movements",
            "Aggregates filtered QR/package movements by type, direction, product, address, user, day, week, or hour using the same authoritative movement query as Karekod Hareketleri.",
            AiToolRisks.ReadOnly,
            parameters,
            new Dictionary<string, string>
            {
                ["groups"] = "array of aggregate movement groups",
                ["total_group_count"] = "integer",
                ["scanned_movement_count"] = "integer",
                ["truncated"] = "boolean",
                ["limit"] = "integer"
            },
            ["qr", "movement", "aggregate", "distribution", "count", "analysis"]);
    }

    private static AiToolDefinition AuditMovementChainsDefinition()
    {
        var parameters = AnalysisFilterDefinitions();
        parameters["audit_kind"] = new("string", "Which read-only chain anomaly to report.",
            AllowedValues: ["all", "repeated", "exit_without_entry", "entry_without_exit", "missing_address"], DefaultValue: "all");
        parameters["repeat_window_minutes"] = new("integer", "Maximum minutes between otherwise identical movements considered repeated.", DefaultValue: 5);
        parameters["limit"] = new("integer", "Maximum returned issues from 1 to 500.", DefaultValue: 200);
        parameters["scan_limit"] = new("integer", "Maximum movement rows scanned from 1 to 50000; truncation is reported.", DefaultValue: 10000);
        return new AiToolDefinition(
            "qr.audit_movement_chains",
            "Performs a read-only audit of filtered QR movement chains for repeated movements, missing addresses, exits without entries, and entries without exits inside the selected period. Never modifies stock or QR records.",
            AiToolRisks.ReadOnly,
            parameters,
            new Dictionary<string, string>
            {
                ["issues"] = "array of movement-chain issues",
                ["total_issue_count"] = "integer",
                ["scanned_movement_count"] = "integer",
                ["truncated"] = "boolean",
                ["limit"] = "integer"
            },
            ["qr", "movement", "audit", "duplicate", "repeated", "unmatched", "consistency"]);
    }

    private static Dictionary<string, AiToolParameterDefinition> AnalysisFilterDefinitions() => new(StringComparer.Ordinal)
    {
        ["start_date"] = new("string", "Inclusive local pharmacy date in YYYY-MM-DD format.", Required: true),
        ["end_date"] = new("string", "Inclusive local pharmacy date in YYYY-MM-DD format.", Required: true),
        ["movement_type_codes"] = new("array", "Optional configured movement type codes.", ItemsType: "string"),
        ["direction"] = new("string", "Optional entry/exit direction filter.", AllowedValues: ["all", "entry", "exit"], DefaultValue: "all"),
        ["search"] = new("string", "Search value paired with search_kind."),
        ["search_kind"] = new("string", "How search is matched.", AllowedValues: ["qr_code", "barcode", "medicine_name_contains", "medicine_name_exact", "brand_name_exact"]),
        ["barcodes"] = new("array", "Optional 1-20 exact product barcodes.", ItemsType: "string"),
        ["user_id"] = new("string", "Exact movement user ID."),
        ["address_type"] = new("string", "Movement address entity type.", AllowedValues: ["all", "patient", "pharmacy", "depot"], DefaultValue: "all"),
        ["address_code"] = new("string", "Exact prefixed movement address code.")
    };

    private sealed class DelegateAiTool(
        AiToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<object>> execute) : IAiTool
    {
        public AiToolDefinition Definition { get; } = definition;

        public Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default) =>
            execute(parameters, cancellationToken);
    }
}

public sealed record QrMovementTypeToolResult(string Code, string DisplayName, string Direction);

public sealed record QrMovementAddressToolResult(
    string Code,
    string? Name,
    string? EntityType,
    string? RecordKey);

public sealed record QrMovementUserToolResult(string UserId, string? Name);

public sealed record QrMovementToolResult(
    int RecordId,
    string QrCode,
    string? Barcode,
    string? MedicineName,
    int? ProductRecordId,
    QrMovementTypeToolResult MovementType,
    string MovementDate,
    QrMovementAddressToolResult? MovementAddress,
    QrMovementUserToolResult? User,
    string? ReferenceCode,
    string? Notes);

public sealed record QrMovementSummaryToolResult(
    long TotalCount,
    long DistinctQrCount,
    long DistinctBarcodeCount,
    long EntryCount,
    long ExitCount);

public sealed record QrMovementCursorToolResult(string BeforeMovementDate, int BeforeRecordId);

public sealed record QrMovementPageToolResult(
    IReadOnlyList<QrMovementToolResult> Movements,
    QrMovementSummaryToolResult? Summary,
    int Limit,
    bool HasMore,
    QrMovementCursorToolResult? NextCursor);

public sealed record QrHistoryToolResult(
    bool Found,
    string QrCode,
    IReadOnlyList<QrMovementToolResult> Movements,
    long TotalCount,
    int Limit,
    bool HasMore,
    QrMovementCursorToolResult? NextCursor);

public sealed record QrMovementTypesToolResult(IReadOnlyList<QrMovementTypeToolResult> MovementTypes);

public sealed record QrMovementAggregateToolRow(
    string GroupKey,
    string GroupLabel,
    int MovementCount,
    int EntryCount,
    int ExitCount,
    int DistinctQrCount);

public sealed record QrMovementAggregateToolResult(
    IReadOnlyList<QrMovementAggregateToolRow> Groups,
    int TotalGroupCount,
    int ScannedMovementCount,
    bool Truncated,
    int Limit);

public sealed record QrMovementAuditToolRow(
    int RecordId,
    string QrCode,
    string? Barcode,
    string? MedicineName,
    string IssueType,
    string MovementDate,
    string MovementType,
    string Direction,
    string? MovementAddress,
    string? ReferenceCode,
    string Detail);

public sealed record QrMovementAuditToolResult(
    IReadOnlyList<QrMovementAuditToolRow> Issues,
    int TotalIssueCount,
    int ScannedMovementCount,
    bool Truncated,
    int Limit);

internal sealed record QrAnalysisLoadResult(
    IReadOnlyList<QrMovementToolResult> Movements,
    bool Truncated);
