using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Its;

public sealed class ItsProductStatusQueryRepository : ILocalProductStatusSource
{
    private const string NormalizedQrExpression = """
        regexp_replace(
            regexp_replace(
                regexp_replace(qr_code, E'^\\]d2', '', 'i'),
                E'[()\\x1D]', '', 'g'),
            E'<GS>|\\[GS\\]', '', 'gi')
        """;

    public async Task<LocalQrStatus> QueryAsync(
        string enteredQrCode,
        CancellationToken cancellationToken)
    {
        var code = enteredQrCode.Trim();
        var normalizedCode = NormalizeQr(code);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            WITH candidates AS (
                SELECT qr_code, barcode, 'active'::text AS location, 1 AS priority
                  FROM public."qrActive"
                 WHERE qr_code=@code OR {NormalizedQrExpression}=@normalized_code
                UNION ALL
                SELECT qr_code, barcode, 'hanged', 2
                  FROM public."qrHanged"
                 WHERE qr_code=@code OR {NormalizedQrExpression}=@normalized_code
                UNION ALL
                SELECT qr_code, barcode, 'inactive', 3
                  FROM public."qrInactive"
                 WHERE qr_code=@code OR {NormalizedQrExpression}=@normalized_code
                UNION ALL
                SELECT qr_code, COALESCE(public.pharmacy_qr_barcode(qr_code), ''), 'history', 4
                  FROM public."qrMovements"
                 WHERE qr_code=@code OR {NormalizedQrExpression}=@normalized_code
                UNION ALL
                SELECT qr_code, barcode, 'history', 5
                  FROM public."takasQrItems"
                 WHERE qr_code=@code OR {NormalizedQrExpression}=@normalized_code
                UNION ALL
                SELECT qr_code, barcode, 'history', 6
                  FROM public."salesQrItems"
                 WHERE qr_code=@code OR {NormalizedQrExpression}=@normalized_code
            ), matched AS (
                SELECT qr_code, barcode, location
                  FROM candidates
                 ORDER BY CASE WHEN qr_code=@code THEN 0 ELSE 1 END, priority
                 LIMIT 1
            ), latest_movement AS (
                SELECT movement.record_id, type.code, type.display_name,
                       movement.movement_date, COALESCE(movement.movement_address, '') AS movement_address,
                       COALESCE(movement.reference_code, '') AS reference_code
                  FROM public."qrMovements" movement
                  JOIN public."qrMovementTypes" type ON type.record_id=movement.movement_type
                  JOIN matched ON movement.qr_code=matched.qr_code
                 ORDER BY movement.record_id DESC
                 LIMIT 1
            ), latest_takas AS (
                SELECT transfer.direction, transfer.status, transfer.destination_gln,
                       COALESCE(NULLIF(destination.pharmacy_name, ''),
                                NULLIF(partner.pharmacy_name, ''), transfer.destination_gln) AS destination_name
                  FROM public."takasQrItems" item
                  JOIN public.takas transfer ON transfer.record_id=item.takas_id
                  JOIN matched ON item.qr_code=matched.qr_code
                  LEFT JOIN public.pharmacy destination ON destination.gln_code=transfer.destination_gln
                  LEFT JOIN public.pharmacy partner ON partner.record_id=transfer.pharmacy_id
                 ORDER BY item.record_id DESC
                 LIMIT 1
            ), latest_sale AS (
                SELECT sale.record_id AS sale_id, sale.patient_id,
                       NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), '') AS patient_name
                  FROM public."salesQrItems" item
                  JOIN public.sales sale ON sale.record_id=item.sale_id
                  JOIN matched ON item.qr_code=matched.qr_code
                  LEFT JOIN public.patient patient ON patient.record_id=sale.patient_id
                 ORDER BY item.record_id DESC
                 LIMIT 1
            )
            SELECT matched.qr_code,
                   matched.barcode,
                   COALESCE(NULLIF(product.medicine_name, ''), NULLIF(product.brand_name, ''),
                            NULLIF(product.old_name, ''), matched.barcode, 'Ürün bilgisi yok'),
                   matched.location,
                   COALESCE(latest_movement.code, ''),
                   COALESCE(latest_movement.display_name, ''),
                   latest_movement.movement_date,
                   COALESCE(latest_movement.movement_address, ''),
                   COALESCE(latest_movement.reference_code, ''),
                   latest_takas.direction,
                   latest_takas.status,
                   latest_takas.destination_gln,
                   latest_takas.destination_name,
                   latest_sale.sale_id,
                   latest_sale.patient_id,
                   latest_sale.patient_name
              FROM matched
              LEFT JOIN public.barcode product ON product.barcode=matched.barcode
              LEFT JOIN latest_movement ON TRUE
              LEFT JOIN latest_takas ON TRUE
              LEFT JOIN latest_sale ON TRUE
            """;

        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("normalized_code", NpgsqlDbType.Varchar, normalizedCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return LocalQrStatusFormatter.NotFound();

        var location = reader.GetString(3) switch
        {
            "active" => LocalQrLocation.Active,
            "hanged" => LocalQrLocation.Hanged,
            "inactive" => LocalQrLocation.Inactive,
            _ => LocalQrLocation.HistoryOnly
        };
        var movementCode = GetString(reader, 4);
        return new LocalQrStatus(
            true,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            location,
            LocalQrStatusFormatter.Format(location, movementCode),
            movementCode,
            GetString(reader, 5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            GetString(reader, 7),
            GetString(reader, 8),
            GetNullableString(reader, 9),
            GetNullableString(reader, 10),
            GetNullableString(reader, 11),
            GetNullableString(reader, 12),
            reader.IsDBNull(13) ? null : reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14),
            GetNullableString(reader, 15));
    }

    private static string NormalizeQr(string code) => code
        .Replace("]d2", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("(", string.Empty, StringComparison.Ordinal)
        .Replace(")", string.Empty, StringComparison.Ordinal)
        .Replace(((char)29).ToString(), string.Empty, StringComparison.Ordinal)
        .Replace("<GS>", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("[GS]", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string GetString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
