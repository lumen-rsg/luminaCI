using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina.Shared.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Lumina.ApiGateway.Services;

/// <summary>
/// Issues, validates and revokes opaque refresh tokens backed by Redis.
///
/// The token itself is a 32-byte random value that is never stored; only its
/// SHA-256 hash lives in Redis (keyed <c>auth:refresh:{hash}</c>) together with
/// the issuing user and an absolute expiry. Revocation is therefore a single
/// cache delete (logout) — there is no JWT to "un-sign". Access tokens remain
/// valid until their short (15 min) lifetime elapses; refresh tokens cannot be
/// replayed after revocation.
/// </summary>
public sealed class RefreshTokenStore
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<RefreshTokenStore> _logger;

    public RefreshTokenStore(IDistributedCache cache, ILogger<RefreshTokenStore> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Issues a new refresh token and persists its hashed record.</summary>
    /// <returns>The raw token (send to the client) and the record.</returns>
    public async Task<(string Token, RefreshRecord Record)> IssueAsync(User user, TimeSpan lifetime)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var record = new RefreshRecord
        {
            UserId = user.Id,
            Username = user.Username,
            Role = user.Role,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime)
        };

        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime
        };

        try
        {
            await _cache.SetStringAsync(Key(token), JsonSerializer.Serialize(record), options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist refresh token for {Username}", user.Username);
            throw;
        }

        return (token, record);
    }

    /// <summary>
    /// Validates a presented refresh token. Returns the record when it exists and
    /// has not expired, otherwise <c>null</c>.
    /// </summary>
    public async Task<RefreshRecord?> ValidateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var json = await _cache.GetStringAsync(Key(token));
            if (json is null) return null;

            var record = JsonSerializer.Deserialize<RefreshRecord>(json);
            if (record is null) return null;

            if (record.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await _cache.RemoveAsync(Key(token));
                return null;
            }

            return record;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh token validation error");
            return null;
        }
    }

    /// <summary>
    /// Rotates a refresh token: validates, deletes the old token and issues a new
    /// one (with a fresh expiry). Returns the new token/record, or <c>null</c>
    /// when the presented token was invalid/expired.
    /// </summary>
    public async Task<(string Token, RefreshRecord Record)?> RotateAsync(string token, TimeSpan lifetime)
    {
        var record = await ValidateAsync(token);
        if (record is null) return null;

        await _cache.RemoveAsync(Key(token));

        // Reconstruct a minimal User for IssueAsync — only id/username/role matter.
        var user = new User
        {
            Id = record.UserId,
            Username = record.Username,
            Role = record.Role
        };
        return await IssueAsync(user, lifetime);
    }

    /// <summary>Revokes a refresh token (logout). Idempotent.</summary>
    public async Task RevokeAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        try { await _cache.RemoveAsync(Key(token)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to revoke refresh token"); }
    }

    private static string Key(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return "auth:refresh:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Server-side state for a refresh token.</summary>
public sealed record RefreshRecord
{
    public Guid UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}
