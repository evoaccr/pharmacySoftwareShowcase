using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace pharmacyApp.Modules.Invoices;

public partial class PartialInvoiceRefundView : UserControl
{
    private readonly PartialInvoiceRefundRepository repository = new();
    private readonly PartialInvoiceRefundCart cart = new();
    private readonly PartialInvoiceRefundLaunchOptions launchOptions;
    private CancellationTokenSource? operationCancellation;
    private PartialInvoiceRefundQrRow? selectedQr;

    public PartialInvoiceRefundView() : this(new PartialInvoiceRefundLaunchOptions())
    {
    }

    public PartialInvoiceRefundView(PartialInvoiceRefundLaunchOptions launchOptions)
    {
        this.launchOptions = launchOptions;
        InitializeComponent();
        if (launchOptions.SendItsNotifications)
            pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        RelatedRecordMenus.Attach(RefundGrid);
        DataContext = cart;
        UpdateCartState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        QrCodeInput.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        CancelAndDispose(ref operationCancellation);

    private async void OnAddItemClicked(object sender, RoutedEventArgs e) => await AddEnteredQrAsync();

    private async void OnQrCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddEnteredQrAsync();
    }

    private async Task AddEnteredQrAsync()
    {
        var code = QrCodeInput.Text.Trim();
        if (code.Length == 0 || operationCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        SetBusy(true, "Karekod ve bağlı fatura doğrulanıyor…");
        StatusText.Text = string.Empty;
        try
        {
            var row = await repository.ResolveAvailableAsync(code, cancellation.Token);
            if (row is null)
            {
                ShowError("Karekod, stoka alınmış bir gelen faturanın aktif stokunda bulunamadı.");
                return;
            }

            var group = cart.Add(row);
            RefundGrid.SelectedItem = group;
            selectedQr = row;
            UpdateSelectedQr();
            UpdateCartState();
            ShowSuccess("Karekod kısmi iade listesine eklendi.");
            QrCodeInput.Clear();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError($"Karekod eklenemedi: {exception.GetBaseException().Message}");
        }
        finally
        {
            if (ReferenceEquals(operationCancellation, cancellation))
            {
                operationCancellation = null;
                cancellation.Dispose();
                SetBusy(false, string.Empty);
                UpdateCartState();
                QrCodeInput.Focus();
            }
        }
    }

    private void OnGroupExpandClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: PartialInvoiceRefundGroup group } toggle) return;
        group.IsExpanded = toggle.IsChecked == true;
        if (RefundGrid.ItemContainerGenerator.ContainerFromItem(group) is DataGridRow row)
            row.DetailsVisibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RefundGrid.SelectedItem is not PartialInvoiceRefundGroup group || group.QrCodes.Count == 0) return;
        selectedQr = group.QrCodes[0];
        UpdateSelectedQr();
    }

    private void OnQrSelected(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PartialInvoiceRefundQrRow row) return;
        selectedQr = row;
        RefundGrid.SelectedItem = cart.Groups.FirstOrDefault(group => group.QrCodes.Contains(row));
        UpdateSelectedQr();
    }

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RelatedRecordMenus.HasNestedMenu(RefundGrid, e.OriginalSource as DependencyObject)) return;
        var row = RelatedRecordMenus.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        RefundGrid.SelectedItem = row?.Item;
    }

    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (RelatedRecordMenus.HasNestedMenu(RefundGrid, e.OriginalSource as DependencyObject)) return;
        var row = RelatedRecordMenus.FindAncestor<DataGridRow>(RefundGrid.InputHitTest(Mouse.GetPosition(RefundGrid)) as DependencyObject);
        var selected = e.CursorLeft < 0 && e.CursorTop < 0 ? RefundGrid.SelectedItem : row?.Item;
        if (selected is not PartialInvoiceRefundGroup)
        {
            e.Handled = true;
            return;
        }
        RefundGrid.SelectedItem = selected;
    }

    private void OnRemoveGroupClicked(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as PartialInvoiceRefundGroup
            ?? RefundGrid.SelectedItem as PartialInvoiceRefundGroup;
        if (group is null) return;
        cart.RemoveGroup(group);
        SelectFirstRemainingQr();
    }

    private void OnRemoveQrClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PartialInvoiceRefundQrRow row) return;
        cart.Remove(row);
        SelectFirstRemainingQr();
    }

    private void OnClearCartClicked(object sender, RoutedEventArgs e)
    {
        cart.Clear();
        selectedQr = null;
        UpdateSelectedQr();
        UpdateCartState();
        StatusText.Text = string.Empty;
        QrCodeInput.Focus();
    }

    private void SelectFirstRemainingQr()
    {
        var group = cart.Groups.FirstOrDefault();
        RefundGrid.SelectedItem = group;
        selectedQr = group?.QrCodes.FirstOrDefault();
        UpdateSelectedQr();
        UpdateCartState();
    }

    private async void OnRefundClicked(object sender, RoutedEventArgs e)
    {
        var selected = cart.QrCodes.ToArray();
        if (selected.Length == 0 || operationCancellation is not null) return;
        var answer = pharmacyApp.Localization.EnglishMessageBox.Show(
            $"{selected.Length:N0} QR codes will be removed from stock as a warehouse return. Continue?",
            "Kısmi İade", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var currentUser = ((App)Application.Current).CurrentUser;
        if (currentUser is null)
        {
            ShowError("Kısmi iade için oturum açmış kullanıcı gereklidir.");
            return;
        }

        var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        SetBusy(true, launchOptions.SendItsNotifications
            ? "İTS Mal İade bildirimi ve stok hareketi tamamlanıyor…"
            : "Kısmi iade stok hareketi oluşturuluyor…");
        try
        {
            var result = await repository.RefundAsync(
                selected.Select(row => row.QrCode).ToArray(),
                NotesBox.Text,
                currentUser.UserId,
                launchOptions.SendItsNotifications,
                cancellation.Token);

            cart.Clear();
            selectedQr = null;
            NotesBox.Clear();
            UpdateSelectedQr();
            UpdateCartState();
            var notificationText = result.ItsNotificationCount > 0
                ? $" {result.ItsNotificationCount:N0} Mal İade bildirimi gönderildi" +
                  (result.ItsNotificationIds.Count > 0
                      ? $" ({string.Join(", ", result.ItsNotificationIds)})."
                      : ".")
                : $" {result.ItsRequestCount:N0} Mal İade isteği doğrulandı fakat test verisi modunda gönderilmedi.";
            ShowSuccess(
                $"{result.QrCount:N0} karekod, {result.InvoiceCount:N0} faturaya bağlı " +
                $"{result.ReferenceCode} hareketiyle stoktan çıkarıldı.{notificationText}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError($"Kısmi iade tamamlanamadı: {exception.GetBaseException().Message}");
        }
        finally
        {
            if (ReferenceEquals(operationCancellation, cancellation))
            {
                operationCancellation = null;
                cancellation.Dispose();
                SetBusy(false, string.Empty);
                UpdateCartState();
                QrCodeInput.Focus();
            }
        }
    }

    private void OnCopyQrClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PartialInvoiceRefundQrRow row) return;
        try
        {
            Clipboard.SetText(row.QrCode);
            ShowSuccess("Karekod panoya kopyalandı.");
        }
        catch (Exception exception)
        {
            ShowError($"Karekod kopyalanamadı: {exception.Message}");
        }
    }

    private void UpdateCartState()
    {
        SelectionText.Text = $"İade listesi: {cart.QrCount:N0} karekod / {cart.InvoiceCount:N0} fatura";
        RefundButton.IsEnabled = cart.QrCount > 0 && operationCancellation is null;
        EmptyText.Visibility = cart.QrCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefundGrid.Items.Refresh();
    }

    private void UpdateSelectedQr()
    {
        SelectedQrText.Text = selectedQr?.QrCode ?? "—";
        SelectedProductText.Text = selectedQr?.ProductName ?? "—";
        SelectedInvoiceText.Text = selectedQr?.InvoiceUuid ?? "—";
        SelectedDepotText.Text = selectedQr?.SenderName ?? "—";
        SelectedIssueDateText.Text = selectedQr?.IssueDateDisplay ?? "—";
        SelectedExpiryText.Text = selectedQr?.ExpiryDateDisplay ?? "—";
    }

    private void SetBusy(bool busy, string text)
    {
        LoadingOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = text;
        QrCodeInput.IsEnabled = !busy;
        AddItemButton.IsEnabled = !busy;
        RefundButton.IsEnabled = !busy && cart.QrCount > 0;
    }

    private void ShowSuccess(string message)
    {
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(23, 97, 61));
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(141, 46, 46));
        StatusText.Text = message;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        current?.Cancel();
        current?.Dispose();
    }
}
