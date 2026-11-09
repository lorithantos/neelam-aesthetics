using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The campaigns list's "Not saved yet" (owner, 2026-11-03): a row for each campaign she typed into and never
/// saved, from its working copy's name and metadata alone, with Open and Discard. Requested through the
/// enforcing app, as the rest of the list is.
/// </summary>
public class NotSavedYetListTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord Salon = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private static readonly LocalTime Times = LocalTime.For(LocalTime.DefaultZone);

    private const string Heading = "<h2 id=\"not-saved-yet\">Not saved yet</h2>";

    private CampaignStore Store => app.Stores.Campaigns(Salon.Name, Actor.Demo);

    private InMemoryBlobBackend Container => app.Containers.For(Salon.Name.Value);

    private async Task<HttpClient> SignedIn()
    {
        if ((await app.Clients.ListAsync()).Count == 0) await app.Clients.AddAsync(Salon);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.SignedIn([Features.Campaigns], [Salon.GroupId]);
        return client;
    }

    private static async Task<string> ListAsync(HttpClient client) =>
        WebUtility.HtmlDecode(await (await client.GetAsync("/campaigns")).Content.ReadAsStringAsync());

    private static string Card(string list, string title) =>
        Regex.Match(list, $"<section class=\"card\" aria-label=\"Not saved yet: {Regex.Escape(title)}\">.*?</section>", RegexOptions.Singleline).Value;

    private async Task<WorkingCopy> NeverSaved(Guid id, string title)
    {
        await Store.WorkingCopies.WriteAsync(id, WorkingCopyStoreTests.Edited(title), null, BlobExpected.Nothing, title);
        return (await Store.WorkingCopies.ReadAsync(id))!;
    }

    [Fact]
    public async Task A_campaign_typed_into_and_never_saved_is_listed_with_open_and_discard_and_nothing_of_it_is_read()
    {
        var client = await SignedIn();
        var id = Guid.NewGuid();
        var copy = await NeverSaved(id, "Spring offer, never saved");
        // A saved campaign's working copy is that campaign's, listed with it, not here.
        var save = await Store.SaveDraftAsync(Guid.NewGuid(), "Saved, with changes", DraftFixtures.Finished());
        await Store.WorkingCopies.WriteAsync(save.Id, WorkingCopyStoreTests.Edited("Changes"), save.SavedAt, BlobExpected.Nothing, "Changes");
        var reads = Container.Reads.ToList();

        var list = await ListAsync(client);

        Assert.Contains(Heading, list);
        var card = Card(list, "Spring offer, never saved");
        Assert.Contains(WorkingCopyWords.NotSavedYet("Spring offer, never saved", Times.DateAndTime(copy.WrittenAt)), card);
        Assert.Contains($"<a class=\"button\" href=\"campaigns/{id}\">Open</a>", card);
        Assert.Contains("<button type=\"submit\" class=\"button\">Discard</button>", card);
        Assert.Equal("", Card(list, "Changes"));
        Assert.DoesNotContain(Container.Reads.Skip(reads.Count), name => name.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal));
    }

    private DiskCopy OnDisk(Guid id, string title, DateTimeOffset at) =>
        new(id, CampaignJson.SerializeDraft(WorkingCopyStoreTests.Edited(title)), null, at, title);

    // Storage was down for the whole of her first session on it: the copy reached only the disk, and is listed too.
    [Fact]
    public async Task A_never_saved_campaign_only_on_the_disk_is_listed_from_its_name_and_title_alone()
    {
        var client = await SignedIn();
        // The app's own sweep lists the disk as the app starts, and the disk counts that listing as a
        // content read: stopped, its pass has ended before the count is taken, so the count is the page's.
        await app.SweepStoppedAsync();
        var id = Guid.NewGuid();
        app.Disk.Put(Salon.Name, OnDisk(id, "Only on the disk", app.Clock.Now));
        var contentReads = app.Disk.ContentReads;

        var card = Card(await ListAsync(client), "Only on the disk");

        Assert.Contains(WorkingCopyWords.NotSavedYet("Only on the disk", Times.DateAndTime(app.Clock.Now)), card);
        Assert.Contains($"<a class=\"button\" href=\"campaigns/{id}\">Open</a>", card);
        Assert.Contains("<button type=\"submit\" class=\"button\">Discard</button>", card);
        Assert.Equal(contentReads, app.Disk.ContentReads);
    }

    // Why the test above stops the sweep. Here the app's first pass is caught at its listing of the disk
    // and let go only once the count has been taken: the moment at which that test, before it stopped
    // the sweep, failed one run in many. Stopped first, the pass has ended by then, and the count holds.
    [Fact]
    public async Task The_apps_own_sweep_once_stopped_reads_nothing_into_a_count_taken_after()
    {
        using var own = new EnforcedApp();
        await own.Clients.AddAsync(Salon);
        var held = new HeldDisk(own.Disk);
        using var hosted = own.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.RemoveAll<IWorkingCopyDisk>().AddSingleton<IWorkingCopyDisk>(held)));
        var client = hosted.CreateClient(new() { AllowAutoRedirect = false });
        client.SignedIn([Features.Campaigns], [Salon.GroupId]);
        await Eventually.Until(() => held.Arrived > 0, "the app's sweep listing the disk as it starts", held.Changed);

        await hosted.SweepStoppedAsync();
        var id = Guid.NewGuid();
        own.Disk.Put(Salon.Name, OnDisk(id, "Only on the disk", own.Clock.Now));
        var contentReads = own.Disk.ContentReads;

        held.Release();
        await Eventually.Until(() => held.Listed > 0, "the sweep's listing of the disk going through", held.Changed);
        var card = Card(await ListAsync(client), "Only on the disk");

        Assert.Contains($"<a class=\"button\" href=\"campaigns/{id}\">Open</a>", card);
        Assert.Equal(contentReads, own.Disk.ContentReads);
    }

    /// <summary>
    /// The disk, with each listing of its copies held until the test lets go, or until the lister is told to
    /// stop; then it lists, and the disk counts it.
    /// </summary>
    private sealed class HeldDisk(InMemoryWorkingCopyDisk inner) : IWorkingCopyDisk
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _listed;

        /// <summary>Raised as a listing arrives, and again as it goes through.</summary>
        public Pulse Changed { get; } = new();

        /// <summary>How many listings have arrived.</summary>
        public int Arrived => Volatile.Read(ref _arrived);

        /// <summary>How many have gone through to the disk.</summary>
        public int Listed => Volatile.Read(ref _listed);

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<DiskCopy>> ListAsync(ClientName client, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _arrived);
            Changed.Raise();
            try
            {
                await _release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Told to stop. The disk lists whether or not its caller was, so this does too.
            }
            var listed = await inner.ListAsync(client, CancellationToken.None);
            Interlocked.Increment(ref _listed);
            Changed.Raise();
            return listed;
        }

        public Task WriteAsync(ClientName client, DiskCopy copy, CancellationToken ct = default) => inner.WriteAsync(client, copy, ct);

        public Task<DiskCopy?> ReadAsync(ClientName client, Guid campaignId, CancellationToken ct = default) => inner.ReadAsync(client, campaignId, ct);

        public Task<bool> RemoveAsync(ClientName client, Guid campaignId, CancellationToken ct = default) => inner.RemoveAsync(client, campaignId, ct);

        public Task<IReadOnlyList<NotSavedYet>> ListNotSavedYetAsync(ClientName client, CancellationToken ct = default) => inner.ListNotSavedYetAsync(client, ct);
    }

    // In storage and on the disk both: one row, at the newer of the two times.
    [Fact]
    public async Task A_never_saved_campaign_in_storage_and_on_the_disk_is_one_row()
    {
        var client = await SignedIn();
        var id = Guid.NewGuid();
        var copy = await NeverSaved(id, "In both places");
        var later = copy.WrittenAt + TimeSpan.FromMinutes(5);
        app.Disk.Put(Salon.Name, OnDisk(id, "In both places", later));

        var list = await ListAsync(client);

        Assert.Single(Regex.Matches(list, Regex.Escape("aria-label=\"Not saved yet: In both places\"")));
        Assert.Contains(WorkingCopyWords.NotSavedYet("In both places", Times.DateAndTime(later)), Card(list, "In both places"));
    }

    [Fact]
    public async Task Discard_on_the_list_removes_one_only_on_the_disk()
    {
        var client = await SignedIn();
        var id = Guid.NewGuid();
        app.Disk.Put(Salon.Name, OnDisk(id, "Only on the disk, discarded", app.Clock.Now));
        var card = Card(await ListAsync(client), "Only on the disk, discarded");
        var token = Regex.Match(card, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

        await client.PostAsync("/campaigns", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = $"discard-{id:N}",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Null(app.Disk.Held(Salon.Name, id));
        Assert.Equal("", Card(await ListAsync(client), "Only on the disk, discarded"));
    }

    [Fact]
    public async Task Discard_on_the_list_removes_it_from_storage_and_the_disk()
    {
        var client = await SignedIn();
        var id = Guid.NewGuid();
        await NeverSaved(id, "To be discarded");
        app.Disk.Put(Salon.Name, new DiskCopy(id, CampaignJson.SerializeDraft(WorkingCopyStoreTests.Edited("To be discarded")), null, app.Clock.Now));
        var card = Card(await ListAsync(client), "To be discarded");
        var token = Regex.Match(card, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync("/campaigns", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = $"discard-{id:N}",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.True(response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400, $"Discard answered {response.StatusCode}.");
        Assert.Null(await Store.WorkingCopies.ReadAsync(id));
        Assert.Null(app.Disk.Held(Salon.Name, id));
        Assert.Equal("", Card(await ListAsync(client), "To be discarded"));
    }
}
