using System.Collections.Concurrent;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The editing tabs' beat (<see cref="EditingTabsBeat"/>; owner, 2026-11-05: the table is "cheap insurance.
/// One minute intervals only while someone is active"). While no tab is open it touches nothing; while one
/// is, at once and then each interval, it deletes the closed tabs' rows, writes the connected tabs' rows,
/// reads each subject's rows back into the registry and works every notice out again. A failure is logged
/// and carried past. The clock moves only when the test moves it.
/// </summary>
public class EditingTabsBeatTests
{
    private static readonly ClientName Client = TestRecords.DefaultClient;
    private static readonly TabSubject Campaign = TabSubject.Campaign(Guid.NewGuid());
    private static readonly TabSubject Template = TabSubject.Template(Guid.NewGuid());
    private static readonly TimeSpan Moment = TimeSpan.FromSeconds(1);

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    // A sweep every ten minutes rather than a day, so a test can reach the next.
    private readonly EditingTabsOptions _settings = new() { SweepAfter = TimeSpan.FromMinutes(10) };
    private readonly ListLogger<EditingTabsBeat> _log = new();
    private readonly EditingTabs _tabs;
    private readonly WatchedTabRows _rows;

    public EditingTabsBeatTests()
    {
        _tabs = new EditingTabs(_clock, Options.Create(_settings), new ListLogger<EditingTabs>());
        _rows = new WatchedTabRows(_clock);
    }

    private EditingTabsBeat Beat() => new(_tabs, _rows, _clock, Options.Create(_settings), _log);

    private EditingTab Open(TabSubject? subject = null, Guid? circuit = null, Action? changed = null) =>
        _tabs.Register(Client, subject ?? Campaign, circuit ?? Guid.NewGuid(), changed ?? (() => { }));

    private static string Write(TabSubject subject, EditingTab tab) => $"write {subject} {tab.TabId:N}";

    private static string Delete(TabSubject subject, EditingTab tab) => $"delete {subject} {tab.TabId:N}";

    private static string List(TabSubject subject) => $"list {subject}";

    private int Lists => _rows.Calls.Count(call => call.StartsWith("list ", StringComparison.Ordinal));

    // A beat has ended once the beat waits on the clock for the next one and done holds.
    private Task Beaten(Func<bool> done, string what) =>
        Eventually.Until(() => done() && _clock.Waiting == 1, what, _rows.Changed, _clock.Changed);

    // ---- Only while someone is active ----

    // No tab open: no store call however long it waits, and no timer set.
    [Fact]
    public async Task With_no_tab_open_it_makes_no_store_call()
    {
        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);

        for (var i = 0; i < 60; i++) _clock.Advance(_settings.Interval);

        Assert.Empty(_rows.Calls);
        Assert.Equal(0, _clock.Waiting);
        await beat.StopAsync(CancellationToken.None).WaitAsync(Eventually.Patience);
        Assert.Empty(_rows.Calls);
    }

    // The first tab wakes it at once; it beats each interval and not between; when the last tab closes it
    // deletes the row at the next beat and goes back to waiting, so the next tab wakes it at once again.
    [Fact]
    public async Task A_tab_opening_wakes_it_at_once_and_it_beats_each_interval_until_the_last_closes()
    {
        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);

        var a = Open();
        await Beaten(() => Lists == 1, "the first beat");
        Assert.Equal([Write(Campaign, a), List(Campaign), "sweep"], _rows.Calls);

        var fired = _clock.Fired;
        _clock.Advance(_settings.Interval - Moment);
        Assert.Equal(fired, _clock.Fired);
        Assert.Equal(3, _rows.Calls.Length);

        _clock.Advance(Moment);
        await Beaten(() => Lists == 2, "the second beat");
        Assert.Equal([Write(Campaign, a), List(Campaign), "sweep", Write(Campaign, a), List(Campaign)], _rows.Calls);

        a.Dispose();
        _clock.Advance(_settings.Interval);
        await Eventually.Until(() => _rows.Calls.Length == 6 && _clock.Waiting == 0, "the last row deleted", _rows.Changed, _clock.Changed);
        Assert.Equal(Delete(Campaign, a), _rows.Calls[^1]);
        Assert.Empty(_rows.Table.Rows);

        // Waiting for a tab again, not on the clock: it wakes for the next tab without the clock moving.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(6, _rows.Calls.Length);
        var b = Open();
        await Beaten(() => Lists == 3, "the beat for the next tab");
        Assert.Equal([Write(Campaign, b), List(Campaign)], _rows.Calls.Skip(6).ToArray());
        await beat.StopAsync(CancellationToken.None);
    }

    // ---- What a beat does, in order ----

    // Closed tabs' rows deleted first; then, subject by subject, a row for each connected tab and the
    // subject's rows read back, another instance's tab counting here; the sweep last.
    [Fact]
    public async Task Each_beat_deletes_writes_the_connected_tabs_and_reads_each_subject_back()
    {
        var dropped = Guid.NewGuid();
        var a = Open();
        var told = 0;
        var watching = Open(changed: () => Interlocked.Increment(ref told));
        var b = Open(circuit: dropped);
        var t = Open(subject: Template);
        var closed = Open();
        _tabs.SetConnected(dropped, false);
        _clock.Now += Moment;
        a.Typed();
        var elsewhere = Guid.NewGuid();
        await _rows.Table.WriteAsync(Client, Template, elsewhere, _clock.Now);
        closed.Dispose();
        Assert.False(t.OthersEditing);

        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);
        await Beaten(() => Lists == 2, "the first beat");

        var calls = _rows.Calls;
        Assert.Equal(Delete(Campaign, closed), calls[0]);
        string[] campaign = [Write(Campaign, a), Write(Campaign, watching), List(Campaign)];
        Assert.Equal(campaign.Order(), calls.Skip(1).Take(5).Where(call => call.Contains(Campaign.ToString())).Order());
        var middle = calls.Skip(1).Take(5).ToList();
        Assert.True(middle.IndexOf(List(Campaign)) > middle.IndexOf(Write(Campaign, a)));
        Assert.True(middle.IndexOf(List(Campaign)) > middle.IndexOf(Write(Campaign, watching)));
        Assert.True(middle.IndexOf(List(Template)) > middle.IndexOf(Write(Template, t)));
        Assert.Equal("sweep", calls[6]);
        Assert.Equal(7, calls.Length);

        // The dropped tab has no row, so other instances treat it as gone; the closed one's is deleted.
        var rows = _rows.Table.Rows.Select(row => (row.RowKey, LastTyped: row.GetDateTimeOffset("LastTyped"))).ToList();
        Assert.Equal(
            new (string, DateTimeOffset?)[]
            {
                (a.TabId.ToString("N"), _clock.Now), (watching.TabId.ToString("N"), null), (t.TabId.ToString("N"), null),
                (elsewhere.ToString("N"), _clock.Now),
            }.Order(),
            rows.Order());

        // The tab on another instance counts here, and this instance's own rows read back do not.
        Assert.True(t.OthersEditing);
        Assert.True(watching.OthersEditing);
        Assert.Equal(1, told);
        Assert.False(a.OthersEditing);
        await beat.StopAsync(CancellationToken.None);
    }

    // A subject that cannot be read is still worked out against the clock at the end of each beat: the
    // other tab's typing goes quiet and the notice clears.
    [Fact]
    public async Task Every_notice_is_worked_out_again_each_beat_even_when_its_rows_cannot_be_read()
    {
        _rows.Fails = call => call.StartsWith("list ", StringComparison.Ordinal) ? Busy() : null;
        var a = Open();
        var b = Open();
        _clock.Now += Moment;
        b.Typed();
        Assert.True(a.OthersEditing);

        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);
        await Beaten(() => Lists == 1, "the first beat");
        for (var beats = 2; _clock.Now - b.LastTyped < _settings.QuietAfter; beats++)
        {
            Assert.True(a.OthersEditing);
            _clock.Advance(_settings.Interval);
            var expected = beats;
            await Beaten(() => Lists == expected, "the next beat");
        }

        Assert.False(a.OthersEditing);
        await beat.StopAsync(CancellationToken.None);
    }

    // ---- Sweep ----

    // The first beat sweeps rows older than SweepAfter, whoever's; then at most once per SweepAfter,
    // across going quiet and waking again.
    [Fact]
    public async Task It_sweeps_on_its_first_beat_and_then_at_most_once_per_SweepAfter()
    {
        var other = new ClientName("test-salon-two");
        await _rows.Table.WriteAsync(other, Campaign, Guid.NewGuid(), null);
        _clock.Now += _settings.SweepAfter + Moment;

        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);
        var a = Open();
        await Beaten(() => Lists == 1, "the first beat");
        Assert.Equal([_clock.Now - _settings.SweepAfter], _rows.Swept);
        Assert.Equal([a.TabId.ToString("N")], _rows.Table.Rows.Select(row => row.RowKey).ToArray());

        // Not again until SweepAfter has passed since.
        var lists = 1;
        for (var minute = 1; minute < 10; minute++)
        {
            _clock.Advance(_settings.Interval);
            var expected = ++lists;
            await Beaten(() => Lists == expected, "a beat");
        }
        Assert.Single(_rows.Swept);
        _clock.Advance(_settings.Interval);
        await Beaten(() => Lists == 11, "the beat SweepAfter later");
        Assert.Equal(2, _rows.Swept.Length);
        Assert.Equal(_clock.Now - _settings.SweepAfter, _rows.Swept[^1]);

        // Quiet, then woken within SweepAfter of the last sweep: no sweep on that first beat.
        a.Dispose();
        _clock.Advance(_settings.Interval);
        await Eventually.Until(() => _clock.Waiting == 0 && _rows.Calls[^1] == Delete(Campaign, a), "the last row deleted", _rows.Changed, _clock.Changed);
        var b = Open();
        await Beaten(() => Lists == 12, "the beat for the next tab");
        Assert.Equal(2, _rows.Swept.Length);

        // Quiet past SweepAfter, then woken: the first beat sweeps.
        b.Dispose();
        _clock.Advance(_settings.Interval);
        await Eventually.Until(() => _clock.Waiting == 0 && _rows.Calls[^1] == Delete(Campaign, b), "the last row deleted", _rows.Changed, _clock.Changed);
        _clock.Now += _settings.SweepAfter;
        Open();
        await Beaten(() => Lists == 13, "the beat after a long quiet");
        Assert.Equal(3, _rows.Swept.Length);
        await beat.StopAsync(CancellationToken.None);
    }

    // ---- Never in the way ----

    // Each failing call is logged by type, status and code and the beat goes on: the next subject, the
    // sweep, the next beat.
    [Fact]
    public async Task A_failing_store_call_is_logged_and_the_beat_carries_on()
    {
        var a = Open();
        var t = Open(subject: Template);
        var closed = Open();
        closed.Dispose();
        await _rows.Table.WriteAsync(Client, Template, Guid.NewGuid(), _clock.Now + Moment);
        _rows.Fails = call =>
            call == Delete(Campaign, closed) || call == Write(Campaign, a) || call == "sweep" ? Busy()
            : call == List(Campaign) ? new InvalidDataException($"A tab of another client or subject was read for {Client}.")
            : null;

        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);
        await Beaten(() => Lists == 2, "the first beat");
        Assert.True(t.OthersEditing);

        _clock.Advance(_settings.Interval);
        await Beaten(() => Lists == 4, "the second beat");

        var messages = _log.Entries.Select(e => e.Message).ToList();
        Assert.Equal(
            new[]
            {
                "Editing tabs: a closed tab's row was not deleted: RequestFailedException 503 ServerBusy.",
                "Editing tabs: an open tab's row was not written: RequestFailedException 503 ServerBusy.",
                "Editing tabs: the rows of an open subject could not be read: InvalidDataException.",
                "Editing tabs: old rows were not swept: RequestFailedException 503 ServerBusy.",
                "Editing tabs: an open tab's row was not written: RequestFailedException 503 ServerBusy.",
                "Editing tabs: the rows of an open subject could not be read: InvalidDataException.",
            }.Order(),
            messages.Order());
        Assert.All(_log.Entries, e =>
        {
            Assert.Equal(LogLevel.Warning, e.Level);
            Assert.Null(e.Error);
        });
        // A failed delete is not tried again: the row goes stale, and the sweep has it.
        Assert.Single(_rows.Calls, call => call == Delete(Campaign, closed));
        await beat.StopAsync(CancellationToken.None);
    }

    // A closed tab whose row could not be deleted is read back with the other rows, fresh and typed
    // into, and is still never another tab here.
    [Fact]
    public async Task A_closed_tab_whose_row_was_not_deleted_never_counts_as_another_tab()
    {
        var a = Open();
        var closed = Open();
        _clock.Now += Moment;
        closed.Typed();
        Assert.True(a.OthersEditing);

        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);
        await Beaten(() => Lists == 1, "the first beat");
        closed.Dispose();
        Assert.False(a.OthersEditing);
        _rows.Fails = call => call.StartsWith("delete ", StringComparison.Ordinal) ? Busy() : null;

        _clock.Advance(_settings.Interval);
        await Beaten(() => Lists == 2, "the beat that could not delete");
        Assert.Contains(_rows.Table.Rows, row => row.RowKey == closed.TabId.ToString("N"));
        Assert.False(a.OthersEditing);

        _clock.Advance(_settings.Interval);
        await Beaten(() => Lists == 3, "the beat after");
        Assert.False(a.OthersEditing);
        await beat.StopAsync(CancellationToken.None);
    }

    // It stops with the app at once, whether waiting for a tab or for the next beat, and beats no more.
    [Fact]
    public async Task It_stops_with_the_app()
    {
        using (var idle = Beat())
        {
            await idle.StartAsync(CancellationToken.None);
            await idle.StopAsync(CancellationToken.None).WaitAsync(Eventually.Patience);
        }

        using var beat = Beat();
        await beat.StartAsync(CancellationToken.None);
        Open();
        await Beaten(() => Lists == 1, "the first beat");

        await beat.StopAsync(CancellationToken.None).WaitAsync(Eventually.Patience);
        Assert.Equal(0, _clock.Waiting);
        var fired = _clock.Fired;
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(fired, _clock.Fired);
        Assert.Equal(1, Lists);
    }

    // In the app: the beat runs, and a tab opened there gets its row.
    [Fact]
    public async Task In_the_app_an_open_tab_gets_its_row()
    {
        await using var app = new DemoApp();
        var services = app.Services;
        Assert.Contains(services.GetServices<IHostedService>(), service => service is EditingTabsBeat);

        var tab = services.GetRequiredService<EditingTabs>().Register(Client, Campaign, Guid.NewGuid(), () => { });
        await Eventually.Until(() => app.TabRows.Rows.Any(row => row.RowKey == tab.TabId.ToString("N")),
            "the tab's row written", app.Clock.Changed);
        tab.Dispose();
    }

    private static RequestFailedException Busy() => new(503, "The table service is busy.", "ServerBusy", null);

    /// <summary>The in-memory table, with each call the beat makes kept in order, and any of them made to fail.</summary>
    private sealed class WatchedTabRows(TimeProvider clock) : ITabRows
    {
        private readonly ConcurrentQueue<string> _calls = new();
        private readonly ConcurrentQueue<DateTimeOffset> _swept = new();

        public InMemoryTabRows Table { get; } = new(clock);

        /// <summary>The failure a call is to throw, by its description, or null for none.</summary>
        public Func<string, Exception?> Fails { get; set; } = _ => null;

        public string[] Calls => _calls.ToArray();

        /// <summary>What each sweep was asked to remove rows older than.</summary>
        public DateTimeOffset[] Swept => _swept.ToArray();

        /// <summary>Raised after each call, answered or failed.</summary>
        public Pulse Changed { get; } = new();

        public Task WriteAsync(ClientName client, TabSubject subject, Guid tabId, DateTimeOffset? lastTyped,
            CancellationToken cancellationToken = default) =>
            Call($"write {subject} {tabId:N}", () => Table.WriteAsync(client, subject, tabId, lastTyped, cancellationToken));

        public Task<IReadOnlyList<TabRow>> ListAsync(ClientName client, TabSubject subject,
            CancellationToken cancellationToken = default) =>
            Call($"list {subject}", () => Table.ListAsync(client, subject, cancellationToken));

        public Task DeleteAsync(ClientName client, TabSubject subject, Guid tabId, CancellationToken cancellationToken = default) =>
            Call($"delete {subject} {tabId:N}", () => Table.DeleteAsync(client, subject, tabId, cancellationToken));

        public Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
        {
            _swept.Enqueue(olderThan);
            return Call("sweep", () => Table.SweepAsync(olderThan, cancellationToken));
        }

        private async Task<T> Call<T>(string call, Func<Task<T>> run)
        {
            _calls.Enqueue(call);
            try
            {
                if (Fails(call) is { } failure) throw failure;
                return await run();
            }
            finally
            {
                Changed.Raise();
            }
        }

        private Task Call(string call, Func<Task> run) => Call(call, async () =>
        {
            await run();
            return 0;
        });
    }
}
