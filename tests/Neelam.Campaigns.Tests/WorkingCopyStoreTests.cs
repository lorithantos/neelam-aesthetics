using System.Globalization;
using System.Text.Json.Nodes;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The working copy in storage (owner, 2026-11-03): a recovery copy of unsaved edits, in the client's container
/// under its own prefix, overwritten in place, never a save. Nothing that lists the saves sees it, it is never
/// on the activity trail, and the sweep removes one nobody has written for a week, or whose campaign has no
/// saves left, each only while it is as listed. In memory, on the test's clock.
/// </summary>
public class WorkingCopyStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private readonly InMemoryBlobBackend _blobs = new();
    private readonly ManualClock _clock = new(Start);
    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_blobs, Grace);
    private WorkingCopies Copies => Store.WorkingCopies;

    internal static CampaignDraft Edited(string subject)
    {
        var editor = CampaignEditor.Open(DraftFixtures.Finished());
        editor.Subject.Text = subject;
        return editor.Draft;
    }

    private async Task<SaveRef> Saved(Guid? id = null)
    {
        var save = await Store.SaveDraftAsync(id ?? Guid.NewGuid(), "Saved", DraftFixtures.Finished());
        _clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    [Fact]
    public async Task A_working_copy_holds_the_draft_the_save_it_started_from_and_when_it_was_written()
    {
        var save = await Saved();
        var written = await Copies.WriteAsync(save.Id, Edited("Unsaved"), save.SavedAt, BlobExpected.Nothing);

        var copy = await Copies.ReadAsync(save.Id);

        Assert.NotNull(copy);
        Assert.Equal("Unsaved", copy.Draft.SubjectText());
        Assert.Equal(CampaignJson.SerializeDraft(Edited("Unsaved")), copy.Json);
        Assert.Equal(save.SavedAt, copy.BasedOn);
        Assert.Equal(_clock.Now, copy.WrittenAt);
        Assert.Equal(written, copy.ETag);
        Assert.Equal($"working/{save.Id:N}.json", Assert.Single(_blobs.Blobs.Keys, k => !k.StartsWith("drafts/", StringComparison.Ordinal)));
    }

    // Outside drafts/, templates/ and proofread/: no listing of a save, by its prefix, ever reaches it.
    [Fact]
    public async Task Nothing_that_lists_the_saves_sees_a_working_copy()
    {
        var save = await Saved();
        var deleted = await Saved();
        await Store.DeleteAsync(DocumentKind.Draft, deleted.Id);
        await Copies.WriteAsync(save.Id, Edited("Unsaved"), save.SavedAt, BlobExpected.Nothing);
        await Copies.WriteAsync(deleted.Id, Edited("Unsaved too"), deleted.SavedAt, BlobExpected.Nothing);

        Assert.All(_blobs.Blobs.Keys.Where(k => k.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal)), name =>
            Assert.DoesNotContain(new[] { "drafts/", "templates/", "proofread/" }, prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.Equal(save.BlobName, Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id)).BlobName);
        Assert.Equal(save.BlobName, (await Store.NewestAsync(DocumentKind.Draft, save.Id))!.BlobName);
        Assert.Equal(save.BlobName, Assert.Single(await DraftSession.ListLabelledAsync(Store)).Save.BlobName);
        Assert.Equal(deleted.Id, Assert.Single(await Store.RecentlyDeletedAsync(DocumentKind.Draft)).Id);
        Assert.Single(await Store.ListAsync(DocumentKind.Draft));
        Assert.Empty(await Store.ListAsync(DocumentKind.Template));
        Assert.Equal(0, await Store.SweepAsync());
    }

    [Fact]
    public async Task It_is_overwritten_in_place_while_it_is_the_version_the_page_last_wrote()
    {
        var save = await Saved();
        var first = await Copies.WriteAsync(save.Id, Edited("One"), save.SavedAt, BlobExpected.Nothing);
        var second = await Copies.WriteAsync(save.Id, Edited("Two"), save.SavedAt, BlobExpected.Version(first!));

        Assert.NotNull(second);
        Assert.Equal("Two", (await Copies.ReadAsync(save.Id))!.Draft.SubjectText());
        Assert.Single(_blobs.Blobs.Keys, k => k.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal));

        // Another tab wrote since: refused, and nothing written. Where one was not expected, the same.
        Assert.Null(await Copies.WriteAsync(save.Id, Edited("Stale"), save.SavedAt, BlobExpected.Version(first!)));
        Assert.Null(await Copies.WriteAsync(save.Id, Edited("Stale"), save.SavedAt, BlobExpected.Nothing));
        Assert.Equal("Two", (await Copies.ReadAsync(save.Id))!.Draft.SubjectText());
        // The last writer wins when it writes whatever is there.
        Assert.NotNull(await Copies.WriteAsync(save.Id, Edited("Mine"), save.SavedAt, BlobExpected.Anything));
        Assert.Equal("Mine", (await Copies.ReadAsync(save.Id))!.Draft.SubjectText());
    }

    [Fact]
    public async Task Writing_and_removing_it_leave_nothing_on_the_activity_trail()
    {
        var save = await Saved();
        var before = Records.Activity.Events.Count;

        await Copies.WriteAsync(save.Id, Edited("One"), save.SavedAt, BlobExpected.Nothing);
        await Copies.WriteAsync(save.Id, Edited("Two"), save.SavedAt, BlobExpected.Anything);
        Assert.True(await Copies.RemoveAsync(save.Id));

        Assert.Equal(before, Records.Activity.Events.Count);
        Assert.Null(await Copies.ReadAsync(save.Id));
        Assert.DoesNotContain(_blobs.Blobs.Keys, k => k.StartsWith(WorkingCopies.Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_sweep_removes_one_nobody_has_written_for_a_week_and_keeps_one_written_since()
    {
        var save = await Saved();
        var fresh = await Saved();
        await Copies.WriteAsync(save.Id, Edited("Left"), save.SavedAt, BlobExpected.Nothing);
        var events = Records.Activity.Events.Count;

        _clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromTicks(1);
        await Copies.WriteAsync(fresh.Id, Edited("Still at work"), fresh.SavedAt, BlobExpected.Nothing);
        Assert.Equal(0, await Store.SweepAsync());
        Assert.NotNull(await Copies.ReadAsync(save.Id));

        _clock.Now += TimeSpan.FromTicks(1);
        Assert.Equal(0, await Store.SweepAsync());
        Assert.Null(await Copies.ReadAsync(save.Id));
        Assert.NotNull(await Copies.ReadAsync(fresh.Id));
        // The saves stay, and nothing is recorded.
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id));
        Assert.Equal(events, Records.Activity.Events.Count);
    }

    [Fact]
    public async Task The_sweep_takes_how_long_to_keep_one_as_it_is_given()
    {
        var save = await Saved();
        await Copies.WriteAsync(save.Id, Edited("Left"), save.SavedAt, BlobExpected.Nothing);
        _clock.Now += TimeSpan.FromDays(2);

        await Store.SweepAsync(TimeSpan.FromDays(3));
        Assert.NotNull(await Copies.ReadAsync(save.Id));
        await Store.SweepAsync(TimeSpan.FromDays(2));
        Assert.Null(await Copies.ReadAsync(save.Id));
    }

    [Fact]
    public async Task The_sweep_removes_one_whose_campaign_has_no_saves_left()
    {
        var kept = await Saved();
        var gone = await Saved();
        await Copies.WriteAsync(kept.Id, Edited("Kept"), kept.SavedAt, BlobExpected.Nothing);
        await Copies.WriteAsync(gone.Id, Edited("Gone"), gone.SavedAt, BlobExpected.Nothing);
        await Store.MarkUndoneAsync(gone);

        // Undone, its save is still there to restore: the working copy stays with it.
        Assert.Equal(0, await Store.SweepAsync());
        Assert.NotNull(await Copies.ReadAsync(gone.Id));

        // Swept for good, the campaign has no saves left: its working copy goes in the same run.
        _clock.Now += Grace;
        Assert.Equal(1, await Store.SweepAsync());
        Assert.Null(await Copies.ReadAsync(gone.Id));
        Assert.NotNull(await Copies.ReadAsync(kept.Id));
    }

    // Started on a campaign never saved (no save it started from): it has no saves by nature, so only the
    // week unwritten removes it. One started from a save still goes when its saves are gone (above).
    [Fact]
    public async Task The_sweep_keeps_a_never_saved_campaign_s_copy_until_a_week_unwritten()
    {
        var id = Guid.NewGuid();
        await Copies.WriteAsync(id, Edited("Never saved"), null, BlobExpected.Nothing, "Never saved");

        _clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromTicks(1);
        Assert.Equal(0, await Store.SweepAsync());
        Assert.NotNull(await Copies.ReadAsync(id));

        _clock.Now += TimeSpan.FromTicks(1);
        await Store.SweepAsync();
        Assert.Null(await Copies.ReadAsync(id));
    }

    [Fact]
    public async Task A_never_saved_campaign_s_copy_is_listed_by_its_title_and_time_without_reading_it()
    {
        var neverSaved = Guid.NewGuid();
        var save = await Saved();
        await Copies.WriteAsync(neverSaved, Edited("Spring offer"), null, BlobExpected.Nothing, "Spring offer");
        var written = _clock.Now;
        await Copies.WriteAsync(save.Id, Edited("Of a saved one"), save.SavedAt, BlobExpected.Nothing, "Of a saved one");
        var reads = _blobs.Reads.Count;

        var listed = Assert.Single(await Copies.NotSavedYetAsync());

        Assert.Equal((neverSaved, "Spring offer", written), (listed.CampaignId, listed.Title, listed.WrittenAt));
        Assert.Equal(reads, _blobs.Reads.Count);
    }

    // Removed only while it is as listed: one written again between the listing and the delete is a page at work.
    [Fact]
    public async Task The_sweep_leaves_one_written_again_after_it_was_listed()
    {
        var save = await Saved();
        await Copies.WriteAsync(save.Id, Edited("Left"), save.SavedAt, BlobExpected.Nothing);
        _clock.Now += TimeSpan.FromDays(8);
        _blobs.BeforeDelete = async name =>
        {
            _blobs.BeforeDelete = null;
            if (name == WorkingCopies.Name(save.Id))
                await Copies.WriteAsync(save.Id, Edited("Back at it"), save.SavedAt, BlobExpected.Anything);
        };

        await Store.SweepAsync();

        Assert.Equal("Back at it", (await Copies.ReadAsync(save.Id))!.Draft.SubjectText());
    }

    [Fact]
    public async Task One_that_is_not_a_draft_is_refused_as_unreadable()
    {
        var save = await Saved();
        _blobs.Put(WorkingCopies.Name(save.Id), "{ not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => Copies.ReadAsync(save.Id));
    }
}

internal static class DraftText
{
    /// <summary>The subject line as the editor shows it.</summary>
    public static string SubjectText(this CampaignDraft draft) => CampaignEditor.Open(draft).Subject.Text;
}

/// <summary>The working copy's settings: shipped as the code's defaults, and refused when they cannot run.</summary>
public class WorkingCopyOptionsTests
{
    // Set in appsettings.json and as the code's defaults: the two say the same, so leaving a setting out of a
    // configuration never changes when the working copy is written or swept.
    [Fact]
    public void The_shipped_settings_are_the_code_s_defaults()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))![WorkingCopyOptions.Section]!;
        var defaults = new WorkingCopyOptions();

        Assert.Equal((TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), TimeSpan.FromDays(7)),
            (defaults.Debounce, defaults.Cap, defaults.KeepUnchanged));
        Assert.Equal(defaults.Debounce, TimeSpan.Parse((string)settings[nameof(WorkingCopyOptions.Debounce)]!, CultureInfo.InvariantCulture));
        Assert.Equal(defaults.Cap, TimeSpan.Parse((string)settings[nameof(WorkingCopyOptions.Cap)]!, CultureInfo.InvariantCulture));
        Assert.Equal(defaults.KeepUnchanged, TimeSpan.Parse((string)settings[nameof(WorkingCopyOptions.KeepUnchanged)]!, CultureInfo.InvariantCulture));
    }

    // On App Service, under /home, which persists across restarts, and outside /home/site/wwwroot, which a deploy replaces.
    [Fact]
    public void The_disk_fallback_s_path_is_shipped_as_the_code_s_default_under_home_and_outside_the_site()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))![WorkingCopyOptions.Section]!;
        var defaults = new WorkingCopyOptions();

        Assert.Equal("/home/data/working", defaults.DiskPath);
        Assert.Equal(defaults.DiskPath, (string?)settings[nameof(WorkingCopyOptions.DiskPath)]);
        Assert.NotNull(new WorkingCopyOptions { DiskPath = "" }.Problem());
        Assert.NotNull(new WorkingCopyOptions { DiskPath = "data/working" }.Problem());
        Assert.NotNull(new WorkingCopyOptions { DiskPath = "/home/site/wwwroot/working" }.Problem());
    }

    [Fact]
    public void Settings_that_cannot_run_are_refused()
    {
        Assert.Null(new WorkingCopyOptions().Problem());
        Assert.NotNull(new WorkingCopyOptions { Debounce = TimeSpan.Zero }.Problem());
        Assert.NotNull(new WorkingCopyOptions { Cap = TimeSpan.FromSeconds(4) }.Problem());
        Assert.NotNull(new WorkingCopyOptions { Cap = TimeSpan.FromDays(1) + TimeSpan.FromTicks(1) }.Problem());
        Assert.NotNull(new WorkingCopyOptions { KeepUnchanged = TimeSpan.Zero }.Problem());
    }
}
