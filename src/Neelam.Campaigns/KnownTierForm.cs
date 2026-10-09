namespace Neelam.Campaigns;

/// <summary>
/// A known tier as the Known items page builds it: a name, a price, and benefit lines, each picked
/// from her benefit items or typed. Turned into a <see cref="KnownTier"/> only when every part makes
/// sense, with the reasons when it does not.
/// </summary>
public sealed class KnownTierForm
{
    private readonly List<BenefitEditor> _lines = [];

    public string Name { get; set; } = "";

    /// <summary>In dollars, such as 299 or 149.50.</summary>
    public string Price { get; set; } = "";

    public IReadOnlyList<BenefitEditor> Lines => _lines;

    /// <summary>A form holding a known tier, to change it.</summary>
    public static KnownTierForm Of(KnownTier tier)
    {
        var form = new KnownTierForm { Name = tier.Name, Price = tier.PriceText };
        foreach (var b in tier.Benefits) form._lines.Add(BenefitEditor.Standalone(b));
        return form;
    }

    /// <summary>An empty line at the end, to choose a kind for and fill in.</summary>
    public BenefitEditor AddLine()
    {
        var line = BenefitEditor.Standalone();
        _lines.Add(line);
        return line;
    }

    /// <summary>A benefit item's line at the end, as ordinary fields she can change.</summary>
    public BenefitEditor AddKnown(KnownBenefit benefit)
    {
        var line = BenefitEditor.Standalone(benefit.Benefit);
        _lines.Add(line);
        return line;
    }

    public void RemoveLine(BenefitEditor line) => _lines.Remove(line);

    /// <returns>The tier under <paramref name="id"/>, or null with what is wrong.</returns>
    public (KnownTier? Tier, IReadOnlyList<string> Errors) ToItem(string id)
    {
        var errors = new List<string>();
        var name = FormText.Need(Name, "the tier's name", errors);
        var price = FormText.Money(Price, "the tier's price", errors);
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Error is { } error) errors.Add($"Benefit {i + 1}: {error}");
            else if (_lines[i].Value is null) errors.Add($"Benefit {i + 1}: fill it in, or remove it.");
        }
        return errors.Count == 0
            ? (new KnownTier(id, name, price!.Value, _lines.Select(l => l.Value!).ToList()), [])
            : (null, errors);
    }
}
