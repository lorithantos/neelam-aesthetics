using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Components.Pages;
using Neelam.Web.Components.Shared;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The record of each start (owner, 2026-10-10: "We can add the measurement to help make certain there
/// are no outages"): what it keeps, that the first request is taken once however many arrive together,
/// that the warm-up's own requests never count, that keeping it never fails or slows a request, and
/// where the operator reads it.
/// </summary>
public class StartRecordTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    internal const string Stamp = "Build 7 (local) | Commit 0123456789abcdef | Branch main | 2026-10-03T11:58:00Z";

    // ---- What a start keeps ----

    [Fact]
    public async Task A_start_keeps_its_build_first_request_and_each_page_s_first_visit()
    {
        await using var app = new StartsApp { Build = Stamp };
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/how-it-works")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/how-it-works")).StatusCode);

        var start = app.Services.GetRequiredService<StartLog>();
        var record = await Eventually(app.StartRecords, r => r.FirstVisits.Count == 2 && r.Warmup is not null);

        Assert.Equal(start.Id, record.Id);
        Assert.Equal(app.Clock.GetUtcNow(), record.StartedAt);
        Assert.Equal(Stamp, record.Build);
        Assert.Equal(StartCause.Unknown, record.Cause);
        // The first request was /healthz: an endpoint, not a page, so it is no page's first visit.
        Assert.Equal(("/healthz", 200), (record.FirstRequest!.Route, record.FirstRequest.Status));
        Assert.Equal(["/how-it-works", "/templates"], record.FirstVisits.Select(v => v.Route).Order(StringComparer.Ordinal).ToArray());
        Assert.All(record.FirstVisits, v => Assert.Equal(200, v.Status));
        // The tests' apps run without a warm-up: off, no steps.
        Assert.Equal(new WarmupOutcome(0, 0, 0, null, 0), record.Warmup);
        Assert.Equal("Off", StartsView.Warmup(record.Warmup));

        // The same as log lines, routes as declared.
        var lines = app.Log.Entries.Select(e => e.Message).ToList();
        Assert.Contains(lines, l => l.StartsWith($"Start {start.Id} at ", StringComparison.Ordinal) && l.EndsWith($"build {Stamp}.", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith($"Start {start.Id}: first request /healthz answered 200 in ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith($"Start {start.Id}: first visit /templates answered 200 in ", StringComparison.Ordinal));
        Assert.Contains($"Start {start.Id}: cause unknown.", lines);
    }

    // A page's address carries an id; the record keeps the route as declared, never the address.
    [Fact]
    public async Task A_page_is_kept_by_its_route_never_its_address()
    {
        await using var app = new StartsApp();
        var id = Guid.Parse("cccccccc-1234-0000-0000-000000000009");
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/campaigns/{id}");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, response.StatusCode.ToString());

        var record = await Eventually(app.StartRecords, r => r.FirstVisits.Count == 1);
        Assert.Equal("/campaigns/{Id:guid}", record.FirstVisits.Single().Route);
        Assert.Equal("/campaigns/{Id:guid}", record.FirstRequest!.Route);
        Assert.All(app.Log.Entries, e => Assert.DoesNotContain(id.ToString(), e.Message, StringComparison.OrdinalIgnoreCase));
    }

    // A deploy writes a new build stamp: a start whose stamp differs from the start before is a new build.
    [Fact]
    public async Task A_new_build_is_told_from_the_start_before()
    {
        await using var app = new StartsApp { Build = Stamp };
        await app.StartRecords.SaveAsync(Earlier("20261003T110000000Z-0001", "Build 6 (local) | Commit fedcba | Branch main | x"));
        await app.CreateClient().GetAsync("/healthz");

        var record = await Eventually(app.StartRecords, r => r.FirstRequest is not null && r.Id != "20261003T110000000Z-0001");
        Assert.Equal(StartCause.NewBuild, record.Cause);
    }

    [Theory]
    [InlineData("Build 7", "Build 6", StartCause.NewBuild)]
    [InlineData("Build 7", "Build 7", StartCause.Unknown)]
    [InlineData(null, "Build 7", StartCause.Unknown)]
    [InlineData("Build 7", null, StartCause.Unknown)]
    public void What_started_it(string? now, string? before, string expected) =>
        Assert.Equal(expected, StartCause.From(now, Earlier("20261003T110000000Z-0001", before)));

    [Fact]
    public void Nothing_before_is_unknown() => Assert.Equal(StartCause.Unknown, StartCause.From("Build 7", null));

    // ---- The first request, once ----

    // Many requests arriving at once: exactly one is the first, and each page has exactly one first visit.
    [Fact]
    public void Exactly_one_of_many_simultaneous_requests_is_the_first()
    {
        // One thread per core, released together on a spin rather than a wait, round after round, so
        // their calls land within nanoseconds of each other: the window a check-then-set would leave.
        var threads = Math.Clamp(Environment.ProcessorCount, 2, 16);
        const int rounds = 3000;
        var starts = Enumerable.Range(0, rounds)
            .Select(_ => new StartLog(new ManualClock(DateTimeOffset.UnixEpoch), new ConfigurationBuilder().Build(), new WarmupState()))
            .ToArray();
        var firsts = new int[rounds];
        var visits = new int[rounds];
        var go = -1;
        using var finished = new CountdownEvent(threads);
        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                while (Volatile.Read(ref go) < round) Thread.SpinWait(1);
                var start = starts[round];
                if (start.TryRecordFirstRequest(new FirstRequest(DateTimeOffset.UnixEpoch, "/r" + i, i, 200))) Interlocked.Increment(ref firsts[round]);
                if (start.TryRecordFirstVisit(new PageVisit("/campaigns", DateTimeOffset.UnixEpoch, i, 200))) Interlocked.Increment(ref visits[round]);
            }
            finished.Signal();
        }) { IsBackground = true }).ToList();
        workers.ForEach(w => w.Start());
        for (var round = 0; round < rounds; round++)
        {
            Volatile.Write(ref go, round);
            Thread.SpinWait(200);
        }
        Assert.True(finished.Wait(Patience));

        for (var round = 0; round < rounds; round++)
        {
            Assert.Equal(1, firsts[round]);
            Assert.Equal(1, visits[round]);
            var events = Drain(starts[round]);
            Assert.Single(events.OfType<FirstRequestSeen>());
            Assert.Single(events.OfType<FirstVisitSeen>());
            Assert.Equal(events.OfType<FirstRequestSeen>().Single().Request, starts[round].Snapshot(1).FirstRequest);
        }
    }

    [Fact]
    public async Task Through_the_app_parallel_requests_make_one_first_request()
    {
        await using var app = new StartsApp();
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var answers = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => client.GetAsync("/how-it-works")));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        await Eventually(app.StartRecords, r => r.FirstRequest is not null && r.FirstVisits.Count == 1);
        Assert.Single(app.Log.Entries, e => e.Message.Contains(": first request ", StringComparison.Ordinal));
        Assert.Single(app.Log.Entries, e => e.Message.Contains(": first visit /how-it-works ", StringComparison.Ordinal));
    }

    // ---- The warm-up's own requests ----

    // The warm-up requests every page before anyone does; none of those is a visitor's first request or
    // a page's first visit. The first request is the first one from outside.
    [Fact]
    public async Task The_warm_up_s_own_requests_never_count()
    {
        await using var app = new WarmupApp();
        await WarmupApp.SeedAsync(app);
        var warmup = app.Services.GetRequiredService<WarmupState>();
        await warmup.Completion.WaitAsync(Patience);
        Assert.Contains(warmup.Steps, s => s.Name == "GET /campaigns" && s.Outcome == "200");

        var start = app.Services.GetRequiredService<StartLog>();
        var before = start.Snapshot(0);
        Assert.Null(before.FirstRequest);
        Assert.Empty(before.FirstVisits);
        Assert.Equal(warmup.Steps.Count, before.Warmup!.Steps);
        Assert.Equal(warmup.TotalMs, before.Warmup.TotalMs);

        await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/templates");
        var after = start.Snapshot(0);
        Assert.Equal("/templates", after.FirstRequest!.Route);
        Assert.Equal(["/templates"], after.FirstVisits.Select(v => v.Route).ToArray());
    }

    // Only this process's own value marks a request as the warm-up's; the header's name alone, a wrong
    // value or two values do not.
    [Fact]
    public void Only_the_process_s_own_marker_marks_a_warm_up_request()
    {
        var marker = new WarmupMarker();
        Assert.Matches("^[0-9A-F]{32}$", marker.Value);
        Assert.NotEqual(marker.Value, new WarmupMarker().Value);

        Assert.True(marker.IsOn(With(marker.Value)));
        Assert.False(marker.IsOn(With()));
        Assert.False(marker.IsOn(With("")));
        Assert.False(marker.IsOn(With(new WarmupMarker().Value)));
        Assert.False(marker.IsOn(With(marker.Value, marker.Value)));
        Assert.False(marker.IsOn(With(marker.Value.ToLowerInvariant())));

        static HttpRequest With(params string[] values)
        {
            var context = new DefaultHttpContext();
            if (values.Length > 0) context.Request.Headers[WarmupMarker.Header] = values;
            return context.Request;
        }
    }

    // ---- Never in a request's way ----

    // Storage refusing every write and read: each request is answered as usual, and the failure is logged.
    [Fact]
    public async Task A_record_that_cannot_be_kept_never_fails_a_request()
    {
        await using var app = new StartsApp { Store = new FailingStartRecords() };
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/how-it-works")).StatusCode);

        await EventuallyLogged(app, "the record was not saved: RequestFailedException 503 ServerBusy.");
        Assert.Contains(app.Log.Entries, e => e.Message.EndsWith("the start before could not be read: RequestFailedException 503 ServerBusy.", StringComparison.Ordinal));
        // The writer is still running: a later page's first visit is still logged.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/templates")).StatusCode);
        await EventuallyLogged(app, "first visit /templates answered 200 in ");
    }

    // Storage that never answers: requests are answered regardless, since the writer is off their path.
    [Fact]
    public async Task A_record_that_hangs_never_slows_a_request()
    {
        var hanging = new HangingStartRecords();
        await using var app = new StartsApp { Store = hanging };
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/how-it-works")).StatusCode);
        await hanging.Reached.Task.WaitAsync(Patience);
        var answered = await client.GetAsync("/templates").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    // ---- What is kept, and for how long ----

    [Fact]
    public async Task Only_the_newest_version_of_the_newest_starts_is_kept()
    {
        var settings = new InMemoryBlobBackend();
        var store = new StartRecordStore(settings, keep: 3);
        for (var i = 1; i <= 5; i++)
        {
            var id = $"20261003T1{i}0000000Z-000{i}";
            await store.SaveAsync(Earlier(id, "Build " + i) with { Version = 1 });
            await store.SaveAsync(Earlier(id, "Build " + i) with { Version = 2, Cause = StartCause.NewBuild });
        }

        Assert.Equal(
            ["_starts/20261003T130000000Z-0003/000002.json", "_starts/20261003T140000000Z-0004/000002.json", "_starts/20261003T150000000Z-0005/000002.json"],
            settings.Blobs.Keys.ToArray());
        var recent = await store.RecentAsync(10);
        Assert.Equal(["Build 5", "Build 4", "Build 3"], recent.Select(r => r.Build).ToArray());
        Assert.All(recent, r => Assert.Equal(StartCause.NewBuild, r.Cause));
        Assert.Equal(["Build 5"], (await store.RecentAsync(1)).Select(r => r.Build).ToArray());
    }

    [Fact]
    public async Task An_unreadable_record_is_left_out()
    {
        var settings = new InMemoryBlobBackend();
        var store = new StartRecordStore(settings);
        await store.SaveAsync(Earlier("20261003T110000000Z-0001", "Build 1"));
        settings.Put("_starts/20261003T120000000Z-0002/000001.json", "{ not json");

        Assert.Equal(["Build 1"], (await store.RecentAsync(10)).Select(r => r.Build).ToArray());
    }

    // In the settings container beside the operator's other data, never in a client's container.
    [Fact]
    public async Task Starts_are_kept_in_the_settings_container()
    {
        var containers = new InMemoryContainers();
        var stores = new ClientStores(containers.For, new ManualClock(DateTimeOffset.UnixEpoch), TimeSpan.FromDays(1),
            new InMemoryApprovals(), TestRecords.Unwatched);
        await stores.Starts().SaveAsync(Earlier("20261003T110000000Z-0001", "Build 1"));

        Assert.Equal(["settings"], containers.Names.ToArray());
        Assert.Equal(["_starts/20261003T110000000Z-0001/000001.json"], containers.For("settings").Blobs.Keys.ToArray());
    }

    // ---- Helpers ----

    internal static StartRecord Earlier(string id, string? build) =>
        new(id, new DateTimeOffset(2026, 10, 3, 11, 0, 0, TimeSpan.Zero), build, StartCause.Unknown, 1, null, null, []);

    private static List<object> Drain(StartLog start)
    {
        var events = new List<object>();
        while (start.Events.TryRead(out var happened)) events.Add(happened);
        return events;
    }

    internal static async Task<StartRecord> Eventually(IStartRecords store, Func<StartRecord, bool> done)
    {
        var until = DateTime.UtcNow + Patience;
        while (true)
        {
            if ((await store.RecentAsync(1)).FirstOrDefault() is { } record && done(record)) return record;
            if (DateTime.UtcNow > until) throw new TimeoutException("The start's record never got there.");
            await Task.Delay(20);
        }
    }

    private static async Task EventuallyLogged(StartsApp app, string text)
    {
        var until = DateTime.UtcNow + Patience;
        while (!app.Log.Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Never logged: {text}");
            await Task.Delay(20);
        }
    }
}

/// <summary>The demo with the record of starts' log captured, an optional build stamp, and an optional store in place of the in-memory one.</summary>
public sealed class StartsApp : InMemoryApp
{
    internal string? Build { get; init; }

    internal IStartRecords? Store { get; init; }

    internal ListLogger<StartRecordWriter> Log { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(PrototypeCallerSource.ClientSetting, DemoApp.Client.ToString());
        if (Build is not null) builder.UseSetting(BuildStamp.Setting, Build);
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            if (Store is not null) services.RemoveAll<IStartRecords>().AddSingleton(Store);
            services.AddSingleton<ILogger<StartRecordWriter>>(Log);
        });
    }

    protected override void ConfigureAccess(IServiceCollection services)
    {
    }
}

/// <summary>The record of starts in memory, safe to write from the writer while a test reads it.</summary>
internal sealed class InMemoryStartRecords : IStartRecords
{
    private readonly Dictionary<string, StartRecord> _starts = new(StringComparer.Ordinal);

    public Task SaveAsync(StartRecord record, CancellationToken ct = default)
    {
        lock (_starts)
            if (!_starts.TryGetValue(record.Id, out var kept) || kept.Version < record.Version) _starts[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StartRecord>> RecentAsync(int count, CancellationToken ct = default)
    {
        lock (_starts)
            return Task.FromResult<IReadOnlyList<StartRecord>>(
                _starts.Values.OrderByDescending(s => s.Id, StringComparer.Ordinal).Take(count).ToList());
    }
}

internal sealed class FailingStartRecords : IStartRecords
{
    public Task SaveAsync(StartRecord record, CancellationToken ct = default) => throw StorageFailure.Busy();

    public Task<IReadOnlyList<StartRecord>> RecentAsync(int count, CancellationToken ct = default) => throw StorageFailure.Busy();
}

internal sealed class HangingStartRecords : IStartRecords
{
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task SaveAsync(StartRecord record, CancellationToken ct = default)
    {
        Reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
    }

    public async Task<IReadOnlyList<StartRecord>> RecentAsync(int count, CancellationToken ct = default)
    {
        Reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return [];
    }
}

/// <summary>
/// The Starts page (/admin/starts), the operator's, through the enforcing app: the last starts as a
/// table, newest first, with this start as it stands and a mark on any first visit of a second or more.
/// On the demo it is a 404 like every operator page (OperatorPagesInDemoTests).
/// </summary>
public class StartsPageTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private const string Quick = "20261003T100000000Z-0a0a";
    private const string Slow = "20261003T110000000Z-0b0b";

    private async Task<(HttpStatusCode Status, string Page)> Get(params string[] roles)
    {
        var at = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        await app.StartRecords.SaveAsync(new StartRecord(Quick, at, StartRecordTests.Stamp, StartCause.Unknown, 3,
            new WarmupOutcome(5400, 13, 13, "GET /campaigns", 1600),
            new FirstRequest(at.AddSeconds(2), "/warmup", 9000, 200),
            [new PageVisit("/", at.AddSeconds(20), 175, 200), new PageVisit("/campaigns", at.AddSeconds(25), 999, 200)]));
        await app.StartRecords.SaveAsync(new StartRecord(Slow, at.AddHours(1), "Build 8 (local) | Commit 89abcdef01234567 | Branch main | x",
            StartCause.NewBuild, 2, null, null,
            [new PageVisit("/campaigns", at.AddHours(1).AddSeconds(9), 450, 200), new PageVisit("/campaigns/{Id:guid}", at.AddHours(1).AddSeconds(12), 1000, 200)]));

        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles);
        var response = await client.GetAsync("/admin/starts");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nobody_signed_in_is_challenged() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get()).Status);

    [Fact]
    public async Task A_member_without_the_operator_role_is_refused() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get([.. Features.All.Where(f => f != Features.Operator)])).Status);

    [Fact]
    public async Task The_operator_sees_each_start_newest_first()
    {
        var (status, page) = await Get(Features.Operator);
        Assert.Equal(HttpStatusCode.OK, status);

        var current = app.Services.GetRequiredService<StartLog>().Id;
        var rows = RenderedPage.NamesStartingWith(page, "start-when-");
        Assert.Equal([current, Slow, Quick], rows.ToArray());
        Assert.EndsWith("(this one)", RenderedPage.Named(page, $"start-when-{current}"));

        Assert.Equal("3 Oct 2026, 10:00:00", RenderedPage.Named(page, $"start-when-{Quick}"));
        Assert.Equal("Build 7 · 0123456", RenderedPage.Named(page, $"start-build-{Quick}"));
        Assert.Equal("Restart (cause not known)", RenderedPage.Named(page, $"start-cause-{Quick}"));
        Assert.Equal("New build", RenderedPage.Named(page, $"start-cause-{Slow}"));
        Assert.Equal("5.4 s, 13 of 13 answered; slowest GET /campaigns, 1.6 s", RenderedPage.Named(page, $"start-warmup-{Quick}"));
        Assert.Equal("Not finished", RenderedPage.Named(page, $"start-warmup-{Slow}"));
        Assert.Equal("/warmup, 200, 9.0 s (2.0 s after the start)", RenderedPage.Named(page, $"start-first-request-{Quick}"));
        Assert.Equal("None yet", RenderedPage.Named(page, $"start-first-request-{Slow}"));
        Assert.Equal("/, 175 ms", RenderedPage.Named(page, $"start-first-visit-{Quick}"));
        Assert.Equal("/campaigns, 450 ms", RenderedPage.Named(page, $"start-first-visit-{Slow}"));
    }

    // "a second or more feels sluggish and unprofessional": a slowest first visit of 1,000 ms is
    // marked, one of 999 ms is not.
    [Fact]
    public async Task A_first_visit_of_a_second_or_more_is_marked()
    {
        var (_, page) = await Get(Features.Operator);

        Assert.Equal("/campaigns/{Id:guid}, 1.0 s Over 1 second", RenderedPage.Named(page, $"start-slowest-{Slow}"));
        Assert.Equal("Over 1 second", RenderedPage.Named(page, $"start-sluggish-{Slow}"));
        Assert.Equal("/campaigns, 999 ms", RenderedPage.Named(page, $"start-slowest-{Quick}"));
        Assert.Equal(0, RenderedPage.Count(page, $"start-sluggish-{Quick}"));
    }

    [Fact]
    public async Task The_admin_header_links_to_it()
    {
        var (_, page) = await Get(Features.Operator);
        Assert.Contains("admin/starts", RenderedPage.Links(page));
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1.0 s")]
    [InlineData(11240, "11.2 s")]
    public void Durations_read_as_ms_under_a_second(long ms, string expected) => Assert.Equal(expected, StartsView.Duration(ms));

    [Theory]
    [InlineData(null, "none")]
    [InlineData("Build 73 (local) | Commit 6c16f0f1234 | Branch main | 2026-10-10T05:25:00Z", "Build 73 · 6c16f0f")]
    [InlineData("Build 73", "Build 73")]
    [InlineData("something else", "something else")]
    public void A_build_stamp_reads_as_build_and_commit(string? stamp, string expected) => Assert.Equal(expected, StartsView.Build(stamp));
}
