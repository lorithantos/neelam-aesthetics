using System.Globalization;

namespace Neelam.Campaigns.Storage;

public enum DocumentKind { Draft, Template }

/// <summary>One save: a single blob, named by what it is and when it was saved.</summary>
/// <param name="Id">The campaign or template this save belongs to.</param>
/// <param name="Title">Shown in lists; stored in the blob's own metadata.</param>
public sealed record SaveRef(DocumentKind Kind, Guid Id, DateTimeOffset SavedAt, string Title, string BlobName);

/// <summary>
/// Saves drafts and templates as date/time-stamped blobs:
/// <c>drafts/{id}/{yyyyMMddTHHmmss.fffffffZ}.json</c>. Every save is a new blob and nothing is
/// ever overwritten. There is no index, manifest or database: lists are read from the blob names
/// themselves and the title travels in the blob's metadata. So deleting a blob leaves no record of
/// it anywhere — delete the newest save and the one before it becomes the latest; delete them all
/// and the campaign is gone.
/// </summary>
public sealed class CampaignStore(IBlobBackend blobs, TimeProvider clock)
{
    private const string TitleKey = "title";

    public Task<SaveRef> SaveDraftAsync(Guid id, string title, CampaignDraft draft, CancellationToken ct = default) =>
        SaveAsync(DocumentKind.Draft, id, title, CampaignJson.SerializeDraft(draft), ct);

    public Task<SaveRef> SaveTemplateAsync(Guid id, CampaignTemplate template, CancellationToken ct = default) =>
        SaveAsync(DocumentKind.Template, id, template.Name, CampaignJson.SerializeTemplate(template), ct);

    /// <summary>Every save of this kind, newest first.</summary>
    public async Task<IReadOnlyList<SaveRef>> ListAsync(DocumentKind kind, CancellationToken ct = default) =>
        await ListPrefixAsync(kind, $"{Prefix(kind)}/", ct);

    /// <summary>Every save of one campaign or template, newest first.</summary>
    public async Task<IReadOnlyList<SaveRef>> HistoryAsync(DocumentKind kind, Guid id, CancellationToken ct = default) =>
        await ListPrefixAsync(kind, $"{Prefix(kind)}/{id:N}/", ct);

    /// <summary>The newest save of each campaign or template, newest first.</summary>
    public async Task<IReadOnlyList<SaveRef>> LatestAsync(DocumentKind kind, CancellationToken ct = default) =>
        (await ListAsync(kind, ct)).GroupBy(s => s.Id).Select(g => g.First()).ToList();

    public async Task<CampaignDraft> LoadDraftAsync(SaveRef save, CancellationToken ct = default) =>
        CampaignJson.DeserializeDraft(await blobs.ReadAsync(Expect(save, DocumentKind.Draft), ct));

    public async Task<CampaignTemplate> LoadTemplateAsync(SaveRef save, CancellationToken ct = default) =>
        CampaignJson.DeserializeTemplate(await blobs.ReadAsync(Expect(save, DocumentKind.Template), ct));

    /// <summary>Permanently deletes one save. There is no recycle bin.</summary>
    public Task<bool> DeleteAsync(SaveRef save, CancellationToken ct = default) =>
        blobs.DeleteAsync(save.BlobName, ct);

    private async Task<SaveRef> SaveAsync(DocumentKind kind, Guid id, string title, string json, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string> { [TitleKey] = Uri.EscapeDataString(title) };
        var (name, at) = await SaveStamp.CreateAsync(
            blobs, stamp => BlobName(kind, id, stamp), json, metadata, clock.GetUtcNow(), ct);
        return new SaveRef(kind, id, at, title, name);
    }

    private async Task<List<SaveRef>> ListPrefixAsync(DocumentKind kind, string prefix, CancellationToken ct)
    {
        var saves = new List<SaveRef>();
        await foreach (var blob in blobs.ListAsync(prefix, ct))
        {
            if (TryParse(kind, blob, out var save)) saves.Add(save);
        }
        return saves.OrderByDescending(s => s.SavedAt).ToList();
    }

    private static string Prefix(DocumentKind kind) => kind switch
    {
        DocumentKind.Draft => "drafts",
        DocumentKind.Template => "templates",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string BlobName(DocumentKind kind, Guid id, DateTimeOffset at) =>
        $"{Prefix(kind)}/{id:N}/{SaveStamp.Of(at)}.json";

    // Blobs that do not follow the naming scheme are ignored rather than guessed at.
    private static bool TryParse(DocumentKind kind, BlobEntry blob, out SaveRef save)
    {
        save = null!;
        var parts = blob.Name.Split('/');
        if (parts.Length != 3 || parts[0] != Prefix(kind))
            return false;
        if (!Guid.TryParseExact(parts[1], "N", out var id))
            return false;
        if (!SaveStamp.TryRead(parts[2], out var at))
            return false;

        var title = blob.Metadata.TryGetValue(TitleKey, out var t) ? Uri.UnescapeDataString(t) : "(untitled)";
        save = new SaveRef(kind, id, at, title, blob.Name);
        return true;
    }

    private static string Expect(SaveRef save, DocumentKind kind) =>
        save.Kind == kind
            ? save.BlobName
            : throw new ArgumentException($"Expected a {kind} save, got a {save.Kind} save.", nameof(save));
}
