namespace Neelam.Campaigns.Storage;

/// <summary>
/// A client of this deployment: its name (also its container's), the Entra security group whose
/// members are its people, the name shown to them, and the operator's description of the business,
/// written at onboarding to guide the AI proofread. Which users are in the group is Entra's
/// business, never this table's.
/// </summary>
public sealed record ClientRecord(ClientName Name, Guid GroupId, string DisplayName, string? Description = null)
{
    /// <summary>
    /// The phone numbers the business may publish, such as a main line and a booking line. The
    /// checks flag any other number in a campaign; with none registered, numbers are not checked.
    /// </summary>
    public PhoneNumbers Phones { get; init; } = PhoneNumbers.None;

    /// <summary>What the checks and the proofread are told about who is sending the email.</summary>
    public BusinessContext Business => new(DisplayName, Description) { Phones = Phones };
}

/// <summary>The clients table. The operator adds a client here when onboarding it.</summary>
public interface IClientDirectory
{
    Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a client; refuses a name or group that is already taken.</summary>
    Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a client's display name, description and phone numbers. Its name and Entra group are who it is,
    /// so an update naming an unknown client, or a different group, is refused.
    /// </summary>
    Task UpdateAsync(ClientRecord client, CancellationToken cancellationToken = default);
}

/// <summary>The clients table's rules, the same for every implementation of it.</summary>
public static class ClientDirectoryRules
{
    /// <summary>Refuses an update that would change who a client is rather than how it is described.</summary>
    public static void CheckUpdate(IEnumerable<ClientRecord> clients, ClientRecord updated)
    {
        var existing = clients.FirstOrDefault(c => c.Name == updated.Name)
                       ?? throw new InvalidOperationException($"{updated.Name} is not a client.");
        if (existing.GroupId != updated.GroupId)
            throw new InvalidOperationException(
                $"{updated.Name}'s Entra group is part of who it is, so it is not changed by an update.");
        if (string.IsNullOrWhiteSpace(updated.DisplayName))
            throw new ArgumentException("A client needs a display name.", nameof(updated));
    }
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
