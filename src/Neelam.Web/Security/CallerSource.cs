using Microsoft.AspNetCore.Components.Authorization;
using Neelam.Campaigns.Storage;

namespace Neelam.Web.Security;

/// <summary>
/// The one place a page gets who is asking. A page takes the <see cref="Caller"/> from here and
/// passes it to <see cref="AccessCheck.Decide"/> before it reads or saves a client's data, in both
/// modes, so the prototype exercises the real check rather than skipping it.
/// </summary>
public interface ICallerSource
{
    /// <returns>The caller, or null with the reason when there is none to go on.</returns>
    Task<(Caller? Caller, string Reason)> CurrentAsync(CancellationToken ct = default);
}

/// <summary>
/// While prototyping: one fixed caller, the operator and a member of exactly one client, named by
/// <c>Prototype:Client</c>. It has no name, since nobody signs in: the activity trail says "demo user". Registered only with <see cref="AccessMode.Prototype"/>, which is
/// refused in Production, so it never stands in for a real sign-in where real data lives.
/// </summary>
public sealed class PrototypeCallerSource(ClientName client) : ICallerSource
{
    public const string ClientSetting = "Prototype:Client";

    public ClientName Client { get; } = client;

    public Task<(Caller? Caller, string Reason)> CurrentAsync(CancellationToken ct = default) =>
        Task.FromResult<(Caller?, string)>(
            (new Caller("prototype", IsOperator: true, [Client]), $"prototype: the operator, working as a member of {Client}"));
}

/// <summary>
/// From rollout: the caller the Entra sign-in describes, through <see cref="CallerClaims"/>, which
/// fails closed. Clients' groups come from the clients table, read per request so a client
/// onboarded a moment ago is already known.
/// </summary>
public sealed class SignInCallerSource(AuthenticationStateProvider authentication, IClientDirectory clients) : ICallerSource
{
    public async Task<(Caller? Caller, string Reason)> CurrentAsync(CancellationToken ct = default)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return CallerClaims.FromClaims(state.User, await clients.ListAsync(ct));
    }
}
