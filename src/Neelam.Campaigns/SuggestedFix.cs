using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>What came of using a finding's suggested fix (<see cref="SuggestedFix"/>).</summary>
public enum FixOutcome
{
    /// <summary>The form now holds the suggestion; nothing is saved.</summary>
    Applied,

    /// <summary>The quoted words are not in the block's own text, so nothing was changed.</summary>
    NotFound,

    /// <summary>No one place to change: the whole email, or a part the form has no field for; or no suggestion to use.</summary>
    Ambiguous,

    /// <summary>The words are the template's fixed text, changed only in the template.</summary>
    Fixed,
}

/// <summary>
/// A finding's suggested fix, put into the form with one click (owner, 2026-10-31, answer 3b: "a
/// suggested fix beside each finding, accepted with one click"). The finding's place names the block
/// (<see cref="FindingPlace"/>), or the subject line; its quote (<see cref="Finding.Excerpt"/>) is looked
/// for in that block's own text fields, ignoring whitespace and case, and every match in the block is
/// replaced by <see cref="Finding.Suggestion"/> (choice 4b). A benefit line nearly matching one of hers is
/// set as that benefit (<see cref="Finding.SuggestedBenefit"/>). Prices and benefit amounts are never
/// changed as text. Only the form changes: she saves as usual.
/// </summary>
public static class SuggestedFix
{
    /// <summary>Said in place of the button when the words are the template's.</summary>
    public const string FixedText = "This is the template's fixed text: change it in the template.";

    /// <summary>Said in place of the button when there is no one place to change, or the words are not in it.</summary>
    public const string ByHand = "Change this by hand.";

    /// <summary>What <see cref="Apply"/> would do, changing nothing: whether to offer "Use this".</summary>
    public static FixOutcome Check(CampaignEditor editor, Finding finding) => Run(editor, finding, apply: false);

    /// <summary>Puts the suggestion into the form, or says why not; changes nothing unless <see cref="FixOutcome.Applied"/>.</summary>
    public static FixOutcome Apply(CampaignEditor editor, Finding finding) => Run(editor, finding, apply: true);

    private static FixOutcome Run(CampaignEditor editor, Finding finding, bool apply)
    {
        if (finding.SuggestedBenefit is { } benefit) return UseBenefit(editor, finding.Location, benefit, apply);
        if (finding.Suggestion is not { } suggestion || string.IsNullOrWhiteSpace(finding.Excerpt)) return FixOutcome.Ambiguous;
        var (fields, outcome) = Fields(editor, finding.Location);
        if (fields is null) return outcome;

        var quote = Pattern(finding.Excerpt);
        if (!fields.Any(f => quote.IsMatch(f.Get()))) return FixOutcome.NotFound;
        if (!apply) return FixOutcome.Applied;
        foreach (var field in fields)
        {
            var text = field.Get();
            var replaced = quote.Replace(text, _ => suggestion);
            if (replaced != text) field.Set(replaced);
        }
        return FixOutcome.Applied;
    }

    private sealed record TextField(Func<string> Get, Action<string> Set);

    // The text fields of the place a finding names, or why there are none to change.
    private static (IReadOnlyList<TextField>? Fields, FixOutcome Why) Fields(CampaignEditor editor, string location)
    {
        if (location == FindingPlace.Subject)
            return ([new(() => editor.Subject.Text, v => editor.Subject.Text = v)], FixOutcome.Applied);
        if (Block(editor, location) is not { } block) return (null, FixOutcome.Ambiguous);
        if (block.IsFixed) return (null, FixOutcome.Fixed);
        IReadOnlyList<TextField> fields = block switch
        {
            TextBlockEditor t => [new(() => t.Field.Text, v => t.Field.Text = v)],
            ParagraphsBlockEditor p => [new(() => p.Text, v => p.Text = v)],
            ButtonBlockEditor b => [new(() => b.ButtonLabel, v => b.ButtonLabel = v)],
            SignOffBlockEditor s =>
            [
                new(() => s.Valediction, v => s.Valediction = v),
                new(() => s.From, v => s.From = v),
                new(() => s.Tagline, v => s.Tagline = v),
            ],
            HeaderBlockEditor h => [new(() => h.Text, v => h.Text = v), new(() => h.PhotoAltText, v => h.PhotoAltText = v)],
            ImageBlockEditor i => [new(() => i.PhotoAltText, v => i.PhotoAltText = v)],
            OfferBlockEditor o => OfferFields(o.Offer),
            PromotionBlockEditor p => PromotionFields(p.Promotion),
            _ => [],
        };
        return (fields, FixOutcome.NotFound);
    }

    // An offer's words: its name, summary, note on the tiers, each tier's name, and the words of each
    // benefit (never a price or an amount, which stay structured).
    private static IReadOnlyList<TextField> OfferFields(OfferEditor offer)
    {
        var fields = new List<TextField>
        {
            new(() => offer.Name.Text, v => offer.Name.Text = v),
            new(() => offer.Summary.Text, v => offer.Summary.Text = v),
            new(() => offer.TiersNote.Text, v => offer.TiersNote.Text = v),
        };
        foreach (var tier in offer.Tiers)
        {
            fields.Add(new(() => tier.Name.Text, v => tier.Name.Text = v));            foreach (var b in tier.Benefits)
            {
                fields.Add(new(() => b.AppliesTo, v => b.AppliesTo = v));
                fields.Add(new(() => b.ItemName, v => b.ItemName = v));
                fields.Add(new(() => b.Per, v => b.Per = v));
                fields.Add(new(() => b.Condition, v => b.Condition = v));
                fields.Add(new(() => b.Toward, v => b.Toward = v));
            }
        }
        return fields;
    }

    // A promotion's words: its name, summary and audience, each line's treatment, and the words of each
    // benefit line. Never a price, a date or an amount.
    private static IReadOnlyList<TextField> PromotionFields(PromotionEditor promotion)
    {
        var fields = new List<TextField>
        {
            new(() => promotion.Name.Text, v => promotion.Name.Text = v),
            new(() => promotion.Summary.Text, v => promotion.Summary.Text = v),
            new(() => promotion.Audience.Text, v => promotion.Audience.Text = v),
        };
        foreach (var line in promotion.Lines)
        {
            switch (line)
            {
                case PricedTreatmentEditor t:
                    fields.Add(new(() => t.Treatment.Text, v => t.Rename(v)));
                    break;
                case BenefitLineEditor { Benefit: var b }:
                    fields.Add(new(() => b.AppliesTo, v => b.AppliesTo = v));
                    fields.Add(new(() => b.ItemName, v => b.ItemName = v));
                    fields.Add(new(() => b.Per, v => b.Per = v));
                    fields.Add(new(() => b.Condition, v => b.Condition = v));
                    fields.Add(new(() => b.Toward, v => b.Toward = v));
                    break;
            }
        }
        return fields;
    }

    // The block a location names (a template's labels are unique); null when it names none.
    private static BlockEditor? Block(CampaignEditor editor, string location) =>
        FindingPlace.BlockIndex(location, editor.Blocks.Select(b => b.Label).ToList()) is { } i ? editor.Blocks[i] : null;

    // "Offer › Tier 2, benefit 3", as the known-item check names a benefit line.
    private static readonly Regex BenefitPlace = new(@"^(?<label>.+) › Tier (?<tier>\d+), benefit (?<benefit>\d+)$", RegexOptions.CultureInvariant);

    // "Promotion › Line 2", as the known-item check names a promotion's benefit line.
    private static readonly Regex LinePlace = new(@"^(?<label>.+) › Line (?<line>\d+)$", RegexOptions.CultureInvariant);

    private static FixOutcome UseBenefit(CampaignEditor editor, string location, Benefit benefit, bool apply)
    {
        if (LinePlace.Match(location) is { Success: true } line)
        {
            var promotions = editor.Blocks.OfType<PromotionBlockEditor>().Where(b => b.Label == line.Groups["label"].Value).ToList();
            if (promotions.Count != 1) return FixOutcome.Ambiguous;
            var lines = promotions[0].Promotion.Lines;
            var index = int.Parse(line.Groups["line"].Value) - 1;
            if (index >= lines.Count || lines[index] is not BenefitLineEditor benefitLine) return FixOutcome.NotFound;
            if (apply) benefitLine.Benefit.Use(benefit);
            return FixOutcome.Applied;
        }
        var place = BenefitPlace.Match(location);
        if (!place.Success) return FixOutcome.Ambiguous;
        var label = place.Groups["label"].Value;
        var offers = editor.Blocks.OfType<OfferBlockEditor>().Where(b => b.Label == label).ToList();
        if (offers.Count != 1) return FixOutcome.Ambiguous;
        var (t, b) = (int.Parse(place.Groups["tier"].Value) - 1, int.Parse(place.Groups["benefit"].Value) - 1);
        var tiers = offers[0].Offer.Tiers;
        if (t >= tiers.Count || b >= tiers[t].Benefits.Count) return FixOutcome.NotFound;
        if (apply) tiers[t].Benefits[b].Use(benefit);
        return FixOutcome.Applied;
    }

    // The quote, whitespace and case aside, a straight or curly apostrophe either way.
    private static Regex Pattern(string quote) =>
        new(string.Join(@"\s+", QuotedText.Words(quote).Split(' ').Select(w => Regex.Escape(w).Replace("'", "['’]"))),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
