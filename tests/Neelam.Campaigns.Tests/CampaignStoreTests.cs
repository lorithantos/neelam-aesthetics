using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class CampaignStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 14, 30, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("6b1f0c1e-9a35-4c2e-8e57-0d3c9c1a2b44");

    private readonly InMemoryBlobBackend _blobs = new();
    private readonly ManualClock _clock = new(Start);
    private CampaignStore Store => new(_blobs, _clock);

    [Fact]
    public async Task Each_save_is_a_new_date_time_stamped_blob()
    {
        var first = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(5);
        var second = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());

        Assert.Equal("drafts/6b1f0c1e9a354c2e8e570d3c9c1a2b44/20261002T143000.0000000Z.json", first.BlobName);
        Assert.Equal("drafts/6b1f0c1e9a354c2e8e570d3c9c1a2b44/20261002T143500.0000000Z.json", second.BlobName);
        Assert.Equal(2, _blobs.Blobs.Count);
    }

    [Fact]
    public async Task History_is_newest_first_and_latest_is_the_newest_save()
    {
        await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        _clock.Now = Start.AddMinutes(5);
        await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());

        var history = await Store.HistoryAsync(DocumentKind.Draft, Id);
        var latest = Assert.Single(await Store.LatestAsync(DocumentKind.Draft));

        Assert.Equal([Start.AddMinutes(5), Start], history.Select(s => s.SavedAt));
        Assert.Equal(history[0], latest);
        Assert.True((await Store.LoadDraftAsync(latest)).Build().Succeeded);
    }

    [Fact]
    public async Task Deleting_the_newest_save_leaves_no_trace_and_the_previous_becomes_latest()
    {
        var older = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        _clock.Now = Start.AddMinutes(5);
        var newer = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());

        Assert.True(await Store.DeleteAsync(newer));

        Assert.Equal(older, Assert.Single(await Store.LatestAsync(DocumentKind.Draft)));
        var remaining = Assert.Single(_blobs.Blobs);
        Assert.DoesNotContain(newer.BlobName, remaining.Value.Blob.Content.ToString());
        Assert.DoesNotContain(remaining.Value.Metadata.Values, v => v.Contains("143500"));
    }

    [Fact]
    public async Task Deleting_every_save_removes_the_campaign_entirely()
    {
        var a = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(1);
        var b = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());

        await Store.DeleteAsync(a);
        await Store.DeleteAsync(b);

        Assert.Empty(await Store.LatestAsync(DocumentKind.Draft));
        Assert.Empty(_blobs.Blobs);
    }

    [Fact]
    public async Task Two_saves_in_the_same_instant_never_overwrite_each_other()
    {
        var first = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        var second = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());

        Assert.NotEqual(first.BlobName, second.BlobName);
        Assert.Equal(1, (second.SavedAt - first.SavedAt).Ticks);
        Assert.False((await Store.LoadDraftAsync(first)).Build().Succeeded);   // still the original
    }

    [Fact]
    public async Task Title_with_emoji_survives_blob_metadata()
    {
        await Store.SaveDraftAsync(Id, "WE’RE TURNING ONE! 🥂✨", DraftFixtures.Finished());

        var save = Assert.Single(await Store.ListAsync(DocumentKind.Draft));

        Assert.Equal("WE’RE TURNING ONE! 🥂✨", save.Title);
        Assert.All(_blobs.Blobs.Single().Value.Metadata.Values, v => Assert.True(v.All(char.IsAscii)));
    }

    [Fact]
    public async Task Templates_and_drafts_are_listed_separately()
    {
        await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        var t = await Store.SaveTemplateAsync(Guid.NewGuid(), DraftFixtures.Membership);

        Assert.Equal(t, Assert.Single(await Store.ListAsync(DocumentKind.Template)));
        Assert.Equal(DraftFixtures.Membership.Name, (await Store.LoadTemplateAsync(t)).Name);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.LoadDraftAsync(t));
    }

    [Fact]
    public async Task Blobs_outside_the_naming_scheme_are_ignored()
    {
        _blobs.Put("drafts/readme.txt", "");
        _blobs.Put("drafts/not-a-guid/20261002T143000.0000000Z.json", "");

        Assert.Empty(await Store.ListAsync(DocumentKind.Draft));
    }
}
