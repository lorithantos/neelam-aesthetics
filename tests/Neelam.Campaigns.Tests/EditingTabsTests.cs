using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The editor tabs open on this instance (<see cref="EditingTabs"/>): which of them is also being edited in
/// another tab, on this instance or (by the rows the beat reads) another, and who is told when that changes.
/// The clock moves only when the test moves it.
/// </summary>
public class EditingTabsTests
{
    private static readonly ClientName Client = TestRecords.DefaultClient;
    private static readonly ClientName OtherClient = new("test-salon-two");
    private static readonly TabSubject Campaign = TabSubject.Campaign(Guid.NewGuid());
    private static readonly TabSubject OtherCampaign = TabSubject.Campaign(Guid.NewGuid());

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly ListLogger<EditingTabs> _log = new();
    private readonly EditingTabsOptions _settings = new();
    private readonly EditingTabs _tabs;

    public EditingTabsTests() => _tabs = new EditingTabs(_clock, Options.Create(_settings), _log);

    private void Pass(TimeSpan by) => _clock.Now += by;

    private static readonly TimeSpan Moment = TimeSpan.FromSeconds(1);

    private (EditingTab Tab, Told Told) Open(
        TabSubject? subject = null, Guid? circuit = null, ClientName? client = null)
    {
        var told = new Told();
        var tab = _tabs.Register(client ?? Client, subject ?? Campaign, circuit ?? Guid.NewGuid(), told.Tell);
        return (tab, told);
    }

    [Fact]
    public void Opening_gives_a_new_tab_opened_now_and_closing_it_twice_is_harmless()
    {
        Assert.True(_tabs.IsEmpty);

        var (a, _) = Open();
        Pass(Moment);
        var (b, _) = Open();

        Assert.NotEqual(Guid.Empty, a.TabId);
        Assert.NotEqual(a.TabId, b.TabId);
        Assert.Equal(_clock.Now - Moment, a.OpenedAt);
        Assert.Equal(_clock.Now, b.OpenedAt);
        Assert.False(_tabs.IsEmpty);
        Assert.Equal(2, _tabs.Snapshot().Count);

        a.Dispose();
        a.Dispose();
        Assert.Equal(new[] { b.TabId }, _tabs.Snapshot().Select(t => t.TabId));
        b.Dispose();
        Assert.True(_tabs.IsEmpty);
        Assert.Empty(_tabs.Snapshot());
    }

    [Fact]
    public void A_closed_tab_is_told_nothing()
    {
        var (a, aTold) = Open();
        var (b, _) = Open();
        var circuit = Guid.NewGuid();
        var (c, _) = Open(circuit: circuit);
        a.Dispose();
        Pass(Moment);

        b.Typed();
        c.Typed();
        _tabs.SetConnected(circuit, false);
        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(Guid.NewGuid(), _clock.Now, _clock.Now)]);
        _tabs.Refresh();

        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public void Typing_marks_the_time()
    {
        var (a, _) = Open();
        Assert.Null(a.LastTyped);

        Pass(TimeSpan.FromMinutes(1));
        a.Typed();

        Assert.Equal(_clock.Now, a.LastTyped);
    }

    [Fact]
    public void Another_tab_typing_since_this_one_opened_marks_it_and_not_the_typist()
    {
        var (a, _) = Open();
        var (b, _) = Open();
        Pass(Moment);

        b.Typed();

        Assert.True(a.OthersEditing);
        Assert.False(b.OthersEditing);

        // Nor when every tab is worked out again.
        _tabs.Refresh();
        Assert.True(a.OthersEditing);
        Assert.False(b.OthersEditing);
    }

    [Fact]
    public void A_tab_alone_is_never_marked_by_its_own_typing()
    {
        var (a, aTold) = Open();
        Pass(Moment);

        a.Typed();
        _tabs.Refresh();

        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public void A_tab_that_never_typed_marks_no_one()
    {
        var (a, _) = Open();
        var (b, _) = Open();
        Pass(TimeSpan.FromMinutes(1));

        _tabs.Refresh();

        Assert.False(a.OthersEditing);
        Assert.False(b.OthersEditing);
    }

    [Fact]
    public void Typing_before_a_tab_opened_does_not_mark_it()
    {
        var (a, _) = Open();
        Pass(Moment);
        a.Typed();
        Pass(Moment);
        var (b, _) = Open();
        _tabs.Refresh();

        Assert.False(b.OthersEditing);

        Pass(Moment);
        a.Typed();
        Assert.True(b.OthersEditing);
    }

    [Fact]
    public void Typing_tells_only_the_tabs_whose_notice_changed()
    {
        var (a, aTold) = Open();
        var (b, bTold) = Open();
        var (_, cTold) = Open();
        var (_, elsewhere) = Open(subject: OtherCampaign);
        Pass(Moment);

        a.Typed();
        Assert.Equal((0, 1, 1), (aTold.Count, bTold.Count, cTold.Count));

        Pass(Moment);
        a.Typed();
        Assert.Equal((0, 1, 1), (aTold.Count, bTold.Count, cTold.Count));

        b.Typed();
        Assert.True(a.OthersEditing);
        Assert.Equal((1, 1, 1), (aTold.Count, bTold.Count, cTold.Count));
        Assert.Equal(0, elsewhere.Count);
    }

    [Fact]
    public void A_tab_whose_circuit_drops_stops_counting_until_it_comes_back()
    {
        var (a, aTold) = Open();
        var circuit = Guid.NewGuid();
        var (b, _) = Open(circuit: circuit);
        var (aElsewhere, aElsewhereTold) = Open(subject: TabSubject.Look);
        var (bElsewhere, _) = Open(subject: TabSubject.Look, circuit: circuit);
        Pass(Moment);
        b.Typed();
        bElsewhere.Typed();
        Assert.True(a.OthersEditing);
        Assert.True(aElsewhere.OthersEditing);

        _tabs.SetConnected(circuit, false);
        Assert.False(a.OthersEditing);
        Assert.False(aElsewhere.OthersEditing);
        Assert.False(b.Connected);
        Assert.Equal((2, 2), (aTold.Count, aElsewhereTold.Count));

        _tabs.SetConnected(circuit, false);
        Assert.Equal((2, 2), (aTold.Count, aElsewhereTold.Count));

        _tabs.SetConnected(circuit, true);
        Assert.True(a.OthersEditing);
        Assert.True(b.Connected);
        Assert.Equal((3, 3), (aTold.Count, aElsewhereTold.Count));
    }

    [Fact]
    public void Closing_the_other_tab_clears_the_notice_and_says_so()
    {
        var (a, aTold) = Open();
        var (b, _) = Open();
        Pass(Moment);
        b.Typed();
        Assert.Equal(1, aTold.Count);

        b.Dispose();

        Assert.False(a.OthersEditing);
        Assert.Equal(2, aTold.Count);
    }

    [Fact]
    public void Refresh_clears_the_notice_once_the_other_tab_has_been_quiet_long_enough()
    {
        var (a, aTold) = Open();
        var (b, _) = Open();
        Pass(Moment);
        b.Typed();

        Pass(_settings.QuietAfter - Moment);
        _tabs.Refresh();
        Assert.True(a.OthersEditing);
        Assert.Equal(1, aTold.Count);

        Pass(Moment);
        _tabs.Refresh();
        Assert.False(a.OthersEditing);
        Assert.Equal(2, aTold.Count);

        _tabs.Refresh();
        Assert.Equal(2, aTold.Count);
    }

    [Fact]
    public void A_tab_on_another_instance_counts_while_its_row_is_fresh()
    {
        var (a, aTold) = Open();
        var remote = Guid.NewGuid();
        Pass(Moment);

        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(remote, _clock.Now, _clock.Now)]);
        Assert.True(a.OthersEditing);
        Assert.Equal(1, aTold.Count);

        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(remote, _clock.Now, _clock.Now)]);
        Assert.Equal(1, aTold.Count);

        // Its instance stopped writing the row: gone once the row is GoneAfter old.
        Pass(_settings.GoneAfter - Moment);
        _tabs.Refresh();
        Assert.True(a.OthersEditing);
        Pass(Moment);
        _tabs.Refresh();
        Assert.False(a.OthersEditing);
        Assert.Equal(2, aTold.Count);

        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(remote, _clock.Now - Moment, _clock.Now)]);
        Assert.True(a.OthersEditing);
        _tabs.ApplyRemote(Client, Campaign, []);
        Assert.False(a.OthersEditing);
        Assert.Equal(4, aTold.Count);
    }

    [Fact]
    public void A_remote_row_that_never_typed_or_typed_before_the_tab_opened_does_not_count()
    {
        var typedEarlier = _clock.Now;
        Pass(Moment);
        var (a, aTold) = Open();
        Pass(Moment);

        _tabs.ApplyRemote(Client, Campaign,
        [
            new RemoteTab(Guid.NewGuid(), null, _clock.Now),
            new RemoteTab(Guid.NewGuid(), typedEarlier, _clock.Now),
        ]);

        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public void A_remote_row_for_a_tab_of_this_instance_is_ignored()
    {
        var (a, aTold) = Open();
        var (open, _) = Open();
        var (closed, _) = Open();
        closed.Dispose();
        Pass(Moment);

        // The beat reads back this instance's own rows, and a closed tab's row until it deletes it.
        _tabs.ApplyRemote(Client, Campaign,
        [
            new RemoteTab(open.TabId, _clock.Now, _clock.Now),
            new RemoteTab(closed.TabId, _clock.Now, _clock.Now),
        ]);

        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public void The_beat_sees_each_open_tab_and_each_closed_one_once()
    {
        var circuit = Guid.NewGuid();
        var (a, _) = Open();
        var (b, _) = Open(subject: TabSubject.Look, circuit: circuit);
        Pass(Moment);
        a.Typed();
        _tabs.SetConnected(circuit, false);

        Assert.Equal(
            new[]
            {
                new LocalTab(Client, Campaign, a.TabId, _clock.Now, true),
                new LocalTab(Client, TabSubject.Look, b.TabId, null, false),
            }.OrderBy(t => t.TabId),
            _tabs.Snapshot().OrderBy(t => t.TabId));

        a.Dispose();
        a.Dispose();
        b.Dispose();

        Assert.Equal(
            new[] { new ClosedTab(Client, Campaign, a.TabId), new ClosedTab(Client, TabSubject.Look, b.TabId) },
            _tabs.DrainClosed());
        Assert.Empty(_tabs.DrainClosed());
    }

    [Fact]
    public async Task Waiting_for_tabs_ends_when_one_opens()
    {
        var waiting = _tabs.WaitForTabsAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        var (a, _) = Open();
        await waiting.WaitAsync(Eventually.Patience);
        Assert.True(_tabs.WaitForTabsAsync(CancellationToken.None).IsCompleted);

        a.Dispose();
        using var stop = new CancellationTokenSource();
        var again = _tabs.WaitForTabsAsync(stop.Token);
        Assert.False(again.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => again.WaitAsync(Eventually.Patience));
    }

    [Fact]
    public void A_tab_of_another_client_never_counts()
    {
        var (a, aTold) = Open();
        var (b, _) = Open(client: OtherClient);
        Pass(Moment);

        b.Typed();
        _tabs.ApplyRemote(OtherClient, Campaign, [new RemoteTab(Guid.NewGuid(), _clock.Now, _clock.Now)]);
        _tabs.Refresh();

        Assert.False(a.OthersEditing);
        Assert.True(b.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public void A_tab_that_throws_when_told_is_logged_by_type_and_stops_no_one()
    {
        var thrown = 0;
        var circuit = Guid.NewGuid();
        var failing = _tabs.Register(Client, Campaign, Guid.NewGuid(), () =>
        {
            thrown++;
            throw new InvalidOperationException("what she typed");
        });
        var (_, told) = Open();
        var (b, _) = Open(circuit: circuit);
        Pass(Moment);

        b.Typed();
        _tabs.SetConnected(circuit, false);
        _tabs.SetConnected(circuit, true);
        Pass(_settings.QuietAfter);
        _tabs.Refresh();
        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(Guid.NewGuid(), _clock.Now, _clock.Now)]);

        Assert.Equal(5, thrown);
        Assert.Equal(5, told.Count);
        Assert.True(failing.OthersEditing);
        var entries = _log.Entries;
        Assert.Equal(5, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(LogLevel.Warning, e.Level);
            Assert.Null(e.Error);
            Assert.Contains(nameof(InvalidOperationException), e.Message);
            Assert.DoesNotContain("what she typed", e.Message);
        });
    }

    [Fact]
    public void A_tab_is_told_outside_any_lock()
    {
        var reached = false;
        _ = _tabs.Register(Client, Campaign, Guid.NewGuid(), () =>
            reached = Task.Run(() => _tabs.Refresh()).Wait(Eventually.Patience));
        var (b, _) = Open();
        Pass(Moment);

        b.Typed();

        Assert.True(reached);
    }

    // A tab closes by joining the closed before it leaves the open. A beat that took it as closed (and
    // deleted its row) must not see it open in that moment, or it would write the row back.
    [Fact]
    public void A_tab_the_beat_took_as_closed_is_not_seen_open_while_it_closes()
    {
        var clock = new SteppingClock(_clock);
        var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var open = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        var closing = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });

        // A beat in the moment the tab is among the closed and not yet out of the open.
        IReadOnlyList<ClosedTab> taken = [];
        IReadOnlyList<LocalTab> seen = [];
        clock.OnNextRead = () =>
        {
            taken = tabs.DrainClosed();
            seen = tabs.Snapshot();
        };
        closing.Dispose();

        Assert.Equal([new ClosedTab(Client, Campaign, closing.TabId)], taken.ToArray());
        Assert.Equal([open.TabId], seen.Select(tab => tab.TabId).ToArray());
    }

    // Once the beat has taken a closed tab, a row of it read back (its delete failed, or a write of the
    // beat before landed after) is still this instance's own for GoneAfter, by which time the row is stale.
    [Fact]
    public void A_closed_tab_s_row_is_this_instance_s_own_for_GoneAfter_after_the_beat_takes_it()
    {
        var (a, aTold) = Open();
        var (closed, _) = Open();
        closed.Dispose();
        Pass(TimeSpan.FromMinutes(2));
        Assert.Single(_tabs.DrainClosed());

        // Past GoneAfter since it closed, short of it since the beat took it.
        Pass(TimeSpan.FromMinutes(2));
        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(closed.TabId, _clock.Now, _clock.Now)]);
        Assert.False(a.OthersEditing);
        Pass(_settings.GoneAfter - TimeSpan.FromMinutes(2) - Moment);
        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(closed.TabId, _clock.Now, _clock.Now)]);
        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);

        // Then forgotten: only a row written since, which nothing here writes, could count.
        Pass(Moment);
        _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(closed.TabId, _clock.Now, _clock.Now)]);
        Assert.True(a.OthersEditing);
    }

    // A tab that opens while another types, after the typing's own working-out looked at the tabs, is
    // worked out as it opens rather than at the next beat.
    [Fact]
    public void A_tab_opened_while_another_types_is_worked_out_as_it_opens()
    {
        var clock = new SteppingClock(_clock);
        var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var typist = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        Pass(Moment);

        // The other tab types after this one's opening time was read, before it is in.
        clock.OnNextRead = typist.Typed;
        var opening = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });

        Assert.True(typist.LastTyped > opening.OpenedAt);
        Assert.True(opening.OthersEditing);
    }

    [Fact]
    public void The_settings_default_to_the_plan_and_refuse_a_row_gone_before_the_next_beat()
    {
        var settings = new EditingTabsOptions();

        Assert.Equal(
            (TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3), TimeSpan.FromDays(1)),
            (settings.Interval, settings.QuietAfter, settings.GoneAfter, settings.SweepAfter));
        Assert.Equal("EditingTabs", EditingTabsOptions.Section);
        Assert.Null(settings.Problem());

        settings.GoneAfter = settings.Interval;
        Assert.NotNull(settings.Problem());
    }

    /// <summary>Counts how often a tab was told its notice changed.</summary>
    private sealed class Told
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Tell() => Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// The test clock, but the next reading first moves it on a second and runs <see cref="OnNextRead"/>,
    /// then answers the time from before: something happening between a reading and what follows it.
    /// </summary>
    private sealed class SteppingClock(ManualClock clock) : TimeProvider
    {
        public Action? OnNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var now = clock.Now;
            if (OnNextRead is { } then)
            {
                OnNextRead = null;
                clock.Now = now + Moment;
                then();
            }
            return now;
        }
    }
}
