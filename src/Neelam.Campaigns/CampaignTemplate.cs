namespace Neelam.Campaigns;

/// <summary>
/// A reusable starting point for a kind of campaign, e.g. "membership announcement". It supplies
/// the parts that stay the same from send to send (greeting, closing, sign-off, disclaimer) and
/// leaves every part that must be decided per campaign empty — never pre-filled with last
/// time's text, which is how stale copy goes out.
/// </summary>
/// <param name="Name">Shown when choosing a template.</param>
/// <param name="HasOffer">Whether campaigns from this template announce a tiered offer.</param>
/// <param name="IsRecurring">For an offer: whether it is a recurring charge.</param>
public sealed record CampaignTemplate(
    string Name,
    string Greeting,
    IReadOnlyList<string> Closing,
    SignOff SignOff,
    string? Disclaimer,
    bool HasOffer,
    bool IsRecurring = false)
{
    public CampaignDraft Start() => new()
    {
        TemplateName = Name,
        Greeting = Slot<string>.FromTemplate(Greeting),
        Closing = Slot<IReadOnlyList<string>>.FromTemplate(Closing),
        SignOff = Slot<SignOff>.FromTemplate(SignOff),
        Disclaimer = Disclaimer is null ? Slot<string>.Empty() : Slot<string>.FromTemplate(Disclaimer),
        Offer = HasOffer ? new OfferDraft { IsRecurring = IsRecurring } : null,
    };
}
