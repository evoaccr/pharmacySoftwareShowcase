using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Medula;

public sealed record MedulaDirectoryChange(
    bool WasInserted,
    bool WasUpdated,
    string Title,
    string Summary,
    IReadOnlyList<MedulaDirectoryField>? Fields = null,
    MedulaDirectoryUndo? Undo = null);

public sealed record MedulaDirectoryField(string Label, string Value);

public sealed record MedulaDirectoryUndo(
    string Entity,
    int RecordId,
    bool DeleteOnUndo,
    string IdentityKey,
    string? PreviousFirstName,
    string? PreviousLastName,
    string? PreviousGender,
    DateTime? PreviousBirthDate,
    string? PreviousDiplomaRegistrationNo = null,
    string? PreviousDiplomaNo = null,
    int? PreviousBranchId = null,
    string? PreviousBranchName = null);

public sealed record MedulaRecipientSuggestion(
    PatientSuggestion Patient,
    string RelationshipNote)
{
    public string DisplayText => $"{Patient.DisplayText} · {RelationshipNote}";
}

public sealed record MedulaPrescriptionPartyDebtSummary(
    string OwnerDisplayName,
    string OwnerPrimaryPhone,
    string FamilyGroupName,
    decimal OwnerDebt,
    decimal? RecipientDebt,
    decimal FamilyGroupDebt)
{
    public decimal TotalDebt => OwnerDebt + (RecipientDebt ?? 0m) + FamilyGroupDebt;
    public bool HasFamilyGroup => !string.IsNullOrWhiteSpace(FamilyGroupName);
}

public sealed class MedulaDirectoryRepository
{
    private readonly RetailSaleRepository patientSearch = new();

    public async Task<IReadOnlyDictionary<string, string>> ReadDrugSgkCodesAsync(
        IEnumerable<string> barcodes,
        CancellationToken cancellationToken = default)
    {
        var values = barcodes.Select(value => value.Trim())
            .Where(value => value.Length > 0 && value != "?")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0) return new Dictionary<string, string>(StringComparer.Ordinal);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT barcode, COALESCE(NULLIF(BTRIM(sgk_code), ''), '')
              FROM public.barcode
             WHERE barcode = ANY(@barcodes)
            """, connection);
        command.Parameters.AddWithValue("barcodes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, values);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public async Task<IReadOnlyDictionary<string, int>> ReadDrugStockQuantitiesAsync(
        IEnumerable<string> barcodes,
        CancellationToken cancellationToken = default)
    {
        var codes = barcodes.Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (codes.Length == 0) return new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                SELECT barcode, COALESCE(quantity, 0)
                FROM public."barcodeActive"
                WHERE barcode = ANY(@barcodes)
                """, connection);
            command.Parameters.AddWithValue(
                "barcodes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, codes);
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }
        catch (NpgsqlException)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    public async Task<decimal> ReadPatientDebtTotalAsync(
        IEnumerable<string> tcIdentityNumbers,
        CancellationToken cancellationToken = default)
    {
        var identities = tcIdentityNumbers
            .Select(value => value.Trim())
            .Where(value => value.Length == 11 && value.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identities.Length == 0) return 0m;

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT COALESCE(SUM(patient.debt_amount), 0)
              FROM public.patient patient
             WHERE patient.tc_identity_no = ANY(@identities)
            """, connection);
        command.Parameters.AddWithValue(
            "identities", NpgsqlDbType.Array | NpgsqlDbType.Varchar, identities);
        return Convert.ToDecimal(
            await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public Task<IReadOnlyList<PatientSuggestion>> SearchPatientsAsync(
        string search,
        CancellationToken cancellationToken) =>
        patientSearch.SearchPatientsAsync(search, cancellationToken);

    public async Task<MedulaPrescriptionPartyDebtSummary> ReadPrescriptionPartyDebtSummaryAsync(
        string ownerTcIdentityNumber,
        string recipientTcIdentityNumber,
        CancellationToken cancellationToken = default)
    {
        ownerTcIdentityNumber = ownerTcIdentityNumber.Trim();
        recipientTcIdentityNumber = recipientTcIdentityNumber.Trim();
        var ownerValid = ownerTcIdentityNumber.Length == 11 && ownerTcIdentityNumber.All(char.IsDigit);
        var recipientValid = recipientTcIdentityNumber.Length == 11 && recipientTcIdentityNumber.All(char.IsDigit);
        if (!ownerValid)
            return new MedulaPrescriptionPartyDebtSummary(string.Empty, string.Empty, string.Empty, 0m,
                recipientValid && recipientTcIdentityNumber != ownerTcIdentityNumber ? 0m : null, 0m);

        var distinctParties = new[] { ownerTcIdentityNumber, recipientTcIdentityNumber }
            .Where(value => value.Length == 11 && value.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var samePerson = ownerTcIdentityNumber == recipientTcIdentityNumber;

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            WITH owner AS (
                SELECT record_id, family_group_id,
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''), '') AS display_name,
                       COALESCE(phone_numbers[1], '') AS primary_phone,
                       COALESCE(debt_amount, 0) AS debt_amount
                  FROM public.patient
                 WHERE tc_identity_no = @owner_tc
                 ORDER BY record_id
                 LIMIT 1
            ), recipient AS (
                SELECT COALESCE(debt_amount, 0) AS debt_amount
                  FROM public.patient
                 WHERE tc_identity_no = @recipient_tc
                 ORDER BY record_id
                 LIMIT 1
            )
            SELECT COALESCE((SELECT display_name FROM owner), ''),
                   COALESCE((SELECT primary_phone FROM owner), ''),
                   COALESCE((SELECT family_group_name
                               FROM public.family_group family_group
                               JOIN owner ON owner.family_group_id = family_group.record_id
                              LIMIT 1), ''),
                   COALESCE((SELECT debt_amount FROM owner), 0),
                   CASE WHEN @same_person OR NOT @recipient_valid THEN NULL
                        ELSE COALESCE((SELECT debt_amount FROM recipient), 0) END,
                   COALESCE((SELECT SUM(COALESCE(member.debt_amount, 0))
                               FROM public.patient member
                               JOIN owner ON member.family_group_id = owner.family_group_id
                              WHERE owner.family_group_id IS NOT NULL
                                AND NOT (COALESCE(member.tc_identity_no, '') = ANY(@party_tcs))), 0)
            """, connection);
        command.Parameters.AddWithValue("owner_tc", NpgsqlDbType.Varchar, ownerTcIdentityNumber);
        command.Parameters.AddWithValue("recipient_tc", NpgsqlDbType.Varchar, recipientTcIdentityNumber);
        command.Parameters.AddWithValue("same_person", NpgsqlDbType.Boolean, samePerson);
        command.Parameters.AddWithValue("recipient_valid", NpgsqlDbType.Boolean, recipientValid);
        command.Parameters.AddWithValue("party_tcs", NpgsqlDbType.Array | NpgsqlDbType.Varchar, distinctParties);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new MedulaPrescriptionPartyDebtSummary(string.Empty, string.Empty, string.Empty, 0m,
                samePerson ? null : 0m, 0m);
        return new MedulaPrescriptionPartyDebtSummary(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetDecimal(5));
    }

    public async Task<IReadOnlyList<MedulaRecipientSuggestion>> LoadRecipientSuggestionsAsync(
        string ownerTcIdentityNo,
        CancellationToken cancellationToken)
    {
        ownerTcIdentityNo = ownerTcIdentityNo.Trim();
        if (ownerTcIdentityNo.Length != 11 || ownerTcIdentityNo.Any(character => !char.IsDigit(character)))
            return [];

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH owner AS (
                SELECT record_id, family_group_id
                FROM public.patient
                WHERE tc_identity_no = @owner_tc
                ORDER BY record_id
                LIMIT 1
            )
            SELECT patient.record_id,
                   COALESCE(patient.tc_identity_no, ''),
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                            NULLIF(patient.tc_identity_no, ''), patient.record_id::text),
                   COALESCE(patient.phone_numbers[1], ''),
                   COALESCE(patient.debt_amount, 0),
                   COALESCE(patient.debt_credit_score, 0),
                   patient.photo,
                   patient.record_id = owner.record_id AS is_owner
            FROM public.patient patient
            CROSS JOIN owner
            WHERE patient.record_id = owner.record_id
               OR (owner.family_group_id IS NOT NULL
                   AND patient.family_group_id = owner.family_group_id)
            ORDER BY patient.record_id = owner.record_id DESC,
                     patient.first_name NULLS LAST,
                     patient.last_name NULLS LAST,
                     patient.record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("owner_tc", NpgsqlDbType.Varchar, ownerTcIdentityNo);
        var suggestions = new List<MedulaRecipientSuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var patient = new PatientSuggestion(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6));
            suggestions.Add(new MedulaRecipientSuggestion(
                patient,
                reader.GetBoolean(7) ? "Kendisi" : "Yakını"));
        }
        return suggestions;
    }

    public async Task<MedulaDirectoryChange?> ReconcilePatientAsync(
        string tcIdentityNo,
        string firstName,
        string lastName,
        string gender,
        string birthDate,
        CancellationToken cancellationToken)
    {
        tcIdentityNo = tcIdentityNo.Trim();
        firstName = firstName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        lastName = lastName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        gender = gender.Trim();
        if (tcIdentityNo.Length != 11 || firstName.Length == 0) return null;
        var parsedBirthDate = ParseDate(birthDate);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string selectSql = """
            SELECT record_id, COALESCE(first_name, ''), COALESCE(last_name, ''),
                   COALESCE(gender, ''), date_of_birth
            FROM public.patient
            WHERE tc_identity_no = @tc
            ORDER BY record_id
            LIMIT 1
            """;
        await using var select = pharmacyApp.Infrastructure.DatabaseCommand.Create(selectSql, connection);
        select.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, tcIdentityNo);
        int? recordId = null;
        string oldFirstName = string.Empty;
        string oldLastName = string.Empty;
        string oldGender = string.Empty;
        DateTime? oldBirthDate = null;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                recordId = reader.GetInt32(0);
                oldFirstName = reader.GetString(1);
                oldLastName = reader.GetString(2);
                oldGender = reader.GetString(3);
                oldBirthDate = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
            }
        }

        if (recordId is null)
        {
            const string insertSql = """
                INSERT INTO public.patient
                    (tc_identity_no, first_name, last_name, gender, date_of_birth)
                VALUES (@tc, @first_name, NULLIF(@last_name, ''), NULLIF(@gender, ''), @birth_date)
                RETURNING record_id
                """;
            await using var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create(insertSql, connection);
            AddPatientParameters(insert, tcIdentityNo, firstName, lastName, gender, parsedBirthDate);
            var insertedId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return new MedulaDirectoryChange(
                true,
                false,
                "Yeni Hasta",
                $"{firstName} {lastName}",
                PatientFields(tcIdentityNo, firstName, lastName, gender, parsedBirthDate),
                new MedulaDirectoryUndo(
                    "patient", insertedId, true, tcIdentityNo,
                    null, null, null, null));
        }

        var changed = !Same(oldFirstName, firstName)
            || !Same(oldLastName, lastName)
            || (gender.Length > 0 && !Same(oldGender, gender))
            || (parsedBirthDate.HasValue && oldBirthDate?.Date != parsedBirthDate.Value.Date);
        if (!changed) return null;

        const string updateSql = """
            UPDATE public.patient
            SET first_name = @first_name,
                last_name = NULLIF(@last_name, ''),
                gender = COALESCE(NULLIF(@gender, ''), gender),
                date_of_birth = COALESCE(@birth_date, date_of_birth)
            WHERE record_id = @record_id
            """;
        await using var update = pharmacyApp.Infrastructure.DatabaseCommand.Create(updateSql, connection);
        AddPatientParameters(update, tcIdentityNo, firstName, lastName, gender, parsedBirthDate);
        update.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, recordId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return new MedulaDirectoryChange(
            false,
            true,
            "Hasta Güncellemesi",
            $"{firstName} {lastName}",
            PatientFields(
                tcIdentityNo,
                Transition(oldFirstName, firstName),
                Transition(oldLastName, lastName),
                gender.Length > 0 ? Transition(oldGender, gender) : Display(oldGender),
                parsedBirthDate.HasValue
                    ? Transition(Display(oldBirthDate), Display(parsedBirthDate))
                    : Display(oldBirthDate),
                valuesAreDisplayReady: true),
            new MedulaDirectoryUndo(
                "patient", recordId.Value, false, tcIdentityNo,
                oldFirstName, oldLastName, oldGender, oldBirthDate));
    }

    public async Task<MedulaDirectoryChange?> ReconcileDoctorAsync(
        string diplomaRegistrationNo,
        string diplomaNo,
        string firstName,
        string lastName,
        IReadOnlyList<string>? specialties,
        CancellationToken cancellationToken)
    {
        diplomaRegistrationNo = diplomaRegistrationNo.Trim();
        diplomaNo = diplomaNo.Trim();
        firstName = firstName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        lastName = lastName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        var normalizedSpecialties = specialties?
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (normalizedSpecialties is { Length: 0 }) normalizedSpecialties = null;
        if (diplomaRegistrationNo.Length == 0 || firstName.Length == 0) return null;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var selectedBranch = await ResolveBranchAsync(connection, normalizedSpecialties?.FirstOrDefault(), cancellationToken);
        const string selectSql = """
            SELECT record_id, COALESCE(diploma_registration_no, ''), COALESCE(diploma_no, ''),
                   COALESCE(first_name, ''), COALESCE(last_name, ''),
                   d.branch_id,
                   COALESCE(CASE WHEN parent.branch_id IS NULL THEN branch.branch_name
                                 ELSE parent.branch_name || ' / ' || branch.branch_name END, '')
            FROM public.doctor d
            LEFT JOIN public.branches branch ON branch.branch_id=d.branch_id
            LEFT JOIN public.branches parent ON parent.branch_id=branch.parent_branch_id
            WHERE diploma_registration_no = @diploma_registration_no
               OR (NULLIF(@diploma_no, '') IS NOT NULL AND diploma_no = @diploma_no)
            ORDER BY CASE WHEN diploma_registration_no = @diploma_registration_no THEN 0 ELSE 1 END,
                     record_id
            LIMIT 1
            """;
        await using var select = pharmacyApp.Infrastructure.DatabaseCommand.Create(selectSql, connection);
        AddDoctorIdentityParameters(select, diplomaRegistrationNo, diplomaNo);
        int? recordId = null;
        var oldDiplomaRegistrationNo = string.Empty;
        var oldDiplomaNo = string.Empty;
        var oldFirstName = string.Empty;
        var oldLastName = string.Empty;
        int? oldBranchId = null;
        var oldBranchName = string.Empty;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                recordId = reader.GetInt32(0);
                oldDiplomaRegistrationNo = reader.GetString(1);
                oldDiplomaNo = reader.GetString(2);
                oldFirstName = reader.GetString(3);
                oldLastName = reader.GetString(4);
                oldBranchId = reader.IsDBNull(5) ? null : reader.GetInt32(5);
                oldBranchName = reader.GetString(6);
            }
        }

        if (recordId is null)
        {
            const string insertSql = """
                INSERT INTO public.doctor
                    (diploma_registration_no, diploma_no, first_name, last_name, branch_id)
                VALUES
                    (@diploma_registration_no, NULLIF(@diploma_no, ''), @first_name,
                     NULLIF(@last_name, ''), @branch_id)
                RETURNING record_id
                """;
            await using var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create(insertSql, connection);
            AddDoctorParameters(
                insert, diplomaRegistrationNo, diplomaNo, firstName, lastName, selectedBranch?.Id);
            var insertedId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return new MedulaDirectoryChange(
                true, false, "Yeni Hekim", $"{firstName} {lastName}",
                DoctorFields(diplomaRegistrationNo, diplomaNo, firstName, lastName, selectedBranch?.Name),
                new MedulaDirectoryUndo(
                    "doctor", insertedId, true, diplomaRegistrationNo,
                    null, null, null, null));
        }

        var changed = !Same(oldDiplomaRegistrationNo, diplomaRegistrationNo)
            || !Same(oldDiplomaNo, diplomaNo)
            || !Same(oldFirstName, firstName)
            || !Same(oldLastName, lastName)
            || (normalizedSpecialties is not null && selectedBranch?.Id != oldBranchId);
        if (!changed) return null;
        const string updateSql = """
            UPDATE public.doctor
            SET diploma_registration_no = @diploma_registration_no,
                diploma_no = NULLIF(@diploma_no, ''),
                first_name = @first_name,
                last_name = NULLIF(@last_name, ''),
                branch_id = COALESCE(@branch_id, branch_id)
            WHERE record_id = @record_id
            """;
        await using var update = pharmacyApp.Infrastructure.DatabaseCommand.Create(updateSql, connection);
        AddDoctorParameters(
            update, diplomaRegistrationNo, diplomaNo, firstName, lastName, selectedBranch?.Id);
        update.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, recordId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return new MedulaDirectoryChange(
            false, true, "Hekim Güncellemesi", $"{firstName} {lastName}",
            DoctorFields(
                Transition(oldDiplomaRegistrationNo, diplomaRegistrationNo),
                Transition(oldDiplomaNo, diplomaNo),
                Transition(oldFirstName, firstName),
                Transition(oldLastName, lastName),
                normalizedSpecialties is null
                    ? Display(oldBranchName)
                    : Transition(Display(oldBranchName), Display(selectedBranch?.Name ?? oldBranchName)),
                valuesAreDisplayReady: true),
            new MedulaDirectoryUndo(
                "doctor", recordId.Value, false, diplomaRegistrationNo,
                oldFirstName, oldLastName, null, null,
                oldDiplomaRegistrationNo, oldDiplomaNo, oldBranchId, oldBranchName));
    }

    public async Task UndoAsync(MedulaDirectoryUndo undo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(undo);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (undo.DeleteOnUndo)
        {
            var table = undo.Entity == "patient" ? "patient" : undo.Entity == "doctor" ? "doctor" : null;
            if (table is null) throw new InvalidOperationException("Bilinmeyen dizin değişikliği geri alınamaz.");
            await using var delete = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                $"DELETE FROM public.{table} WHERE record_id = @record_id", connection);
            delete.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, undo.RecordId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (undo.Entity == "patient")
        {
            const string sql = """
                UPDATE public.patient
                SET first_name = @first_name,
                    last_name = @last_name,
                    gender = @gender,
                    date_of_birth = @birth_date
                WHERE record_id = @record_id
                """;
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
            command.Parameters.AddWithValue("first_name", NpgsqlDbType.Varchar,
                (object?)undo.PreviousFirstName ?? DBNull.Value);
            command.Parameters.AddWithValue("last_name", NpgsqlDbType.Varchar,
                EmptyToDbNull(undo.PreviousLastName));
            command.Parameters.AddWithValue("gender", NpgsqlDbType.Varchar,
                EmptyToDbNull(undo.PreviousGender));
            command.Parameters.AddWithValue("birth_date", NpgsqlDbType.Date,
                undo.PreviousBirthDate is null ? DBNull.Value : undo.PreviousBirthDate.Value);
            command.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, undo.RecordId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (undo.Entity == "doctor")
        {
            const string sql = """
                UPDATE public.doctor
                SET diploma_registration_no = @diploma_registration_no,
                    diploma_no = @diploma_no,
                    first_name = @first_name,
                    last_name = @last_name,
                    branch_id = @branch_id
                WHERE record_id = @record_id
                """;
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
            command.Parameters.AddWithValue("diploma_registration_no", NpgsqlDbType.Varchar,
                EmptyToDbNull(undo.PreviousDiplomaRegistrationNo));
            command.Parameters.AddWithValue("diploma_no", NpgsqlDbType.Varchar,
                EmptyToDbNull(undo.PreviousDiplomaNo));
            command.Parameters.AddWithValue("first_name", NpgsqlDbType.Varchar,
                (object?)undo.PreviousFirstName ?? DBNull.Value);
            command.Parameters.AddWithValue("last_name", NpgsqlDbType.Varchar,
                EmptyToDbNull(undo.PreviousLastName));
            command.Parameters.AddWithValue("branch_id", NpgsqlDbType.Integer,
                undo.PreviousBranchId is int branchId ? branchId : DBNull.Value);
            command.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, undo.RecordId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        throw new InvalidOperationException("Bilinmeyen dizin değişikliği geri alınamaz.");
    }

    private static void AddPatientParameters(
        NpgsqlCommand command,
        string tc,
        string firstName,
        string lastName,
        string gender,
        DateTime? birthDate)
    {
        command.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, tc);
        command.Parameters.AddWithValue("first_name", NpgsqlDbType.Varchar, firstName);
        command.Parameters.AddWithValue("last_name", NpgsqlDbType.Varchar, lastName);
        command.Parameters.AddWithValue("gender", NpgsqlDbType.Varchar, gender);
        command.Parameters.AddWithValue("birth_date", NpgsqlDbType.Date, birthDate is null ? DBNull.Value : birthDate.Value);
    }

    private static void AddDoctorIdentityParameters(
        NpgsqlCommand command,
        string diplomaRegistrationNo,
        string diplomaNo)
    {
        command.Parameters.AddWithValue(
            "diploma_registration_no", NpgsqlDbType.Varchar, diplomaRegistrationNo);
        command.Parameters.AddWithValue("diploma_no", NpgsqlDbType.Varchar, diplomaNo);
    }

    private static void AddDoctorParameters(
        NpgsqlCommand command,
        string diplomaRegistrationNo,
        string diplomaNo,
        string firstName,
        string lastName,
        int? branchId)
    {
        AddDoctorIdentityParameters(command, diplomaRegistrationNo, diplomaNo);
        command.Parameters.AddWithValue("first_name", NpgsqlDbType.Varchar, firstName);
        command.Parameters.AddWithValue("last_name", NpgsqlDbType.Varchar, lastName);
        command.Parameters.AddWithValue("branch_id", NpgsqlDbType.Integer,
            branchId is int value ? value : DBNull.Value);
    }

    private static IReadOnlyList<MedulaDirectoryField> PatientFields(
        string tc,
        string firstName,
        string lastName,
        string gender,
        DateTime? birthDate,
        bool valuesAreDisplayReady = false) =>
    [
        new("T.C. Kimlik No", tc),
        new("Hasta Adı", firstName),
        new("Hasta Soyadı", lastName),
        new("Cinsiyet", valuesAreDisplayReady ? gender : Display(gender)),
        new("Doğum Tarihi", valuesAreDisplayReady ? birthDate?.ToString() ?? "—" : Display(birthDate))
    ];

    private static IReadOnlyList<MedulaDirectoryField> PatientFields(
        string tc,
        string firstName,
        string lastName,
        string gender,
        string birthDate,
        bool valuesAreDisplayReady) =>
    [
        new("T.C. Kimlik No", tc),
        new("Hasta Adı", firstName),
        new("Hasta Soyadı", lastName),
        new("Cinsiyet", gender),
        new("Doğum Tarihi", birthDate)
    ];

    private static IReadOnlyList<MedulaDirectoryField> DoctorFields(
        string diplomaRegistrationNo,
        string diplomaNo,
        string firstName,
        string lastName,
        string? branchName,
        bool valuesAreDisplayReady = false) =>
    [
        new("Diploma Tescil No", diplomaRegistrationNo),
        new("Diploma No", diplomaNo),
        new("Hekim Adı", firstName),
        new("Hekim Soyadı", lastName),
        new("Branş", valuesAreDisplayReady ? Display(branchName ?? "") : Display(branchName ?? ""))
    ];

    private static string Transition(string oldValue, string newValue)
    {
        var oldDisplay = Display(oldValue);
        var newDisplay = Display(newValue);
        return Same(oldDisplay, newDisplay) ? newDisplay : $"{oldDisplay} → {newDisplay}";
    }

    private static async Task<BranchReference?> ResolveBranchAsync(
        NpgsqlConnection connection,
        string? specialty,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(specialty)) return null;
        var decoded = WebUtility.HtmlDecode(specialty).Trim();
        decoded = Regex.Replace(decoded, @"\s*\(Ana Branş\)\s*$", "", RegexOptions.IgnoreCase);
        var path = decoded.Split("->", 2, StringSplitOptions.TrimEntries);
        var parentName = path.Length == 2 ? path[0] : "";
        var branchName = path.Length == 2 ? path[1] : path[0];
        const string sql = """
            SELECT child.branch_id,
                   CASE WHEN parent.branch_id IS NULL THEN child.branch_name
                        ELSE parent.branch_name || ' / ' || child.branch_name END
            FROM public.branches child
            LEFT JOIN public.branches parent ON parent.branch_id=child.parent_branch_id
            WHERE LOWER(REPLACE(child.branch_name, ' ', ''))=LOWER(REPLACE(@branch_name, ' ', ''))
              AND (NULLIF(@parent_name, '') IS NULL OR LOWER(parent.branch_name)=LOWER(@parent_name))
            ORDER BY CASE WHEN NULLIF(@parent_name, '') IS NOT NULL THEN 0
                          WHEN child.parent_branch_id IS NULL THEN 1 ELSE 2 END,
                     child.display_order
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("branch_name", NpgsqlDbType.Varchar, branchName);
        command.Parameters.AddWithValue("parent_name", NpgsqlDbType.Varchar, parentName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BranchReference(reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private sealed record BranchReference(int Id, string Name);

    private static object EmptyToDbNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static DateTime? ParseDate(string value)
    {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        return DateTime.TryParse(value.Trim(), culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed.Date
            : null;
    }

    private static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string Display(DateTime? value) => value?.ToString("dd.MM.yyyy") ?? "—";
}
