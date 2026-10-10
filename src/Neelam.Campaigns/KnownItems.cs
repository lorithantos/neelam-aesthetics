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
/// A benefit line, as a pattern (owner, 2026-10-09: known lines are "replacements with limits if
/// needed"). Benefits are typed (<see cref="Benefit"/>), so a known one is too: its kind and its words
/// (what it is off, the item, how often, which ones) are the line, and its amounts are the usual ones.
/// Picking it puts the same kind, words and usual amounts in the form, and she changes the amount as
/// needed. A campaign's benefit is this line when its kind and words are the same, whatever its
/// amounts (<see cref="Matches"/>); an amount outside <paramref name="Limits"/> is worth a look, never
/// a block. No limit on an amount: any amount of it is fine.
/// </summary>
/// <param name="Limits">At most one per amount of <paramref name="Benefit"/>; null or empty when none is limited.</param>
public sealed record KnownBenefit(string Id, Benefit Benefit, IReadOnlyList<AmountLimit>? Limits = null) : KnownItem(Id)
{
    public override KnownItemKind Kind => KnownItemKind.Benefit;

    /// <summary>The sentence at the usual amounts: what pickers list and what picking fills in.</summary>
    public override string Text => Benefit.Describe();

    /// <summary>The limits that limit something; none for a line from before limits existed.</summary>
    public IReadOnlyList<AmountLimit> SetLimits => (Limits ?? []).Where(l => l.IsSet).ToList();

    /// <summary>The limit on one of its amounts, or null when that amount may be anything.</summary>
    public AmountLimit? LimitOn(string field) => SetLimits.FirstOrDefault(l => l.Field == field);

    /// <summary>
    /// The line as her list shows it: "10% off any qualifying treatments — usually 10%, between 5% and
    /// 10%", or "... — usually 10%, any amount" with no limit.
    /// </summary>
    public string Shown =>
        $"{Text} — " + string.Join("; ", Benefit.Amounts.Select(a =>
            $"usually {a}, {(LimitOn(a.Field.Name) is { } limit ? limit.Between(a.Field) : "any amount")}"));

    /// <summary>
    /// Whether <paramref name="written"/> is this line: the same kind of benefit and the same words,
    /// ignoring case, spacing and plural endings as <see cref="KnownItemMatch"/> does. Its amounts are
    /// not compared here: they are held to the limits (<see cref="OutsideLimits"/>).
    /// </summary>
    public bool Matches(Benefit written) =>
        written.GetType() == Benefit.GetType() && KnownItemMatch.IsKnown(written.Pattern(), [Benefit.Pattern()]);

    /// <summary>Each amount of <paramref name="written"/> outside this line's limit on it; empty when none is.</summary>
    public IReadOnlyList<OutsideLimit> OutsideLimits(Benefit written) =>
        written.Amounts
            .Select(a => (Amount: a, Limit: LimitOn(a.Field.Name)))
            .Where(p => p.Limit is { } limit && !limit.Allows(p.Amount.Value))
            .Select(p => new OutsideLimit(p.Amount, p.Limit!))
            .ToList();

    /// <summary>Why the limits cannot be kept as they stand; empty when they can.</summary>
    public IReadOnlyList<string> LimitProblems()
    {
        var problems = new List<string>();
        var amounts = Benefit.Amounts;
        foreach (var limit in Limits ?? [])
        {
            if (amounts.FirstOrDefault(a => a.Field.Name == limit.Field) is not { } usual)
            {
                problems.Add("A limit names an amount this kind of benefit doesn't have.");
                continue;
            }
            if ((Limits ?? []).Count(l => l.Field == limit.Field) > 1)
            {
                problems.Add($"The {usual.Field.Label} has more than one limit.");
                continue;
            }
            var field = usual.Field;
            if (field.Unit != AmountUnit.Dollars && (limit.Min % 1 is not (null or 0m) || limit.Max % 1 is not (null or 0m)))
                problems.Add($"The lowest and highest {field.Label} are whole numbers.");
            if (limit is { Min: { } lo, Max: { } hi } && lo > hi)
                problems.Add($"The lowest {field.Label} ({field.Show(lo)}) is above the highest ({field.Show(hi)}).");
            else if (limit.Min is { } min && usual.Value < min)
                problems.Add($"The usual {field.Label} ({usual}) is below the lowest ({field.Show(min)}).");
            else if (limit.Max is { } max && usual.Value > max)
                problems.Add($"The usual {field.Label} ({usual}) is above the highest ({field.Show(max)}).");
        }
        return problems.Distinct().ToList();
    }
}

/// <summary>
/// The lowest and highest one amount of a known benefit line usually is; either may be left open.
/// </summary>
/// <param name="Field">The amount's field, as <see cref="AmountField.Name"/> names it.</param>
public sealed record AmountLimit(string Field, decimal? Min, decimal? Max)
{
    /// <summary>Whether it limits anything: with neither end set, any amount is fine.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSet => Min is not null || Max is not null;

    public bool Allows(decimal value) => (Min is not { } lo || value >= lo) && (Max is not { } hi || value <= hi);

    /// <summary>As a finding writes it: "5%–10%", "at least 5%", "up to 10%".</summary>
    public string Range(AmountField field) => (Min, Max) switch
    {
        ({ } lo, { } hi) => $"{field.Show(lo)}–{field.Show(hi)}",
        ({ } lo, null) => $"at least {field.Show(lo)}",
        (null, { } hi) => $"up to {field.Show(hi)}",
        _ => "any amount",
    };

    /// <summary>As her list writes it: "between 5% and 10%", "at least 5%", "at most 10%".</summary>
    public string Between(AmountField field) => (Min, Max) switch
    {
        ({ } lo, { } hi) => $"between {field.Show(lo)} and {field.Show(hi)}",
        ({ } lo, null) => $"at least {field.Show(lo)}",
        (null, { } hi) => $"at most {field.Show(hi)}",
        _ => "any amount",
    };
}

/// <summary>An amount a campaign wrote that is outside the known line's limit on it.</summary>
public sealed record OutsideLimit(BenefitAmount Amount, AmountLimit Limit)
{
    public string Range => Limit.Range(Amount.Field);

    /// <summary>Above the highest; otherwise it is below the lowest.</summary>
    public bool AboveHighest => Limit.Max is { } hi && Amount.Value > hi;
}

/// <summary>How a campaign's benefit stands against her benefit lines and her known tiers' lines.</summary>
public enum BenefitStanding
{
    /// <summary>One of her lines, within its limits, or a known tier's line word for word. Nothing is said.</summary>
    Known,

    /// <summary>One of her lines, with an amount outside its limits: worth a look.</summary>
    OutsideLimits,

    /// <summary>Close to one of her lines in its words, or to a known tier's line: "Did you mean".</summary>
    NearMiss,

    /// <summary>None of them: a neutral note, with saving it as a line one click away.</summary>
    NotKnown,
}

/// <summary>What <see cref="KnownItems.CheckBenefit"/> found.</summary>
/// <param name="Suggestion">For a near miss: the line it nearly is, at the amounts written.</param>
/// <param name="Line">The known benefit line it is, or nearly is; null for a tier's line or none.</param>
/// <param name="Outside">For <see cref="BenefitStanding.OutsideLimits"/>: each amount outside its limit.</param>
public sealed record BenefitCheck(
    BenefitStanding Standing, string? Suggestion = null, KnownBenefit? Line = null, IReadOnlyList<OutsideLimit>? Outside = null)
{
    public static BenefitCheck Known { get; } = new(BenefitStanding.Known);
    public static BenefitCheck NotKnown { get; } = new(BenefitStanding.NotKnown);
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
    /// The known tier a campaign's tier is, by its name: the same name as <see cref="KnownItemMatch"/>
    /// compares them; else, for a known name with no number in it, the written name without its
    /// number-bearing parts (<see cref="TierNames.Base"/>), so "Option 1 Platinum Member" is her
    /// "Platinum Member" and its price is held to hers. A known name carrying a number is matched
    /// whole only: "Glow 50" is not her "Glow 100", whose number is the difference.
    /// </summary>
    public KnownTier? TierFor(string name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) return null;
        return Tiers.FirstOrDefault(t => KnownItemMatch.IsKnown(clean, [t.Name]))
               ?? (TierNames.HasNumber(clean)
                   ? Tiers.FirstOrDefault(t => !TierNames.HasNumber(t.Name) && KnownItemMatch.IsKnown(TierNames.Base(clean), [t.Name]))
                   : null);
    }

    /// <summary>
    /// The item of the same kind that says the same thing, other than <paramref name="item"/> itself:
    /// a list that says the same thing twice is no help. A treatment or tier says the same as another
    /// with the same text, ignoring case and surrounding space; a benefit line, as another of the same
    /// kind with the same words, whatever its amounts, since both would match the same benefits.
    /// </summary>
    public KnownItem? SameAs(KnownItem item) =>
        All.FirstOrDefault(i => i.Kind == item.Kind && i.Id != item.Id && SaysTheSame(i, item));

    private static bool SaysTheSame(KnownItem a, KnownItem b) => (a, b) switch
    {
        (KnownBenefit x, KnownBenefit y) => x.Benefit.GetType() == y.Benefit.GetType()
            && string.Equals(x.Benefit.Pattern().Trim(), y.Benefit.Pattern().Trim(), StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(a.Text.Trim(), b.Text.Trim(), StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Her known tiers' lines, word for word. A tier goes out as a whole, so its lines are literal:
    /// a benefit is one of them only with the same amounts too. Only her benefit lines are patterns.
    /// </summary>
    public IReadOnlyList<string> TierLines =>
        Tiers.SelectMany(t => t.Benefits.Select(b => b.Describe())).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Whether there is anything a benefit is checked against: a benefit line or a known tier's line.</summary>
    public bool HasBenefitLines => Benefits.Count > 0 || TierLines.Count > 0;

    /// <summary>
    /// How a campaign's benefit stands against her benefit lines and her known tiers' lines:
    /// <list type="number">
    /// <item>One of her lines (<see cref="KnownBenefit.Matches"/>): known when its amounts are within
    /// that line's limits, else <see cref="BenefitStanding.OutsideLimits"/>. A different amount is
    /// never a near miss: it is a decision, held only to the limits.</item>
    /// <item>A known tier's line, word for word and amount for amount: known. Words she may choose
    /// between for the same benefit ("free" or "complimentary") are the same word here, as they are
    /// to her lines' patterns.</item>
    /// <item>Close to one of her lines in its words (the same kind of benefit, the amounts left out,
    /// <see cref="KnownItemMatch.NearMiss"/>): a near miss, suggesting that line at the amounts
    /// written. Else close to a known tier's line as written: a near miss, suggesting that line.</item>
    /// <item>Otherwise not known.</item>
    /// </list>
    /// </summary>
    public BenefitCheck CheckBenefit(Benefit written)
    {
        var lines = Benefits.Where(k => k.Matches(written)).ToList();
        if (lines.Count > 0)
        {
            if (lines.Any(l => l.OutsideLimits(written).Count == 0)) return BenefitCheck.Known;
            return new(BenefitStanding.OutsideLimits, Line: lines[0], Outside: lines[0].OutsideLimits(written));
        }

        // A known tier's lines are compared as the checks compare benefits (Benefit.Canonical): "free"
        // and "complimentary" are the same line. A near miss is suggested in the words she chose.
        var sentence = written.Canonical().Describe();
        var tierLines = Tiers.SelectMany(t => t.Benefits).Select(b => b.Canonical()).Distinct().ToList();
        var compared = tierLines.Select(b => b.Describe()).ToList();
        if (KnownItemMatch.IsKnown(sentence, compared)) return BenefitCheck.Known;

        var sameKind = Benefits.Where(k => k.Benefit.GetType() == written.GetType()).ToList();
        if (KnownItemMatch.NearMiss(written.Pattern(), sameKind.Select(k => k.Benefit.Pattern())) is { } pattern)
        {
            var line = sameKind.First(k => k.Benefit.Pattern().Trim() == pattern);
            return new(BenefitStanding.NearMiss,
                Suggestion: line.Benefit.WithAmounts(written.Amounts).WordedLike(written).Describe(), Line: line);
        }
        return KnownItemMatch.NearMiss(sentence, compared) is { } near
            ? new(BenefitStanding.NearMiss,
                Suggestion: tierLines.First(b => b.Describe().Trim() == near).WordedLike(written).Describe())
            : BenefitCheck.NotKnown;
    }

    /// <summary>
    /// Whether <paramref name="written"/> is new to her benefit lines: not one of them at any amount,
    /// not a known tier's line, and not a near miss of either. Only then is saving it offered.
    /// </summary>
    public bool IsNewBenefit(Benefit written) => CheckBenefit(written).Standing == BenefitStanding.NotKnown;

    /// <summary>
    /// The known text of this kind that <paramref name="text"/> nearly matches, as the checks'
    /// "Did you mean" names it; null when it is known, or near none. Treatments and tiers only: a
    /// benefit line is a pattern, compared as a benefit (<see cref="CheckBenefit"/>).
    /// </summary>
    public string? NearMiss(KnownItemKind kind, string text) => KnownItemMatch.NearMiss(text, TextsOf(kind));

    /// <summary>
    /// Whether <paramref name="text"/> is new to her list of this kind: not known, and not a near miss
    /// of a known item. Only then is saving it offered, beside the checks' neutral "isn't one of your
    /// known ..." note; a near miss draws "Did you mean" instead, and saving it would keep the
    /// misspelling as known. Treatments and tiers only; for a benefit, <see cref="IsNewBenefit"/>.
    /// </summary>
    public bool IsNew(KnownItemKind kind, string text)
    {
        var clean = (text ?? "").Trim();
        var known = TextsOf(kind);
        return clean.Length > 0 && !KnownItemMatch.IsKnown(clean, known) && KnownItemMatch.NearMiss(clean, known) is null
               && (kind != KnownItemKind.Tier || TierFor(clean) is null);
    }

    private IReadOnlyList<string> TextsOf(KnownItemKind kind) => kind switch
    {
        KnownItemKind.Treatment => Treatments.Select(t => t.Name).ToList(),
        KnownItemKind.Tier => Tiers.Select(t => t.Name).ToList(),
        KnownItemKind.Benefit => throw new ArgumentException(
            "A benefit line is a pattern; compare a benefit with CheckBenefit.", nameof(kind)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

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
            case KnownBenefit b:
                problems.AddRange(b.LimitProblems());
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
/// keeps a number from being taken for a typo of another: a different number is a decision, not a
/// slip. A benefit line's amounts never reach it (they are left out of its
/// <see cref="Benefit.Pattern"/> and held to the line's limits instead), so for benefit lines it
/// guards only numbers in the words, such as "30+ units"; for tier names and a known tier's lines,
/// compared word for word, it guards every number.</item>
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
    // Tier-name ladders match their words the same way (TierLadders).
    internal static bool SameWord(string a, string b)
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
