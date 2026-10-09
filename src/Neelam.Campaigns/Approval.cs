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
    /// Who was shown this save's "Worth a look" findings at export and went on, and when; null until
    /// someone has. Kept with the approval because both belong to the one save.
    /// </summary>
    public WarningsSeen? WarningsSeen { get; init; }
}
