namespace pharmacyApp.Modules.Cards;

public static partial class CardDefinitions
{
    public static readonly CardEntityDefinition Faculties = new(
        CardEntityKind.Faculties, "cards.faculties", "Tesisler", "Tesisler", "Tesis",
        "Tesis Ekle", "Tesis Düzenle", "Tesis Ekle",
        "Tesisi Düzenle", "Tesisi Sil", "faculty", "record_id",
        [
            new("faculty_code", "Tesis Kodu", Section: "Tesis Bilgileri", ConfirmOnChange: true),
            new("faculty_name", "Tesis Adı", Section: "Tesis Bilgileri", StoreUppercase: true),
            new("ownership_type", "Sahiplik Türü", CardFieldKind.Ownership, Section: "Tesis Bilgileri"),
            new("faculty_type_id", "Tesis Türü", CardFieldKind.Lookup, LookupTarget: CardEntityKind.FacultyTypes, Section: "Tesis Bilgileri"),
            new("address", "Adres", CardFieldKind.MultilineText, GroupName: "address", Placeholder: "Adres", Section: "İletişim"),
            new("district", "İlçe", GroupName: "address", Placeholder: "İlçe", Section: "İletişim"),
            new("city", "İl", GroupName: "address", Placeholder: "İl", Section: "İletişim"),
            new("country", "Ülke", GroupName: "address", Placeholder: "Ülke", Section: "İletişim"),
            new("phone_numbers", "Telefon Numaraları", CardFieldKind.PhoneList, Section: "İletişim", PhonePurposeProfile: PhonePurposeProfile.Business),
            new("email_addresses", "E-posta Adresleri", CardFieldKind.EmailList, Section: "İletişim"),
            new("tax_office", "Vergi Dairesi", GroupName: "tax", Placeholder: "Vergi dairesi", Section: "Vergi Bilgileri"),
            new("tax_no", "Vergi No", GroupName: "tax", Placeholder: "Vergi numarası", Section: "Vergi Bilgileri")
        ],
        [
            new("Code", "Tesis Kodu", 180),
            new("Name", "Tesis Adı", 360, true)
        ],
        ["faculty_name"]);

    public static readonly CardEntityDefinition Institutions = new(
        CardEntityKind.Institutions, "cards.institutions", "Kurumlar", "Kurumlar", "Kurum",
        "Kurum Ekle", "Kurum Düzenle", "Kurum Ekle",
        "Kurumu Düzenle", "Kurumu Sil", "kurum", "record_id",
        [
            new("kurum_code", "Kurum Kodu", Section: "Kurum Bilgileri", ConfirmOnChange: true),
            new("kurum_name", "Kurum Adı", Section: "Kurum Bilgileri", StoreUppercase: true),
            new("gln_code", "GLN Kodu", Section: "Kurum Bilgileri", ConfirmOnChange: true),
            new("contact_person", "İrtibat Kişisi", Section: "İletişim", StoreUppercase: true),
            new("address", "Adres", CardFieldKind.MultilineText, GroupName: "address", Placeholder: "Adres", Section: "İletişim"),
            new("district", "İlçe", GroupName: "address", Placeholder: "İlçe", Section: "İletişim"),
            new("city", "İl", GroupName: "address", Placeholder: "İl", Section: "İletişim"),
            new("phone_numbers", "Telefon Numaraları", CardFieldKind.PhoneList, Section: "İletişim", PhonePurposeProfile: PhonePurposeProfile.Business),
            new("email_addresses", "E-posta Adresleri", CardFieldKind.EmailList, Section: "İletişim"),
            new("website", "İnternet Sitesi", CardFieldKind.Website, Section: "Web"),
            new("tax_office", "Vergi Dairesi", GroupName: "tax", Placeholder: "Vergi dairesi", Section: "Vergi Bilgileri"),
            new("tax_no", "Vergi No", GroupName: "tax", Placeholder: "Vergi numarası", Section: "Vergi Bilgileri")
        ],
        [
            new("Code", "Kurum Kodu", 180),
            new("Name", "Kurum Adı", 360, true)
        ],
        ["kurum_name"]);

    public static readonly CardEntityDefinition Depots = new(
        CardEntityKind.Depots, "cards.depots", "Depolar", "Depolar", "Depo",
        "Depo Ekle", "Depo Düzenle", "Depo Ekle",
        "Depoyu Düzenle", "Depoyu Sil", "depot", "record_id",
        [
            new("gln_code", "GLN Kodu", Section: "Depo Bilgileri", ConfirmOnChange: true),
            new("depot_name", "Depo Adı", Section: "Depo Bilgileri", StoreUppercase: true),
            new("is_active", "Aktif", CardFieldKind.Boolean, Section: "Depo Bilgileri"),
            new("contact_person", "İrtibat Kişisi", Section: "İletişim", StoreUppercase: true),
            new("address", "Adres", CardFieldKind.MultilineText, GroupName: "address", Placeholder: "Adres", Section: "İletişim"),
            new("district", "İlçe", GroupName: "address", Placeholder: "İlçe", Section: "İletişim"),
            new("city", "İl", GroupName: "address", Placeholder: "İl", Section: "İletişim"),
            new("postal_code", "Posta Kodu", GroupName: "address", Placeholder: "Posta Kodu", Section: "İletişim"),
            new("business_phone_numbers", "Telefonlar", CardFieldKind.PhoneList, Section: "İletişim", PhonePurposeProfile: PhonePurposeProfile.Business),
            new("fax_numbers", "Faks Numaraları", CardFieldKind.FaxList, ShowOnAdd: false, ShowOnEdit: false),
            new("email_addresses", "E-posta Adresleri", CardFieldKind.EmailList, Section: "İletişim"),
            new("website", "İnternet Sitesi", CardFieldKind.Website, Section: "Web Depo"),
            new("web_username", "Web Kullanıcı Adı", Section: "Web Depo"),
            new("web_password", "Web Şifresi", Section: "Web Depo"),
            new("uts_company_numbers", "ÜTS Firma Numaraları", CardFieldKind.UtsCompanyList, Section: "Depo Bilgileri"),
            new("e_invoice_username", "E-Fatura Kullanıcı Adı", Section: "Fatura ve Vergi Bilgileri"),
            new("e_invoice_password", "E-Fatura Şifresi", Section: "Fatura ve Vergi Bilgileri"),
            new("e_invoice_region_name", "E-Fatura Bölge Adı", Section: "Fatura ve Vergi Bilgileri"),
            new("tax_office", "Vergi Dairesi", GroupName: "tax", Placeholder: "Vergi dairesi", Section: "Fatura ve Vergi Bilgileri"),
            new("tax_no", "Vergi No", GroupName: "tax", Placeholder: "Vergi numarası", Section: "Fatura ve Vergi Bilgileri"),
            new("debt_balance", "Borç Miktarı", CardFieldKind.Decimal, Section: "Borç"),
            new("medicines_in_transit", "Yoldaki İlaçlar", CardFieldKind.TextArray, ShowOnAdd: false, ShowOnEdit: false)
        ],
        [
            new("Code", "GLN Kodu", 180),
            new("Name", "Depo Adı", 360, true),
            new("DebtBalance", "Borç Miktarı", 150)
        ],
        ["depot_name"]);
}
