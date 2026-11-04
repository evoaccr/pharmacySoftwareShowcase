using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public sealed class ProductStockSummaryWorkflow : IAiWorkflow
{
    private const string ProductTool = "product.get";
    private const string AnalysisTool = "product.get_analysis";
    private const string PriceTool = "product.get_price_history";

    public AiWorkflowDefinition Definition { get; } = new(
        "urun_stok_ozeti",
        "Ürün ve stok özeti",
        "Combines one or more exact products' identity, market state, shelf/incoming stock and latest registered prices in one coherent result.",
        RequiredTools: [ProductTool, AnalysisTool, PriceTool],
        Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "One or more exact product barcodes.",
            ["days"] = "Movement window for the stock analysis, 1-365."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Ürün ve stok özeti için barkod gerekiyor.");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var barcode in barcodes)
        {
            var productExecution = await WorkflowData.Execute(context, ProductTool, new { barcode }, cancellationToken);
            var analysisExecution = await WorkflowData.Execute(context, AnalysisTool, new
            {
                barcode,
                days = WorkflowData.Integer(context, "days", 30, 1, 365),
                outgoing_type_code = (string?)null,
                incoming_type_code = (string?)null
            }, cancellationToken);
            var priceExecution = await WorkflowData.Execute(context, PriceTool, new { barcode, offset = 0, limit = 500 }, cancellationToken);
            if (productExecution is null) return WorkflowData.Missing(ProductTool);
            if (analysisExecution is null) return WorkflowData.Missing(AnalysisTool);
            if (priceExecution is null) return WorkflowData.Missing(PriceTool);
            if (!productExecution.IsSuccess || !analysisExecution.IsSuccess || !priceExecution.IsSuccess)
                return WorkflowData.Failed("Ürün ve stok özeti alınamadı.");
            if (!productExecution.Output.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True ||
                !productExecution.Output.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object)
                continue;
            var summary = product.TryGetProperty("summary", out var productSummary) ? productSummary : product;
            var latestPrice = priceExecution.Output.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array
                ? points.EnumerateArray().Where(point => point.ValueKind == JsonValueKind.Object).LastOrDefault()
                : default;
            rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["medicine_name"] = WorkflowData.Text(summary, "name"),
                ["barcode"] = barcode,
                ["active_ingredient"] = WorkflowData.Text(summary, "active_ingredient"),
                ["company"] = WorkflowData.Text(summary, "company"),
                ["is_on_market"] = WorkflowData.Text(summary, "is_on_market"),
                ["shelf_stock_quantity"] = WorkflowData.Text(analysisExecution.Output, "shelf_stock_quantity"),
                ["consigned_stock_quantity"] = WorkflowData.Text(analysisExecution.Output, "consigned_stock_quantity"),
                ["sale_price"] = latestPrice.ValueKind == JsonValueKind.Object ? WorkflowData.Text(latestPrice, "sale_price") : string.Empty,
                ["purchase_price"] = WorkflowData.Text(summary, "purchase_price"),
                ["currency"] = WorkflowData.Text(summary, "currency")
            });
        }
        var message = rows.Count switch
        {
            0 => "Barkodlar için ürün ve stok bilgisi bulunamadı.",
            1 => $"{rows[0].GetValueOrDefault("medicine_name", "Ürün")} için raf stoku {rows[0].GetValueOrDefault("shelf_stock_quantity", "0")} kutu, yoldaki stok {rows[0].GetValueOrDefault("consigned_stock_quantity", "0")} kutu.",
            _ => $"{rows.Count.ToString(CultureInfo.InvariantCulture)} ürün için birleşik ürün ve stok özeti hazırlandı."
        };
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                total_count = rows.Count,
                visible_columns = new[] { "medicine_name", "barcode", "is_on_market", "shelf_stock_quantity", "consigned_stock_quantity", "sale_price", "purchase_price" }
            }));
    }
}
