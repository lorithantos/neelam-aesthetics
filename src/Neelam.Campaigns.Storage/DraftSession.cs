namespace Neelam.Campaigns.Storage;

/// <summary>
/// One campaign open in the editor, over a client's <see cref="CampaignStore"/>: what the campaign
/// page does, without the page. A campaign starts from the newest version of one of the client's
/// templates. A save is a new version and never replaces one. Undoing the last save puts the one
/// before it back and marks the undone version for deletion: for the store's grace period it can be
/// restored, here or after coming back to the campaign, and then the sweep deletes it, leaving no
/// record. Leaving the page deletes nothing.
/// </summary>
public sealed class DraftSession
{
    private readonly CampaignStore _store;

    private DraftSession(CampaignStore store, Guid? id, CampaignEditor editor, SaveRef? latest, SaveRef? restorable = null)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
        Restorable = restorable;
    }

    /// <summary>The campaign's id; null until a new campaign is first saved.</summary>
    public Guid? Id { get; private set; }

    public CampaignEditor Editor { get; private set; }

    /// <summary>The newest saved version, or null when there is none.</summary>
    public SaveRef? Latest { get; private set; }

    /// <summary>The undone version Restore would bring back, while its grace period lasts; otherwise null.</summary>
    public SaveRef? Restorable { get; private set; }

    /// <summary>Until when <see cref="Restorable"/> can be restored.</summary>
    public DateTimeOffset? RestorableUntil => Restorable is null ? null : _store.RestorableUntil(Restorable);

    /// <summary>The newest version of each of the client's campaigns, newest first.</summary>
    public static Task<IReadOnlyList<SaveRef>> ListAsync(CampaignStore store, CancellationToken ct = default) =>
        store.LatestAsync(DocumentKind.Draft, ct);

    /// <summary>A new, unsaved campaign from a template's newest version, or null when the template has no saves.</summary>
    public static async Task<DraftSession?> StartAsync(CampaignStore store, Guid templateId, CancellationToken ct = default)
    {
        var template = (await store.HistoryAsync(DocumentKind.Template, templateId, ct)).FirstOrDefault();
        return template is null
            ? null
            : new DraftSession(store, null, CampaignEditor.Start(await store.LoadTemplateAsync(template, ct)), null);
    }

    /// <summary>
    /// The newest version of a campaign, or null when it has none in use. A campaign whose every
    /// save was undone is null here; <see cref="RestorableAsync"/> says whether it can come back.
    /// </summary>
    public static async Task<DraftSession?> OpenAsync(CampaignStore store, Guid id, CancellationToken ct = default)
    {
        var latest = (await store.HistoryAsync(DocumentKind.Draft, id, ct)).FirstOrDefault();
        return latest is null
            ? null
            : new DraftSession(store, id, CampaignEditor.Open(await store.LoadDraftAsync(latest, ct)), latest,
                await store.RestorableAsync(DocumentKind.Draft, id, ct));
    }

    /// <summary>The undone version of a campaign Restore would bring back, or null.</summary>
    public static Task<SaveRef?> RestorableAsync(CampaignStore store, Guid id, CancellationToken ct = default) =>
        store.RestorableAsync(DocumentKind.Draft, id, ct);

    /// <summary>
    /// Restores an undone version of a campaign with nothing else in use and opens it; null when
    /// its grace period has passed or it is gone.
    /// </summary>
    public static async Task<DraftSession?> RestoreAsync(CampaignStore store, SaveRef undone, CancellationToken ct = default) =>
        await store.RestoreAsync(undone, ct) ? await OpenAsync(store, undone.Id, ct) : null;

    /// <summary>
    /// Saves the draft as it stands, finished or not, as a new version. A new campaign gets its id
    /// here.
    /// </summary>
    public async Task<SaveRef> SaveAsync(CancellationToken ct = default)
    {
        Id ??= Guid.NewGuid();
        Latest = await _store.SaveDraftAsync(Id.Value, Editor.Title, Editor.Draft, ct);
        // Saved over: an undone version below the new one is no longer offered back.
        Restorable = null;
        return Latest;
    }

    /// <summary>
    /// Undoes the newest version and opens the one before it, discarding any unsaved changes. The
    /// undone version is marked for deletion and stays restorable for the grace period. False when
    /// no version is left in use: the campaign is gone from the list, and <see cref="Restorable"/>
    /// still brings it back.
    /// </summary>
    public async Task<bool> UndoLastSaveAsync(CancellationToken ct = default)
    {
        if (Latest is null || Id is null)
            throw new InvalidOperationException("There is no saved version to take back.");
        await _store.MarkUndoneAsync(Latest, ct);
        return await ReopenAsync(ct);
    }

    /// <summary>
    /// Takes back the last undo: <see cref="Restorable"/> is in use again and open, discarding any
    /// unsaved changes. False when its grace period has passed meanwhile.
    /// </summary>
    public async Task<bool> RestoreAsync(CancellationToken ct = default)
    {
        if (Restorable is null)
            throw new InvalidOperationException("There is no undone version to restore.");
        if (!await _store.RestoreAsync(Restorable, ct))
        {
            Restorable = await _store.RestorableAsync(DocumentKind.Draft, Id!.Value, ct);
            return false;
        }
        return await ReopenAsync(ct);
    }

    // Reads the campaign back from storage after an undo or a restore.
    private async Task<bool> ReopenAsync(CancellationToken ct)
    {
        Latest = (await _store.HistoryAsync(DocumentKind.Draft, Id!.Value, ct)).FirstOrDefault();
        Restorable = await _store.RestorableAsync(DocumentKind.Draft, Id.Value, ct);
        if (Latest is null) return false;
        Editor = CampaignEditor.Open(await _store.LoadDraftAsync(Latest, ct));
        return true;
    }
}
