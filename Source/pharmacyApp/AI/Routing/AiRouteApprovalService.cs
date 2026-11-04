using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Routing;

internal sealed record PendingAiRouteApproval(
    string Id,
    AiRequestContext Context,
    AiRouteDecision Route,
    AiDebugTrace Trace,
    DateTimeOffset ExpiresAt,
    string Summary);

public sealed class AiRouteApprovalService
{
    private readonly object sync = new();
    private readonly Dictionary<string, PendingAiRouteApproval> pending = new(StringComparer.Ordinal);

    internal PendingAiRouteApprovalView Create(
        AiRequestContext context,
        AiRouteDecision route,
        AiDebugTrace trace,
        int lifetimeMinutes)
    {
        var reasons = new List<string>();
        if (route.IsExpensive)
            reasons.Add("Maliyet uyarısı: " + (route.ExpenseReason ?? "İstek olağandan fazla kaynak tüketebilir."));
        if (route.IsExplosive)
            reasons.Add("Güvenlik uyarısı: " + (route.ExplosionReason ?? "İstek geniş veya geri döndürülmesi zor değişiklikler oluşturabilir."));
        reasons.Add("Emin misiniz?");
        var summary = string.Join(Environment.NewLine + Environment.NewLine, reasons);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(lifetimeMinutes);
        var approval = new PendingAiRouteApproval(
            Guid.NewGuid().ToString("N"), context, route, trace, expiresAt, summary);
        lock (sync) pending[approval.Id] = approval;
        return new PendingAiRouteApprovalView(
            approval.Id, summary, route.IsExpensive, route.IsExplosive, expiresAt);
    }

    internal PendingAiRouteApproval? Take(string id, string userId)
    {
        lock (sync)
        {
            if (!pending.Remove(id, out var approval) || IsInvalid(approval, userId)) return null;
            return approval;
        }
    }

    public bool Reject(string id, string userId)
    {
        lock (sync)
        {
            if (!pending.TryGetValue(id, out var approval) || IsInvalid(approval, userId)) return false;
            pending.Remove(id);
            return true;
        }
    }

    private static bool IsInvalid(PendingAiRouteApproval approval, string userId) =>
        approval.ExpiresAt <= DateTimeOffset.UtcNow ||
        !string.Equals(approval.Context.ApplicationContext.CurrentUserId ?? string.Empty, userId, StringComparison.Ordinal);
}
