using System.Net;
using System.Text;
using System.Text.Json;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Providers;
using pharmacyApp.Infrastructure;

internal static class ModelConfigurationRegressionTests
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    public static async Task RunAsync(string testDirectory, Action<bool, string> assert)
    {
        var directory = Path.Combine(testDirectory, "model-migration");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ai-settings.jsonc");
        File.WriteAllText(path, """
            {
              // Preserve the owner's router note.
              "RouterModel":"gpt-5.6-luna",
              "RouterMaxOutputTokens":900,
              "SimpleModel":"legacy-simple-pin",
              "DebugMode":true,
              "ExtraSetting":{"keep":true},
            }
            """);
        var provider = new AiConfigurationProvider(path);
        var migrated = provider.GetCurrent();
        assert(migrated.RouterModel == "gpt-6-luna" && migrated.RouterReasoningEffort == "none"
               && migrated.RouterMaxOutputTokens == 1800 && migrated.SimpleModel == "legacy-simple-pin" && migrated.DebugMode,
            "The cheaper router migrates only legacy defaults, preserving workload pins and debug settings.");
        using (var persisted = JsonDocument.Parse(File.ReadAllText(path), ReadOptions))
        {
            assert(persisted.RootElement.GetProperty("ConfigurationVersion").GetInt32() == AiConfiguration.CurrentConfigurationVersion
                   && persisted.RootElement.GetProperty("ExtraSetting").GetProperty("keep").GetBoolean()
                   && File.ReadAllText(path).Contains("// Preserve the owner's router note.", StringComparison.Ordinal),
                "Standalone migration persists its version without removing JSONC comments or unknown fields.");
        }
        provider.Save(migrated with { RouterModel = "gpt-5.6-luna", RouterMaxOutputTokens = 900, GatewayEndpoint = "https://fake.invalid" });
        provider.Save(provider.GetCurrent() with { GatewayEndpoint = null });
        var pinned = new AiConfigurationProvider(path).GetCurrent();
        assert(pinned.RouterModel == "gpt-5.6-luna" && pinned.RouterMaxOutputTokens == 900 && pinned.GatewayEndpoint is null
               && File.ReadAllText(path).Contains("ExtraSetting", StringComparison.Ordinal),
            "Explicit old-model pins remain stable; saving can clear a gateway while preserving unknown settings.");
        File.WriteAllText(path, """{"RouterModel":"custom-router","RouterReasoningEffort":"high","RouterMaxOutputTokens":1200}""");
        var custom = provider.GetCurrent();
        assert(custom.RouterModel == "custom-router" && custom.RouterReasoningEffort == "high" && custom.RouterMaxOutputTokens == 1200,
            "Migration preserves explicit custom routers and token limits.");
        var customOption = AiRouterModelCatalog.ForConfiguration(custom).Single(option => option.Id == "custom-router");
        assert(customOption.RouterReasoningEffort == "high" && customOption.InputPriceUsdPerMillion is null
               && customOption.SelectorLabel.Contains("Custom setting", StringComparison.Ordinal)
               && customOption.PriceDescription.Contains("not been verified", StringComparison.Ordinal),
            "The English selector preserves custom models without inventing prices.");
        File.WriteAllText(path, """{"ConfigurationVersion":99,"RouterModel":"gpt-5.6-luna"}""");
        var futureBytes = File.ReadAllText(path);
        assert(provider.GetCurrent().RouterModel == "gpt-5.6-luna" && File.ReadAllText(path) == futureBytes,
            "Settings from a newer version are not downgraded or rewritten.");
        var fresh = new AiConfigurationProvider(Path.Combine(directory, "fresh.jsonc")).GetCurrent();
        assert(fresh.RouterModel == AiRouterModelCatalog.DefaultModelId && fresh.ConfigurationVersion == AiConfiguration.CurrentConfigurationVersion,
            "Fresh installations and the selector agree on GPT-6 Luna.");
        VerifyUnifiedConfiguration(directory, assert);

        var handler = new CaptureHandler();
        var transport = new OpenAiResponsesTransport(new FixedAiConfigurationProvider(fresh), new HttpClient(handler),
            () => new AiSecrets { OpenAiApiKey = "unit-config-key" });
        var modelRequest = new AiModelRequest(
            fresh.RouterModel, fresh.RouterReasoningEffort, "Return JSON.",
            [JsonSerializer.SerializeToElement(new { role = "user", content = "hello" })],
            [], false, false, PromptCacheKey: "model-migration-test", MaxOutputTokens: fresh.RouterMaxOutputTokens,
            ResponseJsonSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object", properties = new { answer = new { type = "string" } },
                required = new[] { "answer" }, additionalProperties = false
            }));
        await transport.SendAsync(modelRequest);
        using var payload = JsonDocument.Parse(handler.Payload!);
        assert(payload.RootElement.GetProperty("model").GetString() == "gpt-6-luna"
               && payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "none"
               && payload.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean()
               && payload.RootElement.GetProperty("prompt_cache_key").GetString() == "model-migration-test"
               && payload.RootElement.GetProperty("max_output_tokens").GetInt32() == 1800
               && !payload.RootElement.GetProperty("store").GetBoolean()
               && handler.Authorization == "Bearer unit-config-key",
            "Responses uses the structured contract, cache key, no-storage policy and injected local-config secret.");
        var gatewayHandler = new CaptureHandler();
        await new OpenAiResponsesTransport(
            new FixedAiConfigurationProvider(fresh with { GatewayEndpoint = "https://test.invalid/v1/responses" }),
            new HttpClient(gatewayHandler), () => new AiSecrets { GatewayToken = "unit-gateway-key" }).SendAsync(modelRequest);
        assert(gatewayHandler.Authorization == "Bearer unit-gateway-key", "Gateway authentication comes from the local configuration secret.");
        var unconfigured = new OpenAiResponsesTransport(new FixedAiConfigurationProvider(fresh), new HttpClient(new CaptureHandler()), () => new AiSecrets());
        assert(!unconfigured.IsConfigured, "Missing local credentials cannot fall back to process environment secrets.");

        const string phasedResponse = """{"id":"phase-test","output":[{"type":"message","phase":"commentary","content":[{"type":"output_text","text":"Interim note, not a plan."}]},{"type":"message","phase":"final_answer","content":[{"type":"output_text","text":"{\"answer\":\"Hello\"}"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""";
        var phased = await SendFakeAsync(phasedResponse);
        assert(phased.Text == "{\"answer\":\"Hello\"}" && phased.OutputItems.Count == 2
               && phased.OutputItems[0].GetProperty("phase").GetString() == "commentary",
            "Only final text becomes the structured response, while original phase items remain available for replay.");
        const string commentaryOnly = """{"id":"phase-test","output":[{"type":"message","phase":"commentary","content":[{"type":"output_text","text":"Incomplete reasoning."}]}],"output_text":"Incomplete reasoning."}""";
        var incomplete = await SendFakeAsync(commentaryOnly);
        assert(incomplete.Text.Length == 0, "Commentary alone is never passed off as a final answer or plan.");
        var legacy = await SendFakeAsync("""{"id":"legacy","output":[{"type":"message","content":[{"type":"output_text","text":"Hello"}]}]}""");
        var direct = await SendFakeAsync("""{"id":"legacy-direct","output_text":"Hello"}""");
        assert(legacy.Text == "Hello" && direct.Text == "Hello", "Unphased and direct legacy response formats remain supported.");
        Console.WriteLine("PASS model-configuration-migration");

        Task<AiModelResponse> SendFakeAsync(string response) => new OpenAiResponsesTransport(
            new FixedAiConfigurationProvider(fresh), new HttpClient(new CaptureHandler(response)),
            () => new AiSecrets { OpenAiApiKey = "unit-config-key" }).SendAsync(modelRequest);
    }

    private static void VerifyUnifiedConfiguration(string directory, Action<bool, string> assert)
    {
        var path = Path.Combine(directory, "shared-config.json");
        const string original = """
            {
              // Owner application note.
              "Application": {"Language":"en-GB","OwnerFlag":true},
              "Database": {"Host":"unit.invalid","Password":"unit-database-placeholder"},
              "Ai": {
                /* Owner model note. */
                "RouterModel":"gpt-5.6-luna",
                "RouterMaxOutputTokens":900,
                "DefaultModel":"custom-quality-model",
                "GatewayEndpoint":"https://unit.invalid/v1/responses",
                "UnknownAi":{"keep":true},
              },
              "Secrets": {"OpenAiApiKey":"unit-placeholder", "GatewayToken":"unit-placeholder"},
              "UnknownSection":{"keep":true},
            }
            """;
        File.WriteAllText(path, original);
        var store = new ConfigurationStore(path);
        var provider = new AiConfigurationProvider(store);
        var migrated = provider.GetCurrent();
        var changed = File.ReadAllText(path);
        using var before = JsonDocument.Parse(original, ReadOptions);
        using var after = JsonDocument.Parse(changed, ReadOptions);
        assert(migrated.RouterModel == AiRouterModelCatalog.DefaultModelId && migrated.RouterMaxOutputTokens == 1800
               && migrated.DefaultModel == "custom-quality-model" && migrated.GatewayEndpoint == "https://unit.invalid/v1/responses",
            "The shared Ai section receives the same bounded migration without changing endpoint or explicit workload models.");
        foreach (var name in new[] { "Application", "Database", "Secrets", "UnknownSection" })
            assert(before.RootElement.GetProperty(name).GetRawText() == after.RootElement.GetProperty(name).GetRawText(),
                $"The {name} section remains byte-for-byte unchanged during model migration.");
        assert(changed.Contains("// Owner application note.", StringComparison.Ordinal)
               && changed.Contains("/* Owner model note. */", StringComparison.Ordinal)
               && after.RootElement.GetProperty("Ai").GetProperty("UnknownAi").GetProperty("keep").GetBoolean(),
            "Shared migration preserves all comments and unknown Ai fields.");
        _ = provider.GetCurrent();
        assert(File.ReadAllText(path) == changed, "The shared migration writes only once.");
        provider.Save(migrated with { RouterModel = "gpt-5.6-luna", RouterMaxOutputTokens = 900 });
        assert(new AiConfigurationProvider(store).GetCurrent().RouterModel == "gpt-5.6-luna"
               && provider.GetCurrent().RouterMaxOutputTokens == 900,
            "A deliberately pinned legacy model and token limit remain pinned in shared JSONC.");
        const string invalid = "{ // keep this broken edit\n \"Ai\": ";
        File.WriteAllText(path, invalid);
        var rejected = false;
        try { _ = provider.GetCurrent(); } catch (JsonException) { rejected = true; }
        assert(rejected && File.ReadAllText(path) == invalid, "Malformed user configuration fails without silently replacing it.");
    }

    private sealed class CaptureHandler(string? response = null) : HttpMessageHandler
    {
        public string? Payload { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response ?? """{"id":"model-migration","output":[{"type":"message","content":[{"type":"output_text","text":"{\"answer\":\"Hello\"}"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
