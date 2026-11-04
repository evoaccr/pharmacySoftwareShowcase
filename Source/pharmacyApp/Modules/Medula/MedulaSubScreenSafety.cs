namespace pharmacyApp.Modules.Medula;

public static class MedulaSubScreenSafety
{
    public const int DestinationPollAttempts = 30;
    public const int DestinationPollIntervalMilliseconds = 100;
    public static TimeSpan MaximumDestinationWait => TimeSpan.FromMilliseconds(
        (DestinationPollAttempts - 1) * DestinationPollIntervalMilliseconds);

    public static bool ShouldSynchronizePrescriptionInput(string actionId) => actionId is not
        ("f:buttonRaporSecim" or "f:buttonRaporGoruntule"
            or "f:buttonIlacTedaviSema" or "f:buttonReceteTeshis");

    public static bool IsEnteringItsQrScreen(
        string screenKey,
        string? previousScreenKey,
        bool previousScreenWasVisible) =>
        screenKey.Equals("its-qr", StringComparison.Ordinal)
        && (!previousScreenWasVisible
            || !string.Equals(previousScreenKey, "its-qr", StringComparison.Ordinal));

    public static string? InlineRootId(string screenKey) => screenKey switch
    {
        "facility-search" => "f:box11",
        "facility-departments" => "f:box4",
        "branch-select" => "f:box18",
        "doctor-branch-select" => "f:dialog4",
        "warning-code" => "f:box39",
        // The newer warning-code UI is not the legacy f:box39 panel. Its
        // generated container id varies, so it is located structurally.
        "new-warning-code" => null,
        _ => null
    };

    public static bool IsAllowed(string screenKey, string caption)
    {
        var normalized = caption.Trim();
        return screenKey switch
        {
            "drugs" => normalized.Equals("Göz İlaçları", StringComparison.OrdinalIgnoreCase),
            "eye-drugs" => false,
            "reports" => normalized.Equals("Bitmiş raporları da göster", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Yeni Kayıt", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Yeni Rapor", StringComparison.OrdinalIgnoreCase),
            "report-new" => normalized.Equals("Ara", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Ekle", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Sorgula", StringComparison.OrdinalIgnoreCase),
            "facility-search" => normalized.Equals("Ara", StringComparison.OrdinalIgnoreCase),
            "report-selection" => normalized.Equals("Tamam", StringComparison.OrdinalIgnoreCase),
            "treatment-scheme" => normalized.Equals("Tamam", StringComparison.OrdinalIgnoreCase),
            "new-warning-code" => normalized.Equals("Tamam", StringComparison.OrdinalIgnoreCase),
            "its-qr" => normalized.Equals("Listele", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("ITS Satış Bildirimi", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("İTS Satış Bildirimi", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("ITS Satış İptali", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("İTS Satış İptali", StringComparison.OrdinalIgnoreCase),
            "uts-operations" => normalized.Equals(
                    "Yeni Kayıt Ekle/Seçilen Kaydı Güncelle", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Yeni Kayıt", StringComparison.OrdinalIgnoreCase),
            "saved-prescription-document" => normalized.Equals("Doküman Ekle", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Döküman Ekle", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Dokümanı Yükle", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Dökümanı Yükle", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static bool ShouldReturnToPrescriptionAfterAccept(string screenKey, string caption) =>
        screenKey.Equals("treatment-scheme", StringComparison.Ordinal)
        && caption.Trim().Equals("Tamam", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedActionId(string screenKey, string actionId) =>
        screenKey switch
        {
            "branch-select" => IsIndexedRowAction(
                actionId, "f:tableExBransList:", ":rowAction5"),
            "doctor-branch-select" => IsIndexedRowAction(
                actionId, "f:tableExDoktorBransList:", ":rowAction2"),
            "facility-search" => IsIndexedRowAction(
                actionId, "f:tableExTesisSorguList:", ":rowAction1"),
            "uts-operations" => IsIndexedRowAction(
                actionId, "form1:tableExIlacBilgileri:", ":rowAction1"),
            _ => false
        };

    public static bool IsSafePageCaption(string caption) =>
        int.TryParse(caption.Trim(), out var page) && page > 0
        || caption.Trim().Equals("İleri", StringComparison.OrdinalIgnoreCase)
        || caption.Trim().Equals("Geri", StringComparison.OrdinalIgnoreCase)
        || caption.Trim() is ">" or "<" or ">>" or "<<";

    private static bool IsIndexedRowAction(string actionId, string prefix, string suffix) =>
        actionId.StartsWith(prefix, StringComparison.Ordinal)
        && actionId.EndsWith(suffix, StringComparison.Ordinal)
        && int.TryParse(
            actionId.AsSpan(prefix.Length, actionId.Length - prefix.Length - suffix.Length),
            out _);

    public static bool IsExpectedDestination(
        string screenKey,
        string url,
        IReadOnlyCollection<string> visibleElementIds,
        IReadOnlyCollection<string>? visibleActionCaptions = null)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url;
        var normalizedPath = string.Join('/', path.Split('/').Select(segment => segment.Split(';')[0]));
        bool Has(string id) => visibleElementIds.Contains(id, StringComparer.Ordinal);
        bool HasCaption(string caption) => visibleActionCaptions?.Any(item =>
            item.Trim().Equals(caption, StringComparison.OrdinalIgnoreCase)) == true;
        bool EndsWithPage(string page) => normalizedPath.EndsWith('/' + page, StringComparison.OrdinalIgnoreCase);
        var isPrescriptionPage = Has("f:buttonReceteKaydet");
        var isOffLabelPage = !isPrescriptionPage
            && Has("form1:buttonGeriDon")
            && Has("form1:buttonGuncelle");
        var isExaminationList = !isPrescriptionPage
            && Has("form1:buttonGeriDon")
            && Has("form1:tableEx1")
            && !Has("form1:buttonGuncelle")
            && !HasCaption("Bitmiş Raporları da Göster")
            && !HasCaption("Yeni Kayıt")
            && !HasCaption("Göz İlaçları");

        return screenKey switch
        {
            "drugs" => EndsWithPage("ReceteIslem2.jsp")
                && !isPrescriptionPage && HasCaption("Göz İlaçları"),
            "eye-drugs" => normalizedPath.Contains("/haksahibi/", StringComparison.OrdinalIgnoreCase)
                && EndsWithPage("IlacListe.jsp")
                && Has("form1:tableExKisiIlacList"),
            "reports" => EndsWithPage("RaporListe.jsp")
                || (!isPrescriptionPage && (HasCaption("Bitmiş Raporları da Göster")
                    || HasCaption("Yeni Kayıt"))),
            "examinations" => EndsWithPage("MuayeneDetay.jsp")
                || Has("form1:tableEx3")
                || isExaminationList,
            "off-label-old" => EndsWithPage("EndikasyonDisiIzinIslem.jsp")
                || isOffLabelPage,
            "off-label-new" => EndsWithPage("KisiEndikasyonDisiIzinIslem.jsp")
                || isOffLabelPage,
            "facility-search" => Has("f:box14") && Has("f:buttonVazgec")
                && Has("f:buttonTesisSehriAra") && Has("f:menu3") && Has("f:text74"),
            "facility-departments" => Has("f:box4")
                && Has("f:tableExKonuSorguList") && Has("f:buttonKonuTamam"),
            "branch-select" => Has("f:box18") && Has("f:tableExBransList"),
            "warning-code" => Has("f:box39") && Has("f:buttonReceteTeshisPanelKapat"),
            "new-warning-code" => Has("__new_warning_code_dialog__")
                && (HasCaption("Vazgeç") || HasCaption("Kapat") || HasCaption("Tamam")),
            "report-selection" => HasCaption("Tamam") && HasCaption("Vazgeç"),
            "treatment-scheme" => !isPrescriptionPage
                && (EndsWithPage("TedaviSema.jsp")
                    || EndsWithPage("IlacTedaviSema.jsp")
                    || Has("form1:buttonGeriDon")),
            "drug-report-detail" => EndsWithPage("RaporGorme.jsp")
                || (!isPrescriptionPage && Has("form1:buttonGeriDon")
                    && visibleElementIds.Any(id => id.StartsWith("form1:", StringComparison.Ordinal))),
            "its-qr" => Has("form1:textarea1") && Has("form1:buttonListele")
                && (EndsWithPage("ReceteIslem2.jsp") || EndsWithPage("KarekodIslem.jsp")),
            "uts-operations" => !isPrescriptionPage
                && Has("form1:tableExIlacBilgileri")
                && Has("form1:buttonGuncelle")
                && Has("form1:buttonYeniKayit")
                && (normalizedPath.Contains("uts", StringComparison.OrdinalIgnoreCase)
                    || HasCaption("Geri Dön")),
            "report-new" => !EndsWithPage("RaporListe.jsp")
                && visibleElementIds.Any(id => id.StartsWith("form1:", StringComparison.Ordinal)),
            _ => false
        };
    }

    public static bool RequiresPrescriptionReload(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) is var normalized
        && normalized.Contains("REÇETE", StringComparison.Ordinal)
        && normalized.Contains("EKRANINA YENİDEN GİRİŞ YAPINIZ", StringComparison.Ordinal);

    public static bool ShouldRetryAfterPrescriptionReload(string? warning, int completedRetries) =>
        completedRetries == 0 && RequiresPrescriptionReload(warning);
}
