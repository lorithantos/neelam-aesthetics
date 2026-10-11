using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Azure;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Shared;

namespace Neelam.Web;

/// <summary>
/// This process's start, as it happens: when it started, which build, how the warm-up went, the first
/// request that was not the warm-up's own, and each page's first visit (owner, 2026-10-10: "We can add
/// the measurement to help make certain there are no outages"). Shared by every request, so each thing
/// is taken once and atomically: the first request by <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>,
/// a page's first visit by <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/>. Each change is queued
/// as an event for <see cref="StartRecordWriter"/>, which logs it and keeps the record, off the request path.
/// </summary>
public sealed class StartLog
{
    private readonly Channel<StartEvent> _events = Channel.CreateUnbounded<StartEvent>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly ConcurrentDictionary<string, PageVisit> _visits = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private FirstRequest? _first;
    private WarmupOutcome? _warmup;
    private string? _cause;
    private int _heldShown;
    private long _firstHeldTicks;
    private long _readyTicks;
    private readonly TaskCompletionSource<DateTimeOffset?> _lastAliveBefore =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartLog(TimeProvider clock, IConfiguration configuration, WarmupState warmup)
    {
        _clock = clock;
        StartedAt = clock.GetUtcNow();
        Id = StartedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
            + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
        Build = configuration[BuildStamp.Setting] is { } stamp && !string.IsNullOrWhiteSpace(stamp) ? stamp.Trim() : null;
        _events.Writer.TryWrite(new StartBegan());
        warmup.Completion.ContinueWith(_ =>
            {
                Interlocked.CompareExchange(ref _readyTicks, clock.GetUtcNow().UtcTicks, 0);
                RecordWarmup(Outcome(warmup));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public string Id { get; }

    public DateTimeOffset StartedAt { get; }

    public string? Build { get; }

    /// <summary>What started it, once the writer has worked it out from the start before; "unknown" until then.</summary>
    public string Cause => Volatile.Read(ref _cause) ?? StartCause.Unknown;

    /// <summary>The events, for the writer alone.</summary>
    internal ChannelReader<StartEvent> Events => _events.Reader;

    internal void SetCause(string cause) => Volatile.Write(ref _cause, cause);

    /// <summary>
    /// The last heartbeat before this start, once <see cref="Heartbeat"/> has read it, before its own
    /// first beat: null when there was none or it could not be read.
    /// </summary>
    public Task<DateTimeOffset?> LastAliveBefore => _lastAliveBefore.Task;

    /// <summary>Set once, by the heartbeat, before it first beats; later calls change nothing.</summary>
    internal void SetLastAliveBefore(DateTimeOffset? at) => _lastAliveBefore.TrySetResult(at);

    /// <summary>From the last heartbeat before this start to the start, never below zero; null while unknown.</summary>
    public long? DownForMs =>
        _lastAliveBefore.Task.IsCompletedSuccessfully && _lastAliveBefore.Task.Result is { } at
            ? Math.Max(0, (long)(StartedAt - at).TotalMilliseconds)
            : null;

    /// <summary>Takes this request as the first one, unless another already was. True for exactly one request per start.</summary>
    public bool TryRecordFirstRequest(FirstRequest request)
    {
        if (Interlocked.CompareExchange(ref _first, request, null) is not null) return false;
        _events.Writer.TryWrite(new FirstRequestSeen(request));
        return true;
    }

    /// <summary>Takes this visit as its page's first, unless the page already had one. True once per page per start.</summary>
    public bool TryRecordFirstVisit(PageVisit visit)
    {
        if (!_visits.TryAdd(visit.Route, visit)) return false;
        _events.Writer.TryWrite(new FirstVisitSeen(visit));
        return true;
    }

    /// <summary>
    /// Notes a request the getting-ready page answered (<see cref="WarmupGate"/>), with when it arrived:
    /// the count by <see cref="Interlocked.Increment(ref int)"/>, the earliest arrival by a compare-and-swap loop.
    /// </summary>
    public void HeldBack(DateTimeOffset arrived)
    {
        var ticks = arrived.UtcTicks;
        long seen;
        do
        {
            seen = Interlocked.Read(ref _firstHeldTicks);
            if (seen != 0 && seen <= ticks) break;
        }
        while (Interlocked.CompareExchange(ref _firstHeldTicks, ticks, seen) != seen);
        var shown = Interlocked.Increment(ref _heldShown);
        _events.Writer.TryWrite(new GettingReadyShown(shown, arrived));
    }

    /// <summary>The held requests so far: how many, the earliest, and its wait until the warm-up finished (or until now).</summary>
    private HeldRequests? Held()
    {
        var shown = Volatile.Read(ref _heldShown);
        if (shown == 0) return null;
        var first = new DateTimeOffset(Interlocked.Read(ref _firstHeldTicks), TimeSpan.Zero);
        var ready = Interlocked.Read(ref _readyTicks);
        var until = ready != 0 ? new DateTimeOffset(ready, TimeSpan.Zero) : _clock.GetUtcNow();
        return new HeldRequests(shown, first, Math.Max(0, (long)(until - first).TotalMilliseconds));
    }

    internal void RecordWarmup(WarmupOutcome outcome)
    {
        if (Interlocked.CompareExchange(ref _warmup, outcome, null) is null)
            _events.Writer.TryWrite(new WarmupEnded(outcome));
    }

    /// <summary>The record as it stands, at the given version.</summary>
    public StartRecord Snapshot(int version) => new(
        Id, StartedAt, Build, Cause, version, Volatile.Read(ref _warmup), Volatile.Read(ref _first),
        _visits.Values.OrderBy(v => v.At).ThenBy(v => v.Route, StringComparer.Ordinal).ToList(), Held(),
        DownForMs is null ? null : _lastAliveBefore.Task.Result, DownForMs);

    /// <summary>How long after the start this moment is.</summary>
    public long MsSinceStart(DateTimeOffset at) => (long)(at - StartedAt).TotalMilliseconds;

    internal TimeProvider Clock => _clock;

    /// <summary>The warm-up's outcome from its state: null-safe for a warm-up that was off (no steps, no time).</summary>
    internal static WarmupOutcome Outcome(WarmupState warmup)
    {
        var steps = warmup.Steps;
        var slowest = steps.OrderByDescending(s => s.Ms).FirstOrDefault();
        return new WarmupOutcome(warmup.TotalMs ?? 0, steps.Count(s => s.Ok), steps.Count, slowest?.Name, slowest?.Ms ?? 0);
    }
}

/// <summary>What started the app, as far as it can tell.</summary>
public static class StartCause
{
    /// <summary>The build stamp differs from the start before: a deploy.</summary>
    public const string NewBuild = "new build";

    /// <summary>The same build, back within two minutes of the last heartbeat: a setting changed or a platform recycle.</summary>
    public const string Restart = "restart";

    /// <summary>The same build, two minutes or more after the last heartbeat: the app was not running.</summary>
    public const string Down = "down";

    /// <summary>The same build, or nothing to compare, and no heartbeat to go by.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// "new build" when both starts carry a stamp and they differ; otherwise from the gap since the last
    /// heartbeat: "restart" under <see cref="StartRecord.RestartMs"/>, "down" from it, "unknown" with no gap.
    /// </summary>
    public static string From(string? build, StartRecord? before, long? downForMs) =>
        build is not null && before?.Build is { } earlier && !string.Equals(build, earlier, StringComparison.Ordinal) ? NewBuild
        : downForMs is not { } gap ? Unknown
        : gap < StartRecord.RestartMs ? Restart
        : Down;
}

internal abstract record StartEvent;

internal sealed record StartBegan : StartEvent;

internal sealed record WarmupEnded(WarmupOutcome Outcome) : StartEvent;

internal sealed record FirstRequestSeen(FirstRequest Request) : StartEvent;

internal sealed record FirstVisitSeen(PageVisit Visit) : StartEvent;

internal sealed record GettingReadyShown(int Shown, DateTimeOffset At) : StartEvent;

/// <summary>
/// Notes every request that is not the warm-up's own (<see cref="WarmupMarker"/>) against this start:
/// the first one, and each page's first GET. It sits first in the pipeline, so its time is the whole
/// request. What it notes is the route as declared, never the address: <c>/campaigns/{Id:guid}</c>, not
/// a campaign's id. It only adds to memory and queues an event; anything that goes wrong in it is logged
/// and swallowed, so it can never slow or fail the request.
/// </summary>
public static class StartRecording
{
    /// <summary>What a request the getting-ready page answered is noted as, having reached no route.</summary>
    public const string GettingReadyRoute = "(getting-ready page)";

    /// <summary>What a request that reached no route at all is noted as.</summary>
    public const string NoRoute = "(no route)";

    public static IApplicationBuilder UseStartRecording(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var services = context.RequestServices;
            if (services.GetRequiredService<WarmupMarker>().IsOn(context.Request))
            {
                await next(context);
                return;
            }
            var start = services.GetRequiredService<StartLog>();
            var arrived = start.Clock.GetUtcNow();
            var watch = Stopwatch.StartNew();
            var failed = false;
            try
            {
                await next(context);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                try
                {
                    Note(start, context, arrived, watch.ElapsedMilliseconds, failed);
                }
                catch (Exception ex)
                {
                    services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StartRecording))
                        .LogWarning("Start record: a request could not be noted: {Error}.", ex.GetType().Name);
                }
            }
        });

    internal static void Note(StartLog start, HttpContext context, DateTimeOffset arrived, long ms, bool failed)
    {
        var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
        var endpoint = context.GetEndpoint();
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText is { } raw ? "/" + raw.TrimStart('/')
            : context.Items.ContainsKey(GettingReadyRoute) ? GettingReadyRoute
            : NoRoute;
        start.TryRecordFirstRequest(new FirstRequest(arrived, route, ms, status));
        if (endpoint?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null
            && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            start.TryRecordFirstVisit(new PageVisit(route, arrived, ms, status));
    }
}

/// <summary>Settings for the record of starts, section <c>Starts</c>.</summary>
public sealed class StartRecordOptions
{
    public const string Section = "Starts";

    /// <summary>How many starts are kept.</summary>
    public int Keep { get; set; } = StartRecordStore.DefaultKeep;

    /// <summary>How long the writer gathers events before it writes, so a burst of first visits is one write.</summary>
    public TimeSpan Coalesce { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Logs each event of this start as a structured line (Application Insights) and keeps the start's
/// record in <see cref="IStartRecords"/>, off every request: it reads the queue <see cref="StartLog"/>
/// fills. A write that fails is logged, by the failure's type only, and the next event writes again;
/// nothing it does reaches a request. Telemetry carries the build stamp, routes as declared, status
/// codes and times: never a client's name, an id from an address, or anything she wrote.
/// </summary>
public sealed class StartRecordWriter(
    StartLog start, IStartRecords store, IOptions<StartRecordOptions> options, ILogger<StartRecordWriter> log) : BackgroundService
{
    /// <summary>How long the writer waits for the heartbeat to have read the last one before this start.</summary>
    internal static readonly TimeSpan LastAliveWait = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var version = 0;
        var causeKnown = false;
        try
        {
            while (await start.Events.WaitToReadAsync(stoppingToken))
            {
                if (options.Value.Coalesce > TimeSpan.Zero) await Task.Delay(options.Value.Coalesce, stoppingToken);
                while (start.Events.TryRead(out var happened)) Report(happened);
                if (!causeKnown)
                {
                    start.SetCause(await CauseAsync(stoppingToken));
                    causeKnown = true;
                    var gap = start.Snapshot(0);
                    log.LogInformation("Start {StartId}: cause {Cause}; last heartbeat before it {DownFrom}, {DownForMs} ms before the start.",
                        start.Id, start.Cause,
                        gap.DownFrom?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) ?? "none", gap.DownForMs?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
                }
                version++;
                try
                {
                    await store.SaveAsync(start.Snapshot(version), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning("Start {StartId}: the record was not saved: {Error}.", start.Id, Describe(ex));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<string> CauseAsync(CancellationToken ct)
    {
        // The heartbeat reads the last one before this start before it first beats; give it a while.
        try
        {
            await start.LastAliveBefore.WaitAsync(LastAliveWait, ct);
        }
        catch (TimeoutException)
        {
            log.LogWarning("Start {StartId}: the last heartbeat before it was not read in time; the gap is not known.", start.Id);
        }
        try
        {
            var before = (await store.RecentAsync(2, ct)).FirstOrDefault(r => r.Id != start.Id);
            return StartCause.From(start.Build, before, start.DownForMs);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Start {StartId}: the start before could not be read: {Error}.", start.Id, Describe(ex));
            return StartCause.Unknown;
        }
    }

    private void Report(StartEvent happened)
    {
        switch (happened)
        {
            case StartBegan:
                log.LogInformation("Start {StartId} at {StartedAt}: build {Build}.",
                    start.Id, start.StartedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), start.Build ?? "none");
                break;
            case WarmupEnded { Outcome: var w }:
                log.LogInformation("Start {StartId}: warm-up {WarmupMs} ms, {Answered} of {Steps} steps answered, slowest {SlowestStep} in {SlowestMs} ms.",
                    start.Id, w.TotalMs, w.Answered, w.Steps, w.SlowestStep ?? "none", w.SlowestMs);
                break;
            case FirstRequestSeen { Request: var r }:
                log.LogInformation("Start {StartId}: first request {Route} answered {Status} in {Ms} ms, {AfterStartMs} ms after the start.",
                    start.Id, r.Route, r.Status, r.Ms, start.MsSinceStart(r.At));
                break;
            case FirstVisitSeen { Visit: var v }:
                if (v.Ms >= StartRecord.SluggishMs)
                    log.LogWarning("Start {StartId}: first visit {Route} answered {Status} in {Ms} ms, a second or more.",
                        start.Id, v.Route, v.Status, v.Ms);
                else
                    log.LogInformation("Start {StartId}: first visit {Route} answered {Status} in {Ms} ms.",
                        start.Id, v.Route, v.Status, v.Ms);
                break;
            case GettingReadyShown { Shown: var shown, At: var at }:
                log.LogInformation("Start {StartId}: the getting-ready page was shown ({Shown} so far), {AfterStartMs} ms after the start.",
                    start.Id, shown, start.MsSinceStart(at));
                break;
        }
    }

    private static string Describe(Exception ex) => ex is RequestFailedException storage
        ? $"{ex.GetType().Name} {storage.Status} {storage.ErrorCode}"
        : ex.GetType().Name;
}
