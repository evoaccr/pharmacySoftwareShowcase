using System.IO;

namespace pharmacyApp.Modules.Medula;

internal static class MedulaPrescriptionSchema
{
    private const string ResourceName =
        "pharmacyApp.Database.065_normalize_prescription_codes.sql";

    private static readonly Lazy<string> MigrationSql = new(LoadMigrationSql);

    public static string Sql => MigrationSql.Value;

    private static string LoadMigrationSql()
    {
        using var stream = typeof(MedulaPrescriptionSchema).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded prescription schema resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
