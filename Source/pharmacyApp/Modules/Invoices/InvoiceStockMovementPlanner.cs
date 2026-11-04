namespace pharmacyApp.Modules.Invoices;

internal sealed record InvoiceStockLine(
    int RecordId,
    int LineNumber,
    string ItemName,
    string Barcode,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? DiscountAmount,
    decimal? LineAmount,
    string TrackingType,
    string Warning,
    IReadOnlyList<string> QrCodes);

internal sealed record InvoiceStockMovementItem(
    int LineId,
    string Code,
    bool IsSerialized,
    decimal? UnitCost);

internal static class InvoiceStockMovementPlanner
{
    public static IReadOnlyList<InvoiceStockMovementItem> Build(IEnumerable<InvoiceStockLine> lines)
    {
        var movements = new List<InvoiceStockMovementItem>();
        foreach (var line in lines)
        {
            if (line.TrackingType == "QR")
            {
                if (line.Warning.Length > 0)
                    throw new InvalidOperationException($"{line.LineNumber}. satır ({line.ItemName}): {line.Warning}");
                var quantity = RequiredWholeQuantity(line);
                if (line.QrCodes.Count != quantity)
                    throw new InvalidOperationException(
                        $"{line.LineNumber}. satırda {quantity:N0} ürün için {line.QrCodes.Count:N0} karekod var.");
                movements.AddRange(line.QrCodes.Select(code =>
                    new InvoiceStockMovementItem(line.RecordId, code, true, UnitCost(line))));
                continue;
            }

            if (line.TrackingType != "BARCODE") continue;
            if (string.IsNullOrWhiteSpace(line.Barcode))
                throw new InvalidOperationException($"{line.LineNumber}. barkodlu satırın barkodu bulunamadı.");
            var barcodeQuantity = RequiredWholeQuantity(line);
            for (var index = 0; index < barcodeQuantity; index++)
                movements.Add(new InvoiceStockMovementItem(line.RecordId, line.Barcode, false, UnitCost(line)));
        }
        return movements;
    }

    private static int RequiredWholeQuantity(InvoiceStockLine line)
    {
        if (line.Quantity is null || line.Quantity <= 0 || decimal.Truncate(line.Quantity.Value) != line.Quantity.Value
                                  || line.Quantity > int.MaxValue)
            throw new InvalidOperationException($"{line.LineNumber}. satırın stok miktarı pozitif tam sayı olmalıdır.");
        return decimal.ToInt32(line.Quantity.Value);
    }

    private static decimal? UnitCost(InvoiceStockLine line)
    {
        if (line.Quantity is null || line.Quantity <= 0) return line.UnitPrice;
        if (line.LineAmount is not null)
            return decimal.Round(line.LineAmount.Value / line.Quantity.Value, 4, MidpointRounding.AwayFromZero);
        if (line.UnitPrice is null) return null;
        var total = line.UnitPrice.Value * line.Quantity.Value - (line.DiscountAmount ?? 0m);
        return decimal.Round(Math.Max(total, 0m) / line.Quantity.Value, 4, MidpointRounding.AwayFromZero);
    }
}
