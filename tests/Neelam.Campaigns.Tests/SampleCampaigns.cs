using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The one-year anniversary / Beauty Bank email, as sent and as corrected. "As sent" is the
/// closest the typed model can come to the original; some of the original's errors (such as
/// "50% Complimentary") cannot be expressed at all, and the nearest reading is used.
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

    public static Campaign AsSent() => new(
        Subject: "WE’RE TURNING ONE!",
        Headline: "WE’RE TURNING ONE! 🥂✨",
        Greeting: "Hi Beautiful🤍",
        Opening: Opening,
        Offer: new Offer(
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
            TiersNote: "Here’s how it works, 100% of your money goes to any treatments you would like:"),
        Closing: Closing,
        SignOff: new SignOff("With gratitude,", "Neelam Aesthetics Team 🤍✨",
            "Enhancing your beauty. Elevating your confidence."),
        CallToAction: null);

    /// <summary>
    /// The same email with every blocker fixed. The offer name is deliberately left as
    /// "Beauty Bank": renaming it is the owner's call, so it stays a warning.
    /// </summary>
    public static Campaign Corrected() => AsSent() with
    {
        Greeting = "Hi Beautiful 🤍",
        Offer = AsSent().Offer! with
        {
            Name = "✨ The Neelam Aesthetics Beauty Bank ✨",
            Tiers =
            [
                AsSent().Offer!.Tiers[0] with { Name = "Gold Member" },
                AsSent().Offer!.Tiers[1],
            ],
            TermsUrl = TermsUrl,
            TiersNote = "Here’s how it works. 100% of your monthly contribution goes toward any " +
                        "treatments you choose.",
        },
        CallToAction = new CallToAction("Join the Beauty Bank", JoinUrl),
        Disclaimer = "Wellness injections are provided after consultation with a licensed provider " +
                     "and are subject to eligibility.",
    };
}
