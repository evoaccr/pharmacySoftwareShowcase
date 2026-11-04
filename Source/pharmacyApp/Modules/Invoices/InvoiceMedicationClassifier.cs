namespace pharmacyApp.Modules.Invoices;

public static class InvoiceMedicationClassifier
{
    public static bool HasPharmacyPricingFields(IEnumerable<string> headers)
    {
        var normalized = string.Join(' ', headers).ToUpperInvariant()
            .Replace('İ', 'I').Replace('ı', 'I').Replace('Ş', 'S').Replace('Ğ', 'G').Replace('Ü', 'U').Replace('Ö', 'O').Replace('Ç', 'C');
        return normalized.Contains("DEPOCU FIYAT", StringComparison.Ordinal)
            || normalized.Contains("MF ISK", StringComparison.Ordinal)
            || normalized.Contains("ECZ.ISK", StringComparison.Ordinal)
            || normalized.Contains("ECZ ISK", StringComparison.Ordinal)
            || normalized.Contains("PERAKENDE SATIS FIYAT", StringComparison.Ordinal);
    }

    public static bool HasItsTraceabilityData(IEnumerable<string> values)
    {
        return InvoiceTraceabilityParser.Parse(values).HasTraceabilityMarkers;
    }
}
