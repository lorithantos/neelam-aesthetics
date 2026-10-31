using System.Text.Json.Nodes;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A campaign brought up to a template's version (owner, 2026-10-31): her blocks matched to the
/// template's by label, then by type, her text kept, the template's fixed text taken, and every block
/// of hers that would lose what she wrote named, so the page can ask first. Pure: nothing is saved
/// and the draft it is given is left as it was.
/// </summary>
public class TemplateUpdateTests
{
    private static readonly Guid TemplateId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Version = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static CampaignTemplate Template(string name, params TemplateBlock[] blocks) => new(name, blocks);

    private static TemplateUpdate Update(CampaignDraft draft, CampaignTemplate template) =>
        TemplateUpdate.Of(draft, template, TemplateId, Version);

    private static readonly SignOff Hers = new("Love,", "Priya");
    private static readonly SignOff Team = new("With gratitude,", "The Neelam team");

    // Three blocks of one type, so only their labels can tell them apart.
    [Fact]
    public void A_block_with_the_same_label_and_type_keeps_her_text_under_the_template_s_label()
    {
        var draft = Template("News",
            new("Opening", BlockType.Paragraphs), new("Body", BlockType.Paragraphs), new("Closing", BlockType.Paragraphs)).Start();
        draft.Paragraphs("Opening").Set(["Thank you.", "Come and celebrate."]);
        draft.Paragraphs("Body").Set(["Book now."]);
        draft.Paragraphs("Closing").Set(["See you soon."]);

        var update = Update(draft, Template("News",
            new("OPENING", BlockType.Paragraphs), new("Body ", BlockType.Paragraphs), new("Closing", BlockType.Paragraphs)));

        Assert.Equal(["OPENING", "Body ", "Closing"], update.Draft.Blocks.Select(b => b.Label));
        Assert.Equal(["Thank you.", "Come and celebrate."], update.Draft.Paragraphs("OPENING").Value);
        Assert.Equal(Origin.Entered, update.Draft.Paragraphs("OPENING").Origin);
        Assert.Equal(["Book now."], update.Draft.Paragraphs("Body ").Value);
        Assert.Equal(["See you soon."], update.Draft.Paragraphs("Closing").Value);
        Assert.Empty(update.Dropped);
        Assert.Empty(update.Replaced);
        Assert.False(update.LosesAnything);
    }

    // Renamed in the template: the one block of its type left on each side is the same block.
    [Fact]
    public void The_one_unmatched_block_of_a_type_on_each_side_is_paired_whatever_its_label()
    {
        var draft = Template("News", new("Headline", BlockType.Heading), new("Opening", BlockType.Paragraphs)).Start();
        draft.Paragraphs("Opening").Set(["Thank you."]);

        var update = Update(draft, Template("News", new("Headline", BlockType.Heading), new("Intro", BlockType.Paragraphs)));

        Assert.Equal(["Thank you."], update.Draft.Paragraphs("Intro").Value);
        Assert.Empty(update.Dropped);
    }

    // Two of a type left on one side: which is which cannot be told, so nothing is paired.
    [Fact]
    public void Two_unmatched_blocks_of_a_type_on_one_side_are_not_paired()
    {
        var draft = Template("News", new("Opening", BlockType.Paragraphs), new("Body", BlockType.Paragraphs)).Start();
        draft.Paragraphs("Opening").Set(["Thank you."]);
        draft.Paragraphs("Body").Set(["Book now."]);

        var hersTwo = Update(draft, Template("News", new TemplateBlock("Intro", BlockType.Paragraphs)));

        Assert.False(hersTwo.Draft.Paragraphs("Intro").HasValue);
        Assert.Equal(["Opening", "Body"], hersTwo.Dropped);

        var one = Template("News", new TemplateBlock("Opening", BlockType.Paragraphs)).Start();
        one.Paragraphs("Opening").Set(["Thank you."]);

        var templateTwo = Update(one, Template("News", new("Intro", BlockType.Paragraphs), new("Outro", BlockType.Paragraphs)));

        Assert.False(templateTwo.Draft.Paragraphs("Intro").HasValue);
        Assert.False(templateTwo.Draft.Paragraphs("Outro").HasValue);
        Assert.Equal(["Opening"], templateTwo.Dropped);
    }

    // A block of hers with something in it that matches nothing goes, and is named; an empty one goes
    // unnamed. The same label on another type of block is not a match.
    [Fact]
    public void Her_blocks_that_match_nothing_are_dropped_and_named_when_they_hold_something()
    {
        var draft = Template("News",
            new("Headline", BlockType.Heading), new("Note", BlockType.Heading), new("Photo", BlockType.Image, Required: false),
            new("Opening", BlockType.Paragraphs)).Start();
        draft.Text("Headline").Set("We're turning one");
        draft.Text("Note").Set("Closed Monday");

        var update = Update(draft, Template("News",
            new("Headline", BlockType.Heading), new("Note", BlockType.FinePrint), new("Opening", BlockType.Paragraphs)));

        Assert.Equal(["Note"], update.Dropped);
        Assert.False(update.Draft.Text("Note").HasValue);
        Assert.Equal(BlockType.FinePrint, update.Draft["Note"].Type);
        Assert.Empty(update.Replaced);
        Assert.True(update.LosesAnything);
    }

    // The template now fixes a block she wrote herself: the template's text goes in, and hers is named.
    [Fact]
    public void A_block_the_template_now_fixes_takes_its_text_and_names_hers_as_replaced()
    {
        var draft = Template("News", new("Sign-off", BlockType.SignOff), new("Greeting", BlockType.Greeting)).Start();
        draft.SignOff("Sign-off").Set(Hers);
        draft.Text("Greeting").Set("Hi Beautiful");

        var update = Update(draft, Template("News",
            new("Sign-off", BlockType.SignOff, Fixed: new SignOffBlock("Sign-off", Team)),
            new("Greeting", BlockType.Greeting, Fixed: new GreetingBlock("Greeting", "Hi Beautiful"))));

        Assert.Equal(Team, update.Draft.SignOff("Sign-off").Value);
        Assert.Equal(Origin.Template, update.Draft.SignOff("Sign-off").Origin);
        // The same text as hers is nothing lost.
        Assert.Equal(["Sign-off"], update.Replaced);
        Assert.Empty(update.Dropped);
        Assert.True(update.LosesAnything);
    }

    // How a typo fixed in the template reaches a campaign: its fixed text is the template's, not hers.
    [Fact]
    public void Fixed_text_changed_in_the_template_reaches_the_campaign_without_asking()
    {
        var draft = Template("News", new TemplateBlock("Greeting", BlockType.Greeting, Fixed: new GreetingBlock("Greeting", "Hi Beautifull"))).Start();

        var update = Update(draft, Template("News", new TemplateBlock("Greeting", BlockType.Greeting, Fixed: new GreetingBlock("Greeting", "Hi Beautiful"))));

        Assert.Equal("Hi Beautiful", update.Draft.Text("Greeting").Value);
        Assert.Equal(Origin.Template, update.Draft.Text("Greeting").Origin);
        Assert.False(update.LosesAnything);
    }

    // Left to each campaign now: she keeps the text it had, and can edit it.
    [Fact]
    public void A_block_that_stopped_being_fixed_keeps_its_text_and_becomes_hers_to_edit()
    {
        var draft = Template("News", new TemplateBlock("Disclaimer", BlockType.FinePrint, Fixed: new FinePrintBlock("Disclaimer", "Results vary."))).Start();

        var update = Update(draft, Template("News", new TemplateBlock("Disclaimer", BlockType.FinePrint)));

        Assert.Equal("Results vary.", update.Draft.Text("Disclaimer").Value);
        Assert.Equal(Origin.Entered, update.Draft.Text("Disclaimer").Origin);
        Assert.False(Assert.Single(CampaignEditor.Open(update.Draft).Blocks).IsFixed);
        Assert.False(update.LosesAnything);
    }

    [Fact]
    public void The_blocks_come_in_the_template_s_order_with_its_types_and_whether_each_is_required()
    {
        var draft = Template("News",
            new("Headline", BlockType.Heading), new("Opening", BlockType.Paragraphs), new("Call to action", BlockType.Button)).Start();
        draft.Text("Headline").Set("We're turning one");

        var update = Update(draft, Template("News",
            new("Call to action", BlockType.Button, Required: false), new("Headline", BlockType.Heading), new("Opening", BlockType.Paragraphs)));

        Assert.Equal(["Call to action", "Headline", "Opening"], update.Draft.Blocks.Select(b => b.Label));
        Assert.False(update.Draft["Call to action"].Required);
        Assert.Equal("We're turning one", update.Draft.Text("Headline").Value);
    }

    // A block new to the template arrives empty, so it shows under Still to do; a fixed one arrives filled.
    [Fact]
    public void A_block_new_in_the_template_arrives_empty_or_with_its_fixed_text()
    {
        var draft = Template("News", new TemplateBlock("Headline", BlockType.Heading)).Start();
        draft.Text("Headline").Set("We're turning one");

        var update = Update(draft, Template("News",
            new("Headline", BlockType.Heading), new("Photo", BlockType.Image),
            new("Sign-off", BlockType.SignOff, Fixed: new SignOffBlock("Sign-off", Team))));

        Assert.False(update.Draft.Image("Photo").HasValue);
        Assert.Contains(update.Draft.Build().Problems, p => p.Location == "Photo");
        Assert.Equal(Team, update.Draft.SignOff("Sign-off").Value);
        Assert.False(update.LosesAnything);
    }

    // The template's current name, id and version; her label, subject and preheader as they were;
    // and the draft she had is untouched.
    [Fact]
    public void The_draft_takes_the_template_s_name_id_and_version_and_keeps_what_is_hers()
    {
        var draft = Template("News", new TemplateBlock("Headline", BlockType.Heading)).Start();
        draft.Label = "October send";
        draft.Subject.Set("Celebrate with us");
        draft.Preheader.Set("One year of Neelam");
        draft.Text("Headline").Set("We're turning one");
        var before = CampaignJson.SerializeDraft(draft);

        var update = Update(draft, Template("Monthly news", new TemplateBlock("Lead", BlockType.Heading)));

        Assert.Equal("Monthly news", update.Draft.TemplateName);
        Assert.Equal(TemplateId, update.Draft.TemplateId);
        Assert.Equal(Version, update.Draft.TemplateVersion);
        Assert.Equal("October send", update.Draft.Label);
        Assert.Equal("Celebrate with us", update.Draft.Subject.Value);
        Assert.Equal("One year of Neelam", update.Draft.Preheader.Value);
        Assert.Equal("We're turning one", update.Draft.Text("Lead").Value);
        Assert.Equal(before, CampaignJson.SerializeDraft(draft));
        Assert.Null(draft.TemplateId);
    }

    // Her whole campaign, offer and copied benefits included, comes across a template unchanged in
    // every block: the same email, every slot with its origin.
    [Fact]
    public void The_same_template_changes_nothing_in_her_campaign()
    {
        var copied = DraftFixtures.SecondSendReplayed();

        var update = Update(copied, DraftFixtures.Membership);

        Assert.False(update.LosesAnything);
        Assert.Equal(CampaignJson.SerializeContent(copied), CampaignJson.SerializeContent(update.Draft));
        var finished = DraftFixtures.Finished();
        Assert.Equal(
            EditorExport.Preview(finished.Build().Campaign!),
            EditorExport.Preview(Update(finished, DraftFixtures.Membership).Draft.Build().Campaign!));
    }

    [Fact]
    public void The_template_s_id_and_version_round_trip_in_the_draft_s_json()
    {
        var draft = Update(DraftFixtures.Finished(), DraftFixtures.Membership).Draft;

        var json = CampaignJson.SerializeDraft(draft);
        var reopened = CampaignJson.DeserializeDraft(json);

        Assert.Equal(TemplateId, reopened.TemplateId);
        Assert.Equal(Version, reopened.TemplateVersion);
        var saved = JsonNode.Parse(json)!;
        Assert.Equal(TemplateId, (Guid?)saved["templateId"]);
        Assert.Equal(Version, (DateTimeOffset?)saved["templateVersion"]);
        Assert.Equal(CampaignJson.SchemaVersion, (int?)saved["schema"]);
    }

    // Every campaign saved before 2026-10-31 has neither: it opens with no template attached, and
    // one with none attached saves as before.
    [Fact]
    public void An_old_draft_without_them_reads_as_no_template_attached()
    {
        var saved = JsonNode.Parse(CampaignJson.SerializeDraft(Update(DraftFixtures.Finished(), DraftFixtures.Membership).Draft))!.AsObject();
        Assert.True(saved.Remove("templateId"));
        Assert.True(saved.Remove("templateVersion"));

        var old = CampaignJson.DeserializeDraft(saved.ToJsonString());

        Assert.Null(old.TemplateId);
        Assert.Null(old.TemplateVersion);
        Assert.Equal(EditorExport.Preview(DraftFixtures.Finished().Build().Campaign!), EditorExport.Preview(old.Build().Campaign!));
        var none = JsonNode.Parse(CampaignJson.SerializeDraft(DraftFixtures.Finished()))!.AsObject();
        Assert.False(none.ContainsKey("templateId"));
        Assert.False(none.ContainsKey("templateVersion"));
    }
}
