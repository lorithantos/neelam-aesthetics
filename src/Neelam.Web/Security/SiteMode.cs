namespace Neelam.Web.Security;

/// <summary>
/// Whether this site is the demo (owner, 2026-10-09). The demo IS <see cref="AccessMode.Prototype"/>:
/// no setting of its own, so it can only run where Prototype may, which is locally and on the test
/// site, never in Production. A client can walk every step there, with two differences from the real
/// product: what she saves is kept on the operator's test system, not her own account, and the AI
/// proofread is not switched on, so an approved campaign whose rules pass exports without it, marked
/// as not proofread. Enforced, as production runs, is never the demo. Pages ask this rather than the
/// access mode, so the one place the two are tied is <see cref="AccessPolicies.AddFeatureAccess"/>.
/// </summary>
public sealed record SiteMode(bool IsDemo);
