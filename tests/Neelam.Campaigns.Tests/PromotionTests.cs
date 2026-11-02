using Neelam.Campaigns.Claude;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The Promotion block (owner, 2026-10-31, answer 9a, and P1-P6): one offer for a set time, with no
/// tiers. Evidence: her "Filler Promo" campaign, which typed two priced treatments into paragraphs with
/// the dates only in the fine print, and the back-to-school email's benefits for an audience.
/// </summary>
public class PromotionTests
{
    private static readonly ClientName Salon = new("test-salon-one");

    // Her "Filler Promo Nov / Dec" template, with a Promotion block where she had typed paragraphs.
    internal static readonly CampaignTemplate Template = new("Filler Promo Nov / Dec",
    [
        new("Headline", BlockType.Heading),
        new("Promotion", BlockType.Promotion),
        new("Book", BlockType.Button),
        new("Fine print", BlockType.FinePrint, Fixed: new FinePrintBlock("Fine print",
            "Offer valid for filler treatments booked and performed within those dates. Not valid with other offers.")),
    ]);

    private static PromotionEditor PromotionOf(CampaignEditor editor) =>
        editor.Blocks.OfType<PromotionBlockEditor>().Single().Promotion;

    // Filler Promo as it would be written with the block: two treatments, their prices as she typed
    // them ("1,599"), the dates, no audience.
    internal static CampaignEditor FillerPromo(KnownItems? known = null, CampaignTemplate? template = null)
    {
        var editor = CampaignEditor.Start(template ?? Template);
        editor.Subject.Text = "Two months to refresh: November and December";
        foreach (var block in editor.Blocks)
        {
            switch (block)
            {
                case TextBlockEditor { IsFixed: false } heading: heading.Field.Text = "Refresh for the holidays"; break;
                case ButtonBlockEditor button:
                    button.ButtonLabel = "Book now";
                    button.ButtonUrl = "https://example.com/book";
                    break;
            }
        }
        var promotion = PromotionOf(editor);
        promotion.Name.Text = "Filler Promo";
        promotion.Starts.Text = "2026-11-01";
        promotion.Ends.Text = "2026-12-31";
        var midface = promotion.AddTreatment();
        midface.SetTreatment("Midface Lift / Cheek Rejuvenation", known ?? KnownItems.None);
        midface.Price.Text = "1,599";
        midface.RegularPrice.Text = "2,550";
        var balancing = promotion.AddTreatment();
        balancing.SetTreatment("Full-Face Facial Balancing", known ?? KnownItems.None);
        balancing.Price.Text = "$3,499";
        balancing.RegularPrice.Text = "5,100";
        return editor;
    }

    private static Campaign Built(CampaignEditor editor) =>
        editor.Draft.Build().Campaign ?? throw new InvalidOperationException(string.Join("; ", editor.Draft.Build().Problems.Select(p => p.Message)));

    // ---- The export

    // The exact paste text of Filler Promo redone: the name as a heading, then one text block with each
    // line and the dates, the saving worked out with its separator ("$1,601", where she typed "$1601").
    [Fact]
    public async Task Filler_promo_exports_its_lines_and_dates_exactly()
    {
        var campaign = Built(FillerPromo());
        var report = await CampaignGate.ReviewAsync(campaign, FakeProofreader.Clean);
        Assert.Empty(report.Findings);

        var blocks = EditorExport.Blocks(report);
        var heading = blocks.Single(b => b.Text == "Filler Promo");
        Assert.Equal(BlockKind.Heading, heading.Kind);
        var text = blocks[blocks.ToList().IndexOf(heading) + 1];
        Assert.Equal(BlockKind.Text, text.Kind);
        Assert.Equal(
            "Midface Lift / Cheek Rejuvenation — $1,599 (Regularly $2,550, saving $951)\n\n" +
            "Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)\n\n" +
            "Offer valid November 1 – December 31, 2026.",
            text.Text);
    }

    // The back-to-school email's shape: benefit lines, the month's dates and an audience, which the
    // dates line carries.
    [Fact]
    public void A_back_to_school_promotion_exports_its_benefits_and_audience_exactly()
    {
        var campaign = new Campaign("Back to school", [
            new PromotionBlock("Promotion", new Promotion("Back to School", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            [
                new BenefitLine(new DollarsOff(50m, "units", 30)),
                new BenefitLine(new Credit(50m, "your next appointment")),
                new BenefitLine(new FreeItem(1, "Wellness Injection", "with any treatment", FreeWording.Free)),
            ], Audience: "teachers & school faculty")),
            new ButtonBlock("Book", new CallToAction("Book now", new Uri("https://example.com/book"))),
        ]);

        var blocks = EditorExport.PreviewBlocks(campaign);

        Assert.Equal(new EditorBlock(BlockKind.Heading, "Back to School"), blocks[0]);
        Assert.Equal(
            "$50 off 30+ units\n\n" +
            "$50 credit toward your next appointment\n\n" +
            "Free Wellness Injection with any treatment\n\n" +
            "Offer valid September 1 – 30, 2026, for teachers & school faculty.",
            blocks[1].Text);
    }

    // Within a month, across months, across years, one day; an audience typed with a full stop.
    [Theory]
    [InlineData("2026-09-01", "2026-09-30", null, "Offer valid September 1 – 30, 2026.")]
    [InlineData("2026-11-01", "2026-12-31", null, "Offer valid November 1 – December 31, 2026.")]
    [InlineData("2026-12-15", "2027-01-15", null, "Offer valid December 15, 2026 – January 15, 2027.")]
    [InlineData("2026-11-27", "2026-11-27", null, "Offer valid November 27, 2026.")]
    [InlineData("2026-09-01", "2026-09-30", " teachers & school faculty. ", "Offer valid September 1 – 30, 2026, for teachers & school faculty.")]
    [InlineData("2026-09-01", "2026-09-30", "  ", "Offer valid September 1 – 30, 2026.")]
    public void The_dates_line_says_the_month_and_year_once_when_shared(string starts, string ends, string? audience, string expected)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Equal(expected, EditorExport.DatesText(DateOnly.Parse(starts, System.Globalization.CultureInfo.InvariantCulture),
                DateOnly.Parse(ends, System.Globalization.CultureInfo.InvariantCulture), audience));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // ---- The draft: what is missing, the preview so far, JSON

    [Fact]
    public void Still_to_do_names_the_name_both_dates_the_lines_and_each_line_s_prices()
    {
        var editor = CampaignEditor.Start(Template);
        var empty = editor.Status().Missing.Select(m => m.Location).ToList();
        Assert.Contains("Promotion › Name", empty);
        Assert.Contains("Promotion › Starts", empty);
        Assert.Contains("Promotion › Ends", empty);
        Assert.Contains("Promotion › Lines", empty);

        var promotion = PromotionOf(editor);
        promotion.AddTreatment().Treatment.Text = "Lip filler";
        promotion.AddBenefit();
        var lines = editor.Status().Missing.Select(m => m.Location).ToList();
        Assert.DoesNotContain("Promotion › Lines", lines);
        Assert.DoesNotContain("Promotion › Line 1 › Treatment", lines);
        Assert.Contains("Promotion › Line 1 › Promotion price", lines);
        Assert.Contains("Promotion › Line 1 › Regular price", lines);
        Assert.Contains("Promotion › Line 2 › Benefit", lines);
        Assert.All(editor.Status().Missing, m => Assert.Equal("draft-missing", m.Rule));
    }

    // While unfinished, the preview marks each missing part as its field is named.
    [Fact]
    public void The_preview_so_far_marks_each_missing_part()
    {
        var editor = CampaignEditor.Start(Template);
        var promotion = PromotionOf(editor);
        var preview = string.Join("\n", editor.Status().Preview.Select(b => b.Text));
        Assert.Contains("‹Promotion name: not filled in yet›", preview);
        Assert.Contains("‹Promotion lines: not filled in yet›", preview);
        Assert.Contains("‹Promotion dates: not filled in yet›", preview);

        promotion.Name.Text = "Filler Promo";
        var line = promotion.AddTreatment();
        line.Treatment.Text = "Lip filler";
        line.RegularPrice.Text = "800";
        promotion.Starts.Text = "2026-11-01";
        promotion.Ends.Text = "2026-12-31";
        preview = string.Join("\n", editor.Status().Preview.Select(b => b.Text));
        Assert.Contains("Lip filler — ‹Line 1 promotion price: not filled in yet› (Regularly $800)", preview);
        Assert.Contains("Offer valid November 1 – December 31, 2026.", preview);
    }

    // A promotion round-trips with every value and origin, both kinds of line in her order; the saved
    // block is the "promotion" type.
    [Fact]
    public void A_promotion_round_trips_through_the_draft_s_json()
    {
        var editor = FillerPromo();
        var promotion = PromotionOf(editor);
        promotion.Audience.Text = "new clients";
        promotion.Summary.Text = "Two of our favourites, for two months only.";
        promotion.AddBenefit().Benefit.Use(new Credit(50m, "your next appointment"));

        var json = CampaignJson.SerializeDraft(editor.Draft);
        var back = CampaignJson.DeserializeDraft(json);

        Assert.Contains("\"type\": \"promotion\"", json);
        Assert.Equal(json, CampaignJson.SerializeDraft(back));
        var read = back.Promotion("Promotion");
        Assert.Equal(new DateOnly(2026, 12, 31), read.Ends.Value);
        Assert.Equal(Origin.Entered, read.Starts.Origin);
        Assert.IsType<PricedTreatmentDraft>(read.Lines[0]);
        Assert.Equal(5100m, ((PricedTreatmentDraft)read.Lines[1]).RegularPrice.Value);
        Assert.Equal(new Credit(50m, "your next appointment"), ((BenefitLineDraft)read.Lines[2]).Benefit.Value);
        Assert.Equal(EditorExport.Preview(Built(editor)), EditorExport.Preview(back.Build().Campaign!));
    }

    // An empty promotion, as a new campaign has it, reads back empty.
    [Fact]
    public void An_empty_promotion_round_trips_empty()
    {
        var back = CampaignJson.DeserializeDraft(CampaignJson.SerializeDraft(Template.Start()));

        Assert.False(back.Promotion("Promotion").Name.HasValue);
        Assert.False(back.Promotion("Promotion").Starts.HasValue);
        Assert.Empty(back.Promotion("Promotion").Lines);
    }

    // A date that is not one, or a price that is not one, keeps the form from saving and says why.
    [Fact]
    public void A_date_or_price_that_is_not_one_is_said_and_kept_from_the_draft()
    {
        var editor = FillerPromo();
        var promotion = PromotionOf(editor);
        promotion.Ends.Text = "the end of December";
        ((PricedTreatmentEditor)promotion.Lines[0]).Price.Text = "lots";

        Assert.Equal(
        [
            "Promotion › Ends: \"the end of December\" is not a date; choose the end date from the calendar.",
            "Promotion › Line 1 › Promotion price: \"lots\" is not an amount in dollars, such as 149 or 149.50.",
        ], editor.Errors());
        Assert.False(editor.Draft.Promotion("Promotion").Ends.HasValue);
        promotion.Ends.Text = "12/31/2026";
        Assert.Equal(new DateOnly(2026, 12, 31), editor.Draft.Promotion("Promotion").Ends.Value);
    }

    // ---- The checks

    private static IReadOnlyList<Finding> Findings(CampaignEditor editor, BusinessContext? business = null) =>
        editor.Status(business: business).Findings;

    [Theory]
    [InlineData("5,100", "'Full-Face Facial Balancing' is $5,100 in the promotion but regularly $5,100: the promotion price must be below the regular price.")]
    [InlineData("6000", "'Full-Face Facial Balancing' is $6,000 in the promotion but regularly $5,100: the promotion price must be below the regular price.")]
    [InlineData("0", "'Full-Face Facial Balancing' needs a promotion price above $0.")]
    public void A_promotion_price_not_below_the_regular_price_is_a_must_fix(string price, string message)
    {
        var editor = FillerPromo();
        ((PricedTreatmentEditor)PromotionOf(editor).Lines[1]).Price.Text = price;

        var finding = Assert.Single(Findings(editor), f => f.Rule == "promotion-price");
        Assert.Equal((Severity.Blocker, "Promotion › Line 2", message), (finding.Severity, finding.Location, finding.Message));
    }

    [Fact]
    public void An_end_before_the_start_is_a_must_fix()
    {
        var editor = FillerPromo();
        PromotionOf(editor).Ends.Text = "2026-10-31";

        var finding = Assert.Single(Findings(editor), f => f.Rule == "promotion-dates");
        Assert.Equal(Severity.Blocker, finding.Severity);
        Assert.Equal("Promotion › Ends", finding.Location);
        Assert.Equal("The promotion ends on October 31, 2026, before it starts on November 1, 2026; change either date.", finding.Message);
        // About the email, not the day: it stops an approved version's export as much as an approval.
        Assert.Null(finding.OnceApproved);
        PromotionOf(editor).Ends.Text = "2026-11-01";
        Assert.DoesNotContain(Findings(editor), f => f.Rule == "promotion-dates");
    }

    // P5: an end date already past, in her own zone, is a Must fix; its last day is not past, a start
    // already past says nothing, and with the day unknown nothing is said.
    [Fact]
    public void An_end_date_already_past_is_a_must_fix()
    {
        var editor = FillerPromo();
        IReadOnlyList<Finding> On(DateOnly? today) =>
            Findings(editor, new BusinessContext("Neelam Aesthetics") { Today = today }).Where(f => f.Rule == "promotion-dates").ToList();

        var past = Assert.Single(On(new DateOnly(2027, 1, 1)));
        Assert.Equal((Severity.Blocker, "Promotion › Ends"), (past.Severity, past.Location));
        Assert.Equal("The promotion ends on December 31, 2026, which has already passed; change the end date.", past.Message);
        Assert.Empty(On(new DateOnly(2026, 12, 31)));
        Assert.Empty(On(new DateOnly(2026, 11, 15)));
        Assert.Empty(On(null));
        Assert.Empty(Findings(editor).Where(f => f.Rule == "promotion-dates"));
    }

    // "Today" is the day in her zone: 5 AM UTC on November 1 is still October 31 in Los Angeles.
    [Fact]
    public void Today_is_the_day_in_the_client_s_own_zone()
    {
        var pacific = LocalTime.For("America/Los_Angeles");

        Assert.Equal(new DateOnly(2026, 10, 31), pacific.Today(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 1), pacific.Today(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero)));
    }

    // A promotion needs a way to take it up, as an offer does.
    [Fact]
    public void A_promotion_with_no_button_is_blocked_and_the_template_is_told()
    {
        var noButton = new CampaignTemplate("No button", [.. Template.Blocks.Where(b => b.Type != BlockType.Button)]);

        var finding = Assert.Single(Findings(FillerPromo(template: noButton)), f => f.Rule == "cta-required");
        Assert.Equal("The email has a promotion and no way to act on it: add a button with a link (book, join, reply).", finding.Message);
        Assert.Equal("Promotion", finding.Location);

        var advice = Assert.Single(TemplateAdvice.For(noButton.Blocks), f => f.Rule == "cta-required");
        Assert.Equal(Severity.Blocker, advice.Severity);
        Assert.Contains("it has a promotion and no button block", advice.Message);
    }

    // The text checks read the lines: "Filler" is a medical term, so with no fine print it needs one.
    [Fact]
    public void The_text_checks_read_the_promotion_s_name_and_lines()
    {
        var noFinePrint = new CampaignTemplate("No fine print", [.. Template.Blocks.Where(b => b.Type != BlockType.FinePrint)]);
        var editor = FillerPromo(template: noFinePrint);

        var medical = Assert.Single(Findings(editor), f => f.Rule == "medical-disclaimer");
        Assert.Equal("Promotion › Name", medical.Location);

        ((PricedTreatmentEditor)PromotionOf(editor).Lines[0]).Treatment.Text = "Guaranteed glow🤍facial";
        var findings = Findings(editor);
        Assert.Contains(findings, f => f.Rule == "restricted-term" && f.Location == "Promotion › Line 1");
        Assert.Contains(findings, f => f.Rule == "emoji-spacing" && f.Location == "Promotion › Line 1");
    }

    // A benefit line's value is held as a tier's benefit's is.
    [Fact]
    public void A_benefit_line_s_value_must_make_sense()
    {
        var editor = FillerPromo();
        PromotionOf(editor).AddBenefit().Benefit.Use(new PercentOff(0, "any treatment"));

        var finding = Assert.Single(Findings(editor), f => f.Rule == "benefit-value");
        Assert.Equal(("Promotion › Line 3", "0% off is not a discount; use 1–99."), (finding.Location, finding.Message));
    }

    private static BusinessContext Knowing(params KnownItem[] items) => new("Neelam Aesthetics") { Known = new KnownItems(items) };

    private static readonly KnownTreatment Balancing = new(KnownItem.NewId(), "Full-Face Facial Balancing", 5100m);
    private static readonly KnownTreatment Midface = new(KnownItem.NewId(), "Midface Lift / Cheek Rejuvenation", 2550m);

    // P1: picking a known treatment fills in its regular price, which she can change; a price she typed
    // is never replaced.
    [Fact]
    public void Picking_a_known_treatment_fills_in_its_regular_price_unless_one_is_typed()
    {
        var known = new KnownItems([Balancing]);
        var editor = CampaignEditor.Start(Template);
        var line = PromotionOf(editor).AddTreatment();

        line.SetTreatment("full-face facial balancing", known);
        Assert.Equal("5100", line.RegularPrice.Text);

        line.RegularPrice.Text = "4,900";
        line.SetTreatment("Full-Face Facial Balancing", known);
        Assert.Equal("4,900", line.RegularPrice.Text);
        Assert.Equal(4900m, ((PricedTreatmentDraft)editor.Draft.Promotion("Promotion").Lines[0]).RegularPrice.Value);
    }

    // P1: a regular price other than hers is worth a look.
    [Fact]
    public void A_regular_price_other_than_the_known_one_is_worth_a_look()
    {
        var editor = FillerPromo(new KnownItems([Balancing, Midface]));
        Assert.DoesNotContain(Findings(editor, Knowing(Balancing, Midface)), f => f.Rule == "known-item");

        ((PricedTreatmentEditor)PromotionOf(editor).Lines[1]).RegularPrice.Text = "5,400";

        var finding = Assert.Single(Findings(editor, Knowing(Balancing, Midface)), f => f.Rule == "known-item");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("Promotion › Line 2 regular price", finding.Location);
        Assert.Equal("'Full-Face Facial Balancing' is regularly $5,100 in your known items; here it is $5,400.", finding.Message);
    }

    // A near miss of a known treatment offers the known name, and Use this puts it in the line.
    [Fact]
    public void A_misspelt_treatment_is_a_near_miss_with_its_known_name_suggested()
    {
        var editor = FillerPromo();
        var line = (PricedTreatmentEditor)PromotionOf(editor).Lines[1];
        line.Treatment.Text = "Full-Face Facial Balancng";

        var finding = Assert.Single(Findings(editor, Knowing(Balancing, Midface)), f => f.Rule == "known-item");
        Assert.Equal("Promotion › Line 2 treatment", finding.Location);
        Assert.Equal("Did you mean 'Full-Face Facial Balancing'? It's in your known items.", finding.Message);
        Assert.Equal("Full-Face Facial Balancing", finding.Suggestion);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));
        Assert.Equal("Full-Face Facial Balancing", line.Treatment.Text);
        Assert.Equal("Full-Face Facial Balancing", ((PricedTreatmentDraft)editor.Draft.Promotion("Promotion").Lines[1]).Treatment.Value);
        Assert.DoesNotContain(Findings(editor, Knowing(Balancing, Midface)), f => f.Rule == "known-item");
    }

    // A treatment new to her list says it is added on save, with the price it will be kept at (P6).
    [Fact]
    public void A_treatment_new_to_her_list_says_saving_adds_it()
    {
        var finding = Assert.Single(Findings(FillerPromo(), Knowing(Midface)), f => f.Rule == "known-item");

        Assert.Equal("Promotion › Line 2 treatment", finding.Location);
        Assert.Equal("'Full-Face Facial Balancing' isn't one of your known treatments yet. Saving the campaign adds it, regularly $5,100.", finding.Message);
        Assert.Null(finding.Suggestion);
    }

    // A benefit line nearly one of hers offers her line at the amounts written, set as a benefit.
    [Fact]
    public void A_benefit_line_nearly_one_of_hers_is_set_as_hers_by_use_this()
    {
        var editor = FillerPromo(new KnownItems([Balancing, Midface]));
        var line = PromotionOf(editor).AddBenefit();
        line.Benefit.Use(new Credit(75m, "your nxt appointment"));
        var known = Knowing(Balancing, Midface, new KnownBenefit(KnownItem.NewId(), new Credit(50m, "your next appointment")));

        var finding = Assert.Single(Findings(editor, known), f => f.Rule == "known-item");
        Assert.Equal("Promotion › Line 3", finding.Location);
        Assert.Equal(new Credit(75m, "your next appointment"), finding.SuggestedBenefit);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));
        Assert.Equal(new Credit(75m, "your next appointment"), line.Benefit.Value);
    }

    // ---- P6: treatments new to her list, added when the campaign is saved

    [Fact]
    public void Only_new_treatments_with_a_regular_price_are_added_once_each()
    {
        var editor = FillerPromo();
        var promotion = PromotionOf(editor);
        var again = promotion.AddTreatment();
        again.Treatment.Text = "full-face facial balancing";
        again.Price.Text = "3000";
        again.RegularPrice.Text = "5100";
        var unpriced = promotion.AddTreatment();
        unpriced.Treatment.Text = "Lip filler";
        var nearMiss = promotion.AddTreatment();
        nearMiss.Treatment.Text = "Midface Lift / Cheek Rejuvenaton";
        nearMiss.Price.Text = "1";
        nearMiss.RegularPrice.Text = "2";

        var toSave = editor.TreatmentsToSave(new KnownItems([Midface]));

        var added = Assert.Single(toSave);
        Assert.Equal(("Full-Face Facial Balancing", (decimal?)5100m), (added.Name, added.RegularPrice));
        Assert.Empty(editor.TreatmentsToSave(new KnownItems([Midface, Balancing])));
    }

    [Fact]
    public async Task Adding_from_a_promotion_goes_through_the_store_and_the_trail_and_says_so()
    {
        var store = new InMemoryKnownItems();
        var records = new TestRecords(TimeProvider.System);
        var trail = records.Recorder.For(Salon, Actor.Demo);
        await store.AddAsync(Salon, Midface);
        var balancing = new KnownTreatment(KnownItem.NewId(), "Full-Face Facial Balancing", 5100m);
        // Added in another tab meanwhile: refused by the store, passed over in silence.
        var already = new KnownTreatment(KnownItem.NewId(), "Midface Lift / Cheek Rejuvenation", 2550m);

        var said = await KnownItemsSession.AddFromPromotionAsync(store, trail, [balancing, already]);

        Assert.Equal(["Added Full-Face Facial Balancing to your treatments, regularly $5,100."], said);
        var kept = (await store.ForClientAsync(Salon)).Treatments.Single(t => t.Id == balancing.Id);
        Assert.Equal(5100m, kept.RegularPrice);
        var added = Assert.Single(records.Activity.Events);
        Assert.Equal((ActivityAction.KnownItemAdded, balancing.Id), (added.Action, added.EntityId));
    }

    [Fact]
    public async Task A_store_failure_when_adding_from_a_promotion_is_said_and_logged_never_thrown()
    {
        var log = new ListLogger<KnownItemsSession>();
        var treatment = new KnownTreatment(KnownItem.NewId(), "Full-Face Facial Balancing", 5100m);

        var said = await KnownItemsSession.AddFromPromotionAsync(new Unreachable(), TestRecords.Unwatched.For(Salon, Actor.Demo), [treatment], log);

        Assert.Equal(["Couldn't add Full-Face Facial Balancing to your treatments just now. Your campaign is saved; add it on the Known items page."], said);
        Assert.Contains(treatment.Id, Assert.Single(log.Entries).Message);
        Assert.DoesNotContain("Balancing", log.Entries[0].Message);
    }

    private sealed class Unreachable : IKnownItemStore
    {
        public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default) => Task.FromResult(KnownItems.None);
        public Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) =>
            throw new Azure.RequestFailedException(503, "The table service is unavailable.");
        public Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---- Templates

    // A promotion is never poured into a tiered offer's place, nor an offer into a promotion's: blocks
    // are matched by type, so hers is named as dropped.
    [Fact]
    public void A_template_update_never_maps_a_promotion_into_an_offer()
    {
        var offerOnly = new CampaignTemplate("Offer", [.. Template.Blocks.Select(b => b.Type == BlockType.Promotion ? new TemplateBlock("Promotion", BlockType.Offer) : b)]);
        var draft = FillerPromo().Draft;

        var update = TemplateUpdate.Of(draft, offerOnly, Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Assert.Equal(["Promotion"], update.Dropped);
        Assert.True(((OfferBlockDraft)update.Draft["Promotion"]).Offer.Tiers.Count == 0);
        Assert.False(((OfferBlockDraft)update.Draft["Promotion"]).Offer.Name.HasValue);

        var back = TemplateUpdate.Of(update.Draft, Template, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        Assert.Empty(back.Draft.Promotion("Promotion").Lines);
    }

    // To a promotion of the template's, matched by type, her promotion comes whole.
    [Fact]
    public void A_template_update_keeps_her_promotion()
    {
        var renamed = new CampaignTemplate("Filler Promo v2", [.. Template.Blocks.Select(b => b.Type == BlockType.Promotion ? b with { Label = "The offer" } : b)]);
        var draft = FillerPromo().Draft;

        var update = TemplateUpdate.Of(draft, renamed, Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Assert.Empty(update.Dropped);
        Assert.Equal(EditorExport.PreviewBlocks(Built(FillerPromo())).Select(b => b.Text),
            EditorExport.PreviewBlocks(update.Draft.Build().Campaign!).Select(b => b.Text));
    }

    [Fact]
    public void A_template_never_fixes_a_promotion_and_previews_it_as_set_out_for_each_campaign()
    {
        var editor = TemplateEditor.Open(Template, Catalogs.Shipped);
        var block = editor.Blocks.Single(b => b.Type == BlockType.Promotion);

        Assert.False(block.CanBeFixed);
        Assert.Throws<InvalidOperationException>(() => block.IsFixed = true);
        Assert.Throws<ArgumentException>(() => new CampaignTemplate("Fixed", [new("Promotion", BlockType.Promotion,
            Fixed: new PromotionBlock("Promotion", new Promotion("Old", DateOnly.MinValue, DateOnly.MinValue, [])))]));
        Assert.Contains(TemplatePreview.Blocks(Template.Blocks),
            b => b.Kind == BlockKind.Heading && b.Text == "‹Promotion: set out for each campaign›");
        // Named from the catalog, numbered since the template has one already.
        Assert.Equal("Promotion 2", editor.Add(BlockType.Promotion).Label);
        Assert.Equal("Your templates usually have a promotion; this one doesn't.", BaselineGap.For(BlockType.Promotion).Message);
    }

    // A template with a promotion round-trips as "promotion".
    [Fact]
    public void A_template_with_a_promotion_round_trips()
    {
        var json = CampaignJson.SerializeTemplate(Template);

        Assert.Contains("\"type\": \"promotion\"", json);
        Assert.Equal(json, CampaignJson.SerializeTemplate(CampaignJson.DeserializeTemplate(json)));
    }

    // ---- The proofread: the promotion's facts, so the LLM can hold the fine print to its dates.

    [Fact]
    public void The_proofread_is_given_the_promotion_s_facts_in_its_own_tag()
    {
        var editor = FillerPromo();
        PromotionOf(editor).AddBenefit().Benefit.Use(new Credit(50m, "your next appointment"));
        var campaign = Built(editor);

        var content = ClaudeProofreader.Content(campaign, EditorExport.PreviewParts(campaign), null);

        Assert.Contains(
            "<promotion>\n" +
            "Promotion: name \"Filler Promo\"; runs 2026-11-01 to 2026-12-31 (Offer valid November 1 – December 31, 2026.); for everyone\n" +
            "line 1: treatment \"Midface Lift / Cheek Rejuvenation\", promotion price $1,599, regular price $2,550, saving $951\n" +
            "line 2: treatment \"Full-Face Facial Balancing\", promotion price $3,499, regular price $5,100, saving $1,601\n" +
            "line 3: benefit \"$50 credit toward your next appointment\"\n" +
            "</promotion>", content);
        Assert.Contains("<promotion>", ProofreadInstructions.ContentTags);
        Assert.Contains("<promotion>", ProofreadInstructions.Shipped(AppContext.BaseDirectory).SystemPrompt);

        var plain = new Campaign("Hello", [new HeadingBlock("Headline", "Hi")]);
        Assert.DoesNotContain("<promotion>", ClaudeProofreader.Content(plain, EditorExport.PreviewParts(plain), null));
    }

    [Fact]
    public void The_promotion_block_is_in_the_catalog_with_its_checks()
    {
        var guide = Catalogs.Shipped.For(BlockType.Promotion);

        Assert.Equal("Promotion", guide.Label);
        Assert.Contains("promotion-price", guide.Rules);
        Assert.Contains("promotion-dates", guide.Rules);
        Assert.Contains("Each treatment's promotion price must be below its regular price, so the saving the email works out is a real one.", guide.Checks);
    }
}

/// <summary>The campaign page with a promotion, held as its circuit holds it, read by named elements.</summary>
public class PromotionPageTests(ProofreadDemoApp app) : IClassFixture<ProofreadDemoApp>
{
    private async Task<Guid> SavedAsync(CampaignDraft draft)
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Filler Promo", draft);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save.Id;
    }

    // The fields, the line as the email will say it, and P6: saving adds the new treatment to her
    // known items with its regular price, and the save line says so.
    [Fact]
    public async Task Saving_a_promotion_adds_its_new_treatment_to_her_known_items_and_says_so()
    {
        var draft = PromotionTests.FillerPromo().Draft;
        await using var page = await OpenCampaignPage.OpenAsync(app, await SavedAsync(draft), FakeProofreader.Clean);

        var html = await page.HtmlAsync();
        Assert.Contains("Promotion name</label>", html);
        Assert.Contains("Who it is for (optional)</label>", html);
        Assert.Equal("The email says: Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)", RenderedPage.Named(html, "line-sentence-1-l1"));

        await page.ClickAsync("save-button");

        var message = RenderedPage.Named(await page.HtmlAsync(), "page-message");
        Assert.Contains("Added Midface Lift / Cheek Rejuvenation to your treatments, regularly $2,550.", message);
        Assert.Contains("Added Full-Face Facial Balancing to your treatments, regularly $5,100.", message);
        var known = await app.KnownItems.ForClientAsync(DemoApp.Client);
        Assert.Equal(5100m, known.Treatments.Single(t => t.Name == "Full-Face Facial Balancing").RegularPrice);
    }

    // P5 on the page: the page gives the checks today in her zone, so a promotion that ended before
    // the site's day is a Must fix there.
    [Fact]
    public async Task A_promotion_that_has_ended_is_a_must_fix_on_the_page()
    {
        var editor = PromotionTests.FillerPromo();
        var promotion = editor.Blocks.OfType<PromotionBlockEditor>().Single().Promotion;
        var today = LocalTime.For(LocalTime.DefaultZone).Today(app.Clock.GetUtcNow());
        promotion.Starts.Text = today.AddDays(-30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // Two days back, so a zone a day either side of the default still has it ended.
        promotion.Ends.Text = today.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await using var page = await OpenCampaignPage.OpenAsync(app, await SavedAsync(editor.Draft), FakeProofreader.Clean);

        var html = await page.HtmlAsync();
        Assert.Contains("which has already passed; change the end date.", RenderedPage.Named(html, "checks-findings"));
        // And it stops the approval.
        Assert.Contains("Fix everything marked Must fix first.", html);
        Assert.Equal(0, RenderedPage.Count(html, "approve-button"));
    }

    // A Filler Promo that ran until two days before the site's day, saved, as the page would build it.
    private (CampaignDraft Draft, DateOnly Ends) Ended()
    {
        var editor = PromotionTests.FillerPromo();
        var promotion = editor.Blocks.OfType<PromotionBlockEditor>().Single().Promotion;
        var today = LocalTime.For(LocalTime.DefaultZone).Today(app.Clock.GetUtcNow());
        var ends = today.AddDays(-2);
        promotion.Starts.Text = today.AddDays(-30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        promotion.Ends.Text = ends.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return (editor.Draft, ends);
    }

    // Time alone never stops an approved version from exporting (owner, 2026-11-02: "There might be a
    // legitimate reason to re-export. Local archives, for example"). Approved while it ran, the
    // promotion's end is said as information, not as a Must fix, and the export is there.
    [Fact]
    public async Task An_approved_promotion_that_has_since_ended_still_exports_and_says_so()
    {
        var (draft, ends) = Ended();
        var store = app.Stores.Campaigns(DemoApp.Client, Actor.Demo);
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "Filler Promo", draft);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        // Approved while it ran: the store records it as the page's click did then.
        await store.ApproveAsync(save, "Priya");
        await using var page = await OpenCampaignPage.OpenAsync(app, save.Id, FakeProofreader.Clean);

        var html = await page.HtmlAsync();
        Assert.Equal(1, RenderedPage.Count(html, "approved"));
        var day = ends.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal($"This promotion ended on {day}. The approved version can still be exported, for example for your records.",
            RenderedPage.Named(html, "checks-since-approved"));
        Assert.DoesNotContain("which has already passed", RenderedPage.Named(html, "checks-findings"));
        // Whatever is worth a look depends on her known items, which another test here adds to; going on
        // past it is one click, and then the export is there.
        if (RenderedPage.Count(html, "export-anyway") > 0)
        {
            await page.ClickAsync("export-anyway");
            html = await page.HtmlAsync();
        }
        Assert.Equal(1, RenderedPage.Count(html, "export"));
        Assert.Equal(1, RenderedPage.Count(html, "assistant-download"));
    }
}
