namespace Neelam.Campaigns;

/// <summary>
/// What the checks would say about every campaign a template makes, told while the template is
/// built rather than at each campaign's review. Derived only from <see cref="CampaignReview"/>:
/// the template's fixed content is run through the same checks a campaign gets, so the advice
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

        // The one check that is about structure rather than text: an offer needs a button. A
        // template decides that for every campaign it makes, so it is said here.
        if (template.FirstOrDefault(b => b.Type == BlockType.Offer) is { } offer)
        {
            var buttons = template.Where(b => b.Type == BlockType.Button).ToList();
            if (buttons.Count == 0)
                advice.Add(new(Severity.Blocker, "cta-required", offer.Label,
                    "Every campaign from this template will be blocked: it has an offer and no button block " +
                    "to act on it. Add a button block."));
            else if (buttons.All(b => !b.Required && b.Fixed is null))
                advice.Add(new(Severity.Warning, "cta-required", offer.Label,
                    "A campaign that leaves the button out will be blocked, since it has an offer: consider " +
                    "making the button required."));
        }

        // Fixed content goes out in every campaign, so whatever the checks find in it, every
        // campaign will have.
        var fixedContent = template.Where(b => b.Fixed is not null).Select(b => b.Fixed!).ToList();
        var hasFinePrint = template.Any(b => b.Type == BlockType.FinePrint);
        // Only the library is given: the registration (phone numbers, known items) is not, as before.
        var library = libraryPhotos is null ? null : new BusinessContext("") { LibraryPhotos = libraryPhotos };
        foreach (var finding in CampaignReview.Check(new Campaign("", fixedContent), policy, library).Findings)
        {
            // A campaign can still add the disclaimer when the template has a fine-print block to hold it.
            if (finding.Rule == "medical-disclaimer" && hasFinePrint) continue;
            advice.Add(finding with
            {
                Message = $"In the template's fixed content, so every campaign from it will have this: {finding.Message}",
            });
        }
        return advice;
    }
}
