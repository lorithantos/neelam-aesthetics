using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>Who the pages think is asking, in each mode.</summary>
public class CallerSourceTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientName SalonOne = new("test-salon-one");
    private static readonly ClientName SalonTwo = new("test-salon-two");
    private static readonly Guid SalonOneGroup = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SalonTwoGroup = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static IConfiguration Settings(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value)))
            .Build();

    private static ICallerSource Prototype(IConfiguration configuration) =>
        new ServiceCollection().AddLogging()
            .AddFeatureAccess(AccessMode.Prototype, new NamedEnvironment("Test"), configuration)
            .BuildServiceProvider()
            .GetRequiredService<ICallerSource>();

    // The prototype runs the real access check as the operator who is also a member of one client:
    // that client's data is reachable, another client's is not.
    [Fact]
    public async Task The_prototype_caller_is_the_operator_inside_its_one_client()
    {
        var (caller, _) = await Prototype(Settings(("Prototype:Client", "test-salon-one"))).CurrentAsync();

        Assert.NotNull(caller);
        Assert.True(caller.IsOperator);
        Assert.Equal([SalonOne], caller.MemberOf);
        Assert.True(AccessCheck.Decide(caller, Area.ClientData, SalonOne, [], DateTimeOffset.UtcNow).Allowed);
        Assert.False(AccessCheck.Decide(caller, Area.ClientData, SalonTwo, [], DateTimeOffset.UtcNow).Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not A Client")]
    [InlineData("settings")]
    public void The_prototype_refuses_to_start_without_a_valid_client(string? client)
    {
        var configuration = client is null ? Settings() : Settings(("Prototype:Client", client));

        Assert.ThrowsAny<Exception>(() => Prototype(configuration));
    }

    [Fact]
    public async Task Enforced_pages_get_the_caller_from_the_sign_in()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "user-1"), new Claim("groups", SalonTwoGroup.ToString())], authenticationType: "test"));
        var source = new SignInCallerSource(new FixedSignIn(user), new InMemoryClientDirectory(
            new ClientRecord(SalonOne, SalonOneGroup, "Salon One"),
            new ClientRecord(SalonTwo, SalonTwoGroup, "Salon Two")));

        var (caller, _) = await source.CurrentAsync();

        Assert.NotNull(caller);
        Assert.Equal("user-1", caller.UserId);
        Assert.False(caller.IsOperator);
        Assert.Equal([SalonTwo], caller.MemberOf);
    }

    [Fact]
    public async Task Enforced_pages_get_no_caller_without_a_sign_in()
    {
        var source = new SignInCallerSource(
            new FixedSignIn(new ClaimsPrincipal(new ClaimsIdentity())), new InMemoryClientDirectory());

        Assert.Null((await source.CurrentAsync()).Caller);
    }

    // The tests' switch to Enforced replaces the app's prototype caller, not only its policies.
    [Fact]
    public void The_enforced_app_has_no_prototype_caller()
    {
        using var scope = app.Services.CreateScope();

        Assert.IsType<SignInCallerSource>(Assert.Single(scope.ServiceProvider.GetServices<ICallerSource>()));
    }

    private sealed class FixedSignIn(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }
}
