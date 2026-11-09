using System.Globalization;
using System.Text.Json.Nodes;
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
        var container = app.Containers.For(SalonOne.Name.Value);
        app.Clock.Now = undone.UndoneAt!.Value + TimeSpan.FromHours(2) - TimeSpan.FromTicks(1);
        Assert.NotNull(await store.RestorableAsync(DocumentKind.Draft, save.Id));
        await SweepOf(configured.Services).SweepOnceAsync();
        Assert.Contains(save.BlobName, container.Blobs.Keys);

        app.Clock.Now += TimeSpan.FromTicks(1);
        Assert.Null(await store.RestorableAsync(DocumentKind.Draft, save.Id));
        await SweepOf(configured.Services).SweepOnceAsync();
        Assert.DoesNotContain(save.BlobName, container.Blobs.Keys);
    }

    [Fact]
    public void Without_a_positive_grace_period_the_settings_are_refused()
    {
        Assert.NotNull(new UndoOptions().Problem());
        Assert.NotNull(new UndoOptions { GracePeriod = TimeSpan.FromDays(1), SweepInterval = TimeSpan.Zero }.Problem());
        Assert.Null(new UndoOptions { GracePeriod = TimeSpan.FromDays(1) }.Problem());
    }

    // The owner's limits (2026-11-03: "set as 90 days, and one hour"): a grace period outside them is
    // refused, its edges allowed. Read from the settings, not fixed in the check.
    [Theory]
    [InlineData("00:59:59", false)]
    [InlineData("01:00:00", true)]
    [InlineData("1.00:00:00", true)]
    [InlineData("90.00:00:00", true)]
    [InlineData("90.00:00:01", false)]
    public void A_grace_period_must_lie_between_an_hour_and_90_days(string period, bool runs) =>
        Assert.Equal(runs, new UndoOptions { GracePeriod = TimeSpan.Parse(period, CultureInfo.InvariantCulture) }.Problem() is null);

    [Fact]
    public void The_limits_are_the_settings_own()
    {
        UndoOptions Narrower(TimeSpan period) =>
            new() { GracePeriod = period, GracePeriodMinimum = TimeSpan.FromHours(2), GracePeriodMaximum = TimeSpan.FromDays(2) };

        Assert.Contains("between Undo:GracePeriodMinimum (02:00:00) and Undo:GracePeriodMaximum (2.00:00:00)",
            Narrower(TimeSpan.FromHours(1)).Problem());
        Assert.NotNull(Narrower(TimeSpan.FromDays(3)).Problem());
        Assert.Null(Narrower(TimeSpan.FromDays(2)).Problem());
    }

    // As for CampaignWatch:Interval: a grace period outside the limits stops the app as it starts.
    [Fact]
    public void An_app_set_with_a_grace_period_outside_the_limits_does_not_start()
    {
        using var configured = app.WithWebHostBuilder(builder => builder.UseSetting("Undo:GracePeriod", "91.00:00:00"));

        var refused = Assert.ThrowsAny<Exception>(() => configured.Services);
        Assert.Contains("Undo:GracePeriod (required) must lie between Undo:GracePeriodMinimum and Undo:GracePeriodMaximum", refused.ToString());
    }

    // Set in appsettings.json and as the code's defaults: the two say the same, so leaving one out of a
    // configuration never moves a limit.
    [Fact]
    public void The_shipped_limits_are_the_code_s_defaults()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))![UndoOptions.Section]!;
        var defaults = new UndoOptions();

        Assert.Equal((TimeSpan.FromHours(1), TimeSpan.FromDays(90)), (defaults.GracePeriodMinimum, defaults.GracePeriodMaximum));
        Assert.Equal(defaults.GracePeriodMinimum, TimeSpan.Parse((string)settings[nameof(UndoOptions.GracePeriodMinimum)]!, CultureInfo.InvariantCulture));
        Assert.Equal(defaults.GracePeriodMaximum, TimeSpan.Parse((string)settings[nameof(UndoOptions.GracePeriodMaximum)]!, CultureInfo.InvariantCulture));
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

        // The bound is the timer's own, as the sweep makes it on the app's clock: what is allowed it
        // takes, what is not it refuses.
        ManualClock clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        new PeriodicTimer(UndoOptions.LongestSweepInterval, clock).Dispose();
        new PeriodicTimer(TimeSpan.FromMilliseconds(1), clock).Dispose();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodicTimer(TimeSpan.FromDays(50), clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodicTimer(TimeSpan.FromTicks(1), clock));
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

        // The service runs off the startup path, so the test waits for its delete; the clock does not
        // move, so the next run, an hour away, never comes.
        await sweep.StartAsync(CancellationToken.None);
        InMemoryBlobBackend container = blobs.For(SalonOne.Name.Value);
        await Eventually.Until(() => container.Deletes >= 1, "the first sweep deleting", container.Deleted);
        await sweep.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(undone.BlobName, container.Blobs.Keys);
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
        var container = containers.For(SalonOne.Name.Value);
        var prototype = new PrototypeCallerSource(SalonOne.Name);

        Assert.Equal(0, await SweepOver(stores, new InMemoryClientDirectory(), clock).SweepOnceAsync());
        Assert.Equal(1, await SweepOver(stores, new InMemoryClientDirectory(), clock, prototype).SweepOnceAsync());
        Assert.DoesNotContain(expired.BlobName, container.Blobs.Keys);
        Assert.Contains(fresh.BlobName, container.Blobs.Keys);

        clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(1, await SweepOver(stores, new UnreadableDirectory(), clock, prototype).SweepOnceAsync());
        Assert.Empty(container.Blobs);
    }

    // A prototype client that also has a row is still swept once a run, not twice.
    [Fact]
    public async Task A_prototype_client_with_a_row_is_swept_once()
    {
        var containers = new InMemoryContainers();
        var opened = new List<string>();
        var clock = new ManualClock(Noon);
        var stores = new ClientStores(name => { opened.Add(name); return containers.For(name); }, clock, TimeSpan.FromDays(1), new InMemoryApprovals(), TestRecords.Unwatched);

        await SweepOver(stores, new InMemoryClientDirectory(SalonOne, SalonTwo), clock, new PrototypeCallerSource(SalonOne.Name))
            .SweepOnceAsync();

        // Each client's own container once; the settings container, where the looks are, once per client.
        Assert.Equal(
            [SalonOne.Name.Value, SalonTwo.Name.Value],
            opened.Where(n => n != ClientStores.SettingsContainer).Order(StringComparer.Ordinal));
        Assert.Equal(2, opened.Count(n => n == ClientStores.SettingsContainer));
    }

    // A look undone on her Look page lives in the settings container, apart from her drafts; the sweep
    // reaches it there too, once its grace period has passed and not a moment before, and says so on
    // her trail by stamp alone.
    [Fact]
    public async Task An_undone_look_is_swept_after_the_grace_period_and_not_before()
    {
        var containers = new InMemoryContainers();
        var clock = new ManualClock(Noon);
        var records = new TestRecords(clock);
        var stores = records.Stores(containers.For, TimeSpan.FromDays(1));
        var kept = await stores.SaveLookAsync(SalonOne.Name, ClientLookTests.Blush, Actor.Demo);
        clock.Now += TimeSpan.FromMinutes(1);
        await stores.SaveLookAsync(SalonOne.Name, new ClientLook(accentColour: "#225e3e"), Actor.Demo);
        var undone = await stores.UndoLastLookSaveAsync(await stores.HoldLookAsync(SalonOne.Name), Actor.Demo);
        var settings = containers.For(ClientStores.SettingsContainer);
        var sweep = SweepOver(stores, new InMemoryClientDirectory(SalonOne), clock);

        clock.Now = undone!.UndoneAt!.Value + TimeSpan.FromDays(1) - TimeSpan.FromTicks(1);
        Assert.Equal(0, await sweep.SweepOnceAsync());
        Assert.Contains(undone.BlobName, settings.Blobs.Keys);

        clock.Now += TimeSpan.FromTicks(1);
        Assert.Equal(1, await sweep.SweepOnceAsync());
        Assert.DoesNotContain(undone.BlobName, settings.Blobs.Keys);
        Assert.Contains(kept.BlobName, settings.Blobs.Keys);
        Assert.Equal(ClientLookTests.Blush, await stores.Look(SalonOne.Name).CurrentAsync());

        var swept = Assert.Single(records.Activity.Events, e => e.Action == ActivityAction.DeletedBySweep);
        Assert.Equal(
            (SalonOne.Name, ActivityEntity.Look, "look", SaveStamp.Of(undone.SavedAt), Actor.UndoSweep.Name),
            (swept.Client, swept.Entity, swept.EntityId, swept.SaveStamp, swept.Actor));
    }

    // A settings container that cannot be reached stops neither the drafts of that client nor the others.
    [Fact]
    public async Task A_look_that_cannot_be_swept_does_not_stop_the_drafts()
    {
        var (containers, clock, _) = Isolated();
        var stores = new ClientStores(
            name => name == ClientStores.SettingsContainer ? new UnreachableContainer() : containers.For(name), clock,
            TimeSpan.FromDays(1), new InMemoryApprovals(), TestRecords.Unwatched);
        var one = stores.Campaigns(SalonOne.Name, Actor.Demo);
        var two = stores.Campaigns(SalonTwo.Name, Actor.Demo);
        var expiredOne = await one.MarkUndoneAsync(await one.SaveDraftAsync(Guid.NewGuid(), "Undone", DraftFixtures.Finished()));
        var expiredTwo = await two.MarkUndoneAsync(await two.SaveDraftAsync(Guid.NewGuid(), "Undone", DraftFixtures.Finished()));
        clock.Now += TimeSpan.FromDays(1);

        Assert.Equal(2, await SweepOver(stores, new InMemoryClientDirectory(SalonOne, SalonTwo), clock).SweepOnceAsync());
        Assert.DoesNotContain(expiredOne.BlobName, containers.For(SalonOne.Name.Value).Blobs.Keys);
        Assert.DoesNotContain(expiredTwo.BlobName, containers.For(SalonTwo.Name.Value).Blobs.Keys);
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

    // The sweep keeps a working copy nobody writes for WorkingCopy:KeepUnchanged, as configured, not for a period of its own.
    [Fact]
    public async Task The_sweep_removes_working_copies_as_long_after_their_last_write_as_it_is_configured_to()
    {
        var (containers, clock, stores) = Isolated();
        var store = stores.Campaigns(SalonOne.Name, Actor.Demo);
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "Saved", DraftFixtures.Finished());
        await store.WorkingCopies.WriteAsync(save.Id, WorkingCopyStoreTests.Edited("Unsaved"), save.SavedAt, BlobExpected.Nothing);
        // And the disk fallback's copy of it, with the same setting.
        // Written a second after the save and the copy in storage, as storage went down: newer than both.
        var disk = new InMemoryWorkingCopyDisk();
        clock.Now += TimeSpan.FromSeconds(1);
        disk.Put(SalonOne.Name, new DiskCopy(save.Id, CampaignJson.SerializeDraft(WorkingCopyStoreTests.Edited("Unsaved")), save.SavedAt, clock.Now));
        clock.Now += TimeSpan.FromDays(2);
        UndoSweep Keeping(TimeSpan keep) => new(stores, new InMemoryClientDirectory(SalonOne), [],
            Options.Create(new UndoOptions { GracePeriod = TimeSpan.FromDays(1) }),
            Options.Create(new WorkingCopyOptions { KeepUnchanged = keep }), disk, clock, NullLogger<UndoSweep>.Instance);

        await Keeping(TimeSpan.FromDays(3)).SweepOnceAsync();
        Assert.NotNull(await store.WorkingCopies.ReadAsync(save.Id));
        Assert.NotNull(disk.Held(SalonOne.Name, save.Id));
        await Keeping(TimeSpan.FromDays(2)).SweepOnceAsync();
        Assert.Null(await store.WorkingCopies.ReadAsync(save.Id));
        Assert.Null(disk.Held(SalonOne.Name, save.Id));
        Assert.Contains(save.BlobName, containers.For(SalonOne.Name.Value).Blobs.Keys);
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
        new(stores, clients, prototype, Options.Create(new UndoOptions { GracePeriod = TimeSpan.FromDays(1) }),
            Options.Create(new WorkingCopyOptions()), new InMemoryWorkingCopyDisk(), clock, NullLogger<UndoSweep>.Instance);

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

        public Task<string?> ReplaceAsync(
            string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata, BlobExpected expected,
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<bool> SetMetadataAsync(
            string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
            throw Unreachable();

        public Task<bool> SetMetadataIfUnchangedAsync(
            string name, IReadOnlyDictionary<string, string> metadata, string etag, CancellationToken cancellationToken = default) =>
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
