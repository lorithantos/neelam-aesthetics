using System.Globalization;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// The date/time stamp every save is named by, <c>yyyyMMddTHHmmss.fffffffZ</c>, and the way a new
/// save finds a free name. Shared by campaign saves and every per-client document, so they cannot
/// drift apart.
/// </summary>
internal static class SaveStamp
{
    private const string Format = "yyyyMMdd'T'HHmmss'.'fffffff'Z'";
    private const int MaxNameAttempts = 5;

    public static string Of(DateTimeOffset at) => at.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Reads a stamp back from a blob's file name (<c>{stamp}.json</c>).</summary>
    public static bool TryRead(string fileName, out DateTimeOffset at)
    {
        at = default;
        if (!fileName.EndsWith(".json", StringComparison.Ordinal))
            return false;
        if (!DateTime.TryParseExact(fileName[..^".json".Length], Format, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        at = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }

    /// <summary>
    /// Writes a new blob named for <paramref name="at"/>. Two saves in the same 100ns tick would
    /// share a name; the later one moves on a tick rather than replacing the earlier one, because
    /// nothing is ever overwritten.
    /// </summary>
    public static async Task<(string Name, DateTimeOffset At)> CreateAsync(
        IBlobBackend blobs, Func<DateTimeOffset, string> nameFor, string json,
        IReadOnlyDictionary<string, string> metadata, DateTimeOffset at, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxNameAttempts; attempt++, at = at.AddTicks(1))
        {
            var name = nameFor(at);
            if (await blobs.TryCreateTextAsync(name, json, metadata, ct))
                return (name, at);
        }
        throw new IOException($"Could not find a free name for a save after {MaxNameAttempts} attempts.");
    }
}
