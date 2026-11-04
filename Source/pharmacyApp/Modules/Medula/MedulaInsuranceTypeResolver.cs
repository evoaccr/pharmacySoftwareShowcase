using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Medula;

public static partial class MedulaInsuranceTypeResolver
{
    public static string Resolve(
        string? scrapedValue,
        string? pageText,
        decimal? prescriptionCash,
        decimal? prescriptionSalary,
        decimal? examinationCash,
        decimal? examinationSalary)
    {
        var explicitValue = Normalize(scrapedValue);
        if (explicitValue.Length > 0) return explicitValue;

        var textMatch = InsuranceBeforeInvoiceTypeRegex().Match(pageText ?? string.Empty);
        if (textMatch.Success) return Normalize(textMatch.Groups[1].Value);

        var salaryContribution = Math.Max(prescriptionSalary ?? 0m, 0m)
            + Math.Max(examinationSalary ?? 0m, 0m);
        var cashContribution = Math.Max(prescriptionCash ?? 0m, 0m)
            + Math.Max(examinationCash ?? 0m, 0m);
        if (salaryContribution > 0m && cashContribution == 0m) return "Emekli";
        if (cashContribution > 0m && salaryContribution == 0m) return "Çalışan";
        return string.Empty;
    }

    private static string Normalize(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Contains("Çalışan", StringComparison.CurrentCultureIgnoreCase)) return "Çalışan";
        if (trimmed.Contains("Emekli", StringComparison.CurrentCultureIgnoreCase)) return "Emekli";
        return string.Empty;
    }

    [GeneratedRegex(@"\b(Çalışan|Emekli)\s+Fat\.?\s*Türü\b", RegexOptions.IgnoreCase)]
    private static partial Regex InsuranceBeforeInvoiceTypeRegex();
}
