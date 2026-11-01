using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The owner, 2026-10-31, answer 3b: a suggested fix beside each finding, used with one click. The
/// finding's place names the block; its quote is found in that block's own words, whitespace and case
/// aside, and every match is replaced (4b). The template's fixed text, a place that is no one block, and
/// words not there are said, never guessed. Prices and benefit amounts are never changed as text. Rules
/// offer a fix where it is certain (3abcd): known-item near misses on tier and treatment names, a benefit
/// line nearly one of hers (set as the benefit), emoji spacing, and the phone number when exactly one is
/// registered.
/// </summary>
public class SuggestedFixTests
{
    private static CampaignEditor Editor(Action<CampaignDraft>? change = null)
    {
        var draft = DraftFixtures.Finished();
        change?.Invoke(draft);
        return CampaignEditor.Open(draft);
    }

    private static Finding Llm(string location, string quote, string? suggestion) =>
        new(Severity.Blocker, "ai-spelling", location, "Misspelt.", quote) { Suggestion = suggestion };

    private static TextBlockEditor Text(CampaignEditor editor, string label) => (TextBlockEditor)editor.Blocks.Single(b => b.Label == label);

    private static ParagraphsBlockEditor Paragraphs(CampaignEditor editor, string label) =>
        (ParagraphsBlockEditor)editor.Blocks.Single(b => b.Label == label);

    private static OfferEditor Offer(CampaignEditor editor) => ((OfferBlockEditor)editor.Blocks.Single(b => b.Label == "Offer")).Offer;

    // ---- The LLM's suggestions ----

    [Fact]
    public void The_quote_in_its_block_is_replaced_and_checking_first_changes_nothing()
    {
        var editor = Editor(d => d.Text("Headline").Set("Recieve your glow"));
        var finding = Llm("Headline", "Recieve", "Receive");

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Check(editor, finding));
        Assert.Equal("Recieve your glow", Text(editor, "Headline").Field.Text);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));
        Assert.Equal("Receive your glow", Text(editor, "Headline").Field.Text);
        Assert.Equal("Receive your glow", editor.Draft.Text("Headline").Value);
    }

    // Choice 4b: every match in the block, matched ignoring whitespace and case.
    [Fact]
    public void Every_match_in_the_block_is_replaced_whitespace_and_case_aside()
    {
        var editor = Editor(d => d.Paragraphs("Opening").Set(["Book your\nvisit now.", "Then BOOK YOUR VISIT again."]));

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, Llm("Opening", "book your  visit", "Reserve your visit")));

        Assert.Equal(["Reserve your visit now.", "Then Reserve your visit again."], editor.Draft.Paragraphs("Opening").Value);
    }

    [Fact]
    public void The_subject_line_is_a_place_too()
    {
        var editor = Editor(d => d.Subject.Set("Your glow awaitss"));

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, Llm(FindingPlace.Subject, "awaitss", "awaits")));

        Assert.Equal("Your glow awaits", editor.Subject.Text);
    }

    [Fact]
    public void The_template_s_fixed_text_is_changed_only_in_the_template()
    {
        var editor = Editor();
        var greeting = Text(editor, "Greeting");
        Assert.True(greeting.IsFixed);
        var before = editor.Draft.Text("Greeting").Value;

        Assert.Equal(FixOutcome.Fixed, SuggestedFix.Check(editor, Llm("Greeting", "Beautiful", "Lovely")));
        Assert.Equal(FixOutcome.Fixed, SuggestedFix.Apply(editor, Llm("Greeting", "Beautiful", "Lovely")));
        Assert.Equal(before, editor.Draft.Text("Greeting").Value);
        Assert.Equal("This is the template's fixed text: change it in the template.", SuggestedFix.FixedText);
    }

    [Fact]
    public void No_one_place_no_suggestion_or_words_not_there_change_nothing()
    {
        var editor = Editor(d => d.Text("Headline").Set("Recieve your glow"));

        Assert.Equal(FixOutcome.Ambiguous, SuggestedFix.Apply(editor, Llm("Whole email", "Recieve", "Receive")));
        Assert.Equal(FixOutcome.Ambiguous, SuggestedFix.Apply(editor, Llm("Headline", "Recieve", null)));
        Assert.Equal(FixOutcome.Ambiguous, SuggestedFix.Apply(editor, Llm("Headline", "", "Receive")));
        Assert.Equal(FixOutcome.NotFound, SuggestedFix.Apply(editor, Llm("Headline", "Glowy", "Glow")));
        Assert.Equal("Recieve your glow", Text(editor, "Headline").Field.Text);
        Assert.Equal("Change this by hand.", SuggestedFix.ByHand);
    }

    // Prices are structured: a quote of one is never replaced in the price field.
    [Fact]
    public void A_price_is_never_changed_as_text()
    {
        var editor = Editor();
        var price = Offer(editor).Tiers[0].Price.Text;

        SuggestedFix.Apply(editor, Llm("Offer", price, "1"));

        Assert.Equal(price, Offer(editor).Tiers[0].Price.Text);
        Assert.Equal(149m, editor.Draft.Offer("Offer").Tiers[0].MonthlyPrice.Value);
    }

    // ---- The rules' suggestions (3abcd) ----

    private static readonly KnownItems Known = new(
    [
        new KnownTier(KnownItem.NewId(), "Gold Member", 149m, []),
        new KnownTreatment(KnownItem.NewId(), "wellness injection"),
        new KnownBenefit(KnownItem.NewId(), new PercentOff(5, "any qualifying treatments")),
    ]);

    private static BusinessContext Business => new BusinessContext("Neelam") with { Known = Known };

    private static Finding Rule(CampaignEditor editor, string rule, string location, BusinessContext? business = null) =>
        editor.Status(null, business ?? Business).Findings.Single(f => f.Rule == rule && f.Location.StartsWith(location, StringComparison.Ordinal));

    [Fact]
    public void A_tier_name_near_a_known_tier_offers_the_known_name()
    {
        var editor = Editor(d => d.Offer("Offer").Tiers[0].Name.Set("Gold Membr"));
        var finding = Rule(editor, "known-item", "Offer › Tier 1 name");
        Assert.Equal("Gold Member", finding.Suggestion);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));

        Assert.Equal("Gold Member", Offer(editor).Tiers[0].Name.Text);
        Assert.DoesNotContain(editor.Status(null, Business).Findings, f => f.Location == "Offer › Tier 1 name");
    }

    [Fact]
    public void A_treatment_near_a_known_one_offers_the_known_name()
    {
        var editor = Editor(d => d.Offer("Offer").Tiers[0].Benefits[2].Set(new DiscountedItem(50, "welness injection", "per visit")));
        var finding = Rule(editor, "known-item", "Offer › Tier 1, benefit 3");
        Assert.Equal("wellness injection", finding.Suggestion);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));

        Assert.Equal("wellness injection", Offer(editor).Tiers[0].Benefits[2].ItemName);
        Assert.Equal(new DiscountedItem(50, "wellness injection", "per visit"), editor.Draft.Offer("Offer").Tiers[0].Benefits[2].Value);
    }

    // Choice b: the line it nearly is, set as the benefit, its amounts as written.
    [Fact]
    public void A_benefit_line_near_one_of_hers_is_set_as_that_line()
    {
        var editor = Editor(d => d.Offer("Offer").Tiers[0].Benefits[1].Set(new PercentOff(5, "any qualifyng treatments")));
        var finding = Rule(editor, "known-item", "Offer › Tier 1, benefit 2");
        Assert.Equal(new PercentOff(5, "any qualifying treatments"), finding.SuggestedBenefit);
        Assert.Equal(new PercentOff(5, "any qualifying treatments").Describe(), finding.Suggestion);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));

        Assert.Equal(new PercentOff(5, "any qualifying treatments"), editor.Draft.Offer("Offer").Tiers[0].Benefits[1].Value);
        Assert.Equal("any qualifying treatments", Offer(editor).Tiers[0].Benefits[1].AppliesTo);
        Assert.DoesNotContain(editor.Status(null, Business).Findings, f => f.Location == "Offer › Tier 1, benefit 2");
    }

    // Choice c.
    [Fact]
    public void An_emoji_touching_a_word_offers_the_text_spaced()
    {
        var editor = Editor(d => d.Text("Headline").Set("Glow🤍today"));
        var finding = Rule(editor, "emoji-spacing", "Headline");
        Assert.Equal("Glow 🤍 today", finding.Suggestion);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));

        Assert.Equal("Glow 🤍 today", Text(editor, "Headline").Field.Text);
        Assert.DoesNotContain(editor.Status(null, Business).Findings, f => f.Rule == "emoji-spacing");
    }

    [Theory]
    [InlineData("Glow🤍today", "Glow 🤍 today")]
    [InlineData("Glow 🤍🤍 today", "Glow 🤍🤍 today")]
    [InlineData("Hi Beautiful🤍", "Hi Beautiful 🤍")]
    [InlineData("Ask our nurse👩‍⚕️now", "Ask our nurse 👩‍⚕️ now")]
    [InlineData("Wellness ❤️today", "Wellness ❤️ today")]
    public void Emoji_spacing_puts_a_space_only_where_an_emoji_touches_a_word(string text, string spaced) =>
        Assert.Equal(spaced, CampaignReview.EmojiSpaced(text));

    // Choice d: with exactly one number registered, that one; with two, none is guessed.
    [Fact]
    public void A_phone_number_not_registered_offers_the_one_registered_number()
    {
        PhoneNumber.TryParse("(425) 773-5261", out var ours);
        PhoneNumber.TryParse("(425) 555-0100", out var other);
        var one = new BusinessContext("Neelam") with { Phones = new PhoneNumbers([ours!]) };
        var editor = Editor(d => d.Paragraphs("Opening").Set(["Call 425-877-8646 to book."]));
        var finding = Rule(editor, "phone-registered", "Opening", one);
        Assert.Equal(ours!.ToString(), finding.Suggestion);

        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, finding));

        Assert.Equal([$"Call {ours} to book."], editor.Draft.Paragraphs("Opening").Value);
        Assert.Null(Rule(Editor(d => d.Paragraphs("Opening").Set(["Call 425-877-8646 to book."])), "phone-registered", "Opening",
            new BusinessContext("Neelam") with { Phones = new PhoneNumbers([ours, other!]) }).Suggestion);
    }

    // ---- Kept with the proofread ----

    [Fact]
    public async Task A_kept_proofread_keeps_its_suggestions_and_one_kept_before_still_loads()
    {
        var blobs = new InMemoryBlobBackend();
        var store = new TestRecords(new ManualClock(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero))).Campaigns(blobs, TimeSpan.FromDays(1));
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "Test", DraftFixtures.Finished());
        var finding = Llm("Headline", "Recieve", "Receive");

        await store.SaveProofreadAsync(save, new ProofreadRecord(save.SavedAt, [finding], []));
        Assert.Equal("Receive", Assert.Single((await store.ProofreadOfAsync(save))!.Findings).Suggestion);
        Assert.Contains("\"suggestion\":\"Receive\"", blobs.Text(CampaignStore.ProofreadName(save)));

        // As a build before suggestions wrote it: no suggestion field at all.
        var older = await store.SaveDraftAsync(Guid.NewGuid(), "Older", DraftFixtures.Finished());
        blobs.Put(CampaignStore.ProofreadName(older),
            """{"version":1,"at":"2026-10-20T12:00:00+00:00","findings":[{"severity":"must-fix","rule":"ai-spelling","location":"Headline","message":"Misspelt. Suggest: Receive","excerpt":"Recieve"}],"photos":[]}""");
        var loaded = Assert.Single((await store.ProofreadOfAsync(older))!.Findings);
        Assert.Equal(("Misspelt. Suggest: Receive", null), (loaded.Message, loaded.Suggestion));
    }

    // Not part of what says she was shown it at export, nor of the assistant's JSON.
    [Fact]
    public void A_suggestion_is_never_exported()
    {
        var finding = new Finding(Severity.Warning, "ai-clarity", "Headline", "Reads oddly.", "Recieve") { Suggestion = "SUGGESTED WORDS" };
        var campaign = DraftFixtures.Finished().Build().Campaign!;
        var at = DateTimeOffset.UnixEpoch;
        var first = CampaignGate.DemoReview(campaign, new Approval("Priya", at), proofread: [finding]);
        var approval = new Approval("Priya", at) with { WarningsSeen = WarningsSeen.Of("Priya", at, first.Warnings) };
        var report = CampaignGate.DemoReview(campaign, approval, proofread: [finding]);

        var json = AssistantExport.Offer(report, approval).Json!;

        Assert.Contains("Headline: Reads oddly.", json);
        Assert.DoesNotContain("SUGGESTED WORDS", json);
    }

    // Not part of what says she was shown it at export.
    [Fact]
    public void A_suggestion_is_not_part_of_the_seen_key()
    {
        var finding = Llm("Headline", "Recieve", "Receive");
        Assert.Equal(finding.SeenKey, (finding with { Suggestion = null }).SeenKey);
        Assert.Equal(finding.SeenKey, (finding with { Suggestion = "Something else" }).SeenKey);
    }
}

/// <summary>"Use this" on the campaign page, held as its circuit holds it, read by named elements.</summary>
public class SuggestedFixPageTests(ProofreadDemoApp app) : IClassFixture<ProofreadDemoApp>
{
    private async Task<Guid> SavedAsync(CampaignDraft draft)
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Hello", draft);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save.Id;
    }

    // An LLM finding: the suggestion beside it, "Use this" changes the form only, and the finding greys.
    [Fact]
    public async Task Use_this_puts_the_llm_s_words_in_the_form_and_the_finding_greys()
    {
        var finding = new Finding(Severity.Blocker, "ai-spelling", FindingPlace.Subject, "Misspelt.", "clinc") { Suggestion = "clinic" };
        var draft = ProofreadPageTests.Plain();
        draft.Subject.Set("A note from the clinc");
        var id = await SavedAsync(draft);
        await using var page = await OpenCampaignPage.OpenAsync(app, id, new FakeProofreader(finding));
        await page.ClickAsync("proofread-button");

        var html = await page.HtmlAsync();
        Assert.Equal("Suggested: \"clinic\"", RenderedPage.Named(html, $"finding-suggestion-{finding.SeenKey}"));
        Assert.Equal("Use this", RenderedPage.Named(html, $"finding-use-{finding.SeenKey}"));

        await page.ClickAsync($"finding-use-{finding.SeenKey}");

        html = await page.HtmlAsync();
        Assert.Equal("A note from the clinic", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal("Changed in the form. Save the campaign to keep it.", RenderedPage.Named(html, "fix-message"));
        Assert.Equal("Looks fixed", RenderedPage.Named(html, $"finding-fixed-{finding.SeenKey}"));
        Assert.Equal(0, RenderedPage.Count(html, $"finding-use-{finding.SeenKey}"));
        // Nothing saved.
        Assert.Single(await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).HistoryAsync(DocumentKind.Draft, id));
    }

    // A rule's finding goes once the checks run again on the changed form.
    [Fact]
    public async Task Use_this_on_a_rule_s_finding_makes_it_go()
    {
        var draft = ProofreadPageTests.Plain();
        draft.Text("Headline").Set("Our new hours🤍today");
        await using var page = await OpenCampaignPage.OpenAsync(app, await SavedAsync(draft), new FakeProofreader());
        var html = await page.HtmlAsync();
        var use = Assert.Single(RenderedPage.NamesStartingWith(html, "finding-use-"));
        Assert.Equal("Suggested: \"Our new hours 🤍 today\"", RenderedPage.Named(html, $"finding-suggestion-{use}"));

        await page.ClickAsync($"finding-use-{use}");

        html = await page.HtmlAsync();
        Assert.Empty(RenderedPage.NamesStartingWith(html, "finding-use-"));
        Assert.DoesNotContain("emoji touches", RenderedPage.Named(html, "checks-findings"));
    }

    // With no one place for it, the page says to change it by hand and offers no button.
    [Fact]
    public async Task With_no_one_place_it_says_change_this_by_hand_and_offers_no_button()
    {
        var finding = new Finding(Severity.Warning, "ai-tone", "Whole email", "A little long.", "Our new opening hours") { Suggestion = "New hours" };
        await using var page = await OpenCampaignPage.OpenAsync(app, await SavedAsync(ProofreadPageTests.Plain()), new FakeProofreader(finding));
        await page.ClickAsync("proofread-button");

        var html = await page.HtmlAsync();
        Assert.Equal("Change this by hand.", RenderedPage.Named(html, $"finding-by-hand-{finding.SeenKey}"));
        Assert.Equal(0, RenderedPage.Count(html, $"finding-use-{finding.SeenKey}"));
    }
}
