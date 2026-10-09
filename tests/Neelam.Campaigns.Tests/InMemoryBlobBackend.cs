using System.Runtime.CompilerServices;
using Janet.Azure.Storage;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>Behaves like the container for the stores' purposes: create-if-absent, list, read, set metadata, delete.</summary>
internal sealed class InMemoryBlobBackend : IBlobBackend
{
    public SortedDictionary<string, (BlobContent Blob, IReadOnlyDictionary<string, string> Metadata)> Blobs { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Puts a blob straight in, as something outside the stores might have.</summary>
    public void Put(string name, string json, IReadOnlyDictionary<string, string>? metadata = null) =>
        Blobs[name] = (new BlobContent(BinaryData.FromString(json), BlobContent.JsonContentType),
            metadata ?? new Dictionary<string, string>());

    /// <summary>A stored blob's content as text.</summary>
    public string Text(string name) => Blobs[name].Blob.Content.ToString();

    public async IAsyncEnumerable<BlobEntry> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (name, blob) in Blobs.Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            await Task.Yield();
            yield return new BlobEntry(name, blob.Metadata);
        }
    }

    public Task<bool> TryCreateAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Blobs.TryAdd(name, (new BlobContent(content, contentType), metadata)));

    public Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Blobs[name].Blob);

    public Task<bool> SetMetadataAsync(
        string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        if (!Blobs.TryGetValue(name, out var blob)) return Task.FromResult(false);
        Blobs[name] = (blob.Blob, new Dictionary<string, string>(metadata));
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Blobs.Remove(name));
}

/// <summary>A clock that only moves when told to.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
