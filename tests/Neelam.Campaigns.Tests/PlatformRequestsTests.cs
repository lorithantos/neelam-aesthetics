using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// App Service's own requests are no visitor's. On staging, 2026-10-11 07:07 UTC, build 125, a start
/// logged "first request (no route) answered 404 in 268 ms, 4125 ms after the start": Application
/// Insights shows it was <c>GET /robots933456.txt</c> from 169.254.131.2:8080, the platform's start-up
/// ping. The record of starts leaves those requests out (first request, first visits, asked to wait),
/// while the warm-up gate lets them through, or holds them, exactly as before.
/// </summary>
public class PlatformRequestsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static HttpClient Client(InMemoryApp app) => app.CreateClient(new() { AllowAutoRedirect = false });

    private static HttpRequestMessage Get(string path, string? userAgent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return request;
    }

    private const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    // ---- The record of starts ----

    // The staging line: the platform's ping at +4.1 s, a 404 with no route, is not the first request;
    // the first visitor after it is.
    [Fact]
    public async Task App_Service_s_start_up_ping_is_not_the_first_request()
    {
        await using var app = new StartsApp();
        var client = Client(app);
        var start = app.Services.GetRequiredService<StartLog>();

        app.Clock.Now += TimeSpan.FromMilliseconds(4125);
        var ping = await client.SendAsync(Get("/robots933456.txt"));
        Assert.Equal(HttpStatusCode.NotFound, ping.StatusCode);
        Assert.Null(start.Snapshot(0).FirstRequest);

        app.Clock.Now += TimeSpan.FromSeconds(58);
        var visitorAt = app.Clock.Now;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/how-it-works", Browser))).StatusCode);

        var record = await StartRecordTests.Eventually(app.StartRecords, r => r.FirstRequest is not null && r.FirstVisits.Count == 1);
        Assert.Equal(("/how-it-works", visitorAt, 200), (record.FirstRequest!.Route, record.FirstRequest.At, record.FirstRequest.Status));
        Assert.Equal(["/how-it-works"], record.FirstVisits.Select(v => v.Route).ToArray());
        Assert.DoesNotContain(app.Log.Entries, e => e.Message.Contains(StartRecording.NoRoute, StringComparison.Ordinal));
    }

    // Always On's ping of / is not the first request or /'s first visit; a visitor to / is both.
    [Fact]
    public async Task Always_On_s_ping_is_not_a_visit_and_a_visitor_to_the_home_page_is()
    {
        await using var app = new StartsApp();
        var client = Client(app);
        var start = app.Services.GetRequiredService<StartLog>();

        app.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/", "AlwaysOn"))).StatusCode);
        Assert.Null(start.Snapshot(0).FirstRequest);
        Assert.Empty(start.Snapshot(0).FirstVisits);

        app.Clock.Now += TimeSpan.FromSeconds(4);
        var visitorAt = app.Clock.Now;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/", Browser))).StatusCode);

        var record = start.Snapshot(0);
        Assert.Equal(("/", visitorAt), (record.FirstRequest!.Route, record.FirstRequest.At));
        Assert.Equal([("/", visitorAt)], record.FirstVisits.Select(v => (v.Route, v.At)).ToArray());
    }

    // Where App Service has WEBSITE_WARMUP_PATH, its ping of that path is the platform's too; without
    // the setting the same path is an ordinary request.
    [Theory]
    [InlineData("/warmup", null)]
    [InlineData(null, "/warmup")]
    public async Task The_platform_s_warm_up_path_is_the_platform_s(string? setting, string? expectedFirst)
    {
        await using var app = new StartsApp { WarmupPath = setting };
        var client = Client(app);
        var start = app.Services.GetRequiredService<StartLog>();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/warmup"))).StatusCode);

        Assert.Equal(expectedFirst, start.Snapshot(0).FirstRequest?.Route);
    }

    // ---- The gate ----

    // While warming: the start-up ping goes straight through to its 404, as before; Always On's ping is
    // held with the getting-ready page, as before, but nobody was asked to wait; a visitor held is counted.
    [Fact]
    public async Task The_gate_treats_the_platform_as_before_and_counts_only_visitors()
    {
        await using var app = new WarmupApp { Gate = new(), GateWait = TimeSpan.FromMilliseconds(200) };
        await WarmupApp.SeedAsync(app);
        var client = Client(app);
        var start = app.Services.GetRequiredService<StartLog>();

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get("/robots933456.txt"))).StatusCode);
        var alwaysOn = await client.SendAsync(Get("/", "AlwaysOn"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, alwaysOn.StatusCode);
        Assert.Equal("Getting ready…", RenderedPage.Named(await alwaysOn.Content.ReadAsStringAsync(), "getting-ready"));
        Assert.Null(start.Snapshot(0).Held);
        Assert.Null(start.Snapshot(0).FirstRequest);

        var visitorAt = app.Clock.Now;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(Get("/how-it-works", Browser))).StatusCode);
        var record = start.Snapshot(0);
        Assert.Equal((1, visitorAt), (record.Held!.Shown, record.Held.FirstAt));
        Assert.Equal(StartRecording.GettingReadyRoute, record.FirstRequest!.Route);

        app.Gate!.SetResult();
        await app.Services.GetRequiredService<WarmupState>().Completion.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/", "AlwaysOn"))).StatusCode);
        Assert.Empty(start.Snapshot(0).FirstVisits);
        Assert.Equal(1, start.Snapshot(0).Held!.Shown);
    }

    // ---- What counts as the platform's ----

    [Theory]
    [InlineData("/robots933456.txt", null, null, true)]
    [InlineData("/robots933456.txt", Browser, null, true)]
    [InlineData("/ROBOTS933456.TXT", null, null, true)]
    [InlineData("/robots933456.txt/more", null, null, false)]
    [InlineData("/", "AlwaysOn", null, true)]
    [InlineData("/", "alwayson", null, true)]
    [InlineData("/", null, null, false)]
    [InlineData("/", "", null, false)]
    [InlineData("/", Browser, null, false)]
    [InlineData("/", "AlwaysOn/2", null, false)]
    [InlineData("/campaigns", "AlwaysOn", null, false)]
    [InlineData("/warmup", null, null, false)]
    [InlineData("/warmup", null, "/warmup", true)]
    [InlineData("/warmup", null, " /warmup ", true)]
    [InlineData("/", null, "/", false)]
    [InlineData("/healthz", null, null, false)]
    public void What_counts_as_the_platform_s(string path, string? userAgent, string? warmupPath, bool platform)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (userAgent is not null) context.Request.Headers.UserAgent = userAgent;

        Assert.Equal(platform, Shipped(warmupPath).IsPlatform(context.Request));
    }

    // Two User-Agent headers are not Always On's one.
    [Fact]
    public void Two_user_agents_are_not_always_on()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Request.Headers.UserAgent = new(["AlwaysOn", "AlwaysOn"]);
        Assert.False(Shipped(null).IsPlatform(context.Request));
    }

    // ---- The data ----

    // As shipped: the Linux start-up ping by its path, Always On by / and its user agent.
    [Fact]
    public void The_shipped_list()
    {
        Assert.Equal(
            [("/robots933456.txt", null), ("/", "AlwaysOn")],
            Shipped(null).Known.Select(k => (k.Path, k.UserAgent)).ToArray());
        Assert.All(Shipped(null).Known, k => Assert.False(string.IsNullOrWhiteSpace(k.Name)));
    }

    // A problem in the list stops the app as it is built, saying every problem.
    [Fact]
    public void A_problem_in_the_list_stops_the_app_saying_every_problem()
    {
        var problem = Assert.Throws<InvalidOperationException>(() => new PlatformRequests(
        [
            new PlatformRequest { Name = "", Path = "/robots933456.txt" },
            new PlatformRequest { Name = "No slash", Path = "robots933456.txt" },
            new PlatformRequest { Name = "Query", Path = "/ping?x=1" },
            new PlatformRequest { Name = "Everyone", Path = "/" },
            new PlatformRequest { Name = "Blank agent", Path = "/ping", UserAgent = " " },
        ]));

        Assert.Contains("Entry 1 has no Name.", problem.Message);
        Assert.Contains("Entry 2's Path must be", problem.Message);
        Assert.Contains("Entry 3's Path must be", problem.Message);
        Assert.Contains("Entry 4 is / with no UserAgent", problem.Message);
        Assert.Contains("Entry 5's UserAgent is blank", problem.Message);
    }

    // The app reads the list as it is built: a bad one stops it there, not at a request.
    [Fact]
    public void The_app_checks_the_list_as_it_starts()
    {
        using var app = new BadPlatformApp();
        var problem = Assert.ThrowsAny<Exception>(() => app.Services);
        Assert.Contains("is / with no UserAgent", Flatten(problem));

        static string Flatten(Exception e) => e.InnerException is null ? e.Message : e.Message + " " + Flatten(e.InnerException);
    }

    private static PlatformRequests Shipped(string? warmupPath)
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { [PlatformRequests.WarmupPathSetting] = warmupPath })
            .Build();
        return PlatformRequests.From(settings);
    }

    private sealed class BadPlatformApp : InMemoryApp
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting(Neelam.Web.Security.PrototypeCallerSource.ClientSetting, DemoApp.Client.ToString());
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{PlatformRequests.Section}:2:{nameof(PlatformRequest.Name)}"] = "Everyone",
                [$"{PlatformRequests.Section}:2:{nameof(PlatformRequest.Path)}"] = "/",
            }));
        }

        protected override void ConfigureAccess(IServiceCollection services)
        {
        }
    }
}
