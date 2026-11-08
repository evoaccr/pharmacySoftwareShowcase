using Npgsql;

namespace pharmacyApp.Infrastructure;

public sealed record DatabaseConnectionSettings(
    string Hostname = "portfolio-db.acarise.net",
    int Port = 1234,
    string DatabaseName = "Portfolio",
    string Username = "portfolio",
    string Password = "portfolio123",
    string SslMode = "VerifyFull",
    bool AllowSchemaChanges = false);

public static class DatabaseConnectionSettingsStore
{
    private static readonly object SyncRoot = new();
    public static string SettingsFilePath => AppConfiguration.FilePath;

    private static DatabaseConnectionSettings current = Load();

    public static DatabaseConnectionSettings Current
    {
        get
        {
            lock (SyncRoot)
            {
                return current with { };
            }
        }
    }

    public static NpgsqlConnection CreateConnection() => new(BuildConnectionString(Current));

    public static void Save(DatabaseConnectionSettings settings)
    {
        Validate(settings);
        lock (SyncRoot)
        {
            AppConfiguration.WriteSection("Database", settings);
            current = settings;
        }
    }

    private static DatabaseConnectionSettings Load()
    {
        var settings = AppConfiguration.ReadDatabase();
        Validate(settings);
        return settings;
    }

    private static string BuildConnectionString(DatabaseConnectionSettings settings)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = settings.Hostname,
            Port = settings.Port,
            Database = settings.DatabaseName,
            Username = settings.Username,
            SslMode = Enum.Parse<Npgsql.SslMode>(settings.SslMode, ignoreCase: true),
            Timeout = 5,
            CommandTimeout = 20,
            KeepAlive = 15,
            Pooling = true,
            ApplicationName = $"pharmacyApp:{DatabaseAuditContext.CurrentUserId ?? "system"}"
        };
        if (!string.IsNullOrEmpty(settings.Password)) builder.Password = settings.Password;
        return builder.ConnectionString;
    }

    private static void Validate(DatabaseConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Hostname)) throw new ArgumentException("Hostname is required.", nameof(settings));
        if (settings.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(settings), "Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(settings.DatabaseName)) throw new ArgumentException("Database name is required.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.Username)) throw new ArgumentException("Username is required.", nameof(settings));
        if (!Enum.TryParse<Npgsql.SslMode>(settings.SslMode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
            throw new ArgumentException("SSL mode is invalid.", nameof(settings));
    }

}

public static class DatabaseAuditContext
{
    public static string? CurrentUserId { get; set; }

    public static async Task SetReferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string referenceCode,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "SELECT set_config('pharmacy.reference_code', @reference_code, true), set_config('pharmacy.user_id', @user_id, true)",
            connection,
            transaction);
        command.Parameters.AddWithValue("reference_code", referenceCode);
        command.Parameters.AddWithValue("user_id", string.IsNullOrWhiteSpace(userId) ? "system" : userId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
