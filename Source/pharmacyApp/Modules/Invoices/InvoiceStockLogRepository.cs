using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Invoices;

public sealed class InvoiceStockLogRepository
{
    public async Task<IReadOnlyList<InvoiceStockLogRow>> LoadAsync(
        InvoiceStockLogQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        const string sql = """
            SELECT log.record_id, log.invoice_uuid, log.action_code, log.occurred_at,
                   log.user_id,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''), log.user_id),
                   log.reference_code, log.movement_type_id, movement_type.display_name,
                   log.related_log_id, sender.party_name, receiver.party_name, log.notes,
                   count(movement.record_id)::integer,
                   count(movement.record_id) FILTER (WHERE EXISTS (
                       SELECT 1
                       FROM public.invoice_line_qr_codes counted_qr
                       JOIN public.invoice_lines counted_line ON counted_line.record_id = counted_qr.invoice_line_id
                       JOIN public.invoice_stock_log_invoices counted_link
                         ON counted_link.invoice_uuid = counted_line.invoice_uuid
                        AND counted_link.invoice_stock_log_id = log.record_id
                       WHERE counted_qr.qr_code = movement.qr_code
                   ))::integer,
                   count(movement.record_id) FILTER (WHERE movement.record_id IS NOT NULL AND NOT EXISTS (
                       SELECT 1
                       FROM public.invoice_line_qr_codes counted_qr
                       JOIN public.invoice_lines counted_line ON counted_line.record_id = counted_qr.invoice_line_id
                       JOIN public.invoice_stock_log_invoices counted_link
                         ON counted_link.invoice_uuid = counted_line.invoice_uuid
                        AND counted_link.invoice_stock_log_id = log.record_id
                       WHERE counted_qr.qr_code = movement.qr_code
                   ))::integer,
                   linked.invoice_uuids, linked.sender_names, linked.receiver_names
            FROM public.invoice_stock_logs log
            JOIN public.invoices invoice ON invoice.invoice_uuid = log.invoice_uuid
            JOIN public.addresses sender ON sender.record_id = invoice.sender_address_id
            JOIN public.addresses receiver ON receiver.record_id = invoice.receiver_address_id
            JOIN public.users app_user ON app_user.user_id = log.user_id
            JOIN public."qrMovementTypes" movement_type ON movement_type.record_id = log.movement_type_id
            LEFT JOIN public."qrMovements" movement ON movement.reference_code = log.reference_code
            LEFT JOIN LATERAL (
                SELECT string_agg(link.invoice_uuid, ', ' ORDER BY link.invoice_uuid) AS invoice_uuids,
                       string_agg(DISTINCT linked_sender.party_name, ', ') AS sender_names,
                       string_agg(DISTINCT linked_receiver.party_name, ', ') AS receiver_names
                FROM public.invoice_stock_log_invoices link
                JOIN public.invoices linked_invoice ON linked_invoice.invoice_uuid = link.invoice_uuid
                JOIN public.addresses linked_sender ON linked_sender.record_id = linked_invoice.sender_address_id
                JOIN public.addresses linked_receiver ON linked_receiver.record_id = linked_invoice.receiver_address_id
                WHERE link.invoice_stock_log_id = log.record_id
            ) linked ON TRUE
            WHERE log.occurred_at >= @start_utc
              AND log.occurred_at < @end_utc
              AND (@action_code = '' OR log.action_code = @action_code)
              AND (@user_id = '' OR log.user_id = @user_id)
              AND (@search = '' OR log.invoice_uuid ILIKE @contains
                   OR log.reference_code ILIKE @contains
                   OR sender.party_name ILIKE @contains
                   OR receiver.party_name ILIKE @contains
                   OR linked.invoice_uuids ILIKE @contains
                   OR linked.sender_names ILIKE @contains
                   OR linked.receiver_names ILIKE @contains
                   OR EXISTS (
                       SELECT 1 FROM public."qrMovements" filtered_movement
                       LEFT JOIN public.invoice_line_qr_codes filtered_qr
                              ON filtered_qr.qr_code = filtered_movement.qr_code
                       WHERE filtered_movement.reference_code = log.reference_code
                         AND (filtered_movement.qr_code ILIKE @contains
                              OR filtered_qr.barcode ILIKE @contains)))
            GROUP BY log.record_id, app_user.first_name, app_user.last_name,
                     movement_type.display_name, sender.party_name, receiver.party_name,
                     linked.invoice_uuids, linked.sender_names, linked.receiver_names
            ORDER BY log.occurred_at DESC, log.record_id DESC;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("start_utc", NpgsqlDbType.TimestampTz, query.StartUtc.ToUniversalTime());
        command.Parameters.AddWithValue("end_utc", NpgsqlDbType.TimestampTz, query.EndUtc.ToUniversalTime());
        command.Parameters.AddWithValue("action_code", NpgsqlDbType.Text, query.ActionCode.Trim());
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, query.UserId.Trim());
        command.Parameters.AddWithValue("search", NpgsqlDbType.Text, query.Search.Trim());
        command.Parameters.AddWithValue("contains", NpgsqlDbType.Text, $"%{query.Search.Trim()}%");

        var rows = new List<InvoiceStockLogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InvoiceStockLogRow
            {
                RecordId = reader.GetInt64(0),
                InvoiceUuid = reader.GetString(1),
                InvoiceUuids = reader.IsDBNull(16) ? reader.GetString(1) : reader.GetString(16),
                ActionCode = reader.GetString(2),
                OccurredAt = reader.GetFieldValue<DateTimeOffset>(3),
                UserId = reader.GetString(4),
                UserName = reader.GetString(5),
                ReferenceCode = reader.GetString(6),
                MovementTypeId = reader.GetInt32(7),
                MovementTypeName = reader.GetString(8),
                RelatedLogId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                SenderName = reader.IsDBNull(17) ? reader.GetString(10) : reader.GetString(17),
                ReceiverName = reader.IsDBNull(18) ? reader.GetString(11) : reader.GetString(18),
                Notes = reader.GetString(12),
                MovementCount = reader.GetInt32(13),
                SerializedQuantity = reader.GetInt32(14),
                BarcodeQuantity = reader.GetInt32(15)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<InvoiceStockLogMovementRow>> LoadDetailsAsync(
        InvoiceStockLogRow log,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT movement.record_id, movement.qr_code,
                   COALESCE(trace_line.barcode, barcode_line.barcode, active.barcode, inactive.barcode,
                            CASE WHEN movement.qr_code ~ '^[0-9]{8,14}$' THEN movement.qr_code ELSE '' END) AS barcode,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), trace_line.item_name, barcode_line.item_name, '') AS product_name,
                   movement.movement_type, movement_type.display_name, movement.movement_date,
                   movement.notes
            FROM public."qrMovements" movement
            JOIN public."qrMovementTypes" movement_type ON movement_type.record_id = movement.movement_type
            LEFT JOIN LATERAL (
                SELECT line_qr.barcode, invoice_line.item_name
                FROM public.invoice_line_qr_codes line_qr
                JOIN public.invoice_lines invoice_line ON invoice_line.record_id = line_qr.invoice_line_id
                WHERE line_qr.qr_code = movement.qr_code
                  AND EXISTS (
                      SELECT 1 FROM public.invoice_stock_log_invoices link
                      WHERE link.invoice_stock_log_id = @log_id
                        AND link.invoice_uuid = invoice_line.invoice_uuid)
                ORDER BY line_qr.record_id LIMIT 1
            ) trace_line ON TRUE
            LEFT JOIN LATERAL (
                SELECT invoice_line.barcode, invoice_line.item_name
                FROM public.invoice_lines invoice_line
                WHERE EXISTS (
                      SELECT 1 FROM public.invoice_stock_log_invoices link
                      WHERE link.invoice_stock_log_id = @log_id
                        AND link.invoice_uuid = invoice_line.invoice_uuid)
                  AND invoice_line.barcode = movement.qr_code
                ORDER BY invoice_line.record_id LIMIT 1
            ) barcode_line ON TRUE
            LEFT JOIN public."qrActive" active ON active.qr_code = movement.qr_code
            LEFT JOIN public."qrInactive" inactive ON inactive.qr_code = movement.qr_code
            LEFT JOIN public.barcode product ON product.barcode = COALESCE(
                trace_line.barcode, barcode_line.barcode, active.barcode, inactive.barcode,
                CASE WHEN movement.qr_code ~ '^[0-9]{8,14}$' THEN movement.qr_code END)
            WHERE movement.reference_code = @reference_code
            ORDER BY product_name, barcode, movement.record_id;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("log_id", NpgsqlDbType.Bigint, log.RecordId);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, log.ReferenceCode);
        var rows = new List<InvoiceStockLogMovementRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InvoiceStockLogMovementRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetDateTime(6), reader.GetString(7)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<InvoiceStockLogUserOption>> LoadUsersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT user_id,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''), user_id) || ' — ' || user_id
            FROM public.users ORDER BY first_name NULLS LAST, last_name NULLS LAST, user_id;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var rows = new List<InvoiceStockLogUserOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new InvoiceStockLogUserOption(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    public async Task SaveNotesAsync(long recordId, string notes, CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var normalized = notes.Trim();
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                         "UPDATE public.invoice_stock_logs SET notes=@notes WHERE record_id=@record_id", connection, transaction))
        {
            command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, normalized);
            command.Parameters.AddWithValue("record_id", NpgsqlDbType.Bigint, recordId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Fatura hareketi bulunamadı.");
        }
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public."qrMovements" movement
            SET notes = @notes
            FROM public.invoice_stock_logs stock_log
            WHERE stock_log.record_id = @record_id
              AND movement.reference_code = stock_log.reference_code
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, normalized);
            command.Parameters.AddWithValue("record_id", NpgsqlDbType.Bigint, recordId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
