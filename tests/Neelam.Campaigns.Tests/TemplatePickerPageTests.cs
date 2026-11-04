using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The campaign page's Template picker (owner, 2026-10-31), driven in a Blazor circuit through
/// <see cref="OpenCampaignPage"/> over the demo app's in-memory storage: Update to template when the
/// template a campaign was made from has changed, asking first when her text would be lost, and Use
/// this template for a campaign with none attached. Read by named elements.
/// </summary>
public class TemplatePickerPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<SaveRef> Later(Task<SaveRef> saving)
    {
        var save = await saving;
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    [Fact]
    public async Task A_campaign_whose_template_changed_offers_Update_to_template_and_asks_before_replacing_her_text()
    {
        var template = Guid.NewGuid();
        var monthly = new CampaignTemplate("Membership, monthly", DraftFixtures.Membership.Blocks);
        var first = await Later(Store.SaveTemplateAsync(template, monthly));
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!",
            TemplateUpdate.Of(DraftFixtures.Finished(), monthly, template, first.SavedAt).Draft));
        await Later(Store.SaveTemplateAsync(template, new CampaignTemplate("Membership, monthly",
            monthly.Blocks.Select(b => b.Label switch
            {
                "Greeting" => b with { Fixed = new GreetingBlock("Greeting", "Hello lovely 🤍") },
                "Headline" => b with { Fixed = new HeadingBlock("Headline", "NEWS FROM NEELAM") },
                _ => b,
            }).ToList())));
        await using var page = await OpenCampaignPage.OpenAsync(app, campaign.Id, new ManualClock(app.Clock.Now));

        var html = await page.HtmlAsync();
        Assert.Equal("The template Membership, monthly has changed since this campaign was made.", RenderedPage.Named(html, "template-changed"));
        Assert.Equal(template.ToString(), RenderedPage.Attribute(html, "template-picker", "value"));
        Assert.Equal(0, RenderedPage.Count(html, "template-use"));

        await page.ClickAsync("template-update");

        html = await page.HtmlAsync();
        Assert.Equal("Headline: the template's own text takes the place of yours.", RenderedPage.Named(html, "template-replaced"));
        Assert.Equal(0, RenderedPage.Count(html, "template-dropped"));
        // Asked, and nothing changed yet.
        Assert.Contains("Hi Beautiful 🤍", RenderedPage.Text(html));
        Assert.Equal(1, RenderedPage.Count(html, "template-changed"));

        await page.ClickAsync("template-confirm-use");

        html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "template-confirm"));
        Assert.Equal(0, RenderedPage.Count(html, "template-changed"));
        Assert.Equal(0, RenderedPage.Count(html, "template-update"));
        Assert.Contains("Hello lovely 🤍", RenderedPage.Text(html));
        Assert.DoesNotContain("Hi Beautiful 🤍", RenderedPage.Text(html));
        Assert.Equal("The template Membership, monthly is in the form now. Save the campaign to keep it; leave without saving and the campaign stays as it was.",
            RenderedPage.Named(html, "template-message"));
        // The form only: nothing saved.
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, campaign.Id));
    }

    // Saved before campaigns recorded their template: none attached, none guessed from the name it saved
    // (owner, 2026-11-01: "We should use an ID instead of the name"), and the page works as it is; she can
    // choose one, and nothing is attached until she presses.
    [Fact]
    public async Task A_campaign_with_no_template_attached_takes_one_on_Use_this_template_and_keeps_it_on_save()
    {
        var template = Guid.NewGuid();
        await Later(Store.SaveTemplateAsync(template, DraftFixtures.Membership));
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished()));
        await using var page = await OpenCampaignPage.OpenAsync(app, campaign.Id, new ManualClock(app.Clock.Now));

        var html = await page.HtmlAsync();
        Assert.Equal("", RenderedPage.Attribute(html, "template-picker", "value"));
        Assert.Equal(0, RenderedPage.Count(html, "template-changed"));
        Assert.Equal(0, RenderedPage.Count(html, "template-gone"));
        Assert.Equal(0, RenderedPage.Count(html, "template-update"));
        Assert.Equal(0, RenderedPage.Count(html, "template-use"));

        await page.ChooseAsync("template-picker", template.ToString());
        await page.ClickAsync("template-use");

        html = await page.HtmlAsync();
        // Nothing of hers lost, so not asked.
        Assert.Equal(0, RenderedPage.Count(html, "template-confirm"));
        Assert.Equal(0, RenderedPage.Count(html, "template-use"));
        Assert.Equal("The template Membership announcement is in the form now. Save the campaign to keep it; leave without saving and the campaign stays as it was.",
            RenderedPage.Named(html, "template-message"));
        Assert.Null((await Store.LoadDraftAsync(campaign)).TemplateId);

        await page.ClickAsync("save-button");

        var saved = (await Store.HistoryAsync(DocumentKind.Draft, campaign.Id))[0];
        Assert.Equal(template, (await Store.LoadDraftAsync(saved)).TemplateId);
    }

    // Started on the wrong template: another chosen in the picker, and used.
    [Fact]
    public async Task Another_template_can_be_chosen_and_used_at_any_time()
    {
        var used = Guid.NewGuid();
        var other = Guid.NewGuid();
        var thankYou = new CampaignTemplate("Thank you note", DraftFixtures.Membership.Blocks);
        var first = await Later(Store.SaveTemplateAsync(used, new CampaignTemplate("Membership, quarterly", DraftFixtures.Membership.Blocks)));
        await Later(Store.SaveTemplateAsync(other, thankYou));
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!",
            TemplateUpdate.Of(DraftFixtures.Finished(), DraftFixtures.Membership, used, first.SavedAt).Draft));
        await using var page = await OpenCampaignPage.OpenAsync(app, campaign.Id, new ManualClock(app.Clock.Now));
        Assert.Equal(0, RenderedPage.Count(await page.HtmlAsync(), "template-use"));

        await page.ChooseAsync("template-picker", other.ToString());
        await page.ClickAsync("template-use");

        var html = await page.HtmlAsync();
        Assert.Equal(other.ToString(), RenderedPage.Attribute(html, "template-picker", "value"));
        Assert.Equal("The template Thank you note is in the form now. Save the campaign to keep it; leave without saving and the campaign stays as it was.",
            RenderedPage.Named(html, "template-message"));
    }
}
