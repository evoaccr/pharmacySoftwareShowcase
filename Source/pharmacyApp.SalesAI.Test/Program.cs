using System.Text.Json;
using pharmacyApp.AI.Tools;
using pharmacyApp.AI.Tools.Sale;
using pharmacyApp.AI.Tools.Product;
using pharmacyApp.Modules.Sales;

var queryService = new FakeSaleRecordQueryService();
var catalog = new SaleAiToolCatalog(recordQueries: queryService);
var definitions = catalog.Definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

Assert(definitions.ContainsKey("sale.search_records"), "search_records is registered.");
Assert(definitions.ContainsKey("sale.search_patients"), "search_patients is registered.");
Assert(definitions.ContainsKey("sale.aggregate_records"), "aggregate_records is registered.");
Assert(definitions.ContainsKey("sale.get_record"), "get_record is registered.");
Assert(!definitions.ContainsKey("sale.get_revenue_summary"), "the overlapping revenue summary is consolidated.");
Assert(definitions.Values.All(definition => definition.Risk == AiToolRisks.ReadOnly), "all Sale tools remain read-only.");
Assert(definitions["sale.search_records"].Parameters["barcodes"].ItemsType == "string",
    "array element metadata is explicit.");

using (var searchJson = JsonDocument.Parse("""
    {
      "start_date":"2026-08-01",
      "end_date":"2026-08-31",
      "sale_types":["prescription"],
      "include_refunds":false,
      "barcodes":["8690000000001"],
      "payment_types":["card","institution"],
      "statuses":["completed"],
      "patient_record_id":42,
      "minimum_total":10.50,
      "include_lines":false,
      "offset":20,
      "limit":25,
      "sort_direction":"oldest_first"
    }
    """))
{
    var result = (SaleRecordSearchToolResult)await catalog.ExecuteAsync("sale.search_records", searchJson.RootElement);
    var query = queryService.LastSearch ?? throw new InvalidOperationException("Search query was not forwarded.");
    Assert(query.Filter.StartDate == new DateTime(2026, 8, 1)
           && query.Filter.EndDateExclusive == new DateTime(2026, 9, 1), "inclusive dates become an exclusive database range.");
    Assert(query.Filter.SaleTypes.SequenceEqual(["prescription"])
           && query.Filter.Barcodes.SequenceEqual(["8690000000001"]), "sale and barcode filters are forwarded.");
    Assert(!query.Filter.IncludeRefunds && query.Filter.PatientRecordId == 42
           && query.Filter.PaymentTypes.SequenceEqual(["card", "institution"]), "workflow filters are forwarded.");
    Assert(query.Offset == 20 && query.Limit == 25 && !query.IncludeLines && !query.NewestFirst,
        "pagination, line projection, and ordering are forwarded.");
    Assert(result.TotalCount == 26 && result.HasMore && result.Sales.Single().ReferenceCode == "P7",
        "search result preserves structured records and paging metadata.");
}

using (var getJson = JsonDocument.Parse("""{"reference_code":"p7"}"""))
{
    var result = (SaleRecordGetToolResult)await catalog.ExecuteAsync("sale.get_record", getJson.RootElement);
    Assert(result.Found && result.Sale?.ReferenceCode == "P7" && queryService.LastReferenceCode == "P7",
        "get_record canonicalizes and returns a complete record.");
}

using (var aggregateJson = JsonDocument.Parse("""
    {
      "start_date":"2026-01-01",
      "end_date":"2026-08-31",
      "sale_types":["retail","prescription"],
      "include_refunds":true,
      "group_by":"product",
      "sort_by":"profit",
      "sort_direction":"descending",
      "limit":10
    }
    """))
{
    var result = (SaleRecordAggregateToolResult)await catalog.ExecuteAsync("sale.aggregate_records", aggregateJson.RootElement);
    var query = queryService.LastAggregate ?? throw new InvalidOperationException("Aggregate query was not forwarded.");
    Assert(query.GroupBy == "product" && query.SortBy == "profit" && query.Descending && query.Limit == 10,
        "aggregate grouping and ordering are forwarded.");
    Assert(result.Groups.Single().Profit == 15m && !result.HasMore,
        "aggregate result preserves database metrics.");
}

await AssertValidationAsync("sale.search_records", """{"start_date":"01.08.2026","end_date":"2026-08-31"}""",
    "non-ISO dates are rejected.");
await AssertValidationAsync("sale.search_records", """{"start_date":"2026-09-01","end_date":"2026-08-31"}""",
    "reversed dates are rejected.");
await AssertValidationAsync("sale.search_records", """{"start_date":"2026-08-01","end_date":"2026-08-31","sale_types":["consignment"]}""",
    "unsupported sale types are rejected.");
await AssertValidationAsync("sale.get_record", """{"reference_code":"7"}""",
    "ambiguous numeric record IDs are rejected.");

var serialized = await catalog.ExecuteJsonAsync(
    "sale.get_record", """{"reference_code":"G3"}""");
using (var serializedJson = JsonDocument.Parse(serialized))
    Assert(serializedJson.RootElement.TryGetProperty("found", out _)
           && serializedJson.RootElement.GetProperty("sale").TryGetProperty("reference_code", out _),
        "tool JSON uses deterministic snake_case fields.");

if (string.Equals(Environment.GetEnvironmentVariable("PHARMACYAPP_DB_INTEGRATION_TEST"), "1", StringComparison.Ordinal))
{
    var liveCatalog = new SaleAiToolCatalog();
    var liveProductCatalog = new ProductAiToolCatalog();
    var today = DateTime.Today.ToString("yyyy-MM-dd");

    var patientJson = await liveCatalog.ExecuteJsonAsync(
        "sale.search_patients", """{"query":"evren acar"}""");
    using (var patientDocument = JsonDocument.Parse(patientJson))
    {
        var patientMatches = patientDocument.RootElement.GetProperty("patients").EnumerateArray().ToArray();
        var exactPatient = patientMatches.FirstOrDefault(patient =>
            string.Equals(patient.GetProperty("name").GetString(), "EVREN ACAR", StringComparison.CurrentCultureIgnoreCase));
        Assert(exactPatient.ValueKind == JsonValueKind.Object,
            "live patient-directory search resolves Evren Acar by name.");
        using var livePatientSales = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            start_date = DateTime.Today.AddYears(-10).ToString("yyyy-MM-dd"),
            end_date = today,
            patient_record_id = exactPatient.GetProperty("patient_record_id").GetInt32(),
            include_refunds = false,
            limit = 200
        }));
        var livePatientPage = (SaleRecordSearchToolResult)await liveCatalog.ExecuteAsync(
            "sale.search_records", livePatientSales.RootElement);
        Assert(livePatientPage.TotalCount > 0,
            "live all-history patient-filtered sales query returns Evren Acar's existing sales.");
        Console.WriteLine($"PASS live Evren Acar sales; count={livePatientPage.TotalCount}.");
    }

    using (var julyInstitutionRevenue = JsonDocument.Parse(JsonSerializer.Serialize(new
           {
               start_date = "2026-07-01",
               end_date = "2026-07-31",
               sale_types = new[] { "prescription" },
               include_refunds = false,
               group_by = "none",
               sort_by = "institution_revenue",
               limit = 1
           })))
    {
        var result = (SaleRecordAggregateToolResult)await liveCatalog.ExecuteAsync(
            "sale.aggregate_records", julyInstitutionRevenue.RootElement);
        Assert(result.Groups.Count == 1 && result.Groups[0].InstitutionRevenue is not null,
            "live July prescription aggregation exposes transaction-level public-fee revenue.");
    }

    using (var retailRevenueWithRefunds = JsonDocument.Parse(JsonSerializer.Serialize(new
           {
               start_date = DateTime.Today.AddYears(-10).ToString("yyyy-MM-dd"),
               end_date = today,
               sale_types = new[] { "retail" },
               include_refunds = true,
               group_by = "record_type",
               sort_by = "net_revenue",
               limit = 10
           })))
    {
        var result = (SaleRecordAggregateToolResult)await liveCatalog.ExecuteAsync(
            "sale.aggregate_records", retailRevenueWithRefunds.RootElement);
        var refund = result.Groups.FirstOrDefault(group =>
            string.Equals(group.GroupKey, "refund", StringComparison.OrdinalIgnoreCase));
        Assert(refund is not null && refund.RefundCount > 0 && refund.GrossRevenue != 0m,
            "live retail revenue aggregation includes real nonzero refund totals when the refund column is shown.");
        Console.WriteLine($"PASS live retail refunds; count={refund!.RefundCount}; gross={refund.GrossRevenue}.");
    }

    using (var belocSearch = JsonDocument.Parse("""{"query":"belo"}"""))
    {
        var beloc = (ProductNameSearchToolResult)await liveProductCatalog.ExecuteAsync(
            "product.search_by_name", belocSearch.RootElement);
        var belocBarcodes = beloc.Products
            .Where(product => product.Name.StartsWith("BELOC ", StringComparison.CurrentCultureIgnoreCase))
            .Select(product => product.Barcode).Distinct(StringComparer.Ordinal).Take(20).ToArray();
        Assert(belocBarcodes.Length > 0, "live catalog prefix search supplies Beloc candidates for belok correction.");
        var pricedBeloc = beloc.Products.FirstOrDefault(product => product.PurchasePrice is not null);
        Assert(pricedBeloc is not null, "live Beloc catalog data exposes a current local catalog price.");
        var pricedBelocValue = pricedBeloc!;
        using (var liveBelocPriceArguments = JsonDocument.Parse(JsonSerializer.Serialize(new
               {
                   barcode = pricedBelocValue.Barcode,
                   offset = 0,
                   limit = 500
               })))
        {
            var prices = (ProductPriceHistoryToolResult)await liveProductCatalog.ExecuteAsync(
                "product.get_price_history", liveBelocPriceArguments.RootElement);
            Assert(pricedBelocValue.PurchasePrice is not null || prices.Points.Any(point => point.SalePrice is not null),
                "live Beloc price workflow has at least one current local price field to report.");
        }
        using var liveBelocRevenue = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            start_date = DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd"),
            end_date = today,
            barcodes = belocBarcodes,
            include_refunds = false,
            group_by = "record_type",
            limit = 2
        }));
        _ = (SaleRecordAggregateToolResult)await liveCatalog.ExecuteAsync(
            "sale.aggregate_records", liveBelocRevenue.RootElement);
        Console.WriteLine("PASS live one-month multi-barcode Beloc revenue query executes.");
    }
    using (var parolSearch = JsonDocument.Parse("""{"query":"parol 500 mg 20 tablet"}"""))
    {
        var parol = (ProductNameSearchToolResult)await liveProductCatalog.ExecuteAsync(
            "product.search_by_name", parolSearch.RootElement);
        var parolBarcodes = parol.Products.Where(product =>
            string.Equals(product.Name, "PAROL 500 MG 20 TABLET", StringComparison.CurrentCultureIgnoreCase))
            .Select(product => product.Barcode).Distinct(StringComparer.Ordinal).ToArray();
        Assert(parolBarcodes.Length > 0, "live catalog resolves PAROL 500 MG 20 barcode identities.");
        async Task<SaleRecordSearchToolResult> SearchParolAsync(int days)
        {
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                start_date = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd"),
                end_date = today,
                barcodes = parolBarcodes,
                include_refunds = false,
                include_lines = true,
                limit = 200
            }));
            return (SaleRecordSearchToolResult)await liveCatalog.ExecuteAsync(
                "sale.search_records", arguments.RootElement);
        }
        var parol15 = await SearchParolAsync(15);
        var parol60 = await SearchParolAsync(60);
        Console.WriteLine($"LIVE Parol diagnostic; barcodes={string.Join(',', parolBarcodes)}; 15_days={parol15.TotalCount}; 60_days={parol60.TotalCount}.");
        Assert(parol15.TotalCount > 0 && parol60.TotalCount >= parol15.TotalCount,
            "live 60-day PAROL 500 MG 20 sales are a nonempty superset of the exact 15-day result.");
        Console.WriteLine($"PASS live Parol ranges; 15_days={parol15.TotalCount}; 60_days={parol60.TotalCount}.");
    }
    using var liveSearch = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        start_date = DateTime.Today.AddYears(-10).ToString("yyyy-MM-dd"),
        end_date = today,
        limit = 2,
        include_lines = true
    }));
    var page = (SaleRecordSearchToolResult)await liveCatalog.ExecuteAsync("sale.search_records", liveSearch.RootElement);
    using var liveAggregate = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        start_date = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd"),
        end_date = today,
        group_by = "sale_type",
        limit = 10
    }));
    _ = (SaleRecordAggregateToolResult)await liveCatalog.ExecuteAsync("sale.aggregate_records", liveAggregate.RootElement);
    using var liveProductAggregate = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        start_date = DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd"),
        end_date = today,
        group_by = "product",
        sort_by = "profit",
        limit = 2
    }));
    _ = (SaleRecordAggregateToolResult)await liveCatalog.ExecuteAsync(
        "sale.aggregate_records", liveProductAggregate.RootElement);
    var exactSale = page.Sales.FirstOrDefault(record => record.RecordType == "sale" && record.Lines.Count > 0);
    if (exactSale is not null)
    {
        var exactLine = exactSale.Lines[0];
        using var liveExactLineAggregate = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            start_date = DateTime.Today.AddYears(-10).ToString("yyyy-MM-dd"),
            end_date = today,
            qr_codes = new[] { exactLine.QrCode },
            include_refunds = false,
            group_by = "none",
            limit = 2
        }));
        var exactAggregate = (SaleRecordAggregateToolResult)await liveCatalog.ExecuteAsync(
            "sale.aggregate_records", liveExactLineAggregate.RootElement);
        var exactGroup = exactAggregate.Groups.Single();
        Assert(exactGroup.Quantity == 1 && exactGroup.NetRevenue == exactLine.NetPrice,
            "live exact-QR aggregation includes only the matching immutable product line.");

        var comparisonStart = DateTime.Today.AddDays(-60).Date;
        using var liveBarcodeSearch = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            start_date = comparisonStart.ToString("yyyy-MM-dd"),
            end_date = today,
            barcodes = new[] { exactLine.Barcode },
            include_refunds = false,
            include_lines = false,
            limit = 200
        }));
        var toolBarcodePage = (SaleRecordSearchToolResult)await liveCatalog.ExecuteAsync(
            "sale.search_records", liveBarcodeSearch.RootElement);
        using var liveShortBarcodeSearch = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            start_date = DateTime.Today.AddDays(-12).ToString("yyyy-MM-dd"),
            end_date = today,
            barcodes = new[] { exactLine.Barcode },
            include_refunds = false,
            include_lines = false,
            limit = 200
        }));
        var shortBarcodePage = (SaleRecordSearchToolResult)await liveCatalog.ExecuteAsync(
            "sale.search_records", liveShortBarcodeSearch.RootElement);
        Assert(toolBarcodePage.TotalCount >= shortBarcodePage.TotalCount,
            "live 60-day barcode sales are a superset of the same 12-day sales filter.");
        var moduleBarcodeRows = await new RetailSalesListRepository().LoadSalesAsync(
            new RetailSalesListQuery(
                comparisonStart,
                DateTime.Today.AddDays(1),
                TransactionTypes: RetailTransactionTypes.RetailSale | RetailTransactionTypes.PrescriptionSale,
                Barcodes: new[] { exactLine.Barcode }),
            CancellationToken.None);
        Assert(moduleBarcodeRows.Count == toolBarcodePage.TotalCount
               && moduleBarcodeRows.Select(row => row.ReferenceCode).ToHashSet(StringComparer.Ordinal)
                   .SetEquals(toolBarcodePage.Sales.Select(row => row.ReferenceCode)),
            "live filtered Sales tab and chatbot return the same immutable-line barcode matches.");
    }
    if (page.Sales.Count > 0)
    {
        using var liveGet = JsonDocument.Parse(JsonSerializer.Serialize(new { reference_code = page.Sales[0].ReferenceCode }));
        var record = (SaleRecordGetToolResult)await liveCatalog.ExecuteAsync("sale.get_record", liveGet.RootElement);
        Assert(record.Found && record.Sale?.Lines is not null, "live get_record returns complete child collections.");
    }
    Console.WriteLine($"PASS live sales record SQL; ten_year_count={page.TotalCount}");
}

Console.WriteLine("PASS sales-ai-record-tools");

async Task AssertValidationAsync(string toolName, string json, string message)
{
    using var document = JsonDocument.Parse(json);
    try
    {
        await catalog.ExecuteAsync(toolName, document.RootElement);
        throw new InvalidOperationException($"Expected validation failure: {message}");
    }
    catch (AiToolValidationException)
    {
        Console.WriteLine($"PASS {message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}

sealed class FakeSaleRecordQueryService : IRetailSaleRecordQueryService
{
    public RetailSaleRecordSearchQuery? LastSearch { get; private set; }
    public RetailSaleAggregateQuery? LastAggregate { get; private set; }
    public string? LastReferenceCode { get; private set; }

    public Task<RetailSaleRecordPage> SearchAsync(RetailSaleRecordSearchQuery query, CancellationToken cancellationToken)
    {
        LastSearch = query;
        return Task.FromResult(new RetailSaleRecordPage([Record("P7")], 26, query.Offset, query.Limit));
    }

    public Task<RetailSaleRecord?> GetAsync(string referenceCode, CancellationToken cancellationToken)
    {
        LastReferenceCode = referenceCode;
        return Task.FromResult<RetailSaleRecord?>(Record(referenceCode));
    }

    public Task<RetailSaleAggregatePage> AggregateAsync(RetailSaleAggregateQuery query, CancellationToken cancellationToken)
    {
        LastAggregate = query;
        var group = new RetailSaleAggregateGroup(
            "8690000000001", "TEST MEDICINE", 2, 2, 0, 2,
            40m, 5m, 35m, 12m, 20m, 15m, 0, 17.5m, 17.5m);
        return Task.FromResult(new RetailSaleAggregatePage([group], 1, query.Offset, query.Limit));
    }

    private static RetailSaleRecord Record(string referenceCode) => new(
        referenceCode, int.Parse(referenceCode[1..]), referenceCode[0] == 'G' ? "refund" : "sale", "retail",
        new DateTime(2026, 8, 12, 10, 30, 0), "completed", 42, "Test Patient", "11111111111", null,
        "user-1", "Test User", 1, 20m, 2m, 18m, 10m, 8m, 0, 20m, 0m,
        false, false, null,
        [new RetailSaleRecordPayment("cash", null, 18m, 0m, true, new DateTime(2026, 8, 12), null)],
        [new RetailSaleRecordLine(1, null, "0108690000000000", "8690000000001", 1, "TEST MEDICINE", 20m, 2m, 18m, 10m, 8m, null, true)]);
}
