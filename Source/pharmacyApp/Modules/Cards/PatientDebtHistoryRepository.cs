using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Cards;

public sealed record PatientDebtHistoryRow(
    long LogId,
    DateTime Timestamp,
    string PatientName,
    string TcIdentityNo,
    string Direction,
    decimal Amount,
    decimal BalanceAfter,
    string ReferenceCode,
    string Description,
    string UserName);

public sealed record PatientDebtHistoryPage(IReadOnlyList<PatientDebtHistoryRow> Rows, int TotalCount);

public sealed class PatientDebtHistoryRepository
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<IReadOnlyList<CardLookupOption>> SearchPatientsAsync(string term, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT patient.record_id::text,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ',patient.first_name,patient.last_name)),''),patient.record_id::text)
                       || ' — ' || COALESCE(patient.tc_identity_no,'T.C. yok') AS display_name
            FROM public.patient patient
            WHERE CONCAT_WS(' ',patient.first_name,patient.last_name) ILIKE '%' || @term || '%'
               OR patient.tc_identity_no ILIKE @term || '%'
            ORDER BY patient.first_name NULLS LAST, patient.last_name NULLS LAST, patient.record_id
            LIMIT 25
            """;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("term", NpgsqlDbType.Varchar, term.Trim());
        var results = new List<CardLookupOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new CardLookupOption(reader.GetString(0), ToTitleCase(reader.GetString(1))));
        return results;
    }

    public async Task<CardLookupOption?> LoadPatientAsync(int patientId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT patient.record_id::text,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ',patient.first_name,patient.last_name)),''),patient.record_id::text)
                       || ' — ' || COALESCE(patient.tc_identity_no,'T.C. yok') AS display_name
            FROM public.patient patient WHERE patient.record_id=@patient_id
            """;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CardLookupOption(reader.GetString(0), ToTitleCase(reader.GetString(1)))
            : null;
    }

    public async Task<PatientDebtHistoryPage> LoadPageAsync(
        int? patientId,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(limit));
        const string sql = """
            WITH history AS (
                SELECT debt.log_id,
                       debt."timestamp",
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ',patient.first_name,patient.last_name)),''),patient.record_id::text) AS patient_name,
                       COALESCE(patient.tc_identity_no,'') AS tc_identity_no,
                       debt.debt_direction,
                       debt.debt_change,
                       SUM(CASE debt.debt_direction WHEN '+' THEN debt.debt_change ELSE -debt.debt_change END)
                           OVER (PARTITION BY debt.patient_record_id ORDER BY debt."timestamp",debt.log_id) AS balance_after,
                       debt.reference_code,
                       '' AS description,
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ',app_user.first_name,app_user.last_name)),''),debt.user_id) AS user_name
                FROM public.patient_debt_log debt
                JOIN public.patient patient ON patient.record_id=debt.patient_record_id
                LEFT JOIN public.users app_user ON app_user.user_id=debt.user_id
                WHERE @patient_id IS NULL OR debt.patient_record_id=@patient_id
            )
            SELECT history.*, COUNT(*) OVER() AS total_count
            FROM history
            ORDER BY history."timestamp" DESC, history.log_id DESC
            LIMIT @limit OFFSET @offset
            """;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = DatabaseCommand.Create(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Integer) { Value = patientId ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, offset);
        var rows = new List<PatientDebtHistoryRow>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PatientDebtHistoryRow(
                reader.GetInt64(0),
                reader.GetDateTime(1).ToLocalTime(),
                ToTitleCase(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetString(7),
                reader.GetString(8),
                ToTitleCase(reader.GetString(9))));
            totalCount = reader.GetInt32(10);
        }
        return new PatientDebtHistoryPage(rows, totalCount);
    }

    private static string ToTitleCase(string value) => TurkishCulture.TextInfo.ToTitleCase(value.ToLower(TurkishCulture));
}
