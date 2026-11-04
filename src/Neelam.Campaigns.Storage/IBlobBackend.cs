using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>A blob as listed: its name and metadata, without content.</summary>
/// <param name="ETag">The blob's version when it was listed; any change to it, metadata included, gives a new one.</param>
public sealed record BlobEntry(string Name, IReadOnlyDictionary<string, string> Metadata, string ETag);

/// <summary>
/// The blob operations the stores need, in bytes: text is a thin layer on top
/// (<see cref="BlobText"/>), so there is one storage path. <see cref="AzureBlobBackend"/> is the
/// real backend, delegating to Janet.Azure.Storage; tests use an in-memory one. It exists so the
/// stores can be tested without Azure. A save is never overwritten and its content never updated:
/// only a blob's metadata can change, which is how Undo marks a save without deleting it. The one
/// overwrite, <see cref="ReplaceAsync"/>, is the working copy's (<see cref="WorkingCopies"/>), which
/// lives outside every save's prefix and is a recovery copy, never a save.
/// </summary>
public interface IBlobBackend
{
    /// <summary>
    /// Writes the blob whole, content and metadata, in place of any blob of that name, only when what is
    /// there is what <paramref name="expected"/> says. For the working copy alone (owner, 2026-11-03):
    /// overwritten in place, never under a save's prefix.
    /// </summary>
    /// <returns>The blob's new ETag; null when it was not as expected (another tab wrote or removed it), and nothing was written.</returns>
    Task<string?> ReplaceAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata, BlobExpected expected,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<BlobEntry> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Creates the blob only if no blob has that name. False if one already does.</summary>
    Task<bool> TryCreateAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the blob's metadata, leaving its content as it is. False if the blob is gone.
    /// </summary>
    Task<bool> SetMetadataAsync(
        string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the blob's metadata only if it is still the version listed with <paramref name="etag"/>,
    /// leaving its content as it is. False if it is gone or has changed since (an undo marked meanwhile,
    /// say), in which case it is left as it is.
    /// </summary>
    Task<bool> SetMetadataIfUnchangedAsync(
        string name, IReadOnlyDictionary<string, string> metadata, string etag, CancellationToken cancellationToken = default);

    /// <summary>Deletes the blob. False if it was already gone.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the blob only if it is still the version listed with <paramref name="etag"/>. False
    /// if it is gone, or has changed since (its metadata included), in which case it is left as it is.
    /// </summary>
    Task<bool> DeleteIfUnchangedAsync(string name, string etag, CancellationToken cancellationToken = default);
}

/// <summary>What a replacing write (<see cref="IBlobBackend.ReplaceAsync"/>) must find there first.</summary>
/// <param name="ETag">The version that must still be there (If-Match); null for none in particular.</param>
/// <param name="Absent">True when no blob may be there yet (If-None-Match: *).</param>
public sealed record BlobExpected(string? ETag, bool Absent)
{
    /// <summary>Whatever is there, or nothing: the write is unconditional.</summary>
    public static readonly BlobExpected Anything = new(null, false);

    /// <summary>No blob of that name yet.</summary>
    public static readonly BlobExpected Nothing = new(null, true);

    /// <summary>Still the version with this ETag.</summary>
    public static BlobExpected Version(string etag) =>
        string.IsNullOrEmpty(etag) ? throw new ArgumentException("A version needs its ETag.", nameof(etag)) : new(etag, false);
}

/// <summary>
/// JSON documents over the byte-level backend, as text: <see cref="CampaignJson"/> owns the
/// mapping and the schema check, so the stores hand it strings rather than typed values.
/// </summary>
public static class BlobText
{
    public static Task<bool> TryCreateTextAsync(
        this IBlobBackend blobs, string name, string json, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        blobs.TryCreateAsync(name, BinaryData.FromString(json), BlobContent.JsonContentType, metadata, cancellationToken);

    public static async Task<string> ReadTextAsync(
        this IBlobBackend blobs, string name, CancellationToken cancellationToken = default) =>
        (await blobs.ReadAsync(name, cancellationToken)).Content.ToString();
}
