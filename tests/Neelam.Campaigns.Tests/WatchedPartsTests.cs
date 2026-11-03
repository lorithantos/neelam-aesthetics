using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Components.Shared;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The shared pieces of the pages that watch storage (2026-11-03), each held alone as a circuit holds it:
/// the "modified" notice in each page's variant, and Undo and Restore. The pages' own suites
/// (<see cref="TemplatePageWatchTests"/> and the rest in StalePageTests) hold them in place.
/// </summary>
public class ModifiedNoticeTests(DemoApp app) : IClassFixture<DemoApp>
{
    private int _loads;
    private int _kept;

    private Task<OpenCampaignPage> Open(params (string Name, object? Value)[] parameters)
    {
        var all = new Dictionary<string, object?>
        {
            [nameof(ModifiedNotice.OnLoadLatest)] = new EventCallback(null, (Action)(() => _loads++)),
            [nameof(ModifiedNotice.OnKeepMine)] = new EventCallback(null, (Action)(() => _kept++)),
        };
        foreach (var (name, value) in parameters) all[name] = value;
        return OpenCampaignPage.OpenAsync<ModifiedNotice>(app, all);
    }

    [Fact]
    public async Task With_nothing_changed_and_nothing_refused_there_is_no_notice()
    {
        await using var notice = await Open((nameof(ModifiedNotice.Words), ChangeWords.Campaign));

        Assert.Equal("", await notice.HtmlAsync());
    }

    // The campaign page's: Keep mine beside Load the latest when the campaign was saved again or the
    // page's version undone, its help under them; not for an approval changed elsewhere.
    [Theory]
    [InlineData(CampaignChange.SavedSince, true)]
    [InlineData(CampaignChange.Undone, true)]
    [InlineData(CampaignChange.ApprovalWithdrawn, false)]
    [InlineData(CampaignChange.Approved, false)]
    [InlineData(CampaignChange.Deleted, false)]
    public async Task Keep_mine_on_a_change_is_offered_only_where_there_is_a_version_to_keep(CampaignChange change, bool offered)
    {
        await using var notice = await Open(
            (nameof(ModifiedNotice.Words), ChangeWords.Campaign), (nameof(ModifiedNotice.Change), change),
            (nameof(ModifiedNotice.KeepMine), KeepMineWhen.OnChange), (nameof(ModifiedNotice.KeepMineLabel), "Use this version again"),
            (nameof(ModifiedNotice.KeepMineHelp), "What it does."));

        var html = await notice.HtmlAsync();
        Assert.Equal(1, RenderedPage.Count(html, "modified-notice"));
        Assert.Equal(ChangeWords.Campaign.Notice(change), RenderedPage.Named(html, "modified-what"));
        Assert.Equal(offered ? 1 : 0, RenderedPage.Count(html, "keep-mine"));
        Assert.Equal(offered ? 1 : 0, RenderedPage.Count(html, "keep-mine-help"));
        if (!offered) return;
        Assert.Equal("Use this version again", RenderedPage.Named(html, "keep-mine"));
        Assert.Equal("What it does.", RenderedPage.Named(html, "keep-mine-help"));

        await notice.ClickAsync("keep-mine");
        await notice.ClickAsync("load-latest");
        Assert.Equal((1, 1), (_kept, _loads));
    }

    // The template and Look pages': never Keep mine; what loading does to unsaved work above the button.
    [Fact]
    public async Task Without_keep_mine_it_says_what_loading_replaces()
    {
        await using var notice = await Open(
            (nameof(ModifiedNotice.Words), ChangeWords.Template), (nameof(ModifiedNotice.Change), CampaignChange.SavedSince),
            (nameof(ModifiedNotice.Unsaved), ModifiedNotice.ChangesNotSaved));

        var html = await notice.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "keep-mine"));
        Assert.Equal(ModifiedNotice.ChangesNotSaved, RenderedPage.Named(html, "modified-unsaved"));
        Assert.Equal("Load the latest", RenderedPage.Named(html, "load-latest"));
        Assert.StartsWith("<section class=\"card\" role=\"alert\" data-testid=\"modified-notice\">", html);
    }

    // The baseline and ladders sections': inside their own card, under their own names, and Keep mine
    // only once a save of theirs was refused, which shows even before the read says what changed.
    [Fact]
    public async Task After_a_refusal_keep_mine_is_offered_under_the_section_s_own_names()
    {
        (string, object?)[] Section(CampaignChange change, string? refused) =>
        [
            (nameof(ModifiedNotice.Words), ChangeWords.Baseline), (nameof(ModifiedNotice.Change), change),
            (nameof(ModifiedNotice.Refused), refused), (nameof(ModifiedNotice.KeepMine), KeepMineWhen.AfterRefusal),
            (nameof(ModifiedNotice.Prefix), "baseline-"), (nameof(ModifiedNotice.TestId), "baseline-modified"),
            (nameof(ModifiedNotice.Inline), true), (nameof(ModifiedNotice.Help), "Help."),
        ];

        await using (var changed = await Open(Section(CampaignChange.SavedSince, refused: null)))
        {
            var html = await changed.HtmlAsync();
            Assert.StartsWith("<div class=\"card\" role=\"alert\" data-testid=\"baseline-modified\">", html);
            Assert.Equal(ChangeWords.Baseline.Notice(CampaignChange.SavedSince), RenderedPage.Named(html, "baseline-modified-what"));
            Assert.Equal(0, RenderedPage.Count(html, "baseline-keep-mine"));
            Assert.Equal(0, RenderedPage.Count(html, "modified-notice"));
            Assert.Contains("<p class=\"field-help\">Help.</p>", html);
        }

        await using var refused = await Open(Section(CampaignChange.None, refused: "Not saved: why."));
        var after = await refused.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(after, "baseline-modified-what"));
        Assert.Equal("Not saved: why.", RenderedPage.Named(after, "baseline-refused"));
        Assert.Equal("Keep mine", RenderedPage.Named(after, "baseline-keep-mine"));
        await refused.ClickAsync("baseline-load-latest");
        Assert.Equal(1, _loads);
    }

    [Fact]
    public async Task Never_offers_keep_mine_even_after_a_refusal()
    {
        await using var notice = await Open(
            (nameof(ModifiedNotice.Words), ChangeWords.Look), (nameof(ModifiedNotice.Change), CampaignChange.SavedSince),
            (nameof(ModifiedNotice.Refused), "Not saved: why."));

        Assert.Equal(0, RenderedPage.Count(await notice.HtmlAsync(), "keep-mine"));
    }
}

public class UndoRestoreTests(DemoApp app) : IClassFixture<DemoApp>
{
    private static readonly LocalTime Times = new(TimeZoneInfo.Utc);
    private static readonly DateTimeOffset Until = new(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);

    private readonly List<bool> _asked = [];
    private int _undone;
    private int _restored;

    private Task<OpenCampaignPage> Open(params (string Name, object? Value)[] parameters)
    {
        var all = new Dictionary<string, object?>
        {
            [nameof(UndoRestore.Question)] = "The question?",
            [nameof(UndoRestore.Times)] = Times,
            [nameof(UndoRestore.ConfirmingChanged)] = EventCallback.Factory.Create<bool>(this, (bool asking) => _asked.Add(asking)),
            [nameof(UndoRestore.OnUndo)] = new EventCallback(null, (Action)(() => _undone++)),
            [nameof(UndoRestore.OnRestore)] = new EventCallback(null, (Action)(() => _restored++)),
        };
        foreach (var (name, value) in parameters) all[name] = value;
        return OpenCampaignPage.OpenAsync<UndoRestore>(app, all);
    }

    [Fact]
    public async Task Undo_asks_first_and_the_page_decides_whether_it_is_asking()
    {
        await using (var offered = await Open((nameof(UndoRestore.CanUndo), true)))
        {
            Assert.Equal("Undo last save", RenderedPage.Named(await offered.HtmlAsync(), "undo-button"));
            await offered.ClickAsync("undo-button");
            Assert.Equal([true], _asked);
        }

        await using var asking = await Open((nameof(UndoRestore.CanUndo), true), (nameof(UndoRestore.Confirming), true));
        var html = await asking.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "undo-button"));
        Assert.Contains("<p>The question?</p>", html);
        Assert.Equal("Undo it", RenderedPage.Named(html, "undo-confirm"));
        await asking.ClickAsync("undo-confirm");
        Assert.Equal(1, _undone);
    }

    [Fact]
    public async Task With_nothing_saved_there_is_nothing_to_undo_or_restore()
    {
        await using var nothing = await Open((nameof(UndoRestore.CanUndo), false));

        Assert.Equal("", await nothing.HtmlAsync());
    }

    // Undoing the only save deletes: the campaign page offers no button then, having its own Delete; the
    // template page offers it as a delete.
    [Fact]
    public async Task An_undo_that_deletes_is_offered_only_as_a_delete()
    {
        await using (var campaign = await Open((nameof(UndoRestore.CanUndo), true), (nameof(UndoRestore.UndoDeletes), true)))
            Assert.Equal(0, RenderedPage.Count(await campaign.HtmlAsync(), "undo-button"));

        await using (var template = await Open(
            (nameof(UndoRestore.CanUndo), true), (nameof(UndoRestore.UndoDeletes), true), (nameof(UndoRestore.DeleteLabel), "Delete template")))
            Assert.Equal("Delete template", RenderedPage.Named(await template.HtmlAsync(), "undo-button"));

        await using var asking = await Open(
            (nameof(UndoRestore.CanUndo), true), (nameof(UndoRestore.UndoDeletes), true), (nameof(UndoRestore.DeleteLabel), "Delete template"),
            (nameof(UndoRestore.Confirming), true));
        Assert.Equal("Delete it", RenderedPage.Named(await asking.HtmlAsync(), "undo-confirm"));
    }

    // Restore while the last undo can be taken back, until when in her zone; under each page's own names.
    [Fact]
    public async Task Restore_is_offered_until_the_grace_period_ends_under_the_page_s_names()
    {
        await using var look = await Open(
            (nameof(UndoRestore.CanUndo), true), (nameof(UndoRestore.RestorableUntil), (DateTimeOffset?)Until),
            (nameof(UndoRestore.RestoreLabel), "Restore undone look"), (nameof(UndoRestore.UndoId), "look-undo"),
            (nameof(UndoRestore.RestorableId), "look-restorable"), (nameof(UndoRestore.RestoreId), "look-restore"));

        var html = await look.HtmlAsync();
        Assert.Equal("Undo last save", RenderedPage.Named(html, "look-undo"));
        Assert.Equal($"The last undo can be taken back until {Times.DateAndTime(Until)}.", RenderedPage.Named(html, "look-restorable"));
        Assert.Equal("Restore undone look", RenderedPage.Named(html, "look-restore"));
        await look.ClickAsync("look-restore");
        Assert.Equal(1, _restored);
    }
}

/// <summary>
/// The template page's undo of its only save is a delete (owner, 2026-11-03), as the campaign page's:
/// offered as "Delete template" and asked as one, where it used to say the version before it comes back.
/// </summary>
public class TemplateUndoDeletesTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<Guid> Saved(int saves)
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < saves; i++) await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        return id;
    }

    private Task<OpenCampaignPage> Open(Guid id) =>
        OpenCampaignPage.OpenAsync<TemplatePage>(app, new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)id }, StalePages.Clock(app));

    [Fact]
    public async Task Undoing_the_only_save_is_offered_and_asked_as_a_delete()
    {
        var id = await Saved(saves: 1);
        await using var page = await Open(id);
        var html = await page.HtmlAsync();
        Assert.Equal("Delete template", RenderedPage.Named(html, "undo-button"));
        Assert.DoesNotContain("Undo last save", RenderedPage.Text(html));

        await page.ClickAsync("undo-button");

        html = await page.HtmlAsync();
        Assert.Contains("This deletes the template. You can restore it for a day.", RenderedPage.Text(html));
        Assert.DoesNotContain("The version before it comes back", RenderedPage.Text(html));
        Assert.Equal("Delete it", RenderedPage.Named(html, "undo-confirm"));

        await page.ClickAsync("undo-confirm");

        Assert.Contains("Its last save was undone, so it is gone from your templates.", RenderedPage.Text(await page.HtmlAsync()));
        Assert.Empty(await Store.HistoryAsync(DocumentKind.Template, id));
        Assert.NotNull(await Store.RestorableAsync(DocumentKind.Template, id));
    }

    [Fact]
    public async Task With_a_version_before_it_undo_is_an_undo()
    {
        await using var page = await Open(await Saved(saves: 2));
        Assert.Equal("Undo last save", RenderedPage.Named(await page.HtmlAsync(), "undo-button"));

        await page.ClickAsync("undo-button");

        var html = await page.HtmlAsync();
        Assert.Contains(ChangeWords.Template.UndoQuestion(TimeSpan.FromDays(1)), RenderedPage.Text(html));
        Assert.Equal("Undo it", RenderedPage.Named(html, "undo-confirm"));
    }

    // The session counts its saves in use, as the campaign's does: opened, saved, undone.
    [Fact]
    public async Task The_session_knows_when_an_undo_would_delete()
    {
        var catalog = app.Services.GetRequiredService<BlockCatalog>();
        var session = (await TemplateSession.OpenAsync(Store, await Saved(saves: 1), catalog))!;
        Assert.True(session.UndoDeletesTheTemplate);
        Assert.Equal("This deletes the template. You can restore it for a day.", session.UndoQuestion);

        await session.SaveAsync();
        Assert.False(session.UndoDeletesTheTemplate);
        Assert.StartsWith("Undo the last save? The version before it comes back", session.UndoQuestion);

        Assert.True(await session.UndoLastSaveAsync());
        Assert.True(session.UndoDeletesTheTemplate);

        Assert.False(TemplateSession.New(Store, catalog).UndoDeletesTheTemplate);
    }
}
