using System.Globalization;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// The mark Undo leaves on a save: the time it was undone, in that blob's own metadata, so there is
/// no index or list of undone saves. A marked save is left out of every list; until the grace period
/// has passed the mark can be cleared again (restore), and after it the sweep deletes the blob. One
/// mark for drafts and templates (<see cref="CampaignStore"/>) and for versioned documents such as a
/// look (<see cref="DocumentStore{T}"/>), so the sweep reads them all the same way.
/// </summary>
internal static class UndoMark
{
    internal const string Key = "undone";

    /// <summary>When the blob was undone, or null when it is in use. A mark that cannot be read is no mark: the save stays in use and is never swept.</summary>
    public static DateTimeOffset? Read(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue(Key, out var u)
        && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : null;

    /// <summary>The metadata with the mark set to <paramref name="at"/>, everything else kept.</summary>
    public static Dictionary<string, string> With(IReadOnlyDictionary<string, string> metadata, DateTimeOffset at) =>
        new(metadata) { [Key] = at.ToString("o", CultureInfo.InvariantCulture) };

    /// <summary>The metadata without the mark, everything else kept.</summary>
    public static Dictionary<string, string> Without(IReadOnlyDictionary<string, string> metadata) =>
        metadata.Where(m => m.Key != Key).ToDictionary(m => m.Key, m => m.Value);

    /// <summary>Whether a save undone at <paramref name="undoneAt"/> can still be restored: the grace period runs from the mark, and at its end the save belongs to the sweep.</summary>
    public static bool Restorable(DateTimeOffset? undoneAt, TimeSpan gracePeriod, DateTimeOffset now) =>
        undoneAt is { } at && now < at + gracePeriod;
}
