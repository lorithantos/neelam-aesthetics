using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
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
var blobServiceUri = storage["BlobServiceUri"] is { Length: > 0 } uri
    ? new Uri(uri)
    : throw new InvalidOperationException(
        "Storage:BlobServiceUri is not set. In Azure the Bicep deployment sets it; locally, set it to " +
        "the blobServiceUri output of the deployment.");

// Application Insights, wherever its connection string is set: in Azure an App Service setting the
// Bicep deployment fills in from the resource, never a file in the repository. Ingestion is
// Entra-only, so the exporter signs in with the same identity as storage. Without the setting, as
// in a local run, nothing is sent.
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor(monitor => monitor.Credential = credential);

// One deployment serves every client, so a store is opened per request, for a client the access
// check has already allowed. Nothing is registered for "the" client: there is none.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new ClientStores(blobServiceUri, credential, TimeProvider.System));

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
