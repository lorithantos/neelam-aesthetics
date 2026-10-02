namespace Neelam.Campaigns;

/// <summary>
/// A campaign being written. Every value is a <see cref="Slot{T}"/>, so the draft knows what a
/// person entered, what the template supplied, and what was copied and not yet looked at.
/// <see cref="Build"/> turns it into a <see cref="Campaign"/> only when nothing is missing and
/// nothing copied is unreviewed.
/// </summary>
public sealed class CampaignDraft
{
    public string? TemplateName { get; init; }

    public Slot<string> Subject { get; } = Slot<string>.Empty();
    public Slot<string> Preheader { get; } = Slot<string>.Empty();
    public Slot<string> Headline { get; } = Slot<string>.Empty();
    public Slot<string> Greeting { get; init; } = Slot<string>.Empty();
    public Slot<IReadOnlyList<string>> Opening { get; } = Slot<IReadOnlyList<string>>.Empty();
    public OfferDraft? Offer { get; init; }
    public Slot<IReadOnlyList<string>> Closing { get; init; } = Slot<IReadOnlyList<string>>.Empty();
    public Slot<SignOff> SignOff { get; init; } = Slot<SignOff>.Empty();
    public Slot<CallToAction> CallToAction { get; } = Slot<CallToAction>.Empty();
    public Slot<string> Disclaimer { get; init; } = Slot<string>.Empty();

    public DraftResult Build()
    {
        var problems = new DraftProblems();
        problems.Require(Subject, "Subject");
        problems.Require(Headline, "Headline");
        problems.Require(Greeting, "Greeting");
        problems.Require(Opening, "Opening");
        problems.Require(Closing, "Closing");
        problems.Require(SignOff, "Sign-off");
        problems.Require(CallToAction, "Call to action");
        problems.Optional(Preheader, "Preheader");
        problems.Optional(Disclaimer, "Disclaimer");
        Offer?.Check(problems);

        if (problems.Any)
            return new DraftResult(null, problems.Findings);

        return new DraftResult(new Campaign(
            Subject: Subject.Value,
            Headline: Headline.Value,
            Greeting: Greeting.Value,
            Opening: Opening.Value,
            Offer: Offer?.ToOffer(),
            Closing: Closing.Value,
            SignOff: SignOff.Value,
            CallToAction: CallToAction.Value,
            Disclaimer: Disclaimer.HasValue ? Disclaimer.Value : null,
            Preheader: Preheader.HasValue ? Preheader.Value : null), []);
    }
}

/// <param name="Campaign">The built campaign, or null while <paramref name="Problems"/> is not empty.</param>
public sealed record DraftResult(Campaign? Campaign, IReadOnlyList<Finding> Problems)
{
    public bool Succeeded => Campaign is not null;
}

public sealed class OfferDraft
{
    private readonly List<TierDraft> _tiers = [];

    public Slot<string> Name { get; } = Slot<string>.Empty();
    public Slot<string> Summary { get; } = Slot<string>.Empty();
    public Slot<string> TiersNote { get; } = Slot<string>.Empty();
    public Slot<Uri> TermsUrl { get; } = Slot<Uri>.Empty();
    public bool IsRecurring { get; set; }

    public IReadOnlyList<TierDraft> Tiers => _tiers;

    public TierDraft AddTier()
    {
        var tier = new TierDraft();
        _tiers.Add(tier);
        return tier;
    }

    /// <summary>
    /// Starts a new tier from an existing one without silently duplicating it. The benefit
    /// list is copied for convenience, but each benefit is marked as copied and must be edited
    /// or confirmed. The name and price — what makes a tier a different tier — are not copied
    /// at all and must be entered.
    /// </summary>
    public TierDraft CopyTier(int index)
    {
        var source = _tiers[index];
        var label = $"Tier {index + 1}";
        var tier = new TierDraft();
        foreach (var b in source.Benefits)
            tier.AddCopiedBenefit(b.Value, label);
        _tiers.Add(tier);
        return tier;
    }

    public void RemoveTier(int index) => _tiers.RemoveAt(index);

    internal void Check(DraftProblems problems)
    {
        problems.Require(Name, "Offer › Name");
        problems.Require(Summary, "Offer › Summary");
        problems.Optional(TiersNote, "Offer › Tiers note");
        if (IsRecurring) problems.Require(TermsUrl, "Offer › Terms link");
        else problems.Optional(TermsUrl, "Offer › Terms link");

        if (_tiers.Count == 0)
            problems.Add("draft-missing", "Offer › Tiers", "The offer has no tiers.");

        for (var i = 0; i < _tiers.Count; i++)
            _tiers[i].Check(problems, $"Offer › Tier {i + 1}");
    }

    internal Offer ToOffer() => new(
        Name: Name.Value,
        Summary: Summary.Value,
        Tiers: _tiers.Select(t => t.ToTier()).ToList(),
        IsRecurring: IsRecurring,
        TermsUrl: TermsUrl.HasValue ? TermsUrl.Value : null,
        TiersNote: TiersNote.HasValue ? TiersNote.Value : null);
}

public sealed class TierDraft
{
    private readonly List<Slot<Benefit>> _benefits = [];

    public Slot<string> Name { get; } = Slot<string>.Empty();
    public Slot<decimal> MonthlyPrice { get; } = Slot<decimal>.Empty();

    public IReadOnlyList<Slot<Benefit>> Benefits => _benefits;

    public void AddBenefit(Benefit benefit)
    {
        var slot = Slot<Benefit>.Empty();
        slot.Set(benefit);
        _benefits.Add(slot);
    }

    internal void AddCopiedBenefit(Benefit benefit, string source) =>
        _benefits.Add(Slot<Benefit>.CopiedFromSource(benefit, source));

    public void RemoveBenefit(int index) => _benefits.RemoveAt(index);

    internal void Check(DraftProblems problems, string where)
    {
        problems.Require(Name, $"{where} › Name");
        problems.Require(MonthlyPrice, $"{where} › Price");
        if (_benefits.Count == 0)
            problems.Add("draft-missing", where, "The tier has no benefits.");
        for (var i = 0; i < _benefits.Count; i++)
            problems.Require(_benefits[i], $"{where} › Benefit {i + 1}");
    }

    internal Tier ToTier() => new(Name.Value, MonthlyPrice.Value, _benefits.Select(b => b.Value).ToList());
}

/// <summary>Collects build problems as blocker findings, so drafts report like the gate does.</summary>
internal sealed class DraftProblems
{
    private readonly List<Finding> _findings = [];

    public IReadOnlyList<Finding> Findings => _findings;

    public bool Any => _findings.Count > 0;

    public void Add(string rule, string where, string message) =>
        _findings.Add(new Finding(Severity.Blocker, rule, where, message));

    public void Require<T>(Slot<T> slot, string where)
    {
        if (!slot.HasValue) Add("draft-missing", where, $"{where} has not been filled in.");
        else Optional(slot, where);
    }

    public void Optional<T>(Slot<T> slot, string where)
    {
        if (slot.Origin == Origin.Copied)
            Add("draft-unreviewed-copy", where,
                $"{where} was copied from {slot.CopiedFrom} and not reviewed: " +
                $"\"{Describe(slot.Value)}\". Edit it, or confirm it is right for this tier.");
    }

    private static string Describe(object? value) => value is Benefit b ? b.Describe() : value?.ToString() ?? "";
}
