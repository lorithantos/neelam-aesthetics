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
/// The kinds of block an email can be built from. Derived from the real emails the tool has seen
/// (the Beauty Bank announcement, both sends, as Square rendered them), and meant to grow from the
/// next ones, not from guesses.
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
    Header,
    Image,
    Spacer,

    /// <summary>
    /// A promotion: one offer for a set time, with no tiers (owner, 2026-10-31: "effectively a tier, but
    /// without multiple ones"). From her "Filler Promo" campaign, which typed two priced treatments into
    /// paragraphs, and her back-to-school email's benefits for an audience. A type of its own, never an
    /// offer, so a template update never pours one into the other.
    /// </summary>
    Promotion,
}

/// <summary>
/// A photo in the client's image library, by its name there, with the text a reader gets when
/// images do not load. The library is the client's own data; Square has no API, so the export
/// says which photo goes where for a person to place.
/// </summary>
public sealed record ImageRef(string Name, string? AltText = null);

/// <summary>What a header holds: the business's name, over an optional photo.</summary>
public sealed record HeaderContent(string Text, ImageRef? Photo = null);

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
[JsonDerivedType(typeof(HeaderBlock), "header")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(SpacerBlock), "spacer")]
[JsonDerivedType(typeof(PromotionBlock), "promotion")]
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
/// <param name="Marker">What starts each benefit line, e.g. "🤍": the business's own, from its template.</param>
public sealed record OfferBlock(string Label, Offer Offer, string Marker = OfferBlock.DefaultMarker) : Block(Label)
{
    public const string DefaultMarker = "•";

    public override BlockType Type => BlockType.Offer;
}

/// <summary>The top of the email: the business's name, over an optional photo.</summary>
public sealed record HeaderBlock(string Label, string Text, ImageRef? Photo = null) : Block(Label)
{
    public override BlockType Type => BlockType.Header;
}

/// <summary>A photo from the client's image library.</summary>
public sealed record ImageBlock(string Label, ImageRef Image) : Block(Label)
{
    public override BlockType Type => BlockType.Image;
}

/// <summary>Space between blocks; it holds nothing.</summary>
public sealed record SpacerBlock(string Label) : Block(Label)
{
    public override BlockType Type => BlockType.Spacer;
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

/// <summary>A promotion: its name, the dates it runs, and its lines. Never fixed by a template.</summary>
public sealed record PromotionBlock(string Label, Promotion Promotion) : Block(Label)
{
    public override BlockType Type => BlockType.Promotion;
}

/// <summary>
/// One offer for a set time (owner, 2026-10-31, answers P1-P6). Its lines are what she picks and
/// prices, so the saving is worked out by the tool rather than typed ("This means that savings can be
/// automatically calculated"), and the dates are fields, so the email says them one way.
/// </summary>
/// <param name="Name">Shown as the promotion's heading, such as "Filler Promo".</param>
/// <param name="Starts">The first day it runs (P4: required).</param>
/// <param name="Ends">The last day it runs (P4: required; P5: a day already past is a Must fix).</param>
/// <param name="Lines">Priced treatments and benefit lines, mixed, in her order (P3).</param>
/// <param name="Summary">Optional sentence or two before the lines.</param>
/// <param name="Audience">Who it is for, when not everyone (P2), such as "teachers &amp; school faculty".</param>
public sealed record Promotion(
    string Name,
    DateOnly Starts,
    DateOnly Ends,
    IReadOnlyList<PromotionLine> Lines,
    string? Summary = null,
    string? Audience = null);

/// <summary>
/// One line of a promotion: a treatment at a promotion price beside its regular price, or a typed
/// benefit. Typed like a tier's benefits, so its wording is the tool's, never typed.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "line")]
[JsonDerivedType(typeof(PricedTreatment), "treatment")]
[JsonDerivedType(typeof(BenefitLine), "benefit")]
public abstract record PromotionLine
{
    /// <summary>The line as the email carries it.</summary>
    public abstract string Describe();
}

/// <summary>
/// A treatment at a promotion price, beside its regular price, the saving worked out:
/// "Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)".
/// </summary>
public sealed record PricedTreatment(string Treatment, decimal Price, decimal RegularPrice) : PromotionLine
{
    /// <summary>What she saves: the regular price less the promotion price.</summary>
    [JsonIgnore]
    public decimal Saving => RegularPrice - Price;

    public override string Describe() =>
        $"{Treatment} — {Dollars.Text(Price)} (Regularly {Dollars.Text(RegularPrice)}, saving {Dollars.Text(Saving)})";
}

/// <summary>A typed benefit as a promotion line, such as "$50 credit toward your next appointment".</summary>
public sealed record BenefitLine(Benefit Benefit) : PromotionLine
{
    public override string Describe() => Benefit.Describe();
}
