using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Routing;
using pharmacyApp.AI.Search;

namespace pharmacyApp.AI.Runtime;

public sealed record AiRuntime(
    AiChatService Chat,
    AiDiagnosticsService Diagnostics,
    MissingToolStore MissingTools,
    IAiConfigurationProvider Configuration,
    IAiToolRegistry Registry,
    IAiModelTransport Transport);

public static class AiRuntimeFactory
{
    public static AiRuntime Create()
    {
        IAiConfigurationProvider configuration = new AiConfigurationProvider();
        IAiToolRegistry registry = AiToolRegistry.Discover();
        IAiModelTransport transport = new OpenAiResponsesTransport(configuration);
        IAiToolSearchService search = new AiToolSearchService(registry);
        IAiPromptProvider prompts = new AiPromptProvider();
        IAiRouter router = new AiRouter(registry, search, transport, configuration, prompts);
        IAiModelSelector selector = new AiModelSelector(configuration);
        IAiConversationManager conversations = new AiConversationManager(configuration);
        var confirmations = new AiConfirmationService();
        var routeApprovals = new AiRouteApprovalService();
        var operationalLog = new AiOperationalLog();
        var gateway = new AiToolExecutionGateway(
            registry, new AppAiPermissionService(), confirmations, configuration, operationalLog);
        var missing = new MissingToolStore();
        var orchestrator = new AiOrchestrator(
            router, search, registry, selector, transport, gateway, conversations, configuration, prompts,
            routeApprovals, missing, new AiUsageTelemetry(), operationalLog);
        return new AiRuntime(
            new AiChatService(orchestrator, confirmations, routeApprovals, gateway),
            new AiDiagnosticsService(configuration, registry, transport),
            missing, configuration, registry, transport);
    }
}
