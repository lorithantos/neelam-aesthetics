using System.Runtime.CompilerServices;
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

    // Never overwrite: a save is a new blob, and an existing one is left exactly as it was. The
    // create-only upload is Janet.Azure.Storage's, shared with the other apps.
    public Task<bool> TryCreateAsync(
        string name, BinaryData content, string contentType, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        _container.TryCreateAsync(name, content, contentType, metadata, cancellationToken);

    public Task<BlobContent> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        _container.ReadContentAsync(name, cancellationToken);

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await _container.GetBlobClient(name)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        return result.Value;
    }
}
