using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The one-year anniversary / Beauty Bank email: both real sends and a corrected version, as the
/// blocks the email actually has, in its order. The sends are the closest the typed model can
/// come to the originals; some of their errors (such as "50% Complimentary") cannot be expressed
/// at all, and the nearest reading is used. This email is the sample of one the block types were
/// derived from.
/// </summary>
internal static class SampleCampaigns
{
    // Placeholders: the real booking page and terms page are not known yet.
    private static readonly Uri JoinUrl = new("https://example.com/beauty-bank/join");
    private static readonly Uri TermsUrl = new("https://example.com/beauty-bank/terms");

    private static readonly string[] Opening =
    [
        "One year of Neelam Aesthetics and we truly couldn’t have done it without YOU.",
        "From your first appointment to every visit, referrals, kind messages, reviews, and word of " +
        "mouth recommendations. Your support for our small family business has meant more to us than " +
        "we can put into words.",
        "To celebrate this milestone, we’re excited to introduce a new way to make your aesthetic goals " +
        "a little easier to plan for.",
    ];

    private static readonly string[] Closing =
    [
        "The Beauty Bank was created to make investing in yourself feel simple and intentional.",
        "A YEAR OF YOU. A YEAR OF US. 🤍",
        "We want to thank you for being part of our first year. Your trust has allowed us to grow, " +
        "learn, and continue creating an experience centered around confidence, care, and natural, " +
        "refined results.",
        "Here’s to year one and to everything still to come.",
    ];

    private const string Summary =
        "Think of the Beauty Bank as your personal beauty savings account. Each month, you contribute " +
        "toward your future treatments while receiving exclusive Beauty Bank benefits along the way.";

    private static readonly SignOff SignOff = new("With gratitude,", "Neelam Aesthetics Team 🤍✨",
        "Enhancing your beauty. Elevating your confidence.");

    private static readonly Offer SentOffer = new(
        Name: "✨The Neelam Aesthetics Beauty Bank✨",
        Summary: Summary,
        Tiers:
        [
            new Tier("Platinum Member", 149m,
            [
                new BirthdayCredit(25m),
                new PercentOff(5, "any qualifying treatments"),
                new DiscountedItem(50, "wellness injection", "per visit"),
            ]),
            new Tier("Platinum Member", 299m,
            [
                new BirthdayCredit(75m),
                new PercentOff(10, "any qualifying treatments"),
                new FreeItem(1, "wellness injection", "per visit"),
                new DiscountedItem(50, "wellness injection", "per visit", "any additional"),
            ]),
        ],
        IsRecurring: true,
        TermsUrl: null,
        TiersNote: "Here’s how it works, 100% of your money goes to any treatments you would like:");

    /// <summary>First send: both offers identical, name and contents.</summary>
    public static Campaign FirstSend()
    {
        var second = SecondSend();
        var offer = second.OfferOf();
        return second.With(new OfferBlock("Offer", offer with { Tiers = [offer.Tiers[0], offer.Tiers[0]] }));
    }

    /// <summary>Second send: one offer updated, but both still named "Platinum Member"; no button, no disclaimer.</summary>
    public static Campaign SecondSend() => Email(SentOffer, "Hi Beautiful🤍", button: null, disclaimer: null);

    /// <summary>
    /// The same email with every blocker fixed. The offer name is deliberately left as
    /// "Beauty Bank": renaming it is the owner's call, so it stays a warning.
    /// </summary>
    public static Campaign Corrected() => Email(
        SentOffer with
        {
            Name = "✨ The Neelam Aesthetics Beauty Bank ✨",
            Tiers = [SentOffer.Tiers[0] with { Name = "Gold Member" }, SentOffer.Tiers[1]],
            TermsUrl = TermsUrl,
            TiersNote = "Here’s how it works. 100% of your monthly contribution goes toward any " +
                        "treatments you choose.",
        },
        "Hi Beautiful 🤍",
        new CallToAction("Join the Beauty Bank", JoinUrl),
        "Wellness injections are provided after consultation with a licensed provider " +
        "and are subject to eligibility.");

    private static Campaign Email(Offer offer, string greeting, CallToAction? button, string? disclaimer)
    {
        var blocks = new List<Block>
        {
            new HeadingBlock("Headline", "WE’RE TURNING ONE! 🥂✨"),
            new GreetingBlock("Greeting", greeting),
            new ParagraphsBlock("Opening", Opening),
            new OfferBlock("Offer", offer),
        };
        if (button is not null) blocks.Add(new ButtonBlock("Call to action", button));
        blocks.Add(new ParagraphsBlock("Closing", Closing));
        blocks.Add(new SignOffBlock("Sign-off", SignOff));
        if (disclaimer is not null) blocks.Add(new FinePrintBlock("Disclaimer", disclaimer));
        return new Campaign("WE’RE TURNING ONE!", blocks);
    }
}

/// <summary>Reading and replacing a campaign's blocks by label, for tests that change one part.</summary>
internal static class CampaignEdits
{
    public static T Block<T>(this Campaign c, string label) where T : Block =>
        c.Blocks.OfType<T>().Single(b => b.Label == label);

    public static Offer OfferOf(this Campaign c) => c.Block<OfferBlock>("Offer").Offer;

    /// <summary>The campaign with the block of the same label replaced, or added at the end.</summary>
    public static Campaign With(this Campaign c, Block block)
    {
        var blocks = c.Blocks.ToList();
        var i = blocks.FindIndex(b => b.Label == block.Label);
        if (i >= 0) blocks[i] = block;
        else blocks.Add(block);
        return c with { Blocks = blocks };
    }

    public static Campaign Without(this Campaign c, string label) =>
        c with { Blocks = c.Blocks.Where(b => b.Label != label).ToList() };
}
