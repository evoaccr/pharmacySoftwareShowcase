using System.Globalization;
using System.Text.Json;
using pharmacyApp.Modules.Products;

namespace pharmacyApp.AI.Tools.Product;

/// <summary>Read-only analysis over one authoritative, bounded inventory/sales snapshot.</summary>
public sealed class InventorySalesAnalysisAiToolProvider : IAiToolProvider
{
    private readonly Func<DateOnly, DateOnly, int, CancellationToken, Task<ProductInventorySalesSnapshot>> load;
    private readonly IReadOnlyCollection<IAiTool> tools;
    public InventorySalesAnalysisAiToolProvider(Func<DateOnly, DateOnly, int, CancellationToken, Task<ProductInventorySalesSnapshot>>? load = null)
    {
        this.load = load ?? new ProductInventorySalesRepository().LoadAsync;
        tools = [new AnalysisTool(new AiToolDefinition(
            "product.analyze_inventory_sales", "Active catalogue shelf stock joined to sold packs. Replenishment is an estimate, not an order.", AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["analysis_kind"] = new("string", "Inventory/sales analysis.", true, ["dead_stock", "sold_over_stock", "sold_out_recently", "coverage", "replenishment"]),
                ["start_date"] = new("string", "Inclusive YYYY-MM-DD.", true), ["end_date"] = new("string", "Inclusive YYYY-MM-DD.", true),
                ["minimum_quantity"] = new("integer", "Minimum sold packs.", DefaultValue: 1),
                ["maximum_coverage_days"] = new("number", "Optional maximum stock coverage days."),
                ["target_days"] = new("integer", "Replenishment target coverage, 1-365 days.", DefaultValue: 30),
                ["limit"] = new("integer", "Maximum result rows, 1-500.", DefaultValue: 100),
                ["catalog_scan_limit"] = new("integer", "Maximum active catalogue rows, 100-50000.", DefaultValue: 20000)
            }, new Dictionary<string, string>
            {
                ["rows"] = "product analysis rows", ["total_matching_count"] = "integer", ["catalog_record_count"] = "integer",
                ["sales_group_count"] = "integer", ["catalog_truncated"] = "boolean"
            }, ["product", "stock", "sale", "analysis", "read_only"]), ExecuteAsync)];
        AiToolDefinitionValidator.Validate(tools);
    }
    public IReadOnlyCollection<IAiTool> Tools => tools;
    private async Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var reader = new AiToolParameterReader(parameters);
        reader.RejectUnknown("analysis_kind", "start_date", "end_date", "minimum_quantity", "maximum_coverage_days", "target_days", "limit", "catalog_scan_limit");
        var kind = reader.RequireString("analysis_kind");
        if (kind is not ("dead_stock" or "sold_over_stock" or "sold_out_recently" or "coverage" or "replenishment"))
            throw new AiToolValidationException("Unsupported 'analysis_kind'.");
        var start = ParseDate(reader.RequireString("start_date"), "start_date");
        var end = ParseDate(reader.RequireString("end_date"), "end_date");
        if (end < start) throw new AiToolValidationException("'end_date' cannot precede 'start_date'.");
        var minimum = reader.OptionalInteger("minimum_quantity", 1, 1, 1_000_000);
        var maximumCoverage = reader.OptionalNullableDecimal("maximum_coverage_days", 0m, 100000m);
        var target = reader.OptionalInteger("target_days", 30, 1, 365);
        var limit = reader.OptionalInteger("limit", 100, 1, 500);
        var scanLimit = reader.OptionalInteger("catalog_scan_limit", 20000, 100, 50000);
        var snapshot = await load(start, end, scanLimit, cancellationToken);
        var days = Math.Max(1, end.DayNumber - start.DayNumber + 1);
        var joined = snapshot.Rows.Select(p => new
        {
            Product = p,
            Coverage = p.SoldQuantity > 0 ? p.ShelfStock * days / p.SoldQuantity : (decimal?)null,
            Recommended = Math.Max(0, decimal.Ceiling(p.SoldQuantity * target / days - p.ShelfStock))
        });
        joined = kind switch
        {
            "dead_stock" => joined.Where(x => x.Product.ShelfStock > 0 && x.Product.SoldQuantity == 0),
            "sold_over_stock" => joined.Where(x => x.Product.SoldQuantity >= minimum && x.Product.SoldQuantity > x.Product.ShelfStock),
            "sold_out_recently" => joined.Where(x => x.Product.SoldQuantity >= minimum && x.Product.ShelfStock <= 0),
            "coverage" => joined.Where(x => x.Product.SoldQuantity >= minimum && (!maximumCoverage.HasValue || x.Coverage <= maximumCoverage.Value)),
            _ => joined.Where(x => x.Product.SoldQuantity >= minimum && x.Recommended > 0)
        };
        var ordered = kind == "dead_stock" ? joined.OrderByDescending(x => x.Product.ShelfStock) :
            kind == "coverage" ? joined.OrderBy(x => x.Coverage ?? decimal.MaxValue) :
            kind == "replenishment" ? joined.OrderByDescending(x => x.Recommended) :
            joined.OrderByDescending(x => x.Product.SoldQuantity - x.Product.ShelfStock);
        var all = ordered.ThenBy(x => x.Product.Barcode, StringComparer.Ordinal).ToArray();
        return new
        {
            analysis_kind = kind,
            rows = all.Take(limit).Select(x => new Dictionary<string, object?>
            {
                ["barcode"] = x.Product.Barcode, ["medicine_name"] = x.Product.MedicineName,
                ["shelf_stock_quantity"] = x.Product.ShelfStock, ["sold_quantity"] = x.Product.SoldQuantity,
                ["net_revenue"] = x.Product.Revenue, ["coverage_days"] = x.Coverage.HasValue ? Math.Round(x.Coverage.Value, 1) : null,
                ["target_stock_quantity"] = decimal.Ceiling(x.Product.SoldQuantity * target / days),
                ["recommended_purchase_quantity"] = x.Recommended
            }).ToArray(),
            total_matching_count = all.Length, catalog_record_count = snapshot.Rows.Count,
            sales_group_count = snapshot.Rows.Count(row => row.SoldQuantity > 0),
            catalog_truncated = snapshot.CatalogCount > snapshot.Rows.Count, target_days = target,
            observed_days = days, start_date = start.ToString("yyyy-MM-dd"), end_date = end.ToString("yyyy-MM-dd"),
            basis = "active_catalogue_recorded_shelf_stock_and_gross_sold_packs_excluding_refunds_no_incoming_orders"
        };
    }
    private static DateOnly ParseDate(string value, string name) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new AiToolValidationException($"'{name}' must use YYYY-MM-DD.");
    private sealed class AnalysisTool(AiToolDefinition definition, Func<JsonElement, CancellationToken, Task<object>> execute) : IAiTool
    {
        public AiToolDefinition Definition { get; } = definition;
        public Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default) => execute(parameters, cancellationToken);
    }
}
