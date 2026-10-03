using System.Security.Claims;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class CallerClaimsTests
{
    private static readonly Guid NeelamGroup = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherGroup = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private static readonly ClientRecord[] Clients =
    [
        new(new ClientName("neelam-aesthetics"), NeelamGroup, "Neelam Aesthetics"),
        new(new ClientName("other-salon"), OtherGroup, "Other Salon"),
    ];

    private static ClaimsPrincipal SignedIn(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));

    [Fact]
    public void Membership_comes_from_the_client_groups_in_the_token()
    {
        var (caller, _) = CallerClaims.FromClaims(
            SignedIn(("oid", "user-1"), ("groups", NeelamGroup.ToString()), ("groups", Guid.NewGuid().ToString())), Clients);

        Assert.NotNull(caller);
        Assert.Equal("user-1", caller.UserId);
        Assert.Equal([new ClientName("neelam-aesthetics")], caller.MemberOf);
        Assert.False(caller.IsOperator);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData(ClaimTypes.Role)]
    public void The_operator_role_comes_from_the_token(string roleClaim)
    {
        var (caller, _) = CallerClaims.FromClaims(SignedIn(("oid", "operator"), (roleClaim, "Operator")), Clients);

        Assert.True(caller!.IsOperator);
        Assert.Empty(caller.MemberOf);
    }

    [Fact]
    public void The_long_object_id_claim_name_is_read_too()
    {
        var (caller, _) = CallerClaims.FromClaims(
            SignedIn(("http://schemas.microsoft.com/identity/claims/objectidentifier", "user-2")), Clients);

        Assert.Equal("user-2", caller!.UserId);
    }

    [Fact]
    public void A_group_that_is_no_client_s_grants_nothing()
    {
        var (caller, _) = CallerClaims.FromClaims(SignedIn(("oid", "user-3"), ("groups", Guid.NewGuid().ToString())), Clients);

        Assert.Empty(caller!.MemberOf);
        Assert.False(AccessCheck.Decide(caller, Area.ClientData, Clients[0].Name, [], DateTimeOffset.UtcNow).Allowed);
    }

    [Fact]
    public void Not_signed_in_gives_no_caller() =>
        Assert.Null(CallerClaims.FromClaims(new ClaimsPrincipal(new ClaimsIdentity()), Clients).Caller);

    [Fact]
    public void A_token_without_an_object_id_gives_no_caller()
    {
        var (caller, reason) = CallerClaims.FromClaims(SignedIn(("groups", NeelamGroup.ToString())), Clients);

        Assert.Null(caller);
        Assert.Contains("object ID", reason);
    }

    // Too many groups and Entra sends a pointer instead of the list; a partial reading is refused.
    [Fact]
    public void A_group_overage_token_gives_no_caller()
    {
        var (caller, reason) = CallerClaims.FromClaims(
            SignedIn(("oid", "user-4"), ("_claim_names", "{\"groups\":\"src1\"}")), Clients);

        Assert.Null(caller);
        Assert.Contains("too many groups", reason);
    }
}
