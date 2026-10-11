using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Components.Shared;
using LookPage = Neelam.Web.Components.Pages.Look;
using ImagesPage = Neelam.Web.Components.Pages.Images;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Phase 2 of concurrent edits (2026-10-11), on the pages: two tabs on one template, look, baseline,
/// ladders, image library or campaign, each the page held as its circuit holds it
/// (<see cref="OpenCampaignPage"/>), over the app's in-memory storage. A change "from another instance"
/// is one written straight through the store, which raises no signal; each page's clock moves only when
/// told, so a notice that shows without it came through the signal. Read by named elements.
/// </summary>
internal static class StalePages
{
    public static TimeSpan Interval(InMemoryApp app) =>
        app.Services.GetRequiredService<IOptions<CampaignWatchOptions>>().Value.Interval;

    public static SteppingClock Clock(InMemoryApp app) => new(app.Clock.Now);

    public static readonly IReadOnlyDictionary<string, object?> NoParameters = new Dictionary<string, object?>();

    // The page's timer is set once it is live; the regular check is waited for only after that.
    public static Task Live(OpenCampaignPage page, SteppingClock clock) =>
        page.WaitForAsync(_ => clock.Waiting == 1, "its timer set");

    // Each save a second after the last, as a person's would be: the app's clock stands still otherwise.
    public static ClientStores Later(InMemoryApp app)
    {
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return app.Stores;
    }
}

public class TemplatePageWatchTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<Guid> Saved(int saves = 1)
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < saves; i++) await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        return id;
    }

    private Task<OpenCampaignPage> Open(Guid id, SteppingClock clock) =>
        OpenCampaignPage.OpenAsync<TemplatePage>(app, new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)id }, clock);

    private const string SavedAgain = "The template was saved again since you opened it, in another tab or by someone else.";

    [Fact]
    public async Task A_save_in_another_tab_shows_modified_at_once_through_the_signal()
    {
        var id = await Saved();
        var clock = StalePages.Clock(app);
        await using var a = await Open(id, clock);
        await using var b = await Open(id, clock);
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "modified-notice"));

        await b.ClickAsync("save-button");

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal(SavedAgain, RenderedPage.Named(html, "modified-what"));
        Assert.Equal("Load the latest", RenderedPage.Named(html, "load-latest"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-unsaved"));
        // The tab that saved is told nothing about its own save.
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), "modified-notice"));
    }

    [Fact]
    public async Task With_no_signal_the_regular_check_says_modified_within_its_interval()
    {
        var id = await Saved();
        var clock = StalePages.Clock(app);
        await using var a = await Open(id, clock);
        await StalePages.Live(a, clock);

        await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        clock.Advance(StalePages.Interval(app) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "modified-notice"));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(SavedAgain, RenderedPage.Named(await a.WaitForAsync("modified-notice"), "modified-what"));
    }

    // The destructive case: a stale tab's undo would have undone a middle version, then swept it for good.
    [Fact]
    public async Task Undo_from_a_tab_that_has_not_seen_the_newest_is_refused_and_undoes_nothing()
    {
        var id = await Saved(saves: 2);
        var clock = StalePages.Clock(app);
        await using var a = await Open(id, clock);
        // Saved on another instance: no signal, and the page's clock has not moved, so the page cannot know.
        await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "modified-notice"));

        await a.ClickAsync("undo-button");
        await a.ClickAsync("undo-confirm");

        var html = await a.HtmlAsync();
        Assert.Equal("Not undone: the template was saved again since you opened it. Undo takes back only the newest version, " +
                     "so load the latest above first.", RenderedPage.Named(html, "page-message"));
        // And it says so at the top, from the read after the click.
        Assert.Equal(SavedAgain, RenderedPage.Named(html, "modified-what"));
        Assert.Equal(3, (await Store.HistoryAsync(DocumentKind.Template, id)).Count);
        Assert.Null(await Store.RestorableAsync(DocumentKind.Template, id));
    }

    [Fact]
    public async Task What_she_typed_stays_until_she_loads_the_latest()
    {
        var id = await Saved();
        var clock = StalePages.Clock(app);
        await using var a = await Open(id, clock);
        await using var b = await Open(id, clock);
        await a.TypeAsync("template-name", "Mine, not saved");
        await b.TypeAsync("template-name", "Theirs, saved");
        await b.ClickAsync("save-button");

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("Mine, not saved", RenderedPage.Attribute(html, "template-name", "value"));
        Assert.Equal("Your changes on this page are not saved. Loading the latest replaces them, so copy anything you want to keep first.",
            RenderedPage.Named(html, "modified-unsaved"));

        await a.ClickAsync("load-latest");

        html = await a.HtmlAsync();
        Assert.Equal("Theirs, saved", RenderedPage.Attribute(html, "template-name", "value"));
        Assert.Equal("The latest version is open.", RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
        // And from the latest, undo works on it.
        await a.ClickAsync("undo-button");
        await a.ClickAsync("undo-confirm");
        Assert.Equal("The last save was undone; the version before it is open.", RenderedPage.Named(await a.HtmlAsync(), "page-message"));
    }

    [Fact]
    public async Task In_one_tab_save_and_undo_go_on_as_before()
    {
        var id = await Saved();
        var clock = StalePages.Clock(app);
        await using var a = await Open(id, clock);

        await a.ClickAsync("save-button");
        var html = await a.HtmlAsync();
        Assert.StartsWith("Saved at", RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));

        await a.ClickAsync("undo-button");
        await a.ClickAsync("undo-confirm");

        html = await a.HtmlAsync();
        Assert.Equal("The last save was undone; the version before it is open.", RenderedPage.Named(html, "page-message"));
        Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
        Assert.Single(await Store.HistoryAsync(DocumentKind.Template, id));
    }
}

public class LookPageWatchTests(DemoApp app) : IClassFixture<DemoApp>
{
    private static readonly ClientLook Green = new(accentColour: "#225e3e");

    private Task<OpenCampaignPage> Open(SteppingClock clock) =>
        OpenCampaignPage.OpenAsync<LookPage>(app, StalePages.NoParameters, clock);

    [Fact]
    public async Task A_look_saved_elsewhere_shows_modified_at_once_through_the_signal()
    {
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "modified-notice"));

        await StalePages.Later(app).SaveLookAsync(DemoApp.Client, ClientLookTests.Blush, Actor.Demo);

        var html = await a.WaitForAsync("modified-notice");
        Assert.Equal("Your look was changed since you opened this page, in another tab or by someone else.",
            RenderedPage.Named(html, "modified-what"));
        Assert.Equal("Load the latest", RenderedPage.Named(html, "load-latest"));
    }

    [Fact]
    public async Task With_no_signal_the_regular_check_says_modified_within_its_interval()
    {
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        await StalePages.Live(a, clock);

        await StalePages.Later(app).Look(DemoApp.Client).SaveAsync(Green);
        clock.Advance(StalePages.Interval(app) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "modified-notice"));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Your look was changed since you opened this page, in another tab or by someone else.",
            RenderedPage.Named(await a.WaitForAsync("modified-notice"), "modified-what"));
    }

    // Undo used to take back whatever look was newest, even one saved in another tab this page never showed.
    [Fact]
    public async Task Undo_from_a_page_that_has_not_seen_the_newest_look_is_refused_and_undoes_nothing()
    {
        await StalePages.Later(app).SaveLookAsync(DemoApp.Client, Green, Actor.Demo);
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        // Saved on another instance: no signal, and the page's clock has not moved.
        var theirs = await StalePages.Later(app).Look(DemoApp.Client).SaveAsync(ClientLookTests.Blush);

        await a.ClickAsync("look-undo");
        await a.ClickAsync("look-undo-confirm");

        var html = await a.HtmlAsync();
        Assert.Equal("Not undone: your look was changed since you opened this page. Undo takes back only the newest version, " +
                     "so load the latest above first.", RenderedPage.Named(html, "look-message"));
        Assert.Equal(1, RenderedPage.Count(html, "modified-notice"));
        Assert.Equal(theirs, await app.Stores.Look(DemoApp.Client).NewestAsync());
        Assert.Null((await app.Stores.LookHistoryAsync(DemoApp.Client)).Restorable);
    }
}

public class BaselineSectionWatchTests(DemoApp app) : IClassFixture<DemoApp>
{
    private const string Changed =
        "The parts every template should have were changed since you opened this page, in another tab or by someone else.";

    private Task<OpenCampaignPage> Open(SteppingClock clock) =>
        OpenCampaignPage.OpenAsync<BaselineSection>(app, StalePages.NoParameters, clock);

    private Task<DocumentVersion?> Newest() => app.Stores.Baseline(DemoApp.Client).NewestAsync();

    [Fact]
    public async Task A_baseline_saved_elsewhere_shows_modified_at_once_through_the_signal()
    {
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);

        await StalePages.Later(app).SaveBaselineAsync(DemoApp.Client, new TemplateBaseline([BlockType.Offer]), Actor.Demo);

        var html = await a.WaitForAsync("baseline-modified");
        Assert.Equal(Changed, RenderedPage.Named(html, "baseline-modified-what"));
        Assert.Equal(0, RenderedPage.Count(html, "baseline-keep-mine"));
        await a.ClickAsync("baseline-load-latest");
        html = await a.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "baseline-modified"));
        Assert.StartsWith("Your own baseline.", RenderedPage.Named(html, "baseline-status"));
        Assert.Equal("The latest version is open.", RenderedPage.Named(html, "baseline-message"));
    }

    [Fact]
    public async Task With_no_signal_the_regular_check_says_modified_within_its_interval()
    {
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        await StalePages.Live(a, clock);

        await StalePages.Later(app).Baseline(DemoApp.Client).SaveAsync(new TemplateBaseline([BlockType.Greeting]));
        clock.Advance(StalePages.Interval(app) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), "baseline-modified"));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Changed, RenderedPage.Named(await a.WaitForAsync("baseline-modified"), "baseline-modified-what"));
    }

    // A save from a page that has not seen the newest: refused at the click, nothing saved; Keep mine
    // saves hers as the newest, and theirs stays stored.
    [Fact]
    public async Task A_save_from_a_stale_page_is_refused_until_she_keeps_hers()
    {
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        var theirs = await StalePages.Later(app).Baseline(DemoApp.Client).SaveAsync(new TemplateBaseline([BlockType.Offer]));
        app.Clock.Now += TimeSpan.FromSeconds(1);

        await a.ClickAsync("baseline-save-empty");

        var html = await a.HtmlAsync();
        Assert.Equal("Not saved: the parts every template should have were changed since you opened this page.",
            RenderedPage.Named(html, "baseline-refused"));
        Assert.Equal(Changed, RenderedPage.Named(html, "baseline-modified-what"));
        Assert.Equal(theirs, await Newest());

        await a.ClickAsync("baseline-keep-mine");

        html = await a.HtmlAsync();
        Assert.Equal("Saved empty: templates get no baseline warnings.", RenderedPage.Named(html, "baseline-message"));
        Assert.Equal(0, RenderedPage.Count(html, "baseline-modified"));
        var inForce = await app.Stores.BaselineInForceAsync(DemoApp.Client);
        Assert.Empty(inForce.Baseline.Parts);
        Assert.Contains(theirs, await app.Stores.Baseline(DemoApp.Client).HistoryAsync());
    }

    // "Use the standard baseline" deleted every version, those this page never showed included. From a
    // stale page it is refused; from the latest it saves a version saying so, and deletes nothing.
    [Fact]
    public async Task Use_the_standard_from_a_stale_page_is_refused_and_from_the_latest_deletes_nothing()
    {
        await StalePages.Later(app).SaveBaselineAsync(DemoApp.Client, new TemplateBaseline([BlockType.Greeting]), Actor.Demo);
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        var theirs = await StalePages.Later(app).Baseline(DemoApp.Client).SaveAsync(new TemplateBaseline([BlockType.Offer]));
        app.Clock.Now += TimeSpan.FromSeconds(1);

        await a.ClickAsync("baseline-use-standard");
        Assert.Equal("Not saved: the parts every template should have were changed since you opened this page.",
            RenderedPage.Named(await a.HtmlAsync(), "baseline-refused"));
        Assert.Equal(theirs, await Newest());
        var before = (await app.Stores.Baseline(DemoApp.Client).HistoryAsync()).Count;

        await a.ClickAsync("baseline-load-latest");
        await a.ClickAsync("baseline-use-standard");

        var html = await a.HtmlAsync();
        Assert.Equal("The standard baseline applies again.", RenderedPage.Named(html, "baseline-message"));
        Assert.StartsWith("The standard baseline.", RenderedPage.Named(html, "baseline-status"));
        var history = await app.Stores.Baseline(DemoApp.Client).HistoryAsync();
        Assert.Equal(before + 1, history.Count);
        Assert.Contains(theirs, history);
        Assert.Equal(0, RenderedPage.Count(html, "baseline-use-standard"));
    }
}

public class LaddersSectionWatchTests(DemoApp app) : IClassFixture<DemoApp>
{
    private const string Changed = "Your tier name ladders were changed since you opened this page, in another tab or by someone else.";

    private static readonly TierLadders Glow = new([new TierLadder("Glow", ["Glow", "Radiance", "Luminous"], ordered: true)]);

    private Task<OpenCampaignPage> Open(SteppingClock clock) =>
        OpenCampaignPage.OpenAsync<LaddersSection>(app, StalePages.NoParameters, clock);

    [Fact]
    public async Task Ladders_saved_elsewhere_show_modified_through_the_signal_and_the_regular_check()
    {
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        await StalePages.Live(a, clock);

        await StalePages.Later(app).SaveLaddersAsync(DemoApp.Client, Glow, Actor.Demo);
        Assert.Equal(Changed, RenderedPage.Named(await a.WaitForAsync("ladders-modified"), "ladders-modified-what"));

        var bClock = StalePages.Clock(app);
        await using var b = await Open(bClock);
        await StalePages.Live(b, bClock);
        await StalePages.Later(app).Ladders(DemoApp.Client).SaveAsync(TierLadders.None);
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), "ladders-modified"));
        bClock.Advance(StalePages.Interval(app));
        Assert.Equal(Changed, RenderedPage.Named(await b.WaitForAsync("ladders-modified"), "ladders-modified-what"));
    }

    [Fact]
    public async Task A_save_from_a_stale_page_is_refused_until_she_keeps_hers()
    {
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        var theirs = await StalePages.Later(app).Ladders(DemoApp.Client).SaveAsync(Glow);
        app.Clock.Now += TimeSpan.FromSeconds(1);

        await a.ClickAsync("ladders-save-own");

        var html = await a.HtmlAsync();
        Assert.Equal("Not saved: your tier name ladders were changed since you opened this page.",
            RenderedPage.Named(html, "ladders-refused"));
        Assert.Equal(theirs, await app.Stores.Ladders(DemoApp.Client).NewestAsync());

        await a.ClickAsync("ladders-keep-mine");

        html = await a.HtmlAsync();
        Assert.Equal("Saved as your own ladders.", RenderedPage.Named(html, "ladders-message"));
        Assert.Equal(0, RenderedPage.Count(html, "ladders-modified"));
        Assert.NotEqual(theirs, await app.Stores.Ladders(DemoApp.Client).NewestAsync());
        Assert.Contains(theirs, await app.Stores.Ladders(DemoApp.Client).HistoryAsync());
    }

    [Fact]
    public async Task Use_the_standard_ladders_from_the_latest_deletes_nothing()
    {
        await StalePages.Later(app).SaveLaddersAsync(DemoApp.Client, Glow, Actor.Demo);
        var clock = StalePages.Clock(app);
        await using var a = await Open(clock);
        var before = (await app.Stores.Ladders(DemoApp.Client).HistoryAsync()).Count;
        app.Clock.Now += TimeSpan.FromSeconds(1);

        await a.ClickAsync("ladders-use-standard");

        var html = await a.HtmlAsync();
        Assert.Equal("The standard ladders apply again.", RenderedPage.Named(html, "ladders-message"));
        Assert.StartsWith("The standard ladders.", RenderedPage.Named(html, "ladders-status"));
        Assert.Equal(before + 1, (await app.Stores.Ladders(DemoApp.Client).HistoryAsync()).Count);
    }
}

public class ImagesPageStaleDeleteTests(DemoApp app) : IClassFixture<DemoApp>
{
    private const string Toasting = "https://postoffice-production-f.squarecdn.com/images/principals-toasting.jpg";
    private static readonly BinaryData Png = new(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

    private ImageLibrary Library => app.Stores.Images(DemoApp.Client, Actor.Demo);

    private Task<OpenCampaignPage> Open() => OpenCampaignPage.OpenAsync<ImagesPage>(app, StalePages.NoParameters);

    // Two tabs on the library: B deletes "Hero" and adds a different photo under the same name; A, still
    // showing the first, deletes "Hero". Deleting by name alone used to remove B's new photo.
    [Fact]
    public async Task Deleting_a_photo_replaced_under_the_same_name_in_another_tab_deletes_nothing()
    {
        await Library.AddFromSquareAsync("Hero", Toasting);
        await using var a = await Open();
        await Library.DeleteListedAsync("Hero");
        await Library.AddAsync("Hero", Png, "image/png");

        await a.ClickAsync("library-delete-0");
        await a.ClickAsync("library-delete-confirm-0");

        Assert.Equal("Not deleted: \"Hero\" was replaced by another photo of the same name since this page opened, in another tab " +
                     "or by someone else. It is shown below as it is now; delete it again if you still want it gone.",
            RenderedPage.Named(await a.HtmlAsync(), "library-status"));
        var hero = Assert.Single(await Library.ListAsync());
        Assert.False(hero.IsOnSquare);

        // Shown as it is now, it deletes as before.
        await a.ClickAsync("library-delete-0");
        await a.ClickAsync("library-delete-confirm-0");

        Assert.Equal("\"Hero\" is deleted.", RenderedPage.Named(await a.HtmlAsync(), "library-status"));
        Assert.Empty(await Library.ListAsync());
    }
}

public class CampaignPageStaleUndoTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);

    // A stale tab's undo would have undone the version on it, by then a middle version: not restorable,
    // and swept for good after a day. It is refused, and nothing is undone.
    [Fact]
    public async Task Undo_from_a_tab_that_has_not_seen_the_newest_is_refused_and_undoes_nothing()
    {
        var id = Guid.NewGuid();
        await Store.SaveDraftAsync(id, "First", DraftFixtures.Finished());
        await Store.SaveDraftAsync(id, "Second", DraftFixtures.Finished());
        await using var a = await OpenCampaignPage.OpenAsync(app, id, StalePages.Clock(app));
        // Saved on another instance: no signal, and the page's clock has not moved, so the page cannot know.
        await Store.SaveDraftAsync(id, "Third", DraftFixtures.Finished());

        await a.ClickAsync("undo-button");
        await a.ClickAsync("undo-confirm");

        var html = await a.HtmlAsync();
        Assert.Equal("Not undone: the campaign was saved again since you opened it. Undo takes back only the newest version, " +
                     "so load the latest above first.", RenderedPage.Named(html, "page-message"));
        Assert.Equal("The campaign was saved again since you opened it, in another tab or by someone else.",
            RenderedPage.Named(html, "modified-what"));
        Assert.Equal(["Third", "Second", "First"], (await Store.HistoryAsync(DocumentKind.Draft, id)).Select(s => s.Title));
        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, id));
    }
}
