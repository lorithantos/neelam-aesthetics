using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Security;
using LookPage = Neelam.Web.Components.Pages.Look;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Two tabs on one template or on her look (EditingTabs; the owner's browser check, 2026-11-05): the other
/// tab says so as soon as this one does anything, before it has done anything itself. Every action she takes
/// is editing (owner, 2026-11-05: "It feels weird not to count loading - that's a serious update, no? Just as
/// undo is (since that's effectively a reload as is Restore)"); what a page does by itself is not. Each page
/// held as its circuit holds it (<see cref="OpenCampaignPage"/>), read by named elements. EditingTabs keeps the
/// app's clock, which moves on between a tab opening and the other one acting.
/// </summary>
internal static class OtherTabs
{
    public const string Notice = "other-tab-notice";

    public static EditingTabs Of(InMemoryApp app) => app.Services.GetRequiredService<EditingTabs>();

    // Whether a tab on this client's subject has typed, by the registry's own record: what a page does by itself
    // never marks it, so no other tab can be told of it.
    public static bool AnyTyped(InMemoryApp app, ClientName client, TabSubject subject) =>
        Of(app).Snapshot().Any(tab => tab.Client == client && tab.Subject == subject && tab.LastTyped is not null);

    public static Task<string> Told(OpenCampaignPage page) => page.WaitForAsync(Notice);

    public static Task<string> Untold(OpenCampaignPage page) =>
        page.WaitForAsync(html => RenderedPage.Count(html, Notice) == 0, "the notice gone");

    // The other tab's notice, said politely as a card, as the page's "modified" is, not as grey help.
    public static void IsTheNotice(string html)
    {
        Assert.Equal(ChangeWords.OtherTab, RenderedPage.Named(html, Notice));
        Assert.Equal("card", RenderedPage.Attribute(html, Notice, "class"));
        Assert.Equal("status", RenderedPage.Attribute(html, Notice, "role"));
    }
}

/// <summary>
/// No browser to move: what a page asks to load is recorded and the page stays as it is, as the Look page's
/// change and a new template's first save would otherwise leave nothing to look at.
/// </summary>
internal sealed class StayingNavigation : NavigationManager
{
    public StayingNavigation() => Initialize("http://localhost/", "http://localhost/");

    public List<string> Asked { get; } = [];

    protected override void NavigateToCore(string uri, NavigationOptions options) => Asked.Add(uri);
}

public class TemplateEditingTabTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<Guid> Saved(int saves = 1)
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < saves; i++) await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        return id;
    }

    // A saved template's page, or a new one's with no id; what is given goes in place of the app's own.
    private Task<OpenCampaignPage> Open(Guid? id, params object[] replacing) =>
        OpenCampaignPage.OpenAsync<TemplatePage>(app,
            id is { } saved ? new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)saved } : new Dictionary<string, object?>(),
            [.. replacing, StalePages.Clock(app), new StayingNavigation()]);

    private bool AnyTyped(Guid id) => OtherTabs.AnyTyped(app, DemoApp.Client, TabSubject.Template(id));

    // Two tabs open on the template, the app's clock moved on, and what she does in the first tells the second.
    private async Task TellsTheOtherTab(Guid id, Func<OpenCampaignPage, Task> action)
    {
        await using var a = await Open(id);
        await using var b = await Open(id);
        StalePages.Later(app);
        Assert.False(AnyTyped(id));

        await action(a);

        OtherTabs.IsTheNotice(await OtherTabs.Told(b));
    }

    // The template's and the look's words, one for both: no working copy, so what is kept is the last save
    // (a draft for Priya, 2026-11-05).
    [Fact]
    public void The_notice_says_whichever_tab_saves_last_is_kept()
    {
        Assert.Equal("Someone is also editing this in another tab. Whichever tab saves last is what's kept.", ChangeWords.OtherTab);
    }

    [Fact]
    public async Task Her_typing_tells_the_other_tab_before_it_types_and_never_her_own()
    {
        var id = await Saved();
        await using var a = await Open(id);
        await using var b = await Open(id);
        StalePages.Later(app);

        await a.TypeAsync("template-name", "Mine, not saved");

        var html = await OtherTabs.Told(b);
        OtherTabs.IsTheNotice(html);
        Assert.Equal(DraftFixtures.Membership.Name, RenderedPage.Attribute(html, "template-name", "value"));
        // Drawn again with the notice, the second tab has typed nothing, so the first is told nothing.
        Assert.Single(OtherTabs.Of(app).Snapshot(), tab => tab.Subject == TabSubject.Template(id) && tab.LastTyped is not null);
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), OtherTabs.Notice));
    }

    // Fields inside the page (a block's photo) tell the page of every change, and the page tells the other tab.
    [Fact]
    public async Task A_change_in_a_block_s_own_fields_tells_the_other_tab()
    {
        await TellsTheOtherTab(await Saved(), a => a.TypeAsync("photo-0", "Hero"));
    }

    [Fact]
    public async Task A_save_tells_the_other_tab()
    {
        await TellsTheOtherTab(await Saved(), a => a.ClickAsync("save-button"));
    }

    [Fact]
    public async Task Undo_tells_the_other_tab()
    {
        await TellsTheOtherTab(await Saved(saves: 2), async a =>
        {
            await a.ClickAsync("undo-button");
            await a.ClickAsync("undo-confirm");
        });
    }

    // Undone in the first tab before the second opened, so only the restore can tell it.
    [Fact]
    public async Task Restore_tells_the_other_tab()
    {
        var id = await Saved(saves: 2);
        await using var a = await Open(id);
        await a.ClickAsync("undo-button");
        await a.ClickAsync("undo-confirm");
        await using var b = await Open(id);
        StalePages.Later(app);

        await a.ClickAsync("restore-button");

        Assert.Equal("The undone save is back and open.", RenderedPage.Named(await a.HtmlAsync(), "page-message"));
        OtherTabs.IsTheNotice(await OtherTabs.Told(b));
    }

    [Fact]
    public async Task Load_the_latest_tells_the_other_tab()
    {
        var id = await Saved();
        await using var a = await Open(id);
        await using var b = await Open(id);
        StalePages.Later(app);
        // Saved in the second tab: the first is offered the latest, and has done nothing to tell the second.
        await b.TypeAsync("template-name", "Saved in the other tab");
        await b.ClickAsync("save-button");
        await a.WaitForAsync("load-latest");
        Assert.Single(OtherTabs.Of(app).Snapshot(), tab => tab.Subject == TabSubject.Template(id) && tab.LastTyped is not null);
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), OtherTabs.Notice));
        StalePages.Later(app);

        await a.ClickAsync("load-latest");

        Assert.Equal("Saved in the other tab", RenderedPage.Attribute(await a.HtmlAsync(), "template-name", "value"));
        OtherTabs.IsTheNotice(await OtherTabs.Told(b));
    }

    // Opened, then each page draws itself again when its regular check finds a save made elsewhere: nobody typed.
    [Fact]
    public async Task Opening_the_template_and_drawing_it_again_tell_no_one()
    {
        var id = await Saved();
        var (clockA, clockB) = (StalePages.Clock(app), StalePages.Clock(app));
        await using var a = await Open(id, clockA);
        await using var b = await Open(id, clockB);
        await StalePages.Live(clockA);
        await StalePages.Live(clockB);

        // Saved on another instance: no signal, so each page finds it at its own regular check.
        await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        clockA.Advance(StalePages.Interval(app));
        clockB.Advance(StalePages.Interval(app));
        await a.WaitForAsync("modified-notice");
        await b.WaitForAsync("modified-notice");

        Assert.Equal(2, OtherTabs.Of(app).Snapshot().Count(tab => tab.Subject == TabSubject.Template(id)));
        Assert.False(AnyTyped(id));
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), OtherTabs.Notice));
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), OtherTabs.Notice));
    }

    [Fact]
    public async Task The_notice_goes_when_the_other_tab_closes()
    {
        var id = await Saved();
        var a = await Open(id);
        var closed = false;
        try
        {
            await using var b = await Open(id);
            StalePages.Later(app);
            await a.TypeAsync("template-name", "Mine, then closed");
            await OtherTabs.Told(b);

            closed = true;
            await a.DisposeAsync();

            await OtherTabs.Untold(b);
        }
        finally
        {
            // Closed however the test ends, so no tab of it is left open for the next.
            if (!closed) await a.DisposeAsync();
        }
    }

    // A new template has nothing another tab could open until its first save gives it an id: its tab opens then,
    // on that id, and counts from there both ways.
    [Fact]
    public async Task A_new_template_s_tab_opens_once_its_first_save_gives_it_an_id()
    {
        var navigation = new StayingNavigation();
        await using var a = await Open(null, navigation);
        await a.TypeAsync("template-name", "Typed, then saved");
        Assert.DoesNotContain(OtherTabs.Of(app).Snapshot(), tab => tab.Client == DemoApp.Client && tab.Subject.Kind == "template");

        await a.ClickAsync("save-button");

        var id = Guid.Parse(Assert.Single(navigation.Asked).Split('/')[^1]);
        Assert.Equal(TabSubject.Template(id),
            Assert.Single(OtherTabs.Of(app).Snapshot(), tab => tab.Client == DemoApp.Client && tab.Subject.Kind == "template").Subject);
        await using var b = await Open(id);
        StalePages.Later(app);

        await b.TypeAsync("template-name", "Typed in the second tab");
        await OtherTabs.Told(a);

        StalePages.Later(app);
        await a.TypeAsync("template-name", "Typed again in the first");
        await OtherTabs.Told(b);
    }

    // Rendered ahead of time, the page is thrown away and never live (a request's scope, not a circuit's):
    // it opens no tab.
    [Fact]
    public async Task A_page_rendered_ahead_of_time_opens_no_tab()
    {
        var id = await Saved();
        Assert.True(OtherTabs.Of(app).IsEmpty);
        var opened = OtherTabs.Of(app).WaitForTabsAsync(CancellationToken.None);

        var response = await app.CreateClient().GetAsync($"/templates/{id}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(DraftFixtures.Membership.Name, await response.Content.ReadAsStringAsync());
        Assert.False(opened.IsCompleted);
        Assert.True(OtherTabs.Of(app).IsEmpty);
    }
}

public class LookEditingTabTests(DemoApp app) : IClassFixture<DemoApp>
{
    private static readonly ClientLook Green = new(accentColour: "#225e3e");

    private static readonly ClientName Elsewhere = new("test-salon-two");

    private Task<OpenCampaignPage> Open(params object[] replacing) =>
        OpenCampaignPage.OpenAsync<LookPage>(app, StalePages.NoParameters, [.. replacing, StalePages.Clock(app), new StayingNavigation()]);

    private bool AnyTyped(ClientName? client = null) => OtherTabs.AnyTyped(app, client ?? DemoApp.Client, TabSubject.Look);

    private async Task TellsTheOtherTab(Func<OpenCampaignPage, Task> action)
    {
        await using var a = await Open();
        await using var b = await Open();
        StalePages.Later(app);
        Assert.False(AnyTyped());

        await action(a);

        OtherTabs.IsTheNotice(await OtherTabs.Told(b));
    }

    private Task SavedLook() => StalePages.Later(app).SaveLookAsync(DemoApp.Client, Green, Actor.Demo);

    [Fact]
    public async Task Her_colour_change_tells_the_other_tab_before_it_changes_one_and_never_her_own()
    {
        await using var a = await Open();
        await using var b = await Open();
        StalePages.Later(app);
        var before = RenderedPage.Attribute(await b.HtmlAsync(), "look-hex-accent", "value");

        await a.ChooseAsync("look-hex-accent", "#225e3e");

        var html = await OtherTabs.Told(b);
        OtherTabs.IsTheNotice(html);
        Assert.Equal(before, RenderedPage.Attribute(html, "look-hex-accent", "value"));
        Assert.Single(OtherTabs.Of(app).Snapshot(), tab => tab.Subject == TabSubject.Look && tab.LastTyped is not null);
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), OtherTabs.Notice));
    }

    [Fact]
    public async Task A_save_tells_the_other_tab()
    {
        await TellsTheOtherTab(a => a.ClickAsync("look-save"));
    }

    [Fact]
    public async Task Use_the_standard_look_tells_the_other_tab()
    {
        await SavedLook();
        await TellsTheOtherTab(a => a.ClickAsync("look-use-standard"));
    }

    [Fact]
    public async Task Undo_tells_the_other_tab()
    {
        await SavedLook();
        await TellsTheOtherTab(async a =>
        {
            await a.ClickAsync("look-undo");
            await a.ClickAsync("look-undo-confirm");
        });
    }

    // Undone in a tab since closed, so only the restore can tell the other.
    [Fact]
    public async Task Restore_tells_the_other_tab()
    {
        await SavedLook();
        await using (var undoing = await Open())
        {
            await undoing.ClickAsync("look-undo");
            await undoing.ClickAsync("look-undo-confirm");
        }
        await TellsTheOtherTab(a => a.ClickAsync("look-restore"));
    }

    [Fact]
    public async Task Load_the_latest_tells_the_other_tab()
    {
        await using var a = await Open();
        await using var b = await Open();
        StalePages.Later(app);
        // Saved elsewhere: both tabs are offered the latest, and neither has done anything.
        await SavedLook();
        await a.WaitForAsync("load-latest");
        await b.WaitForAsync("load-latest");
        Assert.False(AnyTyped());
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), OtherTabs.Notice));

        await a.ClickAsync("load-latest");

        OtherTabs.IsTheNotice(await OtherTabs.Told(b));
    }

    [Fact]
    public async Task Opening_the_look_and_drawing_it_again_tell_no_one()
    {
        await using var a = await Open();
        await using var b = await Open();
        StalePages.Later(app);

        await SavedLook();
        await a.WaitForAsync("modified-notice");
        await b.WaitForAsync("modified-notice");

        Assert.Equal(2, OtherTabs.Of(app).Snapshot().Count(tab => tab.Client == DemoApp.Client && tab.Subject == TabSubject.Look));
        Assert.False(AnyTyped());
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), OtherTabs.Notice));
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), OtherTabs.Notice));
    }

    [Fact]
    public async Task The_notice_goes_when_the_other_tab_closes()
    {
        var a = await Open();
        var closed = false;
        try
        {
            await using var b = await Open();
            StalePages.Later(app);
            await a.ChooseAsync("look-hex-accent", "#225e3e");
            await OtherTabs.Told(b);

            closed = true;
            await a.DisposeAsync();

            await OtherTabs.Untold(b);
        }
        finally
        {
            if (!closed) await a.DisposeAsync();
        }
    }

    // Each client has one look, so the look's tabs are told apart by client alone: another client's editing is
    // never said here, and her own other tab still is.
    [Fact]
    public async Task One_client_s_look_editor_never_hears_another_client_s()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var elsewhere = ActivatorUtilities.CreateInstance<ClientWorkspace>(scope.ServiceProvider, new PrototypeCallerSource(Elsewhere));
        await using var a = await Open();
        await using var b = await Open();
        await using var other = await Open(elsewhere);
        StalePages.Later(app);
        var looks = OtherTabs.Of(app).Snapshot().Where(tab => tab.Subject == TabSubject.Look).ToList();
        Assert.Equal(2, looks.Count(tab => tab.Client == DemoApp.Client));
        Assert.Single(looks, tab => tab.Client == Elsewhere);

        await other.ChooseAsync("look-hex-accent", "#225e3e");

        Assert.True(AnyTyped(Elsewhere));
        // News goes to each page's own circuit as it happens, so by the time a page is read it is drawn.
        Assert.Equal(0, RenderedPage.Count(await a.HtmlAsync(), OtherTabs.Notice));
        Assert.Equal(0, RenderedPage.Count(await b.HtmlAsync(), OtherTabs.Notice));

        await a.ChooseAsync("look-hex-accent", "#225e3e");

        await OtherTabs.Told(b);
        Assert.Equal(0, RenderedPage.Count(await other.HtmlAsync(), OtherTabs.Notice));
    }

    [Fact]
    public async Task A_page_rendered_ahead_of_time_opens_no_tab()
    {
        Assert.True(OtherTabs.Of(app).IsEmpty);
        var opened = OtherTabs.Of(app).WaitForTabsAsync(CancellationToken.None);

        var response = await app.CreateClient().GetAsync("/look");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("look-sample", await response.Content.ReadAsStringAsync());
        Assert.False(opened.IsCompleted);
        Assert.True(OtherTabs.Of(app).IsEmpty);
    }
}
