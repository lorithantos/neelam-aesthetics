using System.Collections.Concurrent;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The server's disk for working copies, in memory: what <see cref="DiskWorkingCopies"/> does, by client and
/// campaign id. <see cref="Failure"/> makes every call throw, as a full or unwritable disk would.
/// </summary>
internal sealed class InMemoryWorkingCopyDisk : IWorkingCopyDisk
{
    private readonly ConcurrentDictionary<(string Client, Guid Id), DiskCopy> _copies = new();

    /// <summary>When set, what every call throws; null, the default, fails nothing.</summary>
    public Func<Exception>? Failure { get; set; }

    /// <summary>The copy held for a campaign, or null.</summary>
    public DiskCopy? Held(ClientName client, Guid id) => _copies.TryGetValue((client.Value, id), out var copy) ? copy : null;

    /// <summary>Every copy held for a client.</summary>
    public IReadOnlyList<DiskCopy> All(ClientName client) =>
        _copies.Where(c => c.Key.Client == client.Value).Select(c => c.Value).ToList();

    /// <summary>Puts a copy straight on the disk, as an earlier run of the app might have left it.</summary>
    public void Put(ClientName client, DiskCopy copy) => _copies[(client.Value, copy.CampaignId)] = copy;

    private void ThrowIfFailing()
    {
        if (Failure is { } failure) throw failure();
    }

    public Task WriteAsync(ClientName client, DiskCopy copy, CancellationToken ct = default)
    {
        ThrowIfFailing();
        _copies[(client.Value, copy.CampaignId)] = copy;
        return Task.CompletedTask;
    }

    /// <summary>How many times a copy's content was read: by a read, or by the sweep's listing.</summary>
    public int ContentReads => Volatile.Read(ref _contentReads);

    private int _contentReads;

    public Task<DiskCopy?> ReadAsync(ClientName client, Guid campaignId, CancellationToken ct = default)
    {
        ThrowIfFailing();
        Interlocked.Increment(ref _contentReads);
        return Task.FromResult(Held(client, campaignId));
    }

    // From what a sidecar holds: the id, the title and the times, never the content.
    public Task<IReadOnlyList<NotSavedYet>> ListNotSavedYetAsync(ClientName client, CancellationToken ct = default)
    {
        ThrowIfFailing();
        IReadOnlyList<NotSavedYet> listed = All(client)
            .Where(c => c.BasedOn is null)
            .Select(c => new NotSavedYet(c.CampaignId, c.Title, c.WrittenAt))
            .ToList();
        return Task.FromResult(listed);
    }

    public Task<bool> RemoveAsync(ClientName client, Guid campaignId, CancellationToken ct = default)
    {
        ThrowIfFailing();
        return Task.FromResult(_copies.TryRemove((client.Value, campaignId), out _));
    }

    public Task<IReadOnlyList<DiskCopy>> ListAsync(ClientName client, CancellationToken ct = default)
    {
        ThrowIfFailing();
        Interlocked.Increment(ref _contentReads);
        IReadOnlyList<DiskCopy> listed = _copies.Where(c => c.Key.Client == client.Value).Select(c => c.Value).ToList();
        return Task.FromResult(listed);
    }
}
