using System.Collections.Concurrent;
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

    /// <summary>
    /// Storage failing: given the blob name, or the prefix listed, each operation asks this first and throws
    /// what it returns; null lets it through. Null, the default, fails nothing.
    /// </summary>
    public Func<string, Exception?>? Failure { get; set; }

    private void ThrowIfFailing(string name)
    {
        if (Failure?.Invoke(name) is { } failure) throw failure;
    }

    public async IAsyncEnumerable<BlobEntry> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(prefix);
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
        ThrowIfFailing(name);
        if (!Blobs.TryAdd(name, (new BlobContent(content, contentType), metadata))) return Task.FromResult(false);
        Renew(name);
        return Task.FromResult(true);
    }

    /// <summary>Raised after each replacing write: for a test waiting on a working copy written in the background.</summary>
    public Pulse Replaced { get; } = new();

    /// <summary>How many replacing writes went through.</summary>
    public int Replaces => Volatile.Read(ref _replaces);

    private int _replaces;

    public Task<string?> ReplaceAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata, BlobExpected expected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        var there = Blobs.ContainsKey(name);
        if ((expected.Absent && there) || (expected.ETag is { } etag && (!there || ETagOf(name) != etag)))
            return Task.FromResult<string?>(null);
        Blobs[name] = (new BlobContent(content, contentType), new Dictionary<string, string>(metadata));
        Renew(name);
        Interlocked.Increment(ref _replaces);
        Replaced.Raise();
        return Task.FromResult<string?>(ETagOf(name));
    }

    /// <summary>The name of every blob read, in order, so a test can tell what a page read and what it never touched.</summary>
    public ConcurrentQueue<string> Reads { get; } = new();

    public Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        Reads.Enqueue(name);
        return Task.FromResult(Blobs[name].Blob);
    }

    /// <summary>Runs as a metadata write (an undo's mark) arrives, before it takes effect: something else landing in between.</summary>
    public Func<string, Task>? BeforeSetMetadata { get; set; }

    public async Task<bool> SetMetadataAsync(
        string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        if (BeforeSetMetadata is { } before) await before(name);
        if (!Blobs.TryGetValue(name, out var blob)) return false;
        Blobs[name] = (blob.Blob, new Dictionary<string, string>(metadata));
        Renew(name);
        return true;
    }

    public async Task<bool> SetMetadataIfUnchangedAsync(
        string name, IReadOnlyDictionary<string, string> metadata, string etag, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        if (BeforeSetMetadata is { } before) await before(name);
        if (!Blobs.TryGetValue(name, out var blob) || ETagOf(name) != etag) return false;
        Blobs[name] = (blob.Blob, new Dictionary<string, string>(metadata));
        Renew(name);
        return true;
    }

    /// <summary>How many blobs have been deleted: for a test waiting on a delete made in the background.</summary>
    public int Deletes => Volatile.Read(ref this._deletes);

    /// <summary>Raised after each delete.</summary>
    public Pulse Deleted { get; } = new();

    private int _deletes;

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        if (BeforeDelete is { } before) await before(name);
        _etags.Remove(name);
        return this.Removed(Blobs.Remove(name));
    }

    public async Task<bool> DeleteIfUnchangedAsync(string name, string etag, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        if (BeforeDelete is { } before) await before(name);
        if (!Blobs.ContainsKey(name) || ETagOf(name) != etag) return false;
        _etags.Remove(name);
        return this.Removed(Blobs.Remove(name));
    }

    private bool Removed(bool removed)
    {
        if (removed)
        {
            Interlocked.Increment(ref this._deletes);
            this.Deleted.Raise();
        }
        return removed;
    }

    private void Renew(string name) => _etags[name] = $"\"{++_version}\"";

    // A blob a test wrote into Blobs directly gets its first ETag when first asked for.
    private string ETagOf(string name)
    {
        if (!_etags.ContainsKey(name)) Renew(name);
        return _etags[name];
    }
}
