using System.Text.RegularExpressions;

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

/// <summary>One block of the email as Square shows it, named for the AI proofread.</summary>
/// <param name="Id">Its place, "b1" first, as the assistant export numbers its blocks.</param>
/// <param name="Labels">The labels of the campaign's blocks it is made from, in order: one, or several for run-on text.</param>
public sealed record PreviewPart(string Id, EditorBlock Block, IReadOnlyList<string> Labels);

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
        return Render(report.Campaign.Blocks);
    }

    /// <summary>The whole passed email as plain text, for the approver to read top to bottom.</summary>
    public static string PlainText(ReviewReport report) => ToPlainText(Blocks(report));

    /// <summary>
    /// The email as it would be sent, ungated. Used to show the proofreader exactly what the
    /// customer would read; never a source for pasting.
    /// </summary>
    public static string Preview(Campaign c) => ToPlainText(Render(c.Blocks));

    /// <summary>
    /// The email's blocks as Square would show them, ungated: for SHOWING an email, such as the
    /// How it works walkthrough, never a source for pasting -- that is <see cref="Blocks"/>, which
    /// needs a passed review.
    /// </summary>
    public static IReadOnlyList<EditorBlock> PreviewBlocks(Campaign c) => Render(c.Blocks);

    /// <summary>
    /// <see cref="PreviewBlocks"/>, each with its id as the assistant export numbers it ("b1" first)
    /// and the labels of the campaign's blocks it is made from, in order: for the AI proofread, which
    /// reads the blocks she approves and says which one a finding is in. Ungated, never for pasting.
    /// </summary>
    public static IReadOnlyList<PreviewPart> PreviewParts(Campaign c) =>
        RenderParts(c.Blocks).Select((p, i) => new PreviewPart($"b{i + 1}", p.Block, p.Labels)).ToList();

    // The template's blocks, in its order, as Square blocks; consecutive text becomes one block.
    // Shared with TemplatePreview, which passes placeholders for the blocks each campaign fills.
    internal static List<EditorBlock> Render(IEnumerable<Block> source) =>
        RenderParts(source).Select(p => p.Block).ToList();

    // Render, each Square block with the labels of the blocks it came from.
    private static List<(EditorBlock Block, List<string> Labels)> RenderParts(IEnumerable<Block> source)
    {
        var blocks = new List<(EditorBlock Block, List<string> Labels)>();
        var text = new List<string>();
        var textLabels = new List<string>();

        void Flush()
        {
            if (text.Count == 0) return;
            blocks.Add((new(BlockKind.Text, string.Join("\n\n", text)), [.. textLabels]));
            text.Clear();
            textLabels.Clear();
        }

        foreach (var block in source)
        {
            void Add(EditorBlock square)
            {
                Flush();
                blocks.Add((square, [block.Label]));
            }

            // Text runs on into the next block's; each block it came from is named once.
            void Text(IEnumerable<string> lines)
            {
                var before = text.Count;
                text.AddRange(lines);
                if (text.Count > before && (textLabels.Count == 0 || textLabels[^1] != block.Label)) textLabels.Add(block.Label);
            }

            switch (block)
            {
                case PlaceholderBlock p:
                    switch (p.Type)
                    {
                        case BlockType.Header: Add(new(BlockKind.Header, p.Text)); break;
                        case BlockType.Heading: Add(new(BlockKind.Heading, p.Text)); break;
                        case BlockType.Image: Add(new(BlockKind.Image, p.Text, Image: new ImageRef(p.Text))); break;
                        case BlockType.Button: Add(new(BlockKind.Button, p.Text)); break;
                        case BlockType.Spacer: Add(new(BlockKind.Spacer, "")); break;
                        // An offer is its name as a heading, then text, so its placeholder is too.
                        case BlockType.Offer: Add(new(BlockKind.Heading, p.Text)); break;
                        // So is a promotion: its name as a heading, then text.
                        case BlockType.Promotion: Add(new(BlockKind.Heading, p.Text)); break;
                        default: Text([p.Text]); break;
                    }
                    break;
                case HeaderBlock h: Add(new(BlockKind.Header, h.Text, Image: h.Photo)); break;
                case HeadingBlock h: Add(new(BlockKind.Heading, h.Text)); break;
                case ImageBlock i: Add(new(BlockKind.Image, i.Image.AltText ?? "", Image: i.Image)); break;
                case ButtonBlock b: Add(new(BlockKind.Button, b.Action.Label, b.Action.Url)); break;
                case SpacerBlock: Add(new(BlockKind.Spacer, "")); break;
                case GreetingBlock g: Text([g.Text]); break;
                case ParagraphsBlock p: Text(p.Paragraphs); break;
                case FinePrintBlock f: Text([f.Text]); break;
                case SignOffBlock s:
                    Text(s.SignOff.Tagline is null
                        ? [s.SignOff.Valediction, s.SignOff.From]
                        : [s.SignOff.Valediction, s.SignOff.From, s.SignOff.Tagline]);
                    break;
                case OfferBlock o:
                    Add(new(BlockKind.Heading, o.Offer.Name));
                    Text(OfferText(o.Offer, o.Marker));
                    break;
                case PromotionBlock p:
                    Add(new(BlockKind.Heading, p.Promotion.Name));
                    Text(PromotionText(p.Promotion));
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
            yield return TierText(tier.Name, PriceText(tier.MonthlyPrice, offer.IsRecurring), tier.Benefits.Select(b => b.Describe()), marker);
        if (offer.TermsUrl is not null) yield return TermsText(offer.TermsUrl);
    }

    // A promotion is laid out as an offer is: its name as a heading (above), then its summary if any,
    // each line on its own, and the dates it runs, as her Filler Promo email set them out by hand.
    private static IEnumerable<string> PromotionText(Promotion promotion)
    {
        if (promotion.Summary is not null) yield return promotion.Summary;
        foreach (var line in promotion.Lines) yield return line.Describe();
        yield return DatesText(promotion.Starts, promotion.Ends, promotion.Audience);
    }

    /// <summary>
    /// The dates a promotion runs, and who it is for when not everyone, as one line: "Offer valid
    /// November 1 – December 31, 2026." or "Offer valid September 1 – 30, 2026, for teachers &amp; school
    /// faculty." The month and year are said once when they are shared; one day is said alone.
    /// </summary>
    public static string DatesText(DateOnly starts, DateOnly ends, string? audience) =>
        $"Offer valid {Range(starts, ends)}{(string.IsNullOrWhiteSpace(audience) ? "" : $", for {audience.Trim().TrimEnd('.')}")}.";

    /// <summary>
    /// <paramref name="finePrint"/> without a validity sentence at its start that says the promotion's own dates
    /// (<see cref="DatesText"/>, with its audience or without), so a saved promotion's fine print holds no dates:
    /// the campaign says them once. Typed by hand it may differ in case, in spacing, and in a hyphen, en or em
    /// dash for the en dash. Only a whole sentence at the very start goes; anything else is kept as it was.
    /// </summary>
    public static string WithoutLeadingDates(string finePrint, DateOnly starts, DateOnly ends, string? audience)
    {
        foreach (var sentence in new[] { DatesText(starts, ends, audience), DatesText(starts, ends, null) }.Distinct())
        {
            // The sentence as typed by hand: any case, any run of spaces, a hyphen, en or em dash for the en dash.
            var pattern = "^\\s*" + string.Join("\\s+", sentence.Split(' ').Select(w => Regex.Escape(w).Replace("–", "[-–—]")))
                          .Replace("\\s+[-–—]\\s+", "\\s*[-–—]\\s*") + "(?=\\s|$)";
            var match = Regex.Match(finePrint, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return finePrint[match.Length..].Trim();
        }
        return finePrint.Trim();
    }

    // US English whatever the server's culture, with an en dash between the days, as her fine print had it.
    private static string Range(DateOnly starts, DateOnly ends)
    {
        static string Month(DateOnly d) => d.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
        if (starts == ends) return $"{Month(starts)} {starts.Day}, {starts.Year}";
        if (starts.Year != ends.Year) return $"{Month(starts)} {starts.Day}, {starts.Year} – {Month(ends)} {ends.Day}, {ends.Year}";
        if (starts.Month != ends.Month) return $"{Month(starts)} {starts.Day} – {Month(ends)} {ends.Day}, {ends.Year}";
        return $"{Month(starts)} {starts.Day} – {ends.Day}, {ends.Year}";
    }

    // The pieces of an offer's wording, shared with the editor's preview of an unfinished draft
    // (DraftSoFar), so the parts written so far read exactly as they will be exported.

    internal static string TierText(string name, string price, IEnumerable<string> items, string marker) =>
        string.Join("\n", items.Select(i => $"{marker} {i}").Prepend($"{marker} {price}").Prepend($"{name}:"));

    // A tier's price as every amount is written (Dollars.Text): "$1,599/month", "$149.50".
    public static string PriceText(decimal monthlyPrice, bool isRecurring) =>
        Dollars.Text(monthlyPrice) + (isRecurring ? "/month" : "");

    internal static string TermsText(Uri termsUrl) => $"Full terms: {termsUrl}";

    internal static string ToPlainText(IEnumerable<EditorBlock> blocks) =>
        string.Join("\n\n", blocks.Where(b => b.Kind != BlockKind.Spacer).Select(b => b.Kind switch
        {
            BlockKind.Header => b.Image is null ? b.Text : $"{b.Text} [over photo: {b.Image.Name}]",
            BlockKind.Heading => b.Text.ToUpperInvariant(),
            BlockKind.Image => $"[photo: {b.Image!.Name}]",
            BlockKind.Button => b.Url is null ? $"[ {b.Text} ]" : $"[ {b.Text} ] → {b.Url}",
            _ => b.Text,
        }));
}

public sealed class CampaignBlockedException(ReviewReport report)
    : InvalidOperationException(!(report.Proofread || report.DemoApproval is not null)
        ? "Campaign has not been through the full review (rules and LLM proofread)."
        : report.WarningsToSee
            ? $"Campaign has {report.Warnings.Count()} thing(s) worth a look that nobody has been shown at export yet."
            : $"Campaign has {report.Blockers.Count()} blocking problem(s): " +
              string.Join("; ", report.Blockers.Select(b => b.Message)))
{
    public ReviewReport Report { get; } = report;
}
