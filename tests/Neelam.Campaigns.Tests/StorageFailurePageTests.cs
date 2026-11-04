using Neelam.Campaigns.Storage;
using CampaignPage = Neelam.Web.Components.Pages.CampaignEdit;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Storage failing under one of her clicks on the campaign and template editors (2026-11-03): before, the
/// store's exception escaped the click, which on an interactive page ends the connection and her unsaved
/// typing with it. Now the form stays exactly as she typed it, the page says the action did not happen
/// (<see cref="StorageFailed.NotDone"/>), and the page lives on: the click returns and the next one works.
/// Anything other than storage failing still escapes. Each page is held as its circuit holds it
/// (<see cref="OpenCampaignPage"/>), whose clicks rethrow what would have ended the circuit.
/// </summary>
internal static class StorageDown
{
    /// <summary>Runs <paramref name="click"/> with the client's container failing every call, then lets it answer again.</summary>
    public static async Task WhileDown(InMemoryApp app, Func<Task> click, Func<Exception>? failure = null)
    {
        var container = app.Containers.For(DemoApp.Client.Value);
        container.Failure = _ => (failure ?? StorageFailure.Busy)();
        try
        {
            await click();
        }
        finally
        {
            container.Failure = null;
        }
    }
}

public class CampaignStorageFailureTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<SaveRef> Saved(Guid? id = null)
    {
        var save = await Store.SaveDraftAsync(id ?? Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    private Task<OpenCampaignPage> Open(Guid id, ManualClock? clock = null) =>
        OpenCampaignPage.OpenAsync(app, id, clock ?? new ManualClock(app.Clock.Now));

    private Task WhileDown(Func<Task> click) => StorageDown.WhileDown(app, click);

    // Her typing is on the page as she left it, and the page says what did not happen.
    private static void Kept(string html, string typed, string notDone)
    {
        Assert.Equal(typed, RenderedPage.Attribute(html, "subject", "value"));
        Assert.Equal(StorageFailed.NotDone(notDone), RenderedPage.Named(html, "page-message"));
    }

    [Fact]
    public async Task A_save_storage_cannot_take_keeps_her_typing_and_says_it_was_not_saved()
    {
        var save = await Saved();
        await using var page = await Open(save.Id);
        await page.TypeAsync("subject", "Mine, typed");

        await WhileDown(() => page.ClickAsync("save-button"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not saved");
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id));
        // The page lives on: once storage answers, the same click saves it.
        await page.ClickAsync("save-button");
        Assert.Equal("Mine, typed", (await Store.HistoryAsync(DocumentKind.Draft, save.Id)).First().Title);
    }

    // A new campaign's first save failed: the next one is still its first, so the page watches it from then.
    [Fact]
    public async Task A_new_campaign_whose_first_save_failed_is_watched_once_it_is_saved()
    {
        var templateId = Guid.NewGuid();
        await Store.SaveTemplateAsync(templateId, DraftFixtures.Membership);
        var clock = new ManualClock(app.Clock.Now);
        await using var a = await OpenCampaignPage.OpenAsync<CampaignPage>(
            app, new Dictionary<string, object?> { [nameof(CampaignPage.TemplateId)] = (Guid?)templateId }, clock);
        var subject = $"New {Guid.NewGuid():N}";
        await a.TypeAsync("subject", subject);

        await WhileDown(() => a.ClickAsync("save-button"));
        Kept(await a.HtmlAsync(), subject, "Not saved");
        await a.ClickAsync("save-button");

        var saved = Assert.Single(await Store.LatestAsync(DocumentKind.Draft), s => s.Title == subject);
        await using var b = await Open(saved.Id, clock);
        await b.TypeAsync("subject", "Theirs");
        await b.ClickAsync("save-button");
        Assert.Equal("The campaign was saved again since you opened it, in another tab or by someone else.",
            RenderedPage.Named(await a.WaitForAsync("modified-notice"), "modified-what"));
    }

    [Fact]
    public async Task An_undo_storage_cannot_take_keeps_her_typing_and_undoes_nothing()
    {
        var id = Guid.NewGuid();
        await Saved(id);
        await Saved(id);
        await using var page = await Open(id);
        await page.TypeAsync("subject", "Mine, typed");
        await page.ClickAsync("undo-button");

        await WhileDown(() => page.ClickAsync("undo-confirm"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not undone");
        Assert.Equal(2, (await Store.HistoryAsync(DocumentKind.Draft, id)).Count);
    }

    [Fact]
    public async Task A_delete_storage_cannot_take_keeps_her_typing_and_deletes_nothing()
    {
        var save = await Saved();
        await using var page = await Open(save.Id);
        await page.TypeAsync("subject", "Mine, typed");
        await page.ClickAsync("delete-button");

        await WhileDown(() => page.ClickAsync("delete-confirm"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not deleted");
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id));
    }

    [Fact]
    public async Task A_restore_storage_cannot_take_keeps_her_typing_and_restores_nothing()
    {
        var id = Guid.NewGuid();
        await Saved(id);
        await Saved(id);
        await using var page = await Open(id);
        await page.ClickAsync("undo-button");
        await page.ClickAsync("undo-confirm");
        await page.TypeAsync("subject", "Mine, typed");

        await WhileDown(() => page.ClickAsync("restore-button"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not restored");
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, id));
        Assert.NotNull(await Store.RestorableAsync(DocumentKind.Draft, id));
    }

    [Fact]
    public async Task Restoring_a_deleted_campaign_storage_cannot_take_says_so_and_offers_it_again()
    {
        var save = await Saved();
        await Store.DeleteAsync(DocumentKind.Draft, save.Id);
        await using var page = await Open(save.Id);

        await WhileDown(() => page.ClickAsync("restore-deleted"));

        var html = await page.HtmlAsync();
        Assert.Equal(StorageFailed.NotDone("Not restored"), RenderedPage.Named(html, "page-message"));
        Assert.Equal(1, RenderedPage.Count(html, "restore-deleted"));
        Assert.Empty(await Store.HistoryAsync(DocumentKind.Draft, save.Id));
    }

    [Fact]
    public async Task Load_the_latest_storage_cannot_answer_keeps_her_typing_and_the_notice()
    {
        var save = await Saved();
        var clock = new ManualClock(app.Clock.Now);
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await a.TypeAsync("subject", "Mine, typed");
        await b.TypeAsync("subject", "Theirs, saved");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("modified-notice");

        await WhileDown(() => a.ClickAsync("load-latest"));

        var html = await a.HtmlAsync();
        Kept(html, "Mine, typed", "Not loaded");
        Assert.Equal(1, RenderedPage.Count(html, "modified-notice"));
        await a.ClickAsync("load-latest");
        Assert.Equal("Theirs, saved", RenderedPage.Attribute(await a.HtmlAsync(), "subject", "value"));
    }

    // The approver's name is typed too: a load that did not happen leaves it.
    [Fact]
    public async Task Load_the_latest_storage_cannot_answer_keeps_the_approver_s_name_typed()
    {
        var save = await Saved();
        var clock = new ManualClock(app.Clock.Now);
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await a.TypeAsync("approver", "Priya");
        await b.TypeAsync("subject", "Theirs, saved");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("modified-notice");

        await WhileDown(() => a.ClickAsync("load-latest"));

        Assert.Equal("Priya", RenderedPage.Attribute(await a.HtmlAsync(), "approver", "value"));
    }

    [Fact]
    public async Task Keep_mine_storage_cannot_take_keeps_her_typing_and_saves_nothing()
    {
        var save = await Saved();
        var clock = new ManualClock(app.Clock.Now);
        await using var a = await Open(save.Id, clock);
        await using var b = await Open(save.Id, clock);
        await a.TypeAsync("subject", "Mine, typed");
        await b.TypeAsync("subject", "Theirs, saved");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("keep-mine");

        await WhileDown(() => a.ClickAsync("keep-mine"));

        Kept(await a.HtmlAsync(), "Mine, typed", "Not saved");
        Assert.Equal("Theirs, saved", (await Store.HistoryAsync(DocumentKind.Draft, save.Id)).First().Title);
    }

    [Fact]
    public async Task An_approval_storage_cannot_take_keeps_the_name_typed_and_approves_nothing()
    {
        var save = await Saved();
        await using var page = await Open(save.Id);
        await page.TypeAsync("approver", "Priya");

        await WhileDown(() => page.ClickAsync("approve-button"));

        var html = await page.HtmlAsync();
        Assert.Equal("Priya", RenderedPage.Attribute(html, "approver", "value"));
        Assert.Equal(StorageFailed.NotDone("Not approved"), RenderedPage.Named(html, "page-message"));
        Assert.DoesNotContain(app.Approvals.Rows, r => r.CampaignId == save.Id);
    }

    // Only storage failing is caught: a fault in the app still surfaces, as before.
    [Fact]
    public async Task Anything_but_storage_failing_still_escapes_the_click()
    {
        var save = await Saved();
        await using var page = await Open(save.Id);

        await Assert.ThrowsAsync<InvalidCastException>(() => StorageDown.WhileDown(
            app, () => page.ClickAsync("save-button"), () => new InvalidCastException("A fault in the app.")));
    }
}

public class TemplateStorageFailureTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<Guid> Saved(int saves = 1)
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < saves; i++)
        {
            await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
            app.Clock.Now += TimeSpan.FromSeconds(1);
        }
        return id;
    }

    private Task<OpenCampaignPage> Open(Guid id, ManualClock? clock = null) =>
        OpenCampaignPage.OpenAsync<TemplatePage>(app, new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)id },
            clock ?? new ManualClock(app.Clock.Now));

    private Task WhileDown(Func<Task> click) => StorageDown.WhileDown(app, click);

    private static void Kept(string html, string typed, string notDone)
    {
        Assert.Equal(typed, RenderedPage.Attribute(html, "template-name", "value"));
        Assert.Equal(StorageFailed.NotDone(notDone), RenderedPage.Named(html, "page-message"));
    }

    [Fact]
    public async Task A_save_storage_cannot_take_keeps_her_typing_and_says_it_was_not_saved()
    {
        var id = await Saved();
        await using var page = await Open(id);
        await page.TypeAsync("template-name", "Mine, typed");

        await WhileDown(() => page.ClickAsync("save-button"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not saved");
        Assert.Single(await Store.HistoryAsync(DocumentKind.Template, id));
        await page.ClickAsync("save-button");
        Assert.Equal("Mine, typed", (await Store.HistoryAsync(DocumentKind.Template, id)).First().Title);
    }

    [Fact]
    public async Task An_undo_storage_cannot_take_keeps_her_typing_and_undoes_nothing()
    {
        var id = await Saved(saves: 2);
        await using var page = await Open(id);
        await page.TypeAsync("template-name", "Mine, typed");
        await page.ClickAsync("undo-button");

        await WhileDown(() => page.ClickAsync("undo-confirm"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not undone");
        Assert.Equal(2, (await Store.HistoryAsync(DocumentKind.Template, id)).Count);
    }

    // The template page's delete is the undo of its only save.
    [Fact]
    public async Task A_delete_storage_cannot_take_keeps_her_typing_and_deletes_nothing()
    {
        var id = await Saved();
        await using var page = await Open(id);
        await page.TypeAsync("template-name", "Mine, typed");
        await page.ClickAsync("undo-button");

        await WhileDown(() => page.ClickAsync("undo-confirm"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not deleted");
        Assert.Single(await Store.HistoryAsync(DocumentKind.Template, id));
    }

    [Fact]
    public async Task A_restore_storage_cannot_take_keeps_her_typing_and_restores_nothing()
    {
        var id = await Saved(saves: 2);
        await using var page = await Open(id);
        await page.ClickAsync("undo-button");
        await page.ClickAsync("undo-confirm");
        await page.TypeAsync("template-name", "Mine, typed");

        await WhileDown(() => page.ClickAsync("restore-button"));

        Kept(await page.HtmlAsync(), "Mine, typed", "Not restored");
        Assert.Single(await Store.HistoryAsync(DocumentKind.Template, id));
    }

    [Fact]
    public async Task Load_the_latest_storage_cannot_answer_keeps_her_typing()
    {
        var id = await Saved();
        var clock = new ManualClock(app.Clock.Now);
        await using var a = await Open(id, clock);
        await using var b = await Open(id, clock);
        await a.TypeAsync("template-name", "Mine, typed");
        await b.TypeAsync("template-name", "Theirs, saved");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("modified-notice");

        await WhileDown(() => a.ClickAsync("load-latest"));

        Kept(await a.HtmlAsync(), "Mine, typed", "Not loaded");
    }

    [Fact]
    public async Task Anything_but_storage_failing_still_escapes_the_click()
    {
        var id = await Saved();
        await using var page = await Open(id);

        await Assert.ThrowsAsync<InvalidCastException>(() => StorageDown.WhileDown(
            app, () => page.ClickAsync("save-button"), () => new InvalidCastException("A fault in the app.")));
    }
}
