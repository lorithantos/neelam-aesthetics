namespace Neelam.Campaigns;

/// <summary>
/// The words of an email, compared the way the LLM proofread compares a finding's quote with the email
/// it read: words only, so line breaks, runs of spaces and curly apostrophes never decide, and case
/// ignored. Shared by the proofread, which marks a quote it cannot find as one to check by hand, and by
/// the campaign page, which greys an LLM finding once its quote has left the form (owner, 2026-10-31,
/// choice 1a: "looks fixed" is measured against the form as it stands).
/// </summary>
public static class QuotedText
{
    /// <summary>Words only: line breaks, runs of spaces and curly apostrophes aside.</summary>
    public static string Words(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace('’', '\'');

    /// <summary>True when <paramref name="quote"/> is in <paramref name="words"/> (as <see cref="Of"/> gives them), ignoring case; never for a blank quote.</summary>
    public static bool Holds(string words, string? quote) =>
        !string.IsNullOrWhiteSpace(quote) && words.Contains(Words(quote), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The words of a draft as far as it goes: every block as the email would show it (a missing part as
    /// its placeholder), then its subject and preheader, as the proofread reads them.
    /// </summary>
    public static string Of(CampaignDraft draft) => Words(string.Join("\n",
        DraftSoFar.Preview(draft).Select(b => b.Text)
            .Append(draft.Subject.HasValue ? draft.Subject.Value : "")
            .Append(draft.Preheader.HasValue ? draft.Preheader.Value : "")));

    /// <summary>The names of the photos a draft shows, as far as it goes, ignoring case.</summary>
    public static IReadOnlySet<string> PhotosOf(CampaignDraft draft) =>
        DraftSoFar.Preview(draft).Select(b => b.Image?.Name).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
}
