using System.Globalization;
using System.Text.RegularExpressions;

namespace pharmacyApp.AI.Workflows;

public sealed partial class WorkflowRouterChatService
{
    private static bool IsSimpleRetainedMetricQuestion(string text)
    {
        // Local arithmetic is safe only for the whole displayed result and one metric.
        // Unknown words may express a filter, exception, grouping, comparison or entity;
        // leave those to the router's structured previous-result operation instead.
        var remainder = Regex.Replace(text,
            @"\b(?:bunlar(?:da|daki|dan|in)?|bu|son|onceki|gosterilen|listelenen|sonuc(?:ta|lar)?|" +
            @"liste(?:deki|nin|de)?|tablo(?:daki|nun|da)?|satis(?:lar|lardaki|larin|larda)?|kayit(?:lar|lardaki|larin)?|" +
            @"satir(?:lar|lardaki|larin)?|toplam|toplami|ortalama|ortalamasi|en|yuksek|fazla|dusuk|az|maksimum|minimum|" +
            @"ne|kadar|kac|tane|sayisi|iskonto(?:su|lari)?|indirim(?:i|leri)?|net|brut|hasilat(?:i)?|tutar(?:i)?|" +
            @"kamu|kurum|kar(?:i)?|urun|adedi|adet|kutu|oldu|var|nedir|neydi|hesapla|soyle|goster|getir|tl)\b",
            string.Empty, RegexOptions.CultureInvariant);
        return !Regex.IsMatch(remainder, @"[\p{L}\p{N}]", RegexOptions.CultureInvariant);
    }

    private string BuildRouterInstructions() => prompts.WorkflowRouter +
        "\n\nWorkflow contracts (stable application metadata, not conversation data). " +
        "Only keys listed in a workflow's inputs are accepted parameters (missing inputs means none). " +
        "An empty help string inherits common_parameter_help for that key; other strings override it. " +
        "Missing terminal/resolves_entity flags mean false.\n" +
        WorkflowRouterCatalog.Describe(registry.RouterDefinitions);

    private static string SafeCorrectedRequest(AiWorkflowPlan plan, string original)
    {
        if (plan.CorrectionConfidence <= 0.90 || string.IsNullOrWhiteSpace(plan.CorrectedRequest)) return original;
        // Spelling corrections are not a second, unconstrained intent channel. In particular,
        // "which filters did you use?" must not become a command to rerun the previous search.
        var proposed = plan.CorrectedRequest.Trim();
        static string Letters(string value) => Regex.Replace(
            value.ToLower(CultureInfo.GetCultureInfo("tr-TR")), @"[^\p{L}\p{N}]", string.Empty);
        var before = Letters(original);
        var after = Letters(proposed);
        var originalNumbers = Regex.Matches(original, @"\d+").Select(match => match.Value);
        var correctedNumbers = Regex.Matches(proposed, @"\d+").Select(match => match.Value);
        if (!originalNumbers.SequenceEqual(correctedNumbers, StringComparer.Ordinal)) return original;
        var allowedEdits = Math.Clamp(before.Length / 8, 2, 12);
        return DamerauLevenshteinDistance(before, after, allowedEdits) <= allowedEdits ? proposed : original;
    }
}
