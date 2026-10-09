using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Each client's known items (owner, 2026-10-09). Every call names the client, and reads and writes
/// only that client's own items: a page reaches it for the client the access check allowed
/// (<c>ClientWorkspace</c>), never for one a request names.
/// </summary>
public interface IKnownItemStore
{
    Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default);

    /// <summary>Adds an item; refuses one <see cref="KnownItemRules"/> refuses, or an id already in use.</summary>
    Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default);

    /// <summary>Replaces the item with the same id, of the same kind; refuses an unknown id.</summary>
    Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default);

    /// <summary>Removes the item for good. Campaigns that used it keep their text.</summary>
    Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default);
}

/// <summary>The store's checks, the same for every implementation of it.</summary>
public static class KnownItemStoreRules
{
    public static void CheckAdd(KnownItems existing, KnownItem item)
    {
        if (existing.Find(item.Id) is not null)
            throw new InvalidOperationException("That known item already exists.");
        Refuse(KnownItemRules.Problems(item, existing));
    }

    public static void CheckUpdate(KnownItems existing, KnownItem item)
    {
        var current = existing.Find(item.Id) ?? throw new InvalidOperationException("That known item no longer exists.");
        if (current.Kind != item.Kind)
            throw new InvalidOperationException("A known item keeps its kind; remove it and add the other kind instead.");
        Refuse(KnownItemRules.Problems(item, existing));
    }

    private static void Refuse(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
    }
}

/// <summary>
/// The known items table in Azure Table Storage, reached through the app's <see cref="StorageClients"/>
/// like the other metadata tables (<see cref="TableMetadata"/>): an Entra ID token only. One table,
/// one partition per client: PartitionKey is the client's name, RowKey the item's id. The mapping to
/// and from rows is separate and static, so it is tested without a network.
/// </summary>
public sealed class KnownItemTable(StorageClients storage) : IKnownItemStore
{
    internal const string Table = "knownItems";

    private readonly TableClient _table = storage.Tables.GetTableClient(Table);

    public async Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default)
    {
        var items = new List<KnownItem>();
        await foreach (var row in _table.QueryAsync<TableEntity>(PartitionFilter(client), cancellationToken: cancellationToken))
            items.Add(ToItem(client, row));
        return new KnownItems(items);
    }

    public async Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default)
    {
        KnownItemStoreRules.CheckAdd(await ForClientAsync(client, cancellationToken), item);
        try
        {
            await _table.AddEntityAsync(FromItem(client, item), cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new InvalidOperationException("That known item already exists.", ex);
        }
    }

    public async Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default)
    {
        KnownItemStoreRules.CheckUpdate(await ForClientAsync(client, cancellationToken), item);
        // Replace, not merge, so a tier's lines are exactly the new ones.
        await _table.UpdateEntityAsync(FromItem(client, item), ETag.All, TableUpdateMode.Replace, cancellationToken);
    }

    public Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("A known item's id is 32 hex digits.", nameof(id));
        return _table.DeleteEntityAsync(client.Value, id, ETag.All, cancellationToken);
    }

    /// <summary>The query for one client's partition, and nothing else.</summary>
    internal static string PartitionFilter(ClientName client) =>
        TableClient.CreateQueryFilter($"PartitionKey eq {client.Value}");

    internal static TableEntity FromItem(ClientName client, KnownItem item)
    {
        var row = new TableEntity(client.Value, item.Id)
        {
            ["Kind"] = item.Kind.ToString(),
            ["Text"] = item.Text,
        };
        switch (item)
        {
            case KnownBenefit b:
                row["Benefit"] = CampaignJson.SerializeBenefits([b.Benefit]);
                break;
            case KnownTier t:
                // Text, not a double: a price is exact.
                row["Price"] = t.Price.ToString(CultureInfo.InvariantCulture);
                row["Benefits"] = CampaignJson.SerializeBenefits(t.Benefits);
                break;
        }
        return row;
    }

    /// <summary>
    /// An item from its row, which must be in <paramref name="client"/>'s partition: a row from any
    /// other is refused rather than shown, whatever the query returned.
    /// </summary>
    internal static KnownItem ToItem(ClientName client, TableEntity row)
    {
        if (row.PartitionKey != client.Value)
            throw new InvalidDataException($"A known item of another client was read for {client}.");
        var text = row.GetString("Text") ?? throw new InvalidDataException($"Known item {row.RowKey} has no text.");
        return row.GetString("Kind") switch
        {
            nameof(KnownItemKind.Treatment) => new KnownTreatment(row.RowKey, text),
            nameof(KnownItemKind.Benefit) => new KnownBenefit(row.RowKey,
                CampaignJson.DeserializeBenefits(Required(row, "Benefit")).Single()),
            nameof(KnownItemKind.Tier) => new KnownTier(row.RowKey, text,
                decimal.Parse(Required(row, "Price"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                CampaignJson.DeserializeBenefits(Required(row, "Benefits"))),
            var kind => throw new InvalidDataException($"Known item {row.RowKey} is of no known kind ('{kind}')."),
        };
    }

    private static string Required(TableEntity row, string property) =>
        row.GetString(property) ?? throw new InvalidDataException($"Known item {row.RowKey} has no {property}.");
}
