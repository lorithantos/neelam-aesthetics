using System.Globalization;
using System.Net;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Neelam.Web;

/// <summary>
/// Until the warm-up has finished, a visitor gets a calm "Getting ready" page instead of a cold one
/// (owner, 2026-10-10: "We should add middleware to do the warmup and serve up a page similar to what we
/// see on the image viewer"). The warm-up takes about ten seconds after a start; a request that arrives
/// sooner waits up to <see cref="WarmupOptions.GateWait"/> for it, and if it is still warming gets
/// <c>getting-ready.html</c> with 503 and Retry-After, which refreshes itself (no script needed) and
/// says how long ago the site started. Once the warm-up has finished, or its budget has run out, the gate
/// is open for good and costs one read per request.
/// </summary>
/// <remarks>
/// <para>
/// Passed straight through while warming: the warm-up's own requests (<see cref="WarmupMarker"/>; it
/// would otherwise wait on itself), <c>/healthz</c> and <c>/warmup</c>, the site's static files (so the
/// page has its stylesheet), and Blazor's own addresses (<c>_framework</c>, <c>_content</c>,
/// <c>_blazor</c>), so a page already open is never cut off from its circuit. It sits before routing,
/// authorization and antiforgery, so nothing half-started runs for a held request.
/// </para>
/// <para>
/// A real startup failure (<see cref="WarmupState.Fail"/>) shows <c>startup-failed.html</c> with a
/// short reason instead. A warm-up step that fails is not one: the warm-up still finishes and the gate
/// opens. Every real startup check in this app today stops the process before it listens (the
/// credential guard, the settings, the catalog, glossary and proofread data), so nothing calls Fail yet.
/// </para>
/// <para>
/// Each time the page is shown, the start's record notes it (<see cref="StartLog.HeldBack"/>), so the
/// Starts page says how many requests were held and how long the earliest of them waited.
/// </para>
/// </remarks>
public sealed class WarmupGate
{
    /// <summary>The getting-ready page, in wwwroot. Its <c>{{SECONDS}}</c> is how long ago the site started ("7 seconds").</summary>
    public const string GettingReadyFile = "getting-ready.html";

    /// <summary>The startup-failed page, in wwwroot. Its <c>{{REASON}}</c> is the reason, HTML-encoded.</summary>
    public const string StartupFailedFile = "startup-failed.html";

    /// <summary>Seconds a browser is told to wait before asking again while warming; the page's own refresh says the same.</summary>
    public const int RetryAfterSeconds = 3;

    /// <summary>Seconds a browser is told to wait before asking again after a startup failure.</summary>
    public const int FailedRetryAfterSeconds = 30;

    private static readonly string[] Passes = ["/_framework/", "/_content/", "/_blazor"];

    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".map", ".ico", ".png", ".svg", ".jpg", ".jpeg", ".gif", ".webp", ".woff", ".woff2", ".txt",
    };

    private readonly RequestDelegate _next;
    private readonly WarmupState _warmup;
    private readonly WarmupMarker _marker;
    private readonly StartLog _start;
    private readonly WarmupOptions _options;
    private readonly string _gettingReady;
    private readonly string _startupFailed;

    public WarmupGate(
        RequestDelegate next, WarmupState warmup, WarmupMarker marker, StartLog start, IOptions<WarmupOptions> options,
        IWebHostEnvironment environment)
    {
        _next = next;
        _warmup = warmup;
        _marker = marker;
        _start = start;
        _options = options.Value;
        // Read once, here, as the pipeline is built: a page missing from wwwroot stops the app at its
        // start, rather than failing a held request.
        _gettingReady = Page(environment.WebRootFileProvider, GettingReadyFile, "{{SECONDS}}");
        _startupFailed = Page(environment.WebRootFileProvider, StartupFailedFile, "{{REASON}}");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_warmup.FailureReason is null && IsOpen())
        {
            await _next(context);
            return;
        }
        if (PassesWhileWarming(context.Request) || _marker.IsOn(context.Request))
        {
            await _next(context);
            return;
        }
        if (_warmup.FailureReason is { } reason)
        {
            await AnswerAsync(context, FailedRetryAfterSeconds,
                _startupFailed.Replace("{{REASON}}", WebUtility.HtmlEncode(reason), StringComparison.Ordinal));
            return;
        }

        var arrived = _start.Clock.GetUtcNow();
        // Nearly warm is worth a short wait rather than a page and a refresh.
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            await Task.WhenAny(_warmup.Completion, Task.Delay(_options.GateWait, waiting.Token));
            await waiting.CancelAsync();
        }
        if (_warmup.FailureReason is null && IsOpen())
        {
            await _next(context);
            return;
        }
        if (context.RequestAborted.IsCancellationRequested) return;

        _start.HeldBack(arrived);
        context.Items[StartRecording.GettingReadyRoute] = true;
        if (_warmup.FailureReason is { } failed)
        {
            await AnswerAsync(context, FailedRetryAfterSeconds,
                _startupFailed.Replace("{{REASON}}", WebUtility.HtmlEncode(failed), StringComparison.Ordinal));
            return;
        }
        var seconds = Math.Max(0, _start.MsSinceStart(_start.Clock.GetUtcNow()) / 1000);
        await AnswerAsync(context, RetryAfterSeconds,
            _gettingReady.Replace("{{SECONDS}}", Seconds(seconds), StringComparison.Ordinal));
    }

    /// <summary>
    /// Open once the warm-up has finished, however it finished, or once its budget has passed since the
    /// start in any case, so the gate can never stay shut.
    /// </summary>
    private bool IsOpen() =>
        _warmup.IsDone || _start.MsSinceStart(_start.Clock.GetUtcNow()) >= _options.Budget.TotalMilliseconds;

    /// <summary>"1 second", "7 seconds".</summary>
    public static string Seconds(long seconds) =>
        seconds == 1 ? "1 second" : seconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>What goes through while warming: the health and warm-up endpoints, Blazor's own addresses and static files.</summary>
    public static bool PassesWhileWarming(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) || path.Equals(WarmupEndpoint.Path, StringComparison.OrdinalIgnoreCase))
            return true;
        if (Passes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
        var last = path[(path.LastIndexOf('/') + 1)..];
        return StaticExtensions.Contains(Path.GetExtension(last));
    }

    private static async Task AnswerAsync(HttpContext context, int retryAfter, string html)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static string Page(IFileProvider files, string name, string placeholder)
    {
        var file = files.GetFileInfo(name);
        if (!file.Exists) throw new InvalidOperationException($"wwwroot/{name} is missing: the warm-up gate shows it while the site starts.");
        using var reader = new StreamReader(file.CreateReadStream());
        var html = reader.ReadToEnd();
        if (!html.Contains(placeholder, StringComparison.Ordinal))
            throw new InvalidOperationException($"wwwroot/{name} has no {placeholder} for the warm-up gate to fill in.");
        return html;
    }
}
