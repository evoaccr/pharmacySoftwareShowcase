using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.StockMovements;

public sealed class ManualStockRepository
{
    public async Task<int> CompleteAsync(
        StockMovementDirection direction, IReadOnlyList<string> qrCodes, string? note,
        string userId, bool itsNotificationEnabled, string? notificationId,
        CancellationToken cancellationToken)
    {
        var normalized = qrCodes.Select(code => code.Trim()).Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0) throw new InvalidOperationException("Kaydedilecek karekod bulunamadı.");
        userId = string.IsNullOrWhiteSpace(userId) ? "admin" : userId.Trim();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ManualStockSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await UnnotifiedItsMovementRepository.ValidateBeforeActionAsync(
                connection, transaction, normalized, direction, itsNotificationEnabled, cancellationToken);
            await ValidateLocalStateAsync(connection, transaction, normalized, direction, cancellationToken);
            await using var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                INSERT INTO public.manual_movements
                    (movement_direction, its_notification_enabled, its_notification_id, user_id, note)
                VALUES (@direction, @enabled, @notification_id, @user_id, NULLIF(BTRIM(@note), ''))
                RETURNING record_id
                """, connection, transaction);
            insert.Parameters.AddWithValue("direction", NpgsqlDbType.Varchar, UnnotifiedItsMovementRepository.ToDatabase(direction));
            insert.Parameters.AddWithValue("enabled", NpgsqlDbType.Boolean, itsNotificationEnabled);
            insert.Parameters.Add(new NpgsqlParameter("notification_id", NpgsqlDbType.Varchar) { Value = notificationId ?? (object)DBNull.Value });
            insert.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
            insert.Parameters.AddWithValue("note", NpgsqlDbType.Varchar, note ?? string.Empty);
            var id = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
            var reference = $"M{id}";
            foreach (var qrCode in normalized)
            {
                await using var movement = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                    INSERT INTO public."qrMovements"
                        (qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
                    VALUES (@qr_code,
                        (SELECT record_id FROM public."qrMovementTypes" WHERE code = @movement_code),
                        CURRENT_TIMESTAMP, 'M', @user_id, @reference)
                    """, connection, transaction);
                movement.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
                movement.Parameters.AddWithValue("movement_code", NpgsqlDbType.Varchar,
                    direction == StockMovementDirection.In ? "MAN_IN" : "MAN_OUT");
                movement.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
                movement.Parameters.AddWithValue("reference", NpgsqlDbType.Varchar, reference);
                await movement.ExecuteNonQueryAsync(cancellationToken);
                if (!itsNotificationEnabled)
                    await UnnotifiedItsMovementRepository.RecordSkippedNotificationAsync(
                        connection, transaction, qrCode, reference, direction,
                        direction == StockMovementDirection.In ? StockItsNotificationKind.Acceptance : StockItsNotificationKind.Sale,
                        userId, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ValidateLocalStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string[] qrCodes,
        StockMovementDirection direction, CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT qr_code FROM public."qrActive" WHERE qr_code = ANY(@qr_codes) FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, qrCodes);
        var active = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) active.Add(reader.GetString(0));
        var invalid = qrCodes.FirstOrDefault(qr => direction == StockMovementDirection.Out ? !active.Contains(qr) : active.Contains(qr));
        if (invalid is not null)
            throw new InvalidOperationException(direction == StockMovementDirection.Out
                ? $"Karekod yerel aktif stokta değil: {invalid}"
                : $"Karekod zaten yerel aktif stokta: {invalid}");
    }
}
