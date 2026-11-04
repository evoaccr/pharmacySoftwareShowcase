using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using pharmacyApp.AI.Execution;
using pharmacyApp.AI.Models;

namespace pharmacyApp.AI.Workflows;

internal static partial class WorkflowData
{
    public static AiRequestContext Request(AiWorkflowContext context) => context.RequestContext ?? new AiRequestContext(
        Guid.NewGuid().ToString("N"), context.OriginalRequest, context.ApplicationContext, new HashSet<string>());

    public static string? Parameter(AiWorkflowContext context, string name) =>
        context.Parameters?.GetValueOrDefault(name)?.Trim() is { Length: > 0 } value ? value : null;

    public static int Integer(AiWorkflowContext context, string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Parameter(context, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    public static int? NullableInteger(AiWorkflowContext context, string name, int minimum, int maximum) =>
        int.TryParse(Parameter(context, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= minimum && value <= maximum
            ? value
            : null;

    public static bool Boolean(AiWorkflowContext context, string name, bool fallback = false) =>
        bool.TryParse(Parameter(context, name), out var value) ? value : fallback;

    public static bool? NullableBoolean(AiWorkflowContext context, string name) =>
        bool.TryParse(Parameter(context, name), out var value) ? value : null;

    public static decimal? NullableDecimal(AiWorkflowContext context, string name) =>
        decimal.TryParse(Parameter(context, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static (string Start, string End) DateRange(AiWorkflowContext context, int defaultDays = 30)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var requestedStart = ReadDate("start_date");
        var requestedEnd = ReadDate("end_date");
        var calendarPeriod = Parameter(context, "calendar_period")?.ToLowerInvariant();
        var end = requestedEnd ?? today;
        DateOnly start;
        if (requestedStart is { } explicitStart)
            start = explicitStart;
        else if (calendarPeriod is not null)
        {
            if (requestedEnd is not null)
                throw new AiWorkflowInputException("Takvim dönemi ile yalnızca bitiş tarihi birlikte kullanılamaz. Lütfen tarih aralığını netleştirin.");
            var weekStart = today.AddDays(-((7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7));
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var yearStart = new DateOnly(today.Year, 1, 1);
            (start, end) = calendarPeriod switch
            {
                "today" => (today, today),
                "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
                "this_week" => (weekStart, today),
                "last_week" => (weekStart.AddDays(-7), weekStart.AddDays(-1)),
                "this_month" => (monthStart, today),
                "last_month" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
                "this_year" => (yearStart, today),
                "last_year" => (yearStart.AddYears(-1), yearStart.AddDays(-1)),
                _ => throw new AiWorkflowInputException("İstenen takvim dönemi tanınamadı. Lütfen gün, hafta, ay veya yıl belirtin.")
            };
        }
        else if (Boolean(context, "nearest"))
            start = new DateOnly(2000, 1, 1);
        else if (Boolean(context, "all_history"))
            start = end.AddYears(-10);
        else if (ReadPeriod("period_months", 120) is { } months)
            start = end.AddMonths(-months);
        else if (ReadPeriod("period_days", 3650) is { } days)
            start = end.AddDays(-days);
        else
            start = end.AddDays(-defaultDays);
        if (start > end)
            throw new AiWorkflowInputException("Başlangıç tarihi bitiş tarihinden sonra olamaz. Lütfen tarih aralığını düzeltin.");
        return (start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        DateOnly? ReadDate(string key)
        {
            var value = Parameter(context, key);
            if (value is null) return null;
            return ParseDate(value) ?? throw new AiWorkflowInputException("Tarih okunamadı. Lütfen geçerli bir tarih aralığı belirtin.");
        }

        int? ReadPeriod(string key, int maximum)
        {
            var value = Parameter(context, key);
            if (value is null) return null;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 1 && count <= maximum)
                return count;
            throw new AiWorkflowInputException("Sorgu dönemi geçerli bir pozitif gün veya ay sayısı olmalıdır (en fazla 10 yıl).");
        }
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static IReadOnlyList<string> List(AiWorkflowContext context, string name) =>
        Split(Parameter(context, name));

    public static IReadOnlyList<string> Barcodes(AiWorkflowContext context)
    {
        var maximum = Regex.IsMatch(Normalize(context.OriginalRequest), @"\bilk\s+(?:iki|2)\b", RegexOptions.CultureInvariant) ? 2 : 20;
        var direct = List(context, "barcodes").Where(value => !IsBarcodePlaceholder(value)).ToArray();
        if (direct.Length > 0) return direct.Take(maximum).ToArray();
        var completedThisTurn = context.Trace.Where(entry => entry.Status == AiWorkflowStepStatus.Completed)
            .Select(entry => entry.WorkflowName).ToHashSet(StringComparer.Ordinal);
        // An explicit entity resolution in this queue supersedes an older remembered medicine.
        var resolved = context.Outputs.Reverse().Where(pair => completedThisTurn.Contains(pair.Key) &&
                pair.Value.ValueKind == JsonValueKind.Object && Text(pair.Value, "resolved_entity") == "medicine")
            .SelectMany(pair => ReadBarcodes(pair.Value)).Distinct(StringComparer.Ordinal).Take(maximum).ToArray();
        if (resolved.Length > 0) return resolved;
        var matched = context.Outputs
            .Where(pair => pair.Key.StartsWith("ilac_barkod_eslestir", StringComparison.Ordinal))
            .SelectMany(pair => ReadBarcodes(pair.Value))
            .Distinct(StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
        if (matched.Length > 0) return matched;
        foreach (var pair in context.Outputs.Reverse())
        {
            var output = pair.Value;
            if (output.ValueKind != JsonValueKind.Object || pair.Key == PreviousResultData.ContextKey) continue;
            var mayUseRows = completedThisTurn.Contains(pair.Key);
            var hasResolvedMedicine = Text(output, "resolved_entity") == "medicine" ||
                                      output.TryGetProperty("selected_products", out var products) && products.ValueKind == JsonValueKind.Array ||
                                      pair.Key is "recent_barcode_context" or "remembered_medicine_context" or "onceki_ilac_bilgisini_getir";
            var productGrouping = output.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                                  Text(metadata, "group_by") is "product" or "barcode";
            var result = (mayUseRows || hasResolvedMedicine) && output.TryGetProperty("barcodes", out var values) && values.ValueKind == JsonValueKind.Array
                ? ReadBarcodes(output).Distinct(StringComparer.Ordinal).Take(maximum).ToArray()
                : mayUseRows && output.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
                    ? rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object)
                        .Select(row => row.TryGetProperty("barcode", out var barcode) && barcode.ValueKind == JsonValueKind.String
                            ? barcode.GetString()!.Trim()
                            : productGrouping && row.TryGetProperty("group_key", out var groupKey) && groupKey.ValueKind == JsonValueKind.String
                                ? groupKey.GetString()!.Trim() : string.Empty).Where(value => value.Length > 0)
                        .Distinct(StringComparer.Ordinal).Take(maximum).ToArray()
                    : [];
            if (result.Length > 0) return result;
        }
        return [];

        static IEnumerable<string> ReadBarcodes(JsonElement output) =>
            output.ValueKind == JsonValueKind.Object && output.TryGetProperty("barcodes", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!.Trim()).Where(value => value.Length > 0)
                : [];
    }

    public static string ProductName(AiWorkflowContext context, string barcode)
    {
        foreach (var output in context.Outputs.Reverse().Select(pair => pair.Value))
        {
            if (!output.TryGetProperty("selected_products", out var products) || products.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var product in products.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
                if (string.Equals(Text(product, "barcode"), barcode, StringComparison.Ordinal))
                    return Text(product, "name");
        }
        return string.Empty;
    }

    private static bool IsBarcodePlaceholder(string value)
    {
        var normalized = Normalize(value);
        return value.Contains('<', StringComparison.Ordinal) || value.Contains('>', StringComparison.Ordinal) ||
               normalized.Contains("resolved", StringComparison.Ordinal) ||
               normalized.Contains("cozumlenmis", StringComparison.Ordinal) ||
               normalized.Contains("eslestirilen", StringComparison.Ordinal) ||
               normalized.Contains("secilen barkod", StringComparison.Ordinal);
    }

    public static JsonElement? ResolvedPatientContext(AiWorkflowContext context)
    {
        // Outputs retain insertion order when an existing key is replaced. Only
        // execution order establishes which resolver the current queue selected.
        var current = context.Trace.LastOrDefault(entry => entry.Status == AiWorkflowStepStatus.Completed &&
            (entry.WorkflowName is "hasta_eslestir" or "onceki_hasta_bilgisini_getir" ||
             entry.WorkflowName == PreviousResultWorkflow.WorkflowName &&
             entry.Output.ValueKind == JsonValueKind.Object && Text(entry.Output, "resolved_entity") == "patient"));
        if (current is not null) return current.Output;

        // Confirmation places its approved identity in hasta_eslestir even when
        // the marker step is skipped. It takes precedence over retained snapshots.
        foreach (var key in new[] { "hasta_eslestir", "remembered_patient_context", PreviousResultWorkflow.WorkflowName })
            if (context.Outputs.TryGetValue(key, out var output) && HasPatient(output))
                return output;
        foreach (var output in context.Outputs.Values.Reverse())
            if (HasPatient(output)) return output;
        return null;

        static bool HasPatient(JsonElement output) => output.ValueKind == JsonValueKind.Object &&
            output.TryGetProperty("selected_patient", out var patient) && patient.ValueKind == JsonValueKind.Object;
    }

    public static int? PatientRecordId(AiWorkflowContext context)
    {
        var direct = NullableInteger(context, "patient_record_id", 1, int.MaxValue);
        if (direct.HasValue) return direct;
        if (ResolvedPatientContext(context) is { } resolved)
        {
            if (resolved.ValueKind != JsonValueKind.Object) return null;
            if (ReadId(resolved) is { } rootId) return rootId;
            return resolved.TryGetProperty("selected_patient", out var patient) && patient.ValueKind == JsonValueKind.Object
                ? ReadId(patient) : null;
        }
        foreach (var output in context.Outputs.Reverse().Select(pair => pair.Value))
        {
            if (output.ValueKind == JsonValueKind.Object && ReadId(output) is { } id) return id;
        }
        return null;

        static int? ReadId(JsonElement output) =>
            int.TryParse(Text(output, "patient_record_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public static string PatientName(AiWorkflowContext context)
    {
        if (ResolvedPatientContext(context) is { ValueKind: JsonValueKind.Object } output &&
            output.TryGetProperty("selected_patient", out var patient) && patient.ValueKind == JsonValueKind.Object)
        {
            var name = Text(patient, "name");
            if (name.Length > 0) return name;
        }
        return "Hasta";
    }

    public static string PreviousResultMessage(AiWorkflowContext context)
    {
        var entry = context.Trace.LastOrDefault(trace => trace.Status == AiWorkflowStepStatus.Completed &&
            trace.WorkflowName is not ("tabloda_goster" or "bilgi_kartlari_goster" or "metrik_kartlari_goster" or
                "sirali_liste_goster" or "zaman_cizelgesinde_goster" or "bolumlu_ozet_goster" or "modulleri_ac"));
        return entry?.Message ?? string.Empty;
    }

    public static IReadOnlyList<string> SaleTypes(AiWorkflowContext context) =>
        List(context, "sale_types").Select(value => Normalize(value) switch
        {
            "receteli" or "recete" or "prescription" => "prescription",
            "perakende" or "retail" => "retail",
            _ => value.Trim().ToLowerInvariant()
        }).Distinct(StringComparer.Ordinal).ToArray();

    public static string GroupBy(AiWorkflowContext context, string fallback)
    {
        var value = Parameter(context, "group_by")?.ToLowerInvariant();
        return value is "none" or "day" or "month" or "sale_type" or "record_type" or "product" or "user" or "patient"
            ? value
            : fallback;
    }

    public static string SortBy(AiWorkflowContext context, string fallback)
    {
        var value = Parameter(context, "sort_by")?.ToLowerInvariant();
        return value is "net_revenue" or "gross_revenue" or "institution_revenue" or "discount_total" or
            "profit" or "quantity" or "record_count" or "group_key"
            ? value
            : fallback;
    }

    public static string SortDirection(AiWorkflowContext context, string fallback)
    {
        var value = Normalize(Parameter(context, "sort_direction") ?? string.Empty);
        return value switch
        {
            "ascending" or "artan" or "eskiden yeniye" => "ascending",
            "descending" or "azalan" or "yeniden eskiye" => "descending",
            _ => fallback
        };
    }

    private static string Normalize(string value) => value.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"))
        .Replace('ç', 'c').Replace('ğ', 'g').Replace('ı', 'i').Replace('ö', 'o').Replace('ş', 's').Replace('ü', 'u');

    public static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (value.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!.Trim()).Where(item => item.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (JsonException) { }
        }
        return value.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static AiWorkflowStepResult Missing(params string[] tools) => new(
        AiWorkflowStepStatus.MissingTool,
        $"Bu iş akışı için eksik araçlar: {string.Join(", ", tools)}. Bu araçları eklemeniz gerekiyor.",
        JsonSerializer.SerializeToElement(new { missing_tools = tools }), MissingTools: tools);

    public static AiWorkflowStepResult Failed(string message, object? details = null) => new(
        AiWorkflowStepStatus.Failed, message,
        JsonSerializer.SerializeToElement(details ?? new { error = message }));

    public static async Task<AiWorkflowToolExecution?> Execute(
        AiWorkflowContext context, string tool, object arguments, CancellationToken cancellationToken)
    {
        if (context.ToolExecutor is null || !context.ToolExecutor.HasTool(tool)) return null;
        if (context.ConfirmedToolExecution is { } confirmed &&
            string.Equals(confirmed.ToolName, tool, StringComparison.Ordinal))
            return confirmed;
        return await context.ToolExecutor.ExecuteAsync(
            Request(context), tool, JsonSerializer.SerializeToElement(arguments), cancellationToken);
    }

    public static string PatientTc(AiWorkflowContext context)
    {
        var direct = Parameter(context, "patient_tc");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (ResolvedPatientContext(context) is { ValueKind: JsonValueKind.Object } output &&
            output.TryGetProperty("selected_patient", out var patient) && patient.ValueKind == JsonValueKind.Object)
            return Text(patient, "patient_tc").Trim();
        return string.Empty;
    }

    public static JsonElement RowsOutput(IEnumerable<IReadOnlyDictionary<string, string>> rows, object? metadata = null)
    {
        var array = new JsonArray();
        foreach (var row in rows)
        {
            var item = new JsonObject();
            foreach (var pair in row) item[pair.Key] = pair.Value;
            array.Add(item);
        }
        return JsonSerializer.SerializeToElement(new { rows = array, metadata });
    }

    public static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        return value.GetRawText();
    }

    public static string NestedText(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, name)
            : string.Empty;

    public static IReadOnlyDictionary<string, string> FlatRow(JsonElement element)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
            row[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }
        return row;
    }

    public static string Label(string key) => key switch
    {
        "ranking_type" => "Sıralama Ölçütü", "rank" => "Sıra",
        "barcode" => "Barkod", "medicine_name" => "İlaç", "qr_code" => "Karekod",
        "movement_date" => "Tarih", "movement_type" => "Hareket Türü", "direction" => "Yön",
        "movement_count" => "Hareket Sayısı", "entry_count" => "Giriş Sayısı", "exit_count" => "Çıkış Sayısı",
        "analysis_dimension" => "Analiz Boyutu",
        "distinct_qr_count" => "Farklı Karekod", "issue_type" => "Şüphe Türü", "detail" => "Açıklama",
        "address" => "Hareket Adresi", "user" => "Kullanıcı", "reference_code" => "Referans",
        "name" => "Ürün", "purchase_price" => "Depocu Satış Fiyatı", "campaign_purchase_price" => "Kampanyalı Alış",
        "shelf_stock_quantity" => "Raf Stoku", "stock_value" => "Stok Değeri", "is_on_market" => "Piyasada", "active_ingredient" => "Etken Madde",
        "company" => "Firma", "day" => "Gün", "sale_price" => "Satış Fiyatı", "warehouse_price" => "Depocu Satış Fiyatı",
        "manufacturer_price" => "İmalatçı Fiyatı", "public_price" => "Kamu Fiyatı", "public_paid_price" => "Kamu Ödenen Fiyatı", "currency" => "Para Birimi", "net_revenue" => "Net Hasılat",
        "institution_revenue" => "Kamu Ücreti Hasılatı", "average_institution_amount" => "Ortalama Kamu Ücreti",
        "average_discount" => "Ortalama İndirim", "refund_rate" => "İade Oranı (%)",
        "week_start" => "Hafta Başlangıcı", "week_end" => "Hafta Bitişi", "peak_day" => "En Yüksek Gün",
        "group_key" => "Grup", "group_label" => "Grup Adı", "record_count" => "Kayıt Sayısı",
        "gross_revenue" => "Brüt Hasılat", "discount_total" => "İndirim Toplamı",
        "average_record_revenue" => "Kayıt Başına Hasılat", "average_item_revenue" => "Ürün Başına Hasılat",
        "pharmaceutical_form" => "Farmasötik Form", "package_description" => "Ambalaj", "prescription_type" => "Reçete Türü", "timestamp" => "İşlem Tarihi",
        "record_type" => "Kayıt Türü", "sale_type" => "Satış Türü", "status" => "Durum",
        "record_id" => "Kayıt No",
        "patient_record_id" => "Hasta Kayıt No", "patient_tc" => "Hasta T.C.", "phone_number" => "Telefon Numarası", "prescription_no" => "Reçete No",
        "prescription_number" => "Reçete Numarası", "electronic_prescription_number" => "E-Reçete Numarası",
        "prescription_uid" => "Reçete Kimliği", "version_number" => "Sürüm Numarası",
        "prescription_date" => "Reçete Tarihi", "last_transaction_date" => "Son İşlem Tarihi",
        "medula_filter_date" => "Medula Filtre Tarihi", "coverage" => "Kapsam",
        "qr_status" => "Karekod Durumu", "invoice_type" => "Fatura Türü",
        "invoice_type_code" => "Fatura Türü Kodu", "protocol_number" => "Protokol Numarası",
        "facility_code" => "Tesis Kodu", "doctor_registration_number" => "Doktor Tescil Numarası",
        "doctor_first_name" => "Doktor Adı", "doctor_last_name" => "Doktor Soyadı",
        "doctor_branch" => "Doktor Branşı", "doctor_certificate" => "Doktor Sertifikası",
        "patient_type" => "Hasta Türü", "insurance_type" => "Sigortalı Türü",
        "is_saved_on_medula" => "Medula'ya Kaydedildi", "is_local_archive" => "Yerel Arşiv Kaydı",
        "is_live_on_medula" => "Medula'da Mevcut", "is_stored_locally" => "Yerelde Kayıtlı",
        "is_deleted" => "Silindi", "is_permanently_deleted" => "Kalıcı Silindi", "last_user" => "Son Kullanıcı",
        "user_id" => "Kullanıcı Kodu", "patient_name" => "Hasta", "user_name" => "Kullanıcı",
        "quantity" => "Toplam Ürün Adedi", "transaction_quantity" => "İşlemdeki Toplam Ürün Adedi",
        "requested_products" => "İstenen Ürün", "requested_product_quantity" => "İstenen Ürün Adedi",
        "requested_product_gross_amount" => "İstenen Ürün Brüt Tutarı",
        "requested_product_net_amount" => "İstenen Ürün Net Tutarı",
        "gross_amount" => "Brüt Tutar", "discount_amount" => "İndirim", "net_amount" => "Net Tutar",
        "transaction_gross_amount" => "İşlem Brüt Tutarı", "transaction_net_amount" => "İşlem Net Tutarı",
        "entry_cost_total" => "Maliyet", "profit" => "Kâr", "missing_cost_quantity" => "Maliyeti Eksik Ürün Adedi",
        "total_amount" => "Ödeme Toplamı",
        "institution_amount" => "Kurum Tutarı", "is_debted" => "Veresiye", "is_marked" => "İşaretli",
        "notes" => "Not", "start_date" => "Başlangıç Tarihi", "end_date_inclusive" => "Bitiş Tarihi",
        "product_name" => "Ürün", "gross_price" => "Brüt Satır Tutarı", "net_price" => "Net Satır Tutarı",
        "entry_cost" => "Satış Anı Maliyeti", "payment_summary" => "Ödemeler", "qr_codes" => "Karekodlar",
        "section" => "Bölüm", "average_days" => "Ortalama Gün", "first_purchase" => "İlk Alışveriş",
        "last_purchase" => "Son Alışveriş", "purchase_count" => "Alışveriş Sayısı",
        "prescription_share" => "Reçeteli Payı (%)", "total_quantity" => "Toplam Adet",
        "prescription_quantity" => "Reçeteli Adet", "revenue_share" => "Hasılat Payı (%)",
        "cumulative_share" => "Kümülatif Pay (%)", "average_daily_revenue" => "Günlük Ortalama Net Hasılat",
        "zero_sale_days" => "Satış Olmayan Gün", "weekday" => "Haftanın Günü", "hour_range" => "Saat Aralığı",
        "average_basket" => "Ortalama Sepet", "patient_payment" => "Hasta Ödemesi", "patient_payment_share" => "Hasta Ödeme Payı (%)",
        "institution_payment_share" => "Kamu Ödeme Payı (%)", "discount_percent" => "İskonto Oranı (%)",
        "profit_margin" => "Kâr Marjı (%)",
        "calendar_days" => "Takvim Günü", "active_sale_days" => "Satış Yapılan Gün",
        "inactive_since" => "Bu Tarihten Beri Gelmedi", "abc_class" => "ABC Sınıfı",
        "sale_count" => "Satış Sayısı", "refund_count" => "İade Sayısı", "product_quantity" => "Ürün Adedi",
        "gross_sales" => "Brüt Satış", "refund_total" => "İade Toplamı",
        "requested_report_status" => "Rapor Durumu", "prescription_count" => "Reçete Sayısı",
        "reported_prescription_count" => "Raporlu Reçete Sayısı", "unreported_prescription_count" => "Raporsuz Reçete Sayısı",
        "total_prescription_count" => "Toplam Reçete Sayısı",
        "summary" => "Özet", "products" => "Ürünler", "payments" => "Ödemeler",
        "period" => "Dönem", "comparison_date" => "Karşılaştırma Tarihi",
        "old_sale_price" => "Önceki Satış Fiyatı", "current_sale_price" => "Güncel Satış Fiyatı",
        "old_warehouse_price" => "Önceki Depocu Fiyatı", "current_warehouse_price" => "Güncel Depocu Fiyatı",
        "price_change_count" => "Fiyat Değişikliği Sayısı", "first_change" => "İlk Değişiklik", "last_change" => "Son Değişiklik",
        "average_monthly_sales" => "Aylık Ortalama Satış", "wanted_stock_months" => "Hedef Stok Süresi (Ay)",
        "target_stock_quantity" => "Hedef Stok", "missing_stock_quantity" => "Eksik Stok",
        "recommended_purchase_quantity" => "Önerilen Alım Adedi", "consigned_stock_quantity" => "Yoldaki Stok",
        "stock_comment" => "Stok Değerlendirmesi",
        "estimated_coverage_days" or "coverage_days" => "Tahmini Stok Yeterlilik Süresi (Gün)",
        "sold_quantity" => "Satılan Ürün Adedi", "company_count" => "Firma Sayısı", "product_count" => "Ürün Sayısı",
        "stored_total" => "Kayıtlı Toplam", "payment_total" => "Ödeme Toplamı", "payment_type" => "Ödeme Türü",
        "amount" => "Tutar", "difference" => "Fark",
        "is_reconciled" => "Tutarlar Uyumlu", "duplicate_count" => "Tekrar Sayısı",
        "refund_reference" => "İade Referansı", "original_sale_reference" => "Asıl Satış Referansı",
        "refund_amount_rate_percent" => "İade Tutarı Oranı (%)", "average_items_per_sale" => "Satış Başına Ortalama Ürün",
        "discount_cohort" => "İndirim Grubu", "active_month_count" => "Satış Yapılan Ay Sayısı",
        "missing_fields" => "Eksik Alanlar", "missing_field_count" => "Eksik Alan Sayısı",
        "sale_references" => "Satış Referansları", "refund_references" => "İade Referansları",
        _ => CultureInfo.GetCultureInfo("tr-TR").TextInfo.ToTitleCase(key.Replace('_', ' '))
    };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    public static partial Regex HexColor();
}

public sealed class SeeMovementsWorkflow : IAiWorkflow
{
    private const string SearchTool = "qr.search_movements";
    private const string TypesTool = "qr.list_movement_types";
    public AiWorkflowDefinition Definition { get; } = new(
        "hareketleri_gor", "Hareketleri gör",
        "Lists QR/package movements. Supports one or multiple barcodes, an inclusive date range, movement type or entry/exit direction, user, movement address, and a bounded result limit. Queue ilac_barkod_eslestir first when the user supplied medicine names.",
        RequiredTools: [SearchTool, TypesTool],
        Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "Optional comma-separated or JSON-array exact barcodes; normally supplied by the preceding matcher.",
            ["start_date"] = "Inclusive YYYY-MM-DD only when the user supplied an exact date.",
            ["end_date"] = "Inclusive YYYY-MM-DD only when the user supplied an exact date.",
            ["period_months"] = "Relative number of months; deterministic code calculates dates.",
            ["period_days"] = "Relative number of days; deterministic code calculates dates.",
            ["calendar_period"] = "today, yesterday, this_week, last_week, this_month, last_month, this_year, last_year; calendar boundaries calculated locally.",
            ["nearest"] = "true for the most recent matching movement across all supported history.",
            ["movement_types"] = "Optional comma-separated configured movement type codes or Turkish display names.",
            ["direction"] = "all, entry, or exit.", ["user_id"] = "Optional exact user ID.",
            ["address_type"] = "all, patient, pharmacy, or depot.", ["address_code"] = "Optional exact prefixed address code.",
            ["limit"] = "Maximum combined rows, 1-200; default 100."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (context.ToolExecutor is null || !context.ToolExecutor.HasTool(SearchTool)) return WorkflowData.Missing(SearchTool);
        var (start, end) = WorkflowData.DateRange(context);
        var requestedTypes = WorkflowData.List(context, "movement_types");
        string[] typeCodes = [];
        if (requestedTypes.Count > 0)
        {
            var typeExecution = await WorkflowData.Execute(context, TypesTool, new { }, cancellationToken);
            if (typeExecution is null) return WorkflowData.Missing(TypesTool);
            if (!typeExecution.IsSuccess) return WorkflowData.Failed("Hareket türleri alınamadı.", new { tool = TypesTool, status = typeExecution.Status.ToString() });
            typeCodes = ResolveTypes(typeExecution.Output, requestedTypes);
            var hasStructuredFallback = !string.Equals(WorkflowData.Parameter(context, "direction") ?? "all", "all", StringComparison.OrdinalIgnoreCase) ||
                                        !string.Equals(WorkflowData.Parameter(context, "address_type") ?? "all", "all", StringComparison.OrdinalIgnoreCase);
            if (typeCodes.Length == 0 && !hasStructuredFallback)
                return WorkflowData.Failed("İstenen hareket türü tanınamadı.");
        }

        var barcodes = WorkflowData.Barcodes(context);
        var limit = WorkflowData.Boolean(context, "nearest")
            ? 1
            : WorkflowData.Integer(context, "limit", 100, 1, 200);
        var arguments = new Dictionary<string, object?>
        {
            ["start_date"] = start, ["end_date"] = end,
            ["movement_type_codes"] = typeCodes.Length == 0 ? null : typeCodes,
            ["direction"] = WorkflowData.Parameter(context, "direction") ?? "all",
            ["barcodes"] = barcodes.Count == 0 ? null : barcodes,
            ["user_id"] = WorkflowData.Parameter(context, "user_id"),
            ["address_type"] = WorkflowData.Parameter(context, "address_type") ?? "all",
            ["address_code"] = WorkflowData.Parameter(context, "address_code"),
            ["include_summary"] = true, ["limit"] = limit
        };
        var execution = await WorkflowData.Execute(context, SearchTool, arguments, cancellationToken);
        if (execution is null) return WorkflowData.Missing(SearchTool);
        if (!execution.IsSuccess && execution.Status is AiToolExecutionStatus.ExternalServiceUnavailable
                or AiToolExecutionStatus.ExecutionFailed or AiToolExecutionStatus.Timeout)
            execution = await WorkflowData.Execute(context, SearchTool, arguments, cancellationToken);
        if (execution is null) return WorkflowData.Missing(SearchTool);
        if (!execution.IsSuccess)
            return WorkflowData.Failed("Hareketler alınamadı.", new
            {
                tool = SearchTool,
                status = execution.Status.ToString(),
                execution.SafeMessage
            });
        var rows = execution.Output.TryGetProperty("movements", out var movements) && movements.ValueKind == JsonValueKind.Array
            ? movements.EnumerateArray().Select(ToRow).Take(limit).ToList()
            : [];
        var totalCount = execution.Output.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object &&
                         summary.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var parsedTotal)
            ? parsedTotal
            : rows.Count;
        var hasMore = execution.Output.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
        var moduleAction = new AiWorkflowModuleAction(
            "open_qr_movements",
            "Karekod Hareketleri modülünde aç",
            "products.qr-movements",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["start_date"] = start,
                ["end_date"] = end,
                ["barcodes"] = string.Join(',', barcodes),
                ["movement_types"] = string.Join(',', typeCodes),
                ["user_id"] = WorkflowData.Parameter(context, "user_id") ?? string.Empty,
                ["address_type"] = WorkflowData.Parameter(context, "address_type") ?? string.Empty,
                ["address_code"] = WorkflowData.Parameter(context, "address_code") ?? string.Empty
            });
        var periodDescription = DescribeMovementPeriod(context, start, end);
        var resultDescription = rows.Count == 0
            ? "Filtrelere uyan hareket bulunamadı."
            : hasMore || totalCount > rows.Count
                ? $"Toplam {totalCount:N0} hareket bulundu; ilk {rows.Count:N0} kayıt gösteriliyor."
                : $"{rows.Count:N0} hareket bulundu.";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            $"{periodDescription} {resultDescription}",
            WorkflowData.RowsOutput(rows, new
            {
                start_date = start,
                end_date = end,
                barcodes,
                period_mode = WorkflowData.Boolean(context, "nearest") ? "nearest" : "range",
                default_period = WorkflowData.Boolean(context, "_period_default"),
                total_count = totalCount,
                columns = new[] { "movement_date", "barcode", "medicine_name", "qr_code", "movement_type", "direction", "address", "user", "reference_code" },
                visible_columns = new[] { "movement_date", "medicine_name", "barcode", "movement_type", "direction", "address", "user", "reference_code" }
            }),
            ModuleActions: [moduleAction]);
    }

    private static string DescribeMovementPeriod(AiWorkflowContext context, string start, string end)
    {
        static string Display(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"));
        if (WorkflowData.Boolean(context, "nearest"))
            return $"En güncel hareket {Display(start)}–{Display(end)} arasındaki kayıt geçmişinde arandı.";
        if (WorkflowData.Boolean(context, "_period_default"))
            return $"Tarih belirtilmediği için son 30 günlük varsayılan dönem ({Display(start)}–{Display(end)}) kullanıldı.";
        return $"{Display(start)}–{Display(end)} dönemi kullanıldı.";
    }

    private static string[] ResolveTypes(JsonElement output, IReadOnlyList<string> requested)
    {
        if (!output.TryGetProperty("movement_types", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        var normalizedRequested = requested.Select(NormalizeMovementPhrase).Where(value => value.Length > 0).ToArray();
        return values.EnumerateArray().Where(type =>
            {
                var code = NormalizeMovementPhrase(WorkflowData.Text(type, "code"));
                var display = NormalizeMovementPhrase(WorkflowData.Text(type, "display_name"));
                var displayTokens = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return normalizedRequested.Any(item =>
                    item == code || item == display || item.Contains(display, StringComparison.Ordinal) ||
                    display.Contains(item, StringComparison.Ordinal) ||
                    displayTokens.Length > 0 && displayTokens.All(token => item.Split(' ').Contains(token, StringComparer.Ordinal)));
            })
            .Select(type => WorkflowData.Text(type, "code"))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeMovementPhrase(string value) => string.Join(' ', value
        .Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"))
        .Replace('ü', 'u').Replace('ı', 'i').Replace('ş', 's').Replace('ç', 'c').Replace('ö', 'o').Replace('ğ', 'g')
        .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IReadOnlyDictionary<string, string> ToRow(JsonElement movement) => new Dictionary<string, string>
    {
        ["movement_date"] = WorkflowData.Text(movement, "movement_date"),
        ["barcode"] = WorkflowData.Text(movement, "barcode"),
        ["medicine_name"] = WorkflowData.Text(movement, "medicine_name"),
        ["qr_code"] = WorkflowData.Text(movement, "qr_code"),
        ["movement_type"] = WorkflowData.NestedText(movement, "movement_type", "display_name"),
        ["direction"] = WorkflowData.NestedText(movement, "movement_type", "direction"),
        ["address"] = WorkflowData.NestedText(movement, "movement_address", "name"),
        ["user"] = WorkflowData.NestedText(movement, "user", "name"),
        ["reference_code"] = WorkflowData.Text(movement, "reference_code")
    };
}

public sealed class DisplayTableWorkflow : IAiWorkflow
{
    private static readonly IReadOnlyDictionary<string, string[]> Palettes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ocean"] = ["#CCE5FF", "#FFD6CC", "#D8F2C4", "#E8D2FF", "#FFF0AD", "#C8F1EC", "#FFD1E5", "#E7E2C8", "#D1D7FF", "#F6D4A9"],
            ["mint"] = ["#D3F0C1", "#D5DCFF", "#FFD1DD", "#FFE7A8", "#C6EFE9", "#E6D0FF", "#FFD9B8", "#CDE7FF", "#E4E0BE", "#F4CDEA"],
            ["amber"] = ["#FFE39A", "#CBE5FF", "#FFD0D0", "#D4EFC0", "#E3D0FF", "#C4EEE7", "#F5CFEB", "#E5DFC0", "#CDD2FF", "#FFD5AE"],
            ["lavender"] = ["#E2CDFF", "#C9E8FF", "#FFD1C7", "#D6F0C1", "#FFEAA4", "#C4EEE7", "#FFD0E8", "#D4D9FF", "#E9DFBE", "#F4D0B2"],
            ["rose"] = ["#FFC9DC", "#CBE7FF", "#D5F0C0", "#FFE49E", "#DFCFFF", "#C3EEE8", "#FFD1C7", "#D2D8FF", "#E8E0BD", "#F2CFEE"],
            ["slate"] = ["#D0DAE2", "#FFD2C7", "#D5EDC5", "#DDCEFF", "#FFE6A2", "#C5ECE7", "#F8CDE1", "#C9E3FF", "#E5DDBB", "#F2D0AF"]
        };
    public AiWorkflowDefinition Definition { get; } = new(
        "tabloda_goster", "Tabloda göster",
        "Renders the preceding workflow's structured rows in an in-chat table. Color modes are retained only when categories repeat enough to aid visual identification.",
        ProducesUserResponse: true,
        Parameters: new Dictionary<string, string>
        {
            ["title"] = "Short English table title.",
            ["color_modes"] = "Compact field|English label|palette entries separated by semicolons. Palettes: ocean, mint, amber, lavender, rose, slate.",
            ["default_color_mode"] = "ID of the initially selected color mode."
        });

    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var source = context.Outputs.Reverse().Select(pair => pair.Value)
            .FirstOrDefault(output => output.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array);
        if (source.ValueKind == JsonValueKind.Undefined || !source.TryGetProperty("rows", out var rowArray))
            return Task.FromResult(WorkflowData.Failed("Tabloda gösterilecek yapılandırılmış veri bulunamadı."));
        var rows = rowArray.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object)
            .Select(row => (IReadOnlyDictionary<string, string>)row.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText(), StringComparer.Ordinal))
            .ToArray();
        var rowKeys = rows.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var declaredKeys = MetadataStrings(source, "columns");
        var keys = declaredKeys.Concat(rowKeys).Distinct(StringComparer.Ordinal).ToArray();
        var visibleKeys = MetadataStrings(source, "visible_columns").ToHashSet(StringComparer.Ordinal);
        var columns = keys.Select(key => new AiWorkflowTableColumn(
            key,
            ColumnLabel(source, key),
            visibleKeys.Count == 0 || visibleKeys.Contains(key))).ToArray();
        var modes = EnsureScenarioModes(ParseModes(WorkflowData.Parameter(context, "color_modes"), keys), keys, rows);
        var requestedDefault = WorkflowData.Parameter(context, "default_color_mode");
        var defaultMode = modes.Any(mode => mode.Id == requestedDefault) ? requestedDefault! : modes.FirstOrDefault()?.Id ?? string.Empty;
        var title = WorkflowData.Parameter(context, "title") ?? "İş Akışı Sonuçları";
        var totalRowCount = MetadataInteger(source, "total_count");
        var rowBackgroundField = MetadataText(source, "row_background_field");
        if (rows.Length == 0)
        {
            var emptyMessage = WorkflowData.PreviousResultMessage(context);
            return Task.FromResult(new AiWorkflowStepResult(
                AiWorkflowStepStatus.Completed,
                emptyMessage.Length > 0 ? emptyMessage : "Filtrelere uyan kayıt bulunamadı.",
                source.Clone()));
        }
        var table = new AiWorkflowTablePresentation(
            title, columns, rows, modes, defaultMode, totalRowCount,
            MetadataText(source, "highlight_field"), rowBackgroundField);
        var sourceMessage = WorkflowData.PreviousResultMessage(context);
        var message = sourceMessage.Length > 0
            ? sourceMessage
            : rows.Length == 0 ? "Sonuç bulunamadı." : $"{rows.Length} kayıt tabloda gösteriliyor.";
        return Task.FromResult(new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            message, source.Clone(), Table: table));
    }

    private static IReadOnlyList<string> MetadataStrings(JsonElement source, string name) =>
        source.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).Where(value => value.Length > 0).ToArray()
            : [];

    private static int? MetadataInteger(JsonElement source, string name) =>
        source.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string? MetadataText(JsonElement source, string name) =>
        source.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ColumnLabel(JsonElement source, string key)
    {
        if (source.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("column_labels", out var labels) &&
            labels.ValueKind == JsonValueKind.Object &&
            labels.TryGetProperty(key, out var supplied) &&
            supplied.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(supplied.GetString()))
            return supplied.GetString()!.Trim();
        if (key != "group_label") return WorkflowData.Label(key);
        return MetadataText(source, "group_by") switch
        {
            "product" => "İlaç",
            "patient" => "Hasta",
            "user" => "Kullanıcı",
            "sale_type" => "Satış Türü",
            "record_type" => "Kayıt Türü",
            "movement_type" => "Hareket Türü",
            "direction" => "Yön",
            "barcode" or "medicine" => "İlaç",
            "address" => "Hareket Adresi",
            "day" or "month" => "Dönem",
            "hour" => "Saat",
            "weekday" => "Haftanın Günü",
            "day_hour" => "Gün ve Saat",
            _ => "Grup"
        };
    }

    private static IReadOnlyList<AiWorkflowTableColorMode> ParseModes(string? value, IReadOnlyCollection<string> keys)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (!value.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Split('|', StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 3 && keys.Contains(parts[0]) && Palettes.ContainsKey(parts[2]))
                .Select(parts => new AiWorkflowTableColorMode(parts[0], parts[1], parts[0], Palettes[parts[2]]))
                .Where(mode => mode.Label.Length > 0)
                .DistinctBy(mode => mode.Id, StringComparer.Ordinal).Take(6).ToArray();
        }
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.EnumerateArray().Select(item => new AiWorkflowTableColorMode(
                    WorkflowData.Text(item, "id"), WorkflowData.Text(item, "label"), WorkflowData.Text(item, "field"),
                    item.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonValueKind.Array
                        ? colors.EnumerateArray().Select(color => color.GetString() ?? string.Empty).Where(color => WorkflowData.HexColor().IsMatch(color)).Take(12).ToArray()
                        : item.TryGetProperty("colors", out colors) && colors.ValueKind == JsonValueKind.Object
                            ? colors.EnumerateObject().Select(color => color.Value.GetString() ?? string.Empty).Where(color => WorkflowData.HexColor().IsMatch(color)).Take(12).ToArray()
                        : []))
                .Where(mode => mode.Id.Length > 0 && mode.Label.Length > 0 && keys.Contains(mode.Field) && mode.Colors.Count > 0)
                .DistinctBy(mode => mode.Id, StringComparer.Ordinal).Take(6).ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<AiWorkflowTableColorMode> EnsureScenarioModes(
        IReadOnlyList<AiWorkflowTableColorMode> requested,
        IReadOnlyCollection<string> keys,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var modes = requested.Where(mode => IsMeaningful(mode.Field)).ToList();
        Add("sale_type", "Satış türüne göre", "amber");
        Add("record_type", "Kayıt türüne göre", "rose");
        Add("movement_type", "Hareket türüne göre", "mint");
        Add("barcode", "Barkoda göre", "ocean");
        return modes.Take(6).ToArray();

        void Add(string field, string label, string palette)
        {
            if (!keys.Contains(field, StringComparer.Ordinal) ||
                !IsMeaningful(field) || modes.Any(mode => string.Equals(mode.Field, field, StringComparison.Ordinal))) return;
            modes.Add(new AiWorkflowTableColorMode(field, label, field, Palettes[palette]));
        }

        bool IsMeaningful(string field)
        {
            var values = rows.Select(row => row.GetValueOrDefault(field))
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (values.Length < 3) return false;
            var distinct = values.Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
            return distinct >= 2 && distinct <= 12 && distinct * 2 <= values.Length;
        }
    }
}

public sealed class OpenModulesWorkflow : IAiWorkflow
{
    private static readonly HashSet<string> AllowedModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "products.catalog", "products.stock", "products.qr-movements",
        "sales.retail", "sales.retail-list", "sales.retail-refund", "sales.consignment",
        "takas.incoming-entry", "takas.entry", "takas.list",
        "invoices.offline-list", "invoices.addresses", "invoices.stock-movements", "invoices.partial-refund",
        "medula.prescription-entry", "medula.prescription-list", "medula.e-prescription-query", "medula.connection-test"
    };

    public AiWorkflowDefinition Definition { get; } = new(
        "modulleri_ac", "Modülleri aç",
        "Opens one allowed module or two side-by-side in a new tab. products.catalog=Ürün Kataloğu; products.stock=Stok; products.qr-movements=Karekod Hareketleri; sales.retail-list=Satışlar (existing transactions); sales.retail=Yeni Perakende Satış (new sale entry, NOT the sales list); sales.retail-refund=İade; sales.consignment=Emanet; takas.list=Takas Listesi; invoices.offline-list=Faturalar; invoices.stock-movements=Fatura Hareketleri; medula.prescription-entry=Reçete Giriş; medula.prescription-list=Reçete Listesi.",
        Parameters: new Dictionary<string, string>
        {
            ["module_ids"] = "One or two exact allowed module IDs, comma-separated.",
            ["layout"] = "side_by_side or stacked when two modules are requested.",
            ["tab_title"] = "Optional concise English title for the new tab."
        });

    public Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var moduleIds = WorkflowData.List(context, "module_ids").Select(NormalizeModuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (moduleIds.Length > 2)
            return Task.FromResult(WorkflowData.Failed("Bir sekmede en fazla iki modül açılabilir. Lütfen bir veya iki modül seçin."));
        if (moduleIds.Length == 0)
            return Task.FromResult(WorkflowData.Failed("Açılacak modül belirtilmedi."));
        var unknown = moduleIds.Where(moduleId => !AllowedModules.Contains(moduleId)).ToArray();
        if (unknown.Length > 0)
            return Task.FromResult(WorkflowData.Failed($"Desteklenmeyen modül: {string.Join(", ", unknown)}."));
        var layout = string.Equals(WorkflowData.Parameter(context, "layout"), "stacked", StringComparison.OrdinalIgnoreCase)
            ? "stacked"
            : "side_by_side";
        var tabTitle = WorkflowData.Parameter(context, "tab_title") ?? string.Empty;
        var message = moduleIds.Length == 1
            ? "Modülü yeni sekmede açabilirsiniz."
            : "İki modülü aynı yeni sekmede yan yana açabilirsiniz.";
        var barcodes = WorkflowData.Barcodes(context);
        var directCatalog = moduleIds.Length == 1 && moduleIds[0].Equals("products.catalog", StringComparison.OrdinalIgnoreCase);
        var action = new AiWorkflowModuleAction(
            "open_module_workspace",
            moduleIds.Length == 1 ? "Modülü yeni sekmede aç" : "Modülleri yan yana aç",
            directCatalog ? "products.catalog" : "workspace.modules",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["module_ids"] = string.Join(',', moduleIds),
                ["layout"] = layout,
                ["tab_title"] = tabTitle,
                ["barcodes"] = string.Join(',', barcodes)
            });
        return Task.FromResult(new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            message,
            JsonSerializer.SerializeToElement(new { module_ids = moduleIds, layout, tab_title = tabTitle }),
            ModuleActions: [action]));
    }

    private static string NormalizeModuleId(string value) => value.Trim().ToLowerInvariant() switch
    {
        "qr_movements" or "qr-movements" or "karekod_hareketleri" => "products.qr-movements",
        "product_catalog" or "product-catalog" or "urun_katalogu" => "products.catalog",
        "product_stock" or "stock" or "stok" => "products.stock",
        "sales" or "sales_list" or "satislar" => "sales.retail-list",
        "retail_sales" or "perakende_satis" => "sales.retail",
        _ => value.Trim()
    };
}

public sealed class SeeSalesWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "satislari_gor", "Satışları gör",
        "Lists individual pharmacy sales with optional barcode, date, sale kind (prescription/retail), payment, patient, user, and refund filters. Queue ilac_barkod_eslestir first for medicine names and tabloda_goster for a table.",
        RequiredTools: ["sale.search_records"],
        Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "Optional barcode list.", ["start_date"] = "Exact inclusive YYYY-MM-DD.", ["end_date"] = "Exact inclusive YYYY-MM-DD.",
            ["period_months"] = "Relative number of months.", ["period_days"] = "Relative number of days.",
            ["calendar_period"] = "today, yesterday, this_week, last_week, this_month, last_month, this_year, last_year; not a rolling window.",
            ["all_history"] = "true when the user asks for all available sales without a narrower period.",
            ["sale_types"] = "prescription, retail, or both.", ["payment_types"] = "Optional payment filters.",
            ["statuses"] = "Optional pending, medula_reported, completed, reconciliation_required, cancelled values.",
            ["qr_codes"] = "Optional exact QR-code list.", ["prescription_no"] = "Optional exact prescription number.",
            ["user_id"] = "Optional user ID. Patient IDs are supplied only by hasta_eslestir.",
            ["include_refunds"] = "true or false.", ["is_debted"] = "Optional true or false debt filter.",
            ["marked_only"] = "true for marked records only.", ["minimum_total"] = "Optional minimum total.",
            ["maximum_total"] = "Optional maximum total.", ["sort_direction"] = "newest_first or oldest_first.",
            ["start_time"] = "Optional local HH:mm lower bound within each selected day.",
            ["end_time"] = "Optional local HH:mm upper bound within each selected day.",
            ["include_lines"] = "true when item/QR details are requested.", ["limit"] = "1-200.",
            ["latest_only"] = "true when the result is the requested newest N records; changes only the user-facing summary."
        });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        const string tool = "sale.search_records";
        var (start, end) = WorkflowData.DateRange(context);
        var barcodes = WorkflowData.Barcodes(context);
        var saleTypes = WorkflowData.SaleTypes(context);
        var includeRefunds = WorkflowData.Boolean(context, "include_refunds");
        var requestedLimit = WorkflowData.Integer(context, "limit", 100, 1, 200);
        var pageSize = Math.Min(20, requestedLimit);
        var execution = await WorkflowData.Execute(context, tool, new
        {
            barcodes,
            qr_codes = WorkflowData.List(context, "qr_codes"),
            start_date = start,
            end_date = end,
            start_time = WorkflowData.Parameter(context, "start_time"),
            end_time = WorkflowData.Parameter(context, "end_time"),
            sale_types = saleTypes,
            payment_types = WorkflowData.List(context, "payment_types"),
            statuses = WorkflowData.List(context, "statuses"),
            patient_record_id = WorkflowData.PatientRecordId(context),
            user_id = WorkflowData.Parameter(context, "user_id"),
            prescription_no = WorkflowData.Parameter(context, "prescription_no"),
            include_refunds = includeRefunds,
            is_debted = WorkflowData.NullableBoolean(context, "is_debted"),
            marked_only = WorkflowData.Boolean(context, "marked_only"),
            minimum_total = WorkflowData.NullableDecimal(context, "minimum_total"),
            maximum_total = WorkflowData.NullableDecimal(context, "maximum_total"),
            include_lines = barcodes.Count > 0 || WorkflowData.Boolean(context, "include_lines"),
            sort_direction = WorkflowData.Parameter(context, "sort_direction") ?? "newest_first",
            offset = 0,
            limit = pageSize
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Satış kayıtları alınamadı.", new { tool, status = execution.Status.ToString(), execution.SafeMessage });
        var barcodeSet = barcodes.ToHashSet(StringComparer.Ordinal);
        if (!execution.Output.TryGetProperty("sales", out var sales) || sales.ValueKind != JsonValueKind.Array)
            return WorkflowData.Failed("Satış kayıtları okunamadı. Lütfen tekrar deneyin.");
        var saleValues = sales.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).Select(value => value.Clone()).ToList();
        var reportedTotal = execution.Output.TryGetProperty("total_count", out var reported) && reported.TryGetInt32(out var parsedReported)
            ? parsedReported : saleValues.Count;
        var incompletePage = false;
        for (var offset = saleValues.Count; offset < Math.Min(reportedTotal, requestedLimit);)
        {
            var page = await WorkflowData.Execute(context, tool, new
            {
                barcodes, qr_codes = WorkflowData.List(context, "qr_codes"), start_date = start, end_date = end,
                start_time = WorkflowData.Parameter(context, "start_time"), end_time = WorkflowData.Parameter(context, "end_time"),
                sale_types = saleTypes, payment_types = WorkflowData.List(context, "payment_types"),
                statuses = WorkflowData.List(context, "statuses"), patient_record_id = WorkflowData.PatientRecordId(context),
                user_id = WorkflowData.Parameter(context, "user_id"), prescription_no = WorkflowData.Parameter(context, "prescription_no"),
                include_refunds = includeRefunds, is_debted = WorkflowData.NullableBoolean(context, "is_debted"),
                marked_only = WorkflowData.Boolean(context, "marked_only"), minimum_total = WorkflowData.NullableDecimal(context, "minimum_total"),
                maximum_total = WorkflowData.NullableDecimal(context, "maximum_total"),
                include_lines = barcodes.Count > 0 || WorkflowData.Boolean(context, "include_lines"),
                sort_direction = WorkflowData.Parameter(context, "sort_direction") ?? "newest_first",
                offset, limit = Math.Min(pageSize, requestedLimit - offset)
            }, cancellationToken);
            if (page is null || !page.IsSuccess || !page.Output.TryGetProperty("sales", out var pageSales) || pageSales.ValueKind != JsonValueKind.Array)
            {
                incompletePage = true;
                break;
            }
            var additions = pageSales.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).Select(value => value.Clone()).ToArray();
            if (additions.Length == 0)
            {
                incompletePage = true;
                break;
            }
            saleValues.AddRange(additions);
            offset += additions.Length;
        }
        saleValues = saleValues.Take(requestedLimit).ToList();
        var rows = saleValues.Select(value => ToSaleRow(value, barcodeSet)).ToArray();
        var totalCount = reportedTotal;
        var hasMore = totalCount > rows.Length;
        var action = new AiWorkflowModuleAction(
            "open_sales_list",
            "Satışlar modülünde aç",
            "sales.retail-list",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["start_date"] = start,
                ["end_date"] = end,
                ["barcodes"] = string.Join(',', barcodes),
                ["sale_types"] = string.Join(',', saleTypes),
                ["payment_types"] = string.Join(',', WorkflowData.List(context, "payment_types")),
                ["start_time"] = WorkflowData.Parameter(context, "start_time") ?? string.Empty,
                ["end_time"] = WorkflowData.Parameter(context, "end_time") ?? string.Empty,
                ["statuses"] = string.Join(',', WorkflowData.List(context, "statuses")),
                ["qr_codes"] = string.Join(',', WorkflowData.List(context, "qr_codes")),
                ["prescription_no"] = WorkflowData.Parameter(context, "prescription_no") ?? string.Empty,
                ["include_refunds"] = includeRefunds ? "true" : "false",
                ["user_id"] = WorkflowData.Parameter(context, "user_id") ?? string.Empty,
                ["patient_record_id"] = WorkflowData.PatientRecordId(context)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["is_debted"] = WorkflowData.Parameter(context, "is_debted") ?? string.Empty,
                ["marked_only"] = WorkflowData.Parameter(context, "marked_only") ?? string.Empty,
                ["minimum_total"] = WorkflowData.Parameter(context, "minimum_total") ?? string.Empty,
                ["maximum_total"] = WorkflowData.Parameter(context, "maximum_total") ?? string.Empty
            });
        var period = $"{DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd.MM.yyyy}–" +
                     $"{DateOnly.ParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd.MM.yyyy} dönemi";
        var latestOnly = WorkflowData.Boolean(context, "latest_only");
        var discountedOnly = WorkflowData.List(context, "payment_types")
            .Contains("discount", StringComparer.OrdinalIgnoreCase);
        var message = rows.Length == 0
            ? latestOnly
                ? discountedOnly ? "İskontolu satış kaydı bulunamadı." : "Satış kaydı bulunamadı."
                : $"{period}: Filtrelere uyan satış bulunamadı."
            : latestOnly
                ? discountedOnly
                    ? $"En son {rows.Length:N0} iskontolu satış, toplam {totalCount:N0} iskontolu satış içinden gösteriliyor. Her satır tek bir satış işlemini temsil eder."
                    : $"En son {rows.Length:N0} satış, toplam {totalCount:N0} kayıt içinden gösteriliyor."
                : hasMore || totalCount > rows.Length
                    ? $"{period}: Toplam {totalCount:N0} satış kaydı bulundu; ilk {rows.Length:N0} kayıt gösteriliyor."
                    : $"{period}: {rows.Length:N0} satış kaydı bulundu.";
        if (discountedOnly && rows.Length > 0 && !message.Contains("Her satır", StringComparison.Ordinal))
            message += " Her satır tek bir satış işlemini temsil eder.";
        if (incompletePage)
            message += " Sonraki kayıtlar alınamadığı için liste eksik olabilir; lütfen tekrar deneyin.";
        if (WorkflowData.Parameter(context, "start_time") is not null || WorkflowData.Parameter(context, "end_time") is not null)
            message += $" Saat filtresi: {WorkflowData.Parameter(context, "start_time") ?? "00:00"}–{WorkflowData.Parameter(context, "end_time") ?? "23:59"}.";
        var columns = new[]
        {
            "timestamp", "reference_code", "sale_type", "patient_name", "requested_products",
            "requested_product_quantity", "requested_product_gross_amount", "requested_product_net_amount",
            "transaction_quantity", "transaction_gross_amount", "discount_amount", "transaction_net_amount",
            "record_id", "record_type", "status", "patient_record_id", "patient_tc", "prescription_no",
            "user_id", "user_name", "entry_cost_total", "profit", "missing_cost_quantity", "total_amount",
            "institution_amount", "is_debted", "is_marked", "notes"
        };
        var visibleColumns = barcodes.Count > 0
            ? new[] { "timestamp", "sale_type", "patient_name", "requested_products", "requested_product_quantity", "requested_product_net_amount" }
            : new[] { "timestamp", "sale_type", "patient_name", "transaction_quantity", "transaction_gross_amount", "discount_amount", "transaction_net_amount" };
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            message,
            WorkflowData.RowsOutput(rows, new
            {
                start_date = start, end_date = end,
                start_time = WorkflowData.Parameter(context, "start_time"), end_time = WorkflowData.Parameter(context, "end_time"),
                barcodes, total_count = totalCount, is_partial = incompletePage, columns, visible_columns = visibleColumns
            }),
            ModuleActions: [action]);

    }

    private static IReadOnlyDictionary<string, string> ToSaleRow(JsonElement sale, IReadOnlySet<string> requestedBarcodes)
    {
        var row = new Dictionary<string, string>(WorkflowData.FlatRow(sale), StringComparer.Ordinal);
        Rename(row, "quantity", "transaction_quantity");
        Rename(row, "gross_amount", "transaction_gross_amount");
        Rename(row, "net_amount", "transaction_net_amount");
        if (requestedBarcodes.Count == 0 || !sale.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return row;
        var matching = lines.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.Object &&
            requestedBarcodes.Contains(WorkflowData.Text(line, "barcode"))).ToArray();
        row["requested_products"] = string.Join(", ", matching.Select(line => WorkflowData.Text(line, "product_name"))
            .Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase));
        row["requested_product_quantity"] = matching.Length.ToString(CultureInfo.InvariantCulture);
        row["requested_product_gross_amount"] = Sum(matching, "gross_price").ToString(CultureInfo.InvariantCulture);
        row["requested_product_net_amount"] = Sum(matching, "net_price").ToString(CultureInfo.InvariantCulture);
        return row;
    }

    private static void Rename(IDictionary<string, string> row, string source, string target)
    {
        if (!row.Remove(source, out var value)) return;
        row[target] = value;
    }

    private static decimal Sum(IEnumerable<JsonElement> values, string property) => values.Sum(value =>
        value.TryGetProperty(property, out var item) && item.TryGetDecimal(out var number) ? number : 0m);
}

public sealed class GetBarcodeInformationWorkflow : IAiWorkflow
{
    private const string Tool = "product.get";
    private const string PriceHistoryTool = "product.get_price_history";
    public AiWorkflowDefinition Definition { get; } = new(
        "barkod_bilgisi_getir", "Barkod bilgisi getir",
        "Gets local catalog information for one or more exact barcodes and returns only the fields requested by the user. Queue ilac_barkod_eslestir first for medicine names.",
        RequiredTools: [Tool, PriceHistoryTool], Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "Optional barcode list, normally inherited from the matcher.",
            ["fields"] = "Requested comma-separated fields. Allowed examples: name, sale_price, public_price, public_paid_price, purchase_price (depocu satış), campaign_purchase_price, shelf_stock_quantity, is_on_market, active_ingredient, company, pharmaceutical_form, prescription_type."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Barkod bilgisi için bir barkod veya ilaç seçimi gerekiyor.");
        var fields = WorkflowData.List(context, "fields").Select(NormalizeField)
            .Where(field => field.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var missingFieldsOnly = fields.Any(field => field is "missing_fields" or "bos_alanlar");
        if (fields.Length == 0 || fields.Any(IsAllFieldsRequest))
            fields = ["name", "active_ingredient", "company", "purchase_price", "campaign_purchase_price",
                "shelf_stock_quantity", "is_on_market", "pharmaceutical_form", "package_description", "prescription_type"];
        if (missingFieldsOnly)
            fields = ["name", "active_ingredient", "company", "pharmaceutical_form", "package_description", "prescription_type"];
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var barcode in barcodes)
        {
            var execution = await WorkflowData.Execute(context, Tool, new { barcode }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(Tool);
            if (!execution.IsSuccess) return WorkflowData.Failed("Barkod bilgisi alınamadı.", new { tool = Tool, status = execution.Status.ToString() });
            if (!execution.Output.TryGetProperty("found", out var found) || !found.GetBoolean() ||
                !execution.Output.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object) continue;
            var summary = product.TryGetProperty("summary", out var value) ? value : default;
            var row = new Dictionary<string, string> { ["barcode"] = barcode };
            var missing = new List<string>();
            foreach (var field in fields)
            {
                var normalized = NormalizeField(field);
                var text = summary.ValueKind == JsonValueKind.Object ? WorkflowData.Text(summary, normalized) : string.Empty;
                if (text.Length == 0) text = WorkflowData.Text(product, normalized);
                if (text.Length > 0) row[normalized] = text;
                else if (missingFieldsOnly && normalized != "name") missing.Add(WorkflowData.Label(normalized));
            }
            if (missingFieldsOnly)
                row["missing_fields"] = missing.Count == 0 ? "Boş alan yok" : string.Join(", ", missing);
            var priceFields = fields.Select(field => field.ToLowerInvariant()).Where(field =>
                field is "sale_price" or "public_price" or "public_paid_price" or "warehouse_price" or "manufacturer_price").ToArray();
            if (priceFields.Length > 0)
            {
                var prices = await WorkflowData.Execute(context, PriceHistoryTool, new { barcode, offset = 0, limit = 500 }, cancellationToken);
                if (prices is null) return WorkflowData.Missing(PriceHistoryTool);
                if (!prices.IsSuccess) return WorkflowData.Failed("Barkod fiyat bilgisi alınamadı.", new { tool = PriceHistoryTool, status = prices.Status.ToString() });
                if (prices.Output.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
                {
                    var latest = points.EnumerateArray().LastOrDefault(point => point.ValueKind == JsonValueKind.Object);
                    if (latest.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in priceFields)
                        {
                            var text = WorkflowData.Text(latest, field);
                            if (text.Length > 0) row[field] = text;
                        }
                    }
                }
            }
            rows.Add(row);
        }
        var message = rows.Count == 0 ? "Barkod için yerel ürün kaydı bulunamadı." :
            string.Join(Environment.NewLine, rows.Select(row => string.Join(" · ", row.Select(pair => $"{WorkflowData.Label(pair.Key)}: {pair.Value}"))));
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message, WorkflowData.RowsOutput(rows));
    }

    private static bool IsAllFieldsRequest(string value)
    {
        var normalized = value.ToLowerInvariant()
            .Replace('ü', 'u').Replace('ı', 'i').Replace('ş', 's').Replace('ç', 'c').Replace('ö', 'o').Replace('ğ', 'g');
        return normalized is "all" or "tum" or "tum bilgiler" or "butun" or "butun bilgiler";
    }

    private static string NormalizeField(string value)
    {
        var normalized = value.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"))
            .Replace('ü', 'u').Replace('ı', 'i').Replace('ş', 's').Replace('ç', 'c').Replace('ö', 'o').Replace('ğ', 'g')
            .Replace(' ', '_');
        return normalized switch
        {
            "product_name" or "medicine_name" or "urun_adi" or "ilac_adi" => "name",
            "manufacturer" or "manufacturer_name" or "uretici" or "uretici_firma" or "uretici_firmasi" => "company",
            "warehouse_price" or "depocu_satis" or "depocu_satis_fiyati" => "purchase_price",
            "aktiflik" or "aktiflik_durumu" or "piyasa_durumu" => "is_on_market",
            "kayitli_satis_fiyati" or "satis_fiyati" => "sale_price",
            "raf_stoku" or "raf_stok_miktari" => "shelf_stock_quantity",
            "bos_birakilmis_alanlar" or "bos_alanlar" => "missing_fields",
            _ => normalized
        };
    }
}

public sealed class GetLiveBarcodePriceWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "canli_barkod_fiyati_getir", "Canlı barkod fiyatı ve kampanyası getir",
        "Gets current depot-by-depot barcode prices and campaign offers. Queue ilac_barkod_eslestir first for medicine names.",
        RequiredTools: ["depot.get_live_product_offers"], Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "One or more exact barcodes.", ["depots"] = "Optional depot identifiers; empty means all configured depots."
        });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        const string tool = "depot.get_live_product_offers";
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Canlı fiyat için barkod seçimi gerekiyor.");
        var execution = await WorkflowData.Execute(context, tool, new
        {
            barcodes,
            depots = WorkflowData.List(context, "depots")
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Canlı depo fiyatları ve kampanyaları alınamadı.", new { tool, status = execution.Status.ToString(), execution.SafeMessage });
        var rows = execution.Output.TryGetProperty("offers", out var offers) && offers.ValueKind == JsonValueKind.Array
            ? offers.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).Select(WorkflowData.FlatRow).ToArray()
            : [];
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            rows.Length == 0 ? "Güncel depo teklifi bulunamadı." : $"{rows.Length} güncel depo teklifi bulundu.",
            WorkflowData.RowsOutput(rows));
    }
}

public sealed class ProductPriceHistoryWorkflow : IAiWorkflow
{
    private const string Tool = "product.get_price_history";
    public AiWorkflowDefinition Definition { get; } = new(
        "fiyat_gecmisini_gor", "Fiyat geçmişini gör",
        "Shows recorded local price history for one or more barcodes. Queue ilac_barkod_eslestir first for names and tabloda_goster for a table.",
        RequiredTools: [Tool], Parameters: new Dictionary<string, string> { ["barcodes"] = "Barcode list.", ["limit"] = "Points per barcode, 1-500." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Fiyat geçmişi için barkod seçimi gerekiyor.");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var barcode in barcodes)
        {
            var execution = await WorkflowData.Execute(context, Tool, new { barcode, offset = 0, limit = WorkflowData.Integer(context, "limit", 100, 1, 500) }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(Tool);
            if (!execution.IsSuccess) return WorkflowData.Failed("Fiyat geçmişi alınamadı.");
            if (!execution.Output.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array) continue;
            rows.AddRange(points.EnumerateArray().Select(point => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["barcode"] = barcode, ["day"] = WorkflowData.Text(point, "effective_date"),
                ["sale_price"] = WorkflowData.Text(point, "sale_price"), ["warehouse_price"] = WorkflowData.Text(point, "warehouse_price"),
                ["manufacturer_price"] = WorkflowData.Text(point, "manufacturer_price"), ["public_price"] = WorkflowData.Text(point, "public_price")
            }));
        }
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, $"{rows.Count} fiyat geçmişi kaydı bulundu.", WorkflowData.RowsOutput(rows));
    }
}

public sealed class StockAnalysisWorkflow : IAiWorkflow
{
    private const string Tool = "product.get_analysis";
    private const string ProductTool = "product.get";
    public AiWorkflowDefinition Definition { get; } = new(
        "stok_analizini_gor", "Stok analizini gör",
        "Gets target, missing, shelf and consigned stock plus the incoming/outgoing daily series for selected barcodes.",
        RequiredTools: [Tool, ProductTool], Parameters: new Dictionary<string, string> { ["barcodes"] = "Barcode list.", ["days"] = "Recent days, 1-365." });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var barcodes = WorkflowData.Barcodes(context);
        if (barcodes.Count == 0) return WorkflowData.Failed("Stok analizi için barkod seçimi gerekiyor.");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var barcode in barcodes)
        {
            var execution = await WorkflowData.Execute(context, Tool, new { barcode, days = WorkflowData.Integer(context, "days", 90, 1, 365), outgoing_type_code = (string?)null, incoming_type_code = (string?)null }, cancellationToken);
            if (execution is null) return WorkflowData.Missing(Tool);
            var productExecution = await WorkflowData.Execute(context, ProductTool, new { barcode }, cancellationToken);
            if (productExecution is null) return WorkflowData.Missing(ProductTool);
            if (!execution.IsSuccess || !productExecution.IsSuccess) return WorkflowData.Failed("Stok analizi alınamadı.");
            if (!execution.Output.TryGetProperty("found", out var found) || !found.GetBoolean()) continue;
            rows.Add(new Dictionary<string, string>
            {
                ["medicine_name"] = ProductName(productExecution.Output, context, barcode),
                ["barcode"] = barcode, ["average_monthly_sales"] = DecimalText(execution.Output, "average_monthly_sales"),
                ["target_stock_quantity"] = DecimalText(execution.Output, "target_stock_quantity"),
                ["missing_stock_quantity"] = DecimalText(execution.Output, "missing_stock_quantity"),
                ["shelf_stock_quantity"] = DecimalText(execution.Output, "shelf_stock_quantity"),
                ["consigned_stock_quantity"] = DecimalText(execution.Output, "consigned_stock_quantity"),
                ["stock_comment"] = WorkflowData.Text(execution.Output, "stock_comment"),
                ["estimated_coverage_days"] = CoverageDays(execution.Output)
            });
        }
        var highlightField = RequestedHighlightField(context.EffectiveRequest);
        var message = Summary(rows, highlightField);
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed, message,
            WorkflowData.RowsOutput(rows, new
            {
                visible_columns = new[]
                {
                    "medicine_name", "barcode", "missing_stock_quantity", "target_stock_quantity",
                    "shelf_stock_quantity", "average_monthly_sales", "consigned_stock_quantity",
                    "estimated_coverage_days", "stock_comment"
                },
                highlight_field = highlightField
            }));
    }

    private static string Summary(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string highlightField)
    {
        if (rows.Count == 0) return "Seçili ürün için stok bilgisi bulunamadı.";
        if (highlightField == "missing_stock_quantity")
        {
            var total = rows.Sum(row => decimal.TryParse(row.GetValueOrDefault(highlightField), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var value) ? value : 0m);
            var formatted = total.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR"));
            return rows.Count == 1
                ? $"{rows[0].GetValueOrDefault("medicine_name", "Seçili ürün")} için eksik stok: {formatted} kutu."
                : $"{rows.Count} ürün için toplam eksik stok: {formatted} kutu; ayrıntılar tabloda gösteriliyor.";
        }
        return rows.Count == 1
            ? $"{rows[0].GetValueOrDefault("medicine_name", "Seçili ürün")} için raf stoku: {rows[0].GetValueOrDefault("shelf_stock_quantity", "0")} kutu; tahmini stok yeterlilik süresi: {rows[0].GetValueOrDefault("estimated_coverage_days", "hesaplanamadı")} gün."
            : $"{rows.Count} ürün için stok analizi hazırlandı.";
    }

    private static string RequestedHighlightField(string request)
    {
        var value = request.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        if (value.Contains("eksik", StringComparison.Ordinal) || value.Contains("eksiğ", StringComparison.Ordinal) ||
            value.Contains("eksig", StringComparison.Ordinal)) return "missing_stock_quantity";
        if (value.Contains("kaç gün", StringComparison.Ordinal) || value.Contains("kac gun", StringComparison.Ordinal)) return "estimated_coverage_days";
        if (value.Contains("hedef", StringComparison.Ordinal)) return "target_stock_quantity";
        if (value.Contains("aylık", StringComparison.Ordinal) || value.Contains("aylik", StringComparison.Ordinal)) return "average_monthly_sales";
        if (value.Contains("yolda", StringComparison.Ordinal)) return "consigned_stock_quantity";
        return "shelf_stock_quantity";
    }

    private static string DecimalText(JsonElement output, string propertyName) =>
        output.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var number)
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : WorkflowData.Text(output, propertyName);

    private static string ProductName(JsonElement output, AiWorkflowContext context, string barcode)
    {
        if (output.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.Object)
        {
            var summary = product.TryGetProperty("summary", out var value) ? value : product;
            var name = WorkflowData.Text(summary, "name");
            if (name.Length > 0) return name;
        }
        return WorkflowData.ProductName(context, barcode);
    }

    private static string CoverageDays(JsonElement output)
    {
        var average = output.TryGetProperty("average_monthly_sales", out var avg) && avg.TryGetDecimal(out var monthly) ? monthly : 0m;
        var stock = output.TryGetProperty("shelf_stock_quantity", out var shelf) && shelf.TryGetDecimal(out var quantity) ? quantity : 0m;
        return average > 0 ? Math.Round(stock / average * 30m, 1).ToString(CultureInfo.InvariantCulture) : "hesaplanamadı";
    }}

public sealed class SalesRevenueSummaryWorkflow : IAiWorkflow
{
    private const string Tool = "sale.aggregate_records";
    public AiWorkflowDefinition Definition { get; } = new(
        "satis_hasilati_ozeti", "Satış hasılatı özeti",
        "Aggregates retail/prescription sales and refunds for the pharmacy or selected barcodes, with date, payment, patient, user, status, debt and grouping filters. Queue ilac_barkod_eslestir first for a named medicine.",
        RequiredTools: [Tool], Parameters: new Dictionary<string, string>
        {
            ["barcodes"] = "Optional barcode list.", ["start_date"] = "Exact inclusive YYYY-MM-DD.",
            ["end_date"] = "Exact inclusive YYYY-MM-DD.", ["period_months"] = "Relative number of months; default 3.",
            ["period_days"] = "Relative number of days.", ["all_history"] = "true when all available sales are requested without a narrower period.", ["sale_types"] = "retail, prescription, or both.",
            ["calendar_period"] = "today, yesterday, this_week, last_week, this_month, last_month, this_year, last_year; not a rolling window.",
            ["include_refunds"] = "true or false.", ["payment_types"] = "Optional payment filters.",
            ["statuses"] = "Optional lifecycle statuses.", ["user_id"] = "Optional user ID. Patient IDs are supplied only by hasta_eslestir.", ["prescription_no"] = "Optional prescription number.",
            ["is_debted"] = "Optional true or false.", ["marked_only"] = "true for marked records only.",
            ["minimum_total"] = "Optional minimum transaction total.", ["maximum_total"] = "Optional maximum transaction total.",
            ["metric"] = "quantity, record_count, net_revenue, gross_revenue, refund_total, or institution_revenue (public/institution fee revenue).",
            ["group_by"] = "none, day, month, sale_type, record_type, product, user, or patient.",
            ["sort_by"] = "net_revenue, gross_revenue, institution_revenue, discount_total, profit, quantity, record_count, or group_key.",
            ["sort_direction"] = "descending or ascending.", ["limit"] = "1-200 groups."
        });
    public async Task<AiWorkflowStepResult> ExecuteAsync(AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var hasPeriod = WorkflowData.Parameter(context, "start_date") is not null ||
                        WorkflowData.Parameter(context, "end_date") is not null ||
                        WorkflowData.Parameter(context, "period_months") is not null ||
                        WorkflowData.Parameter(context, "period_days") is not null ||
                        WorkflowData.Parameter(context, "calendar_period") is not null ||
                        WorkflowData.Boolean(context, "all_history");
        var rangeContext = hasPeriod
            ? context
            : context with
            {
                Parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                {
                    ["period_months"] = "3"
                }
            };
        var (start, end) = WorkflowData.DateRange(rangeContext);
        var barcodes = WorkflowData.Barcodes(context);
        var saleTypes = WorkflowData.SaleTypes(context);
        var requestedGroupBy = WorkflowData.GroupBy(context, "record_type");
        var groupBy = requestedGroupBy == "none" ? "record_type" : requestedGroupBy;
        var metric = RevenueMetric(context);
        var includeRefunds = WorkflowData.NullableBoolean(context, "include_refunds") ?? metric != "quantity";
        var patientRecordId = WorkflowData.PatientRecordId(context);
        var execution = await WorkflowData.Execute(context, Tool, new
        {
            start_date = start,
            end_date = end,
            sale_types = saleTypes,
            include_refunds = includeRefunds,
            barcodes,
            qr_codes = Array.Empty<string>(),
            payment_types = WorkflowData.List(context, "payment_types"),
            statuses = WorkflowData.List(context, "statuses"),
            patient_record_id = patientRecordId,
            user_id = WorkflowData.Parameter(context, "user_id"),
            prescription_no = WorkflowData.Parameter(context, "prescription_no"),
            is_debted = WorkflowData.NullableBoolean(context, "is_debted"),
            marked_only = WorkflowData.Boolean(context, "marked_only"),
            minimum_total = WorkflowData.NullableDecimal(context, "minimum_total"),
            maximum_total = WorkflowData.NullableDecimal(context, "maximum_total"),
            group_by = groupBy,
            sort_by = WorkflowData.SortBy(context, groupBy == "record_type" ? "group_key" : metric),
            sort_direction = WorkflowData.SortDirection(context, groupBy == "record_type" ? "ascending" : "descending"),
            offset = 0,
            limit = WorkflowData.Integer(context, "limit", groupBy == "record_type" ? 2 : 100, 1, 200)
        }, cancellationToken);
        if (execution is null) return WorkflowData.Missing(Tool);
        if (!execution.IsSuccess) return WorkflowData.Failed("Satış hasılatı özeti alınamadı.");
        var groups = execution.Output.TryGetProperty("groups", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).ToArray()
            : [];
        var sale = groups.FirstOrDefault(group => WorkflowData.Text(group, "group_key") == "sale");
        var refund = groups.FirstOrDefault(group => WorkflowData.Text(group, "group_key") == "refund");
        static decimal Number(JsonElement value, string name) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.TryGetDecimal(out var number) ? number : 0m;
        var grossSales = Number(sale, "gross_revenue");
        var refundTotal = Math.Abs(Number(refund, "gross_revenue"));
        var discountTotal = groups.Sum(group => Number(group, "discount_total"));
        var netRevenue = groups.Sum(group => Number(group, "net_revenue"));
        var institutionRevenue = groups.Sum(group => Number(group, "institution_revenue"));
        var productQuantity = groups.Sum(group => Number(group, "quantity"));
        var summaryRow = new Dictionary<string, string>
        {
            ["start_date"] = start, ["end_date_inclusive"] = end,
            ["sale_count"] = groups.Sum(group => (int)Number(group, "sale_count")).ToString(CultureInfo.InvariantCulture),
            ["refund_count"] = groups.Sum(group => (int)Number(group, "refund_count")).ToString(CultureInfo.InvariantCulture),
            ["product_quantity"] = productQuantity.ToString(CultureInfo.InvariantCulture),
            ["gross_sales"] = grossSales.ToString(CultureInfo.InvariantCulture),
            ["discount_total"] = discountTotal.ToString(CultureInfo.InvariantCulture),
            ["refund_total"] = refundTotal.ToString(CultureInfo.InvariantCulture),
            ["net_revenue"] = netRevenue.ToString(CultureInfo.InvariantCulture),
            ["institution_revenue"] = institutionRevenue.ToString(CultureInfo.InvariantCulture)
        };
        var summaryColumns = includeRefunds
            ? new[] { "start_date", "end_date_inclusive", "product_quantity", "sale_count", "refund_count", "gross_sales", "discount_total", "refund_total", "net_revenue", "institution_revenue" }
            : new[] { "start_date", "end_date_inclusive", "product_quantity", "sale_count", "gross_sales", "discount_total", "net_revenue", "institution_revenue" };
        var groupedColumns = new[] { "group_key", "group_label", "record_count", "sale_count", "refund_count", "quantity", "gross_revenue", "discount_total", "net_revenue", "institution_revenue", "entry_cost_total", "profit", "missing_cost_quantity", "average_record_revenue", "average_item_revenue" };
        var rows = groupBy == "record_type"
            ? new IReadOnlyDictionary<string, string>[] { summaryRow }
            : groups.Select(WorkflowData.FlatRow).ToArray();
        var action = new AiWorkflowModuleAction(
            "open_sales_list",
            "Satışlar modülünde aç",
            "sales.retail-list",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["start_date"] = start, ["end_date"] = end, ["barcodes"] = string.Join(',', barcodes),
                ["sale_types"] = string.Join(',', saleTypes),
                ["include_refunds"] = includeRefunds ? "true" : "false",
                ["payment_types"] = string.Join(',', WorkflowData.List(context, "payment_types")),
                ["statuses"] = string.Join(',', WorkflowData.List(context, "statuses")),
                ["prescription_no"] = WorkflowData.Parameter(context, "prescription_no") ?? string.Empty,
                ["user_id"] = WorkflowData.Parameter(context, "user_id") ?? string.Empty,
                ["patient_record_id"] = patientRecordId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["is_debted"] = WorkflowData.Parameter(context, "is_debted") ?? string.Empty,
                ["marked_only"] = WorkflowData.Parameter(context, "marked_only") ?? string.Empty,
                ["minimum_total"] = WorkflowData.Parameter(context, "minimum_total") ?? string.Empty,
                ["maximum_total"] = WorkflowData.Parameter(context, "maximum_total") ?? string.Empty
            });
        var period = $"{DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd.MM.yyyy}–" +
                     $"{DateOnly.ParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd.MM.yyyy}";
        var scopeLabel = saleTypes.Count == 1 && saleTypes[0] == "retail"
            ? "perakende satışlar"
            : saleTypes.Count == 1 && saleTypes[0] == "prescription"
                ? "reçeteli satışlar"
                : "tüm satışlar";
        var message = groupBy != "record_type"
            ? $"{period} dönemi {scopeLabel} için {rows.Length:N0} grup tabloda gösteriliyor."
            : metric == "refund_total"
                ? $"{period} dönemi {scopeLabel} iade toplamı: {summaryRow["refund_total"]} · İade sayısı: {summaryRow["refund_count"]}"
            : metric == "institution_revenue"
            ? barcodes.Count > 0
                ? "Kamu ücreti işlem düzeyinde tutulduğu için seçili ürünlere güvenilir biçimde dağıtılamaz. Ürün hasılatı için net veya brüt hasılatı isteyin."
                : $"{period} dönemi kamu ücreti hasılatı: {summaryRow["institution_revenue"]}."
            : metric == "quantity"
                ? barcodes.Count > 0
                    ? $"Ürün Adedi: {summaryRow["product_quantity"]} kutu · {period} dönemi" +
                      $" · Net hasılat: {summaryRow["net_revenue"]} · Brüt satış: {summaryRow["gross_sales"]}" +
                      (includeRefunds ? $" · İade: {summaryRow["refund_total"]}" : string.Empty)
                    : $"Ürün Adedi: {summaryRow["product_quantity"]} kutu · {period} dönemi · Satış Sayısı: {summaryRow["sale_count"]} · Net hasılat: {summaryRow["net_revenue"]}"
            : metric == "record_count"
                ? $"{period} dönemi · Satış Sayısı: {summaryRow["sale_count"]} · Ürün Adedi: {summaryRow["product_quantity"]} · Net hasılat: {summaryRow["net_revenue"]}"
            : barcodes.Count > 0
                ? includeRefunds
                    ? $"{period} dönemi seçili ürün satırları: {summaryRow["product_quantity"]} kutu · Brüt satış: {summaryRow["gross_sales"]} · İskonto: {summaryRow["discount_total"]} · İade: {summaryRow["refund_total"]} · Net hasılat: {summaryRow["net_revenue"]}"
                    : $"{period} dönemi seçili ürün satırları: {summaryRow["product_quantity"]} kutu · Brüt satış: {summaryRow["gross_sales"]} · İskonto: {summaryRow["discount_total"]} · Net hasılat: {summaryRow["net_revenue"]}"
                : includeRefunds
                    ? $"{period} dönemi {scopeLabel}: Brüt satış {summaryRow["gross_sales"]} · İskonto {summaryRow["discount_total"]} · İade {summaryRow["refund_total"]} · Net hasılat {summaryRow["net_revenue"]}"
                    : $"{period} dönemi {scopeLabel}: Brüt satış {summaryRow["gross_sales"]} · İskonto {summaryRow["discount_total"]} · Net hasılat {summaryRow["net_revenue"]}";
        return new AiWorkflowStepResult(AiWorkflowStepStatus.Completed,
            message,
            WorkflowData.RowsOutput(rows, new
            {
                start_date = start,
                end_date = end,
                barcodes,
                sale_types = saleTypes,
                include_refunds = includeRefunds,
                group_by = groupBy,
                highlight_field = groupBy == "record_type"
                    ? metric switch
                    {
                        "quantity" => "product_quantity",
                        "record_count" => "sale_count",
                        "gross_revenue" => "gross_sales",
                        "refund_total" => "refund_total",
                        "institution_revenue" => "institution_revenue",
                        _ => "net_revenue"
                    }
                    : metric,
                total_count = rows.Length,
                columns = groupBy == "record_type" ? summaryColumns : groupedColumns,
                visible_columns = groupBy != "record_type"
                    ? metric == "institution_revenue"
                        ? new[] { "group_label", "record_count", "institution_revenue" }
                        : new[] { "group_label", "record_count", "quantity", "gross_revenue", "net_revenue" }
                    : metric == "institution_revenue"
                        ? new[] { "start_date", "end_date_inclusive", "sale_count", "institution_revenue" }
                        : barcodes.Count > 0
                            ? includeRefunds
                                ? new[] { "start_date", "end_date_inclusive", "product_quantity", "gross_sales", "discount_total", "refund_total", "net_revenue" }
                                : new[] { "start_date", "end_date_inclusive", "product_quantity", "gross_sales", "discount_total", "net_revenue" }
                            : includeRefunds
                                ? new[] { "start_date", "end_date_inclusive", "sale_count", "refund_count", "gross_sales", "discount_total", "refund_total", "net_revenue" }
                                : new[] { "start_date", "end_date_inclusive", "sale_count", "gross_sales", "discount_total", "net_revenue" }
            }),
            ModuleActions: [action]);
    }

    private static string RevenueMetric(AiWorkflowContext context)
    {
        var requested = WorkflowData.Parameter(context, "metric")?.ToLowerInvariant();
        if (requested is "quantity" or "record_count" or "net_revenue" or "gross_revenue" or "refund_total" or "institution_revenue") return requested;
        var text = context.EffectiveRequest.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        if (text.Contains("kaç tane", StringComparison.Ordinal) || text.Contains("kac tane", StringComparison.Ordinal) ||
            text.Contains("kutu", StringComparison.Ordinal) || text.Contains("ürün adedi", StringComparison.Ordinal) ||
            text.Contains("urun adedi", StringComparison.Ordinal)) return "quantity";
        if (text.Contains("kaç satış", StringComparison.Ordinal) || text.Contains("kac satis", StringComparison.Ordinal) ||
            text.Contains("satış sayısı", StringComparison.Ordinal) || text.Contains("satis sayisi", StringComparison.Ordinal)) return "record_count";
        return text.Contains("kamu ücret", StringComparison.Ordinal) ||
               text.Contains("kamu ucret", StringComparison.Ordinal) ||
               text.Contains("kurum tutar", StringComparison.Ordinal)
            ? "institution_revenue"
            : "net_revenue";
    }
}
