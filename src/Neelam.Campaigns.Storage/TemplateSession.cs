namespace Neelam.Campaigns.Storage;

/// <summary>
/// One template open in the editor, over a client's <see cref="CampaignStore"/>: what the editor
/// page does, without the page. A save is a new version and never replaces one. Undoing the last
/// save puts the one before it back, which is how a bad change to a template is taken back, and
/// marks the undone version for deletion: for the store's grace period it can be restored, here or
/// after coming back to the template, and then the sweep deletes it, leaving no record. Leaving the
/// page deletes nothing.
/// </summary>
public sealed class TemplateSession
{
    private readonly CampaignStore _store;

    private TemplateSession(CampaignStore store, Guid? id, TemplateEditor editor, SaveRef? latest, SaveRef? restorable = null)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
        Restorable = restorable;
    }

    /// <summary>The template's id; null until a new template is first saved.</summary>
    public Guid? Id { get; private set; }

    public TemplateEditor Editor { get; private set; }

    /// <summary>The newest saved version, or null when there is none.</summary>
    public SaveRef? Latest { get; private set; }

    /// <summary>The undone version Restore would bring back, while its grace period lasts; otherwise null.</summary>
    public SaveRef? Restorable { get; private set; }

    /// <summary>Until when <see cref="Restorable"/> can be restored.</summary>
    public DateTimeOffset? RestorableUntil => Restorable is null ? null : _store.RestorableUntil(Restorable);

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
        var latest = (await store.HistoryAsync(DocumentKind.Template, id, ct)).FirstOrDefault();
        return latest is null
            ? null
            : new TemplateSession(store, id, TemplateEditor.Open(await store.LoadTemplateAsync(latest, ct), catalog), latest,
                await store.RestorableAsync(DocumentKind.Template, id, ct));
    }

    /// <summary>The undone version of a template Restore would bring back, or null.</summary>
    public static Task<SaveRef?> RestorableAsync(CampaignStore store, Guid id, CancellationToken ct = default) =>
        store.RestorableAsync(DocumentKind.Template, id, ct);

    /// <summary>
    /// Restores an undone version of a template with nothing else in use and opens it; null when
    /// its grace period has passed or it is gone.
    /// </summary>
    public static async Task<TemplateSession?> RestoreAsync(CampaignStore store, SaveRef undone, BlockCatalog catalog, CancellationToken ct = default) =>
        await store.RestoreAsync(undone, ct) ? await OpenAsync(store, undone.Id, catalog, ct) : null;

    /// <summary>A new, unsaved template copied from another's newest version, or null when that has no saves.</summary>
    public static async Task<TemplateSession?> CopyAsync(CampaignStore store, Guid from, BlockCatalog catalog, CancellationToken ct = default)
    {
        var source = (await store.HistoryAsync(DocumentKind.Template, from, ct)).FirstOrDefault();
        return source is null
            ? null
            : new TemplateSession(store, null, TemplateEditor.CopyOf(await store.LoadTemplateAsync(source, ct), catalog), null);
    }

    /// <summary>Saves the editor's template as a new version; refused while it has errors.</summary>
    public async Task<SaveRef> SaveAsync(CancellationToken ct = default)
    {
        var template = Editor.Build();
        Id ??= Guid.NewGuid();
        Latest = await _store.SaveTemplateAsync(Id.Value, template, ct);
        // Saved over: an undone version below the new one is no longer offered back.
        Restorable = null;
        return Latest;
    }

    /// <summary>
    /// Undoes the newest version and opens the one before it, discarding any unsaved changes. The
    /// undone version is marked for deletion and stays restorable for the grace period. False when
    /// no version is left in use: the template is gone from the list, and <see cref="Restorable"/>
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
            Restorable = await _store.RestorableAsync(DocumentKind.Template, Id!.Value, ct);
            return false;
        }
        return await ReopenAsync(ct);
    }

    // Reads the template back from storage after an undo or a restore.
    private async Task<bool> ReopenAsync(CancellationToken ct)
    {
        Latest = (await _store.HistoryAsync(DocumentKind.Template, Id!.Value, ct)).FirstOrDefault();
        Restorable = await _store.RestorableAsync(DocumentKind.Template, Id.Value, ct);
        if (Latest is null) return false;
        Editor = TemplateEditor.Open(await _store.LoadTemplateAsync(Latest, ct), Editor.Catalog);
        return true;
    }
}
