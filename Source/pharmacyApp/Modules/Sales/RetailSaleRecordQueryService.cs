using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Sales;

public sealed record RetailSaleRecordFilter(
    DateTime StartDate,
    DateTime EndDateExclusive,
    IReadOnlyList<string> SaleTypes,
    bool IncludeRefunds,
    IReadOnlyList<string> Barcodes,
    IReadOnlyList<string> QrCodes,
    IReadOnlyList<string> PaymentTypes,
    IReadOnlyList<string> Statuses,
    int? PatientRecordId,
    string UserId,
    string PrescriptionNo,
    bool? IsDebted,
    bool MarkedOnly,
    decimal? MinimumTotal,
    decimal? MaximumTotal,
    IReadOnlyList<string>? RecordTypes = null,
    TimeOnly? StartTime = null,
    TimeOnly? EndTime = null,
    string? PatientTc = null,
    bool? HasPrescription = null);

public sealed record RetailSaleRecordSearchQuery(
    RetailSaleRecordFilter Filter,
    int Offset,
    int Limit,
    bool IncludeLines,
    bool NewestFirst);

public sealed record RetailSaleRecordPage(
    IReadOnlyList<RetailSaleRecord> Records,
    int TotalCount,
    int Offset,
    int Limit);

public sealed record RetailSaleRecord(
    string ReferenceCode,
    int RecordId,
    string RecordType,
    string SaleType,
    DateTime Timestamp,
    string Status,
    int? PatientRecordId,
    string? PatientName,
    string? PatientTc,
    string? PrescriptionNo,
    string? UserId,
    string? UserName,
    int Quantity,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal NetAmount,
    decimal? EntryCostTotal,
    decimal? Profit,
    int MissingCostQuantity,
    decimal TotalAmount,
    decimal InstitutionAmount,
    bool IsDebted,
    bool IsMarked,
    string? Notes,
    IReadOnlyList<RetailSaleRecordPayment> Payments,
    IReadOnlyList<RetailSaleRecordLine> Lines);

public sealed record RetailSaleRecordPayment(
    string Type,
    string? Role,
    decimal Amount,
    decimal OwedAmount,
    bool IsFinished,
    DateTime? SettledAt,
    string? InvoiceNo);

public sealed record RetailSaleRecordLine(
    int LineId,
    int? OriginalSaleId,
    string QrCode,
    string Barcode,
    int? ProductRecordId,
    string ProductName,
    decimal GrossPrice,
    decimal DiscountAmount,
    decimal NetPrice,
    decimal? EntryCost,
    decimal? Profit,
    DateTime? ExpiryDate,
    bool InventoryTracked);

public sealed record RetailSaleAggregateQuery(
    RetailSaleRecordFilter Filter,
    string GroupBy,
    int Offset,
    int Limit,
    string SortBy,
    bool Descending);

public sealed record RetailSaleAggregatePage(
    IReadOnlyList<RetailSaleAggregateGroup> Groups,
    int TotalCount,
    int Offset,
    int Limit);

public sealed record RetailSaleAggregateGroup(
    string GroupKey,
    string GroupLabel,
    int RecordCount,
    int SaleCount,
    int RefundCount,
    int Quantity,
    decimal GrossRevenue,
    decimal DiscountTotal,
    decimal NetRevenue,
    decimal? InstitutionRevenue,
    decimal? EntryCostTotal,
    decimal? Profit,
    int MissingCostQuantity,
    decimal AverageRecordRevenue,
    decimal AverageItemRevenue);

public interface IRetailSaleRecordQueryService
{
    Task<RetailSaleRecordPage> SearchAsync(RetailSaleRecordSearchQuery query, CancellationToken cancellationToken);
    Task<RetailSaleRecord?> GetAsync(string referenceCode, CancellationToken cancellationToken);
    Task<RetailSaleAggregatePage> AggregateAsync(RetailSaleAggregateQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Database-side, read-only projection for retail/prescription sales and their refunds.
/// It deliberately uses the immutable per-QR sale/refund snapshots for revenue and profit.
/// </summary>
public sealed class RetailSaleRecordQueryService : IRetailSaleRecordQueryService
{
    private const string RecordCtes = """
        WITH sale_payments AS (
            SELECT payment.sale_id,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='cash'), 0) AS cash_amount,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='credit'), 0) AS card_amount,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='banktransfer'), 0) AS bank_transfer_amount,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='debt'), 0) AS debt_amount,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='discount'), 0) AS payment_discount_amount,
                   COALESCE(SUM(payment.amount) FILTER (
                       WHERE payment.payment_role IN ('institution_primary', 'institution_secondary')
                          OR (payment.payment_role IS NULL AND type.payment_type='institution')), 0) AS institution_amount
            FROM public."salesPayements" payment
            JOIN public."payementTypes" type ON type.record_id=payment.payment_type
            JOIN public.sales scoped_sale ON scoped_sale.record_id=payment.sale_id
            WHERE scoped_sale."timestamp">=@start_date AND scoped_sale."timestamp"<@end_exclusive
              AND (@patient_id IS NULL OR scoped_sale.patient_id=@patient_id)
            GROUP BY payment.sale_id
        ), refund_payments AS (
            SELECT payment.refund_id,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='cash'), 0) AS cash_amount,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='credit'), 0) AS card_amount,
                   COALESCE(SUM(payment.amount) FILTER (WHERE type.payment_type='banktransfer'), 0) AS bank_transfer_amount
            FROM public."salesRefundPayements" payment
            JOIN public."payementTypes" type ON type.record_id=payment.payment_type
            JOIN public."salesRefunds" scoped_refund ON scoped_refund.record_id=payment.refund_id
            WHERE scoped_refund."timestamp">=@start_date AND scoped_refund."timestamp"<@end_exclusive
              AND (@patient_id IS NULL OR scoped_refund.patient_id=@patient_id)
            GROUP BY payment.refund_id
        ), sale_lines AS (
            SELECT item.sale_id,
                   COUNT(*)::integer AS quantity,
                   COALESCE(SUM(item.gross_price), 0) AS gross_amount,
                   COALESCE(SUM(item.discount_amount), 0) AS discount_amount,
                   COALESCE(SUM(item.exit_price), 0) AS net_amount,
                   CASE WHEN COUNT(*) FILTER (WHERE item.entry_cost IS NULL)>0 THEN NULL
                        ELSE COALESCE(SUM(item.entry_cost), 0) END AS entry_cost_total,
                   CASE WHEN COUNT(*) FILTER (WHERE item.entry_cost IS NULL)>0 THEN NULL
                        ELSE COALESCE(SUM(item.exit_price-item.entry_cost), 0) END AS profit,
                   COUNT(*) FILTER (WHERE item.entry_cost IS NULL)::integer AS missing_cost_quantity
            FROM public."salesQrItems" item
            JOIN public.sales scoped_sale ON scoped_sale.record_id=item.sale_id
            WHERE scoped_sale."timestamp">=@start_date AND scoped_sale."timestamp"<@end_exclusive
              AND (@patient_id IS NULL OR scoped_sale.patient_id=@patient_id)
            GROUP BY item.sale_id
        ), refund_lines AS (
            SELECT item.refund_id,
                   COUNT(*)::integer AS quantity,
                   COALESCE(SUM(item.gross_price), 0) AS gross_amount,
                   COALESCE(SUM(item.discount_amount), 0) AS discount_amount,
                   COALESCE(SUM(item.exit_price), 0) AS net_amount,
                   CASE WHEN COUNT(*) FILTER (WHERE item.entry_cost IS NULL)>0 THEN NULL
                        ELSE COALESCE(SUM(item.entry_cost), 0) END AS entry_cost_total,
                   CASE WHEN COUNT(*) FILTER (WHERE item.entry_cost IS NULL)>0 THEN NULL
                        ELSE COALESCE(SUM(item.exit_price-item.entry_cost), 0) END AS profit,
                   COUNT(*) FILTER (WHERE item.entry_cost IS NULL)::integer AS missing_cost_quantity,
                   BOOL_OR(original.prescription_no IS NOT NULL) AS is_prescription,
                   MAX(original.prescription_no) AS prescription_no
            FROM public."salesRefundItems" item
            JOIN public.sales original ON original.record_id=item.original_sale_id
            JOIN public."salesRefunds" scoped_refund ON scoped_refund.record_id=item.refund_id
            WHERE scoped_refund."timestamp">=@start_date AND scoped_refund."timestamp"<@end_exclusive
              AND (@patient_id IS NULL OR scoped_refund.patient_id=@patient_id)
            GROUP BY item.refund_id
        ), records AS (
            SELECT 'P' || sale.record_id::text AS reference_code,
                   sale.record_id, 'sale'::text AS record_type,
                   CASE WHEN sale.prescription_no IS NULL THEN 'retail' ELSE 'prescription' END AS sale_type,
                   sale."timestamp", sale.sale_status AS status,
                   sale.patient_id,
                   NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), '') AS patient_name,
                   patient.tc_identity_no AS patient_tc,
                   sale.prescription_no,
                   sale.user_no AS user_id,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''), sale.user_no) AS user_name,
                   COALESCE(lines.quantity, 0) AS quantity,
                   COALESCE(lines.gross_amount, sale.total_amount) AS gross_amount,
                   COALESCE(lines.discount_amount, payments.payment_discount_amount, 0) AS discount_amount,
                   COALESCE(lines.net_amount, sale.total_amount-COALESCE(payments.payment_discount_amount, 0)) AS net_amount,
                   lines.entry_cost_total, lines.profit, COALESCE(lines.missing_cost_quantity, 0) AS missing_cost_quantity,
                   sale.total_amount,
                   COALESCE(payments.institution_amount, sale.institution_amount, 0) AS institution_amount,
                   sale.debted AS is_debted, sale.marked AS is_marked, sale.notes,
                   COALESCE(payments.cash_amount, 0) AS cash_amount,
                   COALESCE(payments.card_amount, 0) AS card_amount,
                   COALESCE(payments.bank_transfer_amount, 0) AS bank_transfer_amount,
                   COALESCE(payments.debt_amount, 0) AS debt_amount,
                   COALESCE(payments.payment_discount_amount, 0) AS payment_discount_amount
            FROM public.sales sale
            LEFT JOIN public.patient patient ON patient.record_id=sale.patient_id
            LEFT JOIN public.users app_user ON app_user.user_id=sale.user_no
            LEFT JOIN sale_payments payments ON payments.sale_id=sale.record_id
            LEFT JOIN sale_lines lines ON lines.sale_id=sale.record_id
            WHERE sale."timestamp">=@start_date AND sale."timestamp"<@end_exclusive
              AND (@patient_id IS NULL OR sale.patient_id=@patient_id)

            UNION ALL

            SELECT 'G' || refund.record_id::text,
                   refund.record_id, 'refund'::text,
                   CASE WHEN COALESCE(lines.is_prescription, FALSE) THEN 'prescription' ELSE 'retail' END,
                   refund."timestamp", refund.refund_status::text,
                   refund.patient_id,
                   NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                   patient.tc_identity_no,
                   lines.prescription_no,
                   refund.user_no,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''), refund.user_no),
                   COALESCE(lines.quantity, 0),
                   COALESCE(lines.gross_amount, refund.total_amount),
                   COALESCE(lines.discount_amount, refund.discount_amount, 0),
                   COALESCE(lines.net_amount, refund.total_amount-COALESCE(refund.discount_amount, 0)),
                   lines.entry_cost_total, lines.profit, COALESCE(lines.missing_cost_quantity, 0),
                   refund.total_amount,
                   COALESCE(refund.institution_amount, 0)+COALESCE(refund.institution_secondary_amount, 0),
                   COALESCE(refund.debt_reduction_amount, 0)>0, refund.marked, refund.notes,
                   COALESCE(payments.cash_amount, 0), COALESCE(payments.card_amount, 0),
                   COALESCE(payments.bank_transfer_amount, 0), COALESCE(refund.debt_reduction_amount, 0),
                   COALESCE(refund.discount_amount, 0)
            FROM public."salesRefunds" refund
            LEFT JOIN public.patient patient ON patient.record_id=refund.patient_id
            LEFT JOIN public.users app_user ON app_user.user_id=refund.user_no
            LEFT JOIN refund_payments payments ON payments.refund_id=refund.record_id
            LEFT JOIN refund_lines lines ON lines.refund_id=refund.record_id
            WHERE refund."timestamp">=@start_date AND refund."timestamp"<@end_exclusive
              AND (@patient_id IS NULL OR refund.patient_id=@patient_id)
        ), filtered_records AS (
            SELECT record.*
            FROM records record
            WHERE record."timestamp">=@start_date AND record."timestamp"<@end_exclusive
              AND (CASE WHEN @start_time IS NOT NULL AND @end_time IS NOT NULL AND @start_time>@end_time
                   THEN record."timestamp"::time>=@start_time OR date_trunc('minute', record."timestamp")::time<=@end_time
                   ELSE (@start_time IS NULL OR record."timestamp"::time>=@start_time)
                    AND (@end_time IS NULL OR date_trunc('minute', record."timestamp")::time<=@end_time) END)
              AND record.sale_type=ANY(@sale_types)
              AND (@include_refunds OR record.record_type='sale')
              AND (cardinality(@record_types)=0 OR record.record_type=ANY(@record_types))
              AND (cardinality(@statuses)=0
                   AND (record.record_type='refund' OR record.status<>'pending')
                   OR cardinality(@statuses)>0 AND record.status=ANY(@statuses))
              AND (@patient_id IS NULL OR record.patient_id=@patient_id)
              AND (@patient_tc='' OR record.patient_tc=@patient_tc)
              AND (@user_id='' OR record.user_id=@user_id)
              AND (@prescription_no='' OR record.prescription_no=@prescription_no)
              AND (@has_prescription IS NULL OR (NULLIF(BTRIM(record.prescription_no), '') IS NOT NULL)=@has_prescription)
              AND (@debt_filter=-1 OR record.is_debted=(@debt_filter=1))
              AND (NOT @marked_only OR record.is_marked)
              AND (@minimum_total IS NULL OR record.total_amount>=@minimum_total)
              AND (@maximum_total IS NULL OR record.total_amount<=@maximum_total)
              AND (cardinality(@payment_types)=0 OR
                   ('cash'=ANY(@payment_types) AND record.cash_amount>0) OR
                   ('card'=ANY(@payment_types) AND record.card_amount>0) OR
                   ('bank_transfer'=ANY(@payment_types) AND record.bank_transfer_amount>0) OR
                   ('debt'=ANY(@payment_types) AND record.debt_amount>0) OR
                   ('discount'=ANY(@payment_types) AND record.payment_discount_amount>0) OR
                   ('institution'=ANY(@payment_types) AND record.institution_amount>0))
              AND (cardinality(@barcodes)=0 OR EXISTS (
                   SELECT 1 FROM public."salesQrItems" item
                   WHERE record.record_type='sale' AND item.sale_id=record.record_id AND item.barcode=ANY(@barcodes)
                   UNION ALL
                   SELECT 1 FROM public."salesRefundItems" item
                   WHERE record.record_type='refund' AND item.refund_id=record.record_id AND item.barcode=ANY(@barcodes)))
              AND (cardinality(@qr_codes)=0 OR EXISTS (
                   SELECT 1 FROM public."salesQrItems" item
                   WHERE record.record_type='sale' AND item.sale_id=record.record_id AND item.qr_code=ANY(@qr_codes)
                   UNION ALL
                   SELECT 1 FROM public."salesRefundItems" item
                   WHERE record.record_type='refund' AND item.refund_id=record.record_id AND item.qr_code=ANY(@qr_codes)))
        )
        """;

    public async Task<RetailSaleRecordPage> SearchAsync(
        RetailSaleRecordSearchQuery query,
        CancellationToken cancellationToken)
    {
        ValidateFilter(query.Filter);
        ValidatePage(query.Offset, query.Limit);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var order = query.NewestFirst ? "DESC" : "ASC";
        var sql = $"""
            {RecordCtes}, total AS (SELECT COUNT(*)::integer AS count FROM filtered_records),
            page AS (
                SELECT * FROM filtered_records
                ORDER BY "timestamp" {order}, reference_code {order}
                OFFSET @offset LIMIT @limit
            )
            SELECT total.count, page.*
            FROM total LEFT JOIN page ON TRUE
            ORDER BY page."timestamp" {order} NULLS LAST, page.reference_code {order} NULLS LAST
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        BindFilter(command, query.Filter);
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, query.Offset);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.Limit);
        var records = new List<RetailSaleRecord>();
        var totalCount = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                totalCount = reader.GetInt32(0);
                if (reader.IsDBNull(1)) continue;
                records.Add(ReadRecord(reader, 1));
            }
        }
        await LoadChildrenAsync(connection, records, query.IncludeLines, cancellationToken);
        return new RetailSaleRecordPage(records, totalCount, query.Offset, query.Limit);
    }

    public async Task<RetailSaleRecord?> GetAsync(string referenceCode, CancellationToken cancellationToken)
    {
        var canonical = referenceCode.Trim().ToUpperInvariant();
        if (canonical.Length < 2 || canonical[0] is not ('P' or 'G') ||
            !int.TryParse(canonical[1..], out var recordId) || recordId <= 0)
            throw new ArgumentException("Reference code must be P<sale_id> or G<refund_id>.", nameof(referenceCode));

        var filter = new RetailSaleRecordFilter(
            DateTime.MinValue, DateTime.MaxValue, ["retail", "prescription"], true,
            [], [], [], ["pending", "medula_reported", "completed", "reconciliation_required", "cancelled"],
            null, string.Empty, string.Empty, null, false, null, null);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var sql = $"""
            {RecordCtes}
            SELECT * FROM records WHERE reference_code=@reference_code
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        BindFilter(command, filter);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, canonical);
        RetailSaleRecord? record = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) record = ReadRecord(reader, 0);
        if (record is null) return null;
        var records = new List<RetailSaleRecord> { record };
        await LoadChildrenAsync(connection, records, true, cancellationToken);
        return records[0];
    }

    public async Task<RetailSaleAggregatePage> AggregateAsync(
        RetailSaleAggregateQuery query,
        CancellationToken cancellationToken)
    {
        ValidateFilter(query.Filter);
        ValidatePage(query.Offset, query.Limit);
        var groupExpression = query.GroupBy switch
        {
            "day" => "TO_CHAR(record.\"timestamp\", 'YYYY-MM-DD')",
            "month" => "TO_CHAR(record.\"timestamp\", 'YYYY-MM')",
            "sale_type" => "record.sale_type",
            "record_type" => "record.record_type",
            "user" => "COALESCE(record.user_id, '')",
            "patient" => "COALESCE(record.patient_id::text, '')",
            _ => "'all'"
        };
        var labelExpression = query.GroupBy switch
        {
            "user" => "COALESCE(NULLIF(record.user_name, ''), '(unknown)')",
            "patient" => "COALESCE(NULLIF(record.patient_name, ''), '(no patient)')",
            _ => groupExpression
        };
        const string lineProjection = """
            SELECT 'P' || sale_id::text AS reference_code, item.barcode, item.qr_code,
                   item.gross_price, item.discount_amount, item.exit_price, item.entry_cost
            FROM public."salesQrItems" item
            JOIN public.sales scoped_sale ON scoped_sale.record_id=item.sale_id
            WHERE scoped_sale."timestamp">=@start_date AND scoped_sale."timestamp"<@end_exclusive
            UNION ALL
            SELECT 'G' || refund_id::text, item.barcode, item.qr_code,
                   item.gross_price, item.discount_amount, item.exit_price, item.entry_cost
            FROM public."salesRefundItems" item
            JOIN public."salesRefunds" scoped_refund ON scoped_refund.record_id=item.refund_id
            WHERE scoped_refund."timestamp">=@start_date AND scoped_refund."timestamp"<@end_exclusive
            """;
        var hasLineFilter = query.Filter.Barcodes.Count > 0 || query.Filter.QrCodes.Count > 0;
        var source = query.GroupBy == "product"
            ? """
              SELECT line.barcode AS group_key,
                     COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                              NULLIF(product.old_name, ''), line.barcode) AS group_label,
                     COUNT(DISTINCT record.reference_code)::integer AS record_count,
                     COUNT(DISTINCT record.reference_code) FILTER (WHERE record.record_type='sale')::integer AS sale_count,
                     COUNT(DISTINCT record.reference_code) FILTER (WHERE record.record_type='refund')::integer AS refund_count,
                     COALESCE(SUM(CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END), 0)::integer AS quantity,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.gross_price), 0) AS gross_revenue,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.discount_amount), 0) AS discount_total,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.exit_price), 0) AS net_revenue,
                     NULL::numeric AS institution_revenue,
                     CASE WHEN COUNT(*) FILTER (WHERE line.entry_cost IS NULL)>0 THEN NULL
                          ELSE COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.entry_cost), 0) END AS entry_cost_total,
                     CASE WHEN COUNT(*) FILTER (WHERE line.entry_cost IS NULL)>0 THEN NULL
                          ELSE COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*(line.exit_price-line.entry_cost)), 0) END AS profit,
                     COUNT(*) FILTER (WHERE line.entry_cost IS NULL)::integer AS missing_cost_quantity
              FROM filtered_records record
              JOIN (
                  SELECT 'P' || sale_id::text AS reference_code, item.barcode, item.qr_code,
                         item.gross_price, item.discount_amount, item.exit_price, item.entry_cost
                  FROM public."salesQrItems" item
                  JOIN public.sales scoped_sale ON scoped_sale.record_id=item.sale_id
                  WHERE scoped_sale."timestamp">=@start_date AND scoped_sale."timestamp"<@end_exclusive
                  UNION ALL
                  SELECT 'G' || refund_id::text, item.barcode, item.qr_code,
                         item.gross_price, item.discount_amount, item.exit_price, item.entry_cost
                  FROM public."salesRefundItems" item
                  JOIN public."salesRefunds" scoped_refund ON scoped_refund.record_id=item.refund_id
                  WHERE scoped_refund."timestamp">=@start_date AND scoped_refund."timestamp"<@end_exclusive
               ) line ON line.reference_code=record.reference_code
              LEFT JOIN public.barcode product ON product.barcode=line.barcode
              WHERE (cardinality(@barcodes)=0 OR line.barcode=ANY(@barcodes))
                AND (cardinality(@qr_codes)=0 OR line.qr_code=ANY(@qr_codes))
              GROUP BY line.barcode, group_label
              """
            : hasLineFilter
                ? $"""
                  SELECT {groupExpression} AS group_key, {labelExpression} AS group_label,
                         COUNT(DISTINCT record.reference_code)::integer AS record_count,
                         COUNT(DISTINCT record.reference_code) FILTER (WHERE record.record_type='sale')::integer AS sale_count,
                         COUNT(DISTINCT record.reference_code) FILTER (WHERE record.record_type='refund')::integer AS refund_count,
                         COALESCE(SUM(CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END), 0)::integer AS quantity,
                         COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.gross_price), 0) AS gross_revenue,
                         COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.discount_amount), 0) AS discount_total,
                         COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.exit_price), 0) AS net_revenue,
                         NULL::numeric AS institution_revenue,
                         CASE WHEN COUNT(*) FILTER (WHERE line.entry_cost IS NULL)>0 THEN NULL
                              ELSE COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*line.entry_cost), 0) END AS entry_cost_total,
                         CASE WHEN COUNT(*) FILTER (WHERE line.entry_cost IS NULL)>0 THEN NULL
                              ELSE COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*(line.exit_price-line.entry_cost)), 0) END AS profit,
                         COUNT(*) FILTER (WHERE line.entry_cost IS NULL)::integer AS missing_cost_quantity
                  FROM filtered_records record
                  JOIN ({lineProjection}) line ON line.reference_code=record.reference_code
                  WHERE (cardinality(@barcodes)=0 OR line.barcode=ANY(@barcodes))
                    AND (cardinality(@qr_codes)=0 OR line.qr_code=ANY(@qr_codes))
                  GROUP BY group_key, group_label
                  """
            : $"""
              SELECT {groupExpression} AS group_key, {labelExpression} AS group_label,
                     COUNT(*)::integer AS record_count,
                     COUNT(*) FILTER (WHERE record.record_type='sale')::integer AS sale_count,
                     COUNT(*) FILTER (WHERE record.record_type='refund')::integer AS refund_count,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*record.quantity), 0)::integer AS quantity,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*record.gross_amount), 0) AS gross_revenue,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*record.discount_amount), 0) AS discount_total,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*record.net_amount), 0) AS net_revenue,
                     COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*record.institution_amount), 0) AS institution_revenue,
                     CASE WHEN SUM(record.missing_cost_quantity)>0 THEN NULL
                          ELSE COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*record.entry_cost_total), 0) END AS entry_cost_total,
                     CASE WHEN SUM(record.missing_cost_quantity)>0 THEN NULL
                          ELSE COALESCE(SUM((CASE WHEN record.record_type='refund' THEN -1 ELSE 1 END)*record.profit), 0) END AS profit,
                     COALESCE(SUM(record.missing_cost_quantity), 0)::integer AS missing_cost_quantity
              FROM filtered_records record
              GROUP BY group_key, group_label
              """;
        var sort = query.SortBy switch
        {
            "record_count" => "record_count",
            "quantity" => "quantity",
            "gross_revenue" => "gross_revenue",
            "institution_revenue" => "institution_revenue",
            "discount_total" => "discount_total",
            "profit" => "profit",
            "group_key" => "group_key",
            _ => "net_revenue"
        };
        var direction = query.Descending ? "DESC" : "ASC";
        var sql = $"""
            {RecordCtes}, grouped AS ({source}), total AS (SELECT COUNT(*)::integer AS count FROM grouped),
            page AS (SELECT * FROM grouped ORDER BY {sort} {direction} NULLS LAST, group_key ASC OFFSET @offset LIMIT @limit)
            SELECT total.count, page.* FROM total LEFT JOIN page ON TRUE
            ORDER BY page.{sort} {direction} NULLS LAST, page.group_key ASC NULLS LAST
            """;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        BindFilter(command, query.Filter);
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, query.Offset);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.Limit);
        var groups = new List<RetailSaleAggregateGroup>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(0);
            if (reader.IsDBNull(1)) continue;
            var recordCount = reader.GetInt32(3);
            var quantity = reader.GetInt32(6);
            var netRevenue = reader.GetDecimal(9);
            groups.Add(new RetailSaleAggregateGroup(
                reader.GetString(1), reader.GetString(2), recordCount, reader.GetInt32(4), reader.GetInt32(5),
                quantity, reader.GetDecimal(7), reader.GetDecimal(8), netRevenue,
                reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                reader.IsDBNull(12) ? null : reader.GetDecimal(12), reader.GetInt32(13),
                recordCount == 0 ? 0 : decimal.Round(netRevenue/recordCount, 2),
                quantity == 0 ? 0 : decimal.Round(netRevenue/quantity, 2)));
        }
        return new RetailSaleAggregatePage(groups, totalCount, query.Offset, query.Limit);
    }

    private static async Task LoadChildrenAsync(
        NpgsqlConnection connection,
        List<RetailSaleRecord> records,
        bool includeLines,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        var references = records.Select(record => record.ReferenceCode).ToArray();
        var payments = references.ToDictionary(reference => reference, _ => new List<RetailSaleRecordPayment>(), StringComparer.Ordinal);
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT 'P' || payment.sale_id::text, type.payment_type,
                   payment.payment_role, payment.amount, payment.owed_amount,
                   payment.payment_finished, payment.settled_at, payment.invoice_no
            FROM public."salesPayements" payment
            JOIN public."payementTypes" type ON type.record_id=payment.payment_type
            WHERE 'P' || payment.sale_id::text=ANY(@references)
            UNION ALL
            SELECT 'G' || payment.refund_id::text, type.payment_type,
                   payment.payment_role, payment.amount, 0::numeric,
                   payment.payment_finished, payment.settled_at, NULL::varchar
            FROM public."salesRefundPayements" payment
            JOIN public."payementTypes" type ON type.record_id=payment.payment_type
            WHERE 'G' || payment.refund_id::text=ANY(@references)
            ORDER BY 1, 2, 3
            """, connection))
        {
            command.Parameters.AddWithValue("references", NpgsqlDbType.Array | NpgsqlDbType.Varchar, references);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                payments[reader.GetString(0)].Add(new RetailSaleRecordPayment(
                    NormalizePaymentType(reader.GetString(1)), GetString(reader, 2), reader.GetDecimal(3),
                    reader.GetDecimal(4), reader.GetBoolean(5), GetDateTime(reader, 6), GetString(reader, 7)));
        }

        var lines = references.ToDictionary(reference => reference, _ => new List<RetailSaleRecordLine>(), StringComparer.Ordinal);
        if (includeLines)
        {
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                SELECT line.reference_code, line.line_id, line.original_sale_id, line.qr_code, line.barcode,
                       product.record_id,
                       COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                                NULLIF(product.old_name, ''), line.barcode) AS product_name,
                       line.gross_price, line.discount_amount, line.exit_price, line.entry_cost,
                       CASE WHEN line.entry_cost IS NULL THEN NULL ELSE line.exit_price-line.entry_cost END,
                       COALESCE(active.expiry_date, inactive.expiry_date), line.inventory_tracked
                FROM (
                    SELECT 'P' || item.sale_id::text AS reference_code, item.record_id AS line_id,
                           NULL::integer AS original_sale_id, item.qr_code, item.barcode,
                           item.gross_price, item.discount_amount, item.exit_price, item.entry_cost,
                           item.inventory_tracked
                    FROM public."salesQrItems" item
                    UNION ALL
                    SELECT 'G' || item.refund_id::text, item.record_id, item.original_sale_id,
                           item.qr_code, item.barcode, item.gross_price, item.discount_amount,
                           item.exit_price, item.entry_cost, TRUE
                    FROM public."salesRefundItems" item
                ) line
                LEFT JOIN public.barcode product ON product.barcode=line.barcode
                LEFT JOIN public."qrActive" active ON active.qr_code=line.qr_code
                LEFT JOIN public."qrInactive" inactive ON inactive.qr_code=line.qr_code
                WHERE line.reference_code=ANY(@references)
                ORDER BY line.reference_code, product_name, line.line_id
                """, connection);
            command.Parameters.AddWithValue("references", NpgsqlDbType.Array | NpgsqlDbType.Varchar, references);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines[reader.GetString(0)].Add(new RetailSaleRecordLine(
                    reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetString(6),
                    reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9),
                    reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    reader.IsDBNull(11) ? null : reader.GetDecimal(11), GetDateTime(reader, 12), reader.GetBoolean(13)));
        }

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            records[index] = record with
            {
                Payments = payments[record.ReferenceCode],
                Lines = lines[record.ReferenceCode]
            };
        }
    }

    private static RetailSaleRecord ReadRecord(NpgsqlDataReader reader, int offset) => new(
        reader.GetString(offset), reader.GetInt32(offset+1), reader.GetString(offset+2), reader.GetString(offset+3),
        reader.GetDateTime(offset+4), reader.GetString(offset+5),
        reader.IsDBNull(offset+6) ? null : reader.GetInt32(offset+6), GetString(reader, offset+7), GetString(reader, offset+8),
        GetString(reader, offset+9), GetString(reader, offset+10), GetString(reader, offset+11), reader.GetInt32(offset+12),
        reader.GetDecimal(offset+13), reader.GetDecimal(offset+14), reader.GetDecimal(offset+15),
        reader.IsDBNull(offset+16) ? null : reader.GetDecimal(offset+16),
        reader.IsDBNull(offset+17) ? null : reader.GetDecimal(offset+17), reader.GetInt32(offset+18),
        reader.GetDecimal(offset+19), reader.GetDecimal(offset+20), reader.GetBoolean(offset+21), reader.GetBoolean(offset+22),
        GetString(reader, offset+23), [], []);

    private static void BindFilter(NpgsqlCommand command, RetailSaleRecordFilter filter)
    {
        command.Parameters.AddWithValue("start_date", NpgsqlDbType.Timestamp, filter.StartDate);
        command.Parameters.AddWithValue("end_exclusive", NpgsqlDbType.Timestamp, filter.EndDateExclusive);
        command.Parameters.Add(new NpgsqlParameter("start_time", NpgsqlDbType.Time) { Value = filter.StartTime ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("end_time", NpgsqlDbType.Time) { Value = filter.EndTime ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("sale_types", NpgsqlDbType.Array | NpgsqlDbType.Text, filter.SaleTypes.ToArray());
        command.Parameters.AddWithValue("include_refunds", NpgsqlDbType.Boolean, filter.IncludeRefunds);
        command.Parameters.AddWithValue("record_types", NpgsqlDbType.Array | NpgsqlDbType.Text,
            (filter.RecordTypes ?? Array.Empty<string>()).ToArray());
        command.Parameters.AddWithValue("barcodes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, filter.Barcodes.ToArray());
        command.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, filter.QrCodes.ToArray());
        command.Parameters.AddWithValue("payment_types", NpgsqlDbType.Array | NpgsqlDbType.Text, filter.PaymentTypes.ToArray());
        command.Parameters.AddWithValue("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text, filter.Statuses.ToArray());
        command.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Integer) { Value = filter.PatientRecordId ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("patient_tc", NpgsqlDbType.Varchar, filter.PatientTc?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, filter.UserId);
        command.Parameters.AddWithValue("prescription_no", NpgsqlDbType.Varchar, filter.PrescriptionNo);
        command.Parameters.Add(new NpgsqlParameter("has_prescription", NpgsqlDbType.Boolean) { Value = filter.HasPrescription ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("debt_filter", NpgsqlDbType.Integer, filter.IsDebted is null ? -1 : filter.IsDebted.Value ? 1 : 0);
        command.Parameters.AddWithValue("marked_only", NpgsqlDbType.Boolean, filter.MarkedOnly);
        command.Parameters.Add(new NpgsqlParameter("minimum_total", NpgsqlDbType.Numeric) { Value = filter.MinimumTotal ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("maximum_total", NpgsqlDbType.Numeric) { Value = filter.MaximumTotal ?? (object)DBNull.Value });
    }

    private static void ValidateFilter(RetailSaleRecordFilter filter)
    {
        if (filter.StartDate >= filter.EndDateExclusive)
            throw new ArgumentException("Start date must be earlier than end date.", nameof(filter));
        if (filter.SaleTypes.Count == 0)
            throw new ArgumentException("At least one sale type is required.", nameof(filter));
        if (!string.IsNullOrWhiteSpace(filter.PatientTc) &&
            (filter.PatientTc.Length != 11 || !filter.PatientTc.All(char.IsAsciiDigit)))
            throw new ArgumentException("Patient identity number must contain exactly 11 digits.", nameof(filter));
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit < 1 || limit > 200)
            throw new ArgumentOutOfRangeException(nameof(limit), "Result pages require a nonnegative offset and 1-200 rows.");
    }

    private static string NormalizePaymentType(string value) => value switch
    {
        "credit" => "card",
        "banktransfer" => "bank_transfer",
        "institution_secondary" => "institution",
        "prescription_patient" => "patient",
        _ => value
    };

    private static string? GetString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? GetDateTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}
