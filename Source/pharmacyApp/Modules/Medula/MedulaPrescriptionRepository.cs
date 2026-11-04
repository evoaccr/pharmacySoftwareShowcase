using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Medula;

public sealed class MedulaPrescriptionRepository
{
    private static string EnsureSchemaSql => MedulaPrescriptionSchema.Sql;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateMedulaListPositionsAsync(
        IReadOnlyCollection<(Guid PrescriptionUid, int PageNumber, int RowIndex)> positions,
        CancellationToken cancellationToken = default)
    {
        var rows = positions
            .Where(position => position.PageNumber > 0 && position.RowIndex >= 0)
            .GroupBy(position => position.PrescriptionUid)
            .Select(group => group.Last())
            .ToArray();
        if (rows.Length == 0) return;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.prescription AS p
               SET medula_page_number = position.page_number,
                   medula_row_index = position.row_index
              FROM unnest(@uids, @pages, @rows)
                   AS position(prescription_uid, page_number, row_index)
             WHERE p.prescription_uid = position.prescription_uid;
            """, connection);
        command.Parameters.AddWithValue("uids", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            rows.Select(row => row.PrescriptionUid).ToArray());
        command.Parameters.AddWithValue("pages", NpgsqlDbType.Array | NpgsqlDbType.Integer,
            rows.Select(row => row.PageNumber).ToArray());
        command.Parameters.AddWithValue("rows", NpgsqlDbType.Array | NpgsqlDbType.Integer,
            rows.Select(row => row.RowIndex).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<MedulaPrescriptionInsertResult> InsertVersionAsync(
        MedulaPrescriptionPersistenceRecord record,
        CancellationToken cancellationToken = default) =>
        InsertVersionCoreAsync(record, onlyIfChanged: false, cancellationToken);

    public Task<MedulaPrescriptionInsertResult> InsertVersionIfChangedAsync(
        MedulaPrescriptionPersistenceRecord record,
        CancellationToken cancellationToken = default) =>
        InsertVersionCoreAsync(record, onlyIfChanged: true, cancellationToken);

    private async Task<MedulaPrescriptionInsertResult> InsertVersionCoreAsync(
        MedulaPrescriptionPersistenceRecord record,
        bool onlyIfChanged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection, transaction))
            await schema.ExecuteNonQueryAsync(cancellationToken);

        // Reçete Listesi can expose previously unseen Medula patients and doctors.
        // Create their directory rows in the same transaction before inserting the
        // prescription version so the history and its references remain consistent.
        await EnsurePatientReferencesAsync(connection, transaction, record, cancellationToken);
        var doctorBranchId = await EnsureDoctorReferenceAsync(connection, transaction, record, cancellationToken);
        await EnsureDiagnosisReferencesAsync(connection, transaction, record.Icd10Diagnoses, cancellationToken);

        var uid = record.PrescriptionUid
            ?? await FindUidAsync(connection, transaction, record, cancellationToken)
            ?? Guid.NewGuid();
        var currentState = await LockCurrentPrescriptionAsync(
            connection, transaction, uid, cancellationToken);
        if (!MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(record.MedulaPrescriptionNo))
        {
            if (MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber(
                    currentState?.MedulaPrescriptionNumber))
            {
                var preservedNumber = currentState!.MedulaPrescriptionNumber;
                record = record with
                {
                    MedulaPrescriptionNo = preservedNumber,
                    IdentityFingerprint = BuildIdentityFingerprint(
                        preservedNumber,
                        record.PatientTcIdentityNo,
                        record.MedulaFilterDate,
                        record.MedulaTypeValue,
                        record.ProtocolNo)
                };
            }
            else
                throw new InvalidOperationException(
                    "Medula reçete numarası okunmadan yerel reçete kaydı oluşturulamaz.");
        }
        var stateFingerprint = BuildStateFingerprint(record);
        if (onlyIfChanged
            && currentState is not null
            && !currentState.IsDeleted
            && string.Equals(currentState.StateFingerprint, stateFingerprint, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new MedulaPrescriptionInsertResult(
                currentState.RecordId, uid, currentState.VersionNumber, WasWritten: false);
        }
        var version = await NextVersionAsync(connection, transaction, uid, cancellationToken);
        const string sql = """
            INSERT INTO public.prescription (
                prescription_uid, version_number, medula_filter_date,
                imported_from_medula, identity_fingerprint, invoice_type_code,
                list_status_code, list_barcode_status, list_medical_report, list_document_status,
                medula_page_number, medula_row_index, state_fingerprint,
                e_prescription_no, medula_prescription_no, qr_status_code, prescription_type_code,
                prescription_subtype_code, provision_type_code, protocol_no, facility_code,
                prescription_date, medication_purchase_date, has_qr_codes, is_sequential_distribution,
                patient_tc_identity_no, patient_type, patient_insurance_type, patient_coverage_type,
                recipient_tc_identity_no, recipient_relationship, doctor_diploma_registration_no,
                doctor_branch_id, doctor_certificate,
                registered_content, final_content, icd10_diagnoses, prescription_note,
                prescription_copayment_cash, prescription_copayment_salary, examination_copayment_cash,
                examination_copayment_salary, medication_copayment, equivalent_price_difference,
                institution_total_payment, patient_total_payment, prescription_total, qr_codes,
                saved_by_user_id, saved_at, is_deleted)
            VALUES (
                @uid, @version, @filter_date,
                @imported, @fingerprint, @type_value,
                @list_status, @list_barcode, @list_report, @list_document,
                @medula_page, @medula_row, @state_fingerprint,
                @e_no, @medula_no, @qr_status, @type, @subtype, @provision, @protocol,
                @facility, @prescription_date, @purchase_date, @has_qr, @sequential, @patient_tc,
                @patient_type, @insurance, @coverage, @recipient_tc, @relationship,
                @doctor_registration, @doctor_branch_id,
                @doctor_certificate, @registered, @final, @diagnoses, @note, @rx_cash, @rx_salary, @exam_cash,
                @exam_salary, @drug_copayment, @difference, @institution, @patient_payment,
                @total, @qr_codes, @user_id, now(), false)
            ON CONFLICT (prescription_uid) DO UPDATE SET
                version_number = EXCLUDED.version_number,
                medula_filter_date = COALESCE(EXCLUDED.medula_filter_date, prescription.medula_filter_date),
                imported_from_medula = EXCLUDED.imported_from_medula,
                identity_fingerprint = EXCLUDED.identity_fingerprint,
                invoice_type_code = EXCLUDED.invoice_type_code,
                list_status_code = EXCLUDED.list_status_code,
                list_barcode_status = EXCLUDED.list_barcode_status,
                list_medical_report = EXCLUDED.list_medical_report,
                list_document_status = EXCLUDED.list_document_status,
                medula_page_number = COALESCE(NULLIF(EXCLUDED.medula_page_number, 0), prescription.medula_page_number),
                medula_row_index = CASE
                    WHEN EXCLUDED.medula_page_number > 0 THEN EXCLUDED.medula_row_index
                    ELSE prescription.medula_row_index
                END,
                state_fingerprint = EXCLUDED.state_fingerprint,
                e_prescription_no = EXCLUDED.e_prescription_no,
                medula_prescription_no = COALESCE(
                    NULLIF(BTRIM(EXCLUDED.medula_prescription_no), ''),
                    prescription.medula_prescription_no),
                qr_status_code = EXCLUDED.qr_status_code,
                prescription_type_code = EXCLUDED.prescription_type_code,
                prescription_subtype_code = EXCLUDED.prescription_subtype_code,
                provision_type_code = EXCLUDED.provision_type_code,
                protocol_no = EXCLUDED.protocol_no,
                facility_code = EXCLUDED.facility_code,
                prescription_date = EXCLUDED.prescription_date,
                medication_purchase_date = EXCLUDED.medication_purchase_date,
                has_qr_codes = EXCLUDED.has_qr_codes,
                is_sequential_distribution = EXCLUDED.is_sequential_distribution,
                patient_tc_identity_no = EXCLUDED.patient_tc_identity_no,
                patient_type = EXCLUDED.patient_type,
                patient_insurance_type = EXCLUDED.patient_insurance_type,
                patient_coverage_type = EXCLUDED.patient_coverage_type,
                recipient_tc_identity_no = EXCLUDED.recipient_tc_identity_no,
                recipient_relationship = EXCLUDED.recipient_relationship,
                doctor_diploma_registration_no = EXCLUDED.doctor_diploma_registration_no,
                doctor_branch_id = EXCLUDED.doctor_branch_id,
                doctor_certificate = EXCLUDED.doctor_certificate,
                registered_content = EXCLUDED.registered_content,
                final_content = EXCLUDED.final_content,
                icd10_diagnoses = EXCLUDED.icd10_diagnoses,
                prescription_note = EXCLUDED.prescription_note,
                prescription_copayment_cash = EXCLUDED.prescription_copayment_cash,
                prescription_copayment_salary = EXCLUDED.prescription_copayment_salary,
                examination_copayment_cash = EXCLUDED.examination_copayment_cash,
                examination_copayment_salary = EXCLUDED.examination_copayment_salary,
                medication_copayment = EXCLUDED.medication_copayment,
                equivalent_price_difference = EXCLUDED.equivalent_price_difference,
                institution_total_payment = EXCLUDED.institution_total_payment,
                patient_total_payment = EXCLUDED.patient_total_payment,
                prescription_total = EXCLUDED.prescription_total,
                qr_codes = EXCLUDED.qr_codes,
                saved_by_user_id = EXCLUDED.saved_by_user_id,
                saved_at = EXCLUDED.saved_at,
                is_deleted = false
            RETURNING record_id;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, uid);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
        AddDate(command, "filter_date", record.MedulaFilterDate);
        command.Parameters.AddWithValue("imported", NpgsqlDbType.Boolean, record.ImportedFromMedula);
        AddText(command, "fingerprint", record.IdentityFingerprint);
        AddSmallInt(command, "type_value", MedulaPrescriptionCodebook.InvoiceTypeCode(
            record.InvoiceType, record.MedulaTypeValue));
        AddText(command, "e_no", record.EPrescriptionNo);
        AddText(command, "list_status", record.ListStatusCode); AddText(command, "list_barcode", record.ListBarcodeStatus);
        AddText(command, "list_report", record.ListMedicalReport); AddText(command, "list_document", record.ListDocumentStatus);
        command.Parameters.AddWithValue("medula_page", NpgsqlDbType.Integer, record.MedulaPageNumber);
        command.Parameters.AddWithValue("medula_row", NpgsqlDbType.Integer, record.MedulaRowIndex);
        command.Parameters.AddWithValue("state_fingerprint", NpgsqlDbType.Varchar, stateFingerprint);
        AddText(command, "medula_no", record.MedulaPrescriptionNo);
        AddSmallInt(command, "qr_status", MedulaPrescriptionCodebook.QrStatusCode(
            record.MedulaQrStatus, record.ItsFinalized, hasQrCodes: record.HasQrCodes));
        AddSmallInt(command, "type", MedulaPrescriptionCodebook.PrescriptionTypeCode(record.PrescriptionType));
        AddSmallInt(command, "subtype", MedulaPrescriptionCodebook.PrescriptionSubtypeCode(record.PrescriptionSubtype));
        AddSmallInt(command, "provision", MedulaPrescriptionCodebook.ProvisionTypeCode(record.ProvisionType));
        AddText(command, "protocol", record.ProtocolNo); AddText(command, "facility", record.FacilityCode);
        AddDate(command, "prescription_date", record.PrescriptionDate); AddDate(command, "purchase_date", record.MedicationPurchaseDate);
        command.Parameters.AddWithValue("has_qr", NpgsqlDbType.Boolean, record.HasQrCodes);
        command.Parameters.AddWithValue("sequential", NpgsqlDbType.Boolean, record.IsSequentialDistribution);
        AddText(command, "patient_tc", record.PatientTcIdentityNo); AddText(command, "patient_type", record.PatientType);
        AddText(command, "insurance", record.PatientInsuranceType); AddText(command, "coverage", record.PatientCoverageType);
        AddText(command, "recipient_tc", record.RecipientTcIdentityNo); AddText(command, "relationship", record.RecipientRelationship);
        AddText(command, "doctor_registration", record.DoctorDiplomaRegistrationNo);
        command.Parameters.AddWithValue("doctor_branch_id", NpgsqlDbType.Integer,
            doctorBranchId is int branchId ? branchId : DBNull.Value);
        AddText(command, "doctor_certificate", record.DoctorCertificate);
        command.Parameters.AddWithValue("registered", NpgsqlDbType.Array | NpgsqlDbType.Varchar, record.RegisteredContent);
        command.Parameters.AddWithValue("final", NpgsqlDbType.Array | NpgsqlDbType.Varchar, record.FinalContent);
        command.Parameters.AddWithValue("diagnoses", NpgsqlDbType.Array | NpgsqlDbType.Varchar,
            DiagnosisCodes(record.Icd10Diagnoses));
        AddText(command, "note", record.PrescriptionNote);
        AddDecimal(command, "rx_cash", record.PrescriptionCopaymentCash); AddDecimal(command, "rx_salary", record.PrescriptionCopaymentSalary);
        AddDecimal(command, "exam_cash", record.ExaminationCopaymentCash); AddDecimal(command, "exam_salary", record.ExaminationCopaymentSalary);
        AddDecimal(command, "drug_copayment", record.MedicationCopayment); AddDecimal(command, "difference", record.EquivalentPriceDifference);
        AddDecimal(command, "institution", record.InstitutionTotalPayment); AddDecimal(command, "patient_payment", record.PatientTotalPayment);
        AddDecimal(command, "total", record.PrescriptionTotal);
        command.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, record.QrCodes);
        AddText(command, "user_id", record.SavedByUserId);
        var recordId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await AppendLogAsync(
            connection,
            transaction,
            uid,
            version,
            currentState is null ? "FIRST_SAVE" : "UPDATE",
            record.SavedByUserId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MedulaPrescriptionInsertResult(recordId, uid, version);
    }

    public async Task<int> InsertAsync(MedulaPrescriptionPersistenceRecord record, CancellationToken token = default) =>
        (await InsertVersionAsync(record, token)).RecordId;

    public async Task<MedulaPrescriptionInsertResult?> MarkDeletedAsync(
        Guid prescriptionUid,
        string userId,
        CancellationToken cancellationToken = default)
    {
        Exception? lastTransientFailure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await MarkDeletedOnceAsync(
                    prescriptionUid, userId, cancellationToken);
            }
            catch (Exception exception) when (IsTransientDatabaseFailure(exception))
            {
                lastTransientFailure = exception;

                // A connection can fail while PostgreSQL is returning the COMMIT result.
                // Verify the durable state on a fresh connection before retrying, otherwise
                // an ambiguous success could create an unnecessary second DELETE version.
                try
                {
                    if (await FindCommittedDeletionAsync(prescriptionUid, cancellationToken) is { } committed)
                        return committed;
                }
                catch (Exception verificationException) when (IsTransientDatabaseFailure(verificationException))
                {
                    lastTransientFailure = verificationException;
                }

                if (attempt == 2) break;
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), cancellationToken);
            }
        }

        throw new NpgsqlException(
            "Reçete silme günlüğü uzak veritabanına üç denemede yazılamadı.",
            lastTransientFailure);
    }

    public async Task<bool> PermanentlyDeleteAsync(
        Guid prescriptionUid,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection, transaction))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using (var lockCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "SELECT 1 FROM public.prescription WHERE prescription_uid=@uid FOR UPDATE",
            connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        var version = await NextVersionAsync(connection, transaction, prescriptionUid, cancellationToken);
        await using (var update = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.prescription
               SET version_number=@version,
                   is_deleted=true,
                   qr_status_code=3,
                   saved_by_user_id=NULLIF(@user_id, ''),
                   saved_at=now()
             WHERE prescription_uid=@uid;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
            update.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
            update.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId.Trim());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AppendLogAsync(
            connection, transaction, prescriptionUid, version, "PERMANENT_DELETE", userId, cancellationToken);
        await using (var delete = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "DELETE FROM public.prescription WHERE prescription_uid=@uid", connection, transaction))
        {
            delete.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AdminDeleteAsync(
        Guid prescriptionUid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var deleteCurrent = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "DELETE FROM public.prescription WHERE prescription_uid=@uid", connection, transaction);
        deleteCurrent.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        var currentCount = await deleteCurrent.ExecuteNonQueryAsync(cancellationToken);
        await using var deleteLog = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "DELETE FROM public.prescription_log WHERE prescription_uid=@uid", connection, transaction);
        deleteLog.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        var logCount = await deleteLog.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return currentCount + logCount > 0;
    }

    public async Task<bool> IsAdminAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT COALESCE(status, false)
                   AND (LOWER(BTRIM(user_id)) = 'admin'
                        OR UPPER(BTRIM(COALESCE(permisson_level, ''))) = 'A')
              FROM public.users
             WHERE LOWER(BTRIM(user_id))=LOWER(BTRIM(@user_id));
            """, connection);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId.Trim());
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<MedulaPrescriptionInsertResult?> MarkDeletedOnceAsync(
        Guid prescriptionUid,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int currentRecordId;
        int currentVersion;
        bool alreadyDeleted;
        await using (var lockCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "SELECT record_id, version_number, is_deleted FROM public.prescription WHERE prescription_uid=@uid FOR UPDATE",
            connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            currentRecordId = reader.GetInt32(0);
            currentVersion = reader.GetInt32(1);
            alreadyDeleted = reader.GetBoolean(2);
        }

        if (alreadyDeleted
            && await HasDeleteLogAsync(
                connection, transaction, prescriptionUid, currentVersion, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new MedulaPrescriptionInsertResult(
                currentRecordId, prescriptionUid, currentVersion, WasWritten: false);
        }

        var version = await NextVersionAsync(connection, transaction, prescriptionUid, cancellationToken);
        const string sql = """
            UPDATE public.prescription
               SET version_number=@version,
                   is_deleted=true,
                   qr_status_code=3,
                   saved_by_user_id=NULLIF(@user_id, ''),
                   saved_at=now()
             WHERE prescription_uid=@uid
            RETURNING record_id;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId.Trim());
        var recordId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await AppendLogAsync(
            connection, transaction, prescriptionUid, version, "DELETE", userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MedulaPrescriptionInsertResult(recordId, prescriptionUid, version);
    }

    private static async Task<bool> HasDeleteLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid prescriptionUid,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            """
            SELECT EXISTS (
                SELECT 1 FROM public.prescription_log
                 WHERE prescription_uid=@uid AND version_number=@version AND action_type='DELETE')
            """,
            connection, transaction);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<MedulaPrescriptionInsertResult?> FindCommittedDeletionAsync(
        Guid prescriptionUid,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            """
            SELECT p.record_id, p.version_number
              FROM public.prescription p
             WHERE p.prescription_uid=@uid
               AND p.is_deleted
               AND EXISTS (
                    SELECT 1 FROM public.prescription_log l
                     WHERE l.prescription_uid=p.prescription_uid
                       AND l.version_number=p.version_number
                       AND l.action_type='DELETE')
            """,
            connection);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MedulaPrescriptionInsertResult(
                reader.GetInt32(0), prescriptionUid, reader.GetInt32(1), WasWritten: false)
            : null;
    }

    private static bool IsTransientDatabaseFailure(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception is IOException
        || exception.InnerException is not null && IsTransientDatabaseFailure(exception.InnerException);

    public async Task<IReadOnlyList<MedulaPrescriptionListItem>> LoadLatestAsync(
        DateOnly startDate, DateOnly endDate, string? medulaTypeValue, CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection)) await schema.ExecuteNonQueryAsync(cancellationToken);
        const string sql = """
            SELECT DISTINCT ON (p.prescription_uid)
                p.record_id, p.prescription_uid, p.version_number, p.medula_filter_date, p.saved_at,
                p.medula_prescription_no, p.prescription_date,
                COALESCE(patient.first_name, ''),
                COALESCE(patient.last_name, ''),
                p.patient_coverage_type,
                CASE p.qr_status_code WHEN 0 THEN 'Yok' WHEN 1 THEN 'Sonlandırılmadı'
                     WHEN 2 THEN 'Sonlandırıldı' WHEN 3 THEN 'Silindi' ELSE '' END,
                p.qr_status_code=2, p.invoice_type_code::varchar,
                p.e_prescription_no, p.prescription_type_code::varchar,
                p.prescription_subtype_code::varchar, COALESCE(invoice_type.invoice_group_name, ''),
                p.provision_type_code::varchar, p.protocol_no, p.facility_code, p.medication_purchase_date,
                p.has_qr_codes, p.is_sequential_distribution, p.patient_tc_identity_no, p.patient_type,
                p.patient_insurance_type, p.recipient_tc_identity_no, p.recipient_relationship,
                p.doctor_diploma_registration_no, doctor.diploma_no, doctor.first_name, doctor.last_name,
                COALESCE(parent_branch.branch_name || ' -> ', '') || COALESCE(prescription_branch.branch_name, ''),
                p.doctor_certificate, p.registered_content, p.final_content,
                ARRAY(SELECT ARRAY[diagnosis.code, COALESCE(icd.description, '')]
                        FROM unnest(COALESCE(p.icd10_diagnoses, ARRAY[]::varchar[])) AS diagnosis(code)
                        LEFT JOIN public.icd10_diagnosis icd ON icd.code=diagnosis.code),
                p.prescription_note, p.prescription_copayment_cash,
                p.prescription_copayment_salary, p.examination_copayment_cash,
                p.examination_copayment_salary, p.medication_copayment, p.equivalent_price_difference,
                p.institution_total_payment, p.patient_total_payment, p.prescription_total,
                p.list_status_code, p.list_barcode_status, p.list_medical_report,
                p.list_document_status, p.is_deleted,
                COALESCE(p.saved_by_user_id, ''), p.qr_codes,
                COALESCE(NULLIF(p.medula_page_number, 0), historic_position.page_number, 0),
                CASE WHEN COALESCE(p.medula_page_number, 0) > 0
                     THEN COALESCE(p.medula_row_index, 0)
                     ELSE COALESCE(historic_position.row_index, 0)
                END
            FROM public.prescription p
            LEFT JOIN public.patient_identity patient_key
                   ON patient_key.tc_identity_no = p.patient_tc_identity_no
            LEFT JOIN public.patient patient ON patient.record_id = patient_key.patient_record_id
            LEFT JOIN public.doctor doctor
                   ON doctor.diploma_registration_no = p.doctor_diploma_registration_no
            LEFT JOIN public.branches prescription_branch
                   ON prescription_branch.branch_id = p.doctor_branch_id
            LEFT JOIN public.branches parent_branch
                   ON parent_branch.branch_id = prescription_branch.parent_branch_id
            LEFT JOIN public.prescription_invoice_type invoice_type
                   ON invoice_type.invoice_type_code = p.invoice_type_code
            LEFT JOIN LATERAL (
                SELECT (log.prescription_snapshot->>'medula_page_number')::integer AS page_number,
                       CASE
                           WHEN COALESCE(log.prescription_snapshot->>'medula_row_index', '') ~ '^[0-9]+$'
                           THEN (log.prescription_snapshot->>'medula_row_index')::integer
                           ELSE 0
                       END AS row_index
                  FROM public.prescription_log log
                 WHERE log.prescription_uid = p.prescription_uid
                   AND COALESCE(log.prescription_snapshot->>'medula_page_number', '') ~ '^[1-9][0-9]*$'
                 ORDER BY log.version_number DESC, log.log_id DESC
                 LIMIT 1
            ) historic_position ON true
            WHERE COALESCE(p.medula_filter_date, p.prescription_date, p.saved_at::date) BETWEEN @start AND @end
              AND (@type IS NULL OR p.invoice_type_code IS NULL OR p.invoice_type_code = @type)
            ORDER BY p.prescription_uid, p.version_number DESC, p.record_id DESC;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("start", NpgsqlDbType.Date, startDate);
        command.Parameters.AddWithValue("end", NpgsqlDbType.Date, endDate);
        AddSmallInt(command, "type", MedulaPrescriptionCodebook.InvoiceTypeCode(null, medulaTypeValue));
        var result = new List<MedulaPrescriptionListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadArchiveItem(reader));
        return OrderLikeMedula(result).ToArray();
    }

    public async Task<IReadOnlyList<MedulaPrescriptionListItem>> LoadPermanentlyDeletedAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? medulaTypeValue,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT DISTINCT ON (log.prescription_uid)
                   log.prescription_uid, log.version_number, log.update_datetime,
                   COALESCE(log.user_id, ''), log.prescription_snapshot::text,
                   COALESCE(historic_position.page_number, 0),
                   COALESCE(historic_position.row_index, 0)
              FROM public.prescription_log log
              LEFT JOIN LATERAL (
                    SELECT (position_log.prescription_snapshot->>'medula_page_number')::integer AS page_number,
                           CASE
                               WHEN COALESCE(position_log.prescription_snapshot->>'medula_row_index', '') ~ '^[0-9]+$'
                               THEN (position_log.prescription_snapshot->>'medula_row_index')::integer
                               ELSE 0
                           END AS row_index
                      FROM public.prescription_log position_log
                     WHERE position_log.prescription_uid = log.prescription_uid
                       AND COALESCE(position_log.prescription_snapshot->>'medula_page_number', '') ~ '^[1-9][0-9]*$'
                     ORDER BY position_log.version_number DESC, position_log.log_id DESC
                     LIMIT 1
              ) historic_position ON true
             WHERE log.action_type='PERMANENT_DELETE'
               AND NOT EXISTS (
                    SELECT 1 FROM public.prescription current
                     WHERE current.prescription_uid=log.prescription_uid)
               AND COALESCE(
                    NULLIF(log.prescription_snapshot->>'medula_filter_date', '')::date,
                    NULLIF(log.prescription_snapshot->>'prescription_date', '')::date,
                    log.update_datetime::date) BETWEEN @start AND @end
               AND (@type IS NULL
                    OR COALESCE(log.prescription_snapshot->>'invoice_type_code', '')=''
                    OR (log.prescription_snapshot->>'invoice_type_code')::smallint=@type)
             ORDER BY log.prescription_uid, log.version_number DESC, log.log_id DESC;
            """, connection);
        command.Parameters.AddWithValue("start", NpgsqlDbType.Date, startDate);
        command.Parameters.AddWithValue("end", NpgsqlDbType.Date, endDate);
        AddSmallInt(command, "type", MedulaPrescriptionCodebook.InvoiceTypeCode(null, medulaTypeValue));
        var result = new List<MedulaPrescriptionListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var snapshot = JsonDocument.Parse(reader.GetString(4));
            result.Add(ReadPermanentlyDeletedItem(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetFieldValue<DateTime>(2),
                reader.GetString(3), reader.GetInt32(5), reader.GetInt32(6), snapshot.RootElement));
        }
        return OrderLikeMedula(result).ToArray();
    }

    public async Task<MedulaPrescriptionSnapshot?> LoadLocalSnapshotForQrAsync(string qrCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(qrCode)) return null;
        Guid? uid;
        await using (var connection = DatabaseConnectionSettingsStore.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                SELECT prescription_uid FROM public.prescription
                WHERE @qr=ANY(qr_codes) AND NOT is_deleted
                ORDER BY saved_at DESC, version_number DESC LIMIT 1
                """, connection);
            command.Parameters.AddWithValue("qr", NpgsqlDbType.Varchar, qrCode);
            uid = await command.ExecuteScalarAsync(cancellationToken) is Guid value ? value : null;
        }
        if (uid is null) return null;
        return (await LoadHistoryAsync(uid.Value, cancellationToken)).FirstOrDefault()?.Snapshot;
    }

    public async Task<IReadOnlyList<MedulaPrescriptionHistoryEntry>> LoadHistoryAsync(
        Guid prescriptionUid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var schema = pharmacyApp.Infrastructure.DatabaseCommand.Create(EnsureSchemaSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT log.prescription_uid,
                   log.version_number,
                   log.action_type,
                   log.update_datetime,
                   COALESCE(log.user_id, ''),
                   log.prescription_snapshot::text,
                   current.version_number,
                   CASE
                       WHEN COALESCE(log.prescription_snapshot->>'medula_page_number', '') ~ '^[1-9][0-9]*$'
                       THEN (log.prescription_snapshot->>'medula_page_number')::integer
                       ELSE 0
                   END,
                   CASE
                       WHEN COALESCE(log.prescription_snapshot->>'medula_row_index', '') ~ '^[0-9]+$'
                       THEN (log.prescription_snapshot->>'medula_row_index')::integer
                       ELSE 0
                   END
              FROM public.prescription_log log
              LEFT JOIN public.prescription current
                     ON current.prescription_uid=log.prescription_uid
             WHERE log.prescription_uid=@uid
             ORDER BY log.version_number DESC, log.update_datetime DESC, log.log_id DESC;
            """, connection);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        var rows = new List<HistoryLogRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new HistoryLogRow(
                    reader.GetGuid(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetFieldValue<DateTime>(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8)));
        }

        var result = new List<MedulaPrescriptionHistoryEntry>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            using var document = JsonDocument.Parse(row.SnapshotJson);
            var archived = ReadPermanentlyDeletedItem(
                row.PrescriptionUid,
                row.VersionNumber,
                row.UpdateDateTime,
                row.UserId,
                row.PageNumber,
                row.RowIndex,
                document.RootElement);
            var isDeleted = document.RootElement.TryGetProperty("is_deleted", out var deletedValue)
                && deletedValue.ValueKind == JsonValueKind.True;
            var source = archived with
            {
                IsDeleted = isDeleted,
                IsPermanentlyDeleted = row.ActionType.Equals("PERMANENT_DELETE", StringComparison.Ordinal),
                LocalSnapshot = null
            };
            var snapshot = archived.LocalSnapshot! with
            {
                Page = archived.LocalSnapshot.Page with
                {
                    PageTitle = "Reçete Geçmişi",
                    Message = $"Reçetenin {row.VersionNumber}. sürümü görüntüleniyor.",
                    SavedSummary = archived.LocalSnapshot.Page.SavedSummary is { } summary
                        ? summary with
                        {
                            ActionCaptions =
                            [
                                "Kaydet", "Sil", "Bilgi", "Yazdır", "Reçeteye Doküman Ekle",
                                "Yeni Reçete", "E-Reçete Sorgu", "Önceki Reçete", "Sonraki Reçete",
                                "ITS Karekod İşlemleri", "Karekod Sonlandır", "Karekod Sonlandırma İptal"
                            ]
                        }
                        : null
                },
                SourceListItem = source
            };
            result.Add(new MedulaPrescriptionHistoryEntry(
                row.PrescriptionUid,
                row.VersionNumber,
                row.ActionType,
                row.UpdateDateTime,
                row.UserId,
                MedulaPrescriptionHistoryFormatter.Describe(
                    row.ActionType,
                    row.SnapshotJson,
                    index + 1 < rows.Count ? rows[index + 1].SnapshotJson : null),
                row.CurrentVersion == row.VersionNumber,
                snapshot));
        }
        return result;
    }

    private static MedulaPrescriptionListItem ReadArchiveItem(NpgsqlDataReader reader)
    {
        string S(int index) => reader.IsDBNull(index) ? string.Empty : reader.GetString(index);
        bool B(int index) => !reader.IsDBNull(index) && reader.GetBoolean(index);
        int I(int index) => reader.IsDBNull(index) ? 0 : reader.GetInt32(index);
        DateOnly? D(int index) => reader.IsDBNull(index) ? null : reader.GetFieldValue<DateOnly>(index);
        decimal? M(int index) => reader.IsDBNull(index) ? null : reader.GetDecimal(index);
        var recordId = reader.GetInt32(0); var uid = reader.GetGuid(1); var version = reader.GetInt32(2);
        var filterDate = D(3); var savedAt = reader.GetFieldValue<DateTime>(4); var prescriptionDate = D(6);
        var fields = new Dictionary<string, string>
        {
            ["medula:prescription-no"] = S(5), ["f:t29"] = FormatDate(prescriptionDate), ["f:t18"] = S(23),
            ["medula:patient-type"] = S(24), ["medula:coverage"] = S(9), ["medula:qr-status"] = S(10),
            ["f:m4"] = S(14), ["f:m8"] = S(15), ["medula:invoice-type"] = S(16), ["f:menu2"] = S(17),
            ["f:t24"] = S(18), ["f:t33"] = S(19), ["f:t31"] = FormatDate(D(20)),
            ["f:checkbox1"] = B(21).ToString(), ["f:checkbox2"] = B(22).ToString(), ["f:text37"] = S(26),
            ["f:menu4"] = S(27), ["f:t43"] = S(28), ["f:t39"] = S(29), ["f:t41"] = S(30),
            ["f:t42"] = S(31), ["medula:branch"] = S(32), ["f:m7"] = S(33),
            ["medula:prescription-note"] = S(37), ["medula:e-prescription-no"] = S(13),
            ["medula:its-qr-codes"] = reader.IsDBNull(53)
                ? string.Empty
                : string.Join('\n', reader.GetFieldValue<string[]>(53))
        };
        var registeredDrugs = ReadDrugs(reader.IsDBNull(34) ? null : ReadMatrixValue(reader.GetValue(34)));
        var drugs = ReadDrugs(reader.IsDBNull(35) ? null : ReadMatrixValue(reader.GetValue(35)));
        if (drugs.Count == 0) drugs = registeredDrugs;
        else MedulaDrugQuantityAdjustments.AnnotateReductions(registeredDrugs, drugs);
        var diagnoses = ReadDiagnoses(reader.IsDBNull(36) ? null : ReadMatrixValue(reader.GetValue(36)));
        var payments = new[]
        {
            Payment("Reç.Kat.Payı Elden", M(38)), Payment("Reç.Kat.Payı Maaştan", M(39)),
            Payment("Mua.Kat.Payı Elden", M(40)), Payment("Mua.Kat.Payı Maaştan", M(41)),
            Payment("İl.Kat.Payı", M(42)), Payment("Fiyat Farkı", M(43)), Payment("Ödenecek Tutar", M(44)),
            Payment("Hastanın eczaneye ödemesi gereken", M(45)), Payment("Toplam Tutar", M(46))
        };
        var page = new MedulaPrescriptionPageResult(true, true, "Kayıtlı Reçete", string.Empty,
            "Yerel reçete geçmişi açıldı.", string.Empty, FieldValues: fields,
            SavedSummary: new MedulaSavedPrescriptionSummary(
                "Ödeme Bilgileri",
                payments,
                [
                    "Kaydet", "Sil", "Bilgi", "Yazdır", "Reçeteye Doküman Ekle",
                    "Yeni Reçete", "E-Reçete Sorgu", "Önceki Reçete", "Sonraki Reçete",
                    "ITS Karekod İşlemleri", "Karekod Sonlandırma İptal"
                ]));
        var snapshot = new MedulaPrescriptionSnapshot(page, drugs, diagnoses, $"{S(7)} {S(8)}".Trim(),
            null, uid, version, true, filterDate, IsSavedOnMedula: true);
        var statusCode = S(47);
        if (statusCode.Length == 0)
            statusCode = S(13).Length > 0
                || S(14).Contains("elektronik", StringComparison.CurrentCultureIgnoreCase) ? "I" : "K";
        return new MedulaPrescriptionListItem(S(5), savedAt.ToString("dd/MM/yyyy"), FormatDate(prescriptionDate),
            S(7), S(8), S(9), S(10), statusCode, S(48), S(49),
            S(50), S(12), filterDate?.ToString("yyyyMM") + "01" ?? string.Empty, "local", I(54), I(55))
        {
            PrescriptionUid = uid, RecordId = recordId, VersionNumber = version,
            MedulaFilterDate = FormatDate(filterDate), IsStoredLocally = true,
            IsDeleted = B(51), LastUser = S(52), PatientTcIdentityNo = S(23), LocalSnapshot = snapshot
        };
    }

    private static MedulaPrescriptionListItem ReadPermanentlyDeletedItem(
        Guid uid,
        int version,
        DateTime updateDateTime,
        string userId,
        int historicPageNumber,
        int historicRowIndex,
        JsonElement root)
    {
        string S(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString().Trim()
            : string.Empty;
        bool B(string name) => root.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
        int I(string name) => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : 0;
        int? NullableI(string name) => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
        DateOnly? D(string name) => DateOnly.TryParse(S(name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
        decimal? M(string name) => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number)
            ? number
            : null;
        string[,] Matrix(string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                return new string[0, 0];
            var rows = value.EnumerateArray().ToArray();
            var columnCount = rows.Length == 0 || rows[0].ValueKind != JsonValueKind.Array
                ? 0
                : rows.Max(row => row.ValueKind == JsonValueKind.Array ? row.GetArrayLength() : 0);
            var matrix = new string[rows.Length, columnCount];
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                if (rows[rowIndex].ValueKind != JsonValueKind.Array) continue;
                var columns = rows[rowIndex].EnumerateArray().ToArray();
                for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                    matrix[rowIndex, columnIndex] = columns[columnIndex].ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : columns[columnIndex].ToString();
            }
            return matrix;
        }
        string[] Array(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ToString()).ToArray()
            : [];

        var filterDate = D("medula_filter_date");
        var prescriptionDate = D("prescription_date");
        var invoiceTypeCode = NullableI("invoice_type_code") is int invoiceCode
            ? (short?)invoiceCode
            : null;
        var qrStatusCode = NullableI("qr_status_code") is int qrCode
            ? (short?)qrCode
            : null;
        var fields = new Dictionary<string, string>
        {
            ["medula:prescription-no"] = S("medula_prescription_no"),
            ["medula:e-prescription-no"] = S("e_prescription_no"),
            ["medula:qr-status"] = MedulaPrescriptionCodebook.QrStatusText(qrStatusCode),
            ["medula:invoice-type"] = MedulaPrescriptionCodebook.InvoiceGroupName(invoiceTypeCode),
            ["medula:coverage"] = S("patient_coverage_type"),
            ["medula:patient-type"] = S("patient_type"),
            ["medula:insurance-type"] = S("patient_insurance_type"),
            ["medula:branch"] = S("doctor_specialty"),
            ["medula:prescription-note"] = S("prescription_note"),
            ["medula:its-qr-codes"] = string.Join('\n', Array("qr_codes")),
            ["f:t18"] = S("patient_tc_identity_no"), ["f:text37"] = S("recipient_tc_identity_no"),
            ["f:menu4"] = S("recipient_relationship"),
            ["f:menu2"] = MedulaPrescriptionCodebook.ControlValue(
                NullableI("provision_type_code") is int provisionCode ? (short?)provisionCode : null),
            ["f:t24"] = S("protocol_no"), ["f:t33"] = S("facility_code"),
            ["f:t29"] = FormatDate(prescriptionDate), ["f:t31"] = FormatDate(D("medication_purchase_date")),
            ["f:m4"] = MedulaPrescriptionCodebook.ControlValue(
                NullableI("prescription_type_code") is int typeCode ? (short?)typeCode : null),
            ["f:m8"] = MedulaPrescriptionCodebook.ControlValue(
                NullableI("prescription_subtype_code") is int subtypeCode ? (short?)subtypeCode : null),
            ["f:t43"] = S("doctor_diploma_registration_no"), ["f:t39"] = S("doctor_diploma_no"),
            ["f:t41"] = S("doctor_first_name"), ["f:t42"] = S("doctor_last_name"),
            ["f:m7"] = S("doctor_certificate"), ["f:checkbox1"] = B("has_qr_codes").ToString(),
            ["f:checkbox2"] = B("is_sequential_distribution").ToString()
        };
        var registeredDrugs = ReadDrugs(Matrix("registered_content"));
        var drugs = ReadDrugs(Matrix("final_content"));
        if (drugs.Count == 0) drugs = registeredDrugs;
        else MedulaDrugQuantityAdjustments.AnnotateReductions(registeredDrugs, drugs);
        var diagnoses = ReadDiagnoses(Matrix("icd10_diagnoses"));
        var payments = new[]
        {
            Payment("Reç.Kat.Payı Elden", M("prescription_copayment_cash")),
            Payment("Reç.Kat.Payı Maaştan", M("prescription_copayment_salary")),
            Payment("Mua.Kat.Payı Elden", M("examination_copayment_cash")),
            Payment("Mua.Kat.Payı Maaştan", M("examination_copayment_salary")),
            Payment("İl.Kat.Payı", M("medication_copayment")),
            Payment("Fiyat Farkı", M("equivalent_price_difference")),
            Payment("Ödenecek Tutar", M("institution_total_payment")),
            Payment("Hastanın eczaneye ödemesi gereken", M("patient_total_payment")),
            Payment("Toplam Tutar", M("prescription_total"))
        };
        var page = new MedulaPrescriptionPageResult(true, true, "Kalıcı Silinmiş Reçete", string.Empty,
            "Kalıcı silinmiş reçete geçmişten açıldı.", string.Empty, FieldValues: fields,
            SavedSummary: new MedulaSavedPrescriptionSummary("Ödeme Bilgileri", payments,
                ["Bilgi", "Yazdır", "Yeni Reçete", "E-Reçete Sorgu"]));
        var displayName = $"{S("patient_first_name")} {S("patient_last_name")}".Trim();
        var archivedSnapshot = new MedulaPrescriptionSnapshot(
            page, drugs, diagnoses, displayName, null, uid, version, true, filterDate,
            IsSavedOnMedula: true);
        var statusCode = S("list_status_code");
        if (statusCode.Length == 0) statusCode = S("e_prescription_no").Length > 0 ? "I" : "K";
        return new MedulaPrescriptionListItem(
            S("medula_prescription_no"), updateDateTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            FormatDate(prescriptionDate), S("patient_first_name"), S("patient_last_name"),
            S("patient_coverage_type"), MedulaPrescriptionCodebook.QrStatusText(qrStatusCode), statusCode,
            S("list_barcode_status"), S("list_medical_report"), S("list_document_status"),
            MedulaPrescriptionCodebook.ControlValue(invoiceTypeCode),
            filterDate?.ToString("yyyyMM") + "01" ?? string.Empty,
            "permanent-delete-log",
            historicPageNumber > 0 ? historicPageNumber : I("medula_page_number"),
            historicPageNumber > 0 ? historicRowIndex : I("medula_row_index"))
        {
            PrescriptionUid = uid,
            RecordId = NullableI("record_id"),
            VersionNumber = version,
            MedulaFilterDate = FormatDate(filterDate),
            IsStoredLocally = true,
            IsDeleted = true,
            IsPermanentlyDeleted = true,
            LastUser = userId,
            PatientTcIdentityNo = S("patient_tc_identity_no"),
            LocalSnapshot = archivedSnapshot
        };
    }

    private static MedulaPrescriptionPaymentField Payment(string label, decimal? value) => new(
        label,
        value?.ToString("0.00", CultureInfo.GetCultureInfo("tr-TR")) ?? "?");
    private static IReadOnlyList<MedulaDrugEntry> ReadDrugs(string[,]? matrix)
    {
        if (matrix is null) return [];
        var result = new List<MedulaDrugEntry>();
        for (var row = 0; row < matrix.GetLength(0); row++) result.Add(new MedulaDrugEntry
        {
            Barcode = Cell(matrix, row, 0), Name = Cell(matrix, row, 1), Quantity = Cell(matrix, row, 2),
            Period = Cell(matrix, row, 3), PeriodUnit = Cell(matrix, row, 4), DoseCount = Cell(matrix, row, 5),
            DoseAmount = Cell(matrix, row, 6), Amount = Cell(matrix, row, 7), Difference = Cell(matrix, row, 8),
            ShelfQuantity = Cell(matrix, row, 9), Report = Cell(matrix, row, 10), AvailableDate = Cell(matrix, row, 11),
            DispensingMessage = Cell(matrix, row, 12), DiagnosisNote = Cell(matrix, row, 14)
        });
        return result;
    }
    private static IReadOnlyList<MedulaDiagnosisEntry> ReadDiagnoses(string[,]? matrix)
    {
        if (matrix is null) return [];
        var result = new List<MedulaDiagnosisEntry>();
        for (var row = 0; row < matrix.GetLength(0); row++) result.Add(new()
            { Code = Cell(matrix, row, 0), Description = Cell(matrix, row, 1) });
        return result;
    }

    private static string[,] ReadMatrixValue(object value)
    {
        if (value is string[,] matrix) return matrix;
        if (value is string[] values)
        {
            // PostgreSQL/Npgsql represents an empty multidimensional array as
            // string[] because an empty PostgreSQL array has no dimensions.
            // A non-empty one-dimensional value is retained as a single-column
            // matrix so historical rows remain readable as well.
            var converted = new string[values.Length, values.Length == 0 ? 0 : 1];
            for (var index = 0; index < values.Length; index++) converted[index, 0] = values[index];
            return converted;
        }
        throw new InvalidCastException(
            $"Unsupported prescription matrix value: {value.GetType().FullName}");
    }
    private static string Cell(string[,] matrix, int row, int column) =>
        column < matrix.GetLength(1) ? matrix[row, column] ?? string.Empty : string.Empty;

    private static async Task<Guid?> FindUidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedulaPrescriptionPersistenceRecord record,
        CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(record.IdentityFingerprint))
        {
            await using var fingerprintCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "SELECT prescription_uid FROM public.prescription WHERE identity_fingerprint=@key ORDER BY version_number DESC LIMIT 1",
                connection, transaction);
            fingerprintCommand.Parameters.AddWithValue(
                "key", NpgsqlDbType.Varchar, record.IdentityFingerprint.Trim());
            if (await fingerprintCommand.ExecuteScalarAsync(token) is Guid fingerprintUid)
                return fingerprintUid;
        }

        // Farmassist and Medula use different identity-fingerprint formats. Resolve
        // their shared business identity before allocating a new UID so whichever
        // side imported first is enriched instead of duplicated.
        var ePrescriptionNo = NormalizeElectronicPrescriptionIdentity(record.EPrescriptionNo);
        var medulaPrescriptionNo = NormalizeExternalPrescriptionNumber(record.MedulaPrescriptionNo);
        var patientTc = record.PatientTcIdentityNo.Trim();
        if (ePrescriptionNo.Length == 0 && (medulaPrescriptionNo.Length == 0 || patientTc.Length == 0))
            return null;

        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            """
            SELECT prescription_uid,
                   (CASE
                        WHEN NULLIF(@e_no, '') IS NOT NULL
                         AND UPPER(BTRIM(COALESCE(e_prescription_no, ''))) = @e_no
                        THEN 32 ELSE 16
                    END
                    + CASE WHEN NULLIF(@patient_tc, '') IS NOT NULL
                                AND BTRIM(COALESCE(patient_tc_identity_no, '')) = @patient_tc
                           THEN 8 ELSE 0 END
                    + CASE WHEN @prescription_date IS NOT NULL
                                AND prescription_date = @prescription_date
                           THEN 4 ELSE 0 END
                    + CASE WHEN NULLIF(@protocol_no, '') IS NOT NULL
                                AND UPPER(BTRIM(COALESCE(protocol_no, ''))) = @protocol_no
                           THEN 2 ELSE 0 END) AS match_score
              FROM public.prescription
             WHERE (
                    NULLIF(@e_no, '') IS NOT NULL
                AND UPPER(BTRIM(COALESCE(e_prescription_no, ''))) = @e_no
                   )
                OR (
                    NULLIF(@medula_no, '') IS NOT NULL
                AND NULLIF(@patient_tc, '') IS NOT NULL
                AND UPPER(BTRIM(COALESCE(medula_prescription_no, ''))) = @medula_no
                AND BTRIM(COALESCE(patient_tc_identity_no, '')) = @patient_tc
                   )
             ORDER BY
                 match_score DESC,
                 CASE WHEN imported_from_farmassist THEN 0 ELSE 1 END,
                 version_number DESC,
                 record_id DESC
             LIMIT 2;
            """,
            connection, transaction);
        command.Parameters.AddWithValue("e_no", NpgsqlDbType.Varchar, ePrescriptionNo);
        command.Parameters.AddWithValue("medula_no", NpgsqlDbType.Varchar, medulaPrescriptionNo);
        command.Parameters.AddWithValue("patient_tc", NpgsqlDbType.Varchar, patientTc);
        command.Parameters.AddWithValue(
            "prescription_date", NpgsqlDbType.Date,
            record.PrescriptionDate is DateOnly prescriptionDate ? prescriptionDate : DBNull.Value);
        command.Parameters.AddWithValue(
            "protocol_no", NpgsqlDbType.Varchar, record.ProtocolNo.Trim().ToUpperInvariant());
        var candidates = new List<(Guid Uid, int Score)>(2);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            candidates.Add((reader.GetGuid(0), reader.GetInt32(1)));
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 || candidates[0].Uid == candidates[1].Uid)
            return candidates[0].Uid;
        return candidates[0].Score > candidates[1].Score ? candidates[0].Uid : null;
    }

    private static string NormalizeExternalPrescriptionNumber(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static string NormalizeElectronicPrescriptionIdentity(string value)
    {
        var candidate = NormalizeExternalPrescriptionNumber(value);
        return candidate.Length is 6 or 7
               && candidate.Any(char.IsLetter)
               && candidate.All(char.IsLetterOrDigit)
            ? candidate
            : string.Empty;
    }

    private static async Task AppendLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid prescriptionUid,
        int version,
        string actionType,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.prescription_log (
                prescription_uid, version_number, action_type, update_datetime, user_id, prescription_snapshot)
            SELECT p.prescription_uid, p.version_number, @action_type, p.saved_at, NULLIF(@user_id, ''),
                   to_jsonb(p) || jsonb_build_object(
                       'patient_first_name', COALESCE(patient.first_name, ''),
                       'patient_last_name', COALESCE(patient.last_name, ''),
                       'doctor_diploma_no', COALESCE(doctor.diploma_no, ''),
                       'doctor_first_name', COALESCE(doctor.first_name, ''),
                       'doctor_last_name', COALESCE(doctor.last_name, ''),
                       'doctor_specialty', COALESCE(parent_branch.branch_name || ' -> ', '')
                                           || COALESCE(branch.branch_name, ''),
                       'doctor_certificate', COALESCE(p.doctor_certificate, ''),
                       'icd10_diagnoses', COALESCE((
                           SELECT jsonb_agg(jsonb_build_array(diagnosis.code, COALESCE(icd.description, '')))
                             FROM unnest(COALESCE(p.icd10_diagnoses, ARRAY[]::varchar[])) AS diagnosis(code)
                             LEFT JOIN public.icd10_diagnosis icd ON icd.code=diagnosis.code), '[]'::jsonb))
              FROM public.prescription p
              LEFT JOIN public.patient_identity patient_key
                     ON patient_key.tc_identity_no=p.patient_tc_identity_no
              LEFT JOIN public.patient patient ON patient.record_id=patient_key.patient_record_id
              LEFT JOIN public.doctor doctor
                     ON doctor.diploma_registration_no=p.doctor_diploma_registration_no
              LEFT JOIN public.branches branch ON branch.branch_id=p.doctor_branch_id
              LEFT JOIN public.branches parent_branch ON parent_branch.branch_id=branch.parent_branch_id
             WHERE prescription_uid=@uid AND version_number=@version
            ON CONFLICT (prescription_uid, version_number) DO NOTHING;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
        command.Parameters.AddWithValue("action_type", NpgsqlDbType.Varchar, actionType);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int?> EnsureDoctorReferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedulaPrescriptionPersistenceRecord record,
        CancellationToken cancellationToken)
    {
        var registrationNo = record.DoctorDiplomaRegistrationNo.Trim();
        if (registrationNo.Length == 0) return null;
        var branchId = await ResolveBranchIdAsync(connection, transaction, record.DoctorBranch, cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            MedulaDirectorySql.InsertDoctorIfMissing, connection, transaction);
        command.Parameters.AddWithValue("registration_no", NpgsqlDbType.Varchar, registrationNo);
        command.Parameters.AddWithValue("diploma_no", NpgsqlDbType.Varchar, record.DoctorDiplomaNo.Trim());
        command.Parameters.AddWithValue("first_name", NpgsqlDbType.Varchar,
            record.DoctorFirstName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR")));
        command.Parameters.AddWithValue("last_name", NpgsqlDbType.Varchar,
            record.DoctorLastName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR")));
        command.Parameters.AddWithValue("branch_id", NpgsqlDbType.Integer,
            branchId is int value ? value : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return branchId;
    }

    private static async Task<int?> ResolveBranchIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string specialty,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(specialty)) return null;
        var decoded = WebUtility.HtmlDecode(specialty).Trim();
        decoded = Regex.Replace(decoded, @"\s*\(Ana Branş\)\s*$", "", RegexOptions.IgnoreCase);
        var path = decoded.Split("->", 2, StringSplitOptions.TrimEntries);
        var parentName = path.Length == 2 ? path[0] : "";
        var branchName = path.Length == 2 ? path[1] : path[0];
        const string sql = """
            SELECT child.branch_id
            FROM public.branches child
            LEFT JOIN public.branches parent ON parent.branch_id=child.parent_branch_id
            WHERE LOWER(REPLACE(child.branch_name, ' ', ''))=LOWER(REPLACE(@branch_name, ' ', ''))
              AND (NULLIF(@parent_name, '') IS NULL OR LOWER(parent.branch_name)=LOWER(@parent_name))
            ORDER BY CASE WHEN NULLIF(@parent_name, '') IS NOT NULL THEN 0
                          WHEN child.parent_branch_id IS NULL THEN 1 ELSE 2 END,
                     child.display_order
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("branch_name", NpgsqlDbType.Varchar, branchName);
        command.Parameters.AddWithValue("parent_name", NpgsqlDbType.Varchar, parentName);
        return await command.ExecuteScalarAsync(cancellationToken) is int branchId ? branchId : null;
    }

    private static async Task EnsurePatientReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedulaPrescriptionPersistenceRecord record,
        CancellationToken cancellationToken)
    {
        await EnsurePatientReferenceAsync(connection, transaction, record.PatientTcIdentityNo,
            record.PatientFirstName, record.PatientLastName, cancellationToken);
        if (!string.Equals(record.RecipientTcIdentityNo.Trim(), record.PatientTcIdentityNo.Trim(), StringComparison.Ordinal))
            await EnsurePatientReferenceAsync(connection, transaction, record.RecipientTcIdentityNo,
                string.Empty, string.Empty, cancellationToken);
    }

    private static async Task EnsurePatientReferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tcIdentityNoValue,
        string firstName,
        string lastName,
        CancellationToken cancellationToken)
    {
        var tcIdentityNo = tcIdentityNoValue.Trim();
        if (tcIdentityNo.Length == 0) return;
        const string sql = """
            INSERT INTO public.patient (tc_identity_no, first_name, last_name)
            SELECT @tc_identity_no, NULLIF(@first_name, ''), NULLIF(@last_name, '')
            WHERE NOT EXISTS (
                SELECT 1 FROM public.patient WHERE tc_identity_no = @tc_identity_no);
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("tc_identity_no", NpgsqlDbType.Varchar, tcIdentityNo);
        command.Parameters.AddWithValue("first_name", NpgsqlDbType.Varchar,
            firstName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR")));
        command.Parameters.AddWithValue("last_name", NpgsqlDbType.Varchar,
            lastName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR")));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var identityCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.patient_identity (tc_identity_no, patient_record_id)
            SELECT @tc_identity_no, record_id
              FROM public.patient
             WHERE tc_identity_no=@tc_identity_no
             ORDER BY record_id DESC
             LIMIT 1
            ON CONFLICT (tc_identity_no) DO UPDATE
                SET patient_record_id=EXCLUDED.patient_record_id;
            """, connection, transaction);
        identityCommand.Parameters.AddWithValue("tc_identity_no", NpgsqlDbType.Varchar, tcIdentityNo);
        await identityCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDiagnosisReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string[,] diagnoses,
        CancellationToken cancellationToken)
    {
        for (var row = 0; row < diagnoses.GetLength(0); row++)
        {
            var code = Cell(diagnoses, row, 0).Trim().ToUpperInvariant();
            if (code.Length == 0) continue;
            var description = Cell(diagnoses, row, 1).Trim();
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                INSERT INTO public.icd10_diagnosis (code, description)
                VALUES (@code, COALESCE(NULLIF(@description, ''), @code))
                ON CONFLICT (code) DO UPDATE
                    SET description = CASE WHEN EXCLUDED.description = EXCLUDED.code
                                           THEN icd10_diagnosis.description
                                           ELSE EXCLUDED.description END;
                """, connection, transaction);
            command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
            command.Parameters.AddWithValue("description", NpgsqlDbType.Varchar, description);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string[] DiagnosisCodes(string[,] diagnoses) =>
        Enumerable.Range(0, diagnoses.GetLength(0))
            .Select(row => Cell(diagnoses, row, 0).Trim().ToUpperInvariant())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    private static async Task<int> NextVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid uid, CancellationToken token)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            """
            SELECT GREATEST(
                COALESCE((SELECT MAX(version_number) FROM public.prescription WHERE prescription_uid=@uid), 0),
                COALESCE((SELECT MAX(version_number) FROM public.prescription_log WHERE prescription_uid=@uid), 0)) + 1
            """,
            connection, transaction);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, uid);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task<CurrentPrescriptionState?> LockCurrentPrescriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid uid,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            """
            SELECT record_id, version_number, COALESCE(state_fingerprint, ''), is_deleted,
                   COALESCE(medula_prescription_no, '')
              FROM public.prescription
             WHERE prescription_uid=@uid
             FOR UPDATE
            """,
            connection, transaction);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, uid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CurrentPrescriptionState(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetString(4))
            : null;
    }

    private static string BuildStateFingerprint(MedulaPrescriptionPersistenceRecord record)
    {
        var payload = new StringBuilder(2048);
        void Add(object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            payload.Append(text.Length).Append(':').Append(text).Append('|');
        }
        void AddMatrix(string[,] matrix)
        {
            Add(matrix.GetLength(0));
            Add(matrix.GetLength(1));
            for (var row = 0; row < matrix.GetLength(0); row++)
                for (var column = 0; column < matrix.GetLength(1); column++)
                    Add(matrix[row, column]);
        }

        Add(record.EPrescriptionNo); Add(record.MedulaPrescriptionNo); Add(record.MedulaQrStatus);
        Add(record.PrescriptionType); Add(record.PrescriptionSubtype); Add(record.InvoiceType);
        Add(record.ProvisionType); Add(record.ProtocolNo); Add(record.FacilityCode);
        Add(record.PrescriptionDate); Add(record.MedicationPurchaseDate); Add(record.HasQrCodes);
        Add(record.IsSequentialDistribution); Add(record.PatientTcIdentityNo); Add(record.PatientType);
        Add(record.PatientInsuranceType); Add(record.PatientCoverageType); Add(record.RecipientTcIdentityNo);
        Add(record.RecipientRelationship); Add(record.DoctorDiplomaRegistrationNo); Add(record.DoctorBranch);
        Add(record.DoctorCertificate);
        AddMatrix(record.RegisteredContent); AddMatrix(record.FinalContent);
        foreach (var diagnosisCode in DiagnosisCodes(record.Icd10Diagnoses)) Add(diagnosisCode);
        Add(record.PrescriptionNote); Add(record.PrescriptionCopaymentCash);
        Add(record.PrescriptionCopaymentSalary); Add(record.ExaminationCopaymentCash);
        Add(record.ExaminationCopaymentSalary); Add(record.MedicationCopayment);
        Add(record.EquivalentPriceDifference); Add(record.InstitutionTotalPayment);
        Add(record.PatientTotalPayment); Add(record.PrescriptionTotal);
        foreach (var qrCode in record.QrCodes) Add(qrCode);
        Add(record.ItsFinalized); Add(record.MedulaFilterDate);
        Add(record.ImportedFromMedula); Add(record.IdentityFingerprint);
        Add(record.MedulaTypeValue); Add(record.ListStatusCode); Add(record.ListBarcodeStatus);
        Add(record.ListMedicalReport); Add(record.ListDocumentStatus);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private sealed record CurrentPrescriptionState(
        int RecordId,
        int VersionNumber,
        string StateFingerprint,
        bool IsDeleted,
        string MedulaPrescriptionNumber);
    private sealed record HistoryLogRow(
        Guid PrescriptionUid,
        int VersionNumber,
        string ActionType,
        DateTime UpdateDateTime,
        string UserId,
        string SnapshotJson,
        int? CurrentVersion,
        int PageNumber,
        int RowIndex);
    public static string BuildIdentityFingerprint(string prescriptionNumber, string patientTc,
        DateOnly? filterDate, string typeValue, string protocolNo) =>
        string.Join('|', prescriptionNumber.Trim(), patientTc.Trim(), filterDate?.ToString("yyyyMMdd") ?? string.Empty,
            typeValue.Trim(), protocolNo.Trim()).ToUpperInvariant();
    private static string FormatDate(DateOnly? value) => value?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
    private static DateOnly? ParseDate(string value) => DateOnly.TryParseExact(value, ["dd/MM/yyyy", "d/M/yyyy"],
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static IOrderedEnumerable<MedulaPrescriptionListItem> OrderLikeMedula(
        IEnumerable<MedulaPrescriptionListItem> items) => items
        .OrderBy(item => (ParseDate(item.MedulaFilterDate) ?? ParseDate(item.PrescriptionDate)) is null)
        .ThenByDescending(item => ParseDate(item.MedulaFilterDate) ?? ParseDate(item.PrescriptionDate))
        .ThenByDescending(item => item.PageNumber > 0 ? item.PageNumber : int.MinValue)
        .ThenByDescending(item => item.PageNumber > 0 ? item.RowIndex : int.MinValue)
        .ThenByDescending(item => item.RecordId ?? int.MinValue);
    private static void AddText(NpgsqlCommand command, string name, string value) => command.Parameters.AddWithValue(
        name, NpgsqlDbType.Varchar, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim());
    private static void AddSmallInt(NpgsqlCommand command, string name, short? value) => command.Parameters.AddWithValue(
        name, NpgsqlDbType.Smallint, value is null ? DBNull.Value : value.Value);
    private static void AddDate(NpgsqlCommand command, string name, DateOnly? value) => command.Parameters.AddWithValue(
        name, NpgsqlDbType.Date, value is null ? DBNull.Value : value.Value);
    private static void AddDecimal(NpgsqlCommand command, string name, decimal? value) => command.Parameters.AddWithValue(
        name, NpgsqlDbType.Numeric, value is null ? DBNull.Value : value.Value);
}
