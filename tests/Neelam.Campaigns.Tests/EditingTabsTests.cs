using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The editor tabs open on this instance (<see cref="EditingTabs"/>): which of them is also being edited in
/// another tab, on this instance or (by the rows the beat reads) another, and who is told when that changes.
/// The clock moves only when the test moves it. The notices are worked out off the test's thread, by the
/// registry's loop, so a test awaits the Task a call hands back, or <see cref="EditingTabs.SettledAsync"/>
/// after a tab opens or closes, before it reads a notice or a count.
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
    public async Task A_closed_tab_is_told_nothing()
    {
        var (a, aTold) = Open();
        var (b, _) = Open();
        var circuit = Guid.NewGuid();
        var (c, _) = Open(circuit: circuit);
        a.Dispose();
        Pass(Moment);

        await b.Typed();
        await c.Typed();
        await _tabs.SetConnected(circuit, false);
        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(Guid.NewGuid(), _clock.Now, _clock.Now)]);
        await _tabs.Refresh();

        Assert.Equal(0, aTold.Count);
    }

    // Marked by the caller, before anything is worked out: nothing to await.
    [Fact]
    public void Typing_marks_the_time()
    {
        var (a, _) = Open();
        Assert.Null(a.LastTyped);

        Pass(TimeSpan.FromMinutes(1));
        _ = a.Typed();

        Assert.Equal(_clock.Now, a.LastTyped);
    }

    [Fact]
    public async Task Another_tab_typing_since_this_one_opened_marks_it_and_not_the_typist()
    {
        var (a, _) = Open();
        var (b, _) = Open();
        Pass(Moment);

        await b.Typed();

        Assert.True(a.OthersEditing);
        Assert.False(b.OthersEditing);

        // Nor when every tab is worked out again.
        await _tabs.Refresh();
        Assert.True(a.OthersEditing);
        Assert.False(b.OthersEditing);
    }

    [Fact]
    public async Task A_tab_alone_is_never_marked_by_its_own_typing()
    {
        var (a, aTold) = Open();
        Pass(Moment);

        await a.Typed();
        await _tabs.Refresh();

        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public async Task A_tab_that_never_typed_marks_no_one()
    {
        var (a, _) = Open();
        var (b, _) = Open();
        Pass(TimeSpan.FromMinutes(1));

        await _tabs.Refresh();

        Assert.False(a.OthersEditing);
        Assert.False(b.OthersEditing);
    }

    [Fact]
    public async Task Typing_before_a_tab_opened_does_not_mark_it()
    {
        var (a, _) = Open();
        Pass(Moment);
        await a.Typed();
        Pass(Moment);
        var (b, _) = Open();
        await _tabs.Refresh();

        Assert.False(b.OthersEditing);

        Pass(Moment);
        await a.Typed();
        Assert.True(b.OthersEditing);
    }

    [Fact]
    public async Task Typing_tells_only_the_tabs_whose_notice_changed()
    {
        var (a, aTold) = Open();
        var (b, bTold) = Open();
        var (_, cTold) = Open();
        var (_, elsewhere) = Open(subject: OtherCampaign);
        Pass(Moment);

        await a.Typed();
        Assert.Equal((0, 1, 1), (aTold.Count, bTold.Count, cTold.Count));

        Pass(Moment);
        await a.Typed();
        Assert.Equal((0, 1, 1), (aTold.Count, bTold.Count, cTold.Count));

        await b.Typed();
        Assert.True(a.OthersEditing);
        Assert.Equal((1, 1, 1), (aTold.Count, bTold.Count, cTold.Count));
        Assert.Equal(0, elsewhere.Count);
    }

    [Fact]
    public async Task A_tab_whose_circuit_drops_stops_counting_until_it_comes_back()
    {
        var (a, aTold) = Open();
        var circuit = Guid.NewGuid();
        var (b, _) = Open(circuit: circuit);
        var (aElsewhere, aElsewhereTold) = Open(subject: TabSubject.Look);
        var (bElsewhere, _) = Open(subject: TabSubject.Look, circuit: circuit);
        Pass(Moment);
        await b.Typed();
        await bElsewhere.Typed();
        Assert.True(a.OthersEditing);
        Assert.True(aElsewhere.OthersEditing);

        await _tabs.SetConnected(circuit, false);
        Assert.False(a.OthersEditing);
        Assert.False(aElsewhere.OthersEditing);
        Assert.False(b.Connected);
        Assert.Equal((2, 2), (aTold.Count, aElsewhereTold.Count));

        await _tabs.SetConnected(circuit, false);
        Assert.Equal((2, 2), (aTold.Count, aElsewhereTold.Count));

        await _tabs.SetConnected(circuit, true);
        Assert.True(a.OthersEditing);
        Assert.True(b.Connected);
        Assert.Equal((3, 3), (aTold.Count, aElsewhereTold.Count));
    }

    [Fact]
    public async Task Closing_the_other_tab_clears_the_notice_and_says_so()
    {
        var (a, aTold) = Open();
        var (b, _) = Open();
        Pass(Moment);
        await b.Typed();
        Assert.Equal(1, aTold.Count);

        b.Dispose();
        await _tabs.SettledAsync();

        Assert.False(a.OthersEditing);
        Assert.Equal(2, aTold.Count);
    }

    [Fact]
    public async Task Refresh_clears_the_notice_once_the_other_tab_has_been_quiet_long_enough()
    {
        var (a, aTold) = Open();
        var (b, _) = Open();
        Pass(Moment);
        await b.Typed();

        Pass(_settings.QuietAfter - Moment);
        await _tabs.Refresh();
        Assert.True(a.OthersEditing);
        Assert.Equal(1, aTold.Count);

        Pass(Moment);
        await _tabs.Refresh();
        Assert.False(a.OthersEditing);
        Assert.Equal(2, aTold.Count);

        await _tabs.Refresh();
        Assert.Equal(2, aTold.Count);
    }

    [Fact]
    public async Task A_tab_on_another_instance_counts_while_its_row_is_fresh()
    {
        var (a, aTold) = Open();
        var remote = Guid.NewGuid();
        Pass(Moment);

        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(remote, _clock.Now, _clock.Now)]);
        Assert.True(a.OthersEditing);
        Assert.Equal(1, aTold.Count);

        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(remote, _clock.Now, _clock.Now)]);
        Assert.Equal(1, aTold.Count);

        // Its instance stopped writing the row: gone once the row is GoneAfter old.
        Pass(_settings.GoneAfter - Moment);
        await _tabs.Refresh();
        Assert.True(a.OthersEditing);
        Pass(Moment);
        await _tabs.Refresh();
        Assert.False(a.OthersEditing);
        Assert.Equal(2, aTold.Count);

        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(remote, _clock.Now - Moment, _clock.Now)]);
        Assert.True(a.OthersEditing);
        await _tabs.ApplyRemote(Client, Campaign, []);
        Assert.False(a.OthersEditing);
        Assert.Equal(4, aTold.Count);
    }

    [Fact]
    public async Task A_remote_row_that_never_typed_or_typed_before_the_tab_opened_does_not_count()
    {
        var typedEarlier = _clock.Now;
        Pass(Moment);
        var (a, aTold) = Open();
        Pass(Moment);

        await _tabs.ApplyRemote(Client, Campaign,
        [
            new RemoteTab(Guid.NewGuid(), null, _clock.Now),
            new RemoteTab(Guid.NewGuid(), typedEarlier, _clock.Now),
        ]);

        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public async Task A_remote_row_for_a_tab_of_this_instance_is_ignored()
    {
        var (a, aTold) = Open();
        var (open, _) = Open();
        var (closed, _) = Open();
        closed.Dispose();
        Pass(Moment);

        // The beat reads back this instance's own rows, and a closed tab's row until it deletes it.
        await _tabs.ApplyRemote(Client, Campaign,
        [
            new RemoteTab(open.TabId, _clock.Now, _clock.Now),
            new RemoteTab(closed.TabId, _clock.Now, _clock.Now),
        ]);

        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    // What the beat reads is changed by the caller, before anything is worked out: nothing to await.
    [Fact]
    public void The_beat_sees_each_open_tab_and_each_closed_one_once()
    {
        var circuit = Guid.NewGuid();
        var (a, _) = Open();
        var (b, _) = Open(subject: TabSubject.Look, circuit: circuit);
        Pass(Moment);
        _ = a.Typed();
        _ = _tabs.SetConnected(circuit, false);

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
    public async Task A_tab_of_another_client_never_counts()
    {
        var (a, aTold) = Open();
        var (b, _) = Open(client: OtherClient);
        Pass(Moment);

        await b.Typed();
        await _tabs.ApplyRemote(OtherClient, Campaign, [new RemoteTab(Guid.NewGuid(), _clock.Now, _clock.Now)]);
        await _tabs.Refresh();

        Assert.False(a.OthersEditing);
        Assert.True(b.OthersEditing);
        Assert.Equal(0, aTold.Count);
    }

    [Fact]
    public async Task A_tab_that_throws_when_told_is_logged_by_type_and_stops_no_one()
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

        await b.Typed();
        await _tabs.SetConnected(circuit, false);
        await _tabs.SetConnected(circuit, true);
        Pass(_settings.QuietAfter);
        await _tabs.Refresh();
        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(Guid.NewGuid(), _clock.Now, _clock.Now)]);

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

    // A tab is told on the thread that works the notices out. A call back in from there could only wait on
    // the loop that is running it, so it is refused before it changes anything, as the tab's own failure:
    // logged by type, and the loop goes on.
    [Fact]
    public async Task A_tab_told_that_posts_is_refused_and_the_loop_goes_on()
    {
        EditingTab? told = null;
        Exception? refused = null;
        Exception? closeRefused = null;
        Task? posted = null;
        told = _tabs.Register(Client, Campaign, Guid.NewGuid(), () =>
        {
            try
            {
                told!.Dispose();
            }
            catch (Exception ex)
            {
                closeRefused = ex;
            }
            try
            {
                posted = _tabs.Refresh();
            }
            catch (Exception ex)
            {
                refused = ex;
                throw;
            }
        });
        var (b, _) = Open();
        Pass(Moment);

        await b.Typed();

        Assert.IsType<InvalidOperationException>(refused);
        Assert.Null(posted);
        Assert.IsType<InvalidOperationException>(closeRefused);
        Assert.Contains(_tabs.Snapshot(), tab => tab.TabId == told.TabId);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);

        // The loop goes on, and off it the same calls are taken.
        Assert.True(told.OthersEditing);
        Pass(_settings.QuietAfter);
        await _tabs.Refresh();
        Assert.False(told.OthersEditing);
        told.Dispose();
        Assert.DoesNotContain(_tabs.Snapshot(), tab => tab.TabId == told.TabId);
    }

    // A tab closes by joining the closed before it leaves the open. A beat that took it as closed (and
    // deleted its row) must not see it open in that moment, or it would write the row back.
    [Fact]
    public async Task A_tab_the_beat_took_as_closed_is_not_seen_open_while_it_closes()
    {
        var clock = new SteppingClock(_clock);
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var open = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        var closing = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        // Their opening worked out, so the next reading of the clock is the closing's own.
        await tabs.SettledAsync();

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
    public async Task A_closed_tab_s_row_is_this_instance_s_own_for_GoneAfter_after_the_beat_takes_it()
    {
        var (a, aTold) = Open();
        var (closed, _) = Open();
        closed.Dispose();
        Pass(TimeSpan.FromMinutes(2));
        Assert.Single(_tabs.DrainClosed());

        // Past GoneAfter since it closed, short of it since the beat took it.
        Pass(TimeSpan.FromMinutes(2));
        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(closed.TabId, _clock.Now, _clock.Now)]);
        Assert.False(a.OthersEditing);
        Pass(_settings.GoneAfter - TimeSpan.FromMinutes(2) - Moment);
        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(closed.TabId, _clock.Now, _clock.Now)]);
        Assert.False(a.OthersEditing);
        Assert.Equal(0, aTold.Count);

        // Then forgotten: only a row written since, which nothing here writes, could count.
        Pass(Moment);
        await _tabs.ApplyRemote(Client, Campaign, [new RemoteTab(closed.TabId, _clock.Now, _clock.Now)]);
        Assert.True(a.OthersEditing);
    }

    // A tab that opens while another types, after the typing's own working-out looked at the tabs, is
    // worked out as it opens rather than at the next beat.
    [Fact]
    public async Task A_tab_opened_while_another_types_is_worked_out_as_it_opens()
    {
        var clock = new SteppingClock(_clock);
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var typist = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        // Its opening worked out, so the next reading of the clock is the other tab's opening time.
        await tabs.SettledAsync();
        Pass(Moment);

        // The other tab types after this one's opening time was read, before it is in.
        clock.OnNextRead = () => _ = typist.Typed();
        var opening = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        await opening.Opened;

        Assert.True(typist.LastTyped > opening.OpenedAt);
        Assert.True(opening.OthersEditing);
    }

    // ---- The loop that works the notices out ----

    // Everything waiting when the loop wakes is one working-out, against what the tabs hold by then: three
    // keystrokes posted while it was busy read the clock once between them, and tell the other tab once.
    [Fact]
    public async Task A_burst_is_one_working_out_with_the_newest_typing()
    {
        var clock = new WatchedClock(_clock);
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var told = new Told();
        var a = tabs.Register(Client, Campaign, Guid.NewGuid(), told.Tell);
        var b = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        await tabs.SettledAsync();
        Pass(Moment);

        // The loop is held inside one working-out while the keystrokes arrive.
        var hold = new Hold();
        clock.OnNextRead = hold.Here;
        var held = tabs.Refresh();
        await hold.Reached.WaitAsync(Eventually.Patience);
        var first = b.Typed();
        // The first keystroke alone would be quiet by the time it is worked out.
        Pass(_settings.QuietAfter);
        var second = b.Typed();
        var third = b.Typed();
        var reads = clock.Reads;
        hold.LetGo();
        await Task.WhenAll(held, first, second, third).WaitAsync(Eventually.Patience);

        Assert.Equal(reads + 1, clock.Reads);
        Assert.Equal(_clock.Now, b.LastTyped);
        Assert.True(a.OthersEditing);
        Assert.Equal(1, told.Count);
    }

    // A token cancels a post the loop has not taken: its Task is cancelled at once, without the loop, and
    // what it carried never happens. One already cancelled is refused the same way.
    [Fact]
    public async Task A_post_cancelled_before_the_loop_takes_it_did_not_happen()
    {
        var clock = new WatchedClock(_clock);
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var told = new Told();
        var a = tabs.Register(Client, Campaign, Guid.NewGuid(), told.Tell);
        await tabs.SettledAsync();
        Pass(Moment);
        var hold = new Hold();
        clock.OnNextRead = hold.Here;
        var held = tabs.Refresh();
        await hold.Reached.WaitAsync(Eventually.Patience);

        using var stop = new CancellationTokenSource();
        var applied = tabs.ApplyRemote(Client, Campaign, [new RemoteTab(Guid.NewGuid(), _clock.Now, _clock.Now)], stop.Token);
        Assert.False(applied.IsCompleted);
        stop.Cancel();
        // While the loop is still held.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applied.WaitAsync(Eventually.Patience));
        Assert.True(applied.IsCanceled);
        Assert.True(tabs.Refresh(stop.Token).IsCanceled);

        hold.LetGo();
        await held.WaitAsync(Eventually.Patience);
        await tabs.SettledAsync();
        await tabs.Refresh();
        Assert.False(a.OthersEditing);
        Assert.Equal(0, told.Count);
    }

    // Once the loop has taken a post, its token changes nothing: the Task says what happened, which is that
    // the notices were worked out.
    [Fact]
    public async Task A_post_cancelled_after_the_loop_took_it_reports_what_happened()
    {
        var clock = new WatchedClock(_clock);
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var told = new Told();
        var a = tabs.Register(Client, Campaign, Guid.NewGuid(), told.Tell);
        var b = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        Pass(Moment);
        await b.Typed();
        Assert.True(a.OthersEditing);
        Pass(_settings.QuietAfter);

        var hold = new Hold();
        clock.OnNextRead = hold.Here;
        using var stop = new CancellationTokenSource();
        var refreshed = tabs.Refresh(stop.Token);
        await hold.Reached.WaitAsync(Eventually.Patience);
        stop.Cancel();
        hold.LetGo();

        await refreshed.WaitAsync(Eventually.Patience);
        Assert.True(refreshed.IsCompletedSuccessfully);
        Assert.False(a.OthersEditing);
        Assert.Equal(2, told.Count);
    }

    // A working-out that fails: its posts fail with it, it is logged by the failure's type alone, and the
    // loop takes the next post as ever.
    [Fact]
    public async Task A_working_out_that_fails_fails_its_posts_is_logged_by_type_and_the_loop_goes_on()
    {
        var clock = new WatchedClock(_clock);
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var a = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        var b = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        await tabs.SettledAsync();
        Pass(Moment);

        clock.OnNextRead = () => throw new InvalidOperationException("what she typed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tabs.Refresh().WaitAsync(Eventually.Patience));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Error);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        Assert.DoesNotContain("what she typed", entry.Message);

        await b.Typed();
        Assert.True(a.OthersEditing);
        Assert.Single(_log.Entries);
    }

    // The loop itself ending is a bug nothing caught (here the log failing as a working-out's failure is
    // reported). What it had taken and what still waited both fail rather than wait for ever; it is logged
    // as an error by type; and the next post builds a loop, which works every tab out before anything else.
    [Fact]
    public async Task A_loop_that_ends_fails_what_it_held_and_the_next_post_is_served_by_a_fresh_one()
    {
        var clock = new WatchedClock(_clock);
        var log = new BreakableLog();
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), log);
        var told = new Told();
        var a = tabs.Register(Client, Campaign, Guid.NewGuid(), told.Tell);
        var b = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        Pass(Moment);
        await b.Typed();
        Assert.True(a.OthersEditing);
        // Quiet long enough from here, which only a working-out finds.
        Pass(_settings.QuietAfter);

        var hold = new Hold();
        clock.OnNextRead = () =>
        {
            hold.Here();
            throw new InvalidOperationException("A bug.");
        };
        log.Breaks = true;
        var taken = tabs.Refresh();
        await hold.Reached.WaitAsync(Eventually.Patience);
        var waiting = tabs.SettledAsync();
        hold.LetGo();

        await Assert.ThrowsAsync<NotSupportedException>(() => taken.WaitAsync(Eventually.Patience));
        await Assert.ThrowsAsync<NotSupportedException>(() => waiting.WaitAsync(Eventually.Patience));
        log.Breaks = false;
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Null(entry.Error);
        Assert.Contains(nameof(NotSupportedException), entry.Message);
        Assert.DoesNotContain(BreakableLog.Words, entry.Message);
        Assert.True(a.OthersEditing);

        // A post that asks for no tab at all: the fresh loop's first working-out takes every tab anyway.
        await tabs.SettledAsync().WaitAsync(Eventually.Patience);
        Assert.False(a.OthersEditing);
        Assert.Equal(2, told.Count);
        Pass(Moment);
        await b.Typed();
        Assert.True(a.OthersEditing);
        Assert.Single(log.Entries);
    }

    // Ended as often as allowed within the window, the loop is not built again: Critical is logged once, no
    // tab is marked or told from then on, and a post completes at once. Ends further apart than the window
    // are each followed by a fresh loop.
    [Fact]
    public async Task A_loop_that_keeps_ending_is_not_built_again_and_the_notices_go_off()
    {
        var clock = new WatchedClock(_clock);
        var log = new BreakableLog();
        await using var tabs = new EditingTabs(clock, Options.Create(_settings), log);
        var told = new Told();
        var a = tabs.Register(Client, Campaign, Guid.NewGuid(), told.Tell);
        var b = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        await tabs.SettledAsync();

        // One fewer than allowed, then the window passes, then one more: still built again.
        for (var ended = 1; ended < EditingTabs.DeathsAllowed; ended++) await EndLoop(tabs, clock, log);
        Pass(EditingTabs.DeathWindow + Moment);
        await EndLoop(tabs, clock, log);
        await b.Typed();
        Assert.True(a.OthersEditing);
        Assert.Equal(1, told.Count);
        Assert.Equal(EditingTabs.DeathsAllowed, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal(LogLevel.Error, e.Level));

        // With the one just now, as many as allowed within the window.
        for (var ended = 1; ended < EditingTabs.DeathsAllowed; ended++) await EndLoop(tabs, clock, log);

        var critical = Assert.Single(log.Entries, e => e.Level == LogLevel.Critical);
        Assert.Null(critical.Error);
        Assert.Contains(nameof(NotSupportedException), critical.Message);
        Assert.DoesNotContain(BreakableLog.Words, critical.Message);
        Assert.False(a.OthersEditing);
        Pass(Moment);
        var reads = clock.Reads;
        Assert.True(b.Typed().IsCompletedSuccessfully);
        Assert.True(tabs.Refresh().IsCompletedSuccessfully);
        Assert.True(tabs.SettledAsync().IsCompletedSuccessfully);
        // Only the typing's own reading of the clock: nothing is worked out.
        Assert.Equal(reads + 1, clock.Reads);
        Assert.False(a.OthersEditing);
        Assert.Equal(1, told.Count);
        // The tabs themselves go on: what the beat writes is still true.
        Assert.Equal(_clock.Now, b.LastTyped);
        Assert.Equal(2, tabs.Snapshot().Count);
        Assert.Equal(2 * EditingTabs.DeathsAllowed - 1, log.Entries.Count);
    }

    // The app stopping: what the loop had taken is finished, what still waited is cancelled, and every call
    // after it is handed a cancelled Task. None throws, as a page closing later in the shutdown must not.
    [Fact]
    public async Task Disposing_finishes_what_was_taken_and_cancels_what_waits_and_every_post_after_it()
    {
        var clock = new WatchedClock(_clock);
        var tabs = new EditingTabs(clock, Options.Create(_settings), _log);
        var circuit = Guid.NewGuid();
        var a = tabs.Register(Client, Campaign, circuit, () => { });
        var b = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        await tabs.SettledAsync();
        var hold = new Hold();
        clock.OnNextRead = hold.Here;
        var taken = tabs.Refresh();
        await hold.Reached.WaitAsync(Eventually.Patience);
        var waiting = tabs.Refresh();

        var disposing = tabs.DisposeAsync().AsTask();
        hold.LetGo();
        await disposing.WaitAsync(Eventually.Patience);

        Assert.True(taken.IsCompletedSuccessfully);
        Assert.True(waiting.IsCanceled);

        Assert.True(tabs.Refresh().IsCanceled);
        Assert.True(b.Typed().IsCanceled);
        Assert.True(tabs.SetConnected(circuit, false).IsCanceled);
        Assert.True(tabs.ApplyRemote(Client, Campaign, []).IsCanceled);
        Assert.True(tabs.SettledAsync().IsCanceled);
        var late = tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        Assert.True(late.Opened.IsCanceled);
        late.Dispose();
        a.Dispose();
        Assert.Equal([b.TabId], tabs.Snapshot().Select(tab => tab.TabId).ToArray());
        await tabs.DisposeAsync();
        Assert.Empty(_log.Entries);
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

    // Ends the loop, and returns once it has: a working-out fails on the clock, and the log fails as that is
    // reported, which nothing catches. The post that was being worked out fails only after the end is recorded.
    private static async Task EndLoop(EditingTabs tabs, WatchedClock clock, BreakableLog log)
    {
        clock.OnNextRead = () => throw new InvalidOperationException("A bug.");
        log.Breaks = true;
        await Assert.ThrowsAsync<NotSupportedException>(() => tabs.Refresh().WaitAsync(Eventually.Patience));
        log.Breaks = false;
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

    /// <summary>
    /// The test clock, counting its readings; the next one first runs <see cref="OnNextRead"/>, once, on the
    /// thread that reads: a place to hold the loop inside a working-out, or to fail one. Set while the loop
    /// is idle and before a post whose caller reads no clock, so the reading is the working-out's own.
    /// </summary>
    private sealed class WatchedClock(ManualClock clock) : TimeProvider
    {
        private Action? _onNextRead;
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Action? OnNextRead
        {
            set => Volatile.Write(ref _onNextRead, value);
        }

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _reads);
            Interlocked.Exchange(ref _onNextRead, null)?.Invoke();
            return clock.Now;
        }

        public override long GetTimestamp() => clock.GetTimestamp();

        public override long TimestampFrequency => clock.TimestampFrequency;
    }

    /// <summary>
    /// Holds whatever thread reaches <see cref="Here"/> until the test lets it go, and says when it got
    /// there: the test then acts knowing where that thread stands, without waiting on time.
    /// </summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _letGo = new();

        public Task Reached => _reached.Task;

        public void Here()
        {
            _reached.TrySetResult();
            // Patience is a guard against a hang, never what is waited for.
            if (!_letGo.Wait(Eventually.Patience)) throw new TimeoutException("Never let go.");
        }

        public void LetGo() => _letGo.Set();
    }

    /// <summary>A log that keeps what it is told, and while <see cref="Breaks"/> throws at a warning instead.</summary>
    private sealed class BreakableLog : ILogger<EditingTabs>
    {
        public const string Words = "The log is broken.";

        private readonly ListLogger<EditingTabs> _kept = new();
        private int _breaks;

        public bool Breaks
        {
            set => Volatile.Write(ref _breaks, value ? 1 : 0);
        }

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Error)> Entries => _kept.Entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && Volatile.Read(ref _breaks) != 0) throw new NotSupportedException(Words);
            _kept.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
