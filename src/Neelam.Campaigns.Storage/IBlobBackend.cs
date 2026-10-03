using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>A blob as listed: its name and metadata, without content.</summary>
public sealed record BlobEntry(string Name, IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// The four blob operations the stores need, in bytes: text is a thin layer on top
/// (<see cref="BlobText"/>), so there is one storage path. <see cref="AzureBlobBackend"/> is the
/// real backend, delegating to Janet.Azure.Storage; tests use an in-memory one. It exists so the
/// stores can be tested without Azure. There is deliberately no overwrite and no update.
/// </summary>
public interface IBlobBackend
{
    IAsyncEnumerable<BlobEntry> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Creates the blob only if no blob has that name. False if one already does.</summary>
    Task<bool> TryCreateAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the blob. False if it was already gone.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>JSON documents over the byte-level backend.</summary>
public static class BlobText
{
    public const string JsonContentType = "application/json; charset=utf-8";

    public static Task<bool> TryCreateTextAsync(
        this IBlobBackend blobs, string name, string json, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        blobs.TryCreateAsync(name, BinaryData.FromString(json), JsonContentType, metadata, cancellationToken);

    public static async Task<string> ReadTextAsync(
        this IBlobBackend blobs, string name, CancellationToken cancellationToken = default) =>
        (await blobs.ReadAsync(name, cancellationToken)).Content.ToString();
}
