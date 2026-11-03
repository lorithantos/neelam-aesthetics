using System.Text.Json.Nodes;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Content a template starts each campaign with, which each campaign can change (owner, 2026-11-03:
/// "the real problem is that there is no "default text""): one choice of three per block in place of two
/// checkboxes, any block but a spacer, offers and promotions included and possibly only part of one; kept
/// in the template's JSON as an object that old templates simply lack; filled into a new campaign as if
/// she had typed it; and replaced by Update to template only where she has not changed it and the version
/// on her page is not approved ("Updating should modify only those that have the default and are not
/// approved").
/// </summary>
public class TemplateDefaultTests
{
    private static readonly Guid TemplateId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Version = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Version.AddHours(1);

    // A fixed greeting, a headline, a tiered offer and a button, built as the template page builds it: the
    // headline and the offer starting each campaign with what is given here, or with nothing.
    private static CampaignTemplate News(
        string? headline, string greeting = "Hi Beautiful", Action<OfferEditor>? offer = null, bool fixedHeadline = false)
    {
        var editor = TemplateEditor.StartBlank(Catalogs.Shipped);
        editor.Name = "News";
        var g = editor.Add(BlockType.Greeting);
        (g.Label, g.Content, g.Text) = ("Greeting", BlockContent.Fixed, greeting);
        var h = editor.Add(BlockType.Heading);
        h.Label = "Headline";
        if (headline is not null) (h.Content, h.Text) = (fixedHeadline ? BlockContent.Fixed : BlockContent.Default, headline);
        var o = editor.Add(BlockType.Offer);
        o.Label = "Offer";
        if (offer is not null)
        {
            o.Content = BlockContent.Default;
            offer(o.DefaultOffer!);
        }
        editor.Add(BlockType.Button).Label = "Call to action";
        return editor.Build();
    }

    // The Beauty Bank, its gold tier named and given a birthday credit, its price left to each campaign.
    private static void BeautyBank(OfferEditor offer) => BeautyBank(offer, "Beauty Bank");

    private static void BeautyBank(OfferEditor offer, string name)
    {
        offer.Name.Text = name;
        var gold = offer.AddTier();
        gold.Name.Text = "Gold Member";
        gold.AddKnownBenefit(new BirthdayCredit(25m));
    }

    // ---- The template

    // A template saved before 2026-11-03, as that build wrote it: read exactly as before, nothing to start with.
    [Fact]
    public void A_template_saved_before_reads_as_it_did_with_nothing_to_start_with()
    {
        const string old = """
            {
              "schema": 2,
              "template": {
                "name": "News",
                "blocks": [
                  { "label": "Greeting", "type": "greeting", "required": true,
                    "fixed": { "block": "greeting", "label": "Greeting", "text": "Hi Beautiful" }, "recurring": false },
                  { "label": "Headline", "type": "heading", "required": false, "recurring": false }
                ]
              }
            }
            """;

        var template = CampaignJson.DeserializeTemplate(old);

        Assert.All(template.Blocks, b => Assert.Null(b.Default));
        Assert.Equal("Hi Beautiful", Assert.IsType<GreetingBlock>(template.Blocks[0].Fixed).Text);
        var draft = template.Start();
        Assert.Equal(Origin.Template, draft.Text("Greeting").Origin);
        Assert.False(draft.Text("Headline").HasValue);
        // And written as it was: no new field where there is nothing to start with.
        Assert.DoesNotContain("\"default\"", CampaignJson.SerializeTemplate(template));
        Assert.DoesNotContain("\"default\"", CampaignJson.SerializeTemplate(DraftFixtures.Membership));
    }

    [Fact]
    public void What_a_campaign_starts_with_is_kept_in_the_template_s_json_as_an_object_and_reads_back()
    {
        var template = News("NEWS FROM NEELAM", offer: BeautyBank);

        var json = CampaignJson.SerializeTemplate(template);
        var blocks = JsonNode.Parse(json)!["template"]!["blocks"]!.AsArray();

        Assert.Null(blocks[0]!["default"]);
        Assert.Equal("NEWS FROM NEELAM", (string?)blocks[1]!["default"]!["text"]!["value"]);
        Assert.Equal("Beauty Bank", (string?)blocks[2]!["default"]!["offer"]!["name"]!["value"]);
        Assert.Null(blocks[3]!["default"]);
        var back = CampaignJson.DeserializeTemplate(json);
        Assert.Equal(template.Blocks, back.Blocks);
        Assert.Equal(json, CampaignJson.SerializeTemplate(back));
    }

    [Fact]
    public void Content_is_either_the_same_every_time_or_a_start_and_a_spacer_has_none()
    {
        var start = News("NEWS").Blocks[1].Default!;

        Assert.Contains("not both", Assert.Throws<ArgumentException>(() => new CampaignTemplate("News",
            [new TemplateBlock("Headline", BlockType.Heading, Fixed: new HeadingBlock("Headline", "NEWS"), Default: start)])).Message);
        Assert.Contains("a spacer has nothing to start with", Assert.Throws<ArgumentException>(() =>
            new CampaignTemplate("News", [new TemplateBlock("Space", BlockType.Spacer, Default: start)])).Message);
        // A heading's text is no start for an offer.
        Assert.Contains("does not fit its type (Offer)",Assert.Throws<ArgumentException>(() =>
            new CampaignTemplate("News", [new TemplateBlock("Offer", BlockType.Offer, Default: start)])).Message);
    }

    // ---- A new campaign

    [Fact]
    public void A_new_campaign_starts_with_it_as_if_she_had_typed_it_and_can_change_it()
    {
        var template = News("NEWS FROM NEELAM", offer: BeautyBank);

        var draft = template.Start(TemplateId, Version);

        Assert.Equal("NEWS FROM NEELAM", draft.Text("Headline").Value);
        Assert.Equal(Origin.Entered, draft.Text("Headline").Origin);
        var offer = draft.Offer("Offer");
        Assert.Equal(("Beauty Bank", Origin.Entered), (offer.Name.Value, offer.Name.Origin));
        var gold = Assert.Single(offer.Tiers);
        Assert.Equal("Gold Member", gold.Name.Value);
        Assert.Equal(Origin.Entered, Assert.Single(gold.Benefits).Origin);

        var editor = CampaignEditor.Open(draft);
        var headline = Assert.IsType<TextBlockEditor>(editor.Blocks[1]);
        Assert.False(headline.IsFixed);
        headline.Field.Text = "Our birthday";
        Assert.Equal("Our birthday", draft.Text("Headline").Value);
        // A second campaign starts afresh: what the first changed is its own.
        Assert.Equal("NEWS FROM NEELAM", template.Start().Text("Headline").Value);

        // Still to do counts what it started with as filled in, and never as a copy to check.
        var missing = editor.Status().Missing;
        Assert.DoesNotContain(missing, m => m.Location is "Headline" or "Offer › Name" or "Offer › Tier 1 › Name");
        Assert.Contains(missing, m => m.Location == "Offer › Tier 1 › Price");
        Assert.Contains(missing, m => m.Location == "Offer › Summary");
        Assert.DoesNotContain(missing, m => m.Rule == "draft-unreviewed-copy");
    }

    // Recurring is the block's to say, not its content's: the start carries none of its own.
    [Fact]
    public void An_offer_to_start_with_recurs_as_its_block_says()
    {
        var editor = TemplateEditor.Open(News(null, offer: BeautyBank), Catalogs.Shipped);
        var offer = editor.Blocks[2];

        offer.Recurring = true;

        Assert.True(offer.DefaultOffer!.IsRecurring);
        Assert.True(editor.Build().Start().Offer("Offer").IsRecurring);
        offer.Recurring = false;
        Assert.False(editor.Build().Start().Offer("Offer").IsRecurring);
    }

    // ---- The template page's editor

    [Fact]
    public void What_each_campaign_gets_is_one_choice_and_whether_it_must_have_it_is_another()
    {
        var editor = TemplateEditor.StartBlank(Catalogs.Shipped);
        editor.Name = "News";
        var heading = editor.Add(BlockType.Heading);
        (heading.Content, heading.Text, heading.Required) = (BlockContent.Default, "Hello", false);

        var built = editor.Build().Blocks.Single();
        Assert.Equal((false, (Block?)null), (built.Required, built.Fixed));
        Assert.Equal("Hello", new CampaignTemplate("News", [built]).Start().Text("Heading").Value);

        // The same every time: what was typed stays, now fixed.
        heading.Content = BlockContent.Fixed;
        built = editor.Build().Blocks.Single();
        Assert.Equal(("Hello", (BlockDefault?)null), (Assert.IsType<HeadingBlock>(built.Fixed).Text, built.Default));
        Assert.True(heading.IsFixed);

        // Unticking "fixed", as old callers do, means nothing; it never takes away a start.
        heading.IsFixed = false;
        Assert.Equal(BlockContent.Empty, heading.Content);
        heading.Content = BlockContent.Default;
        heading.IsFixed = false;
        Assert.Equal(BlockContent.Default, heading.Content);

        // An offer may start filled and is never fixed; a spacer has nothing to choose.
        var offer = editor.Add(BlockType.Offer);
        Assert.Equal((false, true), (offer.CanBeFixed, offer.CanStartFilled));
        Assert.Throws<InvalidOperationException>(() => offer.Content = BlockContent.Fixed);
        offer.Content = BlockContent.Default;
        var spacer = editor.Add(BlockType.Spacer);
        Assert.False(spacer.CanStartFilled);
        Assert.Throws<InvalidOperationException>(() => spacer.Content = BlockContent.Default);
    }

    [Fact]
    public void A_start_needs_something_in_it_that_its_fields_can_hold_and_no_copy_left_unchecked()
    {
        var editor = TemplateEditor.StartBlank(Catalogs.Shipped);
        editor.Name = "News";
        var heading = editor.Add(BlockType.Heading);
        heading.Content = BlockContent.Default;
        var offerBlock = editor.Add(BlockType.Offer);
        offerBlock.Content = BlockContent.Default;

        Assert.Equal(
            [("Heading", "Fill in the heading, or let each campaign write it."),
             ("Tiered offer", "Fill in something for each campaign to start with, or let each campaign fill it in.")],
            editor.Errors().Select(e => (e.Where, e.Message)));

        heading.Text = "NEWS";
        var offer = offerBlock.DefaultOffer!;
        BeautyBank(offer);
        offer.Tiers[0].Price.Text = "a lot";
        Assert.Equal(
            "Tier 1 › Price: \"a lot\" is not an amount in dollars, such as 149 or 149.50.",
            Assert.Single(editor.Errors()).Message);

        offer.Tiers[0].Price.Text = "";
        offer.CopyTier(offer.Tiers[0]);
        Assert.Equal(
            "Tier 2 › Benefit 1 was copied from Tier 1 and not checked: change it, or confirm it is right for this tier.",
            Assert.Single(editor.Errors()).Message);

        offer.Tiers[1].Benefits[0].Confirm();
        Assert.Empty(editor.Errors());
        Assert.NotNull(editor.Build().Blocks[1].Default);
    }

    [Fact]
    public void Opening_a_template_gives_its_starts_back_to_change_and_a_copy_changes_on_its_own()
    {
        var template = News("NEWS FROM NEELAM", offer: BeautyBank);

        var editor = TemplateEditor.Open(template, Catalogs.Shipped);

        Assert.Equal((BlockContent.Default, "NEWS FROM NEELAM"), (editor.Blocks[1].Content, editor.Blocks[1].Text));
        var offer = editor.Blocks[2].DefaultOffer!;
        Assert.Equal(("Beauty Bank", "Gold Member"), (offer.Name.Text, offer.Tiers[0].Name.Text));
        Assert.Equal(CampaignJson.SerializeTemplate(template), CampaignJson.SerializeTemplate(editor.Build()));

        var copy = TemplateEditor.CopyOf(template, Catalogs.Shipped);
        copy.Blocks[2].DefaultOffer!.Name.Text = "Glow Club";
        Assert.Equal("Beauty Bank", offer.Name.Text);
    }

    [Fact]
    public void A_promotion_can_start_with_its_lines_and_leave_its_dates_to_each_campaign()
    {
        var editor = TemplateEditor.StartBlank(Catalogs.Shipped);
        editor.Name = "Filler Promo";
        var block = editor.Add(BlockType.Promotion);
        block.Content = BlockContent.Default;
        var promotion = block.DefaultPromotion!;
        promotion.Name.Text = "Filler Promo";
        var line = promotion.AddTreatment();
        line.Treatment.Text = "Full-Face Facial Balancing";
        line.RegularPrice.Text = "5100";

        var draft = editor.Build().Start().Promotion("Promotion");

        Assert.Equal("Filler Promo", draft.Name.Value);
        var started = Assert.IsType<PricedTreatmentDraft>(Assert.Single(draft.Lines));
        Assert.Equal((5100m, Origin.Entered), (started.RegularPrice.Value, started.RegularPrice.Origin));
        Assert.False(started.Price.HasValue);
        Assert.False(draft.Starts.HasValue);
    }

    // ---- Preview and advice

    [Fact]
    public void The_preview_shows_what_each_campaign_starts_with_and_marks_what_it_leaves()
    {
        var template = News("NEWS FROM NEELAM", offer: BeautyBank);

        var preview = TemplatePreview.PlainText(template.Blocks);

        Assert.Contains("NEWS FROM NEELAM", preview);
        // A heading in capitals, as the plain text shows every heading.
        Assert.Contains("BEAUTY BANK", preview);
        Assert.Contains("‹Tier 1 price: not filled in yet›", preview);
        Assert.DoesNotContain("‹Headline: written for each campaign›", preview);

        var editor = TemplateEditor.Open(template, Catalogs.Shipped);
        Assert.Equal("NEWS FROM NEELAM", Assert.Single(editor.Blocks[1].DefaultPreview).Text);
        Assert.Contains(editor.Blocks[2].DefaultPreview, b => b.Text == "Beauty Bank");
        Assert.Contains(editor.Blocks[2].DefaultPreview, b => b.Text.Contains("‹Tier 1 price: not filled in yet›"));
        Assert.Empty(editor.Blocks[0].DefaultPreview);
        Assert.Empty(editor.Blocks[3].DefaultPreview);
    }

    [Fact]
    public void A_start_is_checked_and_said_apart_from_fixed_content()
    {
        var editor = TemplateEditor.StartBlank(Catalogs.Shipped);
        editor.Name = "News";
        var button = editor.Add(BlockType.Button);
        (button.Content, button.ButtonLabel, button.ButtonUrl) = (BlockContent.Fixed, "Join", "http://neelamaesthetics.square.site/");
        var greeting = editor.Add(BlockType.Greeting);
        (greeting.Content, greeting.Text) = (BlockContent.Default, "Results guaranteed!");

        var advice = editor.Advice();

        Assert.Equal(
            [("cta-https", "Button", true), ("restricted-term", "Greeting", false)],
            advice.Select(a => (a.Rule, a.Location, a.Message.StartsWith("In the template's fixed content", StringComparison.Ordinal))));
        Assert.StartsWith("In what each campaign starts with, so every campaign that keeps it will have this:", advice[1].Message);
    }

    // An offer to start with and a button each campaign fills: the button is the template's to require,
    // which it does, so its campaigns are not told they lack one. What the offer starts with is checked.
    [Fact]
    public void An_offer_to_start_with_is_checked_but_not_held_to_a_button_each_campaign_fills()
    {
        var template = News(null, offer: BeautyBank);

        Assert.True(template.Blocks.Single(b => b.Type == BlockType.Button).Required);
        var advice = Assert.Single(TemplateAdvice.For(template.Blocks));
        Assert.Equal(("restricted-term", "Offer › Name"), (advice.Rule, advice.Location));
        Assert.StartsWith("In what each campaign starts with", advice.Message);
    }

    // ---- Update to template

    [Fact]
    public void An_unchanged_start_takes_the_template_s_new_one()
    {
        var was = News("NEWS FROM NEELAM", offer: BeautyBank);
        var draft = was.Start(TemplateId, Version);
        draft.Subject.Set("We're turning one");

        var update = TemplateUpdate.Of(draft, News("NEWS FROM NEELAM AESTHETICS", offer: o => BeautyBank(o, "The Beauty Bank")),
            TemplateId, Later, madeFrom: was);

        Assert.Equal(("NEWS FROM NEELAM AESTHETICS", Origin.Entered), (update.Draft.Text("Headline").Value, update.Draft.Text("Headline").Origin));
        Assert.Equal("The Beauty Bank", update.Draft.Offer("Offer").Name.Value);
        Assert.Single(update.Draft.Offer("Offer").Tiers);
        Assert.False(update.LosesAnything);
        Assert.Equal("We're turning one", update.Draft.Subject.Value);
    }

    // She changed it, so it is hers: kept, and nothing is said about it.
    [Fact]
    public void A_start_she_changed_stays_hers_and_is_not_reported()
    {
        var was = News("NEWS FROM NEELAM", offer: BeautyBank);
        var draft = was.Start(TemplateId, Version);
        draft.Text("Headline").Set("WE'RE TURNING ONE");
        draft.Offer("Offer").Tiers[0].MonthlyPrice.Set(149m);

        var update = TemplateUpdate.Of(draft, News("NEWS FROM NEELAM AESTHETICS", offer: o => BeautyBank(o, "The Beauty Bank")),
            TemplateId, Later, madeFrom: was);

        Assert.Equal("WE'RE TURNING ONE", update.Draft.Text("Headline").Value);
        var offer = update.Draft.Offer("Offer");
        Assert.Equal(("Beauty Bank", 149m), (offer.Name.Value, offer.Tiers.Single().MonthlyPrice.Value));
        Assert.Empty(update.Replaced);
        Assert.Empty(update.Dropped);
    }

    // Approved: what it starts with stays as she has it; fixed text and the blocks still follow the template.
    [Fact]
    public void On_an_approved_version_starts_stay_and_fixed_text_still_updates()
    {
        var was = News("NEWS FROM NEELAM");
        var draft = was.Start(TemplateId, Version);

        var update = TemplateUpdate.Of(draft, News("NEWS FROM NEELAM AESTHETICS", greeting: "Hello lovely"),
            TemplateId, Later, madeFrom: was, approved: true);

        Assert.Equal("NEWS FROM NEELAM", update.Draft.Text("Headline").Value);
        Assert.Equal(("Hello lovely", Origin.Template), (update.Draft.Text("Greeting").Value, update.Draft.Text("Greeting").Origin));
    }

    // The version she was made from not known (none recorded, or no longer in use): nothing tells her
    // text from the template's, so hers stays.
    [Fact]
    public void Without_the_version_she_was_made_from_what_she_has_stays()
    {
        var draft = News("NEWS FROM NEELAM").Start(TemplateId, Version);

        var update = TemplateUpdate.Of(draft, News("NEWS FROM NEELAM AESTHETICS"), TemplateId, Later, madeFrom: null);

        Assert.Equal("NEWS FROM NEELAM", update.Draft.Text("Headline").Value);
    }

    // A block the version she was made from left empty, and still empty, takes the new start as a new
    // campaign would; one she wrote in stays hers.
    [Fact]
    public void An_empty_block_takes_a_new_start_and_one_she_wrote_in_keeps_her_text()
    {
        var was = News(null);
        var empty = was.Start(TemplateId, Version);
        var written = was.Start(TemplateId, Version);
        written.Text("Headline").Set("WE'RE TURNING ONE");
        var now = News("NEWS FROM NEELAM");

        Assert.Equal("NEWS FROM NEELAM", TemplateUpdate.Of(empty, now, TemplateId, Later, was).Draft.Text("Headline").Value);
        var kept = TemplateUpdate.Of(written, now, TemplateId, Later, was);
        Assert.Equal("WE'RE TURNING ONE", kept.Draft.Text("Headline").Value);
        Assert.Empty(kept.Replaced);
    }

    // Fixed before: she could not have changed it, so the new start takes its place.
    [Fact]
    public void Text_that_was_fixed_and_is_now_a_start_takes_the_new_start()
    {
        var was = News("OLD NEWS", fixedHeadline: true);
        var draft = was.Start(TemplateId, Version);

        var update = TemplateUpdate.Of(draft, News("NEWS FROM NEELAM"), TemplateId, Later, was);

        Assert.Equal(("NEWS FROM NEELAM", Origin.Entered), (update.Draft.Text("Headline").Value, update.Draft.Text("Headline").Origin));
    }

    // A block new to the template arrives as a new campaign has it, start and all.
    [Fact]
    public void A_block_new_to_the_template_arrives_with_its_start()
    {
        var was = new CampaignTemplate("News", [News(null).Blocks[0]]);
        var draft = was.Start(TemplateId, Version);

        var update = TemplateUpdate.Of(draft, News("NEWS FROM NEELAM"), TemplateId, Later, madeFrom: null);

        Assert.Equal("NEWS FROM NEELAM", update.Draft.Text("Headline").Value);
    }
}
