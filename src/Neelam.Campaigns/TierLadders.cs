using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>
/// A ladder of tier names (owner, 2026-10-09): words that tell readers which tier is which, such as
/// precious metals or gemstones, "things that imply they should be different". A tier whose name
/// holds one of the words stands on that word's rung. In an <see cref="Ordered"/> ladder the words
/// run lowest first and the rung is a weight, so a higher rung should cost more; in an equal-weight
/// one (colours, unless the client orders them) the words only tell tiers apart.
/// </summary>
/// <remarks>
/// Data, never a list a check holds: the operator keeps standard ladders for every client, and a
/// client may save her own set, which replaces the standard for her (<see cref="TierLadders"/>).
/// </remarks>
public sealed class TierLadder
{
    public string Name { get; }

    /// <summary>The words, lowest rung first when the ladder is ordered. A word may be several words ("Rose Gold").</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>Whether the words are weighted, lowest first; false means they are of equal weight.</summary>
    public bool Ordered { get; }

    public TierLadder(string name, IEnumerable<string> words, bool ordered)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A ladder needs a name.", nameof(name));
        var list = (words ?? throw new ArgumentNullException(nameof(words)))
            .Select(w => string.Join(' ', TierLadders.WordsOf(w))).ToList();
        if (list.Any(w => w.Length == 0)) throw new ArgumentException($"The ladder '{name.Trim()}' has an empty word.", nameof(words));
        if (list.Count < 2) throw new ArgumentException($"The ladder '{name.Trim()}' needs at least two words.", nameof(words));
        for (var i = 0; i < list.Count; i++)
        for (var j = i + 1; j < list.Count; j++)
            if (TierLadders.Same(TierLadders.WordsOf(list[i]), TierLadders.WordsOf(list[j])))
                throw new ArgumentException($"The ladder '{name.Trim()}' has '{list[j]}' twice.", nameof(words));
        Name = name.Trim();
        Words = list;
        Ordered = ordered;
    }

    /// <summary>The same ladder with its words in another order, as she reorders them.</summary>
    public TierLadder WithWords(IEnumerable<string> words) => new(Name, words, Ordered);

    /// <summary>
    /// The same ladder with one word moved <paramref name="by"/> places: positive is higher (towards the
    /// top rung), negative lower; a move past either end leaves it at that end. Her order is the one the
    /// checks use.
    /// </summary>
    public TierLadder Move(int word, int by)
    {
        var words = Words.ToList();
        var moved = words[word];
        words.RemoveAt(word);
        words.Insert(Math.Clamp(word + by, 0, words.Count), moved);
        return WithWords(words);
    }

    /// <summary>The same words, weighted lowest first (<paramref name="ordered"/>) or of equal weight.</summary>
    public TierLadder WithOrdered(bool ordered) => new(Name, Words, ordered);

    /// <summary>
    /// A ladder from what she typed: a name, and its words separated by commas or lines, lowest first.
    /// Null, with what is wrong, when it is not one.
    /// </summary>
    public static (TierLadder? Ladder, string? Problem) Parse(string? name, string? words, bool ordered)
    {
        try
        {
            return (new TierLadder(name ?? "", (words ?? "").Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !string.IsNullOrWhiteSpace(w)), ordered), null);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message.Split(" (Parameter")[0]);
        }
    }

    /// <summary>The rung a tier name stands on, or null when it holds none of the words, or words of two rungs.</summary>
    public int? RungOf(string tierName)
    {
        var name = TierLadders.WordsOf(tierName);
        int? found = null;
        for (var rung = 0; rung < Words.Count; rung++)
        {
            if (!TierLadders.Contains(name, TierLadders.WordsOf(Words[rung]))) continue;
            if (found is not null) return null;
            found = rung;
        }
        return found;
    }

    /// <summary>"Bronze, Silver, Gold and Platinum": the first few words, to name the ladder by example.</summary>
    public string Examples
    {
        get
        {
            var some = Words.Take(4).ToList();
            return $"{string.Join(", ", some[..^1])} and {some[^1]}";
        }
    }
}

/// <summary>
/// The tier-name ladders the checks read an offer's tier names with: the client's own set once she
/// has saved one (an empty set included), else the operator's standard, else <see cref="Standard"/>.
/// </summary>
/// <remarks>
/// <para><b>Matching</b> ignores case and plural endings, as <see cref="KnownItemMatch"/> does, and a
/// word is matched whole inside the name: "Platinum Member", "Option 1 Gold", "Golds".</para>
/// <para><b>Which ladder an offer uses</b> (<see cref="Read"/>): a word may sit in more than one
/// ladder (Diamond is a metal tier and a gemstone). An offer is read with the ladder whose words the
/// most of its tiers' names hold; on a tie, the ladder listed first. So "Gold" and "Diamond" read as
/// metals, "Ruby" and "Diamond" as gemstones, and "Diamond" alone as whichever is listed first.</para>
/// </remarks>
public sealed partial class TierLadders
{
    public IReadOnlyList<TierLadder> Ladders { get; }

    public TierLadders(IEnumerable<TierLadder> ladders)
    {
        var list = ladders.ToList();
        var twice = list.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null) throw new ArgumentException($"Two ladders are named '{twice.Key}'.", nameof(ladders));
        Ladders = list;
    }

    /// <summary>
    /// The standard ladders the tool starts with, in force until the operator saves others (owner,
    /// 2026-10-09: "Let's do the first two as weighted values and the colors as having equal weight
    /// unless ordered by the client"). Metals and gemstones are weighted, lowest first. The colours
    /// are a short everyday list, of equal weight.
    /// </summary>
    public static TierLadders Standard { get; } = new(
    [
        new TierLadder("Metals", ["Bronze", "Silver", "Gold", "Platinum", "Diamond"], ordered: true),
        new TierLadder("Gemstones", ["Pearl", "Sapphire", "Ruby", "Emerald", "Diamond"], ordered: true),
        new TierLadder("Colours", ["Rose", "Blue", "Green", "Purple", "Black", "White", "Red", "Pink"], ordered: false),
    ]);

    /// <summary>No ladders: a client that saves this gets no ladder notes.</summary>
    public static TierLadders None { get; } = new([]);

    /// <summary>These ladders with the one at <paramref name="index"/> replaced.</summary>
    public TierLadders Replace(int index, TierLadder ladder) =>
        new(Ladders.Select((l, i) => i == index ? ladder : l));

    /// <summary>These ladders without the one at <paramref name="index"/>, a standard one included.</summary>
    public TierLadders Without(int index) => new(Ladders.Where((_, i) => i != index));

    /// <summary>These ladders and one more, last. Refused when one already has its name.</summary>
    public TierLadders With(TierLadder ladder) => new([.. Ladders, ladder]);

    /// <summary>
    /// The ladder an offer's tier names are read with and each tier's rung on it, or null when no
    /// ladder holds the names of two tiers or more.
    /// </summary>
    public LadderReading? Read(IReadOnlyList<string> tierNames)
    {
        LadderReading? best = null;
        var bestCount = 1;
        foreach (var ladder in Ladders)
        {
            var rungs = tierNames.Select(ladder.RungOf).ToList();
            var count = rungs.Count(r => r is not null);
            if (count > bestCount) (best, bestCount) = (new LadderReading(ladder, rungs), count);
        }
        return best;
    }

    internal static string[] WordsOf(string? s) =>
        NotAWord().Split(s ?? "").Where(w => w.Length > 0).ToArray();

    internal static bool Same(string[] a, string[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => KnownItemMatch.SameWord(p.First, p.Second));

    // Whether the name holds the word's words, together and whole.
    internal static bool Contains(string[] name, string[] word)
    {
        if (word.Length == 0) return false;
        for (var start = 0; start + word.Length <= name.Length; start++)
            if (Same(name[start..(start + word.Length)], word)) return true;
        return false;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotAWord();
}

/// <summary>An offer's tier names read on one ladder: each tier's rung, by position, or null when it has none.</summary>
public sealed record LadderReading(TierLadder Ladder, IReadOnlyList<int?> Rungs);
