namespace Neelam.Campaigns;

/// <summary>
/// Which field a finding is about, read from its location, so the page can take her straight to it.
/// A location starts with the block's label, as the checks write it ("Offer › Tier 2 › Benefit",
/// "Opening, paragraph 2"), or names the subject line.
/// </summary>
public static class FindingPlace
{
    /// <summary>The location the checks give the subject line.</summary>
    public const string Subject = "Subject";

    /// <summary>
    /// The block a finding is in, as its index among <paramref name="labels"/> (the campaign's blocks
    /// in order); null when it is about no one block, such as the subject line or the whole email.
    /// The longest label that the location starts with wins, so "Offer 2" is never taken for "Offer".
    /// </summary>
    public static int? BlockIndex(string location, IReadOnlyList<string> labels)
    {
        int? best = null;
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (label.Length == 0 || !Names(location, label)) continue;
            if (best is null || label.Length > labels[best.Value].Length) best = i;
        }
        return best;
    }

    // The label itself, or the label followed by a part of it (" › ...") or a paragraph (", ...").
    private static bool Names(string location, string label) =>
        location.StartsWith(label, StringComparison.Ordinal)
        && (location.Length == label.Length
            || location.AsSpan(label.Length).StartsWith(" ›", StringComparison.Ordinal)
            || location.AsSpan(label.Length).StartsWith(",", StringComparison.Ordinal));
}
