using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The owner, 2026-11-01: "renaming a template should not change the campaigns. We should use an ID
/// instead of the name", and "We can run an update when the new one is deployed to main". A campaign
/// refers to its template by id alone: renaming the template changes no campaign and ends no approval,
/// and wherever a name is shown it is the template's own, now. Campaigns saved before they recorded
/// their template are linked by a one-time update, in each save's metadata, leaving the saves, their
/// history and their approvals exactly as they were.
/// </summary>
public class TemplateLinkTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_container, TimeSpan.FromDays(1));

    private async Task<SaveRef> Later(Task<SaveRef> saving)
    {
        var save = await saving;
        _clock.Now += TimeSpan.FromMinutes(1);
        return save;
    }

    private static CampaignTemplate Named(string name) => new(name, DraftFixtures.Membership.Blocks);

    // ---- Renaming a template ----

    [Fact]
    public async Task Renaming_a_template_ends_no_approval_and_changes_no_content_even_once_updated_to()
    {
        var template = Guid.NewGuid();
        await Later(Store.SaveTemplateAsync(template, Named("Membership announcement")));
        var started = (await DraftSession.StartAsync(Store, template))!;
        var draft = TemplateUpdate.Of(DraftFixtures.Finished(), Named("Membership announcement"), template, started.Editor.Draft.TemplateVersion!.Value).Draft;
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", draft));
        await Store.ApproveAsync(campaign, "Priya");
        var before = DraftSession.ContentOf(await Store.LoadDraftAsync(campaign));

        await Later(Store.SaveTemplateAsync(template, Named("Membership, monthly")));

        var session = (await DraftSession.OpenAsync(Store, campaign.Id))!;
        var templates = await session.TemplatesAsync();
        Assert.Equal("Membership, monthly", session.TemplateNameNow(templates));
        Assert.NotNull(session.CurrentApproval);

        // Brought up to the renamed template: the name and version it records change, nothing approved does.
        var choice = (await session.ChooseTemplateAsync(template))!;
        Assert.Null(session.UseTemplate(choice));
        Assert.Equal("Membership, monthly", session.Editor.TemplateName);
        Assert.Equal(before, DraftSession.ContentOf(session.Editor.Draft));
        Assert.NotNull(session.CurrentApproval);
        // Saved so, the approval goes with it, as for a save that changes only her label.
        Assert.Equal("Priya", (await session.SaveAsync()).Approval?.By);
    }

    [Fact]
    public void The_template_s_name_is_not_part_of_what_an_approval_is_of()
    {
        var a = DraftFixtures.Finished();
        var b = TemplateUpdate.Of(DraftFixtures.Finished(), Named("Another name entirely"), Guid.NewGuid(), _clock.Now).Draft;

        Assert.NotEqual(a.TemplateName, b.TemplateName);
        Assert.Equal(DraftSession.ContentOf(a), DraftSession.ContentOf(b));
    }

    // ---- The one-time update ----

    // A campaign saved before campaigns recorded their template, under the template's old name.
    private async Task<(Guid Template, SaveRef First, SaveRef Second, SaveRef TemplateV1)> OldCampaignAsync(string name = "Membership announcement")
    {
        var template = Guid.NewGuid();
        var v1 = await Later(Store.SaveTemplateAsync(template, Named(name)));
        var first = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "First", DraftFixtures.Finished(Named(name))));
        var second = await Later(Store.SaveDraftAsync(first.Id, "Second", DraftFixtures.Finished(Named(name))));
        // A later version of the template: not the one the campaign was made from.
        await Later(Store.SaveTemplateAsync(template, Named(name)));
        return (template, first, second, v1);
    }

    [Fact]
    public async Task Report_only_writes_nothing_and_says_what_it_would_link()
    {
        var (_, first, second, _) = await OldCampaignAsync();
        var metadata = _container.Blobs.ToDictionary(b => b.Key, b => b.Value.Metadata);

        var report = await Store.LinkTemplatesAsync(apply: false);

        Assert.False(report.Applied);
        Assert.Equal([first.Id], report.Linked);
        Assert.All(_container.Blobs, b => Assert.Same(metadata[b.Key], b.Value.Metadata));
        Assert.Null((await Store.LoadDraftAsync(second)).TemplateId);
    }

    [Fact]
    public async Task Applied_each_save_is_linked_in_its_metadata_and_its_content_history_and_approval_stay_as_they_were()
    {
        var (template, first, second, v1) = await OldCampaignAsync();
        await Store.ApproveAsync(second, "Priya");
        var content = _container.Text(second.BlobName);
        var approvedContent = DraftSession.ContentOf(await Store.LoadDraftAsync(second));

        var report = await Store.LinkTemplatesAsync(apply: true);

        Assert.Equal([first.Id], report.Linked);
        Assert.Equal(content, _container.Text(second.BlobName));
        var history = await Store.HistoryAsync(DocumentKind.Draft, first.Id);
        Assert.Equal([second.BlobName, first.BlobName], history.Select(s => s.BlobName));
        Assert.Equal("Priya", history[0].Approval?.By);
        // Linked by id, with the version that was newest when the campaign was first saved.
        var loaded = await Store.LoadDraftAsync(history[0]);
        Assert.Equal((template, v1.SavedAt), (loaded.TemplateId, loaded.TemplateVersion));
        Assert.Equal(template, (await Store.LoadDraftAsync(history[1])).TemplateId);
        Assert.Equal(approvedContent, DraftSession.ContentOf(loaded));

        var session = (await DraftSession.OpenAsync(Store, first.Id))!;
        Assert.NotNull(session.CurrentApproval);
        Assert.Equal("Membership announcement", session.TemplateNameNow(await session.TemplatesAsync()));
        // Its next save writes the link into its own JSON.
        session.Editor.Label = "Linked";
        var saved = await session.SaveAsync();
        Assert.Contains($"\"templateId\": \"{template}\"", _container.Text(saved.BlobName));
    }

    [Fact]
    public async Task Run_again_it_touches_nothing_linked_before()
    {
        var (_, first, _, _) = await OldCampaignAsync();
        await Store.LinkTemplatesAsync(apply: true);
        var metadata = _container.Blobs.ToDictionary(b => b.Key, b => b.Value.Metadata);

        var again = await Store.LinkTemplatesAsync(apply: true);

        Assert.Empty(again.Linked);
        Assert.Equal(2, again.AlreadyLinked);
        Assert.All(_container.Blobs, b => Assert.Same(metadata[b.Key], b.Value.Metadata));
        Assert.DoesNotContain(first.Id, (await Store.LinkTemplatesAsync(apply: false)).Linked);
    }

    [Fact]
    public async Task A_name_no_template_has_or_two_templates_share_is_reported_and_left()
    {
        var (_, lonely, _, _) = await OldCampaignAsync("Retired template");
        // The template it named was renamed: no template has that name now.
        var all = (await Store.LatestAsync(DocumentKind.Template)).ToList();
        await Later(Store.SaveTemplateAsync(all.Single(t => t.Title == "Retired template").Id, Named("Something new")));
        var twice = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "Twice", DraftFixtures.Finished(Named("Shared name"))));
        await Later(Store.SaveTemplateAsync(Guid.NewGuid(), Named("Shared name")));
        await Later(Store.SaveTemplateAsync(Guid.NewGuid(), Named("Shared name")));

        var report = await Store.LinkTemplatesAsync(apply: true);

        Assert.Equal([lonely.Id], report.NoMatch);
        Assert.Equal([twice.Id], report.Ambiguous);
        Assert.Empty(report.Linked);
        Assert.Null((await Store.LoadDraftAsync(twice)).TemplateId);
        Assert.Contains($"no match 1 ({lonely.Id:N})", report.ToString());
    }

    // An undone save is linked too, keeping its mark, so a restore brings it back linked; a save that
    // changes while it is being linked (an undo marked meanwhile) is left for the next run, its mark kept.
    [Fact]
    public async Task An_undone_save_keeps_its_mark_and_one_changed_meanwhile_is_left_to_retry()
    {
        var (template, first, second, _) = await OldCampaignAsync();
        await Store.MarkUndoneAsync(second);
        var report = await Store.LinkTemplatesAsync(apply: true);
        Assert.Equal([first.Id], report.Linked);
        Assert.Contains("undone", _container.Blobs[second.BlobName].Metadata.Keys);
        Assert.Equal(template, (await Store.LoadDraftAsync((await Store.RestorableAsync(DocumentKind.Draft, first.Id))!)).TemplateId);

        var (_, other, _, _) = await OldCampaignAsync("Thank you");
        var raced = false;
        _container.BeforeSetMetadata = async name =>
        {
            if (raced || !name.Contains(other.Id.ToString("N"), StringComparison.Ordinal)) return;
            raced = true;
            _container.BeforeSetMetadata = null;
            await Store.MarkUndoneAsync((await Store.HistoryAsync(DocumentKind.Draft, other.Id)).Single(s => s.BlobName == name));
        };

        var racedReport = await Store.LinkTemplatesAsync(apply: true);

        Assert.Equal([other.Id], racedReport.Retry);
        Assert.Contains(_container.Blobs, b => b.Key.Contains(other.Id.ToString("N"), StringComparison.Ordinal) && b.Value.Metadata.ContainsKey("undone"));
    }

    [Fact]
    public async Task A_draft_s_own_template_wins_over_a_link()
    {
        var template = Guid.NewGuid();
        var own = Guid.NewGuid();
        await Later(Store.SaveTemplateAsync(template, Named("Membership announcement")));
        var save = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "Own", TemplateUpdate.Of(DraftFixtures.Finished(), Named("X"), own, _clock.Now).Draft));

        var linked = save with { Template = new TemplateLink(template, null) };

        Assert.Equal(own, (await Store.LoadDraftAsync(linked)).TemplateId);
        Assert.Empty((await Store.LinkTemplatesAsync(apply: true)).Linked);
    }
}

/// <summary>The template link update as the app runs it, and the page's names, by id.</summary>
public class TemplateLinkAppTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<SaveRef> Later(Task<SaveRef> saving)
    {
        var save = await saving;
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    [Fact]
    public void It_is_off_unless_its_setting_says_otherwise()
    {
        Assert.Equal(TemplateLinkMode.Off, app.Services.GetRequiredService<IOptions<TemplateLinkOptions>>().Value.Mode);
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<TemplateLinkUpdate>());
    }

    // Report mode, as on staging first: every client it works in, the prototype's included, and nothing written.
    [Fact]
    public async Task In_report_mode_the_app_s_run_writes_nothing()
    {
        var template = Guid.NewGuid();
        var name = $"Report mode {Guid.NewGuid():N}";
        await Later(Store.SaveTemplateAsync(template, new CampaignTemplate(name, DraftFixtures.Membership.Blocks)));
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "Old", DraftFixtures.Finished(new CampaignTemplate(name, DraftFixtures.Membership.Blocks))));
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["TemplateLinks:Mode"] = "Report" })));
        var update = configured.Services.GetServices<IHostedService>().OfType<TemplateLinkUpdate>().Single();

        var reports = await update.RunOnceAsync();

        Assert.Contains(campaign.Id, reports[DemoApp.Client].Linked);
        Assert.False(reports[DemoApp.Client].Applied);
        Assert.Null((await Store.LoadDraftAsync((await Store.HistoryAsync(DocumentKind.Draft, campaign.Id)).Single())).TemplateId);
    }

    // The export shows the template's name as it is now, and the page names it so.
    [Fact]
    public async Task The_page_and_the_export_name_the_template_as_it_is_now()
    {
        var template = Guid.NewGuid();
        var first = await Later(Store.SaveTemplateAsync(template, new CampaignTemplate("Membership announcement", DraftFixtures.Membership.Blocks)));
        var draft = TemplateUpdate.Of(DraftFixtures.Finished(), new CampaignTemplate("Membership announcement", DraftFixtures.Membership.Blocks), template, first.SavedAt).Draft;
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", draft));
        var approved = await Store.ApproveAsync(campaign, "Priya");
        await Store.WarningsSeenAtExportAsync(approved, SeenAtExport.KeysOf(DraftFixtures.Finished()));
        var renamed = $"Membership, renamed {Guid.NewGuid():N}";
        await Later(Store.SaveTemplateAsync(template, new CampaignTemplate(renamed, DraftFixtures.Membership.Blocks)));

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/campaigns/{campaign.Id}");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal($"The template {renamed} has changed since this campaign was made.", RenderedPage.Named(page, "template-changed"));
        Assert.StartsWith("Approved by Priya", RenderedPage.Named(page, "approved"));
        var href = RenderedPage.Attribute(page, "assistant-download", "href");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(href[(href.IndexOf(',') + 1)..])))!;
        Assert.Equal(renamed, (string?)json["campaign"]!["template"]);
    }

    // A template gone: said plainly, and the export carries no name rather than an old one.
    [Fact]
    public async Task A_template_gone_is_said_plainly_and_the_export_names_none()
    {
        var template = Guid.NewGuid();
        var only = await Later(Store.SaveTemplateAsync(template, new CampaignTemplate("Gone soon", DraftFixtures.Membership.Blocks)));
        var draft = TemplateUpdate.Of(DraftFixtures.Finished(), new CampaignTemplate("Gone soon", DraftFixtures.Membership.Blocks), template, only.SavedAt).Draft;
        var campaign = await Later(Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", draft));
        var approved = await Store.ApproveAsync(campaign, "Priya");
        await Store.WarningsSeenAtExportAsync(approved, SeenAtExport.KeysOf(DraftFixtures.Finished()));
        await Store.MarkUndoneAsync(only);

        var page = await (await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/campaigns/{campaign.Id}")).Content.ReadAsStringAsync();

        Assert.Equal("The template this campaign was made from no longer exists. This campaign still works as it is, and you can choose another template.",
            RenderedPage.Named(page, "template-gone"));
        var href = RenderedPage.Attribute(page, "assistant-download", "href");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(href[(href.IndexOf(',') + 1)..])))!;
        Assert.Null(json["campaign"]!["template"]);
    }
}
