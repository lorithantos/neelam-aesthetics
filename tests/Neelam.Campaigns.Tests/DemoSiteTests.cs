using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The demo's export path in the library: the rules alone, with a person's approval standing in for
/// the proofread, and the report saying plainly that it was not proofread.
/// </summary>
public class DemoReviewTests
{
    private static readonly Approval ByPriya = new("Priya", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private static Campaign Build(CampaignDraft draft) => draft.Build().Campaign!;

    [Fact]
    public void An_approved_campaign_the_rules_pass_exports_marked_as_not_proofread()
    {
        var report = CampaignGate.DemoReview(Build(DraftFixtures.Finished()), ByPriya);

        Assert.False(report.Proofread);
        Assert.Equal(ByPriya, report.DemoApproval);
        Assert.True(report.CanExport);
        Assert.Equal(EditorExport.PreviewBlocks(report.Campaign), EditorExport.Blocks(report));
    }

    [Fact]
    public void A_must_fix_still_stops_the_demo_export()
    {
        var draft = DraftFixtures.Finished();
        draft.Offer("Offer").Tiers[0].Name.Set("Platinum Member");

        var report = CampaignGate.DemoReview(Build(draft), ByPriya);

        Assert.NotEmpty(report.Blockers);
        Assert.False(report.CanExport);
        var blocked = Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(report));
        Assert.Contains("blocking problem", blocked.Message);
    }

    // The same rules the page shows, her registration included: a number she has not registered
    // is still worth a look in the export's review.
    [Fact]
    public void The_demo_review_checks_with_her_business()
    {
        var campaign = new Campaign("Hello",
            [new SignOffBlock("Sign-off", new SignOff("With gratitude,", "Neelam Aesthetics Team", "Call 425-877-8646"))]);
        var business = new BusinessContext("Neelam Aesthetics")
        {
            Phones = new PhoneNumbers([PhoneNumber.TryParse("(425) 773-5261", out var n) ? n : throw new InvalidOperationException()]),
        };

        var report = CampaignGate.DemoReview(campaign, ByPriya, business: business);

        Assert.Contains(report.Findings, f => f.Rule == "phone-registered");
    }

    // The production gate is unchanged: a rules-only report, which is what the editor holds, never
    // exports, and only the demo review carries an approval.
    [Fact]
    public void Without_the_demo_review_the_rules_alone_still_never_export()
    {
        var rulesOnly = CampaignReview.Check(Build(DraftFixtures.Finished()));

        Assert.Null(rulesOnly.DemoApproval);
        Assert.False(rulesOnly.CanExport);
        Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(rulesOnly));
    }
}

/// <summary>
/// The test site as it runs today, Prototype access, which is the demo: a banner on every client
/// page, and an approved campaign the rules pass exports without the proofread, marked so.
/// </summary>
public class DemoSiteTests(DemoApp app) : IClassFixture<DemoApp>
{
    private const string Banner =
        "This is a demo. What you save here is kept on a test system, not your own account, and the AI proofread isn't switched on yet.";

    private const string NotProofread = "Not proofread by AI yet";

    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client);

    private async Task<string> Get(string path)
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<SaveRef> Saved(CampaignDraft draft)
    {
        var save = await Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", draft);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    private static MatchCollection ExportBlocks(string page) =>
        Regex.Matches(page, "<li class=\"export-block\">(.*?)</li>", RegexOptions.Singleline);

    [Fact]
    public void The_demo_is_prototype_access() =>
        Assert.True(app.Services.GetRequiredService<SiteMode>().IsDemo);

    [Theory]
    [InlineData("/")]
    [InlineData("/how-it-works")]
    [InlineData("/campaigns")]
    [InlineData("/templates")]
    public async Task Every_client_page_carries_the_demo_banner(string path) =>
        Assert.Contains(Banner, await Get(path));

    [Fact]
    public async Task An_approved_campaign_exports_each_block_with_a_copy_button_and_the_notice()
    {
        var save = await Store.ApproveAsync(await Saved(DraftFixtures.Finished()), "Priya");

        var page = await Get($"/campaigns/{save.Id}");
        Assert.Contains("Approved by Priya, 3 Oct 2026, 12:0", page);
        Assert.Contains("<h2>Copy into Square</h2>", page);
        var expected = EditorExport.PreviewBlocks(DraftFixtures.Finished().Build().Campaign!);
        var blocks = ExportBlocks(page);
        Assert.Equal(expected.Count, blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            Assert.Contains(NotProofread, blocks[i].Value);
            // Everything with words to paste has them, with its copy button; a spacer has none.
            if (expected[i].Text.Length > 0)
            {
                Assert.Contains($"data-copy=\"export-{i}\"", blocks[i].Value);
                Assert.Contains($"<pre id=\"export-{i}\">{expected[i].Text}</pre>", blocks[i].Value);
            }
        }
        // The section says it too, not only each block.
        var section = Regex.Match(page, "<h2>Copy into Square</h2>(.*?)<ol", RegexOptions.Singleline).Value;
        Assert.Contains(NotProofread, section);
        // The button's link has its own copy.
        Assert.Contains("Copy link", page);
    }

    [Fact]
    public async Task Without_approval_there_is_no_export()
    {
        var save = await Saved(DraftFixtures.Finished());

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Contains(">Approve</button>", page);
        Assert.DoesNotContain("Copy into Square", page);
        Assert.DoesNotContain("data-copy", page);
        Assert.Contains("On this demo, export comes once the campaign is approved", page);
    }

    // Approved, and then the rules found something to stop: no export while it stands.
    [Fact]
    public async Task A_must_fix_stops_the_export_even_when_approved()
    {
        var draft = DraftFixtures.Finished();
        draft.Offer("Offer").Tiers[0].Name.Set("Platinum Member");
        var save = await Store.ApproveAsync(await Saved(draft), "Priya");

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Contains("Must fix", page);
        Assert.DoesNotContain("Copy into Square", page);
        Assert.DoesNotContain("data-copy", page);
    }

    [Fact]
    public async Task A_save_after_approval_removes_the_export_and_says_so()
    {
        var approved = await Store.ApproveAsync(await Saved(DraftFixtures.Finished()), "Priya");
        var edited = DraftFixtures.Finished();
        edited.Subject.Set("WE’RE TURNING TWO!");
        await Store.SaveDraftAsync(approved.Id, "WE’RE TURNING TWO!", edited);

        var page = await Get($"/campaigns/{approved.Id}");

        Assert.Contains("An earlier version was approved by Priya. It has been saved since, so this version is not approved.", page);
        Assert.DoesNotContain("Copy into Square", page);
        Assert.DoesNotContain("data-copy", page);
    }

    [Fact]
    public async Task How_it_works_points_steps_four_and_five_at_the_real_pages()
    {
        var page = await Get("/how-it-works");

        Assert.DoesNotContain("Being built", page);
        Assert.Contains("Try it: in <a href=\"campaigns\">Campaigns</a>, finish and save a campaign, then approve it with your name.", page);
        Assert.Contains("each block is marked \"Not proofread by AI yet\"", page);
        Assert.Contains("The AI proofread is not switched on yet.", page);
    }
}

/// <summary>
/// The boundary: Enforced, as production runs, is never the demo. No banner, and no export without
/// the proofread, even for a campaign approved with nothing to fix.
/// </summary>
public class EnforcedIsNotTheDemoTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private async Task<string> Get(string path, params string[] roles)
    {
        if ((await app.Clients.ListAsync()).Count == 0) await app.Clients.AddAsync(SalonOne);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<SaveRef> Approved()
    {
        var store = app.Stores.Campaigns(SalonOne.Name);
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished());
        return await store.ApproveAsync(save, "Priya");
    }

    [Fact]
    public void Enforced_is_not_the_demo() =>
        Assert.False(app.Services.GetRequiredService<SiteMode>().IsDemo);

    [Theory]
    [InlineData("/", null)]
    [InlineData("/how-it-works", null)]
    [InlineData("/campaigns", Features.Campaigns)]
    public async Task No_page_carries_the_demo_banner(string path, string? role) =>
        Assert.DoesNotContain("This is a demo.", await (role is null ? Get(path) : Get(path, role)));

    [Fact]
    public async Task An_approved_campaign_still_waits_for_the_proofread()
    {
        var save = await Approved();

        var page = await Get($"/campaigns/{save.Id}", Features.Campaigns, Features.Review);

        Assert.Contains("Approved by Priya", page);
        Assert.Contains("Export comes once the AI proofread is switched on.", page);
        Assert.DoesNotContain("Copy into Square", page);
        Assert.DoesNotContain("data-copy", page);
        Assert.DoesNotContain("Not proofread by AI yet", page);
    }

    // Approving is the Review feature, apart from writing campaigns.
    [Fact]
    public async Task Approving_takes_the_review_feature()
    {
        var save = await app.Stores.Campaigns(SalonOne.Name)
            .SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished());

        var writer = await Get($"/campaigns/{save.Id}", Features.Campaigns);
        Assert.Contains("Approving and exporting take the review permission", writer);
        Assert.DoesNotContain(">Approve</button>", writer);

        var reviewer = await Get($"/campaigns/{save.Id}", Features.Campaigns, Features.Review);
        Assert.Contains(">Approve</button>", reviewer);
    }

    [Fact]
    public async Task How_it_works_says_export_waits_for_the_proofread()
    {
        var page = await Get("/how-it-works");

        Assert.DoesNotContain("Being built", page);
        Assert.Contains("Approved campaigns are in <a href=\"campaigns\">Campaigns</a>.", page);
        Assert.DoesNotContain("Not proofread by AI yet", page);
    }
}
