namespace pharmacyApp.Modules.Medula;

public sealed record MedulaReportRowAlignmentSlot(
    int? ReportRowIndex,
    int? MedicationRowIndex,
    string MatchCode,
    bool IsSeparator);

public static class MedulaReportRowAlignment
{
    public static IReadOnlyList<MedulaReportRowAlignmentSlot> Build(
        IReadOnlyList<string?> reportCodes,
        IReadOnlyList<string?> medicationCodes)
    {
        var normalizedReports = reportCodes
            .Select(MedulaReportIngredientMatching.NormalizeSgkCode)
            .ToArray();
        var normalizedMedications = medicationCodes
            .Select(MedulaReportIngredientMatching.NormalizeSgkCode)
            .ToArray();
        var medicationCodeSet = normalizedMedications
            .Where(code => code.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var matchedCodes = normalizedReports
            .Where(code => code.Length > 0 && medicationCodeSet.Contains(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var slots = new List<MedulaReportRowAlignmentSlot>();
        var matchedReportIndexes = new HashSet<int>();
        var matchedMedicationIndexes = new HashSet<int>();
        foreach (var code in matchedCodes)
        {
            var reportIndexes = normalizedReports
                .Select((value, index) => (value, index))
                .Where(item => item.value == code)
                .Select(item => item.index)
                .ToArray();
            var medicationIndexes = normalizedMedications
                .Select((value, index) => (value, index))
                .Where(item => item.value == code)
                .Select(item => item.index)
                .ToArray();
            foreach (var index in reportIndexes) matchedReportIndexes.Add(index);
            foreach (var index in medicationIndexes) matchedMedicationIndexes.Add(index);

            for (var index = 0; index < Math.Max(reportIndexes.Length, medicationIndexes.Length); index++)
            {
                slots.Add(new MedulaReportRowAlignmentSlot(
                    index < reportIndexes.Length ? reportIndexes[index] : null,
                    index < medicationIndexes.Length ? medicationIndexes[index] : null,
                    code,
                    false));
            }
        }

        var unmatchedReports = Enumerable.Range(0, normalizedReports.Length)
            .Where(index => !matchedReportIndexes.Contains(index))
            .ToArray();
        var unmatchedMedications = Enumerable.Range(0, normalizedMedications.Length)
            .Where(index => !matchedMedicationIndexes.Contains(index))
            .ToArray();
        if (slots.Count > 0 && (unmatchedReports.Length > 0 || unmatchedMedications.Length > 0))
            slots.Add(new MedulaReportRowAlignmentSlot(null, null, string.Empty, true));

        for (var index = 0; index < Math.Max(unmatchedReports.Length, unmatchedMedications.Length); index++)
        {
            slots.Add(new MedulaReportRowAlignmentSlot(
                index < unmatchedReports.Length ? unmatchedReports[index] : null,
                index < unmatchedMedications.Length ? unmatchedMedications[index] : null,
                string.Empty,
                false));
        }
        return slots;
    }
}
