using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
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
/// swapped whole, so opening, typing and reading never wait. The notices are worked out by one loop reading
/// a mailbox, so no two are worked out at once and no caller waits for it: whoever changes something a
/// notice depends on makes the change itself and then posts, and the Task it gets completes once the loop
/// has worked the notices out and told the tabs that flipped, in the order posted. Everything waiting when
/// the loop wakes is one working-out. The loop alone holds what is known of other instances' tabs. Design:
/// <c>docs/editing-tabs-mailbox.md</c>.
/// </para>
/// <para>
/// A token cancels only a post the loop has not taken yet, which then did not happen; once taken, its Task
/// says what happened. A post made from inside a working-out, as from a tab's callback, is refused: it could
/// only wait on the loop that is running it. A working-out that fails is logged by the failure's type and
/// fails its posts, and the loop goes on. Should the loop itself end, everything waiting fails and the next
/// post builds a new one, which knows nothing of other instances until the next beat and starts by working
/// every tab out. Ended <see cref="DeathsAllowed"/> times within <see cref="DeathWindow"/>, it is not built
/// again: no tab is marked or told from then on, and a post completes at once. The check as she saves is
/// untouched by any of this.
/// </para>
/// </remarks>
public sealed class EditingTabs : IAsyncDisposable
{
    /// <summary>How many times the loop may end within <see cref="DeathWindow"/>: at the last it is not built again.</summary>
    internal const int DeathsAllowed = 3;

    /// <summary>How close together <see cref="DeathsAllowed"/> ends of the loop must be to stop it being built again.</summary>
    internal static readonly TimeSpan DeathWindow = TimeSpan.FromMinutes(10);

    private const int Running = 0;
    private const int Off = 1;
    private const int Disposed = 2;

    // The registry this thread is working notices out for, while it is: a post to it from here is refused.
    [ThreadStatic]
    private static EditingTabs? t_working;

    private readonly TimeProvider _clock;
    private readonly EditingTabsOptions _settings;
    private readonly ILogger<EditingTabs> _log;
    private readonly ConcurrentDictionary<(ClientName Client, TabSubject Subject, Guid TabId), EditingTab> _tabs = new();
    private readonly ConcurrentQueue<ClosedTab> _closed = new();
    // Closed tabs' ids, by when they closed or the beat took them, whichever was later: kept GoneAfter.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _closedLately = new();
    // When the loop ended, on the clock's timestamps: those within DeathWindow.
    private readonly ConcurrentQueue<long> _deaths = new();
    private TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The loop posts go to; null once one has ended, until the next post builds another.
    private Loop? _loop;
    private int _state = Running;

    public EditingTabs(TimeProvider clock, IOptions<EditingTabsOptions> options, ILogger<EditingTabs> log)
    {
        _clock = clock;
        _settings = options.Value;
        _log = log;
        _loop = Start();
    }

    /// <summary>
    /// Registers a tab opened now. <paramref name="changed"/> is called whenever the tab's
    /// <see cref="EditingTab.OthersEditing"/> flips, on the thread that worked it out, so it should only hand
    /// the news on to the page's circuit and return; it must not post here (it is refused). Disposing the
    /// result closes the tab. The tab's notice is worked out as soon as it is in, not before this returns.
    /// </summary>
    /// <param name="circuitId">The circuit the tab's page runs on, for <see cref="SetConnected"/>.</param>
    public EditingTab Register(ClientName client, TabSubject subject, Guid circuitId, Action changed)
    {
        RefuseWhileWorking();
        var tab = new EditingTab(this, client, subject, circuitId, Guid.NewGuid(), _clock.GetUtcNow(), changed);
        _tabs[tab.Key] = tab;
        // After the tab is in, so a wait that looked before and found none is woken.
        Interlocked.Exchange(ref _opened, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        // Another tab that typed after this one's opening time was read, while it was not yet in, was
        // worked out without it: worked out now rather than at the next beat.
        tab.Opened = Post(Scope.Just(tab));
        return tab;
    }

    /// <summary>
    /// Marks every tab of the circuit connected or not: a dropped browser's tabs stop counting at once,
    /// and count again if it comes back. The Task completes once the other tabs on their subjects have been
    /// worked out and those whose notice flipped told.
    /// </summary>
    public Task SetConnected(Guid circuitId, bool connected)
    {
        RefuseWhileWorking();
        var affected = new HashSet<(ClientName, TabSubject)>();
        foreach (var tab in _tabs.Values)
        {
            if (tab.CircuitId != circuitId) continue;
            tab.Update(state => state with { Connected = connected });
            affected.Add(tab.Edits);
        }
        return affected.Count > 0 ? Post(Scope.Of(affected)) : Task.CompletedTask;
    }

    /// <summary>
    /// Works out every tab's notice against the clock, so one clears once the other tab has been quiet for
    /// <see cref="EditingTabsOptions.QuietAfter"/> or its row has gone stale, and tells those that flipped;
    /// the Task completes once that is done. <paramref name="cancellationToken"/> cancels it only until the
    /// loop takes it.
    /// </summary>
    public Task Refresh(CancellationToken cancellationToken = default) => Post(Scope.Everything, null, cancellationToken);

    /// <summary>
    /// Replaces what is known of the tabs on other instances for this client and subject with
    /// <paramref name="rows"/>, as the beat read them, leaving out this instance's own tabs: open, or closed
    /// within <see cref="EditingTabsOptions.GoneAfter"/> of the beat taking them (<see cref="DrainClosed"/>),
    /// so a row whose delete failed, or that a beat wrote just as its tab closed, is never another tab here;
    /// past that the row is stale. Then tells the tabs here whose notice flipped; the Task completes once that
    /// is done. Kept only while a tab here has the subject open. <paramref name="cancellationToken"/> cancels
    /// it only until the loop takes it: cancelled, the rows were not taken.
    /// </summary>
    public Task ApplyRemote(
        ClientName client, TabSubject subject, IEnumerable<RemoteTab> rows, CancellationToken cancellationToken = default)
    {
        RefuseWhileWorking();
        Forget(_clock.GetUtcNow());
        // Open tabs first, then closed: a tab closes by joining the closed before it leaves the open, so
        // one closing meanwhile is in one or the other.
        var local = _tabs.Keys.Select(key => key.TabId).ToHashSet();
        local.UnionWith(_closedLately.Keys);
        local.UnionWith(_closed.Select(c => c.TabId));
        var remote = rows.Where(row => !local.Contains(row.TabId)).ToImmutableArray();
        var edits = (client, subject);
        // Looking and writing are one step: only the loop writes what it holds. A tab closing as it looks
        // posts after it has left, and that post drops the rows again.
        return Post(Scope.Of(edits), loop =>
        {
            if (HasOpen(edits)) loop.Remote[edits] = remote;
        }, cancellationToken);
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
        var now = _clock.GetUtcNow();
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

    /// <summary>
    /// Completes once everything posted before it has been worked out and told: for a test, after a tab
    /// opened or closed, neither of which hands back a Task.
    /// </summary>
    internal Task SettledAsync() => Post(Scope.Nothing);

    /// <summary>Whether the loop ended too often to be built again: no tab is marked from then on.</summary>
    internal bool NoticesOff => Volatile.Read(ref _state) == Off;

    internal Task Typed(EditingTab tab)
    {
        RefuseWhileWorking();
        // Stamped here, before the post: whichever working-out runs next sees it.
        var now = _clock.GetUtcNow();
        tab.Update(state => state with { LastTyped = now });
        return Post(Scope.Of(tab.Edits, except: tab));
    }

    internal void Close(EditingTab tab)
    {
        // Among the closed and kept as closed lately before it leaves the open. Kept from when it closed
        // unless a beat took it meanwhile, which keeps it from then.
        _closed.Enqueue(new ClosedTab(tab.Client, tab.Subject, tab.TabId));
        _closedLately.TryAdd(tab.TabId, _clock.GetUtcNow());
        _tabs.TryRemove(tab.Key, out _);
        var edits = tab.Edits;
        _ = Post(Scope.Of(edits), loop =>
        {
            if (!HasOpen(edits)) loop.Remote.Remove(edits);
        });
    }

    /// <summary>
    /// Stops the loop: what it has taken is finished, what still waits is cancelled, and every post from
    /// then on is handed a cancelled Task rather than an exception, as a page closing later in a shutdown
    /// makes one. The app's container calls this after the beat has stopped.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        RefuseWhileWorking();
        if (Interlocked.Exchange(ref _state, Disposed) == Disposed) return;
        if (Interlocked.Exchange(ref _loop, null) is not { } loop) return;
        loop.Mail.Writer.TryComplete();
        await loop.Running.ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a call that would post from the thread working the notices out, before it changes anything:
    /// the post could only wait on the loop that is running its caller.
    /// </summary>
    internal void RefuseWhileWorking()
    {
        if (ReferenceEquals(t_working, this))
            throw new InvalidOperationException(
                "Refused a call to the editing tabs from inside their own working-out, such as from a tab's " +
                "callback: it would wait on the loop that is running it. A callback only hands the news on and returns.");
    }

    // Every change a notice depends on is made by its caller first and posted after, so a working-out
    // that read the tabs while they moved is put right by the one the change posts.
    private Task Post(Scope scope, Action<Loop>? step = null, CancellationToken cancellationToken = default)
    {
        RefuseWhileWorking();
        // Already cancelled: refused, and nothing is posted.
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        var item = new Item(scope, step, cancellationToken);
        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Disposed:
                    item.Cancel();
                    return item.Done;
                case Off:
                    item.Ran();
                    return item.Done;
            }
            // Refused only by a loop that has ended, which took itself out first: the next is built.
            if (Current().Mail.Writer.TryWrite(item)) return item.Done;
        }
    }

    private Loop Current()
    {
        while (true)
        {
            if (Volatile.Read(ref _loop) is { } loop) return loop;
            var fresh = Start();
            if (Interlocked.CompareExchange(ref _loop, fresh, null) is not null)
            {
                // Another post built one first.
                fresh.Mail.Writer.TryComplete();
                continue;
            }
            // Stopped while this one was being built: it ends once it has dealt with what reached it.
            if (Volatile.Read(ref _state) != Running)
            {
                Interlocked.CompareExchange(ref _loop, null, fresh);
                fresh.Mail.Writer.TryComplete();
            }
            return fresh;
        }
    }

    private Loop Start()
    {
        var loop = new Loop();
        // On the pool with no context of whoever happens to build it (a circuit's, a request's): the loop
        // outlives them. Parked between posts, it holds no thread.
        if (ExecutionContext.IsFlowSuppressed())
        {
            loop.Running = Task.Run(() => RunAsync(loop));
        }
        else
        {
            using (ExecutionContext.SuppressFlow()) loop.Running = Task.Run(() => RunAsync(loop));
        }
        return loop;
    }

    private async Task RunAsync(Loop loop)
    {
        var batch = new List<Item>();
        try
        {
            while (await loop.Mail.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Everything waiting is taken as one working-out. A post cancelled before this is passed
                // over; one taken here reports what happened, whatever its token does next.
                while (loop.Mail.Reader.TryRead(out var item))
                {
                    if (Volatile.Read(ref _state) == Disposed) item.Cancel();
                    else if (item.TryClaim()) batch.Add(item);
                }
                if (batch.Count > 0) Work(loop, batch);
                batch.Clear();
            }
        }
        catch (Exception ex)
        {
            Ended(loop, batch, ex);
        }
    }

    // One working-out for everything taken: each post's own step in the order posted, the notices of every
    // tab any of them reaches worked out once, the tabs whose notice differs from before told, and then each
    // post's Task completed. Synchronous, so the mark that refuses a post from here is never held over an await.
    private void Work(Loop loop, List<Item> batch)
    {
        t_working = this;
        try
        {
            foreach (var item in batch) item.Step?.Invoke(loop);
            var reach = new Reach(all: loop.Fresh);
            foreach (var item in batch) reach.Add(item.Scope);
            if (!reach.IsEmpty) Tell(Flipped(loop, reach));
            loop.Fresh = false;
        }
        catch (Exception ex)
        {
            // What this left half done is put right by the next working-out, which takes every tab.
            loop.Fresh = true;
            // By the failure's type only, as a tab's own failure is. Logged before the posts fail, so
            // whoever awaits one finds the log already written.
            _log.LogWarning("Editing tabs: the notices could not be worked out: {Error}.", ex.GetType().Name);
            foreach (var item in batch) item.Failed(ex);
            return;
        }
        finally
        {
            t_working = null;
        }
        foreach (var item in batch) item.Ran();
    }

    // The loop ended on something nothing caught, a bug: everything it held or was sent fails rather than
    // waits for ever, and the next post builds another, unless it has ended too often lately.
    private void Ended(Loop loop, List<Item> batch, Exception ex)
    {
        // Counted first, so a post that finds no loop already knows whether to build one.
        var last = TooManyDeaths();
        if (last) Interlocked.CompareExchange(ref _state, Off, Running);
        // Out before it is closed, and by value, so a post it refuses builds the next one and a loop that
        // has already replaced this one is never taken out.
        Interlocked.CompareExchange(ref _loop, null, loop);
        loop.Mail.Writer.TryComplete(ex);
        try
        {
            if (last)
                _log.LogCritical(
                    "Editing tabs: the loop that works the notices out ended {Deaths} times within {Window}, last on {Error}. " +
                    "It is not started again: no tab is told of another tab's editing until the site restarts. A save still finds another tab's.",
                    DeathsAllowed, DeathWindow, ex.GetType().Name);
            else
                _log.LogError("Editing tabs: the loop that works the notices out ended on {Error}; the next change starts another.", ex.GetType().Name);
        }
        catch (Exception)
        {
            // A log that fails must not leave anyone waiting.
        }
        foreach (var item in batch) item.Failed(ex);
        while (loop.Mail.Reader.TryRead(out var item)) item.Failed(ex);
    }

    private bool TooManyDeaths()
    {
        try
        {
            var now = _clock.GetTimestamp();
            _deaths.Enqueue(now);
            while (_deaths.TryPeek(out var first) && _clock.GetElapsedTime(first, now) > DeathWindow) _deaths.TryDequeue(out _);
            return _deaths.Count >= DeathsAllowed;
        }
        catch (Exception)
        {
            // The clock itself fails: nothing can be counted, so no more are built.
            return true;
        }
    }

    private bool HasOpen((ClientName, TabSubject) edits) => _tabs.Values.Any(tab => tab.Edits == edits);

    // Drops the closed tabs' ids kept GoneAfter, unless kept again meanwhile.
    private void Forget(DateTimeOffset now)
    {
        foreach (var closed in _closedLately)
            if (now - closed.Value >= _settings.GoneAfter) _closedLately.TryRemove(closed);
    }

    // On the loop: sets the notice of each tab reached afresh and returns those whose notice flipped.
    private List<EditingTab> Flipped(Loop loop, Reach reach)
    {
        var now = _clock.GetUtcNow();
        var tabs = _tabs.Values.ToList();
        var flipped = new List<EditingTab>();
        foreach (var tab in tabs)
        {
            if (!reach.Covers(tab)) continue;
            if (tab.SetOthersEditing(OthersEditing(loop, tab, tabs, now))) flipped.Add(tab);
        }
        return flipped;
    }

    private bool OthersEditing(Loop loop, EditingTab tab, List<EditingTab> tabs, DateTimeOffset now)
    {
        foreach (var other in tabs)
        {
            if (ReferenceEquals(other, tab) || other.Edits != tab.Edits) continue;
            var state = other.State;
            if (state.Connected && Editing(state.LastTyped, tab, now)) return true;
        }
        if (loop.Remote.TryGetValue(tab.Edits, out var remote))
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
                _log.LogWarning("Editing tabs: a tab could not be told another tab's editing changed: {Error}.", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// One loop, its mailbox and what it alone holds. A loop that ends is replaced whole, so the next knows
    /// nothing of other instances until the beat brings their rows again, and works every tab out first.
    /// </summary>
    private sealed class Loop
    {
        // One reader, and never run on the poster's thread: a post must not work the notices out inline
        // on a circuit (as StartLog's queue).
        public Channel<Item> Mail { get; } = Channel.CreateUnbounded<Item>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

        /// <summary>The tabs on other instances, by client and subject, as the beat last read them.</summary>
        public Dictionary<(ClientName, TabSubject), ImmutableArray<RemoteTab>> Remote { get; } = [];

        /// <summary>Whether the next working-out takes every tab: a loop's first, and the one after a failure.</summary>
        public bool Fresh { get; set; } = true;

        public Task Running { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// One post: the tabs to work out, a step on what the loop holds if it has one, and the Task its poster
    /// was handed. Waiting until the loop takes it or its token cancels it, whichever is first; only one is.
    /// </summary>
    private sealed class Item
    {
        private const int Waiting = 0;
        private const int Claimed = 1;
        private const int Cancelled = 2;

        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _registration;
        private int _state = Waiting;

        // Listening to its token from before the loop can see it, so the loop only ever stops a listening
        // that is already there.
        public Item(Scope scope, Action<Loop>? step, CancellationToken token)
        {
            Scope = scope;
            Step = step;
            _token = token;
            _registration = token.UnsafeRegister(static item => ((Item)item!).TryCancel(), this);
        }

        public Scope Scope { get; }

        public Action<Loop>? Step { get; }

        public Task Done => _done.Task;

        public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Waiting) == Waiting;

        /// <summary>Its token fired: cancelled if the loop has not taken it, and nothing otherwise.</summary>
        private void TryCancel()
        {
            if (Interlocked.CompareExchange(ref _state, Cancelled, Waiting) == Waiting) _done.TrySetCanceled(_token);
        }

        /// <summary>The loop is stopping: cancelled unless taken already.</summary>
        public void Cancel()
        {
            _registration.Unregister();
            if (Interlocked.CompareExchange(ref _state, Cancelled, Waiting) == Waiting) _done.TrySetCanceled();
        }

        public void Ran()
        {
            _registration.Unregister();
            _done.TrySetResult();
        }

        /// <summary>Failed, and marked as seen: a post nobody awaits (a tab closing) leaves no unobserved failure.</summary>
        public void Failed(Exception ex)
        {
            _registration.Unregister();
            _done.TrySetException(ex);
            _ = _done.Task.Exception;
        }
    }

    /// <summary>
    /// Which tabs a post asks to have worked out: every one, those on some subjects (but for the one tab
    /// that typed, whose own notice its typing cannot change), or just one.
    /// </summary>
    private readonly record struct Scope(
        bool All, IReadOnlyCollection<(ClientName, TabSubject)>? Subjects, EditingTab? Except, EditingTab? Only)
    {
        public static Scope Nothing => default;

        public static Scope Everything => new(true, null, null, null);

        public static Scope Of((ClientName, TabSubject) edits, EditingTab? except = null) => new(false, [edits], except, null);

        public static Scope Of(IReadOnlyCollection<(ClientName, TabSubject)> subjects) => new(false, subjects, null, null);

        public static Scope Just(EditingTab tab) => new(false, null, null, tab);
    }

    /// <summary>
    /// The tabs one working-out takes: the union of what its posts ask for. A subject leaves a tab out only
    /// when every post for it leaves that same tab out.
    /// </summary>
    private sealed class Reach(bool all)
    {
        private readonly Dictionary<(ClientName, TabSubject), EditingTab?> _subjects = [];
        private readonly HashSet<EditingTab> _tabs = [];
        private bool _all = all;

        public bool IsEmpty => !_all && _subjects.Count == 0 && _tabs.Count == 0;

        public void Add(Scope scope)
        {
            _all |= scope.All;
            if (scope.Only is { } tab) _tabs.Add(tab);
            if (scope.Subjects is null) return;
            foreach (var edits in scope.Subjects)
                _subjects[edits] = !_subjects.TryGetValue(edits, out var except) || ReferenceEquals(except, scope.Except) ? scope.Except : null;
        }

        public bool Covers(EditingTab tab) =>
            _all || _tabs.Contains(tab) || (_subjects.TryGetValue(tab.Edits, out var except) && !ReferenceEquals(except, tab));
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
    /// notice while this holds. Never once the notices are off (<see cref="EditingTabs.NoticesOff"/>): one
    /// left showing would never clear.
    /// </summary>
    public bool OthersEditing => Volatile.Read(ref _othersEditing) != 0 && !_tabs.NoticesOff;

    /// <summary>
    /// Says the tab was just typed into, which is marked before this returns; the Task completes once the
    /// other tabs on its subject have been worked out and those whose notice flipped told. A page ignores it.
    /// </summary>
    public Task Typed() => IsDisposed ? Task.CompletedTask : _tabs.Typed(this);

    public void Dispose()
    {
        if (IsDisposed) return;
        // Before it is marked closed: refused from inside a working-out, it stays open.
        _tabs.RefuseWhileWorking();
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _tabs.Close(this);
    }

    /// <summary>Completes once the tab's notice has been worked out for its opening: for a test to await.</summary>
    internal Task Opened { get; set; } = Task.CompletedTask;

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
