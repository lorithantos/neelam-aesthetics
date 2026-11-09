using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// What lets a tab see the tabs on other instances of the site (owner, 2026-11-05: the table is "cheap
/// insurance. One minute intervals only while someone is active"). While no tab is open on this instance
/// it waits, and touches nothing. Once one opens it beats at once, then every
/// <see cref="EditingTabsOptions.Interval"/> while any is open. Each beat, in order: deletes the rows of the
/// tabs closed since the last; for each subject open here, writes a row for each of its tabs whose circuit
/// is connected (a dropped one is not written, so its row goes stale and other instances count it gone),
/// then reads the subject's rows and hands them to <see cref="EditingTabs.ApplyRemote"/>; then works every
/// notice out again against the clock (<see cref="EditingTabs.Refresh"/>); and, on its first beat and then
/// at most once per <see cref="EditingTabsOptions.SweepAfter"/>, sweeps rows older than that, left by
/// instances that went away. When the last tab closes it deletes the rows still to delete and waits again.
/// </summary>
/// <remarks>
/// A tab's typing reaches its row on the beat, not as it types: tabs on this instance hear at once,
/// through <see cref="EditingTabs"/>, and tabs on another within about two intervals.
/// <para>
/// Off every request: a hosted service of its own, and no editor page waits on it. A store call that fails
/// is logged by the failure's type, status and error code (as <see cref="Heartbeat"/> does) and the beat
/// carries on with the next tab, the next subject and the next beat; it stops when the app does. The
/// notices themselves are worked out by <see cref="EditingTabs"/>, which the beat awaits each time, so the
/// rows are in before the clock is consulted; a working-out that fails is logged the same way. A delete
/// that fails is not tried again: other instances count the row gone once it is
/// <see cref="EditingTabsOptions.GoneAfter"/> old, this instance leaves the tab out that long after the beat
/// took it (<see cref="EditingTabs.ApplyRemote"/>), and the sweep removes it. A sweep is tried once per
/// SweepAfter whether or not it worked: a row it left is stale already.
/// </para>
/// </remarks>
public sealed class EditingTabsBeat(
    EditingTabs tabs, ITabRows rows, TimeProvider clock, IOptions<EditingTabsOptions> options, ILogger<EditingTabsBeat> log)
    : BackgroundService
{
    private readonly EditingTabsOptions _settings = options.Value;
    private DateTimeOffset? _swept;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // No store call while no tab is open here: the first to open wakes it.
                await tabs.WaitForTabsAsync(stoppingToken);
                do
                {
                    await BeatAsync(stoppingToken);
                    await Task.Delay(_settings.Interval, clock, stoppingToken);
                }
                while (!tabs.IsEmpty);
                await DeleteClosedAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        await DeleteClosedAsync(cancellationToken);
        // In a fixed order (by subject, then client), not the registry's, which follows its tabs' random ids:
        // a beat then does the same each time, and a subject that fails is always followed by the same ones.
        var subjects = tabs.Snapshot().GroupBy(tab => (tab.Client, tab.Subject))
            .OrderBy(group => group.Key.Subject.ToString(), StringComparer.Ordinal)
            .ThenBy(group => group.Key.Client.ToString(), StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            var (client, edits) = subject.Key;
            foreach (var tab in subject.Where(tab => tab.Connected))
            {
                try
                {
                    await rows.WriteAsync(client, edits, tab.TabId, tab.LastTyped, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    log.LogWarning("Editing tabs: an open tab's row was not written: {Error}.", Heartbeat.Describe(ex));
                }
            }
            IReadOnlyList<TabRow> read;
            try
            {
                read = await rows.ListAsync(client, edits, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One row of another subject or with no timestamp refuses the whole subject: the tabs
                // elsewhere known before stand until they go stale, and the next subject is read.
                log.LogWarning("Editing tabs: the rows of an open subject could not be read: {Error}.", Heartbeat.Describe(ex));
                continue;
            }
            await WorkedOutAsync(
                tabs.ApplyRemote(client, edits, read.Select(row => new RemoteTab(row.TabId, row.LastTyped, row.Timestamp)), cancellationToken),
                cancellationToken);
        }
        // After every subject's rows: awaited in turn, so the notices are worked out with them in.
        await WorkedOutAsync(tabs.Refresh(cancellationToken), cancellationToken);

        var now = clock.GetUtcNow();
        if (_swept is { } swept && now - swept < _settings.SweepAfter) return;
        _swept = now;
        try
        {
            await rows.SweepAsync(now - _settings.SweepAfter, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning("Editing tabs: old rows were not swept: {Error}.", Heartbeat.Describe(ex));
        }
    }

    // The notices are worked out off this service (EditingTabs' loop): one that fails there is logged by its
    // type and the beat carries on, as with a store call; the app stopping ends the beat as anywhere else.
    private async Task WorkedOutAsync(Task posted, CancellationToken cancellationToken)
    {
        try
        {
            await posted;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning("Editing tabs: the notices were not worked out: {Error}.", ex.GetType().Name);
        }
    }

    private async Task DeleteClosedAsync(CancellationToken cancellationToken)
    {
        foreach (var closed in tabs.DrainClosed())
        {
            try
            {
                await rows.DeleteAsync(closed.Client, closed.Subject, closed.TabId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                log.LogWarning("Editing tabs: a closed tab's row was not deleted: {Error}.", Heartbeat.Describe(ex));
            }
        }
    }
}
