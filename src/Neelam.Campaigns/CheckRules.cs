namespace Neelam.Campaigns;

/// <summary>
/// The rule ids the rule checks report (<see cref="CampaignReview"/>, and <see cref="TemplateAdvice"/>
/// with the same ids): what the block catalog's files are checked against at startup, so they can
/// neither describe a rule no check reports nor leave a reported rule undescribed. A test holds this
/// list to the ids written in <c>CampaignReview.cs</c>, both ways. The AI proofread's findings
/// ("ai-...") are not rule checks and are not here.
/// </summary>
public static class CheckRules
{
    public static IReadOnlyList<string> Reported { get; } =
    [
        "photo-described",
        "known-item",
        "phone-registered",
        "cta-required",
        "cta-https",
        "tier-names-unique",
        "tier-names-numbered",
        "tier-rung-repeated",
        "tier-rung-order",
        "tier-content-distinct",
        "tier-price-positive",
        "tier-prices-increase",
        "benefit-value",
        "tiers-parallel",
        "terms-required",
        "medical-disclaimer",
        "restricted-term",
        "repeated-phrase",
        "emoji-spacing",
        "emoji-budget",
    ];
}
