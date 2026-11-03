namespace Neelam.Campaigns;

/// <summary>
/// One block of a template: its label, its type, whether a campaign must fill it, and what each
/// campaign gets in it. A block with <see cref="Fixed"/> content (a greeting, a sign-off, a
/// disclaimer) arrives filled and stays as it is; a block with <see cref="Default"/> content arrives
/// filled and is hers to change (owner, 2026-11-03); every other block starts empty.
/// </summary>
/// <param name="Recurring">For an offer block: whether the customer is charged repeatedly.</param>
/// <param name="Marker">For an offer block: what starts each benefit line, e.g. "🤍".</param>
/// <param name="Default">
/// What each campaign starts with and can change: any block but a spacer, an offer and a promotion
/// included, and for those two possibly only part of one. Never with <see cref="Fixed"/> content.
/// Optional in the saved JSON, so a template saved before has none and reads as it did.
/// </param>
public sealed record TemplateBlock(
    string Label, BlockType Type, bool Required = true, Block? Fixed = null, bool Recurring = false,
    string? Marker = null, BlockDefault? Default = null);

/// <summary>
/// A reusable layout for a kind of campaign, e.g. "membership announcement": the blocks the email
/// has, in order. It supplies the parts that stay the same from send to send, and the content she
/// chose for a part to start with (owner, 2026-11-03), and leaves every other part empty, never
/// pre-filled with last time's text, which is how stale copy goes out. Templates are the client's
/// own data; a client can build their own.
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
            if (b.Type == BlockType.Promotion && b.Fixed is not null)
                throw new ArgumentException($"\"{b.Label}\": a promotion is never fixed by a template.", nameof(blocks));
            if (b.Fixed is { } f && (f.Type != b.Type || f.Label != b.Label))
                throw new ArgumentException($"\"{b.Label}\": its fixed content is a different block.", nameof(blocks));
            if (b.Default is null) continue;
            if (b.Fixed is not null)
                throw new ArgumentException($"\"{b.Label}\": its content is either the same in every campaign or a start, not both.", nameof(blocks));
            if (b.Type == BlockType.Spacer)
                throw new ArgumentException($"\"{b.Label}\": a spacer has nothing to start with.", nameof(blocks));
            if (!BlockDraft.Start(b).HasContent)
                throw new ArgumentException($"\"{b.Label}\": its content to start with does not fit its type ({b.Type}).", nameof(blocks));
        }

        Name = name;
        Blocks = blocks;
    }

    /// <summary>
    /// A new campaign: the fixed parts filled, the parts with content to start with filled as if she had
    /// typed them (<see cref="Origin.Entered"/>), everything else empty.
    /// </summary>
    /// <param name="id">The template's id, recorded on the campaign; null when it has none, as before its first save.</param>
    /// <param name="version">When the version started from was saved, recorded on the campaign.</param>
    public CampaignDraft Start(Guid? id = null, DateTimeOffset? version = null) =>
        new(Name, Blocks.Select(BlockDraft.Start).ToList()) { TemplateId = id, TemplateVersion = version };
}
