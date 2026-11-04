using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Medula;

public sealed record LoyalPatientIdentity(int RecordId, string TcIdentityNo, string DisplayName);
public sealed record LoyalPatientOpenEvaluation(
    LoyalPatientEvaluation Evaluation,
    bool WasLoyal,
    bool IsLoyal,
    int StrikeCount,
    bool Removed);
public sealed record LoyalPatientMedicationHistoryDisplayRow(
    string PrescriptionNo,
    string MedicationBarcode,
    string MedicationName,
    DateOnly? MedicationPurchaseDate,
    DateOnly? AvailableDate,
    decimal? Quantity,
    string MedicationUsage,
    string ReportDiagnosisNo);
public sealed record LoyalPatientHistoryReplacementResult(
    int MedicationCount,
    int ReportCount,
    IReadOnlyList<string> UnresolvedMedicationNames);
public sealed record LoyalPatientHistoryStatus(
    DateTime? MedicationSyncedAt,
    DateTime? ReportSyncedAt,
    string LastSyncError);
public sealed record LoyalPatientClassificationProgress(int ProcessedPrescriptions, int TotalPrescriptions);
public sealed record LoyalPatientClassificationResult(
    int PatientCount,
    int LoyalPatientCount,
    int PrescriptionCount,
    int MedulaImportedPrescriptionCount,
    int LocalPrescriptionCount);

public sealed class LoyalPatientRepository
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LoyalPatientClassificationResult> UpdateAllStatusesAsync(
        LoyalPatientClassificationOptions options,
        IProgress<LoyalPatientClassificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);

        const string countSql = """
            SELECT COUNT(*)
              FROM public.prescription p
             WHERE NOT COALESCE(p.is_deleted, false)
               AND p.patient_tc_identity_no IS NOT NULL
               AND COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date) IS NOT NULL;
            """;
        await using var count = pharmacyApp.Infrastructure.DatabaseCommand.Create(countSql, connection);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        var visits = new Dictionary<int, List<DateOnly>>();
        var processed = 0;
        var medulaImported = 0;
        const string visitsSql = """
            SELECT identity.patient_record_id,
                   COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date),
                   COALESCE(p.imported_from_medula, false)
              FROM public.prescription p
              JOIN public.patient_identity identity
                ON identity.tc_identity_no=p.patient_tc_identity_no
             WHERE NOT COALESCE(p.is_deleted, false)
               AND COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date) IS NOT NULL
             ORDER BY identity.patient_record_id,
                      COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date);
            """;
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(visitsSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var patientId = reader.GetInt32(0);
                if (!visits.TryGetValue(patientId, out var patientVisits))
                    visits[patientId] = patientVisits = [];
                patientVisits.Add(DateOnly.FromDateTime(reader.GetDateTime(1)));
                if (reader.GetBoolean(2)) medulaImported++;
                processed++;
                progress?.Report(new LoyalPatientClassificationProgress(processed, total));
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var loyalIds = visits
            .Where(pair => LoyalPatientClassifier.Evaluate(
                pair.Value, options, today).IsLoyal)
            .Select(pair => pair.Key)
            .ToArray();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var loyaltySettings = ApplicationSettingsStore.Current;
        await using (var reset = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.patient
               SET is_loyal_mechanism1=false,
                   loyalty_unloyal_since=CASE
                       WHEN is_loyal_mechanism2 OR is_loyal_mechanism3 THEN NULL
                       WHEN NOT @wear_off_enabled THEN now()
                       ELSE COALESCE(loyalty_unloyal_since, now())
                   END,
                   is_loyal=CASE
                       WHEN is_loyal_mechanism2 OR is_loyal_mechanism3 THEN true
                       WHEN NOT @wear_off_enabled THEN false
                       WHEN is_loyal
                        AND now() < COALESCE(loyalty_unloyal_since, now())
                                      + make_interval(days => @wear_off_days) THEN true
                       ELSE false
                   END
             WHERE is_loyal_mechanism1
                OR is_loyal IS DISTINCT FROM (is_loyal_mechanism2 OR is_loyal_mechanism3);
            """, connection, transaction))
        {
            reset.Parameters.AddWithValue("wear_off_enabled", NpgsqlDbType.Boolean, loyaltySettings.LoyalPatientWearOffEnabled);
            reset.Parameters.AddWithValue("wear_off_days", NpgsqlDbType.Integer, loyaltySettings.LoyalPatientWearOffDays);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
        if (loyalIds.Length > 0)
        {
            await using var mark = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.patient
                   SET is_loyal_mechanism1=true,
                       is_loyal=true,
                       loyalty_unloyal_since=NULL,
                       loyalty_unloyal_strikes=0
                 WHERE record_id=ANY(@patient_ids);
                INSERT INTO public.loyal_patient_profile(patient_record_id)
                SELECT unnest(@patient_ids)
                ON CONFLICT (patient_record_id) DO NOTHING;
                """, connection, transaction);
            mark.Parameters.AddWithValue(
                "patient_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, loyalIds);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var removeProfiles = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            DELETE FROM public.loyal_patient_profile profile
             WHERE NOT EXISTS (
                 SELECT 1 FROM public.patient patient
                  WHERE patient.record_id=profile.patient_record_id
                    AND patient.is_loyal);
            """, connection, transaction))
            await removeProfiles.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await using var patientCountCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "SELECT COUNT(*) FROM public.patient", connection);
        var patientCount = Convert.ToInt32(
            await patientCountCommand.ExecuteScalarAsync(cancellationToken));
        progress?.Report(new LoyalPatientClassificationProgress(total, total));
        return new LoyalPatientClassificationResult(
            patientCount,
            loyalIds.Length,
            total,
            medulaImported,
            total - medulaImported);
    }

    public async Task<LoyalPatientEvaluation?> EvaluatePatientAsync(
        string tcIdentityNo,
        LoyalPatientClassificationOptions options,
        CancellationToken cancellationToken) =>
        (await EvaluatePatientOnPrescriptionOpenAsync(
            tcIdentityNo, options, cancellationToken))?.Evaluation;

    public async Task<LoyalPatientOpenEvaluation?> EvaluatePatientOnPrescriptionOpenAsync(
        string tcIdentityNo,
        LoyalPatientClassificationOptions options,
        CancellationToken cancellationToken)
    {
        tcIdentityNo = tcIdentityNo.Trim();
        if (tcIdentityNo.Length != 11 || tcIdentityNo.Any(character => !char.IsDigit(character)))
            return null;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        const string sql = """
            SELECT patient.record_id,
                   COALESCE(patient.is_loyal, false),
                   COALESCE(patient.loyalty_unloyal_strikes, 0),
                   COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date)
              FROM public.patient patient
              LEFT JOIN public.patient_identity identity
                ON identity.patient_record_id=patient.record_id
              LEFT JOIN public.prescription p
                ON p.patient_tc_identity_no=identity.tc_identity_no
               AND NOT COALESCE(p.is_deleted, false)
             WHERE patient.tc_identity_no=@tc
             ORDER BY COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date);
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, tcIdentityNo);
        int? patientId = null;
        var wasLoyal = false;
        var currentStrikes = 0;
        var visits = new List<DateOnly>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                patientId ??= reader.GetInt32(0);
                wasLoyal = reader.GetBoolean(1);
                currentStrikes = reader.GetInt32(2);
                if (!reader.IsDBNull(3))
                    visits.Add(DateOnly.FromDateTime(reader.GetDateTime(3)));
            }
        }
        if (patientId is null) return null;
        var evaluation = LoyalPatientClassifier.Evaluate(
            visits, options, DateOnly.FromDateTime(DateTime.Today));
        var decision = LoyalPatientStrikePolicy.Apply(
            wasLoyal, currentStrikes, evaluation.IsLoyal);
        await ApplyOpenedPatientStatusAsync(
            connection, patientId.Value, evaluation.IsLoyal, decision, cancellationToken);
        return new LoyalPatientOpenEvaluation(
            evaluation, wasLoyal, decision.IsLoyal, decision.StrikeCount, decision.Removed);
    }

    public async Task<IReadOnlyList<LoyalPatientIdentity>> LoadLoyalPatientsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id, COALESCE(tc_identity_no, ''),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''), tc_identity_no, record_id::text)
              FROM public.patient
             WHERE is_loyal
               AND LENGTH(COALESCE(tc_identity_no, ''))=11
             ORDER BY first_name NULLS LAST, last_name NULLS LAST, record_id;
            """, connection);
        var rows = new List<LoyalPatientIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LoyalPatientIdentity(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    public async Task<LoyalPatientIdentity?> FindLoyalPatientAsync(
        string tcIdentityNo,
        CancellationToken cancellationToken)
    {
        tcIdentityNo = tcIdentityNo.Trim();
        if (tcIdentityNo.Length != 11 || tcIdentityNo.Any(character => !char.IsDigit(character)))
            return null;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id, tc_identity_no,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''),
                            tc_identity_no, record_id::text)
              FROM public.patient
             WHERE tc_identity_no=@tc AND is_loyal
             ORDER BY record_id
             LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, tcIdentityNo);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LoyalPatientIdentity(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<IReadOnlyList<LoyalPatientMedicationHistoryDisplayRow>> LoadMedicationHistoryAsync(
        int patientRecordId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT COALESCE(history.prescription_no, ''),
                   history.medication_barcode,
                   COALESCE(NULLIF(BTRIM(product.medicine_name), ''),
                            NULLIF(BTRIM(product.brand_name), ''),
                            history.medication_barcode),
                   history.medication_purchase_date,
                   history.available_date,
                   history.quantity,
                   COALESCE(history.medication_usage, ''),
                   COALESCE(history.report_diagnosis_no, '')
              FROM public.loyal_patient_medication_history history
              LEFT JOIN public.barcode product
                ON product.barcode=history.medication_barcode
             WHERE history.patient_record_id=@patient_id
             ORDER BY history.medication_purchase_date DESC NULLS LAST,
                      history.available_date DESC NULLS LAST,
                      history.record_id DESC;
            """, connection);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
        var rows = new List<LoyalPatientMedicationHistoryDisplayRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LoyalPatientMedicationHistoryDisplayRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.GetString(6),
                reader.GetString(7)));
        return rows;
    }

    public async Task<IReadOnlyList<LoyalPatientReportHistoryRow>> LoadReportHistoryAsync(
        int patientRecordId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT COALESCE(report_tracking_no, ''), COALESCE(report_no, ''),
                   start_date, end_date, COALESCE(diagnosis, '')
              FROM public.loyal_patient_report_history
             WHERE patient_record_id=@patient_id
             ORDER BY start_date DESC NULLS LAST, end_date DESC NULLS LAST, record_id DESC;
            """, connection);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
        var rows = new List<LoyalPatientReportHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LoyalPatientReportHistoryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : DateOnly.FromDateTime(reader.GetDateTime(2)),
                reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                reader.GetString(4)));
        return rows;
    }

    public async Task<LoyalPatientHistoryStatus> LoadHistoryStatusAsync(
        int patientRecordId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                         MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT medication_history_synced_at, report_history_synced_at,
                   COALESCE(last_sync_error, '')
              FROM public.loyal_patient_profile
             WHERE patient_record_id=@patient_id;
            """, connection);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LoyalPatientHistoryStatus(
                reader.IsDBNull(0) ? null : reader.GetDateTime(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.GetString(2))
            : new LoyalPatientHistoryStatus(null, null, string.Empty);
    }

    public async Task<LoyalPatientHistoryReplacementResult> ReplaceLiveHistoriesAsync(
        int patientRecordId,
        IReadOnlyCollection<LoyalPatientMedicationHistoryRow> medications,
        IReadOnlyCollection<LoyalPatientReportHistoryRow> reports,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var localCandidates = await LoadPrescriptionMedicationCandidatesAsync(
            connection, patientRecordId, cancellationToken);
        var resolvedMedications = medications
            .Select(row => row with
            {
                MedicationBarcode = LoyalPatientMedicationBarcodeResolver.Resolve(
                    row, localCandidates)
            })
            .ToArray();
        var unresolvedRows = resolvedMedications
            .Where(row => row.MedicationBarcode.Length == 0)
            .ToArray();
        if (unresolvedRows.Length > 0)
        {
            var catalogCandidates = await LoadCatalogMedicationCandidatesAsync(
                connection,
                unresolvedRows.Select(row => row.MedicationName)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                cancellationToken);
            resolvedMedications = resolvedMedications
                .Select(row => row.MedicationBarcode.Length > 0
                    ? row
                    : row with
                    {
                        MedicationBarcode = LoyalPatientMedicationBarcodeResolver.Resolve(
                            row, catalogCandidates)
                    })
                .ToArray();
        }
        var unresolvedNames = resolvedMedications
            .Where(row => row.MedicationBarcode.Length == 0)
            .Select(row => string.IsNullOrWhiteSpace(row.MedicationName)
                ? "Adı okunamayan ilaç"
                : row.MedicationName.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var persistedMedications = resolvedMedications
            .Where(row => row.MedicationBarcode.Length > 0)
            .Distinct()
            .ToArray();
        var syncError = unresolvedNames.Length == 0
            ? null
            : $"{unresolvedNames.Length:N0} ilaç barkodu yerel reçete veya ürün kataloğundan güvenli biçimde çözülemedi: {string.Join(", ", unresolvedNames.Take(5))}";

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection, transaction))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using (var profile = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.loyal_patient_profile(
                patient_record_id, medication_history_synced_at, report_history_synced_at, last_sync_error)
            SELECT record_id, now(), now(), @sync_error FROM public.patient
             WHERE record_id=@patient_id AND is_loyal
            ON CONFLICT (patient_record_id) DO UPDATE SET
                medication_history_synced_at=EXCLUDED.medication_history_synced_at,
                report_history_synced_at=EXCLUDED.report_history_synced_at,
                last_sync_error=EXCLUDED.last_sync_error;
            """, connection, transaction))
        {
            profile.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
            profile.Parameters.AddWithValue(
                "sync_error", NpgsqlDbType.Text, syncError is null ? DBNull.Value : syncError);
            if (await profile.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new InvalidOperationException("Hasta artık sadık hasta olarak işaretli değil.");
        }
        await using (var delete = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            DELETE FROM public.loyal_patient_medication_history WHERE patient_record_id=@patient_id;
            DELETE FROM public.loyal_patient_report_history WHERE patient_record_id=@patient_id;
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var row in persistedMedications)
            await InsertMedicationAsync(connection, transaction, patientRecordId, row, cancellationToken);
        foreach (var row in reports)
            await InsertReportAsync(connection, transaction, patientRecordId, row, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LoyalPatientHistoryReplacementResult(
            persistedMedications.Length, reports.Count, unresolvedNames);
    }

    private static async Task<IReadOnlyList<LoyalPatientMedicationBarcodeCandidate>>
        LoadPrescriptionMedicationCandidatesAsync(
            NpgsqlConnection connection,
            int patientRecordId,
            CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT DISTINCT source.prescription_no, source.barcode, source.medication_name,
                            source.is_final
              FROM (
                    SELECT COALESCE(p.medula_prescription_no, ''),
                           p.final_content[index][1],
                           p.final_content[index][2],
                           true
                      FROM public.patient patient
                      JOIN public.prescription p
                        ON p.patient_tc_identity_no=patient.tc_identity_no
                      CROSS JOIN LATERAL generate_subscripts(p.final_content, 1) AS index
                     WHERE patient.record_id=@patient_id
                       AND NOT COALESCE(p.is_deleted, false)
                    UNION ALL
                    SELECT COALESCE(p.medula_prescription_no, ''),
                           p.registered_content[index][1],
                           p.registered_content[index][2],
                           false
                      FROM public.patient patient
                      JOIN public.prescription p
                        ON p.patient_tc_identity_no=patient.tc_identity_no
                      CROSS JOIN LATERAL generate_subscripts(p.registered_content, 1) AS index
                     WHERE patient.record_id=@patient_id
                       AND NOT COALESCE(p.is_deleted, false)
                   ) AS source(prescription_no, barcode, medication_name, is_final)
             WHERE LENGTH(COALESCE(source.barcode, '')) BETWEEN 13 AND 14
               AND COALESCE(source.barcode, '') ~ '^[0-9]+$';
            """, connection);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
        var rows = new List<LoyalPatientMedicationBarcodeCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LoyalPatientMedicationBarcodeCandidate(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetBoolean(3)));
        return rows;
    }

    private static async Task<IReadOnlyList<LoyalPatientMedicationBarcodeCandidate>>
        LoadCatalogMedicationCandidatesAsync(
            NpgsqlConnection connection,
            IReadOnlyCollection<string> medicationNames,
            CancellationToken cancellationToken)
    {
        if (medicationNames.Count == 0) return [];
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            WITH requested(name) AS (SELECT unnest(@names::text[]))
            SELECT DISTINCT '', product.barcode,
                   COALESCE(NULLIF(BTRIM(product.medicine_name), ''),
                            NULLIF(BTRIM(product.old_name), ''),
                            NULLIF(BTRIM(product.brand_name), '')),
                   false
              FROM requested
              JOIN public.barcode product
                ON regexp_replace(upper(COALESCE(product.medicine_name, '')),
                                  '[^[:alnum:]]+', '', 'g')
                   = regexp_replace(upper(requested.name), '[^[:alnum:]]+', '', 'g')
                OR regexp_replace(upper(COALESCE(product.old_name, '')),
                                  '[^[:alnum:]]+', '', 'g')
                   = regexp_replace(upper(requested.name), '[^[:alnum:]]+', '', 'g')
                OR regexp_replace(upper(COALESCE(product.brand_name, '')),
                                  '[^[:alnum:]]+', '', 'g')
                   = regexp_replace(upper(requested.name), '[^[:alnum:]]+', '', 'g')
             WHERE LENGTH(COALESCE(product.barcode, '')) BETWEEN 13 AND 14
               AND product.barcode ~ '^[0-9]+$';
            """, connection);
        command.Parameters.AddWithValue(
            "names", NpgsqlDbType.Array | NpgsqlDbType.Text, medicationNames.ToArray());
        var rows = new List<LoyalPatientMedicationBarcodeCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LoyalPatientMedicationBarcodeCandidate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), false));
        return rows;
    }

    public async Task RecordSyncErrorAsync(
        int patientRecordId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.loyal_patient_profile(patient_record_id, last_sync_error)
            SELECT record_id, @error FROM public.patient
             WHERE record_id=@patient_id AND is_loyal
            ON CONFLICT (patient_record_id) DO UPDATE SET last_sync_error=EXCLUDED.last_sync_error;
            """, connection);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyOpenedPatientStatusAsync(
        NpgsqlConnection connection,
        int patientId,
        bool qualifiesNow,
        LoyalPatientStrikeDecision decision,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            """
            UPDATE public.patient
               SET is_loyal_mechanism1=@qualifies_now,
                   is_loyal_mechanism2=CASE WHEN @removed THEN false ELSE is_loyal_mechanism2 END,
                   is_loyal_mechanism3=CASE WHEN @removed THEN false ELSE is_loyal_mechanism3 END,
                   is_loyal=@is_loyal,
                   loyalty_unloyal_strikes=@strikes,
                   loyalty_unloyal_since=CASE
                       WHEN @qualifies_now THEN NULL
                       WHEN @removed THEN COALESCE(loyalty_unloyal_since, now())
                       ELSE loyalty_unloyal_since
                   END
             WHERE record_id=@patient_id
            """,
            connection, transaction))
        {
            update.Parameters.AddWithValue("qualifies_now", NpgsqlDbType.Boolean, qualifiesNow);
            update.Parameters.AddWithValue("removed", NpgsqlDbType.Boolean, decision.Removed);
            update.Parameters.AddWithValue("is_loyal", NpgsqlDbType.Boolean, decision.IsLoyal);
            update.Parameters.AddWithValue("strikes", NpgsqlDbType.Integer, decision.StrikeCount);
            update.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var profile = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.loyal_patient_profile(patient_record_id)
            SELECT record_id FROM public.patient WHERE record_id=@patient_id AND is_loyal
            ON CONFLICT (patient_record_id) DO NOTHING;
            DELETE FROM public.loyal_patient_profile profile
             WHERE profile.patient_record_id=@patient_id
               AND NOT EXISTS (SELECT 1 FROM public.patient patient
                                WHERE patient.record_id=@patient_id AND patient.is_loyal);
            """, connection, transaction);
        profile.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        await profile.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertMedicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int patientId,
        LoyalPatientMedicationHistoryRow row,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.loyal_patient_medication_history(
                patient_record_id, prescription_no, medication_barcode,
                medication_purchase_date, available_date, quantity,
                medication_usage, report_diagnosis_no)
            VALUES (@patient_id, NULLIF(@prescription_no, ''), @barcode,
                    @purchase_date, @available_date, @quantity,
                    NULLIF(@usage, ''), NULLIF(@report, ''));
            """, connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        command.Parameters.AddWithValue("prescription_no", NpgsqlDbType.Varchar, row.PrescriptionNo);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, row.MedicationBarcode);
        AddDate(command, "purchase_date", row.MedicationPurchaseDate);
        AddDate(command, "available_date", row.AvailableDate);
        command.Parameters.AddWithValue("quantity", NpgsqlDbType.Numeric,
            row.Quantity is decimal quantity ? quantity : DBNull.Value);
        command.Parameters.AddWithValue("usage", NpgsqlDbType.Text, row.MedicationUsage);
        command.Parameters.AddWithValue("report", NpgsqlDbType.Text, row.ReportDiagnosisNo);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int patientId,
        LoyalPatientReportHistoryRow row,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.loyal_patient_report_history(
                patient_record_id, report_tracking_no, report_no,
                start_date, end_date, diagnosis)
            VALUES (@patient_id, NULLIF(@tracking_no, ''), NULLIF(@report_no, ''),
                    @start_date, @end_date, NULLIF(@diagnosis, ''));
            """, connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        command.Parameters.AddWithValue("tracking_no", NpgsqlDbType.Varchar, row.ReportTrackingNo);
        command.Parameters.AddWithValue("report_no", NpgsqlDbType.Varchar, row.ReportNo);
        AddDate(command, "start_date", row.StartDate);
        AddDate(command, "end_date", row.EndDate);
        command.Parameters.AddWithValue("diagnosis", NpgsqlDbType.Text, row.Diagnosis);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddDate(NpgsqlCommand command, string name, DateOnly? value) =>
        command.Parameters.AddWithValue(
            name, NpgsqlDbType.Date, value is DateOnly date ? date : DBNull.Value);
}
