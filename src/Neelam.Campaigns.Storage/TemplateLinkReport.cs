namespace Neelam.Campaigns.Storage;

/// <summary>
/// What the one-time template link update did, or would do, for one client
/// (<see cref="CampaignStore.LinkTemplatesAsync"/>): by campaign id only, never a name or any content,
/// so it can go to the logs. A campaign is listed once under each heading, however many of its saves
/// fall under it.
/// </summary>
/// <param name="Applied">False for a report-only run: nothing was written.</param>
public sealed class TemplateLinkReport(bool applied)
{
    public bool Applied { get; } = applied;

    /// <summary>Campaigns with saves linked (or, report-only, that would be).</summary>
    public SortedSet<Guid> Linked { get; } = [];

    /// <summary>Campaigns whose template name matches none of her templates' names, or that record none.</summary>
    public SortedSet<Guid> NoMatch { get; } = [];

    /// <summary>Campaigns whose template name two or more of her templates have.</summary>
    public SortedSet<Guid> Ambiguous { get; } = [];

    /// <summary>Campaigns with a save that changed while it was being linked (an undo, say): run again.</summary>
    public SortedSet<Guid> Retry { get; } = [];

    /// <summary>Saves that record their template already, in their JSON or linked before.</summary>
    public int AlreadyLinked { get; set; }

    /// <summary>One line for the logs: the counts and the ids under each heading.</summary>
    public override string ToString() =>
        $"{(Applied ? "applied" : "report only")}; linked {Ids(Linked)}; no match {Ids(NoMatch)}; " +
        $"more than one match {Ids(Ambiguous)}; changed meanwhile, run again {Ids(Retry)}; saves already linked {AlreadyLinked}";

    private static string Ids(SortedSet<Guid> ids) => ids.Count == 0 ? "0" : $"{ids.Count} ({string.Join(", ", ids.Select(i => i.ToString("N")))})";
}
