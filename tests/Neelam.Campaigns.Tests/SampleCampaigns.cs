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
    // The real sends' button went to the clinic's Square site. The corrected version's join and
    // terms pages are placeholders: no page for joining existed.
    private static readonly CallToAction ComeVisit = new("Come visit 🤍", new Uri("https://neelamaesthetics.square.site/"));
    private static readonly Uri JoinUrl = new("https://example.com/beauty-bank/join");
    private static readonly Uri TermsUrl = new("https://example.com/beauty-bank/terms");

    // Two different photos of the clinic's principals: one behind the header, one above the button.
    // In the real sends both had empty alt text.
    private static readonly ImageRef HeaderPhoto = new("Principals toasting");
    private static readonly ImageRef BodyPhoto = new("Principals seated");

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

    /// <summary>First send: both options were the $299 tier, name and contents.</summary>
    public static Campaign FirstSend()
    {
        var second = SecondSend();
        var offer = second.OfferOf();
        return second.WithOffer(offer with { Tiers = [offer.Tiers[1], offer.Tiers[1]] });
    }

    /// <summary>
    /// Second send, 38 minutes later: option 1 became the $149 tier, but both were still named
    /// "Platinum Member". It had a button, "Come visit", to the clinic's site; nothing let a reader
    /// join the Beauty Bank, and there was no disclaimer or terms link.
    /// </summary>
    public static Campaign SecondSend() => Email(SentOffer, "Hi Beautiful🤍", ComeVisit, disclaimer: null);

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

    // In the order Square rendered the real sends: header photo with the business name, a spacer,
    // the headline, the greeting and opening, the offer through to the sign-off, then a photo and
    // the button.
    private static Campaign Email(Offer offer, string greeting, CallToAction button, string? disclaimer)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock("Header", "Neelam Aesthetics", HeaderPhoto),
            new SpacerBlock("Spacer"),
            new HeadingBlock("Headline", "WE’RE TURNING ONE! 🥂✨"),
            new GreetingBlock("Greeting", greeting),
            new ParagraphsBlock("Opening", Opening),
            new OfferBlock("Offer", offer, "🤍"),
            new ParagraphsBlock("Closing", Closing),
            new SignOffBlock("Sign-off", SignOff),
        };
        if (disclaimer is not null) blocks.Add(new FinePrintBlock("Disclaimer", disclaimer));
        blocks.Add(new ImageBlock("Photo", BodyPhoto));
        blocks.Add(new ButtonBlock("Call to action", button));
        return new Campaign("WE’RE TURNING ONE!", blocks);
    }
}

/// <summary>Reading and replacing a campaign's blocks by label, for tests that change one part.</summary>
internal static class CampaignEdits
{
    public static T Block<T>(this Campaign c, string label) where T : Block =>
        c.Blocks.OfType<T>().Single(b => b.Label == label);

    public static Offer OfferOf(this Campaign c) => c.Block<OfferBlock>("Offer").Offer;

    /// <summary>The campaign with its offer's content replaced, keeping the offer's label and marker.</summary>
    public static Campaign WithOffer(this Campaign c, Offer offer) =>
        c.With(c.Block<OfferBlock>("Offer") with { Offer = offer });

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
