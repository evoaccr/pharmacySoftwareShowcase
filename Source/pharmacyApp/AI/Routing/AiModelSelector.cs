using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Tools;

namespace pharmacyApp.AI.Routing;

public sealed record AiModelSelection(string Model, string ReasoningEffort);

public interface IAiModelSelector
{
    AiModelSelection Select(AiRouteDecision route, IReadOnlyList<AiToolDefinition> tools);
}

public sealed class AiModelSelector(IAiConfigurationProvider configurationProvider) : IAiModelSelector
{
    public AiModelSelection Select(AiRouteDecision route, IReadOnlyList<AiToolDefinition> tools)
    {
        var configuration = configurationProvider.GetCurrent();
        var hasWrite = tools.Any(tool => tool.Risk != AiToolRisks.ReadOnly);
        var routedComplexity = route.ModelTier switch
        {
            "complex" => AiComplexity.Complex,
            "default" => AiComplexity.Normal,
            "simple" => AiComplexity.Simple,
            _ => route.Complexity
        };
        var complexity = hasWrite && routedComplexity == AiComplexity.Simple ? AiComplexity.Normal : routedComplexity;
        return complexity switch
        {
            AiComplexity.Complex => new(configuration.ComplexModel, configuration.ComplexReasoningEffort),
            AiComplexity.Normal => new(configuration.DefaultModel, configuration.DefaultReasoningEffort),
            _ => new(configuration.SimpleModel, configuration.SimpleReasoningEffort)
        };
    }
}
