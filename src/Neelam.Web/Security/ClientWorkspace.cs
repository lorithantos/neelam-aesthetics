using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Web.Security;

/// <summary>
/// Which client's data a page works in, decided by the access check. For now that is the one
/// client the caller belongs to (the prototype's fixed caller belongs to exactly one). Choosing
/// between several, and the operator working in a client under its support grant, come later.
/// </summary>
public sealed class ClientWorkspace(
    ICallerSource callers, ISupportGrantStore grants, IClientDirectory clients, IKnownItemStore knownItems, TimeProvider clock,
    DefaultTimeZone defaultZone, ILogger<ClientWorkspace> log)
{
    /// <summary>
    /// Times in the deployment's default zone, for what a page shows before it knows its client.
    /// </summary>
    public LocalTime DefaultTimes => defaultZone.Times;

    /// <summary>
    /// The client's times as its people read them: in the zone its row in the clients table names, else
    /// the deployment's default, Pacific (owner, 2026-10-09). One read of the table, once per page, as for
    /// the display name. A stored zone this machine does not know, or a row that cannot be read, is logged
    /// by the client's name and gives the default: the time shown never stops a page.
    /// </summary>
    public async Task<LocalTime> TimesAsync(ClientName client, CancellationToken ct = default)
    {
        string? zone;
        try
        {
            zone = (await clients.ListAsync(ct)).FirstOrDefault(c => c.Name == client)?.TimeZone;
        }
        catch (Exception ex) when (IsMalformedRow(ex))
        {
            log.LogError(ex, "The clients table has a row that cannot be read, so {Client}'s times are shown in the default zone.", client.Value);
            return defaultZone.Times;
        }
        if (string.IsNullOrWhiteSpace(zone)) return defaultZone.Times;
        if (LocalTime.TryFor(zone) is { } times) return times;
        log.LogWarning("The time zone registered for {Client} is not one this machine knows, so its times are shown in the default zone.", client.Value);
        return defaultZone.Times;
    }

    /// <summary>
    /// The client's known items, to add, change and pick from. Only for a client
    /// <see cref="ClientDataAsync"/> gave, as for every other store of client data.
    /// </summary>
    public IKnownItemStore Known => knownItems;

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
    /// it may publish, with its known items. Null when the clients table has no row for it and it has
    /// no known items, so nothing is checked against either; with items and no row, the business is
    /// named by its client name. Known items that cannot be read (the table not deployed yet, or a row
    /// the store cannot make sense of) are logged and taken as none: they help, but a campaign page must
    /// not fail for want of them; the store itself leaves out a single bad row and keeps the rest.
    /// </summary>
    /// <exception cref="RegistrationUnreadableException">
    /// The clients table holds a row that cannot be read. The registration decides which phone numbers
    /// the email may carry, so the checks do not go on as though there were none; the page says so.
    /// </exception>
    public async Task<BusinessContext?> BusinessAsync(ClientName client, CancellationToken ct = default)
    {
        BusinessContext? registered;
        try
        {
            registered = (await clients.ListAsync(ct)).FirstOrDefault(c => c.Name == client)?.Business;
        }
        catch (Exception ex) when (IsMalformedRow(ex))
        {
            log.LogError(ex, "The clients table has a row that cannot be read, so the registration of {Client} is unknown.", client.Value);
            throw new RegistrationUnreadableException(ex);
        }
        KnownItems known;
        try
        {
            known = await knownItems.ForClientAsync(client, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not read the known items of {Client}; the checks go on without them.", client.Value);
            known = KnownItems.None;
        }
        if (known.IsEmpty) return registered;
        return (registered ?? new BusinessContext(client.ToString())) with { Known = known };
    }

    // What reading a row the code cannot make sense of throws: a missing or mistyped column, a client
    // name or phone number that is not one. Not an outage or a cancellation, which are other failures.
    private static bool IsMalformedRow(Exception ex) =>
        ex is InvalidDataException or System.Text.Json.JsonException or FormatException
            or InvalidOperationException or ArgumentException;

    /// <summary>
    /// Who the activity trail says is acting: the signed-in user's name, or in Prototype, where
    /// nobody signs in, "demo user" (an approval then goes against the name typed for it).
    /// </summary>
    public async Task<Actor> ActorAsync(CancellationToken ct = default)
    {
        var (caller, _) = await callers.CurrentAsync(ct);
        return caller is null ? Actor.Demo : Actor.Of(caller);
    }

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

/// <summary>
/// The client's registration could not be read from the clients table, so a campaign cannot be
/// checked against it. Its message is what the page tells her; the cause is logged where it is thrown.
/// </summary>
public sealed class RegistrationUnreadableException(Exception inner) : Exception(
    "Your business's details couldn't be read, so this campaign can't be checked yet. Nothing has been changed, and the problem has been logged for fixing.",
    inner);
