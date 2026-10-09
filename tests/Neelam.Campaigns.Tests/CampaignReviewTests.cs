using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class CampaignReviewTests
{
    private static string[] Rules(ReviewReport r, Severity s) =>
        r.Findings.Where(f => f.Severity == s).Select(f => f.Rule).Distinct().Order().ToArray();

    // The first send as it went out (checked against the sent email, 2026-10-09) named its options
    // "Option 1 Platinum Member" and "Option 2 Platinum Member". It is stopped for offering the same
    // option twice, at the same price. Its names are not the same, so tier-names-unique stays quiet;
    // they are the same apart from their numbers, which the owner (2026-10-09) made "a strongly worded
    // note", never a block: "Glow 50 and Glow 100 are perfectly good types of exceptions". Both stand
    // on the metals ladder's Platinum, so since the ladders (2026-10-09) the ladder's note says so in
    // place of the numbers' one: one mistake, one note.
    [Fact]
    public void First_send_is_blocked_for_identical_offers()
    {
        var report = CampaignReview.Check(SampleCampaigns.FirstSend());

        Assert.Equal(["medical-disclaimer", "terms-required", "tier-content-distinct", "tier-prices-increase"], Rules(report, Severity.Blocker));
        var note = Assert.Single(report.Findings, f => f.Rule == "tier-rung-repeated");
        Assert.Equal(Severity.Warning, note.Severity);
        Assert.Equal(
            "Tiers 1 and 2 are both 'Platinum'. Tier names like Bronze, Silver, Gold and Platinum tell readers which is which; " +
            "give each tier its own.",
            note.Message);
        Assert.DoesNotContain(report.Findings, f => f.Rule == "tier-names-numbered");
    }

    // The same name is still a Must fix, and only that: the numbered note is for names that differ.
    [Fact]
    public void Identical_names_are_still_a_must_fix_and_not_the_numbered_note()
    {
        var second = CampaignReview.Check(TwoTiers(
            new Tier("Platinum Member", 149m, [new BirthdayCredit(25m)]), new Tier("Platinum Member", 299m, [new BirthdayCredit(75m)])));
        var numbered = CampaignReview.Check(TwoTiers(
            new Tier("Option 1 Gold", 149m, [new BirthdayCredit(25m)]), new Tier("Option 1 Gold", 299m, [new BirthdayCredit(75m)])));

        Assert.Equal(Severity.Blocker, Assert.Single(second.Findings, f => f.Rule == "tier-names-unique").Severity);
        Assert.DoesNotContain(second.Findings, f => f.Rule == "tier-names-numbered");
        Assert.Equal(Severity.Blocker, Assert.Single(numbered.Findings, f => f.Rule == "tier-names-unique").Severity);
        Assert.DoesNotContain(numbered.Findings, f => f.Rule == "tier-names-numbered");
    }

    // The number can be the difference: the note still says so, strongly, and nothing blocks.
    [Fact]
    public void Glow_50_and_glow_100_get_the_note_but_nothing_blocks()
    {
        var report = CampaignReview.Check(TwoTiers(
            new Tier("Glow 50", 50m, [new BirthdayCredit(25m)]), new Tier("Glow 100", 100m, [new BirthdayCredit(75m)])));

        var note = Assert.Single(report.Findings, f => f.Rule == "tier-names-numbered");
        Assert.Equal(Severity.Warning, note.Severity);
        Assert.StartsWith("Tiers 1 and 2 are both 'Glow' apart from their numbers ('Glow 50', 'Glow 100'). Readers will see", note.Message);
        Assert.DoesNotContain(report.Blockers, f => f.Rule.StartsWith("tier"));
    }

    [Fact]
    public void Three_tiers_the_same_apart_from_their_numbers_are_one_note()
    {
        var offer = new Offer("Membership", "Join us.",
        [
            new Tier("Tier 1 Glow", 100m, [new BirthdayCredit(25m)]),
            new Tier("Tier 2 Glow", 200m, [new BirthdayCredit(50m)]),
            new Tier("Tier 3 Glow", 300m, [new BirthdayCredit(75m)]),
        ], IsRecurring: false, TermsUrl: null, TiersNote: null);

        var note = Assert.Single(CampaignReview.Check(new Campaign("Hello", [new OfferBlock("Offer", offer)])).Findings,
            f => f.Rule == "tier-names-numbered");

        Assert.StartsWith("Tiers 1, 2 and 3 are all 'Glow' apart from their numbers ('Tier 1 Glow', 'Tier 2 Glow', 'Tier 3 Glow'). " +
                          "Readers will see the same name 3 times", note.Message);
    }

    [Theory]
    [InlineData("Option 1 Platinum Member", "Platinum Member")]
    [InlineData("Tier 2 Gold", "Gold")]
    [InlineData("Platinum Member 2", "Platinum Member")]
    [InlineData("Platinum Member (2)", "Platinum Member")]
    [InlineData("Glow 50", "Glow")]
    [InlineData("Platinum Member", "Platinum Member")]
    [InlineData("100", "100")]
    public void A_tier_name_without_its_numbers(string name, string expected) =>
        Assert.Equal(expected, TierNames.Base(name));

    [Fact]
    public void Every_run_of_digits_is_the_same_token_and_nothing_else_is_ignored()
    {
        Assert.Equal(TierNames.Shape("Option 1 Platinum Member"), TierNames.Shape("option 22  PLATINUM member"));
        Assert.Equal(TierNames.Shape("Glow 50"), TierNames.Shape("Glow 100"));
        Assert.NotEqual(TierNames.Shape("Option 1 Platinum Member"), TierNames.Shape("Choice 1 Platinum Member"));
    }

    // It had a button ("Come visit", to the clinic's site), so cta-required rightly stays quiet; that
    // the button did not let anyone join the offer is the proofread's to catch. As sent (checked
    // 2026-10-09) its options were "Option 1 Platinum Member" and "Option 2 Platinum Member", not
    // the same name, so tier-names-unique is quiet; both are Platinum, which tier-rung-repeated says,
    // worth a look. "50% off Complimentary Wellness Injections" is left to the proofread.
    [Fact]
    public void Second_send_is_blocked_for_every_known_failure()
    {
        var report = CampaignReview.Check(SampleCampaigns.SecondSend());

        Assert.Equal(["medical-disclaimer", "terms-required"], Rules(report, Severity.Blocker));
        Assert.Equal(
            "Tiers 1 and 2 are both 'Platinum'. Tier names like Bronze, Silver, Gold and Platinum tell readers which is which; " +
            "give each tier its own.",
            Assert.Single(report.Warnings, f => f.Rule == "tier-rung-repeated").Message);
        Assert.DoesNotContain(report.Findings, f => f.Rule is "tier-names-unique" or "tier-names-numbered");
    }

    // ---- Wording: the benefit as she wrote it, never its key; singular and plural; text as written

    private static Campaign TwoTiers(Tier first, Tier second) =>
        new("Hello", [new OfferBlock("Offer", new Offer("Membership", "Join us.", [first, second], IsRecurring: false, TermsUrl: null, TiersNote: null))]);

    // The walkthrough read "has free:wellness injecton that tier 1 lacks".
    [Fact]
    public void Tiers_that_differ_name_the_benefits_as_written()
    {
        var finding = CampaignReview.Check(TwoTiers(
                new Tier("Gold Member", 149m, [new BirthdayCredit(25m), new PercentOff(5, "any qualifying treatments")]),
                new Tier("Platinum Member", 299m, [new FreeItem(1, "Wellness Injecton", "per visit"), new DiscountedItem(50, "facial", "per visit")])))
            .Findings.Single(f => f.Rule == "tiers-parallel");

        Assert.Equal(
            "'Platinum Member' has \"1 complimentary Wellness Injecton per visit\" and \"50% off one facial per visit\" that tier 1 lacks " +
            "and lacks \"$25 birthday credit during your birth month\" and \"5% off any qualifying treatments\" that tier 1 has; " +
            "check the tiers read side by side.",
            finding.Message);
        Assert.DoesNotContain("free:", finding.Message);
    }

    [Theory]
    [InlineData("Join the Beauty Bank.", "'bank' appears in 1 place —")]
    [InlineData("Join the Beauty Bank. Your bank, your way.", "'bank' appears in 1 place —")]
    public void A_restricted_term_in_one_place_is_said_in_the_singular(string opening, string expected) =>
        Assert.StartsWith(expected, CampaignReview.Check(new Campaign("Hello", [new ParagraphsBlock("Opening", [opening])]))
            .Findings.Single(f => f.Rule == "restricted-term").Message);

    [Fact]
    public void A_restricted_term_in_several_places_is_said_in_the_plural()
    {
        var finding = CampaignReview.Check(new Campaign("The Beauty Bank", [new ParagraphsBlock("Opening", ["Join the Beauty Bank."])]))
            .Findings.Single(f => f.Rule == "restricted-term");

        Assert.StartsWith("'bank' appears in 2 places —", finding.Message);
        Assert.DoesNotContain("(s)", finding.Message);
    }

    // The walkthrough read 'Repeats "75 birthday credit..."', the "$" dropped and her capitals lowered.
    [Fact]
    public void A_repeated_phrase_is_quoted_as_written()
    {
        var finding = CampaignReview.Check(new Campaign("Hello",
            [
                new ParagraphsBlock("Opening", ["Members get a $75 Birthday Reward during your birth month, every year!"]),
                new ParagraphsBlock("Closing", ["Remember: $75 Birthday Reward during your birth month, every year."]),
            ]))
            .Findings.Single(f => f.Rule == "repeated-phrase");

        Assert.Equal("Repeats \"$75 Birthday Reward during your birth month…\" from Opening, paragraph 1.", finding.Message);
    }

    [Fact]
    public void Second_send_warns_on_wording()
    {
        var warnings = Rules(CampaignReview.Check(SampleCampaigns.SecondSend()), Severity.Warning);

        Assert.Contains("restricted-term", warnings);   // "Bank", "savings account"
        Assert.Contains("emoji-spacing", warnings);     // "Beautiful🤍", "✨The"
        Assert.Contains("tiers-parallel", warnings);    // only tier 2 has a free injection
    }

    [Fact]
    public void Rules_alone_never_allow_export()
    {
        Assert.False(CampaignReview.Check(SampleCampaigns.Corrected()).CanExport);
    }

    [Fact]
    public void Duplicate_tier_name_is_reported_once_naming_both_tiers()
    {
        var finding = Assert.Single(
            CampaignReview.Check(TwoTiers(
                new Tier("Platinum Member", 149m, [new BirthdayCredit(25m)]), new Tier("Platinum Member", 299m, [new BirthdayCredit(75m)]))).Findings,
            f => f.Rule == "tier-names-unique");

        Assert.Contains("Tiers 1 and 2", finding.Message);
        Assert.Contains("Platinum Member", finding.Message);
    }

    [Fact]
    public void Corrected_email_passes_with_only_the_owners_naming_call_left()
    {
        var report = CampaignReview.Check(SampleCampaigns.Corrected());

        Assert.Empty(report.Blockers);
        Assert.Equal(
            ["restricted-term", "tiers-parallel"],
            Rules(report, Severity.Warning));
    }

    [Fact]
    public void Repeated_sentence_across_sections_is_flagged()
    {
        var corrected = SampleCampaigns.Corrected();
        var c = corrected.With(new ParagraphsBlock("Opening",
            [.. corrected.Block<ParagraphsBlock>("Opening").Paragraphs,
                "100% of your monthly contribution goes toward any treatments you choose."]));

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

    // Owner, 2026-10-09: tiers may run lowest price first or highest price first, as long as they
    // run one way. The corrected email listed highest first is fine; a tier against the direction
    // the first two set is a Must fix, and so is the same price twice.
    [Fact]
    public void Prices_may_run_either_way_but_only_one_way()
    {
        var offer = SampleCampaigns.Corrected().OfferOf();
        Campaign With(params decimal[] prices) => SampleCampaigns.Corrected().With(new OfferBlock("Offer", offer with
        {
            Tiers = prices.Select((p, i) => new Tier($"Tier {(char)('A' + i)}", p, [new BirthdayCredit(i + 1)])).ToList(),
        }));
        string[] Messages(Campaign c) => CampaignReview.Check(c).Blockers.Where(f => f.Rule == "tier-prices-increase").Select(f => f.Message).ToArray();

        var highestFirst = SampleCampaigns.Corrected().With(new OfferBlock("Offer", offer with { Tiers = [offer.Tiers[1], offer.Tiers[0]] }));
        Assert.Empty(Messages(highestFirst));
        Assert.Empty(Messages(With(100m, 200m, 300m)));
        Assert.Empty(Messages(With(300m, 200m, 100m)));
        Assert.Equal(
            ["Tier 3 costs less than tier 2, but the tiers before it go up in price; change either price, or put the tiers in order " +
             "with Lowest price first or Highest price first."],
            Messages(With(100m, 300m, 200m)));
        Assert.Equal(
            ["Tier 3 costs more than tier 2, but the tiers before it go down in price; change either price, or put the tiers in order " +
             "with Lowest price first or Highest price first."],
            Messages(With(300m, 100m, 200m)));
        Assert.Equal(["Tier 2 costs the same as tier 1; change either price."], Messages(With(100m, 100m, 200m)));
        Assert.DoesNotContain(Messages(With(100m, 300m, 200m)).Single(), "cheapest");
    }

    [Fact]
    public void Non_https_call_to_action_is_blocked()
    {
        var c = SampleCampaigns.Corrected().With(
            new ButtonBlock("Call to action", new CallToAction("Join", new Uri("http://example.com/join"))));

        Assert.Contains(CampaignReview.Check(c).Blockers, f => f.Rule == "cta-https");
    }

    [Fact]
    public void Too_many_emoji_is_a_warning()
    {
        var c = SampleCampaigns.Corrected().With(new HeadingBlock("Headline", "WE’RE TURNING ONE! 🥂✨🎉🎂💖🤍🤍🤍"));

        Assert.Contains(CampaignReview.Check(c).Warnings, f => f.Rule == "emoji-budget");
    }

    private static Campaign WithTier1Benefit(Benefit benefit)
    {
        var c = SampleCampaigns.Corrected();
        var tiers = c.OfferOf().Tiers.ToList();
        tiers[0] = tiers[0] with { Benefits = [.. tiers[0].Benefits, benefit] };
        return c.With(new OfferBlock("Offer", c.OfferOf() with { Tiers = tiers }));
    }
}
