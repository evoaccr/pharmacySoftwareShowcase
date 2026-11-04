using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Cards;

public sealed class CardRepository
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly HashSet<string> TitleCaseListKeys =
        ["DisplayName", "FirstName", "LastName", "FacilityName", "PharmacyName", "PharmacistName", "Name", "InstitutionName"];

    public async Task<string?> ResolveRelatedCardKeyAsync(CardEntityKind kind, string identity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var sql = kind switch
        {
            CardEntityKind.Patients => """
                SELECT p.record_id::text FROM public.patient p
                WHERE p.tc_identity_no=@identity OR EXISTS (
                    SELECT 1 FROM public.patient_identity i WHERE i.patient_record_id=p.record_id AND i.tc_identity_no=@identity)
                ORDER BY p.record_id LIMIT 1
                """,
            CardEntityKind.Doctors => "SELECT record_id::text FROM public.doctor WHERE diploma_registration_no=@identity ORDER BY record_id LIMIT 1",
            CardEntityKind.Faculties => "SELECT record_id::text FROM public.faculty WHERE faculty_code=@identity ORDER BY record_id LIMIT 1",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("identity", NpgsqlDbType.Varchar, identity.Trim());
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
    public async Task<CardListPage> LoadListPageAsync(
        CardEntityDefinition definition,
        CardListQuery query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(limit));
        var listSql = GetListSql(definition.Kind);
        var orderIndex = listSql.LastIndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        if (orderIndex < 0) throw new InvalidOperationException("Kart liste sıralaması bulunamadı.");
        var baseSql = listSql[..orderIndex].TrimEnd();
        var conditions = BuildListConditions(definition, query);
        var where = conditions.Count == 0 ? "" : $"WHERE {string.Join(" AND ", conditions)}";
        var sql = $"""
            SELECT filtered.*, COUNT(*) OVER() AS "__TotalCount"
            FROM ({baseSql}) filtered
            {where}
            ORDER BY {GetListOrderSql(definition, query)}
            LIMIT @limit OFFSET @offset
            """;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CardSchema.EnsureAsync(connection, cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        AddListParameters(command, query);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CardRow>();
        var totalCount = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 1; index < reader.FieldCount - 1; index++)
                values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            foreach (var key in TitleCaseListKeys.Where(values.ContainsKey).ToList())
                if (values[key] is string text) values[key] = TitleCase(text);
            rows.Add(new CardRow(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!, values));
            totalCount = Convert.ToInt32(reader.GetValue(reader.FieldCount - 1), CultureInfo.InvariantCulture);
        }
        return new CardListPage(rows, totalCount);
    }

    public async Task<IReadOnlyList<string>> LoadListFilterOptionsAsync(
        CardEntityKind kind,
        string filter,
        CancellationToken cancellationToken)
    {
        var sql = (kind, filter) switch
        {
            (CardEntityKind.Patients, "institution") => """
                SELECT DISTINCT k.kurum_name
                FROM public.patient p JOIN public.kurum k ON k.record_id=p.kurum_id
                WHERE NULLIF(BTRIM(k.kurum_name),'') IS NOT NULL ORDER BY k.kurum_name
                """,
            (CardEntityKind.Doctors, "specialty") => """
                SELECT DISTINCT CASE WHEN parent.branch_id IS NULL THEN branch.branch_name
                                     ELSE parent.branch_name || ' / ' || branch.branch_name END AS specialty
                FROM public.doctor d
                JOIN public.branches branch ON branch.branch_id=d.branch_id
                LEFT JOIN public.branches parent ON parent.branch_id=branch.parent_branch_id
                ORDER BY specialty
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(0)) result.Add(TitleCase(reader.GetString(0)));
        return result;
    }

    public async Task<IReadOnlyList<string>> SearchListFilterOptionsAsync(
        CardEntityKind kind,
        string filter,
        string term,
        CancellationToken cancellationToken)
    {
        var sql = (kind, filter) switch
        {
            (CardEntityKind.Patients, "family") => """
                SELECT family_group_name FROM public.family_group
                WHERE family_group_name ILIKE @term || '%'
                ORDER BY family_group_name LIMIT 20
                """,
            (CardEntityKind.Doctors, "facility") => """
                SELECT DISTINCT COALESCE(f.faculty_name,d.facility_no) AS facility
                FROM public.doctor d LEFT JOIN public.faculty f ON f.faculty_code=d.facility_no
                WHERE COALESCE(f.faculty_name,d.facility_no) ILIKE '%' || @term || '%'
                ORDER BY facility LIMIT 20
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("term", NpgsqlDbType.Varchar, term.Trim());
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(0)) result.Add(TitleCase(reader.GetString(0)));
        return result;
    }

    public async Task<Dictionary<string, object?>> LoadValuesAsync(CardEntityDefinition definition, string key, CancellationToken cancellationToken)
    {
        var fields = definition.FieldsFor(false);
        var sql = $"SELECT {string.Join(", ", fields.Select(field => Quote(field.ColumnName)))} FROM public.{Quote(definition.TableName)} WHERE {Quote(definition.KeyColumn)} = @key";
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("key", ConvertKey(definition, key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"{definition.SingularName} kaydı bulunamadı.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < fields.Count; index++) values[fields[index].ColumnName] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return values;
    }

    public Task<IReadOnlyList<CardLookupOption>> SearchLookupOptionsAsync(CardEntityKind target, string term, CancellationToken cancellationToken) =>
        QueryLookupOptionsAsync(target, term, null, cancellationToken);

    public async Task<CardLookupOption?> LoadLookupOptionAsync(CardEntityKind target, string key, CancellationToken cancellationToken) =>
        (await QueryLookupOptionsAsync(target, null, key, cancellationToken)).FirstOrDefault();

    private async Task<IReadOnlyList<CardLookupOption>> QueryLookupOptionsAsync(CardEntityKind target, string? term, string? exactKey, CancellationToken cancellationToken)
    {
        var sql = target switch
        {
            CardEntityKind.Doctors => exactKey is null ? """
                SELECT diploma_registration_no, COALESCE(NULLIF(TRIM(CONCAT_WS(' ', first_name, last_name)), ''), diploma_registration_no)
                FROM public.doctor
                WHERE diploma_registration_no IS NOT NULL
                  AND (diploma_registration_no ILIKE '%' || @term || '%' OR CONCAT_WS(' ', first_name, last_name) ILIKE '%' || @term || '%')
                ORDER BY first_name NULLS LAST, last_name NULLS LAST LIMIT 25
                """ : """
                SELECT diploma_registration_no, COALESCE(NULLIF(TRIM(CONCAT_WS(' ', first_name, last_name)), ''), diploma_registration_no)
                FROM public.doctor WHERE diploma_registration_no = @key LIMIT 1
                """,
            CardEntityKind.Faculties => exactKey is null ? """
                SELECT faculty_code, COALESCE(faculty_name, faculty_code)
                FROM public.faculty
                WHERE faculty_code IS NOT NULL AND (faculty_name ILIKE '%' || @term || '%' OR faculty_code ILIKE @term || '%')
                ORDER BY faculty_name NULLS LAST LIMIT 25
                """ : """
                SELECT faculty_code, COALESCE(faculty_name, faculty_code)
                FROM public.faculty WHERE faculty_code = @key LIMIT 1
                """,
            CardEntityKind.Institutions => exactKey is null ? """
                SELECT record_id::text, COALESCE(kurum_name, kurum_code, record_id::text)
                FROM public.kurum
                WHERE (kurum_name ILIKE '%' || @term || '%'
                   OR kurum_code ILIKE @term || '%'
                   OR (LOWER(@term) = 'sgk' AND UPPER(kurum_code) = 'GSS'))
                ORDER BY CASE WHEN LOWER(@term) = 'sgk' AND UPPER(kurum_code) = 'GSS' THEN 0 ELSE 1 END,
                         kurum_name NULLS LAST LIMIT 25
                """ : """
                SELECT record_id::text, COALESCE(kurum_name, kurum_code, record_id::text)
                FROM public.kurum WHERE record_id = @key::integer LIMIT 1
                """,
            CardEntityKind.Branches => exactKey is null ? """
                SELECT child.branch_id::text,
                       CASE WHEN parent.branch_id IS NULL THEN child.branch_name
                            ELSE parent.branch_name || ' / ' || child.branch_name END
                FROM public.branches child
                LEFT JOIN public.branches parent ON parent.branch_id=child.parent_branch_id
                WHERE child.branch_name ILIKE '%' || @term || '%'
                   OR parent.branch_name ILIKE '%' || @term || '%'
                ORDER BY child.display_order LIMIT 50
                """ : """
                SELECT child.branch_id::text,
                       CASE WHEN parent.branch_id IS NULL THEN child.branch_name
                            ELSE parent.branch_name || ' / ' || child.branch_name END
                FROM public.branches child
                LEFT JOIN public.branches parent ON parent.branch_id=child.parent_branch_id
                WHERE child.branch_id=@key::integer LIMIT 1
                """,
            CardEntityKind.FacultyTypes => exactKey is null ? """
                SELECT faculty_type_id::text, faculty_type_name
                FROM public.faculty_type
                WHERE faculty_type_name ILIKE '%' || @term || '%'
                   OR faculty_type_id::text=@term
                ORDER BY faculty_type_id LIMIT 50
                """ : """
                SELECT faculty_type_id::text, faculty_type_name
                FROM public.faculty_type WHERE faculty_type_id=@key::integer LIMIT 1
                """,
            CardEntityKind.FamilyGroups => exactKey is null ? """
                SELECT record_id::text, family_group_name
                FROM public.family_group
                WHERE family_group_name ILIKE '%' || @term || '%'
                ORDER BY family_group_name LIMIT 25
                """ : """
                SELECT record_id::text, family_group_name
                FROM public.family_group WHERE record_id=@key::integer LIMIT 1
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Bu kart türü arama listesi olarak kullanılamaz.")
        };
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        if (exactKey is null) command.Parameters.AddWithValue("term", NpgsqlDbType.Varchar, term!);
        else command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, exactKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var options = new List<CardLookupOption>();
        while (await reader.ReadAsync(cancellationToken)) options.Add(new CardLookupOption(reader.GetString(0), TitleCase(reader.GetString(1))));
        return options;
    }

    public async Task<string> SaveAsync(CardEntityDefinition definition, string? originalKey, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken)
    {
        var editableFields = definition.Fields.Where(field => values.ContainsKey(field.ColumnName)).ToList();
        string sql;
        if (originalKey is null)
        {
            var columns = editableFields.Select(field => Quote(field.ColumnName)).ToList();
            var parameters = editableFields.Select((_, index) => $"@value_{index}").ToList();
            if (definition.IsUsers)
            {
                columns.AddRange(["entry_date", "user_points", "status"]);
                parameters.AddRange(["CURRENT_DATE", "0", "TRUE"]);
            }
            sql = $"INSERT INTO public.{Quote(definition.TableName)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameters)}) RETURNING {Quote(definition.KeyColumn)}";
        }
        else
        {
            var assignments = editableFields.Select((field, index) => $"{Quote(field.ColumnName)} = @value_{index}");
            sql = $"UPDATE public.{Quote(definition.TableName)} SET {string.Join(", ", assignments)} WHERE {Quote(definition.KeyColumn)} = @original_key RETURNING {Quote(definition.KeyColumn)}";
        }
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        for (var index = 0; index < editableFields.Count; index++)
        {
            var field = editableFields[index];
            command.Parameters.Add(new NpgsqlParameter($"value_{index}", GetParameterType(field)) { Value = values[field.ColumnName] ?? DBNull.Value });
        }
        if (originalKey is not null) command.Parameters.AddWithValue("original_key", ConvertKey(definition, originalKey));
        var result = await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Kayıt kaydedilemedi.");
        return Convert.ToString(result, CultureInfo.InvariantCulture)!;
    }

    public async Task<int> CreateFamilyGroupAsync(
        string name,
        int? mainPatientId,
        IReadOnlyCollection<int> memberIds,
        CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Aile grubu adı zorunludur.");
        var members = memberIds.Distinct().ToList();
        if (mainPatientId is int main)
        {
            members.Remove(main);
            members.Insert(0, main);
        }
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                INSERT INTO public.family_group(family_group_name, main_patient_id, member_ids)
                VALUES (@name, @main_patient_id, @member_ids)
                RETURNING record_id
                """;
            await using var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
            insert.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
            insert.Parameters.Add(new NpgsqlParameter("main_patient_id", NpgsqlDbType.Integer) { Value = mainPatientId ?? (object)DBNull.Value });
            insert.Parameters.AddWithValue("member_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, members.ToArray());
            var groupId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            foreach (var patientId in members)
            {
                await using var update = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                    "UPDATE public.patient SET family_group_id=@group_id WHERE record_id=@patient_id", connection, transaction);
                update.Parameters.AddWithValue("group_id", NpgsqlDbType.Integer, groupId);
                update.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Aile grubu üyesi bulunamadı.");
            }
            await transaction.CommitAsync(cancellationToken);
            return groupId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task<IReadOnlyList<CardLookupOption>> SearchPatientsAsync(string term, CancellationToken cancellationToken) =>
        SearchPatientOptionsAsync(term, null, cancellationToken);

    public Task<CardLookupOption?> LoadPatientOptionAsync(int patientId, CancellationToken cancellationToken) =>
        LoadPatientOptionCoreAsync(patientId, cancellationToken);

    public async Task<FamilyGroupDetail?> LoadFamilyGroupDetailAsync(int familyGroupId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT fg.record_id, fg.family_group_name, fg.main_patient_id,
                   member.record_id,
                   COALESCE(NULLIF(BTRIM(CONCAT_WS(' ',member.first_name,member.last_name)),''),
                            member.tc_identity_no,member.record_id::text) AS member_name
            FROM public.family_group fg
            LEFT JOIN LATERAL unnest(fg.member_ids) WITH ORDINALITY listed(patient_id,position) ON TRUE
            LEFT JOIN public.patient member ON member.record_id=listed.patient_id
            WHERE fg.record_id=@family_group_id
            ORDER BY CASE WHEN member.record_id=fg.main_patient_id THEN 0 ELSE 1 END,
                     listed.position, member.record_id
            """;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("family_group_id", NpgsqlDbType.Integer, familyGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<CardLookupOption>();
        int? recordId = null;
        int? mainPatientId = null;
        string? name = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            recordId ??= reader.GetInt32(0);
            name ??= reader.GetString(1);
            if (!reader.IsDBNull(2)) mainPatientId ??= reader.GetInt32(2);
            if (!reader.IsDBNull(3))
                members.Add(new CardLookupOption(reader.GetInt32(3).ToString(CultureInfo.InvariantCulture), TitleCase(reader.GetString(4))));
        }
        if (recordId is null || name is null) return null;
        var mainPerson = mainPatientId is null
            ? null
            : members.FirstOrDefault(member => member.Key == mainPatientId.Value.ToString(CultureInfo.InvariantCulture));
        return new FamilyGroupDetail(recordId.Value, TitleCase(name), mainPerson, members);
    }

    private async Task<CardLookupOption?> LoadPatientOptionCoreAsync(int patientId, CancellationToken cancellationToken) =>
        (await SearchPatientOptionsAsync(null, patientId, cancellationToken)).FirstOrDefault();

    private async Task<IReadOnlyList<CardLookupOption>> SearchPatientOptionsAsync(string? term, int? patientId, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var sql = patientId is null ? """
            SELECT record_id::text, COALESCE(NULLIF(BTRIM(CONCAT_WS(' ',first_name,last_name)),''),tc_identity_no,record_id::text)
            FROM public.patient
            WHERE CONCAT_WS(' ',first_name,last_name) ILIKE '%' || @term || '%'
               OR tc_identity_no ILIKE @term || '%'
            ORDER BY first_name NULLS LAST,last_name NULLS LAST LIMIT 25
            """ : """
            SELECT record_id::text, COALESCE(NULLIF(BTRIM(CONCAT_WS(' ',first_name,last_name)),''),tc_identity_no,record_id::text)
            FROM public.patient WHERE record_id=@patient_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        if (patientId is null) command.Parameters.AddWithValue("term", NpgsqlDbType.Varchar, term!);
        else command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId.Value);
        var result = new List<CardLookupOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new CardLookupOption(reader.GetInt32(0).ToString(CultureInfo.InvariantCulture), TitleCase(reader.GetString(1))));
        return result;
    }

    public async Task DeleteAsync(CardEntityDefinition definition, string key, CancellationToken cancellationToken)
    {
        var sql = definition.IsUsers
            ? "UPDATE public.users SET status = FALSE, exit_date = CURRENT_DATE WHERE user_id = @key"
            : $"DELETE FROM public.{Quote(definition.TableName)} WHERE {Quote(definition.KeyColumn)} = @key";
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("key", ConvertKey(definition, key));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool?> FindUserActiveStatusAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("SELECT status FROM public.users WHERE user_id = @user_id", connection);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return reader.IsDBNull(0) || reader.GetBoolean(0);
    }

    public async Task ReactivateUserAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("UPDATE public.users SET status=TRUE, entry_date=CURRENT_DATE, exit_date=NULL WHERE user_id=@user_id AND status IS FALSE", connection);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId.Trim());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Pasif kullanıcı yeniden etkinleştirilemedi.");
    }

    public async Task<decimal> AdjustPatientDebtAsync(
        int patientId,
        decimal amount,
        bool add,
        CancellationToken cancellationToken)
    {
        if (amount <= 0) throw new InvalidOperationException("Borç değişim miktarı sıfırdan büyük olmalıdır.");
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var lockCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "SELECT COALESCE(debt_amount,0) FROM public.patient WHERE record_id=@patient_id FOR UPDATE",
                connection,
                transaction);
            lockCommand.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
            var currentResult = await lockCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Hasta kaydı bulunamadı.");
            var currentDebt = Convert.ToDecimal(currentResult, CultureInfo.InvariantCulture);
            if (!add && amount > currentDebt)
                throw new InvalidOperationException("Borçtan düşülecek miktar mevcut borçtan fazla olamaz.");
            var newDebt = add ? currentDebt + amount : currentDebt - amount;
            await using var update = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "UPDATE public.patient SET debt_amount=@new_debt WHERE record_id=@patient_id",
                connection,
                transaction);
            update.Parameters.AddWithValue("new_debt", NpgsqlDbType.Numeric, newDebt);
            update.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return newDebt;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static List<string> BuildListConditions(CardEntityDefinition definition, CardListQuery query)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            if (definition.IsPatients && query.Search.All(char.IsDigit))
                conditions.Add("COALESCE(\"TcIdentityNo\",'') LIKE @search || '%'");
            else if (definition.IsPatients)
                conditions.Add(WordPrefixCondition("SearchName", "search"));
            else if (definition.IsDoctors)
                conditions.Add($"({StartsWithCondition("SearchCode", "search")} OR {ContainsCondition("SearchName", "search")})");
            else if (definition.IsUsers)
                conditions.Add(StartsWithCondition("SearchCode", "search"));
            else
                conditions.Add($"({StartsWithCondition("SearchCode", "search")} OR {ContainsCondition("SearchName", "search")})");
        }
        if (!string.IsNullOrWhiteSpace(query.SecondarySearch))
            conditions.Add(definition.IsDoctors
                ? StartsWithCondition("SecondaryCode", "secondary_search")
                : WordPrefixCondition("SecondaryName", "secondary_search"));
        if (query.MinimumAge is not null) conditions.Add("\"Age\" >= @minimum_age");
        if (query.MaximumAge is not null) conditions.Add("\"Age\" <= @maximum_age");
        if (query.DebtState == "Borçlu") conditions.Add("COALESCE(\"DebtAmount\",0) > 0");
        else if (query.DebtState == "Borçsuz") conditions.Add("COALESCE(\"DebtAmount\",0) = 0");
        if (query.ConsignmentState == "Emaneti Var") conditions.Add("COALESCE(\"ConsignedAmount\",0) > 0");
        else if (query.ConsignmentState == "Emaneti Yok") conditions.Add("COALESCE(\"ConsignedAmount\",0) = 0");
        if (!string.IsNullOrWhiteSpace(query.Institution)) conditions.Add(EqualsCondition("InstitutionName", "institution"));
        if (!string.IsNullOrWhiteSpace(query.FamilyGroup)) conditions.Add(StartsWithCondition("FamilyGroupName", "family_group"));
        if (!string.IsNullOrWhiteSpace(query.Specialty)) conditions.Add(EqualsCondition("Specialty", "specialty"));
        if (!string.IsNullOrWhiteSpace(query.Facility)) conditions.Add(ContainsCondition("FacilityName", "facility"));
        if (query.PharmacyDebtOnly) conditions.Add("COALESCE(\"DebtBalance\",0) <> 0");
        return conditions;
    }

    private static void AddListParameters(NpgsqlCommand command, CardListQuery query)
    {
        AddTextParameter(command, "search", query.Search);
        AddTextParameter(command, "secondary_search", query.SecondarySearch);
        if (query.MinimumAge is int minimumAge) command.Parameters.AddWithValue("minimum_age", NpgsqlDbType.Integer, minimumAge);
        if (query.MaximumAge is int maximumAge) command.Parameters.AddWithValue("maximum_age", NpgsqlDbType.Integer, maximumAge);
        AddTextParameter(command, "institution", query.Institution);
        AddTextParameter(command, "family_group", query.FamilyGroup);
        AddTextParameter(command, "specialty", query.Specialty);
        AddTextParameter(command, "facility", query.Facility);
    }

    private static void AddTextParameter(NpgsqlCommand command, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            command.Parameters.AddWithValue(name, NpgsqlDbType.Varchar, value.Trim());
    }

    private static string NormalizeSql(string column) =>
        $"translate(upper(COALESCE(\"{column}\",'')),'İ','I')";

    private static string StartsWithCondition(string column, string parameter) =>
        $"{NormalizeSql(column)} LIKE translate(upper(@{parameter}),'İ','I') || '%'";

    private static string ContainsCondition(string column, string parameter) =>
        $"{NormalizeSql(column)} LIKE '%' || translate(upper(@{parameter}),'İ','I') || '%'";

    private static string EqualsCondition(string column, string parameter) =>
        $"{NormalizeSql(column)} = translate(upper(@{parameter}),'İ','I')";

    private static string WordPrefixCondition(string column, string parameter) => $"""
        NOT EXISTS (
            SELECT 1 FROM unnest(regexp_split_to_array(BTRIM(@{parameter}), '\s+')) search_term
            WHERE NOT EXISTS (
                SELECT 1 FROM unnest(regexp_split_to_array(COALESCE("{column}",''), '\s+')) source_word
                WHERE translate(upper(source_word),'İ','I') LIKE translate(upper(search_term),'İ','I') || '%'))
        """;

    private static string GetListOrderSql(CardEntityDefinition definition, CardListQuery query)
    {
        var defaultOrder = GetDefaultListOrderSql(definition.Kind);
        if (string.IsNullOrWhiteSpace(query.SortColumn)) return defaultOrder;
        var column = definition.Columns.FirstOrDefault(candidate => candidate.Key == query.SortColumn);
        if (column is null) return defaultOrder;
        var sortKey = column.SortKey ?? column.Key;
        var direction = query.SortDescending ? "DESC" : "ASC";
        return $"{Quote(sortKey)} {direction} NULLS LAST, {defaultOrder}";
    }

    private static string GetDefaultListOrderSql(CardEntityKind kind) => kind switch
    {
        CardEntityKind.Users => "\"Status\" DESC NULLS LAST, user_id",
        CardEntityKind.Doctors => "\"FirstName\" NULLS LAST, \"LastName\" NULLS LAST, record_id",
        CardEntityKind.Pharmacies => "\"PharmacyName\" NULLS LAST, record_id",
        CardEntityKind.Faculties or CardEntityKind.Institutions or CardEntityKind.Depots => "\"Name\" NULLS LAST, record_id",
        CardEntityKind.Patients => "\"FirstName\" NULLS LAST, \"LastName\" NULLS LAST, record_id",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string GetListSql(CardEntityKind kind) => kind switch
    {
        CardEntityKind.Users => """
            SELECT user_id,
                   COALESCE(NULLIF(TRIM(CONCAT_WS(' ', first_name, last_name)), ''), user_id) AS "DisplayName",
                   user_id AS "UserId", first_name AS "FirstName", last_name AS "LastName",
                   permisson_level AS "PermissionLevel", COALESCE(user_points,0) AS "UserPoints", NULL::text AS "Active",
                   status AS "Status", CASE WHEN status IS TRUE THEN 'Aktif' WHEN status IS FALSE THEN 'Pasif' ELSE NULL END AS "StatusDisplay",
                   user_id AS "SearchCode", CONCAT_WS(' ', first_name, last_name) AS "SearchName"
            FROM public.users ORDER BY status DESC NULLS LAST, user_id
            """,
        CardEntityKind.Doctors => """
            SELECT d.record_id,
                   COALESCE(NULLIF(TRIM(CONCAT_WS(' ',d.first_name,d.last_name)),''),d.diploma_registration_no,d.record_id::text) AS "DisplayName",
                   d.first_name AS "FirstName", d.last_name AS "LastName",
                   CASE WHEN parent.branch_id IS NULL THEN branch.branch_name
                        ELSE parent.branch_name || ' / ' || branch.branch_name END AS "Specialty",
                   COALESCE(f.faculty_name,d.facility_no) AS "FacilityName",
                   d.diploma_registration_no AS "SearchCode", d.diploma_no AS "SecondaryCode", CONCAT_WS(' ',d.first_name,d.last_name) AS "SearchName"
            FROM public.doctor d
            LEFT JOIN public.faculty f ON f.faculty_code=d.facility_no
            LEFT JOIN public.branches branch ON branch.branch_id=d.branch_id
            LEFT JOIN public.branches parent ON parent.branch_id=branch.parent_branch_id
            ORDER BY d.first_name NULLS LAST,d.last_name NULLS LAST,d.record_id
            """,
        CardEntityKind.Pharmacies => """
            SELECT record_id, COALESCE(pharmacy_name,gln_code,record_id::text) AS "DisplayName",
                   pharmacy_name AS "PharmacyName", pharmacist_name AS "PharmacistName",
                   debt_balance AS "DebtBalance", gln_code AS "SearchCode", pharmacy_name AS "SearchName", pharmacist_name AS "SecondaryName",
                   gln_code AS "MovementKey"
            FROM public.pharmacy ORDER BY pharmacy_name NULLS LAST,record_id
            """,
        CardEntityKind.Faculties => """
            SELECT record_id, COALESCE(faculty_name,faculty_code,record_id::text) AS "DisplayName",
                   faculty_code AS "Code", faculty_name AS "Name", ft.faculty_type_name AS "FacultyType",
                   faculty_code AS "SearchCode", faculty_name AS "SearchName"
            FROM public.faculty f LEFT JOIN public.faculty_type ft ON ft.faculty_type_id=f.faculty_type_id
            ORDER BY faculty_name NULLS LAST,record_id
            """,
        CardEntityKind.Institutions => """
            SELECT record_id, COALESCE(kurum_name,kurum_code,record_id::text) AS "DisplayName",
                   kurum_code AS "Code", kurum_name AS "Name", kurum_code AS "SearchCode", kurum_name AS "SearchName"
            FROM public.kurum ORDER BY kurum_name NULLS LAST,record_id
            """,
        CardEntityKind.Depots => """
            SELECT record_id, COALESCE(depot_name,gln_code,record_id::text) AS "DisplayName",
                   gln_code AS "Code", depot_name AS "Name", debt_balance AS "DebtBalance",
                   gln_code AS "SearchCode", depot_name AS "SearchName"
            FROM public.depot ORDER BY depot_name NULLS LAST,record_id
            """,
        CardEntityKind.Patients => """
            SELECT p.record_id, COALESCE(NULLIF(TRIM(CONCAT_WS(' ',p.first_name,p.last_name)),''),p.tc_identity_no,p.record_id::text) AS "DisplayName",
                   p.is_loyal AS "IsLoyal", CASE WHEN p.is_loyal THEN '✓' ELSE '✕' END AS "LoyalDisplay",
                   p.first_name AS "FirstName", p.last_name AS "LastName", COALESCE(p.alive,false) AS "Alive",
                   CASE WHEN p.date_of_birth IS NULL THEN NULL ELSE EXTRACT(YEAR FROM age(CURRENT_DATE,p.date_of_birth))::int END AS "Age",
                   CASE WHEN BTRIM(COALESCE(p.patient_type,''))='' THEN NULL
                   ELSE CASE translate(upper(BTRIM(p.patient_type)),'İ','I')
                       WHEN '1' THEN 'Çalışan' WHEN '2' THEN 'Emekli' WHEN '3' THEN 'Diğer'
                       WHEN 'ÇALIŞAN' THEN 'Çalışan' WHEN 'EMEKLI' THEN 'Emekli' ELSE 'Diğer'
                   END END AS "PatientTypeDisplay",
                   CASE WHEN BTRIM(COALESCE(p.patient_type,''))='' THEN NULL
                   ELSE CASE translate(upper(BTRIM(p.patient_type)),'İ','I')
                       WHEN '1' THEN 1 WHEN 'ÇALIŞAN' THEN 1 WHEN '2' THEN 2 WHEN 'EMEKLI' THEN 2 ELSE 3
                   END END AS "PatientTypeSort",
                   primary_phone.value AS "Phone", COALESCE(p.prescription_count,0) AS "PrescriptionCount",
                   NULL::integer AS "UpcomingMedicationCount", COALESCE(p.debt_amount,0) AS "DebtAmount",
                   CASE WHEN p.allow_debt THEN '✓ ' ELSE '✕ ' END || TO_CHAR(COALESCE(p.debt_amount,0),'FM999999990.00') AS "DebtDisplay",
                   p.allow_debt AS "AllowDebt", COALESCE(cardinality(p.consigned_qr_codes),0) AS "ConsignedAmount",
                   COALESCE(cardinality(p.consigned_qr_codes),0) <> 0 AS "HasConsignment", p.note AS "Notes",
                   p.tc_identity_no AS "TcIdentityNo", CONCAT_WS(' ',p.first_name,p.last_name) AS "SearchName",
                   p.kurum_id AS "InstitutionId", k.kurum_name AS "InstitutionName",
                   p.family_group_id AS "FamilyGroupId", fg.family_group_name AS "FamilyGroupName",
                   EXISTS (SELECT 1 FROM public.users employee
                           WHERE NULLIF(BTRIM(employee.tc_identity_no),'')=NULLIF(BTRIM(p.tc_identity_no),'')) AS "IsEmployee",
                   p.tc_identity_no AS "MovementKey"
            FROM public.patient p LEFT JOIN public.kurum k ON k.record_id=p.kurum_id
            LEFT JOIN public.family_group fg ON fg.record_id=p.family_group_id
            LEFT JOIN LATERAL (
                SELECT phone.value
                FROM unnest(COALESCE(p.phone_numbers,ARRAY[]::varchar[])) WITH ORDINALITY phone(value,position)
                WHERE regexp_replace(phone.value,'\D','','g') ~ '^0[0-9]{10}$'
                ORDER BY phone.position LIMIT 1
            ) primary_phone ON TRUE
            ORDER BY p.first_name NULLS LAST,p.last_name NULLS LAST,p.record_id
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static object ConvertKey(CardEntityDefinition definition, string key) => definition.KeyKind == CardFieldKind.Integer
        ? int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException("Kayıt kimliği tam sayı olmalıdır.")
        : key;

    private static NpgsqlDbType GetParameterType(CardFieldDefinition field) => field.Kind switch
    {
        CardFieldKind.Integer or CardFieldKind.CareLevel => NpgsqlDbType.Integer,
        CardFieldKind.Lookup when field.LookupTarget is CardEntityKind.Institutions or CardEntityKind.Branches or CardEntityKind.FacultyTypes or CardEntityKind.FamilyGroups => NpgsqlDbType.Integer,
        CardFieldKind.FamilyGroup => NpgsqlDbType.Integer,
        CardFieldKind.Decimal => NpgsqlDbType.Numeric,
        CardFieldKind.Date => NpgsqlDbType.Date,
        CardFieldKind.Boolean => NpgsqlDbType.Boolean,
        CardFieldKind.TextArray or CardFieldKind.PhoneList or CardFieldKind.EmailList or CardFieldKind.UtsCompanyList => NpgsqlDbType.Array | NpgsqlDbType.Varchar,
        CardFieldKind.Photo => NpgsqlDbType.Bytea,
        _ => NpgsqlDbType.Varchar
    };

    private static string Quote(string identifier)
    {
        var quote = ((char)34).ToString();
        return quote + identifier.Replace(quote, quote + quote) + quote;
    }

    private static string TitleCase(string value) => TurkishCulture.TextInfo.ToTitleCase(value.ToLower(TurkishCulture));
}
