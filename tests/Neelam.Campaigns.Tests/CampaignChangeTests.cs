using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Two editing sessions over one campaign, as two tabs hold it: what each can tell of the other's
/// changes from storage (<see cref="DraftSession.ChangeSince"/>), Approve refusing a version storage no
/// longer agrees with, and the in-process signal each change raises for the other.
/// </summary>
public class CampaignChangeTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly CampaignSignals _signals = new();
    private TestRecords? _records;

    private TestRecords Records => _records ??= new(_clock);

    private CampaignStore Store =>
        new(_container, _clock, TimeSpan.FromDays(1), Records.Approvals, Records.Recorder.For(TestRecords.DefaultClient, Actor.Demo), _signals);

    private async Task<(DraftSession A, DraftSession B)> TwoTabs(bool approved = false)
    {
        var id = Guid.NewGuid();
        var save = await Store.SaveDraftAsync(id, "Hello", DraftFixtures.Finished());
        if (approved) await Store.ApproveAsync(save, "Priya");
        _clock.Now += TimeSpan.FromMinutes(1);
        return ((await DraftSession.OpenAsync(Store, id))!, (await DraftSession.OpenAsync(Store, id))!);
    }

    private static async Task<CampaignChange> Change(DraftSession session) => session.ChangeSince(await session.NewestAsync());

    [Fact]
    public async Task Nothing_changed_is_none()
    {
        var (a, _) = await TwoTabs(approved: true);

        Assert.Equal(CampaignChange.None, await Change(a));
    }

    [Fact]
    public async Task A_save_in_the_other_tab_is_saved_since()
    {
        var (a, b) = await TwoTabs();
        b.Editor.Subject.Text = "Theirs";

        await b.SaveAsync();

        Assert.Equal(CampaignChange.SavedSince, await Change(a));
        Assert.Equal(CampaignChange.None, await Change(b));
    }

    [Fact]
    public async Task An_approval_withdrawn_in_the_other_tab_is_withdrawn()
    {
        var (a, b) = await TwoTabs(approved: true);

        await b.WithdrawApprovalAsync();

        Assert.Equal(CampaignChange.ApprovalWithdrawn, await Change(a));
    }

    [Fact]
    public async Task An_approval_given_in_the_other_tab_is_approved()
    {
        var (a, b) = await TwoTabs();

        await b.ApproveAsync("Priya");

        Assert.Equal(CampaignChange.Approved, await Change(a));
    }

    [Fact]
    public async Task The_version_undone_in_the_other_tab_is_undone_and_the_only_one_is_deleted()
    {
        var (a, b) = await TwoTabs();
        b.Editor.Subject.Text = "Second";
        await b.SaveAsync();
        var c = (await DraftSession.OpenAsync(Store, b.Id!.Value))!;

        await b.UndoLastSaveAsync();

        Assert.Equal(CampaignChange.Undone, await Change(c));
        await b.UndoLastSaveAsync();
        Assert.Equal(CampaignChange.Deleted, await Change(a));
    }

    // Storage may keep a time coarser than the clock gave it: the same approval read back is no change.
    [Fact]
    public async Task An_approval_read_back_a_little_coarser_is_the_same()
    {
        var (a, _) = await TwoTabs();
        _clock.Now += TimeSpan.FromTicks(1234);
        await a.ApproveAsync("Priya");
        var stored = await a.NewestAsync();

        var coarser = stored! with { Approval = stored.Approval! with { At = stored.Approval.At.AddTicks(-1234) } };

        Assert.Equal(CampaignChange.None, a.ChangeSince(coarser));
        Assert.Equal(CampaignChange.Approved, a.ChangeSince(stored with { Approval = stored.Approval with { At = stored.Approval.At.AddSeconds(2) } }));
        Assert.Equal(CampaignChange.Approved, a.ChangeSince(stored with { Approval = stored.Approval with { By = "Neelam" } }));
    }

    [Fact]
    public async Task Approving_after_the_other_tab_saved_is_refused_and_records_nothing()
    {
        var (a, b) = await TwoTabs();
        b.Editor.Subject.Text = "Theirs";
        await b.SaveAsync();

        var refused = await Assert.ThrowsAsync<CampaignChangedException>(() => a.ApproveAsync("Priya"));

        Assert.Equal(CampaignChange.SavedSince, refused.Change);
        Assert.Equal("Not approved: the campaign was saved again since you opened it. Load the latest above, check it, then approve it.",
            refused.Message);
        Assert.Empty(Records.Approvals.Rows);
        Assert.Null(a.Latest!.Approval);
    }

    [Fact]
    public async Task Approving_after_the_other_tab_approved_is_refused()
    {
        var (a, b) = await TwoTabs();
        await b.ApproveAsync("Neelam");

        var refused = await Assert.ThrowsAsync<CampaignChangedException>(() => a.ApproveAsync("Priya"));

        Assert.Equal(CampaignChange.Approved, refused.Change);
        Assert.Equal("Neelam", Assert.Single(Records.Approvals.Rows).ApprovedBy);
    }

    [Fact]
    public async Task Each_change_is_signalled_to_the_other_tab_and_never_back_to_its_own()
    {
        var (a, b) = await TwoTabs();
        var toA = 0;
        var toB = 0;
        using var listenA = _signals.Listen(a.Client, a.Id!.Value, a, () => Interlocked.Increment(ref toA));
        using var listenB = _signals.Listen(b.Client, b.Id!.Value, b, () => Interlocked.Increment(ref toB));

        await b.ApproveAsync("Priya");
        await b.WithdrawApprovalAsync();
        b.Editor.Subject.Text = "Second";
        await b.SaveAsync();
        await b.UndoLastSaveAsync();
        await b.RestoreAsync();

        Assert.Equal(5, toA);
        Assert.Equal(0, toB);
    }

    [Fact]
    public async Task A_restore_from_the_campaigns_list_is_signalled_to_every_open_tab()
    {
        var (a, b) = await TwoTabs();
        var told = 0;
        using var listening = _signals.Listen(a.Client, a.Id!.Value, a, () => Interlocked.Increment(ref told));
        await b.UndoLastSaveAsync();
        var undone = b.Restorable!;

        await DraftSession.RestoreAsync(Store, undone);

        Assert.Equal(2, told);
    }

    [Fact]
    public async Task A_write_straight_through_the_store_raises_nothing()
    {
        var (a, _) = await TwoTabs();
        var told = 0;
        using var listening = _signals.Listen(a.Client, a.Id!.Value, new object(), () => told++);

        await Store.ApproveAsync(a.Latest!, "Priya");
        await Store.SaveDraftAsync(a.Id.Value, "Hello", DraftFixtures.Finished());

        Assert.Equal(0, told);
    }

    [Theory]
    [InlineData(CampaignChange.SavedSince, "The campaign was saved again since you opened it, in another tab or by someone else.")]
    [InlineData(CampaignChange.Undone, "The version on this page was undone since you opened it, in another tab or by someone else.")]
    [InlineData(CampaignChange.Deleted, "The campaign was deleted since you opened it, in another tab or by someone else. You can restore it from Recently deleted in your campaigns.")]
    [InlineData(CampaignChange.ApprovalWithdrawn, "Its approval was withdrawn since you opened it, in another tab or by someone else.")]
    [InlineData(CampaignChange.Approved, "Its approval changed since you opened it, in another tab or by someone else.")]
    public void The_notice_says_what_changed(CampaignChange change, string notice) =>
        Assert.Equal(notice, DraftSession.ChangedNotice(change));

    [Theory]
    [InlineData("00:00:09", false)]
    [InlineData("00:00:10", true)]
    [InlineData("00:01:00", true)]
    [InlineData("00:02:00", true)]
    [InlineData("00:02:01", false)]
    public void The_interval_is_at_most_two_minutes_as_the_owner_asked(string interval, bool runs) =>
        Assert.Equal(runs, new CampaignWatchOptions { Interval = TimeSpan.Parse(interval) }.Problem() is null);
}

/// <summary>
/// The in-process signal, shared by every circuit on the instance: listening, leaving and raising from
/// many threads at once lose no listener and tell no one twice, and the last one out takes the
/// campaign's entry with it.
/// </summary>
public class CampaignSignalsTests
{
    private static readonly ClientName Client = TestRecords.DefaultClient;

    [Fact]
    public void A_listener_hears_others_but_not_itself_until_it_leaves()
    {
        var signals = new CampaignSignals();
        var id = Guid.NewGuid();
        var owner = new object();
        var heard = 0;
        var listening = signals.Listen(Client, id, owner, () => heard++);

        signals.Raise(Client, id, source: null);
        signals.Raise(Client, id, source: owner);
        signals.Raise(Client, Guid.NewGuid(), source: null);
        signals.Raise(new ClientName("test-salon-two"), id, source: null);
        listening.Dispose();
        signals.Raise(Client, id, source: null);

        Assert.Equal(1, heard);
        Assert.Equal(0, signals.Listening(Client, id));
        Assert.Equal(0, signals.Campaigns);
    }

    [Fact]
    public void A_listener_that_throws_stops_no_one_else()
    {
        var signals = new CampaignSignals();
        var id = Guid.NewGuid();
        var heard = 0;
        using var failing = signals.Listen(Client, id, new object(), () => throw new InvalidOperationException("gone"));
        using var listening = signals.Listen(Client, id, new object(), () => heard++);

        signals.Raise(Client, id, source: null);

        Assert.Equal(1, heard);
    }

    // Many tabs opening and closing on a few campaigns while saves land on them from every side: those
    // listening throughout hear every raise exactly once, and nothing is left behind.
    [Fact]
    public async Task Listening_leaving_and_raising_at_once_lose_nothing()
    {
        var signals = new CampaignSignals();
        var campaigns = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        const int Raises = 2_000;
        var steady = campaigns.Select(id =>
        {
            var heard = new StrongBox<int>();
            return (Id: id, Heard: heard, Listening: signals.Listen(Client, id, new object(), () => Interlocked.Increment(ref heard.Value)));
        }).ToArray();
        using var go = new ManualResetEventSlim();

        var churn = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            go.Wait();
            for (var i = 0; i < 1_000; i++)
                signals.Listen(Client, campaigns[(t + i) % campaigns.Length], new object(), () => { }).Dispose();
        })).ToArray();
        var raisers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            go.Wait();
            for (var i = 0; i < Raises; i++) signals.Raise(Client, campaigns[t], source: null);
        })).ToArray();
        go.Set();
        await Task.WhenAll(churn.Concat(raisers));

        Assert.All(steady, s => Assert.Equal(Raises, s.Heard.Value));
        Assert.All(campaigns, id => Assert.Equal(1, signals.Listening(Client, id)));
        foreach (var s in steady) s.Listening.Dispose();
        Assert.Equal(0, signals.Campaigns);
    }

    private sealed class StrongBox<T>
    {
        public T Value = default!;
    }
}
