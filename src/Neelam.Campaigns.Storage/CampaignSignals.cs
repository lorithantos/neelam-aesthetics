using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Tells the campaign pages open on this instance, at once, that someone saved, undid, restored,
/// approved or withdrew a campaign they show (owner, 2026-10-11, on "concurrent usage by two users").
/// The realistic case is one person in two tabs, or on phone and laptop. One per app; nothing in it is
/// stored, and it says only which campaign changed, never what.
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
    private readonly ConcurrentDictionary<(ClientName Client, Guid Campaign), ImmutableList<Listener>> _listeners = new();

    /// <summary>
    /// Calls <paramref name="changed"/> whenever someone other than <paramref name="owner"/> raises a
    /// change of this campaign, until the result is disposed. <paramref name="changed"/> runs on the
    /// raiser's thread, so it should only hand the news on (to its own circuit) and return.
    /// </summary>
    /// <param name="owner">Who is listening: its own changes are not signalled back to it.</param>
    public IDisposable Listen(ClientName client, Guid campaign, object owner, Action changed)
    {
        var listener = new Listener(this, (client, campaign), owner, changed);
        _listeners.AddOrUpdate(listener.Key, _ => [listener], (_, list) => list.Add(listener));
        return listener;
    }

    /// <summary>
    /// Says the campaign changed, to everyone listening for it but <paramref name="source"/>. Never
    /// throws: the change it reports has already been made, and a listener's failure only loses a
    /// notice the periodic look will give.
    /// </summary>
    /// <param name="source">Who made the change, or null when no listener made it.</param>
    public void Raise(ClientName client, Guid campaign, object? source)
    {
        if (!_listeners.TryGetValue((client, campaign), out var listeners)) return;
        foreach (var listener in listeners)
        {
            if (ReferenceEquals(listener.Owner, source) || listener.IsDisposed) continue;
            try
            {
                listener.Changed();
            }
            catch (Exception ex)
            {
                // Ids only, as every log line about a campaign.
                log?.LogWarning(ex, "A page listening for changes to campaign {Campaign} of {Client} could not be told.", campaign, client.Value);
            }
        }
    }

    /// <summary>How many are listening for this campaign now.</summary>
    public int Listening(ClientName client, Guid campaign) =>
        _listeners.TryGetValue((client, campaign), out var listeners) ? listeners.Count : 0;

    /// <summary>How many campaigns anyone is listening for now: none are kept once their last listener leaves.</summary>
    public int Campaigns => _listeners.Count;

    // Replaces the list without this listener; the last one out takes the campaign's entry with it.
    // Both are compare-and-swap on the exact list read, so a listener joining meanwhile is never lost.
    private void Remove(Listener listener)
    {
        while (_listeners.TryGetValue(listener.Key, out var list))
        {
            var without = list.Remove(listener);
            if (ReferenceEquals(without, list)) return;
            var done = without.IsEmpty
                ? ((ICollection<KeyValuePair<(ClientName, Guid), ImmutableList<Listener>>>)_listeners).Remove(new(listener.Key, list))
                : _listeners.TryUpdate(listener.Key, without, list);
            if (done) return;
        }
    }

    private sealed class Listener(CampaignSignals signals, (ClientName, Guid) key, object owner, Action changed) : IDisposable
    {
        private int _disposed;

        public (ClientName, Guid) Key { get; } = key;

        public object Owner { get; } = owner;

        public Action Changed { get; } = changed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) signals.Remove(this);
        }
    }
}
