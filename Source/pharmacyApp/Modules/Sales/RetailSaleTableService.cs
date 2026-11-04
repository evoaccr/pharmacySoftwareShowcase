namespace pharmacyApp.Modules.Sales;

public static class RetailSaleTableIssueCodes
{
    public const string NotInStock = "not_in_stock";
    public const string SerializedCodeRequired = "serialized_code_required";
    public const string Consigned = "consigned";
    public const string DuplicateQr = "duplicate_qr";
    public const string StockLimitReached = "stock_limit_reached";
    public const string InvalidLineDiscount = "invalid_line_discount";
}

public sealed record RetailSaleTableCheck(bool CanAdd, string? IssueCode = null, string? Message = null)
{
    public static RetailSaleTableCheck Allowed { get; } = new(true);
}

public sealed record RetailSaleTableIssue(string EnteredCode, string Code, string Message);

public sealed record RetailSaleTableBuildResult(
    RetailSaleCart Cart,
    IReadOnlyList<RetailSaleTableIssue> Issues);

public static class RetailSaleTableRules
{
    public static RetailSaleTableCheck CheckCanAdd(
        RetailSaleCart cart,
        RetailSaleItem? item,
        RetailSaleMode mode = RetailSaleMode.Retail)
    {
        ArgumentNullException.ThrowIfNull(cart);
        if (item is null)
            return Rejected(RetailSaleTableIssueCodes.NotInStock, "Bu ilaç sizin üzerinize değildir.");
        if (mode == RetailSaleMode.ConsignmentCreate && item.IsBarcodeOnly)
            return Rejected(
                RetailSaleTableIssueCodes.SerializedCodeRequired,
                "Emanet satış için ilaç kutusunun karekodu okutulmalıdır.");
        if (RetailSaleRepository.IsConsigned(item.Status))
            return Rejected(RetailSaleTableIssueCodes.Consigned, "Bu ilaç emanettedir.");
        if (!item.IsBarcodeOnly && cart.ContainsSerializedCode(item.EnteredCode))
            return Rejected(
                RetailSaleTableIssueCodes.DuplicateQr,
                "Bu karekod satış listesinde zaten bulunuyor.");
        if (item.IsBarcodeOnly && cart.CountForBarcode(item.Barcode) >= item.StockQuantity)
            return Rejected(
                RetailSaleTableIssueCodes.StockLimitReached,
                "Bu ürün için stok miktarından daha fazla adet eklenemez.");
        return RetailSaleTableCheck.Allowed;
    }

    private static RetailSaleTableCheck Rejected(string code, string message) => new(false, code, message);
}

/// <summary>
/// UI-independent application service for building and calculating the same draft
/// rows displayed by the Retail Sale screen. It performs no sale, stock, debt, or ITS write.
/// </summary>
public sealed class RetailSaleTableService(RetailSaleRepository? repository = null)
{
    private readonly RetailSaleRepository repository = repository ?? new RetailSaleRepository();

    public Task<RetailSaleItem?> ResolveItemAsync(
        string enteredCode,
        CancellationToken cancellationToken = default) =>
        repository.ResolveItemAsync(enteredCode, cancellationToken);

    public async Task<RetailSaleTableBuildResult> BuildAsync(
        IReadOnlyList<string> enteredCodes,
        RetailSaleMode mode = RetailSaleMode.Retail,
        decimal? distributedDiscount = null,
        IReadOnlyList<decimal>? lineDiscounts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enteredCodes);
        if (distributedDiscount is not null && lineDiscounts is not null)
            throw new ArgumentException("Distributed and manual line discounts cannot be used together.");
        if (lineDiscounts is not null && lineDiscounts.Count != enteredCodes.Count)
            throw new ArgumentException("Line discounts must match the entered-code count.", nameof(lineDiscounts));

        var cart = new RetailSaleCart();
        var issues = new List<RetailSaleTableIssue>();
        for (var index = 0; index < enteredCodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var enteredCode = enteredCodes[index]?.Trim() ?? string.Empty;
            if (enteredCode.Length == 0)
            {
                issues.Add(new RetailSaleTableIssue(
                    string.Empty,
                    RetailSaleTableIssueCodes.NotInStock,
                    "Boş karekod veya barkod satış tablosuna eklenemez."));
                continue;
            }

            var item = await repository.ResolveItemAsync(enteredCode, cancellationToken);
            var check = RetailSaleTableRules.CheckCanAdd(cart, item, mode);
            if (!check.CanAdd)
            {
                issues.Add(new RetailSaleTableIssue(
                    enteredCode,
                    check.IssueCode!,
                    check.Message!));
                continue;
            }

            cart.Add(item!);
            if (lineDiscounts is null) continue;
            var requestedDiscount = lineDiscounts[index];
            if (requestedDiscount < 0m || requestedDiscount > item!.UnitPrice)
            {
                issues.Add(new RetailSaleTableIssue(
                    enteredCode,
                    RetailSaleTableIssueCodes.InvalidLineDiscount,
                    "Satır iskontosu sıfır ile ürünün birim fiyatı arasında olmalıdır."));
                continue;
            }
            cart.Groups.First(group => group.Barcode == item.Barcode)
                .Entries.Last()
                .DiscountAmount = requestedDiscount;
        }

        if (distributedDiscount is not null)
            cart.AllocateDiscount(distributedDiscount.Value);
        if (lineDiscounts is not null)
            cart.SetManualDiscountEditing(true);
        return new RetailSaleTableBuildResult(cart, issues);
    }
}
