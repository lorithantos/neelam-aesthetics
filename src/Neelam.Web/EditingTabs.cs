using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// Every editor tab open on this instance, so a tab can say the campaign, template or look it edits is also
/// being edited in another tab. A tab is also being edited when some other tab on the same client and
/// subject, on this instance or another, has typed since it opened, within
/// <see cref="EditingTabsOptions.QuietAfter"/>, and is still there: connected, here, or its row fresh
/// within <see cref="EditingTabsOptions.GoneAfter"/>, elsewhere. One per app; ids and times only, never
/// what anyone typed.
/// </summary>
/// <remarks>
/// A tab's notice (<see cref="EditingTab.OthersEditing"/>) is worked out again whenever something it
/// depends on changes here: another tab types or closes, a circuit drops or comes back, the beat brings
/// the rows of other instances (<see cref="ApplyRemote"/>), or the clock is consulted
/// (<see cref="Refresh"/>). A tab whose notice flips is told through its callback, which only hands the
/// news on to its own circuit. A closed tab is told nothing; one closing on another thread at the moment
/// it is told may still hear once, which its page, closing, ignores (as <see cref="PageWatch{T}"/> does).
/// <para>
/// Shared between circuits. The tabs are a concurrent dictionary, each tab's own state one immutable value
/// swapped whole, so opening, typing and reading never wait. Working out the notices takes one lock, so two
/// at once cannot leave a stale answer behind; the tabs are told after it is let go, so a callback that
/// reaches back in never waits on it.
/// </para>
/// </remarks>
public sealed class EditingTabs(TimeProvider clock, IOptions<EditingTabsOptions> options, ILogger<EditingTabs> log)
{
    private readonly EditingTabsOptions _settings = options.Value;
    private readonly ConcurrentDictionary<(ClientName Client, TabSubject Subject, Guid TabId), EditingTab> _tabs = new();
    private readonly ConcurrentDictionary<(ClientName Client, TabSubject Subject), ImmutableArray<RemoteTab>> _remote = new();
    private readonly ConcurrentQueue<ClosedTab> _closed = new();
    // Closed tabs' ids, by when they closed or the beat took them, whichever was later: kept GoneAfter.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _closedLately = new();
    private readonly Lock _evaluating = new();
    private TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Registers a tab opened now. <paramref name="changed"/> is called whenever the tab's
    /// <see cref="EditingTab.OthersEditing"/> flips, on the thread that flipped it, so it should only hand
    /// the news on to the page's circuit and return. Disposing the result closes the tab. The tab's notice
    /// is worked out once as it opens, so it may be told before this returns.
    /// </summary>
    /// <param name="circuitId">The circuit the tab's page runs on, for <see cref="SetConnected"/>.</param>
    public EditingTab Register(ClientName client, TabSubject subject, Guid circuitId, Action changed)
    {
        var tab = new EditingTab(this, client, subject, circuitId, Guid.NewGuid(), clock.GetUtcNow(), changed);
        _tabs[tab.Key] = tab;
        // After the tab is in, so a wait that looked before and found none is woken.
        Interlocked.Exchange(ref _opened, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        // Another tab that typed after this one's opening time was read, while it was not yet in, was
        // worked out without it: work it out now rather than at the next beat.
        Evaluate(other => ReferenceEquals(other, tab), except: null);
        return tab;
    }

    /// <summary>
    /// Marks every tab of the circuit connected or not: a dropped browser's tabs stop counting at once,
    /// and count again if it comes back. The other tabs on their subjects are told when their notice flips.
    /// </summary>
    public void SetConnected(Guid circuitId, bool connected)
    {
        var affected = new HashSet<(ClientName, TabSubject)>();
        foreach (var tab in _tabs.Values)
        {
            if (tab.CircuitId != circuitId) continue;
            tab.Update(state => state with { Connected = connected });
            affected.Add(tab.Edits);
        }
        if (affected.Count > 0) Evaluate(tab => affected.Contains(tab.Edits), except: null);
    }

    /// <summary>
    /// Works out every tab's notice against the clock now, so one clears once the other tab has been quiet
    /// for <see cref="EditingTabsOptions.QuietAfter"/> or its row has gone stale, and tells those that flipped.
    /// </summary>
    public void Refresh() => Evaluate(_ => true, except: null);

    /// <summary>
    /// Replaces what is known of the tabs on other instances for this client and subject with
    /// <paramref name="rows"/>, as the beat read them, leaving out this instance's own tabs: open, or closed
    /// within <see cref="EditingTabsOptions.GoneAfter"/> of the beat taking them (<see cref="DrainClosed"/>),
    /// so a row whose delete failed, or that a beat wrote just as its tab closed, is never another tab here;
    /// past that the row is stale. Then tells the tabs here whose notice flipped. Kept only while a tab here
    /// has the subject open.
    /// </summary>
    public void ApplyRemote(ClientName client, TabSubject subject, IEnumerable<RemoteTab> rows)
    {
        Forget(clock.GetUtcNow());
        // Open tabs first, then closed: a tab closes by joining the closed before it leaves the open, so
        // one closing meanwhile is in one or the other.
        var local = _tabs.Keys.Select(key => key.TabId).ToHashSet();
        local.UnionWith(_closedLately.Keys);
        local.UnionWith(_closed.Select(c => c.TabId));
        var remote = rows.Where(row => !local.Contains(row.TabId)).ToImmutableArray();
        List<EditingTab> flipped;
        lock (_evaluating)
        {
            if (!HasOpen((client, subject))) return;
            _remote[(client, subject)] = remote;
            flipped = Flipped(tab => tab.Edits == (client, subject), except: null);
        }
        Tell(flipped);
    }

    /// <summary>
    /// This instance's open tabs, for the beat to write as rows. A tab already closing is left out: it is
    /// among the closed before it leaves the open, and a beat that took it there must not write it back.
    /// </summary>
    public IReadOnlyList<LocalTab> Snapshot() =>
        _tabs.Values.Where(tab => !tab.IsDisposed).Select(tab =>
        {
            var state = tab.State;
            return new LocalTab(tab.Client, tab.Subject, tab.TabId, state.LastTyped, state.Connected);
        }).ToList();

    /// <summary>
    /// The tabs closed since the last call, each once, for the beat to delete their rows. Their ids are
    /// still this instance's own to <see cref="ApplyRemote"/> for <see cref="EditingTabsOptions.GoneAfter"/>
    /// from now: every row the beat wrote for them was written before this.
    /// </summary>
    public IReadOnlyList<ClosedTab> DrainClosed()
    {
        var now = clock.GetUtcNow();
        Forget(now);
        var drained = new List<ClosedTab>();
        while (_closed.TryDequeue(out var closed))
        {
            _closedLately[closed.TabId] = now;
            drained.Add(closed);
        }
        return drained;
    }

    /// <summary>Completes once at least one tab is open: at once if one already is.</summary>
    public Task WaitForTabsAsync(CancellationToken cancellationToken)
    {
        // Taken before looking, so a tab opened between the two still completes it.
        var opened = Volatile.Read(ref _opened).Task;
        return IsEmpty ? opened.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>Whether no tab is open on this instance.</summary>
    public bool IsEmpty => _tabs.IsEmpty;

    internal void Typed(EditingTab tab)
    {
        var now = clock.GetUtcNow();
        tab.Update(state => state with { LastTyped = now });
        Evaluate(other => other.Edits == tab.Edits, except: tab);
    }

    internal void Close(EditingTab tab)
    {
        // Among the closed and kept as closed lately before it leaves the open. Kept from when it closed
        // unless a beat took it meanwhile, which keeps it from then.
        _closed.Enqueue(new ClosedTab(tab.Client, tab.Subject, tab.TabId));
        _closedLately.TryAdd(tab.TabId, clock.GetUtcNow());
        _tabs.TryRemove(tab.Key, out _);
        List<EditingTab> flipped;
        lock (_evaluating)
        {
            if (!HasOpen(tab.Edits)) _remote.TryRemove(tab.Edits, out _);
            flipped = Flipped(other => other.Edits == tab.Edits, except: null);
        }
        Tell(flipped);
    }

    private bool HasOpen((ClientName, TabSubject) edits) => _tabs.Values.Any(tab => tab.Edits == edits);

    // Drops the closed tabs' ids kept GoneAfter, unless kept again meanwhile.
    private void Forget(DateTimeOffset now)
    {
        foreach (var closed in _closedLately)
            if (now - closed.Value >= _settings.GoneAfter) _closedLately.TryRemove(closed);
    }

    private void Evaluate(Func<EditingTab, bool> which, EditingTab? except)
    {
        List<EditingTab> flipped;
        lock (_evaluating) flipped = Flipped(which, except);
        Tell(flipped);
    }

    // Under the lock: sets each chosen tab's notice afresh and returns those whose notice flipped.
    private List<EditingTab> Flipped(Func<EditingTab, bool> which, EditingTab? except)
    {
        var now = clock.GetUtcNow();
        var tabs = _tabs.Values.ToList();
        var flipped = new List<EditingTab>();
        foreach (var tab in tabs)
        {
            if (ReferenceEquals(tab, except) || !which(tab)) continue;
            if (tab.SetOthersEditing(OthersEditing(tab, tabs, now))) flipped.Add(tab);
        }
        return flipped;
    }

    private bool OthersEditing(EditingTab tab, List<EditingTab> tabs, DateTimeOffset now)
    {
        foreach (var other in tabs)
        {
            if (ReferenceEquals(other, tab) || other.Edits != tab.Edits) continue;
            var state = other.State;
            if (state.Connected && Editing(state.LastTyped, tab, now)) return true;
        }
        if (_remote.TryGetValue(tab.Edits, out var remote))
        {
            foreach (var row in remote)
                if (now - row.RowTimestamp < _settings.GoneAfter && Editing(row.LastTyped, tab, now)) return true;
        }
        return false;
    }

    // Typed since this tab opened, and not quiet for long.
    private bool Editing(DateTimeOffset? lastTyped, EditingTab tab, DateTimeOffset now) =>
        lastTyped is { } typed && typed > tab.OpenedAt && now - typed < _settings.QuietAfter;

    private void Tell(List<EditingTab> flipped)
    {
        foreach (var tab in flipped)
        {
            if (tab.IsDisposed) continue;
            try
            {
                tab.Changed();
            }
            catch (Exception ex)
            {
                // By the failure's type only: a page's exception may carry what it shows.
                log.LogWarning("Editing tabs: a tab could not be told another tab's editing changed: {Error}.", ex.GetType().Name);
            }
        }
    }
}

/// <summary>
/// One editor tab open on this instance, from <see cref="EditingTabs.Register"/>; disposing it closes the
/// tab, and disposing it again does nothing.
/// </summary>
public sealed class EditingTab : IDisposable
{
    private readonly EditingTabs _tabs;
    private TabState _state = new(LastTyped: null, Connected: true);
    private int _othersEditing;
    private int _disposed;

    internal EditingTab(
        EditingTabs tabs, ClientName client, TabSubject subject, Guid circuitId, Guid tabId, DateTimeOffset openedAt,
        Action changed)
    {
        _tabs = tabs;
        Client = client;
        Subject = subject;
        CircuitId = circuitId;
        TabId = tabId;
        OpenedAt = openedAt;
        Changed = changed;
    }

    /// <summary>Whose data the tab edits.</summary>
    public ClientName Client { get; }

    /// <summary>What the tab edits.</summary>
    public TabSubject Subject { get; }

    /// <summary>The circuit its page runs on.</summary>
    public Guid CircuitId { get; }

    /// <summary>New each time a page opens.</summary>
    public Guid TabId { get; }

    public DateTimeOffset OpenedAt { get; }

    /// <summary>When the tab was last typed into; null until it is.</summary>
    public DateTimeOffset? LastTyped => State.LastTyped;

    /// <summary>Whether the tab's circuit is connected, as <see cref="EditingTabs.SetConnected"/> last said.</summary>
    public bool Connected => State.Connected;

    /// <summary>
    /// Whether the same thing is also being edited in another tab, as last worked out: the page shows the
    /// notice while this holds.
    /// </summary>
    public bool OthersEditing => Volatile.Read(ref _othersEditing) != 0;

    /// <summary>Says the tab was just typed into; the other tabs on its subject are told if their notice flips.</summary>
    public void Typed()
    {
        if (!IsDisposed) _tabs.Typed(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _tabs.Close(this);
    }

    internal (ClientName Client, TabSubject Subject, Guid TabId) Key => (Client, Subject, TabId);

    /// <summary>What other tabs are compared on: the same client and the same subject.</summary>
    internal (ClientName Client, TabSubject Subject) Edits => (Client, Subject);

    internal Action Changed { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal TabState State => Volatile.Read(ref _state);

    internal void Update(Func<TabState, TabState> change)
    {
        while (true)
        {
            var seen = State;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, change(seen), seen), seen)) return;
        }
    }

    /// <summary>Sets the notice; true when that flipped it.</summary>
    internal bool SetOthersEditing(bool othersEditing) =>
        Interlocked.Exchange(ref _othersEditing, othersEditing ? 1 : 0) != (othersEditing ? 1 : 0);

    internal sealed record TabState(DateTimeOffset? LastTyped, bool Connected);
}

/// <summary>One of this instance's open tabs, as the beat writes it: ids and times only.</summary>
public sealed record LocalTab(ClientName Client, TabSubject Subject, Guid TabId, DateTimeOffset? LastTyped, bool Connected);

/// <summary>A tab closed on this instance, whose row the beat deletes.</summary>
public sealed record ClosedTab(ClientName Client, TabSubject Subject, Guid TabId);

/// <summary>
/// A tab on another instance, as its row says: when it was last typed into, if ever, and when its instance
/// last wrote the row.
/// </summary>
public sealed record RemoteTab(Guid TabId, DateTimeOffset? LastTyped, DateTimeOffset RowTimestamp);

/// <summary>Settings for the editor tabs' notice, section <c>EditingTabs</c>.</summary>
public sealed class EditingTabsOptions
{
    public const string Section = "EditingTabs";

    /// <summary>How often the beat writes this instance's rows and reads the others: a minute.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long after its last typing another tab stops counting: five minutes, the design's suggestion
    /// and the owner's to change.
    /// </summary>
    public TimeSpan QuietAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How old a tab's row may be before its tab counts as gone: three beats.</summary>
    public TimeSpan GoneAfter { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How old a row may be before the beat sweeps it, for a tab whose instance never deleted it.</summary>
    public TimeSpan SweepAfter { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Why these settings cannot run, or null when they can.</summary>
    public string? Problem() =>
        Interval <= TimeSpan.Zero || QuietAfter <= TimeSpan.Zero || GoneAfter <= Interval || SweepAfter <= GoneAfter
            ? "EditingTabs:Interval and EditingTabs:QuietAfter must be positive, EditingTabs:GoneAfter longer than " +
              "the interval and EditingTabs:SweepAfter longer than that, such as 00:01:00, 00:05:00, 00:03:00 and 1.00:00:00."
            : null;
}
