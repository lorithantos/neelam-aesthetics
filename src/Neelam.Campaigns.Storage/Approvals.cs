namespace Neelam.Campaigns.Storage;

/// <summary>
/// One approval of one campaign save, as the approvals table keeps it (owner, 2026-10-09): keyed by
/// client, campaign and the save's stamp, with who approved it and when, and when it was withdrawn.
/// The row outlives the save: undoing it, the sweep deleting it, or deleting it any other way removes
/// nothing here, so the row is the record that the save was approved. It never holds the campaign's
/// content, only ids, a name and times.
/// </summary>
/// <param name="Stamp">The save's date/time stamp, as in its blob name.</param>
/// <param name="WithdrawnAt">When the approval was withdrawn, or the save undone; null while it stands.</param>
/// <param name="WarningsSeenBy">
/// Who was shown the save's "Worth a look" findings at export and went on; null until someone has.
/// A name only: never which findings, or what they said.
/// </param>
/// <param name="WarningsSeenAt">When, in UTC; null until then.</param>
public sealed record ApprovalRecord(
    ClientName Client, Guid CampaignId, string Stamp, string ApprovedBy, DateTimeOffset ApprovedAt,
    DateTimeOffset? WithdrawnAt = null, string? WarningsSeenBy = null, DateTimeOffset? WarningsSeenAt = null)
{
    public bool Withdrawn => WithdrawnAt is not null;

    public Approval Approval => new(ApprovedBy, ApprovedAt)
    {
        // Both or neither: a half-written record is not taken as seen, so the list is shown again.
        WarningsSeen = WarningsSeenBy is { Length: > 0 } by && WarningsSeenAt is { } at ? new WarningsSeen(by, at) : null,
    };
}

/// <summary>The approvals table. One partition per client, so a client's approvals sit under its own access rules.</summary>
public interface IApprovalStore
{
    /// <summary>Every approval of one campaign's saves, withdrawn ones and those of deleted saves included.</summary>
    Task<IReadOnlyList<ApprovalRecord>> ForCampaignAsync(
        ClientName client, Guid campaignId, CancellationToken cancellationToken = default);

    /// <summary>Writes the approval of one save, replacing any earlier row for the same save.</summary>
    Task PutAsync(ApprovalRecord approval, CancellationToken cancellationToken = default);
}
