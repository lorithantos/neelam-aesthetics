using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The demo with the AI proofread switched on, as the test site will be once the key is in Key Vault:
/// Prototype access, in-memory storage, and a fake proofreader in place of Claude, so nothing leaves
/// the test.
/// </summary>
public sealed class ProofreadDemoApp : InMemoryApp
{
    internal FakeProofreader Proofreader { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(PrototypeCallerSource.ClientSetting, DemoApp.Client.ToString());
        base.ConfigureWebHost(builder);
    }

    // Program's own access stands (Prototype, the demo); the proofread is on, and fake.
    protected override void ConfigureAccess(IServiceCollection services)
    {
        services.RemoveAll<ProofreadSwitch>().AddSingleton(new ProofreadSwitch(true));
        services.RemoveAll<IProofreader>().AddSingleton<IProofreader>(Proofreader);
    }
}

/// <summary>
/// What she sees of the AI proofread on the campaign page (owner, 2026-10-10): "Proofread with AI" on a
/// saved version; once proofread, its findings with the rules' and its photos' notes; the export then
/// says proofread, and the JSON too; a Must fix it found stops the export. Read by named elements.
/// </summary>
public class ProofreadPageTests(ProofreadDemoApp app) : IClassFixture<ProofreadDemoApp>
{
    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private static readonly Finding Unclear = new(Severity.Warning, "ai-clarity", "Offer", "Could read two ways. Suggest: say per visit.", "per visit");

    private async Task<string> Get(string path)
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<SaveRef> Saved(CampaignDraft? draft = null)
    {
        var save = await Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", draft ?? DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    private async Task Proofread(SaveRef save, params Finding[] findings) =>
        await Store.SaveProofreadAsync(save, new ProofreadRecord(app.Clock.GetUtcNow(), findings,
            [new PhotoCheck("Principals toasting", "Couldn't check this photo: it is not a Square photo in your image library.", false)]));

    [Fact]
    public async Task A_saved_version_offers_proofread_with_ai()
    {
        var save = await Saved();

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal("Proofread with AI", RenderedPage.Named(page, "proofread-button"));
        Assert.StartsWith("It reads this saved version once, when you click", RenderedPage.Named(page, "proofread-status"));
    }

    [Fact]
    public async Task A_proofread_version_shows_what_it_found_with_the_rules_and_offers_no_second_run()
    {
        var save = await Saved();
        await Proofread(save, Unclear);

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal(0, RenderedPage.Count(page, "proofread-button"));
        Assert.EndsWith("The AI proofread found 1 thing in this version, listed with the rules' findings.", RenderedPage.Named(page, "proofread-status"));
        Assert.StartsWith("These are the instant rules, with what the AI proofread found", RenderedPage.Named(page, "checks-with-proofread"));
        Assert.Contains(Unclear.Message, RenderedPage.Text(page));
        Assert.Equal("Principals toasting Couldn't check this photo: it is not a Square photo in your image library.",
            RenderedPage.Named(page, "proofread-photos"));
    }

    [Fact]
    public async Task Proofread_and_approved_the_export_says_proofread_and_so_does_the_json()
    {
        var save = await Saved();
        await Proofread(save, Unclear);
        var approved = await Store.ApproveAsync(save, "Priya");
        await Store.WarningsSeenAtExportAsync(approved, [.. SeenAtExport.KeysOf(DraftFixtures.Finished()), Unclear.SeenKey]);

        var page = await Get($"/campaigns/{save.Id}");

        Assert.StartsWith("Approved and proofread by AI", RenderedPage.Named(page, "export-proofread"));
        Assert.DoesNotContain("Not proofread by AI yet", RenderedPage.Text(page));
        var href = RenderedPage.Attribute(page, "assistant-download", "href");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(href[(href.IndexOf(',') + 1)..])))!;
        Assert.True((bool?)json["review"]!["proofread"]);
        Assert.Null(json["review"]!["notice"]);
    }

    [Fact]
    public async Task Approved_but_not_proofread_the_export_is_still_marked_not_proofread()
    {
        var save = await Saved();
        var approved = await Store.ApproveAsync(save, "Priya");
        await Store.WarningsSeenAtExportAsync(approved, SeenAtExport.KeysOf(DraftFixtures.Finished()));

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal(0, RenderedPage.Count(page, "export-proofread"));
        Assert.Contains("Not proofread by AI yet", RenderedPage.Text(page));
        Assert.Contains("This version has not been proofread by AI", RenderedPage.Text(page));
    }

    // What the AI found that is worth a look comes up at export, with the rules' own.
    [Fact]
    public async Task Its_warning_is_listed_before_the_export()
    {
        var save = await Saved();
        await Proofread(save, Unclear);
        await Store.ApproveAsync(save, "Priya");

        var page = await Get($"/campaigns/{save.Id}");

        var list = page[page.IndexOf("Before you copy it into Square", StringComparison.Ordinal)..];
        Assert.Contains(Unclear.Message, RenderedPage.Text(list));
    }

    [Fact]
    public async Task Its_must_fix_stops_the_export_and_says_why()
    {
        var save = await Saved();
        await Proofread(save, new Finding(Severity.Blocker, "ai-spelling", "Opening", "Misspelt. Suggest: receive", "recieve"));
        await Store.ApproveAsync(save, "Priya");

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal("The AI proofread found something to fix in this version. Fix it, save, and approve the new version.",
            RenderedPage.Named(page, "export-waits-for-proofread-fix"));
        Assert.Equal(0, RenderedPage.Count(page, "assistant-download"));
        Assert.DoesNotContain("Copy into Square", RenderedPage.Text(page));
    }

    [Fact]
    public async Task With_the_proofread_on_the_banner_no_longer_says_it_is_off() =>
        Assert.Equal("This is a demo. What you save here is kept on a test system, not your own account.",
            RenderedPage.Named(await Get("/campaigns"), "demo-banner"));
}

/// <summary>While the proofread is off, as on the demo until the key is set, nothing offers it.</summary>
public class ProofreadOffPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    [Fact]
    public async Task No_proofread_is_offered_and_the_page_says_it_is_not_switched_on()
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Hello", DraftFixtures.Finished());

        var response = await app.CreateClient().GetAsync($"/campaigns/{save.Id}");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(0, RenderedPage.Count(page, "proofread-button"));
        Assert.Equal(0, RenderedPage.Count(page, "proofread-status"));
        Assert.Contains("The AI proofread is not switched on yet.", RenderedPage.Text(page));
        Assert.False(app.Services.GetRequiredService<ProofreadSwitch>().IsOn);
    }
}
