using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Templates and tier copy, replayed against the two real sends.</summary>
public class CampaignDraftTests
{
    private static readonly Campaign Target = SampleCampaigns.Corrected();
    private static readonly CampaignTemplate Membership = DraftFixtures.Membership;

    // Sorted, because xunit 2.4 compares sets in order (WIP.md).
    private static string[] Locations(DraftResult r, string rule) =>
        r.Problems.Where(p => p.Rule == rule).Select(p => p.Location).Order(StringComparer.Ordinal).ToArray();

    private static string[] Sorted(params string[] locations) => locations.Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void New_draft_from_a_template_asks_for_every_per_campaign_part_and_nothing_else()
    {
        var result = Membership.Start().Build();

        Assert.False(result.Succeeded);
        Assert.Equal(
            Sorted("Subject", "Headline", "Opening", "Call to action",
                "Offer › Name", "Offer › Summary", "Offer › Terms link", "Offer › Tiers"),
            Locations(result, "draft-missing"));
    }

    // The name and price are copied as they stand, not as unreviewed: the rules flag them while
    // they match (owner, 2026-10-09). Only the benefits wait to be looked at.
    [Fact]
    public void Copied_tier_keeps_its_name_and_price_and_marks_only_the_benefits_unreviewed()
    {
        var o = Membership.Start().Offer("Offer");
        DraftFixtures.AddGold(o);

        var copy = o.CopyTier(0);

        Assert.Equal(("Gold Member", Origin.Entered), (copy.Name.Value, copy.Name.Origin));
        Assert.Equal((149m, Origin.Entered), (copy.MonthlyPrice.Value, copy.MonthlyPrice.Origin));
        Assert.Equal(3, copy.Benefits.Count);
        Assert.All(copy.Benefits, b => Assert.Equal(Origin.Copied, b.Origin));
        Assert.All(copy.Benefits, b => Assert.Equal("Tier 1", b.CopiedFrom));
    }

    /// <summary>
    /// Second send, replayed: tier 2 started as a copy, some benefits were updated, the rest and
    /// the name were left as they were. The draft will not build, and says exactly what is left;
    /// the name both tiers share, the second send's mistake, is a rule's Must fix meanwhile.
    /// </summary>
    [Fact]
    public void Second_send_replayed_cannot_build_with_an_unreviewed_benefit_and_its_shared_name_is_flagged()
    {
        var d = DraftFixtures.SecondSendReplayed();

        var result = d.Build();

        Assert.False(result.Succeeded);
        Assert.Empty(Locations(result, "draft-missing"));
        Assert.Contains(CampaignEditor.Open(d).Status().Findings,
            f => f.Severity == Severity.Blocker && f.Rule == "tier-names-unique");
        var unreviewed = Assert.Single(result.Problems, p => p.Rule == "draft-unreviewed-copy");
        Assert.Equal("Offer › Tier 2 › Benefit 3", unreviewed.Location);
        Assert.Contains("copied from Tier 1", unreviewed.Message);
        Assert.Contains("50% off one wellness injection per visit", unreviewed.Message);
    }

    /// <summary>
    /// First send, replayed as deliberately as possible: someone confirms every copied benefit
    /// unchanged. The draft builds — confirming is a person's decision — but the rules still stop it.
    /// </summary>
    [Fact]
    public void First_send_replayed_with_every_copy_confirmed_is_still_stopped_by_the_rules()
    {
        var d = DraftFixtures.StartAndFillText();
        DraftFixtures.AddGold(d.Offer("Offer"));
        var platinum = d.Offer("Offer").CopyTier(0);
        platinum.Name.Set("Platinum Member");
        platinum.MonthlyPrice.Set(299m);
        foreach (var b in platinum.Benefits) b.Confirm();

        var result = d.Build();

        Assert.True(result.Succeeded);
        Assert.Contains(CampaignReview.Check(result.Campaign!).Blockers, f => f.Rule == "tier-content-distinct");
    }

    [Fact]
    public async Task Draft_filled_properly_builds_the_corrected_email_and_passes_the_gate()
    {
        var d = DraftFixtures.Finished();

        var result = d.Build();

        Assert.True(result.Succeeded, string.Join("\n", result.Problems.Select(p => p.Message)));
        Assert.Equal(EditorExport.Preview(Target), EditorExport.Preview(result.Campaign!));
        Assert.True((await CampaignGate.ReviewAsync(result.Campaign!, FakeProofreader.Clean)).CanExport);
    }

    [Fact]
    public void Only_a_copied_value_can_be_confirmed()
    {
        var gold = DraftFixtures.AddGold(Membership.Start().Offer("Offer"));

        Assert.Throws<InvalidOperationException>(() => gold.Benefits[0].Confirm());
        Assert.Throws<InvalidOperationException>(() => gold.Name.Confirm());
    }
}
