using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Web;

/// <summary>
/// Deletes for good the drafts and templates undone longer ago than the grace period, in every
/// client's container: once at startup, then every <see cref="UndoOptions.SweepInterval"/>. It
/// keeps no state of its own. Each run lists the blobs and reads their marks
/// (<see cref="CampaignStore.SweepAsync"/>), so a run cut short by a crash or a restart is simply
/// finished by the next one. Undo itself never deletes, and neither does leaving a page.
/// </summary>
/// <param name="prototype">
/// In Prototype mode, the one fixed caller: its client (<c>Prototype:Client</c>) is worked in whether
/// or not the clients table has a row for it, so it is swept either way. Empty in Enforced mode.
/// </param>
public sealed class UndoSweep(
    ClientStores stores, IClientDirectory clients, IEnumerable<PrototypeCallerSource> prototype,
    IOptions<UndoOptions> options, TimeProvider clock, ILogger<UndoSweep> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: the app answers requests while the first sweep runs.
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(options.Value.SweepInterval, clock);
            do
            {
                await SweepOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is stopping; the next start sweeps again.
        }
    }

    /// <summary>
    /// One sweep over every client in the clients table, and the prototype's client in Prototype
    /// mode, each once. A client that cannot be swept is logged and left for the next run; it never
    /// stops the others, or the app. Nor does a clients table that cannot be read: the prototype's
    /// client, which does not depend on it, is still swept.
    /// </summary>
    /// <returns>How many saves were deleted.</returns>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ClientRecord> listed;
        try
        {
            listed = await clients.ListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "The undo sweep could not read the clients table; it tries again next run.");
            listed = [];
        }

        var deleted = 0;
        foreach (var client in listed.Select(c => c.Name).Concat(prototype.Select(p => p.Client)).Distinct())
        {
            try
            {
                deleted += await stores.Campaigns(client).SweepAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "The undo sweep failed for {Client}; it tries again next run.", client.Value);
            }
        }
        if (deleted > 0) log.LogInformation("The undo sweep deleted {Count} undone saves.", deleted);
        return deleted;
    }
}
