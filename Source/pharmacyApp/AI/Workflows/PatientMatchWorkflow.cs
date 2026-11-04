using System.Globalization;
using System.Text.Json;
using pharmacyApp.AI.Execution;

namespace pharmacyApp.AI.Workflows;

/// <summary>
/// Resolves a patient name or identity number to the stable local patient record ID used by sales workflows.
/// </summary>
public sealed class PatientMatchWorkflow : IAiWorkflow
{
    private const string Tool = "sale.search_patients";

    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_eslestir",
        "Hasta eşleştir",
        "Searches by a first name, surname, partial/full name or identity number and resolves one exact local patient record ID. A first name is sufficient to SEARCH, not to assume identity: multiple matches pause with a single-selection menu. Never demand a surname before searching. Queue before patient-filtered workflows.",
        RequiredTools: [Tool],
        Parameters: new Dictionary<string, string>
        {
            ["query"] = "The isolated patient first name, surname, partial/full name or Turkish identity number. Search the supplied name even if incomplete; selection resolves ambiguity."
        },
        ProvidesResolvedContext: true);

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.SelectedOptionIds is { Count: > 0 } && context.OfferedOptions is { Count: > 0 })
        {
            var selected = context.OfferedOptions.FirstOrDefault(option =>
                context.SelectedOptionIds.Contains(option.Id, StringComparer.Ordinal));
            return selected is null
                ? WorkflowData.Failed("Seçilen hasta doğrulanamadı.")
                : Completed(selected);
        }

        var query = WorkflowData.Parameter(context, "query");
        if (string.IsNullOrWhiteSpace(query))
            return WorkflowData.Failed("Hasta eşleştirmek için ad, soyad veya T.C. kimlik numarası gerekiyor.");
        var execution = await WorkflowData.Execute(context, Tool, new { query = TurkishUpper(query) }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess)
            return WorkflowData.Failed("Hasta kayıtları alınamadı.", new
            {
                tool = Tool,
                status = execution.Status.ToString(),
                execution.SafeMessage
            });
        if (!execution.Output.TryGetProperty("patients", out var patients) || patients.ValueKind != JsonValueKind.Array)
            return WorkflowData.Failed("Hasta arama sonucu doğrulanamadı.");

        var options = Options(patients);
        if (options.Length == 0 && StripPossessiveSuffix(query) is { } stripped)
        {
            var retry = await WorkflowData.Execute(context, Tool, new { query = TurkishUpper(stripped) }, cancellationToken);
            if (retry is { IsSuccess: true } && retry.Output.TryGetProperty("patients", out var retryPatients) && retryPatients.ValueKind == JsonValueKind.Array)
                options = Options(retryPatients);
        }
        if (options.Length == 0)
        {
            foreach (var fallbackTerm in ConservativeFallbackTerms(query))
            {
                var fallback = await WorkflowData.Execute(context, Tool, new { query = TurkishUpper(fallbackTerm), limit = 200 }, cancellationToken);
                if (fallback is null || !fallback.IsSuccess ||
                    !fallback.Output.TryGetProperty("patients", out var fallbackPatients) || fallbackPatients.ValueKind != JsonValueKind.Array)
                    continue;
                options = Options(fallbackPatients)
                    // Colloquial possessives can combine a suffix with consonant softening
                    // (Örneğin -> Örneğ -> Örnek). Compare only actual directory candidates;
                    // keep the same one-edited-word bound and preserve ambiguous choices.
                    .Where(option => option.Data.TryGetProperty("name", out var name) &&
                                     (IsConservativeOneEditMatch(query, name.GetString()) ||
                                      StripPossessiveSuffix(query) is { } baseQuery &&
                                      IsConservativeOneEditMatch(baseQuery, name.GetString())))
                    .ToArray();
                if (options.Length > 0) break;
            }
        }
        if (options.Length == 0) return WorkflowData.Failed("Hasta kaydı bulunamadı.");

        var exact = options.Where(option =>
            option.Data.TryGetProperty("name", out var name) &&
            string.Equals(Normalize(name.GetString()), Normalize(query), StringComparison.Ordinal)).ToArray();
        if (exact.Length == 1) return Completed(exact[0]);
        if (options.Length == 1) return Completed(options[0]);

        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.AwaitingSelection,
            "Birden fazla hasta eşleşti. Hastayı seçip Seçimi Onayla'ya basın.",
            JsonSerializer.SerializeToElement(new { query, candidate_count = options.Length }),
            exact.Length > 1 ? exact : options,
            AllowsMultipleSelection: false);
    }

    private static AiWorkflowSelectionOption? ToOption(JsonElement patient)
    {
        var recordId = patient.TryGetProperty("patient_record_id", out var id) && id.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        var name = WorkflowData.Text(patient, "name").Trim();
        if (recordId <= 0 || name.Length == 0) return null;
        var tc = WorkflowData.Text(patient, "patient_tc").Trim();
        var phoneNumber = WorkflowData.Text(patient, "phone_number").Trim();
        return new AiWorkflowSelectionOption(
            recordId.ToString(CultureInfo.InvariantCulture),
            tc.Length == 0 ? name : $"{name} · {tc}",
            JsonSerializer.SerializeToElement(new
            {
                patient_record_id = recordId,
                patient_tc = tc,
                name,
                phone_number = phoneNumber,
                barcode = tc.Length == 0 ? recordId.ToString(CultureInfo.InvariantCulture) : tc,
                is_on_market = true
            }));
    }

    private static AiWorkflowSelectionOption[] Options(JsonElement patients) => patients.EnumerateArray()
        .Where(patient => patient.ValueKind == JsonValueKind.Object)
        .Select(ToOption)
        .Where(option => option is not null)
        .Select(option => option!)
        .DistinctBy(option => option.Id, StringComparer.Ordinal)
        .ToArray();

    private static string? StripPossessiveSuffix(string query)
    {
        var words = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 2) return null;
        var last = words[^1];
        var normalized = Normalize(last);
        var hasApostrophe = last.Contains((char)39) || last.Contains((char)8217);
        var suffixes = hasApostrophe
            ? new[] { "nin", "nun", "nın", "nün", "in", "un", "ın", "ün" }
            : new[] { "in", "un", "ın", "ün", "nin", "nun", "nın", "nün" };
        foreach (var suffix in suffixes)
        {
            if (!normalized.EndsWith(Normalize(suffix), StringComparison.Ordinal) || last.Length - suffix.Length < 3) continue;
            words[^1] = last[..^suffix.Length].TrimEnd((char)39, (char)8217);
            return string.Join(' ', words);
        }
        return null;
    }

    private static IReadOnlyList<string> ConservativeFallbackTerms(string query)
    {
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 2) return [];
        return new[] { words[^1], words[0] }.Where(word => word.Length >= 3).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsConservativeOneEditMatch(string requested, string? candidate)
    {
        var requestedWords = Normalize(requested).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateWords = Normalize(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requestedWords.Length < 2 || requestedWords.Length != candidateWords.Length)
            return false;
        var edits = 0;
        for (var index = 0; index < requestedWords.Length; index++)
        {
            if (string.Equals(requestedWords[index], candidateWords[index], StringComparison.Ordinal)) continue;
            if (!IsEditDistanceAtMostOne(requestedWords[index], candidateWords[index]) || ++edits > 1) return false;
        }
        return edits == 1;
    }

    private static bool IsEditDistanceAtMostOne(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        var leftIndex = 0;
        var rightIndex = 0;
        var edits = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex] == right[rightIndex])
            {
                leftIndex++;
                rightIndex++;
                continue;
            }
            if (++edits > 1) return false;
            if (left.Length > right.Length) leftIndex++;
            else if (right.Length > left.Length) rightIndex++;
            else
            {
                leftIndex++;
                rightIndex++;
            }
        }
        if (leftIndex < left.Length || rightIndex < right.Length) edits++;
        return edits <= 1;
    }

    private static AiWorkflowStepResult Completed(AiWorkflowSelectionOption option)
    {
        var recordId = option.Data.GetProperty("patient_record_id").GetInt32();
        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            "Hasta kaydı eşleştirildi.",
            JsonSerializer.SerializeToElement(new
            {
                patient_record_id = recordId,
                selected_patient = option.Data
            }));
    }

    private static string Normalize(string? value) => (value ?? string.Empty)
        .Trim()
        .ToLower(CultureInfo.GetCultureInfo("tr-TR"))
        .Replace('ı', 'i')
        .Replace('â', 'a').Replace('î', 'i').Replace('û', 'u');

    internal static string TurkishUpper(string value) => value.Trim()
        .ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
}

/// <summary>
/// Presents the contact information already returned while resolving a patient. It deliberately
/// consumes resolved context instead of querying the patient directory a second time.
/// </summary>
public sealed class PatientContactInformationWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "hasta_iletisim_bilgisi_getir",
        "Hasta iletişim bilgisini getir",
        "Returns the registered phone number for one resolved local patient. Accepts hasta_eslestir or a uniquely resolved previous-result patient. Missing contact fields are fetched read-only for that exact patient ID; never changes patient data.",
        ProducesUserResponse: true,
        Parameters: new Dictionary<string, string>(),
        ProvidesResolvedContext: true);

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var match = WorkflowData.ResolvedPatientContext(context) ?? default;
        if (match.ValueKind != JsonValueKind.Object ||
            !match.TryGetProperty("selected_patient", out var patient) || patient.ValueKind != JsonValueKind.Object)
            return WorkflowData.Failed("Telefon numarası için önce hasta kaydının eşleştirilmesi gerekiyor.");

        // A displayed sales row identifies the patient but does not prove their phone is
        // absent. Fetch missing fields using the existing directory tool and verify ID.
        if (!patient.TryGetProperty("phone_number", out _))
        {
            var id = WorkflowData.Text(patient, "patient_record_id");
            var query = WorkflowData.Text(patient, "patient_tc");
            if (string.IsNullOrWhiteSpace(query)) query = WorkflowData.Text(patient, "name");
            if (string.IsNullOrWhiteSpace(id) || query.Length < 3)
                return WorkflowData.Failed("Bu kayıttan hastanın telefon bilgisi doğrulanamıyor. Lütfen hastayı adıyla yeniden seçin.");
            var execution = await WorkflowData.Execute(context, "sale.search_patients",
                new { query = PatientMatchWorkflow.TurkishUpper(query), limit = 200 }, cancellationToken);
            if (execution is null) return WorkflowData.Missing("sale.search_patients");
            if (!execution.IsSuccess || !execution.Output.TryGetProperty("patients", out var patients) ||
                patients.ValueKind != JsonValueKind.Array)
                return WorkflowData.Failed("Hastanın telefon bilgisi şu anda alınamadı.");
            var exact = patients.EnumerateArray().Where(candidate =>
                WorkflowData.Text(candidate, "patient_record_id") == id).ToArray();
            if (exact.Length != 1)
                return WorkflowData.Failed("Önceki sonuçtaki hasta kaydı doğrulanamadı. Lütfen hastayı yeniden seçin.");
            patient = exact[0].Clone();
        }

        var name = WorkflowData.Text(patient, "name").Trim();
        var phoneNumber = WorkflowData.Text(patient, "phone_number").Trim();
        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["patient_name"] = name,
            ["phone_number"] = phoneNumber
        };
        var output = WorkflowData.RowsOutput([row], new
        {
            total_count = 1,
            visible_columns = new[] { "patient_name", "phone_number" }
        });
        if (phoneNumber.Length == 0)
            return new AiWorkflowStepResult(
                AiWorkflowStepStatus.Completed,
                $"{name} adlı hastanın kayıtlı telefon numarası bulunmuyor.",
                output);

        var content = new AiWorkflowContentPresentation(
            AiWorkflowContentKind.InformationCards,
            "Hasta İletişim Bilgisi",
            [new AiWorkflowContentItem(name, Value: phoneNumber, Detail: "Telefon Numarası")]);
        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            $"{name} için kayıtlı telefon numarası: {phoneNumber}",
            output,
            Content: content);
    }
}
