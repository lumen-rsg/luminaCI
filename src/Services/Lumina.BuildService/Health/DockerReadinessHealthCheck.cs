using Docker.DotNet;
using Lumina.BuildService.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lumina.BuildService.Health;

public sealed class DockerReadinessHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var endpoint = configuration["Docker:SocketPath"] ?? "/var/run/docker.sock";
        var dockerUri = endpoint.Contains("://", StringComparison.Ordinal)
            ? new Uri(endpoint)
            : new Uri($"unix://{endpoint}");

        using var client = new DockerClientConfiguration(dockerUri).CreateClient();
        await client.System.PingAsync(cancellationToken);

        var images = configuration.GetSection("Docker:AllowedBuildImages")
            .GetChildren()
            .Select(item => item.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (images.Count == 0)
        {
            return HealthCheckResult.Unhealthy("No allowed build runner images are configured.");
        }

        var digests = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            BuildImagePolicy.Resolve(configuration, image);
            var inspection = await client.Images.InspectImageAsync(image, cancellationToken);
            if (string.IsNullOrWhiteSpace(inspection.ID) ||
                !inspection.ID.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return HealthCheckResult.Unhealthy(
                    $"Runner image '{image}' did not resolve to a content digest.");
            }

            digests[image] = inspection.ID;
        }

        return HealthCheckResult.Healthy(
            "Docker API and runner image digests are available.",
            digests);
    }
}
