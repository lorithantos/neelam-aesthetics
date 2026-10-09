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
/// check policy (<c>policy/</c>), its own template baseline (<c>baseline/</c>) and its own tier-name
/// ladders (<c>ladders/</c>): all the client's own data. Its look lives apart, in the <c>settings</c>
/// container at <c>settings/{client}/</c>, so working on a look never needs access to the client's
/// data; the operator's standard baseline and ladders are there too, at
/// <c>settings/_standard-baseline/</c> and <c>settings/_standard-ladders/</c>.
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

    internal const string LaddersPrefix = "ladders";

    /// <summary>Where the operator's standard tier-name ladders live in the <c>settings</c> container, beside the standard baseline.</summary>
    internal const string StandardLaddersPrefix = "_standard-ladders";

    private readonly Func<string, IBlobBackend> _container;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _undoGracePeriod;
    private readonly IApprovalStore _approvals;
    private readonly ActivityRecorder _activity;

    /// <param name="storage">The app's storage clients, built once at startup.</param>
    /// <param name="undoGracePeriod">How long an undone draft or template can be restored (<see cref="UndoOptions"/>).</param>
    /// <param name="approvals">The approvals table.</param>
    /// <param name="activity">Writes the activity trail; a failed write never fails an action.</param>
    public ClientStores(
        StorageClients storage, TimeProvider clock, TimeSpan undoGracePeriod, IApprovalStore approvals, ActivityRecorder activity)
        : this(container => new AzureBlobBackend(storage, container), clock, undoGracePeriod, approvals, activity)
    {
    }

    // Any backend per container name, so the layout, and the pages above it, are tested without
    // Azure. Internal: outside this assembly a client's container is reached only through Azure.
    internal ClientStores(
        Func<string, IBlobBackend> container, TimeProvider clock, TimeSpan undoGracePeriod, IApprovalStore approvals,
        ActivityRecorder activity)
    {
        _container = container;
        _clock = clock;
        _undoGracePeriod = undoGracePeriod;
        _approvals = approvals;
        _activity = activity;
    }

    /// <summary>
    /// The client's drafts and templates, with their approvals; what <paramref name="actor"/> does
    /// through it goes on the client's activity trail.
    /// </summary>
    public CampaignStore Campaigns(ClientName client, Actor actor) =>
        new(_container(client.Value), _clock, _undoGracePeriod, _approvals, _activity.For(client, actor));

    /// <summary>The procedures and medications the client offers.</summary>
    public DocumentStore<ClientCatalog> Catalog(ClientName client) => CatalogIn(_container(client.Value), _clock);

    /// <summary>The client's own check policy: restricted terms, medical terms, emoji limit.</summary>
    public DocumentStore<CampaignPolicy> Policy(ClientName client) => PolicyIn(_container(client.Value), _clock);

    /// <summary>
    /// The client's photos, for blocks to choose from; adding and removing one goes on the client's
    /// activity trail against <paramref name="actor"/>.
    /// </summary>
    public ImageLibrary Images(ClientName client, Actor actor) =>
        new(_container(client.Value), _clock, _activity.For(client, actor));

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

    /// <summary>Saves the client's own baseline, which replaces the standard one for it, on its activity trail.</summary>
    public async Task<DocumentVersion> SaveBaselineAsync(
        ClientName client, TemplateBaseline baseline, Actor actor, CancellationToken ct = default)
    {
        var version = await Baseline(client).SaveAsync(baseline, ct);
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Baseline, BaselineEntityId, SaveStamp.Of(version.SavedAt), ActivityAction.BaselineSaved, ct);
        return version;
    }

    /// <summary>
    /// Deletes every version of the client's own baseline, so the standard one applies again. Like
    /// any deleted save, nothing of its contents remains; the activity trail says it was reset, and when.
    /// </summary>
    public async Task UseStandardBaselineAsync(ClientName client, Actor actor, CancellationToken ct = default)
    {
        await DeleteEveryVersionAsync(Baseline(client), ct);
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Baseline, BaselineEntityId, null, ActivityAction.BaselineResetToStandard, ct);
    }

    /// <summary>A client has one baseline of its own, so its events name it by this rather than by an id.</summary>
    internal const string BaselineEntityId = "baseline";

    /// <summary>The client's own tier-name ladders, which replace the standard ones for that client.</summary>
    public DocumentStore<TierLadders> Ladders(ClientName client) => LaddersIn(_container(client.Value), _clock);

    /// <summary>The operator's standard tier-name ladders, for every client without its own. Operator settings, not client data.</summary>
    public DocumentStore<TierLadders> StandardLadders() => StandardLaddersIn(_container(SettingsContainer), _clock);

    /// <summary>
    /// The tier-name ladders the checks read this client's tier names with: its own once it has saved
    /// a set (an empty one included), else the operator's standard, else the ones the tool starts with
    /// (<see cref="TierLadders.Standard"/>), as for the template baseline.
    /// </summary>
    public Task<LaddersInForce> LaddersInForceAsync(ClientName client, CancellationToken ct = default) =>
        LaddersInForceAsync(Ladders(client), StandardLadders(), ct);

    internal static async Task<LaddersInForce> LaddersInForceAsync(
        DocumentStore<TierLadders> own, DocumentStore<TierLadders> standard, CancellationToken ct) =>
        await own.CurrentAsync(ct) is { } mine
            ? new LaddersInForce(mine, IsOwn: true)
            : new LaddersInForce(await standard.CurrentAsync(ct) ?? TierLadders.Standard, IsOwn: false);

    /// <summary>Saves the client's own ladders, which replace the standard ones for it, on its activity trail.</summary>
    public async Task<DocumentVersion> SaveLaddersAsync(
        ClientName client, TierLadders ladders, Actor actor, CancellationToken ct = default)
    {
        var version = await Ladders(client).SaveAsync(ladders, ct);
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Ladders, LaddersEntityId, SaveStamp.Of(version.SavedAt), ActivityAction.LaddersSaved, ct);
        return version;
    }

    /// <summary>
    /// Deletes every version of the client's own ladders, so the standard ones apply again. Nothing of
    /// their contents remains; the activity trail says they were reset, and when.
    /// </summary>
    public async Task UseStandardLaddersAsync(ClientName client, Actor actor, CancellationToken ct = default)
    {
        await DeleteEveryVersionAsync(Ladders(client), ct);
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Ladders, LaddersEntityId, null, ActivityAction.LaddersResetToStandard, ct);
    }

    /// <summary>A client has one set of ladders of its own, named by this in its events.</summary>
    internal const string LaddersEntityId = "ladders";

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

    internal static DocumentStore<TierLadders> LaddersIn(IBlobBackend clientContainer, TimeProvider clock) =>
        new(clientContainer, LaddersPrefix, clock, CampaignJson.SerializeLadders, CampaignJson.DeserializeLadders);

    internal static DocumentStore<TierLadders> StandardLaddersIn(IBlobBackend settingsContainer, TimeProvider clock) =>
        new(settingsContainer, StandardLaddersPrefix, clock, CampaignJson.SerializeLadders, CampaignJson.DeserializeLadders);
}

/// <summary>The template baseline in force for a client, and whether it is the client's own or the standard one.</summary>
public sealed record BaselineInForce(TemplateBaseline Baseline, bool IsOwn);

/// <summary>The tier-name ladders in force for a client, and whether they are the client's own or the standard ones.</summary>
public sealed record LaddersInForce(TierLadders Ladders, bool IsOwn);
