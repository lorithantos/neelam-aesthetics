using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The template page's one choice per block (owner, 2026-11-03: "These are check boxes and that's not
/// how check boxes work either. But the real problem is that there is no "default text""): "What each
/// campaign gets" as radio buttons under a legend, "Every campaign must have it" as its own checkbox, and
/// content to start with typed into the same fields as fixed content, or for an offer the campaign page's
/// own offer fields. The template editor held as its circuit holds it (<see cref="OpenCampaignPage"/>),
/// read by named elements.
/// </summary>
public class TemplateDefaultPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    // In the membership template.
    private const int Spacer = 1;
    private const int Headline = 2;
    private const int Greeting = 3;
    private const int Offer = 5;

    private static CampaignStore Store(InMemoryApp app) => StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<(Guid Id, OpenCampaignPage Page)> OpenMembershipAsync()
    {
        var id = Guid.NewGuid();
        await Store(app).SaveTemplateAsync(id, DraftFixtures.Membership);
        var page = await OpenCampaignPage.OpenAsync<TemplatePage>(app,
            new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)id }, StalePages.Clock(app));
        return (id, page);
    }

    // The radio button named testId is the one chosen.
    private static bool Chosen(string html, string testId) =>
        Regex.Match(html, $"<input\\b[^>]*\\sdata-testid=\"{Regex.Escape(testId)}\"[^>]*>") is { Success: true } tag
            ? Regex.IsMatch(tag.Value, "\\schecked[\\s>]")
            : throw new InvalidOperationException($"No {testId} on the page.");

    [Fact]
    public async Task What_each_campaign_gets_is_one_choice_and_must_have_it_is_its_own()
    {
        var (_, page) = await OpenMembershipAsync();
        await using var _ = page;
        var html = await page.HtmlAsync();

        Assert.StartsWith("What each campaign gets This, the same in every campaign This to start with; each campaign can change it Nothing; each campaign fills it in",
            RenderedPage.Named(html, $"content-choice-{Greeting}"));
        Assert.Equal("radio", RenderedPage.Attribute(html, $"content-fixed-{Greeting}", "type"));
        Assert.Equal(
            [true, false, false],
            new[] { "fixed", "default", "empty" }.Select(c => Chosen(html, $"content-{c}-{Greeting}")));
        // Every block with content says whether every campaign must have it, a fixed one included (owner,
        // 2026-11-03: "Any fixed block can be removed"); this was 0 while a fixed block hid it.
        Assert.Equal(1, RenderedPage.Count(html, $"required-{Greeting}"));
        Assert.True(Chosen(html, $"content-empty-{Headline}"));
        Assert.Equal(1, RenderedPage.Count(html, $"required-{Headline}"));
        // An offer is never fixed; a spacer has nothing to choose.
        Assert.Equal(0, RenderedPage.Count(html, $"content-fixed-{Offer}"));
        Assert.Equal(1, RenderedPage.Count(html, $"content-default-{Offer}"));
        Assert.Equal(0, RenderedPage.Count(html, $"content-choice-{Spacer}"));

        // Choosing to start with something keeps "must have it", where the two checkboxes hid it.
        await page.ChooseAsync($"content-default-{Headline}", "on");
        html = await page.HtmlAsync();
        Assert.True(Chosen(html, $"content-default-{Headline}"));
        Assert.Equal(1, RenderedPage.Count(html, $"required-{Headline}"));
    }

    // Fixed means the same text in every campaign, not that the block must be there (owner, 2026-11-03:
    // "Any fixed block can be removed"): the fixed greeting offers "Every campaign must have it", ticked as
    // saved; unticked, the page says what happens while campaigns cannot leave a block out, and the
    // template saves it fixed and not required. A campaign started from it still gets the greeting.
    [Fact]
    public async Task A_fixed_block_can_be_marked_not_required_and_is_saved_so()
    {
        (Guid id, OpenCampaignPage page) = await this.OpenMembershipAsync();
        await using OpenCampaignPage _ = page;
        string html = await page.HtmlAsync();
        Assert.True(Chosen(html, $"content-fixed-{Greeting}"));
        Assert.True(Chosen(html, $"required-{Greeting}"));
        Assert.Equal(0, RenderedPage.Count(html, $"fixed-optional-note-{Greeting}"));

        await page.TickAsync($"required-{Greeting}", false);

        html = await page.HtmlAsync();
        Assert.False(Chosen(html, $"required-{Greeting}"));
        Assert.Equal(
            "Campaigns can't leave a block out yet, so for now this one still goes out in every campaign.",
            RenderedPage.Named(html, $"fixed-optional-note-{Greeting}"));
        CampaignStore store = Store(app);
        await page.ClickAsync("save-button");

        CampaignTemplate saved = await store.LoadTemplateAsync((await store.NewestAsync(DocumentKind.Template, id))!);
        TemplateBlock greeting = saved.Blocks[Greeting];
        Assert.NotNull(greeting.Fixed);
        Assert.False(greeting.Required);
        Slot<string> started = (await DraftSession.StartAsync(store, id))!.Editor.Draft.Text(greeting.Label);
        Assert.Equal(Origin.Template, started.Origin);
        Assert.Equal(DraftFixtures.Membership.Blocks[Greeting].Fixed, saved.Blocks[Greeting].Fixed);
    }

    [Fact]
    public async Task A_headline_to_start_with_is_typed_shown_saved_and_starts_each_new_campaign()
    {
        var (id, page) = await OpenMembershipAsync();
        await using var _ = page;

        await page.ChooseAsync($"content-default-{Headline}", "on");
        await page.TypeAsync($"text-{Headline}", "NEWS FROM NEELAM");

        var html = await page.HtmlAsync();
        Assert.Equal("Each campaign starts with this, and can change it: NEWS FROM NEELAM", RenderedPage.Named(html, $"default-preview-{Headline}"));
        Assert.Contains("NEWS FROM NEELAM", RenderedPage.Named(html, "template-preview"));
        Assert.DoesNotContain("‹Headline: written for each campaign›", RenderedPage.Named(html, "template-preview"));

        var store = Store(app);
        await page.ClickAsync("save-button");

        var saved = await store.LoadTemplateAsync((await store.NewestAsync(DocumentKind.Template, id))!);
        Assert.NotNull(saved.Blocks[Headline].Default);
        var campaign = (await DraftSession.StartAsync(store, id))!.Editor.Draft.Text("Headline");
        Assert.Equal(("NEWS FROM NEELAM", Origin.Entered), (campaign.Value, campaign.Origin));
    }

    [Fact]
    public async Task An_offer_to_start_with_is_written_in_the_campaign_page_s_offer_fields()
    {
        var (id, page) = await OpenMembershipAsync();
        await using var _ = page;

        await page.ChooseAsync($"content-default-{Offer}", "on");
        var html = await page.HtmlAsync();
        // Nothing in it yet: it cannot be saved so.
        Assert.Contains("Offer: Fill in something for each campaign to start with, or let each campaign fill it in.", RenderedPage.Text(html));

        await page.TypeAsync($"offer-name-{Offer}", "Beauty Bank");

        html = await page.HtmlAsync();
        Assert.DoesNotContain("Fill in something for each campaign to start with", RenderedPage.Text(html));
        var preview = RenderedPage.Named(html, $"default-preview-{Offer}");
        Assert.StartsWith("Each campaign starts with this, and can change it:", preview);
        Assert.Contains("Beauty Bank", preview);
        Assert.Contains("‹Offer summary: not filled in yet›", preview);

        var store = Store(app);
        await page.ClickAsync("save-button");

        var offer = (await DraftSession.StartAsync(store, id))!.Editor.Draft.Offer("Offer");
        Assert.Equal(("Beauty Bank", Origin.Entered), (offer.Name.Value, offer.Name.Origin));
        Assert.True(offer.IsRecurring);
        Assert.Empty(offer.Tiers);
    }
}
