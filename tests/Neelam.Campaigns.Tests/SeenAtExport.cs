namespace Neelam.Campaigns.Tests;

/// <summary>
/// Going on past the warnings at export, for tests whose subject is what comes after: since
/// 2026-10-09 that records which warnings were shown (their keys), so a test says which campaign's
/// warnings she saw.
/// </summary>
internal static class SeenAtExport
{
    /// <summary>The keys of every warning the rules give the campaign: what the page lists before export.</summary>
    public static IReadOnlyCollection<string> KeysOf(Campaign campaign) =>
        CampaignReview.Check(campaign).Warnings.Select(w => w.SeenKey).ToList();

    /// <summary>The same, for a draft as built.</summary>
    public static IReadOnlyCollection<string> KeysOf(CampaignDraft draft) =>
        KeysOf(draft.Build().Campaign ?? throw new InvalidOperationException("The draft does not build."));

    /// <summary>The approval, with every warning the rules give the campaign shown and gone on past.</summary>
    public static Approval Seeing(this Approval approval, Campaign campaign, BusinessContext? business = null) =>
        approval with
        {
            WarningsSeen = WarningsSeen.Of(approval.WarningsSeen?.By ?? approval.By, approval.WarningsSeen?.At ?? approval.At,
                CampaignReview.Check(campaign, business: business).Warnings),
        };

    /// <summary>The same person and time, having been shown every warning the rules give the campaign.</summary>
    public static WarningsSeen For(this WarningsSeen seen, Campaign campaign, BusinessContext? business = null) =>
        WarningsSeen.Of(seen.By, seen.At, CampaignReview.Check(campaign, business: business).Warnings);

    /// <summary>Who went on and when, having been shown every warning of the report.</summary>
    public static WarningsSeen Saw(this ReviewReport report, string by, DateTimeOffset at) =>
        WarningsSeen.Of(by, at, report.Warnings);
}
