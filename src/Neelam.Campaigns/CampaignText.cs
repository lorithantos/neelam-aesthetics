namespace Neelam.Campaigns;

/// <summary>A piece of reader-visible text and where it sits in the email.</summary>
public sealed record TextFragment(string Location, string Text);

public static class CampaignText
{
    /// <summary>Every piece of text the reader will see, benefits included as rendered.</summary>
    public static IReadOnlyList<TextFragment> Fragments(Campaign c)
    {
        var list = new List<TextFragment>
        {
            new("Subject", c.Subject),
            new("Headline", c.Headline),
            new("Greeting", c.Greeting),
        };
        if (c.Preheader is not null) list.Add(new("Preheader", c.Preheader));
        list.AddRange(c.Opening.Select((p, i) => new TextFragment($"Opening ¶{i + 1}", p)));

        if (c.Offer is { } offer)
        {
            list.Add(new("Offer › Name", offer.Name));
            list.Add(new("Offer › Summary", offer.Summary));
            if (offer.TiersNote is not null) list.Add(new("Offer › Tiers note", offer.TiersNote));
            for (var t = 0; t < offer.Tiers.Count; t++)
            {
                var tier = offer.Tiers[t];
                var where = $"Offer › Tier {t + 1}";
                list.Add(new(where, tier.Name));
                list.AddRange(tier.Benefits.Select(b => new TextFragment($"{where} › Benefit", b.Describe())));
            }
        }

        list.AddRange(c.Closing.Select((p, i) => new TextFragment($"Closing ¶{i + 1}", p)));
        list.Add(new("Sign-off", $"{c.SignOff.Valediction} {c.SignOff.From}"));
        if (c.SignOff.Tagline is not null) list.Add(new("Sign-off › Tagline", c.SignOff.Tagline));
        if (c.CallToAction is not null) list.Add(new("Call to action", c.CallToAction.Label));
        if (c.Disclaimer is not null) list.Add(new("Disclaimer", c.Disclaimer));
        return list;
    }
}
