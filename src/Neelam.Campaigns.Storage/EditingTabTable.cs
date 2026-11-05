using Azure;
using Azure.Data.Tables;
using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// One open editor tab, as another instance of the site sees it: which tab, when it last typed (null when
/// it has not), and the table's own <c>Timestamp</c> for the row, the time the tab last reported in.
/// Ids and times only, never content.
/// </summary>
public sealed record TabRow(Guid TabId, DateTimeOffset? LastTyped, DateTimeOffset Timestamp);

/// <summary>
/// The tabs being edited, shared between instances of the site so a tab sees the others (owner,
/// 2026-11-05: an "also being edited in another tab" notice). Each tab writes only its own row, so
/// every write is unconditional. Every call but <see cref="SweepAsync"/> names the client and reads and
/// writes only that client's own rows.
/// </summary>
public interface ITabRows
{
    /// <summary>Puts the tab's row, replacing what was there: it is only ever written by that tab.</summary>
    Task WriteAsync(ClientName client, TabSubject subject, Guid tabId, DateTimeOffset? lastTyped,
        CancellationToken cancellationToken = default);

    /// <summary>The rows of the tabs editing <paramref name="subject"/> for <paramref name="client"/>.</summary>
    Task<IReadOnlyList<TabRow>> ListAsync(ClientName client, TabSubject subject,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the tab's row; a row already gone is not an error.</summary>
    Task DeleteAsync(ClientName client, TabSubject subject, Guid tabId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every row, whichever client's, whose <c>Timestamp</c> is older than
    /// <paramref name="olderThan"/>: the rows of tabs whose instance went away without closing them.
    /// Returns how many it removed.
    /// </summary>
    Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

/// <summary>
/// The editing tabs table in Azure Table Storage, reached through the app's <see cref="StorageClients"/>
/// like the other metadata tables (<see cref="TableMetadata"/>): an Entra ID token only. PartitionKey is
/// <c>{client}|{kind}</c> for the look and <c>{client}|{kind}|{id:N}</c> for a campaign or template, so one
/// query names one subject of one client; RowKey is the tab's id (<c>N</c> form). The one property is
/// <c>LastTyped</c>, absent until the tab has typed. The mapping to and from rows is separate and static,
/// so it is tested without a network.
/// </summary>
public sealed class EditingTabTable(StorageClients storage) : ITabRows
{
    internal const string Table = "editingTabs";

    private readonly TableClient _table = storage.Tables.GetTableClient(Table);

    public Task WriteAsync(ClientName client, TabSubject subject, Guid tabId, DateTimeOffset? lastTyped,
        CancellationToken cancellationToken = default) =>
        // Replace, not merge, so a LastTyped that is no longer known does not linger.
        _table.UpsertEntityAsync(FromTab(client, subject, tabId, lastTyped), TableUpdateMode.Replace, cancellationToken);

    public async Task<IReadOnlyList<TabRow>> ListAsync(ClientName client, TabSubject subject,
        CancellationToken cancellationToken = default)
    {
        var tabs = new List<TabRow>();
        await foreach (var row in _table.QueryAsync<TableEntity>(PartitionFilter(client, subject), cancellationToken: cancellationToken))
            tabs.Add(ToTab(client, subject, row));
        return tabs;
    }

    public async Task DeleteAsync(ClientName client, TabSubject subject, Guid tabId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _table.DeleteEntityAsync(PartitionKey(client, subject), RowKeyOf(tabId), ETag.All, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone: swept, or deleted by an earlier close.
        }
    }

    public async Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        // The table holds a few rows at most, so one filtered query over every partition is fine.
        await foreach (var row in _table.QueryAsync<TableEntity>(OlderThanFilter(olderThan), cancellationToken: cancellationToken))
        {
            try
            {
                // By the row's own ETag: a tab that reported in since the query keeps its row.
                await _table.DeleteEntityAsync(row.PartitionKey, row.RowKey, row.ETag, cancellationToken);
                removed++;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
                // Gone already, or written again since: not ours to remove.
            }
        }
        return removed;
    }

    /// <summary>The query for one subject of one client, and nothing else.</summary>
    internal static string PartitionFilter(ClientName client, TabSubject subject) =>
        TableClient.CreateQueryFilter($"PartitionKey eq {PartitionKey(client, subject)}");

    /// <summary>The query for rows the service last wrote before <paramref name="olderThan"/>.</summary>
    internal static string OlderThanFilter(DateTimeOffset olderThan) =>
        TableClient.CreateQueryFilter($"Timestamp lt {olderThan}");

    /// <summary>
    /// <c>{client}|{kind}</c> or <c>{client}|{kind}|{id:N}</c>. A client name is lowercase letters, digits and
    /// hyphens, a kind is a fixed word and an id is hex digits, so no key can hold <c>/ \ # ?</c> or a control character.
    /// </summary>
    internal static string PartitionKey(ClientName client, TabSubject subject) =>
        subject.Id is { } id ? $"{client.Value}|{subject.Kind}|{id:N}" : $"{client.Value}|{subject.Kind}";

    internal static string RowKeyOf(Guid tabId) => tabId.ToString("N");

    internal static TableEntity FromTab(ClientName client, TabSubject subject, Guid tabId, DateTimeOffset? lastTyped)
    {
        var row = new TableEntity(PartitionKey(client, subject), RowKeyOf(tabId));
        if (lastTyped is { } typed) row["LastTyped"] = typed;
        return row;
    }

    /// <summary>
    /// A tab from its row, which must be in the partition of <paramref name="client"/> and
    /// <paramref name="subject"/>: a row from any other is refused rather than shown, whatever the query returned.
    /// </summary>
    internal static TabRow ToTab(ClientName client, TabSubject subject, TableEntity row)
    {
        if (row.PartitionKey != PartitionKey(client, subject))
            throw new InvalidDataException($"A tab of another client or subject was read for {client}.");
        return new TabRow(
            Guid.ParseExact(row.RowKey, "N"),
            row.GetDateTimeOffset("LastTyped"),
            row.Timestamp ?? throw new InvalidDataException($"Tab row {row.RowKey} has no timestamp."));
    }
}
