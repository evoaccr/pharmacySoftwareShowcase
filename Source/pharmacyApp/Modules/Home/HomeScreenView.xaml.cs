using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using pharmacyApp.AI.Models;
using pharmacyApp.AI.Configuration;
using pharmacyApp.AI.Runtime;
using pharmacyApp.AI.Debug;
using pharmacyApp.AI.Tools.Medula;
using pharmacyApp.AI.Workflows;
using pharmacyApp.Modules.Medula;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.StockMovements;

namespace pharmacyApp.Modules.Home;

public partial class HomeScreenView : UserControl, IModuleTabRequestSource, IModuleWorkspaceRequestSource
{
    private readonly AiRuntime runtime;
    private readonly AiWorkflowRuntime workflowRuntime;
    private readonly Func<AiApplicationContext> contextProvider;
    private CancellationTokenSource? requestCancellation;
    private CancellationTokenSource? rowInteractionCancellation;
    private readonly IMedulaAiOperations medulaOperations = new MedulaAiOperations();
    private string sessionId = Guid.NewGuid().ToString("N");
    private PendingAiActionView? pendingAction;
    private PendingAiRouteApprovalView? pendingRouteApproval;
    private AiWorkflowApprovalView? pendingWorkflowApproval;
    private ScrollViewer? messageScrollViewer;
    private double smoothScrollTarget;
    private DateTime smoothScrollFrameAt;
    private bool isSmoothScrolling;
    private bool legacyMode;

    public HomeScreenView(Func<AiApplicationContext> contextProvider)
    {
        this.contextProvider = contextProvider;
        runtime = AiRuntimeFactory.Create();
        workflowRuntime = AiWorkflowRuntimeFactory.Create(runtime.Configuration, runtime.Transport, runtime.Registry);
        InitializeComponent();
        var routerConfiguration = runtime.Configuration.GetCurrent();
        var routerModels = AiRouterModelCatalog.ForConfiguration(routerConfiguration);
        RouterModelSelector.ItemsSource = routerModels;
        RouterModelSelector.SelectedItem = routerModels.First(option => option.Id == routerConfiguration.RouterModel);
        DataContext = this;
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public event Action<ModuleTabRequest>? TabRequested;
    public event Action<ModuleWorkspaceRequest>? WorkspaceRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshDebugControls();
        UpdateModeUi();
        MessageBox.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        requestCancellation?.Cancel();
        rowInteractionCancellation?.Cancel();
        rowInteractionCancellation?.Dispose();
        rowInteractionCancellation = null;
        StopSmoothScrolling();
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async Task SendCurrentAsync()
    {
        var message = MessageBox.Text.Trim();
        if (message.Length == 0 || requestCancellation is not null || HasPendingWorkflowSelection()) return;
        var debugMode = RefreshDebugControls();
        MessageBox.Clear();
        AddMessage(true, message);
        var loadingMessage = AddLoadingMessage("Eczanenizin Yapay Zeka Asistanı yanıt hazırlıyor…", debugMode);
        var progress = CreateProgress(loadingMessage, debugMode);
        SetBusy(true);
        requestCancellation = new CancellationTokenSource();
        try
        {
            var applicationContext = contextProvider();
            var context = new AiRequestContext(
                sessionId, message, applicationContext, new HashSet<string>(), progress,
                debugMode && !legacyMode ? SelectedRouterModel() : null);
            if (legacyMode)
            {
                var result = await runtime.Chat.SendAsync(context, requestCancellation.Token);
                RemoveMessage(loadingMessage);
                Present(result);
            }
            else
            {
                var result = await workflowRuntime.Chat.SendAsync(context, requestCancellation.Token);
                RemoveMessage(loadingMessage);
                Present(result);
            }
        }
        catch (OperationCanceledException)
        {
            RemoveMessage(loadingMessage);
            AddMessage(false, "İstek iptal edildi.");
        }
        catch (Exception)
        {
            RemoveMessage(loadingMessage);
            AddMessage(false, "AI isteği işlenirken beklenmeyen bir hata oluştu. Uygulama çalışmaya devam ediyor; yeniden deneyebilir veya tanılama bilgilerini kontrol edebilirsiniz.");
        }
        finally
        {
            RemoveMessage(loadingMessage);
            requestCancellation?.Dispose();
            requestCancellation = null;
            SetBusy(false);
            MessageBox.Focus();
        }
    }

    private void Present(AiAssistantResult result)
    {
        RefreshDebugControls();
        var assistantMessage = AddMessage(
            false, result.Message, result.DebugSummary, result.ModelUsed);
        assistantMessage.FooterText = FormatUsage(result.UsageByModel);
        if (result.Blocks is { Count: > 0 })
        {
            foreach (var block in result.Blocks)
            {
                var content = block.Data?.ToString();
                if (!string.IsNullOrWhiteSpace(content))
                    AddMessage(false, string.IsNullOrWhiteSpace(block.Title) ? content : $"{block.Title}\n{content}");
            }
        }
        if (result.SuggestedActions is { Count: > 0 })
            foreach (var action in result.SuggestedActions.Where(action => !string.IsNullOrWhiteSpace(action.Label)).Take(8))
                assistantMessage.AddSuggestedAction(action);
        pendingAction = result.PendingConfirmation;
        pendingRouteApproval = result.PendingRouteApproval;
        pendingWorkflowApproval = null;
        if (pendingAction is not null)
        {
            ConfirmationTitle.Text = "İşlem onayı gerekli";
            ConfirmationSummary.Text = pendingAction.Summary;
            ConfirmationArguments.Text = PrettyJson(pendingAction.ArgumentsJson);
            ConfirmationArguments.Visibility = Visibility.Visible;
            ConfirmButton.Content = "Onayla";
            RejectButton.Content = "Reddet";
            ConfirmationPanel.Visibility = Visibility.Visible;
        }
        else if (pendingRouteApproval is not null)
        {
            ConfirmationTitle.Text = "Yönlendirme duraklatıldı";
            ConfirmationSummary.Text = pendingRouteApproval.Summary;
            ConfirmationArguments.Clear();
            ConfirmationArguments.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = "Evet";
            RejectButton.Content = "Hayır";
            ConfirmationPanel.Visibility = Visibility.Visible;
        }
        else ConfirmationPanel.Visibility = Visibility.Collapsed;
    }

    private void OnWorkflowTableAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName is "__row_color" or "__search_text")
        {
            e.Cancel = true;
            return;
        }
        if (sender is DataGrid grid)
        {
            var table = grid.Tag as WorkflowTableViewModel ??
                        (grid.DataContext is ChatMessageViewModel message ? message.Table : null);
            var column = table?.Columns.FirstOrDefault(value => value.Key == e.PropertyName);
            if (column is not null)
            {
                e.Column.Header = column.Label;
                e.Column.Visibility = table!.IsColumnVisible(column.Key) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        e.Column.SortMemberPath = e.PropertyName;
        e.Column.Width = e.PropertyName switch
        {
            "medicine_name" or "product_name" or "name" => new DataGridLength(240),
            "qr_code" => new DataGridLength(275),
            "movement_date" or "effective_date" or "day" => new DataGridLength(145),
            "barcode" => new DataGridLength(125),
            "address" => new DataGridLength(180),
            _ => new DataGridLength(125)
        };
        if (e.Column is DataGridTextColumn textColumn)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            textColumn.ElementStyle = style;
        }
    }

    private void OnWorkflowTableAutoGeneratedColumns(object? sender, EventArgs e)
    {
        if (sender is not DataGrid
            {
                DataContext: ChatMessageViewModel { Table: { InteractionKind: not WorkflowRowInteractionKind.None } }
            } grid || grid.Columns.Any(column => Equals(column.SortMemberPath, "__workflow_forward")))
            return;
        grid.Columns.Insert(0, new DataGridTemplateColumn
        {
            Header = "Aç",
            SortMemberPath = "__workflow_forward",
            CellTemplate = (DataTemplate)FindResource("WorkflowForwardCellTemplate"),
            Width = new DataGridLength(44),
            CanUserSort = false,
            IsReadOnly = true
        });
    }

    private void Present(AiWorkflowAssistantResult result)
    {
        RefreshDebugControls();
        var assistantMessage = AddMessage(
            false, result.Message, result.DebugSummary, result.ModelUsed,
            result.ModelUsed is null ? "İş Akışı" : null);
        assistantMessage.FooterText = FormatWorkflowFooter(result.UsageByModel, result.WorkflowQueue);
        foreach (var capture in workflowRuntime.Chat.GetLatestRequestCaptureFiles(sessionId))
            assistantMessage.AddRequestCapture(capture);
        if (result.Prescription is not null)
            assistantMessage.SetPrescription(result.Prescription);
        if (result.Table is not null)
            assistantMessage.SetTable(result.Table);
        if (result.Content is not null)
            assistantMessage.SetContent(result.Content);
        if (result.ModuleActions is { Count: > 0 })
            foreach (var action in result.ModuleActions)
                assistantMessage.AddModuleAction(action);
        if (result.PendingSelection is not null)
            assistantMessage.SetWorkflowSelection(result.PendingSelection);
        pendingWorkflowApproval = result.PendingApproval;
        pendingAction = null;
        pendingRouteApproval = null;
        if (pendingWorkflowApproval is not null)
        {
            ConfirmationTitle.Text = "Yönlendirme duraklatıldı";
            ConfirmationSummary.Text = pendingWorkflowApproval.Summary;
            ConfirmationArguments.Clear();
            ConfirmationArguments.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = "Evet";
            RejectButton.Content = "Hayır";
            ConfirmationPanel.Visibility = Visibility.Visible;
        }
        else ConfirmationPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        if ((pendingAction is null && pendingRouteApproval is null && pendingWorkflowApproval is null) || requestCancellation is not null) return;
        var userId = contextProvider().CurrentUserId ?? string.Empty;
        var debugMode = RefreshDebugControls();
        var routingApproval = pendingRouteApproval;
        var workflowApproval = pendingWorkflowApproval;
        var loadingMessage = AddLoadingMessage(
            routingApproval is null && workflowApproval is null ? "Onaylanan işlem yürütülüyor…" : "Onaylanan istek yönlendiriliyor…",
            debugMode);
        var progress = CreateProgress(loadingMessage, debugMode);
        SetBusy(true);
        requestCancellation = new CancellationTokenSource();
        try
        {
            if (workflowApproval is not null)
            {
                var workflowResult = await workflowRuntime.Chat.ApproveAsync(
                    workflowApproval.Id, userId, requestCancellation.Token, progress);
                pendingWorkflowApproval = null;
                pendingAction = null;
                pendingRouteApproval = null;
                ConfirmationPanel.Visibility = Visibility.Collapsed;
                RemoveMessage(loadingMessage);
                Present(workflowResult);
                return;
            }
            var result = routingApproval is not null
                ? await runtime.Chat.ApproveRouteAsync(routingApproval.Id, userId, requestCancellation.Token, progress)
                : await runtime.Chat.ConfirmAsync(pendingAction!.Id, userId, requestCancellation.Token, progress);
            pendingAction = null;
            pendingRouteApproval = null;
            ConfirmationPanel.Visibility = Visibility.Collapsed;
            RemoveMessage(loadingMessage);
            Present(result);
        }
        catch (OperationCanceledException)
        {
            RemoveMessage(loadingMessage);
            AddMessage(false, "İstek iptal edildi.");
        }
        catch (Exception)
        {
            RemoveMessage(loadingMessage);
            AddMessage(false, "Onaylanan işlem yürütülürken beklenmeyen bir hata oluştu. Uygulama çalışmaya devam ediyor.");
        }
        finally
        {
            RemoveMessage(loadingMessage);
            requestCancellation?.Dispose();
            requestCancellation = null;
            SetBusy(false);
        }
    }

    private void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        if (pendingAction is null && pendingRouteApproval is null && pendingWorkflowApproval is null) return;
        var userId = contextProvider().CurrentUserId ?? string.Empty;
        if (pendingWorkflowApproval is not null)
            workflowRuntime.Chat.RejectApproval(pendingWorkflowApproval.Id, userId);
        else if (pendingRouteApproval is not null)
            runtime.Chat.RejectRoute(pendingRouteApproval.Id, userId);
        else
            runtime.Chat.Reject(pendingAction!.Id, userId);
        pendingAction = null;
        pendingRouteApproval = null;
        pendingWorkflowApproval = null;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        AddMessage(false, "İstek durduruldu; herhangi bir işlem yapılmadı.");
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => requestCancellation?.Cancel();

    private void OnNewConversationClicked(object sender, RoutedEventArgs e)
    {
        ResetConversation();
    }

    private void ResetConversation()
    {
        requestCancellation?.Cancel();
        workflowRuntime.Chat.Clear(sessionId);
        sessionId = Guid.NewGuid().ToString("N");
        Messages.Clear();
        WelcomePanel.Visibility = Visibility.Visible;
        pendingAction = null;
        pendingRouteApproval = null;
        pendingWorkflowApproval = null;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        SetBusy(false);
    }

    private void OnDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (legacyMode)
        {
            ShowTextWindow("AI Diagnostics", runtime.Diagnostics.FormatTurkish());
            return;
        }
        var configuration = runtime.Configuration.GetCurrent();
        var definitions = workflowRuntime.Registry.Definitions;
        ShowTextWindow("AI Diagnostics",
            $"Status: {(configuration.Enabled ? "Enabled" : "Disabled")}\n" +
            $"Router model: {configuration.RouterModel}\n" +
            $"Transport: {runtime.Transport.ConfigurationDescription}\n" +
            $"Workflows: {definitions.Count}\n\n" +
            string.Join(Environment.NewLine, definitions.Select(definition =>
                $"- {definition.DisplayFunctionName}{(definition.RouterVisible ? string.Empty : " [internal]")}")));
    }

    private void OnLegacyAiClicked(object sender, RoutedEventArgs e)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.ShowRestricted();
    }

    private void OnMissingToolsClicked(object sender, RoutedEventArgs e)
    {
        var records = runtime.MissingTools.Load().OrderByDescending(record => record.LastSeen).ToArray();
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var list = new ListBox { ItemsSource = records, DisplayMemberPath = nameof(MissingToolRecord.SuggestedToolName) };
        var detail = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12),
            Text = records.Length == 0
                ? $"Kayıtlı eksik yetenek bulunmuyor.\n\nKayıt dosyası:\n{MissingToolStore.DefaultFilePath}"
                : string.Empty
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not MissingToolRecord record) return;
            detail.Text = $"{record.MissingCapability}\n\nNeden: {record.Reason}\n\n" +
                          $"Beklenen sonuç: {record.SuggestedResult}\nRisk: {record.EstimatedRisk}\n" +
                          $"Tekrar: {record.Occurrences}";
        };
        var copy = new Button { Content = "Codex İstemini Kopyala", Margin = new Thickness(0, 12, 0, 0), Height = 38, Padding = new Thickness(14, 0, 14, 0) };
        copy.Click += (_, _) =>
        {
            if (list.SelectedItem is not MissingToolRecord record) return;
            try { Clipboard.SetText(runtime.MissingTools.CreateCodexPrompt(record)); }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        };
        var refresh = new Button
        {
            Content = "Yenile", Margin = new Thickness(8, 12, 0, 0), Height = 38,
            Padding = new Thickness(14, 0, 14, 0)
        };
        refresh.Click += (_, _) =>
        {
            var refreshed = runtime.MissingTools.Load().OrderByDescending(record => record.LastSeen).ToArray();
            list.ItemsSource = refreshed;
            list.SelectedIndex = refreshed.Length > 0 ? 0 : -1;
            if (refreshed.Length == 0)
                detail.Text = $"Kayıtlı eksik yetenek bulunmuyor.\n\nKayıt dosyası:\n{MissingToolStore.DefaultFilePath}";
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(copy);
        actions.Children.Add(refresh);
        Grid.SetColumn(detail, 2);
        Grid.SetRow(actions, 1);
        Grid.SetColumn(actions, 2);
        root.Children.Add(list);
        root.Children.Add(detail);
        root.Children.Add(actions);
        list.SelectedIndex = records.Length > 0 ? 0 : -1;
        new Window
        {
            Title = "Eksik AI Yetenekleri", Owner = Window.GetWindow(this), Width = 780, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, Content = root
        }.Show();
    }

    private void ShowTextWindow(string title, string text)
    {
        new Window
        {
            Title = title,
            Owner = Window.GetWindow(this),
            Width = 680,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
            Content = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18)
            }
        }.Show();
    }

    private async void OnCopyConversationClicked(object sender, RoutedEventArgs e)
    {
        if (Messages.Count == 0) return;
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            Messages.Where(message => !message.IsLoading).Select(message =>
            {
                var model = string.IsNullOrWhiteSpace(message.ModelText)
                    ? string.Empty
                    : $" [{message.ModelText}]";
                var debug = string.IsNullOrWhiteSpace(message.DebugText)
                    ? string.Empty
                    : Environment.NewLine + Environment.NewLine + "Debug:" + Environment.NewLine + message.DebugText;
                var footer = string.IsNullOrWhiteSpace(message.FooterText)
                    ? string.Empty
                    : Environment.NewLine + Environment.NewLine + message.FooterText;
                var table = message.Table is null
                    ? string.Empty
                    : Environment.NewLine + Environment.NewLine + message.Table.ToClipboardText();
                return $"{message.Sender}{model}:{Environment.NewLine}{message.Text}{table}{footer}{debug}";
            }));
        try
        {
            Clipboard.SetText(text);
            CopyConversationButton.Content = "Kopyalandı";
            await Task.Delay(1200);
            CopyConversationButton.Content = "Sohbeti Kopyala";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            CopyConversationButton.ToolTip = "Sohbet panoya kopyalanamadı.";
        }
    }

    private async void OnSuggestedActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiSuggestedAction action } || requestCancellation is not null) return;
        Messages.FirstOrDefault(message => message.SuggestedActions.Contains(action))?.ConsumeSuggestedActions();
        MessageBox.Text = action.Label;
        await SendCurrentAsync();
    }

    private void OnWorkflowModuleActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiWorkflowModuleAction action }) return;
        if (action.ModuleId == "workspace.modules")
        {
            var moduleIds = SplitActionValue(action, "module_ids").Take(2).ToArray();
            if (moduleIds.Length == 0) return;
            var layout = string.Equals(action.Parameters.GetValueOrDefault("layout"), "stacked", StringComparison.OrdinalIgnoreCase)
                ? ModuleWorkspaceLayout.Stacked
                : ModuleWorkspaceLayout.SideBySide;
            WorkspaceRequested?.Invoke(new ModuleWorkspaceRequest(
                moduleIds,
                action.Parameters.GetValueOrDefault("tab_title"),
                layout));
            return;
        }
        if (action.ModuleId == "products.catalog")
        {
            var barcodes = SplitActionValue(action, "barcodes").Take(100).ToArray();
            TabRequested?.Invoke(new ModuleTabRequest(
                "products.catalog.ai." + Guid.NewGuid().ToString("N"),
                "Ürün Kataloğu",
                () => new ProductCatalogView(new ProductCatalogLaunchOptions(Barcodes: barcodes)),
                1050,
                620));
            return;
        }
        if (action.ModuleId == "products.qr-movements")
        {
            var start = ParseActionDate(action, "start_date", DateTime.Today.AddDays(-30));
            var end = ParseActionDate(action, "end_date", DateTime.Today);
            var barcodes = SplitActionValue(action, "barcodes");
            var movementTypes = SplitActionValue(action, "movement_types");
            var addressType = action.Parameters.GetValueOrDefault("address_type")?.ToLowerInvariant() switch
            {
                "patient" => "Hasta",
                "pharmacy" => "Eczane",
                "depot" => "Depo",
                _ => string.Empty
            };
            var options = new QrMovementsLaunchOptions(
                start,
                end,
                UserId: action.Parameters.GetValueOrDefault("user_id") ?? string.Empty,
                MovementTypes: movementTypes,
                MovementAddress: action.Parameters.GetValueOrDefault("address_code") ?? string.Empty,
                AddressType: addressType,
                PeriodTitle: $"{start:dd.MM.yyyy}–{end:dd.MM.yyyy}",
                Barcodes: barcodes);
            TabRequested?.Invoke(new ModuleTabRequest(
                FilteredModuleId(action.ModuleId, start, end, barcodes),
                "Karekod Hareketleri",
                () => new QrMovementsView(options),
                1050,
                560));
            return;
        }
        if (action.ModuleId == "sales.retail-list")
        {
            var start = ParseActionDate(action, "start_date", DateTime.Today.AddDays(-30));
            var end = ParseActionDate(action, "end_date", DateTime.Today);
            var barcodes = SplitActionValue(action, "barcodes");
            var saleTypes = SplitActionValue(action, "sale_types");
            var includeRefunds = !string.Equals(action.Parameters.GetValueOrDefault("include_refunds"), "false", StringComparison.OrdinalIgnoreCase);
            var transactionTypes = RetailTransactionTypes.None;
            if (saleTypes.Count == 0 || saleTypes.Contains("retail", StringComparer.OrdinalIgnoreCase))
                transactionTypes |= RetailTransactionTypes.RetailSale | (includeRefunds ? RetailTransactionTypes.RetailRefund : 0);
            if (saleTypes.Count == 0 || saleTypes.Contains("prescription", StringComparer.OrdinalIgnoreCase))
                transactionTypes |= RetailTransactionTypes.PrescriptionSale | (includeRefunds ? RetailTransactionTypes.PrescriptionRefund : 0);
            var options = new RetailSalesListLaunchOptions(
                StartDate: start,
                EndDate: end,
                Barcodes: barcodes,
                UserId: action.Parameters.GetValueOrDefault("user_id") ?? string.Empty,
                TransactionTypes: transactionTypes,
                IsDebted: ParseNullableBoolean(action.Parameters.GetValueOrDefault("is_debted")),
                PatientId: int.TryParse(action.Parameters.GetValueOrDefault("patient_record_id"), out var patientId) ? patientId : null,
                MinimumTotal: ParseNullableDecimal(action.Parameters.GetValueOrDefault("minimum_total")),
                MaximumTotal: ParseNullableDecimal(action.Parameters.GetValueOrDefault("maximum_total")),
                MarkedOnly: string.Equals(action.Parameters.GetValueOrDefault("marked_only"), "true", StringComparison.OrdinalIgnoreCase),
                DiscountedOnly: SplitActionValue(action, "payment_types") is { Count: 1 } paymentTypes &&
                    paymentTypes[0].Equals("discount", StringComparison.OrdinalIgnoreCase),
                StartTime: TimeOnly.TryParseExact(action.Parameters.GetValueOrDefault("start_time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTime) ? startTime : null,
                EndTime: TimeOnly.TryParseExact(action.Parameters.GetValueOrDefault("end_time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endTime) ? endTime : null,
                Statuses: SplitActionValue(action, "statuses"),
                PrescriptionNo: action.Parameters.GetValueOrDefault("prescription_no") ?? string.Empty,
                QrCodes: SplitActionValue(action, "qr_codes"),
                PaymentTypes: SplitActionValue(action, "payment_types"));
            TabRequested?.Invoke(new ModuleTabRequest(
                FilteredModuleId(action.ModuleId, start, end, barcodes),
                "Satışlar",
                () => new RetailSalesListView(options),
                1180,
                650));
        }
    }

    private void OnAiRequestCaptureClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiRequestCaptureFile capture } || !File.Exists(capture.FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(capture.FilePath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowTextWindow("AI istek dosyası", File.ReadAllText(capture.FilePath));
        }
    }

    private async void OnWorkflowTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid
            {
                DataContext: ChatMessageViewModel { Table: { } table },
                SelectedItem: DataRowView row
            } || table.InteractionKind == WorkflowRowInteractionKind.None)
            return;
        table.SelectedRow = row;
        await LoadInlineRowDetailAsync(table, row);
    }

    private async void OnWorkflowTableLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid
            {
                DataContext: ChatMessageViewModel { Table: { } table }
            } || table.InteractionKind == WorkflowRowInteractionKind.None)
            return;
        var clicked = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as DataRowView;
        if (clicked is null) return;
        table.SelectedRow = clicked;
        await LoadInlineRowDetailAsync(table, clicked);
    }

    private void OnWorkflowTableRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not DataRowView selected) return;
        row.IsSelected = true;
        if (grid.DataContext is ChatMessageViewModel { Table: { } table })
            table.SelectedRow = selected;
    }

    private void OnWorkflowTableContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid
            {
                DataContext: ChatMessageViewModel { Table: { } table },
                ContextMenu: { } menu
            } || table.InteractionKind == WorkflowRowInteractionKind.None)
        {
            e.Handled = true;
            return;
        }
        var hit = ((DataGrid)sender).InputHitTest(Mouse.GetPosition((DataGrid)sender)) as DependencyObject;
        if (FindAncestor<DataGridRow>(hit)?.Item is not DataRowView selected)
        {
            e.Handled = true;
            return;
        }
        table.SelectedRow = selected;
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.Header = Equals(item.Tag, "open") ? table.OpenLabel : table.ChatOpenLabel;
    }

    private async void OnWorkflowTableOpenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ChatMessageViewModel { Table: { SelectedRow: { } row } table } })
            return;
        await OpenInteractionModuleAsync(table, row);
    }

    private async void OnWorkflowTableChatOpenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ChatMessageViewModel { Table: { SelectedRow: { } row } table } })
            return;
        await OpenInteractionInChatAsync(table, row);
    }

    private async void OnWorkflowRowForwardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DataRowView row } button ||
            FindAncestor<DataGrid>(button) is not
            {
                DataContext: ChatMessageViewModel { Table: { InteractionKind: not WorkflowRowInteractionKind.None } table }
            })
            return;
        table.SelectedRow = row;
        await OpenInteractionInChatAsync(table, row);
    }

    private async void OnOpenPrescriptionPresentationClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessageViewModel { Prescription: { } prescription } }) return;
        var source = prescription.IsLiveOnMedula ? "ai_chat" : "ai_chat_local_first";
        await OpenPrescriptionModuleAsync(new MedulaPrescriptionLocator(
            prescription.PrescriptionNumber,
            prescription.PatientTc,
            ElectronicPrescriptionNumber: prescription.ElectronicPrescriptionNumber,
            Source: source));
    }

    private async Task OpenInteractionInChatAsync(WorkflowTableViewModel table, DataRowView row)
    {
        rowInteractionCancellation?.Cancel();
        rowInteractionCancellation?.Dispose();
        rowInteractionCancellation = new CancellationTokenSource();
        var token = rowInteractionCancellation.Token;
        table.BeginDetailLoad(row, "Sohbet görünümü hazırlanıyor…");
        try
        {
            var detail = await LoadInteractionDetailAsync(table, row, token);
            if (detail is null) return;
            if (table.InteractionKind == WorkflowRowInteractionKind.Prescription)
            {
                var prescription = MedulaWorkflowPresentation.BuildPrescription(detail.Value.Output);
                if (prescription is null)
                {
                    table.SetDetailMessage("Reçete ayrıntısı görüntülenemedi.");
                    return;
                }
                var assistant = AddMessage(false,
                    $"{prescription.PrescriptionNumber} numaralı reçete sohbette açıldı.",
                    sender: "İş Akışı");
                assistant.SetPrescription(prescription);
            }
            else
            {
                var assistant = AddMessage(false, detail.Value.Message, sender: "İş Akışı");
                if (detail.Value.Table.Rows.Count > 0)
                    assistant.SetTable(detail.Value.Table, forceTable: true);
            }
            table.SetDetail(detail.Value.Table);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested) table.EndDetailLoad();
        }
    }

    private async Task LoadInlineRowDetailAsync(WorkflowTableViewModel table, DataRowView row)
    {
        var key = table.InteractionKey(row);
        if (key.Length == 0 || table.IsCurrentDetail(key)) return;
        rowInteractionCancellation?.Cancel();
        rowInteractionCancellation?.Dispose();
        rowInteractionCancellation = new CancellationTokenSource();
        var token = rowInteractionCancellation.Token;
        table.BeginDetailLoad(row, table.LoadingDetailText);
        try
        {
            var detail = await LoadInteractionDetailAsync(table, row, token);
            if (detail is null)
            {
                if (!token.IsCancellationRequested)
                    table.SetDetailMessage(table.EmptyDetailText);
                return;
            }
            table.SetDetail(detail.Value.Table);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
                table.SetDetailMessage("Ayrıntılar yüklenemedi.");
        }
        finally
        {
            if (!token.IsCancellationRequested) table.EndDetailLoad();
        }
    }

    private async Task<(JsonElement Output, AiWorkflowTablePresentation Table, string Message)?> LoadInteractionDetailAsync(
        WorkflowTableViewModel table,
        DataRowView row,
        CancellationToken cancellationToken)
    {
        var cacheKey = table.InteractionKey(row);
        if (table.TryGetCachedDetail(cacheKey, out var cached))
            return BuildInteractionDetail(table.InteractionKind, cached, row);

        var (tool, arguments) = table.InteractionKind switch
        {
            WorkflowRowInteractionKind.Prescription => (
                "medula.prescription_get",
                JsonSerializer.SerializeToElement(new
                {
                    prescription_number = RowValue(row, "prescription_number"),
                    patient_tc = EmptyToNull(RowValue(row, "patient_tc")),
                    electronic_prescription_number = EmptyToNull(RowValue(row, "electronic_prescription_number")),
                    medula_filter_date = ParseRowDate(RowValue(row, "medula_filter_date"))?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    invoice_type_code = EmptyToNull(RowValue(row, "invoice_type_code")),
                    // A synchronized local snapshot is both complete enough for the
                    // inline view and dramatically faster than reopening Medula.
                    prefer_local = RowBoolean(row, "is_stored_locally")
                })),
            WorkflowRowInteractionKind.Sale => (
                "sale.get_record",
                JsonSerializer.SerializeToElement(new { reference_code = RowValue(row, "reference_code") })),
            WorkflowRowInteractionKind.Product => (
                "product.get",
                JsonSerializer.SerializeToElement(new { barcode = RowValue(row, "barcode") })),
            WorkflowRowInteractionKind.QrMovement => (
                "qr.get_history",
                JsonSerializer.SerializeToElement(new { qr_code = RowValue(row, "qr_code"), limit = 200 })),
            _ => (string.Empty, JsonSerializer.SerializeToElement(new { }))
        };
        if (tool.Length == 0 || !workflowRuntime.ToolExecutor.HasTool(tool))
        {
            table.SetDetailMessage("Bu satır türü için ayrıntı görünümü kullanılamıyor.");
            return null;
        }

        var execution = await workflowRuntime.ToolExecutor.ExecuteAsync(
            new AiRequestContext(
                sessionId,
                $"Satır ayrıntısı: {cacheKey}",
                contextProvider(),
                new HashSet<string>()),
            tool,
            arguments,
            cancellationToken);
        if (!execution.IsSuccess)
        {
            table.SetDetailMessage(table.EmptyDetailText);
            return null;
        }
        table.CacheDetail(cacheKey, execution.Output);
        return BuildInteractionDetail(table.InteractionKind, execution.Output, row);
    }

    private static (JsonElement Output, AiWorkflowTablePresentation Table, string Message)? BuildInteractionDetail(
        WorkflowRowInteractionKind kind,
        JsonElement output,
        DataRowView row)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        string title;
        string message;
        IReadOnlyList<string> visible;
        switch (kind)
        {
            case WorkflowRowInteractionKind.Prescription:
                var prescription = MedulaWorkflowPresentation.BuildPrescription(output);
                if (prescription is null) return null;
                title = $"{prescription.PrescriptionNumber} · Reçete İçeriği";
                rows = prescription.Drugs.Select(drug =>
                    (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                    {
                        ["barcode"] = drug.Barcode,
                        ["medicine_name"] = drug.Name,
                        ["quantity"] = drug.Quantity,
                        ["usage"] = drug.Usage,
                        ["report"] = drug.Report,
                        ["available_date"] = drug.AvailableDate,
                        ["message"] = drug.Message
                    }).ToArray();
                visible = ["medicine_name", "barcode", "quantity", "usage", "report", "available_date", "message"];
                message = rows.Count == 0
                    ? $"{prescription.PrescriptionNumber} numaralı reçetede ilaç kaydı bulunamadı."
                    : $"{prescription.PrescriptionNumber} numaralı reçetede {rows.Count} ilaç satırı bulunuyor.";
                break;
            case WorkflowRowInteractionKind.Sale:
                if (!output.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True ||
                    !output.TryGetProperty("sale", out var sale) || sale.ValueKind != JsonValueKind.Object)
                    return null;
                var reference = WorkflowData.Text(sale, "reference_code");
                title = $"{reference} · Satış/İade İçeriği";
                rows = sale.TryGetProperty("lines", out var saleLines) && saleLines.ValueKind == JsonValueKind.Array
                    ? saleLines.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                        .Select(WorkflowData.FlatRow).Cast<IReadOnlyDictionary<string, string>>().ToArray()
                    : [];
                visible = ["product_name", "barcode", "qr_code", "gross_price", "discount_amount", "net_price", "profit"];
                message = rows.Count == 0
                    ? $"{reference} kaydında ürün satırı bulunamadı."
                    : $"{reference} kaydının {rows.Count} ürün satırı gösteriliyor.";
                break;
            case WorkflowRowInteractionKind.Product:
                if (!output.TryGetProperty("found", out var productFound) || productFound.ValueKind != JsonValueKind.True ||
                    !output.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object)
                    return null;
                rows = [WorkflowData.FlatRow(product)];
                title = $"{RowValue(row, "barcode")} · Ürün Bilgisi";
                visible = ["name", "barcode", "active_ingredient", "sale_price", "purchase_price", "public_price", "shelf_stock_quantity", "is_on_market"];
                message = "Ürün bilgisi gösteriliyor.";
                break;
            case WorkflowRowInteractionKind.QrMovement:
                title = $"{RowValue(row, "qr_code")} · Karekod Geçmişi";
                rows = output.TryGetProperty("movements", out var movements) && movements.ValueKind == JsonValueKind.Array
                    ? movements.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                        .Select(WorkflowData.FlatRow).Cast<IReadOnlyDictionary<string, string>>().ToArray()
                    : [];
                visible = ["movement_date", "medicine_name", "barcode", "movement_type", "direction", "address", "user", "reference_code"];
                message = rows.Count == 0 ? "Karekod hareketi bulunamadı." : $"{rows.Count} karekod hareketi gösteriliyor.";
                break;
            default:
                return null;
        }
        return (output, CreateDetailTable(title, rows, visible), message);
    }

    private async Task OpenInteractionModuleAsync(WorkflowTableViewModel table, DataRowView row)
    {
        switch (table.InteractionKind)
        {
            case WorkflowRowInteractionKind.Prescription:
                await OpenPrescriptionModuleAsync(row);
                break;
            case WorkflowRowInteractionKind.Sale:
                OpenSaleModule(row);
                break;
            case WorkflowRowInteractionKind.Product:
                OpenProductModule(row);
                break;
            case WorkflowRowInteractionKind.QrMovement:
                OpenQrMovementModule(row);
                break;
        }
    }

    private async Task OpenPrescriptionModuleAsync(DataRowView row)
    {
        var number = RowValue(row, "prescription_number");
        if (number.Length == 0) return;
        await OpenPrescriptionModuleAsync(new MedulaPrescriptionLocator(
            number,
            RowValue(row, "patient_tc"),
            ElectronicPrescriptionNumber: RowValue(row, "electronic_prescription_number"),
            MedulaFilterDate: ParseRowDate(RowValue(row, "medula_filter_date")),
            TypeValue: RowValue(row, "invoice_type_code"),
            Source: RowBoolean(row, "is_stored_locally")
                ? "ai_table_local_first"
                : "ai_table"));
    }

    private async Task OpenPrescriptionModuleAsync(MedulaPrescriptionLocator locator)
    {
        var number = locator.PrescriptionNumber;
        rowInteractionCancellation?.Cancel();
        rowInteractionCancellation?.Dispose();
        rowInteractionCancellation = new CancellationTokenSource();
        try
        {
            var snapshot = await medulaOperations.OpenPrescriptionAsync(locator, rowInteractionCancellation.Token);
            if (snapshot is null) return;
            var safeNumber = new string(number.Where(char.IsLetterOrDigit).ToArray());
            TabRequested?.Invoke(new ModuleTabRequest(
                $"medula.prescription-view.ai.{safeNumber}",
                $"Reçete {number}",
                () => new MedulaPrescriptionEntryView(snapshot),
                1180,
                680,
                CreatePrescriptionIcon));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpenSaleModule(DataRowView row)
    {
        var reference = RowValue(row, "reference_code").ToUpperInvariant();
        if (reference.Length < 2 || !int.TryParse(reference[1..], out var recordId)) return;
        var date = ParseRowDate(RowValue(row, "timestamp"))?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        var saleType = RowValue(row, "sale_type");
        var recordType = RowValue(row, "record_type");
        var transactionTypes = (saleType, recordType) switch
        {
            ("retail", "refund") => RetailTransactionTypes.RetailRefund,
            ("prescription", "refund") => RetailTransactionTypes.PrescriptionRefund,
            ("prescription", _) => RetailTransactionTypes.PrescriptionSale,
            _ => RetailTransactionTypes.RetailSale
        };
        var options = new RetailSalesListLaunchOptions(
            InitialSaleId: reference.StartsWith('P') ? recordId : null,
            InitialDate: date,
            StartDate: date,
            EndDate: date,
            TransactionTypes: transactionTypes);
        TabRequested?.Invoke(new ModuleTabRequest(
            $"sales.retail-list.ai.{Uri.EscapeDataString(reference)}",
            reference.StartsWith('G') ? $"İade {reference}" : $"Satış {reference}",
            () => new RetailSalesListView(options),
            1180,
            650));
    }

    private void OpenProductModule(DataRowView row)
    {
        var barcode = RowValue(row, "barcode");
        if (barcode.Length == 0) return;
        TabRequested?.Invoke(new ModuleTabRequest(
            $"products.catalog.ai.{Uri.EscapeDataString(barcode)}",
            "Ürün Kataloğu",
            () => new ProductCatalogView(new ProductCatalogLaunchOptions(Barcodes: [barcode])),
            1050,
            620));
    }

    private void OpenQrMovementModule(DataRowView row)
    {
        var qrCode = RowValue(row, "qr_code");
        if (qrCode.Length == 0) return;
        var options = new QrMovementsLaunchOptions(
            DateTime.Today,
            DateTime.Today,
            PeriodTitle: string.Empty,
            Search: qrCode,
            IgnoreDateFilter: true);
        TabRequested?.Invoke(new ModuleTabRequest(
            $"products.qr-movements.ai.{Uri.EscapeDataString(qrCode)}",
            "Karekod Hareketleri",
            () => new QrMovementsView(options),
            1050,
            560));
    }

    private static AiWorkflowTablePresentation CreateDetailTable(
        string title,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        IReadOnlyList<string> visible)
    {
        var keys = rows.SelectMany(value => value.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var visibleSet = visible.ToHashSet(StringComparer.Ordinal);
        var columns = keys.Select(key => new AiWorkflowTableColumn(
            key,
            WorkflowData.Label(key),
            visibleSet.Count == 0 || visibleSet.Contains(key))).ToArray();
        return new AiWorkflowTablePresentation(title, columns, rows, [], string.Empty, rows.Count);
    }

    private static string RowValue(DataRowView row, string key) =>
        row.Row.Table.Columns.Contains(key)
            ? Convert.ToString(row.Row[key], CultureInfo.CurrentCulture)?.Trim() ?? string.Empty
            : string.Empty;

    private static bool RowBoolean(DataRowView row, string key) =>
        bool.TryParse(RowValue(row, key), out var value) && value;

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static DateOnly? ParseRowDate(string value)
    {
        foreach (var format in new[] { "dd.MM.yyyy HH:mm", "dd.MM.yyyy", "dd/MM/yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return DateOnly.FromDateTime(parsed);
        return DateTime.TryParse(value, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var fallback)
            ? DateOnly.FromDateTime(fallback)
            : null;
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

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T result) return result;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static DateTime ParseActionDate(AiWorkflowModuleAction action, string key, DateTime fallback) =>
        DateTime.TryParseExact(action.Parameters.GetValueOrDefault(key), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value) ? value : fallback;

    private static IReadOnlyList<string> SplitActionValue(AiWorkflowModuleAction action, string key) =>
        (action.Parameters.GetValueOrDefault(key) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool? ParseNullableBoolean(string? value) => bool.TryParse(value, out var parsed) ? parsed : null;

    private static decimal? ParseNullableDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string FilteredModuleId(
        string moduleId,
        DateTime start,
        DateTime end,
        IReadOnlyList<string> barcodes)
    {
        var identity = string.Join('-', barcodes.Take(2));
        return $"{moduleId}.ai.{start:yyyyMMdd}.{end:yyyyMMdd}.{barcodes.Count}.{Uri.EscapeDataString(identity)}";
    }

    private async void OnWorkflowSelectionCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessageViewModel message } ||
            string.IsNullOrWhiteSpace(message.WorkflowSelectionId) ||
            requestCancellation is not null)
            return;
        var selected = message.WorkflowSelectionOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Id)
            .ToArray();
        if (selected.Length == 0)
        {
            message.WorkflowSelectionError = message.WorkflowSelectionEmptyError;
            return;
        }

        var debugMode = RefreshDebugControls();
        var loadingMessage = AddLoadingMessage("İş akışı seçiminizle devam ediyor…", debugMode);
        var progress = CreateProgress(loadingMessage, debugMode);
        SetBusy(true);
        requestCancellation = new CancellationTokenSource();
        try
        {
            var result = await workflowRuntime.Chat.ContinueSelectionAsync(
                message.WorkflowSelectionId,
                contextProvider().CurrentUserId ?? string.Empty,
                selected,
                requestCancellation.Token,
                progress);
            message.ConsumeWorkflowSelection();
            RemoveMessage(loadingMessage);
            Present(result);
        }
        catch (OperationCanceledException)
        {
            RemoveMessage(loadingMessage);
            AddMessage(false, "İstek iptal edildi.");
        }
        catch (Exception)
        {
            RemoveMessage(loadingMessage);
            AddMessage(false, "İş akışı seçimi işlenirken beklenmeyen bir hata oluştu.");
        }
        finally
        {
            RemoveMessage(loadingMessage);
            requestCancellation?.Dispose();
            requestCancellation = null;
            SetBusy(false);
        }
    }

    private void OnSelectAllWorkflowOptionsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessageViewModel message }) message.SelectAllWorkflowOptions();
    }

    private void OnClearWorkflowOptionsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessageViewModel message }) message.ClearWorkflowOptions();
    }

    private void OnTogglePassiveWorkflowOptionsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessageViewModel message }) message.TogglePassiveWorkflowOptions();
    }

    private void OnToggleHiddenWorkflowColumnsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessageViewModel { Table: not null } message }) return;
        message.Table.ToggleHiddenColumns();
        foreach (var grid in FindVisualChildren<DataGrid>(MessageList)
                     .Where(grid => ReferenceEquals(grid.DataContext, message)))
            foreach (var column in grid.Columns)
                column.Visibility = message.Table.IsColumnVisible(column.SortMemberPath)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
    }

    private void OnShowWorkflowTableClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessageViewModel message }) message.ShowTableView();
    }

    private void OnShowWorkflowCardsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessageViewModel message }) message.ShowCardView();
    }

    private void OnMessageBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        _ = SendCurrentAsync();
    }

    private void OnMessageListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        messageScrollViewer ??= FindVisualChild<ScrollViewer>(MessageList);
        if (messageScrollViewer is null || messageScrollViewer.ScrollableHeight <= 0) return;
        var origin = isSmoothScrolling ? smoothScrollTarget : messageScrollViewer.VerticalOffset;
        smoothScrollTarget = Math.Clamp(origin - e.Delta * 0.42, 0, messageScrollViewer.ScrollableHeight);
        if (!isSmoothScrolling)
        {
            isSmoothScrolling = true;
            smoothScrollFrameAt = DateTime.UtcNow;
            CompositionTarget.Rendering += OnSmoothScrollFrame;
        }
        e.Handled = true;
    }

    private void OnSmoothScrollFrame(object? sender, EventArgs e)
    {
        if (messageScrollViewer is null)
        {
            StopSmoothScrolling();
            return;
        }
        var now = DateTime.UtcNow;
        var elapsedSeconds = Math.Clamp((now - smoothScrollFrameAt).TotalSeconds, 0, 0.05);
        smoothScrollFrameAt = now;
        var remaining = smoothScrollTarget - messageScrollViewer.VerticalOffset;
        if (Math.Abs(remaining) < 0.5)
        {
            messageScrollViewer.ScrollToVerticalOffset(smoothScrollTarget);
            StopSmoothScrolling();
            return;
        }
        var easing = 1 - Math.Exp(-18 * elapsedSeconds);
        messageScrollViewer.ScrollToVerticalOffset(messageScrollViewer.VerticalOffset + remaining * easing);
    }

    private void StopSmoothScrolling()
    {
        if (!isSmoothScrolling) return;
        CompositionTarget.Rendering -= OnSmoothScrollFrame;
        isSmoothScrolling = false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private ChatMessageViewModel AddMessage(
        bool user,
        string text,
        string? debugText = null,
        string? model = null,
        string? sender = null)
    {
        var message = new ChatMessageViewModel(user, text, sender)
        {
            DebugText = debugText ?? string.Empty,
            ModelText = string.IsNullOrWhiteSpace(model) ? string.Empty : "Model: " + model
        };
        Messages.Add(message);
        WelcomePanel.Visibility = Visibility.Collapsed;
        StopSmoothScrolling();
        MessageList.ScrollIntoView(message);
        return message;
    }

    private ChatMessageViewModel AddLoadingMessage(string text, bool debugMode)
    {
        var message = AddMessage(false, text);
        message.IsLoading = true;
        if (debugMode) message.AppendDebug("Debug modu etkin. Operasyonel akış izleniyor.");
        return message;
    }

    private IProgress<AiActivityUpdate> CreateProgress(ChatMessageViewModel loadingMessage, bool debugMode) =>
        new Progress<AiActivityUpdate>(update =>
        {
            loadingMessage.Text = update.Message;
            if (debugMode && !string.IsNullOrWhiteSpace(update.Model))
                loadingMessage.ModelText = "Model: " + update.Model;
            if (debugMode && !string.IsNullOrWhiteSpace(update.DebugDetail))
                loadingMessage.AppendDebug(update.DebugDetail);
            if (Messages.Contains(loadingMessage)) MessageList.ScrollIntoView(loadingMessage);
        });

    private void RemoveMessage(ChatMessageViewModel message)
    {
        if (Messages.Contains(message)) Messages.Remove(message);
    }

    private bool RefreshDebugControls()
    {
        var debug = runtime.Configuration.GetCurrent().DebugMode;
        if (!debug && legacyMode)
        {
            legacyMode = false;
            ResetConversation();
        }
        LegacyAiButton.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsButton.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
        MissingToolsButton.Visibility = debug && legacyMode ? Visibility.Visible : Visibility.Collapsed;
        ModelSelectorBorder.Visibility = debug && !legacyMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateModeUi();
        return debug;
    }

    private void UpdateModeUi()
    {
        AssistantTitle.Text = legacyMode
            ? "Legacy AI"
            : "Asistan";
        LegacyAiButton.Content = "Legacy AI";
        LegacyAiButton.Foreground = Brushes.Gray;
        ModelSelectorBorder.Visibility = runtime.Configuration.GetCurrent().DebugMode && !legacyMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string SelectedRouterModel() =>
        RouterModelSelector.SelectedItem is AiRouterModelOption option
            ? option.Id
            : AiRouterModelCatalog.DefaultModelId;

    private bool HasPendingWorkflowSelection() =>
        Messages.Any(message => !string.IsNullOrWhiteSpace(message.WorkflowSelectionId));

    private void SetBusy(bool busy)
    {
        var canCompose = !busy && !HasPendingWorkflowSelection();
        SendButton.IsEnabled = canCompose;
        MessageBox.IsEnabled = canCompose;
        RouterModelSelector.IsEnabled = canCompose;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    private string FormatWorkflowFooter(
        IReadOnlyList<AiModelUsage>? usageByModel,
        IReadOnlyList<string>? workflowQueue)
    {
        var usage = FormatUsage(usageByModel);
        var configuration = runtime.Configuration.GetCurrent();
        if (!configuration.DebugMode || !configuration.DebugShowWorkflowFunctions || workflowQueue is not { Count: > 0 })
            return usage;
        var queue = "Workflow: [" + string.Join(" → ", workflowQueue.Select(name =>
            workflowRuntime.Registry.Get(name)?.Definition.DisplayFunctionName ?? name)) + "]";
        return usage.Length == 0 ? queue : usage + " | " + queue;
    }
    private static string FormatUsage(IReadOnlyList<AiModelUsage>? usageByModel)
    {
        if (usageByModel is not { Count: > 0 }) return string.Empty;
        return string.Join(Environment.NewLine, usageByModel.Select(item =>
        {
            var usage = item.Usage;
            return $"{pharmacyApp.Localization.UiText.Translate(item.Stage)} · {item.Model} — Input: {usage.InputTokens:N0} " +
                   $"(cached: {usage.CachedInputTokens:N0}, cache writes: {usage.CacheWriteTokens:N0}) | " +
                   $"Output: {usage.OutputTokens:N0} (reasoning: {usage.ReasoningTokens:N0}) | " +
                   $"Total: {usage.InputTokens + usage.OutputTokens:N0}";
        }));
    }

}

public sealed class ChatMessageViewModel : INotifyPropertyChanged
{
    private string text;
    private string debugText = string.Empty;
    private string footerText = string.Empty;
    private string modelText = string.Empty;
    private string workflowSelectionId = string.Empty;
    private string workflowSelectionError = string.Empty;
    private string workflowSelectionSearchText = string.Empty;
    private bool isLoading;
    private WorkflowTableViewModel? table;
    private WorkflowContentViewModel? workflowContent;
    private AiWorkflowPrescriptionPresentation? prescription;
    private bool workflowSelectionAllowsMultiple;
    private bool showPassiveWorkflowOptions;
    private bool isPatientWorkflowSelection;
    private bool isWorkflowBranchSelection;
    private bool showResultAsTable = true;

    public ChatMessageViewModel(bool isUser, string text, string? sender = null)
    {
        IsUser = isUser;
        this.text = text;
        Sender = sender ?? (isUser ? "Siz" : "Yapay Zeka Asistanı");
        Alignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var isWorkflow = !isUser && string.Equals(Sender, "İş Akışı", StringComparison.Ordinal);
        Background = new SolidColorBrush(isUser
            ? Color.FromRgb(232, 238, 242)
            : isWorkflow ? Color.FromRgb(248, 248, 248) : Color.FromRgb(243, 244, 245));
        Foreground = new SolidColorBrush(Color.FromRgb(32, 37, 43));
        MutedForeground = new SolidColorBrush(Color.FromRgb(94, 103, 112));
        WorkflowSelectionView = CollectionViewSource.GetDefaultView(WorkflowSelectionOptions);
        WorkflowSelectionView.Filter = value => value is WorkflowSelectionOptionViewModel option && IsWorkflowOptionVisible(option);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsUser { get; }
    public ObservableCollection<AiSuggestedAction> SuggestedActions { get; } = [];
    public ObservableCollection<AiWorkflowModuleAction> ModuleActions { get; } = [];
    public ObservableCollection<AiRequestCaptureFile> RequestCaptureFiles { get; } = [];
    public ObservableCollection<WorkflowSelectionOptionViewModel> WorkflowSelectionOptions { get; } = [];
    public ICollectionView WorkflowSelectionView { get; }
    public WorkflowTableViewModel? Table
    {
        get => table;
        private set
        {
            if (ReferenceEquals(table, value)) return;
            table = value;
            Notify();
            Notify(nameof(TableVisibility));
            Notify(nameof(BubbleMaxWidth));
            Notify(nameof(BubbleAlignment));
        }
    }
    public WorkflowContentViewModel? WorkflowContent
    {
        get => workflowContent;
        private set
        {
            if (ReferenceEquals(workflowContent, value)) return;
            workflowContent = value;
            Notify();
            Notify(nameof(WorkflowContentVisibility));
            Notify(nameof(BubbleMaxWidth));
            Notify(nameof(BubbleAlignment));
        }
    }
    public AiWorkflowPrescriptionPresentation? Prescription
    {
        get => prescription;
        private set
        {
            if (ReferenceEquals(prescription, value)) return;
            prescription = value;
            Notify();
            Notify(nameof(PrescriptionVisibility));
            Notify(nameof(BubbleMaxWidth));
            Notify(nameof(BubbleAlignment));
        }
    }
    public string Text
    {
        get => text;
        set { if (text == value) return; text = value; Notify(); }
    }
    public string DebugText
    {
        get => debugText;
        set
        {
            if (debugText == value) return;
            debugText = value;
            Notify();
            Notify(nameof(DebugVisibility));
        }
    }
    public string FooterText
    {
        get => footerText;
        set
        {
            if (footerText == value) return;
            footerText = value;
            Notify();
            Notify(nameof(FooterVisibility));
            Notify(nameof(UsageFooterText));
            Notify(nameof(WorkflowFooterText));
        }
    }
    private int WorkflowFooterStart => FooterText.IndexOf("Workflow: [", StringComparison.Ordinal);
    public string UsageFooterText => WorkflowFooterStart < 0 ? FooterText : FooterText[..WorkflowFooterStart];
    public string WorkflowFooterText => WorkflowFooterStart < 0 ? string.Empty : FooterText[WorkflowFooterStart..];
    public string ModelText
    {
        get => modelText;
        set
        {
            if (modelText == value) return;
            modelText = value;
            Notify();
            Notify(nameof(ModelVisibility));
        }
    }
    public string WorkflowSelectionId
    {
        get => workflowSelectionId;
        private set
        {
            if (workflowSelectionId == value) return;
            workflowSelectionId = value;
            Notify();
            Notify(nameof(WorkflowSelectionVisibility));
        }
    }
    public string WorkflowSelectionError
    {
        get => workflowSelectionError;
        set
        {
            if (workflowSelectionError == value) return;
            workflowSelectionError = value;
            Notify();
            Notify(nameof(WorkflowSelectionErrorVisibility));
        }
    }
    public string WorkflowSelectionSearchText
    {
        get => workflowSelectionSearchText;
        set
        {
            if (workflowSelectionSearchText == value) return;
            workflowSelectionSearchText = value;
            Notify();
            RefreshWorkflowSelectionView();
        }
    }
    public bool IsLoading
    {
        get => isLoading;
        set
        {
            if (isLoading == value) return;
            isLoading = value;
            Notify();
            Notify(nameof(LoadingVisibility));
        }
    }
    public string Sender { get; }
    public HorizontalAlignment Alignment { get; }
    public HorizontalAlignment BubbleAlignment => Prescription is not null || Table is not null || WorkflowContent is not null || WorkflowSelectionColumnCount > 1
        ? HorizontalAlignment.Stretch
        : Alignment;
    public Brush Background { get; }
    public Brush Foreground { get; }
    public Brush MutedForeground { get; }
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DebugVisibility => string.IsNullOrWhiteSpace(DebugText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FooterVisibility => string.IsNullOrWhiteSpace(FooterText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelVisibility => string.IsNullOrWhiteSpace(ModelText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActionsVisibility => SuggestedActions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModuleActionsVisibility => ModuleActions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RequestCaptureFilesVisibility => RequestCaptureFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WorkflowSelectionVisibility => string.IsNullOrWhiteSpace(WorkflowSelectionId)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility WorkflowSelectionErrorVisibility => string.IsNullOrWhiteSpace(WorkflowSelectionError)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility TableVisibility => Table is not null && (WorkflowContent is null || showResultAsTable)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkflowContentVisibility => WorkflowContent is not null && (Table is null || !showResultAsTable)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PrescriptionVisibility => Prescription is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ResultViewOptionsVisibility => Table is not null && WorkflowContent is not null
        ? Visibility.Visible : Visibility.Collapsed;
    public Brush TableViewButtonBackground => showResultAsTable ? new SolidColorBrush(Color.FromRgb(49, 90, 115)) : Brushes.White;
    public Brush TableViewButtonForeground => showResultAsTable ? Brushes.White : new SolidColorBrush(Color.FromRgb(72, 96, 107));
    public Brush CardViewButtonBackground => !showResultAsTable ? new SolidColorBrush(Color.FromRgb(49, 90, 115)) : Brushes.White;
    public Brush CardViewButtonForeground => !showResultAsTable ? Brushes.White : new SolidColorBrush(Color.FromRgb(72, 96, 107));
    public double BubbleMaxWidth => Prescription is null && Table is null && WorkflowContent is null && WorkflowSelectionColumnCount <= 1 ? 760 : 1600;
    public string WorkflowSelectionTitle => isWorkflowBranchSelection
        ? "Nasıl devam edilsin?"
        : isPatientWorkflowSelection ? "Hasta seçimi" : "Ürün seçimi";
    public string WorkflowSelectionSearchHint => isPatientWorkflowSelection
        ? "Hasta adı veya T.C. kimlik no ara"
        : "Ürün adı veya barkod ara";
    public string WorkflowSelectionEmptyError => isPatientWorkflowSelection
        ? "Bir hasta seçin."
        : isWorkflowBranchSelection ? "Bir seçenek belirleyin." : "En az bir ilaç seçin.";
    public string WorkflowSelectionConfirmText => isWorkflowBranchSelection ? "Devam Et" : "Seçimi Onayla";
    public Visibility WorkflowSelectionToolsVisibility => isWorkflowBranchSelection ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WorkflowBranchSelectionVisibility => isWorkflowBranchSelection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkflowPickerVisibility => isWorkflowBranchSelection ? Visibility.Collapsed : Visibility.Visible;
    public string WorkflowSelectionSummary
    {
        get
        {
            var selected = WorkflowSelectionOptions.Count(option => option.IsSelected);
            var visible = WorkflowSelectionOptions.Count(IsWorkflowOptionVisible);
            var visibleText = visible == WorkflowSelectionOptions.Count
                ? $"{visible} eşleşme"
                : $"{visible} gösteriliyor · {WorkflowSelectionOptions.Count} eşleşme";
            if (isWorkflowBranchSelection)
                return selected == 0 ? $"{visible} seçenek · birini seçin" : $"{selected} seçenek seçildi";
            return selected == 0
                ? $"{visibleText} · seçim yapın"
                : $"{visibleText} · {selected} {(isPatientWorkflowSelection ? "hasta" : "ürün")} seçildi";
        }
    }
    public int WorkflowSelectionColumnCount
    {
        get
        {
            var visible = WorkflowSelectionOptions.Count(IsWorkflowOptionVisible);
            return visible > 12 ? 3 : visible > 6 ? 2 : 1;
        }
    }
    public string PassiveToggleText => showPassiveWorkflowOptions ? "Pasifleri gizle" : "Pasifleri göster";
    public Visibility PassiveToggleVisibility => WorkflowSelectionOptions.Any(option => !option.IsOnMarket)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility MultiSelectionVisibility => workflowSelectionAllowsMultiple
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void AppendDebug(string detail)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {detail}";
        DebugText = string.IsNullOrWhiteSpace(DebugText) ? line : DebugText + Environment.NewLine + line;
    }

    public void AddSuggestedAction(AiSuggestedAction action)
    {
        SuggestedActions.Add(action);
        Notify(nameof(ActionsVisibility));
    }

    public void ConsumeSuggestedActions()
    {
        SuggestedActions.Clear();
        Notify(nameof(ActionsVisibility));
    }

    public void AddModuleAction(AiWorkflowModuleAction action)
    {
        ModuleActions.Add(action);
        Notify(nameof(ModuleActionsVisibility));
    }

    public void AddRequestCapture(AiRequestCaptureFile capture)
    {
        RequestCaptureFiles.Add(capture);
        Notify(nameof(RequestCaptureFilesVisibility));
    }

    public void SetWorkflowSelection(AiWorkflowSelectionView selection)
    {
        WorkflowSelectionOptions.Clear();
        foreach (var option in selection.Options)
        {
            var item = WorkflowSelectionOptionViewModel.From(option);
            item.PropertyChanged += OnWorkflowOptionChanged;
            WorkflowSelectionOptions.Add(item);
        }
        workflowSelectionAllowsMultiple = selection.AllowsMultiple;
        isPatientWorkflowSelection = selection.Options.Any(option => option.Data.TryGetProperty("patient_record_id", out _));
        isWorkflowBranchSelection = selection.Options.Any(option => option.Data.TryGetProperty("queue", out _));
        showPassiveWorkflowOptions = false;
        workflowSelectionSearchText = string.Empty;
        WorkflowSelectionError = string.Empty;
        WorkflowSelectionId = selection.Id;
        Notify(nameof(WorkflowSelectionSearchText));
        Notify(nameof(MultiSelectionVisibility));
        Notify(nameof(WorkflowSelectionTitle));
        Notify(nameof(WorkflowSelectionSearchHint));
        Notify(nameof(WorkflowSelectionEmptyError));
        Notify(nameof(WorkflowSelectionConfirmText));
        Notify(nameof(WorkflowSelectionToolsVisibility));
        Notify(nameof(WorkflowBranchSelectionVisibility));
        Notify(nameof(WorkflowPickerVisibility));
        Notify(nameof(PassiveToggleText));
        Notify(nameof(PassiveToggleVisibility));
        RefreshWorkflowSelectionView();
    }

    public void ConsumeWorkflowSelection()
    {
        foreach (var option in WorkflowSelectionOptions) option.PropertyChanged -= OnWorkflowOptionChanged;
        WorkflowSelectionId = string.Empty;
        WorkflowSelectionError = string.Empty;
    }

    public void SelectAllWorkflowOptions()
    {
        if (!workflowSelectionAllowsMultiple) return;
        foreach (var option in WorkflowSelectionOptions.Where(IsWorkflowOptionVisible)) option.IsSelected = true;
    }

    public void ClearWorkflowOptions()
    {
        foreach (var option in WorkflowSelectionOptions) option.IsSelected = false;
    }

    public void TogglePassiveWorkflowOptions()
    {
        showPassiveWorkflowOptions = !showPassiveWorkflowOptions;
        Notify(nameof(PassiveToggleText));
        RefreshWorkflowSelectionView();
    }

    private bool IsWorkflowOptionVisible(WorkflowSelectionOptionViewModel option)
    {
        if (!showPassiveWorkflowOptions && !option.IsOnMarket) return false;
        var search = workflowSelectionSearchText.Trim();
        return search.Length == 0 ||
               option.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               option.Barcode.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshWorkflowSelectionView()
    {
        WorkflowSelectionView.Refresh();
        Notify(nameof(WorkflowSelectionSummary));
        Notify(nameof(WorkflowSelectionColumnCount));
        Notify(nameof(BubbleMaxWidth));
        Notify(nameof(BubbleAlignment));
    }

    private void OnWorkflowOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkflowSelectionOptionViewModel.IsSelected)) return;
        if (!workflowSelectionAllowsMultiple && sender is WorkflowSelectionOptionViewModel { IsSelected: true } selected)
            foreach (var option in WorkflowSelectionOptions.Where(option => !ReferenceEquals(option, selected)))
                option.IsSelected = false;
        WorkflowSelectionError = string.Empty;
        Notify(nameof(WorkflowSelectionSummary));
    }

    public void SetTable(AiWorkflowTablePresentation presentation, bool forceTable = false)
    {
        Table = new WorkflowTableViewModel(presentation);
        if (WorkflowContent is null) WorkflowContent = WorkflowContentViewModel.FromTable(Table);
        showResultAsTable = forceTable || presentation.Rows.Count != 1;
        NotifyResultView();
    }

    public void SetContent(AiWorkflowContentPresentation presentation)
    {
        WorkflowContent = new WorkflowContentViewModel(presentation);
        if (Table is null) Table = WorkflowTableViewModel.FromContent(presentation);
        showResultAsTable = presentation.Items.Count != 1;
        NotifyResultView();
    }

    public void SetPrescription(AiWorkflowPrescriptionPresentation presentation) => Prescription = presentation;

    public void ShowTableView()
    {
        if (Table is null) return;
        showResultAsTable = true;
        NotifyResultView();
    }

    public void ShowCardView()
    {
        if (WorkflowContent is null) return;
        showResultAsTable = false;
        NotifyResultView();
    }

    private void NotifyResultView()
    {
        Notify(nameof(TableVisibility));
        Notify(nameof(WorkflowContentVisibility));
        Notify(nameof(ResultViewOptionsVisibility));
        Notify(nameof(TableViewButtonBackground));
        Notify(nameof(TableViewButtonForeground));
        Notify(nameof(CardViewButtonBackground));
        Notify(nameof(CardViewButtonForeground));
        Notify(nameof(BubbleMaxWidth));
        Notify(nameof(BubbleAlignment));
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class WorkflowContentViewModel
{
    public WorkflowContentViewModel(AiWorkflowContentPresentation presentation)
    {
        Title = presentation.Title;
        Kind = presentation.Kind;
        Items = new ObservableCollection<AiWorkflowContentItem>(presentation.Items);
    }

    public string Title { get; }
    public AiWorkflowContentKind Kind { get; }
    public ObservableCollection<AiWorkflowContentItem> Items { get; }

    public static WorkflowContentViewModel FromTable(WorkflowTableViewModel table)
    {
        var titleKeys = new[] { "medicine_name", "product_name", "name", "group_label", "patient_name", "reference_code", "barcode", "period" };
        var subtitleKeys = new[] { "barcode", "timestamp", "movement_date", "start_date", "end_date_inclusive" };
        var items = table.Rows.Cast<DataRowView>().Select((view, index) =>
        {
            string Value(string key) => table.Columns.Any(column => column.Key == key)
                ? Convert.ToString(view.Row[key]) ?? string.Empty : string.Empty;
            var titleKey = titleKeys.FirstOrDefault(key => Value(key).Length > 0);
            var title = titleKey is null ? $"{index + 1}. kayıt" : Value(titleKey);
            var subtitleKey = subtitleKeys.FirstOrDefault(key => key != titleKey && Value(key).Length > 0);
            var subtitle = subtitleKey is null ? null : Value(subtitleKey);
            var highlight = table.HighlightField is { Length: > 0 } && Value(table.HighlightField).Length > 0
                ? table.Columns.FirstOrDefault(column => column.Key == table.HighlightField)
                : null;
            var emphasized = highlight is null ? null : $"{highlight.Label}: {Value(highlight.Key)}";
            var detail = string.Join(Environment.NewLine, table.Columns
                .Where(column => column.IsVisibleByDefault && column.Key != titleKey && column.Key != subtitleKey && column.Key != highlight?.Key)
                .Select(column => (column.Label, Value: Value(column.Key)))
                .Where(item => item.Value.Length > 0)
                .Select(item => $"{item.Label}: {item.Value}"));
            return new AiWorkflowContentItem(title, subtitle, emphasized, detail);
        }).ToArray();
        return new WorkflowContentViewModel(new AiWorkflowContentPresentation(
            AiWorkflowContentKind.InformationCards, table.Title, items));
    }
}

public enum WorkflowRowInteractionKind
{
    None,
    Prescription,
    Sale,
    Product,
    QrMovement
}

public sealed class WorkflowTableViewModel : INotifyPropertyChanged
{
    private const string ColorColumn = "__row_color";
    private const string SearchColumn = "__search_text";
    private WorkflowTableColorModeViewModel? selectedColorMode;
    private string searchText = string.Empty;
    private bool showHiddenColumns;
    private readonly DataTable data = new();
    private readonly int totalRowCount;
    private readonly Dictionary<string, JsonElement> detailCache = new(StringComparer.Ordinal);
    private WorkflowTableViewModel? inlineDetail;
    private DataRowView? selectedRow;
    private string currentDetailKey = string.Empty;
    private string detailTitle = string.Empty;
    private string detailMessage = string.Empty;
    private bool detailLoading;

    public WorkflowTableViewModel(AiWorkflowTablePresentation presentation)
    {
        Title = presentation.Title;
        Columns = presentation.Columns;
        HighlightField = presentation.HighlightField;
        RowBackgroundField = presentation.RowBackgroundField;
        InteractionKind = InferInteractionKind(presentation.Columns);
        totalRowCount = Math.Max(presentation.TotalRowCount ?? presentation.Rows.Count, presentation.Rows.Count);
        data.Locale = CultureInfo.GetCultureInfo("tr-TR");
        data.CaseSensitive = false;
        foreach (var column in Columns) data.Columns.Add(column.Key, typeof(string));
        data.Columns.Add(ColorColumn, typeof(Brush));
        data.Columns.Add(SearchColumn, typeof(string));
        foreach (var source in presentation.Rows)
        {
            var row = data.NewRow();
            foreach (var column in Columns) row[column.Key] = FormatCell(column.Key, source.GetValueOrDefault(column.Key));
            row[ColorColumn] = DefaultRowBackground(row);
            row[SearchColumn] = string.Join(' ', Columns.Select(column => Convert.ToString(row[column.Key]) ?? string.Empty));
            data.Rows.Add(row);
        }
        ColorModes = new ObservableCollection<WorkflowTableColorModeViewModel>(
            new[] { new WorkflowTableColorModeViewModel(new AiWorkflowTableColorMode("__none", "Yok", string.Empty, [])) }
                .Concat(presentation.ColorModes.Select(mode => new WorkflowTableColorModeViewModel(mode))));
        SelectedColorMode = ColorModes.FirstOrDefault(mode => mode.Id == presentation.DefaultColorMode)
                            ?? ColorModes.First();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get; }
    public string? HighlightField { get; }
    public string? RowBackgroundField { get; }
    public WorkflowRowInteractionKind InteractionKind { get; }
    public IReadOnlyList<AiWorkflowTableColumn> Columns { get; }
    public DataView Rows => data.DefaultView;
    public ObservableCollection<WorkflowTableColorModeViewModel> ColorModes { get; }
    public WorkflowTableViewModel? InlineDetail
    {
        get => inlineDetail;
        private set
        {
            if (ReferenceEquals(inlineDetail, value)) return;
            inlineDetail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InlineDetail)));
            NotifyDetailVisibility();
        }
    }
    public DataRowView? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (ReferenceEquals(selectedRow, value)) return;
            selectedRow = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRow)));
        }
    }
    public string DetailTitle
    {
        get => detailTitle;
        private set
        {
            if (detailTitle == value) return;
            detailTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailTitle)));
        }
    }
    public string DetailMessage
    {
        get => detailMessage;
        private set
        {
            if (detailMessage == value) return;
            detailMessage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailMessage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailMessageVisibility)));
            NotifyDetailVisibility();
        }
    }
    public Visibility DetailVisibility => InteractionKind != WorkflowRowInteractionKind.None &&
        (detailLoading || InlineDetail is not null || DetailMessage.Length > 0)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility DetailGridVisibility => InlineDetail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailMessageVisibility => DetailMessage.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailLoadingVisibility => detailLoading ? Visibility.Visible : Visibility.Collapsed;
    public string OpenLabel => InteractionKind switch
    {
        WorkflowRowInteractionKind.Prescription => "Reçeteyi Aç",
        WorkflowRowInteractionKind.Sale => "Satışı/İadeyi Aç",
        WorkflowRowInteractionKind.Product => "Ürünü Aç",
        WorkflowRowInteractionKind.QrMovement => "Karekod Hareketlerini Aç",
        _ => "Ekranda Aç"
    };
    public string ChatOpenLabel => InteractionKind switch
    {
        WorkflowRowInteractionKind.Prescription => "Reçeteyi Sohbette Aç",
        WorkflowRowInteractionKind.Sale => "Satışı/İadeyi Sohbette Aç",
        WorkflowRowInteractionKind.Product => "Ürünü Sohbette Aç",
        WorkflowRowInteractionKind.QrMovement => "Karekod Geçmişini Sohbette Aç",
        _ => "Sohbette Aç"
    };
    public string LoadingDetailText => InteractionKind switch
    {
        WorkflowRowInteractionKind.Prescription => "Reçete içeriği yükleniyor…",
        WorkflowRowInteractionKind.Sale => "Satış içeriği yükleniyor…",
        WorkflowRowInteractionKind.Product => "Ürün bilgisi yükleniyor…",
        WorkflowRowInteractionKind.QrMovement => "Karekod geçmişi yükleniyor…",
        _ => "Ayrıntılar yükleniyor…"
    };
    public string EmptyDetailText => InteractionKind switch
    {
        WorkflowRowInteractionKind.Prescription => "Bu reçetede ilaç kaydı bulunamadı.",
        WorkflowRowInteractionKind.Sale => "Bu satış/iade kaydında ürün satırı bulunamadı.",
        WorkflowRowInteractionKind.Product => "Ürün ayrıntısı bulunamadı.",
        WorkflowRowInteractionKind.QrMovement => "Karekod hareketi bulunamadı.",
        _ => "Ayrıntı bulunamadı."
    };

    public static WorkflowTableViewModel FromContent(AiWorkflowContentPresentation presentation)
    {
        var rows = presentation.Items.Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["title"] = item.Title,
            ["subtitle"] = item.Subtitle ?? string.Empty,
            ["value"] = item.Value ?? string.Empty,
            ["detail"] = item.Detail ?? string.Empty
        }).ToArray();
        return new WorkflowTableViewModel(new AiWorkflowTablePresentation(
            presentation.Title,
            [
                new AiWorkflowTableColumn("title", "Başlık"),
                new AiWorkflowTableColumn("subtitle", "Alt Başlık"),
                new AiWorkflowTableColumn("value", "Öne Çıkan Değer"),
                new AiWorkflowTableColumn("detail", "Açıklama")
            ],
            rows, [], string.Empty, rows.Length, "value"));
    }
    public string RowCountText
    {
        get
        {
            if (Rows.Count != data.Rows.Count)
                return totalRowCount == data.Rows.Count
                    ? $"{Rows.Count:N0} / {data.Rows.Count:N0} kayıt"
                    : $"{Rows.Count:N0} / {data.Rows.Count:N0} gösteriliyor · toplam {totalRowCount:N0}";
            return totalRowCount == data.Rows.Count
                ? $"{data.Rows.Count:N0} kayıt"
                : $"{data.Rows.Count:N0} / {totalRowCount:N0} kayıt";
        }
    }
    public Visibility HiddenColumnsButtonVisibility => Columns.Any(column => !column.IsVisibleByDefault)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string HiddenColumnsButtonText => showHiddenColumns ? "Ek sütunları gizle" : "Ek sütunları göster";
    public string SearchText
    {
        get => searchText;
        set
        {
            if (searchText == value) return;
            searchText = value;
            var escaped = EscapeLikeValue(value.Trim());
            data.DefaultView.RowFilter = escaped.Length == 0 ? string.Empty : $"[{SearchColumn}] LIKE '%{escaped}%'";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowCountText)));
        }
    }
    public Visibility ColorSelectorVisibility => ColorModes.Count <= 1 ? Visibility.Collapsed : Visibility.Visible;
    public WorkflowTableColorModeViewModel? SelectedColorMode
    {
        get => selectedColorMode;
        set
        {
            if (ReferenceEquals(selectedColorMode, value)) return;
            selectedColorMode = value;
            ApplyColors();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedColorMode)));
        }
    }

    public bool IsColumnVisible(string? key) => showHiddenColumns ||
        Columns.FirstOrDefault(column => column.Key == key)?.IsVisibleByDefault != false;

    public void ToggleHiddenColumns()
    {
        showHiddenColumns = !showHiddenColumns;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HiddenColumnsButtonText)));
    }

    public string InteractionKey(DataRowView row) => InteractionKind switch
    {
        WorkflowRowInteractionKind.Prescription => Value(row, "prescription_number"),
        WorkflowRowInteractionKind.Sale => Value(row, "reference_code"),
        WorkflowRowInteractionKind.Product => Value(row, "barcode"),
        WorkflowRowInteractionKind.QrMovement => Value(row, "qr_code"),
        _ => string.Empty
    };

    public bool IsCurrentDetail(string key) =>
        key.Length > 0 && key == currentDetailKey && (detailLoading || InlineDetail is not null);

    public void BeginDetailLoad(DataRowView row, string message)
    {
        SelectedRow = row;
        currentDetailKey = InteractionKey(row);
        DetailTitle = InteractionKind switch
        {
            WorkflowRowInteractionKind.Prescription => $"{currentDetailKey} · Reçete İçeriği",
            WorkflowRowInteractionKind.Sale => $"{currentDetailKey} · Satış/İade İçeriği",
            WorkflowRowInteractionKind.Product => $"{currentDetailKey} · Ürün Bilgisi",
            WorkflowRowInteractionKind.QrMovement => $"{currentDetailKey} · Karekod Geçmişi",
            _ => "Satır Ayrıntısı"
        };
        InlineDetail = null;
        DetailMessage = message;
        detailLoading = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLoadingVisibility)));
        NotifyDetailVisibility();
    }

    public void EndDetailLoad()
    {
        if (!detailLoading) return;
        detailLoading = false;
        if (InlineDetail is not null) DetailMessage = string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLoadingVisibility)));
        NotifyDetailVisibility();
    }

    public void SetDetail(AiWorkflowTablePresentation presentation)
    {
        DetailTitle = presentation.Title;
        if (presentation.Rows.Count == 0)
        {
            InlineDetail = null;
            DetailMessage = EmptyDetailText;
            return;
        }
        InlineDetail = new WorkflowTableViewModel(presentation);
        DetailMessage = string.Empty;
    }

    public void SetDetailMessage(string message)
    {
        InlineDetail = null;
        DetailMessage = message;
    }

    public bool TryGetCachedDetail(string key, out JsonElement output) =>
        detailCache.TryGetValue(key, out output);

    public void CacheDetail(string key, JsonElement output)
    {
        if (key.Length > 0) detailCache[key] = output.Clone();
    }

    private void NotifyDetailVisibility()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailGridVisibility)));
    }

    private static string Value(DataRowView row, string key) =>
        row.Row.Table.Columns.Contains(key)
            ? Convert.ToString(row.Row[key], CultureInfo.CurrentCulture)?.Trim() ?? string.Empty
            : string.Empty;

    private static WorkflowRowInteractionKind InferInteractionKind(
        IReadOnlyList<AiWorkflowTableColumn> columns)
    {
        var keys = columns.Select(column => column.Key).ToHashSet(StringComparer.Ordinal);
        if (keys.Contains("prescription_number")) return WorkflowRowInteractionKind.Prescription;
        if (keys.Contains("reference_code") &&
            (keys.Contains("record_id") || keys.Contains("record_type") || keys.Contains("sale_type")))
            return WorkflowRowInteractionKind.Sale;
        if (keys.Contains("qr_code") &&
            (keys.Contains("movement_type") || keys.Contains("movement_date")))
            return WorkflowRowInteractionKind.QrMovement;
        if (keys.Contains("barcode") &&
            (keys.Contains("medicine_name") || keys.Contains("product_name") || keys.Contains("name")))
            return WorkflowRowInteractionKind.Product;
        return WorkflowRowInteractionKind.None;
    }

    private void ApplyColors()
    {
        if (selectedColorMode is null || !data.Columns.Contains(selectedColorMode.Field) || selectedColorMode.Colors.Count == 0)
        {
            foreach (DataRow row in data.Rows) row[ColorColumn] = DefaultRowBackground(row);
            return;
        }
        var colorByValue = new Dictionary<string, Brush>(StringComparer.CurrentCultureIgnoreCase);
        foreach (DataRow row in data.Rows)
        {
            var value = Convert.ToString(row[selectedColorMode.Field]) ?? string.Empty;
            if (!colorByValue.TryGetValue(value, out var brush))
            {
                var colorText = selectedColorMode.Colors[colorByValue.Count % selectedColorMode.Colors.Count];
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText));
                brush.Freeze();
                colorByValue[value] = brush;
            }
            row[ColorColumn] = brush;
        }
    }

    private Brush DefaultRowBackground(DataRow row)
    {
        if (RowBackgroundField != "is_live_on_medula" || !data.Columns.Contains(RowBackgroundField))
            return Brushes.White;
        var value = Convert.ToString(row[RowBackgroundField]) ?? string.Empty;
        if (!bool.TryParse(value, out var isLive) || !isLive) return Brushes.White;
        var brush = new SolidColorBrush(Color.FromRgb(220, 239, 252));
        brush.Freeze();
        return brush;
    }

    public string ToClipboardText()
    {
        var visibleColumns = Columns.Where(column => IsColumnVisible(column.Key)).ToArray();
        var lines = new List<string> { Title, string.Join('\t', visibleColumns.Select(column => column.Label)) };
        lines.AddRange(Rows.Cast<DataRowView>().Select(view =>
            string.Join('\t', visibleColumns.Select(column => Convert.ToString(view.Row[column.Key]) ?? string.Empty))));
        return string.Join(Environment.NewLine, lines);
    }

    private static string EscapeLikeValue(string value) => value
        .Replace("'", "''", StringComparison.Ordinal)
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("*", "[*]", StringComparison.Ordinal);

    private static string FormatCell(string key, string? value) => AiWorkflowValueFormatter.Format(key, value);
}

public sealed class WorkflowTableColorModeViewModel(AiWorkflowTableColorMode mode)
{
    public string Id { get; } = mode.Id;
    public string Label { get; } = mode.Label;
    public string Field { get; } = mode.Field;
    public IReadOnlyList<string> Colors { get; } = mode.Colors;
}

public sealed class WorkflowSelectionOptionViewModel : INotifyPropertyChanged
{
    private bool isSelected;

    private WorkflowSelectionOptionViewModel(string id, string name, string barcode, bool isOnMarket)
    {
        Id = id;
        Name = name;
        Barcode = barcode;
        IsOnMarket = isOnMarket;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Name { get; }
    public string Barcode { get; }
    public bool IsOnMarket { get; }
    public Visibility PassiveVisibility => IsOnMarket ? Visibility.Collapsed : Visibility.Visible;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public static WorkflowSelectionOptionViewModel From(AiWorkflowSelectionOption option)
    {
        var name = option.Data.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
            ? nameValue.GetString() ?? option.Label
            : option.Label.Split(" · ", StringSplitOptions.None)[0];
        var barcode = option.Data.TryGetProperty("barcode", out var barcodeValue) && barcodeValue.ValueKind == JsonValueKind.String
            ? barcodeValue.GetString() ?? option.Id
            : option.Data.TryGetProperty("queue", out _) ? string.Empty : option.Id;
        var isOnMarket = !option.Data.TryGetProperty("is_on_market", out var marketValue) ||
                         marketValue.ValueKind != JsonValueKind.False;
        return new WorkflowSelectionOptionViewModel(option.Id, name, barcode, isOnMarket);
    }
}
