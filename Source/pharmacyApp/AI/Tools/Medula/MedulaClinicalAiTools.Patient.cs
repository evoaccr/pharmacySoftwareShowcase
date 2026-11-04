using pharmacyApp.Modules.Medula;

namespace pharmacyApp.AI.Tools.Medula;

internal static partial class MedulaClinicalAiTools
{
    private static IAiTool PrescriptionWarningsGet(IMedulaAiOperations client) => new DelegatingAiTool(
        Definition(
            "medula.prescription_warnings_get",
            "Opens one saved Medula prescription and returns current errors, notices, confirmations, and barcode-specific errors.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["prescription_number"] = Parameter("string", "Exact Medula prescription number.", true)
            },
            new Dictionary<string, string>
            {
                ["success"] = "boolean",
                ["prescription_number"] = "string",
                ["errors"] = "array<string>",
                ["notices"] = "array<string>",
                ["confirmations"] = "array<string>",
                ["drug_errors"] = "array<{barcode:string,error:string}>"
            },
            ["medula", "prescription", "warning", "validation"]),
        async (parameters, token) =>
        {
            var reader = Reader(parameters);
            reader.RejectUnknown("prescription_number");
            var number = Text(reader.RequireString("prescription_number"), "prescription_number", 40);
            var snapshot = await client.GetPrescriptionWarningsAsync(
                new MedulaPrescriptionLocator(number), token);
            if (snapshot is null)
                return new MedulaAiPrescriptionMessagesResult(
                    false, number, ["Reçete Medula'da açılamadı."], [], [], []);
            var errors = DistinctMessages(snapshot.Page.WarningText);
            var notices = DistinctMessages(snapshot.Page.NoticeText);
            var confirmations = DistinctMessages(
                snapshot.Page.ConfirmationMessage, snapshot.Page.SuccessText);
            var drugErrors = snapshot.Drugs
                .Where(drug => !string.IsNullOrWhiteSpace(drug.ErrorText))
                .Select(drug => new MedulaAiDrugError(drug.Barcode, drug.ErrorText.Trim()))
                .ToArray();
            return new MedulaAiPrescriptionMessagesResult(
                snapshot.Page.IsSuccess, number, errors, notices, confirmations, drugErrors);
        });

    private static IAiTool PatientExaminationsSearch(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_examinations_search",
            "Searches the examination records Medula makes available for one patient.",
            PatientScreenParameters(),
            ["medula", "patient", "examination", "search"],
            async (reader, token) =>
            {
                reader.RejectUnknown("patient_tc", "offset", "limit");
                return Remote(await client.SearchPatientExaminationsAsync(
                    Tc(reader.RequireString("patient_tc")), token), Offset(reader), Limit(reader));
            });

    private static IAiTool PatientOffLabelApprovalsSearch(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_off_label_approvals_search",
            "Searches one patient's legacy or person-based Medula off-label medication approvals.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["patient_tc"] = PatientTcParameter(),
                ["mode"] = Parameter(
                    "string", "Approval source.",
                    allowedValues: ["legacy", "person_based"], defaultValue: "person_based"),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            ["medula", "patient", "off-label", "approval", "search"],
            async (reader, token) =>
            {
                reader.RejectUnknown("patient_tc", "mode", "offset", "limit");
                var personBased = reader.EnumValue(
                    "mode", "person_based", "legacy", "person_based") == "person_based";
                return Remote(await client.SearchPatientOffLabelApprovalsAsync(
                    Tc(reader.RequireString("patient_tc")), personBased, token),
                    Offset(reader), Limit(reader));
            });

    private static IAiTool PatientTreatmentSchemeGet(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_treatment_scheme_get",
            "Returns Medula treatment-scheme rows associated with the selected reports and medicines of one saved prescription.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["prescription_number"] = Parameter("string", "Exact saved Medula prescription number.", true),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            ["medula", "patient", "treatment-scheme"],
            async (reader, token) =>
            {
                reader.RejectUnknown("prescription_number", "offset", "limit");
                var number = Text(
                    reader.RequireString("prescription_number"), "prescription_number", 40);
                return Remote(await client.GetPatientTreatmentSchemeAsync(
                    new MedulaPrescriptionLocator(number), token), Offset(reader), Limit(reader));
            });
}
