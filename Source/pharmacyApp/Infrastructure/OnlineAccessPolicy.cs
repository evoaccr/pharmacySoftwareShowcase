namespace pharmacyApp.Infrastructure;

public static class OnlineAccessPolicy
{
    public static bool MedulaOnlineEnabled => false;
    public const string MedulaMessage = "Medula online access is disabled in this version. Local archives remain available.";
    public const string DepotMessage = "Web depot connections are disabled in this version.";
    public const string RestrictedMessage = "This feature cannot be opened in the safe portfolio version as it connects to government services or pharmacy depots. These screens' functions can be seen from the attached images.";

    public static void ShowRestricted() => pharmacyApp.Localization.EnglishMessageBox.Show(RestrictedMessage, "Restricted Feature");

    public static bool IsModuleEnabled(string id) =>
        id == "medula.prescription-list" ||
        (!id.StartsWith("medula.", StringComparison.OrdinalIgnoreCase) &&
         !id.StartsWith("debug.", StringComparison.OrdinalIgnoreCase));

    public static bool IsAiToolEnabled(string name) => !name.StartsWith("medula.", StringComparison.Ordinal) ||
        name is "medula.prescriptions_search" or "medula.patient_prescriptions_search" or
            "medula.prescription_get" or "medula.prescription_history" or
            "medula.aggregate_prescription_reports" or "medula.prescription_filter_options";

    public static void RequireMedulaOnline() => throw new NotSupportedException(MedulaMessage);
    public static void RequireWebDepot() => throw new NotSupportedException(DepotMessage);
    public static void RequireItsApi() => throw new NotSupportedException(RestrictedMessage);
    public static void RequireEczaciKart() => throw new NotSupportedException(RestrictedMessage);
}
