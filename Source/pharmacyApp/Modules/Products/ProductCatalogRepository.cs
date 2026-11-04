using Npgsql;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Products;

public sealed record ProductCatalogQuery(
    string Search = "",
    string ProductPrefix = "",
    string Ingredient = "",
    string Prescription = "",
    bool OnlyInStock = false,
    int? StockMinimum = null,
    int? StockMaximum = null,
    int? MarketStatus = null,
    bool RequireBarcode = false,
    string? HeaderField = null,
    string? HeaderValue = null,
    string SortField = "Name",
    bool SortDescending = false,
    int Offset = 0,
    int Limit = 300,
    IReadOnlyList<string>? Barcodes = null,
    bool OnlyWithMovements = false);

public sealed record ProductCatalogPage(IReadOnlyList<ProductCatalogItem> Items, int TotalCount, bool HasMore);

public sealed record ProductBarcodePage(IReadOnlyList<string> Barcodes, int TotalCount, bool HasMore);

public sealed record ProductCatalogFilterOptions(
    IReadOnlyList<string> ProductPrefixes,
    IReadOnlyList<string> Ingredients,
    IReadOnlyList<string> Prescriptions,
    bool StockDataAvailable);

public sealed record ProductPriceChangeFrequency(
    string Barcode, string MedicineName, int ChangeCount, DateTime FirstChange, DateTime LastChange);

public sealed class ProductCatalogRepository
{
    private const string ActiveCampaignPredicate = "(c.start_date IS NULL OR c.start_date <= CURRENT_DATE) AND (c.end_date IS NULL OR c.end_date >= CURRENT_DATE)";
    private const string ShelfStockExpression = "GREATEST(COALESCE(a.quantity, 0) - COALESCE(a.consignment_quantity, 0), 0)";

    private const string HistoryQuery = """
        SELECT effective_date, sale_price, warehouse_price, manufacturer_price,
               public_price, public_paid_price, price_difference, discount_rate, vat_rate
        FROM public."barcodePriceHistory"
        WHERE record_reference_no = @record_id
        ORDER BY effective_date
        """;

    public async Task<ProductCatalogPage> LoadPageAsync(ProductCatalogQuery query, CancellationToken cancellationToken)
    {
        ValidateQuery(query);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var hasStockTable = await HasTableAsync(connection, "barcodeActive", cancellationToken);
        var where = BuildWhereClause(query, hasStockTable);

        var countSql = $"""
            SELECT COUNT(*)
            FROM public.barcode b
            {(hasStockTable ? "LEFT JOIN public.\"barcodeActive\" a ON a.barcode = b.barcode" : string.Empty)}
            {where}
            """;
        await using var countCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(countSql, connection);
        AddQueryParameters(countCommand, query);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        var products = new List<ProductCatalogItem>(Math.Min(query.Limit, totalCount));
        var catalogSql = $"""
            {BuildCatalogSelect(hasStockTable)}
            {where}
            ORDER BY {BuildOrderBy(query, hasStockTable)}
            OFFSET @offset LIMIT @limit
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(catalogSql, connection);
        AddQueryParameters(command, query);
        command.Parameters.AddWithValue("offset", query.Offset);
        command.Parameters.AddWithValue("limit", query.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) products.Add(ReadProduct(reader));

        return new ProductCatalogPage(products, totalCount, query.Offset + products.Count < totalCount);
    }

    public async Task<ProductBarcodePage> LoadBarcodePageAsync(
        ProductCatalogQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var hasStockTable = await HasTableAsync(connection, "barcodeActive", cancellationToken);
        var where = BuildBarcodeWhereClause(query, hasStockTable);

        var countSql = $"""
            SELECT COUNT(*)
            FROM public.barcode b
            {(hasStockTable ? "LEFT JOIN public.\"barcodeActive\" a ON a.barcode = b.barcode" : string.Empty)}
            {where}
            """;
        await using var countCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(countSql, connection);
        AddQueryParameters(countCommand, query);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        var sql = $"""
            SELECT btrim(b.barcode)
            FROM public.barcode b
            LEFT JOIN public."barcodePrices" p ON p.record_reference_no = b.record_id
            {(hasStockTable ? "LEFT JOIN public.\"barcodeActive\" a ON a.barcode = b.barcode" : string.Empty)}
            {where}
            ORDER BY {BuildOrderBy(query, hasStockTable)}
            OFFSET @offset LIMIT @limit
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        AddQueryParameters(command, query);
        command.Parameters.AddWithValue("offset", query.Offset);
        command.Parameters.AddWithValue("limit", query.Limit);
        var barcodes = new List<string>(Math.Min(query.Limit, totalCount));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) barcodes.Add(reader.GetString(0));

        return new ProductBarcodePage(barcodes, totalCount, query.Offset + barcodes.Count < totalCount);
    }

    public async Task<ProductCatalogItem?> LoadByRecordIdAsync(int recordId, CancellationToken cancellationToken)
    {
        if (recordId < 1) return null;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var hasStockTable = await HasTableAsync(connection, "barcodeActive", cancellationToken);
        var sql = $"""
            {BuildCatalogSelect(hasStockTable)}
            WHERE b.record_id = @record_id
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("record_id", recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProduct(reader) : null;
    }

    public async Task<ProductCatalogItem?> LoadByBarcodeAsync(string? barcode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var hasStockTable = await HasTableAsync(connection, "barcodeActive", cancellationToken);
        var sql = $"""
            {BuildCatalogSelect(hasStockTable)}
            WHERE b.barcode = @barcode
            ORDER BY b.record_id
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("barcode", barcode.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProduct(reader) : null;
    }

    public async Task<ProductCatalogFilterOptions> LoadFilterOptionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var prefixes = await LoadStringsAsync(connection, """
            SELECT DISTINCT btrim(product_prefix)
            FROM public.barcode
            WHERE NULLIF(btrim(product_prefix), '') IS NOT NULL
            ORDER BY 1
            """, cancellationToken);
        var ingredients = await LoadStringsAsync(connection, """
            SELECT DISTINCT btrim(ingredient)
            FROM public.barcode b
            CROSS JOIN LATERAL unnest(string_to_array(COALESCE(b.active_ingredient, ''), ',')) ingredient
            WHERE NULLIF(btrim(ingredient), '') IS NOT NULL
            ORDER BY 1
            """, cancellationToken);
        var prescriptions = await LoadStringsAsync(connection, """
            SELECT DISTINCT btrim(prescription_type_name)
            FROM public.barcode
            WHERE NULLIF(btrim(prescription_type_name), '') IS NOT NULL
            ORDER BY 1
            """, cancellationToken);
        var stockDataAvailable = await HasTableAsync(connection, "barcodeActive", cancellationToken);

        return new ProductCatalogFilterOptions(prefixes, ingredients, prescriptions, stockDataAvailable);
    }

    public async Task<IReadOnlyList<PriceHistoryPoint>> LoadPriceHistoryAsync(int recordId, CancellationToken cancellationToken)
    {
        var history = new List<PriceHistoryPoint>();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(HistoryQuery, connection);
        command.Parameters.AddWithValue("record_id", recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new PriceHistoryPoint
            {
                EffectiveDate = reader.GetDateTime(0),
                SalePrice = GetDecimal(reader, 1),
                WarehousePrice = GetDecimal(reader, 2),
                ManufacturerPrice = GetDecimal(reader, 3),
                PublicPrice = GetDecimal(reader, 4),
                PublicPaidPrice = GetDecimal(reader, 5),
                PriceDifference = GetDecimal(reader, 6),
                DiscountRate = GetDecimal(reader, 7),
                VatRate = GetDecimal(reader, 8)
            });
        }
        return history;
    }

    public async Task<IReadOnlyList<ProductPriceChangeFrequency>> LoadPriceChangeFrequencyAsync(
        DateOnly start, DateOnly end, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH ordered AS (
                SELECT record_reference_no, effective_date, sale_price,
                       LAG(sale_price) OVER (PARTITION BY record_reference_no ORDER BY effective_date) AS previous_sale_price
                FROM public."barcodePriceHistory"
            ), changed AS (
                SELECT record_reference_no, effective_date
                FROM ordered
                WHERE effective_date >= @start_date AND effective_date < @end_date
                  AND previous_sale_price IS NOT NULL AND sale_price IS DISTINCT FROM previous_sale_price
            )
            SELECT b.barcode, b.medicine_name, COUNT(*)::integer, MIN(changed.effective_date), MAX(changed.effective_date)
            FROM changed
            JOIN public.barcode b ON b.record_id = changed.record_reference_no
            WHERE b.market_status = 1 AND NULLIF(BTRIM(b.barcode), '') IS NOT NULL
            GROUP BY b.barcode, b.medicine_name
            ORDER BY COUNT(*) DESC, b.medicine_name ASC
            LIMIT @limit
            """;
        var rows = new List<ProductPriceChangeFrequency>();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("start_date", start.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("end_date", end.AddDays(1).ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new ProductPriceChangeFrequency(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetDateTime(3), reader.GetDateTime(4)));
        return rows;
    }

    public async Task<ProductDetailPayload> LoadProductDetailsAsync(int recordId, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT b.photo, b.warnings::text, b.sut_summary::text, b.medula_info::text, b.medula_details::text,
                   d.kub, d.kit,
                   COALESCE((
                       SELECT jsonb_agg(jsonb_build_object(
                           'header', icons.warning_header,
                           'source_url', icons.source_url,
                           'icon_base64', encode(icons.icon, 'base64')))
                       FROM public."warningIcons" icons
                       JOIN LATERAL jsonb_array_elements(
                           COALESCE(b.warnings->'urunUyariIcerik', '[]'::jsonb)
                       ) warning ON warning->>'baslik' = icons.warning_header
                                AND COALESCE(NULLIF(warning->>'resim_url', ''), NULLIF(warning->>'resim', '')) = icons.source_url
                   ), '[]'::jsonb)::text AS warning_icons
            FROM public.barcode b
            LEFT JOIN public.documents d ON d.barcode = b.barcode
            WHERE b.record_id = @record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("record_id", recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new ProductDetailPayload([], null, null, [], null, null, null);

        var warningsJson = GetString(reader, 1);
        return new ProductDetailPayload(
            reader.IsDBNull(0) ? [] : reader.GetFieldValue<byte[][]>(0),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6),
            ProductWarningFormatter.Parse(warningsJson, GetString(reader, 7)),
            GetString(reader, 2),
            GetString(reader, 3),
            GetString(reader, 4));
    }

    public async Task<IReadOnlyList<ProductCatalogItem>> LoadSgkEquivalentsAsync(
        string? equivalentGroupCode,
        int excludedRecordId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(equivalentGroupCode)) return [];

        var page = await LoadPageAsync(new ProductCatalogQuery(
            HeaderField: "EquivalentGroupCode",
            HeaderValue: equivalentGroupCode.Trim(),
            Limit: 1000), cancellationToken);
        return page.Items.Where(product => product.RecordId != excludedRecordId).ToList();
    }

    public async Task<IReadOnlyList<ProductCatalogItem>> LoadGeneralEquivalentsAsync(
        IReadOnlyList<string> barcodes,
        int excludedRecordId,
        CancellationToken cancellationToken)
    {
        var normalized = barcodes.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct().ToArray();
        if (normalized.Length == 0) return [];

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var hasStockTable = await HasTableAsync(connection, "barcodeActive", cancellationToken);
        var sql = $"""
            {BuildCatalogSelect(hasStockTable)}
            WHERE b.barcode = ANY(@barcodes) AND b.record_id <> @excluded_record_id
            ORDER BY COALESCE(p.price_difference, 999999999), COALESCE(b.medicine_name, b.brand_name, b.barcode)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("barcodes", normalized);
        command.Parameters.AddWithValue("excluded_record_id", excludedRecordId);
        var products = new List<ProductCatalogItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) products.Add(ReadProduct(reader));
        return products;
    }

    public async Task<IReadOnlyList<QrCodeDisplayItem>> LoadQrCodesAsync(
        string? barcode,
        bool includeInactive,
        DateTime inactiveSince,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return [];
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var sql = includeInactive ? """
            SELECT q.qr_code, q.expiry_date, q.entry_cost, movement.movement_date,
                   q.exit_price,
                   CASE WHEN q.exit_price IS NULL OR q.entry_cost IS NULL THEN NULL ELSE q.exit_price - q.entry_cost END AS profit
            FROM public."qrInactive" q
            LEFT JOIN LATERAL (
                SELECT m.movement_date
                FROM public."qrMovements" m
                JOIN public."qrMovementTypes" movement_type
                  ON movement_type.record_id = m.movement_type
                WHERE m.qr_code = q.qr_code AND movement_type.code = ANY(@movement_types)
                ORDER BY m.movement_date DESC, m.record_id DESC LIMIT 1
            ) movement ON TRUE
            WHERE q.barcode = @barcode AND movement.movement_date >= @inactive_since
            ORDER BY movement.movement_date DESC NULLS LAST, q.qr_code
            """ : """
            SELECT q.qr_code, q.expiry_date, q.purchase_cost, movement.movement_date,
                   NULL::numeric AS exit_price, NULL::numeric AS profit
            FROM public."qrActive" q
            LEFT JOIN LATERAL (
                SELECT m.movement_date
                FROM public."qrMovements" m
                JOIN public."qrMovementTypes" movement_type
                  ON movement_type.record_id = m.movement_type
                WHERE m.qr_code = q.qr_code AND movement_type.code = ANY(@movement_types)
                ORDER BY m.movement_date DESC, m.record_id DESC LIMIT 1
            ) movement ON TRUE
            WHERE q.barcode = @barcode
            ORDER BY q.expiry_date, q.qr_code
            """;
        var movementTypes = includeInactive
            ? new[] { "DEP_RETURN", "FIRM_RETURN", "TRANSF_OUT", "PERAK_OUT", "PRESC_OUT", "DESTRUCTION", "MAN_OUT" }
            : new[] { "DEP_IN", "FIRM_IN", "TRANSF_IN", "PERAK_RETURN", "PRESC_RETURN", "MAN_IN", "QR_ADD" };
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("barcode", barcode.Trim());
        command.Parameters.AddWithValue("movement_types", movementTypes);
        if (includeInactive) command.Parameters.AddWithValue("inactive_since", inactiveSince);
        var rows = new List<QrCodeDisplayItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new QrCodeDisplayItem
            {
                QrCode = reader.GetString(0),
                ExpiryDate = reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                PurchasePrice = GetDecimal(reader, 2),
                MovementDate = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                ExitPrice = GetDecimal(reader, 4),
                Profit = GetDecimal(reader, 5)
            });
        }
        return rows;
    }

    private static string BuildCatalogSelect(bool hasStockTable) => $"""
        SELECT
            b.record_id,
            b.medicine_name,
            b.brand_name,
            b.old_name,
            b.barcode,
            b.product_prefix,
            b.product_type_name,
            b.company_name,
            b.origin,
            b.equivalent_group_code,
            b.equivalent_classification,
            b.active_ingredient,
            b.atc_code,
            b.sgk_code,
            b.indication_summary,
            b.dosage_instructions,
            b.package_description,
            b.pharmaceutical_form,
            b.prescription_type_name,
            b.pregnancy_category,
            b.shelf_life,
            b.generic_code,
            b.reimbursement_status_description,
            NULL::text AS documents,
            b.sut_rules::text,
            NULL::bytea AS photo,
            p.sale_price,
            p.warehouse_price,
            p.price_difference,
            p.currency,
            EXISTS (
                SELECT 1
                FROM public."barcodeCampaign" c
                WHERE c.record_reference_no = b.record_id
                  AND {ActiveCampaignPredicate}
            ) AS has_active_campaign,
            (
                SELECT MIN(
                    p.warehouse_price * c.purchase_quantity /
                    NULLIF(c.purchase_quantity + COALESCE(c.bonus_quantity, 0), 0)
                )
                FROM public."barcodeCampaign" c
                WHERE c.record_reference_no = b.record_id
                  AND {ActiveCampaignPredicate}
                  AND c.purchase_quantity > 0
                  AND COALESCE(c.bonus_quantity, 0) > 0
            ) AS campaign_purchase_price,
            {(hasStockTable ? ShelfStockExpression : "NULL::integer")} AS stock_quantity,
            p.manufacturer_price,
            p.vat_rate,
            b.market_status,
            COALESCE(b.all_equivalents, ARRAY[]::varchar[]) AS all_equivalents
        FROM public.barcode b
        LEFT JOIN public."barcodePrices" p ON p.record_reference_no = b.record_id
        {(hasStockTable ? "LEFT JOIN public.\"barcodeActive\" a ON a.barcode = b.barcode" : string.Empty)}
        """;

    private static string BuildWhereClause(ProductCatalogQuery query, bool hasStockTable)
    {
        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            predicates.Add("""
                (COALESCE(b.medicine_name, '') ILIKE @search OR
                 COALESCE(b.brand_name, '') ILIKE @search OR
                 COALESCE(b.old_name, '') ILIKE @search OR
                 COALESCE(b.barcode, '') ILIKE @barcode_search OR
                 COALESCE(b.active_ingredient, '') ILIKE @search OR
                 COALESCE(b.company_name, '') ILIKE @search)
                """);
        }
        if (!string.IsNullOrWhiteSpace(query.ProductPrefix))
            predicates.Add("btrim(COALESCE(b.product_prefix, '')) ILIKE @product_prefix");
        if (!string.IsNullOrWhiteSpace(query.Ingredient))
        {
            predicates.Add("""
                EXISTS (
                    SELECT 1
                    FROM unnest(string_to_array(COALESCE(b.active_ingredient, ''), ',')) ingredient
                    WHERE btrim(ingredient) ILIKE @ingredient)
                """);
        }
        if (!string.IsNullOrWhiteSpace(query.Prescription))
            predicates.Add("btrim(COALESCE(b.prescription_type_name, '')) ILIKE @prescription");
        if (query.OnlyInStock)
            predicates.Add(hasStockTable ? $"{ShelfStockExpression} > 0" : "FALSE");
        if (query.OnlyWithMovements)
            predicates.Add("""
                EXISTS (SELECT 1 FROM (
                    SELECT qr_code FROM public."qrActive" WHERE barcode=b.barcode
                    UNION ALL SELECT qr_code FROM public."qrInactive" WHERE barcode=b.barcode
                    UNION ALL SELECT qr_code FROM public."qrHanged" WHERE barcode=b.barcode
                    UNION ALL SELECT b.barcode
                ) codes JOIN public."qrMovements" movement ON movement.qr_code=codes.qr_code
                WHERE movement.movement_date <= (CURRENT_TIMESTAMP AT TIME ZONE 'Europe/Istanbul'))
                """);
        if (query.StockMinimum.HasValue)
            predicates.Add(hasStockTable ? $"{ShelfStockExpression} >= @stock_minimum" : "FALSE");
        if (query.StockMaximum.HasValue)
            predicates.Add(hasStockTable ? $"{ShelfStockExpression} <= @stock_maximum" : "FALSE");
        if (query.MarketStatus.HasValue)
            predicates.Add("b.market_status = @market_status");
        if (query.RequireBarcode)
            predicates.Add("NULLIF(btrim(COALESCE(b.barcode, '')), '') IS NOT NULL");
        if (query.Barcodes is { Count: > 0 })
            predicates.Add("b.barcode = ANY(@barcodes)");

        if (!string.IsNullOrWhiteSpace(query.HeaderField) && !string.IsNullOrWhiteSpace(query.HeaderValue))
        {
            var column = query.HeaderField switch
            {
                "ProductPrefix" => "b.product_prefix",
                "PharmaceuticalForm" => "b.pharmaceutical_form",
                "SgkCode" => "b.sgk_code",
                "AtcCode" => "b.atc_code",
                "GenericCode" => "b.generic_code",
                "CompanyName" => "b.company_name",
                "BrandName" => "b.brand_name",
                "EquivalentGroupCode" => "b.equivalent_group_code",
                _ => null
            };
            if (column is not null)
                predicates.Add($"btrim(COALESCE({column}, '')) ILIKE @header_value");
        }

        return predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates);
    }

    private static string BuildBarcodeWhereClause(ProductCatalogQuery query, bool hasStockTable)
    {
        const string hasBarcode = "NULLIF(btrim(COALESCE(b.barcode, '')), '') IS NOT NULL";
        var where = BuildWhereClause(query, hasStockTable);
        return string.IsNullOrEmpty(where) ? $"WHERE {hasBarcode}" : $"{where} AND {hasBarcode}";
    }

    private static void ValidateQuery(ProductCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0) throw new ArgumentOutOfRangeException(nameof(query), "Offset cannot be negative.");
        if (query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query), "Limit must be between 1 and 1000.");
        if (query.Barcodes is { Count: > 100 }) throw new ArgumentOutOfRangeException(nameof(query), "At most 100 barcodes can be filtered.");
    }

    private static string BuildOrderBy(ProductCatalogQuery query, bool hasStockTable)
    {
        var direction = query.SortDescending ? "DESC" : "ASC";
        var name = "COALESCE(NULLIF(b.medicine_name, ''), NULLIF(b.brand_name, ''), b.barcode)";
        return query.SortField switch
        {
            "Price" => $"p.warehouse_price {direction} NULLS LAST, {name} ASC, b.record_id ASC",
            "Stock" when hasStockTable => $"{ShelfStockExpression} {direction}, {name} ASC, b.record_id ASC",
            "StockValue" when hasStockTable => $"(COALESCE(p.warehouse_price, 0) * {ShelfStockExpression}) {direction}, {name} ASC, b.record_id ASC",
            _ => $"{name} {direction} NULLS LAST, b.record_id {direction}"
        };
    }

    private static void AddQueryParameters(NpgsqlCommand command, ProductCatalogQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            command.Parameters.AddWithValue("search", $"%{query.Search.Trim()}%");
            command.Parameters.AddWithValue("barcode_search", $"{query.Search.Trim()}%");
        }
        if (query.MarketStatus.HasValue)
            command.Parameters.AddWithValue("market_status", query.MarketStatus.Value);
        if (query.Barcodes is { Count: > 0 })
            command.Parameters.AddWithValue("barcodes", query.Barcodes.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
        if (query.StockMinimum.HasValue)
            command.Parameters.AddWithValue("stock_minimum", query.StockMinimum.Value);
        if (query.StockMaximum.HasValue)
            command.Parameters.AddWithValue("stock_maximum", query.StockMaximum.Value);
        if (!string.IsNullOrWhiteSpace(query.ProductPrefix))
            command.Parameters.AddWithValue("product_prefix", query.ProductPrefix.Trim());
        if (!string.IsNullOrWhiteSpace(query.Ingredient))
            command.Parameters.AddWithValue("ingredient", query.Ingredient.Trim());
        if (!string.IsNullOrWhiteSpace(query.Prescription))
            command.Parameters.AddWithValue("prescription", query.Prescription.Trim());
        if (!string.IsNullOrWhiteSpace(query.HeaderField) && !string.IsNullOrWhiteSpace(query.HeaderValue))
            command.Parameters.AddWithValue("header_value", query.HeaderValue.Trim());
    }

    private static ProductCatalogItem ReadProduct(NpgsqlDataReader reader) => new()
    {
        RecordId = reader.GetInt32(0),
        MedicineName = GetString(reader, 1),
        BrandName = GetString(reader, 2),
        OldName = GetString(reader, 3),
        Barcode = GetString(reader, 4),
        ProductPrefix = GetString(reader, 5),
        ProductTypeName = GetString(reader, 6),
        CompanyName = GetString(reader, 7),
        Origin = GetString(reader, 8),
        EquivalentGroupCode = GetString(reader, 9),
        EquivalentClassification = GetString(reader, 10),
        ActiveIngredient = GetString(reader, 11),
        AtcCode = GetString(reader, 12),
        SgkCode = GetString(reader, 13),
        IndicationSummary = GetString(reader, 14),
        DosageInstructions = GetString(reader, 15),
        PackageDescription = GetString(reader, 16),
        PharmaceuticalForm = GetString(reader, 17),
        PrescriptionTypeName = GetString(reader, 18),
        PregnancyCategory = GetString(reader, 19),
        ShelfLife = GetString(reader, 20),
        GenericCode = GetString(reader, 21),
        ReimbursementStatusDescription = GetString(reader, 22),
        Documents = GetString(reader, 23),
        SutRules = GetString(reader, 24),
        SalePrice = GetDecimal(reader, 26),
        WarehousePrice = GetDecimal(reader, 27),
        PriceDifference = GetDecimal(reader, 28),
        Currency = GetString(reader, 29),
        HasActiveCampaign = reader.GetBoolean(30),
        CampaignPurchasePrice = GetDecimal(reader, 31),
        StockQuantity = reader.IsDBNull(32) ? null : reader.GetInt32(32),
        ManufacturerPrice = GetDecimal(reader, 33),
        VatRate = GetDecimal(reader, 34),
        MarketStatus = reader.IsDBNull(35) ? null : reader.GetInt32(35),
        AllEquivalentBarcodes = reader.IsDBNull(36) ? [] : reader.GetFieldValue<string[]>(36)
    };

    private static async Task<IReadOnlyList<string>> LoadStringsAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<bool> HasTableAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("SELECT to_regclass(@qualified_name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("qualified_name", $"public.\"{tableName}\"");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static string? GetString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? GetDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

}
