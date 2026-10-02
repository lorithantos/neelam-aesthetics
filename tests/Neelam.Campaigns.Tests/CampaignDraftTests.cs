using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Templates and tier copy, replayed against the two real sends.</summary>
public class CampaignDraftTests
{
    private static readonly Campaign Target = SampleCampaigns.Corrected();

    /// <summary>A membership-announcement template made from the corrected email's fixed parts.</summary>
    private static readonly CampaignTemplate Membership = new(
        Name: "Membership announcement",
        Greeting: Target.Greeting,
        Closing: Target.Closing,
        SignOff: Target.SignOff,
        Disclaimer: Target.Disclaimer,
        HasOffer: true,
        IsRecurring: true);

    private static ISet<string> Locations(DraftResult r, string rule) =>
        r.Problems.Where(p => p.Rule == rule).Select(p => p.Location).ToHashSet();

    /// <summary>Everything a campaign author types, apart from the tiers.</summary>
    private static CampaignDraft StartAndFillText()
    {
        var d = Membership.Start();
        d.Subject.Set(Target.Subject);
        d.Headline.Set(Target.Headline);
        d.Opening.Set(Target.Opening);
        d.CallToAction.Set(Target.CallToAction!);
        var o = d.Offer!;
        o.Name.Set(Target.Offer!.Name);
        o.Summary.Set(Target.Offer.Summary);
        o.TiersNote.Set(Target.Offer.TiersNote!);
        o.TermsUrl.Set(Target.Offer.TermsUrl!);
        return d;
    }

    private static TierDraft AddGold(OfferDraft o)
    {
        var gold = o.AddTier();
        gold.Name.Set("Gold Member");
        gold.MonthlyPrice.Set(149m);
        gold.AddBenefit(new BirthdayCredit(25m));
        gold.AddBenefit(new PercentOff(5, "any qualifying treatments"));
        gold.AddBenefit(new DiscountedItem(50, "wellness injection", "per visit"));
        return gold;
    }

    [Fact]
    public void New_draft_from_a_template_asks_for_every_per_campaign_part_and_nothing_else()
    {
        var result = Membership.Start().Build();

        Assert.False(result.Succeeded);
        Assert.Equal(
            new HashSet<string>
            {
                "Subject", "Headline", "Opening", "Call to action",
                "Offer › Name", "Offer › Summary", "Offer › Terms link", "Offer › Tiers",
            },
            Locations(result, "draft-missing"));
    }

    [Fact]
    public void Copied_tier_keeps_benefits_but_not_name_or_price()
    {
        var o = Membership.Start().Offer!;
        AddGold(o);

        var copy = o.CopyTier(0);

        Assert.False(copy.Name.HasValue);
        Assert.False(copy.MonthlyPrice.HasValue);
        Assert.Equal(3, copy.Benefits.Count);
        Assert.All(copy.Benefits, b => Assert.Equal(Origin.Copied, b.Origin));
        Assert.All(copy.Benefits, b => Assert.Equal("Tier 1", b.CopiedFrom));
    }

    /// <summary>
    /// Second send, replayed: tier 2 started as a copy, some benefits were updated, the rest and
    /// the name were left as they were. The draft will not build, and says exactly what is left.
    /// </summary>
    [Fact]
    public void Second_send_replayed_cannot_build_with_an_unnamed_tier_and_unreviewed_benefits()
    {
        var d = StartAndFillText();
        AddGold(d.Offer!);
        var platinum = d.Offer!.CopyTier(0);
        platinum.MonthlyPrice.Set(299m);
        platinum.Benefits[0].Set(new BirthdayCredit(75m));   // updated
        platinum.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));   // updated
        // Benefit 3 and the name never touched.

        var result = d.Build();

        Assert.False(result.Succeeded);
        Assert.Equal(new HashSet<string> { "Offer › Tier 2 › Name" }, Locations(result, "draft-missing"));
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
        var d = StartAndFillText();
        AddGold(d.Offer!);
        var platinum = d.Offer!.CopyTier(0);
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
        var d = StartAndFillText();
        AddGold(d.Offer!);
        var platinum = d.Offer!.CopyTier(0);
        platinum.Name.Set("Platinum Member");
        platinum.MonthlyPrice.Set(299m);
        platinum.Benefits[0].Set(new BirthdayCredit(75m));
        platinum.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));
        platinum.Benefits[2].Set(new FreeItem(1, "wellness injection", "per visit"));
        platinum.AddBenefit(new DiscountedItem(50, "wellness injection", "per visit", "any additional"));

        var result = d.Build();

        Assert.True(result.Succeeded, string.Join("\n", result.Problems.Select(p => p.Message)));
        Assert.Equal(EditorExport.Preview(Target), EditorExport.Preview(result.Campaign!));
        Assert.True((await CampaignGate.ReviewAsync(result.Campaign!, FakeProofreader.Clean)).CanExport);
    }

    [Fact]
    public void Only_a_copied_value_can_be_confirmed()
    {
        var gold = AddGold(Membership.Start().Offer!);

        Assert.Throws<InvalidOperationException>(() => gold.Benefits[0].Confirm());
        Assert.Throws<InvalidOperationException>(() => gold.Name.Confirm());
    }
}
