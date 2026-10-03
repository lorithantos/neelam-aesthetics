using Janet.Entra;
using Microsoft.AspNetCore.Authorization;
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

    /// <summary>A client's catalog of procedures and medications, and its check policy.</summary>
    public const string ClientSettings = "Client.Settings";

    /// <summary>How the site looks for a client.</summary>
    public const string Look = "Client.Look";

    /// <summary>Clients and onboarding; the person who runs the deployment, and their helpers.</summary>
    public const string Operator = CallerClaims.OperatorRole;

    public static IReadOnlyList<string> All { get; } = [Campaigns, Review, ClientSettings, Look, Operator];
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
/// The one place the feature policies are registered. The pages carry their real attributes from
/// the start; going from <see cref="AccessMode.Prototype"/> to <see cref="AccessMode.Enforced"/>
/// is changing the mode <c>Program</c> passes here, and every attribute takes effect at once.
/// </summary>
public static class AccessPolicies
{
    public static IServiceCollection AddFeatureAccess(this IServiceCollection services, AccessMode mode, IHostEnvironment environment)
    {
        if (mode == AccessMode.Prototype && environment.IsProduction())
            throw new InvalidOperationException(
                "Prototype access lets everyone through, so it is refused in Production, where real client " +
                "data lives. Run the prototype locally or on the test deployment, or switch to Enforced.");

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
}
