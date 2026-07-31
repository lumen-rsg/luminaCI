using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class BuildExecutorSelectionPolicyTests
{
    [Fact]
    public void Resolve_DefaultsToDockerAndRejectsUnknownBackend()
    {
        var selection = BuildExecutorSelectionPolicy.Resolve(Configuration(), false);

        Assert.Equal(BuildExecutorBackend.Docker, selection.Backend);
        Assert.Null(selection.KubernetesNamespace);
        Assert.Throws<InvalidOperationException>(() => BuildExecutorSelectionPolicy.Resolve(
            Configuration(("BuildExecutor:Type", "shell")), false));
    }

    [Fact]
    public void Resolve_KubernetesFailsClosedUntilCompleteTransportIsAvailable()
    {
        var configuration = Configuration(
            ("BuildExecutor:Type", "Kubernetes"),
            ("Kubernetes:Enabled", "true"),
            ("Kubernetes:Namespace", "lumina-builds"));

        Assert.Throws<InvalidOperationException>(() =>
            BuildExecutorSelectionPolicy.Resolve(configuration, false));
        var selection = BuildExecutorSelectionPolicy.Resolve(configuration, true);
        Assert.Equal(BuildExecutorBackend.Kubernetes, selection.Backend);
        Assert.Equal("lumina-builds", selection.KubernetesNamespace);
    }

    [Theory]
    [InlineData("Lumina-Builds")]
    [InlineData("bad_namespace")]
    [InlineData("-builds")]
    public void Resolve_KubernetesRejectsUnsafeNamespace(string buildNamespace)
    {
        Assert.Throws<InvalidOperationException>(() => BuildExecutorSelectionPolicy.Resolve(
            Configuration(
                ("BuildExecutor:Type", "Kubernetes"),
                ("Kubernetes:Enabled", "true"),
                ("Kubernetes:Namespace", buildNamespace)),
            true));
    }

    [Fact]
    public void BuildIdentity_AppliesOnlyMatchingImmutableKubernetesIdentity()
    {
        var job = new BuildJob
        {
            Id = Guid.NewGuid(),
            ExecutionBackend = BuildExecutorBackend.Kubernetes
        };
        var identity = new KubernetesBuildResourceIdentity(
            "lumina-builds",
            KubernetesBuildIdentity.JobName(job.Id),
            "uid-1",
            "pod-1");

        KubernetesBuildIdentity.Apply(job, identity);

        Assert.Equal("uid-1", job.KubernetesJobUid);
        Assert.Equal("pod-1", job.KubernetesPodName);
        Assert.Throws<ConflictException>(() => KubernetesBuildIdentity.Apply(
            job, identity with { JobUid = "uid-2" }));
        Assert.Throws<ConflictException>(() => KubernetesBuildIdentity.Apply(
            job, identity with { Namespace = "other-builds" }));
        job.ExecutionBackend = BuildExecutorBackend.Docker;
        Assert.Throws<ValidationException>(() => KubernetesBuildIdentity.Apply(job, identity));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(item =>
                new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build();
}
