using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Components.Pages;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The warm-up gate (owner, 2026-10-10: "We should add middleware to do the warmup and serve up a page
/// similar to what we see on the image viewer"): until the warm-up has finished a visitor gets the
/// getting-ready page, after a short wait, with 503 and Retry-After; the warm-up's own requests, the
/// health and warm-up endpoints, static files and Blazor's addresses go through; it opens for good once
/// warm or once the budget has passed; a real startup failure shows the startup-failed page.
/// </summary>
public class WarmupGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    // A warm-up held on the clients table, so the gate stays shut until the test lets it go.
    private static async Task<WarmupApp> Warming(TimeSpan? gateWait = null, TimeSpan? budget = null)
    {
        var app = new WarmupApp { Gate = new(), GateWait = gateWait ?? Short, Budget = budget };
        await WarmupApp.SeedAsync(app);
        return app;
    }

    private static HttpClient Client(WarmupApp app) => app.CreateClient(new() { AllowAutoRedirect = false });

    // ---- Shut while warming, open once warm ----

    [Fact]
    public async Task While_warming_a_page_is_the_getting_ready_page_and_once_warm_the_page_itself()
    {
        await using var app = await Warming();
        var client = Client(app);
        app.Clock.Now += TimeSpan.FromSeconds(7);

        var held = await client.GetAsync("/campaigns");
        var page = await held.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, held.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(WarmupGate.RetryAfterSeconds), held.Headers.RetryAfter!.Delta);
        Assert.Equal("text/html; charset=utf-8", held.Content.Headers.ContentType!.ToString());
        Assert.Contains("no-store", held.Headers.CacheControl!.ToString());
        Assert.Equal("Getting ready…", RenderedPage.Named(page, "getting-ready"));
        Assert.Equal("Started 7 seconds ago.", RenderedPage.Named(page, "getting-ready-elapsed"));
        Assert.Equal("Campaign safety", RenderedPage.Named(page, "site-name"));
        // It asks again by itself as often as Retry-After says, with no script.
        Assert.Contains($"<meta http-equiv=\"refresh\" content=\"{WarmupGate.RetryAfterSeconds}\" />", page);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);

        app.Gate!.SetResult();
        await app.Services.GetRequiredService<WarmupState>().Completion.WaitAsync(Patience);
        var warm = await client.GetAsync("/campaigns");
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.Null(warm.Headers.RetryAfter);
    }

    // Nearly warm is worth the short wait: a request that arrives just before the warm-up ends is
    // answered with the page it asked for, not the getting-ready page.
    [Fact]
    public async Task A_request_waits_for_the_warm_up_up_to_the_gate_s_wait()
    {
        await using var app = await Warming(gateWait: TimeSpan.FromSeconds(20));
        var client = Client(app);

        var waiting = client.GetAsync("/how-it-works");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        app.Gate!.SetResult();

        Assert.Equal(HttpStatusCode.OK, (await waiting.WaitAsync(Patience)).StatusCode);
    }

    [Fact]
    public async Task After_the_gate_s_wait_the_answer_is_503()
    {
        await using var app = await Warming(gateWait: TimeSpan.FromMilliseconds(400));
        var watch = Stopwatch.StartNew();

        var held = await Client(app).GetAsync("/how-it-works").WaitAsync(Patience);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, held.StatusCode);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(350), $"answered after {watch.Elapsed}");
    }

    // ---- What goes through while warming ----

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/copy.js")]
    public async Task Static_files_go_through(string path)
    {
        await using var app = await Warming();
        var response = await Client(app).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The health and warm-up endpoints go through to their own waits: each is still waiting (on the
    // clients table, on the warm-up) after the gate's wait, rather than answered with the page, and
    // each answers 200 once let go.
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/warmup")]
    public async Task The_health_and_warm_up_endpoints_go_through(string path)
    {
        await using var app = await Warming();
        var pending = Client(app).GetAsync(path);
        await Task.Delay(Short * 3);
        Assert.False(pending.IsCompleted, "answered by the gate");

        app.Gate!.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await pending.WaitAsync(Patience)).StatusCode);
    }

    // The warm-up's own requests go through, or it would wait on itself; only this process's marker does.
    [Fact]
    public async Task The_warm_up_s_own_requests_go_through()
    {
        await using var app = await Warming();
        var marker = app.Services.GetRequiredService<WarmupMarker>();

        var own = new HttpRequestMessage(HttpMethod.Get, "/how-it-works");
        own.Headers.Add(WarmupMarker.Header, marker.Value);
        Assert.Equal(HttpStatusCode.OK, (await Client(app).SendAsync(own)).StatusCode);

        var forged = new HttpRequestMessage(HttpMethod.Get, "/how-it-works");
        forged.Headers.Add(WarmupMarker.Header, new WarmupMarker().Value);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Client(app).SendAsync(forged)).StatusCode);
    }

    [Theory]
    [InlineData("/healthz", true)]
    [InlineData("/warmup", true)]
    [InlineData("/app.css", true)]
    [InlineData("/app.0123abcd.css", true)]
    [InlineData("/copy.js", true)]
    [InlineData("/favicon.ico", true)]
    [InlineData("/_framework/blazor.web.js", true)]
    [InlineData("/_content/some/file", true)]
    [InlineData("/_blazor", true)]
    [InlineData("/_blazor/negotiate", true)]
    [InlineData("/", false)]
    [InlineData("/campaigns", false)]
    [InlineData("/campaigns/0a0a0a0a-0000-0000-0000-000000000001", false)]
    [InlineData("/admin/starts", false)]
    [InlineData("/healthz/more", false)]
    [InlineData("/warmup.html", false)]
    public void What_goes_through_while_warming(string path, bool passes)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        Assert.Equal(passes, WarmupGate.PassesWhileWarming(context.Request));
    }

    // ---- Never shut for good ----

    // The warm-up gives up after its budget, and that opens the gate like finishing does.
    [Fact]
    public async Task The_warm_up_giving_up_opens_the_gate()
    {
        await using var app = await Warming(gateWait: TimeSpan.FromMilliseconds(100), budget: TimeSpan.FromMilliseconds(600));
        var warmup = app.Services.GetRequiredService<WarmupState>();

        await warmup.Completion.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.OK, (await Client(app).GetAsync("/how-it-works")).StatusCode);
    }

    // However the warm-up is doing, once its budget has passed since the start the gate is open.
    [Fact]
    public async Task The_budget_passing_since_the_start_opens_the_gate()
    {
        await using var app = await Warming();
        var client = Client(app);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/how-it-works")).StatusCode);

        app.Clock.Now += TimeSpan.FromMinutes(1);

        Assert.False(app.Services.GetRequiredService<WarmupState>().IsDone);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/how-it-works")).StatusCode);
    }

    // ---- A real startup failure ----

    [Fact]
    public async Task A_startup_failure_shows_the_failed_page_with_its_reason()
    {
        await using var app = await Warming();
        var warmup = app.Services.GetRequiredService<WarmupState>();
        warmup.Fail("Storage <can't> be reached.");
        app.Gate!.SetResult();
        await warmup.Completion.WaitAsync(Patience);

        var failed = await Client(app).GetAsync("/campaigns");
        var page = await failed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(WarmupGate.FailedRetryAfterSeconds), failed.Headers.RetryAfter!.Delta);
        Assert.Equal("The site couldn't start", RenderedPage.Named(page, "startup-failed"));
        Assert.Equal("Storage <can't> be reached.", RenderedPage.Named(page, "startup-failed-reason"));
        Assert.Contains("Storage &lt;can&#39;t&gt; be reached.", page);
        Assert.Contains($"<meta http-equiv=\"refresh\" content=\"{WarmupGate.FailedRetryAfterSeconds}\" />", page);
        Assert.Equal(0, RenderedPage.Count(page, "getting-ready"));

        // The health check still answers for itself.
        Assert.Equal(HttpStatusCode.OK, (await Client(app).GetAsync("/healthz")).StatusCode);
    }

    // A warm-up step failing is not a startup failure: the warm-up finishes and the gate opens.
    [Fact]
    public async Task A_failed_warm_up_step_is_not_a_startup_failure()
    {
        await using var app = new WarmupApp { Broken = true, GateWait = Short };
        await WarmupApp.SeedAsync(app);
        var warmup = app.Services.GetRequiredService<WarmupState>();
        await warmup.Completion.WaitAsync(Patience);

        Assert.Contains(warmup.Steps, s => !s.Ok);
        Assert.Null(warmup.FailureReason);
        Assert.Equal(HttpStatusCode.OK, (await Client(app).GetAsync("/how-it-works")).StatusCode);
    }

    // ---- Many at once ----

    [Fact]
    public async Task Requests_arriving_together_each_get_a_whole_answer()
    {
        await using var app = await Warming();
        var client = Client(app);

        var held = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => client.GetAsync("/how-it-works")));
        foreach (var response in held)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("Getting ready…", RenderedPage.Named(await response.Content.ReadAsStringAsync(), "getting-ready"));
        }

        // Let go while thirty more are in flight: each is either the page or the getting-ready page, whole.
        var during = Enumerable.Range(0, 30).Select(_ => client.GetAsync("/how-it-works")).ToList();
        app.Gate!.SetResult();
        foreach (var response in await Task.WhenAll(during))
        {
            var page = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK) Assert.Equal(0, RenderedPage.Count(page, "getting-ready"));
            else Assert.Equal("Getting ready…", RenderedPage.Named(page, "getting-ready"));
        }

        await app.Services.GetRequiredService<WarmupState>().Completion.WaitAsync(Patience);
        var after = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => client.GetAsync("/how-it-works")));
        Assert.All(after, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
    }

    // ---- In the record of starts ----

    // Each showing is noted: how many, when the first held request arrived, and its wait until warm.
    [Fact]
    public async Task The_start_s_record_says_who_was_asked_to_wait_and_for_how_long()
    {
        await using var app = await Warming();
        var client = Client(app);
        var start = app.Services.GetRequiredService<StartLog>();
        var at = app.Clock.Now;

        await client.GetAsync("/how-it-works");
        app.Clock.Now += TimeSpan.FromSeconds(3);
        await client.GetAsync("/how-it-works");

        var record = start.Snapshot(0);
        Assert.Equal(2, record.Held!.Shown);
        Assert.Equal(at, record.Held.FirstAt);
        Assert.Equal(new FirstRequest(at, StartRecording.GettingReadyRoute, record.FirstRequest!.Ms, 503), record.FirstRequest);
        Assert.Empty(record.FirstVisits);

        app.Clock.Now += TimeSpan.FromSeconds(6);
        app.Gate!.SetResult();
        await app.Services.GetRequiredService<WarmupState>().Completion.WaitAsync(Patience);
        app.Clock.Now += TimeSpan.FromSeconds(30);

        Assert.Equal(new HeldRequests(2, at, 9000), start.Snapshot(0).Held);
        Assert.Equal("2 times; longest wait up to 9.0 s", StartsView.Held(start.Snapshot(0).Held));
    }

    // Held requests are counted from many threads at once: none is lost, and the earliest arrival stands.
    [Fact]
    public void Held_requests_counted_at_once_are_all_counted()
    {
        var epoch = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var start = new StartLog(new ManualClock(epoch), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new WarmupState());
        const int each = 5000;
        var threads = Math.Clamp(Environment.ProcessorCount, 2, 16);

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < each; i++) start.HeldBack(epoch.AddMilliseconds(1 + ((t * each + i) % 997)));
        });

        var held = start.Snapshot(0).Held!;
        Assert.Equal(threads * each, held.Shown);
        Assert.Equal(epoch.AddMilliseconds(1), held.FirstAt);
    }

    [Fact]
    public void How_long_ago_reads_naturally()
    {
        Assert.Equal("0 seconds", WarmupGate.Seconds(0));
        Assert.Equal("1 second", WarmupGate.Seconds(1));
        Assert.Equal("12 seconds", WarmupGate.Seconds(12));
    }

    // ---- Its pages ----

    // A page missing from wwwroot, or one without its blank to fill, stops the app at its start.
    [Theory]
    [InlineData(WarmupGate.GettingReadyFile)]
    [InlineData(WarmupGate.StartupFailedFile)]
    public void Its_pages_are_checked_as_the_app_starts(string missing)
    {
        var root = Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "wwwroot");
        var files = new FilteredFiles(new PhysicalFileProvider(root), missing);

        var problem = Assert.Throws<InvalidOperationException>(() => new WarmupGate(
            _ => Task.CompletedTask, new WarmupState(), new WarmupMarker(),
            new StartLog(TimeProvider.System, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new WarmupState()),
            Options.Create(new WarmupOptions()), new Root(files)));
        Assert.Contains(missing, problem.Message);

        // Both as shipped load.
        _ = new WarmupGate(_ => Task.CompletedTask, new WarmupState(), new WarmupMarker(),
            new StartLog(TimeProvider.System, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new WarmupState()),
            Options.Create(new WarmupOptions()), new Root(new PhysicalFileProvider(root)));
    }

    // Shipped settings: a two-second wait, within the budget.
    [Fact]
    public void The_shipped_wait_is_two_seconds()
    {
        var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))!;
        Assert.Equal(TimeSpan.FromSeconds(2), TimeSpan.Parse((string)settings["Warmup"]!["GateWait"]!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class FilteredFiles(IFileProvider inner, string hidden) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) =>
            subpath.TrimStart('/') == hidden ? new NotFoundFileInfo(subpath) : inner.GetFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

        public Microsoft.Extensions.Primitives.IChangeToken Watch(string filter) => inner.Watch(filter);
    }

    private sealed class Root(IFileProvider files) : IWebHostEnvironment
    {
        public IFileProvider WebRootFileProvider { get; set; } = files;
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Neelam.Web";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = files;
    }
}
