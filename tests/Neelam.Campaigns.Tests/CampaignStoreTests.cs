using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class CampaignStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 14, 30, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("6b1f0c1e-9a35-4c2e-8e57-0d3c9c1a2b44");

    private readonly InMemoryBlobBackend _blobs = new();
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private readonly ManualClock _clock = new(Start);
    private CampaignStore Store => new(_blobs, _clock, Grace);

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

    // To its client an undone save is gone at once: no list, history or latest shows it, though
    // its blob waits, marked, for the sweep.
    [Fact]
    public async Task An_undone_save_drops_out_of_every_list_and_the_previous_becomes_latest()
    {
        var older = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        _clock.Now = Start.AddMinutes(5);
        var newer = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());

        var undone = await Store.MarkUndoneAsync(newer);

        Assert.Equal(Start.AddMinutes(5), undone.UndoneAt);
        Assert.Equal(older, Assert.Single(await Store.LatestAsync(DocumentKind.Draft)));
        Assert.Equal(older, Assert.Single(await Store.ListAsync(DocumentKind.Draft)));
        Assert.Equal(older, Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, Id)));
        // Marked in its own metadata, with nothing else written anywhere.
        Assert.Equal(2, _blobs.Blobs.Count);
        Assert.Equal("2026-10-02T14:35:00.0000000+00:00", _blobs.Blobs[newer.BlobName].Metadata["undone"]);
    }

    // The sweep deletes only what has been undone for the whole grace period, and then the save
    // leaves no trace: not its blob, and nothing about it in the save that remains.
    [Fact]
    public async Task The_sweep_deletes_only_saves_undone_longer_ago_than_the_grace_period_and_leaves_no_trace()
    {
        var kept = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        _clock.Now = Start.AddMinutes(5);
        var early = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        var template = await Store.SaveTemplateAsync(Guid.NewGuid(), DraftFixtures.Membership);
        await Store.MarkUndoneAsync(early);
        await Store.MarkUndoneAsync(template);
        _clock.Now = Start.AddHours(1);
        var late = await Store.SaveDraftAsync(Guid.NewGuid(), "Another", DraftFixtures.Finished());
        await Store.MarkUndoneAsync(late);

        // One tick short of the period: nothing goes.
        _clock.Now = Start.AddMinutes(5) + Grace - TimeSpan.FromTicks(1);
        Assert.Equal(0, await Store.SweepAsync());
        Assert.Equal(4, _blobs.Blobs.Count);

        // The period over for the two undone at 14:35, not for the one undone at 15:30.
        _clock.Now = Start.AddMinutes(5) + Grace;
        Assert.Equal(2, await Store.SweepAsync());
        Assert.Equal(new[] { kept.BlobName, late.BlobName }.Order(StringComparer.Ordinal).ToArray(),
            _blobs.Blobs.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(_blobs.Blobs.Values, b => Assert.DoesNotContain(b.Metadata.Values, v => v.Contains("143500")));

        // Running again, as after a crash part-way, only finishes the job.
        _clock.Now = Start.AddHours(1) + Grace;
        Assert.Equal(1, await Store.SweepAsync());
        Assert.Equal(0, await Store.SweepAsync());
        Assert.Equal(kept, Assert.Single(await Store.LatestAsync(DocumentKind.Draft)));
        Assert.Equal(kept.BlobName, Assert.Single(_blobs.Blobs).Key);
    }

    // A save nobody undid is never the sweep's, however old it is.
    [Fact]
    public async Task The_sweep_never_deletes_a_save_that_was_not_undone()
    {
        var save = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        var template = await Store.SaveTemplateAsync(Guid.NewGuid(), DraftFixtures.Membership);
        _blobs.Put("drafts/readme.txt", "", new Dictionary<string, string> { ["undone"] = "2020-01-01T00:00:00.0000000+00:00" });
        _blobs.Put($"drafts/{Id:N}/20261002T150000.0000000Z.json", "{}", new Dictionary<string, string> { ["undone"] = "not a time" });

        _clock.Now = Start.AddYears(5);

        Assert.Equal(0, await Store.SweepAsync());
        Assert.Equal(4, _blobs.Blobs.Count);
        Assert.Contains(save.BlobName, _blobs.Blobs.Keys);
        Assert.Contains(template.BlobName, _blobs.Blobs.Keys);
    }

    // Restore read the mark a moment before the period ended, and its write lands after the sweep
    // listed the save as expired: the sweep's delete finds the blob changed and leaves it.
    [Fact]
    public async Task A_save_restored_between_the_sweeps_listing_and_its_delete_survives()
    {
        var save = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        var undone = await Store.MarkUndoneAsync(save);
        _clock.Now = Start + Grace;
        _blobs.BeforeDelete = async _ =>
        {
            _blobs.BeforeDelete = null;
            _clock.Now = Start + Grace - TimeSpan.FromTicks(1);
            Assert.True(await Store.RestoreAsync(undone));
            _clock.Now = Start + Grace;
        };

        Assert.Equal(0, await Store.SweepAsync());

        Assert.Contains(save.BlobName, _blobs.Blobs.Keys);
        Assert.Equal(save, Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, Id)));
        Assert.Equal(0, await Store.SweepAsync());
        Assert.Contains(save.BlobName, _blobs.Blobs.Keys);
    }

    // Several undos in a row take the saves back newest first, so Restore brings them back in the
    // reverse order: always the one undone last, the oldest of the undone saves above the newest in
    // use, which is the next one back in history. Restoring the newest first would put it back
    // over a save still undone.
    [Fact]
    public async Task After_several_undos_restore_offers_the_one_undone_last()
    {
        var first = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        _clock.Now = Start.AddMinutes(5);
        var second = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(10);
        var third = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        _clock.Now = Start.AddMinutes(15);
        var thirdUndone = await Store.MarkUndoneAsync(third);
        _clock.Now = Start.AddMinutes(20);
        var secondUndone = await Store.MarkUndoneAsync(second);

        Assert.Equal(secondUndone, await Store.RestorableAsync(DocumentKind.Draft, Id));
        Assert.True(await Store.RestoreAsync(secondUndone));
        Assert.Equal([second, first], await Store.HistoryAsync(DocumentKind.Draft, Id));

        Assert.Equal(thirdUndone, await Store.RestorableAsync(DocumentKind.Draft, Id));
        Assert.True(await Store.RestoreAsync(thirdUndone));
        Assert.Equal([third, second, first], await Store.HistoryAsync(DocumentKind.Draft, Id));
        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, Id));
    }

    [Fact]
    public async Task Restoring_within_the_grace_period_puts_the_save_back_in_use()
    {
        var older = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        _clock.Now = Start.AddMinutes(5);
        var newer = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        var undone = await Store.MarkUndoneAsync(newer);

        Assert.Equal(undone, await Store.RestorableAsync(DocumentKind.Draft, Id));
        Assert.Equal(Start.AddMinutes(5) + Grace, Store.RestorableUntil(undone));
        _clock.Now = Start.AddMinutes(5) + Grace - TimeSpan.FromTicks(1);
        Assert.True(await Store.RestoreAsync(undone));

        Assert.Equal(newer, (await Store.HistoryAsync(DocumentKind.Draft, Id))[0]);
        Assert.DoesNotContain("undone", _blobs.Blobs[newer.BlobName].Metadata.Keys);
        Assert.Equal("Year one", Uri.UnescapeDataString(_blobs.Blobs[newer.BlobName].Metadata["title"]));
        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, Id));
        _clock.Now = Start.AddYears(1);
        Assert.Equal(0, await Store.SweepAsync());
        Assert.Equal([newer, older], await Store.HistoryAsync(DocumentKind.Draft, Id));
    }

    [Fact]
    public async Task Once_the_grace_period_has_passed_restore_is_neither_offered_nor_done()
    {
        var newer = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());
        var undone = await Store.MarkUndoneAsync(newer);

        _clock.Now = Start + Grace;

        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, Id));
        Assert.False(await Store.RestoreAsync(undone));
        Assert.Empty(await Store.HistoryAsync(DocumentKind.Draft, Id));
    }

    // Undone, then saved over: the undone save is below one in use, so it is not offered back.
    [Fact]
    public async Task An_undone_save_that_was_saved_over_is_not_offered_back()
    {
        var older = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.SecondSendReplayed());
        _clock.Now = Start.AddMinutes(5);
        await Store.MarkUndoneAsync(await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished()));
        _clock.Now = Start.AddMinutes(10);
        var replacement = await Store.SaveDraftAsync(Id, "Year one", DraftFixtures.Finished());

        Assert.Null(await Store.RestorableAsync(DocumentKind.Draft, Id));
        Assert.Equal([replacement, older], await Store.HistoryAsync(DocumentKind.Draft, Id));
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
