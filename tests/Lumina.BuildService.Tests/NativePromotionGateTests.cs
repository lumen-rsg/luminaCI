using System.Text.Json;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class NativePromotionGateTests
{
    [Theory]
    [InlineData("quickshell-0.3.1^20260911git2d3b3e9-2.lu26.aarch64.rpm", true)]
    [InlineData("chroma-compositor-0.2.0~rc1-1.lu26.x86_64.rpm", true)]
    [InlineData("../escape.rpm", false)]
    [InlineData("nested/package.rpm", false)]
    [InlineData("package^snapshot\\escape.rpm", false)]
    [InlineData("package;touch.rpm", false)]
    [InlineData("package%2fescape.rpm", false)]
    [InlineData("package.rpm\nextra", false)]
    public async Task Runner_AcceptsRpmVersionsButRejectsUnsafeCandidateNames(string fileName, bool accepted)
    {
        // Execute the actual gate condition with Bash: its regex dialect differs
        // from .NET, and an ingestion-only check misses this later boundary.
        var condition = NativePromotionGateTransportPolicy.RunnerScript.Split('\n')
            .Single(line => line.Contains("candidate filename is invalid", StringComparison.Ordinal))
            .Split(" || fail", StringSplitOptions.None)[0];
        var start = new System.Diagnostics.ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(condition);
        start.Environment["file"] = fileName;
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(accepted ? 0 : 1, process.ExitCode);
    }

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
    public void Manifest_IdentitySurvivesJsonbPropertyReordering()
    {
        var resource = Resources("firmware");
        var gate = CreateGate(resource);
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            gate.CandidateManifestJson)!;

        gate.CandidateManifestJson = JsonSerializer.Serialize(
            properties.OrderByDescending(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value));

        var manifest = NativePromotionGateManifestPolicy.Read(gate);

        Assert.Equal(gate.Id, manifest.PromotionSetId);
        gate.CandidateManifestJson = gate.CandidateManifestJson.Replace(
            "jetson-r39.2", "jetson-r39.3", StringComparison.Ordinal);
        Assert.Throws<ValidationException>(() => NativePromotionGateManifestPolicy.Read(gate));
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
        Assert.Contains("baseline-upgrade", NativePromotionGateTransportPolicy.RunnerScript);

        gate.KubernetesNamespace = "lumina-builds";
        gate.KubernetesJobName = job.Metadata.Name;
        gate.KubernetesJobUid = "job-uid-1";
        gate.BundleSha256 = new string('c', 64);
        gate.BundleObjectName = $"promotion-gates/{gate.Id:N}/sha256/{gate.BundleSha256}/input.tar";
        gate.BundleSize = 4096;
        gate.BundlePreparedAt = DateTime.UtcNow;
        var identity = new KubernetesBuildResourceIdentity(
            "lumina-builds", job.Metadata.Name, "job-uid-1", null);
        var signer = new FakeSigner();
        var transport = await NativePromotionGateTransportPolicy.CreateAsync(
            gate, identity, limits, signer, DateTimeOffset.UtcNow, default);
        var secret = NativePromotionGateTransportPolicy.CreateSecret(
            identity.Namespace, identity.JobName, transport);

        Assert.Equal([gate.BundleObjectName], signer.Objects);
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
            transaction = "baseline-upgrade",
            baselinePackageNames = new[] { "firmware" },
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

    [Fact]
    public void PreparedBundle_IsBoundToCandidateManifestAndContentAddress()
    {
        var resource = Resources("firmware");
        var gate = CreateGate(resource);
        var bundleHash = new string('d', 64);
        var prepared = new Lumina.Shared.Events.PromotionGatePrepared(
            gate.Id, gate.RepositoryId, gate.CandidateManifestSha256,
            $"promotion-gates/{gate.Id:N}/sha256/{bundleHash}/input.tar",
            bundleHash, 4096, DateTime.UtcNow);

        NativePromotionGateManifestPolicy.RecordPrepared(gate, prepared);
        gate.Status = NativePromotionGateStatus.Running;
        NativePromotionGateManifestPolicy.RecordPrepared(gate, prepared);

        Assert.Equal(bundleHash, gate.BundleSha256);
        Assert.Throws<ConflictException>(() => NativePromotionGateManifestPolicy.RecordPrepared(
            gate, prepared with { BundleSize = 8192 }));
        Assert.Throws<ValidationException>(() => NativePromotionGateManifestPolicy.RecordPrepared(
            CreateGate(resource), prepared with { CandidateManifestSha256 = new string('e', 64) }));
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
