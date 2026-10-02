using System.Globalization;

namespace Neelam.Campaigns;

/// <summary>
/// A tier benefit. Benefits are typed rather than free text so that wording the business can be
/// held to is generated, never typed: "50% Complimentary" cannot be expressed, because a thing is
/// either <see cref="FreeItem"/> or <see cref="DiscountedItem"/>.
/// </summary>
public abstract record Benefit
{
    /// <summary>Benefits of the same kind are compared across tiers.</summary>
    public abstract string Kind { get; }

    /// <summary>The customer-facing sentence for this benefit.</summary>
    public abstract string Describe();

    /// <summary>The service this benefit is about, if any; used to spot medical services.</summary>
    public virtual string? Item => null;

    protected static string Money(decimal amount) =>
        amount % 1 == 0
            ? amount.ToString("$#,0", CultureInfo.InvariantCulture)
            : amount.ToString("$#,0.00", CultureInfo.InvariantCulture);
}

/// <summary>A credit applied during the customer's birth month.</summary>
public sealed record BirthdayCredit(decimal Amount) : Benefit
{
    public override string Kind => "birthday-credit";
    public override string Describe() => $"{Money(Amount)} birthday credit during your birth month";
}

/// <summary>A percentage off a category of services.</summary>
public sealed record PercentOff(int Percent, string AppliesTo) : Benefit
{
    public override string Kind => "percent-off";
    public override string Describe() => $"{Percent}% off {AppliesTo}";
}

/// <summary>Something given at no charge, e.g. one wellness injection per visit.</summary>
public sealed record FreeItem(int Quantity, string ItemName, string Per) : Benefit
{
    public override string Kind => $"free:{ItemName.ToLowerInvariant()}";
    public override string? Item => ItemName;
    public override string Describe() =>
        $"{Quantity} complimentary {Pluralise(ItemName, Quantity)} {Per}";

    private static string Pluralise(string noun, int n) => n == 1 ? noun : noun + "s";
}

/// <summary>A percentage off a specific item, e.g. 50% off one wellness injection per visit.</summary>
/// <param name="Condition">Optional qualifier, e.g. "any additional".</param>
public sealed record DiscountedItem(int Percent, string ItemName, string Per, string? Condition = null) : Benefit
{
    public override string Kind => $"discount:{ItemName.ToLowerInvariant()}";
    public override string? Item => ItemName;
    public override string Describe() =>
        Condition is null
            ? $"{Percent}% off one {ItemName} {Per}"
            : $"{Percent}% off {Condition} {ItemName}s {Per}";
}
