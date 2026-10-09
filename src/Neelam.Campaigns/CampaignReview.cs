using System.Text;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>
/// The rule checks: deterministic, instant, and not dismissable. They are half of the gate;
/// <see cref="CampaignGate"/> adds the AI proofread. Each rule exists because of a real failure
/// in a sent email — see README.
/// </summary>
public static class CampaignReview
{
    /// <param name="business">
    /// Who is sending it, from the clients table: the phone numbers it has registered are the only
    /// ones the email may carry. Unknown, or with no numbers registered, numbers are not checked.
    /// </param>
    public static ReviewReport Check(Campaign campaign, CampaignPolicy? policy = null, BusinessContext? business = null)
    {
        policy ??= CampaignPolicy.Default;
        var text = CampaignText.Fragments(campaign);
        var findings = new List<Finding>();

        // Each block type brings its own checks; an email only gets the checks its blocks need.
        findings.AddRange(OfferHasAButton(campaign));
        foreach (var button in campaign.BlocksOf<ButtonBlock>())
            findings.AddRange(ButtonLinkIsHttps(button));
        foreach (var block in campaign.BlocksOf<OfferBlock>())
        {
            var (label, offer) = (block.Label, block.Offer);
            findings.AddRange(TierNamesUnique(label, offer));
            findings.AddRange(TierContentDistinct(label, offer));
            findings.AddRange(TierPricesIncrease(label, offer));
            findings.AddRange(BenefitValuesValid(label, offer));
            findings.AddRange(TiersParallel(label, offer));
            findings.AddRange(TermsForRecurring(label, offer));
        }
        findings.AddRange(MedicalDisclaimer(campaign, text, policy));
        findings.AddRange(RestrictedTerms(text, policy));
        findings.AddRange(RepeatedPhrases(text, policy));
        findings.AddRange(EmojiSpacing(text));
        findings.AddRange(EmojiBudget(text, policy));
        findings.AddRange(RegisteredPhones(campaign, text, business));
        findings.AddRange(KnownItemNearMisses(campaign, business));

        return new ReviewReport(campaign, findings, Proofread: false);
    }

    /// <summary>
    /// A treatment name or benefit line that nearly matches one of the client's known items is a typo
    /// or a stray capital more often than a new thing: "Wellness Injecton" when "Wellness injection"
    /// is known. What counts as nearly is <see cref="KnownItemMatch"/>'s. A warning, never a block,
    /// naming the known item; with no known items, nothing is said.
    /// </summary>
    private static IEnumerable<Finding> KnownItemNearMisses(Campaign c, BusinessContext? business)
    {
        var known = business?.Known ?? KnownItems.None;
        if (known.IsEmpty) yield break;
        var treatments = known.Treatments.Select(t => t.Name).ToList();
        var lines = known.BenefitLines;

        foreach (var block in c.BlocksOf<OfferBlock>())
        for (var i = 0; i < block.Offer.Tiers.Count; i++)
        foreach (var benefit in block.Offer.Tiers[i].Benefits)
        {
            var where = $"{block.Label} › Tier {i + 1}";
            if (benefit.Item is { } item && KnownItemMatch.NearMiss(item, treatments) is { } treatment)
                yield return NearMiss(where, treatment, item);
            var sentence = benefit.Describe();
            if (KnownItemMatch.NearMiss(sentence, lines) is { } line)
                yield return NearMiss(where, line, sentence);
        }
    }

    private static Finding NearMiss(string where, string known, string written) =>
        new(Severity.Warning, "known-item", where, $"Did you mean '{known}'? It's in your known items.", Excerpt: written);

    /// <summary>
    /// A phone number the business has not registered is stale or mistyped more often than not:
    /// an email signed off with 425-877-8646 while Square had the business at (425) 773-5261. The
    /// numbers come from the client's registration, so before any are registered nothing is said.
    /// Links are read too, for a tel: button.
    /// </summary>
    private static IEnumerable<Finding> RegisteredPhones(
        Campaign c, IReadOnlyList<TextFragment> text, BusinessContext? business)
    {
        var registered = business?.Phones ?? PhoneNumbers.None;
        if (registered.Count == 0) yield break;

        var links = c.BlocksOf<ButtonBlock>().Select(b => new TextFragment($"{b.Label} › Link", b.Action.Url.OriginalString))
            .Concat(c.BlocksOf<OfferBlock>().Where(o => o.Offer.TermsUrl is not null)
                .Select(o => new TextFragment($"{o.Label} › Terms link", o.Offer.TermsUrl!.OriginalString)));
        var reported = new HashSet<(string, PhoneNumber)>();
        foreach (var fragment in text.Concat(links))
        foreach (var (written, number) in PhoneNumber.FindIn(fragment.Text))
        {
            if (registered.Contains(number) || !reported.Add((fragment.Location, number))) continue;
            yield return new(Severity.Warning, "phone-registered", fragment.Location,
                $"{written} isn't one of your registered numbers ({registered}). Check it before sending.",
                Excerpt: written);
        }
    }

    /// <summary>
    /// An offer with no button gives the reader no way to take it up: the Beauty Bank email went
    /// out that way. Emails without an offer decide in their template whether a button is required.
    /// </summary>
    private static IEnumerable<Finding> OfferHasAButton(Campaign c)
    {
        var offer = c.BlocksOf<OfferBlock>().FirstOrDefault();
        if (offer is not null && !c.BlocksOf<ButtonBlock>().Any())
            yield return new(Severity.Blocker, "cta-required", offer.Label,
                "The email has an offer and no way to act on it: add a button with a link (book, join, reply).");
    }

    private static IEnumerable<Finding> ButtonLinkIsHttps(ButtonBlock button)
    {
        var url = button.Action.Url;
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            yield return new(Severity.Blocker, "cta-https", button.Label,
                $"The link must be an absolute https:// address, not '{url}'.");
    }

    // Says nothing about which tier to rename: a copied tier keeps its name, and the one to change
    // may as well be the original.
    private static IEnumerable<Finding> TierNamesUnique(string label, Offer offer) =>
        offer.Tiers
            .Select((t, i) => (Key: t.Name.Trim().ToLowerInvariant(), Index: i + 1))
            .GroupBy(x => x.Key)
            .Where(g => g.Count() > 1)
            .Select(g => new Finding(Severity.Blocker, "tier-names-unique", label,
                $"Tiers {string.Join(" and ", g.Select(x => x.Index))} share the name " +
                $"'{offer.Tiers[g.First().Index - 1].Name}'; customers cannot tell them apart. " +
                (g.Count() == 2 ? "Rename either one." : "Rename all but one of them.")));

    /// <summary>Two tiers offering exactly the same benefits are one offer shown twice.</summary>
    private static IEnumerable<Finding> TierContentDistinct(string label, Offer offer)
    {
        for (var i = 0; i < offer.Tiers.Count; i++)
        for (var j = i + 1; j < offer.Tiers.Count; j++)
        {
            var a = offer.Tiers[i].Benefits.Select(b => b.Describe()).ToHashSet();
            if (a.SetEquals(offer.Tiers[j].Benefits.Select(b => b.Describe())))
                yield return new(Severity.Blocker, "tier-content-distinct", $"{label} › Tier {j + 1}",
                    $"Tiers {i + 1} and {j + 1} offer identical benefits; one of them was not updated.");
        }
    }

    private static IEnumerable<Finding> TierPricesIncrease(string label, Offer offer)
    {
        for (var i = 0; i < offer.Tiers.Count; i++)
        {
            var tier = offer.Tiers[i];
            if (tier.MonthlyPrice <= 0)
                yield return new(Severity.Blocker, "tier-price-positive", $"{label} › Tier {i + 1}",
                    $"'{tier.Name}' has no price.");
            // Neutral about which tier to change, as for names: a copy keeps its price too.
            if (i > 0 && tier.MonthlyPrice <= offer.Tiers[i - 1].MonthlyPrice)
                yield return new(Severity.Blocker, "tier-prices-increase", $"{label} › Tier {i + 1}",
                    $"Tier {i + 1} costs no more than tier {i}; change either price, or reorder the tiers so they run cheapest first.");
        }
    }

    private static IEnumerable<Finding> BenefitValuesValid(string label, Offer offer)
    {
        for (var i = 0; i < offer.Tiers.Count; i++)
        {
            var where = $"{label} › Tier {i + 1}";
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
    private static IEnumerable<Finding> TiersParallel(string label, Offer offer)
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
            yield return new(Severity.Warning, "tiers-parallel", $"{label} › Tier {i + 1}",
                $"'{offer.Tiers[i].Name}' {string.Join(" and ", parts)}; check the tiers read side by side.");
        }
    }

    private static IEnumerable<Finding> TermsForRecurring(string label, Offer offer)
    {
        if (offer.IsRecurring && offer.TermsUrl is null)
            yield return new(Severity.Blocker, "terms-required", label,
                "A recurring charge needs a terms link covering cancellation, rollover, expiry and refunds.");
    }

    private static IEnumerable<Finding> MedicalDisclaimer(
        Campaign c, IReadOnlyList<TextFragment> text, CampaignPolicy policy)
    {
        // Any fine-print block with text counts: what it must say is the proofread's to judge.
        if (c.BlocksOf<FinePrintBlock>().Any(f => !string.IsNullOrWhiteSpace(f.Text))) yield break;
        var hit = text
            .SelectMany(f => policy.MedicalTerms
                .Where(term => WordPrefix(term).IsMatch(f.Text))
                .Select(term => (f.Location, term)))
            .FirstOrDefault();
        if (hit != default)
            yield return new(Severity.Blocker, "medical-disclaimer", hit.Location,
                $"Promotes a medical service ('{hit.term}') without a disclaimer: add one in a fine-print block.");
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
