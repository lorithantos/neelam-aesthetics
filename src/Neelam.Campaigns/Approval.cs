namespace Neelam.Campaigns;

/// <summary>
/// A person's sign-off on one saved version of a campaign, with nothing left to fix. It belongs to
/// that exact save: any later edit or save is not approved until someone approves it again.
/// </summary>
/// <param name="By">Who approved it. Typed in while there is no sign-in; from the sign-in once there is.</param>
/// <param name="At">When, in UTC.</param>
public sealed record Approval(string By, DateTimeOffset At)
{
    /// <summary>
    /// What was seen at export: which "Worth a look" findings had been shown, on this save or any
    /// other of the same campaign (owner, 2026-10-09: "yes, carry across saves"), and who went on
    /// last for the campaign, and when; null until someone has. Stored with each save's approval.
    /// </summary>
    public WarningsSeen? WarningsSeen { get; init; }
}
