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
/// <c>settings/_standard-baseline/</c> and <c>settings/_standard-ladders/</c>, and the record of the
/// app's own starts at <c>settings/_starts/</c>.
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
    private readonly CampaignSignals? _signals;

    /// <param name="storage">The app's storage clients, built once at startup.</param>
    /// <param name="undoGracePeriod">How long an undone draft or template can be restored (<see cref="UndoOptions"/>).</param>
    /// <param name="approvals">The approvals table.</param>
    /// <param name="activity">Writes the activity trail; a failed write never fails an action.</param>
    /// <param name="signals">
    /// The app's in-process signal of changes, for the open pages: the editing sessions raise it for
    /// campaigns and templates, and this raises it for her look, baseline and ladders; null where none listen.
    /// </param>
    public ClientStores(
        StorageClients storage, TimeProvider clock, TimeSpan undoGracePeriod, IApprovalStore approvals, ActivityRecorder activity,
        CampaignSignals? signals = null)
        : this(container => new AzureBlobBackend(storage, container), clock, undoGracePeriod, approvals, activity, signals)
    {
    }

    // Any backend per container name, so the layout, and the pages above it, are tested without
    // Azure. Internal: outside this assembly a client's container is reached only through Azure.
    internal ClientStores(
        Func<string, IBlobBackend> container, TimeProvider clock, TimeSpan undoGracePeriod, IApprovalStore approvals,
        ActivityRecorder activity, CampaignSignals? signals = null)
    {
        _container = container;
        _clock = clock;
        _undoGracePeriod = undoGracePeriod;
        _approvals = approvals;
        _activity = activity;
        _signals = signals;
    }

    /// <summary>
    /// The client's drafts and templates, with their approvals; what <paramref name="actor"/> does
    /// through it goes on the client's activity trail, and the editing sessions over it signal their
    /// changes to the other pages open on this instance.
    /// </summary>
    public CampaignStore Campaigns(ClientName client, Actor actor) =>
        new(_container(client.Value), _clock, _undoGracePeriod, _approvals, _activity.For(client, actor), _signals);

    /// <summary>
    /// What the AI proofread read in the client's photos, by the hash of each image's bytes, in her own
    /// container (<c>proofread/images/</c>), so an unchanged photo is not sent again.
    /// </summary>
    public IPhotoReadings PhotoReadings(ClientName client) => new PhotoReadingStore(_container(client.Value));

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

    /// <summary>How the site looks for the client's people. With nothing saved, the standard look.</summary>
    public DocumentStore<ClientLook> Look(ClientName client) => LookIn(_container(SettingsContainer), client, _clock);

    /// <summary>
    /// Saves the client's own look, on its activity trail. A look whose text is too faint to read on it
    /// is refused, naming each pair of colours below WCAG AA: a look is chosen for how it feels, and
    /// must never make the checks or the email hard to read.
    /// </summary>
    /// <exception cref="ArgumentException">Some text would be below 4.5:1 on its background; the message says which.</exception>
    public Task<DocumentVersion> SaveLookAsync(ClientName client, ClientLook look, Actor actor, CancellationToken ct = default) =>
        SaveLookAsync(client, look, actor, ActivityAction.LookSaved, ct);

    /// <summary>
    /// Saves an earlier look of the client's again, as a new version in force: the earlier version is
    /// left as it was, and stays listed. Checked as any save is, so a look too faint to read is refused.
    /// </summary>
    /// <exception cref="ArgumentException">The look is too faint to read, or is not one of the client's.</exception>
    /// <exception cref="InvalidDataException">The earlier version cannot be read as a look.</exception>
    public async Task<DocumentVersion> UseLookAgainAsync(
        ClientName client, DocumentVersion earlier, Actor actor, CancellationToken ct = default) =>
        await SaveLookAsync(client, await Look(client).LoadAsync(earlier, ct), actor, ActivityAction.LookUsedAgain, ct);

    /// <summary>
    /// Puts the client's pages back in the standard look by saving a version that says so
    /// (<see cref="ClientLook.Standard"/>). Her earlier looks are kept, to be used again; the activity
    /// trail says it was reset, and when.
    /// </summary>
    public Task<DocumentVersion> UseStandardLookAsync(ClientName client, Actor actor, CancellationToken ct = default) =>
        SaveLookAsync(client, ClientLook.Standard, actor, ActivityAction.LookResetToStandard, ct);

    private async Task<DocumentVersion> SaveLookAsync(
        ClientName client, ClientLook look, Actor actor, ActivityAction action, CancellationToken ct)
    {
        var problems = look.Palette.ContrastProblems();
        if (problems.Count > 0)
            throw new ArgumentException("This look is hard to read. " + string.Join(" ", problems), nameof(look));
        var version = await Look(client).SaveAsync(look, ct);
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Look, LookEntityId, SaveStamp.Of(version.SavedAt), action, ct);
        // The Look page loads again after each change, so every open one is told, its own included.
        _signals?.Raise(client, WatchSubject.Look, source: null);
        return version;
    }

    /// <summary>The client's look as an open page holds it: <paramref name="held"/>, the newest version it showed (null for none).</summary>
    public HeldDocument HoldLook(ClientName client, DocumentVersion? held) =>
        new(client, WatchSubject.Look, held, ct => Look(client).NewestAsync(ct), _signals);

    /// <summary>The client's look as it stands in storage now, held as a page would hold it.</summary>
    public async Task<HeldDocument> HoldLookAsync(ClientName client, CancellationToken ct = default) =>
        HoldLook(client, await Look(client).NewestAsync(ct));

    /// <summary>How many of her saved looks the Look page lists, newest first.</summary>
    public const int EarlierLooksListed = 20;

    /// <summary>
    /// The client's saved looks for her Look page: the newest <see cref="EarlierLooksListed"/> in use,
    /// newest first, each read on its own so one that cannot be read is listed as such and never stops
    /// the others; and the undone version Restore would bring back, while it can.
    /// </summary>
    public async Task<LookHistory> LookHistoryAsync(ClientName client, CancellationToken ct = default)
    {
        var look = Look(client);
        var listed = (await look.HistoryAsync(ct)).Take(EarlierLooksListed).ToList();
        var looks = await Task.WhenAll(listed.Select(v => ReadableAsync(look, v, ct)));
        var versions = listed.Select((v, i) => new LookVersion(v, looks[i], InUse: i == 0 && looks[i] is not null)).ToList();
        var restorable = await look.RestorableAsync(_undoGracePeriod, ct);
        return new LookHistory(versions, restorable, restorable?.UndoneAt + _undoGracePeriod, _undoGracePeriod);
    }

    // A look whose document cannot be read is listed as one that cannot be used; an outage still throws.
    private static async Task<ClientLook?> ReadableAsync(DocumentStore<ClientLook> look, DocumentVersion version, CancellationToken ct)
    {
        try
        {
            return await look.LoadAsync(version, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes the look the page holds as the editors undo a save: the version is marked undone, the one
    /// before is in force at once (or the standard look, when there is none), and it can be restored for
    /// the grace period before the sweep deletes it. Only while it is still her newest look, read again
    /// at the click (2026-10-11): undo used to take back whatever was newest, even a look saved in
    /// another tab that this page never showed. Null when the page held no look and storage has none.
    /// </summary>
    /// <exception cref="CampaignChangedException">Her look changed since the page read it; nothing is undone.</exception>
    public async Task<DocumentVersion?> UndoLastLookSaveAsync(HeldDocument held, Actor actor, CancellationToken ct = default)
    {
        if (held.Subject != WatchSubject.Look) throw new ArgumentException("That is not a look.", nameof(held));
        if (held.Held is not { } version)
        {
            if (await held.ChangeNowAsync(ct) is var nothing and not CampaignChange.None)
                throw new CampaignChangedException(nothing, HeldDocument.NotUndone(held.Subject, nothing));
            return null;
        }
        var undo = await Look(held.Client).UndoIfNewestAsync(version, ct);
        if (!undo.Done)
        {
            var change = held.ChangeSince(undo.Newest);
            throw new CampaignChangedException(change, HeldDocument.NotUndone(held.Subject, change));
        }
        await _activity.For(held.Client, actor).RecordAsync(
            ActivityEntity.Look, LookEntityId, SaveStamp.Of(version.SavedAt), ActivityAction.Undone, ct);
        held.Changed(undo.Newest);
        return undo.Undone;
    }

    /// <summary>
    /// Takes back the undo of <paramref name="version"/>, so it is in force again. False when it is gone,
    /// not undone, or its grace period has passed.
    /// </summary>
    public async Task<bool> RestoreLookAsync(ClientName client, DocumentVersion version, Actor actor, CancellationToken ct = default)
    {
        if (!await Look(client).RestoreAsync(version, _undoGracePeriod, ct)) return false;
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Look, LookEntityId, SaveStamp.Of(version.SavedAt), ActivityAction.Restored, ct);
        _signals?.Raise(client, WatchSubject.Look, source: null);
        return true;
    }

    /// <summary>
    /// Deletes for good the client's look versions undone longer ago than the grace period, each on its
    /// trail by stamp only, against <see cref="Actor.UndoSweep"/>. Returns how many it deleted.
    /// </summary>
    public async Task<int> SweepLookAsync(ClientName client, CancellationToken ct = default)
    {
        var deleted = await Look(client).SweepAsync(_undoGracePeriod, ct);
        foreach (var version in deleted)
        {
            await _activity.For(client, Actor.UndoSweep).RecordAsync(
                ActivityEntity.Look, LookEntityId, SaveStamp.Of(version.SavedAt), ActivityAction.DeletedBySweep, ct);
        }
        return deleted.Count;
    }

    /// <summary>A client has one look, named by this in its events.</summary>
    internal const string LookEntityId = "look";

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

    // Her newest version is the one in force, unless it says the standard applies
    // (TemplateBaseline.FollowStandard); with it, the version, so a page can hold what it showed.
    internal static async Task<BaselineInForce> BaselineInForceAsync(
        DocumentStore<TemplateBaseline> own, DocumentStore<TemplateBaseline> standard, CancellationToken ct)
    {
        var newest = await own.NewestAsync(ct);
        return newest is not null && await own.LoadAsync(newest, ct) is { FollowsStandard: false } mine
            ? new BaselineInForce(mine, IsOwn: true, newest)
            : new BaselineInForce(await standard.CurrentAsync(ct) ?? TemplateBaseline.Standard, IsOwn: false, newest);
    }

    /// <summary>The client's baseline as an open page holds it: <paramref name="held"/>, her newest version it showed (null for none).</summary>
    public HeldDocument HoldBaseline(ClientName client, DocumentVersion? held) =>
        new(client, WatchSubject.Baseline, held, ct => Baseline(client).NewestAsync(ct), _signals);

    /// <summary>Saves the client's own baseline, which replaces the standard one for it, on its activity trail.</summary>
    public async Task<DocumentVersion> SaveBaselineAsync(
        ClientName client, TemplateBaseline baseline, Actor actor, CancellationToken ct = default)
    {
        var version = await KeepBaselineAsync(client, baseline, actor, ct);
        _signals?.Raise(client, WatchSubject.Baseline, source: null);
        return version;
    }

    /// <summary>
    /// Saves the client's own baseline from an open page, only while her newest version is still the one
    /// the page held, read again at the click (2026-10-11), unless <paramref name="keepMine"/>: she chose to
    /// save hers over the other. Nothing is deleted either way; the other version stays stored.
    /// </summary>
    /// <exception cref="CampaignChangedException">Her baseline changed since the page read it; nothing is saved.</exception>
    public Task<DocumentVersion> SaveBaselineAsync(
        HeldDocument held, TemplateBaseline baseline, Actor actor, bool keepMine = false, CancellationToken ct = default) =>
        SaveHeldAsync(held, WatchSubject.Baseline, keepMine, () => KeepBaselineAsync(held.Client, baseline, actor, ct), ct);

    /// <summary>
    /// Makes the standard baseline apply to the client again by saving a version that says so
    /// (<see cref="TemplateBaseline.FollowStandard"/>), as the look's standard is a version (2026-10-11).
    /// It used to delete every version of hers, a version saved in another tab included, which she had
    /// never seen; now nothing is deleted, and her earlier versions stay stored. The activity trail says it
    /// was reset, by the new version's stamp.
    /// </summary>
    public async Task<DocumentVersion> UseStandardBaselineAsync(ClientName client, Actor actor, CancellationToken ct = default)
    {
        var version = await KeepBaselineAsync(client, TemplateBaseline.FollowStandard, actor, ct);
        _signals?.Raise(client, WatchSubject.Baseline, source: null);
        return version;
    }

    /// <summary>
    /// <see cref="UseStandardBaselineAsync(ClientName, Actor, CancellationToken)"/> from an open page, only
    /// while her newest version is still the one the page held, unless <paramref name="keepMine"/>.
    /// </summary>
    /// <exception cref="CampaignChangedException">Her baseline changed since the page read it; nothing is saved.</exception>
    public Task<DocumentVersion> UseStandardBaselineAsync(
        HeldDocument held, Actor actor, bool keepMine = false, CancellationToken ct = default) =>
        SaveHeldAsync(held, WatchSubject.Baseline, keepMine,
            () => KeepBaselineAsync(held.Client, TemplateBaseline.FollowStandard, actor, ct), ct);

    // A new version of her baseline, on the trail as saved or as reset to the standard; nobody told yet.
    private async Task<DocumentVersion> KeepBaselineAsync(ClientName client, TemplateBaseline baseline, Actor actor, CancellationToken ct)
    {
        var version = await Baseline(client).SaveAsync(baseline, ct);
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Baseline, BaselineEntityId, SaveStamp.Of(version.SavedAt),
            baseline.FollowsStandard ? ActivityAction.BaselineResetToStandard : ActivityAction.BaselineSaved, ct);
        return version;
    }

    // A save from an open page: refused when what it held is no longer the newest, unless she keeps hers;
    // then the page holds the new version, and the other pages are told.
    private static async Task<DocumentVersion> SaveHeldAsync(
        HeldDocument held, string subject, bool keepMine, Func<Task<DocumentVersion>> save, CancellationToken ct)
    {
        if (held.Subject != subject) throw new ArgumentException($"That is not the client's {subject}.", nameof(held));
        if (!keepMine && await held.ChangeNowAsync(ct) is var change and not CampaignChange.None)
            throw new CampaignChangedException(change, HeldDocument.NotSaved(held.Subject, change));
        var version = await save();
        held.Changed(version);
        return version;
    }

    /// <summary>A client has one baseline of its own, so its events name it by this rather than by an id.</summary>
    internal const string BaselineEntityId = "baseline";

    /// <summary>The client's own tier-name ladders, which replace the standard ones for that client.</summary>
    public DocumentStore<TierLadders> Ladders(ClientName client) => LaddersIn(_container(client.Value), _clock);

    /// <summary>The operator's standard tier-name ladders, for every client without its own. Operator settings, not client data.</summary>
    public DocumentStore<TierLadders> StandardLadders() => StandardLaddersIn(_container(SettingsContainer), _clock);

    /// <summary>
    /// The record of the app's recent starts, at <c>settings/_starts/</c>: operator data, beside the
    /// standard baseline and ladders, and no client's.
    /// </summary>
    public IStartRecords Starts(int keep = StartRecordStore.DefaultKeep) => new StartRecordStore(_container(SettingsContainer), keep);

    /// <summary>
    /// The tier-name ladders the checks read this client's tier names with: its own once it has saved
    /// a set (an empty one included), else the operator's standard, else the ones the tool starts with
    /// (<see cref="TierLadders.Standard"/>), as for the template baseline.
    /// </summary>
    public Task<LaddersInForce> LaddersInForceAsync(ClientName client, CancellationToken ct = default) =>
        LaddersInForceAsync(Ladders(client), StandardLadders(), ct);

    // Her newest version is the one in force, unless it says the standard applies
    // (TierLadders.FollowStandard); with it, the version, so a page can hold what it showed.
    internal static async Task<LaddersInForce> LaddersInForceAsync(
        DocumentStore<TierLadders> own, DocumentStore<TierLadders> standard, CancellationToken ct)
    {
        var newest = await own.NewestAsync(ct);
        return newest is not null && await own.LoadAsync(newest, ct) is { FollowsStandard: false } mine
            ? new LaddersInForce(mine, IsOwn: true, newest)
            : new LaddersInForce(await standard.CurrentAsync(ct) ?? TierLadders.Standard, IsOwn: false, newest);
    }

    /// <summary>The client's ladders as an open page holds them: <paramref name="held"/>, her newest version it showed (null for none).</summary>
    public HeldDocument HoldLadders(ClientName client, DocumentVersion? held) =>
        new(client, WatchSubject.Ladders, held, ct => Ladders(client).NewestAsync(ct), _signals);

    /// <summary>Saves the client's own ladders, which replace the standard ones for it, on its activity trail.</summary>
    public async Task<DocumentVersion> SaveLaddersAsync(
        ClientName client, TierLadders ladders, Actor actor, CancellationToken ct = default)
    {
        var version = await KeepLaddersAsync(client, ladders, actor, ct);
        _signals?.Raise(client, WatchSubject.Ladders, source: null);
        return version;
    }

    /// <summary>
    /// Saves the client's own ladders from an open page, only while her newest version is still the one
    /// the page held, read again at the click (2026-10-11), unless <paramref name="keepMine"/>.
    /// </summary>
    /// <exception cref="CampaignChangedException">Her ladders changed since the page read them; nothing is saved.</exception>
    public Task<DocumentVersion> SaveLaddersAsync(
        HeldDocument held, TierLadders ladders, Actor actor, bool keepMine = false, CancellationToken ct = default) =>
        SaveHeldAsync(held, WatchSubject.Ladders, keepMine, () => KeepLaddersAsync(held.Client, ladders, actor, ct), ct);

    /// <summary>
    /// Makes the standard ladders apply to the client again by saving a version that says so
    /// (<see cref="TierLadders.FollowStandard"/>), as for the baseline (2026-10-11): it used to delete every
    /// version of hers, one she never saw included. Nothing is deleted now; the trail says they were reset.
    /// </summary>
    public async Task<DocumentVersion> UseStandardLaddersAsync(ClientName client, Actor actor, CancellationToken ct = default)
    {
        var version = await KeepLaddersAsync(client, TierLadders.FollowStandard, actor, ct);
        _signals?.Raise(client, WatchSubject.Ladders, source: null);
        return version;
    }

    /// <summary>
    /// <see cref="UseStandardLaddersAsync(ClientName, Actor, CancellationToken)"/> from an open page, only
    /// while her newest version is still the one the page held, unless <paramref name="keepMine"/>.
    /// </summary>
    /// <exception cref="CampaignChangedException">Her ladders changed since the page read them; nothing is saved.</exception>
    public Task<DocumentVersion> UseStandardLaddersAsync(
        HeldDocument held, Actor actor, bool keepMine = false, CancellationToken ct = default) =>
        SaveHeldAsync(held, WatchSubject.Ladders, keepMine,
            () => KeepLaddersAsync(held.Client, TierLadders.FollowStandard, actor, ct), ct);

    // A new version of her ladders, on the trail as saved or as reset to the standard; nobody told yet.
    private async Task<DocumentVersion> KeepLaddersAsync(ClientName client, TierLadders ladders, Actor actor, CancellationToken ct)
    {
        var version = await Ladders(client).SaveAsync(ladders, ct);
        await _activity.For(client, actor).RecordAsync(
            ActivityEntity.Ladders, LaddersEntityId, SaveStamp.Of(version.SavedAt),
            ladders.FollowsStandard ? ActivityAction.LaddersResetToStandard : ActivityAction.LaddersSaved, ct);
        return version;
    }

    /// <summary>A client has one set of ladders of its own, named by this in its events.</summary>
    internal const string LaddersEntityId = "ladders";

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
/// <param name="Version">
/// Her newest version in use, read with it, which a page holds to tell a change since; null when she has
/// never saved one. Also set while the standard applies, when that version is the one saying so.
/// </param>
public sealed record BaselineInForce(TemplateBaseline Baseline, bool IsOwn, DocumentVersion? Version = null);

/// <summary>The tier-name ladders in force for a client, and whether they are the client's own or the standard ones.</summary>
/// <param name="Version">Her newest version in use, read with them; null when she has never saved any.</param>
public sealed record LaddersInForce(TierLadders Ladders, bool IsOwn, DocumentVersion? Version = null);

/// <summary>One of a client's saved looks, as her Look page lists it.</summary>
/// <param name="Look">The look, or null when its document cannot be read: listed, but never used.</param>
/// <param name="InUse">The newest, and readable: the look her pages are drawn in.</param>
public sealed record LookVersion(DocumentVersion Version, ClientLook? Look, bool InUse)
{
    /// <summary>The version's stamp, which names it in storage and on the activity trail.</summary>
    public string Id => SaveStamp.Of(Version.SavedAt);
}

/// <summary>A client's saved looks, newest first, and the undone one Restore would bring back.</summary>
/// <param name="Versions">At most <see cref="ClientStores.EarlierLooksListed"/>, newest first.</param>
/// <param name="Restorable">The last version undone, while it can be restored.</param>
/// <param name="RestorableUntil">When <paramref name="Restorable"/> goes for good.</param>
/// <param name="UndoGracePeriod">How long an undone look can be restored.</param>
public sealed record LookHistory(
    IReadOnlyList<LookVersion> Versions, DocumentVersion? Restorable, DateTimeOffset? RestorableUntil, TimeSpan UndoGracePeriod)
{
    /// <summary>
    /// The look her pages are drawn in, or null for the standard one: with nothing saved, or when the
    /// newest cannot be read (as the pages then fall back to the standard look).
    /// </summary>
    public ClientLook? InForce => Versions.FirstOrDefault(v => v.InUse)?.Look;

    /// <summary>Whether her pages have a look of her own, rather than the standard one.</summary>
    public bool IsOwn => InForce is { IsStandard: false };
}
