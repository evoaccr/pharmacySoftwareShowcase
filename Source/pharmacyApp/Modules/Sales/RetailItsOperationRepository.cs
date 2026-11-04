using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;

namespace pharmacyApp.Modules.Sales;

public sealed class RetailItsOperationRepository : IRetailItsOperationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public async Task<Guid> PrepareAsync(
        RetailItsOperationKind operationKind,
        string pharmacyGln,
        ItsPharmacyNotificationRequest request,
        IReadOnlyList<string> qrCodes,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            const string insertOperationSql = """
                INSERT INTO public.retail_its_operations (
                    operation_id, operation_type, operation_status, pharmacy_gln,
                    request_payload, qr_codes, requested_by)
                VALUES (
                    @operation_id, @operation_type, 'prepared', @pharmacy_gln,
                    @request_payload::jsonb, @qr_codes, @requested_by)
                """;
            await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(insertOperationSql, connection, transaction))
            {
                command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
                command.Parameters.AddWithValue("operation_type", NpgsqlDbType.Varchar, ToDatabaseType(operationKind));
                command.Parameters.AddWithValue("pharmacy_gln", NpgsqlDbType.Varchar, pharmacyGln.Trim());
                command.Parameters.AddWithValue(
                    "request_payload",
                    NpgsqlDbType.Varchar,
                    JsonSerializer.Serialize(request, JsonOptions));
                command.Parameters.AddWithValue(
                    "qr_codes",
                    NpgsqlDbType.Array | NpgsqlDbType.Varchar,
                    qrCodes.ToArray());
                command.Parameters.AddWithValue("requested_by", NpgsqlDbType.Varchar, requestedBy);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string insertItemSql = """
                INSERT INTO public.retail_its_operation_items (operation_id, qr_code, is_active)
                VALUES (@operation_id, @qr_code, TRUE)
                """;
            foreach (var qrCode in qrCodes)
            {
                await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(insertItemSql, connection, transaction);
                command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
                command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return operationId;
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            exception.ConstraintName == "ux_retail_its_operation_items_active_qr")
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException(
                "Karekodlardan biri bekleyen veya mutabakat gerektiren başka bir İTS işleminde bulunuyor. Aynı karekodu yeniden göndermeyin.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task MarkRemoteConfirmedAsync(
        Guid operationId,
        ItsNotificationResult result,
        CancellationToken cancellationToken) =>
        UpdateRemoteStateAsync(
            operationId,
            "remote_confirmed",
            result,
            null,
            releaseQrCodes: false,
            cancellationToken);

    public Task MarkDefinitelyNotSentAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken) =>
        UpdateRemoteStateAsync(
            operationId,
            "definitely_not_sent",
            null,
            error,
            releaseQrCodes: true,
            cancellationToken);

    public Task MarkFailedAsync(
        Guid operationId,
        ItsNotificationResult result,
        string error,
        CancellationToken cancellationToken) =>
        UpdateRemoteStateAsync(
            operationId,
            "failed",
            result,
            error,
            releaseQrCodes: true,
            cancellationToken);

    public Task MarkReconciliationRequiredAsync(
        Guid operationId,
        ItsNotificationResult? result,
        string error,
        CancellationToken cancellationToken) =>
        UpdateRemoteStateAsync(
            operationId,
            "reconciliation_required",
            result,
            error,
            releaseQrCodes: false,
            cancellationToken);

    public async Task MarkLocalCompletedAsync(
        Guid operationId,
        RetailItsOperationKind operationKind,
        int localRecordId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var referenceColumn = operationKind == RetailItsOperationKind.Sale ? "sale_id" : "refund_id";
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create($"""
            UPDATE public.retail_its_operations
               SET operation_status='local_completed',
                   local_completed_at=CURRENT_TIMESTAMP,
                   {referenceColumn}=@local_record_id
             WHERE operation_id=@operation_id
               AND operation_type=@operation_type
               AND operation_status='remote_confirmed'
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            command.Parameters.AddWithValue("operation_type", NpgsqlDbType.Varchar, ToDatabaseType(operationKind));
            command.Parameters.AddWithValue("local_record_id", NpgsqlDbType.Integer, localRecordId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("İTS işlem kaydı yerel tamamlanmış durumuna getirilemedi.");
        }

        if (operationKind == RetailItsOperationKind.Sale)
        {
            await using var saleCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.sales sale
                   SET its_no=COALESCE(operation.notification_id, sale.its_no)
                  FROM public.retail_its_operations operation
                 WHERE sale.record_id=@sale_id
                   AND operation.operation_id=@operation_id
                """, connection, transaction);
            saleCommand.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, localRecordId);
            saleCommand.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            if (await saleCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("İTS bildirim numarası satış kaydına bağlanamadı.");
        }

        await ReleaseQrCodesAsync(connection, transaction, operationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpdateRemoteStateAsync(
        Guid operationId,
        string status,
        ItsNotificationResult? result,
        string? error,
        bool releaseQrCodes,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE public.retail_its_operations
               SET operation_status=@status,
                   remote_confirmed_at=CASE WHEN @status='remote_confirmed'
                       THEN COALESCE(remote_confirmed_at, CURRENT_TIMESTAMP)
                       ELSE remote_confirmed_at END,
                   notification_id=@notification_id,
                   http_status=@http_status,
                   response_body=@response_body,
                   fault_code=@fault_code,
                   fault_message=@fault_message,
                   error_text=@error_text
             WHERE operation_id=@operation_id
               AND operation_status='prepared'
            """;
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
            AddNullable(command, "notification_id", NpgsqlDbType.Varchar, result?.NotificationId);
            AddNullable(command, "http_status", NpgsqlDbType.Integer, result?.HttpStatusCode);
            AddNullable(command, "response_body", NpgsqlDbType.Text, result?.ResponseBody);
            AddNullable(command, "fault_code", NpgsqlDbType.Varchar, result?.FaultCode);
            AddNullable(command, "fault_message", NpgsqlDbType.Text, result?.FaultMessage);
            AddNullable(command, "error_text", NpgsqlDbType.Text, error);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("İTS işlem kaydının durumu güncellenemedi.");
        }

        if (releaseQrCodes)
            await ReleaseQrCodesAsync(connection, transaction, operationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ReleaseQrCodesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.retail_its_operation_items
               SET is_active=FALSE
             WHERE operation_id=@operation_id
               AND is_active
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static string ToDatabaseType(RetailItsOperationKind operationKind) => operationKind switch
    {
        RetailItsOperationKind.Sale => "SALE",
        RetailItsOperationKind.SaleCancellation => "SALE_CANCELLATION",
        _ => throw new ArgumentOutOfRangeException(nameof(operationKind))
    };
}
