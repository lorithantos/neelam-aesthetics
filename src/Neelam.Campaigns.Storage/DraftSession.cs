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

    // The same, without her label: what an approval is of (ContentOf).
    private string? _savedContent;

    // How many saves of the campaign are in use: undoing the only one takes the campaign off her list.
    private int _versions;

    // The AI proofread kept for Latest, if it has one.
    private ProofreadRecord? _proofread;

    private DraftSession(
        CampaignStore store, Guid? id, CampaignEditor editor, SaveRef? latest, SaveRef? restorable = null, SaveRef? superseded = null,
        int versions = 0, ProofreadRecord? proofread = null)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
        Restorable = restorable;
        SupersededApproval = superseded;
        _versions = versions;
        _proofread = proofread;
        if (latest is not null) MarkSaved();
    }

    private void MarkSaved()
    {
        _saved = CampaignJson.SerializeDraft(Editor.Draft);
        _savedContent = ContentOf(Editor.Draft);
    }

    /// <summary>
    /// The draft as an approval sees it: its JSON with the client's label left out. The label is hers
    /// alone, never in the email, never checked or exported, so changing it changes nothing approved.
    /// </summary>
    internal static string ContentOf(CampaignDraft draft)
    {
        var label = draft.Label;
        draft.Label = null;
        try
        {
            return CampaignJson.SerializeDraft(draft);
        }
        finally
        {
            draft.Label = label;
        }
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

    /// <summary>
    /// True when undoing the last save leaves no version in use: the campaign goes from her list,
    /// restorable for the grace period, so the page calls it deleting.
    /// </summary>
    public bool UndoDeletesTheCampaign => Latest is not null && _versions <= 1;

    /// <summary>
    /// What the page asks before an undo, with the grace period in words. When the save is the only
    /// one, there is no version before it to come back: the campaign is deleted, and can be restored.
    /// </summary>
    public string UndoQuestion
    {
        get
        {
            var period = TimeWords.Period(_store.UndoGracePeriod);
            return UndoDeletesTheCampaign
                ? $"This deletes the campaign. You can restore it for {period}."
                : "Undo the last save? The version before it comes back, and any changes not saved are lost. " +
                  $"The undone version can be restored for {period}, then it is deleted for good.";
        }
    }

    /// <summary>True when the form holds anything not in <see cref="Latest"/>, or nothing is saved yet.</summary>
    public bool HasUnsavedChanges =>
        _saved is null || Editor.Errors().Count > 0 || CampaignJson.SerializeDraft(Editor.Draft) != _saved;

    /// <summary>
    /// The approval in force: <see cref="Latest"/>'s, while the form still holds exactly what was
    /// approved, her label aside. Null once anything else is edited or saved since, and when nobody
    /// approved it.
    /// </summary>
    public Approval? CurrentApproval =>
        _savedContent is null || Editor.Errors().Count > 0 || ContentOf(Editor.Draft) != _savedContent
            ? null
            : Latest?.Approval;

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
        if (status.Review is null || status.Review.Blockers.Any()
            || CurrentProofread?.Findings.Any(f => f.Severity == Severity.Blocker) == true)
            return "Fix everything marked Must fix first.";
        return null;
    }

    /// <summary>
    /// The AI proofread of <see cref="Latest"/>, while the form still holds exactly that save, her
    /// label aside (the label is never in the email, so it is never proofread). Null once anything else
    /// is edited, and for a save nobody has had proofread: the result belongs to the saved version it
    /// read, and any edit voids it (owner, 2026-10-10).
    /// </summary>
    public ProofreadRecord? CurrentProofread =>
        _proofread is null || _savedContent is null || Editor.Errors().Count > 0 || ContentOf(Editor.Draft) != _savedContent
            ? null
            : _proofread;

    /// <summary>
    /// Why the saved version cannot be proofread as it stands, or null when it can: saved, unchanged
    /// since, built with no part missing, and not proofread already.
    /// </summary>
    public string? CannotProofread(CampaignPolicy? policy = null, BusinessContext? business = null)
    {
        if (Latest is null) return "Save the campaign first: the AI proofread reads a saved version.";
        if (HasUnsavedChanges) return "Changed since the last save: save it, then the proofread reads that version.";
        if (Editor.Status(policy, business).Review is null) return "Fill in every part first: the AI proofread reads the whole email.";
        if (CurrentProofread is not null) return "This version is proofread.";
        return null;
    }

    /// <summary>
    /// Has the saved version, as it stands, proofread by AI: only when she asks, once per version
    /// (owner, 2026-10-10: on her click, never per keystroke or by itself). The result is kept beside
    /// the save (<see cref="CampaignStore.SaveProofreadAsync"/>) and its findings join the rules'
    /// (<see cref="DemoExportReport"/>). Never throws for the proofread itself: a refusal, a failure
    /// or today's limit comes back as what to tell her, and the rules' checks stand as they are.
    /// </summary>
    /// <param name="allowance">Today's proofreads for this client, taken before the call.</param>
    public async Task<ProofreadAttempt> ProofreadAsync(
        IProofreader proofreader, DailyProofreadAllowance allowance, TimeProvider clock,
        CampaignPolicy? policy = null, BusinessContext? business = null, CancellationToken ct = default)
    {
        if (CannotProofread(policy, business) is { } why) return new(false, why);
        // The version read, held now: the result is that save's, whatever happens on the page meanwhile.
        var save = Latest!;
        var campaign = Editor.Status(policy, business).Review!.Campaign;
        if (!allowance.TryTake(_store.Client.ToString()))
            return new(false, $"The AI proofread has run {allowance.PerClientPerDay} times for you today, its daily limit. " +
                "It can run again tomorrow; the rules' checks above still stand.");
        ProofreadResult result;
        try
        {
            result = await proofreader.ProofreadAsync(campaign, business, ct);
        }
        catch (ProofreadUnavailableException ex)
        {
            return new(false, $"{ex.Message} The rules' checks above still stand.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not one the proofreader words for her: its message could say anything, so it is not shown.
            return new(false, "The AI proofread could not run. The rules' checks above still stand.");
        }
        var kept = await _store.SaveProofreadAsync(save, new ProofreadRecord(clock.GetUtcNow(), result.Findings, result.Photos), ct);
        if (Latest?.BlobName == save.BlobName) _proofread = kept;
        return new(true, ProofreadSummary(kept));
    }

    /// <summary>What the page says of a version's proofread: whether it found anything, and how much.</summary>
    public static string ProofreadSummary(ProofreadRecord proofread)
    {
        var count = proofread.Findings.Count;
        return count == 0
            ? "The AI proofread found nothing to fix in this version."
            : $"The AI proofread found {(count == 1 ? "1 thing" : $"{count} things")} in this version, listed with the rules' findings.";
    }

    /// <summary>
    /// The demo's export report for the campaign as it stands, or null when it has none: approved
    /// and unchanged since (<see cref="CurrentApproval"/>, never <see cref="Latest"/>'s approval,
    /// which an unsaved edit leaves in place), built with no part missing, and passed by the rules
    /// through <see cref="CampaignGate.DemoReview"/>, so it can export. Whether the site is the demo
    /// and the person may review is the page's to decide.
    /// </summary>
    public ReviewReport? DemoExportReport(CampaignPolicy? policy = null, BusinessContext? business = null)
    {
        var report = DemoReview(policy, business);
        return report?.CanExport == true ? report : null;
    }

    /// <summary>
    /// The "Worth a look" findings to show before the demo export, or none: what stands between an
    /// approved campaign, unchanged since and with nothing to fix, and its export. Only those nobody
    /// has been shown at export for this campaign, on this version or an earlier one
    /// (<see cref="ReviewReport.UnseenWarnings"/>), so after she has gone on once, a warning that
    /// appears later, or a new save adds or rewords, is listed alone. In the order the checks
    /// give. Warnings never stop the email (owner, 2026-10-09): going on is
    /// <see cref="WarningsSeenAtExportAsync"/>, one click, and fixing one is an edit like any other.
    /// </summary>
    public IReadOnlyList<Finding> WarningsBeforeExport(CampaignPolicy? policy = null, BusinessContext? business = null) =>
        DemoReview(policy, business) is { WarningsToSee: true } report ? report.UnseenWarnings.ToList() : [];

    /// <summary>
    /// What the list of <see cref="WarningsBeforeExport"/> says first: how many, whether they are new
    /// since she last went on past this campaign's warnings, and that none of them stops the email,
    /// each sentence agreeing with the count ("It doesn't stop the email" for one).
    /// </summary>
    public static string WarningsLead(int count, bool sinceLastExport)
    {
        var one = count == 1;
        var howMany = sinceLastExport
            ? (one ? "1 new thing is" : $"{count} new things are") + " worth a look since you last exported."
            : (one ? "One thing is" : $"{count} things are") + " worth a look.";
        return one
            ? $"{howMany} It doesn't stop the email. If it is a mistake, go to it and fix it; if it is right as written, carry on."
            : $"{howMany} None of them stops the email. If one is a mistake, go to it and fix it; if it is right as written, carry on.";
    }

    /// <summary>
    /// Goes on to export past this version's "Worth a look" findings as they stand: recorded with its
    /// approval, who and when and each finding's key, so they are not shown again for this campaign
    /// while they stay as they are, on this version or a later one (owner, 2026-10-09: "yes, carry
    /// across saves"). Give the same policy and business as for <see cref="WarningsBeforeExport"/>, so the findings
    /// recorded are the ones shown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The version as it stands is not approved.</exception>
    public async Task<WarningsSeen> WarningsSeenAtExportAsync(
        CampaignPolicy? policy = null, BusinessContext? business = null, CancellationToken ct = default)
    {
        if (CurrentApproval is null || Latest?.Approval is null)
            throw new InvalidOperationException("Approve this version first: export is of an approved version.");
        var shown = (DemoReview(policy, business)?.Warnings ?? []).Select(w => w.SeenKey).ToList();
        Latest = await _store.WarningsSeenAtExportAsync(Latest, shown, ct);
        return Latest.Approval!.WarningsSeen!;
    }

    // The demo's review of the campaign as it stands, approved and unchanged since; null otherwise.
    private ReviewReport? DemoReview(CampaignPolicy? policy, BusinessContext? business)
    {
        if (CurrentApproval is not { } approval) return null;
        var review = Editor.Status(policy, business).Review;
        return review is null ? null : CampaignGate.DemoReview(review.Campaign, approval, policy, business, CurrentProofread?.Findings);
    }

    /// <summary>
    /// What the page says of a save, in the Approve panel's terms. The approval is the one the save
    /// carries (<paramref name="saved"/>'s, kept when only her label changed), so a save that kept it
    /// says it is still approved; one that left an approval behind says it is not approved yet; any
    /// other save says nothing of approval.
    /// </summary>
    /// <param name="before">The newest save before this one, or null when there was none.</param>
    /// <param name="saved">The save just made, as <see cref="SaveAsync"/> returned it.</param>
    /// <param name="times">How the page shows a time: in the client's zone.</param>
    public static string SavedMessage(SaveRef? before, SaveRef saved, LocalTime times) =>
        $"Saved at {times.TimeOfDay(saved.SavedAt)}." + (saved.Approval is { } kept
            ? $" It is still approved by {kept.By}."
            : before?.Approval is not null ? " This version is not approved yet." : "");

    /// <summary>
    /// Approves the saved version as it stands, in the approver's name, now. Recorded in the approvals
    /// table against that one save, so a later edit or save is not approved, and undoing the save
    /// withdraws it.
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

    /// <summary>
    /// <see cref="ListAsync"/>, each campaign with the client's label for it. The label lives only in
    /// the draft's JSON, so this reads each listed version once, all at the same time: one read per
    /// campaign, from this client's store alone. Undone versions are never listed, so their labels
    /// are never read.
    /// </summary>
    public static async Task<IReadOnlyList<CampaignListing>> ListLabelledAsync(CampaignStore store, CancellationToken ct = default)
    {
        var latest = await ListAsync(store, ct);
        var drafts = await Task.WhenAll(latest.Select(save => store.LoadDraftAsync(save, ct)));
        return latest.Zip(drafts, (save, draft) => new CampaignListing(save, draft.Label)).ToList();
    }

    /// <summary>
    /// The client's campaigns whose every save was undone and that can still be restored, each as the
    /// save Restore brings back, most recently deleted first. Read from the blob names and marks alone:
    /// nothing in a deleted campaign's JSON is read.
    /// </summary>
    public static Task<IReadOnlyList<SaveRef>> RecentlyDeletedAsync(CampaignStore store, CancellationToken ct = default) =>
        store.RecentlyDeletedAsync(DocumentKind.Draft, ct);

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
                await store.RestorableAsync(DocumentKind.Draft, id, ct), Superseded(history), history.Count,
                await store.ProofreadOfAsync(latest, ct));
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
        // Only her label changed since an approved save: what was approved is what is saved, so the
        // approval goes with it to the new save.
        var keeps = before?.Approval is not null && CurrentApproval == before.Approval ? before : null;
        Latest = await _store.SaveDraftAsync(Id.Value, Editor.Title, Editor.Draft, ct);
        // A new version, which nobody has had proofread yet.
        _proofread = null;
        _versions++;
        MarkSaved();
        // Saved over: an undone version below the new one is no longer offered back.
        Restorable = null;
        if (keeps is not null)
            Latest = await _store.KeepApprovalAsync(keeps, Latest, ct);
        // Otherwise a new version is not approved, whatever the one before it was.
        else if (before?.Approval is not null)
            SupersededApproval = before;
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
        _versions = history.Count;
        SupersededApproval = Superseded(history);
        Restorable = await _store.RestorableAsync(DocumentKind.Draft, Id.Value, ct);
        if (Latest is null)
        {
            _saved = _savedContent = null;
            _proofread = null;
            return false;
        }
        Editor = CampaignEditor.Open(await _store.LoadDraftAsync(Latest, ct));
        _proofread = await _store.ProofreadOfAsync(Latest, ct);
        MarkSaved();
        return true;
    }
}

/// <summary>A campaign as its client's list shows it: its newest version, and her label for it.</summary>
/// <param name="Save">The newest version in use; its title is the subject, from the blob's metadata as before.</param>
/// <param name="Label">The client's own label, read from that version's JSON; null when it has none.</param>
public sealed record CampaignListing(SaveRef Save, string? Label);

/// <summary>What came of asking for the AI proofread, in words for her: done, or why not.</summary>
/// <param name="Done">True when the proofread ran and its result is kept with the version.</param>
public sealed record ProofreadAttempt(bool Done, string Message);
