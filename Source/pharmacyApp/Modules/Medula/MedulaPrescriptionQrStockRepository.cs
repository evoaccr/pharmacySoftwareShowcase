using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Medula;

public sealed record MedulaQrStockValidation(
    IReadOnlyList<string> MissingCodes,
    IReadOnlyList<string> ReservedByAnotherPrescription)
{
    public bool IsValid => MissingCodes.Count == 0 && ReservedByAnotherPrescription.Count == 0;
}

public sealed class MedulaPrescriptionQrStockRepository
{
    private const string EnsureSchemaSql = """
        ALTER TABLE public."qrActive"
            ADD COLUMN IF NOT EXISTS reserved_prescription_uid uuid,
            ADD COLUMN IF NOT EXISTS reserved_at timestamptz;
        CREATE INDEX IF NOT EXISTS ix_qrActive_reserved_prescription
            ON public."qrActive" (reserved_prescription_uid)
            WHERE reserved_prescription_uid IS NOT NULL;
        """;

    public async Task<MedulaQrStockValidation> ValidateAsync(
        IEnumerable<string> enteredCodes,
        Guid prescriptionUid,
        CancellationToken cancellationToken)
    {
        var codes = NormalizeCodes(enteredCodes);
        if (codes.Length == 0) return new MedulaQrStockValidation([], []);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT qr_code, reserved_prescription_uid
              FROM public."qrActive"
             WHERE regexp_replace(regexp_replace(qr_code, E'^\\]d2', ''), E'[()\\x1D\\s]', '', 'g') = ANY(@codes);
            """, connection);
        command.Parameters.AddWithValue("codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, codes);
        var found = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            found[RetailQrCodeNormalizer.Normalize(reader.GetString(0))] =
                reader.IsDBNull(1) ? null : reader.GetGuid(1);
        var missing = codes.Where(code => !found.ContainsKey(code)).ToArray();
        var reserved = codes.Where(code => found.TryGetValue(code, out var owner)
            && owner is not null && owner != prescriptionUid).ToArray();
        return new MedulaQrStockValidation(missing, reserved);
    }

    public async Task SynchronizeAsync(
        Guid prescriptionUid,
        IEnumerable<string> enteredCodes,
        CancellationToken cancellationToken)
    {
        var codes = NormalizeCodes(enteredCodes);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection, transaction))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using (var release = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public."qrActive"
               SET status=NULL, reserved_prescription_uid=NULL, reserved_at=NULL
             WHERE reserved_prescription_uid=@prescription_uid
               AND NOT (regexp_replace(regexp_replace(qr_code, E'^\\]d2', ''), E'[()\\x1D\\s]', '', 'g') = ANY(@codes));
            """, connection, transaction))
        {
            release.Parameters.AddWithValue("prescription_uid", NpgsqlDbType.Uuid, prescriptionUid);
            release.Parameters.AddWithValue("codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, codes);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }
        if (codes.Length > 0)
        {
            await using var reserve = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public."qrActive"
                   SET status='reçetede', reserved_prescription_uid=@prescription_uid, reserved_at=now()
                 WHERE regexp_replace(regexp_replace(qr_code, E'^\\]d2', ''), E'[()\\x1D\\s]', '', 'g') = ANY(@codes)
                   AND (reserved_prescription_uid IS NULL OR reserved_prescription_uid=@prescription_uid);
                """, connection, transaction);
            reserve.Parameters.AddWithValue("prescription_uid", NpgsqlDbType.Uuid, prescriptionUid);
            reserve.Parameters.AddWithValue("codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, codes);
            await reserve.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string[] NormalizeCodes(IEnumerable<string> values) => values
        .Select(RetailQrCodeNormalizer.Normalize)
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
