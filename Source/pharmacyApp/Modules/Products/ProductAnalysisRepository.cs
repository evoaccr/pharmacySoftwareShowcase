using Npgsql;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Products;

public sealed class ProductAnalysisRepository
{
    private const string ResolvedMovementBarcode = "COALESCE(active_qr.barcode, inactive_qr.barcode, public.pharmacy_qr_barcode(movement.qr_code))";

    public async Task<ProductAnalysisData> LoadAsync(
        int productRecordId,
        string? barcode,
        CancellationToken cancellationToken = default) =>
        await LoadCoreAsync(productRecordId, barcode, ensureSchema: true, cancellationToken);

    public async Task<ProductAnalysisData> LoadReadOnlyAsync(
        int productRecordId,
        string? barcode,
        CancellationToken cancellationToken = default) =>
        await LoadCoreAsync(productRecordId, barcode, ensureSchema: false, cancellationToken);

    private static async Task<ProductAnalysisData> LoadCoreAsync(
        int productRecordId,
        string? barcode,
        bool ensureSchema,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (ensureSchema)
            await ProductAnalysisSchema.EnsureAsync(connection, cancellationToken);

        var (wantedMonths, shelfStock, consignedStock) = await LoadStockAndSettingAsync(
            connection, productRecordId, barcode, cancellationToken);
        var salesInLastNinetyDays = await CountSalesInLastNinetyDaysAsync(
            connection, barcode, cancellationToken);
        var movementTypes = await LoadMovementTypesAsync(connection, cancellationToken);
        var dailyMovements = await LoadDailyMovementsAsync(connection, barcode, cancellationToken);

        return new ProductAnalysisData(
            ProductAnalysisCalculations.AverageMonthlySales(salesInLastNinetyDays),
            wantedMonths,
            shelfStock,
            consignedStock,
            movementTypes,
            dailyMovements);
    }

    public async Task<decimal> LoadDefaultWantedStockMonthsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ProductAnalysisSchema.EnsureAsync(connection, cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT wanted_stock_months
            FROM public."barcodeAnalysis"
            WHERE record_reference_no IS NULL
            LIMIT 1
            """, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ProductAnalysisSettingItem>> SearchProductsAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ProductAnalysisSchema.EnsureAsync(connection, cancellationToken);
        const string sql = """
            SELECT b.record_id,
                   COALESCE(NULLIF(b.medicine_name, ''), NULLIF(b.brand_name, ''), 'Adsız ürün'),
                   COALESCE(b.barcode, ''),
                   analysis.wanted_stock_months
            FROM public.barcode b
            LEFT JOIN public."barcodeAnalysis" analysis
              ON analysis.record_reference_no = b.record_id
            WHERE NULLIF(btrim(@search), '') IS NULL
               OR COALESCE(b.medicine_name, '') ILIKE @pattern
               OR COALESCE(b.brand_name, '') ILIKE @pattern
               OR COALESCE(b.barcode, '') ILIKE @prefix
            ORDER BY COALESCE(NULLIF(b.medicine_name, ''), NULLIF(b.brand_name, ''), b.barcode)
            LIMIT 80
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var normalized = search?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("search", normalized);
        command.Parameters.AddWithValue("pattern", $"%{normalized}%");
        command.Parameters.AddWithValue("prefix", $"{normalized}%");
        var items = new List<ProductAnalysisSettingItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ProductAnalysisSettingItem(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3)));
        }
        return items;
    }

    public async Task<IReadOnlyList<ProductAnalysisSettingItem>> LoadOverridesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ProductAnalysisSchema.EnsureAsync(connection, cancellationToken);
        const string sql = """
            SELECT b.record_id,
                   COALESCE(NULLIF(b.medicine_name, ''), NULLIF(b.brand_name, ''), 'Adsız ürün'),
                   COALESCE(b.barcode, ''),
                   analysis.wanted_stock_months
            FROM public."barcodeAnalysis" analysis
            JOIN public.barcode b ON b.record_id = analysis.record_reference_no
            ORDER BY COALESCE(NULLIF(b.medicine_name, ''), NULLIF(b.brand_name, ''), b.barcode)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var items = new List<ProductAnalysisSettingItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new ProductAnalysisSettingItem(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3)));
        return items;
    }

    public async Task SetDefaultForAllAsync(decimal wantedStockMonths, CancellationToken cancellationToken = default)
    {
        ValidateWantedStockMonths(wantedStockMonths);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ProductAnalysisSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public."barcodeAnalysis"
            SET wanted_stock_months = @months, updated_at = CURRENT_TIMESTAMP
            WHERE record_reference_no IS NULL
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("months", wantedStockMonths);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clear = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                         "DELETE FROM public.\"barcodeAnalysis\" WHERE record_reference_no IS NOT NULL",
                         connection, transaction))
            await clear.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveOverrideAsync(
        int productRecordId,
        decimal wantedStockMonths,
        CancellationToken cancellationToken = default)
    {
        ValidateWantedStockMonths(wantedStockMonths);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ProductAnalysisSchema.EnsureAsync(connection, cancellationToken);
        const string sql = """
            INSERT INTO public."barcodeAnalysis" (record_reference_no, wanted_stock_months)
            VALUES (@record_id, @months)
            ON CONFLICT (record_reference_no) WHERE record_reference_no IS NOT NULL
            DO UPDATE SET wanted_stock_months = EXCLUDED.wanted_stock_months,
                          updated_at = CURRENT_TIMESTAMP
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("record_id", productRecordId);
        command.Parameters.AddWithValue("months", wantedStockMonths);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveOverrideAsync(int productRecordId, CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ProductAnalysisSchema.EnsureAsync(connection, cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            DELETE FROM public."barcodeAnalysis"
            WHERE record_reference_no = @record_id
            """, connection);
        command.Parameters.AddWithValue("record_id", productRecordId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(decimal WantedMonths, int ShelfStock, int ConsignedStock)> LoadStockAndSettingAsync(
        NpgsqlConnection connection,
        int productRecordId,
        string? barcode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(product_setting.wanted_stock_months, default_setting.wanted_stock_months, 1.5),
                   GREATEST(COALESCE(stock.quantity, 0) - COALESCE(stock.consignment_quantity, 0), 0),
                   COALESCE(stock.consignment_quantity, 0)
            FROM (SELECT 1) singleton
            LEFT JOIN public."barcodeAnalysis" product_setting
              ON product_setting.record_reference_no = @record_id
            LEFT JOIN public."barcodeAnalysis" default_setting
              ON default_setting.record_reference_no IS NULL
            LEFT JOIN public."barcodeActive" stock
              ON stock.barcode = @barcode
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("record_id", productRecordId);
        command.Parameters.AddWithValue("barcode", barcode?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (ProductAnalysisCalculations.DefaultWantedStockMonths, 0, 0);
        return (reader.GetDecimal(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<int> CountSalesInLastNinetyDaysAsync(
        NpgsqlConnection connection,
        string? barcode,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(DISTINCT movement.record_id)::integer
            FROM public."qrMovements" movement
            JOIN public."qrMovementTypes" movement_type
              ON movement_type.record_id = movement.movement_type
            LEFT JOIN public."qrActive" active_qr ON active_qr.qr_code = movement.qr_code
            LEFT JOIN public."qrInactive" inactive_qr ON inactive_qr.qr_code = movement.qr_code
            WHERE {ResolvedMovementBarcode} = @barcode
              AND movement_type.code IN ('PERAK_OUT', 'PRESC_OUT')
              AND movement.movement_date >= CURRENT_TIMESTAMP - INTERVAL '90 days'
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("barcode", barcode?.Trim() ?? string.Empty);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<ProductAnalysisMovementType>> LoadMovementTypesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT code, display_name, is_entry
            FROM public."qrMovementTypes"
            ORDER BY record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var types = new List<ProductAnalysisMovementType>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            types.Add(new ProductAnalysisMovementType(reader.GetString(0), pharmacyApp.Localization.UiText.Translate(reader.GetString(1)), reader.GetBoolean(2)));
        return types;
    }

    private static async Task<IReadOnlyList<ProductAnalysisMovementDay>> LoadDailyMovementsAsync(
        NpgsqlConnection connection,
        string? barcode,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT movement.movement_date::date AS movement_day,
                   movement_type.code,
                   COUNT(DISTINCT movement.record_id)::integer AS quantity
            FROM public."qrMovements" movement
            JOIN public."qrMovementTypes" movement_type
              ON movement_type.record_id = movement.movement_type
            LEFT JOIN public."qrActive" active_qr ON active_qr.qr_code = movement.qr_code
            LEFT JOIN public."qrInactive" inactive_qr ON inactive_qr.qr_code = movement.qr_code
            WHERE {ResolvedMovementBarcode} = @barcode
              AND movement.movement_date >= CURRENT_DATE - INTERVAL '364 days'
              AND movement.movement_date < CURRENT_DATE + INTERVAL '1 day'
            GROUP BY 1, 2
            ORDER BY 1, 2
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("barcode", barcode?.Trim() ?? string.Empty);
        var loaded = new Dictionary<DateTime, Dictionary<string, int>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var day = reader.GetDateTime(0);
            if (!loaded.TryGetValue(day, out var quantities))
            {
                quantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                loaded[day] = quantities;
            }
            quantities[reader.GetString(1)] = reader.GetInt32(2);
        }

        var start = DateTime.Today.AddDays(-364);
        return Enumerable.Range(0, 365)
            .Select(offset => start.AddDays(offset))
            .Select(day => new ProductAnalysisMovementDay(
                day,
                loaded.TryGetValue(day, out var quantities)
                    ? quantities
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static void ValidateWantedStockMonths(decimal value)
    {
        if (!ProductAnalysisCalculations.IsValidWantedStockMonths(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Stok süresi 0,5 ile 60 ay arasında ve 0,5'in katı olmalıdır.");
    }
}
