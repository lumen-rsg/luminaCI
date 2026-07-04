using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lumina.Shared.Extensions;

/// <summary>
/// Redis-based distributed cache service with typed GetOrSet pattern.
/// Used across all Lumina CI microservices for caching database queries.
/// </summary>
/// <remarks>
/// <b>CONTRACT — cache plain DTOs only, never EF Core entities.</b> Values are
/// serialized with <see cref="JsonSerializer"/> and shipped to Redis, then
/// deserialized on the next read. <c>System.Text.Json</c> cannot reliably
/// round-trip EF Core entities: navigation properties form reference cycles
/// (e.g. <c>Pipeline ↔ PipelineStep</c>) and EF tracking/proxy state is not
/// JSON-representable. Round-tripping such a value silently yields
/// <c>null</c>/default data — rows "disappear" on the next cache hit. This
/// previously forced <c>PipelineEngine</c> to disable caching at every call
/// site; the underlying <em>service</em> was the actual hazard.
///
/// To make misuse fail loud and fast instead of corrupting silently, the setter
/// performs a <see cref="VerifyRoundTrip"/> smoke check and rejects any value it
/// cannot reproduce. Cache a projection DTO that has only primitive/simple-type
/// members (and no cycles) and this guard will never fire. If you genuinely need
/// to cache an entity, project it to a DTO in the factory first.
/// </remarks>
public class RedisCacheService
{
    // Default ReferenceHandler (NOT IgnoreCycles). A bidirectional EF Core
    // navigation (e.g. Pipeline.Steps[].Pipeline) is a reference cycle that the
    // default serializer refuses to write, which is exactly the signal we want:
    // it makes TrySerialize round-trip-verification fail loud instead of letting
    // STJ silently drop the back-reference and store a value whose later
    // deserialization returns null/default.
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IDistributedCache cache, ILogger<RedisCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Get a cached value or set it from the factory if not found.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="T"/> MUST be a cache-safe DTO, not an EF Core entity
    /// — see the contract on the class. As defense in depth, the value written
    /// to Redis is round-trip-verified before being stored; an unverifiable
    /// value is logged and dropped rather than corrupting the cache slot.
    /// </remarks>
    public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null)
    {
        try
        {
            var cached = await _cache.GetStringAsync(key);
            if (cached != null)
            {
                return JsonSerializer.Deserialize<T>(cached, SerializerOptions);
            }

            var value = await factory();
            if (value != null)
            {
                // Fail closed: never persist a value we cannot reproduce. If
                // System.Text.Json cannot serialize it (reference cycle) or drops
                // data on the round trip (the EF-entity-with-navigation-properties
                // failure mode), refuse to cache it and surface the problem
                // instead of letting it sit in Redis and silently return
                // null/default on every subsequent hit.
                if (!TrySerializeForCache(value, out var json))
                {
                    _logger.LogWarning(
                        "Refused to cache key {Key}: {Type} is not cache-safe (failed serialization round-trip). " +
                        "Cache a projection DTO instead of an EF Core entity (see RedisCacheService contract).",
                        key, typeof(T).FullName);
                    return value;
                }

                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(5)
                };
                await _cache.SetStringAsync(key, json, options);
            }

            return value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache error for key {Key}, falling through to factory", key);
            return await factory();
        }
    }

    /// <summary>
    /// Returns true only if <paramref name="value"/> can be serialized and
    /// faithfully reconstructed: it must serialize without throwing (rejecting
    /// reference cycles such as EF navigation properties) and the JSON must be
    /// byte-identical when the deserialized value is re-serialized (rejecting
    /// any silent data loss). Comparing JSON forms avoids needing value equality
    /// on arbitrary DTOs while still detecting lossy round trips.
    /// </summary>
    private static bool TrySerializeForCache<T>(T value, out string json)
    {
        json = null!;
        try
        {
            var first = JsonSerializer.Serialize(value, SerializerOptions);
            var roundTripped = JsonSerializer.Deserialize<T>(first, SerializerOptions);
            if (roundTripped is null)
            {
                return false;
            }
            var second = JsonSerializer.Serialize(roundTripped, SerializerOptions);
            if (second != first)
            {
                return false;
            }

            json = first;
            return true;
        }
        catch (Exception)
        {
            // Cycle, unsupported type, etc. — value is not cache-safe.
            return false;
        }
    }

    /// <summary>
    /// Remove a cached value by key.
    /// </summary>
    public async Task RemoveAsync(string key)
    {
        try
        {
            await _cache.RemoveAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache remove error for key {Key}", key);
        }
    }
}

/// <summary>
/// Common cache key patterns for all Lumina CI services.
/// </summary>
public static class CacheKeys
{
    // Pipeline keys
    public static string PipelineList(int page, int pageSize) => $"pipelines:list:{page}:{pageSize}";
    public static string Pipeline(Guid id) => $"pipeline:{id}";

    // Build job keys
    public static string BuildJob(Guid id) => $"buildjob:{id}";
    public static string BuildJobList(int page, int pageSize) => $"buildjobs:list:{page}:{pageSize}";
    public static string BuildQueue => "builds:queue";

    // Hash/security keys
    public static string HashHistory(Guid artifactId) => $"hash:history:{artifactId}";
    public static string HashList(int page, int pageSize) => $"hash:list:{page}:{pageSize}";
    public static string SecurityKeysList => "security:keys:list";
    public static string SigningHistory(int count) => $"signing:history:{count}";

    // Scanner keys
    public static string ScanReport(Guid artifactId) => $"scan:report:{artifactId}";
    public static string ScanArtifactReports(Guid artifactId) => $"scan:artifact:{artifactId}:reports";
}
