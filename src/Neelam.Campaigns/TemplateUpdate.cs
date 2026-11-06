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
/// a new campaign would have it: empty, with its fixed text, or with its content to start with. A
/// matched block keeps what she wrote, except that a block the template fixes takes the template's
/// text, which is how a typo fixed in the template reaches the campaign; a block that was fixed and no
/// longer is keeps its text, now hers to edit. Her label, subject and preheader stay as they are.
/// <para>
/// A block the template gives content to start with (owner, 2026-11-03: "Updating should modify only
/// those that have the default and are not approved") takes the template's new content only where hers
/// still holds what the version she was made from started it with, unchanged (nothing there, when that
/// version gave it none; the template's own text, when that version fixed it), and the version on her
/// page carries no approval that stands. Otherwise what she has stays: she changed it, so it is hers,
/// and it is neither replaced nor reported. When the version she was made from is not known (none
/// recorded, or no longer in use), nothing tells her text from the template's, so hers stays.
/// </para>
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
    /// <param name="madeFrom">
    /// The version of a template <paramref name="draft"/> records (<see cref="CampaignDraft.TemplateVersion"/>),
    /// read; null when it records none or that version is no longer in use.
    /// </param>
    /// <param name="approved">The version on her page carries an approval that stands: content to start with is left as she has it.</param>
    public static TemplateUpdate Of(
        CampaignDraft draft, CampaignTemplate template, Guid templateId, DateTimeOffset version,
        CampaignTemplate? madeFrom = null, bool approved = false)
    {
        var matched = Match(draft.Blocks, template.Blocks.Select(t => (t.Label, t.Type)).ToList());
        var left = draft.Blocks.Where(b => !matched.Contains(b)).ToList();

        var replaced = new List<string>();
        var blocks = new List<BlockDraft>();
        for (var i = 0; i < matched.Length; i++)
        {
            var t = template.Blocks[i];
            BlockDraft block;
            if (matched[i] is not { } hers)
                block = BlockDraft.Start(t);
            else if (t.Default is not null && !approved && StartsAsMadeFrom(hers, madeFrom))
                block = BlockDraft.Start(t);
            else
            {
                block = BlockDraft.From(t);
                if (Take(hers, block)) replaced.Add(hers.Label);
            }
            blocks.Add(block);
        }

        var result = new CampaignDraft(template.Name, blocks)
        {
            Label = draft.Label, TemplateId = templateId, TemplateVersion = version,
        };
        Copy(draft.Subject, result.Subject);
        Copy(draft.Preheader, result.Preheader);
        return new TemplateUpdate(result, left.Where(b => b.HasContent).Select(b => b.Label).ToList(), replaced);
    }

    /// <summary>
    /// Which of her blocks goes into each place of <paramref name="into"/>, or null for a place matched to
    /// nothing: first by label, ignoring case and surrounding spaces, and type alike; then, of those left,
    /// the one block of a type left on each side, whatever its label; two of a type left on either side
    /// pair nothing. The one rule for pairing blocks: the update pours her content by it, and
    /// <see cref="CampaignComparison"/> pairs two drafts' blocks by it.
    /// </summary>
    internal static BlockDraft?[] Match(IReadOnlyList<BlockDraft> hers, IReadOnlyList<(string Label, BlockType Type)> into)
    {
        var left = hers.ToList();
        var matched = new BlockDraft?[into.Count];

        for (var i = 0; i < matched.Length; i++)
        {
            var (label, type) = into[i];
            matched[i] = left.FirstOrDefault(b => b.Type == type && SameLabel(b.Label, label));
            if (matched[i] is { } found) left.Remove(found);
        }

        foreach (var type in into.Select(t => t.Type).Distinct())
        {
            var unmatched = Enumerable.Range(0, matched.Length)
                .Where(i => matched[i] is null && into[i].Type == type).ToList();
            var ofType = left.Where(b => b.Type == type).ToList();
            if (unmatched.Count != 1 || ofType.Count != 1) continue;
            matched[unmatched[0]] = ofType[0];
            left.Remove(ofType[0]);
        }

        return matched;
    }

    private static bool SameLabel(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // Whether her block still holds what the version she was made from started it with, unchanged: its
    // content to start with, or nothing when it gave none, or its fixed text, which she could not change.
    // Not known, so false, without that version or a block of it by her block's label and type.
    private static bool StartsAsMadeFrom(BlockDraft hers, CampaignTemplate? madeFrom)
    {
        if (madeFrom?.Blocks.FirstOrDefault(b => b.Type == hers.Type && SameLabel(b.Label, hers.Label)) is not { } was)
            return false;
        if (was.Fixed is not null) return true;
        return was.Default is { } start ? start.IsHeldBy(hers) : !hers.HasContent;
    }

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
        (PromotionBlockDraft h, PromotionBlockDraft n) => Take(h.Promotion, n.Promotion),
        _ => false,
    };

    private static bool Take(PromotionDraft hers, PromotionDraft into)
    {
        Copy(hers.Name, into.Name);
        Copy(hers.Summary, into.Summary);
        Copy(hers.Audience, into.Audience);
        Copy(hers.Starts, into.Starts);
        Copy(hers.Ends, into.Ends);
        foreach (var line in hers.Lines)
        {
            switch (line)
            {
                case PricedTreatmentDraft t:
                    var priced = into.AddTreatment();
                    Copy(t.Treatment, priced.Treatment);
                    Copy(t.Price, priced.Price);
                    Copy(t.RegularPrice, priced.RegularPrice);
                    break;
                case BenefitLineDraft b:
                    Copy(b.Benefit, into.AddBenefit().Benefit);
                    break;
            }
        }
        // A promotion is never fixed, so nothing of hers is replaced.
        return false;
    }

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
