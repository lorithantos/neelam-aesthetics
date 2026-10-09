using Microsoft.Extensions.Logging;

namespace Neelam.Campaigns.Storage;

/// <summary>What an activity event is about.</summary>
public enum ActivityEntity
{
    Campaign,
    Template,

    /// <summary>The client's own template baseline.</summary>
    Baseline,

    /// <summary>An entry in the client's image library.</summary>
    ImageEntry,

    /// <summary>The client's row in the clients table.</summary>
    ClientRegistration,
}

/// <summary>What was done. Stored by name, so the numbers may change but the names may not.</summary>
public enum ActivityAction
{
    Saved,
    Approved,
    ApprovalWithdrawn,

    /// <summary>A save marked undone: gone from every list, restorable for the grace period.</summary>
    Undone,
    Restored,

    /// <summary>An undone save deleted for good by the undo sweep once its grace period had passed.</summary>
    DeletedBySweep,
    BaselineSaved,

    /// <summary>The client's own baseline deleted, so the standard one applies again.</summary>
    BaselineResetToStandard,
    ImageEntryAdded,
    ImageEntryRemoved,
    ClientRegistered,
    ClientChanged,
}

/// <summary>
/// One thing someone did, as the activity trail keeps it: who, what, to which thing, and when.
/// <b>Never the content</b> (owner, 2026-10-09: "deletions too are recorded, but not the data"): no
/// subject, label, block text, price, phone number, or name of an offer or a photo. Things are named
/// only by their ids, and a deleted save's event says it was deleted, never what it held.
/// </summary>
/// <param name="Client">Whose activity it is; also the table partition, so it sits under that client's access rules.</param>
/// <param name="EntityId">
/// The campaign's or template's id, <c>baseline</c>, an image entry's own random id (never its name), or
/// the client's name for its registration.
/// </param>
/// <param name="SaveStamp">The save's date/time stamp where the action concerns one save; otherwise null.</param>
/// <param name="Actor">The signed-in user's name; in Prototype, the name typed for an approval, or "demo user".</param>
/// <param name="At">The server's UTC time.</param>
public sealed record ActivityEvent(
    ClientName Client, ActivityEntity Entity, string EntityId, string? SaveStamp, ActivityAction Action,
    string Actor, DateTimeOffset At);

/// <summary>The activity table: one row per event, written and never changed. Nothing here reads it back yet.</summary>
public interface IActivityLog
{
    Task RecordAsync(ActivityEvent activity, CancellationToken cancellationToken = default);
}

/// <summary>Who an action is recorded against.</summary>
/// <param name="FromSignIn">
/// True when <paramref name="Name"/> comes from a real sign-in. False in Prototype, where nobody signs in:
/// there an approval is recorded against the name typed for it.
/// </param>
public sealed record Actor(string Name, bool FromSignIn)
{
    /// <summary>Prototype's caller, who has not signed in.</summary>
    public static Actor Demo { get; } = new("demo user", FromSignIn: false);

    /// <summary>The background undo sweep, which deletes undone saves once their grace period has passed.</summary>
    public static Actor UndoSweep { get; } = new("undo sweep", FromSignIn: false);

    /// <summary>The caller's name from the sign-in, or <see cref="Demo"/> for a caller with none (Prototype).</summary>
    public static Actor Of(Caller caller) =>
        string.IsNullOrWhiteSpace(caller.Name) ? Demo : new Actor(caller.Name.Trim(), FromSignIn: true);

    /// <summary>
    /// Who an approval or its withdrawal is recorded against: the signed-in user, or in Prototype the
    /// name typed for it when there is one.
    /// </summary>
    public string NameFor(string? typed) =>
        FromSignIn || string.IsNullOrWhiteSpace(typed) ? Name : typed.Trim();
}

/// <summary>
/// Writes activity events, and never lets one stop what it records: a write that fails is logged
/// and the action carries on. The trail is wanted, but a campaign must never fail to save because
/// the activity table could not be reached.
/// </summary>
public sealed class ActivityRecorder(IActivityLog log, TimeProvider clock, ILogger<ActivityRecorder> logger)
{
    /// <summary>A trail for one client and one actor, for the stores opened for a request.</summary>
    public ActivityTrail For(ClientName client, Actor actor) => new(this, client, actor);

    /// <summary>Records an event now, in the server's UTC time. Never throws.</summary>
    public async Task RecordAsync(
        ClientName client, ActivityEntity entity, string entityId, string? saveStamp, ActivityAction action, string actor,
        CancellationToken ct = default)
    {
        try
        {
            await log.RecordAsync(new ActivityEvent(client, entity, entityId, saveStamp, action, actor, clock.GetUtcNow()), ct);
        }
        catch (Exception ex)
        {
            // Ids only, as in the event itself: the log never carries content either.
            logger.LogError(ex, "Could not record activity {Action} on {Entity} {EntityId} for {Client}; the action itself went ahead.",
                action, entity, entityId, client.Value);
        }
    }
}

/// <summary>An <see cref="ActivityRecorder"/> bound to one client and one actor.</summary>
public sealed class ActivityTrail
{
    private readonly ActivityRecorder _recorder;

    internal ActivityTrail(ActivityRecorder recorder, ClientName client, Actor actor)
    {
        _recorder = recorder;
        Client = client;
        Actor = actor;
    }

    public ClientName Client { get; }

    public Actor Actor { get; }

    /// <summary>Records an event against <see cref="Actor"/>, or against <paramref name="actorName"/> when given. Never throws.</summary>
    public Task RecordAsync(
        ActivityEntity entity, string entityId, string? saveStamp, ActivityAction action, CancellationToken ct = default,
        string? actorName = null) =>
        _recorder.RecordAsync(Client, entity, entityId, saveStamp, action, actorName ?? Actor.Name, ct);
}
