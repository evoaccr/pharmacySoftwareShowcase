using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Routing;

namespace pharmacyApp.AI.Runtime;

public sealed class AiChatService(
    AiOrchestrator orchestrator,
    AiConfirmationService confirmations,
    AiRouteApprovalService routeApprovals,
    AiToolExecutionGateway gateway)
{
    public Task<AiAssistantResult> SendAsync(AiRequestContext context, CancellationToken cancellationToken = default) =>
        orchestrator.RunAsync(context, cancellationToken);

    public async Task<AiAssistantResult> ConfirmAsync(
        string pendingActionId,
        string userId,
        CancellationToken cancellationToken = default,
        IProgress<AiActivityUpdate>? progress = null)
    {
        var action = confirmations.GetAction(pendingActionId, userId);
        if (action?.ContinuationState is not AiContinuationState state)
            return AiAssistantResult.Error("Onay bekleyen işlem bulunamadı veya süresi doldu.");
        if (progress is not null) state = state with { Context = state.Context with { Progress = progress } };
        if (!confirmations.Confirm(pendingActionId, userId))
            return AiAssistantResult.Error("İşlem onaylanamadı.");
        var result = await gateway.ExecuteConfirmedAsync(pendingActionId, userId, cancellationToken);
        return result.IsSuccess
            ? await orchestrator.ResumeConfirmedAsync(state, action.Call, result, cancellationToken)
            : AiAssistantResult.Error(result.SafeMessage ?? "Onaylanan işlem tamamlanamadı.");
    }

    public void Reject(string pendingActionId, string userId) => confirmations.Reject(pendingActionId, userId);

    public async Task<AiAssistantResult> ApproveRouteAsync(
        string pendingApprovalId,
        string userId,
        CancellationToken cancellationToken = default,
        IProgress<AiActivityUpdate>? progress = null)
    {
        var approval = routeApprovals.Take(pendingApprovalId, userId);
        if (approval is null)
            return AiAssistantResult.Error("Onay bekleyen yönlendirme bulunamadı veya süresi doldu.");
        var context = progress is null
            ? approval.Context
            : approval.Context with { Progress = progress };
        return await orchestrator.ResumeApprovedRouteAsync(
            context, approval.Route, approval.Trace, cancellationToken);
    }

    public bool RejectRoute(string pendingApprovalId, string userId) =>
        routeApprovals.Reject(pendingApprovalId, userId);
}
