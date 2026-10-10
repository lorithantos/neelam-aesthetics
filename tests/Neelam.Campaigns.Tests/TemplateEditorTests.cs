using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Tests;

public class TemplateEditorTests
{
    private static readonly Campaign Target = SampleCampaigns.Corrected();

    // ---- Building and arranging

    [Fact]
    public void A_blank_template_cannot_be_saved_until_it_has_a_name_and_a_block()
    {
        var editor = TemplateEditor.StartBlank();

        Assert.Equal(["Give the template a name.", "Add at least one block."], editor.Errors().Select(e => e.Message));
        Assert.Throws<InvalidOperationException>(editor.Build);

        editor.Name = "Thank you";
        editor.Add(BlockType.Paragraphs);
        Assert.Empty(editor.Errors());
        Assert.Equal("Thank you", editor.Build().Name);
    }

    [Fact]
    public void New_blocks_are_labelled_by_type_and_numbered_when_the_label_is_taken()
    {
        var editor = TemplateEditor.StartBlank();

        editor.Add(BlockType.Paragraphs);
        editor.Add(BlockType.SignOff);
        editor.Add(BlockType.Paragraphs);
        editor.Add(BlockType.Paragraphs);

        Assert.Equal(["Paragraphs", "Sign-off", "Paragraphs 2", "Paragraphs 3"], editor.Blocks.Select(b => b.Label));
    }

    [Fact]
    public void Blocks_move_and_are_removed_and_moving_past_an_end_does_nothing()
    {
        var editor = TemplateEditor.StartBlank();
        var heading = editor.Add(BlockType.Heading);
        var body = editor.Add(BlockType.Paragraphs);
        var button = editor.Add(BlockType.Button);

        editor.MoveUp(button);
        Assert.Equal([heading, button, body], editor.Blocks);
        editor.MoveUp(heading);
        editor.MoveDown(body);
        Assert.Equal([heading, button, body], editor.Blocks);
        editor.MoveDown(heading);
        Assert.Equal([button, heading, body], editor.Blocks);

        editor.Remove(heading);
        Assert.Equal([button, body], editor.Blocks);
    }

    // ---- Starting from the baseline (owner, 2026-10-09: what every template should have is what
    // you get when you say new)

    // The standard lists Header, Heading, Sign-off, Button, Image; the blocks come as an email reads.
    [Fact]
    public void A_new_template_starts_with_the_standard_baseline_s_parts_in_email_order()
    {
        var editor = TemplateEditor.StartFrom(TemplateBaseline.Standard);

        Assert.Equal([BlockType.Header, BlockType.Heading, BlockType.Image, BlockType.Button, BlockType.SignOff],
            editor.Blocks.Select(b => b.Type));
        Assert.Equal(["Header", "Heading", "Image", "Button", "Sign-off"], editor.Blocks.Select(b => b.Label));
        Assert.True(editor.FromBaseline);
    }

    [Fact]
    public void A_client_s_own_baseline_gives_its_parts_and_an_empty_one_a_blank_start()
    {
        var own = TemplateEditor.StartFrom(new TemplateBaseline([BlockType.SignOff, BlockType.Greeting, BlockType.Offer]));
        Assert.Equal([BlockType.Greeting, BlockType.Offer, BlockType.SignOff], own.Blocks.Select(b => b.Type));

        var none = TemplateEditor.StartFrom(TemplateBaseline.None);
        Assert.Empty(none.Blocks);
        Assert.False(none.FromBaseline);
        Assert.False(TemplateEditor.CopyOf(DraftFixtures.Membership).FromBaseline);
    }

    // Each starting block is left to each campaign, and is moved and removed like any other.
    [Fact]
    public void The_starting_blocks_are_written_by_each_campaign_not_fixed()
    {
        var editor = TemplateEditor.StartFrom(TemplateBaseline.Standard);

        Assert.All(editor.Blocks, b => Assert.False(b.IsFixed));
        Assert.All(editor.Blocks, b => Assert.True(b.Required));
        // Only the name is wanting: an empty fixed block would ask to be filled in.
        Assert.Equal(["Give the template a name."], editor.Errors().Select(e => e.Message));
        editor.Name = "Spring news";
        Assert.All(editor.Build().Blocks, b => Assert.Null(b.Fixed));

        var image = editor.Blocks.Single(b => b.Type == BlockType.Image);
        editor.MoveDown(image);
        editor.Remove(editor.Blocks[0]);
        Assert.Equal(["Heading", "Button", "Image", "Sign-off"], editor.Blocks.Select(b => b.Label));
    }

    [Fact]
    public void A_template_started_from_the_baseline_lacks_nothing_until_a_part_is_removed()
    {
        var editor = TemplateEditor.StartFrom(TemplateBaseline.Standard);

        Assert.Empty(editor.Missing(TemplateBaseline.Standard));
        editor.Remove(editor.Blocks.Single(b => b.Type == BlockType.Button));
        Assert.Equal([BlockType.Button], editor.Missing(TemplateBaseline.Standard).Select(g => g.Part));
    }

    // ---- Opening and copying

    // Opening a template and saving it unchanged gives the same template, fixed content and all.
    [Fact]
    public void A_template_opened_and_saved_unchanged_is_the_same_template()
    {
        var rebuilt = TemplateEditor.Open(DraftFixtures.Membership).Build();

        Assert.Equal(CampaignJson.SerializeTemplate(DraftFixtures.Membership), CampaignJson.SerializeTemplate(rebuilt));
    }

    [Fact]
    public void A_copy_is_named_as_one_and_changing_it_leaves_the_original_alone()
    {
        var before = CampaignJson.SerializeTemplate(DraftFixtures.Membership);
        var copy = TemplateEditor.CopyOf(DraftFixtures.Membership);

        Assert.Equal("Copy of Membership announcement", copy.Name);
        copy.Blocks.Single(b => b.Label == "Greeting").Text = "Hello there";
        copy.Remove(copy.Blocks.Single(b => b.Label == "Photo"));
        var changed = copy.Build();

        Assert.Equal(before, CampaignJson.SerializeTemplate(DraftFixtures.Membership));
        Assert.Equal("Hello there", Assert.IsType<GreetingBlock>(changed.Blocks.Single(b => b.Label == "Greeting").Fixed).Text);
        Assert.DoesNotContain(changed.Blocks, b => b.Label == "Photo");
    }

    // The editor's whole path: Neelam's membership template typed in from blank, field by field,
    // previews exactly as the fixture's does.
    [Fact]
    public void The_membership_template_built_from_blank_previews_like_the_fixture()
    {
        var editor = TemplateEditor.StartBlank();
        editor.Name = "Membership announcement";

        var header = Target.Block<HeaderBlock>("Header");
        Fix(editor.Add(BlockType.Header), b => (b.Text, b.PhotoName, b.PhotoAltText) = (header.Text, header.Photo!.Name, header.Photo.AltText ?? ""));
        editor.Add(BlockType.Spacer);
        editor.Add(BlockType.Heading).Label = "Headline";
        Fix(editor.Add(BlockType.Greeting), b => b.Text = Target.Block<GreetingBlock>("Greeting").Text);
        editor.Add(BlockType.Paragraphs).Label = "Opening";
        var offer = editor.Add(BlockType.Offer);
        (offer.Recurring, offer.Marker) = (true, "🤍");
        Fix(editor.Add(BlockType.Paragraphs), b => (b.Label, b.Paragraphs) =
            ("Closing", string.Join("\r\n\r\n", Target.Block<ParagraphsBlock>("Closing").Paragraphs)));
        var signOff = Target.Block<SignOffBlock>("Sign-off").SignOff;
        Fix(editor.Add(BlockType.SignOff), b => (b.Valediction, b.From, b.Tagline) = (signOff.Valediction, signOff.From, signOff.Tagline ?? ""));
        Fix(editor.Add(BlockType.FinePrint), b => (b.Label, b.Required, b.Text) =
            ("Disclaimer", false, Target.Block<FinePrintBlock>("Disclaimer").Text));
        var photo = editor.Add(BlockType.Image);
        (photo.Label, photo.Required) = ("Photo", false);
        editor.Add(BlockType.Button).Label = "Call to action";

        var built = editor.Build();

        Assert.Equal(TemplatePreview.PlainText(DraftFixtures.Membership.Blocks), TemplatePreview.PlainText(built.Blocks));
        Assert.Equal(DraftFixtures.Membership.Blocks.Select(b => (b.Label, b.Type, b.Required)), built.Blocks.Select(b => (b.Label, b.Type, b.Required)));
    }

    private static void Fix(EditableBlock block, Action<EditableBlock> fill)
    {
        block.IsFixed = true;
        fill(block);
    }

    // ---- What stops a save

    [Fact]
    public void Fixed_content_must_be_filled_in()
    {
        var editor = Named();
        editor.Add(BlockType.Greeting).IsFixed = true;
        editor.Add(BlockType.SignOff).IsFixed = true;

        Assert.Equal(
            [("Greeting", "Fill in the greeting, or let each campaign write it."),
             ("Sign-off", "Fill in the valediction, or let each campaign write it."),
             ("Sign-off", "Fill in the name it is from, or let each campaign write it.")],
            editor.Errors().Select(e => (e.Where, e.Message)));
    }

    [Fact]
    public void A_fixed_button_needs_a_web_address()
    {
        var editor = Named();
        var button = editor.Add(BlockType.Button);
        (button.IsFixed, button.ButtonLabel, button.ButtonUrl) = (true, "Book now", "neelamaesthetics");

        Assert.Contains("is not a web address", Assert.Single(editor.Errors()).Message);

        button.ButtonUrl = "https://neelamaesthetics.square.site/";
        Assert.Empty(editor.Errors());
    }

    [Fact]
    public void Labels_must_be_present_and_different_ignoring_case()
    {
        var editor = Named();
        editor.Add(BlockType.Paragraphs).Label = "Opening";
        editor.Add(BlockType.Paragraphs).Label = " opening ";
        editor.Add(BlockType.Heading).Label = " ";

        Assert.Equal(
            [("A heading block", "Give the block a label."),
             ("Opening", "Two blocks have this label; findings could not say which one they mean.")],
            editor.Errors().Select(e => (e.Where, e.Message)));
    }

    [Theory]
    [InlineData(BlockType.Offer)]
    [InlineData(BlockType.Spacer)]
    public void Offers_and_spacers_cannot_be_fixed(BlockType type)
    {
        var block = Named().Add(type);

        Assert.False(block.CanBeFixed);
        Assert.Throws<InvalidOperationException>(() => block.IsFixed = true);
    }

    [Fact]
    public void Fixed_paragraphs_split_on_blank_lines_whatever_the_line_endings()
    {
        var editor = Named();
        var body = editor.Add(BlockType.Paragraphs);
        (body.IsFixed, body.Paragraphs) = (true, "First line\r\nstill first.\r\n\r\n  \r\nSecond.\n\nThird.  ");

        var paragraphs = Assert.IsType<ParagraphsBlock>(editor.Build().Blocks.Single().Fixed).Paragraphs;
        Assert.Equal(["First line\nstill first.", "Second.", "Third."], paragraphs);
    }

    // ---- Advice: what the checks would say about every campaign

    [Fact]
    public void An_offer_with_no_button_block_blocks_every_campaign()
    {
        var editor = Named();
        editor.Add(BlockType.Offer);

        var advice = Assert.Single(editor.Advice());
        Assert.Equal((Severity.Blocker, "cta-required", "Offer"), (advice.Severity, advice.Rule, advice.Location));

        var button = editor.Add(BlockType.Button);
        button.Required = false;
        Assert.Equal((Severity.Warning, "cta-required"), Assert.Single(editor.Advice()) is var w ? (w.Severity, w.Rule) : default);

        button.Required = true;
        Assert.Empty(editor.Advice());
    }

    [Fact]
    public void Fixed_content_is_checked_as_every_campaign_will_be()
    {
        var editor = Named();
        var button = editor.Add(BlockType.Button);
        (button.IsFixed, button.ButtonLabel, button.ButtonUrl) = (true, "Join", "http://neelamaesthetics.square.site/");
        var greeting = editor.Add(BlockType.Greeting);
        (greeting.IsFixed, greeting.Text) = (true, "Results guaranteed!");

        var advice = editor.Advice().Select(a => (a.Severity, a.Rule, a.Location)).ToList();

        Assert.Equal([(Severity.Blocker, "cta-https", "Button"), (Severity.Warning, "restricted-term", "Greeting")], advice);
        Assert.All(editor.Advice(), a => Assert.StartsWith("In the template's fixed content", a.Message));
    }

    // A medical term in fixed text needs a disclaimer; a fine-print block lets each campaign add one.
    [Fact]
    public void A_fixed_medical_term_draws_advice_only_when_there_is_nowhere_for_a_disclaimer()
    {
        var editor = Named();
        var closing = editor.Add(BlockType.Paragraphs);
        (closing.IsFixed, closing.Paragraphs) = (true, "Ask us about our wellness injections.");

        Assert.Equal("medical-disclaimer", Assert.Single(editor.Advice()).Rule);

        editor.Add(BlockType.FinePrint).Required = false;
        Assert.Empty(editor.Advice());
    }

    [Fact]
    public void The_client_s_own_policy_decides_the_advice()
    {
        var editor = Named();
        var greeting = editor.Add(BlockType.Greeting);
        (greeting.IsFixed, greeting.Text) = (true, "Welcome to the Beauty Bank");

        Assert.Equal("restricted-term", Assert.Single(editor.Advice()).Rule);
        Assert.Empty(editor.Advice(new CampaignPolicy { RestrictedTerms = new Dictionary<string, string>() }));
    }

    [Fact]
    public void The_membership_template_blocks_no_campaign_by_itself() =>
        Assert.DoesNotContain(TemplateAdvice.For(DraftFixtures.Membership.Blocks), a => a.Severity == Severity.Blocker);

    // ---- Preview

    [Fact]
    public void The_preview_shows_fixed_content_and_names_what_each_campaign_fills()
    {
        var preview = TemplatePreview.Blocks(DraftFixtures.Membership.Blocks);

        Assert.Equal(
            [BlockKind.Header, BlockKind.Spacer, BlockKind.Heading, BlockKind.Text, BlockKind.Heading, BlockKind.Text,
             BlockKind.Image, BlockKind.Button],
            preview.Select(b => b.Kind));
        Assert.Equal(Target.Block<HeaderBlock>("Header").Text, preview[0].Text);
        Assert.Equal("‹Headline: written for each campaign›", preview[2].Text);
        // Text runs together as Square shows it: the fixed greeting, then the opening to be written.
        Assert.Equal($"{Target.Block<GreetingBlock>("Greeting").Text}\n\n‹Opening: written for each campaign›", preview[3].Text);
        Assert.Equal("‹Offer: set out for each campaign›", preview[4].Text);
        Assert.Equal("‹Photo: chosen for each campaign, or left out›", preview[6].Image!.Name);
        Assert.Equal("‹Call to action: label and link chosen for each campaign›", preview[7].Text);
        Assert.True(TemplatePreview.IsPlaceholder(preview[7].Text));
        Assert.False(TemplatePreview.IsPlaceholder(preview[0].Text));
    }

    // While a fixed block is incomplete it previews as a placeholder; Errors says why.
    [Fact]
    public void An_incomplete_fixed_block_previews_as_a_placeholder()
    {
        var editor = Named();
        editor.Add(BlockType.Greeting).IsFixed = true;

        Assert.Equal("‹Greeting: written for each campaign›", Assert.Single(editor.Preview()).Text);
        Assert.NotEmpty(editor.Errors());
    }

    // ---- The guide

    [Fact]
    public void Every_block_type_has_a_guide()
    {
        Assert.Equal(Enum.GetValues<BlockType>().Order(), BlockGuide.All.Select(g => g.Type).Order());
        Assert.All(BlockGuide.All, g => Assert.Equal(g.Name, TemplateEditor.StartBlank().Add(g.Type).Label));
    }

    // The guide describes what the checks do, so every rule it names must be one they report.
    [Fact]
    public void The_guide_names_only_rules_the_checks_report()
    {
        var review = File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Campaigns", "CampaignReview.cs"));
        var reported = Regex.Matches(review, "\"([a-z]+(?:-[a-z]+)+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.All(BlockGuide.All.SelectMany(g => g.Rules), rule => Assert.Contains(rule, reported));
    }

    private static TemplateEditor Named()
    {
        var editor = TemplateEditor.StartBlank();
        editor.Name = "Test";
        return editor;
    }
}
