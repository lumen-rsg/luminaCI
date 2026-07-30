using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Lumina.Web.Shared.Observability;

public static class OpenTelemetryExtensions
{
    public const string OtlpEndpointEnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Enables OTLP traces and metrics when an endpoint is configured. Services
    /// remain dependency-free in local development when the endpoint is absent.
    /// </summary>
    public static IServiceCollection AddLuminaOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var configuredEndpoint =
            configuration[OtlpEndpointEnvironmentVariable] ??
            configuration["Observability:OtlpEndpoint"];

        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return services;
        }

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException(
                $"{OtlpEndpointEnvironmentVariable} must be an absolute HTTP(S) URI without embedded credentials.");
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: serviceName,
                    serviceVersion: Assembly.GetEntryAssembly()?
                        .GetName().Version?.ToString(),
                    serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing => tracing
                .AddSource("MassTransit")
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = true;
                    options.Filter = context => !IsNoiseProbe(context.Request.Path);
                })
                .AddHttpClientInstrumentation(options => options.RecordException = true)
                .AddOtlpExporter(options => options.Endpoint = endpoint))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(options => options.Endpoint = endpoint));

        return services;
    }

    private static bool IsNoiseProbe(PathString path)
        => path.StartsWithSegments("/health/live") ||
           path.StartsWithSegments("/health/startup");
}
