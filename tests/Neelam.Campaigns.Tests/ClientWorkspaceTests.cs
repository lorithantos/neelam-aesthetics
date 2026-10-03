using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

public class ClientWorkspaceTests
{
    private static readonly ClientName SalonOne = new("test-salon-one");
    private static readonly ClientName SalonTwo = new("test-salon-two");
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    private static Task<(ClientName? Client, string Reason)> For(Caller? caller) =>
        new ClientWorkspace(new FixedCaller(caller), new InMemorySupportGrants(), Clock).ClientDataAsync();

    [Fact]
    public async Task A_member_of_one_client_works_in_it() =>
        Assert.Equal(SalonOne, (await For(new Caller("u", false, [SalonOne]))).Client);

    [Fact]
    public async Task No_caller_no_membership_or_several_give_no_client()
    {
        Assert.Equal((null, "not signed in"), await For(null));
        Assert.Equal((null, "you are not a member of any client"), await For(new Caller("u", true, [])));
        Assert.Null((await For(new Caller("u", false, [SalonOne, SalonTwo]))).Client);
    }

    private sealed class FixedCaller(Caller? caller) : ICallerSource
    {
        public Task<(Caller? Caller, string Reason)> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<(Caller?, string)>((caller, "not signed in"));
    }
}
