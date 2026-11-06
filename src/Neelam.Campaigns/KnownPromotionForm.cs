namespace Neelam.Campaigns;

/// <summary>
/// A saved promotion as the Known items page builds it: a name, optional summary, audience and fine
/// print, and lines that are one of her treatments at a promotion price or a typed benefit. Her
/// treatments are the one source: a regular price typed here changes her treatment. Turned into a
/// <see cref="KnownPromotionPlan"/> only when every line makes sense, with the reasons when not.
/// </summary>
public sealed class KnownPromotionForm
{
    /// <summary>What sits under the fine print: dates are the campaign's, said once by its Promotion block.</summary>
    public const string FinePrintHelp = "No dates here: each campaign's Promotion block says them.";   // (proposed)

    private readonly List<KnownPromotionLineForm> _lines = [];

    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Audience { get; set; } = "";
    public string FinePrint { get; set; } = "";

    public IReadOnlyList<KnownPromotionLineForm> Lines => _lines;

    /// <summary>A form holding a saved promotion, to change it, its lines as her treatments are today.</summary>
    public static KnownPromotionForm Of(KnownPromotion promotion, KnownItems known)
    {
        var form = new KnownPromotionForm
        {
            Name = promotion.Name, Summary = promotion.Summary ?? "", Audience = promotion.Audience ?? "",
            FinePrint = promotion.FinePrint ?? "",
        };
        foreach (var line in promotion.Lines)
        {
            if (line is KnownBenefitLine benefit) form.AddKnownLine(benefit.Benefit);
            else if (line is KnownTreatmentLine treatment)
            {
                var found = known.TreatmentById(treatment.TreatmentId);
                var priced = new KnownTreatmentLineForm
                {
                    Treatment = found?.Name ?? KnownPromotion.NoLongerOnYourList,
                    MissingId = found is null ? treatment.TreatmentId : null,
                    Price = PlainNumber.Text(treatment.Price),
                };
                priced.Fill(found);
                form._lines.Add(priced);
            }
        }
        return form;
    }

    /// <summary>An empty treatment line at the end, to name and price.</summary>
    public KnownTreatmentLineForm AddTreatment()
    {
        var line = new KnownTreatmentLineForm();
        _lines.Add(line);
        return line;
    }

    /// <summary>An empty benefit line at the end, to choose a kind for and fill in.</summary>
    public KnownBenefitLineForm AddBenefit() => AddKnownLine(null);

    /// <summary>A benefit item's line at the end, as ordinary fields she can change.</summary>
    public KnownBenefitLineForm AddKnown(KnownBenefit benefit) => AddKnownLine(benefit.Benefit);

    public void RemoveLine(KnownPromotionLineForm line) => _lines.Remove(line);

    private KnownBenefitLineForm AddKnownLine(Benefit? benefit)
    {
        var line = new KnownBenefitLineForm(BenefitEditor.Standalone(benefit));
        _lines.Add(line);
        return line;
    }

    /// <returns>
    /// The promotion under <paramref name="id"/> with the treatments to add to her list and the ones
    /// to reprice, or no plan with what is wrong. Whether the promotion itself may be kept is
    /// <see cref="KnownItemRules"/>'s to say, once the plan is laid over her list.
    /// </returns>
    public (KnownPromotionPlan? Plan, IReadOnlyList<string> Errors) ToPlan(string id, KnownItems known)
    {
        var errors = new List<string>();
        var name = FormText.Need(Name, "the promotion's name", errors);
        var lines = new List<KnownPromotionLine>();
        var added = new List<KnownTreatment>();
        var repriced = new List<KnownTreatment>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var n = i + 1;
            switch (_lines[i])
            {
                case KnownBenefitLineForm { Benefit: { Error: null, Value: { } benefit } }:
                    lines.Add(new KnownBenefitLine(benefit));
                    break;
                case KnownBenefitLineForm:
                    errors.Add($"Line {n}: fill it in, or remove it.");   // (proposed)
                    break;
                case KnownTreatmentLineForm line:
                    var found = line.ReadInto(n, known, added, repriced, errors);
                    if (found is not null) lines.Add(found);
                    break;
            }
        }
        if (errors.Count > 0) return (null, errors);
        var promotion = new KnownPromotion(id, name, Optional(Summary), Optional(Audience), lines, Optional(FinePrint));
        return (new KnownPromotionPlan(promotion, added, repriced), []);
    }

    private static string? Optional(string value) => FormText.Clean(value) is { Length: > 0 } clean ? clean : null;
}

/// <summary>One line of the promotion form: a treatment of hers or a benefit.</summary>
public abstract class KnownPromotionLineForm;

/// <summary>A treatment at a promotion price, with its regular price, which is her treatment's.</summary>
public sealed class KnownTreatmentLineForm : KnownPromotionLineForm
{
    /// <summary>Changing it here changes it on your treatments and in every promotion that uses it.</summary>
    public const string RegularPriceHelp = "Changing it here changes it on your treatments and in every promotion that uses it.";

    /// <summary>The treatment's id when it has gone from her list and the line is shown as such, until she names another.</summary>
    internal string? MissingId { get; set; }

    /// <summary>The treatment as written, or her treatment's name.</summary>
    public string Treatment { get; internal set; } = "";

    /// <summary>The promotion price, in dollars such as 3499 or 3,499.</summary>
    public string Price { get; set; } = "";

    private string _regularPrice = "";

    // Whether the box holds what the form filled in from her treatment rather than what she typed.
    private bool _filled;

    /// <summary>
    /// Her treatment's regular price, blank for none. Set by her, it is hers: changing it changes her treatment.
    /// </summary>
    public string RegularPrice
    {
        get => _regularPrice;
        set
        {
            _regularPrice = value;
            _filled = false;
        }
    }

    /// <summary>
    /// Names the treatment. An empty regular price box, or one the form filled, takes the price of her treatment of
    /// that name, or is emptied when she has none of that name: typing "Botox Cosmetic" passes through "Botox". A
    /// price she typed stays.
    /// </summary>
    public void SetTreatment(string text, KnownItems known)
    {
        Treatment = text;
        MissingId = null;
        if (_filled || FormText.Clean(RegularPrice).Length == 0) Fill(known.TreatmentNamed(text));
    }

    /// <summary>The box filled with <paramref name="treatment"/>'s regular price, blank for none, as not typed by her.</summary>
    internal void Fill(KnownTreatment? treatment)
    {
        _regularPrice = treatment?.RegularPriceText ?? "";
        _filled = true;
    }

    /// <summary>The line as the email says it, once the treatment and both prices are there.</summary>
    public string? Sentence(KnownItems known)
    {
        var name = FormText.Clean(Treatment);
        if (name.Length == 0 || Amount(Price) is not { } price || Amount(RegularPrice) is not { } regular) return null;
        return new PricedTreatment(known.TreatmentNamed(name)?.Name ?? name, price, regular).Describe();
    }

    private static decimal? Amount(string text) => FormText.Clean(text).Length == 0 ? null : FormText.Money(text, "", []);

    /// <returns>The line, or null with the reasons added to <paramref name="errors"/>.</returns>
    internal KnownTreatmentLine? ReadInto(
        int n, KnownItems known, List<KnownTreatment> added, List<KnownTreatment> repriced, List<string> errors)
    {
        var mine = new List<string>();
        var name = FormText.Clean(Treatment);
        var price = FormText.Money(Price, "the promotion price", mine);
        if (MissingId is { } missing && name == KnownPromotion.NoLongerOnYourList)
            return Finish(mine, price is { } p ? new KnownTreatmentLine(missing, p) : null);

        // Only a price she typed is a new one for her list: a filled box says her list's own.
        var regular = _filled || FormText.Clean(RegularPrice).Length == 0 ? null : FormText.Money(RegularPrice, "the regular price", mine);
        string? id = null;
        if (name.Length == 0) mine.Insert(0, "fill in the treatment, or remove the line.");   // (proposed)
        else if (known.TreatmentNamed(name) is { } existing)
        {
            id = existing.Id;
            if (regular is { } amount && amount != existing.RegularPrice)
            {
                repriced.RemoveAll(t => t.Id == existing.Id);
                repriced.Add(existing with { RegularPrice = amount });
            }
        }
        else if (known.NearMiss(KnownItemKind.Treatment, name) is { } near)
            mine.Insert(0, $"Did you mean '{near}'? It's in your known items.");
        else
        {
            var same = added.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            var treatment = same >= 0 ? added[same] : new KnownTreatment(KnownItem.NewId(), name, regular);
            if (same >= 0) added[same] = treatment = treatment with { RegularPrice = regular ?? treatment.RegularPrice };
            else added.Add(treatment);
            id = treatment.Id;
        }
        return Finish(mine, id is not null && price is { } amountOff ? new KnownTreatmentLine(id, amountOff) : null);

        KnownTreatmentLine? Finish(List<string> problems, KnownTreatmentLine? line)
        {
            errors.AddRange(problems.Select(p => $"Line {n}: {p}"));
            return problems.Count == 0 ? line : null;
        }
    }
}

/// <summary>A benefit line of the promotion form, edited as a standalone benefit.</summary>
public sealed class KnownBenefitLineForm(BenefitEditor benefit) : KnownPromotionLineForm
{
    public BenefitEditor Benefit { get; } = benefit;
}

/// <summary>
/// What a promotion form comes to: the promotion, the treatments to add to her list first, and the
/// ones of hers whose regular price it changes.
/// </summary>
public sealed record KnownPromotionPlan(
    KnownPromotion Promotion, IReadOnlyList<KnownTreatment> Added, IReadOnlyList<KnownTreatment> Repriced);
