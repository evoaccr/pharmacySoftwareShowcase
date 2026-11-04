namespace pharmacyApp.Infrastructure;

public static class LocalActivityClock
{
    public static async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("SELECT public.refresh_visible_activity()", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
