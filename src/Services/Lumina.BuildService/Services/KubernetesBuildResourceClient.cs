using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services;

internal sealed class KubernetesResourceAlreadyExistsException : Exception;
internal sealed class KubernetesResourceNotFoundException : Exception;

internal interface IKubernetesApiOperations
{
    Task<V1NetworkPolicy> CreateNetworkPolicyAsync(
        string buildNamespace,
        V1NetworkPolicy policy,
        CancellationToken cancellationToken);

    Task<V1NetworkPolicy> ReadNetworkPolicyAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken);

    Task<V1NetworkPolicy> ReplaceNetworkPolicyAsync(
        string buildNamespace,
        string name,
        V1NetworkPolicy policy,
        CancellationToken cancellationToken);

    Task DeleteNetworkPolicyAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken);

    Task<V1Job> CreateJobAsync(
        string buildNamespace,
        V1Job job,
        CancellationToken cancellationToken);

    Task<V1Job> ReadJobAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken);

    Task<V1Job> ReplaceJobAsync(
        string buildNamespace,
        string name,
        V1Job job,
        CancellationToken cancellationToken);

    Task<V1Secret> CreateSecretAsync(
        string buildNamespace,
        V1Secret secret,
        CancellationToken cancellationToken);

    Task<V1Secret> ReadSecretAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken);

    Task<V1PodList> ListPodsAsync(
        string buildNamespace,
        string labelSelector,
        CancellationToken cancellationToken);

    Task<string> ReadPodLogAsync(
        string buildNamespace,
        string podName,
        int maximumBytes,
        CancellationToken cancellationToken);

    Task DeleteJobAsync(
        string buildNamespace,
        string name,
        string uid,
        CancellationToken cancellationToken);
}

internal sealed class KubernetesApiOperations(Kubernetes client) : IKubernetesApiOperations
{
    private const string FieldManager = "lumina-build-service";

    public Task<V1NetworkPolicy> CreateNetworkPolicyAsync(
        string buildNamespace,
        V1NetworkPolicy policy,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.CreateNamespacedNetworkPolicyAsync(
            policy,
            buildNamespace,
            fieldManager: FieldManager,
            fieldValidation: "Strict",
            cancellationToken: cancellationToken));

    public Task<V1NetworkPolicy> ReadNetworkPolicyAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.ReadNamespacedNetworkPolicyAsync(
            name,
            buildNamespace,
            cancellationToken: cancellationToken));

    public Task<V1NetworkPolicy> ReplaceNetworkPolicyAsync(
        string buildNamespace,
        string name,
        V1NetworkPolicy policy,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.ReplaceNamespacedNetworkPolicyAsync(
            policy,
            name,
            buildNamespace,
            fieldManager: FieldManager,
            fieldValidation: "Strict",
            cancellationToken: cancellationToken));

    public async Task DeleteNetworkPolicyAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken) =>
        await TranslateAsync(() => client.DeleteNamespacedNetworkPolicyAsync(
            name,
            buildNamespace,
            cancellationToken: cancellationToken));

    public Task<V1Job> CreateJobAsync(
        string buildNamespace,
        V1Job job,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.CreateNamespacedJobAsync(
            job,
            buildNamespace,
            fieldManager: FieldManager,
            fieldValidation: "Strict",
            cancellationToken: cancellationToken));

    public Task<V1Job> ReadJobAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.ReadNamespacedJobAsync(
            name,
            buildNamespace,
            cancellationToken: cancellationToken));

    public Task<V1Job> ReplaceJobAsync(
        string buildNamespace,
        string name,
        V1Job job,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.ReplaceNamespacedJobAsync(
            job,
            name,
            buildNamespace,
            fieldManager: FieldManager,
            fieldValidation: "Strict",
            cancellationToken: cancellationToken));

    public Task<V1Secret> CreateSecretAsync(
        string buildNamespace,
        V1Secret secret,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.CreateNamespacedSecretAsync(
            secret,
            buildNamespace,
            fieldManager: FieldManager,
            fieldValidation: "Strict",
            cancellationToken: cancellationToken));

    public Task<V1Secret> ReadSecretAsync(
        string buildNamespace,
        string name,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.ReadNamespacedSecretAsync(
            name,
            buildNamespace,
            cancellationToken: cancellationToken));

    public Task<V1PodList> ListPodsAsync(
        string buildNamespace,
        string labelSelector,
        CancellationToken cancellationToken) =>
        TranslateAsync(() => client.ListNamespacedPodAsync(
            buildNamespace,
            labelSelector: labelSelector,
            cancellationToken: cancellationToken));

    public async Task<string> ReadPodLogAsync(
        string buildNamespace,
        string podName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await TranslateAsync(() => client.ReadNamespacedPodLogAsync(
            podName,
            buildNamespace,
            container: KubernetesJobFactory.ContainerName,
            follow: false,
            limitBytes: maximumBytes,
            timestamps: false,
            cancellationToken: cancellationToken));
        using var reader = new StreamReader(stream);
        var buffer = new char[maximumBytes];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return new string(buffer, 0, count);
    }

    public async Task DeleteJobAsync(
        string buildNamespace,
        string name,
        string uid,
        CancellationToken cancellationToken) =>
        await TranslateAsync(() => client.DeleteNamespacedJobAsync(
            name,
            buildNamespace,
            new V1DeleteOptions
            {
                Preconditions = new V1Preconditions { Uid = uid },
                PropagationPolicy = "Foreground"
            },
            propagationPolicy: "Foreground",
            cancellationToken: cancellationToken));

    private static async Task<T> TranslateAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            throw new KubernetesResourceAlreadyExistsException();
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KubernetesResourceNotFoundException();
        }
    }
}

internal sealed class KubernetesBuildResourceClient(IKubernetesApiOperations api)
    : IKubernetesBuildResourceClient
{
    private const string BuildIdLabel = "lumina.1t.ru/build-job-id";
    private const int MaximumLogCharacters = 1_000_000;

    public async Task<KubernetesBuildResourceIdentity> EnsureCreatedAsync(
        string buildNamespace,
        V1Job job,
        V1NetworkPolicy networkPolicy,
        CancellationToken cancellationToken)
    {
        ValidateCreateRequest(buildNamespace, job, networkPolicy);
        var policy = await EnsureNetworkPolicyAsync(
            buildNamespace,
            networkPolicy,
            cancellationToken);
        var persistedJob = await EnsureJobAsync(buildNamespace, job, cancellationToken);
        ValidatePersistedJob(job, persistedJob);

        var jobUid = RequireValue(persistedJob.Metadata?.Uid, "Kubernetes Job UID is missing.");
        policy = await AttachNetworkPolicyOwnerAsync(
            buildNamespace,
            policy,
            persistedJob,
            cancellationToken);
        ValidateNetworkPolicy(networkPolicy, policy);

        return new KubernetesBuildResourceIdentity(
            buildNamespace,
            RequireValue(persistedJob.Metadata?.Name, "Kubernetes Job name is missing."),
            jobUid,
            null);
    }

    public async Task ActivateAsync(
        KubernetesBuildResourceIdentity identity,
        V1Secret transportSecret,
        KubernetesBuildTransport requestedTransport,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var job = await ReadJobForIdentityAsync(identity, cancellationToken);
        ValidateTransportVolume(job, identity);
        V1Secret persistedSecret;
        try
        {
            persistedSecret = await api.CreateSecretAsync(
                identity.Namespace,
                transportSecret,
                cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException)
        {
            persistedSecret = await ReadAfterCreateConflictAsync(
                () => api.ReadSecretAsync(
                    identity.Namespace,
                    KubernetesBuildTransportPolicy.SecretName(identity.JobName),
                    cancellationToken),
                "Kubernetes transport Secret disappeared during create reconciliation.");
        }
        _ = KubernetesBuildTransportPolicy.ValidateSecret(
            persistedSecret,
            identity,
            requestedTransport);

        if (job.Spec?.Suspend == false)
            return;
        if (job.Spec?.Suspend != true)
            throw new ConflictException("Kubernetes Job suspension state is invalid.");
        job.Spec.Suspend = false;
        V1Job activated;
        try
        {
            activated = await api.ReplaceJobAsync(
                identity.Namespace,
                identity.JobName,
                job,
                cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException exception)
        {
            throw new ConflictException(
                "Kubernetes Job changed while activation was in progress.",
                exception);
        }
        ValidateIdentity(activated, identity);
        ValidateTransportVolume(activated, identity);
        if (activated.Spec?.Suspend != false)
            throw new ConflictException("Kubernetes Job did not leave suspended state.");
    }

    public async Task<KubernetesBuildObservation> ObserveAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        V1Job job;
        try
        {
            job = await api.ReadJobAsync(
                identity.Namespace,
                identity.JobName,
                cancellationToken);
        }
        catch (KubernetesResourceNotFoundException exception)
        {
            throw new NotFoundException("Kubernetes Job no longer exists.", exception);
        }

        ValidateIdentity(job, identity);
        var pods = await ReadOwnedPodsAsync(identity, cancellationToken);
        var pod = pods
            .OrderByDescending(item => item.Metadata?.CreationTimestamp)
            .ThenBy(item => item.Metadata?.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        var condition = job.Status?.Conditions?
            .LastOrDefault(item => string.Equals(item.Status, "True", StringComparison.OrdinalIgnoreCase));
        var phase = ResolvePhase(job, pod, condition);
        var reason = condition?.Reason ?? pod?.Status?.Reason ?? pod?.Status?.Phase;

        return new KubernetesBuildObservation(
            phase,
            pod?.Metadata?.Name,
            reason,
            DateTimeOffset.UtcNow);
    }

    public async Task<string> ReadLogsAsync(
        KubernetesBuildResourceIdentity identity,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        if (maximumCharacters is <= 0 or > MaximumLogCharacters)
        {
            throw new ValidationException(
                $"Kubernetes log limit must be between 1 and {MaximumLogCharacters} characters.");
        }
        if (string.IsNullOrWhiteSpace(identity.PodName))
            return string.Empty;

        var pods = await ReadOwnedPodsAsync(identity, cancellationToken);
        if (!pods.Any(pod => string.Equals(
                pod.Metadata?.Name,
                identity.PodName,
                StringComparison.Ordinal)))
        {
            throw new ConflictException("Kubernetes Pod does not belong to the recorded Job UID.");
        }

        string logs;
        try
        {
            logs = await api.ReadPodLogAsync(
                identity.Namespace,
                identity.PodName,
                maximumCharacters,
                cancellationToken);
        }
        catch (KubernetesResourceNotFoundException exception)
        {
            throw new NotFoundException("Kubernetes Pod logs are no longer available.", exception);
        }
        return logs.Length <= maximumCharacters ? logs : logs[..maximumCharacters];
    }

    public async Task DeleteAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        try
        {
            var job = await api.ReadJobAsync(
                identity.Namespace,
                identity.JobName,
                cancellationToken);
            ValidateIdentity(job, identity);
            await api.DeleteJobAsync(
                identity.Namespace,
                identity.JobName,
                identity.JobUid,
                cancellationToken);
        }
        catch (KubernetesResourceNotFoundException)
        {
            await DeleteOrphanedNetworkPolicyAsync(identity, cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException exception)
        {
            throw new ConflictException(
                "Kubernetes Job changed while its UID-preconditioned deletion was in progress.",
                exception);
        }
    }

    private async Task<V1NetworkPolicy> EnsureNetworkPolicyAsync(
        string buildNamespace,
        V1NetworkPolicy requested,
        CancellationToken cancellationToken)
    {
        try
        {
            return await api.CreateNetworkPolicyAsync(
                buildNamespace,
                requested,
                cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException)
        {
            var existing = await ReadAfterCreateConflictAsync(
                () => api.ReadNetworkPolicyAsync(
                    buildNamespace,
                    requested.Metadata.Name,
                    cancellationToken),
                "Kubernetes NetworkPolicy disappeared during create reconciliation.");
            ValidateNetworkPolicy(requested, existing);
            return existing;
        }
    }

    private async Task<V1Job> EnsureJobAsync(
        string buildNamespace,
        V1Job requested,
        CancellationToken cancellationToken)
    {
        try
        {
            return await api.CreateJobAsync(buildNamespace, requested, cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException)
        {
            return await ReadAfterCreateConflictAsync(
                () => api.ReadJobAsync(
                    buildNamespace,
                    requested.Metadata.Name,
                    cancellationToken),
                "Kubernetes Job disappeared during create reconciliation.");
        }
    }

    private async Task<V1NetworkPolicy> AttachNetworkPolicyOwnerAsync(
        string buildNamespace,
        V1NetworkPolicy policy,
        V1Job job,
        CancellationToken cancellationToken)
    {
        var uid = RequireValue(job.Metadata?.Uid, "Kubernetes Job UID is missing.");
        if (policy.Metadata?.OwnerReferences?.Any(owner =>
                string.Equals(owner.Uid, uid, StringComparison.Ordinal)) == true)
        {
            return policy;
        }
        if (policy.Metadata?.OwnerReferences is { Count: > 0 })
            throw new ConflictException("Kubernetes NetworkPolicy owner UID changed.");

        policy.Metadata ??= new V1ObjectMeta();
        policy.Metadata.OwnerReferences =
        [
            new V1OwnerReference
            {
                ApiVersion = "batch/v1",
                Kind = "Job",
                Name = RequireValue(job.Metadata?.Name, "Kubernetes Job name is missing."),
                Uid = uid,
                Controller = true
            }
        ];
        try
        {
            return await api.ReplaceNetworkPolicyAsync(
                buildNamespace,
                policy.Metadata.Name,
                policy,
                cancellationToken);
        }
        catch (KubernetesResourceAlreadyExistsException exception)
        {
            throw new ConflictException(
                "Kubernetes NetworkPolicy changed while attaching its Job owner.",
                exception);
        }
        catch (KubernetesResourceNotFoundException exception)
        {
            throw new ConflictException(
                "Kubernetes NetworkPolicy disappeared while attaching its Job owner.",
                exception);
        }
    }

    private async Task<IReadOnlyList<V1Pod>> ReadOwnedPodsAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        var podList = await api.ListPodsAsync(
            identity.Namespace,
            $"batch.kubernetes.io/job-name={identity.JobName}",
            cancellationToken);
        var pods = podList.Items ?? [];
        foreach (var pod in pods)
        {
            if (pod.Metadata?.OwnerReferences?.Any(owner =>
                    string.Equals(owner.Kind, "Job", StringComparison.Ordinal) &&
                    string.Equals(owner.Uid, identity.JobUid, StringComparison.Ordinal)) != true)
            {
                throw new ConflictException("Kubernetes Pod ownership does not match the recorded Job UID.");
            }
        }
        return pods.ToList();
    }

    private async Task<V1Job> ReadJobForIdentityAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await api.ReadJobAsync(
                identity.Namespace,
                identity.JobName,
                cancellationToken);
            ValidateIdentity(job, identity);
            return job;
        }
        catch (KubernetesResourceNotFoundException exception)
        {
            throw new NotFoundException("Kubernetes Job no longer exists.", exception);
        }
    }

    private async Task DeleteOrphanedNetworkPolicyAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        var name = $"{identity.JobName}-deny";
        try
        {
            var policy = await api.ReadNetworkPolicyAsync(identity.Namespace, name, cancellationToken);
            var expectedBuildId = BuildIdFromJobName(identity.JobName);
            if (Label(policy.Metadata?.Labels, BuildIdLabel) != expectedBuildId)
                throw new ConflictException("Kubernetes NetworkPolicy ownership is invalid.");
            await api.DeleteNetworkPolicyAsync(identity.Namespace, name, cancellationToken);
        }
        catch (KubernetesResourceNotFoundException)
        {
            // Idempotent cleanup: both resources are already absent.
        }
    }

    private static void ValidateCreateRequest(
        string buildNamespace,
        V1Job job,
        V1NetworkPolicy networkPolicy)
    {
        if (string.IsNullOrWhiteSpace(buildNamespace))
            throw new ValidationException("Kubernetes namespace is required.");
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(networkPolicy);
        var jobName = RequireValue(job.Metadata?.Name, "Kubernetes Job name is missing.");
        var policyName = RequireValue(
            networkPolicy.Metadata?.Name,
            "Kubernetes NetworkPolicy name is missing.");
        if (!string.Equals(policyName, $"{jobName}-deny", StringComparison.Ordinal))
            throw new ValidationException("Kubernetes NetworkPolicy name does not match the Job.");
        if (!jobName.StartsWith("lumina-build-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(BuildIdFromJobName(jobName), "N", out _) ||
            Label(job.Metadata?.Labels, BuildIdLabel) != BuildIdFromJobName(jobName) ||
            job.Spec?.Suspend != true)
        {
            throw new ValidationException("Kubernetes Job creation intent is invalid.");
        }
        ValidateTransportVolume(
            job,
            new KubernetesBuildResourceIdentity(buildNamespace, jobName, "pending", null));
        ValidateNetworkPolicy(networkPolicy, networkPolicy);
    }

    private static void ValidatePersistedJob(V1Job requested, V1Job persisted)
    {
        var expectedName = RequireValue(requested.Metadata?.Name, "Kubernetes Job name is missing.");
        if (!string.Equals(persisted.Metadata?.Name, expectedName, StringComparison.Ordinal) ||
            Label(persisted.Metadata?.Labels, BuildIdLabel) !=
            Label(requested.Metadata?.Labels, BuildIdLabel) ||
            persisted.Spec?.Template?.Spec?.AutomountServiceAccountToken != false)
        {
            throw new ConflictException("Existing Kubernetes Job is not owned by this build request.");
        }

        var expectedContainer = requested.Spec?.Template?.Spec?.Containers?.SingleOrDefault();
        var actualContainer = persisted.Spec?.Template?.Spec?.Containers?.SingleOrDefault();
        if (expectedContainer == null || actualContainer == null ||
            !string.Equals(actualContainer.Name, expectedContainer.Name, StringComparison.Ordinal) ||
            !string.Equals(actualContainer.Image, expectedContainer.Image, StringComparison.Ordinal) ||
            !(actualContainer.Command ?? []).SequenceEqual(expectedContainer.Command ?? []) ||
            !(actualContainer.Args ?? []).SequenceEqual(expectedContainer.Args ?? []))
        {
            throw new ConflictException("Existing Kubernetes Job workload differs from this build request.");
        }
    }

    private static void ValidateTransportVolume(
        V1Job job,
        KubernetesBuildResourceIdentity identity)
    {
        var pod = job.Spec?.Template?.Spec;
        var container = pod?.Containers?.SingleOrDefault();
        var volume = pod?.Volumes?.SingleOrDefault(item =>
            string.Equals(item.Name, KubernetesJobFactory.TransportVolumeName, StringComparison.Ordinal));
        var mount = container?.VolumeMounts?.SingleOrDefault(item =>
            string.Equals(item.Name, KubernetesJobFactory.TransportVolumeName, StringComparison.Ordinal));
        if (pod?.AutomountServiceAccountToken != false ||
            !string.Equals(pod.ServiceAccountName, KubernetesJobFactory.RunnerServiceAccountName, StringComparison.Ordinal) ||
            !string.Equals(
                volume?.Secret?.SecretName,
                KubernetesBuildTransportPolicy.SecretName(identity.JobName),
                StringComparison.Ordinal) ||
            volume?.Secret?.Optional != false ||
            mount?.ReadOnlyProperty != true ||
            !string.Equals(mount.MountPath, KubernetesJobFactory.TransportMountPath, StringComparison.Ordinal))
        {
            throw new ConflictException("Kubernetes Job transport mount is invalid.");
        }
    }

    private static void ValidateNetworkPolicy(V1NetworkPolicy requested, V1NetworkPolicy persisted)
    {
        var expectedBuildId = Label(requested.Metadata?.Labels, BuildIdLabel);
        if (string.IsNullOrWhiteSpace(expectedBuildId) ||
            !string.Equals(persisted.Metadata?.Name, requested.Metadata?.Name, StringComparison.Ordinal) ||
            Label(persisted.Metadata?.Labels, BuildIdLabel) != expectedBuildId ||
            Label(persisted.Spec?.PodSelector?.MatchLabels, BuildIdLabel) != expectedBuildId ||
            persisted.Spec?.Ingress is not { Count: 0 } ||
            persisted.Spec?.Egress is not { Count: 0 } ||
            persisted.Spec.PolicyTypes?.ToHashSet(StringComparer.Ordinal) is not { } types ||
            !types.SetEquals(["Ingress", "Egress"]))
        {
            throw new ConflictException("Existing Kubernetes NetworkPolicy is not the required default-deny policy.");
        }
    }

    private static void ValidateIdentity(
        V1Job job,
        KubernetesBuildResourceIdentity identity)
    {
        if (!string.Equals(job.Metadata?.Name, identity.JobName, StringComparison.Ordinal) ||
            !string.Equals(job.Metadata?.Uid, identity.JobUid, StringComparison.Ordinal) ||
            Label(job.Metadata?.Labels, BuildIdLabel) != BuildIdFromJobName(identity.JobName))
        {
            throw new ConflictException("Kubernetes Job identity no longer matches the recorded build.");
        }
    }

    private static void ValidateIdentity(KubernetesBuildResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsDnsLabel(identity.Namespace) ||
            string.IsNullOrWhiteSpace(identity.JobName) ||
            string.IsNullOrWhiteSpace(identity.JobUid) ||
            identity.JobUid.Length > 128 ||
            !identity.JobName.StartsWith("lumina-build-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(BuildIdFromJobName(identity.JobName), "N", out _) ||
            identity.PodName?.Length > 253)
        {
            throw new ValidationException("Kubernetes build resource identity is invalid.");
        }
    }

    private static KubernetesBuildPhase ResolvePhase(
        V1Job job,
        V1Pod? pod,
        V1JobCondition? condition)
    {
        if (string.Equals(condition?.Type, "Complete", StringComparison.Ordinal))
            return KubernetesBuildPhase.Succeeded;
        if (string.Equals(condition?.Type, "Failed", StringComparison.Ordinal))
            return KubernetesBuildPhase.Failed;
        if (job.Status?.Active > 0 || string.Equals(pod?.Status?.Phase, "Running", StringComparison.Ordinal))
            return KubernetesBuildPhase.Running;
        if (pod == null || string.Equals(pod.Status?.Phase, "Pending", StringComparison.Ordinal))
            return KubernetesBuildPhase.Pending;
        return KubernetesBuildPhase.Unknown;
    }

    private static string BuildIdFromJobName(string jobName) => jobName["lumina-build-".Length..];

    private static string? Label(IDictionary<string, string>? labels, string name) =>
        labels != null && labels.TryGetValue(name, out var value) ? value : null;

    private static bool IsDnsLabel(string? value) =>
        value is { Length: >= 1 and <= 63 } &&
        IsLowercaseLetterOrDigit(value[0]) &&
        IsLowercaseLetterOrDigit(value[^1]) &&
        value.All(character => IsLowercaseLetterOrDigit(character) || character == '-');

    private static bool IsLowercaseLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string RequireValue(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ValidationException(message) : value;

    private static async Task<T> ReadAfterCreateConflictAsync<T>(
        Func<Task<T>> read,
        string message)
    {
        try
        {
            return await read();
        }
        catch (KubernetesResourceNotFoundException exception)
        {
            throw new ConflictException(message, exception);
        }
    }
}
