using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.StockMovements;

public enum StockMovementDirection
{
    In,
    Out
}

public enum StockItsNotificationKind
{
    Sale,
    SaleCancellation,
    Acceptance
}

public sealed record UnnotifiedItsMovementItem(
    string QrCode,
    IReadOnlyList<int> RecordIds,
    IReadOnlyList<string> ReferenceNumbers,
    StockMovementDirection Direction,
    StockItsNotificationKind NotificationKind,
    DateTime CreatedAt)
{
    public int Count => RecordIds.Count;
    public string ReferenceDisplay => string.Join(", ", ReferenceNumbers.Distinct(StringComparer.Ordinal));
    public string DirectionDisplay => Direction == StockMovementDirection.In ? "Giriş" : "Çıkış";
    public string NotificationDisplay => NotificationKind switch
    {
        StockItsNotificationKind.Sale => "Satış",
        StockItsNotificationKind.SaleCancellation => "Satış İptal",
        StockItsNotificationKind.Acceptance => "Mal Alım",
        _ => "—"
    };
    public string CreatedAtDisplay => CreatedAt.ToString("dd.MM.yyyy HH:mm:ss");
}

public sealed record UnnotifiedItsMovementRecord(
    int RecordId,
    string QrCode,
    string ReferenceNumber,
    StockMovementDirection Direction,
    StockItsNotificationKind NotificationKind,
    bool IsCorrected,
    string CreatedBy,
    DateTime CreatedAt,
    string? CorrectedBy,
    DateTime? CorrectedAt,
    string? CorrectionNotificationId,
    int? PairedRecordId);

public sealed class UnnotifiedItsMovementRepository
{
    public async Task ValidateBeforeActionAsync(
        IReadOnlyList<string> qrCodes,
        StockMovementDirection direction,
        bool itsNotificationEnabled,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ManualStockSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ValidateBeforeActionAsync(
            connection, transaction, qrCodes, direction, itsNotificationEnabled, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task ValidateBeforeActionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> qrCodes,
        StockMovementDirection direction,
        bool itsNotificationEnabled,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeQrCodes(qrCodes);
        if (normalized.Length == 0) return;

        await using (var activeOperation = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT qr_code
            FROM (
                SELECT qr_code FROM public.stock_its_operation_items WHERE is_active
                UNION ALL
                SELECT qr_code FROM public.retail_its_operation_items WHERE is_active
            ) active
            WHERE qr_code = ANY(@qr_codes)
            LIMIT 1
            """, connection, transaction))
        {
            activeOperation.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, normalized);
            if (await activeOperation.ExecuteScalarAsync(cancellationToken) is string activeQr)
                throw new InvalidOperationException(
                    $"Karekod bekleyen veya mutabakat gerektiren bir İTS işleminde bulunuyor: {activeQr}");
        }

        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT qr_code, movement_direction
            FROM public.unnotified_its_movements
            WHERE NOT is_corrected
              AND qr_code = ANY(@qr_codes)
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qrCode = reader.GetString(0);
            var existingDirection = ParseDirection(reader.GetString(1));
            if (itsNotificationEnabled || existingDirection == direction)
                throw new InvalidOperationException(
                    $"Karekodun düzeltilmemiş bir İTS hareketi bulunuyor: {qrCode}. Önce Debug > İTS Bildirimsiz Hareketler ekranından düzeltin.");
        }
    }

    public static async Task RecordSkippedNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qrCode,
        string referenceNumber,
        StockMovementDirection direction,
        StockItsNotificationKind notificationKind,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var find = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id, movement_direction
            FROM public.unnotified_its_movements
            WHERE qr_code = @qr_code AND NOT is_corrected
            FOR UPDATE
            """, connection, transaction);
        find.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        int? existingId = null;
        StockMovementDirection? existingDirection = null;
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                existingId = reader.GetInt32(0);
                existingDirection = ParseDirection(reader.GetString(1));
            }
        }

        if (existingDirection == direction)
            throw new InvalidOperationException($"Karekodun aynı yönde bekleyen İTS hareketi bulunuyor: {qrCode}");

        if (existingId.HasValue)
        {
            await using var closeExisting = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.unnotified_its_movements
                SET is_corrected = TRUE, corrected_by = @user_id, corrected_at = CURRENT_TIMESTAMP
                WHERE record_id = @record_id AND NOT is_corrected
                """, connection, transaction);
            closeExisting.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
            closeExisting.Parameters.AddWithValue("record_id", NpgsqlDbType.Integer, existingId.Value);
            if (await closeExisting.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Karşıt İTS hareketi eşleştirilemedi.");
        }

        await using var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.unnotified_its_movements (
                qr_code, reference_no, movement_direction, notification_type,
                is_corrected, created_by, corrected_by, corrected_at, paired_record_id)
            VALUES (
                @qr_code, @reference_no, @direction, @notification_type,
                @is_corrected, @user_id,
                CASE WHEN @is_corrected THEN @user_id ELSE NULL END,
                CASE WHEN @is_corrected THEN CURRENT_TIMESTAMP ELSE NULL END,
                @paired_record_id)
            RETURNING record_id
            """, connection, transaction);
        insert.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        insert.Parameters.AddWithValue("reference_no", NpgsqlDbType.Varchar, referenceNumber);
        insert.Parameters.AddWithValue("direction", NpgsqlDbType.Varchar, ToDatabase(direction));
        insert.Parameters.AddWithValue("notification_type", NpgsqlDbType.Varchar, ToDatabase(notificationKind));
        insert.Parameters.AddWithValue("is_corrected", NpgsqlDbType.Boolean, existingId.HasValue);
        insert.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        insert.Parameters.Add(new NpgsqlParameter("paired_record_id", NpgsqlDbType.Integer)
        {
            Value = existingId ?? (object)DBNull.Value
        });
        var newId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));

        if (!existingId.HasValue) return;
        await using var pair = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.unnotified_its_movements
            SET paired_record_id = @new_id
            WHERE record_id = @existing_id
            """, connection, transaction);
        pair.Parameters.AddWithValue("new_id", NpgsqlDbType.Integer, newId);
        pair.Parameters.AddWithValue("existing_id", NpgsqlDbType.Integer, existingId.Value);
        await pair.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UnnotifiedItsMovementItem>> LoadOpenAsync(CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ManualStockSchema.EnsureAsync(connection, cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT qr_code,
                   array_agg(record_id ORDER BY record_id),
                   array_agg(reference_no ORDER BY record_id),
                   min(movement_direction),
                   min(notification_type),
                   min(created_at)
            FROM public.unnotified_its_movements
            WHERE NOT is_corrected
            GROUP BY qr_code
            ORDER BY min(created_at), qr_code
            """, connection);
        var result = new List<UnnotifiedItsMovementItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new UnnotifiedItsMovementItem(
                reader.GetString(0),
                reader.GetFieldValue<int[]>(1),
                reader.GetFieldValue<string[]>(2),
                ParseDirection(reader.GetString(3)),
                ParseNotificationKind(reader.GetString(4)),
                reader.GetDateTime(5)));
        return result;
    }

    public async Task<IReadOnlyList<UnnotifiedItsMovementRecord>> LoadHistoryAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ManualStockSchema.EnsureAsync(connection, cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id,
                   qr_code,
                   reference_no,
                   movement_direction,
                   notification_type,
                   is_corrected,
                   created_by,
                   created_at,
                   corrected_by,
                   corrected_at,
                   correction_notification_id,
                   paired_record_id
            FROM public.unnotified_its_movements
            ORDER BY qr_code, created_at DESC, record_id DESC
            """, connection);
        var result = new List<UnnotifiedItsMovementRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new UnnotifiedItsMovementRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseDirection(reader.GetString(3)),
                ParseNotificationKind(reader.GetString(4)),
                reader.GetBoolean(5),
                reader.GetString(6),
                reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11)));
        return result;
    }

    public async Task<int> MarkCorrectedAsync(
        IReadOnlyList<int> recordIds,
        string notificationId,
        string userId,
        CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0) throw new InvalidOperationException("Düzeltilecek İTS hareketi bulunamadı.");
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ManualStockSchema.EnsureAsync(connection, cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.unnotified_its_movements
            SET is_corrected = TRUE,
                corrected_by = @user_id,
                corrected_at = CURRENT_TIMESTAMP,
                correction_notification_id = @notification_id
            WHERE record_id = ANY(@record_ids) AND NOT is_corrected
            """, connection);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("notification_id", NpgsqlDbType.Varchar, notificationId);
        command.Parameters.AddWithValue("record_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, recordIds.ToArray());
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0) throw new InvalidOperationException("İTS hareketi daha önce düzeltilmiş.");
        return recordIds[0];
    }

    private static string[] NormalizeQrCodes(IReadOnlyList<string> qrCodes) => qrCodes
        .Select(code => code.Trim())
        .Where(code => code.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static string ToDatabase(StockMovementDirection direction) =>
        direction == StockMovementDirection.In ? "IN" : "OUT";

    public static string ToDatabase(StockItsNotificationKind kind) => kind switch
    {
        StockItsNotificationKind.Sale => "SALE",
        StockItsNotificationKind.SaleCancellation => "SALE_CANCELLATION",
        StockItsNotificationKind.Acceptance => "ACCEPTANCE",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static StockMovementDirection ParseDirection(string value) =>
        value == "IN" ? StockMovementDirection.In : StockMovementDirection.Out;

    private static StockItsNotificationKind ParseNotificationKind(string value) => value switch
    {
        "SALE" => StockItsNotificationKind.Sale,
        "SALE_CANCELLATION" => StockItsNotificationKind.SaleCancellation,
        "ACCEPTANCE" => StockItsNotificationKind.Acceptance,
        _ => throw new InvalidOperationException($"Bilinmeyen İTS bildirim türü: {value}")
    };
}
