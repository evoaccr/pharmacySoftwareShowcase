using System.IO;

namespace pharmacyApp.Modules.Medula;

internal static class MedulaLoyalPatientSchema
{
    private static readonly string[] ResourceNames =
    [
        "pharmacyApp.Database.067_create_loyal_patient_data.sql",
        "pharmacyApp.Database.068_create_loyal_patient_report_sampling.sql",
        "pharmacyApp.Database.069_create_loyal_patient_hybrid_scoring.sql",
        "pharmacyApp.Database.070_create_loyal_patient_benchmark_cohort.sql",
        "pharmacyApp.Database.073_add_loyal_patient_strikes.sql"
    ];
    private static readonly Lazy<string> MigrationSql = new(LoadMigrationSql);

    public static string Sql => MigrationSql.Value;

    private static string LoadMigrationSql()
    {
        var scripts = new List<string>();
        foreach (var resourceName in ResourceNames)
        {
            using var stream = typeof(MedulaLoyalPatientSchema).Assembly
                .GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded loyal-patient schema resource '{resourceName}' was not found.");
            using var reader = new StreamReader(stream);
            scripts.Add(reader.ReadToEnd());
        }
        return string.Join(Environment.NewLine, scripts);
    }
}
