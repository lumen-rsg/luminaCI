using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lumina.Web.Shared.Health;

public sealed class DbContextHealthCheck<TContext>(IServiceScopeFactory scopeFactory) : IHealthCheck
    where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Database connection succeeded.")
            : HealthCheckResult.Unhealthy("Database connection failed.");
    }
}

public sealed class DistributedCacheHealthCheck(IDistributedCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var key = $"lumina:health:{Guid.NewGuid():N}";
        var value = Guid.NewGuid().ToByteArray();

        try
        {
            await cache.SetAsync(
                key,
                value,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
                },
                cancellationToken);

            var stored = await cache.GetAsync(key, cancellationToken);
            return stored is not null && stored.AsSpan().SequenceEqual(value)
                ? HealthCheckResult.Healthy("Distributed cache round trip succeeded.")
                : HealthCheckResult.Unhealthy("Distributed cache round trip returned an unexpected value.");
        }
        finally
        {
            await cache.RemoveAsync(key, CancellationToken.None);
        }
    }
}

public sealed class ConfiguredHttpEndpointHealthCheck(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    string configurationKey,
    string fallbackBaseUrl,
    string relativePath) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration[configurationKey] ?? fallbackBaseUrl;
        if (!baseUrl.Contains("://", StringComparison.Ordinal))
        {
            baseUrl = "http://" + baseUrl;
        }
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/'), UriKind.Absolute, out var uri))
        {
            return HealthCheckResult.Unhealthy($"Configured endpoint '{configurationKey}' is not a valid absolute URL.");
        }

        using var client = httpClientFactory.CreateClient("LuminaHealthChecks");
        using var response = await client.GetAsync(uri, cancellationToken);
        return response.IsSuccessStatusCode
            ? HealthCheckResult.Healthy($"{uri.Host} responded successfully.")
            : HealthCheckResult.Unhealthy($"{uri.Host} returned HTTP {(int)response.StatusCode}.");
    }
}

public sealed class WritableDirectoriesHealthCheck(IReadOnlyCollection<string> directories) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, $".lumina-health-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(probe, "ok", cancellationToken);
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy(
                    $"Required directory '{directory}' is not writable.",
                    ex);
            }
        }

        return HealthCheckResult.Healthy("Required directories are writable.");
    }
}
