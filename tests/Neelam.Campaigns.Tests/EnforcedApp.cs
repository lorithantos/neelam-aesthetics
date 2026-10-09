using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The app as it will be at rollout: hosted in-process with the ENFORCING policies, whatever mode
/// Program runs in today, so every page's attribute is proven before the switch rather than on the
/// day of it. Requests sign in through <see cref="TestSignIn"/>, and storage is in memory, so no
/// test reaches the network.
/// </summary>
public sealed class EnforcedApp : WebApplicationFactory<Program>
{
    internal InMemoryContainers Containers { get; } = new();
    internal InMemoryClientDirectory Clients { get; } = new();
    internal InMemorySupportGrants Grants { get; } = new();
    internal ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The app's own stores, over <see cref="Containers"/>.</summary>
    internal ClientStores Stores => Services.GetRequiredService<ClientStores>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Storage:BlobServiceUri", "https://test.blob.core.windows.net/");
        builder.UseSetting("Storage:TableServiceUri", "https://test.table.core.windows.net/");
        builder.ConfigureTestServices(services =>
        {
            services.AddFeatureAccess(
                AccessMode.Enforced, new NamedEnvironment(Environments.Development), CallerSourceTests.Settings());
            services.AddTestSignIn();

            services.RemoveAll<TimeProvider>().AddSingleton<TimeProvider>(Clock);
            // In memory, with the grace period the app is configured with, as Program wires it.
            services.RemoveAll<ClientStores>().AddSingleton(provider => new ClientStores(
                Containers.For, Clock, provider.GetRequiredService<IOptions<UndoOptions>>().Value.GracePeriod));
            services.RemoveAll<IClientDirectory>().AddSingleton<IClientDirectory>(Clients);
            services.RemoveAll<ISupportGrantStore>().AddSingleton<ISupportGrantStore>(Grants);
        });
    }
}

/// <summary>An environment that is only a name, for registering policies outside a host.</summary>
internal sealed class NamedEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "Neelam.Web";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}
