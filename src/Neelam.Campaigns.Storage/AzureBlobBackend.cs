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
            yield return new BlobEntry(item.Name, new Dictionary<string, string>(item.Metadata));
        }
    }

    public async Task<bool> TryCreateAsync(
        string name, string content, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _container.GetBlobClient(name).UploadAsync(BinaryData.FromString(content), new BlobUploadOptions
            {
                // Never overwrite: a save is a new blob, and an existing one is left exactly as it was.
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" },
            }, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public async Task<string> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await _container.GetBlobClient(name).DownloadContentAsync(cancellationToken);
        return result.Value.Content.ToString();
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await _container.GetBlobClient(name)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        return result.Value;
    }
}
