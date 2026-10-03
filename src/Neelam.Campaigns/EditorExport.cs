namespace Neelam.Campaigns;

/// <summary>Square's block kinds, as the real emails use them.</summary>
public enum BlockKind
{
    /// <summary>The header: the business's name over an optional photo.</summary>
    Header,

    /// <summary>A text block in a heading style.</summary>
    Heading,

    /// <summary>A text block in the paragraph style; one block holds many paragraphs.</summary>
    Text,

    Image,
    Button,
    Spacer,
}

/// <summary>
/// One block to paste into the email editor. The editor has no API, so export is a sequence of
/// blocks a person copies in order; each maps to one Square block of the same kind. Text runs
/// together the way the real emails do it: one text block holds the greeting and the opening, and
/// one holds an offer's details through to the sign-off.
/// </summary>
public sealed record EditorBlock(BlockKind Kind, string Text, Uri? Url = null, ImageRef? Image = null);

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

    // The template's blocks, in its order, as Square blocks; consecutive text becomes one block.
    private static List<EditorBlock> Render(Campaign c)
    {
        var blocks = new List<EditorBlock>();
        var text = new List<string>();

        void Flush()
        {
            if (text.Count == 0) return;
            blocks.Add(new(BlockKind.Text, string.Join("\n\n", text)));
            text.Clear();
        }

        void Add(EditorBlock block)
        {
            Flush();
            blocks.Add(block);
        }

        foreach (var block in c.Blocks)
        {
            switch (block)
            {
                case HeaderBlock h: Add(new(BlockKind.Header, h.Text, Image: h.Photo)); break;
                case HeadingBlock h: Add(new(BlockKind.Heading, h.Text)); break;
                case ImageBlock i: Add(new(BlockKind.Image, i.Image.AltText ?? "", Image: i.Image)); break;
                case ButtonBlock b: Add(new(BlockKind.Button, b.Action.Label, b.Action.Url)); break;
                case SpacerBlock: Add(new(BlockKind.Spacer, "")); break;
                case GreetingBlock g: text.Add(g.Text); break;
                case ParagraphsBlock p: text.AddRange(p.Paragraphs); break;
                case FinePrintBlock f: text.Add(f.Text); break;
                case SignOffBlock s:
                    text.Add(s.SignOff.Valediction);
                    text.Add(s.SignOff.From);
                    if (s.SignOff.Tagline is not null) text.Add(s.SignOff.Tagline);
                    break;
                case OfferBlock o:
                    Add(new(BlockKind.Heading, o.Offer.Name));
                    text.AddRange(OfferText(o.Offer, o.Marker));
                    break;
            }
        }
        Flush();
        return blocks;
    }

    // Each tier is its name, then one marked line per item, the price first: how the Beauty Bank
    // email laid its options out, worded by the model rather than typed.
    private static IEnumerable<string> OfferText(Offer offer, string marker)
    {
        yield return offer.Summary;
        if (offer.TiersNote is not null) yield return offer.TiersNote;
        foreach (var tier in offer.Tiers)
        {
            var price = tier.MonthlyPrice % 1 == 0 ? $"${tier.MonthlyPrice:0}" : $"${tier.MonthlyPrice:0.00}";
            var lines = new List<string> { $"{tier.Name}:", $"{marker} {price}{(offer.IsRecurring ? "/month" : "")}" };
            lines.AddRange(tier.Benefits.Select(b => $"{marker} {b.Describe()}"));
            yield return string.Join("\n", lines);
        }
        if (offer.TermsUrl is not null) yield return $"Full terms: {offer.TermsUrl}";
    }

    private static string ToPlainText(IEnumerable<EditorBlock> blocks) =>
        string.Join("\n\n", blocks.Where(b => b.Kind != BlockKind.Spacer).Select(b => b.Kind switch
        {
            BlockKind.Header => b.Image is null ? b.Text : $"{b.Text} [over photo: {b.Image.Name}]",
            BlockKind.Heading => b.Text.ToUpperInvariant(),
            BlockKind.Image => $"[photo: {b.Image!.Name}]",
            BlockKind.Button => $"[ {b.Text} ] → {b.Url}",
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
