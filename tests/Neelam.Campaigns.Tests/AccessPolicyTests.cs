using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

public class AccessPolicyTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static ClaimsPrincipal SignedIn(params string[] roles) =>
        new(new ClaimsIdentity(
            [new Claim("oid", "user-1"), .. roles.Select(r => new Claim("roles", r))], authenticationType: "test"));

    private async Task<bool> Allowed(ClaimsPrincipal user, string feature) =>
        (await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, feature)).Succeeded;

    // The contract: no page or endpoint can ship without saying who may reach it. Framework plumbing
    // (paths starting "_", such as the Blazor hub) is not ours to gate.
    [Fact]
    public void Every_page_and_endpoint_names_a_policy_or_is_explicitly_public()
    {
        var ungated = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => !(e.RoutePattern.RawText ?? "").TrimStart('/').StartsWith('_'))
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => Features.All.Contains(a.Policy)))
            .Where(e => !IsStaticAssetsDevelopmentFallback(e))
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.True(ungated.Count == 0, "Name a Features policy, or mark it [AllowAnonymous]: " + string.Join(", ", ungated));
    }

    // Also framework plumbing: MapStaticAssets, run from build output (as here and in a local run, never
    // in a published app), adds a fallback that serves a file from wwwroot changed since the build. It
    // takes none of the conventions given to MapStaticAssets, and holds nothing but the site's own files.
    private static bool IsStaticAssetsDevelopmentFallback(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText == "{**path:file}" && endpoint.DisplayName == "Fallback {**path:file}";

    [Fact]
    public void The_contract_sees_the_app_s_own_pages_and_endpoints()
    {
        var patterns = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();

        Assert.Contains("/", patterns);
        Assert.Contains("/healthz", patterns);
    }

    [Theory]
    [MemberData(nameof(EveryFeature))]
    public async Task A_feature_is_reached_only_with_its_own_app_role(string feature)
    {
        Assert.True(await Allowed(SignedIn(feature), feature));
        Assert.False(await Allowed(SignedIn(Features.All.Where(f => f != feature).ToArray()), feature));
        Assert.False(await Allowed(new ClaimsPrincipal(new ClaimsIdentity()), feature));
    }

    // A sign-in Janet.Entra cannot read completely holds no role, whatever roles it lists.
    [Fact]
    public async Task A_group_overage_sign_in_reaches_no_feature()
    {
        var overage = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "u"), new Claim("roles", Features.Campaigns), new Claim("_claim_names", "{\"groups\":\"src1\"}")],
            authenticationType: "test"));

        Assert.False(await Allowed(overage, Features.Campaigns));
    }

    [Fact]
    public void Prototype_access_refuses_to_run_in_production() =>
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddFeatureAccess(AccessMode.Prototype, new NamedEnvironment(Environments.Production),
                CallerSourceTests.Settings(("Prototype:Client", "test-salon-one"))));

    [Fact]
    public async Task Prototype_access_lets_everyone_through_where_it_may_run()
    {
        var services = new ServiceCollection().AddLogging()
            .AddFeatureAccess(AccessMode.Prototype, new NamedEnvironment("Test"),
                CallerSourceTests.Settings(("Prototype:Client", "test-salon-one")))
            .BuildServiceProvider();

        var result = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), Features.Operator);
        Assert.True(result.Succeeded);
    }

    public static TheoryData<string> EveryFeature()
    {
        var data = new TheoryData<string>();
        foreach (var feature in Features.All) data.Add(feature);
        return data;
    }
}
