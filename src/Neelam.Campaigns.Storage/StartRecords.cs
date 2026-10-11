using System.Text.Json;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// One start of the app and what its first minutes were like (owner, 2026-10-10: "We can add the
/// measurement to help make certain there are no outages"). Operator data: build stamps, route
/// templates, status codes and times. Never a client's name, an id from an address, or anything she wrote.
/// </summary>
/// <param name="Id">Sortable by start time: <c>yyyyMMddTHHmmssfffZ</c> and a short random tail, so two instances started in the same millisecond never share one.</param>
/// <param name="Build">The deploy's build stamp (LATEST_BUILD_INFO), or null where there is none, as in a local run.</param>
/// <param name="Cause">"new build" when the build stamp differs from the start before; otherwise "unknown" (a restart, a setting changed, a platform recycle: the app cannot tell them apart).</param>
/// <param name="Version">How many times this start's record has been written; the newest is the one kept.</param>
/// <param name="Warmup">How the warm-up went, once it has finished.</param>
/// <param name="FirstRequest">The first request after the start that was not the warm-up's own.</param>
/// <param name="FirstVisits">Each page's first visit after the start, in the order they came.</param>
/// <param name="Held">Requests shown the getting-ready page while the warm-up ran; null when none were.</param>
public sealed record StartRecord(
    string Id,
    DateTimeOffset StartedAt,
    string? Build,
    string Cause,
    int Version,
    WarmupOutcome? Warmup,
    FirstRequest? FirstRequest,
    IReadOnlyList<PageVisit> FirstVisits,
    HeldRequests? Held = null)
{
    /// <summary>What the owner calls sluggish: "a second or more feels sluggish and unprofessional".</summary>
    public const long SluggishMs = 1000;

    /// <summary>The slowest first visit to a page, or null before any.</summary>
    public PageVisit? SlowestVisit => FirstVisits.OrderByDescending(v => v.Ms).FirstOrDefault();

    /// <summary>Whether any first visit took a second or more.</summary>
    public bool Sluggish => FirstVisits.Any(v => v.Ms >= SluggishMs);
}

/// <summary>The warm-up's outcome: its total time, how many of its steps were answered, and its slowest step.</summary>
public sealed record WarmupOutcome(long TotalMs, int Answered, int Steps, string? SlowestStep, long SlowestMs);

/// <summary>A request after a start: when, the route as declared (never the address asked for), how long, and its status.</summary>
public sealed record FirstRequest(DateTimeOffset At, string Route, long Ms, int Status);

/// <summary>A page's first visit after a start: its route as declared, when, how long, and its status.</summary>
public sealed record PageVisit(string Route, DateTimeOffset At, long Ms, int Status);

/// <summary>
/// How many times the getting-ready page was shown after a start, when the first request it held
/// arrived, and the longest anyone can have waited: from that arrival until the warm-up finished (or
/// until now, while it still runs). Without a cookie two visitors cannot be told apart, so this is the
/// earliest held request's wait, which no one's exceeds.
/// </summary>
public sealed record HeldRequests(int Shown, DateTimeOffset FirstAt, long LongestWaitMs);

/// <summary>The record of the app's recent starts, kept for the operator.</summary>
public interface IStartRecords
{
    /// <summary>Keeps this version of a start's record, in place of the one before; the oldest starts beyond the limit go.</summary>
    Task SaveAsync(StartRecord record, CancellationToken ct = default);

    /// <summary>The newest <paramref name="count"/> starts, newest first. A record that cannot be read is left out.</summary>
    Task<IReadOnlyList<StartRecord>> RecentAsync(int count, CancellationToken ct = default);
}

/// <summary>
/// The starts in the <c>settings</c> container at <c>_starts/{start id}/{version}.json</c>, beside the
/// operator's standard baseline and ladders: operator data, never in a client's container. Blobs are
/// never overwritten (<see cref="IBlobBackend"/>), so a start's newer record is a new blob and the one
/// before is then deleted. Only the newest <see cref="Keep"/> starts are kept; older ones are deleted
/// outright, as nothing here is a client's save.
/// </summary>
public sealed class StartRecordStore : IStartRecords
{
    /// <summary>Where starts live in the settings container. No client name can start with an underscore.</summary>
    internal const string Prefix = "_starts";

    /// <summary>How many starts are kept unless told otherwise.</summary>
    public const int DefaultKeep = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IBlobBackend _settings;

    internal StartRecordStore(IBlobBackend settings, int keep = DefaultKeep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        _settings = settings;
        Keep = keep;
    }

    public int Keep { get; }

    public async Task SaveAsync(StartRecord record, CancellationToken ct = default)
    {
        if (record.Id.Length == 0 || record.Id.Contains('/')) throw new ArgumentException("A start id is one path segment.", nameof(record));
        var name = $"{Prefix}/{record.Id}/{record.Version:D6}.json";
        await _settings.TryCreateTextAsync(name, JsonSerializer.Serialize(record, Json), new Dictionary<string, string>(), ct);

        var entries = await ListAsync(ct);
        foreach (var older in entries.Where(e => StartOf(e) == record.Id && string.CompareOrdinal(e, name) < 0))
            await _settings.DeleteAsync(older, ct);
        var starts = entries.Select(StartOf).Distinct(StringComparer.Ordinal).OrderDescending(StringComparer.Ordinal).ToList();
        foreach (var gone in starts.Skip(Keep).ToHashSet(StringComparer.Ordinal))
            foreach (var blob in entries.Where(e => StartOf(e) == gone))
                await _settings.DeleteAsync(blob, ct);
    }

    public async Task<IReadOnlyList<StartRecord>> RecentAsync(int count, CancellationToken ct = default)
    {
        var newest = (await ListAsync(ct))
            .GroupBy(StartOf, StringComparer.Ordinal)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Take(count)
            .Select(g => g.Max(StringComparer.Ordinal)!)
            .ToList();
        var records = new List<StartRecord>(newest.Count);
        foreach (var blob in newest)
        {
            try
            {
                if (JsonSerializer.Deserialize<StartRecord>(await _settings.ReadTextAsync(blob, ct), Json) is { } record)
                    records.Add(record with { FirstVisits = record.FirstVisits ?? [] });
            }
            catch (JsonException)
            {
                // Unreadable: left out, as the interface says; the next write of a start replaces it.
            }
        }
        return records;
    }

    private async Task<List<string>> ListAsync(CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var entry in _settings.ListAsync(Prefix + "/", ct))
            if (entry.Name.Split('/') is [Prefix, _, _]) names.Add(entry.Name);
        return names;
    }

    private static string StartOf(string blob) => blob.Split('/')[1];
}
