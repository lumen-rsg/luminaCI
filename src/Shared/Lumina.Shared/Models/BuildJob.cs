using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class BuildJob
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public BuildStatus Status { get; set; } = BuildStatus.Queued;
    public string SpecName { get; set; } = string.Empty;
    public string SpecContent { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string? ContainerId { get; set; }
    public string Logs { get; set; } = string.Empty;
    public List<BuildArtifact> Artifacts { get; set; } = [];
    public List<BuildStepRun> StepRuns { get; set; } = [];
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? DeadlineAt { get; set; }
    public string TriggeredBy { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Git integration — commit/branch info from webhook
    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public string? CommitMessage { get; set; }
    public string? CommitAuthor { get; set; }

    // Immutable build target and runner snapshot captured when the job starts.
    public string TargetDistribution { get; set; } = string.Empty;
    public string TargetRelease { get; set; } = string.Empty;
    public string TargetArchitecture { get; set; } = string.Empty;
    public string BuildProfile { get; set; } = string.Empty;
    public string? RunnerImageReference { get; set; }
    public string? RunnerImageDigest { get; set; }

    // Durable executor identity. Kubernetes names are deterministic and may be
    // persisted before the API create call; UID and pod name arrive later.
    public BuildExecutorBackend ExecutionBackend { get; set; } = BuildExecutorBackend.Docker;
    public string? KubernetesNamespace { get; set; }
    public string? KubernetesJobName { get; set; }
    public string? KubernetesJobUid { get; set; }
    public string? KubernetesPodName { get; set; }
    public string? KubernetesArtifactManifestSha256 { get; set; }
    public DateTime? KubernetesArtifactsImportedAt { get; set; }

    // Repository-project provenance. These fields are populated together for
    // builds created by a selective project dispatch and remain null for
    // manual and legacy per-pipeline builds.
    public Guid? ProjectWebhookDeliveryId { get; set; }
    public string? ProjectPackageId { get; set; }
    public string? PromotionGroup { get; set; }
    public int? ProjectStageOrder { get; set; }
    public ProjectWebhookDelivery? ProjectWebhookDelivery { get; set; }

    public Pipeline? Pipeline { get; set; }
}
