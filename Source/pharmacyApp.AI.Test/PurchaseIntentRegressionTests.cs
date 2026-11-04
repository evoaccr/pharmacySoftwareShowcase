using System.Diagnostics;
using System.Text.Json;
using pharmacyApp.AI.Tools.Product;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Products;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Conversation;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Providers;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Workflows;

internal static class PurchaseIntentRegressionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static readonly FixedAiConfigurationProvider Config = new(new AiConfiguration { DebugMode = true, StoreResponses = false });
    private static readonly ProductInventorySalesSnapshot Snapshot = new([
        new("A", "TEST A", 5, 30, 300), new("B", "TEST B", 50, 30, 600),
        new("C", "TEST C", 0, 15, 150), new("D", "TEST D", 10, 0, 0)], 4);

    private static WorkflowRouterChatService Chat(CountTransport transport, IAiWorkflowToolExecutor tools) =>
        new(AiWorkflowRegistry.Discover(), transport, Config, new AiConversationManager(Config),
            new AiUsageTelemetry(Path.Combine(Path.GetTempPath(), "purchase-tests", Guid.NewGuid()+".json")), new AiPromptProvider(), tools);
    private static AiRequestContext Request(string session, string prompt) => new(session, prompt,
        new AiApplicationContext(CurrentModule: "home", CurrentUserId: "test"), new HashSet<string>());

    public static async Task RunAsync()
    {
        var transport = new CountTransport(null);
        var tools = new InventoryTools(new InventorySalesAnalysisAiToolProvider((_, _, _, _) => Task.FromResult(Snapshot)).Tools.Single());
        var chat = Chat(transport, tools);
        foreach (var prompt in new[] { "purchase 10 arveles", "purchase 10 of that medication", "BUY 12 boxes of Arveles", "please order 8 packs of that medicine!" })
        {
            var answer = await chat.SendAsync(Request("orders", prompt));
            Check(answer.WorkflowQueue?.SequenceEqual(["siparis_istegini_tani"]) == true && answer.PendingApproval is null &&
                answer.Message.Contains("Arveles", StringComparison.OrdinalIgnoreCase) && answer.Message.Contains("yapılmadı"), "Explicit order must retain the name/count and explain unavailable execution: " + answer.Message);
        }
        Check(tools.Calls == 0 && transport.Calls == 0, "Order recognition must not call a provider, database or model.");
        var orphan = await chat.SendAsync(Request("new-session", "purchase 10 of that medication"));
        Check(orphan.Message.Contains("hangi ürünü"), "An orphan reference must ask for a medicine, not copy another session.");
        chat.Clear("orders");
        orphan = await chat.SendAsync(Request("orders", "purchase 10 of that medication"));
        Check(orphan.Message.Contains("hangi ürünü"), "Clear must forget unresolved purchase context.");
        foreach (var prompt in new[] { "what should I purchase?", "what should we buy?", "ne almalıyım?", "hangi ürünleri almalıyım?" })
        {
            var answer = await chat.SendAsync(Request(Guid.NewGuid().ToString(), prompt));
            Check(answer.WorkflowQueue?.Contains("stok_satis_kesisim_analizi") == true && answer.Table?.Rows.Count == 2 &&
                answer.Table.Columns.Any(column => column.Key == "recommended_purchase_quantity"), "Advice must render a replenishment table with pack counts: " + answer.Message);
            Check(answer.Message.Contains("30 günlük") && answer.Message.Contains("iadeler"), "Advice must explain target/period/refund/incoming-order assumptions.");
        }
        Check(transport.Calls == 0 && tools.Calls == 4, "Simple advice uses one read-only tool and zero router calls.");
        var tool = tools.Tool;
        async Task<JsonElement> Execute(string kind) => JsonSerializer.SerializeToElement(await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        { analysis_kind = kind, start_date = "2026-10-01", end_date = "2026-10-30", target_days = 30 })));
        var advice = await Execute("replenishment");
        Check(advice.GetProperty("rows")[0].GetProperty("barcode").GetString() == "A" &&
            advice.GetProperty("rows")[0].GetProperty("recommended_purchase_quantity").GetDecimal() == 25 &&
            advice.GetProperty("rows")[1].GetProperty("recommended_purchase_quantity").GetDecimal() == 15, "Exact positive stock deficits, not sold-out-only or already-sufficient products.");
        foreach (var kind in new[] { "dead_stock", "sold_over_stock", "sold_out_recently", "coverage" })
            Check((await Execute(kind)).GetProperty("rows").GetArrayLength() > 0, "Existing analysis modes remain usable.");
        var empty = new InventoryTools(new InventorySalesAnalysisAiToolProvider((_, _, _, _) => Task.FromResult(new ProductInventorySalesSnapshot([], 0))).Tools.Single());
        var noRows = await Chat(new(null), empty).SendAsync(Request("empty", "what should I purchase?"));
        Check(noRows.Table is null && noRows.Content is null && noRows.Message.Contains("bulunamadı"), "Empty advice needs a meaningful message, not an empty display.");
        var failed = new InventoryTools(tool) { Fail = true };
        var failure = await Chat(new(null), failed).SendAsync(Request("failed", "what should I purchase?"));
        Check(failure.Table is null && failure.Message.Contains("güvenilir bir alım önerisi"), "Database failure must not fabricate advice.");
        var translate = typeof(WorkflowRouterChatService).Assembly.GetType("pharmacyApp.Localization.UiText")?.GetMethod("Translate");
        if (translate is not null)
        {
            var order = await chat.SendAsync(Request("english", "purchase 10 arveles"));
            var translated = (string)translate.Invoke(null, [order.Message])!;
            Check(translated.Contains("Requested packs: 10") && translated.Contains("no purchase was made"), "Entire order response must be English: " + translated);
            Check(((string)translate.Invoke(null, [failure.Message])!).StartsWith("I could not read"), "Failure response must be English.");
        }
        Console.WriteLine("PASS purchase intent, reference isolation, recommendation math, empty/failure and localization regressions");
    }

    public static async Task LiveAsync(IAiModelTransport inner)
    {
        var counted = new CountTransport(inner);
        var tools = new InventoryTools(new InventorySalesAnalysisAiToolProvider((_, _, _, _) => Task.FromResult(Snapshot)).Tools.Single());
        var chat = Chat(counted, tools);
        foreach (var prompt in new[] { "purchase 10 arveles", "purchase 10 of that medication", "what should I purchase?",
            "Please suggest products to replenish for 20 days using the last 60 days of sales", "I'd like to order ten packs of Arveles", "10 kutu Arveles sipariş ver" })
        {
            var previous = counted.Calls;
            var result = await chat.SendAsync(Request("live", prompt));
            var advice = prompt.Contains("should") || prompt.Contains("suggest");
            Check(result.WorkflowQueue?.Contains(advice ? "stok_satis_kesisim_analizi" : "siparis_istegini_tani") == true,
                "Live planner must distinguish execution/advice: " + prompt + " / " + result.Message + " / " + result.DebugSummary);
            Check(counted.Calls - previous <= 1 && result.PendingApproval is null, "No unnecessary replan/approval for advice or nonexecuting order recognition.");
            if (advice) Check(result.Table is { Rows.Count: > 0 }, "Advice needs useful table rows.");
            if (prompt.StartsWith("Please suggest"))
                Check(tools.LastArguments.GetProperty("target_days").GetInt32() == 20 &&
                    DateOnly.Parse(tools.LastArguments.GetProperty("end_date").GetString()!).DayNumber - DateOnly.Parse(tools.LastArguments.GetProperty("start_date").GetString()!).DayNumber == 60,
                    "Model-planned advice must preserve the 20-day target and 60-day observation period.");
            if (prompt.Contains("ten packs") || prompt.StartsWith("10 kutu"))
                Check(result.Message.Contains("10") && result.Message.Contains("Arveles", StringComparison.OrdinalIgnoreCase), "Model-planned orders must preserve quantity and medicine.");
            Console.WriteLine("PASS live purchase plan: calls=" + (counted.Calls-previous) + ", " + prompt);
        }
    }

    private sealed class CountTransport(IAiModelTransport? inner) : IAiModelTransport
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public string ConfigurationDescription => "Purchase regression";
        public Task<AiModelResponse> SendAsync(AiModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; return inner?.SendAsync(request, cancellationToken) ?? throw new InvalidOperationException("Unexpected model call."); }
    }
    private sealed class InventoryTools(pharmacyApp.AI.Tools.IAiTool tool) : IAiWorkflowToolExecutor
    {
        public pharmacyApp.AI.Tools.IAiTool Tool => tool;
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public JsonElement LastArguments { get; private set; }
        // Realistic advertised read-only availability; execution is still strictly limited
        // to the inventory tool below, so a wrong queue cannot contact other services.
        private static readonly HashSet<string> Available = AiToolRegistry.Discover().GetAllTools()
            .Where(definition => definition.Risk == pharmacyApp.AI.Tools.AiToolRisks.ReadOnly)
            .Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        public bool HasTool(string name) => Available.Contains(name);
        public async Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string name, JsonElement args, CancellationToken cancellationToken = default)
        {
            Check(name == tool.Definition.Name, "Unexpected tool: " + name); Calls++; LastArguments = args.Clone();
            return Fail ? new(name, AiToolExecutionStatus.Timeout, JsonSerializer.SerializeToElement(new {}), "timeout", 0) :
                new(name, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(await tool.ExecuteAsync(args, cancellationToken)), null, 0);
        }
    }
    public static async Task ProbeAsync()
    {
        using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        if (!connection.Database.Contains("Portfolio", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This opt-in probe requires the isolated Portfolio database.");
        var tool = new InventorySalesAnalysisAiToolProvider().Tools.Single();
        var start = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
        var end = DateTime.Today.ToString("yyyy-MM-dd");
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var snapshot = await new ProductInventorySalesRepository().LoadAsync(DateOnly.Parse(start), DateOnly.Parse(end), 50000, timeout.Token);
            await connection.OpenAsync(timeout.Token);
            // Independent base-table checks, not the tool's projection or rendered text.
            var sold = new Dictionary<string, (decimal Quantity, decimal Revenue)>();
            await using (var command = DatabaseCommand.Create("""
                SELECT q.barcode, COUNT(*)::numeric, COALESCE(SUM(q.exit_price),0)
                FROM public."salesQrItems" q JOIN public.sales s ON s.record_id=q.sale_id
                WHERE s."timestamp">=@start AND s."timestamp"<@end GROUP BY q.barcode
                """, connection))
            {
                command.Parameters.AddWithValue("start", DateOnly.Parse(start).ToDateTime(TimeOnly.MinValue));
                command.Parameters.AddWithValue("end", DateOnly.Parse(end).AddDays(1).ToDateTime(TimeOnly.MinValue));
                await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                while (await reader.ReadAsync(timeout.Token)) sold[reader.GetString(0)] = (reader.GetDecimal(1), reader.GetDecimal(2));
            }
            foreach (var row in snapshot.Rows)
            {
                var expected = sold.GetValueOrDefault(row.Barcode);
                Check(row.SoldQuantity == expected.Quantity && row.Revenue == expected.Revenue, "Real sale-line quantity/revenue mismatch.");
            }
            var result = JsonSerializer.SerializeToElement(await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
            { analysis_kind = "replenishment", start_date = start, end_date = end }), timeout.Token));
            var days = DateOnly.Parse(end).DayNumber - DateOnly.Parse(start).DayNumber + 1;
            foreach (var row in result.GetProperty("rows").EnumerateArray())
            {
                var expected = snapshot.Rows.Single(item => item.Barcode == row.GetProperty("barcode").GetString());
                Check(row.GetProperty("recommended_purchase_quantity").GetDecimal() == Math.Max(0, decimal.Ceiling(expected.SoldQuantity * 30 / days - expected.ShelfStock)), "Real replenishment calculation mismatch.");
            }
            var chat = Chat(new(null), new InventoryTools(tool));
            var rendered = await chat.SendAsync(Request("real-data", "what should I purchase?"));
            Check(rendered.Table?.Rows.Count > 0 && rendered.Table.Columns.Any(c => c.Key == "recommended_purchase_quantity" && c.IsVisibleByDefault), "Actual local data must render useful recommendation columns.");
            var translate = typeof(WorkflowRouterChatService).Assembly.GetType("pharmacyApp.Localization.UiText")?.GetMethod("Translate");
            if (translate is not null)
            {
                var message = (string)translate.Invoke(null, [rendered.Message])!;
                Check(message.Contains("No purchase was made") && message.Contains("days of stock coverage"), "Real advice message must be English: " + message);
            }
            Console.WriteLine($"PASS real inventory/sales equality and rendered recommendation: {watch.ElapsedMilliseconds}ms, checked={snapshot.Rows.Count}, displayed={result.GetProperty("rows").GetArrayLength()}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAIL inventory probe: {watch.ElapsedMilliseconds}ms, {exception.GetType().Name}: {exception.Message}");
            Environment.ExitCode = 1;
        }
    }
}
