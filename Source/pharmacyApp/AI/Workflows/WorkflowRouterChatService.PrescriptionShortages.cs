using System.Text.RegularExpressions;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Workflows;

public sealed partial class WorkflowRouterChatService
{
    // Whole-intent templates only: unknown qualifiers stay with the router rather
    // than silently losing medicine/date/source constraints.
    private bool TryBuildPrescriptionShortageState(AiRequestContext request, AiConfiguration configuration, out AiWorkflowRunState state)
    {
        state = null!;
        var text = request.UserMessage.Trim();
        var match = Regex.Match(text,
            @"^(?:please\s+)?(?:(?:tell|show|give|calculate)(?:\s+me)?\s+)?(?:the\s+)?(?:missing\s+(?:contents|medicines|items|drugs)|(?:which|what)\s+(?:medicines|items|drugs)\s+are\s+missing)\s+(?:of|from|in|for)\s+(?<patient>[\p{L} .'-]+?)['’]s\s+(?:last|latest|most\s+recent)\s+prescription\s*[?.!]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(text,
                @"^(?<patient>[\p{L} .'-]+?)['’]n?[ıiuü]n\s+son\s+reçete(?:sinde|sinin|sindeki)\s+eksik(?:\s+olan)?\s+(?:ilaçlar[ıi](?:n[ıi])?|ürünler[ıi](?:n[ıi])?|içeriği)(?:\s+(?:söyle|göster|getir|listele))?\s*[?.!]*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(text,
                @"^(?:lütfen\s+)?(?<patient>[\p{L} .'-]+?)\s+adlı\s+hastanın\s+(?:en\s+)?son\s+reçetesindeki\s+eksik\s+ilaçları\s+(?:hesapla|göster|getir|söyle)\s*[?.!]*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var patient = match.Groups["patient"].Value.Trim();
        if (patient.Length < 2 || patient.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5) return false;
        string[] queue = ["hasta_eslestir", "medula_son_receteyi_ac", "recete_stok_eksiklerini_hesapla"];
        if (queue.Any(name => registry.Get(name) is null)) return false;
        var inputs = queue.ToDictionary(name => name, _ => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(), StringComparer.Ordinal);
        inputs["hasta_eslestir"] = new Dictionary<string, string> { ["query"] = patient };
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false, queue, inputs, new AiUsage(0, 0, 0, 0, 0));
        state = new AiWorkflowRunState(request, plan, queue, 0, new(StringComparer.Ordinal), [], [], configuration.RouterModel, false);
        return true;
    }
}

