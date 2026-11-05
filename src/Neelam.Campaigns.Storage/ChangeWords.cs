namespace Neelam.Campaigns.Storage;

/// <summary>
/// What a page says when storage changed under it, and what it asks before an undo: one set of sentence
/// frames for every page, each subject's own phrases as data (<see cref="Campaign"/>, <see cref="Template"/>,
/// <see cref="Look"/>, <see cref="Baseline"/>, <see cref="Ladders"/>). The wording is the owner's, approved
/// on 2026-10-11 (WIP "Concurrent edits": phase 1, every sentence; phase 2, "Go on"); the frames were
/// three copies until 2026-11-03.
/// </summary>
public sealed class ChangeWords
{
    // The document, for one with saves of its own ("the campaign was saved again"); null for hers, which
    // are only ever "changed".
    private readonly string? _noun;

    // What the page holds, when an undo of it is told apart ("the version on this page was undone").
    private readonly string? _held;

    // Her document and its verb, for every change but an undo told apart ("your look was changed").
    private readonly string? _changed;

    private readonly bool _approvals;

    // Added to the notice when the document was deleted: where to get it back.
    private readonly string? _deletedHint;

    // What an undo brings back, for the undo question; null where the page has no undo.
    private readonly string? _goesBack;

    private ChangeWords(
        string? noun = null, string? held = null, string? changed = null, bool approvals = false, string? deletedHint = null,
        string? goesBack = null)
    {
        _noun = noun;
        _held = held;
        _changed = changed;
        _approvals = approvals;
        _deletedHint = deletedHint;
        _goesBack = goesBack;
    }

    private const string VersionGoesBack = "The version before it comes back, and any changes not saved are lost.";

    public static readonly ChangeWords Campaign = new(
        noun: "campaign", held: "version", approvals: true,
        deletedHint: "You can restore it from Recently deleted in your campaigns.", goesBack: VersionGoesBack);

    public static readonly ChangeWords Template = new(
        noun: "template", held: "version", deletedHint: "Load the latest to restore it.", goesBack: VersionGoesBack);

    public static readonly ChangeWords Look = new(
        held: "look", changed: "your look was", goesBack: "Your pages go back to the look before it.");

    public static readonly ChangeWords Baseline = new(changed: "the parts every template should have were");

    public static readonly ChangeWords Ladders = new(changed: "your tier name ladders were");

    /// <summary>
    /// When another tab on the template or the look has done something since this page opened (EditingTabs):
    /// the template and Look pages' notice. They keep no working copy, so what is kept is the last save; the
    /// campaign page's own is <see cref="WorkingCopyWords.OtherTab"/>. A draft for Priya (2026-11-05).
    /// </summary>
    public const string OtherTab = "Someone is also editing this in another tab. Whichever tab saves last is what's kept.";

    /// <summary>The words for one of her own documents, by what a page watches it under (<see cref="WatchSubject"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of her documents.</exception>
    public static ChangeWords For(string subject) => subject switch
    {
        WatchSubject.Look => Look,
        WatchSubject.Baseline => Baseline,
        WatchSubject.Ladders => Ladders,
        _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, "Not one of her documents."),
    };

    /// <summary>
    /// Why the page is out of date, in words that finish "Not undone: ..." and the like; null for
    /// <see cref="CampaignChange.None"/>, and for a change this subject cannot have.
    /// </summary>
    public string? WhatChanged(CampaignChange change) => change switch
    {
        CampaignChange.None => null,
        CampaignChange.Undone when _held is not null => $"the {_held} on this page was undone since you opened it",
        _ when _changed is not null => $"{_changed} changed since you opened this page",
        CampaignChange.SavedSince => $"the {_noun} was saved again since you opened it",
        CampaignChange.Deleted => $"the {_noun} was deleted since you opened it",
        CampaignChange.ApprovalWithdrawn when _approvals => "its approval was withdrawn since you opened it",
        CampaignChange.Approved when _approvals => "its approval changed since you opened it",
        _ => null,
    };

    /// <summary>What the page says at the top of its notice once it knows storage has changed: what, and that it was another tab or person.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Nothing changed.</exception>
    public string Notice(CampaignChange change)
    {
        var what = WhatChanged(change) ?? throw new ArgumentOutOfRangeException(nameof(change), "Nothing changed.");
        var notice = $"{char.ToUpperInvariant(what[0])}{what[1..]}, in another tab or by someone else.";
        return change == CampaignChange.Deleted && _deletedHint is not null ? $"{notice} {_deletedHint}" : notice;
    }

    /// <summary>Said when an undo is refused because the page is out of date.</summary>
    public string NotUndone(CampaignChange change) =>
        $"Not undone: {WhatChanged(change)}. Undo takes back only the newest version, so load the latest above first.";

    /// <summary>Said when a save is refused because the page is out of date.</summary>
    public string NotSaved(CampaignChange change) => $"Not saved: {WhatChanged(change)}.";

    /// <summary>Said in place of the export when the page is out of date.</summary>
    public string NotExported(CampaignChange change) =>
        $"Not ready to copy into Square: {WhatChanged(change)}. Load the latest above, and copy from it once it is approved.";

    /// <summary>Said when Approve is refused because the page is out of date.</summary>
    public string NotApproved(CampaignChange change) =>
        $"Not approved: {WhatChanged(change)}. Load the latest above, check it, then approve it.";

    /// <summary>
    /// What the page asks before an undo, with the grace period in words. When the save is the only one,
    /// there is no version before it to come back: the undo deletes the document, which can be restored.
    /// </summary>
    /// <exception cref="InvalidOperationException">This subject has no undo.</exception>
    public string UndoQuestion(TimeSpan gracePeriod, bool deletes = false)
    {
        if (_goesBack is null) throw new InvalidOperationException("There is no undo here.");
        var period = TimeWords.Period(gracePeriod);
        return deletes && _noun is not null
            ? $"This deletes the {_noun}. You can restore it for {period}."
            : $"Undo the last save? {_goesBack} The undone {_held} can be restored for {period}, then it is deleted for good.";
    }
}
