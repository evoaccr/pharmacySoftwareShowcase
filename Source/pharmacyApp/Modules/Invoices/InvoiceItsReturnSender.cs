using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.Invoices;

internal sealed record InvoiceItsReturnConfiguration(
    ItsEnvironment Environment,
    ItsCredentials Credentials);

internal sealed record InvoiceItsReturnExecution(
    int NotificationCount,
    IReadOnlyList<string> NotificationIds);

internal interface IInvoiceItsReturnClient
{
    Task<ItsNotificationResult> SendReturnAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsReturnRequest request,
        CancellationToken cancellationToken);
}

internal sealed class InvoiceItsReturnApiClient : IInvoiceItsReturnClient
{
    private readonly ItsApiClient client = new();

    public Task<ItsNotificationResult> SendReturnAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsReturnRequest request,
        CancellationToken cancellationToken) =>
        client.SendReturnAsync(environment, credentials, request, cancellationToken);
}

internal sealed class InvoiceItsReturnSender
{
    private readonly IInvoiceItsReturnClient client;
    private readonly Func<InvoiceItsReturnConfiguration> configurationProvider;

    internal InvoiceItsReturnSender(
        IInvoiceItsReturnClient client,
        Func<InvoiceItsReturnConfiguration> configurationProvider)
    {
        this.client = client;
        this.configurationProvider = configurationProvider;
    }

    public async Task<InvoiceItsReturnExecution> SendAsync(
        IReadOnlyList<InvoiceItsNotificationPlan> plans,
        CancellationToken cancellationToken)
    {
        var required = plans.Where(plan => plan.IsRequired).ToArray();
        if (required.Length == 0) return new InvoiceItsReturnExecution(0, []);
        if (required.Length > 1)
            throw new InvalidOperationException(
                "Gerçek İTS Mal İade işlemi farklı depolara ait karekodları tek harekette gönderemez. " +
                "Her depo için ayrı kısmi iade oluşturun.");

        var configuration = configurationProvider();
        if (string.IsNullOrWhiteSpace(configuration.Credentials.Username) ||
            string.IsNullOrWhiteSpace(configuration.Credentials.Password))
            throw new InvalidOperationException(
                "İTS kullanıcı adı ve parolası Ayarlar > İTS Ayarları bölümünde yapılandırılmalıdır.");

        if (required[0].Request is not ItsReturnRequest request)
            throw new InvalidOperationException("Mal İade isteği hazırlanamadı.");
        var result = await client.SendReturnAsync(
            configuration.Environment,
            configuration.Credentials,
            request,
            cancellationToken);
        if (!result.IsSuccessful)
            throw new InvalidOperationException(FormatFailure(result));

        return new InvoiceItsReturnExecution(
            1,
            string.IsNullOrWhiteSpace(result.NotificationId) ? [] : [result.NotificationId]);
    }

    private static string FormatFailure(ItsNotificationResult result)
    {
        var fault = $"{result.FaultCode} {result.FaultMessage}".Trim();
        if (fault.Length > 0) return $"İTS Mal İade bildirimi reddedildi. HTTP {result.HttpStatusCode}: {fault}";
        var products = string.Join(", ", result.Products.Select(product =>
            $"{product.SerialNumber}: {product.ResultDisplay}"));
        return products.Length > 0
            ? $"İTS Mal İade bildirimi reddedildi. HTTP {result.HttpStatusCode}: {products}"
            : $"İTS Mal İade bildirimi kesin bir sonuç vermedi. HTTP {result.HttpStatusCode}.";
    }
}
