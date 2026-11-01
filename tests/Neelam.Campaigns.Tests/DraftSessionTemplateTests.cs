using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A campaign and its template, over storage (owner, 2026-10-31): the campaign records the template
/// and version it was made from, sees when that template has a newer version or is gone, and takes a
/// template into the form only on her say, never saving it.
/// </summary>
public class DraftSessionTemplateTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_container, Grace);

    private static readonly CampaignTemplate Membership = DraftFixtures.Membership;

    // The Membership template with its greeting worded anew and its headline now fixed.
    private static CampaignTemplate Revised(string name = "Membership announcement") => new(name,
        Membership.Blocks.Select(b => b.Label switch
        {
            "Greeting" => b with { Fixed = new GreetingBlock("Greeting", "Hello lovely 🤍") },
            "Headline" => b with { Fixed = new HeadingBlock("Headline", "NEWS FROM NEELAM") },
            _ => b,
        }).ToList());

    private async Task<SaveRef> SaveTemplate(Guid id, CampaignTemplate template)
    {
        var save = await Store.SaveTemplateAsync(id, template);
        _clock.Now += TimeSpan.FromMinutes(1);
        return save;
    }

    private async Task<SaveRef> SaveDraft(Guid id, CampaignDraft draft)
    {
        var save = await Store.SaveDraftAsync(id, "WE’RE TURNING ONE!", draft);
        _clock.Now += TimeSpan.FromMinutes(1);
        return save;
    }

    [Fact]
    public async Task Starting_records_the_template_s_id_and_newest_version_and_saving_keeps_them()
    {
        var id = Guid.NewGuid();
        await SaveTemplate(id, Membership);
        var newest = await SaveTemplate(id, Revised());

        var session = (await DraftSession.StartAsync(Store, id))!;

        Assert.Equal(id, session.Editor.Draft.TemplateId);
        Assert.Equal(newest.SavedAt, session.Editor.Draft.TemplateVersion);
        var saved = await session.SaveAsync();
        var reopened = await Store.LoadDraftAsync(saved);
        Assert.Equal(id, reopened.TemplateId);
        Assert.Equal(newest.SavedAt, reopened.TemplateVersion);
    }

    [Fact]
    public async Task A_newer_version_of_the_template_or_an_undone_one_is_seen_from_one_listing()
    {
        var id = Guid.NewGuid();
        await SaveTemplate(id, Membership);
        var session = (await DraftSession.StartAsync(Store, id))!;
        Assert.Equal(TemplateStanding.Current, session.TemplateStatus(await session.TemplatesAsync()));

        var newer = await SaveTemplate(id, Revised());
        _container.Reads.Clear();
        var templates = await session.TemplatesAsync();

        Assert.Equal(TemplateStanding.Changed, session.TemplateStatus(templates));
        // A listing, not a read of any template.
        Assert.Empty(_container.Reads);

        // The version she was made from is newest again once the newer one is undone, and the one she
        // is attached to is undone: either way, it is not the newest.
        var onNewer = (await DraftSession.StartAsync(Store, id))!;
        await Store.MarkUndoneAsync(newer);
        Assert.Equal(TemplateStanding.Changed, onNewer.TemplateStatus(await onNewer.TemplatesAsync()));
        Assert.Equal(TemplateStanding.Current, session.TemplateStatus(await session.TemplatesAsync()));
    }

    // Every save of the template undone: the campaign says so, and still opens and checks as before.
    [Fact]
    public async Task A_template_with_no_save_in_use_is_gone_and_the_campaign_still_opens_and_checks()
    {
        var id = Guid.NewGuid();
        var only = await SaveTemplate(id, Membership);
        var start = (await DraftSession.StartAsync(Store, id))!;
        var campaign = await SaveDraft(Guid.NewGuid(), TemplateUpdate.Of(DraftFixtures.Finished(), Membership, id, only.SavedAt).Draft);
        await Store.MarkUndoneAsync(only);

        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;

        Assert.Equal(TemplateStanding.Gone, session.TemplateStatus(await session.TemplatesAsync()));
        Assert.Equal(TemplateStanding.Gone, start.TemplateStatus(await start.TemplatesAsync()));
        Assert.NotNull(session.Editor.Status().Review);
        Assert.Null(await session.ChooseTemplateAsync(id));
    }

    // A campaign saved before templates were recorded: none attached, and none guessed from the name it
    // saved (owner, 2026-11-01: "We should use an ID instead of the name"); the one-time update links it
    // (TemplateLinkTests).
    [Fact]
    public async Task With_no_template_attached_none_is_guessed_from_its_name()
    {
        var campaign = await SaveDraft(Guid.NewGuid(), DraftFixtures.Finished());
        await SaveTemplate(Guid.NewGuid(), Membership);
        await SaveTemplate(Guid.NewGuid(), new CampaignTemplate("Thank you", Membership.Blocks));
        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;

        var templates = await session.TemplatesAsync();

        Assert.Equal(TemplateStanding.None, session.TemplateStatus(templates));
        Assert.Null(session.TemplateNameNow(templates));
        // As the Templates list orders them: by name.
        Assert.Equal(["Membership announcement", "Thank you"], templates.Select(t => t.Title));
    }

    // Using a template changes the form and nothing in storage: leaving without saving keeps the old
    // version, and saving makes a new one that Undo takes back.
    [Fact]
    public async Task Using_a_template_changes_the_form_only_until_she_saves()
    {
        var campaign = await SaveDraft(Guid.NewGuid(), DraftFixtures.Finished());
        var id = Guid.NewGuid();
        var version = await SaveTemplate(id, new CampaignTemplate("Membership, monthly", Membership.Blocks));
        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;

        var choice = (await session.ChooseTemplateAsync(id))!;
        Assert.Null(session.Editor.Draft.TemplateId);
        Assert.Null(session.UseTemplate(choice));

        Assert.Equal(id, session.Editor.Draft.TemplateId);
        Assert.Equal(version.SavedAt, session.Editor.Draft.TemplateVersion);
        Assert.Equal("Membership, monthly", session.Editor.TemplateName);
        Assert.Equal(TemplateStanding.Current, session.TemplateStatus(await session.TemplatesAsync()));
        Assert.True(session.HasUnsavedChanges);
        Assert.Equal(campaign, session.Latest);
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, campaign.Id));
        Assert.Null((await DraftSession.OpenAsync(Store, campaign.Id))!.Editor.Draft.TemplateId);

        await session.SaveAsync();
        Assert.Equal(id, (await DraftSession.OpenAsync(Store, campaign.Id))!.Editor.Draft.TemplateId);
        Assert.True(await session.UndoLastSaveAsync());
        Assert.Null(session.Editor.Draft.TemplateId);
    }

    // Losing anything she wrote is asked first: the form stays as it is until she says yes to what
    // would be lost. Less lost by the time she says yes is still what she agreed to.
    [Fact]
    public async Task A_template_that_would_replace_her_text_is_asked_about_first()
    {
        var id = Guid.NewGuid();
        var first = await SaveTemplate(id, Membership);
        var campaign = await SaveDraft(Guid.NewGuid(), TemplateUpdate.Of(DraftFixtures.Finished(), Membership, id, first.SavedAt).Draft);
        await SaveTemplate(id, Revised());
        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;
        var before = CampaignJson.SerializeDraft(session.Editor.Draft);

        var asked = session.UseTemplate((await session.ChooseTemplateAsync(id))!);

        Assert.NotNull(asked);
        Assert.Equal(["Headline"], asked.Update.Replaced);
        Assert.Empty(asked.Update.Dropped);
        Assert.Equal(before, CampaignJson.SerializeDraft(session.Editor.Draft));

        // She clears the headline while asked: nothing of hers would be lost now, so yes applies.
        ((TextBlockEditor)session.Editor.Blocks.Single(b => b.Label == "Headline")).Field.Text = "";
        Assert.Null(session.UseTemplate(asked, confirmed: true));
        Assert.Equal("NEWS FROM NEELAM", session.Editor.Draft.Text("Headline").Value);
        Assert.Equal("Hello lovely 🤍", session.Editor.Draft.Text("Greeting").Value);
        Assert.Equal(TemplateStanding.Current, session.TemplateStatus(await session.TemplatesAsync()));
    }

    // Something more to lose by the time she says yes, written while she was asked: she is asked again.
    [Fact]
    public async Task Saying_yes_does_not_cover_a_loss_she_was_not_asked_about()
    {
        var id = Guid.NewGuid();
        var first = await SaveTemplate(id, Membership);
        var draft = TemplateUpdate.Of(DraftFixtures.Finished(), Membership, id, first.SavedAt).Draft;
        draft.Paragraphs("Opening").Clear();
        var campaign = await SaveDraft(Guid.NewGuid(), draft);
        await SaveTemplate(id, new CampaignTemplate("Membership announcement",
            Revised().Blocks.Where(b => b.Label != "Opening").ToList()));
        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;
        var asked = session.UseTemplate((await session.ChooseTemplateAsync(id))!)!;
        Assert.Equal(["Headline"], asked.Update.Replaced);
        Assert.Empty(asked.Update.Dropped);

        ((ParagraphsBlockEditor)session.Editor.Blocks.Single(b => b.Label == "Opening")).Text = "Thank you for a wonderful year.";
        var again = session.UseTemplate(asked, confirmed: true);

        Assert.NotNull(again);
        Assert.Equal(["Opening"], again.Update.Dropped);
        Assert.Equal(["Headline"], again.Update.Replaced);
        Assert.Equal(first.SavedAt, session.Editor.Draft.TemplateVersion);
        Assert.Null(session.UseTemplate(again, confirmed: true));
        Assert.NotEqual(first.SavedAt, session.Editor.Draft.TemplateVersion);
        Assert.DoesNotContain(session.Editor.Draft.Blocks, b => b.Label == "Opening");
    }

    // The approval is of the email: attaching a template that changes no block keeps it, as her label
    // does, and a template that changes a block takes it away, as any edit does.
    [Fact]
    public async Task Attaching_a_template_that_changes_no_block_keeps_the_approval_and_a_change_drops_it()
    {
        var campaign = await Store.ApproveAsync(await SaveDraft(Guid.NewGuid(), DraftFixtures.Finished()), "Priya");
        var same = Guid.NewGuid();
        await SaveTemplate(same, Membership);
        var revised = Guid.NewGuid();
        await SaveTemplate(revised, Revised("Membership, revised"));
        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;
        Assert.NotNull(session.CurrentApproval);

        Assert.Null(session.UseTemplate((await session.ChooseTemplateAsync(same))!));

        Assert.True(session.HasUnsavedChanges);
        Assert.Equal("Priya", session.CurrentApproval?.By);
        Assert.Equal(DraftSession.ContentOf(DraftFixtures.Finished()), DraftSession.ContentOf(session.Editor.Draft));

        var asked = session.UseTemplate((await session.ChooseTemplateAsync(revised))!)!;
        Assert.Null(session.UseTemplate(asked, confirmed: true));
        Assert.Null(session.CurrentApproval);
    }
}
