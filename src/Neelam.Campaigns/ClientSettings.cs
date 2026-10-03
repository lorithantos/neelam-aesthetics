using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>Whether a catalog entry is a procedure or a medication.</summary>
public enum OfferingKind
{
    Procedure,
    Medication,
}

/// <summary>
/// Something the client offers, by the name a campaign's benefits use (a <see cref="FreeItem"/>'s
/// or <see cref="DiscountedItem"/>'s <c>ItemName</c>), with its usual price when there is one.
/// </summary>
public sealed record CatalogEntry(string Name, OfferingKind Kind, decimal? UsualPrice = null);

/// <summary>
/// The procedures and medications a client offers, and their usual prices. The client's own data:
/// it lives in the client's container, and the operator sees it only under a support grant.
/// </summary>
public sealed record ClientCatalog
{
    public IReadOnlyList<CatalogEntry> Entries { get; }

    public ClientCatalog(IReadOnlyList<CatalogEntry> entries)
    {
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name))
                throw new ArgumentException("A catalog entry needs a name.", nameof(entries));
            if (e.UsualPrice is < 0)
                throw new ArgumentException($"{e.Name}: a usual price cannot be negative.", nameof(entries));
        }
        // Two entries with one name would make "which one is the campaign offering?" unanswerable.
        var repeated = entries.GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw new ArgumentException($"\"{repeated.Key}\" is in the catalog twice.", nameof(entries));
        Entries = entries;
    }

    public static ClientCatalog Empty { get; } = new([]);

    /// <summary>The entry a campaign's item name refers to, if the client offers it.</summary>
    public CatalogEntry? Find(string itemName) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name.Trim(), itemName.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// How the site looks when a client's people sign in. Both the client and the operator can edit it;
/// other clients never see it. Only the accent colour exists so far: the rest of the look is
/// designed later, and each part is added here when it is.
/// </summary>
public sealed partial record ClientLook
{
    /// <summary>A #RRGGBB colour, or null for the default.</summary>
    public string? AccentColour { get; }

    public ClientLook(string? accentColour = null)
    {
        if (accentColour is not null && !HexColour().IsMatch(accentColour))
            throw new ArgumentException($"\"{accentColour}\" is not a #RRGGBB colour.", nameof(accentColour));
        AccentColour = accentColour;
    }

    public static ClientLook Default { get; } = new();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}
