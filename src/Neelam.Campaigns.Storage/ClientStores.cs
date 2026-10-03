using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Opens a client's own storage: one deployment serves every client, so a store is opened per
/// request for the client that request is working in. Whether the signed-in user may reach that
/// client is decided before this is called, by the access check; this only turns a
/// <see cref="ClientName"/> into that client's container.
/// </summary>
public sealed class ClientStores(StorageClients storage, TimeProvider clock)
{
    /// <summary>The client's drafts and templates.</summary>
    public CampaignStore Campaigns(ClientName client) => new(new AzureBlobBackend(storage, client), clock);
}
