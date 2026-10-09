using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

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
        Assert.Equal(TimeSpan.FromDays(1), app.Stores.Campaigns(SalonOne.Name).UndoGracePeriod);
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<UndoSweep>());
    }

    // Change the setting and restore and the sweep both follow it; nothing else holds a period.
    [Fact]
    public async Task A_grace_period_set_in_configuration_is_the_one_restore_and_the_sweep_use()
    {
        await Onboarded();
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Undo:GracePeriod"] = "02:00:00" })));
        var store = configured.Services.GetRequiredService<ClientStores>().Campaigns(SalonOne.Name);
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

    // Every client in the clients table is swept, and only what has been undone long enough goes.
    [Fact]
    public async Task One_sweep_covers_every_client_and_deletes_only_expired_undos()
    {
        await Onboarded();
        var stores = app.Stores;
        var expiredOne = await stores.Campaigns(SalonOne.Name).MarkUndoneAsync(
            await stores.Campaigns(SalonOne.Name).SaveDraftAsync(Guid.NewGuid(), "One, undone", DraftFixtures.Finished()));
        var expiredTwo = await stores.Campaigns(SalonTwo.Name).MarkUndoneAsync(
            await stores.Campaigns(SalonTwo.Name).SaveTemplateAsync(Guid.NewGuid(), DraftFixtures.Membership));
        var inUse = await stores.Campaigns(SalonTwo.Name).SaveDraftAsync(Guid.NewGuid(), "Two, in use", DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromDays(1);
        var fresh = await stores.Campaigns(SalonOne.Name).MarkUndoneAsync(
            await stores.Campaigns(SalonOne.Name).SaveDraftAsync(Guid.NewGuid(), "One, just undone", DraftFixtures.Finished()));

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
        var stores = new ClientStores(blobs.For, clock, TimeSpan.FromDays(1));
        var store = stores.Campaigns(SalonOne.Name);
        var undone = await store.MarkUndoneAsync(await store.SaveDraftAsync(Guid.NewGuid(), "Undone", DraftFixtures.Finished()));
        clock.Now += TimeSpan.FromDays(2);
        var sweep = new UndoSweep(stores, new InMemoryClientDirectory(SalonOne),
            Options.Create(new UndoOptions { GracePeriod = TimeSpan.FromDays(1) }), clock, NullLogger<UndoSweep>.Instance);

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
        var sweep = new UndoSweep(app.Stores, new UnreadableDirectory(),
            Options.Create(new UndoOptions { GracePeriod = TimeSpan.FromDays(1) }), app.Clock, NullLogger<UndoSweep>.Instance);

        Assert.Equal(0, await sweep.SweepOnceAsync());
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
