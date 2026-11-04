using System.Text.Json;
using System.Text.RegularExpressions;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Workflows;

public sealed partial class WorkflowRouterChatService
{
    // An unresolved name is conversation context, never a confirmed barcode/order.
    private readonly Dictionary<string, string> recentPurchaseSubjects = new(StringComparer.Ordinal);

    private bool TryBuildPurchaseState(AiRequestContext request, AiConfiguration configuration, out AiWorkflowRunState state)
    {
        state = null!;
        var text = NormalizeIntent(request.UserMessage).Trim().TrimEnd('.', '?', '!');
        string[] queue;
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (Regex.IsMatch(text, @"^(?:please\s+)?(?:what\s+should\s+(?:i|we)\s+(?:purchase|buy|order)|(?:ne|neler)\s+almaliyim|hangi\s+urunleri\s+almaliyim)$", RegexOptions.CultureInvariant))
        {
            queue = ["stok_satis_kesisim_analizi", "tabloda_goster"];
            inputs[queue[0]] = new Dictionary<string, string>
            {
                ["analysis_kind"] = "replenishment", ["period_days"] = "30", ["target_days"] = "30", ["limit"] = "100"
            };
            inputs[queue[1]] = new Dictionary<string, string> { ["title"] = "Stok Alım Önerileri" };
        }
        else
        {
            var match = Regex.Match(request.UserMessage.Trim(),
                @"^(?:please\s+)?(?:purchase|buy|order)\s+(?<quantity>[1-9][0-9]{0,5})\s+(?:(?:packs?|boxes|units?)\s+)?(?:of\s+)?(?<medicine>[\p{L}0-9][\p{L}0-9 ./'-]{0,80}?)\s*[.!]?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            var medicine = match.Groups["medicine"].Value.Trim();
            // Additional constraints or multiple operations belong to the full planner.
            if (Regex.IsMatch(NormalizeIntent(medicine), @"\b(?:and|then|from|for|if|under|over|cheapest|tomorrow|today|not|never|unless|price|history)\b")) return false;
            if (medicine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) return false;
            var reference = NormalizeIntent(medicine) is "it" or "that" or "this" or "that medication" or "this medication" or "that medicine" or "this medicine" or "them" or "those medicines";
            var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (reference)
            {
                string? recentSubject;
                lock (sync) recentSubject = recentPurchaseSubjects.GetValueOrDefault(request.SessionId);
                if (recentSubject is not null) medicine = recentSubject;
                else if (GetRememberedSelection(request.SessionId) is { } confirmed)
                {
                    outputs["ilac_barkod_eslestir"] = confirmed;
                    medicine = "";
                }
                else
                {
                    lock (sync) medicine = recentPurchaseSubjects.GetValueOrDefault(request.SessionId) ?? "";
                }
            }
            else lock (sync) recentPurchaseSubjects[request.SessionId] = medicine;
            queue = ["siparis_istegini_tani"];
            inputs[queue[0]] = new Dictionary<string, string>
            { ["medicine_query"] = medicine, ["quantity"] = match.Groups["quantity"].Value };
            if (registry.Get(queue[0]) is null) return false;
            var orderPlan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false, queue, inputs, new AiUsage(0, 0, 0, 0, 0));
            state = new(request, orderPlan, queue, 0, outputs, [], [], configuration.RouterModel, false);
            return true;
        }
        if (queue.Any(name => registry.Get(name) is null)) return false;
        var plan = new AiWorkflowPlan(null, false, null, request.UserMessage, 0, [], false, queue, inputs, new AiUsage(0, 0, 0, 0, 0));
        state = new(request, plan, queue, 0, new(StringComparer.Ordinal), [], [], configuration.RouterModel, false);
        return true;
    }
}
