using Azure;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// While the app runs, it says so about once a minute (owner, 2026-10-10: "yes, heartbeat in and drop
/// the paid test"): one small blob beside the record of starts, whose metadata holds the time of the
/// last beat (<see cref="IStartRecords.BeatAsync"/>). At each start, before its own first beat, it reads
/// the one left by the process before, so the start's record can say when the app was last running and
/// how long it was down (<see cref="StartLog.LastAliveBefore"/>). Operator data: a time and a start id.
/// </summary>
/// <remarks>
/// Off every request: a hosted service of its own. A read or a beat that fails is logged, by the
/// failure's type only, and the next beat tries again; it stops when the app does. Its only shared state
/// is the start's <see cref="TaskCompletionSource{TResult}"/>, set once. One instance per site: a second
/// instance would beat into the same blob, which still says the site was running.
/// </remarks>
public sealed class Heartbeat(
    StartLog start, IStartRecords store, TimeProvider clock, IOptions<HeartbeatOptions> options, ILogger<Heartbeat> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        DateTimeOffset? before = null;
        try
        {
            before = await store.LastAliveAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            log.LogWarning("Heartbeat: the last one before this start could not be read: {Error}.", Describe(ex));
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping before it could read: nothing to say about the gap.
        }
        finally
        {
            // Before the first beat, always: this start's gap is read from what the process before left.
            start.SetLastAliveBefore(before);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await store.BeatAsync(clock.GetUtcNow(), start.Id, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning("Heartbeat not written: {Error}.", Describe(ex));
                }
                await Task.Delay(options.Value.Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static string Describe(Exception ex) => ex is RequestFailedException storage
        ? $"{ex.GetType().Name} {storage.Status} {storage.ErrorCode}"
        : ex.GetType().Name;
}

/// <summary>Settings for the heartbeat, section <c>Heartbeat</c>.</summary>
public sealed class HeartbeatOptions
{
    public const string Section = "Heartbeat";

    /// <summary>How often the app says it is running: a minute, so a gap is known to within a minute.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}
