namespace Neelam.Campaigns;

/// <summary>
/// The tunable parts of the checks. The defaults are a starting point for the owner to adjust,
/// not legal advice: a restricted term is flagged for a person to sign off, never auto-rejected.
/// </summary>
public sealed record CampaignPolicy
{
    /// <summary>Words that need a human (and possibly counsel) to approve before they go out.</summary>
    public IReadOnlyDictionary<string, string> RestrictedTerms { get; init; } = new Dictionary<string, string>
    {
        ["bank"] = "Use of \"bank\" by a non-bank is restricted in many states.",
        ["savings account"] = "Describes a regulated financial product; this is a prepaid service plan.",
        ["guaranteed"] = "Outcome claims about treatments need substantiation.",
    };

    /// <summary>Services that are medical, so their promotion needs a disclaimer.</summary>
    public IReadOnlyList<string> MedicalTerms { get; init; } =
        ["injection", "neurotoxin", "botox", "filler", "laser", "peel", "microneedling"];

    /// <summary>Above this many emoji in the whole email, warn.</summary>
    public int MaxEmoji { get; init; } = 8;

    /// <summary>A run of this many identical words in two places counts as repetition.</summary>
    public int RepeatedPhraseWords { get; init; } = 7;

    public static CampaignPolicy Default { get; } = new();
}
