namespace Neelam.Campaigns;

/// <summary>
/// A campaign brought up to a version of a template (owner, 2026-10-31): to the newest version of the
/// one it was made from, when that has changed, or to another template she picks, so a campaign started
/// on the wrong one can be finished on the right one. Worked out here and applied by the page only on
/// her say; nothing is saved.
/// </summary>
/// <remarks>
/// The blocks are the template's, in its order, each with its label, type and whether it is required
/// (and for an offer, its marker and whether it recurs). Hers are matched to them first by label,
/// ignoring case, and type alike; then, of those left, the one block of a type left on each side,
/// whatever its label (a block renamed in the template). A template block matched to nothing arrives as
/// a new campaign would have it: empty, or with its fixed text. A matched block keeps what she wrote,
/// except that a block the template fixes takes the template's text, which is how a typo fixed in the
/// template reaches the campaign; a block that was fixed and no longer is keeps its text, now hers to
/// edit. Her label, subject and preheader stay as they are.
/// </remarks>
/// <param name="Draft">The campaign as it would be, with the template's current name, id and version.</param>
/// <param name="Dropped">Her blocks, by her label, with something in them that match no block of the template, so would go.</param>
/// <param name="Replaced">Her blocks, by her label, whose own text the template's fixed text would take the place of.</param>
public sealed record TemplateUpdate(CampaignDraft Draft, IReadOnlyList<string> Dropped, IReadOnlyList<string> Replaced)
{
    /// <summary>True when something she wrote would be lost, so she is asked first.</summary>
    public bool LosesAnything => Dropped.Count > 0 || Replaced.Count > 0;

    /// <summary>Works out <paramref name="draft"/> brought up to <paramref name="template"/>; <paramref name="draft"/> is left as it is.</summary>
    /// <param name="templateId">The template's id, recorded on the result.</param>
    /// <param name="version">When this version of the template was saved, recorded on the result.</param>
    public static TemplateUpdate Of(CampaignDraft draft, CampaignTemplate template, Guid templateId, DateTimeOffset version)
    {
        var left = draft.Blocks.ToList();
        var matched = new BlockDraft?[template.Blocks.Count];

        for (var i = 0; i < matched.Length; i++)
        {
            var t = template.Blocks[i];
            matched[i] = left.FirstOrDefault(b => b.Type == t.Type && SameLabel(b.Label, t.Label));
            if (matched[i] is { } hers) left.Remove(hers);
        }

        foreach (var type in template.Blocks.Select(t => t.Type).Distinct())
        {
            var unmatched = Enumerable.Range(0, matched.Length)
                .Where(i => matched[i] is null && template.Blocks[i].Type == type).ToList();
            var hers = left.Where(b => b.Type == type).ToList();
            if (unmatched.Count != 1 || hers.Count != 1) continue;
            matched[unmatched[0]] = hers[0];
            left.Remove(hers[0]);
        }

        var replaced = new List<string>();
        var blocks = new List<BlockDraft>();
        for (var i = 0; i < matched.Length; i++)
        {
            var block = BlockDraft.From(template.Blocks[i]);
            if (matched[i] is { } hers && Take(hers, block)) replaced.Add(hers.Label);
            blocks.Add(block);
        }

        var result = new CampaignDraft(template.Name, blocks)
        {
            Label = draft.Label, TemplateId = templateId, TemplateVersion = version,
        };
        Copy(draft.Subject, result.Subject);
        Copy(draft.Preheader, result.Preheader);
        return new TemplateUpdate(result, left.Where(HasContent).Select(b => b.Label).ToList(), replaced);
    }

    private static bool SameLabel(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool HasContent(BlockDraft block) => block switch
    {
        ValueBlockDraft<string> v => v.Value.HasValue,
        ValueBlockDraft<IReadOnlyList<string>> v => v.Value.HasValue,
        ValueBlockDraft<CallToAction> v => v.Value.HasValue,
        ValueBlockDraft<SignOff> v => v.Value.HasValue,
        ValueBlockDraft<HeaderContent> v => v.Value.HasValue,
        ValueBlockDraft<ImageRef> v => v.Value.HasValue,
        OfferBlockDraft o => !o.Offer.IsUntouched,
        _ => false,
    };

    // Her block's content into the template's block of the same type; true when the template's fixed
    // text takes the place of text of her own.
    private static bool Take(BlockDraft hers, BlockDraft into) => (hers, into) switch
    {
        (ValueBlockDraft<string> h, ValueBlockDraft<string> n) => Take(h.Value, n.Value),
        (ValueBlockDraft<IReadOnlyList<string>> h, ValueBlockDraft<IReadOnlyList<string>> n) => Take(h.Value, n.Value),
        (ValueBlockDraft<CallToAction> h, ValueBlockDraft<CallToAction> n) => Take(h.Value, n.Value),
        (ValueBlockDraft<SignOff> h, ValueBlockDraft<SignOff> n) => Take(h.Value, n.Value),
        (ValueBlockDraft<HeaderContent> h, ValueBlockDraft<HeaderContent> n) => Take(h.Value, n.Value),
        (ValueBlockDraft<ImageRef> h, ValueBlockDraft<ImageRef> n) => Take(h.Value, n.Value),
        (OfferBlockDraft h, OfferBlockDraft n) => Take(h.Offer, n.Offer),
        _ => false,
    };

    private static bool Take<T>(Slot<T> hers, Slot<T> into)
    {
        // The template fixes it: its text goes in. Hers is lost only when she wrote it and it differs.
        if (into.Origin == Origin.Template)
            return hers.Origin is Origin.Entered or Origin.Copied && !Same(hers.Value, into.Value);
        // Fixed before and left to each campaign now: the text stays, hers to edit.
        if (hers.Origin == Origin.Template) into.Restore(Origin.Entered, hers.Value, null);
        else Copy(hers, into);
        return false;
    }

    private static bool Take(OfferDraft hers, OfferDraft into)
    {
        Copy(hers.Name, into.Name);
        Copy(hers.Summary, into.Summary);
        Copy(hers.TiersNote, into.TiersNote);
        Copy(hers.TermsUrl, into.TermsUrl);
        foreach (var tier in hers.Tiers)
        {
            var copy = into.AddTier();
            Copy(tier.Name, copy.Name);
            Copy(tier.MonthlyPrice, copy.MonthlyPrice);
            foreach (var benefit in tier.Benefits) Copy(benefit, copy.AddEmptyBenefit());
        }
        // An offer is never fixed, so nothing of hers is replaced.
        return false;
    }

    // A value exactly as it stands, origin included, so a copy nobody reviewed is still unreviewed.
    private static void Copy<T>(Slot<T> from, Slot<T> to)
    {
        if (from.HasValue) to.Restore(from.Origin, from.Value, from.CopiedFrom);
        else to.Clear();
    }

    private static bool Same<T>(T a, T b) =>
        a is IReadOnlyList<string> x && b is IReadOnlyList<string> y ? x.SequenceEqual(y) : EqualityComparer<T>.Default.Equals(a, b);
}
