using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Approval (How it works, step 4): a person approves one saved version with nothing left to fix,
/// in their name, at the server's time. It is kept with that save, in its own metadata, and belongs
/// to it alone: an edit, a later save or an undo leaves the campaign unapproved.
/// </summary>
public class ApprovalTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private CampaignStore Store => new(_container, _clock, Grace);

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

    [Fact]
    public async Task A_campaign_with_nothing_to_fix_is_approved_in_a_name_at_the_server_s_time()
    {
        var session = await SavedFinished();
        Assert.Null(session.CannotApprove());
        Assert.Null(session.CurrentApproval);

        var approval = await session.ApproveAsync("  Priya Śharma ");

        Assert.Equal(new Approval("Priya Śharma", _clock.Now), approval);
        Assert.Equal(approval, session.CurrentApproval);
        // Kept with the save itself, and read back by whoever opens it next.
        var blob = _container.Blobs[session.Latest!.BlobName];
        Assert.Contains("approvedby", blob.Metadata.Keys);
        Assert.Contains("approvedat", blob.Metadata.Keys);
        Assert.All(blob.Metadata.Values, v => Assert.True(v.All(char.IsAscii), v));
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.Equal(approval, reopened.CurrentApproval);
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
        Assert.DoesNotContain(_container.Blobs.Values, b => b.Metadata.ContainsKey("approvedby"));
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

        // Approving the new version leaves nothing superseded.
        await reopened.ApproveAsync("Priya");
        Assert.Null(reopened.SupersededApproval);
        Assert.Null((await DraftSession.OpenAsync(Store, session.Id.Value))!.SupersededApproval);
    }

    // Forgiving undo: the undone save drops its approval, so restoring it brings it back unapproved.
    [Fact]
    public async Task An_undone_save_drops_its_approval()
    {
        var session = await SavedFinished();
        session.Editor.Subject.Text = "WE’RE TURNING TWO!";
        await session.SaveAsync();
        await session.ApproveAsync("Priya");
        var approved = session.Latest!;

        Assert.True(await session.UndoLastSaveAsync());
        Assert.Null(session.CurrentApproval);
        Assert.DoesNotContain("approvedby", _container.Blobs[approved.BlobName].Metadata.Keys);

        Assert.True(await session.RestoreAsync());
        Assert.Equal(approved.BlobName, session.Latest!.BlobName);
        Assert.Null(session.Latest.Approval);
        Assert.Null(session.CurrentApproval);
        // An undone save cannot be approved either.
        await session.UndoLastSaveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ApproveAsync(approved, "Priya"));
    }

    [Fact]
    public async Task An_approval_can_be_withdrawn()
    {
        var session = await SavedFinished();
        await session.ApproveAsync("Priya");

        await session.WithdrawApprovalAsync();

        Assert.Null(session.CurrentApproval);
        Assert.Null((await DraftSession.OpenAsync(Store, session.Id!.Value))!.CurrentApproval);
        Assert.Equal(["title"], _container.Blobs[session.Latest!.BlobName].Metadata.Keys.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WithdrawApprovalAsync());
    }

    // Only a campaign is approved, never a layout; and half an approval, as something outside the
    // store might leave, is none.
    [Fact]
    public async Task Templates_are_not_approved_and_half_an_approval_is_none()
    {
        var template = await Store.SaveTemplateAsync(Guid.NewGuid(), DraftFixtures.Membership);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ApproveAsync(template, "Priya"));

        var session = await SavedFinished();
        var name = session.Latest!.BlobName;
        _container.Blobs[name] = (_container.Blobs[name].Blob,
            new Dictionary<string, string>(_container.Blobs[name].Metadata) { ["approvedby"] = "Priya" });

        Assert.Null((await DraftSession.OpenAsync(Store, session.Id!.Value))!.Latest!.Approval);
    }
}
