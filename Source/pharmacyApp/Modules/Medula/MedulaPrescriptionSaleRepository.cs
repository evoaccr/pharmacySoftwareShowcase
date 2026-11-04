using System.Data;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Medula;

public sealed record MedulaPrescriptionSalePreparation(
    Guid OperationId,
    int SaleId,
    IReadOnlyList<string> QrCodes);

/// <summary>
/// Persists the four distinct Medula prescription-sale transitions. A saved
/// prescription creates an invisible pending receivable. Stock is moved only
/// after Medula definitively confirms ITS Satış Bildirimi, and the sale is
/// finalized only after Medula confirms Karekod Sonlandır.
/// </summary>
public sealed class MedulaPrescriptionSaleRepository
{
    private const string ItsSaleOperation = "ITS_SALE";
    private const string FinalizeOperation = "QR_FINALIZE";
    private const string FinalizationCancelOperation = "QR_FINALIZE_CANCEL";
    private const string ItsSaleCancelOperation = "ITS_SALE_CANCEL";

    public async Task<int> EnsurePendingSaleAsync(
        MedulaPrescriptionPersistenceRecord record,
        Guid prescriptionUid,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await EnsurePendingSaleOnceAsync(
                    record, prescriptionUid, cancellationToken);
            }
            catch (Exception exception) when (
                attempt < MedulaDatabaseRetryPolicy.MaxAttempts
                && MedulaDatabaseRetryPolicy.IsTransient(exception))
            {
                // A Tailscale/network interruption can leave stale idle connections in the
                // Npgsql pool. The transaction is idempotent by prescription_uid, so retrying
                // the complete unit of work on a new connection is safe.
                NpgsqlConnection.ClearAllPools();
                await Task.Delay(
                    MedulaDatabaseRetryPolicy.DelayForAttempt(attempt),
                    cancellationToken);
            }
        }
    }

    private static async Task<int> EnsurePendingSaleOnceAsync(
        MedulaPrescriptionPersistenceRecord record,
        Guid prescriptionUid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var amounts = MedulaPrescriptionSaleCalculator.Calculate(
            record.PatientInsuranceType,
            record.PatientTotalPayment,
            record.InstitutionTotalPayment,
            record.PrescriptionCopaymentCash,
            record.ExaminationCopaymentCash,
            record.MedicationCopayment);
        var patientAmount = amounts.PatientAmount;
        var primaryInstitutionAmount = amounts.PrimaryInstitutionAmount;
        var secondaryInstitutionAmount = amounts.SecondaryInstitutionAmount;
        var owedAmount = amounts.OwedAmount;
        var totalAmount = amounts.TotalAmount;

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var patientId = await ReadPatientIdAsync(
                connection, transaction, record.PatientTcIdentityNo, cancellationToken);
            var existing = await ReadSaleAsync(connection, transaction, prescriptionUid, cancellationToken);
            int saleId;
            if (existing is null
                || MedulaPrescriptionSaleLifecycleRules.RequiresNewSaleAttempt(existing.Value.Status))
            {
                const string insertSql = """
                    INSERT INTO public.sales (
                        patient_id, institution_amount, total_amount,
                        payement_finished, institution_payement_finished, debted,
                        prescription_no, user_no, "timestamp", notes, with_iskonto,
                        prescription_uid, sale_status, patient_amount,
                        institution_secondary_amount, owed_amount)
                    VALUES (
                        @patient_id, @institution_amount, @total_amount,
                        FALSE, FALSE, FALSE,
                        @prescription_no, NULLIF(@user_id, ''), CURRENT_TIMESTAMP,
                        'Medula reçetesi için bekleyen satış.', FALSE,
                        @prescription_uid, 'pending', @patient_amount,
                        @secondary_amount, @owed_amount)
                    RETURNING record_id
                    """;
                await using var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create(insertSql, connection, transaction);
                AddSaleParameters(insert, record, prescriptionUid, patientId, patientAmount,
                    primaryInstitutionAmount, secondaryInstitutionAmount, owedAmount, totalAmount);
                saleId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                saleId = existing.Value.SaleId;
                if (!existing.Value.Status.Equals("pending", StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return saleId;
                }

                const string updateSql = """
                    UPDATE public.sales
                       SET patient_id=@patient_id,
                           institution_amount=@institution_amount,
                           total_amount=@total_amount,
                           prescription_no=@prescription_no,
                           user_no=NULLIF(@user_id, ''),
                           patient_amount=@patient_amount,
                           institution_secondary_amount=@secondary_amount,
                           owed_amount=@owed_amount,
                           notes='Medula reçetesi için bekleyen satış.'
                     WHERE record_id=@sale_id
                    """;
                await using var update = pharmacyApp.Infrastructure.DatabaseCommand.Create(updateSql, connection, transaction);
                AddSaleParameters(update, record, prescriptionUid, patientId, patientAmount,
                    primaryInstitutionAmount, secondaryInstitutionAmount, owedAmount, totalAmount);
                update.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReplacePendingPaymentsAsync(
                connection,
                transaction,
                saleId,
                patientAmount,
                primaryInstitutionAmount,
                secondaryInstitutionAmount,
                owedAmount,
                cancellationToken);
            await RefreshPaymentStatusAsync(connection, transaction, saleId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saleId;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original database/transport failure. A broken connection
                // cannot roll back explicitly; PostgreSQL rolls it back when it closes.
            }
            throw;
        }
    }

    public Task<MedulaPrescriptionSalePreparation> PrepareItsSaleAsync(
        Guid prescriptionUid,
        IReadOnlyList<string> qrCodes,
        string userId,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? untrackedQrCodes = null) =>
        PrepareOperationAsync(
            prescriptionUid, ItsSaleOperation, "pending", qrCodes,
            untrackedQrCodes ?? [], userId, cancellationToken);

    public async Task<IReadOnlySet<string>> ReadReportedQrCodesAsync(
        Guid prescriptionUid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            WITH current_sale AS (
                SELECT record_id, sale_status
                  FROM public.sales
                 WHERE prescription_uid=@uid
                 ORDER BY record_id DESC
                 LIMIT 1
            )
            SELECT item.qr_code
              FROM current_sale sale
              JOIN public."salesQrItems" item ON item.sale_id=sale.record_id
             WHERE sale.sale_status IN ('medula_reported', 'completed')
               AND item.item_source='prescription'
            """, connection);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task ConfigureItsPatientPaymentsAsync(
        Guid prescriptionUid,
        IReadOnlyList<RetailSalePaymentInput> patientPayments,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var saleReference = await ReadSaleAsync(
                connection, transaction, prescriptionUid, cancellationToken)
                ?? throw new InvalidOperationException("Reçete için bekleyen satış kaydı bulunamadı.");
            var sale = await ReadSaleByIdAsync(
                connection, transaction, saleReference.SaleId, cancellationToken);
            if (!sale.Status.Equals("pending", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Reçete satışı hasta ödemesi için hazır değil: {sale.Status}");
            var validation = ValidatePatientPayments(sale.PatientAmount, sale.PatientId, patientPayments);
            await ReplacePatientPaymentsAsync(
                connection, transaction, saleReference.SaleId, patientPayments,
                completed: false, cancellationToken);
            await UpdatePatientPaymentFlagsAsync(
                connection, transaction, saleReference.SaleId,
                validation.DebtAmount, validation.WithDiscount, cancellationToken);
            await RefreshPaymentStatusAsync(
                connection, transaction, saleReference.SaleId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task<MedulaPrescriptionSalePreparation> PrepareFinalizationAsync(
        Guid prescriptionUid,
        string userId,
        CancellationToken cancellationToken = default) =>
        PrepareOperationAsync(
            prescriptionUid, FinalizeOperation, "medula_reported", [], [], userId, cancellationToken);

    public Task<MedulaPrescriptionSalePreparation> PrepareFinalizationCancellationAsync(
        Guid prescriptionUid,
        string userId,
        CancellationToken cancellationToken = default) =>
        PrepareOperationAsync(
            prescriptionUid, FinalizationCancelOperation, "completed", [], [], userId, cancellationToken);

    public Task<MedulaPrescriptionSalePreparation> PrepareItsSaleCancellationAsync(
        Guid prescriptionUid,
        IReadOnlyList<string> qrCodes,
        string userId,
        CancellationToken cancellationToken = default) =>
        PrepareOperationAsync(
            prescriptionUid, ItsSaleCancelOperation, "medula_reported", qrCodes, [], userId, cancellationToken);

    public Task MarkRemoteConfirmedAsync(
        Guid operationId,
        string remoteMessage,
        CancellationToken cancellationToken = default) =>
        SetOperationStatusAsync(
            operationId, "remote_confirmed", remoteMessage, null, cancellationToken);

    public Task MarkCancelledAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        SetOperationStatusAsync(operationId, "cancelled", null, null, cancellationToken);

    public Task MarkFailedAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken = default) =>
        SetOperationStatusAsync(operationId, "failed", null, error, cancellationToken);

    public Task MarkReconciliationRequiredAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken = default) =>
        SetOperationStatusAsync(
            operationId, "reconciliation_required", null, error, cancellationToken);

    public async Task CompleteItsSaleAsync(
        Guid operationId,
        string remoteMessage,
        IReadOnlyList<RetailSalePaymentInput> patientPayments,
        MedulaPrescriptionDebtCollection debtCollection,
        string ownerTcIdentityNumber,
        string recipientTcIdentityNumber,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var operation = await ReadOperationAsync(
                connection, transaction, operationId, ItsSaleOperation, cancellationToken);
            if (operation.Status == "local_completed")
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (operation.Status != "remote_confirmed")
                throw new InvalidOperationException("Medula ITS satış onayı yerel işleme kaydedilmedi.");

            var sale = await ReadSaleByIdAsync(connection, transaction, operation.SaleId, cancellationToken);
            if (sale.Status is not ("pending" or "medula_reported"))
                throw new InvalidOperationException($"Reçete satışı beklenen durumda değil: {sale.Status}");
            var validation = ValidatePatientPayments(
                sale.PatientAmount, sale.PatientId, patientPayments);

            var stockRows = await LoadAndLockQrStockAsync(
                connection, transaction, operation.QrCodes, operation.UntrackedQrCodes,
                operation.PrescriptionUid, cancellationToken);
            var referenceCode = $"P{operation.SaleId}";
            await DatabaseAuditContext.SetReferenceAsync(
                connection, transaction, referenceCode, sale.UserId, cancellationToken);
            var movementAddress = RetailSaleMovementAddress.Format(sale.PatientId);
            foreach (var stock in stockRows)
            {
                await InsertSaleItemAsync(
                    connection, transaction, operation.SaleId, stock, cancellationToken);
                if (stock.InventoryTracked)
                    await InsertPrescriptionMovementAsync(
                        connection,
                        transaction,
                        stock.QrCode,
                        referenceCode,
                        movementAddress,
                        sale.UserId,
                        stock.GrossPrice,
                        cancellationToken);
            }

            await ReplacePatientPaymentsAsync(
                connection, transaction, operation.SaleId, patientPayments,
                completed: true, cancellationToken);
            await CollectExistingPatientDebtsAsync(
                connection,
                transaction,
                ownerTcIdentityNumber,
                recipientTcIdentityNumber,
                debtCollection,
                cancellationToken);
            if (validation.DebtAmount > 0m)
                await IncreasePatientDebtAsync(
                    connection, transaction, sale.PatientId!.Value, validation.DebtAmount, cancellationToken);

            await using (var saleUpdate = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.sales
                   SET sale_status='medula_reported',
                       debted=@debted,
                       with_iskonto=@with_iskonto,
                       medula_sale_confirmed_at=COALESCE(medula_sale_confirmed_at, CURRENT_TIMESTAMP),
                       "timestamp"=CURRENT_TIMESTAMP,
                       notes='Medula ITS satış bildirimi tamamlandı; karekod sonlandırma bekleniyor.'
                 WHERE record_id=@sale_id
                """, connection, transaction))
            {
                saleUpdate.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, operation.SaleId);
                saleUpdate.Parameters.AddWithValue("debted", NpgsqlDbType.Boolean, validation.DebtAmount > 0m);
                saleUpdate.Parameters.AddWithValue("with_iskonto", NpgsqlDbType.Boolean, validation.WithDiscount);
                await saleUpdate.ExecuteNonQueryAsync(cancellationToken);
            }
            await RefreshPaymentStatusAsync(connection, transaction, operation.SaleId, cancellationToken);
            await CompleteOperationAsync(
                connection, transaction, operationId, remoteMessage, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CompleteFinalizationAsync(
        Guid operationId,
        string remoteMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var operation = await ReadOperationAsync(
                connection, transaction, operationId, FinalizeOperation, cancellationToken);
            if (operation.Status == "local_completed")
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (operation.Status != "remote_confirmed")
                throw new InvalidOperationException("Medula karekod sonlandırma onayı yerel işleme kaydedilmedi.");

            await using (var update = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.sales
                   SET sale_status='completed',
                       prescription_finalized_at=COALESCE(prescription_finalized_at, CURRENT_TIMESTAMP),
                       notes='Medula reçeteli satış ve karekod sonlandırma tamamlandı.'
                 WHERE record_id=@sale_id
                   AND sale_status IN ('medula_reported', 'completed')
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, operation.SaleId);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Sonlandırılacak reçete satışı bulunamadı.");
            }
            await CompleteOperationAsync(
                connection, transaction, operationId, remoteMessage, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CompleteFinalizationCancellationAsync(
        Guid operationId,
        string remoteMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var operation = await ReadOperationAsync(
                connection, transaction, operationId, FinalizationCancelOperation, cancellationToken);
            if (operation.Status == "local_completed")
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (operation.Status != "remote_confirmed")
                throw new InvalidOperationException(
                    "Medula karekod sonlandırma iptal onayı yerel işleme kaydedilmedi.");

            await using (var update = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.sales
                   SET sale_status='medula_reported',
                       prescription_finalized_at=NULL,
                       notes='Medula karekod sonlandırması iptal edildi; ITS satış iptali bekleniyor.'
                 WHERE record_id=@sale_id
                   AND sale_status IN ('completed', 'medula_reported')
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, operation.SaleId);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Sonlandırma iptali uygulanacak reçete satışı bulunamadı.");
            }
            await CompleteOperationAsync(
                connection, transaction, operationId, remoteMessage, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> CompleteItsSaleCancellationAsync(
        Guid operationId,
        string remoteMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var operation = await ReadOperationAsync(
                connection, transaction, operationId, ItsSaleCancelOperation, cancellationToken);
            if (operation.Status == "local_completed")
            {
                var existingRefundId = await ReadPrescriptionRefundIdAsync(
                    connection, transaction, operation.SaleId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return existingRefundId ?? throw new InvalidOperationException(
                    "Tamamlanan reçete satış iptalinin yerel iade kaydı bulunamadı.");
            }
            if (operation.Status != "remote_confirmed")
                throw new InvalidOperationException("Medula ITS satış iptal onayı yerel işleme kaydedilmedi.");

            var sale = await ReadCancellationSaleAsync(
                connection, transaction, operation.SaleId, cancellationToken);
            if (sale.Status is not ("medula_reported" or "cancelled"))
                throw new InvalidOperationException($"Reçete satışı iptal için hazır değil: {sale.Status}");

            var refundId = await InsertPrescriptionRefundAsync(
                connection, transaction, operation, sale, cancellationToken);
            var items = await ReadPrescriptionSaleItemsAsync(
                connection, transaction, operation.SaleId, operation.QrCodes, cancellationToken);
            if (items.Count != operation.QrCodes.Length)
                throw new InvalidOperationException(
                    "İptal edilen ITS karekodlarının tamamı yerel reçete satışında bulunamadı.");

            var movementAddress = RetailSaleMovementAddress.Format(sale.PatientId);
            foreach (var item in items)
            {
                await InsertPrescriptionRefundItemAsync(
                    connection, transaction, refundId, operation.SaleId, item, cancellationToken);
                if (MedulaPrescriptionSaleLifecycleRules.ShouldCreateInventoryMovement(item.InventoryTracked))
                    await InsertPrescriptionReturnMovementAsync(
                        connection, transaction, item.QrCode, $"G{refundId}", movementAddress,
                        operation.RequestedBy, cancellationToken);
            }

            await InsertPrescriptionRefundPaymentAsync(
                connection, transaction, refundId, sale.PatientAmount, cancellationToken);
            await using (var cancelPayments = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public."salesPayements"
                   SET cancelled_at=COALESCE(cancelled_at, CURRENT_TIMESTAMP)
                 WHERE sale_id=@sale_id
                   AND (payment_role='patient'
                        OR payment_role LIKE 'patient_%'
                        OR payment_role IN ('institution_primary', 'institution_secondary'))
                """, connection, transaction))
            {
                cancelPayments.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, operation.SaleId);
                await cancelPayments.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var cancelSale = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                UPDATE public.sales
                   SET sale_status='cancelled',
                       cancelled_at=COALESCE(cancelled_at, CURRENT_TIMESTAMP),
                       notes='Medula ITS satış iptali tamamlandı; reçeteli satış iade edildi.'
                 WHERE record_id=@sale_id
                """, connection, transaction))
            {
                cancelSale.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, operation.SaleId);
                if (await cancelSale.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("İptal edilecek reçete satışı bulunamadı.");
            }
            await RefreshRefundPaymentStatusAsync(
                connection, transaction, refundId, cancellationToken);
            await CompleteOperationAsync(
                connection, transaction, operationId, remoteMessage, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return refundId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<MedulaPrescriptionSalePreparation> PrepareOperationAsync(
        Guid prescriptionUid,
        string operationType,
        string requiredSaleStatus,
        IReadOnlyList<string> qrCodes,
        IReadOnlyCollection<string> untrackedQrCodes,
        string userId,
        CancellationToken cancellationToken)
    {
        var normalizedCodes = qrCodes
            .Select(code => NormalizeOperationQrCode(operationType, code))
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (operationType is ItsSaleOperation or ItsSaleCancelOperation && normalizedCodes.Length == 0)
            throw new InvalidOperationException("ITS satış işlemi için karekod bulunamadı.");
        var normalizedUntrackedCodes = untrackedQrCodes
            .Select(RetailQrCodeNormalizer.Normalize)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedUntrackedCodes.Except(normalizedCodes, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Stok dışı karekod seçimi reçetedeki karekodlarla eşleşmiyor.");

        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var sale = await ReadSaleAsync(connection, transaction, prescriptionUid, cancellationToken)
                ?? throw new InvalidOperationException("Reçeteye ait bekleyen satış kaydı bulunamadı.");
            if (sale.Status != requiredSaleStatus)
                throw new InvalidOperationException(
                    $"Reçete satışı {operationType} işlemi için hazır değil: {sale.Status}");

            await using (var active = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                SELECT operation_status
                  FROM public.prescription_sale_operations
                 WHERE prescription_uid=@uid AND operation_type=@operation_type
                   AND operation_status IN ('prepared', 'remote_confirmed', 'reconciliation_required')
                 FOR UPDATE
                """, connection, transaction))
            {
                active.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
                active.Parameters.AddWithValue("operation_type", NpgsqlDbType.Varchar, operationType);
                if (await active.ExecuteScalarAsync(cancellationToken) is string activeStatus)
                    throw new InvalidOperationException(
                        activeStatus == "reconciliation_required"
                            ? "Önceki Medula işlemi yerel mutabakat bekliyor; işlem tekrar gönderilmedi."
                            : "Aynı Medula işlemi zaten devam ediyor; işlem tekrar gönderilmedi.");
            }

            var operationId = Guid.NewGuid();
            await using (var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                INSERT INTO public.prescription_sale_operations (
                    operation_id, prescription_uid, sale_id, operation_type,
                    operation_status, qr_codes, untracked_qr_codes, requested_by)
                VALUES (
                    @operation_id, @uid, @sale_id, @operation_type,
                    'prepared', @qr_codes, @untracked_qr_codes, NULLIF(@user_id, ''))
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
                insert.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
                insert.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, sale.SaleId);
                insert.Parameters.AddWithValue("operation_type", NpgsqlDbType.Varchar, operationType);
                insert.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, normalizedCodes);
                insert.Parameters.AddWithValue(
                    "untracked_qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, normalizedUntrackedCodes);
                insert.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId?.Trim() ?? string.Empty);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new MedulaPrescriptionSalePreparation(operationId, sale.SaleId, normalizedCodes);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string NormalizeOperationQrCode(string operationType, string code)
    {
        var normalized = RetailQrCodeNormalizer.Normalize(code);
        // Medula renders the ITS AI 21/17 boundary with a visual hyphen after a
        // completed sale. That character is not part of the serialized QR and
        // must not prevent matching the original prescription sale item.
        return operationType == ItsSaleCancelOperation
            ? MedulaPrescriptionSaleLifecycleRules.NormalizeItsCancellationQr(normalized)
            : normalized;
    }

    private static async Task SetOperationStatusAsync(
        Guid operationId,
        string status,
        string? remoteMessage,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE public.prescription_sale_operations
               SET operation_status=@status,
                   remote_confirmed_at=CASE WHEN @status='remote_confirmed'
                       THEN COALESCE(remote_confirmed_at, CURRENT_TIMESTAMP)
                       ELSE remote_confirmed_at END,
                   remote_message=COALESCE(NULLIF(@remote_message, ''), remote_message),
                   error_text=COALESCE(NULLIF(@error, ''), error_text)
             WHERE operation_id=@operation_id
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
        command.Parameters.AddWithValue("remote_message", NpgsqlDbType.Text, remoteMessage ?? string.Empty);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, error ?? string.Empty);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Reçete satış işlem kaydı bulunamadı.");
    }

    private static async Task CompleteOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        string remoteMessage,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.prescription_sale_operations
               SET operation_status='local_completed',
                   local_completed_at=CURRENT_TIMESTAMP,
                   remote_message=COALESCE(NULLIF(@message, ''), remote_message),
                   error_text=NULL
             WHERE operation_id=@operation_id
            """, connection, transaction);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Text, remoteMessage ?? string.Empty);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Reçete satış işlem kaydı tamamlanamadı.");
    }

    private static async Task<(int SaleId, string Status)?> ReadSaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid prescriptionUid,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id, sale_status
              FROM public.sales
             WHERE prescription_uid=@uid
             ORDER BY CASE WHEN sale_status='cancelled' THEN 1 ELSE 0 END,
                      record_id DESC
             LIMIT 1
             FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("uid", NpgsqlDbType.Uuid, prescriptionUid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private static async Task<(int? PatientId, string UserId, string Status, decimal PatientAmount)> ReadSaleByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT patient_id, COALESCE(user_no, ''), sale_status, patient_amount
              FROM public.sales
             WHERE record_id=@sale_id
             FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Reçete satışı bulunamadı.");
        return (reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.GetDecimal(3));
    }

    private static async Task<(Guid PrescriptionUid, int SaleId, string Status,
        string[] QrCodes, string[] UntrackedQrCodes, string RequestedBy)> ReadOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT prescription_uid, sale_id, operation_status, qr_codes, untracked_qr_codes,
                   COALESCE(requested_by, '')
              FROM public.prescription_sale_operations
             WHERE operation_id=@operation_id AND operation_type=@operation_type
             FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.AddWithValue("operation_type", NpgsqlDbType.Varchar, operationType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Reçete satış işlem kaydı bulunamadı.");
        return (reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
            reader.GetFieldValue<string[]>(3), reader.GetFieldValue<string[]>(4), reader.GetString(5));
    }

    private static async Task<CancellationSaleRow> ReadCancellationSaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT patient_id, COALESCE(user_no, ''), sale_status,
                   total_amount, patient_amount,
                   GREATEST(institution_amount - institution_secondary_amount, 0),
                   institution_secondary_amount, owed_amount,
                   COALESCE(prescription_no, ''), prescription_uid
              FROM public.sales
             WHERE record_id=@sale_id
             FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Reçete satışı bulunamadı.");
        return new CancellationSaleRow(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9));
    }

    private static async Task<int?> ReadPrescriptionRefundIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id
              FROM public."salesRefunds"
             WHERE original_sale_id=@sale_id
               AND refund_source='prescription'
               AND NOT historical_import
            """, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async Task<int> InsertPrescriptionRefundAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        (Guid PrescriptionUid, int SaleId, string Status, string[] QrCodes,
            string[] UntrackedQrCodes, string RequestedBy) operation,
        CancellationSaleRow sale,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public."salesRefunds" (
                patient_id, total_amount, discount_amount, debt_reduction_amount,
                payement_finished, user_no, "timestamp", notes,
                original_sale_id, prescription_uid, refund_source, refund_status,
                patient_amount, institution_amount, institution_secondary_amount, owed_amount)
            VALUES (
                @patient_id, @total_amount, 0, 0,
                @patient_amount=0, NULLIF(@user_id, ''), CURRENT_TIMESTAMP,
                'Medula ITS satış iptali ile oluşturulan reçeteli satış iadesi.',
                @sale_id, @prescription_uid, 'prescription', 'completed',
                @patient_amount, @institution_amount, @secondary_amount, @owed_amount)
            ON CONFLICT (original_sale_id)
                WHERE refund_source='prescription' AND NOT historical_import
            DO UPDATE SET
                refund_status='completed',
                notes=EXCLUDED.notes,
                user_no=EXCLUDED.user_no,
                "timestamp"=EXCLUDED."timestamp"
            RETURNING record_id
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Integer)
        {
            Value = sale.PatientId is null ? DBNull.Value : sale.PatientId.Value
        });
        command.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, sale.TotalAmount);
        command.Parameters.AddWithValue("patient_amount", NpgsqlDbType.Numeric, sale.PatientAmount);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, operation.RequestedBy);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, operation.SaleId);
        command.Parameters.AddWithValue("prescription_uid", NpgsqlDbType.Uuid,
            sale.PrescriptionUid ?? operation.PrescriptionUid);
        command.Parameters.AddWithValue("institution_amount", NpgsqlDbType.Numeric,
            sale.PrimaryInstitutionAmount);
        command.Parameters.AddWithValue("secondary_amount", NpgsqlDbType.Numeric,
            sale.SecondaryInstitutionAmount);
        command.Parameters.AddWithValue("owed_amount", NpgsqlDbType.Numeric, sale.OwedAmount);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<PrescriptionSaleItemRow>> ReadPrescriptionSaleItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        IReadOnlyList<string> requestedQrCodes,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            WITH requested AS (
                SELECT code, ordinal
                  FROM unnest(@qr_codes::varchar[]) WITH ORDINALITY AS input(code, ordinal)
            )
            SELECT item.qr_code, item.barcode, item.gross_price,
                   item.discount_amount, item.exit_price, item.entry_cost,
                   item.inventory_tracked
              FROM requested
              JOIN LATERAL (
                  SELECT sold.*
                    FROM public."salesQrItems" sold
                   WHERE sold.sale_id=@sale_id
                     AND sold.item_source='prescription'
                     AND regexp_replace(regexp_replace(sold.qr_code, E'^\\]d2', ''),
                             E'[()\\x1D\\s]', '', 'g')=requested.code
                   ORDER BY sold.record_id DESC
                   LIMIT 1
              ) item ON TRUE
             ORDER BY requested.ordinal
            """, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue(
            "qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, requestedQrCodes.ToArray());
        var items = new List<PrescriptionSaleItemRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PrescriptionSaleItemRow(
                reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.GetDecimal(4), reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.GetBoolean(6)));
        return items;
    }

    private static async Task InsertPrescriptionRefundItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int refundId,
        int saleId,
        PrescriptionSaleItemRow item,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public."salesRefundItems" (
                refund_id, original_sale_id, qr_code, barcode,
                gross_price, discount_amount, exit_price, entry_cost,
                item_source, inventory_tracked)
            VALUES (
                @refund_id, @sale_id, @qr_code, @barcode,
                @gross_price, @discount, @exit_price, @entry_cost,
                'prescription', @inventory_tracked)
            ON CONFLICT (refund_id, qr_code)
                WHERE item_source='prescription' AND inventory_tracked DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("refund_id", NpgsqlDbType.Integer, refundId);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, item.QrCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, item.Barcode);
        command.Parameters.AddWithValue("gross_price", NpgsqlDbType.Numeric, item.GrossPrice);
        command.Parameters.AddWithValue("discount", NpgsqlDbType.Numeric, item.DiscountAmount);
        command.Parameters.AddWithValue("exit_price", NpgsqlDbType.Numeric, item.ExitPrice);
        command.Parameters.Add(new NpgsqlParameter("entry_cost", NpgsqlDbType.Numeric)
        {
            Value = item.EntryCost is null ? DBNull.Value : item.EntryCost.Value
        });
        command.Parameters.AddWithValue("inventory_tracked", NpgsqlDbType.Boolean, item.InventoryTracked);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPrescriptionReturnMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qrCode,
        string referenceCode,
        string movementAddress,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            SELECT @qr_code, type.record_id, CURRENT_TIMESTAMP,
                   @movement_address, NULLIF(@user_id, ''), @reference_code
              FROM public."qrMovementTypes" type
             WHERE type.code='PRESC_RETURN'
               AND NOT EXISTS (
                   SELECT 1 FROM public."qrMovements" movement
                    WHERE movement.qr_code=@qr_code
                      AND movement.reference_code=@reference_code
                      AND movement.movement_type=type.record_id)
            """, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        command.Parameters.AddWithValue("movement_address", NpgsqlDbType.Varchar, movementAddress);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Karekod reçeteli satış iade hareketi oluşturulamadı: {qrCode}");
    }

    private static async Task InsertPrescriptionRefundPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int refundId,
        decimal patientAmount,
        CancellationToken cancellationToken)
    {
        if (patientAmount <= 0m) return;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public."salesRefundPayements" (
                refund_id, payment_type, amount, payment_role, payment_finished, settled_at)
            VALUES (@refund_id, 7, @amount, 'patient', TRUE, CURRENT_TIMESTAMP)
            ON CONFLICT (refund_id, payment_role) WHERE payment_role='patient'
            DO UPDATE SET amount=EXCLUDED.amount,
                          payment_finished=TRUE,
                          settled_at=COALESCE(public."salesRefundPayements".settled_at, CURRENT_TIMESTAMP)
            """, connection, transaction);
        command.Parameters.AddWithValue("refund_id", NpgsqlDbType.Integer, refundId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, patientAmount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshRefundPaymentStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int refundId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "SELECT public.pharmacy_refresh_refund_payment_status(@refund_id)", connection, transaction);
        command.Parameters.AddWithValue("refund_id", NpgsqlDbType.Integer, refundId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<QrStockRow>> LoadAndLockQrStockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> qrCodes,
        IReadOnlyList<string> untrackedQrCodes,
        Guid prescriptionUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH requested AS (
                SELECT code, ordinal
                  FROM unnest(@qr_codes::varchar[]) WITH ORDINALITY AS input(code, ordinal)
            )
            SELECT requested.code,
                   active.qr_code,
                   COALESCE(active.barcode, public.pharmacy_qr_barcode(requested.code)),
                   active.purchase_cost,
                   COALESCE(price.sale_price, price.public_price, price.public_paid_price, 0)::numeric(12,2),
                   active.reserved_prescription_uid,
                   requested.code=ANY(@untracked_qr_codes)
              FROM requested
              LEFT JOIN LATERAL (
                  SELECT stock.*
                    FROM public."qrActive" stock
                   WHERE stock.qr_code=requested.code
                      OR regexp_replace(
                           regexp_replace(stock.qr_code, E'^\\]d2', ''),
                           E'[()\\x1D]', '', 'g')=requested.code
                   LIMIT 1
              ) active ON TRUE
              LEFT JOIN public.barcode product
                ON product.barcode=COALESCE(active.barcode, public.pharmacy_qr_barcode(requested.code))
              LEFT JOIN public."barcodePrices" price ON price.record_reference_no=product.record_id
             ORDER BY requested.ordinal
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection, transaction);
        command.Parameters.AddWithValue("qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, qrCodes.ToArray());
        command.Parameters.AddWithValue(
            "untracked_qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, untrackedQrCodes.ToArray());
        var rows = new List<QrStockRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var requested = reader.GetString(0);
            var explicitlyUntracked = reader.GetBoolean(6);
            var hasActiveStock = !reader.IsDBNull(1);
            var reservedByAnotherPrescription = !reader.IsDBNull(5) && reader.GetGuid(5) != prescriptionUid;
            if ((!hasActiveStock || reservedByAnotherPrescription) && !explicitlyUntracked)
            {
                if (reservedByAnotherPrescription)
                    throw new InvalidOperationException($"Karekod başka bir reçeteye ayrılmış: {requested}");
                throw new InvalidOperationException($"Karekod aktif stokta bulunamadı: {requested}");
            }
            if (reader.IsDBNull(2))
                throw new InvalidOperationException($"Karekoddan barkod bilgisi okunamadı: {requested}");
            var inventoryTracked = hasActiveStock && !reservedByAnotherPrescription;
            rows.Add(new QrStockRow(
                inventoryTracked ? reader.GetString(1) : requested,
                reader.GetString(2),
                inventoryTracked && !reader.IsDBNull(3) ? reader.GetDecimal(3) : null,
                reader.GetDecimal(4),
                inventoryTracked));
        }
        await reader.DisposeAsync();
        if (rows.Count != qrCodes.Count)
            throw new InvalidOperationException("Reçete karekodlarının tamamı aktif stokta doğrulanamadı.");

        var canonicalCodes = rows.Where(row => row.InventoryTracked).Select(row => row.QrCode).ToArray();
        if (canonicalCodes.Length == 0) return rows;
        await using var lockCommand = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT qr_code
              FROM public."qrActive"
             WHERE qr_code=ANY(@qr_codes)
             FOR UPDATE
            """, connection, transaction);
        lockCommand.Parameters.AddWithValue(
            "qr_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar, canonicalCodes);
        var locked = new HashSet<string>(StringComparer.Ordinal);
        await using (var lockReader = await lockCommand.ExecuteReaderAsync(cancellationToken))
            while (await lockReader.ReadAsync(cancellationToken)) locked.Add(lockReader.GetString(0));
        if (canonicalCodes.Any(code => !locked.Contains(code)))
            throw new InvalidOperationException("Reçete karekodlarından biri satış hazırlanırken stoktan çıktı.");
        return rows;
    }

    private static async Task InsertSaleItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        QrStockRow stock,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public."salesQrItems" (
                sale_id, qr_code, barcode, gross_price, discount_amount, exit_price, entry_cost,
                item_source, inventory_tracked)
            VALUES (@sale_id, @qr_code, @barcode, @price, 0, @price, @entry_cost,
                    'prescription', @inventory_tracked)
            ON CONFLICT (sale_id, qr_code)
                WHERE item_source='prescription' AND inventory_tracked DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, stock.QrCode);
        command.Parameters.AddWithValue("barcode", NpgsqlDbType.Varchar, stock.Barcode);
        command.Parameters.AddWithValue("price", NpgsqlDbType.Numeric, stock.GrossPrice);
        command.Parameters.AddWithValue("inventory_tracked", NpgsqlDbType.Boolean, stock.InventoryTracked);
        command.Parameters.Add(new NpgsqlParameter("entry_cost", NpgsqlDbType.Numeric)
        {
            Value = stock.EntryCost is null ? DBNull.Value : stock.EntryCost.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPrescriptionMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qrCode,
        string referenceCode,
        string movementAddress,
        string userId,
        decimal exitPrice,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public."qrMovements" (
                qr_code, movement_type, movement_date, movement_address, user_id, reference_code)
            SELECT @qr_code, type.record_id, CURRENT_TIMESTAMP,
                   @movement_address, NULLIF(@user_id, ''), @reference_code
              FROM public."qrMovementTypes" type
             WHERE type.code='PRESC_OUT'
               AND NOT EXISTS (
                   SELECT 1 FROM public."qrMovements" movement
                    WHERE movement.qr_code=@qr_code
                      AND movement.reference_code=@reference_code
                      AND movement.movement_type=type.record_id)
            """, connection, transaction);
        command.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        command.Parameters.AddWithValue("movement_address", NpgsqlDbType.Varchar, movementAddress);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("reference_code", NpgsqlDbType.Varchar, referenceCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Karekod satış hareketi oluşturulamadı: {qrCode}");

        await using var priceUpdate = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public."qrInactive"
               SET exit_price=@exit_price
             WHERE qr_code=@qr_code
            """, connection, transaction);
        priceUpdate.Parameters.AddWithValue("exit_price", NpgsqlDbType.Numeric, exitPrice);
        priceUpdate.Parameters.AddWithValue("qr_code", NpgsqlDbType.Varchar, qrCode);
        if (await priceUpdate.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Karekod çıkış fiyatı kaydedilemedi: {qrCode}");
    }

    private static async Task<int?> ReadPatientIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tcIdentityNo,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tcIdentityNo)) return null;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT patient_record_id
              FROM public.patient_identity
             WHERE tc_identity_no=@tc
            """, connection, transaction);
        command.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, tcIdentityNo.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static MedulaPrescriptionPatientPaymentSummary ValidatePatientPayments(
        decimal patientAmount,
        int? patientId,
        IReadOnlyList<RetailSalePaymentInput> patientPayments)
    {
        var unsupportedPayment = patientPayments.FirstOrDefault(payment =>
            payment.Kind is not (RetailPaymentKind.Cash or RetailPaymentKind.Credit
                or RetailPaymentKind.BankTransfer or RetailPaymentKind.Debt
                or RetailPaymentKind.Discount));
        if (unsupportedPayment is not null)
            throw new InvalidOperationException("Reçeteli satışta desteklenmeyen bir hasta ödeme türü seçildi.");
        if (patientPayments.GroupBy(payment => payment.Kind).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Aynı hasta ödeme türü birden fazla kez kullanılamaz.");
        return MedulaPrescriptionPatientPaymentRules.Validate(
            patientAmount, patientPayments, patientId);
    }

    private static async Task UpdatePatientPaymentFlagsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        decimal debtAmount,
        bool withDiscount,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.sales
               SET debted=@debted,
                   with_iskonto=@with_iskonto
             WHERE record_id=@sale_id
            """, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("debted", NpgsqlDbType.Boolean, debtAmount > 0m);
        command.Parameters.AddWithValue("with_iskonto", NpgsqlDbType.Boolean, withDiscount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplacePatientPaymentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        IReadOnlyList<RetailSalePaymentInput> payments,
        bool completed,
        CancellationToken cancellationToken)
    {
        await using (var delete = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            DELETE FROM public."salesPayements"
             WHERE sale_id=@sale_id
               AND (payment_role='patient' OR payment_role LIKE 'patient_%')
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var payment in payments.Where(payment => payment.Amount > 0m))
        {
            await using var insert = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
                INSERT INTO public."salesPayements" (
                    sale_id, payment_type, amount, payment_role, owed_amount,
                    payment_finished, settled_at)
                VALUES (
                    @sale_id, @payment_type, @amount, @payment_role, 0,
                    @completed, CASE WHEN @completed THEN CURRENT_TIMESTAMP ELSE NULL END)
                """, connection, transaction);
            insert.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
            insert.Parameters.AddWithValue("payment_type", NpgsqlDbType.Smallint, (short)payment.Kind);
            insert.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, payment.Amount);
            insert.Parameters.AddWithValue("completed", NpgsqlDbType.Boolean, completed);
            insert.Parameters.AddWithValue("payment_role", NpgsqlDbType.Varchar, payment.Kind switch
            {
                RetailPaymentKind.Cash => "patient_cash",
                RetailPaymentKind.Credit => "patient_credit",
                RetailPaymentKind.BankTransfer => "patient_bank_transfer",
                RetailPaymentKind.Debt => "patient_debt",
                RetailPaymentKind.Discount => "patient_discount",
                _ => throw new InvalidOperationException("Desteklenmeyen hasta ödeme türü.")
            });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task IncreasePatientDebtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int patientId,
        decimal debtAmount,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.patient
               SET debt_amount=COALESCE(debt_amount, 0) + @amount
             WHERE record_id=@patient_id
            """, connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patientId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, debtAmount);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Veresiye ödeme için reçete hastası bulunamadı.");
    }

    private static async Task CollectExistingPatientDebtsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ownerTcIdentityNumber,
        string recipientTcIdentityNumber,
        MedulaPrescriptionDebtCollection collection,
        CancellationToken cancellationToken)
    {
        if (collection.TotalAmount <= 0m) return;
        ownerTcIdentityNumber = ownerTcIdentityNumber.Trim();
        recipientTcIdentityNumber = recipientTcIdentityNumber.Trim();
        var owner = await ReadDebtPatientAsync(
            connection, transaction, ownerTcIdentityNumber, cancellationToken)
            ?? throw new InvalidOperationException("Borcu tahsil edilecek reçete sahibi bulunamadı.");
        var samePerson = ownerTcIdentityNumber == recipientTcIdentityNumber;
        var recipient = samePerson || collection.RecipientAmount <= 0m
            ? null
            : await ReadDebtPatientAsync(
                connection, transaction, recipientTcIdentityNumber, cancellationToken);
        var familyMembers = collection.FamilyAmount <= 0m || owner.FamilyGroupId is null
            ? []
            : await ReadFamilyDebtPatientsAsync(
                connection,
                transaction,
                owner.FamilyGroupId.Value,
                [owner.RecordId, recipient?.RecordId ?? -1],
                cancellationToken);

        static decimal Money(decimal value) =>
            decimal.Round(Math.Max(0m, value), 2, MidpointRounding.AwayFromZero);
        if (Money(owner.Debt) != Money(collection.OwnerAmount))
            throw new InvalidOperationException("Reçete sahibinin borcu ödeme sırasında değişti; ekranı yenileyin.");
        if (Money(collection.RecipientAmount) > 0m
            && (recipient is null || Money(recipient.Debt) != Money(collection.RecipientAmount)))
            throw new InvalidOperationException("Teslim alan kişinin borcu ödeme sırasında değişti; ekranı yenileyin.");
        if (Money(familyMembers.Sum(member => member.Debt)) != Money(collection.FamilyAmount))
            throw new InvalidOperationException("Aile grubu borcu ödeme sırasında değişti; ekranı yenileyin.");

        if (collection.OwnerAmount > 0m)
            await ReducePatientDebtAsync(connection, transaction, owner, cancellationToken);
        if (collection.RecipientAmount > 0m && recipient is not null)
            await ReducePatientDebtAsync(connection, transaction, recipient, cancellationToken);
        foreach (var member in familyMembers.Where(member => member.Debt > 0m))
            await ReducePatientDebtAsync(connection, transaction, member, cancellationToken);
    }

    private static async Task<DebtPatient?> ReadDebtPatientAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tcIdentityNumber,
        CancellationToken cancellationToken)
    {
        if (tcIdentityNumber.Length != 11 || tcIdentityNumber.Any(character => !char.IsDigit(character)))
            return null;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id, family_group_id, COALESCE(debt_amount, 0)
              FROM public.patient
             WHERE tc_identity_no=@tc
             ORDER BY record_id
             LIMIT 1
             FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("tc", NpgsqlDbType.Varchar, tcIdentityNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DebtPatient(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDecimal(2))
            : null;
    }

    private static async Task<IReadOnlyList<DebtPatient>> ReadFamilyDebtPatientsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int familyGroupId,
        IReadOnlyList<int> excludedPatientIds,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            SELECT record_id, family_group_id, COALESCE(debt_amount, 0)
              FROM public.patient
             WHERE family_group_id=@family_group_id
               AND NOT (record_id=ANY(@excluded_ids))
             ORDER BY record_id
             FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("family_group_id", NpgsqlDbType.Integer, familyGroupId);
        command.Parameters.AddWithValue(
            "excluded_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, excludedPatientIds.ToArray());
        var result = new List<DebtPatient>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new DebtPatient(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDecimal(2)));
        return result;
    }

    private static async Task ReducePatientDebtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DebtPatient patient,
        CancellationToken cancellationToken)
    {
        if (patient.Debt <= 0m) return;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            UPDATE public.patient
               SET debt_amount=COALESCE(debt_amount, 0) - @amount
             WHERE record_id=@patient_id
               AND COALESCE(debt_amount, 0)=@amount
            """, connection, transaction);
        command.Parameters.AddWithValue("patient_id", NpgsqlDbType.Integer, patient.RecordId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, patient.Debt);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Hasta borcu tahsilat sırasında değişti; ekranı yenileyin.");
    }

    private static async Task ReplacePendingPaymentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        decimal patientAmount,
        decimal primaryInstitutionAmount,
        decimal secondaryInstitutionAmount,
        decimal owedAmount,
        CancellationToken cancellationToken)
    {
        await using (var delete = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            DELETE FROM public."salesPayements"
             WHERE sale_id=@sale_id
               AND (payment_role='patient'
                    OR payment_role LIKE 'patient_%'
                    OR payment_role IN ('institution_primary', 'institution_secondary'))
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertPendingPaymentAsync(connection, transaction, saleId, 7, "patient",
            patientAmount, 0m, cancellationToken);
        await InsertPendingPaymentAsync(connection, transaction, saleId, 5, "institution_primary",
            primaryInstitutionAmount, owedAmount, cancellationToken);
        await InsertPendingPaymentAsync(connection, transaction, saleId, 8, "institution_secondary",
            secondaryInstitutionAmount, 0m, cancellationToken);
    }

    private static async Task InsertPendingPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        short paymentType,
        string paymentRole,
        decimal amount,
        decimal owedAmount,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m) return;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("""
            INSERT INTO public."salesPayements" (
                sale_id, payment_type, amount, payment_role, owed_amount, payment_finished)
            VALUES (@sale_id, @payment_type, @amount, @payment_role, @owed_amount, FALSE)
            """, connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        command.Parameters.AddWithValue("payment_type", NpgsqlDbType.Smallint, paymentType);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
        command.Parameters.AddWithValue("payment_role", NpgsqlDbType.Varchar, paymentRole);
        command.Parameters.AddWithValue("owed_amount", NpgsqlDbType.Numeric, owedAmount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshPaymentStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int saleId,
        CancellationToken cancellationToken)
    {
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(
            "SELECT public.pharmacy_refresh_sale_payment_status(@sale_id)", connection, transaction);
        command.Parameters.AddWithValue("sale_id", NpgsqlDbType.Integer, saleId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSaleParameters(
        NpgsqlCommand command,
        MedulaPrescriptionPersistenceRecord record,
        Guid prescriptionUid,
        int? patientId,
        decimal patientAmount,
        decimal primaryInstitutionAmount,
        decimal secondaryInstitutionAmount,
        decimal owedAmount,
        decimal totalAmount)
    {
        command.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Integer)
        {
            Value = patientId is null ? DBNull.Value : patientId.Value
        });
        command.Parameters.AddWithValue("institution_amount", NpgsqlDbType.Numeric,
            primaryInstitutionAmount + secondaryInstitutionAmount);
        command.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, totalAmount);
        command.Parameters.AddWithValue("prescription_no", NpgsqlDbType.Varchar,
            string.IsNullOrWhiteSpace(record.MedulaPrescriptionNo)
                ? record.EPrescriptionNo.Trim()
                : record.MedulaPrescriptionNo.Trim());
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, record.SavedByUserId.Trim());
        command.Parameters.AddWithValue("prescription_uid", NpgsqlDbType.Uuid, prescriptionUid);
        command.Parameters.AddWithValue("patient_amount", NpgsqlDbType.Numeric, patientAmount);
        command.Parameters.AddWithValue("secondary_amount", NpgsqlDbType.Numeric, secondaryInstitutionAmount);
        command.Parameters.AddWithValue("owed_amount", NpgsqlDbType.Numeric, owedAmount);
    }

    private sealed record QrStockRow(
        string QrCode,
        string Barcode,
        decimal? EntryCost,
        decimal GrossPrice,
        bool InventoryTracked);

    private sealed record DebtPatient(
        int RecordId,
        int? FamilyGroupId,
        decimal Debt);

    private sealed record CancellationSaleRow(
        int? PatientId,
        string UserId,
        string Status,
        decimal TotalAmount,
        decimal PatientAmount,
        decimal PrimaryInstitutionAmount,
        decimal SecondaryInstitutionAmount,
        decimal OwedAmount,
        string PrescriptionNumber,
        Guid? PrescriptionUid);

    private sealed record PrescriptionSaleItemRow(
        string QrCode,
        string Barcode,
        decimal GrossPrice,
        decimal DiscountAmount,
        decimal ExitPrice,
        decimal? EntryCost,
        bool InventoryTracked);
}
