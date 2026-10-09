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

    // ---- Offer details against known items (owner, 2026-10-09): good faith for prose, strict on offer details

    private static IReadOnlyList<Finding> NearMisses(Campaign campaign, BusinessContext? business) =>
        CampaignReview.Check(campaign, business: business).Findings.Where(f => f.Rule == "known-item").ToList();

    private static Campaign WithTier(Tier tier) =>
        new("Hello", [new OfferBlock("Offer", new Offer("Membership", "Join us.", [tier], IsRecurring: false, TermsUrl: null, TiersNote: null))]);

    // The tier is Platinum's own name and price, so only the benefit is in question.
    private static Campaign WithBenefit(Benefit benefit) => WithTier(new Tier("Platinum Member", 299m, [benefit]));

    private static readonly KnownTreatment Facial = new(KnownItem.NewId(), "Facial");
    private static readonly KnownTreatment Lashes = new(KnownItem.NewId(), "Lashes");
    private static readonly KnownTreatment Botox = new(KnownItem.NewId(), "Botox");

    // What misfired when every word was held to the list: "Social" for "Facial", "Lasers" for
    // "Lashes", "Facials" for "Facial". Prose is the proofread's job; the list never reads it.
    [Fact]
    public void Prose_is_never_held_to_the_known_items()
    {
        var campaign = new Campaign("A Social evening",
        [
            new HeadingBlock("Headline", "Lasers, Facials and a Social evening"),
            new GreetingBlock("Greeting", "Hi Beautiful"),
            new ParagraphsBlock("Opening", ["Join us for our Social: Lasers and Facials all night, and ask about botox while you are here."]),
            new ParagraphsBlock("Closing", ["Facials, Lasers, Social. See you there."]),
            new FinePrintBlock("Fine print", "A wellness injection is given after consultation."),
        ]);

        Assert.Empty(NearMisses(campaign, Knowing(Facial, Lashes, Botox, Wellness, TenOff, Platinum)));
    }

    [Fact]
    public void A_misspelt_treatment_in_the_item_field_is_worth_a_look_naming_the_known_one()
    {
        var finding = Assert.Single(NearMisses(WithBenefit(new FreeItem(1, "Wellness Injecton", "per visit")), Knowing(Wellness)));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("Offer › Tier 1", finding.Location);
        Assert.Equal("Did you mean 'Wellness injection'? It's in your known items.", finding.Message);
        Assert.Equal("Wellness Injecton", finding.Excerpt);
    }

    // The same typo, caught in the line itself: the known tier's lines are known lines.
    [Fact]
    public void Injecton_in_a_benefit_line_is_worth_a_look_naming_the_known_line()
    {
        var finding = Assert.Single(NearMisses(WithBenefit(new FreeItem(1, "wellness injecton", "per visit")), Knowing(Platinum)));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("Did you mean '1 complimentary wellness injection per visit'? It's in your known items.", finding.Message);
    }

    [Fact]
    public void A_benefit_line_one_letter_off_a_known_one_is_worth_a_look()
    {
        var finding = Assert.Single(NearMisses(WithBenefit(new PercentOff(10, "any qualifing treatments")), Knowing(TenOff)));

        Assert.Equal("Did you mean '10% off any qualifying treatments'? It's in your known items.", finding.Message);
    }

    [Fact]
    public void An_unknown_treatment_in_the_item_field_gets_a_neutral_note()
    {
        var finding = Assert.Single(NearMisses(WithBenefit(new FreeItem(1, "Hydrafacial", "per visit")), Knowing(Wellness)));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("'Hydrafacial' isn't one of your known treatments.", finding.Message);
        Assert.Equal("Hydrafacial", finding.Excerpt);
    }

    [Fact]
    public void An_unknown_tier_or_benefit_line_gets_a_neutral_note()
    {
        var findings = NearMisses(WithTier(new Tier("Diamond Member", 499m, [new BirthdayCredit(100m)])), Knowing(Platinum));

        Assert.Equal(
            ["'$100 birthday credit during your birth month' isn't one of your known benefit lines.",
             "'Diamond Member' isn't one of your known tiers."],
            findings.Select(f => f.Message).Order(StringComparer.Ordinal).ToArray());
        Assert.All(findings, f => Assert.Equal(Severity.Warning, f.Severity));
    }

    [Fact]
    public void A_known_tier_at_another_price_is_worth_a_look()
    {
        var finding = Assert.Single(NearMisses(WithTier(new Tier("Platinum Member", 249m, [new BirthdayCredit(75m)])), Knowing(Platinum)));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("Offer › Tier 1", finding.Location);
        Assert.Equal("'Platinum Member' is $299 in your known items; here it is $249.", finding.Message);
    }

    [Fact]
    public void Plural_and_case_variants_of_known_items_in_offer_details_are_silent()
    {
        var tier = new Tier("platinum MEMBERS", 299m,
        [
            new FreeItem(1, "WELLNESS INJECTIONS", "per visit"),
            new PercentOff(10, "Any Qualifying Treatment"),
            new DiscountedItem(50, "Facials", "per visit"),
        ]);

        Assert.Empty(NearMisses(WithTier(tier), Knowing(Wellness, Facial, Platinum,
            new KnownBenefit(KnownItem.NewId(), new DiscountedItem(50, "facial", "per visit")))));
    }

    [Fact]
    public void What_matches_a_known_item_exactly_draws_nothing()
    {
        Assert.Empty(NearMisses(WithBenefit(new FreeItem(1, "Wellness injection", "per visit")), Knowing(Wellness)));
        Assert.Empty(NearMisses(WithBenefit(new PercentOff(10, "any qualifying treatments")), Knowing(TenOff)));
    }

    [Fact]
    public void With_no_known_items_nothing_is_said()
    {
        var campaign = WithBenefit(new FreeItem(1, "Wellness Injecton", "per visit"));

        Assert.Empty(NearMisses(campaign, null));
        Assert.Empty(NearMisses(campaign, new BusinessContext("Neelam Aesthetics")));
    }

    // The edit budget, both sides of the length threshold, and both sides of each budget.
    [Theory]
    [InlineData("Microdem", "Microderm", true)]      // 9 characters: one edit
    [InlineData("Mcrodem", "Microderm", false)]      // 9 characters: two edits is too many
    [InlineData("Dermplne", "Dermaplane", true)]     // 10 characters: two edits
    [InlineData("Drmplne", "Dermaplane", false)]     // 10 characters: three edits is too many
    [InlineData("Botx", "Botox", true)]              // short: one edit
    [InlineData("Social", "Facial", false)]          // two edits on six characters
    [InlineData("Lasers", "Lashes", false)]
    [InlineData("Facials", "Facial", false)]         // a plural is the same item, not a near miss
    [InlineData("wellness Injection", "Wellness injection", false)] // so is another case
    [InlineData("15% off any qualifying treatments", "10% off any qualifying treatments", false)] // a different number
    [InlineData("10% off any qualifyng treatment", "10% off any qualifying treatments", true)]
    public void A_near_miss_is_one_edit_under_ten_characters_two_at_ten_or_more_with_the_same_numbers(
        string written, string known, bool nearMiss) =>
        Assert.Equal(nearMiss ? known : null, KnownItemMatch.NearMiss(written, [known]));

    // The boundary rows above sit either side of the threshold, wherever it is set.
    [Fact]
    public void The_edit_budget_steps_up_at_the_length_threshold()
    {
        Assert.Equal(KnownItemMatch.LongItemLength, "Dermaplane".Length);
        Assert.Equal(1, KnownItemMatch.EditBudget("Microderm"));
        Assert.Equal(2, KnownItemMatch.EditBudget("Dermaplane"));
    }

    [Theory]
    [InlineData("Facials", "Facial")]
    [InlineData("lash", "Lashes")]
    [InlineData("  Wellness   INJECTIONS ", "Wellness injection")]
    [InlineData("10% off any qualifying treatment", "10% off any qualifying treatments")]
    public void A_plural_or_another_case_is_the_known_item(string written, string known) =>
        Assert.True(KnownItemMatch.IsKnown(written, [known]));

    [Fact]
    public void An_exact_match_to_any_known_item_wins_over_a_near_one() =>
        Assert.Null(KnownItemMatch.NearMiss("Filler", ["Fillet", "Filler"]));

    // The checks run while parts are missing too, as every rule does.
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

    // The neutral note comes with a one-click save of the treatment itself, exactly as written.
    [Fact]
    public void A_benefit_s_treatment_can_be_saved_as_a_known_item()
    {
        Assert.Equal("Hydrafacial", BenefitEditor.Standalone(new FreeItem(1, " Hydrafacial ", "per visit")).TreatmentToKnown()!.Name);
        Assert.Equal("Wellness injection", BenefitEditor.Standalone(new DiscountedItem(50, "Wellness injection", "per visit")).TreatmentToKnown()!.Name);
        Assert.Null(BenefitEditor.Standalone(new PercentOff(10, "any qualifying treatments")).TreatmentToKnown());
        Assert.Null(BenefitEditor.Standalone().TreatmentToKnown());
    }

    // ---- A store that fails never ends her session
    // On an interactive page an exception ends the connection, and her unsaved campaign with it, so
    // a storage failure is told to her and logged, never thrown.

    [Fact]
    public async Task Saving_from_a_campaign_when_the_store_fails_says_so_and_logs_it()
    {
        var log = new ListLogger<KnownItemsSession>();

        var message = await KnownItemsSession.SaveFromCampaignAsync(
            new FailingKnownItems { FailWrites = true }, Trail, Platinum, log);

        Assert.Equal("Couldn't save that to your known items. Your campaign is untouched. Try again in a moment.", message);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.IsType<Azure.RequestFailedException>(entry.Error);
        Assert.Contains(Platinum.Id, entry.Message);
        Assert.DoesNotContain("Platinum", entry.Message);
    }

    // Anything else the table client throws is a storage failure too; a refusal still says why.
    [Fact]
    public async Task Any_store_failure_from_a_campaign_is_a_message_but_a_refusal_keeps_its_reason()
    {
        var log = new ListLogger<KnownItemsSession>();
        var timingOut = new FailingKnownItems { FailWrites = true, Failure = () => new TimeoutException("The operation timed out.") };

        Assert.Equal(KnownItemsSession.CampaignSaveFailed, await KnownItemsSession.SaveFromCampaignAsync(timingOut, Trail, Wellness, log));
        Assert.IsType<TimeoutException>(Assert.Single(log.Entries).Error);
        Assert.Equal("Fill in the treatment's name.",
            await KnownItemsSession.SaveFromCampaignAsync(new InMemoryKnownItems(), Trail, new KnownTreatment(KnownItem.NewId(), " "), log));
    }

    [Fact]
    public async Task Adding_changing_or_removing_when_the_store_fails_says_so_and_keeps_what_she_typed()
    {
        var store = new FailingKnownItems();
        await store.AddAsync(SalonOne, Wellness);
        var log = new ListLogger<KnownItemsSession>();
        var session = await KnownItemsSession.OpenAsync(store, Trail, log);
        store.FailWrites = true;

        session.NewTreatment = "Hydrafacial";
        Assert.False(await session.AddTreatmentAsync());
        Assert.Equal([KnownItemsSession.SaveFailed], session.Errors);
        Assert.Equal("Hydrafacial", session.NewTreatment);

        session.StartEdit(Wellness);
        session.EditTreatment = "Wellness injections";
        Assert.False(await session.SaveEditAsync());
        Assert.Equal([KnownItemsSession.SaveFailed], session.Errors);
        Assert.Equal(Wellness.Id, session.Editing!.Id);
        Assert.Equal("Wellness injections", session.EditTreatment);

        await session.RemoveAsync(Wellness);
        Assert.Equal([KnownItemsSession.RemoveFailed], session.Errors);
        Assert.Equal([Wellness.Id], session.Items.All.Select(i => i.Id));

        Assert.Equal(3, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.IsType<Azure.RequestFailedException>(e.Error));
        Assert.All(log.Entries, e => Assert.DoesNotContain("Hydrafacial", e.Message));
    }

    // A change that went through stands even when the list cannot be read again just after it.
    [Fact]
    public async Task A_change_stands_when_the_list_cannot_be_read_again()
    {
        var store = new FailingKnownItems();
        var log = new ListLogger<KnownItemsSession>();
        var session = await KnownItemsSession.OpenAsync(store, Trail, log);
        store.FailReads = true;

        session.NewTreatment = "Hydrafacial";
        Assert.True(await session.AddTreatmentAsync());

        Assert.Equal("Added \"Hydrafacial\".", session.Message);
        store.FailReads = false;
        Assert.Equal(["Hydrafacial"], (await store.ForClientAsync(SalonOne)).All.Select(i => i.Text));
        Assert.IsType<Azure.RequestFailedException>(Assert.Single(log.Entries).Error);
    }

    // Each way a row can be malformed is left out and logged by its key, never its content; the good
    // row is kept. A row of another client's partition is refused outright, never shown.
    [Fact]
    public void A_malformed_row_is_left_out_and_logged_by_its_key_alone()
    {
        Azure.Data.Tables.TableEntity Row(string id, params (string Key, object Value)[] columns)
        {
            var row = new Azure.Data.Tables.TableEntity(SalonOne.Value, id);
            foreach (var (key, value) in columns) row[key] = value;
            return row;
        }
        var good = KnownItemTable.FromItem(SalonOne, Wellness);
        var bad = new[]
        {
            Row(KnownItem.NewId(), ("Kind", "Treatment")),                                           // no text
            Row(KnownItem.NewId(), ("Kind", "Voucher"), ("Text", "Secret one")),                    // no such kind
            Row(KnownItem.NewId(), ("Kind", "Tier"), ("Text", "Secret two"), ("Price", "lots"), ("Benefits", "[]")),
            Row(KnownItem.NewId(), ("Kind", "Tier"), ("Text", "Secret three"), ("Price", "299"), ("Benefits", "{not json")),
            Row(KnownItem.NewId(), ("Kind", "Benefit"), ("Text", "Secret four"), ("Benefit", "[]")), // no benefit in it
        };
        var log = new ListLogger<KnownItemTable>();

        var items = KnownItemTable.ReadAll(SalonOne, [good, .. bad], log);

        Assert.Equal([Wellness.Id], items.All.Select(i => i.Id));
        Assert.Equal(bad.Select(r => r.RowKey).Order(), log.Entries.Select(e => bad.Single(r => e.Message.Contains(r.RowKey)).RowKey).Order());
        Assert.All(log.Entries, e => Assert.Null(e.Error));
        Assert.All(log.Entries, e => Assert.DoesNotContain("Secret", e.Message));
        Assert.All(log.Entries, e => Assert.DoesNotContain("lots", e.Message));

        var elsewhere = KnownItemTable.FromItem(SalonTwo, Wellness);
        Assert.Throws<InvalidDataException>(() => KnownItemTable.ReadAll(SalonOne, [good, elsewhere], log));
    }

    // The known items table, failing on demand as the real one does when it is unreachable.
    private sealed class FailingKnownItems : IKnownItemStore
    {
        private readonly InMemoryKnownItems _inner = new();

        public bool FailWrites { get; set; }
        public bool FailReads { get; set; }
        public Func<Exception> Failure { get; init; } = () => new Azure.RequestFailedException(503, "The table service is unavailable.");

        public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default) =>
            FailReads ? throw Failure() : _inner.ForClientAsync(client, cancellationToken);

        public Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) =>
            FailWrites ? throw Failure() : _inner.AddAsync(client, item, cancellationToken);

        public Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) =>
            FailWrites ? throw Failure() : _inner.UpdateAsync(client, item, cancellationToken);

        public Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default) =>
            FailWrites ? throw Failure() : _inner.RemoveAsync(client, id, cancellationToken);
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
