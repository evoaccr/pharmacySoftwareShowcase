using System.Globalization;

namespace pharmacyApp.Modules.Products;

public static class ProductAnalysisCalculations
{
    public const decimal DefaultWantedStockMonths = 1.5m;

    public static decimal AverageMonthlySales(int salesInLastNinetyDays) =>
        Math.Max(0, salesInLastNinetyDays) / 3m;

    public static decimal MissingStock(decimal averageMonthlySales, decimal wantedStockMonths, int shelfStock) =>
        decimal.Ceiling(averageMonthlySales * wantedStockMonths - Math.Max(0, shelfStock));

    public static string StockComment(decimal missingStock) => missingStock switch
    {
        < -3m => "İade Edebilirsin",
        < 0m => "stok fazlası",
        0m => "stok yeterli",
        < 3m => "alma",
        _ => "al"
    };

    public static bool IsValidWantedStockMonths(decimal value) =>
        value is >= 0.5m and <= 60m && value * 2m == decimal.Truncate(value * 2m);
}

public sealed record ProductAnalysisMovementType(string? Code, string DisplayName, bool IsEntry);

public sealed record ProductAnalysisMovementDay(
    DateTime Day,
    IReadOnlyDictionary<string, int> Quantities)
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    public string MonthDisplay => Day.ToString("MMM yy", CultureInfo.GetCultureInfo("en-GB"));
}

public sealed record ProductAnalysisData(
    decimal AverageMonthlySales,
    decimal WantedStockMonths,
    int ShelfStockQuantity,
    int ConsignedStockQuantity,
    IReadOnlyList<ProductAnalysisMovementType> MovementTypes,
    IReadOnlyList<ProductAnalysisMovementDay> DailyMovements)
{
    public decimal TargetStockQuantity => AverageMonthlySales * WantedStockMonths;
    public decimal MissingStockQuantity => ProductAnalysisCalculations.MissingStock(
        AverageMonthlySales, WantedStockMonths, ShelfStockQuantity);
    public string StockComment => pharmacyApp.Localization.UiText.Translate(ProductAnalysisCalculations.StockComment(MissingStockQuantity));

    public string AverageMonthlySalesDisplay => $"{AverageMonthlySales:N1} packs";
    public string WantedStockMonthsDisplay => $"{WantedStockMonths:N1} months";
    public string TargetStockDisplay => $"{decimal.Ceiling(TargetStockQuantity):N0} packs";
    public string MissingStockDisplay => $"{MissingStockQuantity:N0} packs";
    public string ShelfStockDisplay => $"{ShelfStockQuantity:N0} packs";
    public string ConsignedStockDisplay => $"{ConsignedStockQuantity:N0} packs";
    public string NotImplementedDisplay => "Not implemented";

    public int QuantityFor(ProductAnalysisMovementDay day, ProductAnalysisMovementType? selection)
    {
        if (selection is null) return 0;
        if (selection.Code is not null)
            return day.Quantities.TryGetValue(selection.Code, out var quantity) ? quantity : 0;

        var codes = MovementTypes
            .Where(type => type.IsEntry == selection.IsEntry && type.Code is not null)
            .Select(type => type.Code!);
        return codes.Sum(code => day.Quantities.TryGetValue(code, out var quantity) ? quantity : 0);
    }
}

public sealed record ProductAnalysisSettingItem(
    int RecordId,
    string DisplayName,
    string Barcode,
    decimal? WantedStockMonths)
{
    public string SelectionDisplay => $"{DisplayName} — {Barcode}";
    public string WantedStockMonthsDisplay => WantedStockMonths.HasValue
        ? $"{WantedStockMonths:N1} months"
        : "Default setting";
}
