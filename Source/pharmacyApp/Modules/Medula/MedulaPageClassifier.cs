namespace pharmacyApp.Modules.Medula;

internal enum MedulaPageKind
{
    Authenticated,
    Login,
    LoginRejected,
    SmsVerification,
    SystemError,
    Unexpected
}

internal static class MedulaPageClassifier
{
    public static MedulaPageKind Classify(
        string url,
        bool hasLoginForm,
        string? pageText,
        string? pageTitle = null,
        bool hasKnownMedulaControls = false)
    {
        var text = pageText ?? string.Empty;
        if (MedulaSystemErrorDetector.IsSystemError(
                url, pageTitle, text, hasKnownMedulaControls))
            return MedulaPageKind.SystemError;
        if (url.Contains("loginerror", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Kullanıcı adı veya şifre hatalı", StringComparison.OrdinalIgnoreCase))
            return MedulaPageKind.LoginRejected;
        if (text.Contains("SMS doğrul", StringComparison.OrdinalIgnoreCase))
            return MedulaPageKind.SmsVerification;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("medeczane.sgk.gov.tr", StringComparison.OrdinalIgnoreCase))
            return MedulaPageKind.Unexpected;
        return hasLoginForm ? MedulaPageKind.Login : MedulaPageKind.Authenticated;
    }
}

internal static class MedulaSystemErrorDetector
{
    public static bool IsSystemError(
        string? url,
        string? title,
        string? pageText,
        bool hasKnownMedulaControls)
    {
        var normalizedTitle = Normalize(title);
        var normalizedText = Normalize(pageText);
        var normalizedUrl = (url ?? string.Empty).Trim();
        var blockingError = normalizedTitle.Contains(
                "yazılımsal veya sistemsel bir hata oluştu", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains(
                "yazılımsal veya sistemsel bir hata oluştu", StringComparison.OrdinalIgnoreCase);
        var plainSystemError = normalizedTitle.Equals("sistem hatası", StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Equals("system error", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Equals("sistem hatası", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Equals("system error", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(part => part.Equals("sistem hatası", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("system error", StringComparison.OrdinalIgnoreCase));
        if (blockingError || plainSystemError) return true;
        if (hasKnownMedulaControls) return false;

        var titleMarker = normalizedTitle.Contains("sistem hatası", StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains("system error", StringComparison.OrdinalIgnoreCase);
        var bodyMarker = normalizedText.StartsWith("sistem hatası", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains("beklenmeyen bir sistem hatası oluştu", StringComparison.OrdinalIgnoreCase)
            || (normalizedText.Length <= 3000
                && normalizedText.Contains("sistem hatası", StringComparison.OrdinalIgnoreCase));
        var errorPath = normalizedUrl.Contains("/error", StringComparison.OrdinalIgnoreCase)
            || normalizedUrl.Contains("error.jsp", StringComparison.OrdinalIgnoreCase)
            || normalizedUrl.Contains("exception", StringComparison.OrdinalIgnoreCase);

        return titleMarker || bodyMarker || (errorPath
            && normalizedText.Contains("sistem", StringComparison.OrdinalIgnoreCase)
            && normalizedText.Contains("hata", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
