namespace pharmacyApp.Modules.Cards;

public static partial class CardDefinitions
{
    public static readonly CardEntityDefinition Users = new(
        CardEntityKind.Users, "cards.users", "Kullanıcılar", "Kullanıcılar", "Kullanıcı",
        "Kullanıcı Ekle", "Kullanıcı Düzenle", "Kullanıcı Ekle",
        "Kullanıcıyı Düzenle", "Kullanıcıyı Sil", "users", "user_id",
        [
            new("user_id", "Kullanıcı Adı", Required: true, Section: "Kullanıcı Bilgileri"),
            new("tc_identity_no", "T.C. Kimlik No", Required: true, Section: "Kullanıcı Bilgileri"),
            new("first_name", "Ad", Section: "Kullanıcı Bilgileri", StoreUppercase: true),
            new("last_name", "Soyadı", Section: "Kullanıcı Bilgileri", StoreUppercase: true),
            new("gender", "Cinsiyet", CardFieldKind.Gender, Section: "Kişisel Bilgiler"),
            new("date_of_birth", "Doğum Tarihi", CardFieldKind.Date, Section: "Kişisel Bilgiler"),
            new("age", "Yaş", CardFieldKind.Integer, Section: "Kişisel Bilgiler"),
            new("phone_number", "Telefon Numarası", CardFieldKind.Phone, Section: "İletişim"),
            new("permisson_level", "Yetki Seviyesi", CardFieldKind.Permission, Section: "Yetki"),
            new("photo", "Fotoğraf", CardFieldKind.Photo)
        ],
        [
            new("UserId", "Kullanıcı Adı", 150),
            new("FirstName", "Ad", 140),
            new("LastName", "Soyadı", 140),
            new("PermissionLevel", "Yetki Seviyesi", 130),
            new("UserPoints", "Puan", 85),
            new("Active", "Aktif", 85),
            new("StatusDisplay", "Durum", 95)
        ],
        ["first_name", "last_name"],
        KeyKind: CardFieldKind.Text);
}
