using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using pharmacyApp.Modules;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Cards;

namespace pharmacyApp.Modules.Medula;

public partial class MedulaPrescriptionListView : UserControl, IModuleTabRequestSource, IModuleBackgroundWorkSource
{
    private const string AutonomousUser = "Otonom";
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly MedulaAutomation service = MedulaAutomation.Instance;
    private readonly MedulaDirectoryRepository directoryRepository = new();
    private readonly MedulaPrescriptionRepository prescriptionRepository = new();
    private readonly SolidColorBrush returnStatusBorderBrush = new(Color.FromRgb(186, 201, 207));
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? patientSearchCancellation;
    private CancellationTokenSource? patientQueryCancellation;
    private CancellationTokenSource? filterRefreshCancellation;
    private CancellationTokenSource? reachabilityMergeCancellation;
    private CancellationTokenSource? mergeCancellation;
    private PatientSuggestion? selectedPatient;
    private bool loadedOnce;
    private bool busy;
    private bool suppressFilterModeEvents;
    private bool suppressPatientFilter;
    private bool suppressAutomaticRefresh;
    private bool periodOptionsLoaded;
    private bool isAdmin;
    private bool mergeInProgress;
    private bool reachabilityMergeInProgress;
    private bool lastReportedBackgroundWork;
    private int reachabilityMergeRevision;
    private string? reachabilityTypeValue;
    private IReadOnlyList<MedulaPrescriptionListItem> reachableRows = [];
    private IReadOnlyList<MedulaPrescriptionListItem> databaseRows = [];
    private IReadOnlyList<MedulaPrescriptionListItem> activePatientRows = [];
    private IReadOnlyList<MedulaPrescriptionHistoryEntry> loadedHistoryEntries = [];
    private Guid? loadedHistoryPrescriptionUid;
    private string prescriptionSortMember = nameof(MedulaPrescriptionListItem.MedulaFilterDate);

    public bool IsBackgroundWorkRunning => busy || reachabilityMergeInProgress;
    public event Action<bool>? BackgroundWorkStateChanged;

    public MedulaPrescriptionListView()
    {
        Prescriptions = new ObservableCollection<MedulaPrescriptionListItem>();
        PrescriptionHistoryEntries = new ObservableCollection<MedulaPrescriptionHistoryEntry>();
        PrescriptionView = CollectionViewSource.GetDefaultView(Prescriptions);
        PrescriptionView.Filter = FilterPrescription;
        InitializeComponent();
        DataContext = this;
        if (PrescriptionView is ListCollectionView listView)
            listView.CustomSort = new PrescriptionRowComparer(
                nameof(MedulaPrescriptionListItem.MedulaFilterDate),
                ListSortDirection.Descending,
                useMedulaPositionTieBreak: true);
        MedulaFilterDateColumn.SortDirection = ListSortDirection.Descending;

        var today = DateTime.Today;
        StartDatePicker.SelectedDate = today;
        EndDatePicker.SelectedDate = today;
        DayComboBox.ItemsSource = Enumerable.Range(1, today.Day).ToArray();
        DayComboBox.SelectedItem = today.Day;
        GroupComboBox.ItemsSource = MedulaPrescriptionListTypes.Groups;
        GroupComboBox.SelectedValue = "B Grubu";
        ReturnStatusComboBox.BorderBrush = returnStatusBorderBrush;
        UpdateReturnStatuses(MedulaPrescriptionListTypes.NoReturn);
        PeriodComboBox.ItemsSource = DefaultPeriodOptions();
        PeriodComboBox.SelectedIndex = 0;
        TypeFilterComboBox.SelectedIndex = 0;
        CustomRangeBox.Text = MedulaPrescriptionListRefreshPolicy.DefaultQuickRangeDays
            .ToString(CultureInfo.InvariantCulture);
    }

    public ObservableCollection<MedulaPrescriptionListItem> Prescriptions { get; }
    public ObservableCollection<MedulaPrescriptionHistoryEntry> PrescriptionHistoryEntries { get; }
    public ICollectionView PrescriptionView { get; }
    public event Action<ModuleTabRequest>? TabRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.StatusChanged += OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnWarningChanged;
        service.ScopedWarningChanged += OnWarningChanged;
        service.ScopedConfirmationChanged -= OnConfirmationChanged;
        service.ScopedConfirmationChanged += OnConfirmationChanged;
        service.ScopedNoticeChanged -= OnNoticeChanged;
        service.ScopedNoticeChanged += OnNoticeChanged;
        service.ForegroundOperationRequested -= OnForegroundOperationRequested;
        service.ForegroundOperationRequested += OnForegroundOperationRequested;
        if (loadedOnce) return;
        loadedOnce = true;
        var currentUserId = (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty;
        try
        {
            isAdmin = await prescriptionRepository.IsAdminAsync(currentUserId, lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            isAdmin = false;
            SetWarning($"Kullanıcı yetkisi denetlenemedi: {exception.Message}");
        }
        await SearchAsync(MedulaPrescriptionListRefreshCause.InitialOpen);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnWarningChanged;
        service.ScopedConfirmationChanged -= OnConfirmationChanged;
        service.ScopedNoticeChanged -= OnNoticeChanged;
        patientSearchCancellation?.Cancel();
        patientQueryCancellation?.Cancel();
        filterRefreshCancellation?.Cancel();
        PatientSuggestionPopup.IsOpen = false;
        // Keep rows and filter state when the shell temporarily unloads this tab.
    }

    private void OnForegroundOperationRequested()
    {
        mergeCancellation?.Cancel();
        reachabilityMergeCancellation?.Cancel();
    }

    private void OnServiceStatusChanged(object? sender, MedulaConnectionStatus status) =>
        Dispatcher.InvokeAsync(() =>
        {
            ConnectionText.Text = MedulaStatusText.For(status.State);
            StatusDot.Fill = status.State switch
            {
                MedulaConnectionState.Connected => new SolidColorBrush(Color.FromRgb(13, 148, 136)),
                MedulaConnectionState.Failed => new SolidColorBrush(Color.FromRgb(198, 67, 67)),
                _ => new SolidColorBrush(Color.FromRgb(210, 139, 44))
            };
        });

    private void OnWarningChanged(MedulaScopedText message) => Dispatcher.InvokeAsync(() =>
    {
        if (message.Scope != MedulaMessageScope.PrescriptionList) return;
        if ((mergeInProgress || reachabilityMergeInProgress)
            && IsExpectedEmptyUnfinishedPeriodWarning(message.Text)) return;
        if (mergeInProgress || reachabilityMergeInProgress) SetMergeWarning(message.Text);
        else SetMessage(message.Text, MedulaMessageSeverity.Error);
    });

    private void OnConfirmationChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.PrescriptionList) return;
        Dispatcher.InvokeAsync(() => SetMessage(message.Text, MedulaMessageSeverity.Success));
    }

    private void OnNoticeChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.PrescriptionList) return;
        Dispatcher.InvokeAsync(() => SetMessage(message.Text, MedulaMessageSeverity.Notice));
    }
    private async void OnSearchClick(object sender, RoutedEventArgs e) =>
        await SearchAfterCancelAsync(MedulaPrescriptionListRefreshCause.FilterChange);
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        CancelScheduledFilterRefresh();
        ShowCachedLocalRowsForRefresh();
        await Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);
        await SearchAsync(MedulaPrescriptionListRefreshCause.ExplicitRefresh);
    }

    private void ShowCachedLocalRowsForRefresh()
    {
        var localRows = databaseRows
            .Where(item => item.IsStoredLocally)
            .Select(item => item with { IsLiveOnMedula = false })
            .ToArray();
        activePatientRows = [];
        ReplaceDatabaseRows(localRows);
        ConnectionText.Text = $"{localRows.Length} yerel reçete · Medula yenileniyor";
    }

    private async Task SearchAfterCancelAsync(MedulaPrescriptionListRefreshCause cause)
    {
        CancelScheduledFilterRefresh();
        await SearchAsync(cause);
    }

    private void CancelScheduledFilterRefresh()
    {
        filterRefreshCancellation?.Cancel();
        filterRefreshCancellation?.Dispose();
        filterRefreshCancellation = null;
    }

    private async Task SearchAsync(MedulaPrescriptionListRefreshCause cause)
    {
        if (busy) return;
        if (GroupComboBox.SelectedValue is not string groupKey
            || ReturnStatusComboBox.SelectedValue is not string returnStatusKey
            || !MedulaPrescriptionListTypes.TryResolve(groupKey, returnStatusKey, out var typeValue))
        {
            StartInvalidReturnStatusFlash();
            SetWarning("Seçilen reçete grubu bu iade durumunu desteklemiyor. İade durumunu değiştirin.");
            return;
        }

        mergeCancellation?.Cancel();
        mergeCancellation?.Dispose();
        mergeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var operationToken = mergeCancellation.Token;
        var offline = ApplicationSettingsStore.Current.OfflinePrescriptionList;
        SetBusy(
            true,
            offline ? "Reçeteler yerel geçmişten alınıyor…" : "Yerel reçeteler gösteriliyor · Medula birleştiriliyor…",
            blockContent: offline);
        SetWarning(null);
        SetMergeWarning(null);
        try
        {
            var range = ResolveSelectedRange();
            if (range is null) return;
            var (startDate, endDate) = range.Value;
            IReadOnlyList<MedulaPrescriptionListItem> localRows = [];
            string? localLoadWarning = null;
            try
            {
                localRows = await prescriptionRepository.LoadLatestAsync(
                    startDate, endDate, typeValue, operationToken);
                if (ShowPermanentlyDeletedCheckBox.IsChecked == true)
                    localRows = localRows.Concat(await prescriptionRepository.LoadPermanentlyDeletedAsync(
                        startDate, endDate, typeValue, operationToken)).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                localLoadWarning = $"Yerel reçete geçmişi alınamadı: {exception.Message}";
            }

            activePatientRows = [];
            ReplaceDatabaseRows(localRows);
            ConnectionText.Text = offline ? "Offline" : "Connecting";

            await Dispatcher.InvokeAsync(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);

            if (offline)
            {
                if (localLoadWarning is not null) SetWarning(localLoadWarning);
                return;
            }

            mergeInProgress = true;
            SetMergeWarning(null);

            MedulaPrescriptionListResult result;
            if (PrescriptionDateModeCheckBox.IsChecked == true)
            {
                result = await service.SearchPrescriptionListAsync(
                    startDate, endDate, typeValue, operationToken);
            }
            else
            {
                var daily = DailyModeCheckBox.IsChecked == true;
                var day = DayComboBox.SelectedItem is int selectedDay ? selectedDay : DateTime.Today.Day;
                var period = PeriodComboBox.SelectedValue as string ?? string.Empty;
                result = await service.SearchPrescriptionListByMedulaFilterAsync(
                    daily, day, period, typeValue, operationToken);
            }

            var merged = await MergeAndImportAsync(localRows, result.Prescriptions, typeValue, operationToken);
            merged = ApplyMedulaReachability(
                merged,
                result.Prescriptions.Concat(
                    string.Equals(reachabilityTypeValue, typeValue, StringComparison.Ordinal)
                        ? reachableRows
                        : []));
            ReplaceDatabaseRows(merged);
            await RefreshActivePatientPrescriptionsAsync();
            ConnectionText.Text = result.Message;
            if (localLoadWarning is not null) SetMergeWarning(localLoadWarning);
            else if (!string.IsNullOrWhiteSpace(result.WarningText)
                && !IsExpectedEmptyUnfinishedPeriodWarning(result.WarningText))
                SetMergeWarning(result.WarningText);
            if (!result.IsSuccess && !IsExpectedEmptyUnfinishedPeriodWarning(result.Message))
                SetMergeWarning(result.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            mergeInProgress = false;
            SetBusy(false);
        }

        if (!operationToken.IsCancellationRequested)
            StartReachabilityMerge(typeValue, cause);
    }

    private void StartReachabilityMerge(
        string typeValue,
        MedulaPrescriptionListRefreshCause cause)
    {
        if (ApplicationSettingsStore.Current.OfflinePrescriptionList
            || !MedulaPrescriptionListRefreshPolicy.RequiresReachabilityRefresh(cause))
            return;

        reachabilityMergeCancellation?.Cancel();
        reachabilityMergeCancellation?.Dispose();
        reachabilityMergeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        reachabilityTypeValue = typeValue;
        var revision = ++reachabilityMergeRevision;
        _ = RunReachabilityMergeAsync(typeValue, revision, reachabilityMergeCancellation.Token);
    }

    private async Task RunReachabilityMergeAsync(
        string typeValue,
        int revision,
        CancellationToken cancellationToken)
    {
        SetReachabilityMergeState(true);
        SetMergeWarning(null);
        UpdateMergeProgress("Medula'da erişilebilir dönemler arka planda denetleniyor…");
        try
        {
            var result = await service.SearchReachablePrescriptionsAsync(typeValue, cancellationToken);
            if (revision != reachabilityMergeRevision) return;
            reachableRows = result.Prescriptions;
            if (CurrentTypeValueEquals(typeValue))
            {
                ReplaceDatabaseRows(ApplyMedulaReachability(databaseRows, reachableRows));
                ConnectionText.Text = $"{databaseRows.Count} reçete · Medula erişilebilirlik denetimi tamamlandı";
            }
            if (!result.IsSuccess
                && !string.IsNullOrWhiteSpace(result.WarningText)
                && !IsExpectedEmptyUnfinishedPeriodWarning(result.WarningText))
                SetMergeWarning(result.WarningText);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (revision == reachabilityMergeRevision)
                SetReachabilityMergeState(false);
        }
    }

    private bool CurrentTypeValueEquals(string typeValue) =>
        GroupComboBox.SelectedValue is string groupKey
        && ReturnStatusComboBox.SelectedValue is string returnStatusKey
        && MedulaPrescriptionListTypes.TryResolve(groupKey, returnStatusKey, out var currentTypeValue)
        && string.Equals(currentTypeValue, typeValue, StringComparison.Ordinal);

    private static IReadOnlyList<MedulaPrescriptionListItem> ApplyMedulaReachability(
        IReadOnlyList<MedulaPrescriptionListItem> localRows,
        IEnumerable<MedulaPrescriptionListItem> reachableRows)
    {
        var reachableKeys = reachableRows
            .Select(ListIdentityKey)
            .ToHashSet(StringComparer.Ordinal);
        return localRows.Select(item => item with
        {
            IsLiveOnMedula = !item.IsDeleted && !item.IsPermanentlyDeleted
                && reachableKeys.Contains(ListIdentityKey(item))
        }).ToArray();
    }

    private (DateOnly Start, DateOnly End)? ResolveSelectedRange()
    {
        if (PrescriptionDateModeCheckBox.IsChecked == true)
        {
            if (StartDatePicker.SelectedDate is not { } start || EndDatePicker.SelectedDate is not { } end)
            {
                SetWarning("Başlangıç ve bitiş tarihlerini seçin.");
                return null;
            }
            if (end.Date < start.Date)
            {
                SetWarning("Bitiş tarihi başlangıç tarihinden önce olamaz.");
                return null;
            }
            return (DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (DailyModeCheckBox.IsChecked == true)
        {
            var day = DayComboBox.SelectedItem is int value ? value : today.Day;
            var selected = new DateOnly(today.Year, today.Month, day);
            return (selected, selected);
        }
        var period = PeriodComboBox.SelectedValue as string ?? string.Empty;
        if (!DateOnly.TryParseExact(period, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            SetWarning("Geçerli bir Medula dönemi seçin.");
            return null;
        }
        var first = new DateOnly(month.Year, month.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    private async Task<IReadOnlyList<MedulaPrescriptionListItem>> MergeAndImportAsync(
        IReadOnlyList<MedulaPrescriptionListItem> localRows,
        IReadOnlyList<MedulaPrescriptionListItem> liveRows,
        string typeValue,
        CancellationToken cancellationToken)
    {
        var merged = new List<MedulaPrescriptionListItem>();
        var matchedUids = new HashSet<Guid>();
        var observedPositions = new List<(Guid PrescriptionUid, int PageNumber, int RowIndex)>();
        foreach (var live in liveRows)
        {
            var local = FindLocalMatch(live, localRows, matchedUids);
            if (local is not null)
            {
                if (local.PrescriptionUid is { } uid)
                {
                    matchedUids.Add(uid);
                    observedPositions.Add((uid, live.PageNumber, live.RowIndex));
                }
                if ((local.IsQrUnfinalized || live.IsQrUnfinalized)
                    && await RefreshUnfinalizedPrescriptionAsync(live, local, typeValue, cancellationToken) is { } refreshed)
                {
                    merged.Add(refreshed);
                    continue;
                }
                merged.Add(live with
                {
                    PrescriptionUid = local.PrescriptionUid,
                    RecordId = local.RecordId,
                    VersionNumber = local.VersionNumber,
                    IsLiveOnMedula = true,
                    IsStoredLocally = true,
                    LastUser = local.LastUser,
                    PatientTcIdentityNo = local.PatientTcIdentityNo,
                    LocalSnapshot = local.LocalSnapshot
                });
                continue;
            }

            var imported = await ImportLivePrescriptionAsync(live, typeValue, cancellationToken);
            merged.Add(imported ?? live with { IsLiveOnMedula = true });
            if (imported?.PrescriptionUid is { } importedUid) matchedUids.Add(importedUid);
        }
        await prescriptionRepository.UpdateMedulaListPositionsAsync(observedPositions, cancellationToken);
        merged.AddRange(localRows.Where(local => local.PrescriptionUid is not { } uid || !matchedUids.Contains(uid)));
        return merged
            .GroupBy(item => item.PrescriptionUid?.ToString() ?? $"live:{ListIdentityKey(item)}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.IsLiveOnMedula).ThenByDescending(item => item.VersionNumber).First())
            .OrderBy(item => (ParseListDate(item.MedulaFilterDate) ?? ParseListDate(item.PrescriptionDate)) is null)
            .ThenByDescending(item => ParseListDate(item.MedulaFilterDate) ?? ParseListDate(item.PrescriptionDate))
            .ThenByDescending(item => item.PageNumber > 0 ? item.PageNumber : int.MinValue)
            .ThenByDescending(item => item.PageNumber > 0 ? item.RowIndex : int.MinValue)
            .ThenByDescending(item => item.RecordId ?? int.MinValue)
            .ToArray();
    }

    private async Task<MedulaPrescriptionListItem?> ImportLivePrescriptionAsync(
        MedulaPrescriptionListItem item,
        string typeValue,
        CancellationToken cancellationToken)
    {
        try
        {
            UpdateMergeProgress($"{item.PrescriptionNumber} yerel geçmişe aktarılıyor…");
            var snapshot = await service.OpenPrescriptionFromListAsync(item, cancellationToken);
            if (snapshot is null) return null;
            var record = MedulaPrescriptionPersistenceMapper.FromSnapshot(
                snapshot,
                importedFromMedula: true,
                savedByUserId: AutonomousUser);
            var inserted = await prescriptionRepository.InsertVersionAsync(record, cancellationToken);
            return item with
            {
                PrescriptionUid = inserted.PrescriptionUid,
                RecordId = inserted.RecordId,
                VersionNumber = inserted.VersionNumber,
                IsLiveOnMedula = true,
                IsStoredLocally = true,
                LastUser = AutonomousUser,
                LocalSnapshot = snapshot with
                {
                    PrescriptionUid = inserted.PrescriptionUid,
                    VersionNumber = inserted.VersionNumber,
                    StoredFilterDate = record.MedulaFilterDate
                }
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetWarning($"{item.PrescriptionNumber} yerel geçmişe aktarılamadı: {exception.Message}");
            return null;
        }
    }

    private async Task<MedulaPrescriptionListItem?> RefreshUnfinalizedPrescriptionAsync(
        MedulaPrescriptionListItem live,
        MedulaPrescriptionListItem local,
        string typeValue,
        CancellationToken cancellationToken)
    {
        if (local.PrescriptionUid is not { } prescriptionUid) return null;
        try
        {
            UpdateMergeProgress($"{live.PrescriptionNumber} numaralı tamamlanmamış reçete denetleniyor…");
            var snapshot = await service.OpenPrescriptionFromListAsync(live, cancellationToken);
            if (snapshot is null) return null;
            snapshot = snapshot with
            {
                PrescriptionUid = prescriptionUid,
                VersionNumber = local.VersionNumber,
                StoredFilterDate = ParseListDate(local.MedulaFilterDate)
            };
            var record = MedulaPrescriptionPersistenceMapper.FromSnapshot(
                    snapshot,
                    importedFromMedula: true,
                    savedByUserId: AutonomousUser)
                with { MedulaTypeValue = typeValue };
            var inserted = await prescriptionRepository.InsertVersionIfChangedAsync(record, cancellationToken);
            return live with
            {
                PrescriptionUid = prescriptionUid,
                RecordId = inserted.RecordId,
                VersionNumber = inserted.VersionNumber,
                IsLiveOnMedula = true,
                IsStoredLocally = true,
                IsDeleted = false,
                LastUser = inserted.WasWritten ? AutonomousUser : local.LastUser,
                LocalSnapshot = snapshot with { VersionNumber = inserted.VersionNumber }
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetWarning($"{live.PrescriptionNumber} numaralı tamamlanmamış reçete güncellenemedi: {exception.Message}");
            return null;
        }
    }

    private static MedulaPrescriptionListItem? FindLocalMatch(
        MedulaPrescriptionListItem live,
        IEnumerable<MedulaPrescriptionListItem> localRows,
        ISet<Guid>? excludedUids = null)
    {
        var candidates = localRows.Where(local =>
                (local.PrescriptionUid is not { } uid || excludedUids?.Contains(uid) != true)
                && local.PrescriptionNumber.Trim().Equals(live.PrescriptionNumber.Trim(), StringComparison.OrdinalIgnoreCase)
                && Normalize(local.PatientName).Equals(Normalize(live.PatientName), StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0) return null;
        var livePrescriptionDate = ParseListDate(live.PrescriptionDate);
        var samePrescriptionDate = candidates.Where(candidate =>
                livePrescriptionDate is not null && ParseListDate(candidate.PrescriptionDate) == livePrescriptionDate)
            .ToArray();
        if (livePrescriptionDate is not null && samePrescriptionDate.Length == 0) return null;
        if (samePrescriptionDate.Length == 1) return samePrescriptionDate[0];
        if (samePrescriptionDate.Length > 1) candidates = samePrescriptionDate;
        if (candidates.Length == 1) return candidates[0];
        var liveFilterDate = ParseListDate(live.MedulaFilterDate);
        return candidates.OrderByDescending(candidate =>
                liveFilterDate is not null && ParseListDate(candidate.MedulaFilterDate) == liveFilterDate)
            .ThenByDescending(candidate => candidate.VersionNumber)
            .FirstOrDefault();
    }

    private static MedulaPrescriptionListItem? FindPatientQueryLocalMatch(
        MedulaPrescriptionListItem active,
        string patientTc,
        IEnumerable<MedulaPrescriptionListItem> localRows)
    {
        var prescriptionDate = ParseListDate(active.PrescriptionDate);
        return localRows.Where(local =>
                local.PatientTcIdentityNo.Equals(patientTc, StringComparison.Ordinal)
                && local.PrescriptionNumber.Trim().Equals(active.PrescriptionNumber.Trim(), StringComparison.OrdinalIgnoreCase)
                && (prescriptionDate is null || ParseListDate(local.PrescriptionDate) == prescriptionDate))
            .OrderByDescending(local => local.VersionNumber)
            .FirstOrDefault();
    }

    private static string ListIdentityKey(MedulaPrescriptionListItem item) => string.Join('|',
        item.PrescriptionNumber.Trim(), ParseListDate(item.PrescriptionDate)?.ToString("yyyyMMdd") ?? string.Empty,
        Normalize(item.PatientName));

    private static DateOnly? ParseListDate(string value) => DateOnly.TryParseExact(value.Trim(),
        ["dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private void ReplaceDatabaseRows(IEnumerable<MedulaPrescriptionListItem> rows)
    {
        databaseRows = AssignMedulaOrderKeys(rows);
        RebuildRows();
    }

    private static IReadOnlyList<MedulaPrescriptionListItem> AssignMedulaOrderKeys(
        IEnumerable<MedulaPrescriptionListItem> source)
    {
        var rows = source.ToArray();
        var keys = Enumerable.Repeat(double.NaN, rows.Length).ToArray();
        var indexedRows = rows.Select((item, index) => (Item: item, Index: index));
        foreach (var group in indexedRows.GroupBy(entry => (
                     Date: ParseListDate(entry.Item.MedulaFilterDate)
                         ?? ParseListDate(entry.Item.PrescriptionDate),
                     entry.Item.TypeValue)))
        {
            var ordered = group
                .OrderBy(entry => entry.Item.RecordId is null)
                .ThenBy(entry => entry.Item.RecordId ?? int.MaxValue)
                .ThenBy(entry => entry.Item.PageNumber)
                .ThenBy(entry => entry.Item.RowIndex)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index].Item;
                if (item.PageNumber > 0)
                    keys[ordered[index].Index] = (item.PageNumber - 1) * 1000d + item.RowIndex;
            }

            var cursor = 0;
            while (cursor < ordered.Length)
            {
                if (!double.IsNaN(keys[ordered[cursor].Index]))
                {
                    cursor++;
                    continue;
                }

                var start = cursor;
                while (cursor < ordered.Length && double.IsNaN(keys[ordered[cursor].Index])) cursor++;
                var count = cursor - start;
                var previous = start > 0 ? keys[ordered[start - 1].Index] : double.NaN;
                var next = cursor < ordered.Length ? keys[ordered[cursor].Index] : double.NaN;
                for (var offset = 0; offset < count; offset++)
                {
                    var key = !double.IsNaN(previous) && !double.IsNaN(next)
                        ? previous + (next - previous) * (offset + 1d) / (count + 1d)
                        : !double.IsNaN(previous)
                            ? previous + offset + 1d
                            : !double.IsNaN(next)
                                ? next - (count - offset)
                                : ordered[start + offset].Item.RecordId ?? start + offset;
                    keys[ordered[start + offset].Index] = key;
                }
            }
        }

        return rows.Select((item, index) => item with { MedulaOrderKey = keys[index] }).ToArray();
    }

    private void RebuildRows()
    {
        Prescriptions.Clear();
        var hasPatientTc = ResolvePatientTcFilter() is not null;
        foreach (var row in activePatientRows)
            Prescriptions.Add(row with { SectionTitle = "Hastanın Aktif Reçeteleri" });
        foreach (var row in databaseRows)
            Prescriptions.Add(row with
            {
                SectionTitle = hasPatientTc ? "Filtrelere göre veritabanındaki reçeteler" : string.Empty
            });
        if (PrescriptionView is ListCollectionView listView)
        {
            listView.GroupDescriptions.Clear();
            if (hasPatientTc)
                listView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MedulaPrescriptionListItem.SectionTitle)));
        }
        PrescriptionView.Refresh();
        ApplyMedulaDateSeparators();
        UpdateListSummary();
    }

    private void OnPrescriptionGridLeftClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not MedulaPrescriptionListItem item) return;
        var drugs = item.LocalSnapshot?.Drugs ?? [];
        PrescriptionDetailTitle.Text = $"Reçete İçeriği · {item.PrescriptionNumber} · {item.PatientName}";
        PrescriptionDrugDetailGrid.ItemsSource = drugs;
        PrescriptionDetailPanel.Visibility = Visibility.Visible;
    }

    private void OnPrescriptionGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = e.Column.SortDirection != ListSortDirection.Ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;
        foreach (var column in PrescriptionGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        prescriptionSortMember = e.Column.SortMemberPath;
        if (PrescriptionView is ListCollectionView listView)
            listView.CustomSort = new PrescriptionRowComparer(
                e.Column.SortMemberPath,
                direction,
                useMedulaPositionTieBreak: e.Column.SortMemberPath ==
                    nameof(MedulaPrescriptionListItem.MedulaFilterDate));
        ApplyMedulaDateSeparators();
        UpdateListSummary();
    }

    private void ApplyMedulaDateSeparators()
    {
        foreach (var item in Prescriptions)
        {
            item.ShowMedulaDateSeparator = false;
            item.MedulaDateSeparatorLabel = string.Empty;
            item.MedulaDateSeparatorTooltip = string.Empty;
        }

        if (prescriptionSortMember != nameof(MedulaPrescriptionListItem.MedulaFilterDate))
        {
            PrescriptionGrid.Items.Refresh();
            return;
        }

        var visibleRows = PrescriptionView.Cast<MedulaPrescriptionListItem>().ToArray();
        for (var index = 0; index + 1 < visibleRows.Length; index++)
        {
            var current = visibleRows[index];
            var next = visibleRows[index + 1];
            if (!string.Equals(current.SectionTitle, next.SectionTitle, StringComparison.Ordinal)) continue;
            var currentDate = ParseListDate(current.MedulaFilterDate) ?? ParseListDate(current.PrescriptionDate);
            var nextDate = ParseListDate(next.MedulaFilterDate) ?? ParseListDate(next.PrescriptionDate);
            if (currentDate is null || nextDate is null || currentDate == nextDate) continue;

            current.ShowMedulaDateSeparator = true;
            current.MedulaDateSeparatorLabel = nextDate.Value.Day.ToString(CultureInfo.InvariantCulture);
            current.MedulaDateSeparatorTooltip = nextDate.Value.ToString("dd MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        }
        PrescriptionGrid.Items.Refresh();
    }

    private async void OnPrescriptionRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not MedulaPrescriptionListItem item) return;
        await OpenPrescriptionAsync(item);
    }

    private void OnPrescriptionGridRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        PrescriptionGrid.SelectedItem = row?.Item is MedulaPrescriptionListItem item ? item : null;
    }

    private void OnPrescriptionContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = PrescriptionGrid.InputHitTest(Mouse.GetPosition(PrescriptionGrid)) as DependencyObject;
        var item = e.CursorLeft < 0 && e.CursorTop < 0
            ? PrescriptionGrid.SelectedItem as MedulaPrescriptionListItem
            : FindAncestor<DataGridRow>(hit)?.Item as MedulaPrescriptionListItem;
        if (item is null)
        {
            e.Handled = true;
            return;
        }
        PrescriptionGrid.SelectedItem = item;
        ConfigureRelatedCardMenu(item);
        PermanentDeleteMenuItem.Visibility = (!item.IsLiveOnMedula || item.IsDeleted)
            && !item.IsPermanentlyDeleted
                ? Visibility.Visible
                : Visibility.Collapsed;
        PrescriptionHistoryMenuItem.IsEnabled = item.PrescriptionUid is not null && item.IsStoredLocally;
        var currentUserId = (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty;
        AdminDeleteMenuItem.Visibility = isAdmin
            || currentUserId.Equals("admin", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ConfigureRelatedCardMenu(MedulaPrescriptionListItem item)
    {
        GoToPatientMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(item.PatientTcIdentityNo);
        GoToDoctorMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(item.LocalSnapshot?.Page.FieldValues?.GetValueOrDefault("f:t43"));
        GoToFacilityMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(item.LocalSnapshot?.Page.FieldValues?.GetValueOrDefault("f:t33"));
    }

    private async void OnGoToRelatedCardClick(object sender, RoutedEventArgs e)
    {
        if (PrescriptionGrid.SelectedItem is not MedulaPrescriptionListItem item || sender is not MenuItem menu) return;
        var (definition, identity) = (menu.Tag as string) switch
        {
            "doctor" => (CardDefinitions.Doctors, item.LocalSnapshot?.Page.FieldValues?.GetValueOrDefault("f:t43")),
            "facility" => (CardDefinitions.Faculties, item.LocalSnapshot?.Page.FieldValues?.GetValueOrDefault("f:t33")),
            _ => (CardDefinitions.Patients, item.PatientTcIdentityNo)
        };
        try
        {
            var key = await new CardRepository().ResolveRelatedCardKeyAsync(definition.Kind, identity ?? "", lifetime.Token);
            if (key is null) { SetWarning("The linked record is not available locally."); return; }
            TabRequested?.Invoke(new ModuleTabRequest(
                $"{definition.ModuleId}.edit.{key}",
                pharmacyApp.Localization.UiText.Translate(definition.SingularName),
                () => new CardEditorView(definition, key, null, () => { }, startEditing: false), 1180, 760));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { SetWarning($"Could not open the linked record: {exception.Message}"); }
    }

    private async void OnOpenPrescriptionMenuClick(object sender, RoutedEventArgs e)
    {
        if (PrescriptionGrid.SelectedItem is MedulaPrescriptionListItem item)
            await OpenPrescriptionAsync(item);
    }

    private async void OnOpenPrescriptionHistoryMenuClick(object sender, RoutedEventArgs e)
    {
        if (PrescriptionGrid.SelectedItem is not MedulaPrescriptionListItem
            { PrescriptionUid: { } prescriptionUid } item) return;
        await OpenPrescriptionHistoryAsync(prescriptionUid, item.PrescriptionNumber, reload: true);
    }

    private async Task OpenPrescriptionHistoryAsync(Guid prescriptionUid, string prescriptionNumber, bool reload)
    {
        try
        {
            if (reload || loadedHistoryPrescriptionUid != prescriptionUid)
            {
                loadedHistoryEntries = await prescriptionRepository.LoadHistoryAsync(
                    prescriptionUid, lifetime.Token);
                loadedHistoryPrescriptionUid = prescriptionUid;
            }
            HistoryTitleText.Text = string.IsNullOrWhiteSpace(prescriptionNumber)
                ? "Reçete Değişiklik Geçmişi"
                : $"{prescriptionNumber} · Reçete Değişiklik Geçmişi";
            HistoryStartDatePicker.SelectedDate = null;
            HistoryEndDatePicker.SelectedDate = null;
            RefreshPrescriptionHistoryRows();
            PrescriptionHistoryPopup.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetWarning($"Reçete değişiklik geçmişi yüklenemedi: {exception.Message}");
        }
    }

    private void RefreshPrescriptionHistoryRows()
    {
        var start = HistoryStartDatePicker.SelectedDate?.Date;
        var end = HistoryEndDatePicker.SelectedDate?.Date;
        var visible = loadedHistoryEntries.Where(entry =>
            (start is null || entry.UpdateDateTime.Date >= start.Value)
            && (end is null || entry.UpdateDateTime.Date <= end.Value));
        PrescriptionHistoryEntries.Clear();
        foreach (var entry in visible) PrescriptionHistoryEntries.Add(entry);
        HistoryCountText.Text = $"{PrescriptionHistoryEntries.Count} sürüm · En yeni kayıt üstte";
    }

    private void OnHistoryDateFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        RefreshPrescriptionHistoryRows();
    }

    private void OnClearHistoryDateFilterClick(object sender, RoutedEventArgs e)
    {
        HistoryStartDatePicker.SelectedDate = null;
        HistoryEndDatePicker.SelectedDate = null;
        RefreshPrescriptionHistoryRows();
    }

    private void OnClosePrescriptionHistoryClick(object sender, RoutedEventArgs e) =>
        PrescriptionHistoryPopup.IsOpen = false;

    private void OnPrescriptionHistoryRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PrescriptionHistoryGrid.SelectedItem is not MedulaPrescriptionHistoryEntry entry) return;
        var prescriptionNumber = entry.Snapshot.Page.FieldValues?
            .GetValueOrDefault("medula:prescription-no") ?? string.Empty;
        PrescriptionHistoryPopup.IsOpen = false;
        TabRequested?.Invoke(new ModuleTabRequest(
            $"medula.prescription-history.{entry.PrescriptionUid:N}.{entry.VersionNumber}",
            $"Reçete {prescriptionNumber} · Sürüm {entry.VersionNumber}",
            () => new MedulaPrescriptionEntryView(
                entry.Snapshot,
                () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshPrescriptionHistoryRows();
                    PrescriptionHistoryPopup.IsOpen = true;
                }), System.Windows.Threading.DispatcherPriority.ContextIdle)),
            1180,
            680,
            CreatePrescriptionIcon));
    }

    private async void OnPermanentDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (PrescriptionGrid.SelectedItem is not MedulaPrescriptionListItem
            { PrescriptionUid: { } prescriptionUid } item
            || item.IsLiveOnMedula || item.IsPermanentlyDeleted) return;
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                $"{item.PrescriptionNumber} numaralı reçete aktif reçete listesinden kalıcı olarak kaldırılacak. Devam edilsin mi?",
                "Reçeteyi Kalıcı Olarak Sil",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true, "Reçete kalıcı silme günlüğüne taşınıyor…");
        try
        {
            var userId = (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty;
            if (!await prescriptionRepository.PermanentlyDeleteAsync(prescriptionUid, userId, lifetime.Token))
                SetWarning("Reçete veritabanında bulunamadı.");
            else
            {
                SetWarning(null);
                await ReloadLocalRowsAsync();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetWarning($"Reçete kalıcı olarak silinemedi: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnAdminDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (PrescriptionGrid.SelectedItem is not MedulaPrescriptionListItem
            { PrescriptionUid: { } prescriptionUid } item) return;
        var currentUserId = (Application.Current as App)?.CurrentUser?.UserId ?? string.Empty;
        try
        {
            isAdmin = await prescriptionRepository.IsAdminAsync(currentUserId, lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetWarning($"Admin yetkisi denetlenemedi: {exception.Message}");
            return;
        }
        if (!isAdmin)
        {
            SetWarning("Bu işlem yalnızca admin yetkisine sahip kullanıcılar tarafından yapılabilir.");
            return;
        }
        if (pharmacyApp.Localization.EnglishMessageBox.Show(
                $"{item.PrescriptionNumber} numaralı reçete ve tüm geçmişi geri alınamayacak biçimde silinecek. Devam edilsin mi?",
                "Admin Reçeteyi Sil",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
        SetBusy(true, "Reçete ve tüm geçmişi siliniyor…");
        try
        {
            if (!await prescriptionRepository.AdminDeleteAsync(prescriptionUid, lifetime.Token))
                SetWarning("Reçete veya geçmiş kaydı bulunamadı.");
            else
            {
                SetWarning(null);
                await ReloadLocalRowsAsync();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetWarning($"Admin reçete silme işlemi tamamlanamadı: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OpenPrescriptionAsync(MedulaPrescriptionListItem item)
    {
        var openLocalDuringMerge = busy && !item.IsLiveOnMedula && item.LocalSnapshot is not null;
        if (busy && !openLocalDuringMerge) return;
        PrescriptionGrid.SelectedItem = item;
        if (!openLocalDuringMerge)
            SetBusy(true, $"{item.PrescriptionNumber} numaralı reçete açılıyor…");
        SetWarning(null);
        try
        {
            var snapshot = item.IsLiveOnMedula
                ? await service.OpenPrescriptionFromListAsync(item, lifetime.Token)
                : item.LocalSnapshot;
            if (snapshot is null)
            {
                SetWarning("Seçilen reçete Medula'da açılamadı.");
                return;
            }
            snapshot = snapshot with { SourceListItem = snapshot.SourceListItem ?? item };
            if (item.PrescriptionUid is { } uid)
                snapshot = snapshot with
                {
                    PrescriptionUid = uid,
                    VersionNumber = item.VersionNumber,
                    StoredFilterDate = ParseListDate(item.MedulaFilterDate)
                };
            var safeNumber = item.PrescriptionUid?.ToString("N")
                ?? new string(item.PrescriptionNumber.Where(char.IsLetterOrDigit).ToArray());
            TabRequested?.Invoke(new ModuleTabRequest(
                $"medula.prescription-view.{safeNumber}",
                $"Reçete {item.PrescriptionNumber}",
                () => new MedulaPrescriptionEntryView(snapshot),
                1180,
                680,
                CreatePrescriptionIcon));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!openLocalDuringMerge) SetBusy(false);
        }
    }

    private async void OnFilterModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || suppressFilterModeEvents) return;
        suppressFilterModeEvents = true;
        var medula = ReferenceEquals(sender, MedulaModeCheckBox) && MedulaModeCheckBox.IsChecked == true;
        if (ReferenceEquals(sender, PrescriptionDateModeCheckBox) && PrescriptionDateModeCheckBox.IsChecked == false)
            medula = true;
        PrescriptionDateModeCheckBox.IsChecked = !medula;
        MedulaModeCheckBox.IsChecked = medula;
        DateFilterPanel.Visibility = medula ? Visibility.Collapsed : Visibility.Visible;
        MedulaFilterPanel.Visibility = medula ? Visibility.Visible : Visibility.Collapsed;
        suppressFilterModeEvents = false;
        if (medula && !ApplicationSettingsStore.Current.OfflinePrescriptionList)
            await EnsurePeriodOptionsAsync();
        ScheduleFilterRefresh();
    }

    private void OnMedulaSubModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || suppressFilterModeEvents) return;
        suppressFilterModeEvents = true;
        var period = ReferenceEquals(sender, PeriodModeCheckBox) && PeriodModeCheckBox.IsChecked == true;
        if (ReferenceEquals(sender, DailyModeCheckBox) && DailyModeCheckBox.IsChecked == false)
            period = true;
        DailyModeCheckBox.IsChecked = !period;
        PeriodModeCheckBox.IsChecked = period;
        DailyFilterPanel.Visibility = period ? Visibility.Collapsed : Visibility.Visible;
        PeriodFilterPanel.Visibility = period ? Visibility.Visible : Visibility.Collapsed;
        suppressFilterModeEvents = false;
        ScheduleFilterRefresh();
    }

    private async Task EnsurePeriodOptionsAsync()
    {
        if (periodOptionsLoaded) return;
        periodOptionsLoaded = true;
        var selected = PeriodComboBox.SelectedValue as string;
        try
        {
            var options = await service.LoadPrescriptionListPeriodsAsync(lifetime.Token);
            if (options.Count == 0) return;
            PeriodComboBox.ItemsSource = options;
            PeriodComboBox.SelectedValue = options.Any(option => option.Value == selected) ? selected : options[0].Value;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateReturnStatuses(ReturnStatusComboBox.SelectedValue as string ?? MedulaPrescriptionListTypes.NoReturn);
        ScheduleFilterRefresh();
    }

    private void UpdateReturnStatuses(string selectedKey)
    {
        var groupKey = GroupComboBox.SelectedValue as string ?? "B Grubu";
        var options = MedulaPrescriptionListTypes.ReturnStatusesFor(groupKey);
        ReturnStatusComboBox.ItemsSource = options;
        ReturnStatusComboBox.SelectedValue = options.Any(option => option.Key == selectedKey)
            ? selectedKey
            : MedulaPrescriptionListTypes.NoReturn;
        UpdateReturnStatusValidation();
    }

    private void OnReturnStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateReturnStatusValidation();
        ScheduleFilterRefresh();
    }

    private void OnDateFilterChanged(object? sender, SelectionChangedEventArgs e) =>
        ScheduleFilterRefresh();

    private void OnMedulaFilterValueChanged(object sender, SelectionChangedEventArgs e) =>
        ScheduleFilterRefresh();

    private void ScheduleFilterRefresh()
    {
        if (!loadedOnce || !IsLoaded || suppressFilterModeEvents || suppressAutomaticRefresh) return;
        if (ReturnStatusComboBox.SelectedItem is MedulaPrescriptionReturnStatus { IsAvailable: false }) return;

        filterRefreshCancellation?.Cancel();
        filterRefreshCancellation?.Dispose();
        filterRefreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = RefreshAfterFilterChangeAsync(filterRefreshCancellation.Token);
    }

    private async Task RefreshAfterFilterChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            while (busy)
                await Task.Delay(80, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await SearchAsync(MedulaPrescriptionListRefreshCause.FilterChange);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateReturnStatusValidation()
    {
        if (ReturnStatusComboBox.SelectedItem is MedulaPrescriptionReturnStatus { IsAvailable: false })
        {
            StartInvalidReturnStatusFlash();
            return;
        }
        returnStatusBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        returnStatusBorderBrush.Color = Color.FromRgb(186, 201, 207);
        if (WarningText.Text.StartsWith("Seçilen reçete grubu", StringComparison.Ordinal)) SetWarning(null);
    }

    private void StartInvalidReturnStatusFlash() =>
        returnStatusBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            From = Color.FromRgb(186, 201, 207),
            To = Color.FromRgb(211, 47, 47),
            Duration = TimeSpan.FromMilliseconds(420),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });

    private async void OnPatientFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressPatientFilter) return;
        if (selectedPatient is not null
            && !PatientFilterBox.Text.Equals(selectedPatient.DisplayName, StringComparison.CurrentCultureIgnoreCase))
            selectedPatient = null;
        PrescriptionView.Refresh();
        ApplyMedulaDateSeparators();
        UpdateListSummary();

        patientSearchCancellation?.Cancel();
        patientSearchCancellation?.Dispose();
        patientSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = patientSearchCancellation.Token;
        var term = PatientFilterBox.Text.Trim();
        if (term.Length < 3)
        {
            PatientSuggestionPopup.IsOpen = false;
            PatientSuggestionList.ItemsSource = null;
            activePatientRows = [];
            RebuildRows();
            return;
        }
        try
        {
            await Task.Delay(220, token);
            var suggestions = await directoryRepository.SearchPatientsAsync(term, token);
            PatientSuggestionList.ItemsSource = suggestions;
            PatientSuggestionPopup.IsOpen = suggestions.Count > 0 && PatientFilterBox.IsKeyboardFocusWithin;
            if (ResolvePatientTcFilter() is not null)
                await RefreshActivePatientPrescriptionsAsync();
            else if (activePatientRows.Count > 0)
            {
                activePatientRows = [];
                RebuildRows();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetWarning($"Hasta önerileri yüklenemedi: {exception.Message}");
        }
    }

    private void OnPatientSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PatientSuggestionList.SelectedItem is PatientSuggestion patient) ApplyPatientSuggestion(patient);
    }

    private void OnPatientFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && PatientSuggestionList.Items.Count > 0)
        {
            PatientSuggestionPopup.IsOpen = true;
            PatientSuggestionList.SelectedIndex = e.Key == Key.Down ? 0 : PatientSuggestionList.Items.Count - 1;
            PatientSuggestionList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) PatientSuggestionPopup.IsOpen = false;
    }

    private void OnPatientSuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            PatientSuggestionList.SelectedIndex = Math.Clamp(
                PatientSuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1),
                0, PatientSuggestionList.Items.Count - 1);
            PatientSuggestionList.ScrollIntoView(PatientSuggestionList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && PatientSuggestionList.SelectedItem is PatientSuggestion patient)
        {
            ApplyPatientSuggestion(patient);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PatientSuggestionPopup.IsOpen = false;
            PatientFilterBox.Focus();
        }
    }

    private void ApplyPatientSuggestion(PatientSuggestion patient)
    {
        selectedPatient = patient;
        suppressPatientFilter = true;
        PatientFilterBox.Text = patient.DisplayName;
        PatientFilterBox.CaretIndex = PatientFilterBox.Text.Length;
        suppressPatientFilter = false;
        PatientSuggestionPopup.IsOpen = false;
        PatientSuggestionList.SelectedItem = null;
        PrescriptionView.Refresh();
        ApplyMedulaDateSeparators();
        UpdateListSummary();
        _ = RefreshActivePatientPrescriptionsAsync();
    }

    private bool FilterPrescription(object value)
    {
        if (value is not MedulaPrescriptionListItem item) return false;
        if (item.IsPermanentlyDeleted && ShowPermanentlyDeletedCheckBox.IsChecked != true) return false;
        var patientText = PatientFilterBox.Text.Trim();
        var patientTc = ResolvePatientTcFilter();
        if (patientTc is not null)
        {
            var rowTc = item.PatientTcIdentityNo.Length > 0
                ? item.PatientTcIdentityNo
                : item.LocalSnapshot?.Page.FieldValues?.GetValueOrDefault("f:t18")?.Trim();
            if (!item.Source.Equals("patient-query", StringComparison.Ordinal)
                && !patientTc.Equals(rowTc, StringComparison.Ordinal)) return false;
        }
        else
        {
            var patientFilter = Normalize(selectedPatient?.DisplayName ?? patientText);
            if (patientFilter.Length > 0
                && !Normalize(item.PatientName).Contains(patientFilter, StringComparison.Ordinal)) return false;
        }

        var numberFilter = PrescriptionNumberFilterBox.Text.Trim();
        if (numberFilter.Length > 0
            && !item.PrescriptionNumber.Contains(numberFilter, StringComparison.CurrentCultureIgnoreCase)) return false;
        var typeFilter = (TypeFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        if (typeFilter.Length > 0 && !item.StatusCode.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) return false;
        var coverageFilter = Normalize(CoverageFilterBox.Text);
        if (coverageFilter.Length > 0
            && !Normalize(item.CoverageDisplay).Contains(coverageFilter, StringComparison.Ordinal)) return false;
        if (!DateMatches(LastTransactionStartPicker.SelectedDate, LastTransactionEndPicker.SelectedDate,
                ParseListDate(item.LastTransactionDate))) return false;
        if (!DateMatches(MedulaFilterStartPicker.SelectedDate, MedulaFilterEndPicker.SelectedDate,
                ParseListDate(item.MedulaFilterDate))) return false;
        var userFilter = Normalize(UserFilterBox.Text);
        if (userFilter.Length > 0 && !Normalize(item.LastUser).Contains(userFilter, StringComparison.Ordinal)) return false;
        var medicationFilter = Normalize(MedicationFilterBox.Text);
        if (medicationFilter.Length > 0 && !(item.LocalSnapshot?.Drugs.Any(drug =>
                Normalize(drug.Barcode).Contains(medicationFilter, StringComparison.Ordinal)
                || Normalize(drug.Name).Contains(medicationFilter, StringComparison.Ordinal)) ?? false)) return false;
        return true;
    }

    private static bool DateMatches(DateTime? start, DateTime? end, DateOnly? value)
    {
        if (start is null && end is null) return true;
        if (value is null) return false;
        return (start is null || value >= DateOnly.FromDateTime(start.Value))
            && (end is null || value <= DateOnly.FromDateTime(end.Value));
    }

    private string? ResolvePatientTcFilter()
    {
        if (selectedPatient?.TcIdentityNo is { Length: 11 } selectedTc && selectedTc.All(char.IsDigit))
            return selectedTc;
        var typed = PatientFilterBox.Text.Trim();
        return typed.Length == 11 && typed.All(char.IsDigit) ? typed : null;
    }

    private async Task RefreshActivePatientPrescriptionsAsync()
    {
        var tc = ResolvePatientTcFilter();
        if (tc is null || ApplicationSettingsStore.Current.OfflinePrescriptionList)
        {
            activePatientRows = [];
            RebuildRows();
            return;
        }

        patientQueryCancellation?.Cancel();
        patientQueryCancellation?.Dispose();
        patientQueryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = patientQueryCancellation.Token;
        try
        {
            var knownPatientRow = databaseRows.FirstOrDefault(row =>
                row.PatientTcIdentityNo.Equals(tc, StringComparison.Ordinal));
            var patientName = selectedPatient?.DisplayName ?? knownPatientRow?.PatientName ?? string.Empty;
            var result = await service.SearchActivePrescriptionsByPatientAsync(tc, patientName, token);
            activePatientRows = result.Prescriptions.Select(active =>
            {
                var local = FindPatientQueryLocalMatch(active, tc, databaseRows)
                    ?? FindLocalMatch(active, databaseRows);
                return local is null
                    ? active
                    : active with
                    {
                        PatientFirstName = local.PatientFirstName,
                        PatientLastName = local.PatientLastName,
                        Coverage = local.Coverage,
                        QrCodeStatus = local.QrCodeStatus,
                        StatusCode = local.StatusCode,
                        BarcodeStatus = local.BarcodeStatus,
                        MedicalReport = local.MedicalReport,
                        DocumentStatus = local.DocumentStatus,
                        TypeValue = local.TypeValue,
                        PrescriptionUid = local.PrescriptionUid,
                        RecordId = local.RecordId,
                        VersionNumber = local.VersionNumber,
                        IsStoredLocally = true,
                        LastUser = local.LastUser,
                        LocalSnapshot = local.LocalSnapshot
                    };
            }).ToArray();
            RebuildRows();
            if (!result.IsSuccess) SetWarning(result.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnLocalFilterChanged(object sender, RoutedEventArgs e)
    {
        PrescriptionView.Refresh();
        ApplyMedulaDateSeparators();
        UpdateListSummary();
    }

    private async void OnPermanentDeleteFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!loadedOnce || !IsLoaded || busy) return;
        SetBusy(true, "Yerel reçete geçmişi yenileniyor…");
        try
        {
            await ReloadLocalRowsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetWarning($"Kalıcı silinmiş reçeteler yüklenemedi: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadLocalRowsAsync()
    {
        var liveIdentities = databaseRows
            .Where(item => item.IsLiveOnMedula)
            .Select(ListIdentityKey)
            .ToHashSet(StringComparer.Ordinal);
        var range = ResolveSelectedRange();
        if (range is null) return;
        if (GroupComboBox.SelectedValue is not string groupKey
            || ReturnStatusComboBox.SelectedValue is not string returnStatusKey
            || !MedulaPrescriptionListTypes.TryResolve(groupKey, returnStatusKey, out var typeValue))
            return;
        var rows = await prescriptionRepository.LoadLatestAsync(
            range.Value.Start, range.Value.End, typeValue, lifetime.Token);
        if (ShowPermanentlyDeletedCheckBox.IsChecked == true)
            rows = rows.Concat(await prescriptionRepository.LoadPermanentlyDeletedAsync(
                range.Value.Start, range.Value.End, typeValue, lifetime.Token)).ToArray();
        rows = rows.Select(item => item with
        {
            IsLiveOnMedula = !item.IsDeleted && !item.IsPermanentlyDeleted
                && liveIdentities.Contains(ListIdentityKey(item))
        }).ToArray();
        activePatientRows = [];
        ReplaceDatabaseRows(rows);
    }

    private void OnDetailedFilterClick(object sender, RoutedEventArgs e)
    {
        DetailedFilterPanel.Visibility = DetailedFilterPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        DetailedFilterButton.Content = DetailedFilterPanel.Visibility == Visibility.Visible
            ? "Detaylı Filtreyi Kapat"
            : "Detaylı Filtre";
    }

    private static string Normalize(string value) => value.Trim().ToLower(TurkishCulture);

    private async void OnPeriodPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string period }) return;
        var today = DateTime.Today;
        suppressAutomaticRefresh = true;
        try
        {
            StartDatePicker.SelectedDate = period switch
            {
                "Week" => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
                "Month" => new DateTime(today.Year, today.Month, 1),
                "Year" => new DateTime(today.Year, 1, 1),
                _ => today
            };
            EndDatePicker.SelectedDate = today;
            CustomRangeBox.Text = string.Empty;
        }
        finally
        {
            suppressAutomaticRefresh = false;
        }
        CancelScheduledFilterRefresh();
        await SearchAsync(MedulaPrescriptionListRefreshCause.FilterChange);
    }

    private void OnCustomRangePreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var proposed = CustomRangeBox.Text.Remove(CustomRangeBox.SelectionStart, CustomRangeBox.SelectionLength)
            .Insert(CustomRangeBox.SelectionStart, e.Text);
        e.Handled = !int.TryParse(proposed, out var value) || value <= 0;
    }

    private void OnCustomRangePasting(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        if (!int.TryParse(text, out var value) || value <= 0) e.CancelCommand();
    }

    private async void OnCustomRangeLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        await ApplyCustomRangeAsync();

    private async void OnCustomRangeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyCustomRangeAsync();
    }

    private async Task ApplyCustomRangeAsync()
    {
        if (!int.TryParse(CustomRangeBox.Text, out var value) || value <= 0) return;
        var today = DateTime.Today;
        var unit = (CustomRangeUnit.SelectedItem as ComboBoxItem)?.Tag as string ?? "Day";
        suppressAutomaticRefresh = true;
        try
        {
            StartDatePicker.SelectedDate = unit switch
            {
                "Week" => today.AddDays(-(value * 7 - 1)),
                "Month" => today.AddMonths(-value).AddDays(1),
                "Year" => today.AddYears(-value).AddDays(1),
                _ => today.AddDays(-(value - 1))
            };
            EndDatePicker.SelectedDate = today;
        }
        finally
        {
            suppressAutomaticRefresh = false;
        }
        CancelScheduledFilterRefresh();
        await SearchAsync(MedulaPrescriptionListRefreshCause.FilterChange);
    }

    private static IReadOnlyList<MedulaPrescriptionPeriodOption> DefaultPeriodOptions()
    {
        var month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        return Enumerable.Range(0, 4).Select(offset => month.AddMonths(-offset))
            .Select(date => new MedulaPrescriptionPeriodOption(
                date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                date.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))))
            .ToArray();
    }

    private void UpdateListSummary()
    {
        EmptyText.Visibility = PrescriptionView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        var liveCount = Prescriptions.Count(item => item.IsLiveOnMedula);
        CountText.Text = $"{PrescriptionView.Cast<object>().Count()} reçete gösteriliyor · {liveCount} Medula'da canlı";
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return null;
    }

    private sealed class PrescriptionRowComparer(
        string property,
        ListSortDirection direction,
        bool useMedulaPositionTieBreak) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not MedulaPrescriptionListItem left || y is not MedulaPrescriptionListItem right)
                return 0;

            var sectionComparison = SectionRank(left.SectionTitle).CompareTo(SectionRank(right.SectionTitle));
            if (sectionComparison != 0) return sectionComparison;

            var leftValue = SortValue(left, property);
            var rightValue = SortValue(right, property);
            if (leftValue is null || rightValue is null)
                return leftValue is null ? rightValue is null ? 0 : 1 : -1;
            var result = CompareValues(leftValue, rightValue);
            if (result == 0 && useMedulaPositionTieBreak)
            {
                result = EffectiveMedulaOrder(left).CompareTo(EffectiveMedulaOrder(right));
                if (result == 0) result = (left.RecordId ?? int.MinValue).CompareTo(right.RecordId ?? int.MinValue);
            }
            return direction == ListSortDirection.Ascending ? result : -result;
        }

        private static double EffectiveMedulaOrder(MedulaPrescriptionListItem item) =>
            !double.IsNaN(item.MedulaOrderKey)
                ? item.MedulaOrderKey
                : item.PageNumber > 0
                    ? (item.PageNumber - 1) * 1000d + item.RowIndex
                    : item.RecordId ?? double.MinValue;

        private static int SectionRank(string section) => section.StartsWith(
            "Hastanın Aktif", StringComparison.CurrentCultureIgnoreCase) ? 0 : 1;

        private static object? SortValue(MedulaPrescriptionListItem item, string member) => member switch
        {
            nameof(MedulaPrescriptionListItem.LastTransactionDate) => ParseSortableDate(item.LastTransactionDate),
            nameof(MedulaPrescriptionListItem.MedulaFilterDate) =>
                ParseSortableDate(item.MedulaFilterDate) ?? ParseSortableDate(item.PrescriptionDate),
            nameof(MedulaPrescriptionListItem.PrescriptionDate) => ParseSortableDate(item.PrescriptionDate),
            nameof(MedulaPrescriptionListItem.PrescriptionNumber) => item.PrescriptionNumber,
            nameof(MedulaPrescriptionListItem.LastUser) => item.LastUser,
            nameof(MedulaPrescriptionListItem.PatientName) => item.PatientName,
            nameof(MedulaPrescriptionListItem.CoverageDisplay) => item.CoverageDisplay,
            nameof(MedulaPrescriptionListItem.QrCodeStatusDisplay) => item.QrCodeStatusDisplay,
            nameof(MedulaPrescriptionListItem.TypeDisplay) => item.TypeDisplay,
            nameof(MedulaPrescriptionListItem.BarcodeStatus) => item.BarcodeStatus,
            nameof(MedulaPrescriptionListItem.MedicalReport) => item.MedicalReport,
            nameof(MedulaPrescriptionListItem.DocumentStatus) => item.DocumentStatus,
            _ => item.PrescriptionNumber
        };

        private static DateTime? ParseSortableDate(string value) => DateTime.TryParse(
            value.Trim(), TurkishCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;

        private static int CompareValues(object left, object right) => left switch
        {
            IComparable comparable when left.GetType() == right.GetType() => comparable.CompareTo(right),
            _ => TurkishCulture.CompareInfo.Compare(
                Convert.ToString(left), Convert.ToString(right), CompareOptions.IgnoreCase)
        };
    }

    private void SetBusy(bool value, string? message = null, bool blockContent = true)
    {
        busy = value;
        ReportBackgroundWorkState();
        BusyLayer.Visibility = value && blockContent ? Visibility.Visible : Visibility.Collapsed;
        MergeProgressPanel.Visibility = value && !blockContent || reachabilityMergeInProgress
            || MergeWarningText.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            BusyText.Text = message;
            MergeProgressText.Text = message;
        }
        RefreshButton.IsEnabled = !value;
        GroupComboBox.IsEnabled = !value;
        ReturnStatusComboBox.IsEnabled = !value;
    }

    private void SetReachabilityMergeState(bool value)
    {
        reachabilityMergeInProgress = value;
        ReportBackgroundWorkState();
        MergeProgressPanel.Visibility = value || busy && BusyLayer.Visibility != Visibility.Visible
            || MergeWarningText.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (!value && !busy && MergeWarningText.Visibility != Visibility.Visible)
            MergeProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void ReportBackgroundWorkState()
    {
        var current = IsBackgroundWorkRunning;
        if (current == lastReportedBackgroundWork) return;
        lastReportedBackgroundWork = current;
        BackgroundWorkStateChanged?.Invoke(current);
    }

    private void UpdateMergeProgress(string message)
    {
        BusyText.Text = message;
        MergeProgressText.Text = message;
    }

    private void SetWarning(string? warning) => SetMessage(warning, MedulaMessageSeverity.Error);

    private void SetMessage(string? message, MedulaMessageSeverity severity)
    {
        message = message?.Trim();
        WarningText.Text = message ?? string.Empty;
        var (background, border, foreground) = severity switch
        {
            MedulaMessageSeverity.Success => (Color.FromRgb(235, 248, 239), Color.FromRgb(144, 203, 160), Color.FromRgb(31, 112, 61)),
            MedulaMessageSeverity.Notice => (Color.FromRgb(255, 248, 224), Color.FromRgb(230, 197, 102), Color.FromRgb(126, 91, 16)),
            _ => (Color.FromRgb(255, 241, 231), Color.FromRgb(237, 180, 132), Color.FromRgb(148, 61, 22))
        };
        WarningBanner.Background = new SolidColorBrush(background);
        WarningBanner.BorderBrush = new SolidColorBrush(border);
        WarningText.Foreground = new SolidColorBrush(foreground);
        WarningBanner.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetMergeWarning(string? warning)
    {
        MergeWarningText.Text = warning ?? string.Empty;
        MergeWarningText.Visibility = string.IsNullOrWhiteSpace(warning)
            ? Visibility.Collapsed
            : Visibility.Visible;
        MergeProgressPanel.Visibility = mergeInProgress || reachabilityMergeInProgress
            || MergeWarningText.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool IsExpectedEmptyUnfinishedPeriodWarning(string? warning) =>
        string.Equals(
            warning?.Trim().TrimEnd('.'),
            "Bu döneme ait sonlandırılmamış reçete bulunamadı",
            StringComparison.CurrentCultureIgnoreCase);

    private static FrameworkElement CreatePrescriptionIcon() => new Border
    {
        Width = 16, Height = 16, Background = new SolidColorBrush(Color.FromRgb(35, 94, 115)),
        CornerRadius = new CornerRadius(0),
        Child = new TextBlock
        {
            Text = "R", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };
}
