using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Janet.Azure.Storage;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neelam.Campaigns.Claude;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The warm-up at start (owner, 2026-10-10: "Once she hits the site ... everything else should be
/// snappy"): it runs without holding up the start or a request, warms every client page, writes
/// nothing anywhere, never proofreads or fetches a photo, logs timings and outcomes only, survives
/// every failure, and stops with the app.
/// </summary>
public class WarmupTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // ---- What it covers ----

    // Every page of the client's site is warmed: a page added later fails this until it is listed.
    // The operator's pages (/admin, a 404 on the demo) and the error page are not hers.
    [Fact]
    public void It_warms_every_client_page()
    {
        using var app = new DemoApp();
        var pages = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ComponentTypeMetadata>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Where(route => !route.StartsWith("/admin", StringComparison.Ordinal) && route != "/Error")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(pages, Warmup.Pages.Select(p => p.Route).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("/campaigns/{Id:guid}", pages);
        Assert.Contains("/templates/{Id:guid}", pages);
    }

    // On in the shipped settings, and run by the app as it starts.
    [Fact]
    public void The_app_runs_it_at_start()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))!;
        Assert.True((bool)settings["Warmup"]!["Enabled"]!);
        Assert.Equal(TimeSpan.FromMinutes(1), TimeSpan.Parse((string)settings["Warmup"]!["Budget"]!, System.Globalization.CultureInfo.InvariantCulture));

        using var app = new DemoApp();
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<Warmup>());
    }

    // ---- Read-only, no LLM, no photos, nothing of hers in the log ----

    [Fact]
    public async Task It_reads_every_page_as_she_would_and_writes_nothing()
    {
        await using var app = new WarmupApp();
        await WarmupApp.SeedAsync(app);

        var state = app.Services.GetRequiredService<WarmupState>();
        await state.Completion.WaitAsync(Patience);

        // Every page answered, the campaign and template pages by the ones she has.
        var steps = state.Steps;
        foreach (var page in Warmup.Pages)
            Assert.Contains(steps, s => s.Name == "GET " + page.Route && s.Outcome == "200");
        Assert.Contains(steps, s => s.Name == "clients table" && s.Outcome == "read");
        Assert.Contains(steps, s => s.Name == "settings container" && s.Outcome == "read");

        // Her data was read by the pages, as a first visit reads it...
        var reads = app.Containers.For(DemoApp.Client.Value).Reads.ToArray();
        Assert.Contains(reads, r => r.StartsWith($"drafts/{WarmupApp.CampaignId:N}/", StringComparison.Ordinal));
        Assert.Contains(reads, r => r.StartsWith($"templates/{WarmupApp.TemplateId:N}/", StringComparison.Ordinal));

        // ...and nothing was written anywhere, proofread or fetched.
        Assert.Empty(app.Writes.Attempts);
        Assert.Empty(app.Writes.Calls);
    }

    [Fact]
    public async Task Its_log_says_steps_outcomes_and_times_never_her_content()
    {
        await using var app = new WarmupApp();
        await WarmupApp.SeedAsync(app);
        await app.Services.GetRequiredService<WarmupState>().Completion.WaitAsync(Patience);

        var messages = app.Log.Entries.Select(e => e.Message).ToList();
        Assert.NotEmpty(messages);
        string[] hers =
        [
            WarmupApp.Subject, WarmupApp.DisplayName, DraftFixtures.Membership.Name, WarmupApp.PhotoName, WarmupApp.Treatment,
            WarmupApp.CampaignId.ToString(), WarmupApp.TemplateId.ToString(),
            WarmupApp.CampaignId.ToString("N"), WarmupApp.TemplateId.ToString("N"), DemoApp.Client.Value,
        ];
        foreach (var message in messages)
        {
            foreach (var content in hers) Assert.DoesNotContain(content, message, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(new Regex(@"^Warm-up (.+: .+ in \d+ ms\.|done in \d+ ms: \d+ of \d+ steps answered\.)$"), message);
        }
        Assert.All(app.Log.Entries, e => Assert.Null(e.Error));
    }

    // ---- Never in the way ----

    [Fact]
    public async Task It_holds_up_neither_the_start_nor_a_request()
    {
        await using var app = new WarmupApp { Gate = new() };
        await WarmupApp.SeedAsync(app);

        // The host starts, and answers, while the warm-up waits on the clients table.
        var started = await Task.Run(() => app.CreateClient(new() { AllowAutoRedirect = false })).WaitAsync(Patience);
        var page = await started.GetAsync("/how-it-works").WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var state = app.Services.GetRequiredService<WarmupState>();
        Assert.False(state.IsDone);
        Assert.Empty(state.Steps);

        app.Gate.SetResult();
        await state.Completion.WaitAsync(Patience);
        Assert.Contains(state.Steps, s => s.Name == "GET /campaigns" && s.Outcome == "200");
    }

    // App Service waits on /warmup before sending her to a new start (WEBSITE_WARMUP_PATH): it answers
    // once the warm-up is done, and always 200.
    [Fact]
    public async Task Warmup_answers_once_the_warm_up_is_done()
    {
        await using var app = new WarmupApp { Gate = new() };
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var waiting = client.GetAsync(WarmupEndpoint.Path);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(waiting.IsCompleted);

        app.Gate.SetResult();
        var response = await waiting.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("warm", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["status"]!);
        Assert.True(app.Services.GetRequiredService<WarmupState>().IsDone);
    }

    // With nothing to warm (tests turn it off), /warmup answers at once.
    [Fact]
    public async Task Warmup_answers_at_once_when_the_warm_up_is_off()
    {
        await using var app = new DemoApp();
        var response = await app.CreateClient().GetAsync(WarmupEndpoint.Path).WaitAsync(Patience);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("warm", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["status"]!);
    }

    [Fact]
    public async Task It_stops_when_the_app_does()
    {
        var app = new WarmupApp { Gate = new() };
        var state = app.Services.GetRequiredService<WarmupState>();
        Assert.False(state.IsDone);

        var stopping = Stopwatch.StartNew();
        await app.DisposeAsync().AsTask().WaitAsync(Patience);

        Assert.True(state.IsDone);
        Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(10), $"Stopping took {stopping.Elapsed}.");
        Assert.DoesNotContain(state.Steps, s => s.Name.StartsWith("GET ", StringComparison.Ordinal));
        Assert.Contains(app.Log.Entries, e => e.Message.StartsWith("Warm-up stopped with the app", StringComparison.Ordinal));
    }

    // Storage down and every page request failing: each step says so, the warm-up finishes, and the
    // app goes on answering.
    [Fact]
    public async Task A_failure_is_logged_and_the_app_carries_on()
    {
        await using var app = new WarmupApp { Broken = true };
        await WarmupApp.SeedAsync(app);
        var state = app.Services.GetRequiredService<WarmupState>();
        await state.Completion.WaitAsync(Patience);

        Assert.Contains(state.Steps, s => s.Name == "clients table" && s.Outcome == "failed: RequestFailedException 503 ServerBusy");
        Assert.Contains(state.Steps, s => s.Name == "settings container" && s.Outcome == "failed: RequestFailedException 503 ServerBusy");
        Assert.Contains(state.Steps, s => s.Name == "GET /campaigns" && s.Outcome == "failed: HttpRequestException");
        Assert.Contains(state.Steps, s => s.Name == "GET /campaigns/{Id:guid}" && s.Outcome == "skipped: none listed");
        Assert.Contains(app.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Warm-up clients table: failed", StringComparison.Ordinal));
        Assert.Contains(app.Log.Entries, e => e.Message.StartsWith("Warm-up done in", StringComparison.Ordinal));

        var after = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(WarmupEndpoint.Path);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    // ---- Its own address, and nowhere else ----

    [Theory]
    [InlineData("http://[::]:8080", "http://localhost:8080/")]
    [InlineData("http://+:8080", "http://localhost:8080/")]
    [InlineData("http://*:80", "http://localhost/")]
    [InlineData("http://0.0.0.0:5000", "http://localhost:5000/")]
    [InlineData("http://127.0.0.1:5199", "http://127.0.0.1:5199/")]
    [InlineData("http://localhost:5123;https://localhost:7057", "https://localhost:7057/")]
    [InlineData("http://[::1]:5000", "http://[::1]:5000/")]
    public void It_requests_the_app_at_its_own_address(string listening, string expected) =>
        Assert.Equal(new Uri(expected), LoopbackRequests.Pick(listening.Split(';')));

    [Theory]
    [InlineData("http://example.com:80")]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("ftp://localhost:21")]
    [InlineData("")]
    public void It_never_requests_anywhere_else(string listening) =>
        Assert.Null(LoopbackRequests.Pick(listening.Length == 0 ? [] : [listening]));

    // A page of one campaign or template follows the first one its list links to, by id alone.
    [Fact]
    public void It_follows_the_first_link_by_id()
    {
        var page = """<a href="campaigns/new/aaaaaaaa-0000-0000-0000-000000000001">x</a> <a href="campaigns/bbbbbbbb-0000-0000-0000-000000000002">y</a>""";

        Assert.Equal("bbbbbbbb-0000-0000-0000-000000000002", Warmup.FirstLinked(page, "campaigns/"));
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", Warmup.FirstLinked(page, "campaigns/new/"));
        Assert.Null(Warmup.FirstLinked(page, "templates/"));
        Assert.Null(Warmup.FirstLinked("""<a href="campaigns/../admin">z</a>""", "campaigns/"));
    }

    // The deployed site's warm-up path is this endpoint (Linux App Service; see site.bicep).
    [Fact]
    public void App_service_waits_on_the_warm_up_endpoint()
    {
        var site = File.ReadAllText(Path.Combine(InfrastructureTests.Root, "infra", "site.bicep"));
        var setting = Regex.Match(site, @"name: 'WEBSITE_WARMUP_PATH'\s*value: '([^']*)'");

        Assert.True(setting.Success, "site.bicep sets WEBSITE_WARMUP_PATH");
        Assert.Equal(WarmupEndpoint.Path, setting.Groups[1].Value);
        // Any answer counts as ready: a status list could fail a start the app answered.
        Assert.DoesNotContain("name: 'WEBSITE_WARMUP_STATUSES'", site);
    }
}

/// <summary>
/// The demo with the warm-up on, and everything it could change refusing: every blob, table and
/// activity write, the proofreader and the photo fetcher. Each refusal is noted before it throws, as
/// a page might catch the throw (the activity trail does).
/// </summary>
public sealed class WarmupApp : InMemoryApp
{
    internal static readonly Guid TemplateId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    internal static readonly Guid CampaignId = Guid.Parse("bbbbbbbb-1111-0000-0000-000000000001");
    internal const string Subject = "Celebrate 1 year of the warm-up salon";
    internal const string DisplayName = "Warm-up Salon Aesthetics";
    internal const string PhotoName = "Principals toasting";
    internal const string Treatment = "Glow Infusion Deluxe";

    internal RefusedWrites Writes { get; } = new();

    internal ListLogger<Warmup> Log { get; } = new();

    /// <summary>When set, the clients table waits on it, honouring cancellation.</summary>
    internal TaskCompletionSource? Gate { get; init; }

    /// <summary>Storage fails and every request the warm-up makes fails.</summary>
    internal bool Broken { get; init; }

    /// <summary>Her data, put straight into storage before the app starts: a template, a campaign, a photo, a known item, a look.</summary>
    internal static async Task SeedAsync(WarmupApp app)
    {
        app.Clients.Put(new ClientRecord(DemoApp.Client, Guid.Parse("11111111-1111-1111-1111-111111111111"), DisplayName));
        var stores = new ClientStores(app.Containers.For, app.Clock, TimeSpan.FromDays(1), app.Approvals, TestRecords.Unwatched);
        var campaigns = stores.Campaigns(DemoApp.Client, Actor.Demo);
        await campaigns.SaveTemplateAsync(TemplateId, DraftFixtures.Membership);
        await campaigns.SaveDraftAsync(CampaignId, Subject, DraftFixtures.Finished());
        await stores.Images(DemoApp.Client, Actor.Demo)
            .AddFromSquareAsync(PhotoName, "https://square-web-production-f.squarecdn.com/files/warmup/original.jpeg");
        await app.KnownItems.AddAsync(DemoApp.Client, new KnownTreatment(KnownItem.NewId(), Treatment));
        await stores.SaveLookAsync(DemoApp.Client, ClientLook.Standard, Actor.Demo);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(PrototypeCallerSource.ClientSetting, DemoApp.Client.ToString());
        base.ConfigureWebHost(builder);
        builder.UseSetting($"{WarmupOptions.Section}:{nameof(WarmupOptions.Enabled)}", "true");
        builder.ConfigureTestServices(services =>
        {
            // The warm-up alone: the undo sweep, which may delete at start, is tested on its own.
            foreach (var sweep in services.Where(d => d.ImplementationType == typeof(UndoSweep)).ToList())
                services.Remove(sweep);

            services.RemoveAll<ISelfRequests>().AddSingleton<ISelfRequests>(provider =>
                new TestServerRequests((TestServer)provider.GetRequiredService<IServer>(), Broken));
            var approvals = new ReadOnlyApprovals(Approvals, Writes);
            services.RemoveAll<IApprovalStore>().AddSingleton<IApprovalStore>(approvals);
            services.RemoveAll<IActivityLog>().AddSingleton<IActivityLog>(new RefusingActivityLog(Writes));
            services.RemoveAll<ClientStores>().AddSingleton(provider => new ClientStores(
                container => new ReadOnlyBlobs(Containers.For(container), container, Writes, Broken), Clock,
                TimeSpan.FromDays(1), approvals, provider.GetRequiredService<ActivityRecorder>()));
            services.RemoveAll<IClientDirectory>().AddSingleton<IClientDirectory>(new ReadOnlyClients(Clients, Writes, Gate, Broken));            services.RemoveAll<ISupportGrantStore>().AddSingleton<ISupportGrantStore>(new ReadOnlyGrants(Grants, Writes));
            services.RemoveAll<IKnownItemStore>().AddSingleton<IKnownItemStore>(new ReadOnlyKnownItems(KnownItems, Writes));

            // The proofread on, as the test site will have it, and refusing: the warm-up must never reach it.
            services.RemoveAll<ProofreadSwitch>().AddSingleton(new ProofreadSwitch(true));
            services.RemoveAll<IProofreader>().AddSingleton<IProofreader>(new RefusingProofreader(Writes));
            services.RemoveAll<SquarePhotoFetcher>().AddSingleton(
                new SquarePhotoFetcher(new HttpClient(new RefusingPhotoHandler(Writes)), ImageLibrary.SquareHosts));

            services.AddSingleton<ILogger<Warmup>>(Log);
        });
    }

    protected override void ConfigureAccess(IServiceCollection services)
    {
    }
}

/// <summary>Every write, proofread and photo fetch attempted, refused.</summary>
internal sealed class RefusedWrites
{
    public ConcurrentQueue<string> Attempts { get; } = new();

    /// <summary>Calls out of the app that cost money or reach Square.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public Exception Write(string what)
    {
        Attempts.Enqueue(what);
        return new InvalidOperationException("The warm-up must not write: " + what);
    }

    public Exception Call(string what)
    {
        Calls.Enqueue(what);
        return new InvalidOperationException("The warm-up must not call out: " + what);
    }
}

/// <summary>What storage throws when it is overloaded.</summary>
internal static class StorageFailure
{
    public static Azure.RequestFailedException Busy() => new(503, "Storage is busy.", "ServerBusy", null);
}

/// <summary>The app's own pages through the test server, or failing outright when broken.</summary>
internal sealed class TestServerRequests(TestServer server, bool broken) : ISelfRequests
{
    public HttpClient? Create() => broken ? new HttpClient(new FailingHandler()) { BaseAddress = server.BaseAddress } : server.CreateClient();

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused."));
    }
}

internal sealed class ReadOnlyBlobs(IBlobBackend inner, string container, RefusedWrites writes, bool broken) : IBlobBackend
{
    public async IAsyncEnumerable<BlobEntry> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (broken) throw StorageFailure.Busy();
        await foreach (var entry in inner.ListAsync(prefix, cancellationToken)) yield return entry;
    }

    public Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        broken ? throw StorageFailure.Busy() : inner.ReadAsync(name, cancellationToken);

    public Task<bool> TryCreateAsync(string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        throw writes.Write($"create {container}/{name}");

    public Task<bool> SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        throw writes.Write($"set metadata {container}/{name}");

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        throw writes.Write($"delete {container}/{name}");

    public Task<bool> DeleteIfUnchangedAsync(string name, string etag, CancellationToken cancellationToken = default) =>
        throw writes.Write($"delete {container}/{name}");
}

internal sealed class ReadOnlyClients(IClientDirectory inner, RefusedWrites writes, TaskCompletionSource? gate, bool broken) : IClientDirectory
{
    public async Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
        if (broken) throw StorageFailure.Busy();
        return await inner.ListAsync(cancellationToken);
    }

    public Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default) => throw writes.Write("add client");

    public Task UpdateAsync(ClientRecord client, CancellationToken cancellationToken = default) => throw writes.Write("update client");
}

internal sealed class ReadOnlyGrants(ISupportGrantStore inner, RefusedWrites writes) : ISupportGrantStore
{
    public Task<IReadOnlyList<SupportGrant>> ForClientAsync(ClientName client, CancellationToken cancellationToken = default) =>
        inner.ForClientAsync(client, cancellationToken);

    public Task RecordAsync(SupportGrant grant, CancellationToken cancellationToken = default) => throw writes.Write("record grant");
}

internal sealed class ReadOnlyApprovals(IApprovalStore inner, RefusedWrites writes) : IApprovalStore
{
    public Task<IReadOnlyList<ApprovalRecord>> ForCampaignAsync(ClientName client, Guid campaignId, CancellationToken cancellationToken = default) =>
        inner.ForCampaignAsync(client, campaignId, cancellationToken);

    public Task PutAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => throw writes.Write("put approval");
}

internal sealed class RefusingActivityLog(RefusedWrites writes) : IActivityLog
{
    public Task RecordAsync(Neelam.Campaigns.Storage.ActivityEvent activity, CancellationToken cancellationToken = default) =>
        throw writes.Write($"activity {activity.Action}");
}

internal sealed class ReadOnlyKnownItems(IKnownItemStore inner, RefusedWrites writes) : IKnownItemStore
{
    public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default) =>
        inner.ForClientAsync(client, cancellationToken);

    public Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) => throw writes.Write("add known item");

    public Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) => throw writes.Write("update known item");

    public Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default) => throw writes.Write("remove known item");
}

internal sealed class RefusingProofreader(RefusedWrites writes) : IProofreader
{
    public Task<ProofreadResult> ProofreadAsync(Campaign campaign, BusinessContext? business, CancellationToken cancellationToken = default) =>
        throw writes.Call("proofread");
}

internal sealed class RefusingPhotoHandler(RefusedWrites writes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw writes.Call($"photo fetch {request.RequestUri?.Host}");
}
