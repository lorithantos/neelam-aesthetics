using Neelam.Campaigns.Storage;

namespace Neelam.Web.Security;

/// <summary>
/// Which client's data a page works in, decided by the access check. For now that is the one
/// client the caller belongs to (the prototype's fixed caller belongs to exactly one). Choosing
/// between several, and the operator working in a client under its support grant, come later.
/// </summary>
public sealed class ClientWorkspace(ICallerSource callers, ISupportGrantStore grants, TimeProvider clock)
{
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
