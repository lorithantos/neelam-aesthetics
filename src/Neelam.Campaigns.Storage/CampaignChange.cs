namespace Neelam.Campaigns.Storage;

/// <summary>
/// What storage says has happened to a campaign since the version a page holds
/// (<see cref="DraftSession.ChangeSince"/>). Anything but <see cref="None"/> means the page is out of
/// date: it may not approve or export what it shows, and it says so.
/// </summary>
public enum CampaignChange
{
    /// <summary>The page's version is still the newest in use, with the approval it shows.</summary>
    None,

    /// <summary>A newer version was saved.</summary>
    SavedSince,

    /// <summary>The page's version was undone; an earlier one is in use.</summary>
    Undone,

    /// <summary>Every save was undone: the campaign is deleted, and can be restored for the grace period.</summary>
    Deleted,

    /// <summary>The approval the page shows was withdrawn.</summary>
    ApprovalWithdrawn,

    /// <summary>The version was approved, or approved again, by someone other than the page shows.</summary>
    Approved,
}

/// <summary>
/// An approval or export refused because storage no longer agrees with the page: its
/// <see cref="Exception.Message"/> is what to tell her.
/// </summary>
public sealed class CampaignChangedException(CampaignChange change, string message) : InvalidOperationException(message)
{
    public CampaignChange Change { get; } = change;
}
