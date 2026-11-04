using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Shared;

namespace pharmacyApp.AI.Tools.Sale;

/// <summary>
/// Discoverable AI tools for the Retail Sale table and its payment validation.
/// These tools calculate and validate drafts; they never complete a sale or perform stock/ITS writes.
/// </summary>
public sealed class SaleAiToolCatalog : IAiToolProvider
{
    private const decimal MaximumMoney = 1_000_000_000m;
    private static readonly string[] RecordFilterParameterNames =
    [
        "start_date", "end_date", "sale_types", "include_refunds", "record_types", "barcodes", "qr_codes",
        "payment_types", "statuses", "patient_record_id", "user_id", "prescription_no",
        "is_debted", "marked_only", "minimum_total", "maximum_total", "start_time", "end_time", "patient_tc", "has_prescription"
    ];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RetailSaleTableService tableService;
    private readonly IRetailSaleRecordQueryService recordQueries;
    private readonly PatientFunctions patients;
    private readonly IReadOnlyDictionary<string, IAiTool> tools;

    public SaleAiToolCatalog(
        RetailSaleTableService? tableService = null,
        PatientFunctions? patients = null,
        IRetailSaleRecordQueryService? recordQueries = null)
    {
        this.tableService = tableService ?? new RetailSaleTableService();
        this.patients = patients ?? PharmacyFunctions.Patients;
        this.recordQueries = recordQueries ?? new RetailSaleRecordQueryService();
        var registered = new IAiTool[]
        {
            Tool(ResolveItemDefinition(), ResolveItemAsync),
            Tool(PreviewTableDefinition(), PreviewTableAsync),
            Tool(CalculatePaymentSplitDefinition(), CalculatePaymentSplitAsync),
            Tool(ValidateDraftDefinition(), ValidateDraftAsync),
            Tool(SearchPatientsDefinition(), SearchPatientsAsync),
            Tool(SearchRecordsDefinition(), SearchRecordsAsync),
            Tool(AggregateRecordsDefinition(), AggregateRecordsAsync),
            Tool(GetRecordDefinition(), GetRecordAsync)
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
            throw new KeyNotFoundException($"Unknown Sale AI tool '{toolName}'.");
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

    private async Task<object> ResolveItemAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("entered_code", "sale_mode");
        var enteredCode = reader.RequireString("entered_code");
        var mode = ParseSaleMode(reader.EnumValue("sale_mode", "retail", "retail", "consignment"));
        var item = await tableService.ResolveItemAsync(enteredCode, cancellationToken);
        var check = RetailSaleTableRules.CheckCanAdd(new RetailSaleCart(), item, mode);
        return new SaleResolvedItemToolResult(
            item is not null,
            check.CanAdd,
            check.IssueCode,
            check.Message,
            item is null ? null : ToItem(item));
    }

    private async Task<object> SearchRecordsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(RecordFilterParameterNames
            .Concat(["offset", "limit", "include_lines", "sort_direction"]).ToArray());
        var filter = ReadRecordFilter(reader);
        var offset = reader.OptionalInteger("offset", 0, 0, 1_000_000);
        var limit = reader.OptionalInteger("limit", 100, 1, 200);
        var includeLines = reader.OptionalBoolean("include_lines", true);
        var newestFirst = reader.EnumValue("sort_direction", "newest_first", "newest_first", "oldest_first") == "newest_first";
        var page = await recordQueries.SearchAsync(
            new RetailSaleRecordSearchQuery(filter, offset, limit, includeLines, newestFirst), cancellationToken);
        return new SaleRecordSearchToolResult(
            page.Records, page.TotalCount, page.Offset, page.Limit, page.Offset + page.Records.Count < page.TotalCount);
    }

    private async Task<object> SearchPatientsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("query", "limit");
        var query = reader.RequireString("query");
        var limit = reader.OptionalInteger("limit", 20, 1, 200);
        var matches = await patients.SearchAsync(query, cancellationToken, limit);
        return new
        {
            query,
            patients = matches.Select(patient => new
            {
                patient_record_id = patient.RecordId,
                patient_tc = patient.TcIdentityNo,
                name = patient.DisplayName,
                phone_number = patient.PhoneNumber
            }).ToArray()
        };
    }

    private async Task<object> GetRecordAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("reference_code");
        var referenceCode = reader.RequireString("reference_code").ToUpperInvariant();
        if (referenceCode.Length < 2 || referenceCode[0] is not ('P' or 'G') ||
            !int.TryParse(referenceCode[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new AiToolValidationException("'reference_code' must be P<sale_id> or G<refund_id>.");
        var record = await recordQueries.GetAsync(referenceCode, cancellationToken);
        return new SaleRecordGetToolResult(record is not null, record);
    }

    private async Task<object> AggregateRecordsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(RecordFilterParameterNames
            .Concat(["group_by", "sort_by", "sort_direction", "offset", "limit"]).ToArray());
        var filter = ReadRecordFilter(reader);
        var groupBy = reader.EnumValue(
            "group_by", "none", "none", "day", "month", "sale_type", "record_type", "product", "user", "patient");
        var sortBy = reader.EnumValue(
            "sort_by", "net_revenue", "net_revenue", "gross_revenue", "institution_revenue", "discount_total", "profit", "quantity", "record_count", "group_key");
        var descending = reader.EnumValue("sort_direction", "descending", "descending", "ascending") == "descending";
        var offset = reader.OptionalInteger("offset", 0, 0, 1_000_000);
        var limit = reader.OptionalInteger("limit", 100, 1, 200);
        var page = await recordQueries.AggregateAsync(
            new RetailSaleAggregateQuery(filter, groupBy, offset, limit, sortBy, descending), cancellationToken);
        return new SaleRecordAggregateToolResult(
            page.Groups, page.TotalCount, page.Offset, page.Limit, page.Offset + page.Groups.Count < page.TotalCount);
    }

    private async Task<object> PreviewTableAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(
            "entered_codes", "sale_mode", "discount_mode", "discount_amount", "line_discounts");
        var draft = ReadDraft(reader);
        var result = await tableService.BuildAsync(
            draft.EnteredCodes,
            draft.SaleMode,
            draft.DistributedDiscount,
            draft.LineDiscounts,
            cancellationToken);
        return ToPreview(result);
    }

    private Task<object> CalculatePaymentSplitAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(
            "total_amount", "selected_payment_types", "edited_payment_type",
            "cash_amount", "card_amount", "bank_transfer_amount", "debt_amount", "discount_amount");
        var totalAmount = reader.OptionalDecimal("total_amount", 0m, 0m, MaximumMoney);
        var selectedNames = reader.RequireStringArray("selected_payment_types", 1, 5);
        var selectedKinds = selectedNames.Select(ParsePaymentKind).ToArray();
        var editedName = reader.OptionalString("edited_payment_type");
        RetailPaymentKind? editedKind = editedName is null ? null : ParsePaymentKind(editedName);
        if (editedKind is not null && !selectedKinds.Contains(editedKind.Value))
            throw new AiToolValidationException("'edited_payment_type' must also be selected.");

        var amounts = ReadPaymentAmounts(reader);
        RetailPaymentBalancer.Rebalance(totalAmount, selectedKinds, amounts, editedKind);
        return Task.FromResult<object>(ToPaymentSplit(totalAmount, selectedKinds, amounts));
    }

    private async Task<object> ValidateDraftAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(
            "entered_codes", "sale_mode", "discount_mode", "discount_amount", "line_discounts",
            "patient_record_id", "cash_amount", "card_amount", "bank_transfer_amount", "debt_amount");
        var draft = ReadDraft(reader);
        var patientRecordId = reader.OptionalNullableInteger("patient_record_id", 1, int.MaxValue);
        var build = await tableService.BuildAsync(
            draft.EnteredCodes,
            draft.SaleMode,
            draft.DistributedDiscount,
            draft.LineDiscounts,
            cancellationToken);
        var preview = ToPreview(build);
        var amounts = ReadPaymentAmounts(reader);
        amounts[RetailPaymentKind.Discount] = build.Cart.TotalDiscount;
        var payments = amounts
            .Where(pair => pair.Value != 0m)
            .Select(pair => new RetailSalePaymentInput(pair.Key, pair.Value))
            .ToArray();
        var errors = build.Issues
            .Select(issue => new SaleDraftValidationError(issue.Code, issue.Message))
            .ToList();
        var debtAmount = 0m;

        if (build.Cart.ItemCount == 0)
            errors.Add(new SaleDraftValidationError("empty_table", "Satış tablosunda en az bir ürün bulunmalıdır."));
        if (errors.Count == 0)
        {
            try
            {
                var lines = build.Cart.Groups.SelectMany(group => group.Entries)
                    .Select(entry => new RetailSaleLineInput(entry.Item, entry.DiscountAmount))
                    .ToArray();
                RetailSaleRules.ValidateLineDiscounts(
                    lines,
                    build.Cart.TotalDiscount,
                    draft.DiscountMode != "none");
                debtAmount = RetailSaleRules.ValidatePayments(
                    build.Cart.TotalAmount,
                    payments,
                    draft.DiscountMode != "none",
                    patientRecordId);
                if (draft.SaleMode != RetailSaleMode.Retail && patientRecordId is null)
                    throw new InvalidOperationException("Emanet satış için hasta seçilmelidir.");
                if (patientRecordId is not null &&
                    await patients.GetByRecordIdAsync(patientRecordId.Value, cancellationToken) is null)
                    errors.Add(new SaleDraftValidationError("patient_not_found", "Hasta kaydı bulunamadı."));
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(new SaleDraftValidationError("validation_failed", exception.Message));
            }
        }

        var paymentResults = amounts.Select(pair => new SalePaymentToolResult(ToPaymentName(pair.Key), pair.Value)).ToArray();
        return new SaleDraftValidationToolResult(
            errors.Count == 0,
            preview,
            paymentResults,
            decimal.Round(amounts.Values.Sum(), 2),
            debtAmount,
            patientRecordId,
            errors);
    }

    private static RetailSaleRecordFilter ReadRecordFilter(AiToolParameterReader reader)
    {
        var start = ParseDate(reader.RequireString("start_date"), "start_date");
        var end = ParseDate(reader.RequireString("end_date"), "end_date");
        if (end < start)
            throw new AiToolValidationException("'end_date' must be on or after 'start_date'.");
        if (end.DayNumber - start.DayNumber > 3_653)
            throw new AiToolValidationException("The inclusive date range cannot exceed 10 years.");

        var saleTypes = NormalizeAllowedArray(
            reader.OptionalStringArray("sale_types", 2), ["retail", "prescription"], "sale_types");
        if (saleTypes.Count == 0) saleTypes = ["retail", "prescription"];
        var paymentTypes = NormalizeAllowedArray(
            reader.OptionalStringArray("payment_types", 6),
            ["cash", "card", "bank_transfer", "debt", "discount", "institution"], "payment_types");
        var statuses = NormalizeAllowedArray(
            reader.OptionalStringArray("statuses", 5),
            ["pending", "medula_reported", "completed", "reconciliation_required", "cancelled"], "statuses");
        var barcodes = reader.OptionalStringArray("barcodes", 50);
        var qrCodes = reader.OptionalStringArray("qr_codes", 50);
        var minimumTotal = reader.OptionalNullableDecimal("minimum_total", 0m, MaximumMoney);
        var maximumTotal = reader.OptionalNullableDecimal("maximum_total", 0m, MaximumMoney);
        if (minimumTotal is not null && maximumTotal is not null && minimumTotal > maximumTotal)
            throw new AiToolValidationException("'minimum_total' cannot exceed 'maximum_total'.");
        var patientTc = reader.OptionalString("patient_tc")?.Trim();
        if (!string.IsNullOrEmpty(patientTc) && (patientTc.Length != 11 || !patientTc.All(char.IsAsciiDigit)))
            throw new AiToolValidationException("'patient_tc' must contain exactly 11 digits.");

        return new RetailSaleRecordFilter(
            start.ToDateTime(TimeOnly.MinValue), end.AddDays(1).ToDateTime(TimeOnly.MinValue),
            saleTypes, reader.OptionalBoolean("include_refunds", true), barcodes, qrCodes,
            paymentTypes, statuses,
            reader.OptionalNullableInteger("patient_record_id", 1, int.MaxValue),
            reader.OptionalString("user_id") ?? string.Empty,
            reader.OptionalString("prescription_no") ?? string.Empty,
            reader.OptionalNullableBoolean("is_debted"), reader.OptionalBoolean("marked_only", false),
            minimumTotal, maximumTotal,
            NormalizeAllowedArray(reader.OptionalStringArray("record_types", 2), ["sale", "refund"], "record_types"),
            ParseTime(reader.OptionalString("start_time"), "start_time"),
            ParseTime(reader.OptionalString("end_time"), "end_time"), patientTc,
            reader.OptionalNullableBoolean("has_prescription"));
    }

    private static TimeOnly? ParseTime(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new AiToolValidationException($"'{parameterName}' must use exact HH:mm format.");
        return time;
    }

    private static DateOnly ParseDate(string value, string parameterName)
    {
        if (!DateOnly.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new AiToolValidationException($"'{parameterName}' must use exact YYYY-MM-DD format.");
        if (result == DateOnly.MaxValue)
            throw new AiToolValidationException($"'{parameterName}' is outside the supported date range.");
        return result;
    }

    private static IReadOnlyList<string> NormalizeAllowedArray(
        IReadOnlyList<string> values,
        IReadOnlyList<string> allowed,
        string parameterName)
    {
        var normalized = values.Select(value => value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var invalid = normalized.Where(value => !allowed.Contains(value, StringComparer.Ordinal)).ToArray();
        if (invalid.Length > 0)
            throw new AiToolValidationException(
                $"Every value in '{parameterName}' must be one of: {string.Join(", ", allowed)}.");
        return normalized;
    }

    private static SaleDraftParameters ReadDraft(AiToolParameterReader reader)
    {
        var enteredCodes = reader.RequireStringArrayPreservingOrder("entered_codes", 1, 100);
        var saleMode = ParseSaleMode(reader.EnumValue("sale_mode", "retail", "retail", "consignment"));
        var discountMode = reader.EnumValue("discount_mode", "none", "none", "distribute", "manual");
        var discountAmount = reader.OptionalDecimal("discount_amount", 0m, 0m, MaximumMoney);
        var lineDiscounts = reader.OptionalDecimalArray("line_discounts", enteredCodes.Count, 0m, MaximumMoney);

        if (discountMode == "none" && (discountAmount != 0m || lineDiscounts is not null))
            throw new AiToolValidationException("Discount values require 'discount_mode' to be 'distribute' or 'manual'.");
        if (discountMode == "distribute" && lineDiscounts is not null)
            throw new AiToolValidationException("'line_discounts' cannot be used with distributed discount mode.");
        if (discountMode == "manual" && lineDiscounts is null)
            throw new AiToolValidationException("'line_discounts' is required for manual discount mode.");
        if (discountMode == "manual" && discountAmount != 0m)
            throw new AiToolValidationException("'discount_amount' cannot be used with manual discount mode.");

        return new SaleDraftParameters(
            enteredCodes,
            saleMode,
            discountMode,
            discountMode == "distribute" ? discountAmount : null,
            discountMode == "manual" ? lineDiscounts : null);
    }

    private static Dictionary<RetailPaymentKind, decimal> ReadPaymentAmounts(AiToolParameterReader reader) => new()
    {
        [RetailPaymentKind.Cash] = reader.OptionalDecimal("cash_amount", 0m, 0m, MaximumMoney),
        [RetailPaymentKind.Credit] = reader.OptionalDecimal("card_amount", 0m, 0m, MaximumMoney),
        [RetailPaymentKind.BankTransfer] = reader.OptionalDecimal("bank_transfer_amount", 0m, 0m, MaximumMoney),
        [RetailPaymentKind.Debt] = reader.OptionalDecimal("debt_amount", 0m, 0m, MaximumMoney),
        [RetailPaymentKind.Discount] = reader.OptionalDecimal("discount_amount", 0m, 0m, MaximumMoney)
    };

    private static SalePaymentSplitToolResult ToPaymentSplit(
        decimal target,
        IReadOnlyList<RetailPaymentKind> selectedKinds,
        IReadOnlyDictionary<RetailPaymentKind, decimal> amounts)
    {
        var payments = selectedKinds
            .Select(kind => new SalePaymentToolResult(ToPaymentName(kind), amounts[kind]))
            .ToArray();
        var allocated = decimal.Round(payments.Sum(payment => payment.Amount), 2);
        return new SalePaymentSplitToolResult(
            payments,
            target,
            allocated,
            decimal.Round(target - allocated, 2),
            RetailPaymentBalancer.IsExact(target, selectedKinds, amounts));
    }

    private static SaleTablePreviewToolResult ToPreview(RetailSaleTableBuildResult result)
    {
        var groups = result.Cart.Groups.Select(group => new SaleTableGroupToolResult(
            group.Barcode,
            group.ProductRecordId,
            group.ProductName,
            group.StockQuantity,
            group.Quantity,
            group.UnitCost,
            group.UnitPrice,
            group.TotalPrice,
            group.TotalDiscount,
            group.TotalPrice - group.TotalDiscount,
            group.Entries.Select(entry => new SaleTableEntryToolResult(
                entry.Code,
                entry.Item.IsBarcodeOnly,
                entry.Item.ExpiryDate,
                entry.Item.EntryCost,
                entry.GrossPrice,
                entry.DiscountAmount,
                entry.NetExitPrice)).ToArray())).ToArray();
        return new SaleTablePreviewToolResult(
            groups,
            result.Issues.Select(issue =>
                new SaleTableIssueToolResult(issue.EnteredCode, issue.Code, issue.Message)).ToArray(),
            result.Cart.ItemCount,
            result.Cart.TotalAmount,
            result.Cart.TotalDiscount,
            result.Cart.TotalAmount - result.Cart.TotalDiscount);
    }

    private static SaleTableItemToolResult ToItem(RetailSaleItem item) => new(
        item.EnteredCode,
        item.Barcode,
        item.IsBarcodeOnly,
        item.Status,
        item.ProductRecordId,
        item.ProductName,
        item.UnitPrice,
        item.WarehousePrice,
        item.StockQuantity,
        item.ExpiryDate,
        item.EntryCost,
        item.PublicPrice,
        item.PrescriptionColor,
        item.Indication);

    private static RetailSaleMode ParseSaleMode(string value) => value switch
    {
        "consignment" => RetailSaleMode.ConsignmentCreate,
        _ => RetailSaleMode.Retail
    };

    private static RetailPaymentKind ParsePaymentKind(string value) => value.ToLowerInvariant() switch
    {
        "cash" => RetailPaymentKind.Cash,
        "card" => RetailPaymentKind.Credit,
        "bank_transfer" => RetailPaymentKind.BankTransfer,
        "debt" => RetailPaymentKind.Debt,
        "discount" => RetailPaymentKind.Discount,
        _ => throw new AiToolValidationException(
            "Payment type must be one of: cash, card, bank_transfer, debt, discount.")
    };

    private static string ToPaymentName(RetailPaymentKind kind) => kind switch
    {
        RetailPaymentKind.Cash => "cash",
        RetailPaymentKind.Credit => "card",
        RetailPaymentKind.BankTransfer => "bank_transfer",
        RetailPaymentKind.Debt => "debt",
        RetailPaymentKind.Discount => "discount",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static IAiTool Tool(
        AiToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<object>> execute) =>
        new DelegateAiTool(definition, execute);

    private static AiToolDefinition ResolveItemDefinition() => new(
        "sale.resolve_item",
        "Resolves one exact QR code or non-serialized product barcode against current local stock and reports whether it can be added to an empty Retail Sale table.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["entered_code"] = new("string", "Exact scanned QR code or exact non-serialized product barcode.", Required: true),
            ["sale_mode"] = new("string", "Retail or consignment table rules.", AllowedValues: ["retail", "consignment"], DefaultValue: "retail")
        },
        new Dictionary<string, string>
        {
            ["found"] = "boolean", ["can_add"] = "boolean", ["issue_code"] = "string or null",
            ["issue_message"] = "string or null",
            ["item"] = "resolved sale-table item or null"
        },
        ["sale", "table", "stock", "qr", "barcode"]);

    private static AiToolDefinition PreviewTableDefinition() => new(
        "sale.preview_table",
        "Builds a bounded Retail Sale table preview from exact scanned codes using current stock, duplicate, consignment, grouping, price, and discount rules; it does not change the live table.",
        AiToolRisks.ReadOnly,
        DraftParameters(),
        new Dictionary<string, string>
        {
            ["groups"] = "array of grouped medicine rows with individual entries",
            ["issues"] = "array of rejected-code issues", ["item_count"] = "integer",
            ["gross_total"] = "number", ["discount_total"] = "number", ["net_total"] = "number"
        },
        ["sale", "table", "preview", "discount", "stock"]);

    private static AiToolDefinition CalculatePaymentSplitDefinition() => new(
        "sale.calculate_payment_split",
        "Applies the Retail Sale screen's checkbox and two-payment balancing rules to a bounded payment split without changing a sale.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["total_amount"] = new("number", "Gross sale total that all selected payment types must equal.", Required: true),
            ["selected_payment_types"] = new("array", "One to five distinct values: cash, card, bank_transfer, debt, discount.", Required: true, ItemsType: "string"),
            ["edited_payment_type"] = new("string", "Selected payment type most recently edited."),
            ["cash_amount"] = MoneyParameter("Current cash amount."),
            ["card_amount"] = MoneyParameter("Current card amount."),
            ["bank_transfer_amount"] = MoneyParameter("Current bank-transfer amount."),
            ["debt_amount"] = MoneyParameter("Current patient-debt amount."),
            ["discount_amount"] = MoneyParameter("Current discount amount.")
        },
        new Dictionary<string, string>
        {
            ["payments"] = "array of selected payment types and balanced amounts",
            ["target_total"] = "number", ["allocated_total"] = "number",
            ["remaining_amount"] = "number", ["is_exact"] = "boolean"
        },
        ["sale", "payment", "calculate", "balance"]);

    private static AiToolDefinition ValidateDraftDefinition() => new(
        "sale.validate_draft",
        "Checks whether a bounded Retail Sale or consignment draft satisfies current table, discount, payment, patient, debt, duplicate, and stock rules; it performs no sale or ITS notification.",
        AiToolRisks.ReadOnly,
        DraftParameters(includePayments: true),
        new Dictionary<string, string>
        {
            ["ready"] = "boolean", ["table"] = "structured sale-table preview",
            ["payments"] = "array of payment types and amounts", ["payment_total"] = "number",
            ["debt_amount"] = "number", ["patient_record_id"] = "integer or null",
            ["errors"] = "array of structured validation errors"
        },
        ["sale", "draft", "validate", "payment", "patient", "stock"]);

    private static AiToolDefinition SearchRecordsDefinition() => new(
        "sale.search_records",
        "Returns a database-paginated list of individual retail or prescription sales and optional refunds using composable exact filters. Records contain structured payment totals and immutable per-QR line snapshots; no data is changed.",
        AiToolRisks.ReadOnly,
        RecordFilterParameters(includePaging: true, includeLines: true),
        new Dictionary<string, string>
        {
            ["sales"] = "array of sale/refund records with headers, payment rows, and optionally line rows",
            ["total_count"] = "integer count before pagination", ["offset"] = "integer",
            ["limit"] = "integer", ["has_more"] = "boolean"
        },
        ["sale", "records", "search", "retail", "prescription", "refund", "barcode"]);

    private static AiToolDefinition SearchPatientsDefinition() => new(
        "sale.search_patients",
        "Searches the existing local patient directory by full/partial name or Turkish identity number. No data is changed.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["query"] = new("string", "Patient name or Turkish identity number, at least three characters.", Required: true),
            ["limit"] = new("integer", "Maximum patient candidates from 1 to 200. Use the default for normal search; deterministic match workflows may expand it.", DefaultValue: 20)
        },
        new Dictionary<string, string>
        {
            ["query"] = "string", ["limit"] = "integer", ["patients"] = "array of patient record IDs, names, identity numbers, and phone numbers"
        },
        ["sale", "patient", "directory", "search"]);

    private static AiToolDefinition GetRecordDefinition() => new(
        "sale.get_record",
        "Returns one complete retail/prescription sale or refund, including patient, user, status, payments, immutable QR price/cost snapshots, product data, and original-sale links. No data is changed.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["reference_code"] = new("string", "Canonical Sales reference: P<sale_id> for a sale or G<refund_id> for a refund.", Required: true)
        },
        new Dictionary<string, string>
        {
            ["found"] = "boolean", ["sale"] = "complete structured sale/refund record or null"
        },
        ["sale", "record", "detail", "payment", "line", "qr"]);

    private static AiToolDefinition AggregateRecordsDefinition() => new(
        "sale.aggregate_records",
        "Aggregates filtered retail/prescription sales and optional refunds entirely in the database. Refund values are negative; profit is null when any included line lacks entry cost. No individual records are loaded.",
        AiToolRisks.ReadOnly,
        RecordFilterParameters(includePaging: true, includeAggregation: true),
        new Dictionary<string, string>
        {
            ["groups"] = "paginated aggregate groups with counts, signed quantity, gross/net/institution revenue, discount, entry cost, profit, missing-cost count, and averages",
            ["total_count"] = "integer group count before pagination", ["offset"] = "integer",
            ["limit"] = "integer", ["has_more"] = "boolean"
        },
        ["sale", "aggregate", "revenue", "profit", "quantity", "retail", "prescription", "refund"]);

    private static Dictionary<string, AiToolParameterDefinition> RecordFilterParameters(
        bool includePaging = false,
        bool includeLines = false,
        bool includeAggregation = false)
    {
        var result = new Dictionary<string, AiToolParameterDefinition>
        {
            ["start_date"] = new("string", "Inclusive start date in exact YYYY-MM-DD format; required to bound the query.", Required: true),
            ["end_date"] = new("string", "Inclusive end date in exact YYYY-MM-DD format; required to bound the query.", Required: true),
            ["start_time"] = new("string", "Optional inclusive local HH:mm bound applied each day before counting/paging; start later than end denotes an overnight interval."),
            ["end_time"] = new("string", "Optional inclusive local HH:mm end minute applied each day before counting/paging."),
            ["sale_types"] = new("array", "Any of retail and prescription; empty or omitted means both.", ItemsType: "string"),
            ["include_refunds"] = new("boolean", "Whether matching retail/prescription refunds are included.", DefaultValue: true),
            ["record_types"] = new("array", "Optional sale and/or refund record types; empty means both.", ItemsType: "string"),
            ["barcodes"] = new("array", "Up to 50 exact product barcodes; a record matches any supplied barcode.", ItemsType: "string"),
            ["qr_codes"] = new("array", "Up to 50 exact QR codes; a record matches any supplied QR code.", ItemsType: "string"),
            ["payment_types"] = new("array", "Any of cash, card, bank_transfer, debt, discount, institution; a record matches any supplied type with a positive amount.", ItemsType: "string"),
            ["statuses"] = new("array", "Any of pending, medula_reported, completed, reconciliation_required, cancelled. Omitted excludes pending sales.", ItemsType: "string"),
            ["patient_record_id"] = new("integer", "Exact patient record ID."),
            ["patient_tc"] = new("string", "Optional exact 11-digit patient identity number; combines with other filters."),
            ["user_id"] = new("string", "Exact application user ID."),
            ["prescription_no"] = new("string", "Exact prescription number."),
            ["has_prescription"] = new("boolean", "True requires a nonblank prescription number; false requires no prescription number."),
            ["is_debted"] = new("boolean", "True for records with debt, false for records without debt."),
            ["marked_only"] = new("boolean", "Return only records marked in the Sales screen.", DefaultValue: false),
            ["minimum_total"] = new("number", "Minimum stored transaction total, inclusive."),
            ["maximum_total"] = new("number", "Maximum stored transaction total, inclusive.")
        };
        if (includeLines)
        {
            result["include_lines"] = new("boolean", "Include immutable per-QR line rows; payment rows are always included.", DefaultValue: true);
            result["sort_direction"] = new("string", "Record order by timestamp.", AllowedValues: ["newest_first", "oldest_first"], DefaultValue: "newest_first");
        }
        if (includeAggregation)
        {
            result["group_by"] = new("string", "Aggregation grouping.", AllowedValues: ["none", "day", "month", "sale_type", "record_type", "product", "user", "patient"], DefaultValue: "none");
            result["sort_by"] = new("string", "Aggregate ordering metric.", AllowedValues: ["net_revenue", "gross_revenue", "institution_revenue", "discount_total", "profit", "quantity", "record_count", "group_key"], DefaultValue: "net_revenue");
            result["sort_direction"] = new("string", "Aggregate ordering direction.", AllowedValues: ["descending", "ascending"], DefaultValue: "descending");
        }
        if (includePaging)
        {
            result["offset"] = new("integer", "Zero-based result offset from 0 to 1000000.", DefaultValue: 0);
            result["limit"] = new("integer", "Maximum rows or groups to return, from 1 to 200.", DefaultValue: 100);
        }
        return result;
    }

    private static Dictionary<string, AiToolParameterDefinition> DraftParameters(bool includePayments = false)
    {
        var result = new Dictionary<string, AiToolParameterDefinition>
        {
            ["entered_codes"] = new("array", "One to 100 exact scanned QR codes or non-serialized product barcodes, in table-entry order.", Required: true, ItemsType: "string"),
            ["sale_mode"] = new("string", "Retail or consignment table rules.", AllowedValues: ["retail", "consignment"], DefaultValue: "retail"),
            ["discount_mode"] = new("string", "No discount, proportional distribution, or per-line manual discount.", AllowedValues: ["none", "distribute", "manual"], DefaultValue: "none"),
            ["discount_amount"] = MoneyParameter("Total discount used only when discount_mode is distribute."),
            ["line_discounts"] = new("array", "Manual discount numbers aligned one-to-one with entered_codes; required only for manual mode.", ItemsType: "number")
        };
        if (!includePayments) return result;
        result["patient_record_id"] = new("integer", "Selected patient record ID; required for debt or consignment sales.");
        result["cash_amount"] = MoneyParameter("Cash payment amount.");
        result["card_amount"] = MoneyParameter("Card payment amount.");
        result["bank_transfer_amount"] = MoneyParameter("Bank-transfer payment amount.");
        result["debt_amount"] = MoneyParameter("Amount added to patient debt.");
        return result;
    }

    private static AiToolParameterDefinition MoneyParameter(string description) =>
        new("number", $"{description} Must be between 0 and {MaximumMoney}.", DefaultValue: 0m);

    private sealed class DelegateAiTool(
        AiToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<object>> execute) : IAiTool
    {
        public AiToolDefinition Definition { get; } = definition;

        public Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default) =>
            execute(parameters, cancellationToken);
    }

    private sealed record SaleDraftParameters(
        IReadOnlyList<string> EnteredCodes,
        RetailSaleMode SaleMode,
        string DiscountMode,
        decimal? DistributedDiscount,
        IReadOnlyList<decimal>? LineDiscounts);
}
