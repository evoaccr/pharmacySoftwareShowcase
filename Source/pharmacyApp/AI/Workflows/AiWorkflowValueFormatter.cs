using System.Globalization;

namespace pharmacyApp.AI.Workflows;

/// <summary>Shared English display formatting; original identifiers and stored values remain unchanged.</summary>
public static class AiWorkflowValueFormatter
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static string Format(string key, string? value)
    {
        value ??= string.Empty;
        if (key is "movement_date" or "effective_date" or "day" or "timestamp" or "date"
            or "start_date" or "end_date" or "prescription_date" or "dispensing_date" or "report_date")
        {
            DateTime date;
            // Retain the represented pharmacy wall time, not the workstation's time zone.
            if (value.Length >= 10 && value[4] == '-' && value[7] == '-' &&
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                date = timestamp.DateTime;
            else if (!DateTime.TryParseExact(value, ["dd/MM/yyyy", "dd.MM.yyyy", "dd.MM.yyyy HH:mm"],
                         Turkish, DateTimeStyles.None, out date)) return value;
            return date.ToString(date.TimeOfDay == TimeSpan.Zero ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm", Turkish);
        }
        if (key is "gross_amount" or "discount_amount" or "net_amount" or "entry_cost_total" or "profit"
            or "total_amount" or "institution_amount" or "gross_sales" or "refund_total" or "net_revenue"
            or "gross_revenue" or "institution_revenue" or "discount_total" or "sale_price" or "purchase_price"
            or "public_price" or "public_paid_price" or "stock_value" or "gross_price" or "net_price" or "entry_cost"
            or "transaction_gross_amount" or "transaction_net_amount" or "requested_product_gross_amount" or "requested_product_net_amount"
            && PreviousResultData.TryDecimal(value, out var amount))
            return "TRY " + amount.ToString("N2", Turkish);
        return value switch
        {
            "entry" when key == "direction" => "Entry",
            "exit" when key == "direction" => "Issue",
            "true" when key is "is_on_market" or "is_debted" or "is_marked" => "Yes",
            "false" when key is "is_on_market" or "is_debted" or "is_marked" => "No",
            "retail" when key == "sale_type" => "Retail",
            "prescription" when key == "sale_type" => "Prescription",
            "sale" when key == "record_type" => "Sale",
            "refund" when key == "record_type" => "Return",
            "pending" when key == "status" => "Pending",
            "medula_reported" when key == "status" => "Notified to Medula",
            "completed" when key == "status" => "Completed",
            "reconciliation_required" when key == "status" => "Reconciliation Required",
            "cancelled" when key == "status" => "Cancelled",
            "Maliyet bilgisi eksik" when key == "profit" => "Missing cost information",
            _ => value
        };
    }
}
