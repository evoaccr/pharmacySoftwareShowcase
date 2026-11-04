using System.Globalization;
using System.Windows;

namespace pharmacyApp.Modules.Medula;

internal static class MedulaPrescriptionPersistenceMapper
{
    public static MedulaPrescriptionPersistenceRecord FromSnapshot(
        MedulaPrescriptionSnapshot snapshot,
        MedulaPrescriptionInput? fallbackInput = null,
        bool importedFromMedula = false,
        string? savedByUserId = null)
    {
        snapshot = MedulaPrescriptionHeaderParser.Normalize(snapshot);
        var page = snapshot.Page;
        var fields = page.FieldValues ?? new Dictionary<string, string>();
        string Field(string key) => fields.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
        string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
        var names = snapshot.SourceListItem is { } source
            ? (FirstName: source.PatientFirstName.Trim(), LastName: source.PatientLastName.Trim())
            : SplitName(snapshot.PatientDisplayName);
        var filterDate = snapshot.StoredFilterDate
            ?? ParseDate(snapshot.SourceListItem?.MedulaFilterDate)
            ?? (snapshot.PrescriptionUid is null ? DateOnly.FromDateTime(DateTime.Today) : null);
        var prescriptionNo = FirstValidPrescriptionNumber(
            Field("medula:prescription-no"),
            snapshot.SourceListItem?.PrescriptionNumber,
            fallbackInput?.SourceListItem?.PrescriptionNumber);
        var electronicPrescriptionNo = Field("medula:e-prescription-no");
        var patientTc = First(Field("f:t18"), fallbackInput?.PatientIdentityNumber ?? string.Empty);
        var protocolNo = First(Field("f:t24"), fallbackInput?.ProtocolNumber ?? string.Empty);
        var typeValue = snapshot.SourceListItem?.TypeValue ?? string.Empty;
        var qrStatus = Field("medula:qr-status");
        var payments = page.SavedSummary?.PaymentFields ?? [];
        var finalDrugs = snapshot.Drugs.Count > 0 ? snapshot.Drugs : fallbackInput?.Drugs ?? [];
        var registeredDrugs = fallbackInput?.Drugs is { Count: > 0 }
            ? fallbackInput.Drugs
            : finalDrugs;
        var diagnoses = snapshot.Diagnoses.Count > 0 ? snapshot.Diagnoses : fallbackInput?.Diagnoses ?? [];
        var qrCodes = Field("medula:its-qr-codes")
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var registeredContent = DrugMatrix(registeredDrugs);
        var finalContent = DrugMatrix(finalDrugs);
        var prescriptionCash = Payment(payments, "Reç.Kat.Payı", "Elden");
        var prescriptionSalary = Payment(payments, "Reç.Kat.Payı", "Maaştan");
        var examinationCash = Payment(payments, "Mua.Kat.Payı", "Elden");
        var examinationSalary = Payment(payments, "Mua.Kat.Payı", "Maaştan");
        var insuranceType = MedulaInsuranceTypeResolver.Resolve(
            Field("medula:insurance-type"), page.PageText,
            prescriptionCash, prescriptionSalary, examinationCash, examinationSalary);
        return new MedulaPrescriptionPersistenceRecord(
            electronicPrescriptionNo, prescriptionNo, qrStatus,
            First(Field("f:m4"), fallbackInput?.PrescriptionType ?? string.Empty),
            First(Field("f:m8"), fallbackInput?.PrescriptionSubType ?? string.Empty),
            MedulaPrescriptionListTypes.ResolveInvoiceType(Field("medula:invoice-type"), typeValue),
            First(Field("f:menu2"), fallbackInput?.ProvisionType ?? string.Empty),
            protocolNo, First(Field("f:t33"), fallbackInput?.FacilityCode ?? string.Empty),
            ParseDate(First(Field("f:t29"), fallbackInput?.PrescriptionDate ?? string.Empty)),
            ParseDate(First(Field("f:t31"), fallbackInput?.DispensingDate ?? string.Empty)),
            ParseBool(Field("f:checkbox1"), fallbackInput?.HasQrCode ?? false),
            ParseBool(Field("f:checkbox2"), fallbackInput?.IsSequentialDistribution ?? false),
            patientTc, Field("medula:patient-type"),
            insuranceType, Field("medula:coverage"),
            First(Field("f:text37"), fallbackInput?.RecipientIdentityNumber ?? string.Empty),
            First(Field("f:menu4"), fallbackInput?.RecipientType ?? string.Empty),
            First(Field("f:t43"), fallbackInput?.RegistrationNumber ?? string.Empty),
            First(Field("f:t41"), fallbackInput?.PhysicianFirstName ?? string.Empty),
            First(Field("f:t42"), fallbackInput?.PhysicianLastName ?? string.Empty),
            Field("medula:branch"), First(Field("f:m7"), fallbackInput?.CertificateType ?? string.Empty),
            registeredContent, finalContent, DiagnosisMatrix(diagnoses), Field("medula:prescription-note"),
            prescriptionCash, prescriptionSalary, examinationCash, examinationSalary,
            Payment(payments, "İl.Kat.Payı"), Payment(payments, "Fiyat Farkı"),
            Payment(payments, "Ödenecek Tutar"), Payment(payments, "Hastanın eczaneye ödemesi gereken"),
            Payment(payments, "Toplam Tutar"), qrCodes,
            MedulaPrescriptionListTypes.IsItsFinalized(qrStatus),
            savedByUserId ?? (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty,
            snapshot.PrescriptionUid, filterDate, names.FirstName, names.LastName, importedFromMedula,
            MedulaPrescriptionRepository.BuildIdentityFingerprint(prescriptionNo, patientTc, filterDate, typeValue, protocolNo),
            typeValue, First(Field("f:t39"), fallbackInput?.PhysicianRegistrationNumber ?? string.Empty),
            First(snapshot.SourceListItem?.StatusCode ?? string.Empty,
                electronicPrescriptionNo.Length > 0 ? "I" : "K"),
            snapshot.SourceListItem?.BarcodeStatus ?? string.Empty,
            snapshot.SourceListItem?.MedicalReport ?? string.Empty,
            snapshot.SourceListItem?.DocumentStatus ?? string.Empty,
            snapshot.SourceListItem?.PageNumber ?? 0,
            snapshot.SourceListItem?.RowIndex ?? 0);
    }

    private static string[,] DrugMatrix(IReadOnlyList<MedulaDrugEntry> drugs)
    {
        // Quantity is deliberately stored next to every barcode in both content arrays.
        var result = new string[drugs.Count, 15];
        for (var row = 0; row < drugs.Count; row++)
        {
            var drug = drugs[row];
            result[row, 0] = drug.Barcode; result[row, 1] = drug.Name; result[row, 2] = drug.Quantity;
            result[row, 3] = drug.Period; result[row, 4] = drug.PeriodUnit; result[row, 5] = drug.DoseCount;
            result[row, 6] = drug.DoseAmount; result[row, 7] = drug.Amount; result[row, 8] = drug.Difference;
            result[row, 9] = drug.ShelfQuantity; result[row, 10] = drug.Report; result[row, 11] = drug.AvailableDate;
            result[row, 12] = drug.DispensingMessage; result[row, 13] = drug.Selected.ToString(CultureInfo.InvariantCulture);
            result[row, 14] = drug.DiagnosisNote;
        }
        return result;
    }

    private static string[,] DiagnosisMatrix(IReadOnlyList<MedulaDiagnosisEntry> diagnoses)
    {
        var result = new string[diagnoses.Count, 2];
        for (var row = 0; row < diagnoses.Count; row++)
        {
            result[row, 0] = diagnoses[row].Code;
            result[row, 1] = diagnoses[row].Description;
        }
        return result;
    }

    private static (string FirstName, string LastName) SplitName(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (parts[0], string.Empty),
            _ => (string.Join(' ', parts[..^1]), parts[^1])
        };
    }

    private static string FirstValidPrescriptionNumber(params string?[] values) =>
        values.FirstOrDefault(MedulaPrescriptionHeaderParser.IsValidPrescriptionNumber)?.Trim()
        ?? string.Empty;

    private static bool ParseBool(string value, bool fallback) => bool.TryParse(value, out var result) ? result : fallback;
    private static DateOnly? ParseDate(string? value) => DateOnly.TryParseExact(value?.Trim(),
        ["dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy"], CultureInfo.GetCultureInfo("tr-TR"),
        DateTimeStyles.None, out var result) ? result : null;
    private static decimal? Payment(IReadOnlyList<MedulaPrescriptionPaymentField> fields, params string[] parts)
    {
        var value = fields.FirstOrDefault(field => parts.All(part =>
            field.Label.Contains(part, StringComparison.CurrentCultureIgnoreCase)))?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(character => char.IsDigit(character) || character is ',' or '.' or '-').ToArray());
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out var result)
            ? result : null;
    }
}
