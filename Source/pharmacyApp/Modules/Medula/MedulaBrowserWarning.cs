namespace pharmacyApp.Modules.Medula;

public static class MedulaBrowserWarning
{
    public static bool IsPrescriptionDataStatus(string? message)
    {
        var normalized = string.Join(' ', (message ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Equals("Silinmiş Karekod", StringComparison.CurrentCultureIgnoreCase)
            || normalized.StartsWith(
                "Reçeteye eklenmiş doküman bulunmamaktadır.",
                StringComparison.CurrentCultureIgnoreCase);
    }

    public static string? First(
        IEnumerable<string>? messages,
        IEnumerable<string>? confirmations)
    {
        return messages?.FirstOrDefault(message =>
                !string.IsNullOrWhiteSpace(message) && !IsPrescriptionDataStatus(message))
            ?? confirmations?.FirstOrDefault(message =>
                !string.IsNullOrWhiteSpace(message) && !IsPrescriptionDataStatus(message));
    }
}
