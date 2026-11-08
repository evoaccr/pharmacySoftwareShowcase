using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceIncrementalSyncState(
    DateTimeOffset? LastSuccessfulCutoff,
    Guid? ActiveRunId,
    DateTimeOffset? ActiveRunCutoff,
    string Phase,
    int Total,
    int Completed,
    int Imported,
    int Failed,
    string CurrentInvoice,
    string LastError,
    DateTimeOffset UpdatedAt);

public sealed record InvoiceIncrementalSyncProgress(
    InvoiceIncrementalSyncState State,
    string Message,
    string CurrentType,
    string CurrentInvoice,
    TimeSpan Elapsed);

internal sealed record InvoiceIncrementalSyncItem(
    Guid RunId,
    string TypeKey,
    string DocumentUuid,
    string DocumentNumber,
    DateOnly? IssueDate,
    DateTimeOffset? ReceivedAt,
    string Status);

public sealed class InvoiceIncrementalSyncService
{
    public const string SourceKey = "eczaci-kart-xml";
    private static readonly EczaciKartInvoiceType[] Types = EczaciKartInvoiceType.All
        .Where(type => type.Key is "incoming-invoice" or "outgoing-invoice" or "archive-invoice")
        .ToArray();
    private readonly EczaciKartConnectionService portal = EczaciKartConnectionService.Instance;
    private readonly CompactInvoiceRepository repository = new();

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (!DatabaseConnectionSettingsStore.Current.AllowSchemaChanges) return;
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(SchemaSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<InvoiceIncrementalSyncState> LoadStateAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureStateRowAsync(connection, cancellationToken);
        return await ReadStateAsync(connection, cancellationToken);
    }

    public async Task RunAsync(
        IProgress<InvoiceIncrementalSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await repository.EnsureCompatibleSchemaAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            IReadOnlyList<InvoiceIncrementalSyncItem> items;
            if (state.ActiveRunId is { } activeRun)
            {
                items = await LoadActionableItemsAsync(activeRun, cancellationToken);
                await SetPhaseAsync("Running", string.Empty, cancellationToken);
                state = await LoadStateAsync(cancellationToken);
                Report(progress, state, "Kaydedilmiş kontrol noktasından devam ediliyor…", string.Empty, string.Empty, stopwatch);
            }
            else
            {
                await SetPhaseAsync("Discovering", string.Empty, cancellationToken);
                state = await LoadStateAsync(cancellationToken);
                Report(progress, state, "Son başarılı aktarımdan sonraki faturalar aranıyor…", string.Empty, string.Empty, stopwatch);
                items = await DiscoverAndCreateRunAsync(state, progress, stopwatch, cancellationToken);
                state = await LoadStateAsync(cancellationToken);
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MarkItemPendingAsync(item, cancellationToken);
                state = await LoadStateAsync(cancellationToken);
                var type = EczaciKartInvoiceType.FromKey(item.TypeKey);
                Report(progress, state, $"{item.DocumentNumber} XML olarak indiriliyor…",
                    type.DisplayName, item.DocumentNumber, stopwatch);
                try
                {
                    var date = item.IssueDate
                        ?? (item.ReceivedAt is { } received
                            ? DateOnly.FromDateTime(received.LocalDateTime)
                            : DateOnly.FromDateTime(DateTime.Today));
                    var xmlBytes = await RetryAsync(() => portal.DownloadSingleXmlAsync(
                        item.TypeKey, item.DocumentUuid, date.Year, date.Month - 1, cancellationToken),
                        cancellationToken);
                    var xml = Encoding.UTF8.GetString(xmlBytes);
                    var document = UblInvoiceParser.Parse(xml, item.TypeKey);
                    await repository.SaveAsync(document, cancellationToken);
                    await MarkItemImportedAsync(item, cancellationToken);
                    state = await LoadStateAsync(cancellationToken);
                    Report(progress, state, $"{document.InvoiceNumber} veritabanına eklendi.",
                        type.DisplayName, document.InvoiceNumber, stopwatch);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await MarkItemFailedAsync(item, exception.GetBaseException().Message, cancellationToken);
                    state = await LoadStateAsync(cancellationToken);
                    Report(progress, state, $"{item.DocumentNumber} aktarılamadı; yeniden denemek üzere kaydedildi.",
                        type.DisplayName, item.DocumentNumber, stopwatch);
                }
            }

            state = await CompleteRunIfSuccessfulAsync(cancellationToken);
            var message = state.Phase == "Completed"
                ? "Yeni faturalar aktarıldı ve son başarılı aktarım zamanı ilerletildi."
                : $"{state.Failed:N0} fatura yeniden denenmek üzere takipte tutuluyor.";
            Report(progress, state, message, string.Empty, string.Empty, stopwatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SetPhaseAsync("Paused", string.Empty, CancellationToken.None);
            var state = await LoadStateAsync(CancellationToken.None);
            Report(progress, state, "Aktarım duraklatıldı; fatura kontrol noktaları korundu.", string.Empty, string.Empty, stopwatch);
        }
        catch (Exception exception)
        {
            await SetPhaseAsync("Faulted", exception.GetBaseException().Message, CancellationToken.None);
            throw;
        }
    }

    public async Task RecordFullImportCompletionAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            INSERT INTO public.invoice_incremental_sync_state (
                source_key, last_successful_cutoff, phase, updated_at)
            VALUES (@source_key, @cutoff, 'Completed', now())
            ON CONFLICT (source_key) DO UPDATE SET
                last_successful_cutoff = GREATEST(
                    public.invoice_incremental_sync_state.last_successful_cutoff, EXCLUDED.last_successful_cutoff),
                phase = 'Completed', last_error = '', updated_at = now()
            WHERE public.invoice_incremental_sync_state.active_run_id IS NULL;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("source_key", SourceKey);
        command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<InvoiceIncrementalSyncItem>> DiscoverAndCreateRunAsync(
        InvoiceIncrementalSyncState state,
        IProgress<InvoiceIncrementalSyncProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.Now;
        var start = state.LastSuccessfulCutoff is { } successfulCutoff
            ? DateOnly.FromDateTime(successfulCutoff.LocalDateTime).AddDays(-2)
            : await ReadInitialStartDateAsync(cancellationToken);
        var candidates = new List<EczaciKartInvoiceItem>();
        foreach (var type in Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, state, $"{type.DisplayName} listesi taranıyor…", type.DisplayName, string.Empty, stopwatch);
            var listed = await portal.LoadInvoicesAsync(start, cutoff, type.Key, cancellationToken);
            candidates.AddRange(listed.Where(item => !string.IsNullOrWhiteSpace(item.Ettn)));
        }

        var missing = await FilterMissingAsync(candidates, cancellationToken);
        var runId = Guid.NewGuid();
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string stateSql = """
            UPDATE public.invoice_incremental_sync_state
            SET active_run_id = @run_id, active_run_cutoff = @cutoff,
                phase = 'Running', total = @total, completed = 0, imported = 0, failed = 0,
                current_invoice = '', last_error = '', updated_at = now()
            WHERE source_key = @source_key;
            """;
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(stateSql, connection, transaction))
        {
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
            command.Parameters.AddWithValue("total", missing.Count);
            command.Parameters.AddWithValue("source_key", SourceKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string itemSql = """
            INSERT INTO public.invoice_incremental_sync_items (
                run_id, type_key, document_uuid, document_number, issue_date, received_at)
            VALUES (@run_id, @type_key, @document_uuid, @document_number, @issue_date, @received_at)
            ON CONFLICT DO NOTHING;
            """;
        foreach (var item in missing)
        {
            await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(itemSql, connection, transaction);
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("type_key", item.TypeKey);
            command.Parameters.AddWithValue("document_uuid", item.Ettn.Trim());
            command.Parameters.AddWithValue("document_number", item.DocumentNumber ?? string.Empty);
            AddNullable(command, "issue_date", NpgsqlDbType.Date, ParseDate(item.IssueDate));
            AddNullable(command, "received_at", NpgsqlDbType.TimestampTz,
                item.ReceivedAt?.ToUniversalTime());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await LoadActionableItemsAsync(runId, cancellationToken);
    }

    private static async Task<DateOnly> ReadInitialStartDateAsync(CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create("SELECT max(issue_date) FROM public.invoices;", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateOnly date
            ? date.AddDays(-30)
            : DateOnly.FromDateTime(DateTime.Today).AddDays(-30);
    }

    private static async Task<IReadOnlyList<EczaciKartInvoiceItem>> FilterMissingAsync(
        IReadOnlyList<EczaciKartInvoiceItem> candidates,
        CancellationToken cancellationToken)
    {
        var unique = candidates.GroupBy(item => (item.TypeKey, Uuid: item.Ettn.Trim()))
            .Select(group => group.OrderByDescending(item => item.ReceivedAt).First()).ToArray();
        if (unique.Length == 0) return [];
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT invoice.invoice_uuid,
                   NOT EXISTS (
                       SELECT 1 FROM public.invoice_lines line
                       WHERE line.invoice_uuid = invoice.invoice_uuid) OR
                   EXISTS (
                       SELECT 1
                       FROM public.invoice_lines line
                       WHERE line.invoice_uuid = invoice.invoice_uuid
                         AND (
                             (line.stock_tracking_type = 'NONE'
                              AND line.raw_data::text ~* '[(](GTIN|01)[)]')
                             OR (line.barcode !~ '^[0-9]{8,14}$'
                                 AND line.raw_data::text ~ '(^|[^0-9])[0-9]{8,14}([^0-9]|$)')
                             OR (line.stock_tracking_type = 'BARCODE' AND EXISTS (
                                 SELECT 1 FROM public.barcode product
                                 WHERE product.barcode IN (
                                     NULLIF(btrim(line.barcode), ''),
                                     NULLIF(btrim(line.item_code), ''),
                                     NULLIF(trim(leading '0' from btrim(line.barcode)), ''),
                                     NULLIF(trim(leading '0' from btrim(line.item_code)), ''))
                                   AND COALESCE(NULLIF(btrim(product.product_type_name), ''),
                                                NULLIF(btrim(product.atc_code), ''),
                                                NULLIF(btrim(product.sgk_code), '')) IS NOT NULL))
                             OR line.tracking_warning ILIKE '%ayrıştırılamadı%'
                             OR line.tracking_warning ILIKE '%aynı satırda tekrarlandı%')) AS needs_repair
            FROM public.invoices invoice
            WHERE invoice.invoice_uuid = ANY(@uuids);
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("uuids", NpgsqlDbType.Array | NpgsqlDbType.Text,
            unique.Select(item => item.Ettn.Trim()).ToArray());
        var existing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) existing[reader.GetString(0)] = reader.GetBoolean(1);
        return unique.Where(item => !existing.TryGetValue(item.Ettn.Trim(), out var needsRepair) || needsRepair)
            .OrderBy(item => Array.FindIndex(Types, type => type.Key == item.TypeKey))
            .ThenByDescending(item => item.ReceivedAt)
            .ToArray();
    }

    private static async Task EnsureStateRowAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.invoice_incremental_sync_state (source_key)
            VALUES (@source_key) ON CONFLICT DO NOTHING;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("source_key", SourceKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<InvoiceIncrementalSyncState> ReadStateAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT last_successful_cutoff, active_run_id, active_run_cutoff, phase,
                   total, completed, imported, failed, current_invoice, last_error, updated_at
            FROM public.invoice_incremental_sync_state WHERE source_key = @source_key;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("source_key", SourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new InvoiceIncrementalSyncState(
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetString(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10));
    }

    private static async Task<IReadOnlyList<InvoiceIncrementalSyncItem>> LoadActionableItemsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT run_id, type_key, document_uuid, document_number, issue_date, received_at, status
            FROM public.invoice_incremental_sync_items
            WHERE run_id = @run_id AND status <> 'Imported'
            ORDER BY received_at DESC NULLS LAST, issue_date DESC NULLS LAST, document_uuid;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("run_id", runId);
        var result = new List<InvoiceIncrementalSyncItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new InvoiceIncrementalSyncItem(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6)));
        return result;
    }

    private static Task MarkItemPendingAsync(InvoiceIncrementalSyncItem item, CancellationToken cancellationToken) =>
        UpdateItemAsync(item, "Pending", string.Empty, cancellationToken);
    private static Task MarkItemImportedAsync(InvoiceIncrementalSyncItem item, CancellationToken cancellationToken) =>
        UpdateItemAsync(item, "Imported", string.Empty, cancellationToken);
    private static Task MarkItemFailedAsync(InvoiceIncrementalSyncItem item, string error, CancellationToken cancellationToken) =>
        UpdateItemAsync(item, "Failed", error, cancellationToken);

    private static async Task UpdateItemAsync(
        InvoiceIncrementalSyncItem item,
        string status,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string itemSql = """
            UPDATE public.invoice_incremental_sync_items
            SET status = @status, last_error = @error, updated_at = now()
            WHERE run_id = @run_id AND type_key = @type_key AND document_uuid = @document_uuid;
            """;
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(itemSql, connection, transaction))
        {
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("error", error);
            command.Parameters.AddWithValue("run_id", item.RunId);
            command.Parameters.AddWithValue("type_key", item.TypeKey);
            command.Parameters.AddWithValue("document_uuid", item.DocumentUuid);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string stateSql = """
            UPDATE public.invoice_incremental_sync_state state
            SET completed = summary.completed, imported = summary.imported, failed = summary.failed,
                current_invoice = @current_invoice,
                last_error = CASE WHEN @error = '' THEN state.last_error ELSE @error END,
                updated_at = now()
            FROM (
                SELECT count(*) FILTER (WHERE status IN ('Imported', 'Failed'))::integer completed,
                       count(*) FILTER (WHERE status = 'Imported')::integer imported,
                       count(*) FILTER (WHERE status = 'Failed')::integer failed
                FROM public.invoice_incremental_sync_items WHERE run_id = @run_id
            ) summary
            WHERE state.source_key = @source_key;
            """;
        await using (var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(stateSql, connection, transaction))
        {
            command.Parameters.AddWithValue("current_invoice", item.DocumentNumber);
            command.Parameters.AddWithValue("error", error);
            command.Parameters.AddWithValue("run_id", item.RunId);
            command.Parameters.AddWithValue("source_key", SourceKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<InvoiceIncrementalSyncState> CompleteRunIfSuccessfulAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE public.invoice_incremental_sync_state
            SET last_successful_cutoff = CASE WHEN failed = 0 THEN active_run_cutoff ELSE last_successful_cutoff END,
                active_run_id = CASE WHEN failed = 0 THEN NULL ELSE active_run_id END,
                active_run_cutoff = CASE WHEN failed = 0 THEN NULL ELSE active_run_cutoff END,
                phase = CASE WHEN failed = 0 THEN 'Completed' ELSE 'Faulted' END,
                current_invoice = '', last_error = CASE WHEN failed = 0 THEN '' ELSE last_error END,
                updated_at = now()
            WHERE source_key = @source_key;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("source_key", SourceKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureStateRowAsync(connection, cancellationToken);
        return await ReadStateAsync(connection, cancellationToken);
    }

    private static async Task SetPhaseAsync(string phase, string error, CancellationToken cancellationToken)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureStateRowAsync(connection, cancellationToken);
        const string sql = """
            UPDATE public.invoice_incremental_sync_state
            SET phase = @phase, last_error = @error, updated_at = now()
            WHERE source_key = @source_key;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        command.Parameters.AddWithValue("phase", phase);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("source_key", SourceKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Report(
        IProgress<InvoiceIncrementalSyncProgress>? progress,
        InvoiceIncrementalSyncState state,
        string message,
        string currentType,
        string currentInvoice,
        Stopwatch stopwatch) =>
        progress?.Report(new InvoiceIncrementalSyncProgress(
            state, message, currentType, currentInvoice, stopwatch.Elapsed));

    private static DateOnly? ParseDate(string value) => DateOnly.TryParseExact(
        value, "dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var date)
        ? date
        : null;

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var delay in new[] { 0, 1, 3 })
        {
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
            try { return await action(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { last = exception; }
        }
        throw new InvalidOperationException("XML indirme yeniden denemelere rağmen tamamlanamadı.", last);
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS public.invoice_incremental_sync_state (
            source_key text PRIMARY KEY,
            last_successful_cutoff timestamptz,
            active_run_id uuid,
            active_run_cutoff timestamptz,
            phase text NOT NULL DEFAULT 'Idle',
            total integer NOT NULL DEFAULT 0,
            completed integer NOT NULL DEFAULT 0,
            imported integer NOT NULL DEFAULT 0,
            failed integer NOT NULL DEFAULT 0,
            current_invoice text NOT NULL DEFAULT '',
            last_error text NOT NULL DEFAULT '',
            updated_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT invoice_incremental_sync_phase_check CHECK (
                phase IN ('Idle', 'Discovering', 'Running', 'Paused', 'Faulted', 'Completed')));
        CREATE TABLE IF NOT EXISTS public.invoice_incremental_sync_items (
            run_id uuid NOT NULL,
            type_key text NOT NULL,
            document_uuid text NOT NULL,
            document_number text NOT NULL DEFAULT '',
            issue_date date,
            received_at timestamptz,
            status text NOT NULL DEFAULT 'Pending',
            last_error text NOT NULL DEFAULT '',
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (run_id, type_key, document_uuid),
            CONSTRAINT invoice_incremental_sync_item_status_check CHECK (
                status IN ('Pending', 'Imported', 'Failed')));
        CREATE INDEX IF NOT EXISTS invoice_incremental_sync_items_pending_idx
            ON public.invoice_incremental_sync_items (run_id, status, received_at, issue_date);
        """;
}
