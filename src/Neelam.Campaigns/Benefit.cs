using System.Globalization;
using System.Text.Json.Serialization;

namespace Neelam.Campaigns;

/// <summary>
/// A tier benefit. Benefits are typed rather than free text so that wording the business can be
/// held to is generated, never typed: "50% Complimentary" cannot be expressed, because a thing is
/// either <see cref="FreeItem"/> or <see cref="DiscountedItem"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BirthdayCredit), "birthday-credit")]
[JsonDerivedType(typeof(PercentOff), "percent-off")]
[JsonDerivedType(typeof(FreeItem), "free-item")]
[JsonDerivedType(typeof(DiscountedItem), "discounted-item")]
[JsonDerivedType(typeof(DollarsOff), "dollars-off")]
[JsonDerivedType(typeof(Credit), "credit")]
public abstract record Benefit
{
    /// <summary>Benefits of the same kind are compared across tiers.</summary>
    [JsonIgnore]
    public abstract string Kind { get; }

    /// <summary>The customer-facing sentence for this benefit.</summary>
    public abstract string Describe();

    /// <summary>The service this benefit is about, if any; used to spot medical services.</summary>
    [JsonIgnore]
    public virtual string? Item => null;

    /// <summary>
    /// Its amounts: the numbers a campaign changes from one send to the next (dollars, a percentage,
    /// how many), as against the words that say what the benefit is. A known benefit line is a
    /// pattern over these: the words fixed, each amount usual and optionally limited.
    /// </summary>
    [JsonIgnore]
    public abstract IReadOnlyList<BenefitAmount> Amounts { get; }

    /// <summary>The same benefit with one amount changed; the words stay as they are.</summary>
    public abstract Benefit WithAmount(string field, decimal value);

    /// <summary>The same benefit with <paramref name="amounts"/> in place of its own, field by field.</summary>
    public Benefit WithAmounts(IEnumerable<BenefitAmount> amounts) =>
        amounts.Aggregate(this, (b, a) => b.Amounts.Any(own => own.Field.Name == a.Field.Name) ? b.WithAmount(a.Field.Name, a.Value) : b);

    /// <summary>
    /// The sentence with its amounts left out (<see cref="AmountMark"/> in their place), so two
    /// benefits that differ only by an amount read the same: what a known benefit line is matched by.
    /// </summary>
    public abstract string Pattern();

    /// <summary>Where an amount stood in a <see cref="Pattern"/>.</summary>
    public const string AmountMark = "#";

    /// <summary>
    /// The benefit as the checks compare it: wording she may choose between that says the same thing
    /// (something free, "free" or "complimentary") is set one way, so two benefits that differ only by
    /// it are the same benefit, in a tier comparison or against her known items. What the email says
    /// is always the benefit's own <see cref="Describe"/>.
    /// </summary>
    public virtual Benefit Canonical() => this;

    /// <summary>
    /// The same benefit in <paramref name="written"/>'s choice of wording, where both have one: a "Did
    /// you mean" suggests her line in the words she chose, not in the line's.
    /// </summary>
    public virtual Benefit WordedLike(Benefit written) => this;

    protected static string Money(decimal amount) =>
        amount % 1 == 0
            ? amount.ToString("$#,0", CultureInfo.InvariantCulture)
            : amount.ToString("$#,0.00", CultureInfo.InvariantCulture);

    internal static string Shown(AmountUnit unit, decimal value) => unit switch
    {
        AmountUnit.Dollars => Money(value),
        AmountUnit.Percent => value.ToString("0.##", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("0.##", CultureInfo.InvariantCulture),
    };

    // A whole-number field takes only a whole number: a limit or a usual amount never rounds.
    protected static int Whole(string field, decimal value) =>
        value % 1 == 0 && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new ArgumentException($"{field} is a whole number.", nameof(value));

    protected static ArgumentException NoSuchAmount(string field) =>
        new($"This kind of benefit has no amount called {field}.", nameof(field));
}

/// <summary>What an amount counts: dollars, a percentage, or how many.</summary>
public enum AmountUnit
{
    Dollars,
    Percent,
    Count,
}

/// <summary>
/// One amount a kind of benefit has: its field (the property's name, as the saved JSON names it),
/// what the form calls it, and what it counts.
/// </summary>
public sealed record AmountField(string Name, string Label, AmountUnit Unit)
{
    /// <summary>A value of this field as the email writes it: $50, 10%, 2.</summary>
    public string Show(decimal value) => Benefit.Shown(Unit, value);
}

/// <summary>An amount of one benefit: which field, and its value.</summary>
public sealed record BenefitAmount(AmountField Field, decimal Value)
{
    public override string ToString() => Field.Show(Value);
}

/// <summary>A credit applied during the customer's birth month.</summary>
public sealed record BirthdayCredit(decimal Amount) : Benefit
{
    public static AmountField AmountField { get; } = new(nameof(Amount), "credit", AmountUnit.Dollars);

    public override string Kind => "birthday-credit";
    public override string Describe() => $"{Money(Amount)} birthday credit during your birth month";
    public override string Pattern() => $"{AmountMark} birthday credit during your birth month";
    public override IReadOnlyList<BenefitAmount> Amounts => [new(AmountField, Amount)];

    public override Benefit WithAmount(string field, decimal value) =>
        field == AmountField.Name ? this with { Amount = value } : throw NoSuchAmount(field);
}

/// <summary>A percentage off a category of services.</summary>
public sealed record PercentOff(int Percent, string AppliesTo) : Benefit
{
    public static AmountField PercentField { get; } = new(nameof(Percent), "percentage", AmountUnit.Percent);

    public override string Kind => "percent-off";
    public override string Describe() => $"{Percent}% off {AppliesTo}";
    public override string Pattern() => $"{AmountMark}% off {AppliesTo}";
    public override IReadOnlyList<BenefitAmount> Amounts => [new(PercentField, Percent)];

    public override Benefit WithAmount(string field, decimal value) =>
        field == PercentField.Name ? this with { Percent = Whole(field, value) } : throw NoSuchAmount(field);
}

/// <summary>
/// How something free says so (owner, 2026-10-09: "Complimentary is the same as free, so allow
/// either"). Complimentary is the default, so a save from before the choice reads as it always did.
/// </summary>
public enum FreeWording
{
    Complimentary,
    Free,
}

/// <summary>
/// Something given at no charge, worded "complimentary" (the Beauty Bank email: "1 Complimentary
/// Wellness Injection per visit") or "free" (the back-to-school email: "Free Wellness Injection with
/// any treatment"). The two are the same benefit to every check (<see cref="Canonical"/>).
/// </summary>
/// <param name="Wording">Left out of the saved JSON when complimentary, so a save from before the choice is unchanged.</param>
public sealed record FreeItem(
    int Quantity, string ItemName, string Per,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] FreeWording Wording = FreeWording.Complimentary)
    : Benefit
{
    public static AmountField QuantityField { get; } = new(nameof(Quantity), "number", AmountUnit.Count);

    public override string Kind => $"free:{ItemName.ToLowerInvariant()}";
    public override string? Item => ItemName;

    /// <summary>The word it is worded with: "complimentary" or "free".</summary>
    [JsonIgnore]
    public string Word => Wording == FreeWording.Free ? "free" : "complimentary";

    // One free thing has no count, as her back-to-school email wrote it ("Free Wellness Injection with
    // any treatment"), and it starts the line, so "Free" is capitalised. One complimentary thing keeps
    // its count, as the Beauty Bank email did ("1 Complimentary Wellness Injection per visit").
    public override string Describe() =>
        Wording == FreeWording.Free && Quantity == 1
            ? $"Free {ItemName} {Per}"
            : $"{Quantity} {Word} {Pluralise(ItemName, Quantity)} {Per}";

    // Not pluralised: how many is the amount, so "1 ... injection" and "2 ... injections" are one line.
    // Always "complimentary": the wording is a choice of words for the same line, never a different one.
    public override string Pattern() => $"{AmountMark} complimentary {ItemName} {Per}";
    public override IReadOnlyList<BenefitAmount> Amounts => [new(QuantityField, Quantity)];

    public override Benefit WithAmount(string field, decimal value) =>
        field == QuantityField.Name ? this with { Quantity = Whole(field, value) } : throw NoSuchAmount(field);

    public override Benefit Canonical() => this with { Wording = FreeWording.Complimentary };

    public override Benefit WordedLike(Benefit written) =>
        written is FreeItem f ? this with { Wording = f.Wording } : this;

    private static string Pluralise(string noun, int n) => n == 1 ? noun : noun + "s";
}

/// <summary>A percentage off a specific item, e.g. 50% off one wellness injection per visit.</summary>
/// <param name="Condition">Optional qualifier, e.g. "any additional".</param>
public sealed record DiscountedItem(int Percent, string ItemName, string Per, string? Condition = null) : Benefit
{
    public static AmountField PercentField { get; } = new(nameof(Percent), "percentage", AmountUnit.Percent);

    public override string Kind => $"discount:{ItemName.ToLowerInvariant()}";
    public override string? Item => ItemName;
    public override string Describe() =>
        Condition is null
            ? $"{Percent}% off one {ItemName} {Per}"
            : $"{Percent}% off {Condition} {ItemName}s {Per}";

    public override string Pattern() =>
        Condition is null
            ? $"{AmountMark}% off one {ItemName} {Per}"
            : $"{AmountMark}% off {Condition} {ItemName}s {Per}";

    public override IReadOnlyList<BenefitAmount> Amounts => [new(PercentField, Percent)];

    public override Benefit WithAmount(string field, decimal value) =>
        field == PercentField.Name ? this with { Percent = Whole(field, value) } : throw NoSuchAmount(field);
}

/// <summary>
/// Dollars off something, as her back-to-school email offered it: "$50 off 30+ units" (owner,
/// 2026-10-09: "Otherwise, yes, add the benefit types").
/// </summary>
/// <param name="Amount">The dollars off: "$50".</param>
/// <param name="AppliesTo">What it is off: "units", "any facial".</param>
/// <param name="Minimum">Optional: how many at least, read "30+" before what it is off.</param>
public sealed record DollarsOff(decimal Amount, string AppliesTo, int? Minimum = null) : Benefit
{
    public static AmountField AmountField { get; } = new(nameof(Amount), "dollars off", AmountUnit.Dollars);
    public static AmountField MinimumField { get; } = new(nameof(Minimum), "minimum", AmountUnit.Count);

    public override string Kind => "dollars-off";
    public override string Describe() => $"{Money(Amount)} off {AtLeast(Minimum?.ToString(CultureInfo.InvariantCulture))}{AppliesTo}";
    public override string Pattern() => $"{AmountMark} off {AtLeast(Minimum is null ? null : AmountMark)}{AppliesTo}";

    // The minimum is an amount too when there is one: "$50 off 30+ units" and "$100 off 60+ units"
    // are one line. Without one, the line has none to limit.
    public override IReadOnlyList<BenefitAmount> Amounts =>
        Minimum is { } least ? [new(AmountField, Amount), new(MinimumField, least)] : [new(AmountField, Amount)];

    public override Benefit WithAmount(string field, decimal value) =>
        field == AmountField.Name ? this with { Amount = value }
        : field == MinimumField.Name && Minimum is not null ? this with { Minimum = Whole(field, value) }
        : throw NoSuchAmount(field);

    private static string AtLeast(string? least) => least is null ? "" : $"{least}+ ";
}

/// <summary>
/// A credit toward something, as her back-to-school email offered it: "$50 credit toward your next
/// appointment" (owner, 2026-10-09).
/// </summary>
/// <param name="Amount">The credit: "$50".</param>
/// <param name="Toward">What it is toward: "your next appointment".</param>
public sealed record Credit(decimal Amount, string Toward) : Benefit
{
    public static AmountField AmountField { get; } = new(nameof(Amount), "credit", AmountUnit.Dollars);

    public override string Kind => "credit";
    public override string Describe() => $"{Money(Amount)} credit toward {Toward}";
    public override string Pattern() => $"{AmountMark} credit toward {Toward}";
    public override IReadOnlyList<BenefitAmount> Amounts => [new(AmountField, Amount)];

    public override Benefit WithAmount(string field, decimal value) =>
        field == AmountField.Name ? this with { Amount = value } : throw NoSuchAmount(field);
}
