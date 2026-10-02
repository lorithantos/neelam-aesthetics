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
    /// Builds the paste sequence from a passed review. An email that failed the gate, or that was
    /// never proofread, has no export to copy from.
    /// </summary>
    public static IReadOnlyList<EditorBlock> Blocks(ReviewReport report)
    {
        if (!report.CanExport)
            throw new CampaignBlockedException(report);
        return Render(report.Campaign);
    }

    /// <summary>The whole passed email as plain text, for the approver to read top to bottom.</summary>
    public static string PlainText(ReviewReport report) => ToPlainText(Blocks(report));

    /// <summary>
    /// The email as it would be sent, ungated. Used to show the proofreader exactly what the
    /// customer would read; never a source for pasting.
    /// </summary>
    public static string Preview(Campaign c) => ToPlainText(Render(c));

    private static List<EditorBlock> Render(Campaign c)
    {
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

        if (c.CallToAction is not null)
            blocks.Add(new(BlockKind.Button, c.CallToAction.Label, c.CallToAction.Url));
        blocks.Add(new(BlockKind.Divider, ""));
        blocks.AddRange(c.Closing.Select(p => new EditorBlock(BlockKind.Text, p)));

        var signOff = $"{c.SignOff.Valediction}\n{c.SignOff.From}";
        if (c.SignOff.Tagline is not null) signOff += $"\n\n{c.SignOff.Tagline}";
        blocks.Add(new(BlockKind.Text, signOff));
        if (c.Disclaimer is not null) blocks.Add(new(BlockKind.Text, c.Disclaimer));
        return blocks;
    }

    private static string ToPlainText(IEnumerable<EditorBlock> blocks) =>
        string.Join("\n\n", blocks.Select(b => b.Kind switch
        {
            BlockKind.Heading => b.Text.ToUpperInvariant(),
            BlockKind.List => string.Join('\n', b.Text.Split('\n').Select(l => $"• {l}")),
            BlockKind.Button => $"[ {b.Text} ] → {b.Url}",
            BlockKind.Divider => "———",
            _ => b.Text,
        }));
}

public sealed class CampaignBlockedException(ReviewReport report)
    : InvalidOperationException(report.Proofread
        ? $"Campaign has {report.Blockers.Count()} blocking problem(s): " +
          string.Join("; ", report.Blockers.Select(b => b.Message))
        : "Campaign has not been through the full review (rules and AI proofread).")
{
    public ReviewReport Report { get; } = report;
}
