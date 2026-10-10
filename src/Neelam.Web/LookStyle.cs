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
    /// <summary>
    /// The look's colours as declarations (<c>--bg:#faf0f2;--surface:#fffbf7</c>), for a style block
    /// or the look page's sample; null when it sets none.
    /// </summary>
    public static string? Declarations(ClientLook look)
    {
        var declarations = new List<string>();
        Add("--bg", look.PageBackground);
        Add("--surface", look.Surface);        Add("--border", look.Border);
        Add("--text", look.Text);
        Add("--text-muted", look.MutedText);
        Add("--accent", look.AccentColour);
        // A button under the pointer goes a little darker than her accent, as the standard one does.
        Add("--accent-hover", look.AccentColour is { } accent ? Darker(accent) : null);
        return declarations.Count == 0 ? null : string.Join(";", declarations);

        void Add(string property, string? colour)
        {
            if (colour is not null) declarations.Add($"{property}:{colour}");
        }
    }

    /// <summary>Every colour of <paramref name="palette"/> as declarations, the ones it shares with the standard look included.</summary>
    public static string Declarations(LookPalette palette) =>
        Declarations(new ClientLook(
            palette.Accent, palette.PageBackground, palette.Surface, palette.Border, palette.Text, palette.MutedText))!;

    /// <summary>The style block a client's pages carry, <c>:root{...}</c>; null when the look sets no colour.</summary>
    public static string? RootBlock(ClientLook look) => Declarations(look) is { } d ? ":root{" + d + "}" : null;

    // Each channel at four fifths, still #rrggbb.
    internal static string Darker(string hex)
    {
        string Channel(int at) =>
            (int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 4 / 5)
            .ToString("x2", CultureInfo.InvariantCulture);
        return "#" + Channel(1) + Channel(3) + Channel(5);
    }
}
