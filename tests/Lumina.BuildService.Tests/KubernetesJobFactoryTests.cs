using k8s;
using k8s.Models;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesJobFactoryTests
{
    private const string Digest =
        "registry.example/lumina/fedora-runner@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ResolveRunner_RequiresExactAdministratorDigestForTarget()
    {
        var job = Job();
        var configuration = Configuration(
            ("Kubernetes:RunnerImages:fedora-44-aarch64", Digest));

        var runner = KubernetesBuildPolicy.ResolveRunner(configuration, job, Digest);

        Assert.Equal("arm64", runner.Architecture);
        Assert.Equal(Digest, runner.Image);
        Assert.Throws<ValidationException>(() =>
            KubernetesBuildPolicy.ResolveRunner(configuration, job, "attacker/runner@sha256:" + new string('b', 64)));
    }

    [Theory]
    [InlineData("registry.example/runner:latest")]
    [InlineData("registry.example/runner:f44")]
    [InlineData("registry.example/runner@sha256:abc")]
    public void ResolveRunner_RejectsMutableOrTruncatedImage(string image)
    {
        var configuration = Configuration(
            ("Kubernetes:RunnerImages:fedora-44-aarch64", image));

        Assert.Throws<InvalidOperationException>(() =>
            KubernetesBuildPolicy.ResolveRunner(configuration, Job(), null));
    }

    [Fact]
    public void ResolveLimits_UsesBoundedOperatorConfiguration()
    {
        var limits = KubernetesBuildPolicy.ResolveLimits(Configuration(
            ("Kubernetes:Jobs:ActiveDeadlineSeconds", "3600"),
            ("Kubernetes:Jobs:CpuLimit", "4"),
            ("Kubernetes:Jobs:MemoryLimit", "8Gi")));

        Assert.Equal(3600, limits.ActiveDeadlineSeconds);
        Assert.Equal("4", limits.CpuLimit);
        Assert.Equal("8Gi", limits.MemoryLimit);
        Assert.Throws<InvalidOperationException>(() => KubernetesBuildPolicy.ResolveLimits(
            Configuration(("Kubernetes:Jobs:ActiveDeadlineSeconds", "0"))));
        Assert.Throws<InvalidOperationException>(() => KubernetesBuildPolicy.ResolveLimits(
            Configuration(("Kubernetes:Jobs:MemoryLimit", "unbounded"))));
        Assert.Throws<InvalidOperationException>(() => KubernetesBuildPolicy.ResolveLimits(
            Configuration(("Kubernetes:Jobs:CpuRequest", "4"), ("Kubernetes:Jobs:CpuLimit", "2"))));
        Assert.Throws<InvalidOperationException>(() => KubernetesBuildPolicy.ResolveLimits(
            Configuration(("Kubernetes:Jobs:CpuLimit", "4Gi"))));
    }

    [Fact]
    public void ResolveNetworkPolicy_RequiresOneSharedExactHttpsOrigin()
    {
        var configuration = Configuration(
            ("Kubernetes:Network:EgressCidr", "146.120.224.52/32"),
            ("Kubernetes:Network:HttpsPort", "443"),
            ("Kubernetes:Network:FedoraRepositoryBaseUrl", "https://packages.lumina.1t.ru/fedora/"),
            ("MinIO:RunnerEndpoint", "packages.lumina.1t.ru:443"),
            ("MinIO:RunnerUseSSL", "true"));

        var network = KubernetesBuildPolicy.ResolveNetworkPolicy(configuration);

        Assert.Equal("146.120.224.52/32", network.EgressCidr);
        Assert.Equal(443, network.HttpsPort);
        Assert.Equal("https://packages.lumina.1t.ru/fedora", network.FedoraRepositoryBaseUrl);
        Assert.Throws<InvalidOperationException>(() => KubernetesBuildPolicy.ResolveNetworkPolicy(
            Configuration(
                ("Kubernetes:Network:EgressCidr", "0.0.0.0/0"),
                ("Kubernetes:Network:FedoraRepositoryBaseUrl", "https://packages.lumina.1t.ru/fedora"),
                ("MinIO:RunnerEndpoint", "packages.lumina.1t.ru:443"))));
        Assert.Throws<InvalidOperationException>(() => KubernetesBuildPolicy.ResolveNetworkPolicy(
            Configuration(
                ("Kubernetes:Network:EgressCidr", "146.120.224.52/32"),
                ("Kubernetes:Network:FedoraRepositoryBaseUrl", "https://packages.lumina.1t.ru/fedora"),
                ("MinIO:RunnerEndpoint", "minio.example:443"))));
    }

    [Fact]
    public void Create_ProducesHardenedArchitecturePinnedJob()
    {
        var job = Job();
        var runner = new KubernetesRunner(job.BuildProfile, "arm64", Digest);
        var manifest = KubernetesJobFactory.Create(
            job,
            runner,
            KubernetesBuildPolicy.ResolveLimits(Configuration()),
            Network());
        var pod = manifest.Spec.Template.Spec;
        var container = Assert.Single(pod.Containers);

        Assert.Equal("batch/v1", manifest.ApiVersion);
        Assert.True(manifest.Spec.Suspend);
        Assert.Equal(0, manifest.Spec.BackoffLimit);
        Assert.Equal("Never", pod.RestartPolicy);
        Assert.False(pod.AutomountServiceAccountToken);
        Assert.Equal(KubernetesJobFactory.RunnerServiceAccountName, pod.ServiceAccountName);
        Assert.False(pod.EnableServiceLinks);
        Assert.False(pod.HostNetwork);
        Assert.False(pod.HostPID);
        Assert.False(pod.HostIPC);
        Assert.False(pod.HostUsers);
        Assert.Null(pod.SecurityContext.FsGroup);
        Assert.Equal("arm64", pod.NodeSelector["kubernetes.io/arch"]);
        Assert.Equal("true", pod.NodeSelector[KubernetesJobFactory.WorkerLabel]);
        var hostAlias = Assert.Single(pod.HostAliases);
        Assert.Equal("10.77.0.1", hostAlias.Ip);
        Assert.Equal(["packages.lumina.1t.ru"], hostAlias.Hostnames);
        Assert.Equal("NoSchedule", Assert.Single(pod.Tolerations).Effect);
        Assert.Equal(Digest, container.Image);
        Assert.False(container.SecurityContext.RunAsNonRoot);
        Assert.Equal(0, container.SecurityContext.RunAsUser);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.False(container.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Equal(["ALL"], container.SecurityContext.Capabilities.Drop);
        Assert.Equal(
            ["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETFCAP", "SETGID", "SETUID"],
            container.SecurityContext.Capabilities.Add);
        Assert.Equal("RuntimeDefault", container.SecurityContext.SeccompProfile.Type);
        Assert.Equal("RuntimeDefault", pod.SecurityContext.SeccompProfile.Type);
        Assert.NotNull(container.Resources.Requests["ephemeral-storage"]);
        Assert.NotNull(container.Resources.Limits["ephemeral-storage"]);
        Assert.All(pod.Volumes, volume => Assert.Null(volume.HostPath));
        var transportVolume = Assert.Single(pod.Volumes, volume =>
            volume.Name == KubernetesJobFactory.TransportVolumeName);
        Assert.Equal(
            KubernetesBuildTransportPolicy.SecretName(manifest.Metadata.Name),
            transportVolume.Secret.SecretName);
        Assert.False(transportVolume.Secret.Optional);
        Assert.Equal(0x100, transportVolume.Secret.DefaultMode);
        var transportMount = Assert.Single(container.VolumeMounts, mount =>
            mount.Name == KubernetesJobFactory.TransportVolumeName);
        Assert.True(transportMount.ReadOnlyProperty);
        Assert.Equal(KubernetesJobFactory.TransportMountPath, transportMount.MountPath);
        Assert.DoesNotContain(container.Env, variable =>
            variable.Name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
            variable.Name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
            variable.Name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "jetson/kernel-tegra.spec",
            Assert.Single(container.Env, variable => variable.Name == "SPEC_PATH_IN_REPO").Value);
        Assert.Equal(
            "sha256:" + new string('a', 64),
            Assert.Single(container.Env, variable => variable.Name == "RUNNER_IMAGE_DIGEST").Value);
        var serialized = KubernetesJson.Serialize(manifest);
        Assert.Contains("\"automountServiceAccountToken\":false", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("hostPath", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateNetworkPolicy_AllowsOnlyExactHttpsOriginAndCoreDns()
    {
        var job = Job();

        var policy = KubernetesJobFactory.CreateNetworkPolicy(job, Network());

        Assert.Equal(["Ingress", "Egress"], policy.Spec.PolicyTypes);
        Assert.Empty(policy.Spec.Ingress);
        Assert.Equal(2, policy.Spec.Egress.Count);
        var https = policy.Spec.Egress[0];
        Assert.Equal("10.77.0.1/32", Assert.Single(https.To).IpBlock.Cidr);
        Assert.Equal("443", Assert.Single(https.Ports).Port.Value);
        var dns = policy.Spec.Egress[1];
        Assert.Equal("kube-system", Assert.Single(dns.To).NamespaceSelector
            .MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal(["TCP", "UDP"], dns.Ports.Select(port => port.Protocol).Order().ToArray());
        Assert.Equal(
            job.Id.ToString("N"),
            policy.Spec.PodSelector.MatchLabels["lumina.1t.ru/build-job-id"]);
    }

    [Fact]
    public void Create_RejectsUnapprovedRunnerAndUnsafeProvenanceLabels()
    {
        var job = Job();
        var limits = KubernetesBuildPolicy.ResolveLimits(Configuration());

        Assert.Throws<ValidationException>(() => KubernetesJobFactory.Create(
            job, new KubernetesRunner(job.BuildProfile, "amd64", Digest), limits, Network()));
        Assert.Throws<ValidationException>(() => KubernetesJobFactory.Create(
            job, new KubernetesRunner(job.BuildProfile, "arm64", "runner:latest"), limits, Network()));
        job.CommitSha = "bad/label";
        Assert.Throws<ValidationException>(() => KubernetesJobFactory.Create(
            job, new KubernetesRunner(job.BuildProfile, "arm64", Digest), limits, Network()));
    }

    [Fact]
    public void Create_RejectsMissingOrAmbiguousSourceSpecPath()
    {
        var job = Job();
        var limits = KubernetesBuildPolicy.ResolveLimits(Configuration());
        var runner = new KubernetesRunner(job.BuildProfile, "arm64", Digest);
        job.SourceUrl = "git://https://example.com/lumina.git#branch=main";
        Assert.Throws<ValidationException>(() => KubernetesJobFactory.Create(job, runner, limits, Network()));

        job.SourceUrl = "git://https://example.com/lumina.git#specPath=one.spec&specPath=two.spec";
        Assert.Throws<ValidationException>(() => KubernetesJobFactory.Create(job, runner, limits, Network()));
    }

    private static BuildJob Job() => new()
    {
        Id = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        SpecName = "kernel-tegra.spec",
        SourceUrl = "git://https://example.com/lumina.git#branch=main&specPath=jetson/kernel-tegra.spec&commit=" + new string('b', 40),
        CommitSha = new string('b', 40),
        TargetDistribution = "fedora",
        TargetRelease = "44",
        TargetArchitecture = "aarch64",
        BuildProfile = "fedora-44-aarch64",
        RunnerImageDigest = "sha256:" + new string('a', 64)
    };

    private static KubernetesBuildNetworkPolicy Network() => new(
        "10.77.0.1/32",
        443,
        "https://packages.lumina.1t.ru/fedora");

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(item =>
                new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build();
}
