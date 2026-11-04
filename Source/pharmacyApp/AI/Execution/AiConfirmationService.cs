using pharmacyApp.AI.Models;
using pharmacyApp.AI.Tools;

namespace pharmacyApp.AI.Execution;

public sealed record PendingAiAction(
    string Id,
    AiRequestContext Context,
    AiToolCall Call,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Summary,
    object? ContinuationState,
    bool Confirmed = false);

public sealed class AiConfirmationService
{
    private readonly object sync = new();
    private readonly Dictionary<string, PendingAiAction> pending = new(StringComparer.Ordinal);

    public PendingAiActionView Create(
        AiRequestContext context,
        AiToolCall call,
        AiToolDefinition definition,
        object? continuationState,
        int lifetimeMinutes)
    {
        var now = DateTimeOffset.UtcNow;
        var action = new PendingAiAction(
            Guid.NewGuid().ToString("N"), context, call, now, now.AddMinutes(lifetimeMinutes),
            $"{definition.Description}\n\nTool: {definition.Name}", continuationState);
        lock (sync) pending[action.Id] = action;
        return View(action);
    }

    public bool Confirm(string id, string userId)
    {
        lock (sync)
        {
            if (!pending.TryGetValue(id, out var action) || IsInvalid(action, userId)) return false;
            pending[id] = action with { Confirmed = true };
            return true;
        }
    }

    public void Reject(string id, string userId)
    {
        lock (sync)
            if (pending.TryGetValue(id, out var action) && !IsInvalid(action, userId)) pending.Remove(id);
    }

    public PendingAiAction? TakeConfirmed(string id, string userId)
    {
        lock (sync)
        {
            if (!pending.Remove(id, out var action) || !action.Confirmed || IsInvalid(action, userId)) return null;
            return action;
        }
    }

    public PendingAiActionView? Get(string id)
    {
        lock (sync) return pending.TryGetValue(id, out var action) && action.ExpiresAt > DateTimeOffset.UtcNow ? View(action) : null;
    }

    internal PendingAiAction? GetAction(string id, string userId)
    {
        lock (sync)
            return pending.TryGetValue(id, out var action) && !IsInvalid(action, userId) ? action : null;
    }

    private static bool IsInvalid(PendingAiAction action, string userId) =>
        action.ExpiresAt <= DateTimeOffset.UtcNow ||
        !string.Equals(action.Context.ApplicationContext.CurrentUserId, userId, StringComparison.Ordinal);

    private static PendingAiActionView View(PendingAiAction action) =>
        new(action.Id, action.Call.Name, action.Summary, action.Call.ArgumentsJson, action.ExpiresAt);
}
