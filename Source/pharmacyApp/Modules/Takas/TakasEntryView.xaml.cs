using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pharmacyApp.Infrastructure;
using pharmacyApp.Modules.Its;
using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Takas;

public partial class TakasEntryView : UserControl, IModuleBackgroundWorkSource
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly TakasRepository repository = new();
    private readonly ItsApiClient apiClient = new();
    private readonly TakasCart cart = new();
    private readonly TakasEntryLaunchOptions options;
    private CancellationTokenSource? pharmacySearchCancellation;
    private TakasPharmacySuggestion? selectedPharmacy;
    private bool suppressPharmacySearch;
    private bool operationInProgress;

    public TakasEntryView()
        : this(TakasEntryLaunchOptions.Outgoing)
    {
    }

    public TakasEntryView(TakasEntryLaunchOptions options)
    {
        this.options = options;
        InitializeComponent();
        pharmacyApp.Infrastructure.EditionPresentation.AddItsNotice(this);
        ConfigureDirectionUi();
        TakasGrid.ItemsSource = cart.Groups;
        cart.Groups.CollectionChanged += OnCartGroupsChanged;
        RefreshUi();
    }

    public bool IsBackgroundWorkRunning => operationInProgress;
    private bool IsIncoming => options.IsIncoming;
    public event Action<bool>? BackgroundWorkStateChanged;

    private void ConfigureDirectionUi()
    {
        PharmacyPlaceholder.Text = IsIncoming
            ? "Gönderen eczane ara (en az 2 karakter)"
            : "Alıcı eczane ara (en az 2 karakter)";
        PharmacySearchBox.ToolTip = IsIncoming
            ? "Gönderen eczaneyi ad, eczacı veya GLN ile ara"
            : "Alıcı eczaneyi ad, eczacı veya GLN ile ara";
        CounterpartyPharmacyLabel.Text = IsIncoming ? "Gönderen eczane" : "Alıcı eczane";
        CounterpartyGlnLabel.Text = IsIncoming ? "Gönderen GLN" : "Alıcı GLN";
        LocalGlnLabel.Text = IsIncoming ? "Alıcı eczane GLN" : "Gönderen eczane GLN";
        CompleteTakasButton.Content = IsIncoming
            ? "İTS Mal Alım ve Takası Tamamla"
            : "İTS Mal Devir ve Takası Tamamla";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshUi();
        PharmacySearchBox.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        CancellationTokenSourceGuard.CancelAndDispose(ref pharmacySearchCancellation);

    private async void OnPharmacySearchTextChanged(object sender, TextChangedEventArgs e)
    {
        PharmacyPlaceholder.Visibility = PharmacySearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (suppressPharmacySearch) return;
        selectedPharmacy = null;
        RefreshUi();
        CancellationTokenSourceGuard.CancelAndDispose(ref pharmacySearchCancellation);
        PharmacySuggestionPopup.IsOpen = false;
        PharmacySuggestionList.ItemsSource = null;
        var search = PharmacySearchBox.Text.Trim();
        if (search.Length < 2) return;

        var cancellation = new CancellationTokenSource();
        pharmacySearchCancellation = cancellation;
        try
        {
            await Task.Delay(250, cancellation.Token);
            var suggestions = await repository.SearchPharmaciesAsync(search, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(pharmacySearchCancellation, cancellation)) return;
            PharmacySuggestionList.ItemsSource = suggestions;
            PharmacySuggestionPopup.IsOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Eczane araması yapılamadı: {exception.Message}", false);
        }
    }

    private void OnPharmacySuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PharmacySuggestionList.SelectedItem is not TakasPharmacySuggestion pharmacy) return;
        selectedPharmacy = pharmacy;
        suppressPharmacySearch = true;
        PharmacySearchBox.Text = pharmacy.DisplayText;
        PharmacySearchBox.CaretIndex = PharmacySearchBox.Text.Length;
        suppressPharmacySearch = false;
        PharmacySuggestionPopup.IsOpen = false;
        PharmacySuggestionList.SelectedItem = null;
        QrCodeInput.Focus();
        RefreshUi();
    }

    private async void OnAddItemClicked(object sender, RoutedEventArgs e) => await AddEnteredItemAsync();

    private async void OnQrCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddEnteredItemAsync();
    }

    private async Task AddEnteredItemAsync()
    {
        var enteredCode = QrCodeInput.Text.Trim();
        if (enteredCode.Length == 0 || operationInProgress) return;
        try
        {
            SetOperationState(true);
            SetStatus(IsIncoming ? "Gelen karekod hazırlanıyor..." : "Karekod aktif stokta aranıyor...", null);
            var item = IsIncoming
                ? await repository.ResolveIncomingItemAsync(enteredCode, CancellationToken.None)
                : await repository.ResolveItemAsync(enteredCode, CancellationToken.None);
            if (item is null)
            {
                SetStatus("Karekod aktif stokta bulunamadı. Takas yalnızca tekil ve aktif karekodlarla yapılabilir.", false);
                return;
            }
            if (cart.Contains(item.QrCode))
            {
                SetStatus("Bu karekod Takas listesinde zaten bulunuyor.", false);
                return;
            }
            var addedLine = cart.Add(item);
            var addedGroup = cart.Groups.First(group => group.Entries.Contains(addedLine));
            TakasGrid.SelectedItem = addedGroup;
            QrCodeInput.Clear();
            SetStatus(IsIncoming
                ? "Karekod gelen Takas listesine eklendi."
                : "Karekod giden Takas listesine eklendi.", true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Npgsql.NpgsqlException)
        {
            SetStatus($"Karekod eklenemedi: {exception.Message}", false);
        }
        finally
        {
            SetOperationState(false);
            QrCodeInput.Focus();
        }
    }

    private void OnCartGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TakasGroup group in e.OldItems) group.PropertyChanged -= OnCartGroupChanged;
        if (e.NewItems is not null)
            foreach (TakasGroup group in e.NewItems) group.PropertyChanged += OnCartGroupChanged;
        RefreshUi();
    }

    private void OnCartGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TakasGroup.TotalDebt) or nameof(TakasGroup.Quantity))
        {
            RefreshUi();
        }
    }

    private void OnTakasGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TakasGrid.SelectedItem is TakasGroup group) ShowProduct(group.Representative);
    }

    private void OnTakasGroupExpandClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: TakasGroup group } toggle) return;
        group.IsExpanded = toggle.IsChecked == true;
        if (TakasGrid.ItemContainerGenerator.ContainerFromItem(group) is DataGridRow row)
            row.DetailsVisibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnTakasEntrySelected(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TakasCartLine line) return;
        var group = cart.Groups.FirstOrDefault(candidate => candidate.Entries.Contains(line));
        if (group is not null) TakasGrid.SelectedItem = group;
        ShowProduct(line.Item);
    }

    private void OnRemoveGroupClicked(object sender, RoutedEventArgs e)
    {
        if (TakasGrid.SelectedItem is not TakasGroup group) return;
        cart.Remove(group);
        if (cart.ItemCount == 0) ClearProduct();
    }

    private void OnRemoveLineClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TakasCartLine line) return;
        cart.Remove(line);
        if (cart.ItemCount == 0) ClearProduct();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (operationInProgress) return;
        cart.Clear();
        ClearProduct();
        SetStatus("Takas listesi temizlendi.", null);
    }

    private async void OnCompleteTakasClicked(object sender, RoutedEventArgs e)
    {
        if (operationInProgress || selectedPharmacy is null || cart.ItemCount == 0) return;
        TakasGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TakasGrid.CommitEdit(DataGridEditingUnit.Row, true);

        ItsCredentials credentials;
        string sourceGln;
        string destinationGln;
        ItsTransferRequest? transferRequest = null;
        ItsAcceptanceRequest? acceptanceRequest = null;
        string userId;
        try
        {
            var settings = ApplicationSettingsStore.Current;
            var localGln = settings.ItsPharmacyGln.Trim();
            if (!Gs1IdentifierValidator.IsValidGln13(localGln))
                throw new InvalidOperationException("Eczane GLN kodu Ayarlar > İTS Ayarları bölümünde geçerli olarak yapılandırılmalıdır.");
            if (settings.ItsUsername.Length == 0 || settings.ItsPassword.Length == 0)
                throw new InvalidOperationException("İTS kullanıcı adı ve parolası Ayarlar > İTS Ayarları bölümünde yapılandırılmalıdır.");
            if (!Gs1IdentifierValidator.IsValidGln13(selectedPharmacy.GlnCode))
                throw new InvalidOperationException("Seçilen eczanenin GLN kodu geçerli değil.");
            if (string.Equals(localGln, selectedPharmacy.GlnCode, StringComparison.Ordinal))
                throw new InvalidOperationException("Takas için farklı bir eczane seçilmelidir.");

            var currentUser = (Application.Current as App)?.CurrentUser
                ?? throw new InvalidOperationException("Takas işlemi için oturum açmış kullanıcı gereklidir.");
            userId = currentUser.UserId;
            credentials = new ItsCredentials(settings.ItsUsername, settings.ItsPassword);
            var products = cart.Lines.Select(line => line.Item.ItsProduct).ToArray();
            sourceGln = IsIncoming ? selectedPharmacy.GlnCode : localGln;
            destinationGln = IsIncoming ? localGln : selectedPharmacy.GlnCode;
            if (IsIncoming)
                acceptanceRequest = ItsAcceptanceRequestFactory.Create(products);
            else
                transferRequest = ItsTransferRequestFactory.Create(destinationGln, products);
        }
        catch (InvalidOperationException exception)
        {
            SetStatus(exception.Message, false);
            return;
        }

        var confirmation = pharmacyApp.Localization.EnglishMessageBox.Show(
            Window.GetWindow(this),
            $"Bu işlem GERÇEK İTS sistemine {(IsIncoming ? "Mal Alım" : "Mal Devir")} bildirimi gönderecektir.\n\n" +
            $"Gönderen GLN: {sourceGln}\n" +
            $"Alıcı GLN: {destinationGln}\n" +
            $"Karşı eczane: {selectedPharmacy.PharmacyName}\n" +
            $"Karekod adedi: {cart.ItemCount}\n" +
            $"Takas borç değeri: {cart.TotalDebt.ToString("N2", TurkishCulture)} ₺\n" +
            $"Adres: {(IsIncoming ? ItsServiceEndpoints.Acceptance(ItsEnvironment.Production) : ItsServiceEndpoints.Transfer(ItsEnvironment.Production))}\n\n" +
            (IsIncoming
                ? "İTS kabulünden sonra karekodlar aktif stoka alınacak ve gönderen eczanenin borç bakiyesi azaltılacaktır. Devam edilsin mi?"
                : "İTS kabulünden sonra karekodlar bekleyen stoka alınacak ve alıcı eczanenin borç bakiyesi artırılacaktır. Devam edilsin mi?"),
            IsIncoming
                ? "DİKKAT: Gerçek İTS Mal Alım Bildirimi"
                : "DİKKAT: Gerçek İTS Mal Devir Bildirimi",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        int? preparedTakasId = null;
        try
        {
            SetOperationState(true);
            SetStatus("Takas kaydı hazırlanıyor...", null);
            var lines = cart.Lines.Select(line => new TakasLineInput(line.Item, line.DebtAmount)).ToArray();
            preparedTakasId = IsIncoming
                ? await repository.PrepareIncomingAsync(
                    selectedPharmacy.RecordId, sourceGln, destinationGln, lines,
                    NotesBox.Text, userId, CancellationToken.None)
                : await repository.PrepareOutgoingAsync(
                    selectedPharmacy.RecordId, sourceGln, destinationGln, lines,
                    NotesBox.Text, userId, CancellationToken.None);

            SetStatus(IsIncoming
                ? "İTS Mal Alım bildirimi gönderiliyor..."
                : "İTS Mal Devir bildirimi gönderiliyor...", null);
            var result = IsIncoming
                ? await apiClient.SendAcceptanceAsync(
                    ItsEnvironment.Production, credentials, acceptanceRequest!, CancellationToken.None)
                : await apiClient.SendTransferAsync(
                    ItsEnvironment.Production, credentials, transferRequest!, CancellationToken.None);
            var finalized = IsIncoming
                ? await repository.ApplyAcceptanceResultAsync(
                    preparedTakasId.Value, result, userId, CancellationToken.None)
                : await repository.ApplyTransferResultAsync(
                    preparedTakasId.Value, result, userId, CancellationToken.None);

            cart.Clear();
            ClearProduct();
            NotesBox.Clear();
            var notification = string.IsNullOrWhiteSpace(finalized.NotificationId)
                ? string.Empty
                : $" Bildirim no: {finalized.NotificationId}.";
            if (finalized.FailedItemCount == 0)
                SetStatus(IsIncoming
                    ? $"Gelen Takas #{finalized.TakasId} tamamlandı. {finalized.SuccessfulItemCount} karekod aktif stoka alındı; borç bakiyesi {finalized.AppliedDebtAmount:N2} ₺ azaltıldı.{notification}"
                    : $"Giden Takas #{finalized.TakasId} tamamlandı. {finalized.SuccessfulItemCount} karekod bekleyen stoka alındı; borç bakiyesi {finalized.AppliedDebtAmount:N2} ₺ artırıldı.{notification}", true);
            else
                SetStatus($"{(IsIncoming ? "Gelen" : "Giden")} Takas #{finalized.TakasId}: {finalized.SuccessfulItemCount} karekod kabul edildi, {finalized.FailedItemCount} karekod reddedildi.{notification} Ayrıntılar Takas kaydında tutuldu.", false);
        }
        catch (ItsNotificationNotSentException exception)
        {
            if (preparedTakasId.HasValue)
            {
                try
                {
                    if (IsIncoming)
                        await repository.MarkAcceptanceNotSentAsync(
                            preparedTakasId.Value, exception.Message, CancellationToken.None);
                    else
                        await repository.MarkNotificationNotSentAsync(
                            preparedTakasId.Value, exception.Message, CancellationToken.None);
                }
                catch (Exception localException)
                {
                    SetStatus(
                        $"İTS {(IsIncoming ? "Mal Alım" : "Mal Devir")} bildirimi gönderilmedi ancak yerel Takas kaydı kapatılamadı: {localException.Message} " +
                        $"Takas #{preparedTakasId.Value} için teknik kontrol gerekir.",
                        false);
                    return;
                }
            }

            SetStatus(
                $"{exception.Message} " +
                (preparedTakasId.HasValue ? $"Takas #{preparedTakasId.Value} başarısız olarak kaydedildi. " : string.Empty) +
                "Stok ve borç değiştirilmedi. Ağ bağlantısı düzeldikten sonra aynı karekodlarla yeniden deneyebilirsiniz.",
                false);
        }
        catch (Exception exception)
        {
            if (preparedTakasId.HasValue)
            {
                try
                {
                    await repository.MarkReconciliationRequiredAsync(
                        preparedTakasId.Value,
                        $"İTS {(IsIncoming ? "Mal Alım" : "Mal Devir")} çağrısı veya yerel sonuç uygulaması kesinleştirilemedi: " + exception.Message,
                        CancellationToken.None);
                }
                catch
                {
                }
            }
            SetStatus(
                preparedTakasId.HasValue
                    ? $"İşlem kesinleştirilemedi: {exception.Message} Takas #{preparedTakasId.Value} mutabakat gerektiriyor; aynı karekodlarla tekrar bildirim göndermeyin."
                    : $"Takas başlatılamadı: {exception.Message}",
                false);
        }
        finally
        {
            SetOperationState(false);
        }
    }

    private void RefreshUi()
    {
        var settings = ApplicationSettingsStore.Current;
        LocalGlnText.Text = settings.ItsPharmacyGln.Length == 0 ? "Ayarlanmamış" : settings.ItsPharmacyGln;
        SelectedPharmacyName.Text = selectedPharmacy?.PharmacyName ?? "Eczane seçilmedi";
        CounterpartyGlnText.Text = selectedPharmacy?.GlnCode ?? "—";
        CurrentDebtText.Text = selectedPharmacy?.DebtDisplay ?? "—";
        TotalsText.Text = $"{cart.ItemCount} karekod  ·  Takas değeri: {cart.TotalDebt.ToString("N2", TurkishCulture)} ₺";
        CompleteTakasButton.IsEnabled = !operationInProgress && selectedPharmacy is not null && cart.ItemCount > 0;
    }

    private void ShowProduct(TakasStockItem item)
    {
        SummaryProductName.Text = item.ProductName;
        SummaryWarehousePrice.Text = item.WarehousePriceDisplay;
        SummarySalePrice.Text = item.SalePriceDisplay;
        SummaryPublicPrice.Text = item.PublicPriceDisplay;
        SummaryStock.Text = item.StockQuantity.ToString("N0", TurkishCulture);
        SummaryPhoto.Source = CreateImage(item.FirstPhoto);
        NoPhotoText.Visibility = SummaryPhoto.Source is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearProduct()
    {
        SummaryProductName.Text = "—";
        SummaryWarehousePrice.Text = "—";
        SummarySalePrice.Text = "—";
        SummaryPublicPrice.Text = "—";
        SummaryStock.Text = "—";
        SummaryPhoto.Source = null;
        NoPhotoText.Visibility = Visibility.Visible;
    }

    private static ImageSource? CreateImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void SetOperationState(bool running)
    {
        if (operationInProgress == running) return;
        operationInProgress = running;
        RefreshUi();
        BackgroundWorkStateChanged?.Invoke(running);
    }

    private void SetStatus(string text, bool? success)
    {
        StatusText.Text = text;
        StatusText.Foreground = success switch
        {
            true => new SolidColorBrush(Color.FromRgb(22, 125, 78)),
            false => new SolidColorBrush(Color.FromRgb(179, 58, 50)),
            _ => new SolidColorBrush(Color.FromRgb(102, 118, 128))
        };
    }
}
