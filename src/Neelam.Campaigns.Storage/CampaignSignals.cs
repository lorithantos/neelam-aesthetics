using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Tells the pages open on this instance, at once, that someone changed what they show (owner,
/// 2026-10-11, on "concurrent usage by two users"): a campaign saved, undone, restored, approved or
/// withdrawn, and since phase 2 a template, the client's look, her baseline or her tier-name ladders.
/// The realistic case is one person in two tabs, or on phone and laptop. One per app; nothing in it is
/// stored, and it says only which thing changed (<see cref="WatchSubject"/>), never what.
/// </summary>
/// <remarks>
/// In-process only: a change made on another instance is not signalled here, and a write path that
/// forgets to raise it is not either. The page's own periodic look at storage (the owner's "regular
/// checks") is the fallback for both, so a missed signal costs only time, never a wrong email.
/// <para>
/// Shared between circuits, so its state is a concurrent dictionary of immutable lists, replaced whole
/// on every change: a raise works on the list as it was when it started, and listening or leaving never
/// waits on a raise.
/// </para>
/// </remarks>
public sealed class CampaignSignals(ILogger<CampaignSignals>? log = null)
{
    private readonly ConcurrentDictionary<(ClientName Client, string Subject), ImmutableList<Listener>> _listeners = new();

    /// <summary>
    /// Calls <paramref name="changed"/> whenever someone other than <paramref name="owner"/> raises a
    /// change of this subject, until the result is disposed. <paramref name="changed"/> runs on the
    /// raiser's thread, so it should only hand the news on (to its own circuit) and return.
    /// </summary>
    /// <param name="subject">What is watched, as <see cref="WatchSubject"/> names it.</param>
    /// <param name="owner">Who is listening: its own changes are not signalled back to it.</param>
    public IDisposable Listen(ClientName client, string subject, object owner, Action changed)
    {
        var listener = new Listener(this, (client, subject), owner, changed);
        _listeners.AddOrUpdate(listener.Key, _ => [listener], (_, list) => list.Add(listener));
        return listener;
    }

    /// <summary>Listens for changes to one campaign; see <see cref="Listen(ClientName, string, object, Action)"/>.</summary>
    public IDisposable Listen(ClientName client, Guid campaign, object owner, Action changed) =>
        Listen(client, WatchSubject.Campaign(campaign), owner, changed);

    /// <summary>
    /// Says the subject changed, to everyone listening for it but <paramref name="source"/>. Never
    /// throws: the change it reports has already been made, and a listener's failure only loses a
    /// notice the periodic look will give.
    /// </summary>
    /// <param name="source">Who made the change, or null when no listener made it.</param>
    public void Raise(ClientName client, string subject, object? source)
    {
        if (!_listeners.TryGetValue((client, subject), out var listeners)) return;
        foreach (var listener in listeners)
        {
            if (ReferenceEquals(listener.Owner, source) || listener.IsDisposed) continue;
            try
            {
                listener.Changed();
            }
            catch (Exception ex)
            {
                // Ids only, as every log line about a client's data.
                log?.LogWarning(ex, "A page listening for changes to {Subject} of {Client} could not be told.", subject, client.Value);
            }
        }
    }

    /// <summary>Says one campaign changed; see <see cref="Raise(ClientName, string, object?)"/>.</summary>
    public void Raise(ClientName client, Guid campaign, object? source) =>
        Raise(client, WatchSubject.Campaign(campaign), source);

    /// <summary>How many are listening for this subject now.</summary>
    public int Listening(ClientName client, string subject) =>
        _listeners.TryGetValue((client, subject), out var listeners) ? listeners.Count : 0;

    /// <summary>How many are listening for this campaign now.</summary>
    public int Listening(ClientName client, Guid campaign) => Listening(client, WatchSubject.Campaign(campaign));

    /// <summary>
    /// How many subjects (campaigns, templates, documents) anyone is listening for now: none are kept once
    /// their last listener leaves.
    /// </summary>
    public int Campaigns => _listeners.Count;

    // Replaces the list without this listener; the last one out takes the subject's entry with it.
    // Both are compare-and-swap on the exact list read, so a listener joining meanwhile is never lost.
    private void Remove(Listener listener)
    {
        while (_listeners.TryGetValue(listener.Key, out var list))
        {
            var without = list.Remove(listener);
            if (ReferenceEquals(without, list)) return;
            var done = without.IsEmpty
                ? ((ICollection<KeyValuePair<(ClientName, string), ImmutableList<Listener>>>)_listeners).Remove(new(listener.Key, list))
                : _listeners.TryUpdate(listener.Key, without, list);
            if (done) return;
        }
    }

    private sealed class Listener(CampaignSignals signals, (ClientName, string) key, object owner, Action changed) : IDisposable
    {
        private int _disposed;

        public (ClientName, string) Key { get; } = key;

        public object Owner { get; } = owner;

        public Action Changed { get; } = changed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) signals.Remove(this);
        }
    }
}

/// <summary>
/// What an open page watches, by name, for <see cref="CampaignSignals"/>: one campaign or template by
/// its id, or one of the client's own documents, of which she has one each. Ids only, never content.
/// </summary>
public static class WatchSubject
{
    public static string Campaign(Guid id) => $"campaign/{id:N}";

    public static string Template(Guid id) => $"template/{id:N}";

    /// <summary>The client's look.</summary>
    public const string Look = "look";

    /// <summary>The client's own template baseline.</summary>
    public const string Baseline = "baseline";

    /// <summary>The client's own tier-name ladders.</summary>
    public const string Ladders = "ladders";
}

/// <summary>
/// What an open page holds, as the page's look at storage sees it (<c>PageWatch</c> in the web app):
/// what it holds now, how to read what storage has now, and what changed between the two.
/// </summary>
/// <typeparam name="T">One version as storage lists it: a <see cref="SaveRef"/> or a <see cref="DocumentVersion"/>.</typeparam>
public interface IWatched<T> where T : class
{
    /// <summary>Whose data it is.</summary>
    ClientName Client { get; }

    /// <summary>
    /// What its changes are signalled under (<see cref="WatchSubject"/>); null while there is nothing in
    /// storage to watch, such as a campaign not saved yet.
    /// </summary>
    string? Subject { get; }

    /// <summary>The version the page holds, as storage gave it when the page opened it or last changed it.</summary>
    T? Held { get; }

    /// <summary>The newest version in use as storage has it now; null when there is none.</summary>
    Task<T?> NewestAsync(CancellationToken ct = default);

    /// <summary>What changed in storage since <see cref="Held"/>, given what <see cref="NewestAsync"/> read.</summary>
    CampaignChange ChangeSince(T? newest);
}
