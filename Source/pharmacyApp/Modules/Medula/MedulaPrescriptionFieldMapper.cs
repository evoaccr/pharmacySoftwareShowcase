namespace pharmacyApp.Modules.Medula;

public sealed record MedulaPrescriptionFieldPayload(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, bool> Checks);

public static class MedulaPrescriptionFieldMapper
{
    public static MedulaPrescriptionFieldPayload Build(MedulaPrescriptionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["f:t18"] = input.PatientIdentityNumber,
            ["f:text37"] = input.RecipientIdentityNumber,
            ["f:menu4"] = input.RecipientType,
            ["f:menu2"] = input.ProvisionType,
            ["f:t24"] = input.ProtocolNumber,
            ["f:t33"] = input.FacilityCode,
            ["f:t29"] = input.PrescriptionDate,
            ["f:t31"] = input.DispensingDate,
            ["f:m4"] = input.PrescriptionType,
            ["f:m8"] = input.PrescriptionSubType,
            ["f:t43"] = input.RegistrationNumber,
            ["f:m6"] = input.PhysicianType,
            ["f:t39"] = input.PhysicianRegistrationNumber,
            ["f:t41"] = input.PhysicianFirstName,
            ["f:t42"] = input.PhysicianLastName,
            ["f:m7"] = input.CertificateType,
            ["f:text15"] = input.DiagnosisSearch
        };
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["f:checkbox1"] = input.HasQrCode,
            ["f:checkbox2"] = input.IsSequentialDistribution,
            ["f:checkbox4"] = input.RepeatEligibilityCheck
        };

        for (var index = 0; index < input.Drugs.Count; index++)
        {
            var drug = input.Drugs[index];
            values[$"f:tbl1:{index}:t1"] = drug.Barcode;
            values[$"f:tbl1:{index}:t2"] = drug.Quantity;
            values[$"f:tbl1:{index}:t5"] = drug.Period;
            values[$"f:tbl1:{index}:m1"] = drug.PeriodUnit;
            values[$"f:tbl1:{index}:t3"] = drug.DoseCount;
            values[$"f:tbl1:{index}:t4"] = drug.DoseAmount;
            checks[$"f:tbl1:{index}:checkbox7"] = drug.Selected;
        }

        for (var index = 0; index < Math.Min(3, input.Diagnoses.Count); index++)
        {
            var diagnosis = input.Diagnoses[index];
            values[$"f:tableEx1:{index}:text7"] = diagnosis.Code;
            values[$"f:tableEx1:{index}:text3"] = diagnosis.Description;
        }

        return new MedulaPrescriptionFieldPayload(values, checks);
    }
}
