namespace Neelam.Campaigns.Storage;

/// <summary>
/// How forgiving Undo is, from the <c>Undo</c> section of the app's settings. Undo marks a save
/// rather than deleting it; for the grace period the save can be restored, and after it the sweep
/// deletes the save for good, leaving no record.
/// </summary>
public sealed class UndoOptions
{
    public const string Section = "Undo";

    /// <summary>
    /// How long an undone save can still be restored. No default on purpose: the setting
    /// (<c>Undo:GracePeriod</c>, such as <c>1.00:00:00</c>) is required, and the app refuses to
    /// start without a positive one.
    /// </summary>
    public TimeSpan GracePeriod { get; set; }

    /// <summary>How often the sweep looks for saves whose grace period has passed. Hourly unless set.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The longest <see cref="SweepInterval"/> allowed. The sweep's timer cannot wait longer than
    /// about 49.7 days (2^32 - 2 milliseconds), nor less than a millisecond, and throws if asked
    /// to, which would stop the app.
    /// </summary>
    public static readonly TimeSpan LongestSweepInterval = TimeSpan.FromDays(49);

    /// <summary>Why these settings cannot run, or null when they can.</summary>
    public string? Problem() =>
        GracePeriod <= TimeSpan.Zero ? "Undo:GracePeriod must be set to a positive time span, such as 1.00:00:00."
        : SweepInterval < TimeSpan.FromMilliseconds(1)
            ? "Undo:SweepInterval must be a positive time span of at least a millisecond, such as 01:00:00."
        : SweepInterval > LongestSweepInterval
            ? "Undo:SweepInterval must be at most 49.00:00:00 (49 days), the longest the sweep's timer can wait."
        : null;
}
