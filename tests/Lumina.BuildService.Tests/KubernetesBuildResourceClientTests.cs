using k8s.Models;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesBuildResourceClientTests
{
    private const string Digest =
        "registry.example/lumina/fedora-runner@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task EnsureCreatedAsync_ProtectsJobBeforeCreateAndAttachesOwner()
    {
        var api = new FakeKubernetesApi();
        var client = new KubernetesBuildResourceClient(api);
        var resources = Resources();

        var identity = await client.EnsureCreatedAsync(
            "lumina-builds",
            resources.Job,
            resources.Policy,
            CancellationToken.None);

        Assert.Equal(["create-policy", "create-job", "replace-policy"], api.Calls);
        Assert.Equal(resources.Job.Metadata.Name, identity.JobName);
        Assert.Equal("job-uid-1", identity.JobUid);
        var owner = Assert.Single(api.Policy!.Metadata.OwnerReferences);
        Assert.Equal(identity.JobUid, owner.Uid);
        Assert.Equal("Job", owner.Kind);
    }

    [Fact]
    public async Task EnsureCreatedAsync_ReconcilesDuplicateCreateWithoutChangingUid()
    {
        var api = new FakeKubernetesApi();
        var client = new KubernetesBuildResourceClient(api);
        var first = Resources();
        var firstIdentity = await client.EnsureCreatedAsync(
            "lumina-builds", first.Job, first.Policy, CancellationToken.None);
        api.Calls.Clear();
        var retry = Resources(first.BuildJob);

        var retryIdentity = await client.EnsureCreatedAsync(
            "lumina-builds", retry.Job, retry.Policy, CancellationToken.None);

        Assert.Equal(firstIdentity, retryIdentity);
        Assert.Equal(
            ["create-policy", "read-policy", "create-job", "read-job"],
            api.Calls);
    }

    [Fact]
    public async Task ActivateAsync_CreatesImmutableTransportThenUnsuspendsIdempotently()
    {
        var api = new FakeKubernetesApi();
        var client = new KubernetesBuildResourceClient(api);
        var resources = Resources();
        var identity = await client.EnsureCreatedAsync(
            "lumina-builds", resources.Job, resources.Policy, CancellationToken.None);
        var transport = Transport(resources.BuildJob.Id, identity.JobUid);
        var secret = KubernetesBuildTransportPolicy.CreateSecret(
            identity.Namespace,
            identity.JobName,
            transport);
        api.Calls.Clear();

        await client.ActivateAsync(identity, secret, transport, CancellationToken.None);

        Assert.False(api.Job!.Spec.Suspend);
        Assert.Equal(["read-job", "create-secret", "replace-job"], api.Calls);
        api.Calls.Clear();
        var replay = transport with
        {
            SnapshotDownloadUrl = "https://minio.example/snapshot?signature=replay",
            ArtifactBundleUploadUrl = "https://minio.example/bundle?signature=replay"
        };
        await client.ActivateAsync(
            identity,
            KubernetesBuildTransportPolicy.CreateSecret(identity.Namespace, identity.JobName, replay),
            replay,
            CancellationToken.None);
        Assert.Equal(["read-job", "create-secret", "read-secret"], api.Calls);
    }

    [Fact]
    public async Task ActivateAsync_RejectsChangedTransportSecret()
    {
        var api = new FakeKubernetesApi();
        var client = new KubernetesBuildResourceClient(api);
        var resources = Resources();
        var identity = await client.EnsureCreatedAsync(
            "lumina-builds", resources.Job, resources.Policy, CancellationToken.None);
        var transport = Transport(resources.BuildJob.Id, identity.JobUid);
        api.Secret = KubernetesBuildTransportPolicy.CreateSecret(
            identity.Namespace,
            identity.JobName,
            transport with { SnapshotSha256 = new string('f', 64) });

        await Assert.ThrowsAsync<ConflictException>(() => client.ActivateAsync(
            identity,
            KubernetesBuildTransportPolicy.CreateSecret(identity.Namespace, identity.JobName, transport),
            transport,
            CancellationToken.None));
        Assert.True(api.Job!.Spec.Suspend);
    }

    [Fact]
    public async Task EnsureCreatedAsync_RejectsExistingJobWithDifferentWorkload()
    {
        var api = new FakeKubernetesApi();
        var client = new KubernetesBuildResourceClient(api);
        var first = Resources();
        await client.EnsureCreatedAsync(
            "lumina-builds", first.Job, first.Policy, CancellationToken.None);
        api.Job!.Spec.Template.Spec.Containers.Single().Image =
            "registry.example/other@sha256:" + new string('b', 64);
        var retry = Resources(first.BuildJob);

        await Assert.ThrowsAsync<ConflictException>(() => client.EnsureCreatedAsync(
            "lumina-builds", retry.Job, retry.Policy, CancellationToken.None));
    }

    [Fact]
    public async Task ObserveAsync_MapsStateAndRejectsPodFromDifferentJobUid()
    {
        var api = new FakeKubernetesApi();
        var client = new KubernetesBuildResourceClient(api);
        var resources = Resources();
        var identity = await client.EnsureCreatedAsync(
            "lumina-builds", resources.Job, resources.Policy, CancellationToken.None);
        api.Job!.Status = new V1JobStatus { Active = 1 };
        api.Pods.Add(Pod("pod-1", identity.JobUid, "Running"));

        var running = await client.ObserveAsync(identity, CancellationToken.None);

        Assert.Equal(KubernetesBuildPhase.Running, running.Phase);
        Assert.Equal("pod-1", running.PodName);
        api.Job.Status = new V1JobStatus
        {
            Conditions =
            [
                new V1JobCondition
                {
                    Type = "Complete",
                    Status = "True",
                    Reason = "Completed"
                }
            ]
        };
        var complete = await client.ObserveAsync(identity, CancellationToken.None);
        Assert.Equal(KubernetesBuildPhase.Succeeded, complete.Phase);
        Assert.Equal("Completed", complete.Reason);

        api.Pods[0].Metadata.OwnerReferences![0].Uid = "another-job-uid";
        await Assert.ThrowsAsync<ConflictException>(() =>
            client.ObserveAsync(identity, CancellationToken.None));
    }

    [Fact]
    public async Task ReadLogsAsync_RequiresOwnedPodAndEnforcesBound()
    {
        var api = new FakeKubernetesApi { Logs = "0123456789" };
        var client = new KubernetesBuildResourceClient(api);
        var resources = Resources();
        var identity = await client.EnsureCreatedAsync(
            "lumina-builds", resources.Job, resources.Policy, CancellationToken.None);
        api.Pods.Add(Pod("pod-1", identity.JobUid, "Running"));
        identity = identity with { PodName = "pod-1" };

        var logs = await client.ReadLogsAsync(identity, 5, CancellationToken.None);

        Assert.Equal("01234", logs);
        Assert.Equal(5, api.LastLogLimit);
        await Assert.ThrowsAsync<ConflictException>(() => client.ReadLogsAsync(
            identity with { PodName = "spoofed-pod" },
            5,
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => client.ReadLogsAsync(
            identity,
            1_000_001,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadLogsAsync_TreatsContainerCreatingAsEmptyLogs()
    {
        var api = new FakeKubernetesApi { ContainerReady = false };
        var client = new KubernetesBuildResourceClient(api);
        var resources = Resources();
        var identity = await client.EnsureCreatedAsync(
            "lumina-builds", resources.Job, resources.Policy, CancellationToken.None);
        api.Pods.Add(Pod("pod-1", identity.JobUid, "Pending"));
        identity = identity with { PodName = "pod-1" };

        var logs = await client.ReadLogsAsync(identity, 5, CancellationToken.None);

        Assert.Empty(logs);
    }

    [Fact]
    public async Task DeleteAsync_UsesUidPreconditionAndCleansOrphanedPolicy()
    {
        var api = new FakeKubernetesApi();
        var client = new KubernetesBuildResourceClient(api);
        var resources = Resources();
        var identity = await client.EnsureCreatedAsync(
            "lumina-builds", resources.Job, resources.Policy, CancellationToken.None);

        await client.DeleteAsync(identity, CancellationToken.None);

        Assert.Equal(identity.JobUid, api.DeletedJobUid);
        api.Job = null;
        await client.DeleteAsync(identity, CancellationToken.None);
        Assert.Equal($"{identity.JobName}-deny", api.DeletedPolicyName);

        api.Job = resources.Job;
        api.Job.Metadata.Uid = "replacement-uid";
        await Assert.ThrowsAsync<ConflictException>(() =>
            client.DeleteAsync(identity, CancellationToken.None));
    }

    private static TestResources Resources(BuildJob? existing = null)
    {
        var buildJob = existing ?? new BuildJob
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
        var runner = new KubernetesRunner(buildJob.BuildProfile, "arm64", Digest);
        var network = new KubernetesBuildNetworkPolicy(
            "146.120.224.52/32",
            443,
            "https://packages.lumina.1t.ru/fedora");
        return new TestResources(
            buildJob,
            KubernetesJobFactory.Create(
                buildJob,
                runner,
                new KubernetesJobLimits(7200, 86400, "500m", "2", "1Gi", "4Gi", "4Gi", "16Gi"),
                network),
            KubernetesJobFactory.CreateNetworkPolicy(buildJob, network));
    }

    private static V1Pod Pod(string name, string jobUid, string phase) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            CreationTimestamp = DateTime.UtcNow,
            OwnerReferences =
            [
                new V1OwnerReference
                {
                    ApiVersion = "batch/v1",
                    Kind = "Job",
                    Name = "build-job",
                    Uid = jobUid,
                    Controller = true
                }
            ]
        },
        Status = new V1PodStatus { Phase = phase }
    };

    private static KubernetesBuildTransport Transport(Guid jobId, string jobUid) => new(
        KubernetesBuildTransportPolicy.CurrentVersion,
        jobId,
        jobUid,
        "project/source/verified.tar.gz",
        new string('a', 64),
        4096,
        "https://minio.example/snapshot?signature=download",
        KubernetesBuildTransportPolicy.BundleObjectName(jobId, jobUid),
        "https://minio.example/bundle?signature=upload",
        DateTimeOffset.UtcNow.AddHours(1));

    private sealed record TestResources(BuildJob BuildJob, V1Job Job, V1NetworkPolicy Policy);

    private sealed class FakeKubernetesApi : IKubernetesApiOperations
    {
        public List<string> Calls { get; } = [];
        public V1Job? Job { get; set; }
        public V1NetworkPolicy? Policy { get; set; }
        public V1Secret? Secret { get; set; }
        public List<V1Pod> Pods { get; } = [];
        public string Logs { get; set; } = string.Empty;
        public bool ContainerReady { get; set; } = true;
        public int LastLogLimit { get; private set; }
        public string? DeletedJobUid { get; private set; }
        public string? DeletedPolicyName { get; private set; }

        public Task<V1NetworkPolicy> CreateNetworkPolicyAsync(
            string buildNamespace,
            V1NetworkPolicy policy,
            CancellationToken cancellationToken)
        {
            Calls.Add("create-policy");
            if (Policy != null)
                throw new KubernetesResourceAlreadyExistsException();
            Policy = policy;
            Policy.Metadata.NamespaceProperty = buildNamespace;
            Policy.Metadata.Uid = "policy-uid-1";
            Policy.Metadata.ResourceVersion = "1";
            // Kubernetes normalizes an explicit empty ingress array to an
            // omitted field when persisting a deny-all NetworkPolicy.
            Policy.Spec.Ingress = null;
            return Task.FromResult(Policy);
        }

        public Task<V1NetworkPolicy> ReadNetworkPolicyAsync(
            string buildNamespace,
            string name,
            CancellationToken cancellationToken)
        {
            Calls.Add("read-policy");
            return Task.FromResult(Policy ?? throw new KubernetesResourceNotFoundException());
        }

        public Task<V1NetworkPolicy> ReplaceNetworkPolicyAsync(
            string buildNamespace,
            string name,
            V1NetworkPolicy policy,
            CancellationToken cancellationToken)
        {
            Calls.Add("replace-policy");
            Policy = policy;
            Policy.Metadata.ResourceVersion = "2";
            Policy.Spec.Ingress = null;
            return Task.FromResult(Policy);
        }

        public Task DeleteNetworkPolicyAsync(
            string buildNamespace,
            string name,
            CancellationToken cancellationToken)
        {
            Calls.Add("delete-policy");
            if (Policy == null)
                throw new KubernetesResourceNotFoundException();
            DeletedPolicyName = name;
            Policy = null;
            return Task.CompletedTask;
        }

        public Task<V1Job> CreateJobAsync(
            string buildNamespace,
            V1Job job,
            CancellationToken cancellationToken)
        {
            Calls.Add("create-job");
            if (Job != null)
                throw new KubernetesResourceAlreadyExistsException();
            Job = job;
            Job.Metadata.NamespaceProperty = buildNamespace;
            Job.Metadata.Uid = "job-uid-1";
            Job.Metadata.ResourceVersion = "1";
            return Task.FromResult(Job);
        }

        public Task<V1Job> ReadJobAsync(
            string buildNamespace,
            string name,
            CancellationToken cancellationToken)
        {
            Calls.Add("read-job");
            return Task.FromResult(Job ?? throw new KubernetesResourceNotFoundException());
        }

        public Task<V1Job> ReplaceJobAsync(
            string buildNamespace,
            string name,
            V1Job job,
            CancellationToken cancellationToken)
        {
            Calls.Add("replace-job");
            Job = job;
            Job.Metadata.ResourceVersion = "2";
            return Task.FromResult(Job);
        }

        public Task<V1Secret> CreateSecretAsync(
            string buildNamespace,
            V1Secret secret,
            CancellationToken cancellationToken)
        {
            Calls.Add("create-secret");
            if (Secret != null)
                throw new KubernetesResourceAlreadyExistsException();
            Secret = secret;
            Secret.Metadata.NamespaceProperty = buildNamespace;
            Secret.Metadata.ResourceVersion = "1";
            return Task.FromResult(Secret);
        }

        public Task<V1Secret> ReadSecretAsync(
            string buildNamespace,
            string name,
            CancellationToken cancellationToken)
        {
            Calls.Add("read-secret");
            return Task.FromResult(Secret ?? throw new KubernetesResourceNotFoundException());
        }

        public Task<V1PodList> ListPodsAsync(
            string buildNamespace,
            string labelSelector,
            CancellationToken cancellationToken)
        {
            Calls.Add("list-pods");
            return Task.FromResult(new V1PodList { Items = Pods });
        }

        public Task<string> ReadPodLogAsync(
            string buildNamespace,
            string podName,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            Calls.Add("read-log");
            LastLogLimit = maximumBytes;
            if (!ContainerReady)
                throw new KubernetesContainerNotReadyException();
            return Task.FromResult(Logs);
        }

        public Task DeleteJobAsync(
            string buildNamespace,
            string name,
            string uid,
            CancellationToken cancellationToken)
        {
            Calls.Add("delete-job");
            DeletedJobUid = uid;
            return Task.CompletedTask;
        }
    }
}
