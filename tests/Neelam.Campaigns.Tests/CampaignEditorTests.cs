using System.Reflection;
using System.Text.Json.Serialization;

namespace Neelam.Campaigns.Tests;

/// <summary>The campaign page's model: every field writes through to the draft, which has the last word.</summary>
public class CampaignEditorTests
{
    private static CampaignEditor Start() => CampaignEditor.Start(DraftFixtures.Membership);

    private static T Block<T>(CampaignEditor editor, string label) where T : BlockEditor =>
        (T)editor.Blocks.Single(b => b.Label == label);

    private static OfferEditor OfferOf(CampaignEditor editor) => Block<OfferBlockEditor>(editor, "Offer").Offer;

    private static BenefitEditor AddBenefit(TierEditor tier, string kind, Action<BenefitEditor> fill)
    {
        var benefit = tier.AddBenefit();
        benefit.Kind = kind;
        fill(benefit);
        return benefit;
    }

    // ---- Starting and typing

    [Fact]
    public void A_new_campaign_shows_the_template_s_fixed_parts_and_leaves_the_rest_empty()
    {
        var editor = Start();

        Assert.Equal(["Header", "Greeting", "Closing", "Sign-off", "Disclaimer"],
            editor.Blocks.Where(b => b.IsFixed).Select(b => b.Label));
        Assert.Contains(Block<BlockEditor>(editor, "Sign-off").FixedPreview, p => p.Text.Contains("With gratitude,"));
        Assert.Empty(Block<BlockEditor>(editor, "Headline").FixedPreview);
        Assert.Equal("", editor.Subject.Text);
        Assert.Equal("", Block<TextBlockEditor>(editor, "Headline").Field.Text);
        Assert.Empty(OfferOf(editor).Tiers);
        Assert.Equal("Untitled campaign from Membership announcement", editor.Title);
    }

    [Fact]
    public void Typing_fills_the_draft_as_entered_and_clearing_empties_it()
    {
        var editor = Start();

        editor.Subject.Text = "  WE’RE TURNING ONE!  ";
        Assert.Equal(Origin.Entered, editor.Draft.Subject.Origin);
        Assert.Equal("WE’RE TURNING ONE!", editor.Draft.Subject.Value);
        Assert.Equal("WE’RE TURNING ONE!", editor.Title);
        Assert.DoesNotContain(editor.Status().Missing, m => m.Location == "Subject");

        editor.Subject.Text = "   ";
        Assert.False(editor.Draft.Subject.HasValue);
        Assert.Contains(editor.Status().Missing, m => m.Location == "Subject" && m.Rule == "draft-missing");
    }

    [Fact]
    public void Paragraphs_are_the_chunks_between_blank_lines()
    {
        var editor = Start();

        Block<ParagraphsBlockEditor>(editor, "Opening").Text = "One.\r\n\r\nTwo,\nstill two.\n  \nThree.";

        Assert.Equal(["One.", "Two,\nstill two.", "Three."], editor.Draft.Paragraphs("Opening").Value);
    }

    [Fact]
    public void A_button_needs_its_text_and_a_web_address_and_says_so_until_it_has_both()
    {
        var editor = Start();
        var button = Block<ButtonBlockEditor>(editor, "Call to action");
        var slot = editor.Draft.Button("Call to action");

        button.ButtonLabel = "Join the Beauty Bank";
        Assert.False(slot.HasValue);
        Assert.Equal("Call to action: Fill in the button's link.", Assert.Single(editor.Errors()));

        button.ButtonUrl = "example.com/join";
        Assert.Contains("is not a web address", button.Error);
        Assert.False(slot.HasValue);

        button.ButtonUrl = "https://example.com/join";
        Assert.Null(button.Error);
        Assert.Empty(editor.Errors());
        Assert.Equal(new CallToAction("Join the Beauty Bank", new Uri("https://example.com/join")), slot.Value);

        // Nothing typed is not an error, only missing.
        button.ButtonLabel = "";
        button.ButtonUrl = "";
        Assert.Null(button.Error);
        Assert.False(slot.HasValue);
    }

    // An optional photo left empty leaves the block out of the email, and the draft still builds.
    [Fact]
    public void An_optional_photo_left_empty_is_left_out()
    {
        var editor = CampaignEditor.Open(DraftFixtures.Finished());

        Block<ImageBlockEditor>(editor, "Photo").PhotoName = "  ";

        Assert.False(editor.Draft.Image("Photo").HasValue);
        var status = editor.Status();
        Assert.Empty(status.Missing);
        Assert.DoesNotContain(status.Preview!, b => b.Kind == BlockKind.Image);
    }

    // ---- Tiers and typed benefits

    [Fact]
    public void A_price_must_be_an_amount_in_dollars()
    {
        var editor = Start();
        var tier = OfferOf(editor).AddTier();
        var price = editor.Draft.Offer("Offer").Tiers[0].MonthlyPrice;

        tier.Price.Text = "$149";
        Assert.Equal(149m, price.Value);
        tier.Price.Text = "149.50";
        Assert.Equal(149.50m, price.Value);

        tier.Price.Text = "-5";
        Assert.False(price.HasValue);
        Assert.Equal("Offer › Tier 1 › Price: \"-5\" is not an amount in dollars, such as 149 or 149.50.", Assert.Single(editor.Errors()));
    }

    public static TheoryData<string, Action<BenefitEditor>, Benefit> EveryKind => new()
    {
        { "birthday-credit", b => b.Amount = "25", new BirthdayCredit(25m) },
        { "percent-off", b => { b.Percent = "10%"; b.AppliesTo = "any qualifying treatments"; }, new PercentOff(10, "any qualifying treatments") },
        { "free-item", b => { b.Quantity = "1"; b.ItemName = "wellness injection"; b.Per = "per visit"; }, new FreeItem(1, "wellness injection", "per visit") },
        {
            "discounted-item", b => { b.Percent = "50"; b.ItemName = "wellness injection"; b.Per = "per visit"; b.Condition = "any additional"; },
            new DiscountedItem(50, "wellness injection", "per visit", "any additional")
        },
    };

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Each_kind_of_benefit_has_its_own_fields_and_reopens_with_them(string kind, Action<BenefitEditor> fill, Benefit expected)
    {
        var editor = Start();
        var benefit = AddBenefit(OfferOf(editor).AddTier(), kind, fill);

        Assert.Null(benefit.Error);
        Assert.Equal(expected, editor.Draft.Offer("Offer").Tiers[0].Benefits[0].Value);
        Assert.Equal(expected.Describe(), benefit.Sentence);

        var reopened = OfferOf(CampaignEditor.Open(CampaignJson.DeserializeDraft(CampaignJson.SerializeDraft(editor.Draft))))
            .Tiers[0].Benefits[0];
        Assert.Equal(kind, reopened.Kind);
        Assert.Equal(expected.Describe(), reopened.Sentence);
        Assert.Equal(Origin.Entered, reopened.Origin);
    }

    [Fact]
    public void A_benefit_with_only_some_fields_is_empty_and_says_what_is_left()
    {
        var editor = Start();
        var tier = OfferOf(editor).AddTier();
        var benefit = tier.AddBenefit();

        benefit.Kind = "free-item";
        Assert.Null(benefit.Error); // chosen, nothing typed: missing, not wrong

        benefit.Quantity = "one";
        benefit.ItemName = "wellness injection";
        Assert.Equal("\"one\" is not a whole number. Fill in how often, such as \"per visit\".", benefit.Error);
        Assert.False(editor.Draft.Offer("Offer").Tiers[0].Benefits[0].HasValue);
        Assert.Contains("Offer › Tier 1 › Benefit 1: \"one\" is not a whole number.", Assert.Single(editor.Errors()));
    }

    // The form offers exactly the benefit types the model declares, so a new one cannot be missed.
    [Fact]
    public void The_benefit_kinds_offered_are_exactly_the_model_s() =>
        Assert.Equal(
            typeof(Benefit).GetCustomAttributes<JsonDerivedTypeAttribute>()
                .Select(a => ((string)a.TypeDiscriminator!, a.DerivedType)).OrderBy(k => k.Item1, StringComparer.Ordinal),
            BenefitKind.All.Select(k => (k.Key, k.Type)).OrderBy(k => k.Key, StringComparer.Ordinal));

    // The owner's call (2026-10-09): the copy keeps the name and price, and the rules, not the copy,
    // see that one of the two tiers changes each. Only the benefits wait to be looked at.
    [Fact]
    public void A_copied_tier_keeps_its_name_and_price_and_each_benefit_waits_until_changed_or_confirmed()
    {
        var editor = Start();
        var offer = OfferOf(editor);
        var gold = offer.AddTier();
        gold.Name.Text = "Gold Member";
        gold.Price.Text = "149";
        AddBenefit(gold, "birthday-credit", b => b.Amount = "25");
        AddBenefit(gold, "percent-off", b => { b.Percent = "5"; b.AppliesTo = "any qualifying treatments"; });

        var copy = offer.CopyTier(gold);

        Assert.Equal(["Gold Member", "149"], new[] { copy.Name.Text, copy.Price.Text });
        var copied = editor.Draft.Offer("Offer").Tiers[1];
        Assert.Equal([Origin.Entered, Origin.Entered], new[] { copied.Name.Origin, copied.MonthlyPrice.Origin });
        Assert.DoesNotContain(editor.Status().Missing, m => m.Location is "Offer › Tier 2 › Name" or "Offer › Tier 2 › Price");
        Assert.All(copy.Benefits, b => Assert.True(b.IsUnreviewed));
        Assert.Equal("Tier 1", copy.Benefits[0].CopiedFrom);
        Assert.Equal("25", copy.Benefits[0].Amount);
        Assert.Contains(editor.Status().Missing, m => m.Rule == "draft-unreviewed-copy" && m.Location == "Offer › Tier 2 › Benefit 1");

        copy.Benefits[0].Confirm();
        copy.Benefits[1].Percent = "10";

        Assert.All(copy.Benefits, b => Assert.False(b.IsUnreviewed));
        Assert.DoesNotContain(editor.Status().Missing, m => m.Rule == "draft-unreviewed-copy");
        Assert.Equal([new BirthdayCredit(25m), new PercentOff(10, "any qualifying treatments")],
            editor.Draft.Offer("Offer").Tiers[1].Benefits.Select(b => b.Value));
    }

    // The draft copies every benefit's value, so an empty one has to go before a tier can be copied.
    [Fact]
    public void A_tier_with_an_empty_benefit_is_copied_only_once_it_is_filled_or_removed()
    {
        var offer = OfferOf(Start());
        var gold = offer.AddTier();
        AddBenefit(gold, "birthday-credit", b => b.Amount = "25");
        var empty = gold.AddBenefit();

        Assert.False(offer.CanCopy(gold));
        Assert.Throws<InvalidOperationException>(() => offer.CopyTier(gold));

        gold.RemoveBenefit(empty);
        Assert.True(offer.CanCopy(gold));
        Assert.Single(offer.CopyTier(gold).Benefits);
    }

    // ---- Ordering tiers by price (owner, 2026-10-09: "a fast ordering of tiers top to bottom or bottom to top")

    private static OfferEditor ThreeTiers(CampaignEditor editor, params string[] prices)
    {
        var offer = OfferOf(editor);
        for (var i = 0; i < prices.Length; i++)
        {
            var tier = offer.AddTier();
            tier.Name.Text = $"Tier {(char)('A' + i)}";
            tier.Price.Text = prices[i];
            AddBenefit(tier, "birthday-credit", b => b.Amount = (25 * (i + 1)).ToString());
        }
        return offer;
    }

    private static string[] Names(OfferEditor offer) => offer.Tiers.Select(t => t.Name.Text).ToArray();

    [Fact]
    public void Lowest_or_highest_price_first_reorders_the_form_and_the_draft_together()
    {
        var editor = Start();
        var offer = ThreeTiers(editor, "299", "149", "199");

        offer.OrderByPrice(highestFirst: false);
        Assert.Equal(["Tier B", "Tier C", "Tier A"], Names(offer));
        Assert.Equal([149m, 199m, 299m], editor.Draft.Offer("Offer").Tiers.Select(t => t.MonthlyPrice.Value));

        offer.OrderByPrice(highestFirst: true);
        Assert.Equal(["Tier A", "Tier C", "Tier B"], Names(offer));
        Assert.Equal([299m, 199m, 149m], editor.Draft.Offer("Offer").Tiers.Select(t => t.MonthlyPrice.Value));
        // Each tier's fields move with it.
        Assert.Equal("75", offer.Tiers[1].Benefits[0].Amount);
    }

    // Equal prices keep their order; a tier with no price yet goes last, either way.
    [Fact]
    public void Ordering_is_stable_and_leaves_an_unpriced_tier_last()
    {
        var editor = Start();
        var offer = ThreeTiers(editor, "", "199", "99", "199");

        offer.OrderByPrice(highestFirst: false);
        Assert.Equal(["Tier C", "Tier B", "Tier D", "Tier A"], Names(offer));
        offer.OrderByPrice(highestFirst: true);
        Assert.Equal(["Tier B", "Tier D", "Tier C", "Tier A"], Names(offer));
    }

    // Either order is a direction tier-prices-increase accepts.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Either_order_satisfies_the_price_rule(bool highestFirst)
    {
        var editor = CampaignEditor.Open(DraftFixtures.Finished());
        var offer = OfferOf(editor);
        var silver = offer.AddTier();
        silver.Name.Text = "Silver Member";
        silver.Price.Text = "99";
        AddBenefit(silver, "birthday-credit", b => b.Amount = "10");
        Assert.Contains("tier-prices-increase", BlockerRules(editor));   // $149, $299, $99: mixed

        offer.OrderByPrice(highestFirst);

        Assert.DoesNotContain("tier-prices-increase", BlockerRules(editor));
        Assert.DoesNotContain(editor.Status().Review!.Findings, f => f.Rule.StartsWith("tier-rung"));
    }

    // WIP noted a copied benefit's "Tier N" going stale when tiers move: it names its tier's new
    // number, in the form and in Still to do, and a removed source says so.
    [Fact]
    public void A_copied_benefit_keeps_naming_its_source_after_tiers_move_or_go()
    {
        var editor = Start();
        var offer = ThreeTiers(editor, "199", "299");
        var copy = offer.CopyTier(offer.Tiers[1]);
        copy.Price.Text = "99";
        Assert.Equal("Tier 2", copy.Benefits[0].CopiedFrom);

        offer.OrderByPrice(highestFirst: false);

        Assert.Same(copy, offer.Tiers[0]);
        Assert.Equal("Tier 3", copy.Benefits[0].CopiedFrom);
        Assert.Contains(editor.Status().Missing, m => m.Rule == "draft-unreviewed-copy"
            && m.Location == "Offer › Tier 1 › Benefit 1" && m.Message.StartsWith("Offer › Tier 1 › Benefit 1 was copied from Tier 3 and"));

        offer.RemoveTier(offer.Tiers[1]);
        Assert.Equal("Tier 2", copy.Benefits[0].CopiedFrom);
        offer.RemoveTier(offer.Tiers[1]);
        Assert.Equal("a tier since removed", copy.Benefits[0].CopiedFrom);
    }

    // A copy whose benefits are looked at but whose name and price are as copied.
    private static CampaignEditor CopiedAsItStands() => CampaignEditor.Open(DraftFixtures.CopiedAsItStands());

    private static string[] BlockerRules(CampaignEditor editor) =>
        editor.Status().Review!.Blockers.Select(f => f.Rule).Order(StringComparer.Ordinal).ToArray();

    // The rules are what make her change a copied name and price, and they do not say which tier.
    // Each is one finding, however many tiers it names.
    [Fact]
    public void A_copy_left_with_its_name_and_price_is_a_must_fix_for_each_until_either_tier_changes()
    {
        var status = CopiedAsItStands().Status();

        Assert.Empty(status.Missing);
        Assert.Equal(
            [
                ("tier-names-unique", "Tiers 1 and 2 share the name 'Gold Member'; customers cannot tell them apart. Rename either one."),
                ("tier-prices-increase", "Tier 2 costs the same as tier 1; change either price."),
            ],
            status.Review!.Blockers.Select(f => (f.Rule, f.Message)).OrderBy(f => f.Rule, StringComparer.Ordinal));
    }

    // With three alike, renaming either one is not enough, and the one finding says so.
    [Fact]
    public void Three_tiers_sharing_a_name_are_one_finding_asking_to_rename_all_but_one()
    {
        var draft = DraftFixtures.CopiedAsItStands();
        var third = draft.Offer("Offer").CopyTier(1);
        third.MonthlyPrice.Set(299m);
        third.Benefits[0].Set(new BirthdayCredit(100m));
        third.Benefits[1].Confirm();
        third.Benefits[2].Confirm();

        var finding = Assert.Single(CampaignEditor.Open(draft).Status().Review!.Blockers, f => f.Rule == "tier-names-unique");

        Assert.Equal(
            "Tiers 1 and 2 and 3 share the name 'Gold Member'; customers cannot tell them apart. Rename all but one of them.",
            finding.Message);
    }

    [Theory]
    [InlineData(1)] // the copy
    [InlineData(0)] // the original instead
    public void Renaming_either_tier_clears_the_shared_name(int tier)
    {
        var editor = CopiedAsItStands();

        OfferOf(editor).Tiers[tier].Name.Text = "Platinum Member";

        Assert.Equal(["tier-prices-increase"], BlockerRules(editor));
    }

    [Theory]
    [InlineData(1, "299")] // the copy, up
    [InlineData(0, "99")] // the original instead, down
    public void Changing_either_price_clears_the_price_finding(int tier, string price)
    {
        var editor = CopiedAsItStands();

        OfferOf(editor).Tiers[tier].Price.Text = price;

        Assert.Equal(["tier-names-unique"], BlockerRules(editor));
    }

    [Fact]
    public void Removing_tiers_and_benefits_keeps_the_form_and_the_draft_in_step()
    {
        var editor = Start();
        var offer = OfferOf(editor);
        var first = offer.AddTier();
        var second = offer.AddTier();
        first.Name.Text = "First";
        second.Name.Text = "Second";

        offer.RemoveTier(first);
        var one = AddBenefit(second, "birthday-credit", b => b.Amount = "1");
        AddBenefit(second, "birthday-credit", b => b.Amount = "2");
        second.RemoveBenefit(one);

        var tiers = editor.Draft.Offer("Offer").Tiers;
        Assert.Equal([second], offer.Tiers);
        Assert.Equal(["Second"], tiers.Select(t => t.Name.Value));
        Assert.Equal([new BirthdayCredit(2m)], tiers[0].Benefits.Select(b => b.Value));
        Assert.Equal(["2"], second.Benefits.Select(b => b.Amount));
    }

    // ---- Where it stands

    [Fact]
    public void A_finished_draft_has_nothing_missing_and_the_whole_email_s_findings_and_preview()
    {
        var finished = CampaignEditor.Open(DraftFixtures.Finished()).Status();
        var campaign = DraftFixtures.Finished().Build().Campaign!;

        Assert.Empty(finished.Missing);
        Assert.Equal(CampaignReview.Check(campaign).Findings, finished.Review!.Findings);
        Assert.Equal(finished.Review.Findings, finished.Findings);
        Assert.Equal(EditorExport.PreviewBlocks(campaign), finished.Preview);
    }

    // Priya's case: the email as sent had no terms link, and the checks still catch the tier names.
    [Fact]
    public void With_a_part_missing_the_rules_still_check_what_is_filled_in()
    {
        var status = CampaignEditor.Open(DraftFixtures.SameNamesNoTerms()).Status();

        var missing = Assert.Single(status.Missing);
        Assert.Equal((Severity.Blocker, "Offer › Terms link has not been filled in."), (missing.Severity, missing.Message));
        Assert.Contains(status.Findings, f => f.Severity == Severity.Blocker && f.Rule == "tier-names-unique"
            && f.Message == "Tiers 1 and 2 share the name 'Platinum Member'; customers cannot tell them apart. Rename either one.");
    }

    // The client's own policy is what the checks run with while parts are missing, as once the
    // draft builds: a term she restricts is flagged in what is filled in so far.
    [Fact]
    public void With_a_part_missing_the_checks_use_the_client_s_own_policy()
    {
        var editor = CampaignEditor.Open(DraftFixtures.SameNamesNoTerms());
        var policy = CampaignPolicy.Default with
        {
            RestrictedTerms = new Dictionary<string, string> { ["platinum"] = "Platinum is a partner's trademark." },
        };

        var own = editor.Status(policy);
        var defaults = editor.Status();

        Assert.NotEmpty(own.Missing);
        Assert.Contains(own.Findings, f => f.Rule == "restricted-term" && f.Message.Contains("Platinum is a partner's trademark."));
        // Hers replaces the defaults rather than adding to them.
        Assert.DoesNotContain(own.Findings, f => f.Rule == "restricted-term" && f.Message.StartsWith("'bank'"));
        Assert.Contains(defaults.Findings, f => f.Rule == "restricted-term" && f.Message.StartsWith("'bank'"));
    }

    // An optional part left empty is left out of the preview so far, as it would be sent, rather
    // than marked as something still to fill in.
    [Fact]
    public void With_a_part_missing_an_empty_optional_photo_has_no_placeholder()
    {
        var draft = DraftFixtures.Finished();
        draft.Offer("Offer").TermsUrl.Clear();
        var editor = CampaignEditor.Open(draft);
        Block<ImageBlockEditor>(editor, "Photo").PhotoName = "";

        var status = editor.Status();

        Assert.NotEmpty(status.Missing);
        Assert.DoesNotContain(status.Preview, b => b.Kind == BlockKind.Image);
        Assert.DoesNotContain(status.Preview, b => b.Text.Contains("‹Photo"));
        Assert.Contains(status.Preview, b => b.Text.Contains("‹Terms link: not filled in yet›"));
    }

    // A price with cents keeps them, in the finished email and in the one so far alike; a whole
    // price has none. It reads as in US English, the clinic's, whatever the server's culture: a
    // German one writes "149,50" unless told otherwise.
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void A_price_with_cents_is_shown_with_its_cents(string serverCulture)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(serverCulture);
        try
        {
            var draft = DraftFixtures.Finished();
            draft.Offer("Offer").Tiers[0].MonthlyPrice.Set(149.50m);
            var finished = string.Join("\n", CampaignEditor.Open(draft).Status().Preview.Select(b => b.Text));
            draft.Offer("Offer").TermsUrl.Clear();
            var soFar = string.Join("\n", CampaignEditor.Open(draft).Status().Preview.Select(b => b.Text));

            foreach (var text in new[] { finished, soFar })
            {
                Assert.Contains("🤍 $149.50/month", text);
                Assert.Contains("🤍 $299/month", text);
            }
            Assert.Contains("not filled in yet", soFar);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void The_preview_marks_each_missing_part_and_shows_the_rest_as_the_export_would()
    {
        var campaign = DraftFixtures.Finished().Build().Campaign!;
        var finished = EditorExport.PreviewBlocks(campaign);
        var terms = $"Full terms: {campaign.OfferOf().TermsUrl}";
        var draft = DraftFixtures.Finished();
        draft.Offer("Offer").TermsUrl.Clear();
        draft.Button("Call to action").Clear();

        var preview = CampaignEditor.Open(draft).Status().Preview;

        // The same email, block for block, with a placeholder where each missing part goes.
        var expected = finished.Select(b => b.Kind == BlockKind.Button
            ? new EditorBlock(BlockKind.Button, "‹The button (Call to action): not filled in yet›")
            : b with { Text = b.Text.Replace(terms, "‹Terms link: not filled in yet›") });
        Assert.Equal(expected, preview);
        Assert.Contains(finished, b => b.Text.Contains(terms));
    }

    // One placeholder style, the template's: "‹Name: state›", never a breadcrumb's "›" inside the
    // marks (the walkthrough read "‹Offer › Terms link: not filled in yet›").
    [Fact]
    public void Every_placeholder_is_one_name_in_one_pair_of_marks()
    {
        var editor = CampaignEditor.Start(DraftFixtures.Membership);
        var offer = editor.Blocks.OfType<OfferBlockEditor>().Single().Offer;
        offer.AddTier();
        offer.AddTier().AddBenefit();

        var text = string.Join("\n", editor.Status().Preview.Select(b => b.Text));
        var placeholders = System.Text.RegularExpressions.Regex.Matches(text, "‹[^‹›]*›").Select(m => m.Value).ToList();

        Assert.Equal(text.Count(c => c == '›'), placeholders.Count);
        Assert.Equal(text.Count(c => c == '‹'), placeholders.Count);
        Assert.Contains("‹Offer name: not filled in yet›", placeholders);
        Assert.Contains("‹Terms link: not filled in yet›", placeholders);
        Assert.Contains("‹Tier 1 name: not filled in yet›", placeholders);
        Assert.Contains("‹Tier 1 price: not filled in yet›", placeholders);
        Assert.Contains("‹Tier 1 benefits: not filled in yet›", placeholders);
        Assert.Contains("‹Tier 2, benefit 1: not filled in yet›", placeholders);
        // The same style as the template's own preview.
        Assert.Contains(TemplatePreview.Blocks(DraftFixtures.Membership.Blocks), b => b.Text == "‹Headline: written for each campaign›");
    }

    // Findings name tiers by their place in the form, so a finished tier after an unfinished one
    // waits for it rather than being numbered as if the gap were not there.
    [Fact]
    public void A_tier_after_an_unfinished_one_is_checked_once_that_one_is_finished()
    {
        // Tier 1 "Gold Member", tier 2 with its name cleared, tier 3 "Gold Member" again.
        var draft = DraftFixtures.Finished();
        var offer = draft.Offer("Offer");
        var third = offer.CopyTier(0);
        third.Name.Set("Gold Member");
        third.MonthlyPrice.Set(399m);
        offer.Tiers[1].Name.Clear();

        var status = CampaignEditor.Open(draft).Status();

        Assert.Contains(status.Missing, m => m.Location == "Offer › Tier 2 › Name");
        Assert.DoesNotContain(status.Findings, f => f.Rule == "tier-names-unique");
        Assert.Contains("‹Tier 2 name: not filled in yet›:", string.Join("\n", status.Preview.Select(b => b.Text)));
    }

    // Nothing about the gate loosens: until the draft builds there is no review at all to export.
    [Fact]
    public void A_draft_with_parts_missing_has_no_review_to_export()
    {
        var status = CampaignEditor.Open(DraftFixtures.SameNamesNoTerms()).Status();

        Assert.NotEmpty(status.Missing);
        Assert.NotEmpty(status.Findings);
        Assert.Null(status.Review);
    }

    // The owner's rule: export needs the AI proofread too, so the page's review can never export.
    [Fact]
    public void The_rules_alone_never_unlock_export()
    {
        var review = CampaignEditor.Open(DraftFixtures.Finished()).Status().Review!;

        Assert.False(review.Proofread);
        Assert.False(review.CanExport);
        Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(review));
    }
}
