using Janet.Entra;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neelam.Campaigns.Storage;

namespace Neelam.Web.Security;

/// <summary>
/// What a page or endpoint can require. Each is an authorization policy, named on the page with
/// <c>[Authorize(Policy = Features.X)]</c>, and at rollout an Entra app role of the same value.
/// A feature says what someone may <em>do</em>; whose data they reach is the access check's.
/// </summary>
public static class Features
{
    /// <summary>List, write, save and delete a client's drafts and templates.</summary>
    public const string Campaigns = "Campaigns";

    /// <summary>Run the checks and the proofread, see the findings, export.</summary>
    public const string Review = "Campaigns.Review";

    /// <summary>
    /// Create and change a client's templates, the layouts campaigns are written from. Apart from
    /// <see cref="Campaigns"/>, so writing campaigns and changing their layouts can be granted
    /// separately.
    /// </summary>
    public const string Templates = "Campaigns.Templates";

    /// <summary>A client's catalog of procedures and medications, and its check policy.</summary>
    public const string ClientSettings = "Client.Settings";

    /// <summary>How the site looks for a client.</summary>
    public const string Look = "Client.Look";

    /// <summary>Clients and onboarding; the person who runs the deployment, and their helpers.</summary>
    public const string Operator = CallerClaims.OperatorRole;

    public static IReadOnlyList<string> All { get; } = [Campaigns, Review, Templates, ClientSettings, Look, Operator];
}

/// <summary>How the feature policies behave.</summary>
public enum AccessMode
{
    /// <summary>
    /// While prototyping: every policy lets everyone through, so work does not wait on the Entra
    /// setup. Refused in Production, so it can never serve a deployment holding real client data.
    /// </summary>
    Prototype,

    /// <summary>From rollout: signed in through Entra, and holding the feature's app role.</summary>
    Enforced,
}

/// <summary>
/// The one place the feature policies and the <see cref="ICallerSource"/> are registered. The pages
/// carry their real attributes from the start; going from <see cref="AccessMode.Prototype"/> to
/// <see cref="AccessMode.Enforced"/> is changing the mode <c>Program</c> passes here, and every
/// attribute, and who the pages think is asking, changes at once.
/// </summary>
public static class AccessPolicies
{
    /// <param name="configuration">Read only in Prototype mode, for <c>Prototype:Client</c>.</param>
    public static IServiceCollection AddFeatureAccess(
        this IServiceCollection services, AccessMode mode, IHostEnvironment environment, IConfiguration configuration)
    {
        if (mode == AccessMode.Prototype && environment.IsProduction())
            throw new InvalidOperationException(
                "Prototype access lets everyone through, so it is refused in Production, where real client " +
                "data lives. Run the prototype locally or on the test deployment, or switch to Enforced.");

        // Whichever mode registered last is the one pages get, so the tests' switch to Enforced
        // replaces the app's prototype caller as well as its policies.
        services.RemoveAll<ICallerSource>();
        services.RemoveAll<PrototypeCallerSource>();
        if (mode == AccessMode.Prototype)
        {
            // Also as itself, so the undo sweep covers the prototype's client even with no row in
            // the clients table.
            var prototype = new PrototypeCallerSource(PrototypeClient(configuration));
            services.AddSingleton(prototype);
            services.AddSingleton<ICallerSource>(prototype);
        }
        else
            services.AddScoped<ICallerSource, SignInCallerSource>();

        // The demo is Prototype, and nothing else: replaced with the mode, like the caller.
        services.RemoveAll<SiteMode>();
        services.AddSingleton(new SiteMode(IsDemo: mode == AccessMode.Prototype));

        services.AddAuthorization(options =>
        {
            foreach (var feature in Features.All)
                options.AddPolicy(feature, policy =>
                {
                    if (mode == AccessMode.Prototype)
                        policy.RequireAssertion(_ => true);
                    else
                        // Read through Janet.Entra, which fails closed: a sign-in it cannot read
                        // completely (no object ID, a group overage) holds no role at all.
                        policy.RequireAssertion(context =>
                            EntraClaims.Read(context.User).User?.HasAppRole(feature) == true);
                });
        });
        return services;
    }

    // Checked at startup, so a prototype without its client fails before the first page does.
    private static ClientName PrototypeClient(IConfiguration configuration) =>
        configuration[PrototypeCallerSource.ClientSetting] is { Length: > 0 } name
            ? new ClientName(name)
            : throw new InvalidOperationException(
                $"{PrototypeCallerSource.ClientSetting} is not set. The prototype works as one client: " +
                "appsettings.Development.json names it locally, and the test deployment's Bicep sets it.");
}
