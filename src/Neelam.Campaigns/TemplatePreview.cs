namespace Neelam.Campaigns;

/// <summary>
/// The shape of the email a template makes, for the editor to show while it is built: the same
/// Square blocks, in the same order and with text grouped the same way, as the export would give.
/// Fixed content appears as it will be sent; each block a campaign fills appears as a placeholder
/// naming it, e.g. "‹Opening: written for each campaign›".
/// </summary>
public static class TemplatePreview
{
    public static IReadOnlyList<EditorBlock> Blocks(IEnumerable<TemplateBlock> template) =>
        EditorExport.Render(template.Select(b => b switch
        {
            { Fixed: { } content } => content,
            { Type: BlockType.Spacer } => new SpacerBlock(b.Label),
            _ => new PlaceholderBlock(b.Label, b.Type, Placeholder(b)),
        }));

    /// <summary>The preview as plain text, the way <see cref="EditorExport.Preview"/> shows an email.</summary>
    public static string PlainText(IEnumerable<TemplateBlock> template) => EditorExport.ToPlainText(Blocks(template));

    /// <summary>True for a placeholder's text, so the editor can set it apart from fixed content.</summary>
    public static bool IsPlaceholder(string line) => line.StartsWith(Open) && line.EndsWith(Close);

    private const string Open = "‹";
    private const string Close = "›";

    /// <summary>Text marked as a placeholder, as <see cref="IsPlaceholder"/> recognises it; the campaign editor's preview marks its missing parts the same way.</summary>
    internal static string Marked(string text) => $"{Open}{text}{Close}";

    private static string Placeholder(TemplateBlock b)
    {
        var what = b.Type switch
        {
            BlockType.Image => "chosen",
            BlockType.Button => "label and link chosen",
            BlockType.Offer => "set out",
            _ => "written",
        };
        return Marked($"{b.Label}: {what} for each campaign{(b.Required ? "" : ", or left out")}");
    }
}

/// <summary>
/// A block a campaign will fill, standing in for it in a template's preview. Never part of a
/// campaign, never saved.
/// </summary>
internal sealed record PlaceholderBlock(string Label, BlockType Of, string Text) : Block(Label)
{
    public override BlockType Type => Of;
}
