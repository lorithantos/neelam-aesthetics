namespace Neelam.Campaigns;

/// <summary>
/// The Known items page shows one kind at a time, chosen from a row of tabs (owner, 2026-11-03: the
/// one long page was "far too long and finding things is not sensible on it"; "Selection is much
/// easier than scrolling down a long page finding what selections exist"). Each tab is a link to the
/// page's own address naming it in <c>?show=</c>, so back, refresh and a link all keep it.
/// </summary>
public static class KnownItemsTab
{
    public const string Treatments = "treatments";
    public const string BenefitLines = "benefit-lines";
    public const string Tiers = "tiers";
    public const string Ladders = "ladders";

    /// <summary>The tabs in the page's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Treatments, BenefitLines, Tiers, Ladders];

    /// <summary>The page open on <paramref name="tab"/>, relative to the site's base as its other links are.</summary>
    public static string Link(string tab) => $"known-items?show={tab}";

    /// <summary>
    /// The tab a request opens: the one <paramref name="show"/> names; else Benefit lines when the
    /// request names a limit box to start in (<paramref name="cap"/>, as a finding's link written
    /// before the tabs does); else Treatments, the first.
    /// </summary>
    public static string Chosen(string? show, string? cap) =>
        All.FirstOrDefault(tab => string.Equals(tab, show, StringComparison.OrdinalIgnoreCase))
        ?? (cap is not null ? BenefitLines : Treatments);
}
