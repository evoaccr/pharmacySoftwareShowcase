using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Invoices;

public sealed partial class EczaciKartConnectionService
{
    public const string LoginUrl = "https://portal.kolaysoft.com.tr/accounting/login";
    public const string DashboardUrl = "https://portal.kolaysoft.com.tr/accounting/";
    private const string ApiBaseUrl = "https://portal.kolaysoft.com.tr/accounting/api";
    private const int ApiPageSize = 100;
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(35);
    private readonly SemaphoreSlim browserGate = new(1, 1);
    private readonly object requestSync = new();
    private Window? browserWindow;
    private WebView2? browser;
    private readonly Dictionary<string, AuthorizedApiRequest> authorizedApiRequests = new(StringComparer.OrdinalIgnoreCase);

    private EczaciKartConnectionService()
    {
    }

    public static EczaciKartConnectionService Instance { get; } = new();

    public async Task<IReadOnlyList<EczaciKartDashboardCount>> LoadDashboardCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            var settings = ApplicationSettingsStore.Current;
            if (string.IsNullOrWhiteSpace(settings.EczaciKartUsername)
                || string.IsNullOrEmpty(settings.EczaciKartPassword))
                throw new InvalidOperationException(
                    "Eczacı Kart kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");
            await EnsureBrowserAsync(settings.EczaciKartBrowserDebug, cancellationToken);
            await EnsureAuthenticatedAsync(settings.EczaciKartUsername, settings.EczaciKartPassword, cancellationToken);
            await OpenDashboardAsync(cancellationToken);
            var definitionsJson = JsonSerializer.Serialize(EczaciKartInvoiceType.All.Select(type => new
            {
                key = type.Key,
                name = type.DisplayName,
                title = type.DashboardTitle
            }));
            var script = $$"""
                (() => {
                    const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                    const result = [];
                    for (const definition of {{definitionsJson}}) {
                        const title = normalize(definition.title);
                        const titleNode = Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6'))
                            .find(element => normalize(element.textContent) === title);
                        let count = 0;
                        let found = false;
                        let container = titleNode?.parentElement ?? null;
                        for (let level = 0; container && level < 4 && !found; level++, container = container.parentElement) {
                            const lines = (container.innerText ?? '').split(/\r?\n/)
                                .map(value => value.trim()).filter(Boolean);
                            const titleIndex = lines.findIndex(line => normalize(line) === title);
                            for (let cursor = titleIndex - 1; cursor >= 0; cursor--) {
                                const candidate = lines[cursor].replace(/[.\s]/g, '');
                                if (/^\d+$/.test(candidate)) {
                                    count = Number.parseInt(candidate, 10);
                                    found = true;
                                    break;
                                }
                            }
                        }
                        result.push({ TypeKey: definition.key, TypeName: definition.name, Count: count });
                    }
                    return JSON.stringify(result);
                })();
                """;
            List<EczaciKartDashboardCount>? counts = null;
            await WaitUntilAsync(async () =>
            {
                var json = await ExecuteStringAsync(script);
                counts = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<List<EczaciKartDashboardCount>>(json);
                return counts is { Count: 5 }
                    && counts.Where(item => item.TypeKey is "incoming-invoice" or "outgoing-invoice" or "archive-invoice")
                        .All(item => item.Count > 0);
            }, cancellationToken);
            return counts
                ?? throw new InvalidOperationException("Eczacı Kart ana sayfa belge sayıları çözümlenemedi.");
        }
        finally { browserGate.Release(); }
    }

    public async Task<IReadOnlyList<EczaciKartInvoiceItem>> LoadInvoicesAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? typeKey,
        CancellationToken cancellationToken = default)
        => await LoadInvoicesAsync(startDate, endDate, typeKey, null, cancellationToken);

    public async Task<IReadOnlyList<EczaciKartInvoiceItem>> LoadInvoicesAsync(
        DateOnly startDate,
        DateTimeOffset endTimestamp,
        string? typeKey,
        CancellationToken cancellationToken = default)
        => await LoadInvoicesCoreAsync(startDate, endTimestamp.DateTime, typeKey, null, cancellationToken);

    public async Task<IReadOnlyList<EczaciKartInvoiceItem>> LoadInvoicesAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? typeKey,
        IProgress<EczaciKartInvoiceScanProgress>? scanProgress,
        CancellationToken cancellationToken = default)
    {
        return await LoadInvoicesCoreAsync(
            startDate, endDate.ToDateTime(new TimeOnly(23, 59, 59)), typeKey, scanProgress, cancellationToken);
    }

    private async Task<IReadOnlyList<EczaciKartInvoiceItem>> LoadInvoicesCoreAsync(
        DateOnly startDate,
        DateTime endTimestamp,
        string? typeKey,
        IProgress<EczaciKartInvoiceScanProgress>? scanProgress,
        CancellationToken cancellationToken)
    {
        var endDate = DateOnly.FromDateTime(endTimestamp);
        if (endDate < startDate)
            throw new ArgumentException("Bitiş tarihi başlangıç tarihinden önce olamaz.", nameof(endTimestamp));

        var requestedTypes = string.IsNullOrWhiteSpace(typeKey)
            ? EczaciKartInvoiceType.All
            : [EczaciKartInvoiceType.FromKey(typeKey)];

        await browserGate.WaitAsync(cancellationToken);
        try
        {
            var settings = ApplicationSettingsStore.Current;
            if (string.IsNullOrWhiteSpace(settings.EczaciKartUsername)
                || string.IsNullOrEmpty(settings.EczaciKartPassword))
                throw new InvalidOperationException(
                    "Eczacı Kart kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");

            await EnsureBrowserAsync(settings.EczaciKartBrowserDebug, cancellationToken);
            await EnsureAuthenticatedAsync(
                settings.EczaciKartUsername,
                settings.EczaciKartPassword,
                cancellationToken);

            var result = new List<EczaciKartInvoiceItem>();
            foreach (var type in requestedTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await OpenDashboardAsync(cancellationToken);
                await OpenPermittedDetailAsync(type, cancellationToken);
                // The portal UI becomes unresponsive when a multi-year range is submitted. A valid
                // single-month query is sufficient to capture authorization; the complete range is
                // then paged through the authorized API without blocking the WebView.
                var captureStart = new DateOnly(endDate.Year, endDate.Month, 1);
                if (captureStart < startDate) captureStart = startDate;
                await ApplyDateRangeAsync(type, captureStart, endDate, cancellationToken);
                var authorizedRequest = RequireAuthorizedRequest(type);
                result.AddRange(await ReadAllPagesAsync(
                    type,
                    startDate,
                    endTimestamp,
                    authorizedRequest,
                    scanProgress,
                    cancellationToken));
            }

            return result
                .OrderByDescending(item => item.ReceivedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.DocumentNumber, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            browserGate.Release();
        }
    }

    public async Task<EczaciKartInvoiceDetailTable> LoadInvoiceDetailAsync(
        EczaciKartInvoiceItem invoice,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (endDate < startDate)
            throw new ArgumentException("Bitiş tarihi başlangıç tarihinden önce olamaz.", nameof(endDate));

        var type = EczaciKartInvoiceType.FromKey(invoice.TypeKey);
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            var settings = ApplicationSettingsStore.Current;
            if (string.IsNullOrWhiteSpace(settings.EczaciKartUsername)
                || string.IsNullOrEmpty(settings.EczaciKartPassword))
                throw new InvalidOperationException(
                    "Eczacı Kart kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");

            await EnsureBrowserAsync(settings.EczaciKartBrowserDebug, cancellationToken);
            await EnsureAuthenticatedAsync(
                settings.EczaciKartUsername,
                settings.EczaciKartPassword,
                cancellationToken);
            await OpenDashboardAsync(cancellationToken);
            await OpenPermittedDetailAsync(type, cancellationToken);
            await ApplyDateRangeAsync(type, startDate, endDate, cancellationToken);
            await FilterForDocumentAsync(type, invoice, cancellationToken);
            await OpenInvoicePopupAsync(type, invoice, cancellationToken);
            try
            {
                return await ExtractInvoiceDetailTableAsync(cancellationToken);
            }
            finally
            {
                await CloseInvoicePopupAsync();
            }
        }
        finally
        {
            browserGate.Release();
        }
    }

    public async Task<EczaciKartInvoiceDocument> LoadInvoiceDocumentAsync(
        EczaciKartInvoiceItem invoice,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (endDate < startDate)
            throw new ArgumentException("Bitiş tarihi başlangıç tarihinden önce olamaz.", nameof(endDate));

        var type = EczaciKartInvoiceType.FromKey(invoice.TypeKey);
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            var settings = ApplicationSettingsStore.Current;
            if (string.IsNullOrWhiteSpace(settings.EczaciKartUsername)
                || string.IsNullOrEmpty(settings.EczaciKartPassword))
                throw new InvalidOperationException(
                    "Eczacı Kart kullanıcı adı ve parolası Ayarlar ekranında kaydedilmelidir.");

            await EnsureBrowserAsync(settings.EczaciKartBrowserDebug, cancellationToken);
            await EnsureAuthenticatedAsync(settings.EczaciKartUsername, settings.EczaciKartPassword, cancellationToken);
            await OpenDashboardAsync(cancellationToken);
            await OpenPermittedDetailAsync(type, cancellationToken);
            await ApplyDateRangeAsync(type, startDate, endDate, cancellationToken);
            await FilterForDocumentAsync(type, invoice, cancellationToken);
            await OpenInvoicePopupAsync(type, invoice, cancellationToken);
            try
            {
                var detail = await ExtractInvoiceDetailTableAsync(cancellationToken);
                var sourceHtml = await ExtractInvoiceSourceHtmlAsync(cancellationToken);
                return EczaciKartInvoiceDocumentParser.Parse(invoice, detail, sourceHtml);
            }
            finally
            {
                await CloseInvoicePopupAsync();
            }
        }
        finally
        {
            browserGate.Release();
        }
    }

    public static string BuildApiUrl(
        EczaciKartInvoiceType type,
        DateOnly startDate,
        DateOnly endDate,
        int page,
        int pageSize)
    {
        var query = new List<string>();
        if (type.RequiresPrefix) query.Add("prefix=kolaysoft");
        query.Add($"year={startDate.Year}");
        query.Add($"month={startDate.Month - 1}");
        query.Add($"page={page}");
        query.Add($"size={pageSize}");
        query.Add("multipleVkn=");
        query.Add("headerSearch=");
        query.Add("notInList=false");
        query.Add("documentIds=");
        if (type.Key == "incoming-invoice") query.Add("chemistWarehouseFilter=ALL");
        query.Add("sort=receivedDate%2Cdesc");
        query.Add("isArchive=0");
        query.Add($"receivedDate={EncodeDate(startDate)}");
        query.Add($"receivedDate={EncodeTimestamp(endDate.ToDateTime(new TimeOnly(23, 59, 59)))}");
        return $"{ApiBaseUrl}{type.ApiPath}?{string.Join('&', query)}";
    }

    private async Task EnsureAuthenticatedAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (await IsAuthenticatedAsync()) return;

        await NavigateAsync(LoginUrl, cancellationToken);
        await WaitUntilAsync(
            async () => await IsAuthenticatedAsync() || await HasLoginFormAsync(),
            cancellationToken);
        if (await IsAuthenticatedAsync()) return;

        await FillLoginFieldAsync("#username", username, "kullanıcı adı", cancellationToken);
        await FillLoginFieldAsync("#password", password, "parola", cancellationToken);
        await Task.Delay(250, cancellationToken);
        if (!await LoginFieldsHaveLengthsAsync(username.Length, password.Length))
            throw new InvalidOperationException("Eczacı Kart giriş alanları yazıldıktan sonra portal tarafından temizlendi.");
        await WaitUntilAsync(CanSubmitLoginAsync, cancellationToken);
        var submitScript = """
            (() => {
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const submit = Array.from(document.querySelectorAll('button,input[type="submit"]'))
                    .find(element => normalize(element.textContent || element.value).startsWith('GİRİŞ'));
                if (!submit || submit.disabled) return 'missing';
                submit.click();
                return 'submitted';
            })();
            """;
        if (await ExecuteStringAsync(submitScript) != "submitted")
            throw new InvalidOperationException("Eczacı Kart GİRİŞ düğmesine basılamadı.");

        await WaitUntilAsync(IsAuthenticatedAsync, cancellationToken);
        if (!await IsAuthenticatedAsync())
            throw new InvalidOperationException("Eczacı Kart giriş bilgileri kabul edilmedi.");
    }

    private async Task FillLoginFieldAsync(
        string selector,
        string value,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var selectorJson = JsonSerializer.Serialize(selector);
        var focusScript = $$"""
            (() => {
                const field = document.querySelector({{selectorJson}});
                if (!field) return 'missing';
                field.scrollIntoView({ block: 'center', inline: 'nearest' });
                field.focus();
                field.select();
                return document.activeElement === field ? 'focused' : 'unfocused';
            })();
            """;
        if (await ExecuteStringAsync(focusScript) != "focused")
            throw new InvalidOperationException($"Eczacı Kart {fieldName} alanına odaklanılamadı.");

        cancellationToken.ThrowIfCancellationRequested();
        var inputPayload = JsonSerializer.Serialize(new { text = value });
        await RequireBrowser().CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", inputPayload);
        await Task.Delay(80, cancellationToken);

        var lengthScript = $"document.querySelector({selectorJson})?.value?.length ?? -1";
        var rawLength = await RequireBrowser().ExecuteScriptAsync(lengthScript);
        if (!int.TryParse(rawLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualLength)
            || actualLength != value.Length)
            throw new InvalidOperationException($"Eczacı Kart {fieldName} alanı beklenen uzunlukta doldurulamadı.");
    }

    private async Task<bool> LoginFieldsHaveLengthsAsync(int usernameLength, int passwordLength)
    {
        if (browser?.CoreWebView2 is null) return false;
        var raw = await browser.ExecuteScriptAsync(
            $"document.querySelector('#username')?.value?.length === {usernameLength}"
            + $" && document.querySelector('#password')?.value?.length === {passwordLength}");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task OpenDashboardAsync(CancellationToken cancellationToken)
    {
        if (await HasDashboardCardsAsync()) return;
        await NavigateAsync(DashboardUrl, cancellationToken);
        await WaitUntilAsync(HasDashboardCardsAsync, cancellationToken);
    }

    private async Task OpenPermittedDetailAsync(
        EczaciKartInvoiceType type,
        CancellationToken cancellationToken)
    {
        ResetCapturedRequest(type);
        var titleJson = JsonSerializer.Serialize(type.DashboardTitle);
        var script = $$"""
            (() => {
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const title = normalize({{titleJson}});
                const titleNode = Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6'))
                    .find(element => normalize(element.textContent) === title);
                if (!titleNode) return 'missing-title';

                let container = titleNode.parentElement;
                while (container && container !== document.body) {
                    const detail = Array.from(container.querySelectorAll('button,a'))
                        .find(element => normalize(element.textContent).startsWith('DETAYLAR'));
                    if (detail) {
                        detail.click();
                        return 'clicked';
                    }
                    container = container.parentElement;
                }
                return 'missing-detail';
            })();
            """;

        if (await ExecuteStringAsync(script) != "clicked")
            throw new InvalidOperationException($"{type.DashboardTitle} Detaylar düğmesi bulunamadı.");
        await WaitUntilAsync(HasInvoiceTableAsync, cancellationToken);
    }

    private async Task ApplyDateRangeAsync(
        EczaciKartInvoiceType type,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        ResetCapturedRequest(type);
        var startTextJson = JsonSerializer.Serialize(startDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
        var endTextJson = JsonSerializer.Serialize(endDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
        var script = $$"""
            (() => {
                const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const candidates = Array.from(document.querySelectorAll('input'))
                    .filter(input => visible(input) && normalize(input.placeholder).includes('GG-AA-YYYY'));
                if (candidates.length < 2) return 'missing-dates';

                const setValue = (element, value) => {
                    const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
                    descriptor.set.call(element, value);
                    element.dispatchEvent(new Event('input', { bubbles: true }));
                    element.dispatchEvent(new Event('change', { bubbles: true }));
                    element.dispatchEvent(new Event('blur', { bubbles: true }));
                };
                setValue(candidates[0], {{startTextJson}});
                setValue(candidates[1], {{endTextJson}});
                const action = Array.from(document.querySelectorAll('button'))
                    .find(button => visible(button) && normalize(button.textContent).startsWith('ARAMA YAP'));
                if (!action) return 'missing-search';
                action.click();
                return 'submitted';
            })();
            """;
        if (await ExecuteStringAsync(script) != "submitted")
            throw new InvalidOperationException("Eczacı Kart tarih filtresi uygulanamadı.");

        await WaitUntilAsync(() => HasAuthorizedRequestAsync(type), cancellationToken);
    }

    private async Task FilterForDocumentAsync(
        EczaciKartInvoiceType type,
        EczaciKartInvoiceItem invoice,
        CancellationToken cancellationToken)
    {
        var documentNumberJson = JsonSerializer.Serialize(invoice.DocumentNumber);
        var labelJson = JsonSerializer.Serialize(type.IsDispatch ? "İRSALİYE NO" : "FATURA NO");
        var script = $$"""
            (() => {
                const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const expectedLabel = normalize({{labelJson}});
                const field = Array.from(document.querySelectorAll('input[type="text"]'))
                    .filter(input => visible(input) && !normalize(input.placeholder).includes('GG-AA-YYYY'))
                    .find(input => {
                        let container = input.parentElement;
                        for (let level = 0; container && level < 4; level++, container = container.parentElement) {
                            const text = normalize(container.innerText);
                            if (text === expectedLabel || text.startsWith(expectedLabel + ' ')) return true;
                        }
                        return false;
                    });
                if (!field) return 'missing-document-filter';
                const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
                descriptor.set.call(field, {{documentNumberJson}});
                field.dispatchEvent(new Event('input', { bubbles: true }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
                const action = Array.from(document.querySelectorAll('button'))
                    .find(button => visible(button) && normalize(button.textContent).startsWith('ARAMA YAP'));
                if (!action) return 'missing-search';
                action.click();
                return 'submitted';
            })();
            """;
        if (await ExecuteStringAsync(script) != "submitted")
            throw new InvalidOperationException("Eczacı Kart fatura numarası filtresi uygulanamadı.");
        await WaitUntilAsync(() => HasInvoiceRowAsync(invoice), cancellationToken);
    }

    private async Task<bool> HasInvoiceRowAsync(EczaciKartInvoiceItem invoice)
    {
        if (browser?.CoreWebView2 is null) return false;
        var documentNumberJson = JsonSerializer.Serialize(invoice.DocumentNumber);
        var ettnJson = JsonSerializer.Serialize(invoice.Ettn);
        var raw = await browser.ExecuteScriptAsync($$"""
            (() => {
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const documentNumber = normalize({{documentNumberJson}});
                const ettn = normalize({{ettnJson}});
                const table = Array.from(document.querySelectorAll('table')).find(candidate =>
                    Array.from(candidate.querySelectorAll('th')).some(header => normalize(header.textContent) === 'İŞLEMLER'));
                if (!table) return false;
                return Array.from(table.querySelectorAll('tbody tr')).some(row => {
                    const text = normalize(row.textContent);
                    return (documentNumber && text.includes(documentNumber)) || (ettn && text.includes(ettn));
                });
            })();
            """);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task OpenInvoicePopupAsync(
        EczaciKartInvoiceType type,
        EczaciKartInvoiceItem invoice,
        CancellationToken cancellationToken)
    {
        var documentNumberJson = JsonSerializer.Serialize(invoice.DocumentNumber);
        var ettnJson = JsonSerializer.Serialize(invoice.Ettn);
        var selectScript = $$"""
            (() => {
                const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const documentNumber = normalize({{documentNumberJson}});
                const ettn = normalize({{ettnJson}});
                const table = Array.from(document.querySelectorAll('table')).find(candidate =>
                    Array.from(candidate.querySelectorAll('th')).some(header => normalize(header.textContent) === 'İŞLEMLER'));
                const row = table && Array.from(table.querySelectorAll('tbody tr')).find(candidate => {
                    const text = normalize(candidate.textContent);
                    return (documentNumber && text.includes(documentNumber)) || (ettn && text.includes(ettn));
                });
                if (!row) return 'missing-row';
                const select = Array.from(row.querySelectorAll('button')).find(button =>
                    visible(button) && normalize(button.textContent).startsWith('SEÇİNİZ'));
                if (!select) return 'missing-select';
                select.click();
                return 'selected';
            })();
            """;
        if (await ExecuteStringAsync(selectScript) != "selected")
            throw new InvalidOperationException("Eczacı Kart Seçiniz düğmesi bulunamadı.");

        await WaitUntilAsync(async () => await HasInvoiceViewActionAsync(type, invoice), cancellationToken);
        var actionWordJson = JsonSerializer.Serialize(type.IsDispatch ? "İRSALİYE" : "FATURA");
        var actionScript = $$"""
            (() => {
                const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const documentNumber = normalize({{documentNumberJson}});
                const ettn = normalize({{ettnJson}});
                const actionWord = normalize({{actionWordJson}});
                const table = Array.from(document.querySelectorAll('table')).find(candidate =>
                    Array.from(candidate.querySelectorAll('th')).some(header => normalize(header.textContent) === 'İŞLEMLER'));
                const row = table && Array.from(table.querySelectorAll('tbody tr')).find(candidate => {
                    const text = normalize(candidate.textContent);
                    return (documentNumber && text.includes(documentNumber)) || (ettn && text.includes(ettn));
                });
                if (!row) return 'missing-row';
                const action = Array.from(row.querySelectorAll('a,button')).find(element => {
                    const text = normalize(element.textContent);
                    return visible(element) && text.includes(actionWord)
                        && (text.includes('GÖSTER') || text.includes('GÖRÜNTÜLE'));
                });
                if (!action) return 'missing-view';
                action.click();
                return 'opened';
            })();
            """;
        if (await ExecuteStringAsync(actionScript) != "opened")
            throw new InvalidOperationException("Eczacı Kart Faturayı Göster işlemi bulunamadı.");
        await WaitUntilAsync(HasInvoicePopupFrameAsync, cancellationToken);
    }

    private async Task<bool> HasInvoiceViewActionAsync(EczaciKartInvoiceType type, EczaciKartInvoiceItem invoice)
    {
        if (browser?.CoreWebView2 is null) return false;
        var documentNumberJson = JsonSerializer.Serialize(invoice.DocumentNumber);
        var actionWordJson = JsonSerializer.Serialize(type.IsDispatch ? "İRSALİYE" : "FATURA");
        var raw = await browser.ExecuteScriptAsync($$"""
            (() => {
                const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const documentNumber = normalize({{documentNumberJson}});
                const actionWord = normalize({{actionWordJson}});
                const row = Array.from(document.querySelectorAll('table tbody tr')).find(candidate =>
                    normalize(candidate.textContent).includes(documentNumber));
                return !!row && Array.from(row.querySelectorAll('a,button')).some(element => {
                    const text = normalize(element.textContent);
                    return visible(element) && text.includes(actionWord)
                        && (text.includes('GÖSTER') || text.includes('GÖRÜNTÜLE'));
                });
            })();
            """);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasInvoicePopupFrameAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        var raw = await browser.ExecuteScriptAsync(
            "!!Array.from(document.querySelectorAll('.modal iframe, [role=\"dialog\"] iframe')).find(frame => frame.src.startsWith('blob:'))");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EczaciKartInvoiceDetailTable> ExtractInvoiceDetailTableAsync(
        CancellationToken cancellationToken)
    {
        var extractionScript = """
            (() => {
                window.__pharmacyInvoiceDetail = { state: 'loading' };
                const frame = Array.from(document.querySelectorAll('.modal iframe, [role="dialog"] iframe'))
                    .find(candidate => candidate.src.startsWith('blob:'));
                if (!frame) {
                    window.__pharmacyInvoiceDetail = { state: 'error', message: 'Fatura penceresi bulunamadı.' };
                    return 'missing-frame';
                }

                const clean = value => (value ?? '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                const normalize = value => clean(value).toLocaleUpperCase('tr-TR');
                const expandCells = row => {
                    const cells = [];
                    for (const cell of Array.from(row.children).filter(child => child.matches('th,td'))) {
                        const text = clean(cell.innerText || cell.textContent);
                        const span = Math.max(1, Number.parseInt(cell.getAttribute('colspan') || '1', 10) || 1);
                        cells.push(text);
                        for (let index = 1; index < span; index++) cells.push('');
                    }
                    return cells;
                };
                const keywordScore = values => {
                    const text = normalize(values.join(' '));
                    return ['MAL', 'HİZMET', 'ÜRÜN', 'MİKTAR', 'BİRİM', 'FİYAT', 'KDV', 'TUTAR', 'SIRA']
                        .filter(keyword => text.includes(keyword)).length;
                };
                const readTable = table => {
                    const entries = Array.from(table.querySelectorAll('tr'))
                        .filter(row => row.closest('table') === table)
                        .map(element => ({ element, cells: expandCells(element) }))
                        .filter(entry => entry.cells.some(Boolean));
                    if (entries.length < 2) return null;
                    const matrices = entries.map(entry => entry.cells);
                    const headerIndexes = entries
                        .map((entry, index) => entry.element.querySelector('th') ? index : -1)
                        .filter(index => index >= 0);
                    let headerEnd = headerIndexes.length ? Math.max(...headerIndexes) : 0;
                    if (!headerIndexes.length && keywordScore(matrices[0]) < 2) return null;
                    const headerRows = matrices.slice(0, headerEnd + 1);
                    const dataRows = matrices.slice(headerEnd + 1).filter(row => row.some(Boolean));
                    if (!dataRows.length) return null;
                    const width = Math.max(...matrices.map(row => row.length));
                    const headers = Array.from({ length: width }, (_, column) => {
                        const labels = headerRows.map(row => clean(row[column] || '')).filter(Boolean);
                        return Array.from(new Set(labels)).join(' / ');
                    });
                    const score = keywordScore(headers) * 10000 + dataRows.length * 100 + width;
                    return { headers, rows: dataRows.map(row => Array.from({ length: width }, (_, column) => clean(row[column] || ''))), score };
                };

                fetch(frame.src)
                    .then(response => {
                        if (!response.ok) throw new Error('Fatura belgesi indirilemedi.');
                        return response.text();
                    })
                    .then(html => {
                        const invoiceDocument = new DOMParser().parseFromString(html, 'text/html');
                        const invoiceTables = Array.from(invoiceDocument.querySelectorAll('table'));
                        const candidates = invoiceTables
                            .map(readTable).filter(Boolean).sort((left, right) => right.score - left.score);
                        let extracted = candidates.find(candidate => keywordScore(candidate.headers) >= 2) || null;
                        if (!extracted) {
                            const sectionRows = [];
                            const sectionContainers = Array.from(invoiceDocument.querySelectorAll('div')).filter(container => {
                                const children = Array.from(container.children);
                                return children.length === 2
                                    && children[0].matches('h1,h2,h3,h4,h5,h6')
                                    && children[1].children.length >= 2;
                            });
                            for (const section of sectionContainers) {
                                const sectionName = clean(section.children[0].textContent);
                                const values = Array.from(section.children[1].children)
                                    .map(child => clean(child.textContent)).filter(Boolean);
                                if (!sectionName || values.length < 2) continue;
                                for (let index = 0; index + 1 < values.length; index += 2) {
                                    const description = values[index];
                                    const amount = values[index + 1];
                                    if (!description || !amount || !/[0-9]/.test(amount)) continue;
                                    sectionRows.push([sectionName, description, amount]);
                                }
                            }
                            if (sectionRows.length)
                                extracted = { headers: ['Bölüm', 'Açıklama', 'Tutar'], rows: sectionRows };
                        }
                        if (!extracted) {
                            // Utility/service invoices may legitimately contain no item table.
                            // Preserve their complete HTML and metadata while storing zero lines.
                            extracted = { headers: [], rows: [] };
                        }
                        const { headers, rows } = extracted;
                        window.__pharmacyInvoiceDetail = { state: 'ready', table: { headers, rows } };
                    })
                    .catch(error => {
                        window.__pharmacyInvoiceDetail = {
                            state: 'error',
                            message: clean(error && error.message) || 'Fatura içeriği okunamadı.'
                        };
                    });
                return 'started';
            })();
            """;
        if (await ExecuteStringAsync(extractionScript) != "started")
            throw new InvalidOperationException("Eczacı Kart fatura içeriği okunmaya başlanamadı.");

        await WaitUntilAsync(async () =>
        {
            var state = await ExecuteStringAsync("window.__pharmacyInvoiceDetail?.state ?? 'missing'");
            return state is "ready" or "error";
        }, cancellationToken);
        var finalState = await ExecuteStringAsync("window.__pharmacyInvoiceDetail?.state ?? 'missing'");
        if (finalState == "error")
        {
            var message = await ExecuteStringAsync(
                "window.__pharmacyInvoiceDetail?.message ?? 'Fatura içeriği okunamadı.'");
            throw new InvalidOperationException(message ?? "Fatura içeriği okunamadı.");
        }

        var detailJson = await ExecuteStringAsync(
            "JSON.stringify(window.__pharmacyInvoiceDetail?.table ?? null)");
        return string.IsNullOrWhiteSpace(detailJson) || detailJson == "null"
            ? throw new InvalidOperationException("Eczacı Kart fatura içeriği tablosu alınamadı.")
            : EczaciKartInvoiceDetailTable.Parse(detailJson);
    }

    private async Task<string> ExtractInvoiceSourceHtmlAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
                window.__pharmacyInvoiceSource = { state: 'loading' };
                const frame = Array.from(document.querySelectorAll('.modal iframe, [role="dialog"] iframe'))
                    .find(candidate => candidate.src.startsWith('blob:'));
                if (!frame) return 'missing-frame';
                fetch(frame.src)
                    .then(response => response.text())
                    .then(html => window.__pharmacyInvoiceSource = { state: 'ready', html })
                    .catch(error => window.__pharmacyInvoiceSource = {
                        state: 'error', message: error?.message || 'Fatura kaynağı okunamadı.'
                    });
                return 'started';
            })();
            """;
        if (await ExecuteStringAsync(script) != "started")
            throw new InvalidOperationException("Eczacı Kart fatura kaynağı okunmaya başlanamadı.");
        await WaitUntilAsync(async () =>
        {
            var state = await ExecuteStringAsync("window.__pharmacyInvoiceSource?.state ?? 'missing'");
            return state is "ready" or "error";
        }, cancellationToken);
        if (await ExecuteStringAsync("window.__pharmacyInvoiceSource?.state") == "error")
            throw new InvalidOperationException(await ExecuteStringAsync("window.__pharmacyInvoiceSource?.message")
                ?? "Fatura kaynağı okunamadı.");
        return await ExecuteStringAsync("window.__pharmacyInvoiceSource?.html ?? ''") ?? string.Empty;
    }

    private async Task CloseInvoicePopupAsync()
    {
        if (browser?.CoreWebView2 is null) return;
        await browser.ExecuteScriptAsync(
            """
            (() => {
                const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                const modal = Array.from(document.querySelectorAll('.modal, [role="dialog"]')).find(visible);
                if (!modal) return false;
                const close = Array.from(modal.querySelectorAll('button')).find(button =>
                    visible(button) && ((button.textContent ?? '').trim() === '×' || button.matches('[data-dismiss="modal"]')));
                if (!close) return false;
                close.click();
                return true;
            })();
            """);
    }

    private async Task<IReadOnlyList<EczaciKartInvoiceItem>> ReadAllPagesAsync(
        EczaciKartInvoiceType type,
        DateOnly startDate,
        DateTime endTimestamp,
        AuthorizedApiRequest authorizedRequest,
        IProgress<EczaciKartInvoiceScanProgress>? scanProgress,
        CancellationToken cancellationToken)
    {
        using var client = await CreateAuthorizedClientAsync(authorizedRequest.Authorization);
        var result = new List<EczaciKartInvoiceItem>();
        var endDate = DateOnly.FromDateTime(endTimestamp);
        var month = new DateOnly(startDate.Year, startDate.Month, 1);
        var finalMonth = new DateOnly(endDate.Year, endDate.Month, 1);
        var totalMonths = (finalMonth.Year - month.Year) * 12 + finalMonth.Month - month.Month + 1;
        var completedMonths = 0;
        var expectedDocuments = 0;

        while (month <= finalMonth)
        {
            var segmentStart = startDate > month ? startDate : month;
            var monthEnd = month.AddMonths(1).ToDateTime(TimeOnly.MinValue).AddSeconds(-1);
            var segmentEnd = endTimestamp < monthEnd ? endTimestamp : monthEnd;
            foreach (var archiveState in new[] { 0, 1 })
            {
                var firstPage = await GetPageAsync(
                    client, type, segmentStart, segmentEnd, 0, archiveState, authorizedRequest.Uri, cancellationToken);
                result.AddRange(EczaciKartInvoiceMapper.MapPage(type, firstPage));
                expectedDocuments += EczaciKartInvoiceMapper.ReadTotalElements(firstPage);
                var totalPages = EczaciKartInvoiceMapper.ReadTotalPages(firstPage);
                if (totalPages > 10_000)
                    throw new InvalidOperationException("Eczacı Kart beklenmeyen sayıda sonuç sayfası döndürdü.");
                for (var page = 1; page < totalPages; page++)
                {
                    var pageJson = await GetPageAsync(
                        client, type, segmentStart, segmentEnd, page, archiveState, authorizedRequest.Uri, cancellationToken);
                    result.AddRange(EczaciKartInvoiceMapper.MapPage(type, pageJson));
                }
            }
            completedMonths++;
            scanProgress?.Report(new EczaciKartInvoiceScanProgress(
                type.Key, type.DisplayName, completedMonths, totalMonths, result.Count, expectedDocuments));
            month = month.AddMonths(1);
        }
        return result;
    }

    private static async Task<string> GetPageAsync(
        HttpClient client,
        EczaciKartInvoiceType type,
        DateOnly startDate,
        DateTime endTimestamp,
        int page,
        int archiveState,
        Uri requestTemplate,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            BuildApiUrlFromTemplate(type, requestTemplate, startDate, endTimestamp, page, ApiPageSize, archiveState),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Eczacı Kart oturumu liste sorgusunu yetkilendirmedi.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string BuildApiUrlFromTemplate(
        EczaciKartInvoiceType type,
        Uri template,
        DateOnly startDate,
        DateTime endTimestamp,
        int page,
        int pageSize,
        int archiveState)
    {
        var replacedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "year", "month", "page", "size", "receivedDate", "isArchive"
        };
        var query = template.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var separator = part.IndexOf('=');
                var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
                return !replacedKeys.Contains(key);
            })
            .ToList();
        if (type.RequiresPrefix && !query.Any(part => part.StartsWith("prefix=", StringComparison.OrdinalIgnoreCase)))
            query.Add("prefix=kolaysoft");
        query.Add($"year={startDate.Year}");
        query.Add($"month={startDate.Month - 1}");
        query.Add($"page={page}");
        query.Add($"size={pageSize}");
        query.Add($"isArchive={archiveState}");
        query.Add($"receivedDate={EncodeDate(startDate)}");
        query.Add($"receivedDate={EncodeTimestamp(endTimestamp)}");
        return $"{ApiBaseUrl}{type.ApiPath}?{string.Join('&', query)}";
    }

    private async Task<HttpClient> CreateAuthorizedClientAsync(string authorization)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireEczaciKart();
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        var cookies = await RequireBrowser().CoreWebView2.CookieManager
            .GetCookiesAsync("https://portal.kolaysoft.com.tr/");
        foreach (var cookie in cookies)
        {
            try
            {
                handler.CookieContainer.Add(new Cookie(
                    cookie.Name,
                    cookie.Value,
                    string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                    cookie.Domain.TrimStart('.'))
                {
                    HttpOnly = cookie.IsHttpOnly,
                    Secure = cookie.IsSecure
                });
            }
            catch (CookieException)
            {
                // An invalid optional browser cookie must not prevent the authorized list request.
            }
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(DashboardUrl),
            Timeout = BrowserTimeout
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Referrer = new Uri(DashboardUrl);
        return client;
    }

    private async Task<bool> IsAuthenticatedAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        var raw = await browser.ExecuteScriptAsync(
            """
            (() => {
                const host = location.hostname.toLocaleLowerCase('en-US');
                const path = location.pathname.toLocaleLowerCase('en-US');
                const hasLoginForm = !!document.querySelector('#username') && !!document.querySelector('#password');
                const hasAuthenticatedShell = !!document.querySelector('a[href="/accounting/homePage"]');
                return host === 'portal.kolaysoft.com.tr'
                    && path.startsWith('/accounting')
                    && !path.endsWith('/login')
                    && !hasLoginForm
                    && hasAuthenticatedShell;
            })();
            """);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasLoginFormAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        var raw = await browser.ExecuteScriptAsync(
            "!!document.querySelector('#username') && !!document.querySelector('#password')");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CanSubmitLoginAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        var raw = await browser.ExecuteScriptAsync(
            """
            (() => {
                const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('tr-TR');
                const submit = Array.from(document.querySelectorAll('button,input[type="submit"]'))
                    .find(element => normalize(element.textContent || element.value).startsWith('GİRİŞ'));
                return !!submit && !submit.disabled;
            })();
            """);
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasDashboardCardsAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        var titles = JsonSerializer.Serialize(EczaciKartInvoiceType.All.Select(type => type.DashboardTitle));
        var raw = await browser.ExecuteScriptAsync(
            $"(() => {{ const normalize = value => (value ?? '').toLocaleUpperCase('tr-TR'); "
            + "const path = location.pathname.toLocaleLowerCase('en-US'); "
            + "const isDashboard = path === '/accounting/' || path === '/accounting/homepage'; "
            + $"const headings = Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6')).map(node => normalize(node.textContent)); "
            + $"return isDashboard && {titles}.every(title => headings.includes(normalize(title))); }})()");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasInvoiceTableAsync()
    {
        if (browser?.CoreWebView2 is null) return false;
        var raw = await browser.ExecuteScriptAsync(
            "!!Array.from(document.querySelectorAll('table')).find(table => table.querySelectorAll('th').length > 0)");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private Task<bool> HasAuthorizedRequestAsync(EczaciKartInvoiceType type) =>
        Task.FromResult(TryGetAuthorizedRequest(type, out _));

    private AuthorizedApiRequest RequireAuthorizedRequest(EczaciKartInvoiceType type) =>
        TryGetAuthorizedRequest(type, out var request)
            ? request
            : throw new InvalidOperationException("Eczacı Kart liste isteği yetkilendirmesi alınamadı.");

    private bool TryGetAuthorizedRequest(EczaciKartInvoiceType type, out AuthorizedApiRequest request)
    {
        lock (requestSync)
        {
            if (authorizedApiRequests.TryGetValue(type.ApiPath, out var captured))
            {
                request = captured;
                return true;
            }
        }
        request = null!;
        return false;
    }

    private void ResetCapturedRequest()
    {
        lock (requestSync) authorizedApiRequests.Clear();
    }

    private void ResetCapturedRequest(EczaciKartInvoiceType type)
    {
        lock (requestSync) authorizedApiRequests.Remove(type.ApiPath);
    }

    private void OnWebResourceResponseReceived(
        object? sender,
        CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
            || !uri.AbsolutePath.StartsWith("/accounting/api/", StringComparison.OrdinalIgnoreCase)) return;
        var type = EczaciKartInvoiceType.All.FirstOrDefault(item =>
            uri.AbsolutePath.EndsWith(item.ApiPath, StringComparison.OrdinalIgnoreCase));
        if (type is null) return;
        try
        {
            var authorization = args.Request.Headers.GetHeader("Authorization");
            if (string.IsNullOrWhiteSpace(authorization)) return;
            lock (requestSync) authorizedApiRequests[type.ApiPath] = new AuthorizedApiRequest(uri, authorization);
        }
        catch (ArgumentException)
        {
            // Some non-API resource headers are unavailable after completion.
        }
    }

    private async Task<string?> ExecuteStringAsync(string script)
    {
        var raw = await RequireBrowser().ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(raw);
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + BrowserTimeout;
        while (!await condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Eczacı Kart beklenen sayfayı zamanında açmadı.");
            await Task.Delay(120, cancellationToken);
        }
    }

    private async Task EnsureBrowserAsync(bool browserDebug, CancellationToken cancellationToken)
    {
        pharmacyApp.Infrastructure.OnlineAccessPolicy.RequireEczaciKart();
        if (browser?.CoreWebView2 is not null && browserWindow is not null)
        {
            ApplyBrowserWindowMode(browserDebug, activate: browserDebug);
            return;
        }
        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "pharmacyApp";
        var profileFolderName = executableName.Equals("pharmacyApp", StringComparison.OrdinalIgnoreCase)
            ? "EczaciKartWebView2"
            : $"EczaciKartWebView2-{executableName}";
        var userDataFolder = Path.Combine(
            pharmacyApp.Infrastructure.AppPaths.DataDirectory,
            profileFolderName);
        Directory.CreateDirectory(userDataFolder);

        browser = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder }
        };
        browserWindow = new Window
        {
            Title = "Eczacı Kart Arka Plan Tarayıcısı",
            Width = 1180,
            Height = 820,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0.01,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = browser
        };
        if (Application.Current.MainWindow is { IsVisible: true } mainWindow) browserWindow.Owner = mainWindow;
        ApplyBrowserWindowMode(browserDebug, activate: false);
        browserWindow.Closed += (_, _) =>
        {
            browser?.Dispose();
            browser = null;
            browserWindow = null;
            ResetCapturedRequest();
        };

        try
        {
            browserWindow.Show();
            await browser.EnsureCoreWebView2Async().WaitAsync(BrowserTimeout, cancellationToken);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
            if (browserDebug) browserWindow.Activate();
        }
        catch
        {
            browserWindow.Close();
            throw;
        }
    }

    private void ApplyBrowserWindowMode(bool browserDebug, bool activate)
    {
        if (browserWindow is null) return;
        if (browserDebug)
        {
            var workArea = SystemParameters.WorkArea;
            browserWindow.ShowInTaskbar = true;
            browserWindow.Opacity = 1;
            browserWindow.WindowStyle = WindowStyle.SingleBorderWindow;
            browserWindow.ResizeMode = ResizeMode.CanResize;
            browserWindow.Left = workArea.Left + Math.Max(0, (workArea.Width - browserWindow.Width) / 2);
            browserWindow.Top = workArea.Top + Math.Max(0, (workArea.Height - browserWindow.Height) / 2);
            if (browserWindow.IsLoaded && !browserWindow.IsVisible) browserWindow.Show();
            if (activate && browserWindow.IsVisible) browserWindow.Activate();
            return;
        }

        browserWindow.ShowInTaskbar = false;
        browserWindow.Opacity = 0.01;
        browserWindow.WindowStyle = WindowStyle.None;
        browserWindow.ResizeMode = ResizeMode.NoResize;
        browserWindow.Left = -32000;
        browserWindow.Top = -32000;
    }

    private async Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        var currentBrowser = RequireBrowser();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? requestedNavigationId = null;
        EventHandler<CoreWebView2NavigationStartingEventArgs>? startingHandler = null;
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? completedHandler = null;
        startingHandler = (_, args) =>
        {
            if (args.Uri.Equals(url, StringComparison.OrdinalIgnoreCase))
                requestedNavigationId = args.NavigationId;
        };
        completedHandler = (_, args) =>
        {
            if (requestedNavigationId is null || args.NavigationId != requestedNavigationId.Value) return;
            if (args.IsSuccess || args.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown)
                completion.TrySetResult();
            else completion.TrySetException(
                new InvalidOperationException($"Eczacı Kart sayfası açılamadı: {args.WebErrorStatus}."));
        };
        currentBrowser.NavigationStarting += startingHandler;
        currentBrowser.NavigationCompleted += completedHandler;
        try
        {
            currentBrowser.CoreWebView2.Navigate(url);
            await completion.Task.WaitAsync(BrowserTimeout, cancellationToken);
        }
        finally
        {
            currentBrowser.NavigationStarting -= startingHandler;
            currentBrowser.NavigationCompleted -= completedHandler;
        }
    }

    private WebView2 RequireBrowser() => browser?.CoreWebView2 is not null
        ? browser
        : throw new InvalidOperationException("Eczacı Kart arka plan tarayıcısı başlatılmadı.");

    private static string EncodeDate(DateOnly date) => Uri.EscapeDataString(
        $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 00:00:00");

    private static string EncodeTimestamp(DateTime timestamp) => Uri.EscapeDataString(
        timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

    private sealed record AuthorizedApiRequest(Uri Uri, string Authorization);
}
