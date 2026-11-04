namespace pharmacyApp.Modules.Medula;

public static class MedulaReportIngredientMatching
{
    public static string NormalizeSgkCode(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());

    public static bool HasPrescriptionMedication(
        string? reportSgkCode,
        IEnumerable<string?> prescriptionSgkCodes)
    {
        var reportCode = NormalizeSgkCode(reportSgkCode);
        return reportCode.Length > 0 && prescriptionSgkCodes
            .Select(NormalizeSgkCode)
            .Any(code => code.Equals(reportCode, StringComparison.Ordinal));
    }
}
