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
/// test reaches the network. Enforced is what production runs, so this is also never the demo.
/// </summary>
public sealed class EnforcedApp : InMemoryApp
{
    protected override void ConfigureAccess(IServiceCollection services)
    {
        services.AddFeatureAccess(
            AccessMode.Enforced, new NamedEnvironment(Environments.Development), CallerSourceTests.Settings());
        services.AddTestSignIn();
    }
}

/// <summary>
/// The app as the test site runs it today: Prototype access, as Program registers it, which is the
/// demo. Everyone gets through, as the prototype's fixed caller, a member of
/// <see cref="Client"/>. Storage is in memory, as for <see cref="EnforcedApp"/>.
/// </summary>
public sealed class DemoApp : InMemoryApp
{
    internal static readonly ClientName Client = new("test-salon-one");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(PrototypeCallerSource.ClientSetting, Client.ToString());
        base.ConfigureWebHost(builder);
    }

    // Program's own registration stands: Prototype, the demo.
    protected override void ConfigureAccess(IServiceCollection services)
    {
    }
}

/// <summary>The app hosted in-process over in-memory storage and a manual clock; the access mode is the subclass's.</summary>
public abstract class InMemoryApp : WebApplicationFactory<Program>
{
    internal InMemoryContainers Containers { get; } = new();
    internal InMemoryClientDirectory Clients { get; } = new();
    internal InMemorySupportGrants Grants { get; } = new();
    internal ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The app's own stores, over <see cref="Containers"/>.</summary>
    internal ClientStores Stores => Services.GetRequiredService<ClientStores>();

    protected abstract void ConfigureAccess(IServiceCollection services);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Storage:BlobServiceUri", "https://test.blob.core.windows.net/");
        builder.UseSetting("Storage:TableServiceUri", "https://test.table.core.windows.net/");
        builder.ConfigureTestServices(services =>
        {
            ConfigureAccess(services);

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
