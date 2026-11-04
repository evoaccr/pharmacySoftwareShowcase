using System.Text.Json;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Workflows;

internal static class PatientNameResolutionTests
{
    public static async Task RunAsync()
    {
        foreach (var pair in new[] { (Request: "Deniz Örneğin", Name: "DENİZ ÖRNEK"), (Request: "Deniz Çeliğin", Name: "DENİZ ÇELİK") })
        {
            var executor = new DirectoryFixture([pair.Name]);
            var result = await Match(pair.Request, executor);
            Require(result.Status == AiWorkflowStepStatus.Completed && result.Output.GetProperty("selected_patient").GetProperty("name").GetString() == pair.Name,
                "Possessive suffix and one consonant-softening edit resolve only against real directory candidates.");
            Require(executor.Queries.Count <= 4 && executor.Queries[0] == pair.Request.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("tr-TR")),
                "Exact full-name lookup occurs first and recovery remains bounded.");
        }
        var exact = new DirectoryFixture(["DENİZ ÖRNEĞİN"]);
        Require((await Match("Deniz Örneğin", exact)).Status == AiWorkflowStepStatus.Completed && exact.Queries.Count == 1,
            "A genuine surname ending in a suffix is never modified when its exact directory record exists.");
        var ambiguous = await Match("Deniz Örneğin", new DirectoryFixture(["DENİZ ÖRNEK", "DENİZ ÖRNEG"]));
        Require(ambiguous.Status == AiWorkflowStepStatus.AwaitingSelection && ambiguous.SelectionOptions?.Count == 2,
            "Two plausible directory candidates require a choice rather than an arbitrary patient.");
        Require((await Match("Deniz Örneğin", new DirectoryFixture(["DENİZ DOĞAN", "DERYA ÖRNEK"]))).Status == AiWorkflowStepStatus.Failed,
            "Suffix recovery does not broaden unrelated surnames or first names.");
        Console.WriteLine("PASS patient-name-resolution");
    }

    private static Task<AiWorkflowStepResult> Match(string query, IAiWorkflowToolExecutor executor) =>
        new PatientMatchWorkflow().ExecuteAsync(new AiWorkflowContext(query, query, new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
            ToolExecutor: executor, Parameters: new Dictionary<string, string> { ["query"] = query }));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class DirectoryFixture(string[] names) : IAiWorkflowToolExecutor
    {
        public List<string> Queries { get; } = [];
        public bool HasTool(string name) => name == "sale.search_patients";
        public Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            var query = arguments.GetProperty("query").GetString()!;
            Queries.Add(query);
            var patients = names.Select((name, index) => new { patient_record_id = index + 1, name, patient_tc = "" })
                .Where(patient => patient.name.Contains(query, StringComparison.Ordinal)).ToArray();
            return Task.FromResult(new AiWorkflowToolExecution(toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { patients }), null, 0));
        }
    }
}
