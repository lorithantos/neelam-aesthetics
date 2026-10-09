using System.Net;
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
        Assert.Contains("The layouts Salon One's campaigns", page);
        Assert.DoesNotContain("test-salon-one", page);
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
    }

    [Fact]
    public async Task Copy_starts_a_new_template_from_another()
    {
        var (_, page) = await Get($"/templates/new?from={Membership}", [Features.Templates]);

        Assert.Contains("value=\"Copy of Membership announcement\"", page);
    }

    [Fact]
    public async Task A_template_that_is_not_there_says_so()
    {
        var (status, page) = await Get($"/templates/{Guid.NewGuid()}", [Features.Templates]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Template not found", page);
    }
}
