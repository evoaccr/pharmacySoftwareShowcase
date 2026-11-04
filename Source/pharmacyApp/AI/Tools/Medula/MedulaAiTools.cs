using System.Globalization;
using System.Text.Json;
using pharmacyApp.Modules.Medula;

namespace pharmacyApp.AI.Tools.Medula;

public static class MedulaAiTools
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    public static AiToolCatalog CreateCatalog(IMedulaAiOperations? operations = null) =>
        new(Create(operations));

    public static IReadOnlyList<IAiTool> Create(IMedulaAiOperations? operations = null)
    {
        var client = operations ?? new MedulaAiOperations();
        return
        [
            SessionStatus(client),
            SessionLogin(client),
            SessionRestart(client),
            PrescriptionFilterOptions(client),
            PrescriptionReportSummary(client),
            PrescriptionsSearch(client),
            PatientPrescriptionsSearch(client),
            PrescriptionGet(client),
            PrescriptionHistory(client),
            ElectronicPrescriptionsSearch(client),
            ElectronicPrescriptionGet(client),
            .. MedulaClinicalAiTools.Create(client)
        ];
    }

    private static IAiTool SessionStatus(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.session_status",
            "Returns the current Medula backend session and browser state without exposing browser internals.",
            AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>(),
            new Dictionary<string, string>
            {
                ["success"] = "boolean",
                ["state"] = "string",
                ["message"] = "string",
                ["browser_recreated"] = "boolean",
                ["state_restored"] = "boolean"
            },
            ["medula", "session", "status"]),
        async (parameters, cancellationToken) =>
        {
            Reader(parameters).RejectUnknown();
            return Session(await client.GetSessionStateAsync(cancellationToken));
        });

    private static IAiTool SessionLogin(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.session_login",
            "Logs the configured pharmacy account into Medula; credentials remain in application settings and are never tool parameters.",
            AiToolRisks.LowRiskWrite,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["force_new_session"] = Parameter(
                    "boolean",
                    "When true, discards the current Medula session before logging in.",
                    defaultValue: false)
            },
            SessionReturns(),
            ["medula", "session", "login"]),
        async (parameters, cancellationToken) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("force_new_session");
            return Session(await client.LoginAsync(
                reader.OptionalBoolean("force_new_session", false),
                cancellationToken));
        });

    private static IAiTool SessionRestart(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.session_restart",
            "Recreates the Medula backend browser and optionally restores the last business screen after fatal browser or Medula errors.",
            AiToolRisks.LowRiskWrite,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["restore_previous_state"] = Parameter(
                    "boolean",
                    "When true, restores the last supported Medula business state after restart.",
                    defaultValue: true)
            },
            SessionReturns(),
            ["medula", "session", "recovery"]),
        async (parameters, cancellationToken) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("restore_previous_state");
            return Session(await client.RestartSessionAsync(
                reader.OptionalBoolean("restore_previous_state", true),
                cancellationToken));
        });

    private static IAiTool PrescriptionFilterOptions(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.prescription_filter_options",
            "Lists supported Medula invoice-type codes and currently available period values for prescription searches.",
            AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>(),
            new Dictionary<string, string>
            {
                ["invoice_types"] = "array<{value:string,name:string}>",
                ["periods"] = "array<{value:string,name:string}>"
            },
            ["medula", "prescription", "filters"]),
        async (parameters, cancellationToken) =>
        {
            Reader(parameters).RejectUnknown();
            var periods = await client.LoadPrescriptionPeriodsAsync(cancellationToken);
            return new MedulaAiFilterOptionsResult(
                MedulaPrescriptionListTypes.All
                    .Select(option => new MedulaAiFilterOption(option.Value, option.Name)).ToArray(),
                periods.Select(option => new MedulaAiFilterOption(option.Value, option.Name)).ToArray());
        });

    private static IAiTool PrescriptionReportSummary(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.aggregate_prescription_reports",
            "Counts locally stored prescriptions with and without at least one medical-report-marked drug in one date range without returning patient data.",
            AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["start_date"] = Parameter("string", "Inclusive ISO date (yyyy-MM-dd).", required: true),
                ["end_date"] = Parameter("string", "Inclusive ISO date (yyyy-MM-dd).", required: true)
            },
            new Dictionary<string, string>
            {
                ["reported_count"] = "integer",
                ["unreported_count"] = "integer",
                ["total_count"] = "integer"
            },
            ["medula", "prescription", "medical-report", "aggregate", "local"]),
        async (parameters, cancellationToken) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("start_date", "end_date");
            var start = ParseIsoDate(reader.RequireString("start_date"), "start_date", DateOnly.MinValue);
            var end = ParseIsoDate(reader.RequireString("end_date"), "end_date", start);
            if (end < start) throw new AiToolValidationException("Parameter 'end_date' cannot be earlier than 'start_date'.");
            if (end.DayNumber - start.DayNumber + 1 > 3660)
                throw new AiToolValidationException("Prescription report aggregation supports at most 3660 days per call.");
            var items = await client.SearchLocalPrescriptionsAsync(start, end, null, false, cancellationToken);
            var reported = items.Count(item => !string.IsNullOrWhiteSpace(item.MedicalReport) ||
                item.LocalSnapshot?.Drugs.Any(drug => !string.IsNullOrWhiteSpace(drug.Report)) == true);
            return new
            {
                reported_count = reported,
                unreported_count = items.Count - reported,
                total_count = items.Count
            };
        });
    private static IAiTool PrescriptionsSearch(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.prescriptions_search",
            "Searches prescription summaries using one explicit local or live Medula filter mode and returns a bounded page of results.",
            AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["mode"] = Parameter(
                    "string",
                    "Search source and filter mode.",
                    allowedValues: ["local", "live_date_range", "medula_daily", "medula_period", "reachable"],
                    defaultValue: "local"),
                ["start_date"] = Parameter("string", "ISO date (yyyy-MM-dd); used by local and live_date_range modes."),
                ["end_date"] = Parameter("string", "ISO date (yyyy-MM-dd); used by local and live_date_range modes."),
                ["day"] = Parameter("integer", "Day number used only by medula_daily mode."),
                ["period"] = Parameter("string", "Medula period value returned by medula.prescription_filter_options; used only by medula_period mode."),
                ["invoice_type_code"] = Parameter("string", "One Medula invoice-type code returned by medula.prescription_filter_options; optional for local mode and backward-compatible single-group live searches."),
                ["invoice_type_codes"] = Parameter("array", "Distinct Medula invoice-type codes for a live search. When neither invoice parameter is supplied, normal A, B, and both C variants are searched.", itemsType: "string"),
                ["qr_status"] = Parameter("string", "Optional QR finalization status filter.", allowedValues: ["finalized", "unfinalized", "none"]),
                ["include_permanently_deleted"] = Parameter("boolean", "Include permanently deleted local prescriptions; valid only in local mode.", defaultValue: false),
                ["offset"] = Parameter("integer", "Zero-based result offset.", defaultValue: 0),
                ["limit"] = Parameter("integer", "Maximum number of returned prescriptions (1-100).", defaultValue: 50)
            },
            PagedPrescriptionReturns(),
            ["medula", "prescription", "search", "local-history"]),
        async (parameters, cancellationToken) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown(
                "mode", "start_date", "end_date", "day", "period", "invoice_type_code", "invoice_type_codes",
                "qr_status", "include_permanently_deleted", "offset", "limit");
            var mode = reader.EnumValue(
                "mode", "local", "local", "live_date_range", "medula_daily", "medula_period", "reachable");
            var startText = reader.OptionalString("start_date");
            var endText = reader.OptionalString("end_date");
            var day = reader.OptionalNullableInteger("day", 1, 31);
            var period = reader.OptionalString("period");
            var typeCode = ValidateInvoiceTypeCode(reader.OptionalString("invoice_type_code"));
            var typeCodes = ValidateInvoiceTypeCodes(reader.OptionalStringArray("invoice_type_codes", 12));
            if (typeCode.Length > 0 && typeCodes.Count > 0)
                throw new AiToolValidationException(
                    "Use either 'invoice_type_code' or 'invoice_type_codes', not both.");
            var qrStatus = reader.EnumValue("qr_status", string.Empty, string.Empty, "finalized", "unfinalized", "none");
            var includeDeleted = reader.OptionalBoolean("include_permanently_deleted", false);
            var offset = reader.OptionalInteger("offset", 0, 0, 100000);
            var limit = reader.OptionalInteger("limit", 50, 1, 100);

            IReadOnlyList<MedulaPrescriptionListItem> items;
            bool success;
            string message;
            string? warning;
            if (mode is "local" or "live_date_range")
            {
                if (day is not null || period is not null)
                    throw new AiToolValidationException(
                        "Parameters 'day' and 'period' are not valid for date-range modes.");
                var start = ParseIsoDate(startText, "start_date", DateOnly.FromDateTime(DateTime.Today));
                var end = ParseIsoDate(endText, "end_date", start);
                if (end < start)
                    throw new AiToolValidationException("Parameter 'end_date' cannot be earlier than 'start_date'.");
                var maximumDays = mode == "local" ? 366 : 31;
                if (end.DayNumber - start.DayNumber + 1 > maximumDays)
                    throw new AiToolValidationException(
                        $"Mode '{mode}' supports at most {maximumDays} days per call.");
                if (mode == "local")
                {
                    if (typeCodes.Count > 0)
                        throw new AiToolValidationException(
                            "Parameter 'invoice_type_codes' is valid only in live modes.");
                    items = await client.SearchLocalPrescriptionsAsync(
                        start, end, NullIfEmpty(typeCode), includeDeleted, cancellationToken);
                    success = true;
                    message = "Local prescription records loaded.";
                    warning = null;
                }
                else
                {
                    if (includeDeleted)
                        throw new AiToolValidationException(
                            "Parameter 'include_permanently_deleted' is valid only in local mode.");
                    var results = await SearchInvoiceGroupsAsync(
                        ResolveLiveInvoiceTypeCodes(typeCode, typeCodes),
                        code => client.SearchLivePrescriptionsAsync(start, end, code, cancellationToken));
                    (items, success, message, warning) = CombineLiveResults(results);
                }
            }
            else if (mode == "medula_daily")
            {
                RejectDateRange(startText, endText, period, includeDeleted, "medula_daily");
                var results = await SearchInvoiceGroupsAsync(
                    ResolveLiveInvoiceTypeCodes(typeCode, typeCodes),
                    code => client.SearchMedulaFilteredPrescriptionsAsync(
                        true, day ?? DateTime.Today.Day, string.Empty, code, cancellationToken));
                (items, success, message, warning) = CombineLiveResults(results);
            }
            else if (mode == "medula_period")
            {
                RejectDateRange(startText, endText, null, includeDeleted, "medula_period");
                if (day is not null)
                    throw new AiToolValidationException("Parameter 'day' is valid only in medula_daily mode.");
                var periodValue = ValidatePeriod(period);
                var results = await SearchInvoiceGroupsAsync(
                    ResolveLiveInvoiceTypeCodes(typeCode, typeCodes),
                    code => client.SearchMedulaFilteredPrescriptionsAsync(
                        false, 0, periodValue, code, cancellationToken));
                (items, success, message, warning) = CombineLiveResults(results);
            }
            else
            {
                RejectDateRange(startText, endText, period, includeDeleted, "reachable");
                if (day is not null)
                    throw new AiToolValidationException("Parameter 'day' is not valid in reachable mode.");
                var results = await SearchInvoiceGroupsAsync(
                    ResolveLiveInvoiceTypeCodes(typeCode, typeCodes),
                    code => client.SearchReachablePrescriptionsAsync(code, cancellationToken));
                (items, success, message, warning) = CombineLiveResults(results);
            }
            if (mode != "local")
            {
                var liveSucceeded = success;
                var (localStart, localEnd) = LocalRange(mode, startText, endText, day, period);
                // The live Medula screen requires one invoice group, but the local
                // counterpart must never inherit that UI limitation.  Merge every
                // local prescription in the requested period with the selected live group.
                var local = await client.SearchLocalPrescriptionsAsync(
                    localStart, localEnd, invoiceTypeCode: null, false, cancellationToken);
                items = MedulaAiOperations.MergePatientPrescriptions(local, items);
                if (!liveSucceeded)
                {
                    warning = string.Join(" | ", new[] { warning, message }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                    message = "Yerel reçete arşivi tarandı; canlı Medula sonucu doğrulanamadı.";
                }
                success = true;
            }
            items = items.Where(item => MatchesQrStatus(item, qrStatus)).ToArray();
            return Page(items.Select(PrescriptionListItem).ToArray(), success, message, warning, offset, limit);
        });

    private static IAiTool PatientPrescriptionsSearch(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.patient_prescriptions_search",
            "Returns one patient's unified prescription history by merging local database records with prescriptions currently active in Medula.",
            AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["patient_tc"] = Parameter("string", "Patient Turkish identity number (11 digits).", required: true),
                ["patient_name"] = Parameter("string", "Optional display name used only to label the returned Medula section."),
                ["start_date"] = Parameter("string", "Optional inclusive ISO start date (yyyy-MM-dd)."),
                ["end_date"] = Parameter("string", "Optional inclusive ISO end date (yyyy-MM-dd)."),
                ["qr_status"] = Parameter("string", "Optional QR finalization status filter.", allowedValues: ["finalized", "unfinalized", "none"]),
                ["offset"] = Parameter("integer", "Zero-based result offset.", defaultValue: 0),
                ["limit"] = Parameter("integer", "Maximum number of returned prescriptions (1-1000).", defaultValue: 1000)
            },
            PagedPrescriptionReturns(),
            ["medula", "prescription", "patient", "search"]),
        async (parameters, cancellationToken) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("patient_tc", "patient_name", "start_date", "end_date", "qr_status", "offset", "limit");
            var tc = ValidateTc(reader.RequireString("patient_tc"), "patient_tc");
            var name = reader.OptionalString("patient_name") ?? string.Empty;
            var startText = reader.OptionalString("start_date");
            var endText = reader.OptionalString("end_date");
            if (startText is null != (endText is null))
                throw new AiToolValidationException("Parameters 'start_date' and 'end_date' must be supplied together.");
            DateOnly? start = startText is null ? null : ParseIsoDate(startText, "start_date", DateOnly.MinValue);
            DateOnly? end = endText is null ? null : ParseIsoDate(endText, "end_date", start!.Value);
            if (start.HasValue && end < start)
                throw new AiToolValidationException("Parameter 'end_date' cannot be earlier than 'start_date'.");
            var qrStatus = reader.EnumValue("qr_status", string.Empty, string.Empty, "finalized", "unfinalized", "none");
            var offset = reader.OptionalInteger("offset", 0, 0, 100000);
            var limit = reader.OptionalInteger("limit", 1000, 1, 1000);
            var result = await client.SearchPatientPrescriptionsAsync(tc, name, cancellationToken);
            var filtered = result.Prescriptions.Where(item =>
                    (!start.HasValue || PrescriptionDate(item) is { } date && date >= start.Value && date <= end!.Value) &&
                    MatchesQrStatus(item, qrStatus))
                .ToArray();
            return Page(
                filtered.Select(PrescriptionListItem).ToArray(),
                result.IsSuccess,
                result.Message,
                result.WarningText,
                offset,
                limit);
        });

    private static IAiTool PrescriptionGet(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.prescription_get",
            "Opens one saved Medula prescription by prescription number and returns a compact structured snapshot without raw page text or DOM fields.",
            AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["prescription_number"] = Parameter("string", "Medula prescription number.", required: true),
                ["patient_tc"] = Parameter("string", "Optional patient Turkish identity number used to disambiguate the prescription."),
                ["electronic_prescription_number"] = Parameter("string", "Optional e-prescription number used as additional lookup evidence."),
                ["medula_filter_date"] = Parameter("string", "Optional ISO Medula filter date (yyyy-MM-dd)."),
                ["invoice_type_code"] = Parameter("string", "Optional Medula invoice-type code." ),
                ["prefer_local"] = Parameter("boolean", "Use the local archived snapshot first when the selected list row is stored locally.", defaultValue: false)
            },
            PrescriptionDetailReturns(),
            ["medula", "prescription", "detail"]),
        async (parameters, cancellationToken) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown(
                "prescription_number", "patient_tc", "electronic_prescription_number",
                "medula_filter_date", "invoice_type_code", "prefer_local");
            var number = ValidateCode(reader.RequireString("prescription_number"), "prescription_number", 40);
            var tc = reader.OptionalString("patient_tc") is { } tcValue
                ? ValidateTc(tcValue, "patient_tc")
                : string.Empty;
            var ePrescription = reader.OptionalString("electronic_prescription_number") is { } eValue
                ? ValidateCode(eValue, "electronic_prescription_number", 40).ToUpperInvariant()
                : string.Empty;
            var filterDate = reader.OptionalString("medula_filter_date") is { } dateValue
                ? ParseIsoDate(dateValue, "medula_filter_date", DateOnly.MinValue)
                : (DateOnly?)null;
            var typeCode = ValidateInvoiceTypeCode(reader.OptionalString("invoice_type_code"));
            var preferLocal = reader.OptionalBoolean("prefer_local", false);
            var snapshot = await client.OpenPrescriptionAsync(
                new MedulaPrescriptionLocator(
                    number,
                    tc,
                    ElectronicPrescriptionNumber: ePrescription,
                    MedulaFilterDate: filterDate,
                    TypeValue: typeCode,
                    Source: preferLocal ? "ai_tool_local_first" : "ai_tool"),
                cancellationToken);
            return PrescriptionDetail(snapshot, number);
        });

    private static IAiTool PrescriptionHistory(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.prescription_history",
            "Returns versioned save, update, delete, and permanent-delete events for one locally stored Medula prescription.",
            AiToolRisks.ReadOnly,
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["prescription_uid"] = Parameter("string", "Stable prescription UUID returned by Medula prescription tools.", required: true),
                ["offset"] = Parameter("integer", "Zero-based history offset.", defaultValue: 0),
                ["limit"] = Parameter("integer", "Maximum number of history events (1-100).", defaultValue: 50)
            },
            new Dictionary<string, string>
            {
                ["success"] = "boolean",
                ["message"] = "string",
                ["items"] = "array<{prescription_uid:string,version_number:integer,action_type:string,update_datetime:datetime,user_id:string,changes:string,is_current:boolean}>",
                ["offset"] = "integer",
                ["limit"] = "integer",
                ["total_count"] = "integer",
                ["next_offset"] = "integer|null",
                ["warning"] = "string|null"
            },
            ["medula", "prescription", "history", "audit"]),
        async (parameters, cancellationToken) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("prescription_uid", "offset", "limit");
            if (!Guid.TryParse(reader.RequireString("prescription_uid"), out var uid))
                throw new AiToolValidationException("Parameter 'prescription_uid' must be a UUID.");
            var offset = reader.OptionalInteger("offset", 0, 0, 100000);
            var limit = reader.OptionalInteger("limit", 50, 1, 100);
            var rows = await client.LoadPrescriptionHistoryAsync(uid, cancellationToken);
            var items = rows.Select(row => new MedulaAiHistoryItem(
                row.PrescriptionUid.ToString(), row.VersionNumber, row.ActionType,
                row.UpdateDateTime, row.UserId, row.Changes, row.IsCurrent)).ToArray();
            return Page(items, true, "Prescription history loaded.", null, offset, limit);
        });

    private static IAiTool ElectronicPrescriptionsSearch(IMedulaAiOperations client) =>
        new DelegatingAiTool(
            ElectronicPrescriptionSearchDefinition("medula.e_prescriptions_search",
                "Searches Medula e-prescriptions by e-prescription number or tracking number and returns bounded summaries.",
                detail: false),
            async (parameters, cancellationToken) =>
            {
                var query = ReadElectronicPrescriptionQuery(parameters, includeSelectionNumber: false);
                var result = await client.SearchElectronicPrescriptionsAsync(
                    query.PatientTc,
                    query.ElectronicPrescriptionNumber,
                    query.TrackingNumber,
                    query.PrescriptionDate,
                    query.ByTracking,
                    cancellationToken);
                return Page(
                    result.Prescriptions.Select(ElectronicPrescriptionItem).ToArray(),
                    result.IsSuccess,
                    result.Message,
                    result.WarningText,
                    query.Offset,
                    query.Limit);
            });

    private static IAiTool ElectronicPrescriptionGet(IMedulaAiOperations client) =>
        new DelegatingAiTool(
            ElectronicPrescriptionSearchDefinition(
                "medula.e_prescription_get",
                "Searches and opens one e-prescription detail using its number or a uniquely matching tracking-number result.",
                detail: true),
            async (parameters, cancellationToken) =>
            {
                var query = ReadElectronicPrescriptionQuery(parameters, includeSelectionNumber: true);
                var result = await client.SearchElectronicPrescriptionsAsync(
                    query.PatientTc,
                    query.ElectronicPrescriptionNumber,
                    query.TrackingNumber,
                    query.PrescriptionDate,
                    query.ByTracking,
                    cancellationToken);
                if (!result.IsSuccess)
                    return EmptyElectronicPrescriptionDetail(result.Message, result.WarningText);
                var selected = query.SelectionElectronicPrescriptionNumber is { Length: > 0 }
                    ? result.Prescriptions.FirstOrDefault(item => item.ElectronicPrescriptionNumber.Equals(
                        query.SelectionElectronicPrescriptionNumber,
                        StringComparison.OrdinalIgnoreCase))
                    : result.Prescriptions.Count == 1 ? result.Prescriptions[0] : null;
                if (selected is null)
                    return EmptyElectronicPrescriptionDetail(
                        "The search did not identify exactly one e-prescription. Supply 'selected_electronic_prescription_number'.",
                        null);
                var detail = await client.OpenElectronicPrescriptionDetailAsync(
                    selected, query.PatientTc, cancellationToken);
                return ElectronicPrescriptionDetail(detail);
            });

    private static AiToolDefinition ElectronicPrescriptionSearchDefinition(
        string name,
        string description,
        bool detail)
    {
        var parameters = new Dictionary<string, AiToolParameterDefinition>
        {
            ["mode"] = Parameter("string", "Search mode.", allowedValues: ["number", "tracking"], defaultValue: "number"),
            ["patient_tc"] = Parameter("string", "Patient Turkish identity number (11 digits).", required: true),
            ["electronic_prescription_number"] = Parameter("string", "E-prescription number required in number mode."),
            ["tracking_number"] = Parameter("string", "Tracking number required in tracking mode."),
            ["prescription_date"] = Parameter("string", "Optional ISO prescription date (yyyy-MM-dd), primarily for tracking searches.")
        };
        if (detail)
        {
            parameters["selected_electronic_prescription_number"] = Parameter(
                "string",
                "Selects one e-prescription when a tracking-number search returns multiple results.");
        }
        else
        {
            parameters["offset"] = Parameter("integer", "Zero-based result offset.", defaultValue: 0);
            parameters["limit"] = Parameter("integer", "Maximum number of returned results (1-50).", defaultValue: 25);
        }

        return
        Definition(
            name,
            description,
            AiToolRisks.ReadOnly,
            parameters,
            detail
                ? new Dictionary<string, string>
                {
                    ["success"] = "boolean",
                    ["message"] = "string",
                    ["patient_tc"] = "string",
                    ["electronic_prescription_number"] = "string",
                    ["patient_name"] = "string",
                    ["patient_fields"] = "array<{label:string,value:string}>",
                    ["prescription_fields"] = "array<{label:string,value:string}>",
                    ["doctor_fields"] = "array<{label:string,value:string}>",
                    ["drugs"] = "array<object>",
                    ["diagnoses"] = "array<{code:string,description:string}>",
                    ["warning"] = "string|null"
                }
                : new Dictionary<string, string>
                {
                    ["success"] = "boolean",
                    ["message"] = "string",
                    ["items"] = "array<{tracking_number:string,electronic_prescription_number:string,prescription_date:string,doctor_name:string,prescription_type:string,facility_name:string}>",
                    ["offset"] = "integer",
                    ["limit"] = "integer",
                    ["total_count"] = "integer",
                    ["next_offset"] = "integer|null",
                    ["warning"] = "string|null"
                },
            ["medula", "e-prescription", detail ? "detail" : "search"]);
    }

    private static ElectronicPrescriptionQuery ReadElectronicPrescriptionQuery(
        JsonElement parameters,
        bool includeSelectionNumber)
    {
        var reader = Reader(parameters);
        if (includeSelectionNumber)
            reader.RejectUnknown(
                "mode", "patient_tc", "electronic_prescription_number", "tracking_number",
                "prescription_date", "selected_electronic_prescription_number");
        else
            reader.RejectUnknown(
                "mode", "patient_tc", "electronic_prescription_number", "tracking_number",
                "prescription_date", "offset", "limit");
        var mode = reader.EnumValue("mode", "number", "number", "tracking");
        var tc = ValidateTc(reader.RequireString("patient_tc"), "patient_tc");
        var ePrescription = reader.OptionalString("electronic_prescription_number")?.ToUpperInvariant() ?? string.Empty;
        var tracking = reader.OptionalString("tracking_number") ?? string.Empty;
        if (mode == "number")
        {
            ePrescription = ValidateCode(ePrescription, "electronic_prescription_number", 40).ToUpperInvariant();
            if (tracking.Length > 0)
                throw new AiToolValidationException("Parameter 'tracking_number' is valid only in tracking mode.");
        }
        else
        {
            tracking = ValidateCode(tracking, "tracking_number", 60);
        }
        var date = reader.OptionalString("prescription_date") is { } value
            ? ParseIsoDate(value, "prescription_date", DateOnly.MinValue).ToString("dd/MM/yyyy", InvariantCulture)
            : string.Empty;
        var selected = reader.OptionalString("selected_electronic_prescription_number")?.ToUpperInvariant() ?? string.Empty;
        if (!includeSelectionNumber && selected.Length > 0)
            throw new AiToolValidationException(
                "Parameter 'selected_electronic_prescription_number' is valid only for medula.e_prescription_get.");
        if (selected.Length > 0) selected = ValidateCode(selected, "selected_electronic_prescription_number", 40);
        return new ElectronicPrescriptionQuery(
            tc,
            ePrescription,
            tracking,
            date,
            mode == "tracking",
            selected,
            includeSelectionNumber ? 0 : reader.OptionalInteger("offset", 0, 0, 100000),
            includeSelectionNumber ? 1 : reader.OptionalInteger("limit", 25, 1, 50));
    }

    private static MedulaAiPrescriptionDetail PrescriptionDetail(
        MedulaPrescriptionSnapshot? snapshot,
        string requestedNumber)
    {
        if (snapshot is null)
            return new MedulaAiPrescriptionDetail(
                false, "Prescription was not found in Medula.", "Prescription was not found in Medula.",
                requestedNumber, string.Empty, string.Empty, 0, string.Empty, string.Empty,
                false, false, false, new Dictionary<string, string>(), [], [], []);
        var page = snapshot.Page;
        var fields = page.FieldValues ?? new Dictionary<string, string>();
        string Field(params string[] keys) => keys.Select(key => fields.GetValueOrDefault(key)?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var prescriptionNumber = snapshot.SourceListItem?.PrescriptionNumber
            ?? Field("medula:prescription-no");
        var ePrescriptionNumber = Field("medula:e-prescription-no");
        var normalizedFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prescription_date"] = Field("f:t29"),
            ["dispensing_date"] = Field("f:t31"),
            ["coverage"] = Field("medula:coverage"),
            ["patient_type"] = Field("medula:patient-type"),
            ["insurance_type"] = Field("medula:insurance-type"),
            ["qr_status"] = Field("medula:qr-status"),
            ["invoice_type"] = Field("medula:invoice-type"),
            ["protocol_number"] = Field("f:t24"),
            ["facility_code"] = Field("f:t33"),
            ["doctor_registration_number"] = Field("f:text37"),
            ["doctor_first_name"] = Field("f:t43"),
            ["doctor_last_name"] = Field("f:t39"),
            ["doctor_branch"] = Field("medula:branch"),
            ["doctor_certificate"] = Field("f:m7")
        };
        return new MedulaAiPrescriptionDetail(
            page.IsSuccess,
            page.Message,
            page.WarningText,
            prescriptionNumber,
            ePrescriptionNumber,
            snapshot.PrescriptionUid?.ToString() ?? string.Empty,
            snapshot.VersionNumber,
            snapshot.SourceListItem?.PatientTcIdentityNo ?? Field("f:t18"),
            snapshot.PatientDisplayName,
            snapshot.IsSavedOnMedula,
            snapshot.IsLocalArchive,
            snapshot.SourceListItem?.IsLiveOnMedula == true,
            normalizedFields,
            snapshot.Drugs.Select(Drug).ToArray(),
            snapshot.Diagnoses.Select(diagnosis => new MedulaAiDiagnosis(
                diagnosis.Code, diagnosis.Description)).ToArray(),
            page.SavedSummary?.PaymentFields.Select(payment =>
                new MedulaAiLabelValue(payment.Label, payment.Value)).ToArray() ?? []);
    }

    private static MedulaAiPrescriptionListItem PrescriptionListItem(MedulaPrescriptionListItem item)
    {
        var ePrescriptionNumber = item.LocalSnapshot?.Page.FieldValues?
            .GetValueOrDefault("medula:e-prescription-no") ?? string.Empty;
        return new MedulaAiPrescriptionListItem(
            item.PrescriptionNumber,
            ePrescriptionNumber,
            item.PrescriptionUid?.ToString() ?? string.Empty,
            item.VersionNumber,
            item.PatientTcIdentityNo,
            item.PatientName,
            item.PrescriptionDate,
            item.LastTransactionDate,
            item.MedulaFilterDate,
            item.CoverageDisplay,
            item.QrCodeStatusDisplay,
            item.TypeDisplay,
            item.TypeValue,
            item.IsLiveOnMedula,
            item.IsStoredLocally,
            item.IsDeleted,
            item.IsPermanentlyDeleted,
            item.LastUser)
        {
            TotalAmount = PrescriptionTotalAmount(item),
            MedicalReport = item.MedicalReport,
            HasMedicalReport = !string.IsNullOrWhiteSpace(item.MedicalReport) ||
                               item.LocalSnapshot?.Drugs.Any(drug => !string.IsNullOrWhiteSpace(drug.Report)) == true
        };
    }

    private static string PrescriptionTotalAmount(MedulaPrescriptionListItem item) =>
        item.LocalSnapshot?.Page.SavedSummary?.PaymentFields
            .FirstOrDefault(value => value.Label.Trim().TrimEnd(':').Equals(
                "Toplam Tutar", StringComparison.CurrentCultureIgnoreCase))?.Value?.Trim()
        ?? string.Empty;

    private static bool MatchesQrStatus(MedulaPrescriptionListItem item, string status) => status switch
    {
        "finalized" => item.IsQrFinalized,
        "unfinalized" => item.IsQrUnfinalized,
        "none" => item.HasNoQrStatus,
        _ => true
    };

    private static DateOnly? PrescriptionDate(MedulaPrescriptionListItem item)
    {
        foreach (var value in new[] { item.MedulaFilterDate, item.PrescriptionDate, item.LastTransactionDate })
            foreach (var format in new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm" })
                if (DateTime.TryParseExact(value?.Trim(), format, InvariantCulture,
                        DateTimeStyles.None, out var parsed))
                    return DateOnly.FromDateTime(parsed);
        return null;
    }

    private static (DateOnly Start, DateOnly End) LocalRange(
        string mode, string? startText, string? endText, int? day, string? period)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (mode == "live_date_range")
            return (ParseIsoDate(startText, "start_date", today), ParseIsoDate(endText, "end_date", today));
        if (mode == "medula_daily")
        {
            var selectedDay = day ?? today.Day;
            selectedDay = Math.Min(selectedDay, DateTime.DaysInMonth(today.Year, today.Month));
            var selected = new DateOnly(today.Year, today.Month, selectedDay);
            return (selected, selected);
        }
        if (mode == "medula_period" && DateOnly.TryParseExact(
                period, "yyyyMMdd", InvariantCulture, DateTimeStyles.None, out var month))
            return (new DateOnly(month.Year, month.Month, 1),
                new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month)));
        return (today.AddYears(-20), today);
    }

    private static MedulaAiElectronicPrescriptionItem ElectronicPrescriptionItem(
        MedulaElectronicPrescriptionListItem item) =>
        new(item.TrackingNumber, item.ElectronicPrescriptionNumber, item.PrescriptionDate,
            item.DoctorName, item.PrescriptionType, item.FacilityName);

    private static MedulaAiElectronicPrescriptionDetail ElectronicPrescriptionDetail(
        MedulaElectronicPrescriptionDetail detail) =>
        new(
            detail.IsOpen && string.IsNullOrWhiteSpace(detail.WarningText),
            detail.Message,
            detail.WarningText,
            detail.PatientIdentityNumber,
            detail.ElectronicPrescriptionNumber,
            detail.PatientName,
            detail.PatientFields.Select(Field).ToArray(),
            detail.PrescriptionFields.Select(Field).ToArray(),
            detail.DoctorFields.Select(Field).ToArray(),
            detail.Drugs.Select(drug => new MedulaAiDrug(
                drug.Barcode, drug.Name, drug.Quantity, null, string.Empty, string.Empty,
                drug.Usage, drug.UsageRoute, string.Empty, string.Empty, drug.Description)).ToArray(),
            detail.Diagnoses.Select(diagnosis => new MedulaAiDiagnosis(
                diagnosis.Code, diagnosis.Description)).ToArray(),
            detail.SignatureText,
            detail.CanContinue);

    private static MedulaAiElectronicPrescriptionDetail EmptyElectronicPrescriptionDetail(
        string message,
        string? warning) =>
        new(false, message, warning, string.Empty, string.Empty, string.Empty,
            [], [], [], [], [], string.Empty, false);

    private static MedulaAiLabelValue Field(MedulaElectronicPrescriptionDetailField field) =>
        new(field.Label, field.Value);

    private static MedulaAiDrug Drug(MedulaDrugEntry drug) => new(
        drug.Barcode,
        drug.Name,
        drug.Quantity,
        string.IsNullOrWhiteSpace(drug.OriginalQuantity) ? null : drug.OriginalQuantity,
        drug.Period,
        drug.PeriodUnit,
        drug.DoseCount,
        drug.DoseAmount,
        drug.Report,
        drug.AvailableDate,
        drug.DispensingMessage);

    private static MedulaAiSessionResult Session(MedulaBrowserOperationResult result) =>
        new(result.IsSuccess, result.State.ToString(), result.Message,
            result.BrowserRecreated, result.StateRestored);

    private static MedulaAiPagedResult<T> Page<T>(
        IReadOnlyList<T> items,
        bool success,
        string message,
        string? warning,
        int offset,
        int limit)
    {
        var page = items.Skip(offset).Take(limit).ToArray();
        var next = offset + page.Length < items.Count ? offset + page.Length : (int?)null;
        return new MedulaAiPagedResult<T>(
            success, message, page, offset, limit, items.Count, next, warning);
    }

    private static AiToolDefinition Definition(
        string name,
        string description,
        string risk,
        IReadOnlyDictionary<string, AiToolParameterDefinition> parameters,
        IReadOnlyDictionary<string, string> returns,
        IReadOnlyList<string> tags) =>
        new(name, description, risk, parameters, returns, tags);

    private static AiToolParameterDefinition Parameter(
        string type,
        string description,
        bool required = false,
        IReadOnlyList<string>? allowedValues = null,
        object? defaultValue = null,
        string? itemsType = null) =>
        new(type, description, required, allowedValues, defaultValue, itemsType);

    private static IReadOnlyDictionary<string, string> SessionReturns() =>
        new Dictionary<string, string>
        {
            ["success"] = "boolean",
            ["state"] = "string",
            ["message"] = "string",
            ["browser_recreated"] = "boolean",
            ["state_restored"] = "boolean"
        };

    private static IReadOnlyDictionary<string, string> PagedPrescriptionReturns() =>
        new Dictionary<string, string>
        {
            ["success"] = "boolean",
            ["message"] = "string",
            ["items"] = "array<prescription_summary>",
            ["offset"] = "integer",
            ["limit"] = "integer",
            ["total_count"] = "integer",
            ["next_offset"] = "integer|null",
            ["warning"] = "string|null"
        };

    private static IReadOnlyDictionary<string, string> PrescriptionDetailReturns() =>
        new Dictionary<string, string>
        {
            ["success"] = "boolean",
            ["message"] = "string",
            ["prescription_number"] = "string",
            ["electronic_prescription_number"] = "string",
            ["prescription_uid"] = "string",
            ["version_number"] = "integer",
            ["patient_tc"] = "string",
            ["patient_name"] = "string",
            ["is_saved_on_medula"] = "boolean",
            ["is_local_archive"] = "boolean",
            ["is_live_on_medula"] = "boolean",
            ["fields"] = "object",
            ["drugs"] = "array<object>",
            ["diagnoses"] = "array<{code:string,description:string}>",
            ["payments"] = "array<{label:string,value:string}>",
            ["warning"] = "string|null"
        };

    private static AiToolParameterReader Reader(JsonElement parameters) => new(parameters);

    private static string ValidateTc(string value, string parameterName)
    {
        var normalized = value.Trim();
        if (normalized.Length != 11 || normalized.Any(character => !char.IsDigit(character)))
            throw new AiToolValidationException(
                $"Parameter '{parameterName}' must contain exactly 11 digits.");
        return normalized;
    }

    private static string ValidateCode(string value, string parameterName, int maximumLength)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Length > maximumLength
            || normalized.Any(character => char.IsControl(character)))
            throw new AiToolValidationException(
                $"Parameter '{parameterName}' must contain 1-{maximumLength} printable characters.");
        return normalized;
    }

    private static string ValidateInvoiceTypeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim();
        if (!MedulaPrescriptionListTypes.All.Any(type => type.Value == normalized))
            throw new AiToolValidationException(
                "Parameter 'invoice_type_code' must be a value returned by medula.prescription_filter_options.");
        return normalized;
    }

    private static IReadOnlyList<string> ValidateInvoiceTypeCodes(IReadOnlyList<string> values)
    {
        foreach (var value in values)
            ValidateInvoiceTypeCode(value);
        return values;
    }

    private static IReadOnlyList<string> ResolveLiveInvoiceTypeCodes(
        string singleCode,
        IReadOnlyList<string> multipleCodes) => multipleCodes.Count > 0
        ? multipleCodes
        : singleCode.Length > 0
            ? [singleCode]
            : ["1", "4", "28", "7"];

    private static async Task<IReadOnlyList<MedulaPrescriptionListResult>> SearchInvoiceGroupsAsync(
        IReadOnlyList<string> codes,
        Func<string, Task<MedulaPrescriptionListResult>> search)
    {
        var results = new List<MedulaPrescriptionListResult>(codes.Count);
        foreach (var code in codes)
            results.Add(await search(code));
        return results;
    }

    private static (IReadOnlyList<MedulaPrescriptionListItem> Items, bool Success, string Message, string? Warning)
        CombineLiveResults(IReadOnlyList<MedulaPrescriptionListResult> results)
    {
        IReadOnlyList<MedulaPrescriptionListItem> items = [];
        foreach (var result in results)
            items = MedulaAiOperations.MergePatientPrescriptions(items, result.Prescriptions);
        var failures = results.Where(result => !result.IsSuccess && result.Prescriptions.Count == 0)
            .SelectMany(result => new[] { result.WarningText, result.Message })
            .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            !value.Contains("Kayıt Bulunamamıştır", StringComparison.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return (items, results.Any(result => result.IsSuccess) || failures.Length == 0,
            "Medula reçete grupları tarandı.", failures.Length == 0 ? null : string.Join(" | ", failures));
    }

    private static string RequireInvoiceTypeCode(string value, string mode)
    {
        if (value.Length == 0)
            throw new AiToolValidationException(
                $"Parameter 'invoice_type_code' is required in {mode} mode.");
        return value;
    }

    private static string ValidatePeriod(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != 8 || normalized.Any(character => !char.IsDigit(character))
            || !DateOnly.TryParseExact(
                normalized,
                "yyyyMMdd",
                InvariantCulture,
                DateTimeStyles.None,
                out _))
            throw new AiToolValidationException(
                "Parameter 'period' must be a value returned by medula.prescription_filter_options.");
        return normalized;
    }

    private static DateOnly ParseIsoDate(
        string? value,
        string parameterName,
        DateOnly defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (!DateOnly.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                InvariantCulture,
                DateTimeStyles.None,
                out var result))
            throw new AiToolValidationException(
                $"Parameter '{parameterName}' must use yyyy-MM-dd format.");
        return result;
    }

    private static void RejectDateRange(
        string? startDate,
        string? endDate,
        string? period,
        bool includeDeleted,
        string mode)
    {
        if (startDate is not null || endDate is not null)
            throw new AiToolValidationException(
                $"Parameters 'start_date' and 'end_date' are not valid in {mode} mode.");
        if (period is not null)
            throw new AiToolValidationException($"Parameter 'period' is not valid in {mode} mode.");
        if (includeDeleted)
            throw new AiToolValidationException(
                "Parameter 'include_permanently_deleted' is valid only in local mode.");
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed record ElectronicPrescriptionQuery(
        string PatientTc,
        string ElectronicPrescriptionNumber,
        string TrackingNumber,
        string PrescriptionDate,
        bool ByTracking,
        string SelectionElectronicPrescriptionNumber,
        int Offset,
        int Limit);
}
