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

    // One pass over the template's blocks, in its order: the email's layout is the template's,
    // never this method's.
    private static List<EditorBlock> Render(Campaign c)
    {
        var blocks = new List<EditorBlock>();
        foreach (var block in c.Blocks)
        {
            switch (block)
            {
                case HeadingBlock h: blocks.Add(new(BlockKind.Heading, h.Text)); break;
                case GreetingBlock g: blocks.Add(new(BlockKind.Text, g.Text)); break;
                case ParagraphsBlock p: blocks.AddRange(p.Paragraphs.Select(t => new EditorBlock(BlockKind.Text, t))); break;
                case FinePrintBlock f: blocks.Add(new(BlockKind.Text, f.Text)); break;
                case ButtonBlock b: blocks.Add(new(BlockKind.Button, b.Action.Label, b.Action.Url)); break;
                case SignOffBlock s:
                    var signOff = $"{s.SignOff.Valediction}\n{s.SignOff.From}";
                    if (s.SignOff.Tagline is not null) signOff += $"\n\n{s.SignOff.Tagline}";
                    blocks.Add(new(BlockKind.Text, signOff));
                    break;
                case OfferBlock o: blocks.AddRange(RenderOffer(o.Offer)); break;
            }
        }
        return blocks;
    }

    private static IEnumerable<EditorBlock> RenderOffer(Offer offer)
    {
        yield return new(BlockKind.Heading, offer.Name);
        yield return new(BlockKind.Text, offer.Summary);
        if (offer.TiersNote is not null) yield return new(BlockKind.Text, offer.TiersNote);
        foreach (var tier in offer.Tiers)
        {
            var price = tier.MonthlyPrice % 1 == 0 ? $"${tier.MonthlyPrice:0}" : $"${tier.MonthlyPrice:0.00}";
            var cadence = offer.IsRecurring ? "/month" : "";
            yield return new(BlockKind.Heading, $"{tier.Name} — {price}{cadence}");
            yield return new(BlockKind.List, string.Join('\n', tier.Benefits.Select(b => b.Describe())));
        }
        if (offer.TermsUrl is not null)
            yield return new(BlockKind.Text, $"Full terms: {offer.TermsUrl}", offer.TermsUrl);
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
