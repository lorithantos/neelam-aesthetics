using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Web.Security;

/// <summary>
/// Which client's data a page works in, decided by the access check. For now that is the one
/// client the caller belongs to (the prototype's fixed caller belongs to exactly one). Choosing
/// between several, and the operator working in a client under its support grant, come later.
/// </summary>
public sealed class ClientWorkspace(
    ICallerSource callers, ISupportGrantStore grants, IClientDirectory clients, TimeProvider clock)
{
    /// <summary>
    /// The name a client's people know it by, from the clients table: its display name, or its
    /// name when the table has no row for it or the row has no display name.
    /// </summary>
    public async Task<string> DisplayNameAsync(ClientName client, CancellationToken ct = default)
    {
        var record = (await clients.ListAsync(ct)).FirstOrDefault(c => c.Name == client);
        return string.IsNullOrWhiteSpace(record?.DisplayName) ? client.ToString() : record.DisplayName;
    }

    /// <summary>
    /// The client's registration as the checks take it: its name, description and the phone numbers
    /// it may publish. Null when the clients table has no row for it, so nothing is checked against one.
    /// </summary>
    public async Task<BusinessContext?> BusinessAsync(ClientName client, CancellationToken ct = default) =>
        (await clients.ListAsync(ct)).FirstOrDefault(c => c.Name == client)?.Business;

    /// <returns>The client, or null with the reason the page cannot show one.</returns>
    public async Task<(ClientName? Client, string Reason)> ClientDataAsync(CancellationToken ct = default)
    {
        var (caller, reason) = await callers.CurrentAsync(ct);
        if (caller is null) return (null, reason);
        if (caller.MemberOf.Count == 0) return (null, "you are not a member of any client");
        if (caller.MemberOf.Count > 1)
            return (null, "you belong to more than one client, and choosing between them is not built yet");

        var client = caller.MemberOf[0];
        // A member needs no grant, so grants are read only for a caller who is not one.
        IReadOnlyList<SupportGrant> onRecord = caller.IsMemberOf(client) ? [] : await grants.ForClientAsync(client, ct);
        var decision = AccessCheck.Decide(caller, Area.ClientData, client, onRecord, clock.GetUtcNow());
        return decision.Allowed ? (client, decision.Reason) : (null, decision.Reason);
    }
}
