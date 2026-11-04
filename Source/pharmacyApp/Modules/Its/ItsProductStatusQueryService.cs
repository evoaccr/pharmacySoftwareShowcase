namespace pharmacyApp.Modules.Its;

public interface IProductStatusQueryService
{
    Task<ProductStatusQueryReport> QueryAsync(
        string qrCode,
        ItsEnvironment environment,
        ItsCredentials credentials,
        string pharmacyGln,
        CancellationToken cancellationToken);
}

public sealed class ItsProductStatusQueryService(
    ILocalProductStatusSource localSource,
    IItsProductStatusClient itsClient) : IProductStatusQueryService
{
    public async Task<ProductStatusQueryReport> QueryAsync(
        string qrCode,
        ItsEnvironment environment,
        ItsCredentials credentials,
        string pharmacyGln,
        CancellationToken cancellationToken)
    {
        if (!ItsQrParser.TryParse(qrCode, out var product, out var parseError) || product is null)
            throw new InvalidOperationException(parseError);
        pharmacyGln = pharmacyGln.Trim();

        var localTask = CaptureLocalAsync(qrCode, cancellationToken);
        var itsTask = CaptureItsAsync(
            environment,
            credentials,
            ItsStatusQueryRequestFactory.Create(product),
            product,
            pharmacyGln,
            cancellationToken);
        await Task.WhenAll(localTask, itsTask);

        var local = await localTask;
        var its = await itsTask;
        return new ProductStatusQueryReport(
            qrCode.Trim(),
            product,
            local.Status,
            local.Error,
            its.Result,
            its.Assessment,
            its.Error,
            BuildComparison(local.Status, local.Error, its.Assessment, its.Error));
    }

    private async Task<(LocalQrStatus? Status, string? Error)> CaptureLocalAsync(
        string qrCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await localSource.QueryAsync(qrCode, cancellationToken), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, exception.Message);
        }
    }

    private async Task<(ItsStatusQueryResult? Result, ItsOwnershipAssessment? Assessment, string? Error)> CaptureItsAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsStatusQueryRequest request,
        ItsQrProduct requestedProduct,
        string pharmacyGln,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Gs1IdentifierValidator.IsValidGln13(pharmacyGln))
                return (null, null,
                    "Eczane GLN kodu Ayarlar > İTS Ayarları bölümünde geçerli olarak yapılandırılmalıdır.");
            if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
                return (null, null,
                    "İTS kullanıcı adı ve parolası Ayarlar > İTS Ayarları bölümünde yapılandırılmalıdır.");

            var result = await itsClient.QueryProductStatusAsync(
                environment, credentials, request, cancellationToken);
            if (!result.HasUsableResponse)
            {
                var detail = string.IsNullOrWhiteSpace(result.FaultMessage)
                    ? $"İTS kullanılabilir bir ürün durumu döndürmedi (HTTP {result.HttpStatusCode})."
                    : $"{result.FaultCode} {result.FaultMessage}".Trim();
                return (result, null, detail);
            }

            var item = result.Products.FirstOrDefault(candidate =>
                           candidate.Gtin == requestedProduct.Gtin &&
                           candidate.SerialNumber == requestedProduct.SerialNumber)
                       ?? result.Products[0];
            return (result, ItsStatusOwnershipClassifier.Classify(item, pharmacyGln), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null,
                "İTS durum sorgusu zaman aşımına uğradı. İnternet bağlantısını kontrol edip tekrar deneyin.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, null, exception.Message);
        }
    }

    private static string BuildComparison(
        LocalQrStatus? local,
        string? localError,
        ItsOwnershipAssessment? its,
        string? itsError)
    {
        if (!string.IsNullOrWhiteSpace(localError) || !string.IsNullOrWhiteSpace(itsError))
            return "İki kaynak birlikte doğrulanamadı; ayrıntılardaki bağlantı veya veritabanı hatasını kontrol edin.";
        if (local is null || its is null)
            return "Yerel kayıt ile İTS sonucu karşılaştırılamadı.";
        if (local.Location == LocalQrLocation.Active && its.IsRegisteredToThisPharmacy)
            return "Yerel aktif stok ile İTS sahiplik bilgisi uyumludur.";
        if (local.Location == LocalQrLocation.Hanged && its.State == ItsOwnershipState.OutgoingTransfer &&
            (string.IsNullOrWhiteSpace(local.TransferDestinationGln) ||
             local.TransferDestinationGln == its.DestinationGln))
            return "Yerel Takas hedefi ile İTS sevk bilgisi uyumludur.";
        if (local.Location == LocalQrLocation.Inactive &&
            local.LastMovementCode is "PERAK_OUT" or "PRESC_OUT" &&
            its.State == ItsOwnershipState.SoldToEndConsumer)
            return "Yerel satış kaydı ile İTS sonucu uyumludur.";
        if (!local.ExistsInDatabase && !its.IsRegisteredToThisPharmacy)
            return "Karekod yerel veritabanında yoktur ve İTS üzerinde eczanenize kayıtlı değildir.";
        return "Yerel kayıt ile İTS sonucu aynı durumu göstermiyor; işlem yapmadan önce ayrıntıları kontrol edin.";
    }
}
