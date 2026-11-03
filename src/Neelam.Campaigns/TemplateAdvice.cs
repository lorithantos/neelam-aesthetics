namespace Neelam.Campaigns;

/// <summary>
/// What the checks would say about every campaign a template makes, told while the template is
/// built rather than at each campaign's review. Derived only from <see cref="CampaignReview"/>:
/// the template's fixed content, and the content it starts each campaign with, are run through
/// the same checks a campaign gets, so the advice
/// never invents a style rule of its own. A template that draws advice can still be saved; the
/// advice says what its campaigns will meet.
/// </summary>
public static class TemplateAdvice
{
    /// <param name="libraryPhotos">
    /// The names of the photos in the client's image library, so a fixed photo the library does not
    /// hold draws only the note its field already gives; null when the library was not read.
    /// </param>
    public static IReadOnlyList<Finding> For(
        IReadOnlyList<TemplateBlock> template, CampaignPolicy? policy = null, IReadOnlyCollection<string>? libraryPhotos = null)
    {
        var advice = new List<Finding>();

        // The one check that is about structure rather than text: an offer, or a promotion, needs a
        // button. A template decides that for every campaign it makes, so it is said here.
        if (template.FirstOrDefault(b => b.Type is BlockType.Offer or BlockType.Promotion) is { } offer)
        {
            var what = offer.Type == BlockType.Promotion ? "a promotion" : "an offer";
            var buttons = template.Where(b => b.Type == BlockType.Button).ToList();
            if (buttons.Count == 0)
                advice.Add(new(Severity.Blocker, "cta-required", offer.Label,
                    $"Every campaign from this template will be blocked: it has {what} and no button block " +
                    "to act on it. Add a button block."));
            else if (buttons.All(b => !b.Required && b.Fixed is null))
                advice.Add(new(Severity.Warning, "cta-required", offer.Label,
                    $"A campaign that leaves the button out will be blocked, since it has {what}: consider " +
                    "making the button required."));
        }

        // Fixed content goes out in every campaign, so whatever the checks find in it, every
        // campaign will have. Content to start with goes out in every campaign that keeps it (owner,
        // 2026-11-03), so it is checked with it, as far as it goes, and said apart.
        var content = template
            .Select(b => b.Fixed ?? (b.Default is not null ? DraftSoFar.Checked(BlockDraft.Start(b)) : null))
            .OfType<Block>().ToList();
        var labels = content.Select(b => b.Label).ToList();
        var hasFinePrint = template.Any(b => b.Type == BlockType.FinePrint);
        var anyStart = template.Any(b => b.Default is not null);
        // Only the library is given: the registration (phone numbers, known items) is not, as before.
        var library = libraryPhotos is null ? null : new BusinessContext("") { LibraryPhotos = libraryPhotos };
        foreach (var finding in CampaignReview.Check(new Campaign("", content), policy, library).Findings)
        {
            // A campaign can still add the disclaimer when the template has a fine-print block to hold it.
            if (finding.Rule == "medical-disclaimer" && hasFinePrint) continue;
            // Said above, from the template's blocks: an offer to start with may have its button filled in
            // by each campaign.
            if (finding.Rule == "cta-required") continue;
            var block = FindingPlace.BlockIndex(finding.Location, labels) is { } i ? content[i] : null;
            var fixedOne = block is not null ? template.Any(b => ReferenceEquals(b.Fixed, block)) : !anyStart;
            advice.Add(finding with
            {
                Message = fixedOne
                    ? $"In the template's fixed content, so every campaign from it will have this: {finding.Message}"
                    : $"In what each campaign starts with, so every campaign that keeps it will have this: {finding.Message}",
            });
        }
        return advice;
    }
}
