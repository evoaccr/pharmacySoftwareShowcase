namespace pharmacyApp.Modules.Medula;

public sealed record MedulaElectronicPrescriptionListItem(
    string ActionId,
    string TrackingNumber,
    string ElectronicPrescriptionNumber,
    string PrescriptionDate,
    string DoctorName,
    string PrescriptionType,
    string FacilityName);

public sealed record MedulaElectronicPrescriptionQueryResult(
    bool IsSuccess,
    bool IsOpen,
    string Message,
    string Url,
    string PatientIdentityNumber,
    string ElectronicPrescriptionNumber,
    string TrackingNumber,
    string PrescriptionDate,
    IReadOnlyList<MedulaElectronicPrescriptionListItem> Prescriptions,
    string? WarningText = null);

public sealed record MedulaElectronicPrescriptionDetailField(
    string Label,
    string Value);

public sealed record MedulaElectronicPrescriptionDrug(
    string Barcode,
    string Name,
    string Quantity,
    string Usage,
    string UsageRoute,
    string Description)
{
    public string QuantityPeriodDose => string.Join(" / ",
        new[] { Quantity, Usage }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Amount => string.Empty;
    public string Difference => string.Empty;
    public string ShelfQuantity { get; set; } = string.Empty;
    public string AvailableDate => string.Empty;
    public string Report => string.Empty;
}

public static class MedulaElectronicPrescriptionDrugNotes
{
    private static readonly System.Text.RegularExpressions.Regex DiagnosisPrefix = new(
        @"^\s*Teşhis\s*/\s*Tanı\s*[-:–—]*\s*",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ProtocolOnly = new(
        @"^[\p{L}\p{N}._/\-]+(?:\s+[\p{L}\p{N}._/\-]+)*\s+Protokolüne\s+ait\s+reçeteler[.!]?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string ImportantDiagnosis(string? value)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            value?.Trim() ?? string.Empty, @"\s+", " ");
        var content = DiagnosisPrefix.Replace(normalized, string.Empty).Trim();
        if (content.Length == 0 || ProtocolOnly.IsMatch(content)) return string.Empty;
        return $"Teşhis/Tanı - {content}";
    }
}

public sealed record MedulaElectronicPrescriptionDiagnosis(
    string Code,
    string Description);

public sealed record MedulaElectronicPrescriptionDetail(
    bool IsOpen,
    string Title,
    string Url,
    string Message,
    string PatientIdentityNumber,
    string ElectronicPrescriptionNumber,
    string PatientName,
    IReadOnlyList<MedulaElectronicPrescriptionDetailField> PatientFields,
    IReadOnlyList<MedulaElectronicPrescriptionDetailField> PrescriptionFields,
    IReadOnlyList<MedulaElectronicPrescriptionDetailField> DoctorFields,
    IReadOnlyList<MedulaElectronicPrescriptionDrug> Drugs,
    IReadOnlyList<MedulaElectronicPrescriptionDiagnosis> Diagnoses,
    MedulaRemoteTable? ApprovalTable,
    string SignatureText,
    bool CanReturn,
    bool CanContinue,
    string? WarningText = null);

public sealed record MedulaElectronicPrescriptionContinueResult(
    bool IsSuccess,
    string Message,
    MedulaPrescriptionSnapshot? Snapshot,
    string? WarningText = null);
