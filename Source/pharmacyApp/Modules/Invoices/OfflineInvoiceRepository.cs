using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Invoices;

public sealed record OfflineInvoiceRow(
    string InvoiceUuid,
    string InvoiceType,
    string SenderName,
    string SenderIdentityNo,
    string SenderGln,
    string ReceiverName,
    string ReceiverIdentityNo,
    string ReceiverGln,
    DateOnly? IssueDate,
    DateOnly? DueDate,
    DateTimeOffset DatabaseEntryAt,
    decimal? TotalCost,
    decimal? DiscountAmount,
    decimal? KdvAmount,
    decimal? PayableAmount,
    bool IsSaved,
    string LatestStockAction)
{
    public string RowState => InvoiceType switch
    {
        var type when type.Equals("Gelen Fatura", StringComparison.OrdinalIgnoreCase) =>
            IsSaved ? "Saved" : "Unsaved",
        var type when type.Equals("Giden Fatura", StringComparison.OrdinalIgnoreCase)
                      || type.Equals("E-Arşiv Fatura", StringComparison.OrdinalIgnoreCase) => "Sent",
        _ => "Neutral"
    };

    public bool IsIncoming => InvoiceType.Equals("Gelen Fatura", StringComparison.OrdinalIgnoreCase);
    public bool CanTakeIntoStock => IsIncoming && !IsSaved;
    public bool CanRefund => IsIncoming && IsSaved
                             && LatestStockAction is "SAVE" or "PARTIAL_REFUND";
}

public sealed record InvoiceLineQrRow(
    string QrCode,
    string Gtin,
    string SerialNumber,
    string BatchNumber,
    DateOnly ExpiryDate,
    bool IsPartiallyRefunded)
{
    public string ExpiryDateDisplay => ExpiryDate.ToString("dd.MM.yyyy");
}

public sealed class OfflineInvoiceLineRow : INotifyPropertyChanged
{
    private bool isExpanded;

    public required int RecordId { get; init; }
    public required int LineNumber { get; init; }
    public required string ItemCode { get; init; }
    public required string Barcode { get; init; }
    public required string ItemName { get; init; }
    public required string Description { get; init; }
    public decimal? Quantity { get; init; }
    public required string Unit { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? VatRate { get; init; }
    public decimal? VatAmount { get; init; }
    public decimal? DiscountAmount { get; init; }
    public decimal? LineAmount { get; init; }
    public required string Currency { get; init; }
    public bool IsMedication { get; init; }
    public required string StockTrackingType { get; init; }
    public required string TrackingWarning { get; init; }
    public ObservableCollection<InvoiceLineQrRow> QrCodes { get; } = [];

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value) return;
            isExpanded = value;
            OnPropertyChanged();
        }
    }

    public bool HasQrCodes => QrCodes.Count > 0;
    public bool HasQrTrackingDetails => QrCodes.Count > 0
                                        || StockTrackingType.Equals("QR", StringComparison.Ordinal);
    public bool HasTrackingWarning => TrackingWarning.Length > 0;
    public string TrackingDisplay => QrCodes.Count > 0
        ? TrackingWarning.Length > 0
            ? $"Karekod {QrCodes.Count:N0} / {FormatQuantity(Quantity)} (eksik)"
            : $"Karekod ({QrCodes.Count:N0})"
        : StockTrackingType switch
    {
        "QR" when TrackingWarning.Length > 0 => $"Karekod {QrCodes.Count:N0} / {FormatQuantity(Quantity)} (eksik)",
        "QR" => $"Karekod ({QrCodes.Count:N0})",
        "BARCODE" => $"Barkod ({FormatQuantity(Quantity)})",
        _ => "Stok dışı"
    };

    public void FinishQrLoading()
    {
        OnPropertyChanged(nameof(HasQrCodes));
        OnPropertyChanged(nameof(HasQrTrackingDetails));
        OnPropertyChanged(nameof(HasTrackingWarning));
        OnPropertyChanged(nameof(TrackingDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatQuantity(decimal? quantity) => quantity is null
        ? "—"
        : quantity.Value.ToString(quantity == decimal.Truncate(quantity.Value) ? "N0" : "N4");
}

public sealed record InvoiceStockActionResult(
    string ReferenceCode,
    int StockLineCount,
    int SerializedQuantity,
    int BarcodeQuantity,
    bool IsSaved,
    string ItsOperationDisplay,
    bool ItsNotificationSent);

public sealed record OfflineInvoicePage(
    IReadOnlyList<OfflineInvoiceRow> Rows,
    int TotalCount);

public sealed class OfflineInvoiceRepository
{
    public async Task<OfflineInvoicePage> LoadInvoicesAsync(
        string? invoiceType,
        DateOnly? startDate,
        DateOnly? endDate,
        string? search,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        const string sql = """
            WITH filtered AS (
                SELECT invoice.invoice_uuid, invoice.invoice_type,
                       sender.party_name AS sender_name,
                       sender.identity_no AS sender_identity_no,
                       sender.gln AS sender_gln,
                       receiver.party_name AS receiver_name,
                       receiver.identity_no AS receiver_identity_no,
                       receiver.gln AS receiver_gln,
                       invoice.issue_date, invoice.due_date, invoice.database_entry_at,
                       invoice.total_cost, invoice.discount_amount, invoice.kdv_amount,
                       invoice.payable_amount, invoice.is_saved,
                       COALESCE(latest_action.action_code, '') AS latest_stock_action
                FROM public.invoices invoice
                JOIN public.addresses sender ON sender.record_id = invoice.sender_address_id
                JOIN public.addresses receiver ON receiver.record_id = invoice.receiver_address_id
                LEFT JOIN LATERAL (
                    SELECT stock_log.action_code
                    FROM public.invoice_stock_logs stock_log
                    JOIN public.invoice_stock_log_invoices link
                      ON link.invoice_stock_log_id = stock_log.record_id
                    WHERE link.invoice_uuid = invoice.invoice_uuid
                    ORDER BY stock_log.occurred_at DESC, stock_log.record_id DESC
                    LIMIT 1
                ) latest_action ON TRUE
                WHERE (@invoice_type IS NULL OR invoice.invoice_type = @invoice_type)
                  AND (@start_date IS NULL OR invoice.issue_date >= @start_date)
                  AND (@end_date IS NULL OR invoice.issue_date <= @end_date)
                  AND (@search IS NULL
                       OR invoice.invoice_uuid ILIKE @search
                       OR sender.party_name ILIKE @search OR sender.identity_no ILIKE @search OR sender.gln ILIKE @search
                       OR receiver.party_name ILIKE @search OR receiver.identity_no ILIKE @search OR receiver.gln ILIKE @search)
            )
            SELECT page.invoice_uuid, page.invoice_type,
                   page.sender_name, page.sender_identity_no, page.sender_gln,
                   page.receiver_name, page.receiver_identity_no, page.receiver_gln,
                   page.issue_date, page.due_date, page.database_entry_at,
                   page.total_cost, page.discount_amount, page.kdv_amount,
                   page.payable_amount, page.is_saved, page.latest_stock_action,
                   totals.total_count
            FROM (SELECT COUNT(*)::integer AS total_count FROM filtered) totals
            LEFT JOIN LATERAL (
                SELECT *
                FROM filtered
                ORDER BY issue_date DESC NULLS LAST, database_entry_at DESC, invoice_uuid DESC
                LIMIT @page_size OFFSET @offset
            ) page ON TRUE
            ORDER BY page.issue_date DESC NULLS LAST, page.database_entry_at DESC, page.invoice_uuid DESC;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        AddNullable(command, "invoice_type", NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(invoiceType) ? null : invoiceType);
        AddNullable(command, "start_date", NpgsqlDbType.Date, startDate);
        AddNullable(command, "end_date", NpgsqlDbType.Date, endDate);
        AddNullable(command, "search", NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%");
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, offset);
        command.Parameters.AddWithValue("page_size", NpgsqlDbType.Integer, pageSize);

        var rows = new List<OfflineInvoiceRow>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(17);
            if (reader.IsDBNull(0))
                continue;

            rows.Add(new OfflineInvoiceRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateOnly>(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                reader.GetBoolean(15), reader.GetString(16)));
        }
        return new OfflineInvoicePage(rows, totalCount);
    }

    public async Task<IReadOnlyList<OfflineInvoiceLineRow>> LoadLinesAsync(
        string invoiceUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        const string lineSql = """
            SELECT line.record_id, line.line_number, line.item_code, line.barcode, line.item_name,
                   CASE WHEN line.is_medication AND upper(trim(line.description)) = upper(trim(line.item_name))
                        THEN '' ELSE line.description END,
                   line.quantity, line.unit, line.unit_price, line.vat_rate, line.vat_amount,
                   line.discount_amount, line.line_amount, line.currency, line.is_medication,
                   line.stock_tracking_type, line.tracking_warning,
                   code.qr_code, code.gtin, code.serial_number, code.batch_number, code.expiry_date,
                   EXISTS (
                       SELECT 1
                       FROM public."qrMovements" movement
                       JOIN public.invoice_stock_logs stock_log
                         ON stock_log.reference_code = movement.reference_code
                       WHERE movement.qr_code = code.qr_code
                         AND stock_log.action_code = 'PARTIAL_REFUND'
                         AND EXISTS (
                             SELECT 1 FROM public.invoice_stock_log_invoices link
                             WHERE link.invoice_stock_log_id = stock_log.record_id
                               AND link.invoice_uuid = line.invoice_uuid)) AS is_partially_refunded
            FROM public.invoice_lines line
            LEFT JOIN public.invoice_line_qr_codes code ON code.invoice_line_id = line.record_id
            WHERE line.invoice_uuid = @invoice_uuid
            ORDER BY line.line_number, line.record_id, code.record_id;
            """;
        var rows = new List<OfflineInvoiceLineRow>();
        var byId = new Dictionary<int, OfflineInvoiceLineRow>();
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(lineSql, connection))
        {
            command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var recordId = reader.GetInt32(0);
                if (!byId.TryGetValue(recordId, out var line))
                {
                    line = new OfflineInvoiceLineRow
                    {
                        RecordId = recordId, LineNumber = reader.GetInt32(1),
                        ItemCode = reader.GetString(2), Barcode = reader.GetString(3),
                        ItemName = reader.GetString(4), Description = reader.GetString(5),
                        Quantity = reader.IsDBNull(6) ? null : reader.GetDecimal(6), Unit = reader.GetString(7),
                        UnitPrice = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                        VatRate = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                        VatAmount = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                        DiscountAmount = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                        LineAmount = reader.IsDBNull(12) ? null : reader.GetDecimal(12), Currency = reader.GetString(13),
                        IsMedication = reader.GetBoolean(14), StockTrackingType = reader.GetString(15),
                        TrackingWarning = reader.GetString(16)
                    };
                    byId.Add(recordId, line);
                    rows.Add(line);
                }
                if (!reader.IsDBNull(17))
                    line.QrCodes.Add(new InvoiceLineQrRow(
                        reader.GetString(17), reader.GetString(18), reader.GetString(19), reader.GetString(20),
                        reader.GetFieldValue<DateOnly>(21), reader.GetBoolean(22)));
            }
        }
        foreach (var row in rows) row.FinishQrLoading();
        return rows;
    }

    public async Task<InvoiceStockActionResult> ApplyStockActionAsync(
        string invoiceUuid,
        string userId,
        bool save,
        string notes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("Fatura stok işlemi için oturum açmış kullanıcı gereklidir.");

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await InvoiceStockSchema.PrepareInvoiceAsync(connection, transaction, invoiceUuid, cancellationToken);

        var invoice = await ReadLockedInvoiceAsync(connection, transaction, invoiceUuid, cancellationToken);
        if (!invoice.InvoiceType.Equals("Gelen Fatura", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Yalnızca gelen faturalar stok işlemine alınabilir.");
        if (save && invoice.IsSaved) throw new InvalidOperationException("Fatura daha önce stoka alınmış.");
        if (!save && !invoice.IsSaved) throw new InvalidOperationException("Fatura stokta kayıtlı değil.");
        if (!save && invoice.LatestAction is not ("SAVE" or "PARTIAL_REFUND"))
            throw new InvalidOperationException("Fatura bu uygulamaya ait stok giriş kaydı olmadan iade edilemez.");

        var lines = await ReadStockLinesAsync(connection, transaction, invoiceUuid, cancellationToken);
        if (!save)
        {
            var partiallyRefunded = await ReadPartiallyRefundedQrCodesAsync(
                connection, transaction, invoiceUuid, cancellationToken);
            lines = lines.Select(line => line.TrackingType == "QR"
                    ? line with
                    {
                        QrCodes = line.QrCodes.Where(code => !partiallyRefunded.Contains(code)).ToArray(),
                        Quantity = line.QrCodes.Count(code => !partiallyRefunded.Contains(code))
                    }
                    : line)
                .Where(line => line.TrackingType != "QR" || line.QrCodes.Count > 0)
                .ToArray();
        }
        var movements = InvoiceStockMovementPlanner.Build(lines);
        if (movements.Count == 0)
            throw new InvalidOperationException("Faturada stoka alınabilecek karekodlu veya barkodlu ürün bulunmuyor.");

        var duplicateQr = movements.Where(item => item.IsSerialized)
            .GroupBy(item => item.Code, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateQr is not null)
            throw new InvalidOperationException($"Aynı karekod faturada birden fazla kez bulunuyor: {duplicateQr.Key}");

        var itsPlan = InvoiceItsNotificationPlanner.Build(save, invoice.DepotGln, movements);
        var movementType = save ? "DEP_IN" : "DEP_RETURN";
        var actionCode = save ? "SAVE" : "REFUND";
        var relatedLogId = save ? null : invoice.LatestLogId;
        var serializedQuantity = movements.Count(item => item.IsSerialized);
        var barcodeQuantity = movements.Count - serializedQuantity;
        var stockLineCount = movements.Select(item => item.LineId).Distinct().Count();
        var stockLog = await InsertStockLogAsync(
            connection, transaction, invoiceUuid, actionCode, userId,
            movementType, relatedLogId, notes, cancellationToken);

        foreach (var movement in movements)
        {
            await InsertMovementAsync(connection, transaction, movement.Code, stockLog.MovementTypeId,
                invoice.MovementAddress, userId, stockLog.ReferenceCode, notes, cancellationToken);
            if (save && movement.IsSerialized && movement.UnitCost is not null)
                await UpdateSerializedEntryCostAsync(connection, transaction, movement.Code,
                    movement.UnitCost.Value, stockLog.ReferenceCode, cancellationToken);
        }

        await using (var updateInvoice = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                         "UPDATE public.invoices SET is_saved=@is_saved WHERE invoice_uuid=@invoice_uuid",
                         connection, transaction))
        {
            updateInvoice.Parameters.AddWithValue("is_saved", NpgsqlDbType.Boolean, save);
            updateInvoice.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
            if (await updateInvoice.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Fatura kaydedilme durumu güncellenemedi.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new InvoiceStockActionResult(
            stockLog.ReferenceCode, stockLineCount, serializedQuantity, barcodeQuantity, save,
            itsPlan.OperationDisplay, false);
    }

    private static async Task<LockedInvoice> ReadLockedInvoiceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string invoiceUuid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT invoice.invoice_type, invoice.is_saved,
                   sender.gln, sender.identity_no, sender.party_name,
                   COALESCE(latest.action_code, ''), latest.record_id
            FROM public.invoices invoice
            JOIN public.addresses sender ON sender.record_id = invoice.sender_address_id
            LEFT JOIN LATERAL (
                SELECT stock_log.record_id, stock_log.action_code
                FROM public.invoice_stock_logs stock_log
                JOIN public.invoice_stock_log_invoices link
                  ON link.invoice_stock_log_id = stock_log.record_id
                WHERE link.invoice_uuid = invoice.invoice_uuid
                ORDER BY stock_log.occurred_at DESC, stock_log.record_id DESC LIMIT 1
            ) latest ON TRUE
            WHERE invoice.invoice_uuid = @invoice_uuid
            FOR UPDATE OF invoice
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Fatura bulunamadı.");
        var depotGln = reader.GetString(2).Trim();
        var counterparty = FirstNonEmpty(depotGln, reader.GetString(3), reader.GetString(4));
        return new LockedInvoice(
            reader.GetString(0), reader.GetBoolean(1), depotGln, $"D{counterparty}",
            reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    private static async Task<IReadOnlyList<InvoiceStockLine>> ReadStockLinesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string invoiceUuid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT line.record_id, line.line_number, line.item_name, line.barcode,
                   line.quantity, line.unit_price, line.discount_amount, line.line_amount,
                   line.stock_tracking_type, line.tracking_warning, code.qr_code
            FROM public.invoice_lines line
            LEFT JOIN public.invoice_line_qr_codes code ON code.invoice_line_id = line.record_id
            WHERE line.invoice_uuid = @invoice_uuid AND line.stock_tracking_type IN ('QR', 'BARCODE')
            ORDER BY line.record_id, code.record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
        var grouped = new Dictionary<int, StockLineBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var recordId = reader.GetInt32(0);
            if (!grouped.TryGetValue(recordId, out var line))
            {
                line = new StockLineBuilder(
                    recordId, reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                    reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.GetString(8), reader.GetString(9));
                grouped.Add(recordId, line);
            }
            if (!reader.IsDBNull(10)) line.QrCodes.Add(reader.GetString(10));
        }
        return grouped.Values.Select(line => line.Build()).ToArray();
    }

    private static async Task<StockLogIdentity> InsertStockLogAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string invoiceUuid,
        string actionCode, string userId, string movementTypeCode, long? relatedLogId, string notes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH next_log AS (
                SELECT nextval(pg_get_serial_sequence('public.invoice_stock_logs', 'record_id'))::bigint AS record_id
            ), movement_type AS (
                SELECT record_id
                FROM public."qrMovementTypes"
                WHERE code = @movement_type_code
            ), inserted AS (
            INSERT INTO public.invoice_stock_logs (
                record_id, invoice_uuid, action_code, user_id, reference_code,
                movement_type_id, related_log_id, notes)
            SELECT next_log.record_id, @invoice_uuid, @action_code, @user_id,
                   'FI:' || next_log.record_id::text, movement_type.record_id,
                   @related_log_id, @notes
            FROM next_log CROSS JOIN movement_type
            RETURNING record_id, reference_code, movement_type_id
            ), linked AS (
                INSERT INTO public.invoice_stock_log_invoices (invoice_stock_log_id, invoice_uuid)
                SELECT record_id, @invoice_uuid FROM inserted
            )
            SELECT record_id, reference_code, movement_type_id FROM inserted;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
        command.Parameters.AddWithValue("action_code", NpgsqlDbType.Text, actionCode);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("movement_type_code", NpgsqlDbType.Varchar, movementTypeCode);
        command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, notes.Trim());
        AddNullable(command, "related_log_id", NpgsqlDbType.Bigint, relatedLogId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Fatura hareket türü bulunamadı: {movementTypeCode}");
        return new StockLogIdentity(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2));
    }

    private static async Task InsertMovementAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string code, int movementTypeId,
        string movementAddress, string userId, string referenceCode, string notes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code, notes)
            VALUES (
                @code, @movement_type_id,
                CURRENT_TIMESTAMP, @movement_address, @user_id, @reference_code, @notes)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("movement_type_id", NpgsqlDbType.Integer, movementTypeId);
        command.Parameters.AddWithValue("movement_address", NpgsqlDbType.Varchar, movementAddress);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, notes.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ReadPartiallyRefundedQrCodesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string invoiceUuid,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT DISTINCT movement.qr_code
            FROM public.invoice_stock_logs stock_log
            JOIN public.invoice_stock_log_invoices link
              ON link.invoice_stock_log_id = stock_log.record_id
            JOIN public."qrMovements" movement ON movement.reference_code = stock_log.reference_code
            WHERE link.invoice_uuid = @invoice_uuid
              AND stock_log.action_code = 'PARTIAL_REFUND'
            """, connection, transaction);
        command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) codes.Add(reader.GetString(0));
        return codes;
    }

    private static async Task UpdateSerializedEntryCostAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string qrCode,
        decimal unitCost, string referenceCode, CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public."qrActive"
            SET purchase_cost=@purchase_cost, reference_no=@reference_code
            WHERE qr_code=@qr_code
            """, connection, transaction);
        command.Parameters.AddWithValue("purchase_cost", NpgsqlDbType.Numeric, unitCost);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private sealed record LockedInvoice(string InvoiceType, bool IsSaved,
        string DepotGln, string MovementAddress, string LatestAction, long? LatestLogId);

    private sealed record StockLogIdentity(long RecordId, string ReferenceCode, int MovementTypeId);

    private sealed class StockLineBuilder(int recordId, int lineNumber, string itemName, string barcode,
        decimal? quantity, decimal? unitPrice, decimal? discountAmount, decimal? lineAmount,
        string trackingType, string warning)
    {
        public List<string> QrCodes { get; } = [];
        public InvoiceStockLine Build() => new(recordId, lineNumber, itemName, barcode, quantity, unitPrice,
            discountAmount, lineAmount, trackingType, warning, QrCodes);
    }
}
