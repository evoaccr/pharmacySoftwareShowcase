using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace pharmacyApp.Modules.Its;

public sealed class ItsNotificationNotSentException(string message, Exception innerException)
    : Exception(message, innerException);

public interface IItsPharmacyNotificationClient
{
    Task<ItsNotificationResult> SendNotificationAsync(
        ItsEnvironment environment,
        ItsPharmacyOperation operation,
        ItsCredentials credentials,
        ItsPharmacyNotificationRequest request,
        CancellationToken cancellationToken);
}

public interface IItsProductStatusClient
{
    Task<ItsStatusQueryResult> QueryProductStatusAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsStatusQueryRequest request,
        CancellationToken cancellationToken);
}

public interface IItsPharmacyStockMovementClient
{
    Task<ItsNotificationResult> SendTransferAsync(
        ItsEnvironment environment, ItsCredentials credentials, ItsTransferRequest request,
        CancellationToken cancellationToken);
    Task<ItsNotificationResult> SendTransferCancellationAsync(
        ItsEnvironment environment, ItsCredentials credentials, ItsTransferCancellationRequest request,
        CancellationToken cancellationToken);
    Task<ItsNotificationResult> SendAcceptanceAsync(
        ItsEnvironment environment, ItsCredentials credentials, ItsAcceptanceRequest request,
        CancellationToken cancellationToken);
    Task<ItsNotificationResult> SendReturnAsync(
        ItsEnvironment environment, ItsCredentials credentials, ItsReturnRequest request,
        CancellationToken cancellationToken);
    Task<ItsNotificationResult> SendDeactivationAsync(
        ItsEnvironment environment, ItsCredentials credentials, ItsDeactivationRequest request,
        CancellationToken cancellationToken);
    Task<ItsNotificationResult> SendDeactivationCancellationAsync(
        ItsEnvironment environment, ItsCredentials credentials, ItsDeactivationRequest request,
        CancellationToken cancellationToken);
}

public interface IItsReferenceDataClient
{
    Task<IReadOnlyList<ItsResponseCode>> GetResponseCodesAsync(
        ItsEnvironment environment, ItsCredentials credentials, CancellationToken cancellationToken);
    Task<IReadOnlyList<ItsDrug>> GetDrugsAsync(
        ItsEnvironment environment, ItsCredentials credentials, bool includeInactive,
        CancellationToken cancellationToken);
}

public interface IItsPtsClient
{
    Task<IReadOnlyList<ItsPtsTransfer>> SearchPtsTransfersAsync(
        ItsEnvironment environment, ItsCredentials credentials, ItsPtsSearchRequest request,
        CancellationToken cancellationToken);
    Task<ItsPtsPackageResult> GetPtsPackageAsync(
        ItsEnvironment environment, ItsCredentials credentials, long transferId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ItsStakeholder>> GetStakeholdersAsync(
        ItsEnvironment environment, ItsCredentials credentials, int cityPlate,
        CancellationToken cancellationToken);
}

public sealed class ItsApiClient(HttpClient? httpClient = null)
    : IItsPharmacyNotificationClient, IItsProductStatusClient, IItsPharmacyStockMovementClient,
      IItsReferenceDataClient, IItsPtsClient
{
    private const int NameResolutionAttemptCount = 3;
    private const int ReadOnlyStatusAttemptCount = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient client = httpClient ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly SemaphoreSlim responseCodeCacheGate = new(1, 1);
    private readonly ConcurrentDictionary<ItsEnvironment, ResponseCodeCacheEntry> responseCodeCache = [];

    public async Task TestConnectionAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        CancellationToken cancellationToken)
    {
        _ = await AcquireTokenAsync(environment, credentials, cancellationToken);
    }

    public async Task<ItsNotificationResult> SendNotificationAsync(
        ItsEnvironment environment,
        ItsPharmacyOperation operation,
        ItsCredentials credentials,
        ItsPharmacyNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var operationName = operation == ItsPharmacyOperation.RetailSale
            ? "Elden Perakende Satış"
            : "Elden Perakende Satış İptali";
        string token;
        try
        {
            token = await AcquireTokenAsync(environment, credentials, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new ItsNotificationNotSentException(
                $"İTS access token isteği iptal edildi; {operationName} bildirimi gönderilmedi.", exception);
        }
        catch (Exception exception) when (exception is not ItsNotificationNotSentException)
        {
            throw new ItsNotificationNotSentException(
                $"İTS access token alınamadı; {operationName} bildirimi gönderilmedi. {exception.Message}", exception);
        }

        HttpResponseMessage response;
        try
        {
            response = await SendWithNameResolutionRetryAsync(() =>
            {
                var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    ItsServiceEndpoints.PharmacyNotification(environment, operation));
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                message.Content = new StringContent(
                    JsonSerializer.Serialize(request, JsonOptions),
                    Encoding.UTF8,
                    "application/json");
                return message;
            }, cancellationToken);
        }
        catch (HttpRequestException exception) when (IsDefinitelyNotSent(exception))
        {
            throw new ItsNotificationNotSentException(
                $"İTS sunucusuna bağlantı kurulamadı; {operationName} bildirimi gönderilmedi. {exception.Message}", exception);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return await EnrichNotificationResultAsync(
                environment,
                credentials,
                ParseNotificationResult((int)response.StatusCode, responseBody));
        }
    }

    public async Task<ItsNotificationResult> SendTransferAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsTransferRequest request,
        CancellationToken cancellationToken)
    {
        return await SendTransferOperationAsync(
            environment,
            credentials,
            ItsServiceEndpoints.Transfer(environment),
            request,
            "Mal Devir",
            cancellationToken);
    }

    public async Task<ItsNotificationResult> SendTransferCancellationAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsTransferCancellationRequest request,
        CancellationToken cancellationToken)
    {
        return await SendTransferOperationAsync(
            environment,
            credentials,
            ItsServiceEndpoints.TransferCancellation(environment),
            request,
            "Mal Devir İptal",
            cancellationToken);
    }

    public async Task<ItsNotificationResult> SendAcceptanceAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        return await SendTransferOperationAsync(
            environment,
            credentials,
            ItsServiceEndpoints.Acceptance(environment),
            request,
            "Mal Alım",
            cancellationToken);
    }

    public async Task<ItsNotificationResult> SendReturnAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsReturnRequest request,
        CancellationToken cancellationToken)
    {
        return await SendTransferOperationAsync(
            environment,
            credentials,
            ItsServiceEndpoints.Return(environment),
            request,
            "Mal İade",
            cancellationToken);
    }

    public async Task<ItsNotificationResult> SendDeactivationAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsDeactivationRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.NotificationType, "D", StringComparison.Ordinal))
            throw new InvalidOperationException("Deaktivasyon bildiriminin dt alanı D olmalıdır.");
        return await SendTransferOperationAsync(
            environment,
            credentials,
            ItsServiceEndpoints.Deactivation(environment),
            request,
            "Deaktivasyon",
            cancellationToken);
    }

    public async Task<ItsNotificationResult> SendDeactivationCancellationAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsDeactivationRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.NotificationType, "C", StringComparison.Ordinal))
            throw new InvalidOperationException("Deaktivasyon İptal bildiriminin dt alanı C olmalıdır.");
        return await SendTransferOperationAsync(
            environment,
            credentials,
            ItsServiceEndpoints.DeactivationCancellation(environment),
            request,
            "Deaktivasyon İptal",
            cancellationToken);
    }

    public async Task<ItsStatusQueryResult> QueryProductStatusAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsStatusQueryRequest request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await QueryProductStatusOnceAsync(
                    environment, credentials, request, cancellationToken);
                return await EnrichStatusQueryResultAsync(environment, credentials, result);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                attempt < ReadOnlyStatusAttemptCount)
            {
                await DelayReadOnlyRetryAsync(attempt, cancellationToken);
            }
            catch (Exception exception) when (
                (exception is HttpRequestException or ItsTransientResponseException) &&
                attempt < ReadOnlyStatusAttemptCount)
            {
                await DelayReadOnlyRetryAsync(attempt, cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyList<ItsPtsTransfer>> SearchPtsTransfersAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsPtsSearchRequest request,
        CancellationToken cancellationToken)
    {
        var root = await PostReadOnlyJsonAsync(
            environment, credentials, ItsServiceEndpoints.PtsSearch(environment), request,
            "PTS paket araması", cancellationToken);
        if (!TryGetProperty(root, out var list, "transferDetails") || list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray().Select(item => new ItsPtsTransfer(
            TryGetString(item, "sourceGln") ?? string.Empty,
            TryGetString(item, "destinationGln") ?? string.Empty,
            long.TryParse(TryGetString(item, "transferId"), out var id) ? id : 0,
            DateTimeOffset.TryParse(TryGetString(item, "transferDate"), out var date) ? date : null))
            .Where(item => item.TransferId > 0)
            .ToArray();
    }

    public async Task<ItsPtsPackageResult> GetPtsPackageAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        long transferId,
        CancellationToken cancellationToken)
    {
        var root = await PostReadOnlyJsonAsync(
            environment, credentials, ItsServiceEndpoints.PtsGetPackage(environment), new { transferId },
            "PTS paket indirme", cancellationToken);
        var encoded = TryGetString(root, "fileStream", "filestream") ?? string.Empty;
        if (encoded.Length == 0) throw new InvalidDataException($"PTS {transferId} paketinde fileStream bulunamadı.");
        try { return new ItsPtsPackageResult(transferId, Convert.FromBase64String(encoded)); }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"PTS {transferId} paketindeki fileStream Base64 biçiminde değil.", exception);
        }
    }

    public async Task<IReadOnlyList<ItsStakeholder>> GetStakeholdersAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        int cityPlate,
        CancellationToken cancellationToken)
    {
        var root = await PostReadOnlyJsonAsync(
            environment, credentials, ItsServiceEndpoints.Stakeholders(environment),
            new { stakeholderType = "depo", getAll = true, cityPlate },
            "İTS paydaş araması", cancellationToken);
        if (!TryGetProperty(root, out var list, "companyList") || list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray().Select(item => new ItsStakeholder(
                TryGetString(item, "gln") ?? string.Empty,
                TryGetString(item, "companyName") ?? string.Empty))
            .Where(item => item.Gln.Length == 13 && item.CompanyName.Length > 0)
            .ToArray();
    }

    public async Task<IReadOnlyList<ItsResponseCode>> GetResponseCodesAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedResponseCodes(environment, out var cached)) return cached;

        await responseCodeCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedResponseCodes(environment, out cached)) return cached;
            var root = await PostReadOnlyJsonAsync(
                environment,
                credentials,
                ItsServiceEndpoints.ResponseCodes(environment),
                new Dictionary<string, string>(),
                "İTS cevap kodu listesi",
                cancellationToken);
            var result = ParseResponseCodes(root);
            responseCodeCache[environment] = new ResponseCodeCacheEntry(DateTimeOffset.UtcNow, result);
            return result;
        }
        finally
        {
            responseCodeCacheGate.Release();
        }
    }

    public async Task<IReadOnlyList<ItsDrug>> GetDrugsAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var root = await PostReadOnlyJsonAsync(
            environment,
            credentials,
            ItsServiceEndpoints.Drugs(environment),
            new { getAll = includeInactive },
            "İTS ilaç listesi",
            cancellationToken);
        if (!TryGetProperty(root, out var list, "drugList", "drug_list") || list.ValueKind != JsonValueKind.Array)
            return [];

        return list.EnumerateArray().Select(item => new ItsDrug(
                TryGetString(item, "gtin") ?? string.Empty,
                TryGetString(item, "drugName", "drugname") ?? string.Empty,
                TryGetString(item, "manufacturerGln", "manufacturergln") ?? string.Empty,
                TryGetString(item, "manufacturerName", "manufacturername") ?? string.Empty,
                TryGetBoolean(item, "active", "acitve"),
                TryGetBoolean(item, "imported")))
            .Where(item => item.Gtin.Length > 0)
            .ToArray();
    }

    private async Task<JsonElement> PostReadOnlyJsonAsync<TRequest>(
        ItsEnvironment environment,
        ItsCredentials credentials,
        Uri endpoint,
        TRequest request,
        string operationName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var token = await AcquireTokenAsync(environment, credentials, cancellationToken);
                using var response = await SendWithNameResolutionRetryAsync(() =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    message.Content = JsonContent.Create(request, options: JsonOptions);
                    return message;
                }, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransientStatusCode(response.StatusCode))
                        throw new HttpRequestException(
                            $"{operationName} geçici bir HTTP {(int)response.StatusCode} yanıtı verdi.",
                            null,
                            response.StatusCode);
                    throw CreateApiException(operationName, (int)response.StatusCode, body);
                }
                using var json = JsonDocument.Parse(body);
                return json.RootElement.Clone();
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested && attempt < ReadOnlyStatusAttemptCount)
            {
                await DelayReadOnlyRetryAsync(attempt, cancellationToken);
            }
        }
    }

    private async Task<ItsStatusQueryResult> QueryProductStatusOnceAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsStatusQueryRequest request,
        CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(environment, credentials, cancellationToken);
        using var response = await SendWithNameResolutionRetryAsync(() =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, ItsServiceEndpoints.StatusQuery(environment));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json");
            return message;
        }, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (IsTransientStatusCode(response.StatusCode))
            throw new ItsTransientResponseException(
                $"İTS durum sorgusu geçici bir sunucu yanıtı verdi (HTTP {(int)response.StatusCode}).");
        return ParseStatusQueryResult((int)response.StatusCode, responseBody);
    }

    private async Task<ItsNotificationResult> SendTransferOperationAsync<TRequest>(
        ItsEnvironment environment,
        ItsCredentials credentials,
        Uri endpoint,
        TRequest request,
        string operationName,
        CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await AcquireTokenAsync(environment, credentials, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ItsNotificationNotSentException(
                $"İTS access token isteği zaman aşımına uğradı; {operationName} bildirimi gönderilmedi.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ItsNotificationNotSentException(
                $"İTS access token alınamadı; {operationName} bildirimi gönderilmedi. {exception.Message}", exception);
        }

        HttpResponseMessage response;
        try
        {
            response = await SendWithNameResolutionRetryAsync(() =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                message.Content = new StringContent(
                    JsonSerializer.Serialize(request, JsonOptions),
                    Encoding.UTF8,
                    "application/json");
                return message;
            }, cancellationToken);
        }
        catch (HttpRequestException exception) when (IsDefinitelyNotSent(exception))
        {
            throw new ItsNotificationNotSentException(
                $"İTS sunucusuna bağlantı kurulamadı; {operationName} bildirimi gönderilmedi. {exception.Message}", exception);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return await EnrichNotificationResultAsync(
                environment,
                credentials,
                ParseNotificationResult((int)response.StatusCode, responseBody));
        }
    }

    private bool TryGetCachedResponseCodes(
        ItsEnvironment environment,
        out IReadOnlyList<ItsResponseCode> codes)
    {
        if (responseCodeCache.TryGetValue(environment, out var entry) &&
            DateTimeOffset.UtcNow - entry.LoadedAt < TimeSpan.FromHours(24))
        {
            codes = entry.Codes;
            return true;
        }
        codes = [];
        return false;
    }

    private async Task<ItsNotificationResult> EnrichNotificationResultAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsNotificationResult result)
    {
        var codes = await TryGetResponseCodeMapAsync(
            environment,
            credentials,
            result.Products.Select(product => product.ResultCode).Append(result.FaultCode));
        var products = result.Products.Select(product =>
        {
            if (codes.TryGetValue(product.ResultCode, out var code))
                return product with { ResultMessage = code.Message, ResultDescription = code.Description };
            return product.ResultCode == "00000"
                ? product with { ResultMessage = "İşlem başarılı." }
                : product;
        }).ToArray();

        var faultMessage = result.FaultMessage;
        if (string.IsNullOrWhiteSpace(faultMessage) && !string.IsNullOrWhiteSpace(result.FaultCode) &&
            codes.TryGetValue(result.FaultCode, out var fault))
            faultMessage = fault.Message;
        return result with { FaultMessage = faultMessage, Products = products };
    }

    private async Task<ItsStatusQueryResult> EnrichStatusQueryResultAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsStatusQueryResult result)
    {
        var codes = await TryGetResponseCodeMapAsync(
            environment,
            credentials,
            result.Products.Select(product => product.ResultCode).Append(result.FaultCode));
        var products = result.Products.Select(product =>
        {
            if (codes.TryGetValue(product.ResultCode, out var code))
                return product with { ResultMessage = code.Message, ResultDescription = code.Description };
            return product.ResultCode == "00000"
                ? product with { ResultMessage = "İşlem başarılı." }
                : product;
        }).ToArray();

        var faultMessage = result.FaultMessage;
        if (string.IsNullOrWhiteSpace(faultMessage) && !string.IsNullOrWhiteSpace(result.FaultCode) &&
            codes.TryGetValue(result.FaultCode, out var fault))
            faultMessage = fault.Message;
        return result with { FaultMessage = faultMessage, Products = products };
    }

    private async Task<IReadOnlyDictionary<string, ItsResponseCode>> TryGetResponseCodeMapAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        IEnumerable<string?> requestedCodes)
    {
        var needsLookup = requestedCodes.Any(code =>
            !string.IsNullOrWhiteSpace(code) && code != "00000");
        if (!needsLookup) return new Dictionary<string, ItsResponseCode>();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var codes = await GetResponseCodesAsync(environment, credentials, timeout.Token);
            return codes
                .Where(code => code.Code.Length > 0)
                .GroupBy(code => code.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Reference-data enrichment must never change the outcome classification of a movement.
            return new Dictionary<string, ItsResponseCode>();
        }
    }

    private static IReadOnlyList<ItsResponseCode> ParseResponseCodes(JsonElement root)
    {
        if (!TryGetProperty(root, out var list, "errorCodeList", "error_code_list") ||
            list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray().Select(item => new ItsResponseCode(
                TryGetString(item, "code") ?? string.Empty,
                TryGetString(item, "message") ?? string.Empty,
                TryGetString(item, "description") ?? string.Empty))
            .Where(item => item.Code.Length > 0)
            .ToArray();
    }

    private async Task<string> AcquireTokenAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
            throw new InvalidOperationException("İTS kullanıcı adı ve şifre alanları zorunludur.");

        using var response = await SendWithNameResolutionRetryAsync(() =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, ItsServiceEndpoints.Token(environment));
            message.Content = JsonContent.Create(
                new TokenRequest(credentials.Username.Trim(), credentials.Password),
                options: JsonOptions);
            return message;
        }, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (IsTransientStatusCode(response.StatusCode))
                throw new ItsTransientResponseException(
                    $"İTS access token servisi geçici bir yanıt verdi (HTTP {(int)response.StatusCode}).");
            throw CreateApiException("İTS access token alınamadı", (int)response.StatusCode, responseBody);
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (TryGetString(document.RootElement, "token") is { Length: > 0 } token) return token;
            var faultCode = TryGetString(document.RootElement, "fc", "faultCode");
            var faultMessage = TryGetString(document.RootElement, "fm", "faultMessage");
            if (!string.IsNullOrWhiteSpace(faultCode) || !string.IsNullOrWhiteSpace(faultMessage))
                throw new InvalidOperationException($"İTS access token alınamadı: {faultCode} {faultMessage}".Trim());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("İTS access token cevabı geçerli JSON değil.", exception);
        }
        throw new InvalidOperationException("İTS access token cevabında token alanı bulunamadı.");
    }

    private async Task<HttpResponseMessage> SendWithNameResolutionRetryAsync(
        Func<HttpRequestMessage> createMessage,
        CancellationToken cancellationToken)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireItsApi();
        for (var attempt = 1; ; attempt++)
        {
            using var message = createMessage();
            try
            {
                return await client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken);
            }
            catch (HttpRequestException exception) when (
                exception.HttpRequestError == HttpRequestError.NameResolutionError &&
                attempt < NameResolutionAttemptCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsDefinitelyNotSent(HttpRequestException exception) =>
        exception.HttpRequestError is
            HttpRequestError.NameResolutionError or
            HttpRequestError.ConnectionError or
            HttpRequestError.SecureConnectionError or
            HttpRequestError.ProxyTunnelError;

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static Task DelayReadOnlyRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);

    private static ItsNotificationResult ParseNotificationResult(int statusCode, string responseBody)
    {
        string? notificationId = null;
        string? faultCode = null;
        string? faultMessage = null;
        var products = new List<ItsProductResult>();
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            notificationId = TryGetString(root, "notificationid", "notification_id", "notificationId");
            faultCode = TryGetString(root, "fc", "faultCode");
            faultMessage = TryGetString(root, "fm", "faultMessage");
            if (TryGetProperty(root, out var productList, "productList", "product_list") &&
                productList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in productList.EnumerateArray())
                {
                    products.Add(new ItsProductResult(
                        TryGetString(item, "gtin") ?? string.Empty,
                        TryGetString(item, "sn") ?? string.Empty,
                        TryGetString(item, "uc") ?? string.Empty));
                }
            }
        }
        catch (JsonException)
        {
            if (statusCode is >= 200 and <= 299)
                faultMessage = "İTS cevabı geçerli JSON değil.";
        }
        return new ItsNotificationResult(
            statusCode,
            responseBody,
            notificationId,
            faultCode,
            faultMessage,
            products);
    }

    private static ItsStatusQueryResult ParseStatusQueryResult(int statusCode, string responseBody)
    {
        string? notificationId = null;
        string? faultCode = null;
        string? faultMessage = null;
        var products = new List<ItsStatusProductResult>();
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            notificationId = TryGetString(root, "notificationid", "notification_id", "notificationId");
            faultCode = TryGetString(root, "fc", "faultCode");
            faultMessage = TryGetString(root, "fm", "faultMessage");
            if (TryGetProperty(root, out var productList, "responseObjectList", "response_object_list") &&
                productList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in productList.EnumerateArray())
                {
                    products.Add(new ItsStatusProductResult(
                        TryGetString(item, "gln1", "Gln1") ?? string.Empty,
                        TryGetString(item, "gln2", "Gln2") ?? string.Empty,
                        TryGetString(item, "gtin") ?? string.Empty,
                        TryGetString(item, "sn") ?? string.Empty,
                        TryGetString(item, "uc") ?? string.Empty));
                }
            }
        }
        catch (JsonException)
        {
            if (statusCode is >= 200 and <= 299)
                faultMessage = "İTS durum sorgulama cevabı geçerli JSON değil.";
        }

        return new ItsStatusQueryResult(
            statusCode,
            responseBody,
            notificationId,
            faultCode,
            faultMessage,
            products);
    }

    private static Exception CreateApiException(string prefix, int statusCode, string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var faultCode = TryGetString(document.RootElement, "fc", "faultCode");
            var faultMessage = TryGetString(document.RootElement, "fm", "faultMessage");
            if (!string.IsNullOrWhiteSpace(faultCode) || !string.IsNullOrWhiteSpace(faultMessage))
                return new InvalidOperationException($"{prefix} (HTTP {statusCode}): {faultCode} {faultMessage}".Trim());
        }
        catch (JsonException)
        {
        }
        return new InvalidOperationException($"{prefix} (HTTP {statusCode}).");
    }

    private static string? TryGetString(JsonElement element, params string[] propertyNames)
    {
        if (!TryGetProperty(element, out var property, propertyNames)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetBoolean(JsonElement element, params string[] propertyNames)
    {
        if (!TryGetProperty(element, out var property, propertyNames)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var boolean)
                ? boolean
                : property.GetString() == "1",
            _ => false
        };
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement property, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            foreach (var propertyName in propertyNames)
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
        }
        property = default;
        return false;
    }

    private sealed record TokenRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("username")] string Username,
        [property: System.Text.Json.Serialization.JsonPropertyName("password")] string Password);

    private sealed record ResponseCodeCacheEntry(
        DateTimeOffset LoadedAt,
        IReadOnlyList<ItsResponseCode> Codes);

    private sealed class ItsTransientResponseException(string message) : Exception(message);
}
