using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A client's earlier looks kept and brought back (owner, 2026-10-09: "We should allow it to be
/// restored after a set of edits, no?"): every save is a version she can use again, going back to the
/// standard look keeps them, and the newest can be undone and restored as an editor's save can. In
/// memory, on the test's own clock.
/// </summary>
public class LookHistoryTests
{
    private static readonly ClientName Salon = new("look-salon");
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);
    private static readonly ClientLook Green = new(accentColour: "#225e3e");

    private readonly InMemoryContainers _containers = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly TestRecords _records;
    private readonly ClientStores _stores;

    public LookHistoryTests()
    {
        _records = new TestRecords(_clock);
        _stores = _records.Stores(_containers.For, Grace);
    }

    private IReadOnlyCollection<string> SettingsBlobs => _containers.For("settings").Blobs.Keys;

    // Saves a look a minute after the last, so each version has its own time.
    private async Task<DocumentVersion> Saved(ClientLook look)
    {
        _clock.Now += TimeSpan.FromMinutes(1);
        return await _stores.SaveLookAsync(Salon, look, Actor.Demo);
    }

    private async Task<ClientLook?> InForce() => await _stores.Look(Salon).CurrentAsync();

    [Fact]
    public async Task Earlier_looks_are_listed_newest_first_with_the_one_in_use_marked()
    {
        var first = await Saved(Green);
        var second = await Saved(ClientLookTests.Blush);

        var history = await _stores.LookHistoryAsync(Salon);

        Assert.Equal([second, first], history.Versions.Select(v => v.Version));
        Assert.Equal([ClientLookTests.Blush, Green], history.Versions.Select(v => v.Look));
        Assert.Equal([true, false], history.Versions.Select(v => v.InUse));
        Assert.Equal(ClientLookTests.Blush, history.InForce);
        Assert.True(history.IsOwn);
    }

    [Fact]
    public async Task The_list_is_her_last_twenty_saves()
    {
        var saved = new List<DocumentVersion>();
        for (var i = 0; i < ClientStores.EarlierLooksListed + 3; i++)
            saved.Add(await Saved(i % 2 == 0 ? Green : ClientLookTests.Blush));

        var history = await _stores.LookHistoryAsync(Salon);

        Assert.Equal(20, ClientStores.EarlierLooksListed);
        Assert.Equal(saved.AsEnumerable().Reverse().Take(20), history.Versions.Select(v => v.Version));
    }

    // A copy, as a new version: the earlier one is neither changed nor removed, and stays listed.
    [Fact]
    public async Task Using_a_look_again_saves_a_new_version_equal_in_colours_and_leaves_the_old_one()
    {
        var earlier = await Saved(Green);
        await Saved(ClientLookTests.Blush);
        var before = await _containers.For("settings").ReadAsync(earlier.BlobName);
        _clock.Now += TimeSpan.FromMinutes(1);

        var again = await _stores.UseLookAgainAsync(Salon, earlier, Actor.Demo);

        Assert.NotEqual(earlier.BlobName, again.BlobName);
        Assert.Equal(Green, await InForce());
        Assert.Equal(Green, await _stores.Look(Salon).LoadAsync(again));
        Assert.Equal(before.Content.ToString(), (await _containers.For("settings").ReadAsync(earlier.BlobName)).Content.ToString());
        var history = await _stores.LookHistoryAsync(Salon);
        Assert.Equal(3, history.Versions.Count);
        Assert.Equal(again, history.Versions[0].Version);
        Assert.Contains(history.Versions, v => v.Version == earlier && !v.InUse);
    }

    // Going back to the standard look saves a version that says so; nothing she saved is deleted.
    [Fact]
    public async Task The_standard_look_keeps_her_earlier_looks_to_use_again()
    {
        var mine = await Saved(ClientLookTests.Blush);
        _clock.Now += TimeSpan.FromMinutes(1);

        var standard = await _stores.UseStandardLookAsync(Salon, Actor.Demo);

        Assert.Equal(ClientLook.Standard, await InForce());
        Assert.Equal(2, SettingsBlobs.Count(k => k.StartsWith("look-salon/", StringComparison.Ordinal)));
        var history = await _stores.LookHistoryAsync(Salon);
        Assert.Equal([standard, mine], history.Versions.Select(v => v.Version));
        Assert.True(history.Versions[0].InUse);
        Assert.False(history.IsOwn);

        _clock.Now += TimeSpan.FromMinutes(1);
        await _stores.UseLookAgainAsync(Salon, mine, Actor.Demo);
        Assert.Equal(ClientLookTests.Blush, await InForce());
    }

    // As the editors' undo: marked, not deleted; the one before is in force at once; Restore brings it back.
    [Fact]
    public async Task Undo_marks_the_newest_undone_puts_the_one_before_in_force_and_restore_brings_it_back()
    {
        var first = await Saved(Green);
        var second = await Saved(ClientLookTests.Blush);
        _clock.Now += TimeSpan.FromMinutes(1);

        var undone = await _stores.UndoLastLookSaveAsync(Salon, Actor.Demo);

        Assert.Equal(second.BlobName, undone!.BlobName);
        Assert.Equal(_clock.Now, undone.UndoneAt);
        Assert.Equal(Green, await InForce());
        Assert.Contains(second.BlobName, SettingsBlobs);
        var history = await _stores.LookHistoryAsync(Salon);
        Assert.Equal([first], history.Versions.Select(v => v.Version));
        Assert.Equal(second.BlobName, history.Restorable!.BlobName);
        Assert.Equal(_clock.Now + Grace, history.RestorableUntil);

        Assert.True(await _stores.RestoreLookAsync(Salon, history.Restorable, Actor.Demo));

        Assert.Equal(ClientLookTests.Blush, await InForce());
        Assert.Null((await _stores.LookHistoryAsync(Salon)).Restorable);
    }

    // Her only look undone: the standard look, and still hers to restore.
    [Fact]
    public async Task Undoing_her_only_look_leaves_the_standard_one()
    {
        var only = await Saved(ClientLookTests.Blush);

        await _stores.UndoLastLookSaveAsync(Salon, Actor.Demo);

        Assert.Null(await InForce());
        Assert.Equal(only.BlobName, (await _stores.LookHistoryAsync(Salon)).Restorable!.BlobName);
        Assert.Null(await _stores.UndoLastLookSaveAsync(Salon, Actor.Demo));
    }

    [Fact]
    public async Task An_undone_look_cannot_be_restored_once_its_grace_period_has_passed()
    {
        await Saved(Green);
        await Saved(ClientLookTests.Blush);
        var undone = await _stores.UndoLastLookSaveAsync(Salon, Actor.Demo);

        _clock.Now += Grace;

        Assert.Null((await _stores.LookHistoryAsync(Salon)).Restorable);
        Assert.False(await _stores.RestoreLookAsync(Salon, undone!, Actor.Demo));
        Assert.Equal(Green, await InForce());
    }

    // A save made after an undo is not something Restore should take back over.
    [Fact]
    public async Task A_look_undone_and_then_saved_over_is_not_offered_back()
    {
        await Saved(Green);
        await Saved(ClientLookTests.Blush);
        await _stores.UndoLastLookSaveAsync(Salon, Actor.Demo);

        await Saved(Green);

        Assert.Null((await _stores.LookHistoryAsync(Salon)).Restorable);
    }

    // Ids only: which look (always "look") and which version, by its stamp; never a colour.
    [Fact]
    public async Task Using_again_going_back_undoing_and_restoring_are_on_the_trail_by_id_only()
    {
        var first = await Saved(ClientLookTests.Blush);
        _clock.Now += TimeSpan.FromMinutes(1);
        var standard = await _stores.UseStandardLookAsync(Salon, Actor.Demo);
        _clock.Now += TimeSpan.FromMinutes(1);
        var again = await _stores.UseLookAgainAsync(Salon, first, Actor.Demo);
        var undone = await _stores.UndoLastLookSaveAsync(Salon, Actor.Demo);
        await _stores.RestoreLookAsync(Salon, undone!, Actor.Demo);

        var events = _records.Activity.Events.Where(e => e.Client == Salon).ToList();

        Assert.Equal(
            [
                (ActivityAction.LookSaved, Stamp(first)), (ActivityAction.LookResetToStandard, Stamp(standard)),
                (ActivityAction.LookUsedAgain, Stamp(again)), (ActivityAction.Undone, Stamp(again)),
                (ActivityAction.Restored, Stamp(again)),
            ],
            events.Select(e => (e.Action, e.SaveStamp)));
        Assert.All(events, e => Assert.Equal((ActivityEntity.Look, "look", "demo user"), (e.Entity, e.EntityId, e.Actor)));
        Assert.All(events, e => Assert.DoesNotContain("#", e.ToString()));
    }

    // A document changed by hand is listed, so she can see a save is there, but it is never used:
    // the pages fall back to the standard look, as they do when it is the newest.
    [Theory]
    [InlineData("""{"schema":2,"pageBackground":"red;}body{display:none"}""")]
    [InlineData("""{"schema":2,"standard":true,"accentColour":"#225e3e"}""")]
    [InlineData("""not json""")]
    public async Task A_stored_look_that_cannot_be_read_is_listed_as_such_and_never_used(string document)
    {
        var good = await Saved(Green);
        _clock.Now += TimeSpan.FromMinutes(1);
        var tampered = new DocumentVersion(_clock.Now, $"look-salon/{Stamp(_clock.Now)}.json");
        _containers.For("settings").Put(tampered.BlobName, document);

        var history = await _stores.LookHistoryAsync(Salon);

        Assert.Equal([tampered.BlobName, good.BlobName], history.Versions.Select(v => v.Version.BlobName));
        Assert.Null(history.Versions[0].Look);
        Assert.Equal([false, false], history.Versions.Select(v => v.InUse));
        Assert.Null(history.InForce);
        await Assert.ThrowsAnyAsync<Exception>(() => _stores.UseLookAgainAsync(Salon, history.Versions[0].Version, Actor.Demo));
        Assert.Equal(2, SettingsBlobs.Count);
    }

    [Fact]
    public void The_standard_look_is_saved_as_a_marker_with_no_colours()
    {
        var json = CampaignJson.SerializeLook(ClientLook.Standard);

        Assert.Equal(ClientLook.Standard, CampaignJson.DeserializeLook(json));
        Assert.True(CampaignJson.DeserializeLook(json).IsStandard);
        Assert.Equal(LookPalette.Standard, ClientLook.Standard.Palette);
        Assert.Null(Neelam.Web.LookStyle.RootBlock(ClientLook.Standard));
        // A reader from before the marker skips it and finds no colours: the standard look either way.
        Assert.Equal(ClientLook.Default, CampaignJson.DeserializeLook(json.Replace("\"standard\"", "\"unknownToAnOldReader\"")));
        // A look she saved is never the marker, so an old look still reads as hers.
        Assert.False(CampaignJson.DeserializeLook(CampaignJson.SerializeLook(Green)).IsStandard);
        Assert.DoesNotContain("standard", CampaignJson.SerializeLook(Green));
    }

    private static string Stamp(DocumentVersion version) => SaveStamp.Of(version.SavedAt);

    private static string Stamp(DateTimeOffset at) => SaveStamp.Of(at);
}
