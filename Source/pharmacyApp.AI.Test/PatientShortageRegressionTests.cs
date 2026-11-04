using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Workflows;

internal static class PatientShortageRegressionTests
{
    private const string Plan = """
    {"answer":null,"explosive":false,"explosion_reason":null,"corrected_request":"","correction_confidence":0,"medicine_names":[],"use_remembered_medicine":false,"needs_advanced_planning":false,"advanced_planning_reason":null,"workflow_queue":["hasta_eslestir","medula_son_receteyi_ac","recete_stok_eksiklerini_hesapla"],"workflow_inputs":[{"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"Ada"}]},{"workflow_name":"medula_son_receteyi_ac","arguments":[]},{"workflow_name":"recete_stok_eksiklerini_hesapla","arguments":[]}]}
    """;
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    public static async Task RunAsync()
    {
        Check(pharmacyApp.Localization.UiText.Translate("Reçetenin stok eksiklerini hesapla") == "Calculate Prescription Shortages", "Shortage workflow alias is English.");
        Check(pharmacyApp.Localization.UiText.Translate("ADA TWO — TEST-RX reçetesi: kayıtlı raf stoklarına göre 1 ürün eksik.").Contains("products have shortages", StringComparison.Ordinal), "Dynamic shortage summary is English.");
        var config = new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true });
        var transport = new CountTransport(new ScriptedTransport(true, ScriptedTransport.Final(Plan)));
        await VerifyChain(config, transport, "Tell me the missing contents of Ada's last prescription", false);
        await VerifyChain(config, new CountTransport(new ScriptedTransport(true, ScriptedTransport.Final(Plan))),
            "Compute stock deficits for the newest prescription belonging to Ada", false);
        foreach (var (stock, quantity, failed, expected) in new[]
        {
            ("2", "3", false, "3"), ("-5", "3", false, "5"), ("20", "3", false, ""),
            ("unknown", "3", true, ""), ("2", "unknown", true, ""), ("2", "1.5", true, "")
        })
        {
            var tools = new Fixtures { Stock = stock, Quantity = quantity };
            var output = JsonSerializer.SerializeToElement(new { metadata = new { prescription = tools.Prescription() } });
            var result = await new PrescriptionShortagesWorkflow().ExecuteAsync(new AiWorkflowContext("test", "test",
                new AiApplicationContext(), new Dictionary<string, JsonElement> { ["rx"] = output }, [], ToolExecutor: tools));
            Check((result.Status == AiWorkflowStepStatus.Failed) == failed, "Unverified quantities/stock must fail without fabricated shortage.");
            if (!failed)
            {
                var rows = result.Output.GetProperty("rows");
                Check(expected.Length == 0 ? rows.GetArrayLength() == 0 && result.Table is null :
                    rows.GetArrayLength() == 1 && rows[0].GetProperty("missing_quantity").GetString() == expected,
                    "Shortage must group prescription packs, exclude sufficient stock and never use target-stock shortage.");
                Check(tools.StockCalls <= 2, "Repeated drug lines need one stock lookup per barcode.");
            }
        }
        var zero = new Fixtures { NoPatients = true };
        var match = await new PatientMatchWorkflow().ExecuteAsync(new AiWorkflowContext("Ada", "Ada", new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
            ToolExecutor: zero, Parameters: new Dictionary<string, string> { ["query"] = "Ada" }));
        Check(match.Status == AiWorkflowStepStatus.Failed && zero.PatientQueries.SequenceEqual(["ADA"]), "A first name is searched before reporting no match.");
        Console.WriteLine("PASS first-name selection and prescription shortage regressions");
    }

    public static async Task RunLiveAsync(IAiConfigurationProvider config, IAiModelTransport transport)
    {
        foreach (var prompt in new[]
        {
            "Tell me the missing contents of Ada's last prescription",
            "Ada'nın son reçetesinde eksik olan ilaçları söyle",
            "Which medicines are missing from Ada's latest prescription?",
            "Please calculate the missing medicines for Ada's most recent prescription",
            "Lütfen Ada adlı hastanın en son reçetesindeki eksik ilaçları hesapla"
        })
        {
            var counted = new CountTransport(transport);
            await VerifyChain(config, counted, prompt, true);
            Console.WriteLine("PASS synthetic patient-shortage, model calls=" + counted.Calls + ": " + prompt);
        }
    }

    private static async Task VerifyChain(IAiConfigurationProvider config, CountTransport transport, string prompt, bool fullRegistry)
    {
        var tools = new Fixtures();
        var registry = fullRegistry ? AiWorkflowRegistry.Discover() :
            new AiWorkflowRegistry([new PatientMatchWorkflow(), new MedulaLatestPrescriptionWorkflow(), new PrescriptionShortagesWorkflow()]);
        var directory = Path.Combine(Path.GetTempPath(), "pharmacy-patient-shortage-" + Guid.NewGuid().ToString("N"));
        var chat = new WorkflowRouterChatService(registry, transport, config, new AiConversationManager(config),
            new AiUsageTelemetry(Path.Combine(directory, "usage.json")), new AiPromptProvider(), tools);
        if (!fullRegistry)
        {
            var tryPlan = typeof(WorkflowRouterChatService).GetMethod("TryBuildPrescriptionShortageState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            foreach (var extra in new[] { " in July", " excluding TEST A", " from live Medula only" })
                Check(!(bool)tryPlan.Invoke(chat, [new AiRequestContext("scope", prompt + extra, new AiApplicationContext(), new HashSet<string>()), config.GetCurrent(), null])!,
                    "Automatic planning cannot silently discard additional date, medicine or source constraints.");
        }
        var result = await chat.SendAsync(new AiRequestContext(Guid.NewGuid().ToString("N"), prompt,
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "test"), new HashSet<string>()));
        Check(result.PendingSelection is { AllowsMultiple: false } selection && selection.Options.Count == 2,
            "A partial patient name must search and present both matches. Actual: " + result.Message + " / " + result.DebugSummary);
        Check(tools.StockCalls == 0 && tools.SelectedPatientId is null, "Nothing beyond patient matching runs before selection.");
        var initialCalls = transport.Calls;
        result = await chat.ContinueSelectionAsync(result.PendingSelection!.Id, "test", ["502"]);
        Check(transport.Calls == initialCalls && initialCalls <= 1 && tools.SelectedPatientId == 502, "Selection must continue the saved queue with the selected patient's exact ID and no second model call. Actual: " + result.Message + " / " + result.DebugSummary);
        Check(result.Prescription is null && result.Table?.Rows.Count == 1 &&
            result.Table.Rows[0].GetValueOrDefault("missing_quantity") == "3" && tools.StockCalls == 2,
            "Must display shortages, not the full prescription or a forecast. Actual: " + result.Message + " / " + result.DebugSummary);
        Check(result.WorkflowQueue?.Contains("recete_stok_eksiklerini_hesapla") == true, "Shortage workflow must execute.");
    }

    private sealed class CountTransport(IAiModelTransport inner) : IAiModelTransport
    {
        public int Calls { get; private set; }
        public bool IsConfigured => inner.IsConfigured;
        public string ConfigurationDescription => inner.ConfigurationDescription;
        public Task<AiModelResponse> SendAsync(AiModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; return inner.SendAsync(request, cancellationToken); }
    }

    private sealed class Fixtures : IAiWorkflowToolExecutor
    {
        public string Stock { get; init; } = "2";
        public string Quantity { get; init; } = "3";
        public bool NoPatients { get; init; }
        public int StockCalls { get; private set; }
        public int? SelectedPatientId { get; private set; }
        public List<string> PatientQueries { get; } = [];
        public bool HasTool(string tool) => tool is "sale.search_patients" or "sale.search_records" or "medula.prescription_get"
            or "medula.patient_prescriptions_search" or "medula.prescriptions_search" or "product.get";
        public object Prescription() => new
        {
            prescription_number = "TEST-RX", patient_name = "ADA TWO", fields = new { prescription_date = "01.11.2026" },
            drugs = new[] { new { barcode = "8690000000001", name = "TEST A", quantity = Quantity },
                new { barcode = "8690000000001", name = "TEST A", quantity = "2" },
                new { barcode = "8690000000002", name = "TEST B", quantity = "1" } }
        };
        public Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string tool, JsonElement args, CancellationToken cancellationToken = default)
        {
            object output;
            switch (tool)
            {
                case "sale.search_patients":
                    PatientQueries.Add(args.GetProperty("query").GetString()!);
                    output = new { patients = NoPatients ? Array.Empty<object>() : new object[]
                        { new { patient_record_id = 501, name = "ADA ONE" }, new { patient_record_id = 502, name = "ADA TWO" } } };
                    break;
                case "sale.search_records":
                    SelectedPatientId = args.GetProperty("patient_record_id").GetInt32();
                    output = new { sales = new[] { new { prescription_no = "TEST-RX", timestamp = "2026-11-01T10:00:00" } } };
                    break;
                case "medula.prescription_get": output = Prescription(); break;
                case "product.get":
                    StockCalls++;
                    var barcode = args.GetProperty("barcode").GetString()!;
                    output = new { found = true, product = new { summary = new { barcode, shelf_stock_quantity = barcode.EndsWith("1") ? Stock : "20" } } };
                    break;
                default: throw new InvalidOperationException("Unexpected live/unsupported fixture tool: " + tool);
            }
            return Task.FromResult(new AiWorkflowToolExecution(tool, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(output), null, 0));
        }
    }
}

