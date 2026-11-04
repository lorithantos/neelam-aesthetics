using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using CampaignPage = Neelam.Web.Components.Pages.CampaignEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The campaign page's working copy (owner, 2026-11-03), on the page held as its circuit holds it
/// (<see cref="OpenCampaignPage"/>), over the app's in-memory storage, with a clock whose timers move only when
/// told. A write the clock sets off is done once the page has answered after it (<see cref="Settled"/>): the
/// write runs on the page's circuit, ahead of anything asked of it later, and the in-memory store answers at once.
/// </summary>
public class WorkingCopyPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private static readonly LocalTime Times = LocalTime.For(LocalTime.DefaultZone);

    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private InMemoryBlobBackend Container => app.Containers.For(DemoApp.Client.Value);

    private WorkingCopyOptions Settings => app.Services.GetRequiredService<IOptions<WorkingCopyOptions>>().Value;

    private async Task<SaveRef> Saved(Guid? id = null, string subject = "Saved subject")
    {
        var save = await Store.SaveDraftAsync(id ?? Guid.NewGuid(), subject, WorkingCopyStoreTests.Edited(subject));
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    private ManualClock Clock() => new(app.Clock.Now);

    private Task<OpenCampaignPage> Open(Guid id, ManualClock clock, params object[] more) =>
        OpenCampaignPage.OpenAsync(app, id, [clock, .. more]);

    // The subject the working copy holds, or null when the campaign has none.
    private string? Held(Guid id) =>
        Container.Blobs.TryGetValue(WorkingCopies.Name(id), out var blob)
            ? CampaignJson.DeserializeDraft(blob.Blob.Content.ToString()).SubjectText()
            : null;

    private int WorkingBlobs => Container.Blobs.Keys.Count(k => k.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal));

    // Typed, and the write set for after it on the page's own clock.
    private static async Task Typed(OpenCampaignPage page, ManualClock clock, string subject, TimeSpan due)
    {
        await page.TypeAsync("subject", subject);
        await Eventually.Until(() => clock.WaitingFor(due) == 1, $"the working copy set for {due}", clock.Changed);
    }

    // Anything the clock set off on the page has run: the page answers only after it.
    private static Task Settled(OpenCampaignPage page) => page.HtmlAsync();

    private async Task<WorkingCopy> Written(Guid id, string subject, SaveRef basedOn)
    {
        await Store.WorkingCopies.WriteAsync(id, WorkingCopyStoreTests.Edited(subject), basedOn.SavedAt, BlobExpected.Anything);
        return (await Store.WorkingCopies.ReadAsync(id))!;
    }

    [Fact]
    public async Task It_is_written_a_few_seconds_after_she_stops_typing_and_not_before()
    {
        var save = await Saved();
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        var events = app.Activity.Events.Count;
        var replaces = Container.Replaces;

        await Typed(page, clock, "Mine, typed", Settings.Debounce);
        clock.Advance(Settings.Debounce - TimeSpan.FromSeconds(1));
        await Settled(page);
        Assert.Null(Held(save.Id));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Settled(page);
        Assert.Equal("Mine, typed", Held(save.Id));
        Assert.Equal(replaces + 1, Container.Replaces);
        var copy = (await Store.WorkingCopies.ReadAsync(save.Id))!;
        Assert.Equal(save.SavedAt, copy.BasedOn);
        // Not a save: no new version, nothing on the trail, and the page shows nothing of it.
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id));
        Assert.Equal(events, app.Activity.Events.Count);
        var html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "working-copy-notice"));
        Assert.Equal(0, RenderedPage.Count(html, "working-copy-other-tab"));
    }

    [Fact]
    public async Task Each_change_puts_it_off_again_but_never_past_a_minute_from_the_first()
    {
        var save = await Saved();
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        var step = Settings.Debounce - TimeSpan.FromSeconds(1);

        // A change every four seconds: each puts the write off, until a minute from the first.
        await Typed(page, clock, "Change 0", Settings.Debounce);
        var typed = TimeSpan.Zero;
        for (var i = 1; typed + step <= Settings.Cap - step; i++)
        {
            clock.Advance(step);
            typed += step;
            await Settled(page);
            Assert.Null(Held(save.Id));
            await page.TypeAsync("subject", $"Change {i}");
        }
        var left = Settings.Cap - typed;
        Assert.True(left < Settings.Debounce);
        await Eventually.Until(() => clock.WaitingFor(left) == 1, "the write set for the minute's end", clock.Changed);

        clock.Advance(left - TimeSpan.FromTicks(1));
        await Settled(page);
        Assert.Null(Held(save.Id));
        clock.Advance(TimeSpan.FromTicks(1));
        await Settled(page);
        Assert.StartsWith("Change ", Held(save.Id));
    }

    [Fact]
    public async Task Nothing_is_written_that_it_holds_already_or_that_is_the_newest_save()
    {
        var save = await Saved(subject: "Saved subject");
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        var replaces = Container.Replaces;

        // Changed and changed back: the newest save, so nothing to keep.
        await page.TypeAsync("subject", "For a moment");
        await Typed(page, clock, "Saved subject", Settings.Debounce);
        clock.Advance(Settings.Cap);
        await Settled(page);
        Assert.Equal(replaces, Container.Replaces);
        Assert.Null(Held(save.Id));

        // Written once, then changed and changed back to what it holds: not written again.
        await Typed(page, clock, "Kept", Settings.Debounce);
        clock.Advance(Settings.Debounce);
        await Settled(page);
        Assert.Equal(replaces + 1, Container.Replaces);
        await page.TypeAsync("subject", "Kept, nearly");
        await Typed(page, clock, "Kept", Settings.Debounce);
        clock.Advance(Settings.Cap);
        await Settled(page);
        Assert.Equal(replaces + 1, Container.Replaces);
    }

    [Fact]
    public async Task On_opening_her_unsaved_changes_are_brought_back_unapproved_with_a_notice()
    {
        var save = await Store.ApproveAsync(await Saved(), "Priya");
        await Store.WarningsSeenAtExportAsync(save, SeenAtExport.KeysOf(WorkingCopyStoreTests.Edited("Saved subject")));
        var copy = await Written(save.Id, "Unsaved, brought back", save);

        await using var page = await Open(save.Id, Clock());

        var html = await page.HtmlAsync();
        Assert.Equal("Unsaved, brought back", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(WorkingCopyWords.BroughtBack(Times.DateAndTime(copy.WrittenAt)), RenderedPage.Named(html, "working-copy-what"));
        Assert.Equal("Discard", RenderedPage.Named(html, "working-copy-discard"));
        // Unsaved content, as if she had typed it: not approved, nothing to export.
        Assert.Equal(0, RenderedPage.Count(html, "approved"));
        Assert.Equal(0, RenderedPage.Count(html, "export"));
        Assert.Equal(0, RenderedPage.Count(html, "assistant-download"));
    }

    [Fact]
    public async Task A_working_copy_that_is_the_newest_save_is_not_brought_back()
    {
        var save = await Saved(subject: "Saved subject");
        await Written(save.Id, "Saved subject", save);

        await using var page = await Open(save.Id, Clock());

        var html = await page.HtmlAsync();
        Assert.Equal("Saved subject", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(0, RenderedPage.Count(html, "working-copy-notice"));
    }

    [Fact]
    public async Task Discard_removes_it_and_opens_the_newest_save()
    {
        var save = await Saved(subject: "Saved subject");
        await Written(save.Id, "Unsaved", save);
        var events = app.Activity.Events.Count;
        await using var page = await Open(save.Id, Clock());

        await page.ClickAsync("working-copy-discard");

        var html = await page.HtmlAsync();
        Assert.Null(Held(save.Id));
        Assert.Equal("Saved subject", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(0, RenderedPage.Count(html, "working-copy-notice"));
        Assert.Equal(events, app.Activity.Events.Count);
    }

    [Fact]
    public async Task Discard_storage_cannot_take_keeps_the_changes_and_says_so()
    {
        var save = await Saved(subject: "Saved subject");
        await Written(save.Id, "Unsaved", save);
        await using var page = await Open(save.Id, Clock());

        Container.Failure = name => name.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal) ? StorageFailure.Busy() : null;
        try
        {
            await page.ClickAsync("working-copy-discard");
        }
        finally
        {
            Container.Failure = null;
        }

        var html = await page.HtmlAsync();
        Assert.Equal("Unsaved", Held(save.Id));
        Assert.Equal("Unsaved", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(StorageFailed.NotDone("Not discarded"), RenderedPage.Named(html, "page-message"));
        Assert.Equal(1, RenderedPage.Count(html, "working-copy-notice"));
    }

    [Fact]
    public async Task A_save_removes_it()
    {
        var save = await Saved();
        await Written(save.Id, "Unsaved, then saved", save);
        await using var page = await Open(save.Id, Clock());

        await page.ClickAsync("save-button");

        Assert.Null(Held(save.Id));
        Assert.Equal("Unsaved, then saved", (await Store.HistoryAsync(DocumentKind.Draft, save.Id)).First().Title);
        Assert.Equal(0, RenderedPage.Count(await page.HtmlAsync(), "working-copy-notice"));
    }

    [Fact]
    public async Task Keep_mine_removes_it()
    {
        var save = await Saved();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await Typed(a, clock, "Mine", Settings.Debounce);
        clock.Advance(Settings.Debounce);
        await Settled(a);
        Assert.Equal("Mine", Held(save.Id));
        await b.TypeAsync("subject", "Theirs");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("keep-mine");

        await a.ClickAsync("keep-mine");

        Assert.Null(Held(save.Id));
        Assert.Equal("Mine", (await Store.HistoryAsync(DocumentKind.Draft, save.Id)).First().Title);
    }

    [Fact]
    public async Task Deleting_the_campaign_removes_it()
    {
        var save = await Saved();
        await Written(save.Id, "Unsaved", save);
        await using var page = await Open(save.Id, Clock());

        await page.ClickAsync("delete-button");
        await page.ClickAsync("delete-confirm");

        Assert.Null(Held(save.Id));
        Assert.Equal(1, RenderedPage.Count(await page.HtmlAsync(), "restore-deleted"));
    }

    // Undo and Restore each put a stored version in the form, losing what was not saved, as Undo asks first:
    // the working copy of what was lost goes, so it is never brought back over the version they opened.
    [Fact]
    public async Task Undo_and_restore_remove_it()
    {
        var id = Guid.NewGuid();
        await Saved(id, "First");
        var second = await Saved(id, "Second");
        await Written(id, "Unsaved on the second", second);
        await using var page = await Open(id, Clock());

        await page.ClickAsync("undo-button");
        await page.ClickAsync("undo-confirm");
        Assert.Null(Held(id));
        Assert.Equal("First", RenderedPage.Attribute(await page.HtmlAsync(), "subject", "value"));

        var first = (await Store.HistoryAsync(DocumentKind.Draft, id)).First();
        await Written(id, "Unsaved on the first", first);
        await page.ClickAsync("restore-button");
        Assert.Null(Held(id));
        Assert.Equal("Second", RenderedPage.Attribute(await page.HtmlAsync(), "subject", "value"));
    }

    // Deleted elsewhere with a working copy left: restored, it opens as it was saved, and the copy goes.
    [Fact]
    public async Task Restoring_a_deleted_campaign_removes_one_left_from_before()
    {
        var save = await Saved(subject: "Saved subject");
        await Written(save.Id, "Unsaved", save);
        await Store.DeleteAsync(DocumentKind.Draft, save.Id);
        await using var page = await Open(save.Id, Clock());

        await page.ClickAsync("restore-deleted");

        Assert.Null(Held(save.Id));
        var html = await page.HtmlAsync();
        Assert.Equal("Saved subject", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(0, RenderedPage.Count(html, "working-copy-notice"));
    }

    [Fact]
    public async Task Load_the_latest_removes_it()
    {
        var save = await Saved();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await Typed(a, clock, "Mine", Settings.Debounce);
        clock.Advance(Settings.Debounce);
        await Settled(a);
        await b.TypeAsync("subject", "Theirs");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("modified-notice");

        await a.ClickAsync("load-latest");

        Assert.Null(Held(save.Id));
        Assert.Equal("Theirs", RenderedPage.Attribute(await a.HtmlAsync(), "subject", "value"));
    }

    // Each page writes over its own last write only (If-Match); another tab's write since is said, then the
    // last writer wins. Nothing more is done about two tabs (concurrency phase 3).
    [Fact]
    public async Task Another_tab_s_write_is_said_and_then_written_over()
    {
        var save = await Saved();
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        await Typed(page, clock, "Mine, first", Settings.Debounce);
        clock.Advance(Settings.Debounce);
        await Settled(page);
        Assert.Equal(0, RenderedPage.Count(await page.HtmlAsync(), "working-copy-other-tab"));

        await Written(save.Id, "Another tab's", save);
        await Typed(page, clock, "Mine, again", Settings.Debounce);
        clock.Advance(Settings.Debounce);
        await Settled(page);

        Assert.Equal("Mine, again", Held(save.Id));
        Assert.Equal(WorkingCopyWords.OtherTab, RenderedPage.Named(await page.HtmlAsync(), "working-copy-other-tab"));
    }

    [Fact]
    public async Task A_write_storage_cannot_take_is_logged_never_shown_and_tried_again()
    {
        var save = await Saved();
        var clock = Clock();
        var logs = new KeeperLogs();
        await using var page = await Open(save.Id, clock, logs);
        Container.Failure = name => name.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal) ? StorageFailure.Busy() : null;
        try
        {
            await Typed(page, clock, "Mine, typed", Settings.Debounce);
            clock.Advance(Settings.Debounce);
            var html = await page.HtmlAsync();

            Assert.Null(Held(save.Id));
            Assert.Equal("Mine, typed", RenderedPage.Attribute(html, "subject", "value"));
            Assert.Equal(0, RenderedPage.Count(html, "page-message"));
            Assert.Equal(0, RenderedPage.Count(html, "working-copy-other-tab"));
            Assert.IsType<Azure.RequestFailedException>(Assert.Single(logs.Keeper.Entries, e => e.Level == LogLevel.Warning).Error);
        }
        finally
        {
            Container.Failure = null;
        }

        // Tried again a minute on, with nothing more typed.
        clock.Advance(Settings.Cap);
        await Settled(page);
        Assert.Equal("Mine, typed", Held(save.Id));
    }

    // ---- The server-disk fallback (owner, 2026-11-03) ----

    private Func<string, Exception?> WorkingDown => name =>
        name.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal) ? StorageFailure.Busy() : null;

    private string? OnDisk(Guid id) => app.Disk.Held(DemoApp.Client, id)?.Draft.SubjectText();

    // Typed, and the write storage could not take put on the disk instead.
    private async Task OnDiskFirst(OpenCampaignPage page, ManualClock clock, Guid id, string subject)
    {
        Container.Failure = WorkingDown;
        try
        {
            await Typed(page, clock, subject, Settings.Debounce);
            clock.Advance(Settings.Debounce);
            await Settled(page);
        }
        finally
        {
            Container.Failure = null;
        }
        Assert.Equal(subject, OnDisk(id));
    }

    [Fact]
    public async Task A_write_storage_cannot_take_goes_to_the_server_s_disk_until_storage_answers()
    {
        var save = await Saved();
        var clock = Clock();
        await using var page = await Open(save.Id, clock);

        await OnDiskFirst(page, clock, save.Id, "Mine, on the disk");

        Assert.Null(Held(save.Id));
        var copy = app.Disk.Held(DemoApp.Client, save.Id)!;
        Assert.Equal(save.SavedAt, copy.BasedOn);
        Assert.Equal(clock.Now, copy.WrittenAt);
        // Storage answers again: the next write puts it in storage, and the disk copy goes.
        clock.Advance(Settings.Cap);
        await Settled(page);
        Assert.Equal("Mine, on the disk", Held(save.Id));
        Assert.Null(OnDisk(save.Id));
    }

    [Fact]
    public async Task A_save_storage_cannot_take_goes_to_the_server_s_disk()
    {
        var save = await Saved();
        await using var page = await Open(save.Id, Clock());
        await page.TypeAsync("subject", "Mine, not saved");

        await StorageDown.WhileDown(app, () => page.ClickAsync("save-button"));

        Assert.Equal(StorageFailed.NotDone("Not saved"), RenderedPage.Named(await page.HtmlAsync(), "page-message"));
        Assert.Equal("Mine, not saved", OnDisk(save.Id));
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id));
    }

    [Fact]
    public async Task On_opening_a_disk_copy_newer_than_storage_s_is_brought_back_and_moved_into_storage()
    {
        var save = await Saved();
        await Written(save.Id, "Older, in storage", save);
        app.Clock.Now += TimeSpan.FromMinutes(1);
        var disk = new DiskCopy(save.Id, CampaignJson.SerializeDraft(WorkingCopyStoreTests.Edited("Newer, on the disk")), save.SavedAt, app.Clock.Now);
        app.Disk.Put(DemoApp.Client, disk);

        await using var page = await Open(save.Id, Clock());

        var html = await page.HtmlAsync();
        Assert.Equal("Newer, on the disk", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(WorkingCopyWords.BroughtBack(Times.DateAndTime(disk.WrittenAt)), RenderedPage.Named(html, "working-copy-what"));
        Assert.Equal("Discard", RenderedPage.Named(html, "working-copy-discard"));
        Assert.Equal("Newer, on the disk", Held(save.Id));
        Assert.Null(OnDisk(save.Id));
    }

    [Fact]
    public async Task On_opening_a_disk_copy_older_than_what_storage_holds_is_dropped()
    {
        var save = await Saved();
        app.Disk.Put(DemoApp.Client, new DiskCopy(save.Id,
            CampaignJson.SerializeDraft(WorkingCopyStoreTests.Edited("Older, on the disk")), save.SavedAt, app.Clock.Now));
        app.Clock.Now += TimeSpan.FromMinutes(1);
        await Written(save.Id, "Newer, in storage", save);

        await using var page = await Open(save.Id, Clock());

        Assert.Equal("Newer, in storage", RenderedPage.Attribute(await page.HtmlAsync(), "subject", "value"));
        Assert.Null(OnDisk(save.Id));
    }

    [Fact]
    public async Task A_save_removes_the_disk_copy_too()
    {
        var save = await Saved();
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        await OnDiskFirst(page, clock, save.Id, "On the disk, then saved");

        await page.ClickAsync("save-button");

        Assert.Null(OnDisk(save.Id));
        Assert.Null(Held(save.Id));
    }

    [Fact]
    public async Task Discard_removes_the_disk_copy_too()
    {
        var save = await Saved();
        await Written(save.Id, "Brought back", save);
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        await OnDiskFirst(page, clock, save.Id, "On the disk, then discarded");

        await page.ClickAsync("working-copy-discard");

        Assert.Null(OnDisk(save.Id));
        Assert.Null(Held(save.Id));
    }

    [Fact]
    public async Task Deleting_the_campaign_removes_the_disk_copy_too()
    {
        var save = await Saved();
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        await OnDiskFirst(page, clock, save.Id, "On the disk, then deleted");

        await page.ClickAsync("delete-button");
        await page.ClickAsync("delete-confirm");

        Assert.Null(OnDisk(save.Id));
    }

    [Fact]
    public async Task A_disk_the_fallback_cannot_write_to_is_logged_and_never_escapes()
    {
        var save = await Saved();
        var clock = Clock();
        var logs = new KeeperLogs();
        await using var page = await Open(save.Id, clock, logs);
        app.Disk.Failure = () => new IOException("No space left on device.");
        Container.Failure = WorkingDown;
        try
        {
            await Typed(page, clock, "Mine, typed", Settings.Debounce);
            clock.Advance(Settings.Debounce);
            var html = await page.HtmlAsync();

            Assert.Equal("Mine, typed", RenderedPage.Attribute(html, "subject", "value"));
            Assert.IsType<IOException>(Assert.Single(logs.Keeper.Entries, e => e.Level == LogLevel.Error).Error);
        }
        finally
        {
            Container.Failure = null;
            app.Disk.Failure = null;
        }
        // Nothing stopped: storage is tried again after the cap, as for any failed write.
        clock.Advance(Settings.Cap);
        await Settled(page);
        Assert.Equal("Mine, typed", Held(save.Id));
    }

    // Changed back to what was saved: nothing unsaved is left, so the copy of what was goes.
    [Fact]
    public async Task Changed_back_to_the_newest_save_its_working_copy_goes()
    {
        var save = await Saved(subject: "Saved subject");
        var clock = Clock();
        await using var page = await Open(save.Id, clock);
        await Typed(page, clock, "For a while", Settings.Debounce);
        clock.Advance(Settings.Debounce);
        await Settled(page);
        Assert.Equal("For a while", Held(save.Id));

        await Typed(page, clock, "Saved subject", Settings.Debounce);
        clock.Advance(Settings.Debounce);
        await Settled(page);

        Assert.Null(Held(save.Id));
    }

    // ---- A campaign never saved (owner, 2026-11-03: "No typing means no harm, no foul") ----

    private async Task<OpenCampaignPage> OpenNew(ManualClock clock, params object[] more)
    {
        var templateId = Guid.NewGuid();
        await Store.SaveTemplateAsync(templateId, DraftFixtures.Membership);
        return await OpenCampaignPage.OpenAsync<CampaignPage>(
            app, new Dictionary<string, object?> { [nameof(CampaignPage.TemplateId)] = (Guid?)templateId }, [clock, .. more]);
    }

    private IReadOnlyList<string> WorkingNames =>
        Container.Blobs.Keys.Where(k => k.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal)).ToList();

    private static Guid IdOf(string name) => Guid.ParseExact(name[WorkingCopies.Prefix.Length..^".json".Length], "N");

    [Fact]
    public async Task On_a_new_campaign_nothing_is_kept_until_she_changes_the_form()
    {
        var clock = Clock();
        var browser = new RecordingBrowser();
        await using var page = await OpenNew(clock, browser);
        var replaces = Container.Replaces;

        clock.Advance(Settings.Cap + Settings.Debounce);
        await Settled(page);

        Assert.Equal(replaces, Container.Replaces);
        Assert.DoesNotContain(browser.Calls, c => c.Identifier == "history.replaceState");
    }

    [Fact]
    public async Task Her_first_change_to_a_new_campaign_gives_it_an_id_its_own_address_and_a_working_copy_her_save_keeps()
    {
        var clock = Clock();
        var browser = new RecordingBrowser();
        await using var page = await OpenNew(clock, browser);
        var before = WorkingNames;

        await page.TypeAsync("subject", "Typed, never saved");
        await Settled(page);

        // At once, not after the pause: there is no save to fall back on.
        var name = Assert.Single(WorkingNames.Except(before));
        var id = IdOf(name);
        Assert.Equal("Typed, never saved", Held(id));
        Assert.Null((await Store.WorkingCopies.ReadAsync(id))!.BasedOn);
        Assert.Empty(await Store.HistoryAsync(DocumentKind.Draft, id));
        var moved = Assert.Single(browser.Calls, c => c.Identifier == "history.replaceState");
        Assert.EndsWith($"/campaigns/{id}", (string)moved.Arguments[2]!);

        await page.ClickAsync("save-button");

        var saved = Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, id));
        Assert.Equal("Typed, never saved", saved.Title);
        Assert.Null(Held(id));
    }

    [Fact]
    public async Task A_new_campaign_reopened_at_its_address_brings_back_what_she_typed()
    {
        var id = Guid.NewGuid();
        await Store.WorkingCopies.WriteAsync(id, WorkingCopyStoreTests.Edited("Typed before the tab closed"), null, BlobExpected.Nothing);
        var copy = (await Store.WorkingCopies.ReadAsync(id))!;

        await using var page = await Open(id, Clock());

        var html = await page.HtmlAsync();
        Assert.Equal("Typed before the tab closed", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(WorkingCopyWords.BroughtBack(Times.DateAndTime(copy.WrittenAt)), RenderedPage.Named(html, "working-copy-what"));
        await page.ClickAsync("save-button");
        Assert.Equal("Typed before the tab closed", Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, id)).Title);
    }

    [Fact]
    public async Task A_new_campaign_storage_cannot_take_is_kept_on_the_disk_and_brought_back_from_it()
    {
        var clock = Clock();
        Guid id;
        await using (var page = await OpenNew(clock))
        {
            Container.Failure = WorkingDown;
            try
            {
                await page.TypeAsync("subject", "Typed while storage was down");
                await Settled(page);
            }
            finally
            {
                Container.Failure = null;
            }
            id = LastDiskId("Typed while storage was down");
            Assert.Null(Held(id));
            Assert.Null(app.Disk.Held(DemoApp.Client, id)!.BasedOn);
        }

        await using var reopened = await Open(id, Clock());

        Assert.Equal("Typed while storage was down", RenderedPage.Attribute(await reopened.HtmlAsync(), "subject", "value"));
        Assert.Equal("Typed while storage was down", Held(id));
        Assert.Null(OnDisk(id));
    }

    // The disk copy of a subject, found by what it holds: the test does not know the id the page gave it.
    private Guid LastDiskId(string subject) =>
        Assert.Single(app.Disk.All(DemoApp.Client), c => c.Draft.SubjectText() == subject).CampaignId;

    [Fact]
    public async Task Closing_the_page_stops_its_timer()
    {
        var save = await Saved();
        var clock = Clock();
        var page = await Open(save.Id, clock);
        await Typed(page, clock, "Mine, typed", Settings.Debounce);
        var replaces = Container.Replaces;

        await page.DisposeAsync();

        Assert.Equal(0, clock.WaitingFor(Settings.Debounce));
        clock.Advance(Settings.Cap);
        Assert.Equal(replaces, Container.Replaces);
    }
}

/// <summary>A browser that answers nothing and keeps what the page asked of it, such as moving its address.</summary>
internal sealed class RecordingBrowser : Microsoft.JSInterop.IJSRuntime
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Identifier, object?[] Arguments)> _calls = new();

    public IReadOnlyList<(string Identifier, object?[] Arguments)> Calls => _calls.ToList();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        _calls.Enqueue((identifier, args ?? []));
        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);

    public ValueTask SetValueAsync<TValue>(string identifier, TValue value)
    {
        _calls.Enqueue((identifier, [value]));
        return ValueTask.CompletedTask;
    }

    public ValueTask SetValueAsync<TValue>(string identifier, TValue value, CancellationToken cancellationToken) =>
        SetValueAsync(identifier, value);
}

/// <summary>The working copy's own log, kept; every other category goes nowhere.</summary>
internal sealed class KeeperLogs : ILoggerFactory
{
    public ListLogger<WorkingCopyKeeper> Keeper { get; } = new();

    public ILogger CreateLogger(string categoryName) =>
        categoryName == typeof(WorkingCopyKeeper).FullName ? Keeper : NullLogger.Instance;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
