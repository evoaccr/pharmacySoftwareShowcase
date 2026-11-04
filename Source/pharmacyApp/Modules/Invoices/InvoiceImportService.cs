namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceImportProgress(
    int Completed,
    int Total,
    string Message,
    int Inserted,
    int Versioned,
    int Unchanged,
    int Deleted,
    int Failed);

public sealed record InvoiceImportResult(
    int Total,
    int Inserted,
    int Versioned,
    int Unchanged,
    int Deleted,
    int Failed,
    int MedicationInvoices);

public sealed class InvoiceImportService(
    EczaciKartConnectionService portal,
    InvoiceArchiveRepository repository)
{
    private static readonly IReadOnlyList<EczaciKartInvoiceType> InvoiceTypes =
        EczaciKartInvoiceType.All.Where(type => !type.IsDispatch).ToArray();

    public async Task<InvoiceImportResult> ImportAsync(
        DateOnly startDate,
        DateOnly endDate,
        IProgress<InvoiceImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (endDate < startDate)
            throw new ArgumentException("Bitiş tarihi başlangıç tarihinden önce olamaz.", nameof(endDate));

        await repository.EnsureSchemaAsync(cancellationToken);
        var batches = new List<(EczaciKartInvoiceType Type, IReadOnlyList<EczaciKartInvoiceItem> Items)>();
        foreach (var type in InvoiceTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new InvoiceImportProgress(0, 0, $"{type.DisplayName} listesi okunuyor…", 0, 0, 0, 0, 0));
            var items = await portal.LoadInvoicesAsync(startDate, endDate, type.Key, cancellationToken);
            batches.Add((type, items));
        }

        var total = batches.Sum(batch => batch.Items.Count);
        var completed = 0;
        var inserted = 0;
        var versioned = 0;
        var unchanged = 0;
        var deleted = 0;
        var failed = 0;
        var medication = 0;

        foreach (var batch in batches)
        {
            var typeFailed = false;
            var seen = batch.Items.Select(item => InvoiceArchiveRepository.BuildLogicalKey(
                    item.TypeKey, item.Ettn, item.DocumentNumber))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in batch.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new InvoiceImportProgress(
                    completed, total, $"{item.DocumentNumber} ayrıntıları alınıyor…",
                    inserted, versioned, unchanged, deleted, failed));
                try
                {
                    var document = await portal.LoadInvoiceDocumentAsync(item, startDate, endDate, cancellationToken);
                    var saved = await repository.SaveAsync(document, startDate, endDate, cancellationToken: cancellationToken);
                    if (saved.IsMedicationInvoice) medication++;
                    switch (saved.Kind)
                    {
                        case InvoiceArchiveSaveKind.Inserted: inserted++; break;
                        case InvoiceArchiveSaveKind.Versioned: versioned++; break;
                        case InvoiceArchiveSaveKind.Unchanged: unchanged++; break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    failed++;
                    typeFailed = true;
                }
                completed++;
                progress?.Report(new InvoiceImportProgress(
                    completed, total, $"{completed}/{total} fatura işlendi",
                    inserted, versioned, unchanged, deleted, failed));
            }

            // A partial portal/detail failure must never create false deletion records.
            if (!typeFailed)
                deleted += await repository.MarkMissingDeletedAsync(
                    batch.Type.Key, startDate, endDate, seen, cancellationToken);
        }

        return new InvoiceImportResult(total, inserted, versioned, unchanged, deleted, failed, medication);
    }
}
