using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Takas;

public sealed class TakasRepository
{
    public async Task<IReadOnlyList<TakasPharmacySuggestion>> SearchPharmaciesAsync(
        string search,
        CancellationToken cancellationToken)
    {
        var term = RetailTurkishSearch.Normalize(search);
        if (term.Length < 2) return [];

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT record_id,
                   COALESCE(gln_code, ''),
                   COALESCE(pharmacy_name, gln_code, record_id::text),
                   COALESCE(pharmacist_name, ''),
                   COALESCE(debt_balance, 0)
            FROM public.pharmacy
            WHERE gln_code IS NOT NULL
              AND (LOWER(TRANSLATE(COALESCE(pharmacy_name, ''), 'İIŞĞÜÖÇ', 'iışğüöç')) LIKE @contains
                   OR LOWER(TRANSLATE(COALESCE(pharmacist_name, ''), 'İIŞĞÜÖÇ', 'iışğüöç')) LIKE @contains
                   OR gln_code LIKE @prefix)
            ORDER BY CASE WHEN gln_code LIKE @prefix THEN 0 ELSE 1 END,
                     pharmacy_name NULLS LAST
            LIMIT 20
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("contains", NpgsqlDbType.Varchar, "%" + term + "%");
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Varchar, term + "%");
        var results = new List<TakasPharmacySuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new TakasPharmacySuggestion(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4)));
        return results;
    }

    public async Task<TakasStockItem?> ResolveItemAsync(string enteredCode, CancellationToken cancellationToken)
    {
        var code = enteredCode.Trim();
        if (code.Length == 0) return null;
        var normalizedCode = RetailQrCodeNormalizer.Normalize(code);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT active.qr_code,
                   active.barcode,
                   active.status,
                   active.expiry_date,
                   active.purchase_cost,
                   product.record_id,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), product.barcode, active.barcode),
                   COALESCE(aggregate.quantity, 0),
                   price.warehouse_price,
                   COALESCE(price.sale_price, price.public_price, price.public_paid_price, 0),
                   price.public_price,
                   (product.photo)[1]
            FROM public."qrActive" active
            LEFT JOIN public.barcode product ON product.barcode = active.barcode
            LEFT JOIN public."barcodeActive" aggregate ON aggregate.barcode = active.barcode
            LEFT JOIN public."barcodePrices" price ON price.record_reference_no = product.record_id
            WHERE active.qr_code = @code
               OR regexp_replace(
                    regexp_replace(active.qr_code, E'^\\]d2', ''),
                    E'[()\\x1D]', '', 'g') = @normalized_code
            ORDER BY CASE WHEN active.qr_code = @code THEN 0 ELSE 1 END
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("normalized_code", NpgsqlDbType.Varchar, normalizedCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var storedQr = reader.GetString(0);
        if (!reader.IsDBNull(2) && !string.Equals(reader.GetString(2), "ACTIVE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Karekodun stok durumu Takas için uygun değil: {reader.GetString(2)}");
        if (!ItsQrParser.TryParse(storedQr, out var itsProduct, out var parseError) || itsProduct is null)
            throw new InvalidOperationException($"Karekod İTS Mal Devir için okunamadı: {parseError}");

        return new TakasStockItem(
            storedQr,
            reader.GetString(1),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            reader.IsDBNull(8) ? null : reader.GetDecimal(8),
            reader.GetDecimal(9),
            reader.IsDBNull(10) ? null : reader.GetDecimal(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11),
            itsProduct);
    }

    public async Task<TakasStockItem> ResolveIncomingItemAsync(
        string enteredCode,
        CancellationToken cancellationToken)
    {
        var code = enteredCode.Trim();
        if (!ItsQrParser.TryParse(code, out var itsProduct, out var parseError) || itsProduct is null)
            throw new InvalidOperationException($"Karekod İTS Mal Alım için okunamadı: {parseError}");

        var normalizedCode = RetailQrCodeNormalizer.Normalize(code);
        var barcode = itsProduct.Gtin.Length == 14 && itsProduct.Gtin[0] == '0'
            ? itsProduct.Gtin[1..]
            : itsProduct.Gtin;

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string conflictSql = """
            SELECT location
            FROM (
                SELECT 'aktif stok' AS location
                FROM public."qrActive"
                WHERE qr_code = @code
                   OR regexp_replace(regexp_replace(qr_code, E'^\\]d2', ''), E'[()\\x1D]', '', 'g') = @normalized_code
                UNION ALL
                SELECT 'bekleyen stok'
                FROM public."qrHanged"
                WHERE qr_code = @code
                   OR regexp_replace(regexp_replace(qr_code, E'^\\]d2', ''), E'[()\\x1D]', '', 'g') = @normalized_code
                UNION ALL
                SELECT 'sonucu kesinleşmemiş bir Takas'
                FROM public."takasQrItems"
                WHERE (qr_code = @code
                    OR regexp_replace(regexp_replace(qr_code, E'^\\]d2', ''), E'[()\\x1D]', '', 'g') = @normalized_code)
                  AND status IN ('pending_transfer', 'in_transit', 'reconciliation_required')
            ) conflicts
            LIMIT 1
            """;
        await using (var conflict = pharmacyApp.Infrastructure.DatabaseCommand.Create(conflictSql, connection))
        {
            conflict.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
            conflict.Parameters.AddWithValue("normalized_code", NpgsqlDbType.Varchar, normalizedCode);
            var location = Convert.ToString(await conflict.ExecuteScalarAsync(cancellationToken));
            if (!string.IsNullOrWhiteSpace(location))
                throw new InvalidOperationException($"Karekod zaten {location} içinde bulunuyor.");
        }

        const string sql = """
            WITH input AS (
                SELECT @code::varchar AS qr_code,
                       @normalized_code::varchar AS normalized_code,
                       @barcode::varchar AS barcode
            )
            SELECT COALESCE(inactive.qr_code, input.qr_code),
                   COALESCE(inactive.barcode, input.barcode),
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), product.barcode, input.barcode),
                   COALESCE(stock.quantity, 0),
                   inactive.expiry_date,
                   inactive.entry_cost,
                   price.warehouse_price,
                   COALESCE(price.sale_price, price.public_price, price.public_paid_price, 0),
                   price.public_price,
                   (product.photo)[1]
            FROM input
            LEFT JOIN LATERAL (
                SELECT qr_code, barcode, expiry_date, entry_cost
                FROM public."qrInactive"
                WHERE qr_code = input.qr_code
                   OR regexp_replace(regexp_replace(qr_code, E'^\\]d2', ''), E'[()\\x1D]', '', 'g') = input.normalized_code
                ORDER BY CASE WHEN qr_code = input.qr_code THEN 0 ELSE 1 END
                LIMIT 1
            ) inactive ON TRUE
            LEFT JOIN public.barcode product ON product.barcode = COALESCE(inactive.barcode, input.barcode)
            LEFT JOIN public."barcodeActive" stock ON stock.barcode = COALESCE(inactive.barcode, input.barcode)
            LEFT JOIN public."barcodePrices" price ON price.record_reference_no = product.record_id
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("normalized_code", NpgsqlDbType.Varchar, normalizedCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, barcode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Karekod ürün bilgileri hazırlanamadı.");

        return new TakasStockItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetDecimal(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<byte[]>(9),
            itsProduct);
    }

    public async Task<int> PrepareOutgoingAsync(
        int pharmacyId,
        string sourceGln,
        string destinationGln,
        IReadOnlyList<TakasLineInput> lines,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) throw new InvalidOperationException("Takas listesi boş.");
        if (!Gs1IdentifierValidator.IsValidGln13(sourceGln))
            throw new InvalidOperationException("Ayarlar bölümündeki eczane GLN kodu geçerli değil.");
        if (!Gs1IdentifierValidator.IsValidGln13(destinationGln))
            throw new InvalidOperationException("Seçilen eczanenin GLN kodu geçerli değil.");
        if (sourceGln == destinationGln)
            throw new InvalidOperationException("Aynı eczaneye Mal Devir yapılamaz.");
        if (lines.Any(line => line.DebtAmount < 0))
            throw new InvalidOperationException("Takas borç değeri negatif olamaz.");

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await ValidatePharmacyAsync(connection, transaction, pharmacyId, destinationGln, cancellationToken);
            foreach (var line in lines)
                await ValidateQrStockAsync(connection, transaction, line.Item, cancellationToken);

            var takasId = await InsertTakasAsync(
                connection, transaction, pharmacyId, "outgoing", sourceGln, destinationGln,
                notes, userId, cancellationToken);
            foreach (var line in lines)
                await InsertTakasItemAsync(connection, transaction, takasId, line, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return takasId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> PrepareIncomingAsync(
        int pharmacyId,
        string sourceGln,
        string destinationGln,
        IReadOnlyList<TakasLineInput> lines,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) throw new InvalidOperationException("Takas listesi boş.");
        if (!Gs1IdentifierValidator.IsValidGln13(sourceGln))
            throw new InvalidOperationException("Seçilen gönderen eczanenin GLN kodu geçerli değil.");
        if (!Gs1IdentifierValidator.IsValidGln13(destinationGln))
            throw new InvalidOperationException("Ayarlar bölümündeki eczane GLN kodu geçerli değil.");
        if (sourceGln == destinationGln)
            throw new InvalidOperationException("Aynı eczaneden Mal Alım yapılamaz.");
        if (lines.Any(line => line.DebtAmount < 0))
            throw new InvalidOperationException("Takas borç değeri negatif olamaz.");

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await ValidatePharmacyAsync(connection, transaction, pharmacyId, sourceGln, cancellationToken);
            foreach (var line in lines)
                await ValidateIncomingQrStockAsync(connection, transaction, line.Item, cancellationToken);

            var takasId = await InsertTakasAsync(
                connection, transaction, pharmacyId, "incoming", sourceGln, destinationGln,
                notes, userId, cancellationToken);
            foreach (var line in lines)
                await InsertTakasItemAsync(connection, transaction, takasId, line, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return takasId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TakasFinalizeResult> ApplyTransferResultAsync(
        int takasId,
        ItsNotificationResult result,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = """
                SELECT pharmacy_id, status, debt_applied_at
                FROM public.takas
                WHERE record_id = @takas_id
                FOR UPDATE
                """;
            int pharmacyId;
            await using (var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection, transaction))
            {
                header.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Hazırlanan Takas kaydı bulunamadı.");
                pharmacyId = reader.GetInt32(0);
                if (!reader.IsDBNull(2))
                    throw new InvalidOperationException("Bu Takas sonucu daha önce yerel stoka uygulanmış.");
            }

            var items = await LoadPendingItemsAsync(connection, transaction, takasId, cancellationToken);
            var resultByProduct = result.Products
                .GroupBy(product => ProductKey(product.Gtin, product.SerialNumber))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var successCount = 0;
            var failureCount = 0;
            var appliedDebt = 0m;
            var referenceCode = $"T{takasId}";

            foreach (var item in items)
            {
                resultByProduct.TryGetValue(ProductKey(item.Gtin, item.SerialNumber), out var productResult);
                var resultCode = productResult?.ResultCode ?? string.Empty;
                if (result.HttpStatusCode is >= 200 and <= 299 &&
                    string.IsNullOrWhiteSpace(result.FaultCode) &&
                    resultCode == "00000")
                {
                    await InsertMovementAsync(
                        connection, transaction, item.QrCode, referenceCode,
                        item.DestinationGln, userId, cancellationToken);
                    await UpdateItemResultAsync(
                        connection, transaction, item.RecordId, "in_transit", resultCode, null, cancellationToken);
                    successCount++;
                    appliedDebt += item.DebtAmount;
                }
                else
                {
                    var error = resultCode.Length == 0
                        ? (result.FaultMessage ?? $"HTTP {result.HttpStatusCode}")
                        : $"İTS sonuç kodu: {resultCode}";
                    await UpdateItemResultAsync(
                        connection, transaction, item.RecordId, "transfer_failed", resultCode, error, cancellationToken);
                    failureCount++;
                }
            }

            if (appliedDebt > 0)
                await IncreasePharmacyDebtAsync(connection, transaction, pharmacyId, appliedDebt, cancellationToken);

            var status = successCount switch
            {
                0 => "failed",
                _ when failureCount == 0 => "in_transit",
                _ => "partially_transferred"
            };
            var lastError = BuildResultError(result, failureCount);
            const string updateHeaderSql = """
                UPDATE public.takas
                SET status = @status,
                    debt_amount = @debt_amount,
                    transfer_notification_id = @notification_id,
                    transferred_at = CASE WHEN @successful_count > 0 THEN CURRENT_TIMESTAMP ELSE NULL END,
                    debt_applied_at = CASE WHEN @successful_count > 0 THEN CURRENT_TIMESTAMP ELSE NULL END,
                    next_status_check_at = CASE WHEN @successful_count > 0 THEN CURRENT_TIMESTAMP + INTERVAL '24 hours' ELSE NULL END,
                    last_status_error = @last_error,
                    updated_at = CURRENT_TIMESTAMP
                WHERE record_id = @takas_id
                """;
            await using (var updateHeader = pharmacyApp.Infrastructure.DatabaseCommand.Create(updateHeaderSql, connection, transaction))
            {
                updateHeader.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
                updateHeader.Parameters.AddWithValue("debt_amount", NpgsqlDbType.Numeric, appliedDebt);
                updateHeader.Parameters.Add(new NpgsqlParameter("notification_id", NpgsqlDbType.Varchar)
                {
                    Value = result.NotificationId ?? (object)DBNull.Value
                });
                updateHeader.Parameters.AddWithValue("successful_count", NpgsqlDbType.Integer, successCount);
                updateHeader.Parameters.Add(new NpgsqlParameter("last_error", NpgsqlDbType.Text)
                {
                    Value = lastError ?? (object)DBNull.Value
                });
                updateHeader.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await updateHeader.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new TakasFinalizeResult(
                takasId, successCount, failureCount, appliedDebt, result.NotificationId, status);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TakasFinalizeResult> ApplyAcceptanceResultAsync(
        int takasId,
        ItsNotificationResult result,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = """
                SELECT pharmacy_id, status, direction, debt_applied_at, accept_notification_id
                FROM public.takas
                WHERE record_id = @takas_id
                FOR UPDATE
                """;
            int pharmacyId;
            await using (var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection, transaction))
            {
                header.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Hazırlanan gelen Takas kaydı bulunamadı.");
                pharmacyId = reader.GetInt32(0);
                if (!string.Equals(reader.GetString(1), "pending_transfer", StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), "incoming", StringComparison.Ordinal))
                    throw new InvalidOperationException("Bu kayıt İTS Mal Alım sonucunu uygulamaya uygun değil.");
                if (!reader.IsDBNull(3) || !reader.IsDBNull(4))
                    throw new InvalidOperationException("Bu Mal Alım sonucu daha önce yerel stoka uygulanmış.");
            }

            var items = await LoadPendingIncomingItemsAsync(connection, transaction, takasId, cancellationToken);
            var resultByProduct = result.Products
                .GroupBy(product => ProductKey(product.Gtin, product.SerialNumber))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var successCount = 0;
            var failureCount = 0;
            var appliedDebt = 0m;
            var referenceCode = $"T{takasId}";

            foreach (var item in items)
            {
                resultByProduct.TryGetValue(ProductKey(item.Gtin, item.SerialNumber), out var productResult);
                var resultCode = productResult?.ResultCode ?? string.Empty;
                if (result.HttpStatusCode is >= 200 and <= 299 &&
                    string.IsNullOrWhiteSpace(result.FaultCode) &&
                    resultCode == "00000")
                {
                    await InsertIncomingMovementAsync(
                        connection, transaction, item.QrCode, referenceCode,
                        item.SourceGln, userId, cancellationToken);
                    await UpdateAcceptanceItemResultAsync(
                        connection, transaction, item.RecordId, "accepted", resultCode, null, cancellationToken);
                    successCount++;
                    appliedDebt += item.DebtAmount;
                }
                else
                {
                    var error = resultCode.Length == 0
                        ? (result.FaultMessage ?? $"HTTP {result.HttpStatusCode}")
                        : $"İTS sonuç kodu: {resultCode}";
                    await UpdateAcceptanceItemResultAsync(
                        connection, transaction, item.RecordId, "transfer_failed", resultCode, error, cancellationToken);
                    failureCount++;
                }
            }

            if (appliedDebt > 0)
                await DecreasePharmacyDebtAsync(connection, transaction, pharmacyId, appliedDebt, cancellationToken);

            var status = successCount switch
            {
                0 => "failed",
                _ when failureCount == 0 => "accepted",
                _ => "partially_accepted"
            };
            var lastError = BuildResultError(result, failureCount);
            const string updateHeaderSql = """
                UPDATE public.takas
                SET status = @status,
                    debt_amount = @debt_amount,
                    accept_notification_id = @notification_id,
                    accepted_at = CASE WHEN @successful_count > 0 THEN CURRENT_TIMESTAMP ELSE NULL END,
                    debt_applied_at = CASE WHEN @successful_count > 0 THEN CURRENT_TIMESTAMP ELSE NULL END,
                    next_status_check_at = NULL,
                    last_status_error = @last_error,
                    updated_at = CURRENT_TIMESTAMP
                WHERE record_id = @takas_id
                """;
            await using (var updateHeader = pharmacyApp.Infrastructure.DatabaseCommand.Create(updateHeaderSql, connection, transaction))
            {
                updateHeader.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
                updateHeader.Parameters.AddWithValue("debt_amount", NpgsqlDbType.Numeric, appliedDebt);
                updateHeader.Parameters.Add(new NpgsqlParameter("notification_id", NpgsqlDbType.Varchar)
                {
                    Value = result.NotificationId ?? (object)DBNull.Value
                });
                updateHeader.Parameters.AddWithValue("successful_count", NpgsqlDbType.Integer, successCount);
                updateHeader.Parameters.Add(new NpgsqlParameter("last_error", NpgsqlDbType.Text)
                {
                    Value = lastError ?? (object)DBNull.Value
                });
                updateHeader.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await updateHeader.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new TakasFinalizeResult(
                takasId, successCount, failureCount, appliedDebt, result.NotificationId, status);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task MarkReconciliationRequiredAsync(int takasId, string error, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE public.takas
            SET status = 'reconciliation_required',
                last_status_error = @error,
                next_status_check_at = CURRENT_TIMESTAMP,
                updated_at = CURRENT_TIMESTAMP
            WHERE record_id = @takas_id
              AND debt_applied_at IS NULL
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkNotificationNotSentAsync(int takasId, string error, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string itemSql = """
                UPDATE public."takasQrItems"
                SET status = 'transfer_failed',
                    last_error = @error,
                    status_changed_at = CURRENT_TIMESTAMP
                WHERE takas_id = @takas_id
                  AND status IN ('pending_transfer', 'reconciliation_required')
                """;
            await using (var items = pharmacyApp.Infrastructure.DatabaseCommand.Create(itemSql, connection, transaction))
            {
                items.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
                items.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await items.ExecuteNonQueryAsync(cancellationToken);
            }

            const string headerSql = """
                UPDATE public.takas
                SET status = 'failed',
                    last_status_error = @error,
                    next_status_check_at = NULL,
                    updated_at = CURRENT_TIMESTAMP
                WHERE record_id = @takas_id
                  AND status IN ('pending_transfer', 'reconciliation_required')
                  AND transfer_notification_id IS NULL
                  AND debt_applied_at IS NULL
                """;
            await using (var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection, transaction))
            {
                header.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
                header.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                if (await header.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Gönderilmemiş Takas kaydı güvenli biçimde başarısız duruma alınamadı.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task MarkAcceptanceNotSentAsync(int takasId, string error, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string itemSql = """
                UPDATE public."takasQrItems"
                SET status = 'transfer_failed',
                    last_error = @error,
                    status_changed_at = CURRENT_TIMESTAMP
                WHERE takas_id = @takas_id
                  AND status IN ('pending_transfer', 'reconciliation_required')
                """;
            await using (var items = pharmacyApp.Infrastructure.DatabaseCommand.Create(itemSql, connection, transaction))
            {
                items.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
                items.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await items.ExecuteNonQueryAsync(cancellationToken);
            }

            const string headerSql = """
                UPDATE public.takas
                SET status = 'failed',
                    last_status_error = @error,
                    next_status_check_at = NULL,
                    updated_at = CURRENT_TIMESTAMP
                WHERE record_id = @takas_id
                  AND direction = 'incoming'
                  AND status IN ('pending_transfer', 'reconciliation_required')
                  AND accept_notification_id IS NULL
                  AND debt_applied_at IS NULL
                """;
            await using (var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection, transaction))
            {
                header.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
                header.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                if (await header.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Gönderilmemiş Mal Alım kaydı güvenli biçimde başarısız duruma alınamadı.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TakasCancellationPreparation> LoadCancellationPreparationAsync(
        int takasId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string headerSql = """
            SELECT source_gln, destination_gln, status, direction
            FROM public.takas
            WHERE record_id = @takas_id
            """;
        string sourceGln;
        string destinationGln;
        await using (var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection))
        {
            header.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("İptal edilecek Takas kaydı bulunamadı.");
            sourceGln = reader.GetString(0);
            destinationGln = reader.GetString(1);
            if (!string.Equals(reader.GetString(2), "in_transit", StringComparison.Ordinal))
                throw new InvalidOperationException("Yalnızca Yolda durumundaki Takas iptal edilebilir.");
            if (!string.Equals(reader.GetString(3), "outgoing", StringComparison.Ordinal))
                throw new InvalidOperationException("Yalnızca bu eczaneden gönderilen Takas iptal edilebilir.");
        }

        const string itemSql = """
            SELECT item.gtin, item.serial_number, item.batch_number, item.expiry_date
            FROM public."takasQrItems" item
            JOIN public."qrHanged" hanged ON hanged.takas_item_id = item.record_id
            WHERE item.takas_id = @takas_id
              AND item.status = 'in_transit'
            ORDER BY item.record_id
            """;
        await using var items = pharmacyApp.Infrastructure.DatabaseCommand.Create(itemSql, connection);
        items.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        var products = new List<ItsQrProduct>();
        await using (var reader = await items.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                products.Add(new ItsQrProduct(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateOnly.FromDateTime(reader.GetDateTime(3))));
            }
        }
        if (products.Count == 0)
            throw new InvalidOperationException("Takas için Yolda durumunda bekleyen karekod bulunamadı.");
        await ValidateCancellationItemCountAsync(connection, null, takasId, products.Count, cancellationToken);

        return new TakasCancellationPreparation(takasId, sourceGln, destinationGln, products);
    }

    public async Task<TakasCancellationResult> ApplyCancellationResultAsync(
        int takasId,
        ItsNotificationResult result,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = """
                SELECT pharmacy_id, status, direction, debt_applied_at, cancelled_at
                FROM public.takas
                WHERE record_id = @takas_id
                FOR UPDATE
                """;
            int pharmacyId;
            await using (var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection, transaction))
            {
                header.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("İptal edilen Takas kaydı bulunamadı.");
                pharmacyId = reader.GetInt32(0);
                if (!string.Equals(reader.GetString(1), "in_transit", StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), "outgoing", StringComparison.Ordinal) ||
                    reader.IsDBNull(3) || !reader.IsDBNull(4))
                    throw new InvalidOperationException("Takas artık Mal Devir İptal sonucunun uygulanmasına uygun değil.");
            }

            var items = await LoadCancelableItemsAsync(connection, transaction, takasId, cancellationToken);
            await ValidateCancellationItemCountAsync(connection, transaction, takasId, items.Count, cancellationToken);
            var resultByProduct = result.Products
                .GroupBy(product => ProductKey(product.Gtin, product.SerialNumber))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var cancelledCount = 0;
            var failureCount = 0;
            var reversedDebt = 0m;
            var referenceCode = $"T{takasId}";

            foreach (var item in items)
            {
                resultByProduct.TryGetValue(ProductKey(item.Gtin, item.SerialNumber), out var productResult);
                var resultCode = productResult?.ResultCode ?? string.Empty;
                if (result.HttpStatusCode is >= 200 and <= 299 &&
                    string.IsNullOrWhiteSpace(result.FaultCode) &&
                    resultCode == "00000")
                {
                    await RestoreCancelledItemAsync(
                        connection, transaction, item, referenceCode, userId, cancellationToken);
                    await UpdateCancellationItemResultAsync(
                        connection, transaction, item.RecordId, "transfer_cancelled", resultCode, null, cancellationToken);
                    cancelledCount++;
                    reversedDebt += item.DebtAmount;
                }
                else
                {
                    var error = resultCode.Length == 0
                        ? (result.FaultMessage ?? $"HTTP {result.HttpStatusCode}")
                        : $"İTS sonuç kodu: {resultCode}";
                    await UpdateCancellationItemResultAsync(
                        connection, transaction, item.RecordId, "in_transit", resultCode, error, cancellationToken);
                    failureCount++;
                }
            }

            if (reversedDebt > 0)
                await DecreasePharmacyDebtAsync(connection, transaction, pharmacyId, reversedDebt, cancellationToken);

            var status = cancelledCount switch
            {
                0 => "in_transit",
                _ when failureCount == 0 => "cancelled",
                _ => "partially_cancelled"
            };
            var lastError = BuildResultError(result, failureCount);
            const string updateHeaderSql = """
                UPDATE public.takas
                SET status = @status,
                    cancel_notification_id = @notification_id,
                    cancelled_at = CASE WHEN @cancelled_count > 0 THEN CURRENT_TIMESTAMP ELSE cancelled_at END,
                    debt_reversed_at = CASE WHEN @cancelled_count > 0 THEN CURRENT_TIMESTAMP ELSE debt_reversed_at END,
                    next_status_check_at = CASE WHEN @status = 'cancelled' THEN NULL ELSE next_status_check_at END,
                    last_status_error = @last_error,
                    updated_at = CURRENT_TIMESTAMP
                WHERE record_id = @takas_id
                  AND status = 'in_transit'
                """;
            await using (var updateHeader = pharmacyApp.Infrastructure.DatabaseCommand.Create(updateHeaderSql, connection, transaction))
            {
                updateHeader.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
                updateHeader.Parameters.Add(new NpgsqlParameter("notification_id", NpgsqlDbType.Varchar)
                {
                    Value = result.NotificationId ?? (object)DBNull.Value
                });
                updateHeader.Parameters.AddWithValue("cancelled_count", NpgsqlDbType.Integer, cancelledCount);
                updateHeader.Parameters.Add(new NpgsqlParameter("last_error", NpgsqlDbType.Text)
                {
                    Value = lastError ?? (object)DBNull.Value
                });
                updateHeader.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                if (await updateHeader.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Takas iptal sonucu eşzamanlı bir değişiklik nedeniyle uygulanamadı.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new TakasCancellationResult(
                takasId, cancelledCount, failureCount, reversedDebt, result.NotificationId, status);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task MarkCancellationNotSentAsync(
        int takasId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE public.takas
            SET last_status_error = @error,
                updated_at = CURRENT_TIMESTAMP
            WHERE record_id = @takas_id
              AND status = 'in_transit'
              AND cancelled_at IS NULL
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Gönderilmeyen Mal Devir İptal denemesi Takas kaydına işlenemedi.");
    }

    public async Task MarkCancellationReconciliationRequiredAsync(
        int takasId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string itemSql = """
                UPDATE public."takasQrItems"
                SET status = 'reconciliation_required',
                    last_error = @error,
                    status_changed_at = CURRENT_TIMESTAMP
                WHERE takas_id = @takas_id
                  AND status = 'in_transit'
                """;
            await using (var items = pharmacyApp.Infrastructure.DatabaseCommand.Create(itemSql, connection, transaction))
            {
                items.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
                items.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                await items.ExecuteNonQueryAsync(cancellationToken);
            }

            const string headerSql = """
                UPDATE public.takas
                SET status = 'reconciliation_required',
                    last_status_error = @error,
                    next_status_check_at = CURRENT_TIMESTAMP,
                    updated_at = CURRENT_TIMESTAMP
                WHERE record_id = @takas_id
                  AND status = 'in_transit'
                  AND cancelled_at IS NULL
                """;
            await using (var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection, transaction))
            {
                header.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
                header.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
                if (await header.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Takas iptal mutabakat durumuna alınamadı.");
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ValidatePharmacyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int pharmacyId,
        string destinationGln,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT gln_code FROM public.pharmacy WHERE record_id = @id FOR UPDATE";
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, pharmacyId);
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (value is null) throw new InvalidOperationException("Seçilen eczane artık bulunmuyor.");
        if (!string.Equals(value, destinationGln, StringComparison.Ordinal))
            throw new InvalidOperationException("Seçilen eczanenin GLN kodu değişti. Eczaneyi yeniden seçin.");
    }

    private static async Task ValidateQrStockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TakasStockItem item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT barcode, status
            FROM public."qrActive"
            WHERE qr_code = @qr_code
            FOR UPDATE
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Karekod artık aktif stokta bulunmuyor: {item.QrCode}");
        if (!string.Equals(reader.GetString(0), item.Barcode, StringComparison.Ordinal))
            throw new InvalidOperationException($"Karekodun ürün bilgisi değişti: {item.QrCode}");
        if (!reader.IsDBNull(1) && !string.Equals(reader.GetString(1), "ACTIVE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Karekodun stok durumu Takas için uygun değil: {item.QrCode}");
    }

    private static async Task ValidateIncomingQrStockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TakasStockItem item,
        CancellationToken cancellationToken)
    {
        const string conflictSql = """
            SELECT location
            FROM (
                SELECT 'aktif stok' AS location FROM public."qrActive" WHERE qr_code = @qr_code
                UNION ALL
                SELECT 'bekleyen stok' FROM public."qrHanged" WHERE qr_code = @qr_code
                UNION ALL
                SELECT 'sonucu kesinleşmemiş bir Takas'
                FROM public."takasQrItems"
                WHERE qr_code = @qr_code
                  AND status IN ('pending_transfer', 'in_transit', 'reconciliation_required')
            ) conflicts
            LIMIT 1
            """;
        await using (var conflict = pharmacyApp.Infrastructure.DatabaseCommand.Create(conflictSql, connection, transaction))
        {
            conflict.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
            var location = Convert.ToString(await conflict.ExecuteScalarAsync(cancellationToken));
            if (!string.IsNullOrWhiteSpace(location))
                throw new InvalidOperationException($"Karekod artık {location} içinde bulunuyor: {item.QrCode}");
        }

        const string inactiveSql = """
            SELECT barcode
            FROM public."qrInactive"
            WHERE qr_code = @qr_code
            FOR UPDATE
            """;
        await using var inactive = pharmacyApp.Infrastructure.DatabaseCommand.Create(inactiveSql, connection, transaction);
        inactive.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
        var inactiveBarcode = Convert.ToString(await inactive.ExecuteScalarAsync(cancellationToken));
        if (!string.IsNullOrWhiteSpace(inactiveBarcode) &&
            !string.Equals(inactiveBarcode, item.Barcode, StringComparison.Ordinal))
            throw new InvalidOperationException($"Karekodun ürün bilgisi değişti: {item.QrCode}");
    }

    private static async Task<int> InsertTakasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int pharmacyId,
        string direction,
        string sourceGln,
        string destinationGln,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.takas (
                pharmacy_id, direction, source_gln, destination_gln,
                status, debt_amount, user_no, notes)
            VALUES (
                @pharmacy_id, @direction, @source_gln, @destination_gln,
                'pending_transfer', 0, @user_no, @notes)
            RETURNING record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("pharmacy_id", NpgsqlDbType.Integer, pharmacyId);
        command.Parameters.AddWithValue("direction", NpgsqlDbType.Varchar, direction);
        command.Parameters.AddWithValue("source_gln", NpgsqlDbType.Varchar, sourceGln);
        command.Parameters.AddWithValue("destination_gln", NpgsqlDbType.Varchar, destinationGln);
        command.Parameters.AddWithValue("user_no", NpgsqlDbType.Varchar, userId);
        command.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim()
        });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertTakasItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int takasId,
        TakasLineInput line,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."takasQrItems" (
                takas_id, qr_code, barcode, gtin, serial_number,
                batch_number, expiry_date, entry_cost, debt_amount, status)
            VALUES (
                @takas_id, @qr_code, @barcode, @gtin, @serial_number,
                @batch_number, @expiry_date, @entry_cost, @debt_amount, 'pending_transfer')
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, line.Item.QrCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, line.Item.Barcode);
        command.Parameters.AddWithValue("gtin", NpgsqlDbType.Varchar, line.Item.ItsProduct.Gtin);
        command.Parameters.AddWithValue("serial_number", NpgsqlDbType.Varchar, line.Item.ItsProduct.SerialNumber);
        command.Parameters.AddWithValue("batch_number", NpgsqlDbType.Varchar, line.Item.ItsProduct.BatchNumber);
        command.Parameters.AddWithValue("expiry_date", NpgsqlDbType.Date, line.Item.ItsProduct.ExpiryDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.Add(new NpgsqlParameter("entry_cost", NpgsqlDbType.Numeric)
        {
            Value = line.Item.EntryCost ?? (object)DBNull.Value
        });
        command.Parameters.AddWithValue("debt_amount", NpgsqlDbType.Numeric, line.DebtAmount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<PendingItem>> LoadPendingItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int takasId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item.record_id, item.qr_code, item.gtin, item.serial_number,
                   item.debt_amount, transfer.destination_gln
            FROM public."takasQrItems" item
            JOIN public.takas transfer ON transfer.record_id = item.takas_id
            WHERE item.takas_id = @takas_id
              AND item.status = 'pending_transfer'
            ORDER BY item.record_id
            FOR UPDATE OF item
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        var items = new List<PendingItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PendingItem(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4), reader.GetString(5)));
        if (items.Count == 0) throw new InvalidOperationException("Takas için bekleyen karekod bulunamadı.");
        return items;
    }

    private static async Task<List<PendingIncomingItem>> LoadPendingIncomingItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int takasId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item.record_id, item.qr_code, item.gtin, item.serial_number,
                   item.debt_amount, transfer.source_gln
            FROM public."takasQrItems" item
            JOIN public.takas transfer ON transfer.record_id = item.takas_id
            WHERE item.takas_id = @takas_id
              AND transfer.direction = 'incoming'
              AND item.status = 'pending_transfer'
            ORDER BY item.record_id
            FOR UPDATE OF item
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        var items = new List<PendingIncomingItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PendingIncomingItem(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4), reader.GetString(5)));
        if (items.Count == 0) throw new InvalidOperationException("Mal Alım için bekleyen karekod bulunamadı.");
        return items;
    }

    private static async Task<List<CancelableItem>> LoadCancelableItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int takasId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item.record_id, item.qr_code, item.gtin, item.serial_number,
                   item.debt_amount, transfer.destination_gln
            FROM public."takasQrItems" item
            JOIN public.takas transfer ON transfer.record_id = item.takas_id
            JOIN public."qrHanged" hanged ON hanged.takas_item_id = item.record_id
            WHERE item.takas_id = @takas_id
              AND item.status = 'in_transit'
            ORDER BY item.record_id
            FOR UPDATE OF item, hanged
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        var items = new List<CancelableItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CancelableItem(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4), reader.GetString(5)));
        if (items.Count == 0) throw new InvalidOperationException("Takas için iptal edilebilir karekod bulunamadı.");
        return items;
    }

    private static async Task RestoreCancelledItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancelableItem item,
        string referenceCode,
        string userId,
        CancellationToken cancellationToken)
    {
        const string restoreSql = """
            WITH restored AS (
                INSERT INTO public."qrActive" (
                    qr_code, barcode, expiry_date, status, purchase_cost, reference_no)
                SELECT hanged.qr_code, hanged.barcode, hanged.expiry_date,
                       NULL, hanged.entry_cost, @reference_code
                FROM public."qrHanged" hanged
                WHERE hanged.takas_item_id = @item_id
                  AND hanged.qr_code = @qr_code
                RETURNING barcode
            ), removed AS (
                DELETE FROM public."qrHanged"
                WHERE takas_item_id = @item_id
                  AND qr_code = @qr_code
                RETURNING barcode
            )
            SELECT barcode FROM restored
            """;
        string? barcode;
        await using (var restore = pharmacyApp.Infrastructure.DatabaseCommand.Create(restoreSql, connection, transaction))
        {
            restore.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
            restore.Parameters.AddWithValue("item_id", NpgsqlDbType.Integer, item.RecordId);
            restore.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
            barcode = Convert.ToString(await restore.ExecuteScalarAsync(cancellationToken));
        }
        if (string.IsNullOrWhiteSpace(barcode))
            throw new InvalidOperationException($"İptal edilen karekod bekleyen stokta bulunamadı: {item.QrCode}");

        await using (var refresh = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "SELECT public.pharmacy_refresh_barcode_active(@barcode)", connection, transaction))
        {
            refresh.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, barcode);
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }

        const string movementSql = """
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            VALUES (
                @qr_code,
                (SELECT record_id FROM public."qrMovementTypes" WHERE code = 'TRANSF_CANCEL'),
                CURRENT_TIMESTAMP, @destination_gln, @user_id, @reference_code)
            """;
        await using var movement = pharmacyApp.Infrastructure.DatabaseCommand.Create(movementSql, connection, transaction);
        movement.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
        movement.Parameters.AddWithValue("destination_gln", NpgsqlDbType.Varchar, item.DestinationGln);
        movement.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        movement.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        await movement.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateCancellationItemCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int takasId,
        int hangedInTransitCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)::integer,
                   COUNT(*) FILTER (WHERE status = 'in_transit')::integer
            FROM public."takasQrItems"
            WHERE takas_id = @takas_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("takas_id", NpgsqlDbType.Integer, takasId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt32(0) != hangedInTransitCount ||
            reader.GetInt32(1) != hangedInTransitCount)
            throw new InvalidOperationException("Takas karekodlarının bekleyen stok durumu tutarsız; İTS iptal bildirimi gönderilmedi.");
    }

    private static async Task UpdateCancellationItemResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int itemId,
        string status,
        string resultCode,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public."takasQrItems"
            SET status = @status,
                cancel_result_code = NULLIF(@result_code, ''),
                last_error = @error,
                status_changed_at = CURRENT_TIMESTAMP
            WHERE record_id = @item_id
              AND status = 'in_transit'
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
        command.Parameters.AddWithValue("result_code", NpgsqlDbType.Varchar, resultCode);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text)
        {
            Value = error ?? (object)DBNull.Value
        });
        command.Parameters.AddWithValue("item_id", NpgsqlDbType.Integer, itemId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Takas karekodunun iptal sonucu uygulanamadı.");
    }

    private static async Task InsertMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qrCode,
        string referenceCode,
        string destinationGln,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            VALUES (
                @qr_code,
                (SELECT record_id FROM public."qrMovementTypes" WHERE code = 'TRANSF_OUT'),
                CURRENT_TIMESTAMP, @destination_gln, @user_id, @reference_code)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        command.Parameters.AddWithValue("destination_gln", NpgsqlDbType.Varchar, destinationGln);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertIncomingMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qrCode,
        string referenceCode,
        string sourceGln,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            VALUES (
                @qr_code,
                (SELECT record_id FROM public."qrMovementTypes" WHERE code = 'TRANSF_IN'),
                CURRENT_TIMESTAMP, @source_gln, @user_id, @reference_code)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        command.Parameters.AddWithValue("source_gln", NpgsqlDbType.Varchar, sourceGln);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateItemResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int itemId,
        string status,
        string resultCode,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public."takasQrItems"
            SET status = @status,
                transfer_result_code = NULLIF(@result_code, ''),
                last_error = @error,
                status_changed_at = CURRENT_TIMESTAMP
            WHERE record_id = @item_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
        command.Parameters.AddWithValue("result_code", NpgsqlDbType.Varchar, resultCode);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text)
        {
            Value = error ?? (object)DBNull.Value
        });
        command.Parameters.AddWithValue("item_id", NpgsqlDbType.Integer, itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAcceptanceItemResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int itemId,
        string status,
        string resultCode,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public."takasQrItems"
            SET status = @status,
                accept_result_code = NULLIF(@result_code, ''),
                last_error = @error,
                status_changed_at = CURRENT_TIMESTAMP
            WHERE record_id = @item_id
              AND status = 'pending_transfer'
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
        command.Parameters.AddWithValue("result_code", NpgsqlDbType.Varchar, resultCode);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text)
        {
            Value = error ?? (object)DBNull.Value
        });
        command.Parameters.AddWithValue("item_id", NpgsqlDbType.Integer, itemId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Mal Alım karekod sonucu uygulanamadı.");
    }

    private static async Task IncreasePharmacyDebtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int pharmacyId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.pharmacy
            SET debt_balance = debt_balance + @amount
            WHERE record_id = @pharmacy_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
        command.Parameters.AddWithValue("pharmacy_id", NpgsqlDbType.Integer, pharmacyId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Takas borcu için seçilen eczane bulunamadı.");
    }

    private static async Task DecreasePharmacyDebtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int pharmacyId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.pharmacy
            SET debt_balance = debt_balance - @amount
            WHERE record_id = @pharmacy_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
        command.Parameters.AddWithValue("pharmacy_id", NpgsqlDbType.Integer, pharmacyId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Takas borcu azaltılacak eczane bulunamadı.");
    }

    private static string? BuildResultError(ItsNotificationResult result, int failureCount)
    {
        if (!string.IsNullOrWhiteSpace(result.FaultMessage))
            return $"{result.FaultCode} {result.FaultMessage}".Trim();
        return failureCount > 0 ? $"{failureCount} karekod İTS tarafından kabul edilmedi." : null;
    }

    private static string ProductKey(string gtin, string serialNumber) => $"{gtin}\u001F{serialNumber}";

    private sealed record PendingItem(
        int RecordId,
        string QrCode,
        string Gtin,
        string SerialNumber,
        decimal DebtAmount,
        string DestinationGln);

    private sealed record PendingIncomingItem(
        int RecordId,
        string QrCode,
        string Gtin,
        string SerialNumber,
        decimal DebtAmount,
        string SourceGln);

    private sealed record CancelableItem(
        int RecordId,
        string QrCode,
        string Gtin,
        string SerialNumber,
        decimal DebtAmount,
        string DestinationGln);
}
