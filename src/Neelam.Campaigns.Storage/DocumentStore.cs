namespace Neelam.Campaigns.Storage;

/// <summary>One saved version of a document.</summary>
/// <param name="UndoneAt">When the version was undone, or null for a version in use. Stored in the blob's own metadata.</param>
public sealed record DocumentVersion(DateTimeOffset SavedAt, string BlobName, DateTimeOffset? UndoneAt = null);

/// <summary>
/// A document kept the way campaign saves are: every save is a new blob, <c>{prefix}/{stamp}.json</c>,
/// the newest in use is the one in force, and nothing is overwritten. There is no index, so deleting the
/// newest version leaves no record of it and the one before is in force again. That is how a bad
/// change to a catalog or a policy is undone.
/// </summary>
/// <remarks>
/// A version can also be undone the way a draft is (<see cref="UndoMark"/>): marked in its own metadata,
/// left out of the history at once, restorable for the grace period, then deleted by the sweep
/// (<see cref="SweepAsync"/>). The look is undone this way; nothing else marks its versions yet.
/// </remarks>
public sealed class DocumentStore<T> where T : class
{
    private readonly IBlobBackend _blobs;
    private readonly string _prefix;
    private readonly TimeProvider _clock;
    private readonly Func<T, string> _serialize;
    private readonly Func<string, T> _deserialize;

    internal DocumentStore(
        IBlobBackend blobs, string prefix, TimeProvider clock, Func<T, string> serialize, Func<string, T> deserialize)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Contains('/'))
            throw new ArgumentException("A document prefix is one path segment.", nameof(prefix));
        _blobs = blobs;
        _prefix = prefix;
        _clock = clock;
        _serialize = serialize;
        _deserialize = deserialize;
    }

    public async Task<DocumentVersion> SaveAsync(T value, CancellationToken ct = default)
    {
        var (name, at) = await SaveStamp.CreateAsync(
            _blobs, stamp => $"{_prefix}/{SaveStamp.Of(stamp)}.json", _serialize(value),
            new Dictionary<string, string>(), _clock.GetUtcNow(), ct);
        return new DocumentVersion(at, name);
    }

    /// <summary>Every saved version in use, newest first. Undone versions are left out.</summary>
    public async Task<IReadOnlyList<DocumentVersion>> HistoryAsync(CancellationToken ct = default) =>
        (await EntriesAsync(ct)).Select(e => e.Version).Where(v => v.UndoneAt is null).ToList();

    /// <summary>The version in force, or null when none has been saved.</summary>
    public async Task<T?> CurrentAsync(CancellationToken ct = default)
    {
        var newest = (await HistoryAsync(ct)).FirstOrDefault();
        return newest is null ? null : await LoadAsync(newest, ct);
    }

    public async Task<T> LoadAsync(DocumentVersion version, CancellationToken ct = default) =>
        _deserialize(await _blobs.ReadTextAsync(Expect(version), ct));

    /// <summary>Permanently deletes one version. Deleting the newest puts the one before back in force.</summary>
    public Task<bool> DeleteAsync(DocumentVersion version, CancellationToken ct = default) =>
        _blobs.DeleteAsync(Expect(version), ct);

    /// <summary>
    /// Undoes a version: marks it undone now, in its own metadata, so it drops out of the history and
    /// the one before is in force. Its content is untouched until the sweep deletes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The version is already gone.</exception>
    public async Task<DocumentVersion> MarkUndoneAsync(DocumentVersion version, CancellationToken ct = default)
    {
        var blob = await FindAsync(Expect(version), ct) ?? throw new InvalidOperationException("That version is already gone.");
        var at = _clock.GetUtcNow();
        if (!await _blobs.SetMetadataAsync(blob.Name, UndoMark.With(blob.Metadata, at), ct))
            throw new InvalidOperationException("That version is already gone.");
        return version with { UndoneAt = at };
    }

    /// <summary>
    /// The undone version Restore would bring back: the one just above the newest in use (the last one
    /// undone), while its grace period lasts. Null when there is none, so a version undone and then
    /// saved over is not offered back.
    /// </summary>
    public async Task<DocumentVersion?> RestorableAsync(TimeSpan gracePeriod, CancellationToken ct = default)
    {
        var undoneAbove = (await EntriesAsync(ct)).Select(e => e.Version)
            .TakeWhile(v => v.UndoneAt is not null).LastOrDefault();
        return undoneAbove is not null && UndoMark.Restorable(undoneAbove.UndoneAt, gracePeriod, _clock.GetUtcNow())
            ? undoneAbove
            : null;
    }

    /// <summary>
    /// Takes an undo back: clears the version's mark, so it is in use again. False when the version is
    /// gone, is not undone, or its grace period has passed; the mark is read from storage, not from
    /// <paramref name="version"/>.
    /// </summary>
    public async Task<bool> RestoreAsync(DocumentVersion version, TimeSpan gracePeriod, CancellationToken ct = default)
    {
        var blob = await FindAsync(Expect(version), ct);
        if (blob is null || !UndoMark.Restorable(UndoMark.Read(blob.Metadata), gracePeriod, _clock.GetUtcNow()))
            return false;
        return await _blobs.SetMetadataAsync(blob.Name, UndoMark.Without(blob.Metadata), ct);
    }

    /// <summary>
    /// Deletes for good every version undone longer ago than <paramref name="gracePeriod"/>, as
    /// <see cref="CampaignStore.SweepAsync"/> does for drafts: found by listing and reading the marks,
    /// with no state of its own, and each delete conditional on the blob being as listed (its ETag), so a
    /// version restored in between survives. Returns the versions it deleted.
    /// </summary>
    public async Task<IReadOnlyList<DocumentVersion>> SweepAsync(TimeSpan gracePeriod, CancellationToken ct = default)
    {
        var deleted = new List<DocumentVersion>();
        foreach (var (version, etag) in await EntriesAsync(ct))
        {
            if (version.UndoneAt is not null
                && !UndoMark.Restorable(version.UndoneAt, gracePeriod, _clock.GetUtcNow())
                && await _blobs.DeleteIfUnchangedAsync(version.BlobName, etag, ct))
                deleted.Add(version);
        }
        return deleted;
    }

    // Every version, undone ones included, newest first, each with its blob's ETag as listed.
    private async Task<List<(DocumentVersion Version, string ETag)>> EntriesAsync(CancellationToken ct)
    {
        var versions = new List<(DocumentVersion, string)>();
        await foreach (var blob in _blobs.ListAsync($"{_prefix}/", ct))
        {
            // Only {prefix}/{stamp}.json; anything else under the prefix is ignored, not guessed at.
            var rest = blob.Name[(_prefix.Length + 1)..];
            if (!rest.Contains('/') && SaveStamp.TryRead(rest, out var at))
                versions.Add((new DocumentVersion(at, blob.Name, UndoMark.Read(blob.Metadata)), blob.ETag));
        }
        return versions.OrderByDescending(v => v.Item1.SavedAt).ToList();
    }

    private async Task<BlobEntry?> FindAsync(string blobName, CancellationToken ct)
    {
        await foreach (var blob in _blobs.ListAsync(blobName, ct))
        {
            if (blob.Name == blobName) return blob;
        }
        return null;
    }

    private string Expect(DocumentVersion version) =>
        version.BlobName.StartsWith($"{_prefix}/", StringComparison.Ordinal)
            ? version.BlobName
            : throw new ArgumentException($"{version.BlobName} is not a version of this document.", nameof(version));
}
