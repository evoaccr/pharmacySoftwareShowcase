using System.Diagnostics;
using System.Globalization;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceTypeOperationProgress(
    string TypeKey,
    string DisplayName,
    int PortalTotal,
    int AccessibleTotal,
    int Completed,
    int Imported,
    int Failed,
    bool IsActive);

public sealed record InvoiceOperationProgress(
    int Completed,
    int Total,
    string Message,
    int Inserted,
    int Failed,
    IReadOnlyList<string> FailureMessages,
    string Phase = "Preparing",
    string CurrentTypeKey = "",
    string CurrentInvoiceNumber = "",
    string CurrentInvoiceDate = "",
    string CurrentInvoiceAddress = "",
    TimeSpan Elapsed = default,
    double AverageSecondsPerInvoice = 0,
    IReadOnlyList<InvoiceTypeOperationProgress>? TypeStates = null);

public sealed record InvoiceOperationResult(
    int Total,
    int PortalTotal,
    int Inserted,
    int Failed,
    IReadOnlyList<string> FailureMessages);

public sealed class InvoiceDatabaseSyncService(
    EczaciKartConnectionService portal,
    InvoiceArchiveRepository repository)
{
    public async Task<InvoiceOperationResult> SyncNewAsync(
        IProgress<InvoiceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddMonths(-3);
        var all = await portal.LoadInvoicesAsync(start, today, null, cancellationToken);
        var existing = await repository.ReadExistingLogicalKeysAsync(all, cancellationToken);
        var candidates = all.Where(item => item.IsRead == false || (item.IsRead is null && !existing.Contains(
                InvoiceArchiveRepository.BuildLogicalKey(item.TypeKey, item.Ettn, item.DocumentNumber))))
            .OrderBy(item => TypeOrder(item.TypeKey))
            .ThenByDescending(item => item.ReceivedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        return await ImportItemsAsync(candidates, start, today, false, null, progress, cancellationToken);
    }

    internal async Task<InvoiceOperationResult> ImportItemsAsync(
        IReadOnlyList<EczaciKartInvoiceItem> items,
        DateOnly rangeStart,
        DateOnly rangeEnd,
        bool forceNewVersion,
        IReadOnlyDictionary<string, int>? portalTotals,
        IProgress<InvoiceOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        var inserted = 0;
        var failures = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var perType = EczaciKartInvoiceType.All.ToDictionary(
            type => type.Key,
            type => new MutableTypeState
            {
                PortalTotal = portalTotals?.GetValueOrDefault(type.Key) ?? items.Count(item => item.TypeKey == type.Key),
                AccessibleTotal = items.Count(item => item.TypeKey == type.Key)
            },
            StringComparer.Ordinal);
        var portalTotal = portalTotals?.Values.Sum() ?? items.Count;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = perType[item.TypeKey];
            Report(item, state, "Importing", $"{item.DocumentNumber} ayrıntıları alınıyor…",
                item.CounterpartyName, completed, inserted, failures, stopwatch, portalTotal, perType, progress);

            Exception? lastError = null;
            string resolvedAddress = item.CounterpartyName;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var itemDate = item.ReceivedAt is { } received
                        ? DateOnly.FromDateTime(received.LocalDateTime)
                        : ParseIssueDate(item.IssueDate) ?? rangeEnd;
                    var document = await portal.LoadInvoiceDocumentAsync(item, itemDate, itemDate, cancellationToken);
                    var counterparty = EczaciKartInvoiceType.FromKey(item.TypeKey).IsIncoming
                        ? document.InvoiceFrom
                        : document.InvoiceTo;
                    resolvedAddress = string.IsNullOrWhiteSpace(counterparty.Address)
                        ? counterparty.Name
                        : counterparty.Address;
                    await repository.SaveAsync(document, rangeStart, rangeEnd, forceNewVersion, cancellationToken);
                    inserted++;
                    state.Imported++;
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    lastError = exception;
                    if (attempt == 1) await Task.Delay(500, cancellationToken);
                }
            }

            if (lastError is not null)
            {
                state.Failed++;
                failures.Add($"{item.TypeDisplay} / {item.DocumentNumber}: {lastError.Message}");
            }
            completed++;
            state.Completed++;
            Report(item, state, "Importing", $"{completed}/{items.Count} erişilebilir belge işlendi",
                resolvedAddress, completed, inserted, failures, stopwatch, portalTotal, perType, progress);
        }

        stopwatch.Stop();
        return new InvoiceOperationResult(items.Count, portalTotal, inserted, failures.Count, failures);
    }

    private static void Report(
        EczaciKartInvoiceItem item,
        MutableTypeState active,
        string phase,
        string message,
        string address,
        int completed,
        int inserted,
        IReadOnlyList<string> failures,
        Stopwatch stopwatch,
        int portalTotal,
        IReadOnlyDictionary<string, MutableTypeState> states,
        IProgress<InvoiceOperationProgress>? progress)
    {
        var typeStates = EczaciKartInvoiceType.All.Select(type =>
        {
            var state = states[type.Key];
            return new InvoiceTypeOperationProgress(
                type.Key, type.DisplayName, state.PortalTotal, state.AccessibleTotal,
                state.Completed, state.Imported, state.Failed, ReferenceEquals(state, active));
        }).ToArray();
        progress?.Report(new InvoiceOperationProgress(
            completed, Math.Max(portalTotal, 1), message, inserted, failures.Count, failures,
            phase, item.TypeKey, item.DocumentNumber,
            item.ReceivedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("tr-TR"))
                ?? item.IssueDate,
            address, stopwatch.Elapsed,
            completed == 0 ? 0 : stopwatch.Elapsed.TotalSeconds / completed,
            typeStates));
    }

    internal static DateOnly? ParseIssueDate(string value) => DateOnly.TryParseExact(
        value, "dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var date) ? date : null;

    private static int TypeOrder(string key)
    {
        for (var index = 0; index < EczaciKartInvoiceType.All.Count; index++)
            if (EczaciKartInvoiceType.All[index].Key == key) return index;
        return int.MaxValue;
    }

    private sealed class MutableTypeState
    {
        public int PortalTotal { get; init; }
        public int AccessibleTotal { get; init; }
        public int Completed { get; set; }
        public int Imported { get; set; }
        public int Failed { get; set; }
    }
}
