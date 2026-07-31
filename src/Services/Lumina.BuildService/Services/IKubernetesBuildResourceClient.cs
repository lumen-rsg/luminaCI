using k8s.Models;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services;

public sealed record KubernetesBuildResourceIdentity(
    string Namespace,
    string JobName,
    string JobUid,
    string? PodName);

public enum KubernetesBuildPhase
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Unknown = 4
}

public sealed record KubernetesBuildObservation(
    KubernetesBuildPhase Phase,
    string? PodName,
    string? Reason,
    DateTimeOffset ObservedAt);

/// <summary>
/// Narrow cluster boundary used by the future launcher. It deliberately
/// exposes Jobs, their matching NetworkPolicy, observations, bounded logs,
/// and deletion only—not arbitrary Kubernetes resources or generic API calls.
/// </summary>
public interface IKubernetesBuildResourceClient
{
    Task<KubernetesBuildResourceIdentity> EnsureCreatedAsync(
        string buildNamespace,
        V1Job job,
        V1NetworkPolicy networkPolicy,
        CancellationToken cancellationToken);

    Task ActivateAsync(
        KubernetesBuildResourceIdentity identity,
        V1Secret transportSecret,
        KubernetesBuildTransport requestedTransport,
        CancellationToken cancellationToken);

    Task<KubernetesBuildObservation> ObserveAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken);

    Task<string> ReadLogsAsync(
        KubernetesBuildResourceIdentity identity,
        int maximumCharacters,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        KubernetesBuildResourceIdentity identity,
        CancellationToken cancellationToken);
}

public static class KubernetesBuildIdentity
{
    public static string JobName(Guid buildJobId)
    {
        if (buildJobId == Guid.Empty)
            throw new ValidationException("Kubernetes build job ID cannot be empty.");
        return $"lumina-build-{buildJobId:N}";
    }

    public static void Apply(BuildJob job, KubernetesBuildResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(identity);
        if (job.ExecutionBackend != BuildExecutorBackend.Kubernetes ||
            !string.Equals(identity.JobName, JobName(job.Id), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.Namespace) ||
            string.IsNullOrWhiteSpace(identity.JobUid))
        {
            throw new ValidationException("Kubernetes resource identity does not match the build job.");
        }
        if (!string.IsNullOrWhiteSpace(job.KubernetesJobUid) &&
            !string.Equals(job.KubernetesJobUid, identity.JobUid, StringComparison.Ordinal))
        {
            throw new ConflictException("Kubernetes Job UID changed for an existing build.");
        }
        if (!string.IsNullOrWhiteSpace(job.KubernetesNamespace) &&
            !string.Equals(job.KubernetesNamespace, identity.Namespace, StringComparison.Ordinal))
        {
            throw new ConflictException("Kubernetes namespace changed for an existing build.");
        }

        job.KubernetesNamespace = identity.Namespace;
        job.KubernetesJobName = identity.JobName;
        job.KubernetesJobUid = identity.JobUid;
        job.KubernetesPodName = identity.PodName;
    }
}
