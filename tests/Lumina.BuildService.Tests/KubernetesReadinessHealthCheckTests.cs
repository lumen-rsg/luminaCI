using Lumina.BuildService.Health;
using Lumina.BuildService.Services;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesReadinessHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_SkipsClusterWhenDockerIsSelected()
    {
        var probe = new FakeProbe { Exception = new InvalidOperationException("must not run") };
        var check = new KubernetesReadinessHealthCheck(
            new BuildExecutorSelection(BuildExecutorBackend.Docker, null),
            probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.False(probe.Called);
    }

    [Fact]
    public async Task CheckHealthAsync_FailsClosedForDeniedPermission()
    {
        var probe = new FakeProbe { Denied = ["delete batch/jobs"] };
        var check = new KubernetesReadinessHealthCheck(
            new BuildExecutorSelection(BuildExecutorBackend.Kubernetes, "lumina-builds"),
            probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("lumina-builds", probe.Namespace);
        Assert.True(result.Data.ContainsKey("denied"));
    }

    [Fact]
    public async Task CheckHealthAsync_AcceptsExistingNamespaceAndCompletePermissions()
    {
        var check = new KubernetesReadinessHealthCheck(
            new BuildExecutorSelection(BuildExecutorBackend.Kubernetes, "lumina-builds"),
            new FakeProbe());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class FakeProbe : IKubernetesReadinessProbe
    {
        public IReadOnlyList<string> Denied { get; init; } = [];
        public Exception? Exception { get; init; }
        public bool Called { get; private set; }
        public string? Namespace { get; private set; }

        public Task<IReadOnlyList<string>> FindDeniedPermissionsAsync(
            string buildNamespace,
            CancellationToken cancellationToken)
        {
            Called = true;
            Namespace = buildNamespace;
            return Exception != null
                ? Task.FromException<IReadOnlyList<string>>(Exception)
                : Task.FromResult(Denied);
        }
    }
}
