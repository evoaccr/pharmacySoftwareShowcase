using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.Modules.Medula;

public sealed record MedulaPrescriptionHistoryEntry(
    Guid PrescriptionUid,
    int VersionNumber,
    string ActionType,
    DateTime UpdateDateTime,
    string UserId,
    string Changes,
    bool IsCurrent,
    MedulaPrescriptionSnapshot Snapshot)
{
    public string VersionDisplay => $"Sürüm {VersionNumber}";
    public string DateDisplay => UpdateDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("tr-TR"));
    public string UserDisplay => string.IsNullOrWhiteSpace(UserId) ? "—" : UserId.Trim();
    public string ActionDisplay => ActionType switch
    {
        "FIRST_SAVE" => "İlk Kayıt",
        "UPDATE" => "Güncelleme",
        "DELETE" => "Silme",
        "PERMANENT_DELETE" => "Kalıcı Silme",
        _ => ActionType
    };
}

public static class MedulaPrescriptionHistoryFormatter
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["medula_prescription_no"] = "Reçete No",
        ["e_prescription_no"] = "E-Reçete No",
        ["qr_status_code"] = "Karekod Durumu",
        ["invoice_type_code"] = "Fatura Türü",
        ["prescription_type_code"] = "Reçete Türü",
        ["prescription_subtype_code"] = "Reçete Alt Türü",
        ["provision_type_code"] = "Provizyon Türü",
        ["protocol_no"] = "Protokol No",
        ["facility_code"] = "Tesis Kodu",
        ["prescription_date"] = "Reçete Tarihi",
        ["medication_purchase_date"] = "İlaç Alım Tarihi",
        ["has_qr_codes"] = "Karekodlu",
        ["is_sequential_distribution"] = "Sıralı Dağıtım",
        ["patient_tc_identity_no"] = "Reçete Sahibi T.C.",
        ["patient_type"] = "Hasta Türü",
        ["patient_insurance_type"] = "Sigortalılık Türü",
        ["patient_coverage_type"] = "Kapsam",
        ["recipient_tc_identity_no"] = "Teslim Alan T.C.",
        ["recipient_relationship"] = "Yakınlık",
        ["doctor_diploma_registration_no"] = "Doktor Dip. Tesc. No",
        ["doctor_branch_id"] = "Branş",
        ["doctor_certificate"] = "Sertifika",
        ["registered_content"] = "İlaç Bilgileri",
        ["final_content"] = "Son İlaç Bilgileri",
        ["icd10_diagnoses"] = "ICD-10 Tanıları",
        ["prescription_note"] = "Reçete Notu",
        ["prescription_copayment_cash"] = "Reçete Katılım Payı (Elden)",
        ["prescription_copayment_salary"] = "Reçete Katılım Payı (Maaştan)",
        ["examination_copayment_cash"] = "Muayene Katılım Payı (Elden)",
        ["examination_copayment_salary"] = "Muayene Katılım Payı (Maaştan)",
        ["medication_copayment"] = "İlaç Katılım Payı",
        ["equivalent_price_difference"] = "Fiyat Farkı",
        ["institution_total_payment"] = "Kurum Ödemesi",
        ["patient_total_payment"] = "Hasta Ödemesi",
        ["prescription_total"] = "Reçete Toplamı",
        ["list_status_code"] = "Tür",
        ["list_barcode_status"] = "Barkod Durumu",
        ["list_medical_report"] = "Rapor Durumu",
        ["list_document_status"] = "Doküman Durumu",
        ["qr_codes"] = "Karekodlar",
        ["is_deleted"] = "Silinme Durumu"
    };

    public static string Describe(string actionType, string snapshotJson, string? previousSnapshotJson)
    {
        if (actionType.Equals("FIRST_SAVE", StringComparison.OrdinalIgnoreCase))
            return "Reçete ilk kez kaydedildi.";
        if (actionType.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            return "Reçete silindi.";
        if (actionType.Equals("PERMANENT_DELETE", StringComparison.OrdinalIgnoreCase))
            return "Reçete aktif listeden kalıcı olarak kaldırıldı.";
        if (string.IsNullOrWhiteSpace(previousSnapshotJson))
            return "Reçete kaydı oluşturuldu.";

        using var current = JsonDocument.Parse(snapshotJson);
        using var previous = JsonDocument.Parse(previousSnapshotJson);
        var changes = new List<string>();
        foreach (var (key, label) in Labels)
        {
            var currentValue = ReadValue(current.RootElement, key);
            var previousValue = ReadValue(previous.RootElement, key);
            if (string.Equals(currentValue, previousValue, StringComparison.Ordinal)) continue;
            changes.Add(IsCollection(current.RootElement, key) || IsCollection(previous.RootElement, key)
                ? $"{label} değiştirildi"
                : $"{label}: {Display(key, previousValue)} → {Display(key, currentValue)}");
        }

        if (changes.Count == 0) return "Teknik kayıt bilgileri güncellendi.";
        const int visibleChangeCount = 8;
        var text = string.Join("\n", changes.Take(visibleChangeCount).Select(change => $"• {change}"));
        return changes.Count <= visibleChangeCount
            ? text
            : $"{text}\n• +{changes.Count - visibleChangeCount} alan daha";
    }

    private static bool IsCollection(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.Array or JsonValueKind.Object;

    private static string ReadValue(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.True => "Evet",
            JsonValueKind.False => "Hayır",
            JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
            _ => value.ToString().Trim()
        };
    }

    private static string Display(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        if (!short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) return value;
        return key switch
        {
            "qr_status_code" => MedulaPrescriptionCodebook.QrStatusText(code),
            "invoice_type_code" => MedulaPrescriptionCodebook.InvoiceDisplayName(code),
            "prescription_type_code" => MedulaPrescriptionCodebook.PrescriptionTypeName(code),
            "prescription_subtype_code" => MedulaPrescriptionCodebook.PrescriptionSubtypeName(code),
            "provision_type_code" => MedulaPrescriptionCodebook.ProvisionTypeName(code),
            _ => value
        };
    }
}
