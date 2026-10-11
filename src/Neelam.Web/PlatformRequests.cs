using System.Collections.Immutable;

namespace Neelam.Web;

/// <summary>
/// One request Azure App Service makes of its own: its path, and the user agent it carries where the
/// path alone would take in a visitor too. Data, from <c>PlatformRequests</c> in <c>appsettings.json</c>.
/// </summary>
public sealed record PlatformRequest
{
    /// <summary>What it is, for whoever reads the settings: "Always On".</summary>
    public string Name { get; init; } = "";

    /// <summary>The path it requests, from the slash, compared ignoring case: <c>/robots933456.txt</c>.</summary>
    public string Path { get; init; } = "";

    /// <summary>The whole User-Agent it sends, compared ignoring case; null when the path alone tells it.</summary>
    public string? UserAgent { get; init; }
}

/// <summary>
/// Tells the requests Azure App Service makes of its own from a visitor's, so the record of starts
/// never takes one for her: not as a start's first request, not as a page's first visit, and not as
/// someone asked to wait (staging, 2026-10-11 07:07 UTC, build 125: "first request (no route) answered
/// 404 in 268 ms, 4125 ms after the start" was the platform's start-up ping, <c>GET /robots933456.txt</c>).
/// It only classifies: the warm-up gate lets each through, or holds it, exactly as before.
/// </summary>
/// <remarks>
/// <para>
/// The requests are data (<c>PlatformRequests</c> in <c>appsettings.json</c>), checked as the app starts:
/// a problem stops the app, saying every problem. To them is added the path in <c>WEBSITE_WARMUP_PATH</c>
/// when App Service has it, since the platform pings that path in place of <c>/robots933456.txt</c>
/// whenever it starts the app (Microsoft Learn, "Environment variables and app settings in Azure App
/// Service"); a warm-up path of <c>/</c> is not taken, as her home page could not be told from it.
/// </para>
/// <para>
/// Built once and never changed, so any number of requests may ask it at once.
/// </para>
/// </remarks>
public sealed class PlatformRequests
{
    /// <summary>The settings section holding the list.</summary>
    public const string Section = "PlatformRequests";

    /// <summary>The App Service setting naming the path the platform pings as it starts the app.</summary>
    public const string WarmupPathSetting = "WEBSITE_WARMUP_PATH";

    private readonly ImmutableArray<PlatformRequest> _known;

    public PlatformRequests(IEnumerable<PlatformRequest> known, string? warmupPath = null)
    {
        var listed = known.ToList();
        var problems = listed.SelectMany(Problems).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException($"{Section} in appsettings.json: " + string.Join(" ", problems));
        if (warmupPath?.Trim() is { Length: > 1 } path && path.StartsWith('/'))
            listed.Add(new PlatformRequest { Name = $"App Service warm-up ping ({WarmupPathSetting})", Path = path });
        _known = [.. listed];
    }

    /// <summary>From the app's configuration: the shipped list, and <c>WEBSITE_WARMUP_PATH</c> when set.</summary>
    public static PlatformRequests From(IConfiguration configuration) =>
        new(configuration.GetSection(Section).Get<List<PlatformRequest>>() ?? [], configuration[WarmupPathSetting]);

    /// <summary>Every request it knows as the platform's, the warm-up path included.</summary>
    public ImmutableArray<PlatformRequest> Known => _known;

    /// <summary>Whether this request is one App Service makes of its own.</summary>
    public bool IsPlatform(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        foreach (var known in _known)
        {
            if (!path.Equals(known.Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (known.UserAgent is null) return true;
            if (request.Headers.UserAgent is { Count: 1 } agents
                && string.Equals(agents[0], known.UserAgent, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> Problems(PlatformRequest request, int index)
    {
        var at = $"Entry {index + 1}";
        if (string.IsNullOrWhiteSpace(request.Name)) yield return $"{at} has no Name.";
        if (string.IsNullOrEmpty(request.Path) || !request.Path.StartsWith('/') || request.Path.Any(c => c is '?' or '#' || char.IsWhiteSpace(c)))
            yield return $"{at}'s Path must be a path from the slash, with no query or spaces, such as /robots933456.txt.";
        else if (request.Path == "/" && request.UserAgent is null)
            yield return $"{at} is / with no UserAgent, which would leave out every visitor to the home page.";
        if (request.UserAgent is { } agent && string.IsNullOrWhiteSpace(agent))
            yield return $"{at}'s UserAgent is blank: leave it out to match on the path alone.";
    }
}
