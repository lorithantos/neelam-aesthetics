using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Opens a client's own storage: one deployment serves every client, so a store is opened per
/// request for the client that request is working in. Whether the signed-in user may reach that
/// client is decided before this is called, by the access check; this only turns a
/// <see cref="ClientName"/> into where that client's data lives.
/// </summary>
/// <remarks>
/// The client's container holds its drafts and templates, its catalog (<c>catalog/</c>) and its
/// check policy (<c>policy/</c>): all the client's own data. Its look lives apart, in the
/// <c>settings</c> container at <c>settings/{client}/</c>, so working on a look never needs access
/// to the client's data.
/// </remarks>
public sealed class ClientStores(StorageClients storage, TimeProvider clock)
{
    /// <summary>The container holding every client's look; no client may take this name.</summary>
    internal const string SettingsContainer = "settings";

    internal const string CatalogPrefix = "catalog";
    internal const string PolicyPrefix = "policy";

    /// <summary>The client's drafts and templates.</summary>
    public CampaignStore Campaigns(ClientName client) => new(new AzureBlobBackend(storage, client), clock);

    /// <summary>The procedures and medications the client offers.</summary>
    public DocumentStore<ClientCatalog> Catalog(ClientName client) =>
        CatalogIn(new AzureBlobBackend(storage, client), clock);

    /// <summary>The client's own check policy: restricted terms, medical terms, emoji limit.</summary>
    public DocumentStore<CampaignPolicy> Policy(ClientName client) =>
        PolicyIn(new AzureBlobBackend(storage, client), clock);

    /// <summary>How the site looks for the client's people.</summary>
    public DocumentStore<ClientLook> Look(ClientName client) =>
        LookIn(new AzureBlobBackend(storage, SettingsContainer), client, clock);

    /// <summary>
    /// The policy the checks run with for this client: its own once it has saved one, the starting
    /// defaults until then. Pass it to <see cref="CampaignReview.Check"/> or
    /// <see cref="CampaignGate.ReviewAsync"/>.
    /// </summary>
    public Task<CampaignPolicy> PolicyInForceAsync(ClientName client, CancellationToken ct = default) =>
        PolicyInForceAsync(Policy(client), ct);

    internal static async Task<CampaignPolicy> PolicyInForceAsync(DocumentStore<CampaignPolicy> policy, CancellationToken ct) =>
        await policy.CurrentAsync(ct) ?? CampaignPolicy.Default;

    // Built over any backend so the layout is tested without Azure.
    internal static DocumentStore<ClientCatalog> CatalogIn(IBlobBackend clientContainer, TimeProvider clock) =>
        new(clientContainer, CatalogPrefix, clock, CampaignJson.SerializeCatalog, CampaignJson.DeserializeCatalog);

    internal static DocumentStore<CampaignPolicy> PolicyIn(IBlobBackend clientContainer, TimeProvider clock) =>
        new(clientContainer, PolicyPrefix, clock, CampaignJson.SerializePolicy, CampaignJson.DeserializePolicy);

    internal static DocumentStore<ClientLook> LookIn(IBlobBackend settingsContainer, ClientName client, TimeProvider clock) =>
        new(settingsContainer, client.Value, clock, CampaignJson.SerializeLook, CampaignJson.DeserializeLook);
}
