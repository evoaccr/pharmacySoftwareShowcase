using System.Text.Json;
using System.Text.Json.Serialization;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Shared;

namespace pharmacyApp.AI.Tools.Product;

/// <summary>
/// Discoverable, executable AI tools owned by the Product module.
/// All tools are read-only wrappers over ProductFunctions; no UI or SQL is exposed.
/// </summary>
public sealed class ProductAiToolCatalog : IAiToolProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ProductFunctions products;
    private readonly IReadOnlyDictionary<string, IAiTool> tools;

    public ProductAiToolCatalog(ProductFunctions? products = null)
    {
        this.products = products ?? PharmacyFunctions.Products;
        var registered = new IAiTool[]
        {
            Tool(SearchByNameDefinition(), SearchByNameAsync),
            Tool(SearchDefinition(), SearchAsync),
            Tool(GetDefinition(), GetAsync),
            Tool(PriceHistoryDefinition(), GetPriceHistoryAsync),
            Tool(EquivalentsDefinition(), GetEquivalentsAsync),
            Tool(AnalysisDefinition(), GetAnalysisAsync),
            Tool(CompareDefinition(), CompareAsync)
        };
        AiToolDefinitionValidator.Validate(registered);
        tools = registered.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
    }

    private async Task<object> SearchByNameAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("query");
        var query = reader.RequireString("query");
        var result = await products.SearchProductsByNameAsync(query, 50, cancellationToken);
        return new ProductNameSearchToolResult(
            result.RequestedText,
            result.MatchedText,
            !string.Equals(result.RequestedText, result.MatchedText, StringComparison.OrdinalIgnoreCase),
            result.Items.Select(ToSummary).ToArray(),
            result.TotalCount,
            result.HasMore);
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
            throw new KeyNotFoundException($"Unknown Product AI tool '{toolName}'.");
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

    private async Task<object> SearchAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown(
            "search", "product_prefix", "ingredient", "prescription_type", "only_in_stock",
            "stock_minimum", "stock_maximum", "only_on_market", "market_status", "filter_field", "filter_value",
            "sort_by", "sort_direction", "offset", "limit");

        var stockMinimum = reader.OptionalNullableInteger("stock_minimum", -1_000_000, int.MaxValue);
        var stockMaximum = reader.OptionalNullableInteger("stock_maximum", -1_000_000, int.MaxValue);
        if (stockMinimum.HasValue && stockMaximum.HasValue && stockMinimum > stockMaximum)
            throw new AiToolValidationException("'stock_minimum' cannot be greater than 'stock_maximum'.");

        var filterField = reader.OptionalString("filter_field");
        var filterValue = reader.OptionalString("filter_value");
        if ((filterField is null) != (filterValue is null))
            throw new AiToolValidationException("'filter_field' and 'filter_value' must be supplied together.");

        var headerField = filterField?.ToLowerInvariant() switch
        {
            null => null,
            "product_prefix" => "ProductPrefix",
            "pharmaceutical_form" => "PharmaceuticalForm",
            "sgk_code" => "SgkCode",
            "atc_code" => "AtcCode",
            "generic_code" => "GenericCode",
            "company" => "CompanyName",
            "brand_name" => "BrandName",
            "equivalent_group_code" => "EquivalentGroupCode",
            _ => throw new AiToolValidationException("Unsupported 'filter_field'.")
        };
        var sortBy = reader.EnumValue("sort_by", "name", "name", "price", "stock", "stock_value");
        var sortDirection = reader.EnumValue("sort_direction", "asc", "asc", "desc");
        var marketStatus = reader.EnumValue("market_status", "all", "all", "active", "passive");
        var onlyOnMarket = reader.OptionalBoolean("only_on_market", false);
        if (onlyOnMarket && marketStatus == "passive")
            throw new AiToolValidationException("'only_on_market=true' cannot be combined with 'market_status=passive'.");
        var offset = reader.OptionalInteger("offset", 0, 0, int.MaxValue);
        var limit = reader.OptionalInteger("limit", 50, 1, 100);

        var query = new ProductCatalogQuery(
            Search: reader.OptionalString("search") ?? string.Empty,
            ProductPrefix: reader.OptionalString("product_prefix") ?? string.Empty,
            Ingredient: reader.OptionalString("ingredient") ?? string.Empty,
            Prescription: reader.OptionalString("prescription_type") ?? string.Empty,
            OnlyInStock: reader.OptionalBoolean("only_in_stock", false),
            StockMinimum: stockMinimum,
            StockMaximum: stockMaximum,
            MarketStatus: onlyOnMarket || marketStatus == "active" ? 1 : marketStatus == "passive" ? 0 : null,
            RequireBarcode: true,
            HeaderField: headerField,
            HeaderValue: filterValue,
            SortField: sortBy switch { "price" => "Price", "stock" => "Stock", "stock_value" => "StockValue", _ => "Name" },
            SortDescending: sortDirection == "desc",
            Offset: offset,
            Limit: limit);
        var page = await products.SearchProductsAsync(query, cancellationToken);
        return new ProductSearchToolResult(
            page.Items.Select(ToSummary).ToArray(), page.TotalCount, offset, limit, page.HasMore);
    }

    private async Task<object> GetAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("barcode");
        var barcode = reader.RequireString("barcode");
        var product = await products.GetByBarcodeAsync(barcode, cancellationToken);
        if (product is null) return new ProductLookupToolResult(false, null);

        var details = await products.GetDetailsAsync(product.RecordId, cancellationToken);
        return new ProductLookupToolResult(true, ToDetails(product, details));
    }

    private async Task<object> GetPriceHistoryAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("barcode", "offset", "limit");
        var barcode = reader.RequireString("barcode");
        var offset = reader.OptionalInteger("offset", 0, 0, int.MaxValue);
        var limit = reader.OptionalInteger("limit", 100, 1, 500);
        var product = await products.GetByBarcodeAsync(barcode, cancellationToken);
        if (product is null) return new ProductPriceHistoryToolResult(false, barcode, [], 0, offset, limit, false);

        var history = await products.GetPriceHistoryAsync(product.RecordId, cancellationToken);
        var points = history.Skip(offset).Take(limit).Select(point => new ProductPriceHistoryToolPoint(
            point.EffectiveDate,
            point.SalePrice,
            point.WarehousePrice,
            point.ManufacturerPrice,
            point.PublicPrice,
            point.PublicPaidPrice,
            point.PriceDifference,
            point.DiscountRate,
            point.VatRate)).ToArray();
        return new ProductPriceHistoryToolResult(
            true, product.Barcode!, points, history.Count, offset, limit, offset + points.Length < history.Count);
    }

    private async Task<object> GetEquivalentsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("barcode", "kind", "limit");
        var barcode = reader.RequireString("barcode");
        var kind = reader.EnumValue("kind", "all", "all", "sgk", "general");
        var limit = reader.OptionalInteger("limit", 100, 1, 500);
        var product = await products.GetByBarcodeAsync(barcode, cancellationToken);
        if (product is null)
            return new ProductEquivalentsToolResult(false, barcode, kind, [], 0, [], 0);

        IReadOnlyList<string> sgk = [];
        IReadOnlyList<string> general = [];
        if (kind is "all" or "sgk")
            sgk = await products.GetSgkEquivalentBarcodesAsync(
                product.EquivalentGroupCode, product.RecordId, cancellationToken);
        if (kind is "all" or "general")
            general = await products.GetGeneralEquivalentBarcodesAsync(
                product.AllEquivalentBarcodes, product.RecordId, cancellationToken);

        return new ProductEquivalentsToolResult(
            true,
            product.Barcode!,
            kind,
            sgk.Take(limit).ToArray(),
            sgk.Count,
            general.Take(limit).ToArray(),
            general.Count);
    }

    private async Task<object> GetAnalysisAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("barcode", "days", "outgoing_type_code", "incoming_type_code");
        var barcode = reader.RequireString("barcode");
        var days = reader.OptionalInteger("days", 90, 1, 365);
        var outgoingCode = reader.OptionalString("outgoing_type_code");
        var incomingCode = reader.OptionalString("incoming_type_code");
        var analysis = await products.GetAnalysisByBarcodeAsync(barcode, cancellationToken);
        if (analysis is null)
            return ProductAnalysisToolResult.NotFound(barcode);

        var outgoingType = ResolveMovementType(analysis, outgoingCode, isEntry: false);
        var incomingType = ResolveMovementType(analysis, incomingCode, isEntry: true);
        var movements = analysis.DailyMovements.TakeLast(days)
            .Select(day => new ProductAnalysisDayToolResult(
                day.Day.ToString("yyyy-MM-dd"),
                analysis.QuantityFor(day, outgoingType),
                analysis.QuantityFor(day, incomingType)))
            .ToArray();

        return new ProductAnalysisToolResult(
            true,
            barcode,
            analysis.AverageMonthlySales,
            analysis.WantedStockMonths,
            decimal.ToInt32(decimal.Ceiling(analysis.TargetStockQuantity)),
            decimal.ToInt32(analysis.MissingStockQuantity),
            analysis.ShelfStockQuantity,
            analysis.ConsignedStockQuantity,
            analysis.StockComment,
            outgoingType.Code,
            incomingType.Code,
            analysis.MovementTypes.Where(type => !type.IsEntry).Select(ToMovementType).ToArray(),
            analysis.MovementTypes.Where(type => type.IsEntry).Select(ToMovementType).ToArray(),
            movements);
    }

    private async Task<object> CompareAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("barcodes");
        var barcodes = reader.RequireStringArray("barcodes", 2, 10);
        var resolved = await Task.WhenAll(barcodes.Select(async barcode => new
        {
            Barcode = barcode,
            Product = await products.GetByBarcodeAsync(barcode, cancellationToken)
        }));
        var compared = new List<ProductCompareRow>();
        foreach (var item in resolved.Where(item => item.Product is not null))
        {
            var product = item.Product!;
            var latest = (await products.GetPriceHistoryAsync(product.RecordId, cancellationToken)).LastOrDefault();
            compared.Add(new ProductCompareRow(
                product.Barcode!,
                product.DisplayName,
                Clean(product.ActiveIngredient),
                Clean(product.CompanyName),
                latest?.SalePrice,
                product.WarehousePrice ?? latest?.WarehousePrice,
                latest?.PublicPrice,
                latest?.PublicPaidPrice,
                product.StockQuantity,
                product.MarketStatus == 1,
                product.CurrencyDisplay));
        }
        return new ProductCompareToolResult(
            compared,
            resolved.Where(item => item.Product is null).Select(item => item.Barcode).ToArray());
    }

    private static ProductAnalysisMovementType ResolveMovementType(
        ProductAnalysisData analysis,
        string? code,
        bool isEntry)
    {
        if (code is null)
            return new ProductAnalysisMovementType(null, isEntry ? "Tüm Girişler" : "Tüm Çıkışlar", isEntry);
        return analysis.MovementTypes.FirstOrDefault(type =>
                   type.IsEntry == isEntry && string.Equals(type.Code, code, StringComparison.OrdinalIgnoreCase))
               ?? throw new AiToolValidationException(
                   $"Movement type '{code}' is not a valid {(isEntry ? "incoming" : "outgoing")} type.");
    }

    private static ProductMovementTypeToolResult ToMovementType(ProductAnalysisMovementType type) =>
        new(type.Code!, type.DisplayName);

    private static ProductToolSummary ToSummary(ProductCatalogItem product) => new(
        product.RecordId,
        product.Barcode!,
        product.DisplayName,
        Clean(product.ProductPrefix),
        Clean(product.ActiveIngredient),
        Clean(product.CompanyName),
        product.StockQuantity,
        product.SalePrice,
        product.WarehousePrice,
        product.CurrencyDisplay,
        product.HasActiveCampaign,
        product.MarketStatus == 1,
        Clean(product.ReimbursementStatusDescription),
        product.EffectivePriceDifference);

    private static ProductToolDetails ToDetails(ProductCatalogItem product, ProductDetailPayload details) => new(
        ToSummary(product),
        Clean(product.BrandName),
        Clean(product.OldName),
        Clean(product.ProductTypeName),
        Clean(product.Origin),
        Clean(product.EquivalentGroupCode),
        Clean(product.EquivalentClassification),
        Clean(product.AtcCode),
        Clean(product.SgkCode),
        Clean(product.GenericCode),
        Clean(product.PharmaceuticalForm),
        Clean(product.PackageDescription),
        Clean(product.PrescriptionTypeName),
        Clean(product.PregnancyCategory),
        Clean(product.ShelfLife),
        Clean(product.IndicationSummary),
        Clean(product.DosageInstructionsDisplay),
        product.ManufacturerPrice,
        product.VatRate,
        product.CampaignPurchasePrice,
        details.Photos.Count,
        details.KubDocument is { Length: > 0 },
        details.KitDocument is { Length: > 0 },
        !string.IsNullOrWhiteSpace(product.SutRules) || !string.IsNullOrWhiteSpace(details.SutSummaryJson),
        details.Warnings.Select(warning => new ProductWarningToolResult(warning.Header, warning.Description)).ToArray());

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "—" ? null : value.Trim();

    private static IAiTool Tool(
        AiToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<object>> execute) =>
        new DelegateAiTool(definition, execute);

    private static AiToolDefinition SearchDefinition() => new(
        "product.search",
        "Searches the product catalog with the same business filters and shelf-stock rules as Ürün Katalogu. Returns a bounded page of product summaries.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["search"] = new("string", "Medicine name, brand, old name, barcode prefix, ingredient, or company."),
            ["product_prefix"] = new("string", "Exact product prefix filter."),
            ["ingredient"] = new("string", "One exact active ingredient; combination products are matched by individual ingredient."),
            ["prescription_type"] = new("string", "Exact prescription type filter."),
            ["only_in_stock"] = new("boolean", "When true, returns only products with positive shelf stock.", DefaultValue: false),
            ["stock_minimum"] = new("integer", "Minimum shelf-stock boxes, inclusive."),
            ["stock_maximum"] = new("integer", "Maximum shelf-stock boxes, inclusive."),
            ["only_on_market"] = new("boolean", "When true, returns only products currently on the Turkish market. Leave false for name resolution so passive products remain visible.", DefaultValue: false),
            ["market_status"] = new("string", "Explicit market-state filter. Use passive for off-market products.", AllowedValues: ["all", "active", "passive"], DefaultValue: "all"),
            ["filter_field"] = new("string", "Optional exact catalog field filter.", AllowedValues: ["product_prefix", "pharmaceutical_form", "sgk_code", "atc_code", "generic_code", "company", "brand_name", "equivalent_group_code"]),
            ["filter_value"] = new("string", "Exact value for filter_field."),
            ["sort_by"] = new("string", "Result ordering field.", AllowedValues: ["name", "price", "stock", "stock_value"], DefaultValue: "name"),
            ["sort_direction"] = new("string", "Result ordering direction.", AllowedValues: ["asc", "desc"], DefaultValue: "asc"),
            ["offset"] = new("integer", "Zero-based result offset.", DefaultValue: 0),
            ["limit"] = new("integer", "Page size from 1 to 100.", DefaultValue: 50)
        },
        new Dictionary<string, string>
        {
            ["products"] = "array of product summaries",
            ["total_count"] = "integer",
            ["offset"] = "integer",
            ["limit"] = "integer",
            ["has_more"] = "boolean"
        },
        ["product", "catalog", "search", "barcode", "stock"]);

    private static AiToolDefinition SearchByNameDefinition() => new(
        "product.search_by_name",
        "Finds medicine names, brands, forms, prices, and passive/off-market records from plain user text. Use this instead of product.search for ordinary name, form, or price questions; the backend safely extracts the catalog name from surrounding words.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["query"] = new("string", "The user's medicine name or complete short phrase, such as 'arveles fiyat söyle'.", Required: true)
        },
        new Dictionary<string, string>
        {
            ["requested_text"] = "string",
            ["matched_text"] = "string|null",
            ["fallback_used"] = "boolean",
            ["products"] = "array of product summaries",
            ["total_count"] = "integer",
            ["has_more"] = "boolean"
        },
        ["product", "medicine", "name", "search", "price", "form", "urun", "ilac", "fiyat", "bul"]);

    private static AiToolDefinition GetDefinition() => new(
        "product.get",
        "Returns a single product's business details by exact barcode, including pricing, shelf stock, document availability, and warning text without binary files or raw JSON.",
        AiToolRisks.ReadOnly,
        RequiredBarcode(),
        new Dictionary<string, string> { ["found"] = "boolean", ["product"] = "product details or null" },
        ["product", "details", "barcode", "price", "stock", "warning"]);

    private static AiToolDefinition PriceHistoryDefinition() => new(
        "product.get_price_history",
        "Returns a bounded chronological page of recorded product prices by exact barcode.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>(RequiredBarcode())
        {
            ["offset"] = new("integer", "Zero-based history offset.", DefaultValue: 0),
            ["limit"] = new("integer", "Maximum history points from 1 to 500.", DefaultValue: 100)
        },
        new Dictionary<string, string>
        {
            ["found"] = "boolean", ["barcode"] = "string", ["points"] = "array",
            ["total_count"] = "integer", ["offset"] = "integer", ["limit"] = "integer", ["has_more"] = "boolean"
        },
        ["product", "price", "history"]);

    private static AiToolDefinition EquivalentsDefinition() => new(
        "product.get_equivalents",
        "Returns SGK and/or general equivalent product barcodes for one exact product barcode.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>(RequiredBarcode())
        {
            ["kind"] = new("string", "Equivalent set to return.", AllowedValues: ["all", "sgk", "general"], DefaultValue: "all"),
            ["limit"] = new("integer", "Maximum barcodes returned per equivalent set, from 1 to 500.", DefaultValue: 100)
        },
        new Dictionary<string, string>
        {
            ["found"] = "boolean", ["barcode"] = "string", ["kind"] = "string",
            ["sgk_barcodes"] = "array of strings", ["sgk_total_count"] = "integer",
            ["general_barcodes"] = "array of strings", ["general_total_count"] = "integer"
        },
        ["product", "equivalent", "sgk", "barcode"]);

    private static AiToolDefinition AnalysisDefinition() => new(
        "product.get_analysis",
        "Returns compound stock analysis and a bounded daily incoming/outgoing movement series for one product barcode.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>(RequiredBarcode())
        {
            ["days"] = new("integer", "Number of most recent daily movement points from 1 to 365.", DefaultValue: 90),
            ["outgoing_type_code"] = new("string", "Optional outgoing qrMovementTypes code; omitted means all outgoing movements."),
            ["incoming_type_code"] = new("string", "Optional incoming qrMovementTypes code; omitted means all incoming movements.")
        },
        new Dictionary<string, string>
        {
            ["found"] = "boolean", ["barcode"] = "string", ["average_monthly_sales"] = "number",
            ["wanted_stock_months"] = "number", ["target_stock_quantity"] = "integer",
            ["missing_stock_quantity"] = "integer", ["shelf_stock_quantity"] = "integer",
            ["consigned_stock_quantity"] = "integer", ["stock_comment"] = "string",
            ["movements"] = "array of daily incoming/outgoing quantities"
        },
        ["product", "analysis", "stock", "sales", "movement"]);

    private static AiToolDefinition CompareDefinition() => new(
        "product.compare",
        "Returns comparable catalog, price, market, and shelf-stock summaries for 2 to 10 exact product barcodes.",
        AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["barcodes"] = new("array", "Two to ten distinct exact product barcodes.", Required: true, ItemsType: "string")
        },
        new Dictionary<string, string>
        {
            ["products"] = "array of product summaries", ["missing_barcodes"] = "array of strings"
        },
        ["product", "compare", "price", "stock", "barcode"]);

    private static Dictionary<string, AiToolParameterDefinition> RequiredBarcode() => new()
    {
        ["barcode"] = new("string", "Exact product barcode.", Required: true)
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

public sealed record ProductToolSummary(
    int RecordId,
    string Barcode,
    string Name,
    string? ProductPrefix,
    string? ActiveIngredient,
    string? Company,
    int? ShelfStockQuantity,
    decimal? SalePrice,
    decimal? PurchasePrice,
    string Currency,
    bool HasActiveCampaign,
    bool IsOnMarket,
    string? ReimbursementStatus,
    decimal? PriceDifference);

public sealed record ProductSearchToolResult(
    IReadOnlyList<ProductToolSummary> Products,
    int TotalCount,
    int Offset,
    int Limit,
    bool HasMore);

public sealed record ProductNameSearchToolResult(
    string RequestedText,
    string? MatchedText,
    bool FallbackUsed,
    IReadOnlyList<ProductToolSummary> Products,
    int TotalCount,
    bool HasMore);

public sealed record ProductWarningToolResult(string Header, string Description);

public sealed record ProductToolDetails(
    ProductToolSummary Summary,
    string? BrandName,
    string? OldName,
    string? ProductType,
    string? Origin,
    string? EquivalentGroupCode,
    string? EquivalentClassification,
    string? AtcCode,
    string? SgkCode,
    string? GenericCode,
    string? PharmaceuticalForm,
    string? PackageDescription,
    string? PrescriptionType,
    string? PregnancyCategory,
    string? ShelfLife,
    string? Indication,
    string? DosageInstructions,
    decimal? ManufacturerPrice,
    decimal? VatRate,
    decimal? CampaignPurchasePrice,
    int PhotoCount,
    bool HasKubDocument,
    bool HasKitDocument,
    bool HasSutContent,
    IReadOnlyList<ProductWarningToolResult> Warnings);

public sealed record ProductLookupToolResult(bool Found, ProductToolDetails? Product);

public sealed record ProductPriceHistoryToolPoint(
    DateTime EffectiveDate,
    decimal? SalePrice,
    decimal? WarehousePrice,
    decimal? ManufacturerPrice,
    decimal? PublicPrice,
    decimal? PublicPaidPrice,
    decimal? PriceDifference,
    decimal? DiscountRate,
    decimal? VatRate);

public sealed record ProductPriceHistoryToolResult(
    bool Found,
    string Barcode,
    IReadOnlyList<ProductPriceHistoryToolPoint> Points,
    int TotalCount,
    int Offset,
    int Limit,
    bool HasMore);

public sealed record ProductEquivalentsToolResult(
    bool Found,
    string Barcode,
    string Kind,
    IReadOnlyList<string> SgkBarcodes,
    int SgkTotalCount,
    IReadOnlyList<string> GeneralBarcodes,
    int GeneralTotalCount);

public sealed record ProductMovementTypeToolResult(string Code, string DisplayName);

public sealed record ProductAnalysisDayToolResult(string Day, int OutgoingQuantity, int IncomingQuantity);

public sealed record ProductAnalysisToolResult(
    bool Found,
    string Barcode,
    decimal? AverageMonthlySales,
    decimal? WantedStockMonths,
    int? TargetStockQuantity,
    int? MissingStockQuantity,
    int? ShelfStockQuantity,
    int? ConsignedStockQuantity,
    string? StockComment,
    string? OutgoingTypeCode,
    string? IncomingTypeCode,
    IReadOnlyList<ProductMovementTypeToolResult> AvailableOutgoingTypes,
    IReadOnlyList<ProductMovementTypeToolResult> AvailableIncomingTypes,
    IReadOnlyList<ProductAnalysisDayToolResult> Movements)
{
    public static ProductAnalysisToolResult NotFound(string barcode) => new(
        false, barcode, null, null, null, null, null, null, null, null, null, [], [], []);
}

public sealed record ProductCompareToolResult(
    IReadOnlyList<ProductCompareRow> Products,
    IReadOnlyList<string> MissingBarcodes);

public sealed record ProductCompareRow(
    string Barcode,
    string Name,
    string? ActiveIngredient,
    string? Company,
    decimal? SalePrice,
    decimal? PurchasePrice,
    decimal? PublicPrice,
    decimal? PublicPaidPrice,
    int? ShelfStockQuantity,
    bool IsOnMarket,
    string Currency);
