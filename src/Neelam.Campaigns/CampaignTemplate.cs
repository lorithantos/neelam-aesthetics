namespace Neelam.Campaigns;

/// <summary>
/// One block of a template: its label, its type, whether a campaign must fill it, and any content
/// the template fixes. A block with <see cref="Fixed"/> content (a greeting, a sign-off, a
/// disclaimer) arrives filled; every other block starts empty.
/// </summary>
/// <param name="Recurring">For an offer block: whether the customer is charged repeatedly.</param>
/// <param name="Marker">For an offer block: what starts each benefit line, e.g. "🤍".</param>
public sealed record TemplateBlock(
    string Label, BlockType Type, bool Required = true, Block? Fixed = null, bool Recurring = false,
    string? Marker = null);

/// <summary>
/// A reusable layout for a kind of campaign, e.g. "membership announcement": the blocks the email
/// has, in order. It supplies the parts that stay the same from send to send and leaves every
/// part that must be decided per campaign empty, never pre-filled with last time's text, which is
/// how stale copy goes out. Templates are the client's own data; a client can build their own.
/// </summary>
public sealed record CampaignTemplate
{
    public string Name { get; }

    public IReadOnlyList<TemplateBlock> Blocks { get; }

    public CampaignTemplate(string name, IReadOnlyList<TemplateBlock> blocks)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A template needs a name.", nameof(name));
        if (blocks.Count == 0)
            throw new ArgumentException("A template needs at least one block.", nameof(blocks));

        var repeated = blocks.GroupBy(b => b.Label.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw new ArgumentException($"Two blocks are labelled \"{repeated.Key}\"; findings could not say which.", nameof(blocks));

        foreach (var b in blocks)
        {
            if (string.IsNullOrWhiteSpace(b.Label))
                throw new ArgumentException("Every block needs a label.", nameof(blocks));
            // An offer is what a campaign is about; a template that fixed it would send last
            // time's offer again.
            if (b.Type == BlockType.Offer && b.Fixed is not null)
                throw new ArgumentException($"\"{b.Label}\": an offer is never fixed by a template.", nameof(blocks));
            if (b.Fixed is { } f && (f.Type != b.Type || f.Label != b.Label))
                throw new ArgumentException($"\"{b.Label}\": its fixed content is a different block.", nameof(blocks));
        }

        Name = name;
        Blocks = blocks;
    }

    public CampaignDraft Start() => new(Name, Blocks.Select(BlockDraft.From).ToList());
}
