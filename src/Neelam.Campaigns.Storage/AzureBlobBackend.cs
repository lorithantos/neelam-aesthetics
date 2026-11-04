using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// One container in Azure Blob Storage, a client's own or the shared settings container, reached
/// through the app's <see cref="StorageClients"/>: an Entra ID token only, the app's retry budget,
/// and no way in for a connection string, account key or SAS.
/// </summary>
public sealed class AzureBlobBackend : IBlobBackend
{
    private readonly BlobContainerClient _container;

    /// <param name="storage">The app's storage clients, built once at startup.</param>
    /// <param name="client">Whose data: the client's own container, and the only one this reaches.</param>
    public AzureBlobBackend(StorageClients storage, ClientName client)
        : this(storage, client.Value)
    {
    }

    // For the shared settings container, which is no client's. Internal so that outside this
    // assembly a container can only be named through a ClientName.
    internal AzureBlobBackend(StorageClients storage, string container) =>
        _container = storage.Blobs.GetBlobContainerClient(container);

    public async IAsyncEnumerable<BlobEntry> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _container.GetBlobsAsync(
                           BlobTraits.Metadata, BlobStates.None, prefix, cancellationToken))
        {
            yield return new BlobEntry(
                item.Name, new Dictionary<string, string>(item.Metadata), item.Properties.ETag?.ToString() ?? "");
        }
    }

    // Never overwrite: a save is a new blob, and an existing one is left exactly as it was. The
    // create-only upload is Janet.Azure.Storage's, shared with the other apps.
    public Task<bool> TryCreateAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        _container.TryCreateAsync(name, content, contentType, metadata, cancellationToken);

    // The working copy's write, and the only overwrite (IBlobBackend.ReplaceAsync): If-Match on the version
    // the page last wrote, or If-None-Match: * for one it has not. A blob changed since fails the condition
    // (412), and so does one written meanwhile where none was (409) or removed meanwhile (404).
    public async Task<string?> ReplaceAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata, BlobExpected expected,
        CancellationToken cancellationToken = default)
    {
        var conditions = expected.Absent ? new BlobRequestConditions { IfNoneMatch = ETag.All }
            : expected.ETag is { } etag ? new BlobRequestConditions { IfMatch = new ETag(etag) }
            : null;
        try
        {
            var written = await _container.GetBlobClient(name).UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Metadata = new Dictionary<string, string>(metadata),
                Conditions = conditions,
            }, cancellationToken);
            return written.Value.ETag.ToString();
        }
        catch (RequestFailedException ex) when (conditions is not null && ex.Status is 404 or 409 or 412)
        {
            return null;
        }
    }

    public Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        _container.ReadContentAsync(name, cancellationToken);

    // Metadata only: the content, and its create-only guarantee, are untouched.
    public async Task<bool> SetMetadataAsync(
        string name, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            await _container.GetBlobClient(name).SetMetadataAsync(
                new Dictionary<string, string>(metadata), cancellationToken: cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    // If-Match on the listed ETag, as for DeleteIfUnchangedAsync: a blob changed since is left alone.
    public async Task<bool> SetMetadataIfUnchangedAsync(
        string name, IReadOnlyDictionary<string, string> metadata, string etag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(etag);
        try
        {
            await _container.GetBlobClient(name).SetMetadataAsync(
                new Dictionary<string, string>(metadata), new BlobRequestConditions { IfMatch = new ETag(etag) }, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await _container.GetBlobClient(name)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        return result.Value;
    }

    // If-Match on the listed ETag: a blob changed since, such as a save restored in the meantime,
    // fails the precondition (412) and is left alone.
    public async Task<bool> DeleteIfUnchangedAsync(string name, string etag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(etag);
        try
        {
            var result = await _container.GetBlobClient(name).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots, new BlobRequestConditions { IfMatch = new ETag(etag) },
                cancellationToken);
            return result.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return false;
        }
    }
}
