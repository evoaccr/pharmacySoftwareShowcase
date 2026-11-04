namespace pharmacyApp.Modules.Medula;

public static class MedulaDoctorReconciliation
{
    public const string DefaultSpecialty = "Pratisyen Hekim";

    public static string? SpecialtyForPersistence(
        string? selectedSpecialty,
        bool explicitlySelected,
        bool prescriptionSaved)
    {
        var specialty = selectedSpecialty?.Trim() ?? string.Empty;
        if (specialty.Length == 0) return null;
        if (!specialty.Equals(DefaultSpecialty, StringComparison.OrdinalIgnoreCase)) return specialty;
        return explicitlySelected || prescriptionSaved ? specialty : null;
    }
}
