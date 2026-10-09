using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Two benefit kinds from her back-to-school email, "$50 off 30+ units + $50 credit toward your next
/// appointment" (owner, 2026-10-09: "Otherwise, yes, add the benefit types"): dollars off, with an
/// optional minimum, and a credit toward something. Each is typed like the others, and every check
/// treats them as it treats the others.
/// </summary>
public class DollarsOffAndCreditTests
{
    private static readonly DollarsOff FiftyOffThirtyUnits = new(50m, "units", 30);
    private static readonly Credit FiftyTowardNext = new(50m, "your next appointment");
    private static readonly ClientName Client = new("test-salon-one");

    private static BusinessContext Knowing(params KnownItem[] items) => new("Neelam Aesthetics") { Known = new KnownItems(items) };

    private static IReadOnlyList<Finding> Findings(Campaign campaign, BusinessContext? business = null) =>
        CampaignReview.Check(campaign, business: business).Findings;

    private static IReadOnlyList<Finding> Notes(Benefit benefit, params KnownItem[] known) =>
        Findings(WithTiers(new Tier("Platinum Member", 299m, [benefit])), Knowing(known)).Where(f => f.Rule == "known-item").ToList();

    private static Campaign WithTiers(params Tier[] tiers) =>
        new("Hello", [new OfferBlock("Offer", new Offer("Membership", "Join us.", tiers, IsRecurring: false, TermsUrl: null, TiersNote: null))]);

    // ---- How they read

    [Fact]
    public void Each_reads_as_the_back_to_school_email_wrote_it()
    {
        Assert.Equal("$50 off 30+ units", FiftyOffThirtyUnits.Describe());
        Assert.Equal("$50 credit toward your next appointment", FiftyTowardNext.Describe());
        // No minimum, no "+"; cents only when there are some.
        Assert.Equal("$25 off any facial", new DollarsOff(25m, "any facial").Describe());
        Assert.Equal("$49.50 credit toward your next appointment", (FiftyTowardNext with { Amount = 49.50m }).Describe());
    }

    // ---- The form: typed fields per kind, reopened with them

    public static TheoryData<string, Action<BenefitEditor>, Benefit> NewKinds => new()
    {
        { "dollars-off", b => { b.Amount = "$50"; b.AppliesTo = "units"; b.Minimum = "30+"; }, FiftyOffThirtyUnits },
        { "dollars-off", b => { b.Amount = "25"; b.AppliesTo = "any facial"; }, new DollarsOff(25m, "any facial") },
        { "credit", b => { b.Amount = "50"; b.Toward = "your next appointment"; }, FiftyTowardNext },
    };

    [Theory]
    [MemberData(nameof(NewKinds))]
    public void Each_has_its_own_fields_and_reopens_with_them(string kind, Action<BenefitEditor> fill, Benefit expected)
    {
        var editor = CampaignEditor.Start(DraftFixtures.Membership);
        var benefit = editor.Blocks.OfType<OfferBlockEditor>().Single().Offer.AddTier().AddBenefit();
        benefit.Kind = kind;
        fill(benefit);

        Assert.Null(benefit.Error);
        Assert.Equal(expected, benefit.Value);
        Assert.Equal(expected.Describe(), benefit.Sentence);
        var reopened = CampaignEditor.Open(CampaignJson.DeserializeDraft(CampaignJson.SerializeDraft(editor.Draft)))
            .Blocks.OfType<OfferBlockEditor>().Single().Offer.Tiers[0].Benefits[0];
        Assert.Equal(kind, reopened.Kind);
        Assert.Equal(expected, reopened.Value);
        Assert.Equal(Origin.Entered, reopened.Origin);
        // Its fields hold every part of it: built again from them, it is the same benefit.
        reopened.Kind = kind;
        Assert.Equal(expected, reopened.Value);
    }

    [Fact]
    public void Unfinished_or_wrong_fields_say_what_is_left()
    {
        var off = BenefitEditor.Standalone();
        off.Kind = "dollars-off";
        off.Amount = "50";
        off.Minimum = "lots";
        Assert.Equal("Fill in what it is off, such as \"units\". \"lots\" is not a whole number.", off.Error);
        Assert.Null(off.Value);

        var credit = BenefitEditor.Standalone();
        credit.Kind = "credit";
        credit.Amount = "fifty";
        credit.Toward = "your next appointment";
        Assert.Equal("\"fifty\" is not an amount in dollars, such as 149 or 149.50.", credit.Error);
        Assert.Null(credit.Value);
    }

    // ---- Saved: campaign JSON and the known-items row, old data unaffected

    [Fact]
    public void Each_round_trips_through_benefit_json_and_a_minimum_left_out_is_none()
    {
        var json = CampaignJson.SerializeBenefits([FiftyOffThirtyUnits, FiftyTowardNext, new DollarsOff(25m, "any facial")]);

        Assert.Equal([FiftyOffThirtyUnits, FiftyTowardNext, new DollarsOff(25m, "any facial")], CampaignJson.DeserializeBenefits(json));
        Assert.Contains("\"type\":\"dollars-off\"", json);
        Assert.Contains("\"type\":\"credit\"", json);
        var noMinimum = Assert.IsType<DollarsOff>(Assert.Single(CampaignJson.DeserializeBenefits(
            """[{"type":"dollars-off","amount":25,"appliesTo":"any facial"}]""")));
        Assert.Null(noMinimum.Minimum);
    }

    [Fact]
    public void Each_round_trips_through_a_known_items_row_with_its_limits()
    {
        var off = new KnownBenefit(KnownItem.NewId(), FiftyOffThirtyUnits, [new AmountLimit("Amount", 25m, 75m), new AmountLimit("Minimum", 20m, null)]);
        var credit = new KnownBenefit(KnownItem.NewId(), FiftyTowardNext, [new AmountLimit("Amount", null, 100m)]);
        var tier = new KnownTier(KnownItem.NewId(), "Back to School", 99m, [FiftyOffThirtyUnits, FiftyTowardNext]);

        foreach (var line in new[] { off, credit })
        {
            var row = KnownItemTable.FromItem(Client, line);
            var read = Assert.IsType<KnownBenefit>(KnownItemTable.ToItem(Client, row));
            Assert.Equal(line.Benefit, read.Benefit);
            Assert.Equal(line.SetLimits, read.SetLimits);
            Assert.Equal(line.Benefit.Describe(), row.GetString("Text"));
        }
        Assert.Equal("""[{"field":"Amount","min":25,"max":75},{"field":"Minimum","min":20}]""",
            KnownItemTable.FromItem(Client, off).GetString("Limits"));
        Assert.Equal(tier.Benefits, Assert.IsType<KnownTier>(KnownItemTable.ToItem(Client, KnownItemTable.FromItem(Client, tier))).Benefits);
    }

    // ---- Her benefit lines: the words matched, the amounts held to the limits

    [Fact]
    public void A_line_s_amounts_are_held_to_its_limits_and_its_words_matched()
    {
        var off = new KnownBenefit(KnownItem.NewId(), FiftyOffThirtyUnits, [new AmountLimit("Amount", 25m, 75m), new AmountLimit("Minimum", 20m, null)]);
        var credit = new KnownBenefit(KnownItem.NewId(), FiftyTowardNext, [new AmountLimit("Amount", null, 75m)]);

        Assert.Equal("$50 off 30+ units — usually $50, between $25 and $75; usually 30, at least 20", off.Shown);
        // Within the limits, at any amount, or a plural ending: silent.
        Assert.Empty(Notes(new DollarsOff(60m, "units", 40), off));
        Assert.Empty(Notes(new DollarsOff(50m, "unit", 30), off));
        Assert.Empty(Notes(FiftyTowardNext with { Amount = 75m }, credit));
        // Outside them: worth a look, in the amount's own unit.
        Assert.Equal("'$100 off 30+ units' is outside your usual range for this line ($25–$75).",
            Assert.Single(Notes(new DollarsOff(100m, "units", 30), off)).Message);
        Assert.Equal("'$50 off 10+ units' is outside your usual range for this line (at least 20).",
            Assert.Single(Notes(new DollarsOff(50m, "units", 10), off)).Message);
        Assert.Equal("'$100 credit toward your next appointment' is outside your usual range for this line (up to $75).",
            Assert.Single(Notes(FiftyTowardNext with { Amount = 100m }, credit)).Message);
        // A typo in the words: "Did you mean", at the amounts written.
        Assert.Equal("Did you mean '$60 off 40+ units'? It's in your known items.",
            Assert.Single(Notes(new DollarsOff(60m, "untis", 40), off)).Message);
        Assert.Equal("Did you mean '$50 credit toward your next appointment'? It's in your known items.",
            Assert.Single(Notes(FiftyTowardNext with { Toward = "your next apointment" }, credit)).Message);
    }

    // With a minimum and without are different lines, and a limit on a minimum the line lacks is refused.
    [Fact]
    public void A_minimum_is_part_of_the_line()
    {
        var noMinimum = new KnownItems([new KnownBenefit(KnownItem.NewId(), new DollarsOff(50m, "units"))]);

        Assert.Equal(BenefitStanding.NotKnown, noMinimum.CheckBenefit(FiftyOffThirtyUnits).Standing);
        Assert.Equal(BenefitStanding.Known, noMinimum.CheckBenefit(new DollarsOff(80m, "units")).Standing);
        Assert.Equal(["A limit names an amount this kind of benefit doesn't have."],
            new KnownBenefit(KnownItem.NewId(), new DollarsOff(50m, "units"), [new AmountLimit("Minimum", 1m, null)]).LimitProblems());
        Assert.Equal(["The lowest and highest minimum are whole numbers."],
            new KnownBenefit(KnownItem.NewId(), FiftyOffThirtyUnits, [new AmountLimit("Minimum", 1.5m, null)]).LimitProblems());
    }

    // The Known items page offers limit boxes for each amount the line has: the minimum's only with one.
    [Fact]
    public void The_limit_boxes_follow_the_line_s_amounts()
    {
        var form = new AmountLimitsForm();
        var benefit = BenefitEditor.Standalone();
        benefit.Kind = "dollars-off";
        Assert.Equal(["Amount", "Minimum"], form.FieldsFor(benefit).Select(f => f.Field.Name));

        benefit.Amount = "50";
        benefit.AppliesTo = "units";
        Assert.Equal(["Amount"], form.FieldsFor(benefit).Select(f => f.Field.Name));

        benefit.Minimum = "30";
        Assert.Equal(["Amount", "Minimum"], form.FieldsFor(benefit).Select(f => f.Field.Name));
        form.For(DollarsOff.MinimumField).Lowest = "20";
        var (line, errors) = form.ToItem(KnownItem.NewId(), benefit, "unfinished");
        Assert.Empty(errors);
        Assert.Equal([new AmountLimit("Minimum", 20m, null)], line!.SetLimits);
    }

    // ---- Tiers: compared as every other kind

    [Fact]
    public void Two_tiers_with_the_same_lines_are_identical_and_a_kind_tier_1_lacks_is_named()
    {
        var identical = Findings(WithTiers(
            new Tier("Gold Member", 149m, [FiftyOffThirtyUnits, FiftyTowardNext]),
            new Tier("Platinum Member", 299m, [FiftyTowardNext, FiftyOffThirtyUnits])));
        Assert.Contains(identical, f => f.Rule == "tier-content-distinct");

        var parallel = Findings(WithTiers(
            new Tier("Gold Member", 149m, [FiftyOffThirtyUnits]),
            new Tier("Platinum Member", 299m, [new DollarsOff(100m, "units", 30), FiftyTowardNext])));
        Assert.DoesNotContain(parallel, f => f.Rule == "tier-content-distinct");
        Assert.Equal("'Platinum Member' has \"$50 credit toward your next appointment\" that tier 1 lacks; check the tiers read side by side.",
            Assert.Single(parallel, f => f.Rule == "tiers-parallel").Message);
    }

    // ---- benefit-value: an impossible value is a Must fix, as for the other kinds

    [Theory]
    [InlineData(0, 30, "Dollars off needs an amount.")]
    [InlineData(-5, null, "Dollars off needs an amount.")]
    [InlineData(50, 0, "0+ units is not a minimum; leave it out or make it at least 1.")]
    public void Dollars_off_without_an_amount_or_with_no_minimum_is_a_must_fix(int amount, int? minimum, string message)
    {
        var finding = Assert.Single(Findings(WithTiers(new Tier("Gold Member", 149m, [new DollarsOff(amount, "units", minimum)]))),
            f => f.Rule == "benefit-value");

        Assert.Equal(Severity.Blocker, finding.Severity);
        Assert.Equal(message, finding.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void A_credit_without_an_amount_is_a_must_fix(int amount)
    {
        var finding = Assert.Single(Findings(WithTiers(new Tier("Gold Member", 149m, [FiftyTowardNext with { Amount = amount }]))),
            f => f.Rule == "benefit-value");

        Assert.Equal(Severity.Blocker, finding.Severity);
        Assert.Equal("A credit needs an amount.", finding.Message);
    }

    [Fact]
    public void Sensible_values_are_not_a_must_fix() =>
        Assert.DoesNotContain(Findings(WithTiers(new Tier("Gold Member", 149m, [FiftyOffThirtyUnits, FiftyTowardNext, new DollarsOff(25m, "any facial")]))),
            f => f.Rule == "benefit-value");

    // ---- Export

    private static readonly Approval ByPriya = new("Priya", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero))
    {
        WarningsSeen = new WarningsSeen("Priya", new DateTimeOffset(2026, 10, 9, 12, 5, 0, TimeSpan.Zero)),
    };

    [Fact]
    public void The_export_carries_each_line_as_it_reads()
    {
        var corrected = BeautyBankEmail.Corrected();
        var campaign = corrected with
        {
            Blocks = corrected.Blocks.Select(b => b is OfferBlock o
                ? o with { Offer = o.Offer with { Tiers = o.Offer.Tiers.Select(t => t with { Benefits = [.. t.Benefits, FiftyOffThirtyUnits, FiftyTowardNext] }).ToList() } }
                : b).ToList(),
        };
        var marker = campaign.BlocksOf<OfferBlock>().Single().Marker;
        var report = CampaignGate.DemoReview(campaign, ByPriya);

        var text = EditorExport.PlainText(report);
        var json = AssistantExport.Json(report);

        Assert.Contains($"{marker} $50 off 30+ units\n{marker} $50 credit toward your next appointment", text);
        Assert.Contains("$50 off 30+ units", json);
        Assert.Contains("$50 credit toward your next appointment", json);
    }
}
