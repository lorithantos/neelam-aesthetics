using System.Globalization;
using Neelam.Campaigns.Storage;

namespace Neelam.Web.Components.Pages;

/// <summary>How the Starts page words each cell. Times are UTC: the operator is no one client, so no client's zone applies.</summary>
public static class StartsView
{
    /// <summary>The stored starts with this one, as it stands, in place of its stored copy; newest first, at most <paramref name="count"/>.</summary>
    public static IReadOnlyList<StartRecord> WithCurrent(IReadOnlyList<StartRecord> stored, StartRecord current, int count) =>
        stored.Where(s => s.Id != current.Id).Append(current)
            .OrderByDescending(s => s.Id, StringComparer.Ordinal)
            .Take(count)
            .ToList();

    public static string When(DateTimeOffset at) =>
        at.UtcDateTime.ToString("d MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"Build 73 · abc1234" from the deploy's stamp ("Build 73 (local) | Commit abc1234... | Branch main | time"), or "none".</summary>
    public static string Build(string? stamp)
    {
        if (stamp is null) return "none";
        var parts = stamp.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var build = parts.FirstOrDefault(p => p.StartsWith("Build ", StringComparison.Ordinal))?.Split(' ')[..2] is { } b ? string.Join(' ', b) : null;
        var commit = parts.FirstOrDefault(p => p.StartsWith("Commit ", StringComparison.Ordinal))?["Commit ".Length..].Trim();
        if (commit is { Length: > 7 }) commit = commit[..7];
        return (build, commit) switch
        {
            (null, null) => stamp,
            ({ } only, null) => only,
            (null, { } only) => only,
            ({ } both, { } and) => $"{both} · {and}",
        };
    }

    /// <summary>Why the start happened: "New build", "Restart", "Down for 7 min", or "Unknown" with no heartbeat to go by.</summary>
    public static string Why(StartRecord start) => start.Cause switch
    {
        StartCause.NewBuild => "New build",
        StartCause.Restart => "Restart",
        StartCause.Down when start.DownForMs is { } gap => "Down for " + Gap(gap),
        _ => "Unknown",
    };

    /// <summary>How long the app was not running before the start, or "Not known" with no heartbeat before it.</summary>
    public static string DownFor(StartRecord start) => start.DownForMs is { } gap ? Gap(gap) : "Not known";

    /// <summary>"40 s" under two minutes, "7 min" under two hours, "3 h 5 min" from there.</summary>
    public static string Gap(long ms)
    {
        var seconds = ms / 1000;
        if (seconds < 120) return seconds.ToString(CultureInfo.InvariantCulture) + " s";
        var minutes = seconds / 60;
        if (minutes < 120) return minutes.ToString(CultureInfo.InvariantCulture) + " min";
        return $"{(minutes / 60).ToString(CultureInfo.InvariantCulture)} h {(minutes % 60).ToString(CultureInfo.InvariantCulture)} min";
    }

    public static string Warmup(WarmupOutcome? warmup) => warmup switch
    {
        null => "Not finished",
        { Steps: 0 } => "Off",
        { } w => $"{Duration(w.TotalMs)}, {w.Answered} of {w.Steps} answered" +
                 (w.SlowestStep is { } step ? $"; slowest {step}, {Duration(w.SlowestMs)}" : ""),
    };

    public static string FirstRequest(StartRecord start) => start.FirstRequest is { } r
        ? $"{r.Route}, {r.Status}, {Duration(r.Ms)} ({Duration(Math.Max(0, (long)(r.At - start.StartedAt).TotalMilliseconds))} after the start)"
        : "None yet";

    /// <summary>How often the getting-ready page was shown, and the longest anyone can have waited.</summary>
    public static string Held(HeldRequests? held) => held switch
    {
        null => "No one",
        { Shown: 1 } h => $"Once; waited up to {Duration(h.LongestWaitMs)}",
        { } h => $"{h.Shown} times; longest wait up to {Duration(h.LongestWaitMs)}",
    };

    public static string Visit(PageVisit? visit) => visit is { } v ? $"{v.Route}, {Duration(v.Ms)}" : "None yet";

    /// <summary>"420 ms" under a second, "1.4 s" from a second.</summary>
    public static string Duration(long ms) => ms < 1000
        ? ms.ToString(CultureInfo.InvariantCulture) + " ms"
        : (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
