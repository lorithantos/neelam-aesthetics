namespace Neelam.Campaigns;

/// <summary>
/// A known treatment as the Known items page builds it: a name and an optional regular price.
/// Turned into a <see cref="KnownTreatment"/> only when the price, if any, is an amount, with the
/// reason when it is not. Whether the name is filled in, and the price above zero, is the store's
/// to say (<see cref="KnownItemRules"/>), as for every item.
/// </summary>
public sealed class KnownTreatmentForm
{
    public string Name { get; set; } = "";

    /// <summary>In dollars, such as 5100 or 5,100; blank for none.</summary>
    public string RegularPrice { get; set; } = "";

    /// <summary>A form holding a known treatment, to change it.</summary>
    public static KnownTreatmentForm Of(KnownTreatment treatment) =>
        new() { Name = treatment.Name, RegularPrice = treatment.RegularPriceText };

    /// <returns>The treatment under <paramref name="id"/>, or null with what is wrong.</returns>
    public (KnownTreatment? Treatment, IReadOnlyList<string> Errors) ToItem(string id)
    {
        var errors = new List<string>();
        var price = FormText.Clean(RegularPrice).Length == 0 ? null : FormText.Money(RegularPrice, "the regular price", errors);
        return errors.Count == 0 ? (new KnownTreatment(id, FormText.Clean(Name), price), []) : (null, errors);
    }
}
