namespace Neelam.Campaigns.Storage;

/// <summary>
/// One of the client's own documents as an open page holds it: her look, her template baseline or her
/// tier-name ladders (2026-10-11, phase 2 of the owner's "concurrent usage by two users"). It keeps the
/// version the page was opened on or last changed, so the page's look at storage can say when another
/// tab or person changed it, and <see cref="ClientStores"/> can refuse, at the click, an action that
/// would undo or replace a version the page never showed. Get one from <see cref="ClientStores"/>
/// (<c>HoldLookAsync</c>, <c>HoldBaselineAsync</c>, <c>HoldLaddersAsync</c>); one per page, on its circuit.
/// </summary>
public sealed class HeldDocument : IWatched<DocumentVersion>
{
    private readonly Func<CancellationToken, Task<DocumentVersion?>> _newest;
    private readonly CampaignSignals? _signals;

    internal HeldDocument(
        ClientName client, string subject, DocumentVersion? held, Func<CancellationToken, Task<DocumentVersion?>> newest,
        CampaignSignals? signals)
    {
        Client = client;
        Subject = subject;
        Held = held;
        _newest = newest;
        _signals = signals;
    }

    public ClientName Client { get; }

    /// <summary>Which of her documents: <see cref="WatchSubject.Look"/>, <see cref="WatchSubject.Baseline"/> or <see cref="WatchSubject.Ladders"/>.</summary>
    public string Subject { get; }

    /// <summary>The newest version in use when the page read it, or null when she had none of her own.</summary>
    public DocumentVersion? Held { get; private set; }

    /// <summary>The newest version in use as storage has it now; one listing.</summary>
    public Task<DocumentVersion?> NewestAsync(CancellationToken ct = default) => _newest(ct);

    /// <summary>
    /// What changed since the version the page holds: a newer version saved (also her first, when the page
    /// showed none), or the page's version undone with an earlier one, or none, in use.
    /// </summary>
    public CampaignChange ChangeSince(DocumentVersion? newest)
    {
        if (newest?.BlobName == Held?.BlobName) return CampaignChange.None;
        if (Held is null || (newest is not null && newest.SavedAt > Held.SavedAt)) return CampaignChange.SavedSince;
        return CampaignChange.Undone;
    }

    /// <summary>What changed in storage now, read at the click.</summary>
    internal async Task<CampaignChange> ChangeNowAsync(CancellationToken ct) => ChangeSince(await NewestAsync(ct));

    /// <summary>The page's own change is done: it now holds <paramref name="version"/>, and the other pages are told.</summary>
    internal void Changed(DocumentVersion? version)
    {
        Held = version;
        _signals?.Raise(Client, Subject, this);
    }

    /// <summary>What a page says at the top of its notice (<see cref="ChangeWords.For"/>).</summary>
    public static string ChangedNotice(string subject, CampaignChange change) => ChangeWords.For(subject).Notice(change);
}
