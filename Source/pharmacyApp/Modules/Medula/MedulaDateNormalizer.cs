using System.Globalization;

namespace pharmacyApp.Modules.Medula;

public static class MedulaDateNormalizer
{
    private static readonly string[] AcceptedFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy",
        "dd/MM/yy", "d/M/yy", "dd.MM.yy", "d.M.yy"
    ];

    public static string? Normalize(string? text, DateTime? selectedDate = null)
    {
        if (selectedDate is DateTime selected)
            return selected.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        return DateTime.TryParseExact(
            text?.Trim(),
            AcceptedFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : null;
    }
}
