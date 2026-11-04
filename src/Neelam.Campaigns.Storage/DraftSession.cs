namespace Neelam.Campaigns.Storage;

/// <summary>
/// One campaign open in the editor, over a client's <see cref="CampaignStore"/>: what the campaign
/// page does, without the page. A campaign starts from the newest version of one of the client's
/// templates. A save is a new version and never replaces one. Undoing the last save puts the one
/// before it back and marks the undone version for deletion: for the store's grace period it can be
/// restored, here or after coming back to the campaign, and then the sweep deletes it, leaving no
/// record. Leaving the page deletes nothing.
/// </summary>
public sealed class DraftSession : IWatched<SaveRef>
{
    private readonly CampaignStore _store;

    // The draft as last saved or opened, to tell an edit since then; null before the first save.
    private string? _saved;

    // The same, without her label: what an approval is of (ContentOf).
    private string? _savedContent;

    // How many saves of the campaign are in use: undoing the only one takes the campaign off her list.
    private int _versions;

    // The AI proofread kept for Latest, if it has one: the only one the gate reads.
    private ProofreadRecord? _proofread;

    // The LLM report the page shows (Report): Latest's own, or the newest earlier save's, carried across
    // edits and saves, with the words of the version it read. Null when no save in use was proofread.
    private CarriedReport? _report;

    private sealed record CarriedReport(KeptProofread Kept, string VersionWords);

    private DraftSession(
        CampaignStore store, Guid? id, CampaignEditor editor, SaveRef? latest, SaveRef? restorable = null, SaveRef? superseded = null,
        int versions = 0, CarriedReport? report = null)
    {
        _store = store;
        Id = id;
        Editor = editor;
        Latest = latest;
        Restorable = restorable;
        SupersededApproval = superseded;
        _versions = versions;
        Carry(report);
        if (latest is not null) MarkSaved();
    }

    // The report to show, and Latest's own proofread when it is that save's.
    private void Carry(CarriedReport? report)
    {
        _report = report;
        _proofread = report is not null && report.Kept.Save.BlobName == Latest?.BlobName ? report.Kept.Record : null;
    }

    // The newest proofread of the saves in use, with the words of the save it read: Latest's own draft is
    // already open (latestDraft); an earlier save's is read once. One listing, and at most two reads.
    private static async Task<CarriedReport?> ReportAsync(
        CampaignStore store, IReadOnlyList<SaveRef> history, CampaignDraft latestDraft, CancellationToken ct)
    {
        if (await store.NewestProofreadAsync(history, ct) is not { } kept) return null;
        var draft = history.Count > 0 && kept.Save.BlobName == history[0].BlobName ? latestDraft : await store.LoadDraftAsync(kept.Save, ct);
        return new CarriedReport(kept, QuotedText.Of(draft));
    }

    /// <summary>
    /// The LLM report the page shows, or null when no save in use was proofread: the newest save's own
    /// while it has one, otherwise the newest earlier save's, carried forward, each finding marked Live
    /// or LooksFixed against the form as it stands (<see cref="LlmReport"/>). Display only, apart from a
    /// carried Must fix still to look at, which stops the approval (<see cref="CannotApprove"/>).
    /// </summary>
    public LlmReport? Report =>
        _report is { } r ? LlmReport.Marked(r.Kept, r.VersionWords, Editor.Draft, CurrentProofread is not null) : null;

    private void MarkSaved()
    {
        _saved = CampaignJson.SerializeDraft(Editor.Draft);
        _savedContent = ContentOf(Editor.Draft);
    }

    /// <summary>
    /// The draft as an approval sees it: its JSON with the client's label left out, and the template's
    /// id and version (<see cref="CampaignJson.SerializeContent"/>). The label is hers alone, never in
    /// the email, never checked or exported, so changing it changes nothing approved; attaching a
    /// template that changes no block changes nothing approved either (owner, 2026-10-31).
    /// </summary>
    internal static string ContentOf(CampaignDraft draft) => CampaignJson.SerializeContent(draft);

    /// <summary>The campaign's id; null until a new campaign is first saved.</summary>
    public Guid? Id { get; private set; }

    /// <summary>The client whose campaign this is.</summary>
    public ClientName Client => _store.Client;

    public CampaignEditor Editor { get; private set; }

    /// <summary>The newest saved version, or null when there is none.</summary>
    public SaveRef? Latest { get; private set; }

    /// <summary>What the open page watches it under; null before the first save, when there is nothing to watch.</summary>
    public string? Subject => Latest is not null && Id is { } id ? WatchSubject.Campaign(id) : null;

    SaveRef? IWatched<SaveRef>.Held => Latest;

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
    public string UndoQuestion => ChangeWords.Campaign.UndoQuestion(_store.UndoGracePeriod, UndoDeletesTheCampaign);

    /// <summary>
    /// What the page asks before deleting the campaign (<see cref="DeleteAsync"/>): how many versions go,
    /// and where and for how long it can be restored (owner, 2026-10-31: "All good"). A campaign with an
    /// approved version is never deleted (<see cref="CannotDelete"/>), so the question has no approval to
    /// speak of.
    /// </summary>
    public string DeleteQuestion
    {
        get
        {
            var what = _versions > 1 ? $"this campaign and all {_versions} of its versions" : "this campaign";
            return $"Delete {what}? It goes from your campaigns. You can restore it from Recently deleted for " +
                   $"{TimeWords.Period(_store.UndoGracePeriod)}, then it is deleted for good.";
        }
    }

    /// <summary>
    /// Why the campaign cannot be deleted, said on the page in place of Delete, or null when it can: a
    /// version in use carries an approval that stands, the newest's or an earlier one's (owner, 2026-10-31,
    /// "No, these are recorded"). An approval someone withdrew no longer stands, so it keeps nothing.
    /// </summary>
    public string? CannotDelete => KeptAsRecord(Latest, SupersededApproval is not null);

    // Why a campaign is kept, given its newest save in use and whether an earlier one carries an approval
    // that stands; null when neither does. The newest save's approval has a way out on the page, Withdraw
    // approval (owner, 2026-11-01: "Withdrawal of an approval is still a thing, maybe the mail didn't go
    // out"), so the words name it; an earlier version's approval has no such button yet.
    private static string? KeptAsRecord(SaveRef? newest, bool earlierApproved) =>
        newest?.Approval is not null
            ? "This campaign was approved, so it is kept as a record and can't be deleted. " +
              "If the email didn't go out, use Withdraw approval first."
            : earlierApproved
                ? "This campaign was approved, so it is kept as a record and can't be deleted."
                : null;

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
    /// For a client that needs more than one approver: <see cref="Latest"/>'s approvals so far, while the
    /// form still holds exactly what they approved, her label aside, and they are fewer than needed, so it
    /// is not approved yet (<see cref="SaveRef.PartApproval"/>). Null otherwise, and always with one
    /// approver required.
    /// </summary>
    public Approval? CurrentPartApproval =>
        _savedContent is null || Editor.Errors().Count > 0 || ContentOf(Editor.Draft) != _savedContent
            ? null
            : Latest?.PartApproval;

    /// <summary>How many different people must approve a version before it counts as approved; one unless the client's registration says more.</summary>
    public int ApprovalsRequired => _store.ApprovalsRequired;

    /// <summary>
    /// What the Approve card says of a version approved so far by fewer people than the client needs
    /// (owner, 2026-11-03): "Approved by 1 of 2: Priya." Proposed wording, the owner's to change.
    /// </summary>
    public static string PartApprovalWords(Approval soFar, int required) =>
        $"Approved by {soFar.Approvers.Count} of {required}: {soFar.Names}.";

    /// <summary>
    /// What the Approve card says under <see cref="PartApprovalWords"/>: how many more approvals the
    /// version needs before it can be copied into Square. Proposed wording, the owner's to change.
    /// </summary>
    public static string StillNeededWords(Approval soFar, int required)
    {
        int more = required - soFar.Approvers.Count;
        string who = more == 1 ? "one more person" : $"{more} more people";
        return $"It needs approving by {who} before it can be copied into Square.";
    }

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
        // A Must fix the LLM found in an earlier version, still not looking fixed (owner, 2026-10-31,
        // choice 2b): only a fresh proofread of this version, or the fix, lets it through.
        if (CurrentProofread is null && Report?.StopsApproval == true)
            return "The LLM's report on an earlier version has a Must fix that doesn't look fixed yet. Fix it, or proofread this version again.";
        return null;
    }

    /// <summary>
    /// The AI proofread of <see cref="Latest"/>, while the form still holds exactly that save, her
    /// label aside (the label is never in the email, so it is never proofread). Null once anything else
    /// is edited, and for a save nobody has had proofread: as far as the gate goes, the result belongs to
    /// the saved version it read, and any edit voids it (owner, 2026-10-10). What the page shows is
    /// <see cref="Report"/>, which carries it forward across edits and saves (owner, 2026-10-31).
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
        if (Latest is null) return "Save the campaign first: the LLM reads a saved version.";
        if (HasUnsavedChanges) return "Changed since the last save: save it, then proofread that version.";
        if (Editor.Status(policy, business).Review is null) return "Fill in every part first: the LLM reads the whole email.";
        if (CurrentProofread is not null) return "This version already has its LLM report.";
        return null;
    }

    /// <summary>
    /// Has the saved version, as it stands, proofread with the LLM: only when she asks, once per version
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
        // The words of the version read, taken now, while the form is that save.
        var words = QuotedText.Of(Editor.Draft);
        if (!allowance.TryTake(_store.Client.ToString()))
            return new(false, $"Proofreading with the LLM has run {allowance.PerClientPerDay} times for you today, its daily limit. " +
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
            return new(false, "The LLM could not proofread this version. The rules' checks above still stand.");
        }
        var kept = await _store.SaveProofreadAsync(save, new ProofreadRecord(clock.GetUtcNow(), result.Findings, result.Photos), ct);
        if (Latest?.BlobName == save.BlobName) Carry(new CarriedReport(new KeptProofread(save, kept), words));
        return new(true, $"LLM report: {ProofreadSummary(kept)}");
    }

    /// <summary>
    /// What a version's LLM report found, after "LLM report, {when}:" on the page (owner, 2026-10-10,
    /// "LLM report, 10 Oct 2:46 PM: 5 things worth a look."): nothing, or how many to fix and how many
    /// worth a look, the words the list beside it uses.
    /// </summary>
    public static string ProofreadSummary(ProofreadRecord proofread)
    {
        var mustFix = proofread.Findings.Count(f => f.Severity == Severity.Blocker);
        var worthALook = proofread.Findings.Count - mustFix;
        static string Things(int n) => n == 1 ? "1 thing" : $"{n} things";
        return (mustFix, worthALook) switch
        {
            (0, 0) => "nothing to fix in this version.",
            (0, _) => $"{Things(worthALook)} worth a look.",
            (_, 0) => $"{Things(mustFix)} to fix.",
            _ => $"{Things(mustFix)} to fix, {worthALook} worth a look.",
        };
    }

    /// <summary>
    /// What "What the checks say" says first: every finding it lists, the rules' and the AI
    /// proofread's alike, counted by how much it matters (owner, 2026-10-10, option B: "they don't
    /// care too much about the source of the corrections", so one summary, each AI finding tagged in
    /// the list). "Has to be fixed" is the list's "Must fix", "worth a look" its "Worth a look". With
    /// parts still missing, nothing to fix is only so far.
    /// </summary>
    public static string ChecksSummary(IEnumerable<Finding> findings, bool soFar)
    {
        var list = findings.ToList();
        var mustFix = list.Count(f => f.Severity == Severity.Blocker);
        var worthALook = list.Count - mustFix;
        var fix = mustFix switch
        {
            0 => soFar ? "Nothing has to be fixed so far." : "Nothing has to be fixed.",
            1 => "1 thing has to be fixed.",
            _ => $"{mustFix} things have to be fixed.",
        };
        return worthALook switch
        {
            0 => fix,
            1 => $"{fix} 1 thing is worth a look.",
            _ => $"{fix} {worthALook} things are worth a look.",
        };
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
    /// withdraws it. Only while the version on the page is still the campaign's newest in storage, not
    /// undone, with its approval as the page shows it (<see cref="ChangeSince"/>): read again here, at
    /// the click, since another tab or person may have saved, undone or approved since the page opened.
    /// </summary>
    /// <remarks>
    /// For a client that needs more than one approver, each different person's approval is added to the
    /// version's, and it is approved once there are as many as needed (<see cref="ApprovalsRequired"/>):
    /// until then the result is the approvals so far, which <see cref="CurrentPartApproval"/> also gives.
    /// </remarks>
    /// <returns>The version's approval, or with more approvers still needed, its approvals so far.</returns>
    /// <exception cref="InvalidOperationException">It cannot be approved; <see cref="CannotApprove"/> says why. Or this person has already approved it and it needs someone else.</exception>
    /// <exception cref="CampaignChangedException">Storage has changed since the page's version; nothing is approved.</exception>
    public async Task<Approval> ApproveAsync(
        string approvedBy, CampaignPolicy? policy = null, BusinessContext? business = null, CancellationToken ct = default)
    {
        if (CannotApprove(policy, business) is { } why) throw new InvalidOperationException(why);
        if (ChangeSince(await NewestAsync(ct)) is var change and not CampaignChange.None)
            throw new CampaignChangedException(change, ChangeWords.Campaign.NotApproved(change));
        Latest = await _store.ApproveAsync(Latest!, approvedBy, ct);
        SupersededApproval = null;
        Raise();
        return Latest.Approval ?? Latest.PartApproval!;
    }

    /// <summary>
    /// Withdraws the approval of the saved version, or its approvals so far when it needs more; the
    /// campaign stays as it is.
    /// </summary>
    public async Task WithdrawApprovalAsync(CancellationToken ct = default)
    {
        if (Latest?.Approval is null && Latest?.PartApproval is null) throw new InvalidOperationException("There is no approval to withdraw.");
        Latest = await _store.WithdrawApprovalAsync(Latest, ct);
        Raise();
    }

    /// <summary>
    /// The campaign's newest save in use as storage has it now, with its approval; null when it has
    /// none in use, or before its first save. One listing and one query (<see cref="CampaignStore.NewestAsync"/>).
    /// </summary>
    public async Task<SaveRef?> NewestAsync(CancellationToken ct = default) =>
        Id is { } id ? await _store.NewestAsync(DocumentKind.Draft, id, ct) : null;

    /// <summary>
    /// What changed in storage since the version this session holds, given the newest save in use as
    /// storage has it (<see cref="NewestAsync"/>): saved again, the version undone or every save undone,
    /// its approval withdrawn, or approved by someone else; <see cref="CampaignChange.None"/> when it is
    /// still the newest with the approval it shows, and before the first save, when there is nothing
    /// to be out of date. Unsaved edits are the page's own and never count.
    /// </summary>
    public CampaignChange ChangeSince(SaveRef? newest)
    {
        if (Latest is null) return CampaignChange.None;
        if (newest is null) return CampaignChange.Deleted;
        if (newest.BlobName != Latest.BlobName)
            return newest.SavedAt > Latest.SavedAt ? CampaignChange.SavedSince : CampaignChange.Undone;
        if (SameApproval(newest.Approval, Latest.Approval) && SameApproval(newest.PartApproval, Latest.PartApproval))
            return CampaignChange.None;
        // With more than one approver needed, approvals so far count too: fewer, or none, is withdrawn.
        Approval? now = newest.Approval ?? newest.PartApproval;
        Approval? held = Latest.Approval ?? Latest.PartApproval;
        return now is null || (held is not null && now.Approvers.Count < held.Approvers.Count)
            ? CampaignChange.ApprovalWithdrawn
            : CampaignChange.Approved;
    }

    // The same people at the same moments. Storage may keep a time coarser than the clock gave it, so
    // within a second is the same: two approvals by one name inside a second change nothing she sees.
    private static bool SameApproval(Approval? stored, Approval? held) =>
        stored is null
            ? held is null
            : held is not null && SameApprovers(stored.Approvers, held.Approvers);

    private static bool SameApprovers(IReadOnlyList<Approver> stored, IReadOnlyList<Approver> held) =>
        stored.Count == held.Count
        && stored.Zip(held).All(pair => pair.First.By == pair.Second.By
                                        && Math.Abs((pair.First.At - pair.Second.At).Ticks) < TimeSpan.TicksPerSecond);

    /// <summary>What the page says at its top once it knows storage has changed (<see cref="ChangeWords.Campaign"/>).</summary>
    public static string ChangedNotice(CampaignChange change) => ChangeWords.Campaign.Notice(change);

    /// <summary>
    /// "Keep mine" (owner, 2026-10-11: "what if the person wants to overwrite anyway?" ... "yes, add Keep
    /// mine"): saves what the page holds, edited or not, as a new version, the campaign's newest. Nothing
    /// is deleted or undone: the version it takes the place of stays stored, in the campaign's history.
    /// No approval goes with it, neither the other version's nor her own earlier one's: after someone
    /// else's change, what she keeps is approved afresh. The other pages are told, as for any save.
    /// </summary>
    /// <returns>Her new save, and the newest version storage had at the click, which hers now follows.</returns>
    /// <exception cref="InvalidOperationException">There is no saved campaign, or the form cannot be saved as it stands.</exception>
    public async Task<KeptMine> KeepMineAsync(CancellationToken ct = default)
    {
        if (Latest is null || Id is not { } id) throw new InvalidOperationException("There is no saved version to keep.");
        if (Editor.Errors().Count > 0) throw new InvalidOperationException("Fix what is marked first: the campaign cannot be saved as it stands.");
        var before = Latest;
        // Read at the click: whatever is newest now, even a save that landed after the notice, is what
        // hers follows, and the page says which.
        var history = await _store.HistoryAsync(DocumentKind.Draft, id, ct);
        var replaced = history.FirstOrDefault() is { } newest && newest.BlobName != before.BlobName ? newest : null;
        Latest = await _store.SaveDraftAsync(id, Editor.Title, Editor.Draft, ct);
        try
        {
            // Her new save has no proofread; the page's report is carried forward (Report).
            _proofread = null;
            _versions = history.Count + 1;
            MarkSaved();
            Restorable = null;
            SupersededApproval = replaced?.Approval is not null ? replaced : before.Approval is not null ? before : null;
            return new KeptMine(Latest, replaced, before.Approval);
        }
        finally
        {
            Raise();
        }
    }

    /// <summary>
    /// What the page says once "Keep mine" has saved: when, the version it took the place of as the
    /// newest (read at the click, so a save that landed after the notice is the one named), and that no
    /// approval came with it. Proposed wording, the owner's to change.
    /// </summary>
    public static string KeptMessage(KeptMine kept, LocalTime times)
    {
        var message = $"Saved at {times.TimeOfDay(kept.Saved.SavedAt)} as the newest version";
        message += kept.Replaced is { } replaced
            ? $", in place of the one saved {times.DateAndTime(replaced.SavedAt)}, which stays in the campaign's history."
            : ".";
        if (kept.Replaced?.Approval is { } theirs)
            message += $" Its approval by {theirs.By} does not carry over: approve this version before you copy it into Square.";
        else if (kept.HadApproval is not null)
            message += " This version is not approved yet.";
        return message;
    }

    // Tells the other pages open on this instance that this campaign changed; never this session's own.
    private void Raise()
    {
        if (Id is { } id) _store.Signals?.Raise(_store.Client, id, this);
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

    /// <summary>
    /// A new, unsaved campaign from a template's newest version, or null when the template has no saves.
    /// The campaign records the template and that version (2026-10-31).
    /// </summary>
    public static async Task<DraftSession?> StartAsync(CampaignStore store, Guid templateId, CancellationToken ct = default)
    {
        var template = (await store.HistoryAsync(DocumentKind.Template, templateId, ct)).FirstOrDefault();
        return template is null
            ? null
            : new DraftSession(store, null,
                CampaignEditor.Start(await store.LoadTemplateAsync(template, ct), templateId, template.SavedAt), null);
    }

    /// <summary>
    /// The client's templates, the newest version of each, by name as the Templates list orders them:
    /// what the campaign page's Template picker offers. One listing, and no template is read.
    /// </summary>
    public async Task<IReadOnlyList<SaveRef>> TemplatesAsync(CancellationToken ct = default) =>
        (await _store.LatestAsync(DocumentKind.Template, ct))
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>
    /// Where the campaign on the page stands with its template, given her templates as
    /// <see cref="TemplatesAsync"/> listed them: none attached, the version it records still the
    /// newest, a newer one saved (or the one it records undone), or the template gone.
    /// </summary>
    public TemplateStanding TemplateStatus(IReadOnlyList<SaveRef> templates)
    {
        if (Editor.Draft.TemplateId is not { } id) return TemplateStanding.None;
        var newest = templates.FirstOrDefault(t => t.Id == id);
        if (newest is null) return TemplateStanding.Gone;
        return newest.SavedAt == Editor.Draft.TemplateVersion ? TemplateStanding.Current : TemplateStanding.Changed;
    }

    /// <summary>
    /// The name of the template the campaign is attached to, as that template's newest version has it
    /// now, from her templates as <see cref="TemplatesAsync"/> listed them; null with none attached, or
    /// when that template no longer exists. The campaign refers to its template by id alone (owner,
    /// 2026-11-01: "renaming a template should not change the campaigns. We should use an ID instead of
    /// the name"): the name it saved is never shown or matched on.
    /// </summary>
    public string? TemplateNameNow(IReadOnlyList<SaveRef> templates) =>
        Editor.Draft.TemplateId is { } id ? templates.FirstOrDefault(t => t.Id == id)?.Title : null;

    /// <summary>
    /// The newest version of one of her templates, read now, and what using it would do to the form as
    /// it stands; null when the template has no version in use. Changes nothing.
    /// </summary>
    public async Task<TemplateChoice?> ChooseTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var history = await _store.HistoryAsync(DocumentKind.Template, templateId, ct);
        if (history.FirstOrDefault() is not { } version) return null;
        var template = await _store.LoadTemplateAsync(version, ct);
        var madeFrom = await MadeFromAsync(template, templateId, history, ct);
        return new TemplateChoice(version, template,
            TemplateUpdate.Of(Editor.Draft, template, templateId, version.SavedAt, madeFrom, StandingApproval), madeFrom);
    }

    // Whether the version on the page carries an approval that stands: then the update leaves the content
    // a template starts a campaign with as she has it (owner, 2026-11-03, "not approved": "That's exactly
    // what it means to me"). Latest's approval, as Delete reads it, whether or not the form has changed since.
    private bool StandingApproval => Latest?.Approval is not null;

    // The version of a template the campaign records, read, so the update can tell content it started her
    // blocks with from content of her own (TemplateUpdate): only when the chosen template gives some block
    // content to start with, so no other choice reads anything more. Null when the campaign records none,
    // or that version is no longer in use (undone, or swept): her blocks then keep what they hold.
    private async Task<CampaignTemplate?> MadeFromAsync(
        CampaignTemplate chosen, Guid chosenId, IReadOnlyList<SaveRef> chosenHistory, CancellationToken ct)
    {
        if (!chosen.Blocks.Any(b => b.Default is not null)) return null;
        if (Editor.Draft.TemplateId is not { } id || Editor.Draft.TemplateVersion is not { } at) return null;
        var history = id == chosenId ? chosenHistory : await _store.HistoryAsync(DocumentKind.Template, id, ct);
        if (history.FirstOrDefault(s => s.SavedAt == at) is not { } save) return null;
        return id == chosenId && save.BlobName == chosenHistory[0].BlobName ? chosen : await _store.LoadTemplateAsync(save, ct);
    }

    /// <summary>
    /// Puts a template's version into the form (<see cref="TemplateUpdate"/>), worked out again against
    /// the form as it stands now, and saves nothing: she saves as usual, and leaving without saving keeps
    /// the campaign as it was. When that would lose something she wrote, it is applied only once she has
    /// said yes (<paramref name="confirmed"/>) to <paramref name="choice"/>'s question and nothing beyond
    /// what it named would be lost.
    /// </summary>
    /// <returns>Null when applied; otherwise the choice with what would be lost now, to ask her about.</returns>
    /// <exception cref="InvalidOperationException">The form holds typed text the draft cannot hold yet, which this would lose.</exception>
    public TemplateChoice? UseTemplate(TemplateChoice choice, bool confirmed = false)
    {
        if (Editor.Errors().Count > 0)
            throw new InvalidOperationException("Fix what is marked first: using the template would lose it.");
        var update = TemplateUpdate.Of(
            Editor.Draft, choice.Template, choice.Version.Id, choice.Version.SavedAt, choice.MadeFrom, StandingApproval);
        var agreed = confirmed
                     && update.Dropped.All(choice.Update.Dropped.Contains)
                     && update.Replaced.All(choice.Update.Replaced.Contains);
        if (update.LosesAnything && !agreed) return choice with { Update = update };
        Editor = CampaignEditor.Open(update.Draft);
        return null;
    }

    /// <summary>
    /// The newest version of a campaign, or null when it has none in use. A campaign whose every
    /// save was undone is null here; <see cref="RestorableAsync"/> says whether it can come back.
    /// </summary>
    public static async Task<DraftSession?> OpenAsync(CampaignStore store, Guid id, CancellationToken ct = default)
    {
        var history = await store.HistoryAsync(DocumentKind.Draft, id, ct);
        var latest = history.FirstOrDefault();
        if (latest is null) return null;
        var draft = await store.LoadDraftAsync(latest, ct);
        return new DraftSession(store, id, CampaignEditor.Open(draft), latest,
            await store.RestorableAsync(DocumentKind.Draft, id, ct), Superseded(history), history.Count,
            await ReportAsync(store, history, draft, ct));
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
    public static async Task<DraftSession?> RestoreAsync(CampaignStore store, SaveRef undone, CancellationToken ct = default)
    {
        if (!await store.RestoreAsync(undone, ct)) return null;
        // Restored from a page with no session open on it: every open page is told.
        store.Signals?.Raise(store.Client, undone.Id, source: null);
        return await OpenAsync(store, undone.Id, ct);
    }

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
        // Approvals so far, for a client needing more than one, go with it the same way.
        var keeps = before?.Approval is not null && CurrentApproval == before.Approval ? before
            : before?.PartApproval is not null && CurrentPartApproval == before.PartApproval ? before
            : null;
        Latest = await _store.SaveDraftAsync(Id.Value, Editor.Title, Editor.Draft, ct);
        try
        {
            // A new version, which nobody has had proofread yet. The report shown stays, carried
            // forward as the earlier version's (Report), so the page keeps what is still to look at.
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
        finally
        {
            // Saved, whatever happened to the approval it carries: the other pages are told.
            Raise();
        }
    }

    /// <summary>
    /// Undoes the newest version and opens the one before it, discarding any unsaved changes. The
    /// undone version is marked for deletion and stays restorable for the grace period. False when
    /// no version is left in use: the campaign is gone from the list, and <see cref="Restorable"/>
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
            throw new CampaignChangedException(change, ChangeWords.Campaign.NotUndone(change));
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
    /// Deletes the whole campaign, whatever its number of versions (Priya, 2026-10-31: "We need a delete
    /// for campaigns - it should be able to be undone"): every version in use is undone at one moment
    /// (<see cref="CampaignStore.DeleteAsync"/>), any unsaved changes are discarded, and the campaign goes
    /// from her list. <see cref="Restorable"/> then brings it back whole for the grace period, here or from
    /// Recently deleted; after it the sweep deletes it. Every version goes, a version saved in another tab
    /// since this page opened included, since all of it comes back together (owner, 2026-11-01: "Allow it,
    /// but not if it has been approved"). A campaign with a version whose approval stands is kept: decided
    /// from storage as it is at the click, every version in use included, those this page has not seen,
    /// never from what the page holds. An approval someone withdrew keeps nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no saved campaign, or a version of it is approved.</exception>
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        if (Latest is null || Id is null)
            throw new InvalidOperationException("There is no saved campaign to delete.");
        var history = await _store.HistoryAsync(DocumentKind.Draft, Id.Value, ct);
        if (KeptAsRecord(history.FirstOrDefault(), history.Skip(1).Any(s => s.Approval is not null)) is { } why)
            throw new InvalidOperationException(why);
        await _store.DeleteAsync(DocumentKind.Draft, Id.Value, ct);
        try
        {
            await ReopenAsync(ct);
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
            Restorable = await _store.RestorableAsync(DocumentKind.Draft, Id!.Value, ct);
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
            Carry(null);
            return false;
        }
        var draft = await _store.LoadDraftAsync(Latest, ct);
        Editor = CampaignEditor.Open(draft);
        Carry(await ReportAsync(_store, history, draft, ct));
        MarkSaved();
        return true;
    }
}

/// <summary>A campaign as its client's list shows it: its newest version, and her label for it.</summary>
/// <param name="Save">The newest version in use; its title is the subject, from the blob's metadata as before.</param>
/// <param name="Label">The client's own label, read from that version's JSON; null when it has none.</param>
public sealed record CampaignListing(SaveRef Save, string? Label);

/// <summary>What "Keep mine" did.</summary>
/// <param name="Saved">Her new save, now the campaign's newest.</param>
/// <param name="Replaced">The version that was newest at the click and no longer is; it stays stored. Null when hers was still the newest.</param>
/// <param name="HadApproval">The approval the page's own version had before, which does not go with the new save.</param>
public sealed record KeptMine(SaveRef Saved, SaveRef? Replaced, Approval? HadApproval);

/// <summary>Where a campaign stands with its template (<see cref="DraftSession.TemplateStatus"/>).</summary>
public enum TemplateStanding
{
    /// <summary>No template attached: saved before campaigns recorded theirs (2026-10-31).</summary>
    None,

    /// <summary>The version it records is the template's newest.</summary>
    Current,

    /// <summary>The template has a newer version, or the version it records was undone.</summary>
    Changed,

    /// <summary>The template has no version in use: every save undone, or swept.</summary>
    Gone,
}

/// <summary>A template version she chose for the campaign, and what using it would do.</summary>
/// <param name="Version">The template's newest version when chosen.</param>
/// <param name="Template">That version, read.</param>
/// <param name="Update">What it would do to the form: what would be lost is what she is asked about.</param>
/// <param name="MadeFrom">
/// The version of a template the campaign recorded when this was chosen, read, to tell content a
/// template started her blocks with from her own; null when not needed or not known.
/// </param>
public sealed record TemplateChoice(SaveRef Version, CampaignTemplate Template, TemplateUpdate Update, CampaignTemplate? MadeFrom = null);

/// <summary>What came of asking for the AI proofread, in words for her: done, or why not.</summary>
/// <param name="Done">True when the proofread ran and its result is kept with the version.</param>
public sealed record ProofreadAttempt(bool Done, string Message);
