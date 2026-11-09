using System.Globalization;
using Neelam.Campaigns;

namespace Neelam.Web;

/// <summary>
/// A client's look as the custom properties of <c>app.css</c>, which every rule there draws with.
/// Only colours the look sets are written; the stylesheet's own defaults, the standard look, stand for
/// the rest. Every value comes from a <see cref="ClientLook"/>, which holds nothing but #RRGGBB, so
/// nothing a look's document says can reach a page's style but a colour.
/// </summary>
public static class LookStyle
{
    // Each custom property a look can set and where its colour comes from, in the order they are
    // written; a null colour is one the look does not set.
    private static readonly (string Property, Func<ClientLook, string?> Colour)[] Rows =
    [
        ("--bg", look => look.PageBackground),
        ("--surface", look => look.Surface),
        ("--border", look => look.Border),
        ("--text", look => look.Text),
        ("--text-muted", look => look.MutedText),
        ("--accent", look => look.AccentColour),
        // A button under the pointer goes a little darker than her accent, as the standard one does.
        ("--accent-hover", look => look.AccentColour is { } accent ? Darker(accent) : null),
    ];

    /// <summary>The custom properties a look can set, in the order they are written.</summary>
    internal static IReadOnlyList<string> Properties { get; } = [.. Rows.Select(row => row.Property)];

    /// <summary>
    /// The look's colours as declarations (<c>--bg:#faf0f2;--surface:#fffbf7</c>), for a style block
    /// or the look page's sample; null when it sets none.
    /// </summary>
    public static string? CssDeclarations(this ClientLook look)
    {
        var declarations = new List<string>();
        foreach (var (property, colour) in Rows)
        {
            if (colour(look) is { } value) declarations.Add($"{property}:{value}");
        }
        return declarations.Count == 0 ? null : string.Join(";", declarations);
    }

    /// <summary>Every colour of <paramref name="palette"/> as declarations, the ones it shares with the standard look included.</summary>
    public static string CssDeclarations(this LookPalette palette) =>
        new ClientLook(
            palette.Accent, palette.PageBackground, palette.Surface, palette.Border, palette.Text, palette.MutedText)
            .CssDeclarations()!;

    /// <summary>The style block a client's pages carry, <c>:root{...}</c>; null when the look sets no colour.</summary>
    public static string? CssRootBlock(this ClientLook look) => look.CssDeclarations() is { } d ? ":root{" + d + "}" : null;

    // Each channel at four fifths, still #rrggbb.
    internal static string Darker(string hex)
    {
        string Channel(int at) =>
            (int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 4 / 5)
            .ToString("x2", CultureInfo.InvariantCulture);
        return "#" + Channel(1) + Channel(3) + Channel(5);
    }
}
