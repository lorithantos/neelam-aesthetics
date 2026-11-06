namespace Neelam.Campaigns;

/// <summary>
/// What the compare (<see cref="CampaignComparison"/>) says to her, in one place: the voice the owner
/// gave on 2026-11-06 ("Changed since your save at 10:42 AM", "Added", "Removed", "Moved from 3rd to
/// 5th", "No changes since your last save."), the rest proposed wording, the owner's to change.
/// </summary>
public static class CompareWords
{
    /// <summary>The button beside Save that shows the form against its last save.</summary>
    public const string UnsavedButton = "What haven't I saved yet?";

    /// <summary>Above what the form holds that its last save does not.</summary>
    public static string UnsavedHeading(string savedAt) => $"Changed since your save at {savedAt}:";

    /// <summary>The form is its last save.</summary>
    public const string NothingUnsaved = "No changes since your last save.";

    /// <summary>Under an empty compare of the form, while it holds text the draft cannot hold yet.</summary>
    public const string UnsavedErrors = "What is marked to fix above isn't in this list until it is fixed.";

    /// <summary>The button that opens the compare of an earlier save with the last one.</summary>
    public const string EarlierButton = "Compare with an earlier save";

    /// <summary>The label of the list of earlier saves.</summary>
    public const string EarlierLabel = "Earlier save";

    /// <summary>Above what changed between an earlier save and the last one.</summary>
    public static string EarlierHeading(string earlierAt) => $"Changed since your save at {earlierAt}, up to your last save:";

    /// <summary>An earlier save that is the same as the last one.</summary>
    public static string NothingSinceEarlier(string earlierAt) => $"No changes between your save at {earlierAt} and your last save.";

    /// <summary>Above what using a template would change, when nothing she wrote would be lost.</summary>
    public static string TemplateHeading(string template) => $"Using the template {template} changes this campaign like this:";

    /// <summary>Using a template that would change nothing.</summary>
    public static string NothingFromTemplate(string template) => $"Using the template {template} changes nothing in this campaign.";

    /// <summary>Beside a value that is the template's own text, not hers.</summary>
    public const string FixedText = "The template's fixed text";

    /// <summary>Where a field was, or is, empty.</summary>
    public const string Empty = "Empty";

    public const string Added = "Added";

    public const string Removed = "Removed";

    public const string Changed = "Changed";

    /// <summary>"Moved from 3rd to 5th".</summary>
    public static string Moved(int from, int to) => $"Moved from {Ordinal(from)} to {Ordinal(to)}";

    /// <summary>"Renamed from Headline": the template renamed the block.</summary>
    public static string Renamed(string was) => $"Renamed from {was}";

    /// <summary>What happened to a block, in a few words: "Added", "Removed", or what of moved, renamed and changed applies.</summary>
    public static string What(BlockChange change)
    {
        if (change.Kind == BlockChangeKind.Added) return Added;
        if (change.Kind == BlockChangeKind.Removed) return Removed;
        var parts = new List<string>();
        if (change is { Moved: true, From: { } from, To: { } to }) parts.Add(Moved(from, to));
        if (change.WasLabel is { } was) parts.Add(Renamed(was));
        if (change.Fields.Count > 0) parts.Add(Changed);
        return string.Join(" · ", parts);
    }

    /// <summary>1st, 2nd, 3rd, 4th, 11th, 21st.</summary>
    public static string Ordinal(int n) =>
        (n % 100) is >= 11 and <= 13 ? $"{n}th"
        : (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };
}
