using System.Globalization;

namespace Neelam.Campaigns;

/// <summary>The three kinds of known item (owner, 2026-10-09).</summary>
public enum KnownItemKind
{
    /// <summary>A treatment or service by its name, such as "Botox" or "Wellness injection".</summary>
    Treatment,

    /// <summary>A benefit line, such as "10% off any qualifying treatments".</summary>
    Benefit,

    /// <summary>A whole tier: its name, its price and its benefit lines.</summary>
    Tier,
}

/// <summary>
/// Something a client writes again and again, kept once so it is spelled once: picked while writing
/// a campaign instead of retyped, and the checks flag a line that nearly matches one. The client's
/// own data, kept per client; the id is the item's for as long as it exists, so it can be changed
/// and removed.
/// </summary>
public abstract record KnownItem(string Id)
{
    public abstract KnownItemKind Kind { get; }

    /// <summary>What lists and pickers show: a treatment's or tier's name, or a benefit's sentence.</summary>
    public abstract string Text { get; }

    /// <summary>A new item's id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>A treatment or service, by the name every campaign should spell it with.</summary>
public sealed record KnownTreatment(string Id, string Name) : KnownItem(Id)
{
    public override KnownItemKind Kind => KnownItemKind.Treatment;
    public override string Text => Name;
}

/// <summary>
/// A benefit line. Benefits are typed (<see cref="Benefit"/>), so a known one is too: picking it puts
/// the same kind and values in the form, and its text is the sentence the email will carry.
/// </summary>
public sealed record KnownBenefit(string Id, Benefit Benefit) : KnownItem(Id)
{
    public override KnownItemKind Kind => KnownItemKind.Benefit;
    public override string Text => Benefit.Describe();
}

/// <summary>
/// A whole tier, dropped into an offer at once. It carries its own benefit lines rather than
/// pointing at known benefits: a tier is what goes out together, so changing or removing a benefit
/// item must not quietly change a tier, and a tier saved from a campaign keeps lines that were never
/// benefit items.
/// </summary>
public sealed record KnownTier(string Id, string Name, decimal Price, IReadOnlyList<Benefit> Benefits) : KnownItem(Id)
{
    public override KnownItemKind Kind => KnownItemKind.Tier;
    public override string Text => Name;

    /// <summary>The price as the form shows it, such as 299 or 149.50.</summary>
    public string PriceText => Price.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>A client's known items, each kind in the order lists show it: alphabetical by its text.</summary>
public sealed class KnownItems
{
    public KnownItems(IEnumerable<KnownItem> items)
    {
        All = items.OrderBy(i => i.Kind).ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static KnownItems None { get; } = new([]);

    public IReadOnlyList<KnownItem> All { get; }

    public bool IsEmpty => All.Count == 0;

    public IReadOnlyList<KnownTreatment> Treatments => All.OfType<KnownTreatment>().ToList();
    public IReadOnlyList<KnownBenefit> Benefits => All.OfType<KnownBenefit>().ToList();
    public IReadOnlyList<KnownTier> Tiers => All.OfType<KnownTier>().ToList();

    public KnownItem? Find(string id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary>The known benefit a picker's text names: exactly, else ignoring case.</summary>
    public KnownBenefit? BenefitNamed(string text) => Named(Benefits, text);

    /// <summary>The known tier a picker's text names: exactly, else ignoring case.</summary>
    public KnownTier? TierNamed(string text) => Named(Tiers, text);

    /// <summary>
    /// The item of the same kind with the same text, ignoring case and surrounding space, other
    /// than <paramref name="item"/> itself: a list that says the same thing twice is no help.
    /// </summary>
    public KnownItem? SameAs(KnownItem item) =>
        All.FirstOrDefault(i => i.Kind == item.Kind && i.Id != item.Id
                                && string.Equals(i.Text.Trim(), item.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a benefit line is checked against: the benefit items, and the lines of the known tiers.
    /// </summary>
    public IReadOnlyList<string> BenefitLines =>
        Benefits.Select(b => b.Text).Concat(Tiers.SelectMany(t => t.Benefits.Select(b => b.Describe())))
            .Distinct(StringComparer.Ordinal).ToList();

    private static T? Named<T>(IReadOnlyList<T> items, string text) where T : KnownItem
    {
        var clean = (text ?? "").Trim();
        if (clean.Length == 0) return null;
        return items.FirstOrDefault(i => i.Text.Trim() == clean)
               ?? items.FirstOrDefault(i => string.Equals(i.Text.Trim(), clean, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What makes a known item one that can be kept, the same for every store.</summary>
public static class KnownItemRules
{
    /// <returns>Why the item cannot be kept as it stands; empty when it can.</returns>
    public static IReadOnlyList<string> Problems(KnownItem item, KnownItems existing)
    {
        var problems = new List<string>();
        if (!Guid.TryParseExact(item.Id, "N", out _))
            problems.Add("A known item's id is 32 hex digits.");
        switch (item)
        {
            case KnownTreatment t when string.IsNullOrWhiteSpace(t.Name):
                problems.Add("Fill in the treatment's name.");
                break;
            case KnownTier t:
                if (string.IsNullOrWhiteSpace(t.Name)) problems.Add("Fill in the tier's name.");
                if (t.Price <= 0) problems.Add("A tier needs a price above zero.");
                break;
        }
        if (problems.Count == 0 && existing.SameAs(item) is { } same)
            problems.Add($"\"{same.Text}\" is already in your known items.");
        return problems;
    }
}

/// <summary>
/// Whether something written nearly matches a known item: the threshold, in one place.
/// <list type="bullet">
/// <item>Exactly a known item (ignoring surrounding space): no match, it is right.</item>
/// <item>The same letters in a different case, such as "wellness Injection" for "Wellness injection": a near miss.</item>
/// <item>Otherwise, when both are at least <see cref="MinLength"/> characters, at most
/// <see cref="MaxEdits"/> single-character edits apart ignoring case (insert, delete or change, as
/// Levenshtein counts them), and carrying the same digits in the same order: a near miss. The digit
/// condition keeps "15% off" from being taken for a typo of "10% off": a different number is a
/// decision, not a slip.</item>
/// </list>
/// Shorter items are compared only by case, since two edits turn "Botox" into many real words.
/// </summary>
public static class KnownItemMatch
{
    public const int MinLength = 6;
    public const int MaxEdits = 2;

    /// <returns>The known text <paramref name="written"/> most nearly matches, or null when it is exact or near none.</returns>
    public static string? NearMiss(string written, IEnumerable<string> known)
    {
        var w = (written ?? "").Trim();
        if (w.Length == 0) return null;
        var candidates = known.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        if (candidates.Any(k => k == w)) return null;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var k in candidates)
        {
            int distance;
            if (string.Equals(k, w, StringComparison.OrdinalIgnoreCase)) distance = 0;
            else if (w.Length >= MinLength && k.Length >= MinLength && Digits(w) == Digits(k))
                distance = Edits(w.ToLowerInvariant(), k.ToLowerInvariant());
            else continue;
            if (distance <= MaxEdits && distance < bestDistance) (best, bestDistance) = (k, distance);
        }
        return best;
    }

    private static string Digits(string s) => string.Concat(s.Where(char.IsAsciiDigit));

    // Levenshtein distance, stopping early once it cannot come in under the threshold.
    private static int Edits(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > MaxEdits) return MaxEdits + 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > MaxEdits) return MaxEdits + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
