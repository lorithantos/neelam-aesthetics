namespace Neelam.Campaigns;

/// <summary>
/// One marketing email, as structured data. Every section of the email is a slot here, so the
/// checks in <see cref="CampaignReview"/> can reason about it instead of about free text.
/// </summary>
public sealed record Campaign(
    string Subject,
    string Headline,
    string Greeting,
    IReadOnlyList<string> Opening,
    Offer? Offer,
    IReadOnlyList<string> Closing,
    SignOff SignOff,
    CallToAction? CallToAction,
    string? Disclaimer = null,
    string? Preheader = null);

public sealed record SignOff(string Valediction, string From, string? Tagline = null);

/// <summary>The button or link that tells the reader what to do next.</summary>
public sealed record CallToAction(string Label, Uri Url);

/// <summary>A product being announced, such as a membership with tiers.</summary>
/// <param name="Name">Shown as the offer heading.</param>
/// <param name="Summary">One or two sentences on what the offer is.</param>
/// <param name="Tiers">Ordered cheapest first.</param>
/// <param name="IsRecurring">True when the customer is charged repeatedly (a monthly plan).</param>
/// <param name="TermsUrl">Where cancellation, rollover and refund terms live.</param>
/// <param name="TiersNote">One line said once above the tiers, never repeated per tier.</param>
public sealed record Offer(
    string Name,
    string Summary,
    IReadOnlyList<Tier> Tiers,
    bool IsRecurring,
    Uri? TermsUrl,
    string? TiersNote = null);

public sealed record Tier(string Name, decimal MonthlyPrice, IReadOnlyList<Benefit> Benefits);
