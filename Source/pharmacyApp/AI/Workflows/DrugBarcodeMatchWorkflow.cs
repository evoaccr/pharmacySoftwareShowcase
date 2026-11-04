using System.Text.Json;
using System.Text.RegularExpressions;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Workflows;

/// <summary>
/// Resolves medicine names to stable barcodes for downstream workflows.
/// It reuses the Product Catalog service and never exposes the catalog's individual AI tools.
/// </summary>
public sealed partial class DrugBarcodeMatchWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "ilac_barkod_eslestir",
        "İlaç barkod eşleştir",
        "Resolves top-level medicine_names to exact product barcodes for later workflows. Preserve strength/package qualifiers. An optional query supplies the medicine phrase when medicine_names is empty.",
        RequiredTools: ["product.search_by_name"],
        Parameters: new Dictionary<string, string> { ["query"] = "Optional isolated medicine phrase used only when top-level medicine_names is empty; never the whole user request." },
        ProvidesResolvedContext: true);

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.SelectedOptionIds is { Count: > 0 } && context.OfferedOptions is { Count: > 0 })
        {
            var selected = context.OfferedOptions
                .Where(option => context.SelectedOptionIds.Contains(option.Id, StringComparer.Ordinal))
                .ToArray();
            if (selected.Length == 0)
                return Failed("Seçilen ilaçlar doğrulanamadı.");
            return Completed(selected);
        }

        var searchTerms = context.MedicineNames is { Count: > 0 }
            ? context.MedicineNames
            : [WorkflowData.Parameter(context, "query") ?? context.EffectiveRequest];
        const string requiredTool = "product.search_by_name";
        if (context.ToolExecutor is null || !context.ToolExecutor.HasTool(requiredTool))
            return Missing(requiredTool);
        var request = context.RequestContext ?? new AiRequestContext(
            Guid.NewGuid().ToString("N"), context.OriginalRequest, context.ApplicationContext,
            new HashSet<string>());
        var matches = new List<AiWorkflowSelectionOption>();
        foreach (var term in searchTerms.Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalizedTerm = term.Trim();
            var execution = await SearchAsync(normalizedTerm);
            if (execution.Status == AiToolExecutionStatus.ToolMissing)
                return Missing(requiredTool);
            if (!execution.IsSuccess)
                return new AiWorkflowStepResult(
                    AiWorkflowStepStatus.Failed,
                    "İlaç barkod eşleştirme aracı çalıştırılamadı.",
                    JsonSerializer.SerializeToElement(new
                    {
                        tool = requiredTool,
                        status = execution.Status.ToString(),
                        error = execution.SafeMessage
                    }));
            var termMatches = Options(execution.Output).ToList();
            if (termMatches.Count == 0 && TryTerminalCorrectionProbe(normalizedTerm, out var prefix))
            {
                var fallback = await SearchAsync(prefix);
                if (fallback.IsSuccess)
                    termMatches.AddRange(Options(fallback.Output).Where(option =>
                        option.Data.TryGetProperty("name", out var name) &&
                        IsOneCharacterBrandCorrection(normalizedTerm, name.GetString())));
            }
            matches.AddRange(termMatches);
        }
        if (matches.Count == 0)
            return Failed("İlaç adı ürün kataloğunda eşleştirilemedi.");

        var options = matches
            .DistinctBy(option => option.Id, StringComparer.Ordinal)
            .ToArray();
        options = NarrowByProductQualifiers(searchTerms, options);
        if (options.Length == 0)
            return Failed("Eşleşen ürünlerin kullanılabilir barkodu bulunamadı.");
        if (options.Length == 1) return Completed(options);
        var distinctNames = options.Select(option =>
                option.Data.TryGetProperty("name", out var name) ? name.GetString()?.Trim() ?? string.Empty : option.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctNames == 1) return Completed(options);

        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.AwaitingSelection,
            "Birden fazla ilaç eşleşti. Bir veya daha fazla ürün seçip Tamam'a basın.",
            JsonSerializer.SerializeToElement(new
            {
                medicine_names = searchTerms,
                candidate_count = options.Length
            }),
            options,
            AllowsMultipleSelection: true);

        Task<AiWorkflowToolExecution> SearchAsync(string query) => context.ToolExecutor.ExecuteAsync(
            request,
            requiredTool,
            JsonSerializer.SerializeToElement(new { query }),
            cancellationToken);
    }

    private static IEnumerable<AiWorkflowSelectionOption> Options(JsonElement output)
    {
        if (!output.TryGetProperty("products", out var productsElement) ||
            productsElement.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var product in productsElement.EnumerateArray())
        {
            var barcode = Text(product, "barcode");
            var name = Text(product, "name");
            if (string.IsNullOrWhiteSpace(barcode) || string.IsNullOrWhiteSpace(name)) continue;
            var isOnMarket = product.TryGetProperty("is_on_market", out var market) && market.ValueKind == JsonValueKind.True;
            var label = $"{name} · {barcode}{(isOnMarket ? string.Empty : " · Pasif")}";
            yield return new AiWorkflowSelectionOption(
                barcode,
                label,
                JsonSerializer.SerializeToElement(new { barcode, name, is_on_market = isOnMarket }));
        }
    }

    private static bool TryTerminalCorrectionProbe(string term, out string prefix)
    {
        prefix = string.Empty;
        if (term.Length < 5 || term.Any(character => !char.IsLetter(character))) return false;
        prefix = term[..^1];
        return true;
    }

    private static bool IsOneCharacterBrandCorrection(string requested, string? productName)
    {
        var brand = (productName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (brand is null || brand.Length != requested.Length) return false;
        var differenceCount = 0;
        for (var index = 0; index < requested.Length; index++)
            if (char.ToUpperInvariant(requested[index]) != char.ToUpperInvariant(brand[index]) && ++differenceCount > 1)
                return false;
        return differenceCount == 1;
    }

    private static AiWorkflowStepResult Completed(IReadOnlyList<AiWorkflowSelectionOption> selected) => new(
        AiWorkflowStepStatus.Completed,
        selected.Count == 1 ? "İlaç barkodu eşleştirildi." : $"{selected.Count} ilaç barkodu eşleştirildi.",
        JsonSerializer.SerializeToElement(new
        {
            selected_products = selected.Select(option => option.Data).ToArray(),
            barcodes = selected.Select(option => option.Id).ToArray()
        }));

    private static AiWorkflowStepResult Failed(string message) => new(
        AiWorkflowStepStatus.Failed,
        message,
        JsonSerializer.SerializeToElement(new { error = message }));

    private static AiWorkflowStepResult Missing(params string[] toolNames) => new(
        AiWorkflowStepStatus.MissingTool,
        $"Bu iş akışı için eksik araç: {string.Join(", ", toolNames)}. Bu aracı eklemeniz gerekiyor.",
        JsonSerializer.SerializeToElement(new { missing_tools = toolNames }),
        MissingTools: toolNames);

    private static AiWorkflowSelectionOption[] NarrowByProductQualifiers(
        IReadOnlyList<string> searchTerms,
        AiWorkflowSelectionOption[] options)
    {
        var requestedNumbers = NumberPattern().Matches(string.Join(' ', searchTerms))
            .Select(match => int.TryParse(match.Value, out var value) ? value : -1)
            .Where(value => value >= 0)
            .ToArray();
        if (requestedNumbers.Length < 2) return options;

        var requestedDose = requestedNumbers[0];
        var requestedPackage = requestedNumbers[^1];
        // Common pharmacy shorthand occasionally drops the final zero in a dose ("50/30" -> "500 mg / 30 tablet").
        // Prefer that interpretation only when the package count also matches a catalog name.
        var doseCandidates = requestedDose is >= 10 and < 100
            ? new[] { requestedDose * 10, requestedDose }
            : new[] { requestedDose };
        foreach (var dose in doseCandidates)
        {
            var narrowed = options.Where(option =>
            {
                var name = option.Data.TryGetProperty("name", out var value) ? value.GetString() ?? string.Empty : option.Label;
                var numbers = NumberPattern().Matches(name)
                    .Select(match => int.TryParse(match.Value, out var parsed) ? parsed : -1)
                    .Where(parsed => parsed >= 0)
                    .ToArray();
                return numbers.Contains(dose) && numbers.Length > 0 && numbers[^1] == requestedPackage;
            }).ToArray();
            if (narrowed.Length > 0) return narrowed;
        }
        // Explicit dose/package qualifiers are a hard identity constraint. Falling back to a
        // different package here is worse than returning no match in a pharmacy workflow.
        return [];
    }

    private static string? Text(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}

/// <summary>
/// Internal matcher slots let one saved router plan pause independently for two medicine names.
/// They deliberately remain hidden from the router; deterministic normalization owns their order.
/// </summary>
public abstract class ScopedDrugBarcodeMatchWorkflowBase(string name, string displayName) : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        name,
        displayName,
        "Resolves one deterministic medicine-comparison slot to exact barcodes without another model call.",
        RouterVisible: false,
        RequiredTools: ["product.search_by_name"],
        Parameters: new Dictionary<string, string> { ["query"] = "One medicine name for this comparison slot." },
        ProvidesResolvedContext: true);

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var query = WorkflowData.Parameter(context, "query");
        if (string.IsNullOrWhiteSpace(query))
            return WorkflowData.Failed("Karşılaştırılacak ilaç adı belirtilmedi.");
        var result = await new DrugBarcodeMatchWorkflow().ExecuteAsync(
            context with { MedicineNames = [query] }, cancellationToken);
        if (context.SelectedOptionIds is { Count: > 0 } || result.Status != AiWorkflowStepStatus.Completed ||
            !result.Output.TryGetProperty("selected_products", out var products) || products.ValueKind != JsonValueKind.Array)
            return result;
        var options = products.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
            .Select(product =>
            {
                var barcode = Text(product, "barcode");
                var productName = Text(product, "name");
                var active = product.TryGetProperty("is_on_market", out var market) && market.ValueKind == JsonValueKind.True;
                return new AiWorkflowSelectionOption(barcode,
                    $"{productName} · {barcode}{(active ? string.Empty : " · Pasif")}", product.Clone());
            })
            .Where(option => option.Id.Length > 0).ToArray();
        return options.Length == 0 ? result : new AiWorkflowStepResult(
            AiWorkflowStepStatus.AwaitingSelection,
            $"Karşılaştırma için {query} ürününü seçip Tamam'a basın.",
            JsonSerializer.SerializeToElement(new { medicine_name = query, candidate_count = options.Length }),
            options,
            AllowsMultipleSelection: true);
    }

    private static string Text(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}

public sealed class FirstMedicineBarcodeMatchWorkflow()
    : ScopedDrugBarcodeMatchWorkflowBase("ilac_barkod_eslestir_1", "İlk ilaç barkodunu eşleştir");

public sealed class SecondMedicineBarcodeMatchWorkflow()
    : ScopedDrugBarcodeMatchWorkflowBase("ilac_barkod_eslestir_2", "İkinci ilaç barkodunu eşleştir");
