using System.Text.Json;
using pharmacyApp.Modules.Medula;

namespace pharmacyApp.AI.Tools.Medula;

internal static partial class MedulaClinicalAiTools
{
    public static IReadOnlyList<IAiTool> Create(IMedulaAiOperations operations) =>
    [
        PatientMedicationHistorySearch(operations),
        PatientMedicationListGet(operations),
        PatientEligibilityGet(operations),
        PatientReportsSearch(operations),
        PatientReportGet(operations),
        DrugSearch(operations),
        DrugGet(operations),
        DrugEquivalentsSearch(operations),
        DoctorSearch(operations),
        DiagnosisSearch(operations),
        PrescriptionWarningsGet(operations),
        PatientExaminationsSearch(operations),
        PatientOffLabelApprovalsSearch(operations),
        PatientTreatmentSchemeGet(operations)
    ];

    private static IAiTool PatientMedicationHistorySearch(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_medication_history_search",
            "Verifies a patient live in Medula, then returns medication lines from the application's synchronized Medula prescription archive without creating or changing a prescription.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["patient_tc"] = PatientTcParameter(),
                ["eye_medications"] = Parameter("boolean", "Request eye-medication history; if the archive has no eye-drug classification, all medications are returned with an explicit notice.", defaultValue: false),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            ["medula", "patient", "medication", "history"],
            async (reader, token) =>
            {
                reader.RejectUnknown("patient_tc", "eye_medications", "offset", "limit");
                var screen = await client.SearchPatientMedicationHistoryAsync(
                    Tc(reader.RequireString("patient_tc")),
                    reader.OptionalBoolean("eye_medications", false), token);
                return Remote(screen, Offset(reader), Limit(reader));
            });

    private static IAiTool PatientMedicationListGet(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_medication_list_get",
            "Opens a host prescription from Reçete Listesi Günlük, replaces only Reçete Sahibi T.C. with the selected patient's T.C., and reads the Medula patient medication list shown by the İlaç action. It never creates, edits, or saves a prescription.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["patient_tc"] = PatientTcParameter(),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            ["medula", "patient", "medication", "current-list"],
            async (reader, token) =>
            {
                reader.RejectUnknown("patient_tc", "offset", "limit");
                return Remote(await client.GetPatientMedicationListAsync(
                    Tc(reader.RequireString("patient_tc")), token), Offset(reader), Limit(reader));
            });

    private static IAiTool PatientEligibilityGet(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_eligibility_get",
            "Returns Medula's current identity, eligibility, birth-date, gender, and coverage fields for one patient.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["patient_tc"] = PatientTcParameter()
            },
            ["medula", "patient", "eligibility"],
            async (reader, token) =>
            {
                reader.RejectUnknown("patient_tc");
                return Remote(await client.GetPatientEligibilityAsync(
                    Tc(reader.RequireString("patient_tc")), token), 0, 100);
            });

    private static IAiTool PatientReportsSearch(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_reports_search",
            "Opens a host prescription from Reçete Listesi Günlük, replaces only Reçete Sahibi T.C. with the selected patient's T.C., and searches the patient's Medula medical reports through the Rapor action, optionally including expired reports. It never creates, edits, or saves a prescription.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["patient_tc"] = PatientTcParameter(),
                ["include_expired"] = Parameter("boolean", "Include expired reports.", defaultValue: false),
                ["offset"] = OffsetParameter(),
                ["limit"] = LimitParameter()
            },
            ["medula", "patient", "medical-report", "search"],
            async (reader, token) =>
            {
                reader.RejectUnknown("patient_tc", "include_expired", "offset", "limit");
                var screen = await client.SearchPatientReportsAsync(
                    Tc(reader.RequireString("patient_tc")),
                    reader.OptionalBoolean("include_expired", false), token);
                return Remote(screen, Offset(reader), Limit(reader));
            });

    private static IAiTool PatientReportGet(IMedulaAiOperations client) =>
        ScreenTool(
            "medula.patient_report_get",
            "Returns the detailed Medula report matching an exact report number in one patient's results.",
            new Dictionary<string, AiToolParameterDefinition>
            {
                ["patient_tc"] = PatientTcParameter(),
                ["report_number"] = Parameter("string", "Exact Medula report or tracking number.", true)
            },
            ["medula", "patient", "medical-report", "detail"],
            async (reader, token) =>
            {
                reader.RejectUnknown("patient_tc", "report_number");
                return Remote(await client.GetPatientReportAsync(
                    Tc(reader.RequireString("patient_tc")),
                    Text(reader.RequireString("report_number"), "report_number", 64), token), 0, 100);
            });
}
