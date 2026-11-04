namespace pharmacyApp.Modules.StockMovements;

public sealed class QrMovementItem
{
    public required int RecordId { get; init; }
    public required string QrCode { get; init; }
    public string? Barcode { get; init; }
    public string? MedicineName { get; init; }
    public int? ProductRecordId { get; init; }
    public required short MovementTypeId { get; init; }
    public required string MovementTypeCode { get; init; }
    public required string MovementTypeDisplay { get; init; }
    public required bool IsEntryDirection { get; init; }
    public required DateTime MovementDate { get; init; }
    public string? MovementAddress { get; init; }
    public string? MovementAddressName { get; init; }
    public string? MovementAddressEntityType { get; init; }
    public string? MovementAddressRecordKey { get; init; }
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public string? ReferenceCode { get; init; }
    public string? Notes { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(MedicineName)
        ? Barcode ?? "—"
        : MedicineName;
    public string MovementAddressDisplay => string.IsNullOrWhiteSpace(MovementAddressName)
        ? string.IsNullOrWhiteSpace(MovementAddress) ? "—" : MovementAddress
        : MovementAddressName;
    public string UserDisplay => string.IsNullOrWhiteSpace(UserName)
        ? string.IsNullOrWhiteSpace(UserId) ? "—" : UserId
        : UserName;
    public string ReferenceCodeDisplay => string.IsNullOrWhiteSpace(ReferenceCode) ? "—" : ReferenceCode;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? "—" : Notes;
    public string MovementDateDisplay => MovementDate.ToString("dd.MM.yyyy HH:mm");
    public string Direction => IsEntryDirection ? "Entry" : "Exit";
}

public sealed class QrMovementTypeOption
{
    public QrMovementTypeOption(short id, string code, string displayName, bool isEntry)
    {
        Id = id;
        Code = code;
        DisplayName = displayName;
        IsEntry = isEntry;
    }

    public short Id { get; }
    public string Code { get; }
    public string DisplayName { get; }
    public bool IsEntry { get; }
    public bool IsSelected { get; set; } = true;
}

public sealed record QrSearchSuggestion(
    string DisplayText,
    string FilterValue,
    QrMovementSearchMode SearchMode,
    bool IsBrand,
    bool HasStock);

public sealed record BarcodeSearchResolution(string Barcode, string DisplayName);

public sealed record QrUserFilterOption(string UserId, string DisplayText);

public sealed record QrAddressFilterOption(string Value, string DisplayText);
