using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace pharmacyApp.Modules.Its;

public enum ItsEnvironment
{
    Test,
    Production
}

public enum ItsPharmacyOperation
{
    RetailSale,
    RetailSaleCancellation
}

public sealed record ItsEnvironmentOption(ItsEnvironment Value, string DisplayName);

public sealed record ItsOperationOption(ItsPharmacyOperation Value, string DisplayName);

public sealed record ItsCredentials(string Username, string Password);

public sealed record ItsQrProduct(string Gtin, string SerialNumber, string BatchNumber, DateOnly ExpiryDate)
{
    public string ExpiryDateText => ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed record ItsPharmacyDocument(
    [property: JsonPropertyName("eid")] string EId,
    [property: JsonPropertyName("rkn")] string PrescriptionRecordNumber);

public sealed record ItsNotificationProduct(
    [property: JsonPropertyName("gtin")] string Gtin,
    [property: JsonPropertyName("sn")] string SerialNumber,
    [property: JsonPropertyName("bn")] string BatchNumber,
    [property: JsonPropertyName("xd")] string ExpiryDate);

public sealed record ItsPharmacyNotificationRequest(
    [property: JsonPropertyName("dt")] string NotificationType,
    [property: JsonPropertyName("fr")] string FromGln,
    [property: JsonPropertyName("to")] string ToGln,
    [property: JsonPropertyName("document")] ItsPharmacyDocument Document,
    [property: JsonPropertyName("productList")] IReadOnlyList<ItsNotificationProduct> Products);

public sealed record ItsTransferRequest(
    [property: JsonPropertyName("togln")] string ToGln,
    [property: JsonPropertyName("productList")] IReadOnlyList<ItsNotificationProduct> Products);

public sealed record ItsTransferCancellationRequest(
    [property: JsonPropertyName("productList")] IReadOnlyList<ItsNotificationProduct> Products);

public sealed record ItsAcceptanceRequest(
    [property: JsonPropertyName("productList")] IReadOnlyList<ItsNotificationProduct> Products);

public sealed record ItsReturnRequest(
    [property: JsonPropertyName("togln")] string ToGln,
    [property: JsonPropertyName("productList")] IReadOnlyList<ItsNotificationProduct> Products);

public sealed record ItsMovementDocument(
    [property: JsonPropertyName("dd")] string DocumentDate,
    [property: JsonPropertyName("dn")] string DocumentNumber);

public enum ItsDeactivationReason
{
    SystemRemoval = 10,
    ProductionWaste = 20,
    Recall = 30,
    Expiry = 40,
    Revision = 50,
    Consumption = 60
}

public sealed record ItsDeactivationRequest(
    [property: JsonPropertyName("dt")] string NotificationType,
    [property: JsonPropertyName("frGln")] string FromGln,
    [property: JsonPropertyName("ds")] string ReasonCode,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("document")] ItsMovementDocument Document,
    [property: JsonPropertyName("productList")] IReadOnlyList<ItsNotificationProduct> Products);

public sealed record ItsProductResult(
    string Gtin,
    string SerialNumber,
    string ResultCode,
    string? ResultMessage = null,
    string? ResultDescription = null)
{
    public string ResultDisplay => ItsResponseCodeFormatter.Format(ResultCode, ResultMessage, ResultDescription);
}

public sealed record ItsNotificationResult(
    int HttpStatusCode,
    string ResponseBody,
    string? NotificationId,
    string? FaultCode,
    string? FaultMessage,
    IReadOnlyList<ItsProductResult> Products)
{
    public bool IsSuccessful =>
        HttpStatusCode is >= 200 and <= 299 &&
        string.IsNullOrWhiteSpace(FaultCode) &&
        Products.Count > 0 &&
        Products.All(product => string.Equals(product.ResultCode, "00000", StringComparison.Ordinal));
}

public static class ItsServiceEndpoints
{
    private const string TestBaseAddress = "https://itstest2.saglik.gov.tr";
    private const string ProductionBaseAddress = "https://its2.saglik.gov.tr";

    public static Uri Token(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/token/app/token/");

    public static Uri PharmacyNotification(ItsEnvironment environment, ItsPharmacyOperation operation) =>
        new($"{BaseAddress(environment)}/prescription/app/{(operation == ItsPharmacyOperation.RetailSale ? "pharmacysale" : "pharmacysalecancel")}/");

    public static Uri Transfer(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/common/app/transfer/");

    public static Uri TransferCancellation(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/common/app/transfercancel/");

    public static Uri Acceptance(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/common/app/accept/");

    public static Uri Return(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/common/app/return/");

    public static Uri Deactivation(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/common/app/deactivation/");

    public static Uri DeactivationCancellation(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/common/app/deactivationcancel/");

    public static Uri StatusQuery(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/reference/app/check_status/");

    public static Uri PtsSearch(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/pts/app/search");

    public static Uri PtsGetPackage(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/pts/app/GetPackage");

    public static Uri Stakeholders(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/reference/app/stakeholder/");

    public static Uri ResponseCodes(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/reference/app/errorcode/");

    public static Uri Drugs(ItsEnvironment environment) =>
        new($"{BaseAddress(environment)}/reference/app/drug/");

    private static string BaseAddress(ItsEnvironment environment) =>
        environment == ItsEnvironment.Test ? TestBaseAddress : ProductionBaseAddress;
}

public static class ItsReturnRequestFactory
{
    public static ItsReturnRequest Create(string destinationGln, IReadOnlyList<ItsQrProduct> products)
    {
        destinationGln = destinationGln.Trim();
        if (!Gs1IdentifierValidator.IsValidGln13(destinationGln))
            throw new InvalidOperationException("İade alıcısı GLN kodu 13 haneli ve geçerli bir kontrol basamağına sahip olmalıdır.");
        if (products.Count == 0)
            throw new InvalidOperationException("Mal İade bildirimi için en az bir karekod gereklidir.");

        return new ItsReturnRequest(destinationGln, ToNotificationProducts(products));
    }

    private static IReadOnlyList<ItsNotificationProduct> ToNotificationProducts(IReadOnlyList<ItsQrProduct> products) =>
        products.Select(product => new ItsNotificationProduct(
            product.Gtin,
            product.SerialNumber,
            product.BatchNumber,
            product.ExpiryDateText)).ToArray();
}

public static class ItsDeactivationRequestFactory
{
    public static ItsDeactivationRequest Create(
        string pharmacyGln,
        ItsDeactivationReason reason,
        string description,
        IReadOnlyList<ItsQrProduct> products,
        DateOnly? documentDate = null,
        string? documentNumber = null) =>
        CreateCore("D", pharmacyGln, reason, description, products, documentDate, documentNumber);

    public static ItsDeactivationRequest CreateCancellation(
        string pharmacyGln,
        ItsDeactivationReason reason,
        string description,
        IReadOnlyList<ItsQrProduct> products,
        DateOnly? documentDate = null,
        string? documentNumber = null) =>
        CreateCore("C", pharmacyGln, reason, description, products, documentDate, documentNumber);

    private static ItsDeactivationRequest CreateCore(
        string notificationType,
        string pharmacyGln,
        ItsDeactivationReason reason,
        string description,
        IReadOnlyList<ItsQrProduct> products,
        DateOnly? documentDate,
        string? documentNumber)
    {
        pharmacyGln = pharmacyGln.Trim();
        description = description.Trim();
        if (!Gs1IdentifierValidator.IsValidGln13(pharmacyGln))
            throw new InvalidOperationException("Eczane GLN kodu 13 haneli ve geçerli bir kontrol basamağına sahip olmalıdır.");
        if (!Enum.IsDefined(reason))
            throw new InvalidOperationException("Geçerli bir deaktivasyon nedeni seçilmelidir.");
        if (description.Length == 0)
            throw new InvalidOperationException("Deaktivasyon açıklaması zorunludur.");
        if (products.Count == 0)
            throw new InvalidOperationException("Deaktivasyon bildirimi için en az bir karekod gereklidir.");

        var serializedProducts = products.Select(product => new ItsNotificationProduct(
            product.Gtin,
            product.SerialNumber,
            product.BatchNumber,
            product.ExpiryDateText)).ToArray();
        return new ItsDeactivationRequest(
            notificationType,
            pharmacyGln,
            ((int)reason).ToString(CultureInfo.InvariantCulture),
            description,
            new ItsMovementDocument(
                documentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                documentNumber?.Trim() ?? string.Empty),
            serializedProducts);
    }
}

public static class ItsAcceptanceRequestFactory
{
    public static ItsAcceptanceRequest Create(IReadOnlyList<ItsQrProduct> products)
    {
        if (products.Count == 0)
            throw new InvalidOperationException("Mal Alım bildirimi için en az bir karekod gereklidir.");
        if (products.Any(product => product.ExpiryDate < DateOnly.FromDateTime(DateTime.Today)))
            throw new InvalidOperationException("Son kullanma tarihi geçmiş ürün için Mal Alım bildirimi yapılamaz.");

        return new ItsAcceptanceRequest(
            products.Select(product => new ItsNotificationProduct(
                product.Gtin,
                product.SerialNumber,
                product.BatchNumber,
                product.ExpiryDateText)).ToArray());
    }
}

public static class ItsTransferRequestFactory
{
    public static ItsTransferRequest Create(string destinationGln, IReadOnlyList<ItsQrProduct> products)
    {
        destinationGln = destinationGln.Trim();
        if (!Gs1IdentifierValidator.IsValidGln13(destinationGln))
            throw new InvalidOperationException("Alıcı eczane GLN kodu 13 haneli ve geçerli bir kontrol basamağına sahip olmalıdır.");
        if (products.Count == 0)
            throw new InvalidOperationException("Mal Devir bildirimi için en az bir karekod gereklidir.");
        if (products.Any(product => product.ExpiryDate < DateOnly.FromDateTime(DateTime.Today)))
            throw new InvalidOperationException("Son kullanma tarihi geçmiş ürün Mal Devir bildirimine eklenemez.");

        return new ItsTransferRequest(
            destinationGln,
            products.Select(product => new ItsNotificationProduct(
                product.Gtin,
                product.SerialNumber,
                product.BatchNumber,
                product.ExpiryDateText)).ToArray());
    }
}

public static class ItsTransferCancellationRequestFactory
{
    public static ItsTransferCancellationRequest Create(IReadOnlyList<ItsQrProduct> products)
    {
        if (products.Count == 0)
            throw new InvalidOperationException("Mal Devir İptal bildirimi için en az bir karekod gereklidir.");
        if (products.Any(product => product.ExpiryDate < DateOnly.FromDateTime(DateTime.Today)))
            throw new InvalidOperationException("Son kullanma tarihi geçmiş ürün için Mal Devir İptal bildirimi yapılamaz.");

        return new ItsTransferCancellationRequest(
            products.Select(product => new ItsNotificationProduct(
                product.Gtin,
                product.SerialNumber,
                product.BatchNumber,
                product.ExpiryDateText)).ToArray());
    }
}

public static class ItsPharmacyRequestFactory
{
    public const string EndConsumerGln = "0000000000000";

    public static ItsPharmacyNotificationRequest Create(
        ItsPharmacyOperation operation,
        string pharmacyGln,
        ItsQrProduct product) =>
        Create(operation, pharmacyGln, [product]);

    public static ItsPharmacyNotificationRequest Create(
        ItsPharmacyOperation operation,
        string pharmacyGln,
        IReadOnlyList<ItsQrProduct> products)
    {
        pharmacyGln = pharmacyGln.Trim();
        if (!Gs1IdentifierValidator.IsValidGln13(pharmacyGln))
            throw new InvalidOperationException("Eczane GLN kodu 13 haneli ve geçerli bir kontrol basamağına sahip olmalıdır.");
        if (products.Count == 0)
            throw new InvalidOperationException("İTS bildirimi için en az bir karekod gereklidir.");
        if (products.Any(product => product.ExpiryDate < DateOnly.FromDateTime(DateTime.Today)))
            throw new InvalidOperationException("Karekodların son kullanma tarihi bildirim tarihinden önce olamaz.");

        return new ItsPharmacyNotificationRequest(
            operation == ItsPharmacyOperation.RetailSale ? "S" : "C",
            pharmacyGln,
            EndConsumerGln,
            new ItsPharmacyDocument(string.Empty, string.Empty),
            products.Select(product => new ItsNotificationProduct(
                product.Gtin,
                product.SerialNumber,
                product.BatchNumber,
                product.ExpiryDateText)).ToArray());
    }
}

public static class ItsQrParser
{
    private const char GroupSeparator = (char)29;
    private static readonly Regex ParenthesizedElement = new(
        @"\((?<ai>01|17|10|21)\)(?<value>.*?)(?=\((?:01|17|10|21)\)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static bool TryParse(string? enteredValue, out ItsQrProduct? product, out string error)
    {
        product = null;
        error = string.Empty;
        var value = NormalizeScannerValue(enteredValue);
        if (value.Length == 0)
        {
            error = "Karekod alanı boş bırakılamaz.";
            return false;
        }

        Dictionary<string, string> elements;
        if (value.Contains("(01)", StringComparison.Ordinal))
        {
            if (!TryParseParenthesized(value, out elements, out error)) return false;
        }
        else
        {
            if (!TryParseRaw(value, out elements, out error)) return false;
        }

        if (!TryGetRequired(elements, "01", "GTIN (01)", out var gtin, out error) ||
            !TryGetRequired(elements, "21", "sıra numarası (21)", out var serialNumber, out error) ||
            !TryGetRequired(elements, "10", "parti numarası (10)", out var batchNumber, out error) ||
            !TryGetRequired(elements, "17", "son kullanma tarihi (17)", out var encodedExpiry, out error))
            return false;

        if (!Gs1IdentifierValidator.IsValidGtin14(gtin))
        {
            error = "GTIN (01) 14 haneli ve geçerli bir kontrol basamağına sahip olmalıdır.";
            return false;
        }
        if (serialNumber.Length > 20)
        {
            error = "Sıra numarası (21) en fazla 20 karakter olabilir.";
            return false;
        }
        if (batchNumber.Length > 20)
        {
            error = "Parti numarası (10) en fazla 20 karakter olabilir.";
            return false;
        }
        if (!TryParseExpiry(encodedExpiry, out var expiryDate))
        {
            error = "Son kullanma tarihi (17) YYAAGG biçiminde geçerli bir tarih olmalıdır.";
            return false;
        }

        product = new ItsQrProduct(gtin, serialNumber, batchNumber, expiryDate);
        return true;
    }

    private static string NormalizeScannerValue(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.StartsWith("]d2", StringComparison.OrdinalIgnoreCase)) normalized = normalized[3..];
        return normalized
            .Replace("<GS>", GroupSeparator.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("[GS]", GroupSeparator.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace('\u241D', GroupSeparator);
    }

    private static bool TryParseParenthesized(
        string value,
        out Dictionary<string, string> elements,
        out string error)
    {
        elements = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        var matches = ParenthesizedElement.Matches(value);
        if (matches.Count == 0)
        {
            error = "GS1 uygulama tanımlayıcıları okunamadı.";
            return false;
        }

        foreach (Match match in matches)
        {
            var ai = match.Groups["ai"].Value;
            var elementValue = match.Groups["value"].Value.Trim().Trim(GroupSeparator);
            if (!TryAddElement(elements, ai, elementValue, out error)) return false;
        }
        return true;
    }

    private static bool TryParseRaw(
        string value,
        out Dictionary<string, string> elements,
        out string error)
    {
        if (!value.Contains(GroupSeparator) && LooksLikeSeparatorFreeItsCode(value))
            return TryParseSeparatorFreeItsCode(value, out elements, out error);

        elements = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        var index = 0;
        while (index < value.Length)
        {
            if (value[index] == GroupSeparator)
            {
                index++;
                continue;
            }
            if (index + 2 > value.Length)
            {
                error = "Karekodun sonunda eksik bir GS1 uygulama tanımlayıcısı var.";
                return false;
            }

            var ai = value.Substring(index, 2);
            index += 2;
            string elementValue;
            switch (ai)
            {
                case "01":
                    if (!TryReadFixed(value, ref index, 14, ai, out elementValue, out error)) return false;
                    break;
                case "17":
                    if (!TryReadFixed(value, ref index, 6, ai, out elementValue, out error)) return false;
                    break;
                case "10":
                case "21":
                    var separatorIndex = value.IndexOf(GroupSeparator, index);
                    if (separatorIndex < 0)
                    {
                        elementValue = value[index..];
                        index = value.Length;
                    }
                    else
                    {
                        elementValue = value[index..separatorIndex];
                        index = separatorIndex + 1;
                    }
                    break;
                default:
                    error = $"Desteklenmeyen veya beklenmeyen GS1 uygulama tanımlayıcısı: {ai}.";
                    return false;
            }

            if (!TryAddElement(elements, ai, elementValue, out error)) return false;
        }
        return true;
    }

    private static bool LooksLikeSeparatorFreeItsCode(string value) =>
        value.Length >= 18 &&
        value.StartsWith("01", StringComparison.Ordinal) &&
        value.AsSpan(16, 2).SequenceEqual("21");

    private static bool TryParseSeparatorFreeItsCode(
        string value,
        out Dictionary<string, string> elements,
        out string error)
    {
        elements = new Dictionary<string, string>(StringComparer.Ordinal);
        const int gtinStart = 2;
        const int serialStart = 18;
        const int fixedSuffixLength = 2 + 6 + 2;

        if (value.Length < serialStart + 1 + fixedSuffixLength + 1 ||
            !value.AsSpan(gtinStart, 14).ToString().All(char.IsAsciiDigit))
        {
            error = "Ayıraçsız İTS karekodu geçersiz. Beklenen sıra: 01 + 14 haneli GTIN, 21 + sıra numarası, 17 + 6 haneli SKT ve 10 + parti numarasıdır.";
            return false;
        }

        var candidates = new List<(string Serial, string Expiry, string Batch)>();
        var maximumSerialLength = Math.Min(20, value.Length - serialStart - fixedSuffixLength - 1);
        for (var serialLength = 1; serialLength <= maximumSerialLength; serialLength++)
        {
            var expiryAiStart = serialStart + serialLength;
            var expiryStart = expiryAiStart + 2;
            var batchAiStart = expiryStart + 6;
            var batchStart = batchAiStart + 2;
            var batchLength = value.Length - batchStart;
            var serial = value.Substring(serialStart, serialLength);
            if (!value.AsSpan(expiryAiStart, 2).SequenceEqual("17") ||
                !serial.All(char.IsAsciiLetterOrDigit) ||
                !value.AsSpan(expiryStart, 6).ToString().All(char.IsAsciiDigit) ||
                !TryParseExpiry(value.Substring(expiryStart, 6), out _) ||
                !value.AsSpan(batchAiStart, 2).SequenceEqual("10") ||
                batchLength is < 1 or > 20)
                continue;

            candidates.Add((
                serial,
                value.Substring(expiryStart, 6),
                value[batchStart..]));
        }

        if (candidates.Count != 1)
        {
            error = candidates.Count == 0
                ? "Ayıraçsız İTS karekodunda sıra numarası, SKT ve parti alanları güvenli biçimde ayrılamadı."
                : "Ayıraçsız İTS karekodu birden fazla şekilde yorumlanabiliyor. Değişken alanlar arasında FNC1/GS ayıracı bulunmalıdır.";
            return false;
        }

        var candidate = candidates[0];
        elements["01"] = value.Substring(gtinStart, 14);
        elements["21"] = candidate.Serial;
        elements["17"] = candidate.Expiry;
        elements["10"] = candidate.Batch;
        error = string.Empty;
        return true;
    }

    private static bool TryReadFixed(
        string value,
        ref int index,
        int length,
        string ai,
        out string elementValue,
        out string error)
    {
        elementValue = string.Empty;
        error = string.Empty;
        if (index + length > value.Length)
        {
            error = $"GS1 ({ai}) alanı beklenen {length} karakterden kısa.";
            return false;
        }
        elementValue = value.Substring(index, length);
        index += length;
        return true;
    }

    private static bool TryAddElement(
        IDictionary<string, string> elements,
        string ai,
        string value,
        out string error)
    {
        error = string.Empty;
        value = value.Trim().Trim(GroupSeparator);
        if (value.Length == 0)
        {
            error = $"GS1 ({ai}) alanı boş olamaz.";
            return false;
        }
        if (!elements.TryAdd(ai, value))
        {
            error = $"GS1 ({ai}) alanı karekodda birden fazla kez bulunuyor.";
            return false;
        }
        return true;
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string> elements,
        string ai,
        string displayName,
        out string value,
        out string error)
    {
        if (elements.TryGetValue(ai, out var found) && !string.IsNullOrWhiteSpace(found))
        {
            value = found.Trim();
            error = string.Empty;
            return true;
        }
        value = string.Empty;
        error = $"Karekodda {displayName} bulunamadı. Değişken alanlar arasında FNC1/GS ayıracı bulunmalıdır.";
        return false;
    }

    private static bool TryParseExpiry(string encoded, out DateOnly expiryDate)
    {
        expiryDate = default;
        if (encoded.Length != 6 || !encoded.All(char.IsAsciiDigit)) return false;
        var year = 2000 + int.Parse(encoded.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(encoded.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(encoded.AsSpan(4, 2), CultureInfo.InvariantCulture);
        if (month is < 1 or > 12) return false;
        if (day == 0) day = DateTime.DaysInMonth(year, month);
        return DateOnly.TryParseExact(
            $"{year:0000}-{month:00}-{day:00}",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out expiryDate);
    }
}

public static class Gs1IdentifierValidator
{
    public static bool IsValidGtin14(string value) => IsValidNumericIdentifier(value, 14);

    public static bool IsValidGln13(string value) => IsValidNumericIdentifier(value, 13);

    private static bool IsValidNumericIdentifier(string value, int requiredLength)
    {
        if (value.Length != requiredLength || !value.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var index = 0; index < value.Length - 1; index++)
        {
            var digit = value[index] - '0';
            var positionFromRight = value.Length - 1 - index;
            sum += digit * (positionFromRight % 2 == 1 ? 3 : 1);
        }
        var expectedCheckDigit = (10 - sum % 10) % 10;
        return value[^1] - '0' == expectedCheckDigit;
    }
}
