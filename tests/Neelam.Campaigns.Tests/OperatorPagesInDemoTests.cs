using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// On the demo the operator's pages do not exist (owner, 2026-10-09): Prototype lets everyone
/// through every policy, so an admin page there would let anyone with its address change a client.
/// </summary>
public class OperatorPagesInDemoTests(DemoApp app) : IClassFixture<DemoApp>
{
    private IReadOnlyList<RouteEndpoint> Endpoints =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    private IReadOnlyList<string> OperatorRoutes =>
        Endpoints.Where(OperatorPagesInDemo.IsOperatorOnly).Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct().Order(StringComparer.Ordinal).ToList();

    // What the gate covers is found from each endpoint's policy. Today that is the Clients page; every
    // page under /admin is among them, so none can slip out from under the gate by its address.
    [Fact]
    public void Every_admin_page_is_an_operator_page()
    {
        Assert.Equal(["/admin/clients"], OperatorRoutes);
        var admin = Endpoints.Where(e => (e.RoutePattern.RawText ?? "").TrimStart('/').StartsWith("admin", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(admin);
        Assert.All(admin, e => Assert.True(OperatorPagesInDemo.IsOperatorOnly(e), e.RoutePattern.RawText));
    }

    // A plain 404, the same answer as an address with no page at all.
    [Fact]
    public async Task Each_operator_page_is_not_found_on_the_demo()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var nothing = await client.GetAsync("/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);

        foreach (var route in OperatorRoutes)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
            {
                var response = await client.SendAsync(new HttpRequestMessage(method, route));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal(await nothing.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
            }
        }
    }

    // Nothing she can see points at an admin page.
    [Theory]
    [InlineData("/")]
    [InlineData("/how-it-works")]
    [InlineData("/campaigns")]
    [InlineData("/templates")]
    [InlineData("/images")]
    [InlineData("/known-items")]
    public async Task No_client_page_links_to_an_admin_page(string path)
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.DoesNotMatch("href=\"/?admin", await response.Content.ReadAsStringAsync());
    }
}
