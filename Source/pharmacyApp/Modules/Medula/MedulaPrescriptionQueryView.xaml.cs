using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.Modules;

namespace pharmacyApp.Modules.Medula;

public partial class MedulaPrescriptionQueryView : UserControl, IModuleTabRequestSource
{
    private readonly MedulaAutomation service = MedulaAutomation.Instance;
    private readonly CancellationTokenSource lifetime = new();
    private bool loadedOnce;
    private bool busy;

    public MedulaPrescriptionQueryView()
    {
        Prescriptions = [];
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<MedulaPrescriptionListItem> Prescriptions { get; }
    public event Action<ModuleTabRequest>? TabRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.StatusChanged += OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnWarningChanged;
        service.ScopedWarningChanged += OnWarningChanged;
        if (loadedOnce) return;
        loadedOnce = true;
        await OpenQueryAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        service.StatusChanged -= OnServiceStatusChanged;
        service.ScopedWarningChanged -= OnWarningChanged;
    }

    private void OnServiceStatusChanged(object? sender, MedulaConnectionStatus status) =>
        Dispatcher.InvokeAsync(() =>
        {
            ConnectionText.Text = status.Message;
            StatusDot.Fill = new SolidColorBrush(status.State switch
            {
                MedulaConnectionState.Connected => Color.FromRgb(13, 148, 136),
                MedulaConnectionState.Failed or MedulaConnectionState.TemporarilyOffline =>
                    Color.FromRgb(198, 67, 67),
                _ => Color.FromRgb(210, 139, 44)
            });
        });

    private void OnWarningChanged(MedulaScopedText message)
    {
        if (message.Scope != MedulaMessageScope.PrescriptionList) return;
        Dispatcher.InvokeAsync(() => SetMessage(message.Text, true));
    }

    private async Task OpenQueryAsync()
    {
        if (busy) return;
        SetBusy(true, "Reçete Sorgu ekranı açılıyor…");
        try
        {
            var result = await service.OpenPrescriptionQueryAsync(lifetime.Token);
            ConnectionText.Text = result.Message;
            if (!result.IsSuccess) SetMessage(result.Message, true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        Prescriptions.Clear();
        EmptyState.Visibility = Visibility.Visible;
        SetMessage(null, false);
        await OpenQueryAsync();
    }

    private async void OnPrescriptionNumberSearchClick(object sender, RoutedEventArgs e) =>
        await OpenPrescriptionByNumberAsync();

    private async void OnPrescriptionNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenPrescriptionByNumberAsync();
    }

    private async Task OpenPrescriptionByNumberAsync()
    {
        if (busy) return;
        var number = PrescriptionNumberBox.Text.Trim();
        if (number.Length == 0)
        {
            SetMessage("Reçete numarası giriniz.", true);
            return;
        }

        SetBusy(true, "Reçete sorgulanıyor…");
        SetMessage(null, false);
        try
        {
            var snapshot = await service.OpenPrescriptionAsync(
                new MedulaPrescriptionLocator(number), lifetime.Token);
            if (snapshot is null)
            {
                SetMessage("Reçete Medula'da açılamadı.", true);
                return;
            }
            OpenPrescriptionTab(snapshot, number);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetMessage(exception.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPatientSearchClick(object sender, RoutedEventArgs e) =>
        await SearchPatientAsync();

    private async void OnPatientIdentityKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchPatientAsync();
    }

    private void OnPatientIdentityPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private async Task SearchPatientAsync()
    {
        if (busy) return;
        var tc = PatientIdentityBox.Text.Trim();
        if (tc.Length != 11 || tc.Any(character => !char.IsDigit(character)))
        {
            SetMessage("Geçerli bir T.C. Kimlik No giriniz.", true);
            return;
        }

        SetBusy(true, "Hastanın reçeteleri sorgulanıyor…");
        SetMessage(null, false);
        try
        {
            var result = await service.OpenPatientPrescriptionsAsync(tc, cancellationToken: lifetime.Token);
            Prescriptions.Clear();
            foreach (var item in result.Prescriptions) Prescriptions.Add(item);
            EmptyState.Visibility = Prescriptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetMessage(result.WarningText ?? result.Message, !result.IsSuccess);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnResultGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (busy || ResultGrid.SelectedItem is not MedulaPrescriptionListItem item) return;
        SetBusy(true, $"{item.PrescriptionNumber} açılıyor…");
        SetMessage(null, false);
        try
        {
            var snapshot = await service.OpenPrescriptionFromListAsync(item, lifetime.Token);
            if (snapshot is null)
            {
                SetMessage("Seçilen reçete Medula'da açılamadı.", true);
                return;
            }
            OpenPrescriptionTab(snapshot with { SourceListItem = snapshot.SourceListItem ?? item },
                item.PrescriptionNumber);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenPrescriptionTab(MedulaPrescriptionSnapshot snapshot, string prescriptionNumber)
    {
        var safeNumber = new string(prescriptionNumber.Where(char.IsLetterOrDigit).ToArray());
        TabRequested?.Invoke(new ModuleTabRequest(
            $"medula.prescription-view.{safeNumber}",
            $"Reçete {prescriptionNumber}",
            () => new MedulaPrescriptionEntryView(snapshot),
            1180,
            680,
            CreatePrescriptionIcon));
    }

    private static FrameworkElement CreatePrescriptionIcon() => new Border
    {
        Width = 16,
        Height = 16,
        Background = new SolidColorBrush(Color.FromRgb(43, 106, 125)),
        CornerRadius = new CornerRadius(0),
        Child = new TextBlock
        {
            Text = "R",
            Foreground = Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private void SetBusy(bool value, string? text = null)
    {
        busy = value;
        PrescriptionNumberBox.IsEnabled = !value;
        PatientIdentityBox.IsEnabled = !value;
        ResultGrid.IsEnabled = !value;
        if (!string.IsNullOrWhiteSpace(text)) ConnectionText.Text = text;
    }

    private void SetMessage(string? message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            MessageText.Text = string.Empty;
            return;
        }

        MessageText.Text = message.Trim();
        MessagePanel.Background = new SolidColorBrush(isError
            ? Color.FromRgb(255, 242, 241)
            : Color.FromRgb(239, 248, 244));
        MessagePanel.BorderBrush = new SolidColorBrush(isError
            ? Color.FromRgb(232, 184, 180)
            : Color.FromRgb(173, 213, 193));
        MessageText.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(154, 62, 55)
            : Color.FromRgb(35, 122, 87));
        MessageIcon.Foreground = MessageText.Foreground;
        MessageIcon.Text = isError ? "" : "";
        MessagePanel.Visibility = Visibility.Visible;
    }
}
