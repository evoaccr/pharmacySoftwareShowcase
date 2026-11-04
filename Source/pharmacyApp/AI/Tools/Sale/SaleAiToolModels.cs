namespace pharmacyApp.AI.Tools.Sale;

using pharmacyApp.Modules.Sales;

public sealed record SaleResolvedItemToolResult(
    bool Found,
    bool CanAdd,
    string? IssueCode,
    string? IssueMessage,
    SaleTableItemToolResult? Item);

public sealed record SaleTableItemToolResult(
    string EnteredCode,
    string Barcode,
    bool IsBarcodeOnly,
    string StockStatus,
    int ProductRecordId,
    string ProductName,
    decimal UnitPrice,
    decimal? UnitCost,
    int StockQuantity,
    DateTime? ExpiryDate,
    decimal? EntryCost,
    decimal? PublicPrice,
    string? PrescriptionColor,
    string? Indication);

public sealed record SaleTableEntryToolResult(
    string EnteredCode,
    bool IsBarcodeOnly,
    DateTime? ExpiryDate,
    decimal? EntryCost,
    decimal GrossPrice,
    decimal DiscountAmount,
    decimal NetPrice);

public sealed record SaleTableGroupToolResult(
    string Barcode,
    int ProductRecordId,
    string ProductName,
    int StockQuantity,
    int Quantity,
    decimal? UnitCost,
    decimal UnitPrice,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal NetTotal,
    IReadOnlyList<SaleTableEntryToolResult> Entries);

public sealed record SaleTableIssueToolResult(string EnteredCode, string Code, string Message);

public sealed record SaleTablePreviewToolResult(
    IReadOnlyList<SaleTableGroupToolResult> Groups,
    IReadOnlyList<SaleTableIssueToolResult> Issues,
    int ItemCount,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal NetTotal);

public sealed record SalePaymentToolResult(string Type, decimal Amount);

public sealed record SalePaymentSplitToolResult(
    IReadOnlyList<SalePaymentToolResult> Payments,
    decimal TargetTotal,
    decimal AllocatedTotal,
    decimal RemainingAmount,
    bool IsExact);

public sealed record SaleDraftValidationError(string Code, string Message);

public sealed record SaleDraftValidationToolResult(
    bool Ready,
    SaleTablePreviewToolResult Table,
    IReadOnlyList<SalePaymentToolResult> Payments,
    decimal PaymentTotal,
    decimal DebtAmount,
    int? PatientRecordId,
    IReadOnlyList<SaleDraftValidationError> Errors);

public sealed record SaleRecordSearchToolResult(
    IReadOnlyList<RetailSaleRecord> Sales,
    int TotalCount,
    int Offset,
    int Limit,
    bool HasMore);

public sealed record SaleRecordGetToolResult(bool Found, RetailSaleRecord? Sale);

public sealed record SaleRecordAggregateToolResult(
    IReadOnlyList<RetailSaleAggregateGroup> Groups,
    int TotalCount,
    int Offset,
    int Limit,
    bool HasMore);
