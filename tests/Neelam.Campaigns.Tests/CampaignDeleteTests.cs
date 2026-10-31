using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Priya, 2026-10-31: "We need a delete for campaigns - it should be able to be undone, but it will allow
/// test campaigns". Deleting a campaign is Undo applied to every version in use at one moment: each is
/// marked undone in its own metadata with the same time, so the campaign leaves her list at once, waits
/// under Recently deleted for the grace period, comes back whole with Restore, and is then swept like any
/// undone save, its proofreads with it, leaving no record of what it held.
/// </summary>
public class CampaignDeleteTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("d3e1c0de-0000-4000-8000-000000000001");
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private readonly InMemoryBlobBackend _blobs = new();
    private readonly ManualClock _clock = new(Start);
    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_blobs, Grace);

    // Three versions a minute apart, the newest approved and the middle one proofread.
    private async Task<IReadOnlyList<SaveRef>> ThreeVersionsAsync(Guid? id = null)
    {
        var saves = new List<SaveRef>();
        for (var i = 0; i < 3; i++)
        {
            _clock.Now = Start.AddMinutes(i);
            saves.Add(await Store.SaveDraftAsync(id ?? Id, $"Version {i + 1}", DraftFixtures.Finished()));
        }
        await Store.SaveProofreadAsync(saves[1], new ProofreadRecord(_clock.Now, [], []));
        await Store.ApproveAsync(saves[2], "Priya");
        return saves;
    }

    private string Prefix(Guid id) => $"{id:N}/";

    [Fact]
    public async Task Deleting_takes_every_version_off_her_list_at_once_and_restore_brings_them_all_back()
    {
        var saves = await ThreeVersionsAsync();
        _clock.Now = Start.AddMinutes(5);
        // Time moves on while the marks are written; they all still carry the moment of the delete.
        _blobs.BeforeSetMetadata = _ =>
        {
            _clock.Now += TimeSpan.FromSeconds(1);
            return Task.CompletedTask;
        };

        await Store.DeleteAsync(DocumentKind.Draft, Id);
        _blobs.BeforeSetMetadata = null;
        _clock.Now = Start.AddMinutes(5);

        Assert.Empty(await Store.HistoryAsync(DocumentKind.Draft, Id));
        Assert.DoesNotContain(await Store.LatestAsync(DocumentKind.Draft), s => s.Id == Id);
        // Each version marked in its own metadata, all with the one time, and nothing else written.
        Assert.All(saves, s => Assert.Equal("2026-10-31T18:05:00.0000000+00:00", _blobs.Blobs[s.BlobName].Metadata["undone"]));
        Assert.Equal(4, _blobs.Blobs.Count);
        var deleted = Assert.Single(await Store.RecentlyDeletedAsync(DocumentKind.Draft));
        Assert.Equal((Id, Start.AddMinutes(5)), (deleted.Id, deleted.UndoneAt!.Value));
        Assert.Equal(Start.AddMinutes(5) + Grace, Store.RestorableUntil(deleted));
        Assert.Equal(deleted, await Store.RestorableAsync(DocumentKind.Draft, Id));
        // The approval is withdrawn, as Undo withdraws it; the row stays as the record.
        Assert.Equal(Start.AddMinutes(5), Assert.Single(Records.Approvals.Rows).WithdrawnAt);

        _clock.Now = Start.AddMinutes(5) + Grace - TimeSpan.FromTicks(1);
        Assert.True(await Store.RestoreAsync(deleted));

        Assert.Equal(saves.Reverse().Select(s => s.BlobName), (await Store.HistoryAsync(DocumentKind.Draft, Id)).Select(s => s.BlobName));
        Assert.All(saves, s => Assert.DoesNotContain("undone", _blobs.Blobs[s.BlobName].Metadata.Keys));
        Assert.Empty(await Store.RecentlyDeletedAsync(DocumentKind.Draft));
        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, Id));
        // Back unapproved, as an undone save comes back.
        Assert.Null((await Store.NewestAsync(DocumentKind.Draft, Id))!.Approval);
        Assert.NotNull(await Store.ProofreadOfAsync(saves[1]));
    }

    [Fact]
    public async Task After_the_grace_period_the_sweep_takes_the_whole_campaign_and_its_proofreads_and_leaves_no_content()
    {
        var saves = await ThreeVersionsAsync();
        var other = Guid.NewGuid();
        var kept = await Store.SaveDraftAsync(other, "Kept", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(5);
        await Store.DeleteAsync(DocumentKind.Draft, Id);

        _clock.Now = Start.AddMinutes(5) + Grace - TimeSpan.FromTicks(1);
        Assert.Equal(0, await Store.SweepAsync());
        Assert.Equal(5, _blobs.Blobs.Count);

        _clock.Now = Start.AddMinutes(5) + Grace;
        Assert.Equal(3, await Store.SweepAsync());

        Assert.Equal([kept.BlobName], _blobs.Blobs.Keys.ToArray());
        Assert.DoesNotContain(_blobs.Blobs.Keys, k => k.Contains(Prefix(Id)));
        Assert.Empty(await Store.RecentlyDeletedAsync(DocumentKind.Draft));
        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, Id));
        Assert.False(await Store.RestoreAsync(saves[0]));
        // The trail says which versions were deleted and when, by ids only.
        var events = Records.Activity.Events.Where(e => e.EntityId == Id.ToString("N")).ToList();
        Assert.Equal(3, events.Count(e => e.Action == ActivityAction.Undone));
        Assert.Equal(3, events.Count(e => e.Action == ActivityAction.DeletedBySweep));
        Assert.Equal(saves.Select(s => SaveStamp.Of(s.SavedAt)).Order(),
            events.Where(e => e.Action == ActivityAction.DeletedBySweep).Select(e => e.SaveStamp!).Order());
    }

    // A version undone before the delete stays undone: Restore brings back what the delete took, and the
    // earlier undo is then offered back as any undo is.
    [Fact]
    public async Task Restore_brings_back_what_the_delete_took_and_an_earlier_undo_stays_undone()
    {
        var saves = await ThreeVersionsAsync();
        _clock.Now = Start.AddMinutes(3);
        var undoneFirst = await Store.MarkUndoneAsync(saves[2]);
        _clock.Now = Start.AddMinutes(5);
        await Store.DeleteAsync(DocumentKind.Draft, Id);

        var deleted = Assert.Single(await Store.RecentlyDeletedAsync(DocumentKind.Draft));
        Assert.Equal((saves[0].BlobName, Start.AddMinutes(5)), (deleted.BlobName, deleted.UndoneAt!.Value));

        Assert.True(await Store.RestoreAsync(deleted));

        Assert.Equal([saves[1].BlobName, saves[0].BlobName], (await Store.HistoryAsync(DocumentKind.Draft, Id)).Select(s => s.BlobName));
        Assert.Equal(undoneFirst.BlobName, (await Store.RestorableAsync(DocumentKind.Draft, Id))!.BlobName);
    }

    // Its only save undone, then saved again from a tab still open on it, then deleted: what Restore
    // offers is what the delete took, the newer save, not the older one undone before it.
    [Fact]
    public async Task After_a_delete_restore_offers_what_the_delete_took_not_an_older_undo_below_it()
    {
        _clock.Now = Start;
        var first = await Store.SaveDraftAsync(Id, "First", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(1);
        await Store.MarkUndoneAsync(first);
        _clock.Now = Start.AddMinutes(2);
        var again = await Store.SaveDraftAsync(Id, "Saved again", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(3);
        await Store.DeleteAsync(DocumentKind.Draft, Id);

        Assert.Equal(again.BlobName, (await Store.RestorableAsync(DocumentKind.Draft, Id))!.BlobName);
        var deleted = Assert.Single(await Store.RecentlyDeletedAsync(DocumentKind.Draft));
        Assert.Equal(again.BlobName, deleted.BlobName);

        Assert.True(await Store.RestoreAsync(deleted));
        Assert.Equal(again.BlobName, Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, Id)).BlobName);
        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, Id));
    }

    // A version saved in another tab while the marks go on is taken too, so nothing of the campaign is
    // left in use, and it comes back with the rest.
    [Fact]
    public async Task A_version_saved_while_the_campaign_is_being_deleted_goes_with_it()
    {
        await ThreeVersionsAsync();
        _clock.Now = Start.AddMinutes(5);
        SaveRef? late = null;
        _blobs.BeforeSetMetadata = async _ =>
        {
            _blobs.BeforeSetMetadata = null;
            late = await Store.SaveDraftAsync(Id, "Saved meanwhile", DraftFixtures.Finished());
        };

        await Store.DeleteAsync(DocumentKind.Draft, Id);

        Assert.NotNull(late);
        Assert.Empty(await Store.HistoryAsync(DocumentKind.Draft, Id));
        Assert.True(await Store.RestoreAsync((await Store.RestorableAsync(DocumentKind.Draft, Id))!));
        Assert.Equal(4, (await Store.HistoryAsync(DocumentKind.Draft, Id)).Count);
    }

    // ---- The campaign page's session

    [Fact]
    public async Task The_question_says_what_goes_for_how_long_and_that_an_approval_is_withdrawn()
    {
        _clock.Now = Start;
        var one = Guid.NewGuid();
        await Store.SaveDraftAsync(one, "Test", DraftFixtures.Finished());
        Assert.Equal("Delete this campaign? It goes from your campaigns. You can restore it from Recently deleted for a day, " +
                     "then it is deleted for good.", (await DraftSession.OpenAsync(Store, one))!.DeleteQuestion);

        await ThreeVersionsAsync();
        Assert.Equal("Delete this campaign and all 3 of its versions? It goes from your campaigns. You can restore it from " +
                     "Recently deleted for a day, then it is deleted for good. Its approval is withdrawn: restored, it needs " +
                     "approving again.", (await DraftSession.OpenAsync(Store, Id))!.DeleteQuestion);

        // An earlier version's approval counts too: deleting withdraws it as well.
        _clock.Now = Start.AddMinutes(4);
        await Store.SaveDraftAsync(Id, "Version 4", DraftFixtures.Finished());
        Assert.EndsWith("Its approval is withdrawn: restored, it needs approving again.",
            (await DraftSession.OpenAsync(Store, Id))!.DeleteQuestion);

        var hours = Records.Campaigns(_blobs, TimeSpan.FromHours(12));
        Assert.Contains("for 12 hours,", (await DraftSession.OpenAsync(hours, one))!.DeleteQuestion);
    }

    [Fact]
    public async Task Deleting_from_the_page_leaves_it_restorable_and_restore_opens_the_newest_version()
    {
        var saves = await ThreeVersionsAsync();
        _clock.Now = Start.AddMinutes(5);
        var session = (await DraftSession.OpenAsync(Store, Id))!;

        await session.DeleteAsync();

        Assert.Null(session.Latest);
        Assert.Equal(saves[0].BlobName, session.Restorable!.BlobName);
        Assert.Equal(Start.AddMinutes(5) + Grace, session.RestorableUntil);
        Assert.DoesNotContain(await DraftSession.ListAsync(Store), s => s.Id == Id);
        Assert.Equal(Id, Assert.Single(await DraftSession.RecentlyDeletedAsync(Store)).Id);
        Assert.Null(await DraftSession.OpenAsync(Store, Id));

        var restored = (await DraftSession.RestoreAsync(Store, session.Restorable))!;

        Assert.Equal(saves[2].BlobName, restored.Latest!.BlobName);
        Assert.Equal(3, (await Store.HistoryAsync(DocumentKind.Draft, Id)).Count);
    }

    [Fact]
    public async Task Another_campaign_is_untouched()
    {
        await ThreeVersionsAsync();
        var other = Guid.NewGuid();
        var kept = await Store.SaveDraftAsync(other, "Kept", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(5);

        await Store.DeleteAsync(DocumentKind.Draft, Id);

        Assert.Equal(kept, Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, other)));
        Assert.DoesNotContain("undone", _blobs.Blobs[kept.BlobName].Metadata.Keys);
    }
}

/// <summary>Delete on the campaign page, held as its circuit holds it (<see cref="OpenCampaignPage"/>), read by named elements.</summary>
public class CampaignDeletePageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<Guid> SavedAsync(int versions)
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < versions; i++) await Store.SaveDraftAsync(id, $"Test campaign, version {i + 1}", DraftFixtures.Finished());
        return id;
    }

    // Any campaign can be deleted, however many times it was saved; Undo last save is offered only
    // while there is a version before the newest to go back to.
    [Fact]
    public async Task Delete_is_offered_whatever_the_number_of_versions()
    {
        await using var once = await OpenCampaignPage.OpenAsync(app, await SavedAsync(1), StalePages.Clock(app));
        var html = await once.HtmlAsync();
        Assert.Equal("Delete campaign", RenderedPage.Named(html, "delete-button"));
        Assert.Equal(0, RenderedPage.Count(html, "undo-button"));

        await using var twice = await OpenCampaignPage.OpenAsync(app, await SavedAsync(2), StalePages.Clock(app));
        html = await twice.HtmlAsync();
        Assert.Equal("Delete campaign", RenderedPage.Named(html, "delete-button"));
        Assert.Equal("Undo last save", RenderedPage.Named(html, "undo-button"));
    }

    [Fact]
    public async Task Delete_asks_first_and_keep_it_deletes_nothing()
    {
        var id = await SavedAsync(3);
        await using var page = await OpenCampaignPage.OpenAsync(app, id, StalePages.Clock(app));

        await page.ClickAsync("delete-button");

        var html = await page.HtmlAsync();
        Assert.Equal("Delete this campaign and all 3 of its versions? It goes from your campaigns. You can restore it from " +
                     "Recently deleted for a day, then it is deleted for good.", RenderedPage.Named(html, "delete-question"));
        Assert.Equal("Delete it", RenderedPage.Named(html, "delete-confirm"));
        Assert.Equal("Keep it", RenderedPage.Named(html, "delete-keep"));
        Assert.Equal(3, (await Store.HistoryAsync(DocumentKind.Draft, id)).Count);

        await page.ClickAsync("delete-keep");

        html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "delete-question"));
        Assert.Equal("Delete campaign", RenderedPage.Named(html, "delete-button"));
        Assert.Equal(3, (await Store.HistoryAsync(DocumentKind.Draft, id)).Count);
    }

    [Fact]
    public async Task Deleted_the_page_offers_restore_and_restore_brings_every_version_back()
    {
        var id = await SavedAsync(3);
        await using var page = await OpenCampaignPage.OpenAsync(app, id, StalePages.Clock(app));

        await page.ClickAsync("delete-button");
        await page.ClickAsync("delete-confirm");

        var html = await page.HtmlAsync();
        Assert.StartsWith("This campaign is deleted. You can restore it until", RenderedPage.Named(html, "deleted-note"));
        Assert.Equal("Restore", RenderedPage.Named(html, "restore-deleted"));
        Assert.Empty(await Store.HistoryAsync(DocumentKind.Draft, id));
        Assert.Equal(id, Assert.Single(await Store.RecentlyDeletedAsync(DocumentKind.Draft), s => s.Id == id).Id);

        await page.ClickAsync("restore-deleted");

        html = await page.HtmlAsync();
        Assert.Equal("The campaign is back and open.", RenderedPage.Named(html, "page-message"));
        Assert.Equal("Delete campaign", RenderedPage.Named(html, "delete-button"));
        Assert.Equal(3, (await Store.HistoryAsync(DocumentKind.Draft, id)).Count);
    }
}
