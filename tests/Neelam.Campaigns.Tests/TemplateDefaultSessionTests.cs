using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Update to template over storage for content a template starts each campaign with (owner, 2026-11-03:
/// "Updating should modify only those that have the default and are not approved"): the version the
/// campaign records is read to tell what it started with from what she changed, an approval that stands
/// leaves it all as she has it, and a recorded version no longer in use leaves hers.
/// </summary>
public class TemplateDefaultSessionTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_container, TimeSpan.FromDays(1));

    // A fixed greeting and a headline each campaign starts with: the smallest template with both kinds of content.
    private static CampaignTemplate News(string headline, string greeting = "Hi Beautiful", string name = "News")
    {
        var editor = TemplateEditor.StartBlank(Catalogs.Shipped);
        editor.Name = name;
        var g = editor.Add(BlockType.Greeting);
        (g.Label, g.Content, g.Text) = ("Greeting", BlockContent.Fixed, greeting);
        var h = editor.Add(BlockType.Heading);
        (h.Label, h.Content, h.Text) = ("Headline", BlockContent.Default, headline);
        return editor.Build();
    }

    private async Task<SaveRef> SaveTemplate(Guid id, CampaignTemplate template)
    {
        var save = await Store.SaveTemplateAsync(id, template);
        _clock.Now += TimeSpan.FromMinutes(1);
        return save;
    }

    // A campaign started from the template's newest version and saved, its headline as given or as it started.
    private async Task<SaveRef> Campaign(Guid template, string? headline = null)
    {
        var session = (await DraftSession.StartAsync(Store, template))!;
        if (headline is not null) ((TextBlockEditor)session.Editor.Blocks[1]).Field.Text = headline;
        var save = await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        return save;
    }

    private async Task<DraftSession> Updated(Guid campaign, Guid template)
    {
        var session = (await DraftSession.OpenAsync(Store, campaign))!;
        Assert.Null(session.UseTemplate((await session.ChooseTemplateAsync(template))!));
        return session;
    }

    [Fact]
    public async Task Update_to_template_replaces_a_start_she_kept_and_leaves_one_she_changed()
    {
        var id = Guid.NewGuid();
        await SaveTemplate(id, News("NEWS FROM NEELAM"));
        var kept = await Campaign(id);
        var changed = await Campaign(id, "WE'RE TURNING ONE");
        await SaveTemplate(id, News("NEWS FROM NEELAM AESTHETICS", greeting: "Hello lovely"));

        var keptNow = await Updated(kept.Id, id);
        var changedNow = await Updated(changed.Id, id);

        Assert.Equal("NEWS FROM NEELAM AESTHETICS", keptNow.Editor.Draft.Text("Headline").Value);
        Assert.Equal("WE'RE TURNING ONE", changedNow.Editor.Draft.Text("Headline").Value);
        Assert.All(new[] { keptNow, changedNow }, s => Assert.Equal("Hello lovely", s.Editor.Draft.Text("Greeting").Value));
    }

    // "That's exactly what it means to me": the version on her page approved, its starts stay as they are,
    // while the template's fixed text still comes in, as before.
    [Fact]
    public async Task An_approved_campaign_keeps_its_starts_and_takes_fixed_text()
    {
        var id = Guid.NewGuid();
        await SaveTemplate(id, News("NEWS FROM NEELAM"));
        var campaign = await Store.ApproveAsync(await Campaign(id), "Priya");
        await SaveTemplate(id, News("NEWS FROM NEELAM AESTHETICS", greeting: "Hello lovely"));

        var session = await Updated(campaign.Id, id);

        Assert.Equal("NEWS FROM NEELAM", session.Editor.Draft.Text("Headline").Value);
        Assert.Equal("Hello lovely", session.Editor.Draft.Text("Greeting").Value);

        // Withdrawn, the approval no longer stands, and the start follows the template again.
        await session.WithdrawApprovalAsync();
        var withdrawn = await Updated(campaign.Id, id);
        Assert.Equal("NEWS FROM NEELAM AESTHETICS", withdrawn.Editor.Draft.Text("Headline").Value);
    }

    // The version she was made from undone (and so, in a day, swept): what it started her with cannot be
    // told from what she wrote, so hers stays.
    [Fact]
    public async Task With_the_version_she_was_made_from_no_longer_in_use_hers_stays()
    {
        var id = Guid.NewGuid();
        var first = await SaveTemplate(id, News("NEWS FROM NEELAM"));
        var campaign = await Campaign(id);
        await SaveTemplate(id, News("NEWS FROM NEELAM AESTHETICS"));
        await Store.MarkUndoneAsync(first);

        var session = await Updated(campaign.Id, id);

        Assert.Equal("NEWS FROM NEELAM", session.Editor.Draft.Text("Headline").Value);
    }

    // Another template chosen: the version of the one she was made from is read, so its start, untouched,
    // gives way to the new template's.
    [Fact]
    public async Task Choosing_another_template_reads_the_one_she_was_made_from()
    {
        var news = Guid.NewGuid();
        await SaveTemplate(news, News("NEWS FROM NEELAM"));
        var kept = await Campaign(news);
        var changed = await Campaign(news, "WE'RE TURNING ONE");
        var holiday = Guid.NewGuid();
        await SaveTemplate(holiday, News("HAPPY HOLIDAYS", name: "Holidays"));

        Assert.Equal("HAPPY HOLIDAYS", (await Updated(kept.Id, holiday)).Editor.Draft.Text("Headline").Value);
        Assert.Equal("WE'RE TURNING ONE", (await Updated(changed.Id, holiday)).Editor.Draft.Text("Headline").Value);
    }

    // A template with nothing to start with reads only itself, as before.
    [Fact]
    public async Task A_template_with_nothing_to_start_with_reads_no_other_version()
    {
        var id = Guid.NewGuid();
        await SaveTemplate(id, DraftFixtures.Membership);
        var campaign = await Campaign(id);
        await SaveTemplate(id, DraftFixtures.Membership);
        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;
        _container.Reads.Clear();

        await session.ChooseTemplateAsync(id);

        Assert.Single(_container.Reads);
    }
}
