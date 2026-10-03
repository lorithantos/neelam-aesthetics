namespace Neelam.Campaigns.Storage;

/// <summary>
/// One template open in the editor, over a client's <see cref="CampaignStore"/>: what the editor
/// page does, without the page. A save is a new version and never replaces one. Undoing the last
/// save deletes that version for good and puts the one before it back, which is how a bad change
/// to a template is taken back; nothing keeps a record of the deleted version.
/// </summary>
public sealed class TemplateSession
{
    private readonly CampaignStore _store;

    private TemplateSession(CampaignStore store, Guid? id, TemplateEditor editor, SaveRef? latest)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
    }

    /// <summary>The template's id; null until a new template is first saved.</summary>
    public Guid? Id { get; private set; }

    public TemplateEditor Editor { get; private set; }

    /// <summary>The newest saved version, or null when there is none.</summary>
    public SaveRef? Latest { get; private set; }

    public static TemplateSession New(CampaignStore store) => new(store, null, TemplateEditor.StartBlank(), null);

    /// <summary>The newest version of a template, or null when it has no saves.</summary>
    public static async Task<TemplateSession?> OpenAsync(CampaignStore store, Guid id, CancellationToken ct = default)
    {
        var latest = (await store.HistoryAsync(DocumentKind.Template, id, ct)).FirstOrDefault();
        return latest is null
            ? null
            : new TemplateSession(store, id, TemplateEditor.Open(await store.LoadTemplateAsync(latest, ct)), latest);
    }

    /// <summary>A new, unsaved template copied from another's newest version, or null when that has no saves.</summary>
    public static async Task<TemplateSession?> CopyAsync(CampaignStore store, Guid from, CancellationToken ct = default)
    {
        var source = (await store.HistoryAsync(DocumentKind.Template, from, ct)).FirstOrDefault();
        return source is null
            ? null
            : new TemplateSession(store, null, TemplateEditor.CopyOf(await store.LoadTemplateAsync(source, ct)), null);
    }

    /// <summary>Saves the editor's template as a new version; refused while it has errors.</summary>
    public async Task<SaveRef> SaveAsync(CancellationToken ct = default)
    {
        var template = Editor.Build();
        Id ??= Guid.NewGuid();
        Latest = await _store.SaveTemplateAsync(Id.Value, template, ct);
        return Latest;
    }

    /// <summary>
    /// Deletes the newest version for good and opens the one before it, discarding any unsaved
    /// changes. False when no version is left, and the template is gone.
    /// </summary>
    public async Task<bool> UndoLastSaveAsync(CancellationToken ct = default)
    {
        if (Latest is null || Id is null)
            throw new InvalidOperationException("There is no saved version to take back.");
        await _store.DeleteAsync(Latest, ct);

        Latest = (await _store.HistoryAsync(DocumentKind.Template, Id.Value, ct)).FirstOrDefault();
        if (Latest is null) return false;
        Editor = TemplateEditor.Open(await _store.LoadTemplateAsync(Latest, ct));
        return true;
    }
}
