using System.Net;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>The campaigns list and editor, requested through the enforcing app.</summary>
public class CampaignPagesTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private static readonly ClientRecord SalonTwo = new(
        new ClientName("test-salon-two"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "Salon Two");

    private static readonly Guid Membership = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Replayed = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Finished = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SameNames = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid SalonTwoCampaign = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, string[] roles, Guid[]? groups = null)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(SalonOne);
            await app.Clients.AddAsync(SalonTwo);
            var stores = app.Stores;
            var one = stores.Campaigns(SalonOne.Name);
            await one.SaveTemplateAsync(Membership, DraftFixtures.Membership);
            await one.SaveDraftAsync(Replayed, "Second send, replayed", DraftFixtures.SecondSendReplayed());
            await one.SaveDraftAsync(Finished, "WE’RE TURNING ONE!", DraftFixtures.Finished());
            await one.SaveDraftAsync(SameNames, "Both tiers Platinum, no terms", DraftFixtures.SameNamesNoTerms());
            // Salon two has a campaign and no templates.
            await stores.Campaigns(SalonTwo.Name).SaveDraftAsync(SalonTwoCampaign, "Salon two’s own campaign", DraftFixtures.StartAndFillText());
        }
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, groups ?? [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        // Razor encodes characters outside ASCII, such as ’ and emoji.
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData("/campaigns")]
    [InlineData("/campaigns/new/aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("/campaigns/bbbbbbbb-0000-0000-0000-000000000001")]
    public async Task Nobody_signed_in_is_challenged(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(path, [])).Status);

    // Changing layouts and reviewing are granted apart from writing campaigns.
    [Theory]
    [InlineData("/campaigns")]
    [InlineData("/campaigns/new/aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("/campaigns/bbbbbbbb-0000-0000-0000-000000000001")]
    public async Task Other_roles_do_not_open_the_campaigns(string path) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(path, [Features.Templates, Features.Review])).Status);

    [Fact]
    public async Task A_member_sees_their_client_s_campaigns_and_templates_and_not_another_client_s()
    {
        var (status, page) = await Get("/campaigns", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Second send, replayed", page);
        Assert.Contains($"campaigns/{Replayed}", page);
        Assert.Contains($"campaigns/new/{Membership}", page);
        Assert.DoesNotContain("Salon two’s own campaign", page);
        // Her business by the name in the clients table, not its container's.
        Assert.Contains("Salon One's campaigns.", page);
        Assert.DoesNotContain("test-salon-one", page);

        var (_, other) = await Get("/campaigns", [Features.Campaigns], groups: [SalonTwo.GroupId]);
        Assert.Contains("Salon Two's campaigns.", other);
        Assert.Contains("Salon two’s own campaign", other);
        Assert.DoesNotContain("Second send, replayed", other);
        // No templates to start from: it says so, and where to make one.
        Assert.Contains("no templates to start a campaign from yet", other);
        Assert.Contains("href=\"templates/new\"", other);
    }

    // The role is not enough: whose campaigns comes from the client groups, through the access check.
    [Fact]
    public async Task The_role_without_a_client_shows_no_campaigns()
    {
        var (status, page) = await Get("/campaigns", [Features.Campaigns], groups: []);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("not a member of any client", page);
        Assert.DoesNotContain("Second send, replayed", page);
    }

    // Another client's campaign, by its id, is simply not there for this member.
    [Fact]
    public async Task Another_client_s_campaign_is_not_found()
    {
        var (_, page) = await Get($"/campaigns/{SalonTwoCampaign}", [Features.Campaigns]);

        Assert.Contains("Campaign not found", page);
        Assert.DoesNotContain("Salon two’s own campaign", page);
    }

    [Fact]
    public async Task A_new_campaign_opens_with_the_template_s_fixed_parts_and_empty_fields()
    {
        var (status, page) = await Get($"/campaigns/new/{Membership}", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Untitled campaign from Membership announcement", page);
        // Fixed parts, as they will be sent.
        Assert.Contains("With gratitude,", page);
        Assert.Contains("Hi Beautiful 🤍", page);
        // Fields for the rest, empty, and what is missing.
        Assert.Contains("id=\"subject\"", page);
        Assert.Contains("Subject has not been filled in.", page);
        Assert.Contains("Headline has not been filled in.", page);
        Assert.Contains("Save campaign", page);
        // The email so far, each part to write marked where it goes.
        Assert.Contains("‹Headline: not filled in yet›", page);
    }

    private static string MustFix(string message) => $"<strong>Must fix</strong>\\s*<span>{Regex.Escape(message)}</span>";

    // Priya's campaign as it went out: no terms link, which blocks, and the checks still run over the rest.
    [Fact]
    public async Task With_a_part_missing_the_page_lists_it_as_must_fix_and_still_shows_the_findings_and_the_preview()
    {
        var (_, page) = await Get($"/campaigns/{SameNames}", [Features.Campaigns]);

        Assert.Matches(MustFix("Offer › Terms link has not been filled in."), page);
        Assert.Contains("What the checks say", page);
        Assert.Contains("Tiers 1 and 2 share the name 'Platinum Member'; customers cannot tell them apart.", page);
        // In the preview, set apart as a placeholder.
        Assert.Matches($"<p class=\"placeholder\">\\s*{Regex.Escape("‹Offer › Terms link: not filled in yet›")}\\s*</p>", page);
    }

    [Theory]
    [InlineData("/campaigns/new/dddddddd-0000-0000-0000-000000000001", "Template not found")]
    [InlineData("/campaigns/dddddddd-0000-0000-0000-000000000001", "Campaign not found")]
    public async Task What_is_not_there_says_so(string path, string heading)
    {
        var (status, page) = await Get(path, [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(heading, page);
    }

    [Fact]
    public async Task A_saved_campaign_reopens_with_its_values_and_its_unchecked_copy()
    {
        var (_, page) = await Get($"/campaigns/{Replayed}", [Features.Campaigns]);

        Assert.Contains("value=\"WE’RE TURNING ONE!\"", page);
        Assert.Contains("value=\"Gold Member\"", page);
        Assert.Contains("value=\"299\"", page);
        // Tier 2's third benefit was copied and never touched: still marked, with its Confirm.
        Assert.Single(Regex.Matches(page, "Copied from Tier 1, not yet checked."));
        Assert.Contains(">Confirm</button>", page);
        Assert.Contains("was copied from Tier 1 and not reviewed", page);
    }

    [Fact]
    public async Task Missing_parts_come_first_then_the_rule_findings_and_never_an_export()
    {
        var (_, unfinished) = await Get($"/campaigns/{Replayed}", [Features.Campaigns]);
        Assert.Contains("Still to do", unfinished);
        Assert.Matches(MustFix("Offer › Tier 2 › Name has not been filled in."), unfinished);
        Assert.Contains("Checked so far: the parts filled in.", unfinished);

        var (_, sameNames) = await Get($"/campaigns/{SameNames}", [Features.Campaigns]);

        var (_, finished) = await Get($"/campaigns/{Finished}", [Features.Campaigns]);
        Assert.DoesNotContain("Still to do", finished);
        Assert.DoesNotContain("Checked so far", finished);
        Assert.DoesNotContain("not filled in yet", finished);
        Assert.Contains("What the checks say", finished);
        Assert.Contains("Worth a look", finished);
        Assert.Contains("Join the Beauty Bank", finished);

        foreach (var page in new[] { unfinished, sameNames, finished })
        {
            Assert.Contains("Export comes once the AI proofread is switched on.", page);
            Assert.DoesNotContain("Copy into Square", page);
            Assert.DoesNotContain("clipboard", page);
        }
    }

    // Undo hides a save from every page at once; coming back to the campaign within the grace
    // period offers it back, and nothing was deleted by leaving.
    [Fact]
    public async Task An_undone_campaign_is_gone_from_the_list_and_its_page_offers_restore()
    {
        await Get("/campaigns", [Features.Campaigns]);
        var store = app.Stores.Campaigns(SalonOne.Name);
        var onlySave = await store.SaveDraftAsync(Guid.NewGuid(), "Undone, all of it", DraftFixtures.Finished());
        var partly = Guid.NewGuid();
        await store.SaveDraftAsync(partly, "Kept version", DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        var undoneVersion = await store.SaveDraftAsync(partly, "Undone version", DraftFixtures.Finished());
        await store.MarkUndoneAsync(onlySave);
        await store.MarkUndoneAsync(undoneVersion);

        var (_, list) = await Get("/campaigns", [Features.Campaigns]);
        Assert.DoesNotContain("Undone, all of it", list);
        Assert.DoesNotContain("Undone version", list);
        Assert.Contains("Kept version", list);

        var (_, gone) = await Get($"/campaigns/{onlySave.Id}", [Features.Campaigns]);
        Assert.Contains("<h1>Undone, all of it</h1>", gone);
        Assert.Contains("Its last save was undone", gone);
        Assert.Contains(">Restore</button>", gone);

        var (_, editor) = await Get($"/campaigns/{partly}", [Features.Campaigns]);
        Assert.Contains("Save campaign", editor);
        Assert.Contains("Restore undone save", editor);
    }

    [Fact]
    public async Task The_client_header_and_how_it_works_lead_to_campaigns()
    {
        var (_, page) = await Get("/how-it-works", []);

        var nav = Regex.Match(page, "<nav class=\"site-nav\".*?</nav>", RegexOptions.Singleline).Value;
        Assert.Contains("href=\"campaigns\"", nav);
        Assert.Contains("Try it: <a href=\"campaigns\">Campaigns</a>.", page);
        Assert.DoesNotContain("Writing campaigns here is being built.", page);
    }
}
