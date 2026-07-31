using k8s;
using k8s.Models;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.BuildService.Services;

internal interface INativePromotionGateResourceClient
{
    Task<KubernetesBuildResourceIdentity> EnsureCreatedAsync(
        string buildNamespace,
        V1Job job,
        V1NetworkPolicy networkPolicy,
        CancellationToken cancellationToken);

    Task ActivateAsync(
        KubernetesBuildResourceIdentity identity,
        V1Secret secret,
        NativePromotionGate gate,
        CancellationToken cancellationToken);

    Task<KubernetesBuildObservation> ObserveAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken);

    Task<string> ReadLogsAsync(
        KubernetesBuildResourceIdentity identity,
        int maximumCharacters,
        CancellationToken cancellationToken);
}

internal sealed class NativePromotionGateResourceClient(IKubernetesApiOperations api)
    : INativePromotionGateResourceClient
{
    private const int MaximumLogCharacters = 1_000_000;

    public async Task<KubernetesBuildResourceIdentity> EnsureCreatedAsync(
        string buildNamespace,
        V1Job job,
        V1NetworkPolicy networkPolicy,
        CancellationToken cancellationToken)
    {
        ValidateCreation(buildNamespace, job, networkPolicy);
        V1NetworkPolicy persistedPolicy;
        try
        {
            persistedPolicy = await api.CreateNetworkPolicyAsync(
                buildNamespace, networkPolicy, cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException)
        {
            persistedPolicy = await api.ReadNetworkPolicyAsync(
                buildNamespace, networkPolicy.Metadata.Name, cancellationToken);
            ValidatePolicy(networkPolicy, persistedPolicy);
        }

        V1Job persistedJob;
        try
        {
            persistedJob = await api.CreateJobAsync(buildNamespace, job, cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException)
        {
            persistedJob = await api.ReadJobAsync(
                buildNamespace, job.Metadata.Name, cancellationToken);
        }
        ValidateJob(job, persistedJob);
        var jobName = Required(persistedJob.Metadata?.Name, "Native gate Job name is missing.");
        var jobUid = Required(persistedJob.Metadata?.Uid, "Native gate Job UID is missing.");

        if (persistedPolicy.Metadata?.OwnerReferences is not { Count: > 0 })
        {
            persistedPolicy.Metadata ??= new V1ObjectMeta();
            persistedPolicy.Metadata.OwnerReferences =
            [
                new V1OwnerReference
                {
                    ApiVersion = "batch/v1", Kind = "Job", Controller = true,
                    Name = jobName, Uid = jobUid
                }
            ];
            persistedPolicy = await api.ReplaceNetworkPolicyAsync(
                buildNamespace, persistedPolicy.Metadata.Name, persistedPolicy, cancellationToken);
        }
        if (persistedPolicy.Metadata?.OwnerReferences is not [{ Kind: "Job" } owner] || owner.Uid != jobUid)
            throw new ConflictException("Native gate NetworkPolicy owner changed.");
        ValidatePolicy(networkPolicy, persistedPolicy);
        return new KubernetesBuildResourceIdentity(
            buildNamespace, jobName, jobUid, null);
    }

    public async Task ActivateAsync(
        KubernetesBuildResourceIdentity identity,
        V1Secret secret,
        NativePromotionGate gate,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity, gate.Id);
        var job = await ReadJobAsync(identity, cancellationToken);
        V1Secret persisted;
        try
        {
            persisted = await api.CreateSecretAsync(identity.Namespace, secret, cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException)
        {
            persisted = await api.ReadSecretAsync(
                identity.Namespace,
                NativePromotionGateTransportPolicy.SecretName(identity.JobName),
                cancellationToken);
        }
        _ = NativePromotionGateTransportPolicy.ValidateSecret(persisted, identity, gate);
        if (job.Spec?.Suspend == false)
            return;
        if (job.Spec?.Suspend != true)
            throw new ConflictException("Native gate Job suspension state is invalid.");
        job.Spec.Suspend = false;
        var activated = await api.ReplaceJobAsync(
            identity.Namespace, identity.JobName, job, cancellationToken);
        ValidateIdentity(activated, identity, gate.Id);
        if (activated.Spec?.Suspend != false)
            throw new ConflictException("Native gate Job did not activate.");
    }

    public async Task<KubernetesBuildObservation> ObserveAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        var gateId = NativePromotionGateId(identity.JobName);
        var job = await ReadJobAsync(identity, cancellationToken);
        var pods = await OwnedPodsAsync(identity, cancellationToken);
        var pod = pods.OrderByDescending(item => item.Metadata?.CreationTimestamp)
            .ThenBy(item => item.Metadata?.Name, StringComparer.Ordinal).FirstOrDefault();
        var condition = job.Status?.Conditions?
            .LastOrDefault(item => string.Equals(item.Status, "True", StringComparison.OrdinalIgnoreCase));
        var phase = string.Equals(condition?.Type, "Complete", StringComparison.Ordinal)
            ? KubernetesBuildPhase.Succeeded
            : string.Equals(condition?.Type, "Failed", StringComparison.Ordinal)
                ? KubernetesBuildPhase.Failed
                : job.Status?.Active > 0 || string.Equals(pod?.Status?.Phase, "Running", StringComparison.Ordinal)
                    ? KubernetesBuildPhase.Running
                    : pod == null || string.Equals(pod.Status?.Phase, "Pending", StringComparison.Ordinal)
                        ? KubernetesBuildPhase.Pending
                        : KubernetesBuildPhase.Unknown;
        ValidateIdentity(job, identity, gateId);
        return new KubernetesBuildObservation(
            phase, pod?.Metadata?.Name,
            condition?.Reason ?? pod?.Status?.Reason ?? pod?.Status?.Phase,
            DateTimeOffset.UtcNow);
    }

    public async Task<string> ReadLogsAsync(
        KubernetesBuildResourceIdentity identity,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (maximumCharacters is <= 0 or > MaximumLogCharacters)
            throw new ValidationException("Native gate log limit is invalid.");
        if (string.IsNullOrWhiteSpace(identity.PodName))
            return string.Empty;
        var pods = await OwnedPodsAsync(identity, cancellationToken);
        if (!pods.Any(item => item.Metadata?.Name == identity.PodName))
            throw new ConflictException("Native gate Pod does not belong to its Job.");
        try
        {
            var logs = await api.ReadPodLogAsync(
                identity.Namespace, identity.PodName, maximumCharacters, cancellationToken);
            return logs.Length <= maximumCharacters ? logs : logs[..maximumCharacters];
        }
        catch (KubernetesContainerNotReadyException)
        {
            return string.Empty;
        }
    }

    private async Task<V1Job> ReadJobAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await api.ReadJobAsync(identity.Namespace, identity.JobName, cancellationToken);
            ValidateIdentity(job, identity, NativePromotionGateId(identity.JobName));
            return job;
        }
        catch (KubernetesResourceNotFoundException exception)
        {
            throw new NotFoundException("Native promotion gate Job no longer exists.", exception);
        }
    }

    private async Task<IReadOnlyList<V1Pod>> OwnedPodsAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        var result = await api.ListPodsAsync(
            identity.Namespace, $"batch.kubernetes.io/job-name={identity.JobName}", cancellationToken);
        foreach (var pod in result.Items ?? [])
        {
            if (pod.Metadata?.OwnerReferences?.Any(owner =>
                    owner.Kind == "Job" && owner.Uid == identity.JobUid) != true)
                throw new ConflictException("Native gate Pod owner UID changed.");
        }
        return (result.Items ?? []).ToList();
    }

    private static void ValidateCreation(string ns, V1Job job, V1NetworkPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(ns) || job.Spec?.Suspend != true ||
            policy.Metadata?.Name != $"{job.Metadata?.Name}-deny")
            throw new ValidationException("Native gate creation intent is invalid.");
        var id = NativePromotionGateId(job.Metadata?.Name ?? string.Empty);
        if (Label(job.Metadata?.Labels) != id.ToString("N"))
            throw new ValidationException("Native gate Job label is invalid.");
        ValidatePolicy(policy, policy);
    }

    private static void ValidateJob(V1Job requested, V1Job persisted)
    {
        var expected = requested.Spec?.Template?.Spec?.Containers?.SingleOrDefault();
        var actual = persisted.Spec?.Template?.Spec?.Containers?.SingleOrDefault();
        if (persisted.Metadata?.Name != requested.Metadata?.Name ||
            Label(persisted.Metadata?.Labels) != Label(requested.Metadata?.Labels) ||
            persisted.Spec?.Template?.Spec?.AutomountServiceAccountToken != false ||
            expected == null || actual == null || actual.Name != expected.Name ||
            actual.Image != expected.Image ||
            !(actual.Command ?? []).SequenceEqual(expected.Command ?? []) ||
            !(actual.Args ?? []).SequenceEqual(expected.Args ?? []))
            throw new ConflictException("Existing native gate Job workload changed.");
    }

    private static void ValidatePolicy(V1NetworkPolicy requested, V1NetworkPolicy persisted)
    {
        if (persisted.Metadata?.Name != requested.Metadata?.Name ||
            Label(persisted.Metadata?.Labels) != Label(requested.Metadata?.Labels) ||
            Label(persisted.Spec?.PodSelector?.MatchLabels) != Label(requested.Spec?.PodSelector?.MatchLabels) ||
            KubernetesJson.Serialize(persisted.Spec?.Egress) != KubernetesJson.Serialize(requested.Spec?.Egress) ||
            persisted.Spec?.Ingress is { Count: > 0 } ||
            persisted.Spec?.PolicyTypes?.ToHashSet(StringComparer.Ordinal) is not { } types ||
            !types.SetEquals(["Ingress", "Egress"]))
            throw new ConflictException("Existing native gate NetworkPolicy changed.");
    }

    private static void ValidateIdentity(
        V1Job job,
        KubernetesBuildResourceIdentity identity,
        Guid gateId)
    {
        if (job.Metadata?.Name != identity.JobName || job.Metadata?.Uid != identity.JobUid ||
            Label(job.Metadata?.Labels) != gateId.ToString("N"))
            throw new ConflictException("Native gate Kubernetes identity changed.");
    }

    private static void ValidateIdentity(KubernetesBuildResourceIdentity identity, Guid gateId)
    {
        if (identity.JobName != NativePromotionGateIdentity.JobName(gateId) ||
            string.IsNullOrWhiteSpace(identity.Namespace) || string.IsNullOrWhiteSpace(identity.JobUid))
            throw new ValidationException("Native gate Kubernetes identity is invalid.");
    }

    private static Guid NativePromotionGateId(string name)
    {
        const string prefix = "lumina-gate-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out var value))
            throw new ValidationException("Native gate Job name is invalid.");
        return value;
    }

    private static string? Label(IDictionary<string, string>? labels) =>
        labels != null && labels.TryGetValue(NativePromotionGateJobFactory.GateIdLabel, out var value)
            ? value
            : null;

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ValidationException(message) : value;
}
