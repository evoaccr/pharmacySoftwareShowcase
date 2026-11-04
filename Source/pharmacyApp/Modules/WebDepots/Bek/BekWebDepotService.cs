using pharmacyApp.Modules.WebDepots.Cooperative;

namespace pharmacyApp.Modules.WebDepots.Bek;

public sealed class BekWebDepotService : CooperativeWebDepotService
{
    private BekWebDepotService()
        : base(
            depotName: "BEK E-Şube",
            homeUrl: "https://esube.bek.org.tr/",
            profileFolderName: "BekWebView2",
            usernameSelector: settings => settings.BekUsername,
            passwordSelector: settings => settings.BekPassword)
    {
    }

    public static BekWebDepotService Instance { get; } = new();
}
