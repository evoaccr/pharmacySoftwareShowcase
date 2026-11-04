using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.Invoices;

internal sealed record InvoiceItsNotificationPlan(
    string OperationCode,
    string OperationDisplay,
    int ProductCount,
    object? Request)
{
    public bool IsRequired => ProductCount > 0;
}

internal static class InvoiceItsNotificationPlanner
{
    public static InvoiceItsNotificationPlan Build(
        bool save,
        string depotGln,
        IEnumerable<InvoiceStockMovementItem> movements)
    {
        var products = new List<ItsQrProduct>();
        foreach (var movement in movements.Where(item => item.IsSerialized))
        {
            if (!ItsQrParser.TryParse(movement.Code, out var product, out var error) || product is null)
                throw new InvalidOperationException($"İTS karekodu doğrulanamadı: {error}");
            products.Add(product);
        }

        var operationCode = save ? "ACCEPTANCE" : "RETURN";
        var operationDisplay = save ? "Mal Alım" : "Mal İade";
        if (products.Count == 0)
            return new InvoiceItsNotificationPlan(operationCode, operationDisplay, 0, null);

        object request = save
            ? ItsAcceptanceRequestFactory.Create(products)
            : ItsReturnRequestFactory.Create(depotGln, products);
        return new InvoiceItsNotificationPlan(operationCode, operationDisplay, products.Count, request);
    }
}
