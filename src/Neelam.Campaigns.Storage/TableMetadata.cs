using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// The metadata tables in Azure Table Storage, reached through the app's
/// <see cref="StorageClients"/> like the blobs: an Entra ID token only, with the app's retry
/// budget. The mapping to and from table rows is separate and static, so it is tested without a
/// network.
/// </summary>
public sealed class TableMetadata : IClientDirectory, ISupportGrantStore
{
    internal const string ClientsTable = "clients";
    internal const string GrantsTable = "supportGrants";
    private const string ClientPartition = "client";

    private readonly TableClient _clients;
    private readonly TableClient _grants;

    /// <param name="storage">The app's storage clients, built once at startup.</param>
    public TableMetadata(StorageClients storage)
    {
        _clients = storage.Tables.GetTableClient(ClientsTable);
        _grants = storage.Tables.GetTableClient(GrantsTable);
    }

    public async Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var clients = new List<ClientRecord>();
        await foreach (var row in _clients.QueryAsync<TableEntity>(
                           e => e.PartitionKey == ClientPartition, cancellationToken: cancellationToken))
            clients.Add(ToClient(row));
        return clients;
    }

    public async Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default)
    {
        if ((await ListAsync(cancellationToken)).Any(c => c.GroupId == client.GroupId))
            throw new InvalidOperationException($"Entra group {client.GroupId} already belongs to another client.");
        try
        {
            await _clients.AddEntityAsync(FromClient(client), cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new InvalidOperationException($"{client.Name} is already a client.", ex);
        }
    }

    public async Task UpdateAsync(ClientRecord client, CancellationToken cancellationToken = default)
    {
        ClientDirectoryRules.CheckUpdate(await ListAsync(cancellationToken), client);
        // Replace, not merge, so clearing the description removes it rather than keeping the old one.
        await _clients.UpdateEntityAsync(FromClient(client), ETag.All, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task<IReadOnlyList<SupportGrant>> ForClientAsync(ClientName client, CancellationToken cancellationToken = default)
    {
        var grants = new List<SupportGrant>();
        await foreach (var row in _grants.QueryAsync<TableEntity>(
                           e => e.PartitionKey == client.Value, cancellationToken: cancellationToken))
            grants.Add(ToGrant(row));
        return grants;
    }

    public Task RecordAsync(SupportGrant grant, CancellationToken cancellationToken = default) =>
        _grants.AddEntityAsync(FromGrant(grant, Guid.NewGuid()), cancellationToken);

    internal static TableEntity FromClient(ClientRecord client)
    {
        var row = new TableEntity(ClientPartition, client.Name.Value)
        {
            ["GroupId"] = client.GroupId,
            ["DisplayName"] = client.DisplayName,
        };
        // Left out rather than stored as null, like a standing grant's expiry.
        if (client.Description is { } description) row["Description"] = description;
        return row;
    }

    internal static ClientRecord ToClient(TableEntity row) =>
        new(new ClientName(row.RowKey),
            row.GetGuid("GroupId") ?? throw new InvalidDataException($"Client {row.RowKey} has no Entra group."),
            row.GetString("DisplayName") ?? row.RowKey,
            row.GetString("Description"));

    // One partition per client; rows sort by when the grant was given, and the id keeps two grants
    // in the same instant apart.
    internal static TableEntity FromGrant(SupportGrant grant, Guid id)
    {
        var row = new TableEntity(grant.Client.Value,
            $"{grant.GivenAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'.'fffffff'Z'", CultureInfo.InvariantCulture)}-{id:N}")
        {
            ["GivenBy"] = grant.GivenBy,
            ["Reason"] = grant.Reason,
            ["GivenAt"] = grant.GivenAt,
        };
        // A standing grant has no expiry; the property is left out rather than stored as null.
        if (grant.Expires is { } expires) row["Expires"] = expires;
        return row;
    }

    internal static SupportGrant ToGrant(TableEntity row) =>
        new(new ClientName(row.PartitionKey),
            row.GetString("GivenBy") ?? throw new InvalidDataException("A support grant has no giver."),
            row.GetString("Reason") ?? "",
            row.GetDateTimeOffset("GivenAt") ?? throw new InvalidDataException("A support grant has no time."),
            row.GetDateTimeOffset("Expires"));
}
