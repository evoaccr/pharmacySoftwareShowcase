using System.Globalization;
using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Workflows;

internal static class PrescriptionFastPathRegressionTests
{
    public static async Task RunAsync()
    {
        foreach (var text in new[]
        {
            "geçen ay reçeteleri göster", "son 10 reçeteleri göster", "reçeteleri sil",
            "reçeteleri PDF olarak dışa aktar", "2026-08-01 ile 2026-08-31 reçeteleri göster",
            "bugün reçeteleri saat 14:00 sonrasına göre getir", "sonlandırılmamış reçeteleri tutara göre sırala"
        })
        {
            var search = new Probe(new MedulaPrescriptionSearchWorkflow().Definition);
            var (chat, transport) = Chat([search, new DisplayTableWorkflow()], EmptyPlan);
            await chat.SendAsync(Request(text));
            Require(transport.Requests.Count == 1 && search.Calls == 0,
                $"A partial prescription keyword shortcut must defer the complete request to the router: {text}");
        }

        var prescriptionSearch = new Probe(new MedulaPrescriptionSearchWorkflow().Definition);
        var (prescriptionChat, prescriptionTransport) = Chat([prescriptionSearch, new DisplayTableWorkflow()], EmptyPlan, EmptyPlan, EmptyPlan);
        await prescriptionChat.SendAsync(Request("sonalndırılmamış reçeteleri getir"));
        Require(prescriptionSearch.Calls == 1 && prescriptionTransport.Requests.Count == 0 &&
                prescriptionSearch.Last!.Parameters!["invoice_type_codes"] == "1,4,28,7" &&
                prescriptionSearch.Last.Parameters["qr_status"] == "unfinalized",
            "The fully understood default A/B/C unfinished-prescription request keeps its zero-model-call path.");
        await prescriptionChat.SendAsync(Request("A b ve c için son 3 gün kontrol et"));
        Require(prescriptionSearch.Calls == 2 && prescriptionTransport.Requests.Count == 0 &&
                prescriptionSearch.Last!.Parameters!["start_date"] == DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "Pure invoice-group/date refinements retain all saved constraints without another model call.");
        foreach (var otherIntent in new[] { "bu ay satış hasılatı", "bugün stokta kaç ürün var", "son 3 gün tamamlanmış olanları getir" })
            await prescriptionChat.SendAsync(Request(otherIntent));
        Require(prescriptionSearch.Calls == 2 && prescriptionTransport.Requests.Count == 3,
            "Recognized date words never hijack a different intent or silently retain an explicitly changed status.");

        var invoicePatient = new Probe(new PatientMatchWorkflow().Definition);
        var invoiceSearch = new Probe(new MedulaPrescriptionSearchWorkflow().Definition);
        var (invoiceChat, invoiceTransport) = Chat(
            [invoicePatient, invoiceSearch, new MedulaPatientPrescriptionWorkflow(), new DisplayTableWorkflow()], EmptyPlan);
        await invoiceChat.SendAsync(Request("A grubu reçeteleri getir"));
        Require(invoicePatient.Calls == 0 && invoiceSearch.Calls == 1 && invoiceTransport.Requests.Count == 0 &&
                invoiceSearch.Last!.Parameters!["invoice_type_codes"] == "1",
            "A common invoice group is a prescription filter, never a patient named A GRUBU.");

        var patient = new Probe(new PatientMatchWorkflow().Definition, context =>
            JsonSerializer.SerializeToElement(new
            {
                patient_record_id = 51,
                selected_patient = new { record_id = 51, name = context.Parameters!["query"], tc_no = "12289124650" }
            }));
        var patientPrescriptions = new Probe(new MedulaPatientPrescriptionWorkflow().Definition);
        var (patientChat, patientTransport) = Chat(
            [patient, patientPrescriptions, new PreviousPatientContextWorkflow(), new DisplayTableWorkflow()], EmptyPlan);
        await patientChat.SendAsync(Request("Evren Acar'ın tüm reçetelerini getir"));
        var secondPatient = await patientChat.SendAsync(Request("Ayşe Yılmaz'ın yerel reçetelerini getir"));
        Require(secondPatient.PendingApproval is null && patient.Calls == 2 &&
                patient.Last!.Parameters!["query"] == "AYŞE YILMAZ" && patientTransport.Requests.Count == 0,
            "A new named patient cannot be replaced by the remembered patient's local-prescription shortcut.");
        var remembered = await patientChat.SendAsync(Request("Veritabanındaki reçeteleri?"));
        Require(remembered.PendingApproval is not null && patientTransport.Requests.Count == 0,
            "An actual patient-less local-prescription follow-up still asks to confirm remembered patient context.");
        await patientChat.SendAsync(Request("yerel reçeteleri geçen ay için getir"));
        Require(patientTransport.Requests.Count == 1,
            "Unparsed date filters in a remembered-patient request are not silently dropped.");

        var sales = new Probe(new SeeSalesWorkflow().Definition);
        var calendarPlan = JsonSerializer.Serialize(new
        {
            answer = (string?)null, explosive = false, explosion_reason = (string?)null,
            corrected_request = "", correction_confidence = 0, medicine_names = Array.Empty<string>(), use_remembered_medicine = false,
            workflow_queue = new[] { "satislari_gor" },
            workflow_inputs = new[]
            {
                new { workflow_name = "satislari_gor", arguments = new[]
                {
                    new { name = "calendar_period", value = "last_month" }, new { name = "sale_types", value = "retail" }
                } }
            }
        });
        var (salesChat, salesTransport) = Chat([sales, new DisplayTableWorkflow()], calendarPlan);
        await salesChat.SendAsync(Request("örnek satış sorgusunu yap"));
        await salesChat.SendAsync(Request("60 gün"));
        Require(sales.Calls == 2 && salesTransport.Requests.Count == 1 &&
                sales.Last!.Parameters!["period_days"] == "60" && !sales.Last.Parameters.ContainsKey("calendar_period") &&
                sales.Last.Parameters["sale_types"] == "retail",
            "Changing a calendar-month result to 60 days removes the obsolete calendar selector and retains non-date filters.");

        var earlierRows = new Probe(new("test_earlier_rows", "Earlier rows", "Synthetic earlier data."), _ =>
            JsonSerializer.SerializeToElement(new { rows = new[] { new { medicine_name = "XANAX", patient_name = "TEST HASTA" } } }));
        var latestRows = new Probe(new("test_latest_rows", "Latest rows", "Synthetic latest data."), _ =>
            JsonSerializer.SerializeToElement(new { rows = new[] { new { medicine_name = "PAROL" } } }));
        var lookupPatient = new Probe(new PatientMatchWorkflow().Definition);
        var clinical = new Probe(new MedulaPatientClinicalWorkflow().Definition);
        var dataPlan = """
            {"answer":null,"explosive":false,"explosion_reason":null,"corrected_request":"","correction_confidence":0,
             "medicine_names":[],"use_remembered_medicine":false,"workflow_queue":["test_earlier_rows","test_latest_rows"],"workflow_inputs":[]}
            """;
        var (historyChat, historyTransport) = Chat([earlierRows, latestRows, lookupPatient, clinical, new DisplayTableWorkflow()], dataPlan, EmptyPlan);
        await historyChat.SendAsync(Request("iki örnek veri kaynağını getir"));
        await historyChat.SendAsync(Request("bu listedeki xanax alan hastanın raporlarını göster"));
        Require(historyTransport.Requests.Count == 2 && lookupPatient.Calls == 0 && clinical.Calls == 0,
            "Patient lookup from the latest table cannot fall back to a matching patient in an older dataset.");
        Console.WriteLine("PASS prescription-fastpath-regressions (whole intent, patient ownership, calendar replay)");
    }

    private const string EmptyPlan = """
        {"answer":"Bu istek için filtreleri netleştirelim.","explosive":false,"explosion_reason":null,
         "corrected_request":"","correction_confidence":0,"medicine_names":[],"use_remembered_medicine":false,
         "workflow_queue":[],"workflow_inputs":[]}
        """;

    private static AiRequestContext Request(string text) => new("fastpath-test", text,
        new AiApplicationContext(CurrentModule: "home", CurrentUserId: "owner"), new HashSet<string>());

    private static (WorkflowRouterChatService Chat, ScriptedTransport Transport) Chat(IAiWorkflow[] workflows, params string[] plans)
    {
        var settings = new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true });
        var transport = new ScriptedTransport(true, plans.Select(ScriptedTransport.Final).ToArray());
        var directory = Path.Combine(Path.GetTempPath(), "pharmacy-fastpath-regression", Guid.NewGuid().ToString("N"));
        return (new WorkflowRouterChatService(new AiWorkflowRegistry(workflows), transport, settings,
            new AiConversationManager(settings), new AiUsageTelemetry(Path.Combine(directory, "usage.json")),
            new TestPrompts(), new TestWorkflowToolExecutor(), new AiRequestCaptureStore(Path.Combine(directory, "requests"))), transport);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Probe(AiWorkflowDefinition definition, Func<AiWorkflowContext, JsonElement>? output = null) : IAiWorkflow
    {
        public AiWorkflowDefinition Definition { get; } = definition with { RequiredTools = [] };
        public int Calls { get; private set; }
        public AiWorkflowContext? Last { get; private set; }
        public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = context;
            return Task.FromResult(new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, "Test sonucu hazır.",
                output?.Invoke(context) ?? JsonSerializer.SerializeToElement(new { rows = new[] { new { value = "test" } } })));
        }
    }
}
