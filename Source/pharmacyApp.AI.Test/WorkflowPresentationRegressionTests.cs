using System.Text.Json;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Workflows;

internal static class WorkflowPresentationRegressionTests
{
    public static async Task RunAsync()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        Check(AiWorkflowValueFormatter.Format("timestamp", "2026-10-24T18:00:00+03:00") == "24.10.2026 18:00",
            "Display formatting preserves the pharmacy wall time.");
        Check(AiWorkflowValueFormatter.Format("start_date", "2026-10-01") == "01.10.2026"
              && AiWorkflowValueFormatter.Format("prescription_date", "03/09/2026") == "03.09.2026",
            "Date-only fields and Turkish Medula dates remain unambiguous.");
        Check(AiWorkflowValueFormatter.Format("barcode", "20261024") == "20261024"
              && AiWorkflowValueFormatter.Format("day", "not a date") == "not a date"
              && AiWorkflowValueFormatter.Format("patient_name", "Çağrı Öztürk") == "Çağrı Öztürk",
            "Formatting does not reinterpret names, identifiers or unknown date values.");
        Check(AiWorkflowValueFormatter.Format("discount_total", "1234.50") == "TRY 1.234,50"
              && AiWorkflowValueFormatter.Format("entry_cost_total", "90") == "TRY 90,00"
              && AiWorkflowValueFormatter.Format("sale_price", "1,20") == "TRY 1,20"
              && AiWorkflowValueFormatter.Format("profit", "Maliyet bilgisi eksik") == "Missing cost information",
            "Cards and tables use English currency captions without changing numeric values or treating missing cost as zero.");
        var output = JsonSerializer.SerializeToElement(new
        {
            rows = new[] { new
            {
                name = "TEST MEDICINE", timestamp = "2026-10-24T18:00:00+03:00", sale_type = "retail",
                record_type = "sale", status = "completed", discount_total = "12.50", start_date = "2026-10-01"
            } }
        });
        var context = new AiWorkflowContext("test", "test", new AiApplicationContext(),
            new Dictionary<string, JsonElement> { ["sample"] = output }, []);
        foreach (var workflow in new IAiWorkflow[]
                 { new InformationCardsWorkflow(), new MetricCardsWorkflow(), new RankedListWorkflow(), new TimelineWorkflow() })
        {
            var result = await workflow.ExecuteAsync(context);
            var item = result.Content?.Items.Single() ?? throw new InvalidOperationException("A content item was not produced.");
            Check(item.Title == "TEST MEDICINE" && item.Subtitle == "24.10.2026 18:00"
                  && item.Detail?.Contains("Retail", StringComparison.Ordinal) == true
                  && item.Detail.Contains("Sale", StringComparison.Ordinal)
                  && item.Detail.Contains("Completed", StringComparison.Ordinal)
                  && item.Detail.Contains("TRY 12,50", StringComparison.Ordinal)
                  && item.Detail.Contains("01.10.2026", StringComparison.Ordinal),
                $"{workflow.Definition.Name} renders the same English values as the table.");
            Check(result.Output.GetProperty("rows")[0].GetProperty("timestamp").GetString() == "2026-10-24T18:00:00+03:00"
                  && result.Output.GetProperty("rows")[0].GetProperty("sale_type").GetString() == "retail",
                "Formatting is presentation-only; retained data keeps original values for filters and calculations.");
        }
        var helper = new PreviousResultWorkflow().Definition;
        Check(helper.Name == "onceki_sonucu_isle" && helper.DisplayName == "Process Previous Result"
              && helper.DisplayFunctionName == "process_previous_result",
            "Workflow display names are English while canonical routing identifiers remain unchanged.");
        Check(pharmacyApp.Localization.UiText.Translate(
                  "Önceki sonuçtan 6 kayıt eşleşti; 6 kayıt gösteriliyor. Önceki yanıtta gösterilen 15 kayıt esas alındı; yeni bir veritabanı sorgusu yapılmadı.")
              == "Matched 6 records from the previous result; displaying 6 records. The 15 records displayed in the previous response were used; no new database query was made.",
            "Nested generic-result scope messages remain fully English and preserve the exact retained-row scope.");
        Check(pharmacyApp.Localization.UiText.Translate(
                  "Önceki sonuçta koşullara uyan 2 farklı hasta var. Hangisini kastettiğinizi belirterek seçimi daraltın.")
              == "The previous result contains 2 different matching patient records. Please narrow the selection.",
            "Ambiguous retained identities use an English clarification without exposing internal IDs.");
        Check(pharmacyApp.Localization.UiText.Translate("Toplam Net Tutar") == "Total Net Amount"
              && pharmacyApp.Localization.UiText.Translate("Son gösterilen 10 kayıttaki toplam net tutar 1.234,50 ₺.")
              == "Total net amount across the last 10 displayed records: 1.234,50 ₺."
              && pharmacyApp.Localization.UiText.Translate("Son gösterilen sonuçta 6 kayıt var.")
              == "The latest displayed result contains 6 records.",
            "Deterministic metric titles and messages translate whole multiword labels without changing counts or amounts.");
        Check(pharmacyApp.Localization.UiText.Translate(
                  "İstenen hesaplama tamamlanamadı: bazı kayıtlarda sayısal bilgi eksik veya geçersiz. Bu değerler sıfır kabul edilmedi.")
              == "The calculation could not be completed: some records have missing or invalid numeric values. These values were not treated as zero.",
            "Safe calculation failures explain missing values in English rather than presenting invented zeroes.");

        var aliasedOutput = JsonSerializer.SerializeToElement(new
        {
            rows = new[] { new { name = "Shown sales", custom_total = "12.50", reviewed_days = "30", quantity = "4", status = "completed" } },
            metadata = new
            {
                highlight_field = "custom_total",
                column_labels = new Dictionary<string, object>
                {
                    ["custom_total"] = "Total discount in shown sales", ["reviewed_days"] = "Days reviewed",
                    ["quantity"] = "  ", ["status"] = 7
                }
            }
        });
        var aliasedContext = context with { Outputs = new Dictionary<string, JsonElement> { ["sample"] = aliasedOutput } };
        foreach (var workflow in new IAiWorkflow[]
                 { new InformationCardsWorkflow(), new MetricCardsWorkflow(), new RankedListWorkflow(), new TimelineWorkflow() })
        {
            var result = await workflow.ExecuteAsync(aliasedContext);
            var item = result.Content?.Items.Single() ?? throw new InvalidOperationException("An aliased content item was not produced.");
            Check(item.Value == "Total discount in shown sales: 12.50"
                  && item.Detail?.Contains("Days reviewed: 30", StringComparison.Ordinal) == true
                  && item.Detail.Contains("Toplam Ürün Adedi: 4", StringComparison.Ordinal)
                  && item.Detail.Contains("Durum: Completed", StringComparison.Ordinal),
                $"{workflow.Definition.Name} preserves requested English metric captions and falls back for blank or invalid labels.");
            Check(result.Output.GetRawText() == aliasedOutput.GetRawText(),
                "Custom display captions never modify retained rows or metadata.");
        }
        Console.WriteLine("PASS workflow-presentation-formatting");
    }
}
