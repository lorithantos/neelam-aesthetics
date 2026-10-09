using System.Runtime.CompilerServices;
using Janet.Azure.Storage;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Behaves like the container for the stores' purposes: create-if-absent, list, read, set metadata,
/// delete, and delete only if unchanged since listed (an ETag that every write renews).
/// </summary>
internal sealed class InMemoryBlobBackend : IBlobBackend
{
    private readonly Dictionary<string, string> _etags = new(StringComparer.Ordinal);
    private long _version;

    public SortedDictionary<string, (BlobContent Blob, IReadOnlyDictionary<string, string> Metadata)> Blobs { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Runs as a delete arrives, before it takes effect: something else landing in between.</summary>
    public Func<string, Task>? BeforeDelete { get; set; }

    /// <summary>Puts a blob straight in, as something outside the stores might have.</summary>
    public void Put(string name, string json, IReadOnlyDictionary<string, string>? metadata = null)
    {
        Blobs[name] = (new BlobContent(BinaryData.FromString(json), BlobContent.JsonContentType),
            metadata ?? new Dictionary<string, string>());
        Renew(name);
    }

    /// <summary>A stored blob's content as text.</summary>
    public string Text(string name) => Blobs[name].Blob.Content.ToString();

    public async IAsyncEnumerable<BlobEntry> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (name, blob) in Blobs.Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            await Task.Yield();
            yield return new BlobEntry(name, blob.Metadata, ETagOf(name));
        }
    }

    public Task<bool> TryCreateAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        if (!Blobs.TryAdd(name, (new BlobContent(content, contentType), metadata))) return Task.FromResult(false);
        Renew(name);
        return Task.FromResult(true);
    }

    public Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Blobs[name].Blob);

    public Task<bool> SetMetadataAsync(
        string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        if (!Blobs.TryGetValue(name, out var blob)) return Task.FromResult(false);
        Blobs[name] = (blob.Blob, new Dictionary<string, string>(metadata));
        Renew(name);
        return Task.FromResult(true);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (BeforeDelete is { } before) await before(name);
        _etags.Remove(name);
        return Blobs.Remove(name);
    }

    public async Task<bool> DeleteIfUnchangedAsync(string name, string etag, CancellationToken cancellationToken = default)
    {
        if (BeforeDelete is { } before) await before(name);
        if (!Blobs.ContainsKey(name) || ETagOf(name) != etag) return false;
        _etags.Remove(name);
        return Blobs.Remove(name);
    }

    private void Renew(string name) => _etags[name] = $"\"{++_version}\"";

    // A blob a test wrote into Blobs directly gets its first ETag when first asked for.
    private string ETagOf(string name)
    {
        if (!_etags.ContainsKey(name)) Renew(name);
        return _etags[name];
    }
}

/// <summary>A clock that only moves when told to.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
