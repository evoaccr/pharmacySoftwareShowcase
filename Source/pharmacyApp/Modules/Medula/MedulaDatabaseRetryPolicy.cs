using System.IO;
using System.Net.Sockets;

namespace pharmacyApp.Modules.Medula;

public static class MedulaDatabaseRetryPolicy
{
    public const int MaxAttempts = 3;

    public static bool IsTransient(Exception exception)
    {
        if (exception is OperationCanceledException) return false;
        if (exception is IOException or SocketException or TimeoutException) return true;

        var type = exception.GetType();
        if (type.FullName == "Npgsql.NpgsqlException")
        {
            var isTransient = type.GetProperty("IsTransient")?.GetValue(exception) as bool?;
            if (isTransient == true) return true;
        }

        return exception.InnerException is not null && IsTransient(exception.InnerException);
    }

    public static TimeSpan DelayForAttempt(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Clamp(attempt, 1, MaxAttempts) * 175);
}
