using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lumina.Web.Shared.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Lumina.Web.Shared;

/// <summary>
/// Shared JWT authentication and authorization wiring.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds JWT-bearer authentication using the same validation parameters the
    /// ApiGateway issues tokens with (HmacSha256 signing key, configured
    /// issuer/audience). The secret is read from <c>Jwt:Secret</c>, which every
    /// service now receives via its environment in docker-compose.
    /// </summary>
    /// <remarks>
    /// Defense-in-depth: even though the ApiGateway validates the JWT and forwards
    /// authorized requests, each downstream service independently re-validates the
    /// bearer token. If an internal port is ever exposed again, an anonymous caller
    /// still cannot reach a controller that carries <c>[Authorize]</c>.
    /// </remarks>
    public static IServiceCollection AddLuminaJwtAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var jwtSecret = configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException(
                "Jwt:Secret is not configured. Set JWT_SECRET (min 32 chars) via environment variable.");
        var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"] ?? "LuminaCI",
                    ValidAudience = configuration["Jwt:Audience"] ?? "LuminaCI",
                    IssuerSigningKey = jwtKey,
                    ClockSkew = TimeSpan.Zero
                };

                // Support JWT via query string for SSE/EventSource connections
                // (the EventSource API cannot set custom headers).
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    /// <summary>
    /// Adds authorization with the shared policy set:
    /// <list type="bullet">
    /// <item><c>lumina-default</c> — any authenticated caller (used by YARP routes).</item>
    /// <item><c>admin</c> — caller in the <c>Admin</c> role.</item>
    /// <item><c>lumina-anonymous</c> — always allowed (used by YARP for webhook routes).</item>
    /// </list>
    /// The policy values are deliberately prefixed <c>lumina-</c>: YARP reserves
    /// the bare strings <c>"default"</c> and <c>"anonymous"</c> as route-config
    /// sentinels, and loading the route config throws if the app also registers
    /// a policy under either name (dotnet/yarp#2346). See AuthPolicies.cs.
    /// </summary>
    public static IServiceCollection AddLuminaAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // Any valid, authenticated caller.
            options.AddPolicy(AuthPolicies.Default, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

            // Admin role only.
            options.AddPolicy(AuthPolicies.Admin, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(AuthRoles.Admin)
                .Build());

            // Anonymous — the request itself (e.g. a webhook signature) is the gate.
            options.AddPolicy(AuthPolicies.Anonymous, new AuthorizationPolicyBuilder()
                .RequireAssertion(_ => true)
                .Build());

            // Default fallback: nothing is reachable without authentication.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    /// <summary>
    /// Convenience: the role claim from a validated JWT, or null when the caller
    /// is unauthenticated (e.g. an anonymous webhook). Downstream controllers use
    /// this only for informational purposes; authorization is enforced by policy.
    /// </summary>
    public static string? GetRole(this ClaimsPrincipal principal)
        => principal.FindFirst(ClaimTypes.Role)?.Value
           ?? principal.FindFirst(JwtRegisteredClaimNames.Typ)?.Value;
}
