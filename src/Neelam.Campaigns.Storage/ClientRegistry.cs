namespace Neelam.Campaigns.Storage;

/// <summary>
/// Registering a client and changing its registration, as the operator does on the Clients page:
/// the clients table, with each change on that client's activity trail. The event names the client
/// and says what happened; it never holds the description, display name or phone numbers.
/// </summary>
public sealed class ClientRegistry(IClientDirectory clients, ActivityRecorder activity)
{
    /// <summary>Adds a client; refuses a name or group that is already taken, and then records nothing.</summary>
    public async Task AddAsync(ClientRecord client, Actor actor, CancellationToken ct = default)
    {
        await clients.AddAsync(client, ct);
        await activity.For(client.Name, actor).RecordAsync(
            ActivityEntity.ClientRegistration, client.Name.Value, null, ActivityAction.ClientRegistered, ct);
    }

    /// <summary>Changes a client's description; refuses a change to who it is, and then records nothing.</summary>
    public async Task UpdateAsync(ClientRecord client, Actor actor, CancellationToken ct = default)
    {
        await clients.UpdateAsync(client, ct);
        await activity.For(client.Name, actor).RecordAsync(
            ActivityEntity.ClientRegistration, client.Name.Value, null, ActivityAction.ClientChanged, ct);
    }
}
