using System.Net;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>The templates list and editor, requested through the enforcing app.</summary>
public class TemplatePagesTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private static readonly Guid Membership = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, string[] roles, Guid[]? groups = null)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(SalonOne);
            await app.Stores.Campaigns(SalonOne.Name, Actor.Demo)
                .SaveTemplateAsync(Membership, DraftFixtures.Membership);
        }
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, groups ?? [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        // Razor encodes characters outside ASCII, such as the placeholders' ‹ and ›.
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData("/templates")]
    [InlineData("/templates/new")]
    [InlineData("/templates/aaaaaaaa-0000-0000-0000-000000000001")]
    public async Task Nobody_signed_in_is_challenged(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(path, [])).Status);

    // Writing campaigns and changing their layouts are granted separately.
    [Theory]
    [InlineData("/templates")]
    [InlineData("/templates/new")]
    public async Task Writing_campaigns_does_not_open_the_templates(string path) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(path, [Features.Campaigns, Features.Review])).Status);

    [Fact]
    public async Task A_member_sees_their_client_s_templates()
    {
        var (status, page) = await Get("/templates", [Features.Templates]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Membership announcement", page);
        Assert.Contains($"templates/{Membership}", page);
        // Her business by the name in the clients table, not its container's.
        // No possessive is built from the name ("Neelam Aesthetics's").
        Assert.Contains("Templates for Salon One: the layouts campaigns are written from.", page);
        Assert.DoesNotContain("Salon One's", page);
        Assert.DoesNotContain("test-salon-one", page);
        // In her time zone, as every page shows a time.
        Assert.Matches(@"^Last saved 3 Oct 2026, 5:0\d AM PDT$", RenderedPage.Named(page, $"last-saved-{Membership}"));
        Assert.DoesNotContain(" UTC", RenderedPage.Text(page));
    }

    // The role is not enough: whose templates comes from the client groups, through the access check.
    [Fact]
    public async Task The_role_without_a_client_shows_no_templates()
    {
        var (status, page) = await Get("/templates", [Features.Templates], groups: []);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("not a member of any client", page);
        Assert.DoesNotContain("Membership announcement", page);
    }

    [Fact]
    public async Task The_editor_opens_a_template_with_its_preview()
    {
        var (status, page) = await Get($"/templates/{Membership}", [Features.Templates]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("value=\"Membership announcement\"", page);
        Assert.Contains("‹Headline: written for each campaign›", page);
        Assert.Contains("Save template", page);
        Assert.Matches(@"^Last saved 3 Oct 2026, 5:0\d AM PDT\.$", RenderedPage.Named(page, "last-saved"));
    }

    // The guide under each block is the block catalog's (Catalog/blocks.json and rules.json): a
    // fine-print block says its text is the disclaimer, and no longer asks for one in a fine-print block.
    [Fact]
    public async Task The_editor_s_guide_and_add_block_list_come_from_the_block_catalog()
    {
        var (_, page) = await Get($"/templates/{Membership}", [Features.Templates]);
        var finePrint = DraftFixtures.Membership.Blocks.ToList().FindIndex(b => b.Type == BlockType.FinePrint);
        Assert.True(finePrint >= 0, "The membership template has a fine-print block.");

        var checks = RenderedPage.Named(page, $"block-checks-{finePrint}");
        Assert.Equal(
            "What the checks look for in this block " + string.Join(" ", Catalogs.Shipped.For(BlockType.FinePrint).Checks),
            checks);
        Assert.StartsWith("What the checks look for in this block Any text here counts as the disclaimer a medical term needs.", checks);
        Assert.DoesNotContain("A medical term needs a disclaimer in a fine-print block.", checks);
        // Every other text block still lists it.
        var heading = DraftFixtures.Membership.Blocks.ToList().FindIndex(b => b.Type == BlockType.Heading);
        Assert.Contains("A medical term needs a disclaimer in a fine-print block.", RenderedPage.Named(page, $"block-checks-{heading}"));

        // Every block type, in the catalog's order.
        Assert.Equal(
            string.Join(" ", Catalogs.Shipped.All.Select(g => $"{g.Label} {g.Description}")),
            RenderedPage.Named(page, "add-blocks"));
    }

    [Fact]
    public async Task Copy_starts_a_new_template_from_another()
    {
        await app.Stores.UseStandardBaselineAsync(SalonOne.Name, Actor.Demo);
        var (_, page) = await Get($"/templates/new?from={Membership}", [Features.Templates]);

        Assert.Contains("value=\"Copy of Membership announcement\"", page);
        // That template's blocks, not the baseline's.
        Assert.Equal(DraftFixtures.Membership.Blocks.Select(b => b.Label), BlockLabels(page));
        Assert.DoesNotContain(StartedWith, page);
    }

    // ---- A new template starts with the baseline (owner, 2026-10-09: what every template should
    // have is what you get when you say new). Each test leaves the standard baseline in force.

    private const string StartedWith = "Started with the parts every template should have.";

    private static List<string> BlockLabels(string page) =>
        Regex.Matches(page, "<section class=\"card block-card\" aria-label=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public async Task A_new_template_starts_with_the_standard_baseline_and_says_where_its_blocks_came_from()
    {
        await app.Stores.UseStandardBaselineAsync(SalonOne.Name, Actor.Demo);
        var (status, page) = await Get("/templates/new", [Features.Templates]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Header", "Heading", "Sign-off", "Image", "Button"], BlockLabels(page));
        Assert.Contains(StartedWith + " <a href=\"templates#baseline-heading\">Change that list on the Templates page.</a>", page);
        Assert.DoesNotContain("No blocks yet", page);
        // Nothing in the baseline is missing from it.
        Assert.DoesNotContain("Parts this template lacks", page);
        Assert.DoesNotContain("Your templates usually have", page);
    }

    [Fact]
    public async Task A_client_s_own_baseline_replaces_the_standard_one_at_the_start()
    {
        await app.Stores.Baseline(SalonOne.Name).SaveAsync(new TemplateBaseline([BlockType.Greeting, BlockType.Heading]));
        try
        {
            var (_, page) = await Get("/templates/new", [Features.Templates]);

            // In her order, not the order the editor lists block types in.
            Assert.Equal(["Greeting", "Heading"], BlockLabels(page));
            Assert.Contains(StartedWith, page);
        }
        finally
        {
            await app.Stores.UseStandardBaselineAsync(SalonOne.Name, Actor.Demo);
        }
    }

    [Fact]
    public async Task A_baseline_saved_empty_gives_a_blank_start()
    {
        await app.Stores.Baseline(SalonOne.Name).SaveAsync(TemplateBaseline.None);
        try
        {
            var (_, page) = await Get("/templates/new", [Features.Templates]);

            Assert.Empty(BlockLabels(page));
            Assert.Contains("No blocks yet", page);
            Assert.DoesNotContain(StartedWith, page);
        }
        finally
        {
            await app.Stores.UseStandardBaselineAsync(SalonOne.Name, Actor.Demo);
        }
    }

    [Fact]
    public async Task A_template_that_is_not_there_says_so()
    {
        var (status, page) = await Get($"/templates/{Guid.NewGuid()}", [Features.Templates]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Template not found", page);
    }
}
