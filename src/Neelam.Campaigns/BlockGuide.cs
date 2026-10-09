namespace Neelam.Campaigns;

/// <summary>
/// What a block type is for, in plain language, and the checks it brings. It lives in code beside
/// the block types because it describes what the code guarantees; each check names the rule id
/// <see cref="CampaignReview"/> reports, and a test holds the two together.
/// </summary>
/// <param name="Name">What the editor calls the type, also the default label of a new block.</param>
/// <param name="Rules">The rule ids of the checks this type brings or answers.</param>
public sealed record BlockGuide(BlockType Type, string Name, string Purpose, IReadOnlyList<string> Checks, IReadOnlyList<string> Rules)
{
    // Every block whose words a reader sees gets the text checks.
    private static readonly string[] TextChecks =
    [
        "Words the client's policy restricts are flagged for sign-off.",
        "The same run of words in two places is flagged as a likely paste error.",
        "An emoji touching a word, and more emoji than the policy allows, are flagged.",
        "A medical term needs a disclaimer in a fine-print block.",
    ];

    private static readonly string[] TextRules = ["restricted-term", "repeated-phrase", "emoji-spacing", "emoji-budget", "medical-disclaimer"];

    // Every photo, in a header or on its own: words in a picture can't be checked, and some readers never see it.
    private const string PhotoCheck = "A photo with no description is flagged: say what it shows, and put any offer or dates it holds in the text too.";
    private const string PhotoRule = "photo-described";

    public static IReadOnlyList<BlockGuide> All { get; } =
    [
        new(BlockType.Header, "Header", "The top of the email: the business's name, over a photo from the library if you like.",
            [PhotoCheck, .. TextChecks], [PhotoRule, .. TextRules]),
        new(BlockType.Heading, "Heading", "A line set large, such as the email's headline.", TextChecks, TextRules),
        new(BlockType.Greeting, "Greeting", "How the email addresses the reader, such as \"Hi Beautiful\".", TextChecks, TextRules),
        new(BlockType.Paragraphs, "Paragraphs", "Body text, one or more paragraphs.", TextChecks, TextRules),
        new(BlockType.Offer, "Offer",
            "A tiered offer, such as a membership: its name, a summary, and tiers with prices and typed benefits. " +
            "Always written for each campaign, never fixed.",
            [
                "It needs a button somewhere in the email, or the email is blocked.",
                "Tiers must have different names and different benefits, and prices that rise from the first.",
                "Names that are the same apart from their numbers (\"Option 1 Platinum Member\", \"Option 2 Platinum Member\") are flagged strongly, but not blocked: \"Glow 50\" and \"Glow 100\" are fine.",
                "Tier names are read on ladders such as Bronze, Silver, Gold, Platinum: two tiers on the same rung are flagged, and so is a higher rung that costs less.",
                "Each benefit's value must make sense (a discount between 1% and 99%, at least one free item).",
                "A recurring charge needs a link to its terms.",
                "Tiers offering different kinds of benefit are flagged so they are checked side by side.",
                .. TextChecks,
            ],
            ["cta-required", "tier-names-unique", "tier-names-numbered", "tier-rung-repeated", "tier-rung-order", "tier-content-distinct", "tier-price-positive", "tier-prices-increase",
             "benefit-value", "terms-required", "tiers-parallel", .. TextRules]),
        new(BlockType.Button, "Button", "What the reader should do next, and the web page it opens.",
            ["Its link must be a full https:// address.", "An email with an offer needs one.", .. TextChecks],
            ["cta-https", "cta-required", .. TextRules]),
        new(BlockType.Image, "Image", "A photo from the client's image library.",
            [PhotoCheck, "Its description, what a reader gets when images do not load, gets the text checks."],
            [PhotoRule, .. TextRules]),
        new(BlockType.SignOff, "Sign-off", "The closing: a valediction, who it is from, and a tagline if you like.",
            TextChecks, TextRules),
        new(BlockType.FinePrint, "Fine print", "Terms, disclaimers and other small print.",
            ["Any text here counts as the disclaimer a medical term needs.", .. TextChecks], TextRules),
        new(BlockType.Spacer, "Spacer", "Space between blocks. It holds nothing.", [], []),
    ];

    public static BlockGuide For(BlockType type) => All.Single(g => g.Type == type);
}
