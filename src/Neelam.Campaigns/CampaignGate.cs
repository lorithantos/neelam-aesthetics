namespace Neelam.Campaigns;

/// <summary>
/// What the checks know about the business sending the email, from its registration in the
/// clients table: its name and the operator's description of it, which the proofread takes as
/// background for judging names, services and facts, never instructions; and the phone numbers it
/// may publish, against which the rules check every number in the email; and its known items,
/// against which the rules check treatment names and benefit lines for near misses; and the photos
/// in its image library, so a photo the library does not hold is not noted twice.
/// </summary>
public sealed record BusinessContext(string Name, string? Description = null)
{
    /// <summary>The numbers the business has registered; none means the numbers are not checked.</summary>
    public PhoneNumbers Phones { get; init; } = PhoneNumbers.None;

    /// <summary>
    /// The client's known items; none means nothing is checked against them. Never given to the
    /// proofread, which is told only the name and description.
    /// </summary>
    public KnownItems Known { get; init; } = KnownItems.None;

    /// <summary>
    /// The names of the photos in the client's image library. A photo the library does not hold
    /// already has its own note beside its field and in the preview, so the rules say nothing more
    /// about it. Null when the library was not read: then every photo is checked.
    /// </summary>
    public IReadOnlyCollection<string>? LibraryPhotos { get; init; }

    /// <summary>
    /// Whether the library holds a photo by this name, matched as the library finds one: ignoring
    /// case and the spaces around it. True when the library was not read.
    /// </summary>
    public bool InLibrary(string name) =>
        LibraryPhotos is null || LibraryPhotos.Any(p => string.Equals(p.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads an email the way a careful editor would and reports mistakes.</summary>
public interface IProofreader
{
    /// <summary>
    /// Findings about the email as it will be sent. Throws when the proofread could not run;
    /// the gate turns that into a blocker rather than letting the email through unread.
    /// </summary>
    /// <param name="business">Who is sending it, when known.</param>
    Task<IReadOnlyList<Finding>> ProofreadAsync(
        Campaign campaign, BusinessContext? business, CancellationToken cancellationToken = default);
}

/// <summary>
/// The full review: rule checks plus the AI proofread. This is the only way to obtain a report
/// that allows export.
/// </summary>
public static class CampaignGate
{
    /// <param name="warningsSeen">
    /// Who was shown this save's "Worth a look" findings at export and went on, as recorded with its
    /// approval; null when nobody has, and then a report with any such finding does not export.
    /// </param>
    public static async Task<ReviewReport> ReviewAsync(
        Campaign campaign,
        IProofreader proofreader,
        IReadOnlyCollection<Dismissal>? dismissals = null,
        CampaignPolicy? policy = null,
        BusinessContext? business = null,
        WarningsSeen? warningsSeen = null,
        CancellationToken cancellationToken = default)
    {
        var findings = CampaignReview.Check(campaign, policy, business).Findings.ToList();

        IReadOnlyList<Finding> ai;
        try
        {
            ai = await proofreader.ProofreadAsync(campaign, business, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: an email nobody proofread does not go out by default.
            ai = [new Finding(Severity.Blocker, "ai-unavailable", "Whole email",
                $"The AI proofread could not run ({ex.Message}). Retry, or dismiss to send without it.",
                Excerpt: "")];
        }

        findings.AddRange(ai.Select(f => ApplyDismissals(f, dismissals)));
        return new ReviewReport(campaign, findings, Proofread: true) { WarningsSeen = warningsSeen };
    }

    /// <summary>
    /// The demo site's review, for a campaign a person has approved: the rule checks alone, with the
    /// approval standing in for the AI proofread while it is not switched on. The report says it was
    /// not proofread (<see cref="ReviewReport.Proofread"/> is false) and carries the approval; it
    /// allows export only when the rules find nothing blocking. The web app calls this only in
    /// Prototype access mode, which runs only on the test site; Enforced, as in production, never
    /// does, so there export still needs <see cref="ReviewAsync"/> and the proofread. Whether its
    /// "Worth a look" findings were shown at export is the approval's
    /// (<see cref="Approval.WarningsSeen"/>), since both belong to the one save.
    /// </summary>
    public static ReviewReport DemoReview(
        Campaign campaign, Approval approval, CampaignPolicy? policy = null, BusinessContext? business = null)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return CampaignReview.Check(campaign, policy, business) with
        {
            DemoApproval = approval,
            WarningsSeen = approval.WarningsSeen,
        };
    }

    private static Finding ApplyDismissals(Finding f, IReadOnlyCollection<Dismissal>? dismissals)
    {
        if (!f.IsDismissable || dismissals is null) return f;
        var d = dismissals.FirstOrDefault(d => d.Rule == f.Rule && d.Excerpt == (f.Excerpt ?? ""));
        return d is null
            ? f
            : f with
            {
                Severity = Severity.Warning,
                Message = $"{f.Message} [Dismissed by {d.DismissedBy}: {d.Reason}]",
            };
    }
}
