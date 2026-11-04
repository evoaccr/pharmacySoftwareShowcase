using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Workflows;

/// <summary>
/// Opt-in live router evaluation. Only the OpenAI transport is live. Production workflows
/// execute against synthetic read-only tool responses: no database, Medula, depot, or write
/// service is instantiated. Assertions inspect actual tool arguments, not reassuring prose.
/// </summary>
internal static class RouterReliabilityEvaluation
{
    private const string Barcode = "8699717010109";
    private const string OtherBarcode = "8699786030367";
    private sealed record Case(string Id, string Session, string Prompt, Func<Observation, string?> Check);
    private sealed record ToolCall(string Name, JsonElement Arguments);
    private sealed record Step(string Name, IReadOnlyDictionary<string, string> Arguments, AiWorkflowStepStatus Status, JsonElement Output);
    private sealed record Observation(AiWorkflowAssistantResult Result, IReadOnlyList<ToolCall> Tools,
        IReadOnlyList<Step> Steps, int ModelCalls);
    private sealed record CaseReport(string Id, string Prompt, bool Passed, string? Failure, string Message,
        IReadOnlyList<string> Queue, IReadOnlyList<ToolCall> Tools, IReadOnlyList<Step> Steps,
        int ModelCalls, int InputTokens, int CachedInputTokens, int OutputTokens, int Rows, string[] VisibleColumns, long Milliseconds,
        string? DebugSummary, IReadOnlyList<string> RawPlans, AiWorkflowContentPresentation? Content,
        IReadOnlyList<JsonElement> ModelOutputItems);

    public static async Task<int> RunAsync(string[] arguments)
    {
        VerifyScorerGuards();
        string? Option(string name)
        {
            var index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
        var model = Option("--model") ?? AiRouterModelCatalog.DefaultModelId;
        var outputPath = Option("--report") ?? Path.Combine(Path.GetTempPath(), "pharmacy-router-eval-" + Guid.NewGuid().ToString("N") + ".json");
        var selectedIds = (Option("--cases") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var configuration = new FixedAiConfigurationProvider(new AiConfiguration
        {
            RouterModel = model, DebugMode = true, StoreResponses = false,
            RouterReasoningEffort = "none", RequestTimeoutSeconds = 90
        });
        // This opt-in synthetic evaluator injects its test key explicitly. Production
        // continues to read only the portfolio's local Secrets section.
        var transport = new RecordingTransport(new OpenAiResponsesTransport(configuration,
            getSecrets: () => new pharmacyApp.Infrastructure.AiSecrets
            {
                OpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty
            }));
        if (!transport.IsConfigured)
        {
            Console.Error.WriteLine("Router evaluation requires OPENAI_API_KEY in the process environment.");
            return 2;
        }
        var workspace = Path.Combine(Path.GetTempPath(), "pharmacy-router-eval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var tools = new SyntheticTools();
        var steps = new List<Step>();
        var discovered = AiWorkflowRegistry.Discover();
        var registry = new AiWorkflowRegistry(discovered.Definitions.Select(definition =>
            new RecordingWorkflow(discovered.Get(definition.Name)!, steps)));
        var chat = new WorkflowRouterChatService(registry, transport, configuration,
            new AiConversationManager(configuration), new AiUsageTelemetry(Path.Combine(workspace, "usage.json")),
            new AiPromptProvider(), tools, new AiRequestCaptureStore(Path.Combine(workspace, "requests")));
        var reports = new List<CaseReport>();
        var allCases = Cases().Select(test => arguments.Contains("--english", StringComparer.Ordinal)
                ? test with { Prompt = EnglishPrompts[test.Id] } : test).ToArray();
        if (selectedIds.Any(id => allCases.All(test => test.Id != id)))
        {
            Console.Error.WriteLine("Unknown evaluation case ID; no prompts were sent.");
            return 2;
        }
        // A targeted follow-up needs its preceding turns, exactly as the full bank does.
        var cases = allCases.Where((test, index) => selectedIds.Count == 0 || selectedIds.Contains(test.Id)
            || allCases.Skip(index + 1).Any(later => later.Session == test.Session && selectedIds.Contains(later.Id))).ToArray();
        Console.WriteLine($"Router eval: {cases.Length} cases; model={model}; workflows={registry.RouterDefinitions.Count}; all tools synthetic.");
        foreach (var test in cases)
        {
            tools.Calls.Clear();
            steps.Clear();
            transport.Calls.Clear();
            var watch = Stopwatch.StartNew();
            AiWorkflowAssistantResult result;
            string? failure;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                result = await chat.SendAsync(new AiRequestContext(test.Session, test.Prompt,
                    new AiApplicationContext(CurrentModule: "home", CurrentUserId: "router-eval-user"),
                    new HashSet<string>()), timeout.Token);
                // Selection is a deterministic synthetic-user action, never another prompt.
                for (var selection = 0; result.PendingSelection is { } pending && selection < 4; selection++)
                {
                    if (pending.Options.Count == 0) break;
                    result = await chat.ContinueSelectionAsync(pending.Id, "router-eval-user",
                        [pending.Options[0].Id], timeout.Token);
                }
                failure = test.Check(new Observation(result, tools.Calls.ToArray(), steps.ToArray(), transport.Calls.Count));
                if (transport.Calls.Count > 1) failure ??= "Unexpected second model call during a deterministic queue.";
                if (result.PendingApproval is not null) failure ??= "Unexpected approval/advanced-planning pause for a supported read-only request.";
                if (result.PendingSelection is not null) failure ??= "The fixture selection did not finish the requested queue.";
                if (string.IsNullOrWhiteSpace(result.Message) && result.Table is null && result.Content is null)
                    failure ??= "The response was empty.";
                if (steps.Any(step => step.Status is AiWorkflowStepStatus.Failed or AiWorkflowStepStatus.MissingTool))
                    failure ??= "Workflow failed against the synthetic fixture; inspect step and tool contract.";
            }
            catch (Exception exception)
            {
                result = new AiWorkflowAssistantResult("Evaluation exception: " + exception.GetType().Name);
                failure = exception.Message;
            }
            reports.Add(new CaseReport(test.Id, test.Prompt, failure is null, failure, result.Message,
                result.WorkflowQueue ?? [], tools.Calls.ToArray(), steps.ToArray(), transport.Calls.Count,
                transport.Calls.Sum(call => call.Usage.InputTokens), transport.Calls.Sum(call => call.Usage.CachedInputTokens),
                transport.Calls.Sum(call => call.Usage.OutputTokens),
                result.Table?.Rows.Count ?? 0,
                result.Table?.Columns.Where(column => column.IsVisibleByDefault).Select(column => column.Key).ToArray() ?? [],
                watch.ElapsedMilliseconds, result.DebugSummary, transport.Calls.Select(call => call.Text).ToArray(), result.Content,
                transport.Calls.SelectMany(call => call.OutputItems).ToArray()));
            Console.WriteLine($"{(failure is null ? "PASS" : "FAIL")} {test.Id}: calls={transport.Calls.Count}, input={reports[^1].InputTokens}, queue=[{string.Join(",", result.WorkflowQueue ?? [])}] {(failure ?? "")}");
            await SaveReportAsync();
        }
        Console.WriteLine($"Evaluation: {reports.Count(report => report.Passed)}/{reports.Count} passed; report={Path.GetFullPath(outputPath)}");
        return reports.All(report => report.Passed) ? 0 : 1;

        Task SaveReportAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            return File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
            {
                model, generated_utc = DateTimeOffset.UtcNow,
                scope = "Live OpenAI router; synthetic local tools only. No real-data correctness or live Medula/depot claim.",
                workflow_count = registry.RouterDefinitions.Count,
                description_characters = registry.RouterDefinitions.Sum(definition => definition.Description.Length),
                parameter_description_characters = registry.RouterDefinitions.Sum(definition => definition.Parameters?.Sum(pair => pair.Key.Length + pair.Value.Length) ?? 0),
                passed = reports.Count(report => report.Passed), total = reports.Count,
                model_calls = reports.Sum(report => report.ModelCalls), input_tokens = reports.Sum(report => report.InputTokens),
                cached_input_tokens = reports.Sum(report => report.CachedInputTokens),
                output_tokens = reports.Sum(report => report.OutputTokens), results = reports
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static IEnumerable<Case> Cases()
    {
        static string? Workflow(Observation o, params string[] alternatives) =>
            alternatives.Any(name => o.Steps.Any(step => step.Name == name)) ? null : "Missing intended workflow: " + string.Join(" or ", alternatives);
        static string? Tool(Observation o, string name, Func<JsonElement, bool>? check = null) =>
            o.Tools.Any(call => call.Name == name && (check?.Invoke(call.Arguments) ?? true)) ? null : "Missing tool or requested filter: " + name;
        static string? Sales(Observation o, Func<JsonElement, bool>? check = null) =>
            Tool(o, "sale.search_records", check) ?? (o.TableRows() > 0 ? null : "No sale rows displayed.");
        static string? NoData(Observation o) => o.Tools.Count == 0 && o.Result.Table is null
            && o.Result.ModuleActions?.Count is not > 0 && !string.IsNullOrWhiteSpace(o.Result.Message)
            ? null : "Expected a meaningful direct answer without an unrelated data workflow.";
        static string? Field(Observation o, string field) => o.Result.Table?.Columns.Any(c => c.Key == field) == true
            || o.Result.Content is { } content && (field switch
            {
                "sale_price" => content.Items.Any(item => (item.Value + item.Detail).Contains("90", StringComparison.Ordinal)),
                "active_ingredient" => content.Items.Any(item => (item.Value + item.Detail).Contains("PARASETAMOL", StringComparison.Ordinal)),
                _ => false
            }) ? null : "Missing requested output field: " + field;
        static string? All(params string?[] failures) => failures.FirstOrDefault(failure => failure is not null);
        static Case Single(string id, string prompt, Func<Observation, string?> check) => new(id, id, prompt, check);

        yield return Single("greeting", "Merhaba, sen kimsin?", o => All(NoData(o),
            Matches(o.Result.Message, @"\b(?:assistant|asistan)\b")
                ? null : "Greeting did not introduce the assistant."));
        yield return Single("explanation", "Net hasılatla brüt hasılatın farkını kısaca anlat.", o => All(NoData(o),
            o.Result.Message.Contains("indirim", StringComparison.OrdinalIgnoreCase) || o.Result.Message.Contains("iskonto", StringComparison.OrdinalIgnoreCase) || o.Result.Message.Contains("discount", StringComparison.OrdinalIgnoreCase)
                ? null : "Net versus gross explanation omitted discounts."));
        yield return Single("bare_medicine", "Parol", o => All(NoData(o),
            Matches(o.Result.Message, @"\b(?:what|which|clarify|specify|would you like|ne|hangi|belirt\w*)\b")
                ? null : "A bare medicine name did not ask what information was wanted."));
        yield return Single("unsupported_translate", "Bütün satışlarımızın içeriğini önce Çinceye sonra Japoncaya çevir ve her hastaya e-posta gönder.", o => All(NoData(o),
            Matches(o.Result.Message, @"cannot|can['’]?t|unable|not (?:able|available|supported)|do not have|don['’]?t have|yapamam|desteklenm|yeteneğim yok|erişimim yok")
                ? null : "Unsupported translation/email request did not explain the capability limit."));
        yield return Single("discounted_sales", "Son 7 iskontolu satışı listele.", o => Sales(o, a => ArrayHas(a, "payment_types", "discount") && Number(a, "limit") == 7));
        yield return Single("retail_sales", "En son 9 perakende satış işlemini getir.", o => Sales(o, a => ArrayHas(a, "sale_types", "retail") && Number(a, "limit") == 9));
        yield return Single("prescription_sales", "Son 8 reçeteli satışı göster.", o => Sales(o, a => ArrayHas(a, "sale_types", "prescription") && Number(a, "limit") == 8));
        yield return Single("debt_sales", "Son 20 gündeki veresiye satışları göster.", o => Sales(o, a => Boolean(a, "is_debted") && RangeDays(a) == 20));
        yield return Single("marked_sales", "Bu ay işaretlenmiş satışları listele.", o => Sales(o, a => Boolean(a, "marked_only")
            && Text(a, "start_date") == DateTime.Today.ToString("yyyy-MM-01") && Text(a, "end_date") == DateTime.Today.ToString("yyyy-MM-dd")));
        yield return Single("sale_amount", "Son 30 günde toplamı 500 TL üzerinde olan perakende satışları listele.", o => Sales(o, a => Number(a, "minimum_total") is >= 500 and <= 500.01m
            && ArrayHas(a, "sale_types", "retail") && RangeDays(a) == 30));
        yield return Single("sale_detail", "P9000 satışının içindeki ilaçları göster.", o => Tool(o, "sale.get_record", a => Text(a, "reference_code") == "P9000"));
        yield return Single("retail_revenue", "Geçen takvim ayında yalnızca perakende satışların net hasılatı kaç TL?", o => All(
            Tool(o, "sale.aggregate_records", a => ArrayHas(a, "sale_types", "retail") && Text(a, "start_date") == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1).ToString("yyyy-MM-dd") && Text(a, "end_date") == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1).ToString("yyyy-MM-dd")),
            o.Tools.Count(call => call.Name == "sale.aggregate_records") == 1 ? null : "A single-period question incorrectly queried comparison periods."));
        yield return Single("medicine_quantity", Barcode + " barkodlu üründen son 50 günde kaç kutu sattım?", o => Tool(o, "sale.aggregate_records", a => ArrayHas(a, "barcodes", Barcode) && RangeDays(a) == 50));
        yield return Single("sales_by_day", "Son 14 günlük satış hasılatını gün gün göster.", o => Tool(o, "sale.aggregate_records", a => Text(a, "group_by") == "day" && RangeDays(a) == 14));
        yield return Single("sales_by_product", "Son 30 gün en çok satılan 5 ürünü kutu adedine göre sırala.", o => All(
            Tool(o, "sale.aggregate_records", a => Text(a, "group_by") == "product" && Text(a, "sort_by") == "quantity" && Number(a, "limit") == 5 && RangeDays(a) == 30),
            o.Steps.Any(step => step.Name == "urun_satis_siralamalarini_karsilastir")
                ? "A single quantity ranking incorrectly became two quantity/revenue rankings." : null));
        yield return Single("patient_latest", "Deniz Örnek'in en son aldığı ilacı göster.", o => All(Tool(o, "sale.search_patients", a => Text(a, "query").Contains("DENİZ ÖRNEK", StringComparison.Ordinal)), Tool(o, "sale.search_records", a => Number(a, "patient_record_id") == 701 && Number(a, "limit") == 1 && Boolean(a, "include_lines"))));
        yield return Single("patient_sales", "Deniz Örneğin son 60 gündeki satışlarını getir.", o => Tool(o, "sale.search_records", a => Number(a, "patient_record_id") == 701 && RangeDays(a) == 60));
        yield return Single("patient_contents", "Deniz Örnek'in son 1 yılda aldığı ilaçları kaç kutu aldığını göster.", o => All(Workflow(o, "hasta_urun_ozeti"), Tool(o, "sale.aggregate_records", a => Number(a, "patient_record_id") == 701 && Text(a, "group_by") == "product" && RangeDays(a) is 365 or 366)));
        yield return Single("patient_phone", "Deniz Örnek telefon numarası", o => Workflow(o, "hasta_iletisim_bilgisi_getir"));
        yield return Single("stock_shortage", Barcode + " barkodlu ilaçtan kaç eksiğimiz var?", o => All(Tool(o, "product.get_analysis", a => Text(a, "barcode") == Barcode), Field(o, "missing_stock_quantity")));
        yield return Single("purchase_quantity", Barcode + " barkodlu ilaçtan son 30 günlük satışına göre kaç kutu almalıyım?", o => All(Workflow(o, "alim_miktari_oner"), Tool(o, "product.get_analysis", a => Number(a, "days") == 30)));
        yield return Single("negative_stock", "Stok adedi negatif olan aktif ürünleri listele.", o => Tool(o, "product.search", a => Number(a, "stock_maximum") == -1 && Text(a, "market_status") == "active"));
        yield return Single("passive_stock", "Pasif olup rafta stoku kalan ürünleri göster.", o => Tool(o, "product.search", a => Text(a, "market_status") == "passive" && Boolean(a, "only_in_stock")));
        yield return Single("catalog_price", Barcode + " barkodlu ürünün sadece kayıtlı satış fiyatını göster.", o => All(Tool(o, "product.get", a => Text(a, "barcode") == Barcode), Field(o, "sale_price")));
        yield return Single("catalog_ingredient", Barcode + " barkodlu ürünün etken maddesi nedir?", o => All(Tool(o, "product.get"), Field(o, "active_ingredient")));
        yield return Single("price_history", Barcode + " barkodlu ürünün fiyat geçmişini getir.", o => Tool(o, "product.get_price_history", a => Text(a, "barcode") == Barcode));
        yield return Single("local_comparison", Barcode + " ve " + OtherBarcode + " barkodlarının kayıtlı fiyatlarını karşılaştır.", o => Workflow(o, "urunleri_karsilastir", "urun_operasyonel_karsilastirma"));
        yield return new Case("comparison_sort", "local_comparison", "Bu iki ürünü sadece bu listedeki satış fiyatına göre azalan sırala.", o => All(
            Workflow(o, "onceki_sonucu_isle"), o.Tools.Count == 0 ? null : "Sorting displayed products queried the database again.",
            o.Steps.FirstOrDefault(step => step.Name == "onceki_sonucu_isle")?.Output is { } output
                && output.TryGetProperty("rows", out var rows) && rows.GetArrayLength() == 2 && Text(rows[0], "barcode") == OtherBarcode
                && (o.Result.Table?.Rows.Count == 2 || o.Result.Content?.Items.Count == 2)
                ? null : "Displayed products were not sorted using the retained prices."));
        yield return Single("movements", Barcode + " son 20 gün depo giriş hareketlerini göster.", o => All(Workflow(o, "hareketleri_gor"), Tool(o, "qr.search_movements", a => RangeDays(a) == 20 && Text(a, "direction") == "entry" && Text(a, "address_type") == "depot")));
        yield return Single("module_navigation", "Ürün Kataloğu ile Satışlar ekranlarını yan yana aç.", o => o.Result.ModuleActions?.Any(action =>
            action.ModuleId == "workspace.modules" && action.Parameters.GetValueOrDefault("layout") == "side_by_side"
            && action.Parameters.GetValueOrDefault("module_ids", "").Split(',').Order().SequenceEqual(new[] { "products.catalog", "sales.retail-list" })) == true
            ? null : "No side-by-side workspace action for the exact two requested modules.");

        yield return new Case("discount_chain_1", "discount_chain", "Son 6 iskontolu satışı göster.", o => All(
            Sales(o, a => ArrayHas(a, "payment_types", "discount") && Number(a, "limit") == 6),
            o.Result.Table?.Rows is { Count: 6 } saleRows && ExactReferences(saleRows.Select(row => row.GetValueOrDefault("reference_code", "")))
                ? null : "The retained list must contain the exact six fixture sales."));
        yield return new Case("discount_chain_2", "discount_chain", "Bunlardaki toplam indirim kaç TL?", ExactDiscountTotal);
        yield return new Case("discount_chain_3", "discount_chain", "Önceki satış listesindeki ilaçları göster.", ExactRetainedContents);
        yield return new Case("period_chain_1", "period_chain", "Parol son 12 gün satışları", o => Tool(o, "sale.search_records", a => ArrayHas(a, "barcodes", Barcode) && RangeDays(a) == 12));
        yield return new Case("period_chain_2", "period_chain", "60 günlük", o => All(Tool(o, "sale.search_records", a => ArrayHas(a, "barcodes", Barcode) && RangeDays(a) == 60), o.ModelCalls == 0 ? null : "Period-only follow-up required another model call."));
        yield return new Case("patient_chain_1", "patient_chain", "Deniz Örnek'in son 30 gündeki satışlarını listele.", o => Sales(o, a => Number(a, "patient_record_id") == 701));
        yield return new Case("patient_chain_2", "patient_chain", "Aynı hastanın satışlarını içerikleriyle göster.", o => Tool(o, "sale.search_records", a => Number(a, "patient_record_id") == 701 && Boolean(a, "include_lines")));
        yield return new Case("scope_chain_1", "scope_chain", "Son 30 gün sadece perakende satışların net hasılatını getir, iadeler düşülmüş olsun.", o => Tool(o, "sale.aggregate_records", a => ArrayHas(a, "sale_types", "retail") && Boolean(a, "include_refunds")));
        yield return new Case("scope_chain_2", "scope_chain", "Bu tutarın içinde reçeteli satış var mı?", o => o.Tools.Count == 0
            && Matches(o.Result.Message, @"retail|perakende")
            && Matches(o.Result.Message, @"\bno\b|not includ|doesn['’]?t includ|exclud|only retail|retail[- ]only|hayır|dahil değil|yalnızca perakende|sadece perakende")
            && !Matches(o.Result.Message, @"\byes\b|also includes? prescription|includes? both|\bevet\b")
            ? null : "Previous-result scope answer did not clearly exclude prescription sales, or ran a new data query.");
        yield return new Case("filter_chain_1", "filter_chain", "Son 6 satışı göster.", o => Sales(o));
        yield return new Case("filter_chain_2", "filter_chain", "Yalnızca bu tablodaki reçeteli olanları bırak.", o => All(
            Workflow(o, "onceki_sonucu_isle"), o.Tools.Count == 0 ? null : "Filtering displayed sales reran a different database query.",
            o.Result.Table?.Rows is { Count: 3 } filtered && filtered.All(row => Matches(row.GetValueOrDefault("sale_type", ""), @"^(?:prescription|reçeteli)$"))
                && filtered.Select(row => row.GetValueOrDefault("reference_code", "")).Order().SequenceEqual(new[] { "P8995", "P8997", "P8999" })
                ? null : "Expected exactly the three prescribed sales from the retained six-row table."));
    }

    private static readonly IReadOnlyDictionary<string, string> EnglishPrompts = new Dictionary<string, string>
    {
        ["greeting"] = "Hello, who are you?",
        ["explanation"] = "Briefly explain the difference between gross sales revenue and net sales revenue.",
        ["bare_medicine"] = "Parol",
        ["unsupported_translate"] = "Translate the contents of every sale first into Chinese and then Japanese and email them to every patient.",
        ["discounted_sales"] = "List the last 7 discounted sales transactions.",
        ["retail_sales"] = "Show the latest 9 retail sales transactions.",
        ["prescription_sales"] = "Show the last 8 prescription sales.",
        ["debt_sales"] = "Show sales on credit in the last 20 days.",
        ["marked_sales"] = "List marked sales this month.",
        ["sale_amount"] = "List retail sales over 500 TRY in the last 30 days.",
        ["sale_detail"] = "Show the medicines inside sale P9000.",
        ["retail_revenue"] = "What was the net revenue from retail sales only last calendar month?",
        ["medicine_quantity"] = "How many boxes of barcode " + Barcode + " did I sell in the last 50 days?",
        ["sales_by_day"] = "Show daily sales revenue for the last 14 days.",
        ["sales_by_product"] = "Rank the top 5 best-selling medicines by box quantity over the last 30 days.",
        ["patient_latest"] = "Show the medicine Deniz Örnek bought most recently.",
        ["patient_sales"] = "Show Deniz Örnek's sales in the last 60 days.",
        ["patient_contents"] = "Show which medicines Deniz Örnek bought in the last year and how many boxes of each.",
        ["patient_phone"] = "What is Deniz Örnek's registered phone number?",
        ["stock_shortage"] = "How many boxes are we short of for barcode " + Barcode + "?",
        ["purchase_quantity"] = "How many boxes of barcode " + Barcode + " should I buy based on the last 30 days of sales?",
        ["negative_stock"] = "List active products with negative stock quantities.",
        ["passive_stock"] = "Show inactive products with stock still on the shelf.",
        ["catalog_price"] = "Show only the registered selling price of barcode " + Barcode + ".",
        ["catalog_ingredient"] = "What is the active ingredient of barcode " + Barcode + "?",
        ["price_history"] = "Show the price history of barcode " + Barcode + ".",
        ["local_comparison"] = "Compare registered prices of barcodes " + Barcode + " and " + OtherBarcode + ".",
        ["comparison_sort"] = "Sort just these two displayed products by their selling prices, highest first.",
        ["movements"] = "Show depot stock entries for barcode " + Barcode + " in the last 20 days.",
        ["module_navigation"] = "Open Product Catalog and Sales screens side by side.",
        ["discount_chain_1"] = "Show the last 6 discounted sales.",
        ["discount_chain_2"] = "What is the total discount on these displayed sales?",
        ["discount_chain_3"] = "Show the medicines inside the sales from the previous sales list.",
        ["period_chain_1"] = "Parol sales in the last 12 days",
        ["period_chain_2"] = "60 days",
        ["patient_chain_1"] = "List Deniz Örnek's sales in the last 30 days.",
        ["patient_chain_2"] = "Show the same patient's sales with their line contents.",
        ["scope_chain_1"] = "Show net retail-only sales revenue over the last 30 days, after subtracting refunds.",
        ["scope_chain_2"] = "Does that amount include prescription sales?",
        ["filter_chain_1"] = "Show the last 6 sales.",
        ["filter_chain_2"] = "Keep only the prescription sales already in this table; do not search for other sales."
    };

    private static bool Matches(string? text, string pattern) => Regex.IsMatch(text ?? string.Empty, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void VerifyScorerGuards()
    {
        foreach (var (text, expected) in new[]
        {
            ("Total discount: 21 TRY", true), ("Total discount: 21.00 TL", true),
            ("Toplam iskonto: 21,00 ₺", true), ("Total discount: 121 TRY", false),
            ("Total discount: 210 TRY", false), ("Total discount: 21.50 TRY", false),
            ("Total discount: 1,021.00 TRY", false), ("21 sales were discounted.", false)
        })
        {
            var observation = new Observation(new AiWorkflowAssistantResult(text), [], [], 0);
            if (ShowsDiscountTotal(observation) != expected)
                throw new InvalidOperationException("Discount scorer numeric-boundary regression.");
        }
    }

    private static bool ExactReferences(IEnumerable<string> references) => references.Order(StringComparer.Ordinal)
        .SequenceEqual(Enumerable.Range(0, 6).Select(index => "P" + (9000 - index)).Order(StringComparer.Ordinal));

    private static bool TryAmount(string value, out decimal amount)
    {
        var cleaned = Regex.Replace(value, @"\b(?:TRY|TL)\b|₺|\s", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!Regex.IsMatch(cleaned, @"^[+-]?\d+(?:[.,]\d+)*$", RegexOptions.CultureInvariant))
        {
            amount = 0;
            return false;
        }
        var separator = Math.Max(cleaned.LastIndexOf('.'), cleaned.LastIndexOf(','));
        if (separator >= 0 && cleaned.Length - separator - 1 is 1 or 2)
            cleaned = cleaned[..separator].Replace(".", "").Replace(",", "") + "." + cleaned[(separator + 1)..];
        else cleaned = cleaned.Replace(".", "").Replace(",", "");
        return decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    private static bool ShowsDiscountTotal(Observation observation, string? alias = null)
    {
        bool DiscountLabel(string? text) => Matches(text, @"discount|indirim|iskonto");
        if (observation.Result.Table is { } table)
        {
            var keys = table.Columns.Where(column => column.Key == alias || DiscountLabel(column.Key + " " + column.Label)).Select(column => column.Key);
            if (keys.Any(key => table.Rows.Any(row => TryAmount(row.GetValueOrDefault(key, ""), out var total) && total == 21))) return true;
        }
        if (observation.Result.Content?.Items.Any(item => DiscountLabel(item.Title + " " + item.Subtitle)
            && TryAmount(item.Value ?? "", out var total) && total == 21) == true) return true;
        if (!DiscountLabel(observation.Result.Message)) return false;
        // A currency token has numeric boundaries: 121, 210 and 21.50 must not pass.
        return Regex.Matches(observation.Result.Message, @"(?<![\w.,])(?<amount>\d+(?:[.,]\d+)*)\s*(?:₺|TRY\b|TL\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Any(match => TryAmount(match.Groups["amount"].Value, out var amount) && amount == 21);
    }

    private static string? ExactDiscountTotal(Observation observation)
    {
        const string failure = "Did not show exactly the displayed six sales' discount sum (1+2+3+4+5+6 = 21).";
        if (observation.Tools.Count != 0) return failure;
        var calculations = observation.Steps.Where(step => step.Name == PreviousResultWorkflow.WorkflowName).ToArray();
        if (calculations.Length == 0) return ShowsDiscountTotal(observation) ? null : failure;
        foreach (var step in calculations)
        {
            if (!step.Arguments.TryGetValue("metrics_json", out var metricsJson)) continue;
            try
            {
                using var metrics = JsonDocument.Parse(metricsJson);
                if (metrics.RootElement.ValueKind != JsonValueKind.Array
                    || !step.Output.TryGetProperty("metadata", out var metadata) || Number(metadata, "source_row_count") != 6
                    || !step.Output.TryGetProperty("rows", out var rows) || rows.GetArrayLength() != 1) continue;
                foreach (var metric in metrics.RootElement.EnumerateArray())
                {
                    var alias = Text(metric, "alias");
                    if (Text(metric, "op") != "sum" || Text(metric, "field") is not ("discount_amount" or "discount_total")) continue;
                    if (rows[0].TryGetProperty(alias, out var value) && TryAmount(value.ToString(), out var total)
                        && total == 21 && ShowsDiscountTotal(observation, alias)) return null;
                }
            }
            catch (JsonException) { }
        }
        return failure;
    }

    private static string? ExactRetainedContents(Observation observation)
    {
        var calls = observation.Tools.Where(call => call.Name == "sale.get_record").ToArray();
        if (observation.Tools.Count != 6 || calls.Length != 6 || !ExactReferences(calls.Select(call => Text(call.Arguments, "reference_code"))))
            return "Contents must load each of the six retained sales exactly once, without a new search or unrelated tool.";
        if (observation.Result.Table?.Rows is not { Count: 6 } rows
            || !ExactReferences(rows.Select(row => row.GetValueOrDefault("reference_code", "")))
            || rows.Any(row => string.IsNullOrWhiteSpace(row.GetValueOrDefault("medicine_name") ?? row.GetValueOrDefault("product_name"))))
            return "The contents table did not preserve the exact six sale references and their medicine rows.";
        return null;
    }

    private static int TableRows(this Observation observation) => observation.Result.Table?.Rows.Count ?? 0;
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static decimal Number(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var number) ? number : -1;
    private static bool Boolean(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static bool ArrayHas(JsonElement value, string name, string expected) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Array && item.EnumerateArray().Any(element => element.ValueKind == JsonValueKind.String && element.GetString() == expected);
    private static int RangeDays(JsonElement value) => DateOnly.TryParse(Text(value, "start_date"), out var start) && DateOnly.TryParse(Text(value, "end_date"), out var end) ? end.DayNumber - start.DayNumber : -1;

    private sealed class RecordingTransport(IAiModelTransport inner) : IAiModelTransport
    {
        public List<AiModelResponse> Calls { get; } = [];
        public bool IsConfigured => inner.IsConfigured;
        public string ConfigurationDescription => inner.ConfigurationDescription;
        public async Task<AiModelResponse> SendAsync(AiModelRequest request, CancellationToken cancellationToken = default)
        {
            var result = await inner.SendAsync(request, cancellationToken);
            Calls.Add(result);
            return result;
        }
    }

    private sealed class RecordingWorkflow(IAiWorkflow inner, List<Step> steps) : IAiWorkflow
    {
        public AiWorkflowDefinition Definition => inner.Definition;
        public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var result = await inner.ExecuteAsync(context, cancellationToken);
            steps.Add(new Step(Definition.Name, new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>()), result.Status, result.Output.Clone()));
            return result;
        }
    }

    private sealed class SyntheticTools : IAiWorkflowToolExecutor
    {
        public List<ToolCall> Calls { get; } = [];
        private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
        {
            "sale.search_records", "sale.aggregate_records", "sale.get_record", "sale.search_patients",
            "product.search", "product.search_by_name", "product.get", "product.get_analysis", "product.get_price_history",
            "product.compare", "product.get_equivalents", "qr.search_movements", "qr.list_movement_types"
        };
        public bool HasTool(string toolName) => Supported.Contains(toolName);

        public Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string toolName, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            if (!Supported.Contains(toolName)) throw new InvalidOperationException("Non-fixture tool blocked: " + toolName);
            Calls.Add(new ToolCall(toolName, arguments.Clone()));
            var barcode = Text(arguments, "barcode");
            if (barcode.Length == 0) barcode = Barcode;
            var product = new { barcode, name = "PAROL 500 MG 20 TABLET", is_on_market = true, active_ingredient = "PARASETAMOL", sale_price = 90m, purchase_price = 60m, shelf_stock_quantity = 23 };
            object output = toolName switch
            {
                "sale.search_patients" => new { patients = new[] { new { patient_record_id = 701, name = "DENİZ ÖRNEK", phone_number = "0000000000", patient_tc = "" } }.Where(_ => Text(arguments, "query") is "DENİZ ÖRNEK" or "DENİZ" or "ÖRNEK").ToArray() },
                "sale.search_records" => Sales(arguments),
                "sale.aggregate_records" => Aggregate(arguments),
                "sale.get_record" => SaleDetail(arguments),
                "product.search_by_name" => new { products = Text(arguments, "query").Contains("parol", StringComparison.OrdinalIgnoreCase)
                    ? new[] { product, product with { barcode = "8699717010093", name = "PAROL 500 MG 30 TABLET" } } : [product], total_count = 2 },
                "product.search" => new { products = new[] { product }, total_count = 1, has_more = false },
                "product.get" => new { found = true, product = new { summary = product } },
                "product.get_analysis" => new { found = true, barcode, average_monthly_sales = 16.67m, wanted_stock_months = 1.5m, target_stock_quantity = 25, missing_stock_quantity = 2, shelf_stock_quantity = 23, consigned_stock_quantity = 0, stock_comment = "alım öneriliyor" },
                "product.get_price_history" => new { found = true, points = new[] { new { date = "2026-01-01", sale_price = 80m, public_price = 75m }, new { date = "2026-10-01", sale_price = 90m, public_price = 85m } }, total_count = 2, has_more = false },
                "product.compare" => new { products = new[] { product, product with { barcode = OtherBarcode, name = "BELOC ZOK 50 MG 20 TABLET", sale_price = 120m } } },
                "product.get_equivalents" => new { found = true, barcodes = new[] { Barcode, OtherBarcode }, general_equivalents = new[] { Barcode, OtherBarcode }, sgk_equivalents = new[] { Barcode, OtherBarcode } },
                "qr.list_movement_types" => new { movement_types = new[] { new { code = "purchase", name = "Depo Girişi", direction = "entry" }, new { code = "sale", name = "Perakende Satış", direction = "exit" } } },
                "qr.search_movements" => new { movements = new[] { new { timestamp = DateTime.Today.AddDays(-1).ToString("O"), barcode, medicine_name = product.name, qr_code = "SYNTHETIC-QR", movement_type = "Depo Girişi", direction = "entry" } }, total_count = 1, has_more = false },
                _ => new { }
            };
            return Task.FromResult(new AiWorkflowToolExecution(toolName, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(output), null, 1));
        }

        private static object Sales(JsonElement arguments)
        {
            var offset = Math.Max(0, (int)Number(arguments, "offset"));
            var limit = Math.Clamp((int)Number(arguments, "limit"), 1, 200);
            const int total = 15;
            return new { sales = Enumerable.Range(offset, Math.Min(limit, Math.Max(0, total - offset))).Select(index => Sale(index, arguments)).ToArray(), total_count = total, offset, limit, has_more = offset + limit < total };
        }

        private static object SaleDetail(JsonElement arguments)
        {
            var reference = Text(arguments, "reference_code");
            if (reference.Length < 2 || reference[0] != 'P' || !int.TryParse(reference[1..], out var id) || id is < 8986 or > 9000)
                return new { found = false };
            return new { found = true, sale = Sale(9000 - id, arguments, reference) };
        }

        private static object Sale(int index, JsonElement arguments, string? reference = null)
        {
            var barcode = arguments.TryGetProperty("barcodes", out var barcodes) && barcodes.ValueKind == JsonValueKind.Array && barcodes.GetArrayLength() > 0 ? barcodes[0].GetString() ?? Barcode : Barcode;
            var line = new { barcode, product_name = "PAROL 500 MG 20 TABLET", medicine_name = "PAROL 500 MG 20 TABLET", quantity = 1, qr_code = "SYNTHETIC-" + index, gross_price = 100m, discount_amount = index + 1m, net_price = 99m - index, net_amount = 99m - index };
            return new
            {
                record_id = 9000 - index, reference_code = reference ?? $"P{9000 - index}", record_type = "sale",
                timestamp = DateTime.Today.AddDays(-1).AddHours(18).AddMinutes(-index).ToString("O", CultureInfo.InvariantCulture),
                sale_type = ArrayHas(arguments, "sale_types", "prescription") ? "prescription" : ArrayHas(arguments, "sale_types", "retail") ? "retail" : index % 2 == 0 ? "retail" : "prescription",
                patient_record_id = 701, patient_name = "DENİZ ÖRNEK", status = "completed", quantity = 1,
                gross_amount = 100m, discount_amount = index + 1m, net_amount = 99m - index, total_amount = 99m - index,
                institution_amount = 0, lines = new[] { line }
            };
        }

        private static object Aggregate(JsonElement arguments)
        {
            var groupBy = Text(arguments, "group_by");
            var groups = groupBy == "record_type" || groupBy == "none"
                ? new[] { Group("sale", "Satış", 12, 20, 1000m, 100m, 900m), Group("refund", "İade", 0, -2, -100m, 0, -100m) }.Take(Boolean(arguments, "include_refunds") ? 2 : 1)
                : new[] { Group(groupBy == "product" ? Barcode : "2026-10-24", groupBy == "product" ? "PAROL 500 MG 20 TABLET" : "24.10.2026", 12, 20, 1000m, 100m, 900m) };
            var array = groups.ToArray();
            return new { groups = array, total_count = array.Length, has_more = false };
        }
        private static object Group(string key, string label, int sales, int quantity, decimal gross, decimal discount, decimal net) => new
        {
            group_key = key, group_label = label, record_count = sales == 0 ? 1 : sales, sale_count = sales,
            refund_count = sales == 0 ? 1 : 0, quantity, gross_revenue = gross, discount_total = discount,
            net_revenue = net, institution_revenue = 0, profit = 100m, missing_cost_quantity = 0, entry_cost_total = 700m
        };
    }
}
