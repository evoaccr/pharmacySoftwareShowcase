using System.IO;
using Npgsql;
using NpgsqlTypes;

namespace pharmacyApp.Modules.Invoices;

public static class InvoiceStockSchema
{
    private static readonly string[] ResourceNames =
    [
        "pharmacyApp.Database.051_create_invoice_stock_tracking.sql",
        "pharmacyApp.Database.052_compact_invoice_stock_logs.sql",
        "pharmacyApp.Database.054_normalize_invoice_stock_actions.sql",
        "pharmacyApp.Database.055_add_partial_invoice_refunds.sql"
    ];
    private static readonly Lazy<string> SchemaSql = new(ReadSchemaSql);
    private static readonly SemaphoreSlim EnsureLock = new(1, 1);
    private static readonly HashSet<string> EnsuredDatabases = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        var key = $"{settings.Host}:{settings.Port}/{settings.Database}/{settings.Username}";
        await EnsureLock.WaitAsync(cancellationToken);
        try
        {
            if (EnsuredDatabases.Contains(key)) return;
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(SchemaSql.Value, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            EnsuredDatabases.Add(key);
        }
        finally
        {
            EnsureLock.Release();
        }
    }

    public static async Task PrepareInvoiceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string invoiceUuid,
        CancellationToken cancellationToken = default)
    {
        var sourceLines = new List<SourceLine>();
        await using (var read = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id, quantity, raw_data::text
            FROM public.invoice_lines
            WHERE invoice_uuid = @invoice_uuid
            ORDER BY record_id
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sourceLines.Add(new SourceLine(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                    reader.GetString(2)));
            }
        }

        foreach (var sourceLine in sourceLines)
        {
            var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sourceLine.RawData)
                         ?? new Dictionary<string, string>();
            var parsed = InvoiceTraceabilityParser.Parse(values);
            foreach (var code in parsed.Codes)
                await InsertCodeAsync(connection, transaction, sourceLine.RecordId, code, cancellationToken);

            var storedQrCount = await CountQrCodesAsync(
                connection, transaction, sourceLine.RecordId, cancellationToken);
            var warning = InvoiceTraceabilityParser.BuildTrackingWarning(
                sourceLine.Quantity, parsed, storedQrCount);
            await using var updateWarning = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.invoice_lines
                SET tracking_warning = @tracking_warning
                WHERE record_id = @record_id
                """, connection, transaction);
            updateWarning.Parameters.AddWithValue("tracking_warning", NpgsqlDbType.Text, warning);
            updateWarning.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, sourceLine.RecordId);
            await updateWarning.ExecuteNonQueryAsync(cancellationToken);
        }

        await ClassifyInvoiceLinesAsync(connection, transaction, invoiceUuid, cancellationToken);
    }

    private static async Task<int> CountQrCodesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int invoiceLineId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT count(*)::integer
            FROM public.invoice_line_qr_codes
            WHERE invoice_line_id = @invoice_line_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("invoice_line_id", NpgsqlDbType.Integer, invoiceLineId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ClassifyInvoiceLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string invoiceUuid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH resolved AS (
                SELECT
                    line.record_id,
                    EXISTS (
                        SELECT 1 FROM public.invoice_line_qr_codes code
                        WHERE code.invoice_line_id = line.record_id) AS has_qr,
                    line.raw_data::text ~* '[(](GTIN|01)[)]|GTIN[[:space:]]*[:=]' AS has_trace_markers,
                    CASE
                        WHEN btrim(line.barcode) ~ '^[0-9]{8,14}$'
                            THEN CASE
                                WHEN length(btrim(line.barcode)) = 14 AND left(btrim(line.barcode), 1) = '0'
                                    THEN substring(btrim(line.barcode) from 2)
                                ELSE btrim(line.barcode)
                            END
                        ELSE NULL
                    END AS invoice_barcode,
                    line.raw_data::text ~* 'DEPOCU[[:space:]]+FIYAT|ECZ[.]?[[:space:]]*ISK|PERAKENDE[[:space:]]+SATIS[[:space:]]+FIYAT' AS has_pharmacy_pricing,
                    direct_match.barcode AS direct_barcode,
                    name_match.barcode AS unique_name_barcode,
                    COALESCE(direct_match.requires_qr, name_match.requires_qr, FALSE) AS catalog_requires_qr
                FROM public.invoice_lines line
                LEFT JOIN LATERAL (
                    SELECT
                        product.barcode,
                        COALESCE(NULLIF(btrim(product.product_type_name), ''),
                                 NULLIF(btrim(product.atc_code), ''),
                                 NULLIF(btrim(product.sgk_code), '')) IS NOT NULL AS requires_qr
                    FROM public.barcode product
                    WHERE product.barcode IN (
                        NULLIF(btrim(line.barcode), ''),
                        NULLIF(btrim(line.item_code), ''),
                        NULLIF(trim(leading '0' from btrim(line.barcode)), ''),
                        NULLIF(trim(leading '0' from btrim(line.item_code)), ''))
                    ORDER BY CASE WHEN product.barcode = btrim(line.barcode) THEN 0 ELSE 1 END
                    LIMIT 1
                ) direct_match ON TRUE
                LEFT JOIN LATERAL (
                    SELECT min(candidate.barcode) AS barcode,
                           bool_or(candidate.requires_qr) AS requires_qr
                    FROM (
                        SELECT DISTINCT product.barcode,
                            COALESCE(NULLIF(btrim(product.product_type_name), ''),
                                     NULLIF(btrim(product.atc_code), ''),
                                     NULLIF(btrim(product.sgk_code), '')) IS NOT NULL AS requires_qr
                        FROM public.barcode product
                        WHERE regexp_replace(upper(btrim(line.item_name)), '[[:space:]]+', ' ', 'g') <> ''
                          AND regexp_replace(upper(btrim(line.item_name)), '[[:space:]]+', ' ', 'g') IN (
                              regexp_replace(upper(btrim(COALESCE(product.medicine_name, ''))), '[[:space:]]+', ' ', 'g'),
                              regexp_replace(upper(btrim(COALESCE(product.old_name, ''))), '[[:space:]]+', ' ', 'g'),
                              regexp_replace(upper(btrim(COALESCE(product.brand_name, ''))), '[[:space:]]+', ' ', 'g'))
                    ) candidate
                    HAVING count(DISTINCT candidate.barcode) = 1
                ) name_match ON direct_match.barcode IS NULL
                             AND btrim(line.barcode) !~ '^[0-9]{8,14}$'
                WHERE line.invoice_uuid = @invoice_uuid
            )
            UPDATE public.invoice_lines line
                SET stock_tracking_type = CASE
                    WHEN resolved.has_qr OR resolved.has_trace_markers THEN 'QR'
                    WHEN resolved.catalog_requires_qr THEN 'QR'
                    WHEN resolved.invoice_barcode IS NOT NULL AND resolved.has_pharmacy_pricing THEN 'BARCODE'
                    WHEN COALESCE(resolved.direct_barcode, resolved.unique_name_barcode) IS NOT NULL THEN 'BARCODE'
                    ELSE 'NONE'
                END,
                barcode = CASE
                    WHEN resolved.has_qr THEN COALESCE(
                        (SELECT code.barcode FROM public.invoice_line_qr_codes code
                         WHERE code.invoice_line_id = line.record_id ORDER BY code.record_id LIMIT 1),
                        line.barcode)
                    ELSE COALESCE(resolved.invoice_barcode, resolved.direct_barcode,
                                  resolved.unique_name_barcode, line.barcode)
                END,
                is_medication = CASE
                    WHEN resolved.has_qr OR resolved.has_trace_markers THEN TRUE
                    WHEN resolved.catalog_requires_qr THEN TRUE
                    WHEN resolved.has_pharmacy_pricing
                        THEN TRUE
                    ELSE FALSE
                END,
                tracking_warning = CASE
                    WHEN NOT resolved.has_qr AND NOT resolved.has_trace_markers AND resolved.catalog_requires_qr
                        THEN 'İlaç için XML dosyasında tekil karekod bulunamadı.'
                    ELSE line.tracking_warning
                END
            FROM resolved
            WHERE line.record_id = resolved.record_id;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("invoice_uuid", NpgsqlDbType.Text, invoiceUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int invoiceLineId,
        InvoiceTraceabilityCode code,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.invoice_line_qr_codes (
                invoice_line_id, qr_code, gtin, barcode, serial_number,
                batch_number, expiry_date, raw_code)
            VALUES (
                @invoice_line_id, @qr_code, @gtin, @barcode, @serial_number,
                @batch_number, @expiry_date, @raw_code)
            ON CONFLICT (invoice_line_id, qr_code) DO NOTHING;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("invoice_line_id", NpgsqlDbType.Integer, invoiceLineId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Text, code.QrCode);
        command.Parameters.AddWithValue("gtin", NpgsqlDbType.Text, code.Gtin);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Text, code.Barcode);
        command.Parameters.AddWithValue("serial_number", NpgsqlDbType.Text, code.SerialNumber);
        command.Parameters.AddWithValue("batch_number", NpgsqlDbType.Text, code.BatchNumber);
        command.Parameters.AddWithValue("expiry_date", NpgsqlDbType.Date, code.ExpiryDate);
        command.Parameters.AddWithValue("raw_code", NpgsqlDbType.Text, code.RawCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadSchemaSql()
    {
        return string.Join(Environment.NewLine, ResourceNames.Select(resourceName =>
        {
            using var stream = typeof(InvoiceStockSchema).Assembly.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidOperationException(
                                   $"Invoice stock schema resource was not found: {resourceName}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }

    private sealed record SourceLine(int RecordId, decimal? Quantity, string RawData);
}
