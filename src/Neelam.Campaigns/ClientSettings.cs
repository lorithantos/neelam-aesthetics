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
/// How the site looks when a client's people sign in: the colours of its pages (owner, 2026-10-09:
/// "maybe a light pink background and something softer than white for the main editing"). Both the
/// client and the operator can edit it; other clients never see it. Each colour is a #RRGGBB colour,
/// or null for the standard one (<see cref="LookPalette.Standard"/>). Nothing else can be held here:
/// the colours are written into the pages' style, so the constructor is what keeps anything but a
/// colour out of it.
/// </summary>
public sealed partial record ClientLook
{
    /// <summary>Links and the main buttons.</summary>
    public string? AccentColour { get; }

    /// <summary>Behind everything.</summary>
    public string? PageBackground { get; }

    /// <summary>The cards and panels she writes in.</summary>
    public string? Surface { get; }

    public string? Border { get; }

    public string? Text { get; }

    /// <summary>Help, dates and other quieter text.</summary>
    public string? MutedText { get; }

    // The accent comes first, as it did when it was the only colour, so every earlier caller reads the same.
    public ClientLook(
        string? accentColour = null, string? pageBackground = null, string? surface = null, string? border = null,
        string? text = null, string? mutedText = null)
    {
        AccentColour = Checked(accentColour, nameof(accentColour));
        PageBackground = Checked(pageBackground, nameof(pageBackground));
        Surface = Checked(surface, nameof(surface));
        Border = Checked(border, nameof(border));
        Text = Checked(text, nameof(text));
        MutedText = Checked(mutedText, nameof(mutedText));
    }

    public static ClientLook Default { get; } = new();

    /// <summary>The colours its pages are drawn in: its own where it has one, the standard's elsewhere.</summary>
    public LookPalette Palette
    {
        get
        {
            var standard = LookPalette.Standard;
            return new LookPalette(
                PageBackground ?? standard.PageBackground, Surface ?? standard.Surface, Border ?? standard.Border,
                Text ?? standard.Text, MutedText ?? standard.MutedText, AccentColour ?? standard.Accent);
        }
    }

    /// <summary>Whether <paramref name="colour"/> is # and six hex digits, and nothing else.</summary>
    public static bool IsColour(string? colour) => colour is not null && HexColour().IsMatch(colour);

    private static string? Checked(string? colour, string name) =>
        colour is null || IsColour(colour)
            ? colour
            : throw new ArgumentException($"\"{colour}\" is not a #RRGGBB colour.", name);

    // \z, not $: $ also matches before a final newline.
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex HexColour();
}
