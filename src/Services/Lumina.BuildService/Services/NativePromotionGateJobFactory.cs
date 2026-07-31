using k8s.Models;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.BuildService.Services;

public static class NativePromotionGateIdentity
{
    public static string JobName(Guid promotionSetId)
    {
        if (promotionSetId == Guid.Empty)
            throw new ValidationException("Native promotion set identity cannot be empty.");
        return $"lumina-gate-{promotionSetId:N}";
    }
}

public static class NativePromotionGateJobFactory
{
    public const string GateIdLabel = "lumina.1t.ru/promotion-set-id";

    public static V1Job Create(
        NativePromotionGate gate,
        KubernetesRunner runner,
        KubernetesJobLimits limits,
        KubernetesBuildNetworkPolicy network)
    {
        Validate(gate, runner);
        var name = NativePromotionGateIdentity.JobName(gate.Id);
        var labels = Labels(gate.Id);
        var resources = new V1ResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new(limits.CpuRequest),
                ["memory"] = new(limits.MemoryRequest),
                ["ephemeral-storage"] = new(limits.EphemeralStorageRequest)
            },
            Limits = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new(limits.CpuLimit),
                ["memory"] = new(limits.MemoryLimit),
                ["ephemeral-storage"] = new(limits.EphemeralStorageLimit)
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
                        ServiceAccountName = KubernetesJobFactory.RunnerServiceAccountName,
                        EnableServiceLinks = false,
                        HostNetwork = false,
                        HostPID = false,
                        HostIPC = false,
                        HostUsers = false,
                        RestartPolicy = "Never",
                        TerminationGracePeriodSeconds = 30,
                        NodeSelector = new Dictionary<string, string>
                        {
                            ["kubernetes.io/arch"] = runner.Architecture,
                            [KubernetesJobFactory.WorkerLabel] = "true"
                        },
                        HostAliases =
                        [
                            new V1HostAlias
                            {
                                Ip = network.EgressCidr[..network.EgressCidr.LastIndexOf('/')],
                                Hostnames = [new Uri(network.FedoraRepositoryBaseUrl).Host]
                            }
                        ],
                        Tolerations =
                        [
                            new V1Toleration
                            {
                                Effect = "NoSchedule",
                                Key = KubernetesJobFactory.WorkerTaint,
                                OperatorProperty = "Equal",
                                Value = "true"
                            }
                        ],
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = false,
                            RunAsUser = 0,
                            RunAsGroup = 0,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                        },
                        Containers =
                        [
                            new V1Container
                            {
                                Name = KubernetesJobFactory.ContainerName,
                                Image = runner.Image,
                                ImagePullPolicy = "IfNotPresent",
                                Command = ["/bin/bash"],
                                Args = [$"{NativePromotionGateTransportPolicy.MountPath}/{NativePromotionGateTransportPolicy.ScriptDataKey}"],
                                Env =
                                [
                                    new V1EnvVar { Name = "LUMINA_PROMOTION_SET_ID", Value = gate.Id.ToString("D") },
                                    new V1EnvVar { Name = "RUNNER_IMAGE_DIGEST", Value = gate.RunnerImageDigest },
                                    new V1EnvVar { Name = "FEDORA_REPOSITORY_BASE_URL", Value = network.FedoraRepositoryBaseUrl }
                                ],
                                Resources = resources,
                                SecurityContext = new V1SecurityContext
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
                                },
                                VolumeMounts =
                                [
                                    new V1VolumeMount
                                    {
                                        Name = NativePromotionGateTransportPolicy.VolumeName,
                                        MountPath = NativePromotionGateTransportPolicy.MountPath,
                                        ReadOnlyProperty = true
                                    },
                                    new V1VolumeMount
                                    {
                                        Name = "workspace",
                                        MountPath = "/workspace"
                                    }
                                ]
                            }
                        ],
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = NativePromotionGateTransportPolicy.VolumeName,
                                Secret = new V1SecretVolumeSource
                                {
                                    SecretName = NativePromotionGateTransportPolicy.SecretName(name),
                                    Optional = false,
                                    DefaultMode = 0x100
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

    public static V1NetworkPolicy CreateNetworkPolicy(
        NativePromotionGate gate,
        KubernetesBuildNetworkPolicy network)
    {
        var labels = Labels(gate.Id);
        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = $"{NativePromotionGateIdentity.JobName(gate.Id)}-deny",
                Labels = labels
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        [GateIdLabel] = gate.Id.ToString("N")
                    }
                },
                Ingress = [],
                Egress = KubernetesJobFactory.CreateNetworkPolicy(
                    new BuildJob { Id = gate.Id }, network).Spec.Egress,
                PolicyTypes = ["Ingress", "Egress"]
            }
        };
    }

    private static Dictionary<string, string> Labels(Guid id) => new(StringComparer.Ordinal)
    {
        ["app.kubernetes.io/name"] = "lumina-native-promotion-gate",
        ["app.kubernetes.io/managed-by"] = "lumina-ci",
        [GateIdLabel] = id.ToString("N")
    };

    private static void Validate(NativePromotionGate gate, KubernetesRunner runner)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(runner);
        _ = NativePromotionGateManifestPolicy.Read(gate);
        var expectedNodeArch = gate.TargetArchitecture switch
        {
            "x86_64" => "amd64",
            "aarch64" => "arm64",
            _ => throw new ValidationException("Native promotion gate target is unsupported.")
        };
        if (!string.Equals(runner.Architecture, expectedNodeArch, StringComparison.Ordinal) ||
            !runner.Image.EndsWith($"@{gate.RunnerImageDigest}", StringComparison.Ordinal))
            throw new ValidationException("Native promotion gate runner does not match candidate provenance.");
    }
}
