using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Invoices;

public sealed class PartialInvoiceRefundRepository
{
    private readonly InvoiceItsReturnSender itsReturnSender;

    public PartialInvoiceRefundRepository() : this(new InvoiceItsReturnSender(
        new InvoiceItsReturnApiClient(),
        ReadItsConfiguration))
    {
    }

    internal PartialInvoiceRefundRepository(InvoiceItsReturnSender itsReturnSender) =>
        this.itsReturnSender = itsReturnSender;

    public async Task<PartialInvoiceRefundQrRow?> ResolveAvailableAsync(
        string enteredCode,
        CancellationToken cancellationToken = default)
    {
        var code = enteredCode.Trim();
        if (code.Length == 0) return null;
        var normalizedCode = RetailQrCodeNormalizer.Normalize(code);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        const string sql = """
            SELECT invoice.invoice_uuid, invoice.issue_date, sender.party_name,
                   code.barcode, line.item_name, code.qr_code, code.expiry_date
            FROM public.invoices invoice
            JOIN public.addresses sender ON sender.record_id = invoice.sender_address_id
            JOIN public.invoice_lines line ON line.invoice_uuid = invoice.invoice_uuid
            JOIN public.invoice_line_qr_codes code ON code.invoice_line_id = line.record_id
            JOIN public."qrActive" active ON active.qr_code = code.qr_code
            WHERE invoice.invoice_type = 'Gelen Fatura'
              AND invoice.is_saved
              AND (code.qr_code = @code OR regexp_replace(
                    regexp_replace(code.qr_code, E'^\\]d2', ''),
                    E'[()\\x1D]', '', 'g') = @normalized_code)
            ORDER BY CASE WHEN code.qr_code = @code THEN 0 ELSE 1 END,
                     invoice.issue_date DESC NULLS LAST, invoice.invoice_uuid, code.record_id
            LIMIT 2;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("normalized_code", NpgsqlDbType.Varchar, normalizedCode);
        var rows = new List<PartialInvoiceRefundQrRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PartialInvoiceRefundQrRow
            {
                InvoiceUuid = reader.GetString(0),
                IssueDate = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1),
                SenderName = reader.GetString(2),
                Barcode = reader.GetString(3),
                ProductName = reader.GetString(4),
                QrCode = reader.GetString(5),
                ExpiryDate = reader.GetFieldValue<DateOnly>(6)
            });
        }
        if (rows.Count > 1 && rows.Select(row => row.InvoiceUuid).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new InvalidOperationException("Karekod birden fazla faturaya bağlı görünüyor; iade güvenli biçimde oluşturulamadı.");
        return rows.FirstOrDefault();
    }

    public async Task<PartialInvoiceRefundResult> RefundAsync(
        IReadOnlyCollection<string> selectedQrCodes,
        string notes,
        string userId,
        bool sendItsNotifications,
        CancellationToken cancellationToken = default)
    {
        var codes = selectedQrCodes.Select(code => code.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (codes.Length == 0) throw new InvalidOperationException("İade edilecek en az bir karekod seçilmelidir.");
        if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("Oturum açmış kullanıcı gereklidir.");

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var locked = new List<LockedQr>();
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT code.qr_code, line.record_id, invoice.invoice_uuid,
                   sender.gln, sender.identity_no, sender.party_name
            FROM public.invoice_line_qr_codes code
            JOIN public.invoice_lines line ON line.record_id = code.invoice_line_id
            JOIN public.invoices invoice ON invoice.invoice_uuid = line.invoice_uuid
            JOIN public.addresses sender ON sender.record_id = invoice.sender_address_id
            JOIN public."qrActive" active ON active.qr_code = code.qr_code
            WHERE code.qr_code = ANY(@codes)
              AND invoice.invoice_type = 'Gelen Fatura'
              AND invoice.is_saved
            ORDER BY code.qr_code, invoice.invoice_uuid
            FOR UPDATE OF active, invoice
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("codes", NpgsqlDbType.Array | NpgsqlDbType.Text, codes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var depotGln = reader.GetString(3).Trim();
                var counterparty = FirstNonEmpty(depotGln, reader.GetString(4), reader.GetString(5));
                locked.Add(new LockedQr(
                    reader.GetString(0), reader.GetInt32(1), reader.GetString(2), depotGln, $"D{counterparty}"));
            }
        }

        if (locked.Count != codes.Length || locked.GroupBy(row => row.QrCode, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Seçilen karekodlardan biri aktif stokta ve kaydedilmiş gelen faturada bulunamadı.");

        var itsPlans = new List<InvoiceItsNotificationPlan>();
        foreach (var depot in locked.GroupBy(row => row.DepotGln, StringComparer.Ordinal))
        {
            var plan = InvoiceItsNotificationPlanner.Build(false, depot.Key,
                depot.Select(row => new InvoiceStockMovementItem(row.LineId, row.QrCode, true, null)));
            if (plan.IsRequired) itsPlans.Add(plan);
        }

        var itsExecution = sendItsNotifications
            ? await itsReturnSender.SendAsync(itsPlans, cancellationToken)
            : new InvoiceItsReturnExecution(0, []);

        var invoiceUuids = locked.Select(row => row.InvoiceUuid).Distinct(StringComparer.Ordinal).Order().ToArray();
        var primaryInvoiceUuid = invoiceUuids[0];
        var normalizedNotes = notes.Trim();
        long logId;
        string referenceCode;
        int movementTypeId;
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            WITH next_log AS (
                SELECT nextval(pg_get_serial_sequence('public.invoice_stock_logs', 'record_id'))::bigint AS record_id
            ), movement_type AS (
                SELECT record_id FROM public."qrMovementTypes" WHERE code = 'DEP_RETURN'
            )
            INSERT INTO public.invoice_stock_logs (
                record_id, invoice_uuid, action_code, user_id, reference_code,
                movement_type_id, related_log_id, notes)
            SELECT next_log.record_id, @invoice_uuid, 'PARTIAL_REFUND', @user_id,
                   'FI:' || next_log.record_id::text, movement_type.record_id, NULL, @notes
            FROM next_log CROSS JOIN movement_type
            RETURNING record_id, reference_code, movement_type_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, primaryInvoiceUuid);
            command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
            command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, normalizedNotes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("DEP_RETURN hareket türü bulunamadı.");
            logId = reader.GetInt64(0);
            referenceCode = reader.GetString(1);
            movementTypeId = reader.GetInt32(2);
        }

        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.invoice_stock_log_invoices (invoice_stock_log_id, invoice_uuid)
            SELECT @log_id, unnest(@invoice_uuids)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("log_id", NpgsqlDbType.Bigint, logId);
            command.Parameters.AddWithValue("invoice_uuids", NpgsqlDbType.Array | NpgsqlDbType.Text, invoiceUuids);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var row in locked)
        {
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                INSERT INTO public."qrMovements" (
                    qr_code, movement_type, movement_date, movement_address,
                    user_id, reference_code, notes)
                VALUES (@qr_code, @movement_type, CURRENT_TIMESTAMP, @movement_address,
                        @user_id, @reference_code, @notes)
                """, connection, transaction);
            command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, row.QrCode);
            command.Parameters.AddWithValue("movement_type", NpgsqlDbType.Integer, movementTypeId);
            command.Parameters.AddWithValue("movement_address", NpgsqlDbType.Varchar, row.MovementAddress);
            command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
            command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
            command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, normalizedNotes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PartialInvoiceRefundResult(
            referenceCode,
            locked.Count,
            invoiceUuids.Length,
            itsPlans.Count,
            itsExecution.NotificationCount,
            itsExecution.NotificationIds);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static InvoiceItsReturnConfiguration ReadItsConfiguration()
    {
        var settings = ApplicationSettingsStore.Current;
        return new InvoiceItsReturnConfiguration(
            ItsEnvironment.Production,
            new ItsCredentials(settings.ItsUsername, settings.ItsPassword));
    }

    private sealed record LockedQr(
        string QrCode,
        int LineId,
        string InvoiceUuid,
        string DepotGln,
        string MovementAddress);
}
