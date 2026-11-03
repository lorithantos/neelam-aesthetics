namespace Neelam.Campaigns.Storage;

/// <summary>
/// One template open in the editor, over a client's <see cref="CampaignStore"/>: what the editor
/// page does, without the page. A save is a new version and never replaces one. Undoing the last
/// save puts the one before it back, which is how a bad change to a template is taken back, and
/// marks the undone version for deletion: for the store's grace period it can be restored, here or
/// after coming back to the template, and then the sweep deletes it, leaving no record. Leaving the
/// page deletes nothing.
/// </summary>
/// <remarks>
/// Another tab or person may change the template while it is open (2026-10-11, phase 2 of the owner's
/// "concurrent usage by two users"): every save, undo and restore here tells the other pages open on
/// this instance (<see cref="CampaignStore.Signals"/>), the page watches storage for the rest
/// (<see cref="IWatched{T}"/>), and Undo acts only on the version the page holds while it is still the
/// newest. Saving is unchanged: the check at Save comes with phase 3.
/// </remarks>
public sealed class TemplateSession : IWatched<SaveRef>
{
    private readonly CampaignStore _store;

    private TemplateSession(
        CampaignStore store, Guid? id, TemplateEditor editor, SaveRef? latest, SaveRef? restorable = null, int versions = 0)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
        Restorable = restorable;
        _versions = versions;
        if (latest is not null) MarkSaved();
    }

    // How many saves of the template are in use: undoing the only one takes the template off her list.
    private int _versions;

    // The template as last saved or opened, to tell an edit since then; null before the first save, and
    // for one opened with errors, which then always counts as holding something unsaved.
    private string? _saved;

    private void MarkSaved() =>
        _saved = Editor.Errors().Count == 0 ? CampaignJson.SerializeTemplate(Editor.Build()) : null;

    /// <summary>The template's id; null until a new template is first saved.</summary>
    public Guid? Id { get; private set; }

    /// <summary>The client whose template this is.</summary>
    public ClientName Client => _store.Client;

    public TemplateEditor Editor { get; private set; }

    /// <summary>The newest saved version, or null when there is none.</summary>
    public SaveRef? Latest { get; private set; }

    /// <summary>What the open page watches it under; null before the first save, when there is nothing to watch.</summary>
    public string? Subject => Latest is not null && Id is { } id ? WatchSubject.Template(id) : null;

    SaveRef? IWatched<SaveRef>.Held => Latest;

    /// <summary>The undone version Restore would bring back, while its grace period lasts; otherwise null.</summary>
    public SaveRef? Restorable { get; private set; }

    /// <summary>Until when <see cref="Restorable"/> can be restored.</summary>
    public DateTimeOffset? RestorableUntil => Restorable is null ? null : _store.RestorableUntil(Restorable);

    /// <summary>
    /// True when undoing the last save leaves no version in use: the template goes from her list,
    /// restorable for the grace period, so the page calls it deleting, as the campaign page does
    /// (owner, 2026-11-03).
    /// </summary>
    public bool UndoDeletesTheTemplate => Latest is not null && _versions <= 1;

    /// <summary>What the page asks before an undo, with the grace period in words (<see cref="ChangeWords.UndoQuestion"/>).</summary>
    public string UndoQuestion => ChangeWords.Template.UndoQuestion(_store.UndoGracePeriod, UndoDeletesTheTemplate);

    /// <summary>
    /// True when the form holds anything not in <see cref="Latest"/>, or nothing is saved yet. A form
    /// that cannot be built as a template (it has errors) holds something unsaved by definition.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            if (_saved is null || Editor.Errors().Count > 0) return true;
            return CampaignJson.SerializeTemplate(Editor.Build()) != _saved;
        }
    }

    public static TemplateSession New(CampaignStore store, BlockCatalog catalog) =>
        new(store, null, TemplateEditor.StartBlank(catalog), null);

    /// <summary>A new template starting with the baseline's parts; see <see cref="TemplateEditor.StartFrom"/>. Nothing is saved.</summary>
    public static TemplateSession New(CampaignStore store, TemplateBaseline baseline, BlockCatalog catalog) =>
        new(store, null, TemplateEditor.StartFrom(baseline, catalog), null);

    /// <summary>
    /// The newest version of a template, or null when it has none in use. A template whose every
    /// save was undone is null here; <see cref="RestorableAsync"/> says whether it can come back.
    /// </summary>
    public static async Task<TemplateSession?> OpenAsync(CampaignStore store, Guid id, BlockCatalog catalog, CancellationToken ct = default)
    {
        var history = await store.HistoryAsync(DocumentKind.Template, id, ct);
        var latest = history.FirstOrDefault();
        return latest is null
            ? null
            : new TemplateSession(store, id, TemplateEditor.Open(await store.LoadTemplateAsync(latest, ct), catalog), latest,
                await store.RestorableAsync(DocumentKind.Template, id, ct), history.Count);
    }

    /// <summary>The undone version of a template Restore would bring back, or null.</summary>
    public static Task<SaveRef?> RestorableAsync(CampaignStore store, Guid id, CancellationToken ct = default) =>
        store.RestorableAsync(DocumentKind.Template, id, ct);

    /// <summary>
    /// Restores an undone version of a template with nothing else in use and opens it; null when
    /// its grace period has passed or it is gone.
    /// </summary>
    public static async Task<TemplateSession?> RestoreAsync(CampaignStore store, SaveRef undone, BlockCatalog catalog, CancellationToken ct = default)
    {
        if (!await store.RestoreAsync(undone, ct)) return null;
        // Restored from a page with no session open on it: every open page is told.
        store.Signals?.Raise(store.Client, WatchSubject.Template(undone.Id), source: null);
        return await OpenAsync(store, undone.Id, catalog, ct);
    }

    /// <summary>A new, unsaved template copied from another's newest version, or null when that has no saves.</summary>
    public static async Task<TemplateSession?> CopyAsync(CampaignStore store, Guid from, BlockCatalog catalog, CancellationToken ct = default)
    {
        var source = (await store.HistoryAsync(DocumentKind.Template, from, ct)).FirstOrDefault();
        return source is null
            ? null
            : new TemplateSession(store, null, TemplateEditor.CopyOf(await store.LoadTemplateAsync(source, ct), catalog), null);
    }

    /// <summary>
    /// The template's newest save in use as storage has it now; null when it has none in use, or before
    /// its first save. One listing.
    /// </summary>
    public async Task<SaveRef?> NewestAsync(CancellationToken ct = default) =>
        Id is { } id ? await _store.NewestAsync(DocumentKind.Template, id, ct) : null;

    /// <summary>
    /// What changed in storage since the version this session holds: saved again, the version undone,
    /// or every save undone; <see cref="CampaignChange.None"/> while it is still the newest, and before
    /// the first save. Unsaved edits are the page's own and never count.
    /// </summary>
    public CampaignChange ChangeSince(SaveRef? newest)
    {
        if (Latest is null) return CampaignChange.None;
        if (newest is null) return CampaignChange.Deleted;
        if (newest.BlobName == Latest.BlobName) return CampaignChange.None;
        return newest.SavedAt > Latest.SavedAt ? CampaignChange.SavedSince : CampaignChange.Undone;
    }

    /// <summary>What the template page says at its top once it knows storage has changed (<see cref="ChangeWords.Template"/>).</summary>
    public static string ChangedNotice(CampaignChange change) => ChangeWords.Template.Notice(change);

    /// <summary>Saves the editor's template as a new version; refused while it has errors.</summary>
    public async Task<SaveRef> SaveAsync(CancellationToken ct = default)
    {
        var template = Editor.Build();
        Id ??= Guid.NewGuid();
        Latest = await _store.SaveTemplateAsync(Id.Value, template, ct);
        _versions++;
        // Saved over: an undone version below the new one is no longer offered back.
        Restorable = null;
        MarkSaved();
        Raise();
        return Latest;
    }

    /// <summary>
    /// Undoes the newest version and opens the one before it, discarding any unsaved changes. The
    /// undone version is marked for deletion and stays restorable for the grace period. False when
    /// no version is left in use: the template is gone from the list, and <see cref="Restorable"/>
    /// still brings it back. Only while the page's version is still the newest in use, read again at
    /// the click (<see cref="CampaignStore.UndoIfNewestAsync"/>): a version with a newer one above it
    /// could not be restored and would be swept for good, so it is never undone (2026-10-11).
    /// </summary>
    /// <exception cref="CampaignChangedException">Another tab or person saved or undid since; nothing is undone.</exception>
    public async Task<bool> UndoLastSaveAsync(CancellationToken ct = default)
    {
        if (Latest is null || Id is null)
            throw new InvalidOperationException("There is no saved version to take back.");
        var undo = await _store.UndoIfNewestAsync(Latest, ct);
        if (!undo.Done)
        {
            var change = ChangeSince(undo.Newest);
            throw new CampaignChangedException(change, ChangeWords.Template.NotUndone(change));
        }
        try
        {
            return await ReopenAsync(ct);
        }
        finally
        {
            Raise();
        }
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
            Restorable = await _store.RestorableAsync(DocumentKind.Template, Id!.Value, ct);
            return false;
        }
        try
        {
            return await ReopenAsync(ct);
        }
        finally
        {
            Raise();
        }
    }

    // Reads the template back from storage after an undo or a restore.
    private async Task<bool> ReopenAsync(CancellationToken ct)
    {
        var history = await _store.HistoryAsync(DocumentKind.Template, Id!.Value, ct);
        Latest = history.FirstOrDefault();
        _versions = history.Count;
        Restorable = await _store.RestorableAsync(DocumentKind.Template, Id.Value, ct);
        if (Latest is null)
        {
            _saved = null;
            return false;
        }
        Editor = TemplateEditor.Open(await _store.LoadTemplateAsync(Latest, ct), Editor.Catalog);
        MarkSaved();
        return true;
    }

    // Tells the other pages open on this instance that this template changed; never this session's own.
    private void Raise()
    {
        if (Id is { } id) _store.Signals?.Raise(_store.Client, WatchSubject.Template(id), this);
    }
}
