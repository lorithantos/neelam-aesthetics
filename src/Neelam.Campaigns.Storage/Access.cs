namespace Neelam.Campaigns.Storage;

/// <summary>What a request wants to reach.</summary>
public enum Area
{
    /// <summary>A client's own container: drafts, templates, catalog and check policy.</summary>
    ClientData,

    /// <summary>A client's look, at <c>settings/{client}</c>.</summary>
    Look,

    /// <summary>The clients and members tables. Belongs to no one client.</summary>
    Administration,
}

/// <summary>
/// Who is asking. <paramref name="UserId"/> is the Entra object ID from sign-in;
/// <paramref name="MemberOf"/> comes from the members table; <paramref name="IsOperator"/> says
/// this is the person who runs the deployment.
/// </summary>
public sealed record Caller(string UserId, bool IsOperator, IReadOnlyList<ClientName> MemberOf)
{
    public bool IsMemberOf(ClientName client) => MemberOf.Contains(client);
}

/// <summary>
/// A client's permission for the operator to read its own data, given when it asks for help.
/// Only a member of the client can give one. <paramref name="Expires"/> is null only for a
/// standing grant, which exists for Neelam until 1.0.
/// </summary>
public sealed record SupportGrant(
    ClientName Client, string GivenBy, string Reason, DateTimeOffset GivenAt, DateTimeOffset? Expires)
{
    public static SupportGrant Give(ClientName client, Caller giver, string reason, TimeSpan lasting, DateTimeOffset now)
    {
        if (lasting <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lasting), "A support grant has to last some time.");
        return Create(client, giver, reason, now, now + lasting);
    }

    /// <summary>
    /// A grant with no expiry, for a client the tool is being shaped with: Neelam, until 1.0.
    /// Ending it is deleting it.
    /// </summary>
    public static SupportGrant Standing(ClientName client, Caller giver, string reason, DateTimeOffset now) =>
        Create(client, giver, reason, now, expires: null);

    public bool IsActive(DateTimeOffset now) => GivenAt <= now && (Expires is null || now < Expires);

    private static SupportGrant Create(
        ClientName client, Caller giver, string reason, DateTimeOffset now, DateTimeOffset? expires)
    {
        // The point of a grant is that the client chose it, so the operator cannot give one to
        // themselves, and nobody can give one for a client they do not belong to.
        if (!giver.IsMemberOf(client))
            throw new InvalidOperationException($"Only a member of {client} can give support access to its data.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A support grant needs a reason.", nameof(reason));
        return new SupportGrant(client, giver.UserId, reason.Trim(), now, expires);
    }
}

public sealed record AccessDecision(bool Allowed, string Reason)
{
    public static AccessDecision Allow(string reason) => new(true, reason);
    public static AccessDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides what a request may reach. One deployment serves every client and the app's identity
/// can reach every container, so this is what keeps clients apart. Pure: everything it needs is
/// passed in, so every rule is tested without storage, sign-in or a clock.
/// <list type="bullet">
/// <item>A member of a client reaches that client's data and look, and nothing of any other client's.</item>
/// <item>The operator manages clients and members and reaches every client's look, but reaches a
/// client's own data only under an active support grant from that client.</item>
/// <item>Anyone else reaches nothing.</item>
/// </list>
/// </summary>
public static class AccessCheck
{
    /// <param name="client">The client the request is working in; null only for <see cref="Area.Administration"/>.</param>
    /// <param name="grants">Support grants on record for that client.</param>
    public static AccessDecision Decide(
        Caller caller, Area area, ClientName? client, IEnumerable<SupportGrant> grants, DateTimeOffset now)
    {
        if (area == Area.Administration)
        {
            if (client is not null)
                throw new ArgumentException("Administration belongs to no one client.", nameof(client));
            return caller.IsOperator
                ? AccessDecision.Allow("the operator manages clients and members")
                : AccessDecision.Deny("only the operator manages clients and members");
        }

        if (client is null)
            throw new ArgumentNullException(nameof(client), $"{area} belongs to a client; say which.");

        if (caller.IsMemberOf(client))
            return AccessDecision.Allow($"a member of {client}");

        if (!caller.IsOperator)
            return AccessDecision.Deny($"not a member of {client}");

        if (area == Area.Look)
            return AccessDecision.Allow($"the operator works on {client}'s look");

        var grant = grants.FirstOrDefault(g => g.Client == client && g.IsActive(now));
        return grant is null
            ? AccessDecision.Deny($"{client} has not given support access")
            : AccessDecision.Allow(grant.Expires is null
                ? $"standing support access from {client}: {grant.Reason}"
                : $"support access from {client} until {grant.Expires:u}: {grant.Reason}");
    }
}
