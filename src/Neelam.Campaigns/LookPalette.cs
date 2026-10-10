using System.Globalization;

namespace Neelam.Campaigns;

/// <summary>
/// The colours a client's pages are drawn in, each one a custom property in the site's stylesheet:
/// the page background, the surface she writes on (cards and panels), borders, text, muted text such
/// as help and dates, and the accent of links and the main buttons. A <see cref="ClientLook"/> sets
/// any of them; the rest come from <see cref="Standard"/>.
/// </summary>
public sealed record LookPalette(
    string PageBackground, string Surface, string Border, string Text, string MutedText, string Accent)
{
    /// <summary>
    /// The neutral look every page has without a client's own: the defaults block at the top of
    /// <c>app.css</c>, which a test holds to these values.
    /// </summary>
    public static LookPalette Standard { get; } = new(
        PageBackground: "#f6f7f9", Surface: "#ffffff", Border: "#d5d9df", Text: "#1d2127", MutedText: "#555c66",
        Accent: "#1f4e79");

    /// <summary>The text on an accent-coloured button. Not part of a look: her accent is checked against it.</summary>
    public const string AccentText = "#ffffff";

    /// <summary>WCAG AA for normal-size text.</summary>
    public const double MinimumContrast = 4.5;

    /// <summary>Every pair of colours that text is read in, foreground on background.</summary>
    public IReadOnlyList<ContrastPair> TextPairs =>
    [
        new("Text on the page background", Text, PageBackground),
        new("Text on the editing surface", Text, Surface),
        new("Muted text on the page background", MutedText, PageBackground),
        new("Muted text on the editing surface", MutedText, Surface),
        new("Links in the accent colour on the page background", Accent, PageBackground),
        new("Links in the accent colour on the editing surface", Accent, Surface),
        new("Button text on the accent colour", AccentText, Accent),
    ];

    /// <summary>
    /// What is too faint to read, one sentence per pair below WCAG AA (4.5:1), naming the pair and its
    /// ratio; empty when every pair passes.
    /// </summary>
    public IReadOnlyList<string> ContrastProblems() =>
        TextPairs.Where(p => p.Ratio < MinimumContrast)
            .Select(p => string.Create(CultureInfo.InvariantCulture,
                $"{p.Name} is {Math.Floor(p.Ratio * 100) / 100:0.00}:1; it needs at least 4.5:1."))
            .ToList();
}

/// <summary>Text in <paramref name="Foreground"/> on <paramref name="Background"/>, both #RRGGBB.</summary>
public sealed record ContrastPair(string Name, string Foreground, string Background)
{
    /// <summary>The WCAG contrast ratio, from 1 (the same colour) to 21 (black on white).</summary>
    public double Ratio => Contrast.Ratio(Foreground, Background);
}

/// <summary>WCAG 2 contrast between two #RRGGBB colours.</summary>
public static class Contrast
{
    public static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Relative luminance, as WCAG 2 defines it from sRGB.
    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
