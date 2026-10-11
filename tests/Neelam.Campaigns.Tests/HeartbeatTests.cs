using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The heartbeat (owner, 2026-10-10: "yes, heartbeat in and drop the paid test"): about once a minute
/// the app notes it is running; each start first reads the last note, so its record says how long the
/// app was down before it. Off every request, failures logged and swallowed, stopped with the app.
/// </summary>
public class HeartbeatTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static StartLog StartAt(TimeProvider clock) => new(clock, new ConfigurationBuilder().Build(), new WarmupState());

    private static Heartbeat Beating(StartLog start, IStartRecords store, TimeProvider clock, ListLogger<Heartbeat>? log = null) =>
        new(start, store, clock, Options.Create(new HeartbeatOptions { Interval = TimeSpan.FromMinutes(1) }), log ?? new ListLogger<Heartbeat>());

    // ---- On schedule ----

    // A beat at once, then one each interval of the app's clock, and none in between.
    [Fact]
    public async Task It_beats_once_a_minute_by_the_app_s_clock()
    {
        var clock = new SteppingClock(Noon);
        var store = new InMemoryStartRecords();
        var start = StartAt(clock);
        using var heartbeat = Beating(start, store, clock);

        await heartbeat.StartAsync(CancellationToken.None);
        await Until(() => store.Beats.Count == 1 && clock.Waiting == 1);
        Assert.Equal((Noon, start.Id), store.Beats[0]);

        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(100);
        Assert.Single(store.Beats);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => store.Beats.Count == 2 && clock.Waiting == 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Until(() => store.Beats.Count == 3);

        Assert.Equal([Noon, Noon.AddMinutes(1), Noon.AddMinutes(2)], store.Beats.Select(b => b.At).ToArray());
        await heartbeat.StopAsync(CancellationToken.None);
    }

    // ---- Read before written ----

    // The last heartbeat before this start is read before this process's first beat replaces it, and
    // the start's record says when it was and how long the app was down.
    [Fact]
    public async Task The_start_reads_the_last_heartbeat_before_its_own_first_beat()
    {
        var clock = new SteppingClock(Noon);
        var store = new InMemoryStartRecords();
        await store.BeatAsync(Noon.AddMinutes(-7), "20261003T110000000Z-0001");
        var start = StartAt(clock);
        using var heartbeat = Beating(start, store, clock);

        await heartbeat.StartAsync(CancellationToken.None);
        Assert.Equal(Noon.AddMinutes(-7), await start.LastAliveBefore.WaitAsync(Patience));
        await Until(() => store.Beats.Count == 2);

        Assert.Equal(["beat", "read", "beat"], store.Calls.ToArray());
        var record = start.Snapshot(0);
        Assert.Equal(Noon.AddMinutes(-7), record.DownFrom);
        Assert.Equal(7 * 60 * 1000, record.DownForMs);
        Assert.True(record.Outage);
        Assert.Equal(Noon, (await store.LastAliveAsync()).GetValueOrDefault());
        await heartbeat.StopAsync(CancellationToken.None);
    }

    // The first start ever has no heartbeat before it: the gap is not known.
    [Fact]
    public async Task With_no_heartbeat_before_the_gap_is_not_known()
    {
        var clock = new SteppingClock(Noon);
        var start = StartAt(clock);
        using var heartbeat = Beating(start, new InMemoryStartRecords(), clock);

        await heartbeat.StartAsync(CancellationToken.None);
        Assert.Null(await start.LastAliveBefore.WaitAsync(Patience));
        Assert.Null(start.Snapshot(0).DownFrom);
        Assert.Null(start.Snapshot(0).DownForMs);
        Assert.False(start.Snapshot(0).Outage);
        await heartbeat.StopAsync(CancellationToken.None);
    }

    // A heartbeat later than the start (another instance still running) is no gap at all.
    [Fact]
    public void A_heartbeat_after_the_start_is_no_gap()
    {
        var start = StartAt(new SteppingClock(Noon));
        start.SetLastAliveBefore(Noon.AddSeconds(20));
        Assert.Equal(0, start.Snapshot(0).DownForMs);
    }

    // Set once: a second answer changes nothing.
    [Fact]
    public void The_last_heartbeat_before_is_set_once()
    {
        var start = StartAt(new SteppingClock(Noon));
        start.SetLastAliveBefore(Noon.AddMinutes(-1));
        start.SetLastAliveBefore(Noon.AddMinutes(-9));
        Assert.Equal(60_000, start.DownForMs);
    }

    // ---- Never in the way ----

    // Storage refusing: the read and each beat are logged by type, the gap is unknown, and it keeps beating.
    [Fact]
    public async Task A_failure_is_logged_and_it_beats_again()
    {
        var clock = new SteppingClock(Noon);
        var store = new FailingStartRecords();
        var log = new ListLogger<Heartbeat>();
        var start = StartAt(clock);
        using var heartbeat = Beating(start, store, clock, log);

        await heartbeat.StartAsync(CancellationToken.None);
        Assert.Null(await start.LastAliveBefore.WaitAsync(Patience));
        await Until(() => store.BeatsTried == 1 && clock.Waiting == 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Until(() => store.BeatsTried == 2);

        var messages = log.Entries.Select(e => e.Message).ToList();
        Assert.Contains("Heartbeat: the last one before this start could not be read: RequestFailedException 503 ServerBusy.", messages);
        Assert.Contains("Heartbeat not written: RequestFailedException 503 ServerBusy.", messages);
        Assert.All(log.Entries, e => Assert.Null(e.Error));
        await heartbeat.StopAsync(CancellationToken.None);
    }

    // It stops with the app, at once, and beats no more.
    [Fact]
    public async Task It_stops_with_the_app()
    {
        var clock = new SteppingClock(Noon);
        var store = new InMemoryStartRecords();
        using var heartbeat = Beating(StartAt(clock), store, clock);
        await heartbeat.StartAsync(CancellationToken.None);
        await Until(() => store.Beats.Count == 1 && clock.Waiting == 1);

        var stopping = Stopwatch.StartNew();
        await heartbeat.StopAsync(CancellationToken.None).WaitAsync(Patience);
        Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(5), $"Stopping took {stopping.Elapsed}.");
        Assert.Equal(0, clock.Waiting);

        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.Single(store.Beats);
    }

    // The app's heartbeat never stands in a request's way: with storage that never answers, pages answer.
    [Fact]
    public async Task Requests_never_wait_on_the_heartbeat()
    {
        var hanging = new HangingStartRecords();
        await using var app = new StartsApp { Store = hanging };
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        await hanging.Reached.Task.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/how-it-works").WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        Assert.False(app.Services.GetRequiredService<StartLog>().LastAliveBefore.IsCompleted);
    }

    // In the app: the heartbeat is running, and a start after seven minutes' silence is "down", an outage.
    [Fact]
    public async Task In_the_app_a_start_after_seven_quiet_minutes_is_down()
    {
        await using var app = new StartsApp();
        await app.StartRecords.BeatAsync(app.Clock.Now.AddMinutes(-7), "20261003T115000000Z-0001");
        await app.CreateClient().GetAsync("/healthz");

        var record = await StartRecordTests.Eventually(app.StartRecords, r => r.FirstRequest is not null);
        Assert.Equal(StartCause.Down, record.Cause);
        Assert.Equal(app.Clock.Now.AddMinutes(-7), record.DownFrom);
        Assert.Equal(420_000, record.DownForMs);
        Assert.Equal(app.Clock.Now, (await app.StartRecords.LastAliveAsync()).GetValueOrDefault());
        Assert.Contains(app.Log.Entries, e => e.Message.EndsWith(": cause down; last heartbeat before it 2026-10-03T11:53:00.0000000Z, 420000 ms before the start.", StringComparison.Ordinal));
    }

    // The record's writer waits for the heartbeat to have read the last one before working out why the
    // start happened, so a slow read still gives the right answer rather than "unknown".
    [Fact]
    public async Task The_cause_waits_for_the_last_heartbeat_to_be_read()
    {
        var clock = new SteppingClock(Noon);
        var store = new InMemoryStartRecords();
        var start = StartAt(clock);
        using var writer = new StartRecordWriter(start, store,
            Options.Create(new StartRecordOptions { Coalesce = TimeSpan.Zero }), new ListLogger<StartRecordWriter>());

        await writer.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Empty(await store.RecentAsync(1));

        start.SetLastAliveBefore(Noon.AddMinutes(-7));
        var record = await StartRecordTests.Eventually(store, r => r.Id == start.Id);
        Assert.Equal(StartCause.Down, record.Cause);
        Assert.Equal(420_000, record.DownForMs);
        await writer.StopAsync(CancellationToken.None);
    }

    // ---- Where it is kept ----

    // One blob beside the starts, its metadata the last beat; never listed as a start.
    [Fact]
    public async Task The_heartbeat_is_one_blob_s_metadata_beside_the_starts()
    {
        var settings = new InMemoryBlobBackend();
        var store = new StartRecordStore(settings);
        Assert.Null(await store.LastAliveAsync());

        await store.BeatAsync(Noon, "20261003T120000000Z-0001");
        await store.BeatAsync(Noon.AddMinutes(1), "20261003T120000000Z-0001");
        await store.SaveAsync(StartRecordTests.Earlier("20261003T120000000Z-0001", "Build 1"));

        Assert.Equal(Noon.AddMinutes(1), await store.LastAliveAsync());
        Assert.Equal(["_starts/20261003T120000000Z-0001/000001.json", "_starts/heartbeat.json"], settings.Blobs.Keys.ToArray());
        Assert.Equal(new Dictionary<string, string> { ["alive"] = "2026-10-03T12:01:00.0000000Z", ["start"] = "20261003T120000000Z-0001" },
            settings.Blobs["_starts/heartbeat.json"].Metadata);
        Assert.Equal(["Build 1"], (await store.RecentAsync(10)).Select(r => r.Build).ToArray());
    }

    [Fact]
    public void The_shipped_interval_is_a_minute()
    {
        var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))!;
        Assert.Equal(TimeSpan.FromMinutes(1), TimeSpan.Parse((string)settings["Heartbeat"]!["Interval"]!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + Patience;
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Never got there.");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// A clock whose time and timers move only when told to: <see cref="Advance"/> fires every timer then
/// due, as <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> sets them.
/// </summary>
internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<SteppedTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>How many timers are set and not yet fired or disposed.</summary>
    public int Waiting
    {
        get { lock (_gate) return _timers.Count(t => t.Due is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SteppedTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<SteppedTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var timer in due) timer.Due = timer.Period is { } period ? _now + period : null;
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class SteppedTimer(SteppingClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
                if (!clock._timers.Contains(this)) clock._timers.Add(this);
            }
            return true;
        }

        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
