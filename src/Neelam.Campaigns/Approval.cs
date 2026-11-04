namespace Neelam.Campaigns;

/// <summary>
/// A person's sign-off on one saved version of a campaign, with nothing left to fix. It belongs to
/// that exact save: any later edit or save is not approved until someone approves it again.
/// </summary>
/// <param name="By">Who approved it (the first, when the client needs more than one). Typed in while there is no sign-in; from the sign-in once there is.</param>
/// <param name="At">When, in UTC.</param>
public sealed record Approval(string By, DateTimeOffset At)
{
    /// <summary>
    /// What was seen at export: which "Worth a look" findings had been shown, on this save or any
    /// other of the same campaign (owner, 2026-10-09: "yes, carry across saves"), and who went on
    /// last for the campaign, and when; null until someone has. Stored with each save's approval.
    /// </summary>
    public WarningsSeen? WarningsSeen { get; init; }

    /// <summary>
    /// Everyone after <see cref="By"/> who approved the same save, in the order they did, when the
    /// client needs more than one approver (owner, 2026-11-03: "make the number a config setting");
    /// null when <see cref="By"/> is the only one, as it always is with one approver required.
    /// </summary>
    public IReadOnlyList<Approver>? Others { get; init; }

    /// <summary>Everyone who approved the save, <see cref="By"/> first, in the order they did.</summary>
    public IReadOnlyList<Approver> Approvers
    {
        get
        {
            List<Approver> all = [new Approver(this.By, this.At)];
            if (this.Others is { } others)
            {
                all.AddRange(others);
            }
            return all;
        }
    }

    /// <summary>When the last of them approved: <see cref="At"/> when there is only one.</summary>
    public DateTimeOffset LastAt => this.Others is { Count: > 0 } others ? others[^1].At : this.At;

    /// <summary>
    /// Who approved it, as a sentence names them: "Priya", "Priya and Sam", "Priya, Sam and Lee".
    /// With one approver, exactly <see cref="By"/>.
    /// </summary>
    public string Names
    {
        get
        {
            List<string> names = this.Approvers.Select(a => a.By).ToList();
            if (names.Count == 1)
            {
                return names[0];
            }
            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        }
    }

    /// <summary>Whether <paramref name="name"/> has already approved the save, ignoring case and spacing at the ends.</summary>
    public bool IsApprovedBy(string name) =>
        this.Approvers.Any(a => string.Equals(a.By.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>One person's approval of a save: who, and when, in UTC.</summary>
public sealed record Approver(string By, DateTimeOffset At);
