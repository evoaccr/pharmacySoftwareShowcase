using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Sales;

public sealed record ConsignmentSaleSnapshot(
    int RecordId,
    PatientSuggestion Patient,
    IReadOnlyList<RetailSaleLineInput> Lines,
    IReadOnlyDictionary<RetailPaymentKind, decimal> Payments,
    string Status,
    string? Notes);

public sealed record ConsignmentReturnResult(decimal RefundedAmount, decimal ReversedDebtAmount);

public sealed class ConsignmentSaleRepository
{
    public async Task<int> CreateAsync(
        IReadOnlyList<RetailSaleLineInput> lines,
        int patientId,
        IReadOnlyList<RetailSalePaymentInput> payments,
        bool withDiscount,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
    {
        ValidateSerializedLines(lines);
        var total = Round(lines.Sum(line => line.Item.UnitPrice));
        var debt = RetailSaleRules.ValidatePayments(total, payments, withDiscount, patientId);
        var discount = Round(payments.Where(payment => payment.Kind == RetailPaymentKind.Discount).Sum(payment => payment.Amount));
        RetailSaleRules.ValidateLineDiscounts(lines, discount, withDiscount);
        var paid = Round(payments.Where(payment => payment.Kind is RetailPaymentKind.Cash or RetailPaymentKind.Credit or RetailPaymentKind.BankTransfer)
            .Sum(payment => payment.Amount));
        userId = NormalizeUser(userId);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await EnsurePatientExistsAsync(connection, transaction, patientId, cancellationToken);
            foreach (var line in lines)
                await ValidateAvailableQrAsync(connection, transaction, line.Item, cancellationToken);

            const string headerSql = """
                INSERT INTO public."consignmentSales" (
                    patient_id, total_amount, paid_amount, debt_amount, discount_amount,
                    with_iskonto, status, user_no, "timestamp", notes)
                VALUES (
                    @patient_id, @total, @paid, @debt, @discount,
                    @with_discount, 'reserved', @user_id, CURRENT_TIMESTAMP, @notes)
                RETURNING record_id
                """;
            await using var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection, transaction);
            header.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
            header.Parameters.AddWithValue("total", NpgsqlDbType.Numeric, total);
            header.Parameters.AddWithValue("paid", NpgsqlDbType.Numeric, paid);
            header.Parameters.AddWithValue("debt", NpgsqlDbType.Numeric, debt);
            header.Parameters.AddWithValue("discount", NpgsqlDbType.Numeric, discount);
            header.Parameters.AddWithValue("with_discount", NpgsqlDbType.Boolean, withDiscount);
            header.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
            header.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Varchar)
            {
                Value = string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim()
            });
            var consignmentId = Convert.ToInt32(await header.ExecuteScalarAsync(cancellationToken));
            await DatabaseAuditContext.SetReferenceAsync(connection, transaction, $"E{consignmentId}", userId, cancellationToken);

            foreach (var payment in payments.Where(payment => payment.Amount > 0))
                await InsertConsignmentPaymentAsync(connection, transaction, consignmentId, payment, cancellationToken);
            if (debt > 0)
                await ChangePatientDebtAsync(connection, transaction, patientId, debt, cancellationToken);

            var reference = $"E{consignmentId}";
            foreach (var line in lines)
            {
                await InsertConsignmentItemAsync(connection, transaction, consignmentId, line, cancellationToken);
                await InsertMovementAsync(connection, transaction, line.Item.EnteredCode, "RESERVED", reference,
                    RetailSaleMovementAddress.Format(patientId), userId, cancellationToken);
            }
            await AddPatientConsignedQrsAsync(connection, transaction, patientId,
                lines.Select(line => line.Item.EnteredCode).ToArray(), cancellationToken);
            await RefreshConsignmentQuantitiesAsync(connection, transaction,
                lines.Select(line => line.Item.Barcode), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return consignmentId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ConsignmentSaleSnapshot> LoadAsync(int consignmentId, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string headerSql = """
            SELECT sale.patient_id,
                   COALESCE(patient.tc_identity_no, ''),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                            NULLIF(patient.tc_identity_no, ''), patient.record_id::text),
                   COALESCE(patient.phone_numbers[1], ''), COALESCE(patient.debt_amount, 0),
                   COALESCE(patient.debt_credit_score, 0), patient.photo, sale.status, sale.notes
            FROM public."consignmentSales" sale
            JOIN public.patient patient ON patient.record_id = sale.patient_id
            WHERE sale.record_id = @record_id
            """;
        await using var header = pharmacyApp.Infrastructure.DatabaseCommand.Create(headerSql, connection);
        header.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, consignmentId);
        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Emanet satış bulunamadı.");
        var patient = new PatientSuggestion(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6));
        var status = reader.GetString(7);
        var notes = reader.IsDBNull(8) ? null : reader.GetString(8);
        await reader.DisposeAsync();

        const string itemSql = """
            SELECT item.qr_code, item.barcode, COALESCE(active.status, ''), product.record_id,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), item.barcode),
                   item.gross_price, price.warehouse_price, COALESCE(stock.quantity, 0),
                   active.expiry_date, COALESCE(item.entry_cost, active.purchase_cost),
                   (product.photo)[1], price.public_price, product.prescription_type_name,
                   product.indication_summary, item.discount_amount
            FROM public."consignmentSaleItems" item
            JOIN public.barcode product ON product.barcode = item.barcode
            LEFT JOIN public."qrActive" active ON active.qr_code = item.qr_code
            LEFT JOIN public."barcodeActive" stock ON stock.barcode = item.barcode
            LEFT JOIN public."barcodePrices" price ON price.record_reference_no = product.record_id
            WHERE item.consignment_sale_id = @record_id
            ORDER BY item.record_id
            """;
        await using var itemCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(itemSql, connection);
        itemCommand.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, consignmentId);
        var lines = new List<RetailSaleLineInput>();
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            var item = new RetailSaleItem(itemReader.GetString(0), itemReader.GetString(1), false,
                itemReader.GetString(2), itemReader.GetInt32(3), itemReader.GetString(4), itemReader.GetDecimal(5),
                itemReader.IsDBNull(6) ? null : itemReader.GetDecimal(6), itemReader.GetInt32(7),
                itemReader.IsDBNull(8) ? null : itemReader.GetDateTime(8),
                itemReader.IsDBNull(9) ? null : itemReader.GetDecimal(9),
                itemReader.IsDBNull(10) ? null : itemReader.GetFieldValue<byte[]>(10),
                itemReader.IsDBNull(11) ? null : itemReader.GetDecimal(11),
                itemReader.IsDBNull(12) ? null : itemReader.GetString(12),
                itemReader.IsDBNull(13) ? null : itemReader.GetString(13));
            lines.Add(new RetailSaleLineInput(item, itemReader.GetDecimal(14)));
        }
        await itemReader.DisposeAsync();

        const string paymentSql = """
            SELECT payment_type, COALESCE(SUM(amount), 0)
            FROM public."consignmentSalePayements"
            WHERE consignment_sale_id = @record_id
            GROUP BY payment_type
            """;
        await using var paymentCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(paymentSql, connection);
        paymentCommand.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, consignmentId);
        var payments = Enum.GetValues<RetailPaymentKind>().ToDictionary(kind => kind, _ => 0m);
        await using var paymentReader = await paymentCommand.ExecuteReaderAsync(cancellationToken);
        while (await paymentReader.ReadAsync(cancellationToken))
            if (Enum.IsDefined(typeof(RetailPaymentKind), (short)paymentReader.GetInt16(0)))
                payments[(RetailPaymentKind)paymentReader.GetInt16(0)] = paymentReader.GetDecimal(1);

        return new ConsignmentSaleSnapshot(consignmentId, patient, lines, payments, status, notes);
    }

    public async Task<ConsignmentReturnResult> ReturnAsync(int consignmentId, string userId, CancellationToken cancellationToken)
    {
        userId = NormalizeUser(userId);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await DatabaseAuditContext.SetReferenceAsync(connection, transaction, $"E{consignmentId}", userId, cancellationToken);
            var header = await LockHeaderAsync(connection, transaction, consignmentId, cancellationToken);
            var items = await LoadLockedItemsAsync(connection, transaction, consignmentId, cancellationToken);
            await ValidateReservedItemsAsync(connection, transaction, consignmentId, items, cancellationToken);
            if (header.Debt > 0)
                await ChangePatientDebtAsync(connection, transaction, header.PatientId, -header.Debt, cancellationToken);

            foreach (var item in items)
                await InsertMovementAsync(connection, transaction, item.QrCode, "CONSIGN_RETURN", $"E{consignmentId}",
                    RetailSaleMovementAddress.Format(header.PatientId), userId, cancellationToken);
            await RemovePatientConsignedQrsAsync(connection, transaction, header.PatientId,
                items.Select(item => item.QrCode).ToArray(), cancellationToken);
            await RefreshConsignmentQuantitiesAsync(connection, transaction, items.Select(item => item.Barcode), cancellationToken);

            const string paymentSql = """
                UPDATE public."consignmentSalePayements" payment
                SET settlement_status = CASE type.payment_type
                        WHEN 'debt' THEN 'debt_reversed'
                        WHEN 'discount' THEN 'closed'
                        ELSE 'refunded' END,
                    settled_at = CURRENT_TIMESTAMP
                FROM public."payementTypes" type
                WHERE payment.payment_type = type.record_id
                  AND payment.consignment_sale_id = @record_id
                """;
            await using (var payment = pharmacyApp.Infrastructure.DatabaseCommand.Create(paymentSql, connection, transaction))
            {
                payment.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, consignmentId);
                await payment.ExecuteNonQueryAsync(cancellationToken);
            }
            const string closeSql = """
                UPDATE public."consignmentSales"
                SET status = 'returned', refunded_amount = paid_amount, debt_reversed_amount = debt_amount,
                    settled_at = CURRENT_TIMESTAMP, settled_by = @user_id
                WHERE record_id = @record_id AND status = 'reserved'
                """;
            await using (var close = pharmacyApp.Infrastructure.DatabaseCommand.Create(closeSql, connection, transaction))
            {
                close.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, consignmentId);
                close.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
                if (await close.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Emanet satış başka bir işlem tarafından kapatıldı.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new ConsignmentReturnResult(header.Paid, header.Debt);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> CompleteRetailSaleAsync(
        int consignmentId,
        IReadOnlyList<RetailSaleLineInput> lines,
        IReadOnlyList<RetailSalePaymentInput> payments,
        string? notes,
        string userId,
        CancellationToken cancellationToken)
    {
        ValidateSerializedLines(lines);
        if (payments.Any(payment => payment.Kind == RetailPaymentKind.Debt && payment.Amount > 0))
            throw new InvalidOperationException("Emanetin perakende satışa dönüşümünde yeni veresiye bırakılamaz.");
        userId = NormalizeUser(userId);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await DatabaseAuditContext.SetReferenceAsync(connection, transaction, $"E{consignmentId}", userId, cancellationToken);
            var header = await LockHeaderAsync(connection, transaction, consignmentId, cancellationToken);
            var items = await LoadLockedItemsAsync(connection, transaction, consignmentId, cancellationToken);
            var originalPayments = await LoadPaymentTotalsAsync(connection, transaction, consignmentId, cancellationToken);
            ValidateSettlementInput(lines, payments, header, items, originalPayments);
            await ValidateReservedItemsAsync(connection, transaction, consignmentId, items, cancellationToken);

            var saleId = await InsertRetailSaleAsync(connection, transaction, header.PatientId, header.Total,
                header.Discount > 0, notes, userId, cancellationToken);
            foreach (var payment in payments.Where(payment => payment.Amount > 0))
                await InsertRetailPaymentAsync(connection, transaction, saleId, payment, cancellationToken);
            if (header.Debt > 0)
                await ChangePatientDebtAsync(connection, transaction, header.PatientId, -header.Debt, cancellationToken);

            foreach (var line in lines)
            {
                await InsertRetailItemAsync(connection, transaction, saleId, line, cancellationToken);
                await InsertMovementAsync(connection, transaction, line.Item.EnteredCode, "PERAK_OUT", $"P{saleId}",
                    RetailSaleMovementAddress.Format(header.PatientId), userId, cancellationToken);
                await UpdateInactiveExitPriceAsync(connection, transaction, line.Item.EnteredCode, line.ExitPrice, cancellationToken);
            }
            await RemovePatientConsignedQrsAsync(connection, transaction, header.PatientId,
                items.Select(item => item.QrCode).ToArray(), cancellationToken);
            await RefreshConsignmentQuantitiesAsync(connection, transaction, items.Select(item => item.Barcode), cancellationToken);

            const string closeSql = """
                UPDATE public."consignmentSales"
                SET status = 'converted_to_retail', settlement_sale_id = @sale_id,
                    debt_reversed_amount = debt_amount, settled_at = CURRENT_TIMESTAMP, settled_by = @user_id
                WHERE record_id = @record_id AND status = 'reserved'
                """;
            await using (var close = pharmacyApp.Infrastructure.DatabaseCommand.Create(closeSql, connection, transaction))
            {
                close.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
                close.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
                close.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, consignmentId);
                if (await close.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Emanet satış başka bir işlem tarafından kapatıldı.");
            }
            await using (var settlePayments = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "UPDATE public.\"consignmentSalePayements\" SET settlement_status = 'applied_to_retail', settled_at = CURRENT_TIMESTAMP WHERE consignment_sale_id = @record_id",
                connection, transaction))
            {
                settlePayments.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, consignmentId);
                await settlePayments.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var paymentCheck = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "SELECT payement_finished FROM public.sales WHERE record_id = @sale_id", connection, transaction))
            {
                paymentCheck.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
                if (await paymentCheck.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new InvalidOperationException("Perakende satış ödeme toplamı doğrulanamadı.");
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

    private static void ValidateSerializedLines(IReadOnlyList<RetailSaleLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) throw new InvalidOperationException("Emanet listesi boş.");
        if (lines.Any(line => line.Item.IsBarcodeOnly))
            throw new InvalidOperationException("Emanet satışta ilaç kutusunun karekodu okutulmalıdır.");
        if (lines.Select(line => line.Item.EnteredCode).Distinct(StringComparer.Ordinal).Count() != lines.Count)
            throw new InvalidOperationException("Aynı karekod emanet listesine birden fazla kez eklenemez.");
    }

    private static void ValidateSettlementInput(
        IReadOnlyList<RetailSaleLineInput> lines,
        IReadOnlyList<RetailSalePaymentInput> payments,
        (int PatientId, decimal Total, decimal Paid, decimal Debt, decimal Discount) header,
        IReadOnlyList<(string QrCode, string Barcode, decimal Gross, decimal Discount, decimal? EntryCost)> items,
        IReadOnlyDictionary<RetailPaymentKind, decimal> originalPayments)
    {
        var expected = items.OrderBy(item => item.QrCode, StringComparer.Ordinal).ToArray();
        var actual = lines.OrderBy(line => line.Item.EnteredCode, StringComparer.Ordinal).ToArray();
        if (actual.Length != expected.Length || actual.Where((line, index) =>
                line.Item.EnteredCode != expected[index].QrCode || line.Item.Barcode != expected[index].Barcode ||
                Round(line.Item.UnitPrice) != expected[index].Gross || Round(line.DiscountAmount) != expected[index].Discount).Any())
            throw new InvalidOperationException("Emanet kutuları, fiyatları veya iskontoları değiştirilemez.");

        RetailSaleRules.ValidatePayments(header.Total, payments, header.Discount > 0, header.PatientId);
        RetailSaleRules.ValidateLineDiscounts(lines, header.Discount, header.Discount > 0);
        foreach (var kind in new[] { RetailPaymentKind.Cash, RetailPaymentKind.Credit, RetailPaymentKind.BankTransfer })
        {
            var finalAmount = Round(payments.Where(payment => payment.Kind == kind).Sum(payment => payment.Amount));
            if (finalAmount < originalPayments[kind])
                throw new InvalidOperationException("Emanet verilirken alınan ödeme, perakende satış dönüşümünde azaltılamaz.");
        }
        var finalDiscount = Round(payments.Where(payment => payment.Kind == RetailPaymentKind.Discount).Sum(payment => payment.Amount));
        if (finalDiscount != originalPayments[RetailPaymentKind.Discount])
            throw new InvalidOperationException("Emanete ait iskonto perakende satış dönüşümünde değiştirilemez.");
    }

    private static async Task ValidateAvailableQrAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RetailSaleItem item, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT barcode, status, reserved_prescription_uid
            FROM public."qrActive"
            WHERE qr_code = @qr_code
            FOR UPDATE
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.EnteredCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Karekod aktif stokta bulunamadı: {item.EnteredCode}");
        if (reader.GetString(0) != item.Barcode)
            throw new InvalidOperationException($"Karekodun ürün bilgisi değişti: {item.EnteredCode}");
        if (!reader.IsDBNull(1))
            throw new InvalidOperationException($"Karekod başka bir işlem için ayrılmış: {item.EnteredCode}");
        if (!reader.IsDBNull(2))
            throw new InvalidOperationException($"Karekod reçete için ayrılmış: {item.EnteredCode}");
    }

    private static async Task EnsurePatientExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int patientId, CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("SELECT 1 FROM public.patient WHERE record_id = @patient_id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("Seçilen hasta bulunamadı.");
    }

    private static async Task<(int PatientId, decimal Total, decimal Paid, decimal Debt, decimal Discount)> LockHeaderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int recordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT patient_id, total_amount, paid_amount, debt_amount, discount_amount
            FROM public."consignmentSales"
            WHERE record_id = @record_id AND status = 'reserved'
            FOR UPDATE
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Emanet satış bulunamadı veya daha önce kapatıldı.");
        return (reader.GetInt32(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4));
    }

    private static async Task<IReadOnlyList<(string QrCode, string Barcode, decimal Gross, decimal Discount, decimal? EntryCost)>> LoadLockedItemsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int recordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT qr_code, barcode, gross_price, discount_amount, entry_cost
            FROM public."consignmentSaleItems"
            WHERE consignment_sale_id = @record_id
            ORDER BY record_id
            FOR UPDATE
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, recordId);
        var result = new List<(string, string, decimal, decimal, decimal?)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4)));
        if (result.Count == 0) throw new InvalidOperationException("Emanet satışın karekodları bulunamadı.");
        return result;
    }

    private static async Task<IReadOnlyDictionary<RetailPaymentKind, decimal>> LoadPaymentTotalsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int recordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payment_type, COALESCE(SUM(amount), 0)
            FROM public."consignmentSalePayements"
            WHERE consignment_sale_id = @record_id
            GROUP BY payment_type
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, recordId);
        var result = Enum.GetValues<RetailPaymentKind>().ToDictionary(kind => kind, _ => 0m);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = (RetailPaymentKind)reader.GetInt16(0);
            if (result.ContainsKey(kind)) result[kind] = reader.GetDecimal(1);
        }
        return result;
    }

    private static async Task ValidateReservedItemsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int consignmentId, IEnumerable<(string QrCode, string Barcode, decimal Gross, decimal Discount, decimal? EntryCost)> items,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
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
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != item.Barcode ||
                !RetailSaleRepository.IsConsigned(reader.IsDBNull(1) ? null : reader.GetString(1)))
                throw new InvalidOperationException($"Emanet karekod ayrılmış stokta bulunamadı: {item.QrCode} (E{consignmentId})");
        }
    }

    private static async Task ChangePatientDebtAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int patientId, decimal change, CancellationToken cancellationToken)
    {
        var sql = change >= 0
            ? "UPDATE public.patient SET debt_amount = COALESCE(debt_amount, 0) + @amount WHERE record_id = @patient_id"
            : "UPDATE public.patient SET debt_amount = COALESCE(debt_amount, 0) - @amount WHERE record_id = @patient_id AND COALESCE(debt_amount, 0) >= @amount";
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, Math.Abs(change));
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException(change >= 0 ? "Hasta borcu yazılamadı." : "Emanete ait borç hasta hesabından silinemedi.");
    }

    private static async Task InsertConsignmentItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int saleId, RetailSaleLineInput line, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."consignmentSaleItems" (
                consignment_sale_id, qr_code, barcode, gross_price, discount_amount, net_price, entry_cost)
            VALUES (@sale_id, @qr_code, @barcode, @gross, @discount, @net, @entry_cost)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, line.Item.EnteredCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, line.Item.Barcode);
        command.Parameters.AddWithValue("gross", NpgsqlDbType.Numeric, line.Item.UnitPrice);
        command.Parameters.AddWithValue("discount", NpgsqlDbType.Numeric, line.DiscountAmount);
        command.Parameters.AddWithValue("net", NpgsqlDbType.Numeric, line.ExitPrice);
        command.Parameters.Add(new NpgsqlParameter("entry_cost", NpgsqlDbType.Numeric) { Value = line.Item.EntryCost ?? (object)DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertConsignmentPaymentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int saleId, RetailSalePaymentInput payment, CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "INSERT INTO public.\"consignmentSalePayements\" (consignment_sale_id, payment_type, amount) VALUES (@sale_id, @type, @amount)",
            connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("type", NpgsqlDbType.Smallint, (short)payment.Kind);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, payment.Amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMovementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string qrCode, string movementCode, string reference, string address, string userId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            VALUES (@qr_code, (SELECT record_id FROM public."qrMovementTypes" WHERE code = @movement_code),
                    CURRENT_TIMESTAMP, @address, @user_id, @reference)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        command.Parameters.AddWithValue("movement_code", NpgsqlDbType.Varchar, movementCode);
        command.Parameters.AddWithValue("address", NpgsqlDbType.Varchar, address);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("reference", NpgsqlDbType.Varchar, reference);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddPatientConsignedQrsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int patientId, string[] qrCodes, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.patient
            SET consigned_qr_codes = ARRAY(
                SELECT DISTINCT value
                FROM unnest(COALESCE(consigned_qr_codes, ARRAY[]::varchar[]) || @qr_codes) value)
            WHERE record_id = @patient_id
            """;
        await ExecutePatientQrUpdateAsync(connection, transaction, sql, patientId, qrCodes, cancellationToken);
    }

    private static async Task RemovePatientConsignedQrsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int patientId, string[] qrCodes, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.patient
            SET consigned_qr_codes = ARRAY(
                SELECT value FROM unnest(COALESCE(consigned_qr_codes, ARRAY[]::varchar[])) value
                WHERE NOT (value = ANY(@qr_codes)))
            WHERE record_id = @patient_id
            """;
        await ExecutePatientQrUpdateAsync(connection, transaction, sql, patientId, qrCodes, cancellationToken);
    }

    private static async Task ExecutePatientQrUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, int patientId, string[] qrCodes, CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        command.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, qrCodes);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Hasta emanet karekodları güncellenemedi.");
    }

    private static async Task RefreshConsignmentQuantitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IEnumerable<string> barcodes, CancellationToken cancellationToken)
    {
        foreach (var barcode in barcodes.Distinct(StringComparer.Ordinal))
        {
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("SELECT public.pharmacy_refresh_consignment_quantity(@barcode)", connection, transaction);
            command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, barcode);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> InsertRetailSaleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int patientId, decimal total, bool withDiscount, string? notes, string userId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.sales (
                patient_id, institution_amount, total_amount, payement_finished,
                institution_payement_finished, debted, user_no, "timestamp", with_iskonto, notes)
            VALUES (@patient_id, 0, @total, FALSE, FALSE, FALSE, @user_id, CURRENT_TIMESTAMP, @with_discount, @notes)
            RETURNING record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        command.Parameters.AddWithValue("total", NpgsqlDbType.Numeric, total);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("with_discount", NpgsqlDbType.Boolean, withDiscount);
        command.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Varchar) { Value = string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim() });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertRetailPaymentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int saleId, RetailSalePaymentInput payment, CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "INSERT INTO public.\"salesPayements\" (sale_id, payment_type, amount) VALUES (@sale_id, @type, @amount)", connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("type", NpgsqlDbType.Smallint, (short)payment.Kind);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, payment.Amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRetailItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int saleId, RetailSaleLineInput line, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public."salesQrItems" (sale_id, qr_code, barcode, gross_price, discount_amount, exit_price, entry_cost)
            VALUES (@sale_id, @qr_code, @barcode, @gross, @discount, @exit, @entry_cost)
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, line.Item.EnteredCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, line.Item.Barcode);
        command.Parameters.AddWithValue("gross", NpgsqlDbType.Numeric, line.Item.UnitPrice);
        command.Parameters.AddWithValue("discount", NpgsqlDbType.Numeric, line.DiscountAmount);
        command.Parameters.AddWithValue("exit", NpgsqlDbType.Numeric, line.ExitPrice);
        command.Parameters.Add(new NpgsqlParameter("entry_cost", NpgsqlDbType.Numeric) { Value = line.Item.EntryCost ?? (object)DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateInactiveExitPriceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string qrCode, decimal exitPrice, CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "UPDATE public.\"qrInactive\" SET exit_price = @exit_price WHERE qr_code = @qr_code", connection, transaction);
        command.Parameters.AddWithValue("exit_price", NpgsqlDbType.Numeric, exitPrice);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static string NormalizeUser(string userId) => string.IsNullOrWhiteSpace(userId) ? "admin" : userId.Trim();
}
