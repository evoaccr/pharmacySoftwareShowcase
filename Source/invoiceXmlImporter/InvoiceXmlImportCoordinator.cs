using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using pharmacyApp.Modules.Invoices;

namespace invoiceXmlImporter;

public sealed record ImportTypeProgress(
    string Key, string Name, int PortalTotal, int Found, int Downloaded, int Packaged, int Imported, bool Active);

public sealed record ImportProgress(
    string Phase, string Message, string CurrentType, string CurrentMonth, int CurrentPage, int TotalPages,
    int CurrentChunk, int TotalChunks, string CurrentInvoice, string CurrentAddress,
    int Completed, int Total, TimeSpan Elapsed, double AverageSeconds,
    IReadOnlyList<ImportTypeProgress> Types, int ImportedQrCodes, int IncompleteQrLines,
    int IndividualXmlQrRetries, string Error = "");

internal sealed record TraceabilitySaveResult(
    InvoiceTraceabilityImportStatus Status,
    bool IndividualXmlRetried,
    bool PtsRetried,
    int PtsAddedQrCodes,
    string PtsWarning);

public sealed class InvoiceXmlImportCoordinator
{
    public const int PackageSize = 250;
    private static readonly DateOnly EarliestPortalMonth = new(2016, 1, 1);
    private static readonly EczaciKartInvoiceType[] Types = EczaciKartInvoiceType.All
        .Where(type => type.Key is "incoming-invoice" or "outgoing-invoice" or "archive-invoice")
        .ToArray();
    private readonly CheckpointStore store;
    private readonly EczaciKartConnectionService portal = EczaciKartConnectionService.Instance;
    private readonly CompactInvoiceRepository repository = new();
    private readonly InvoicePtsEnrichmentService ptsEnrichment = new();

    public InvoiceXmlImportCoordinator(CheckpointStore store) => this.store = store;

    public async Task RunAsync(
        ImportCheckpoint checkpoint,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var initialElapsed = TimeSpan.FromSeconds(checkpoint.AccumulatedSeconds);
        try
        {
            checkpoint.LastError = string.Empty;
            NormalizeOptions(checkpoint);
            await EnsureExpectedTotalsAsync(checkpoint, progress, stopwatch, cancellationToken);
            if (checkpoint.ResumePhase == "Downloading")
            {
                checkpoint.Phase = "Downloading";
                store.Save(checkpoint);
                await DownloadAllAsync(checkpoint, progress, stopwatch, cancellationToken);
                checkpoint.Phase = "Packaging";
                checkpoint.ResumePhase = "Packaging";
                store.Save(checkpoint);
            }
            if (checkpoint.ResumePhase == "Packaging")
            {
                await BuildFinalPackagesAsync(checkpoint, progress, stopwatch, cancellationToken);
                checkpoint.Phase = "Importing";
                checkpoint.ResumePhase = "Importing";
                store.Save(checkpoint);
            }
            await ImportAllAsync(checkpoint, progress, stopwatch, cancellationToken);
            if (IsCompletePortalCoverage(checkpoint))
                await new InvoiceIncrementalSyncService()
                    .RecordFullImportCompletionAsync(checkpoint.RunCutoff, cancellationToken);
            checkpoint.Phase = "Completed";
            checkpoint.ResumePhase = "Completed";
            checkpoint.AccumulatedSeconds = (initialElapsed + stopwatch.Elapsed).TotalSeconds;
            store.Save(checkpoint);
            var incompleteQrLines = checkpoint.Packages.Sum(package => package.IncompleteQrLines);
            var traceabilityMessage = incompleteQrLines > 0
                ? $" {incompleteQrLines:N0} ilaç satırında Eczacı Kart XML kaynağı tekil karekodları sağlamadı; bu satırlar eksik olarak işaretlendi."
                : " Karekod gerektiren bütün ilaç satırları doğrulandı.";
            Report(checkpoint, progress, stopwatch, "Tamamlandı",
                IsCompletePortalCoverage(checkpoint)
                    ? "Üç fatura türü tarandı; XML ayrıntıları eski program kayıtlarıyla birleştirildi ve kaydedilme durumu korundu."
                      + traceabilityMessage
                    : "Debug aktarımı seçilen tür, sayı ve sıralama ile tamamlandı; tam arşiv güncelleme işareti değiştirilmedi."
                      + traceabilityMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            checkpoint.AccumulatedSeconds = (initialElapsed + stopwatch.Elapsed).TotalSeconds;
            checkpoint.Phase = "Paused";
            store.Save(checkpoint);
            Report(checkpoint, progress, stopwatch, "Duraklatıldı",
                "Kategori, 250'li paket, alt paket ve XML konumu kaydedildi; daha sonra devam edebilirsiniz.");
        }
        catch (Exception exception)
        {
            checkpoint.AccumulatedSeconds = (initialElapsed + stopwatch.Elapsed).TotalSeconds;
            checkpoint.Phase = "Faulted";
            checkpoint.LastError = exception.GetBaseException().Message;
            store.Save(checkpoint);
            Report(checkpoint, progress, stopwatch, "Hata", "Aktarım güvenli bir kontrol noktasında durduruldu.",
                error: checkpoint.LastError);
            throw;
        }
    }

    private async Task EnsureExpectedTotalsAsync(
        ImportCheckpoint checkpoint,
        IProgress<ImportProgress> progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var selectedTypes = SelectedTypes(checkpoint);
        if (selectedTypes.All(type => checkpoint.ExpectedTotals.ContainsKey(type.Key))
            && selectedTypes.All(type => checkpoint.TargetTotals.ContainsKey(type.Key))) return;
        Report(checkpoint, progress, stopwatch, "Sayılıyor",
            "Portal ana sayfasındaki üç fatura türünün toplamı okunuyor…");
        var totals = await RetryAsync(() => portal.LoadDashboardCountsAsync(cancellationToken), cancellationToken);
        foreach (var type in selectedTypes)
            checkpoint.ExpectedTotals[type.Key] = totals.FirstOrDefault(item => item.TypeKey == type.Key)?.Count ?? 0;
        checkpoint.TargetTotals.Clear();
        var remaining = checkpoint.MaximumInvoices ?? int.MaxValue;
        foreach (var type in selectedTypes)
        {
            var target = Math.Min(checkpoint.ExpectedTotals.GetValueOrDefault(type.Key), remaining);
            checkpoint.TargetTotals[type.Key] = target;
            if (remaining != int.MaxValue) remaining -= target;
        }
        checkpoint.RunCutoff = checkpoint.CreatedAt;
        store.Save(checkpoint);
        Report(checkpoint, progress, stopwatch, "Sayım tamamlandı",
            $"Portal toplamı: {selectedTypes.Sum(type => checkpoint.ExpectedTotals.GetValueOrDefault(type.Key)):N0}; "
            + $"bu çalışma hedefi: {checkpoint.TargetTotals.Values.Sum():N0} belge.");
    }

    private async Task DownloadAllAsync(
        ImportCheckpoint checkpoint,
        IProgress<ImportProgress> progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        foreach (var type in SelectedTypes(checkpoint))
        {
            if (checkpoint.CompletedDownloadTypeKeys.Contains(type.Key, StringComparer.Ordinal)) continue;
            if (checkpoint.TargetTotals.GetValueOrDefault(type.Key) == 0)
            {
                checkpoint.CompletedDownloadTypeKeys.Add(type.Key);
                store.Save(checkpoint);
                Report(checkpoint, progress, stopwatch, "İndiriliyor",
                    $"{type.DisplayName}: portal toplamı 0, kategori tamamlandı.", type.Key);
                continue;
            }

            await DownloadListedTypeAsync(checkpoint, type, type.Key == "incoming-invoice",
                progress, stopwatch, cancellationToken);

            checkpoint.CompletedDownloadTypeKeys.Add(type.Key);
            store.Save(checkpoint);
            var downloaded = DownloadedCount(checkpoint, type.Key);
            var expected = checkpoint.TargetTotals.GetValueOrDefault(type.Key);
            Report(checkpoint, progress, stopwatch, "Kategori tamamlandı",
                $"{type.DisplayName}: {downloaded:N0} XML indirildi (portal toplamı {expected:N0}).", type.Key);
        }
    }

    private async Task DownloadListedTypeAsync(
        ImportCheckpoint checkpoint,
        EczaciKartInvoiceType type,
        bool useBulkEndpoint,
        IProgress<ImportProgress> progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var target = checkpoint.TargetTotals.GetValueOrDefault(type.Key);
        foreach (var month in OrderedMonths(checkpoint, type.Key))
        {
            if (DownloadedCount(checkpoint, type.Key) >= target) break;
            if (MonthIsComplete(checkpoint, type.Key, month)) continue;
            Report(checkpoint, progress, stopwatch, "Taranıyor",
                $"{type.DisplayName}: {month:MM.yyyy} belge listesi okunuyor…", type.Key, month);
            var monthEnd = MonthEndTimestamp(month, checkpoint.RunCutoff);
            var loaded = await RetryAsync(
                () => portal.LoadInvoicesAsync(month, monthEnd, type.Key, cancellationToken), cancellationToken);
            var orderedItems = loaded
                .Where(item => !string.IsNullOrWhiteSpace(item.Ettn))
                .GroupBy(item => item.Ettn, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var itemsOutsideMonth = checkpoint.Pages
                .Where(page => page.TypeKey == type.Key && page.Month != month && page.DownloadComplete)
                .Sum(page => page.XmlCount);
            var monthCapacity = Math.Max(0, target - itemsOutsideMonth);
            var items = OrderItems(orderedItems, checkpoint, type.Key).Take(monthCapacity).ToArray();
            var groups = items.Chunk(PackageSize).ToArray();
            if (groups.Length == 0)
            {
                var empty = FindPage(checkpoint, type.Key, month, 0) ?? AddPage(checkpoint, type, month, 0);
                ApplySelection(empty, 0, 0, [], string.Empty);
                empty.DownloadComplete = true;
                store.Save(checkpoint);
                continue;
            }

            for (var pageIndex = 0; pageIndex < groups.Length; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageItems = groups[pageIndex];
                var records = pageItems
                    .Select(item => useBulkEndpoint ? ReadBulkRecordId(item.RawJson) : item.Ettn.Trim())
                    .ToArray();
                var hash = HashRecords(records);
                var page = FindPage(checkpoint, type.Key, month, pageIndex) ?? AddPage(checkpoint, type, month, pageIndex);
                if (page.RecordsHash.Length > 0 && page.RecordsHash != hash)
                    throw new InvalidOperationException(
                        $"{type.DisplayName} {month:MM.yyyy} kayıt sırası devam sırasında değişti.");
                ApplySelection(page, items.Length, groups.Length, records, hash);
                store.Save(checkpoint);
                if (page.DownloadComplete) continue;
                var chunks = records.Chunk(EczaciKartConnectionService.XmlExportPortalBatchLimit).ToArray();
                var packet = checkpoint.Pages.Count(candidate => candidate.TypeKey == type.Key && candidate.DownloadComplete) + 1;
                var expectedPackets = ExpectedPackageCount(checkpoint, type.Key);
                for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var firstItem = pageItems[chunkIndex * EczaciKartConnectionService.XmlExportPortalBatchLimit];
                    var chunkPath = ChunkPath(checkpoint, page, chunkIndex);
                    if (ChunkAlreadySaved(page, chunkIndex)) continue;
                    Report(checkpoint, progress, stopwatch, "İndiriliyor",
                        $"{type.DisplayName}: {packet}. paket, {chunkIndex + 1}/{chunks.Length} XML grubu indiriliyor…",
                        type.Key, month, packet, expectedPackets, chunkIndex + 1, chunks.Length,
                        currentInvoice: $"{firstItem.DocumentNumber} • {firstItem.IssueDate}",
                        currentAddress: firstItem.CounterpartyName);
                    byte[] bytes;
                    if (useBulkEndpoint)
                    {
                        try
                        {
                            bytes = await RetryAsync(
                                () => portal.DownloadXmlChunkAsync(
                                    type.Key, month.Year, month.Month - 1,
                                    chunks[chunkIndex], cancellationToken),
                                cancellationToken);
                        }
                        catch (EczaciKartXmlDownloadException exception)
                            when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                        {
                            var uuids = pageItems
                                .Skip(chunkIndex * EczaciKartConnectionService.XmlExportPortalBatchLimit)
                                .Take(chunks[chunkIndex].Length)
                                .Select(item => item.Ettn.Trim())
                                .ToArray();
                            bytes = await BuildIndividualChunkZipAsync(
                                checkpoint, type, month, uuids, cancellationToken);
                        }
                    }
                    else
                    {
                        bytes = await BuildIndividualChunkZipAsync(
                            checkpoint, type, month, chunks[chunkIndex], cancellationToken);
                    }
                    ValidateZip(bytes, chunks[chunkIndex].Length);
                    AtomicWrite(chunkPath, bytes);
                    SaveChunk(page, chunkIndex, chunkPath, checkpoint);
                }
                CompleteRawPage(checkpoint, page);
                Report(checkpoint, progress, stopwatch, "İndiriliyor",
                    $"{type.DisplayName}: {page.XmlCount} XML içeren ham sayfa paketi kaydedildi.",
                    type.Key, month, packet, expectedPackets, chunks.Length, chunks.Length);
            }
        }
    }

    private async Task<byte[]> BuildIndividualChunkZipAsync(
        ImportCheckpoint checkpoint,
        EczaciKartInvoiceType type,
        DateOnly month,
        IReadOnlyList<string> recordIds,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var recordId in recordIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var xml = await RetryAsync(() => portal.DownloadSingleXmlAsync(
                    type.Key, recordId, month.Year, month.Month - 1, cancellationToken), cancellationToken);
                ValidateXml(xml, type.DisplayName, recordId);
                var entry = archive.CreateEntry($"{SafeFileName(recordId)}.xml", CompressionLevel.Optimal);
                await using var destination = entry.Open();
                await destination.WriteAsync(xml, cancellationToken);
            }
        }
        return memory.ToArray();
    }

    private async Task BuildFinalPackagesAsync(
        ImportCheckpoint checkpoint,
        IProgress<ImportProgress> progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        checkpoint.Phase = "Packaging";
        checkpoint.ResumePhase = "Packaging";
        store.Save(checkpoint);
        foreach (var type in SelectedTypes(checkpoint))
        {
            if (checkpoint.CompletedPackageTypeKeys.Contains(type.Key, StringComparer.Ordinal)) continue;
            var sources = ReadSourceEntries(checkpoint, type.Key);
            var groups = sources.Chunk(PackageSize).ToArray();
            for (var index = 0; index < groups.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = checkpoint.Packages.FirstOrDefault(package =>
                    package.TypeKey == type.Key && package.PackageIndex == index);
                if (existing is not null && File.Exists(existing.ZipPath)
                    && CountXmlEntries(existing.ZipPath) == existing.XmlCount) continue;
                Report(checkpoint, progress, stopwatch, "Paketleniyor",
                    $"{type.DisplayName}: {index + 1}/{groups.Length} numaralı 250'li ZIP oluşturuluyor…",
                    type.Key, currentPage: index + 1, totalPages: groups.Length);
                var path = FinalPackagePath(checkpoint, type, index, groups[index].Length);
                BuildFinalPackage(groups[index], path);
                var package = existing ?? new PackageCheckpoint
                {
                    TypeKey = type.Key, TypeName = type.DisplayName, PackageIndex = index
                };
                if (existing is null) checkpoint.Packages.Add(package);
                package.TotalPackages = groups.Length;
                package.ZipPath = path;
                package.XmlCount = groups[index].Length;
                store.Save(checkpoint);
            }
            checkpoint.CompletedPackageTypeKeys.Add(type.Key);
            store.Save(checkpoint);
        }
    }

    private async Task ImportAllAsync(
        ImportCheckpoint checkpoint,
        IProgress<ImportProgress> progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        await repository.EnsureCompatibleSchemaAsync(cancellationToken);
        foreach (var package in checkpoint.Packages
                     .Where(package => SelectedTypes(checkpoint).Any(type => type.Key == package.TypeKey))
                     .OrderBy(package => TypeOrder(package.TypeKey)).ThenBy(package => package.PackageIndex))
        {
            if (package.ImportComplete) continue;
            using var archive = ZipFile.OpenRead(package.ZipPath);
            var entries = archive.Entries.Where(IsXml).OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();
            if (entries.Length != package.XmlCount)
                throw new InvalidDataException(
                    $"{package.ZipPath} içinde beklenen {package.XmlCount} yerine {entries.Length} XML var.");
            for (var index = package.ImportedEntries; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                Report(checkpoint, progress, stopwatch, "Veritabanına aktarılıyor", $"{entry.Name} okunuyor…",
                    package.TypeKey, currentPage: package.PackageIndex + 1, totalPages: package.TotalPackages,
                    currentInvoice: entry.Name);
                string xml;
                await using (var stream = entry.Open())
                using (var reader = new StreamReader(stream)) xml = await reader.ReadToEndAsync(cancellationToken);
                var document = UblInvoiceParser.Parse(xml, package.TypeKey);
                var traceability = await SaveWithTraceabilityEnrichmentAsync(
                    checkpoint, document, cancellationToken);
                package.ImportedQrCodes += traceability.Status.StoredQrCodes;
                package.IncompleteQrLines += traceability.Status.IncompleteQrLines;
                if (traceability.IndividualXmlRetried) package.IndividualXmlQrRetries++;
                package.ImportedEntries = index + 1;
                store.Save(checkpoint);
                var counterparty = EczaciKartInvoiceType.FromKey(package.TypeKey).IsIncoming
                    ? document.InvoiceFrom : document.InvoiceTo;
                var qrMessage = traceability.Status.IncompleteQrLines > 0
                    ? $" {traceability.Status.StoredQrCodes:N0} karekod kaydedildi; "
                      + $"{traceability.Status.IncompleteQrLines:N0} ilaç satırında kaynak XML karekodları eksik."
                    : traceability.Status.StoredQrCodes > 0
                        ? $" {traceability.Status.StoredQrCodes:N0} tekil karekod kaydedildi."
                        : string.Empty;
                if (traceability.PtsAddedQrCodes > 0)
                    qrMessage += $" PTS paketinden {traceability.PtsAddedQrCodes:N0} ek karekod tamamlandı.";
                else if (traceability.PtsRetried && traceability.PtsWarning.Length > 0)
                    qrMessage += $" PTS: {traceability.PtsWarning}";
                Report(checkpoint, progress, stopwatch, "Veritabanına aktarılıyor",
                    $"{document.InvoiceNumber} kaydedildi.{qrMessage}", package.TypeKey,
                    currentPage: package.PackageIndex + 1, totalPages: package.TotalPackages,
                    currentInvoice: document.InvoiceNumber,
                    currentAddress: string.IsNullOrWhiteSpace(counterparty.Address) ? counterparty.Name : counterparty.Address);
            }
            package.ImportComplete = true;
            store.Save(checkpoint);
        }
    }

    private async Task<TraceabilitySaveResult> SaveWithTraceabilityEnrichmentAsync(
        ImportCheckpoint checkpoint,
        EczaciKartInvoiceDocument document,
        CancellationToken cancellationToken)
    {
        await SaveDocumentAsync(checkpoint, document, cancellationToken);
        var status = await repository.LoadTraceabilityStatusAsync(document.Ettn, cancellationToken);
        var sourceQrCodes = ReadSourceQrCodes(document);
        var retried = false;
        if (document.ListItem.TypeKey == "incoming-invoice"
            && status.IncompleteQrLines > 0
            && document.IssueDate is { } issueDate)
        {
            retried = true;
            var bytes = await RetryAsync(() => portal.DownloadSingleXmlAsync(
                document.ListItem.TypeKey, document.Ettn,
                issueDate.Year, issueDate.Month - 1, cancellationToken), cancellationToken);
            ValidateXml(bytes, document.ListItem.TypeDisplay, document.Ettn);
            var individual = UblInvoiceParser.Parse(Encoding.UTF8.GetString(bytes), document.ListItem.TypeKey);
            if (!string.Equals(individual.Ettn.Trim(), document.Ettn.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Tekil XML yanıtı beklenen faturaya ait değil: {document.InvoiceNumber}.");
            var individualQrCodes = ReadSourceQrCodes(individual);
            if (individualQrCodes.Except(sourceQrCodes, StringComparer.Ordinal).Any())
            {
                await SaveDocumentAsync(checkpoint, individual, cancellationToken);
                document = individual;
                sourceQrCodes = individualQrCodes;
                status = await repository.LoadTraceabilityStatusAsync(document.Ettn, cancellationToken);
            }
        }
        var ptsRetried = false;
        var ptsAdded = 0;
        var ptsWarning = string.Empty;
        if (status.IncompleteQrLines > 0)
        {
            var pts = await ptsEnrichment.EnrichAsync(document, cancellationToken);
            ptsRetried = pts.Attempted;
            ptsAdded = pts.AddedQrCodes;
            ptsWarning = pts.Warning;
            if (pts.AddedQrCodes > 0)
            {
                await SaveDocumentAsync(checkpoint, pts.Document, cancellationToken);
                document = pts.Document;
                status = await repository.LoadTraceabilityStatusAsync(document.Ettn, cancellationToken);
            }
        }
        return new TraceabilitySaveResult(status, retried, ptsRetried, ptsAdded, ptsWarning);
    }

    private async Task SaveDocumentAsync(
        ImportCheckpoint checkpoint,
        EczaciKartInvoiceDocument document,
        CancellationToken cancellationToken)
    {
        if (checkpoint.WithoutFarmassistRecords)
            await repository.SaveAsync(document, cancellationToken);
        else
            await repository.SaveFullArchiveAsync(document, cancellationToken);
    }

    private static IReadOnlySet<string> ReadSourceQrCodes(EczaciKartInvoiceDocument document) => document.Lines
        .SelectMany(line => InvoiceTraceabilityParser.Parse(line.Values).Codes)
        .Select(code => code.QrCode)
        .ToHashSet(StringComparer.Ordinal);

    private void Report(
        ImportCheckpoint checkpoint,
        IProgress<ImportProgress> progress,
        Stopwatch stopwatch,
        string phase,
        string message,
        string activeType = "",
        DateOnly? month = null,
        int currentPage = 0,
        int totalPages = 0,
        int currentChunk = 0,
        int totalChunks = 0,
        string currentInvoice = "",
        string currentAddress = "",
        string error = "")
    {
        var states = Types.Select(type =>
        {
            var pages = checkpoint.Pages.Where(page => page.TypeKey == type.Key).ToArray();
            var packages = checkpoint.Packages.Where(package => package.TypeKey == type.Key).ToArray();
            return new ImportTypeProgress(type.Key, type.DisplayName,
                checkpoint.ExpectedTotals.GetValueOrDefault(type.Key), pages.Sum(page => page.XmlCount),
                pages.Where(page => page.DownloadComplete).Sum(page => page.XmlCount),
                packages.Sum(package => package.XmlCount), packages.Sum(package => package.ImportedEntries),
                type.Key == activeType);
        }).ToArray();
        var total = checkpoint.TargetTotals.Values.Sum() * 2;
        var completed = states.Sum(state => state.Downloaded + state.Imported);
        var elapsed = checkpoint.Phase is "Completed" or "Paused" or "Faulted"
            ? TimeSpan.FromSeconds(checkpoint.AccumulatedSeconds)
            : TimeSpan.FromSeconds(checkpoint.AccumulatedSeconds) + stopwatch.Elapsed;
        var importedQrCodes = checkpoint.Packages.Sum(package => package.ImportedQrCodes);
        var incompleteQrLines = checkpoint.Packages.Sum(package => package.IncompleteQrLines);
        var individualRetries = checkpoint.Packages.Sum(package => package.IndividualXmlQrRetries);
        progress.Report(new ImportProgress(phase, message,
            Types.FirstOrDefault(type => type.Key == activeType)?.DisplayName ?? string.Empty,
            month?.ToString("MM.yyyy") ?? string.Empty, currentPage, totalPages, currentChunk, totalChunks,
            currentInvoice, currentAddress, completed, Math.Max(total, 1), elapsed,
            completed == 0 ? 0 : elapsed.TotalSeconds / completed, states,
            importedQrCodes, incompleteQrLines, individualRetries, error));
    }

    private static DateTimeOffset MonthEndTimestamp(DateOnly month, DateTimeOffset cutoff)
    {
        var endDate = month.AddMonths(1).AddDays(-1);
        var cutoffDate = DateOnly.FromDateTime(cutoff.Date);
        if (endDate > cutoffDate) endDate = cutoffDate;
        return new DateTimeOffset(endDate.ToDateTime(new TimeOnly(23, 59, 59)), cutoff.Offset);
    }

    private static string ReadBulkRecordId(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("recordId", StringComparison.OrdinalIgnoreCase)) continue;
            var value = property.Value.ToString().Trim();
            if (long.TryParse(value, out _)) return value;
        }
        throw new InvalidDataException("Eczacı Kart gelen fatura kaydında sayısal recordId bulunamadı.");
    }

    private static PageCheckpoint AddPage(
        ImportCheckpoint checkpoint, EczaciKartInvoiceType type, DateOnly month, int pageIndex)
    {
        var page = new PageCheckpoint
            { TypeKey = type.Key, TypeName = type.DisplayName, Month = month, PageIndex = pageIndex };
        checkpoint.Pages.Add(page);
        return page;
    }

    private static void ApplySelection(
        PageCheckpoint page, int totalDocuments, int totalPages, IReadOnlyList<string> records, string hash)
    {
        if (page.RecordsHash.Length > 0 && page.RecordsHash != hash)
            throw new InvalidOperationException(
                $"{page.TypeName} {page.Month:MM.yyyy} sayfa içeriği devam sırasında değişti.");
        page.TotalDocuments = totalDocuments;
        page.TotalPages = totalPages;
        page.RecordIds = records.ToList();
        page.RecordsHash = hash;
        page.XmlCount = records.Count;
    }

    private void CompleteRawPage(ImportCheckpoint checkpoint, PageCheckpoint page)
    {
        page.ZipPath = RawPageZipPath(checkpoint, page);
        MergeZips(page.ChunkFiles, page.ZipPath, page.XmlCount);
        page.DownloadComplete = true;
        store.Save(checkpoint);
    }

    private void SaveChunk(PageCheckpoint page, int chunkIndex, string path, ImportCheckpoint checkpoint)
    {
        while (page.ChunkFiles.Count <= chunkIndex) page.ChunkFiles.Add(string.Empty);
        page.ChunkFiles[chunkIndex] = path;
        store.Save(checkpoint);
    }

    private static bool ChunkAlreadySaved(PageCheckpoint page, int chunkIndex) =>
        chunkIndex < page.ChunkFiles.Count && File.Exists(page.ChunkFiles[chunkIndex]);

    private static PageCheckpoint? FindPage(
        ImportCheckpoint checkpoint, string typeKey, DateOnly month, int pageIndex) =>
        checkpoint.Pages.FirstOrDefault(page => page.TypeKey == typeKey && page.Month == month && page.PageIndex == pageIndex);

    private static bool MonthIsComplete(ImportCheckpoint checkpoint, string typeKey, DateOnly month)
    {
        var pages = checkpoint.Pages.Where(page => page.TypeKey == typeKey && page.Month == month).ToArray();
        if (pages.Length == 0 || pages.Any(page => !page.DownloadComplete)) return false;
        var expectedPages = pages[0].TotalPages;
        return expectedPages == 0 ? pages.Length == 1 : pages.Length >= expectedPages;
    }

    private static int DownloadedCount(ImportCheckpoint checkpoint, string typeKey) =>
        checkpoint.Pages.Where(page => page.TypeKey == typeKey && page.DownloadComplete).Sum(page => page.XmlCount);

    private static int ExpectedPackageCount(ImportCheckpoint checkpoint, string typeKey)
    {
        var total = checkpoint.TargetTotals.GetValueOrDefault(typeKey);
        return total == 0 ? 0 : (int)Math.Ceiling(total / (double)PackageSize);
    }

    private string ChunkPath(ImportCheckpoint checkpoint, PageCheckpoint page, int index)
    {
        var folder = Path.Combine(store.RunDirectory(checkpoint), "raw", page.TypeKey);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{page.Month:yyyyMM}_p{page.PageIndex + 1:000}_c{index + 1:00}.zip");
    }

    private string RawPageZipPath(ImportCheckpoint checkpoint, PageCheckpoint page)
    {
        var folder = Path.Combine(store.RunDirectory(checkpoint), "raw", page.TypeKey);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{page.Month:yyyyMM}_p{page.PageIndex + 1:000}_{page.XmlCount}.zip");
    }

    private string FinalPackagePath(
        ImportCheckpoint checkpoint, EczaciKartInvoiceType type, int index, int count)
    {
        var folder = Path.Combine(store.RunDirectory(checkpoint), "packages", type.Key);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{type.Key}_paket_{index + 1:00000}_{count}.zip");
    }

    private static IReadOnlyList<SourceEntry> ReadSourceEntries(ImportCheckpoint checkpoint, string typeKey)
    {
        var result = new List<SourceEntry>();
        foreach (var page in checkpoint.Pages.Where(page => page.TypeKey == typeKey && page.DownloadComplete && page.XmlCount > 0)
                     .OrderByDescending(page => page.Month).ThenBy(page => page.PageIndex))
        {
            using var archive = ZipFile.OpenRead(page.ZipPath);
            result.AddRange(archive.Entries.Where(IsXml).OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .Select(entry => new SourceEntry(page.ZipPath, entry.FullName)));
        }
        return result;
    }

    private static void BuildFinalPackage(IReadOnlyList<SourceEntry> sources, string destination)
    {
        var temporary = destination + ".part";
        if (File.Exists(temporary)) File.Delete(temporary);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var output = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            foreach (var source in sources)
            {
                using var input = ZipFile.OpenRead(source.ZipPath);
                var entry = input.GetEntry(source.EntryName)
                    ?? throw new InvalidDataException($"Kaynak XML bulunamadı: {source.EntryName}");
                var name = entry.Name;
                for (var suffix = 2; !names.Add(name); suffix++)
                    name = $"{Path.GetFileNameWithoutExtension(entry.Name)}_{suffix}.xml";
                var target = output.CreateEntry(name, CompressionLevel.Optimal);
                using var from = entry.Open();
                using var to = target.Open();
                from.CopyTo(to);
            }
        }
        if (CountXmlEntries(temporary) != sources.Count)
            throw new InvalidDataException("250'li nihai XML paketi doğrulanamadı.");
        File.Move(temporary, destination, true);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".part";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }

    private static void ValidateZip(byte[] bytes, int expectedXml)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var count = archive.Entries.Count(IsXml);
        if (count != expectedXml)
            throw new InvalidDataException($"İndirilen pakette beklenen {expectedXml} yerine {count} XML var.");
    }

    private static void MergeZips(IReadOnlyList<string> sources, string destination, int expectedXml)
    {
        var temporary = destination + ".part";
        if (File.Exists(temporary)) File.Delete(temporary);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var output = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            foreach (var source in sources)
            using (var input = ZipFile.OpenRead(source))
            foreach (var entry in input.Entries.Where(IsXml))
            {
                var name = entry.Name;
                for (var suffix = 2; !names.Add(name); suffix++)
                    name = $"{Path.GetFileNameWithoutExtension(entry.Name)}_{suffix}.xml";
                var target = output.CreateEntry(name, CompressionLevel.Optimal);
                using var from = entry.Open();
                using var to = target.Open();
                from.CopyTo(to);
            }
        }
        if (CountXmlEntries(temporary) != expectedXml)
            throw new InvalidDataException("Birleştirilen ham XML paketi doğrulanamadı.");
        File.Move(temporary, destination, true);
    }

    private static int CountXmlEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Count(IsXml);
    }

    private static bool IsXml(ZipArchiveEntry entry) =>
        entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static void ValidateXml(byte[] bytes, string typeName, string recordId)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            _ = XDocument.Load(stream, LoadOptions.None);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            throw new InvalidDataException($"{typeName} / {recordId} geçerli XML değil.", exception);
        }
    }

    private static string HashRecords(IEnumerable<string> records) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', records)))).ToLowerInvariant();

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static int TypeOrder(string key)
    {
        for (var index = 0; index < Types.Length; index++)
            if (Types[index].Key == key) return index;
        return int.MaxValue;
    }

    private static IReadOnlyList<EczaciKartInvoiceType> SelectedTypes(ImportCheckpoint checkpoint)
    {
        if (!checkpoint.DebugMode || string.IsNullOrWhiteSpace(checkpoint.SelectedTypeKey)) return Types;
        var selected = Types.FirstOrDefault(type => type.Key == checkpoint.SelectedTypeKey);
        return selected is null ? Types : [selected];
    }

    private static void NormalizeOptions(ImportCheckpoint checkpoint)
    {
        if (!checkpoint.DebugMode)
        {
            checkpoint.SelectedTypeKey = string.Empty;
            checkpoint.MaximumInvoices = null;
            checkpoint.DownloadOrder = InvoiceDownloadOrder.NewestFirst;
            checkpoint.WithoutFarmassistRecords = false;
        }
        if (!InvoiceDownloadOrder.IsValid(checkpoint.DownloadOrder))
            checkpoint.DownloadOrder = InvoiceDownloadOrder.NewestFirst;
        if (checkpoint.MaximumInvoices <= 0) checkpoint.MaximumInvoices = null;
        if (!Types.Any(type => type.Key == checkpoint.SelectedTypeKey)) checkpoint.SelectedTypeKey = string.Empty;
    }

    private static bool IsCompletePortalCoverage(ImportCheckpoint checkpoint) =>
        SelectedTypes(checkpoint).Count == Types.Length
        && checkpoint.MaximumInvoices is null
        && Types.All(type => DownloadedCount(checkpoint, type.Key)
                             >= checkpoint.ExpectedTotals.GetValueOrDefault(type.Key));

    private static IReadOnlyList<DateOnly> OrderedMonths(ImportCheckpoint checkpoint, string typeKey)
    {
        var latest = new DateOnly(checkpoint.RunCutoff.Year, checkpoint.RunCutoff.Month, 1);
        var months = new List<DateOnly>();
        for (var month = EarliestPortalMonth; month <= latest; month = month.AddMonths(1)) months.Add(month);
        return checkpoint.DownloadOrder switch
        {
            InvoiceDownloadOrder.OldestFirst => months,
            InvoiceDownloadOrder.Random => months
                .OrderBy(month => StableOrder(checkpoint.RunId, typeKey, month.ToString("yyyyMM"))).ToArray(),
            _ => months.AsEnumerable().Reverse().ToArray()
        };
    }

    private static IEnumerable<EczaciKartInvoiceItem> OrderItems(
        IEnumerable<EczaciKartInvoiceItem> items,
        ImportCheckpoint checkpoint,
        string typeKey) => checkpoint.DownloadOrder switch
        {
            InvoiceDownloadOrder.OldestFirst => items
                .OrderBy(item => item.ReceivedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.DocumentNumber, StringComparer.Ordinal),
            InvoiceDownloadOrder.Random => items
                .OrderBy(item => StableOrder(checkpoint.RunId, typeKey, item.Ettn))
                .ThenBy(item => item.Ettn, StringComparer.Ordinal),
            _ => items
                .OrderByDescending(item => item.ReceivedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.DocumentNumber, StringComparer.Ordinal)
        };

    private static string StableOrder(string runId, string typeKey, string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}|{typeKey}|{value}")));

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        Exception? last = null;
        var delays = new[] { 1, 2, 5, 10 };
        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await action(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (EczaciKartXmlDownloadException exception)
                when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                throw;
            }
            catch (Exception exception)
            {
                last = exception;
                if (attempt < delays.Length)
                    await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), cancellationToken);
            }
        }
        throw new InvalidOperationException("Ağ işlemi yeniden denemelere rağmen tamamlanamadı.", last);
    }

    private sealed record SourceEntry(string ZipPath, string EntryName);
}
