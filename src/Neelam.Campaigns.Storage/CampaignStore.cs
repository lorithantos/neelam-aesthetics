using System.Globalization;

namespace Neelam.Campaigns.Storage;

public enum DocumentKind { Draft, Template }

/// <summary>One save: a single blob, named by what it is and when it was saved.</summary>
/// <param name="Id">The campaign or template this save belongs to.</param>
/// <param name="Title">Shown in lists; stored in the blob's own metadata.</param>
/// <param name="UndoneAt">When the save was undone, or null for a save in use. Stored in the blob's own metadata.</param>
public sealed record SaveRef(
    DocumentKind Kind, Guid Id, DateTimeOffset SavedAt, string Title, string BlobName, DateTimeOffset? UndoneAt = null);

/// <summary>
/// Saves drafts and templates as date/time-stamped blobs:
/// <c>drafts/{id}/{yyyyMMddTHHmmss.fffffffZ}.json</c>. Every save is a new blob and nothing is
/// ever overwritten. There is no index, manifest or database: lists are read from the blob names
/// themselves and the title travels in the blob's metadata. So deleting a blob leaves no record of
/// it anywhere — delete the newest save and the one before it becomes the latest; delete them all
/// and the campaign is gone.
/// </summary>
/// <remarks>
/// Undo does not delete. It marks the save undone in that blob's own metadata, with the time, and
/// from then on every list, history and latest leaves the save out, so to its client it is gone.
/// Until the grace period has passed the mark can be cleared again (restore); after it, the sweep
/// deletes the blob, and the save leaves no record as before. There is still no index: the sweep
/// finds marks by listing the blobs and reading their metadata.
/// </remarks>
/// <param name="undoGracePeriod">How long an undone save can be restored before the sweep may delete it.</param>
public sealed class CampaignStore(IBlobBackend blobs, TimeProvider clock, TimeSpan undoGracePeriod)
{
    private const string TitleKey = "title";
    private const string UndoneKey = "undone";

    public Task<SaveRef> SaveDraftAsync(Guid id, string title, CampaignDraft draft, CancellationToken ct = default) =>
        SaveAsync(DocumentKind.Draft, id, title, CampaignJson.SerializeDraft(draft), ct);

    public Task<SaveRef> SaveTemplateAsync(Guid id, CampaignTemplate template, CancellationToken ct = default) =>
        SaveAsync(DocumentKind.Template, id, template.Name, CampaignJson.SerializeTemplate(template), ct);

    /// <summary>How long an undone save can be restored.</summary>
    public TimeSpan UndoGracePeriod => undoGracePeriod;

    /// <summary>Every save of this kind, newest first. Undone saves are left out.</summary>
    public async Task<IReadOnlyList<SaveRef>> ListAsync(DocumentKind kind, CancellationToken ct = default) =>
        InUse(await ListPrefixAsync(kind, $"{Prefix(kind)}/", ct));

    /// <summary>Every save of one campaign or template, newest first. Undone saves are left out.</summary>
    public async Task<IReadOnlyList<SaveRef>> HistoryAsync(DocumentKind kind, Guid id, CancellationToken ct = default) =>
        InUse(await ListPrefixAsync(kind, $"{Prefix(kind)}/{id:N}/", ct));

    /// <summary>The newest save of each campaign or template, newest first.</summary>
    public async Task<IReadOnlyList<SaveRef>> LatestAsync(DocumentKind kind, CancellationToken ct = default) =>
        (await ListAsync(kind, ct)).GroupBy(s => s.Id).Select(g => g.First()).ToList();

    public async Task<CampaignDraft> LoadDraftAsync(SaveRef save, CancellationToken ct = default) =>
        CampaignJson.DeserializeDraft(await blobs.ReadTextAsync(Expect(save, DocumentKind.Draft), ct));

    public async Task<CampaignTemplate> LoadTemplateAsync(SaveRef save, CancellationToken ct = default) =>
        CampaignJson.DeserializeTemplate(await blobs.ReadTextAsync(Expect(save, DocumentKind.Template), ct));

    /// <summary>
    /// Undoes a save: marks it undone now, in its own metadata, so it drops out of every list. Its
    /// content is untouched until the sweep deletes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The save is already gone.</exception>
    public async Task<SaveRef> MarkUndoneAsync(SaveRef save, CancellationToken ct = default)
    {
        var blob = await FindAsync(save, ct) ?? throw new InvalidOperationException("That save is already gone.");
        var at = clock.GetUtcNow();
        var metadata = new Dictionary<string, string>(blob.Metadata) { [UndoneKey] = at.ToString("o", CultureInfo.InvariantCulture) };
        if (!await blobs.SetMetadataAsync(save.BlobName, metadata, ct))
            throw new InvalidOperationException("That save is already gone.");
        return save with { UndoneAt = at };
    }

    /// <summary>When an undone save can no longer be restored, and the sweep may delete it.</summary>
    public DateTimeOffset? RestorableUntil(SaveRef save) => save.UndoneAt + undoGracePeriod;

    /// <summary>
    /// The undone save that Restore would bring back for one campaign or template: the save just
    /// above the newest one in use (the last one undone), while its grace period lasts. Null when
    /// there is none, so a save undone and then saved over is not offered back.
    /// </summary>
    public async Task<SaveRef?> RestorableAsync(DocumentKind kind, Guid id, CancellationToken ct = default)
    {
        var undoneAbove = (await ListPrefixAsync(kind, $"{Prefix(kind)}/{id:N}/", ct))
            .TakeWhile(s => s.UndoneAt is not null).LastOrDefault();
        return undoneAbove is not null && CanRestore(undoneAbove) ? undoneAbove : null;
    }

    /// <summary>
    /// Takes an undo back: clears the save's mark, so it is in use again. False when the save is
    /// gone, is not undone, or its grace period has passed; the mark is read from storage, not
    /// from <paramref name="save"/>.
    /// </summary>
    public async Task<bool> RestoreAsync(SaveRef save, CancellationToken ct = default)
    {
        var blob = await FindAsync(save, ct);
        if (blob is null || !TryParse(save.Kind, blob, out var current) || !CanRestore(current))
            return false;
        var metadata = blob.Metadata.Where(m => m.Key != UndoneKey).ToDictionary(m => m.Key, m => m.Value);
        return await blobs.SetMetadataAsync(save.BlobName, metadata, ct);
    }

    /// <summary>
    /// Deletes for good every draft and template undone longer ago than the grace period, leaving
    /// no record. Finds them by listing the blobs and reading their marks, so it holds no state:
    /// running it twice, or again after a crash part-way, only finishes the job. Returns how many
    /// it deleted.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            foreach (var save in await ListPrefixAsync(kind, $"{Prefix(kind)}/", ct))
            {
                if (save.UndoneAt is not null && !CanRestore(save) && await blobs.DeleteAsync(save.BlobName, ct))
                    deleted++;
            }
        }
        return deleted;
    }

    // The grace period runs from the mark; at its end the save belongs to the sweep.
    private bool CanRestore(SaveRef save) => save.UndoneAt is { } at && clock.GetUtcNow() < at + undoGracePeriod;

    private static List<SaveRef> InUse(List<SaveRef> saves) => saves.Where(s => s.UndoneAt is null).ToList();

    private async Task<BlobEntry?> FindAsync(SaveRef save, CancellationToken ct)
    {
        await foreach (var blob in blobs.ListAsync(save.BlobName, ct))
        {
            if (blob.Name == save.BlobName) return blob;
        }
        return null;
    }

    private async Task<SaveRef> SaveAsync(DocumentKind kind, Guid id, string title, string json, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string> { [TitleKey] = Uri.EscapeDataString(title) };
        var (name, at) = await SaveStamp.CreateAsync(
            blobs, stamp => BlobName(kind, id, stamp), json, metadata, clock.GetUtcNow(), ct);
        return new SaveRef(kind, id, at, title, name);
    }

    // Every save under the prefix, undone ones included, newest first.
    private async Task<List<SaveRef>> ListPrefixAsync(DocumentKind kind, string prefix, CancellationToken ct)
    {
        var saves = new List<SaveRef>();
        await foreach (var blob in blobs.ListAsync(prefix, ct))
        {
            if (TryParse(kind, blob, out var save)) saves.Add(save);
        }
        return saves.OrderByDescending(s => s.SavedAt).ToList();
    }

    private static string Prefix(DocumentKind kind) => kind switch
    {
        DocumentKind.Draft => "drafts",
        DocumentKind.Template => "templates",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string BlobName(DocumentKind kind, Guid id, DateTimeOffset at) =>
        $"{Prefix(kind)}/{id:N}/{SaveStamp.Of(at)}.json";

    // Blobs that do not follow the naming scheme are ignored rather than guessed at.
    private static bool TryParse(DocumentKind kind, BlobEntry blob, out SaveRef save)
    {
        save = null!;
        var parts = blob.Name.Split('/');
        if (parts.Length != 3 || parts[0] != Prefix(kind))
            return false;
        if (!Guid.TryParseExact(parts[1], "N", out var id))
            return false;
        if (!SaveStamp.TryRead(parts[2], out var at))
            return false;

        var title = blob.Metadata.TryGetValue(TitleKey, out var t) ? Uri.UnescapeDataString(t) : "(untitled)";
        // A mark that cannot be read is treated as no mark: the save stays in use and is never swept.
        DateTimeOffset? undone = blob.Metadata.TryGetValue(UndoneKey, out var u)
                                 && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : null;
        save = new SaveRef(kind, id, at, title, blob.Name, undone);
        return true;
    }

    private static string Expect(SaveRef save, DocumentKind kind) =>
        save.Kind == kind
            ? save.BlobName
            : throw new ArgumentException($"Expected a {kind} save, got a {save.Kind} save.", nameof(save));
}
