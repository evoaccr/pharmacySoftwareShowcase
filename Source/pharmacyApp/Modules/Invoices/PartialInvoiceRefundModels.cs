using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace pharmacyApp.Modules.Invoices;

public sealed record PartialInvoiceRefundLaunchOptions(bool SendItsNotifications = false);

public sealed class PartialInvoiceRefundQrRow
{
    public required string InvoiceUuid { get; init; }
    public required DateOnly? IssueDate { get; init; }
    public required string SenderName { get; init; }
    public required string Barcode { get; init; }
    public required string ProductName { get; init; }
    public required string QrCode { get; init; }
    public required DateOnly ExpiryDate { get; init; }

    public string IssueDateDisplay => IssueDate?.ToString("dd.MM.yyyy") ?? "—";
    public string ExpiryDateDisplay => ExpiryDate.ToString("dd.MM.yyyy");
}

public sealed class PartialInvoiceRefundGroup : INotifyPropertyChanged
{
    private bool isExpanded;

    public PartialInvoiceRefundGroup(PartialInvoiceRefundQrRow first)
    {
        ArgumentNullException.ThrowIfNull(first);
        Barcode = first.Barcode;
        ProductName = first.ProductName;
        QrCodes.Add(first);
    }

    public PartialInvoiceRefundGroup(
        string barcode,
        string productName,
        IEnumerable<PartialInvoiceRefundQrRow> rows)
    {
        Barcode = barcode;
        ProductName = productName;
        foreach (var row in rows) QrCodes.Add(row);
    }

    public string Barcode { get; }
    public string ProductName { get; }
    public ObservableCollection<PartialInvoiceRefundQrRow> QrCodes { get; } = [];
    public int Quantity => QrCodes.Count;

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

    public void Add(PartialInvoiceRefundQrRow row)
    {
        QrCodes.Add(row);
        OnPropertyChanged(nameof(Quantity));
    }

    public bool Remove(PartialInvoiceRefundQrRow row)
    {
        var removed = QrCodes.Remove(row);
        if (removed) OnPropertyChanged(nameof(Quantity));
        return removed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PartialInvoiceRefundCart
{
    public ObservableCollection<PartialInvoiceRefundGroup> Groups { get; } = [];
    public IEnumerable<PartialInvoiceRefundQrRow> QrCodes => Groups.SelectMany(group => group.QrCodes);
    public int QrCount => Groups.Sum(group => group.Quantity);
    public int InvoiceCount => QrCodes.Select(row => row.InvoiceUuid).Distinct(StringComparer.Ordinal).Count();

    public bool Contains(string qrCode) =>
        QrCodes.Any(row => row.QrCode.Equals(qrCode, StringComparison.Ordinal));

    public PartialInvoiceRefundGroup Add(PartialInvoiceRefundQrRow row)
    {
        if (Contains(row.QrCode))
            throw new InvalidOperationException("Bu karekod kısmi iade listesinde zaten bulunuyor.");
        var group = Groups.FirstOrDefault(candidate =>
            candidate.Barcode.Equals(row.Barcode, StringComparison.Ordinal) &&
            candidate.ProductName.Equals(row.ProductName, StringComparison.Ordinal));
        if (group is null)
        {
            group = new PartialInvoiceRefundGroup(row);
            Groups.Add(group);
        }
        else
        {
            group.Add(row);
        }
        return group;
    }

    public void RemoveGroup(PartialInvoiceRefundGroup group) => Groups.Remove(group);

    public void Remove(PartialInvoiceRefundQrRow row)
    {
        var group = Groups.FirstOrDefault(candidate => candidate.QrCodes.Contains(row));
        if (group is null) return;
        group.Remove(row);
        if (group.Quantity == 0) Groups.Remove(group);
    }

    public void Clear() => Groups.Clear();
}

public sealed record PartialInvoiceRefundResult(
    string ReferenceCode,
    int QrCount,
    int InvoiceCount,
    int ItsRequestCount,
    int ItsNotificationCount,
    IReadOnlyList<string> ItsNotificationIds);
