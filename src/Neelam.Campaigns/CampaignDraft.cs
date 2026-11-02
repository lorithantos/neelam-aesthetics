namespace Neelam.Campaigns;

/// <summary>
/// A campaign being written, block by block as its template laid it out. Every value is a
/// <see cref="Slot{T}"/>, so the draft knows what a person entered, what the template supplied,
/// and what was copied and not yet looked at. <see cref="Build"/> turns it into a
/// <see cref="Campaign"/> only when nothing required is missing and nothing copied is unreviewed.
/// </summary>
public sealed class CampaignDraft
{
    public string? TemplateName { get; }

    /// <summary>
    /// The template the campaign was made from, or last brought up to (<see cref="TemplateUpdate"/>), by
    /// its id. Null for a campaign saved before 2026-10-31, which has no template attached. Saved with the
    /// draft's JSON; never part of the email, so an approval does not rest on it.
    /// </summary>
    public Guid? TemplateId { get; init; }

    /// <summary>
    /// Which version of <see cref="TemplateId"/>: the time that version was saved, which names its save.
    /// A newer save of the template than this, or this one undone, is a template changed since.
    /// </summary>
    public DateTimeOffset? TemplateVersion { get; init; }

    /// <summary>
    /// The client's own name for the campaign, such as "Beauty Bank -- first send", to tell apart
    /// campaigns that share a subject. Hers alone: it is not part of the email, so
    /// <see cref="Build"/> leaves it out, and nothing checks or exports it. Saved with the draft's
    /// JSON, never in a blob's name or metadata. Null or blank when she has not given one; kept as
    /// typed, and trimmed when saved.
    /// </summary>
    public string? Label { get; set; }

    public Slot<string> Subject { get; } = Slot<string>.Empty();
    public Slot<string> Preheader { get; } = Slot<string>.Empty();

    public IReadOnlyList<BlockDraft> Blocks { get; }

    public CampaignDraft(string? templateName, IReadOnlyList<BlockDraft> blocks)
    {
        var repeated = blocks.GroupBy(b => b.Label.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw new ArgumentException($"Two blocks are labelled \"{repeated.Key}\".", nameof(blocks));
        TemplateName = templateName;
        Blocks = blocks;
    }

    public BlockDraft this[string label] =>
        Blocks.FirstOrDefault(b => string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"This draft has no block labelled \"{label}\".");

    /// <summary>A heading, greeting or fine-print block's text.</summary>
    public Slot<string> Text(string label) => Value<string>(label);

    public Slot<IReadOnlyList<string>> Paragraphs(string label) => Value<IReadOnlyList<string>>(label);

    public Slot<CallToAction> Button(string label) => Value<CallToAction>(label);

    public Slot<SignOff> SignOff(string label) => Value<SignOff>(label);

    public Slot<HeaderContent> Header(string label) => Value<HeaderContent>(label);

    public Slot<ImageRef> Image(string label) => Value<ImageRef>(label);

    public OfferDraft Offer(string label) =>
        this[label] is OfferBlockDraft o ? o.Offer : throw new InvalidOperationException($"\"{label}\" is not an offer block.");

    public PromotionDraft Promotion(string label) =>
        this[label] is PromotionBlockDraft p ? p.Promotion : throw new InvalidOperationException($"\"{label}\" is not a promotion block.");

    public DraftResult Build()
    {
        var problems = new DraftProblems();
        problems.Require(Subject, "Subject");
        problems.Optional(Preheader, "Preheader");
        foreach (var block in Blocks)
            block.Check(problems);

        if (problems.Any)
            return new DraftResult(null, problems.Findings);

        return new DraftResult(new Campaign(
            Subject.Value,
            Blocks.Select(b => b.ToBlock()).OfType<Block>().ToList(),
            Preheader.HasValue ? Preheader.Value : null), []);
    }

    private Slot<T> Value<T>(string label) =>
        this[label] is ValueBlockDraft<T> v
            ? v.Value
            : throw new InvalidOperationException($"\"{label}\" is a {this[label].Type} block, not one holding {typeof(T).Name}.");
}

/// <param name="Campaign">The built campaign, or null while <paramref name="Problems"/> is not empty.</param>
public sealed record DraftResult(Campaign? Campaign, IReadOnlyList<Finding> Problems)
{
    public bool Succeeded => Campaign is not null;
}

/// <summary>One block of a draft, as its template defined it.</summary>
public abstract class BlockDraft(string label, BlockType type, bool required)
{
    public string Label { get; } = label;
    public BlockType Type { get; } = type;
    public bool Required { get; } = required;

    /// <summary>
    /// The block as what is still to fill in names it: its label, and for a button whose label does
    /// not say so, "The button (Call to action)", so Still to do says "button" as the checks do ("add
    /// a button with a link"), while the label still ties it to its card in the form.
    /// </summary>
    public string Name =>
        Type == BlockType.Button && !Label.Contains("button", StringComparison.OrdinalIgnoreCase) ? $"The button ({Label})" : Label;

    internal abstract void Check(DraftProblems problems);

    /// <summary>The finished block, or null for an optional block left empty.</summary>
    internal abstract Block? ToBlock();

    internal static BlockDraft From(TemplateBlock t) => t.Type switch
    {
        BlockType.Heading => new ValueBlockDraft<string>(t, (l, v) => new HeadingBlock(l, v), (t.Fixed as HeadingBlock)?.Text),
        BlockType.Greeting => new ValueBlockDraft<string>(t, (l, v) => new GreetingBlock(l, v), (t.Fixed as GreetingBlock)?.Text),
        BlockType.FinePrint => new ValueBlockDraft<string>(t, (l, v) => new FinePrintBlock(l, v), (t.Fixed as FinePrintBlock)?.Text),
        BlockType.Paragraphs => new ValueBlockDraft<IReadOnlyList<string>>(t, (l, v) => new ParagraphsBlock(l, v), (t.Fixed as ParagraphsBlock)?.Paragraphs),
        BlockType.Button => new ValueBlockDraft<CallToAction>(t, (l, v) => new ButtonBlock(l, v), (t.Fixed as ButtonBlock)?.Action),
        BlockType.SignOff => new ValueBlockDraft<SignOff>(t, (l, v) => new SignOffBlock(l, v), (t.Fixed as SignOffBlock)?.SignOff),
        BlockType.Header => new ValueBlockDraft<HeaderContent>(t, (l, v) => new HeaderBlock(l, v.Text, v.Photo),
            t.Fixed is HeaderBlock h ? new HeaderContent(h.Text, h.Photo) : null),
        BlockType.Image => new ValueBlockDraft<ImageRef>(t, (l, v) => new ImageBlock(l, v), (t.Fixed as ImageBlock)?.Image),
        BlockType.Spacer => new SpacerBlockDraft(t.Label),
        BlockType.Offer => new OfferBlockDraft(t.Label, t.Required, t.Marker ?? OfferBlock.DefaultMarker)
        {
            Offer = { IsRecurring = t.Recurring },
        },
        BlockType.Promotion => new PromotionBlockDraft(t.Label, t.Required),
        _ => throw new ArgumentOutOfRangeException(nameof(t), $"Unknown block type {t.Type}."),
    };
}

/// <summary>A block holding one value: text, paragraphs, a button or a sign-off.</summary>
public sealed class ValueBlockDraft<T> : BlockDraft
{
    private readonly Func<string, T, Block> _make;

    public Slot<T> Value { get; }

    internal ValueBlockDraft(string label, BlockType type, bool required, Func<string, T, Block> make, Slot<T> value)
        : base(label, type, required)
    {
        _make = make;
        Value = value;
    }

    internal ValueBlockDraft(TemplateBlock t, Func<string, T, Block> make, T? fixedValue)
        : this(t.Label, t.Type, t.Required, make, fixedValue is null ? Slot<T>.Empty() : Slot<T>.FromTemplate(fixedValue))
    {
    }

    internal override void Check(DraftProblems problems)
    {
        if (Required) problems.Require(Value, Label, Name);
        else problems.Optional(Value, Label);
    }

    internal override Block? ToBlock() => Value.HasValue ? _make(Label, Value.Value) : null;
}

/// <summary>A tiered offer being written.</summary>
public sealed class OfferBlockDraft(string label, bool required, string marker) : BlockDraft(label, BlockType.Offer, required)
{
    public OfferDraft Offer { get; } = new();

    /// <summary>What starts each benefit line; the template's choice.</summary>
    public string Marker { get; } = marker;

    internal override void Check(DraftProblems problems)
    {
        // An optional offer nobody has started is simply left out.
        if (!Required && Offer.IsUntouched) return;
        Offer.Check(problems, Label);
    }

    internal override Block? ToBlock() =>
        !Required && Offer.IsUntouched ? null : new OfferBlock(Label, Offer.ToOffer(), Marker);
}

/// <summary>A promotion being written. Never fixed by a template: it is what a campaign is about.</summary>
public sealed class PromotionBlockDraft(string label, bool required) : BlockDraft(label, BlockType.Promotion, required)
{
    public PromotionDraft Promotion { get; } = new();

    internal override void Check(DraftProblems problems)
    {
        // An optional promotion nobody has started is simply left out.
        if (!Required && Promotion.IsUntouched) return;
        Promotion.Check(problems, Label);
    }

    internal override Block? ToBlock() =>
        !Required && Promotion.IsUntouched ? null : new PromotionBlock(Label, Promotion.ToPromotion());
}

/// <summary>
/// A promotion's values as slots: its name, dates, optional summary and audience, and its lines in her
/// order, each a priced treatment or a benefit.
/// </summary>
public sealed class PromotionDraft
{
    private readonly List<PromotionLineDraft> _lines = [];

    public Slot<string> Name { get; } = Slot<string>.Empty();
    public Slot<string> Summary { get; } = Slot<string>.Empty();
    public Slot<string> Audience { get; } = Slot<string>.Empty();
    public Slot<DateOnly> Starts { get; } = Slot<DateOnly>.Empty();
    public Slot<DateOnly> Ends { get; } = Slot<DateOnly>.Empty();

    public IReadOnlyList<PromotionLineDraft> Lines => _lines;

    internal bool IsUntouched =>
        !Name.HasValue && !Summary.HasValue && !Audience.HasValue && !Starts.HasValue && !Ends.HasValue && _lines.Count == 0;

    /// <summary>An empty priced-treatment line at the end.</summary>
    public PricedTreatmentDraft AddTreatment()
    {
        var line = new PricedTreatmentDraft();
        _lines.Add(line);
        return line;
    }

    /// <summary>An empty benefit line at the end.</summary>
    public BenefitLineDraft AddBenefit()
    {
        var line = new BenefitLineDraft();
        _lines.Add(line);
        return line;
    }

    public void RemoveLine(int index) => _lines.RemoveAt(index);

    // Where a line is, as the checks and Still to do name it: "Promotion › Line 2".
    internal static string LineWhere(string label, int index) => $"{label} › Line {index + 1}";

    internal void Check(DraftProblems problems, string label)
    {
        problems.Require(Name, $"{label} › Name");
        problems.Optional(Summary, $"{label} › Summary");
        problems.Optional(Audience, $"{label} › Audience");
        problems.Require(Starts, $"{label} › Starts");
        problems.Require(Ends, $"{label} › Ends");
        if (_lines.Count == 0)
            problems.Add("draft-missing", $"{label} › Lines", "The promotion has no lines.");
        for (var i = 0; i < _lines.Count; i++)
            _lines[i].Check(problems, LineWhere(label, i));
    }

    internal Promotion ToPromotion() => new(
        Name: Name.Value,
        Starts: Starts.Value,
        Ends: Ends.Value,
        Lines: _lines.Select(l => l.ToLine()).ToList(),
        Summary: Summary.HasValue ? Summary.Value : null,
        Audience: Audience.HasValue ? Audience.Value : null);
}

/// <summary>One line of a promotion being written.</summary>
public abstract class PromotionLineDraft
{
    private protected PromotionLineDraft()
    {
    }

    /// <summary>Every value of the line is there.</summary>
    public abstract bool IsFinished { get; }

    internal abstract void Check(DraftProblems problems, string where);

    internal abstract PromotionLine ToLine();
}

/// <summary>A treatment, its promotion price and its regular price.</summary>
public sealed class PricedTreatmentDraft : PromotionLineDraft
{
    public Slot<string> Treatment { get; } = Slot<string>.Empty();
    public Slot<decimal> Price { get; } = Slot<decimal>.Empty();
    public Slot<decimal> RegularPrice { get; } = Slot<decimal>.Empty();

    public override bool IsFinished => Treatment.HasValue && Price.HasValue && RegularPrice.HasValue;

    internal override void Check(DraftProblems problems, string where)
    {
        problems.Require(Treatment, $"{where} › Treatment");
        problems.Require(Price, $"{where} › Promotion price");
        problems.Require(RegularPrice, $"{where} › Regular price");
    }

    internal override PromotionLine ToLine() => new PricedTreatment(Treatment.Value, Price.Value, RegularPrice.Value);
}

/// <summary>A typed benefit as a promotion line.</summary>
public sealed class BenefitLineDraft : PromotionLineDraft
{
    public Slot<Benefit> Benefit { get; } = Slot<Benefit>.Empty();

    public override bool IsFinished => Benefit.HasValue;

    internal override void Check(DraftProblems problems, string where) => problems.Require(Benefit, $"{where} › Benefit");

    internal override PromotionLine ToLine() => new BenefitLine(Benefit.Value);
}

/// <summary>Space between blocks: nothing to fill, so always ready.</summary>
public sealed class SpacerBlockDraft(string label) : BlockDraft(label, BlockType.Spacer, required: false)
{
    internal override void Check(DraftProblems problems)
    {
    }

    internal override Block? ToBlock() => new SpacerBlock(Label);
}

public sealed class OfferDraft
{
    private readonly List<TierDraft> _tiers = [];

    public Slot<string> Name { get; } = Slot<string>.Empty();
    public Slot<string> Summary { get; } = Slot<string>.Empty();
    public Slot<string> TiersNote { get; } = Slot<string>.Empty();
    public Slot<Uri> TermsUrl { get; } = Slot<Uri>.Empty();
    public bool IsRecurring { get; set; }

    public IReadOnlyList<TierDraft> Tiers => _tiers;

    internal bool IsUntouched =>
        !Name.HasValue && !Summary.HasValue && !TiersNote.HasValue && !TermsUrl.HasValue && _tiers.Count == 0;

    public TierDraft AddTier()
    {
        var tier = new TierDraft();
        _tiers.Add(tier);
        return tier;
    }

    /// <summary>
    /// Starts a new tier from an existing one without silently duplicating it. The benefit
    /// list is copied for convenience, but each benefit is marked as copied and must be edited
    /// or confirmed. The name and price are copied as they stand: while the two tiers share a
    /// name, or the later one costs no more than the one before it, the rules stop the campaign,
    /// so the client changes whichever tier she means to rather than being made to invent a name
    /// or a price for the new one.
    /// </summary>
    public TierDraft CopyTier(int index)
    {
        var source = _tiers[index];
        var label = $"Tier {index + 1}";
        var tier = new TierDraft();
        if (source.Name.HasValue)
            tier.Name.Set(source.Name.Value);
        if (source.MonthlyPrice.HasValue)
            tier.MonthlyPrice.Set(source.MonthlyPrice.Value);
        foreach (var b in source.Benefits)
            tier.AddCopiedBenefit(b.Value, label);
        _tiers.Add(tier);
        return tier;
    }

    /// <summary>
    /// Removes a tier. A benefit copied from it, still waiting to be checked, then says it came from
    /// a tier since removed, and one copied from a later tier names that tier's new number.
    /// </summary>
    public void RemoveTier(int index)
    {
        var before = _tiers.ToList();
        _tiers.RemoveAt(index);
        RenumberCopies(before);
    }

    /// <summary>
    /// One click (owner, 2026-10-09: "a fast ordering of tiers top to bottom or bottom to top"): the
    /// tiers by price, lowest first or highest first. Equal prices keep their order, and a tier with no
    /// price yet goes last, in its order. Either way is a consistent direction for
    /// tier-prices-increase. A copied benefit still waiting to be checked keeps naming the tier it was
    /// copied from, by that tier's new number.
    /// </summary>
    public void OrderByPrice(bool highestFirst)
    {
        var before = _tiers.ToList();
        var ordered = before
            .OrderBy(t => t.MonthlyPrice.HasValue ? 0 : 1)
            .ThenBy(t => !t.MonthlyPrice.HasValue ? 0m : highestFirst ? -t.MonthlyPrice.Value : t.MonthlyPrice.Value)
            .ToList();
        _tiers.Clear();
        _tiers.AddRange(ordered);
        RenumberCopies(before);
    }

    // A copied benefit names its source as "Tier N" (CopyTier). After tiers move or go, N is that
    // tier's place now, or it is said to be removed.
    private void RenumberCopies(IReadOnlyList<TierDraft> before)
    {
        foreach (var slot in _tiers.SelectMany(t => t.Benefits).Where(s => s.Origin == Origin.Copied))
        {
            if (slot.CopiedFrom is not { } from || !from.StartsWith("Tier ", StringComparison.Ordinal)
                || !int.TryParse(from.AsSpan(5), out var number) || number < 1 || number > before.Count) continue;
            var now = _tiers.IndexOf(before[number - 1]);
            slot.Relabel(now < 0 ? "a tier since removed" : $"Tier {now + 1}");
        }
    }

    internal void Check(DraftProblems problems, string label)
    {
        problems.Require(Name, $"{label} › Name");
        problems.Require(Summary, $"{label} › Summary");
        problems.Optional(TiersNote, $"{label} › Tiers note");
        if (IsRecurring) problems.Require(TermsUrl, $"{label} › Terms link");
        else problems.Optional(TermsUrl, $"{label} › Terms link");

        if (_tiers.Count == 0)
            problems.Add("draft-missing", $"{label} › Tiers", "The offer has no tiers.");

        for (var i = 0; i < _tiers.Count; i++)
            _tiers[i].Check(problems, $"{label} › Tier {i + 1}");
    }

    internal Offer ToOffer() => new(
        Name: Name.Value,
        Summary: Summary.Value,
        Tiers: _tiers.Select(t => t.ToTier()).ToList(),
        IsRecurring: IsRecurring,
        TermsUrl: TermsUrl.HasValue ? TermsUrl.Value : null,
        TiersNote: TiersNote.HasValue ? TiersNote.Value : null);
}

public sealed class TierDraft
{
    private readonly List<Slot<Benefit>> _benefits = [];

    public Slot<string> Name { get; } = Slot<string>.Empty();
    public Slot<decimal> MonthlyPrice { get; } = Slot<decimal>.Empty();

    public IReadOnlyList<Slot<Benefit>> Benefits => _benefits;

    public void AddBenefit(Benefit benefit)
    {
        var slot = Slot<Benefit>.Empty();
        slot.Set(benefit);
        _benefits.Add(slot);
    }

    internal void AddCopiedBenefit(Benefit benefit, string source) =>
        _benefits.Add(Slot<Benefit>.CopiedFromSource(benefit, source));

    internal Slot<Benefit> AddEmptyBenefit()
    {
        var slot = Slot<Benefit>.Empty();
        _benefits.Add(slot);
        return slot;
    }

    public void RemoveBenefit(int index) => _benefits.RemoveAt(index);

    internal void Check(DraftProblems problems, string where)
    {
        problems.Require(Name, $"{where} › Name");
        problems.Require(MonthlyPrice, $"{where} › Price");
        if (_benefits.Count == 0)
            problems.Add("draft-missing", where, "The tier has no benefits.");
        for (var i = 0; i < _benefits.Count; i++)
            problems.Require(_benefits[i], $"{where} › Benefit {i + 1}");
    }

    internal Tier ToTier() => new(Name.Value, MonthlyPrice.Value, _benefits.Select(b => b.Value).ToList());
}

/// <summary>Collects build problems as blocker findings, so drafts report like the gate does.</summary>
internal sealed class DraftProblems
{
    private readonly List<Finding> _findings = [];

    public IReadOnlyList<Finding> Findings => _findings;

    public bool Any => _findings.Count > 0;

    public void Add(string rule, string where, string message) =>
        _findings.Add(new Finding(Severity.Blocker, rule, where, message));

    /// <param name="what">What the message calls it; <paramref name="where"/> when not given.</param>
    public void Require<T>(Slot<T> slot, string where, string? what = null)
    {
        if (!slot.HasValue) Add("draft-missing", where, $"{what ?? where} has not been filled in.");
        else Optional(slot, where);
    }

    public void Optional<T>(Slot<T> slot, string where)
    {
        if (slot.Origin == Origin.Copied)
            Add("draft-unreviewed-copy", where,
                $"{where} was copied from {slot.CopiedFrom} and not reviewed: " +
                $"\"{Describe(slot.Value)}\". Edit it, or confirm it is right for this tier.");
    }

    private static string Describe(object? value) => value is Benefit b ? b.Describe() : value?.ToString() ?? "";
}
