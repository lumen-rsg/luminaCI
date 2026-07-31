using Lumina.BuildService.Services;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class BuildExecutorResolverTests
{
    [Fact]
    public void Resolve_IsLazyAndReturnsOnlyRecordedBackend()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var docker = new FakeExecutor(BuildExecutorBackend.Docker);
        var kubernetes = new FakeExecutor(BuildExecutorBackend.Kubernetes);
        var dockerResolutions = 0;
        var kubernetesResolutions = 0;
        var resolver = new BuildExecutorResolver(
            services,
            [
                new BuildExecutorRegistration(BuildExecutorBackend.Docker, _ =>
                {
                    dockerResolutions++;
                    return docker;
                }),
                new BuildExecutorRegistration(BuildExecutorBackend.Kubernetes, _ =>
                {
                    kubernetesResolutions++;
                    return kubernetes;
                })
            ]);

        var resolved = resolver.Resolve(BuildExecutorBackend.Kubernetes);

        Assert.Same(kubernetes, resolved);
        Assert.Equal(0, dockerResolutions);
        Assert.Equal(1, kubernetesResolutions);
    }

    [Fact]
    public void Resolve_RejectsMissingDuplicateAndMismatchedRegistrations()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var docker = new FakeExecutor(BuildExecutorBackend.Docker);
        var missing = new BuildExecutorResolver(services, []);
        Assert.Throws<InvalidOperationException>(() =>
            missing.Resolve(BuildExecutorBackend.Kubernetes));

        Assert.Throws<InvalidOperationException>(() => new BuildExecutorResolver(
            services,
            [
                new BuildExecutorRegistration(BuildExecutorBackend.Docker, _ => docker),
                new BuildExecutorRegistration(BuildExecutorBackend.Docker, _ => docker)
            ]));

        var mismatch = new BuildExecutorResolver(
            services,
            [new BuildExecutorRegistration(BuildExecutorBackend.Kubernetes, _ => docker)]);
        Assert.Throws<InvalidOperationException>(() =>
            mismatch.Resolve(BuildExecutorBackend.Kubernetes));
    }

    private sealed class FakeExecutor(BuildExecutorBackend backend) : IBuildExecutor
    {
        public BuildExecutorBackend Backend { get; } = backend;

        public Task<BuildJob> StartBuildAsync(
            BuildJob job,
            string? specContent,
            string? sourceUrl,
            string? buildImage = null,
            string? gitUsername = null,
            string? gitToken = null,
            string? extraSourcesPipelineDir = null) => Task.FromResult(job);

        public Task MonitorBuildAsync(
            BuildJob job,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> CancelBuildAsync(
            Guid jobId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
