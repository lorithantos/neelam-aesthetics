using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class DraftSessionTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_container, Grace);

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

    // Undo with no version before it deletes the campaign, and the question says so, with the grace
    // period as configured; with one before it, it says that one comes back.
    [Fact]
    public async Task The_undo_question_says_when_it_deletes_the_campaign()
    {
        var session = await Started();
        Assert.False(session.UndoDeletesTheCampaign);
        await session.SaveAsync();

        Assert.True(session.UndoDeletesTheCampaign);
        Assert.Equal("This deletes the campaign. You can restore it for a day.", session.UndoQuestion);

        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Subject.Text = "Second";
        await session.SaveAsync();
        Assert.False(session.UndoDeletesTheCampaign);
        Assert.StartsWith("Undo the last save? The version before it comes back", session.UndoQuestion);
        Assert.EndsWith("can be restored for a day, then it is deleted for good.", session.UndoQuestion);

        // Opened again, it still knows how many saves there are.
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.False(reopened.UndoDeletesTheCampaign);
        Assert.True(await reopened.UndoLastSaveAsync());
        Assert.True(reopened.UndoDeletesTheCampaign);

        // The period is the store's, in words.
        var hours = (await DraftSession.OpenAsync(Records.Campaigns(_container, TimeSpan.FromHours(12)), session.Id.Value))!;
        Assert.Equal("This deletes the campaign. You can restore it for 12 hours.", hours.UndoQuestion);
    }

    [Theory]
    [InlineData("1.00:00:00", "a day")]
    [InlineData("2.00:00:00", "2 days")]
    [InlineData("01:00:00", "an hour")]
    [InlineData("1.12:00:00", "36 hours")]
    [InlineData("00:30:00", "30 minutes")]
    [InlineData("00:01:00", "a minute")]
    public void A_grace_period_reads_in_its_largest_whole_unit(string period, string words) =>
        Assert.Equal(words, TimeWords.Period(TimeSpan.Parse(period, System.Globalization.CultureInfo.InvariantCulture)));

    // Deleted campaigns are listed, for her to restore from the list, until the sweep may take them.
    [Fact]
    public async Task Recently_deleted_lists_each_campaign_with_no_save_in_use_while_it_can_be_restored()
    {
        var kept = await Started();
        await kept.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        // A campaign with two saves, its newest undone: still in her list, so not deleted.
        kept.Editor.Subject.Text = "Kept";
        await kept.SaveAsync();
        await kept.UndoLastSaveAsync();

        var older = await Started();
        older.Editor.Subject.Text = "Deleted first";
        var olderSave = await older.SaveAsync();
        Assert.False(await older.UndoLastSaveAsync());
        _clock.Now += TimeSpan.FromHours(1);

        var newer = await Started();
        newer.Editor.Subject.Text = "Deleted second";
        await newer.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        newer.Editor.Subject.Text = "Deleted second, again";
        await newer.SaveAsync();
        await newer.UndoLastSaveAsync();
        Assert.False(await newer.UndoLastSaveAsync());

        // Deleted and then saved again from the page still open: in use, so not deleted, though its
        // first save is still restorable.
        var resaved = await Started();
        resaved.Editor.Subject.Text = "Saved again";
        await resaved.SaveAsync();
        Assert.False(await resaved.UndoLastSaveAsync());
        _clock.Now += TimeSpan.FromMinutes(1);
        await resaved.SaveAsync();

        var deleted = await DraftSession.RecentlyDeletedAsync(Store);

        // The save Restore brings back: for one undone save by save, the last one undone.
        Assert.Equal([(newer.Id!.Value, "Deleted second"), (olderSave.Id, "Deleted first")], deleted.Select(s => (s.Id, s.Title)));
        var restored = (await DraftSession.RestoreAsync(Store, deleted[1]))!;
        Assert.Equal("Deleted first", restored.Latest!.Title);
        Assert.Equal([newer.Id.Value], (await DraftSession.RecentlyDeletedAsync(Store)).Select(s => s.Id));

        // Past its grace period, it is the sweep's, and no longer offered.
        _clock.Now += Grace;
        Assert.Empty(await DraftSession.RecentlyDeletedAsync(Store));
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
        Assert.Equal(["Platinum Member", "Platinum Member"], tiers.Select(t => t.Name.Text));
        Assert.Equal([false, false, true, false], tiers[1].Benefits.Select(b => b.IsUnreviewed));
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
