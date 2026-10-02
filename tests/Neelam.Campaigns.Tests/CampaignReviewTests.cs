using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class CampaignReviewTests
{
    private static string[] Rules(ReviewReport r, Severity s) =>
        r.Findings.Where(f => f.Severity == s).Select(f => f.Rule).Distinct().Order().ToArray();

    [Fact]
    public void As_sent_email_is_blocked_for_every_known_failure()
    {
        var report = CampaignReview.Check(SampleCampaigns.AsSent());

        Assert.False(report.CanExport);
        Assert.Equal(
            ["cta-required", "medical-disclaimer", "terms-required", "tier-names-unique"],
            Rules(report, Severity.Blocker));
    }

    [Fact]
    public void As_sent_email_warns_on_wording()
    {
        var warnings = Rules(CampaignReview.Check(SampleCampaigns.AsSent()), Severity.Warning);

        Assert.Contains("restricted-term", warnings);   // "Bank", "savings account"
        Assert.Contains("emoji-spacing", warnings);     // "Beautiful🤍", "✨The"
        Assert.Contains("tiers-parallel", warnings);    // only tier 2 has a free injection
    }

    [Fact]
    public void Duplicate_tier_name_is_reported_once_naming_both_tiers()
    {
        var finding = Assert.Single(
            CampaignReview.Check(SampleCampaigns.AsSent()).Findings,
            f => f.Rule == "tier-names-unique");

        Assert.Contains("Tiers 1 and 2", finding.Message);
        Assert.Contains("Platinum Member", finding.Message);
    }

    [Fact]
    public void Corrected_email_passes_with_only_the_owners_naming_call_left()
    {
        var report = CampaignReview.Check(SampleCampaigns.Corrected());

        Assert.True(report.CanExport, string.Join("\n", report.Blockers.Select(b => b.Message)));
        Assert.Equal(
            ["restricted-term", "tiers-parallel"],
            Rules(report, Severity.Warning));
    }

    [Fact]
    public void Repeated_sentence_across_sections_is_flagged()
    {
        var c = SampleCampaigns.Corrected() with
        {
            Opening = [.. SampleCampaigns.Corrected().Opening,
                "100% of your monthly contribution goes toward any treatments you choose."],
        };

        var finding = Assert.Single(CampaignReview.Check(c).Findings, f => f.Rule == "repeated-phrase");
        Assert.Equal("Offer › Tiers note", finding.Location);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(0)]
    public void Discount_outside_1_to_99_percent_is_blocked(int percent)
    {
        var c = WithTier1Benefit(new DiscountedItem(percent, "wellness injection", "per visit"));

        Assert.Contains(CampaignReview.Check(c).Blockers, f => f.Rule == "benefit-value");
    }

    [Fact]
    public void Tier_cheaper_than_the_one_before_is_blocked()
    {
        var offer = SampleCampaigns.Corrected().Offer!;
        var c = SampleCampaigns.Corrected() with
        {
            Offer = offer with { Tiers = [offer.Tiers[1], offer.Tiers[0]] },
        };

        Assert.Contains(CampaignReview.Check(c).Blockers, f => f.Rule == "tier-prices-increase");
    }

    [Fact]
    public void Non_https_call_to_action_is_blocked()
    {
        var c = SampleCampaigns.Corrected() with
        {
            CallToAction = new CallToAction("Join", new Uri("http://example.com/join")),
        };

        Assert.Contains(CampaignReview.Check(c).Blockers, f => f.Rule == "cta-https");
    }

    [Fact]
    public void Too_many_emoji_is_a_warning()
    {
        var c = SampleCampaigns.Corrected() with { Headline = "WE’RE TURNING ONE! 🥂✨🎉🎂💖🤍🤍🤍" };

        Assert.Contains(CampaignReview.Check(c).Warnings, f => f.Rule == "emoji-budget");
    }

    private static Campaign WithTier1Benefit(Benefit benefit)
    {
        var c = SampleCampaigns.Corrected();
        var tiers = c.Offer!.Tiers.ToList();
        tiers[0] = tiers[0] with { Benefits = [.. tiers[0].Benefits, benefit] };
        return c with { Offer = c.Offer with { Tiers = tiers } };
    }
}
