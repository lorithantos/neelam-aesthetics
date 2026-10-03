using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class AccessCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly ClientName Neelam = new("neelam-aesthetics");
    private static readonly ClientName Other = new("other-salon");

    private static readonly Caller NeelamMember = new("neelam-owner", IsOperator: false, [Neelam]);
    private static readonly Caller OtherMember = new("other-owner", IsOperator: false, [Other]);
    private static readonly Caller Operator = new("operator", IsOperator: true, []);
    private static readonly Caller Stranger = new("stranger", IsOperator: false, []);

    private static AccessDecision Decide(Caller caller, Area area, ClientName? client, params SupportGrant[] grants) =>
        AccessCheck.Decide(caller, area, client, grants, Now);

    [Theory]
    [InlineData(Area.ClientData)]
    [InlineData(Area.Look)]
    public void A_member_reaches_their_own_client(Area area) =>
        Assert.True(Decide(NeelamMember, area, Neelam).Allowed);

    [Theory]
    [InlineData(Area.ClientData)]
    [InlineData(Area.Look)]
    public void A_member_reaches_nothing_of_another_client(Area area) =>
        Assert.False(Decide(OtherMember, area, Neelam).Allowed);

    [Theory]
    [InlineData(Area.ClientData)]
    [InlineData(Area.Look)]
    [InlineData(Area.Administration)]
    public void Someone_with_no_member_row_reaches_nothing(Area area) =>
        Assert.False(Decide(Stranger, area, area == Area.Administration ? null : Neelam).Allowed);

    [Fact]
    public void Only_the_operator_manages_clients_and_members()
    {
        Assert.True(Decide(Operator, Area.Administration, null).Allowed);
        Assert.False(Decide(NeelamMember, Area.Administration, null).Allowed);
    }

    [Fact]
    public void The_operator_works_on_any_client_s_look_without_a_grant()
    {
        Assert.True(Decide(Operator, Area.Look, Neelam).Allowed);
        Assert.True(Decide(Operator, Area.Look, Other).Allowed);
    }

    [Fact]
    public void The_operator_does_not_reach_client_data_without_a_grant()
    {
        var decision = Decide(Operator, Area.ClientData, Neelam);

        Assert.False(decision.Allowed);
        Assert.Contains("has not given support access", decision.Reason);
    }

    [Fact]
    public void A_grant_from_the_client_lets_the_operator_in_until_it_expires()
    {
        var grant = SupportGrant.Give(Neelam, NeelamMember, "the export looks wrong", TimeSpan.FromHours(48), Now);

        Assert.True(Decide(Operator, Area.ClientData, Neelam, grant).Allowed);
        Assert.False(AccessCheck.Decide(Operator, Area.ClientData, Neelam, [grant], Now.AddHours(48)).Allowed);
    }

    [Fact]
    public void A_grant_opens_only_the_client_that_gave_it()
    {
        var grant = SupportGrant.Give(Neelam, NeelamMember, "help", TimeSpan.FromHours(1), Now);

        Assert.False(Decide(Operator, Area.ClientData, Other, grant).Allowed);
    }

    [Fact]
    public void A_grant_does_not_count_before_it_was_given()
    {
        var grant = SupportGrant.Give(Neelam, NeelamMember, "help", TimeSpan.FromHours(1), Now.AddMinutes(5));

        Assert.False(Decide(Operator, Area.ClientData, Neelam, grant).Allowed);
    }

    [Fact]
    public void A_grant_never_widens_what_another_client_s_member_reaches()
    {
        var grant = SupportGrant.Give(Neelam, NeelamMember, "help", TimeSpan.FromHours(1), Now);

        Assert.False(Decide(OtherMember, Area.ClientData, Neelam, grant).Allowed);
    }

    // Neelam, until 1.0: a standing grant, which has no expiry and ends by being deleted.
    [Fact]
    public void A_standing_grant_lets_the_operator_in_with_no_expiry()
    {
        var grant = SupportGrant.Standing(Neelam, NeelamMember, "shaping the tool with Neelam until 1.0", Now);

        var decision = AccessCheck.Decide(Operator, Area.ClientData, Neelam, [grant], Now.AddYears(1));

        Assert.True(decision.Allowed);
        Assert.Contains("standing", decision.Reason);
    }

    // The point of a grant is that the client chose it.
    [Fact]
    public void Only_a_member_of_the_client_can_give_a_grant()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SupportGrant.Give(Neelam, Operator, "let me in", TimeSpan.FromHours(1), Now));
        Assert.Throws<InvalidOperationException>(() =>
            SupportGrant.Standing(Neelam, OtherMember, "let me in", Now));
    }

    [Fact]
    public void A_grant_needs_a_reason_and_a_duration()
    {
        Assert.Throws<ArgumentException>(() => SupportGrant.Give(Neelam, NeelamMember, " ", TimeSpan.FromHours(1), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportGrant.Give(Neelam, NeelamMember, "help", TimeSpan.Zero, Now));
    }

    [Fact]
    public void Client_areas_must_name_the_client_and_administration_must_not()
    {
        Assert.Throws<ArgumentNullException>(() => Decide(NeelamMember, Area.ClientData, null));
        Assert.Throws<ArgumentException>(() => Decide(Operator, Area.Administration, Neelam));
    }
}
