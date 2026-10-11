using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// One open campaign page's look at storage, so the page knows when what it shows is no longer the
/// campaign as it stands: saved again, its version undone, its approval withdrawn or given elsewhere.
/// The owner's design (2026-10-11): "Regular checks on the table will tell us if anything has been
/// saved, so we can pop up 'modified' after no more than one or two minutes". Three parts:
/// <list type="bullet">
/// <item>a timer on the page's circuit, every <see cref="CampaignWatchOptions.Interval"/>, reading the
/// campaign's newest save and its approval (<see cref="DraftSession.NewestAsync"/>: one listing and one
/// query), which sees a change made on any instance or device;</item>
/// <item>the in-process <see cref="CampaignSignals"/>, raised by every save, undo, restore, approval and
/// withdrawal on this instance, which makes the same read at once, so on one instance the page knows
/// within moments;</item>
/// <item>the read once more after each of the page's own actions, and whenever the page is about to
/// offer the export (<see cref="Observations"/>), so the export appears only from a read that agrees.</item>
/// </list>
/// </summary>
/// <remarks>
/// Everything but <see cref="Changed"/> runs on the page's circuit, one thing at a time, so its own state
/// needs no lock. The signal and the timer arrive on other threads and only hand the work to the circuit.
/// Disposed with the page: the timer stops and the page stops listening, so a closed tab costs nothing.
/// </remarks>
public sealed class CampaignWatch : IDisposable
{
    private readonly CampaignSignals _signals;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly Func<Func<Task>, Task> _onCircuit;
    private readonly ILogger _log;

    private DraftSession? _session;
    private IDisposable? _listening;
    private ITimer? _timer;
    private bool _started;
    private int _disposed;

    // What storage said last: the campaign's newest save in use, with its approval.
    private SaveRef? _observed;

    // Moves on with every own action, so a read begun before one is not taken for after it.
    private int _generation;

    // The page's own actions under way: storage is changing under them, so nothing is said until done.
    private int _acting;

    // The read under way, if any, and whether another is wanted after it.
    private Task? _running;
    private bool _again;

    /// <param name="onCircuit">Runs work on the page's circuit and then renders the page (the page's own InvokeAsync).</param>
    public CampaignWatch(
        CampaignSignals signals, TimeProvider clock, TimeSpan interval, Func<Func<Task>, Task> onCircuit, ILogger log)
    {
        _signals = signals;
        _clock = clock;
        _interval = interval;
        _onCircuit = onCircuit;
        _log = log;
    }

    /// <summary>
    /// How many times storage has been read for this page so far, the read the page opened with
    /// included. A page about to offer the export waits for one more (<see cref="CheckAsync"/>).
    /// </summary>
    public int Observations { get; private set; }

    /// <summary>
    /// What changed in storage since the version the page holds, by the last read; none while the
    /// page's own action is under way, and none before the campaign is first saved.
    /// </summary>
    public CampaignChange Change =>
        _session is null || _acting > 0 || Observations == 0 ? CampaignChange.None : _session.ChangeSince(_observed);

    /// <summary>
    /// Watches this session from now: what it was just opened or saved with counts as a read, since it
    /// came from storage a moment ago. Null, or a campaign not yet saved, watches nothing.
    /// </summary>
    public void Watch(DraftSession? session)
    {
        _session = session;
        _generation++;
        _listening?.Dispose();
        _listening = null;
        if (session?.Latest is null) return;
        _observed = session.Latest;
        Observations++;
        if (_started) Listen(session);
    }

    /// <summary>
    /// Starts listening and the timer. Called once the page is live on its circuit, never while it is
    /// rendered ahead of time, which is thrown away.
    /// </summary>
    public void Start()
    {
        if (_started || IsDisposed) return;
        _started = true;
        if (_session?.Latest is not null) Listen(_session);
        _timer = _clock.CreateTimer(_ => Changed(), null, _interval, _interval);
    }

    private void Listen(DraftSession session) =>
        _listening = _signals.Listen(session.Client, session.Id!.Value, session, Changed);

    /// <summary>The page's own action starts: what storage says meanwhile is not news.</summary>
    public void Begin()
    {
        _acting++;
        _generation++;
    }

    /// <summary>The page's own action is done: storage is read again, so the page knows where it stands.</summary>
    public async Task EndAsync()
    {
        _acting--;
        _generation++;
        await CheckAsync();
    }

    /// <summary>
    /// Reads the campaign's newest save and its approval from storage now. When a read is already under
    /// way, it reads once more after that one and this waits for both, so a change signalled meanwhile,
    /// or the page's own action just done, is never answered by a read begun before it. A failed read
    /// keeps what was known and is logged; the next one tries again.
    /// </summary>
    public Task CheckAsync()
    {
        if (_running is { IsCompleted: false } running)
        {
            _again = true;
            return running;
        }
        return _running = ReadAsync();
    }

    private async Task ReadAsync()
    {
        do
        {
            _again = false;
            if (_session is not { Latest: not null } session || IsDisposed) return;
            var generation = _generation;
            try
            {
                var newest = await session.NewestAsync();
                if (generation == _generation && ReferenceEquals(session, _session))
                {
                    _observed = newest;
                    Observations++;
                }
                else
                {
                    // Begun before the page's own action or a new session: what it read is not news.
                    _again = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not check campaign {Campaign} for changes; the next check tries again.", session.Id);
            }
        }
        while (_again && !IsDisposed);
    }

    // From the signal or the timer, on their own threads: the read happens on the page's circuit.
    private void Changed()
    {
        if (IsDisposed) return;
        _ = OnCircuitAsync();
    }

    private async Task OnCircuitAsync()
    {
        try
        {
            await _onCircuit(CheckAsync);
        }
        catch (Exception ex) when (IsDisposed || ex is ObjectDisposedException or OperationCanceledException)
        {
            // The page closed meanwhile: nobody is left to tell.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not check an open campaign for changes; the next check tries again.");
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _listening?.Dispose();
    }
}

/// <summary>Settings for the open campaign page's look at storage, section <c>CampaignWatch</c>.</summary>
public sealed class CampaignWatchOptions
{
    public const string Section = "CampaignWatch";

    /// <summary>The longest the owner allows between looks (owner, 2026-10-11: "no more than one or two minutes").</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(2);

    /// <summary>The shortest allowed: each look is two storage reads per open page.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(10);

    /// <summary>How often an open campaign page reads storage for changes. A minute unless set.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Why these settings cannot run, or null when they can.</summary>
    public string? Problem() =>
        Interval < Shortest || Interval > Longest
            ? "CampaignWatch:Interval must lie between 00:00:10 and 00:02:00, such as 00:01:00."
            : null;
}
