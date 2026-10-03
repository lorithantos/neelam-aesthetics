using System.Security.Claims;
using Janet.Entra;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Builds a <see cref="Caller"/> from the Entra sign-in and nothing else. Reading the sign-in is
/// Janet.Entra's job, and it fails closed: not signed in, no object ID, or a group overage gives
/// no caller. What the sign-in <em>means</em> here is this app's: a user belongs to the clients
/// whose Entra groups they are in (each client's group, from the clients table), and the Operator
/// app role makes them the operator.
/// </summary>
public static class CallerClaims
{
    /// <summary>The app role value on the app registration.</summary>
    public const string OperatorRole = "Operator";

    /// <returns>The caller, or null with the reason when the sign-in does not say enough.</returns>
    public static (Caller? Caller, string Reason) FromClaims(ClaimsPrincipal principal, IReadOnlyList<ClientRecord> clients)
    {
        var reading = EntraClaims.Read(principal);
        if (reading.User is not { } user)
            return (null, reading.Reason);

        var memberOf = clients.Where(c => user.IsInGroup(c.GroupId)).Select(c => c.Name).ToList();
        return (new Caller(user.ObjectId, user.HasAppRole(OperatorRole), memberOf), reading.Reason);
    }
}
