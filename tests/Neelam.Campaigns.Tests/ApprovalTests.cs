using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Approval (How it works, step 4): a person approves one saved version with nothing left to fix,
/// in their name, at the server's time. It is kept in the approvals table, keyed by client,
/// campaign and that save's stamp (owner, 2026-10-09), and counts only while the form holds exactly
/// that save: an edit, a later save, an undo or a withdrawal leaves the campaign unapproved. The row
/// outlives the save as the record; nothing about an approval is kept in the save's blob.
/// </summary>
public class ApprovalTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_container, Grace);

    // A finished campaign, saved once, open as the page would have it.
    private async Task<DraftSession> SavedFinished(CampaignDraft? draft = null)
    {
        var id = Guid.NewGuid();
        await Store.SaveDraftAsync(id, "WE’RE TURNING ONE!", draft ?? DraftFixtures.Finished());
        _clock.Now += TimeSpan.FromMinutes(1);
        return (await DraftSession.OpenAsync(Store, id))!;
    }

    // Finished, but both tiers named alike: it builds, and the rules must stop it.
    private static CampaignDraft SameNames()
    {
        var d = DraftFixtures.Finished();
        d.Offer("Offer").Tiers[0].Name.Set("Platinum Member");
        return d;
    }

    private static string Stamp(SaveRef save) => save.BlobName.Split('/')[2][..^".json".Length];

    [Fact]
    public async Task A_campaign_with_nothing_to_fix_is_approved_in_a_name_at_the_server_s_time()
    {
        var session = await SavedFinished();
        Assert.Null(session.CannotApprove());
        Assert.Null(session.CurrentApproval);

        var approval = await session.ApproveAsync("  Priya Śharma ");

        Assert.Equal(new Approval("Priya Śharma", _clock.Now), approval);
        Assert.Equal(approval, session.CurrentApproval);
        // One row, keyed by client, campaign and the save's stamp, read back by whoever opens it next.
        var row = Assert.Single(Records.Approvals.Rows);
        Assert.Equal(
            new ApprovalRecord(TestRecords.DefaultClient, session.Id!.Value, Stamp(session.Latest!), "Priya Śharma", _clock.Now),
            row);
        Assert.False(row.Withdrawn);
        var reopened = (await DraftSession.OpenAsync(Store, session.Id.Value))!;
        Assert.Equal(approval, reopened.CurrentApproval);
        // Nothing about the approval is in the save's blob.
        Assert.Equal(["title"], _container.Blobs[session.Latest!.BlobName].Metadata.Keys.ToArray());
        Assert.Single(_container.Blobs);
    }

    [Fact]
    public async Task Missing_parts_a_must_fix_or_unsaved_changes_cannot_be_approved()
    {
        var missing = await SavedFinished(DraftFixtures.SameNamesNoTerms());
        var mustFix = await SavedFinished(SameNames());
        var edited = await SavedFinished();
        edited.Editor.Subject.Text = "WE’RE TURNING TWO!";
        var neverSaved = (await DraftSession.StartAsync(Store, await SavedTemplate()))!;

        foreach (var session in new[] { missing, mustFix, edited, neverSaved })
        {
            Assert.NotNull(session.CannotApprove());
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApproveAsync("Priya"));
        }
        Assert.Equal("Fix everything marked Must fix first.", mustFix.CannotApprove());
        Assert.Equal("Save the campaign first: an approval is of a saved version.", edited.CannotApprove());
        Assert.Empty(Records.Approvals.Rows);
    }

    // A copied tier keeps its name and price (owner, 2026-10-09): until either tier changes each,
    // the rules' Must fix items stop the approval, with nothing else left to fill in.
    [Fact]
    public async Task A_copy_still_sharing_its_name_and_price_cannot_be_approved()
    {
        var session = await SavedFinished(DraftFixtures.CopiedAsItStands());

        Assert.Empty(session.Editor.Status().Missing);
        Assert.Equal("Fix everything marked Must fix first.", session.CannotApprove());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApproveAsync("Priya"));
        Assert.DoesNotContain(_container.Blobs.Values, b => b.Metadata.ContainsKey("approvedby"));
    }

    private async Task<Guid> SavedTemplate()
    {
        var id = Guid.NewGuid();
        await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        return id;
    }

    [Fact]
    public async Task A_blank_name_is_refused()
    {
        var session = await SavedFinished();

        await Assert.ThrowsAsync<ArgumentException>(() => session.ApproveAsync("   "));
        Assert.Null(session.CurrentApproval);
        Assert.Empty(Records.Approvals.Rows);
    }

    // Pinned to what was approved: the form changed is not approved, and the same words again are.
    [Fact]
    public async Task Editing_after_approval_means_it_is_no_longer_approved()
    {
        var session = await SavedFinished();
        var approval = await session.ApproveAsync("Priya");

        session.Editor.Subject.Text = "WE’RE TURNING TWO!";

        Assert.Null(session.CurrentApproval);
        Assert.Equal(approval, session.Latest!.Approval);
        Assert.NotNull(session.CannotApprove());

        session.Editor.Subject.Text = "WE’RE TURNING ONE!";
        Assert.Equal(approval, session.CurrentApproval);
    }

    [Fact]
    public async Task A_later_save_is_not_approved_and_says_an_earlier_one_was()
    {
        var session = await SavedFinished();
        await session.ApproveAsync("Priya");
        var approved = session.Latest!;

        session.Editor.Subject.Text = "WE’RE TURNING TWO!";
        await session.SaveAsync();

        Assert.Null(session.Latest!.Approval);
        Assert.Null(session.CurrentApproval);
        Assert.Equal(approved, session.SupersededApproval);
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.Null(reopened.CurrentApproval);
        Assert.Equal(approved, reopened.SupersededApproval);
        Assert.Equal("Priya", reopened.SupersededApproval!.Approval!.By);

        // Approving the new version leaves nothing superseded.
        await reopened.ApproveAsync("Priya");
        Assert.Null(reopened.SupersededApproval);
        Assert.Null((await DraftSession.OpenAsync(Store, session.Id.Value))!.SupersededApproval);
        Assert.Equal(2, Records.Approvals.Rows.Count);
    }

    // Her label is hers alone, never in the email: changing it, saved or not, keeps the approval,
    // which goes to the new save with its approver and time. The earlier save's row stays.
    [Fact]
    public async Task A_label_only_edit_and_save_keeps_the_approval()
    {
        var session = await SavedFinished();
        var approval = await session.ApproveAsync("Priya");
        _clock.Now += TimeSpan.FromMinutes(5);

        session.Editor.Label = "Beauty Bank -- first send";
        Assert.Equal(approval, session.CurrentApproval);
        await session.SaveAsync();

        Assert.Equal(approval, session.CurrentApproval);
        Assert.Null(session.SupersededApproval);
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.Equal(approval, reopened.CurrentApproval);
        Assert.Equal("Beauty Bank -- first send", reopened.Editor.Label);
        Assert.Equal(
            [(Stamp((await Store.HistoryAsync(DocumentKind.Draft, session.Id.Value))[1]), false), (Stamp(session.Latest!), false)],
            Records.Approvals.Rows.Select(r => (r.Stamp, r.Withdrawn)).Order());
    }

    // Anything else changed still voids it, the label changed with it or not.
    [Fact]
    public async Task A_content_edit_with_a_label_edit_still_voids_the_approval()
    {
        var session = await SavedFinished();
        var approved = session.Latest!;
        await session.ApproveAsync("Priya");

        session.Editor.Label = "Beauty Bank -- first send";
        session.Editor.Subject.Text = "WE’RE TURNING TWO!";
        Assert.Null(session.CurrentApproval);
        await session.SaveAsync();

        Assert.Null(session.CurrentApproval);
        Assert.Null(session.Latest!.Approval);
        Assert.Equal(approved.BlobName, session.SupersededApproval!.BlobName);
        Assert.Null((await DraftSession.OpenAsync(Store, session.Id!.Value))!.CurrentApproval);
        Assert.Single(Records.Approvals.Rows);
    }

    // Forgiving undo: the undone save loses its approval, so restoring it brings it back unapproved.
    // The row stays, marked withdrawn at the moment of the undo.
    [Fact]
    public async Task An_undone_save_loses_its_approval_and_the_row_stays()
    {
        var session = await SavedFinished();
        session.Editor.Subject.Text = "WE’RE TURNING TWO!";
        await session.SaveAsync();
        await session.ApproveAsync("Priya");
        var approved = session.Latest!;
        _clock.Now += TimeSpan.FromMinutes(5);

        Assert.True(await session.UndoLastSaveAsync());
        Assert.Null(session.CurrentApproval);
        var row = Assert.Single(Records.Approvals.Rows);
        Assert.Equal(Stamp(approved), row.Stamp);
        Assert.Equal(_clock.Now, row.WithdrawnAt);

        Assert.True(await session.RestoreAsync());
        Assert.Equal(approved.BlobName, session.Latest!.BlobName);
        Assert.Null(session.Latest.Approval);
        Assert.Null(session.CurrentApproval);
        // An undone save cannot be approved either.
        await session.UndoLastSaveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ApproveAsync(approved, "Priya"));
        Assert.Single(Records.Approvals.Rows);
    }

    [Fact]
    public async Task An_approval_can_be_withdrawn_and_the_row_says_when()
    {
        var session = await SavedFinished();
        await session.ApproveAsync("Priya");
        _clock.Now += TimeSpan.FromMinutes(5);

        await session.WithdrawApprovalAsync();

        Assert.Null(session.CurrentApproval);
        Assert.Null((await DraftSession.OpenAsync(Store, session.Id!.Value))!.CurrentApproval);
        var row = Assert.Single(Records.Approvals.Rows);
        Assert.Equal("Priya", row.ApprovedBy);
        Assert.Equal(_clock.Now, row.WithdrawnAt);
        Assert.Equal(["title"], _container.Blobs[session.Latest!.BlobName].Metadata.Keys.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WithdrawApprovalAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.WithdrawApprovalAsync(session.Latest!));

        // Approved again, it stands again: the row is replaced, the withdrawal cleared.
        await session.ApproveAsync("Priya");
        Assert.NotNull(session.CurrentApproval);
        Assert.False(Assert.Single(Records.Approvals.Rows).Withdrawn);
    }

    // The row is the record: the sweep deleting the undone save, or the save deleted any other way,
    // removes nothing from the approvals table. It simply matches no save in use.
    [Fact]
    public async Task Approval_history_survives_undo_sweep_and_delete()
    {
        var swept = await SavedFinished();
        await swept.ApproveAsync("Priya");
        var deleted = await SavedFinished();
        await deleted.ApproveAsync("Asha");
        var before = Records.Approvals.Rows.OrderBy(r => r.ApprovedBy).ToList();

        await swept.UndoLastSaveAsync();
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        Assert.Equal(1, await Store.SweepAsync());
        Assert.True(_container.Blobs.Remove(deleted.Latest!.BlobName));

        var after = Records.Approvals.Rows.OrderBy(r => r.ApprovedBy).ToList();
        Assert.Equal(before.Select(r => (r.CampaignId, r.Stamp, r.ApprovedBy, r.ApprovedAt)),
            after.Select(r => (r.CampaignId, r.Stamp, r.ApprovedBy, r.ApprovedAt)));
        Assert.Null(await DraftSession.OpenAsync(Store, swept.Id!.Value));
        Assert.Null(await DraftSession.OpenAsync(Store, deleted.Id!.Value));
        Assert.Empty(_container.Blobs);
    }

    // Approvals are read from the client's own partition only: another client's row for the very
    // same campaign id and stamp is never asked for, and never counts.
    [Fact]
    public async Task Another_client_s_rows_are_never_read()
    {
        var session = await SavedFinished();
        var other = new ClientName("other-salon");
        Records.Approvals.Seed(new ApprovalRecord(other, session.Id!.Value, Stamp(session.Latest!), "Someone else", _clock.Now));

        var reopened = (await DraftSession.OpenAsync(Store, session.Id.Value))!;

        Assert.Null(reopened.CurrentApproval);
        Assert.Null(reopened.Latest!.Approval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.WithdrawApprovalAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.WithdrawApprovalAsync(reopened.Latest));
        Assert.NotEmpty(Records.Approvals.PartitionsRead);
        Assert.All(Records.Approvals.PartitionsRead, p => Assert.Equal(TestRecords.DefaultClient.Value, p));
    }

    // Only a campaign is approved, never a layout; and an old blob's approval metadata, as the
    // store kept before 2026-10-09, means nothing now.
    [Fact]
    public async Task Templates_are_not_approved_and_blob_metadata_is_no_approval()
    {
        var template = await Store.SaveTemplateAsync(Guid.NewGuid(), DraftFixtures.Membership);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ApproveAsync(template, "Priya"));

        var session = await SavedFinished();
        var name = session.Latest!.BlobName;
        _container.Blobs[name] = (_container.Blobs[name].Blob,
            new Dictionary<string, string>(_container.Blobs[name].Metadata)
            {
                ["approvedby"] = "Priya",
                ["approvedat"] = _clock.Now.ToString("o"),
            });

        Assert.Null((await DraftSession.OpenAsync(Store, session.Id!.Value))!.Latest!.Approval);
    }
}
