using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Search;

namespace pharmacyApp.AI.Routing;

public interface IAiRouter
{
    Task<AiRouteDecision> RouteAsync(AiRequestContext context, CancellationToken cancellationToken = default);
}

public sealed class AiRouter(
    IAiToolRegistry registry,
    IAiToolSearchService search,
    IAiModelTransport transport,
    IAiConfigurationProvider configurationProvider,
    IAiPromptProvider prompts) : IAiRouter
{
    public async Task<AiRouteDecision> RouteAsync(AiRequestContext context, CancellationToken cancellationToken = default)
    {
        var configuration = configurationProvider.GetCurrent();
        var local = Deterministic(context, configuration);
        if (!transport.IsConfigured)
        {
            context.Progress?.Report(new AiActivityUpdate(
                "İstek yerel olarak sınıflandırıldı…",
                $"Deterministic router accepted confidence={local.Confidence:0.00}; domains=[{string.Join(", ", local.Domains)}]."));
            return local;
        }

        context.Progress?.Report(new AiActivityUpdate(
            "İstek sınıflandırılıyor…",
            $"Deterministic confidence={local.Confidence:0.00}; invoking router model {configuration.RouterModel}.",
            configuration.RouterModel));
        var summaries = registry.Namespaces.Select(domain => new
        {
            domain,
            tools = registry.GetToolsByNamespace(domain).Select(tool => new
            {
                tool.Name,
                tool.Tags,
                tool.Risk
            })
        });
        var dynamicInput = JsonSerializer.Serialize(new
        {
            request = context.UserMessage,
            domains = summaries,
            application_context = context.ApplicationContext.RelevantValues(),
            deterministic_suggestion = new
            {
                domains = local.Domains,
                local.NeedsTools,
                complexity = local.Complexity.ToString().ToLowerInvariant(),
                local.Confidence,
                clarification_required = local.NeedsClarification,
                clarification_question = local.ClarificationQuestion,
                clarification_options = local.ClarificationOptions ?? []
            }
        });
        var response = await transport.SendAsync(new AiModelRequest(
            configuration.RouterModel,
            configuration.RouterReasoningEffort,
            prompts.Router,
            [Message("user", dynamicInput)],
            [],
            false,
            false,
            ResponseJsonSchema: RouterSchema(),
            MaxOutputTokens: configuration.RouterMaxOutputTokens), cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(response.Text);
            var root = document.RootElement;
            var domains = root.GetProperty("domains").EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                .Where(domain => registry.Namespaces.Contains(domain, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var complexity = root.GetProperty("complexity").GetString() switch
            {
                "complex" => AiComplexity.Complex,
                "normal" => AiComplexity.Normal,
                _ => AiComplexity.Simple
            };
            var modelTier = root.GetProperty("model_tier").GetString() switch
            {
                "complex" => "complex",
                "default" => "default",
                "simple" => "simple",
                _ => "auto"
            };
            var preferredTools = root.GetProperty("tool_names").EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .Where(name => registry.GetTool(name) is not null)
                .Distinct(StringComparer.Ordinal)
                .Take(configuration.MaxToolsPerRequest)
                .ToArray();
            var expensive = root.GetProperty("expensive").GetBoolean();
            var explosive = root.GetProperty("explosive").GetBoolean();
            var needsClarification = root.GetProperty("clarification_required").GetBoolean();
            var clarificationQuestion = NullableText(root, "clarification_question");
            var clarificationOptions = root.GetProperty("clarification_options").EnumerateArray()
                .Select(value => value.GetString()?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            if (clarificationQuestion is null || clarificationOptions.Length < 2)
                needsClarification = false;
            if (IsSalesRevenueRequest(context.UserMessage) && HasExplicitSalesScope(context.UserMessage))
                needsClarification = false;
            var decision = new AiRouteDecision(domains, root.GetProperty("needs_tools").GetBoolean(), complexity,
                root.GetProperty("confidence").GetDouble(), true, modelTier, preferredTools,
                expensive, NullableText(root, "expense_reason"), explosive, NullableText(root, "explosion_reason"),
                needsClarification, clarificationQuestion, clarificationOptions, response.Usage);
            if (local.NeedsClarification && !decision.NeedsClarification)
                decision = decision with
                {
                    NeedsClarification = true,
                    ClarificationQuestion = local.ClarificationQuestion,
                    ClarificationOptions = local.ClarificationOptions
                };
            context.Progress?.Report(new AiActivityUpdate(
                "İstek sınıflandırıldı…",
                $"Router model selected domains=[{string.Join(", ", decision.Domains)}], tools=[{string.Join(", ", preferredTools)}], model_tier={decision.ModelTier}, needs_tools={decision.NeedsTools}, clarification={decision.NeedsClarification}, expensive={decision.IsExpensive}, explosive={decision.IsExplosive}, confidence={decision.Confidence:0.00}.",
                configuration.RouterModel));
            return decision;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return local with { UsedModelRouter = true, RouterUsage = response.Usage };
        }

        static string? NullableText(JsonElement root, string name)
        {
            var value = root.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
        }
    }

    private AiRouteDecision Deterministic(AiRequestContext context, AiConfiguration configuration)
    {
        if (NeedsSalesScopeClarification(context))
            return WithLocalRisk(new AiRouteDecision(
                ["sale"], true, AiComplexity.Normal, 0.98,
                ModelTier: "default",
                PreferredToolNames: registry.GetTool("sale.aggregate_records") is null
                    ? []
                    : ["sale.aggregate_records"],
                NeedsClarification: true,
                ClarificationQuestion: "Satış hasılatını hangi kapsam için hesaplamamı istersiniz?",
                ClarificationOptions:
                [
                    "Seçili ürünün belirtilen dönemdeki satış hasılatı",
                    "Tüm eczane satışlarının belirtilen dönemdeki hasılatı"
                ]), context.UserMessage);

        var scores = new Dictionary<string, double>(search.ScoreDomains(context.UserMessage), StringComparer.OrdinalIgnoreCase);
        var module = context.ApplicationContext.CurrentModule?.Split('.')[0];
        if (!string.IsNullOrWhiteSpace(module))
        {
            var normalized = module.Equals("products", StringComparison.OrdinalIgnoreCase) ? "product" : module;
            if (registry.Namespaces.Contains(normalized, StringComparer.OrdinalIgnoreCase)) scores[normalized] = scores.GetValueOrDefault(normalized) + 12;
        }
        var ordered = scores.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).ToArray();
        if (ordered.Length == 0)
            return WithLocalRisk(new AiRouteDecision([], false, AiComplexity.Simple, 0.25), context.UserMessage);
        var top = ordered[0].Value;
        var selected = ordered.Where(pair => pair.Value >= Math.Max(3, top * 0.55)).Select(pair => pair.Key).Take(3).ToArray();
        var confidence = Math.Min(0.98, 0.45 + top / 30 + (ordered.Length == 1 || top >= ordered.ElementAtOrDefault(1).Value * 1.6 ? 0.15 : 0));
        var complexity = selected.Length > 1 || ContainsMultiStepSignal(context.UserMessage) ? AiComplexity.Normal : AiComplexity.Simple;
        return WithLocalRisk(new AiRouteDecision(selected, true, complexity, confidence), context.UserMessage);
    }

    private static AiRouteDecision WithLocalRisk(AiRouteDecision decision, string message)
    {
        var normalized = message.ToLowerInvariant();
        var explosive = ContainsAny(normalized,
            "veritabanını sil", "veritabanini sil", "tüm kayıtları sil", "tum kayitlari sil",
            "bütün stok", "butun stok", "toplu sil", "delete all", "drop table", "truncate");
        var expensive = ContainsAny(normalized,
            "tüm ürünleri", "tum urunleri", "bütün ürünleri", "butun urunleri", "all products",
            "sınırsız", "sinirsiz", "son 10 yıl", "son 10 yil", "entire database");
        return decision with
        {
            IsExpensive = expensive,
            ExpenseReason = expensive ? "İstek çok geniş veya sınırsız bir veri/model çalışması gerektirebilir." : null,
            IsExplosive = explosive,
            ExplosionReason = explosive ? "İstek geniş kapsamlı ve geri döndürülmesi zor veri değişiklikleri içerebilir." : null
        };
    }

    private static bool ContainsAny(string value, params string[] signals) =>
        signals.Any(signal => value.Contains(signal, StringComparison.Ordinal));

    private static bool ContainsMultiStepSignal(string message) =>
        message.Contains(" and ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains(" ve ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("compare", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("karşılaştır", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsSalesScopeClarification(AiRequestContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ApplicationContext.SelectedBarcode)) return false;
        return IsSalesRevenueRequest(context.UserMessage) && !HasExplicitSalesScope(context.UserMessage);
    }

    private static bool IsSalesRevenueRequest(string value)
    {
        var message = value.ToLowerInvariant();
        return ContainsAny(message, "hasılat", "hasilat", "ciro", "revenue") &&
               ContainsAny(message, "satış", "satis", "sale");
    }

    private static bool HasExplicitSalesScope(string value)
    {
        var message = value.ToLowerInvariant();
        return ContainsAny(message,
            "seçili", "secili", "bu ürün", "bu urun", "bu ilac", "bu ilaç",
            "tüm eczane", "tum eczane", "genel satış", "genel satis", "bütün satış", "butun satis");
    }

    internal static JsonElement Message(string role, string content) =>
        JsonSerializer.SerializeToElement(new { role, content });

    private static JsonElement RouterSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            domains = new { type = "array", items = new { type = "string" } },
            needs_tools = new { type = "boolean" },
            complexity = new { type = "string", @enum = new[] { "simple", "normal", "complex" } },
            model_tier = new { type = "string", @enum = new[] { "simple", "default", "complex" } },
            tool_names = new { type = "array", items = new { type = "string" } },
            expensive = new { type = "boolean" },
            expense_reason = new { type = new[] { "string", "null" } },
            explosive = new { type = "boolean" },
            explosion_reason = new { type = new[] { "string", "null" } },
            clarification_required = new { type = "boolean" },
            clarification_question = new { type = new[] { "string", "null" } },
            clarification_options = new { type = "array", items = new { type = "string" } },
            confidence = new { type = "number" }
        },
        required = new[]
        {
            "domains", "needs_tools", "complexity", "model_tier", "tool_names",
            "expensive", "expense_reason", "explosive", "explosion_reason",
            "clarification_required", "clarification_question", "clarification_options", "confidence"
        },
        additionalProperties = false
    });
}
