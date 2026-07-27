using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lumina.Web.Shared.Health;

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddLuminaCoreReadiness<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddHttpClient("LuminaHealthChecks", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        return services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<DbContextHealthCheck<TContext>>("postgres", tags: ["ready"])
            .AddCheck<DistributedCacheHealthCheck>("redis", tags: ["ready"]);
    }

    public static IHealthChecksBuilder AddConfiguredHttpReadiness(
        this IHealthChecksBuilder checks,
        string name,
        string configurationKey,
        string fallbackBaseUrl,
        string relativePath)
    {
        return checks.Add(
            new HealthCheckRegistration(
                name,
                services => new ConfiguredHttpEndpointHealthCheck(
                    services.GetRequiredService<IHttpClientFactory>(),
                    services.GetRequiredService<IConfiguration>(),
                    configurationKey,
                    fallbackBaseUrl,
                    relativePath),
                HealthStatus.Unhealthy,
                ["ready"],
                TimeSpan.FromSeconds(6)));
    }

    public static IHealthChecksBuilder AddWritableDirectoriesReadiness(
        this IHealthChecksBuilder checks,
        params string[] directories)
    {
        return checks.Add(
            new HealthCheckRegistration(
                "writable-directories",
                _ => new WritableDirectoriesHealthCheck(directories),
                HealthStatus.Unhealthy,
                ["ready"],
                TimeSpan.FromSeconds(6)));
    }

    public static void MapLuminaHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var processOnly = new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        };

        // These routes become available only after migrations and startup
        // reconciliation complete, so startup probes cannot pass prematurely.
        endpoints.MapHealthChecks("/health/startup", processOnly).AllowAnonymous();
        endpoints.MapHealthChecks("/health/live", processOnly).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", CreateReadinessOptions()).AllowAnonymous();

        // Keep the original route as a readiness alias for existing probes.
        endpoints.MapHealthChecks("/health", CreateReadinessOptions()).AllowAnonymous();
    }

    private static HealthCheckOptions CreateReadinessOptions() => new()
    {
        Predicate = registration => registration.Tags.Contains("ready"),
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.ToDictionary(
                        entry => entry.Key,
                        entry => new
                        {
                            status = entry.Value.Status.ToString(),
                            description = entry.Value.Description,
                            data = entry.Value.Data,
                            duration = entry.Value.Duration.TotalMilliseconds
                        }),
                    duration = report.TotalDuration.TotalMilliseconds
                },
                cancellationToken: context.RequestAborted);
        }
    };
}
