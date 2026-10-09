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
public sealed class TableMetadata : IClientDirectory, ISupportGrantStore, IApprovalStore, IActivityLog
{
    internal const string ClientsTable = "clients";
    internal const string GrantsTable = "supportGrants";
    internal const string ApprovalsTable = "approvals";
    internal const string ActivityTable = "activity";
    private const string ClientPartition = "client";

    private readonly TableClient _clients;
    private readonly TableClient _grants;
    private readonly TableClient _approvals;
    private readonly TableClient _activity;

    /// <param name="storage">The app's storage clients, built once at startup.</param>
    public TableMetadata(StorageClients storage)
    {
        _clients = storage.Tables.GetTableClient(ClientsTable);
        _grants = storage.Tables.GetTableClient(GrantsTable);
        _approvals = storage.Tables.GetTableClient(ApprovalsTable);
        _activity = storage.Tables.GetTableClient(ActivityTable);
    }

    public async Task<IReadOnlyList<ApprovalRecord>> ForCampaignAsync(
        ClientName client, Guid campaignId, CancellationToken cancellationToken = default)
    {
        var approvals = new List<ApprovalRecord>();
        await foreach (var row in _approvals.QueryAsync<TableEntity>(
                           ApprovalsFilter(client, campaignId), cancellationToken: cancellationToken))
            approvals.Add(ToApproval(row));
        return approvals;
    }

    public Task PutAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) =>
        // Replace, not merge, so approving again after a withdrawal clears the withdrawal.
        _approvals.UpsertEntityAsync(FromApproval(approval), TableUpdateMode.Replace, cancellationToken);

    public Task RecordAsync(ActivityEvent activity, CancellationToken cancellationToken = default) =>
        _activity.AddEntityAsync(FromActivity(activity, Guid.NewGuid()), cancellationToken);

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
        ClientDirectoryRules.CheckRegistration(client);
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
        // Each number as its digits with the country code, comma-separated; left out when none.
        if (client.Phones.Count > 0) row["Phones"] = string.Join(",", client.Phones.Select(p => p.Digits));
        // The zone's IANA id; left out for the default.
        if (!string.IsNullOrWhiteSpace(client.TimeZone)) row["TimeZone"] = client.TimeZone;
        return row;
    }

    // A row written before phone numbers or time zones were registered has none. A zone is read as
    // written, known or not: the pages fall back to the default for one they cannot use.
    internal static ClientRecord ToClient(TableEntity row) =>
        new(new ClientName(row.RowKey),
            row.GetGuid("GroupId") ?? throw new InvalidDataException($"Client {row.RowKey} has no Entra group."),
            row.GetString("DisplayName") ?? row.RowKey,
            row.GetString("Description"))
        {
            Phones = row.GetString("Phones") is { Length: > 0 } phones
                ? new PhoneNumbers(phones.Split(',').Select(digits => PhoneNumber.FromDigits(digits)
                    ?? throw new InvalidDataException($"Client {row.RowKey} has a phone number that is not one: '{digits}'.")))
                : PhoneNumbers.None,
            TimeZone = row.GetString("TimeZone") is { Length: > 0 } zone ? zone : null,
        };

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

    // The client's own partition, and only the rows of this one campaign: their keys start with its id.
    internal static string ApprovalsFilter(ClientName client, Guid campaignId) =>
        TableClient.CreateQueryFilter(
            $"PartitionKey eq {client.Value} and RowKey ge {$"{campaignId:N}_"} and RowKey lt {$"{campaignId:N}`"}");

    // One partition per client; the row is one save of one campaign, {campaign id}_{save stamp}.
    internal static TableEntity FromApproval(ApprovalRecord approval)
    {
        var row = new TableEntity(approval.Client.Value, $"{approval.CampaignId:N}_{approval.Stamp}")
        {
            ["ApprovedBy"] = approval.ApprovedBy,
            ["ApprovedAt"] = approval.ApprovedAt,
            ["Withdrawn"] = approval.Withdrawn,
        };
        if (approval.WithdrawnAt is { } withdrawnAt) row["WithdrawnAt"] = withdrawnAt;
        // Who went on past the warnings at export, and when: left out until someone has.
        if (approval.WarningsSeenBy is { } seenBy) row["WarningsSeenBy"] = seenBy;
        if (approval.WarningsSeenAt is { } seenAt) row["WarningsSeenAt"] = seenAt;
        return row;
    }

    internal static ApprovalRecord ToApproval(TableEntity row)
    {
        var key = row.RowKey.Split('_', 2);
        if (key.Length != 2 || !Guid.TryParseExact(key[0], "N", out var campaignId))
            throw new InvalidDataException($"An approval row has a key that names no campaign save: '{row.RowKey}'.");
        var withdrawnAt = row.GetDateTimeOffset("WithdrawnAt");
        // Withdrawn with no time still counts as withdrawn: it fails closed.
        if (withdrawnAt is null && row.GetBoolean("Withdrawn") == true)
            withdrawnAt = row.GetDateTimeOffset("ApprovedAt") ?? DateTimeOffset.MinValue;
        return new ApprovalRecord(
            new ClientName(row.PartitionKey), campaignId, key[1],
            row.GetString("ApprovedBy") ?? throw new InvalidDataException("An approval has no approver."),
            row.GetDateTimeOffset("ApprovedAt") ?? throw new InvalidDataException("An approval has no time."),
            withdrawnAt,
            row.GetString("WarningsSeenBy"),
            row.GetDateTimeOffset("WarningsSeenAt"));
    }

    // One partition per client; rows sort by time, and the id keeps two events in the same instant
    // apart. Ids, names of kinds and actions, and times only: never content.
    internal static TableEntity FromActivity(ActivityEvent activity, Guid id)
    {
        var row = new TableEntity(activity.Client.Value,
            $"{activity.At.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'.'fffffff'Z'", CultureInfo.InvariantCulture)}-{id:N}")
        {
            ["Entity"] = activity.Entity.ToString(),
            ["EntityId"] = activity.EntityId,
            ["Action"] = activity.Action.ToString(),
            ["Actor"] = activity.Actor,
            ["At"] = activity.At,
        };
        if (activity.SaveStamp is { } stamp) row["SaveStamp"] = stamp;
        return row;
    }

    internal static ActivityEvent ToActivity(TableEntity row) =>
        new(new ClientName(row.PartitionKey),
            Enum.Parse<ActivityEntity>(row.GetString("Entity") ?? throw new InvalidDataException("An event has no entity.")),
            row.GetString("EntityId") ?? "",
            row.GetString("SaveStamp"),
            Enum.Parse<ActivityAction>(row.GetString("Action") ?? throw new InvalidDataException("An event has no action.")),
            row.GetString("Actor") ?? "",
            row.GetDateTimeOffset("At") ?? throw new InvalidDataException("An event has no time."));
}
