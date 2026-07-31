using System.Text.Json;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.Configuration;
using Minio.DataModel.Args;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesBuildTransportPolicyTests
{
    [Fact]
    public async Task RunnerObjectStore_SignsAgainstControlledHttpsOrigin()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MinIO:RunnerEndpoint"] = "packages.lumina.1t.ru:443",
                ["MinIO:RunnerUseSSL"] = "true",
                ["MinIO:AccessKey"] = "test-access-key",
                ["MinIO:SecretKey"] = "test-secret-key"
            }).Build();

        using var store = KubernetesRunnerObjectStore.Create(configuration);
        var url = await store.Client.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket("lumina-sources")
                .WithObject("project/snapshot.tar.gz")
                .WithExpiry(900));

        Assert.StartsWith(
            "https://packages.lumina.1t.ru/lumina-sources/project/snapshot.tar.gz?",
            url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_BindsExactSnapshotBundleAndExpiryToJobUid()
    {
        var resources = Resources();
        var signer = new FakeSigner();
        var now = DateTimeOffset.UtcNow;

        var transport = await KubernetesBuildTransportPolicy.CreateAsync(
            resources.Job,
            resources.Delivery,
            resources.Identity,
            Limits(),
            signer,
            now,
            CancellationToken.None);
        var secret = KubernetesBuildTransportPolicy.CreateSecret(
            resources.Identity.Namespace,
            resources.Identity.JobName,
            transport);

        Assert.Equal(resources.Identity.JobUid, transport.KubernetesJobUid);
        Assert.Equal(resources.Delivery.SnapshotStoragePath, signer.DownloadObject);
        Assert.Equal(
            KubernetesBuildTransportPolicy.BundleObjectName(
                resources.Job.Id,
                resources.Identity.JobUid),
            signer.UploadObject);
        Assert.Equal(8100, signer.ExpirySeconds);
        Assert.Equal(now.AddSeconds(8100), transport.ExpiresAt);
        Assert.True(secret.Immutable);
        Assert.Equal(
            KubernetesBuildTransportPolicy.SecretName(resources.Identity.JobName),
            secret.Metadata.Name);
        Assert.Equal(resources.Identity.JobUid, Assert.Single(secret.Metadata.OwnerReferences).Uid);
        var decoded = JsonSerializer.Deserialize<KubernetesBuildTransport>(
            secret.Data[KubernetesBuildTransportPolicy.SecretDataKey]);
        Assert.Equal(transport, decoded);
    }

    [Fact]
    public async Task CreateAsync_RejectsManualBuildAndMismatchedSnapshotIdentity()
    {
        var resources = Resources();
        resources.Job.ProjectWebhookDeliveryId = null;

        await Assert.ThrowsAsync<ValidationException>(() =>
            KubernetesBuildTransportPolicy.CreateAsync(
                resources.Job,
                resources.Delivery,
                resources.Identity,
                Limits(),
                new FakeSigner(),
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        resources.Job.ProjectWebhookDeliveryId = resources.Delivery.Id;
        resources.Delivery.SnapshotStoragePath = "attacker/snapshot.tar.gz";
        await Assert.ThrowsAsync<ValidationException>(() =>
            KubernetesBuildTransportPolicy.CreateAsync(
                resources.Job,
                resources.Delivery,
                resources.Identity,
                Limits(),
                new FakeSigner(),
                DateTimeOffset.UtcNow,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RejectsNonPresignedTransportUrl()
    {
        var resources = Resources();
        var signer = new FakeSigner { DownloadUrl = "https://minio.example/snapshot" };

        await Assert.ThrowsAsync<ValidationException>(() =>
            KubernetesBuildTransportPolicy.CreateAsync(
                resources.Job,
                resources.Delivery,
                resources.Identity,
                Limits(),
                signer,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
    }

    private static TestResources Resources()
    {
        var projectId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var uid = "12345678-1234-1234-1234-123456789abc";
        var commit = new string('c', 40);
        var digest = new string('a', 64);
        var delivery = new ProjectWebhookDelivery
        {
            Id = deliveryId,
            BuildProjectId = projectId,
            CommitSha = commit,
            SnapshotSha256 = digest,
            SnapshotFileSize = 4096,
            Status = ProjectWebhookStatus.PlanReady,
            ManifestSha256 = new string('b', 64),
            SnapshotStoragePath =
                $"project-{projectId:N}/{digest}/project-{projectId:N}-sources.tar.gz"
        };
        var job = new BuildJob
        {
            Id = jobId,
            ExecutionBackend = BuildExecutorBackend.Kubernetes,
            CommitSha = commit,
            ProjectWebhookDeliveryId = deliveryId,
            KubernetesNamespace = "lumina-builds",
            KubernetesJobName = KubernetesBuildIdentity.JobName(jobId),
            KubernetesJobUid = uid
        };
        return new TestResources(
            job,
            delivery,
            new KubernetesBuildResourceIdentity(
                "lumina-builds",
                KubernetesBuildIdentity.JobName(jobId),
                uid,
                null));
    }

    private static KubernetesJobLimits Limits() => new(
        7200,
        86400,
        "500m",
        "2",
        "1Gi",
        "4Gi",
        "4Gi",
        "16Gi");

    private sealed record TestResources(
        BuildJob Job,
        ProjectWebhookDelivery Delivery,
        KubernetesBuildResourceIdentity Identity);

    private sealed class FakeSigner : IKubernetesObjectUrlSigner
    {
        public string DownloadUrl { get; init; } = "https://minio.example/snapshot?signature=download";
        public string UploadUrl { get; init; } = "https://minio.example/bundle?signature=upload";
        public string? DownloadObject { get; private set; }
        public string? UploadObject { get; private set; }
        public int ExpirySeconds { get; private set; }

        public Task<string> SignSnapshotDownloadAsync(
            string objectName,
            int expirySeconds,
            CancellationToken cancellationToken)
        {
            DownloadObject = objectName;
            ExpirySeconds = expirySeconds;
            return Task.FromResult(DownloadUrl);
        }

        public Task<string> SignArtifactBundleUploadAsync(
            string objectName,
            int expirySeconds,
            CancellationToken cancellationToken)
        {
            UploadObject = objectName;
            Assert.Equal(ExpirySeconds, expirySeconds);
            return Task.FromResult(UploadUrl);
        }
    }
}
