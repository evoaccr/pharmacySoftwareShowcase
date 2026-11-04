using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.Takas;

public enum TakasEntryDirection
{
    Incoming,
    Outgoing
}

public sealed record TakasEntryLaunchOptions(TakasEntryDirection Direction)
{
    public static TakasEntryLaunchOptions Incoming { get; } = new(TakasEntryDirection.Incoming);
    public static TakasEntryLaunchOptions Outgoing { get; } = new(TakasEntryDirection.Outgoing);
    public bool IsIncoming => Direction == TakasEntryDirection.Incoming;
}

public sealed record TakasPharmacySuggestion(
    int RecordId,
    string GlnCode,
    string PharmacyName,
    string PharmacistName,
    decimal DebtBalance)
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    public string DisplayText => $"{PharmacyName} · {GlnCode}";
    public string PharmacistDisplay => string.IsNullOrWhiteSpace(PharmacistName) ? "—" : PharmacistName;
    public string DebtDisplay => DebtBalance.ToString("N2", TurkishCulture) + " ₺";
}

public sealed record TakasStockItem(
    string QrCode,
    string Barcode,
    string ProductName,
    int StockQuantity,
    DateTime? ExpiryDate,
    decimal? EntryCost,
    decimal? WarehousePrice,
    decimal SalePrice,
    decimal? PublicPrice,
    byte[]? FirstPhoto,
    ItsQrProduct ItsProduct)
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    public decimal SuggestedDebtAmount => decimal.Round(
        WarehousePrice ?? (SalePrice > 0 ? SalePrice : PublicPrice ?? 0m),
        2,
        MidpointRounding.AwayFromZero);
    public string ExpiryDisplay => ExpiryDate?.ToString("dd.MM.yyyy") ?? ItsProduct.ExpiryDate.ToString("dd.MM.yyyy");
    public string WarehousePriceDisplay => Money(WarehousePrice);
    public string SalePriceDisplay => Money(SalePrice);
    public string PublicPriceDisplay => Money(PublicPrice);
    public string EntryCostDisplay => Money(EntryCost);

    private static string Money(decimal? value) => value is null
        ? "—"
        : value.Value.ToString("N2", TurkishCulture) + " ₺";
}

public sealed class TakasCartLine : INotifyPropertyChanged
{
    private decimal debtAmount;

    public TakasCartLine(TakasStockItem item)
    {
        Item = item;
        debtAmount = item.SuggestedDebtAmount;
    }

    public TakasStockItem Item { get; }
    public string QrCode => Item.QrCode;
    public string Barcode => Item.Barcode;
    public string ProductName => Item.ProductName;
    public string SerialNumber => Item.ItsProduct.SerialNumber;
    public string ExpiryDisplay => Item.ExpiryDisplay;
    public string EntryCostDisplay => Item.EntryCostDisplay;
    public string DebtAmountDisplay => DebtAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";

    public decimal DebtAmount
    {
        get => debtAmount;
        set
        {
            var normalized = decimal.Round(Math.Max(0m, value), 2, MidpointRounding.AwayFromZero);
            if (debtAmount == normalized) return;
            debtAmount = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DebtAmountDisplay));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TakasGroup : INotifyPropertyChanged
{
    private bool isExpanded;

    public TakasGroup(TakasCartLine firstLine)
    {
        Entries.Add(firstLine);
        firstLine.PropertyChanged += OnLineChanged;
    }

    public ObservableCollection<TakasCartLine> Entries { get; } = [];
    public TakasStockItem Representative => Entries[0].Item;
    public string ProductName => Representative.ProductName;
    public string Barcode => Representative.Barcode;
    public string StockDisplay => Representative.StockQuantity.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
    public int Quantity => Entries.Count;
    public string UnitCostDisplay => Representative.EntryCostDisplay;
    public decimal TotalDebt => Entries.Sum(line => line.DebtAmount);
    public string UnitDebtDisplay
    {
        get
        {
            if (Entries.Count == 0) return "—";
            var first = Entries[0].DebtAmount;
            return Entries.All(line => line.DebtAmount == first)
                ? first.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺"
                : "Farklı";
        }
    }
    public string TotalDebtDisplay => TotalDebt.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value) return;
            isExpanded = value;
            OnPropertyChanged();
        }
    }

    public void Add(TakasCartLine line)
    {
        Entries.Add(line);
        line.PropertyChanged += OnLineChanged;
        RefreshTotals();
    }

    public void Remove(TakasCartLine line)
    {
        line.PropertyChanged -= OnLineChanged;
        Entries.Remove(line);
        RefreshTotals();
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TakasCartLine.DebtAmount)) RefreshTotals();
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(UnitDebtDisplay));
        OnPropertyChanged(nameof(TotalDebt));
        OnPropertyChanged(nameof(TotalDebtDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TakasCart
{
    public ObservableCollection<TakasGroup> Groups { get; } = [];
    public IReadOnlyList<TakasCartLine> Lines => Groups.SelectMany(group => group.Entries).ToArray();
    public int ItemCount => Groups.Sum(group => group.Quantity);
    public decimal TotalDebt => Groups.Sum(group => group.TotalDebt);

    public bool Contains(string qrCode) => Lines.Any(line => line.QrCode == qrCode);
    public TakasCartLine Add(TakasStockItem item)
    {
        var line = new TakasCartLine(item);
        var group = Groups.FirstOrDefault(candidate => candidate.Barcode == item.Barcode);
        if (group is null) Groups.Add(new TakasGroup(line));
        else group.Add(line);
        return line;
    }

    public void Remove(TakasCartLine line)
    {
        var group = Groups.FirstOrDefault(candidate => candidate.Entries.Contains(line));
        if (group is null) return;
        group.Remove(line);
        if (group.Entries.Count == 0) Groups.Remove(group);
    }

    public void Remove(TakasGroup group)
    {
        foreach (var line in group.Entries.ToArray()) group.Remove(line);
        Groups.Remove(group);
    }

    public void Clear()
    {
        foreach (var group in Groups.ToArray()) Remove(group);
    }
}

public sealed record TakasLineInput(TakasStockItem Item, decimal DebtAmount);

public sealed record TakasFinalizeResult(
    int TakasId,
    int SuccessfulItemCount,
    int FailedItemCount,
    decimal AppliedDebtAmount,
    string? NotificationId,
    string Status);

public sealed record TakasCancellationPreparation(
    int TakasId,
    string SourceGln,
    string DestinationGln,
    IReadOnlyList<ItsQrProduct> Products);

public sealed record TakasCancellationResult(
    int TakasId,
    int CancelledItemCount,
    int FailedItemCount,
    decimal ReversedDebtAmount,
    string? NotificationId,
    string Status);
