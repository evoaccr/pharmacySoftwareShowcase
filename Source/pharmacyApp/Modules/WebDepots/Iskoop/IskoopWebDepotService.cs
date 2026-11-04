using pharmacyApp.Modules.WebDepots.Cooperative;

namespace pharmacyApp.Modules.WebDepots.Iskoop;

public sealed class IskoopWebDepotService : CooperativeWebDepotService
{
    private IskoopWebDepotService()
        : base(
            depotName: "İSKOOP E-Şube",
            homeUrl: "https://esube.iskoop.org/",
            profileFolderName: "IskoopWebView2",
            usernameSelector: settings => settings.IskoopUsername,
            passwordSelector: settings => settings.IskoopPassword)
    {
    }

    public static IskoopWebDepotService Instance { get; } = new();
}
