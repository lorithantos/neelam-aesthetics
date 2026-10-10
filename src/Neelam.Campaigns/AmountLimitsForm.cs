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

    /// <summary>
    /// The boxes to show beside a benefit form: one pair per amount of the benefit as typed, once it
    /// is complete, else of its chosen kind. An optional amount (dollars off's minimum) has boxes
    /// only while the line has one, as only then can it be limited.
    /// </summary>
    public IReadOnlyList<AmountLimitFields> FieldsFor(BenefitEditor benefit) =>
        benefit.Value is { } value
            ? value.Amounts.Select(a => For(a.Field)).ToList()
            : BenefitKind.Of(benefit.Kind)?.Amounts.Select(For).ToList() ?? [];

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

/// <summary>
/// A known benefit line's caps, changed in place beside the line on the Known items page (owner,
/// 2026-10-09: "make sure the editing of the caps is easy to find and update"): for each amount the
/// line has, its usual value and its lowest and highest, as boxes. The usual value is needed (the
/// line's sentence carries it); a blank lowest or highest is no limit at that end. The line's kind
/// and words are not here: changing those is the line's Change form. What she typed stays in the
/// boxes, with <see cref="Errors"/> beside them, until it saves.
/// </summary>
public sealed class AmountCapsForm
{
    private AmountCapsForm(IReadOnlyList<AmountCapFields> fields) => Fields = fields;

    /// <summary>The boxes for <paramref name="line"/>, holding its usual amounts and limits as kept.</summary>
    public static AmountCapsForm Of(KnownBenefit line) =>
        new(line.Benefit.Amounts.Select(a => new AmountCapFields(a.Field)
        {
            Usual = Number(a.Value),
            Lowest = line.LimitOn(a.Field.Name)?.Min is { } lo ? Number(lo) : "",
            Highest = line.LimitOn(a.Field.Name)?.Max is { } hi ? Number(hi) : "",
        }).ToList());

    /// <summary>One set of boxes per amount of the line, in the order the line has them.</summary>
    public IReadOnlyList<AmountCapFields> Fields { get; }

    /// <summary>Why the last save of these boxes was refused; empty when it was not.</summary>
    public IReadOnlyList<string> Errors { get; set; } = [];

    /// <summary>Said beside the boxes once they are saved; null otherwise.</summary>
    public string? Saved { get; set; }

    /// <returns>
    /// <paramref name="line"/> with the usual amounts and limits typed, under its own id, its kind and
    /// words unchanged. Null, with what is wrong, when a box is not an amount, the usual one is blank,
    /// or the limits do not hold together (<see cref="KnownBenefit.LimitProblems"/>, as the store
    /// holds every line to).
    /// </returns>
    public (KnownBenefit? Line, IReadOnlyList<string> Errors) ToItem(KnownBenefit line)
    {
        var errors = new List<string>();
        var benefit = line.Benefit;
        var limits = new List<AmountLimit>();
        foreach (var fields in Fields)
        {
            var field = fields.Field;
            var usual = Parse(fields.Usual, field, $"the usual {field.Label}", errors);
            var lowest = Blank(fields.Lowest) ? null : Parse(fields.Lowest, field, $"the lowest {field.Label}", errors);
            var highest = Blank(fields.Highest) ? null : Parse(fields.Highest, field, $"the highest {field.Label}", errors);
            if (usual is { } value) benefit = benefit.WithAmount(field.Name, value);
            if (lowest is not null || highest is not null) limits.Add(new AmountLimit(field.Name, lowest, highest));
        }
        if (errors.Count > 0) return (null, errors);
        var changed = new KnownBenefit(line.Id, benefit, limits);
        var problems = changed.LimitProblems();
        return problems.Count == 0 ? (changed, []) : (null, problems);
    }

    private static bool Blank(string? text) => FormText.Clean(text ?? "").Length == 0;

    // Blank is "Fill in ..." (only the usual amount is ever parsed blank); anything else must be an
    // amount of the field's unit, as the line's own form takes it.
    private static decimal? Parse(string? text, AmountField field, string what, List<string> errors) =>
        field.Unit == AmountUnit.Dollars
            ? FormText.Money(text ?? "", what, errors)
            : FormText.Whole(text ?? "", what, errors);

    private static string Number(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The usual, lowest and highest boxes for one amount of a known benefit line.</summary>
public sealed class AmountCapFields(AmountField amount)
{
    public AmountField Field { get; } = amount;

    /// <summary>The usual amount: the line's own, which picking it fills in.</summary>
    public string Usual { get; set; } = "";

    /// <summary>The lowest amount; blank is no limit.</summary>
    public string Lowest { get; set; } = "";

    /// <summary>The highest amount; blank is no limit.</summary>
    public string Highest { get; set; } = "";
}

/// <summary>The lowest and highest boxes for one amount of a known benefit line; blank is no limit.</summary>
public sealed class AmountLimitFields(AmountField amount)
{
    public AmountField Field { get; } = amount;

    public string Lowest { get; set; } = "";

    public string Highest { get; set; } = "";
}
