namespace Neelam.Campaigns;

/// <summary>
/// Which field a finding is about, read from its location, so the page can take her straight to it.
/// A location starts with the block's label, as the checks write it ("Offer › Tier 2 › Benefit",
/// "Opening, paragraph 2"), or names the subject line. A finding about one of her known benefit
/// lines' limits also leads to that line on the Known items page (<see cref="KnownLineLink"/>).
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

    /// <summary>
    /// The anchor of one of her benefit lines on the Known items page, by the line's id: where its
    /// usual amount and limits are changed in place.
    /// </summary>
    public static string KnownLineAnchor(string id) => $"known-line-{id}";

    /// <summary>
    /// The name of one of a line's limit boxes on the Known items page, the same on every line: which
    /// end and which amount, such as "highest-percent". A name, never a value.
    /// </summary>
    public static string CapBox(bool highest, string field) =>
        $"{(highest ? "highest" : "lowest")}-{field.ToLowerInvariant()}";

    /// <summary>
    /// The limit box an amount outside a line's limits leads to: Highest when it is above the highest,
    /// Lowest when it is below the lowest.
    /// </summary>
    public static string CapBox(OutsideLimit outside) => CapBox(outside.AboveHighest, outside.Amount.Field.Name);

    /// <summary>
    /// Where to change the limits a finding holds an amount to: the Known items page at that line, with
    /// the box to start in ("known-items?cap=highest-percent#known-line-{id}", relative to the site's
    /// base, as the site's other links are). Null for a finding that is not about one of her lines'
    /// limits.
    /// </summary>
    public static string? KnownLineLink(Finding finding) =>
        finding.KnownLine is { Length: > 0 } id
            ? $"known-items{(finding.KnownCap is { Length: > 0 } cap ? $"?cap={Uri.EscapeDataString(cap)}" : "")}#{KnownLineAnchor(id)}"
            : null;

    // The label itself, or the label followed by a part of it (" › ...") or a paragraph (", ...").
    private static bool Names(string location, string label) =>
        location.StartsWith(label, StringComparison.Ordinal)
        && (location.Length == label.Length
            || location.AsSpan(label.Length).StartsWith(" ›", StringComparison.Ordinal)
            || location.AsSpan(label.Length).StartsWith(",", StringComparison.Ordinal));
}
