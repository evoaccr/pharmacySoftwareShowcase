using System.IO;
using Npgsql;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Cards;

internal static class CardSchema
{
    private static readonly string[] ResourceNames =
    [
        "pharmacyApp.Database.071_add_patient_prescription_count.sql",
        "pharmacyApp.Database.072_clean_patient_phone_numbers.sql"
    ];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool isReady;

    public static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (!DatabaseConnectionSettingsStore.Current.AllowSchemaChanges) return;
        if (isReady) return;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (isReady) return;
            foreach (var resourceName in ResourceNames)
            {
                await using var stream = typeof(CardSchema).Assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Gömülü veritabanı kaynağı bulunamadı: {resourceName}");
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(cancellationToken);
                await using var command = DatabaseCommand.Create(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            isReady = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
