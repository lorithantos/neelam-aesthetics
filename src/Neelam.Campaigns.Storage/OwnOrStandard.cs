namespace Neelam.Campaigns.Storage;

/// <summary>
/// A document a client may keep of her own in place of the operator's standard: the template baseline
/// and the tier-name ladders (owner, 2026-10-09). Hers is in force once she saves one (an empty one
/// included), else the operator's saved standard, else the standard the tool starts with. Going back to
/// the standard saves a version saying so (2026-10-11), so none of hers is ever deleted. Each save from
/// an open page is made only while her newest version is still the one the page showed, unless she keeps
/// hers. One copy since 2026-11-03; <see cref="ClientStores"/> held a near-copy for each document.
/// </summary>
/// <typeparam name="T">The document: <see cref="TemplateBaseline"/> or <see cref="TierLadders"/>.</typeparam>
internal sealed class OwnOrStandard<T> where T : class
{
    private readonly OwnOrStandardKind<T> _kind;
    private readonly Func<ClientName, DocumentStore<T>> _own;
    private readonly Func<DocumentStore<T>> _standard;
    private readonly ActivityRecorder _activity;
    private readonly CampaignSignals? _signals;

    /// <param name="kind">What this document is called, on the trail and to the open pages, and its standards.</param>
    /// <param name="own">The client's own versions, in her container.</param>
    /// <param name="standard">The operator's standard versions, in the settings container.</param>
    /// <param name="activity">Writes the activity trail; a failed write never fails an action.</param>
    /// <param name="signals">The open pages on this instance; null where none listen.</param>
    public OwnOrStandard(
        OwnOrStandardKind<T> kind, Func<ClientName, DocumentStore<T>> own, Func<DocumentStore<T>> standard,
        ActivityRecorder activity, CampaignSignals? signals)
    {
        _kind = kind;
        _own = own;
        _standard = standard;
        _activity = activity;
        _signals = signals;
    }

    /// <summary>
    /// The document in force for <paramref name="client"/>, as <paramref name="inForce"/> names it: her
    /// newest version, unless it says the standard applies; with it, whether it is hers, and that newest
    /// version, so a page can hold what it showed.
    /// </summary>
    public async Task<TInForce> InForceAsync<TInForce>(
        ClientName client, Func<T, bool, DocumentVersion?, TInForce> inForce, CancellationToken ct)
    {
        var own = _own(client);
        var standard = _standard();
        var newest = await own.NewestAsync(ct);
        return newest is not null && await own.LoadAsync(newest, ct) is { } mine && !_kind.FollowsStandard(mine)
            ? inForce(mine, true, newest)
            : inForce(await standard.CurrentAsync(ct) ?? _kind.Standard, false, newest);
    }

    /// <summary>Her document as an open page holds it: <paramref name="held"/>, her newest version it showed (null for none).</summary>
    public HeldDocument Hold(ClientName client, DocumentVersion? held) =>
        new(client, _kind.Subject, held, ct => _own(client).NewestAsync(ct), _signals);

    /// <summary>Saves her own, which replaces the standard for her, on her trail; the open pages are told.</summary>
    public async Task<DocumentVersion> SaveAsync(ClientName client, T document, Actor actor, CancellationToken ct)
    {
        var version = await KeepAsync(client, document, actor, ct);
        _signals?.Raise(client, _kind.Subject, source: null);
        return version;
    }

    /// <summary>
    /// Saves her own from an open page, only while her newest version is still the one the page held, read
    /// again at the click, unless <paramref name="keepMine"/>: she chose to save hers over the other. Nothing
    /// is deleted either way; then the page holds the new version, and the other pages are told.
    /// </summary>
    /// <exception cref="CampaignChangedException">Her document changed since the page read it; nothing is saved.</exception>
    public async Task<DocumentVersion> SaveAsync(HeldDocument held, T document, Actor actor, bool keepMine, CancellationToken ct)
    {
        if (held.Subject != _kind.Subject) throw new ArgumentException($"That is not the client's {_kind.Subject}.", nameof(held));
        if (!keepMine && await held.ChangeNowAsync(ct) is var change and not CampaignChange.None)
            throw new CampaignChangedException(change, ChangeWords.For(held.Subject).NotSaved(change));
        var version = await KeepAsync(held.Client, document, actor, ct);
        held.Changed(version);
        return version;
    }

    /// <summary>Makes the standard apply to her again, by saving a version that says so.</summary>
    public Task<DocumentVersion> UseStandardAsync(ClientName client, Actor actor, CancellationToken ct) =>
        SaveAsync(client, _kind.FollowStandard, actor, ct);

    /// <summary>
    /// <see cref="UseStandardAsync(ClientName, Actor, CancellationToken)"/> from an open page, only while her
    /// newest version is still the one the page held, unless <paramref name="keepMine"/>.
    /// </summary>
    /// <exception cref="CampaignChangedException">Her document changed since the page read it; nothing is saved.</exception>
    public Task<DocumentVersion> UseStandardAsync(HeldDocument held, Actor actor, bool keepMine, CancellationToken ct) =>
        SaveAsync(held, _kind.FollowStandard, actor, keepMine, ct);

    // A new version of hers, on the trail as saved or as reset to the standard; nobody told yet.
    private async Task<DocumentVersion> KeepAsync(ClientName client, T document, Actor actor, CancellationToken ct)
    {
        var version = await _own(client).SaveAsync(document, ct);
        await _activity.For(client, actor).RecordAsync(
            _kind.Entity, _kind.EntityId, SaveStamp.Of(version.SavedAt),
            _kind.FollowsStandard(document) ? _kind.ResetToStandard : _kind.Saved, ct);
        return version;
    }
}

/// <summary>What sets one of a client's own-or-standard documents apart from the other.</summary>
/// <param name="Subject">What the open pages watch it as (<see cref="WatchSubject"/>), and what a wrong page is told it is not.</param>
/// <param name="Entity">What the activity trail files it under.</param>
/// <param name="EntityId">A client has one of it, named by this on the trail rather than by an id.</param>
/// <param name="Saved">On the trail when she saves her own.</param>
/// <param name="ResetToStandard">On the trail when she goes back to the standard.</param>
/// <param name="Standard">The standard the tool starts with, until the operator saves one.</param>
/// <param name="FollowStandard">The version saying the standard applies.</param>
/// <param name="FollowsStandard">Whether a version says the standard applies.</param>
internal sealed record OwnOrStandardKind<T>(
    string Subject, ActivityEntity Entity, string EntityId, ActivityAction Saved, ActivityAction ResetToStandard,
    T Standard, T FollowStandard, Func<T, bool> FollowsStandard);
