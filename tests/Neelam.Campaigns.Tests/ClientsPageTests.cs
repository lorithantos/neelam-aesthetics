using System.Net;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Pages;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>The Clients page, requested through the enforcing app as nobody, a member and the operator.</summary>
public class ClientsPageTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One", "A hair salon in Tacoma.");

    private async Task<HttpResponseMessage> Get(params string[] roles)
    {
        if ((await app.Clients.ListAsync()).Count == 0) await app.Clients.AddAsync(SalonOne);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, [SalonOne.GroupId]);
        return await client.GetAsync("/clients");
    }

    [Fact]
    public async Task Nobody_signed_in_is_challenged() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get()).StatusCode);

    // A client's own member, with every role but Operator, still cannot manage clients.
    [Fact]
    public async Task A_member_without_the_operator_role_is_refused()
    {
        var response = await Get([.. Features.All.Where(f => f != Features.Operator)]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_operator_sees_each_client_with_its_description()
    {
        var response = await Get(Features.Operator);
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Salon One", page);
        Assert.Contains("A hair salon in Tacoma.", page);
        Assert.Contains("Add a client", page);
    }

    // ---- The form behind it

    [Fact]
    public void A_complete_form_makes_a_client_record()
    {
        var (client, errors) = new ClientForm
        {
            Name = " test-salon-one ",
            GroupId = SalonOne.GroupId.ToString(),
            DisplayName = " Salon One ",
            Description = "A hair salon\r\nin Tacoma.",
        }.ToRecord();

        Assert.Empty(errors);
        Assert.Equal(SalonOne with { Description = "A hair salon\nin Tacoma." }, client);
    }

    [Fact]
    public void A_blank_description_is_no_description() =>
        Assert.Null(ClientForm.Of(SalonOne with { Description = null }).ToRecord().Client!.Description);

    [Fact]
    public void Every_problem_with_a_form_is_said_at_once()
    {
        var (client, errors) = new ClientForm { Name = "Salon One", GroupId = "not-a-guid", DisplayName = " " }.ToRecord();

        Assert.Null(client);
        Assert.Equal(3, errors.Count);
        Assert.StartsWith("'Salon One' is not a client name", errors[0]);
        Assert.DoesNotContain("(Parameter", errors[0]);
    }

    [Fact]
    public void The_shared_settings_container_cannot_be_a_client() =>
        Assert.Contains("reserved", Assert.Single(new ClientForm
        {
            Name = "settings", GroupId = Guid.NewGuid().ToString(), DisplayName = "Settings",
        }.ToRecord().Errors));
}
