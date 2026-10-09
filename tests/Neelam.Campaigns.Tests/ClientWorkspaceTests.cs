using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

public class ClientWorkspaceTests
{
    private static readonly ClientName SalonOne = new("test-salon-one");
    private static readonly ClientName SalonTwo = new("test-salon-two");
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    private static Task<(ClientName? Client, string Reason)> For(Caller? caller) =>
        new ClientWorkspace(new FixedCaller(caller), new InMemorySupportGrants(), new InMemoryClientDirectory(), new InMemoryKnownItems(), Clock, new ListLogger<ClientWorkspace>())
            .ClientDataAsync();

    private static Task<string> DisplayName(ClientName client, params ClientRecord[] table) =>
        new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(), new InMemoryClientDirectory(table), new InMemoryKnownItems(), Clock, new ListLogger<ClientWorkspace>())
            .DisplayNameAsync(client);

    // Known items reach the checks with the registration, the way the phone numbers do, and only the
    // asking client's: salon two's items never reach salon one's checks.
    [Fact]
    public async Task The_business_carries_the_client_s_own_known_items()
    {
        var known = new InMemoryKnownItems();
        await known.AddAsync(SalonOne, new KnownTreatment(KnownItem.NewId(), "Wellness injection"));
        await known.AddAsync(SalonTwo, new KnownTreatment(KnownItem.NewId(), "Hydrafacial"));
        var workspace = new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(),
            new InMemoryClientDirectory(new ClientRecord(SalonOne, Guid.NewGuid(), "Neelam Aesthetics")), known, Clock, new ListLogger<ClientWorkspace>());

        var one = await workspace.BusinessAsync(SalonOne);
        var two = await workspace.BusinessAsync(SalonTwo);

        Assert.Equal("Neelam Aesthetics", one!.Name);
        Assert.Equal(["Wellness injection"], one.Known.All.Select(i => i.Text));
        // No row for salon two: named by its client name, with its own items only.
        Assert.Equal("test-salon-two", two!.Name);
        Assert.Equal(["Hydrafacial"], two.Known.All.Select(i => i.Text));
        Assert.Null(await new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(),
            new InMemoryClientDirectory(), new InMemoryKnownItems(), Clock, new ListLogger<ClientWorkspace>()).BusinessAsync(SalonOne));
    }

    // Two registered clients, each with its own phones and description: each campaign is checked
    // against its own client's registration only, whichever row the table lists first.
    [Fact]
    public async Task Each_client_s_registration_is_its_own()
    {
        static PhoneNumbers Phones(params string[] written) =>
            new(written.Select(w => PhoneNumber.TryParse(w, out var n) ? n : throw new FormatException(w)));
        var one = new ClientRecord(SalonOne, Guid.NewGuid(), "Neelam Aesthetics", "Medical aesthetics in Snohomish")
            { Phones = Phones("425-877-8646") };
        var two = new ClientRecord(SalonTwo, Guid.NewGuid(), "Salon Two Spa", "Hair and nails in Everett")
            { Phones = Phones("(425) 555-0100", "425-555-0199") };

        foreach (var table in new[] { new[] { one, two }, new[] { two, one } })
        {
            var workspace = new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(),
                new InMemoryClientDirectory(table), new InMemoryKnownItems(), Clock, new ListLogger<ClientWorkspace>());

            Assert.Equal(one.Business, await workspace.BusinessAsync(SalonOne));
            Assert.Equal(two.Business, await workspace.BusinessAsync(SalonTwo));
            Assert.Equal("Medical aesthetics in Snohomish", (await workspace.BusinessAsync(SalonOne))!.Description);
            Assert.Equal("Hair and nails in Everett", (await workspace.BusinessAsync(SalonTwo))!.Description);
        }
    }

    // The name her pages call her business by is data in the clients table, whoever the client is.
    [Fact]
    public async Task A_client_is_called_by_its_display_name_from_the_clients_table()
    {
        var table = new[]
        {
            new ClientRecord(SalonOne, Guid.NewGuid(), "Neelam Aesthetics"),
            new ClientRecord(SalonTwo, Guid.NewGuid(), "Salon Two Spa"),
        };

        Assert.Equal("Neelam Aesthetics", await DisplayName(SalonOne, table));
        Assert.Equal("Salon Two Spa", await DisplayName(SalonTwo, table));
    }

    [Fact]
    public async Task Without_a_row_or_a_display_name_a_client_is_called_by_its_name()
    {
        Assert.Equal("test-salon-one", await DisplayName(SalonOne));
        Assert.Equal("test-salon-one", await DisplayName(SalonOne, new ClientRecord(SalonTwo, Guid.NewGuid(), "Salon Two Spa")));
        Assert.Equal("test-salon-one", await DisplayName(SalonOne, new ClientRecord(SalonOne, Guid.NewGuid(), "  ")));
    }

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

    // Who the pages put on the activity trail: the signed-in user's name, or the prototype's "demo user".
    [Fact]
    public async Task The_actor_is_the_signed_in_user_or_the_demo_user()
    {
        static Task<Actor> ActorOf(ICallerSource callers) =>
            new ClientWorkspace(callers, new InMemorySupportGrants(), new InMemoryClientDirectory(), new InMemoryKnownItems(), Clock, new ListLogger<ClientWorkspace>()).ActorAsync();

        Assert.Equal(new Actor("Priya Sharma", true),
            await ActorOf(new FixedCaller(new Caller("u", false, [SalonOne]) { Name = "Priya Sharma" })));
        Assert.Equal(Actor.Demo, await ActorOf(new PrototypeCallerSource(SalonOne)));
        Assert.Equal(Actor.Demo, await ActorOf(new FixedCaller(null)));
    }

    // Before the knownItems table is deployed, reading it fails: the campaign page still opens, with
    // its registration and no known items, and the failure is logged.
    [Fact]
    public async Task Known_items_that_cannot_be_read_are_logged_and_taken_as_none()
    {
        var log = new ListLogger<ClientWorkspace>();
        var workspace = new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(),
            new InMemoryClientDirectory(new ClientRecord(SalonOne, Guid.NewGuid(), "Neelam Aesthetics")),
            new UnreadableKnownItems(), Clock, log);

        var business = await workspace.BusinessAsync(SalonOne);

        Assert.Equal("Neelam Aesthetics", business!.Name);
        Assert.True(business.Known.IsEmpty);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.IsType<Azure.RequestFailedException>(entry.Error);
    }

    // One bad known-items row does not take the campaign page down: it is left out, logged by its
    // row key and never its content, and the rest still reach the checks.
    [Fact]
    public async Task A_malformed_known_item_is_left_out_and_the_rest_reach_the_checks()
    {
        var known = new InMemoryKnownItems();
        await known.AddAsync(SalonOne, new KnownTreatment(KnownItem.NewId(), "Wellness injection"));
        var bad = KnownItem.NewId();
        known.PutRow(new Azure.Data.Tables.TableEntity(SalonOne.Value, bad)
        {
            ["Kind"] = "Tier", ["Text"] = "Secret Tier", ["Price"] = "two hundred", ["Benefits"] = "[]",
        });
        var log = new ListLogger<ClientWorkspace>();
        var workspace = new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(),
            new InMemoryClientDirectory(new ClientRecord(SalonOne, Guid.NewGuid(), "Neelam Aesthetics")), known, Clock, log);

        var business = await workspace.BusinessAsync(SalonOne);

        Assert.Equal(["Wellness injection"], business!.Known.All.Select(i => i.Text));
        Assert.Empty(log.Entries);
        var entry = Assert.Single(known.Log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.Contains(bad, entry.Message);
        Assert.DoesNotContain("Secret Tier", entry.Message);
        Assert.DoesNotContain("two hundred", entry.Message);
    }

    // Anything else the known-items read throws (here a row of the wrong partition, which the store
    // refuses outright) leaves the page with no known items rather than no page.
    [Fact]
    public async Task Known_items_that_fail_in_any_way_are_logged_and_taken_as_none()
    {
        var log = new ListLogger<ClientWorkspace>();
        var workspace = new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(),
            new InMemoryClientDirectory(new ClientRecord(SalonOne, Guid.NewGuid(), "Neelam Aesthetics")),
            new UnreadableKnownItems(new InvalidDataException("A known item of another client was read for test-salon-one.")), Clock, log);

        var business = await workspace.BusinessAsync(SalonOne);

        Assert.True(business!.Known.IsEmpty);
        Assert.IsType<InvalidDataException>(Assert.Single(log.Entries).Error);
    }

    // A malformed client row is not skipped: the registration decides which phone numbers the email
    // may carry, so the page says it cannot check the campaign rather than checking it as if none.
    [Fact]
    public async Task A_malformed_client_record_fails_with_a_clear_message_and_is_logged()
    {
        var log = new ListLogger<ClientWorkspace>();
        var workspace = new ClientWorkspace(new FixedCaller(null), new InMemorySupportGrants(),
            new UnreadableClients(), new InMemoryKnownItems(), Clock, log);

        var ex = await Assert.ThrowsAsync<RegistrationUnreadableException>(() => workspace.BusinessAsync(SalonOne));

        Assert.Equal("Your business's details couldn't be read, so this campaign can't be checked yet. Nothing has been changed, and the problem has been logged for fixing.", ex.Message);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.IsType<InvalidDataException>(entry.Error);
    }

    // The clients table with a row the real mapping refuses: a client with no Entra group.
    private sealed class UnreadableClients : IClientDirectory
    {
        public async Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return [TableMetadata.ToClient(new Azure.Data.Tables.TableEntity("client", SalonOne.Value)
            {
                ["DisplayName"] = "Neelam Aesthetics",
            })];
        }

        public Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(ClientRecord client, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnreadableKnownItems(Exception? failure = null) : IKnownItemStore
    {
        public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default) =>
            throw failure ?? new Azure.RequestFailedException(404, "The table specified does not exist.");

        public Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedCaller(Caller? caller) : ICallerSource
    {
        public Task<(Caller? Caller, string Reason)> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<(Caller?, string)>((caller, "not signed in"));
    }
}
