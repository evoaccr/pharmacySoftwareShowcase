namespace pharmacyApp.Modules.Medula;

public static class MedulaRemoteScreenRefiner
{
    public static IReadOnlyList<string> ResolveItsQrCodes(string? medulaValue, string? storedValue)
    {
        static string[] Parse(string? value) => (value ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var medulaCodes = Parse(medulaValue);
        return medulaCodes.Length > 0 ? medulaCodes : Parse(storedValue);
    }

    private static readonly string[] DrugHeaders =
    [
        "Reçete No", "İlaç Adı", "Reçete Tarihi", "İlaç Alım Tarihi",
        "Verilebileceği Tarih", "Adet", "İlaç Kullanımı",
        "Rap. Teşhisi / Rap. Tak. No", "UK"
    ];

    private static readonly IReadOnlyDictionary<string, string> ReportNewFieldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["form1:text28"] = "Rapor Numarası (*)",
            ["form1:text30"] = "Rapor Tarihi (*)",
            ["form1:text24"] = "Protokol No",
            ["form1:menu2"] = "Düzenleme Türü",
            ["form1:text120"] = "Açıklama",
            ["form1:text60"] = "Tesis Kodu (*)",
            ["form1:menu3"] = "Tanı (*)",
            ["form1:text13"] = "ICD-10 Tanı Arama",
            ["form1:text46"] = "Dr. Diploma No",
            ["form1:text25"] = "Dip. Tescil No",
            ["form1:menu5"] = "Branş",
            ["form1:menu4"] = "Hekim Türü",
            ["form1:text50"] = "Doktor Adı",
            ["form1:text52"] = "Doktor Soyadı",
            ["form1:checkbox1"] = "Doktoru Bulamadım"
        };

    public static string FindColumnValue(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> row,
        string header)
    {
        var index = headers
            .Select((text, position) => new { Text = text, Position = position })
            .FirstOrDefault(item => item.Text.Contains(header, StringComparison.OrdinalIgnoreCase))
            ?.Position ?? -1;
        return index >= 0 ? row.ElementAtOrDefault(index)?.Trim() ?? string.Empty : string.Empty;
    }

    public static string ResolveVerifiedIdentity(string scrapedIdentity, string enteredIdentity)
    {
        var scraped = scrapedIdentity.Trim();
        if (scraped.Length == 11 && scraped.All(char.IsDigit)) return scraped;
        var entered = enteredIdentity.Trim();
        return entered.Length == 11 && entered.All(char.IsDigit) ? entered : string.Empty;
    }

    public static bool ItsQrQuantitiesMatch(string? prescribed, string? scanned)
    {
        static decimal Parse(string? value)
        {
            var text = value?.Trim() ?? string.Empty;
            var turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            return decimal.TryParse(text, System.Globalization.NumberStyles.Number, turkish, out var result)
                || decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out result)
                    ? result
                    : 0m;
        }

        var expected = Parse(prescribed);
        return expected > 0m && expected == Parse(scanned);
    }

    public static IReadOnlyList<MedulaRemoteField> RefineFields(
        string screenKey,
        IReadOnlyList<MedulaRemoteField> fields)
    {
        return fields.Select(field => field.Id switch
        {
            "f:menu10" => field with { Label = "İlgili Uyarı Kodu" },
            "f:menu5" => field with { Label = "Uyarı Kodları" },
            _ when screenKey == "its-qr" && field.Id == "form1:textarea1" =>
                field with { Label = "Karekodlar", Kind = "multiline" },
            _ when screenKey == "report-new" && ReportNewFieldLabels.TryGetValue(field.Id, out var label) =>
                field with { Label = label },
            _ when screenKey == "report-new" && field.Id.StartsWith("form1:tableEx5:", StringComparison.Ordinal)
                && field.Id.EndsWith(":text83", StringComparison.Ordinal) =>
                field with { Label = $"ICD-10 Kodu {ReadRowNumber(field.Id)}" },
            _ => field
        }).ToArray();
    }

    public static IReadOnlyList<MedulaRemoteTable> RefineTables(
        string screenKey,
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        return screenKey switch
        {
            "drugs" or "eye-drugs" => RefineDrugTable(tables),
            "reports" => RefineReportList(tables),
            "report-detail" or "drug-report-detail" => RefineReportDetail(tables),
            "report-new" => [],
            "facility-search" => RefineDataTable(
                tables, "f:tableExTesisSorguList", retainRowActions: true),
            "facility-departments" => RefineSingleList(
                tables, "f:tableExKonuSorguList", "Sözleşmeli Bölüm", "Kod"),
            "branch-select" => RefineBranches(tables),
            "doctor-branch-select" => RefineDoctorBranches(tables),
            "saved-prescription-document" => RefinePrescriptionDocuments(tables),
            "its-qr" => RefineItsQrMedicationTable(tables),
            "uts-operations" => RefineUtsMedicationTable(tables),
            _ => tables
        };
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineUtsMedicationTable(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var source = tables.FirstOrDefault(table =>
            table.Id == "form1:tableExIlacBilgileri");
        if (source is null) return [];
        var rows = source.Rows
            .Select(row => AlignVisibleCells(row, 3))
            .Where(row => !LooksLikePagerRow(row))
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
            .ToArray();
        return [source with
        {
            Headers = ["Seç", "Barkod", "İlaç Adı"],
            Rows = rows,
            Title = "ÜTS Kapsamındaki İlaçlar"
        }];
    }

    private static IReadOnlyList<MedulaRemoteTable> RefinePrescriptionDocuments(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var candidates = tables
            .Where(table => table.Id.Contains("dok", StringComparison.OrdinalIgnoreCase)
                || table.Id.Contains("document", StringComparison.OrdinalIgnoreCase)
                || table.Headers.Any(header => header.Contains("doküman", StringComparison.OrdinalIgnoreCase)
                    || header.Contains("döküman", StringComparison.OrdinalIgnoreCase)
                    || header.Contains("dosya", StringComparison.OrdinalIgnoreCase))
                || table.Rows.SelectMany(row => row).Any(value =>
                    value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (candidates.Length == 0)
            candidates = tables.Where(table => table.Id.Contains("tableEx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(table => table.Headers.Count)
                .Take(1)
                .ToArray();
        return candidates
            .Select(table => table with
            {
                Rows = table.Rows.Where(row => !row.Any(value =>
                    value.Contains("reçeteye eklenmiş", StringComparison.CurrentCultureIgnoreCase)
                    && value.Contains("bulunamamaktadır", StringComparison.CurrentCultureIgnoreCase))).ToArray()
            })
            .Where(table => table.Rows.Count > 0)
            .ToArray();
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineItsQrMedicationTable(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var source = tables.FirstOrDefault(table => table.Id == "form1:tableEx1");
        if (source is null) return [];
        string[] headers = ["Barkod", "Adı", "Reçete Adet", "Karekod Adet"];
        var rows = source.Rows
            .Select(row => AlignVisibleCells(row, headers.Length))
            .Where(row => !LooksLikePagerRow(row))
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
            .ToArray();
        return [new MedulaRemoteTable(
            source.Id,
            headers,
            rows,
            null,
            source.PageActions,
            "İlaç Listesi")];
    }

    private static int ReadRowNumber(string id)
    {
        var parts = id.Split(':');
        return parts.Length > 2 && int.TryParse(parts[^2], out var index) ? index + 1 : 1;
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineDrugTable(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var source = tables.FirstOrDefault(table => table.Id == "form1:tableExKisiIlacList");
        if (source is null) return [];
        var rows = source.Rows
            .Select(row => AlignDrugCells(source.Headers, row))
            .Where(row => !LooksLikePagerRow(row))
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
            .ToArray();
        return [new MedulaRemoteTable(
            source.Id,
            DrugHeaders,
            rows,
            null,
            source.PageActions,
            "İlaç Geçmişi")];
    }

    private static IReadOnlyList<string> AlignDrugCells(
        IReadOnlyList<string> sourceHeaders,
        IReadOnlyList<string> row)
    {
        var offset = Math.Max(0, row.Count - sourceHeaders.Count);
        while (offset > 0 && !string.IsNullOrWhiteSpace(row.ElementAtOrDefault(offset - 1))) offset--;
        var normalizedHeaders = sourceHeaders.Select(NormalizeHeader).ToArray();
        var mapped = new[]
        {
            FindDrugHeader(normalizedHeaders, value => value.Contains("RECETENO", StringComparison.Ordinal)),
            FindDrugHeader(normalizedHeaders, value => value.Contains("ILACADI", StringComparison.Ordinal)),
            FindDrugHeader(normalizedHeaders, value => value.Contains("RECETETAR", StringComparison.Ordinal)),
            FindDrugHeader(normalizedHeaders, value => value.Contains("ILACALIM", StringComparison.Ordinal)),
            FindDrugHeader(normalizedHeaders, value => value.Contains("VERILEBILECEGI", StringComparison.Ordinal)),
            FindDrugHeader(normalizedHeaders, value => value == "ADET"),
            FindDrugHeader(normalizedHeaders, value => value.Contains("ILACKULLANIM", StringComparison.Ordinal)),
            FindDrugHeader(normalizedHeaders, value => value.Contains("RAP", StringComparison.Ordinal)
                && (value.Contains("TESHIS", StringComparison.Ordinal)
                    || value.Contains("TAK", StringComparison.Ordinal))),
            FindDrugHeader(normalizedHeaders, value => value == "UK")
        };
        if (mapped.Count(index => index >= 0) < DrugHeaders.Length - 1)
            return AlignVisibleCells(row, DrugHeaders.Length);

        return mapped.Select((sourceIndex, targetIndex) =>
        {
            var index = sourceIndex >= 0 ? sourceIndex + offset : targetIndex + offset;
            return row.ElementAtOrDefault(index) ?? string.Empty;
        }).ToArray();
    }

    private static int FindDrugHeader(
        IReadOnlyList<string> headers,
        Func<string, bool> predicate)
    {
        for (var index = 0; index < headers.Count; index++)
            if (predicate(headers[index])) return index;
        return -1;
    }

    private static string NormalizeHeader(string value)
    {
        var upper = value.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))
            .Replace('İ', 'I')
            .Replace('Ş', 'S')
            .Replace('Ğ', 'G')
            .Replace('Ü', 'U')
            .Replace('Ö', 'O')
            .Replace('Ç', 'C');
        return new string(upper.Where(char.IsLetterOrDigit).ToArray());
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineReportList(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var source = tables.FirstOrDefault(table => table.Id == "form1:tableExRaporTeshisList");
        if (source is null) return [];
        string[] headers = ["Rapor Takip No", "Rapor No", "Başlangıç Tarihi", "Bitiş Tarihi", "Kayıt Şekli", "Tanı"];
        var reportTable = new MedulaRemoteTable(
            source.Id,
            headers,
            source.Rows.Select(row => AlignReportListCells(row, headers.Length)).ToArray(),
            source.RowActionIds,
            source.PageActions,
            "Raporlar");
        var linkedTables = tables
            .Where(table => !ReferenceEquals(table, source) && table.Id != source.Id)
            .Where(table => table.Rows.Any(row => row.Any(value => !string.IsNullOrWhiteSpace(value))))
            .Select(table => table with { Title = table.Title ?? "Rapor Reçete Bağlantıları" })
            .ToArray();
        return [reportTable, .. linkedTables];
    }

    private static IReadOnlyList<string> AlignReportListCells(IReadOnlyList<string> row, int visibleCount)
    {
        var offset = 0;
        while (row.Count - offset > visibleCount
            && string.IsNullOrWhiteSpace(row.ElementAtOrDefault(offset)))
            offset++;
        var retainedCount = Math.Max(visibleCount, row.Count - offset);
        return Enumerable.Range(0, retainedCount)
            .Select(index => row.ElementAtOrDefault(index + offset) ?? string.Empty)
            .ToArray();
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineReportDetail(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var definitions = new (string Id, string Title, string[] Headers)[]
        {
            ("form1:tableEx2", "Açıklamalar", ["Açıklama", "Eklenme Zamanı"]),
            ("form1:tableExRaporTeshisList", "Tanı Bilgileri", ["Tanı / ICD-10", "Detay", "Başlangıç", "Bitiş"]),
            ("form1:tableExRaporDoktorList", "Doktor Bilgileri", ["Dr. Diploma No", "Dip. Tescil No", "Branş", "Adı", "Soyadı"]),
            ("form1:tableEx1", "Rapor Etkin Madde Bilgileri", ["SGK Kodu", "Adı", "Form", "Tedavi Şeması", "Adet / Miktar", "İçerik Miktarı", "Eklenme Zamanı", "Açıklama"]),
            ("form1:tableEx5", "Rapor İlave Değer Bilgileri", ["Türü", "Değeri", "Açıklama", "Eklenme Zamanı"])
        };
        var output = new List<MedulaRemoteTable>();
        foreach (var definition in definitions)
        {
            var source = tables.FirstOrDefault(table => table.Id == definition.Id);
            if (source is null) continue;
            var rows = source.Rows
                .Select((row, index) => definition.Id == "form1:tableExRaporTeshisList"
                    ? AlignReportDiagnosisRow(tables, row, index)
                    : AlignVisibleCells(row, definition.Headers.Length))
                .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
                .ToArray();
            if (rows.Length == 0) continue;
            output.Add(new MedulaRemoteTable(
                source.Id,
                definition.Headers,
                rows,
                null,
                source.PageActions,
                definition.Title));
        }
        return output;
    }

    private static IReadOnlyList<string> AlignReportDiagnosisRow(
        IReadOnlyList<MedulaRemoteTable> tables,
        IReadOnlyList<string> row,
        int rowIndex)
    {
        var aligned = AlignVisibleCells(row, 4).ToArray();
        var detailTable = tables.FirstOrDefault(table => table.Id.Equals(
            $"form1:tableExRaporTeshisList:{rowIndex}:tableEx4",
            StringComparison.Ordinal));
        if (detailTable is null) return aligned;
        var details = detailTable.Rows
            .Select(detailRow => detailRow.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray())
            .Where(values => values.Length > 0)
            .Select(values => string.Join(" — ", values))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (details.Length > 0) aligned[1] = string.Join(Environment.NewLine, details);
        return aligned;
    }

    private static IReadOnlyList<string> AlignVisibleCells(IReadOnlyList<string> row, int visibleCount)
    {
        var offset = 0;
        while (row.Count - offset > visibleCount
            && string.IsNullOrWhiteSpace(row.ElementAtOrDefault(offset)))
            offset++;
        return Enumerable.Range(0, visibleCount)
            .Select(index => row.ElementAtOrDefault(index + offset) ?? string.Empty)
            .ToArray();
    }

    private static bool LooksLikePagerRow(IReadOnlyList<string> row)
    {
        var values = row.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (values.Length != 1) return false;
        var tokens = values[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens.All(token => int.TryParse(token, out var page) && page > 0);
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineDataTable(
        IReadOnlyList<MedulaRemoteTable> tables,
        string tableId,
        bool retainRowActions)
    {
        var source = tables.FirstOrDefault(table => table.Id == tableId);
        if (source is null) return [];

        var columnCount = Math.Max(
            source.Headers.Count,
            source.Rows.Count == 0 ? 0 : source.Rows.Max(row => row.Count));
        var retainedColumns = Enumerable.Range(0, columnCount)
            .Where(index => source.Rows.Any(row =>
                index < row.Count && !string.IsNullOrWhiteSpace(row[index]))
                )
            .ToArray();
        if (retainedColumns.Length == 0) return [];

        var headers = retainedColumns
            .Select((sourceIndex, outputIndex) =>
            {
                var header = source.Headers.ElementAtOrDefault(sourceIndex);
                return string.IsNullOrWhiteSpace(header) ? $"Alan {outputIndex + 1}" : header;
            })
            .ToArray();
        var rows = source.Rows
            .Select(row => (IReadOnlyList<string>)retainedColumns
                .Select(index => row.ElementAtOrDefault(index) ?? string.Empty)
                .ToArray())
            .ToArray();
        return [new MedulaRemoteTable(
            source.Id,
            headers,
            rows,
            retainRowActions ? source.RowActionIds : null,
            source.PageActions)];
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineSingleList(
        IReadOnlyList<MedulaRemoteTable> tables,
        string tableId,
        string primaryHeader,
        string secondaryHeader)
    {
        var source = tables.FirstOrDefault(table => table.Id == tableId);
        if (source is null) return [];

        var rows = source.Rows
            .Select((row, index) => new
            {
                Values = (IReadOnlyList<string>)row.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                ActionId = source.RowActionIds?.ElementAtOrDefault(index) ?? string.Empty
            })
            .Where(row => row.Values.Count > 0)
            .ToArray();
        var columnCount = rows.Length == 0 ? 1 : rows.Max(row => row.Values.Count);
        var headers = Enumerable.Range(0, columnCount)
            .Select(index => index switch
            {
                0 => primaryHeader,
                1 => secondaryHeader,
                _ => $"Alan {index + 1}"
            })
            .ToArray();
        return [new MedulaRemoteTable(
            source.Id,
            headers,
            rows.Select(row => row.Values).ToArray(),
            rows.Select(row => row.ActionId).ToArray(),
            source.PageActions)];
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineBranches(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var source = tables.FirstOrDefault(table => table.Id == "f:tableExBransList");
        if (source is null) return [];

        var entries = new List<(bool IsChild, string Parent, string Name, string Action)>();
        var outputRows = new List<IReadOnlyList<string>>();
        var outputActions = new List<string>();
        var outputParents = new List<string>();
        var parent = string.Empty;
        for (var index = 0; index < source.Rows.Count; index++)
        {
            var raw = source.Rows[index].FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                ?? string.Empty;
            if (raw.Length == 0) continue;
            var isChild = raw.StartsWith("|-->", StringComparison.Ordinal)
                || raw.StartsWith("|—>", StringComparison.Ordinal);
            var name = raw.TrimStart('|', '-', '—', '>', ' ').Trim();
            if (!isChild) parent = name;
            entries.Add((isChild, parent, name, source.RowActionIds?.ElementAtOrDefault(index) ?? string.Empty));
        }
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var hasFollowingSibling = entry.IsChild && index + 1 < entries.Count
                && entries[index + 1].IsChild
                && entries[index + 1].Parent == entry.Parent;
            outputRows.Add(entry.IsChild
                ? [string.Empty, $"{(hasFollowingSibling ? "├──›" : "└──›")}  {entry.Name}"]
                : [entry.Name, string.Empty]);
            outputActions.Add(entry.Action);
            outputParents.Add(entry.Parent);
        }

        return [new MedulaRemoteTable(
            source.Id,
            ["Ana Branş", "Alt Branş"],
            outputRows,
            outputActions,
            source.PageActions,
            "Branşlar",
            outputParents)];
    }

    private static IReadOnlyList<MedulaRemoteTable> RefineDoctorBranches(
        IReadOnlyList<MedulaRemoteTable> tables)
    {
        var source = tables.FirstOrDefault(table => table.Id == "f:tableExDoktorBransList");
        if (source is null) return [];

        var rows = source.Rows
            .Select((row, index) => new
            {
                Name = row.LastOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                    ?? string.Empty,
                Action = source.RowActionIds?.ElementAtOrDefault(index) ?? string.Empty
            })
            .Where(row => row.Name.Length > 0 && row.Action.Length > 0)
            .ToArray();
        return [new MedulaRemoteTable(
            source.Id,
            ["Branş"],
            rows.Select(row => (IReadOnlyList<string>)[row.Name]).ToArray(),
            rows.Select(row => row.Action).ToArray(),
            source.PageActions,
            "Doktor Branşları")];
    }
}
