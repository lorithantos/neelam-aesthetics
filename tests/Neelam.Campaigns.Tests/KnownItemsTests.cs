using Azure.Data.Tables;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Known items (owner, 2026-10-09): treatments, benefit lines and tiers a client writes again and
/// again, kept once per client in the knownItems table, picked while writing a campaign, saved from
/// one, and checked against for near misses.
/// </summary>
public class KnownItemsTests
{
    private static readonly ClientName SalonOne = new("test-salon-one");
    private static readonly ClientName SalonTwo = new("test-salon-two");
    private static ActivityTrail Trail => TestRecords.Unwatched.For(SalonOne, Actor.Demo);

    private static readonly KnownTreatment Wellness = new(KnownItem.NewId(), "Wellness injection");
    private static readonly KnownBenefit TenOff = new(KnownItem.NewId(), new PercentOff(10, "any qualifying treatments"));
    private static readonly KnownTier Platinum = new(KnownItem.NewId(), "Platinum Member", 299m,
    [
        new BirthdayCredit(75m),
        new PercentOff(10, "any qualifying treatments"),
        new FreeItem(1, "wellness injection", "per visit"),
    ]);

    private static BusinessContext Knowing(params KnownItem[] items) => new("Neelam Aesthetics") { Known = new KnownItems(items) };

    private static OfferEditor OfferOf(CampaignEditor editor) =>
        editor.Blocks.OfType<OfferBlockEditor>().Single().Offer;

    // ---- Storage: one table, a partition per client

    public static TheoryData<KnownItem> EachKind => new() { Wellness, TenOff, Platinum };

    [Theory]
    [MemberData(nameof(EachKind))]
    public void Each_kind_round_trips_through_its_table_row(KnownItem item)
    {
        var row = KnownItemTable.FromItem(SalonOne, item);

        Assert.Equal("test-salon-one", row.PartitionKey);
        Assert.Equal(item.Id, row.RowKey);
        Assert.Equal(item.Kind.ToString(), row.GetString("Kind"));
        Assert.Equal(item.Text, row.GetString("Text"));
        AssertSame(item, KnownItemTable.ToItem(SalonOne, row));
    }

    [Fact]
    public void A_tier_row_keeps_its_price_exactly_and_its_lines_with_their_kinds()
    {
        var tier = Platinum with { Price = 149.50m };
        var row = KnownItemTable.FromItem(SalonOne, tier);

        Assert.Equal("149.50", row.GetString("Price"));
        var read = Assert.IsType<KnownTier>(KnownItemTable.ToItem(SalonOne, row));
        Assert.Equal(149.50m, read.Price);
        Assert.Equal(tier.Benefits, read.Benefits);
    }

    [Theory]
    [MemberData(nameof(EachKind))]
    public async Task Each_kind_round_trips_through_the_store(KnownItem item)
    {
        var store = new InMemoryKnownItems();
        await store.AddAsync(SalonOne, item);

        AssertSame(item, Assert.Single((await store.ForClientAsync(SalonOne)).All));
    }

    // Another client's items are never read: the query names one partition, a row from any other is
    // refused, and in the store salon two's list holds only salon two's.
    [Fact]
    public async Task Another_client_s_items_are_never_read()
    {
        Assert.Equal("PartitionKey eq 'test-salon-one'", KnownItemTable.PartitionFilter(SalonOne));
        Assert.Throws<InvalidDataException>(() => KnownItemTable.ToItem(SalonOne, KnownItemTable.FromItem(SalonTwo, Wellness)));

        var store = new InMemoryKnownItems();
        await store.AddAsync(SalonOne, Wellness);
        await store.AddAsync(SalonTwo, new KnownTreatment(KnownItem.NewId(), "Hydrafacial"));

        Assert.Equal(["Wellness injection"], (await store.ForClientAsync(SalonOne)).All.Select(i => i.Text));
        Assert.Equal(["Hydrafacial"], (await store.ForClientAsync(SalonTwo)).All.Select(i => i.Text));
        // Salon two removing salon one's item by its id removes nothing of salon one's.
        await store.RemoveAsync(SalonTwo, Wellness.Id);
        Assert.Single((await store.ForClientAsync(SalonOne)).All);
    }

    [Fact]
    public async Task The_list_says_each_thing_once_and_a_tier_needs_a_name_and_a_price()
    {
        var store = new InMemoryKnownItems();
        await store.AddAsync(SalonOne, Wellness);

        var twice = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AddAsync(SalonOne, new KnownTreatment(KnownItem.NewId(), "  wellness INJECTION ")));
        Assert.Contains("\"Wellness injection\" is already in your known items.", twice.Message);
        // Another client may know the same thing.
        await store.AddAsync(SalonTwo, Wellness);

        Assert.NotEmpty(KnownItemRules.Problems(Platinum with { Name = " " }, KnownItems.None));
        Assert.NotEmpty(KnownItemRules.Problems(Platinum with { Price = 0m }, KnownItems.None));
        Assert.NotEmpty(KnownItemRules.Problems(Wellness with { Id = "not-an-id" }, KnownItems.None));
        Assert.Empty(KnownItemRules.Problems(Platinum, KnownItems.None));
    }

    [Fact]
    public async Task An_item_keeps_its_kind_when_it_is_changed()
    {
        var store = new InMemoryKnownItems();
        await store.AddAsync(SalonOne, Wellness);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync(SalonOne, new KnownBenefit(Wellness.Id, new BirthdayCredit(75m))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync(SalonOne, new KnownTreatment(KnownItem.NewId(), "Botox")));
    }

    // ---- Picking while writing a campaign

    [Fact]
    public void A_picked_benefit_is_an_editable_line_she_entered_not_one_to_check()
    {
        var editor = CampaignEditor.Open(DraftFixtures.Finished());
        var tier = OfferOf(editor).Tiers[0];

        var picked = tier.AddKnownBenefit(TenOff.Benefit);

        Assert.Same(picked, tier.Benefits[^1]);
        Assert.Equal("10% off any qualifying treatments", picked.Sentence);
        Assert.Equal(Origin.Entered, picked.Origin);
        Assert.False(picked.IsUnreviewed);
        // Nothing left to do: a line still to check would be a Must fix here.
        Assert.Empty(editor.Status().Missing);
        // Ordinary text afterwards: changing it changes the line.
        picked.Percent = "15";
        Assert.Equal("15% off any qualifying treatments", picked.Sentence);
    }

    [Fact]
    public void A_picked_tier_brings_its_name_price_and_benefits()
    {
        var editor = CampaignEditor.Start(DraftFixtures.Membership);
        var offer = OfferOf(editor);

        var tier = offer.AddKnownTier(Platinum);

        Assert.Same(tier, Assert.Single(offer.Tiers));
        Assert.Equal("Platinum Member", tier.Name.Text);
        Assert.Equal("299", tier.Price.Text);
        Assert.Equal(Platinum.Benefits.Select(b => b.Describe()), tier.Benefits.Select(b => b.Sentence));
        Assert.All(tier.Benefits, b => Assert.False(b.IsUnreviewed));
        Assert.All(tier.Benefits, b => Assert.Equal(Origin.Entered, b.Origin));
    }

    // Allowed in, and the rules that catch a copied tier catch it: Must fix until either changes.
    [Fact]
    public void A_picked_tier_repeating_another_s_name_and_price_is_a_must_fix()
    {
        var editor = CampaignEditor.Start(DraftFixtures.Membership);
        var offer = OfferOf(editor);
        offer.AddKnownTier(Platinum);
        offer.AddKnownTier(Platinum);

        var findings = editor.Status().Findings;

        Assert.Contains(findings, f => f is { Rule: "tier-names-unique", Severity: Severity.Blocker });
        Assert.Contains(findings, f => f is { Rule: "tier-prices-increase", Severity: Severity.Blocker });
    }

    [Fact]
    public void A_picker_finds_an_item_by_its_text_exactly_else_ignoring_case()
    {
        var known = new KnownItems([TenOff, Platinum]);

        Assert.Same(TenOff, known.BenefitNamed("10% off any qualifying treatments"));
        Assert.Same(TenOff, known.BenefitNamed(" 10% OFF any qualifying treatments "));
        Assert.Same(Platinum, known.TierNamed("platinum member"));
        Assert.Null(known.BenefitNamed("10% off"));
        Assert.Null(known.TierNamed(""));
    }

    // ---- Saving from a campaign, exactly as written

    [Fact]
    public void A_tier_and_a_benefit_line_are_saved_exactly_as_written()
    {
        var editor = CampaignEditor.Open(DraftFixtures.Finished());
        var platinum = OfferOf(editor).Tiers[1];

        var (tier, problem) = platinum.ToKnown();
        var benefit = platinum.Benefits[3].ToKnown();

        Assert.Null(problem);
        Assert.Equal("Platinum Member", tier!.Name);
        Assert.Equal(299m, tier.Price);
        Assert.Equal(platinum.Benefits.Select(b => b.Sentence), tier.Benefits.Select(b => b.Describe()));
        Assert.Equal(new DiscountedItem(50, "wellness injection", "per visit", "any additional"), benefit!.Benefit);
    }

    [Fact]
    public void An_unfinished_tier_or_line_cannot_be_saved_yet()
    {
        var editor = CampaignEditor.Start(DraftFixtures.Membership);
        var tier = OfferOf(editor).AddTier();
        var line = tier.AddBenefit();

        Assert.NotNull(tier.ToKnown().Problem);
        Assert.Null(line.ToKnown());
        tier.Name.Text = "Gold";
        tier.Price.Text = "149";
        Assert.Equal("Fill in or remove its empty benefits first.", tier.ToKnown().Problem);
    }

    [Fact]
    public async Task Saving_what_is_already_known_says_so()
    {
        var store = new InMemoryKnownItems();

        Assert.Equal("Saved \"Platinum Member\" to your known items.",
            await KnownItemsSession.SaveFromCampaignAsync(store, Trail, Platinum));
        Assert.Equal("\"Platinum Member\" is already in your known items.",
            await KnownItemsSession.SaveFromCampaignAsync(store, Trail, Platinum with { Id = KnownItem.NewId() }));
        Assert.Single((await store.ForClientAsync(SalonOne)).All);
    }

    // ---- Near misses

    private static IReadOnlyList<Finding> NearMisses(Campaign campaign, BusinessContext? business) =>
        CampaignReview.Check(campaign, business: business).Findings.Where(f => f.Rule == "known-item").ToList();

    private static Campaign WithBenefit(Benefit benefit) =>
        new("Hello", [new OfferBlock("Offer", new Offer("Membership", "Join us.", [new Tier("Gold", 149m, [benefit])], IsRecurring: false, TermsUrl: null, TiersNote: null))]);

    [Fact]
    public void A_misspelt_treatment_is_worth_a_look_naming_the_known_one()
    {
        var finding = Assert.Single(NearMisses(WithBenefit(new FreeItem(1, "Wellness Injecton", "per visit")), Knowing(Wellness)));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("Offer › Tier 1", finding.Location);
        Assert.Equal("Did you mean 'Wellness injection'? It's in your known items.", finding.Message);
        Assert.Equal("Wellness Injecton", finding.Excerpt);
    }

    [Fact]
    public void A_benefit_line_one_letter_off_a_known_one_is_worth_a_look()
    {
        var finding = Assert.Single(NearMisses(WithBenefit(new PercentOff(10, "any qualifing treatments")), Knowing(TenOff)));

        Assert.Equal("Did you mean '10% off any qualifying treatments'? It's in your known items.", finding.Message);
    }

    [Fact]
    public void The_lines_of_a_known_tier_are_known_lines_too() =>
        Assert.Single(NearMisses(WithBenefit(new PercentOff(10, "any qualifyng treatments")), Knowing(Platinum)));

    [Fact]
    public void What_matches_a_known_item_exactly_or_nothing_like_one_draws_nothing()
    {
        Assert.Empty(NearMisses(WithBenefit(new FreeItem(1, "Wellness injection", "per visit")), Knowing(Wellness)));
        Assert.Empty(NearMisses(WithBenefit(new FreeItem(1, "Hydrafacial", "per visit")), Knowing(Wellness)));
        Assert.Empty(NearMisses(WithBenefit(new PercentOff(10, "any qualifying treatments")), Knowing(TenOff)));
    }

    [Fact]
    public void With_no_known_items_nothing_is_said()
    {
        var campaign = WithBenefit(new FreeItem(1, "Wellness Injecton", "per visit"));

        Assert.Empty(NearMisses(campaign, null));
        Assert.Empty(NearMisses(campaign, new BusinessContext("Neelam Aesthetics")));
    }

    // The threshold, both sides of each edge.
    [Theory]
    [InlineData("wellness injection", "Wellness injection", true)]    // case alone
    [InlineData("BOTOX", "Botox", true)]                              // case alone, even when short
    [InlineData("Botax", "Botox", false)]                             // short: case only
    [InlineData("Wellness injecton", "Wellness injection", true)]     // one edit
    [InlineData("Welness injecton", "Wellness injection", true)]      // two edits
    [InlineData("Welness injectn", "Wellness injection", false)]      // three edits
    [InlineData("Filler", "Fillers", true)]                           // six characters, one edit
    [InlineData("Filer", "Filler", false)]                            // five characters
    [InlineData("15% off any qualifying treatments", "10% off any qualifying treatments", false)] // a different number
    [InlineData("10% off any qualifying treatment", "10% off any qualifying treatments", true)]
    public void The_threshold_is_case_or_two_edits_on_six_characters_with_the_same_numbers(
        string written, string known, bool nearMiss) =>
        Assert.Equal(nearMiss ? known : null, KnownItemMatch.NearMiss(written, [known]));

    [Fact]
    public void An_exact_match_to_any_known_item_wins_over_a_near_one() =>
        Assert.Null(KnownItemMatch.NearMiss("Filler", ["Fillers", "Filler"]));

    // The near misses run while parts are missing too, as every rule does.
    [Fact]
    public void Near_misses_are_flagged_while_parts_are_missing()
    {
        var editor = CampaignEditor.Start(DraftFixtures.Membership);
        var tier = OfferOf(editor).AddTier();
        tier.Name.Text = "Gold";
        tier.Price.Text = "149";
        var line = tier.AddBenefit();
        line.Kind = "free-item";
        line.Quantity = "1";
        line.ItemName = "Wellness Injecton";
        line.Per = "per visit";

        var status = editor.Status(business: Knowing(Wellness));

        Assert.NotEmpty(status.Missing);
        var finding = Assert.Single(status.Findings, f => f.Rule == "known-item");
        Assert.Equal("Did you mean 'Wellness injection'? It's in your known items.", finding.Message);
    }

    private static void AssertSame(KnownItem expected, KnownItem actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Text, actual.Text);
        switch (expected)
        {
            case KnownBenefit b:
                Assert.Equal(b.Benefit, ((KnownBenefit)actual).Benefit);
                break;
            case KnownTier t:
                var tier = (KnownTier)actual;
                Assert.Equal(t.Price, tier.Price);
                Assert.Equal(t.Benefits, tier.Benefits);
                break;
        }
    }
}
