using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Takas;

public sealed class TakasListRepository
{
    public async Task<IReadOnlyList<TakasListItem>> LoadAsync(
        TakasListQuery query,
        CancellationToken cancellationToken)
    {
        if (query.StartDate >= query.EndDateExclusive)
            throw new InvalidOperationException("Başlangıç tarihi bitiş tarihinden sonra olamaz.");

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT transfer.record_id,
                   transfer.pharmacy_id,
                   COALESCE(pharmacy.pharmacy_name, transfer.destination_gln),
                   COALESCE(pharmacy.pharmacist_name, ''),
                   transfer.source_gln,
                   transfer.destination_gln,
                   transfer.direction,
                   transfer.status,
                   ((SELECT COUNT(*) FROM public."takasQrItems" item WHERE item.takas_id = transfer.record_id) +
                    (SELECT COUNT(*) FROM public."takasBarcodeItems" item WHERE item.takas_id = transfer.record_id))::integer,
                   transfer.debt_amount,
                   transfer.transfer_notification_id,
                   transfer.accept_notification_id,
                   transfer.cancel_notification_id,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''), transfer.user_no),
                   transfer.notes,
                   transfer.last_status_error,
                   transfer.created_at,
                   transfer.updated_at,
                   transfer.transferred_at,
                   transfer.accepted_at
            FROM public.takas transfer
            JOIN public.pharmacy pharmacy ON pharmacy.record_id = transfer.pharmacy_id
            LEFT JOIN public.users app_user ON app_user.user_id = transfer.user_no
            WHERE transfer.created_at >= @start_date
              AND transfer.created_at < @end_exclusive
              AND (@user_id = '' OR transfer.user_no = @user_id)
              AND (@pharmacy_id IS NULL OR transfer.pharmacy_id = @pharmacy_id)
              AND (@direction = '' OR transfer.direction = @direction)
              AND (@statuses & CASE transfer.status
                    WHEN 'pending_transfer' THEN 1
                    WHEN 'in_transit' THEN 2
                    WHEN 'partially_transferred' THEN 4
                    WHEN 'partially_accepted' THEN 8
                    WHEN 'accepted' THEN 16
                    WHEN 'partially_cancelled' THEN 32
                    WHEN 'cancelled' THEN 64
                    WHEN 'failed' THEN 128
                    WHEN 'reconciliation_required' THEN 256
                    ELSE 0 END) <> 0
              AND (@minimum_debt IS NULL OR transfer.debt_amount >= @minimum_debt)
              AND (@maximum_debt IS NULL OR transfer.debt_amount <= @maximum_debt)
              AND (@qr_code = '' OR EXISTS (
                    SELECT 1 FROM public."takasQrItems" qr
                    WHERE qr.takas_id = transfer.record_id AND qr.qr_code = @qr_code))
              AND (@barcode = '' OR EXISTS (
                    SELECT 1 FROM public."takasQrItems" qr
                    WHERE qr.takas_id = transfer.record_id AND qr.barcode = @barcode)
                  OR EXISTS (
                    SELECT 1 FROM public."takasBarcodeItems" barcode_item
                    WHERE barcode_item.takas_id = transfer.record_id AND barcode_item.barcode = @barcode))
              AND (@brand_name = '' OR EXISTS (
                    SELECT 1
                    FROM public."takasQrItems" qr
                    JOIN public.barcode product ON product.barcode = qr.barcode
                    WHERE qr.takas_id = transfer.record_id AND product.brand_name = @brand_name)
                  OR EXISTS (
                    SELECT 1
                    FROM public."takasBarcodeItems" barcode_item
                    JOIN public.barcode product ON product.barcode = barcode_item.barcode
                    WHERE barcode_item.takas_id = transfer.record_id AND product.brand_name = @brand_name))
            ORDER BY transfer.created_at DESC, transfer.record_id DESC
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("start_date", NpgsqlDbType.Timestamp, query.StartDate);
        command.Parameters.AddWithValue("end_exclusive", NpgsqlDbType.Timestamp, query.EndDateExclusive);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, query.UserId.Trim());
        command.Parameters.Add(new NpgsqlParameter("pharmacy_id", NpgsqlDbType.Integer)
            { Value = query.PharmacyId.HasValue ? query.PharmacyId.Value : DBNull.Value });
        command.Parameters.AddWithValue("direction", NpgsqlDbType.Varchar, query.Direction.Trim());
        command.Parameters.AddWithValue("statuses", NpgsqlDbType.Integer, (int)query.Statuses);
        command.Parameters.Add(new NpgsqlParameter("minimum_debt", NpgsqlDbType.Numeric)
            { Value = query.MinimumDebt.HasValue ? query.MinimumDebt.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("maximum_debt", NpgsqlDbType.Numeric)
            { Value = query.MaximumDebt.HasValue ? query.MaximumDebt.Value : DBNull.Value });
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, query.QrCode.Trim());
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, query.Barcode.Trim());
        command.Parameters.AddWithValue("brand_name", NpgsqlDbType.Varchar, query.BrandName.Trim());

        var result = new List<TakasListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TakasListItem
            {
                TakasId = reader.GetInt32(0),
                PharmacyId = reader.GetInt32(1),
                PharmacyName = reader.GetString(2),
                PharmacistName = reader.GetString(3),
                SourceGln = reader.GetString(4),
                DestinationGln = reader.GetString(5),
                Direction = reader.GetString(6),
                Status = reader.GetString(7),
                MedicineQuantity = reader.GetInt32(8),
                DebtAmount = reader.GetDecimal(9),
                TransferNotificationId = GetString(reader, 10),
                AcceptNotificationId = GetString(reader, 11),
                CancelNotificationId = GetString(reader, 12),
                UserName = GetString(reader, 13),
                Notes = GetString(reader, 14),
                LastStatusError = GetString(reader, 15),
                CreatedAt = reader.GetDateTime(16),
                UpdatedAt = reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                TransferredAt = reader.IsDBNull(18) ? null : reader.GetDateTime(18),
                AcceptedAt = reader.IsDBNull(19) ? null : reader.GetDateTime(19)
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<TakasListProductGroup>> LoadDetailsAsync(
        int takasId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH item_rows AS (
                SELECT item.qr_code AS item_code,
                       item.barcode,
                       item.serial_number,
                       item.batch_number,
                       item.expiry_date,
                       item.entry_cost,
                       item.debt_amount,
                       item.status,
                       COALESCE(item.last_status_result_code, item.transfer_result_code,
                                item.accept_result_code, item.cancel_result_code) AS result_code,
                       item.last_error,
                       TRUE AS is_serialized,
                       item.record_id
                FROM public."takasQrItems" item
                WHERE item.takas_id = @takas_id
                UNION ALL
                SELECT item.barcode,
                       item.barcode,
                       NULL::varchar,
                       NULL::varchar,
                       NULL::date,
                       item.entry_cost,
                       item.debt_amount,
                       item.status,
                       NULL::varchar,
                       NULL::text,
                       FALSE,
                       item.record_id
                FROM public."takasBarcodeItems" item
                WHERE item.takas_id = @takas_id
            )
            SELECT item.item_code,
                   item.barcode,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), product.barcode, item.barcode),
                   COALESCE(stock.quantity, 0),
                   item.serial_number, item.batch_number, item.expiry_date,
                   item.entry_cost, item.debt_amount, item.status,
                   item.result_code, item.last_error, item.is_serialized
            FROM item_rows item
            LEFT JOIN public.barcode product ON product.barcode = item.barcode
            LEFT JOIN public."barcodeActive" stock ON stock.barcode = item.barcode
            ORDER BY 3, item.barcode, item.record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        var rows = new List<TakasDetailRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TakasDetailRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                GetString(reader, 4), GetString(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.GetDecimal(8), reader.GetString(9),
                GetString(reader, 10), GetString(reader, 11), reader.GetBoolean(12)));
        }

        return rows.GroupBy(row => new { row.Barcode, row.ProductName })
            .Select(group =>
            {
                var product = new TakasListProductGroup
                {
                    Barcode = group.Key.Barcode,
                    ProductName = group.Key.ProductName,
                    StockQuantity = group.First().StockQuantity
                };
                foreach (var row in group)
                {
                    product.QrCodes.Add(new TakasListQrEntry(
                        row.QrCode, row.SerialNumber, row.BatchNumber, row.ExpiryDate,
                        row.EntryCost, row.DebtAmount, row.Status, row.ResultCode, row.LastError,
                        row.IsSerialized));
                }
                return product;
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<SaleProductSuggestion>> SearchProductsAsync(
        string search,
        CancellationToken cancellationToken)
    {
        var term = search.Trim();
        if (term.Length < 3) return [];
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH suggestions AS (
                SELECT product.barcode,
                       COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.old_name, ''),
                                NULLIF(product.brand_name, ''), product.barcode),
                       product.barcode, FALSE, COALESCE(stock.quantity, 0) > 0,
                       CASE WHEN product.barcode ILIKE @prefix THEN 0 ELSE 1 END
                FROM public.barcode product
                LEFT JOIN public."barcodeActive" stock ON stock.barcode = product.barcode
                WHERE NULLIF(product.barcode, '') IS NOT NULL
                  AND (product.barcode ILIKE @prefix OR COALESCE(product.medicine_name, '') ILIKE @contains
                       OR COALESCE(product.old_name, '') ILIKE @contains)
                UNION ALL
                SELECT '', product.brand_name, product.brand_name, TRUE,
                       BOOL_OR(COALESCE(stock.quantity, 0) > 0), 2
                FROM public.barcode product
                LEFT JOIN public."barcodeActive" stock ON stock.barcode = product.barcode
                WHERE NULLIF(product.brand_name, '') IS NOT NULL AND product.brand_name ILIKE @contains
                GROUP BY product.brand_name
            )
            SELECT * FROM suggestions ORDER BY 6, 2, 1 LIMIT 24
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Varchar, term + "%");
        command.Parameters.AddWithValue("contains", NpgsqlDbType.Varchar, "%" + term + "%");
        var result = new List<SaleProductSuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SaleProductSuggestion(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        return result;
    }

    public async Task<IReadOnlyList<TakasListUserFilterOption>> LoadUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT user_id,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''), user_id) || ' — ' || user_id
            FROM public.users
            ORDER BY first_name NULLS LAST, last_name NULLS LAST, user_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var result = new List<TakasListUserFilterOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new TakasListUserFilterOption(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static string? GetString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record TakasDetailRow(
        string QrCode, string Barcode, string ProductName, int StockQuantity,
        string? SerialNumber, string? BatchNumber, DateTime? ExpiryDate, decimal? EntryCost,
        decimal DebtAmount, string Status, string? ResultCode, string? LastError, bool IsSerialized);
}
