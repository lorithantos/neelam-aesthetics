using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Janet.Azure.Storage;
using Microsoft.Extensions.Options;
using Neelam.Web;
using Neelam.Web.Security;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Managed identity only: refuse to start if any key, SAS or password is configured.
CredentialGuard.EnsureNoStoredCredentials(builder.Configuration.AsEnumerable());

// In Azure, the site's own identity. Locally, the developer's `az login` — still a person's
// Entra sign-in, never a stored secret. DefaultAzureCredential is avoided on purpose: it would
// also pick up a client secret from environment variables.
TokenCredential credential = builder.Environment.IsDevelopment()
    ? new AzureCliCredential()
    : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

var storage = builder.Configuration.GetSection("Storage");
Uri RequiredEndpoint(string setting, string output) =>
    storage[setting] is { Length: > 0 } value
        ? new Uri(value)
        : throw new InvalidOperationException(
            $"Storage:{setting} is not set. In Azure the Bicep deployment sets it; locally, set it to " +
            $"the {output} output of the test deployment.");

// Built once: every client's container and the metadata tables share these clients, the app's
// credential and the shared retry budget. Each endpoint is checked here, at startup.
var storageClients = new StorageClients(
    new StorageEndpoints(
        Blob: RequiredEndpoint("BlobServiceUri", "blobServiceUri"),
        Table: RequiredEndpoint("TableServiceUri", "tableServiceUri")),
    credential);

// Application Insights, wherever its connection string is set: in Azure an App Service setting the
// Bicep deployment fills in from the resource, never a file in the repository. Ingestion is
// Entra-only, so the exporter signs in with the same identity as storage. Without the setting, as
// in a local run, nothing is sent.
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor(monitor => monitor.Credential = credential);

// One deployment serves every client, so a store is opened per request, for a client the access
// check has already allowed. Nothing is registered for "the" client: there is none.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(storageClients);

// Undo marks a save and the sweep deletes it once the grace period has passed (owner, 2026-10-09).
// The period is a setting, Undo:GracePeriod, required: the app refuses to start without it.
builder.Services.AddOptions<UndoOptions>()
    .Bind(builder.Configuration.GetSection(UndoOptions.Section))
    .Validate(undo => undo.Problem() is null,
        "Undo:GracePeriod (required) and Undo:SweepInterval must be positive time spans, such as 1.00:00:00, " +
        "and Undo:SweepInterval at most 49 days.")
    .ValidateOnStart();
var metadata = new TableMetadata(storageClients);
builder.Services.AddSingleton<IClientDirectory>(metadata);
builder.Services.AddSingleton<ISupportGrantStore>(metadata);
// Approvals in the approvals table, and every action as an activity event, deletions included,
// never with the content (owner, 2026-10-09). A failed activity write is logged and the action
// carries on (ActivityRecorder).
builder.Services.AddSingleton<IApprovalStore>(metadata);
builder.Services.AddSingleton<IActivityLog>(metadata);
builder.Services.AddSingleton<ActivityRecorder>();
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton(services => new ClientStores(
    storageClients, services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<IOptions<UndoOptions>>().Value.GracePeriod,
    services.GetRequiredService<IApprovalStore>(), services.GetRequiredService<ActivityRecorder>()));
builder.Services.AddHostedService<UndoSweep>();
builder.Services.AddSingleton<IKnownItemStore>(services =>
    new KnownItemTable(storageClients, services.GetRequiredService<ILogger<KnownItemTable>>()));

// THE ROLLOUT SWITCH. Every page and endpoint already names its policy; Prototype lets everyone
// through (and refuses to start in Production), Enforced requires the Entra app role. Moving to
// Enforced also needs sign-in wired in, which is the rest of the rollout. The mode also decides who
// pages think is asking: Prototype's fixed caller (Prototype:Client), or the Entra sign-in.
builder.Services.AddFeatureAccess(AccessMode.Prototype, builder.Environment, builder.Configuration);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ClientWorkspace>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

// On the demo the operator's pages answer 404, as if they did not exist (owner, 2026-10-09).
app.UseOperatorPagesHiddenInDemo();
app.UseAuthorization();
app.UseAntiforgery();

// The site's own files (app.css, copy.js) at fingerprinted addresses with long-lived caching, linked
// through @Assets in App.razor; a changed file gets a new address, so no browser keeps a stale one.
// They hold no client data, and every page needs them, so they are public.
app.MapStaticAssets().AllowAnonymous();

// Proves the app's identity can reach its storage: it reads the clients table and reports only
// whether that worked, never what is in it. Public so a deploy can check it with no sign-in.
// The answer is JSON -- {"status":"ok","utc":"<ISO 8601 UTC>"} -- and the server's own time shows a
// probe reached a live process, not something cached in front of it. The status code still
// carries the verdict on its own (200 or 503), which is all a deploy's poll reads.
app.MapGet("/healthz", async (IClientDirectory clients, TimeProvider clock, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        await clients.ListAsync(ct);
        return Results.Json(HealthReport.At("ok", clock));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        log.LogError(ex, "Health check could not read the clients table.");
        return Results.Json(HealthReport.At("storage unreachable", clock), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Public so the tests can host the real app and inspect its endpoints.</summary>
public partial class Program;

/// <summary>What /healthz answers: a status word and the server's clock.</summary>
/// <param name="Utc">Round-trip ISO 8601 in UTC, with a Z -- the same instant in every timezone, so a
/// reader never has to guess which one the server was in.</param>
internal sealed record HealthReport(string Status, string Utc)
{
    public static HealthReport At(string status, TimeProvider clock) =>
        new(status, clock.GetUtcNow().UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
}
