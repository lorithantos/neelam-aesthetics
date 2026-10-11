using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Azure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// Warms the app as soon as it starts, so the first page she opens after a restart (a deploy, a
/// setting changed, a platform recycle) is as quick as the second (owner, 2026-10-10: "Once she hits
/// the site and Azure has gotten off its lazy ass, everything else should be snappy").
/// </summary>
/// <remarks>
/// <para>
/// Two steps, off the startup path. First storage: the clients table (as <c>/healthz</c> reads it)
/// and the operator's standard tier-name ladders in the <c>settings</c> container, so the first token
/// for each service is fetched and each connection opened. Neither is a client's data. Then, once the
/// server listens, every client page is requested once from the app itself over its own local address
/// (<see cref="Pages"/>), in order, one at a time, so each page's code is compiled and the reads its
/// first visit makes are made with connections already open.
/// </para>
/// <para>
/// Why requests rather than rendering components in-process: a page's first-use cost is the whole
/// request, not only its component: routing, authorization, the layout, antiforgery, streaming
/// rendering and the reads through the caller, access check and stores. Only a request goes through
/// all of it, and it goes through exactly what hers will, as the same caller, with the same access
/// check, stores and identity: the warm-up reads a client's data only where a page would show it to
/// whoever asked. In Enforced mode nobody is signed in on these requests, so the pages answer as they
/// would to anyone not signed in and no client's data is read at all.
/// </para>
/// <para>
/// It only reads: GET requests, which on these pages write nothing, and two reads. It never proofreads
/// (only her click does) and never fetches a photo: it requests the app's own addresses and nothing a
/// page links to elsewhere. It logs each step's name, outcome and time; never a page, an id or a row.
/// A step that fails is logged and the next one runs; nothing it does fails a request or stops the app;
/// it gives up after <see cref="WarmupOptions.Budget"/>, and stops when the app does.
/// </para>
/// </remarks>
public sealed class Warmup(
    IClientDirectory clients, ClientStores stores, ISelfRequests self, WarmupState state,
    IHostApplicationLifetime lifetime, IOptions<WarmupOptions> options, ILogger<Warmup> log) : BackgroundService
{
    /// <summary>
    /// The pages warmed, in order: every page of the client's site. A page whose address names a saved
    /// campaign or template takes the first one linked from the list page before it, as a click would;
    /// with none listed it is skipped.
    /// </summary>
    public static readonly IReadOnlyList<WarmPage> Pages =
    [
        new("/"),
        new("/how-it-works"),
        new("/campaigns"),
        new("/campaigns/{Id:guid}", From: "/campaigns", Link: "campaigns/"),
        new("/campaigns/new/{TemplateId:guid}", From: "/campaigns", Link: "campaigns/new/"),
        new("/templates"),
        new("/templates/{Id:guid}", From: "/templates", Link: "templates/"),
        new("/templates/new"),
        new("/images"),
        new("/known-items"),
        new("/look"),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            state.Finish();
            return;
        }
        // Off the startup path: the host carries on starting, and answering, while this runs. .NET 10
        // already runs ExecuteAsync apart from the host's start; the yield keeps it so wherever it runs.
        await Task.Yield();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(options.Value.Budget);
        var total = Stopwatch.StartNew();
        try
        {
            await StorageAsync(budget.Token);
            await StartedAsync(budget.Token);
            await PagesAsync(budget.Token);
            log.LogInformation("Warm-up done in {Ms} ms: {Ok} of {Steps} steps answered.",
                total.ElapsedMilliseconds, state.Steps.Count(s => s.Ok), state.Steps.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("Warm-up stopped with the app after {Ms} ms.", total.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            log.LogWarning("Warm-up gave up after {Ms} ms, its budget; the pages not reached warm on first use.",
                total.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Every step already catches its own failure; this is the guard on the guard.
            log.LogError("Warm-up failed after {Ms} ms: {Error}.", total.ElapsedMilliseconds, Describe(ex));
        }
        finally
        {
            state.Finish();
        }
    }

    // The first token for each storage service, and its connection: the clients table, then the
    // settings container, through the same clients and identity every request uses.
    private async Task StorageAsync(CancellationToken ct)
    {
        await StepAsync("clients table", async () => { await clients.ListAsync(ct); return "read"; }, ct);
        await StepAsync("settings container",
            async () => { await stores.StandardLadders().CurrentAsync(ct); return "read"; }, ct);
    }

    // Requests wait for the server to listen; the storage step does not.
    private async Task StartedAsync(CancellationToken ct)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested) return;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(ct);
    }

    private async Task PagesAsync(CancellationToken ct)
    {
        using var http = self.Create();
        if (http is null)
        {
            state.Add(new WarmStep("pages", "skipped: no local address", 0, Ok: false));
            log.LogWarning("Warm-up skipped the pages: the server has no local address to request them at.");
            return;
        }
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in Pages)
        {
            string path;
            if (page.From is null) path = page.Route;
            else if (FirstLinked(bodies.GetValueOrDefault(page.From), page.Link!) is { } id) path = "/" + page.Link + id;
            else
            {
                Record(new WarmStep("GET " + page.Route, "skipped: none listed", 0, Ok: true));
                continue;
            }
            await StepAsync("GET " + page.Route, async () =>
            {
                using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
                // Read to the end: a streamed page finishes rendering only as its body is read.
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode == HttpStatusCode.OK) bodies[page.Route] = text;
                return ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }, ct, ok: Answered);
        }
    }

    // A page answered when the app gave it an answer of its own, a refusal included: in Enforced mode
    // nobody is signed in, and the page's answer is still the code a first visit runs.
    private static bool Answered(string status) =>
        int.TryParse(status, System.Globalization.CultureInfo.InvariantCulture, out var code) && code < 500;

    // One step: its name, outcome and time, logged and kept. A failure is the step's outcome, never
    // the warm-up's end; a cancellation ends the warm-up.
    private async Task<string?> StepAsync(string name, Func<Task<string>> step, CancellationToken ct, Func<string, bool>? ok = null)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var outcome = await step();
            Record(new WarmStep(name, outcome, watch.ElapsedMilliseconds, ok?.Invoke(outcome) ?? true));
            return outcome;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Record(new WarmStep(name, "failed: " + Describe(ex), watch.ElapsedMilliseconds, Ok: false));
            return null;
        }
    }

    private void Record(WarmStep step)
    {
        state.Add(step);
        if (step.Ok) log.LogInformation("Warm-up {Step}: {Outcome} in {Ms} ms.", step.Name, step.Outcome, step.Ms);
        else log.LogWarning("Warm-up {Step}: {Outcome} in {Ms} ms.", step.Name, step.Outcome, step.Ms);
    }

    // What failed, without anything a message might quote: the exception's type, and for storage its
    // status and error code.
    private static string Describe(Exception ex) => ex is RequestFailedException storage
        ? $"{ex.GetType().Name} {storage.Status} {storage.ErrorCode}"
        : ex.GetType().Name;

    /// <summary>The first id linked as <c>href="{link}{id}"</c> in a page, or null.</summary>
    public static string? FirstLinked(string? page, string link)
    {
        if (page is null) return null;
        var match = Regex.Match(page, "href=\"" + Regex.Escape(link) + "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\"");
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>The address App Service pings after starting the app (WEBSITE_WARMUP_PATH in infra/site.bicep).</summary>
public static class WarmupEndpoint
{
    public const string Path = "/warmup";
}

/// <summary>One page the warm-up requests.</summary>
/// <param name="Route">The page's route as declared, which is also what the log names.</param>
/// <param name="From">For a page of one saved campaign or template, the list page whose first link it follows.</param>
/// <param name="Link">The link's start in that list page, before the id.</param>
public sealed record WarmPage(string Route, string? From = null, string? Link = null);

/// <summary>One warm-up step: its name, its outcome (a status code, "read", "skipped: ..." or "failed: ..."), and how long it took.</summary>
public sealed record WarmStep(string Name, string Outcome, long Ms, bool Ok);

/// <summary>
/// Whether the warm-up has finished, and what it did: written by the warm-up alone, read by
/// <c>/warmup</c> and the tests. Its steps are a concurrent queue, as they are added on the warm-up's
/// thread while a request may read them.
/// </summary>
public sealed class WarmupState
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentQueue<WarmStep> _steps = new();

    /// <summary>Completes when the warm-up has finished, however it finished. Never faults.</summary>
    public Task Completion => _done.Task;

    public bool IsDone => _done.Task.IsCompleted;

    public IReadOnlyList<WarmStep> Steps => _steps.ToArray();

    internal void Add(WarmStep step) => _steps.Enqueue(step);

    internal void Finish() => _done.TrySetResult();
}

/// <summary>Settings for the warm-up, section <c>Warmup</c>.</summary>
public sealed class WarmupOptions
{
    public const string Section = "Warmup";

    /// <summary>On unless turned off; the tests turn it off where they do not test it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the warm-up may take in all before it gives up, so it always finishes.</summary>
    public TimeSpan Budget { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>An HTTP client for the app's own pages, or null when there is no address to reach them at.</summary>
public interface ISelfRequests
{
    HttpClient? Create();
}

/// <summary>
/// The app's own pages over its own listening address, as the server reports it: a wildcard host
/// (as App Service's <c>http://+:8080</c>) is reached as localhost. Only a loopback or wildcard
/// address is used, so a request can never leave the machine. It follows no redirect and sends no
/// cookie, credential or proxy.
/// </summary>
public sealed class LoopbackRequests(IServer server) : ISelfRequests
{
    public HttpClient? Create()
    {
        if (Pick(server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []) is not { } address) return null;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
        };
        var http = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("neelam-warmup");
        return http;
    }

    /// <summary>
    /// The address to request the app at: https first where the server listens on both (a local run,
    /// where http redirects to https), else http; a wildcard host as localhost. Null when the server
    /// listens on no loopback or wildcard address.
    /// </summary>
    public static Uri? Pick(IEnumerable<string> addresses)
    {
        var usable = addresses
            .Select(a => Uri.TryCreate(a.Replace("://+", "://localhost").Replace("://*", "://localhost"), UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .Where(u => u.Scheme is "http" or "https")
            .Select(Loopback)
            .OfType<Uri>()
            .ToList();
        return usable.FirstOrDefault(u => u.Scheme == "https") ?? usable.FirstOrDefault();
    }

    private static Uri? Loopback(Uri address)
    {
        var host = address.Host.Trim('[', ']');
        bool wildcard = host is "0.0.0.0" or "::";
        bool loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        if (!wildcard && !loopback) return null;
        return new UriBuilder(address.Scheme, wildcard ? "localhost" : address.Host, address.Port).Uri;
    }
}
