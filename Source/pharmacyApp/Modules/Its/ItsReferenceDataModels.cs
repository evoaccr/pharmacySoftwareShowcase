namespace pharmacyApp.Modules.Its;

public sealed record ItsResponseCode(string Code, string Message, string Description)
{
    public string DisplayText => ItsResponseCodeFormatter.Format(Code, Message, Description);
}

public sealed record ItsDrug(
    string Gtin,
    string DrugName,
    string ManufacturerGln,
    string ManufacturerName,
    bool Active,
    bool Imported);

public static class ItsResponseCodeFormatter
{
    public static string Format(string? code, string? message, string? description)
    {
        var normalizedCode = string.IsNullOrWhiteSpace(code) ? "—" : code.Trim();
        var normalizedMessage = message?.Trim() ?? string.Empty;
        var normalizedDescription = description?.Trim() ?? string.Empty;
        if (normalizedMessage.Length == 0 && normalizedCode == "00000")
            normalizedMessage = "İşlem başarılı.";

        var text = normalizedMessage.Length == 0
            ? normalizedCode
            : $"{normalizedCode} — {normalizedMessage}";
        return normalizedDescription.Length == 0 ||
               string.Equals(normalizedDescription, normalizedMessage, StringComparison.OrdinalIgnoreCase)
            ? text
            : $"{text} ({normalizedDescription})";
    }
}
