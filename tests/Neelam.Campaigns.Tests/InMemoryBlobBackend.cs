using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Janet.Azure.Storage;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Behaves like the container for the stores' purposes: create-if-absent, list, read, set metadata,
/// delete, and delete only if unchanged since listed (an ETag that every write renews).
/// </summary>
/// <remarks>
/// Safe to share between a test and the app's own background services: the blobs, their ETags and
/// the next ETag's version change together under one lock, which is never held across an await or
/// while calling out to a test's hooks, failures or pulses.
/// </remarks>
internal sealed class InMemoryBlobBackend : IBlobBackend
{
    private readonly Lock _lock = new();

    private readonly SortedDictionary<string, (BlobContent Blob, IReadOnlyDictionary<string, string> Metadata)> _blobs =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _etags = new(StringComparer.Ordinal);
    private long _version;

    /// <summary>The blobs as they stand, in name order: a copy, so a test reads them while the app writes.</summary>
    public IReadOnlyDictionary<string, (BlobContent Blob, IReadOnlyDictionary<string, string> Metadata)> Blobs
    {
        get
        {
            lock (_lock)
                return new SortedDictionary<string, (BlobContent Blob, IReadOnlyDictionary<string, string> Metadata)>(
                    _blobs, StringComparer.Ordinal);
        }
    }

    /// <summary>Runs as a delete arrives, before it takes effect: something else landing in between.</summary>
    public Func<string, Task>? BeforeDelete { get; set; }

    /// <summary>Puts a blob straight in, as something outside the stores might have.</summary>
    public void Put(string name, string json, IReadOnlyDictionary<string, string>? metadata = null)
    {
        lock (_lock)
        {
            _blobs[name] = (new BlobContent(BinaryData.FromString(json), BlobContent.JsonContentType),
                metadata ?? new Dictionary<string, string>());
            Renew(name);
        }
    }

    /// <summary>Takes a blob straight out, as something outside the stores might have: no delete is counted.</summary>
    public bool Remove(string name)
    {
        lock (_lock) return Drop(name);
    }

    /// <summary>A stored blob's content as text.</summary>
    public string Text(string name)
    {
        lock (_lock) return _blobs[name].Blob.Content.ToString();
    }

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
        foreach (var entry in Listed(prefix))
        {
            await Task.Yield();
            yield return entry;
        }
    }

    // Each blob as it stood when listed, ETag and all, as a page of a real listing gives them: a write
    // after it leaves the lister holding the ETag it listed, which then no longer matches.
    private List<BlobEntry> Listed(string prefix)
    {
        lock (_lock)
            return [.. _blobs.Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(b => new BlobEntry(b.Key, b.Value.Metadata, _etags[b.Key]))];
    }

    public Task<bool> TryCreateAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        lock (_lock)
        {
            if (!_blobs.TryAdd(name, (new BlobContent(content, contentType), metadata))) return Task.FromResult(false);
            Renew(name);
        }
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
        string written;
        lock (_lock)
        {
            var there = _blobs.ContainsKey(name);
            if ((expected.Absent && there) || (expected.ETag is { } etag && (!there || _etags[name] != etag)))
                return Task.FromResult<string?>(null);
            _blobs[name] = (new BlobContent(content, contentType), new Dictionary<string, string>(metadata));
            written = Renew(name);
        }
        Interlocked.Increment(ref _replaces);
        Replaced.Raise();
        return Task.FromResult<string?>(written);
    }

    /// <summary>The name of every blob read, in order, so a test can tell what a page read and what it never touched.</summary>
    public ConcurrentQueue<string> Reads { get; } = new();

    public Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        Reads.Enqueue(name);
        lock (_lock) return Task.FromResult(_blobs[name].Blob);
    }

    /// <summary>Runs as a metadata write (an undo's mark) arrives, before it takes effect: something else landing in between.</summary>
    public Func<string, Task>? BeforeSetMetadata { get; set; }

    public async Task<bool> SetMetadataAsync(
        string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        if (BeforeSetMetadata is { } before) await before(name);
        lock (_lock)
        {
            if (!_blobs.TryGetValue(name, out var blob)) return false;
            _blobs[name] = (blob.Blob, new Dictionary<string, string>(metadata));
            Renew(name);
        }
        return true;
    }

    public async Task<bool> SetMetadataIfUnchangedAsync(
        string name, IReadOnlyDictionary<string, string> metadata, string etag, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        if (BeforeSetMetadata is { } before) await before(name);
        lock (_lock)
        {
            if (!_blobs.TryGetValue(name, out var blob) || _etags[name] != etag) return false;
            _blobs[name] = (blob.Blob, new Dictionary<string, string>(metadata));
            Renew(name);
        }
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
        bool removed;
        lock (_lock) removed = Drop(name);
        return this.Removed(removed);
    }

    public async Task<bool> DeleteIfUnchangedAsync(string name, string etag, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(name);
        if (BeforeDelete is { } before) await before(name);
        bool removed;
        lock (_lock) removed = _blobs.ContainsKey(name) && _etags[name] == etag && Drop(name);
        return this.Removed(removed);
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

    // Under the lock, as is everything below: every blob has an ETag from the write that stored it,
    // and loses it with the blob.
    private string Renew(string name) => _etags[name] = $"\"{++_version}\"";

    private bool Drop(string name)
    {
        _etags.Remove(name);
        return _blobs.Remove(name);
    }
}
