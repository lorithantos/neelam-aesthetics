using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Phase 2 of concurrent edits (2026-10-11): undo from a tab that has not seen the newest version. Undo
/// used to take back the page's own version even with a newer one above it: that middle version could
/// not be restored, and the sweep deleted it for good. Now undo acts only on the version the page holds
/// while storage still has it as the newest, read at the click, and refuses otherwise, undoing nothing.
/// Two sessions over one store are two tabs.
/// </summary>
public class StaleUndoTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private readonly InMemoryContainers _containers = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly CampaignSignals _signals = new();
    private TestRecords? _records;

    private TestRecords Records => _records ??= new(_clock);

    private InMemoryBlobBackend Container => _containers.For(TestRecords.DefaultClient.Value);

    private CampaignStore Store =>
        new(Container, _clock, Grace, Records.Approvals, Records.Recorder.For(TestRecords.DefaultClient, Actor.Demo), _signals);

    private async Task<(DraftSession A, DraftSession B)> TwoCampaignTabs(int saves = 1)
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < saves; i++)
        {
            await Store.SaveDraftAsync(id, $"Save {i + 1}", DraftFixtures.Finished());
            _clock.Now += TimeSpan.FromMinutes(1);
        }
        return ((await DraftSession.OpenAsync(Store, id))!, (await DraftSession.OpenAsync(Store, id))!);
    }

    // Nothing is marked: every save of the campaign is in use, and nothing is offered back.
    private async Task NothingUndone(Guid id, DocumentKind kind, int saves)
    {
        Assert.Equal(saves, (await Store.HistoryAsync(kind, id)).Count);
        Assert.Null(await Store.RestorableAsync(kind, id));
        Assert.All(Container.Blobs.Values, b => Assert.False(b.Metadata.ContainsKey("undone")));
    }

    [Fact]
    public async Task A_campaign_tab_that_has_not_seen_the_newest_save_cannot_undo_and_undoes_nothing()
    {
        var (a, b) = await TwoCampaignTabs();
        b.Editor.Subject.Text = "Theirs";
        await b.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);

        var refused = await Assert.ThrowsAsync<CampaignChangedException>(() => a.UndoLastSaveAsync());

        Assert.Equal(CampaignChange.SavedSince, refused.Change);
        Assert.Equal("Not undone: the campaign was saved again since you opened it. Undo takes back only the newest version, " +
                     "so load the latest above first.", refused.Message);
        await NothingUndone(a.Id!.Value, DocumentKind.Draft, 2);
        Assert.DoesNotContain(Records.Activity.Events, e => e.Action == ActivityAction.Undone);
    }

    // The case that lost a version for good: A holds v2, B has saved v3 on top. A's undo would have
    // marked v2, which Restore never offers (it is below the newest), and the sweep would delete it.
    [Fact]
    public async Task Undoing_a_middle_version_is_impossible_and_the_sweep_finds_nothing_to_delete()
    {
        var (a, b) = await TwoCampaignTabs(saves: 2);
        b.Editor.Subject.Text = "Third";
        await b.SaveAsync();

        await Assert.ThrowsAsync<CampaignChangedException>(() => a.UndoLastSaveAsync());
        _clock.Now += Grace + TimeSpan.FromMinutes(1);

        Assert.Equal(0, await Store.SweepAsync());
        await NothingUndone(a.Id!.Value, DocumentKind.Draft, 3);
    }

    // The tab that saw the other's undo: its own version is no longer in use, so its undo is refused too.
    [Fact]
    public async Task A_tab_whose_version_was_undone_elsewhere_cannot_undo_the_one_before()
    {
        var (a, b) = await TwoCampaignTabs(saves: 2);
        await b.UndoLastSaveAsync();

        var refused = await Assert.ThrowsAsync<CampaignChangedException>(() => a.UndoLastSaveAsync());

        Assert.Equal(CampaignChange.Undone, refused.Change);
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, a.Id!.Value));
    }

    // A save landing between the click's read and the mark: the mark is taken back, so the version is
    // not left undone in the middle, and the undo is refused.
    [Fact]
    public async Task A_save_landing_between_the_read_and_the_mark_takes_the_mark_back()
    {
        var (a, _) = await TwoCampaignTabs(saves: 2);
        var id = a.Id!.Value;
        var landed = false;
        Container.BeforeSetMetadata = async _ =>
        {
            if (landed) return;
            landed = true;
            _clock.Now += TimeSpan.FromSeconds(1);
            await Store.SaveDraftAsync(id, "Landed meanwhile", DraftFixtures.Finished());
        };

        var refused = await Assert.ThrowsAsync<CampaignChangedException>(() => a.UndoLastSaveAsync());

        Container.BeforeSetMetadata = null;
        Assert.Equal(CampaignChange.SavedSince, refused.Change);
        await NothingUndone(id, DocumentKind.Draft, 3);
        Assert.Equal("Landed meanwhile", (await Store.NewestAsync(DocumentKind.Draft, id))!.Title);
    }

    [Fact]
    public async Task The_newest_version_is_still_undone_and_restored_as_before()
    {
        var (a, _) = await TwoCampaignTabs(saves: 2);

        Assert.True(await a.UndoLastSaveAsync());

        Assert.Equal("Save 1", a.Latest!.Title);
        Assert.Equal("Save 2", a.Restorable!.Title);
        Assert.True(await a.RestoreAsync());
        Assert.Equal("Save 2", a.Latest!.Title);
    }

    // ---- Templates, the same rule.

    private async Task<(TemplateSession A, TemplateSession B)> TwoTemplateTabs(int saves = 1)
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < saves; i++)
        {
            await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
            _clock.Now += TimeSpan.FromMinutes(1);
        }
        return ((await TemplateSession.OpenAsync(Store, id, Catalogs.Shipped))!, (await TemplateSession.OpenAsync(Store, id, Catalogs.Shipped))!);
    }

    [Fact]
    public async Task A_template_tab_that_has_not_seen_the_newest_save_cannot_undo_a_middle_version()
    {
        var (a, b) = await TwoTemplateTabs(saves: 2);
        b.Editor.Name = "Theirs";
        await b.SaveAsync();

        var refused = await Assert.ThrowsAsync<CampaignChangedException>(() => a.UndoLastSaveAsync());

        Assert.Equal(CampaignChange.SavedSince, refused.Change);
        Assert.Equal("Not undone: the template was saved again since you opened it. Undo takes back only the newest version, " +
                     "so load the latest above first.", refused.Message);
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        Assert.Equal(0, await Store.SweepAsync());
        await NothingUndone(a.Id!.Value, DocumentKind.Template, 3);
        // The newest, on the tab that saved it, still undoes as before.
        Assert.True(await b.UndoLastSaveAsync());
    }

    [Fact]
    public async Task A_template_tab_tells_what_changed_since_it_opened()
    {
        var (a, b) = await TwoTemplateTabs(saves: 2);
        Assert.Equal(CampaignChange.None, a.ChangeSince(await a.NewestAsync()));
        Assert.False(a.HasUnsavedChanges);

        b.Editor.Name = "Theirs";
        await b.SaveAsync();
        Assert.Equal(CampaignChange.SavedSince, a.ChangeSince(await a.NewestAsync()));
        Assert.Equal(CampaignChange.None, b.ChangeSince(await b.NewestAsync()));

        await b.UndoLastSaveAsync();
        await b.UndoLastSaveAsync();
        Assert.Equal(CampaignChange.Undone, a.ChangeSince(await a.NewestAsync()));
        await b.UndoLastSaveAsync();
        Assert.Equal(CampaignChange.Deleted, a.ChangeSince(await a.NewestAsync()));

        a.Editor.Name = "Edited here";
        Assert.True(a.HasUnsavedChanges);
    }

    [Fact]
    public async Task Each_template_change_is_signalled_to_the_other_tab_and_never_back_to_its_own()
    {
        var (a, b) = await TwoTemplateTabs();
        var toA = 0;
        var toB = 0;
        using var listenA = _signals.Listen(a.Client, a.Subject!, a, () => Interlocked.Increment(ref toA));
        using var listenB = _signals.Listen(b.Client, b.Subject!, b, () => Interlocked.Increment(ref toB));

        b.Editor.Name = "Second";
        await b.SaveAsync();
        await b.UndoLastSaveAsync();
        await b.RestoreAsync();
        await Assert.ThrowsAsync<CampaignChangedException>(() => a.UndoLastSaveAsync());

        Assert.Equal(3, toA);
        Assert.Equal(0, toB);
        Assert.Equal(WatchSubject.Template(a.Id!.Value), a.Subject);
    }

    [Theory]
    [InlineData(CampaignChange.SavedSince, "The template was saved again since you opened it, in another tab or by someone else.")]
    [InlineData(CampaignChange.Undone, "The version on this page was undone since you opened it, in another tab or by someone else.")]
    [InlineData(CampaignChange.Deleted, "The template was deleted since you opened it, in another tab or by someone else. Load the latest to restore it.")]
    public void The_template_notice_says_what_changed(CampaignChange change, string notice) =>
        Assert.Equal(notice, TemplateSession.ChangedNotice(change));

    // ---- Her look: undo took back whatever was newest, even a look this page never showed.

    private ClientStores Stores => new(_containers.For, _clock, Grace, Records.Approvals, Records.Recorder, _signals);

    private static readonly ClientName Salon = TestRecords.DefaultClient;
    private static readonly ClientLook Green = new(accentColour: "#225e3e");

    private async Task<DocumentVersion> LookSaved(ClientLook look)
    {
        _clock.Now += TimeSpan.FromMinutes(1);
        return await Stores.SaveLookAsync(Salon, look, Actor.Demo);
    }

    [Fact]
    public async Task A_look_tab_that_has_not_seen_the_newest_look_cannot_undo_it_or_the_one_it_shows()
    {
        await LookSaved(Green);
        var a = await Stores.HoldLookAsync(Salon);
        var theirs = await LookSaved(ClientLookTests.Blush);

        var refused = await Assert.ThrowsAsync<CampaignChangedException>(() => Stores.UndoLastLookSaveAsync(a, Actor.Demo));

        Assert.Equal(CampaignChange.SavedSince, refused.Change);
        Assert.Equal("Not undone: your look was changed since you opened this page. Undo takes back only the newest version, " +
                     "so load the latest above first.", refused.Message);
        Assert.Equal(ClientLookTests.Blush, await Stores.Look(Salon).CurrentAsync());
        Assert.Equal(2, (await Stores.Look(Salon).HistoryAsync()).Count);
        Assert.Null((await Stores.LookHistoryAsync(Salon)).Restorable);
        Assert.Equal(theirs, await a.NewestAsync());
    }

    // The page showed no look of hers (the standard), and one was saved since: nothing to undo of its own.
    [Fact]
    public async Task A_look_tab_that_showed_none_cannot_undo_one_saved_since()
    {
        var a = await Stores.HoldLookAsync(Salon);
        await LookSaved(ClientLookTests.Blush);

        await Assert.ThrowsAsync<CampaignChangedException>(() => Stores.UndoLastLookSaveAsync(a, Actor.Demo));

        Assert.Equal(ClientLookTests.Blush, await Stores.Look(Salon).CurrentAsync());
    }

    [Fact]
    public async Task The_look_it_shows_as_newest_is_undone_as_before_and_the_other_tab_is_told()
    {
        await LookSaved(Green);
        var mine = await LookSaved(ClientLookTests.Blush);
        var a = await Stores.HoldLookAsync(Salon);
        var b = await Stores.HoldLookAsync(Salon);
        var told = 0;
        using var listening = _signals.Listen(Salon, WatchSubject.Look, b, () => Interlocked.Increment(ref told));

        var undone = await Stores.UndoLastLookSaveAsync(a, Actor.Demo);

        Assert.Equal(mine.BlobName, undone!.BlobName);
        Assert.Equal(Green, await Stores.Look(Salon).CurrentAsync());
        Assert.Equal(CampaignChange.None, a.ChangeSince(await a.NewestAsync()));
        Assert.Equal(CampaignChange.Undone, b.ChangeSince(await b.NewestAsync()));
        Assert.Equal(1, told);
    }

    [Theory]
    [InlineData(WatchSubject.Look, CampaignChange.SavedSince, "Your look was changed since you opened this page, in another tab or by someone else.")]
    [InlineData(WatchSubject.Look, CampaignChange.Undone, "The look on this page was undone since you opened it, in another tab or by someone else.")]
    [InlineData(WatchSubject.Baseline, CampaignChange.SavedSince, "The parts every template should have were changed since you opened this page, in another tab or by someone else.")]
    [InlineData(WatchSubject.Ladders, CampaignChange.Undone, "Your tier name ladders were changed since you opened this page, in another tab or by someone else.")]
    public void The_document_notice_says_what_changed(string subject, CampaignChange change, string notice) =>
        Assert.Equal(notice, HeldDocument.ChangedNotice(subject, change));
}
