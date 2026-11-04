using System.Globalization;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceStockLogQuery(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string ActionCode,
    string UserId,
    string Search);

public sealed record InvoiceStockLogUserOption(string UserId, string DisplayText);

public sealed record InvoiceStockLogActionOption(string Code, string DisplayText);

public sealed class InvoiceStockLogRow
{
    public required long RecordId { get; init; }
    public required string InvoiceUuid { get; init; }
    public required string InvoiceUuids { get; init; }
    public required string ActionCode { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required string ReferenceCode { get; init; }
    public required int MovementTypeId { get; init; }
    public required string MovementTypeName { get; init; }
    public long? RelatedLogId { get; init; }
    public required string SenderName { get; init; }
    public required string ReceiverName { get; init; }
    public required string Notes { get; set; }
    public int MovementCount { get; init; }
    public int SerializedQuantity { get; init; }
    public int BarcodeQuantity { get; init; }

    public string ActionDisplay => ActionCode switch
    {
        "SAVE" => "Stoka Alındı",
        "REFUND" => "İade Edildi",
        "PARTIAL_REFUND" => "Kısmi İade Edildi",
        _ => ActionCode
    };

    public string OccurredAtDisplay => OccurredAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public string UserDisplay => string.IsNullOrWhiteSpace(UserName) ? UserId : UserName;
    public string RelatedLogDisplay => RelatedLogId?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? "—" : Notes;
    public string InvoiceUuidsDisplay => string.IsNullOrWhiteSpace(InvoiceUuids) ? InvoiceUuid : InvoiceUuids;
}

public sealed record InvoiceStockLogMovementRow(
    long RecordId,
    string QrCode,
    string Barcode,
    string ProductName,
    int MovementTypeId,
    string MovementTypeName,
    DateTime MovementDate,
    string Notes)
{
    public string MovementDateDisplay => MovementDate.ToString("dd.MM.yyyy HH:mm:ss");
    public string BarcodeDisplay => string.IsNullOrWhiteSpace(Barcode) ? "—" : Barcode;
    public string ProductDisplay => string.IsNullOrWhiteSpace(ProductName) ? "Ürün bilgisi yok" : ProductName;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? "—" : Notes;
}
