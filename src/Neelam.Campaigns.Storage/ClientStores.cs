using Azure.Core;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Opens a client's own storage: one deployment serves every client, so a store is opened per
/// request for the client that request is working in. Whether the signed-in user may reach that
/// client is decided before this is called, by the access check; this only turns a
/// <see cref="ClientName"/> into that client's container.
/// </summary>
public sealed class ClientStores
{
    private readonly Uri _blobServiceUri;
    private readonly TokenCredential _credential;
    private readonly TimeProvider _clock;

    public ClientStores(Uri blobServiceUri, TokenCredential credential, TimeProvider clock)
    {
        // Checked here, at startup, rather than on the first request that opens a store.
        AzureBlobBackend.EnsureServiceAddress(blobServiceUri);
        _blobServiceUri = blobServiceUri;
        _credential = credential;
        _clock = clock;
    }

    /// <summary>The client's drafts and templates.</summary>
    public CampaignStore Campaigns(ClientName client) =>
        new(new AzureBlobBackend(_blobServiceUri, client, _credential), _clock);
}
