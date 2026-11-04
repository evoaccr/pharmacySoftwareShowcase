namespace pharmacyApp.Modules.Products;

public sealed record ProductPriceRefreshRequest(
    int RecordId,
    string? Barcode,
    bool DepotSaleMissing,
    bool ManufacturerSaleMissing);

public sealed record ProductPriceRefreshResult(
    decimal? DepotSale,
    decimal? ManufacturerSale,
    decimal? VatRate,
    string? Currency);

public interface IProductPriceRefreshConnector
{
    Task<ProductPriceRefreshResult?> RefreshMissingPricesAsync(
        ProductPriceRefreshRequest request,
        CancellationToken cancellationToken);
}

public sealed class NullProductPriceRefreshConnector : IProductPriceRefreshConnector
{
    public Task<ProductPriceRefreshResult?> RefreshMissingPricesAsync(
        ProductPriceRefreshRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProductPriceRefreshResult?>(null);
}

public static class ProductPriceRefreshConnector
{
    private static IProductPriceRefreshConnector current = new NullProductPriceRefreshConnector();

    public static IProductPriceRefreshConnector Current
    {
        get => current;
        set => current = value ?? throw new ArgumentNullException(nameof(value));
    }
}
