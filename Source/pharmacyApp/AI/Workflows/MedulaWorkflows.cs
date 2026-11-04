using System.Globalization;
using System.Text;
using System.Text.Json;
using pharmacyApp.AI.Execution;
using pharmacyApp.Modules.Medula;

namespace pharmacyApp.AI.Workflows;

public abstract class MedulaWorkflowBase : IAiWorkflow
{
    protected MedulaWorkflowBase(AiWorkflowDefinition definition) => Definition = definition;
    public AiWorkflowDefinition Definition { get; }
    protected abstract (string Tool, object Arguments) BuildCall(AiWorkflowContext context);

    public virtual async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        string tool;
        object arguments;
        try { (tool, arguments) = BuildCall(context); }
        catch (MedulaWorkflowInputException exception) { return WorkflowData.Failed(exception.Message); }
        var execution = await WorkflowData.Execute(context, tool, arguments, cancellationToken);
        if (execution is null) return WorkflowData.Missing(tool);
        if (execution.Status == AiToolExecutionStatus.ConfirmationRequired)
        {
            if (execution.PendingAction is null)
                return WorkflowData.Failed("Canlı Medula sorgusu için onay kaydı oluşturulamadı.");
            return new AiWorkflowStepResult(
                AiWorkflowStepStatus.AwaitingApproval,
                "Canlı Medula sorgusu hasta veya reçete ekranını açacak. Devam edilsin mi?",
                JsonSerializer.SerializeToElement(new
                {
                    pending_action_id = execution.PendingAction.Id,
                    tool
                }));
        }
        if (!execution.IsSuccess)
            return WorkflowData.Failed("Medula sorgusu tamamlanamadı.", new
            {
                tool, status = execution.Status.ToString(), execution.SafeMessage
            });
        var result = MedulaWorkflowPresentation.Create(Definition.DisplayName, tool, execution.Output);
        if (tool == "medula.patient_reports_search" &&
            result.Status == AiWorkflowStepStatus.Completed &&
            result.Output.TryGetProperty("rows", out var reportRows) &&
            reportRows.ValueKind == JsonValueKind.Array && reportRows.GetArrayLength() == 0)
        {
            var patient = WorkflowData.PatientName(context).Trim();
            result = result with
            {
                Message = patient.Length == 0
                    ? "Hasta için Medula raporu bulunamadı."
                    : $"{patient} adlı hastanın Medula raporu bulunamadı."
            };
        }
        return result;
    }

    protected static string Required(AiWorkflowContext context, string name, string message)
    {
        var value = WorkflowData.Parameter(context, name);
        if (string.IsNullOrWhiteSpace(value)) throw new MedulaWorkflowInputException(message);
        return value;
    }

    protected static string PatientTc(AiWorkflowContext context)
    {
        var value = WorkflowData.PatientTc(context).Trim();
        if (value.Length != 11 || value.Any(character => !char.IsDigit(character)))
            throw new MedulaWorkflowInputException(
                "Canlı Medula hasta sorgusu için önce hasta eşleştirmesi veya 11 haneli T.C. kimlik numarası gerekiyor.");
        return value;
    }

    protected static string Barcode(AiWorkflowContext context)
    {
        var direct = WorkflowData.Parameter(context, "barcode");
        var value = !string.IsNullOrWhiteSpace(direct)
            ? direct.Trim()
            : WorkflowData.Barcodes(context).FirstOrDefault() ?? string.Empty;
        if (value.Length is < 8 or > 14 || value.Any(character => !char.IsDigit(character)))
            throw new MedulaWorkflowInputException(
                "Canlı Medula ilaç sorgusu için önce tek bir ilaç/barkod eşleştirilmesi gerekiyor.");
        return value;
    }

    protected static Dictionary<string, object?> Page(
        AiWorkflowContext context, int defaultLimit = 50, int maximumLimit = 100) => new(StringComparer.Ordinal)
    {
        ["offset"] = WorkflowData.Integer(context, "offset", 0, 0, 100_000),
        ["limit"] = WorkflowData.Integer(context, "limit", defaultLimit, 1, maximumLimit)
    };
}

public sealed class MedulaSessionWorkflow : MedulaWorkflowBase
{
    public MedulaSessionWorkflow() : base(new AiWorkflowDefinition(
        "medula_oturumunu_yonet", "Medula oturumunu yönet",
        "Gets Medula session status, logs in, or restarts the browser; use operation=status, login, or restart.",
        RequiredTools: ["medula.session_status", "medula.session_login", "medula.session_restart"],
        Parameters: new Dictionary<string, string>
        {
            ["operation"] = "status, login, or restart.",
            ["force_new_session"] = "true only when a fresh login is explicitly requested.",
            ["restore_previous_state"] = "Restore the prior supported screen after restart."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var operation = WorkflowData.Parameter(context, "operation")?.ToLowerInvariant() ?? "status";
        return operation switch
        {
            "status" => ("medula.session_status", new { }),
            "login" => ("medula.session_login", new
            {
                force_new_session = WorkflowData.Boolean(context, "force_new_session")
            }),
            "restart" => ("medula.session_restart", new
            {
                restore_previous_state = WorkflowData.Boolean(context, "restore_previous_state", true)
            }),
            _ => throw new MedulaWorkflowInputException("Medula oturum işlemi tanınamadı.")
        };
    }
}

public sealed class MedulaPatientClinicalWorkflow : MedulaWorkflowBase
{
    public MedulaPatientClinicalWorkflow() : base(new AiWorkflowDefinition(
        "medula_hasta_klinik_bilgisi_getir", "Hastanın canlı Medula bilgisini getir",
        "Gets live Medula eligibility, reports, examinations, or off-label approvals. Reports use a host prescription from Reçete Listesi Günlük, replace only Reçete Sahibi T.C., and open Rapor without saving. For medication_history it verifies the patient live, then reads medication lines from the synchronized Medula prescription archive without modifying a prescription. Queue hasta_eslestir first for a patient name.",
        RequiredTools: ["medula.patient_medication_history_search", "medula.patient_eligibility_get",
            "medula.patient_reports_search", "medula.patient_examinations_search",
            "medula.patient_off_label_approvals_search"],
        Parameters: new Dictionary<string, string>
        {
            ["operation"] = "medication_history, eligibility, reports, examinations, or off_label.",
            ["patient_tc"] = "Direct T.C. or inherited from hasta_eslestir.",
            ["eye_medications"] = "Eye-medication history only.", ["include_expired"] = "Include expired reports.",
            ["off_label_mode"] = "legacy or person_based.", ["offset"] = "Result offset.",
            ["limit"] = "Maximum 1-100 results."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var operation = WorkflowData.Parameter(context, "operation")?.ToLowerInvariant()
                        ?? throw new MedulaWorkflowInputException("İstenen canlı Medula hasta bilgisi belirtilmedi.");
        var tc = PatientTc(context);
        if (operation == "eligibility")
            return ("medula.patient_eligibility_get", new { patient_tc = tc });
        var values = Page(context);
        values["patient_tc"] = tc;
        return operation switch
        {
            "medication_history" => With("medula.patient_medication_history_search", values,
                "eye_medications", WorkflowData.Boolean(context, "eye_medications")),
            "reports" => With("medula.patient_reports_search", values,
                "include_expired", WorkflowData.Boolean(context, "include_expired")),
            "examinations" => ("medula.patient_examinations_search", values),
            "off_label" => With("medula.patient_off_label_approvals_search", values,
                "mode", WorkflowData.Parameter(context, "off_label_mode") ?? "person_based"),
            _ => throw new MedulaWorkflowInputException("İstenen canlı Medula hasta bilgisi tanınamadı.")
        };
    }

    private static (string Tool, object Arguments) With(
        string tool, IDictionary<string, object?> values, string key, object value)
    {
        values[key] = value;
        return (tool, values);
    }
}

public sealed class MedulaPatientMedicationListWorkflow : MedulaWorkflowBase
{
    public MedulaPatientMedicationListWorkflow() : base(new AiWorkflowDefinition(
        "medula_hasta_ilac_listesini_getir", "Hastanın Medula ilaç listesini getir",
        "Uses a host prescription from Reçete Listesi Günlük, replaces Reçete Sahibi T.C. with the selected patient's T.C., and reads the patient medication list behind Medula's İlaç action without saving. This differs from prescription contents, archived medication history, and pharmacy sales history. Queue hasta_eslestir first for a patient name.",
        RequiredTools: ["medula.patient_medication_list_get"],
        Parameters: new Dictionary<string, string>
        {
            ["patient_tc"] = "Direct T.C. or inherited from hasta_eslestir.",
            ["offset"] = "Result offset.", ["limit"] = "Maximum 1-100 rows."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var values = Page(context);
        values["patient_tc"] = PatientTc(context);
        return ("medula.patient_medication_list_get", values);
    }
}

public sealed class MedulaDrugWorkflow : MedulaWorkflowBase
{
    public MedulaDrugWorkflow() : base(new AiWorkflowDefinition(
        "medula_ilac_bilgisi_getir", "Canlı Medula ilaç bilgisini getir",
        "Searches Medula's drug catalogue, gets one barcode's live payment/rule detail, or finds live equivalents; use operation=search, detail, or equivalents.",
        RequiredTools: ["medula.drug_search", "medula.drug_get", "medula.drug_equivalents_search"],
        Parameters: new Dictionary<string, string>
        {
            ["operation"] = "search, detail, or equivalents.", ["query"] = "Drug phrase for search.",
            ["barcode"] = "Exact barcode or inherited from ilac_barkod_eslestir.",
            ["offset"] = "Result offset.", ["limit"] = "Maximum 1-100 results."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var operation = WorkflowData.Parameter(context, "operation")?.ToLowerInvariant()
                        ?? throw new MedulaWorkflowInputException("İstenen canlı Medula ilaç işlemi belirtilmedi.");
        if (operation == "search")
        {
            var page = Page(context);
            page["query"] = Required(context, "query",
                "Medula ilaç araması için en az üç karakterli ad gerekiyor.");
            return ("medula.drug_search", page);
        }
        var values = Page(context);
        values["barcode"] = Barcode(context);
        return operation switch
        {
            "detail" => ("medula.drug_get", values),
            "equivalents" => ("medula.drug_equivalents_search", values),
            _ => throw new MedulaWorkflowInputException("İstenen canlı Medula ilaç işlemi tanınamadı.")
        };
    }
}

public sealed class MedulaReferenceSearchWorkflow : MedulaWorkflowBase
{
    public MedulaReferenceSearchWorkflow() : base(new AiWorkflowDefinition(
        "medula_referans_bilgisi_ara", "Medula doktor veya tanı bilgisini ara",
        "Searches live Medula doctor information by diploma registration number or ICD-10 diagnoses by code/description; use operation=doctor or diagnosis.",
        RequiredTools: ["medula.doctor_search", "medula.diagnosis_search"],
        Parameters: new Dictionary<string, string>
        {
            ["operation"] = "doctor or diagnosis.", ["query"] = "Diploma number or diagnosis phrase/code.",
            ["offset"] = "Diagnosis offset.", ["limit"] = "Maximum 1-100 diagnosis results."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var operation = WorkflowData.Parameter(context, "operation")?.ToLowerInvariant()
                        ?? throw new MedulaWorkflowInputException("Doktor veya tanı arama türü belirtilmedi.");
        var query = Required(context, "query", "Medula araması için sorgu değeri gerekiyor.");
        if (operation == "doctor")
            return ("medula.doctor_search", new { diploma_registration_number = query });
        if (operation == "diagnosis")
        {
            var page = Page(context);
            page["query"] = query;
            return ("medula.diagnosis_search", page);
        }
        throw new MedulaWorkflowInputException("Medula referans arama türü tanınamadı.");
    }
}

public sealed class MedulaPrescriptionFilterWorkflow : MedulaWorkflowBase
{
    public MedulaPrescriptionFilterWorkflow() : base(new AiWorkflowDefinition(
        "medula_recete_filtrelerini_getir", "Medula reçete filtrelerini getir",
        "Lists the supported Medula invoice groups and current live period choices.",
        RequiredTools: ["medula.prescription_filter_options"],
        Parameters: new Dictionary<string, string>())) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context) =>
        ("medula.prescription_filter_options", new { });
}

public sealed class MedulaPrescriptionSearchWorkflow : MedulaWorkflowBase
{
    internal const string DefaultInvoiceTypeCodes = "1,4,28,7";

    public MedulaPrescriptionSearchWorkflow() : base(new AiWorkflowDefinition(
        "medula_receteleri_ara", "Medula reçetelerini ara",
        "Searches local and/or live Medula prescription lists; source is local, live_date_range, medula_daily, medula_period, or reachable. Live searches use the requested invoice groups (normal A, B, and both C variants by default), while the same date range is always merged with the complete local archive.",
        RequiredTools: ["medula.prescriptions_search"],
        Parameters: new Dictionary<string, string>
        {
            ["source"] = "local, live_date_range, medula_daily, medula_period, or reachable.",
            ["start_date"] = "Inclusive YYYY-MM-DD.", ["end_date"] = "Inclusive YYYY-MM-DD.",
            ["day"] = "Day number for medula_daily.", ["period"] = "Exact Medula period value.",
            ["invoice_type_code"] = "One exact type code for backward-compatible single-group searches.",
            ["invoice_type_codes"] = "Comma-separated exact type codes; live default is normal A, B, and both C variants (1,4,28,7).",
            ["qr_status"] = "Optional finalized, unfinalized, or none status filter.",
            ["_period_default"] = "Internal marker: the application supplied today's default period.",
            ["include_permanently_deleted"] = "Local archive only.", ["offset"] = "Result offset.",
            ["limit"] = "Maximum 1-100 results."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var mode = WorkflowData.Parameter(context, "source")?.ToLowerInvariant() ?? "local";
        var arguments = Page(context);
        arguments["mode"] = mode;
        Add(arguments, "start_date", WorkflowData.Parameter(context, "start_date"));
        Add(arguments, "end_date", WorkflowData.Parameter(context, "end_date"));
        Add(arguments, "period", WorkflowData.Parameter(context, "period"));
        Add(arguments, "qr_status", WorkflowData.Parameter(context, "qr_status"));
        var day = WorkflowData.NullableInteger(context, "day", 1, 31);
        if (day.HasValue) arguments["day"] = day.Value;
        if (mode == "local")
        {
            Add(arguments, "invoice_type_code", WorkflowData.Parameter(context, "invoice_type_code"));
            arguments["include_permanently_deleted"] =
                WorkflowData.Boolean(context, "include_permanently_deleted");
        }
        else
        {
            var invoiceTypeCodes = WorkflowData.List(context, "invoice_type_codes");
            if (invoiceTypeCodes.Count > 0)
                arguments["invoice_type_codes"] = invoiceTypeCodes;
            else if (WorkflowData.Parameter(context, "invoice_type_code") is { } invoiceTypeCode)
                arguments["invoice_type_code"] = invoiceTypeCode;
            else
                arguments["invoice_type_codes"] = DefaultInvoiceTypeCodes.Split(',');
        }
        return ("medula.prescriptions_search", arguments);
    }

    public override async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = await base.ExecuteAsync(context, cancellationToken);
        if (result.Status != AiWorkflowStepStatus.Completed) return result;
        var count = result.Output.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.GetArrayLength()
            : 0;
        var mode = WorkflowData.Parameter(context, "source")?.ToLowerInvariant() ?? "local";
        var source = mode == "local"
            ? "yerel reçete arşivinde"
            : $"Medula'daki {InvoiceTypeDescription(context)} ile tüm yerel reçete arşivinde";
        var period = PrescriptionPeriodDescription(context, mode);
        var status = PrescriptionStatusDescription(WorkflowData.Parameter(context, "qr_status"));
        var message = count == 0
            ? $"{period} {source} {status}reçete bulunamadı."
            : $"{period} {source} {status}{count:N0} reçete bulundu.";
        return result with { Message = message };
    }

    private static void Add(IDictionary<string, object?> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
    }

    internal static string PrescriptionPeriodDescription(AiWorkflowContext context, string mode)
    {
        static string Display(DateOnly value) => value.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"));
        var startText = WorkflowData.Parameter(context, "start_date");
        var endText = WorkflowData.Parameter(context, "end_date");
        if (DateOnly.TryParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start) &&
            DateOnly.TryParseExact(endText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
        {
            if (WorkflowData.Boolean(context, "_period_default"))
                return $"Tarih belirtilmediği için yalnızca {Display(start)} günü kontrol edildi;";
            return start == end ? $"{Display(start)} günü kontrol edildi;" : $"{Display(start)}–{Display(end)} dönemi kontrol edildi;";
        }
        if (mode == "medula_daily" && WorkflowData.NullableInteger(context, "day", 1, 31) is { } day)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (day <= DateTime.DaysInMonth(today.Year, today.Month))
                return $"{Display(new DateOnly(today.Year, today.Month, day))} günü kontrol edildi;";
        }
        var period = WorkflowData.Parameter(context, "period");
        if (mode == "medula_period" && DateOnly.TryParseExact(period, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var month))
            return $"{month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR"))} dönemi kontrol edildi;";
        return "Erişilebilen dönem kontrol edildi;";
    }

    internal static string PrescriptionStatusDescription(string? status) => status?.ToLowerInvariant() switch
    {
        "unfinalized" => "sonlandırılmamış ",
        "finalized" => "sonlandırılmış ",
        "none" => "karekod durumu olmayan ",
        _ => string.Empty
    };

    private static string InvoiceTypeDescription(AiWorkflowContext context)
    {
        var codes = WorkflowData.List(context, "invoice_type_codes");
        if (codes.Count == 0 && WorkflowData.Parameter(context, "invoice_type_code") is { } singleCode)
            codes = [singleCode];
        if (codes.Count == 0)
            codes = DefaultInvoiceTypeCodes.Split(',');
        var groups = codes.Select(code => MedulaPrescriptionListTypes.All
                .FirstOrDefault(type => type.Value == code)?.Name ?? $"{code} kodlu reçete grubu")
            .Select(name => name.StartsWith("C Grubu", StringComparison.Ordinal) ? "C Grubu" : name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return groups.Length switch
        {
            0 => "A, B ve C grupları",
            1 => groups[0],
            2 => $"{groups[0]} ve {groups[1]}",
            _ => $"{string.Join(", ", groups[..^1])} ve {groups[^1]}"
        };
    }
}

public sealed class MedulaPatientPrescriptionWorkflow : MedulaWorkflowBase
{
    public MedulaPatientPrescriptionWorkflow() : base(new AiWorkflowDefinition(
        "medula_hasta_recetelerini_getir", "Hastanın reçetelerini getir",
        "Returns one patient's unified prescription history: local database records plus prescriptions currently active in Medula. Queue hasta_eslestir first when a name is supplied. Active rows are marked for the standard light-blue prescription styling.",
        RequiredTools: ["medula.patient_prescriptions_search"],
        Parameters: new Dictionary<string, string>
        {
            ["patient_tc"] = "Direct T.C. or inherited from hasta_eslestir.",
            ["start_date"] = "Optional inclusive YYYY-MM-DD.",
            ["end_date"] = "Optional inclusive YYYY-MM-DD.",
            ["qr_status"] = "Optional finalized, unfinalized, or none status filter.",
            ["offset"] = "Result offset.", ["limit"] = "Maximum 1-1000 results; defaults to the full patient history."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var values = Page(context, defaultLimit: 1000, maximumLimit: 1000);
        values["patient_tc"] = PatientTc(context);
        values["patient_name"] = WorkflowData.PatientName(context);
        Add(values, "start_date", WorkflowData.Parameter(context, "start_date"));
        Add(values, "end_date", WorkflowData.Parameter(context, "end_date"));
        Add(values, "qr_status", WorkflowData.Parameter(context, "qr_status"));
        return ("medula.patient_prescriptions_search", values);
    }

    public override async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = await base.ExecuteAsync(context, cancellationToken);
        if (result.Status != AiWorkflowStepStatus.Completed ||
            WorkflowData.Parameter(context, "start_date") is null) return result;
        var count = result.Output.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.GetArrayLength()
            : 0;
        var patient = WorkflowData.PatientName(context).Trim();
        var period = MedulaPrescriptionSearchWorkflow.PrescriptionPeriodDescription(context, "live_date_range");
        var status = MedulaPrescriptionSearchWorkflow.PrescriptionStatusDescription(
            WorkflowData.Parameter(context, "qr_status"));
        var message = count == 0
            ? $"{period} {patient} adlı hastanın {status}reçetesi bulunamadı."
            : $"{period} {patient} adlı hastanın {status}{count:N0} reçetesi bulundu.";
        return result with { Message = message };
    }

    private static void Add(IDictionary<string, object?> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
    }
}

public sealed class MedulaLatestPrescriptionWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "medula_son_receteyi_ac", "Medula'daki en son reçeteyi aç",
        "Finds and opens the latest saved prescription. It can filter by inherited patient or by exact inherited medicine barcodes; a barcode filter searches prescription sales containing those products instead of opening the pharmacy's unrelated latest prescription. This workflow is read-only and never saves a prescription or medication.",
        RequiredTools: ["sale.search_records", "medula.patient_prescriptions_search", "medula.prescriptions_search", "medula.prescription_get"],
        Parameters: new Dictionary<string, string>
        {
            ["patient_tc"] = "Optional direct T.C. or inherited from hasta_eslestir.",
            ["patient_record_id"] = "Optional exact local patient ID; combines with barcode/date filters.",
            ["barcodes"] = "Optional exact barcode list or inherited from ilac_barkod_eslestir.",
            ["start_date"] = "Optional inclusive YYYY-MM-DD; all supplied filters are combined.",
            ["end_date"] = "Optional inclusive YYYY-MM-DD.",
            ["period_days"] = "Optional relative day window.",
            ["period_months"] = "Optional relative month window.",
            ["calendar_period"] = "today, yesterday, this_week, last_week, this_month, last_month, this_year, last_year.",
            ["all_history"] = "true for the supported 10-year history window.",
            ["start_time"] = "Optional daily local HH:mm lower bound for medicine sale lookup.",
            ["end_time"] = "Optional daily local HH:mm upper bound for medicine sale lookup.",
            ["statuses"] = "Optional sale lifecycle statuses for medicine sale lookup; default excludes pending/cancelled sales.",
            ["lookback_days"] = "Local lookback when no explicit period, 1-3650; generic latest defaults to 366 days."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var patientTc = WorkflowData.PatientTc(context).Trim();
        if (patientTc.Length > 0 && (patientTc.Length != 11 || !patientTc.All(char.IsAsciiDigit)))
            return WorkflowData.Failed("Hasta T.C. kimlik numarası geçerli değil. Lütfen hastayı yeniden seçin.");
        var barcodes = WorkflowData.Barcodes(context);
        var patientRecordId = WorkflowData.PatientRecordId(context);
        var useSalesLookup = barcodes.Count > 0 || patientRecordId.HasValue && patientTc.Length == 0 ||
                             new[] { "start_time", "end_time", "statuses" }.Any(key => WorkflowData.Parameter(context, key) is not null);
        var hasExplicitPeriod = new[] { "start_date", "end_date", "period_days", "period_months", "calendar_period", "lookback_days" }
            .Any(key => WorkflowData.Parameter(context, key) is not null) || WorkflowData.Boolean(context, "all_history");
        var rangeContext = context;
        if (!hasExplicitPeriod)
            rangeContext = context with { Parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>())
            {
                [barcodes.Count > 0 || patientRecordId.HasValue ? "all_history" : "period_days"] =
                    barcodes.Count > 0 || patientRecordId.HasValue ? "true" : "365"
            } };
        else if (WorkflowData.Parameter(context, "lookback_days") is not null &&
                 WorkflowData.Parameter(context, "start_date") is null &&
                 WorkflowData.Parameter(context, "period_days") is null &&
                 WorkflowData.Parameter(context, "period_months") is null &&
                 WorkflowData.Parameter(context, "calendar_period") is null)
            rangeContext = context with { Parameters = new Dictionary<string, string>(context.Parameters!)
            {
                ["start_date"] = (DateOnly.TryParseExact(WorkflowData.Parameter(context, "end_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var lookbackEnd) ? lookbackEnd : DateOnly.FromDateTime(DateTime.Today))
                    .AddDays(-(WorkflowData.Integer(context, "lookback_days", 366, 1, 3650) - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            } };
        var (start, end) = WorkflowData.DateRange(rangeContext);
        AiWorkflowToolExecution? search;
        var resultProperty = "items";
        if (useSalesLookup)
        {
            search = await WorkflowData.Execute(context, "sale.search_records", new
            {
                barcodes,
                start_date = start,
                end_date = end,
                start_time = WorkflowData.Parameter(context, "start_time"),
                end_time = WorkflowData.Parameter(context, "end_time"),
                patient_record_id = patientRecordId,
                patient_tc = patientTc.Length > 0 ? patientTc : null,
                statuses = WorkflowData.List(context, "statuses") is { Count: > 0 } statuses
                    ? statuses : new[] { "completed", "medula_reported", "reconciliation_required" },
                has_prescription = true,
                sale_types = new[] { "prescription" },
                include_refunds = false,
                include_lines = false,
                sort_direction = "newest_first",
                offset = 0,
                limit = 1
            }, cancellationToken);
            resultProperty = "sales";
        }
        else if (patientTc.Length == 11 && patientTc.All(char.IsDigit))
        {
            search = await WorkflowData.Execute(context, "medula.patient_prescriptions_search", new
            {
                patient_tc = patientTc,
                patient_name = WorkflowData.PatientName(context),
                start_date = hasExplicitPeriod ? start : null,
                end_date = hasExplicitPeriod ? end : null,
                offset = 0,
                limit = 100
            }, cancellationToken);
        }
        else
        {
            search = await WorkflowData.Execute(context, "medula.prescriptions_search", new
            {
                mode = "local",
                start_date = start,
                end_date = end,
                include_permanently_deleted = false,
                offset = 0,
                limit = 100
            }, cancellationToken);
        }
        if (search is null)
            return WorkflowData.Missing(useSalesLookup
                ? "sale.search_records"
                : patientTc.Length == 11
                ? "medula.patient_prescriptions_search"
                : "medula.prescriptions_search");
        if (search.Status == AiToolExecutionStatus.ConfirmationRequired)
            return AwaitingApproval(search);
        if (!search.IsSuccess)
            return WorkflowData.Failed(barcodes.Count > 0
                ? "Seçilen ilacı içeren reçeteli satışlar aranamadı."
                : "En son Medula reçetesi aranamadı.");
        if (!search.Output.TryGetProperty(resultProperty, out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return WorkflowData.Failed(barcodes.Count > 0
                ? "Reçeteli satış araması geçerli bir sonuç listesi döndürmedi."
                : "Medula reçete araması geçerli bir sonuç listesi döndürmedi.");

        var latest = items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                           PrescriptionNumber(item).Length > 0)
            .Select((item, index) => new { Item = item.Clone(), Index = index, Date = LatestDate(item) })
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.Index)
            .FirstOrDefault();
        if (latest is null)
            return new AiWorkflowStepResult(
                AiWorkflowStepStatus.Completed,
                barcodes.Count > 0
                    ? "Seçilen ilacı içeren reçeteli satış bulunamadı."
                    : patientTc.Length == 11
                    ? "Bu hastanın erişilebilen Medula reçetesi bulunamadı."
                    : $"{start}–{end} döneminde açılabilecek Medula reçetesi bulunamadı.",
                WorkflowData.RowsOutput([]));

        var selected = latest.Item;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prescription_number"] = PrescriptionNumber(selected)
        };
        Add(arguments, "patient_tc", WorkflowData.Text(selected, "patient_tc"));
        Add(arguments, "electronic_prescription_number",
            WorkflowData.Text(selected, "electronic_prescription_number"));
        Add(arguments, "medula_filter_date",
            IsoDate(WorkflowData.Text(selected, "medula_filter_date")));
        Add(arguments, "invoice_type_code", WorkflowData.Text(selected, "invoice_type_code"));
        var detail = await WorkflowData.Execute(
            context, "medula.prescription_get", arguments, cancellationToken);
        if (detail is null) return WorkflowData.Missing("medula.prescription_get");
        if (detail.Status == AiToolExecutionStatus.ConfirmationRequired)
            return AwaitingApproval(detail);
        if (!detail.IsSuccess)
            return WorkflowData.Failed("En son reçete bulundu ancak Medula ayrıntısı açılamadı.");
        return MedulaWorkflowPresentation.Create(Definition.DisplayName, "medula.prescription_get", detail.Output);
    }

    private static AiWorkflowStepResult AwaitingApproval(AiWorkflowToolExecution execution)
    {
        if (execution.PendingAction is null)
            return WorkflowData.Failed("Canlı Medula sorgusu için onay kaydı oluşturulamadı.");
        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.AwaitingApproval,
            "Canlı Medula sorgusu hasta veya reçete ekranını açacak. Devam edilsin mi?",
            JsonSerializer.SerializeToElement(new
            {
                pending_action_id = execution.PendingAction.Id,
                tool = execution.ToolName
            }));
    }

    private static DateTime LatestDate(JsonElement item)
    {
        foreach (var name in new[] { "timestamp", "last_transaction_date", "prescription_date", "medula_filter_date" })
        {
            var value = WorkflowData.Text(item, name).Trim();
            if (DateTime.TryParse(
                    value, CultureInfo.GetCultureInfo("tr-TR"),
                    DateTimeStyles.AllowWhiteSpaces, out var date))
                return date;
        }
        return DateTime.MinValue;
    }

    private static string PrescriptionNumber(JsonElement item)
    {
        var number = WorkflowData.Text(item, "prescription_number").Trim();
        return number.Length > 0 ? number : WorkflowData.Text(item, "prescription_no").Trim();
    }

    private static string? IsoDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParse(
                value, CultureInfo.GetCultureInfo("tr-TR"),
                DateTimeStyles.AllowWhiteSpaces, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }

    private static void Add(IDictionary<string, object?> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values[key] = value.Trim();
    }
}

public sealed class MedulaPrescriptionInformationWorkflow : MedulaWorkflowBase
{
    public MedulaPrescriptionInformationWorkflow() : base(new AiWorkflowDefinition(
        "medula_recete_bilgisi_getir", "Medula reçete bilgisini getir",
        "Gets prescription detail, local audit history, live warnings, or live treatment scheme; use operation=detail, history, warnings, or treatment_scheme.",
        RequiredTools: ["medula.prescription_get", "medula.prescription_history",
            "medula.prescription_warnings_get", "medula.patient_treatment_scheme_get"],
        Parameters: new Dictionary<string, string>
        {
            ["operation"] = "detail, history, warnings, or treatment_scheme.",
            ["prescription_number"] = "Exact Medula prescription number.",
            ["prescription_uid"] = "Stable UUID required for local history.",
            ["patient_tc"] = "Optional disambiguating T.C.",
            ["electronic_prescription_number"] = "Optional e-prescription number.",
            ["medula_filter_date"] = "Optional YYYY-MM-DD.", ["invoice_type_code"] = "Optional type code.",
            ["offset"] = "Result offset.", ["limit"] = "Maximum 1-100 results."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var operation = WorkflowData.Parameter(context, "operation")?.ToLowerInvariant() ?? "detail";
        if (operation == "history")
        {
            var page = Page(context);
            page["prescription_uid"] = Inherited(context, "prescription_uid") is { Length: > 0 } uid
                ? uid
                : throw new MedulaWorkflowInputException("Reçete geçmişi için reçete UUID bilgisi gerekiyor.");
            return ("medula.prescription_history", page);
        }
        var number = Inherited(context, "prescription_number");
        if (string.IsNullOrWhiteSpace(number))
            throw new MedulaWorkflowInputException("Bu Medula sorgusu için reçete numarası gerekiyor.");
        if (operation == "warnings")
            return ("medula.prescription_warnings_get", new { prescription_number = number });
        if (operation == "treatment_scheme")
        {
            var page = Page(context);
            page["prescription_number"] = number;
            return ("medula.patient_treatment_scheme_get", page);
        }
        if (operation != "detail")
            throw new MedulaWorkflowInputException("Medula reçete bilgi işlemi tanınamadı.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prescription_number"] = number
        };
        Add(values, "patient_tc", Inherited(context, "patient_tc"));
        Add(values, "electronic_prescription_number",
            Inherited(context, "electronic_prescription_number"));
        Add(values, "medula_filter_date", Inherited(context, "medula_filter_date"));
        Add(values, "invoice_type_code", Inherited(context, "invoice_type_code"));
        return ("medula.prescription_get", values);

        static void Add(IDictionary<string, object?> values, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
        }
    }

    private static string Inherited(AiWorkflowContext context, string key)
    {
        var direct = WorkflowData.Parameter(context, key);
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
        foreach (var output in context.Outputs.Reverse().Select(pair => pair.Value))
        {
            foreach (var candidate in Candidates(output))
            {
                var value = WorkflowData.Text(candidate, key).Trim();
                if (value.Length > 0) return value;
            }
        }
        return string.Empty;

        static IEnumerable<JsonElement> Candidates(JsonElement output)
        {
            if (output.ValueKind != JsonValueKind.Object) yield break;
            yield return output;
            if (output.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("prescription", out var prescription) && prescription.ValueKind == JsonValueKind.Object)
                yield return prescription;
            if (output.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object).Take(1))
                    yield return row;
        }
    }
}

public sealed class MedulaElectronicPrescriptionWorkflow : MedulaWorkflowBase
{
    public MedulaElectronicPrescriptionWorkflow() : base(new AiWorkflowDefinition(
        "medula_erecete_sorgula", "Medula e-reçete sorgula",
        "Searches or opens a live Medula e-prescription by e-prescription number or tracking number; use operation=search or detail and mode=number or tracking.",
        RequiredTools: ["medula.e_prescriptions_search", "medula.e_prescription_get"],
        Parameters: new Dictionary<string, string>
        {
            ["operation"] = "search or detail.", ["mode"] = "number or tracking.",
            ["patient_tc"] = "Direct T.C. or inherited from hasta_eslestir.",
            ["electronic_prescription_number"] = "E-prescription number.",
            ["tracking_number"] = "Tracking number.", ["prescription_date"] = "Optional YYYY-MM-DD.",
            ["selected_electronic_prescription_number"] = "Exact result selected for detail.",
            ["offset"] = "Search offset.", ["limit"] = "Maximum 1-50 search results."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context)
    {
        var operation = WorkflowData.Parameter(context, "operation")?.ToLowerInvariant() ?? "search";
        var mode = WorkflowData.Parameter(context, "mode")?.ToLowerInvariant() ?? "number";
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = mode,
            ["patient_tc"] = PatientTc(context)
        };
        Add(values, "electronic_prescription_number",
            WorkflowData.Parameter(context, "electronic_prescription_number"));
        Add(values, "tracking_number", WorkflowData.Parameter(context, "tracking_number"));
        Add(values, "prescription_date", WorkflowData.Parameter(context, "prescription_date"));
        if (operation == "detail")
        {
            Add(values, "selected_electronic_prescription_number",
                WorkflowData.Parameter(context, "selected_electronic_prescription_number"));
            return ("medula.e_prescription_get", values);
        }
        if (operation != "search")
            throw new MedulaWorkflowInputException("E-reçete işlemi tanınamadı.");
        values["offset"] = WorkflowData.Integer(context, "offset", 0, 0, 100_000);
        values["limit"] = WorkflowData.Integer(context, "limit", 25, 1, 50);
        return ("medula.e_prescriptions_search", values);

        static void Add(IDictionary<string, object?> values, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
        }
    }
}

public sealed class MedulaPatientReportDetailWorkflow : MedulaWorkflowBase
{
    public MedulaPatientReportDetailWorkflow() : base(new AiWorkflowDefinition(
        "medula_hasta_rapor_detayini_getir", "Hastanın Medula rapor detayını getir",
        "Gets one exact live Medula medical-report detail; queue hasta_eslestir first for a patient name.",
        RequiredTools: ["medula.patient_report_get"],
        Parameters: new Dictionary<string, string>
        {
            ["patient_tc"] = "Direct T.C. or inherited from hasta_eslestir.",
            ["report_number"] = "Exact report or tracking number."
        })) { }

    protected override (string Tool, object Arguments) BuildCall(AiWorkflowContext context) =>
        ("medula.patient_report_get", new
        {
            patient_tc = PatientTc(context),
            report_number = Required(context, "report_number",
                "Rapor detayı için rapor numarası gerekiyor.")
        });
}

internal sealed class MedulaWorkflowInputException(string message) : Exception(message);

internal static class MedulaWorkflowPresentation
{
    private static readonly HashSet<string> InfrastructureKeys = new(StringComparer.Ordinal)
    {
        "success", "title", "message", "warning", "offset", "limit", "total_count", "next_offset"
    };

    public static AiWorkflowStepResult Create(string displayName, string tool, JsonElement output)
    {
        var success = !output.TryGetProperty("success", out var successValue) ||
                      successValue.ValueKind != JsonValueKind.False;
        var message = WorkflowData.Text(output, "message").Trim();
        var warning = WorkflowData.Text(output, "warning").Trim();
        if (!success)
            return WorkflowData.Failed(
                message.Length > 0 ? message : "Medula sorgusu sonuç döndürmedi.",
                new { tool, warning });

        var rows = new List<IReadOnlyDictionary<string, string>>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalCount = 0;
        if (output.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
            ReadRemoteTables(tables, rows, labels, ref totalCount);
        if (rows.Count == 0 && output.TryGetProperty("editable_rows", out var editable) &&
            editable.ValueKind == JsonValueKind.Array)
            ReadEditableRows(editable, rows, labels);
        if (rows.Count == 0 && output.TryGetProperty("fields", out var fields))
        {
            if (fields.ValueKind == JsonValueKind.Array)
                ReadRemoteFields(fields, rows, labels);
            else if (fields.ValueKind == JsonValueKind.Object)
                rows.Add(WorkflowData.FlatRow(fields));
        }
        if (rows.Count == 0) ReadKnownArrays(output, rows);
        if (rows.Count == 0)
        {
            var scalar = output.EnumerateObject()
                .Where(property => !InfrastructureKeys.Contains(property.Name) &&
                                   property.Value.ValueKind is not JsonValueKind.Array and not JsonValueKind.Object)
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText(),
                    StringComparer.Ordinal);
            if (scalar.Count > 0) rows.Add(scalar);
        }

        if (totalCount == 0)
            totalCount = output.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var parsed)
                ? parsed
                : rows.Count;
        var columns = rows.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var isPrescriptionList = tool is "medula.prescriptions_search" or "medula.patient_prescriptions_search";
        if (isPrescriptionList) labels["total_amount"] = "Toplam Tutar";
        var visibleColumns = isPrescriptionList
            ? new[]
            {
                "prescription_number", "electronic_prescription_number", "patient_name",
                "prescription_date", "last_transaction_date", "coverage", "qr_status", "total_amount"
            }.Where(columns.Contains).ToArray()
            : columns.Take(10).ToArray();
        var prescription = tool == "medula.prescription_get"
            ? BuildPrescription(output)
            : null;
        var normalized = WorkflowData.RowsOutput(rows, new
        {
            total_count = totalCount,
            columns,
            visible_columns = visibleColumns,
            column_labels = labels,
            tool,
            warning,
            suppress_empty_presentation = tool == "medula.patient_reports_search" && rows.Count == 0,
            row_background_field = tool is "medula.prescriptions_search" or "medula.patient_prescriptions_search"
                ? "is_live_on_medula"
                : null,
            prescription = prescription is null ? (JsonElement?)null : output.Clone()
        });
        var resultMessage = message.Length > 0
            ? message
            : rows.Count == 0
                ? $"{displayName}: gösterilecek kayıt bulunamadı."
                : $"{displayName}: {totalCount:N0} kayıt bulundu.";
        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            resultMessage,
            normalized,
            Prescription: prescription);
    }

    internal static AiWorkflowPrescriptionPresentation? BuildPrescription(JsonElement output)
    {
        var number = WorkflowData.Text(output, "prescription_number").Trim();
        if (number.Length == 0 || !output.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Object)
            return null;

        var prescriptionFields = new List<AiWorkflowPrescriptionField>();
        var doctorFields = new List<AiWorkflowPrescriptionField>();
        foreach (var property in fields.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            if (string.IsNullOrWhiteSpace(value)) continue;
            var item = new AiWorkflowPrescriptionField(PrescriptionFieldLabel(property.Name), value);
            if (property.Name.StartsWith("doctor_", StringComparison.Ordinal)) doctorFields.Add(item);
            else prescriptionFields.Add(item);
        }

        var drugs = output.TryGetProperty("drugs", out var drugValues) && drugValues.ValueKind == JsonValueKind.Array
            ? drugValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => new AiWorkflowPrescriptionDrug(
                    WorkflowData.Text(value, "barcode"),
                    WorkflowData.Text(value, "name"),
                    WorkflowData.Text(value, "quantity"),
                    JoinUsage(value),
                    WorkflowData.Text(value, "report"),
                    WorkflowData.Text(value, "available_date"),
                    WorkflowData.Text(value, "message")))
                .ToArray()
            : [];
        var diagnoses = output.TryGetProperty("diagnoses", out var diagnosisValues) && diagnosisValues.ValueKind == JsonValueKind.Array
            ? diagnosisValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => new AiWorkflowPrescriptionDiagnosis(
                    WorkflowData.Text(value, "code"), WorkflowData.Text(value, "description")))
                .ToArray()
            : [];
        var payments = output.TryGetProperty("payments", out var paymentValues) && paymentValues.ValueKind == JsonValueKind.Array
            ? paymentValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => new AiWorkflowPrescriptionField(
                    WorkflowData.Text(value, "label").Trim().TrimEnd(':'), WorkflowData.Text(value, "value")))
                .Where(value => value.Label.Length > 0)
                .ToArray()
            : [];
        return new AiWorkflowPrescriptionPresentation(
            number,
            WorkflowData.Text(output, "electronic_prescription_number"),
            WorkflowData.Text(output, "patient_name"),
            WorkflowData.Text(output, "patient_tc"),
            prescriptionFields,
            doctorFields,
            drugs,
            diagnoses,
            payments,
            output.TryGetProperty("is_live_on_medula", out var liveValue) &&
            liveValue.ValueKind == JsonValueKind.True);

        static string JoinUsage(JsonElement value)
        {
            var parts = new[]
            {
                WorkflowData.Text(value, "dose_count"),
                WorkflowData.Text(value, "dose_amount"),
                WorkflowData.Text(value, "period"),
                WorkflowData.Text(value, "period_unit")
            }.Where(part => !string.IsNullOrWhiteSpace(part));
            return string.Join(" / ", parts);
        }
    }

    private static string PrescriptionFieldLabel(string key) => key switch
    {
        "prescription_date" => "Reçete Tarihi",
        "dispensing_date" => "Veriliş Tarihi",
        "coverage" => "Kapsam",
        "patient_type" => "Hasta Türü",
        "insurance_type" => "Sigortalı Türü",
        "qr_status" => "Karekod Durumu",
        "invoice_type" => "Fatura Türü",
        "protocol_number" => "Protokol Numarası",
        "facility_code" => "Tesis Kodu",
        "doctor_registration_number" => "Doktor Tescil Numarası",
        "doctor_first_name" => "Doktor Adı",
        "doctor_last_name" => "Doktor Soyadı",
        "doctor_branch" => "Doktor Branşı",
        "doctor_certificate" => "Doktor Sertifikası",
        _ => WorkflowData.Label(key)
    };

    private static void ReadRemoteTables(
        JsonElement tables,
        ICollection<IReadOnlyDictionary<string, string>> rows,
        IDictionary<string, string> labels,
        ref int totalCount)
    {
        var tableArray = tables.EnumerateArray()
            .Where(table => table.ValueKind == JsonValueKind.Object).ToArray();
        foreach (var table in tableArray)
        {
            var title = WorkflowData.Text(table, "title");
            var headers = table.TryGetProperty("headers", out var headerValues) &&
                          headerValues.ValueKind == JsonValueKind.Array
                ? headerValues.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
                : [];
            var keys = UniqueKeys(headers, labels);
            if (table.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var parsed))
                totalCount += parsed;
            if (!table.TryGetProperty("rows", out var rowValues) ||
                rowValues.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var values in rowValues.EnumerateArray()
                         .Where(value => value.ValueKind == JsonValueKind.Array))
            {
                var cells = values.EnumerateArray().ToArray();
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                if (tableArray.Length > 1)
                {
                    row["section"] = title;
                    labels["section"] = "Bölüm";
                }
                for (var index = 0; index < Math.Min(keys.Length, cells.Length); index++)
                    row[keys[index]] = Cell(cells[index]);
                rows.Add(row);
            }
        }
    }

    private static void ReadRemoteFields(
        JsonElement fields,
        ICollection<IReadOnlyDictionary<string, string>> rows,
        IDictionary<string, string> labels)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateArray()
                     .Where(value => value.ValueKind == JsonValueKind.Object))
        {
            var label = WorkflowData.Text(field, "label").Trim();
            if (label.Length == 0) continue;
            var key = UniqueKey(Slug(label), labels.Keys);
            labels[key] = label.TrimEnd(':');
            row[key] = WorkflowData.Text(field, "value");
        }
        if (row.Count > 0) rows.Add(row);
    }

    private static void ReadEditableRows(
        JsonElement editable,
        ICollection<IReadOnlyDictionary<string, string>> rows,
        IDictionary<string, string> labels)
    {
        foreach (var item in editable.EnumerateArray()
                     .Where(value => value.ValueKind == JsonValueKind.Object))
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["medicine_name"] = WorkflowData.Text(item, "medication_name")
            };
            if (item.TryGetProperty("values", out var values) &&
                values.ValueKind == JsonValueKind.Array)
                foreach (var value in values.EnumerateArray()
                             .Where(cell => cell.ValueKind == JsonValueKind.Object))
                {
                    var label = WorkflowData.Text(value, "label").Trim().TrimEnd(':');
                    if (label.Length == 0) continue;
                    var key = UniqueKey(Slug(label), labels.Keys.Concat(row.Keys));
                    labels[key] = label;
                    row[key] = WorkflowData.Text(value, "value");
                }
            rows.Add(row);
        }
    }

    private static void ReadKnownArrays(
        JsonElement output,
        ICollection<IReadOnlyDictionary<string, string>> rows)
    {
        var names = new[]
        {
            "items", "drugs", "diagnoses", "payments", "patient_fields",
            "prescription_fields", "doctor_fields", "invoice_types", "periods"
        };
        var present = names.Where(name =>
            output.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Array).ToArray();
        foreach (var name in present)
        {
            var array = output.GetProperty(name);
            foreach (var item in array.EnumerateArray()
                         .Where(value => value.ValueKind == JsonValueKind.Object))
            {
                var row = new Dictionary<string, string>(
                    WorkflowData.FlatRow(item), StringComparer.Ordinal);
                if (present.Length > 1) row["section"] = SectionLabel(name);
                rows.Add(row);
            }
        }
        ReadMessages(output, "errors", "Hata", rows);
        ReadMessages(output, "notices", "Bilgi", rows);
        ReadMessages(output, "confirmations", "Onay", rows);
        if (output.TryGetProperty("drug_errors", out var drugErrors) &&
            drugErrors.ValueKind == JsonValueKind.Array)
            foreach (var item in drugErrors.EnumerateArray()
                         .Where(value => value.ValueKind == JsonValueKind.Object))
                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["message_type"] = "İlaç Hatası",
                    ["barcode"] = WorkflowData.Text(item, "barcode"),
                    ["detail"] = WorkflowData.Text(item, "error")
                });
    }

    private static void ReadMessages(
        JsonElement output,
        string property,
        string type,
        ICollection<IReadOnlyDictionary<string, string>> rows)
    {
        if (!output.TryGetProperty(property, out var values) ||
            values.ValueKind != JsonValueKind.Array)
            return;
        foreach (var value in values.EnumerateArray()
                     .Where(item => item.ValueKind == JsonValueKind.String))
            rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["message_type"] = type,
                ["detail"] = value.GetString() ?? string.Empty
            });
    }

    private static string[] UniqueKeys(
        IReadOnlyList<string> headers,
        IDictionary<string, string> labels)
    {
        var used = new HashSet<string>(labels.Keys, StringComparer.Ordinal);
        return headers.Select(header =>
        {
            var key = UniqueKey(Slug(header), used);
            used.Add(key);
            labels[key] = string.IsNullOrWhiteSpace(header) ? "Değer" : header.Trim();
            return key;
        }).ToArray();
    }

    private static string UniqueKey(string candidate, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.Ordinal);
        var root = candidate.Length == 0 ? "value" : candidate;
        var key = root;
        for (var suffix = 2; used.Contains(key); suffix++) key = $"{root}_{suffix}";
        return key;
    }

    private static string Slug(string value)
    {
        var normalized = value.ToLower(CultureInfo.GetCultureInfo("tr-TR"))
            .Replace('ç', 'c').Replace('ğ', 'g').Replace('ı', 'i')
            .Replace('ö', 'o').Replace('ş', 's').Replace('ü', 'u');
        var builder = new StringBuilder(normalized.Length);
        var separator = false;
        foreach (var character in normalized)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                separator = false;
            }
            else if (!separator && builder.Length > 0)
            {
                builder.Append('_');
                separator = true;
            }
        }
        return builder.ToString().Trim('_');
    }

    private static string Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText()
    };

    private static string SectionLabel(string name) => name switch
    {
        "items" => "Sonuçlar", "drugs" => "İlaçlar", "diagnoses" => "Tanılar",
        "payments" => "Ödemeler", "patient_fields" => "Hasta Bilgileri",
        "prescription_fields" => "Reçete Bilgileri", "doctor_fields" => "Doktor Bilgileri",
        "invoice_types" => "Fatura Türleri", "periods" => "Dönemler", _ => name
    };
}
