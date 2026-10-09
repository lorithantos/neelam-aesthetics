namespace Neelam.Campaigns.Storage;

/// <summary>
/// One campaign open in the editor, over a client's <see cref="CampaignStore"/>: what the campaign
/// page does, without the page. A campaign starts from the newest version of one of the client's
/// templates. A save is a new version and never replaces one. Undoing the last save deletes that
/// version for good and puts the one before it back; nothing keeps a record of the deleted version.
/// </summary>
public sealed class DraftSession
{
    private readonly CampaignStore _store;

    private DraftSession(CampaignStore store, Guid? id, CampaignEditor editor, SaveRef? latest)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
    }

    /// <summary>The campaign's id; null until a new campaign is first saved.</summary>
    public Guid? Id { get; private set; }

    public CampaignEditor Editor { get; private set; }

    /// <summary>The newest saved version, or null when there is none.</summary>
    public SaveRef? Latest { get; private set; }

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

    /// <summary>The newest version of a campaign, or null when it has no saves.</summary>
    public static async Task<DraftSession?> OpenAsync(CampaignStore store, Guid id, CancellationToken ct = default)
    {
        var latest = (await store.HistoryAsync(DocumentKind.Draft, id, ct)).FirstOrDefault();
        return latest is null
            ? null
            : new DraftSession(store, id, CampaignEditor.Open(await store.LoadDraftAsync(latest, ct)), latest);
    }

    /// <summary>
    /// Saves the draft as it stands, finished or not, as a new version. A new campaign gets its id
    /// here.
    /// </summary>
    public async Task<SaveRef> SaveAsync(CancellationToken ct = default)
    {
        Id ??= Guid.NewGuid();
        Latest = await _store.SaveDraftAsync(Id.Value, Editor.Title, Editor.Draft, ct);
        return Latest;
    }

    /// <summary>
    /// Deletes the newest version for good and opens the one before it, discarding any unsaved
    /// changes. False when no version is left, and the campaign is gone.
    /// </summary>
    public async Task<bool> UndoLastSaveAsync(CancellationToken ct = default)
    {
        if (Latest is null || Id is null)
            throw new InvalidOperationException("There is no saved version to take back.");
        await _store.DeleteAsync(Latest, ct);

        Latest = (await _store.HistoryAsync(DocumentKind.Draft, Id.Value, ct)).FirstOrDefault();
        if (Latest is null) return false;
        Editor = CampaignEditor.Open(await _store.LoadDraftAsync(Latest, ct));
        return true;
    }
}
