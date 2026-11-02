using System.Globalization;

namespace Neelam.Campaigns;

/// <summary>
/// Dollars as every email writes them, in one place: "$1,599", "$149.50", "$25". A US audience
/// whatever the server's culture, so never "$1.599" or "$149,50". Cents only when there are some.
/// </summary>
public static class Dollars
{
    public static string Text(decimal amount) =>
        amount % 1 == 0
            ? amount.ToString("$#,0", CultureInfo.InvariantCulture)
            : amount.ToString("$#,0.00", CultureInfo.InvariantCulture);
}
