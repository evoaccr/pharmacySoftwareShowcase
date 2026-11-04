using System.Text.Json;

namespace pharmacyApp.AI.Models;

public enum AiComplexity { Simple, Normal, Complex }
public enum AiAssistantStatus { Success, ClarificationRequired, ConfirmationRequired, Unsupported, Error, Cancelled }

public sealed record AiApplicationContext(
    string? CurrentModule = null,
    string? CurrentScreen = null,
    string? SelectedProductId = null,
    string? SelectedBarcode = null,
    string? SelectedPrescriptionId = null,
    string? SelectedQrCode = null,
    string? SelectedSaleId = null,
    string? CurrentUserId = null,
    string? PharmacyName = null,
    string? PharmacistName = null,
    string? UserDisplayName = null)
{
    public IReadOnlyDictionary<string, string> RelevantValues()
    {
        var values = new Dictionary<string, string>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) values[name] = value; }
        Add("current_module", CurrentModule);
        Add("current_screen", CurrentScreen);
        Add("selected_product_id", SelectedProductId);
        Add("selected_barcode", SelectedBarcode);
        Add("selected_prescription_id", SelectedPrescriptionId);
        Add("selected_qr_code", SelectedQrCode);
        Add("selected_sale_id", SelectedSaleId);
        Add("pharmacy_name", PharmacyName);
        Add("pharmacist_name", PharmacistName);
        Add("pharmacist_address", string.IsNullOrWhiteSpace(PharmacistName) ? null : $"Eczacı {PharmacistName.Trim()}");
        Add("user_display_name", UserDisplayName);
        return values;
    }
}

public interface IAiApplicationContextSource
{
    AiApplicationContext GetAiApplicationContext();
}

public sealed record AiRequestContext(
    string SessionId,
    string UserMessage,
    AiApplicationContext ApplicationContext,
    IReadOnlySet<string> Permissions,
    IProgress<AiActivityUpdate>? Progress = null,
    string? RouterModelOverride = null);

public sealed record AiActivityUpdate(string Message, string? DebugDetail = null, string? Model = null);

public sealed record AiRouteDecision(
    IReadOnlyList<string> Domains,
    bool NeedsTools,
    AiComplexity Complexity,
    double Confidence,
    bool UsedModelRouter = false,
    string ModelTier = "auto",
    IReadOnlyList<string>? PreferredToolNames = null,
    bool IsExpensive = false,
    string? ExpenseReason = null,
    bool IsExplosive = false,
    string? ExplosionReason = null,
    bool NeedsClarification = false,
    string? ClarificationQuestion = null,
    IReadOnlyList<string>? ClarificationOptions = null,
    AiUsage? RouterUsage = null)
{
    public bool RequiresApproval => IsExpensive || IsExplosive;
}

public sealed record AiUsage(
    int InputTokens = 0,
    int CachedInputTokens = 0,
    int CacheWriteTokens = 0,
    int OutputTokens = 0,
    int ReasoningTokens = 0);

public sealed record AiModelUsage(string Stage, string Model, AiUsage Usage);

public sealed record AiToolCall(string CallId, string Name, string ArgumentsJson);

public sealed record AiAssistantBlock(string Type, string? Title, object? Data);
public sealed record AiSuggestedAction(string Id, string Label);

public sealed record AiAssistantResult(
    AiAssistantStatus Status,
    string Message,
    IReadOnlyList<AiAssistantBlock>? Blocks = null,
    IReadOnlyList<AiSuggestedAction>? SuggestedActions = null,
    PendingAiActionView? PendingConfirmation = null,
    string? DebugTraceId = null,
    string? DebugSummary = null,
    PendingAiRouteApprovalView? PendingRouteApproval = null,
    string? ModelUsed = null,
    IReadOnlyList<AiModelUsage>? UsageByModel = null)
{
    public static AiAssistantResult Error(string message, string? traceId = null) =>
        new(AiAssistantStatus.Error, message, DebugTraceId: traceId);
}

public sealed record PendingAiActionView(
    string Id,
    string ToolName,
    string Summary,
    string ArgumentsJson,
    DateTimeOffset ExpiresAt);

public sealed record PendingAiRouteApprovalView(
    string Id,
    string Summary,
    bool IsExpensive,
    bool IsExplosive,
    DateTimeOffset ExpiresAt);

public sealed record AiConversationMessage(string Role, string Content, DateTimeOffset CreatedAt);

public sealed record MissingToolSuggestion(
    string OriginalRequest,
    string MissingCapability,
    string SuggestedNamespace,
    string SuggestedToolName,
    string Description,
    IReadOnlyList<string> SuggestedParameters,
    string SuggestedResult,
    string Reason,
    IReadOnlyList<string> ClosestExistingTools,
    string EstimatedRisk);

public sealed record AiDebugTrace(
    string RequestId,
    DateTimeOffset StartedAt,
    List<string> Events,
    List<string> CandidateTools,
    List<string> ExecutedTools)
{
    public static AiDebugTrace Start() => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, [], [], []);
    public void Add(string value) => Events.Add(value);
    public string ToSummary()
    {
        var lines = new List<string>
        {
            $"Request: {RequestId}",
            $"Started (UTC): {StartedAt:O}",
            "",
            "Events:"
        };
        lines.AddRange(Events.Select(value => "- " + value));
        lines.Add("");
        lines.Add("Candidate tools:");
        lines.AddRange(CandidateTools.Count == 0
            ? ["- none"]
            : CandidateTools.Select(value => "- " + value));
        lines.Add("");
        lines.Add("Executed tools:");
        lines.AddRange(ExecutedTools.Count == 0
            ? ["- none"]
            : ExecutedTools.Select(value => "- " + value));
        return string.Join(Environment.NewLine, lines);
    }
}
