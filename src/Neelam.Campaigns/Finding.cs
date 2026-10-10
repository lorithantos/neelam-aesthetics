namespace Neelam.Campaigns;

public enum Severity
{
    /// <summary>Worth a look; shown to a person at export, who may go on (never a fix required).</summary>
    Warning,

    /// <summary>The email cannot be exported until this is fixed (or, for AI findings, dismissed).</summary>
    Blocker,
}

/// <param name="Rule">Stable rule id, e.g. "tier-names-unique". AI findings start with "ai-".</param>
/// <param name="Location">Where in the email, e.g. "Offer › Tier 2".</param>
/// <param name="Excerpt">The exact offending text, when the rule can point at it.</param>
public sealed record Finding(
    Severity Severity, string Rule, string Location, string Message, string? Excerpt = null)
{
    /// <summary>Only AI findings can be dismissed by a person; rule-based blockers must be fixed.</summary>
    public bool IsDismissable => Rule.StartsWith("ai-", StringComparison.Ordinal);

    /// <summary>
    /// The id of her known benefit line this finding holds an amount to, when it is about that line's
    /// limits (owner, 2026-10-09: the caps "easy to find and update"), so the page can link straight
    /// to it (<see cref="FindingPlace.KnownLineLink"/>). An id only, never the line's content; null on
    /// every other finding. Not part of <see cref="SeenKey"/>, and never exported: the assistant JSON
    /// writes a finding as its place and message alone.
    /// </summary>
    public string? KnownLine { get; init; }

    /// <summary>
    /// What says she has been shown this finding at export (owner, 2026-10-09): a hash of its rule,
    /// place and message, so the same finding has the same key on every visit and a changed one a new
    /// key. Only the hash is ever stored: never the finding's text.
    /// </summary>
    public string SeenKey =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{Rule}\n{Location}\n{Message}")))[..32];
}

/// <summary>A person's recorded decision that an AI finding is wrong or acceptable.</summary>
/// <param name="Excerpt">Matches the finding's excerpt, so the dismissal survives re-runs.</param>
public sealed record Dismissal(string Rule, string Excerpt, string Reason, string DismissedBy);

/// <param name="Campaign">The exact campaign instance this report is about.</param>
/// <param name="Proofread">True when the AI proofread ran as part of this review.</param>
public sealed record ReviewReport(Campaign Campaign, IReadOnlyList<Finding> Findings, bool Proofread)
{
    public IEnumerable<Finding> Blockers => Findings.Where(f => f.Severity == Severity.Blocker);

    public IEnumerable<Finding> Warnings => Findings.Where(f => f.Severity == Severity.Warning);

    /// <summary>
    /// The demo site's exception, and only that: the approval that stands in for the AI proofread
    /// while it is not switched on. Set by <see cref="CampaignGate.DemoReview"/> alone (it cannot be
    /// set outside this library), and never together with <see cref="Proofread"/>, so a report it
    /// unlocks still says plainly that nobody proofread the email. Null on every other report.
    /// </summary>
    public Approval? DemoApproval { get; internal init; }

    /// <summary>
    /// Who was shown this version's "Worth a look" findings at export and went on, and when; null
    /// until someone has. Set by <see cref="CampaignGate"/> alone (it cannot be set outside this
    /// library), from what was recorded for the save the report is of.
    /// </summary>
    public WarningsSeen? WarningsSeen { get; internal init; }

    /// <summary>
    /// The "Worth a look" findings nobody has been shown at export for this version: every one whose
    /// <see cref="Finding.SeenKey"/> is not among those recorded (owner, 2026-10-09: warnings that
    /// appear after she went on, because her known items, numbers or a check's data changed, are
    /// shown then). With nothing recorded, or a record from before keys were kept, all of them.
    /// </summary>
    public IEnumerable<Finding> UnseenWarnings =>
        Warnings.Where(w => WarningsSeen?.Keys is not { } seen || !seen.Contains(w.SeenKey));

    /// <summary>
    /// True while the report has "Worth a look" findings nobody has been shown at export yet: the one
    /// thing a report that otherwise passes still waits for. Never true of a report with a blocker,
    /// whose findings are for fixing first.
    /// </summary>
    public bool WarningsToSee =>
        (Proofread || DemoApproval is not null) && !Blockers.Any() && UnseenWarnings.Any();

    /// <summary>
    /// Export needs both halves: the rule checks and the AI proofread, with nothing blocking.
    /// A rules-only report never allows export, except the demo's: approved by a person, with
    /// nothing blocking, and marked as not proofread. Either way, every "Worth a look" finding has
    /// been shown to a person at export first, who chose to go on (owner, 2026-10-09: warnings are
    /// handholding, not handcuffs, so going on is one click, never a fix); one that appears later
    /// is shown in its turn.
    /// </summary>
    public bool CanExport =>
        (Proofread || DemoApproval is not null) && !Blockers.Any() && !UnseenWarnings.Any();
}

/// <summary>
/// A person was shown a saved version's "Worth a look" findings when exporting it, and chose to go
/// on. It belongs to that save, as its approval does: a new save (other than one that changes only
/// her label) is shown its findings again. Who went on last and when, and which findings had been
/// shown by then as their keys (<see cref="Finding.SeenKey"/>, hashes), never what they said.
/// </summary>
/// <param name="By">Who went on, the last time. In Prototype, the name typed for the approval; from the sign-in once there is one.</param>
/// <param name="At">When, in UTC.</param>
/// <param name="Keys">
/// The keys of every warning shown by then. Null for a record from before keys were kept: then no
/// warning counts as shown, so the list comes up once more.
/// </param>
public sealed record WarningsSeen(string By, DateTimeOffset At, IReadOnlySet<string>? Keys = null)
{
    /// <summary>Who went on and when, having been shown <paramref name="warnings"/>.</summary>
    public static WarningsSeen Of(string by, DateTimeOffset at, IEnumerable<Finding> warnings) =>
        new(by, at, warnings.Select(w => w.SeenKey).ToHashSet(StringComparer.Ordinal));

    public bool Equals(WarningsSeen? other) =>
        other is not null && By == other.By && At == other.At
        && (Keys is null ? other.Keys is null : other.Keys is not null && Keys.SetEquals(other.Keys));

    public override int GetHashCode() => HashCode.Combine(By, At, Keys?.Count);
}
