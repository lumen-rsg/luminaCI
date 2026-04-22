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
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string TriggeredBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Pipeline? Pipeline { get; set; }
}