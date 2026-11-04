using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Workflows;

internal static class RouterPlanningRegressionTests
{
    public static async Task RunAsync()
    {
        var parsePeriod = typeof(WorkflowRouterChatService).GetMethod("TryParsePeriodOnly",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var (text, name, value) in new[]
        {
            ("60 days", "period_days", "60"), ("show last 2 months", "period_months", "2"),
            ("the past 14 days instead", "period_days", "14"), ("60 günlük", "period_days", "60")
        })
        {
            object?[] parameters = [text, new Dictionary<string, string>(), null, null];
            Assert((bool)parsePeriod.Invoke(null, parameters)! && (string)parameters[2]! == name && (string)parameters[3]! == value,
                "English and Turkish period-only follow-ups reuse the saved scope: " + text);
        }
        foreach (var text in new[] { "60 days retail only", "show last 2 months for a different patient", "0 days", "5000 days" })
            Assert(!(bool)parsePeriod.Invoke(null, [text, new Dictionary<string, string>(), null, null])!,
                "Compound or invalid follow-ups cannot silently discard their filters: " + text);

        var patientRegistry = new AiWorkflowRegistry([new PatientMatchWorkflow(), new PatientSalesContentsWorkflow()]);
        var parsePlan = typeof(WorkflowRouterChatService).GetMethod("ParsePlan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var reusePatient = typeof(WorkflowRouterChatService).GetMethod("ShouldReuseRememberedPatient",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var (patientChat, _) = Chat([new PatientMatchWorkflow(), new PatientSalesContentsWorkflow()]);
        var patientSelection = JsonSerializer.SerializeToElement(new
        {
            patient_record_id = 701, selected_patient = new { patient_record_id = 701, name = "DENİZ ÖRNEK" }
        });
        foreach (var text in new[] { "Show the same patient's sales with line contents.", "Show this patient's sale contents." })
        {
            var plan = parsePlan.Invoke(null, [ScriptedTransport.Final(Plan(["hasta_satis_iceriklerini_gor"], corrected: text)), text, patientRegistry]);
            Assert((bool)reusePatient.Invoke(patientChat, ["planning-test", plan, patientSelection])!,
                "English explicit patient references retain the confirmed identity: " + text);
        }
        var freshPlan = parsePlan.Invoke(null, [ScriptedTransport.Final(Plan(["hasta_eslestir", "hasta_satis_iceriklerini_gor"],
            new() { ["hasta_eslestir"] = new() { ["query"] = "Ada Test" } }, corrected: "Same sales for Ada Test")),
            "Same sales for Ada Test", patientRegistry]);
        Assert(!(bool)reusePatient.Invoke(patientChat, ["planning-test", freshPlan, patientSelection])!,
            "An explicitly new patient wins over English same-task phrasing.");

        foreach (var invalid in new[]
        {
            Plan(["test_probe", "missing_workflow"]),
            Plan(["test_probe", "test_probe"]),
            Plan(["test_probe"], new() { ["test_probe"] = new() { ["unknown"] = "1" } }),
            Plan(["test_probe"], new() { ["missing_workflow"] = new() }),
            Plan(["test_probe"]).Replace("\"workflow_inputs\":[]", "\"workflow_inputs\":[{\"workflow_name\":\"test_probe\",\"arguments\":[{\"name\":\"value\",\"value\":\"1\"},{\"name\":\"value\",\"value\":\"2\"}]}]")
        })
        {
            var probe = new Probe();
            var (chat, _) = Chat([probe], invalid);
            await chat.SendAsync(Request("verileri getir"));
            Assert(probe.Calls == 0, "Invalid queues or parameters cannot execute a valid subset.");
        }

        var correctedProbe = new Probe();
        var (correctedChat, _) = Chat([correctedProbe], Plan(["test_probe"], corrected: "önceki bütün satışları getir", confidence: 0.99));
        await correctedChat.SendAsync(Request("ne arattın?"));
        Assert(correctedProbe.Last!.EffectiveRequest == "ne arattın?", "Spelling channel cannot expand a question into another intent.");
        var numericProbe = new Probe();
        var (numericChat, _) = Chat([numericProbe], Plan(["test_probe"], corrected: "son 60 gün", confidence: 0.99));
        await numericChat.SendAsync(Request("son 6 gün"));
        Assert(numericProbe.Last!.EffectiveRequest == "son 6 gün", "Spelling correction cannot change numeric scope.");
        var typoProbe = new Probe();
        var (typoChat, _) = Chat([typoProbe], Plan(["test_probe"], corrected: "verileri getir", confidence: 0.99));
        await typoChat.SendAsync(Request("verileri geitr"));
        Assert(typoProbe.Last!.EffectiveRequest == "verileri getir", "Bounded spelling correction still works.");

        var selection = new Probe(singleSelection: true);
        var (selectionChat, transport) = Chat([selection], Plan(["test_probe"]));
        var awaiting = await selectionChat.SendAsync(Request("bir seçenek seç"));
        var id = awaiting.PendingSelection!.Id;
        await selectionChat.ContinueSelectionAsync(id, "wrong-user", ["a"]);
        Assert((await selectionChat.ContinueSelectionAsync(id, "owner", ["a", "b"])).PendingSelection is not null,
            "Single-select rejects two options without consuming the owned queue.");
        Assert((await selectionChat.ContinueSelectionAsync(id, "owner", ["unknown"])).PendingSelection is not null,
            "Invalid selection remains recoverable.");
        var selected = await selectionChat.ContinueSelectionAsync(id, "owner", ["b"]);
        Assert(selected.Message == "Tamam." && selection.Calls == 2 && transport.Requests.Count == 1,
            "Valid selection continues the saved queue without another model call.");
        await selectionChat.ContinueSelectionAsync(id, "owner", ["b"]);
        Assert(selection.Calls == 2, "Repeated clicks cannot execute the queue twice.");

        var approval = new Probe();
        var (approvalChat, _) = Chat([approval], Plan(["test_probe"], explosive: true));
        var pending = await approvalChat.SendAsync(Request("güvenli test işlemini onayla"));
        await approvalChat.ApproveAsync(pending.PendingApproval!.Id, "wrong-user");
        await approvalChat.ApproveAsync(pending.PendingApproval.Id, "owner");
        Assert(approval.Calls == 1, "A foreign approval submission cannot consume another user's pending action.");

        var safeFailure = new Probe(failure: new AiWorkflowInputException("Başlangıç tarihi bitiş tarihinden sonra olamaz."));
        var (safeChat, _) = Chat([safeFailure], Plan(["test_probe"]));
        var failed = await safeChat.SendAsync(Request("verileri getir"));
        Assert(failed.Message == "Başlangıç tarihi bitiş tarihinden sonra olamaz.", "User-correctable input errors remain useful Turkish messages.");
        var (unsupportedChat, _) = Chat([new Probe()], Plan([], answer: "Bu işlemi yapamıyorum."));
        Assert((await unsupportedChat.SendAsync(Request("resim çiz"))).PendingApproval is null,
            "Honest unsupported responses do not trigger a pointless paid escalation from keywords.");
        var (explanationChat, _) = Chat([new SalesRevenueSummaryWorkflow(), new MetricCardsWorkflow()],
            Plan([], answer: "Net hasılat, brüt satıştan indirim ve iadeler çıkarıldıktan sonra kalan tutardır."));
        var explanation = await explanationChat.SendAsync(Request("Net hasılatla brüt hasılatın farkını kısaca anlat."));
        Assert(explanation.WorkflowQueue?.Count is null or 0 && explanation.Message.StartsWith("Net hasılat,"),
            "A correct conceptual answer must never become a database query through keyword repair.");

        var rows = JsonSerializer.SerializeToElement(new
        {
            rows = new[]
            {
                new { sale_type = "retail", discount_amount = "10.50", medicine_name = "A", patient_record_id = "7", patient_name = "TEST BİR" },
                new { sale_type = "prescription", discount_amount = "4.25", medicine_name = "B", patient_record_id = "8", patient_name = "TEST İKİ" }
            }
        });
        var sourceSales = JsonSerializer.SerializeToElement(new
        {
            rows = new[] { new { reference_code = "P1", discount_amount = "1" }, new { reference_code = "P2", discount_amount = "2" } },
            metadata = new { columns = new[] { "reference_code", "discount_amount" } }
        });
        var contentsTools = new ContentsTools();
        var sourceProbe = new Probe(output: sourceSales);
        var (lineageChat, lineageTransport) = ChatWithTools([sourceProbe, new PreviousResultWorkflow(), new PreviousSalesContentsWorkflow()], contentsTools,
            Plan(["test_probe"]),
            Plan([PreviousResultWorkflow.WorkflowName], new() { [PreviousResultWorkflow.WorkflowName] = new()
                { ["metrics_json"] = "[{\"field\":\"discount_amount\",\"op\":\"sum\",\"alias\":\"discount_amount\",\"label\":\"Total Discount\"}]" } }),
            Plan(["onceki_satislarin_iceriklerini_gor"]), Plan(["test_probe"]), Plan(["onceki_satislarin_iceriklerini_gor"]));
        await lineageChat.SendAsync(Request("Show two sales."));
        await lineageChat.SendAsync(Request("What is their total discount?"));
        await lineageChat.SendAsync(Request("Show the medicines in the previous sales list."));
        Assert(contentsTools.References.SequenceEqual(["P1", "P2"]) && lineageTransport.Requests.Count == 3,
            "A derived total retains exact source sales, and English contents routing injects them without a language cue.");
        sourceProbe.Output = JsonSerializer.SerializeToElement(new { rows = Array.Empty<object>(), metadata = new { columns = new[] { "reference_code" } } });
        await lineageChat.SendAsync(Request("Run a different empty search."));
        await lineageChat.SendAsync(Request("Show the medicines in those sales."));
        Assert(contentsTools.References.Count == 2, "A fresh empty query clears old lineage and cannot resurrect previous sales.");

        var filteredContents = new ContentsTools();
        var (subsetChat, _) = ChatWithTools([new Probe(output: sourceSales), new PreviousResultWorkflow(), new PreviousSalesContentsWorkflow()], filteredContents,
            Plan(["test_probe"]),
            Plan([PreviousResultWorkflow.WorkflowName], new() { [PreviousResultWorkflow.WorkflowName] = new()
                { ["filters_json"] = "[{\"field\":\"reference_code\",\"op\":\"eq\",\"value\":\"P2\"}]" } }),
            Plan(["onceki_satislarin_iceriklerini_gor"]));
        await subsetChat.SendAsync(Request("Show sales."));
        await subsetChat.SendAsync(Request("Keep only sale P2."));
        await subsetChat.SendAsync(Request("Show their contents."));
        Assert(filteredContents.References.SequenceEqual(["P2"]), "Contents use the filtered subset, never its union with the older source list.");

        var metricInputs = new Dictionary<string, Dictionary<string, string>>
        {
            [PreviousResultWorkflow.WorkflowName] = new()
            {
                ["filters_json"] = "[{\"field\":\"sale_type\",\"op\":\"eq\",\"value\":\"retail\"}]",
                ["metrics_json"] = "[{\"field\":\"discount_amount\",\"op\":\"sum\",\"alias\":\"discount_amount\",\"label\":\"İskonto\"}]"
            }
        };
        var (historyChat, historyTransport) = Chat([new Probe(output: rows), new PreviousResultWorkflow(), new DisplayTableWorkflow()],
            Plan(["test_probe"]), Plan([PreviousResultWorkflow.WorkflowName, "tabloda_goster"], metricInputs));
        await historyChat.SendAsync(Request("örnek sonuçları getir"));
        var total = await historyChat.SendAsync(Request("bunlarda toplam ne kadar iskonto oldu?"));
        Assert(total.Message.Contains("14,75") && historyTransport.Requests.Count == 1,
            "Simple whole-result arithmetic stays exact and free of model calls.");
        var filtered = await historyChat.SendAsync(Request("bunlardan sadece perakende satışların toplam iskontosu ne?"));
        Assert(historyTransport.Requests.Count == 2 && (filtered.Table?.Rows.SingleOrDefault()?.GetValueOrDefault("discount_amount") == "10.50" ||
                filtered.Content?.Items.Any(item => item.Value?.Contains("10,50", StringComparison.Ordinal) == true) == true),
            "A filtered metric does not accidentally sum every saved row. Result: " + JsonSerializer.Serialize(filtered));

        var sortInputs = new Dictionary<string, Dictionary<string, string>>
        {
            [PreviousResultWorkflow.WorkflowName] = new() { ["sort_by"] = "discount_amount", ["sort_direction"] = "desc" }
        };
        var (implicitChat, implicitTransport) = Chat([new Probe(output: rows), new PreviousResultWorkflow(), new DisplayTableWorkflow()],
            Plan(["test_probe"]), Plan([PreviousResultWorkflow.WorkflowName, "tabloda_goster"], sortInputs));
        await implicitChat.SendAsync(Request("örnek veriler"));
        var sorted = await implicitChat.SendAsync(Request("azalan sırayla sırala"));
        Assert(sorted.Table?.Rows.Count == 2 && implicitTransport.Requests.Count == 2,
            "The explicit previous-result workflow receives saved data without relying on a Turkish keyword cue.");

        var newerRows = JsonSerializer.SerializeToElement(new { rows = new[] { new { medicine_name = "C" } } });
        var latestProbe = new Probe(output: rows);
        var (latestChat, latestTransport) = Chat([latestProbe], Plan(["test_probe"]), Plan(["test_probe"]), Plan([], answer: "Son sonuçta iskonto alanı yok."));
        await latestChat.SendAsync(Request("ilk sonuç"));
        latestProbe.Output = newerRows;
        await latestChat.SendAsync(Request("ikinci sonuç"));
        var latest = await latestChat.SendAsync(Request("bunların toplam iskontosu ne?"));
        Assert(latestTransport.Requests.Count == 3 && latest.Message.Contains("alanı yok"),
            "Missing columns in the latest result never reuse older totals.");
        var contactTools = new ContactTools();
        var resolvedPatient = JsonSerializer.SerializeToElement(new
        {
            resolved_entity = "patient", patient_record_id = 7,
            selected_patient = new { patient_record_id = 7, name = "TEST BİR", patient_tc = "11111111111" }
        });
        var contact = await new PatientContactInformationWorkflow().ExecuteAsync(new AiWorkflowContext(
            "bu hastanın telefonu", "bu hastanın telefonu", new AiApplicationContext(),
            new Dictionary<string, JsonElement> { [PreviousResultWorkflow.WorkflowName] = resolvedPatient }, [], ToolExecutor: contactTools));
        Assert(contact.Status == AiWorkflowStepStatus.Completed && contact.Message.Contains("5550000000") && contactTools.Calls == 1,
            "A resolved result-row patient can retrieve missing contact fields rather than falsely reporting no phone.");
        contactTools.ReturnedId = 8;
        var wrongPatient = await new PatientContactInformationWorkflow().ExecuteAsync(new AiWorkflowContext(
            "bu hastanın telefonu", "bu hastanın telefonu", new AiApplicationContext(),
            new Dictionary<string, JsonElement> { [PreviousResultWorkflow.WorkflowName] = resolvedPatient }, [], ToolExecutor: contactTools));
        Assert(wrongPatient.Status == AiWorkflowStepStatus.Failed,
            "Contact enrichment never substitutes a same-name patient with a different ID.");
        var freshPatient = JsonSerializer.SerializeToElement(new
        {
            patient_record_id = 9,
            selected_patient = new { patient_record_id = 9, name = "TEST YENİ", patient_tc = "12289124650", phone_number = "5559999999" }
        });
        var currentPatientContext = new AiWorkflowContext("aynısını yeni hasta için", "aynısını yeni hasta için", new AiApplicationContext(),
            new Dictionary<string, JsonElement>
            {
                ["hasta_eslestir"] = freshPatient,
                [PreviousResultWorkflow.WorkflowName] = resolvedPatient
            }, [new("hasta_eslestir", "Hasta", AiWorkflowStepStatus.Completed, 0, "Hasta seçildi.", freshPatient)], ToolExecutor: contactTools);
        var freshContact = await new PatientContactInformationWorkflow().ExecuteAsync(currentPatientContext);
        Assert(freshContact.Status == AiWorkflowStepStatus.Completed && freshContact.Message.Contains("5559999999") &&
               !freshContact.Message.Contains("5550000000") && contactTools.Calls == 2,
            "A fresh patient matcher supersedes an older injected result-row patient without leaking the old contact.");
        var freshResolved = JsonSerializer.SerializeToElement(new
        {
            resolved_entity = "patient", patient_record_id = 10,
            selected_patient = new { patient_record_id = 10, name = "TEST SON", patient_tc = "22222222222", phone_number = "5551010101" }
        });
        var resolvedContext = currentPatientContext with
        {
            Trace = [.. currentPatientContext.Trace,
                new(PreviousResultWorkflow.WorkflowName, "Önceki sonuç", AiWorkflowStepStatus.Completed, 0, "Hasta seçildi.", freshResolved)]
        };
        var resolvedContact = await new PatientContactInformationWorkflow().ExecuteAsync(resolvedContext);
        Assert(resolvedContact.Message.Contains("5551010101"),
            "The later current-turn grounded result resolver can also supersede an earlier matcher.");
        foreach (var (context, expectedId, expectedName, expectedTc) in new[]
        {
            (currentPatientContext, 9, "TEST YENİ", "12289124650"),
            (resolvedContext, 10, "TEST SON", "22222222222")
        })
        {
            var identityTools = new PatientIdentityTools();
            var current = context with { ToolExecutor = identityTools };
            var sale = await new PatientLatestProductsWorkflow().ExecuteAsync(current);
            await new MedulaPatientPrescriptionWorkflow().ExecuteAsync(current);
            Assert(identityTools.PatientId == expectedId && sale.Message.Contains(expectedName) &&
                   identityTools.PatientTc == expectedTc && identityTools.PatientName == expectedName,
                "Local sale IDs, patient labels and Medula T.C. all follow the same latest current-turn resolver, never dictionary insertion order.");
        }
        Console.WriteLine("PASS router-planning-regressions");
    }

    private static AiRequestContext Request(string text) => new("planning-test", text,
        new AiApplicationContext(CurrentModule: "home", CurrentUserId: "owner"), new HashSet<string>());

    private static string Plan(string[] queue, Dictionary<string, Dictionary<string, string>>? inputs = null,
        string? corrected = null, double confidence = 0, bool explosive = false, string? answer = null) => JsonSerializer.Serialize(new
    {
        answer, explosive, explosion_reason = explosive ? "Test approval." : null,
        corrected_request = corrected ?? string.Empty, correction_confidence = confidence,
        medicine_names = Array.Empty<string>(), use_remembered_medicine = false,
        needs_advanced_planning = false, advanced_planning_reason = (string?)null,
        workflow_queue = queue,
        workflow_inputs = (inputs ?? []).Select(pair => new
        {
            workflow_name = pair.Key, arguments = pair.Value.Select(value => new { name = value.Key, value = value.Value }).ToArray()
        }).ToArray()
    });

    private static (WorkflowRouterChatService Chat, ScriptedTransport Transport) Chat(IAiWorkflow[] workflows, params string[] plans)
        => ChatWithTools(workflows, new TestWorkflowToolExecutor(), plans);

    private static (WorkflowRouterChatService Chat, ScriptedTransport Transport) ChatWithTools(IAiWorkflow[] workflows, IAiWorkflowToolExecutor executor, params string[] plans)
    {
        var settings = new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true });
        var transport = new ScriptedTransport(true, plans.Select(ScriptedTransport.Final).ToArray());
        var directory = Path.Combine(Path.GetTempPath(), "pharmacy-router-regression", Guid.NewGuid().ToString("N"));
        return (new WorkflowRouterChatService(new AiWorkflowRegistry(workflows), transport, settings,
            new AiConversationManager(settings), new AiUsageTelemetry(Path.Combine(directory, "usage.json")),
            new TestPrompts(), executor, new AiRequestCaptureStore(Path.Combine(directory, "requests"))), transport);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Router planning regression: " + message);
    }

    private sealed class ContactTools : IAiWorkflowToolExecutor
    {
        public int Calls { get; private set; }
        public int ReturnedId { get; set; } = 7;
        public bool HasTool(string name) => name == "sale.search_patients";
        public Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string name, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AiWorkflowToolExecution(name, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { patients = new[] { new { patient_record_id = ReturnedId, name = "TEST BİR", phone_number = "5550000000" } } }), null, 0));
        }
    }

    private sealed class ContentsTools : IAiWorkflowToolExecutor
    {
        public List<string> References { get; } = [];
        public bool HasTool(string name) => name == "sale.get_record";
        public Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string name, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            Assert(name == "sale.get_record", "Contents reuse must not search the database again.");
            var reference = arguments.GetProperty("reference_code").GetString()!;
            References.Add(reference);
            return Task.FromResult(new AiWorkflowToolExecution(name, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { found = true, sale = new { reference_code = reference, lines = new[]
                    { new { barcode = "synthetic", product_name = "Test Product", gross_price = 10m, discount_amount = 1m, net_price = 9m } } } }), null, 0));
        }
    }

    private sealed class PatientIdentityTools : IAiWorkflowToolExecutor
    {
        public int? PatientId { get; private set; }
        public string? PatientTc { get; private set; }
        public string? PatientName { get; private set; }
        public bool HasTool(string name) => name is "sale.search_records" or "medula.patient_prescriptions_search";
        public Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string name, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            if (name == "sale.search_records") PatientId = arguments.GetProperty("patient_record_id").GetInt32();
            else
            {
                PatientTc = arguments.GetProperty("patient_tc").GetString();
                PatientName = arguments.GetProperty("patient_name").GetString();
            }
            return Task.FromResult(new AiWorkflowToolExecution(name, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { success = true, sales = Array.Empty<object>(), items = Array.Empty<object>() }), null, 0));
        }
    }

    private sealed class Probe(JsonElement? output = null, bool singleSelection = false, Exception? failure = null) : IAiWorkflow
    {
        public AiWorkflowDefinition Definition { get; } = new("test_probe", "Test", "Test data producer.",
            Parameters: new Dictionary<string, string> { ["value"] = "Test value." });
        public JsonElement Output { get; set; } = output ?? JsonSerializer.SerializeToElement(new { ok = true });
        public int Calls { get; private set; }
        public AiWorkflowContext? Last { get; private set; }
        public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = context;
            if (failure is not null) throw failure;
            if (singleSelection && context.SelectedOptionIds is not { Count: > 0 })
                return Task.FromResult(new AiWorkflowStepResult(AiWorkflowStepStatus.AwaitingSelection, "Bir seçenek seçin.", Output,
                    [new("a", "Bir", Output), new("b", "İki", Output)], AllowsMultipleSelection: false));
            return Task.FromResult(new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, "Tamam.", Output));
        }
    }
}
