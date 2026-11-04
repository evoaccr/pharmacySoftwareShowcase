using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pharmacyApp.AI.Workflows;

/// <summary>Typed, local operations over retained result rows; never queries a database or invokes a model.</summary>
public sealed class PreviousResultWorkflow : IAiWorkflow
{
    public const string WorkflowName = "onceki_sonucu_isle";
    public AiWorkflowDefinition Definition { get; } = new(
        WorkflowName, "Önceki sonucu işle",
        "Filters, sorts, projects or aggregates exact retained result rows from any domain without a new database query; never substitutes all database records for displayed rows.",
        Parameters: new Dictionary<string, string>
        {
            ["source_workflow"] = "Optional exact recent dataset ID; omitted means the latest displayed dataset, never an older matching dataset.",
            ["filters_json"] = "JSON array [{field,op,value}], AND; op: eq,ne,contains,gt,gte,lt,lte,is_empty,not_empty. Use exact schema fields and stored values.",
            ["metrics_json"] = "JSON array [{field,op,alias,label}], op: sum,average,min,max,count,distinct_count; label is English, alias is a unique snake_case key. count needs no field. Missing numeric values are not zero.",
            ["group_by"] = "Optional comma-separated exact fields; requires metrics_json. Aggregates after filtering and before limit.",
            ["sort_by"] = "Optional exact result field; numeric values sort numerically, ISO dates chronologically.",
            ["sort_direction"] = "asc or desc; default asc.",
            ["limit"] = "Optional positive number of output rows; omit to keep all retained matching rows.",
            ["columns"] = "Optional visible field names separated by commas; hidden IDs remain available for row actions.",
            ["resolve_entity"] = "Optional patient,medicine,prescription: promote exactly one grounded entity from filtered rows for the next workflow. Multiple/missing identities require clarification; never infer an ID from a name. Cannot combine with metrics_json."
        });

    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        try { return Task.FromResult(Execute(context, cancellationToken)); }
        catch (ResultValidationException exception) { return Task.FromResult(WorkflowData.Failed(exception.Message)); }
        catch (JsonException) { return Task.FromResult(WorkflowData.Failed("Önceki sonuç için istenen filtre veya hesaplama anlaşılmadı. Lütfen görmek istediğiniz bilgiyi belirtin.")); }
        catch (OverflowException) { return Task.FromResult(WorkflowData.Failed("İstenen hesaplama güvenilir biçimde tamamlanamadı; sayısal değerler hesaplama sınırını aşıyor.")); }
    }

    private static AiWorkflowStepResult Execute(AiWorkflowContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PreviousResultData.TryGetSource(context.Outputs, WorkflowData.Parameter(context, "source_workflow"), out var sourceName, out var source))
            throw new ResultValidationException("Bu konuşmada işlenebilecek önceki bir sonuç bulunamadı. Önce ilgili kayıtları listeleyin.");
        var original = source.GetProperty("rows").EnumerateArray()
            .Select(row => row.ValueKind == JsonValueKind.Object ? WorkflowData.FlatRow(row) : throw new ResultValidationException("Önceki sonuç satırları bu işlem için uygun değil."))
            .ToArray();
        var fields = original.SelectMany(row => row.Keys).Concat(PreviousResultData.MetadataStrings(source, "columns")).ToHashSet(StringComparer.Ordinal);
        var filters = ReadDescriptors(context, "filters_json", 12);
        var metrics = ReadDescriptors(context, "metrics_json", 8);
        var groupBy = ReadFields(context, "group_by", fields, 4);
        var labels = PreviousResultData.ReadLabels(source);
        foreach (var filter in filters) ValidateFilter(filter, fields);
        var aliases = new HashSet<string>(groupBy, StringComparer.Ordinal);
        foreach (var metric in metrics) ValidateMetric(metric, fields, aliases, labels);
        if (groupBy.Length > 0 && metrics.Length == 0)
            throw new ResultValidationException("Gruplama için hangi bilginin hesaplanacağını belirtin; örneğin ürün adedi toplamı.");
        var filtered = new List<IReadOnlyDictionary<string, string>>();
        foreach (var row in original)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filters.All(filter => Matches(row, filter))) filtered.Add(row);
        }
        var entity = WorkflowData.Parameter(context, "resolve_entity")?.Trim();
        if (!string.IsNullOrEmpty(entity) && metrics.Length > 0)
            throw new ResultValidationException("Bir kaydı seçme ve toplu hesaplama aynı adımda birleştirilemedi. Önce ilgili kaydı netleştirin.");
        var resolved = string.IsNullOrEmpty(entity) ? new Dictionary<string, object?>() : ResolveEntity(filtered, entity);
        IReadOnlyList<IReadOnlyDictionary<string, string>> result = filtered;
        if (metrics.Length > 0)
        {
            // A JSON tuple avoids collisions between compound group values containing delimiters.
            var groups = filtered.GroupBy(row => JsonSerializer.Serialize(groupBy.Select(key => row.GetValueOrDefault(key))));
            var aggregateRows = new List<IReadOnlyDictionary<string, string>>();
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = group.ToArray();
                var aggregate = groupBy.ToDictionary(key => key, key => rows[0].GetValueOrDefault(key) ?? string.Empty, StringComparer.Ordinal);
                foreach (var metric in metrics) aggregate[Text(metric, "alias")] = Calculate(rows, metric);
                aggregateRows.Add(aggregate);
            }
            // A count of an empty result is known; a missing average/minimum/maximum is not zero.
            if (filtered.Count == 0 && groupBy.Length == 0 && metrics.All(metric => Text(metric, "op") == "count"))
                aggregateRows.Add(metrics.ToDictionary(metric => Text(metric, "alias"), _ => "0", StringComparer.Ordinal));
            result = aggregateRows;
            fields = groupBy.Concat(metrics.Select(metric => Text(metric, "alias"))).ToHashSet(StringComparer.Ordinal);
        }
        var sort = WorkflowData.Parameter(context, "sort_by")?.Trim();
        var direction = WorkflowData.Parameter(context, "sort_direction") ?? "asc";
        if (direction is not ("asc" or "desc")) throw new ResultValidationException("Sıralama yönü anlaşılamadı; artan veya azalan sıralama belirtin.");
        if (!string.IsNullOrEmpty(sort))
        {
            RequireField(sort, fields);
            var comparer = new ResultCellComparer();
            result = direction == "desc"
                ? result.OrderByDescending(row => row.GetValueOrDefault(sort), comparer).ToArray()
                : result.OrderBy(row => row.GetValueOrDefault(sort), comparer).ToArray();
        }
        var matchingCount = result.Count;
        if (WorkflowData.Parameter(context, "limit") is { } rawLimit)
        {
            if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
                throw new ResultValidationException("Gösterilecek kayıt sayısı pozitif bir tam sayı olmalı.");
            result = result.Take(limit).ToArray();
        }
        var requestedVisible = ReadFields(context, "columns", fields, 40);
        var inheritedVisible = PreviousResultData.MetadataStrings(source, "visible_columns").Where(fields.Contains).ToArray();
        var visible = requestedVisible.Length > 0 ? requestedVisible
            : metrics.Length == 0 && inheritedVisible.Length > 0 ? inheritedVisible : fields.ToArray();
        var scope = $"Önceki yanıtta gösterilen {original.Length:N0} kayıt esas alındı; yeni bir veritabanı sorgusu yapılmadı.";
        var message = result.Count == 0
            ? $"Önceki sonuçta bu koşullara uyan kayıt bulunamadı. {scope}"
            : metrics.Length > 0
                ? $"{filtered.Count:N0} kayıt üzerinden {result.Count:N0} özet hazırlandı. {scope}"
                : $"Önceki sonuçtan {matchingCount:N0} kayıt eşleşti; {result.Count:N0} kayıt gösteriliyor. {scope}";
        var metadata = new Dictionary<string, object?>
        {
            ["source_workflow"] = sourceName, ["source_row_count"] = original.Length,
            ["matched_row_count"] = filtered.Count, ["total_count"] = matchingCount,
            ["columns"] = fields, ["visible_columns"] = visible, ["column_labels"] = labels,
            ["scope"] = "retained_displayed_rows", ["aggregated"] = metrics.Length > 0
        };
        // Preserve provenance and domain interaction styling, but never claim a new database total.
        if (source.TryGetProperty("metadata", out var sourceMetadata) && sourceMetadata.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "start_date", "end_date", "row_background_field" })
                if (sourceMetadata.TryGetProperty(key, out var value)) metadata[key] = value.Clone();
        resolved["rows"] = result;
        resolved["metadata"] = metadata;
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message, JsonSerializer.SerializeToElement(resolved));
    }

    private static Dictionary<string, object?> ResolveEntity(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string entity)
    {
        if (entity is not ("patient" or "medicine" or "prescription"))
            throw new ResultValidationException("Önceki sonuçtan hasta, ilaç veya reçete seçilebilir.");
        var label = entity == "patient" ? "hasta" : entity == "medicine" ? "ilaç" : "reçete";
        if (rows.Count == 0) throw new ResultValidationException($"Önceki sonuçta koşullara uyan {label} bulunamadı.");
        string Identity(IReadOnlyDictionary<string, string> row) => entity switch
        {
            "patient" => int.TryParse(row.GetValueOrDefault("patient_record_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : string.Empty,
            "medicine" => row.GetValueOrDefault("barcode")?.Trim() ?? string.Empty,
            _ => (row.GetValueOrDefault("prescription_number") ?? row.GetValueOrDefault("prescription_no"))?.Trim() ?? string.Empty
        };
        var identities = rows.Select(Identity).Distinct(StringComparer.Ordinal).ToArray();
        if (identities.Any(string.IsNullOrEmpty))
            throw new ResultValidationException($"Önceki sonuçta bu {label} için doğrulanmış kayıt bilgisi bulunmuyor. Adını veya numarasını belirterek yeniden seçin.");
        if (identities.Length != 1)
            throw new ResultValidationException($"Önceki sonuçta koşullara uyan {identities.Length:N0} farklı {label} var. Hangisini kastettiğinizi belirterek seçimi daraltın.");
        var selected = rows[0];
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["resolved_entity"] = entity };
        if (entity == "patient")
        {
            var id = int.Parse(identities[0], CultureInfo.InvariantCulture);
            var names = rows.Select(row => row.GetValueOrDefault("patient_name")?.Trim() ?? string.Empty).Where(name => name.Length > 0)
                .Distinct(StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true)).ToArray();
            var tcs = rows.Select(row => row.GetValueOrDefault("patient_tc")?.Trim() ?? string.Empty).Where(tc => tc.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length > 1 || tcs.Length > 1)
                throw new ResultValidationException("Önceki sonuçta hasta bilgileri birbiriyle uyuşmuyor. Hastayı yeniden seçin.");
            result["patient_record_id"] = id;
            result["selected_patient"] = new { patient_record_id = id, name = names.FirstOrDefault() ?? string.Empty, patient_tc = tcs.FirstOrDefault() ?? string.Empty };
        }
        else if (entity == "medicine")
        {
            result["barcodes"] = identities;
            result["selected_products"] = new[] { new { barcode = identities[0], name = selected.GetValueOrDefault("medicine_name") ?? selected.GetValueOrDefault("product_name") ?? string.Empty } };
        }
        else
        {
            result["prescription_number"] = identities[0];
            foreach (var field in new[] { "electronic_prescription_number", "patient_name", "patient_tc", "prescription_uid", "version_number" })
                if (selected.TryGetValue(field, out var value)) result[field] = value;
        }
        return result;
    }

    private static JsonElement[] ReadDescriptors(AiWorkflowContext context, string name, int maximum)
    {
        var text = WorkflowData.Parameter(context, name);
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Length > 12000) throw new ResultValidationException("Bu işlemde çok fazla filtre veya hesaplama istendi. Daha az koşulla deneyin.");
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > maximum ||
            document.RootElement.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object))
            throw new ResultValidationException("Önceki sonuç için filtre veya hesaplama seçenekleri doğrulanamadı.");
        var allowed = name == "filters_json" ? new[] { "field", "op", "value" } : new[] { "field", "op", "alias", "label" };
        foreach (var descriptor in document.RootElement.EnumerateArray())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in descriptor.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name) ||
                    property.Name != "value" && property.Value.ValueKind != JsonValueKind.String)
                    throw new ResultValidationException("Önceki sonuç için filtre veya hesaplama seçenekleri doğrulanamadı; desteklenmeyen koşullar uygulanmadı.");
        }
        return document.RootElement.EnumerateArray().Select(value => value.Clone()).ToArray();
    }

    private static string[] ReadFields(AiWorkflowContext context, string name, IReadOnlySet<string> fields, int maximum)
    {
        var requested = (WorkflowData.Parameter(context, name) ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > maximum) throw new ResultValidationException("Bir defada daha az bilgi alanı seçin.");
        foreach (var key in requested) RequireField(key, fields);
        return requested;
    }

    private static void RequireField(string field, IReadOnlySet<string> fields)
    {
        if (!fields.Contains(field)) throw new ResultValidationException("İstenen bilgi önceki sonuçta yer almıyor. Bu bilgiyi içeren kayıtların ayrıca getirilmesi gerekiyor.");
    }

    private static void ValidateFilter(JsonElement filter, IReadOnlySet<string> fields)
    {
        RequireField(Text(filter, "field"), fields);
        var op = Text(filter, "op");
        if (op is not ("eq" or "ne" or "contains" or "gt" or "gte" or "lt" or "lte" or "is_empty" or "not_empty"))
            throw new ResultValidationException("İstenen filtre türü desteklenmiyor. Eşitlik, metin içerme veya sayısal karşılaştırma kullanın.");
        if (op is not ("is_empty" or "not_empty") &&
            (!filter.TryGetProperty("value", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array))
            throw new ResultValidationException("Filtre için karşılaştırılacak değeri belirtin.");
        if (op is "gt" or "gte" or "lt" or "lte" && !PreviousResultData.TryDecimal(Text(filter, "value"), out _))
            throw new ResultValidationException("Sayısal filtrede geçerli bir sayı gerekiyor.");
    }

    private static bool Matches(IReadOnlyDictionary<string, string> row, JsonElement filter)
    {
        var value = row.GetValueOrDefault(Text(filter, "field"));
        var target = Text(filter, "value");
        var op = Text(filter, "op");
        var empty = string.IsNullOrWhiteSpace(value) || value is "null";
        if (op == "is_empty") return empty;
        if (op == "not_empty") return !empty;
        if (empty) return false;
        var culture = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;
        if (op == "contains") return culture.IndexOf(value!, target, CompareOptions.IgnoreCase) >= 0;
        if (op is "eq" or "ne")
        {
            var equal = filter.TryGetProperty("value", out var targetValue) && targetValue.ValueKind == JsonValueKind.Number
                ? PreviousResultData.TryDecimal(value, out var numericValue) && PreviousResultData.TryDecimal(target, out var numericTarget) && numericValue == numericTarget
                : culture.Compare(value, target, CompareOptions.IgnoreCase) == 0;
            return op == "eq" ? equal : !equal;
        }
        if (!PreviousResultData.TryDecimal(value, out var left)) return false;
        PreviousResultData.TryDecimal(target, out var right);
        return op switch { "gt" => left > right, "gte" => left >= right, "lt" => left < right, "lte" => left <= right, _ => false };
    }

    private static void ValidateMetric(JsonElement metric, IReadOnlySet<string> fields, ISet<string> aliases, IDictionary<string, string> labels)
    {
        var op = Text(metric, "op");
        if (op is not ("sum" or "average" or "min" or "max" or "count" or "distinct_count"))
            throw new ResultValidationException("İstenen hesaplama desteklenmiyor. Toplam, ortalama, en az, en çok veya kayıt sayısı isteyin.");
        if (op != "count") RequireField(Text(metric, "field"), fields);
        var alias = Text(metric, "alias");
        var label = Text(metric, "label");
        if (!Regex.IsMatch(alias, "^[a-z][a-z0-9_]{0,59}$", RegexOptions.CultureInvariant) || !aliases.Add(alias) || label.Length is < 1 or > 100)
            throw new ResultValidationException("Hesaplama başlıkları doğrulanamadı. Her hesaplama için farklı ve açıklayıcı bir başlık gerekiyor.");
        labels[alias] = label;
    }

    private static string Calculate(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, JsonElement metric)
    {
        var op = Text(metric, "op");
        if (op == "count") return rows.Count.ToString(CultureInfo.InvariantCulture);
        var cells = rows.Select(row => row.GetValueOrDefault(Text(metric, "field"))).ToArray();
        if (op == "distinct_count") return cells.Where(value => !string.IsNullOrWhiteSpace(value) && value != "null")
            .Distinct(StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true)).Count().ToString(CultureInfo.InvariantCulture);
        var values = new List<decimal>();
        foreach (var cell in cells)
        {
            if (!PreviousResultData.TryDecimal(cell, out var number))
                throw new ResultValidationException("İstenen hesaplama tamamlanamadı: bazı kayıtlarda sayısal bilgi eksik veya geçersiz. Bu değerler sıfır kabul edilmedi.");
            values.Add(number);
        }
        var calculated = op switch { "sum" => values.Sum(), "average" => values.Average(), "min" => values.Min(), "max" => values.Max(), _ => throw new InvalidOperationException() };
        return calculated.ToString(CultureInfo.InvariantCulture);
    }

    private static string Text(JsonElement value, string name) => WorkflowData.Text(value, name);
    private sealed class ResultValidationException(string message) : Exception(message);
    private sealed class ResultCellComparer : IComparer<string?>
    {
        public int Compare(string? left, string? right)
        {
            if (PreviousResultData.TryDecimal(left, out var a) && PreviousResultData.TryDecimal(right, out var b)) return a.CompareTo(b);
            if (DateTime.TryParseExact(left, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var da) &&
                DateTime.TryParseExact(right, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var db)) return da.CompareTo(db);
            return string.Compare(left, right, true, CultureInfo.GetCultureInfo("tr-TR"));
        }
    }
}

/// <summary>Shared snapshot contract: complete rows stay local; only bounded schema and samples reach the router.</summary>
public static class PreviousResultData
{
    public const string ContextKey = "previous_result_context";
    public static IReadOnlyList<string> AncestorKeys { get; } =
        ["retained_result_source_1", "retained_result_source_2", "retained_result_source_3"];

    public static bool TryGetSource(IReadOnlyDictionary<string, JsonElement> outputs, string? requested, out string sourceName, out JsonElement source)
    {
        var candidates = string.IsNullOrWhiteSpace(requested)
            ? outputs.ContainsKey(ContextKey) ? outputs.Where(pair => pair.Key == ContextKey) : outputs.Reverse()
            : outputs.Where(pair => pair.Key == requested.Trim());
        foreach (var pair in candidates)
            if (pair.Value.ValueKind == JsonValueKind.Object && pair.Value.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                sourceName = pair.Key;
                source = pair.Value;
                if (pair.Key == ContextKey && pair.Value.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                    metadata.TryGetProperty("source_workflow", out var name) && name.ValueKind == JsonValueKind.String)
                    sourceName = name.GetString() ?? pair.Key;
                return true;
            }
        sourceName = string.Empty;
        source = default;
        return false;
    }

    public static object BuildPreview(string sourceName, JsonElement source, int maximumRows = 8)
    {
        var rows = source.GetProperty("rows").EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object).ToArray();
        var labels = ReadLabels(source);
        var fields = MetadataStrings(source, "columns").Concat(rows.SelectMany(row => row.EnumerateObject().Select(property => property.Name))).Distinct(StringComparer.Ordinal).ToArray();
        var schema = fields.Take(40).Select(field => new { field, label = labels.GetValueOrDefault(field) ?? WorkflowData.Label(field) }).ToArray();
        var visible = MetadataStrings(source, "visible_columns");
        string[] identities = ["patient_name", "patient_record_id", "medicine_name", "product_name", "barcode", "reference_code", "prescription_number", "prescription_no"];
        var previewFields = identities.Where(fields.Contains).Concat(visible).Concat(fields).Distinct(StringComparer.Ordinal).Take(12).ToHashSet(StringComparer.Ordinal);
        var preview = new List<Dictionary<string, string>>();
        var remainingCharacters = 5000;
        foreach (var row in rows.Take(Math.Clamp(maximumRows, 0, 20)))
        {
            var compact = row.EnumerateObject()
                .Where(property => previewFields.Contains(property.Name) && property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                .ToDictionary(property => property.Name, property => Truncate(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText()), StringComparer.Ordinal);
            var length = JsonSerializer.Serialize(compact).Length;
            if (length > remainingCharacters) break;
            preview.Add(compact);
            remainingCharacters -= length;
        }
        return new { source_workflow = sourceName, scope = "retained_displayed_rows", row_count = rows.Length, preview_row_count = preview.Count,
            preview_truncated = preview.Count < rows.Length, columns = schema, schema_truncated = fields.Length > schema.Length, rows = preview };
    }

    public static string[] MetadataStrings(JsonElement source, string key) =>
        source.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];

    public static Dictionary<string, string> ReadLabels(JsonElement source) =>
        source.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("column_labels", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String).ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    public static bool TryDecimal(string? text, out decimal number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = Regex.Replace(text.Trim(), @"^(?:₺\s*)|(?:\s*(?:₺|TL))$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        if (Regex.IsMatch(clean, @"^[+-]?\d+(?:\.\d+)?$", RegexOptions.CultureInvariant))
            return decimal.TryParse(clean, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        if (Regex.IsMatch(clean, @"^[+-]?(?:\d+|\d{1,3}(?:\.\d{3})+),\d+$", RegexOptions.CultureInvariant))
            return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out number);
        if (Regex.IsMatch(clean, @"^[+-]?\d{1,3}(?:,\d{3})+\.\d+$", RegexOptions.CultureInvariant))
            return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
        return false;
    }

    private static string Truncate(string value) => value.Length <= 160 ? value : value[..157] + "…";
}
