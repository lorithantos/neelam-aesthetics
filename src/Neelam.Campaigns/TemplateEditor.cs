using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>Something that stops a template being saved, and where.</summary>
/// <param name="Where">The block's label, or "Template" for the template as a whole.</param>
public sealed record TemplateProblem(string Where, string Message);

/// <summary>
/// A template being built or changed: a name and an ordered list of blocks, each editable in place.
/// The Razor page only binds to this; everything it decides is here and unit-tested. Nothing is
/// checked as it is typed: <see cref="Errors"/> says what stops a save, <see cref="Advice"/> what the
/// checks would say about every campaign, and <see cref="Build"/> gives the template once there are
/// no errors. The rules a template must meet are <see cref="CampaignTemplate"/>'s own; this adds only
/// what a form needs (fixed content that is filled in, a link that is a web address).
/// </summary>
public sealed class TemplateEditor
{
    private readonly List<EditableBlock> _blocks;

    private TemplateEditor(string name, IEnumerable<EditableBlock> blocks)
    {
        Name = name;
        _blocks = [.. blocks];
    }

    public string Name { get; set; }

    public IReadOnlyList<EditableBlock> Blocks => _blocks;

    public static TemplateEditor StartBlank() => new("", []);

    /// <summary>An existing template, to change and save as its next version.</summary>
    public static TemplateEditor Open(CampaignTemplate template) =>
        new(template.Name, template.Blocks.Select(EditableBlock.Of));

    /// <summary>
    /// A new template starting from an existing one. Every block is copied, so changing the copy
    /// never touches the original.
    /// </summary>
    public static TemplateEditor CopyOf(CampaignTemplate template) =>
        new($"Copy of {template.Name}", template.Blocks.Select(EditableBlock.Of));

    /// <summary>Adds a block of that type at the end, labelled by its type, numbered if that label is taken.</summary>
    public EditableBlock Add(BlockType type)
    {
        var name = BlockGuide.For(type).Name;
        var label = name;
        for (var n = 2; _blocks.Any(b => Same(b.Label, label)); n++) label = $"{name} {n}";
        var block = new EditableBlock(type, label);
        _blocks.Add(block);
        return block;
    }

    public void Remove(EditableBlock block) => _blocks.Remove(block);

    public void MoveUp(EditableBlock block) => Move(block, -1);

    public void MoveDown(EditableBlock block) => Move(block, +1);

    private void Move(EditableBlock block, int by)
    {
        var from = _blocks.IndexOf(block);
        var to = from + by;
        if (from < 0 || to < 0 || to >= _blocks.Count) return;
        (_blocks[from], _blocks[to]) = (_blocks[to], _blocks[from]);
    }

    /// <summary>What stops the template being saved. Empty when <see cref="Build"/> will succeed.</summary>
    public IReadOnlyList<TemplateProblem> Errors()
    {
        var errors = new List<TemplateProblem>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add(new("Template", "Give the template a name."));
        if (_blocks.Count == 0) errors.Add(new("Template", "Add at least one block."));

        foreach (var block in _blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Label))
                errors.Add(new($"A {BlockGuide.For(block.Type).Name.ToLowerInvariant()} block", "Give the block a label."));
            errors.AddRange(block.FixedContent().Errors.Select(e => new TemplateProblem(block.Label.Trim(), e)));
        }
        foreach (var repeated in _blocks.Where(b => !string.IsNullOrWhiteSpace(b.Label))
                     .GroupBy(b => b.Label.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add(new(repeated.Key, "Two blocks have this label; findings could not say which one they mean."));

        // The template's own rules are the last word, so nothing this editor missed can be saved.
        if (errors.Count == 0)
        {
            try
            {
                _ = new CampaignTemplate(Name.Trim(), TemplateBlocks());
            }
            catch (ArgumentException ex)
            {
                errors.Add(new("Template", ex.Message));
            }
        }
        return errors;
    }

    /// <summary>What the checks would say about every campaign from this template; see <see cref="TemplateAdvice"/>.</summary>
    public IReadOnlyList<Finding> Advice(CampaignPolicy? policy = null, IReadOnlyCollection<string>? libraryPhotos = null) =>
        TemplateAdvice.For(TemplateBlocks(), policy, libraryPhotos);

    /// <summary>
    /// The baseline parts this template lacks, as it stands. Warnings only: they never stop a
    /// save, since a short note such as "we're closed Monday" may rightly have no button.
    /// </summary>
    public IReadOnlyList<BaselineGap> Missing(TemplateBaseline baseline) => baseline.MissingFrom(_blocks.Select(b => b.Type));

    /// <summary>The email's shape as it stands, even while there are errors; see <see cref="TemplatePreview"/>.</summary>
    public IReadOnlyList<EditorBlock> Preview() => TemplatePreview.Blocks(TemplateBlocks());

    public CampaignTemplate Build()
    {
        var errors = Errors();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "The template cannot be saved yet: " + string.Join("; ", errors.Select(e => $"{e.Where}: {e.Message}")));
        return new CampaignTemplate(Name.Trim(), TemplateBlocks());
    }

    // Best effort while editing: fixed content that is not complete yet is left out, so the
    // preview shows a placeholder and Errors says why.
    private List<TemplateBlock> TemplateBlocks() => _blocks.Select(b => b.ToTemplateBlock()).ToList();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One block as the editor holds it: plain fields a form binds to. Which fields matter depends on
/// <see cref="Type"/>; the others are ignored.
/// </summary>
public sealed partial class EditableBlock : IPhotoChoice
{
    private bool _isFixed;

    internal EditableBlock(BlockType type, string label)
    {
        Type = type;
        Label = label;
    }

    public BlockType Type { get; }

    public string Label { get; set; }

    /// <summary>Whether a campaign must fill it. Fixed blocks are always filled.</summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// Whether the template supplies this block's content, the same in every campaign. Never for an
    /// offer, which is what a campaign is about; a spacer has no content to fix.
    /// </summary>
    public bool IsFixed
    {
        get => _isFixed;
        set
        {
            if (value && !CanBeFixed)
                throw new InvalidOperationException($"A {BlockGuide.For(Type).Name.ToLowerInvariant()} block cannot be fixed.");
            _isFixed = value;
        }
    }

    public bool CanBeFixed => Type is not (BlockType.Offer or BlockType.Spacer);

    /// <summary>Offer only: whether the customer is charged repeatedly.</summary>
    public bool Recurring { get; set; }

    /// <summary>Offer only: what starts each benefit line, e.g. "🤍". Blank for the default.</summary>
    public string Marker { get; set; } = "";

    // Fixed content, by type.

    /// <summary>A heading, greeting, fine print, or the header's business name.</summary>
    public string Text { get; set; } = "";

    /// <summary>Paragraphs, separated by a blank line.</summary>
    public string Paragraphs { get; set; } = "";

    public string ButtonLabel { get; set; } = "";
    public string ButtonUrl { get; set; } = "";

    public string Valediction { get; set; } = "";
    public string From { get; set; } = "";
    public string Tagline { get; set; } = "";

    /// <summary>The image block's photo, or the header's: a name in the client's image library.</summary>
    public string PhotoName { get; set; } = "";
    public string PhotoAltText { get; set; } = "";

    internal TemplateBlock ToTemplateBlock() => new(
        Label.Trim(), Type, Required, FixedContent().Block,
        Recurring: Type == BlockType.Offer && Recurring,
        Marker: Type == BlockType.Offer && !string.IsNullOrWhiteSpace(Marker) ? Marker.Trim() : null);

    /// <summary>The fixed block, or why it cannot be made yet. No block and no errors when not fixed.</summary>
    internal (Block? Block, IReadOnlyList<string> Errors) FixedContent()
    {
        if (!IsFixed) return (null, []);
        var label = Label.Trim();
        var errors = new List<string>();

        string Need(string value, string what)
        {
            var clean = Clean(value);
            if (clean.Length == 0) errors.Add($"Fill in the {what}, or let each campaign write it.");
            return clean;
        }

        Block? block = Type switch
        {
            BlockType.Heading => Make(Need(Text, "heading"), t => new HeadingBlock(label, t)),
            BlockType.Greeting => Make(Need(Text, "greeting"), t => new GreetingBlock(label, t)),
            BlockType.FinePrint => Make(Need(Text, "fine print"), t => new FinePrintBlock(label, t)),
            BlockType.Paragraphs => MakeParagraphs(),
            BlockType.Button => MakeButton(),
            BlockType.SignOff => MakeSignOff(),
            BlockType.Header => Make(Need(Text, "business name"), t => new HeaderBlock(label, t, Photo())),
            BlockType.Image => Make(Need(PhotoName, "photo"), _ => new ImageBlock(label, Photo()!)),
            _ => null,
        };
        return errors.Count == 0 ? (block, []) : (null, errors);

        Block? Make(string value, Func<string, Block> make) => value.Length == 0 ? null : make(value);

        Block? MakeParagraphs()
        {
            var paragraphs = BlankLine().Split(Clean(Paragraphs)).Select(Clean).Where(p => p.Length > 0).ToList();
            if (paragraphs.Count == 0) errors.Add("Fill in at least one paragraph, or let each campaign write them.");
            return paragraphs.Count == 0 ? null : new ParagraphsBlock(label, paragraphs);
        }

        Block? MakeButton()
        {
            var text = Need(ButtonLabel, "button's label");
            var url = Need(ButtonUrl, "button's link");
            if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _))
                errors.Add($"\"{url}\" is not a web address; use the full address, starting https://.");
            return errors.Count == 0 ? new ButtonBlock(label, new CallToAction(text, new Uri(url))) : null;
        }

        Block? MakeSignOff()
        {
            var valediction = Need(Valediction, "valediction");
            var from = Need(From, "name it is from");
            var tagline = Clean(Tagline);
            return errors.Count == 0 ? new SignOffBlock(label, new SignOff(valediction, from, tagline.Length > 0 ? tagline : null)) : null;
        }
    }

    private ImageRef? Photo()
    {
        var name = Clean(PhotoName);
        var alt = Clean(PhotoAltText);
        return name.Length == 0 ? null : new ImageRef(name, alt.Length > 0 ? alt : null);
    }

    internal static EditableBlock Of(TemplateBlock t)
    {
        var block = new EditableBlock(t.Type, t.Label)
        {
            Required = t.Required,
            Recurring = t.Recurring,
            Marker = t.Marker ?? "",
        };
        if (t.Fixed is null) return block;

        block.IsFixed = true;
        switch (t.Fixed)
        {
            case HeadingBlock h: block.Text = h.Text; break;
            case GreetingBlock g: block.Text = g.Text; break;
            case FinePrintBlock f: block.Text = f.Text; break;
            case ParagraphsBlock p: block.Paragraphs = string.Join("\n\n", p.Paragraphs); break;
            case ButtonBlock b: (block.ButtonLabel, block.ButtonUrl) = (b.Action.Label, b.Action.Url.OriginalString); break;
            case SignOffBlock s:
                (block.Valediction, block.From, block.Tagline) = (s.SignOff.Valediction, s.SignOff.From, s.SignOff.Tagline ?? "");
                break;
            case HeaderBlock h:
                block.Text = h.Text;
                (block.PhotoName, block.PhotoAltText) = (h.Photo?.Name ?? "", h.Photo?.AltText ?? "");
                break;
            case ImageBlock i: (block.PhotoName, block.PhotoAltText) = (i.Image.Name, i.Image.AltText ?? ""); break;
        }
        return block;
    }

    // Line endings from a browser form are \r\n; the saved text uses \n.
    private static string Clean(string value) => value.Replace("\r\n", "\n").Trim();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLine();
}
