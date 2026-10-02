namespace Neelam.Campaigns;

public enum Severity
{
    /// <summary>Worth a look; does not stop export.</summary>
    Warning,

    /// <summary>The email cannot be exported until this is fixed.</summary>
    Blocker,
}

/// <param name="Rule">Stable rule id, e.g. "tier-names-unique".</param>
/// <param name="Location">Where in the email, e.g. "Offer › Tier 2".</param>
public sealed record Finding(Severity Severity, string Rule, string Location, string Message);

public sealed record ReviewReport(IReadOnlyList<Finding> Findings)
{
    public IEnumerable<Finding> Blockers => Findings.Where(f => f.Severity == Severity.Blocker);

    public IEnumerable<Finding> Warnings => Findings.Where(f => f.Severity == Severity.Warning);

    /// <summary>Export is allowed only when nothing blocks it.</summary>
    public bool CanExport => !Blockers.Any();
}
