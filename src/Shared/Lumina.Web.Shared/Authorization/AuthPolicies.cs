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
/// configuration (the YARP <c>AuthorizationPolicy</c> routes in
/// ApiGateway/appsettings.json) and from <c>[Authorize(Policy = ...)]</c>
/// attributes on downstream controllers.
/// </summary>
public static class AuthPolicies
{
    /// <summary>
    /// Any authenticated principal — a valid JWT.
    /// </summary>
    /// <remarks>
    /// The value is deliberately <c>"lumina-default"</c>, not <c>"default"</c>:
    /// YARP treats the literal strings <c>"default"</c> and <c>"anonymous"</c>
    /// as reserved sentinels ("use the app default policy" / "skip authz"), and
    /// refuses to load its route config if the app also registers a policy under
    /// either name (dotnet/yarp#2346). The policy name constant here is a
    /// well-known internal identifier referenced from <c>[Authorize]</c> and from
    /// the YARP route config in ApiGateway/appsettings.json — keep them in sync.
    /// </remarks>
    public const string Default = "lumina-default";

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
    /// <remarks>
    /// Value is <c>"lumina-anonymous"</c>, not <c>"anonymous"</c>, for the same
    /// reserved-name reason as <see cref="Default"/> — see dotnet/yarp#2346.
    /// </remarks>
    public const string Anonymous = "lumina-anonymous";
}
