using System.Text.Json;
using Janet.Azure.Storage;

namespace Neelam.Campaigns.Storage;

/// <summary>A campaign's working copy as storage holds it: what the form held, unsaved, when it was last written.</summary>
/// <param name="Draft">The form's draft content, as a save would hold it.</param>
/// <param name="Json">The same, as written (<see cref="CampaignJson.SerializeDraft"/>), to compare with the form and the newest save.</param>
/// <param name="BasedOn">The stamp of the save the form started from; null when the page had none.</param>
/// <param name="WrittenAt">When it was last written.</param>
/// <param name="ETag">The blob's version as read, for the page's next write (If-Match).</param>
public sealed record WorkingCopy(CampaignDraft Draft, string Json, DateTimeOffset? BasedOn, DateTimeOffset WrittenAt, string ETag);

/// <summary>
/// The working copies of a client's campaigns (owner, 2026-11-03): a recovery copy of what the campaign page
/// holds and has not saved, written a few seconds after she stops changing the form, so a closed tab, a
/// dropped connection or a restart loses nothing she typed. It is not a save: one blob per campaign,
/// <c>working/{id}.json</c>, overwritten in place, outside <c>drafts/</c>, so nothing that lists the saves
/// (history, newest, the campaigns list, Recently deleted, the sweep's drafts) ever sees it. It is never
/// proofread, approved or exported, and neither writing nor removing it goes on the activity trail. Blob
/// versioning and soft delete are off for the account (<c>infra/storage.bicep</c>), so an overwrite or a
/// removal leaves nothing of what it held, as for a save swept for good.
/// </summary>
/// <remarks>
/// What it held is in its content; when it was written and the save it started from are in its metadata,
/// as stamps, so the sweep decides from a listing without reading any content.
/// </remarks>
public sealed class WorkingCopies(IBlobBackend blobs, TimeProvider clock)
{
    /// <summary>Where they live in the client's container. No other prefix starts with it.</summary>
    internal const string Prefix = "working/";

    private const string WrittenAtKey = "writtenat";
    private const string BasedOnKey = "basedon";

    internal static string Name(Guid campaignId) => $"{Prefix}{campaignId:N}.json";

    /// <summary>The campaign's working copy, or null when it has none.</summary>
    /// <exception cref="InvalidDataException">It is there and cannot be read as a draft.</exception>
    public async Task<WorkingCopy?> ReadAsync(Guid campaignId, CancellationToken ct = default)
    {
        var name = Name(campaignId);
        await foreach (var blob in blobs.ListAsync(name, ct))
        {
            if (blob.Name != name) continue;
            var json = await blobs.ReadTextAsync(name, ct);
            CampaignDraft draft;
            try
            {
                draft = CampaignJson.DeserializeDraft(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("A working copy that is not a draft.", ex);
            }
            return new WorkingCopy(draft, CampaignJson.SerializeDraft(draft), Stamp(blob.Metadata, BasedOnKey),
                Stamp(blob.Metadata, WrittenAtKey) ?? DateTimeOffset.MinValue, blob.ETag);
        }
        return null;
    }

    /// <summary>
    /// Writes the campaign's working copy in place of the one there, only when what is there is what
    /// <paramref name="expected"/> says: the version this page last wrote, or none.
    /// </summary>
    /// <param name="basedOn">When the save the form started from was saved; null when it has none.</param>
    /// <returns>Its new ETag; null when another tab wrote or removed it meanwhile, and nothing was written.</returns>
    public Task<string?> WriteAsync(
        Guid campaignId, CampaignDraft draft, DateTimeOffset? basedOn, BlobExpected expected, CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string> { [WrittenAtKey] = SaveStamp.Of(clock.GetUtcNow()) };
        if (basedOn is { } at) metadata[BasedOnKey] = SaveStamp.Of(at);
        return blobs.ReplaceAsync(Name(campaignId), BinaryData.FromString(CampaignJson.SerializeDraft(draft)),
            BlobContent.JsonContentType, metadata, expected, ct);
    }

    /// <summary>Removes the campaign's working copy, whoever wrote it. False when it had none.</summary>
    public Task<bool> RemoveAsync(Guid campaignId, CancellationToken ct = default) => blobs.DeleteAsync(Name(campaignId), ct);

    /// <summary>Every working copy as listed, for the sweep: listed before the saves, as a working copy is only ever written after its campaign's first save.</summary>
    internal async Task<IReadOnlyList<BlobEntry>> ListAsync(CancellationToken ct)
    {
        var listed = new List<BlobEntry>();
        await foreach (var blob in blobs.ListAsync(Prefix, ct)) listed.Add(blob);
        return listed;
    }

    /// <summary>
    /// Removes, of the working copies <paramref name="listed"/> before the saves were, those written longer ago
    /// than <paramref name="keep"/>, and those whose campaign has no saves left (<paramref name="withSaves"/>:
    /// every campaign with a save in its prefix after the sweep, undone or not). Listed first, a copy's
    /// campaign was saved before the saves were listed, so one missing from them really has none. Each only
    /// while it is still as listed (its ETag): one written again since is a page still at work, and is left to
    /// the next run. Nothing is recorded.
    /// </summary>
    /// <returns>How many were removed.</returns>
    internal async Task<int> SweepAsync(
        IReadOnlyList<BlobEntry> listed, IReadOnlySet<Guid> withSaves, TimeSpan keep, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var removed = 0;
        foreach (var blob in listed)
        {
            // A name this app did not write is left alone rather than guessed at.
            if (!Guid.TryParseExact(blob.Name[Prefix.Length..].Replace(".json", "", StringComparison.Ordinal), "N", out var id)
                || blob.Name != Name(id))
                continue;
            var stale = Stamp(blob.Metadata, WrittenAtKey) is not { } written || now - written >= keep;
            if ((stale || !withSaves.Contains(id)) && await blobs.DeleteIfUnchangedAsync(blob.Name, blob.ETag, ct)) removed++;
        }
        return removed;
    }

    private static DateTimeOffset? Stamp(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var stamp) && SaveStamp.TryRead(stamp + ".json", out var at) ? at : null;
}

/// <summary>
/// What the campaign page says of its working copy. Placeholder wording (owner, 2026-11-03), the owner's to
/// approve. A working copy that cannot be written is never said: it is logged, and she is shown nothing.
/// </summary>
public static class WorkingCopyWords
{
    /// <summary>On opening a campaign whose working copy differs from its newest save, now in the form.</summary>
    /// <param name="when">When the working copy was last written, in her own zone.</param>
    public static string BroughtBack(string when) => $"Brought back changes you hadn't saved, from {when}";

    /// <summary>The button beside it: removes the working copy and opens the newest save.</summary>
    public const string Discard = "Discard";

    /// <summary>When another tab wrote the working copy since this page last did.</summary>
    public const string OtherTab = "This campaign is being edited in another tab";
}

/// <summary>
/// When the campaign page writes its working copy, and how long the sweep keeps one nobody writes, from the
/// <c>WorkingCopy</c> section of the app's settings (owner, 2026-11-03).
/// </summary>
public sealed class WorkingCopyOptions
{
    public const string Section = "WorkingCopy";

    /// <summary>How long after she stops changing the form the working copy is written. Five seconds unless set.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest it waits while changes keep coming: at least one write this often. A minute unless set.</summary>
    public TimeSpan Cap { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the sweep keeps a working copy nobody has written since. Seven days unless set.</summary>
    public TimeSpan KeepUnchanged { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Why these settings cannot run, or null when they can.</summary>
    public string? Problem() =>
        Debounce <= TimeSpan.Zero || Cap < Debounce
            ? "WorkingCopy:Debounce must be a positive time span, such as 00:00:05, and WorkingCopy:Cap no shorter than it, such as 00:01:00."
        : Cap > TimeSpan.FromDays(1)
            ? "WorkingCopy:Cap must be at most a day, such as 00:01:00."
        : KeepUnchanged <= TimeSpan.Zero
            ? "WorkingCopy:KeepUnchanged must be a positive time span, such as 7.00:00:00."
        : null;
}
