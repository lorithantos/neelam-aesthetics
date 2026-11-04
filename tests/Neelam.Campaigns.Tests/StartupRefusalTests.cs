using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Neelam.Web;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A setting the app refuses stops it as it is built, before app.Run, with its services left as they
/// were built. Refused inside app.Run instead, the app disposes its services on its own thread on the
/// way out while WebApplicationFactory reads them on the test's, to start the host: the test then saw
/// ObjectDisposedException in place of the refusal whenever the app's thread got there first: rarely,
/// once in several full runs (2026-11-03, UndoSweepTests). Here that disposal is watched for, so a
/// refusal moved back into app.Run fails every time, whichever thread gets there first.
/// </summary>
public class StartupRefusalTests
{
    // Each setting checked with ValidateOnStart, set to a value it refuses.
    [Theory]
    [InlineData("Undo:GracePeriod", "91.00:00:00", "Undo:GracePeriod (required) must lie between Undo:GracePeriodMinimum and Undo:GracePeriodMaximum")]
    [InlineData("Undo:SweepInterval", "00:00:00", "Undo:SweepInterval must be a positive")]
    [InlineData("CampaignWatch:Interval", "00:00:01", "CampaignWatch:Interval must lie between")]
    [InlineData("Warmup:Budget", "00:00:00", "Warmup:Budget must be a positive time span")]
    [InlineData("Starts:Keep", "0", "Starts:Keep must be at least 1")]
    [InlineData("Heartbeat:Interval", "00:00:00", "Heartbeat:Interval must lie between")]
    [InlineData("TemplateLinks:Mode", "Sometimes", "TemplateLinks:Mode")]
    public void A_refused_setting_stops_the_app_before_it_runs(string setting, string value, string says)
    {
        using RefusingApp app = new(new Dictionary<string, string?> { [setting] = value });

        Exception refused = Assert.ThrowsAny<Exception>(() => app.Services);

        Assert.Contains(says, refused.ToString());
        Assert.False(app.ServicesDisposed);
    }

    // App Service's own requests are data checked as the app is built, the same way.
    [Fact]
    public void A_refused_platform_request_stops_the_app_before_it_runs()
    {
        using RefusingApp app = new(new Dictionary<string, string?>
        {
            [$"{PlatformRequests.Section}:2:{nameof(PlatformRequest.Name)}"] = "Everyone",
            [$"{PlatformRequests.Section}:2:{nameof(PlatformRequest.Path)}"] = "/",
        });

        Exception refused = Assert.ThrowsAny<Exception>(() => app.Services);

        Assert.Contains("is / with no UserAgent", refused.ToString());
        Assert.False(app.ServicesDisposed);
    }
}

/// <summary>
/// The demo app with settings it refuses, given as app configuration, which the app reads only once its
/// services are built. It notes whether those services were disposed before the refusal reached the test.
/// </summary>
public sealed class RefusingApp(IReadOnlyDictionary<string, string?> settings) : InMemoryApp
{
    private readonly ServicesDisposal _disposal = new();

    internal bool ServicesDisposed => this._disposal.IsDisposed;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(PrototypeCallerSource.ClientSetting, DemoApp.Client.ToString());
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        // Made by the container, so the container disposes it with the rest.
        builder.ConfigureTestServices(services => services.AddSingleton(_ => this._disposal));
    }

    protected override void ConfigureAccess(IServiceCollection services)
    {
    }

    // As WebApplicationFactory's own, once the services have made the watched one.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        IHost host = builder.Build();
        host.Services.GetRequiredService<ServicesDisposal>();
        host.Start();
        return host;
    }
}

/// <summary>Disposed when the services that made it are.</summary>
internal sealed class ServicesDisposal : IDisposable
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref this._disposed) == 1;

    public void Dispose() => Interlocked.Exchange(ref this._disposed, 1);
}
