using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Azure Blob Storage, reached with an Entra ID token only. There is no constructor that takes a
/// connection string, account key or SAS: the storage account has shared-key access turned off,
/// so those would not work anyway, and this keeps them out of the code as well.
/// </summary>
public sealed class AzureBlobBackend : IBlobBackend
{
    private readonly BlobContainerClient _container;

    /// <param name="blobServiceUri">e.g. https://account.blob.core.windows.net/ — no query string.</param>
    /// <param name="credential">A managed identity in Azure; a developer's own sign-in locally.</param>
    public AzureBlobBackend(Uri blobServiceUri, string containerName, TokenCredential credential)
    {
        if (!blobServiceUri.IsAbsoluteUri || blobServiceUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The blob service address must be an absolute https:// URI.", nameof(blobServiceUri));
        if (!string.IsNullOrEmpty(blobServiceUri.Query))
            throw new ArgumentException(
                "The blob service address must not carry a query string; SAS tokens are not accepted.",
                nameof(blobServiceUri));

        _container = new BlobServiceClient(blobServiceUri, credential).GetBlobContainerClient(containerName);
    }

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
