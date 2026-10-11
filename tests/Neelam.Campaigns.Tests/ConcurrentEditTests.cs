using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Two tabs on one campaign (owner, 2026-10-11: "concurrent usage by two users"; the realistic case is
/// one person in two tabs, or on phone and laptop). Each tab is the campaign page held as its circuit
/// holds it (<see cref="OpenCampaignPage"/>), over the app's in-memory storage; a change "from another
/// instance" is one written straight through the store, which raises no signal. Each page's clock moves
/// only when told, so a notice that shows without it came through the signal. Read by named elements.
/// </summary>
public class ConcurrentEditTests(DemoApp app) : IClassFixture<DemoApp>
{
    private const string Subject = "WE’RE TURNING ONE!";

    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private TimeSpan Interval => app.Services.GetRequiredService<IOptions<CampaignWatchOptions>>().Value.Interval;

    private SteppingClock Clock() => new(app.Clock.Now);

    private async Task<SaveRef> Saved(Guid? id = null, CampaignDraft? draft = null)
    {
        var save = await Store.SaveDraftAsync(id ?? Guid.NewGuid(), Subject, draft ?? DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    // Approved, and gone on past its warnings at export: the page opens with the export on it.
    private async Task<SaveRef> Exportable(Guid? id = null)
    {
        var save = await Store.ApproveAsync(await Saved(id), "Priya");
        return await Store.WarningsSeenAtExportAsync(save, SeenAtExport.KeysOf(DraftFixtures.Finished()));
    }

    private Task<OpenCampaignPage> Open(Guid id, SteppingClock clock) => OpenCampaignPage.OpenAsync(app, id, clock);

    private static void HasTheExport(string html)
    {
        Assert.Equal(1, RenderedPage.Count(html, "export"));
        Assert.Equal(1, RenderedPage.Count(html, "assistant-download"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
        Assert.Equal(0, RenderedPage.Count(html, "export-refused"));
    }

    // Nothing to copy and nothing to download: only why not.
    private static void NoExport(string html, string why)
    {
        Assert.Equal(0, RenderedPage.Count(html, "export"));
        Assert.Equal(0, RenderedPage.Count(html, "assistant-download"));
        Assert.Equal(0, RenderedPage.Count(html, "export-warnings-list"));
        Assert.Equal(why, RenderedPage.Named(html, "export-refused"));
    }

    [Fact]
    public async Task A_save_in_another_tab_takes_the_export_away_at_once_and_says_why()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        HasTheExport(await a.HtmlAsync());

        await b.TypeAsync("subject", "Celebrate with us");
        await b.ClickAsync("save-button");

        // The clocks never moved: the signal told the other tab.
        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("The campaign was saved again since you opened it, in another tab or by someone else.",
            RenderedPage.Named(html, "modified-what"));
        NoExport(html, "Not ready to copy into Square: the campaign was saved again since you opened it. " +
                       "Load the latest above, and copy from it once it is approved.");
        Assert.Equal("Load the latest", RenderedPage.Named(html, "load-latest"));
        // The tab that saved is told nothing about its own save.
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), "modified-notice"));
    }

    [Fact]
    public async Task An_approval_withdrawn_in_another_tab_takes_the_export_away()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        HasTheExport(await a.HtmlAsync());

        await b.ClickAsync("withdraw-button");

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("Its approval was withdrawn since you opened it, in another tab or by someone else.",
            RenderedPage.Named(html, "modified-what"));
        NoExport(html, "Not ready to copy into Square: its approval was withdrawn since you opened it. " +
                       "Load the latest above, and copy from it once it is approved.");
    }

    [Fact]
    public async Task A_save_undone_in_another_tab_takes_the_export_away()
    {
        var id = Guid.NewGuid();
        var first = await Saved(id);
        await Exportable(id);
        var clock = Clock();
        await using var a = await Open(id, clock);
        await using var b = await Open(id, clock);
        HasTheExport(await a.HtmlAsync());

        await b.ClickAsync("undo-button");
        await b.ClickAsync("undo-confirm");

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("The version on this page was undone since you opened it, in another tab or by someone else.",
            RenderedPage.Named(html, "modified-what"));
        NoExport(html, "Not ready to copy into Square: the version on this page was undone since you opened it. " +
                       "Load the latest above, and copy from it once it is approved.");
        Assert.Equal(first.BlobName, Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, id)).BlobName);
    }

    [Fact]
    public async Task Approving_a_version_that_is_no_longer_the_newest_is_refused_and_approves_nothing()
    {
        var first = await Saved();
        var clock = Clock();
        await using var a = await Open(first.Id, clock);
        // Saved on another instance: no signal, and the page's clock has not moved, so the page cannot know.
        await Saved(first.Id);
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "modified-notice"));

        await a.TypeAsync("approver", "Priya");
        await a.ClickAsync("approve-button");

        var html = await a.HtmlAsync();
        Assert.Equal("Not approved: the campaign was saved again since you opened it. Load the latest above, check it, then approve it.",
            RenderedPage.Named(html, "page-message"));
        Assert.DoesNotContain(app.Approvals.Rows, r => r.CampaignId == first.Id);
        Assert.Equal(0, RenderedPage.Count(html, "approved"));
        // And it says so at the top, from the read the click made.
        Assert.Equal("The campaign was saved again since you opened it, in another tab or by someone else.",
            RenderedPage.Named(html, "modified-what"));
    }

    [Fact]
    public async Task With_no_signal_the_regular_check_says_modified_within_its_interval()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        HasTheExport(await a.HtmlAsync());

        // Withdrawn on another instance: nothing tells this one but its own look at storage.
        await Store.WithdrawApprovalAsync(save);
        clock.Advance(Interval - TimeSpan.FromSeconds(1));
        HasTheExport(await a.HtmlAsync());

        clock.Advance(TimeSpan.FromSeconds(1));

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("Its approval was withdrawn since you opened it, in another tab or by someone else.",
            RenderedPage.Named(html, "modified-what"));
        Assert.Equal(0, RenderedPage.Count(html, "export"));
    }

    // The export never comes back from what the page alone holds: when it is offered again, here by an
    // edit taken back, storage is read first, and a withdrawal made elsewhere meanwhile stops it.
    [Fact]
    public async Task An_export_offered_again_is_read_from_storage_first()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        var subject = RenderedPage.Attribute(await a.HtmlAsync(), "subject", "value");

        await a.TypeAsync("subject", subject + "!");
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "export"));
        await Store.WithdrawApprovalAsync(save);
        await a.TypeAsync("subject", subject);

        var html = await a.WaitForAsync("export-refused");
        NoExport(html, "Not ready to copy into Square: its approval was withdrawn since you opened it. " +
                       "Load the latest above, and copy from it once it is approved.");
    }

    [Fact]
    public async Task An_export_offered_again_with_nothing_changed_comes_back()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        var subject = RenderedPage.Attribute(await a.HtmlAsync(), "subject", "value");

        await a.TypeAsync("subject", subject + "!");
        await a.TypeAsync("subject", subject);

        HasTheExport(await a.WaitForAsync("export"));
    }

    [Fact]
    public async Task What_she_typed_stays_until_she_loads_the_latest()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await a.TypeAsync("subject", "Mine, not saved");
        await b.TypeAsync("subject", "Theirs, saved");
        await b.ClickAsync("save-button");

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("Mine, not saved", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal("Your changes on this page are not saved. Loading the latest replaces them, so copy anything you want to keep first.",
            RenderedPage.Named(html, "modified-unsaved"));

        await a.ClickAsync("load-latest");

        html = await a.HtmlAsync();
        Assert.Equal("Theirs, saved", RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal("The latest version is open.", RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
    }

    private static readonly LocalTime Times = LocalTime.For(LocalTime.DefaultZone);

    private async Task<SaveRef> Newest(Guid id) => (await Store.HistoryAsync(DocumentKind.Draft, id)).First();

    // "Keep mine" (owner, 2026-10-11): hers saved as the newest, theirs left stored, the other tab told.
    [Fact]
    public async Task Keep_mine_makes_hers_the_newest_keeps_theirs_and_tells_the_other_tab()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await a.TypeAsync("subject", "Mine");
        await b.TypeAsync("subject", "Theirs");
        await b.ClickAsync("save-button");
        var theirs = await Newest(save.Id);

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("Keep mine", RenderedPage.Named(html, "keep-mine"));
        Assert.Equal("Keep mine saves what's on this page as the newest version. The other version stays in the campaign's history.",
            RenderedPage.Named(html, "keep-mine-help"));
        app.Clock.Now += TimeSpan.FromMinutes(1);
        await a.ClickAsync("keep-mine");

        var mine = await Newest(save.Id);
        Assert.Equal("Mine", mine.Title);
        Assert.Contains(await Store.HistoryAsync(DocumentKind.Draft, save.Id), s => s.BlobName == theirs.BlobName);
        html = await a.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
        Assert.Equal($"Saved at {Times.TimeOfDay(mine.SavedAt)} as the newest version, in place of the one saved " +
                     $"{Times.DateAndTime(theirs.SavedAt)}, which stays in the campaign's history. This version is not approved yet.",
            RenderedPage.Named(html, "page-message"));
        Assert.Equal("The campaign was saved again since you opened it, in another tab or by someone else.",
            RenderedPage.Named(await b.WaitForAsync("modified-notice"), "modified-what"));
    }

    [Fact]
    public async Task After_keep_mine_approve_and_export_work_on_hers()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await a.TypeAsync("subject", "Mine");
        await b.TypeAsync("subject", "Theirs");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("modified-notice");
        await a.ClickAsync("keep-mine");

        await a.TypeAsync("approver", "Priya");
        await a.ClickAsync("approve-button");
        var html = await a.HtmlAsync();
        Assert.Equal("Approved by Priya.", RenderedPage.Named(html, "page-message"));
        if (RenderedPage.Count(html, "export-anyway") == 1) await a.ClickAsync("export-anyway");

        HasTheExport(await a.HtmlAsync());
        var mine = await Newest(save.Id);
        Assert.Equal(("Mine", "Priya"), (mine.Title, mine.Approval?.By));
    }

    // Looking at an older version with nothing edited: the same button brings that version back as the
    // newest, and the approval the other version had stays with it.
    [Fact]
    public async Task Use_this_version_again_saves_the_one_on_the_page_as_the_newest()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        var subject = RenderedPage.Attribute(await a.HtmlAsync(), "subject", "value");
        var other = DraftFixtures.Finished();
        other.Subject.Set("Theirs");
        var theirs = await Store.ApproveAsync(await Store.SaveDraftAsync(save.Id, "Theirs", other), "Neelam");
        app.Clock.Now += TimeSpan.FromMinutes(1);
        clock.Advance(Interval);

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("Use this version again", RenderedPage.Named(html, "keep-mine"));
        Assert.Equal("Use this version again saves this version as the newest. The other version stays in the campaign's history.",
            RenderedPage.Named(html, "keep-mine-help"));
        await a.ClickAsync("keep-mine");

        var mine = await Newest(save.Id);
        Assert.Equal(subject, mine.Title);
        Assert.NotEqual(theirs.BlobName, mine.BlobName);
        Assert.Null(mine.Approval);
        html = await a.HtmlAsync();
        Assert.EndsWith(", which stays in the campaign's history. Its approval by Neelam does not carry over: " +
                        "approve this version before you copy it into Square.", RenderedPage.Named(html, "page-message"));
        Assert.Contains(Times.DateAndTime(theirs.SavedAt), RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "approved"));
        Assert.Equal(0, RenderedPage.Count(html, "export"));
    }

    // A save that lands after the notice: hers is still saved as the newest, as she chose, and the page
    // names the version it actually took the place of, read at the click.
    [Fact]
    public async Task Keep_mine_names_the_version_it_replaced_even_one_saved_after_the_notice()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await a.TypeAsync("subject", "Mine");
        await b.TypeAsync("subject", "Theirs");
        await b.ClickAsync("save-button");
        var noticed = await Newest(save.Id);
        await a.WaitForAsync("modified-notice");
        app.Clock.Now += TimeSpan.FromMinutes(5);
        var later = DraftFixtures.Finished();
        later.Subject.Set("Later still");
        var latest = await Store.SaveDraftAsync(save.Id, "Later still", later);
        app.Clock.Now += TimeSpan.FromMinutes(5);

        await a.ClickAsync("keep-mine");

        Assert.Equal("Mine", (await Newest(save.Id)).Title);
        var message = RenderedPage.Named(await a.HtmlAsync(), "page-message");
        Assert.Contains($"in place of the one saved {Times.DateAndTime(latest.SavedAt)}, which stays in the campaign's history.", message);
        Assert.DoesNotContain(Times.DateAndTime(noticed.SavedAt), message);
        Assert.Equal(4, (await Store.HistoryAsync(DocumentKind.Draft, save.Id)).Count);
    }

    // The happy path, one tab: approve, go on past the warnings, and the export is there at once, with
    // nothing more to click and nothing said about changes.
    [Fact]
    public async Task In_one_tab_approve_and_export_go_on_as_before()
    {
        var save = await Saved();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);

        await a.TypeAsync("approver", "Priya");
        await a.ClickAsync("approve-button");

        var html = await a.HtmlAsync();
        Assert.Equal("Approved by Priya.", RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
        Assert.Equal(1, RenderedPage.Count(html, "export-warnings-list"));

        await a.ClickAsync("export-anyway");

        HasTheExport(await a.HtmlAsync());
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "export-checking"));
    }

    [Fact]
    public async Task Her_own_save_and_withdrawal_are_never_news_to_her()
    {
        var save = await Exportable();
        var clock = Clock();
        await using var a = await Open(save.Id, clock);

        await a.ClickAsync("withdraw-button");
        var html = await a.HtmlAsync();
        Assert.Equal("The approval is withdrawn.", RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));

        await a.TypeAsync("subject", "Celebrate with us");
        await a.ClickAsync("save-button");

        html = await a.HtmlAsync();
        Assert.StartsWith("Saved at", RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
    }

    [Fact]
    public async Task Closing_the_page_stops_its_timer_and_its_listening()
    {
        var save = await Exportable();
        var clock = Clock();
        var signals = app.Services.GetRequiredService<CampaignSignals>();
        var a = await Open(save.Id, clock);
        await a.WaitForAsync(_ => clock.Waiting == 1, "its timer set");
        Assert.Equal(1, signals.Listening(DemoApp.Client, save.Id));

        await a.DisposeAsync();

        Assert.Equal(0, clock.Waiting);
        Assert.Equal(0, signals.Listening(DemoApp.Client, save.Id));
        // A change after it closed reaches nobody, and fails nothing.
        signals.Raise(DemoApp.Client, save.Id, source: null);
    }
}

/// <summary>
/// Approve reads the lists the checks use again at the click: a known item another tab added since the
/// page opened is checked against before the approval, and the page shows its finding from then on.
/// Its own app, so the known item added here reaches no other test's campaign.
/// </summary>
public class ApproveRereadsChecksTests(DemoApp app) : IClassFixture<DemoApp>
{
    [Fact]
    public async Task A_known_item_added_in_another_tab_is_checked_at_approve()
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo)
            .SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        await using var a = await OpenCampaignPage.OpenAsync(app, save.Id, new SteppingClock(app.Clock.Now));
        const string KnownPrice = "'Platinum Member' is $249/month in your known items; here it is $299/month.";
        Assert.DoesNotContain(KnownPrice, RenderedPage.Named(await a.HtmlAsync(), "checks-findings"));

        await app.KnownItems.AddAsync(DemoApp.Client, new KnownTier(KnownItem.NewId(), "Platinum Member", 249m, []));
        await a.TypeAsync("approver", "Priya");
        await a.ClickAsync("approve-button");

        var html = await a.HtmlAsync();
        Assert.Contains(KnownPrice, RenderedPage.Named(html, "checks-findings"));
        // Worth a look, never a block: approved, as before.
        Assert.Equal("Approved by Priya.", RenderedPage.Named(html, "page-message"));
    }
}
