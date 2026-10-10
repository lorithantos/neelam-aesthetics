namespace Neelam.Campaigns;

/// <summary>
/// What a block type is for, in plain language, and what the checks look for in it: one entry of
/// the <see cref="BlockCatalog"/>, read from <c>Catalog/blocks.json</c> and <c>Catalog/rules.json</c>.
/// The type, and what it guarantees, is code (<see cref="BlockType"/>); everything said about it is
/// that data.
/// </summary>
/// <param name="Label">What the editor calls the type, also the usual label of a new block.</param>
/// <param name="Checks">
/// The lines under "What the checks look for in this block": each rule's description from
/// <c>rules.json</c>, or this block's own wording for it, in the order the block names its rules,
/// each line once.
/// </param>
/// <param name="Rules">The ids of the rules this type brings or answers, as the checks report them.</param>
public sealed record BlockGuide(BlockType Type, string Label, string Description, IReadOnlyList<string> Checks, IReadOnlyList<string> Rules);
