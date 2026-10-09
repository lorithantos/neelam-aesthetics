using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Undo's grace period as the app is configured with it, and the background sweep that deletes
/// undone saves once it has passed. In memory, on the test's clock.
/// </summary>
public class UndoSweepTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private static readonly ClientRecord SalonTwo = new(
        new ClientName("test-salon-two"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "Salon Two");

    private async Task Onboarded()
    {
        if ((await app.Clients.ListAsync()).Count > 0) return;
        await app.Clients.AddAsync(SalonOne);
        await app.Clients.AddAsync(SalonTwo);
    }

    private static UndoSweep SweepOf(IServiceProvider services) =>
        services.GetServices<IHostedService>().OfType<UndoSweep>().Single();

    // The owner's decision lives in appsettings.json: a day to change her mind, swept hourly.
    [Fact]
    public void The_app_takes_its_grace_period_from_its_settings()
    {
        var undo = app.Services.GetRequiredService<IOptions<UndoOptions>>().Value;

        Assert.Equal(TimeSpan.FromDays(1), undo.GracePeriod);
        Assert.Equal(TimeSpan.FromHours(1), undo.SweepInterval);
        Assert.Equal(TimeSpan.FromDays(1), app.Stores.Campaigns(SalonOne.Name, Actor.Demo).UndoGracePeriod);
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<UndoSweep>());
    }

    // Change the setting and restore and the sweep both follow it; nothing else holds a period.
    [Fact]
    public async Task A_grace_period_set_in_configuration_is_the_one_restore_and_the_sweep_use()
    {
        await Onboarded();
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Undo:GracePeriod"] = "02:00:00" })));
        var store = configured.Services.GetRequiredService<ClientStores>().Campaigns(SalonOne.Name, Actor.Demo);
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "Configured", DraftFixtures.Finished());
        var undone = await store.MarkUndoneAsync(save);

        // Blob states, not counts: the app's own sweep, run at its start, shares these containers.
        var blobs = app.Containers.For(SalonOne.Name.Value).Blobs;
        app.Clock.Now = undone.UndoneAt!.Value + TimeSpan.FromHours(2) - TimeSpan.FromTicks(1);
        Assert.NotNull(await store.RestorableAsync(DocumentKind.Draft, save.Id));
        await SweepOf(configured.Services).SweepOnceAsync();
        Assert.Contains(save.BlobName, blobs.Keys);

        app.Clock.Now += TimeSpan.FromTicks(1);
        Assert.Null(await store.RestorableAsync(DocumentKind.Draft, save.Id));
        await SweepOf(configured.Services).SweepOnceAsync();
        Assert.DoesNotContain(save.BlobName, blobs.Keys);
    }

    [Fact]
    public void Without_a_positive_grace_period_the_settings_are_refused()
    {
        Assert.NotNull(new UndoOptions().Problem());
        Assert.NotNull(new UndoOptions { GracePeriod = TimeSpan.FromDays(1), SweepInterval = TimeSpan.Zero }.Problem());
        Assert.Null(new UndoOptions { GracePeriod = TimeSpan.FromDays(1) }.Problem());
    }

    // The sweep's timer throws on an interval it cannot wait (over about 49.7 days, or under a
    // millisecond), which would stop the app; such a setting is refused at startup instead.
    [Fact]
    public void A_sweep_interval_the_timer_cannot_wait_is_refused()
    {
        UndoOptions With(TimeSpan interval) => new() { GracePeriod = TimeSpan.FromDays(1), SweepInterval = interval };

        Assert.Contains("at most 49", With(TimeSpan.FromDays(50)).Problem());
        Assert.NotNull(With(TimeSpan.FromDays(49) + TimeSpan.FromTicks(1)).Problem());
        Assert.NotNull(With(TimeSpan.FromTicks(1)).Problem());
        Assert.Null(With(UndoOptions.LongestSweepInterval).Problem());
        Assert.Null(With(TimeSpan.FromMilliseconds(1)).Problem());

        // The bound is the timer's own: what is allowed it takes, what is not it refuses.
        new PeriodicTimer(UndoOptions.LongestSweepInterval).Dispose();
        new PeriodicTimer(TimeSpan.FromMilliseconds(1)).Dispose();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodicTimer(TimeSpan.FromDays(50)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodicTimer(TimeSpan.FromTicks(1)));
    }

    // Every client in the clients table is swept, and only what has been undone long enough goes.
    [Fact]
    public async Task One_sweep_covers_every_client_and_deletes_only_expired_undos()
    {
        await Onboarded();
        var stores = app.Stores;
        var expiredOne = await stores.Campaigns(SalonOne.Name, Actor.Demo).MarkUndoneAsync(
            await stores.Campaigns(SalonOne.Name, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "One, undone", DraftFixtures.Finished()));
        var expiredTwo = await stores.Campaigns(SalonTwo.Name, Actor.Demo).MarkUndoneAsync(
            await stores.Campaigns(SalonTwo.Name, Actor.Demo).SaveTemplateAsync(Guid.NewGuid(), DraftFixtures.Membership));
        var inUse = await stores.Campaigns(SalonTwo.Name, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Two, in use", DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromDays(1);
        var fresh = await stores.Campaigns(SalonOne.Name, Actor.Demo).MarkUndoneAsync(
            await stores.Campaigns(SalonOne.Name, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "One, just undone", DraftFixtures.Finished()));

        await SweepOf(app.Services).SweepOnceAsync();

        var one = app.Containers.For(SalonOne.Name.Value).Blobs.Keys;
        var two = app.Containers.For(SalonTwo.Name.Value).Blobs.Keys;
        Assert.DoesNotContain(expiredOne.BlobName, one);
        Assert.DoesNotContain(expiredTwo.BlobName, two);
        Assert.Contains(fresh.BlobName, one);
        Assert.Contains(inUse.BlobName, two);
    }

    // The first sweep runs as the app starts, so a restart never postpones one.
    [Fact]
    public async Task The_sweep_runs_once_as_it_starts()
    {
        var blobs = new InMemoryContainers();
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var stores = new ClientStores(blobs.For, clock, TimeSpan.FromDays(1), new InMemoryApprovals(), TestRecords.Unwatched);
        var store = stores.Campaigns(SalonOne.Name, Actor.Demo);
        var undone = await store.MarkUndoneAsync(await store.SaveDraftAsync(Guid.NewGuid(), "Undone", DraftFixtures.Finished()));
        clock.Now += TimeSpan.FromDays(2);
        var sweep = SweepOver(stores, new InMemoryClientDirectory(SalonOne), clock);

        // The service runs off the startup path, so the test waits for it, a few seconds at most;
        // the next run would be an hour away.
        await sweep.StartAsync(CancellationToken.None);
        var keys = blobs.For(SalonOne.Name.Value).Blobs.Keys;
        for (var wait = 0; wait < 100 && keys.Contains(undone.BlobName); wait++) await Task.Delay(50);
        await sweep.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(undone.BlobName, keys);
    }

    // A clients table that cannot be read is logged and tried again next run; it never stops the app.
    [Fact]
    public async Task A_sweep_that_cannot_read_the_clients_table_deletes_nothing_and_does_not_throw()
    {
        var sweep = SweepOver(app.Stores, new UnreadableDirectory(), app.Clock);

        Assert.Equal(0, await sweep.SweepOnceAsync());
    }

    // In Prototype mode the site works in Prototype:Client whether or not the clients table has a
    // row for it, so its undone saves are swept either way; not even an unreadable table stops that.
    [Fact]
    public async Task The_prototype_client_is_swept_with_no_row_in_the_clients_table()
    {
        var (containers, clock, stores) = Isolated();
        var store = stores.Campaigns(SalonOne.Name, Actor.Demo);
        var expired = await store.MarkUndoneAsync(await store.SaveDraftAsync(Guid.NewGuid(), "Undone", DraftFixtures.Finished()));
        clock.Now += TimeSpan.FromDays(1);
        var fresh = await store.MarkUndoneAsync(await store.SaveDraftAsync(Guid.NewGuid(), "Just undone", DraftFixtures.Finished()));
        var keys = containers.For(SalonOne.Name.Value).Blobs.Keys;
        var prototype = new PrototypeCallerSource(SalonOne.Name);

        Assert.Equal(0, await SweepOver(stores, new InMemoryClientDirectory(), clock).SweepOnceAsync());
        Assert.Equal(1, await SweepOver(stores, new InMemoryClientDirectory(), clock, prototype).SweepOnceAsync());
        Assert.DoesNotContain(expired.BlobName, keys);
        Assert.Contains(fresh.BlobName, keys);

        clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(1, await SweepOver(stores, new UnreadableDirectory(), clock, prototype).SweepOnceAsync());
        Assert.Empty(keys);
    }

    // A prototype client that also has a row is still swept once a run, not twice.
    [Fact]
    public async Task A_prototype_client_with_a_row_is_swept_once()
    {
        var containers = new InMemoryContainers();
        var opened = new List<string>();
        var clock = new ManualClock(Noon);
        var stores = new ClientStores(name => { lock (opened) opened.Add(name); return containers.For(name); }, clock, TimeSpan.FromDays(1), new InMemoryApprovals(), TestRecords.Unwatched);

        await SweepOver(stores, new InMemoryClientDirectory(SalonOne, SalonTwo), clock, new PrototypeCallerSource(SalonOne.Name))
            .SweepOnceAsync();

        Assert.Equal([SalonOne.Name.Value, SalonTwo.Name.Value], opened.Order(StringComparer.Ordinal));
    }

    // One client whose container cannot be reached is logged and left for the next run; the
    // clients after it are still swept.
    [Fact]
    public async Task One_client_failing_does_not_stop_the_sweep_of_the_others()
    {
        var (containers, clock, _) = Isolated();
        var stores = new ClientStores(
            name => name == SalonOne.Name.Value ? new UnreachableContainer() : containers.For(name), clock, TimeSpan.FromDays(1),
            new InMemoryApprovals(), TestRecords.Unwatched);
        var store = stores.Campaigns(SalonTwo.Name, Actor.Demo);
        var expired = await store.MarkUndoneAsync(await store.SaveDraftAsync(Guid.NewGuid(), "Undone", DraftFixtures.Finished()));
        clock.Now += TimeSpan.FromDays(1);

        // Salon One is listed first, so it fails before Salon Two is reached.
        Assert.Equal(1, await SweepOver(stores, new InMemoryClientDirectory(SalonOne, SalonTwo), clock).SweepOnceAsync());
        Assert.DoesNotContain(expired.BlobName, containers.For(SalonTwo.Name.Value).Blobs.Keys);
    }

    private static readonly DateTimeOffset Noon = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static (InMemoryContainers Containers, ManualClock Clock, ClientStores Stores) Isolated()
    {
        var containers = new InMemoryContainers();
        var clock = new ManualClock(Noon);
        return (containers, clock, new ClientStores(containers.For, clock, TimeSpan.FromDays(1), new InMemoryApprovals(), TestRecords.Unwatched));
    }

    private static UndoSweep SweepOver(
        ClientStores stores, IClientDirectory clients, TimeProvider clock, params PrototypeCallerSource[] prototype) =>
        new(stores, clients, prototype, Options.Create(new UndoOptions { GracePeriod = TimeSpan.FromDays(1) }), clock,
            NullLogger<UndoSweep>.Instance);

    private sealed class UnreachableContainer : IBlobBackend
    {
        private static HttpRequestException Unreachable() => new("No such host is known.");

        public async IAsyncEnumerable<BlobEntry> ListAsync(
            string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw Unreachable();
#pragma warning disable CS0162 // An iterator needs a yield, though this one never reaches it.
            yield break;
#pragma warning restore CS0162
        }

        public Task<bool> TryCreateAsync(
            string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<Janet.Azure.Storage.BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default) =>
            throw Unreachable();

        public Task<bool> SetMetadataAsync(
            string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
            throw Unreachable();

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<bool> DeleteIfUnchangedAsync(string name, string etag, CancellationToken cancellationToken = default) =>
            throw Unreachable();
    }

    private sealed class UnreadableDirectory : IClientDirectory
    {
        public Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("No such host is known.");

        public Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(ClientRecord client, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
