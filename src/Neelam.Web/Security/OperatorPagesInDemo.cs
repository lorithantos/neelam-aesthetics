using Microsoft.AspNetCore.Authorization;

namespace Neelam.Web.Security;

/// <summary>
/// On the demo, the operator's pages do not exist (owner, 2026-10-09). Prototype access lets
/// everyone through every policy, so anyone with the address of an admin page could change a
/// client's display name or phone numbers. In Prototype mode every page or endpoint that names the
/// <see cref="Features.Operator"/> policy answers a plain 404, the same as an address with no page,
/// before authorization or the page runs. It is decided from each endpoint's own policy, not from
/// its address, so a new operator page is covered the day it is written. Enforced, as production
/// runs, is unchanged: there the Operator app role decides.
/// </summary>
public static class OperatorPagesInDemo
{
    /// <summary>Whether an endpoint is the operator's: it names the Operator policy.</summary>
    public static bool IsOperatorOnly(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == Features.Operator);

    /// <summary>
    /// Answers 404 for an operator endpoint on the demo. Goes after routing, which the host puts first,
    /// and before authorization.
    /// </summary>
    public static IApplicationBuilder UseOperatorPagesHiddenInDemo(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is { } endpoint && IsOperatorOnly(endpoint)
                && context.RequestServices.GetRequiredService<SiteMode>().IsDemo)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });
}
