using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using pharmacyApp.AI.Tools;
using pharmacyApp.AI.Runtime;

namespace pharmacyApp.AI.Search;

public sealed record AiToolSearchHit(AiToolDefinition Tool, double Score);

public interface IAiToolSearchService
{
    IReadOnlyList<AiToolSearchHit> Search(string query, IReadOnlyList<string> domains, int limit);
    IReadOnlyList<AiToolSearchHit> SearchAll(string query, int limit);
    IReadOnlyDictionary<string, double> ScoreDomains(string query);
}

public sealed partial class AiToolSearchService(IAiToolRegistry registry) : IAiToolSearchService
{
    private static readonly IReadOnlyDictionary<string, string[]> Synonyms = new Dictionary<string, string[]>
    {
        ["product"] = ["urun", "ilac", "medicine", "catalog", "barkod", "barcode"],
        ["qr"] = ["karekod", "datamatrix", "package", "paket"],
        ["sale"] = ["satis", "satislar", "satislari", "satislarin", "satislarinin", "sale", "odeme", "payment", "hasilat", "hasilati", "hasilatini", "ciro", "revenue"],
        ["medula"] = ["recete", "prescription", "erecete", "e-prescription"]
    };

    public IReadOnlyList<AiToolSearchHit> Search(string query, IReadOnlyList<string> domains, int limit)
    {
        var selectedDomains = domains.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = registry.GetAllTools()
            .Where(tool => selectedDomains.Count == 0 || selectedDomains.Contains(AiToolRegistry.GetNamespace(tool)))
            .Select(tool => new AiToolSearchHit(tool, Score(query, tool)))
            .Where(hit => hit.Score > 0)
            .ToArray();
        if (selectedDomains.Count <= 1)
            return candidates.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Tool.Name).Take(limit).ToArray();

        var result = new List<AiToolSearchHit>();
        foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var best = candidates.Where(hit => string.Equals(AiToolRegistry.GetNamespace(hit.Tool), domain, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(hit => hit.Score).FirstOrDefault();
            if (best is not null) result.Add(best);
        }
        result.AddRange(candidates.OrderByDescending(hit => hit.Score)
            .Where(hit => result.All(existing => existing.Tool.Name != hit.Tool.Name)));
        return result.Take(limit).ToArray();
    }

    public IReadOnlyList<AiToolSearchHit> SearchAll(string query, int limit) =>
        Search(query, [], limit);

    public IReadOnlyDictionary<string, double> ScoreDomains(string query) => registry.Namespaces.ToDictionary(
        domain => domain,
        domain => registry.GetToolsByNamespace(domain).Select(tool => Score(query, tool)).DefaultIfEmpty().Max(),
        StringComparer.OrdinalIgnoreCase);

    private static double Score(string query, AiToolDefinition tool)
    {
        var queryTokens = Expand(Tokenize(query));
        if (queryTokens.Count == 0) return 0;
        var nameTokens = Expand(Tokenize(tool.Name.Replace('.', ' ')));
        var tagTokens = Expand(tool.Tags.SelectMany(Tokenize).ToHashSet(StringComparer.Ordinal));
        var descriptionTokens = Expand(Tokenize(tool.Description));
        var parameterTokens = Expand(tool.Parameters.SelectMany(pair => Tokenize(pair.Key + " " + pair.Value.Description)).ToHashSet(StringComparer.Ordinal));
        var exactName = Normalize(query).Contains(Normalize(tool.Name), StringComparison.Ordinal) ? 8 : 0;
        return exactName + queryTokens.Sum(token =>
            (nameTokens.Contains(token) ? 4d : 0) +
            (tagTokens.Contains(token) ? 3d : 0) +
            (descriptionTokens.Contains(token) ? 1.5d : 0) +
            (parameterTokens.Contains(token) ? 1d : 0));
    }

    private static HashSet<string> Expand(HashSet<string> tokens)
    {
        var expanded = new HashSet<string>(tokens, StringComparer.Ordinal);
        foreach (var (canonical, aliases) in Synonyms)
        {
            if (tokens.Contains(canonical) || aliases.Any(tokens.Contains))
            {
                expanded.Add(canonical);
                foreach (var alias in aliases) expanded.Add(alias);
            }
        }
        return expanded;
    }

    private static HashSet<string> Tokenize(string value) => TokenPattern().Matches(Normalize(value))
        .Select(match => match.Value).Where(token => token.Length > 1).ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character == 'ı' ? 'i' : character);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex("[a-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
