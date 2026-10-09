namespace Neelam.Campaigns;

/// <summary>
/// What the checks know about the business sending the email, from its registration in the
/// clients table: its name and the operator's description of it, which the proofread takes as
/// background for judging names, services and facts, never instructions; and the phone numbers it
/// may publish, against which the rules check every number in the email.
/// </summary>
public sealed record BusinessContext(string Name, string? Description = null)
{
    /// <summary>The numbers the business has registered; none means the numbers are not checked.</summary>
    public PhoneNumbers Phones { get; init; } = PhoneNumbers.None;
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
    public static async Task<ReviewReport> ReviewAsync(
        Campaign campaign,
        IProofreader proofreader,
        IReadOnlyCollection<Dismissal>? dismissals = null,
        CampaignPolicy? policy = null,
        BusinessContext? business = null,
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
        return new ReviewReport(campaign, findings, Proofread: true);
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
