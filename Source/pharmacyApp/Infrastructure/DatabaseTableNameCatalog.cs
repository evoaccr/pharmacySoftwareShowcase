using System.Text.RegularExpressions;
using Npgsql;

namespace pharmacyApp.Infrastructure;

public static class DatabaseTableNameCatalog
{
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "addresses_compact", "barcode", "barcodeActive", "barcodeAnalysis", "barcodeCampaign",
        "barcodeCampaignNew", "barcodeNew", "barcodePriceHistory", "barcodePriceHistoryNew",
        "barcodePrices", "barcodePricesNew", "branches", "consignmentSaleItems",
        "consignmentSalePayements", "consignmentSales", "depot", "doctor", "documents", "faculty",
        "faculty_type", "family_group", "farmassist_consignment_import_map",
        "farmassist_e_prescription_list_import_map", "farmassist_family_group_import_map",
        "farmassist_import_map", "farmassist_import_quarantine", "farmassist_invoice_import_map",
        "farmassist_invoice_refund_import_map", "farmassist_invoice_stock_import_map",
        "farmassist_manual_movement_import_map", "farmassist_patient_debt_import_map",
        "farmassist_prescription_import_map", "farmassist_prescription_refund_import_map",
        "farmassist_prescription_sale_import_map", "farmassist_qr_movement_import_map",
        "farmassist_retail_refund_import_map", "farmassist_retail_sale_import_map",
        "farmassist_takas_import_map", "icd10_diagnosis", "importer_marked",
        "invoice_incremental_sync_items", "invoice_incremental_sync_state", "invoice_line_qr_codes",
        "invoice_lines_compact", "invoice_stock_log_invoices", "invoice_stock_logs", "invoices_compact",
        "kurum", "loyal_patient_medication_history", "loyal_patient_profile",
        "loyal_patient_report_history", "loyal_patient_report_sample_run",
        "loyal_patient_report_supply_evidence", "manual_movements", "patient", "patient_debt_log",
        "patient_identity", "payementTypes", "pharmacy", "pharmacy_schema_migration", "prescription",
        "prescription_invoice_type", "prescription_log", "prescription_sale_operations", "qrActive",
        "qrHanged", "qrInactive", "qrMovements", "qrMovementTypes", "retail_its_operation_items",
        "retail_its_operations", "sales", "salesPayements", "salesQrItems", "salesRefundItems",
        "salesRefundPayements", "salesRefunds", "stock_its_operation_items", "stock_its_operations",
        "takas", "takasBarcodeItems", "takasQrItems", "unnotified_its_movements", "users", "warningIcons"
    ];

    public static IReadOnlyDictionary<string, string> Normalize(
        IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var defaultName in Defaults)
        {
            var value = values is not null && values.TryGetValue(defaultName, out var configured)
                ? configured?.Trim() ?? string.Empty
                : defaultName;
            if (!IsValidIdentifier(value)) value = defaultName;
            result[defaultName] = value;
        }
        return result;
    }

    public static bool TryValidate(
        IReadOnlyDictionary<string, string> values,
        out IReadOnlyDictionary<string, string> normalized,
        out string error)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var defaultName in Defaults)
        {
            var value = values.TryGetValue(defaultName, out var configured)
                ? configured.Trim()
                : defaultName;
            if (!IsValidIdentifier(value))
            {
                normalized = new Dictionary<string, string>();
                error = $"'{value}' geçerli bir PostgreSQL tablo adı değildir ({defaultName}).";
                return false;
            }
            result[defaultName] = value;
        }
        normalized = result;
        error = string.Empty;
        return true;
    }

    private static bool IsValidIdentifier(string value) =>
        value.Length is > 0 and <= 63
        && (char.IsLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsLetterOrDigit(character) || character == '_');
}

public static class DatabaseCommand
{
    public static NpgsqlCommand Create(string? commandText, NpgsqlConnection? connection) =>
        new(ResolveSql(commandText), connection);

    public static NpgsqlCommand Create(
        string? commandText,
        NpgsqlConnection? connection,
        NpgsqlTransaction? transaction) =>
        new(ResolveSql(commandText), connection, transaction);

    public static string ResolveSql(string? sql)
        => ResolveSql(sql, ApplicationSettingsStore.Current.DatabaseTableNames);

    public static string ResolveSql(
        string? sql,
        IReadOnlyDictionary<string, string>? configuredTableNames)
    {
        var result = sql ?? string.Empty;
        var mappings = DatabaseTableNameCatalog.Normalize(configuredTableNames);
        foreach (var pair in mappings.Where(pair =>
                     !pair.Key.Equals(pair.Value, StringComparison.Ordinal)))
        {
            var escapedDefault = Regex.Escape(pair.Key);
            var replacement = "public." + new NpgsqlCommandBuilder().QuoteIdentifier(pair.Value);
            result = Regex.Replace(
                result,
                $"(?<![A-Za-z0-9_])public\\s*\\.\\s*(?:\\\"{escapedDefault}\\\"|{escapedDefault})(?![A-Za-z0-9_])",
                replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return result;
    }
}
