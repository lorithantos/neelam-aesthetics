namespace Neelam.Campaigns;

/// <summary>A piece of reader-visible text and where it sits in the email.</summary>
public sealed record TextFragment(string Location, string Text);

public static class CampaignText
{
    /// <summary>
    /// Every piece of text the reader will see, in the order they read it, benefits included as
    /// rendered. Locations come from the template's block labels, e.g. "Opening, paragraph 2".
    /// </summary>
    public static IReadOnlyList<TextFragment> Fragments(Campaign c)
    {
        var list = new List<TextFragment> { new("Subject", c.Subject) };
        if (c.Preheader is not null) list.Add(new("Preheader", c.Preheader));

        foreach (var block in c.Blocks)
        {
            switch (block)
            {
                case HeadingBlock h: list.Add(new(h.Label, h.Text)); break;
                case GreetingBlock g: list.Add(new(g.Label, g.Text)); break;
                case FinePrintBlock f: list.Add(new(f.Label, f.Text)); break;
                case HeaderBlock h:
                    list.Add(new(h.Label, h.Text));
                    if (h.Photo?.AltText is { } headerAlt) list.Add(new($"{h.Label} › Photo text", headerAlt));
                    break;
                // Alt text is what a reader gets when images do not load, so it is checked like any text.
                case ImageBlock i when i.Image.AltText is { } alt: list.Add(new($"{i.Label} › Photo text", alt)); break;
                case ParagraphsBlock p:
                    list.AddRange(p.Paragraphs.Select((text, i) => new TextFragment(ParagraphOf(p.Label, i + 1), text)));
                    break;
                case ButtonBlock b: list.Add(new(b.Label, b.Action.Label)); break;
                case SignOffBlock s:
                    list.Add(new(s.Label, $"{s.SignOff.Valediction} {s.SignOff.From}"));
                    if (s.SignOff.Tagline is not null) list.Add(new($"{s.Label} › Tagline", s.SignOff.Tagline));
                    break;
                case OfferBlock o:
                    var offer = o.Offer;
                    list.Add(new($"{o.Label} › Name", offer.Name));
                    list.Add(new($"{o.Label} › Summary", offer.Summary));
                    if (offer.TiersNote is not null) list.Add(new($"{o.Label} › Tiers note", offer.TiersNote));
                    for (var t = 0; t < offer.Tiers.Count; t++)
                    {
                        var where = $"{o.Label} › Tier {t + 1}";
                        list.Add(new(where, offer.Tiers[t].Name));
                        list.AddRange(offer.Tiers[t].Benefits.Select(b => new TextFragment($"{where} › Benefit", b.Describe())));
                    }
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// Where a paragraph is, in words: "Closing, paragraph 1". A pilcrow ("Closing ¶1") is an
    /// editor's mark the client should not have to read.
    /// </summary>
    public static string ParagraphOf(string label, int number) => $"{label}, paragraph {number}";
}
