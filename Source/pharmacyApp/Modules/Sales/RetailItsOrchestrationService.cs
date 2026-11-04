using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.Sales;

public enum RetailItsOperationKind
{
    Sale,
    SaleCancellation
}

public enum RetailItsResponseOutcome
{
    Successful,
    DefinitelyRejected,
    ReconciliationRequired
}

public sealed record RetailItsConfiguration(
    ItsEnvironment Environment,
    ItsCredentials Credentials,
    string PharmacyGln);

public sealed record RetailItsCompletionResult(
    int LocalRecordId,
    bool ItsNotificationSent,
    string? NotificationId);

public interface IRetailItsOperationStore
{
    Task<Guid> PrepareAsync(
        RetailItsOperationKind operationKind,
        string pharmacyGln,
        ItsPharmacyNotificationRequest request,
        IReadOnlyList<string> qrCodes,
        string requestedBy,
        CancellationToken cancellationToken);

    Task MarkRemoteConfirmedAsync(
        Guid operationId,
        ItsNotificationResult result,
        CancellationToken cancellationToken);

    Task MarkDefinitelyNotSentAsync(Guid operationId, string error, CancellationToken cancellationToken);

    Task MarkFailedAsync(
        Guid operationId,
        ItsNotificationResult result,
        string error,
        CancellationToken cancellationToken);

    Task MarkReconciliationRequiredAsync(
        Guid operationId,
        ItsNotificationResult? result,
        string error,
        CancellationToken cancellationToken);

    Task MarkLocalCompletedAsync(
        Guid operationId,
        RetailItsOperationKind operationKind,
        int localRecordId,
        CancellationToken cancellationToken);
}

public abstract class RetailItsOrchestrationException : InvalidOperationException
{
    protected RetailItsOrchestrationException(string message, Guid operationId, Exception? innerException = null)
        : base(message, innerException) => OperationId = operationId;

    public Guid OperationId { get; }
}

public sealed class RetailItsDefinitelyNotSentException : RetailItsOrchestrationException
{
    public RetailItsDefinitelyNotSentException(string message, Guid operationId, Exception? innerException = null)
        : base(message, operationId, innerException)
    {
    }
}

public sealed class RetailItsRejectedException : RetailItsOrchestrationException
{
    public RetailItsRejectedException(string message, Guid operationId)
        : base(message, operationId)
    {
    }
}

public sealed class RetailItsReconciliationRequiredException : RetailItsOrchestrationException
{
    public RetailItsReconciliationRequiredException(string message, Guid operationId, Exception? innerException = null)
        : base(message, operationId, innerException)
    {
    }
}

public sealed class RetailItsLocalCompletionRequiredException : RetailItsOrchestrationException
{
    public RetailItsLocalCompletionRequiredException(
        string message,
        Guid operationId,
        int? localRecordId,
        Exception innerException)
        : base(message, operationId, innerException) => LocalRecordId = localRecordId;

    public int? LocalRecordId { get; }
}

public static class RetailItsResponseClassifier
{
    public static RetailItsResponseOutcome Classify(
        ItsPharmacyNotificationRequest request,
        ItsNotificationResult result)
    {
        var expected = ToCounts(request.Products.Select(product => ProductKey(product.Gtin, product.SerialNumber)));
        var returned = ToCounts(result.Products.Select(product => ProductKey(product.Gtin, product.SerialNumber)));
        var completeProductSet = expected.Count > 0 && CountsEqual(expected, returned);
        var hasSuccessfulProduct = result.Products.Any(product => IsSuccessCode(product.ResultCode));
        var allProductsSuccessful = completeProductSet && result.Products.All(product => IsSuccessCode(product.ResultCode));

        if (result.HttpStatusCode is >= 200 and <= 299 &&
            string.IsNullOrWhiteSpace(result.FaultCode) &&
            string.IsNullOrWhiteSpace(result.FaultMessage) &&
            allProductsSuccessful)
            return RetailItsResponseOutcome.Successful;

        var hasExplicitFault = !string.IsNullOrWhiteSpace(result.FaultCode) ||
                               !string.IsNullOrWhiteSpace(result.FaultMessage);
        var allReturnedProductsRejected = completeProductSet &&
                                          result.Products.All(product => !IsSuccessCode(product.ResultCode));
        if (!hasSuccessfulProduct && (hasExplicitFault || allReturnedProductsRejected))
            return RetailItsResponseOutcome.DefinitelyRejected;

        return RetailItsResponseOutcome.ReconciliationRequired;
    }

    private static bool IsSuccessCode(string? resultCode) =>
        string.Equals(resultCode?.Trim(), "00000", StringComparison.Ordinal);

    private static string ProductKey(string gtin, string serialNumber) =>
        $"{gtin.Trim()}\u001f{serialNumber.Trim()}";

    private static Dictionary<string, int> ToCounts(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static bool CountsEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var count) && count == pair.Value);
}

public sealed class RetailItsOrchestrationService
{
    private readonly IItsPharmacyNotificationClient apiClient;
    private readonly IRetailItsOperationStore operationStore;
    private readonly Func<RetailItsConfiguration> configurationProvider;

    public RetailItsOrchestrationService(
        IItsPharmacyNotificationClient apiClient,
        IRetailItsOperationStore operationStore,
        Func<RetailItsConfiguration> configurationProvider)
    {
        this.apiClient = apiClient;
        this.operationStore = operationStore;
        this.configurationProvider = configurationProvider;
    }

    public async Task<RetailItsCompletionResult> ExecuteAsync(
        RetailItsOperationKind operationKind,
        IReadOnlyList<string> serializedQrCodes,
        string requestedBy,
        Func<CancellationToken, Task<int>> completeLocalAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serializedQrCodes);
        ArgumentNullException.ThrowIfNull(completeLocalAsync);
        requestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "admin" : requestedBy.Trim();

        var qrCodes = serializedQrCodes.Select(code => code.Trim()).ToArray();
        if (qrCodes.Length == 0)
        {
            var localRecordId = await completeLocalAsync(cancellationToken);
            return new RetailItsCompletionResult(localRecordId, false, null);
        }
        if (qrCodes.Any(string.IsNullOrWhiteSpace) ||
            qrCodes.Distinct(StringComparer.Ordinal).Count() != qrCodes.Length)
            throw new InvalidOperationException("İTS bildirimi karekod listesi boş veya tekrarlı değer içeremez.");

        var products = new List<ItsQrProduct>(qrCodes.Length);
        foreach (var qrCode in qrCodes)
        {
            if (!ItsQrParser.TryParse(qrCode, out var product, out var error) || product is null)
                throw new InvalidOperationException($"Karekod İTS bildirimi için ayrıştırılamadı: {qrCode}. {error}");
            products.Add(product);
        }

        var configuration = configurationProvider();
        if (string.IsNullOrWhiteSpace(configuration.Credentials.Username) ||
            string.IsNullOrWhiteSpace(configuration.Credentials.Password))
            throw new InvalidOperationException(
                "İTS kullanıcı adı ve parolası Ayarlar > İTS Ayarları bölümünde yapılandırılmalıdır.");

        var itsOperation = operationKind == RetailItsOperationKind.Sale
            ? ItsPharmacyOperation.RetailSale
            : ItsPharmacyOperation.RetailSaleCancellation;
        var request = ItsPharmacyRequestFactory.Create(
            itsOperation,
            configuration.PharmacyGln,
            products);
        var operationId = await operationStore.PrepareAsync(
            operationKind,
            configuration.PharmacyGln,
            request,
            qrCodes,
            requestedBy,
            cancellationToken);

        ItsNotificationResult result;
        try
        {
            result = await apiClient.SendNotificationAsync(
                configuration.Environment,
                itsOperation,
                configuration.Credentials,
                request,
                cancellationToken);
        }
        catch (ItsNotificationNotSentException exception)
        {
            if (!await TryMarkDefinitelyNotSentAsync(operationId, exception.Message))
                throw new RetailItsReconciliationRequiredException(
                    "İTS bildirimi kesin olarak gönderilmedi ancak işlem kaydı güvenli yeniden deneme durumuna getirilemedi. Aynı karekodları yeniden göndermeyin.",
                    operationId,
                    exception);
            throw new RetailItsDefinitelyNotSentException(
                "İTS bildirimi kesin olarak gönderilmedi. Yerel işlem oluşturulmadı; yeniden deneyebilirsiniz.",
                operationId,
                exception);
        }
        catch (Exception exception)
        {
            await TryMarkReconciliationAsync(operationId, null, exception.Message);
            throw new RetailItsReconciliationRequiredException(
                "İTS bildiriminin ulaşıp ulaşmadığı kesinleştirilemedi. Yerel işlem oluşturulmadı ve aynı karekodlar yeniden gönderime kapatıldı.",
                operationId,
                exception);
        }

        switch (RetailItsResponseClassifier.Classify(request, result))
        {
            case RetailItsResponseOutcome.DefinitelyRejected:
                try
                {
                    await operationStore.MarkFailedAsync(
                        operationId,
                        result,
                        FormatRemoteFailure(result),
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    throw new RetailItsReconciliationRequiredException(
                        "İTS olumsuz cevabı alındı ancak işlem kaydı kesinleştirilemedi. Aynı karekodları yeniden göndermeyin.",
                        operationId,
                        exception);
                }
                throw new RetailItsRejectedException(
                    $"İTS bildirimi reddedildi. Yerel işlem oluşturulmadı. {FormatRemoteFailure(result)}",
                    operationId);

            case RetailItsResponseOutcome.ReconciliationRequired:
                await TryMarkReconciliationAsync(operationId, result, FormatRemoteFailure(result));
                throw new RetailItsReconciliationRequiredException(
                    "İTS cevabı bütün karekodlar için kesin bir sonuç vermedi. Yerel işlem oluşturulmadı ve aynı karekodlar yeniden gönderime kapatıldı.",
                    operationId);
        }

        try
        {
            await operationStore.MarkRemoteConfirmedAsync(operationId, result, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await TryMarkReconciliationAsync(operationId, result, exception.Message);
            throw new RetailItsReconciliationRequiredException(
                "İTS bildirimi başarılı görünüyor ancak yerel bildirim kaydı güncellenemedi. Aynı karekodları yeniden göndermeyin.",
                operationId,
                exception);
        }

        int localId;
        try
        {
            localId = await completeLocalAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new RetailItsLocalCompletionRequiredException(
                "İTS bildirimi başarılı oldu fakat yerel stok işlemi tamamlanamadı. Aynı karekodları yeniden göndermeyin; yerel tamamlama gerekir.",
                operationId,
                null,
                exception);
        }

        try
        {
            await operationStore.MarkLocalCompletedAsync(
                operationId,
                operationKind,
                localId,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new RetailItsLocalCompletionRequiredException(
                "İTS ve yerel stok işlemi tamamlandı fakat bildirim günlüğü sonlandırılamadı. Aynı karekodları yeniden göndermeyin.",
                operationId,
                localId,
                exception);
        }

        return new RetailItsCompletionResult(localId, true, result.NotificationId);
    }

    private async Task<bool> TryMarkDefinitelyNotSentAsync(Guid operationId, string error)
    {
        try
        {
            await operationStore.MarkDefinitelyNotSentAsync(operationId, error, CancellationToken.None);
            return true;
        }
        catch
        {
            // The prepared operation and active QR rows remain a safe retry barrier.
            return false;
        }
    }

    private async Task TryMarkReconciliationAsync(
        Guid operationId,
        ItsNotificationResult? result,
        string error)
    {
        try
        {
            await operationStore.MarkReconciliationRequiredAsync(
                operationId,
                result,
                error,
                CancellationToken.None);
        }
        catch
        {
            // A still-prepared operation also blocks an unsafe resend.
        }
    }

    private static string FormatRemoteFailure(ItsNotificationResult result)
    {
        var fault = $"{result.FaultCode} {result.FaultMessage}".Trim();
        if (fault.Length > 0) return $"HTTP {result.HttpStatusCode}: {fault}";
        var products = string.Join(", ", result.Products.Select(product =>
            $"{product.SerialNumber}: {product.ResultDisplay}"));
        return products.Length > 0
            ? $"HTTP {result.HttpStatusCode}: {products}"
            : $"HTTP {result.HttpStatusCode}: kesin ürün sonucu alınamadı.";
    }
}
