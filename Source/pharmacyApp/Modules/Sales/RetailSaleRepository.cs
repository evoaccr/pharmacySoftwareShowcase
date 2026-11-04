using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.Modules.Sales;

public sealed class RetailSaleRepository
{
    public async Task<RetailSaleItem?> ResolveItemAsync(string enteredCode, CancellationToken cancellationToken)
    {
        var code = enteredCode.Trim();
        if (code.Length == 0) return null;
        var normalizedCode = RetailQrCodeNormalizer.Normalize(code);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH stock_item AS (
                SELECT active.qr_code AS entered_code,
                       active.barcode,
                       FALSE AS is_barcode_only,
                       active.status,
                       aggregate.quantity,
                       active.expiry_date,
                       active.purchase_cost AS entry_cost
                FROM public."qrActive" active
                LEFT JOIN public."barcodeActive" aggregate ON aggregate.barcode = active.barcode
                WHERE active.qr_code = @code
                   OR regexp_replace(
                        regexp_replace(active.qr_code, E'^\\]d2', ''),
                        E'[()\\x1D]', '', 'g') = @normalized_code

                UNION ALL

                SELECT product.barcode,
                       product.barcode,
                       TRUE,
                       COALESCE(aggregate.status, 'ACTIVE'),
                       aggregate.quantity,
                       NULL::date,
                       NULL::numeric
                FROM public.barcode product
                JOIN public."barcodeActive" aggregate ON aggregate.barcode = product.barcode
                WHERE product.barcode = @code
                  AND aggregate.quantity > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM public."qrActive" serialized
                      WHERE serialized.barcode = product.barcode)
                LIMIT 1
            )
            SELECT stock.entered_code,
                   stock.barcode,
                   stock.is_barcode_only,
                   COALESCE(stock.status, 'ACTIVE'),
                   product.record_id,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), product.barcode),
                   COALESCE(price.sale_price, price.public_price, price.public_paid_price, 0),
                   price.warehouse_price,
                   COALESCE(stock.quantity, 0),
                   stock.expiry_date,
                   stock.entry_cost,
                   (product.photo)[1],
                   price.public_price,
                   product.prescription_type_name,
                   product.indication_summary
            FROM stock_item stock
            LEFT JOIN public.barcode product ON product.barcode = stock.barcode
            LEFT JOIN public."barcodePrices" price ON price.record_reference_no = product.record_id
            ORDER BY CASE WHEN stock.entered_code = @code THEN 0 ELSE 1 END, stock.is_barcode_only
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("normalized_code", NpgsqlDbType.Varchar, normalizedCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (reader.IsDBNull(4))
            throw new InvalidOperationException(
                $"Karekod aktif stokta bulunuyor ancak {reader.GetString(1)} barkodu ürün kataloğunda yok. " +
                "Ürün ve fiyat bilgileri tamamlanmadan satışa eklenemez.");

        return new RetailSaleItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetDecimal(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11),
            reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    public async Task<IReadOnlyList<PatientSuggestion>> SearchPatientsAsync(
        string search,
        CancellationToken cancellationToken,
        int limit = 20)
    {
        var term = RetailTurkishSearch.Normalize(search);
        if (term.Length < 3) return [];
        limit = Math.Clamp(limit, 1, 200);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT record_id,
                   COALESCE(tc_identity_no, ''),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''),
                            NULLIF(tc_identity_no, ''), record_id::text),
                   COALESCE(phone_numbers[1], ''),
                   COALESCE(debt_amount, 0),
                   COALESCE(debt_credit_score, 0),
                   photo
            FROM public.patient
            WHERE LOWER(TRANSLATE(COALESCE(tc_identity_no, ''), 'İIŞĞÜÖÇ', 'iışğüöç')) LIKE @prefix
               OR LOWER(TRANSLATE(CONCAT_WS(' ', first_name, last_name), 'İIŞĞÜÖÇ', 'iışğüöç')) LIKE @contains
               OR LOWER(TRANSLATE(CONCAT_WS(' ', last_name, first_name), 'İIŞĞÜÖÇ', 'iışğüöç')) LIKE @contains
            ORDER BY
                CASE WHEN LOWER(TRANSLATE(COALESCE(tc_identity_no, ''), 'İIŞĞÜÖÇ', 'iışğüöç')) LIKE @prefix THEN 0 ELSE 1 END,
                first_name NULLS LAST,
                last_name NULLS LAST
            LIMIT @limit
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Varchar, term + "%");
        command.Parameters.AddWithValue("contains", NpgsqlDbType.Varchar, "%" + term + "%");
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        var suggestions = new List<PatientSuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            suggestions.Add(ReadPatient(reader));
        return suggestions;
    }

    public async Task<PatientSuggestion?> LoadPatientAsync(int recordId, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT record_id,
                   COALESCE(tc_identity_no, ''),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''),
                            NULLIF(tc_identity_no, ''), record_id::text),
                   COALESCE(phone_numbers[1], ''),
                   COALESCE(debt_amount, 0),
                   COALESCE(debt_credit_score, 0),
                   photo
            FROM public.patient
            WHERE record_id = @record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPatient(reader)
            : null;
    }

    public async Task<PatientSuggestion?> LoadPatientByTcAsync(
        string tcIdentityNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT record_id,
                   COALESCE(tc_identity_no, ''),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''),
                            NULLIF(tc_identity_no, ''), record_id::text),
                   COALESCE(phone_numbers[1], ''),
                   COALESCE(debt_amount, 0),
                   COALESCE(debt_credit_score, 0),
                   photo
            FROM public.patient
            WHERE tc_identity_no = @tc_identity_no
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("tc_identity_no", NpgsqlDbType.Varchar, tcIdentityNumber.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPatient(reader)
            : null;
    }

    public async Task<int> CompleteSaleAsync(
        IReadOnlyList<RetailSaleLineInput> lines,
        int? patientId,
        IReadOnlyList<RetailSalePaymentInput> payments,
        bool withDiscount,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
        => await CompleteSaleAsync(lines, patientId, payments, withDiscount, notes, userId, true, cancellationToken);

    public async Task<int> CompleteSaleAsync(
        IReadOnlyList<RetailSaleLineInput> lines,
        int? patientId,
        IReadOnlyList<RetailSalePaymentInput> payments,
        bool withDiscount,
        string? notes,
        string userId,
        bool itsNotificationEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(payments);
        if (lines.Count == 0) throw new InvalidOperationException("Satış listesi boş.");

        var totalAmount = decimal.Round(lines.Sum(line => line.Item.UnitPrice), 2, MidpointRounding.AwayFromZero);
        var debtAmount = RetailSaleRules.ValidatePayments(totalAmount, payments, withDiscount, patientId);
        var discountAmount = payments.Where(payment => payment.Kind == RetailPaymentKind.Discount).Sum(payment => payment.Amount);
        RetailSaleRules.ValidateLineDiscounts(lines, discountAmount, withDiscount);
        userId = string.IsNullOrWhiteSpace(userId) ? "admin" : userId.Trim();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ManualStockSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var serializedQrCodes = lines.Select(line => line.Item).Where(item => !item.IsBarcodeOnly)
                .Select(item => item.EnteredCode).ToArray();
            await UnnotifiedItsMovementRepository.ValidateBeforeActionAsync(
                connection, transaction, serializedQrCodes, StockMovementDirection.Out,
                itsNotificationEnabled, cancellationToken);
            foreach (var barcodeGroup in lines.Select(line => line.Item).Where(item => item.IsBarcodeOnly).GroupBy(item => item.Barcode))
                await ValidateBarcodeStockAsync(connection, transaction, barcodeGroup.Key, barcodeGroup.Count(), cancellationToken);

            foreach (var item in lines.Select(line => line.Item).Where(item => !item.IsBarcodeOnly))
                await ValidateQrStockAsync(connection, transaction, item, cancellationToken);

            var saleId = await InsertSaleAsync(
                connection, transaction, patientId, totalAmount, withDiscount, debtAmount > 0, notes, userId, cancellationToken);
            await DatabaseAuditContext.SetReferenceAsync(connection, transaction, $"P{saleId}", userId, cancellationToken);
            foreach (var payment in payments.Where(payment => payment.Amount > 0))
                await InsertPaymentAsync(connection, transaction, saleId, payment, cancellationToken);
            if (debtAmount > 0)
                await IncreasePatientDebtAsync(connection, transaction, patientId!.Value, debtAmount, cancellationToken);

            var referenceCode = $"P{saleId}";
            var movementAddress = RetailSaleMovementAddress.Format(patientId);
            foreach (var line in lines)
            {
                var item = line.Item;
                await InsertSaleItemAsync(connection, transaction, saleId, line, cancellationToken);
                await InsertMovementAsync(
                    connection, transaction, item.EnteredCode, referenceCode, movementAddress, userId,
                    item.IsBarcodeOnly ? null : line.ExitPrice, cancellationToken);
            }

            if (!itsNotificationEnabled)
                foreach (var qrCode in serializedQrCodes)
                    await UnnotifiedItsMovementRepository.RecordSkippedNotificationAsync(
                        connection, transaction, qrCode, referenceCode, StockMovementDirection.Out,
                        StockItsNotificationKind.Sale, userId, cancellationToken);

            await using (var paymentCheck = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "SELECT payement_finished FROM public.sales WHERE record_id = @sale_id",
                connection,
                transaction))
            {
                paymentCheck.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
                if (await paymentCheck.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new InvalidOperationException("Satış ödeme toplamı doğrulanamadı.");
            }

            await transaction.CommitAsync(cancellationToken);
            return saleId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ValidateBarcodeStockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string barcode,
        int requestedQuantity,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT quantity, status
            FROM public."barcodeActive"
            WHERE barcode = @barcode
            FOR UPDATE
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, barcode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) < requestedQuantity)
            throw new InvalidOperationException($"{barcode} barkodlu ürün için yeterli stok bulunmuyor.");
        if (!reader.IsDBNull(1) && IsConsigned(reader.GetString(1)))
            throw new InvalidOperationException("Emanetteki ürün satılamaz.");
    }

    private static async Task ValidateQrStockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RetailSaleItem item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT barcode, status
            FROM public."qrActive"
            WHERE qr_code = @qr_code
            FOR UPDATE
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.EnteredCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Karekod artık aktif stokta bulunmuyor: {item.EnteredCode}");
        if (!string.Equals(reader.GetString(0), item.Barcode, StringComparison.Ordinal))
            throw new InvalidOperationException($"Karekodun ürün bilgisi değişti: {item.EnteredCode}");
        if (!reader.IsDBNull(1) && IsConsigned(reader.GetString(1)))
            throw new InvalidOperationException("Emanetteki ürün satılamaz.");
    }

    private static async Task<int> InsertSaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int? patientId,
        decimal totalAmount,
        bool withDiscount,
        bool debted,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.sales (
                patient_id, institution_amount, total_amount,
                payement_finished, institution_payement_finished,
                debted, user_no, "timestamp", with_iskonto, notes)
            VALUES (
                @patient_id, 0, @total_amount,
                FALSE, FALSE,
                @debted, @user_id, CURRENT_TIMESTAMP, @with_iskonto, @notes)
            RETURNING record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Integer)
        {
            Value = patientId is null ? DBNull.Value : patientId.Value
        });
        command.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, totalAmount);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("with_iskonto", NpgsqlDbType.Boolean, withDiscount);
        command.Parameters.AddWithValue("debted", NpgsqlDbType.Boolean, debted);
        command.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Varchar)
        {
            Value = string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim()
        });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task IncreasePatientDebtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int patientId,
        decimal debtAmount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.patient
            SET debt_amount = COALESCE(debt_amount, 0) + @debt_amount
            WHERE record_id = @patient_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("debt_amount", NpgsqlDbType.Numeric, debtAmount);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Veresiye borcu için seçilen hasta bulunamadı.");
    }

    private static async Task InsertPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        RetailSalePaymentInput payment,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."salesPayements" (sale_id, payment_type, amount)
            VALUES (@sale_id, @payment_type, @amount)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("payment_type", NpgsqlDbType.Smallint, (short)payment.Kind);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, payment.Amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSaleItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        RetailSaleLineInput line,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."salesQrItems" (
                sale_id, qr_code, barcode, gross_price, discount_amount, exit_price, entry_cost)
            VALUES (
                @sale_id, @qr_code, @barcode, @gross_price, @discount_amount, @exit_price, @entry_cost)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, line.Item.EnteredCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, line.Item.Barcode);
        command.Parameters.AddWithValue("gross_price", NpgsqlDbType.Numeric, line.Item.UnitPrice);
        command.Parameters.AddWithValue("discount_amount", NpgsqlDbType.Numeric, line.DiscountAmount);
        command.Parameters.AddWithValue("exit_price", NpgsqlDbType.Numeric, line.ExitPrice);
        command.Parameters.Add(new NpgsqlParameter("entry_cost", NpgsqlDbType.Numeric)
        {
            Value = line.Item.EntryCost ?? (object)DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string code,
        string referenceCode,
        string movementAddress,
        string userId,
        decimal? exitPrice,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            VALUES (
                @code,
                (SELECT record_id FROM public."qrMovementTypes" WHERE code = 'PERAK_OUT'),
                CURRENT_TIMESTAMP, @movement_address, @user_id, @reference_code)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        command.Parameters.AddWithValue("movement_address", NpgsqlDbType.Varchar, movementAddress);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (exitPrice is null) return;
        await using var priceCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "UPDATE public.\"qrInactive\" SET exit_price = @exit_price WHERE qr_code = @code",
            connection,
            transaction);
        priceCommand.Parameters.AddWithValue("exit_price", NpgsqlDbType.Numeric, exitPrice.Value);
        priceCommand.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        await priceCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public static bool IsConsigned(string? status) =>
        RetailStockStatus.IsConsigned(status);

    private static PatientSuggestion ReadPatient(NpgsqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetDecimal(4),
        reader.GetDecimal(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6));
}
