namespace Neelam.Campaigns;

public enum Severity
{
    /// <summary>Worth a look; does not stop export.</summary>
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
    /// Export needs both halves: the rule checks and the AI proofread, with nothing blocking.
    /// A rules-only report never allows export, except the demo's: approved by a person, with
    /// nothing blocking, and marked as not proofread.
    /// </summary>
    public bool CanExport => (Proofread || DemoApproval is not null) && !Blockers.Any();
}
