namespace pharmacyApp.Modules.Medula;

public sealed class MedulaRemoteScreenPager(MedulaAutomation automation)
{
    public async Task<MedulaRemoteScreen> LoadAllPagesAsync(MedulaRemoteScreen screen, CancellationToken cancellationToken)
    {
        var firstTable = screen.Tables.FirstOrDefault();
        if (firstTable is null) return screen;
        var rows = new List<IReadOnlyList<string>>();
        var rowKeys = new HashSet<string>(StringComparer.Ordinal);
        var loadedPages = new HashSet<int> { 1 };
        var current = screen;
        MedulaRemoteTable currentTable = firstTable;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            foreach (var row in currentTable.Rows)
            {
                var key = string.Join('\u001f', row);
                if (rowKeys.Add(key)) rows.Add(row);
            }
            var next = (currentTable.PageActions ?? [])
                .Where(action => action.PageNumber > 0 && !action.IsCurrent && !loadedPages.Contains(action.PageNumber))
                .OrderBy(action => action.PageNumber).FirstOrDefault()
                ?? (currentTable.PageActions ?? []).FirstOrDefault(action =>
                    action.Caption.Equals("İleri", StringComparison.CurrentCultureIgnoreCase) || action.Caption is ">" or ">>");
            if (next is null) break;
            if (next.PageNumber > 0) loadedPages.Add(next.PageNumber);
            var nextScreen = await automation.ExecuteTablePageActionAsync(
                current.Key, current.Title, currentTable.Id, next.Id, cancellationToken);
            var nextTable = nextScreen.Tables.FirstOrDefault(table => table.Id == currentTable.Id)
                ?? nextScreen.Tables.FirstOrDefault();
            if (!nextScreen.IsOpen || nextTable is null) break;
            if (next.PageNumber == 0 && nextTable.Rows.Count > 0
                && nextTable.Rows.All(row => rowKeys.Contains(string.Join('\u001f', row)))) break;
            current = nextScreen;
            currentTable = nextTable;
        }
        return current with
        {
            Tables =
            [
                firstTable with { Rows = rows, RowActionIds = null, PageActions = null },
                .. screen.Tables.Skip(1)
            ]
        };
    }
}
