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

    public OfferDraft Offer(string label) =>
        this[label] is OfferBlockDraft o ? o.Offer : throw new InvalidOperationException($"\"{label}\" is not an offer block.");

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
        BlockType.Offer => new OfferBlockDraft(t.Label, t.Required) { Offer = { IsRecurring = t.Recurring } },
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
        if (Required) problems.Require(Value, Label);
        else problems.Optional(Value, Label);
    }

    internal override Block? ToBlock() => Value.HasValue ? _make(Label, Value.Value) : null;
}

/// <summary>A tiered offer being written.</summary>
public sealed class OfferBlockDraft(string label, bool required) : BlockDraft(label, BlockType.Offer, required)
{
    public OfferDraft Offer { get; } = new();

    internal override void Check(DraftProblems problems)
    {
        // An optional offer nobody has started is simply left out.
        if (!Required && Offer.IsUntouched) return;
        Offer.Check(problems, Label);
    }

    internal override Block? ToBlock() => !Required && Offer.IsUntouched ? null : new OfferBlock(Label, Offer.ToOffer());
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
    /// or confirmed. The name and price — what makes a tier a different tier — are not copied
    /// at all and must be entered.
    /// </summary>
    public TierDraft CopyTier(int index)
    {
        var source = _tiers[index];
        var label = $"Tier {index + 1}";
        var tier = new TierDraft();
        foreach (var b in source.Benefits)
            tier.AddCopiedBenefit(b.Value, label);
        _tiers.Add(tier);
        return tier;
    }

    public void RemoveTier(int index) => _tiers.RemoveAt(index);

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

    public void Require<T>(Slot<T> slot, string where)
    {
        if (!slot.HasValue) Add("draft-missing", where, $"{where} has not been filled in.");
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
