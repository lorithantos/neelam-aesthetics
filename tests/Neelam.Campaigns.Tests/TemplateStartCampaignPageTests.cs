using Neelam.Campaigns.Storage;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The owner, 2026-11-03: "On Templates - it makes sense to be able to jump directly to creating a
/// campaign from that template". A new campaign starts from the template's newest save, so the editor
/// offers the link only while the page holds nothing unsaved, and says what to do otherwise. The
/// template editor held as its circuit holds it (<see cref="OpenCampaignPage"/>), read by named elements.
/// </summary>
public class TemplateStartCampaignPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private const string Unsaved = "Save your changes to start a campaign from this template.";

    private async Task<Guid> SaveMembershipAsync()
    {
        var id = Guid.NewGuid();
        await StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo).SaveTemplateAsync(id, DraftFixtures.Membership);
        return id;
    }

    private Task<OpenCampaignPage> Open(Guid id) =>
        OpenCampaignPage.OpenAsync<TemplatePage>(app,
            new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)id }, StalePages.Clock(app));

    [Fact]
    public async Task A_saved_template_links_to_a_new_campaign_from_it()
    {
        var id = await SaveMembershipAsync();
        await using var page = await Open(id);
        var html = await page.HtmlAsync();

        Assert.Equal("Start a campaign from this template", RenderedPage.Named(html, "start-campaign"));
        Assert.Equal($"campaigns/new/{id}", RenderedPage.Attribute(html, "start-campaign", "href"));
        Assert.Equal(0, RenderedPage.Count(html, "start-campaign-unsaved"));
    }

    // The link would start from the last save and leave what is typed behind without a word.
    [Fact]
    public async Task While_changes_are_not_saved_it_says_to_save_them_and_offers_no_link()
    {
        var id = await SaveMembershipAsync();
        await using var page = await Open(id);

        await page.TypeAsync("template-name", "Membership, reworded");

        var html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "start-campaign"));
        Assert.Equal(Unsaved, RenderedPage.Named(html, "start-campaign-unsaved"));

        // Saved, the link is back, to the same template.
        await page.ClickAsync("save-button");
        html = await page.HtmlAsync();
        Assert.Equal($"campaigns/new/{id}", RenderedPage.Attribute(html, "start-campaign", "href"));
        Assert.Equal(0, RenderedPage.Count(html, "start-campaign-unsaved"));
    }

    [Fact]
    public async Task A_change_taken_back_by_hand_offers_the_link_again()
    {
        await using var page = await Open(await SaveMembershipAsync());

        await page.TypeAsync("template-name", "Membership, reworded");
        await page.TypeAsync("template-name", DraftFixtures.Membership.Name);

        var html = await page.HtmlAsync();
        Assert.Equal(1, RenderedPage.Count(html, "start-campaign"));
        Assert.Equal(0, RenderedPage.Count(html, "start-campaign-unsaved"));
    }

    // A template never saved has nothing for a campaign to start from, and nothing to say about it.
    [Fact]
    public async Task A_new_template_offers_no_campaign_to_start()
    {
        await using var page = await OpenCampaignPage.OpenAsync<TemplatePage>(app,
            new Dictionary<string, object?>(), StalePages.Clock(app));
        await page.TypeAsync("template-name", "Something new");

        var html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "start-campaign"));
        Assert.Equal(0, RenderedPage.Count(html, "start-campaign-unsaved"));
    }
}
