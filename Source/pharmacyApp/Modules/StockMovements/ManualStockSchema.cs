using System.IO;
using Npgsql;

namespace pharmacyApp.Modules.StockMovements;

public static class ManualStockSchema
{
    private const string ResourceName = "pharmacyApp.Database.057_create_manual_stock_and_unnotified_its.sql";
    private static readonly Lazy<string> SchemaSql = new(ReadSchemaSql);
    private static readonly SemaphoreSlim EnsureLock = new(1, 1);
    private static readonly HashSet<string> EnsuredDatabases = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        if (!pharmacyApp.Infrastructure.DatabaseConnectionSettingsStore.Current.AllowSchemaChanges) return;
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        var key = $"{settings.Host}:{settings.Port}/{settings.Database}/{settings.Username}";
        await EnsureLock.WaitAsync(cancellationToken);
        try
        {
            if (EnsuredDatabases.Contains(key)) return;
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(SchemaSql.Value, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            EnsuredDatabases.Add(key);
        }
        finally
        {
            EnsureLock.Release();
        }
    }

    private static string ReadSchemaSql()
    {
        using var stream = typeof(ManualStockSchema).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Manual stock schema resource was not found: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
