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

    /// <summary>The price as the campaign shows it, such as $299/month or $149.50/month.</summary>
    public string PriceShown => EditorExport.PriceText(Price, isRecurring: true);
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
/// How an offer detail (a tier name, a benefit line, a treatment in the Item field) compares with
/// the client's known items: the threshold, in one place. Prose is never compared (owner,
/// 2026-10-09): spelling in prose is the proofread's job.
/// <list type="bullet">
/// <item><b>Known</b> (<see cref="IsKnown"/>): the same words as a known item, ignoring case,
/// surrounding and repeated space, and a plural ending on any word ("Facials" for "Facial",
/// "Lashes" for "Lash"). Nothing is said.</item>
/// <item><b>Near miss</b> (<see cref="NearMiss"/>): otherwise, within the known item's
/// <see cref="EditBudget"/> of single-character edits ignoring case (insert, delete or change, as
/// Levenshtein counts them), and carrying the same digits in the same order. The digit condition
/// keeps "15% off" from being taken for a typo of "10% off": a different number is a decision, not
/// a slip.</item>
/// <item>Anything else is not one of the known items at all.</item>
/// </list>
/// </summary>
public static class KnownItemMatch
{
    /// <summary>A known item this many characters long or longer may be two edits off; a shorter one, one.</summary>
    public const int LongItemLength = 10;

    /// <summary>How many edits away a near miss of <paramref name="known"/> may be.</summary>
    public static int EditBudget(string known) => known.Trim().Length >= LongItemLength ? 2 : 1;

    /// <summary>Whether <paramref name="written"/> says the same as any of <paramref name="known"/>.</summary>
    public static bool IsKnown(string written, IEnumerable<string> known) =>
        Matching(written, known) is not null;

    /// <returns>The known text <paramref name="written"/> says the same as, or null when it is none.</returns>
    public static string? Matching(string written, IEnumerable<string> known)
    {
        var w = Words(written);
        if (w.Length == 0) return null;
        return known.FirstOrDefault(k => Same(w, Words(k)));
    }

    /// <returns>The known text <paramref name="written"/> most nearly matches, or null when it is known or near none.</returns>
    public static string? NearMiss(string written, IEnumerable<string> known)
    {
        var w = (written ?? "").Trim();
        if (w.Length == 0) return null;
        var candidates = known.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        if (IsKnown(w, candidates)) return null;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var k in candidates)
        {
            if (Digits(w) != Digits(k)) continue;
            var budget = EditBudget(k);
            var distance = Edits(w.ToLowerInvariant(), k.ToLowerInvariant(), budget);
            if (distance <= budget && distance < bestDistance) (best, bestDistance) = (k, distance);
        }
        return best;
    }

    private static string[] Words(string? s) =>
        (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool Same(string[] a, string[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => SameWord(p.First, p.Second));

    // The same word, ignoring case, or one the other with a plural ending: "Facials", "Lashes".
    private static bool SameWord(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (!longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)) return false;
        var ending = longer[shorter.Length..];
        return ending.Equals("s", StringComparison.OrdinalIgnoreCase) || ending.Equals("es", StringComparison.OrdinalIgnoreCase);
    }

    private static string Digits(string s) => string.Concat(s.Where(char.IsAsciiDigit));

    // Levenshtein distance, stopping early once it cannot come in under the budget.
    private static int Edits(string a, string b, int budget)
    {
        if (Math.Abs(a.Length - b.Length) > budget) return budget + 1;
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
            if (rowMin > budget) return budget + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
