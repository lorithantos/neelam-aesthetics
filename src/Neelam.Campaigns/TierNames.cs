using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>
/// How tier names compare when their numbers are set aside (owner, 2026-10-09). The real first send
/// named its options "Option 1 Platinum Member" and "Option 2 Platinum Member": "common, different
/// number, common". Every run of digits is the same token here, with no list of words such as
/// "Option" or "Tier" (data over dogma), so "Glow 50" and "Glow 100" compare the same too: that is
/// why a name the same apart from its numbers is only worth a strongly worded look, never a block.
/// </summary>
public static partial class TierNames
{
    /// <summary>
    /// The name with every run of digits as one token, ignoring case and spacing: "Option 1 Platinum
    /// Member" and "option 2  platinum member" give the same shape.
    /// </summary>
    public static string Shape(string name) =>
        string.Join(' ', Words(Digits().Replace(name ?? "", "#")).Select(w => w.ToLowerInvariant()));

    /// <summary>Whether the name carries a number at all.</summary>
    public static bool HasNumber(string name) => Digits().IsMatch(name ?? "");

    /// <summary>
    /// The name without its number-bearing parts: a leading word and number when more words follow
    /// ("Option 1 Platinum Member", "Tier 2 Gold"), then any word that is only a number, with its
    /// punctuation ("Platinum Member 2", "#1", "(2)"). "Glow 50" is "Glow": its word is the name.
    /// A name that is only numbers is kept as it is.
    /// </summary>
    public static string Base(string name)
    {
        var words = Words(name).ToList();
        if (words.Count > 2 && !IsNumber(words[0]) && IsNumber(words[1])) words.RemoveRange(0, 2);
        var rest = words.Where(w => !IsNumber(w)).ToList();
        return rest.Count > 0 ? string.Join(' ', rest) : (name ?? "").Trim();
    }

    private static string[] Words(string? s) => (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsNumber(string word) => NumberWord().IsMatch(word);

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^\W*\d+\W*$")]
    private static partial Regex NumberWord();
}
