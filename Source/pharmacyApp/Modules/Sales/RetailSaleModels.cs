using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace pharmacyApp.Modules.Sales;

public enum RetailSaleMode
{
    Retail,
    ConsignmentCreate,
    ConsignmentSettlement
}

public sealed record RetailSaleLaunchOptions(
    RetailSaleMode Mode = RetailSaleMode.Retail,
    int? ConsignmentSaleId = null)
{
    public static RetailSaleLaunchOptions Consignment => new(RetailSaleMode.ConsignmentCreate);
    public static RetailSaleLaunchOptions SettleConsignment(int recordId) =>
        new(RetailSaleMode.ConsignmentSettlement, recordId);
}

public sealed record PatientSuggestion(
    int RecordId,
    string TcIdentityNo,
    string DisplayName,
    string? PhoneNumber = null,
    decimal DebtAmount = 0m,
    decimal DebtCredit = 0m,
    byte[]? Photo = null)
{
    public string DisplayText => string.IsNullOrWhiteSpace(TcIdentityNo)
        ? DisplayName
        : $"{DisplayName} · {TcIdentityNo}";
    public string PhoneDisplay => string.IsNullOrWhiteSpace(PhoneNumber) ? "—" : PhoneNumber;
    public string DebtDisplay => DebtAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string DebtCreditDisplay => DebtCredit.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));
}

public static class RetailTurkishSearch
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    public static string Normalize(string value) => value.Trim().ToLower(TurkishCulture);
}

public static class CancellationTokenSourceGuard
{
    public static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        if (current is null) return;
        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        current.Dispose();
    }
}

public static class MoneyAmountParts
{
    public static decimal Compose(string wholePart, string decimalPart)
    {
        _ = decimal.TryParse(wholePart, NumberStyles.None, CultureInfo.InvariantCulture, out var whole);
        _ = int.TryParse(decimalPart, NumberStyles.None, CultureInfo.InvariantCulture, out var fraction);
        if (decimalPart.Length == 1) fraction *= 10;
        return Math.Max(0m, whole) + Math.Clamp(fraction, 0, 99) / 100m;
    }

    public static (string Whole, string Decimal) Split(decimal amount)
    {
        var rounded = Math.Round(Math.Max(0m, amount), 2, MidpointRounding.AwayFromZero);
        var whole = decimal.Truncate(rounded).ToString("0", CultureInfo.InvariantCulture);
        var fraction = decimal.ToInt32((rounded - decimal.Truncate(rounded)) * 100m)
            .ToString("00", CultureInfo.InvariantCulture);
        return (whole, fraction);
    }

    public static bool ContainsOnlyDigits(string value) => value.All(character => character is >= '0' and <= '9');
}

public sealed record RetailSaleItem(
    string EnteredCode,
    string Barcode,
    bool IsBarcodeOnly,
    string Status,
    int ProductRecordId,
    string ProductName,
    decimal UnitPrice,
    decimal? WarehousePrice,
    int StockQuantity,
    DateTime? ExpiryDate,
    decimal? EntryCost,
    byte[]? FirstPhoto,
    decimal? PublicPrice = null,
    string? PrescriptionColor = null,
    string? Indication = null)
{
    public string UnitPriceDisplay => UnitPrice.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string WarehousePriceDisplay => WarehousePrice is null
        ? "—"
        : WarehousePrice.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string ExpiryDateDisplay => ExpiryDate?.ToString("dd.MM.yyyy") ?? "—";
    public string EntryCostDisplay => EntryCost is null
        ? "—"
        : EntryCost.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string PublicPriceDisplay => PublicPrice is null
        ? "—"
        : PublicPrice.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string PrescriptionColorDisplay => string.IsNullOrWhiteSpace(PrescriptionColor) ? "—" : PrescriptionColor;
    public string IndicationDisplay => string.IsNullOrWhiteSpace(Indication) ? "—" : Indication;
}

public sealed class RetailSaleEntry(RetailSaleItem item) : INotifyPropertyChanged
{
    private decimal discountAmount;
    private bool isDiscountEditable;

    public RetailSaleItem Item { get; } = item;
    public string Code => Item.EnteredCode;
    public string ExpiryDateDisplay => Item.ExpiryDateDisplay;
    public string EntryCostDisplay => Item.EntryCostDisplay;
    public decimal GrossPrice => Item.UnitPrice;
    public decimal NetExitPrice => decimal.Round(GrossPrice - DiscountAmount, 2, MidpointRounding.AwayFromZero);
    public string DiscountDisplay => DiscountAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";

    public decimal DiscountAmount
    {
        get => discountAmount;
        set
        {
            var normalized = decimal.Round(Math.Clamp(value, 0m, GrossPrice), 2, MidpointRounding.AwayFromZero);
            if (discountAmount == normalized) return;
            discountAmount = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NetExitPrice));
            OnPropertyChanged(nameof(DiscountDisplay));
        }
    }

    public bool IsDiscountEditable
    {
        get => isDiscountEditable;
        set { if (isDiscountEditable == value) return; isDiscountEditable = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RetailSaleGroup : INotifyPropertyChanged
{
    private bool isExpanded;

    public RetailSaleGroup(RetailSaleItem firstItem)
    {
        Barcode = firstItem.Barcode;
        ProductRecordId = firstItem.ProductRecordId;
        ProductName = firstItem.ProductName;
        UnitPrice = firstItem.UnitPrice;
        UnitCost = firstItem.WarehousePrice;
        StockQuantity = firstItem.StockQuantity;
        Entries.CollectionChanged += (_, _) => NotifyTotalsChanged();
        AddEntry(new RetailSaleEntry(firstItem));
    }

    public string Barcode { get; }
    public int ProductRecordId { get; }
    public string ProductName { get; }
    public decimal UnitPrice { get; }
    public decimal? UnitCost { get; }
    public int StockQuantity { get; }
    public ObservableCollection<RetailSaleEntry> Entries { get; } = [];
    public int Quantity => Entries.Count;
    public decimal TotalPrice => UnitPrice * Quantity;
    public decimal TotalDiscount => Entries.Sum(entry => entry.DiscountAmount);
    public decimal EditableDiscountAmount
    {
        get => Entries.Count == 1 ? Entries[0].DiscountAmount : TotalDiscount;
        set { if (Entries.Count == 1) Entries[0].DiscountAmount = value; }
    }
    public bool IsDiscountEditable { get; private set; }
    public string UnitPriceDisplay => UnitPrice.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string UnitCostDisplay => UnitCost is null
        ? "—"
        : UnitCost.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string StockDisplay => StockQuantity.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
    public string TotalPriceDisplay => TotalPrice.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string TotalDiscountDisplay => TotalDiscount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";

    public void AddEntry(RetailSaleEntry entry)
    {
        entry.PropertyChanged += OnEntryPropertyChanged;
        Entries.Add(entry);
    }

    public void SetDiscountEditing(bool enabled)
    {
        IsDiscountEditable = enabled && Quantity == 1;
        foreach (var entry in Entries) entry.IsDiscountEditable = enabled && Quantity > 1;
        OnPropertyChanged(nameof(IsDiscountEditable));
    }

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
        OnPropertyChanged(nameof(TotalPrice));
        OnPropertyChanged(nameof(TotalPriceDisplay));
        OnPropertyChanged(nameof(TotalDiscount));
        OnPropertyChanged(nameof(TotalDiscountDisplay));
        OnPropertyChanged(nameof(EditableDiscountAmount));
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RetailSaleEntry.DiscountAmount)) return;
        OnPropertyChanged(nameof(TotalDiscount));
        OnPropertyChanged(nameof(TotalDiscountDisplay));
        OnPropertyChanged(nameof(EditableDiscountAmount));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RetailSaleCart
{
    public ObservableCollection<RetailSaleGroup> Groups { get; } = [];
    public decimal TotalAmount => Groups.Sum(group => group.TotalPrice);
    public decimal TotalDiscount => Groups.Sum(group => group.TotalDiscount);
    public int ItemCount => Groups.Sum(group => group.Quantity);
    public IReadOnlyList<RetailSaleItem> Items => Groups.SelectMany(group => group.Entries).Select(entry => entry.Item).ToArray();

    public void Add(RetailSaleItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var group = Groups.FirstOrDefault(candidate => candidate.Barcode == item.Barcode);
        if (group is null)
            Groups.Add(new RetailSaleGroup(item));
        else
            group.AddEntry(new RetailSaleEntry(item));
    }

    public bool ContainsSerializedCode(string code) => Groups
        .SelectMany(group => group.Entries)
        .Any(entry => !entry.Item.IsBarcodeOnly && entry.Code == code);

    public int CountForBarcode(string barcode) =>
        Groups.FirstOrDefault(group => group.Barcode == barcode)?.Quantity ?? 0;

    public void RemoveGroup(RetailSaleGroup group) => Groups.Remove(group);

    public void RemoveEntry(RetailSaleEntry entry)
    {
        var group = Groups.FirstOrDefault(candidate => candidate.Entries.Contains(entry));
        if (group is null) return;
        group.Entries.Remove(entry);
        if (group.Entries.Count == 0) Groups.Remove(group);
    }

    public void Clear() => Groups.Clear();

    public void AllocateDiscount(decimal totalDiscount)
    {
        var entries = Groups.SelectMany(group => group.Entries).ToArray();
        if (entries.Length == 0 || TotalAmount <= 0m) return;
        totalDiscount = decimal.Round(Math.Clamp(totalDiscount, 0m, TotalAmount), 2, MidpointRounding.AwayFromZero);
        var remaining = totalDiscount;
        var remainingGross = entries.Sum(entry => entry.GrossPrice);
        for (var index = 0; index < entries.Length; index++)
        {
            var amount = index == entries.Length - 1
                ? remaining
                : decimal.Round(remaining * entries[index].GrossPrice / remainingGross, 2, MidpointRounding.AwayFromZero);
            amount = Math.Min(entries[index].GrossPrice, Math.Max(0m, amount));
            entries[index].DiscountAmount = amount;
            remaining -= entries[index].DiscountAmount;
            remainingGross -= entries[index].GrossPrice;
        }
    }

    public void SetManualDiscountEditing(bool enabled)
    {
        foreach (var group in Groups)
        {
            group.SetDiscountEditing(enabled);
            if (enabled) group.IsExpanded = true;
        }
    }
}

public sealed record RetailSaleLineInput(RetailSaleItem Item, decimal DiscountAmount)
{
    public decimal ExitPrice => decimal.Round(Item.UnitPrice - DiscountAmount, 2, MidpointRounding.AwayFromZero);
}

public enum RetailPaymentKind : short
{
    Cash = 1,
    Credit = 2,
    BankTransfer = 3,
    Debt = 4,
    Discount = 6
}

public sealed record RetailSalePaymentInput(RetailPaymentKind Kind, decimal Amount);

public static class ConsignmentSettlementPayments
{
    public static decimal CollectionTarget(IReadOnlyDictionary<RetailPaymentKind, decimal> originalPayments) =>
        Round(GetAmount(originalPayments, RetailPaymentKind.Debt));

    public static IReadOnlyList<RetailSalePaymentInput> ComposeFinalPayments(
        IReadOnlyDictionary<RetailPaymentKind, decimal> originalPayments,
        IEnumerable<RetailSalePaymentInput> collectionPayments)
    {
        ArgumentNullException.ThrowIfNull(originalPayments);
        ArgumentNullException.ThrowIfNull(collectionPayments);
        var totals = Enum.GetValues<RetailPaymentKind>().ToDictionary(kind => kind, _ => 0m);
        foreach (var payment in collectionPayments)
            totals[payment.Kind] += payment.Amount;
        foreach (var kind in new[] { RetailPaymentKind.Cash, RetailPaymentKind.Credit, RetailPaymentKind.BankTransfer })
            totals[kind] += GetAmount(originalPayments, kind);
        totals[RetailPaymentKind.Debt] = 0m;
        return totals
            .Where(pair => Round(pair.Value) > 0m)
            .Select(pair => new RetailSalePaymentInput(pair.Key, Round(pair.Value)))
            .ToArray();
    }

    private static decimal GetAmount(IReadOnlyDictionary<RetailPaymentKind, decimal> payments, RetailPaymentKind kind) =>
        payments.TryGetValue(kind, out var amount) ? amount : 0m;

    private static decimal Round(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

public static class RetailPatientSearchRules
{
    public static bool IsCompleteTcIdentityNumber(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 11 && trimmed.All(character => character is >= '0' and <= '9');
    }
}

public static class RetailQrCodeNormalizer
{
    public static string Normalize(string code) => code
        .Trim()
        .Replace("]d2", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("(", string.Empty, StringComparison.Ordinal)
        .Replace(")", string.Empty, StringComparison.Ordinal)
        .Replace(((char)29).ToString(), string.Empty, StringComparison.Ordinal);
}

public static class RetailStockStatus
{
    public static bool IsConsigned(string? status) =>
        string.Equals(status?.Trim(), "EMANET", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status?.Trim(), "RESERVED", StringComparison.OrdinalIgnoreCase);
}

public static class RetailSaleMovementAddress
{
    public static string Format(int? patientId) => patientId is null ? "P" : $"P{patientId.Value}";
}

public static class RetailSaleRules
{
    public static void ValidateLineDiscounts(
        IReadOnlyList<RetailSaleLineInput> lines,
        decimal discountPayment,
        bool withDiscount)
    {
        if (lines.Any(line => line.DiscountAmount < 0 || line.DiscountAmount > line.Item.UnitPrice))
            throw new InvalidOperationException("Karekoda ait iskonto, ürünün satış fiyatı aralığında olmalıdır.");
        var lineDiscount = decimal.Round(lines.Sum(line => line.DiscountAmount), 2, MidpointRounding.AwayFromZero);
        if (lineDiscount != decimal.Round(discountPayment, 2, MidpointRounding.AwayFromZero))
            throw new InvalidOperationException("Karekod iskontoları toplamı satış iskontosuna eşit olmalıdır.");
        if (!withDiscount && lineDiscount != 0m)
            throw new InvalidOperationException("Karekod iskontosu için iskonto seçeneği işaretlenmelidir.");
    }

    public static decimal ValidatePayments(
        decimal totalAmount,
        IReadOnlyList<RetailSalePaymentInput> payments,
        bool withDiscount,
        int? patientId)
    {
        ArgumentNullException.ThrowIfNull(payments);
        if (payments.Any(payment => payment.Amount < 0))
            throw new InvalidOperationException("Ödeme tutarları negatif olamaz.");

        var paymentTotal = decimal.Round(payments.Sum(payment => payment.Amount), 2, MidpointRounding.AwayFromZero);
        if (paymentTotal != decimal.Round(totalAmount, 2, MidpointRounding.AwayFromZero))
            throw new InvalidOperationException("Nakit, kart, havale, veresiye ve iskonto toplamı satış toplamına eşit olmalıdır.");
        if (!withDiscount && payments.Any(payment => payment.Kind == RetailPaymentKind.Discount && payment.Amount > 0))
            throw new InvalidOperationException("İskonto ödemesi için iskonto seçeneği işaretlenmelidir.");

        var debtAmount = decimal.Round(
            payments.Where(payment => payment.Kind == RetailPaymentKind.Debt).Sum(payment => payment.Amount),
            2,
            MidpointRounding.AwayFromZero);
        if (debtAmount > 0 && patientId is null)
            throw new InvalidOperationException("Veresiye satış için hasta seçilmelidir.");
        return debtAmount;
    }
}

public static class RetailPaymentBalancer
{
    public static void Rebalance(
        decimal totalAmount,
        IReadOnlyList<RetailPaymentKind> selectedKinds,
        IDictionary<RetailPaymentKind, decimal> amounts,
        RetailPaymentKind? editedKind = null)
    {
        foreach (var kind in Enum.GetValues<RetailPaymentKind>())
            if (!selectedKinds.Contains(kind)) amounts[kind] = 0m;

        if (selectedKinds.Count == 1)
        {
            amounts[selectedKinds[0]] = totalAmount;
            return;
        }

        if (selectedKinds.Count != 2) return;
        var source = editedKind is not null && selectedKinds.Contains(editedKind.Value)
            ? editedKind.Value
            : selectedKinds[0];
        var target = selectedKinds.First(kind => kind != source);
        var sourceAmount = amounts.TryGetValue(source, out var value) ? Math.Max(0m, value) : 0m;
        amounts[source] = sourceAmount;
        amounts[target] = Math.Max(0m, totalAmount - sourceAmount);
    }

    public static bool IsExact(
        decimal totalAmount,
        IEnumerable<RetailPaymentKind> selectedKinds,
        IReadOnlyDictionary<RetailPaymentKind, decimal> amounts) =>
        decimal.Round(selectedKinds.Sum(kind => amounts.TryGetValue(kind, out var value) ? value : 0m), 2) ==
        decimal.Round(totalAmount, 2);
}
