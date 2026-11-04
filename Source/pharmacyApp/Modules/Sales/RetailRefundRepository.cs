using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.Modules.Sales;

public sealed class RetailRefundRepository
{
    public async Task<RetailRefundItem?> ResolveItemAsync(string enteredCode, CancellationToken cancellationToken)
    {
        var code = enteredCode.Trim();
        if (code.Length == 0) return null;
        var normalizedCode = RetailQrCodeNormalizer.Normalize(code);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH latest_movement AS (
                SELECT movement.qr_code,
                       movement.reference_code,
                       movement.movement_date,
                       movement.record_id,
                       type.code AS movement_code
                FROM public."qrMovements" movement
                JOIN public."qrMovementTypes" type ON type.record_id = movement.movement_type
                WHERE (movement.qr_code = @code OR regexp_replace(
                        regexp_replace(movement.qr_code, E'^\\]d2', ''),
                        E'[()\\x1D]', '', 'g') = @normalized_code)
                ORDER BY movement.movement_date DESC, movement.record_id DESC
                LIMIT 1
            )
            SELECT inactive.qr_code,
                   inactive.barcode,
                   SUBSTRING(sold.reference_code FROM 2)::integer AS sale_id,
                   sale.patient_id,
                   COALESCE(patient.tc_identity_no, ''),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                            NULLIF(patient.tc_identity_no, ''), sale.patient_id::text),
                   product.record_id,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), product.barcode),
                   COALESCE(sold_item.gross_price, inactive.exit_price, price.sale_price, price.public_price, price.public_paid_price, 0),
                   COALESCE(sold_item.discount_amount, 0),
                   COALESCE(sold_item.exit_price, inactive.exit_price, price.sale_price, price.public_price, price.public_paid_price, 0),
                   COALESCE(sold_item.entry_cost, inactive.entry_cost),
                   price.warehouse_price,
                   price.public_price,
                   COALESCE(stock.quantity, 0),
                   product.prescription_type_name,
                   product.indication_summary,
                   (product.photo)[1]
             FROM latest_movement sold
             JOIN public."qrInactive" inactive ON inactive.qr_code = sold.qr_code
            JOIN public.sales sale ON sale.record_id = CASE
                WHEN sold.reference_code ~ '^P[0-9]+$' THEN SUBSTRING(sold.reference_code FROM 2)::integer
                ELSE NULL
             END
             LEFT JOIN public."salesQrItems" sold_item
               ON sold_item.sale_id = sale.record_id
              AND sold_item.qr_code = inactive.qr_code
            LEFT JOIN public.patient patient ON patient.record_id = sale.patient_id
            JOIN public.barcode product ON product.barcode = inactive.barcode
            LEFT JOIN public."barcodePrices" price ON price.record_reference_no = product.record_id
            LEFT JOIN public."barcodeActive" stock ON stock.barcode = inactive.barcode
            WHERE sold.movement_code = 'PERAK_OUT'
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("normalized_code", NpgsqlDbType.Varchar, normalizedCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var patient = reader.IsDBNull(3)
            ? null
            : new PatientSuggestion(reader.GetInt32(3), reader.GetString(4), reader.GetString(5));
        return new RetailRefundItem(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2), patient,
            reader.GetInt32(6), reader.GetString(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10),
            reader.IsDBNull(11) ? null : reader.GetDecimal(11),
            reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            reader.IsDBNull(13) ? null : reader.GetDecimal(13),
            reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetFieldValue<byte[]>(17));
    }

    public async Task<IReadOnlyList<string>> GetSaleQrCodesAsync(int saleId, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT movement.qr_code
            FROM public."qrMovements" movement
            JOIN public."qrMovementTypes" type ON type.record_id = movement.movement_type
            WHERE movement.reference_code = @reference_code
              AND type.code = 'PERAK_OUT'
            ORDER BY movement.record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, $"P{saleId}");
        var codes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) codes.Add(reader.GetString(0));
        return codes;
    }

    public async Task<int> CompleteRefundAsync(
        IReadOnlyList<RetailRefundItem> items,
        int? patientId,
        IReadOnlyList<RetailSalePaymentInput> payments,
        decimal debtReduction,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
        => await CompleteRefundAsync(items, patientId, payments, debtReduction, notes, userId, true, cancellationToken);

    public async Task<int> CompleteRefundAsync(
        IReadOnlyList<RetailRefundItem> items,
        int? patientId,
        IReadOnlyList<RetailSalePaymentInput> payments,
        decimal debtReduction,
        string? notes,
        string userId,
        bool itsNotificationEnabled,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0) throw new InvalidOperationException("İade listesi boş.");
        if (patientId is null) throw new InvalidOperationException("İade için hasta seçilmelidir.");
        var totalAmount = decimal.Round(items.Sum(item => item.ExitPrice), 2, MidpointRounding.AwayFromZero);
        debtReduction = decimal.Round(debtReduction, 2, MidpointRounding.AwayFromZero);
        if (debtReduction < 0m || debtReduction > totalAmount)
            throw new InvalidOperationException("Silinecek borç iade toplamını aşamaz.");
        if (debtReduction > 0m && patientId is null)
            throw new InvalidOperationException("Borç silmek için hasta seçilmelidir.");
        var paymentTotal = totalAmount - debtReduction;
        RetailRefundRules.ValidatePayments(paymentTotal, payments);
        userId = string.IsNullOrWhiteSpace(userId) ? "admin" : userId.Trim();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ManualStockSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var qrCodes = items.Select(item => item.QrCode).ToArray();
            await UnnotifiedItsMovementRepository.ValidateBeforeActionAsync(
                connection, transaction, qrCodes, StockMovementDirection.In,
                itsNotificationEnabled, cancellationToken);
            foreach (var item in items)
                await ValidateRefundItemAsync(connection, transaction, item, cancellationToken);

            var discountAmount = decimal.Round(items.Sum(item => item.DiscountAmount), 2, MidpointRounding.AwayFromZero);
            var refundId = await InsertRefundAsync(
                connection, transaction, patientId, totalAmount, discountAmount, debtReduction, paymentTotal == 0m,
                notes, userId, cancellationToken);
            await DatabaseAuditContext.SetReferenceAsync(connection, transaction, $"G{refundId}", userId, cancellationToken);
            foreach (var item in items)
                await InsertRefundItemAsync(connection, transaction, refundId, item, cancellationToken);
            foreach (var payment in payments.Where(payment => payment.Amount > 0))
                await InsertPaymentAsync(connection, transaction, refundId, payment, cancellationToken);
            if (debtReduction > 0m)
                await ReducePatientDebtAsync(connection, transaction, patientId!.Value, debtReduction, cancellationToken);

            var referenceCode = $"G{refundId}";
            var movementAddress = RetailSaleMovementAddress.Format(patientId);
            foreach (var item in items)
                await InsertReturnMovementAsync(connection, transaction, item.QrCode, referenceCode, movementAddress, userId, cancellationToken);

            if (!itsNotificationEnabled)
                foreach (var qrCode in qrCodes)
                    await UnnotifiedItsMovementRepository.RecordSkippedNotificationAsync(
                        connection, transaction, qrCode, referenceCode, StockMovementDirection.In,
                        StockItsNotificationKind.SaleCancellation, userId, cancellationToken);

            await using (var refresh = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "SELECT public.pharmacy_refresh_refund_payment_status(@refund_id)", connection, transaction))
            {
                refresh.Parameters.AddWithValue("refund_id", NpgsqlDbType.Integer, refundId);
                await refresh.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var check = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "SELECT payement_finished FROM public.\"salesRefunds\" WHERE record_id = @refund_id",
                connection,
                transaction);
            check.Parameters.AddWithValue("refund_id", NpgsqlDbType.Integer, refundId);
            if (await check.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("İade ödeme toplamı doğrulanamadı.");

            await transaction.CommitAsync(cancellationToken);
            return refundId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ValidateRefundItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RetailRefundItem item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(inactive.exit_price, @expected_price)
            FROM public."qrInactive" inactive
            WHERE inactive.qr_code = @qr_code
              AND (
                  SELECT type.code
                  FROM public."qrMovements" movement
                  JOIN public."qrMovementTypes" type ON type.record_id = movement.movement_type
                  WHERE movement.qr_code = inactive.qr_code
                  ORDER BY movement.movement_date DESC, movement.record_id DESC
                  LIMIT 1) = 'PERAK_OUT'
            FOR UPDATE
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("expected_price", NpgsqlDbType.Numeric, item.ExitPrice);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
            throw new InvalidOperationException($"Karekod artık iade edilebilir durumda değil: {item.QrCode}");
        if (decimal.Round(Convert.ToDecimal(result), 2) != decimal.Round(item.ExitPrice, 2))
            throw new InvalidOperationException($"Karekodun çıkış fiyatı değişti: {item.QrCode}");
    }

    private static async Task<int> InsertRefundAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int? patientId,
        decimal totalAmount,
        decimal discountAmount,
        decimal debtReduction,
        bool paymentFinished,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."salesRefunds" (
                patient_id, total_amount, discount_amount, debt_reduction_amount,
                payement_finished, user_no, "timestamp", notes)
            VALUES (
                @patient_id, @total_amount, @discount_amount, @debt_reduction,
                @payment_finished, @user_id, CURRENT_TIMESTAMP, @notes)
            RETURNING record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Integer) { Value = patientId ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, totalAmount);
        command.Parameters.AddWithValue("discount_amount", NpgsqlDbType.Numeric, discountAmount);
        command.Parameters.AddWithValue("debt_reduction", NpgsqlDbType.Numeric, debtReduction);
        command.Parameters.AddWithValue("payment_finished", NpgsqlDbType.Boolean, paymentFinished);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Varchar)
        {
            Value = string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim()
        });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertRefundItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int refundId,
        RetailRefundItem item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."salesRefundItems" (
                refund_id, original_sale_id, qr_code, barcode, gross_price,
                discount_amount, exit_price, entry_cost)
            VALUES (
                @refund_id, @original_sale_id, @qr_code, @barcode, @gross_price,
                @discount_amount, @exit_price, @entry_cost)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("refund_id", NpgsqlDbType.Integer, refundId);
        command.Parameters.AddWithValue("original_sale_id", NpgsqlDbType.Integer, item.OriginalSaleId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, item.Barcode);
        command.Parameters.AddWithValue("gross_price", NpgsqlDbType.Numeric, item.GrossPrice);
        command.Parameters.AddWithValue("discount_amount", NpgsqlDbType.Numeric, item.DiscountAmount);
        command.Parameters.AddWithValue("exit_price", NpgsqlDbType.Numeric, item.ExitPrice);
        command.Parameters.Add(new NpgsqlParameter("entry_cost", NpgsqlDbType.Numeric)
        {
            Value = item.EntryCost ?? (object)DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReducePatientDebtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int patientId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.patient
            SET debt_amount = COALESCE(debt_amount, 0) - @amount
            WHERE record_id = @patient_id
              AND COALESCE(debt_amount, 0) >= @amount
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Hastanın güncel borcu silinecek tutar için yetersiz.");
    }

    private static async Task InsertPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int refundId,
        RetailSalePaymentInput payment,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."salesRefundPayements" (refund_id, payment_type, amount)
            VALUES (@refund_id, @payment_type, @amount)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("refund_id", NpgsqlDbType.Integer, refundId);
        command.Parameters.AddWithValue("payment_type", NpgsqlDbType.Smallint, (short)payment.Kind);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, payment.Amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReturnMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qrCode,
        string referenceCode,
        string movementAddress,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            VALUES (
                @qr_code,
                (SELECT record_id FROM public."qrMovementTypes" WHERE code = 'PERAK_RETURN'),
                CURRENT_TIMESTAMP, @movement_address, @user_id, @reference_code)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        command.Parameters.AddWithValue("movement_address", NpgsqlDbType.Varchar, movementAddress);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
