namespace pharmacyApp.Modules.StockMovements;

public sealed record QrMovementsLaunchOptions(
    DateTime StartDate,
    DateTime EndDate,
    string UserId = "",
    IReadOnlyList<string>? MovementTypes = null,
    string MovementAddress = "",
    string AddressType = "",
    string PeriodTitle = "Yıllık",
    string Search = "",
    bool IgnoreDateFilter = false,
    IReadOnlyList<string>? Barcodes = null)
{
    public static QrMovementsLaunchOptions Daily
    {
        get
        {
            var today = DateTime.Today;
            return new QrMovementsLaunchOptions(today, today, PeriodTitle: "Günlük");
        }
    }
}
