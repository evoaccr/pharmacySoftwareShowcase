using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.StockMovements;

public enum QrMovementSearchMode
{
    None,
    QrCode,
    Barcode,
    MedicineNameContains,
    MedicineNameExact,
    BrandNameExact
}

public sealed record QrMovementQuery(
    DateTime StartDateTime,
    DateTime EndDateTimeExclusive,
    IReadOnlyList<short> MovementTypeIds,
    string Search = "",
    QrMovementSearchMode SearchMode = QrMovementSearchMode.None,
    string UserId = "",
    string MovementAddress = "",
    string MovementAddressType = "",
    bool IgnoreDateFilter = false,
    IReadOnlyList<string>? Barcodes = null);

public sealed record QrMovementSummary(
    long TotalCount,
    long DistinctQrCount,
    long DistinctMedicineCount,
    long EntryCount,
    long ExitCount)
{
    public static QrMovementSummary Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record QrMovementPage(
    IReadOnlyList<QrMovementItem> Items,
    QrMovementSummary? Summary);

public sealed class QrMovementRepository
{
    private const string ParsedBarcodeExpression = """
        COALESCE(
            active.barcode,
            inactive.barcode,
            CASE
                WHEN movement.qr_code ~ '^[0-9]{8,14}$' THEN movement.qr_code
                ELSE public.pharmacy_qr_barcode(movement.qr_code)
            END)
        """;

    public async Task<QrMovementPage> LoadPageAsync(
        QrMovementQuery query,
        DateTime? beforeMovementDate,
        int? beforeRecordId,
        int pageSize,
        bool includeSummary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.StartDateTime >= query.EndDateTimeExclusive)
            throw new ArgumentException("Start date must be earlier than the exclusive end date.", nameof(query));
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (beforeMovementDate.HasValue != beforeRecordId.HasValue)
            throw new ArgumentException("The movement cursor requires both date and record ID values.");
        if (query.MovementTypeIds.Count == 0)
            return new QrMovementPage([], includeSummary ? QrMovementSummary.Empty : null);

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        QrMovementSummary? summary = null;
        if (includeSummary)
        {
            var summarySql = $"""
                SELECT COUNT(*),
                       COUNT(DISTINCT movement.qr_code)
                           FILTER (WHERE NULLIF(BTRIM(movement.qr_code), '') IS NOT NULL),
                       COUNT(DISTINCT parsed.barcode)
                           FILTER (WHERE NULLIF(BTRIM(parsed.barcode), '') IS NOT NULL),
                       COUNT(*) FILTER (WHERE movement_type.is_entry),
                       COUNT(*) FILTER (WHERE NOT movement_type.is_entry)
                {BuildFromAndWhereSql(includeDisplayJoins: false)}
                """;
            await using var summaryCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create(summarySql, connection);
            AddFilterParameters(summaryCommand, query);
            await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
            if (await summaryReader.ReadAsync(cancellationToken))
            {
                summary = new QrMovementSummary(
                    summaryReader.GetInt64(0),
                    summaryReader.GetInt64(1),
                    summaryReader.GetInt64(2),
                    summaryReader.GetInt64(3),
                    summaryReader.GetInt64(4));
            }
        }

        var pageSql = $"""
            SELECT
                movement.record_id,
                movement.qr_code,
                parsed.barcode,
                COALESCE(NULLIF(product.medicine_name, ''),
                         NULLIF(product.brand_name, ''),
                         NULLIF(product.old_name, '')) AS medicine_name,
                product.record_id AS product_record_id,
                movement.movement_type,
                movement_type.code,
                movement_type.display_name,
                movement_type.is_entry,
                movement.movement_date,
                movement.movement_address,
                COALESCE(
                    NULLIF(BTRIM(CONCAT_WS(' ', patient.first_name, patient.last_name)), ''),
                    NULLIF(pharmacy.pharmacy_name, ''),
                    NULLIF(depot.depot_name, ''),
                    movement.movement_address) AS movement_address_name,
                CASE
                    WHEN patient.record_id IS NOT NULL THEN 'Patient'
                    WHEN pharmacy.record_id IS NOT NULL THEN 'Pharmacy'
                    WHEN depot.record_id IS NOT NULL THEN 'Depot'
                END AS movement_address_entity_type,
                COALESCE(patient.record_id::text, pharmacy.record_id::text, depot.record_id::text)
                    AS movement_address_record_key,
                movement.user_id,
                COALESCE(
                    NULLIF(BTRIM(CONCAT_WS(' ', app_user.first_name, app_user.last_name)), ''),
                    movement.user_id) AS user_name,
                movement.reference_code,
                movement.notes
            {BuildFromAndWhereSql(includeDisplayJoins: true)}
              AND (@cursor_date IS NULL
                   OR movement.movement_date < @cursor_date
                   OR (movement.movement_date = @cursor_date AND movement.record_id < @cursor_record_id))
            ORDER BY movement.movement_date DESC, movement.record_id DESC
            LIMIT @page_size
            """;

        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(pageSql, connection);
        AddFilterParameters(command, query);
        command.Parameters.Add(new NpgsqlParameter("cursor_date", NpgsqlDbType.Timestamp)
            { Value = beforeMovementDate.HasValue ? beforeMovementDate.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursor_record_id", NpgsqlDbType.Integer)
            { Value = beforeRecordId.HasValue ? beforeRecordId.Value : DBNull.Value });
        command.Parameters.AddWithValue("page_size", NpgsqlDbType.Integer, pageSize);

        var movements = new List<QrMovementItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            movements.Add(new QrMovementItem
            {
                RecordId = reader.GetInt32(0),
                QrCode = reader.GetString(1),
                Barcode = GetString(reader, 2),
                MedicineName = GetString(reader, 3),
                ProductRecordId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                MovementTypeId = reader.GetInt16(5),
                MovementTypeCode = reader.GetString(6),
                MovementTypeDisplay = reader.GetString(7),
                IsEntryDirection = reader.GetBoolean(8),
                MovementDate = reader.GetDateTime(9),
                MovementAddress = GetString(reader, 10),
                MovementAddressName = GetString(reader, 11),
                MovementAddressEntityType = GetString(reader, 12),
                MovementAddressRecordKey = GetString(reader, 13),
                UserId = GetString(reader, 14),
                UserName = GetString(reader, 15),
                ReferenceCode = GetString(reader, 16),
                Notes = GetString(reader, 17)
            });
        }

        return new QrMovementPage(movements, summary);
    }

    private static string BuildFromAndWhereSql(bool includeDisplayJoins)
    {
        var displayJoins = includeDisplayJoins
            ? """
              LEFT JOIN public.patient patient
                ON movement.movement_address = 'P' || patient.record_id::text
                OR movement.movement_address = 'P' || NULLIF(patient.tc_identity_no, '')
                OR movement.movement_address = NULLIF(patient.tc_identity_no, '')
              LEFT JOIN public.pharmacy pharmacy
                ON movement.movement_address = 'E' || COALESCE(NULLIF(pharmacy.gln_code, ''), pharmacy.record_id::text)
                OR movement.movement_address = NULLIF(pharmacy.gln_code, '')
              LEFT JOIN public.depot depot
                ON movement.movement_address = 'D' || COALESCE(NULLIF(depot.gln_code, ''), depot.record_id::text)
                OR movement.movement_address = NULLIF(depot.gln_code, '')
              LEFT JOIN public.users app_user ON app_user.user_id = movement.user_id
              """
            : string.Empty;

        return $"""
        FROM public."qrMovements" movement
        JOIN public."qrMovementTypes" movement_type
          ON movement_type.record_id = movement.movement_type
        LEFT JOIN public."qrActive" active ON active.qr_code = movement.qr_code
        LEFT JOIN public."qrInactive" inactive ON inactive.qr_code = movement.qr_code
        CROSS JOIN LATERAL (SELECT {ParsedBarcodeExpression} AS barcode) parsed
        LEFT JOIN public.barcode product ON product.barcode = parsed.barcode
        {displayJoins}
        WHERE (@ignore_date_filter OR (
                movement.movement_date >= @start_date
            AND movement.movement_date < @end_exclusive))
          AND movement.movement_type = ANY(@movement_type_ids)
          AND (@user_id = '' OR movement.user_id = @user_id)
          AND (@movement_address_type = '' OR movement.movement_address LIKE @movement_address_type || '%')
          AND (@movement_address = '' OR movement.movement_address = @movement_address)
          AND (cardinality(@barcodes) = 0 OR parsed.barcode = ANY(@barcodes))
          AND (
                @search_mode = 0
             OR (@search_mode = 1 AND movement.qr_code = @search)
             OR (@search_mode = 2 AND parsed.barcode = @search)
             OR (@search_mode = 3 AND (
                    COALESCE(product.medicine_name, '') ILIKE @name_search OR
                    COALESCE(product.brand_name, '') ILIKE @name_search OR
                    COALESCE(product.old_name, '') ILIKE @name_search))
             OR (@search_mode = 4 AND product.medicine_name = @search)
             OR (@search_mode = 5 AND product.brand_name = @search)
          )
        """;
    }

    private static void AddFilterParameters(NpgsqlCommand command, QrMovementQuery query)
    {
        command.Parameters.AddWithValue("start_date", NpgsqlDbType.Timestamp, query.StartDateTime);
        command.Parameters.AddWithValue("end_exclusive", NpgsqlDbType.Timestamp, query.EndDateTimeExclusive);
        command.Parameters.AddWithValue("ignore_date_filter", NpgsqlDbType.Boolean, query.IgnoreDateFilter);
        command.Parameters.AddWithValue(
            "movement_type_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Smallint,
            query.MovementTypeIds.ToArray());
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, query.UserId.Trim());
        command.Parameters.AddWithValue("movement_address_type", NpgsqlDbType.Varchar, query.MovementAddressType.Trim());
        command.Parameters.AddWithValue("movement_address", NpgsqlDbType.Varchar, query.MovementAddress.Trim());
        command.Parameters.AddWithValue(
            "barcodes",
            NpgsqlDbType.Array | NpgsqlDbType.Varchar,
            (query.Barcodes ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue("search_mode", NpgsqlDbType.Integer, (int)query.SearchMode);
        command.Parameters.AddWithValue("search", NpgsqlDbType.Varchar, query.Search.Trim());
        command.Parameters.AddWithValue("name_search", NpgsqlDbType.Varchar, $"%{query.Search.Trim()}%");
    }

    public async Task<IReadOnlyList<QrMovementTypeOption>> LoadMovementTypesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT record_id, code, display_name, is_entry
            FROM public."qrMovementTypes"
            ORDER BY record_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var options = new List<QrMovementTypeOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            options.Add(new QrMovementTypeOption(
                reader.GetInt16(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return options;
    }

    public async Task<IReadOnlyList<QrUserFilterOption>> LoadUsersAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT user_id,
                   COALESCE(
                       NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''),
                       user_id) || ' — ' || user_id AS display_text
            FROM public.users
            ORDER BY first_name NULLS LAST, last_name NULLS LAST, user_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var users = new List<QrUserFilterOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            users.Add(new QrUserFilterOption(reader.GetString(0), reader.GetString(1)));
        return users;
    }

    public async Task<IReadOnlyList<QrAddressFilterOption>> LoadAddressSuggestionsAsync(
        string addressType,
        string search,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(addressType) || string.IsNullOrWhiteSpace(search) || limit < 1)
            return [];

        var sql = addressType switch
        {
            "Patient" => """
                SELECT 'P' || COALESCE(NULLIF(tc_identity_no, ''), record_id::text),
                       COALESCE(NULLIF(BTRIM(CONCAT_WS(' ', first_name, last_name)), ''),
                                NULLIF(tc_identity_no, ''), record_id::text)
                       || COALESCE(' — ' || NULLIF(tc_identity_no, ''), '')
                FROM public.patient
                WHERE CONCAT_WS(' ', first_name, last_name) ILIKE @contains
                   OR COALESCE(tc_identity_no, '') ILIKE @prefix
                ORDER BY first_name NULLS LAST, last_name NULLS LAST
                LIMIT @limit
                """,
            "Pharmacy" => """
                SELECT 'E' || COALESCE(NULLIF(gln_code, ''), record_id::text),
                       COALESCE(NULLIF(pharmacy_name, ''), NULLIF(gln_code, ''), record_id::text)
                       || COALESCE(' — ' || NULLIF(gln_code, ''), '')
                FROM public.pharmacy
                WHERE COALESCE(pharmacy_name, '') ILIKE @contains
                   OR COALESCE(gln_code, '') ILIKE @prefix
                ORDER BY pharmacy_name NULLS LAST
                LIMIT @limit
                """,
            "Depot" => """
                SELECT 'D' || COALESCE(NULLIF(gln_code, ''), record_id::text),
                       COALESCE(NULLIF(depot_name, ''), NULLIF(gln_code, ''), record_id::text)
                       || COALESCE(' — ' || NULLIF(gln_code, ''), '')
                FROM public.depot
                WHERE COALESCE(depot_name, '') ILIKE @contains
                   OR COALESCE(gln_code, '') ILIKE @prefix
                ORDER BY depot_name NULLS LAST
                LIMIT @limit
                """,
            _ => string.Empty
        };
        if (sql.Length == 0) return [];

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        var term = search.Trim();
        command.Parameters.AddWithValue("contains", NpgsqlDbType.Varchar, $"%{term}%");
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Varchar, $"{term}%");
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        var options = new List<QrAddressFilterOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            options.Add(new QrAddressFilterOption(reader.GetString(0), reader.GetString(1)));
        return options;
    }

    public async Task<IReadOnlyList<QrSearchSuggestion>> LoadSuggestionsAsync(
        string search,
        int limitPerGroup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search) || limitPerGroup < 1) return [];

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH suggestions AS (
                SELECT
                    b.medicine_name AS display_text,
                    b.medicine_name AS filter_value,
                    4 AS search_mode,
                    false AS is_brand,
                    bool_or(COALESCE(active.quantity, 0) > 0) AS has_stock
                FROM public.barcode b
                LEFT JOIN public."barcodeActive" active ON active.barcode = b.barcode
                WHERE NULLIF(btrim(b.medicine_name), '') IS NOT NULL
                  AND b.medicine_name ILIKE @name_search
                GROUP BY b.medicine_name

                UNION ALL

                SELECT
                    b.brand_name,
                    b.brand_name,
                    5,
                    true,
                    bool_or(COALESCE(active.quantity, 0) > 0)
                FROM public.barcode b
                LEFT JOIN public."barcodeActive" active ON active.barcode = b.barcode
                WHERE NULLIF(btrim(b.brand_name), '') IS NOT NULL
                  AND b.brand_name ILIKE @name_search
                GROUP BY b.brand_name
            ), ranked AS (
                SELECT *, row_number() OVER (
                    PARTITION BY is_brand
                    ORDER BY CASE WHEN is_brand THEN false ELSE has_stock END DESC, display_text
                ) AS group_rank
                FROM suggestions
            )
            SELECT display_text, filter_value, search_mode, is_brand, has_stock
            FROM ranked
            WHERE group_rank <= @limit_per_group
            ORDER BY is_brand, group_rank
            """;

        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("name_search", NpgsqlDbType.Varchar, $"%{search.Trim()}%");
        command.Parameters.AddWithValue("limit_per_group", NpgsqlDbType.Integer, limitPerGroup);
        var suggestions = new List<QrSearchSuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            suggestions.Add(new QrSearchSuggestion(
                reader.GetString(0),
                reader.GetString(1),
                (QrMovementSearchMode)reader.GetInt32(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }
        return suggestions;
    }

    public async Task<BarcodeSearchResolution?> ResolveBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT barcode,
                   COALESCE(NULLIF(medicine_name, ''), NULLIF(brand_name, ''),
                            NULLIF(old_name, ''), barcode) AS display_name
            FROM public.barcode
            WHERE barcode = @barcode
            LIMIT 1
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, barcode.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BarcodeSearchResolution(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static string? GetString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

}
