using k8s;
using k8s.Models;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.BuildService.Services;

/// <summary>
/// Creates the only Kubernetes Job shape BuildService may submit. No manifest,
/// labels, commands, security context, volumes, or scheduling policy are taken
/// from repository content.
/// </summary>
public static class KubernetesJobFactory
{
    public const string ContainerName = "fedora-builder";
    public const string RunnerServiceAccountName = "lumina-build-runner";
    public const string TransportVolumeName = "transport";
    public const string TransportMountPath = "/run/lumina-transport";
    public const string WorkerLabel = "lumina.1t.ru/build-worker";
    public const string WorkerTaint = "lumina.1t.ru/build-worker";

    public static V1Job Create(
        BuildJob job,
        KubernetesRunner runner,
        KubernetesJobLimits limits)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(limits);
        Validate(job, runner, limits);
        var name = KubernetesBuildIdentity.JobName(job.Id);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app.kubernetes.io/name"] = "lumina-rpm-build",
            ["app.kubernetes.io/managed-by"] = "lumina-ci",
            ["lumina.1t.ru/build-job-id"] = job.Id.ToString("N"),
            ["lumina.1t.ru/pipeline-id"] = job.PipelineId.ToString("N"),
            ["lumina.1t.ru/build-profile"] = job.BuildProfile
        };
        if (!string.IsNullOrWhiteSpace(job.CommitSha))
            labels["lumina.1t.ru/commit"] = job.CommitSha[..Math.Min(40, job.CommitSha.Length)];

        var securityContext = new V1SecurityContext
        {
            AllowPrivilegeEscalation = false,
            ReadOnlyRootFilesystem = false,
            RunAsNonRoot = false,
            RunAsUser = 0,
            RunAsGroup = 0,
            Capabilities = new V1Capabilities
            {
                Drop = ["ALL"],
                Add = ["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETFCAP", "SETGID", "SETUID"]
            },
            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
        };
        var resources = new V1ResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new ResourceQuantity(limits.CpuRequest),
                ["memory"] = new ResourceQuantity(limits.MemoryRequest),
                ["ephemeral-storage"] = new ResourceQuantity(limits.EphemeralStorageRequest)
            },
            Limits = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new ResourceQuantity(limits.CpuLimit),
                ["memory"] = new ResourceQuantity(limits.MemoryLimit),
                ["ephemeral-storage"] = new ResourceQuantity(limits.EphemeralStorageLimit)
            }
        };

        return new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta { Name = name, Labels = labels },
            Spec = new V1JobSpec
            {
                Suspend = true,
                BackoffLimit = 0,
                ActiveDeadlineSeconds = limits.ActiveDeadlineSeconds,
                TtlSecondsAfterFinished = limits.TtlSecondsAfterFinished,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        AutomountServiceAccountToken = false,
                        ServiceAccountName = RunnerServiceAccountName,
                        EnableServiceLinks = false,
                        HostNetwork = false,
                        HostPID = false,
                        HostIPC = false,
                        HostUsers = false,
                        RestartPolicy = "Never",
                        ShareProcessNamespace = false,
                        TerminationGracePeriodSeconds = 30,
                        NodeSelector = new Dictionary<string, string>
                        {
                            ["kubernetes.io/arch"] = runner.Architecture,
                            [WorkerLabel] = "true"
                        },
                        Tolerations =
                        [
                            new V1Toleration
                            {
                                Effect = "NoSchedule",
                                Key = WorkerTaint,
                                OperatorProperty = "Equal",
                                Value = "true"
                            }
                        ],
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = false,
                            RunAsUser = 0,
                            RunAsGroup = 0,
                            FsGroup = 1654,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                        },
                        Containers =
                        [
                            new V1Container
                            {
                                Name = ContainerName,
                                Image = runner.Image,
                                ImagePullPolicy = "IfNotPresent",
                                Command = ["/usr/local/bin/lumina-kubernetes-build"],
                                Args = ["--job-id", job.Id.ToString("D")],
                                Env = SafeEnvironment(job),
                                Resources = resources,
                                SecurityContext = securityContext,
                                VolumeMounts =
                                [
                                    new V1VolumeMount
                                    {
                                        MountPath = TransportMountPath,
                                        Name = TransportVolumeName,
                                        ReadOnlyProperty = true
                                    },
                                    new V1VolumeMount
                                    {
                                        MountPath = "/workspace",
                                        Name = "workspace"
                                    }
                                ]
                            }
                        ],
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = TransportVolumeName,
                                Secret = new V1SecretVolumeSource
                                {
                                    SecretName = KubernetesBuildTransportPolicy.SecretName(name),
                                    Optional = false,
                                    DefaultMode = 0x120
                                }
                            },
                            new V1Volume
                            {
                                Name = "workspace",
                                EmptyDir = new V1EmptyDirVolumeSource
                                {
                                    SizeLimit = new ResourceQuantity(limits.EphemeralStorageLimit)
                                }
                            }
                        ]
                    }
                }
            }
        };
    }

    public static V1NetworkPolicy CreateDefaultDenyNetworkPolicy(BuildJob job)
    {
        if (job.Id == Guid.Empty)
            throw new ValidationException("Kubernetes build identity is invalid.");
        var jobId = job.Id.ToString("N");
        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = $"lumina-build-{jobId}-deny",
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "lumina-ci",
                    ["lumina.1t.ru/build-job-id"] = jobId
                }
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["lumina.1t.ru/build-job-id"] = jobId
                    }
                },
                Ingress = [],
                Egress = [],
                PolicyTypes = ["Ingress", "Egress"]
            }
        };
    }

    private static List<V1EnvVar> SafeEnvironment(BuildJob job) =>
    [
        new() { Name = "LUMINA_BUILD_JOB_ID", Value = job.Id.ToString("D") },
        new() { Name = "LUMINA_PIPELINE_ID", Value = job.PipelineId.ToString("D") },
        new() { Name = "TARGET_DISTRIBUTION", Value = job.TargetDistribution },
        new() { Name = "TARGET_RELEASE", Value = job.TargetRelease },
        new() { Name = "TARGET_ARCHITECTURE", Value = job.TargetArchitecture },
        new() { Name = "BUILD_PROFILE", Value = job.BuildProfile },
        new() { Name = "SPEC_NAME", Value = job.SpecName },
        new() { Name = "SPEC_PATH_IN_REPO", Value = ResolveSpecPath(job) },
        new() { Name = "RUNNER_IMAGE_DIGEST", Value = job.RunnerImageDigest },
        new() { Name = "COMMIT_SHA", Value = job.CommitSha ?? string.Empty }
    ];

    private static string ResolveSpecPath(BuildJob job)
    {
        const string marker = "specPath=";
        var source = job.SourceUrl ?? string.Empty;
        var fragmentOffset = source.IndexOf('#', StringComparison.Ordinal);
        if (!source.StartsWith("git://https://", StringComparison.Ordinal) || fragmentOffset < 0)
            throw new ValidationException("Kubernetes build source does not contain an immutable spec path.");
        var values = source[(fragmentOffset + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.StartsWith(marker, StringComparison.Ordinal))
            .Select(value => value[marker.Length..].Replace('\\', '/'))
            .ToArray();
        if (values.Length != 1)
            throw new ValidationException("Kubernetes build source spec path is missing or duplicated.");
        var path = values[0];
        if (path.Length is < 6 or > 4096 ||
            !path.EndsWith(".spec", StringComparison.Ordinal) ||
            path.StartsWith('/') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            path.Any(character => char.IsControl(character) || character is '&' or '=' or '#') ||
            !string.Equals(Path.GetFileName(path), job.SpecName, StringComparison.Ordinal))
        {
            throw new ValidationException("Kubernetes build source spec path is invalid.");
        }
        return path;
    }

    private static void Validate(
        BuildJob job,
        KubernetesRunner runner,
        KubernetesJobLimits limits)
    {
        if (job.Id == Guid.Empty || job.PipelineId == Guid.Empty)
            throw new ValidationException("Kubernetes build identity is invalid.");
        KubernetesBuildPolicy.ValidateRunner(runner, job);
        var runnerDigest = runner.Image[(runner.Image.IndexOf('@') + 1)..];
        if (!string.Equals(job.RunnerImageDigest, runnerDigest, StringComparison.Ordinal))
            throw new ValidationException("Kubernetes runner digest was not recorded on the build job.");
        if (limits.ActiveDeadlineSeconds <= 0 || limits.TtlSecondsAfterFinished <= 0)
            throw new ValidationException("Kubernetes Job time limits must be positive.");
        var specName = job.SpecName ?? string.Empty;
        if (specName.Length is < 6 or > 256 ||
            !specName.EndsWith(".spec", StringComparison.Ordinal) ||
            !char.IsLetterOrDigit(specName[0]) ||
            specName.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '+' or '-')))
        {
            throw new ValidationException("Kubernetes build spec name is invalid.");
        }
        if (!string.IsNullOrWhiteSpace(job.CommitSha) &&
            (job.CommitSha is not { Length: 40 or 64 } ||
             job.CommitSha.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ValidationException("Kubernetes build commit SHA is invalid.");
        }
    }
}
