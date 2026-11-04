using System.Globalization;

namespace Neelam.Campaigns;

/// <summary>
/// A number written plainly, in one place: an amount as a box holds it ("5100", "149.5", "30"), and a
/// percent or a count as the email writes one ("50" in "50% off"). At most two decimal places and no
/// separators, the same whatever the server's culture, so never "149,5". Dollars in the email are
/// <see cref="Dollars"/>'s. One copy since 2026-11-03; nine places wrote the format themselves.
/// </summary>
public static class PlainNumber
{
    public static string Text(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
