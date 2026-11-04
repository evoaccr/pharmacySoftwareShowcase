using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace pharmacyApp.Modules.Takas;

[Flags]
public enum TakasListStatuses
{
    None = 0,
    PendingTransfer = 1,
    InTransit = 2,
    PartiallyTransferred = 4,
    PartiallyAccepted = 8,
    Accepted = 16,
    PartiallyCancelled = 32,
    Cancelled = 64,
    Failed = 128,
    ReconciliationRequired = 256,
    All = PendingTransfer | InTransit | PartiallyTransferred | PartiallyAccepted |
          Accepted | PartiallyCancelled | Cancelled | Failed | ReconciliationRequired
}

public sealed record TakasListQuery(
    DateTime StartDate,
    DateTime EndDateExclusive,
    string QrCode = "",
    string Barcode = "",
    string BrandName = "",
    int? PharmacyId = null,
    string UserId = "",
    string Direction = "",
    TakasListStatuses Statuses = TakasListStatuses.All,
    decimal? MinimumDebt = null,
    decimal? MaximumDebt = null);

public sealed record TakasListUserFilterOption(string UserId, string DisplayText);

public sealed class TakasListItem : INotifyPropertyChanged
{
    private bool detailsLoaded;

    public required int TakasId { get; init; }
    public required int PharmacyId { get; init; }
    public required string PharmacyName { get; init; }
    public required string PharmacistName { get; init; }
    public required string SourceGln { get; init; }
    public required string DestinationGln { get; init; }
    public required string Direction { get; init; }
    public required string Status { get; init; }
    public required int MedicineQuantity { get; init; }
    public required decimal DebtAmount { get; init; }
    public string? TransferNotificationId { get; init; }
    public string? AcceptNotificationId { get; init; }
    public string? CancelNotificationId { get; init; }
    public string? UserName { get; init; }
    public string? Notes { get; init; }
    public string? LastStatusError { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? TransferredAt { get; init; }
    public DateTime? AcceptedAt { get; init; }
    public ObservableCollection<TakasListProductGroup> Products { get; } = [];

    public string ReferenceCode => $"T{TakasId}";
    public string PharmacyDisplay => string.IsNullOrWhiteSpace(PharmacyName) ? DestinationGln : PharmacyName;
    public string PharmacistDisplay => string.IsNullOrWhiteSpace(PharmacistName) ? "—" : PharmacistName;
    public string CounterpartyGln => Direction == "incoming" ? SourceGln : DestinationGln;
    public string DirectionDisplay => Direction == "incoming" ? "Gelen" : "Giden";
    public string StatusDisplay => TakasStatusText.Header(Status);
    public string DebtDisplay => DebtAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    public string NotificationDisplay => FirstNotEmpty(TransferNotificationId, AcceptNotificationId, CancelNotificationId) ?? "—";
    public string UserDisplay => string.IsNullOrWhiteSpace(UserName) ? "—" : UserName;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? "—" : Notes;
    public string CreatedAtDisplay => CreatedAt.ToString("dd.MM.yyyy HH:mm");

    public bool DetailsLoaded
    {
        get => detailsLoaded;
        set
        {
            if (detailsLoaded == value) return;
            detailsLoaded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsLoaded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed class TakasListProductGroup : INotifyPropertyChanged
{
    private bool isExpanded;

    public required string Barcode { get; init; }
    public required string ProductName { get; init; }
    public int StockQuantity { get; init; }
    public ObservableCollection<TakasListQrEntry> QrCodes { get; } = [];
    public int Quantity => QrCodes.Count;
    public decimal? UnitCost => SingleValue(QrCodes.Select(item => item.EntryCost));
    public decimal? UnitDebt => SingleValue(QrCodes.Select(item => (decimal?)item.DebtAmount));
    public decimal TotalDebt => QrCodes.Sum(item => item.DebtAmount);
    public string StatusDisplay
    {
        get
        {
            var statuses = QrCodes.Select(item => item.Status).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return statuses.Length == 1 ? TakasStatusText.Item(statuses[0]) : "Karışık";
        }
    }
    public string StockDisplay => StockQuantity.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
    public string UnitCostDisplay => Money(UnitCost);
    public string UnitDebtDisplay => Money(UnitDebt);
    public string TotalDebtDisplay => Money(TotalDebt);

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

    private static decimal? SingleValue(IEnumerable<decimal?> source)
    {
        var values = source.Distinct().Take(2).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static string Money(decimal? amount) => amount is null
        ? "—"
        : amount.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
}

public sealed record TakasListQrEntry(
    string QrCode,
    string? SerialNumber,
    string? BatchNumber,
    DateTime? ExpiryDate,
    decimal? EntryCost,
    decimal DebtAmount,
    string Status,
    string? ResultCode,
    string? LastError,
    bool IsSerialized)
{
    public string CodeTypeDisplay => IsSerialized ? "Karekod" : "Barkod";
    public string ExpiryDateDisplay => ExpiryDate?.ToString("dd.MM.yyyy") ?? "—";
    public string TraceDisplay => IsSerialized
        ? $"SN: {SerialNumber ?? "—"} · Parti: {BatchNumber ?? "—"}"
        : "Barkod seviyesinde geçmiş Takas hareketi";
    public string EntryCostDisplay => Money(EntryCost);
    public string DebtDisplay => Money(DebtAmount);
    public string StatusDisplay => TakasStatusText.Item(Status);
    public string ResultDisplay => string.IsNullOrWhiteSpace(ResultCode) ? "—" : ResultCode;
    public string ErrorDisplay => string.IsNullOrWhiteSpace(LastError) ? "—" : LastError;

    private static string Money(decimal? amount) => amount is null
        ? "—"
        : amount.Value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
}

public static class TakasStatusText
{
    public static string Header(string status) => status switch
    {
        "pending_transfer" => "Bildirim Bekliyor",
        "in_transit" => "Yolda",
        "partially_transferred" => "Kısmen Yolda",
        "partially_accepted" => "Kısmen Kabul Edildi",
        "accepted" => "Kabul Edildi",
        "partially_cancelled" => "Kısmen İptal",
        "cancelled" => "İptal Edildi",
        "failed" => "Gönderilemedi",
        "reconciliation_required" => "Teknik Kontrol Gerekli",
        _ => status
    };

    public static string Item(string status) => status switch
    {
        "pending_transfer" => "Bildirim Bekliyor",
        "in_transit" => "Yolda",
        "accepted" => "Kabul Edildi",
        "transfer_cancelled" => "İptal Edildi",
        "transfer_failed" => "Gönderilemedi",
        "reconciliation_required" => "Teknik Kontrol Gerekli",
        _ => status
    };
}
