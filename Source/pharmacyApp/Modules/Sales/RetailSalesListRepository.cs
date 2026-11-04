using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Sales;

public sealed class RetailSalesListRepository
{
    private const string ResolvedMovementBarcode = """
        COALESCE(
            inactive.barcode,
            active.barcode,
            CASE
                WHEN movement.qr_code ~ '^[0-9]{8,14}$' THEN movement.qr_code
                ELSE public.pharmacy_qr_barcode(movement.qr_code)
            END)
        """;

    public async Task<IReadOnlyList<RetailSaleListItem>> LoadSalesAsync(
        RetailSalesListQuery query,
        CancellationToken cancellationToken)
    {
        if (query.StartDate >= query.EndDateExclusive)
            throw new ArgumentException("Başlangıç tarihi bitiş tarihinden önce olmalıdır.", nameof(query));

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var sql = $"""
            WITH sale_payment_totals AS (
                SELECT payment.sale_id,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'cash'), 0) AS cash_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'credit'), 0) AS credit_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'banktransfer'), 0) AS transfer_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'debt'), 0) AS debt_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'discount'), 0) AS discount_amount,
                       COALESCE(SUM(payment.amount) FILTER (
                           WHERE payment.payment_role IN ('institution_primary', 'institution_secondary')
                              OR (payment.payment_role IS NULL AND type.payment_type = 'institution')), 0) AS institution_amount
                FROM public."salesPayements" payment
                JOIN public."payementTypes" type ON type.record_id = payment.payment_type
                GROUP BY payment.sale_id
            ), refund_payment_totals AS (
                SELECT payment.refund_id,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'cash'), 0) AS cash_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'credit'), 0) AS credit_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'banktransfer'), 0) AS transfer_amount
                FROM public."salesRefundPayements" payment
                JOIN public."payementTypes" type ON type.record_id = payment.payment_type
                GROUP BY payment.refund_id
            ), consignment_payment_totals AS (
                SELECT payment.consignment_sale_id,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'cash'), 0) AS cash_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'credit'), 0) AS credit_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'banktransfer'), 0) AS transfer_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'debt'), 0) AS debt_amount,
                       COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type = 'discount'), 0) AS discount_amount
                FROM public."consignmentSalePayements" payment
                JOIN public."payementTypes" type ON type.record_id = payment.payment_type
                GROUP BY payment.consignment_sale_id
            ), consignment_item_totals AS (
                SELECT consignment_sale_id, COUNT(*)::integer AS medicine_quantity
                FROM public."consignmentSaleItems"
                GROUP BY consignment_sale_id
            ), movement_totals AS (
                SELECT movement.reference_code, COUNT(*)::integer AS medicine_quantity
                FROM public."qrMovements" movement
                JOIN public."qrMovementTypes" type ON type.record_id = movement.movement_type
                WHERE type.code IN ('PERAK_OUT', 'PRESC_OUT', 'PERAK_RETURN', 'PRESC_RETURN')
                GROUP BY movement.reference_code
            ), return_movement_info AS (
                SELECT movement.reference_code,
                       BOOL_OR(type.code = 'PRESC_RETURN') AS is_prescription
                FROM public."qrMovements" movement
                JOIN public."qrMovementTypes" type ON type.record_id = movement.movement_type
                WHERE type.code IN ('PERAK_RETURN', 'PRESC_RETURN')
                GROUP BY movement.reference_code
            ), refund_info AS (
                SELECT item.refund_id,
                       BOOL_OR(original.prescription_no IS NOT NULL) AS is_prescription,
                       MAX(original.prescription_no) AS prescription_no
                FROM public."salesRefundItems" item
                JOIN public.sales original ON original.record_id = item.original_sale_id
                GROUP BY item.refund_id
            ), transactions AS (
                SELECT sale.record_id AS transaction_id,
                       'P' || sale.record_id::text AS reference_code,
                       CASE WHEN sale.prescription_no IS NULL THEN 1 ELSE 2 END AS transaction_type,
                       COALESCE(movement_totals.medicine_quantity, 0) AS medicine_quantity,
                       sale.patient_id,
                       NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), '') AS patient_name,
                       sale.prescription_no,
                       COALESCE(sale_payment_totals.cash_amount, 0) AS cash_amount,
                       COALESCE(sale_payment_totals.credit_amount, 0) AS credit_amount,
                       COALESCE(sale_payment_totals.transfer_amount, 0) AS transfer_amount,
                       COALESCE(sale_payment_totals.debt_amount, 0) AS debt_amount,
                       COALESCE(sale_payment_totals.discount_amount, 0) AS discount_amount,
                       COALESCE(sale_payment_totals.institution_amount, 0) AS institution_amount,
                       sale.total_amount,
                       sale.user_no AS user_id,
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''), sale.user_no) AS user_name,
                       sale."timestamp",
                       sale.debted AS is_debted,
                       sale.marked,
                       sale.notes,
                       sale.sale_status AS transaction_status
                FROM public.sales sale
                LEFT JOIN public.patient patient ON patient.record_id = sale.patient_id
                LEFT JOIN public.users app_user ON app_user.user_id = sale.user_no
                LEFT JOIN sale_payment_totals ON sale_payment_totals.sale_id = sale.record_id
                LEFT JOIN movement_totals ON movement_totals.reference_code = 'P' || sale.record_id::text
                WHERE (cardinality(@statuses)=0 AND sale.sale_status <> 'pending')
                   OR (cardinality(@statuses)>0 AND sale.sale_status=ANY(@statuses))

                UNION ALL

                SELECT refund.record_id,
                       'G' || refund.record_id::text,
                       CASE WHEN COALESCE(refund_info.is_prescription, return_movement_info.is_prescription, FALSE) THEN 8 ELSE 4 END,
                       COALESCE(movement_totals.medicine_quantity, 0),
                       refund.patient_id,
                       NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                       refund_info.prescription_no,
                       COALESCE(refund_payment_totals.cash_amount, 0),
                       COALESCE(refund_payment_totals.credit_amount, 0),
                       COALESCE(refund_payment_totals.transfer_amount, 0),
                       COALESCE(refund.debt_reduction_amount, 0),
                       COALESCE(refund.discount_amount, 0),
                       COALESCE(refund.institution_amount, 0)
                           + COALESCE(refund.institution_secondary_amount, 0),
                       refund.total_amount,
                       refund.user_no,
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''), refund.user_no),
                       refund."timestamp",
                       COALESCE(refund.debt_reduction_amount, 0) > 0,
                       refund.marked,
                       refund.notes,
                       refund.refund_status::varchar
                FROM public."salesRefunds" refund
                LEFT JOIN public.patient patient ON patient.record_id = refund.patient_id
                LEFT JOIN public.users app_user ON app_user.user_id = refund.user_no
                LEFT JOIN refund_payment_totals ON refund_payment_totals.refund_id = refund.record_id
                LEFT JOIN refund_info ON refund_info.refund_id = refund.record_id
                LEFT JOIN movement_totals ON movement_totals.reference_code = 'G' || refund.record_id::text
                LEFT JOIN return_movement_info ON return_movement_info.reference_code = 'G' || refund.record_id::text

                UNION ALL

                SELECT consignment.record_id,
                       'E' || consignment.record_id::text,
                       16,
                       COALESCE(consignment_item_totals.medicine_quantity, 0),
                       consignment.patient_id,
                       NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                       NULL::varchar,
                       COALESCE(consignment_payment_totals.cash_amount, 0),
                       COALESCE(consignment_payment_totals.credit_amount, 0),
                       COALESCE(consignment_payment_totals.transfer_amount, 0),
                       COALESCE(consignment_payment_totals.debt_amount, 0),
                       COALESCE(consignment_payment_totals.discount_amount, 0),
                       0::numeric,
                       consignment.total_amount,
                       consignment.user_no,
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''), consignment.user_no),
                       consignment."timestamp",
                       consignment.debt_amount > 0,
                       consignment.marked,
                       consignment.notes,
                       consignment.status
                FROM public."consignmentSales" consignment
                JOIN public.patient patient ON patient.record_id = consignment.patient_id
                LEFT JOIN public.users app_user ON app_user.user_id = consignment.user_no
                LEFT JOIN consignment_payment_totals ON consignment_payment_totals.consignment_sale_id = consignment.record_id
                LEFT JOIN consignment_item_totals ON consignment_item_totals.consignment_sale_id = consignment.record_id

                UNION ALL

                SELECT consignment.record_id,
                       'E' || consignment.record_id::text,
                       32,
                       COALESCE(consignment_item_totals.medicine_quantity, 0),
                       consignment.patient_id,
                       NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                       NULL::varchar,
                       COALESCE(consignment_payment_totals.cash_amount, 0),
                       COALESCE(consignment_payment_totals.credit_amount, 0),
                       COALESCE(consignment_payment_totals.transfer_amount, 0),
                       COALESCE(consignment.debt_reversed_amount, 0),
                       COALESCE(consignment_payment_totals.discount_amount, 0),
                       0::numeric,
                       consignment.total_amount,
                       consignment.settled_by,
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', settled_user.first_name, settled_user.last_name)), ''), consignment.settled_by),
                       COALESCE(consignment.settled_at, consignment."timestamp"),
                       consignment.debt_reversed_amount > 0,
                       consignment.marked,
                       consignment.notes,
                       'returned'::varchar
                FROM public."consignmentSales" consignment
                JOIN public.patient patient ON patient.record_id = consignment.patient_id
                LEFT JOIN public.users settled_user ON settled_user.user_id = consignment.settled_by
                LEFT JOIN consignment_payment_totals ON consignment_payment_totals.consignment_sale_id = consignment.record_id
                LEFT JOIN consignment_item_totals ON consignment_item_totals.consignment_sale_id = consignment.record_id
                WHERE consignment.status = 'returned'
            )
            SELECT transaction_id, reference_code, transaction_type, medicine_quantity, patient_name,
                   prescription_no, cash_amount, credit_amount, transfer_amount, debt_amount,
                   discount_amount, institution_amount, total_amount, user_name, "timestamp", patient_id, marked, notes,
                   transaction_status
            FROM transactions
            WHERE "timestamp" >= @start_date
              AND "timestamp" < @end_exclusive
              AND (CASE WHEN @start_time IS NOT NULL AND @end_time IS NOT NULL AND @start_time>@end_time
                   THEN "timestamp"::time>=@start_time OR date_trunc('minute', "timestamp")::time<=@end_time
                   ELSE (@start_time IS NULL OR "timestamp"::time>=@start_time)
                    AND (@end_time IS NULL OR date_trunc('minute', "timestamp")::time<=@end_time) END)
              AND (@transaction_types & transaction_type) <> 0
              AND (cardinality(@statuses)=0 OR transaction_status=ANY(@statuses))
              AND (@prescription_no='' OR prescription_no=@prescription_no)
              AND (@user_id = '' OR user_id = @user_id)
              AND (@patient_id IS NULL OR patient_id = @patient_id)
              AND (@debt_filter = -1 OR is_debted = (@debt_filter = 1))
              AND (NOT @marked_only OR marked)
              AND (@minimum_total IS NULL OR total_amount >= @minimum_total)
              AND (@maximum_total IS NULL OR total_amount <= @maximum_total)
              AND (NOT @discounted_only OR discount_amount > 0)
              AND (cardinality(@payment_types)=0 OR
                   ('cash'=ANY(@payment_types) AND cash_amount>0) OR
                   ('card'=ANY(@payment_types) AND credit_amount>0) OR
                   ('bank_transfer'=ANY(@payment_types) AND transfer_amount>0) OR
                   ('debt'=ANY(@payment_types) AND debt_amount>0) OR
                   ('discount'=ANY(@payment_types) AND discount_amount>0) OR
                   ('institution'=ANY(@payment_types) AND institution_amount>0))
              AND (cardinality(@qr_codes)=0
                   OR (transactions.transaction_type IN (1, 2) AND EXISTS (
                        SELECT 1 FROM public."salesQrItems" item
                        WHERE item.sale_id=transactions.transaction_id AND item.qr_code=ANY(@qr_codes)))
                   OR (transactions.transaction_type IN (4, 8) AND EXISTS (
                        SELECT 1 FROM public."salesRefundItems" item
                        WHERE item.refund_id=transactions.transaction_id AND item.qr_code=ANY(@qr_codes)))
                   OR (transactions.transaction_type IN (16, 32) AND EXISTS (
                        SELECT 1 FROM public."consignmentSaleItems" item
                        WHERE item.consignment_sale_id=transactions.transaction_id AND item.qr_code=ANY(@qr_codes))))
              AND (cardinality(@barcodes) = 0
                   OR (transactions.transaction_type IN (1, 2) AND EXISTS (
                        SELECT 1 FROM public."salesQrItems" item
                        WHERE item.sale_id = transactions.transaction_id
                          AND item.barcode = ANY(@barcodes)))
                   OR (transactions.transaction_type IN (4, 8) AND EXISTS (
                        SELECT 1 FROM public."salesRefundItems" item
                        WHERE item.refund_id = transactions.transaction_id
                          AND item.barcode = ANY(@barcodes)))
                   OR (transactions.transaction_type IN (16, 32) AND EXISTS (
                        SELECT 1 FROM public."consignmentSaleItems" item
                        WHERE item.consignment_sale_id = transactions.transaction_id
                          AND item.barcode = ANY(@barcodes))))
              AND (@brand_name = '' OR EXISTS (
                    SELECT 1
                    FROM public."qrMovements" movement
                    LEFT JOIN public."qrInactive" inactive ON inactive.qr_code = movement.qr_code
                    LEFT JOIN public."qrActive" active ON active.qr_code = movement.qr_code
                    JOIN public.barcode filtered_product ON filtered_product.barcode = {ResolvedMovementBarcode}
                    WHERE movement.reference_code = transactions.reference_code
                      AND filtered_product.brand_name = @brand_name))
            ORDER BY "timestamp" DESC, reference_code DESC
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("start_date", NpgsqlDbType.Timestamp, query.StartDate);
        command.Parameters.AddWithValue("end_exclusive", NpgsqlDbType.Timestamp, query.EndDateExclusive);
        command.Parameters.Add(new NpgsqlParameter("start_time", NpgsqlDbType.Time) { Value = query.StartTime ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("end_time", NpgsqlDbType.Time) { Value = query.EndTime ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text, (query.Statuses ?? []).ToArray());
        command.Parameters.AddWithValue("prescription_no", NpgsqlDbType.Varchar, query.PrescriptionNo.Trim());
        command.Parameters.AddWithValue("payment_types", NpgsqlDbType.Array | NpgsqlDbType.Text, (query.PaymentTypes ?? []).ToArray());
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, query.UserId.Trim());
        command.Parameters.AddWithValue("brand_name", NpgsqlDbType.Varchar, query.BrandName.Trim());
        command.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Integer) { Value = query.PatientId.HasValue ? query.PatientId.Value : (object)DBNull.Value });
        command.Parameters.AddWithValue("transaction_types", NpgsqlDbType.Integer, (int)query.TransactionTypes);
        command.Parameters.AddWithValue("debt_filter", NpgsqlDbType.Integer, query.IsDebted is null ? -1 : query.IsDebted.Value ? 1 : 0);
        command.Parameters.AddWithValue("marked_only", NpgsqlDbType.Boolean, query.MarkedOnly);
        command.Parameters.Add(new NpgsqlParameter("minimum_total", NpgsqlDbType.Numeric) { Value = query.MinimumTotal.HasValue ? query.MinimumTotal.Value : (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("maximum_total", NpgsqlDbType.Numeric) { Value = query.MaximumTotal.HasValue ? query.MaximumTotal.Value : (object)DBNull.Value });
        command.Parameters.AddWithValue("discounted_only", NpgsqlDbType.Boolean, query.DiscountedOnly);
        var qrCodes = string.IsNullOrWhiteSpace(query.QrCode)
            ? (query.QrCodes ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray()
            : [query.QrCode.Trim()];
        command.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, qrCodes);
        var barcodes = (query.Barcodes is { Count: > 0 } ? query.Barcodes : [query.Barcode])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        command.Parameters.AddWithValue("barcodes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, barcodes);

        var sales = new List<RetailSaleListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sales.Add(new RetailSaleListItem
            {
                SaleId = reader.GetInt32(0),
                ReferenceCode = reader.GetString(1),
                TransactionType = (RetailTransactionTypes)reader.GetInt32(2),
                MedicineQuantity = reader.GetInt32(3),
                PatientName = GetString(reader, 4),
                PrescriptionNo = GetString(reader, 5),
                CashAmount = reader.GetDecimal(6),
                CreditAmount = reader.GetDecimal(7),
                TransferAmount = reader.GetDecimal(8),
                DebtAmount = reader.GetDecimal(9),
                DiscountAmount = reader.GetDecimal(10),
                InstitutionAmount = reader.GetDecimal(11),
                TotalAmount = reader.GetDecimal(12),
                UserName = GetString(reader, 13),
                Timestamp = reader.GetDateTime(14),
                PatientId = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                IsMarked = reader.GetBoolean(16),
                Notes = GetString(reader, 17),
                TransactionStatus = GetString(reader, 18)
            });
        }
        return sales;
    }

    public async Task SetMarkedAsync(
        int transactionId,
        RetailTransactionTypes transactionType,
        bool marked,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var sql = transactionType switch
        {
            RetailTransactionTypes.RetailRefund or RetailTransactionTypes.PrescriptionRefund =>
                "UPDATE public.\"salesRefunds\" SET marked = @marked WHERE record_id = @record_id",
            RetailTransactionTypes.ConsignmentSale =>
                "UPDATE public.\"consignmentSales\" SET marked = @marked WHERE record_id = @record_id",
            RetailTransactionTypes.ConsignmentCancellation =>
                "UPDATE public.\"consignmentSales\" SET marked = @marked WHERE record_id = @record_id",
            _ => "UPDATE public.sales SET marked = @marked WHERE record_id = @record_id"
        };
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("marked", NpgsqlDbType.Boolean, marked);
        command.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, transactionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetailSaleProductGroup>> LoadSaleDetailsAsync(
        RetailSaleListItem transactionItem,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var sql = $"""
            WITH movement_rows AS (
                SELECT movement.*,
                       ROW_NUMBER() OVER (PARTITION BY movement.qr_code ORDER BY movement.record_id) AS occurrence
                FROM public."qrMovements" movement
                JOIN public."qrMovementTypes" type ON type.record_id = movement.movement_type
                WHERE movement.reference_code = @reference_code
                  AND type.code IN ('PERAK_OUT', 'PRESC_OUT', 'PERAK_RETURN', 'PRESC_RETURN', 'RESERVED')
            ), pricing_rows AS (
                SELECT qr_code, barcode, gross_price, discount_amount, entry_cost,
                       ROW_NUMBER() OVER (PARTITION BY qr_code ORDER BY record_id) AS occurrence
                FROM public."salesQrItems"
                WHERE @is_refund = FALSE AND @is_consignment = FALSE AND sale_id = @transaction_id
                UNION ALL
                SELECT qr_code, barcode, gross_price, discount_amount, entry_cost,
                       ROW_NUMBER() OVER (PARTITION BY qr_code ORDER BY record_id) AS occurrence
                FROM public."salesRefundItems"
                WHERE @is_refund = TRUE AND @is_consignment = FALSE AND refund_id = @transaction_id
                UNION ALL
                SELECT qr_code, barcode, gross_price, discount_amount, entry_cost,
                       ROW_NUMBER() OVER (PARTITION BY qr_code ORDER BY record_id) AS occurrence
                FROM public."consignmentSaleItems"
                WHERE @is_consignment = TRUE AND consignment_sale_id = @transaction_id
            )
            SELECT movement.qr_code,
                   COALESCE(pricing.barcode, {ResolvedMovementBarcode}, 'Barkod bulunamadı') AS barcode,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), pricing.barcode, {ResolvedMovementBarcode}, 'Ürün bilgisi yok') AS product_name,
                   product.record_id,
                   COALESCE(stock.quantity, 0),
                   price.warehouse_price,
                   COALESCE(pricing.gross_price, price.sale_price, price.public_price, price.public_paid_price, 0),
                   COALESCE(active.expiry_date, inactive.expiry_date),
                   COALESCE(pricing.entry_cost, active.purchase_cost, inactive.entry_cost),
                   COALESCE(pricing.discount_amount, 0)
            FROM movement_rows movement
            LEFT JOIN pricing_rows pricing ON pricing.qr_code = movement.qr_code AND pricing.occurrence = movement.occurrence
            LEFT JOIN public."qrInactive" inactive ON inactive.qr_code = movement.qr_code
            LEFT JOIN public."qrActive" active ON active.qr_code = movement.qr_code
            LEFT JOIN public.barcode product ON product.barcode = COALESCE(pricing.barcode, {ResolvedMovementBarcode})
            LEFT JOIN public."barcodeActive" stock ON stock.barcode = COALESCE(pricing.barcode, {ResolvedMovementBarcode})
            LEFT JOIN public."barcodePrices" price ON price.record_reference_no = product.record_id
            ORDER BY product_name, barcode, movement.record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, transactionItem.ReferenceCode);
        command.Parameters.AddWithValue("transaction_id", NpgsqlDbType.Integer, transactionItem.SaleId);
        command.Parameters.AddWithValue("is_refund", NpgsqlDbType.Boolean, transactionItem.IsRefund);
        command.Parameters.AddWithValue("is_consignment", NpgsqlDbType.Boolean, transactionItem.IsConsignment);
        var rows = new List<(string QrCode, string Barcode, string ProductName, int? ProductRecordId, int StockQuantity, decimal? UnitCost, decimal UnitPrice, DateTime? ExpiryDate, decimal? EntryCost, decimal DiscountAmount)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.GetDecimal(9)));

        return rows
            .GroupBy(row => new { row.Barcode, row.ProductName })
            .Select(group =>
            {
                var product = new RetailSaleProductGroup
                {
                    Barcode = group.Key.Barcode,
                    ProductName = group.Key.ProductName,
                    ProductRecordId = group.First().ProductRecordId,
                    StockQuantity = group.First().StockQuantity,
                    UnitCost = group.First().UnitCost,
                    UnitPrice = group.First().UnitPrice
                };
                foreach (var row in group)
                    product.QrCodes.Add(new RetailSaleQrEntry(row.QrCode, row.ExpiryDate, row.EntryCost, row.DiscountAmount));
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
                                NULLIF(product.brand_name, ''), product.barcode) AS display_name,
                       product.barcode AS filter_value,
                       FALSE AS is_brand,
                       COALESCE(stock.quantity, 0) > 0 AS has_stock,
                       CASE WHEN product.barcode ILIKE @prefix THEN 0 ELSE 1 END AS sort_order
                FROM public.barcode product
                LEFT JOIN public."barcodeActive" stock ON stock.barcode = product.barcode
                WHERE NULLIF(product.barcode, '') IS NOT NULL
                  AND (product.barcode ILIKE @prefix
                       OR COALESCE(product.medicine_name, '') ILIKE @contains
                       OR COALESCE(product.old_name, '') ILIKE @contains)

                UNION ALL

                SELECT '' AS barcode,
                       product.brand_name AS display_name,
                       product.brand_name AS filter_value,
                       TRUE AS is_brand,
                       BOOL_OR(COALESCE(stock.quantity, 0) > 0) AS has_stock,
                       2 AS sort_order
                FROM public.barcode product
                LEFT JOIN public."barcodeActive" stock ON stock.barcode = product.barcode
                WHERE NULLIF(product.brand_name, '') IS NOT NULL
                  AND product.brand_name ILIKE @contains
                GROUP BY product.brand_name
            )
            SELECT barcode, display_name, filter_value, is_brand, has_stock
            FROM suggestions
            ORDER BY sort_order, display_name, barcode
            LIMIT 24
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Varchar, term + "%");
        command.Parameters.AddWithValue("contains", NpgsqlDbType.Varchar, "%" + term + "%");
        var products = new List<SaleProductSuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            products.Add(new SaleProductSuggestion(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        return products;
    }

    public async Task<IReadOnlyList<SaleUserFilterOption>> LoadUsersAsync(CancellationToken cancellationToken)
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
        var users = new List<SaleUserFilterOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            users.Add(new SaleUserFilterOption(reader.GetString(0), reader.GetString(1)));
        return users;
    }

    private static string? GetString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
