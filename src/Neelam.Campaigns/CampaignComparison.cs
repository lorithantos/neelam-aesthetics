namespace Neelam.Campaigns;

/// <summary>
/// What changed between two drafts of a campaign, block by block (owner, 2026-10-31, answer 7a/b/c:
/// "Compare against version"): two saves, the form against its last save, or the campaign against a
/// newer template, all through this one compare. Pure: it reads the two drafts and nothing else.
/// </summary>
/// <remarks>
/// Blocks are paired by the rule a template update pours her content by (label, then type:
/// <see cref="TemplateUpdate.Match"/>), so a block the template renamed is the same block, renamed. A
/// block is moved when it changed place among the others, not when one was added or taken out ahead of
/// it. Fields are compared as the email says them: a price in dollars, a date in words, a benefit as its
/// sentence; an offer's tiers paired by name, then by place, and a promotion's lines by place. Where a
/// value came from (typed, copied, the template's) is not a change; only what it says is, and a field
/// holding the template's fixed text is marked so.
/// </remarks>
/// <param name="Email">The email's own fields that changed: subject line, preheader, and her label.</param>
/// <param name="Blocks">Every block added, removed, moved, renamed or changed, in the newer draft's order, a removed one where it was.</param>
public sealed record CampaignComparison(IReadOnlyList<FieldChange> Email, IReadOnlyList<BlockChange> Blocks)
{
    /// <summary>True when anything at all changed.</summary>
    public bool HasChanges => Email.Count > 0 || Blocks.Count > 0;

    /// <summary>What changed from <paramref name="before"/> to <paramref name="after"/>; neither is changed.</summary>
    public static CampaignComparison Of(CampaignDraft before, CampaignDraft after)
    {
        var email = Diff(
            [new("Subject line", Value(before.Subject)), new("Preheader", Value(before.Preheader)), new("Label", Blank(before.Label))],
            [new("Subject line", Value(after.Subject)), new("Preheader", Value(after.Preheader)), new("Label", Blank(after.Label))]);

        var matched = TemplateUpdate.Match(before.Blocks, after.Blocks.Select(b => (b.Label, b.Type)).ToList());
        var from = Places(before.Blocks);
        var to = Places(after.Blocks);
        var inOrder = InOrder(Enumerable.Range(0, matched.Length)
            .Where(i => matched[i] is { Type: not BlockType.Spacer })
            .Select(i => (After: after.Blocks[i], Was: from[matched[i]!]!.Value)).ToList());

        var older = before.Blocks.ToList();
        // Each change listed, with where its block stood in the older draft (-1 for one added).
        var changes = new List<(BlockChange Change, int Was)>();
        for (var i = 0; i < matched.Length; i++)
        {
            var now = after.Blocks[i];
            var change = matched[i] is not { } was
                ? new BlockChange(now.Label, now.Type, BlockChangeKind.Added, null, to[now], false, null, Fields(null, now))
                : new BlockChange(now.Label, now.Type, BlockChangeKind.Kept, from[was], to[now],
                    now.Type != BlockType.Spacer && !inOrder.Contains(now),
                    was.Label == now.Label ? null : was.Label, Fields(was, now));
            if (change.Kind != BlockChangeKind.Kept || change.Moved || change.WasLabel is not null || change.Fields.Count > 0)
                changes.Add((change, matched[i] is { } w ? older.IndexOf(w) : -1));
        }

        // A block taken out goes where it was: after the last change listed whose block came before it.
        for (var i = 0; i < older.Count; i++)
        {
            var gone = older[i];
            if (matched.Contains(gone)) continue;
            var place = changes.FindLastIndex(c => c.Was >= 0 && c.Was < i) + 1;
            changes.Insert(place, (new BlockChange(gone.Label, gone.Type, BlockChangeKind.Removed, from[gone], null, false, null, Fields(gone, null)), i));
        }

        return new CampaignComparison(email, changes.Select(c => c.Change).ToList());
    }

    // Each block's place among those she sees as cards, counting from 1: a spacer has no card, and no place.
    private static Dictionary<BlockDraft, int?> Places(IReadOnlyList<BlockDraft> blocks)
    {
        var places = new Dictionary<BlockDraft, int?>(ReferenceEqualityComparer.Instance);
        var n = 0;
        foreach (var b in blocks) places[b] = b.Type == BlockType.Spacer ? null : ++n;
        return places;
    }

    // The blocks that kept their order among themselves: the longest run, in the newer order, of places
    // in the older one that only rise. Every block kept but not in it has moved.
    private static HashSet<BlockDraft> InOrder(IReadOnlyList<(BlockDraft After, int Was)> kept)
    {
        var length = new int[kept.Count];
        var previous = new int[kept.Count];
        var best = -1;
        for (var i = 0; i < kept.Count; i++)
        {
            (length[i], previous[i]) = (1, -1);
            for (var j = 0; j < i; j++)
                if (kept[j].Was < kept[i].Was && length[j] + 1 > length[i])
                    (length[i], previous[i]) = (length[j] + 1, j);
            if (best < 0 || length[i] > length[best]) best = i;
        }
        var inOrder = new HashSet<BlockDraft>(ReferenceEqualityComparer.Instance);
        for (var i = best; i >= 0; i = previous[i]) inOrder.Add(kept[i].After);
        return inOrder;
    }

    // One named value of a block, as the email says it, and whether it is the template's fixed text.
    private sealed record Named(string Field, string? Value, bool Fixed = false);

    private static IReadOnlyList<FieldChange> Fields(BlockDraft? before, BlockDraft? after)
    {
        var changes = Diff(Flat(before), Flat(after)).ToList();
        if (before is OfferBlockDraft || after is OfferBlockDraft)
            changes.AddRange(Tiers((before as OfferBlockDraft)?.Offer, (after as OfferBlockDraft)?.Offer));
        return changes;
    }

    // Values compared by field name, in the newer draft's order, then any the older alone had.
    private static List<FieldChange> Diff(IReadOnlyList<Named> before, IReadOnlyList<Named> after)
    {
        var changes = new List<FieldChange>();
        foreach (var name in after.Select(n => n.Field).Concat(before.Select(n => n.Field)).Distinct())
        {
            var was = before.FirstOrDefault(n => n.Field == name);
            var now = after.FirstOrDefault(n => n.Field == name);
            if (was?.Value != now?.Value)
                changes.Add(new FieldChange(name, was?.Value, now?.Value, was?.Fixed == true || now?.Fixed == true));
        }
        return changes;
    }

    // A block's values, every one filled, apart from an offer's tiers (Tiers).
    private static IReadOnlyList<Named> Flat(BlockDraft? block) => block switch
    {
        null => [],
        ValueBlockDraft<string> v => Of("Text", v.Value, s => s),
        ValueBlockDraft<IReadOnlyList<string>> v => Of("Paragraphs", v.Value, p => string.Join("\n\n", p)),
        ValueBlockDraft<CallToAction> v => [.. Of("Button text", v.Value, c => c.Label), .. Of("Link", v.Value, c => c.Url.ToString())],
        ValueBlockDraft<SignOff> v =>
            [.. Of("Valediction", v.Value, s => s.Valediction), .. Of("From", v.Value, s => s.From), .. Of("Tagline", v.Value, s => s.Tagline)],
        ValueBlockDraft<HeaderContent> v =>
            [.. Of("Business name", v.Value, h => h.Text), .. Of("Photo", v.Value, h => h.Photo?.Name), .. Of("Photo description", v.Value, h => h.Photo?.AltText)],
        ValueBlockDraft<ImageRef> v => [.. Of("Photo", v.Value, i => i.Name), .. Of("Photo description", v.Value, i => i.AltText)],
        OfferBlockDraft o =>
        [
            new("Offer name", Value(o.Offer.Name)), new("Summary", Value(o.Offer.Summary)),
            new("Tiers note", Value(o.Offer.TiersNote)), new("Terms link", Value(o.Offer.TermsUrl, u => u.ToString())),
        ],
        PromotionBlockDraft p => Promotion(p.Promotion),
        _ => [],
    };

    private static List<Named> Of<T>(string field, Slot<T> slot, Func<T, string?> said) =>
        [new(field, slot.HasValue ? Blank(said(slot.Value)) : null, slot.Origin == Origin.Template)];

    private static IReadOnlyList<Named> Promotion(PromotionDraft p)
    {
        var values = new List<Named>
        {
            new("Promotion name", Value(p.Name)), new("Summary", Value(p.Summary)), new("Who it is for", Value(p.Audience)),
            new("Starts", Value(p.Starts, Day)), new("Ends", Value(p.Ends, Day)),
        };
        for (var i = 0; i < p.Lines.Count; i++)
        {
            var line = $"Line {i + 1}";
            switch (p.Lines[i])
            {
                case PricedTreatmentDraft t:
                    values.Add(new($"{line} › Treatment", Value(t.Treatment)));
                    values.Add(new($"{line} › Promotion price", Value(t.Price, Dollars.Text)));
                    values.Add(new($"{line} › Regular price", Value(t.RegularPrice, Dollars.Text)));
                    break;
                case BenefitLineDraft b:
                    values.Add(new($"{line} › Benefit", Value(b.Benefit, x => x.Describe())));
                    break;
            }
        }
        return values;
    }

    // An offer's tiers: paired by name (ignoring case and surrounding spaces) where each side has the name
    // once, then the rest by place. A pair's name, price and benefits are compared as fields of the tier
    // where it is now; a tier only one side has is said whole; and a new order of the paired tiers is said.
    private static IEnumerable<FieldChange> Tiers(OfferDraft? before, OfferDraft? after)
    {
        var was = before?.Tiers ?? [];
        var now = after?.Tiers ?? [];
        var pair = new TierDraft?[now.Count];
        var left = was.ToList();
        for (var j = 0; j < now.Count; j++)
        {
            if (Key(now[j]) is not { } key || now.Count(t => Key(t) == key) != 1 || left.Count(t => Key(t) == key) != 1) continue;
            pair[j] = left.Single(t => Key(t) == key);
            left.Remove(pair[j]!);
        }
        for (var j = 0; j < now.Count && left.Count > 0; j++)
        {
            if (pair[j] is not null) continue;
            pair[j] = left[0];
            left.RemoveAt(0);
        }

        var recurring = after?.IsRecurring ?? before!.IsRecurring;
        var changes = new List<FieldChange>();
        var paired = Enumerable.Range(0, now.Count).Where(j => pair[j] is not null).ToList();
        if (paired.Select(j => was.ToList().IndexOf(pair[j]!)).Zip(paired.Skip(1).Select(j => was.ToList().IndexOf(pair[j]!))).Any(p => p.First > p.Second))
            changes.Add(new FieldChange("Tier order",
                string.Join(", ", was.Select(t => Value(t.Name) ?? "‹no name›")), string.Join(", ", now.Select(t => Value(t.Name) ?? "‹no name›"))));
        for (var j = 0; j < now.Count; j++)
        {
            if (pair[j] is { } p)
                changes.AddRange(Diff(TierFields(p, j, recurring), TierFields(now[j], j, recurring)));
            else
                changes.Add(new FieldChange($"Tier {j + 1}", null, Whole(now[j], recurring)));
        }
        foreach (var gone in was.Where(t => !pair.Contains(t)))
            changes.Add(new FieldChange("Removed tier", Whole(gone, recurring), null));
        return changes;

        static string? Key(TierDraft t) => Value(t.Name)?.Trim().ToUpperInvariant();
    }

    private static IReadOnlyList<Named> TierFields(TierDraft tier, int place, bool recurring) =>
    [
        new($"Tier {place + 1} › Name", Value(tier.Name)),
        new($"Tier {place + 1} › Price", Value(tier.MonthlyPrice, p => EditorExport.PriceText(p, recurring))),
        new($"Tier {place + 1} › Benefits", Blank(string.Join("\n", tier.Benefits.Select(BenefitText)))),
    ];

    // A tier said whole, a line each: its name, its price, then its benefits.
    private static string Whole(TierDraft tier, bool recurring) =>
        string.Join("\n", new[]
        {
            Value(tier.Name) ?? NotFilled,
            Value(tier.MonthlyPrice, p => EditorExport.PriceText(p, recurring)) ?? NotFilled,
        }.Concat(tier.Benefits.Select(BenefitText)));

    private static string BenefitText(Slot<Benefit> b) => Value(b, x => x.Describe()) ?? NotFilled;

    // A part left empty inside a list, said as the preview marks one.
    private const string NotFilled = "‹not filled in yet›";

    private static string? Value(Slot<string> slot) => slot.HasValue ? Blank(slot.Value) : null;

    private static string? Value<T>(Slot<T> slot, Func<T, string> said) => slot.HasValue ? said(slot.Value) : null;

    private static string? Blank(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // "December 31, 2026", as the checks say a promotion's dates.
    private static string Day(DateOnly day) => day.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What happened to a block between the two drafts.</summary>
public enum BlockChangeKind
{
    /// <summary>The newer draft has it and the older did not.</summary>
    Added,

    /// <summary>The older draft had it and the newer does not.</summary>
    Removed,

    /// <summary>Both have it: moved, renamed or changed, or all three.</summary>
    Kept,
}

/// <summary>One block that is not as it was.</summary>
/// <param name="Label">Its label in the newer draft, or the older one's for a block removed.</param>
/// <param name="From">Its place in the older draft among the blocks she sees, from 1; null when added, and for a spacer.</param>
/// <param name="To">Its place in the newer draft, the same way; null when removed, and for a spacer.</param>
/// <param name="Moved">It changed place among the other blocks.</param>
/// <param name="WasLabel">Its label in the older draft, when the template renamed it; otherwise null.</param>
/// <param name="Fields">
/// What changed in it: for a block added, everything it holds (nothing before); for one removed, everything
/// it held (nothing after).
/// </param>
public sealed record BlockChange(
    string Label, BlockType Type, BlockChangeKind Kind, int? From, int? To, bool Moved, string? WasLabel, IReadOnlyList<FieldChange> Fields);

/// <summary>One field that is not as it was, as the email says it.</summary>
/// <param name="Field">What it is, such as "Text", "Tier 1 › Price" or "Subject line".</param>
/// <param name="Before">What it said; null when it was empty or not there.</param>
/// <param name="After">What it says now; null when it is empty or not there.</param>
/// <param name="Fixed">One side or both is the template's fixed text, not hers.</param>
public sealed record FieldChange(string Field, string? Before, string? After, bool Fixed = false)
{
    /// <summary>The words that went and came, picked out where that is cheap and clear.</summary>
    public WordDiff Words => WordDiff.Of(Before, After);
}
