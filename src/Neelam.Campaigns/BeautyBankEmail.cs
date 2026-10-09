namespace Neelam.Campaigns;

/// <summary>
/// The one-year anniversary / Beauty Bank email: both real sends and a corrected version, as the
/// blocks the email actually has, in its order. The sends are the closest the typed model can
/// come to the originals; some of their errors (such as "50% Complimentary") cannot be expressed
/// at all, and the nearest reading is used. This email is the sample of one the block types were
/// derived from.
/// </summary>
/// <remarks>
/// In the library rather than only the tests since 2026-10-09, so the How it works page can walk a
/// client through the workflow with the email they actually sent: what went out, what the checks
/// catch in it, and what a corrected version pastes into Square as. It is sample content for
/// showing, already sent to Neelam's customers; nothing reads or writes it from storage.
/// </remarks>
public static class BeautyBankEmail
{
    // The real sends' button went to the clinic's Square site. The corrected version's join and
    // terms pages are placeholders: no page for joining existed.
    private static readonly CallToAction ComeVisit = new("Come visit 🤍", new Uri("https://neelamaesthetics.square.site/"));
    private static readonly Uri JoinUrl = new("https://example.com/beauty-bank/join");
    private static readonly Uri TermsUrl = new("https://example.com/beauty-bank/terms");

    // Two different photos of the clinic's principals: one behind the header, one above the button.
    // In the real sends both had empty alt text, which photo-described flags; the corrected version
    // describes them.
    private static readonly ImageRef HeaderPhoto = new("Principals toasting");
    private static readonly ImageRef BodyPhoto = new("Principals seated");
    private static readonly ImageRef HeaderPhotoDescribed = HeaderPhoto with { AltText = "The clinic's principals raising a toast" };
    private static readonly ImageRef BodyPhotoDescribed = BodyPhoto with { AltText = "The clinic's principals, seated together" };

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

    // The offer's shared parts, and the tiers the corrected version starts from (its $149 tier
    // renamed). Neither send's tiers: those are below, as each went out.
    private static readonly Offer BeautyBank = new(
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

    // The $299 option as sent, as close to its words as the typed benefits come (checked word for word
    // against the sent email, 2026-10-09): both options of the first send, and option 2 of the second.
    // As sent, each option was one line per benefit:
    //   "Option 1 Platinum Member:"
    //   "🤍 $299 monthly contribution (100% of your money goes to any treatments you would like)"
    //   "🤍 $75 Birthday reward during your birth month!"
    //   "🤍 10% off any qualifying treatments"
    //   "🤍 1 Complimentary Wellness Injection per visit, 50% off any additional wellness injections during visit"
    // with "or" on a line between the two options. What the model cannot say: the price line (it
    // writes "$299/month"), "Birthday reward ...!" (a birthday credit reads "birthday credit"),
    // "Complimentary" capitalised, the last line as one line rather than two benefits, and the "or".
    private static Tier SentOption299(int number) => new($"Option {number} Platinum Member", 299m,
    [
        new BirthdayCredit(75m),
        new PercentOff(10, "any qualifying treatments"),
        new FreeItem(1, "Wellness Injection", "per visit"),
        new DiscountedItem(50, "wellness injection", "during visit", "any additional"),
    ]);

    // The second send's option 1, the $149 tier, checked word for word against the send (2026-10-09).
    // Its last line went out as "50% Complimentary Wellness Injections per visit", the email's real
    // mistake (half off, or free?), entered as sent: "Complimentary" in "Which ones", so it reads
    // "50% off Complimentary Wellness Injections per visit", the sent words plus the "off" the model adds.
    private static readonly Tier SecondSendOption1 = new("Option 1 Platinum Member", 149m,
    [
        new BirthdayCredit(25m),
        new PercentOff(5, "any qualifying treatments"),
        new DiscountedItem(50, "Wellness Injection", "per visit", "Complimentary"),
    ]);

    private static readonly string[] SentOpening =
        [Opening[0], Opening[1].Replace("small family business", "small Family business"), Opening[2]];

    /// <summary>
    /// First send: both options were the $299 tier, word for word, under the names "Option 1 Platinum
    /// Member" and "Option 2 Platinum Member". Its subject was "Celebrate 1 year of Neelam Aesthetics!"
    /// ("WE'RE TURNING ONE!" was the headline), its offer heading began "Introducing:", and its
    /// opening wrote "small Family business".
    /// </summary>
    public static Campaign FirstSend() =>
        Email(BeautyBank with
            {
                Name = "Introducing: ✨The Neelam Aesthetics Beauty Bank✨",
                Tiers = [SentOption299(1), SentOption299(2)],
            },
            "Hi Beautiful🤍", ComeVisit, disclaimer: null, opening: SentOpening,
            subject: "Celebrate 1 year of Neelam Aesthetics!");

    /// <summary>
    /// Second send, 38 minutes later, subject "Celebrate 1 year of Neelam Aesthetics! - Correction":
    /// option 1 became the $149 tier, but the options were still "Option 1 Platinum Member" and
    /// "Option 2 Platinum Member", and option 1's last line said "50% Complimentary Wellness
    /// Injections per visit". Otherwise as the first send: the "Introducing:" heading, "small Family
    /// business", a button, "Come visit", to the clinic's site that let no reader join the Beauty
    /// Bank, and no disclaimer or terms link.
    /// </summary>
    public static Campaign SecondSend() =>
        Email(BeautyBank with
            {
                Name = "Introducing: ✨The Neelam Aesthetics Beauty Bank✨",
                Tiers = [SecondSendOption1, SentOption299(2)],
            },
            "Hi Beautiful🤍", ComeVisit, disclaimer: null, opening: SentOpening,
            subject: "Celebrate 1 year of Neelam Aesthetics! - Correction");

    /// <summary>
    /// The same email with every blocker fixed. The offer name is deliberately left as
    /// "Beauty Bank": renaming it is the owner's call, so it stays a warning.
    /// </summary>
    public static Campaign Corrected() => Email(
        BeautyBank with
        {
            Name = "✨ The Neelam Aesthetics Beauty Bank ✨",
            Tiers = [BeautyBank.Tiers[0] with { Name = "Gold Member" }, BeautyBank.Tiers[1]],
            TermsUrl = TermsUrl,
            TiersNote = "Here’s how it works. 100% of your monthly contribution goes toward any " +
                        "treatments you choose.",
        },
        "Hi Beautiful 🤍",
        new CallToAction("Join the Beauty Bank", JoinUrl),
        "Wellness injections are provided after consultation with a licensed provider " +
        "and are subject to eligibility.",
        photos: (HeaderPhotoDescribed, BodyPhotoDescribed));

    /// <summary>
    /// Neelam's membership-announcement template, deduced from this email: the corrected email's
    /// blocks, with the parts that stay the same from send to send fixed (header, greeting, closing,
    /// sign-off, disclaimer) and everything else left for each campaign to fill.
    /// </summary>
    /// <remarks>
    /// In the library since 2026-10-09 so the How it works page can show it to Neelam's people
    /// before sign-in exists. It is the template their client would start with; it is not saved
    /// anywhere, and putting it in their own container waits for the real site.
    /// </remarks>
    public static CampaignTemplate MembershipTemplate()
    {
        var corrected = Corrected();
        T Fixed<T>(string label) where T : Block => corrected.Blocks.OfType<T>().Single(b => b.Label == label);

        return new CampaignTemplate("Membership announcement",
        [
            new TemplateBlock("Header", BlockType.Header, Fixed: Fixed<HeaderBlock>("Header")),
            new TemplateBlock("Spacer", BlockType.Spacer),
            new TemplateBlock("Headline", BlockType.Heading),
            new TemplateBlock("Greeting", BlockType.Greeting, Fixed: Fixed<GreetingBlock>("Greeting")),
            new TemplateBlock("Opening", BlockType.Paragraphs),
            new TemplateBlock("Offer", BlockType.Offer, Recurring: true, Marker: "🤍"),
            new TemplateBlock("Closing", BlockType.Paragraphs, Fixed: Fixed<ParagraphsBlock>("Closing")),
            new TemplateBlock("Sign-off", BlockType.SignOff, Fixed: Fixed<SignOffBlock>("Sign-off")),
            new TemplateBlock("Disclaimer", BlockType.FinePrint, Required: false, Fixed: Fixed<FinePrintBlock>("Disclaimer")),
            new TemplateBlock("Photo", BlockType.Image, Required: false),
            new TemplateBlock("Call to action", BlockType.Button),
        ]);
    }

    // In the order Square rendered the real sends: header photo with the business name, a spacer,
    // the headline, the greeting and opening, the offer through to the sign-off, then a photo and
    // the button.
    // The corrected version keeps the headline as its subject; the sends' subjects are their own.
    private static Campaign Email(
        Offer offer, string greeting, CallToAction button, string? disclaimer, string[]? opening = null,
        (ImageRef Header, ImageRef Body)? photos = null, string subject = "WE’RE TURNING ONE!")
    {
        var (headerPhoto, bodyPhoto) = photos ?? (HeaderPhoto, BodyPhoto);
        var blocks = new List<Block>
        {
            new HeaderBlock("Header", "Neelam Aesthetics", headerPhoto),
            new SpacerBlock("Spacer"),
            new HeadingBlock("Headline", "WE’RE TURNING ONE! 🥂✨"),
            new GreetingBlock("Greeting", greeting),
            new ParagraphsBlock("Opening", opening ?? Opening),
            new OfferBlock("Offer", offer, "🤍"),
            new ParagraphsBlock("Closing", Closing),
            new SignOffBlock("Sign-off", SignOff),
        };
        if (disclaimer is not null) blocks.Add(new FinePrintBlock("Disclaimer", disclaimer));
        blocks.Add(new ImageBlock("Photo", bodyPhoto));
        blocks.Add(new ButtonBlock("Call to action", button));
        return new Campaign(subject, blocks);
    }
}
