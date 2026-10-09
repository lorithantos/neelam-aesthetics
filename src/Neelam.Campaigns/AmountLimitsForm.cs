namespace Neelam.Campaigns;

/// <summary>
/// A known benefit line's limits as the Known items page takes them (owner, 2026-10-09: known lines
/// are "replacements with limits if needed"): for each amount the line's kind has, an optional lowest
/// and highest beside the usual amount the benefit's own fields hold. Both left blank: any amount of
/// it is fine. Whether they make sense together (lowest at most usual, usual at most highest) is
/// <see cref="KnownBenefit.LimitProblems"/>'s, which the store holds every line to.
/// </summary>
public sealed class AmountLimitsForm
{
    private readonly Dictionary<string, AmountLimitFields> _fields = [];

    /// <summary>A form holding a known line's limits, to change them.</summary>
    public static AmountLimitsForm Of(KnownBenefit line)
    {
        var form = new AmountLimitsForm();
        foreach (var amount in line.Benefit.Amounts)
        {
            if (line.LimitOn(amount.Field.Name) is not { } limit) continue;
            var fields = form.For(amount.Field);
            fields.Lowest = limit.Min is { } lo ? Number(lo) : "";
            fields.Highest = limit.Max is { } hi ? Number(hi) : "";
        }
        return form;
    }

    /// <summary>The lowest and highest boxes for one amount, kept while she changes the kind and back.</summary>
    public AmountLimitFields For(AmountField field)
    {
        if (!_fields.TryGetValue(field.Name, out var fields))
            _fields[field.Name] = fields = new AmountLimitFields(field);
        return fields;
    }

    /// <summary>The boxes to show beside a benefit form: one pair per amount of its chosen kind.</summary>
    public IReadOnlyList<AmountLimitFields> FieldsFor(BenefitEditor benefit) =>
        BenefitKind.Of(benefit.Kind)?.Amounts.Select(For).ToList() ?? [];

    /// <returns>
    /// The known line under <paramref name="id"/>: <paramref name="benefit"/> at its usual amounts,
    /// with the limits typed for its kind's amounts. Null, with what is wrong, while the benefit is
    /// unfinished (<paramref name="unfinished"/> when it says nothing itself) or a limit is not a number.
    /// </returns>
    public (KnownBenefit? Line, IReadOnlyList<string> Errors) ToItem(string id, BenefitEditor benefit, string unfinished)
    {
        if (benefit.Value is not { } value) return (null, [benefit.Error ?? unfinished]);
        var errors = new List<string>();
        var limits = new List<AmountLimit>();
        foreach (var amount in value.Amounts)
        {
            var fields = For(amount.Field);
            var lowest = Parse(fields.Lowest, amount.Field, errors);
            var highest = Parse(fields.Highest, amount.Field, errors);
            if (lowest is not null || highest is not null) limits.Add(new AmountLimit(amount.Field.Name, lowest, highest));
        }
        return errors.Count == 0 ? (new KnownBenefit(id, value, limits), []) : (null, errors);
    }

    // Blank is no limit; anything else must be an amount of the field's unit.
    private static decimal? Parse(string text, AmountField field, List<string> errors)
    {
        if (FormText.Clean(text ?? "").Length == 0) return null;
        return field.Unit == AmountUnit.Dollars
            ? FormText.Money(text!, $"the {field.Label}", errors)
            : FormText.Whole(text!, $"the {field.Label}", errors);
    }

    private static string Number(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The lowest and highest boxes for one amount of a known benefit line; blank is no limit.</summary>
public sealed class AmountLimitFields(AmountField amount)
{
    public AmountField Field { get; } = amount;

    public string Lowest { get; set; } = "";

    public string Highest { get; set; } = "";
}
