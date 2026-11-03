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

    private TemplateEditor(BlockCatalog catalog, string name, IEnumerable<EditableBlock> blocks)
    {
        Catalog = catalog;
        Name = name;
        _blocks = [.. blocks];
    }

    /// <summary>
    /// The block types this editor offers and what is said about them: the shipped catalog today,
    /// and the seam for a client's own (see <see cref="BlockCatalog"/>).
    /// </summary>
    public BlockCatalog Catalog { get; }

    public string Name { get; set; }

    public IReadOnlyList<EditableBlock> Blocks => _blocks;

    public static TemplateEditor StartBlank(BlockCatalog catalog) => new(catalog, "", []);

    /// <summary>
    /// A new template starting with one block of each part the baseline names (owner, 2026-10-09:
    /// what every template should have is what you get when you say new), in the baseline's own
    /// order, which she sets ("It should use the same order she does"). Each is a block as
    /// <see cref="Add"/> makes it: its usual label, written by each campaign, never fixed content.
    /// An empty baseline is a blank start.
    /// </summary>
    public static TemplateEditor StartFrom(TemplateBaseline baseline, BlockCatalog catalog)
    {
        var editor = StartBlank(catalog);
        foreach (var part in baseline.Parts)
            editor.Add(part);
        editor.FromBaseline = editor._blocks.Count > 0;
        return editor;
    }

    /// <summary>Whether the blocks it started with came from the baseline, so the page can say so.</summary>
    public bool FromBaseline { get; private set; }

    /// <summary>An existing template, to change and save as its next version.</summary>
    public static TemplateEditor Open(CampaignTemplate template, BlockCatalog catalog) =>
        new(catalog, template.Name, template.Blocks.Select(EditableBlock.Of));

    /// <summary>
    /// A new template starting from an existing one. Every block is copied, so changing the copy
    /// never touches the original.
    /// </summary>
    public static TemplateEditor CopyOf(CampaignTemplate template, BlockCatalog catalog) =>
        new(catalog, $"Copy of {template.Name}", template.Blocks.Select(EditableBlock.Of));

    /// <summary>Adds a block of that type at the end, labelled by its type, numbered if that label is taken.</summary>
    public EditableBlock Add(BlockType type)
    {
        var name = Catalog.For(type).Label;
        var label = name;
        for (var n = 2; _blocks.Any(b => Same(b.Label, label)); n++) label = $"{name} {n}";
        var block = new EditableBlock(type, label);
        _blocks.Add(block);
        return block;
    }

    public void Remove(EditableBlock block) => _blocks.Remove(block);

    public void MoveUp(EditableBlock block) => MoveTo(_blocks.IndexOf(block), _blocks.IndexOf(block) - 1);

    public void MoveDown(EditableBlock block) => MoveTo(_blocks.IndexOf(block), _blocks.IndexOf(block) + 1);

    /// <summary>
    /// Takes the block at <paramref name="from"/> out and puts it back so that it ends up at
    /// <paramref name="to"/>, every other block keeping its order: what dragging a block in the page's
    /// Arrange blocks list does, and Up and Down a step at a time. False, and nothing changed, when
    /// either place is outside the list or they are the same.
    /// </summary>
    public bool MoveTo(int from, int to)
    {
        if (from < 0 || from >= _blocks.Count || to < 0 || to >= _blocks.Count || from == to) return false;
        var block = _blocks[from];
        _blocks.RemoveAt(from);
        _blocks.Insert(to, block);
        return true;
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
                errors.Add(new($"A {Catalog.For(block.Type).Label.ToLowerInvariant()} block", "Give the block a label."));
            errors.AddRange(block.ContentErrors().Select(e => new TemplateProblem(block.Label.Trim(), e)));
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

    // Best effort while editing: fixed content, or content to start with, that is not complete yet is
    // left out, so the preview shows a placeholder and Errors says why.
    private List<TemplateBlock> TemplateBlocks() => _blocks.Select(b => b.ToTemplateBlock()).ToList();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a template block gives each campaign (owner, 2026-11-03: one choice, not two checkboxes, "What
/// each campaign gets").
/// </summary>
public enum BlockContent
{
    /// <summary>Nothing: each campaign fills it in.</summary>
    Empty,

    /// <summary>The same content in every campaign: shown on the campaign page, never edited there.</summary>
    Fixed,

    /// <summary>Content each campaign starts with, as if she had typed it, and can change.</summary>
    Default,
}

/// <summary>
/// One block as the editor holds it: plain fields a form binds to. Which fields matter depends on
/// <see cref="Type"/>; the others are ignored. An offer's or a promotion's content to start with is
/// written through the campaign page's own fields (<see cref="DefaultOffer"/>, <see cref="DefaultPromotion"/>).
/// </summary>
public sealed partial class EditableBlock : IPhotoChoice
{
    private BlockContent _content;
    private bool _recurring;

    // An offer's or a promotion's content to start with, as the campaign page's fields write it: a block
    // of its own, never part of a campaign. Null for every other type, whose content is the plain fields.
    private BlockDraft? _start;
    private BlockEditor? _startEditor;

    internal EditableBlock(BlockType type, string label)
    {
        Type = type;
        Label = label;
        if (type is BlockType.Offer or BlockType.Promotion) Begin(null);
    }

    // A fresh offer or promotion to write the content to start with into, holding start's content when given.
    private void Begin(BlockDefault? start)
    {
        _start = BlockDraft.From(new TemplateBlock(Label, Type));
        if (_start is OfferBlockDraft offer) offer.Offer.IsRecurring = _recurring;
        start?.FillInto(_start);
        _startEditor = BlockEditor.For(_start);
    }

    public BlockType Type { get; }

    public string Label { get; set; }

    /// <summary>Whether a campaign must have it. Fixed blocks are always filled.</summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// What each campaign gets in this block: nothing, the same content every time (only where
    /// <see cref="CanBeFixed"/>), or content to start with (only where <see cref="CanStartFilled"/>).
    /// Changing it keeps what is typed, so switching between the same and a start loses nothing.
    /// </summary>
    public BlockContent Content
    {
        get => _content;
        set
        {
            if (value == BlockContent.Fixed && !CanBeFixed)
                throw new InvalidOperationException($"A block of type {Type} cannot be fixed.");
            if (value == BlockContent.Default && !CanStartFilled)
                throw new InvalidOperationException($"A block of type {Type} has nothing to start with.");
            _content = value;
        }
    }

    /// <summary>
    /// Whether the template supplies this block's content, the same in every campaign. Never for an
    /// offer or a promotion, which are what a campaign is about; a spacer has no content to fix.
    /// Setting it false leaves content to start with as it is.
    /// </summary>
    public bool IsFixed
    {
        get => _content == BlockContent.Fixed;
        set => Content = value ? BlockContent.Fixed : _content == BlockContent.Fixed ? BlockContent.Empty : _content;
    }

    public bool CanBeFixed => Type is not (BlockType.Offer or BlockType.Promotion or BlockType.Spacer);

    /// <summary>
    /// Whether this block can give each campaign content to start with: every type but a spacer, an offer
    /// and a promotion included (owner, 2026-11-03: "Offers can also have defaults").
    /// </summary>
    public bool CanStartFilled => Type != BlockType.Spacer;

    /// <summary>An offer's content to start with, through the campaign page's offer fields; null for any other type.</summary>
    public OfferEditor? DefaultOffer => (_startEditor as OfferBlockEditor)?.Offer;

    /// <summary>A promotion's content to start with, through the campaign page's promotion fields; null for any other type.</summary>
    public PromotionEditor? DefaultPromotion => (_startEditor as PromotionBlockEditor)?.Promotion;

    /// <summary>Offer only: whether the customer is charged repeatedly.</summary>
    public bool Recurring
    {
        get => _recurring;
        set
        {
            _recurring = value;
            // The offer fields ask for a terms link on a recurring offer, as the campaign page does.
            if (_start is OfferBlockDraft offer) offer.Offer.IsRecurring = value;
        }
    }

    /// <summary>Offer only: what starts each benefit line, e.g. "🤍". Blank for the default.</summary>
    public string Marker { get; set; } = "";

    // A value block's content, fixed or to start with, by type.

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

    /// <summary>
    /// A fixed block as it will be sent, as it stands, for its own card (the campaign page shows its
    /// fixed blocks the same way); empty while its content is not complete, and for any other block.
    /// </summary>
    public IReadOnlyList<EditorBlock> FixedPreview =>
        _content == BlockContent.Fixed && ValueContent().Block is { } block ? EditorExport.PreviewBlocks(new Campaign("", [block])) : [];

    /// <summary>
    /// A block's content to start with as a new campaign shows it, for its own card: what is there as it
    /// will be sent, and for an offer or a promotion each part not given yet marked as each campaign's to
    /// fill in. Empty while a value block's content is not complete, while nothing is given, and for a
    /// block with no content to start with.
    /// </summary>
    public IReadOnlyList<EditorBlock> DefaultPreview =>
        _content == BlockContent.Default && ToTemplateBlock() is { Default: not null } t
            ? EditorExport.Render(DraftSoFar.Shown(BlockDraft.Start(t with { Required = true })))
            : [];

    internal TemplateBlock ToTemplateBlock()
    {
        var content = _content == BlockContent.Empty || _start is not null ? null : ValueContent().Block;
        return new(
            Label.Trim(), Type, Required,
            Fixed: _content == BlockContent.Fixed ? content : null,
            Recurring: Type == BlockType.Offer && Recurring,
            Marker: Type == BlockType.Offer && !string.IsNullOrWhiteSpace(Marker) ? Marker.Trim() : null,
            Default: _content != BlockContent.Default ? null
                : _start is not null ? BlockDefault.Of(_start)
                : content is null ? null
                : BlockDefault.Of(BlockDraft.From(new TemplateBlock(Label.Trim(), Type, Fixed: content))));
    }

    /// <summary>
    /// What stops this block's content being saved: a value block's content not complete, or for an
    /// offer or a promotion to start with, nothing given, text its fields cannot hold, or a copied
    /// benefit nobody has checked. None when each campaign fills it in.
    /// </summary>
    internal IReadOnlyList<string> ContentErrors()
    {
        if (_content == BlockContent.Empty) return [];
        if (_startEditor is null) return ValueContent().Errors;

        var errors = CampaignEditor.PartErrors(_startEditor).ToList();
        if (!_start!.HasContent)
            errors.Add("Fill in something for each campaign to start with, or let each campaign fill it in.");
        if (DefaultOffer is { } offer)
            for (var t = 0; t < offer.Tiers.Count; t++)
            for (var b = 0; b < offer.Tiers[t].Benefits.Count; b++)
                if (offer.Tiers[t].Benefits[b] is { IsUnreviewed: true } copied)
                    errors.Add($"Tier {t + 1} › Benefit {b + 1} was copied from {copied.CopiedFrom} and not checked: " +
                               "change it, or confirm it is right for this tier.");
        return errors;
    }

    /// <summary>
    /// A value block's content, fixed or to start with, or why it cannot be made yet. No block and no
    /// errors when each campaign fills it in, and for an offer, a promotion or a spacer.
    /// </summary>
    internal (Block? Block, IReadOnlyList<string> Errors) ValueContent()
    {
        if (_content == BlockContent.Empty) return (null, []);
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
        if (t.Default is { } start)
        {
            block.Content = BlockContent.Default;
            if (block._start is not null) block.Begin(start);
            else if (BlockDraft.Start(t).ToBlock() is { } content) block.Load(content);
            return block;
        }
        if (t.Fixed is null) return block;

        block.IsFixed = true;
        block.Load(t.Fixed);
        return block;
    }

    // A value block's content into the plain fields.
    private void Load(Block content)
    {
        switch (content)
        {
            case HeadingBlock h: Text = h.Text; break;
            case GreetingBlock g: Text = g.Text; break;
            case FinePrintBlock f: Text = f.Text; break;
            case ParagraphsBlock p: Paragraphs = string.Join("\n\n", p.Paragraphs); break;
            case ButtonBlock b: (ButtonLabel, ButtonUrl) = (b.Action.Label, b.Action.Url.OriginalString); break;
            case SignOffBlock s:
                (Valediction, From, Tagline) = (s.SignOff.Valediction, s.SignOff.From, s.SignOff.Tagline ?? "");
                break;
            case HeaderBlock h:
                Text = h.Text;
                (PhotoName, PhotoAltText) = (h.Photo?.Name ?? "", h.Photo?.AltText ?? "");
                break;
            case ImageBlock i: (PhotoName, PhotoAltText) = (i.Image.Name, i.Image.AltText ?? ""); break;
        }
    }

    // Line endings from a browser form are \r\n; the saved text uses \n.
    private static string Clean(string value) => value.Replace("\r\n", "\n").Trim();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLine();
}
