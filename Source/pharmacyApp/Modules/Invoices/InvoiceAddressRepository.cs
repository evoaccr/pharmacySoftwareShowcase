using Npgsql;
using NpgsqlTypes;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Invoices;

public sealed record InvoiceAddressRow(
    int RecordId,
    string PartyName,
    string IdentityNo,
    string Gln,
    int SentInvoiceCount,
    int ReceivedInvoiceCount,
    int TotalInvoiceCount);

public sealed class InvoiceAddressRepository
{
    public async Task<IReadOnlyList<InvoiceAddressRow>> LoadAsync(
        string? search,
        string identityFilter,
        CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionSettingsStore.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            WITH sent AS (
                SELECT sender_address_id address_id, count(*)::integer invoice_count
                FROM public.invoices GROUP BY sender_address_id
            ), received AS (
                SELECT receiver_address_id address_id, count(*)::integer invoice_count
                FROM public.invoices GROUP BY receiver_address_id
            )
            SELECT address.record_id, address.party_name, address.identity_no, address.gln,
                   coalesce(sent.invoice_count, 0), coalesce(received.invoice_count, 0),
                   coalesce(sent.invoice_count, 0) + coalesce(received.invoice_count, 0)
            FROM public.addresses address
            LEFT JOIN sent ON sent.address_id = address.record_id
            LEFT JOIN received ON received.address_id = address.record_id
            WHERE (@search IS NULL
                   OR address.party_name ILIKE @search
                   OR address.identity_no ILIKE @search
                   OR address.gln ILIKE @search)
              AND (@identity_filter = 'all'
                   OR (@identity_filter = 'identity' AND address.identity_no <> '')
                   OR (@identity_filter = 'gln' AND address.gln <> '')
                   OR (@identity_filter = 'both' AND address.identity_no <> '' AND address.gln <> '')
                   OR (@identity_filter = 'missing' AND address.identity_no = '' AND address.gln = ''))
            ORDER BY address.party_name, address.record_id;
            """;
        await using var command = pharmacyApp.Infrastructure.DatabaseCommand.Create(sql, connection);
        AddNullable(command, "search", NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%");
        command.Parameters.AddWithValue("identity_filter", identityFilter);
        var rows = new List<InvoiceAddressRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new InvoiceAddressRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        return rows;
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
