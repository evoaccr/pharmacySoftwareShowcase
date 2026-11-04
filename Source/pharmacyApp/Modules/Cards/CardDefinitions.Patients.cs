namespace pharmacyApp.Modules.Cards;

public static partial class CardDefinitions
{
    public static readonly CardEntityDefinition Patients = new(
        CardEntityKind.Patients, "cards.patients", "Hastalar", "Hastalar", "Hasta",
        "Hasta Ekle", "Hasta Düzenle", "Hasta Ekle",
        "Hastayı Düzenle", "Hastayı Sil", "patient", "record_id",
        [
            new("tc_identity_no", "T.C. Kimlik No", Required: true, Section: "Kimlik", ConfirmOnChange: true),
            new("first_name", "Ad", Required: true, Section: "Kimlik", StoreUppercase: true),
            new("last_name", "Soyad", Section: "Kimlik", StoreUppercase: true),
            new("gender", "Cinsiyet", CardFieldKind.Gender, Section: "Kimlik"),
            new("date_of_birth", "Doğum Tarihi", CardFieldKind.Date, Section: "Kimlik"),
            new("alive", "Hayatta", CardFieldKind.Boolean, ShowOnAdd: false, Section: "Kimlik"),
            new("phone_numbers", "Telefon Numaraları", CardFieldKind.PhoneList, Section: "İletişim", PhonePurposeProfile: PhonePurposeProfile.Personal, DefaultPhonePurpose: "cep"),
            new("email_addresses", "E-posta Adresleri", CardFieldKind.EmailList, Section: "İletişim"),
            new("address", "Adres", CardFieldKind.MultilineText, Placeholder: "Adres", Section: "İletişim"),
            new("kurum_id", "Kurumu", CardFieldKind.Lookup, LookupTarget: CardEntityKind.Institutions, Section: "Sağlık"),
            new("patient_type", "Hasta Türü", CardFieldKind.PatientType, ShowOnAdd: false, Section: "Sağlık"),
            new("family_doctor_id", "Aile Hekimi", CardFieldKind.Lookup, LookupTarget: CardEntityKind.Doctors, Section: "Sağlık"),
            new("family_group_id", "Aile Grubu", CardFieldKind.FamilyGroup, LookupTarget: CardEntityKind.FamilyGroups, Section: "Sağlık"),
            new("prescription_count", "Reçete Sayısı", CardFieldKind.Integer, ShowOnAdd: false, ShowOnEdit: false),
            new("debt_amount", "Borç Miktarı", CardFieldKind.Decimal, DefaultValue: 0m, Section: "Borç"),
            new("debt_credit_score", "Borç Kredi Puanı", CardFieldKind.Decimal, DefaultValue: 0m, Section: "Borç"),
            new("allow_debt", "Borç Verilebilir", CardFieldKind.Boolean, DefaultValue: true, Section: "Borç"),
            new("maximum_debt", "Azami Borç", CardFieldKind.Decimal, DefaultValue: 1500m, Section: "Borç"),
            new("sicil_no", "Sicil No", ShowOnAdd: false, Section: "Özel"),
            new("photo", "Fotoğraf", CardFieldKind.Photo),
            new("note", "Notlar", CardFieldKind.MultilineText)
        ],
        [
            new("LoyalDisplay", "Sadık Hasta", 100, SortKey: "IsLoyal"),
            new("FirstName", "Ad", 140),
            new("LastName", "Soyad", 140),
            new("Age", "Yaş", 70),
            new("PatientTypeDisplay", "Hasta Türü", 110, SortKey: "PatientTypeSort"),
            new("Phone", "Telefon", 145),
            new("PrescriptionCount", "Reçete Sayısı", 110),
            new("UpcomingMedicationCount", "Yaklaşan İlaç Sayısı", 155),
            new("DebtDisplay", "Borç", 145, SortKey: "DebtAmount"),
            new("ConsignedAmount", "Emanet", 90),
            new("FamilyGroupName", "Aile Grubu", 170),
            new("Notes", "Notlar", 220, true),
        ],
        ["first_name", "last_name"]);
}
