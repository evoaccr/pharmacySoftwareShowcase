using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace pharmacyApp.Modules.Sales;

[Flags]
public enum RetailTransactionTypes
{
    None = 0,
    RetailSale = 1,
    PrescriptionSale = 2,
    RetailRefund = 4,
    PrescriptionRefund = 8,
    ConsignmentSale = 16,
    ConsignmentCancellation = 32,
    All = RetailSale | PrescriptionSale | RetailRefund | PrescriptionRefund | ConsignmentSale | ConsignmentCancellation
}

public sealed record RetailSalesListLaunchOptions(
    int? InitialSaleId = null,
    DateTime? InitialDate = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string Barcode = "",
    string UserId = "",
    RetailTransactionTypes? TransactionTypes = null,
    bool? IsDebted = null,
    int? PatientId = null,
    decimal? MinimumTotal = null,
    decimal? MaximumTotal = null,
    bool MarkedOnly = false,
    IReadOnlyList<string>? Barcodes = null,
    bool DiscountedOnly = false,
    TimeOnly? StartTime = null,
    TimeOnly? EndTime = null,
    IReadOnlyList<string>? Statuses = null,
    string PrescriptionNo = "",
    IReadOnlyList<string>? QrCodes = null,
    IReadOnlyList<string>? PaymentTypes = null);

public sealed record RetailSalesListQuery(
    DateTime StartDate,
    DateTime EndDateExclusive,
    string QrCode = "",
    string Barcode = "",
    string BrandName = "",
    string UserId = "",
    RetailTransactionTypes TransactionTypes = RetailTransactionTypes.All,
    bool? IsDebted = null,
    int? PatientId = null,
    decimal? MinimumTotal = null,
    decimal? MaximumTotal = null,
    bool MarkedOnly = false,
    IReadOnlyList<string>? Barcodes = null,
    bool DiscountedOnly = false,
    TimeOnly? StartTime = null,
    TimeOnly? EndTime = null,
    IReadOnlyList<string>? Statuses = null,
    string PrescriptionNo = "",
    IReadOnlyList<string>? QrCodes = null,
    IReadOnlyList<string>? PaymentTypes = null);

public sealed record SaleProductSuggestion(
    string Barcode,
    string DisplayName,
    string FilterValue,
    bool IsBrand,
    bool HasStock)
{
    public string DisplayText => IsBrand ? DisplayName : $"{DisplayName} · {Barcode}";
}

public sealed record SaleUserFilterOption(string UserId, string DisplayText);

public sealed class RetailSaleListItem : INotifyPropertyChanged
{
    private bool isExpanded;
    private bool detailsLoaded;
    private bool isMarked;

    public required int SaleId { get; init; }
    public required string ReferenceCode { get; init; }
    public required RetailTransactionTypes TransactionType { get; init; }
    public required int MedicineQuantity { get; init; }
    public int? PatientId { get; init; }
    public string? PatientName { get; init; }
    public string? PrescriptionNo { get; init; }
    public decimal CashAmount { get; init; }
    public decimal CreditAmount { get; init; }
    public decimal TransferAmount { get; init; }
    public decimal DebtAmount { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal InstitutionAmount { get; init; }
    public decimal TotalAmount { get; init; }
    public string? UserName { get; init; }
    public string? Notes { get; init; }
    public string? TransactionStatus { get; init; }
    public required DateTime Timestamp { get; init; }
    public ObservableCollection<RetailSaleProductGroup> Products { get; } = [];
    public bool IsPrescription => TransactionType is RetailTransactionTypes.PrescriptionSale or RetailTransactionTypes.PrescriptionRefund;
    public bool IsRefund => TransactionType is RetailTransactionTypes.RetailRefund or RetailTransactionTypes.PrescriptionRefund;
    public bool IsConsignment => TransactionType is RetailTransactionTypes.ConsignmentSale or RetailTransactionTypes.ConsignmentCancellation;
    public bool IsEntryDirection => TransactionType is RetailTransactionTypes.RetailRefund
        or RetailTransactionTypes.PrescriptionRefund
        or RetailTransactionTypes.ConsignmentCancellation;
    public bool CanResolveConsignment => IsConsignment && string.Equals(TransactionStatus, "reserved", StringComparison.OrdinalIgnoreCase);
    public string TransactionTypeDisplay => TransactionType switch
    {
        RetailTransactionTypes.PrescriptionSale => "Reçeteli Satış",
        RetailTransactionTypes.RetailRefund => "Perakende Satış İade",
        RetailTransactionTypes.PrescriptionRefund => "Reçeteli Satış İade",
        RetailTransactionTypes.ConsignmentSale => "Emanet Satış",
        RetailTransactionTypes.ConsignmentCancellation => "Emanet İptal",
        _ => "Perakende Satış"
    };
    public string PatientDisplay => string.IsNullOrWhiteSpace(PatientName) ? "—" : PatientName;
    public string PrescriptionDisplay => string.IsNullOrWhiteSpace(PrescriptionNo) ? "—" : PrescriptionNo;
    public string UserDisplay => string.IsNullOrWhiteSpace(UserName) ? "—" : UserName;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? "—" : Notes;
    public string TimestampDisplay => Timestamp.ToString("dd.MM.yyyy HH:mm");
    public string CashDisplay => Money(CashAmount);
    public string CreditDisplay => Money(CreditAmount);
    public string TransferDisplay => Money(TransferAmount);
    public string DebtDisplay => Money(DebtAmount);
    public string DiscountDisplay => Money(DiscountAmount);
    public string InstitutionDisplay => Money(InstitutionAmount);
    public string TotalDisplay => Money(TotalAmount);

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

    public bool DetailsLoaded
    {
        get => detailsLoaded;
        set
        {
            if (detailsLoaded == value) return;
            detailsLoaded = value;
            OnPropertyChanged();
        }
    }

    public bool IsMarked
    {
        get => isMarked;
        set
        {
            if (isMarked == value) return;
            isMarked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string Money(decimal amount) =>
        amount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));
}

public sealed class RetailSaleProductGroup : INotifyPropertyChanged
{
    private bool isExpanded;

    public required string Barcode { get; init; }
    public required string ProductName { get; init; }
    public int? ProductRecordId { get; init; }
    public int StockQuantity { get; init; }
    public decimal? UnitCost { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal TotalDiscount => QrCodes.Sum(item => item.DiscountAmount);
    public ObservableCollection<RetailSaleQrEntry> QrCodes { get; } = [];
    public int Quantity => QrCodes.Count;
    public decimal TotalPrice => UnitPrice * Quantity;
    public string StockDisplay => StockQuantity.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
    public string UnitCostDisplay => Money(UnitCost);
    public string UnitPriceDisplay => Money(UnitPrice);
    public string TotalPriceDisplay => Money(TotalPrice);
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string Money(decimal? amount) => amount is null
        ? "—"
        : amount.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
}

public sealed record RetailSaleQrEntry(
    string QrCode,
    DateTime? ExpiryDate,
    decimal? EntryCost,
    decimal DiscountAmount = 0m)
{
    public string ExpiryDateDisplay => ExpiryDate?.ToString("dd.MM.yyyy") ?? "—";
    public string EntryCostDisplay => EntryCost is null
        ? "—"
        : EntryCost.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string DiscountDisplay => DiscountAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
}
