using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Templates define a campaign's structure as data. These pin what the membership email alone
/// could not: another kind of email works with no code change, and each block type brings exactly
/// its own checks.
/// </summary>
public class TemplateBlockTests
{
    private static readonly SignOff Team = new("Warmly,", "The Studio Team");

    // A kind of email the code was never shaped around: a thank-you note with no offer and no button.
    private static readonly CampaignTemplate ThankYou = new("Thank-you note",
    [
        new TemplateBlock("Headline", BlockType.Heading),
        new TemplateBlock("Body", BlockType.Paragraphs),
        new TemplateBlock("Sign-off", BlockType.SignOff, Fixed: new SignOffBlock("Sign-off", Team)),
    ]);

    [Fact]
    public void Another_kind_of_email_is_a_template_not_a_code_change()
    {
        var d = ThankYou.Start();
        d.Subject.Set("Thank you");
        d.Text("Headline").Set("Thank you for a wonderful year");
        d.Paragraphs("Body").Set(["We could not have done it without you."]);

        var result = d.Build();

        Assert.True(result.Succeeded, string.Join("; ", result.Problems.Select(p => p.Message)));
        Assert.Equal(["Headline", "Body", "Sign-off"], result.Campaign!.Blocks.Select(b => b.Label));
        // No offer, so no offer checks and no demand for a button.
        Assert.Empty(CampaignReview.Check(result.Campaign).Blockers);
    }

    [Fact]
    public void An_offer_with_no_button_is_still_blocked() =>
        Assert.Contains(CampaignReview.Check(SampleCampaigns.Corrected().Without("Call to action")).Blockers,
            f => f.Rule == "cta-required" && f.Location == "Offer");

    [Fact]
    public void Each_offer_is_checked_under_its_own_label()
    {
        var corrected = SampleCampaigns.Corrected();
        var offer = corrected.OfferOf();
        var c = corrected.With(new OfferBlock("Spring offer", offer with { Tiers = [offer.Tiers[0], offer.Tiers[0]] }));

        var blockers = CampaignReview.Check(c).Blockers.Where(f => f.Rule == "tier-content-distinct").ToList();

        Assert.Equal(["Spring offer › Tier 2"], blockers.Select(f => f.Location));
    }

    [Fact]
    public void Any_fine_print_block_answers_the_medical_disclaimer_rule()
    {
        var withoutDisclaimer = SampleCampaigns.Corrected().Without("Disclaimer");
        Assert.Contains(CampaignReview.Check(withoutDisclaimer).Blockers, f => f.Rule == "medical-disclaimer");

        var withOtherFinePrint = withoutDisclaimer.With(new FinePrintBlock("Small print", "Consultation required; results vary."));
        Assert.DoesNotContain(CampaignReview.Check(withOtherFinePrint).Blockers, f => f.Rule == "medical-disclaimer");
    }

    [Fact]
    public void An_optional_offer_nobody_starts_is_left_out()
    {
        var t = new CampaignTemplate("Newsletter",
        [
            new TemplateBlock("Headline", BlockType.Heading),
            new TemplateBlock("Offer", BlockType.Offer, Required: false),
        ]);
        var d = t.Start();
        d.Subject.Set("News");
        d.Text("Headline").Set("This month");

        var campaign = d.Build().Campaign!;

        Assert.Empty(campaign.BlocksOf<OfferBlock>());
    }

    [Fact]
    public void A_template_refuses_what_would_make_findings_ambiguous_or_offers_stale()
    {
        Assert.Throws<ArgumentException>(() => new CampaignTemplate("Twice",
            [new TemplateBlock("Body", BlockType.Paragraphs), new TemplateBlock("body", BlockType.Paragraphs)]));
        Assert.Throws<ArgumentException>(() => new CampaignTemplate("Stale offer",
            [new TemplateBlock("Offer", BlockType.Offer, Fixed: new OfferBlock("Offer", SampleCampaigns.Corrected().OfferOf()))]));
        Assert.Throws<ArgumentException>(() => new CampaignTemplate("Mismatch",
            [new TemplateBlock("Greeting", BlockType.Greeting, Fixed: new HeadingBlock("Greeting", "Hello"))]));
        Assert.Throws<ArgumentException>(() => new CampaignTemplate("Empty", []));
    }

    [Fact]
    public void A_block_is_reached_by_its_label_and_type()
    {
        var d = ThankYou.Start();

        Assert.Throws<KeyNotFoundException>(() => d.Text("Offer"));
        Assert.Throws<InvalidOperationException>(() => d.Text("Body"));
    }
}
