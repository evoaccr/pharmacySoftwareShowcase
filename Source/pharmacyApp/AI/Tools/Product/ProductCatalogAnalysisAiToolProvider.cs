using System.Text.Json;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Shared;

namespace pharmacyApp.AI.Tools.Product;

/// <summary>
/// Read-only catalogue-wide analyses over ProductFunctions. Only named analyses are accepted.
/// </summary>
public sealed class ProductCatalogAnalysisAiToolProvider : IAiToolProvider
{
    private readonly ProductFunctions products;
    private readonly IReadOnlyCollection<IAiTool> tools;

    public ProductCatalogAnalysisAiToolProvider(ProductFunctions? products = null)
    {
        this.products = products ?? PharmacyFunctions.Products;
        tools =
        [
            new AnalysisTool(new AiToolDefinition(
                "product.aggregate_catalog",
                "Runs a bounded, named product-catalogue audit or aggregation through the same ProductFunctions query used by Ürün Kataloğu.",
                AiToolRisks.ReadOnly,
                new Dictionary<string, AiToolParameterDefinition>
                {
                    ["analysis_kind"] = new("string", "Named catalogue analysis.", true,
                        ["missing_company_with_ingredient", "company_counts", "ingredient_price_spread", "price_change_frequency", "duplicate_barcode", "stock_range", "stock_value_bottom", "field_completeness", "positive_stock_missing_sale_price"]),
                    ["search"] = new("string", "Optional catalogue search text."),
                    ["only_on_market"] = new("boolean", "Keep active products only.", DefaultValue: false),
                    ["stock_minimum"] = new("integer", "Optional inclusive shelf-stock minimum."),
                    ["stock_maximum"] = new("integer", "Optional inclusive shelf-stock maximum."),
                    ["start_date"] = new("string", "Inclusive YYYY-MM-DD for time-bound analyses."),
                    ["end_date"] = new("string", "Inclusive YYYY-MM-DD for time-bound analyses."),
                    ["limit"] = new("integer", "Maximum result rows, 1-500.", DefaultValue: 100),
                    ["scan_limit"] = new("integer", "Maximum catalogue rows scanned, 1-20000.", DefaultValue: 20000)
                },
                new Dictionary<string, string>
                {
                    ["analysis_kind"] = "string", ["rows"] = "structured catalogue rows",
                    ["source_record_count"] = "integer", ["total_matching_count"] = "integer",
                    ["source_truncated"] = "boolean"
                },
                ["product", "catalogue", "analysis", "read_only"]),
                ExecuteAsync)
        ];
        AiToolDefinitionValidator.Validate(tools);
    }

    public IReadOnlyCollection<IAiTool> Tools => tools;

    private async Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("analysis_kind", "search", "only_on_market", "stock_minimum", "stock_maximum", "start_date", "end_date", "limit", "scan_limit");
        var kind = reader.RequireString("analysis_kind");
        var search = reader.OptionalString("search") ?? string.Empty;
        var onlyOnMarket = reader.OptionalBoolean("only_on_market", false);
        var stockMinimum = reader.OptionalNullableInteger("stock_minimum", -1_000_000, int.MaxValue);
        if (kind == "positive_stock_missing_sale_price") stockMinimum ??= 1;
        var stockMaximum = reader.OptionalNullableInteger("stock_maximum", -1_000_000, int.MaxValue);
        if (stockMinimum.HasValue && stockMaximum.HasValue && stockMinimum > stockMaximum)
            throw new AiToolValidationException("'stock_minimum' cannot exceed 'stock_maximum'.");
        var limit = reader.OptionalInteger("limit", 100, 1, 500);
        if (kind == "price_change_frequency")
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var start = ParseDate(reader.OptionalString("start_date")) ?? today.AddYears(-1);
            var end = ParseDate(reader.OptionalString("end_date")) ?? today;
            if (start > end) throw new AiToolValidationException("'start_date' cannot exceed 'end_date'.");
            var frequency = await products.GetPriceChangeFrequencyAsync(start, end, limit, cancellationToken);
            return new
            {
                analysis_kind = kind,
                rows = frequency.Select(row => Row(("barcode", row.Barcode), ("medicine_name", row.MedicineName),
                    ("price_change_count", row.ChangeCount), ("first_change", row.FirstChange), ("last_change", row.LastChange))).ToArray(),
                source_record_count = frequency.Count, total_matching_count = frequency.Count, source_truncated = false
            };
        }
        var scanLimit = reader.OptionalInteger("scan_limit", 20000, 1, 20000);
        var source = new List<ProductCatalogItem>();
        var totalSource = 0;
        const int pageSize = 1000;
        for (var offset = 0; offset < scanLimit; offset += pageSize)
        {
            var page = await products.SearchProductsAsync(new ProductCatalogQuery(
                Search: search, OnlyInStock: kind == "stock_value_bottom", StockMinimum: stockMinimum, StockMaximum: stockMaximum,
                MarketStatus: onlyOnMarket ? 1 : null, RequireBarcode: false,
                SortField: kind == "stock_value_bottom" ? "StockValue" : "Name",
                SortDescending: false, Offset: offset, Limit: Math.Min(pageSize, scanLimit - offset)), cancellationToken);
            totalSource = page.TotalCount;
            source.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0 || source.Count >= scanLimit) break;
        }

        IEnumerable<IReadOnlyDictionary<string, object?>> rows = kind switch
        {
            "missing_company_with_ingredient" => source.Where(p => string.IsNullOrWhiteSpace(p.CompanyName) && !string.IsNullOrWhiteSpace(p.ActiveIngredient))
                .Select(ProductRow),
            "company_counts" => source.GroupBy(p => string.IsNullOrWhiteSpace(p.CompanyName) ? "Belirtilmemiş" : p.CompanyName!, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => Row(("company_name", g.Key), ("product_count", g.Count()), ("stock_quantity", g.Sum(p => p.StockQuantity ?? 0))))
                .OrderByDescending(r => Convert.ToInt32(r["product_count"])),
            "ingredient_price_spread" => source.Where(p => !string.IsNullOrWhiteSpace(p.ActiveIngredient) && p.SalePrice.HasValue)
                .GroupBy(p => p.ActiveIngredient!.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var minimum = g.Min(p => p.SalePrice!.Value);
                    var maximum = g.Max(p => p.SalePrice!.Value);
                    return Row(("active_ingredient", g.Key), ("product_count", g.Count()), ("minimum_sale_price", minimum),
                        ("maximum_sale_price", maximum), ("price_spread", maximum - minimum));
                }).OrderByDescending(r => Convert.ToDecimal(r["price_spread"])),
            "duplicate_barcode" => source.Where(p => !string.IsNullOrWhiteSpace(p.Barcode)).GroupBy(p => p.Barcode!, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => Row(("barcode", g.Key), ("duplicate_count", g.Count()),
                    ("medicine_names", string.Join(" | ", g.Select(p => p.DisplayName).Distinct())))),
            "stock_range" => source.Select(ProductRow).OrderBy(r => Convert.ToInt32(r["shelf_stock_quantity"])),
            "stock_value_bottom" => source.Where(p => (p.StockQuantity ?? 0) > 0).Select(ProductRow)
                .OrderBy(r => Convert.ToDecimal(r["stock_value"])),
            "positive_stock_missing_sale_price" => source.Where(p => (p.StockQuantity ?? 0) > 0 && p.SalePrice is null).Select(ProductRow),
            "field_completeness" => source.Select(p => Row(("barcode", p.Barcode), ("medicine_name", p.DisplayName),
                    ("missing_fields", string.Join(", ", MissingFields(p))), ("missing_field_count", MissingFields(p).Count))),
            _ => throw new AiToolValidationException("Unsupported 'analysis_kind'.")
        };
        var all = rows.ToArray();
        return new
        {
            analysis_kind = kind,
            rows = all.Take(limit).ToArray(),
            source_record_count = source.Count,
            total_matching_count = all.Length,
            source_truncated = totalSource > source.Count
        };
    }

    private static IReadOnlyList<string> MissingFields(ProductCatalogItem product)
    {
        var fields = new List<string>();
        Add(product.Barcode, "barcode"); Add(product.CompanyName, "company_name");
        Add(product.ActiveIngredient, "active_ingredient"); Add(product.PharmaceuticalForm, "pharmaceutical_form");
        Add(product.PrescriptionTypeName, "prescription_type"); Add(product.PackageDescription, "package_description");
        return fields;
        void Add(string? value, string name) { if (string.IsNullOrWhiteSpace(value)) fields.Add(name); }
    }

    private static IReadOnlyDictionary<string, object?> ProductRow(ProductCatalogItem product)
    {
        var stock = product.StockQuantity ?? 0;
        return Row(("barcode", product.Barcode), ("medicine_name", product.DisplayName),
            ("company_name", product.CompanyName), ("active_ingredient", product.ActiveIngredient),
            ("shelf_stock_quantity", stock), ("sale_price", product.SalePrice), ("purchase_price", product.WarehousePrice),
            ("stock_value", (product.WarehousePrice ?? 0m) * stock), ("is_on_market", product.MarketStatus != 0));
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed) ? parsed : null;

    private sealed class AnalysisTool(
        AiToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<object>> execute) : IAiTool
    {
        public AiToolDefinition Definition { get; } = definition;
        public Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default) =>
            execute(parameters, cancellationToken);
    }
}
