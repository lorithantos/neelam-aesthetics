using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class DraftSessionTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private CampaignStore Store => new(_container, _clock, Grace);

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

    // Undo hides the newest at once: the version before comes back, and the undone one shows in no
    // list, history or reopening. Nothing is deleted until the sweep, which then leaves no trace.
    [Fact]
    public async Task Undoing_the_last_save_hides_it_and_puts_the_one_before_back()
    {
        var session = await Started();
        session.Editor.Subject.Text = "Before";
        await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Subject.Text = "After";
        var bad = await session.SaveAsync();

        Assert.True(await session.UndoLastSaveAsync());

        Assert.Equal("Before", session.Editor.Subject.Text);
        Assert.Equal("Before", session.Latest!.Title);
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, session.Id!.Value));
        Assert.Equal(["Before"], (await DraftSession.ListAsync(Store)).Select(s => s.Title));
        var reopened = await DraftSession.OpenAsync(Store, session.Id.Value);
        Assert.Equal("Before", reopened!.Editor.Subject.Text);
        // Leaving and coming back deletes nothing, and the undo is still offered back.
        Assert.Contains(bad.BlobName, _container.Blobs.Keys);
        Assert.Equal(bad.BlobName, reopened.Restorable!.BlobName);

        _clock.Now += Grace;
        await Store.SweepAsync();
        Assert.DoesNotContain(bad.BlobName, _container.Blobs.Keys);
    }

    [Fact]
    public async Task Restore_within_the_grace_period_brings_the_undone_save_back()
    {
        var session = await Started();
        session.Editor.Subject.Text = "Before";
        await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Subject.Text = "After";
        var after = await session.SaveAsync();
        await session.UndoLastSaveAsync();

        Assert.Equal(_clock.Now + Grace, session.RestorableUntil);
        // She left, and came back with a minute to spare.
        _clock.Now += Grace - TimeSpan.FromMinutes(1);
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.True(await reopened.RestoreAsync());

        Assert.Equal("After", reopened.Editor.Subject.Text);
        Assert.Equal(after, reopened.Latest);
        Assert.Null(reopened.Restorable);
        Assert.Equal(["After"], (await DraftSession.ListAsync(Store)).Select(s => s.Title));
        _clock.Now += Grace;
        await Store.SweepAsync();
        Assert.Equal(2, _container.Blobs.Count(b => b.Key.StartsWith("drafts/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Once_the_grace_period_has_passed_restore_is_not_offered()
    {
        var session = await Started();
        session.Editor.Subject.Text = "Before";
        await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Subject.Text = "After";
        await session.SaveAsync();
        await session.UndoLastSaveAsync();
        Assert.NotNull(session.Restorable);

        _clock.Now += Grace;

        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.Null(reopened.Restorable);
        Assert.Null(reopened.RestorableUntil);
        // The page still holding the offer is refused, and the offer goes.
        Assert.False(await session.RestoreAsync());
        Assert.Null(session.Restorable);
        Assert.Equal("Before", session.Editor.Subject.Text);
    }

    [Fact]
    public async Task Undoing_the_only_save_leaves_no_campaign_until_it_is_restored()
    {
        var session = await Started();
        var only = await session.SaveAsync();

        Assert.False(await session.UndoLastSaveAsync());
        Assert.Null(session.Latest);
        Assert.Empty(await DraftSession.ListAsync(Store));
        Assert.Null(await DraftSession.OpenAsync(Store, only.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UndoLastSaveAsync());

        var undone = await DraftSession.RestorableAsync(Store, only.Id);
        Assert.Equal(only.BlobName, undone!.BlobName);
        var restored = await DraftSession.RestoreAsync(Store, undone);
        Assert.Equal(only, restored!.Latest);
        Assert.Single(await DraftSession.ListAsync(Store));

        // Undone again and left past the period: nothing to restore, and the sweep leaves nothing.
        await restored.UndoLastSaveAsync();
        _clock.Now += Grace;
        Assert.Null(await DraftSession.RestorableAsync(Store, only.Id));
        Assert.Null(await DraftSession.RestoreAsync(Store, undone));
        await Store.SweepAsync();
        Assert.DoesNotContain(_container.Blobs.Keys, k => k.StartsWith("drafts/", StringComparison.Ordinal));
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
