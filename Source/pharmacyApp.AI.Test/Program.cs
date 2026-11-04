using System.Net;
using System.Net.Http;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using pharmacyApp.AI.Tools;
using pharmacyApp.AI.Tools.Medula;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Routing;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Search;
using pharmacyApp.AI.Tools.Product;
using pharmacyApp.AI.Tools.QR;
using pharmacyApp.AI.Tools.Sale;
using pharmacyApp.AI.Workflows;
using pharmacyApp.Modules.Home;
using pharmacyApp.Modules.Medula;
using pharmacyApp.Modules.Sales;
using System.Diagnostics;
using System.Globalization;

if (args.Contains("--purchase-intent-eval", StringComparer.Ordinal))
{
    var purchaseConfig = new FixedAiConfigurationProvider(pharmacyApp.Infrastructure.AppConfiguration.ReadSection<AiConfiguration>("Ai") with
        { DebugMode = true, StoreResponses = false, RouterModel = "gpt-6-luna", RouterReasoningEffort = "none" });
    await PurchaseIntentRegressionTests.LiveAsync(new OpenAiResponsesTransport(purchaseConfig));
    return;
}
if (args.Contains("--inventory-probe", StringComparer.Ordinal))
{
    await PurchaseIntentRegressionTests.ProbeAsync();
    return;
}
if (args.Contains("--router-eval", StringComparer.Ordinal))
{
    Environment.ExitCode = await RouterReliabilityEvaluation.RunAsync(args);
    return;
}

if (args.Contains("--patient-shortage-eval", StringComparer.Ordinal))
{
    var patientConfig = new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true, RouterReasoningEffort = "none", StoreResponses = false });
    await PatientShortageRegressionTests.RunLiveAsync(patientConfig, new pharmacyApp.AI.Providers.OpenAiResponsesTransport(patientConfig, getSecrets: () => new pharmacyApp.Infrastructure.AiSecrets { OpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty }));
    return;
}
await PurchaseIntentRegressionTests.RunAsync();
if (args.Contains("--purchase-intent-tests", StringComparer.Ordinal)) return;
await PatientShortageRegressionTests.RunAsync();
if (args.Contains("--patient-shortage-tests", StringComparer.Ordinal)) return;
await PreviousResultWorkflowTests.RunAsync();
await WorkflowDataReliabilityTests.RunAsync();
await PatientNameResolutionTests.RunAsync();
await RouterPlanningRegressionTests.RunAsync();
await WorkflowPresentationRegressionTests.RunAsync();

if (args.Contains("--router-regressions", StringComparer.Ordinal))
{
    var regressionDirectory = Path.Combine(Path.GetTempPath(), "pharmacy-portfolio-ai-tests", Guid.NewGuid().ToString("N"));
    await BranchValidationRegressionTests.RunAsync(regressionDirectory);
    await ModelConfigurationRegressionTests.RunAsync(regressionDirectory, Assert);
    Console.WriteLine("PASS portfolio router regression suites (no live services)");
    return;
}

var catalog = MedulaAiTools.CreateCatalog();
var expectedNames = new[]
{
    "medula.aggregate_prescription_reports",
    "medula.diagnosis_search",
    "medula.doctor_search",
    "medula.drug_equivalents_search",
    "medula.drug_get",
    "medula.drug_search",
    "medula.e_prescription_get",
    "medula.e_prescriptions_search",
    "medula.patient_eligibility_get",
    "medula.patient_examinations_search",
    "medula.patient_medication_history_search",
    "medula.patient_medication_list_get",
    "medula.patient_off_label_approvals_search",
    "medula.patient_prescriptions_search",
    "medula.patient_report_get",
    "medula.patient_reports_search",
    "medula.patient_treatment_scheme_get",
    "medula.prescription_filter_options",
    "medula.prescription_get",
    "medula.prescription_history",
    "medula.prescription_warnings_get",
    "medula.prescriptions_search",
    "medula.session_login",
    "medula.session_restart",
    "medula.session_status"
};

Assert(catalog.Definitions.Select(definition => definition.Name).SequenceEqual(expectedNames),
    "Medula catalog contains the expected unique business tools in stable order.");
Assert(catalog.Definitions.All(definition => definition.Name.StartsWith("medula.", StringComparison.Ordinal)),
    "Every registered tool belongs to the Medula namespace.");
Assert(catalog.Definitions.Count(definition => definition.Risk == AiToolRisks.LowRiskWrite) == 2,
    "Only configured login and browser recovery are low-risk session writes.");
Assert(catalog.Definitions.All(definition => definition.Risk is
        AiToolRisks.ReadOnly or AiToolRisks.LowRiskWrite or AiToolRisks.ConfirmationRequired),
    "Every Medula tool is read-only, a low-risk session operation, or application-confirmed.");
var clinicalToolNames = expectedNames.Where(name => name is
    "medula.patient_medication_history_search" or "medula.patient_medication_list_get" or "medula.patient_eligibility_get" or
    "medula.patient_reports_search" or "medula.patient_report_get" or
    "medula.drug_search" or "medula.drug_get" or "medula.drug_equivalents_search" or
    "medula.doctor_search" or "medula.diagnosis_search" or
    "medula.prescription_warnings_get" or "medula.patient_examinations_search" or
    "medula.patient_off_label_approvals_search" or "medula.patient_treatment_scheme_get").ToArray();
Assert(clinicalToolNames.Length == 14
       && clinicalToolNames.All(name =>
           catalog.Definitions.Single(definition => definition.Name == name).Risk
               == AiToolRisks.ConfirmationRequired),
    "All 14 remote clinical Medula reads require application-owned user confirmation.");
var patientMedicationListDefinition = catalog.Definitions.Single(definition =>
    definition.Name == "medula.patient_medication_list_get");
Assert(patientMedicationListDefinition.Description.Contains(
           "Reçete Listesi Günlük", StringComparison.Ordinal)
       && patientMedicationListDefinition.Description.Contains(
           "Reçete Sahibi T.C.", StringComparison.Ordinal)
       && !patientMedicationListDefinition.Description.Contains(
           "latest reachable prescription", StringComparison.OrdinalIgnoreCase),
    "The patient medication-list contract uses a daily-list host prescription rather than requiring one of the patient's prescriptions.");
var patientReportsDefinition = catalog.Definitions.Single(definition =>
    definition.Name == "medula.patient_reports_search");
Assert(patientReportsDefinition.Description.Contains(
           "Reçete Listesi Günlük", StringComparison.Ordinal)
       && patientReportsDefinition.Description.Contains(
           "Reçete Sahibi T.C.", StringComparison.Ordinal)
       && patientReportsDefinition.Description.Contains(
           "Rapor", StringComparison.Ordinal)
       && patientReportsDefinition.Description.Contains(
           "never creates, edits, or saves", StringComparison.OrdinalIgnoreCase),
    "The patient-report contract uses a daily-list host prescription, changes only patient identity, and forbids prescription mutation.");
var prescriptionSearchDefinition = catalog.Definitions.Single(definition =>
    definition.Name == "medula.prescriptions_search");
Assert(prescriptionSearchDefinition.Parameters.TryGetValue("invoice_type_codes", out var invoiceTypeCodesParameter)
       && invoiceTypeCodesParameter.Type == "array"
       && invoiceTypeCodesParameter.ItemsType == "string",
    "Prescription search exposes a validated multi-group invoice-type parameter for normal A/B/C searches.");

var forbiddenParameters = new HashSet<string>(
    ["url", "javascript", "sql", "control_id", "action_id", "table_id", "username", "password"],
    StringComparer.Ordinal);
Assert(catalog.Definitions.SelectMany(definition => definition.Parameters.Keys)
        .All(parameter => !forbiddenParameters.Contains(parameter)),
    "Definitions do not expose credentials, URLs, SQL, JavaScript, or UI-control identifiers.");

var ePrescriptionSearch = catalog.Definitions.Single(definition =>
    definition.Name == "medula.e_prescriptions_search");
var ePrescriptionGet = catalog.Definitions.Single(definition =>
    definition.Name == "medula.e_prescription_get");
Assert(!ePrescriptionSearch.Parameters.ContainsKey("selected_electronic_prescription_number")
       && ePrescriptionSearch.Parameters.ContainsKey("limit"),
    "E-prescription search advertises only search and pagination parameters.");
Assert(ePrescriptionGet.Parameters.ContainsKey("selected_electronic_prescription_number")
       && !ePrescriptionGet.Parameters.ContainsKey("limit"),
    "E-prescription detail advertises selection without irrelevant pagination.");

var definitionJson = catalog.GetDefinitionsJson();
Assert(definitionJson.Contains("\"name\":\"medula.prescription_get\"", StringComparison.Ordinal)
       && definitionJson.Contains("\"force_new_session\"", StringComparison.Ordinal),
    "Definitions have a machine-readable JSON boundary with snake_case fields.");

var sample = new MedulaAiPrescriptionListItem(
    "3PU0QGL", "2Q8TERI", Guid.Empty.ToString(), 1, "26321401616", "Test Patient",
    "01/09/2026", "01/09/2026", "01/09/2026", "4A", "Sonlandırıldı",
    "Elektronik", "A", true, true, false, false, "admin");
var sampleJson = JsonSerializer.Serialize(sample);
Assert(sampleJson.Contains("\"prescription_number\"", StringComparison.Ordinal)
       && !sampleJson.Contains("PrescriptionNumber", StringComparison.Ordinal),
    "Medula tool results serialize with declared snake_case field names.");

var localPrescription = new MedulaPrescriptionListItem(
    "3SHARED", "03/09/2026", "03/09/2026", "EVREN", "ACAR",
    "4A", "Sonlandırıldı", "I", "Evet", "", "", "4", "202609", "database", 0, 0)
{
    PatientTcIdentityNo = "12289124650", IsStoredLocally = true, VersionNumber = 2
};
var offlinePrescription = new MedulaPrescriptionListItem(
    "3LOCAL", "02/01/2026", "02/01/2026", "EVREN", "ACAR",
    "4A", "Sonlandırıldı", "I", "Evet", "", "", "4", "202601", "database", 0, 0)
{
    PatientTcIdentityNo = "12289124650", IsStoredLocally = true
};
var activePrescription = localPrescription with
{
    Source = "medula", PageNumber = 1, RowIndex = 2, IsLiveOnMedula = true, IsStoredLocally = false
};
var liveOnlyPrescription = new MedulaPrescriptionListItem(
    "3LIVE", "05/09/2026", "05/09/2026", "EVREN", "ACAR",
    "4A", "Sonlandırılmadı", "I", "Evet", "", "", "4", "202609", "medula", 1, 1)
{
    PatientTcIdentityNo = "12289124650", IsLiveOnMedula = true
};
var mergeMethod = typeof(MedulaAiOperations).GetMethod(
    "MergePatientPrescriptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
var mergedPrescriptions = (IReadOnlyList<MedulaPrescriptionListItem>)mergeMethod.Invoke(
    null, [new[] { localPrescription, offlinePrescription }, new[] { activePrescription, liveOnlyPrescription }])!;
Assert(mergedPrescriptions.Count == 3
       && mergedPrescriptions.Single(item => item.PrescriptionNumber == "3SHARED") is
           { IsLiveOnMedula: true, IsStoredLocally: true, VersionNumber: 2 }
       && mergedPrescriptions.Single(item => item.PrescriptionNumber == "3LOCAL").IsLiveOnMedula == false
       && mergedPrescriptions.Single(item => item.PrescriptionNumber == "3LIVE").IsLiveOnMedula,
    "Patient prescription merging deduplicates shared records, retains local metadata, and marks only live Medula records active.");

var recoveryMethod = typeof(MedulaAiOperations).GetMethod(
    "NeedsPrescriptionBrowserRecovery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
Assert((bool)recoveryMethod.Invoke(null, [new MedulaPrescriptionListResult(false, "Sistem Hatası", [])])!
       && (bool)recoveryMethod.Invoke(null, [new MedulaPrescriptionListResult(false, "Medula reçete listesi denetimleri hazırlanmadı. Geçerli adres: /ReceteListe.jsp", [])])!
       && !(bool)recoveryMethod.Invoke(null, [new MedulaPrescriptionListResult(false, "Kayıt Bulunamamıştır.", [])])!,
    "Prescription queries retry fatal/system-screen failures once but do not restart Medula for a legitimate empty result.");

var rejectedUnknownParameter = false;
try
{
    using var invalid = JsonDocument.Parse("""{"browser_url":"https://example.invalid"}""");
    catalog.ExecuteAsync("medula.session_status", invalid.RootElement).GetAwaiter().GetResult();
}
catch (AiToolValidationException)
{
    rejectedUnknownParameter = true;
}
Assert(rejectedUnknownParameter, "Backend validation rejects unknown parameters before Medula execution.");

var rejectedConflictingLiveGroups = false;
try
{
    using var invalid = JsonDocument.Parse(
        """{"mode":"live_date_range","start_date":"2026-09-01","end_date":"2026-09-01","invoice_type_code":"4","invoice_type_codes":["1","4"]}""");
    catalog.ExecuteAsync("medula.prescriptions_search", invalid.RootElement).GetAwaiter().GetResult();
}
catch (AiToolValidationException)
{
    rejectedConflictingLiveGroups = true;
}
Assert(rejectedConflictingLiveGroups,
    "Live prescription searches reject conflicting single-group and multi-group inputs before Medula execution.");

var rejectedInvalidPatient = false;
try
{
    using var invalid = JsonDocument.Parse("""{"patient_tc":"123"}""");
    catalog.ExecuteAsync("medula.patient_prescriptions_search", invalid.RootElement).GetAwaiter().GetResult();
}
catch (AiToolValidationException)
{
    rejectedInvalidPatient = true;
}
Assert(rejectedInvalidPatient, "Patient searches reject malformed identity numbers before Medula execution.");

var mutationNames = new[]
{
    "medula.prescription_save",
    "medula.prescription_delete",
    "medula.prescription_finalize_qr",
    "medula.its_sale_submit",
    "medula.document_upload"
};
Assert(mutationNames.All(name => !catalog.TryGet(name, out _)),
    "Unconfirmed Medula mutations are intentionally absent from the callable catalog.");

var discovered = AiToolRegistry.Discover();
Assert(discovered.ToolCount == 50, "Runtime discovery finds all 50 current Product, QR, Sale, and Medula tools.");
Assert(discovered.Namespaces.SequenceEqual(new[] { "medula", "product", "qr", "sale" }),
    "Runtime discovery exposes the four current namespaces without orchestration registration.");
Assert(discovered.GetTool("product.get") is not null && discovered.GetToolsByNamespace("qr").Count == 5,
    "Registry resolves exact names and namespace queries.");
Assert(discovered.GetTool("product.aggregate_catalog")?.Definition.Risk == AiToolRisks.ReadOnly
       && discovered.GetTool("sale.audit_record_consistency")?.Definition.Risk == AiToolRisks.ReadOnly
       && discovered.GetTool("sale.aggregate_payments")?.Definition.Risk == AiToolRisks.ReadOnly
       && discovered.GetTool("sale.analyze_baskets")?.Definition.Risk == AiToolRisks.ReadOnly
       && discovered.GetTool("product.analyze_inventory_sales")?.Definition.Risk == AiToolRisks.ReadOnly
       && discovered.GetTool("medula.aggregate_prescription_reports")?.Definition.Risk == AiToolRisks.ReadOnly,
    "Catalogue, consistency, payment, and basket analysis tools are discovered as bounded read-only capabilities.");Assert(discovered.GetTool("qr.aggregate_movements")?.Definition.Risk == AiToolRisks.ReadOnly
       && discovered.GetTool("qr.audit_movement_chains")?.Definition.Risk == AiToolRisks.ReadOnly,
    "QR aggregate and chain-audit tools are registered as read-only capabilities.");
Assert(discovered.GetTool("sale.aggregate_records") is not null
       && discovered.GetTool("sale.search_records") is not null
       && discovered.GetTool("sale.search_patients") is not null
       && discovered.GetTool("sale.get_record") is not null,
    "The Sales record-query and patient-directory tools are automatically discoverable.");
Assert(discovered.CatalogVersion.Length == 16, "Registry computes a stable catalog version hash.");

Assert(discovered.GetTool("product.search")!.Definition.Parameters["only_on_market"].DefaultValue is false,
    "Product name searches include passive/off-market medicines unless the caller explicitly filters them out.");
var nameSearch = discovered.GetTool("product.search_by_name")!.Definition;
Assert(nameSearch.Parameters.Count == 1 && nameSearch.Parameters["query"].Required,
    "Plain medicine lookup exposes one required phrase and leaves catalog filtering to the backend.");

var productSearchSchema = AiToolSchemaConverter.Convert(discovered.GetTool("product.search")!.Definition);
var productSearchSchemaJson = productSearchSchema.ToJsonString();
var productSearchParameters = productSearchSchema["parameters"]!.AsObject();
var productSearchProperties = productSearchParameters["properties"]!.AsObject();
var productSearchRequired = productSearchParameters["required"]!.AsArray();
var sortBySchema = productSearchProperties["sort_by"]!.AsObject();
Assert(productSearchParameters["additionalProperties"]!.GetValue<bool>() == false
       && productSearchRequired.Count == productSearchProperties.Count,
    "Strict OpenAI tool schemas reject unknown fields and require every declared property.");
Assert(sortBySchema["type"]!.AsArray().Any(value => value!.GetValue<string>() == "null")
       && sortBySchema["enum"]!.AsArray().Any(value => value is null),
    "Optional enum parameters admit null consistently in both their type and allowed values.");
Assert(!productSearchSchemaJson.Contains("\"default\"", StringComparison.Ordinal),
    "Strict OpenAI tool schemas omit application-side default annotations.");
Assert(productSearchSchema["name"]!.GetValue<string>() == "product_search",
    "Provider tool names are converted to the OpenAI-safe character set.");
var providerNames = discovered.Definitions.Select(definition => AiToolNameCodec.ToProviderName(definition.Name))
    .Append(AiToolNameCodec.ToProviderName("debug.report_missing_tool"))
    .ToArray();
Assert(providerNames.Distinct(StringComparer.Ordinal).Count() == providerNames.Length
       && providerNames.All(name => name.Length is > 0 and <= 64 &&
           name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')),
    "Every current business and Debug tool has a unique provider-safe function name.");

var aliasHandler = new FixedResponseHandler(
    HttpStatusCode.OK,
    """{"id":"resp_alias","output":[{"type":"function_call","call_id":"call_alias","name":"product_search","arguments":"{}"}],"usage":{"input_tokens":1,"output_tokens":1}}""");
var aliasTransport = new OpenAiResponsesTransport(
    new FixedAiConfigurationProvider(new AiConfiguration { GatewayEndpoint = "https://gateway.test/v1/responses" }),
    new HttpClient(aliasHandler));
var aliasResponse = await aliasTransport.SendAsync(new AiModelRequest(
    "gpt-5.6-luna", "low", "Test",
    [JsonSerializer.SerializeToElement(new { role = "user", content = "Test" })],
    [discovered.GetTool("product.search")!.Definition], false, false,
    MaxOutputTokens: 777));
Assert(aliasHandler.RequestBodies.Single().Contains("\"name\":\"product_search\"", StringComparison.Ordinal)
       && !aliasHandler.RequestBodies.Single().Contains("\"name\":\"product.search\"", StringComparison.Ordinal)
       && aliasHandler.RequestBodies.Single().Contains("\"max_output_tokens\":777", StringComparison.Ordinal)
       && aliasResponse.ToolCalls.Single().Name == "product.search",
    "Transport sends provider-safe aliases, bounded output tokens, and restores canonical dotted names before backend execution.");

var duplicateRejected = false;
try
{
    var duplicate = TestTool.ReadOnly("test.same");
    _ = new AiToolRegistry([duplicate, TestTool.ReadOnly("test.same")]);
}
catch (InvalidOperationException) { duplicateRejected = true; }
Assert(duplicateRejected, "Registry rejects duplicate tool definitions.");

var invalidArrayRejected = false;
try
{
    _ = new AiToolRegistry([new TestTool(new AiToolDefinition(
        "test.invalid", "Invalid array definition.", AiToolRisks.ReadOnly,
        new Dictionary<string, AiToolParameterDefinition> { ["values"] = new("array", "Values") },
        new Dictionary<string, string> { ["ok"] = "boolean" }, ["test"]), (_, _) => Task.FromResult<object>(new { ok = true }))]);
}
catch (InvalidOperationException) { invalidArrayRejected = true; }
Assert(invalidArrayRejected, "Registry rejects malformed array schemas.");

var noTransport = new ScriptedTransport(false);
var fixedConfiguration = new FixedAiConfigurationProvider(new AiConfiguration { DeterministicRoutingThreshold = 0.55 });
var search = new AiToolSearchService(discovered);
var router = new AiRouter(discovered, search, noTransport, fixedConfiguration, new TestPrompts());
var productRoute = await router.RouteAsync(Request("find product barcode and current price", "products.catalog"));
var qrRoute = await router.RouteAsync(Request("show QR package movement history", "stock-movements.qr"));
var crossRoute = await router.RouteAsync(Request("compare product barcode with QR movement history", null));
var ambiguousRevenueRequest = Request("son 3 aydaki satışların hasılatını söyle", "products.catalog") with
{
    ApplicationContext = new AiApplicationContext(
        CurrentModule: "products.catalog", SelectedBarcode: "8699832750034", CurrentUserId: "user-1")
};
var ambiguousRevenueRoute = await router.RouteAsync(ambiguousRevenueRequest);
var explicitRevenueRoute = await router.RouteAsync(ambiguousRevenueRequest with
{
    UserMessage = "Tüm eczane satışlarının belirtilen dönemdeki hasılatı"
});
Assert(productRoute.Domains.Contains("product") && productRoute.NeedsTools && !productRoute.UsedModelRouter,
    "Deterministic routing handles an obvious Product request without an AI router call.");
Assert(qrRoute.Domains.Contains("qr") && qrRoute.NeedsTools,
    "Deterministic routing handles an obvious QR request.");
Assert(crossRoute.Domains.Contains("product") && crossRoute.Domains.Contains("qr") && crossRoute.Complexity == AiComplexity.Normal,
    "Deterministic routing preserves ambiguous multi-domain requests.");
Assert(ambiguousRevenueRoute.NeedsClarification
       && ambiguousRevenueRoute.PreferredToolNames?.Contains("sale.aggregate_records") == true
       && ambiguousRevenueRoute.ClarificationOptions?.Count == 2,
    "A selected product does not silently scope an otherwise ambiguous pharmacy sales-revenue request.");
Assert(!explicitRevenueRoute.NeedsClarification && explicitRevenueRoute.Domains.Contains("sale"),
    "A clicked sales-scope choice proceeds instead of entering a clarification loop.");

var riskRouterTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"domains":["product"],"needs_tools":true,"complexity":"normal","model_tier":"default",
     "tool_names":["product.search"],"expensive":true,"expense_reason":"Çok geniş ürün kapsamı.",
     "explosive":false,"explosion_reason":null,"clarification_required":false,
     "clarification_question":null,"clarification_options":[],"confidence":0.96}
    """));
var riskRouter = new AiRouter(discovered, search, riskRouterTransport, fixedConfiguration, new TestPrompts());
var riskRoute = await riskRouter.RouteAsync(Request("list every product and analyze all history", "products.catalog"));
Assert(riskRoute.IsExpensive && !riskRoute.IsExplosive && riskRoute.ModelTier == "default"
       && riskRoute.PreferredToolNames!.SequenceEqual(["product.search"])
       && riskRoute.RouterUsage is { InputTokens: 8, OutputTokens: 4 },
    "Router model selects model tier and exact validated tools while classifying cost and operational risk.");

var productHits = search.Search("recorded chronological product price history by exact barcode", ["product"], 5);
var plainNameHits = search.Search("arveles fiyat söyle", ["product"], 5);
var crossHits = search.Search("product and qr history", ["product", "qr"], 4);
Assert(productHits[0].Tool.Name == "product.get_price_history",
    "Tool search ranks a relevant Product lookup tool.");
Assert(plainNameHits[0].Tool.Name == "product.search_by_name",
    "Tool search ranks the narrow phrase-based catalog lookup for a plain medicine price request.");
Assert(crossHits.Any(hit => hit.Tool.Name.StartsWith("product.")) && crossHits.Any(hit => hit.Tool.Name.StartsWith("qr.")),
    "Tool search preserves candidates from every selected domain.");
Assert(productHits.All(hit => !hit.Tool.Name.StartsWith("sale.")), "Tool search excludes irrelevant namespaces.");

var selector = new AiModelSelector(fixedConfiguration);
Assert(selector.Select(new AiRouteDecision([], false, AiComplexity.Simple, 1), []).Model == "gpt-5.6-luna",
    "Simple model routing selects Luna.");
Assert(selector.Select(new AiRouteDecision([], false, AiComplexity.Normal, 1), []).Model == "gpt-5.6-terra",
    "Normal model routing selects Terra.");
Assert(selector.Select(new AiRouteDecision([], false, AiComplexity.Complex, 1), []).Model == "gpt-5.6-sol",
    "Complex model routing selects Sol.");
Assert(selector.Select(new AiRouteDecision([], false, AiComplexity.Simple, 1, ModelTier: "complex"), []).Model == "gpt-5.6-sol",
    "An explicit router model tier overrides the deterministic complexity fallback.");
Assert(AiRouterModelCatalog.Default.Id == "gpt-6-luna"
       && AiRouterModelCatalog.Options.Count == 13
       && AiRouterModelCatalog.Options.Where(option => option.Id != "gpt-5.6-terra")
           .All(option => option.InputPriceUsdPerMillion < 2m)
       && AiRouterModelCatalog.Options.All(option =>
           option.DocumentationUrl == $"https://developers.openai.com/api/docs/models/{option.Id}"),
    "The Debug model selector exposes Luna, Terra, and every curated router-compatible sub-$2 input model with an official model link.");

var noReasoningHandler = new FixedResponseHandler(
    HttpStatusCode.OK,
    """{"id":"resp_no_reasoning","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""");
var noReasoningTransport = new OpenAiResponsesTransport(
    new FixedAiConfigurationProvider(new AiConfiguration { GatewayEndpoint = "https://gateway.test/v1/responses" }),
    new HttpClient(noReasoningHandler));
await noReasoningTransport.SendAsync(new AiModelRequest(
    "gpt-4o-mini", string.Empty, "Test",
    [JsonSerializer.SerializeToElement(new { role = "user", content = "Test" })], [], false, false));
Assert(!noReasoningHandler.RequestBodies.Single().Contains("\"reasoning\"", StringComparison.Ordinal),
    "Non-reasoning lower-cost models omit the unsupported reasoning payload instead of causing a provider 400 response.");

var identityContext = new AiApplicationContext(
    PharmacyName: "Merkez Eczanesi", PharmacistName: "Ayşe", UserDisplayName: "Evren");
var identityValues = identityContext.RelevantValues();
Assert(identityValues["pharmacy_name"] == "Merkez Eczanesi"
       && identityValues["pharmacist_address"] == "Eczacı Ayşe"
       && identityValues["user_display_name"] == "Evren",
    "Temporary pharmacy, pharmacist, and user identity values enter trusted assistant context.");

var quotaHandler = new FixedResponseHandler(
    HttpStatusCode.TooManyRequests,
    """{"error":{"message":"You have no credits remaining.","type":"insufficient_quota","code":"insufficient_quota"}}""");
var quotaTransport = new OpenAiResponsesTransport(
    new FixedAiConfigurationProvider(new AiConfiguration { GatewayEndpoint = "https://gateway.test/v1/responses" }),
    new HttpClient(quotaHandler));
var quotaMapped = false;
try
{
    await quotaTransport.SendAsync(new AiModelRequest(
        "gpt-5.6-luna", "none", "Test",
        [JsonSerializer.SerializeToElement(new { role = "user", content = "Test" })], [], false, false));
}
catch (AiTransportException exception)
{
    quotaMapped = exception.Code == "INSUFFICIENT_QUOTA";
}
Assert(quotaMapped && quotaHandler.RequestCount == 1,
    "No-credit API responses map to a non-retried insufficient-quota error.");

var invalidHandler = new FixedResponseHandler(
    HttpStatusCode.BadRequest,
    """{"error":{"message":"Invalid tool schema.","type":"invalid_request_error","code":"invalid_function_parameters"}}""",
    "req_test_123");
var invalidTransport = new OpenAiResponsesTransport(
    new FixedAiConfigurationProvider(new AiConfiguration { GatewayEndpoint = "https://gateway.test/v1/responses" }),
    new HttpClient(invalidHandler));
AiTransportException? invalidTransportException = null;
try
{
    await invalidTransport.SendAsync(new AiModelRequest(
        "gpt-5.6-luna", "none", "Test",
        [JsonSerializer.SerializeToElement(new { role = "user", content = "Test" })], [], false, false));
}
catch (AiTransportException exception)
{
    invalidTransportException = exception;
}
Assert(invalidTransportException is { Code: "INVALID_REQUEST", HttpStatus: 400, RequestId: "req_test_123" },
    "Provider request failures preserve safe HTTP status and request ID for Debug mode.");

var testDirectory = Path.Combine(Path.GetTempPath(), "pharmind-ai-tests", Guid.NewGuid().ToString("N"));
await BranchValidationRegressionTests.RunAsync(testDirectory);
await ModelConfigurationRegressionTests.RunAsync(testDirectory, Assert);
var modelOverridePlan = """
    {"answer":"Merhaba","explosive":false,"explosion_reason":null,
     "corrected_request":"merhaba","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,"workflow_queue":[],"workflow_inputs":[]}
    """;
var modelOverrideTransport = new ScriptedTransport(true, ScriptedTransport.Final(modelOverridePlan));
var modelOverrideChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new DisplayTableWorkflow()]), modelOverrideTransport,
    new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true }), testDirectory,
    new TestWorkflowToolExecutor());
var modelOverrideResult = await modelOverrideChat.SendAsync(new AiRequestContext(
    "model-override-session", "merhaba", new AiApplicationContext(CurrentModule: "home"),
    new HashSet<string>(), RouterModelOverride: "gpt-4o-mini"));
Assert(modelOverrideResult.ModelUsed == "gpt-4o-mini"
       && modelOverrideTransport.Requests.Single().Model == "gpt-4o-mini"
       && string.IsNullOrEmpty(modelOverrideTransport.Requests.Single().ReasoningEffort),
    "A Debug model selection reaches the router request and applies model-specific reasoning compatibility.");
var configurationFile = Path.Combine(testDirectory, "configuration", "ai-settings.json");
var fileConfiguration = new AiConfigurationProvider(configurationFile);
Assert(File.Exists(configurationFile), "Configuration provider creates the documented default settings file.");
using (var configurationDocument = JsonDocument.Parse(File.ReadAllText(configurationFile)))
{
    Assert(configurationDocument.RootElement.GetProperty("DefaultModel").GetString() == "gpt-5.6-terra"
           && !configurationDocument.RootElement.GetProperty("DebugShowWorkflowFunctions").GetBoolean()
           && !configurationDocument.RootElement.TryGetProperty("OpenAiApiKey", out _),
        "Generated settings contain non-secret defaults and no API-key field.");
}
File.WriteAllText(configurationFile, """{"DebugMode":true,"DebugShowWorkflowFunctions":true,"MaxToolTurns":3}""");
Assert(fileConfiguration.GetCurrent().DebugMode && fileConfiguration.GetCurrent().DebugShowWorkflowFunctions && fileConfiguration.GetCurrent().MaxToolTurns == 3,
    "Configuration provider reloads edited settings for each request.");
fileConfiguration.Save(fileConfiguration.GetCurrent() with { DebugMode = false, DebugShowWorkflowFunctions = false, DefaultModel = "custom-test-model" });
Assert(!fileConfiguration.GetCurrent().DebugMode &&
       !fileConfiguration.GetCurrent().DebugShowWorkflowFunctions &&
       fileConfiguration.GetCurrent().DefaultModel == "custom-test-model",
    "Configuration provider atomically saves the Settings-screen debug toggle without losing other values.");
var routerFailureOrchestrator = CreateOrchestrator(
    [], new ScriptedTransport(true), testDirectory,
    router: new ThrowingRouter(new AiTransportException("INSUFFICIENT_QUOTA", "No credits.")));
var routerFailureResult = await routerFailureOrchestrator.RunAsync(Request("hello", null));
Assert(routerFailureResult.Status == AiAssistantStatus.Error &&
       routerFailureResult.Message.Contains("kredi", StringComparison.OrdinalIgnoreCase),
    "Router transport failures return a user-visible result instead of escaping to the WPF dispatcher.");

var preflightTransport = new ScriptedTransport(true, ScriptedTransport.Final("should not run before approval"));
var preflightOrchestrator = CreateOrchestrator(
    [], preflightTransport, testDirectory,
    router: new FixedRouter(new AiRouteDecision(
        ["product"], true, AiComplexity.Normal, 0.99, true, "default", [],
        IsExpensive: true, ExpenseReason: "Çok geniş kapsam.",
        IsExplosive: true, ExplosionReason: "Toplu değişiklik riski.")));
var preflightResult = await preflightOrchestrator.RunAsync(Request("bulk operation", "product"));
Assert(preflightResult.PendingRouteApproval is { IsExpensive: true, IsExplosive: true }
       && preflightResult.Message.Contains("Emin misiniz?", StringComparison.Ordinal)
       && preflightTransport.Requests.Count == 0,
    "Expensive or explosive routing pauses before the advanced model and exposes a hard-coded yes/no approval.");
var clarificationTransport = new ScriptedTransport(true);
var clarificationOrchestrator = CreateOrchestrator(
    [], clarificationTransport, testDirectory,
    router: new FixedRouter(new AiRouteDecision(
        ["sale"], true, AiComplexity.Normal, 0.99, true, "default", ["sale.aggregate_records"],
        NeedsClarification: true,
        ClarificationQuestion: "Satış hasılatını hangi kapsam için hesaplamamı istersiniz?",
        ClarificationOptions: ["Seçili ürün için", "Tüm eczane satışları için"])));
var clarificationResult = await clarificationOrchestrator.RunAsync(Request("son 3 ayın hasılatı", "product"));
Assert(clarificationResult.Status == AiAssistantStatus.ClarificationRequired
       && clarificationResult.SuggestedActions?.Count == 2
       && clarificationTransport.Requests.Count == 0,
    "Router clarification renders text choices and skips the larger main-model request.");
var executionConfiguration = new FixedAiConfigurationProvider(new AiConfiguration { ToolTimeoutSeconds = 1 });
var confirmationService = new AiConfirmationService();
var readTool = TestTool.ReadOnly("test.read", requiredParameter: true);
var executionRegistry = new AiToolRegistry([readTool]);
var executionGateway = new AiToolExecutionGateway(
    executionRegistry, new AllowPermissionService(), confirmationService, executionConfiguration,
    new AiOperationalLog(testDirectory));
var executionContext = Request("read", "test") with
{
    ApplicationContext = new AiApplicationContext(CurrentModule: "test", CurrentUserId: "user-1")
};
var readResult = await executionGateway.ExecuteAsync(executionContext, new AiToolCall("call-1", "test.read", "{\"value\":\"ok\"}"));
var invalidResult = await executionGateway.ExecuteAsync(executionContext, new AiToolCall("call-2", "test.read", "{}"));
Assert(readResult.IsSuccess && readTool.ExecutionCount == 1, "Gateway executes a valid read-only tool.");
Assert(invalidResult.Status == AiToolExecutionStatus.ValidationFailed && readTool.ExecutionCount == 1,
    "Gateway rejects invalid arguments before handler execution.");

var deniedGateway = new AiToolExecutionGateway(
    executionRegistry, new DenyPermissionService(), confirmationService, executionConfiguration,
    new AiOperationalLog(testDirectory));
var denied = await deniedGateway.ExecuteAsync(executionContext, new AiToolCall("call-3", "test.read", "{\"value\":\"ok\"}"));
Assert(denied.Status == AiToolExecutionStatus.PermissionDenied, "Gateway enforces application permissions.");

var throwing = new TestTool(TestTool.CreateDefinition("test.throw"), (_, _) => throw new InvalidOperationException("secret failure"));
var timeoutTool = new TestTool(TestTool.CreateDefinition("test.timeout"), async (_, token) =>
{
    await Task.Delay(TimeSpan.FromSeconds(5), token);
    return new { ok = true };
});
var failureGateway = new AiToolExecutionGateway(
    new AiToolRegistry([throwing, timeoutTool]), new AllowPermissionService(), confirmationService,
    executionConfiguration, new AiOperationalLog(testDirectory));
var failed = await failureGateway.ExecuteAsync(executionContext, new AiToolCall("call-4", "test.throw", "{}"));
var timedOut = await failureGateway.ExecuteAsync(executionContext, new AiToolCall("call-5", "test.timeout", "{}"));
Assert(failed.Status == AiToolExecutionStatus.ExecutionFailed && !failed.SafeMessage!.Contains("secret", StringComparison.Ordinal),
    "Gateway normalizes tool exceptions without leaking raw details.");
Assert(timedOut.Status == AiToolExecutionStatus.Timeout, "Gateway applies the configured tool timeout.");

var confirmationTool = new TestTool(TestTool.CreateDefinition("test.confirm", AiToolRisks.ConfirmationRequired, true),
    (parameters, _) => Task.FromResult<object>(new { echoed = parameters.GetProperty("value").GetString() }));
var confirmationGateway = new AiToolExecutionGateway(
    new AiToolRegistry([confirmationTool]), new AllowPermissionService(), confirmationService,
    executionConfiguration, new AiOperationalLog(testDirectory));
var pendingResult = await confirmationGateway.ExecuteAsync(
    executionContext, new AiToolCall("call-6", "test.confirm", "{\"value\":\"saved-exactly\"}"));
Assert(pendingResult.Status == AiToolExecutionStatus.ConfirmationRequired && confirmationTool.ExecutionCount == 0,
    "Confirmation-required tools do not execute immediately.");
Assert(confirmationService.Confirm(pendingResult.PendingAction!.Id, "user-1"), "The owning user can confirm a pending action.");
var confirmed = await confirmationGateway.ExecuteConfirmedAsync(pendingResult.PendingAction.Id, "user-1");
Assert(confirmed.IsSuccess && confirmed.OutputJson.Contains("saved-exactly", StringComparison.Ordinal) && confirmationTool.ExecutionCount == 1,
    "Confirmation executes the exact saved arguments once.");
var expiredView = confirmationService.Create(executionContext, new AiToolCall("call-7", "test.confirm", "{\"value\":\"x\"}"),
    confirmationTool.Definition, null, 0);
Assert(!confirmationService.Confirm(expiredView.Id, "user-1"), "Expired confirmation cannot execute.");
var rejectedView = confirmationService.Create(executionContext, new AiToolCall("call-8", "test.confirm", "{\"value\":\"x\"}"),
    confirmationTool.Definition, null, 1);
confirmationService.Reject(rejectedView.Id, "user-1");
Assert(!confirmationService.Confirm(rejectedView.Id, "user-1"), "Rejected confirmation cannot execute.");

var missingStore = new MissingToolStore(Path.Combine(testDirectory, "missing-dedup.json"));
var missingSuggestion = new MissingToolSuggestion(
    "Find a capability that is not registered", "unregistered capability", "test", "test.unregistered",
    "A deliberately unavailable test capability.", ["value"], "Structured result.",
    "No matching tool exists.", [], AiToolRisks.ReadOnly);
missingStore.Record(missingSuggestion);
var deduplicatedMissing = missingStore.Record(missingSuggestion);
Assert(deduplicatedMissing.Occurrences == 2 && missingStore.Load().Count == 1,
    "Missing-tool suggestions are persisted in structured deduplicated form.");

var twoGapDirectory = Path.Combine(testDirectory, "two-gaps");
var twoGapTransport = new ScriptedTransport(true, ScriptedTransport.ToolCalls(
    ("gap-1", "debug.report_missing_tool", MissingArguments("zetaquux orbital horizon", "product.zetaquux_horizon")),
    ("gap-2", "debug.report_missing_tool", MissingArguments("omegaquux nebula forecast", "product.omegaquux_forecast"))));
var twoGapOrchestrator = CreateOrchestrator(
    [], twoGapTransport, twoGapDirectory, new AiConfiguration { DebugMode = true });
var twoGapResult = await twoGapOrchestrator.RunAsync(Request("two unavailable capabilities", "product"));
var twoGapRecords = new MissingToolStore(Path.Combine(twoGapDirectory, "missing.json")).Load();
Assert(twoGapResult.Status == AiAssistantStatus.Unsupported && twoGapRecords.Count == 2,
    "Every missing capability in one model response is persisted for the refreshable missing-tools screen.");

var depotGapDirectory = Path.Combine(testDirectory, "depot-gap");
var depotGapTransport = new ScriptedTransport(true, ScriptedTransport.ToolCalls(
    ("depot-gap", "debug.report_missing_tool",
        MissingArguments("compare product depot price and campaign", "depot.compare_price", "depot"))));
var depotGapOrchestrator = CreateOrchestrator(
    [TestTool.ReadOnly("product.compare")], depotGapTransport, depotGapDirectory,
    new AiConfiguration { DebugMode = true },
    new FixedRouter(new AiRouteDecision(["depot"], true, AiComplexity.Simple, 1)));
var depotGapResult = await depotGapOrchestrator.RunAsync(Request("check product price at four depots", null));
Assert(depotGapResult.Status == AiAssistantStatus.Unsupported
       && depotGapTransport.Requests.Count == 1
       && new MissingToolStore(Path.Combine(depotGapDirectory, "missing.json")).Load().Count == 1,
    "A similarly worded Product tool is not retried as a missing Depot capability.");

var oneTool = TestTool.ReadOnly("product.lookup", requiredParameter: true);
var oneToolTransport = new ScriptedTransport(true,
    ScriptedTransport.ToolCalls(("call-a", "product.lookup", "{\"value\":\"123\"}")),
    ScriptedTransport.Final("Araç sonucu tamamlandı."));
var oneToolOrchestrator = CreateOrchestrator([oneTool], oneToolTransport, testDirectory);
var activityUpdates = new List<AiActivityUpdate>();
var oneToolRequest = Request("product lookup", "product") with
{
    Progress = new InlineProgress<AiActivityUpdate>(activityUpdates.Add)
};
var oneToolAnswer = await oneToolOrchestrator.RunAsync(oneToolRequest);
Assert(oneToolAnswer.Status == AiAssistantStatus.Success && oneTool.ExecutionCount == 1 && oneToolTransport.Requests.Count == 2,
    "Agent loop executes one tool and returns the final answer.");
Assert(oneToolAnswer.UsageByModel is [{ Stage: "Yanıt", Usage.InputTokens: 18, Usage.OutputTokens: 6 }],
    "Assistant results expose cumulative response-model input and output token usage.");
Assert(activityUpdates.Any(update => update.DebugDetail?.Contains("Tool started", StringComparison.Ordinal) == true)
       && activityUpdates.Any(update => update.Message == "Yanıt tamamlandı."),
    "Agent loop publishes safe live routing, tool, and completion progress for the loading message.");

var conversationTransport = new ScriptedTransport(true,
    ScriptedTransport.Final("First answer."),
    ScriptedTransport.Final("Second answer."));
var conversationOrchestrator = CreateOrchestrator([], conversationTransport, testDirectory);
var conversationRequest = Request("Birinci mesaj", "product");
await conversationOrchestrator.RunAsync(conversationRequest);
await conversationOrchestrator.RunAsync(conversationRequest with { UserMessage = "İkinci mesaj" });
Assert(conversationTransport.Requests[1].InputItems.Any(item =>
        item.GetRawText().Contains("First answer.", StringComparison.Ordinal)),
    "A second message in the same session replays bounded local conversation context.");

var actionsTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"status":"clarification_required","message":"Bir seçenek belirleyin.","blocks":[],
     "suggested_actions":[{"id":"last_month","label":"Geçen ay"},{"id":"last_six_months","label":"Son 6 ay"}]}
    """));
var actionsOrchestrator = CreateOrchestrator([], actionsTransport, testDirectory);
var actionsResult = await actionsOrchestrator.RunAsync(Request("choose a period", null));
Assert(actionsResult.Status == AiAssistantStatus.ClarificationRequired
       && actionsResult.SuggestedActions?.Select(action => action.Label).SequenceEqual(["Geçen ay", "Son 6 ay"]) == true,
    "Structured suggested actions provide safe text-only choices for application-rendered buttons.");

var parallelProbe = new ParallelProbe();
var parallelOne = TestTool.ReadOnly("product.first", execute: parallelProbe.ExecuteAsync);
var parallelTwo = TestTool.ReadOnly("product.second", execute: parallelProbe.ExecuteAsync);
var multiTransport = new ScriptedTransport(true,
    ScriptedTransport.ToolCalls(
        ("call-b", "product.first", "{}"),
        ("call-c", "product.second", "{}")),
    ScriptedTransport.Final("İki sonuç tamamlandı."));
var multiOrchestrator = CreateOrchestrator([parallelOne, parallelTwo], multiTransport, testDirectory);
var multiAnswer = await multiOrchestrator.RunAsync(Request("compare product first and second", "product"));
Assert(multiAnswer.Status == AiAssistantStatus.Success && parallelProbe.MaximumConcurrency == 2,
    "Agent loop executes independent read-only calls in parallel.");

var repeatedTool = TestTool.ReadOnly("product.repeat");
var repeatedTransport = new ScriptedTransport(true,
    ScriptedTransport.ToolCalls(("call-d", "product.repeat", "{}")),
    ScriptedTransport.ToolCalls(("call-e", "product.repeat", "{}")),
    ScriptedTransport.Final("should not be reached"));
var repeatedOrchestrator = CreateOrchestrator([repeatedTool], repeatedTransport, testDirectory,
    new AiConfiguration { MaxRepeatedToolCalls = 1, MaxToolTurns = 4 });
var repeatedAnswer = await repeatedOrchestrator.RunAsync(Request("repeat product", "product"));
Assert(repeatedAnswer.Status == AiAssistantStatus.Error && repeatedTool.ExecutionCount == 1,
    "Agent loop detects repeated identical calls and stops safely.");

var discoveredWorkflows = AiWorkflowRegistry.Discover();
var requiredWorkflowNames = new[]
{
    "barkod_bilgisi_getir", "hareketleri_gor", "satislari_gor", "satis_hasilati_ozeti",
    "urun_muadillerini_getir", "urunleri_karsilastir", "satis_detayi_getir", "hasta_urun_ozeti",
    "satis_dagilimi_analizi", "donemleri_karsilastir", "karlilik_analizi", "iskonto_analizi",
    "iade_analizi", "karekod_gecmisini_getir", "fiyat_donemlerini_karsilastir",
    "bilgi_kartlari_goster", "metrik_kartlari_goster", "sirali_liste_goster",
    "zaman_cizelgesinde_goster", "bolumlu_ozet_goster", "siparis_istegini_tani",
    "hasta_alisveris_araligi", "geri_kazanilacak_hastalar", "ayni_gun_tekrar_satislari",
    "receteli_satis_payi_analizi", "hasilat_yogunlugu_analizi", "gunluk_ortalama_hasilat",
    "hafta_gunu_satis_analizi", "hafta_gunlerini_karsilastir", "saatlik_satis_analizi",
    "kamu_hasta_odeme_analizi", "kamu_ucretli_satislari_sirala", "urun_satis_siralamalarini_karsilastir",
    "hareket_donemlerini_karsilastir", "yuksek_iskontolu_satislar", "zararina_satis_satirlari",
    "urun_kar_marji_analizi", "abc_hasilat_siniflandirmasi", "pasif_urunleri_filtrele",
    "hasta_iletisim_bilgisi_getir", "hasta_son_urunleri_getir", "alim_miktari_oner",
    "stok_portfoy_analizi", "son_iadeleri_gor", "urun_stok_ozeti",
    "hareket_dagilimi_analizi", "hareket_coklu_dagilim_analizi", "karekod_tutarlilik_kontrolu",
    "urun_katalog_analizi", "odeme_dagilimi_analizi", "sepet_analizi", "satis_tutarlilik_kontrolu",
    "stok_satis_kesisim_analizi", "medula_oturumunu_yonet", "medula_hasta_klinik_bilgisi_getir",
    "medula_hasta_ilac_listesini_getir",
    "medula_ilac_bilgisi_getir", "medula_referans_bilgisi_ara", "medula_recete_filtrelerini_getir",
    "medula_receteleri_ara", "medula_hasta_recetelerini_getir", "medula_recete_bilgisi_getir",
    "medula_erecete_sorgula", "medula_hasta_rapor_detayini_getir", "medula_son_receteyi_ac"
};
Assert(discoveredWorkflows.Definitions.Count >= 70
       && requiredWorkflowNames.All(name => discoveredWorkflows.RouterDefinitions.Any(definition => definition.Name == name))
       && discoveredWorkflows.RouterDefinitions.Count == discoveredWorkflows.Definitions.Count - 3
       && discoveredWorkflows.Get("debug_workflow") is not null
       && !discoveredWorkflows.RouterDefinitions.Any(definition => definition.Name is "debug_workflow" or "ilac_barkod_eslestir_1" or "ilac_barkod_eslestir_2"),
    "Workflow discovery exposes the expanded curated registry and keeps Debug and comparison matcher slots internal.");
if (string.Equals(Environment.GetEnvironmentVariable("PHARMACY_AI_AMBIGUITY_LIVE_TEST"), "1", StringComparison.Ordinal))
{
    var liveAmbiguityConfiguration = new FixedAiConfigurationProvider(new AiConfiguration
    {
        StoreResponses = false,
        RouterModel = "gpt-5.6-luna",
        RouterReasoningEffort = "none",
        RouterMaxOutputTokens = 1200
    });
    var liveAmbiguityChat = new WorkflowRouterChatService(
        discoveredWorkflows,
        new OpenAiResponsesTransport(liveAmbiguityConfiguration),
        liveAmbiguityConfiguration,
        new AiConversationManager(liveAmbiguityConfiguration),
        new AiUsageTelemetry(Path.Combine(testDirectory, "live-medication-ambiguity-usage.json")),
        new AiPromptProvider(),
        new TestWorkflowToolExecutor());
    var liveAmbiguity = await liveAmbiguityChat.SendAsync(new AiRequestContext(
        "live-medication-ambiguity-session", "Evren Acar'ın önceki ilaçlarını göster",
        new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
    var labels = liveAmbiguity.PendingSelection?.Options.Select(option => option.Label).ToArray() ?? [];
    Assert(liveAmbiguity.PendingSelection is { Options.Count: >= 4, AllowsMultiple: false }
           && labels.Count(label => label.Contains("Reçete", StringComparison.CurrentCultureIgnoreCase)) >= 2
           && labels.Any(label => label.Contains("İlaç Listesi", StringComparison.CurrentCultureIgnoreCase))
           && labels.Any(label => label.Contains("Satış", StringComparison.CurrentCultureIgnoreCase)),
        "Live Luna independently plans current/previous prescription, Medula patient-list, and past-sales choices for ambiguous medication wording.");
    Console.WriteLine("PASS optional-live-medication-ambiguity-router: " + string.Join(" | ", labels));
}
var registeredAiTools = AiToolRegistry.Discover();
var expectedMedulaTools = new[]
{
    "medula.session_status", "medula.session_login", "medula.session_restart",
    "medula.prescription_filter_options", "medula.aggregate_prescription_reports",
    "medula.prescriptions_search", "medula.patient_prescriptions_search",
    "medula.prescription_get", "medula.prescription_history",
    "medula.e_prescriptions_search", "medula.e_prescription_get",
    "medula.patient_medication_history_search", "medula.patient_medication_list_get", "medula.patient_eligibility_get",
    "medula.patient_reports_search", "medula.patient_report_get",
    "medula.drug_search", "medula.drug_get", "medula.drug_equivalents_search",
    "medula.doctor_search", "medula.diagnosis_search",
    "medula.prescription_warnings_get", "medula.patient_examinations_search",
    "medula.patient_off_label_approvals_search", "medula.patient_treatment_scheme_get"
};
var workflowMedulaTools = discoveredWorkflows.Definitions
    .SelectMany(definition => definition.RequiredTools ?? [])
    .Where(tool => tool.StartsWith("medula.", StringComparison.Ordinal))
    .ToHashSet(StringComparer.Ordinal);
Assert(expectedMedulaTools.All(tool => registeredAiTools.GetTool(tool) is not null)
       && expectedMedulaTools.All(workflowMedulaTools.Contains),
    "Every implemented Medula AI tool is registered and reachable through at least one curated workflow.");

var medulaEligibilityTool = new TestTool(new AiToolDefinition(
        "medula.patient_eligibility_get", "Test live Medula eligibility lookup.",
        AiToolRisks.ConfirmationRequired,
        new Dictionary<string, AiToolParameterDefinition>
        {
            ["patient_tc"] = new("string", "Patient identity number.", true)
        },
        new Dictionary<string, string> { ["fields"] = "array" },
        ["medula", "patient", "eligibility"]),
    (parameters, _) => Task.FromResult<object>(new
    {
        success = true,
        message = "Müstehaklık bilgisi alındı.",
        fields = new[]
        {
            new { label = "T.C. Kimlik No", value = parameters.GetProperty("patient_tc").GetString() },
            new { label = "Müstehaklık", value = (string?)"Müstehaktır" }
        }
    }));
var medulaConfirmationService = new AiConfirmationService();
var medulaToolRegistry = new AiToolRegistry([
    medulaEligibilityTool,
    TestTool.ReadOnly("medula.patient_medication_history_search"),
    TestTool.ReadOnly("medula.patient_reports_search"),
    TestTool.ReadOnly("medula.patient_examinations_search"),
    TestTool.ReadOnly("medula.patient_off_label_approvals_search")
]);
var medulaGateway = new AiToolExecutionGateway(
    medulaToolRegistry, new AllowPermissionService(), medulaConfirmationService,
    fixedConfiguration, new AiOperationalLog(testDirectory));
var medulaWorkflowExecutor = new AiWorkflowToolExecutor(medulaToolRegistry, medulaGateway);
var medulaApprovalTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"18422213250 T.C. kimlik numaralı hastanın Medula müstehaklığını göster","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["medula_hasta_klinik_bilgisi_getir","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"medula_hasta_klinik_bilgisi_getir","arguments":[{"name":"operation","value":"eligibility"},{"name":"patient_tc","value":"18422213250"}]},
       {"workflow_name":"tabloda_goster","arguments":[{"name":"title","value":"Medula Müstehaklık Bilgisi"}]}
     ]}
    """));
var medulaApprovalChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new MedulaPatientClinicalWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()]),
    medulaApprovalTransport, fixedConfiguration, testDirectory, medulaWorkflowExecutor);
var medulaPending = await medulaApprovalChat.SendAsync(new AiRequestContext(
    "medula-confirmation-session", "18422213250 T.C. kimlik numaralı hastanın Medula müstehaklığını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(medulaPending.PendingApproval is not null && medulaEligibilityTool.ExecutionCount == 0,
    "A live Medula workflow pauses at the application-owned confirmation gate before opening a clinical screen.");
var medulaApproved = await medulaApprovalChat.ApproveAsync(medulaPending.PendingApproval!.Id, "user-1");
Assert(medulaApproved.PendingApproval is null
       && medulaApproved.Table is { Rows.Count: 1 }
       && medulaApproved.Table.Columns.Any(column => column.Label == "T.C. Kimlik No")
       && medulaApproved.Table.Columns.Any(column => column.Label == "Müstehaklık")
       && medulaEligibilityTool.ExecutionCount == 1
       && medulaApprovalTransport.Requests.Count == 1,
    "Approving a live Medula workflow executes the exact saved tool call once, formats its fields, and resumes the queue without a second model call.");
var latestMedulaExecutor = new LatestMedulaPrescriptionToolExecutor();
var latestMedulaTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"meduladan en son reçeteyi aç yeğen","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","medula_hasta_recetelerini_getir","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"meduladan en son"}]},
       {"workflow_name":"medula_hasta_recetelerini_getir","arguments":[]},
       {"workflow_name":"tabloda_goster","arguments":[{"name":"title","value":"Hastanın Medula Reçeteleri"}]}
     ]}
    """), ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"Bu reçetenin ilaçları ne","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["medula_recete_bilgisi_getir"],
     "workflow_inputs":[{"workflow_name":"medula_recete_bilgisi_getir","arguments":[{"name":"operation","value":"detail"}]}]}
    """));
var latestMedulaChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new PatientMatchWorkflow(), new MedulaPatientPrescriptionWorkflow(),
        new MedulaLatestPrescriptionWorkflow(), new InformationCardsWorkflow(),
        new MedulaPrescriptionInformationWorkflow(), new PreviousPrescriptionContextWorkflow(),
        new DisplayTableWorkflow(), new DebugWorkflow()
    ]),
    latestMedulaTransport, fixedConfiguration, testDirectory, latestMedulaExecutor);
var latestMedulaResult = await latestMedulaChat.SendAsync(new AiRequestContext(
    "latest-medula-session", "meduladan en son reçeteyi aç yeğen",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(latestMedulaResult.PendingSelection is null
       && latestMedulaResult.WorkflowQueue?.SequenceEqual(
           ["medula_son_receteyi_ac"]) == true
       && latestMedulaExecutor.Calls.SequenceEqual(
           ["medula.prescriptions_search", "medula.prescription_get"])
       && latestMedulaExecutor.DetailArguments.GetProperty("prescription_number").GetString() == "22222222"
       && latestMedulaResult.Prescription is { PrescriptionNumber: "22222222", Drugs.Count: 1, IsLiveOnMedula: true }
       && latestMedulaResult.Prescription.PrescriptionFields.Any(field => field.Label == "Reçete Tarihi")
       && latestMedulaResult.Prescription.DoctorFields.Any(field => field.Label == "Doktor Tescil Numarası")
       && latestMedulaResult.Table is null && latestMedulaResult.Content is null,
    "A generic latest-Medula-prescription request never treats command words as a patient and renders the newest detail as a prescription view, not generic cards or a table.");
var prescriptionListTable = await new DisplayTableWorkflow().ExecuteAsync(new AiWorkflowContext(
    "reçeteleri göster", "reçeteleri göster", new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["medula_receteleri_ara"] = JsonSerializer.SerializeToElement(new
        {
            rows = new[] { new { prescription_number = "3PXYOGV", electronic_prescription_number = "2JYMZPW", last_transaction_date = "05.09.2026" } },
            metadata = new { columns = new[] { "prescription_number", "electronic_prescription_number", "last_transaction_date" } }
        })
    }, [], Parameters: new Dictionary<string, string> { ["title"] = "Medula Reçeteleri" }));
Assert(prescriptionListTable.Table!.Columns.Any(column => column.Label == "Reçete Numarası")
       && prescriptionListTable.Table.Columns.Any(column => column.Label == "E-Reçete Numarası")
       && prescriptionListTable.Table.Columns.Any(column => column.Label == "Son İşlem Tarihi"),
    "Prescription list tables use Turkish headers rather than title-cased English property names.");
var latestPrescriptionFollowUp = await latestMedulaChat.SendAsync(new AiRequestContext(
    "latest-medula-session", "Bu reçetenin ilaçları ne",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(latestPrescriptionFollowUp.PendingApproval is not null
       && latestPrescriptionFollowUp.PendingApproval.Summary.Contains("22222222", StringComparison.Ordinal),
    "A pronoun-based prescription follow-up asks the user to confirm the exact remembered prescription.");
var approvedPrescriptionFollowUp = await latestMedulaChat.ApproveAsync(
    latestPrescriptionFollowUp.PendingApproval!.Id, "user-1");
Assert(approvedPrescriptionFollowUp.Prescription is { PrescriptionNumber: "22222222", Drugs.Count: 1 }
       && latestMedulaExecutor.Calls.Count(call => call == "medula.prescription_get") == 2
       && latestMedulaTransport.Requests.Count == 2
       && approvedPrescriptionFollowUp.UsageByModel is { Count: 0 },
    "Confirming the remembered prescription injects its number and resumes the saved queue with no second model call.");

var filteredLatestPrescriptionExecutor = new FilteredLatestPrescriptionToolExecutor();
var filteredLatestPrescriptionTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"VIDAPTIN MET'i en son sattığım reçeteyi göster","correction_confidence":0.99,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["medula_son_receteyi_ac"],
     "workflow_inputs":[{"workflow_name":"medula_son_receteyi_ac","arguments":[]}]}
    """));
var filteredLatestPrescriptionChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new DrugBarcodeMatchWorkflow(), new MedulaLatestPrescriptionWorkflow(), new DebugWorkflow()
    ]),
    filteredLatestPrescriptionTransport, fixedConfiguration, testDirectory, filteredLatestPrescriptionExecutor);
var filteredLatestPrescription = await filteredLatestPrescriptionChat.SendAsync(new AiRequestContext(
    "medicine-filtered-latest-prescription-session", "vidaptin meti en son sattığım reçeteyi göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(filteredLatestPrescription.PendingSelection is { Options.Count: 2 }
       && filteredLatestPrescription.WorkflowQueue?.SequenceEqual(
           ["ilac_barkod_eslestir", "medula_son_receteyi_ac"]) == true
       && filteredLatestPrescriptionTransport.Requests.Count == 1,
    "A medicine-specific latest-prescription request repairs a router plan that dropped the medicine and pauses for exact barcode selection.");
var selectedFilteredLatestPrescription = await filteredLatestPrescriptionChat.ContinueSelectionAsync(
    filteredLatestPrescription.PendingSelection!.Id, "user-1", ["8699500000001"]);
Assert(selectedFilteredLatestPrescription.Prescription is { PrescriptionNumber: "RX-VID-LATEST", Drugs.Count: 1 }
       && filteredLatestPrescriptionExecutor.Calls.SequenceEqual(
           ["product.search_by_name", "sale.search_records", "medula.prescription_get"])
       && filteredLatestPrescriptionExecutor.SaleArguments.GetProperty("barcodes")[0].GetString() == "8699500000001"
       && filteredLatestPrescriptionExecutor.SaleArguments.GetProperty("sale_types")[0].GetString() == "prescription"
       && filteredLatestPrescriptionExecutor.SaleArguments.GetProperty("include_refunds").ValueKind == JsonValueKind.False
       && filteredLatestPrescriptionExecutor.DetailArguments.GetProperty("prescription_number").GetString() == "RX-VID-LATEST"
       && filteredLatestPrescriptionTransport.Requests.Count == 1
       && selectedFilteredLatestPrescription.Table is null
       && selectedFilteredLatestPrescription.Content is null,
    "After selection, the saved queue filters prescription sales by the selected medicine, opens the newest matching prescription, and makes no second model call.");

var medulaPatientContextExecutor = new MedulaPatientContextToolExecutor();
var medulaPatientContextTransport = new ScriptedTransport(true,
    ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"Asım Uras Yılmaz adlı hastanın geçmiş ilaçlarını meduladan getir","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["medula_referans_bilgisi_ara","tabloda_goster"],
     "workflow_inputs":[{"workflow_name":"medula_referans_bilgisi_ara","arguments":[{"name":"operation","value":"diagnosis"},{"name":"query","value":"Asım Uras Yılmaz"}]}]}
    """),
    ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"ASIM URAS YILMAZ ile aynı medula ilaç sorgusunu yap","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["onceki_hasta_bilgisini_getir","medula_hasta_klinik_bilgisi_getir","tabloda_goster"],
     "workflow_inputs":[{"workflow_name":"medula_hasta_klinik_bilgisi_getir","arguments":[{"name":"operation","value":"medication_history"}]}]}
    """));
var medulaPatientContextChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new PatientMatchWorkflow(), new MedulaPatientClinicalWorkflow(), new PreviousPatientContextWorkflow(),
        new WorkflowBranchSelectionWorkflow(), new PatientProductSummaryWorkflow(), new MedulaReferenceSearchWorkflow(),
        new DisplayTableWorkflow(), new DebugWorkflow()
    ]),
    medulaPatientContextTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var medulaPatientSession = "medula-patient-context-session";
var asimHistory = await medulaPatientContextChat.SendAsync(new AiRequestContext(
    medulaPatientSession, "Asım Uras Yılmaz adlı hastanın geçmiş ilaçlarını meduladan getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(asimHistory.Table is { Rows.Count: 1 }
       && medulaPatientContextExecutor.PatientQueries.SequenceEqual(["ASIM URAS YILMAZ"])
       && medulaPatientContextExecutor.MedicationHistoryPatientTcs.SequenceEqual(["15748006450"])
       && medulaPatientContextExecutor.DiagnosisCalls == 0,
    "Turkish patient names are uppercased with Turkish casing, matched before Medula history, and never misrouted because 'hastanın' contains 'tanı'.");
var sameMedulaQuery = await medulaPatientContextChat.SendAsync(new AiRequestContext(
    medulaPatientSession, "ASIM URAS YILMAZ ile aynı medula ilaç sorgusunu yap",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(sameMedulaQuery.PendingApproval?.Summary.Contains("ASIM URAS YILMAZ", StringComparison.Ordinal) == true,
    "Repeating the same Medula medication query asks to confirm the remembered patient.");
var approvedSameMedulaQuery = await medulaPatientContextChat.ApproveAsync(
    sameMedulaQuery.PendingApproval!.Id, "user-1");
Assert(approvedSameMedulaQuery.Table is { Rows.Count: 1 }
       && medulaPatientContextExecutor.MedicationHistoryPatientTcs.Count == 2
       && medulaPatientContextTransport.Requests.Count == 2
       && approvedSameMedulaQuery.UsageByModel is { Count: 0 },
    "Confirmed previous-patient context repeats the exact Medula medication workflow without a continuation model call.");
var patientMedicationListWorkflow = await new MedulaPatientMedicationListWorkflow().ExecuteAsync(
    new AiWorkflowContext(
        "Evren Acar'ın Medula ilaç listesini göster",
        "Evren Acar'ın Medula ilaç listesini göster",
        new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"),
        new Dictionary<string, JsonElement>
        {
            ["hasta_eslestir"] = JsonSerializer.SerializeToElement(new
            {
                selected_patient = new
                {
                    patient_tc = "12289124650",
                    name = "EVREN ACAR"
                }
            })
        },
        [],
        RequestContext: Request("Evren Acar'ın Medula ilaç listesini göster", "home"),
        ToolExecutor: medulaPatientContextExecutor));
Assert(patientMedicationListWorkflow.Status == AiWorkflowStepStatus.Completed
       && patientMedicationListWorkflow.Output.GetProperty("rows").GetArrayLength() == 1
       && medulaPatientContextExecutor.MedicationListPatientTcs.SequenceEqual(["12289124650"]),
    "The read-only Medula patient medication-list workflow inherits the matched patient and renders the İlaç screen rows.");

var patientReportsTransport = new ScriptedTransport(true);
var patientReportsChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new PatientMatchWorkflow(), new PreviousPatientContextWorkflow(), new MedulaPatientClinicalWorkflow(),
        new DisplayTableWorkflow(), new DebugWorkflow()
    ]), patientReportsTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var patientReports = await patientReportsChat.SendAsync(new AiRequestContext(
    "patient-reports-daily-host-session", "Evren Acar adlı hastanın geçmiş raporlarını Medula'dan getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(patientReports.Table is { Rows.Count: 1 }
       && patientReports.WorkflowQueue?.SequenceEqual(
           ["hasta_eslestir", "medula_hasta_klinik_bilgisi_getir", "tabloda_goster"]) == true
       && medulaPatientContextExecutor.PatientQueries.Last() == "EVREN ACAR"
       && medulaPatientContextExecutor.ReportPatientTcs.SequenceEqual(["12289124650"])
       && medulaPatientContextExecutor.ReportIncludeExpired.SequenceEqual([true])
       && medulaPatientContextExecutor.DiagnosisCalls == 0
       && patientReportsTransport.Requests.Count == 0
       && patientReports.UsageByModel is { Count: 0 },
    "A clear named-patient report request is planned without a model call, inherits the exact matched T.C., includes expired reports when requested, and never runs diagnosis search.");

var latestOzlemReports = await patientReportsChat.SendAsync(new AiRequestContext(
    "latest-ozlem-reports-session", "özlem dumanın son raporlarını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(latestOzlemReports.Table is { Rows.Count: 1 }
       && medulaPatientContextExecutor.PatientQueries.Last() == "ÖZLEM DUMAN"
       && medulaPatientContextExecutor.ReportPatientTcs.Last() == "25411225316"
       && patientReportsTransport.Requests.Count == 0,
    $"A latest-report modifier is removed from the Turkish patient name and routes to the existing report-list workflow without a model call. Actual: message={latestOzlemReports.Message}; queue={string.Join(',', latestOzlemReports.WorkflowQueue ?? [])}; patient={medulaPatientContextExecutor.PatientQueries.LastOrDefault()}; report_tc={medulaPatientContextExecutor.ReportPatientTcs.LastOrDefault()}; model_calls={patientReportsTransport.Requests.Count}.");
var rememberedReportFollowUp = await patientReportsChat.SendAsync(new AiRequestContext(
    "latest-ozlem-reports-session", "raporlarını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(rememberedReportFollowUp.PendingApproval?.Summary.Contains("ÖZLEM DUMAN", StringComparison.Ordinal) == true
       && rememberedReportFollowUp.Message.Contains("ÖZLEM DUMAN", StringComparison.Ordinal)
       && !rememberedReportFollowUp.Message.Contains("en az iki geçerli seçenek", StringComparison.Ordinal),
    "A report follow-up confirms the remembered patient instead of exposing a branch-validation error.");
var approvedRememberedReports = await patientReportsChat.ApproveAsync(
    rememberedReportFollowUp.PendingApproval!.Id, "user-1");
Assert(approvedRememberedReports.Table is { Rows.Count: 1 }
       && medulaPatientContextExecutor.ReportPatientTcs.Last() == "25411225316"
       && approvedRememberedReports.UsageByModel is { Count: 0 }
       && patientReportsTransport.Requests.Count == 0,
    "Confirming the remembered patient continues the report-list queue locally without a second model call.");

medulaPatientContextExecutor.ReturnEmptyReports = true;
var emptyPatientReports = await patientReportsChat.SendAsync(new AiRequestContext(
    "empty-patient-reports-session", "Evren Acarın raporlarını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(emptyPatientReports.Table is null
       && emptyPatientReports.Content is null
       && emptyPatientReports.Message.Contains("EVREN ACAR adlı hastanın Medula raporu bulunamadı", StringComparison.Ordinal)
       && patientReportsTransport.Requests.Count == 0,
    "An empty Medula patient-report result returns a specific Turkish message without an empty card or table.");
medulaPatientContextExecutor.ReturnEmptyReports = false;

var patientPrescriptionTransport = new ScriptedTransport(true);
var patientPrescriptionChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new PatientMatchWorkflow(), new MedulaPatientPrescriptionWorkflow(), new PreviousPatientContextWorkflow(),
        new DisplayTableWorkflow(), new DebugWorkflow()
    ]), patientPrescriptionTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var patientPrescriptions = await patientPrescriptionChat.SendAsync(new AiRequestContext(
    "unified-patient-prescriptions-session", "Evren Acar'ın geçmiş reçetelerini getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(patientPrescriptions.WorkflowQueue?.SequenceEqual(
           ["hasta_eslestir", "medula_hasta_recetelerini_getir", "tabloda_goster"]) == true
       && patientPrescriptions.Table is { Rows.Count: 2, RowBackgroundField: "is_live_on_medula" }
       && patientPrescriptions.Table.Columns.Any(column => column is { Key: "total_amount", Label: "Toplam Tutar", IsVisibleByDefault: true })
       && patientPrescriptions.Table.Columns.Any(column => column is { Key: "prescription_uid", IsVisibleByDefault: false })
       && medulaPatientContextExecutor.PrescriptionPatientTcs.SequenceEqual(["12289124650"])
       && patientPrescriptionTransport.Requests.Count == 0
       && patientPrescriptions.UsageByModel is { Count: 0 },
    "A named-patient prescription request uses the unified local-plus-active workflow without a model call and marks rows for standard active-prescription coloring.");

var prescriptionToday = DateOnly.FromDateTime(DateTime.Today);
var prescriptionWeekStart = prescriptionToday.AddDays(-(((int)prescriptionToday.DayOfWeek + 6) % 7));
var weeklyPatientPrescriptions = await patientPrescriptionChat.SendAsync(new AiRequestContext(
    "weekly-patient-prescriptions-session", "evren acarın bu haftaki reçetelerini getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var weeklyArguments = medulaPatientContextExecutor.PrescriptionArguments.Last();
Assert(medulaPatientContextExecutor.PatientQueries.Last() == "EVREN ACAR"
       && weeklyArguments.GetProperty("start_date").GetString() ==
          prescriptionWeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
       && weeklyArguments.GetProperty("end_date").GetString() ==
          prescriptionToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
       && weeklyPatientPrescriptions.Message.Contains(
           prescriptionWeekStart == prescriptionToday ? "günü kontrol edildi" : "dönemi kontrol edildi",
           StringComparison.Ordinal)
       && patientPrescriptionTransport.Requests.Count == 0,
    "Turkish possessive patient names and this-week scope are extracted deterministically and passed to the unified patient prescription search.");

foreach (var (session, requestText) in new[]
         {
             ("all-patient-prescriptions-session", "evren acarın tüm reçetelerini getir"),
             ("all-uppercase-patient-prescriptions-session", "Evren acarın BÜTÜN reçetelerini getir")
         })
{
    var allPatientPrescriptions = await patientPrescriptionChat.SendAsync(new AiRequestContext(
        session, requestText,
        new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
    Assert(allPatientPrescriptions.Table is { Rows.Count: 2 }
           && medulaPatientContextExecutor.PatientQueries.Last() == "EVREN ACAR"
           && medulaPatientContextExecutor.PrescriptionArguments.Last().GetProperty("limit").GetInt32() == 1000
           && patientPrescriptionTransport.Requests.Count == 0,
        "All/BÜTÜN modifiers are excluded from the Turkish patient name before local-plus-active prescription lookup.");
}

var inspectedPatient = await patientPrescriptionChat.SendAsync(new AiRequestContext(
    "all-uppercase-patient-prescriptions-session", "hasta olarak kimi aratıyorsun?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(inspectedPatient.Message.Contains("EVREN ACAR", StringComparison.Ordinal)
       && inspectedPatient.Message.Contains("122", StringComparison.Ordinal)
       && inspectedPatient.WorkflowQueue is { Count: 0 }
       && patientPrescriptionTransport.Requests.Count == 0,
    "A patient-context question answers from the exact remembered patient instead of rerunning a prescription search.");

var prescriptionScopeTransport = new ScriptedTransport(true);
var prescriptionScopeChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new MedulaPrescriptionSearchWorkflow(), new DisplayTableWorkflow()]),
    prescriptionScopeTransport, fixedConfiguration, testDirectory, latestMedulaExecutor);
var unfinishedPrescriptions = await prescriptionScopeChat.SendAsync(new AiRequestContext(
    "unfinished-prescription-scope-session", "sonalndırılmamış reçeteleri getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var unfinishedArguments = latestMedulaExecutor.SearchArguments.Last();
var todayIso = prescriptionToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
Assert(unfinishedPrescriptions.WorkflowQueue?.SequenceEqual(["medula_receteleri_ara", "tabloda_goster"]) == true
       && unfinishedArguments.GetProperty("mode").GetString() == "live_date_range"
       && unfinishedArguments.GetProperty("start_date").GetString() == todayIso
       && unfinishedArguments.GetProperty("end_date").GetString() == todayIso
       && unfinishedArguments.GetProperty("invoice_type_codes").EnumerateArray()
           .Select(value => value.GetString()).SequenceEqual(["1", "4", "28", "7"])
       && unfinishedArguments.GetProperty("qr_status").GetString() == "unfinalized"
       && unfinishedPrescriptions.Message.Contains("Tarih belirtilmediği için yalnızca", StringComparison.Ordinal)
       && unfinishedPrescriptions.Message.Contains("A Grubu, B Grubu ve C Grubu", StringComparison.Ordinal)
       && unfinishedPrescriptions.Message.Contains("sonlandırılmamış", StringComparison.Ordinal)
       && prescriptionScopeTransport.Requests.Count == 0,
    "An unfinished-prescription request corrects the typo, searches today's normal Medula A/B/C groups plus the complete local date slice, and states the implicit period.");

var threeDayAbcContinuation = await prescriptionScopeChat.SendAsync(new AiRequestContext(
    "unfinished-prescription-scope-session", "A b ve c için son 3 gün kontrol et",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var threeDayAbcArguments = latestMedulaExecutor.SearchArguments.Last();
Assert(threeDayAbcContinuation.WorkflowQueue?.SequenceEqual(["medula_receteleri_ara", "tabloda_goster"]) == true
       && threeDayAbcArguments.GetProperty("start_date").GetString() ==
          prescriptionToday.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
       && threeDayAbcArguments.GetProperty("end_date").GetString() == todayIso
       && threeDayAbcArguments.GetProperty("invoice_type_codes").EnumerateArray()
           .Select(value => value.GetString()).SequenceEqual(["1", "4", "28", "7"])
       && threeDayAbcArguments.GetProperty("qr_status").GetString() == "unfinalized"
       && threeDayAbcContinuation.Message.Contains("A Grubu, B Grubu ve C Grubu", StringComparison.Ordinal)
       && threeDayAbcContinuation.Message.Contains("sonlandırılmamış", StringComparison.Ordinal)
       && !threeDayAbcContinuation.Message.Contains("sınıflandırıldı", StringComparison.OrdinalIgnoreCase)
       && prescriptionScopeTransport.Requests.Count == 0,
    "A short A/B/C and date follow-up replays the saved unfinished-prescription scope with no model call instead of running product ABC revenue classification.");

var inspectedPrescriptionScope = await prescriptionScopeChat.SendAsync(new AiRequestContext(
    "unfinished-prescription-scope-session", "hangi tarih aralığını kontrol ettin?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(inspectedPrescriptionScope.Message.Contains(prescriptionToday.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR")), StringComparison.Ordinal)
       && inspectedPrescriptionScope.Message.Contains("sonlandırılmamış", StringComparison.Ordinal)
       && inspectedPrescriptionScope.Message.Contains("yerel reçete arşivinde", StringComparison.Ordinal)
       && inspectedPrescriptionScope.WorkflowQueue is { Count: 0 }
       && prescriptionScopeTransport.Requests.Count == 0,
    "A date/filter follow-up reports the exact persisted user-facing scope without exposing workflow diagnostics or calling the model again.");

var ambiguousPrescription = await patientPrescriptionChat.SendAsync(new AiRequestContext(
    "unified-patient-prescriptions-session", "bu reçetelerden bir tanesinin içeriğini göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(ambiguousPrescription.WorkflowQueue is { Count: 0 }
       && ambiguousPrescription.Message.Contains("Hangi reçeteyi", StringComparison.Ordinal)
       && ambiguousPrescription.Message.Contains("Reçeteyi Sohbette Aç", StringComparison.Ordinal)
       && patientPrescriptionTransport.Requests.Count == 0,
    "An unspecified member of a recent prescription list is not replaced with an unrelated empty search.");

var rememberedPatientPrescriptions = await patientPrescriptionChat.SendAsync(new AiRequestContext(
    "unified-patient-prescriptions-session", "Veritabanındaki reçeteleri?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(rememberedPatientPrescriptions.PendingApproval is not null
       && rememberedPatientPrescriptions.WorkflowQueue?.SequenceEqual(
           ["onceki_hasta_bilgisini_getir", "medula_hasta_recetelerini_getir", "tabloda_goster"]) == true
       && rememberedPatientPrescriptions.Message.Contains("EVREN ACAR", StringComparison.Ordinal)
       && patientPrescriptionTransport.Requests.Count == 0,
    "A local-prescription follow-up confirms and reuses the remembered patient with the same unified prescription workflow instead of switching to an unrelated Medula daily list.");

var unsupportedPrescriptionTransport = new ScriptedTransport(true);
var unsupportedPrescriptionChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new MedulaLatestPrescriptionWorkflow(), new MedulaPrescriptionSearchWorkflow(),
        new DisplayTableWorkflow(), new DebugWorkflow()
    ]), unsupportedPrescriptionTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var unsupportedPrescription = await unsupportedPrescriptionChat.SendAsync(new AiRequestContext(
    "unsupported-prescription-session",
    "Meduladaki her bir reçeteyi teker teker her yerini oku ilk Çinceye sonra Japoncaya çevir en son İngilizceye",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(unsupportedPrescription.WorkflowQueue is { Count: 0 }
       && unsupportedPrescription.Message.Contains("gerçekleştirebilen bir iş akışım yok", StringComparison.Ordinal)
       && unsupportedPrescriptionTransport.Requests.Count == 0
       && unsupportedPrescription.UsageByModel is { Count: 0 },
    "Unsupported bulk prescription translation is rejected before routing instead of opening an unrelated latest prescription.");

var wrongLocalHistoryPlan = ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"Özlem Duman'ın geçmiş kullandığı ilaçlar","correction_confidence":0.99,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","hasta_urun_ozeti","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"Özlem Duman"}]},
       {"workflow_name":"hasta_urun_ozeti","arguments":[{"name":"all_history","value":"true"}]},
       {"workflow_name":"tabloda_goster","arguments":[{"name":"title","value":"Geçmiş İlaçlar"}]}
     ]}
    """);
var wrongLocalHistoryTransport = new ScriptedTransport(true, wrongLocalHistoryPlan);
var ambiguityRegistry = new AiWorkflowRegistry([
    new WorkflowBranchSelectionWorkflow(), new PatientMatchWorkflow(), new PatientProductSummaryWorkflow(),
    new MedulaPatientClinicalWorkflow(), new MedulaPatientMedicationListWorkflow(),
    new MedulaLatestPrescriptionWorkflow(), new MedulaPrescriptionSearchWorkflow(),
    new MedulaPrescriptionInformationWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()
]);
var normalizedAmbiguityChat = CreateWorkflowChat(
    ambiguityRegistry, wrongLocalHistoryTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var normalizedAmbiguity = await normalizedAmbiguityChat.SendAsync(new AiRequestContext(
    "normalized-past-medication-session", "Özlem Dumanın geçmiş kullandığı ilaçlar",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(normalizedAmbiguity.PendingSelection is { Options.Count: 4, AllowsMultiple: false }
       && normalizedAmbiguity.PendingSelection.Options.Any(option => option.Id == "patient_medication_list")
       && normalizedAmbiguity.PendingSelection.Options.Any(option => option.Id == "local_sales"),
    "The deterministic normalizer overrides a model's premature local-sales plan for the exact ambiguous past-used-medicines phrase.");

var genericPatientMedicationTransport = new ScriptedTransport(true, wrongLocalHistoryPlan);
var genericPatientMedicationChat = CreateWorkflowChat(
    ambiguityRegistry, genericPatientMedicationTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var genericPatientMedication = await genericPatientMedicationChat.SendAsync(new AiRequestContext(
    "generic-patient-medication-session", "Özlem duman adlı hastanın ilaçlarını getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(genericPatientMedication.PendingSelection is { Options.Count: 4, AllowsMultiple: false }
       && genericPatientMedication.PendingSelection.Options.Any(option => option.Id == "current_prescription")
       && genericPatientMedication.PendingSelection.Options.Any(option => option.Id == "patient_medication_list")
       && genericPatientMedication.PendingSelection.Options.Any(option => option.Id == "local_sales")
       && medulaPatientContextExecutor.AggregatePatientRecordIds.Count == 0,
    "A generic named-patient medication request asks which source the user means and never silently runs pharmacy sales.");

var patientDiscountTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"Özlem Duman'ın eczane satış geçmişindeki ilaçlarını getir","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","hasta_urun_ozeti","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"Özlem Duman"}]},
       {"workflow_name":"hasta_urun_ozeti","arguments":[{"name":"all_history","value":"true"}]},
       {"workflow_name":"tabloda_goster","arguments":[{"name":"title","value":"Özlem Duman — Geçmiş Satışlar"}]}
     ]}
    """));
var patientDiscountChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new PatientMatchWorkflow(), new PatientProductSummaryWorkflow(), new DiscountAnalysisWorkflow(),
        new DisplayTableWorkflow(), new DebugWorkflow()
    ]), patientDiscountTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var patientProducts = await patientDiscountChat.SendAsync(new AiRequestContext(
    "patient-discount-continuation-session", "Özlem Duman'ın eczane satış geçmişindeki ilaçlarını getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var patientDiscount = await patientDiscountChat.SendAsync(new AiRequestContext(
    "patient-discount-continuation-session", "iskonto ile birlikte göster tabloyu",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(patientProducts.Table is { Rows.Count: 1 }
       && patientDiscount.Table is { Rows.Count: 1 }
       && patientDiscount.Table.Columns.Any(column => column.Label == "İndirim Toplamı")
       && patientDiscount.WorkflowQueue?.SequenceEqual(["iskonto_analizi", "tabloda_goster"]) == true
       && medulaPatientContextExecutor.AggregatePatientRecordIds.TakeLast(2).SequenceEqual([8102, 8102])
       && patientDiscountTransport.Requests.Count == 1
       && patientDiscount.UsageByModel is { Count: 0 },
    "Adding discounts to the previous table reuses the exact patient and all-history scope locally, shows the discount column, and makes no second model call.");

var wrongArchivedMedulaPlan = ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"Medula'dan Evren Acar'ın geçmiş kullandığı ilaçları getir","correction_confidence":0.99,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","medula_hasta_klinik_bilgisi_getir","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"Evren Acar"}]},
       {"workflow_name":"medula_hasta_klinik_bilgisi_getir","arguments":[{"name":"operation","value":"medication_history"}]},
       {"workflow_name":"tabloda_goster","arguments":[{"name":"title","value":"Medula İlaç Geçmişi"}]}
     ]}
    """);
var liveMedicationChoiceTransport = new ScriptedTransport(true, wrongArchivedMedulaPlan);
var liveMedicationChoiceChat = CreateWorkflowChat(
    ambiguityRegistry, liveMedicationChoiceTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
// The patient's identity is part of the user request, not invented by a spelling correction in a fresh session.
var liveMedicationChoice = await liveMedicationChoiceChat.SendAsync(new AiRequestContext(
    "live-medication-choice-session", "Medula'dan Evren Acar'ın geçmiş kullandığı ilaçları getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(liveMedicationChoice.PendingSelection is { Options.Count: 2, AllowsMultiple: false }
       && liveMedicationChoice.PendingSelection.Options.Select(option => option.Label).SequenceEqual(
           ["Medula Hasta İlaç Listesi (Canlı)", "Medula İlaç Geçmişi (Senkronize)"]),
    "A named ambiguous Medula medication request distinguishes live versus synchronized data instead of silently choosing the archive.");
var selectedLiveMedicationList = await liveMedicationChoiceChat.ContinueSelectionAsync(
    liveMedicationChoice.PendingSelection!.Id, "user-1", ["patient_medication_list"]);
var approvedLiveMedicationList = selectedLiveMedicationList.PendingApproval is { } liveApproval
    ? await liveMedicationChoiceChat.ApproveAsync(liveApproval.Id, "user-1")
    : selectedLiveMedicationList;
Assert(approvedLiveMedicationList.Table is { Rows.Count: 1 }
       && medulaPatientContextExecutor.MedicationListPatientTcs.Last() == "12289124650"
       && liveMedicationChoiceTransport.Requests.Count == 1,
    "The live branch passes Evren Acar's exact T.C. to the Medula patient-list tool and completes without a second model call; production execution retains its tool-level confirmation gate.");

var ambiguityOptions = JsonSerializer.Serialize(new object[]
{
    new
    {
        id = "current_prescription", label = "Aktif Reçetenin İçeriği",
        queue = new[] { "hasta_eslestir", "medula_son_receteyi_ac" },
        inputs = new Dictionary<string, object>
        {
            ["hasta_eslestir"] = new { query = "ÖZLEM DUMAN" },
            ["medula_son_receteyi_ac"] = new { }
        }
    },
    new
    {
        id = "previous_prescription", label = "Önceki Reçetenin İçeriği",
        queue = new[] { "hasta_eslestir", "medula_receteleri_ara", "medula_recete_bilgisi_getir" },
        inputs = new Dictionary<string, object>
        {
            ["hasta_eslestir"] = new { query = "ÖZLEM DUMAN" },
            ["medula_receteleri_ara"] = new { source = "local", offset = "1", limit = "1" },
            ["medula_recete_bilgisi_getir"] = new { operation = "detail" }
        }
    },
    new
    {
        id = "patient_medication_list", label = "Medula Hasta İlaç Listesi",
        queue = new[] { "hasta_eslestir", "medula_hasta_ilac_listesini_getir", "tabloda_goster" },
        inputs = new Dictionary<string, object>
        {
            ["hasta_eslestir"] = new { query = "ÖZLEM DUMAN" },
            ["medula_hasta_ilac_listesini_getir"] = new { },
            ["tabloda_goster"] = new { title = "Medula Hasta İlaç Listesi" }
        }
    },
    new
    {
        id = "local_sales", label = "Geçmiş Satışlar",
        queue = new[] { "hasta_eslestir", "hasta_urun_ozeti", "tabloda_goster" },
        inputs = new Dictionary<string, object>
        {
            ["hasta_eslestir"] = new { query = "ÖZLEM DUMAN" },
            ["hasta_urun_ozeti"] = new { all_history = "true" },
            ["tabloda_goster"] = new { title = "Özlem Duman — Geçmiş Satışlar" }
        }
    }
});
var ambiguityPlan = JsonSerializer.Serialize(new
{
    answer = (string?)null,
    explosive = false,
    explosion_reason = (string?)null,
    corrected_request = "Özlem Dumanın geçmiş ilaçlarını yap",
    correction_confidence = 0.1,
    medicine_names = Array.Empty<string>(),
    use_remembered_medicine = false,
    workflow_queue = new[] { "is_akisi_dali_sec" },
    workflow_inputs = new[]
    {
        new
        {
            workflow_name = "is_akisi_dali_sec",
            arguments = new[]
            {
                new { name = "question", value = "Özlem Duman için hangi ilaç bilgisi gösterilsin?" },
                new { name = "options_json", value = ambiguityOptions }
            }
        }
    }
});
var ambiguityTransport = new ScriptedTransport(true, ScriptedTransport.Final(ambiguityPlan));
var ambiguityChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new WorkflowBranchSelectionWorkflow(), new PatientMatchWorkflow(), new PatientProductSummaryWorkflow(),
        new MedulaPatientClinicalWorkflow(), new MedulaPatientMedicationListWorkflow(),
        new MedulaLatestPrescriptionWorkflow(), new MedulaPrescriptionSearchWorkflow(),
        new MedulaPrescriptionInformationWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()
    ]), ambiguityTransport, fixedConfiguration, testDirectory, medulaPatientContextExecutor);
var ambiguity = await ambiguityChat.SendAsync(new AiRequestContext(
    "patient-history-ambiguity-session", "Özlem Dumanın geçmiş ilaçlarını yap",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(ambiguity.PendingSelection is { Options.Count: 4, AllowsMultiple: false }
       && ambiguity.PendingSelection.Options.Select(option => option.Label)
           .SequenceEqual(["Aktif Reçetenin İçeriği", "Önceki Reçetenin İçeriği", "Medula Hasta İlaç Listesi", "Geçmiş Satışlar"]),
    "The model-planned ambiguity workflow can present all plausible medication sources as one single-choice menu.");
var localHistoryBranch = await ambiguityChat.ContinueSelectionAsync(
    ambiguity.PendingSelection!.Id, "user-1", ["local_sales"]);
Assert(localHistoryBranch.Table is not null
       && localHistoryBranch.Message.Contains("ÖZLEM DUMAN", StringComparison.Ordinal)
       && ambiguityTransport.Requests.Count == 1,
    "Choosing the local-sales branch runs its saved patient and sales queue without invoking the router again.");
var moduleAliasResult = await new OpenModulesWorkflow().ExecuteAsync(new AiWorkflowContext(
    "ürün kataloğunda aç", "ürün kataloğunda aç", new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["satis_dagilimi_analizi"] = JsonSerializer.SerializeToElement(new
        {
            rows = new[] { new { group_key = "8699536090122", group_label = "Test ürün" } },
            metadata = new { group_by = "product" }
        })
    }, [new AiWorkflowTraceEntry("satis_dagilimi_analizi", "Ürün satış analizi", AiWorkflowStepStatus.Completed, 0, "", JsonSerializer.SerializeToElement(new { }))],
    Parameters: new Dictionary<string, string> { ["module_ids"] = "product_catalog" }));
Assert(moduleAliasResult.Status == AiWorkflowStepStatus.Completed
       && moduleAliasResult.ModuleActions is [{ ModuleId: "products.catalog" }]
       && moduleAliasResult.ModuleActions[0].Parameters["barcodes"] == "8699536090122",
    "Module workflow normalizes router aliases and carries preceding product barcodes into the filtered Catalogue tab.");
var missingWorkflowTool = await new DrugBarcodeMatchWorkflow().ExecuteAsync(new AiWorkflowContext(
    "parol bul", "parol bul", new AiApplicationContext(),
    new Dictionary<string, JsonElement>(), [], MedicineNames: ["parol"],
    RequestContext: Request("parol bul", "home"), ToolExecutor: new TestWorkflowToolExecutor()));
Assert(missingWorkflowTool.Status == AiWorkflowStepStatus.MissingTool
       && missingWorkflowTool.MissingTools?.SequenceEqual(["product.search_by_name"]) == true,
    "A workflow reports the exact missing registered tool instead of silently bypassing its tool queue.");
Assert(discoveredWorkflows.Get("satislari_gor")!.Definition.RequiredTools!.SequenceEqual(["sale.search_records"])
       && discoveredWorkflows.Get("hasta_eslestir")!.Definition.RequiredTools!.SequenceEqual(["sale.search_patients"])
       && discoveredWorkflows.Get("hareketleri_gor")!.Definition.RequiredTools!.SequenceEqual(["qr.search_movements", "qr.list_movement_types"])
       && discoveredWorkflows.Get("hareket_dagilimi_analizi")!.Definition.RequiredTools!.SequenceEqual(["qr.aggregate_movements"])
       && discoveredWorkflows.Get("karekod_tutarlilik_kontrolu")!.Definition.RequiredTools!.SequenceEqual(["qr.audit_movement_chains"])
       && discoveredWorkflows.Get("canli_barkod_fiyati_getir")!.Definition.RequiredTools!.SequenceEqual(["depot.get_live_product_offers"]),
    "Sales and live-depot workflows declare their exact tool contracts.");
var qrAnalysisExecutor = new QrAnalysisToolExecutor();
var aggregateMovement = await new MovementAggregateWorkflow().ExecuteAsync(new AiWorkflowContext(
    "son 14 gün hareket yoğunluğunu saatlere göre göster", "son 14 gün hareket yoğunluğunu saatlere göre göster", new AiApplicationContext(),
    new Dictionary<string, JsonElement>(), [], RequestContext: Request("son 14 gün hareket yoğunluğunu saatlere göre göster", "home"),
    ToolExecutor: qrAnalysisExecutor));
Assert(aggregateMovement.Status == AiWorkflowStepStatus.Completed
       && aggregateMovement.Output.GetProperty("rows").GetArrayLength() == 1
       && qrAnalysisExecutor.AggregateArguments.GetProperty("group_by").GetString() == "hour"
       && !qrAnalysisExecutor.AggregateArguments.TryGetProperty("barcodes", out _),
    "QR aggregation infers an hourly group and omits empty optional arrays from strict tool arguments.");
var multiDimensionMovement = await new MultiDimensionMovementWorkflow().ExecuteAsync(new AiWorkflowContext(
    "depo ve kullanıcıya göre hareket dağılımını göster", "depo ve kullanıcıya göre hareket dağılımını göster", new AiApplicationContext(),
    new Dictionary<string, JsonElement>(), [], RequestContext: Request("depo ve kullanıcıya göre hareket dağılımını göster", "home"),
    ToolExecutor: qrAnalysisExecutor,
    Parameters: new Dictionary<string, string> { ["dimensions"] = "address,user", ["period_days"] = "30" }));
Assert(multiDimensionMovement.Status == AiWorkflowStepStatus.Completed
       && multiDimensionMovement.Output.GetProperty("rows").GetArrayLength() == 2
       && qrAnalysisExecutor.AggregateArgumentCalls.TakeLast(2).Select(arguments => arguments.GetProperty("group_by").GetString())
           .SequenceEqual(new[] { "address", "user" })
       && multiDimensionMovement.Output.GetProperty("rows").EnumerateArray()
           .Select(row => row.GetProperty("analysis_dimension").GetString())
           .SequenceEqual(new[] { "Depo / Adres", "Kullanıcı" }),
    "Multi-dimensional QR analysis executes one deterministic aggregate per requested dimension and labels the combined rows.");
var auditMovement = await new MovementAuditWorkflow().ExecuteAsync(new AiWorkflowContext(
    "son 30 günde tekrarlanan karekod hareketleri", "son 30 günde tekrarlanan karekod hareketleri", new AiApplicationContext(),
    new Dictionary<string, JsonElement>(), [], RequestContext: Request("son 30 günde tekrarlanan karekod hareketleri", "home"),
    ToolExecutor: qrAnalysisExecutor,
    Parameters: new Dictionary<string, string> { ["audit_kind"] = "repeated_qr_movements" }));
Assert(auditMovement.Status == AiWorkflowStepStatus.Completed
       && auditMovement.Output.GetProperty("rows").GetArrayLength() == 1
       && qrAnalysisExecutor.AuditArguments.GetProperty("audit_kind").GetString() == "repeated",
    "QR audit normalizes planner aliases into the registered repeated-movement contract.");
Assert(discoveredWorkflows.Definitions.All(definition => !definition.RequiresRuntimeAi),
    "Every current workflow is deterministic and explicitly requires no runtime AI call.");
var tableSource = JsonSerializer.SerializeToElement(new
{
    rows = new[]
    {
        new { barcode = "111", movement_type = "Satış", medicine_name = "PAROL" },
        new { barcode = "222", movement_type = "Alış", medicine_name = "ARVELES" },
        new { barcode = "333", movement_type = "Satış", medicine_name = "BELOC" },
        new { barcode = "444", movement_type = "Alış", medicine_name = "JARDIANCE" }
    }
});
var tableResult = await new DisplayTableWorkflow().ExecuteAsync(new AiWorkflowContext(
    "tabloda göster", "tabloda göster", new AiApplicationContext(),
    new Dictionary<string, JsonElement> { ["hareketleri_gor"] = tableSource }, [],
    Parameters: new Dictionary<string, string>
    {
        ["title"] = "Hareketler",
        ["color_modes"] = "barcode|Barkoda göre|ocean;movement_type|Hareket türüne göre|mint"
    }));
Assert(tableResult.Table is { Rows.Count: 4, ColorModes.Count: 1 }
       && tableResult.Table.ColorModes[0].Field == "movement_type"
       && tableResult.Table.ColorModes.All(mode => mode.Colors.Count >= 10
            && mode.Colors.Distinct(StringComparer.OrdinalIgnoreCase).Count() == mode.Colors.Count),
    "Table workflow keeps repeated categorical colors but removes effectively unique barcode colors.");
var tableViewModel = new WorkflowTableViewModel(tableResult.Table!);
tableViewModel.SearchText = "ARVELES";
Assert(tableViewModel.Rows.Count == 1 && tableViewModel.RowCountText.Contains("1 / 4", StringComparison.Ordinal),
    "Workflow table search filters all visible columns locally without another model call.");
var groupedProductTable = await new DisplayTableWorkflow().ExecuteAsync(new AiWorkflowContext(
    "karşılaştır", "karşılaştır", new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["satis_dagilimi_analizi"] = JsonSerializer.SerializeToElement(new
        {
            rows = new[] { new { group_label = "PAROL", quantity = 2 }, new { group_label = "BELOC", quantity = 1 } },
            metadata = new { group_by = "product", visible_columns = new[] { "group_label", "quantity" } }
        })
    }, []));
Assert(groupedProductTable.Table!.Columns.Single(column => column.Key == "group_label").Label == "İlaç"
       && groupedProductTable.Table.ColorModes.Count == 0,
    "Product aggregation tables label the group as medicine and default to no color when every medicine is unique.");
var latestSalesExecutor = new LatestSalesPresentationToolExecutor();
var latestSalesTransport = new ScriptedTransport(true);
var latestSalesChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new SeeSalesWorkflow(), new DisplayTableWorkflow(), new InformationCardsWorkflow(), new DebugWorkflow()
    ]), latestSalesTransport, fixedConfiguration, testDirectory, latestSalesExecutor);
var latestSales = await latestSalesChat.SendAsync(new AiRequestContext(
    "latest-sales-presentation-session", "son 10 satış",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(latestSales.Table is { Rows.Count: 10 }
       && latestSales.Content is null
       && latestSales.WorkflowQueue?.SequenceEqual(["satislari_gor", "tabloda_goster"]) == true
       && latestSales.Table.Columns.Single(column => column.Key == "reference_code").IsVisibleByDefault == false
       && latestSales.Table.Columns.Single(column => column.Key == "timestamp").Label == "İşlem Tarihi"
       && latestSales.Table.Columns.Single(column => column.Key == "transaction_gross_amount").IsVisibleByDefault
       && latestSales.Table.Columns.Single(column => column.Key == "discount_amount").IsVisibleByDefault
       && latestSales.Table.Columns.Single(column => column.Key == "transaction_net_amount").IsVisibleByDefault
       && latestSales.Table.ColorModes.Any(mode => mode.Field == "sale_type")
       && latestSales.ModuleActions?.Any(action => action.ModuleId == "sales.retail-list") == true
       && latestSalesTransport.Requests.Count == 0,
    "A simple latest-sales request reuses the shared filtered workflow, forces the specialized table, hides internal references, retains price columns and sale-type colors, and avoids a router call.");
var latestSalesView = new WorkflowTableViewModel(latestSales.Table!);
var latestSalesRow = latestSalesView.Rows.Cast<DataRowView>().First();
Assert(latestSalesView.InteractionKind == WorkflowRowInteractionKind.Sale
       && latestSalesView.ChatOpenLabel == "Satışı/İadeyi Sohbette Aç"
       && Convert.ToString(latestSalesRow["timestamp"], CultureInfo.InvariantCulture) == "04.09.2026 18:52"
       && Convert.ToString(latestSalesRow["transaction_net_amount"], CultureInfo.InvariantCulture)?.EndsWith(" ₺", StringComparison.Ordinal) == true,
    "Latest-sale rows keep the sale interaction contract and present human-readable dates and Turkish currency values.");
var latestDiscountedSales = await latestSalesChat.SendAsync(new AiRequestContext(
    "latest-discounted-sales-session", "son 10 iskontolu satış",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(latestDiscountedSales.Table is { Rows.Count: 10 }
       && latestDiscountedSales.WorkflowQueue?.SequenceEqual(["satislari_gor", "tabloda_goster"]) == true
       && latestDiscountedSales.Message.Contains("10 iskontolu satış", StringComparison.Ordinal)
       && latestDiscountedSales.Message.Contains("Her satır tek bir satış işlemini temsil eder", StringComparison.Ordinal)
       && latestDiscountedSales.Table.Rows.All(row => decimal.Parse(row["discount_amount"], CultureInfo.InvariantCulture) > 0)
       && latestSalesExecutor.Arguments.Last().GetProperty("payment_types")[0].GetString() == "discount"
       && latestSalesTransport.Requests.Count == 0,
    "The latest discounted-sales phrase reuses transaction-level sale.search_records filtering and never becomes a product aggregation or router call.");
var discountedSalesToolCalls = latestSalesExecutor.Arguments.Count;
var discountedSalesTotal = await latestSalesChat.SendAsync(new AiRequestContext(
    "latest-discounted-sales-session", "bunlarda toplam ne kadar iskonto oldu?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(discountedSalesTotal.Message.Contains("1.872,86 ₺", StringComparison.Ordinal)
       && discountedSalesTotal.Content is { Kind: AiWorkflowContentKind.MetricCards, Items.Count: 1 }
       && discountedSalesTotal.Content.Items[0].Title == "Toplam İskonto"
       && discountedSalesTotal.Content.Items[0].Value == "1.872,86 ₺"
       && discountedSalesTotal.WorkflowQueue?.Count == 0
       && discountedSalesTotal.ModelUsed is null
       && discountedSalesTotal.UsageByModel?.Count == 0
       && latestSalesExecutor.Arguments.Count == discountedSalesToolCalls
       && latestSalesTransport.Requests.Count == 0,
    "A metric follow-up over the displayed discounted-sale rows is calculated locally and does not rerun a workflow, query the database, or call a model.");
var discountedSalesMaximum = await latestSalesChat.SendAsync(new AiRequestContext(
    "latest-discounted-sales-session", "bu satışlardaki en yüksek indirim neydi?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(discountedSalesMaximum.Message.Contains("1.097,63 ₺", StringComparison.Ordinal)
       && latestSalesExecutor.Arguments.Count == discountedSalesToolCalls
       && latestSalesTransport.Requests.Count == 0,
    "Maximum-value follow-ups also use the retained table rows without external work.");
var discountedFollowUpSession = "discounted-sales-follow-up-session";
var unfilteredLatestSales = await latestSalesChat.SendAsync(new AiRequestContext(
    discountedFollowUpSession, "en son 10 satışı göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var discountedFollowUp = await latestSalesChat.SendAsync(new AiRequestContext(
    discountedFollowUpSession, "indirimli olan satışları aç",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(unfilteredLatestSales.Table is { Rows.Count: 10 }
       && discountedFollowUp.Table is { Rows.Count: 10 }
       && discountedFollowUp.WorkflowQueue?.SequenceEqual(["satislari_gor", "tabloda_goster"]) == true
       && discountedFollowUp.Message.Contains("10 iskontolu satış", StringComparison.Ordinal)
       && discountedFollowUp.Table.Columns.Any(column => column is { Key: "discount_amount", IsVisibleByDefault: true })
       && discountedFollowUp.Table.Rows.All(row => decimal.Parse(row["discount_amount"], CultureInfo.InvariantCulture) > 0)
       && discountedFollowUp.ModuleActions?.Single(action => action.ModuleId == "sales.retail-list")
           .Parameters["payment_types"] == "discount"
       && latestSalesExecutor.Arguments.Last().GetProperty("payment_types")[0].GetString() == "discount"
       && latestSalesTransport.Requests.Count == 0,
    "After a latest-sales table, 'indirimli olan satışları aç' keeps transaction rows, reuses the latest-ten scope, and carries the discount filter into the Sales-module action without a model call.");
var previousSalesContentsExecutor = new PreviousSalesContentsToolExecutor();
var previousSalesContentsTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":"Bu listedeki XANAX ilacını alan hasta GÖNÜL ÇUBUKLU.","explosive":false,
     "explosion_reason":null,"corrected_request":"bu listedeki xanax alan kişi kim?","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,"workflow_queue":[],"workflow_inputs":[]}
    """));
var previousSalesContentsChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new SeeSalesWorkflow(), new PreviousSalesContentsWorkflow(), new PatientMatchWorkflow(),
        new MedulaPatientClinicalWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()
    ]), previousSalesContentsTransport, fixedConfiguration, testDirectory, previousSalesContentsExecutor);
var latestPrescriptionSales = await previousSalesContentsChat.SendAsync(new AiRequestContext(
    "previous-sales-contents-session", "son 15 reçeteli satış",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var previousSaleMedicines = await previousSalesContentsChat.SendAsync(new AiRequestContext(
    "previous-sales-contents-session", "bunlardaki ilaçları listele",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(latestPrescriptionSales.Table is { Rows.Count: 15 }
       && latestPrescriptionSales.Table.Rows.All(row => row["sale_type"] == "prescription")
       && previousSaleMedicines.Table is { Rows.Count: 15 }
       && previousSaleMedicines.WorkflowQueue?.SequenceEqual(["onceki_satislarin_iceriklerini_gor", "tabloda_goster"]) == true
       && previousSaleMedicines.Message.Contains("15 satıştan 15 ilaç satırı", StringComparison.Ordinal)
       && previousSaleMedicines.Table.Columns.Single(column => column.Key == "medicine_name").Label == "İlaç"
       && previousSaleMedicines.Table.Rows.Select(row => row["reference_code"])
           .SequenceEqual(previousSalesContentsExecutor.DetailReferences)
       && previousSalesContentsExecutor.DetailReferences.Count == 15
       && previousSalesContentsTransport.Requests.Count == 0,
    "A medicine-content follow-up expands the exact 15 retained prescription-sale references, uses no patient match, and displays their product lines without a router-model call.");
var relatedPatientAnswer = await previousSalesContentsChat.SendAsync(new AiRequestContext(
    "previous-sales-contents-session", "bu listedeki xanax alan kişi kim?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var relationshipRouterPayload = previousSalesContentsTransport.Requests.Single().InputItems.Last().GetRawText();
Assert(relatedPatientAnswer.Message.Contains("GÖNÜL ÇUBUKLU", StringComparison.Ordinal)
       && relationshipRouterPayload.Contains("recent_workflow_context", StringComparison.Ordinal)
       && relationshipRouterPayload.Contains("patient_name", StringComparison.Ordinal)
       && relationshipRouterPayload.Contains("XANAX", StringComparison.Ordinal),
    "The bounded router payload includes displayed patient and medicine columns, allowing direct AI answers about relationships in the previous table.");
var relatedPatientReports = await previousSalesContentsChat.SendAsync(new AiRequestContext(
    "previous-sales-contents-session", "bu listedeki xanax alan hastanın raporlarını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(relatedPatientReports.Table is { Rows.Count: 1 }
       && relatedPatientReports.WorkflowQueue?.Take(2).SequenceEqual(["hasta_eslestir", "medula_hasta_klinik_bilgisi_getir"]) == true
       && previousSalesContentsExecutor.PatientQueries.SequenceEqual(["GÖNÜL ÇUBUKLU"])
       && previousSalesContentsExecutor.ReportPatientTcs.SequenceEqual(["17752783112"])
       && previousSalesContentsTransport.Requests.Count == 1,
    "A relational follow-up resolves XANAX to the unique patient from the preceding table and passes that exact patient to the report workflow without treating descriptive words as a name or calling the router.");
var deterministicRefunds = await new LatestRefundsWorkflow().ExecuteAsync(new AiWorkflowContext(
    "en son yapılan 10 iadeyi göster", "en son yapılan 10 iadeyi göster", new AiApplicationContext(),
    new Dictionary<string, JsonElement>(), [], RequestContext: Request("en son yapılan 10 iadeyi göster", "home"),
    ToolExecutor: new LatestRefundToolExecutor()));
var deterministicRefundRows = deterministicRefunds.Output.GetProperty("rows").EnumerateArray().ToArray();
Assert(deterministicRefunds.Status == AiWorkflowStepStatus.Completed
       && deterministicRefundRows.Length == 2
       && deterministicRefundRows.All(row => row.GetProperty("record_type").GetString() == "refund")
       && deterministicRefundRows.Select(row => row.GetProperty("reference_code").GetString()).SequenceEqual(["G2", "G1"]),
    "Latest-refunds workflow skips interleaved sales and preserves newest-first refund order.");
var emptyTableSource = JsonSerializer.SerializeToElement(new
{
    rows = Array.Empty<object>(),
    metadata = new
    {
        total_count = 0,
        columns = new[] { "timestamp", "reference_code", "record_id" },
        visible_columns = new[] { "timestamp", "reference_code" }
    }
});
var emptyTableResult = await new DisplayTableWorkflow().ExecuteAsync(new AiWorkflowContext(
    "boş satışları göster", "boş satışları göster", new AiApplicationContext(),
    new Dictionary<string, JsonElement> { ["satislari_gor"] = emptyTableSource }, [],
    Parameters: new Dictionary<string, string> { ["title"] = "Satış Kayıtları" }));
Assert(emptyTableResult.Table is null
       && emptyTableResult.Message == "Filtrelere uyan kayıt bulunamadı.",
    "Empty workflow results explain that no record exists without rendering an empty table or card.");
var oneRowPresentation = new AiWorkflowTablePresentation(
    "Satış Özeti",
    [
        new AiWorkflowTableColumn("medicine_name", "İlaç"),
        new AiWorkflowTableColumn("product_quantity", "Ürün Adedi"),
        new AiWorkflowTableColumn("net_revenue", "Net Hasılat")
    ],
    [new Dictionary<string, string>
    {
        ["medicine_name"] = "PAROL 500 MG 20 TABLET",
        ["product_quantity"] = "38",
        ["net_revenue"] = "2833.36"
    }], [], string.Empty, 1, "product_quantity");
var oneRowMessage = new ChatMessageViewModel(false, "Özet", "İş Akışı");
oneRowMessage.SetTable(oneRowPresentation);
Assert(oneRowMessage.WorkflowContentVisibility == System.Windows.Visibility.Visible
       && oneRowMessage.TableVisibility == System.Windows.Visibility.Collapsed
       && oneRowMessage.ResultViewOptionsVisibility == System.Windows.Visibility.Visible
       && oneRowMessage.WorkflowContent!.Items.Single().Value == "Ürün Adedi: 38"
       && oneRowMessage.WorkflowContent.Items.Single().Detail!.Contains("Net Hasılat", StringComparison.Ordinal),
    "A one-row table defaults to a locally derived Turkish card and emphasizes the router-planned field.");
oneRowMessage.ShowTableView();
Assert(oneRowMessage.TableVisibility == System.Windows.Visibility.Visible
       && oneRowMessage.WorkflowContentVisibility == System.Windows.Visibility.Collapsed,
    "A one-row result can switch from its default card to table view without another model call.");
var twoRowMessage = new ChatMessageViewModel(false, "Kayıtlar", "İş Akışı");
twoRowMessage.SetTable(new AiWorkflowTablePresentation(
    "Satışlar", [new AiWorkflowTableColumn("reference_code", "Referans")],
    [
        new Dictionary<string, string> { ["reference_code"] = "P1" },
        new Dictionary<string, string> { ["reference_code"] = "P2" }
    ], [], string.Empty));
Assert(twoRowMessage.TableVisibility == System.Windows.Visibility.Visible
       && twoRowMessage.WorkflowContentVisibility == System.Windows.Visibility.Collapsed
       && twoRowMessage.ResultViewOptionsVisibility == System.Windows.Visibility.Visible,
    "A multi-row result defaults to table view while retaining a local card alternative.");
twoRowMessage.ShowCardView();
Assert(twoRowMessage.WorkflowContentVisibility == System.Windows.Visibility.Visible,
    "A multi-row table can switch to cards without another model call.");
var prescriptionInteraction = new WorkflowTableViewModel(new AiWorkflowTablePresentation(
    "Reçeteler",
    [
        new AiWorkflowTableColumn("prescription_number", "Reçete Numarası"),
        new AiWorkflowTableColumn("patient_tc", "Hasta T.C.")
    ],
    [new Dictionary<string, string>
    {
        ["prescription_number"] = "3JVH4EW",
        ["patient_tc"] = "12289124650"
    }], [], string.Empty));
var prescriptionRow = prescriptionInteraction.Rows.Cast<DataRowView>().Single();
Assert(prescriptionInteraction.InteractionKind == WorkflowRowInteractionKind.Prescription
       && prescriptionInteraction.OpenLabel == "Reçeteyi Aç"
       && prescriptionInteraction.ChatOpenLabel == "Reçeteyi Sohbette Aç"
       && prescriptionInteraction.InteractionKey(prescriptionRow) == "3JVH4EW",
    "Prescription tables expose the domain-specific open and in-chat actions.");
prescriptionInteraction.BeginDetailLoad(prescriptionRow, prescriptionInteraction.LoadingDetailText);
prescriptionInteraction.SetDetail(new AiWorkflowTablePresentation(
    "3JVH4EW · Reçete İçeriği",
    [new AiWorkflowTableColumn("medicine_name", "İlaç")],
    [new Dictionary<string, string> { ["medicine_name"] = "PAROL" }],
    [], string.Empty));
prescriptionInteraction.EndDetailLoad();
Assert(prescriptionInteraction.DetailVisibility == System.Windows.Visibility.Visible
       && prescriptionInteraction.DetailGridVisibility == System.Windows.Visibility.Visible
       && prescriptionInteraction.InlineDetail?.Rows.Count == 1,
    "Selecting an interactive prescription row can expose its medicine subtable below the result.");
var saleInteraction = new WorkflowTableViewModel(new AiWorkflowTablePresentation(
    "Satışlar",
    [new AiWorkflowTableColumn("reference_code", "Referans"), new AiWorkflowTableColumn("sale_type", "Satış Türü")],
    [new Dictionary<string, string> { ["reference_code"] = "P56100", ["sale_type"] = "prescription" }],
    [], string.Empty));
var productInteraction = new WorkflowTableViewModel(new AiWorkflowTablePresentation(
    "Ürünler",
    [new AiWorkflowTableColumn("barcode", "Barkod"), new AiWorkflowTableColumn("medicine_name", "İlaç")],
    [new Dictionary<string, string> { ["barcode"] = "8699717010109", ["medicine_name"] = "PAROL" }],
    [], string.Empty));
var qrInteraction = new WorkflowTableViewModel(new AiWorkflowTablePresentation(
    "Hareketler",
    [new AiWorkflowTableColumn("qr_code", "Karekod"), new AiWorkflowTableColumn("movement_type", "Hareket Türü")],
    [new Dictionary<string, string> { ["qr_code"] = "0108699717010109", ["movement_type"] = "Satış" }],
    [], string.Empty));
Assert(saleInteraction.InteractionKind == WorkflowRowInteractionKind.Sale
       && productInteraction.InteractionKind == WorkflowRowInteractionKind.Product
       && qrInteraction.InteractionKind == WorkflowRowInteractionKind.QrMovement,
    "Sales, product, and QR result tables opt into the shared row-interaction infrastructure.");
var selectionMessage = new ChatMessageViewModel(false, "Ürün seçin.");
selectionMessage.SetWorkflowSelection(new AiWorkflowSelectionView(
    "selection-ui-test", "Ürün seçin.",
    [
        new AiWorkflowSelectionOption("active", "PAROL AKTİF", JsonSerializer.SerializeToElement(new { barcode = "111", name = "PAROL AKTİF", is_on_market = true })),
        new AiWorkflowSelectionOption("passive", "PAROL PASİF", JsonSerializer.SerializeToElement(new { barcode = "222", name = "PAROL PASİF", is_on_market = false }))
    ], true, DateTimeOffset.UtcNow.AddMinutes(5)));
Assert(selectionMessage.WorkflowSelectionView.Cast<object>().Count() == 1,
    "Passive medicine choices are hidden by default.");
selectionMessage.SelectAllWorkflowOptions();
Assert(selectionMessage.WorkflowSelectionOptions.Single(option => option.Id == "active").IsSelected
       && !selectionMessage.WorkflowSelectionOptions.Single(option => option.Id == "passive").IsSelected,
    "Select all selects only the medicine choices currently shown.");
selectionMessage.TogglePassiveWorkflowOptions();
selectionMessage.WorkflowSelectionSearchText = "222";
Assert(selectionMessage.WorkflowSelectionView.Cast<object>().Count() == 1
       && selectionMessage.WorkflowSelectionView.Cast<WorkflowSelectionOptionViewModel>().Single().Id == "passive",
    "Medicine selection search filters the expanded active/passive view by name or barcode.");

var movementExecutor = new CapturingMovementToolExecutor();
var relativeMovement = await new SeeMovementsWorkflow().ExecuteAsync(new AiWorkflowContext(
    "son 5 ay", "son 5 ay", new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    RequestContext: Request("son 5 ay", "home"), ToolExecutor: movementExecutor,
    Parameters: new Dictionary<string, string> { ["period_months"] = "5" }));
var relativeArguments = movementExecutor.LastArguments;
var expectedToday = DateOnly.FromDateTime(DateTime.Today);
Assert(relativeMovement.Status == AiWorkflowStepStatus.Completed
       && relativeArguments.GetProperty("start_date").GetString() == expectedToday.AddMonths(-5).ToString("yyyy-MM-dd")
       && relativeArguments.GetProperty("end_date").GetString() == expectedToday.ToString("yyyy-MM-dd")
       && relativeMovement.ModuleActions is [{ ModuleId: "products.qr-movements" }]
       && relativeMovement.ModuleActions[0].Parameters["start_date"] == expectedToday.AddMonths(-5).ToString("yyyy-MM-dd"),
    "Relative movement periods are calculated locally from the authoritative application date.");
movementExecutor.Reset();
await new SeeMovementsWorkflow().ExecuteAsync(new AiWorkflowContext(
    "en yakın hareket", "en yakın hareket", new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    RequestContext: Request("en yakın hareket", "home"), ToolExecutor: movementExecutor,
    Parameters: new Dictionary<string, string> { ["nearest"] = "true" }));
Assert(movementExecutor.LastArguments.GetProperty("start_date").GetString() == "2000-01-01"
       && movementExecutor.LastArguments.GetProperty("limit").GetInt32() == 1,
    "Nearest movement searches full supported history with a deterministic one-record limit.");
var flakyMovementExecutor = new FlakyMovementToolExecutor();
var retriedMovement = await new SeeMovementsWorkflow().ExecuteAsync(new AiWorkflowContext(
    "son 30 gün", "son 30 gün", new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["ilac_barkod_eslestir"] = JsonSerializer.SerializeToElement(new { barcodes = new[] { "111", "222" } })
    }, [], RequestContext: Request("son 30 gün", "home"), ToolExecutor: flakyMovementExecutor,
    Parameters: new Dictionary<string, string> { ["period_days"] = "30" }));
Assert(retriedMovement.Status == AiWorkflowStepStatus.Completed
       && flakyMovementExecutor.ExecutionCount == 2
       && flakyMovementExecutor.LastArguments.GetProperty("barcodes").GetArrayLength() == 2,
    "The read-only movement workflow sends all selected barcodes in one query and retries one transient execution failure.");
var pagedMovement = await new SeeMovementsWorkflow().ExecuteAsync(new AiWorkflowContext(
    "son 30 gün hareketleri", "son 30 gün hareketleri", new AiApplicationContext(),
    new Dictionary<string, JsonElement>(), [], RequestContext: Request("son 30 gün hareketleri", "home"),
    ToolExecutor: new PagedMovementToolExecutor(),
    Parameters: new Dictionary<string, string> { ["period_days"] = "30" }));
Assert(pagedMovement.Status == AiWorkflowStepStatus.Completed
       && pagedMovement.Message.Contains("Toplam 314 hareket", StringComparison.Ordinal)
       && pagedMovement.Message.Contains("ilk 100 kayıt", StringComparison.Ordinal)
       && pagedMovement.Output.GetProperty("metadata").GetProperty("total_count").GetInt32() == 314,
    "A capped movement page reports the authoritative total instead of presenting 100 as the total.");
var describedMovementExecutor = new DescribedMovementTypeToolExecutor();
var describedMovement = await new SeeMovementsWorkflow().ExecuteAsync(new AiWorkflowContext(
    "parol son 20 gün depo giriş hareketleri", "parol son 20 gün depo giriş hareketleri",
    new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["ilac_barkod_eslestir"] = JsonSerializer.SerializeToElement(new { barcodes = new[] { "PAROL20" } })
    }, [], RequestContext: Request("parol son 20 gün depo giriş hareketleri", "home"),
    ToolExecutor: describedMovementExecutor,
    Parameters: new Dictionary<string, string>
    {
        ["period_days"] = "20", ["movement_types"] = "Depo Giriş Hareketleri",
        ["direction"] = "entry", ["address_type"] = "depot"
    }));
Assert(describedMovement.Status == AiWorkflowStepStatus.Completed
       && describedMovementExecutor.SearchArguments.GetProperty("movement_type_codes")[0].GetString() == "DEPOT_IN"
       && describedMovementExecutor.SearchArguments.GetProperty("direction").GetString() == "entry"
       && describedMovementExecutor.SearchArguments.GetProperty("address_type").GetString() == "depot",
    "A descriptive depot-entry phrase resolves against configured movement display names and preserves structured direction/address filters.");

var moduleWorkspace = await new OpenModulesWorkflow().ExecuteAsync(new AiWorkflowContext(
    "ürün kataloğu ve karekod hareketlerini yan yana aç",
    "ürün kataloğu ve karekod hareketlerini yan yana aç",
    new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    Parameters: new Dictionary<string, string>
    {
        ["module_ids"] = "products.catalog,products.qr-movements",
        ["layout"] = "side_by_side", ["tab_title"] = "Ürün ve Hareketler"
    }));
Assert(moduleWorkspace.Status == AiWorkflowStepStatus.Completed
       && moduleWorkspace.ModuleActions is [{ ModuleId: "workspace.modules" }]
       && moduleWorkspace.ModuleActions[0].Parameters["module_ids"] == "products.catalog,products.qr-movements"
       && moduleWorkspace.ModuleActions[0].Parameters["layout"] == "side_by_side",
    "The curated module workflow emits one application-owned action for two modules side-by-side in a new tab.");

var salesExecutor = new CapturingSalesToolExecutor();
var revenueResult = await new SalesRevenueSummaryWorkflow().ExecuteAsync(new AiWorkflowContext(
    "parolun son 30 gunluk satis hasilati", "parolun son 30 gunluk satis hasilati",
    new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["ilac_barkod_eslestir"] = JsonSerializer.SerializeToElement(new { barcodes = new[] { "8690000000001", "8690000000002" } })
    },
    [],
    RequestContext: Request("parolun son 30 gunluk satis hasilati", "home"), ToolExecutor: salesExecutor,
    Parameters: new Dictionary<string, string>
    {
        ["period_days"] = "30", ["sale_types"] = "retail", ["include_refunds"] = "false"
    }));
Assert(revenueResult.Status == AiWorkflowStepStatus.Completed
       && salesExecutor.LastToolName == "sale.aggregate_records"
       && salesExecutor.LastArguments.GetProperty("barcodes").GetArrayLength() == 2
       && salesExecutor.LastArguments.GetProperty("sale_types")[0].GetString() == "retail"
       && !salesExecutor.LastArguments.GetProperty("include_refunds").GetBoolean()
       && revenueResult.Output.GetProperty("rows").GetArrayLength() == 1
       && revenueResult.Output.GetProperty("rows")[0].GetProperty("product_quantity").GetString() == "3"
       && revenueResult.Message.Contains("seçili ürün satırları: 3 kutu", StringComparison.Ordinal)
       && !revenueResult.Output.GetProperty("metadata").GetProperty("visible_columns").EnumerateArray()
           .Any(value => value.GetString() == "refund_total")
       && !revenueResult.Message.Contains("İade", StringComparison.Ordinal)
       && revenueResult.ModuleActions is [{ ModuleId: "sales.retail-list" }],
    "Sales revenue workflow aggregates the selected barcodes with router-planned filters and emits a filtered Sales tab action.");

var quantityRevenueResult = await new SalesRevenueSummaryWorkflow().ExecuteAsync(new AiWorkflowContext(
    "parol son 50 günde kaç tane sattım", "parol son 50 günde kaç tane sattım",
    new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["ilac_barkod_eslestir"] = JsonSerializer.SerializeToElement(new { barcodes = new[] { "8690000000001" } })
    }, [], RequestContext: Request("parol son 50 günde kaç tane sattım", "home"), ToolExecutor: salesExecutor,
    Parameters: new Dictionary<string, string> { ["period_days"] = "50", ["metric"] = "quantity" }));
Assert(quantityRevenueResult.Status == AiWorkflowStepStatus.Completed
       && quantityRevenueResult.Message.StartsWith("Ürün Adedi: 3 kutu", StringComparison.Ordinal)
       && quantityRevenueResult.Output.GetProperty("metadata").GetProperty("highlight_field").GetString() == "product_quantity",
    "A box-count question makes product quantity the preplanned headline and presentation highlight.");

var institutionRevenueResult = await new SalesRevenueSummaryWorkflow().ExecuteAsync(new AiWorkflowContext(
    "temmuz ayında reçeteli satışların kamu ücretleri hasılatı",
    "temmuz ayında reçeteli satışların kamu ücretleri hasılatı",
    new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    RequestContext: Request("temmuz ayında reçeteli satışların kamu ücretleri hasılatı", "home"),
    ToolExecutor: salesExecutor,
    Parameters: new Dictionary<string, string>
    {
        ["start_date"] = "2026-07-01", ["end_date"] = "2026-07-31",
        ["sale_types"] = "reçeteli", ["metric"] = "institution_revenue"
    }));
Assert(institutionRevenueResult.Status == AiWorkflowStepStatus.Completed
       && salesExecutor.LastArguments.GetProperty("sale_types")[0].GetString() == "prescription"
       && salesExecutor.LastArguments.GetProperty("include_refunds").GetBoolean()
       && institutionRevenueResult.Message.Contains("kamu ücreti hasılatı: 115", StringComparison.Ordinal)
       && decimal.Parse(institutionRevenueResult.Output.GetProperty("rows")[0].GetProperty("institution_revenue").GetString()!, CultureInfo.InvariantCulture) == 115m,
    "Public-fee revenue is an explicit institution metric and Turkish prescription filters are normalized locally.");

var retailRevenueWithRefund = await new SalesRevenueSummaryWorkflow().ExecuteAsync(new AiWorkflowContext(
    "perakende satış hasılatı", "perakende satış hasılatı", new AiApplicationContext(),
    new Dictionary<string, JsonElement>(), [], RequestContext: Request("perakende satış hasılatı", "home"),
    ToolExecutor: salesExecutor,
    Parameters: new Dictionary<string, string> { ["sale_types"] = "retail" }));
Assert(salesExecutor.LastArguments.GetProperty("include_refunds").GetBoolean()
       && retailRevenueWithRefund.Output.GetProperty("rows")[0].GetProperty("refund_total").GetString() == "40"
       && retailRevenueWithRefund.Output.GetProperty("metadata").GetProperty("visible_columns").EnumerateArray()
           .Any(value => value.GetString() == "refund_total")
       && retailRevenueWithRefund.Message.Contains("perakende satışlar", StringComparison.Ordinal)
       && retailRevenueWithRefund.Message.Contains("İade 40", StringComparison.Ordinal),
    "Retail revenue includes matching refunds by default and displays the real refund total instead of a filtered-out zero.");

var retailContextExecutor = new CapturingSalesToolExecutor();
var retailContextTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"geçen ay toplam kaç tl perakende satış?","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["satis_hasilati_ozeti","metrik_kartlari_goster"],
     "workflow_inputs":[
       {"workflow_name":"satis_hasilati_ozeti","arguments":[{"name":"period_months","value":"1"}]},
       {"workflow_name":"metrik_kartlari_goster","arguments":[{"name":"title","value":"Satış Hasılatı"}]}
     ]}
    """));
var retailContextChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new SalesRevenueSummaryWorkflow(), new MetricCardsWorkflow(), new DebugWorkflow()]),
    retailContextTransport, new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true, RouterModel = "gpt-5.6-luna", RouterReasoningEffort = "none" }), testDirectory, retailContextExecutor);
var retailContextSession = "retail-context-session";
var retailContextResult = await retailContextChat.SendAsync(new AiRequestContext(
    retailContextSession, "geçen ay toplam kaç tl perakende satış?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var previousMonthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
var previousMonthEnd = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
var retailScopeFollowUp = await retailContextChat.SendAsync(new AiRequestContext(
    retailContextSession, "bunun içerisinde reçeteli satışlar var mı?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(retailContextExecutor.LastArguments.GetProperty("start_date").GetString() == previousMonthStart.ToString("yyyy-MM-dd")
       && retailContextExecutor.LastArguments.GetProperty("end_date").GetString() == previousMonthEnd.ToString("yyyy-MM-dd")
       && retailContextExecutor.LastArguments.GetProperty("sale_types")[0].GetString() == "retail"
       && retailContextResult.Message.Contains("perakende satışlar", StringComparison.Ordinal)
       && retailScopeFollowUp.Message.StartsWith("Hayır.", StringComparison.Ordinal)
       && retailScopeFollowUp.Message.Contains("reçeteli satışlar sonuca dahil değildi", StringComparison.Ordinal)
       && retailContextTransport.Requests.Count == 1
       && retailScopeFollowUp.UsageByModel is { Count: 0 },
    "Previous calendar month retail revenue stays retail-only, and a scope follow-up is answered from saved filters without an unrelated workflow or model call.");

var prescriptionReportExecutor = new PrescriptionReportToolExecutor();
var prescriptionReportTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"bu ay kaç tane raporlu reçetemiz var","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["recete_rapor_durumu_ozeti","metrik_kartlari_goster"],
     "workflow_inputs":[{"workflow_name":"recete_rapor_durumu_ozeti","arguments":[{"name":"report_status","value":"reported"}]}]}
    """));
var prescriptionReportChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new PrescriptionReportStatusSummaryWorkflow(), new MetricCardsWorkflow(), new DebugWorkflow()]),
    prescriptionReportTransport, new FixedAiConfigurationProvider(new AiConfiguration { DebugMode = true, RouterModel = "gpt-5.6-luna", RouterReasoningEffort = "none" }), testDirectory, prescriptionReportExecutor);
var prescriptionReportSession = "prescription-report-session";
var reportedPrescriptionResult = await prescriptionReportChat.SendAsync(new AiRequestContext(
    prescriptionReportSession, "bu ay kaç tane raporlu reçetemiz var",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var unreportedPrescriptionResult = await prescriptionReportChat.SendAsync(new AiRequestContext(
    prescriptionReportSession, "kaç tane raporsuz reçete var?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(reportedPrescriptionResult.Message.Contains("2 raporlu reçete", StringComparison.Ordinal)
       && reportedPrescriptionResult.Content is { Kind: AiWorkflowContentKind.MetricCards }
       && unreportedPrescriptionResult.Message.Contains("1 raporsuz reçete", StringComparison.Ordinal)
       && prescriptionReportExecutor.Arguments.Count == 2
       && prescriptionReportExecutor.Arguments.All(arguments =>
           arguments.GetProperty("start_date").GetString() == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyy-MM-dd")
           && arguments.GetProperty("end_date").GetString() == DateTime.Today.ToString("yyyy-MM-dd"))
       && prescriptionReportTransport.Requests.Count == 1
       && unreportedPrescriptionResult.UsageByModel is { Count: 0 },
    "Raporlu and raporsuz prescription counts use stored medical-report markers, never medicine matching or stock analysis, and preserve the prior month-to-date period.");
var patientExecutor = new PatientMatchingToolExecutor();
var patientMatch = await new PatientMatchWorkflow().ExecuteAsync(new AiWorkflowContext(
    "evren acar hastasının son bir yılki tüm satışlarını göster",
    "evren acar hastasının son bir yılki tüm satışlarını göster",
    new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    RequestContext: Request("evren acar hastasının son bir yılki tüm satışlarını göster", "home"),
    ToolExecutor: patientExecutor,
    Parameters: new Dictionary<string, string> { ["query"] = "evren acar" }));
var patientSalesExecutor = new ProductSaleSearchToolExecutor();
var patientSales = await new SeeSalesWorkflow().ExecuteAsync(new AiWorkflowContext(
    "evren acar hastasının son bir yılki tüm satışlarını göster",
    "evren acar hastasının son bir yılki tüm satışlarını göster",
    new AiApplicationContext(),
    new Dictionary<string, JsonElement> { ["hasta_eslestir"] = patientMatch.Output }, [],
    RequestContext: Request("evren acar hastasının son bir yılki tüm satışlarını göster", "home"),
    ToolExecutor: patientSalesExecutor,
    Parameters: new Dictionary<string, string> { ["period_months"] = "12" }));
Assert(patientMatch.Status == AiWorkflowStepStatus.Completed
       && patientMatch.Output.GetProperty("selected_patient").GetProperty("phone_number").GetString() == ""
       && patientSales.Status == AiWorkflowStepStatus.Completed
       && patientSalesExecutor.LastArguments.GetProperty("patient_record_id").GetInt32() == 4242
       && patientSales.ModuleActions![0].Parameters["patient_record_id"] == "4242",
    "A named patient is resolved once and the stable patient ID reaches both the chat query and Sales-tab action.");

var typoPatientExecutor = new TypoPatientMatchingToolExecutor();
var typoPatientMatch = await new PatientMatchWorkflow().ExecuteAsync(new AiWorkflowContext(
    "fikıret acar satışlar göster", "fikıret acar satışlar göster",
    new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    RequestContext: Request("fikıret acar satışlar göster", "home"), ToolExecutor: typoPatientExecutor,
    Parameters: new Dictionary<string, string> { ["query"] = "fikıret acar" }));
Assert(typoPatientMatch.Status == AiWorkflowStepStatus.Completed
       && typoPatientMatch.Output.GetProperty("patient_record_id").GetInt32() == 6161
       && typoPatientExecutor.Queries.SequenceEqual(["FİKIRET ACAR", "ACAR"]),
    "A failed patient lookup retries once by exact surname and accepts only a one-edit full-name match such as Fikıret to Fikret.");

var productSaleExecutor = new ProductSaleSearchToolExecutor();
var productSalesResult = await new SeeSalesWorkflow().ExecuteAsync(new AiWorkflowContext(
    "parol satışları", "parol satışları", new AiApplicationContext(),
    new Dictionary<string, JsonElement>
    {
        ["ilac_barkod_eslestir"] = JsonSerializer.SerializeToElement(new { barcodes = new[] { "PAROL30" } })
    }, [], RequestContext: Request("parol satışları", "home"), ToolExecutor: productSaleExecutor,
    Parameters: new Dictionary<string, string> { ["period_days"] = "30" }));
var productSaleRow = productSalesResult.Output.GetProperty("rows")[0];
Assert(productSalesResult.Status == AiWorkflowStepStatus.Completed
       && productSaleExecutor.LastArguments.GetProperty("include_lines").GetBoolean()
       && productSaleRow.GetProperty("requested_product_quantity").GetString() == "1"
       && productSaleRow.GetProperty("requested_product_net_amount").GetString() == "100"
       && productSaleRow.GetProperty("transaction_quantity").GetString() == "7",
    "Medicine-filtered sale rows distinguish the requested product quantity and revenue from transaction totals.");
var productSalesTable = await new DisplayTableWorkflow().ExecuteAsync(new AiWorkflowContext(
    "tabloda göster", "tabloda göster", new AiApplicationContext(),
    new Dictionary<string, JsonElement> { ["satislari_gor"] = productSalesResult.Output }, [],
    Parameters: new Dictionary<string, string> { ["title"] = "Satış Kayıtları" }));
Assert(productSalesTable.Table!.Columns.Single(column => column.Key == "requested_product_quantity").IsVisibleByDefault
       && !productSalesTable.Table.Columns.Single(column => column.Key == "transaction_quantity").IsVisibleByDefault
       && productSalesTable.Table.ColorModes.Count == 0,
    "Medicine-filtered sales show relevant medicine metrics and hide misleading transaction totals by default.");
var productSalesViewModel = new WorkflowTableViewModel(productSalesTable.Table);
var noColorMode = productSalesViewModel.ColorModes.Single(mode => mode.Id == "__none" && mode.Label == "Yok");
productSalesViewModel.SelectedColorMode = noColorMode;
Assert(ReferenceEquals(productSalesViewModel.Rows[0]["__row_color"], System.Windows.Media.Brushes.White),
    "Every visible color selector includes a local no-color option that clears row coloring.");

foreach (var shorthand in new[] { "parol 500/30", "parol 500 30", "parol 500e30", "parol 50/30" })
{
    var shorthandResult = await new DrugBarcodeMatchWorkflow().ExecuteAsync(new AiWorkflowContext(
        shorthand, shorthand, new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
        MedicineNames: [shorthand], RequestContext: Request(shorthand, "home"),
        ToolExecutor: new ProductMatchingToolExecutor()));
    Assert(shorthandResult.Status == AiWorkflowStepStatus.Completed
           && shorthandResult.Output.GetProperty("barcodes").GetArrayLength() == 1
           && shorthandResult.Output.GetProperty("barcodes")[0].GetString() == "50030",
        $"Medicine shorthand '{shorthand}' narrows to PAROL 500 MG 30 TABLET.");
}
var wrongPackageResult = await new DrugBarcodeMatchWorkflow().ExecuteAsync(new AiWorkflowContext(
    "parol 500e30", "parol 500e30", new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    MedicineNames: ["parol 500e30"], RequestContext: Request("parol 500e30", "home"),
    ToolExecutor: new WrongPackageOnlyProductMatchingToolExecutor()));
Assert(wrongPackageResult.Status == AiWorkflowStepStatus.Failed
       && !wrongPackageResult.Output.GetRawText().Contains("50020", StringComparison.Ordinal),
    "An explicit 500/30 request never falls back to a 500/20 package.");

var correctedBelocExecutor = new TerminalTypoProductMatchingToolExecutor();
var correctedBeloc = await new DrugBarcodeMatchWorkflow().ExecuteAsync(new AiWorkflowContext(
    "belok", "belok", new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
    MedicineNames: ["belok"], RequestContext: Request("belok", "home"),
    ToolExecutor: correctedBelocExecutor));
Assert(correctedBeloc.Status == AiWorkflowStepStatus.Completed
       && correctedBeloc.Output.GetProperty("barcodes")[0].GetString() == "BELOC100"
       && correctedBelocExecutor.Queries.SequenceEqual(["belok", "belo"]),
    "A no-result single-letter terminal typo such as belok is corrected to the unique high-confidence Beloc brand without another model call.");

var workflowConfiguration = new FixedAiConfigurationProvider(new AiConfiguration
{
    DebugMode = true,
    RouterModel = "gpt-5.6-luna",
    RouterReasoningEffort = "none"
});
var greetingTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":"Merhaba, ben Eczanenizin Yapay Zeka Asistanı'yım.","explosive":false,
     "explosion_reason":null,"corrected_request":"Merhaba sen kimsin?","correction_confidence":0.2,
     "medicine_names":[],"workflow_queue":[],"workflow_inputs":[]}
    """));
var greetingChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new TestSelectingWorkflow(), new DebugWorkflow()]),
    greetingTransport, workflowConfiguration, testDirectory);
var greetingRequest = Request("Merhaba sen kimsin?", "home");
var greetingAnswer = await greetingChat.SendAsync(greetingRequest);
Assert(greetingAnswer.Message.Contains("Eczanenizin Yapay Zeka Asistanı", StringComparison.Ordinal)
       && greetingTransport.Requests.Single().Tools.Count == 0
       && greetingChat.GetLatestRequestCaptureFiles(greetingRequest.SessionId) is { Count: 2 } greetingCaptures
       && greetingCaptures.Any(capture => capture.SentToModel && File.ReadAllText(capture.FilePath).Contains("workflow_queue", StringComparison.Ordinal))
       && greetingCaptures.All(capture => !File.ReadAllText(capture.FilePath).Contains("OPENAI_API_KEY", StringComparison.Ordinal)),
    "The new router answers basic chat and receives no individual application tools.");

var contactExecutor = new PatientContactToolExecutor();
var contactTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"nevzat telefon numarasını değiştir","correction_confidence":0.99,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","hasta_iletisim_bilgisi_getir"],
     "workflow_inputs":[{"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"nevzat değiş"}]}]}
    """));
var contactChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new PatientMatchWorkflow(), new PatientContactInformationWorkflow(), new DebugWorkflow()]),
    contactTransport, workflowConfiguration, testDirectory, contactExecutor);
var contactAnswer = await contactChat.SendAsync(new AiRequestContext(
    "patient-contact-session", "nevzat değiş telefon numarası",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(contactAnswer.PendingApproval is null
       && contactAnswer.Message.Contains("0532 111 22 33", StringComparison.Ordinal)
       && contactAnswer.Content is { Kind: AiWorkflowContentKind.InformationCards, Items.Count: 1 }
       && contactAnswer.Content.Items[0].Title == "NEVZAT DEĞİŞ"
       && contactAnswer.Content.Items[0].Value == "0532 111 22 33"
       && contactExecutor.Queries.SequenceEqual(["NEVZAT DEĞİŞ"])
       && contactTransport.Requests.Count == 1,
    "A patient surname that is also a Turkish verb is resolved as a full name and returns the registered phone without Terra replanning.");

var rowsTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"hareketleri getir","correction_confidence":0.1,
     "medicine_names":[],"workflow_queue":["test_rows"],
     "workflow_inputs":[{"workflow_name":"test_rows","arguments":[]}]}
    """));
var rowsChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new TestRowsWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()]),
    rowsTransport, workflowConfiguration, testDirectory);
var rowsAnswer = await rowsChat.SendAsync(Request("hareketleri getir", "home"));
Assert(rowsAnswer.Table is { Rows.Count: 2 }
       && rowsAnswer.ModuleActions is [{ ModuleId: "products.qr-movements" }]
       && !rowsAnswer.Message.Contains('{', StringComparison.Ordinal)
       && !rowsAnswer.Message.StartsWith("Debug iş akışı", StringComparison.Ordinal)
       && rowsAnswer.DebugSummary?.Contains("debug_workflow", StringComparison.Ordinal) == true,
    "Structured rows receive a local table even when the router omits tabloda_goster, while Debug never replaces the answer with raw JSON.");

var selectingWorkflow = new TestSelectingWorkflow();
var queueTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"parol bul","correction_confidence":0.96,
     "medicine_names":["parol"],"workflow_queue":["ilac_barkod_eslestir"],
     "workflow_inputs":[{"workflow_name":"ilac_barkod_eslestir","arguments":[{"name":"selection_scope","value":"multiple"}]}]}
    """));
var workflowChat = CreateWorkflowChat(
    new AiWorkflowRegistry([selectingWorkflow, new DebugWorkflow()]),
    queueTransport, workflowConfiguration, testDirectory);
var selectionAnswer = await workflowChat.SendAsync(Request("parool bul", "home"));
Assert(selectionAnswer.PendingSelection is { AllowsMultiple: true, Options.Count: 2 }
       && selectingWorkflow.LastEffectiveRequest == "parol bul"
       && selectingWorkflow.LastParameters?.GetValueOrDefault("selection_scope") == "multiple"
       && selectionAnswer.ModelUsed == "gpt-5.6-luna"
       && selectionAnswer.UsageByModel is { Count: 1 }
       && queueTransport.Requests.Single().InputItems.Last().GetRawText().Contains("pharmacy_date", StringComparison.Ordinal)
       && queueTransport.Requests.Count == 1,
    "The router validates generic workflow inputs, applies only a greater-than-90-percent spelling correction, and runs the queue once.");
var continuedAnswer = await workflowChat.ContinueSelectionAsync(
    selectionAnswer.PendingSelection!.Id, "user-1", ["111", "222"]);
Assert(continuedAnswer.PendingSelection is null
       && continuedAnswer.Message == "Barkodlar eşleştirildi."
       && continuedAnswer.ModelUsed is null
       && continuedAnswer.UsageByModel is { Count: 0 }
       && continuedAnswer.DebugSummary?.Contains("Model calls for this continuation: 0", StringComparison.Ordinal) == true
       && selectingWorkflow.ExecutionCount == 2
       && queueTransport.Requests.Count == 1,
    "Tamam continues the saved queue locally, supports multiple choices, and does not create a second model prompt.");

var memorySelectingWorkflow = new TestSelectingWorkflow();
var rememberedWorkflow = new TestRememberedBarcodeWorkflow();
var memoryTransport = new ScriptedTransport(true,
    ScriptedTransport.Final("""
        {"answer":null,"explosive":false,"explosion_reason":null,
         "corrected_request":"parol bul","correction_confidence":0.1,
         "medicine_names":["parol"],"use_remembered_medicine":false,
         "workflow_queue":["ilac_barkod_eslestir"],
         "workflow_inputs":[{"workflow_name":"ilac_barkod_eslestir","arguments":[]}]}
        """),
    ScriptedTransport.Final("""
        {"answer":null,"explosive":false,"explosion_reason":null,
         "corrected_request":"son 30 günlük hareketlerini getir","correction_confidence":0.1,
         "medicine_names":[],"use_remembered_medicine":true,
         "workflow_queue":["ilac_barkod_eslestir","test_remembered_barcode"],
         "workflow_inputs":[{"workflow_name":"ilac_barkod_eslestir","arguments":[]},{"workflow_name":"test_remembered_barcode","arguments":[]}]}
        """));
var memoryChat = CreateWorkflowChat(
    new AiWorkflowRegistry([memorySelectingWorkflow, rememberedWorkflow, new DebugWorkflow()]),
    memoryTransport, workflowConfiguration, testDirectory);
var memorySession = "remembered-medicine-session";
var memoryFirst = await memoryChat.SendAsync(new AiRequestContext(
    memorySession, "parol bul", new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
await memoryChat.ContinueSelectionAsync(memoryFirst.PendingSelection!.Id, "user-1", ["111"]);
var memoryFollowUp = await memoryChat.SendAsync(new AiRequestContext(
    memorySession, "son 30 günlük hareketlerini getir", new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(memoryFollowUp.Message.Contains("111", StringComparison.Ordinal)
       && rememberedWorkflow.LastBarcodes.SequenceEqual(["111"])
       && memorySelectingWorkflow.ExecutionCount == 2
       && memoryTransport.Requests[1].InputItems.Last().GetRawText().Contains("remembered_medicine_selection", StringComparison.Ordinal),
    "A confirmed medicine selection is reused by a later implicit medicine request without rerunning the matcher.");

var periodSalesExecutor = new PeriodAwareSalesToolExecutor();
var periodTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"parol son 12 gün satışları göster","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["satislari_gor"],
     "workflow_inputs":[{"workflow_name":"satislari_gor","arguments":[{"name":"barcodes","value":"PAROL20"},{"name":"period_days","value":"12"}]}]}
    """));
var periodChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new SeeSalesWorkflow(), new DebugWorkflow()]),
    periodTransport, workflowConfiguration, testDirectory, periodSalesExecutor);
var periodSession = "period-continuation-session";
var twelveDaySales = await periodChat.SendAsync(new AiRequestContext(
    periodSession, "parol son 12 gün satışları göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var sixtyDaySales = await periodChat.SendAsync(new AiRequestContext(
    periodSession, "60 günlük göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(twelveDaySales.Message.Contains("8 satış kaydı", StringComparison.Ordinal)
       && sixtyDaySales.Message.Contains("20 satış kaydı", StringComparison.Ordinal)
       && sixtyDaySales.Message.Contains(expectedToday.AddDays(-60).ToString("dd.MM.yyyy"), StringComparison.Ordinal)
       && periodTransport.Requests.Count == 1
       && sixtyDaySales.ModelUsed is null
       && sixtyDaySales.UsageByModel is { Count: 0 }
       && periodSalesExecutor.Arguments.Count == 2
       && periodSalesExecutor.Arguments[1].GetProperty("start_date").GetString() == expectedToday.AddDays(-60).ToString("yyyy-MM-dd")
       && periodSalesExecutor.Arguments[1].GetProperty("barcodes")[0].GetString() == "PAROL20"
       && !periodSalesExecutor.Arguments[1].GetProperty("include_refunds").GetBoolean(),
    "The exact '60 günlük göster' follow-up locally reuses the preceding sales workflow and filters without a second model call.");

var selectedPeriodExecutor = new AmbiguousPeriodWorkflowToolExecutor();
var selectedPeriodTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"parol 12 günlük satışlar","correction_confidence":0.1,
     "medicine_names":["parol"],"use_remembered_medicine":false,
     "workflow_queue":["ilac_barkod_eslestir","satislari_gor"],
     "workflow_inputs":[
       {"workflow_name":"ilac_barkod_eslestir","arguments":[]},
       {"workflow_name":"satislari_gor","arguments":[{"name":"period_days","value":"12"}]}
     ]}
    """));
var selectedPeriodChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new DrugBarcodeMatchWorkflow(), new SeeSalesWorkflow(), new DebugWorkflow()]),
    selectedPeriodTransport, workflowConfiguration, testDirectory, selectedPeriodExecutor);
var selectedPeriodSession = "selected-period-continuation-session";
var selectedPeriodChoice = await selectedPeriodChat.SendAsync(new AiRequestContext(
    selectedPeriodSession, "parol 12 günlük satışlar",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var selectedTwelveDaySales = await selectedPeriodChat.ContinueSelectionAsync(
    selectedPeriodChoice.PendingSelection!.Id, "user-1", ["50020"]);
var selectedSixtyDaySales = await selectedPeriodChat.SendAsync(new AiRequestContext(
    selectedPeriodSession, "60 günlük",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(selectedTwelveDaySales.Message.Contains("8 satış kaydı", StringComparison.Ordinal)
       && selectedSixtyDaySales.Message.Contains("20 satış kaydı", StringComparison.Ordinal)
       && selectedPeriodTransport.Requests.Count == 1
       && selectedSixtyDaySales.ModelUsed is null
       && selectedPeriodExecutor.SalesArguments.Count == 2
       && selectedPeriodExecutor.SalesArguments.All(arguments =>
           arguments.GetProperty("barcodes").GetArrayLength() == 1
           && arguments.GetProperty("barcodes")[0].GetString() == "50020")
       && selectedPeriodExecutor.SalesArguments[1].GetProperty("start_date").GetString()
           == expectedToday.AddDays(-60).ToString("yyyy-MM-dd"),
    "A period continuation replays the exact barcode confirmed by the ambiguity UI, not mutable global medicine memory, with zero extra model calls.");

var allPatientSalesExecutor = new PatientPeriodWorkflowToolExecutor();
var allPatientSalesTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"evren acarın tüm satışlarını göster","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["satislari_gor"],
     "workflow_inputs":[{"workflow_name":"satislari_gor","arguments":[]}]}
    """));
var allPatientSalesChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new PatientMatchWorkflow(), new SeeSalesWorkflow(), new DebugWorkflow()]),
    allPatientSalesTransport, workflowConfiguration, testDirectory, allPatientSalesExecutor);
var allPatientSales = await allPatientSalesChat.SendAsync(new AiRequestContext(
    "all-patient-sales-session", "evren acarın tüm satışlarını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(allPatientSales.Message.Contains("1 satış kaydı", StringComparison.Ordinal)
       && allPatientSalesExecutor.PatientArguments.Single().GetProperty("query").GetString() == "EVREN ACAR"
       && allPatientSalesExecutor.SalesArguments.Single().GetProperty("patient_record_id").GetInt32() == 4242
       && allPatientSalesExecutor.SalesArguments.Single().GetProperty("start_date").GetString() == expectedToday.AddYears(-10).ToString("yyyy-MM-dd")
       && allPatientSalesTransport.Requests.Count == 1,
    "A patient possessive plus 'tüm satışları' is repaired into patient matching and the full supported history even when the router omits both.");

var turkishPossessiveExecutor = new PatientPeriodWorkflowToolExecutor();
var turkishPossessiveTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"fikret acarın geçmiş satışarı","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["satislari_gor"],
     "workflow_inputs":[{"workflow_name":"satislari_gor","arguments":[]}]}
    """));
var turkishPossessiveChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new PatientMatchWorkflow(), new SeeSalesWorkflow(), new DebugWorkflow()]),
    turkishPossessiveTransport, workflowConfiguration, testDirectory, turkishPossessiveExecutor);
var turkishPossessiveSales = await turkishPossessiveChat.SendAsync(new AiRequestContext(
    "turkish-patient-possessive-session", "fikret acarın geçmiş satışarı",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(turkishPossessiveSales.Message.Contains("1 satış kaydı", StringComparison.Ordinal)
       && turkishPossessiveExecutor.PatientArguments.Single().GetProperty("query").GetString() == "FİKRET ACAR"
       && turkishPossessiveExecutor.SalesArguments.Single().GetProperty("patient_record_id").GetInt32() == 4242,
    "Attached Turkish possessive suffixes and a common sales-word typo still produce a patient match before sales.");

var orderTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"parol sipariş ver","correction_confidence":0.1,
     "medicine_names":["parol"],"use_remembered_medicine":false,
     "workflow_queue":["siparis_istegini_tani"],"workflow_inputs":[]}
    """));
var orderChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new RecognizeOrderIntentWorkflow(), new DebugWorkflow()]),
    orderTransport, workflowConfiguration, testDirectory);
var orderResult = await orderChat.SendAsync(new AiRequestContext(
    "order-recognition-session", "parol sipariş ver",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(orderResult.PendingApproval is null
       && orderResult.Message.Contains("parol", StringComparison.OrdinalIgnoreCase)
       && orderResult.Message.Contains("satın alma yapılmadı", StringComparison.Ordinal)
       && orderTransport.Requests.Count == 1,
    "An order command is recognized directly without an explosive or advanced-model approval when no order tool can mutate data.");

var patientRetryExecutor = new PatientRetryAndContentsToolExecutor();
var patientRetryTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"fikretin tüm satışlarını getir","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","satislari_gor","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"fikret"}]},
       {"workflow_name":"satislari_gor","arguments":[{"name":"all_history","value":"true"},{"name":"include_refunds","value":"false"}]},
       {"workflow_name":"tabloda_goster","arguments":[{"name":"title","value":"Fikret Acar hastasının tüm satışları"}]}
     ]}
    """), ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"fikretin satışlarını getir","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","satislari_gor","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"fikret"}]},
       {"workflow_name":"satislari_gor","arguments":[{"name":"all_history","value":"true"}]},
       {"workflow_name":"tabloda_goster","arguments":[{"name":"title","value":"Fikret'in satışları"}]}
     ]}
    """));
var patientRetryChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new PatientMatchWorkflow(), new SeeSalesWorkflow(), new PatientSalesContentsWorkflow(),
        new DisplayTableWorkflow(), new DebugWorkflow()
    ]), patientRetryTransport, workflowConfiguration, testDirectory, patientRetryExecutor);
var patientRetrySession = "patient-retry-and-contents-session";
var patientRetryChoice = await patientRetryChat.SendAsync(new AiRequestContext(
    patientRetrySession, "fikretin tüm satışlarını getir.",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var initialPatientFailure = await patientRetryChat.ContinueSelectionAsync(
    patientRetryChoice.PendingSelection!.Id, "user-1", ["42"]);
var retriedPatientSales = await patientRetryChat.SendAsync(new AiRequestContext(
    patientRetrySession, "tekrar dene",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var patientSaleContents = await patientRetryChat.SendAsync(new AiRequestContext(
    patientRetrySession, "içerikleri ile birlikte göster.",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var firstNamePatientReference = await patientRetryChat.SendAsync(new AiRequestContext(
    patientRetrySession, "fikretin satışlarını getir",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(initialPatientFailure.Message == "Satış kayıtları alınamadı."
       && retriedPatientSales.UsageByModel is { Count: 0 }
       && retriedPatientSales.Table is { Rows.Count: 1 }
       && patientSaleContents.UsageByModel is { Count: 0 }
       && patientSaleContents.Table is { Rows.Count: 2 }
       && patientSaleContents.Table.Rows.All(row => row.GetValueOrDefault("patient_name") == "FİKRET ACAR")
       && patientSaleContents.Table.Rows.Single(row => row.GetValueOrDefault("barcode") == "PAROL20")
           .GetValueOrDefault("product_quantity") == "2"
       && firstNamePatientReference.PendingSelection is null
       && firstNamePatientReference.Table is { Rows.Count: 1 }
       && patientRetryExecutor.SalesArguments.Count == 4
       && patientRetryExecutor.SalesArguments.All(arguments => arguments.GetProperty("patient_record_id").GetInt32() == 42)
       && patientRetryTransport.Requests.Count == 2,
    "A selected patient survives a failed sale query, local retry, line-content follow-up, and first-name possessive reference without becoming an all-pharmacy query or asking for the patient again. " +
    JsonSerializer.Serialize(new { initial = initialPatientFailure.Message, retry = retriedPatientSales.Message,
        contents = patientSaleContents.Message, reference = firstNamePatientReference.Message,
        selection = firstNamePatientReference.PendingSelection is not null, sales = patientRetryExecutor.SalesArguments,
        modelCalls = patientRetryTransport.Requests.Count }));

var selectedPatientSalesExecutor = new PatientPeriodWorkflowToolExecutor();
var selectedPatientSalesTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"adem yılmazın tüm satışlarını göster","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","satislari_gor"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"adem yılmaz"}]},
       {"workflow_name":"satislari_gor","arguments":[]}
     ]}
    """));
var selectedPatientSalesChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new PatientMatchWorkflow(), new SeeSalesWorkflow(), new DebugWorkflow()]),
    selectedPatientSalesTransport, workflowConfiguration, testDirectory, selectedPatientSalesExecutor);
var patientChoice = await selectedPatientSalesChat.SendAsync(new AiRequestContext(
    "selected-patient-sales-session", "adem yılmazın tüm satışlarını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var selectedPatientSales = await selectedPatientSalesChat.ContinueSelectionAsync(
    patientChoice.PendingSelection!.Id, "user-1", ["5252"]);
Assert(selectedPatientSales.Message.Contains("1 satış kaydı", StringComparison.Ordinal)
       && selectedPatientSalesExecutor.PatientArguments.Single().GetProperty("query").GetString() == "ADEM YILMAZ"
       && selectedPatientSalesExecutor.SalesArguments.Single().GetProperty("patient_record_id").GetInt32() == 5252
       && selectedPatientSalesExecutor.SalesArguments.Single().GetProperty("start_date").GetString() == expectedToday.AddYears(-10).ToString("yyyy-MM-dd")
       && selectedPatientSalesTransport.Requests.Count == 1,
    "Multiple patient selection preserves the all-history sales plan and resumes locally with the selected patient ID.");

var reportedComparisonTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"parol satışları ve arveles satışlarını karşılaştır","correction_confidence":0.1,
     "medicine_names":["parol","arveles"],"use_remembered_medicine":false,
     "workflow_queue":["ilac_barkod_eslestir","satis_dagilimi_analizi","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"ilac_barkod_eslestir","arguments":[]},
       {"workflow_name":"satis_dagilimi_analizi","arguments":[{"name":"group_by","value":"product"}]},
       {"workflow_name":"tabloda_goster","arguments":[]}
     ]}
    """));
var reportedComparisonChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new DrugBarcodeMatchWorkflow(), new FirstMedicineBarcodeMatchWorkflow(), new SecondMedicineBarcodeMatchWorkflow(),
        new SalesDistributionWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()
    ]), reportedComparisonTransport, workflowConfiguration, testDirectory, new ProductMatchingToolExecutor());
var firstMedicineSelection = await reportedComparisonChat.SendAsync(new AiRequestContext(
    "reported-two-medicine-comparison-session", "parol satışları ve arveles satışlarını karşılaştır",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(firstMedicineSelection.PendingSelection is not null
       && firstMedicineSelection.WorkflowQueue?.Take(2).SequenceEqual(["ilac_barkod_eslestir_1", "ilac_barkod_eslestir_2"]) == true,
    "The 'A sales and B sales' grammar is normalized into two independent medicine-selection workflow slots.");
var secondMedicineSelection = await reportedComparisonChat.ContinueSelectionAsync(
    firstMedicineSelection.PendingSelection!.Id, "user-1", [firstMedicineSelection.PendingSelection.Options[0].Id]);
Assert(secondMedicineSelection.PendingSelection is not null
       && reportedComparisonTransport.Requests.Count == 1,
    "Confirming the first medicine locally advances to the second medicine selector without another model call.");

var compoundComparisonTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":"Alım miktarı önerisi için ilaç seçimi gerekiyor.","explosive":false,"explosion_reason":null,
     "corrected_request":"aerius ve beloc satışları karşılaştır daha sonra son 1 ayki satışlara göre ikisinden de kaç tane almalıyım söyle","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["alim_miktari_oner"],
     "workflow_inputs":[{"workflow_name":"alim_miktari_oner","arguments":[{"name":"days","value":"30"}]}]
    }
    """));
var compoundComparisonExecutor = new CompoundMedicineToolExecutor();
var compoundComparisonChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new FirstMedicineBarcodeMatchWorkflow(), new SecondMedicineBarcodeMatchWorkflow(),
        new SalesDistributionWorkflow(), new PurchaseQuantityRecommendationWorkflow(),
        new SectionedSummaryWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()
    ]), compoundComparisonTransport, workflowConfiguration, testDirectory, compoundComparisonExecutor);
var compoundResult = await compoundComparisonChat.SendAsync(new AiRequestContext(
    "compound-aerius-beloc-session",
    "aerius ve beloc satışları karşılaştır daha sonra son 1 ayki satışlara göre ikisinden de kaç tane almalıyım söyle",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(compoundResult.PendingSelection is not null
       && compoundResult.WorkflowQueue?.SequenceEqual([
           "ilac_barkod_eslestir_1", "ilac_barkod_eslestir_2", "satis_dagilimi_analizi",
           "alim_miktari_oner", "bolumlu_ozet_goster"]) == true,
    "The Aerius/Beloc compound request is repaired into two independent selections plus both requested analyses even when the router returns an incomplete plan.");
compoundResult = await compoundComparisonChat.ContinueSelectionAsync(
    compoundResult.PendingSelection!.Id, "user-1", [compoundResult.PendingSelection.Options[0].Id]);
Assert(compoundResult.PendingSelection is not null,
    "The compound request retains the second medicine-selection step after Aerius is selected.");
compoundResult = await compoundComparisonChat.ContinueSelectionAsync(
    compoundResult.PendingSelection!.Id, "user-1", [compoundResult.PendingSelection.Options[0].Id]);
Assert(compoundResult.PendingSelection is null
       && compoundResult.Table is { Rows.Count: >= 4 }
       && compoundComparisonExecutor.AggregateCalls == 1
       && compoundComparisonExecutor.AnalysisCalls == 2
       && compoundResult.DebugSummary?.Contains("\"days\":\"30\"", StringComparison.Ordinal) == true
       && compoundComparisonTransport.Requests.Count == 1,
    "The saved compound queue compares both selected medicines and calculates both purchase quantities without another model call.");

var purchaseAdviceTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"kaç tane beloc almalıyım","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["ilac_barkod_eslestir"],
     "workflow_inputs":[{"workflow_name":"ilac_barkod_eslestir","arguments":[]}]}
    """));
var purchaseAdviceChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new TestSelectingWorkflow(), new PurchaseQuantityRecommendationWorkflow(),
        new InformationCardsWorkflow(), new DebugWorkflow()
    ]), purchaseAdviceTransport, workflowConfiguration, testDirectory, new PurchaseAdviceToolExecutor());
var purchaseAdviceSelection = await purchaseAdviceChat.SendAsync(new AiRequestContext(
    "reported-prefix-purchase-session", "kaç tane beloc almalıyım",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(purchaseAdviceSelection.PendingSelection is not null
       && purchaseAdviceSelection.WorkflowQueue?.Contains("alim_miktari_oner", StringComparer.Ordinal) == true,
    "The prefix-form purchase question retains the purchase recommendation after medicine selection.");
var purchaseAdviceResult = await purchaseAdviceChat.ContinueSelectionAsync(
    purchaseAdviceSelection.PendingSelection!.Id, "user-1", [purchaseAdviceSelection.PendingSelection.Options[0].Id]);
Assert(purchaseAdviceResult.Message.Contains("önerilen alım adedi", StringComparison.Ordinal)
       && purchaseAdviceResult.Content is { Items.Count: > 0 }
       && purchaseAdviceTransport.Requests.Count == 1,
    "The Beloc selection continues into purchase analysis and cards without a second model call.");

var stockShortageTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"Parol ilacından kaç eksiğim var?","correction_confidence":0.98,
     "medicine_names":["Parol"],"use_remembered_medicine":false,
     "workflow_queue":["ilac_barkod_eslestir","stok_analizini_gor","metrik_kartlari_goster"],
     "workflow_inputs":[
       {"workflow_name":"ilac_barkod_eslestir","arguments":[]},
       {"workflow_name":"stok_analizini_gor","arguments":[{"name":"days","value":"30"}]},
       {"workflow_name":"metrik_kartlari_goster","arguments":[{"name":"title","value":"Parol Stok Eksiği"}]}
     ]}
    """));
var stockShortageChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new TestSelectingWorkflow(), new StockAnalysisWorkflow(), new DisplayTableWorkflow(),
        new InformationCardsWorkflow(), new MetricCardsWorkflow(), new DebugWorkflow()
    ]), stockShortageTransport, workflowConfiguration, testDirectory, new StockPresentationToolExecutor());
var stockShortageSelection = await stockShortageChat.SendAsync(new AiRequestContext(
    "stock-shortage-presentation-session", "parol ilacınından kaç eksiğim var?",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(stockShortageSelection.PendingSelection is not null,
    "An ambiguous Parol stock-shortage question still pauses for product selection.");
var stockShortageResult = await stockShortageChat.ContinueSelectionAsync(
    stockShortageSelection.PendingSelection!.Id, "user-1", stockShortageSelection.PendingSelection.Options.Select(option => option.Id).ToArray());
Assert(stockShortageResult.WorkflowQueue?.SequenceEqual([
           "ilac_barkod_eslestir", "stok_analizini_gor", "tabloda_goster"]) == true
       && stockShortageResult.Table is { Rows.Count: 2, HighlightField: "missing_stock_quantity" }
       && stockShortageResult.Content is null
       && stockShortageResult.Table.Columns.Single(column => column.Key == "missing_stock_quantity") is
           { Label: "Eksik Stok", IsVisibleByDefault: true }
       && stockShortageResult.Table.Columns.Single(column => column.Key == "average_monthly_sales") is
           { Label: "Aylık Ortalama Satış", IsVisibleByDefault: true }
       && stockShortageResult.Table.Rows[1]["average_monthly_sales"] == "16.67"
       && stockShortageResult.Message.Contains("toplam eksik stok: 2 kutu", StringComparison.OrdinalIgnoreCase)
       && stockShortageTransport.Requests.Count == 1,
    "Multi-product stock shortages render as separate table columns, highlight Eksik Stok, format averages, and continue without a second model call.");

var misspelledStockTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,
     "explosive":false,"explosion_reason":null,
     "corrected_request":"Parol’den kaç ekşimiz var?","correction_confidence":0.1,
     "medicine_names":["parol"],"use_remembered_medicine":false,
     "workflow_queue":["ilac_barkod_eslestir","stok_analizini_gor","tabloda_goster"],
     "workflow_inputs":[]}
    """));
var misspelledStockChat = CreateWorkflowChat(
    new AiWorkflowRegistry([
        new TestSelectingWorkflow(), new StockAnalysisWorkflow(), new DisplayTableWorkflow(),
        new InformationCardsWorkflow(), new MetricCardsWorkflow(), new DebugWorkflow()
    ]), misspelledStockTransport, workflowConfiguration, testDirectory, new StockPresentationToolExecutor());
var misspelledStockSelection = await misspelledStockChat.SendAsync(new AiRequestContext(
    "misspelled-stock-shortage-session", "parolden kaç ekisğimiz var",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(misspelledStockSelection.PendingSelection is not null
       && misspelledStockSelection.WorkflowQueue?.SequenceEqual([
           "ilac_barkod_eslestir", "stok_analizini_gor", "tabloda_goster"]) == true,
    "A medicine shortage typo preserves the planned pharmacy-domain stock-analysis intent instead of accepting an unrelated low-confidence correction.");
var misspelledStockResult = await misspelledStockChat.ContinueSelectionAsync(
    misspelledStockSelection.PendingSelection!.Id, "user-1", [misspelledStockSelection.PendingSelection.Options[1].Id]);
Assert(misspelledStockResult.Table is { Rows.Count: 1, HighlightField: "missing_stock_quantity" }
       && misspelledStockResult.Message.Contains("eksik stok: 2 kutu", StringComparison.OrdinalIgnoreCase)
       && misspelledStockTransport.Requests.Count == 1,
    "The domain-corrected typo continues locally after selection and highlights the requested missing-stock quantity.");

var bareMedicineTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"beloc","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["stok_portfoy_analizi","tabloda_goster"],
     "workflow_inputs":[
       {"workflow_name":"stok_portfoy_analizi","arguments":[]},
       {"workflow_name":"tabloda_goster","arguments":[]}
     ]}
    """));
var bareMedicineChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new InventoryAnalysisWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()]),
    bareMedicineTransport, workflowConfiguration, testDirectory, new TestWorkflowToolExecutor());
var bareMedicineResult = await bareMedicineChat.SendAsync(new AiRequestContext(
    "reported-bare-medicine-session", "beloc",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(bareMedicineResult.PendingSelection is null && bareMedicineResult.Table is null
       && bareMedicineResult.Message.Contains("ne öğrenmek istediğinizi belirtin", StringComparison.Ordinal)
       && bareMedicineResult.WorkflowQueue is { Count: 0 },
    "A bare medicine name asks for the missing intent instead of running a catalogue-wide workflow.");
var localPriceExecutor = new LocalPriceWorkflowToolExecutor();
var localPriceTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"beloc ücret söyle","correction_confidence":0.1,
     "medicine_names":["beloc"],"use_remembered_medicine":false,
     "workflow_queue":["ilac_barkod_eslestir"],
     "workflow_inputs":[{"workflow_name":"ilac_barkod_eslestir","arguments":[]}]}
    """));
var localPriceChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new TestSelectingWorkflow(), new GetBarcodeInformationWorkflow(), new DebugWorkflow()]),
    localPriceTransport, workflowConfiguration, testDirectory, localPriceExecutor);
var localPriceSelection = await localPriceChat.SendAsync(new AiRequestContext(
    "local-price-session", "beloc ücret söyle",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var localPriceAnswer = await localPriceChat.ContinueSelectionAsync(
    localPriceSelection.PendingSelection!.Id, "user-1", ["111"]);
Assert(localPriceAnswer.Message.Contains("Satış Fiyatı: 84.5", StringComparison.Ordinal)
       && localPriceAnswer.Message.Contains("Depocu Satış Fiyatı: 55", StringComparison.Ordinal)
       && localPriceTransport.Requests.Count == 1
       && localPriceExecutor.ExecutedTools.SequenceEqual(["product.get", "product.get_price_history"]),
    "A local medicine-price request cannot stop at barcode matching and continues to current catalog price fields without another model call.");

var patientPeriodExecutor = new PatientPeriodWorkflowToolExecutor();
var patientPeriodTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"evren acar hastasının son bir yılki tüm satışlarını göster","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hasta_eslestir","satislari_gor"],
     "workflow_inputs":[
       {"workflow_name":"hasta_eslestir","arguments":[{"name":"query","value":"evren acar"}]},
       {"workflow_name":"satislari_gor","arguments":[{"name":"period_months","value":"12"}]}
     ]}
    """));
var patientPeriodChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new PatientMatchWorkflow(), new SeeSalesWorkflow(), new DebugWorkflow()]),
    patientPeriodTransport, workflowConfiguration, testDirectory, patientPeriodExecutor);
var patientPeriodSession = "patient-period-continuation-session";
_ = await patientPeriodChat.SendAsync(new AiRequestContext(
    patientPeriodSession, "evren acar hastasının son bir yılki tüm satışlarını göster",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
var patientTwoYears = await patientPeriodChat.SendAsync(new AiRequestContext(
    patientPeriodSession, "son 24",
    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
Assert(patientPeriodTransport.Requests.Count == 1
       && patientTwoYears.UsageByModel is { Count: 0 }
       && patientPeriodExecutor.SalesArguments.Count == 2
       && patientPeriodExecutor.SalesArguments.All(arguments =>
           arguments.GetProperty("patient_record_id").GetInt32() == 4242)
       && patientPeriodExecutor.SalesArguments[1].GetProperty("start_date").GetString() == expectedToday.AddMonths(-24).ToString("yyyy-MM-dd"),
    "Patient ID and month unit survive a unit-less local period replay without another router call.");

var defaultPeriodExecutor = new CapturingMovementToolExecutor();
var defaultPeriodTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":false,"explosion_reason":null,
     "corrected_request":"parol 500e 30 hareketlerini getir","correction_confidence":0.1,
     "medicine_names":[],"use_remembered_medicine":false,
     "workflow_queue":["hareketleri_gor"],
     "workflow_inputs":[{"workflow_name":"hareketleri_gor","arguments":[{"name":"nearest","value":"true"}]}]}
    """));
var defaultPeriodChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new SeeMovementsWorkflow(), new DebugWorkflow()]),
    defaultPeriodTransport, workflowConfiguration, testDirectory, defaultPeriodExecutor);
var defaultPeriodAnswer = await defaultPeriodChat.SendAsync(Request("parol 500e 30 hareketlerini getir", "home"));
Assert(defaultPeriodExecutor.LastArguments.GetProperty("start_date").GetString() == expectedToday.AddDays(-30).ToString("yyyy-MM-dd")
       && defaultPeriodExecutor.LastArguments.GetProperty("limit").GetInt32() == 100
       && defaultPeriodAnswer.Message.Contains("son 30 günlük varsayılan dönem", StringComparison.Ordinal),
    "A generic movement request cannot be silently converted to nearest-only; the local 30-day default is stated in the first answer.");

var explosiveWorkflow = new TestSelectingWorkflow();
var explosiveTransport = new ScriptedTransport(true, ScriptedTransport.Final("""
    {"answer":null,"explosive":true,"explosion_reason":"Toplu kayıt değişikliği veri kaybına yol açabilir.",
     "corrected_request":"toplu işlemi yap","correction_confidence":0.1,
     "medicine_names":["parol"],"workflow_queue":["ilac_barkod_eslestir"],
     "workflow_inputs":[{"workflow_name":"ilac_barkod_eslestir","arguments":[]}]}
    """));
var explosiveChat = CreateWorkflowChat(
    new AiWorkflowRegistry([explosiveWorkflow, new DebugWorkflow()]),
    explosiveTransport, workflowConfiguration, testDirectory);
var explosiveAnswer = await explosiveChat.SendAsync(Request("toplu işlemi yap", "home"));
Assert(explosiveAnswer.PendingApproval is not null && explosiveWorkflow.ExecutionCount == 0,
    "Explosive workflow queues pause behind the application-owned Evet/Hayır gate before execution.");
var approvedExplosive = await explosiveChat.ApproveAsync(
    explosiveAnswer.PendingApproval!.Id, "user-1");
Assert(approvedExplosive.PendingSelection is not null && explosiveWorkflow.ExecutionCount == 1
       && approvedExplosive.ModelUsed is null
       && approvedExplosive.UsageByModel is { Count: 0 }
       && explosiveTransport.Requests.Count == 1,
    "Approving an explosive route resumes the saved queue without a second router request.");

var advancedTransport = new ScriptedTransport(true,
    ScriptedTransport.Final("""
        {"answer":null,"explosive":false,"explosion_reason":null,
         "corrected_request":"özel raporu hazırla","correction_confidence":0.1,
         "medicine_names":[],"use_remembered_medicine":false,
         "needs_advanced_planning":true,"advanced_planning_reason":"Güvenilir iş akışı kuramadım.",
         "workflow_queue":[],"workflow_inputs":[]}
        """),
    ScriptedTransport.Final("""
        {"answer":null,"explosive":false,"explosion_reason":null,
         "corrected_request":"özel raporu hazırla","correction_confidence":0.1,
         "medicine_names":[],"use_remembered_medicine":false,
         "needs_advanced_planning":false,"advanced_planning_reason":null,
         "workflow_queue":["test_rows"],
         "workflow_inputs":[{"workflow_name":"test_rows","arguments":[]}]}
        """));
var advancedChat = CreateWorkflowChat(
    new AiWorkflowRegistry([new TestRowsWorkflow(), new DisplayTableWorkflow(), new DebugWorkflow()]),
    advancedTransport, workflowConfiguration, testDirectory);
var advancedApproval = await advancedChat.SendAsync(Request("özel raporu hazırla", "home"));
Assert(advancedApproval.PendingApproval is not null
       && advancedApproval.Message.Contains("gpt-5.6-terra", StringComparison.Ordinal)
       && advancedTransport.Requests.Count == 1,
    "An uncertain Luna plan pauses behind an application-owned Terra permission gate.");
var advancedAnswer = await advancedChat.ApproveAsync(advancedApproval.PendingApproval!.Id, "user-1");
Assert(advancedAnswer.Table is { Rows.Count: 2 }
       && advancedAnswer.ModelUsed == "gpt-5.6-terra"
       && advancedAnswer.UsageByModel is [{ Stage: "Yönlendirici", Model: "gpt-5.6-terra" }]
       && advancedTransport.Requests.Count == 2
       && advancedTransport.Requests[1].Tools.Count == 0,
    "Approved Terra performs exactly one advanced planning call, then the rebuilt deterministic queue runs without model tool access.");

if (string.Equals(Environment.GetEnvironmentVariable("PHARMIND_AI_INTEGRATION_TEST"), "1", StringComparison.Ordinal))
{
    var integrationConfiguration = new FixedAiConfigurationProvider(new AiConfiguration { StoreResponses = false });
    var integrationTransport = new OpenAiResponsesTransport(integrationConfiguration);
    Assert(integrationTransport.IsConfigured,
        "PHARMIND_AI_INTEGRATION_TEST requires PHARMIND_AI_GATEWAY_URL or OPENAI_API_KEY.");
    var integrationResponse = await integrationTransport.SendAsync(new AiModelRequest(
        "gpt-5.6-luna", "none", "Reply with only OK.",
        [JsonSerializer.SerializeToElement(new { role = "user", content = "Connectivity test" })],
        [], false, false));
    Assert(!string.IsNullOrWhiteSpace(integrationResponse.Text), "Optional Responses API integration returns text.");
    Console.WriteLine("PASS optional-openai-integration");
}

if (string.Equals(Environment.GetEnvironmentVariable("PHARMACYAPP_DB_INTEGRATION_TEST"), "1", StringComparison.Ordinal))
{
    var productTools = new ProductAiToolCatalog();
    foreach (var phrase in new[] { "arveles fiyat söyle", "Diğer Arveles formları", "parol bul" })
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            query = phrase
        }));
        var result = (ProductNameSearchToolResult)await productTools.ExecuteAsync(
            "product.search_by_name", arguments.RootElement);
        Assert(result.Products.Count > 0, $"Configured Product Catalog resolves '{phrase}'.");
        Console.WriteLine($"DB {phrase}: matched={result.MatchedText}; {result.TotalCount} match(es); first={result.Products[0].Name}; on_market={result.Products[0].IsOnMarket}");
    }
    var barcodeWorkflow = new DrugBarcodeMatchWorkflow();
    var workflowGateway = new AiToolExecutionGateway(
        discovered, new AllowPermissionService(), new AiConfirmationService(), fixedConfiguration,
        new AiOperationalLog(testDirectory));
    var workflowToolExecutor = new AiWorkflowToolExecutor(discovered, workflowGateway);
    var liveContactWorkflow = new PatientMatchWorkflow();
    var liveContactContext = new AiWorkflowContext(
        "nevzat değiş telefon numarası", "nevzat değiş telefon numarası", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], RequestContext: Request("nevzat değiş telefon numarası", "home"),
        ToolExecutor: workflowToolExecutor, Parameters: new Dictionary<string, string> { ["query"] = "nevzat değiş" });
    var liveContactMatch = await liveContactWorkflow.ExecuteAsync(liveContactContext);
    if (liveContactMatch.Status == AiWorkflowStepStatus.AwaitingSelection &&
        liveContactMatch.SelectionOptions is { Count: > 0 } contactOptions)
    {
        var exactContact = contactOptions.First(option =>
            option.Label.StartsWith("NEVZAT DEĞİŞ", StringComparison.CurrentCultureIgnoreCase));
        liveContactMatch = await liveContactWorkflow.ExecuteAsync(liveContactContext with
        {
            OfferedOptions = contactOptions, SelectedOptionIds = [exactContact.Id]
        });
    }
    Assert(liveContactMatch.Status == AiWorkflowStepStatus.Completed,
        "Configured patient directory resolves the exact Nevzat Değiş full name.");
    var liveContact = await new PatientContactInformationWorkflow().ExecuteAsync(new AiWorkflowContext(
        "nevzat değiş telefon numarası", "nevzat değiş telefon numarası", new AiApplicationContext(),
        new Dictionary<string, JsonElement> { ["hasta_eslestir"] = liveContactMatch.Output }, [],
        RequestContext: Request("nevzat değiş telefon numarası", "home"), ToolExecutor: workflowToolExecutor));
    Assert(liveContact.Status == AiWorkflowStepStatus.Completed
           && liveContact.Output.GetProperty("rows")[0].GetProperty("phone_number").GetString() is { Length: > 0 },
        "Patient contact workflow returns the registered phone from resolved local patient context.");
    Console.WriteLine("DB patient contact: exact full name and registered phone verified");

    if (string.Equals(Environment.GetEnvironmentVariable("PHARMIND_AI_INTEGRATION_TEST"), "1", StringComparison.Ordinal))
    {
        var liveConfiguration = new FixedAiConfigurationProvider(new AiConfiguration
        {
            StoreResponses = false,
            RouterModel = "gpt-5.6-luna",
            RouterReasoningEffort = "none",
            DebugMode = true
        });
        var liveTransport = new OpenAiResponsesTransport(liveConfiguration);
        var liveChat = CreateWorkflowChat(discoveredWorkflows, liveTransport, liveConfiguration,
            testDirectory, workflowToolExecutor);
        var focusNewRegressions = string.Equals(Environment.GetEnvironmentVariable("PHARMIND_AI_FOCUS_NEW"), "1", StringComparison.Ordinal);
        var liveAnswer = await liveChat.SendAsync(new AiRequestContext(
            "live-patient-contact-session", "nevzat değiş telefon numarası",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        Assert(liveAnswer.PendingApproval is null
               && liveAnswer.ModelUsed == "gpt-5.6-luna"
               && liveAnswer.Message.Contains(
                   liveContact.Output.GetProperty("rows")[0].GetProperty("phone_number").GetString()!,
                   StringComparison.Ordinal),
            "Live Luna router executes the local patient contact workflow without Terra replanning.");
        Console.WriteLine("PASS optional-live-patient-contact-router");

        var liveLatest = await liveChat.SendAsync(new AiRequestContext(
            "live-latest-patient-session", "fikret acarın son aldığı ilaç",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        Assert(liveLatest.PendingApproval is null
               && liveLatest.Table is { Rows.Count: > 0 }
               && liveLatest.Message.Contains("dönemi tarandı", StringComparison.Ordinal)
               && (liveLatest.UsageByModel?.Sum(item => item.Usage.InputTokens) ?? 0) < 15000,
            "Live Luna request routes latest-patient-product semantics correctly and stays below the input-token budget.");
        Console.WriteLine($"PASS optional-live-latest-patient-router: {liveLatest.Message}");

        var exactPatientGrammar = await liveChat.SendAsync(new AiRequestContext(
            "live-exact-patient-grammar-session", "fikret acar hastasının en son aldığı ilaç ne",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        Assert(exactPatientGrammar.PendingApproval is null
               && exactPatientGrammar.Table is { Rows.Count: > 0 }
               && exactPatientGrammar.Message.Contains("FİKRET ACAR", StringComparison.CurrentCultureIgnoreCase),
            "The exact 'patient's latest medicine' grammar resolves Fikret Acar on the first prompt.");
        Console.WriteLine($"PASS optional-live-exact-patient-grammar: {exactPatientGrammar.Message}");

        if (!focusNewRegressions)
        {
            var livePurchase = await liveChat.SendAsync(new AiRequestContext(
                "live-purchase-advice-session", "jardiance kaç tane almalıyım",
                new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
            if (livePurchase.PendingSelection is { } purchaseChoice)
            {
                var active = purchaseChoice.Options.FirstOrDefault(option =>
                    option.Data.TryGetProperty("is_on_market", out var market) && market.ValueKind == JsonValueKind.True)
                    ?? purchaseChoice.Options[0];
                livePurchase = await liveChat.ContinueSelectionAsync(purchaseChoice.Id, "user-1", [active.Id]);
            }
            Assert(livePurchase.PendingApproval is null
                   && livePurchase.Message.Contains("önerilen alım adedi", StringComparison.Ordinal)
                   && livePurchase.Content is { Items.Count: > 0 },
                "Live Jardiance request continues from medicine selection into the deterministic purchase analysis workflow.");
            Console.WriteLine($"PASS optional-live-purchase-advice-router: {livePurchase.Message}");
        }

        var stockSession = "live-stock-memory-session";
        var liveStock = await liveChat.SendAsync(new AiRequestContext(
            stockSession, "kaç hametan var",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        if (liveStock.PendingSelection is { } stockChoice)
        {
            var active = stockChoice.Options.FirstOrDefault(option =>
                option.Data.TryGetProperty("is_on_market", out var market) && market.ValueKind == JsonValueKind.True)
                ?? stockChoice.Options[0];
            liveStock = await liveChat.ContinueSelectionAsync(stockChoice.Id, "user-1", [active.Id]);
        }
        Assert(liveStock.Message.Contains("raf stoku", StringComparison.CurrentCultureIgnoreCase)
               && liveStock.Content is { Items.Count: > 0 },
            "A 'how many medicine' prompt returns shelf stock instead of sales revenue.");
        var rememberedStock = await liveChat.SendAsync(new AiRequestContext(
            stockSession, "stokta kaç tane var?",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        Assert(rememberedStock.Message.Contains("raf stoku", StringComparison.CurrentCultureIgnoreCase)
               && rememberedStock.Content is { Items.Count: > 0 },
            "A stock follow-up reuses the previously resolved medicine selection.");
        Console.WriteLine($"PASS optional-live-stock-memory: {rememberedStock.Message}");

        var comparison = await liveChat.SendAsync(new AiRequestContext(
            "live-two-medicine-comparison-session", "vildega plus ile jardiance satış miktarı, hasılatı ve karını karşılaştır",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        Assert(comparison.PendingSelection is not null, "Two-medicine comparison first asks for the Vildega product.");
        var firstChoice = comparison.PendingSelection!.Options.FirstOrDefault(option =>
            option.Data.TryGetProperty("is_on_market", out var market) && market.ValueKind == JsonValueKind.True)
            ?? comparison.PendingSelection.Options[0];
        comparison = await liveChat.ContinueSelectionAsync(comparison.PendingSelection.Id, "user-1", [firstChoice.Id]);
        Assert(comparison.PendingSelection is not null, "Two-medicine comparison separately asks for the Jardiance product.");
        var secondChoice = comparison.PendingSelection!.Options.FirstOrDefault(option =>
            option.Data.TryGetProperty("is_on_market", out var market) && market.ValueKind == JsonValueKind.True)
            ?? comparison.PendingSelection.Options[0];
        comparison = await liveChat.ContinueSelectionAsync(comparison.PendingSelection.Id, "user-1", [secondChoice.Id]);
        Assert(comparison.Table is not null
               && comparison.Table.Columns.Any(column => column.Label == "İlaç")
               && comparison.Table.Columns.Any(column => column.Key == "quantity")
               && comparison.Table.Columns.Any(column => column.Key == "net_revenue")
               && comparison.Table.Columns.Any(column => column.Key == "profit")
               && comparison.Table.ColorModes.Count == 0
               && !comparison.Message.Contains("Hasta kaydı", StringComparison.CurrentCultureIgnoreCase),
            "Two medicine names are resolved in separate UI steps and compared by quantity, revenue and profit without a useless unique-medicine color mode.");
        Console.WriteLine($"PASS optional-live-two-medicine-comparison: {comparison.Message}");

        var latestRefunds = await liveChat.SendAsync(new AiRequestContext(
            "live-latest-refunds-session", "en son yapılan 10 iadeyi göster",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        Assert(latestRefunds.Table is { Rows.Count: <= 10 }
               && latestRefunds.Table.Rows.All(row => row.GetValueOrDefault("record_type") == "refund"),
            "Latest-refunds routing returns only persisted refunds, newest first, with the requested cap.");
        Console.WriteLine($"PASS optional-live-latest-refunds: {latestRefunds.Message}");

        var revenueSession = "live-revenue-followup-session";
        var recentSalesProbe = await new SalesDistributionWorkflow().ExecuteAsync(new AiWorkflowContext(
            "recent Parol sales probe", "recent Parol sales probe", new AiApplicationContext(),
            new Dictionary<string, JsonElement>(), [], RequestContext: Request("recent Parol sales probe", "home"),
            ToolExecutor: workflowToolExecutor, Parameters: new Dictionary<string, string>
            {
                ["period_months"] = "2", ["group_by"] = "product", ["sort_by"] = "quantity", ["limit"] = "200"
            }));
        var parolSalesBarcode = recentSalesProbe.Output.GetProperty("rows").EnumerateArray()
            .Where(row => (row.GetProperty("group_label").GetString() ?? string.Empty).Contains("PAROL 500 MG 20", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.GetProperty("group_key").GetString() ?? string.Empty).FirstOrDefault(value => value.Length > 0);
        Assert(parolSalesBarcode is not null, "Configured database contains a recent PAROL 500 MG 20 sale for the continuation regression.");
        var liveRevenue = await liveChat.SendAsync(new AiRequestContext(
            revenueSession, "parol son 2 ay hasılat",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        if (liveRevenue.PendingSelection is { } revenueChoice)
        {
            var parol20 = revenueChoice.Options.FirstOrDefault(option =>
                    option.Id == parolSalesBarcode)
                ?? revenueChoice.Options[0];
            liveRevenue = await liveChat.ContinueSelectionAsync(revenueChoice.Id, "user-1", [parol20.Id]);
        }
        var contributingSales = await liveChat.SendAsync(new AiRequestContext(
            revenueSession, "bu satışları göster",
            new AiApplicationContext(CurrentModule: "home", CurrentUserId: "user-1"), new HashSet<string>()));
        Console.WriteLine($"LIVE revenue result: {liveRevenue.Message}; debug={liveRevenue.DebugSummary}");
        Console.WriteLine($"LIVE contributing sales: {contributingSales.Message}; rows={contributingSales.Table?.Rows.Count ?? -1}; debug={contributingSales.DebugSummary}");
        Assert(liveRevenue.Message.Contains("hasılat", StringComparison.CurrentCultureIgnoreCase)
               && contributingSales.UsageByModel is { Count: 0 }
               && contributingSales.Table is { Rows.Count: > 0 }
               && !string.Equals(contributingSales.Message, "Sonuç bulunamadı.", StringComparison.Ordinal),
            "A revenue answer's contributing sales are replayed with the exact resolved medicine and period without another model call.");
        Console.WriteLine($"PASS optional-live-revenue-followup: {contributingSales.Message}");
    }
    foreach (var shorthand in new[] { "parol 500/30", "parol 500 30", "parol 500e30", "parol 50/30" })
    {
        var shorthandResult = await barcodeWorkflow.ExecuteAsync(new AiWorkflowContext(
            shorthand, shorthand, new AiApplicationContext(), new Dictionary<string, JsonElement>(), [],
            MedicineNames: [shorthand], RequestContext: Request(shorthand, "home"), ToolExecutor: workflowToolExecutor));
        var shorthandNames = shorthandResult.Status == AiWorkflowStepStatus.AwaitingSelection
            ? shorthandResult.SelectionOptions!.Select(option => option.Data.GetProperty("name").GetString() ?? string.Empty).ToArray()
            : shorthandResult.Output.GetProperty("selected_products").EnumerateArray()
                .Select(option => option.GetProperty("name").GetString() ?? string.Empty).ToArray();
        Assert(shorthandNames.Length > 0 && shorthandNames.All(name => name.Contains("500", StringComparison.Ordinal)
                                                               && name.Contains("30", StringComparison.Ordinal)),
            $"Configured Product Catalog narrows '{shorthand}' to the 500 mg / 30-tablet form.");
        Console.WriteLine($"DB shorthand {shorthand}: {string.Join(" | ", shorthandNames)}");
    }
    var workflowResult = await barcodeWorkflow.ExecuteAsync(new AiWorkflowContext(
        "parol için işlem yap", "parol için işlem yap", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], MedicineNames: ["parol"],
        RequestContext: Request("parol için işlem yap", "home"), ToolExecutor: workflowToolExecutor));
    Assert(workflowResult.Status == AiWorkflowStepStatus.AwaitingSelection
           && workflowResult.SelectionOptions is { Count: > 1 }
           && workflowResult.SelectionOptions.All(option => option.Id.All(char.IsDigit)),
        "Medicine barcode workflow returns catalog-backed multi-select barcode choices.");
    Console.WriteLine($"DB workflow parol: {workflowResult.SelectionOptions!.Count} selectable barcode(s)");
    var productInformation = await new GetBarcodeInformationWorkflow().ExecuteAsync(new AiWorkflowContext(
        "parolun depocu satış fiyatı", "parolun depocu satış fiyatı", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], RequestContext: Request("parolun depocu satış fiyatı", "home"),
        ToolExecutor: workflowToolExecutor, Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = workflowResult.SelectionOptions[0].Id,
            ["fields"] = "purchase_price"
        }));
    Assert(productInformation.Status == AiWorkflowStepStatus.Completed
           && productInformation.Output.GetProperty("rows").GetArrayLength() == 1,
        "Local barcode-information workflow executes product.get and projects only requested fields.");
    var stockPortfolio = await new InventoryAnalysisWorkflow().ExecuteAsync(new AiWorkflowContext(
        "eksik listesi", "eksik listesi", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], RequestContext: Request("eksik listesi", "home"),
        ToolExecutor: workflowToolExecutor,
        Parameters: new Dictionary<string, string> { ["mode"] = "shortage", ["only_on_market"] = "true", ["limit"] = "5" }));
    Assert(stockPortfolio.Status == AiWorkflowStepStatus.Completed
           && stockPortfolio.Output.GetProperty("rows").GetArrayLength() <= 5
           && !stockPortfolio.Message.Contains("eksik araç", StringComparison.CurrentCultureIgnoreCase),
        "Stock portfolio uses the existing Product Catalog stock query instead of a missing stock.analyze_portfolio placeholder.");
    Console.WriteLine($"DB stock portfolio: {stockPortfolio.Message}");
    var movementResult = await new SeeMovementsWorkflow().ExecuteAsync(new AiWorkflowContext(
        "son hareketleri göster", "son hareketleri göster", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], RequestContext: Request("son hareketleri göster", "home"),
        ToolExecutor: workflowToolExecutor, Parameters: new Dictionary<string, string> { ["limit"] = "5" }));
    Assert(movementResult.Status == AiWorkflowStepStatus.Completed
           && movementResult.Output.TryGetProperty("rows", out _)
           && movementResult.ModuleActions is [{ ModuleId: "products.qr-movements" }],
        "Movement workflow executes the existing bounded QR movement tool queue.");
    var parol500x20 = workflowResult.SelectionOptions.First(option =>
        option.Data.GetProperty("name").GetString()!.Contains("500 MG 20", StringComparison.OrdinalIgnoreCase));
    var depotEntryMovement = await new SeeMovementsWorkflow().ExecuteAsync(new AiWorkflowContext(
        "parol son 20 gün depo giriş hareketleri", "parol son 20 gün depo giriş hareketleri",
        new AiApplicationContext(),
        new Dictionary<string, JsonElement>
        {
            ["ilac_barkod_eslestir"] = JsonSerializer.SerializeToElement(new { barcodes = new[] { parol500x20.Id } })
        }, [], RequestContext: Request("parol son 20 gün depo giriş hareketleri", "home"),
        ToolExecutor: workflowToolExecutor, Parameters: new Dictionary<string, string>
        {
            ["period_days"] = "20", ["movement_types"] = "Depo Giriş Hareketleri",
            ["direction"] = "entry", ["address_type"] = "depot", ["limit"] = "5"
        }));
    Assert(depotEntryMovement.Status == AiWorkflowStepStatus.Completed,
        "Live configured movement types accept the descriptive depot-entry request instead of rejecting it as unknown.");
    var liveTypoPatient = await new PatientMatchWorkflow().ExecuteAsync(new AiWorkflowContext(
        "fikıret acar satışlar göster", "fikıret acar satışlar göster", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], RequestContext: Request("fikıret acar satışlar göster", "home"),
        ToolExecutor: workflowToolExecutor,
        Parameters: new Dictionary<string, string> { ["query"] = "fikıret acar" }));
    Console.WriteLine($"DB typo patient: status={liveTypoPatient.Status}; message={liveTypoPatient.Message}; output={liveTypoPatient.Output.GetRawText()}; options={string.Join(" | ", liveTypoPatient.SelectionOptions?.Select(option => option.Label) ?? [])}");
    var liveTypoPatientName = liveTypoPatient.Status == AiWorkflowStepStatus.Completed
        ? liveTypoPatient.Output.GetProperty("selected_patient").GetProperty("name").GetString() ?? string.Empty
        : string.Empty;
    Assert(liveTypoPatient.Status == AiWorkflowStepStatus.Completed
           && liveTypoPatientName.Replace('İ', 'I').Contains("FIKRET ACAR", StringComparison.OrdinalIgnoreCase),
        "Live patient matching conservatively resolves the Fikıret/Fikret typo through the exact surname candidate set.");
    var latestPatientPurchase = await new PatientLatestProductsWorkflow().ExecuteAsync(new AiWorkflowContext(
        "fikret acarın son aldığı ilaç", "fikret acarın son aldığı ilaç", new AiApplicationContext(),
        new Dictionary<string, JsonElement> { ["hasta_eslestir"] = liveTypoPatient.Output }, [],
        RequestContext: Request("fikret acarın son aldığı ilaç", "home"), ToolExecutor: workflowToolExecutor,
        Parameters: new Dictionary<string, string> { ["all_history"] = "true" }));
    Assert(latestPatientPurchase.Status == AiWorkflowStepStatus.Completed
           && latestPatientPurchase.Message.Contains("dönemi tarandı", StringComparison.Ordinal)
           && latestPatientPurchase.Output.GetProperty("rows").GetArrayLength() > 0
           && latestPatientPurchase.Output.GetProperty("metadata").GetProperty("highlight_field").GetString() == "product_name",
        "Latest-patient-product workflow searches the supported history newest-first and reports its exact date range.");
    Console.WriteLine($"DB latest patient purchase: {latestPatientPurchase.Message}");
    var completePatientProducts = await new PatientProductSummaryWorkflow().ExecuteAsync(new AiWorkflowContext(
        "fikret acarın aldığı ilaçlar listesi", "fikret acarın aldığı ilaçlar listesi", new AiApplicationContext(),
        new Dictionary<string, JsonElement> { ["hasta_eslestir"] = liveTypoPatient.Output }, [],
        RequestContext: Request("fikret acarın aldığı ilaçlar listesi", "home"), ToolExecutor: workflowToolExecutor,
        Parameters: new Dictionary<string, string> { ["all_history"] = "true", ["limit"] = "50" }));
    var completeProductRows = completePatientProducts.Output.GetProperty("rows").GetArrayLength();
    var completeProductTotal = completePatientProducts.Output.GetProperty("metadata").GetProperty("total_count").GetInt32();
    Assert(completePatientProducts.Status == AiWorkflowStepStatus.Completed
           && completeProductRows == completeProductTotal
           && completePatientProducts.Message.Contains("03.09.2016", StringComparison.Ordinal),
        "Patient product history ignores an arbitrary router row cap, pages all groups, and states the searched range.");
    Console.WriteLine($"DB complete patient products: rows={completeProductRows}; total={completeProductTotal}; {completePatientProducts.Message}");

    var jardianceMatch = await barcodeWorkflow.ExecuteAsync(new AiWorkflowContext(
        "jardiance kaç tane almalıyım", "jardiance kaç tane almalıyım", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], MedicineNames: ["jardiance"],
        RequestContext: Request("jardiance kaç tane almalıyım", "home"), ToolExecutor: workflowToolExecutor));
    var jardianceBarcode = jardianceMatch.Status == AiWorkflowStepStatus.AwaitingSelection
        ? jardianceMatch.SelectionOptions!.First().Id
        : jardianceMatch.Output.GetProperty("barcodes")[0].GetString()!;
    var purchaseRecommendation = await new PurchaseQuantityRecommendationWorkflow().ExecuteAsync(new AiWorkflowContext(
        "jardiance kaç tane almalıyım", "jardiance kaç tane almalıyım", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], RequestContext: Request("jardiance kaç tane almalıyım", "home"),
        ToolExecutor: workflowToolExecutor, Parameters: new Dictionary<string, string> { ["barcodes"] = jardianceBarcode }));
    Assert(purchaseRecommendation.Status == AiWorkflowStepStatus.Completed
           && purchaseRecommendation.Message.Contains("önerilen alım adedi", StringComparison.Ordinal)
           && purchaseRecommendation.Output.GetProperty("rows")[0].TryGetProperty("recommended_purchase_quantity", out _),
        "Jardiance purchase advice continues beyond barcode matching and uses the existing product analysis service.");
    Console.WriteLine($"DB purchase recommendation: {purchaseRecommendation.Message}");
    var salesResult = await new SeeSalesWorkflow().ExecuteAsync(new AiWorkflowContext(
        "bugünkü satışları göster", "bugünkü satışları göster", new AiApplicationContext(),
        new Dictionary<string, JsonElement>(), [], RequestContext: Request("bugünkü satışları göster", "home"),
        ToolExecutor: workflowToolExecutor, Parameters: new Dictionary<string, string>
        {
            ["period_days"] = "1", ["limit"] = "5"
        }));
    Assert(salesResult.Status == AiWorkflowStepStatus.Completed
           && salesResult.Output.TryGetProperty("rows", out _)
           && salesResult.ModuleActions is [{ ModuleId: "sales.retail-list" }],
        "Sales workflow executes the completed filtered sale-record tool and emits a filtered module action.");
    var salesListRows = await new RetailSalesListRepository().LoadSalesAsync(new RetailSalesListQuery(
        DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1),
        Barcodes: workflowResult.SelectionOptions.Take(2).Select(option => option.Id).ToArray()),
        CancellationToken.None);
    Assert(salesListRows is not null,
        "The existing Sales module accepts the workflow's multi-barcode launch filter.");
    Console.WriteLine("PASS optional-product-catalog-integration");
}

Console.WriteLine("PASS medula-ai-tool-standard");
Console.WriteLine("PASS pharmacyapp-ai-runtime");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string MissingArguments(string capability, string toolName, string suggestedNamespace = "product") => JsonSerializer.Serialize(new
{
    original_request = "two unavailable capabilities",
    missing_capability = capability,
    suggested_namespace = suggestedNamespace,
    suggested_tool_name = toolName,
    description = "Test-only unavailable capability.",
    suggested_parameters = Array.Empty<string>(),
    suggested_result = "Structured test result.",
    reason = "No registered test tool provides this capability.",
    closest_existing_tools = Array.Empty<string>(),
    estimated_risk = AiToolRisks.ReadOnly
});

static AiRequestContext Request(string message, string? module) => new(
    "test-session-" + Guid.NewGuid().ToString("N"), message,
    new AiApplicationContext(CurrentModule: module, CurrentUserId: "user-1"),
    new HashSet<string>());

static AiOrchestrator CreateOrchestrator(
    IReadOnlyList<IAiTool> tools,
    ScriptedTransport transport,
    string testDirectory,
    AiConfiguration? configuration = null,
    IAiRouter? router = null)
{
    var provider = new FixedAiConfigurationProvider(configuration ?? new AiConfiguration());
    var registry = new AiToolRegistry(tools);
    var search = new AiToolSearchService(registry);
    var confirmations = new AiConfirmationService();
    var log = new AiOperationalLog(testDirectory);
    var gateway = new AiToolExecutionGateway(registry, new AllowPermissionService(), confirmations, provider, log);
    return new AiOrchestrator(
        router ?? new FixedRouter(new AiRouteDecision(["product"], true, AiComplexity.Simple, 1)),
        search, registry, new AiModelSelector(provider), transport, gateway, new AiConversationManager(provider),
        provider, new TestPrompts(), new AiRouteApprovalService(),
        new MissingToolStore(Path.Combine(testDirectory, "missing.json")),
        new AiUsageTelemetry(Path.Combine(testDirectory, "usage.json")), log);
}

static WorkflowRouterChatService CreateWorkflowChat(
    IAiWorkflowRegistry registry,
    IAiModelTransport transport,
    IAiConfigurationProvider configuration,
    string testDirectory,
    IAiWorkflowToolExecutor? toolExecutor = null) => new(
    registry,
    transport,
    configuration,
    new AiConversationManager(configuration),
    new AiUsageTelemetry(Path.Combine(testDirectory, "workflow-usage-" + Guid.NewGuid().ToString("N") + ".json")),
    new TestPrompts(),
    toolExecutor ?? new TestWorkflowToolExecutor(),
    new AiRequestCaptureStore(Path.Combine(testDirectory, "request-captures")));

sealed class TestTool : IAiTool
{
    private readonly Func<JsonElement, CancellationToken, Task<object>> execute;

    public TestTool(AiToolDefinition definition, Func<JsonElement, CancellationToken, Task<object>> execute)
    {
        Definition = definition;
        this.execute = execute;
    }

    public AiToolDefinition Definition { get; }
    public int ExecutionCount { get; private set; }

    public async Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ExecutionCount++;
        return await execute(parameters, cancellationToken);
    }

    public static TestTool ReadOnly(
        string name,
        bool requiredParameter = false,
        Func<JsonElement, CancellationToken, Task<object>>? execute = null) =>
        new(CreateDefinition(name, AiToolRisks.ReadOnly, requiredParameter), execute ?? ((_, _) => Task.FromResult<object>(new { ok = true })));

    public static AiToolDefinition CreateDefinition(string name, string risk = AiToolRisks.ReadOnly, bool requiredParameter = false) => new(
        name, $"Test capability for {name} product barcode lookup.", risk,
        requiredParameter
            ? new Dictionary<string, AiToolParameterDefinition> { ["value"] = new("string", "Test value.", true) }
            : new Dictionary<string, AiToolParameterDefinition>(),
        new Dictionary<string, string> { ["ok"] = "boolean" },
        ["test", "product", "barcode"]);
}

sealed class TestSelectingWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "ilac_barkod_eslestir", "İlaç barkod eşleştir", "Test medicine selection workflow.",
        Parameters: new Dictionary<string, string> { ["selection_scope"] = "Test parameter." });
    public int ExecutionCount { get; private set; }
    public string? LastEffectiveRequest { get; private set; }
    public IReadOnlyDictionary<string, string>? LastParameters { get; private set; }

    public Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ExecutionCount++;
        LastEffectiveRequest = context.EffectiveRequest;
        LastParameters = context.Parameters;
        if (context.SelectedOptionIds is { Count: > 0 })
            return Task.FromResult(new AiWorkflowStepResult(
                AiWorkflowStepStatus.Completed,
                "Barkodlar eşleştirildi.",
                JsonSerializer.SerializeToElement(new { barcodes = context.SelectedOptionIds })));
        var options = new[]
        {
            new AiWorkflowSelectionOption("111", "PAROL 1 · 111", JsonSerializer.SerializeToElement(new { barcode = "111" })),
            new AiWorkflowSelectionOption("222", "PAROL 2 · 222", JsonSerializer.SerializeToElement(new { barcode = "222" }))
        };
        return Task.FromResult(new AiWorkflowStepResult(
            AiWorkflowStepStatus.AwaitingSelection,
            "İlaçları seçin.",
            JsonSerializer.SerializeToElement(new { candidates = 2 }),
            options,
            true));
    }
}

sealed class PurchaseAdviceToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => toolName == "product.get_analysis";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) => Task.FromResult(new AiWorkflowToolExecution(
        toolName,
        AiToolExecutionStatus.Success,
        JsonSerializer.SerializeToElement(new
        {
            found = true,
            missing_stock_quantity = 12,
            average_monthly_sales = 18,
            wanted_stock_months = 1,
            target_stock_quantity = 18,
            shelf_stock_quantity = 4,
            consigned_stock_quantity = 2,
            stock_comment = "stok desteği önerilir"
        }),
        null,
        1));
}

sealed class StockPresentationToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => toolName is "product.get_analysis" or "product.get";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var barcode = arguments.GetProperty("barcode").GetString() ?? string.Empty;
        object output = toolName == "product.get"
            ? new
            {
                found = true,
                product = new { summary = new { name = "PAROL 500 MG 20 TABLET" } }
            }
            : barcode == "111"
                ? new
                {
                    found = true,
                    missing_stock_quantity = 0,
                    average_monthly_sales = 0m,
                    target_stock_quantity = 0,
                    shelf_stock_quantity = 0,
                    consigned_stock_quantity = 0,
                    stock_comment = "stok yeterli"
                }
                : new
                {
                    found = true,
                    missing_stock_quantity = 2,
                    average_monthly_sales = 16.666666666666666666666666667m,
                    target_stock_quantity = 25,
                    shelf_stock_quantity = 23,
                    consigned_stock_quantity = 0,
                    stock_comment = "alım öneriliyor"
                };
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(output), null, 1));
    }
}
sealed class TestWorkflowToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => false;

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No tool execution is expected in this test workflow.");
}

sealed class ProductMatchingToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => toolName == "product.search_by_name";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) => Task.FromResult(new AiWorkflowToolExecution(
        toolName,
        AiToolExecutionStatus.Success,
        JsonSerializer.SerializeToElement(new
        {
            products = new[]
            {
                new { barcode = "50020", name = "PAROL 500 MG 20 TABLET", is_on_market = true },
                new { barcode = "50030", name = "PAROL 500 MG 30 TABLET", is_on_market = true },
                new { barcode = "plus30", name = "PAROL PLUS 250 MG/150 MG/50 MG 30 TABLET", is_on_market = true }
            }
        }),
        null,
        1));
}

sealed class CompoundMedicineToolExecutor : IAiWorkflowToolExecutor
{
    public int AggregateCalls { get; private set; }
    public int AnalysisCalls { get; private set; }

    public bool HasTool(string toolName) => toolName is
        "product.search_by_name" or "sale.aggregate_records" or "product.get_analysis";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        object output = toolName switch
        {
            "product.search_by_name" => Search(arguments),
            "sale.aggregate_records" => Aggregate(arguments),
            "product.get_analysis" => Analysis(arguments),
            _ => new { }
        };
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(output), null, 1));
    }

    private static object Search(JsonElement arguments)
    {
        var query = arguments.GetProperty("query").GetString() ?? string.Empty;
        var aerius = query.Contains("aerius", StringComparison.OrdinalIgnoreCase);
        return new
        {
            products = new[]
            {
                new
                {
                    barcode = aerius ? "8699000000001" : "8699000000011",
                    name = aerius ? "AERIUS 5 MG 20 TABLET" : "BELOC ZOK 50 MG 20 TABLET",
                    is_on_market = true
                },
                new
                {
                    barcode = aerius ? "8699000000002" : "8699000000012",
                    name = aerius ? "AERIUS 0,5 MG/ML ŞURUP" : "BELOC ZOK 100 MG 20 TABLET",
                    is_on_market = true
                }
            }
        };
    }

    private object Aggregate(JsonElement arguments)
    {
        AggregateCalls++;
        var barcodes = arguments.GetProperty("barcodes").EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty).ToArray();
        return new
        {
            groups = barcodes.Select((barcode, index) => new
            {
                group_key = barcode,
                group_label = barcode == "8699000000001" ? "AERIUS 5 MG 20 TABLET" : "BELOC ZOK 50 MG 20 TABLET",
                record_count = 4 + index,
                sale_count = 4 + index,
                refund_count = 0,
                quantity = 5 + index,
                net_revenue = 500 + index * 100,
                profit = 80 + index * 20
            }).ToArray(),
            total_count = barcodes.Length,
            has_more = false
        };
    }

    private object Analysis(JsonElement arguments)
    {
        AnalysisCalls++;
        return new
        {
            found = true,
            missing_stock_quantity = 3,
            average_monthly_sales = 8,
            wanted_stock_months = 1,
            target_stock_quantity = 8,
            shelf_stock_quantity = 4,
            consigned_stock_quantity = 1,
            stock_comment = "stok desteği önerilir"
        };
    }
}

sealed class WrongPackageOnlyProductMatchingToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => toolName == "product.search_by_name";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) => Task.FromResult(new AiWorkflowToolExecution(
        toolName,
        AiToolExecutionStatus.Success,
        JsonSerializer.SerializeToElement(new
        {
            products = new[] { new { barcode = "50020", name = "PAROL 500 MG 20 TABLET", is_on_market = true } }
        }),
        null,
        1));
}

sealed class TerminalTypoProductMatchingToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> Queries { get; } = [];
    public bool HasTool(string toolName) => toolName == "product.search_by_name";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var query = arguments.GetProperty("query").GetString()!;
        Queries.Add(query);
        var products = query == "belo"
            ? new[] { new { barcode = "BELOC100", name = "BELOC 100 MG 20 TABLET", is_on_market = true } }
            : Array.Empty<object>();
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new { products }), null, 1));
    }
}

sealed class PatientMatchingToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => toolName == "sale.search_patients";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) => Task.FromResult(new AiWorkflowToolExecution(
        toolName,
        AiToolExecutionStatus.Success,
        JsonSerializer.SerializeToElement(new
        {
            patients = new[]
            {
                new { patient_record_id = 4242, patient_tc = "11111111111", name = "EVREN ACAR", phone_number = "" }
            }
        }),
        null,
        1));
}

sealed class PatientContactToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> Queries { get; } = [];
    public bool HasTool(string toolName) => toolName == "sale.search_patients";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var query = arguments.GetProperty("query").GetString()!;
        Queries.Add(query);
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                patients = new[]
                {
                    new
                    {
                        patient_record_id = 7171,
                        patient_tc = "11111111111",
                        name = "NEVZAT DEĞİŞ",
                        phone_number = "0532 111 22 33"
                    }
                }
            }),
            null,
            1));
    }
}

sealed class TypoPatientMatchingToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> Queries { get; } = [];
    public bool HasTool(string toolName) => toolName == "sale.search_patients";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var query = arguments.GetProperty("query").GetString()!;
        Queries.Add(query);
        var patients = query == "ACAR"
            ? new[] { new { patient_record_id = 6161, patient_tc = "11111111111", name = "FIKRET ACAR", phone_number = "" } }
            : Array.Empty<object>();
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new { patients }), null, 1));
    }
}

sealed class TestRowsWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new("test_rows", "Test rows", "Returns structured test rows.");

    public Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default) => Task.FromResult(new AiWorkflowStepResult(
        AiWorkflowStepStatus.Completed,
        "2 hareket bulundu.",
        JsonSerializer.SerializeToElement(new
        {
            rows = new[]
            {
                new { barcode = "111", movement_type = "Satış" },
                new { barcode = "222", movement_type = "Alış" }
            }
        }),
        ModuleActions:
        [
            new AiWorkflowModuleAction("open_test_movements", "Karekod Hareketleri modülünde aç",
                "products.qr-movements", new Dictionary<string, string>())
        ]));
}

sealed class TestRememberedBarcodeWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "test_remembered_barcode", "Remembered barcode test", "Reads the prior matcher output.");
    public IReadOnlyList<string> LastBarcodes { get; private set; } = [];

    public Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        LastBarcodes = context.Outputs.Values
            .Where(value => value.TryGetProperty("barcodes", out var barcodes) && barcodes.ValueKind == JsonValueKind.Array)
            .SelectMany(value => value.GetProperty("barcodes").EnumerateArray())
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!)
            .ToArray();
        return Task.FromResult(new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            $"Hatırlanan barkodlar: {string.Join(", ", LastBarcodes)}",
            JsonSerializer.SerializeToElement(new { barcodes = LastBarcodes })));
    }
}

sealed class CapturingMovementToolExecutor : IAiWorkflowToolExecutor
{
    public JsonElement LastArguments { get; private set; }

    public bool HasTool(string toolName) => toolName is "qr.search_movements" or "qr.list_movement_types";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (toolName == "qr.list_movement_types")
        {
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { movement_types = Array.Empty<object>() }), null, 1));
        }
        LastArguments = arguments.Clone();
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new { movements = Array.Empty<object>() }),
            null,
            1));
    }

    public void Reset() => LastArguments = default;
}

sealed class FlakyMovementToolExecutor : IAiWorkflowToolExecutor
{
    public int ExecutionCount { get; private set; }
    public JsonElement LastArguments { get; private set; }
    public bool HasTool(string toolName) => toolName == "qr.search_movements";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ExecutionCount++;
        LastArguments = arguments.Clone();
        return Task.FromResult(ExecutionCount == 1
            ? new AiWorkflowToolExecution(toolName, AiToolExecutionStatus.ExecutionFailed,
                JsonSerializer.SerializeToElement(new { }), "Temporary test failure.", 1)
            : new AiWorkflowToolExecution(toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { movements = Array.Empty<object>() }), null, 1));
    }
}

sealed class PagedMovementToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => toolName == "qr.search_movements";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) => Task.FromResult(new AiWorkflowToolExecution(
        toolName,
        AiToolExecutionStatus.Success,
        JsonSerializer.SerializeToElement(new
        {
            movements = Enumerable.Range(1, 100).Select(index => new
            {
                movement_date = "2026-09-01T10:00:00",
                barcode = "PAROL30",
                medicine_name = "PAROL 500 MG 30 TABLET",
                qr_code = $"QR{index}",
                movement_type = "Reçeteli Satış"
            }),
            summary = new { total_count = 314 },
            has_more = true
        }),
        null,
        1));
}

sealed class DescribedMovementTypeToolExecutor : IAiWorkflowToolExecutor
{
    public JsonElement SearchArguments { get; private set; }
    public bool HasTool(string toolName) => toolName is "qr.search_movements" or "qr.list_movement_types";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (toolName == "qr.list_movement_types")
        {
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    movement_types = new[]
                    {
                        new { code = "DEPOT_IN", display_name = "Depo Giriş", direction = "entry" },
                        new { code = "RETAIL_OUT", display_name = "Perakende Satış", direction = "exit" }
                    }
                }), null, 1));
        }

        SearchArguments = arguments.Clone();
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new { movements = Array.Empty<object>() }), null, 1));
    }
}

sealed class ProductSaleSearchToolExecutor : IAiWorkflowToolExecutor
{
    public JsonElement LastArguments { get; private set; }
    public bool HasTool(string toolName) => toolName == "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        LastArguments = arguments.Clone();
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                sales = new[]
                {
                    new
                    {
                        reference_code = "P42", timestamp = "2026-09-01T10:00:00", sale_type = "prescription",
                        patient_name = "TEST", quantity = 7, gross_amount = 1000m, discount_amount = 0m,
                        net_amount = 1000m, record_id = 42,
                        lines = new[]
                        {
                            new { barcode = "PAROL30", product_name = "PAROL 500 MG 30 TABLET", gross_price = 100m, net_price = 100m },
                            new { barcode = "OTHER", product_name = "OTHER MEDICINE", gross_price = 900m, net_price = 900m }
                        }
                    }
                },
                total_count = 1,
                has_more = false
            }),
            null,
            1));
    }
}

sealed class LatestSalesPresentationToolExecutor : IAiWorkflowToolExecutor
{
    private static readonly decimal[] DiscountedAmounts =
        [0.06m, 2.52m, 102.11m, 1.67m, 3.56m, 248.99m, 2.62m, 0.75m, 1097.63m, 412.95m];

    public List<JsonElement> Arguments { get; } = [];
    public bool HasTool(string toolName) => toolName == "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        Arguments.Add(arguments.Clone());
        var offset = arguments.GetProperty("offset").GetInt32();
        var limit = arguments.GetProperty("limit").GetInt32();
        var discountedOnly = arguments.GetProperty("payment_types").EnumerateArray()
            .Any(value => value.GetString() == "discount");
        var totalCount = discountedOnly ? 1391 : 2547;
        var sales = Enumerable.Range(offset, Math.Min(limit, Math.Max(0, totalCount - offset))).Select(index => new
        {
            reference_code = $"P{153274 - index}",
            record_id = 153274 - index,
            record_type = "sale",
            timestamp = new DateTime(2026, 9, 4, 18, 52, 13).AddMinutes(-index * 5).ToString("O", CultureInfo.InvariantCulture),
            sale_type = index % 2 == 0 ? "retail" : "prescription",
            patient_name = index % 2 == 0 ? "" : "TEST HASTA",
            quantity = index % 3 + 1,
            gross_amount = 229.89m + index,
            discount_amount = discountedOnly ? DiscountedAmounts[index % DiscountedAmounts.Length] : index % 2 == 0 ? 0m : 5m,
            net_amount = 229.89m + index - (discountedOnly ? DiscountedAmounts[index % DiscountedAmounts.Length] : index % 2 == 0 ? 0m : 5m),
            total_amount = 229.89m + index,
            institution_amount = index % 2 == 0 ? 0m : 180m,
            status = "completed"
        }).ToArray();
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                sales,
                total_count = totalCount,
                offset,
                limit,
                has_more = offset + sales.Length < totalCount
            }),
            null,
            1));
    }
}

sealed class PreviousSalesContentsToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> DetailReferences { get; } = [];
    public List<string> PatientQueries { get; } = [];
    public List<string> ReportPatientTcs { get; } = [];

    public bool HasTool(string toolName) => toolName is "sale.search_records" or "sale.get_record" or
        "sale.search_patients" or "medula.patient_reports_search" or "medula.patient_medication_history_search" or
        "medula.patient_eligibility_get" or "medula.patient_examinations_search" or
        "medula.patient_off_label_approvals_search";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (toolName == "sale.search_records")
        {
            var limit = arguments.GetProperty("limit").GetInt32();
            var requestedTypes = arguments.GetProperty("sale_types").EnumerateArray()
                .Select(value => value.GetString()).Where(value => value is not null).ToArray();
            var saleType = requestedTypes.Contains("prescription", StringComparer.Ordinal) ? "prescription" : "retail";
            var sales = Enumerable.Range(0, limit).Select(index => new
            {
                reference_code = $"P{9000 - index}", record_id = 9000 - index, record_type = "sale",
                timestamp = new DateTime(2026, 9, 4, 18, 50, 0).AddMinutes(-index).ToString("O", CultureInfo.InvariantCulture),
                sale_type = saleType, patient_name = $"HASTA {index + 1}", quantity = 1,
                gross_amount = 100m + index, discount_amount = 0m, net_amount = 100m + index,
                total_amount = 100m + index, institution_amount = 100m + index, status = "completed"
            }).ToArray();
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { sales, total_count = 1285, offset = 0, limit, has_more = true }),
                null, 1));
        }

        if (toolName == "sale.search_patients")
        {
            var query = arguments.GetProperty("query").GetString() ?? string.Empty;
            PatientQueries.Add(query);
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    patients = query == "GÖNÜL ÇUBUKLU"
                        ? new[] { new { patient_record_id = 4123, patient_tc = "17752783112", name = "GÖNÜL ÇUBUKLU", phone_number = "" } }
                        : Array.Empty<object>()
                }), null, 1));
        }

        if (toolName == "medula.patient_reports_search")
        {
            var patientTc = arguments.GetProperty("patient_tc").GetString() ?? string.Empty;
            ReportPatientTcs.Add(patientTc);
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    message = "Hastanın Medula raporları hazır.",
                    tables = new[]
                    {
                        new
                        {
                            title = "Raporlar",
                            headers = new[] { "Rapor Takip No", "Başlangıç Tarihi", "Bitiş Tarihi", "Tanı" },
                            rows = new[] { new[] { "RPT-1", "01.01.2026", "31.12.2026", "TEST TANI" } },
                            total_count = 1
                        }
                    }
                }), null, 1));
        }

        var reference = arguments.GetProperty("reference_code").GetString()!;
        DetailReferences.Add(reference);
        var number = int.Parse(reference[1..], CultureInfo.InvariantCulture);
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                found = true,
                sale = new
                {
                    reference_code = reference, record_type = "sale", sale_type = "prescription",
                    timestamp = "2026-09-04T18:50:00",
                    patient_name = number == 9000 ? "GÖNÜL ÇUBUKLU" : $"HASTA {9001 - number}",
                    lines = new[]
                    {
                        new
                        {
                            barcode = $"869000000{number}",
                            product_name = number == 9000 ? "XANAX 0,5 MG 30 TABLET" : $"TEST İLAÇ {number}",
                            qr_code = $"QR-{number}",
                            gross_price = 100m, discount_amount = 0m, net_price = 100m
                        }
                    }
                }
            }), null, 1));
    }
}

sealed class PeriodAwareSalesToolExecutor : IAiWorkflowToolExecutor
{
    public List<JsonElement> Arguments { get; } = [];
    public bool HasTool(string toolName) => toolName == "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        Arguments.Add(arguments.Clone());
        var start = DateOnly.ParseExact(arguments.GetProperty("start_date").GetString()!, "yyyy-MM-dd");
        var end = DateOnly.ParseExact(arguments.GetProperty("end_date").GetString()!, "yyyy-MM-dd");
        var count = end.DayNumber - start.DayNumber >= 60 ? 20 : 8;
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                sales = Enumerable.Range(1, count).Select(index => new
                {
                    reference_code = $"P{index}", timestamp = "2026-09-01T10:00:00", sale_type = "prescription",
                    patient_name = "TEST", quantity = 1, gross_amount = 100m, discount_amount = 0m,
                    net_amount = 100m, record_id = index,
                    lines = new[]
                    {
                        new { barcode = "PAROL20", product_name = "PAROL 500 MG 20 TABLET", gross_price = 100m, net_price = 100m }
                    }
                }),
                total_count = count,
                has_more = false
            }),
            null,
            1));
    }
}

sealed class AmbiguousPeriodWorkflowToolExecutor : IAiWorkflowToolExecutor
{
    public List<JsonElement> SalesArguments { get; } = [];
    public bool HasTool(string toolName) => toolName is "product.search_by_name" or "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (toolName == "product.search_by_name")
        {
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    products = new[]
                    {
                        new { barcode = "50020", name = "PAROL 500 MG 20 TABLET", is_on_market = true },
                        new { barcode = "50030", name = "PAROL 500 MG 30 TABLET", is_on_market = true }
                    }
                }), null, 1));
        }

        SalesArguments.Add(arguments.Clone());
        var start = DateOnly.ParseExact(arguments.GetProperty("start_date").GetString()!, "yyyy-MM-dd");
        var end = DateOnly.ParseExact(arguments.GetProperty("end_date").GetString()!, "yyyy-MM-dd");
        var count = end.DayNumber - start.DayNumber >= 60 ? 20 : 8;
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                sales = Enumerable.Range(1, count).Select(index => new
                {
                    reference_code = $"P{index}", timestamp = "2026-09-01T10:00:00", sale_type = "prescription",
                    patient_name = "TEST", quantity = 1, gross_amount = 100m, discount_amount = 0m,
                    net_amount = 100m, record_id = index,
                    lines = new[]
                    {
                        new { barcode = "50020", product_name = "PAROL 500 MG 20 TABLET", gross_price = 100m, net_price = 100m }
                    }
                }),
                total_count = count,
                has_more = false
            }), null, 1));
    }
}

sealed class PatientPeriodWorkflowToolExecutor : IAiWorkflowToolExecutor
{
    public List<JsonElement> PatientArguments { get; } = [];
    public List<JsonElement> SalesArguments { get; } = [];
    public bool HasTool(string toolName) => toolName is "sale.search_patients" or "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (toolName == "sale.search_patients")
        {
            PatientArguments.Add(arguments.Clone());
            var query = arguments.GetProperty("query").GetString();
            var patients = string.Equals(query, "ADEM YILMAZ", StringComparison.Ordinal)
                ? new[]
                {
                    new { patient_record_id = 5252, patient_tc = "22222222222", name = "ADEM YILMAZ" },
                    new { patient_record_id = 5253, patient_tc = "33333333333", name = "ADEM YILMAZ" }
                }
                : new[] { new { patient_record_id = 4242, patient_tc = "11111111111", name = "EVREN ACAR" } };
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new { patients }), null, 1));
        }
        SalesArguments.Add(arguments.Clone());
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                sales = new[]
                {
                    new
                    {
                        reference_code = "P4242", timestamp = "2026-01-10T10:00:00", sale_type = "prescription",
                        patient_name = "EVREN ACAR", quantity = 1, gross_amount = 84.5m, discount_amount = 0m,
                        net_amount = 84.5m, record_id = 4242
                    }
                },
                total_count = 1,
                has_more = false
            }),
            null, 1));
    }
}

sealed class PatientRetryAndContentsToolExecutor : IAiWorkflowToolExecutor
{
    private bool failFirstSale = true;
    public List<JsonElement> SalesArguments { get; } = [];
    public bool HasTool(string toolName) => toolName is "sale.search_patients" or "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (toolName == "sale.search_patients")
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    patients = new[]
                    {
                        new { patient_record_id = 42, patient_tc = "11111111111", name = "FİKRET ACAR" },
                        new { patient_record_id = 43, patient_tc = "22222222222", name = "FİKRET AK" }
                    }
                }), null, 1));

        SalesArguments.Add(arguments.Clone());
        if (failFirstSale)
        {
            failFirstSale = false;
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.ExecutionFailed,
                JsonSerializer.SerializeToElement(new { }), "Temporary test failure.", 1));
        }
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                sales = new[]
                {
                    new
                    {
                        reference_code = "P42", record_id = 42, record_type = "sale",
                        timestamp = "2026-08-19T14:05:10", sale_type = "retail", patient_record_id = 42,
                        patient_name = "FİKRET ACAR", quantity = 3, gross_amount = 35m,
                        discount_amount = 0m, net_amount = 35m,
                        lines = new[]
                        {
                            new { barcode = "PAROL20", product_name = "PAROL 500 MG 20 TABLET", qr_code = "QR1", gross_price = 10m, discount_amount = 0m, net_price = 10m },
                            new { barcode = "PAROL20", product_name = "PAROL 500 MG 20 TABLET", qr_code = "QR2", gross_price = 10m, discount_amount = 0m, net_price = 10m },
                            new { barcode = "BELOC20", product_name = "BELOC 100 MG 20 TABLET", qr_code = "QR3", gross_price = 15m, discount_amount = 0m, net_price = 15m }
                        }
                    }
                },
                total_count = 1, offset = 0, limit = 200, has_more = false
            }), null, 1));
    }
}

sealed class LocalPriceWorkflowToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> ExecutedTools { get; } = [];
    public bool HasTool(string toolName) => toolName is "product.get" or "product.get_price_history";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ExecutedTools.Add(toolName);
        var output = toolName == "product.get"
            ? JsonSerializer.SerializeToElement(new
            {
                found = true,
                product = new
                {
                    summary = new { name = "BELOC 100 MG 20 TABLET", purchase_price = 55m, currency = "TRY" },
                    campaign_purchase_price = 50m
                }
            })
            : JsonSerializer.SerializeToElement(new
            {
                points = new[]
                {
                    new { sale_price = 80m, public_price = 78m, public_paid_price = 75m, warehouse_price = 52m, manufacturer_price = 48m },
                    new { sale_price = 84.5m, public_price = 82m, public_paid_price = 79m, warehouse_price = 55m, manufacturer_price = 50m }
                }
            });
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success, output, null, 1));
    }
}

sealed class CapturingSalesToolExecutor : IAiWorkflowToolExecutor
{
    public string? LastToolName { get; private set; }
    public JsonElement LastArguments { get; private set; }

    public bool HasTool(string toolName) => toolName == "sale.aggregate_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        LastToolName = toolName;
        LastArguments = arguments.Clone();
        var includeRefunds = arguments.TryGetProperty("include_refunds", out var refundsElement)
                             && refundsElement.ValueKind == JsonValueKind.True;
        var groups = new List<object>
        {
            new
            {
                group_key = "sale", sale_count = 2, refund_count = 0,
                quantity = 3, gross_revenue = 250.50m, net_revenue = 245.00m,
                institution_revenue = 125.00m
            }
        };
        if (includeRefunds)
        {
            groups.Add(new
            {
                group_key = "refund", sale_count = 0, refund_count = 1,
                quantity = -1, gross_revenue = -40m, net_revenue = -35m,
                institution_revenue = -10m
            });
        }

        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new { groups }),
            null,
            1));
    }
}

sealed class PrescriptionReportToolExecutor : IAiWorkflowToolExecutor
{
    public List<JsonElement> Arguments { get; } = [];
    public bool HasTool(string toolName) => toolName == "medula.aggregate_prescription_reports";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        Arguments.Add(arguments.Clone());
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                reported_count = 2,
                unreported_count = 1,
                total_count = 3
            }),
            null,
            1));
    }
}
sealed class LatestRefundToolExecutor : IAiWorkflowToolExecutor
{
    public bool HasTool(string toolName) => toolName == "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var records = new[]
        {
            JsonSerializer.SerializeToElement(new { reference_code = "P2", record_type = "sale", timestamp = "2026-09-03T10:00:00" }),
            JsonSerializer.SerializeToElement(new { reference_code = "G2", record_type = "refund", timestamp = "2026-09-03T09:00:00" }),
            JsonSerializer.SerializeToElement(new { reference_code = "P1", record_type = "sale", timestamp = "2026-09-02T10:00:00" }),
            JsonSerializer.SerializeToElement(new { reference_code = "G1", record_type = "refund", timestamp = "2026-09-01T09:00:00" })
        };
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName,
            AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new { sales = records, total_count = records.Length, has_more = false }),
            null,
            1));
    }
}

sealed class ScriptedTransport : IAiModelTransport
{
    private readonly Queue<AiModelResponse> responses;

    public ScriptedTransport(bool configured, params AiModelResponse[] responses)
    {
        IsConfigured = configured;
        this.responses = new Queue<AiModelResponse>(responses);
    }

    public bool IsConfigured { get; }
    public string ConfigurationDescription => IsConfigured ? "Test transport" : "Not configured";
    public List<AiModelRequest> Requests { get; } = [];

    public Task<AiModelResponse> SendAsync(AiModelRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(responses.Dequeue());
    }

    public static AiModelResponse ToolCalls(params (string CallId, string Name, string Arguments)[] calls)
    {
        var items = calls.Select(call => JsonSerializer.SerializeToElement(new
        {
            type = "function_call", call_id = call.CallId, name = call.Name, arguments = call.Arguments
        })).ToArray();
        return new AiModelResponse(Guid.NewGuid().ToString("N"), string.Empty,
            calls.Select(call => new AiToolCall(call.CallId, call.Name, call.Arguments)).ToArray(), items, new AiUsage(10, 0, 0, 2, 0));
    }

    public static AiModelResponse Final(string text) =>
        new(Guid.NewGuid().ToString("N"), text, [], [], new AiUsage(8, 0, 0, 4, 0));
}

sealed class FixedRouter(AiRouteDecision decision) : IAiRouter
{
    public Task<AiRouteDecision> RouteAsync(AiRequestContext context, CancellationToken cancellationToken = default) => Task.FromResult(decision);
}

sealed class ThrowingRouter(Exception exception) : IAiRouter
{
    public Task<AiRouteDecision> RouteAsync(AiRequestContext context, CancellationToken cancellationToken = default) =>
        Task.FromException<AiRouteDecision>(exception);
}

sealed class FixedResponseHandler(HttpStatusCode statusCode, string responseBody, string? requestId = null) : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody)
        };
        if (!string.IsNullOrWhiteSpace(requestId)) response.Headers.Add("x-request-id", requestId);
        return response;
    }
}

sealed class TestPrompts : IAiPromptProvider
{
    public string AssistantCore => "Test assistant.";
    public string Router => "Test router.";
    public string WorkflowRouter => "Test workflow router.";
    public string DebugToolGap => "Test missing tool.";
}

sealed class AllowPermissionService : IAiPermissionService
{
    public bool CanExecute(AiRequestContext context, AiToolDefinition definition) => true;
}

sealed class DenyPermissionService : IAiPermissionService
{
    public bool CanExecute(AiRequestContext context, AiToolDefinition definition) => false;
}

sealed class ParallelProbe
{
    private int current;
    private int maximum;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int MaximumConcurrency => maximum;

    public async Task<object> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref current);
        maximum = Math.Max(maximum, active);
        if (active == 2) ready.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        Interlocked.Decrement(ref current);
        return new { ok = true };
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class QrAnalysisToolExecutor : IAiWorkflowToolExecutor
{
    public JsonElement AggregateArguments { get; private set; }
    public List<JsonElement> AggregateArgumentCalls { get; } = [];
    public JsonElement AuditArguments { get; private set; }

    public bool HasTool(string toolName) => toolName is "qr.aggregate_movements" or "qr.audit_movement_chains";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (toolName == "qr.aggregate_movements")
        {
            AggregateArguments = arguments.Clone();
            AggregateArgumentCalls.Add(arguments.Clone());
            return Task.FromResult(new AiWorkflowToolExecution(toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    groups = new[] { new { group_key = "10", group_label = "10:00–10:59", movement_count = 4, entry_count = 3, exit_count = 1, distinct_qr_count = 4 } },
                    total_group_count = 1,
                    scanned_movement_count = 4,
                    truncated = false
                }), null, 1));
        }

        AuditArguments = arguments.Clone();
        return Task.FromResult(new AiWorkflowToolExecution(toolName, AiToolExecutionStatus.Success,
            JsonSerializer.SerializeToElement(new
            {
                issues = new[] { new { movement_date = "2026-09-01T10:00:00", qr_code = "QR1", medicine_name = "TEST", issue_type = "repeated", movement_type = "Depo Giriş", direction = "entry", reference_code = "R1", detail = "Tekrar" } },
                scanned_movement_count = 10,
                truncated = false
            }), null, 1));
    }
}

sealed class MedulaPatientContextToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> PatientQueries { get; } = [];
    public List<string> MedicationHistoryPatientTcs { get; } = [];
    public List<string> MedicationListPatientTcs { get; } = [];
    public List<string> ReportPatientTcs { get; } = [];
    public List<bool> ReportIncludeExpired { get; } = [];
    public List<string> PrescriptionPatientTcs { get; } = [];
    public List<JsonElement> PrescriptionArguments { get; } = [];
    public List<int> AggregatePatientRecordIds { get; } = [];
    public int DiagnosisCalls { get; private set; }
    public bool ReturnEmptyReports { get; set; }

    public bool HasTool(string toolName) => toolName is
        "sale.search_patients" or "sale.aggregate_records" or
        "medula.patient_medication_history_search" or "medula.patient_medication_list_get" or "medula.patient_eligibility_get" or
        "medula.patient_reports_search" or "medula.patient_prescriptions_search" or "medula.patient_examinations_search" or
        "medula.patient_off_label_approvals_search" or "medula.diagnosis_search";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        object output;
        switch (toolName)
        {
            case "sale.search_patients":
                var query = arguments.GetProperty("query").GetString() ?? string.Empty;
                PatientQueries.Add(query);
                var patient = query switch
                {
                    "ASIM URAS YILMAZ" => new { patient_record_id = 8101, patient_tc = "15748006450", name = "ASIM URAS YILMAZ", phone_number = "" },
                    "EVREN ACAR" => new { patient_record_id = 8103, patient_tc = "12289124650", name = "EVREN ACAR", phone_number = "" },
                    _ => new { patient_record_id = 8102, patient_tc = "25411225316", name = "ÖZLEM DUMAN", phone_number = "" }
                };
                output = new { patients = new[] { patient } };
                break;
            case "medula.patient_medication_history_search":
                var patientTc = arguments.GetProperty("patient_tc").GetString() ?? string.Empty;
                MedicationHistoryPatientTcs.Add(patientTc);
                output = new
                {
                    success = true,
                    message = "Hastanın Medula ilaç geçmişi getirildi.",
                    items = new[]
                    {
                        new { prescription_date = "03.09.2026", prescription_number = "3PXYOGV", barcode = "8690000000001", medicine_name = "TEST İLAÇ", quantity = "1", report = "" }
                    },
                    total_count = 1
                };
                break;
            case "medula.patient_medication_list_get":
                var medicationListPatientTc = arguments.GetProperty("patient_tc").GetString() ?? string.Empty;
                MedicationListPatientTcs.Add(medicationListPatientTc);
                output = new
                {
                    success = true,
                    message = "Medula hasta ilaç listesi açıldı.",
                    tables = new[]
                    {
                        new
                        {
                            title = "Hasta İlaç Listesi",
                            headers = new[] { "Barkod", "İlaç", "Alınabilir Tarih" },
                            rows = new[] { new[] { "8690000000001", "TEST İLAÇ", "10.09.2026" } },
                            total_count = 1
                        }
                    }
                };
                break;
            case "medula.patient_reports_search":
                var reportPatientTc = arguments.GetProperty("patient_tc").GetString() ?? string.Empty;
                ReportPatientTcs.Add(reportPatientTc);
                ReportIncludeExpired.Add(arguments.TryGetProperty("include_expired", out var includeExpired) &&
                                         includeExpired.ValueKind == JsonValueKind.True);
                output = ReturnEmptyReports ? new
                {
                    success = true,
                    title = "Hasta Raporları",
                    message = "Hastanın Medula raporları hazır.",
                    fields = Array.Empty<object>(),
                    tables = Array.Empty<object>(),
                    editable_rows = Array.Empty<object>()
                } : new
                {
                    success = true,
                    message = "Hastanın Medula raporları hazır.",
                    tables = new[]
                    {
                        new
                        {
                            title = "Raporlar",
                            headers = new[] { "Rapor Takip No", "Rapor No", "Başlangıç Tarihi", "Bitiş Tarihi", "Tanı" },
                            rows = new[] { new[] { "RPT-1", "R-1", "01.01.2026", "31.12.2026", "TEST TANI" } },
                            total_count = 1
                        }
                    }
                };
                break;
            case "medula.patient_prescriptions_search":
                PrescriptionArguments.Add(arguments.Clone());
                var prescriptionPatientTc = arguments.GetProperty("patient_tc").GetString() ?? string.Empty;
                PrescriptionPatientTcs.Add(prescriptionPatientTc);
                output = new
                {
                    success = true,
                    message = "EVREN ACAR adlı hastanın 2 reçetesi bulundu; 1 tanesi Medula'da aktif.",
                    items = new[]
                    {
                        new { prescription_number = "3ACTIVE", prescription_uid = "00000000-0000-0000-0000-000000000001", version_number = 2, patient_tc = prescriptionPatientTc, patient_name = "EVREN ACAR", prescription_date = "03.09.2026", total_amount = "125,40", is_live_on_medula = true, is_stored_locally = true },
                        new { prescription_number = "3LOCAL", prescription_uid = "00000000-0000-0000-0000-000000000002", version_number = 1, patient_tc = prescriptionPatientTc, patient_name = "EVREN ACAR", prescription_date = "02.01.2026", total_amount = "98,10", is_live_on_medula = false, is_stored_locally = true }
                    },
                    total_count = 2
                };
                break;
            case "sale.aggregate_records":
                AggregatePatientRecordIds.Add(arguments.TryGetProperty("patient_record_id", out var patientRecordId) &&
                                              patientRecordId.TryGetInt32(out var parsedPatientRecordId)
                    ? parsedPatientRecordId
                    : 0);
                output = new
                {
                    groups = new[]
                    {
                        new { group_key = "8690000000001", group_label = "TEST İLAÇ", record_count = 1, sale_count = 1, refund_count = 0, quantity = 1, gross_revenue = 110m, discount_total = 10m, net_revenue = 100m, profit = 20m }
                    },
                    total_count = 1,
                    has_more = false
                };
                break;
            case "medula.diagnosis_search":
                DiagnosisCalls++;
                output = new { success = true, items = Array.Empty<object>(), total_count = 0 };
                break;
            default:
                output = new { success = false, message = "Beklenmeyen test aracı." };
                break;
        }
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(output), null, 1));
    }
}

sealed class FilteredLatestPrescriptionToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> Calls { get; } = [];
    public JsonElement SaleArguments { get; private set; }
    public JsonElement DetailArguments { get; private set; }

    public bool HasTool(string toolName) => toolName is
        "product.search_by_name" or "sale.search_records" or "medula.prescription_get" or
        "medula.prescriptions_search" or "medula.patient_prescriptions_search";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(toolName);
        object output;
        switch (toolName)
        {
            case "product.search_by_name":
                output = new
                {
                    products = new[]
                    {
                        new { barcode = "8699500000001", name = "VIDAPTIN MET 50/1000 MG 60 TABLET", is_on_market = true },
                        new { barcode = "8699500000002", name = "VIDAPTIN MET 50/850 MG 60 TABLET", is_on_market = true }
                    }
                };
                break;
            case "sale.search_records":
                SaleArguments = arguments.Clone();
                output = new
                {
                    sales = new[]
                    {
                        new { prescription_no = "RX-VID-OLD", timestamp = "2026-08-01T10:00:00", patient_tc = "10000000000" },
                        new { prescription_no = "RX-VID-LATEST", timestamp = "2026-09-02T15:30:00", patient_tc = "20000000000" }
                    },
                    total_count = 2,
                    has_more = false
                };
                break;
            case "medula.prescription_get":
                DetailArguments = arguments.Clone();
                output = new
                {
                    success = true,
                    message = "Seçilen ilacı içeren en son reçete açıldı.",
                    prescription_number = arguments.GetProperty("prescription_number").GetString(),
                    patient_tc = "20000000000",
                    patient_name = "TEST HASTA",
                    is_live_on_medula = false,
                    fields = new { prescription_date = "02.09.2026", qr_status = "Reçete Sonlandırıldı" },
                    drugs = new[]
                    {
                        new { barcode = "8699500000001", name = "VIDAPTIN MET 50/1000 MG 60 TABLET", quantity = "1" }
                    },
                    diagnoses = Array.Empty<object>(),
                    payments = new[] { new { label = "Reçete Tutarı", value = "250,00" } }
                };
                break;
            default:
                return Task.FromResult(new AiWorkflowToolExecution(
                    toolName, AiToolExecutionStatus.ValidationFailed,
                    JsonSerializer.SerializeToElement(new { error = "Unexpected tool." }), "Unexpected tool.", 0));
        }
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(output), null, 1));
    }
}

sealed class LatestMedulaPrescriptionToolExecutor : IAiWorkflowToolExecutor
{
    public List<string> Calls { get; } = [];
    public List<JsonElement> SearchArguments { get; } = [];
    public JsonElement DetailArguments { get; private set; }

    public bool HasTool(string toolName) => toolName is
        "medula.prescriptions_search" or "medula.patient_prescriptions_search" or
        "medula.prescription_get" or "medula.prescription_history" or
        "medula.prescription_warnings_get" or "medula.patient_treatment_scheme_get" or
        "sale.search_patients" or "sale.search_records";

    public Task<AiWorkflowToolExecution> ExecuteAsync(
        AiRequestContext request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(toolName);
        if (toolName == "medula.prescription_get")
        {
            DetailArguments = arguments.Clone();
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    message = "En son Medula reçetesi açıldı.",
                    prescription_number = arguments.GetProperty("prescription_number").GetString(),
                    electronic_prescription_number = "ERX-2",
                    patient_tc = "12289124650",
                    patient_name = "EVREN ACAR",
                    is_live_on_medula = true,
                    fields = new
                    {
                        prescription_date = "03.09.2026",
                        dispensing_date = "03.09.2026",
                        coverage = "4A Sigortalı",
                        patient_type = "Çalışan",
                        insurance_type = "Çalışan",
                        qr_status = "Reçete Sonlandırıldı",
                        invoice_type = "B Grubu",
                        doctor_registration_number = "13079826556",
                        doctor_first_name = "TEST",
                        doctor_last_name = "HEKİM"
                    },
                    drugs = new[] { new { barcode = "8690000000001", name = "TEST İLAÇ", quantity = "1", dose_count = "2", dose_amount = "1", period = "1", period_unit = "Gün", report = "", available_date = "", message = "" } },
                    diagnoses = new[] { new { code = "J00", description = "TEST TANI" } },
                    payments = new[] { new { label = "Reçete Tutarı", value = "100,00" } }
                }), null, 1));
        }
        if (toolName is "medula.prescriptions_search" or "medula.patient_prescriptions_search")
        {
            SearchArguments.Add(arguments.Clone());
            return Task.FromResult(new AiWorkflowToolExecution(
                toolName, AiToolExecutionStatus.Success,
                JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    message = "Reçeteler bulundu.",
                    items = new[]
                    {
                        new { prescription_number = "11111111", patient_tc = "12289124650", patient_name = "EVREN ACAR", prescription_date = "01.09.2026", last_transaction_date = "01.09.2026 10:00", medula_filter_date = "01.09.2026", invoice_type_code = "4", electronic_prescription_number = "" },
                        new { prescription_number = "22222222", patient_tc = "12289124650", patient_name = "EVREN ACAR", prescription_date = "03.09.2026", last_transaction_date = "03.09.2026 12:00", medula_filter_date = "03.09.2026", invoice_type_code = "4", electronic_prescription_number = "ERX-2" }
                    },
                    total_count = 2
                }), null, 1));
        }
        return Task.FromResult(new AiWorkflowToolExecution(
            toolName, AiToolExecutionStatus.ValidationFailed,
            JsonSerializer.SerializeToElement(new { error = "Unexpected tool." }), "Unexpected tool.", 0));
    }
}
