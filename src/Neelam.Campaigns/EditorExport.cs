namespace Neelam.Campaigns;

public enum BlockKind { Heading, Text, List, Button, Divider }

/// <summary>
/// One block to paste into the email editor. The editor has no API, so export is a sequence of
/// blocks a person copies in order; each block maps to one editor block of the same kind.
/// </summary>
public sealed record EditorBlock(BlockKind Kind, string Text, Uri? Url = null);

public static class EditorExport
{
    /// <summary>
    /// Builds the paste sequence. Refuses while the review has blockers, so an email that failed
    /// the gate has no export to copy from.
    /// </summary>
    public static IReadOnlyList<EditorBlock> Blocks(Campaign c, CampaignPolicy? policy = null)
    {
        var report = CampaignReview.Check(c, policy);
        if (!report.CanExport)
            throw new CampaignBlockedException(report);

        var blocks = new List<EditorBlock>
        {
            new(BlockKind.Heading, c.Headline),
            new(BlockKind.Text, c.Greeting),
        };
        blocks.AddRange(c.Opening.Select(p => new EditorBlock(BlockKind.Text, p)));

        if (c.Offer is { } offer)
        {
            blocks.Add(new(BlockKind.Heading, offer.Name));
            blocks.Add(new(BlockKind.Text, offer.Summary));
            if (offer.TiersNote is not null) blocks.Add(new(BlockKind.Text, offer.TiersNote));
            foreach (var tier in offer.Tiers)
            {
                var price = tier.MonthlyPrice % 1 == 0 ? $"${tier.MonthlyPrice:0}" : $"${tier.MonthlyPrice:0.00}";
                var cadence = offer.IsRecurring ? "/month" : "";
                blocks.Add(new(BlockKind.Heading, $"{tier.Name} — {price}{cadence}"));
                blocks.Add(new(BlockKind.List, string.Join('\n', tier.Benefits.Select(b => b.Describe()))));
            }
            if (offer.TermsUrl is not null)
                blocks.Add(new(BlockKind.Text, $"Full terms: {offer.TermsUrl}", offer.TermsUrl));
        }

        blocks.Add(new(BlockKind.Button, c.CallToAction!.Label, c.CallToAction.Url));
        blocks.Add(new(BlockKind.Divider, ""));
        blocks.AddRange(c.Closing.Select(p => new EditorBlock(BlockKind.Text, p)));

        var signOff = $"{c.SignOff.Valediction}\n{c.SignOff.From}";
        if (c.SignOff.Tagline is not null) signOff += $"\n\n{c.SignOff.Tagline}";
        blocks.Add(new(BlockKind.Text, signOff));
        if (c.Disclaimer is not null) blocks.Add(new(BlockKind.Text, c.Disclaimer));
        return blocks;
    }

    /// <summary>The whole email as plain text, for the approver to read top to bottom.</summary>
    public static string PlainText(Campaign c, CampaignPolicy? policy = null) =>
        string.Join("\n\n", Blocks(c, policy).Select(b => b.Kind switch
        {
            BlockKind.Heading => b.Text.ToUpperInvariant(),
            BlockKind.List => string.Join('\n', b.Text.Split('\n').Select(l => $"• {l}")),
            BlockKind.Button => $"[ {b.Text} ] → {b.Url}",
            BlockKind.Divider => "———",
            _ => b.Text,
        }));
}

public sealed class CampaignBlockedException(ReviewReport report)
    : InvalidOperationException(
        $"Campaign has {report.Blockers.Count()} blocking problem(s): " +
        string.Join("; ", report.Blockers.Select(b => b.Message)))
{
    public ReviewReport Report { get; } = report;
}
