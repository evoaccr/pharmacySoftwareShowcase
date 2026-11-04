using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Invoices;

public sealed class EczaciKartXmlDownloadException(
    HttpStatusCode statusCode,
    string message) : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed record EczaciKartXmlPageSelection(
    string TypeKey,
    string TypeName,
    DateOnly StartDate,
    DateOnly EndDate,
    int PageIndex,
    int TotalDocuments,
    int TotalPages,
    IReadOnlyList<string> RecordIds,
    string RecordsHash,
    Uri ExportRequestUri);

public sealed partial class EczaciKartConnectionService
{
    public const int XmlExportPageSize = 250;
    public const int XmlExportPortalBatchLimit = 20;

    public async Task<EczaciKartXmlPageSelection> CaptureXmlExportPageAsync(
        string typeKey,
        DateOnly startDate,
        DateOnly endDate,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var type = EczaciKartInvoiceType.FromKey(typeKey);
        if (type.IsDispatch)
            throw new NotSupportedException("XML invoice export does not support dispatch documents.");

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
                settings.EczaciKartUsername, settings.EczaciKartPassword, cancellationToken);
            await OpenDashboardAsync(cancellationToken);
            await OpenPermittedDetailAsync(type, cancellationToken);
            await ApplyDateRangeAsync(type, startDate, endDate, cancellationToken);
            await SelectAllArchiveStatesAsync(type, cancellationToken);
            await SetXmlExportPageSizeAsync(cancellationToken);
            await NavigateToXmlExportPageAsync(pageIndex, cancellationToken);

            await WaitUntilAsync(async () =>
            {
                var state = await ReadXmlExportPageStateAsync(cancellationToken);
                var remaining = Math.Max(0, state.TotalDocuments - pageIndex * XmlExportPageSize);
                return state.CurrentPage == pageIndex + 1
                    && state.RowCount == Math.Min(XmlExportPageSize, remaining);
            }, cancellationToken);

            var page = await ReadXmlExportPageStateAsync(cancellationToken);
            var totalPages = page.TotalDocuments == 0
                ? 0
                : (int)Math.Ceiling(page.TotalDocuments / (double)XmlExportPageSize);
            if (pageIndex >= totalPages || page.RowCount == 0)
                return new EczaciKartXmlPageSelection(
                    type.Key, type.DisplayName, startDate, endDate, pageIndex,
                    page.TotalDocuments, totalPages, [], string.Empty, new Uri(DashboardUrl));

            var capturedUri = await CaptureSelectedXmlRequestAsync(type, page.RowCount, cancellationToken);
            var recordIds = ReadQueryValues(capturedUri, "records")
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (recordIds.Length != page.RowCount)
                throw new InvalidOperationException(
                    $"Portal selected {page.RowCount} rows but exposed {recordIds.Length} XML record identifiers.");
            var hash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join(',', recordIds))))
                .ToLowerInvariant();
            return new EczaciKartXmlPageSelection(
                type.Key, type.DisplayName, startDate, endDate, pageIndex,
                page.TotalDocuments, totalPages, recordIds, hash, capturedUri);
        }
        finally
        {
            browserGate.Release();
        }
    }

    public async Task<byte[]> DownloadXmlChunkAsync(
        EczaciKartXmlPageSelection selection,
        IReadOnlyList<string> recordIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return await DownloadXmlChunkAsync(
            selection.TypeKey, selection.StartDate.Year, selection.StartDate.Month - 1,
            recordIds, cancellationToken);
    }

    public async Task<byte[]> DownloadXmlChunkAsync(
        string typeKey,
        int year,
        int zeroBasedMonth,
        IReadOnlyList<string> recordIds,
        CancellationToken cancellationToken = default)
    {
        if (recordIds.Count is < 1 or > XmlExportPortalBatchLimit)
            throw new ArgumentOutOfRangeException(
                nameof(recordIds), $"The portal accepts 1-{XmlExportPortalBatchLimit} XML records per request.");
        var type = EczaciKartInvoiceType.FromKey(typeKey);
        if (type.Key != "incoming-invoice")
            throw new NotSupportedException($"{type.DisplayName} toplu XML indirmeyi desteklemiyor.");
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            var settings = ApplicationSettingsStore.Current;
            await EnsureBrowserAsync(settings.EczaciKartBrowserDebug, cancellationToken);
            await EnsureAuthenticatedAsync(
                settings.EczaciKartUsername, settings.EczaciKartPassword, cancellationToken);
            var endpoint = type.ApiPath[..type.ApiPath.LastIndexOf('/')]
                + "/downloadSelected";
            var uri = ReplaceQueryValues(
                new Uri($"{ApiBaseUrl}{endpoint}?year={year}&month={zeroBasedMonth}&type=xmlZip"),
                "records", recordIds);
            for (var authorizationAttempt = 0; authorizationAttempt < 2; authorizationAttempt++)
            {
                var authorizedRequest = await GetDownloadAuthorizationAsync(
                    type, year, zeroBasedMonth, settings, authorizationAttempt > 0, cancellationToken);
                using var client = await CreateAuthorizedClientAsync(authorizedRequest.Authorization);
                using var response = await client.PostAsync(uri, null, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    ResetCapturedRequest(type);
                    if (authorizationAttempt == 0) continue;
                    throw new InvalidOperationException("Eczacı Kart XML indirme isteğini yetkilendirmedi.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new EczaciKartXmlDownloadException(response.StatusCode,
                        $"Eczacı Kart XML indirme hatası ({(int)response.StatusCode}): {detail}");
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                    throw new InvalidOperationException("Eczacı Kart XML yanıtı geçerli bir ZIP dosyası değil.");
                return bytes;
            }
            throw new InvalidOperationException("Eczacı Kart XML indirme yetkilendirmesi yenilenemedi.");
        }
        finally
        {
            browserGate.Release();
        }
    }

    public async Task<byte[]> DownloadSingleXmlAsync(
        string typeKey,
        string documentUuid,
        int year,
        int zeroBasedMonth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentUuid))
            throw new ArgumentException("Document UUID is required.", nameof(documentUuid));
        var type = EczaciKartInvoiceType.FromKey(typeKey);
        await browserGate.WaitAsync(cancellationToken);
        try
        {
            var settings = ApplicationSettingsStore.Current;
            await EnsureBrowserAsync(settings.EczaciKartBrowserDebug, cancellationToken);
            await EnsureAuthenticatedAsync(
                settings.EczaciKartUsername, settings.EczaciKartPassword, cancellationToken);
            var endpoint = type.Key switch
            {
                "incoming-invoice" => "inbox/downloadMedia/xml",
                "outgoing-invoice" => "outbox/downloadMedia/xml",
                "archive-invoice" => "eArchive/downloadMedia/xml",
                "incoming-dispatch" => "despatchInbox/downloadMedia/xml",
                "outgoing-dispatch" => "despatchOutbox/downloadMedia/xml",
                _ => throw new NotSupportedException($"{type.DisplayName} XML indirmesi desteklenmiyor.")
            };
            var requestJson = JsonSerializer.Serialize(new
            {
                documentUuid = documentUuid.Trim(),
                year,
                month = zeroBasedMonth
            });
            for (var authorizationAttempt = 0; authorizationAttempt < 2; authorizationAttempt++)
            {
                var authorizedRequest = await GetDownloadAuthorizationAsync(
                    type, year, zeroBasedMonth, settings, authorizationAttempt > 0, cancellationToken);
                using var client = await CreateAuthorizedClientAsync(authorizedRequest.Authorization);
                using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync($"api/{endpoint}", content, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    ResetCapturedRequest(type);
                    if (authorizationAttempt == 0) continue;
                    throw new InvalidOperationException("Eczacı Kart XML indirme isteğini yetkilendirmedi.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new EczaciKartXmlDownloadException(response.StatusCode,
                        $"Eczacı Kart {type.DisplayName} XML indirme hatası ({(int)response.StatusCode}): {detail}");
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0)
                    throw new InvalidOperationException($"Eczacı Kart {type.DisplayName} için boş XML döndürdü.");
                var text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 512))).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                if (!text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                    && !text.StartsWith('<'))
                    throw new InvalidOperationException($"Eczacı Kart {type.DisplayName} için geçerli XML döndürmedi.");
                return bytes;
            }
            throw new InvalidOperationException("Eczacı Kart XML indirme yetkilendirmesi yenilenemedi.");
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task<AuthorizedApiRequest> GetDownloadAuthorizationAsync(
        EczaciKartInvoiceType type,
        int year,
        int zeroBasedMonth,
        ApplicationSettings settings,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && TryGetAuthorizedRequest(type, out var existing)) return existing;

        ResetCapturedRequest(type);
        if (forceRefresh)
        {
            await NavigateAsync(LoginUrl, cancellationToken);
            await EnsureAuthenticatedAsync(
                settings.EczaciKartUsername, settings.EczaciKartPassword, cancellationToken);
        }

        var month = new DateOnly(year, checked(zeroBasedMonth + 1), 1);
        await OpenDashboardAsync(cancellationToken);
        await OpenPermittedDetailAsync(type, cancellationToken);
        await ApplyDateRangeAsync(type, month, month.AddMonths(1).AddDays(-1), cancellationToken);
        return RequireAuthorizedRequest(type);
    }

    private async Task SelectAllArchiveStatesAsync(
        EczaciKartInvoiceType type,
        CancellationToken cancellationToken)
    {
        const string filterButton = "Array.from(document.querySelectorAll('button.stickButton'))"
            + ".find(b=>Array.from(b.nextElementSibling?.querySelectorAll('a.dropdown-item')??[])"
            + ".some(e=>(e.textContent||'').trim()==='Tümü'))";
        var hasFilter = await ExecuteBooleanAsync($"!!({filterButton})");
        if (!hasFilter) return;
        var current = await ExecuteStringAsync(
            $"({filterButton})?.textContent?.trim() ?? ''");
        if (string.Equals(current, "Tümü", StringComparison.OrdinalIgnoreCase)) return;
        ResetCapturedRequest(type);
        var selected = await ExecuteBooleanAsync($$"""
            (()=>{const menu=({{filterButton}})?.nextElementSibling;
              const option=Array.from(menu?.querySelectorAll('a.dropdown-item')??[])
                .find(e=>(e.textContent||'').trim()==='Tümü');
              if(!option)return false;option.click();return true;})()
            """);
        if (!selected) throw new InvalidOperationException("Eczacı Kart Tümü arşiv filtresi bulunamadı.");
        await WaitUntilAsync(() => HasAuthorizedRequestAsync(type), cancellationToken);
        await WaitUntilAsync(async () =>
            string.Equals(await ExecuteStringAsync(
                $"({filterButton})?.textContent?.trim() ?? ''"),
                "Tümü", StringComparison.OrdinalIgnoreCase), cancellationToken);
        await Task.Delay(800, cancellationToken);
    }

    private async Task SetXmlExportPageSizeAsync(CancellationToken cancellationToken)
    {
        var current = await ExecuteStringAsync(
            "document.querySelector('.rc-pagination-options-size-changer .rc-select-selection-item')?.textContent?.trim() ?? ''");
        var initialState = await ReadXmlExportPageStateAsync(cancellationToken);
        var expectedRows = Math.Min(initialState.TotalDocuments, XmlExportPageSize);
        if (current?.StartsWith(XmlExportPageSize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) == true
            && initialState.RowCount == expectedRows) return;
        await ClickDomElementAsync(
            "document.querySelector('.rc-pagination-options-size-changer .rc-select-selector')",
            cancellationToken);
        await WaitUntilAsync(async () => await ExecuteBooleanAsync(
            $"!!Array.from(document.querySelectorAll('.rc-select-item-option')).find(e=>(e.getAttribute('title')||'').startsWith('{XmlExportPageSize} '))"),
            cancellationToken);
        await ClickDomElementAsync(
            $"Array.from(document.querySelectorAll('.rc-select-item-option')).find(e=>(e.getAttribute('title')||'').startsWith('{XmlExportPageSize} '))",
            cancellationToken);
        await WaitUntilAsync(async () =>
            (await ExecuteStringAsync(
                "document.querySelector('.rc-pagination-options-size-changer .rc-select-selection-item')?.textContent?.trim() ?? ''"))
            ?.StartsWith(XmlExportPageSize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) == true,
            cancellationToken);
        await WaitUntilAsync(async () =>
        {
            var state = await ReadXmlExportPageStateAsync(cancellationToken);
            return state.RowCount == Math.Min(state.TotalDocuments, XmlExportPageSize);
        }, cancellationToken);
    }

    private async Task NavigateToXmlExportPageAsync(int pageIndex, CancellationToken cancellationToken)
    {
        for (var currentIndex = 0; currentIndex < pageIndex; currentIndex++)
        {
            var state = await ReadXmlExportPageStateAsync(cancellationToken);
            if (state.NextDisabled)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "Requested portal page does not exist.");
            await ClickDomElementAsync(
                "document.querySelector('.rc-pagination-next:not(.rc-pagination-disabled) button')",
                cancellationToken);
            var expectedPage = currentIndex + 2;
            await WaitUntilAsync(async () =>
                (await ReadXmlExportPageStateAsync(cancellationToken)).CurrentPage == expectedPage,
                cancellationToken);
        }
    }

    private async Task<XmlExportPageState> ReadXmlExportPageStateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await ExecuteStringAsync("""
            (()=>{
              const totalText=document.querySelector('.rc-pagination-total-text')?.textContent||'';
              const totalMatch=totalText.replace(/\./g,'').match(/\d+/);
              const pageText=document.querySelector('.rc-pagination-item-active')?.textContent||'1';
              return JSON.stringify({
                TotalDocuments:totalMatch?Number.parseInt(totalMatch[0],10):0,
                CurrentPage:Number.parseInt(pageText,10)||1,
                RowCount:document.querySelectorAll('tbody input[type=checkbox]').length,
                NextDisabled:document.querySelector('.rc-pagination-next')?.classList.contains('rc-pagination-disabled')??true
              });
            })()
            """);
        return string.IsNullOrWhiteSpace(json)
            ? throw new InvalidOperationException("Eczacı Kart sayfa durumu okunamadı.")
            : JsonSerializer.Deserialize<XmlExportPageState>(json)
                ?? throw new InvalidOperationException("Eczacı Kart sayfa durumu çözümlenemedi.");
    }

    private async Task<Uri> CaptureSelectedXmlRequestAsync(
        EczaciKartInvoiceType type,
        int expectedRows,
        CancellationToken cancellationToken)
    {
        await RequireBrowser().ExecuteScriptAsync(
            "document.querySelector('thead input[type=checkbox]')?.click()");
        await WaitUntilAsync(async () =>
        {
            var raw = await RequireBrowser().ExecuteScriptAsync(
                "Array.from(document.querySelectorAll('tbody input[type=checkbox]')).filter(e=>e.checked).length");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                && count == expectedRows;
        }, cancellationToken);

        var endpoint = type.ApiPath[..type.ApiPath.LastIndexOf('/')]
            + "/downloadSelected";
        var completion = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            var uri = new Uri(args.Request.Uri);
            if (uri.AbsolutePath.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase))
                completion.TrySetResult(uri);
        }

        RequireBrowser().CoreWebView2.WebResourceResponseReceived += Handler;
        try
        {
            var clicked = await ExecuteBooleanAsync("""
                (()=>{const normalize=v=>(v||'').replace(/\s+/g,' ').trim().toLocaleUpperCase('tr-TR');
                  const e=Array.from(document.querySelectorAll('button,a'))
                  .find(e=>normalize(e.textContent)==='TOPLU XML İNDİR');
                  if(!e||e.disabled)return false;e.click();return true;})()
                """);
            if (!clicked)
            {
                var available = await ExecuteStringAsync("""
                    Array.from(document.querySelectorAll('button,a'))
                      .map(e=>(e.textContent||'').replace(/\s+/g,' ').trim())
                      .filter(t=>/xml|indir/i.test(t)).filter((t,i,a)=>a.indexOf(t)===i).join(' | ')
                    """);
                throw new InvalidOperationException(
                    $"Eczacı Kart Toplu Xml İndir düğmesi etkinleştirilemedi. Kullanılabilir işlemler: {available}");
            }
            var completed = await Task.WhenAny(
                completion.Task, Task.Delay(BrowserTimeout, cancellationToken));
            if (completed != completion.Task)
                throw new TimeoutException("Eczacı Kart XML indirme isteği yakalanamadı.");
            return await completion.Task;
        }
        finally
        {
            RequireBrowser().CoreWebView2.WebResourceResponseReceived -= Handler;
        }
    }

    private async Task ClickDomElementAsync(
        string expression,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await RequireBrowser().ExecuteScriptAsync(
            "(()=>{const e=" + expression + ";if(!e)return null;"
            + "e.scrollIntoView({block:'center',inline:'nearest'});const r=e.getBoundingClientRect();"
            + "return {x:r.left+r.width/2,y:r.top+r.height/2};})()");
        using var point = JsonDocument.Parse(raw);
        if (point.RootElement.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("Eczacı Kart denetimi bulunamadı.");
        var x = point.RootElement.GetProperty("x").GetDouble();
        var y = point.RootElement.GetProperty("y").GetDouble();
        var pressed = JsonSerializer.Serialize(new
            { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        var released = JsonSerializer.Serialize(new
            { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
        await RequireBrowser().CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent", pressed);
        await RequireBrowser().CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent", released);
    }

    private async Task<bool> ExecuteBooleanAsync(string script) =>
        (await RequireBrowser().ExecuteScriptAsync(script))
        .Equals("true", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadQueryValues(Uri uri, string requestedKey)
    {
        var values = new List<string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (!Uri.UnescapeDataString(pair[0]).Equals(requestedKey, StringComparison.OrdinalIgnoreCase)) continue;
            values.Add(pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
        }
        return values;
    }

    private static Uri ReplaceQueryValues(Uri uri, string key, IEnumerable<string> values)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0])
                .Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        query.AddRange(values.Select(value =>
            $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}"));
        return new UriBuilder(uri) { Query = string.Join('&', query) }.Uri;
    }

    private sealed record XmlExportPageState(
        int TotalDocuments,
        int CurrentPage,
        int RowCount,
        bool NextDisabled);
}
