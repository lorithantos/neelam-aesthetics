using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class TemplateSessionTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_container, Grace);

    private TemplateSession NewWithOneBlock(string name)
    {
        var session = TemplateSession.New(Store);
        session.Editor.Name = name;
        session.Editor.Add(BlockType.Paragraphs);
        return session;
    }

    [Fact]
    public async Task A_new_template_gets_its_id_on_first_save_and_each_save_is_a_new_version()
    {
        var session = NewWithOneBlock("Thank you");
        Assert.Null(session.Id);

        var first = await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Name = "Thank you, again";
        var second = await session.SaveAsync();

        Assert.Equal(first.Id, session.Id);
        Assert.Equal(second, session.Latest);
        Assert.Equal([second.SavedAt, first.SavedAt],
            (await Store.HistoryAsync(DocumentKind.Template, session.Id!.Value)).Select(s => s.SavedAt));
    }

    [Fact]
    public async Task A_template_with_errors_is_not_saved()
    {
        var session = TemplateSession.New(Store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync());
        Assert.Empty(_container.Blobs);
    }

    // Undo hides the newest: the version before comes back, and the undone one is offered back
    // until the sweep has had it, after which it leaves nothing.
    [Fact]
    public async Task Undoing_the_last_save_puts_the_one_before_back_and_the_sweep_leaves_no_trace()
    {
        var session = NewWithOneBlock("Before");
        await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Name = "After";
        var bad = await session.SaveAsync();

        Assert.True(await session.UndoLastSaveAsync());

        Assert.Equal("Before", session.Editor.Name);
        Assert.Equal(bad.BlobName, session.Restorable!.BlobName);
        Assert.Equal(["Before"], (await Store.LatestAsync(DocumentKind.Template)).Select(s => s.Title));

        _clock.Now += Grace;
        await Store.SweepAsync();
        Assert.DoesNotContain(bad.BlobName, _container.Blobs.Keys);
        Assert.Single(_container.Blobs);
    }

    [Fact]
    public async Task Undoing_the_only_save_leaves_no_template_until_it_is_restored()
    {
        var session = NewWithOneBlock("Only");
        var only = await session.SaveAsync();

        Assert.False(await session.UndoLastSaveAsync());
        Assert.Null(session.Latest);
        Assert.Empty(await Store.LatestAsync(DocumentKind.Template));
        Assert.Null(await TemplateSession.OpenAsync(Store, only.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UndoLastSaveAsync());

        // Back on the page later, within the period: it can come back.
        _clock.Now += TimeSpan.FromHours(23);
        var undone = await TemplateSession.RestorableAsync(Store, only.Id);
        var restored = await TemplateSession.RestoreAsync(Store, undone!);
        Assert.Equal("Only", restored!.Editor.Name);
        Assert.Equal(only, restored.Latest);
        Assert.Null(restored.Restorable);
    }

    [Fact]
    public async Task Restore_in_the_editor_brings_the_undone_save_back_and_a_new_save_drops_the_offer()
    {
        var session = NewWithOneBlock("Before");
        await session.SaveAsync();
        _clock.Now += TimeSpan.FromMinutes(1);
        session.Editor.Name = "After";
        var after = await session.SaveAsync();
        await session.UndoLastSaveAsync();

        Assert.True(await session.RestoreAsync());
        Assert.Equal("After", session.Editor.Name);
        Assert.Equal(after, session.Latest);
        Assert.Null(session.Restorable);

        await session.UndoLastSaveAsync();
        Assert.NotNull(session.Restorable);
        _clock.Now += TimeSpan.FromMinutes(1);
        await session.SaveAsync();
        Assert.Null(session.Restorable);
        Assert.Null((await TemplateSession.OpenAsync(Store, session.Id!.Value))!.Restorable);
    }

    [Fact]
    public async Task Opening_reads_the_newest_version_and_an_unknown_template_is_null()
    {
        var id = Guid.NewGuid();
        await Store.SaveTemplateAsync(id, DraftFixtures.Membership);

        var session = await TemplateSession.OpenAsync(Store, id);

        Assert.Equal(id, session!.Id);
        Assert.Equal("Membership announcement", session.Editor.Name);
        Assert.Null(await TemplateSession.OpenAsync(Store, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_copy_saves_as_a_new_template_and_leaves_the_source_alone()
    {
        var source = Guid.NewGuid();
        await Store.SaveTemplateAsync(source, DraftFixtures.Membership);

        var copy = (await TemplateSession.CopyAsync(Store, source))!;
        Assert.Null(copy.Id);
        Assert.Equal("Copy of Membership announcement", copy.Editor.Name);
        await copy.SaveAsync();

        Assert.NotEqual(source, copy.Id);
        Assert.Single(await Store.HistoryAsync(DocumentKind.Template, source));
        Assert.Equal(["Copy of Membership announcement", "Membership announcement"],
            (await Store.LatestAsync(DocumentKind.Template)).Select(s => s.Title).Order(StringComparer.Ordinal));
    }
}
