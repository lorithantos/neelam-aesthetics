using System.Globalization;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>
/// A campaign being written, as a form binds to it: one field object per value of a
/// <see cref="CampaignDraft"/>, in its template's order. The Razor page only binds to this; what it
/// decides is here and unit-tested. Every field writes straight through to its draft slot as it is
/// typed: blank empties the slot, a value fills it as entered, and text the draft cannot hold (a
/// link that is not a web address, a price that is not a number) empties the slot and says why in
/// <see cref="Errors"/>. The draft's own rules are the last word: <see cref="Status"/> is what
/// <see cref="CampaignDraft.Build"/> and <see cref="CampaignReview"/> say, and nothing here exports.
/// </summary>
public sealed class CampaignEditor
{
    private CampaignEditor(CampaignDraft draft)
    {
        Draft = draft;
        Subject = new TextField(draft.Subject);
        Blocks = draft.Blocks.Select(BlockEditor.For).ToList();
    }

    public CampaignDraft Draft { get; }

    public string? TemplateName => Draft.TemplateName;

    public TextField Subject { get; }

    /// <summary>
    /// The client's own label for the campaign (<see cref="CampaignDraft.Label"/>): saved with the
    /// draft, and never part of the email or its checks.
    /// </summary>
    public string? Label
    {
        get => Draft.Label;
        set => Draft.Label = value;
    }

    /// <summary>Every block of the draft, in its template's order, spacers included.</summary>
    public IReadOnlyList<BlockEditor> Blocks { get; }

    /// <summary>A new campaign from a template: its fixed parts filled, everything else empty.</summary>
    public static CampaignEditor Start(CampaignTemplate template) => new(template.Start());

    /// <summary>A saved draft, every value with the origin it was saved with.</summary>
    public static CampaignEditor Open(CampaignDraft draft) => new(draft);

    /// <summary>What lists show: the subject once there is one.</summary>
    public string Title =>
        Draft.Subject.HasValue ? Draft.Subject.Value
        : Draft.TemplateName is { } template ? $"Untitled campaign from {template}"
        : "Untitled campaign";

    /// <summary>
    /// Text typed that the draft cannot hold yet, so saving now would lose it. Empty when saving
    /// keeps everything on the page.
    /// </summary>
    public IReadOnlyList<string> Errors()
    {
        var errors = new List<string>();
        foreach (var block in Blocks)
        {
            if (block.Error is { } e) errors.Add($"{block.Label}: {e}");
            if (block is not OfferBlockEditor { Offer: var offer }) continue;

            if (offer.TermsLink.Error is { } link) errors.Add($"{block.Label} › Terms link: {link}");
            for (var t = 0; t < offer.Tiers.Count; t++)
            {
                var tier = offer.Tiers[t];
                if (tier.Price.Error is { } price) errors.Add($"{block.Label} › Tier {t + 1} › Price: {price}");
                for (var b = 0; b < tier.Benefits.Count; b++)
                    if (tier.Benefits[b].Error is { } benefit)
                        errors.Add($"{block.Label} › Tier {t + 1} › Benefit {b + 1}: {benefit}");
            }
        }
        return errors;
    }

    /// <summary>
    /// Where the campaign stands: what is still missing, what the rule checks find, and the email as
    /// Square will show it. The checks and the preview run while parts are missing too, over what is
    /// filled in (<see cref="DraftSoFar"/>), with each missing part marked in the preview; the
    /// missing parts still block. A rules-only review never allows export.
    /// </summary>
    /// <param name="policy">The client's check policy; the starting defaults when null.</param>
    /// <param name="business">The client's registration, for the phone numbers it may publish.</param>
    public DraftStatus Status(CampaignPolicy? policy = null, BusinessContext? business = null)
    {
        var built = Draft.Build();
        if (built.Campaign is { } campaign)
        {
            var review = CampaignReview.Check(campaign, policy, business);
            return new DraftStatus([], review.Findings, review, EditorExport.PreviewBlocks(campaign));
        }
        return new DraftStatus(
            built.Problems,
            CampaignReview.Check(DraftSoFar.Campaign(Draft), policy, business).Findings,
            null,
            DraftSoFar.Preview(Draft));
    }
}

/// <param name="Missing">The draft's build problems, each one blocking; empty once it builds.</param>
/// <param name="Findings">
/// The rule checks: over the whole email once it builds, and over what is filled in until then.
/// </param>
/// <param name="Review">
/// The rule checks' report, only once the draft builds; never proofread, so never exportable. Null
/// while anything is missing: the checks over part of an email do build a report, but only its
/// findings are kept and the report is dropped, so none over part of an email is ever handed out.
/// </param>
/// <param name="Preview">
/// The email's blocks, for showing only; while parts are missing, each is a placeholder.
/// </param>
public sealed record DraftStatus(
    IReadOnlyList<Finding> Missing, IReadOnlyList<Finding> Findings, ReviewReport? Review, IReadOnlyList<EditorBlock> Preview);

/// <summary>One block of the campaign as the form holds it.</summary>
public abstract class BlockEditor
{
    private protected BlockEditor(BlockDraft block, bool isFixed)
    {
        Draft = block;
        IsFixed = isFixed;
    }

    internal BlockDraft Draft { get; }

    public string Label => Draft.Label;
    public BlockType Type => Draft.Type;
    public bool Required => Draft.Required;

    /// <summary>The template supplies it, the same in every campaign; shown, never edited.</summary>
    public bool IsFixed { get; }

    /// <summary>Typed text the block cannot hold yet; null when there is none.</summary>
    public virtual string? Error => null;

    /// <summary>A fixed block as it will be sent; empty for any other.</summary>
    public IReadOnlyList<EditorBlock> FixedPreview =>
        IsFixed && Draft.ToBlock() is { } block ? EditorExport.PreviewBlocks(new Campaign("", [block])) : [];

    internal static BlockEditor For(BlockDraft block) => block switch
    {
        ValueBlockDraft<string> v => new TextBlockEditor(v),
        ValueBlockDraft<IReadOnlyList<string>> v => new ParagraphsBlockEditor(v),
        ValueBlockDraft<CallToAction> v => new ButtonBlockEditor(v),
        ValueBlockDraft<SignOff> v => new SignOffBlockEditor(v),
        ValueBlockDraft<HeaderContent> v => new HeaderBlockEditor(v),
        ValueBlockDraft<ImageRef> v => new ImageBlockEditor(v),
        OfferBlockDraft o => new OfferBlockEditor(o),
        SpacerBlockDraft s => new SpacerBlockEditor(s),
        _ => throw new ArgumentOutOfRangeException(nameof(block), $"No form for a {block.Type} block."),
    };

    private protected static bool FromTemplate<T>(ValueBlockDraft<T> v) => v.Value.Origin == Origin.Template;
}

/// <summary>A heading, greeting or fine print: one piece of text.</summary>
public sealed class TextBlockEditor : BlockEditor
{
    internal TextBlockEditor(ValueBlockDraft<string> block) : base(block, FromTemplate(block)) =>
        Field = new TextField(block.Value);

    public TextField Field { get; }
}

/// <summary>Body text: one paragraph per chunk separated by a blank line.</summary>
public sealed class ParagraphsBlockEditor : BlockEditor
{
    private readonly Slot<IReadOnlyList<string>> _slot;
    private string _text;

    internal ParagraphsBlockEditor(ValueBlockDraft<IReadOnlyList<string>> block) : base(block, FromTemplate(block))
    {
        _slot = block.Value;
        _text = _slot.HasValue ? string.Join("\n\n", _slot.Value) : "";
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            var paragraphs = FormText.Paragraphs(_text);
            if (paragraphs.Count == 0) _slot.Clear();
            else _slot.Set(paragraphs);
        }
    }
}

/// <summary>A button: its text and the web address it goes to.</summary>
public sealed class ButtonBlockEditor : BlockEditor
{
    private readonly Slot<CallToAction> _slot;
    private string _label;
    private string _url;
    private string? _error;

    internal ButtonBlockEditor(ValueBlockDraft<CallToAction> block) : base(block, FromTemplate(block))
    {
        _slot = block.Value;
        _label = _slot.HasValue ? _slot.Value.Label : "";
        _url = _slot.HasValue ? _slot.Value.Url.OriginalString : "";
    }

    public string ButtonLabel { get => _label; set { _label = value ?? ""; Apply(); } }
    public string ButtonUrl { get => _url; set { _url = value ?? ""; Apply(); } }

    public override string? Error => _error;

    private void Apply()
    {
        var label = FormText.Clean(_label);
        var url = FormText.Clean(_url);
        _error = null;
        if (label.Length == 0 && url.Length == 0)
        {
            _slot.Clear();
            return;
        }
        var problems = new List<string>();
        if (label.Length == 0) problems.Add("Fill in the button's text.");
        var link = FormText.Link(url, "the button's link", problems);
        if (problems.Count > 0)
        {
            _error = string.Join(" ", problems);
            _slot.Clear();
            return;
        }
        _slot.Set(new CallToAction(label, link!));
    }
}

/// <summary>The sign-off: a valediction, who it is from, and an optional tagline.</summary>
public sealed class SignOffBlockEditor : BlockEditor
{
    private readonly Slot<SignOff> _slot;
    private string _valediction;
    private string _from;
    private string _tagline;
    private string? _error;

    internal SignOffBlockEditor(ValueBlockDraft<SignOff> block) : base(block, FromTemplate(block))
    {
        _slot = block.Value;
        _valediction = _slot.HasValue ? _slot.Value.Valediction : "";
        _from = _slot.HasValue ? _slot.Value.From : "";
        _tagline = _slot.HasValue ? _slot.Value.Tagline ?? "" : "";
    }

    public string Valediction { get => _valediction; set { _valediction = value ?? ""; Apply(); } }
    public string From { get => _from; set { _from = value ?? ""; Apply(); } }
    public string Tagline { get => _tagline; set { _tagline = value ?? ""; Apply(); } }

    public override string? Error => _error;

    private void Apply()
    {
        var (valediction, from, tagline) = (FormText.Clean(_valediction), FormText.Clean(_from), FormText.Clean(_tagline));
        _error = null;
        if (valediction.Length == 0 && from.Length == 0 && tagline.Length == 0)
        {
            _slot.Clear();
            return;
        }
        var problems = new List<string>();
        if (valediction.Length == 0) problems.Add("Fill in the valediction.");
        if (from.Length == 0) problems.Add("Fill in who it is from.");
        if (problems.Count > 0)
        {
            _error = string.Join(" ", problems);
            _slot.Clear();
            return;
        }
        _slot.Set(new SignOff(valediction, from, tagline.Length > 0 ? tagline : null));
    }
}

/// <summary>The header: the business's name over an optional photo from the image library.</summary>
public sealed class HeaderBlockEditor : BlockEditor, IPhotoChoice
{
    private readonly Slot<HeaderContent> _slot;
    private string _text;
    private string _photo;
    private string _alt;
    private string? _error;

    internal HeaderBlockEditor(ValueBlockDraft<HeaderContent> block) : base(block, FromTemplate(block))
    {
        _slot = block.Value;
        _text = _slot.HasValue ? _slot.Value.Text : "";
        _photo = _slot.HasValue ? _slot.Value.Photo?.Name ?? "" : "";
        _alt = _slot.HasValue ? _slot.Value.Photo?.AltText ?? "" : "";
    }

    public string Text { get => _text; set { _text = value ?? ""; Apply(); } }
    public string PhotoName { get => _photo; set { _photo = value ?? ""; Apply(); } }
    public string PhotoAltText { get => _alt; set { _alt = value ?? ""; Apply(); } }

    public override string? Error => _error;

    private void Apply()
    {
        var text = FormText.Clean(_text);
        var photo = FormText.Photo(_photo, _alt);
        _error = null;
        if (text.Length == 0)
        {
            if (photo is not null) _error = "Fill in the business name.";
            _slot.Clear();
            return;
        }
        _slot.Set(new HeaderContent(text, photo));
    }
}

/// <summary>A photo from the client's image library, or none when the block is optional.</summary>
public sealed class ImageBlockEditor : BlockEditor, IPhotoChoice
{
    private readonly Slot<ImageRef> _slot;
    private string _photo;
    private string _alt;

    internal ImageBlockEditor(ValueBlockDraft<ImageRef> block) : base(block, FromTemplate(block))
    {
        _slot = block.Value;
        _photo = _slot.HasValue ? _slot.Value.Name : "";
        _alt = _slot.HasValue ? _slot.Value.AltText ?? "" : "";
    }

    public string PhotoName { get => _photo; set { _photo = value ?? ""; Apply(); } }
    public string PhotoAltText { get => _alt; set { _alt = value ?? ""; Apply(); } }

    private void Apply()
    {
        if (FormText.Photo(_photo, _alt) is { } photo) _slot.Set(photo);
        else _slot.Clear();
    }
}

/// <summary>Space between blocks: nothing to write.</summary>
public sealed class SpacerBlockEditor : BlockEditor
{
    internal SpacerBlockEditor(SpacerBlockDraft block) : base(block, isFixed: false)
    {
    }
}

/// <summary>A tiered offer. Never fixed by a template: it is what a campaign is about.</summary>
public sealed class OfferBlockEditor : BlockEditor
{
    internal OfferBlockEditor(OfferBlockDraft block) : base(block, isFixed: false) =>
        Offer = new OfferEditor(block.Offer);

    public OfferEditor Offer { get; }
}

public sealed class OfferEditor
{
    private readonly OfferDraft _offer;
    private readonly List<TierEditor> _tiers;

    internal OfferEditor(OfferDraft offer)
    {
        _offer = offer;
        Name = new TextField(offer.Name);
        Summary = new TextField(offer.Summary);
        TiersNote = new TextField(offer.TiersNote);
        TermsLink = new LinkField(offer.TermsUrl, "the terms link");
        _tiers = offer.Tiers.Select(t => new TierEditor(t)).ToList();
    }

    public TextField Name { get; }
    public TextField Summary { get; }
    public TextField TiersNote { get; }
    public LinkField TermsLink { get; }

    /// <summary>Charged every month, the template's choice; a recurring offer needs a terms link.</summary>
    public bool IsRecurring => _offer.IsRecurring;

    public IReadOnlyList<TierEditor> Tiers => _tiers;

    public TierEditor AddTier()
    {
        var tier = new TierEditor(_offer.AddTier());
        _tiers.Add(tier);
        return tier;
    }

    public void RemoveTier(TierEditor tier)
    {
        var index = _tiers.IndexOf(tier);
        if (index < 0) return;
        _offer.RemoveTier(index);
        _tiers.RemoveAt(index);
    }

    /// <summary>
    /// Whether <see cref="CopyTier"/> can copy this tier: the draft copies every benefit's value,
    /// so a tier with a benefit not yet filled in has to be finished, or that benefit removed, first.
    /// </summary>
    public bool CanCopy(TierEditor tier) => _tiers.Contains(tier) && tier.Tier.Benefits.All(b => b.HasValue);

    /// <summary>
    /// A new tier from this one, through <see cref="OfferDraft.CopyTier"/>: the same name and price,
    /// which the rules flag until one of the two tiers changes each; and every benefit copied but
    /// marked unreviewed until it is changed or confirmed.
    /// </summary>
    public TierEditor CopyTier(TierEditor tier)
    {
        if (!CanCopy(tier))
            throw new InvalidOperationException("Fill in or remove the tier's empty benefits before copying it.");
        var copy = new TierEditor(_offer.CopyTier(_tiers.IndexOf(tier)));
        _tiers.Add(copy);
        return copy;
    }

    /// <summary>
    /// A known tier dropped in whole, at the end: its name, price and benefit lines, each as if she
    /// had typed it, so nothing is marked as not yet checked. A name or price that repeats another
    /// tier's is let in; the rules flag it, as they do a copied tier.
    /// </summary>
    public TierEditor AddKnownTier(KnownTier known)
    {
        var tier = AddTier();
        tier.Name.Text = known.Name;
        tier.Price.Text = known.PriceText;
        foreach (var benefit in known.Benefits) tier.AddKnownBenefit(benefit);
        return tier;
    }
}

public sealed class TierEditor
{
    private readonly List<BenefitEditor> _benefits;

    internal TierEditor(TierDraft tier)
    {
        Tier = tier;
        Name = new TextField(tier.Name);
        Price = new PriceField(tier.MonthlyPrice);
        _benefits = tier.Benefits.Select(b => new BenefitEditor(b)).ToList();
    }

    internal TierDraft Tier { get; }

    public TextField Name { get; }
    public PriceField Price { get; }

    public IReadOnlyList<BenefitEditor> Benefits => _benefits;

    /// <summary>An empty benefit at the end, to choose a kind for and fill in.</summary>
    public BenefitEditor AddBenefit()
    {
        var benefit = new BenefitEditor(Tier.AddEmptyBenefit());
        _benefits.Add(benefit);
        return benefit;
    }

    public void RemoveBenefit(BenefitEditor benefit)
    {
        var index = _benefits.IndexOf(benefit);
        if (index < 0) return;
        Tier.RemoveBenefit(index);
        _benefits.RemoveAt(index);
    }

    /// <summary>
    /// A known benefit picked into this tier, at the end: its kind and values in ordinary fields,
    /// counted as entered by her, so it is not marked as not yet checked and stays editable.
    /// </summary>
    public BenefitEditor AddKnownBenefit(Benefit benefit)
    {
        Tier.AddBenefit(benefit);
        var editor = new BenefitEditor(Tier.Benefits[^1]);
        _benefits.Add(editor);
        return editor;
    }

    /// <summary>
    /// This tier as a known item, exactly as written: its name, price and benefit lines. Null, with
    /// what is missing, until all of them are filled in.
    /// </summary>
    public (KnownTier? Tier, string? Problem) ToKnown()
    {
        if (!Tier.Name.HasValue || !Tier.MonthlyPrice.HasValue)
            return (null, "Fill in the tier's name and price first.");
        if (Tier.Benefits.Any(b => !b.HasValue))
            return (null, "Fill in or remove its empty benefits first.");
        return (new KnownTier(KnownItem.NewId(), Tier.Name.Value, Tier.MonthlyPrice.Value,
            Tier.Benefits.Select(b => b.Value).ToList()), null);
    }

    /// <summary>
    /// Whether "Save as a known item" is offered for this tier: while its name is not filled in (the
    /// button then says what is missing), or when the name is new to her known tiers, never a near miss.
    /// </summary>
    public bool SaveOffered(KnownItems known) =>
        !Tier.Name.HasValue || known.IsNew(KnownItemKind.Tier, Tier.Name.Value);
}

/// <summary>
/// One kind of typed benefit the form offers, by the name the saved JSON gives it. The list is the
/// <see cref="Benefit"/> types themselves; a test holds it to the types <see cref="Benefit"/>
/// declares, so a new kind of benefit cannot be added without its fields here.
/// </summary>
/// <param name="Amounts">The kind's amounts, which a known benefit line may limit.</param>
public sealed record BenefitKind(string Key, string Name, Type Type, IReadOnlyList<AmountField> Amounts)
{
    public static IReadOnlyList<BenefitKind> All { get; } =
    [
        new("birthday-credit", "Birthday credit", typeof(BirthdayCredit), [BirthdayCredit.AmountField]),
        new("percent-off", "Percent off treatments", typeof(PercentOff), [PercentOff.PercentField]),
        new("free-item", "Something free", typeof(FreeItem), [FreeItem.QuantityField]),
        new("discounted-item", "Percent off an item", typeof(DiscountedItem), [DiscountedItem.PercentField]),
        new("dollars-off", "Dollars off", typeof(DollarsOff), [DollarsOff.AmountField, DollarsOff.MinimumField]),
        new("credit", "Credit toward something", typeof(Credit), [Credit.AmountField]),
    ];

    /// <summary>The kind under <paramref name="key"/>, or null.</summary>
    public static BenefitKind? Of(string key) => All.FirstOrDefault(k => k.Key == key);
}

/// <summary>
/// One benefit of a tier: a kind and the fields that kind needs. A copied benefit stays unreviewed
/// until a person changes it or confirms it; changing any field is a decision, so it counts as review.
/// </summary>
public sealed class BenefitEditor
{
    private readonly Slot<Benefit> _slot;
    private string _kind = "";
    private string _amount = "";
    private string _percent = "";
    private string _appliesTo = "";
    private string _quantity = "";
    private string _item = "";
    private string _per = "";
    private string _condition = "";
    private FreeWording _wording = FreeWording.Complimentary;
    private string _minimum = "";
    private string _toward = "";
    private string? _error;

    internal BenefitEditor(Slot<Benefit> slot)
    {
        _slot = slot;
        if (!slot.HasValue) return;
        switch (slot.Value)
        {
            case BirthdayCredit b:
                (_kind, _amount) = ("birthday-credit", Number(b.Amount));
                break;
            case PercentOff p:
                (_kind, _percent, _appliesTo) = ("percent-off", Number(p.Percent), p.AppliesTo);
                break;
            case FreeItem f:
                (_kind, _quantity, _item, _per, _wording) = ("free-item", Number(f.Quantity), f.ItemName, f.Per, f.Wording);
                break;
            case DiscountedItem d:
                (_kind, _percent, _item, _per, _condition) = ("discounted-item", Number(d.Percent), d.ItemName, d.Per, d.Condition ?? "");
                break;
            case DollarsOff o:
                (_kind, _amount, _appliesTo, _minimum) = ("dollars-off", Number(o.Amount), o.AppliesTo, o.Minimum is { } least ? Number(least) : "");
                break;
            case Credit c:
                (_kind, _amount, _toward) = ("credit", Number(c.Amount), c.Toward);
                break;
        }
    }

    /// <summary>A benefit form on its own, outside any campaign: empty, or holding a known benefit to change.</summary>
    public static BenefitEditor Standalone(Benefit? benefit = null)
    {
        var slot = Slot<Benefit>.Empty();
        if (benefit is not null) slot.Set(benefit);
        return new BenefitEditor(slot);
    }

    /// <summary>The benefit as typed, once it is complete; null until then.</summary>
    public Benefit? Value => _slot.HasValue ? _slot.Value : null;

    /// <summary>This benefit as a known item, exactly as written; null until it is complete.</summary>
    public KnownBenefit? ToKnown() => Value is { } benefit ? new KnownBenefit(KnownItem.NewId(), benefit) : null;

    /// <summary>
    /// The treatment in its Item field as a known item, exactly as written; null for a kind with no
    /// item, or until the benefit is complete.
    /// </summary>
    public KnownTreatment? TreatmentToKnown() =>
        Value?.Item is { } item && item.Trim().Length > 0 ? new KnownTreatment(KnownItem.NewId(), item.Trim()) : null;

    /// <summary>
    /// <see cref="TreatmentToKnown"/>, only when it is new to her treatments (<see cref="KnownItems.IsNew"/>):
    /// never for one she already has, and never for a near miss, which the checks answer with "Did you
    /// mean" rather than an offer to keep the misspelling.
    /// </summary>
    public KnownTreatment? TreatmentToSave(KnownItems known) =>
        TreatmentToKnown() is { } treatment && known.IsNew(KnownItemKind.Treatment, treatment.Name) ? treatment : null;

    /// <summary>
    /// Whether "Save as a known item" is offered for this line: while it is unfinished (the button
    /// then says to fill it in), or when the line is new to her list (<see cref="KnownItems.IsNewBenefit"/>:
    /// one of her lines at another amount is not new) and its item is no near miss of a known treatment.
    /// </summary>
    public bool SaveOffered(KnownItems known) =>
        Value is not { } benefit
        || (known.IsNewBenefit(benefit)
            && (benefit.Item is not { } item || known.NearMiss(KnownItemKind.Treatment, item) is null));

    /// <summary>The kind's <see cref="BenefitKind.Key"/>, or blank before one is chosen.</summary>
    public string Kind { get => _kind; set { _kind = value ?? ""; Apply(); } }

    /// <summary>Birthday credit, dollars off, or a credit: dollars.</summary>
    public string Amount { get => _amount; set { _amount = value ?? ""; Apply(); } }

    /// <summary>Percent off, or percent off an item.</summary>
    public string Percent { get => _percent; set { _percent = value ?? ""; Apply(); } }

    /// <summary>Percent off or dollars off: what it applies to, e.g. "any qualifying treatments" or "units".</summary>
    public string AppliesTo { get => _appliesTo; set { _appliesTo = value ?? ""; Apply(); } }

    /// <summary>Dollars off: an optional least number, read "30+" before what it is off.</summary>
    public string Minimum { get => _minimum; set { _minimum = value ?? ""; Apply(); } }

    /// <summary>A credit: what it is toward, e.g. "your next appointment".</summary>
    public string Toward { get => _toward; set { _toward = value ?? ""; Apply(); } }

    /// <summary>Something free: how many.</summary>
    public string Quantity { get => _quantity; set { _quantity = value ?? ""; Apply(); } }

    /// <summary>Something free, or percent off an item: the item, e.g. "wellness injection".</summary>
    public string ItemName { get => _item; set { _item = value ?? ""; Apply(); } }

    /// <summary>Something free, or percent off an item: how often, e.g. "per visit".</summary>
    public string Per { get => _per; set { _per = value ?? ""; Apply(); } }

    /// <summary>Percent off an item: an optional qualifier, e.g. "any additional".</summary>
    public string Condition { get => _condition; set { _condition = value ?? ""; Apply(); } }

    /// <summary>
    /// Something free: "complimentary" (the default) or "free", her choice of word for the same
    /// benefit (owner, 2026-10-09). A choice, not something typed: it alone does not start a benefit.
    /// </summary>
    public FreeWording Wording { get => _wording; set { _wording = value; Apply(); } }

    public Origin Origin => _slot.Origin;

    /// <summary>Copied from another tier and not yet changed or confirmed.</summary>
    public bool IsUnreviewed => _slot.Origin == Origin.Copied;

    public string? CopiedFrom => _slot.CopiedFrom;

    /// <summary>The sentence the email will carry, once the benefit is complete.</summary>
    public string? Sentence => _slot.HasValue ? _slot.Value.Describe() : null;

    /// <summary>Typed text that does not make a benefit yet; null when there is none.</summary>
    public string? Error => _error;

    /// <summary>A person looked at the copied benefit and it is right for this tier as it stands.</summary>
    public void Confirm() => _slot.Confirm();

    private void Apply()
    {
        _error = null;
        var problems = new List<string>();
        Benefit? benefit = _kind switch
        {
            "birthday-credit" => Typed([_amount]) ? MakeBirthdayCredit(problems) : null,
            "percent-off" => Typed([_percent, _appliesTo]) ? MakePercentOff(problems) : null,
            "free-item" => Typed([_quantity, _item, _per]) ? MakeFreeItem(problems) : null,
            "discounted-item" => Typed([_percent, _item, _per, _condition]) ? MakeDiscountedItem(problems) : null,
            "dollars-off" => Typed([_amount, _appliesTo, _minimum]) ? MakeDollarsOff(problems) : null,
            "credit" => Typed([_amount, _toward]) ? MakeCredit(problems) : null,
            _ => null,
        };
        if (benefit is not null)
        {
            _slot.Set(benefit);
            return;
        }
        if (problems.Count > 0) _error = string.Join(" ", problems);
        _slot.Clear();
    }

    private static bool Typed(string[] fields) => fields.Any(f => FormText.Clean(f).Length > 0);

    private BirthdayCredit? MakeBirthdayCredit(List<string> problems)
    {
        var amount = FormText.Money(_amount, "the amount", problems);
        return problems.Count == 0 ? new BirthdayCredit(amount!.Value) : null;
    }

    private PercentOff? MakePercentOff(List<string> problems)
    {
        var percent = FormText.Whole(_percent, "the percentage", problems);
        var appliesTo = FormText.Need(_appliesTo, "what it is off", problems);
        return problems.Count == 0 ? new PercentOff(percent!.Value, appliesTo) : null;
    }

    private FreeItem? MakeFreeItem(List<string> problems)
    {
        var quantity = FormText.Whole(_quantity, "how many", problems);
        var item = FormText.Need(_item, "the item", problems);
        var per = FormText.Need(_per, "how often, such as \"per visit\"", problems);
        return problems.Count == 0 ? new FreeItem(quantity!.Value, item, per, _wording) : null;
    }

    private DiscountedItem? MakeDiscountedItem(List<string> problems)
    {
        var percent = FormText.Whole(_percent, "the percentage", problems);
        var item = FormText.Need(_item, "the item", problems);
        var per = FormText.Need(_per, "how often, such as \"per visit\"", problems);
        var condition = FormText.Clean(_condition);
        return problems.Count == 0 ? new DiscountedItem(percent!.Value, item, per, condition.Length > 0 ? condition : null) : null;
    }

    private DollarsOff? MakeDollarsOff(List<string> problems)
    {
        var amount = FormText.Money(_amount, "the amount", problems);
        var appliesTo = FormText.Need(_appliesTo, "what it is off, such as \"units\"", problems);
        // Typed as the email reads it, "30+", or as a number.
        var least = FormText.Clean(_minimum).TrimEnd('+').Trim();
        var minimum = least.Length > 0 ? FormText.Whole(least, "the minimum", problems) : null;
        return problems.Count == 0 ? new DollarsOff(amount!.Value, appliesTo, minimum) : null;
    }

    private Credit? MakeCredit(List<string> problems)
    {
        var amount = FormText.Money(_amount, "the amount", problems);
        var toward = FormText.Need(_toward, "what it is toward, such as \"your next appointment\"", problems);
        return problems.Count == 0 ? new Credit(amount!.Value, toward) : null;
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>One piece of text and the slot it fills. Blank empties the slot.</summary>
public sealed class TextField
{
    private readonly Slot<string> _slot;
    private string _text;

    internal TextField(Slot<string> slot)
    {
        _slot = slot;
        _text = slot.HasValue ? slot.Value : "";
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            var clean = FormText.Clean(_text);
            if (clean.Length == 0) _slot.Clear();
            else _slot.Set(clean);
        }
    }

    public Origin Origin => _slot.Origin;
}

/// <summary>A web address. Whether it should be https is the checks' to say, as for templates.</summary>
public sealed class LinkField
{
    private readonly Slot<Uri> _slot;
    private readonly string _what;
    private string _text;

    internal LinkField(Slot<Uri> slot, string what)
    {
        _slot = slot;
        _what = what;
        _text = slot.HasValue ? slot.Value.OriginalString : "";
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            var problems = new List<string>();
            var clean = FormText.Clean(_text);
            var link = clean.Length == 0 ? null : FormText.Link(clean, _what, problems);
            Error = problems.Count > 0 ? string.Join(" ", problems) : null;
            if (link is null) _slot.Clear();
            else _slot.Set(link);
        }
    }

    public string? Error { get; private set; }
}

/// <summary>A tier's monthly price, in dollars.</summary>
public sealed class PriceField
{
    private readonly Slot<decimal> _slot;
    private string _text;

    internal PriceField(Slot<decimal> slot)
    {
        _slot = slot;
        _text = slot.HasValue ? slot.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            var problems = new List<string>();
            var price = FormText.Clean(_text).Length == 0 ? null : FormText.Money(_text, "the price", problems);
            Error = problems.Count > 0 ? string.Join(" ", problems) : null;
            if (price is null) _slot.Clear();
            else _slot.Set(price.Value);
        }
    }

    public string? Error { get; private set; }
}

/// <summary>Turning what a form holds into values, the same way for every field.</summary>
internal static partial class FormText
{
    // Line endings from a browser form are \r\n; the saved text uses \n.
    public static string Clean(string value) => value.Replace("\r\n", "\n").Trim();

    public static IReadOnlyList<string> Paragraphs(string value) =>
        BlankLine().Split(Clean(value)).Select(Clean).Where(p => p.Length > 0).ToList();

    // "what" reads after "Fill in", e.g. "the amount" or "how many".

    public static string Need(string value, string what, List<string> problems)
    {
        var clean = Clean(value);
        if (clean.Length == 0) problems.Add($"Fill in {what}.");
        return clean;
    }

    public static Uri? Link(string value, string what, List<string> problems)
    {
        var clean = Clean(value);
        if (clean.Length == 0)
        {
            problems.Add($"Fill in {what}.");
            return null;
        }
        if (Uri.TryCreate(clean, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            return uri;
        problems.Add($"\"{clean}\" is not a web address; use the full address, starting https://.");
        return null;
    }

    public static decimal? Money(string value, string what, List<string> problems)
    {
        var clean = Clean(value).TrimStart('$').Trim();
        if (clean.Length == 0)
        {
            problems.Add($"Fill in {what}.");
            return null;
        }
        if (decimal.TryParse(clean, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var amount))
            return amount;
        problems.Add($"\"{Clean(value)}\" is not an amount in dollars, such as 149 or 149.50.");
        return null;
    }

    public static int? Whole(string value, string what, List<string> problems)
    {
        var clean = Clean(value).TrimEnd('%').Trim();
        if (clean.Length == 0)
        {
            problems.Add($"Fill in {what}.");
            return null;
        }
        if (int.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n;
        problems.Add($"\"{Clean(value)}\" is not a whole number.");
        return null;
    }

    public static ImageRef? Photo(string name, string alt)
    {
        var (n, a) = (Clean(name), Clean(alt));
        return n.Length == 0 ? null : new ImageRef(n, a.Length > 0 ? a : null);
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLine();
}
