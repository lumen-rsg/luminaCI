using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class ProjectWebhookDelivery
{
    public Guid Id { get; set; }
    public Guid BuildProjectId { get; set; }
    public string ProviderDeliveryId { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public List<string> ChangedPaths { get; set; } = [];
    public string? CommitAuthor { get; set; }
    public string? CommitMessage { get; set; }
    public ProjectWebhookStatus Status { get; set; } = ProjectWebhookStatus.SnapshotPending;
    public Guid? SourceJobId { get; set; }
    public string? SnapshotStoragePath { get; set; }
    public string? SnapshotSha256 { get; set; }
    public long? SnapshotFileSize { get; set; }
    public string? DispatchPlanJson { get; set; }
    public string? ManifestSha256 { get; set; }
    public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public BuildProject BuildProject { get; set; } = null!;
    public List<BuildJob> BuildJobs { get; set; } = [];
}
