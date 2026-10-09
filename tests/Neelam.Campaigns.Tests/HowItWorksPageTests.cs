using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The page a client's people are sent to: the workflow, walked through with the Beauty Bank email,
/// and no admin in sight. Admin lives under /admin with its own layout.
/// </summary>
public class HowItWorksPageTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, params string[] roles)
    {
        if ((await app.Clients.ListAsync()).Count == 0) await app.Clients.AddAsync(SalonOne);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // Sent as a link, before sign-in exists: it shows no stored data, so nobody is challenged.
    [Fact]
    public async Task Anyone_can_open_it_without_signing_in()
    {
        var (status, _) = await Get("/how-it-works");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task It_walks_through_every_step_with_the_email_that_was_sent()
    {
        // Decoded: Blazor writes characters such as ’ and emoji as hex entities.
        var page = WebUtility.HtmlDecode((await Get("/how-it-works")).Page);

        foreach (var step in new[] { "1. Start from a template", "2. Write the campaign", "3. Check it, twice", "4. Approve", "5. Copy it into Square and send" })
            Assert.Contains(step, page);
        // Step 2 on Copy tier, as it behaves (owner, 2026-10-09).
        Assert.Contains("keeps the name and price until you change them, in either tier", page);

        // The template deduced from it: its name, which parts are fixed, and the placeholders.
        Assert.Contains("Membership announcement", page);
        Assert.Contains("written once", page);
        Assert.Contains("‹Opening: written for each campaign›", page);
        Assert.Contains("‹Photo: chosen for each campaign, or left out›", page);

        // The real email, every problem the rules find in it, and the corrected version.
        Assert.Contains("WE’RE TURNING ONE! 🥂✨", page);
        Assert.Contains("Must fix", page);
        Assert.All(CampaignReview.Check(BeautyBankEmail.SecondSend()).Blockers,
            blocker => Assert.Contains(blocker.Message, page));
        Assert.Contains("Join the Beauty Bank", page);
    }

    // The point of the separate page: a client never meets the operator's controls.
    [Theory]
    [InlineData("/", null)]
    [InlineData("/how-it-works", null)]
    [InlineData("/campaigns", Features.Campaigns)]
    [InlineData("/templates", Features.Templates)]
    public async Task Client_pages_carry_no_admin_links(string path, string? role)
    {
        var (status, page) = await (role is null ? Get(path) : Get(path, role));

        // A page that did not open would pass the rest for nothing.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("admin/", page);
        Assert.DoesNotContain(">Clients<", page);
        Assert.DoesNotContain("Entra", page);
    }

    [Fact]
    public async Task The_admin_page_says_it_is_admin_and_links_back_to_the_client_view()
    {
        var (status, page) = await Get("/admin/clients", Features.Operator);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Admin", page);
        Assert.Contains("Client view", page);
        Assert.DoesNotContain("how-it-works", page);
    }

    // Every layout carries Blazor's error bar, and it must stay hidden until Blazor shows it. A
    // layout's scoped CSS reaches only that layout's own markup, which once left the admin layout's
    // bar showing on every load. So the rule hiding it is global: the bare selector, in a stylesheet
    // the page links, and every stylesheet the page links is really there.
    [Theory]
    [InlineData("/", null)]
    [InlineData("/how-it-works", null)]
    [InlineData("/campaigns", Features.Campaigns)]
    [InlineData("/templates", Features.Templates)]
    [InlineData("/admin/clients", Features.Operator)]
    public async Task The_error_bar_is_hidden_by_a_global_rule_on_every_layout(string path, string? role)
    {
        var (status, page) = await (role is null ? Get(path) : Get(path, role));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("<div id=\"blazor-error-ui\">", page);

        var sheets = Regex.Matches(page, "<link rel=\"stylesheet\" href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(sheets);
        var css = new StringBuilder();
        foreach (var sheet in sheets)
        {
            var (sheetStatus, text) = await Get("/" + sheet);
            Assert.Equal(HttpStatusCode.OK, sheetStatus);
            css.Append(text).Append('\n');
        }

        var withoutComments = Regex.Replace(css.ToString(), @"/\*.*?\*/", "", RegexOptions.Singleline);
        var rule = Regex.Match(withoutComments, @"(?:^|[}\s])#blazor-error-ui\s*\{(?<body>[^}]*)\}");
        Assert.True(rule.Success, "No stylesheet the page links has a bare #blazor-error-ui rule.");
        Assert.Matches(@"(?:^|[;\s])display\s*:\s*none\s*(?:;|$)", rule.Groups["body"].Value);
    }

    // Moved, not duplicated: the old mixed address answers nothing.
    [Fact]
    public async Task The_old_clients_address_is_gone()
    {
        var (status, _) = await Get("/clients", Features.Operator);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
