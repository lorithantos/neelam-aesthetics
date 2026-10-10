namespace Neelam.Campaigns;

/// <summary>
/// What the checks know about the business sending the email, from its registration in the
/// clients table: its name and the operator's description of it, which the proofread takes as
/// background for judging names, services and facts, never instructions; and the phone numbers it
/// may publish, against which the rules check every number in the email; and its known items,
/// against which the rules check treatment names and benefit lines for near misses; and the photos
/// in its image library, so a photo the library does not hold is not noted twice; and the tier-name
/// ladders its tier names are read with.
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
    /// The tier-name ladders in force for the client (its own, else the operator's standard), which the
    /// rules read tier names with. The standard ones in code until a page says otherwise; with no
    /// business at all, the rules use those too.
    /// </summary>
    public TierLadders Ladders { get; init; } = TierLadders.Standard;

    /// <summary>
    /// The names of the photos in the client's image library. A photo the library does not hold
    /// already has its own note beside its field and in the preview, so the rules say nothing more
    /// about it. Null when the library was not read: then every photo is checked.
    /// </summary>
    public IReadOnlyCollection<string>? LibraryPhotos { get; init; }

    /// <summary>
    /// Where Square holds the library's photos, by name (ignoring case), for the AI proofread to read
    /// the pictures themselves (owner, 2026-10-09: "we will allow the LLM to read the image when it is
    /// hooked up"). Only Square addresses; a photo with none is not read, and the proofread says so.
    /// Null when the library was not read. Never checked by the rules.
    /// </summary>
    public IReadOnlyDictionary<string, Uri>? PhotoAddresses { get; init; }

    /// <summary>
    /// Where the AI's readings of her photos are kept, in her own container, so a photo it has read
    /// before is given as its reading rather than sent again. Null when none are kept: then every
    /// photo is sent. Never checked by the rules.
    /// </summary>
    public IPhotoReadings? PhotoReadings { get; init; }

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
    /// Findings about the email as it will be sent. Throws when the proofread could not run, a
    /// <see cref="ProofreadUnavailableException"/> saying why in plain words; the gate turns that
    /// into a blocker rather than letting the email through unread.
    /// </summary>
    /// <param name="business">Who is sending it, when known.</param>
    Task<ProofreadResult> ProofreadAsync(
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
            ai = (await proofreader.ProofreadAsync(campaign, business, cancellationToken)).Findings;
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
    /// <param name="proofread">
    /// The AI proofread of this exact save, when she asked for one (owner, 2026-10-10: on the demo it
    /// runs on her click, for a saved version): its findings join the rules' and count as theirs do,
    /// a Must fix stopping the export and each "Worth a look" shown before it, and the report says it
    /// was proofread. Null when this version has not been proofread: then the report says it was not.
    /// </param>
    public static ReviewReport DemoReview(
        Campaign campaign, Approval approval, CampaignPolicy? policy = null, BusinessContext? business = null,
        IReadOnlyList<Finding>? proofread = null)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var rules = CampaignReview.Check(campaign, policy, business);
        return rules with
        {
            Findings = proofread is null ? rules.Findings : [.. rules.Findings, .. proofread],
            Proofread = proofread is not null,
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
