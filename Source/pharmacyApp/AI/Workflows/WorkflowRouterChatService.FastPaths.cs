using System.Text.RegularExpressions;

namespace pharmacyApp.AI.Workflows;

public sealed partial class WorkflowRouterChatService
{
    private static bool IsSimplePrescriptionSearchRequest(string text, bool continuation)
    {
        // A deterministic shortcut is safe only when every word is represented by
        // its supported filters. Rich requests belong to the router, not a partial
        // keyword match that silently drops the patient's requested constraints.
        const string prescription = @"\brecete(?:ler|leri|lerini|si|sini)?\b";
        if (!continuation && !Regex.IsMatch(text, prescription, RegexOptions.CultureInvariant)) return false;
        var remainder = text.Replace("sonalndir", "sonlandir", StringComparison.Ordinal);
        var dates = Regex.Matches(remainder,
            @"\b(?:bu\s+hafta(?:ki)?|bugun(?:ku)?|bu\s+ay(?:ki)?|son\s+[0-9]{1,4}\s+gun(?:luk)?)\b",
            RegexOptions.CultureInvariant);
        if (dates.Count > 1 || dates.Count == 1 && ResolvePrescriptionDateRange(dates[0].Value) is null) return false;
        foreach (Match date in dates) remainder = remainder.Replace(date.Value, " ", StringComparison.Ordinal);

        // Resolve the existing A/B/C code vocabulary, but do not consume unrelated
        // 'group' requests (e.g. product ABC analysis) or unsupported invoice groups.
        if (ResolvePrescriptionInvoiceTypeCodes(text) is not null)
            remainder = Regex.Replace(remainder,
                @"\b[abc](?:\s+(?:fatura\s+)?grubu)?(?:\s*,?\s*(?:ve\s+)?[abc](?:\s+(?:fatura\s+)?grubu)?)*(?:\s+gruplari)?\b",
                " ", RegexOptions.CultureInvariant);

        // A continuation currently changes only dates/invoice groups. A new status
        // is intentionally not consumed, so it cannot accidentally retain the old one.
        if (!continuation)
        {
            var statuses = Regex.Matches(remainder,
                @"\b(?:sonlandirilmamis|sonlandirilmadi|bitmemis|tamamlanmamis|sonlandirilmis|sonlandirildi|tamamlanmis)\b",
                RegexOptions.CultureInvariant);
            if (statuses.Count > 1) return false;
            foreach (Match status in statuses) remainder = remainder.Replace(status.Value, " ", StringComparison.Ordinal);
        }
        remainder = Regex.Replace(remainder, prescription, " ", RegexOptions.CultureInvariant);
        remainder = Regex.Replace(remainder,
            @"\b(?:goster|getir|listele|lutfen|icin)\b|\bkontrol\s+et\b", " ", RegexOptions.CultureInvariant);
        return !Regex.IsMatch(remainder, @"[\p{L}\p{N}]", RegexOptions.CultureInvariant);
    }

    private static bool IsSimpleRememberedLocalPrescriptionRequest(string text) => Regex.IsMatch(text,
        @"^\s*(?:(?:bu|ayni)\s+hastanin\s+)?(?:veritabanindaki|yerel|lokal)\s+" +
        @"recete(?:ler|leri|lerini|si|sini)?(?:\s+(?:goster|getir|listele))?\s*[?.!]*\s*$",
        RegexOptions.CultureInvariant);
}
