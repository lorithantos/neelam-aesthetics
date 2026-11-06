using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The compare on the campaign page, one test per way in (owner, 2026-10-31, answers 7a/b/c), driven in a
/// Blazor circuit through <see cref="OpenCampaignPage"/> over the demo app's in-memory storage: an earlier
/// save against the last one, "What haven't I saved yet?", and what a template would change before she
/// uses it. Read by named elements. None of them writes anything.
/// </summary>
public class CampaignComparePageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private static readonly LocalTime Times = LocalTime.For(LocalTime.DefaultZone);

    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<SaveRef> Later(Task<SaveRef> saving)
    {
        var save = await saving;
        app.Clock.Now += TimeSpan.FromMinutes(1);
        return save;
    }

    private static CampaignDraft Finished(string headline, params string[] opening)
    {
        var draft = DraftFixtures.Finished();
        draft.Text("Headline").Set(headline);
        if (opening.Length > 0) draft.Paragraphs("Opening").Set(opening);
        return draft;
    }

    [Fact]
    public async Task What_haven_t_I_saved_yet_shows_the_form_against_its_last_save_as_she_types()
    {
        var save = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", Finished("ONE YEAR")));
        await using var page = await OpenCampaignPage.OpenAsync(app, save.Id, new ManualClock(app.Clock.Now));
        Assert.Equal(0, RenderedPage.Count(await page.HtmlAsync(), "compare-unsaved"));

        await page.ClickAsync("compare-unsaved-button");

        var html = await page.HtmlAsync();
        Assert.Equal("What haven't I saved yet?", RenderedPage.Named(html, "compare-unsaved-button"));
        Assert.Equal("No changes since your last save.", RenderedPage.Named(html, "compare-unsaved-none"));

        await page.TypeIntoAsync("text-2", "ONE WHOLE YEAR");

        html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "compare-unsaved-none"));
        Assert.Equal($"Changed since your save at {Times.DateAndTime(save.SavedAt)}:", RenderedPage.Named(html, "compare-unsaved-heading"));
        Assert.Equal("Headline", RenderedPage.Named(html, "compare-unsaved-label-0"));
        Assert.Equal("Changed", RenderedPage.Named(html, "compare-unsaved-what-0"));
        Assert.Equal("Text", RenderedPage.Named(html, "compare-unsaved-field-0-0"));
        Assert.Equal("Saved: ONE YEAR", RenderedPage.Named(html, "compare-unsaved-before-0-0"));
        Assert.Equal("Now: ONE WHOLE YEAR", RenderedPage.Named(html, "compare-unsaved-after-0-0"));
        Assert.Contains("<ins class=\"compare\">WHOLE </ins>", html);

        // Closed on its button again; and nothing was saved by looking.
        await page.ClickAsync("compare-unsaved-button");
        Assert.Equal(0, RenderedPage.Count(await page.HtmlAsync(), "compare-unsaved"));
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id));
    }

    [Fact]
    public async Task An_earlier_save_is_compared_with_the_last_one_and_another_can_be_picked()
    {
        var id = Guid.NewGuid();
        var first = await Later(Store.SaveDraftAsync(id, "WE’RE TURNING ONE!", Finished("ONE YEAR")));
        var second = await Later(Store.SaveDraftAsync(id, "WE’RE TURNING ONE!", Finished("ONE WHOLE YEAR")));
        await Later(Store.SaveDraftAsync(id, "WE’RE TURNING ONE!", Finished("ONE WHOLE YEAR", "Thank you for a wonderful year.")));
        await using var page = await OpenCampaignPage.OpenAsync(app, id, new ManualClock(app.Clock.Now));

        await page.ClickAsync("compare-earlier-button");

        // The save before the last, to start with: only the opening changed since.
        var html = await page.HtmlAsync();
        Assert.Equal(second.BlobName, RenderedPage.Attribute(html, "compare-earlier-picker", "value"));
        Assert.Equal($"Changed since your save at {Times.DateAndTime(second.SavedAt)}, up to your last save:",
            RenderedPage.Named(html, "compare-earlier-heading"));
        Assert.Equal("Opening", RenderedPage.Named(html, "compare-earlier-label-0"));
        Assert.Equal(0, RenderedPage.Count(html, "compare-earlier-label-1"));
        Assert.Equal("Last save: Thank you for a wonderful year.", RenderedPage.Named(html, "compare-earlier-after-0-0"));

        await page.ChooseAsync("compare-earlier-picker", first.BlobName);

        html = await page.HtmlAsync();
        Assert.Equal($"Changed since your save at {Times.DateAndTime(first.SavedAt)}, up to your last save:",
            RenderedPage.Named(html, "compare-earlier-heading"));
        Assert.Equal("Headline", RenderedPage.Named(html, "compare-earlier-label-0"));
        Assert.Equal("Then: ONE YEAR", RenderedPage.Named(html, "compare-earlier-before-0-0"));
        Assert.Equal("Last save: ONE WHOLE YEAR", RenderedPage.Named(html, "compare-earlier-after-0-0"));
        Assert.Equal("Opening", RenderedPage.Named(html, "compare-earlier-label-1"));
        // Looking reads, and stores nothing.
        Assert.Equal(3, (await Store.HistoryAsync(DocumentKind.Draft, id)).Count);
    }

    [Fact]
    public async Task A_campaign_with_one_save_offers_no_earlier_save_to_compare()
    {
        var save = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", Finished("ONE YEAR")));
        await using var page = await OpenCampaignPage.OpenAsync(app, save.Id, new ManualClock(app.Clock.Now));

        var html = await page.HtmlAsync();

        Assert.Equal(1, RenderedPage.Count(html, "compare-unsaved-button"));
        Assert.Equal(0, RenderedPage.Count(html, "compare-earlier-button"));
    }

    [Fact]
    public async Task Update_to_template_shows_what_it_would_change_before_she_uses_it()
    {
        var template = Guid.NewGuid();
        var monthly = new CampaignTemplate("Membership, monthly", DraftFixtures.Membership.Blocks);
        var first = await Later(Store.SaveTemplateAsync(template, monthly));
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!",
            TemplateUpdate.Of(DraftFixtures.Finished(), monthly, template, first.SavedAt).Draft));
        await Later(Store.SaveTemplateAsync(template, new CampaignTemplate("Membership, monthly",
            monthly.Blocks.Where(b => b.Label != "Photo")
                .Select(b => b.Label == "Greeting" ? b with { Fixed = new GreetingBlock("Greeting", "Hello lovely 🤍") } : b).ToList())));
        await using var page = await OpenCampaignPage.OpenAsync(app, campaign.Id, new ManualClock(app.Clock.Now));

        await page.ClickAsync("template-update");

        var html = await page.HtmlAsync();
        Assert.Equal("Using the template Membership, monthly changes this campaign like this:", RenderedPage.Named(html, "compare-template-heading"));
        Assert.Equal("Greeting", RenderedPage.Named(html, "compare-template-label-0"));
        Assert.Equal("Text · The template's fixed text", RenderedPage.Named(html, "compare-template-field-0-0"));
        Assert.Equal("Now: Hi Beautiful 🤍", RenderedPage.Named(html, "compare-template-before-0-0"));
        Assert.Equal("With the template: Hello lovely 🤍", RenderedPage.Named(html, "compare-template-after-0-0"));
        Assert.Equal("Photo", RenderedPage.Named(html, "compare-template-label-1"));
        Assert.Equal("Removed", RenderedPage.Named(html, "compare-template-what-1"));
        // What the removed photo held, and no side for after, where it is not.
        Assert.Equal("Photo", RenderedPage.Named(html, "compare-template-field-1-0"));
        Assert.StartsWith("Now: ", RenderedPage.Named(html, "compare-template-before-1-0"));
        Assert.Equal(0, RenderedPage.Count(html, "compare-template-after-1-0"));
        // The photo she chose would go, so she is told that too; and nothing is in the form yet.
        Assert.Equal("Photo: the template has no place for it, so it goes.", RenderedPage.Named(html, "template-dropped"));
        Assert.Single(Regex.Matches(RenderedPage.Text(html), "Hello lovely"));

        // Worked out against the form as it stands: what she types while asked is hers either way.
        await page.TypeIntoAsync("text-2", "ONE WHOLE YEAR");
        html = await page.HtmlAsync();
        Assert.Equal("Greeting", RenderedPage.Named(html, "compare-template-label-0"));
        Assert.Equal(0, RenderedPage.Count(html, "compare-template-label-2"));

        await page.ClickAsync("template-confirm-use");

        html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "template-confirm"));
        Assert.True(Regex.Matches(RenderedPage.Text(html), "Hello lovely").Count >= 2);
        Assert.DoesNotContain("Hi Beautiful", RenderedPage.Text(html));
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, campaign.Id));
    }
}
