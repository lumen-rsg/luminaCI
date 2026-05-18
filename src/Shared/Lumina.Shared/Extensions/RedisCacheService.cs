using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lumina.Shared.Extensions;

/// <summary>
/// Redis-based distributed cache service with typed GetOrSet pattern.
/// Used across all Lumina CI microservices for caching database queries.
/// </summary>
public class RedisCacheService
{
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
    public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null)
    {
        try
        {
            var cached = await _cache.GetStringAsync(key);
            if (cached != null)
            {
                return JsonSerializer.Deserialize<T>(cached);
            }

            var value = await factory();
            if (value != null)
            {
                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(5)
                };
                await _cache.SetStringAsync(key, JsonSerializer.Serialize(value), options);
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
