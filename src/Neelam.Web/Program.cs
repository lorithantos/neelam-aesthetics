using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Janet.Azure.Storage;
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
builder.Services.AddSingleton(new ClientStores(storageClients, TimeProvider.System));
var metadata = new TableMetadata(storageClients);
builder.Services.AddSingleton<IClientDirectory>(metadata);
builder.Services.AddSingleton<ISupportGrantStore>(metadata);

// THE ROLLOUT SWITCH. Every page and endpoint already names its policy; Prototype lets everyone
// through (and refuses to start in Production), Enforced requires the Entra app role. Moving to
// Enforced also needs sign-in wired in, which is the rest of the rollout.
builder.Services.AddFeatureAccess(AccessMode.Prototype, builder.Environment);
builder.Services.AddCascadingAuthenticationState();

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

app.UseStaticFiles();
app.UseAuthorization();
app.UseAntiforgery();

// Proves the app's identity can reach its storage: it reads the clients table and reports only
// whether that worked, never what is in it. Public so a deploy can check it with no sign-in.
app.MapGet("/healthz", async (IClientDirectory clients, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        await clients.ListAsync(ct);
        return Results.Text("ok");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        log.LogError(ex, "Health check could not read the clients table.");
        return Results.Text("storage unreachable", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Public so the tests can host the real app and inspect its endpoints.</summary>
public partial class Program;
