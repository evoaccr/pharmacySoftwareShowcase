using System.IO;

namespace pharmacyApp.AI.Runtime;

public interface IAiPromptProvider
{
    string AssistantCore { get; }
    string Router { get; }
    string WorkflowRouter { get; }
    string DebugToolGap { get; }
}

public sealed class AiPromptProvider : IAiPromptProvider
{
    private static string Read(string name, string fallback)
    {
        var path = Path.Combine(pharmacyApp.Infrastructure.AppPaths.RootDirectory, "AI", "Prompts", name);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback; }
        catch (IOException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
    }

    public string AssistantCore => Read("assistant-core.md", "You are Pharmacy AI Assistant. Respond in English. Use application tools for current application data and never invent tool results.");
    public string Router => Read("router.md", "Return only the requested routing JSON.");
    public string WorkflowRouter => Read("workflow-router.md", "Return only the requested workflow routing JSON.");
    public string DebugToolGap => Read("debug-tool-gap.md", "Report a missing capability only after available tools are insufficient.");
}
