namespace Lumina.Web.Shared.Authorization;

/// <summary>
/// Role names written into the <c>role</c> claim of issued JWTs (see the login
/// handler in ApiGateway). Kept in the shared library so every service agrees on
/// the same spelling instead of each one hard-coding its own literal.
/// </summary>
public static class AuthRoles
{
    public const string Admin = "Admin";
    public const string Developer = "Developer";
}

/// <summary>
/// Named authorization policies. These mirror the policy names referenced from
/// configuration (e.g. the YARP <c>AuthorizationPolicy: "default"</c> routes in
/// ApiGateway/appsettings.json) and from <c>[Authorize(Policy = ...)]</c>
/// attributes on downstream controllers.
/// </summary>
public static class AuthPolicies
{
    /// <summary>
    /// Any authenticated principal — a valid JWT. Registered so the YARP routes
    /// that carry <c>AuthorizationPolicy: "default"</c> resolve to an explicit
    /// policy rather than relying on YARP's implicit fallback.
    /// </summary>
    public const string Default = "default";

    /// <summary>
    /// An authenticated principal whose <c>role</c> claim is <see cref="AuthRoles.Admin"/>.
    /// Applied to sensitive operations (PGP key generation, package signing,
    /// repository upload, build-queue clearing, etc.).
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// Allows the request through with no authentication. Used by YARP for the
    /// anonymous webhook route, whose own signature-secret check is the real gate.
    /// </summary>
    public const string Anonymous = "anonymous";
}
