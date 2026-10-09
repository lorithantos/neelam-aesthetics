using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class DraftSessionTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private CampaignStore Store => new(_container, _clock);

    private async Task<Guid> SavedTemplate()
    {
        var id = Guid.NewGuid();
        await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        _clock.Now += TimeSpan.FromMinutes(1);
        return id;
    }

    private async Task<DraftSession> Started() => (await DraftSession.StartAsync(Store, await SavedTemplate()))!;

    [Fact]
    public async Task Starting_uses_the_template_s_newest_version_and_saves_nothing()
    {
        var id = await SavedTemplate();
        await Store.SaveTemplateAsync(id, new CampaignTemplate("Membership, revised", DraftFixtures.Membership.Blocks));

        var session = await DraftSession.StartAsync(Store, id);

        Assert.Null(session!.Id);
        Assert.Null(session.Latest);
        Assert.Equal("Membership, revised", session.Editor.TemplateName);
        Assert.Empty(await DraftSession.ListAsync(Store));
        Assert.Null(await DraftSession.StartAsync(Store, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_new_campaign_gets_its_id_on_first_save_and_each_save_is_a_new_version()
    {
        var session = await Started();

        var first = await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Subject.Text = "WE’RE TURNING ONE!";
        var second = await session.SaveAsync();

        Assert.Equal(first.Id, session.Id);
        Assert.Equal(second, session.Latest);
        // Unfinished is fine to save; the title is the subject once there is one.
        Assert.Equal("Untitled campaign from Membership announcement", first.Title);
        Assert.Equal("WE’RE TURNING ONE!", second.Title);
        Assert.Equal([second.SavedAt, first.SavedAt],
            (await Store.HistoryAsync(DocumentKind.Draft, session.Id!.Value)).Select(s => s.SavedAt));
    }

    // Undo is delete-the-newest: the version before comes back, and the deleted one leaves nothing.
    [Fact]
    public async Task Undoing_the_last_save_puts_the_one_before_back_and_keeps_no_trace()
    {
        var session = await Started();
        session.Editor.Subject.Text = "Before";
        await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Subject.Text = "After";
        var bad = await session.SaveAsync();

        Assert.True(await session.UndoLastSaveAsync());

        Assert.Equal("Before", session.Editor.Subject.Text);
        Assert.DoesNotContain(bad.BlobName, _container.Blobs.Keys);
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, session.Id!.Value));
    }

    [Fact]
    public async Task Undoing_the_only_save_leaves_no_campaign()
    {
        var session = await Started();
        await session.SaveAsync();

        Assert.False(await session.UndoLastSaveAsync());
        Assert.Null(session.Latest);
        Assert.Empty(await DraftSession.ListAsync(Store));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UndoLastSaveAsync());
    }

    // A copy nobody reviewed is still unreviewed after it is saved and opened again.
    [Fact]
    public async Task Opening_reads_the_newest_version_with_every_origin_and_an_unknown_campaign_is_null()
    {
        var id = Guid.NewGuid();
        await Store.SaveDraftAsync(id, "Older", DraftFixtures.StartAndFillText());
        _clock.Now += TimeSpan.FromMinutes(1);
        await Store.SaveDraftAsync(id, "Second send, replayed", DraftFixtures.SecondSendReplayed());

        var session = await DraftSession.OpenAsync(Store, id);

        Assert.Equal(id, session!.Id);
        Assert.Equal("Second send, replayed", session.Latest!.Title);
        var tiers = session.Editor.Blocks.OfType<OfferBlockEditor>().Single().Offer.Tiers;
        Assert.Equal(["Gold Member", ""], tiers.Select(t => t.Name.Text));
        Assert.Equal([false, false, true], tiers[1].Benefits.Select(b => b.IsUnreviewed));
        Assert.Equal("Tier 1", tiers[1].Benefits[2].CopiedFrom);
        Assert.Null(await DraftSession.OpenAsync(Store, Guid.NewGuid()));
    }

    [Fact]
    public async Task The_list_is_the_newest_version_of_each_campaign()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await Store.SaveDraftAsync(a, "A, first", DraftFixtures.StartAndFillText());
        _clock.Now += TimeSpan.FromMinutes(1);
        await Store.SaveDraftAsync(b, "B", DraftFixtures.StartAndFillText());
        _clock.Now += TimeSpan.FromMinutes(1);
        await Store.SaveDraftAsync(a, "A, second", DraftFixtures.StartAndFillText());

        var list = await DraftSession.ListAsync(Store);

        Assert.Equal([(a, "A, second"), (b, "B")], list.Select(s => (s.Id, s.Title)));
    }
}
