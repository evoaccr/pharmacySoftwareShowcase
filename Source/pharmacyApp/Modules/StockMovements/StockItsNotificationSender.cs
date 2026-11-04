using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.StockMovements;

public sealed record StockItsSendResult(Guid OperationId, string NotificationId);

public sealed class StockItsNotificationSender
{
    private readonly ItsApiClient client = new();

    public async Task<StockItsSendResult> SendAsync(StockItsNotificationKind kind,
        IReadOnlyList<string> qrCodes, string userId, CancellationToken cancellationToken)
    {
        var normalized = qrCodes.Select(code => code.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var products = normalized.Select(Parse).ToArray();
        var settings = ApplicationSettingsStore.Current;
        var credentials = new ItsCredentials(settings.ItsUsername, settings.ItsPassword);
        object request = kind == StockItsNotificationKind.Acceptance
            ? ItsAcceptanceRequestFactory.Create(products)
            : ItsPharmacyRequestFactory.Create(
                kind == StockItsNotificationKind.Sale ? ItsPharmacyOperation.RetailSale : ItsPharmacyOperation.RetailSaleCancellation,
                settings.ItsPharmacyGln, products);
        var operationId = await PrepareAsync(kind, normalized, request, settings.ItsPharmacyGln, userId, cancellationToken);
        ItsNotificationResult result;
        try
        {
            if (kind == StockItsNotificationKind.Acceptance)
                result = await client.SendAcceptanceAsync(ItsEnvironment.Production, credentials,
                    (ItsAcceptanceRequest)request, cancellationToken);
            else
            {
                var operation = kind == StockItsNotificationKind.Sale
                    ? ItsPharmacyOperation.RetailSale : ItsPharmacyOperation.RetailSaleCancellation;
                result = await client.SendNotificationAsync(ItsEnvironment.Production, operation, credentials,
                    (ItsPharmacyNotificationRequest)request, cancellationToken);
            }
        }
        catch (ItsNotificationNotSentException exception)
        {
            await SetStateAsync(operationId, "definitely_not_sent", exception.Message, null, true);
            throw new InvalidOperationException("İTS bildirimi kesin olarak gönderilmedi; güvenle yeniden deneyebilirsiniz.", exception);
        }
        catch (Exception exception)
        {
            await SetStateAsync(operationId, "reconciliation_required", exception.Message, null, false);
            throw new InvalidOperationException($"İTS sonucu belirsiz. İşlem {operationId}; aynı karekodu yeniden göndermeyin.", exception);
        }
        if (!result.IsSuccessful)
        {
            await SetStateAsync(operationId, "failed", Failure(result), result, true);
            throw new InvalidOperationException("İTS bildirimi başarısız: " + Failure(result));
        }
        await SetStateAsync(operationId, "remote_confirmed", null, result, false);
        return new StockItsSendResult(operationId, result.NotificationId ?? "—");
    }

    public async Task MarkLocalCompletedAsync(Guid operationId, int? manualMovementId, CancellationToken token)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.stock_its_operations SET operation_status='local_completed',
              local_completed_at=CURRENT_TIMESTAMP, manual_movement_id=@manual_id
            WHERE operation_id=@id AND operation_status='remote_confirmed'
            """, connection, tx);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.Add(new NpgsqlParameter("manual_id", NpgsqlDbType.Integer) { Value = manualMovementId ?? (object)DBNull.Value });
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("İTS işlemi yerel tamamlandı olarak işaretlenemedi.");
        await using var items = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "UPDATE public.stock_its_operation_items SET is_active=FALSE WHERE operation_id=@id", connection, tx);
        items.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, operationId);
        await items.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }

    private static async Task<Guid> PrepareAsync(StockItsNotificationKind kind, string[] qrs,
        object request, string gln, string user, CancellationToken token)
    {
        var id = Guid.NewGuid();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(token);
        await ManualStockSchema.EnsureAsync(connection, token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await using (var active = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT qr_code FROM public.retail_its_operation_items
            WHERE is_active AND qr_code=ANY(@qrs) LIMIT 1
            """, connection, tx))
        {
            active.Parameters.AddWithValue("qrs", NpgsqlDbType.Array | NpgsqlDbType.Varchar, qrs);
            if (await active.ExecuteScalarAsync(token) is string activeQr)
                throw new InvalidOperationException($"Karekod bekleyen veya mutabakat gerektiren bir satış İTS işleminde: {activeQr}");
        }
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public.stock_its_operations
              (operation_id,operation_type,operation_status,pharmacy_gln,request_payload,qr_codes,requested_by)
            VALUES (@id,@type,'prepared',@gln,@payload::jsonb,@qrs,@user)
            """, connection, tx);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, UnnotifiedItsMovementRepository.ToDatabase(kind));
        command.Parameters.AddWithValue("gln", NpgsqlDbType.Varchar, gln);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Varchar, JsonSerializer.Serialize(request));
        command.Parameters.AddWithValue("qrs", NpgsqlDbType.Array | NpgsqlDbType.Varchar, qrs);
        command.Parameters.AddWithValue("user", NpgsqlDbType.Varchar, string.IsNullOrWhiteSpace(user) ? "admin" : user);
        await command.ExecuteNonQueryAsync(token);
        foreach (var qr in qrs)
        {
            await using var item = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "INSERT INTO public.stock_its_operation_items(operation_id,qr_code) VALUES(@id,@qr)", connection, tx);
            item.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            item.Parameters.AddWithValue("qr", NpgsqlDbType.Varchar, qr);
            try { await item.ExecuteNonQueryAsync(token); }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            { throw new InvalidOperationException($"Karekod bekleyen veya mutabakat gerektiren bir İTS işleminde: {qr}", exception); }
        }
        await tx.CommitAsync(token);
        return id;
    }

    private static async Task SetStateAsync(Guid id, string status, string? error,
        ItsNotificationResult? result, bool release)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.stock_its_operations SET operation_status=@status,
              remote_confirmed_at=CASE WHEN @status='remote_confirmed' THEN CURRENT_TIMESTAMP ELSE remote_confirmed_at END,
              notification_id=@notification,http_status=@http,response_body=@body,
              fault_code=@fault_code,fault_message=@fault_message,error_text=@error
            WHERE operation_id=@id
            """, connection, tx);
        command.Parameters.AddWithValue("status", status); command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("notification", NpgsqlDbType.Varchar) { Value = result?.NotificationId ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("http", NpgsqlDbType.Integer) { Value = result?.HttpStatusCode ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Text) { Value = result?.ResponseBody ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("fault_code", NpgsqlDbType.Varchar) { Value = result?.FaultCode ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("fault_message", NpgsqlDbType.Text) { Value = result?.FaultMessage ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = error ?? (object)DBNull.Value });
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        if (release)
        {
            await using var item = pharmacyApp.Infrastructure.DatabaseCommand.Create(
                "UPDATE public.stock_its_operation_items SET is_active=FALSE WHERE operation_id=@id", connection, tx);
            item.Parameters.AddWithValue("id", id); await item.ExecuteNonQueryAsync(CancellationToken.None);
        }
        await tx.CommitAsync(CancellationToken.None);
    }

    private static string Failure(ItsNotificationResult result) =>
        result.Products.FirstOrDefault()?.ResultDisplay ?? result.FaultMessage ?? $"HTTP {result.HttpStatusCode}";
    private static ItsQrProduct Parse(string qrCode)
    {
        if (!ItsQrParser.TryParse(qrCode, out var product, out var error) || product is null)
            throw new InvalidOperationException($"Karekod ayrıştırılamadı: {qrCode}. {error}");
        return product;
    }
}
