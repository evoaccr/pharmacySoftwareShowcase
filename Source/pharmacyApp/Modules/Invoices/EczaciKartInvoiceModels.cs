using System.Globalization;
using System.Text.Json;

namespace pharmacyApp.Modules.Invoices;

public sealed record EczaciKartInvoiceType(
    string Key,
    string DisplayName,
    string DashboardTitle,
    string ApiPath,
    bool IsIncoming,
    bool IsDispatch = false,
    bool RequiresPrefix = false)
{
    public static IReadOnlyList<EczaciKartInvoiceType> All { get; } =
    [
        new("incoming-invoice", "Gelen Fatura", "GELEN FATURALAR", "/inbox/getInboxes", true),
        new("outgoing-invoice", "Giden Fatura", "GİDEN FATURALAR", "/outbox/getOutboxes", false),
        new("archive-invoice", "E-Arşiv Faturası", "e-ARŞİV FATURALAR", "/eArchive/getArchiveInvoices", false),
        new("incoming-dispatch", "Gelen İrsaliye", "GELEN İRSALİYELER", "/despatchInbox/fetch", true, true),
        new("outgoing-dispatch", "Giden İrsaliye", "GİDEN İRSALİYELER", "/despatchOutbox/fetch", false, true, true)
    ];

    public static EczaciKartInvoiceType FromKey(string key) =>
        All.FirstOrDefault(item => item.Key.Equals(key, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(key), key, "Bilinmeyen Eczacı Kart belge türü.");
}

public sealed record EczaciKartInvoiceItem(
    string TypeKey,
    string TypeDisplay,
    string DocumentNumber,
    string Ettn,
    string IssueDate,
    string CounterpartyName,
    string CounterpartyTaxId,
    string Scenario,
    string Amount,
    string Currency,
    string Status,
    string RawJson,
    bool? IsRead = null,
    DateTimeOffset? ReceivedAt = null);

public sealed record EczaciKartInvoiceDetailRow(IReadOnlyList<string> Cells);

public sealed record EczaciKartInvoiceScanProgress(
    string TypeKey,
    string TypeName,
    int CompletedMonths,
    int TotalMonths,
    int DocumentsFound,
    int ExpectedDocuments);

public sealed record EczaciKartDashboardCount(string TypeKey, string TypeName, int Count);

public sealed record EczaciKartInvoiceDetailTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<EczaciKartInvoiceDetailRow> Rows)
{
    public static EczaciKartInvoiceDetailTable Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryArray(root, "headers", out var headerArray)
            || !TryArray(root, "rows", out var rowArray))
            throw new InvalidOperationException("Eczacı Kart fatura içeriği tablosu okunamadı.");

        var headers = headerArray.EnumerateArray()
            .Select(ReadCell)
            .ToList();
        var sourceRows = rowArray.EnumerateArray()
            .Where(row => row.ValueKind == JsonValueKind.Array)
            .Select(row => row.EnumerateArray().Select(ReadCell).ToList())
            .Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .ToList();
        var columnCount = Math.Max(headers.Count, sourceRows.Count == 0 ? 0 : sourceRows.Max(row => row.Count));
        if (columnCount == 0)
            return new EczaciKartInvoiceDetailTable([], []);

        while (headers.Count < columnCount) headers.Add($"Sütun {headers.Count + 1}");
        for (var index = 0; index < headers.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(headers[index])) headers[index] = $"Sütun {index + 1}";
        }

        var rows = sourceRows.Select(row =>
        {
            while (row.Count < columnCount) row.Add(string.Empty);
            if (row.Count > columnCount) row.RemoveRange(columnCount, row.Count - columnCount);
            return new EczaciKartInvoiceDetailRow(row);
        }).ToArray();
        return new EczaciKartInvoiceDetailTable(headers, rows);
    }

    private static bool TryArray(JsonElement item, string propertyName, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string ReadCell(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()?.Trim() ?? string.Empty
        : value.ToString().Trim();
}

public static class EczaciKartInvoiceMapper
{
    public static IReadOnlyList<EczaciKartInvoiceItem> MapPage(
        EczaciKartInvoiceType type,
        string pageJson)
    {
        using var document = JsonDocument.Parse(pageJson);
        var root = document.RootElement;
        var content = root.ValueKind == JsonValueKind.Array
            ? root
            : TryProperty(root, "content", out var pageContent) ? pageContent : default;
        if (content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Eczacı Kart liste yanıtında belge dizisi bulunamadı.");

        return content.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => MapItem(type, item))
            .ToArray();
    }

    public static int ReadTotalPages(string pageJson)
    {
        using var document = JsonDocument.Parse(pageJson);
        return ReadInt(document.RootElement, "totalPages") ?? 1;
    }

    public static int ReadTotalElements(string pageJson)
    {
        using var document = JsonDocument.Parse(pageJson);
        return ReadInt(document.RootElement, "totalElements") ?? MapPage(
            EczaciKartInvoiceType.All[0], pageJson).Count;
    }

    private static EczaciKartInvoiceItem MapItem(EczaciKartInvoiceType type, JsonElement item)
    {
        var counterpartyName = type.IsIncoming
            ? Read(item, "sourceTitle", "senderTitle", "supplierTitle")
            : Read(item, "destinationTitle", "receiverTitle", "customerTitle");
        var counterpartyTaxId = type.IsIncoming
            ? Read(item, "sourceId", "senderId", "supplierId")
            : Read(item, "destinationId", "receiverId", "customerId");
        var scenario = Read(item, "documentProfile", "profileId", "despatchProfileId", "scenarioTypeCode");
        var status = Read(item, "statusText", "documentStatus", "portalStatus", "processState", "responseCode");
        if (string.IsNullOrWhiteSpace(status)) status = scenario;

        return new EczaciKartInvoiceItem(
            type.Key,
            type.DisplayName,
            Read(item, "documentId", "documentNumber", "despatchId"),
            Read(item, "documentUuid", "documentUUID", "uuid"),
            FormatDate(Read(item, "documentIssueDate", "issueDate")),
            counterpartyName,
            counterpartyTaxId,
            scenario,
            type.IsDispatch ? string.Empty : Read(item, "payableAmount", "taxInclusiveAmount", "lineExtensionAmount"),
            type.IsDispatch ? string.Empty : Read(item, "currencyCode", "documentCurrencyCode"),
            status,
            item.GetRawText(),
            ReadBoolean(item, "isRead", "read", "viewed"),
            ParseTimestamp(Read(item, "receivedDate", "createdDate", "createDate")));
    }

    private static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var timestamp)
            ? timestamp
            : null;

    private static bool? ReadBoolean(JsonElement item, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryProperty(item, propertyName, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number != 0;
            if (bool.TryParse(value.ToString(), out var boolean)) return boolean;
            if (int.TryParse(value.ToString(), out number)) return number != 0;
        }
        return null;
    }

    private static string Read(JsonElement item, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryProperty(item, propertyName, out var value)) continue;
            var result = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "Evet",
                JsonValueKind.False => "Hayır",
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        return string.Empty;
    }

    private static int? ReadInt(JsonElement item, string propertyName)
    {
        if (!TryProperty(item, propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool TryProperty(JsonElement item, string propertyName, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string FormatDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"))
            : value;
    }
}
