namespace pharmacyApp.Modules.Medula;

public sealed record MedulaRemoteOption(string Value, string Text);

public sealed record MedulaRemoteField(
    string Id,
    string Label,
    string Value,
    string Kind,
    bool IsReadOnly,
    IReadOnlyList<MedulaRemoteOption> Options);

public sealed record MedulaRemoteAction(
    string Id,
    string Caption,
    string Context);

public sealed record MedulaTreatmentSchemeCell(
    string FieldId,
    string Label,
    string Value,
    string Kind,
    bool IsReadOnly,
    IReadOnlyList<MedulaRemoteOption> Options);

public sealed record MedulaTreatmentSchemeRow(
    string MedicationName,
    IReadOnlyList<MedulaTreatmentSchemeCell> Cells);

public sealed record MedulaEditableColumnGroup(
    string Label,
    IReadOnlyList<int> CellIndexes);

public static class MedulaEditableTableLayout
{
    public static IReadOnlyList<MedulaEditableColumnGroup> BuildColumnGroups(
        IReadOnlyList<MedulaTreatmentSchemeRow> rows)
    {
        if (rows.Count == 0) return [];
        var columnCount = rows.Max(row => row.Cells.Count);
        var result = new List<MedulaEditableColumnGroup>();
        for (var index = 0; index < columnCount; index++)
        {
            var rawLabel = rows
                .Select(row => row.Cells.ElementAtOrDefault(index)?.Label)
                .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))
                ?? $"Alan {index + 1}";
            var label = rawLabel.Trim().TrimEnd(':', '：').Trim();
            if (label.Equals("x", StringComparison.OrdinalIgnoreCase)
                && result.LastOrDefault() is { } previous
                && previous.Label.Equals("Doz", StringComparison.CurrentCultureIgnoreCase))
            {
                result[^1] = previous with
                {
                    CellIndexes = previous.CellIndexes.Concat([index]).ToArray()
                };
                continue;
            }

            result.Add(new MedulaEditableColumnGroup(label, [index]));
        }
        return result;
    }
}

public sealed record MedulaRemotePageAction(
    string Id,
    string Caption,
    int PageNumber,
    bool IsCurrent);

public sealed record MedulaRemoteTable(
    string Id,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<string>? RowActionIds = null,
    IReadOnlyList<MedulaRemotePageAction>? PageActions = null,
    string? Title = null,
    IReadOnlyList<string>? RowParentValues = null,
    IReadOnlyList<string>? RowBackgrounds = null,
    IReadOnlyList<bool>? RowSelections = null);

public sealed record MedulaRemoteScreen(
    bool IsOpen,
    string Key,
    string Title,
    string Url,
    string Message,
    IReadOnlyList<MedulaRemoteField> Fields,
    IReadOnlyList<MedulaRemoteAction> Actions,
    IReadOnlyList<MedulaRemoteTable> Tables,
    string? ConfirmationMessage = null,
    bool RemoteSuccessConfirmed = false,
    IReadOnlyList<MedulaTreatmentSchemeRow>? TreatmentRows = null);

public sealed record MedulaRemoteFieldPayload(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, bool> Checks);
