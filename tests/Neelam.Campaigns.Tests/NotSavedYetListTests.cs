using System.Net;
using System.Text.RegularExpressions;
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
        var id = Guid.NewGuid();
        app.Disk.Put(Salon.Name, OnDisk(id, "Only on the disk", app.Clock.Now));
        var contentReads = app.Disk.ContentReads;

        var card = Card(await ListAsync(client), "Only on the disk");

        Assert.Contains(WorkingCopyWords.NotSavedYet("Only on the disk", Times.DateAndTime(app.Clock.Now)), card);
        Assert.Contains($"<a class=\"button\" href=\"campaigns/{id}\">Open</a>", card);
        Assert.Contains("<button type=\"submit\" class=\"button\">Discard</button>", card);
        Assert.Equal(contentReads, app.Disk.ContentReads);
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
