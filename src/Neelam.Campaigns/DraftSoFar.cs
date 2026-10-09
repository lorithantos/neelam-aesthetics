namespace Neelam.Campaigns;

/// <summary>
/// A draft that does not build yet, as far as it goes, so the editor can run the rule checks and
/// show the email while parts are still missing. Nothing is invented: a part not filled in is left
/// out of what the checks see, and marked in the preview the way <see cref="TemplatePreview"/> marks
/// the parts each campaign fills. Nothing here exports either: <see cref="Campaign"/> only feeds
/// <see cref="CampaignReview"/>, whose report the editor drops after taking its findings, and
/// <see cref="Preview"/> is for showing. The missing parts still block, through the draft's own build.
/// </summary>
internal static class DraftSoFar
{
    /// <summary>
    /// What is filled in, as a campaign for the rule checks: every block with a value, and an offer
    /// once anything in it is written. An offer keeps its tiers up to the first one not finished, so
    /// the checks number tiers as the form does and never compare a tier against a gap.
    /// </summary>
    public static Campaign Campaign(CampaignDraft draft) => new(
        draft.Subject.HasValue ? draft.Subject.Value : "",
        draft.Blocks.Select(Checked).OfType<Block>().ToList(),
        draft.Preheader.HasValue ? draft.Preheader.Value : null);

    /// <summary>
    /// The email's blocks as the export would show them, with each missing part as a placeholder
    /// naming it as the missing list does, e.g. "‹Offer › Terms link: not filled in yet›". Optional
    /// parts left empty are left out, as they would be sent.
    /// </summary>
    public static IReadOnlyList<EditorBlock> Preview(CampaignDraft draft) =>
        EditorExport.Render(draft.Blocks.SelectMany(Shown));

    private static Block? Checked(BlockDraft block) => block switch
    {
        OfferBlockDraft { Offer.IsUntouched: true } => null,
        OfferBlockDraft o => new OfferBlock(o.Label, OfferSoFar(o.Offer), o.Marker),
        _ => block.ToBlock(),
    };

    private static Offer OfferSoFar(OfferDraft offer) => new(
        Name: offer.Name.HasValue ? offer.Name.Value : "",
        Summary: offer.Summary.HasValue ? offer.Summary.Value : "",
        Tiers: offer.Tiers.TakeWhile(IsFinished).Select(t => t.ToTier()).ToList(),
        IsRecurring: offer.IsRecurring,
        TermsUrl: offer.TermsUrl.HasValue ? offer.TermsUrl.Value : null,
        TiersNote: offer.TiersNote.HasValue ? offer.TiersNote.Value : null);

    // Every value of the tier is there. A copied benefit not yet reviewed still counts: the checks
    // should see what the email would say, which is how they catch a tier copied and not updated.
    private static bool IsFinished(TierDraft tier) =>
        tier.Name.HasValue && tier.MonthlyPrice.HasValue && tier.Benefits.Count > 0 && tier.Benefits.All(b => b.HasValue);

    private static IEnumerable<Block> Shown(BlockDraft block)
    {
        if (block is OfferBlockDraft offer)
            return !offer.Required && offer.Offer.IsUntouched ? [] : OfferShown(offer);
        if (block.ToBlock() is { } filled) return [filled];
        return block.Required ? [new PlaceholderBlock(block.Label, block.Type, ToFill(block.Name))] : [];
    }

    // An offer is its name as a heading, then its text, as the export gives it.
    private static IEnumerable<Block> OfferShown(OfferBlockDraft block)
    {
        var (label, offer) = (block.Label, block.Offer);
        yield return offer.Name.HasValue
            ? new HeadingBlock(label, offer.Name.Value)
            : new PlaceholderBlock(label, BlockType.Offer, ToFill($"{label} › Name"));

        var text = new List<string> { offer.Summary.HasValue ? offer.Summary.Value : ToFill($"{label} › Summary") };
        if (offer.TiersNote.HasValue) text.Add(offer.TiersNote.Value);
        if (offer.Tiers.Count == 0) text.Add(ToFill($"{label} › Tiers"));
        for (var i = 0; i < offer.Tiers.Count; i++)
            text.Add(TierShown(offer.Tiers[i], $"{label} › Tier {i + 1}", block.Marker, offer.IsRecurring));
        if (offer.TermsUrl.HasValue) text.Add(EditorExport.TermsText(offer.TermsUrl.Value));
        else if (offer.IsRecurring) text.Add(ToFill($"{label} › Terms link"));
        yield return new ParagraphsBlock(label, text);
    }

    private static string TierShown(TierDraft tier, string where, string marker, bool recurring)
    {
        IReadOnlyList<string> items = tier.Benefits.Count == 0
            ? [ToFill($"{where} › Benefits")]
            : tier.Benefits.Select((b, i) => b.HasValue ? b.Value.Describe() : ToFill($"{where} › Benefit {i + 1}")).ToList();
        return EditorExport.TierText(
            tier.Name.HasValue ? tier.Name.Value : ToFill($"{where} › Name"),
            tier.MonthlyPrice.HasValue ? EditorExport.PriceText(tier.MonthlyPrice.Value, recurring) : ToFill($"{where} › Price"),
            items,
            marker);
    }

    private static string ToFill(string where) => TemplatePreview.Marked($"{where}: not filled in yet");
}
