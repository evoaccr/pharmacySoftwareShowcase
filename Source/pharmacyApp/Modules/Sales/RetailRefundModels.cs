using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace pharmacyApp.Modules.Sales;

public sealed record RetailRefundLaunchOptions(int? InitialSaleId = null);

public sealed record RetailRefundItem(
    string QrCode,
    string Barcode,
    int OriginalSaleId,
    PatientSuggestion? OriginalPatient,
    int ProductRecordId,
    string ProductName,
    decimal GrossPrice,
    decimal DiscountAmount,
    decimal ExitPrice,
    decimal? EntryCost,
    decimal? WarehousePrice,
    decimal? PublicPrice,
    int StockQuantity,
    string? PrescriptionColor,
    string? Indication,
    byte[]? FirstPhoto)
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    public string ExitPriceDisplay => ExitPrice.ToString("N2", TurkishCulture) + " ₺";
    public string EntryCostDisplay => Money(EntryCost);
    public string DiscountDisplay => Money(DiscountAmount);
    public string WarehousePriceDisplay => Money(WarehousePrice);
    public string PublicPriceDisplay => Money(PublicPrice);
    public string PrescriptionColorDisplay => string.IsNullOrWhiteSpace(PrescriptionColor) ? "—" : PrescriptionColor;
    public string IndicationDisplay => string.IsNullOrWhiteSpace(Indication) ? "—" : Indication;

    private static string Money(decimal? amount) => amount is null ? "—" : amount.Value.ToString("N2", TurkishCulture) + " ₺";
}

public sealed class RetailRefundEntry(RetailRefundItem item) : INotifyPropertyChanged
{
    private int saleGroupIndex = -1;

    public RetailRefundItem Item { get; } = item;
    public string QrCode => Item.QrCode;
    public string ExitPriceDisplay => Item.ExitPriceDisplay;
    public string EntryCostDisplay => Item.EntryCostDisplay;
    public string DiscountDisplay => Item.DiscountDisplay;

    /// <summary>-1 when the cart holds a single origin sale (no highlight needed); otherwise a palette index.</summary>
    public int SaleGroupIndex
    {
        get => saleGroupIndex;
        set
        {
            if (saleGroupIndex == value) return;
            saleGroupIndex = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RetailRefundGroup : INotifyPropertyChanged
{
    private bool isExpanded;

    public RetailRefundGroup(RetailRefundItem firstItem)
    {
        Barcode = firstItem.Barcode;
        ProductRecordId = firstItem.ProductRecordId;
        ProductName = firstItem.ProductName;
        UnitCost = firstItem.WarehousePrice;
        StockQuantity = firstItem.StockQuantity;
        Entries.CollectionChanged += (_, _) => NotifyTotalsChanged();
        Entries.Add(new RetailRefundEntry(firstItem));
    }

    public string Barcode { get; }
    public int ProductRecordId { get; }
    public string ProductName { get; }
    public decimal? UnitCost { get; }
    public int StockQuantity { get; }
    public ObservableCollection<RetailRefundEntry> Entries { get; } = [];
    public int Quantity => Entries.Count;
    public decimal TotalExitPrice => Entries.Sum(entry => entry.Item.ExitPrice);
    public decimal TotalDiscount => Entries.Sum(entry => entry.Item.DiscountAmount);
    public decimal TotalPayable => TotalExitPrice;
    public string StockDisplay => StockQuantity.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
    public string UnitCostDisplay => Money(UnitCost);
    public string TotalExitPriceDisplay => Money(TotalExitPrice);
    public string TotalPayableDisplay => Money(TotalPayable);
    public string TotalDiscountDisplay => Money(TotalDiscount);

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyTotalsChanged()
    {
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(TotalExitPrice));
        OnPropertyChanged(nameof(TotalPayable));
        OnPropertyChanged(nameof(TotalExitPriceDisplay));
        OnPropertyChanged(nameof(TotalPayableDisplay));
        OnPropertyChanged(nameof(TotalDiscount));
        OnPropertyChanged(nameof(TotalDiscountDisplay));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string Money(decimal? amount) => amount is null
        ? "—"
        : amount.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
}

public sealed class RetailRefundCart
{
    public ObservableCollection<RetailRefundGroup> Groups { get; } = [];
    public IReadOnlyList<RetailRefundItem> Items => Groups.SelectMany(group => group.Entries).Select(entry => entry.Item).ToArray();
    public decimal TotalAmount => Items.Sum(item => item.ExitPrice);
    public decimal TotalDiscount => Items.Sum(item => item.DiscountAmount);
    public int ItemCount => Items.Count;

    public void Add(RetailRefundItem item)
    {
        var group = Groups.FirstOrDefault(candidate => candidate.Barcode == item.Barcode);
        if (group is null) Groups.Add(new RetailRefundGroup(item));
        else group.Entries.Add(new RetailRefundEntry(item));
        RecomputeSaleGrouping();
    }

    public bool Contains(string qrCode) => Items.Any(item => item.QrCode == qrCode);

    public void RemoveGroup(RetailRefundGroup group)
    {
        Groups.Remove(group);
        RecomputeSaleGrouping();
    }

    public void RemoveEntry(RetailRefundEntry entry)
    {
        var group = Groups.FirstOrDefault(candidate => candidate.Entries.Contains(entry));
        if (group is null) return;
        group.Entries.Remove(entry);
        if (group.Entries.Count == 0) Groups.Remove(group);
        RecomputeSaleGrouping();
    }

    public void Clear() => Groups.Clear();

    /// <summary>Assigns a stable palette index per distinct origin sale so mixed-origin QR rows can be color-coded; a single origin sale clears the highlight.</summary>
    private void RecomputeSaleGrouping()
    {
        var entries = Groups.SelectMany(group => group.Entries).ToArray();
        var distinctSaleIds = entries.Select(entry => entry.Item.OriginalSaleId).Distinct().ToArray();
        if (distinctSaleIds.Length <= 1)
        {
            foreach (var entry in entries) entry.SaleGroupIndex = -1;
            return;
        }

        var indexBySaleId = distinctSaleIds
            .Select((saleId, index) => (saleId, index))
            .ToDictionary(pair => pair.saleId, pair => pair.index);
        foreach (var entry in entries) entry.SaleGroupIndex = indexBySaleId[entry.Item.OriginalSaleId];
    }
}

public sealed record RetailRefundPatientResolution(bool HasConflict, int? PatientId);

public static class RetailRefundRules
{
    public static decimal CalculateDebtReduction(decimal returnTotal, decimal patientDebt, bool useDebt) =>
        useDebt ? decimal.Round(Math.Min(Math.Max(patientDebt, 0m), Math.Max(returnTotal, 0m)), 2) : 0m;

    public static RetailRefundPatientResolution ResolvePatient(IEnumerable<int?> patientIds)
    {
        var distinct = patientIds.Distinct().ToArray();
        return distinct.Length switch
        {
            0 => new RetailRefundPatientResolution(false, null),
            1 => new RetailRefundPatientResolution(false, distinct[0]),
            _ => new RetailRefundPatientResolution(true, null)
        };
    }

    public static void ValidatePayments(decimal totalAmount, IReadOnlyList<RetailSalePaymentInput> payments)
    {
        if (payments.Any(payment => payment.Kind is not (RetailPaymentKind.Cash or RetailPaymentKind.Credit or RetailPaymentKind.BankTransfer)))
            throw new InvalidOperationException("İadede yalnızca nakit, kart ve havale kullanılabilir.");
        if (payments.Any(payment => payment.Amount < 0))
            throw new InvalidOperationException("İade ödeme tutarları negatif olamaz.");
        if (decimal.Round(payments.Sum(payment => payment.Amount), 2) != decimal.Round(totalAmount, 2))
            throw new InvalidOperationException("Nakit, kart ve havale toplamı ödeme toplamına eşit olmalıdır.");
    }
}
