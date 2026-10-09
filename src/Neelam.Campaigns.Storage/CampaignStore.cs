using System.Globalization;

namespace Neelam.Campaigns.Storage;

public enum DocumentKind { Draft, Template }

/// <summary>One save: a single blob, named by what it is and when it was saved.</summary>
/// <param name="Id">The campaign or template this save belongs to.</param>
/// <param name="Title">Shown in lists; stored in the blob's own metadata.</param>
/// <param name="UndoneAt">When the save was undone, or null for a save in use. Stored in the blob's own metadata.</param>
/// <param name="Approval">
/// Who approved this exact save, and when; null when nobody has, or the approval was withdrawn. Read
/// from the approvals table (<see cref="IApprovalStore"/>) for one campaign's history
/// (<see cref="CampaignStore.HistoryAsync"/>); lists across campaigns leave it null.
/// </param>
public sealed record SaveRef(
    DocumentKind Kind, Guid Id, DateTimeOffset SavedAt, string Title, string BlobName, DateTimeOffset? UndoneAt = null,
    Approval? Approval = null);

/// <summary>
/// Saves drafts and templates as date/time-stamped blobs:
/// <c>drafts/{id}/{yyyyMMddTHHmmss.fffffffZ}.json</c>. Every save is a new blob and nothing is
/// ever overwritten. There is no index, manifest or database: lists are read from the blob names
/// themselves and the title travels in the blob's metadata. So deleting a blob leaves nothing of
/// its contents anywhere — delete the newest save and the one before it becomes the latest; delete
/// them all and the campaign is gone.
/// </summary>
/// <remarks>
/// Undo does not delete. It marks the save undone in that blob's own metadata, with the time, and
/// from then on every list, history and latest leaves the save out, so to its client it is gone.
/// Until the grace period has passed the mark can be cleared again (restore); after it, the sweep
/// deletes the blob, and nothing of the save's contents remains, as before. There is still no index: the sweep
/// finds marks by listing the blobs and reading their metadata.
/// <para>
/// Approvals live in the approvals table, keyed by client, campaign and the save's stamp (owner,
/// 2026-10-09), and every action here is recorded as an activity event, deletions by the sweep
/// included. Neither ever holds the save's content, so a deleted save's contents still cannot be
/// recovered; what remains is that it existed, was approved, and was deleted when.
/// </para>
/// </remarks>
/// <param name="undoGracePeriod">How long an undone save can be restored before the sweep may delete it.</param>
/// <param name="approvals">The approvals table; read and written only in <paramref name="activity"/>'s client's partition.</param>
/// <param name="activity">The trail for this client and whoever is acting. A failed write never fails an action.</param>
public sealed class CampaignStore(
    IBlobBackend blobs, TimeProvider clock, TimeSpan undoGracePeriod, IApprovalStore approvals, ActivityTrail activity)
{
    private const string TitleKey = "title";
    private const string UndoneKey = "undone";

    public Task<SaveRef> SaveDraftAsync(Guid id, string title, CampaignDraft draft, CancellationToken ct = default) =>
        SaveAsync(DocumentKind.Draft, id, title, CampaignJson.SerializeDraft(draft), ct);

    public Task<SaveRef> SaveTemplateAsync(Guid id, CampaignTemplate template, CancellationToken ct = default) =>
        SaveAsync(DocumentKind.Template, id, template.Name, CampaignJson.SerializeTemplate(template), ct);

    /// <summary>The client this store belongs to.</summary>
    public ClientName Client => activity.Client;

    /// <summary>How long an undone save can be restored.</summary>
    public TimeSpan UndoGracePeriod => undoGracePeriod;

    /// <summary>Every save of this kind, newest first. Undone saves are left out.</summary>
    public async Task<IReadOnlyList<SaveRef>> ListAsync(DocumentKind kind, CancellationToken ct = default) =>
        InUse(await ListPrefixAsync(kind, $"{Prefix(kind)}/", ct));

    /// <summary>
    /// Every save of one campaign or template, newest first. Undone saves are left out. A campaign's
    /// saves carry their approvals, from the approvals table.
    /// </summary>
    public async Task<IReadOnlyList<SaveRef>> HistoryAsync(DocumentKind kind, Guid id, CancellationToken ct = default)
    {
        var saves = InUse(await ListPrefixAsync(kind, $"{Prefix(kind)}/{id:N}/", ct));
        if (kind != DocumentKind.Draft || saves.Count == 0) return saves;
        var standing = (await approvals.ForCampaignAsync(Client, id, ct))
            .Where(a => !a.Withdrawn && a.Client == Client && a.CampaignId == id)
            .ToDictionary(a => a.Stamp, StringComparer.Ordinal);
        return saves
            .Select(s => standing.TryGetValue(SaveStamp.Of(s.SavedAt), out var a) ? s with { Approval = a.Approval } : s)
            .ToList();
    }

    /// <summary>The newest save of each campaign or template, newest first.</summary>
    public async Task<IReadOnlyList<SaveRef>> LatestAsync(DocumentKind kind, CancellationToken ct = default) =>
        (await ListAsync(kind, ct)).GroupBy(s => s.Id).Select(g => g.First()).ToList();

    public async Task<CampaignDraft> LoadDraftAsync(SaveRef save, CancellationToken ct = default) =>
        CampaignJson.DeserializeDraft(await blobs.ReadTextAsync(Expect(save, DocumentKind.Draft), ct));

    public async Task<CampaignTemplate> LoadTemplateAsync(SaveRef save, CancellationToken ct = default) =>
        CampaignJson.DeserializeTemplate(await blobs.ReadTextAsync(Expect(save, DocumentKind.Template), ct));

    /// <summary>
    /// Undoes a save: marks it undone now, in its own metadata, so it drops out of every list. Its
    /// content is untouched until the sweep deletes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The save is already gone.</exception>
    public async Task<SaveRef> MarkUndoneAsync(SaveRef save, CancellationToken ct = default)
    {
        var blob = await FindAsync(save, ct) ?? throw new InvalidOperationException("That save is already gone.");
        var at = clock.GetUtcNow();
        // An undone save loses its approval: restored, it comes back unapproved. The row stays, as
        // the record that it was approved, marked withdrawn from the moment of the undo. Withdrawn
        // first, so a failure between the two leaves the save unapproved rather than approved.
        if (save.Kind == DocumentKind.Draft && await StandingApprovalAsync(save, ct) is { } approved)
            await approvals.PutAsync(approved with { WithdrawnAt = at }, ct);
        var metadata = new Dictionary<string, string>(blob.Metadata)
        {
            [UndoneKey] = at.ToString("o", CultureInfo.InvariantCulture),
        };
        if (!await blobs.SetMetadataAsync(save.BlobName, metadata, ct))
            throw new InvalidOperationException("That save is already gone.");
        await RecordAsync(save, ActivityAction.Undone, ct);
        return save with { UndoneAt = at, Approval = null };
    }

    /// <summary>
    /// Records that <paramref name="approvedBy"/> approved this exact draft save, now, in the approvals
    /// table. The row is keyed by the save, so a later save is not approved, and it stays when the save
    /// is undone or deleted, as the record. Whether the campaign may be approved is the caller's to
    /// decide (<see cref="DraftSession"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The save is gone or undone.</exception>
    public async Task<SaveRef> ApproveAsync(SaveRef save, string approvedBy, CancellationToken ct = default)
    {
        Expect(save, DocumentKind.Draft);
        if (string.IsNullOrWhiteSpace(approvedBy))
            throw new ArgumentException("An approval needs the approver's name.", nameof(approvedBy));
        var blob = await FindAsync(save, ct);
        if (blob is null || !TryParse(save.Kind, blob, out var current) || current.UndoneAt is not null)
            throw new InvalidOperationException("That save is gone, so it cannot be approved.");

        var approval = new Approval(approvedBy.Trim(), clock.GetUtcNow());
        await approvals.PutAsync(
            new ApprovalRecord(Client, save.Id, SaveStamp.Of(save.SavedAt), approval.By, approval.At), ct);
        await RecordAsync(save, ActivityAction.Approved, ct, activity.Actor.NameFor(approval.By));
        return current with { Approval = approval };
    }

    /// <summary>Takes an approval back: the save stays, unapproved, and the row stays, marked withdrawn.</summary>
    /// <exception cref="InvalidOperationException">The save is gone, or it has no approval to withdraw.</exception>
    public async Task<SaveRef> WithdrawApprovalAsync(SaveRef save, CancellationToken ct = default)
    {
        var blob = await FindAsync(save, ct);
        if (blob is null || !TryParse(save.Kind, blob, out var current))
            throw new InvalidOperationException("That save is already gone.");
        var approved = await StandingApprovalAsync(save, ct)
                       ?? throw new InvalidOperationException("There is no approval to withdraw.");
        await approvals.PutAsync(approved with { WithdrawnAt = clock.GetUtcNow() }, ct);
        await RecordAsync(save, ActivityAction.ApprovalWithdrawn, ct);
        return current with { Approval = null };
    }

    /// <summary>
    /// Carries an approval from one save to a later save of the same campaign whose content is the
    /// same, the client's label aside (<see cref="DraftSession"/> decides that): a row for the later
    /// save with the same approver and time, and the same record of who went on past its warnings
    /// at export, if anyone has. The earlier row stays as it is. Unchanged when the
    /// earlier save's approval no longer stands.
    /// </summary>
    public async Task<SaveRef> KeepApprovalAsync(SaveRef approved, SaveRef later, CancellationToken ct = default)
    {
        Expect(later, DocumentKind.Draft);
        if (approved.Id != later.Id)
            throw new ArgumentException("An approval is kept only within one campaign.", nameof(later));
        if (await StandingApprovalAsync(approved, ct) is not { } row) return later;
        await approvals.PutAsync(row with { Stamp = SaveStamp.Of(later.SavedAt), WithdrawnAt = null }, ct);
        // On the later save, by whoever saved it: ids only, as every event.
        await RecordAsync(later, ActivityAction.ApprovalCarriedToLabelOnlySave, ct);
        return later with { Approval = row.Approval };
    }

    /// <summary>
    /// Records, on the save's approval row, that someone was shown its "Worth a look" findings when
    /// exporting it and went on (owner, 2026-10-09: "This is handholding, not handcuffs"): who and
    /// when, and which findings by their keys (<see cref="Finding.SeenKey"/>, hashes), never what they
    /// said. The keys add to those already recorded, so a warning that appears later is shown then,
    /// and going on past it records who and when anew; asked again with nothing new, the record
    /// stands as it is. It goes with the approval: a label-only save carries it
    /// (<see cref="KeepApprovalAsync"/>), and any other save, a withdrawal or an undo leaves it behind.
    /// </summary>
    /// <param name="shown">The keys of the warnings she was shown, or the version's warnings as they stand.</param>
    /// <returns>The save with its approval, now carrying <see cref="Approval.WarningsSeen"/>.</returns>
    /// <exception cref="InvalidOperationException">The save's approval does not stand.</exception>
    public async Task<SaveRef> WarningsSeenAtExportAsync(
        SaveRef save, IReadOnlyCollection<string> shown, CancellationToken ct = default)
    {
        Expect(save, DocumentKind.Draft);
        var row = await StandingApprovalAsync(save, ct)
                  ?? throw new InvalidOperationException("Approve this version first: export is of an approved version.");
        var before = row.Approval.WarningsSeen;
        if (before?.Keys is not { } known || !shown.All(known.Contains))
        {
            // Signed in, the user; in Prototype, the name typed for this version's approval, as approvals are.
            var by = activity.Actor.NameFor(row.ApprovedBy);
            row = row with
            {
                WarningsSeenBy = by,
                WarningsSeenAt = clock.GetUtcNow(),
                WarningsSeenKeys = ApprovalRecord.JoinKeys((before?.Keys ?? Enumerable.Empty<string>()).Union(shown, StringComparer.Ordinal)),
            };
            await approvals.PutAsync(row, ct);
            await RecordAsync(save, ActivityAction.WarningsSeenAtExport, ct, by);
        }
        return save with { Approval = row.Approval };
    }

    // This save's approval, while it stands.
    private async Task<ApprovalRecord?> StandingApprovalAsync(SaveRef save, CancellationToken ct)
    {
        var stamp = SaveStamp.Of(save.SavedAt);
        return (await approvals.ForCampaignAsync(Client, save.Id, ct))
            .FirstOrDefault(a => a.Client == Client && a.CampaignId == save.Id && a.Stamp == stamp && !a.Withdrawn);
    }

    // Never throws: the trail is wanted, but never at the cost of the action it records.
    private Task RecordAsync(SaveRef save, ActivityAction action, CancellationToken ct, string? actorName = null) =>
        activity.RecordAsync(Entity(save.Kind), save.Id.ToString("N"), SaveStamp.Of(save.SavedAt), action, ct, actorName);

    private static ActivityEntity Entity(DocumentKind kind) =>
        kind == DocumentKind.Draft ? ActivityEntity.Campaign : ActivityEntity.Template;

    /// <summary>When an undone save can no longer be restored, and the sweep may delete it.</summary>
    public DateTimeOffset? RestorableUntil(SaveRef save) => save.UndoneAt + undoGracePeriod;

    /// <summary>
    /// The undone save that Restore would bring back for one campaign or template: the save just
    /// above the newest one in use (the last one undone), while its grace period lasts. Null when
    /// there is none, so a save undone and then saved over is not offered back.
    /// </summary>
    public async Task<SaveRef?> RestorableAsync(DocumentKind kind, Guid id, CancellationToken ct = default)
    {
        var undoneAbove = (await ListPrefixAsync(kind, $"{Prefix(kind)}/{id:N}/", ct))
            .TakeWhile(s => s.UndoneAt is not null).LastOrDefault();
        return undoneAbove is not null && CanRestore(undoneAbove) ? undoneAbove : null;
    }

    /// <summary>
    /// Every campaign or template with no save in use and one still restorable: for each, the save
    /// Restore would bring back (as <see cref="RestorableAsync"/> gives it, the last one undone), most
    /// recently undone first. Read from the blob names and marks; no save's content is read.
    /// </summary>
    public async Task<IReadOnlyList<SaveRef>> RecentlyDeletedAsync(DocumentKind kind, CancellationToken ct = default) =>
        (await ListPrefixAsync(kind, $"{Prefix(kind)}/", ct))
            .GroupBy(s => s.Id)
            .Where(g => g.All(s => s.UndoneAt is not null))
            // Newest first within each campaign, so the last undone is the oldest save.
            .Select(g => g.Last())
            .Where(CanRestore)
            .OrderByDescending(s => s.UndoneAt)
            .ToList();

    /// <summary>
    /// Takes an undo back: clears the save's mark, so it is in use again. False when the save is
    /// gone, is not undone, or its grace period has passed; the mark is read from storage, not
    /// from <paramref name="save"/>.
    /// </summary>
    public async Task<bool> RestoreAsync(SaveRef save, CancellationToken ct = default)
    {
        var blob = await FindAsync(save, ct);
        if (blob is null || !TryParse(save.Kind, blob, out var current) || !CanRestore(current))
            return false;
        var metadata = blob.Metadata.Where(m => m.Key != UndoneKey).ToDictionary(m => m.Key, m => m.Value);
        if (!await blobs.SetMetadataAsync(save.BlobName, metadata, ct)) return false;
        await RecordAsync(current, ActivityAction.Restored, ct);
        return true;
    }

    /// <summary>
    /// Deletes for good every draft and template undone longer ago than the grace period: its
    /// contents cannot be recovered, and what remains is an activity event saying which save was
    /// deleted, and when. Finds them by listing the blobs and reading their marks, so it holds no
    /// state: running it twice, or again after a crash part-way, only finishes the job. Returns how
    /// many it deleted.
    /// </summary>
    /// <remarks>
    /// Each delete holds only if the blob is still as listed (its ETag): a save restored, or undone
    /// again, between the listing and the delete has changed, so it is skipped and left to the next run.
    /// </remarks>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            foreach (var (save, etag) in await ListEntriesAsync(kind, $"{Prefix(kind)}/", ct))
            {
                if (save.UndoneAt is not null && !CanRestore(save) && await blobs.DeleteIfUnchangedAsync(save.BlobName, etag, ct))
                {
                    deleted++;
                    await RecordAsync(save, ActivityAction.DeletedBySweep, ct);
                }
            }
        }
        return deleted;
    }

    // The grace period runs from the mark; at its end the save belongs to the sweep.
    private bool CanRestore(SaveRef save) => save.UndoneAt is { } at && clock.GetUtcNow() < at + undoGracePeriod;

    private static List<SaveRef> InUse(List<SaveRef> saves) => saves.Where(s => s.UndoneAt is null).ToList();

    private async Task<BlobEntry?> FindAsync(SaveRef save, CancellationToken ct)
    {
        await foreach (var blob in blobs.ListAsync(save.BlobName, ct))
        {
            if (blob.Name == save.BlobName) return blob;
        }
        return null;
    }

    private async Task<SaveRef> SaveAsync(DocumentKind kind, Guid id, string title, string json, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string> { [TitleKey] = Uri.EscapeDataString(title) };
        var (name, at) = await SaveStamp.CreateAsync(
            blobs, stamp => BlobName(kind, id, stamp), json, metadata, clock.GetUtcNow(), ct);
        var save = new SaveRef(kind, id, at, title, name);
        await RecordAsync(save, ActivityAction.Saved, ct);
        return save;
    }

    // Every save under the prefix, undone ones included, newest first.
    private async Task<List<SaveRef>> ListPrefixAsync(DocumentKind kind, string prefix, CancellationToken ct) =>
        (await ListEntriesAsync(kind, prefix, ct)).Select(e => e.Save).ToList();

    // The same, each with the blob's ETag as listed.
    private async Task<List<(SaveRef Save, string ETag)>> ListEntriesAsync(DocumentKind kind, string prefix, CancellationToken ct)
    {
        var saves = new List<(SaveRef Save, string ETag)>();
        await foreach (var blob in blobs.ListAsync(prefix, ct))
        {
            if (TryParse(kind, blob, out var save)) saves.Add((save, blob.ETag));
        }
        return saves.OrderByDescending(s => s.Save.SavedAt).ToList();
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
        // A mark that cannot be read is treated as no mark: the save stays in use and is never swept.
        DateTimeOffset? undone = blob.Metadata.TryGetValue(UndoneKey, out var u)
                                 && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : null;
        // Approvals are the approvals table's, never the blob's: HistoryAsync adds them.
        save = new SaveRef(kind, id, at, title, blob.Name, undone);
        return true;
    }

    private static string Expect(SaveRef save, DocumentKind kind) =>
        save.Kind == kind
            ? save.BlobName
            : throw new ArgumentException($"Expected a {kind} save, got a {save.Kind} save.", nameof(save));
}
