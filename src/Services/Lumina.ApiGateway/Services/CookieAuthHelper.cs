using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Lumina.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Lumina.ApiGateway.Services;

/// <summary>
/// Issues the access JWT and writes the browser-facing auth cookies
/// (<c>lumina_access</c>, <c>lumina_refresh</c>).
///
/// Both cookies are <c>HttpOnly</c> (invisible to JS, so XSS cannot exfiltrate
/// the token), <c>Secure</c> (HTTPS only) and <c>SameSite=Strict</c> (not sent
/// on cross-site requests, which is also CSRF protection). The access cookie is
/// scoped to <c>Path=/</c>; the refresh cookie to <c>Path=/api/auth</c> so it is
/// only ever submitted to the auth endpoints.
/// </summary>
public sealed class CookieAuthHelper
{
    public const string AccessCookie = "lumina_access";
    public const string RefreshCookie = "lumina_refresh";

    private readonly SymmetricSecurityKey _signingKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessMinutes;
    private readonly bool _cookieSecure;

    public CookieAuthHelper(IConfiguration config, SymmetricSecurityKey signingKey)
    {
        _signingKey = signingKey;
        _issuer = config["Jwt:Issuer"] ?? "LuminaCI";
        _audience = config["Jwt:Audience"] ?? "LuminaCI";
        _accessMinutes = int.TryParse(config["Jwt:AccessMinutes"], out var m) ? m : 15;
        // Defaults to true; can be disabled for plain-HTTP local dev.
        _cookieSecure = !bool.TryParse(config["Jwt:CookieSecure"], out var secure) || secure;
    }

    /// <summary>Builds and signs a short-lived access JWT for the user.</summary>
    public (string Token, DateTime ExpiresAtUtc) IssueAccessToken(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_accessMinutes);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expires,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>Writes the access-token cookie (Path=/).</summary>
    public void SetAccessCookie(HttpResponse response, string token, DateTime expiresAtUtc)
    {
        response.Cookies.Append(AccessCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = _cookieSecure,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            Expires = expiresAtUtc
        });
    }

    /// <summary>Writes the refresh-token cookie (Path=/api/auth).</summary>
    public void SetRefreshCookie(HttpResponse response, string token, DateTimeOffset expiresAtUtc)
    {
        response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = _cookieSecure,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/api/auth",
            Expires = expiresAtUtc
        });
    }

    /// <summary>Expires both cookies on logout.</summary>
    public void ClearCookies(HttpResponse response)
    {
        response.Cookies.Delete(AccessCookie, new CookieOptions { Path = "/", Secure = _cookieSecure });
        response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth", Secure = _cookieSecure });
    }
}
