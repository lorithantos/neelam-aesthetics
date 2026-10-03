namespace Neelam.Campaigns.Storage;

/// <summary>
/// A client of this deployment: its name (also its container's), the Entra security group whose
/// members are its people, the name shown to them, and the operator's description of the business,
/// written at onboarding to guide the AI proofread. Which users are in the group is Entra's
/// business, never this table's.
/// </summary>
public sealed record ClientRecord(ClientName Name, Guid GroupId, string DisplayName, string? Description = null)
{
    /// <summary>What the proofread is told about who is sending the email.</summary>
    public BusinessContext Business => new(DisplayName, Description);
}

/// <summary>The clients table. The operator adds a client here when onboarding it.</summary>
public interface IClientDirectory
{
    Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a client; refuses a name or group that is already taken.</summary>
    Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default);
}

/// <summary>
/// Support grants. Each is a row kept after it expires, so the table is also the record of when a
/// client let the operator in and why.
/// </summary>
public interface ISupportGrantStore
{
    Task<IReadOnlyList<SupportGrant>> ForClientAsync(ClientName client, CancellationToken cancellationToken = default);

    Task RecordAsync(SupportGrant grant, CancellationToken cancellationToken = default);
}
