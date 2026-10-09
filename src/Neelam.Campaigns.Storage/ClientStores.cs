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
/// check policy (<c>policy/</c>) and its own template baseline (<c>baseline/</c>): all the client's
/// own data. Its look lives apart, in the <c>settings</c> container at <c>settings/{client}/</c>, so
/// working on a look never needs access to the client's data; the operator's standard baseline is
/// there too, at <c>settings/_standard-baseline/</c>.
/// </remarks>
public sealed class ClientStores
{
    /// <summary>The container holding every client's look; no client may take this name.</summary>
    internal const string SettingsContainer = "settings";

    internal const string CatalogPrefix = "catalog";
    internal const string PolicyPrefix = "policy";
    internal const string BaselinePrefix = "baseline";

    /// <summary>
    /// Where the operator's standard baseline lives in the <c>settings</c> container. No client
    /// name can start with an underscore, so it never meets a client's look there.
    /// </summary>
    internal const string StandardBaselinePrefix = "_standard-baseline";

    private readonly Func<string, IBlobBackend> _container;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _undoGracePeriod;

    /// <param name="storage">The app's storage clients, built once at startup.</param>
    /// <param name="undoGracePeriod">How long an undone draft or template can be restored (<see cref="UndoOptions"/>).</param>
    public ClientStores(StorageClients storage, TimeProvider clock, TimeSpan undoGracePeriod)
        : this(container => new AzureBlobBackend(storage, container), clock, undoGracePeriod)
    {
    }

    // Any backend per container name, so the layout, and the pages above it, are tested without
    // Azure. Internal: outside this assembly a client's container is reached only through Azure.
    internal ClientStores(Func<string, IBlobBackend> container, TimeProvider clock, TimeSpan undoGracePeriod)
    {
        _container = container;
        _clock = clock;
        _undoGracePeriod = undoGracePeriod;
    }

    /// <summary>The client's drafts and templates.</summary>
    public CampaignStore Campaigns(ClientName client) => new(_container(client.Value), _clock, _undoGracePeriod);

    /// <summary>The procedures and medications the client offers.</summary>
    public DocumentStore<ClientCatalog> Catalog(ClientName client) => CatalogIn(_container(client.Value), _clock);

    /// <summary>The client's own check policy: restricted terms, medical terms, emoji limit.</summary>
    public DocumentStore<CampaignPolicy> Policy(ClientName client) => PolicyIn(_container(client.Value), _clock);

    /// <summary>The client's photos, for blocks to choose from.</summary>
    public ImageLibrary Images(ClientName client) => new(_container(client.Value), _clock);

    /// <summary>How the site looks for the client's people.</summary>
    public DocumentStore<ClientLook> Look(ClientName client) => LookIn(_container(SettingsContainer), client, _clock);

    /// <summary>
    /// The policy the checks run with for this client: its own once it has saved one, the starting
    /// defaults until then. Pass it to <see cref="CampaignReview.Check"/> or
    /// <see cref="CampaignGate.ReviewAsync"/>.
    /// </summary>
    public Task<CampaignPolicy> PolicyInForceAsync(ClientName client, CancellationToken ct = default) =>
        PolicyInForceAsync(Policy(client), ct);

    internal static async Task<CampaignPolicy> PolicyInForceAsync(DocumentStore<CampaignPolicy> policy, CancellationToken ct) =>
        await policy.CurrentAsync(ct) ?? CampaignPolicy.Default;

    /// <summary>The client's own template baseline, which replaces the standard one for that client.</summary>
    public DocumentStore<TemplateBaseline> Baseline(ClientName client) => BaselineIn(_container(client.Value), _clock);

    /// <summary>The operator's standard baseline, for every client without its own. Operator settings, not client data.</summary>
    public DocumentStore<TemplateBaseline> StandardBaseline() => StandardBaselineIn(_container(SettingsContainer), _clock);

    /// <summary>
    /// The baseline the template editor warns with for this client: its own once it has saved one
    /// (an empty one included), else the operator's standard, else the standard the tool starts with.
    /// </summary>
    public Task<BaselineInForce> BaselineInForceAsync(ClientName client, CancellationToken ct = default) =>
        BaselineInForceAsync(Baseline(client), StandardBaseline(), ct);

    internal static async Task<BaselineInForce> BaselineInForceAsync(
        DocumentStore<TemplateBaseline> own, DocumentStore<TemplateBaseline> standard, CancellationToken ct) =>
        await own.CurrentAsync(ct) is { } mine
            ? new BaselineInForce(mine, IsOwn: true)
            : new BaselineInForce(await standard.CurrentAsync(ct) ?? TemplateBaseline.Standard, IsOwn: false);

    /// <summary>
    /// Deletes every version of the client's own baseline, so the standard one applies again. Like
    /// any deleted save, it leaves no record.
    /// </summary>
    public Task UseStandardBaselineAsync(ClientName client, CancellationToken ct = default) =>
        DeleteEveryVersionAsync(Baseline(client), ct);

    internal static async Task DeleteEveryVersionAsync<T>(DocumentStore<T> document, CancellationToken ct) where T : class
    {
        foreach (var version in await document.HistoryAsync(ct))
            await document.DeleteAsync(version, ct);
    }

    // Built over any backend so the layout is tested without Azure.
    internal static DocumentStore<ClientCatalog> CatalogIn(IBlobBackend clientContainer, TimeProvider clock) =>
        new(clientContainer, CatalogPrefix, clock, CampaignJson.SerializeCatalog, CampaignJson.DeserializeCatalog);

    internal static DocumentStore<CampaignPolicy> PolicyIn(IBlobBackend clientContainer, TimeProvider clock) =>
        new(clientContainer, PolicyPrefix, clock, CampaignJson.SerializePolicy, CampaignJson.DeserializePolicy);

    internal static DocumentStore<ClientLook> LookIn(IBlobBackend settingsContainer, ClientName client, TimeProvider clock) =>
        new(settingsContainer, client.Value, clock, CampaignJson.SerializeLook, CampaignJson.DeserializeLook);

    internal static DocumentStore<TemplateBaseline> BaselineIn(IBlobBackend clientContainer, TimeProvider clock) =>
        new(clientContainer, BaselinePrefix, clock, CampaignJson.SerializeBaseline, CampaignJson.DeserializeBaseline);

    internal static DocumentStore<TemplateBaseline> StandardBaselineIn(IBlobBackend settingsContainer, TimeProvider clock) =>
        new(settingsContainer, StandardBaselinePrefix, clock, CampaignJson.SerializeBaseline, CampaignJson.DeserializeBaseline);
}

/// <summary>The template baseline in force for a client, and whether it is the client's own or the standard one.</summary>
public sealed record BaselineInForce(TemplateBaseline Baseline, bool IsOwn);
