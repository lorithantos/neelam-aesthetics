using System.Text.Json.Serialization;

namespace Neelam.Campaigns;

/// <summary>
/// One marketing email, as structured data: a subject, a preheader, and the blocks its template
/// laid out, in order. The structure is the template's, so a new kind of email is a new template,
/// not a code change. Each block's type carries the checks that type needs, so
/// <see cref="CampaignReview"/> reasons about blocks instead of about free text.
/// </summary>
public sealed record Campaign(string Subject, IReadOnlyList<Block> Blocks, string? Preheader = null)
{
    /// <summary>Every block of one type, in order.</summary>
    public IEnumerable<T> BlocksOf<T>() where T : Block => Blocks.OfType<T>();
}

/// <summary>
/// The kinds of block an email can be built from. Derived from the one real email the tool has
/// seen (the Beauty Bank announcement), and meant to grow from the next ones, not from guesses.
/// </summary>
public enum BlockType
{
    Heading,
    Greeting,
    Paragraphs,
    Offer,
    Button,
    SignOff,
    FinePrint,
}

/// <summary>
/// One part of an email. <see cref="Label"/> is the template's name for it, e.g. "Opening", and is
/// how findings say where they are.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "block")]
[JsonDerivedType(typeof(HeadingBlock), "heading")]
[JsonDerivedType(typeof(GreetingBlock), "greeting")]
[JsonDerivedType(typeof(ParagraphsBlock), "paragraphs")]
[JsonDerivedType(typeof(OfferBlock), "offer")]
[JsonDerivedType(typeof(ButtonBlock), "button")]
[JsonDerivedType(typeof(SignOffBlock), "sign-off")]
[JsonDerivedType(typeof(FinePrintBlock), "fine-print")]
public abstract record Block(string Label)
{
    [JsonIgnore]
    public abstract BlockType Type { get; }
}

/// <summary>A line set as a heading, such as the email's headline.</summary>
public sealed record HeadingBlock(string Label, string Text) : Block(Label)
{
    public override BlockType Type => BlockType.Heading;
}

/// <summary>How the email addresses the reader.</summary>
public sealed record GreetingBlock(string Label, string Text) : Block(Label)
{
    public override BlockType Type => BlockType.Greeting;
}

/// <summary>Body text, one entry per paragraph.</summary>
public sealed record ParagraphsBlock(string Label, IReadOnlyList<string> Paragraphs) : Block(Label)
{
    public override BlockType Type => BlockType.Paragraphs;
}

/// <summary>
/// A tiered offer, such as a membership. Its benefits are typed, so wording the business can be
/// held to is generated, never typed; the offer checks belong to this block.
/// </summary>
public sealed record OfferBlock(string Label, Offer Offer) : Block(Label)
{
    public override BlockType Type => BlockType.Offer;
}

/// <summary>A button: what the reader should do next, and where.</summary>
public sealed record ButtonBlock(string Label, CallToAction Action) : Block(Label)
{
    public override BlockType Type => BlockType.Button;
}

public sealed record SignOffBlock(string Label, SignOff SignOff) : Block(Label)
{
    public override BlockType Type => BlockType.SignOff;
}

/// <summary>Terms, disclaimers and other small print.</summary>
public sealed record FinePrintBlock(string Label, string Text) : Block(Label)
{
    public override BlockType Type => BlockType.FinePrint;
}

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
