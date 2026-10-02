using System.Text;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>
/// The safety gate. Run before export; <see cref="ReviewReport.CanExport"/> is false while any
/// blocker remains. Each rule exists because of a real failure in a sent email — see README.
/// </summary>
public static class CampaignReview
{
    public static ReviewReport Check(Campaign campaign, CampaignPolicy? policy = null)
    {
        policy ??= CampaignPolicy.Default;
        var text = CampaignText.Fragments(campaign);
        var findings = new List<Finding>();

        findings.AddRange(CallToActionPresent(campaign));
        if (campaign.Offer is { } offer)
        {
            findings.AddRange(TierNamesUnique(offer));
            findings.AddRange(TierPricesIncrease(offer));
            findings.AddRange(BenefitValuesValid(offer));
            findings.AddRange(TiersParallel(offer));
            findings.AddRange(TermsForRecurring(offer));
        }
        findings.AddRange(MedicalDisclaimer(campaign, text, policy));
        findings.AddRange(RestrictedTerms(text, policy));
        findings.AddRange(RepeatedPhrases(text, policy));
        findings.AddRange(EmojiSpacing(text));
        findings.AddRange(EmojiBudget(text, policy));

        return new ReviewReport(findings);
    }

    private static IEnumerable<Finding> CallToActionPresent(Campaign c)
    {
        if (c.CallToAction is null)
        {
            yield return new(Severity.Blocker, "cta-required", "Call to action",
                "The email has no way to act on it: add a button with a link (book, join, reply).");
        }
        else if (!c.CallToAction.Url.IsAbsoluteUri || c.CallToAction.Url.Scheme != Uri.UriSchemeHttps)
        {
            yield return new(Severity.Blocker, "cta-https", "Call to action",
                $"The link must be an absolute https:// address, not '{c.CallToAction.Url}'.");
        }
    }

    private static IEnumerable<Finding> TierNamesUnique(Offer offer) =>
        offer.Tiers
            .Select((t, i) => (Key: t.Name.Trim().ToLowerInvariant(), Index: i + 1))
            .GroupBy(x => x.Key)
            .Where(g => g.Count() > 1)
            .Select(g => new Finding(Severity.Blocker, "tier-names-unique", "Offer",
                $"Tiers {string.Join(" and ", g.Select(x => x.Index))} share the name " +
                $"'{offer.Tiers[g.First().Index - 1].Name}'; customers cannot tell them apart."));

    private static IEnumerable<Finding> TierPricesIncrease(Offer offer)
    {
        for (var i = 0; i < offer.Tiers.Count; i++)
        {
            var tier = offer.Tiers[i];
            if (tier.MonthlyPrice <= 0)
                yield return new(Severity.Blocker, "tier-price-positive", $"Offer › Tier {i + 1}",
                    $"'{tier.Name}' has no price.");
            if (i > 0 && tier.MonthlyPrice <= offer.Tiers[i - 1].MonthlyPrice)
                yield return new(Severity.Blocker, "tier-prices-increase", $"Offer › Tier {i + 1}",
                    $"'{tier.Name}' costs no more than the tier before it; list tiers cheapest first.");
        }
    }

    private static IEnumerable<Finding> BenefitValuesValid(Offer offer)
    {
        for (var i = 0; i < offer.Tiers.Count; i++)
        {
            var where = $"Offer › Tier {i + 1}";
            foreach (var b in offer.Tiers[i].Benefits)
            {
                var problem = b switch
                {
                    PercentOff { Percent: < 1 or > 99 } p =>
                        $"{p.Percent}% off is not a discount; use 1–99.",
                    DiscountedItem { Percent: >= 100 } d =>
                        $"{d.Percent}% off {d.ItemName} is free: use a complimentary item instead.",
                    DiscountedItem { Percent: < 1 } d =>
                        $"{d.Percent}% off {d.ItemName} is not a discount.",
                    FreeItem { Quantity: < 1 } f =>
                        $"{f.Quantity} complimentary {f.ItemName} is nothing; quantity must be at least 1.",
                    BirthdayCredit { Amount: <= 0 } =>
                        "A birthday credit needs an amount.",
                    _ => null,
                };
                if (problem is not null)
                    yield return new(Severity.Blocker, "benefit-value", where, problem);
            }
        }
    }

    /// <summary>
    /// Readers compare tiers line by line. A higher tier can be more generous, but if it offers a
    /// kind of benefit the lower tier lacks (or vice versa), the comparison has to be spelled out.
    /// </summary>
    private static IEnumerable<Finding> TiersParallel(Offer offer)
    {
        if (offer.Tiers.Count < 2) yield break;
        var baseline = offer.Tiers[0].Benefits.Select(b => b.Kind).ToHashSet();
        for (var i = 1; i < offer.Tiers.Count; i++)
        {
            var kinds = offer.Tiers[i].Benefits.Select(b => b.Kind).ToHashSet();
            var onlyHere = kinds.Except(baseline).ToList();
            var missing = baseline.Except(kinds).ToList();
            if (onlyHere.Count == 0 && missing.Count == 0) continue;

            var parts = new List<string>();
            if (onlyHere.Count > 0) parts.Add($"has {string.Join(", ", onlyHere)} that tier 1 lacks");
            if (missing.Count > 0) parts.Add($"lacks {string.Join(", ", missing)} that tier 1 has");
            yield return new(Severity.Warning, "tiers-parallel", $"Offer › Tier {i + 1}",
                $"'{offer.Tiers[i].Name}' {string.Join(" and ", parts)}; check the tiers read side by side.");
        }
    }

    private static IEnumerable<Finding> TermsForRecurring(Offer offer)
    {
        if (offer.IsRecurring && offer.TermsUrl is null)
            yield return new(Severity.Blocker, "terms-required", "Offer",
                "A recurring charge needs a terms link covering cancellation, rollover, expiry and refunds.");
    }

    private static IEnumerable<Finding> MedicalDisclaimer(
        Campaign c, IReadOnlyList<TextFragment> text, CampaignPolicy policy)
    {
        if (!string.IsNullOrWhiteSpace(c.Disclaimer)) yield break;
        var hit = text
            .SelectMany(f => policy.MedicalTerms
                .Where(term => WordPrefix(term).IsMatch(f.Text))
                .Select(term => (f.Location, term)))
            .FirstOrDefault();
        if (hit != default)
            yield return new(Severity.Blocker, "medical-disclaimer", hit.Location,
                $"Promotes a medical service ('{hit.term}') without a disclaimer.");
    }

    private static IEnumerable<Finding> RestrictedTerms(IReadOnlyList<TextFragment> text, CampaignPolicy policy)
    {
        foreach (var (term, reason) in policy.RestrictedTerms)
        {
            var pattern = new Regex($@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase);
            var where = text.Where(f => pattern.IsMatch(f.Text)).Select(f => f.Location).ToList();
            if (where.Count > 0)
                yield return new(Severity.Warning, "restricted-term", where[0],
                    $"'{term}' appears in {where.Count} place(s) — {reason} Needs sign-off before sending.");
        }
    }

    /// <summary>The same run of words in two different places is almost always a paste error.</summary>
    private static IEnumerable<Finding> RepeatedPhrases(IReadOnlyList<TextFragment> text, CampaignPolicy policy)
    {
        var n = policy.RepeatedPhraseWords;
        var firstSeen = new Dictionary<string, string>();
        var reported = new HashSet<string>();
        foreach (var fragment in text)
        {
            var words = Words(fragment.Text);
            for (var i = 0; i + n <= words.Count; i++)
            {
                var shingle = string.Join(' ', words.Skip(i).Take(n));
                if (!firstSeen.TryAdd(shingle, fragment.Location)
                    && firstSeen[shingle] != fragment.Location
                    && reported.Add($"{firstSeen[shingle]}|{fragment.Location}"))
                {
                    yield return new(Severity.Warning, "repeated-phrase", fragment.Location,
                        $"Repeats \"{shingle}…\" from {firstSeen[shingle]}.");
                }
            }
        }
    }

    private static IEnumerable<Finding> EmojiSpacing(IReadOnlyList<TextFragment> text)
    {
        foreach (var f in text)
        {
            var runes = f.Text.EnumerateRunes().Where(r => !IsJoiner(r)).ToList();
            for (var i = 0; i < runes.Count; i++)
            {
                if (!IsEmoji(runes[i])) continue;
                var touchesBefore = i > 0 && Rune.IsLetterOrDigit(runes[i - 1]);
                var touchesAfter = i + 1 < runes.Count && Rune.IsLetterOrDigit(runes[i + 1]);
                if (touchesBefore || touchesAfter)
                {
                    yield return new(Severity.Warning, "emoji-spacing", f.Location,
                        $"An emoji touches a word in \"{f.Text}\"; add a space.");
                    break;
                }
            }
        }
    }

    private static IEnumerable<Finding> EmojiBudget(IReadOnlyList<TextFragment> text, CampaignPolicy policy)
    {
        var count = text.Sum(f => f.Text.EnumerateRunes().Count(IsEmoji));
        if (count > policy.MaxEmoji)
            yield return new(Severity.Warning, "emoji-budget", "Whole email",
                $"{count} emoji (limit {policy.MaxEmoji}); heavy emoji use reads as spam to filters and people.");
    }

    // A term matches at the start of a word, so "injection" also catches "injections".
    private static Regex WordPrefix(string term) =>
        new($@"\b{Regex.Escape(term)}", RegexOptions.IgnoreCase);

    private static List<string> Words(string s)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(r) || r.Value is '%' or '\'' or '’')
            {
                current.Append(Rune.ToLowerInvariant(r).ToString());
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    internal static bool IsEmoji(Rune r) =>
        r.Value is >= 0x1F000 and <= 0x1FAFF   // pictographs, emoticons, transport, supplemental
            or >= 0x2600 and <= 0x27BF         // misc symbols, dingbats (✨ ✔)
            or >= 0x2B00 and <= 0x2BFF;        // arrows, stars

    private static bool IsJoiner(Rune r) => r.Value is 0xFE0F or 0x200D;
}
