using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace pharmacyApp.Modules.StockMovements;

public partial class UnnotifiedItsMovementsView : UserControl
{
    private static readonly Brush ErrorBrush = BrushFrom(179, 58, 50);
    private static readonly Brush ErrorBackground = BrushFrom(251, 232, 230);
    private static readonly Brush WarningBrush = BrushFrom(149, 99, 21);
    private static readonly Brush WarningBackground = BrushFrom(255, 248, 231);
    private static readonly Brush SuccessBrush = BrushFrom(22, 125, 78);
    private static readonly Brush SuccessBackground = BrushFrom(229, 244, 236);
    private static readonly Brush BalancedBrush = BrushFrom(15, 118, 110);
    private static readonly Brush BalancedBackground = BrushFrom(231, 242, 242);

    private readonly UnnotifiedItsMovementRepository repository = new();
    private IReadOnlyList<MovementGroupViewModel> allGroups = [];
    private bool busy;

    public UnnotifiedItsMovementsView()
    {
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync(string? completionMessage = null)
    {
        if (busy) return;
        try
        {
            SetBusy(true, "İTS bildirimsiz hareketler yükleniyor...");
            var records = await repository.LoadHistoryAsync(CancellationToken.None);
            allGroups = BuildGroups(records);
            UpdateCounters();
            ApplyFilter();
            StatusText.Text = completionMessage ??
                "Bekleyen hareketi düzeltmek için karekod kartına sağ tıklayın. Karşıt hareketler geçmişte görünmeye devam eder.";
            StatusText.Foreground = completionMessage is null ? BrushFrom(101, 113, 126) : SuccessBrush;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Hareketler yüklenemedi: {exception.Message}";
            StatusText.Foreground = ErrorBrush;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (GroupsList is null) return;
        var search = SearchBox.Text.Trim();
        var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var filtered = allGroups.Where(group =>
                (search.Length == 0 || group.Matches(search)) &&
                (status == "All" || group.Cases.Any(item => item.Status.ToString() == status)))
            .ToArray();
        GroupsList.ItemsSource = filtered;
        EmptyStatePanel.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText.Text = $"{filtered.Length} karekod · {filtered.Sum(group => group.Cases.Count)} hareket grubu";
    }

    private void UpdateCounters()
    {
        var cases = allGroups.SelectMany(group => group.Cases).ToArray();
        PendingCountText.Text = cases.Count(item => item.Status == MovementCaseStatus.Pending).ToString();
        ReversedCountText.Text = cases.Count(item => item.Status == MovementCaseStatus.Reversed).ToString();
        NotifiedCountText.Text = cases.Count(item => item.Status == MovementCaseStatus.Notified).ToString();
        TotalCountText.Text = allGroups.Count.ToString();
    }

    private void OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            e.Handled = true;
            return;
        }
        item.IsSelected = true;
        item.Focus();
        if (item.DataContext is MovementGroupViewModel group)
        {
            var correctItem = new MenuItem
            {
                Header = "İTS Bildirimi Yap",
                IsEnabled = group.CanCorrect,
                DataContext = group
            };
            correctItem.Click += OnCorrectClicked;
            item.ContextMenu = new ContextMenu { Items = { correctItem } };
        }
    }

    private async void OnCorrectClicked(object sender, RoutedEventArgs e)
    {
        var group = (sender as MenuItem)?.DataContext as MovementGroupViewModel
                    ?? GroupsList.SelectedItem as MovementGroupViewModel;
        if (busy || group is not { CanCorrect: true } || group.PendingCase is not { } pendingCase) return;
        try
        {
            SetBusy(true, $"{group.QrCode} için İTS bildirimi gönderiliyor...");
            var user = (Application.Current as App)?.CurrentUser?.UserId ?? "admin";
            var result = await new StockItsNotificationSender().SendAsync(
                pendingCase.Left.NotificationKind,
                [group.QrCode],
                user,
                CancellationToken.None);
            await repository.MarkCorrectedAsync(
                [pendingCase.Left.RecordId], result.NotificationId, user, CancellationToken.None);
            await new StockItsNotificationSender().MarkLocalCompletedAsync(
                result.OperationId, null, CancellationToken.None);
            SetBusy(false);
            await ReloadAsync($"İTS bildirimi tamamlandı: {result.NotificationId}.");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"İTS bildirimi tamamlanamadı: {exception.Message}";
            StatusText.Foreground = ErrorBrush;
            SetBusy(false);
        }
    }

    private void SetBusy(bool value, string? message = null)
    {
        busy = value;
        RefreshButton.IsEnabled = !value;
        SearchBox.IsEnabled = !value;
        StatusFilter.IsEnabled = !value;
        BusyOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) BusyText.Text = message;
    }

    private static IReadOnlyList<MovementGroupViewModel> BuildGroups(
        IReadOnlyList<UnnotifiedItsMovementRecord> records) => records
        .GroupBy(record => record.QrCode, StringComparer.Ordinal)
        .Select(group => MovementGroupViewModel.Create(group.Key, group.ToArray()))
        .OrderByDescending(group => group.HasPending)
        .ThenByDescending(group => group.LatestAt)
        .ToArray();

    private static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : null;
        }
        return null;
    }

    private enum MovementCaseStatus
    {
        Pending,
        Reversed,
        Notified
    }

    private sealed class MovementGroupViewModel : INotifyPropertyChanged
    {
        private bool isExpanded = true;

        private MovementGroupViewModel(string qrCode, IReadOnlyList<MovementCaseViewModel> cases)
        {
            QrCode = qrCode;
            Cases = cases;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string QrCode { get; }
        public IReadOnlyList<MovementCaseViewModel> Cases { get; }
        public MovementCaseViewModel? PendingCase => Cases.FirstOrDefault(item => item.Status == MovementCaseStatus.Pending);
        public bool HasPending => PendingCase is not null;
        public bool CanCorrect => HasPending;
        public DateTime LatestAt => Cases.Max(item => item.LatestAt);
        public int CaseCount => Cases.Count;
        public string CaseCountDisplay => $"{CaseCount} kayıt";
        public string ReferenceSummary
        {
            get
            {
                var references = Cases.SelectMany(item => item.References).Distinct(StringComparer.Ordinal).ToArray();
                return references.Length <= 3
                    ? string.Join(" · ", references)
                    : $"{string.Join(" · ", references.Take(3))} · +{references.Length - 3}";
            }
        }

        public MovementCaseStatus CurrentStatus => HasPending
            ? MovementCaseStatus.Pending
            : Cases.OrderByDescending(item => item.LatestAt).First().Status;
        public string StatusIcon => CurrentStatus switch
        {
            MovementCaseStatus.Pending => "!",
            MovementCaseStatus.Reversed => "⇄",
            _ => "✓"
        };
        public string StatusDisplay => CurrentStatus switch
        {
            MovementCaseStatus.Pending => "Bildirim bekliyor",
            MovementCaseStatus.Reversed => "Karşıt hareketle dengelendi",
            _ => "İTS bildirimi yapıldı"
        };
        public Brush StatusBrush => CurrentStatus switch
        {
            MovementCaseStatus.Pending => ErrorBrush,
            MovementCaseStatus.Reversed => BalancedBrush,
            _ => SuccessBrush
        };
        public Brush StatusBackground => CurrentStatus switch
        {
            MovementCaseStatus.Pending => ErrorBackground,
            MovementCaseStatus.Reversed => BalancedBackground,
            _ => SuccessBackground
        };

        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsVisibility)));
            }
        }
        public Visibility DetailsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

        public bool Matches(string search) =>
            QrCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            Cases.SelectMany(item => item.References)
                .Any(reference => reference.Contains(search, StringComparison.OrdinalIgnoreCase));

        public static MovementGroupViewModel Create(
            string qrCode, IReadOnlyList<UnnotifiedItsMovementRecord> records)
        {
            var byId = records.ToDictionary(record => record.RecordId);
            var visited = new HashSet<int>();
            var cases = new List<MovementCaseViewModel>();
            foreach (var record in records.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.RecordId))
            {
                if (!visited.Add(record.RecordId)) continue;
                if (record.PairedRecordId is int pairId && byId.TryGetValue(pairId, out var paired))
                {
                    visited.Add(paired.RecordId);
                    var ordered = new[] { record, paired }
                        .OrderBy(item => item.CreatedAt).ThenBy(item => item.RecordId).ToArray();
                    cases.Add(MovementCaseViewModel.Reversed(
                        MovementActionViewModel.From(ordered[0]),
                        MovementActionViewModel.From(ordered[1])));
                    continue;
                }

                var action = MovementActionViewModel.From(record);
                cases.Add(!record.IsCorrected
                    ? MovementCaseViewModel.Pending(action)
                    : MovementCaseViewModel.Notified(action, record));
            }
            return new MovementGroupViewModel(qrCode, cases.OrderByDescending(item => item.LatestAt).ToArray());
        }
    }

    private sealed record MovementCaseViewModel(
        MovementCaseStatus Status,
        MovementActionViewModel Left,
        MovementActionViewModel? Right,
        DateTime LatestAt,
        string CompletionDetail)
    {
        public IEnumerable<string> References => Right is null
            ? [Left.ReferenceNumber]
            : [Left.ReferenceNumber, Right.ReferenceNumber];
        public string ConnectorIcon => Status switch
        {
            MovementCaseStatus.Pending => "…",
            MovementCaseStatus.Reversed => "⇄",
            _ => "→"
        };
        public string ConnectorText => Status switch
        {
            MovementCaseStatus.Pending => "İTS bekliyor",
            MovementCaseStatus.Reversed => "Birbirini dengeledi",
            _ => "Bildirim yapıldı"
        };
        public Brush ConnectorBrush => Status switch
        {
            MovementCaseStatus.Pending => WarningBrush,
            MovementCaseStatus.Reversed => BalancedBrush,
            _ => SuccessBrush
        };
        public Visibility RightVisibility => Right is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PendingVisibility => Status == MovementCaseStatus.Pending ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NotifiedVisibility => Status == MovementCaseStatus.Notified ? Visibility.Visible : Visibility.Collapsed;

        public static MovementCaseViewModel Pending(MovementActionViewModel action) =>
            new(MovementCaseStatus.Pending, action, null, action.CreatedAt, string.Empty);
        public static MovementCaseViewModel Reversed(
            MovementActionViewModel original, MovementActionViewModel reverse) =>
            new(MovementCaseStatus.Reversed, original, reverse,
                original.CreatedAt > reverse.CreatedAt ? original.CreatedAt : reverse.CreatedAt, string.Empty);
        public static MovementCaseViewModel Notified(
            MovementActionViewModel action, UnnotifiedItsMovementRecord record) =>
            new(MovementCaseStatus.Notified, action, null, record.CorrectedAt ?? record.CreatedAt,
                $"Bildirim no: {record.CorrectionNotificationId ?? "—"}" +
                (record.CorrectedAt.HasValue ? $" · {record.CorrectedAt:dd.MM.yyyy HH:mm}" : string.Empty));
    }

    private sealed record MovementActionViewModel(
        int RecordId,
        string ReferenceNumber,
        StockItsNotificationKind NotificationKind,
        DateTime CreatedAt,
        string DirectionDisplay,
        string DirectionIcon,
        string NotificationDisplay,
        string ReferenceDisplay,
        string CreatedAtDisplay,
        string UserDisplay,
        Brush AccentBrush,
        Brush Background,
        Brush BorderBrush,
        Brush IconBackground)
    {
        public static MovementActionViewModel From(UnnotifiedItsMovementRecord record)
        {
            var isEntry = record.Direction == StockMovementDirection.In;
            var accent = isEntry ? SuccessBrush : ErrorBrush;
            var background = isEntry ? BrushFrom(244, 251, 247) : BrushFrom(255, 247, 246);
            var border = isEntry ? BrushFrom(198, 224, 210) : BrushFrom(235, 207, 203);
            return new MovementActionViewModel(
                record.RecordId,
                record.ReferenceNumber,
                record.NotificationKind,
                record.CreatedAt,
                isEntry ? "Giriş" : "Çıkış",
                isEntry ? "↓" : "↑",
                FormatNotification(record.NotificationKind),
                $"Referans: {record.ReferenceNumber}",
                record.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
                $"Kullanıcı: {record.CreatedBy}",
                accent,
                background,
                border,
                isEntry ? SuccessBackground : ErrorBackground);
        }

        private static string FormatNotification(StockItsNotificationKind kind) => kind switch
        {
            StockItsNotificationKind.Sale => "İTS Satış",
            StockItsNotificationKind.SaleCancellation => "İTS Satış İptal",
            StockItsNotificationKind.Acceptance => "İTS Mal Alım",
            _ => "İTS Bildirimi"
        };
    }

    private static SolidColorBrush BrushFrom(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));
}
