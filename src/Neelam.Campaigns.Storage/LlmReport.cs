namespace Neelam.Campaigns.Storage;

/// <summary>A save of a campaign, and the AI proofread kept beside it.</summary>
public sealed record KeptProofread(SaveRef Save, ProofreadRecord Record);

/// <summary>Where one finding of a carried LLM report stands against the form as it is now.</summary>
public enum LlmFindingState
{
    /// <summary>Still to look at: its quote is still in the form, or it cannot be told.</summary>
    Live,

    /// <summary>Its quote was in the version the LLM read and is no longer in the form.</summary>
    LooksFixed,
}

/// <summary>One finding of an LLM report, and where it stands against the form.</summary>
public sealed record LlmReportFinding(Finding Finding, LlmFindingState State)
{
    public bool LooksFixed => State == LlmFindingState.LooksFixed;
}

/// <summary>One photo note of an LLM report, greyed once that photo has left the email.</summary>
public sealed record LlmReportPhoto(PhotoCheck Photo, bool LooksFixed);

/// <summary>
/// The LLM report the campaign page shows (owner, 2026-10-31: "fixing ONE LLM issue clears the entire
/// list. That means more calls than we need and wastes the user's time"): the newest proofread of the
/// campaign's saves in use, carried across edits and saves, each finding marked against the form as it
/// stands (choice 1a). A finding looks fixed when its quote was in the text of the version the LLM read
/// and is no longer in the form, words only and ignoring case (<see cref="QuotedText"/>); one with no
/// quote, or a quote that was not in that text (a photo's words among them), never looks fixed. A photo
/// note looks fixed once that photo has left the email. Greyed findings stay in place (choice 5a).
/// Display only: what counts as proofread, at approval, export and the warnings seen at export, is still
/// the exact save's proofread alone (<see cref="DraftSession.CurrentProofread"/>), except that a carried
/// Must fix still to look at stops the approval until this version is proofread again (choice 2b).
/// </summary>
/// <param name="Record">The proofread.</param>
/// <param name="Of">The save it read.</param>
/// <param name="IsCurrent">
/// The report of the newest save while the form still holds exactly that save: today's report, nothing
/// greyed. False once the form differs from the save it read, or when it read an earlier save.
/// </param>
public sealed record LlmReport(
    ProofreadRecord Record, SaveRef Of, bool IsCurrent, IReadOnlyList<LlmReportFinding> Findings, IReadOnlyList<LlmReportPhoto> Photos)
{
    /// <summary>The findings still to look at.</summary>
    public int StillToLookAt => Findings.Count(f => !f.LooksFixed);

    /// <summary>The findings that look fixed.</summary>
    public int LookFixed => Findings.Count(f => f.LooksFixed);

    /// <summary>
    /// A Must fix of a report carried from another version that still looks unfixed: it stops the
    /// approval until the version on the page is proofread again (owner, 2026-10-31, choice 2b).
    /// </summary>
    public bool StopsApproval => !IsCurrent && Findings.Any(f => !f.LooksFixed && f.Finding.Severity == Severity.Blocker);

    /// <summary>
    /// A report marked against the form: <paramref name="versionWords"/> are the words of the version
    /// it read (<see cref="QuotedText.Of"/>), <paramref name="form"/> the draft as the page holds it now.
    /// </summary>
    public static LlmReport Marked(KeptProofread kept, string versionWords, CampaignDraft form, bool current)
    {
        if (current)
            return new(kept.Record, kept.Save, true,
                kept.Record.Findings.Select(f => new LlmReportFinding(f, LlmFindingState.Live)).ToList(),
                kept.Record.Photos.Select(p => new LlmReportPhoto(p, false)).ToList());
        var formWords = QuotedText.Of(form);
        var photos = QuotedText.PhotosOf(form);
        return new(kept.Record, kept.Save, false,
            kept.Record.Findings.Select(f => new LlmReportFinding(f,
                QuotedText.Holds(versionWords, f.Excerpt) && !QuotedText.Holds(formWords, f.Excerpt)
                    ? LlmFindingState.LooksFixed
                    : LlmFindingState.Live)).ToList(),
            kept.Record.Photos.Select(p => new LlmReportPhoto(p, !photos.Contains(p.Name))).ToList());
    }

    /// <summary>
    /// What the page says of a report carried from another version, after "LLM report from an earlier
    /// version, {when}:": how many are still to look at and how many look fixed, and to proofread again.
    /// Proposed wording, from the owner's design (2026-10-31).
    /// </summary>
    public string CarriedSummary()
    {
        if (Findings.Count == 0) return "it found nothing to fix. Proofread again when you're done.";
        var fixedWord = LookFixed == 1 ? "looks" : "look";
        return $"{StillToLookAt} still to look at, {LookFixed} {fixedWord} fixed. Proofread again when you're done.";
    }
}
