using System.Globalization;
using System.Text.Json;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Tools;
using pharmacyApp.AI.Tools.Sale;
using pharmacyApp.AI.Workflows;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Infrastructure;
using Npgsql;
using NpgsqlTypes;

internal static class WorkflowDataReliabilityTests
{
    public static async Task RunAsync()
    {
        await VerifySaleFilterContractAsync();
        await VerifySalesWorkflowAsync();
        await VerifyHighDiscountWorkflowAsync();
        await VerifyPatientSalesContentsContractAsync();
        await VerifyLatestPrescriptionAsync();
        await VerifyCalendarPeriodsAsync();
        await VerifyFocusedWorkflowInputsAsync();
        await PrescriptionFastPathRegressionTests.RunAsync();
        if (Environment.GetEnvironmentVariable("PHARMACYAPP_WORKFLOW_DATA_DB_TEST") == "1")
            await VerifyDatabaseReadOnlyAsync();
        Console.WriteLine("PASS workflow-data-reliability (time/patient/prescription filters, pagination, no broadening)");
    }

    private static async Task VerifyDatabaseReadOnlyAsync()
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync();
        await using (var readOnly = new NpgsqlCommand("SET default_transaction_read_only=on", connection))
            await readOnly.ExecuteNonQueryAsync();
        await using var newest = new NpgsqlCommand("SELECT MAX(\"timestamp\") FROM public.sales", connection);
        var value = await newest.ExecuteScalarAsync();
        Require(value is DateTime, "The local test database contains sale fixtures.");
        var end = ((DateTime)value!).Date.AddDays(1);
        var start = end.AddDays(-7);
        var service = new RetailSaleRecordQueryService();
        var list = new RetailSalesListRepository();
        foreach (var (from, to) in new[] { (new TimeOnly(8, 0), new TimeOnly(12, 0)), (new TimeOnly(22, 0), new TimeOnly(6, 0)) })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var filter = new RetailSaleRecordFilter(start, end, ["retail", "prescription"], false,
                [], [], [], [], null, "", "", null, false, null, null, StartTime: from, EndTime: to);
            var page = await service.SearchAsync(new(filter, 0, 10, false, true), deadline.Token);
            await using var expected = new NpgsqlCommand("""
                SELECT 'P' || record_id::text
                FROM public.sales
                WHERE "timestamp">=@start AND "timestamp"<@end AND sale_status<>'pending'
                  AND (CASE WHEN @from>@to
                       THEN "timestamp"::time>=@from OR date_trunc('minute', "timestamp")::time<=@to
                       ELSE "timestamp"::time>=@from AND date_trunc('minute', "timestamp")::time<=@to END)
                ORDER BY "timestamp" DESC, 'P' || record_id::text DESC
                """, connection) { CommandTimeout = 20 };
            expected.Parameters.AddWithValue("start", NpgsqlDbType.Timestamp, start);
            expected.Parameters.AddWithValue("end", NpgsqlDbType.Timestamp, end);
            expected.Parameters.AddWithValue("from", NpgsqlDbType.Time, from);
            expected.Parameters.AddWithValue("to", NpgsqlDbType.Time, to);
            var references = new List<string>();
            await using (var reader = await expected.ExecuteReaderAsync(deadline.Token))
                while (await reader.ReadAsync(deadline.Token)) references.Add(reader.GetString(0));
            Require(page.TotalCount == references.Count && page.Records.Select(row => row.ReferenceCode).SequenceEqual(references.Take(10)),
                "Real database time-filtered totals and newest-page identities match an independent read-only query.");
            var moduleRows = await list.LoadSalesAsync(new(start, end,
                TransactionTypes: RetailTransactionTypes.RetailSale | RetailTransactionTypes.PrescriptionSale,
                StartTime: from, EndTime: to), deadline.Token);
            Require(moduleRows.Select(row => row.ReferenceCode).SequenceEqual(references),
                "The opened Sales module and chatbot query preserve the same time-filtered records.");
        }
        Console.WriteLine("PASS read-only local DB: daytime/overnight totals, page identities, and Sales-module filter parity");
    }

    private static async Task VerifyFocusedWorkflowInputsAsync()
    {
        var unused = new ScriptedExecutor((_, _) => throw new InvalidOperationException("Navigation must not query data tools."));
        var modules = await new OpenModulesWorkflow().ExecuteAsync(Context(unused, new()
        {
            ["module_ids"] = "products.catalog,sales.retail-list,products.stock"
        }));
        Require(modules.Status == AiWorkflowStepStatus.Failed && modules.ModuleActions is not { Count: > 0 },
            "Requesting three modules fails clearly instead of silently dropping one.");
        var matcher = new ScriptedExecutor((tool, args) =>
        {
            Require(args.GetProperty("query").GetString() == "Parol 500/30",
                "An optional medicine query supplies only the qualified name, not the full request.");
            return Success(tool, new { products = new[]
            {
                new { barcode = "8699500000030", name = "PAROL 500 MG 30 TABLET", is_on_market = true },
                new { barcode = "8699500000020", name = "PAROL 500 MG 20 TABLET", is_on_market = true }
            } });
        });
        var match = await new DrugBarcodeMatchWorkflow().ExecuteAsync(Context(matcher, new() { ["query"] = "Parol 500/30" }) with
        {
            OriginalRequest = "Parol 500/30 ürününün son 60 günlük satışlarını getir",
            EffectiveRequest = "Parol 500/30 ürününün son 60 günlük satışlarını getir"
        });
        Require(match.Status == AiWorkflowStepStatus.Completed &&
                match.Output.GetProperty("barcodes").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["8699500000030"]),
            "Optional medicine-name input still preserves package qualifiers instead of selecting the similarly named 20-tablet product.");
    }

    private static async Task VerifySaleFilterContractAsync()
    {
        var queries = new CapturingQueries();
        var catalog = new SaleAiToolCatalog(recordQueries: queries);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            start_date = "2026-08-01", end_date = "2026-08-31", start_time = "22:00", end_time = "06:00",
            patient_tc = "12289124650", patient_record_id = 42, has_prescription = true,
            barcodes = new[] { "8699500000001" }, limit = 10
        });
        await catalog.ExecuteAsync("sale.search_records", arguments);
        var filter = queries.Search!.Filter;
        Require(filter.StartTime == new TimeOnly(22, 0) && filter.EndTime == new TimeOnly(6, 0),
            "Both local time bounds, including overnight windows, reach the database query before pagination.");
        Require(filter.PatientTc == "12289124650" && filter.PatientRecordId == 42 && filter.HasPrescription == true &&
                filter.Barcodes.SequenceEqual(["8699500000001"]),
            "Patient, medicine, and prescription-existence constraints combine rather than replacing each other.");
        await catalog.ExecuteAsync("sale.aggregate_records", arguments);
        Require(queries.Aggregate!.Filter.StartTime == filter.StartTime && queries.Aggregate.Filter.PatientTc == filter.PatientTc,
            "Listing and aggregation use the same time and patient filters.");
        foreach (var invalid in new object[]
                 {
                     new { start_date = "2026-08-01", end_date = "2026-08-31", start_time = "25:00" },
                     new { start_date = "2026-08-01", end_date = "2026-08-31", end_time = "8am" },
                     new { start_date = "2026-08-01", end_date = "2026-08-31", patient_tc = "invalid" },
                     new { start_date = "2026-08-01", end_date = "2026-08-31", limit = 0 },
                     new { start_date = "2026-08-01", end_date = "2026-08-31", limit = 201 },
                     new { start_date = "2026-08-01", end_date = "2026-08-31", offset = -1 }
                 })
        {
            try { await catalog.ExecuteAsync("sale.search_records", JsonSerializer.SerializeToElement(invalid)); }
            catch (AiToolValidationException) { continue; }
            throw new InvalidOperationException("Invalid date/time, identity, and page bounds must not be silently ignored.");
        }
    }

    private static async Task VerifyCalendarPeriodsAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-((7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var month = new DateOnly(today.Year, today.Month, 1);
        var year = new DateOnly(today.Year, 1, 1);
        var periods = new Dictionary<string, (DateOnly Start, DateOnly End)>
        {
            ["today"] = (today, today), ["yesterday"] = (today.AddDays(-1), today.AddDays(-1)),
            ["this_week"] = (monday, today), ["last_week"] = (monday.AddDays(-7), monday.AddDays(-1)),
            ["this_month"] = (month, today), ["last_month"] = (month.AddMonths(-1), month.AddDays(-1)),
            ["this_year"] = (year, today), ["last_year"] = (year.AddYears(-1), year.AddDays(-1))
        };
        foreach (var (period, expected) in periods)
        {
            var executor = new ScriptedExecutor((tool, args) =>
            {
                Require(args.GetProperty("start_date").GetString() == expected.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) &&
                        args.GetProperty("end_date").GetString() == expected.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    $"Calendar period {period} uses real calendar boundaries, not a rolling approximation.");
                return Success(tool, tool == "sale.search_records"
                    ? (object)new { sales = Array.Empty<object>(), total_count = 0 }
                    : new { groups = Array.Empty<object>(), total_count = 0 });
            });
            var context = Context(executor, new() { ["calendar_period"] = period });
            await new SeeSalesWorkflow().ExecuteAsync(context);
            await new SalesRevenueSummaryWorkflow().ExecuteAsync(context);
        }
        var rolling = new ScriptedExecutor((tool, args) =>
        {
            Require(args.GetProperty("start_date").GetString() == today.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "Rolling 30-day behavior remains distinct from the previous calendar month.");
            return Success(tool, new { sales = Array.Empty<object>(), total_count = 0 });
        });
        await new SeeSalesWorkflow().ExecuteAsync(Context(rolling, new() { ["period_days"] = "30" }));
        foreach (var invalid in new Dictionary<string, string>[]
        {
            new() { ["start_date"] = "2026-08-31", ["end_date"] = "2026-08-01" },
            new() { ["start_date"] = "31.08.2026" },
            new() { ["period_days"] = "0" },
            new() { ["period_months"] = "-1" },
            new() { ["calendar_period"] = "unclear" },
            new() { ["calendar_period"] = "last_month", ["end_date"] = "2026-08-31" }
        })
        {
            var executor = new ScriptedExecutor((_, _) => throw new InvalidOperationException("Invalid dates must not reach data tools."));
            try { await new SeeSalesWorkflow().ExecuteAsync(Context(executor, invalid)); }
            catch (AiWorkflowInputException) { continue; }
            throw new InvalidOperationException("Invalid or reversed dates must not be silently replaced with a default query.");
        }
    }

    private static async Task VerifySalesWorkflowAsync()
    {
        var executor = new ScriptedExecutor((tool, args) =>
        {
            Require(tool == "sale.search_records", "Sales workflow must use its canonical query tool.");
            Require(args.GetProperty("start_time").GetString() == "08:00" && args.GetProperty("end_time").GetString() == "12:00",
                "The workflow must apply the requested time interval in the tool, not after limiting rows.");
            Require(args.GetProperty("patient_record_id").GetInt32() == 42,
                "String-encoded resolved patient IDs must be parsed without throwing.");
            return Success(tool, new { sales = new[] { Sale(1), Sale(2) }, total_count = 47, has_more = true });
        });
        var patient = JsonSerializer.SerializeToElement(new { patient_record_id = "42" });
        var result = await new SeeSalesWorkflow().ExecuteAsync(Context(executor,
            new() { ["start_time"] = "08:00", ["end_time"] = "12:00", ["limit"] = "2" },
            new() { ["hasta_eslestir"] = patient }));
        Require(result.Output.GetProperty("rows").GetArrayLength() == 2 &&
                result.Output.GetProperty("metadata").GetProperty("total_count").GetInt32() == 47,
            "Time-filtered pagination must retain the database total rather than inventing a total equal to the page size.");
        Require(result.Message.Contains("08:00–12:00", StringComparison.Ordinal), "User sees the effective time filter.");

        var empty = new ScriptedExecutor((tool, args) =>
        {
            Require(args.GetProperty("barcodes").GetArrayLength() == 1,
                "An empty exact-product search must never silently broaden to all products.");
            return Success(tool, new { sales = Array.Empty<object>(), total_count = 0, has_more = false });
        });
        result = await new SeeSalesWorkflow().ExecuteAsync(Context(empty, new() { ["barcodes"] = "8699500000001" }));
        Require(empty.Calls.Count == 1 && result.Output.GetProperty("rows").GetArrayLength() == 0,
            "An empty barcode result finishes in one query instead of scanning thousands of unrelated sales.");

        var partial = new ScriptedExecutor((tool, args) => args.GetProperty("offset").GetInt32() == 0
            ? Success(tool, new { sales = Enumerable.Range(1, 20).Select(Sale), total_count = 60, has_more = true })
            : new AiWorkflowToolExecution(tool, AiToolExecutionStatus.ExecutionFailed, JsonSerializer.SerializeToElement(new { }), "Test unavailable", 0));
        result = await new SeeSalesWorkflow().ExecuteAsync(Context(partial, new() { ["limit"] = "25" }));
        Require(result.Output.GetProperty("metadata").GetProperty("is_partial").GetBoolean() &&
                result.Message.Contains("liste eksik olabilir", StringComparison.Ordinal),
            "Failed later pages are disclosed, not presented as a complete result.");

        var shortPages = new ScriptedExecutor((tool, args) =>
        {
            var offset = args.GetProperty("offset").GetInt32();
            return Success(tool, new { sales = Enumerable.Range(offset + 1, Math.Min(3, 8 - offset)).Select(Sale), total_count = 8 });
        });
        result = await new SeeSalesWorkflow().ExecuteAsync(Context(shortPages, new() { ["limit"] = "8" }));
        Require(result.Output.GetProperty("rows").GetArrayLength() == 8 &&
                shortPages.Calls.Select(call => call.Arguments.GetProperty("offset").GetInt32()).SequenceEqual([0, 3, 6]),
            "Paging advances by rows actually received, so short pages cannot skip records.");
    }

    private static async Task VerifyPatientSalesContentsContractAsync()
    {
        var workflow = new PatientSalesContentsWorkflow();
        Require(workflow.Definition.Parameters?.ContainsKey("patient_record_id") == true,
            "The patient sale-content contract exposes the positive patient ID already supported by its implementation.");
        var executor = new ScriptedExecutor((tool, args) =>
        {
            Require(tool == "sale.search_records" && args.GetProperty("patient_record_id").GetInt32() == 701 &&
                    args.GetProperty("include_lines").GetBoolean(),
                "Patient sale contents keep the exact requested patient restriction and request product lines.");
            return Success(tool, new { sales = Array.Empty<object>(), total_count = 0, has_more = false });
        });
        var parameters = new Dictionary<string, string> { ["patient_record_id"] = "701", ["period_days"] = "30" };
        Require((await workflow.ExecuteAsync(Context(executor, parameters))).Status == AiWorkflowStepStatus.Completed && executor.Calls.Count == 1,
            "An already-resolved positive ID can fetch the patient's sale contents without repeating patient matching.");
        var outputs = new Dictionary<string, JsonElement>
        {
            ["hasta_eslestir"] = JsonSerializer.SerializeToElement(new { selected_patient = new { patient_record_id = 701, name = "TEST PATIENT" } })
        };
        foreach (var invalid in new[] { "0", "-1", "not-a-patient-id", "2147483648", " " })
        {
            parameters["patient_record_id"] = invalid;
            Require((await workflow.ExecuteAsync(Context(executor, parameters, outputs))).Status == AiWorkflowStepStatus.Failed && executor.Calls.Count == 1,
                "An invalid explicit patient ID never falls back to a remembered patient or broadens the data query.");
        }
        parameters.Remove("patient_record_id");
        Require((await workflow.ExecuteAsync(Context(executor, parameters, outputs))).Status == AiWorkflowStepStatus.Completed && executor.Calls.Count == 2,
            "Omitting the optional ID still uses the previously resolved patient context.");
        Require((await workflow.ExecuteAsync(Context(executor, parameters))).Status == AiWorkflowStepStatus.Failed && executor.Calls.Count == 2,
            "Missing both an explicit and a resolved patient identity cannot run an unrestricted sale-content query.");
    }

    private static async Task VerifyHighDiscountWorkflowAsync()
    {
        static object DiscountSale(int id, decimal gross, decimal discount, string date, string kind = "sale") => new
        {
            record_id = id, reference_code = $"P{id}", record_type = kind, timestamp = date,
            sale_type = "retail", patient_record_id = 7, patient_name = "TEST PATIENT", patient_tc = "10000000146",
            gross_amount = gross, discount_amount = discount, net_amount = gross - discount
        };
        var sales = Enumerable.Range(1, 15)
            .Select(id => DiscountSale(id, 100m, 16 - id, $"2026-10-31T18:{id:00}:00"))
            .Concat(new[]
            {
                DiscountSale(100, 200m, 40m, "2026-10-01T10:00:00"),
                DiscountSale(101, 10m, 9m, "2026-10-01T10:01:00"),
                DiscountSale(102, 0m, 9m, "2026-10-31T20:00:00"),
                DiscountSale(103, -100m, -50m, "2026-10-31T20:01:00"),
                DiscountSale(104, 100m, 99m, "2026-10-31T20:02:00", "refund")
            }).ToArray();
        var executor = new ScriptedExecutor((tool, args) =>
        {
            Require(tool == "sale.search_records" && !args.GetProperty("include_refunds").GetBoolean(),
                "Discount inspection uses individual sales and excludes refunds in the underlying search.");
            return Success(tool, new { sales, has_more = false });
        });
        var workflow = new HighDiscountSaleWorkflow();
        var parameters = new Dictionary<string, string>
        {
            ["minimum_discount_percent"] = "0", ["limit"] = "7", ["sort_by"] = "date", ["sort_direction"] = "descending"
        };
        var result = await workflow.ExecuteAsync(Context(executor, parameters));
        var rows = result.Output.GetProperty("rows").EnumerateArray().ToArray();
        Require(rows.Select(row => row.GetProperty("reference_code").GetString()).SequenceEqual(Enumerable.Range(9, 7).Reverse().Select(id => $"P{id}")),
            "The seven latest discounted sales are limited after valid-discount filtering and sorted by date, not by percentage.");
        Require(result.Output.GetProperty("metadata").GetProperty("total_count").GetInt32() == 17 &&
                result.Output.GetProperty("metadata").GetProperty("shown_count").GetInt32() == 7 &&
                result.Output.GetProperty("metadata").GetProperty("has_more").GetBoolean() &&
                rows.All(row => row.GetProperty("patient_record_id").GetString() == "7"),
            "Complete match count, shown count, and patient identities remain available without including refunds or invalid gross values.");
        parameters["sort_direction"] = "ascending";
        parameters["limit"] = "2";
        result = await workflow.ExecuteAsync(Context(executor, parameters));
        Require(result.Output.GetProperty("rows")[0].GetProperty("reference_code").GetString() == "P100",
            "Ascending date sorting selects the oldest eligible sale.");
        parameters["sort_direction"] = "descending";
        parameters["sort_by"] = "discount_percent";
        result = await workflow.ExecuteAsync(Context(executor, parameters));
        Require(result.Output.GetProperty("rows")[0].GetProperty("reference_code").GetString() == "P101",
            "Percentage ordering differs correctly from absolute discount ordering.");
        parameters["sort_by"] = "discount_total";
        result = await workflow.ExecuteAsync(Context(executor, parameters));
        Require(result.Output.GetProperty("rows")[0].GetProperty("reference_code").GetString() == "P100",
            "Absolute discount sorting ranks stored discount amounts.");
        parameters["minimum_discount_percent"] = "15";
        result = await workflow.ExecuteAsync(Context(executor, parameters));
        Require(result.Output.GetProperty("metadata").GetProperty("total_count").GetInt32() == 2,
            "The declared exclusive threshold excludes a sale exactly on the percentage boundary.");
        var calls = executor.Calls.Count;
        parameters["sort_by"] = "unrecognized";
        Require((await workflow.ExecuteAsync(Context(executor, parameters))).Status == AiWorkflowStepStatus.Failed && executor.Calls.Count == calls,
            "Unsupported ordering fails before querying instead of silently changing the requested meaning.");
        parameters["sort_by"] = "date";
        parameters["limit"] = "0";
        Require((await workflow.ExecuteAsync(Context(executor, parameters))).Status == AiWorkflowStepStatus.Failed && executor.Calls.Count == calls,
            "Invalid output counts are rejected rather than silently clamped.");
        parameters["limit"] = "2";
        parameters["include_refunds"] = "true";
        Require((await workflow.ExecuteAsync(Context(executor, parameters))).Status == AiWorkflowStepStatus.Failed && executor.Calls.Count == calls,
            "An unsupported refund scope is not silently ignored by the sale-only inspection.");
    }

    private static async Task VerifyLatestPrescriptionAsync()
    {
        var executor = new ScriptedExecutor((tool, args) => tool switch
        {
            "sale.search_records" => Success(tool, new
            {
                sales = new[] { new { prescription_no = "RX-MATCH", timestamp = "2026-08-15T10:00:00", patient_tc = "12289124650" } },
                total_count = 1
            }),
            "medula.prescription_get" => Success(tool, new
            {
                success = true, message = "Reçete açıldı.", prescription_number = args.GetProperty("prescription_number").GetString(),
                patient_name = "EVREN ACAR", patient_tc = "12289124650", is_live_on_medula = false,
                fields = new { prescription_date = "15.08.2026" }, drugs = Array.Empty<object>(),
                diagnoses = Array.Empty<object>(), payments = Array.Empty<object>()
            }),
            _ => throw new InvalidOperationException("A filtered prescription lookup must not switch to the generic latest prescription.")
        });
        var parameters = new Dictionary<string, string>
        {
            ["barcodes"] = "8699500000001", ["patient_tc"] = "12289124650", ["patient_record_id"] = "42",
            ["start_date"] = "2026-08-01", ["end_date"] = "2026-08-31", ["start_time"] = "08:00", ["end_time"] = "12:00"
        };
        var result = await new MedulaLatestPrescriptionWorkflow().ExecuteAsync(Context(executor, parameters));
        var search = executor.Calls[0].Arguments;
        Require(result.Prescription?.PrescriptionNumber == "RX-MATCH" && executor.Calls.Count == 2,
            "The latest matching prescription opens using the already-resolved patient/medicine context.");
        Require(search.GetProperty("patient_tc").GetString() == "12289124650" && search.GetProperty("patient_record_id").GetInt32() == 42 &&
                search.GetProperty("start_date").GetString() == "2026-08-01" && search.GetProperty("end_date").GetString() == "2026-08-31" &&
                search.GetProperty("start_time").GetString() == "08:00" && search.GetProperty("end_time").GetString() == "12:00",
            "Medicine-specific latest-prescription lookup retains patient, date, and time constraints.");
        Require(search.GetProperty("has_prescription").GetBoolean() && search.GetProperty("limit").GetInt32() == 1 &&
                !search.GetProperty("statuses").EnumerateArray().Any(value => value.GetString() is "pending" or "cancelled"),
            "Latest-prescription selection excludes cancelled/pending sales and missing prescription numbers before limiting to one.");

        var empty = new ScriptedExecutor((tool, _) => Success(tool, new { sales = Array.Empty<object>(), total_count = 0 }));
        result = await new MedulaLatestPrescriptionWorkflow().ExecuteAsync(Context(empty, parameters));
        Require(result.Status == AiWorkflowStepStatus.Completed && result.Prescription is null && empty.Calls.Count == 1,
            "No medicine-specific match does not open an unrelated prescription.");
        parameters["patient_tc"] = "invalid";
        result = await new MedulaLatestPrescriptionWorkflow().ExecuteAsync(Context(empty, parameters));
        Require(result.Status == AiWorkflowStepStatus.Failed && empty.Calls.Count == 1,
            "An invalid patient identity must not silently drop the patient filter.");
    }

    private static object Sale(int id) => new
    {
        reference_code = $"P{id}", record_id = id, record_type = "sale", sale_type = "retail",
        timestamp = "2026-08-15T11:00:30", quantity = 1, gross_amount = 20m, discount_amount = 2m, net_amount = 18m
    };

    private static AiWorkflowContext Context(IAiWorkflowToolExecutor executor, Dictionary<string, string> parameters,
        Dictionary<string, JsonElement>? outputs = null) => new("Test request", "Test request",
        new AiApplicationContext(CurrentModule: "home"), outputs ?? [], [], ToolExecutor: executor, Parameters: parameters);

    private static AiWorkflowToolExecution Success(string tool, object output) =>
        new(tool, AiToolExecutionStatus.Success, JsonSerializer.SerializeToElement(output), null, 0);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ScriptedExecutor(Func<string, JsonElement, AiWorkflowToolExecution> script) : IAiWorkflowToolExecutor
    {
        public List<(string Tool, JsonElement Arguments)> Calls { get; } = [];
        public bool HasTool(string toolName) => true;
        public Task<AiWorkflowToolExecution> ExecuteAsync(AiRequestContext request, string toolName, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((toolName, arguments.Clone()));
            return Task.FromResult(script(toolName, arguments));
        }
    }

    private sealed class CapturingQueries : IRetailSaleRecordQueryService
    {
        public RetailSaleRecordSearchQuery? Search { get; private set; }
        public RetailSaleAggregateQuery? Aggregate { get; private set; }
        public Task<RetailSaleRecordPage> SearchAsync(RetailSaleRecordSearchQuery query, CancellationToken cancellationToken)
        {
            Search = query;
            return Task.FromResult(new RetailSaleRecordPage([], 0, query.Offset, query.Limit));
        }
        public Task<RetailSaleAggregatePage> AggregateAsync(RetailSaleAggregateQuery query, CancellationToken cancellationToken)
        {
            Aggregate = query;
            return Task.FromResult(new RetailSaleAggregatePage([], 0, query.Offset, query.Limit));
        }
        public Task<RetailSaleRecord?> GetAsync(string referenceCode, CancellationToken cancellationToken) =>
            Task.FromResult<RetailSaleRecord?>(null);
    }
}
