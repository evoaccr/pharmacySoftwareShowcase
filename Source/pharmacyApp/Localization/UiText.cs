using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pharmacyApp.Localization;

/// <summary>English display text. Integration tokens and stored values remain in their original language.</summary>
public static class UiText
{
    private static readonly Dictionary<string, string> Texts = Load();
    private static readonly (Regex Pattern, string English, string Anchor)[] Templates = Texts
        .Where(pair => Regex.IsMatch(pair.Key, @"\{\d+\}") && Regex.Replace(pair.Key, @"\{\d+\}", "").Length >= 3)
        .OrderByDescending(pair => Regex.Replace(pair.Key, @"\{\d+\}", "").Length)
        .Select(pair => (Compile(pair.Key), pair.Value, Regex.Split(pair.Key, @"\{\d+\}").OrderByDescending(piece => piece.Length).First().Trim())).ToArray();

    public static string Translate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        return TranslateCore(value, 0);
    }

    public static string TranslateSpecialty(string? value) => string.Join(" / ",
        Regex.Split(value ?? string.Empty, @"\s*(?:->|/)\s*").Select(part => Translate(part.Trim())));

    private static string TranslateCore(string value, int depth)
    {
        if (depth > 3 || string.IsNullOrWhiteSpace(value)) return value;
        if (Texts.TryGetValue(value, out var exact)) return exact;
        var trimmed = value.Trim();
        var start = value.IndexOf(trimmed, StringComparison.Ordinal);
        if (Texts.TryGetValue(trimmed, out exact)) return value[..start] + exact + value[(start + trimmed.Length)..];
        var decorated = Regex.Match(trimmed, @"\A(.+?)(\s*[*:]|\s+\(\d+\))\z");
        if (decorated.Success)
        {
            var label = TranslateCore(decorated.Groups[1].Value, depth + 1);
            if (label != decorated.Groups[1].Value)
                return value[..start] + label + decorated.Groups[2].Value + value[(start + trimmed.Length)..];
        }
        foreach (var (pattern, english, anchor) in Templates)
        {
            if (!trimmed.Contains(anchor, StringComparison.Ordinal)) continue;
            Match match;
            try { match = pattern.Match(trimmed); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            var translated = Regex.Replace(english, @"\{(\d+)\}", placeholder =>
                TranslateCore(match.Groups["p" + placeholder.Groups[1].Value].Value, depth + 1));
            return value[..start] + translated + value[(start + trimmed.Length)..];
        }
        if (value.Contains('\n')) return string.Join("\n", value.Split('\n').Select(line => TranslateCore(line, depth + 1)));
        // Concatenated status messages commonly end their static part with a colon.
        var colon = value.IndexOf(':');
        if (colon > 0 && Texts.TryGetValue(value[..(colon + 1)].Trim(), out exact))
            return exact + TranslateCore(value[(colon + 1)..], depth + 1);
        return value;
    }

    private static Regex Compile(string source)
    {
        var escaped = Regex.Escape(source.Trim());
        escaped = Regex.Replace(escaped, @"\\\{(\d+)\\?\}", match => "(?<p" + match.Groups[1].Value + ">[\\s\\S]*?)");
        if (Regex.IsMatch(source.Trim(), @"\A\{0\} (ay|gün|yıl|saat|dakika|kutu|adet|ilaç|reçete|kayıt|satır)\z"))
            escaped = escaped.Replace("(?<p0>[\\s\\S]*?)", "(?<p0>[0-9][0-9,. –-]*)");
        return new Regex("\\A" + escaped + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    private static Dictionary<string, string> Load()
    {
        using var resource = typeof(UiText).Assembly.GetManifestResourceStream("pharmacyApp.Localization.English.json")
            ?? throw new InvalidOperationException("The English UI catalog is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(resource) ?? [];
    }
}
