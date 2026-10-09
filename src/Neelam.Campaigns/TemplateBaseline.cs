namespace Neelam.Campaigns;

/// <summary>
/// The parts every template should have, as block types: data, never a list a check holds. The
/// operator keeps a standard baseline for every client; a client may save its own, which replaces
/// the standard for that client, and saving an empty one turns the warnings off. A template that
/// lacks a part still saves; the editor says what it lacks. A part counts as present when the
/// template has a block of that type, required or optional.
/// </summary>
public sealed class TemplateBaseline
{
    public IReadOnlyList<BlockType> Parts { get; }

    /// <summary>The parts, each once, in the order given.</summary>
    public TemplateBaseline(IEnumerable<BlockType> parts)
    {
        var list = parts.Distinct().ToList();
        foreach (var part in list)
            if (!Enum.IsDefined(part))
                throw new ArgumentException($"{(int)part} is not a block type.", nameof(parts));
        Parts = list;
    }

    /// <summary>
    /// The standard baseline the tool starts with, in force until the operator saves one, as
    /// <see cref="CampaignPolicy.Default"/> is for policy. The owner's decision of 2026-10-09, from
    /// Neelam's five sent emails (Aug-Sep 2026): every one had a header, a headline, a button and a
    /// photo, and the latest signs off from the team. Greeting and body text were in four of the
    /// five, so they are not in it.
    /// </summary>
    public static TemplateBaseline Standard { get; } =
        new([BlockType.Header, BlockType.Heading, BlockType.SignOff, BlockType.Button, BlockType.Image]);

    /// <summary>No parts at all: a client that saves this gets no baseline warnings.</summary>
    public static TemplateBaseline None { get; } = new([]);

    /// <summary>Each part the blocks lack, in the baseline's order; empty when nothing is missing.</summary>
    public IReadOnlyList<BaselineGap> MissingFrom(IEnumerable<BlockType> blocks)
    {
        var present = blocks.ToHashSet();
        return Parts.Where(p => !present.Contains(p)).Select(BaselineGap.For).ToList();
    }

    public IReadOnlyList<BaselineGap> MissingFrom(CampaignTemplate template) =>
        MissingFrom(template.Blocks.Select(b => b.Type));
}

/// <summary>A baseline part a template lacks, said plainly. A warning only: the template still saves.</summary>
public sealed record BaselineGap(BlockType Part, string Message)
{
    public static BaselineGap For(BlockType part) =>
        new(part, $"Your templates usually have {Naming(part)}; this one doesn't.");

    /// <summary>How a sentence names one block of a type, such as "a sign-off" or "body text".</summary>
    public static string Naming(BlockType part) => part switch
    {
        BlockType.Header => "a header",
        BlockType.Heading => "a heading",
        BlockType.Greeting => "a greeting",
        BlockType.Paragraphs => "body text",
        BlockType.Offer => "an offer",
        BlockType.Button => "a button",
        BlockType.SignOff => "a sign-off",
        BlockType.FinePrint => "fine print",
        BlockType.Image => "an image",
        BlockType.Spacer => "a spacer",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "No wording for this block type."),
    };
}
