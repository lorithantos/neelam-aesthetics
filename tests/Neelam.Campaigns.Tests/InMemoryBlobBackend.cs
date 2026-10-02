using System.Runtime.CompilerServices;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>Behaves like the container for the store's purposes: create-if-absent, list, read, delete.</summary>
internal sealed class InMemoryBlobBackend : IBlobBackend
{
    public SortedDictionary<string, (string Content, IReadOnlyDictionary<string, string> Metadata)> Blobs { get; } =
        new(StringComparer.Ordinal);

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
        string name, string content, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Blobs.TryAdd(name, (content, metadata)));

    public Task<string> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Blobs[name].Content);

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Blobs.Remove(name));
}

/// <summary>A clock that only moves when told to.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
