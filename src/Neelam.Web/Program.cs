using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Janet.Azure.Storage;
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
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
