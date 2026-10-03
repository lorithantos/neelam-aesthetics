using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Signs a test request in from headers, shaped like an Entra token (an object ID, app roles and
/// group IDs), so page tests can act as nobody, as a client's member or as the operator without a
/// real sign-in. The same pattern as ImageSelectorV2's TestAuthentication.
/// </summary>
internal static class TestSignIn
{
    public const string Scheme = "Test";

    /// <summary>Present on a signed-in request: the app roles, comma-separated, possibly none.</summary>
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>The Entra group IDs the user is in, comma-separated.</summary>
    public const string GroupsHeader = "X-Test-Groups";

    public const string UserId = "test-user";

    /// <summary>
    /// Registers the scheme as the default for everything, so a gated page answers a request with
    /// no sign-in with 401 and one without the role with 403.
    /// </summary>
    public static IServiceCollection AddTestSignIn(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, _ => { });
        return services;
    }

    /// <summary>Signs the client's requests in with these roles and groups.</summary>
    public static HttpClient SignedIn(this HttpClient client, IEnumerable<string> roles, IEnumerable<Guid>? groups = null)
    {
        client.DefaultRequestHeaders.Add(RolesHeader, string.Join(',', roles));
        if (groups is not null) client.DefaultRequestHeaders.Add(GroupsHeader, string.Join(',', groups));
        return client;
    }

    private sealed class Handler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        // Without the roles header the request is anonymous, so one app serves both kinds of test.
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
                return Task.FromResult(AuthenticateResult.NoResult());

            List<Claim> claims = [new("oid", UserId)];
            claims.AddRange(Split(roles).Select(r => new Claim("roles", r)));
            claims.AddRange(Split(Request.Headers[GroupsHeader]).Select(g => new Claim("groups", g)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestSignIn.Scheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestSignIn.Scheme)));
        }

        private static IEnumerable<string> Split(Microsoft.Extensions.Primitives.StringValues values) =>
            values.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
