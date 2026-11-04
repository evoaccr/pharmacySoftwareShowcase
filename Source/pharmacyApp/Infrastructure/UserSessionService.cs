using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Npgsql;

namespace pharmacyApp.Infrastructure;

public sealed record LoginUser(string UserId, string Username, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class UserSessionRepository
{
    public async Task<IReadOnlyList<LoginUser>> LoadActiveUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseCommand.ResolveSql("""
            SELECT user_id, first_name, last_name
            FROM public.users
            WHERE status IS TRUE
            ORDER BY user_id, first_name, last_name
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<LoginUser>();
        while (await reader.ReadAsync(cancellationToken)) users.Add(ReadUser(reader));
        return users;
    }

    public async Task<LoginUser?> LoadActiveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseCommand.ResolveSql("""
            SELECT user_id, first_name, last_name
            FROM public.users
            WHERE user_id = @user_id AND status IS TRUE
            """);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<LoginUser?> AuthenticateAsync(string userId, string password, CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseCommand.ResolveSql("""
            SELECT user_id, first_name, last_name
            FROM public.users
            WHERE user_id = @user_id AND status IS TRUE AND password = @password
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("password", password);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task SetSessionActiveAsync(string userId, bool active, CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseCommand.ResolveSql(
            "UPDATE public.users SET session_active = @active WHERE user_id = @user_id");
        command.Parameters.AddWithValue("active", active);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static LoginUser ReadUser(NpgsqlDataReader reader)
    {
        var userId = reader.GetString(0);
        var username = userId;
        var firstName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
        var lastName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
        var fullName = $"{firstName} {lastName}".Trim();
        var displayName = fullName.Length == 0 ? username : $"{username} — {fullName}";
        return new LoginUser(userId, username, displayName);
    }
}

public sealed class UserSessionManager(UserSessionRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SessionDirectory = Path.Combine(
        pharmacyApp.Infrastructure.AppPaths.DataDirectory,
        "sessions");
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? markerPath;

    public LoginUser? CurrentUser { get; private set; }

    public async Task CleanupStaleSessionsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SessionDirectory);
        var markers = LoadMarkers();
        var liveUsers = new HashSet<string>(StringComparer.Ordinal);
        var staleUsers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, marker) in markers)
        {
            if (IsProcessAlive(marker))
            {
                liveUsers.Add(marker.UserId);
                continue;
            }

            TryDelete(path);
            staleUsers.Add(marker.UserId);
        }

        foreach (var userId in staleUsers.Where(userId => !liveUsers.Contains(userId)))
            await repository.SetSessionActiveAsync(userId, false, cancellationToken);
    }

    public async Task BeginAsync(LoginUser user, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (CurrentUser is not null) return;
            Directory.CreateDirectory(SessionDirectory);
            var process = Process.GetCurrentProcess();
            var marker = new LocalSessionMarker(user.UserId, process.Id, process.StartTime.ToUniversalTime());
            markerPath = Path.Combine(SessionDirectory, $"{Guid.NewGuid():N}.json");
            File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, JsonOptions));
            DatabaseAuditContext.CurrentUserId = user.UserId;
            try
            {
                await repository.SetSessionActiveAsync(user.UserId, true, cancellationToken);
                CurrentUser = user;
            }
            catch
            {
                DatabaseAuditContext.CurrentUserId = null;
                TryDelete(markerPath);
                markerPath = null;
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EndAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (CurrentUser is null) return;
            var user = CurrentUser;
            var currentMarkerPath = markerPath;
            var anotherLocalInstanceIsAlive = LoadMarkers()
                .Any(pair => !string.Equals(pair.Path, currentMarkerPath, StringComparison.OrdinalIgnoreCase) &&
                             pair.Marker.UserId == user.UserId && IsProcessAlive(pair.Marker));
            if (!anotherLocalInstanceIsAlive)
                await repository.SetSessionActiveAsync(user.UserId, false, cancellationToken);

            if (currentMarkerPath is not null) TryDelete(currentMarkerPath);
            markerPath = null;
            CurrentUser = null;
            DatabaseAuditContext.CurrentUserId = null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<(string Path, LocalSessionMarker Marker)> LoadMarkers()
    {
        if (!Directory.Exists(SessionDirectory)) return [];
        var result = new List<(string, LocalSessionMarker)>();
        foreach (var path in Directory.EnumerateFiles(SessionDirectory, "*.json"))
        {
            try
            {
                var marker = JsonSerializer.Deserialize<LocalSessionMarker>(File.ReadAllText(path));
                if (marker is not null && !string.IsNullOrWhiteSpace(marker.UserId)) result.Add((path, marker));
                else TryDelete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
                TryDelete(path);
            }
        }
        return result;
    }

    private static bool IsProcessAlive(LocalSessionMarker marker)
    {
        try
        {
            var process = Process.GetProcessById(marker.ProcessId);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - marker.ProcessStartedUtc).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record LocalSessionMarker(string UserId, int ProcessId, DateTime ProcessStartedUtc);
}
