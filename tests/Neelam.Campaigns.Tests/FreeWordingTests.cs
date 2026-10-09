using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Something free says "complimentary" or "free", her choice (owner, 2026-10-09: "Complimentary is the
/// same as free, so allow either"). The Beauty Bank email wrote "1 Complimentary Wellness Injection per
/// visit"; the back-to-school email, "Free Wellness Injection with any treatment". To every check the
/// two are the same benefit.
/// </summary>
public class FreeWordingTests
{
    private static readonly FreeItem Complimentary = new(1, "Wellness Injection", "per visit");
    private static readonly FreeItem Free = Complimentary with { Wording = FreeWording.Free };

    private static BusinessContext Knowing(params KnownItem[] items) => new("Neelam Aesthetics") { Known = new KnownItems(items) };

    private static IReadOnlyList<Finding> Findings(Campaign campaign, BusinessContext? business = null) =>
        CampaignReview.Check(campaign, business: business).Findings;

    private static Campaign WithTiers(params Tier[] tiers) =>
        new("Hello", [new OfferBlock("Offer", new Offer("Membership", "Join us.", tiers, IsRecurring: false, TermsUrl: null, TiersNote: null))]);

    private static Campaign WithBenefit(Benefit benefit) => WithTiers(new Tier("Platinum Member", 299m, [benefit]));

    // ---- How it reads

    [Fact]
    public void By_default_it_reads_complimentary_with_its_count()
    {
        Assert.Equal(FreeWording.Complimentary, new FreeItem(1, "Wellness Injection", "per visit").Wording);
        Assert.Equal("1 complimentary Wellness Injection per visit", Complimentary.Describe());
        Assert.Equal("2 complimentary Wellness Injections per visit", (Complimentary with { Quantity = 2 }).Describe());
    }

    // One free thing has no count and starts the line, as her back-to-school email wrote it; more than
    // one keeps the count.
    [Fact]
    public void Free_reads_free_and_one_free_thing_has_no_count()
    {
        Assert.Equal("Free Wellness Injection with any treatment", (Free with { Per = "with any treatment" }).Describe());
        Assert.Equal("Free Wellness Injection per visit", Free.Describe());
        Assert.Equal("2 free Wellness Injections per visit", (Free with { Quantity = 2 }).Describe());
    }

    // ---- Saved: an optional field, left out when complimentary

    [Fact]
    public void A_save_from_before_the_choice_reads_complimentary_and_complimentary_saves_as_before()
    {
        const string old = """[{"type":"free-item","quantity":1,"itemName":"Wellness Injection","per":"per visit"}]""";

        var read = Assert.IsType<FreeItem>(Assert.Single(CampaignJson.DeserializeBenefits(old)));

        Assert.Equal(FreeWording.Complimentary, read.Wording);
        Assert.Equal("1 complimentary Wellness Injection per visit", read.Describe());
        Assert.DoesNotContain("wording", CampaignJson.SerializeBenefits([Complimentary]));
        Assert.Contains("\"wording\":\"free\"", CampaignJson.SerializeBenefits([Free]));
        Assert.Equal(Free, Assert.Single(CampaignJson.DeserializeBenefits(CampaignJson.SerializeBenefits([Free]))));
    }

    [Fact]
    public void The_choice_round_trips_through_a_saved_campaign_and_reopens_in_the_form()
    {
        var editor = CampaignEditor.Start(DraftFixtures.Membership);
        var benefit = editor.Blocks.OfType<OfferBlockEditor>().Single().Offer.AddTier().AddBenefit();
        benefit.Kind = "free-item";
        benefit.Quantity = "1";
        benefit.ItemName = "Wellness Injection";
        benefit.Per = "with any treatment";
        Assert.Equal("1 complimentary Wellness Injection with any treatment", benefit.Sentence);

        benefit.Wording = FreeWording.Free;

        Assert.Equal("Free Wellness Injection with any treatment", benefit.Sentence);
        var json = CampaignJson.SerializeDraft(editor.Draft);
        Assert.Contains("\"wording\": \"free\"", json);
        var reopened = CampaignEditor.Open(CampaignJson.DeserializeDraft(json))
            .Blocks.OfType<OfferBlockEditor>().Single().Offer.Tiers[0].Benefits[0];
        Assert.Equal(FreeWording.Free, reopened.Wording);
        Assert.Equal(Free with { Per = "with any treatment" }, reopened.Value);
        Assert.Equal("Free Wellness Injection with any treatment", reopened.Sentence);
    }

    [Fact]
    public void The_choice_round_trips_through_a_known_items_row()
    {
        var line = new KnownBenefit(KnownItem.NewId(), Free, [new AmountLimit("Quantity", 1, 2)]);
        var tier = new KnownTier(KnownItem.NewId(), "Back to School", 99m, [Free, new PercentOff(10, "any qualifying treatments")]);
        var client = new ClientName("test-salon-one");

        var lineRow = KnownItemTable.FromItem(client, line);
        var readLine = Assert.IsType<KnownBenefit>(KnownItemTable.ToItem(client, lineRow));
        var readTier = Assert.IsType<KnownTier>(KnownItemTable.ToItem(client, KnownItemTable.FromItem(client, tier)));

        Assert.Equal("Free Wellness Injection per visit", lineRow.GetString("Text"));
        Assert.Equal(Free, readLine.Benefit);
        Assert.Equal(line.SetLimits, readLine.SetLimits);
        Assert.Equal(tier.Benefits, readTier.Benefits);
    }

    // ---- The same benefit to every check

    // A known line saved one way matches a campaign line written the other, with no note, and is not
    // offered to save again.
    [Fact]
    public void Free_and_complimentary_match_a_known_line_silently_either_way()
    {
        foreach (var (known, written) in new[] { (Complimentary, Free), (Free, Complimentary) })
        {
            var lines = new KnownItems([new KnownBenefit(KnownItem.NewId(), known)]);

            Assert.Equal(BenefitStanding.Known, lines.CheckBenefit(written).Standing);
            Assert.Empty(Findings(WithBenefit(written), Knowing(lines.All.ToArray())).Where(f => f.Rule == "known-item"));
            Assert.False(BenefitEditor.Standalone(written).SaveOffered(lines));
        }
    }

    // A known tier's lines are compared word for word, and these two words are one word.
    [Fact]
    public void Free_and_complimentary_match_a_known_tier_s_line_silently_either_way()
    {
        foreach (var (known, written) in new[] { (Complimentary, Free), (Free, Complimentary) })
        {
            var tier = new KnownTier(KnownItem.NewId(), "Platinum Member", 299m, [known]);

            Assert.Equal(BenefitStanding.Known, new KnownItems([tier]).CheckBenefit(written).Standing);
            Assert.Empty(Findings(WithBenefit(written), Knowing(tier)).Where(f => f.Rule == "known-item"));
        }
    }

    // A near miss suggests her line in the word she chose.
    [Fact]
    public void A_near_miss_is_suggested_in_the_word_she_chose()
    {
        var typo = Free with { ItemName = "Wellness Injecton" };

        Assert.Equal("Free Wellness Injection per visit",
            new KnownItems([new KnownBenefit(KnownItem.NewId(), Complimentary)]).CheckBenefit(typo).Suggestion);
        Assert.Equal("Free Wellness Injection per visit",
            new KnownItems([new KnownTier(KnownItem.NewId(), "Platinum Member", 299m, [Complimentary])]).CheckBenefit(typo).Suggestion);
    }

    // One line per pattern: the other word for the same line is already known.
    [Fact]
    public async Task The_other_word_for_a_known_line_is_already_known()
    {
        var store = new InMemoryKnownItems();
        var client = new ClientName("test-salon-one");
        await store.AddAsync(client, new KnownBenefit(KnownItem.NewId(), Complimentary));

        var twice = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AddAsync(client, new KnownBenefit(KnownItem.NewId(), Free)));

        Assert.Contains("is already in your known items.", twice.Message);
    }

    [Fact]
    public void Two_tiers_differing_only_by_the_word_offer_identical_benefits()
    {
        var findings = Findings(WithTiers(
            new Tier("Gold Member", 149m, [new PercentOff(10, "any qualifying treatments"), Complimentary]),
            new Tier("Platinum Member", 299m, [Free, new PercentOff(10, "any qualifying treatments")])));

        var identical = Assert.Single(findings, f => f.Rule == "tier-content-distinct");
        Assert.Equal(Severity.Blocker, identical.Severity);
        Assert.Equal("Tiers 1 and 2 offer identical benefits; one of them was not updated.", identical.Message);
    }

    // Side by side, the free line of one tier is the complimentary line of the other: nothing is
    // "has ... that tier 1 lacks".
    [Fact]
    public void Tiers_carrying_the_same_benefit_in_either_word_read_side_by_side()
    {
        var findings = Findings(WithTiers(
            new Tier("Gold Member", 149m, [new PercentOff(10, "any qualifying treatments"), Complimentary]),
            new Tier("Platinum Member", 299m, [new PercentOff(15, "any qualifying treatments"), Free with { Quantity = 2 }])));

        Assert.DoesNotContain(findings, f => f.Rule is "tiers-parallel" or "tier-content-distinct");
    }

    [Fact]
    public void A_free_quantity_below_one_says_free()
    {
        var finding = Assert.Single(Findings(WithBenefit(Free with { Quantity = 0 })), f => f.Rule == "benefit-value");

        Assert.Equal("0 free Wellness Injection is nothing; quantity must be at least 1.", finding.Message);
    }

    // ---- Export: the words she chose, in the paste text and the assistant's expected text

    private static readonly Approval ByPriya = new("Priya", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero))
    {
        WarningsSeen = new WarningsSeen("Priya", new DateTimeOffset(2026, 10, 9, 12, 5, 0, TimeSpan.Zero)),
    };

    private static Campaign FreeEverywhere(Campaign campaign) => campaign with
    {
        Blocks = campaign.Blocks.Select(b => b is OfferBlock o
            ? o with
            {
                Offer = o.Offer with
                {
                    Tiers = o.Offer.Tiers.Select(t => t with
                    {
                        Benefits = t.Benefits.Select(x => x is FreeItem f ? f with { Wording = FreeWording.Free } : x).ToList(),
                    }).ToList(),
                },
            }
            : b).ToList(),
    };

    [Fact]
    public void The_export_carries_the_word_she_chose()
    {
        var campaign = FreeEverywhere(BeautyBankEmail.Corrected());
        var free = campaign.BlocksOf<OfferBlock>().SelectMany(o => o.Offer.Tiers).SelectMany(t => t.Benefits).OfType<FreeItem>().First();
        var marker = campaign.BlocksOf<OfferBlock>().Single().Marker;
        var report = CampaignGate.DemoReview(campaign, ByPriya.Seeing(campaign));

        var text = EditorExport.PlainText(report);
        var json = AssistantExport.Json(report);

        var asBefore = (free with { Wording = FreeWording.Complimentary }).Describe();
        Assert.NotEqual(asBefore, free.Describe());
        Assert.Contains($"{marker} {free.Describe()}", text);
        Assert.Contains(free.Describe(), json);
        Assert.DoesNotContain(asBefore, text);
        Assert.DoesNotContain(asBefore, json);
    }
}
