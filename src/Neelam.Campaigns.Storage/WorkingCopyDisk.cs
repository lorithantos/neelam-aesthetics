using System.Text.Json;

namespace Neelam.Campaigns.Storage;

/// <summary>A working copy kept on the server's disk while storage could not be reached.</summary>
/// <param name="Json">The form's draft content, as a save would hold it (<see cref="CampaignJson.SerializeDraft"/>).</param>
/// <param name="BasedOn">When the save the form started from was saved; null when it has none.</param>
/// <param name="WrittenAt">When it was written.</param>
public sealed record DiskCopy(Guid CampaignId, string Json, DateTimeOffset? BasedOn, DateTimeOffset WrittenAt)
{
    /// <summary>The draft it holds.</summary>
    public CampaignDraft Draft => CampaignJson.DeserializeDraft(Json);
}

/// <summary>
/// Where a working copy goes when storage cannot be reached (owner, 2026-11-03): the server's own disk, one
/// file per client and campaign, named by their ids alone and never by anything she wrote. Taken back into
/// storage once storage answers (<see cref="WorkingCopies"/>), and swept with it (<see cref="CampaignStore.SweepAsync(TimeSpan, IWorkingCopyDisk?, CancellationToken)"/>).
/// <see cref="DiskWorkingCopies"/> is the real one; tests use one in memory.
/// </summary>
public interface IWorkingCopyDisk
{
    /// <summary>Writes the campaign's copy in place of any there.</summary>
    Task WriteAsync(ClientName client, DiskCopy copy, CancellationToken ct = default);

    /// <summary>The campaign's copy, or null when it has none.</summary>
    Task<DiskCopy?> ReadAsync(ClientName client, Guid campaignId, CancellationToken ct = default);

    /// <summary>Removes the campaign's copy. False when it had none.</summary>
    Task<bool> RemoveAsync(ClientName client, Guid campaignId, CancellationToken ct = default);

    /// <summary>Every copy of the client's, for the sweep.</summary>
    Task<IReadOnlyList<DiskCopy>> ListAsync(ClientName client, CancellationToken ct = default);
}

/// <summary>
/// The server's disk under <c>WorkingCopy:DiskPath</c>: <c>{root}/{client}/{campaign id}.json</c>. On App Service
/// on Linux that is <c>/home/data/working</c>, under <c>/home</c>, which persists across restarts and is shared
/// by the site's instances, and outside <c>/home/site/wwwroot</c>, which a deploy replaces. A write goes to a
/// temporary file first and then takes the copy's name, so a crash part-way never leaves half a copy.
/// </summary>
public sealed class DiskWorkingCopies(string root) : IWorkingCopyDisk
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The file as written: the draft as text, with when and from what.
    private sealed record Envelope(string Draft, DateTimeOffset? BasedOn, DateTimeOffset WrittenAt);

    internal string PathOf(ClientName client, Guid campaignId) => Path.Combine(root, client.Value, $"{campaignId:N}.json");

    public async Task WriteAsync(ClientName client, DiskCopy copy, CancellationToken ct = default)
    {
        var path = PathOf(client, copy.CampaignId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".writing";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Envelope(copy.Json, copy.BasedOn, copy.WrittenAt), Json), ct);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<DiskCopy?> ReadAsync(ClientName client, Guid campaignId, CancellationToken ct = default)
    {
        var path = PathOf(client, campaignId);
        return File.Exists(path) ? await ReadFileAsync(path, campaignId, ct) : null;
    }

    public Task<bool> RemoveAsync(ClientName client, Guid campaignId, CancellationToken ct = default)
    {
        var path = PathOf(client, campaignId);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyList<DiskCopy>> ListAsync(ClientName client, CancellationToken ct = default)
    {
        var folder = Path.Combine(root, client.Value);
        if (!Directory.Exists(folder)) return [];
        var copies = new List<DiskCopy>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            // A name this app did not write is left alone rather than guessed at.
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)
                && await ReadFileAsync(path, id, ct) is { } copy)
                copies.Add(copy);
        }
        return copies;
    }

    private static async Task<DiskCopy?> ReadFileAsync(string path, Guid campaignId, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(path, ct), Json)
                       ?? throw new InvalidDataException("An empty working copy on the disk.");
        return new DiskCopy(campaignId, envelope.Draft, envelope.BasedOn, envelope.WrittenAt);
    }
}
