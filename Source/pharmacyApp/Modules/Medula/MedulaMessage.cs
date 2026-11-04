namespace pharmacyApp.Modules.Medula;

public enum MedulaMessageSeverity
{
    Error,
    Success,
    Notice
}

public sealed record MedulaMessage(
    string Text,
    MedulaMessageSeverity Severity,
    DateTimeOffset Timestamp);
