using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public sealed class NativePromotionGate
{
    public Guid Id { get; set; }
    public Guid ProjectWebhookDeliveryId { get; set; }
    public Guid RepositoryId { get; set; }
    public string PromotionGroup { get; set; } = string.Empty;
    public string TargetArchitecture { get; set; } = string.Empty;
    public string RunnerImageDigest { get; set; } = string.Empty;
    public string CandidateManifestJson { get; set; } = string.Empty;
    public string CandidateManifestSha256 { get; set; } = string.Empty;
    public NativePromotionGateStatus Status { get; set; } = NativePromotionGateStatus.Pending;
    public string? KubernetesNamespace { get; set; }
    public string? KubernetesJobName { get; set; }
    public string? KubernetesJobUid { get; set; }
    public string? KubernetesPodName { get; set; }
    public string? ResultSha256 { get; set; }
    public string? FailureReason { get; set; }
    public string? Logs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ProjectWebhookDelivery? ProjectWebhookDelivery { get; set; }
}
