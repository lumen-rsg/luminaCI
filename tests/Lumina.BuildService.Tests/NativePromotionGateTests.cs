using System.Text.Json;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class NativePromotionGateTests
{
    private static readonly string Hash = new('b', 64);
    private static readonly string Digest = $"sha256:{new string('a', 64)}";

    [Fact]
    public void Manifest_IsDeterministicAndRejectsMutableCandidateObject()
    {
        var first = Resources("zeta");
        var second = Resources("alpha", first.DeliveryId, first.SetId, first.RepositoryId);
        var gate = NativePromotionGateManifestPolicy.Create(
            first.DeliveryId, first.SetId, first.RepositoryId, "jetson-r39.2",
            "aarch64", Digest,
            new[] { (first.Job, first.Artifact), (second.Job, second.Artifact) },
            DateTime.UtcNow);

        var manifest = NativePromotionGateManifestPolicy.Read(gate);

        Assert.Equal(["alpha", "zeta"], manifest.Candidates.Select(item => item.ProjectPackageId));
        Assert.Equal(64, gate.CandidateManifestSha256.Length);
        first.Artifact.StoragePath = "mutable/package.rpm";
        Assert.Throws<ValidationException>(() => NativePromotionGateManifestPolicy.Create(
            first.DeliveryId, first.SetId, first.RepositoryId, "jetson-r39.2",
            "aarch64", Digest, new[] { (first.Job, first.Artifact) }, DateTime.UtcNow));
    }

    [Fact]
    public async Task JobAndTransport_UseNativeRestrictedWorkloadAndExactCapabilities()
    {
        var resource = Resources("firmware");
        var gate = CreateGate(resource);
        var runner = new KubernetesRunner(
            "fedora-44-aarch64", "arm64", $"registry.example/runner@{Digest}");
        var limits = new KubernetesJobLimits(3600, 86400, "500m", "2", "1Gi", "4Gi", "4Gi", "16Gi");
        var network = new KubernetesBuildNetworkPolicy(
            "10.77.0.1/32", 443, "https://packages.example/fedora");
        var job = NativePromotionGateJobFactory.Create(gate, runner, limits, network);

        var pod = job.Spec.Template.Spec;
        var container = Assert.Single(pod.Containers);
        Assert.True(job.Spec.Suspend);
        Assert.False(pod.AutomountServiceAccountToken);
        Assert.False(pod.HostUsers);
        Assert.Equal("arm64", pod.NodeSelector["kubernetes.io/arch"]);
        Assert.Equal(["/bin/bash"], container.Command);
        Assert.DoesNotContain("SYS_CHROOT", container.SecurityContext.Capabilities.Add);
        Assert.DoesNotContain(pod.Volumes, volume => volume.HostPath is not null);
        Assert.Contains("dnf --disablerepo", NativePromotionGateTransportPolicy.RunnerScript);

        gate.KubernetesNamespace = "lumina-builds";
        gate.KubernetesJobName = job.Metadata.Name;
        gate.KubernetesJobUid = "job-uid-1";
        var identity = new KubernetesBuildResourceIdentity(
            "lumina-builds", job.Metadata.Name, "job-uid-1", null);
        var signer = new FakeSigner();
        var transport = await NativePromotionGateTransportPolicy.CreateAsync(
            gate, identity, limits, signer, DateTimeOffset.UtcNow, default);
        var secret = NativePromotionGateTransportPolicy.CreateSecret(
            identity.Namespace, identity.JobName, transport);

        Assert.Equal([resource.Artifact.StoragePath], signer.Objects);
        Assert.True(secret.Immutable);
        Assert.Equal(2, secret.Data.Count);
        var persisted = NativePromotionGateTransportPolicy.ValidateSecret(secret, identity, gate);
        Assert.Equal(transport.PromotionSetId, persisted.PromotionSetId);
        Assert.Equal(transport.CandidateManifestSha256, persisted.CandidateManifestSha256);
        Assert.Equal(transport.Candidates, persisted.Candidates);
    }

    [Fact]
    public void Result_RequiresExactJobRunnerAndCandidateSet()
    {
        var resource = Resources("firmware");
        var gate = CreateGate(resource);
        var identity = new KubernetesBuildResourceIdentity(
            "lumina-builds", NativePromotionGateIdentity.JobName(gate.Id), "job-uid-1", "pod-1");
        var result = JsonSerializer.Serialize(new
        {
            version = 1,
            promotionSetId = gate.Id,
            kubernetesJobUid = identity.JobUid,
            runnerImageDigest = Digest,
            targetArchitecture = "aarch64",
            transaction = "clean-install",
            candidates = new[]
            {
                new
                {
                    fileName = resource.Artifact.FileName,
                    sha256 = Hash,
                    nevra = "firmware-0:1.0-1.aarch64"
                }
            }
        });

        var hash = NativePromotionGateResultPolicy.ValidateAndHash(
            gate, identity, $"dnf output\nLUMINA_GATE_RESULT={result}\n");

        Assert.Equal(64, hash.Length);
        Assert.Throws<ValidationException>(() => NativePromotionGateResultPolicy.ValidateAndHash(
            gate, identity, $"LUMINA_GATE_RESULT={result.Replace(Hash, new string('c', 64))}\n"));
    }

    private static NativePromotionGate CreateGate(TestResources resource) =>
        NativePromotionGateManifestPolicy.Create(
            resource.DeliveryId, resource.SetId, resource.RepositoryId,
            "jetson-r39.2", "aarch64", Digest,
            new[] { (resource.Job, resource.Artifact) }, DateTime.UtcNow);

    private static TestResources Resources(
        string packageId,
        Guid? deliveryId = null,
        Guid? setId = null,
        Guid? repositoryId = null)
    {
        var delivery = deliveryId ?? Guid.NewGuid();
        var promotionSet = setId ?? Guid.NewGuid();
        var repository = repositoryId ?? Guid.NewGuid();
        var fileName = $"{packageId}-1.0-1.aarch64.rpm";
        var job = new BuildJob
        {
            Id = Guid.NewGuid(),
            ProjectWebhookDeliveryId = delivery,
            ProjectPackageId = packageId,
            PromotionGroup = "jetson-r39.2",
            TargetArchitecture = "aarch64",
            RunnerImageDigest = Digest
        };
        var artifact = new BuildArtifact
        {
            Id = Guid.NewGuid(),
            BuildJobId = job.Id,
            FileName = fileName,
            FileSize = 42,
            HashSha256 = Hash,
            StoragePath = $"sha256/{Hash}/{fileName}",
            CandidateRepositoryId = repository,
            CandidatePackageId = Guid.NewGuid(),
            PromotionSetId = promotionSet,
            CandidateStagedAt = DateTime.UtcNow
        };
        return new TestResources(delivery, promotionSet, repository, job, artifact);
    }

    private sealed record TestResources(
        Guid DeliveryId,
        Guid SetId,
        Guid RepositoryId,
        BuildJob Job,
        BuildArtifact Artifact);

    private sealed class FakeSigner : IKubernetesObjectUrlSigner
    {
        public List<string> Objects { get; } = [];

        public Task<string> SignArtifactDownloadAsync(
            string objectName,
            int expirySeconds,
            CancellationToken cancellationToken)
        {
            Objects.Add(objectName);
            return Task.FromResult($"https://packages.example/artifacts/{objectName}?signature=test");
        }

        public Task<string> SignSnapshotDownloadAsync(
            string objectName,
            int expirySeconds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SignArtifactBundleUploadAsync(
            string objectName,
            int expirySeconds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
