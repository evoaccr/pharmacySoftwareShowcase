namespace pharmacyApp.Modules.Medula;

public static class MedulaStatusText
{
    public static string For(MedulaConnectionState state) => state switch
    {
        MedulaConnectionState.Connected => "Online",
        MedulaConnectionState.Connecting => "Connecting",
        MedulaConnectionState.TemporarilyOffline => "Temporary offline",
        _ => "Offline"
    };
}
