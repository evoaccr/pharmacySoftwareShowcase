namespace pharmacyApp.Modules.Cards;

public static partial class CardDefinitions
{
    public static readonly CardEntityDefinition Doctors = new(
        CardEntityKind.Doctors, "cards.doctors", "Doktorlar", "Doktorlar", "Doktor",
        "Doktor Ekle", "Doktor Düzenle", "Doktor Ekle",
        "Doktoru Düzenle", "Doktoru Sil", "doctor", "record_id",
        [
            new("diploma_registration_no", "Diploma Tescil No", Section: "Doktor Bilgileri", ConfirmOnChange: true),
            new("diploma_no", "Diploma No", Section: "Doktor Bilgileri", ConfirmOnChange: true),
            new("tc_identity_no", "T.C. Kimlik No", Section: "Doktor Bilgileri"),
            new("first_name", "Adı", Section: "Doktor Bilgileri", StoreUppercase: true),
            new("last_name", "Soyadı", Section: "Doktor Bilgileri", StoreUppercase: true),
            new("branch_id", "Branş", CardFieldKind.Lookup, LookupTarget: CardEntityKind.Branches, Section: "Mesleki Bilgiler"),
            new("certificate", "Sertifika", Section: "Mesleki Bilgiler"),
            new("facility_no", "Tesis", CardFieldKind.Lookup, LookupTarget: CardEntityKind.Faculties, Section: "Mesleki Bilgiler"),
            new("phone_numbers", "Telefon Numaraları", CardFieldKind.PhoneList, Section: "İletişim", PhonePurposeProfile: PhonePurposeProfile.Personal),
            new("email_addresses", "E-posta Adresleri", CardFieldKind.EmailList, Section: "İletişim")
        ],
        [
            new("FirstName", "Adı", 170, true),
            new("LastName", "Soyadı", 170, true),
            new("Specialty", "Branş", 190, true),
            new("FacilityName", "Tesis", 230, true)
        ],
        ["first_name", "last_name"]);

    public static readonly CardEntityDefinition Pharmacies = new(
        CardEntityKind.Pharmacies, "cards.pharmacies", "Eczaneler", "Eczaneler", "Eczane",
        "Eczane Ekle", "Eczane Düzenle", "Eczane Ekle",
        "Eczaneyi Düzenle", "Eczaneyi Sil", "pharmacy", "record_id",
        [
            new("gln_code", "GLN Kodu", Section: "Eczane Bilgileri", ConfirmOnChange: true),
            new("pharmacy_name", "Eczane Adı", Section: "Eczane Bilgileri", StoreUppercase: true),
            new("pharmacist_name", "Eczacı Adı", Section: "Eczane Bilgileri", StoreUppercase: true),
            new("address", "Adres", CardFieldKind.MultilineText, GroupName: "address", Placeholder: "Adres", Section: "İletişim"),
            new("district", "İlçe", GroupName: "address", Placeholder: "İlçe", Section: "İletişim"),
            new("city", "İl", GroupName: "address", Placeholder: "İl", Section: "İletişim"),
            new("postal_code", "Posta Kodu", GroupName: "address", Placeholder: "Posta Kodu", Section: "İletişim"),
            new("phone_numbers", "Telefon Numaraları", CardFieldKind.PhoneList, Section: "İletişim", PhonePurposeProfile: PhonePurposeProfile.Pharmacy),
            new("email_addresses", "E-posta Adresleri", CardFieldKind.EmailList, Section: "İletişim"),
            new("uts_company_numbers", "ÜTS Firma Numaraları", CardFieldKind.UtsCompanyList, Section: "Eczane Bilgileri"),
            new("tax_office", "Vergi Dairesi", GroupName: "tax", Placeholder: "Vergi dairesi", Section: "Vergi Bilgileri"),
            new("tax_no", "Vergi No", GroupName: "tax", Placeholder: "Vergi numarası", Section: "Vergi Bilgileri"),
            new("debt_balance", "Borç Miktarı", CardFieldKind.Decimal, Section: "Borç"),
            new("medicines_in_transit", "Yoldaki İlaçlar", CardFieldKind.TextArray, ShowOnAdd: false, ShowOnEdit: false)
        ],
        [
            new("PharmacyName", "Eczane Adı", 260, true),
            new("PharmacistName", "Eczacı Adı", 220, true),
            new("DebtBalance", "Borç Miktarı", 150)
        ],
        ["pharmacy_name"]);
}
