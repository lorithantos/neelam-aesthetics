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

    // The draft as last saved or opened, to tell an edit since then; null before the first save.
    private string? _saved;

    private DraftSession(
        CampaignStore store, Guid? id, CampaignEditor editor, SaveRef? latest, SaveRef? restorable = null, SaveRef? superseded = null)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
        Restorable = restorable;
        SupersededApproval = superseded;
        _saved = latest is null ? null : CampaignJson.SerializeDraft(editor.Draft);
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

    /// <summary>True when the form holds anything not in <see cref="Latest"/>, or nothing is saved yet.</summary>
    public bool HasUnsavedChanges =>
        _saved is null || Editor.Errors().Count > 0 || CampaignJson.SerializeDraft(Editor.Draft) != _saved;

    /// <summary>
    /// The approval in force: <see cref="Latest"/>'s, while the form still holds exactly what was
    /// approved. Null once anything is edited or saved since, and when nobody approved it.
    /// </summary>
    public Approval? CurrentApproval => HasUnsavedChanges ? null : Latest?.Approval;

    /// <summary>
    /// An earlier version's approval that a later save has left behind, so the page can say the
    /// campaign was approved and is no longer; null when <see cref="Latest"/> is approved itself or
    /// no earlier version was.
    /// </summary>
    public SaveRef? SupersededApproval { get; private set; }

    /// <summary>
    /// Why the campaign cannot be approved as it stands, or null when it can: saved, unchanged since,
    /// built with no part missing, and nothing the rules must stop.
    /// </summary>
    public string? CannotApprove(CampaignPolicy? policy = null, BusinessContext? business = null)
    {
        if (Latest is null || HasUnsavedChanges) return "Save the campaign first: an approval is of a saved version.";
        var status = Editor.Status(policy, business);
        if (status.Review is null || status.Review.Blockers.Any())
            return "Fix everything marked Must fix first.";
        return null;
    }

    /// <summary>
    /// Approves the saved version as it stands, in the approver's name, now. Recorded with the save
    /// itself, so a later edit or save is not approved, and undoing the save drops it.
    /// </summary>
    /// <exception cref="InvalidOperationException">It cannot be approved; <see cref="CannotApprove"/> says why.</exception>
    public async Task<Approval> ApproveAsync(
        string approvedBy, CampaignPolicy? policy = null, BusinessContext? business = null, CancellationToken ct = default)
    {
        if (CannotApprove(policy, business) is { } why) throw new InvalidOperationException(why);
        Latest = await _store.ApproveAsync(Latest!, approvedBy, ct);
        SupersededApproval = null;
        return Latest.Approval!;
    }

    /// <summary>Withdraws the approval of the saved version; the campaign stays as it is.</summary>
    public async Task WithdrawApprovalAsync(CancellationToken ct = default)
    {
        if (Latest?.Approval is null) throw new InvalidOperationException("There is no approval to withdraw.");
        Latest = await _store.WithdrawApprovalAsync(Latest, ct);
    }

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
        var history = await store.HistoryAsync(DocumentKind.Draft, id, ct);
        var latest = history.FirstOrDefault();
        return latest is null
            ? null
            : new DraftSession(store, id, CampaignEditor.Open(await store.LoadDraftAsync(latest, ct)), latest,
                await store.RestorableAsync(DocumentKind.Draft, id, ct), Superseded(history));
    }

    // The newest earlier version with an approval, when the newest itself has none.
    private static SaveRef? Superseded(IReadOnlyList<SaveRef> history) =>
        history.Count == 0 || history[0].Approval is not null
            ? null
            : history.Skip(1).FirstOrDefault(s => s.Approval is not null);

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
        var before = Latest;
        Latest = await _store.SaveDraftAsync(Id.Value, Editor.Title, Editor.Draft, ct);
        _saved = CampaignJson.SerializeDraft(Editor.Draft);
        // Saved over: an undone version below the new one is no longer offered back.
        Restorable = null;
        // A new version is not approved, whatever the one before it was.
        if (before?.Approval is not null) SupersededApproval = before;
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
        var history = await _store.HistoryAsync(DocumentKind.Draft, Id!.Value, ct);
        Latest = history.FirstOrDefault();
        SupersededApproval = Superseded(history);
        Restorable = await _store.RestorableAsync(DocumentKind.Draft, Id.Value, ct);
        if (Latest is null)
        {
            _saved = null;
            return false;
        }
        Editor = CampaignEditor.Open(await _store.LoadDraftAsync(Latest, ct));
        _saved = CampaignJson.SerializeDraft(Editor.Draft);
        return true;
    }
}
