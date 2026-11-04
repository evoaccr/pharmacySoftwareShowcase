using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Invoices;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceTraceabilityImportStatus(
    int StoredQrCodes,
    int ExpectedQrQuantity,
    int IncompleteQrLines,
    int MissingQrQuantity);

public sealed record InvoiceDataClearResult(
    long Addresses,
    long Invoices,
    long InvoiceLines,
    long InvoiceLineQrCodes,
    long InvoiceStockLogs,
    long FarmassistMappings,
    long FarmassistStockMappings,
    long IncrementalSyncStates,
    long IncrementalSyncItems)
{
    public long TotalRows => Addresses + Invoices + InvoiceLines + InvoiceLineQrCodes
                             + InvoiceStockLogs + FarmassistMappings + FarmassistStockMappings
                             + IncrementalSyncStates + IncrementalSyncItems;
}

public sealed class CompactInvoiceRepository
{
    private static readonly IReadOnlyDictionary<string, string> StoredTypeNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["incoming-invoice"] = "Gelen Fatura",
            ["outgoing-invoice"] = "Giden Fatura",
            ["archive-invoice"] = "E-Arşiv Fatura"
        };

    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["addresses"] = ["record_id", "party_name", "identity_no", "gln"],
            ["invoices"] =
            [
                "invoice_uuid", "invoice_type", "sender_address_id", "receiver_address_id",
                "issue_date", "due_date", "database_entry_at", "total_cost", "discount_amount",
                "kdv_amount", "payable_amount", "is_saved"
            ],
            ["invoice_lines"] =
            [
                "record_id", "invoice_uuid", "line_number", "item_code", "barcode", "item_name",
                "description", "quantity", "unit", "unit_price", "vat_rate", "vat_amount",
                "discount_amount", "line_amount", "currency", "is_medication", "raw_data",
                "stock_tracking_type", "tracking_warning"
            ]
        };
    private LegacyInvoiceMergeIndex? legacyMergeIndex;

    public async Task EnsureCompatibleSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        const string sql = """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = ANY(@tables);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tables", NpgsqlDbType.Array | NpgsqlDbType.Text,
            RequiredColumns.Keys.ToArray());
        var actual = RequiredColumns.Keys.ToDictionary(
            table => table,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (actual.TryGetValue(table, out var columns)) columns.Add(reader.GetString(1));
        }

        var missing = RequiredColumns
            .SelectMany(pair => pair.Value
                .Where(column => !actual[pair.Key].Contains(column))
                .Select(column => $"{pair.Key}.{column}"))
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                "Fatura veritabanı kompakt aktarım şemasıyla uyumlu değil. Eksik alanlar: "
                + string.Join(", ", missing));
    }

    public async Task<InvoiceDataClearResult> ClearInvoiceDataAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var tables = new[]
        {
            "addresses",
            "invoices",
            "invoice_lines",
            "invoice_line_qr_codes",
            "invoice_stock_logs",
            "farmassist_invoice_import_map",
            "invoice_incremental_sync_state",
            "invoice_incremental_sync_items"
        };
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
            counts[table] = await CountRowsAsync(connection, transaction, table, cancellationToken);

        const string optionalTable = "farmassist_invoice_stock_import_map";
        var hasOptionalTable = await TableExistsAsync(
            connection, transaction, optionalTable, cancellationToken);
        counts[optionalTable] = hasOptionalTable
            ? await CountRowsAsync(connection, transaction, optionalTable, cancellationToken)
            : 0;

        var resetTables = hasOptionalTable ? tables.Append(optionalTable) : tables;
        var qualifiedTables = string.Join(", ", resetTables.Select(table => $"public.{table}"));
        await using (var reset = new NpgsqlCommand(
                         $"TRUNCATE TABLE {qualifiedTables} RESTART IDENTITY;",
                         connection, transaction))
            await reset.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        legacyMergeIndex = null;
        return new InvoiceDataClearResult(
            counts["addresses"],
            counts["invoices"],
            counts["invoice_lines"],
            counts["invoice_line_qr_codes"],
            counts["invoice_stock_logs"],
            counts["farmassist_invoice_import_map"],
            counts[optionalTable],
            counts["invoice_incremental_sync_state"],
            counts["invoice_incremental_sync_items"]);
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = @table_name);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table_name", NpgsqlDbType.Text, tableName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<long> CountRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM public.{tableName};", connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task SaveAsync(
        EczaciKartInvoiceDocument document,
        CancellationToken cancellationToken = default)
        => await SaveCoreAsync(document, mergeLegacyFarmassist: false, cancellationToken);

    public async Task SaveFullArchiveAsync(
        EczaciKartInvoiceDocument document,
        CancellationToken cancellationToken = default)
        => await SaveCoreAsync(document, mergeLegacyFarmassist: true, cancellationToken);

    public async Task<InvoiceTraceabilityImportStatus> LoadTraceabilityStatusAsync(
        string invoiceUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH line_status AS (
                SELECT line.record_id, line.quantity, line.is_medication, line.stock_tracking_type,
                       count(code.record_id)::integer AS qr_count
                FROM public.invoice_lines line
                LEFT JOIN public.invoice_line_qr_codes code ON code.invoice_line_id = line.record_id
                WHERE line.invoice_uuid = @invoice_uuid
                GROUP BY line.record_id
            )
            SELECT COALESCE(sum(qr_count), 0)::integer,
                   COALESCE(sum(CASE
                       WHEN is_medication AND stock_tracking_type = 'QR'
                            AND quantity > 0 AND quantity = trunc(quantity)
                       THEN quantity ELSE 0 END), 0)::integer,
                   count(*) FILTER (
                       WHERE is_medication AND stock_tracking_type = 'QR'
                         AND (quantity IS NULL OR quantity <= 0 OR quantity <> trunc(quantity)
                              OR qr_count <> quantity))::integer,
                   COALESCE(sum(CASE
                       WHEN is_medication AND stock_tracking_type = 'QR'
                            AND quantity > 0 AND quantity = trunc(quantity)
                       THEN greatest(quantity - qr_count, 0) ELSE 0 END), 0)::integer
            FROM line_status;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new InvoiceTraceabilityImportStatus(0, 0, 0, 0);
        return new InvoiceTraceabilityImportStatus(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private async Task SaveCoreAsync(
        EczaciKartInvoiceDocument document,
        bool mergeLegacyFarmassist,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await InvoiceStockSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var merged = await SaveInTransactionAsync(
            connection, transaction, document, mergeLegacyFarmassist, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (merged is not null)
            legacyMergeIndex?.MarkMerged(merged.InvoiceUuid, document.Ettn.Trim());
    }

    internal async Task<LegacyInvoiceMergeCandidate?> SaveInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EczaciKartInvoiceDocument document,
        bool mergeLegacyFarmassist = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!StoredTypeNames.TryGetValue(document.ListItem.TypeKey, out var storedType))
            throw new InvalidOperationException(
                $"{document.ListItem.TypeDisplay} kompakt fatura arşivine aktarılmaz.");
        var invoiceUuid = document.Ettn.Trim();
        if (invoiceUuid.Length == 0)
            throw new InvalidDataException("XML faturada benzersiz UUID/ETTN bulunamadı.");

        var totalCost = document.LineExtensionAmount
            ?? document.TaxExclusiveAmount
            ?? Sum(document.Lines.Select(line => line.LineAmount));
        var statedReduction = document.LineExtensionAmount is { } gross
                               && document.TaxExclusiveAmount is { } taxExclusive
                               && gross >= taxExclusive
            ? gross - taxExclusive
            : (decimal?)null;
        var discountAmount = statedReduction
            ?? (Sum(document.Lines.Select(line => line.DiscountAmount)) ?? 0m)
            + ReadDecimal(document.Metadata.GetValueOrDefault("DocumentAllowanceAmount"));
        var kdvAmount = document.TaxAmount
            ?? Sum(document.Lines.Select(line => line.VatAmount));
        var payableAmount = document.PayableAmount
            ?? document.TaxInclusiveAmount
            ?? (totalCost is { } cost && kdvAmount is { } tax ? cost + tax : null);

        LegacyInvoiceMergeCandidate? legacyMerge = null;
        if (mergeLegacyFarmassist && document.IssueDate is not null && payableAmount is not null)
        {
            legacyMergeIndex ??= await LoadLegacyMergeIndexAsync(
                connection, transaction, cancellationToken);
            legacyMerge = legacyMergeIndex.Select(
                invoiceUuid, storedType, document.IssueDate.Value, payableAmount.Value,
                ToMergeParty(document.InvoiceFrom), ToMergeParty(document.InvoiceTo),
                document.DueDate, totalCost, discountAmount, kdvAmount);
        }

        var preservedTraceability = await LoadPreservedTraceabilityAsync(
            connection, transaction,
            legacyMerge is null ? [invoiceUuid] : [invoiceUuid, legacyMerge.InvoiceUuid],
            cancellationToken);

        var medicineLines = await DetectMedicationLinesAsync(
            connection, transaction, document.Lines, cancellationToken);
        var senderId = await UpsertAddressAsync(
            connection, transaction, document.InvoiceFrom, cancellationToken);
        var receiverId = await UpsertAddressAsync(
            connection, transaction, document.InvoiceTo, cancellationToken);

        const string invoiceSql = """
            INSERT INTO public.invoices (
                invoice_uuid, invoice_type, sender_address_id, receiver_address_id,
                issue_date, due_date, database_entry_at, total_cost, discount_amount,
                kdv_amount, payable_amount, is_saved)
            VALUES (
                @invoice_uuid, @invoice_type, @sender_address_id, @receiver_address_id,
                @issue_date, @due_date, @database_entry_at, @total_cost, @discount_amount,
                @kdv_amount, @payable_amount, @is_saved)
            ON CONFLICT (invoice_uuid) DO UPDATE SET
                invoice_type = EXCLUDED.invoice_type,
                sender_address_id = EXCLUDED.sender_address_id,
                receiver_address_id = EXCLUDED.receiver_address_id,
                issue_date = EXCLUDED.issue_date,
                due_date = EXCLUDED.due_date,
                database_entry_at = LEAST(public.invoices.database_entry_at, EXCLUDED.database_entry_at),
                total_cost = EXCLUDED.total_cost,
                discount_amount = EXCLUDED.discount_amount,
                kdv_amount = EXCLUDED.kdv_amount,
                payable_amount = EXCLUDED.payable_amount,
                is_saved = public.invoices.is_saved OR EXCLUDED.is_saved;
            """;
        await using (var invoiceCommand = new NpgsqlCommand(invoiceSql, connection, transaction))
        {
            invoiceCommand.Parameters.AddWithValue("invoice_uuid", invoiceUuid);
            invoiceCommand.Parameters.AddWithValue("invoice_type", storedType);
            invoiceCommand.Parameters.AddWithValue("sender_address_id", senderId);
            invoiceCommand.Parameters.AddWithValue("receiver_address_id", receiverId);
            AddNullable(invoiceCommand, "issue_date", NpgsqlDbType.Date, document.IssueDate);
            AddNullable(invoiceCommand, "due_date", NpgsqlDbType.Date, document.DueDate);
            invoiceCommand.Parameters.AddWithValue(
                "database_entry_at", legacyMerge?.DatabaseEntryAt.ToUniversalTime() ?? DateTimeOffset.UtcNow);
            AddNullable(invoiceCommand, "total_cost", NpgsqlDbType.Numeric, totalCost);
            AddNullable(invoiceCommand, "discount_amount", NpgsqlDbType.Numeric, discountAmount);
            AddNullable(invoiceCommand, "kdv_amount", NpgsqlDbType.Numeric, kdvAmount);
            AddNullable(invoiceCommand, "payable_amount", NpgsqlDbType.Numeric, payableAmount);
            invoiceCommand.Parameters.AddWithValue("is_saved", legacyMerge?.IsSaved ?? false);
            await invoiceCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (legacyMerge is not null)
            await ReplaceLegacyInvoiceAsync(
                connection, transaction, legacyMerge, invoiceUuid, cancellationToken);

        await using (var deleteLines = new NpgsqlCommand(
                         "DELETE FROM public.invoice_lines WHERE invoice_uuid = @invoice_uuid;",
                         connection, transaction))
        {
            deleteLines.Parameters.AddWithValue("invoice_uuid", invoiceUuid);
            await deleteLines.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < document.Lines.Count; index++)
            await InsertLineAsync(
                connection, transaction, invoiceUuid, document.Lines[index], medicineLines[index],
                cancellationToken);
        await InvoiceStockSchema.PrepareInvoiceAsync(
            connection, transaction, invoiceUuid, cancellationToken);
        if (preservedTraceability.Count > 0
            && await MergePreservedTraceabilityAsync(
                connection, transaction, invoiceUuid, preservedTraceability, cancellationToken))
            await InvoiceStockSchema.PrepareInvoiceAsync(
                connection, transaction, invoiceUuid, cancellationToken);
        return legacyMerge;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<InvoiceTraceabilityCode>>>
        LoadPreservedTraceabilityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<string> invoiceUuids,
            CancellationToken cancellationToken)
    {
        if (invoiceUuids.Count == 0) return new Dictionary<string, IReadOnlyList<InvoiceTraceabilityCode>>();
        const string sql = """
            SELECT line.record_id, line.raw_data::text,
                   code.qr_code, code.gtin, code.barcode, code.serial_number,
                   code.batch_number, code.expiry_date, code.raw_code
            FROM public.invoice_lines line
            LEFT JOIN public.invoice_line_qr_codes code ON code.invoice_line_id = line.record_id
            WHERE line.invoice_uuid = ANY(@invoice_uuids)
            ORDER BY line.record_id, code.record_id;
            """;
        var byLine = new Dictionary<int, PreservedLineBuilder>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue(
                "invoice_uuids", NpgsqlDbType.Array | NpgsqlDbType.Text, invoiceUuids.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var lineId = reader.GetInt32(0);
                if (!byLine.TryGetValue(lineId, out var line))
                {
                    line = new PreservedLineBuilder(reader.GetString(1));
                    byLine.Add(lineId, line);
                }
                if (!reader.IsDBNull(2))
                    line.Add(new InvoiceTraceabilityCode(
                        reader.GetString(2), reader.GetString(3), reader.GetString(4),
                        reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateOnly>(7),
                        reader.GetString(8)));
            }
        }

        var result = new Dictionary<string, Dictionary<string, InvoiceTraceabilityCode>>(StringComparer.Ordinal);
        foreach (var line in byLine.Values)
        {
            foreach (var code in line.Codes())
            {
                var barcode = NormalizeBarcode(code.Barcode);
                if (barcode.Length == 0) continue;
                if (!result.TryGetValue(barcode, out var codes))
                {
                    codes = new Dictionary<string, InvoiceTraceabilityCode>(StringComparer.Ordinal);
                    result.Add(barcode, codes);
                }
                codes.TryAdd(code.QrCode, code);
            }
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<InvoiceTraceabilityCode>)pair.Value.Values.ToArray(),
            StringComparer.Ordinal);
    }

    private static async Task<bool> MergePreservedTraceabilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string invoiceUuid,
        IReadOnlyDictionary<string, IReadOnlyList<InvoiceTraceabilityCode>> preserved,
        CancellationToken cancellationToken)
    {
        var targets = new List<TraceabilityTarget>();
        const string sql = """
            SELECT line.record_id, line.line_number, line.barcode, line.item_code,
                   line.quantity, line.raw_data::text,
                   COALESCE(array_agg(code.qr_code ORDER BY code.record_id)
                       FILTER (WHERE code.qr_code IS NOT NULL), ARRAY[]::text[])
            FROM public.invoice_lines line
            LEFT JOIN public.invoice_line_qr_codes code ON code.invoice_line_id = line.record_id
            WHERE line.invoice_uuid = @invoice_uuid
            GROUP BY line.record_id
            ORDER BY line.line_number, line.record_id;
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var barcode = NormalizeBarcode(FirstNonEmpty(reader.GetString(2), reader.GetString(3)));
                if (barcode.Length == 0 || !preserved.ContainsKey(barcode)) continue;
                targets.Add(new TraceabilityTarget(
                    reader.GetInt32(0), reader.GetInt32(1), barcode,
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetString(5),
                    reader.GetFieldValue<string[]>(6).ToHashSet(StringComparer.Ordinal)));
            }
        }

        var changed = false;
        foreach (var group in targets.GroupBy(target => target.Barcode, StringComparer.Ordinal))
        {
            var used = group.SelectMany(target => target.QrCodes).ToHashSet(StringComparer.Ordinal);
            var available = preserved[group.Key].Where(code => !used.Contains(code.QrCode)).ToList();
            foreach (var target in group.OrderBy(item => item.LineNumber).ThenBy(item => item.RecordId))
            {
                if (target.Quantity is null || target.Quantity <= 0
                    || target.Quantity != decimal.Truncate(target.Quantity.Value)
                    || target.Quantity > int.MaxValue)
                    continue;
                var capacity = decimal.ToInt32(target.Quantity.Value) - target.QrCodes.Count;
                if (capacity <= 0 || available.Count == 0) continue;
                var additions = available.Take(capacity).ToArray();
                available.RemoveRange(0, additions.Length);
                foreach (var code in additions)
                {
                    target.QrCodes.Add(code.QrCode);
                    used.Add(code.QrCode);
                }

                var rawValues = DeserializeRawValues(target.RawData);
                var assigned = preserved[group.Key]
                    .Where(code => target.QrCodes.Contains(code.QrCode))
                    .Select(ToEczaciKartTraceabilityText)
                    .ToArray();
                rawValues["PreservedTraceability"] = string.Join(" | ", assigned);
                await using var update = new NpgsqlCommand(
                    "UPDATE public.invoice_lines SET raw_data=@raw_data WHERE record_id=@record_id",
                    connection, transaction);
                update.Parameters.AddWithValue(
                    "raw_data", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(rawValues));
                update.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, target.RecordId);
                await update.ExecuteNonQueryAsync(cancellationToken);
                changed = true;
            }
        }
        return changed;
    }

    private static Dictionary<string, string> DeserializeRawValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ToEczaciKartTraceabilityText(InvoiceTraceabilityCode code) =>
        $"(GTIN){code.Gtin}(BN){code.BatchNumber}(SN){code.SerialNumber}(XD){code.ExpiryDate:yyMMdd}";

    private static string NormalizeBarcode(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 14 && digits[0] == '0' ? digits[1..] : digits;
    }

    private static async Task<LegacyInvoiceMergeIndex> LoadLegacyMergeIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
                         "SELECT to_regclass('public.farmassist_invoice_import_map') IS NOT NULL;",
                         connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
                return new LegacyInvoiceMergeIndex([], []);
        }

        const string sql = """
            SELECT map.source_fatura_id, invoice.invoice_uuid, invoice.invoice_type,
                   invoice.issue_date, invoice.due_date, invoice.database_entry_at,
                   invoice.total_cost, invoice.discount_amount, invoice.kdv_amount,
                   invoice.payable_amount, invoice.is_saved,
                   sender.party_name, sender.identity_no, sender.gln,
                   receiver.party_name, receiver.identity_no, receiver.gln
            FROM public.farmassist_invoice_import_map map
            JOIN public.invoices invoice ON invoice.invoice_uuid = map.target_invoice_uuid
            JOIN public.addresses sender ON sender.record_id = invoice.sender_address_id
            JOIN public.addresses receiver ON receiver.record_id = invoice.receiver_address_id
            WHERE invoice.invoice_uuid LIKE 'farmassist:fatura:%'
            ORDER BY map.source_fatura_id;
            """;
        var rows = new List<LegacyInvoiceMergeRow>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(3) || reader.IsDBNull(9)) continue;
                rows.Add(new LegacyInvoiceMergeRow(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    reader.GetFieldValue<DateOnly>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    reader.GetDecimal(9), reader.GetBoolean(10),
                    new LegacyInvoiceMergeParty(reader.GetString(11), reader.GetString(12), reader.GetString(13)),
                    new LegacyInvoiceMergeParty(reader.GetString(14), reader.GetString(15), reader.GetString(16))));
            }
        }

        const string mappedSql = """
            SELECT target_invoice_uuid
            FROM public.farmassist_invoice_import_map
            WHERE target_invoice_uuid NOT LIKE 'farmassist:fatura:%';
            """;
        var mappedTargets = new List<string>();
        await using (var command = new NpgsqlCommand(mappedSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) mappedTargets.Add(reader.GetString(0));
        return new LegacyInvoiceMergeIndex(rows, mappedTargets);
    }

    private static async Task ReplaceLegacyInvoiceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LegacyInvoiceMergeCandidate legacy,
        string invoiceUuid,
        CancellationToken cancellationToken)
    {
        const string mapSql = """
            UPDATE public.farmassist_invoice_import_map
            SET target_invoice_uuid = @invoice_uuid,
                match_method = 'header',
                imported_at = now()
            WHERE source_fatura_id = @source_fatura_id
              AND target_invoice_uuid = @legacy_uuid;
            """;
        await using (var command = new NpgsqlCommand(mapSql, connection, transaction))
        {
            command.Parameters.AddWithValue("invoice_uuid", invoiceUuid);
            command.Parameters.AddWithValue("source_fatura_id", legacy.SourceFaturaId);
            command.Parameters.AddWithValue("legacy_uuid", legacy.InvoiceUuid);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException(
                    $"Farmassist fatura eşlemesi güncellenemedi: {legacy.SourceFaturaId}.");
        }

        await using var delete = new NpgsqlCommand(
            "DELETE FROM public.invoices WHERE invoice_uuid = @legacy_uuid;",
            connection, transaction);
        delete.Parameters.AddWithValue("legacy_uuid", legacy.InvoiceUuid);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidDataException(
                $"Birleştirilen Farmassist faturası kaldırılamadı: {legacy.InvoiceUuid}.");
    }

    private static LegacyInvoiceMergeParty ToMergeParty(InvoiceParty party) =>
        new(party.Name.Trim(), ReadIdentityNo(party), ReadGln(party));

    private static async Task<int> UpsertAddressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InvoiceParty party,
        CancellationToken cancellationToken)
    {
        var name = party.Name.Trim();
        var identityNo = ReadIdentityNo(party);
        var gln = ReadGln(party);
        const string selectSql = """
            SELECT a.record_id, a.party_name, a.identity_no, a.gln,
                   count(invoice.invoice_uuid) AS invoice_count
            FROM public.addresses a
            LEFT JOIN public.invoices invoice
              ON invoice.sender_address_id = a.record_id
              OR invoice.receiver_address_id = a.record_id
            WHERE (@identity_no <> '' AND a.identity_no = @identity_no)
               OR (@gln <> '' AND a.gln = @gln)
            GROUP BY a.record_id
            ORDER BY count(invoice.invoice_uuid) DESC, a.record_id;
            """;
        var candidates = new List<AddressCandidate>();
        await using (var select = new NpgsqlCommand(selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("identity_no", identityNo);
            select.Parameters.AddWithValue("gln", gln);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add(new AddressCandidate(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var identityMatches = identityNo.Length == 0
            ? []
            : candidates.Where(candidate => candidate.IdentityNo == identityNo).ToArray();
        var glnMatches = gln.Length == 0
            ? []
            : candidates.Where(candidate => candidate.Gln == gln).ToArray();
        if (identityMatches.Length > 0 && glnMatches.Length > 0
            && !identityMatches.Any(identity => glnMatches.Any(candidate => candidate.RecordId == identity.RecordId)))
            throw new InvalidDataException(
                $"Adres kimlik çakışması: VKN/TCKN {identityNo} ve GLN {gln} farklı adres kayıtlarına ait.");

        var existing = identityMatches.IntersectBy(glnMatches.Select(candidate => candidate.RecordId), candidate => candidate.RecordId)
            .FirstOrDefault()
            ?? identityMatches.FirstOrDefault()
            ?? glnMatches.FirstOrDefault();
        if (existing is not null)
        {
            if (identityNo.Length > 0 && existing.IdentityNo.Length > 0 && existing.IdentityNo != identityNo)
                throw new InvalidDataException(
                    $"GLN {gln}, farklı VKN/TCKN değerleriyle eşleşiyor: {existing.IdentityNo} ve {identityNo}.");
            if (gln.Length > 0 && existing.Gln.Length > 0 && existing.Gln != gln)
                throw new InvalidDataException(
                    $"VKN/TCKN {identityNo}, farklı GLN değerleriyle eşleşiyor: {existing.Gln} ve {gln}.");
            const string enrichSql = """
                UPDATE public.addresses
                SET identity_no = CASE WHEN identity_no = '' THEN @identity_no ELSE identity_no END,
                    gln = CASE WHEN gln = '' THEN @gln ELSE gln END
                WHERE record_id = @record_id;
                """;
            await using var enrich = new NpgsqlCommand(enrichSql, connection, transaction);
            enrich.Parameters.AddWithValue("identity_no", identityNo);
            enrich.Parameters.AddWithValue("gln", gln);
            enrich.Parameters.AddWithValue("record_id", existing.RecordId);
            await enrich.ExecuteNonQueryAsync(cancellationToken);
            return existing.RecordId;
        }

        if (identityNo.Length == 0 && gln.Length == 0)
        {
            const string nameSql = """
                SELECT record_id
                FROM public.addresses
                WHERE lower(trim(party_name)) = lower(trim(@party_name))
                  AND identity_no = '' AND gln = ''
                ORDER BY record_id
                LIMIT 1;
                """;
            await using var byName = new NpgsqlCommand(nameSql, connection, transaction);
            byName.Parameters.AddWithValue("party_name", name);
            var nameMatch = await byName.ExecuteScalarAsync(cancellationToken);
            if (nameMatch is not null) return Convert.ToInt32(nameMatch);
        }

        const string insertSql = """
            INSERT INTO public.addresses (party_name, identity_no, gln)
            VALUES (@party_name, @identity_no, @gln)
            RETURNING record_id;
            """;
        await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
        insert.Parameters.AddWithValue("party_name", name);
        insert.Parameters.AddWithValue("identity_no", identityNo);
        insert.Parameters.AddWithValue("gln", gln);
        return Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string invoiceUuid,
        InvoiceDocumentLine line,
        bool isMedication,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.invoice_lines (
                invoice_uuid, line_number, item_code, barcode, item_name, description,
                quantity, unit, unit_price, vat_rate, vat_amount, discount_amount,
                line_amount, currency, is_medication, raw_data)
            VALUES (
                @invoice_uuid, @line_number, @item_code, @barcode, @item_name, @description,
                @quantity, @unit, @unit_price, @vat_rate, @vat_amount, @discount_amount,
                @line_amount, @currency, @is_medication, @raw_data);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("invoice_uuid", invoiceUuid);
        command.Parameters.AddWithValue("line_number", line.LineNumber);
        command.Parameters.AddWithValue("item_code", line.ItemCode);
        command.Parameters.AddWithValue("barcode", line.Barcode);
        command.Parameters.AddWithValue("item_name", line.Name);
        command.Parameters.AddWithValue("description", StoredDescription(line, isMedication));
        AddNullable(command, "quantity", NpgsqlDbType.Numeric, line.Quantity);
        command.Parameters.AddWithValue("unit", line.Unit);
        AddNullable(command, "unit_price", NpgsqlDbType.Numeric, line.UnitPrice);
        AddNullable(command, "vat_rate", NpgsqlDbType.Numeric, line.VatRate);
        AddNullable(command, "vat_amount", NpgsqlDbType.Numeric, line.VatAmount);
        AddNullable(command, "discount_amount", NpgsqlDbType.Numeric, line.DiscountAmount);
        AddNullable(command, "line_amount", NpgsqlDbType.Numeric, line.LineAmount);
        command.Parameters.AddWithValue("currency", line.Currency);
        command.Parameters.AddWithValue("is_medication", isMedication);
        command.Parameters.AddWithValue(
            "raw_data", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(line.Values));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool[]> DetectMedicationLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<InvoiceDocumentLine> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return [];
        var barcodes = lines
            .SelectMany(line => new[] { line.Barcode, line.ItemCode }.Concat(line.Values.Values))
            .SelectMany(value => Regex.Matches(value ?? string.Empty, "(?<!\\d)\\d{8,14}(?!\\d)")
                .Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var names = lines.SelectMany(line => new[] { line.Name, line.Description })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        const string sql = """
            SELECT
                COALESCE(array_agg(DISTINCT barcode) FILTER (WHERE barcode IS NOT NULL), ARRAY[]::varchar[]),
                COALESCE(array_agg(DISTINCT upper(medicine_name)) FILTER (WHERE medicine_name IS NOT NULL), ARRAY[]::text[]),
                COALESCE(array_agg(DISTINCT upper(old_name)) FILTER (WHERE old_name IS NOT NULL), ARRAY[]::text[]),
                COALESCE(array_agg(DISTINCT upper(brand_name)) FILTER (WHERE brand_name IS NOT NULL), ARRAY[]::text[])
            FROM public.barcode
            WHERE barcode = ANY(@barcodes)
               OR ('0' || barcode) = ANY(@barcodes)
               OR upper(medicine_name) = ANY(@names)
               OR upper(old_name) = ANY(@names)
               OR EXISTS (
                    SELECT 1 FROM unnest(@names) AS invoice_name
                    WHERE length(trim(medicine_name)) >= 5
                      AND strpos(invoice_name, upper(trim(medicine_name))) > 0)
               OR EXISTS (
                    SELECT 1 FROM unnest(@names) AS invoice_name
                    WHERE length(trim(old_name)) >= 5
                      AND strpos(invoice_name, upper(trim(old_name))) > 0)
               OR EXISTS (
                    SELECT 1 FROM unnest(@names) AS invoice_name
                    WHERE length(split_part(trim(medicine_name), ' ', 1)) >= 4
                      AND split_part(invoice_name, ' ', 1) = upper(split_part(trim(medicine_name), ' ', 1)))
               OR EXISTS (
                    SELECT 1 FROM unnest(@names) AS invoice_name
                    WHERE length(split_part(trim(old_name), ' ', 1)) >= 4
                      AND split_part(invoice_name, ' ', 1) = upper(split_part(trim(old_name), ' ', 1)))
               OR EXISTS (
                    SELECT 1 FROM unnest(@names) AS invoice_name
                    WHERE length(split_part(trim(brand_name), ' ', 1)) >= 4
                      AND split_part(invoice_name, ' ', 1) = upper(split_part(trim(brand_name), ' ', 1)));
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "barcodes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, barcodes);
        command.Parameters.AddWithValue(
            "names", NpgsqlDbType.Array | NpgsqlDbType.Text,
            names.Select(value => value.ToUpperInvariant()).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var matchedBarcodes = reader.GetFieldValue<string[]>(0).ToHashSet(StringComparer.Ordinal);
        var matchedNames = reader.GetFieldValue<string[]>(1)
            .Concat(reader.GetFieldValue<string[]>(2))
            .Concat(reader.GetFieldValue<string[]>(3))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return lines.Select(line =>
            new[] { line.Barcode, line.ItemCode }.Any(value =>
                matchedBarcodes.Contains(value)
                || matchedBarcodes.Contains(value.TrimStart('0')))
            || new[] { line.Name, line.Description }.Any(value =>
                matchedNames.Any(name => name.Length >= 5 &&
                    (value.Contains(name, StringComparison.OrdinalIgnoreCase)
                     || FirstToken(value).Equals(FirstToken(name), StringComparison.OrdinalIgnoreCase))))
            || InvoiceMedicationClassifier.HasPharmacyPricingFields(line.Values.Keys)
            || HasItsTraceabilityData(line.Values.Values)
            || line.Values.Values.Any(value => Regex.Matches(
                    value ?? string.Empty, "(?<!\\d)\\d{8,14}(?!\\d)")
                .Any(match => matchedBarcodes.Contains(match.Value)
                              || matchedBarcodes.Contains(match.Value.TrimStart('0')))))
            .ToArray();
    }

    internal static string StoredDescription(InvoiceDocumentLine line, bool isMedication) =>
        isMedication
        && line.Description.Trim().Equals(line.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : line.Description;

    internal static string ReadIdentityNo(InvoiceParty party)
    {
        var candidates = new[] { party.Tckn, party.Vkn }.Concat(party.Identifiers.Values);
        return candidates.Select(Digits)
            .FirstOrDefault(value => value.Length == 11)
            ?? candidates.Select(Digits).FirstOrDefault(value => value.Length == 10)
            ?? string.Empty;
    }

    internal static string ReadGln(InvoiceParty party)
    {
        var candidates = new[] { party.Gln }.Concat(party.Identifiers
            .Where(pair => pair.Key.Contains("GLN", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value));
        return candidates.Select(Digits).FirstOrDefault(value => value.Length == 13)
               ?? string.Empty;
    }

    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static decimal ReadDecimal(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : 0m;

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
    private static bool HasItsTraceabilityData(IEnumerable<string> values)
        => InvoiceTraceabilityParser.Parse(values).HasTraceabilityMarkers;
    private static string FirstToken(string value) =>
        value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private sealed class PreservedLineBuilder(string rawData)
    {
        private readonly Dictionary<string, InvoiceTraceabilityCode> codes = new(StringComparer.Ordinal);

        public void Add(InvoiceTraceabilityCode code) => codes.TryAdd(code.QrCode, code);

        public IReadOnlyList<InvoiceTraceabilityCode> Codes()
        {
            var parsed = InvoiceTraceabilityParser.Parse(DeserializeRawValues(rawData));
            foreach (var code in parsed.Codes) Add(code);
            return codes.Values.ToArray();
        }
    }

    private sealed record TraceabilityTarget(
        int RecordId,
        int LineNumber,
        string Barcode,
        decimal? Quantity,
        string RawData,
        HashSet<string> QrCodes);

    private sealed record AddressCandidate(
        int RecordId, string PartyName, string IdentityNo, string Gln);

    private sealed record LegacyInvoiceMergeRow(
        int SourceFaturaId,
        string InvoiceUuid,
        string InvoiceType,
        DateOnly IssueDate,
        DateOnly? DueDate,
        DateTimeOffset DatabaseEntryAt,
        decimal? TotalCost,
        decimal? DiscountAmount,
        decimal? KdvAmount,
        decimal PayableAmount,
        bool IsSaved,
        LegacyInvoiceMergeParty Sender,
        LegacyInvoiceMergeParty Receiver)
    {
        public LegacyInvoiceMergeCandidate ToCandidate() => new(
            SourceFaturaId, InvoiceUuid, IsSaved, DatabaseEntryAt, DueDate,
            TotalCost, DiscountAmount, KdvAmount, Sender, Receiver);
    }

    private sealed class LegacyInvoiceMergeIndex
    {
        private readonly Dictionary<LegacyInvoiceMergeKey, List<LegacyInvoiceMergeRow>> rows;
        private readonly HashSet<string> mappedTargets;

        public LegacyInvoiceMergeIndex(
            IEnumerable<LegacyInvoiceMergeRow> sourceRows,
            IEnumerable<string> existingMappedTargets)
        {
            rows = sourceRows.GroupBy(row => new LegacyInvoiceMergeKey(
                    row.InvoiceType, row.IssueDate, RoundMoney(row.PayableAmount)))
                .ToDictionary(group => group.Key, group => group.ToList());
            mappedTargets = existingMappedTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public LegacyInvoiceMergeCandidate? Select(
            string invoiceUuid,
            string invoiceType,
            DateOnly issueDate,
            decimal payableAmount,
            LegacyInvoiceMergeParty sender,
            LegacyInvoiceMergeParty receiver,
            DateOnly? dueDate,
            decimal? totalCost,
            decimal? discountAmount,
            decimal? kdvAmount)
        {
            if (mappedTargets.Contains(invoiceUuid)) return null;
            var key = new LegacyInvoiceMergeKey(invoiceType, issueDate, RoundMoney(payableAmount));
            return rows.TryGetValue(key, out var candidates)
                ? LegacyInvoiceMergeMatcher.Select(
                    candidates.Select(candidate => candidate.ToCandidate()), sender, receiver,
                    dueDate, totalCost, discountAmount, kdvAmount)
                : null;
        }

        public void MarkMerged(string legacyUuid, string invoiceUuid)
        {
            foreach (var pair in rows.ToArray())
            {
                pair.Value.RemoveAll(row =>
                    row.InvoiceUuid.Equals(legacyUuid, StringComparison.OrdinalIgnoreCase));
                if (pair.Value.Count == 0) rows.Remove(pair.Key);
            }
            mappedTargets.Add(invoiceUuid);
        }

        private static decimal RoundMoney(decimal value) =>
            decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private sealed record LegacyInvoiceMergeKey(
        string InvoiceType,
        DateOnly IssueDate,
        decimal PayableAmount);

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
