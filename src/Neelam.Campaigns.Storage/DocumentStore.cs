namespace Neelam.Campaigns.Storage;

/// <summary>One saved version of a document.</summary>
public sealed record DocumentVersion(DateTimeOffset SavedAt, string BlobName);

/// <summary>
/// A document kept the way campaign saves are: every save is a new blob, <c>{prefix}/{stamp}.json</c>,
/// the newest is the one in force, and nothing is overwritten. There is no index, so deleting the
/// newest version leaves no record of it and the one before is in force again. That is how a bad
/// change to a catalog, a policy or a look is undone.
/// </summary>
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

    /// <summary>Every saved version, newest first.</summary>
    public async Task<IReadOnlyList<DocumentVersion>> HistoryAsync(CancellationToken ct = default)
    {
        var versions = new List<DocumentVersion>();
        await foreach (var blob in _blobs.ListAsync($"{_prefix}/", ct))
        {
            // Only {prefix}/{stamp}.json; anything else under the prefix is ignored, not guessed at.
            var rest = blob.Name[(_prefix.Length + 1)..];
            if (!rest.Contains('/') && SaveStamp.TryRead(rest, out var at))
                versions.Add(new DocumentVersion(at, blob.Name));
        }
        return versions.OrderByDescending(v => v.SavedAt).ToList();
    }

    /// <summary>The version in force, or null when none has been saved.</summary>
    public async Task<T?> CurrentAsync(CancellationToken ct = default)
    {
        var newest = (await HistoryAsync(ct)).FirstOrDefault();
        return newest is null ? null : await LoadAsync(newest, ct);
    }

    public async Task<T> LoadAsync(DocumentVersion version, CancellationToken ct = default) =>
        _deserialize(await _blobs.ReadAsync(Expect(version), ct));

    /// <summary>Permanently deletes one version. Deleting the newest puts the one before back in force.</summary>
    public Task<bool> DeleteAsync(DocumentVersion version, CancellationToken ct = default) =>
        _blobs.DeleteAsync(Expect(version), ct);

    private string Expect(DocumentVersion version) =>
        version.BlobName.StartsWith($"{_prefix}/", StringComparison.Ordinal)
            ? version.BlobName
            : throw new ArgumentException($"{version.BlobName} is not a version of this document.", nameof(version));
}
