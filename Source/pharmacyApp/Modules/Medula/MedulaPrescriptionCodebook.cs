using System.Globalization;

namespace pharmacyApp.Modules.Medula;

public static class MedulaPrescriptionCodebook
{
    public const short QrNone = 0;
    public const short QrUnfinished = 1;
    public const short QrFinalized = 2;
    public const short QrDeleted = 3;

    public static short? InvoiceTypeCode(string? invoiceType, string? medulaTypeValue)
    {
        if (TryCode(medulaTypeValue, out var code)
            && MedulaPrescriptionListTypes.All.Any(type => type.Value == code.ToString(CultureInfo.InvariantCulture)))
            return code;

        var group = MedulaPrescriptionListTypes.DisplayGroupName(invoiceType);
        var normal = MedulaPrescriptionListTypes.All.FirstOrDefault(type =>
            MedulaPrescriptionListTypes.DisplayGroupName(type.Name)
                .Equals(group, StringComparison.CurrentCultureIgnoreCase)
            && !type.Name.Contains(" İade", StringComparison.CurrentCultureIgnoreCase));
        return normal is not null && TryCode(normal.Value, out code) ? code : null;
    }

    public static string InvoiceGroupName(short? code)
    {
        if (code is null) return string.Empty;
        var type = MedulaPrescriptionListTypes.All.FirstOrDefault(item =>
            item.Value == code.Value.ToString(CultureInfo.InvariantCulture));
        return type is null ? string.Empty : MedulaPrescriptionListTypes.DisplayGroupName(type.Name);
    }

    public static string InvoiceDisplayName(short? code)
    {
        if (code is null) return string.Empty;
        return MedulaPrescriptionListTypes.All.FirstOrDefault(item =>
            item.Value == code.Value.ToString(CultureInfo.InvariantCulture))?.Name ?? string.Empty;
    }

    public static short? QrStatusCode(
        string? value,
        bool itsFinalized = false,
        bool isDeleted = false,
        bool hasQrCodes = false)
    {
        if (isDeleted) return QrDeleted;
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Contains("silin", StringComparison.CurrentCultureIgnoreCase)) return QrDeleted;
        if (normalized.Contains("sonlandırılmadı", StringComparison.CurrentCultureIgnoreCase)
            || normalized.Contains("sonlandirilmadi", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("karekodlu ilaç var", StringComparison.CurrentCultureIgnoreCase)
            || normalized.Contains("karekodlu ilac var", StringComparison.OrdinalIgnoreCase)) return QrUnfinished;
        if (itsFinalized
            || normalized.Contains("sonlandırıldı", StringComparison.CurrentCultureIgnoreCase)
            || normalized.Contains("sonlandirildi", StringComparison.OrdinalIgnoreCase)) return QrFinalized;
        if (normalized.Contains("yok", StringComparison.CurrentCultureIgnoreCase)) return QrNone;
        return hasQrCodes ? QrUnfinished : null;
    }

    public static string QrStatusText(short? code) => code switch
    {
        QrNone => "Yok",
        QrUnfinished => "Sonlandırılmadı",
        QrFinalized => "Sonlandırıldı",
        QrDeleted => "Silindi",
        _ => string.Empty
    };

    public static short? PrescriptionTypeCode(string? value) => ResolveFiniteCode(
        value,
        1,
        5,
        ("normal", 1), ("standart", 1), ("kırmızı", 2), ("kirmizi", 2),
        ("turuncu", 3), ("mor", 4), ("yeşil", 5), ("yesil", 5));

    public static string PrescriptionTypeName(short? code) => code switch
    {
        1 => "Normal",
        2 => "Kırmızı",
        3 => "Turuncu",
        4 => "Mor",
        5 => "Yeşil",
        _ => string.Empty
    };

    public static short? PrescriptionSubtypeCode(string? value) => ResolveFiniteCode(
        value,
        1,
        8,
        ("ayaktan", 1), ("yatan", 2), ("taburcu", 3), ("günübirlik", 4),
        ("gunubirlik", 4), ("acil", 5), ("yeşil alan", 6), ("yesil alan", 6),
        ("evde bakım", 7), ("evde bakim", 7), ("gezici sağlık hizmeti", 8),
        ("gezici saglik hizmeti", 8));

    public static string PrescriptionSubtypeName(short? code) => code switch
    {
        1 => "Ayaktan",
        2 => "Yatan",
        3 => "Taburcu",
        4 => "Günübirlik",
        5 => "Acil",
        6 => "Yeşil Alan",
        7 => "Evde Bakım",
        8 => "Gezici Sağlık Hizmeti",
        _ => string.Empty
    };

    public static short? ProvisionTypeCode(string? value) => ResolveFiniteCode(
        value,
        1,
        8,
        ("normal", 1), ("trafik kazası", 2), ("trafik kazasi", 2),
        ("doğal afet", 3), ("dogal afet", 3), ("adli vaka", 4),
        ("iş kazası", 5), ("is kazasi", 5), ("meslek hastalığı", 6),
        ("meslek hastaligi", 6), ("analık hali", 7), ("analik hali", 7),
        ("3713/21", 8));

    public static string ProvisionTypeName(short? code) => code switch
    {
        1 => "Normal",
        2 => "Trafik Kazası",
        3 => "Doğal Afet",
        4 => "Adli Vaka",
        5 => "İş Kazası",
        6 => "Meslek Hastalığı",
        7 => "Analık Hali",
        8 => "3713/21",
        _ => string.Empty
    };

    public static string ControlValue(short? code) =>
        code?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static short? ResolveFiniteCode(
        string? value,
        short minimum,
        short maximum,
        params (string Name, short Code)[] names)
    {
        if (TryCode(value, out var code) && code >= minimum && code <= maximum) return code;
        var normalized = value?.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR")) ?? string.Empty;
        var match = names.FirstOrDefault(item => item.Name == normalized);
        return match == default ? null : match.Code;
    }

    private static bool TryCode(string? value, out short code) =>
        short.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out code);
}
