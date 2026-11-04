using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Conversation;

public interface IAiConversationManager
{
    IReadOnlyList<AiConversationMessage> GetRecent(string sessionId);
    void Add(string sessionId, string role, string content);
    void Clear(string sessionId);
}

public sealed class AiConversationManager(IAiConfigurationProvider configurationProvider) : IAiConversationManager
{
    private readonly object sync = new();
    private readonly Dictionary<string, List<AiConversationMessage>> conversations = new(StringComparer.Ordinal);

    public IReadOnlyList<AiConversationMessage> GetRecent(string sessionId)
    {
        lock (sync)
        {
            if (!conversations.TryGetValue(sessionId, out var messages)) return [];
            return messages.TakeLast(configurationProvider.GetCurrent().MaxConversationMessages).ToArray();
        }
    }

    public void Add(string sessionId, string role, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        lock (sync)
        {
            if (!conversations.TryGetValue(sessionId, out var messages)) conversations[sessionId] = messages = [];
            messages.Add(new AiConversationMessage(role, content.Trim(), DateTimeOffset.UtcNow));
            var maximumStored = configurationProvider.GetCurrent().MaxConversationMessages * 3;
            if (messages.Count > maximumStored) messages.RemoveRange(0, messages.Count - maximumStored);
        }
    }

    public void Clear(string sessionId)
    {
        lock (sync) conversations.Remove(sessionId);
    }
}
