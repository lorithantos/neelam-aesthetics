namespace Neelam.Campaigns.Storage;

/// <summary>A blob as listed: its name and metadata, without content.</summary>
public sealed record BlobEntry(string Name, IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// The four blob operations the store needs. <see cref="AzureBlobBackend"/> is the real one;
/// tests use an in-memory one. There is deliberately no overwrite and no update.
/// </summary>
public interface IBlobBackend
{
    IAsyncEnumerable<BlobEntry> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Creates the blob only if no blob has that name. False if one already does.</summary>
    Task<bool> TryCreateAsync(
        string name, string content, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    Task<string> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the blob. False if it was already gone.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
