namespace pharmacyApp.Modules.Medula;

public sealed class MedulaDrugEntry
{
    public bool Selected { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public string Quantity { get; set; } = "0";
    public string OriginalQuantity { get; set; } = string.Empty;
    public string Period { get; set; } = "0";
    public string PeriodUnit { get; set; } = "3";
    public string DoseCount { get; set; } = "0";
    public string DoseAmount { get; set; } = "0,0";
    public string Name { get; set; } = string.Empty;
    public string Amount { get; set; } = "0,00";
    public string Difference { get; set; } = "0,00";
    public string ShelfQuantity { get; set; } = string.Empty;
    public string Report { get; set; } = string.Empty;
    public string AvailableDate { get; set; } = string.Empty;
    public string DispensingMessage { get; set; } = string.Empty;
    public string DiagnosisNote { get; set; } = string.Empty;
    public string ErrorText { get; set; } = string.Empty;
}

public static class MedulaPrescriptionMessageRules
{
    private static readonly System.Text.RegularExpressions.Regex BarcodeErrorPattern = new(
        @"(?<!\d)(?<barcode>\d{13,14})\s+barkodlu\s+ila[cç]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ReasonPrefixPattern = new(
        @"\s+Sebep\s*\(ler\)i\s*[:;]?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool AreDuplicateErrors(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0) return false;
        if (normalizedLeft.Equals(normalizedRight, StringComparison.CurrentCultureIgnoreCase)) return true;

        var leftBarcode = Barcode(left);
        var rightBarcode = Barcode(right);
        if (leftBarcode.Length == 0
            || !leftBarcode.Equals(rightBarcode, StringComparison.Ordinal)) return false;
        return Summary(normalizedLeft).Equals(
            Summary(normalizedRight), StringComparison.CurrentCultureIgnoreCase);
    }

    public static string PreferDetailedError(string left, string right)
    {
        var leftScore = DetailScore(left);
        var rightScore = DetailScore(right);
        return rightScore > leftScore ? right : left;
    }

    public static void ApplyBarcodeErrors(
        IEnumerable<MedulaDrugEntry> drugs,
        IEnumerable<string> messages)
    {
        var drugRows = drugs.ToArray();
        foreach (var drug in drugRows) drug.ErrorText = string.Empty;

        var preferred = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawMessage in messages)
        {
            var message = Normalize(rawMessage);
            var barcode = Barcode(message);
            if (barcode.Length == 0) continue;
            if (preferred.TryGetValue(barcode, out var existing))
                preferred[barcode] = PreferDetailedError(existing, message);
            else
                preferred[barcode] = message;
        }

        foreach (var drug in drugRows)
        {
            var barcode = drug.Barcode.Trim();
            if (preferred.TryGetValue(barcode, out var error)) drug.ErrorText = error;
        }
    }

    private static string Barcode(string? message) =>
        BarcodeErrorPattern.Match(message ?? string.Empty) is { Success: true } match
            ? match.Groups["barcode"].Value
            : string.Empty;

    private static string Summary(string message)
    {
        var reason = ReasonPrefixPattern.Match(message);
        var summary = reason.Success ? message[..reason.Index] : message;
        return summary.Trim().TrimEnd('.', ';', ':', ' ', '\t');
    }

    private static int DetailScore(string message)
    {
        var score = Normalize(message).Length;
        if (ReasonPrefixPattern.IsMatch(message)) score += 10_000;
        if (message.Contains('*')) score += 1_000;
        return score;
    }

    private static string Normalize(string? message) =>
        System.Text.RegularExpressions.Regex.Replace(
            message ?? string.Empty, @"\s+", " ").Trim();
}

public static class MedulaDrugQuantityAdjustments
{
    public static IReadOnlyList<MedulaDrugEntry> AnnotateReductions(
        IReadOnlyList<MedulaDrugEntry>? before,
        IReadOnlyList<MedulaDrugEntry>? after)
    {
        if (after is null or { Count: 0 }) return after ?? [];
        if (before is null or { Count: 0 }) return after;

        var byBarcode = before
            .Where(drug => !string.IsNullOrWhiteSpace(drug.Barcode))
            .GroupBy(drug => drug.Barcode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<MedulaDrugEntry>(group),
                StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < after.Count; index++)
        {
            var finalDrug = after[index];
            MedulaDrugEntry? originalDrug = null;
            if (!string.IsNullOrWhiteSpace(finalDrug.Barcode)
                && byBarcode.TryGetValue(finalDrug.Barcode.Trim(), out var matches)
                && matches.Count > 0)
                originalDrug = matches.Dequeue();
            else if (index < before.Count)
                originalDrug = before[index];

            if (originalDrug is null
                || !TryQuantity(originalDrug.Quantity, out var originalQuantity)
                || !TryQuantity(finalDrug.Quantity, out var finalQuantity)
                || finalQuantity >= originalQuantity)
                continue;

            finalDrug.OriginalQuantity = string.IsNullOrWhiteSpace(originalDrug.OriginalQuantity)
                ? originalDrug.Quantity.Trim()
                : originalDrug.OriginalQuantity.Trim();
        }

        return after;
    }

    private static bool TryQuantity(string? value, out decimal quantity)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return decimal.TryParse(
                normalized,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR"),
                out quantity)
            || decimal.TryParse(
                normalized,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out quantity);
    }
}

public sealed record MedulaDrugSearchOption(
    string ActionId,
    string Barcode,
    string Name,
    int PageIndex = 0);

public sealed record MedulaDrugSearchResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<MedulaDrugSearchOption> Options,
    string? WarningText = null);

public sealed record MedulaDrugActionResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<MedulaDrugEntry> Drugs,
    MedulaRemoteScreen? Screen = null,
    string? WarningText = null);

public sealed class MedulaDiagnosisEntry
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed record MedulaDiagnosisSearchOption(
    string ActionId,
    string Code,
    string Description,
    int PageIndex = 0);

public sealed record MedulaDiagnosisSearchResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<MedulaDiagnosisSearchOption> Options,
    string? WarningText = null);

public sealed record MedulaDiagnosisActionResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<MedulaDiagnosisEntry> Diagnoses,
    string? WarningText = null);

public sealed record MedulaPrescriptionInput(
    string PatientIdentityNumber,
    string RecipientIdentityNumber,
    string RecipientType,
    string ProvisionType,
    string ProtocolNumber,
    string FacilityCode,
    string PrescriptionDate,
    string DispensingDate,
    string PrescriptionType,
    string PrescriptionSubType,
    string RegistrationNumber,
    string PhysicianType,
    string PhysicianRegistrationNumber,
    string PhysicianFirstName,
    string PhysicianLastName,
    string CertificateType,
    bool HasQrCode,
    bool IsSequentialDistribution,
    string DiagnosisSearch,
    bool RepeatEligibilityCheck,
    IReadOnlyList<MedulaDrugEntry> Drugs,
    IReadOnlyList<MedulaDiagnosisEntry> Diagnoses,
    MedulaPrescriptionListItem? SourceListItem = null,
    string ElectronicPrescriptionNumber = "",
    bool IsSavedOnMedula = false);

public enum MedulaPrescriptionAccessRoute
{
    PrescriptionEntry,
    ElectronicPrescriptionQuery,
    PrescriptionQuery
}

public static class MedulaPrescriptionAccessPolicy
{
    public static MedulaPrescriptionAccessRoute Resolve(
        bool isSavedOnMedula,
        string? medulaPrescriptionNumber,
        string? electronicPrescriptionNumber)
    {
        if (isSavedOnMedula)
            return MedulaPrescriptionAccessRoute.PrescriptionQuery;
        return string.IsNullOrWhiteSpace(electronicPrescriptionNumber)
            ? MedulaPrescriptionAccessRoute.PrescriptionEntry
            : MedulaPrescriptionAccessRoute.ElectronicPrescriptionQuery;
    }

    public static MedulaPrescriptionAccessRoute Resolve(MedulaPrescriptionInput input) =>
        Resolve(
            input.IsSavedOnMedula,
            input.SourceListItem?.PrescriptionNumber,
            input.ElectronicPrescriptionNumber);
}

public sealed record MedulaPrescriptionPageResult(
    bool IsSuccess,
    bool IsPrescriptionEntryPage,
    string PageTitle,
    string Url,
    string Message,
    string PageText,
    string? ConfirmationMessage = null,
    IReadOnlyDictionary<string, string>? FieldValues = null,
    string? WarningText = null,
    MedulaSavedPrescriptionSummary? SavedSummary = null,
    string? SuccessText = null,
    MedulaRemoteScreen? OpenedScreen = null,
    string? DownloadedFilePath = null,
    IReadOnlyList<MedulaDrugEntry>? RefreshedDrugs = null,
    string? NoticeText = null);

public static class MedulaQrFinalizationResultDetector
{
    public static bool IsConfirmed(MedulaPrescriptionPageResult result)
    {
        if (result.FieldValues?.Values.Any(MedulaPrescriptionListTypes.IsItsFinalized) == true)
            return true;

        return new[] { result.SuccessText, result.Message, result.PageText }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => value!.Contains("sonlandır", StringComparison.CurrentCultureIgnoreCase)
                && (value.Contains("reçete sonlandırıldı", StringComparison.CurrentCultureIgnoreCase)
                    || value.Contains("başarı", StringComparison.CurrentCultureIgnoreCase)
                    || value.Contains("tamamlan", StringComparison.CurrentCultureIgnoreCase)));
    }
}

public static class MedulaQrFinalizationCancellationResultDetector
{
    public static bool IsConfirmed(MedulaPrescriptionPageResult result)
    {
        static bool IsUnfinalized(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && (value.Contains("Reçete Sonlandırılmadı", StringComparison.CurrentCultureIgnoreCase)
                || value.Trim().Equals("Sonlandırılmadı", StringComparison.CurrentCultureIgnoreCase)
                || value.Trim().Equals("Karekodlu ilaç var", StringComparison.CurrentCultureIgnoreCase));

        if (result.FieldValues?.Values.Any(IsUnfinalized) == true) return true;
        return new[] { result.SuccessText, result.Message, result.PageText }.Any(IsUnfinalized);
    }
}

internal static class MedulaPrescriptionSaveGuard
{
    public static bool IsConfirmed(MedulaPrescriptionPageResult? result)
    {
        if (result is not { IsSuccess: true, SavedSummary.PaymentFields.Count: > 0 }
            || !string.IsNullOrWhiteSpace(result.WarningText))
            return false;
        if (result.FieldValues is null
            || !result.FieldValues.TryGetValue("medula:prescription-no", out var prescriptionNumber))
            return false;
        prescriptionNumber = prescriptionNumber?.Trim() ?? string.Empty;
        return prescriptionNumber.Length > 0
            && prescriptionNumber != "0"
            && prescriptionNumber != "-"
            && prescriptionNumber != "—";
    }
}

public sealed record MedulaPrescriptionSnapshot(
    MedulaPrescriptionPageResult Page,
    IReadOnlyList<MedulaDrugEntry> Drugs,
    IReadOnlyList<MedulaDiagnosisEntry> Diagnoses,
    string PatientDisplayName = "",
    MedulaPrescriptionListItem? SourceListItem = null,
    Guid? PrescriptionUid = null,
    int VersionNumber = 0,
    bool IsLocalArchive = false,
    DateOnly? StoredFilterDate = null,
    bool IsSavedOnMedula = false);

public sealed record MedulaPrescriptionListType(
    string Value,
    string Name);

public sealed record MedulaPrescriptionListGroup(
    string Key,
    string Name);

public sealed record MedulaPrescriptionReturnStatus(
    string Key,
    string Name,
    bool IsAvailable = true);

public sealed record MedulaPrescriptionPeriodOption(
    string Value,
    string Name);

public sealed record MedulaPrescriptionListItem(
    string PrescriptionNumber,
    string LastTransactionDate,
    string PrescriptionDate,
    string PatientFirstName,
    string PatientLastName,
    string Coverage,
    string QrCodeStatus,
    string StatusCode,
    string BarcodeStatus,
    string MedicalReport,
    string DocumentStatus,
    string TypeValue,
    string MonthValue,
    string Source,
    int PageNumber,
    int RowIndex)
{
    public string PatientName => $"{PatientFirstName} {PatientLastName}".Trim();
    public Guid? PrescriptionUid { get; init; }
    public int? RecordId { get; init; }
    public int VersionNumber { get; init; }
    public string MedulaFilterDate { get; init; } = string.Empty;
    public bool IsLiveOnMedula { get; init; }
    public bool IsStoredLocally { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsPermanentlyDeleted { get; init; }
    public string LastUser { get; init; } = string.Empty;
    public string PatientTcIdentityNo { get; init; } = string.Empty;
    public MedulaPrescriptionSnapshot? LocalSnapshot { get; init; }
    public string SectionTitle { get; init; } = string.Empty;
    public bool ShowMedulaDateSeparator { get; set; }
    public string MedulaDateSeparatorLabel { get; set; } = string.Empty;
    public string MedulaDateSeparatorTooltip { get; set; } = string.Empty;
    public double MedulaOrderKey { get; init; } = double.NaN;

    public string CoverageDisplay => NormalizeCoverage(Coverage);
    public string QrCodeStatusDisplay => IsDeleted ? "Silindi" : NormalizeQrStatus(QrCodeStatus);
    public bool IsQrFinalized => QrCodeStatusDisplay.Equals("Sonlandırıldı", StringComparison.CurrentCultureIgnoreCase);
    public bool IsQrUnfinalized => QrCodeStatusDisplay.Equals("Sonlandırılmadı", StringComparison.CurrentCultureIgnoreCase)
        || QrCodeStatus.Trim().Equals("Karekodlu ilaç var", StringComparison.CurrentCultureIgnoreCase);
    public bool HasNoQrStatus => QrCodeStatusDisplay.Equals("Yok", StringComparison.CurrentCultureIgnoreCase);
    public bool IsTypeI => StatusCode.Trim().Equals("I", StringComparison.OrdinalIgnoreCase)
        || StatusCode.Trim().Equals("İ", StringComparison.CurrentCultureIgnoreCase);
    public bool IsTypeK => StatusCode.Trim().Equals("K", StringComparison.OrdinalIgnoreCase);
    public bool HasBarcode => BarcodeStatus.Trim().Equals("Evet", StringComparison.CurrentCultureIgnoreCase);
    public string TypeDisplay => IsTypeI ? "Elektronik" : IsTypeK ? "Kağıt" : StatusCode;

    private static string NormalizeCoverage(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (!char.IsDigit(trimmed[0])) return trimmed;
        return trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]
            .TrimEnd(':', ';', ',')
            .ToUpperInvariant();
    }

    private static string NormalizeQrStatus(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed.Contains("sonlandırılmadı", StringComparison.CurrentCultureIgnoreCase)
            || trimmed.Equals("Karekodlu ilaç var", StringComparison.CurrentCultureIgnoreCase))
            return "Sonlandırılmadı";
        if (trimmed.Contains("sonlandırıldı", StringComparison.CurrentCultureIgnoreCase))
            return "Sonlandırıldı";
        if (trimmed.Equals("Yok", StringComparison.CurrentCultureIgnoreCase)
            || trimmed.Contains("karekodlu ilaç yok", StringComparison.CurrentCultureIgnoreCase))
            return "Yok";
        return trimmed;
    }
}

public sealed record MedulaPrescriptionListResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<MedulaPrescriptionListItem> Prescriptions,
    string? WarningText = null);

public static class MedulaPrescriptionListTypes
{
    public const string NoReturn = "none";
    public const string ContentReturn = "content";
    public const string RegulationReturn = "regulation";

    public static IReadOnlyList<MedulaPrescriptionListType> All { get; } =
    [
        new("1", "A Grubu"),
        new("2", "A Grubu İçerikten İade"),
        new("3", "A Grubu Mevzuattan İade"),
        new("4", "B Grubu"),
        new("5", "B Grubu İçerikten İade"),
        new("6", "B Grubu Mevzuattan İade"),
        new("28", "C Grubu Sıralı Dağıtım"),
        new("29", "C Grubu Sıralı Dağıtım İçerikten İade"),
        new("30", "C Grubu Sıralı Dağıtım Mevzuattan İade"),
        new("7", "C Grubu Kan Ürünü"),
        new("8", "C Grubu Kan Ürünü İçerikten İade"),
        new("9", "C Grubu Kan Ürünü Mevzuattan İade"),
        new("25", "G2D Grubu"),
        new("26", "G2D Grubu İçerikten İade"),
        new("27", "G2D Grubu Mevzuattan İade"),
        new("16", "Yeşil Kart Normal"),
        new("19", "Yeşil Kart Kan Ürünü"),
        new("10", "Yurtdışı İlaç Normal"),
        new("11", "Yurtdışı İlaç Normal İçerikten İade"),
        new("12", "Yurtdışı İlaç Normal Mevzuattan İade"),
        new("13", "Yurtdışı İlaç Kan Ürünü"),
        new("14", "Yurtdışı İlaç Kan Ürünü İçerikten İade"),
        new("15", "Yurtdışı İlaç Kan Ürünü Mevzuattan İade"),
        new("99", "Şahıs Ödemeleri Reçeteleri"),
        new("22", "Yurtdışı Sigortalı Normal"),
        new("24", "Yurtdışı Sigortalı Normal Mevzuattan İade"),
        new("23", "Yurtdışı Sigortalı Normal İçerikten İade"),
        new("31", "Yurtdışı Sigortalı Kan Ürünü"),
        new("33", "Yurtdışı Sigortalı Kan Ürünü Mevzuattan İade"),
        new("32", "Yurtdışı Sigortalı Kan Ürünü İçerikten İade"),
        new("34", "Yurtdışı İlaç Yurtdışı Sigortalı Normal"),
        new("35", "Yurtdışı İlaç Yurtdışı Sigortalı Normal İçerikten İade"),
        new("36", "Yurtdışı İlaç Yurtdışı Sigortalı Normal Mevzuattan İade"),
        new("37", "Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü"),
        new("38", "Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü İçerikten İade"),
        new("39", "Yurtdışı İlaç Yurtdışı Sigortalı Kan Ürünü Mevzuattan İade"),
        new("40", "Geçici Koruma Kanunu Kapsamında Olan Yabancılar"),
        new("41", "Geçici Koruma Kanunu Kapsamında Olan Yabancılar İçerikten İade"),
        new("42", "Geçici Koruma Kanunu Kapsamında Olan Yabancılar Mevzuattan İade"),
        new("43", "Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB"),
        new("44", "Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB İçerikten İade"),
        new("45", "Geçici Koruma Kanunu Kapsamında Olan Yabancılar-ASHB Mevzuattan İade")
    ];

    public static IReadOnlyList<MedulaPrescriptionListGroup> Groups { get; } = All
        .Select(type => GroupName(type.Name))
        .Distinct(StringComparer.Ordinal)
        .Select(name => new MedulaPrescriptionListGroup(name, name))
        .ToArray();

    public static IReadOnlyList<MedulaPrescriptionReturnStatus> ReturnStatuses { get; } =
    [
        new(NoReturn, "Yok"),
        new(ContentReturn, "İçerikten İade"),
        new(RegulationReturn, "Mevzuattan İade")
    ];

    public static IReadOnlyList<MedulaPrescriptionReturnStatus> ReturnStatusesFor(string groupKey) =>
        ReturnStatuses.Select(status => status with
        {
            IsAvailable = TryResolve(groupKey, status.Key, out _)
        }).ToArray();

    public static bool TryResolve(string groupKey, string returnStatusKey, out string value)
    {
        var match = All.FirstOrDefault(type =>
            GroupName(type.Name).Equals(groupKey, StringComparison.Ordinal)
            && ReturnStatusKey(type.Name).Equals(returnStatusKey, StringComparison.Ordinal));
        value = match?.Value ?? string.Empty;
        return match is not null;
    }

    public static string DisplayGroupName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim();
        var match = All.FirstOrDefault(type =>
            type.Value.Equals(normalized, StringComparison.Ordinal)
            || type.Name.Equals(normalized, StringComparison.CurrentCultureIgnoreCase));
        return match is null ? normalized : GroupName(match.Name);
    }

    public static string ResolveInvoiceType(string? scrapedValue, string? medulaTypeValue)
    {
        static bool IsPlaceholder(string? value) => string.IsNullOrWhiteSpace(value)
            || value.Trim() is "-" or "–" or "—"
            || value.Trim().StartsWith("Fatura ", StringComparison.CurrentCultureIgnoreCase)
            || value.Trim().Equals("Seçiniz", StringComparison.CurrentCultureIgnoreCase);
        return DisplayGroupName(IsPlaceholder(scrapedValue) ? medulaTypeValue : scrapedValue);
    }

    public static bool IsItsFinalized(string? qrStatus) =>
        string.Equals(qrStatus?.Trim(), "Sonlandırıldı", StringComparison.CurrentCultureIgnoreCase);

    private static string GroupName(string name) => name
        .Replace(" İçerikten İade", string.Empty, StringComparison.Ordinal)
        .Replace(" Mevzuattan İade", string.Empty, StringComparison.Ordinal);

    private static string ReturnStatusKey(string name) =>
        name.EndsWith(" İçerikten İade", StringComparison.Ordinal)
            ? ContentReturn
            : name.EndsWith(" Mevzuattan İade", StringComparison.Ordinal)
                ? RegulationReturn
                : NoReturn;
}

public sealed record MedulaPrescriptionPaymentField(
    string Label,
    string Value);

public sealed record MedulaSavedPrescriptionSummary(
    string Title,
    IReadOnlyList<MedulaPrescriptionPaymentField> PaymentFields,
    IReadOnlyList<string> ActionCaptions);

public sealed record MedulaPrescriptionPersistenceRecord(
    string EPrescriptionNo,
    string MedulaPrescriptionNo,
    string MedulaQrStatus,
    string PrescriptionType,
    string PrescriptionSubtype,
    string InvoiceType,
    string ProvisionType,
    string ProtocolNo,
    string FacilityCode,
    DateOnly? PrescriptionDate,
    DateOnly? MedicationPurchaseDate,
    bool HasQrCodes,
    bool IsSequentialDistribution,
    string PatientTcIdentityNo,
    string PatientType,
    string PatientInsuranceType,
    string PatientCoverageType,
    string RecipientTcIdentityNo,
    string RecipientRelationship,
    string DoctorDiplomaRegistrationNo,
    string DoctorFirstName,
    string DoctorLastName,
    string DoctorBranch,
    string DoctorCertificate,
    string[,] RegisteredContent,
    string[,] FinalContent,
    string[,] Icd10Diagnoses,
    string PrescriptionNote,
    decimal? PrescriptionCopaymentCash,
    decimal? PrescriptionCopaymentSalary,
    decimal? ExaminationCopaymentCash,
    decimal? ExaminationCopaymentSalary,
    decimal? MedicationCopayment,
    decimal? EquivalentPriceDifference,
    decimal? InstitutionTotalPayment,
    decimal? PatientTotalPayment,
    decimal? PrescriptionTotal,
    string[] QrCodes,
    bool ItsFinalized,
    string SavedByUserId,
    Guid? PrescriptionUid = null,
    DateOnly? MedulaFilterDate = null,
    string PatientFirstName = "",
    string PatientLastName = "",
    bool ImportedFromMedula = false,
    string IdentityFingerprint = "",
    string MedulaTypeValue = "",
    string DoctorDiplomaNo = "",
    string ListStatusCode = "",
    string ListBarcodeStatus = "",
    string ListMedicalReport = "",
    string ListDocumentStatus = "",
    int MedulaPageNumber = 0,
    int MedulaRowIndex = 0);

public sealed record MedulaPrescriptionInsertResult(
    int RecordId,
    Guid PrescriptionUid,
    int VersionNumber,
    bool WasWritten = true);
