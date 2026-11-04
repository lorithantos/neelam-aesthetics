using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The server-disk fallback for working copies (owner, 2026-11-03): where the form's content goes when storage
/// cannot be reached, named by ids alone, and swept once storage holds it, once nobody has written it for a
/// week, or once its campaign is gone. The real disk is exercised once, in a folder of the test's own.
/// </summary>
public class WorkingCopyDiskTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly ClientName Client = new("test-salon-one");

    private readonly InMemoryBlobBackend _blobs = new();
    private readonly InMemoryWorkingCopyDisk _disk = new();
    private readonly ManualClock _clock = new(Start);
    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_blobs, TimeSpan.FromDays(1), Client);

    private static DiskCopy Copy(Guid id, string subject, DateTimeOffset at, DateTimeOffset? basedOn = null) =>
        new(id, CampaignJson.SerializeDraft(WorkingCopyStoreTests.Edited(subject)), basedOn, at);

    private async Task<SaveRef> Saved(Guid? id = null)
    {
        var save = await Store.SaveDraftAsync(id ?? Guid.NewGuid(), "Saved", DraftFixtures.Finished());
        _clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    // The real one, in a folder of this test's own: the file is named by the client and the campaign alone.
    [Fact]
    public async Task On_the_real_disk_a_copy_is_named_by_ids_alone_and_read_listed_and_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "neelam-working-copy-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var disk = new DiskWorkingCopies(root);
            var id = Guid.NewGuid();
            var copy = Copy(id, "Typed while storage was down", Start, Start.AddMinutes(-5));

            await disk.WriteAsync(Client, copy);

            var file = Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            Assert.Equal(Path.Combine(root, Client.Value, $"{id:N}.json"), file);
            var read = await disk.ReadAsync(Client, id);
            Assert.Equal(copy, read);
            Assert.Equal("Typed while storage was down", read!.Draft.SubjectText());
            Assert.Equal(copy, Assert.Single(await disk.ListAsync(Client)));
            Assert.Empty(await disk.ListAsync(new ClientName("test-salon-two")));

            await disk.WriteAsync(Client, copy with { Json = CampaignJson.SerializeDraft(WorkingCopyStoreTests.Edited("Later")) });
            Assert.Equal("Later", (await disk.ReadAsync(Client, id))!.Draft.SubjectText());
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));

            Assert.True(await disk.RemoveAsync(Client, id));
            Assert.Null(await disk.ReadAsync(Client, id));
            Assert.False(await disk.RemoveAsync(Client, id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_sweep_removes_a_disk_copy_storage_already_holds_something_newer_of()
    {
        var saved = await Saved();
        var copied = await Saved();
        _disk.Put(Client, Copy(saved.Id, "Before the save", saved.SavedAt - TimeSpan.FromSeconds(1), saved.SavedAt));
        _disk.Put(Client, Copy(copied.Id, "Before the copy", _clock.Now, copied.SavedAt));
        _clock.Now += TimeSpan.FromSeconds(1);
        await Store.WorkingCopies.WriteAsync(copied.Id, WorkingCopyStoreTests.Edited("Moved"), copied.SavedAt, BlobExpected.Anything);
        var fresh = await Saved();
        _clock.Now += TimeSpan.FromSeconds(1);
        _disk.Put(Client, Copy(fresh.Id, "Newer than anything stored", _clock.Now, fresh.SavedAt));

        await Store.SweepAsync(TimeSpan.FromDays(7), _disk);

        Assert.Null(_disk.Held(Client, saved.Id));
        Assert.Null(_disk.Held(Client, copied.Id));
        Assert.NotNull(_disk.Held(Client, fresh.Id));
    }

    [Fact]
    public async Task The_sweep_removes_a_disk_copy_nobody_has_written_for_a_week()
    {
        var save = await Saved();
        _disk.Put(Client, Copy(save.Id, "Left", _clock.Now, save.SavedAt));

        _clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromTicks(1);
        await Store.SweepAsync(TimeSpan.FromDays(7), _disk);
        Assert.NotNull(_disk.Held(Client, save.Id));

        _clock.Now += TimeSpan.FromTicks(1);
        await Store.SweepAsync(TimeSpan.FromDays(7), _disk);
        Assert.Null(_disk.Held(Client, save.Id));
    }

    [Fact]
    public async Task The_sweep_removes_a_disk_copy_whose_campaign_is_gone()
    {
        var gone = await Saved();
        _clock.Now += TimeSpan.FromSeconds(1);
        _disk.Put(Client, Copy(gone.Id, "Of a campaign swept away", _clock.Now, gone.SavedAt));
        await Store.MarkUndoneAsync(gone);
        _clock.Now += TimeSpan.FromDays(1);

        await Store.SweepAsync(TimeSpan.FromDays(7), _disk);

        Assert.Empty(_blobs.Blobs.Keys.Where(k => k.Contains($"{gone.Id:N}", StringComparison.Ordinal)));
        Assert.Null(_disk.Held(Client, gone.Id));
    }
}
