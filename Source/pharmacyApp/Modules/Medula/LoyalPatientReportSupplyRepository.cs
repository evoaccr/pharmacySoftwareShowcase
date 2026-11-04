using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Medula;

public sealed record LoyalPatientSamplePopulation(
    int TotalPatientCount,
    IReadOnlyList<LoyalPatientIdentity> Patients);

public sealed record LoyalPatientReportSupplyRunResult(
    Guid RunId,
    int TotalPatientCount,
    int SampledPatientCount,
    int EvaluatedPatientCount,
    int FailedPatientCount,
    int PatientsWithReports,
    int LoyalPatientCount,
    decimal LoyalPercentage,
    int EstimatedLoyalPatientCount,
    int Mechanism1LoyalPatientCount,
    decimal Mechanism1LoyalPercentage,
    int Mechanism1EstimatedLoyalPatientCount,
    int Mechanism2LoyalPatientCount,
    decimal Mechanism2LoyalPercentage,
    int Mechanism2EstimatedLoyalPatientCount);

public sealed class LoyalPatientReportSupplyRepository
{
    public const string BenchmarkCohortKey = "LOYALTY_BENCHMARK_V1";

    public async Task<LoyalPatientSamplePopulation> LoadBenchmarkSampleAsync(
        int sampleSize,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = DatabaseCommand.Create(MedulaLoyalPatientSchema.Sql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var cohortLock = DatabaseCommand.Create(
            "SELECT pg_advisory_xact_lock(hashtext(@cohort_key));", connection, transaction))
        {
            cohortLock.Parameters.AddWithValue("cohort_key", NpgsqlDbType.Varchar, BenchmarkCohortKey);
            await cohortLock.ExecuteNonQueryAsync(cancellationToken);
        }
        const string eligible = "LENGTH(BTRIM(COALESCE(tc_identity_no, '')))=11 AND BTRIM(tc_identity_no) ~ '^[0-9]{11}$'";
        await using var count = DatabaseCommand.Create(
            $"SELECT COUNT(*) FROM public.patient WHERE {eligible}", connection, transaction);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        var requestedSize = Math.Min(Math.Max(1, sampleSize), total);
        await using (var extend = DatabaseCommand.Create($$"""
            WITH current_size AS (
                SELECT COUNT(*)::integer AS member_count,
                       COALESCE(MAX(cohort_position), 0)::integer AS last_position
                  FROM public.loyal_patient_benchmark_cohort
                 WHERE cohort_key=@cohort_key
            ), candidates AS (
                SELECT patient.record_id,
                       ROW_NUMBER() OVER (ORDER BY random())::integer AS offset
                  FROM public.patient patient
                 WHERE {{eligible}}
                   AND NOT EXISTS (
                       SELECT 1 FROM public.loyal_patient_benchmark_cohort cohort
                        WHERE cohort.cohort_key=@cohort_key
                          AND cohort.patient_record_id=patient.record_id)
                 ORDER BY random()
                 LIMIT @requested_size
            )
            INSERT INTO public.loyal_patient_benchmark_cohort(
                cohort_key, cohort_position, patient_record_id)
            SELECT @cohort_key, current_size.last_position + candidates.offset,
                   candidates.record_id
              FROM candidates CROSS JOIN current_size
             WHERE candidates.offset <= GREATEST(0, @requested_size-current_size.member_count)
            ON CONFLICT DO NOTHING;
            """, connection, transaction))
        {
            extend.Parameters.AddWithValue("cohort_key", NpgsqlDbType.Varchar, BenchmarkCohortKey);
            extend.Parameters.AddWithValue("requested_size", NpgsqlDbType.Integer, requestedSize);
            await extend.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var sample = DatabaseCommand.Create("""
            SELECT patient.record_id, BTRIM(patient.tc_identity_no),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                            BTRIM(patient.tc_identity_no))
              FROM public.loyal_patient_benchmark_cohort cohort
              JOIN public.patient patient ON patient.record_id=cohort.patient_record_id
             WHERE cohort.cohort_key=@cohort_key
             ORDER BY cohort.cohort_position
             LIMIT @sample_size;
            """, connection, transaction);
        sample.Parameters.AddWithValue("cohort_key", NpgsqlDbType.Varchar, BenchmarkCohortKey);
        sample.Parameters.AddWithValue("sample_size", NpgsqlDbType.Integer, requestedSize);
        var patients = new List<LoyalPatientIdentity>();
        await using (var reader = await sample.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                patients.Add(new LoyalPatientIdentity(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        await transaction.CommitAsync(cancellationToken);
        return new LoyalPatientSamplePopulation(total, patients);
    }

    public async Task<IReadOnlySet<string>> LoadKnownPrescriptionNumbersAsync(
        string patientTcIdentityNo,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = DatabaseCommand.Create("""
            SELECT DISTINCT prescription_no
              FROM (
                    SELECT NULLIF(BTRIM(medula_prescription_no), '') AS prescription_no
                      FROM public.prescription
                     WHERE patient_tc_identity_no=@tc
                    UNION
                    SELECT NULLIF(BTRIM(prescription_snapshot->>'medula_prescription_no'), '')
                      FROM public.prescription_log
                     WHERE prescription_snapshot->>'patient_tc_identity_no'=@tc
              ) known
             WHERE prescription_no IS NOT NULL;
            """, connection);
        command.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, patientTcIdentityNo);
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) numbers.Add(reader.GetString(0).Trim());
        return numbers;
    }

    public async Task<LoyalPatientHybridContext> LoadHybridContextAsync(
        int patientRecordId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var state = DatabaseCommand.Create("""
            SELECT target.is_loyal,
                   target.loyalty_report_ever_qualified,
                   target.loyalty_unloyal_since,
                   COALESCE((
                       SELECT COUNT(*) FROM public.patient member
                        WHERE target.family_group_id IS NOT NULL
                          AND member.family_group_id=target.family_group_id
                          AND member.record_id<>target.record_id
                          AND member.is_loyal
                   ), 0),
                   COALESCE((
                       SELECT COUNT(*) FROM public.patient member
                        WHERE target.family_group_id IS NOT NULL
                          AND member.family_group_id=target.family_group_id
                          AND member.record_id<>target.record_id
                          AND member.loyalty_mechanism3_state=-1
                   ), 0)
              FROM public.patient target
             WHERE target.record_id=@patient_id;
            """, connection);
        state.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
        bool wasLoyal;
        bool reportQualified;
        DateTimeOffset? unloyalSince;
        int loyalFamily;
        int unloyalFamily;
        await using (var reader = await state.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Hasta kaydı bulunamadı.");
            wasLoyal = reader.GetBoolean(0);
            reportQualified = reader.GetBoolean(1);
            unloyalSince = reader.IsDBNull(2)
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
            loyalFamily = Convert.ToInt32(reader.GetInt64(3));
            unloyalFamily = Convert.ToInt32(reader.GetInt64(4));
        }

        await using var visitsCommand = DatabaseCommand.Create("""
            SELECT DISTINCT COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date)
              FROM public.prescription p
             WHERE NOT COALESCE(p.is_deleted, false)
               AND p.patient_tc_identity_no IN (
                   SELECT tc_identity_no FROM public.patient_identity WHERE patient_record_id=@patient_id
                   UNION
                   SELECT tc_identity_no FROM public.patient WHERE record_id=@patient_id
               )
               AND COALESCE(p.prescription_date, p.medula_filter_date, p.saved_at::date) IS NOT NULL
             ORDER BY 1;
            """, connection);
        visitsCommand.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientRecordId);
        var visits = new List<DateOnly>();
        await using (var reader = await visitsCommand.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                visits.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        return new LoyalPatientHybridContext(
            visits, loyalFamily, unloyalFamily, wasLoyal, reportQualified, unloyalSince);
    }

    public async Task<Guid> BeginRunAsync(
        LoyalPatientReportSupplyOptions options,
        LoyalPatientSamplePopulation population,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = DatabaseCommand.Create("""
            INSERT INTO public.loyal_patient_report_sample_run(
                run_id, configured_sample_size, actual_sample_size, threshold_ratio,
                delay_seconds, jitter_seconds, total_patient_count,
                minimum_report_evidence, hybrid_score_threshold, family_loyal_bonus,
                family_additional_loyal_bonus, family_unloyal_penalty,
                wear_off_enabled, wear_off_days, evidence_source, cohort_key)
            VALUES (@run_id, @configured_sample_size, @actual_sample_size, @threshold_ratio,
                    @delay_seconds, @jitter_seconds, @total_patient_count,
                    @minimum_report_evidence, @hybrid_score_threshold, @family_loyal_bonus,
                    @family_additional_loyal_bonus, @family_unloyal_penalty,
                    @wear_off_enabled, @wear_off_days, 'MEDICATION_HISTORY', @cohort_key);
            """, connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Uuid, runId);
        command.Parameters.AddWithValue("configured_sample_size", NpgsqlDbType.Integer, options.SampleSize);
        command.Parameters.AddWithValue("actual_sample_size", NpgsqlDbType.Integer, population.Patients.Count);
        command.Parameters.AddWithValue("threshold_ratio", NpgsqlDbType.Numeric, options.LoyaltyThresholdRatio);
        command.Parameters.AddWithValue("delay_seconds", NpgsqlDbType.Numeric, options.QueryDelaySeconds);
        command.Parameters.AddWithValue("jitter_seconds", NpgsqlDbType.Numeric, options.QueryJitterSeconds);
        command.Parameters.AddWithValue("total_patient_count", NpgsqlDbType.Integer, population.TotalPatientCount);
        command.Parameters.AddWithValue("minimum_report_evidence", NpgsqlDbType.Integer, options.MinimumReportEvidence);
        command.Parameters.AddWithValue("hybrid_score_threshold", NpgsqlDbType.Numeric, options.HybridScoreThreshold);
        command.Parameters.AddWithValue("family_loyal_bonus", NpgsqlDbType.Numeric, options.FamilyLoyalBonus);
        command.Parameters.AddWithValue("family_additional_loyal_bonus", NpgsqlDbType.Numeric, options.FamilyAdditionalLoyalBonus);
        command.Parameters.AddWithValue("family_unloyal_penalty", NpgsqlDbType.Numeric, options.FamilyUnloyalPenalty);
        command.Parameters.AddWithValue("wear_off_enabled", NpgsqlDbType.Boolean, options.WearOffEnabled);
        command.Parameters.AddWithValue("wear_off_days", NpgsqlDbType.Integer, options.WearOffDays);
        command.Parameters.AddWithValue("cohort_key", NpgsqlDbType.Varchar, BenchmarkCohortKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return runId;
    }

    public async Task RecordEvaluationAsync(
        Guid runId,
        LoyalPatientIdentity patient,
        LoyalPatientReportSupplyEvaluation? report,
        LoyalPatientHybridEvaluation? hybrid,
        LoyalPatientHybridContext? context,
        LoyalPatientReportSupplyOptions options,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var evidence = DatabaseCommand.Create("""
            INSERT INTO public.loyal_patient_report_supply_evidence(
                run_id, patient_record_id, patient_tc_identity_no, total_report_count,
                supplied_report_count, supply_ratio, is_loyal, error,
                has_report_evidence, is_report_decisive, frequency_score, report_score,
                family_score, hybrid_score, mechanism3_is_loyal,
                mechanism1_is_loyal, mechanism2_is_loyal,
                loyal_family_member_count, explicitly_unloyal_family_member_count,
                decision_reason, evidence_source)
            VALUES (@run_id, @patient_id, @tc, @total, @supplied, @ratio, @is_loyal, @error,
                    @has_report, @decisive, @frequency_score, @report_score,
                    @family_score, @hybrid_score, @mechanism3_is_loyal,
                    @mechanism1_is_loyal, @mechanism2_is_loyal,
                    @loyal_family, @unloyal_family, @reason, 'MEDICATION_HISTORY')
            ON CONFLICT (run_id, patient_record_id) DO UPDATE SET
                total_report_count=EXCLUDED.total_report_count,
                supplied_report_count=EXCLUDED.supplied_report_count,
                supply_ratio=EXCLUDED.supply_ratio, is_loyal=EXCLUDED.is_loyal,
                has_report_evidence=EXCLUDED.has_report_evidence,
                is_report_decisive=EXCLUDED.is_report_decisive,
                frequency_score=EXCLUDED.frequency_score, report_score=EXCLUDED.report_score,
                family_score=EXCLUDED.family_score, hybrid_score=EXCLUDED.hybrid_score,
                mechanism3_is_loyal=EXCLUDED.mechanism3_is_loyal,
                mechanism1_is_loyal=EXCLUDED.mechanism1_is_loyal,
                mechanism2_is_loyal=EXCLUDED.mechanism2_is_loyal,
                loyal_family_member_count=EXCLUDED.loyal_family_member_count,
                explicitly_unloyal_family_member_count=EXCLUDED.explicitly_unloyal_family_member_count,
                decision_reason=EXCLUDED.decision_reason, evidence_source='MEDICATION_HISTORY',
                evaluated_at=now(), error=EXCLUDED.error;
            """, connection, transaction))
        {
            AddNullable(evidence, "total", NpgsqlDbType.Integer, report?.TotalReports);
            AddNullable(evidence, "supplied", NpgsqlDbType.Integer, report?.SuppliedReports);
            AddNullable(evidence, "ratio", NpgsqlDbType.Numeric, report?.SupplyRatio);
            AddNullable(evidence, "is_loyal", NpgsqlDbType.Boolean, hybrid?.IsLoyal);
            AddNullable(evidence, "has_report", NpgsqlDbType.Boolean, report?.HasReportEvidence);
            AddNullable(evidence, "decisive", NpgsqlDbType.Boolean, report?.IsDecisive);
            AddNullable(evidence, "frequency_score", NpgsqlDbType.Numeric, hybrid?.FrequencyScore);
            AddNullable(evidence, "report_score", NpgsqlDbType.Numeric, hybrid?.ReportScore);
            AddNullable(evidence, "family_score", NpgsqlDbType.Numeric, hybrid?.FamilyScore);
            AddNullable(evidence, "hybrid_score", NpgsqlDbType.Numeric, hybrid?.TotalScore);
            AddNullable(evidence, "mechanism3_is_loyal", NpgsqlDbType.Boolean,
                hybrid?.IsLoyal);
            AddNullable(evidence, "mechanism1_is_loyal", NpgsqlDbType.Boolean,
                hybrid?.Mechanism1IsLoyal);
            AddNullable(evidence, "mechanism2_is_loyal", NpgsqlDbType.Boolean,
                hybrid?.Mechanism2IsLoyal);
            AddNullable(evidence, "loyal_family", NpgsqlDbType.Integer, context?.LoyalFamilyMemberCount);
            AddNullable(evidence, "unloyal_family", NpgsqlDbType.Integer, context?.ExplicitlyUnloyalFamilyMemberCount);
            evidence.Parameters.AddWithValue("run_id", NpgsqlDbType.Uuid, runId);
            evidence.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patient.RecordId);
            evidence.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, patient.TcIdentityNo);
            evidence.Parameters.AddWithValue("reason", NpgsqlDbType.Text,
                hybrid is null ? DBNull.Value : hybrid.Reason);
            evidence.Parameters.AddWithValue("error", NpgsqlDbType.Text,
                string.IsNullOrWhiteSpace(error) ? DBNull.Value : error.Trim());
            await evidence.ExecuteNonQueryAsync(cancellationToken);
        }
        if (report is not null && hybrid is not null)
        {
            var mechanism1 = hybrid.Mechanism1IsLoyal;
            var mechanism2 = hybrid.Mechanism2IsLoyal;
            var mechanism3 = hybrid.IsLoyal;
            await using var patientUpdate = DatabaseCommand.Create("""
                UPDATE public.patient
                   SET is_loyal_mechanism1=@mechanism1,
                       is_loyal_mechanism2=@mechanism2,
                       is_loyal_mechanism3=@mechanism3,
                       loyalty_mechanism3_state=CASE WHEN @raw_loyal THEN 1 ELSE -1 END,
                       loyalty_mechanism3_score=@score,
                       loyalty_mechanism3_evaluated_at=now(),
                       loyalty_report_ever_qualified=
                           loyalty_report_ever_qualified OR @report_loyal,
                       loyalty_report_evidence_count=@report_count,
                       loyalty_unloyal_since=CASE
                           WHEN @mechanism1 OR @mechanism2 OR @mechanism3 THEN NULL
                           WHEN NOT @wear_off_enabled THEN now()
                           ELSE COALESCE(loyalty_unloyal_since, now())
                       END,
                       is_loyal=CASE
                           WHEN @mechanism1 OR @mechanism2 OR @mechanism3 THEN true
                           WHEN NOT @wear_off_enabled THEN false
                           WHEN is_loyal
                            AND now() < COALESCE(loyalty_unloyal_since, now())
                                          + make_interval(days => @wear_off_days) THEN true
                           ELSE false
                       END
                 WHERE record_id=@patient_id;
                INSERT INTO public.loyal_patient_profile(patient_record_id)
                SELECT record_id FROM public.patient WHERE record_id=@patient_id AND is_loyal
                ON CONFLICT (patient_record_id) DO NOTHING;
                DELETE FROM public.loyal_patient_profile profile
                 WHERE profile.patient_record_id=@patient_id
                   AND NOT EXISTS (SELECT 1 FROM public.patient patient
                                    WHERE patient.record_id=@patient_id AND patient.is_loyal);
                """, connection, transaction);
            patientUpdate.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patient.RecordId);
            patientUpdate.Parameters.AddWithValue("mechanism1", NpgsqlDbType.Boolean, mechanism1);
            patientUpdate.Parameters.AddWithValue("mechanism2", NpgsqlDbType.Boolean, mechanism2);
            patientUpdate.Parameters.AddWithValue("mechanism3", NpgsqlDbType.Boolean, mechanism3);
            patientUpdate.Parameters.AddWithValue("raw_loyal", NpgsqlDbType.Boolean, hybrid.IsLoyal);
            patientUpdate.Parameters.AddWithValue("score", NpgsqlDbType.Numeric, hybrid.TotalScore);
            patientUpdate.Parameters.AddWithValue("report_loyal", NpgsqlDbType.Boolean, report.IsLoyal);
            patientUpdate.Parameters.AddWithValue("report_count", NpgsqlDbType.Integer, report.TotalReports);
            patientUpdate.Parameters.AddWithValue("wear_off_enabled", NpgsqlDbType.Boolean, options.WearOffEnabled);
            patientUpdate.Parameters.AddWithValue("wear_off_days", NpgsqlDbType.Integer, options.WearOffDays);
            await patientUpdate.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteRunAsync(
        LoyalPatientReportSupplyRunResult result,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = DatabaseCommand.Create("""
            UPDATE public.loyal_patient_report_sample_run SET
                completed_at=now(), evaluated_patient_count=@evaluated,
                failed_patient_count=@failed, patient_with_report_count=@with_reports,
                loyal_sample_count=@loyal, sample_loyal_ratio=@ratio,
                estimated_loyal_patient_count=@estimated, status=@status, error=@error
                ,mechanism1_loyal_count=@mechanism1_loyal
                ,mechanism1_loyal_ratio=@mechanism1_ratio
                ,mechanism1_estimated_count=@mechanism1_estimated
                ,mechanism2_loyal_count=@mechanism2_loyal
                ,mechanism2_loyal_ratio=@mechanism2_ratio
                ,mechanism2_estimated_count=@mechanism2_estimated
                ,mechanism3_loyal_count=@loyal
                ,mechanism3_loyal_ratio=@ratio
                ,mechanism3_estimated_count=@estimated
             WHERE run_id=@run_id;
            """, connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Uuid, result.RunId);
        command.Parameters.AddWithValue("evaluated", NpgsqlDbType.Integer, result.EvaluatedPatientCount);
        command.Parameters.AddWithValue("failed", NpgsqlDbType.Integer, result.FailedPatientCount);
        command.Parameters.AddWithValue("with_reports", NpgsqlDbType.Integer, result.PatientsWithReports);
        command.Parameters.AddWithValue("loyal", NpgsqlDbType.Integer, result.LoyalPatientCount);
        command.Parameters.AddWithValue("ratio", NpgsqlDbType.Numeric,
            result.EvaluatedPatientCount == 0 ? DBNull.Value : result.LoyalPercentage / 100m);
        command.Parameters.AddWithValue("estimated", NpgsqlDbType.Integer, result.EstimatedLoyalPatientCount);
        command.Parameters.AddWithValue("mechanism1_loyal", NpgsqlDbType.Integer, result.Mechanism1LoyalPatientCount);
        command.Parameters.AddWithValue("mechanism1_ratio", NpgsqlDbType.Numeric,
            result.EvaluatedPatientCount == 0 ? DBNull.Value : result.Mechanism1LoyalPercentage / 100m);
        command.Parameters.AddWithValue("mechanism1_estimated", NpgsqlDbType.Integer,
            result.Mechanism1EstimatedLoyalPatientCount);
        command.Parameters.AddWithValue("mechanism2_loyal", NpgsqlDbType.Integer, result.Mechanism2LoyalPatientCount);
        command.Parameters.AddWithValue("mechanism2_ratio", NpgsqlDbType.Numeric,
            result.EvaluatedPatientCount == 0 ? DBNull.Value : result.Mechanism2LoyalPercentage / 100m);
        command.Parameters.AddWithValue("mechanism2_estimated", NpgsqlDbType.Integer,
            result.Mechanism2EstimatedLoyalPatientCount);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : error.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullable<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        T? value) where T : struct =>
        command.Parameters.AddWithValue(name, type, value.HasValue ? value.Value : DBNull.Value);
}
