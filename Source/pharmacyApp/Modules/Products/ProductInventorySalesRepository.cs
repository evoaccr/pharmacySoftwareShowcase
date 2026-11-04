using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Products;

public sealed record ProductInventorySalesRow(string Barcode, string MedicineName, decimal ShelfStock, decimal SoldQuantity, decimal Revenue);
public sealed record ProductInventorySalesSnapshot(IReadOnlyList<ProductInventorySalesRow> Rows, int CatalogCount);

/// <summary>One read-only snapshot for catalogue replenishment; no payment/card/price/campaign projections.</summary>
public sealed class ProductInventorySalesRepository
{
    public async Task<ProductInventorySalesSnapshot> LoadAsync(DateOnly start, DateOnly end, int scanLimit, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // Same shelf-stock and active-product expressions as ProductCatalogRepository.
        // RLS, configured table names and cancellation remain effective. Every sales QR row is one pack.
        const string sql = """
            WITH catalog AS MATERIALIZED (
                SELECT b.barcode,
                       COALESCE(NULLIF(b.medicine_name,''), NULLIF(b.brand_name,''), NULLIF(b.old_name,''), b.barcode) AS name,
                       GREATEST(COALESCE(a.quantity,0)-COALESCE(a.consignment_quantity,0),0) AS stock,
                       COUNT(*) OVER()::integer AS catalog_count
                FROM public.barcode b
                LEFT JOIN public."barcodeActive" a ON a.barcode=b.barcode
                WHERE b.market_status=1 AND NULLIF(btrim(b.barcode),'') IS NOT NULL
                ORDER BY b.barcode LIMIT @scan_limit
            ), sold AS (
                SELECT item.barcode, COUNT(*)::numeric AS quantity, SUM(item.exit_price) AS revenue
                FROM public."salesQrItems" item
                JOIN public.sales sale ON sale.record_id=item.sale_id
                JOIN catalog ON catalog.barcode=item.barcode
                WHERE sale."timestamp">=@start AND sale."timestamp"<@end
                GROUP BY item.barcode
            )
            SELECT catalog.barcode, catalog.name, catalog.stock, COALESCE(sold.quantity,0),
                   COALESCE(sold.revenue,0), catalog.catalog_count
            FROM catalog LEFT JOIN sold ON sold.barcode=catalog.barcode
            ORDER BY catalog.barcode
            """;
        await using var command = DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("start", start.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("end", end.AddDays(1).ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("scan_limit", scanLimit);
        var rows = new List<ProductInventorySalesRow>();
        var total = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            total = reader.GetInt32(5);
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        }
        return new(rows, total);
    }
}
