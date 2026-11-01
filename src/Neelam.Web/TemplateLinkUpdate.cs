using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Web;

/// <summary>
/// The one-time update that links campaigns saved before they recorded their template by id (owner,
/// 2026-11-01: "renaming a template should not change the campaigns. We should use an ID instead of the
/// name" and "We can run an update when the new one is deployed to main"). It runs once as the app
/// starts, off the startup path, in the deployed site under the site's own managed identity, over every
/// client the undo sweep works in (<see cref="CampaignStore.LinkTemplatesAsync"/>), and only when
/// <c>TemplateLinks:Mode</c> asks: <c>Report</c> writes nothing and logs what it would do, <c>Apply</c>
/// links, <c>Off</c> (the default) does nothing. The update is idempotent, so a restart with Apply still
/// set only reports what is left. What it did is recorded in the log, by campaign id only, one line per
/// client. No page or endpoint starts it, so it adds nothing anyone can reach.
/// </summary>
public sealed class TemplateLinkUpdate(
    ClientStores stores, IClientDirectory clients, IEnumerable<PrototypeCallerSource> prototype,
    IOptions<TemplateLinkOptions> options, ILogger<TemplateLinkUpdate> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Mode == TemplateLinkMode.Off) return;
        // Off the startup path: the app answers requests while it runs.
        await Task.Yield();
        try
        {
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is stopping; the next start runs it again, which is safe.
        }
    }

    /// <summary>
    /// One run over every client in the clients table, and the prototype's client in Prototype mode,
    /// each once. A client that fails is logged and the others go on; nothing here stops the app.
    /// </summary>
    /// <returns>Each client's report, for the tests.</returns>
    public async Task<IReadOnlyDictionary<ClientName, TemplateLinkReport>> RunOnceAsync(CancellationToken ct = default)
    {
        var apply = options.Value.Mode == TemplateLinkMode.Apply;
        var reports = new Dictionary<ClientName, TemplateLinkReport>();
        if (options.Value.Mode == TemplateLinkMode.Off) return reports;
        IReadOnlyList<ClientRecord> listed;
        try
        {
            listed = await clients.ListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "The template link update could not read the clients table.");
            listed = [];
        }
        foreach (var client in listed.Select(c => c.Name).Concat(prototype.Select(p => p.Client)).Distinct())
        {
            try
            {
                var report = await stores.Campaigns(client, Actor.UndoSweep).LinkTemplatesAsync(apply, ct);
                reports[client] = report;
                log.LogInformation("Template link update for {Client}: {Report}.", client.Value, report.ToString());
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "The template link update failed for {Client}; nothing after the failure was linked. Run it again.", client.Value);
            }
        }
        return reports;
    }
}

/// <summary>What the template link update does as the app starts.</summary>
public enum TemplateLinkMode
{
    /// <summary>Nothing: the default.</summary>
    Off,

    /// <summary>Works out and logs what it would link, writing nothing: for staging first.</summary>
    Report,

    /// <summary>Links, and logs what it linked and what it could not.</summary>
    Apply,
}

/// <summary>Settings for the template link update, section <c>TemplateLinks</c>.</summary>
public sealed class TemplateLinkOptions
{
    public const string Section = "TemplateLinks";

    public TemplateLinkMode Mode { get; set; } = TemplateLinkMode.Off;
}
