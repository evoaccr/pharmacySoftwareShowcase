using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Workflows;

internal static class BranchValidationRegressionTests
{
    public static async Task RunAsync(string testDirectory)
    {
        var duplicateOptions = "[{\"id\":\"same\",\"label\":\"Seçenek A\",\"queue\":[]},{\"id\":\"same\",\"label\":\"Seçenek B\",\"queue\":[]}]";
        var uniqueOptions = "[{\"id\":\"first\",\"label\":\"Seçenek A\",\"queue\":[]},{\"id\":\"second\",\"label\":\"Seçenek B\",\"queue\":[]}]";
        var baseContext = new AiWorkflowContext("İşlem seç", "İşlem seç", new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
            Parameters: new Dictionary<string, string> { ["options_json"] = duplicateOptions });
        Require((await new WorkflowBranchSelectionWorkflow().ExecuteAsync(baseContext)).Status == AiWorkflowStepStatus.Failed,
            "Duplicate option IDs cannot identify which label the user selected.");
        Require((await new WorkflowBranchSelectionWorkflow().ExecuteAsync(baseContext with
        {
            Parameters = new Dictionary<string, string> { ["options_json"] = uniqueOptions }, SelectedOptionIds = ["first", "second"]
        })).Status == AiWorkflowStepStatus.Failed, "Direct workflow calls also enforce single-selection cardinality.");
        var singleQueue = "[\"branch_probe_0\"]";
        var cases = new (string Name, string Queue, string Inputs, bool Prefix)[]
        {
            ("unknown workflow", "[\"branch_probe_0\",\"does_not_exist\"]", "{}", false),
            ("repeated workflow", "[\"branch_probe_0\",\"branch_probe_0\"]", "{}", false),
            ("nested branch", "[\"is_akisi_dali_sec\"]", "{}", false),
            ("duplicate workflow inputs", singleQueue, "{\"branch_probe_0\":{},\"branch_probe_0\":{}}", false),
            ("duplicate argument", singleQueue, "{\"branch_probe_0\":{\"value\":\"first\",\"value\":\"second\"}}", false),
            ("non-object inputs", singleQueue, "[]", false),
            ("unsupported argument", singleQueue, "{\"branch_probe_0\":{\"unknown\":\"value\"}}", false),
            ("combined queue bound", JsonSerializer.Serialize(Enumerable.Range(0, 19).Select(index => "branch_probe_" + index)), "{}", true)
        };
        foreach (var scenario in cases)
        {
            var run = CreateChat(testDirectory, scenario.Queue, scenario.Inputs, scenario.Prefix);
            var selection = await run.Chat.SendAsync(Request(run.Session));
            Require(selection.PendingSelection is not null, "Expected planned branch options for " + scenario.Name);
            var before = run.Executions.Count;
            var result = await run.Chat.ContinueSelectionAsync(selection.PendingSelection!.Id, "branch-test-user", ["chosen"]);
            Require(run.Executions.Count == before && result.PendingSelection is null,
                "Invalid branch must fail closed without executing a shortened or guessed queue: " + scenario.Name);
            Require(run.Transport.Requests.Count == 1, "Branch rejection must not invoke another model.");
        }
        var multi = CreateChat(testDirectory, singleQueue, "{}", false);
        var multiSelection = await multi.Chat.SendAsync(Request(multi.Session));
        _ = await multi.Chat.ContinueSelectionAsync(multiSelection.PendingSelection!.Id, "branch-test-user", ["chosen", "alternative"]);
        Require(multi.Executions.Count == 0, "A single-choice branch rejects multiple options instead of silently taking the first.");
        var valid = CreateChat(testDirectory, singleQueue, "{\"branch_probe_0\":{\"value\":\"saved\"}}", false);
        var validSelection = await valid.Chat.SendAsync(Request(valid.Session));
        _ = await valid.Chat.ContinueSelectionAsync(validSelection.PendingSelection!.Id, "branch-test-user", ["chosen"]);
        Require(valid.Executions.SequenceEqual(["branch_probe_0:saved"]) && valid.Transport.Requests.Count == 1,
            "A valid saved branch executes its exact parameters without a second model.");
        Console.WriteLine("PASS workflow-branch-validation");
    }

    private static (WorkflowRouterChatService Chat, ScriptedTransport Transport, List<string> Executions, string Session) CreateChat(
        string directory, string queue, string inputs, bool prefix)
    {
        var options = "[{\"id\":\"chosen\",\"label\":\"Seçenek A\",\"queue\":" + queue + ",\"inputs\":" + inputs + "}," +
                      "{\"id\":\"alternative\",\"label\":\"Seçenek B\",\"queue\":[\"branch_probe_19\"],\"inputs\":{}}]";
        var response = JsonSerializer.Serialize(new
        {
            answer = (string?)null, explosive = false, explosion_reason = (string?)null,
            corrected_request = "Test işlemini seç", correction_confidence = 0.0,
            medicine_names = Array.Empty<string>(), use_remembered_medicine = false,
            workflow_queue = prefix ? new[] { "branch_probe_19", "is_akisi_dali_sec" } : new[] { "is_akisi_dali_sec" },
            workflow_inputs = new[] { new { workflow_name = "is_akisi_dali_sec", arguments = new[] { new { name = "options_json", value = options } } } }
        });
        var transport = new ScriptedTransport(true, ScriptedTransport.Final(response));
        var executions = new List<string>();
        var workflows = Enumerable.Range(0, 20).Select(index => (IAiWorkflow)new ProbeWorkflow("branch_probe_" + index, executions)).ToList();
        workflows.Add(new WorkflowBranchSelectionWorkflow());
        var configuration = new FixedAiConfigurationProvider(new AiConfiguration());
        var chat = new WorkflowRouterChatService(new AiWorkflowRegistry(workflows), transport, configuration,
            new AiConversationManager(configuration), new AiUsageTelemetry(Path.Combine(directory, "branch-usage-" + Guid.NewGuid().ToString("N") + ".json")),
            new TestPrompts(), new TestWorkflowToolExecutor(), new AiRequestCaptureStore(Path.Combine(directory, "branch-capture-" + Guid.NewGuid().ToString("N"))));
        return (chat, transport, executions, Guid.NewGuid().ToString("N"));
    }

    private static AiRequestContext Request(string session) => new(session, "Test işlemini seç",
        new AiApplicationContext(CurrentModule: "home", CurrentUserId: "branch-test-user"), new HashSet<string>());
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ProbeWorkflow(string name, List<string> executions) : IAiWorkflow
    {
        public AiWorkflowDefinition Definition { get; } = new(name, "Deneme", "Harmless test counter.", Parameters: new Dictionary<string, string> { ["value"] = "Saved value." });
        public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
        {
            executions.Add(name + ":" + context.Parameters?.GetValueOrDefault("value"));
            return Task.FromResult(new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, "Tamamlandı.", JsonSerializer.SerializeToElement(new { })));
        }
    }
}
