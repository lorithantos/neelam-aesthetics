using Neelam.Campaigns.Storage;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Priya, 2026-10-31: "There is no preview on the template page". The whole email's preview was there,
/// at the foot of the side column, which on a phone comes after every block and the Add a block list.
/// Each fixed block now shows how it goes out inside its own card, as the campaign page shows its fixed
/// blocks, and both follow what is typed, a photo's name and description included. The template editor
/// held as its circuit holds it (<see cref="OpenCampaignPage"/>), read by named elements.
/// </summary>
public class TemplatePreviewPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    // In the membership template: the header, a spacer, the headline, then the greeting.
    private const int Header = 0;
    private const int Headline = 2;
    private const int Greeting = 3;

    private async Task<OpenCampaignPage> OpenMembershipAsync()
    {
        var id = Guid.NewGuid();
        await StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo).SaveTemplateAsync(id, DraftFixtures.Membership);
        return await OpenCampaignPage.OpenAsync<TemplatePage>(app,
            new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)id }, StalePages.Clock(app));
    }

    [Fact]
    public async Task Each_fixed_block_shows_how_it_goes_out_in_its_own_card()
    {
        await using var page = await OpenMembershipAsync();
        var html = await page.HtmlAsync();

        // The preview sets an emoji in its own element, so the text read back has a space before it.
        Assert.Equal("It goes out like this: Hi Beautiful 🤍", RenderedPage.Named(html, $"fixed-preview-{Greeting}"));
        Assert.StartsWith("It goes out like this:", RenderedPage.Named(html, $"fixed-preview-{Header}"));
        // A block each campaign fills has no preview of its own: the whole email's shows its placeholder.
        Assert.Equal(0, RenderedPage.Count(html, $"fixed-preview-{Headline}"));
        Assert.Contains("‹Headline: written for each campaign›", RenderedPage.Named(html, "template-preview"));
    }

    // The owner, 2026-10-31: "No change - but maybe make it easier to find even on a computer". A link
    // near the top goes to the whole email's preview, on this page's own address so it only moves down.
    [Fact]
    public async Task A_link_at_the_top_goes_to_the_whole_email_s_preview()
    {
        await using var page = await OpenMembershipAsync();
        var html = await page.HtmlAsync();

        Assert.Equal("See the whole email's preview", RenderedPage.Named(html, "preview-link"));
        var href = RenderedPage.Attribute(html, "preview-link", "href");
        Assert.Matches("^templates/[0-9a-f-]{36}#template-preview$", href);
        Assert.Equal("template-preview", RenderedPage.Attribute(html, "template-preview", "id"));
        // Before the first block.
        Assert.True(html.IndexOf("data-testid=\"preview-link\"", StringComparison.Ordinal)
                    < html.IndexOf("Blocks, top to bottom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_previews_follow_what_is_typed()
    {
        await using var page = await OpenMembershipAsync();

        await page.TypeAsync($"text-{Greeting}", "Hello lovely");

        var html = await page.HtmlAsync();
        Assert.Equal("It goes out like this: Hello lovely", RenderedPage.Named(html, $"fixed-preview-{Greeting}"));
        Assert.Contains("Hello lovely", RenderedPage.Named(html, "template-preview"));
        Assert.DoesNotContain("Hi Beautiful", RenderedPage.Named(html, "template-preview"));
    }

    // The photo's fields are their own component, which re-rendered only itself: the page's previews
    // went on showing the photo named before until something else on the page changed.
    [Fact]
    public async Task A_photo_typed_into_a_fixed_header_reaches_both_previews_at_once()
    {
        await using var page = await OpenMembershipAsync();

        await page.TypeAsync($"photo-{Header}", "Principals at the door");

        var html = await page.HtmlAsync();
        Assert.Contains("Principals at the door", RenderedPage.Named(html, $"fixed-preview-{Header}"));
        Assert.Contains("Principals at the door", RenderedPage.Named(html, "template-preview"));
    }
}
