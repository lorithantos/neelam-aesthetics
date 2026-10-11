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

    public static string Cause(string cause) => cause switch
    {
        StartCause.NewBuild => "New build",
        _ => "Restart (cause not known)",
    };

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

    public static string Visit(PageVisit? visit) => visit is { } v ? $"{v.Route}, {Duration(v.Ms)}" : "None yet";

    /// <summary>"420 ms" under a second, "1.4 s" from a second.</summary>
    public static string Duration(long ms) => ms < 1000
        ? ms.ToString(CultureInfo.InvariantCulture) + " ms"
        : (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
