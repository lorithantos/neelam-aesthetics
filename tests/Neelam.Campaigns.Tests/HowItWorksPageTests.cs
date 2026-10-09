using System.Net;
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

        // The real email, every problem the rules find in it, and the corrected version.
        Assert.Contains("WE’RE TURNING ONE! 🥂✨", page);
        Assert.Contains("Must fix", page);
        Assert.All(CampaignReview.Check(BeautyBankEmail.SecondSend()).Blockers,
            blocker => Assert.Contains(blocker.Message, page));
        Assert.Contains("Join the Beauty Bank", page);
    }

    // The point of the separate page: a client never meets the operator's controls.
    [Theory]
    [InlineData("/")]
    [InlineData("/how-it-works")]
    public async Task Client_pages_carry_no_admin_links(string path)
    {
        var (_, page) = await Get(path);

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

    // Moved, not duplicated: the old mixed address answers nothing.
    [Fact]
    public async Task The_old_clients_address_is_gone()
    {
        var (status, _) = await Get("/clients", Features.Operator);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
