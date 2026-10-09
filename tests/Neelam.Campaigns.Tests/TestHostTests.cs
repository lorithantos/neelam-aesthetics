using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>The seams the page tests stand on: stores over any container, the clients table, test sign-in.</summary>
public class TestHostTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientName SalonOne = new("test-salon-one");
    private static readonly Guid SalonOneGroup = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    // The client's own data goes in its own container; its look goes in settings, under its name.
    [Fact]
    public async Task Stores_open_the_client_s_own_container_and_its_place_in_settings()
    {
        var containers = new InMemoryContainers();
        var stores = new ClientStores(containers.For, Clock);

        await stores.Catalog(SalonOne).SaveAsync(new ClientCatalog([]));
        await stores.Images(SalonOne).AddAsync("Front desk", new BinaryData(new byte[] { 1 }), "image/png");
        await stores.Look(SalonOne).SaveAsync(new ClientLook("#997c61"));

        Assert.Equal(["settings", "test-salon-one"], containers.Names.Order(StringComparer.Ordinal).ToArray());
        Assert.All(containers.For("test-salon-one").Blobs.Keys, name => Assert.Matches("^(catalog|images)/", name));
        Assert.All(containers.For("settings").Blobs.Keys, name => Assert.StartsWith("test-salon-one/", name));
    }

    [Fact]
    public async Task A_client_s_description_and_display_name_can_be_changed()
    {
        var clients = new InMemoryClientDirectory(new ClientRecord(SalonOne, SalonOneGroup, "Salon One", "Hair"));

        await clients.UpdateAsync(new ClientRecord(SalonOne, SalonOneGroup, "Salon One & Spa", "Hair and skin"));
        Assert.Equal(new ClientRecord(SalonOne, SalonOneGroup, "Salon One & Spa", "Hair and skin"), Assert.Single(await clients.ListAsync()));

        await clients.UpdateAsync(new ClientRecord(SalonOne, SalonOneGroup, "Salon One & Spa"));
        Assert.Null(Assert.Single(await clients.ListAsync()).Description);
    }

    // Name and group are who a client is: an update cannot invent a client, move it to another
    // group (which would hand its data to that group's members), or leave it nameless.
    [Fact]
    public void An_update_cannot_change_who_a_client_is()
    {
        ClientRecord[] clients = [new(SalonOne, SalonOneGroup, "Salon One")];

        Assert.Throws<InvalidOperationException>(() => ClientDirectoryRules.CheckUpdate(clients,
            new ClientRecord(new ClientName("test-salon-two"), SalonOneGroup, "Salon Two")));
        Assert.Throws<InvalidOperationException>(() => ClientDirectoryRules.CheckUpdate(clients,
            new ClientRecord(SalonOne, Guid.NewGuid(), "Salon One")));
        Assert.Throws<ArgumentException>(() => ClientDirectoryRules.CheckUpdate(clients,
            new ClientRecord(SalonOne, SalonOneGroup, " ")));
    }

    [Fact]
    public async Task Test_sign_in_reads_roles_and_groups_and_is_anonymous_without_them()
    {
        var signedIn = await Authenticate(new()
        {
            [TestSignIn.RolesHeader] = "Operator, Campaigns.Templates",
            [TestSignIn.GroupsHeader] = SalonOneGroup.ToString(),
        });
        Assert.True(signedIn.Succeeded);
        Assert.Equal(TestSignIn.UserId, signedIn.Principal!.FindFirst("oid")?.Value);
        Assert.Equal(["Campaigns.Templates", "Operator"],
            signedIn.Principal.FindAll("roles").Select(c => c.Value).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(SalonOneGroup.ToString(), signedIn.Principal.FindFirst("groups")?.Value);

        Assert.False((await Authenticate(new())).Succeeded);
    }

    // The app under test reads its clients from memory, not from Azure. Its health answer is JSON:
    // the status and the server's own clock, in UTC with a Z.
    [Fact]
    public async Task The_enforced_app_runs_on_in_memory_storage()
    {
        var response = await app.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["status", "utc"], health.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("2026-10-03T12:00:00.0000000Z", health.RootElement.GetProperty("utc").GetString());
    }

    // A storage failure is still JSON, with the same shape and a 503 -- the code alone is the verdict
    // a deploy's poll reads, and the body says why without saying what is stored.
    [Fact]
    public async Task Health_reports_unreachable_storage_as_503_json()
    {
        using var broken = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.RemoveAll<IClientDirectory>().AddSingleton<IClientDirectory>(new UnreachableDirectory())));

        var response = await broken.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("storage unreachable", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("2026-10-03T12:00:00.0000000Z", health.RootElement.GetProperty("utc").GetString());
    }

    private sealed class UnreachableDirectory : IClientDirectory
    {
        public Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("No such host is known.");

        public Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(ClientRecord client, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // A scope per request, as the server gives: handlers and their results are cached per scope.
    private async Task<AuthenticateResult> Authenticate(Dictionary<string, string> headers)
    {
        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        return await context.AuthenticateAsync(TestSignIn.Scheme);
    }
}
