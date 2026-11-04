using System.Text.Json.Serialization;

namespace pharmacyApp.Modules.Its;

public sealed record ItsStatusQueryRequest(
    [property: JsonPropertyName("productList")] IReadOnlyList<ItsNotificationProduct> Products);

public sealed record ItsStatusProductResult(
    string Gln1,
    string Gln2,
    string Gtin,
    string SerialNumber,
    string ResultCode,
    string? ResultMessage = null,
    string? ResultDescription = null)
{
    public string ResultDisplay => ItsResponseCodeFormatter.Format(ResultCode, ResultMessage, ResultDescription);
}

public sealed record ItsStatusQueryResult(
    int HttpStatusCode,
    string ResponseBody,
    string? NotificationId,
    string? FaultCode,
    string? FaultMessage,
    IReadOnlyList<ItsStatusProductResult> Products)
{
    public bool HasUsableResponse =>
        HttpStatusCode is >= 200 and <= 299 &&
        string.IsNullOrWhiteSpace(FaultCode) &&
        Products.Count > 0;
}

public static class ItsStatusQueryRequestFactory
{
    public static ItsStatusQueryRequest Create(ItsQrProduct product) =>
        new([
            new ItsNotificationProduct(
                product.Gtin,
                product.SerialNumber,
                product.BatchNumber,
                product.ExpiryDateText)
        ]);
}

public enum ItsOwnershipState
{
    RegisteredToThisPharmacy,
    IncomingTransfer,
    OutgoingTransfer,
    SoldToEndConsumer,
    RegisteredToAnotherStakeholder,
    TransferBetweenOtherStakeholders,
    Deactivated,
    AtManufacturer,
    AtWholesaler,
    Exported,
    Consumed,
    Recalled,
    AtConsumptionCenter,
    PhaseOne,
    Unknown
}

public sealed record ItsOwnershipAssessment(
    ItsOwnershipState State,
    string Summary,
    bool IsRegisteredToThisPharmacy,
    string? DestinationGln,
    string? SourceGln);

public static class ItsStatusOwnershipClassifier
{
    public static ItsOwnershipAssessment Classify(ItsStatusProductResult result, string pharmacyGln)
    {
        pharmacyGln = pharmacyGln.Trim();
        var gln1 = result.Gln1.Trim();
        var gln2 = result.Gln2.Trim();
        var resultCode = result.ResultCode.Trim();

        // Durum Sorgulama returns both ownership routing (GLN1/GLN2) and the
        // actual product state (uc). A terminal uc such as 60004 must win over
        // GLN1 because İTS can keep the selling pharmacy as the last owner.
        var codedAssessment = ClassifyResultCode(resultCode, pharmacyGln, gln1, gln2);
        if (codedAssessment is not null) return codedAssessment;

        // New status codes are introduced independently of the published REST
        // guide. Never turn an unknown non-success uc into an ownership claim
        // merely because GLN1 still contains this pharmacy.
        if (resultCode.Length > 0 && resultCode != "00000")
            return new ItsOwnershipAssessment(
                ItsOwnershipState.Unknown,
                $"İTS ürün durum kodu tanınmıyor ({resultCode}); ürün üzerinize kayıtlı kabul edilmedi.",
                false,
                null,
                null);

        if (gln1 == ItsPharmacyRequestFactory.EndConsumerGln)
            return SoldAssessment("İlaç satılmıştır.", gln2);

        if (gln1 == pharmacyGln && gln2.Length == 0)
            return new ItsOwnershipAssessment(
                ItsOwnershipState.RegisteredToThisPharmacy,
                "Bu ilaç üzerinize kayıtlıdır.",
                true,
                null,
                null);

        if (gln1 == pharmacyGln && gln2.Length > 0 && gln2 != pharmacyGln)
            return new ItsOwnershipAssessment(
                ItsOwnershipState.IncomingTransfer,
                "İlaç eczanenize sevk edilmiştir; Mal Alım beklenmektedir.",
                false,
                pharmacyGln,
                gln2);

        if (gln2 == pharmacyGln && gln1.Length > 0 && gln1 != pharmacyGln)
            return new ItsOwnershipAssessment(
                ItsOwnershipState.OutgoingTransfer,
                "İlaç eczanenizden sevk edilmiştir.",
                false,
                gln1,
                pharmacyGln);

        if (gln1.Length > 0 && gln2.Length == 0)
            return new ItsOwnershipAssessment(
                ItsOwnershipState.RegisteredToAnotherStakeholder,
                "İlaç üzerinize kayıtlı değildir.",
                false,
                gln1,
                null);

        if (gln1.Length > 0 && gln2.Length > 0)
            return new ItsOwnershipAssessment(
                ItsOwnershipState.TransferBetweenOtherStakeholders,
                "İlaç başka paydaşlar arasında sevk halindedir.",
                false,
                gln1,
                gln2);

        return new ItsOwnershipAssessment(
            ItsOwnershipState.Unknown,
            $"İTS ürün durumu belirlenemedi (sonuç kodu: {DisplayCode(result.ResultCode)}).",
            false,
            null,
            null);
    }

    private static ItsOwnershipAssessment? ClassifyResultCode(
        string code,
        string pharmacyGln,
        string gln1,
        string gln2) => code switch
    {
        "60000" => NonOwned(ItsOwnershipState.Deactivated, "İlaç deaktiftir.", gln1, gln2),
        "60001" => NonOwned(ItsOwnershipState.AtManufacturer, "İlaç üretici üzerindedir.", gln1, gln2),
        "60002" => NonOwned(ItsOwnershipState.AtWholesaler, "İlaç ecza deposundadır.", gln1, gln2),
        "60003" when gln1 == pharmacyGln => new ItsOwnershipAssessment(
            ItsOwnershipState.RegisteredToThisPharmacy,
            "Bu ilaç eczanenizde ve üzerinize kayıtlıdır.",
            true,
            null,
            null),
        "60003" => NonOwned(
            ItsOwnershipState.RegisteredToAnotherStakeholder,
            "İlaç başka bir eczanede kayıtlıdır.",
            gln1,
            gln2),
        "60004" or "60018" or "40004" or "10204" or "10224" =>
            SoldAssessment("İlaç eczane tarafından satılmıştır.", gln1),
        "60005" => NonOwned(ItsOwnershipState.Exported, "İlaç ihraç edilmiştir.", gln1, gln2),
        "60006" or "10231" => NonOwned(ItsOwnershipState.Consumed, "İlaç sarf edilmiştir.", gln1, gln2),
        "60007" => NonOwned(ItsOwnershipState.Recalled, "İlaç geri çekilmiştir.", gln1, gln2),
        "60008" => NonOwned(
            ItsOwnershipState.AtConsumptionCenter,
            "İlaç sarf merkezi üzerindedir.",
            gln1,
            gln2),
        "60010" => new ItsOwnershipAssessment(
            ItsOwnershipState.IncomingTransfer,
            "İlaç eczanenize sevk edilmiştir; Mal Alım yapılabilir.",
            false,
            pharmacyGln,
            NullIfEmpty(gln2)),
        "60011" => SoldAssessment(
            "İlaç tarafınızdan satılmıştır; Satış İptal yapılabilir.",
            gln1),
        "60012" => new ItsOwnershipAssessment(
            ItsOwnershipState.OutgoingTransfer,
            "İlaç eczanenizden sevk edilmiştir; Mal Devir İptal yapılabilir.",
            false,
            NullIfEmpty(gln1),
            pharmacyGln),
        "60013" => new ItsOwnershipAssessment(
            ItsOwnershipState.TransferBetweenOtherStakeholders,
            "İlaç GLN1 ile GLN2 arasında sevk halindedir; işlem yetkiniz yoktur.",
            false,
            NullIfEmpty(gln1),
            NullIfEmpty(gln2)),
        "60014" => new ItsOwnershipAssessment(
            ItsOwnershipState.RegisteredToThisPharmacy,
            "Bu ilaç üzerinize kayıtlıdır.",
            true,
            null,
            null),
        "60015" or "10209" or "10222" => NonOwned(
            ItsOwnershipState.RegisteredToAnotherStakeholder,
            "İlaç üzerinize kayıtlı değildir; GLN1 kodlu paydaş üzerinde kayıtlıdır.",
            gln1,
            gln2),
        "10210" or "10223" => new ItsOwnershipAssessment(
            ItsOwnershipState.RegisteredToThisPharmacy,
            "Bu ilaç üzerinize kayıtlıdır.",
            true,
            null,
            null),
        "60017" => NonOwned(
            ItsOwnershipState.PhaseOne,
            "İlaç Faz 1 ürünüdür; İTS işlem uygunluğunu ayrıca doğrulayın.",
            gln1,
            gln2),
        _ => null
    };

    private static ItsOwnershipAssessment SoldAssessment(string summary, string gln1) => new(
        ItsOwnershipState.SoldToEndConsumer,
        summary,
        false,
        ItsPharmacyRequestFactory.EndConsumerGln,
        NullIfEmpty(gln1));

    private static ItsOwnershipAssessment NonOwned(
        ItsOwnershipState state,
        string summary,
        string gln1,
        string gln2) => new(
        state,
        summary,
        false,
        NullIfEmpty(gln1),
        NullIfEmpty(gln2));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    private static string DisplayCode(string value) => string.IsNullOrWhiteSpace(value) ? "yok" : value;
}

public enum LocalQrLocation
{
    NotFound,
    Active,
    Hanged,
    Inactive,
    HistoryOnly
}

public sealed record LocalQrStatus(
    bool ExistsInDatabase,
    string MatchedQrCode,
    string Barcode,
    string ProductName,
    LocalQrLocation Location,
    string Summary,
    string LastMovementCode,
    string LastMovementName,
    DateTime? LastMovementDate,
    string LastMovementAddress,
    string ReferenceCode,
    string? TransferDirection,
    string? TransferStatus,
    string? TransferDestinationGln,
    string? TransferDestinationName,
    int? SaleId,
    int? PatientId,
    string? PatientName);

public sealed record ProductStatusQueryReport(
    string EnteredQrCode,
    ItsQrProduct Product,
    LocalQrStatus? LocalStatus,
    string? LocalError,
    ItsStatusQueryResult? ItsResult,
    ItsOwnershipAssessment? ItsAssessment,
    string? ItsError,
    string ComparisonSummary);

public interface ILocalProductStatusSource
{
    Task<LocalQrStatus> QueryAsync(string enteredQrCode, CancellationToken cancellationToken);
}

public static class LocalQrStatusFormatter
{
    public static LocalQrStatus NotFound() => new(
        false, string.Empty, string.Empty, string.Empty, LocalQrLocation.NotFound,
        "Bu karekod yerel veritabanında bulunamadı.",
        string.Empty, string.Empty, null, string.Empty, string.Empty,
        null, null, null, null, null, null, null);

    public static string Format(LocalQrLocation location, string lastMovementCode) => location switch
    {
        LocalQrLocation.Active => "Bu ilaç yerel aktif stokunuzdadır.",
        LocalQrLocation.Hanged => "İlaç yerel kayıtlara göre Takas ile sevk edilmiştir; sonuç beklenmektedir.",
        LocalQrLocation.Inactive when lastMovementCode is "PERAK_OUT" or "PRESC_OUT" =>
            "İlaç yerel kayıtlara göre satılmıştır.",
        LocalQrLocation.Inactive when lastMovementCode == "TRANSF_OUT" =>
            "İlaç yerel kayıtlara göre Takas ile çıkmıştır.",
        LocalQrLocation.Inactive when lastMovementCode is "DEP_RETURN" or "FIRM_RETURN" =>
            "İlaç yerel kayıtlara göre iade edilmiştir.",
        LocalQrLocation.Inactive when lastMovementCode == "DESTRUCTION" =>
            "İlaç yerel kayıtlara göre imha edilmiştir.",
        LocalQrLocation.Inactive => "İlaç yerel veritabanındadır ancak aktif stokta değildir.",
        LocalQrLocation.HistoryOnly => "İlaç aktif stokta değildir; yalnızca yerel işlem geçmişinde bulunmuştur.",
        _ => "Bu karekod yerel veritabanında bulunamadı."
    };
}
